using System.Net;
using System.Net.Http.Json;
using Groundhog.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Cli.Tests;

public sealed class KeyRotationTests : IDisposable
{
    private const string OldId = "ffffffffffffffffffffffffffffffff";
    private const string NewId = "00000000000000000000000000000000";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "groundhog-rotation-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly CredentialStore _store;
    private readonly Relay _relay = new();
    private readonly Host _host;
    private readonly LoginSession _session;

    public KeyRotationTests()
    {
        _store = new(_directory);
        _session = new("https://groundhog.unifinity.com", "access-token", _clock.GetUtcNow().AddHours(8), "example",
            new() { ["example/my-tunnel"] = _relay.OldKey }, SessionId: "rotation-session");
        _store.Save(_session);
        _host = new(_relay, _store);
    }

    [Fact]
    public async Task Handover_verifies_new_process_and_connected_key_before_revoking_exact_old_id()
    {
        using var client = Client();
        await Service(client).RunAsync(_session, "example", "my-tunnel", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal([OldId], _relay.Revoked);
        Assert.Equal(_relay.NewKey, _store.Read()!.TunnelKeys["example/my-tunnel"]);
        Assert.Equal(_relay.NewKey, _host.CurrentKey);
        Assert.Equal(1, _host.Replacements);
        Assert.Equal(["add", "replace-new", "revoke-old"], _relay.Events);
    }

    [Fact]
    public async Task Cli_flag_uses_the_same_safe_rotation_and_never_prints_the_new_key()
    {
        var output = new StringWriter();
        var code = await RemoteCommands.RunAsync(["key", "rotate", "my-tunnel", "--no-downtime", "--yes"], output,
            new StringWriter(), TestContext.Current.CancellationToken, _store,
            (relay, token) => new(relay, token, _relay), rotationHost: _host);
        Assert.Equal(0, code);
        Assert.Contains("old key was revoked", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_relay.NewKey, output.ToString(), StringComparison.Ordinal);
        Assert.Equal([OldId], _relay.Revoked);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("aspire")]
    [InlineData("ambiguous")]
    [InlineData("two-keys")]
    [InlineData("no-key-ids")]
    [InlineData("saved-key-mismatch")]
    public async Task Unsafe_hosts_or_unidentifiable_old_keys_are_refused_before_creating_a_key(string condition)
    {
        _host.Condition = condition;
        _relay.TwoKeys = condition == "two-keys";
        _relay.OmitIds = condition == "no-key-ids";
        using var client = Client();
        await Assert.ThrowsAsync<ManagementException>(() => Service(client).RunAsync(_session, "example", "my-tunnel",
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(0, _relay.Added);
        Assert.Empty(_relay.Revoked);
        Assert.Equal(_relay.OldKey, _store.Read()!.TunnelKeys["example/my-tunnel"]);
    }

    [Fact]
    public async Task Fake_timeout_revokes_new_key_and_restores_saved_key_without_disrupting_original_host()
    {
        var ct = TestContext.Current.CancellationToken;
        _host.BlockReplacement = true;
        using var client = Client();
        var rotating = Service(client).RunAsync(_session, "example", "my-tunnel", TimeSpan.FromSeconds(30), ct);
        await _host.ReplacementStarted.Task.WaitAsync(ct);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var error = await Assert.ThrowsAsync<ManagementException>(() => rotating);
        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        Assert.Equal([NewId], _relay.Revoked);
        Assert.Equal(_relay.OldKey, _store.Read()!.TunnelKeys["example/my-tunnel"]);
        Assert.Equal(_relay.OldKey, _host.CurrentKey);
    }

    [Fact]
    public async Task Cancellation_after_handover_restarts_old_key_with_independent_cleanup_token_before_revoking_new()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _host.AfterFirstReplacement = stop.Cancel;
        using var client = Client();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(client).RunAsync(_session, "example", "my-tunnel", TimeSpan.FromSeconds(30), stop.Token));
        Assert.Equal([NewId], _relay.Revoked);
        Assert.Equal(_relay.OldKey, _store.Read()!.TunnelKeys["example/my-tunnel"]);
        Assert.Equal(_relay.OldKey, _host.CurrentKey);
        Assert.Equal(["add", "replace-new", "replace-old", "revoke-new"], _relay.Events);
    }

    [Fact]
    public async Task Lost_revoke_response_does_not_roll_back_or_revoke_both_keys_when_commit_is_proven()
    {
        _relay.LoseOldRevokeResponse = true;
        using var client = Client();
        await Service(client).RunAsync(_session, "example", "my-tunnel", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal([OldId], _relay.Revoked);
        Assert.Equal(_relay.NewKey, _store.Read()!.TunnelKeys["example/my-tunnel"]);
        Assert.Equal(_relay.NewKey, _host.CurrentKey);
    }

    [Fact]
    public async Task Concurrent_saved_key_change_is_preserved_and_only_the_identified_added_key_is_revoked()
    {
        var outsideKey = ConnectionKey.Generate();
        _relay.AfterAdd = () => _store.Update(fresh => { fresh!.TunnelKeys["example/my-tunnel"] = outsideKey; return fresh; });
        using var client = Client();
        await Assert.ThrowsAsync<ManagementException>(() => Service(client).RunAsync(_session, "example", "my-tunnel", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal([NewId], _relay.Revoked);
        Assert.Equal(outsideKey, _store.Read()!.TunnelKeys["example/my-tunnel"]);
        Assert.Equal(0, _host.Replacements);
    }

    private ManagementClient Client() => new(_session.Relay, _session.Token, _relay);
    private KeyRotation Service(ManagementClient client) => new(_directory, client, _host, _clock);
    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class Host(Relay relay, CredentialStore store) : IRotationHost
    {
        internal string? Condition;
        internal bool BlockReplacement;
        internal int Replacements;
        internal string CurrentKey = relay.OldKey;
        internal Action? AfterFirstReplacement;
        internal TaskCompletionSource ReplacementStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _process = 4242;

        private RotationAgent Agent(string source = "Cli") => new(new("my-tunnel", "groundhog-" + _process.ToString("x32"), _process, source),
            FakeServices.Session("my-tunnel") with { Host = source, ProcessId = _process });

        public Task<RotationAgent[]> AgentsAsync(string name, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult<RotationAgent[]>(Condition switch
            { "remote" => [], "ambiguous" => [Agent(), Agent()], "aspire" => [Agent("Aspire")], _ => [Agent()] });
        }

        public Task<bool> MatchesKeyAsync(RotationAgent agent, string key, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(Condition != "saved-key-mismatch" && key == CurrentKey && agent.Registration.ProcessId == _process);
        }

        public async Task<RotationAgent> ReplaceAsync(TunnelProfile profile, CancellationToken cancellation)
        {
            Replacements++;
            ReplacementStarted.TrySetResult();
            if (BlockReplacement) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); }
            cancellation.ThrowIfCancellationRequested();
            CurrentKey = store.Read()!.TunnelKeys["example/my-tunnel"];
            relay.Events.Add(CurrentKey == relay.NewKey ? "replace-new" : "replace-old");
            relay.NewConnected = CurrentKey == relay.NewKey;
            _process++;
            if (Replacements == 1) { AfterFirstReplacement?.Invoke(); }
            return Agent();
        }
    }

    private sealed class Relay : HttpMessageHandler
    {
        internal readonly string OldKey = ConnectionKey.Generate();
        internal readonly string NewKey = ConnectionKey.Generate();
        internal int Added;
        internal bool TwoKeys;
        internal bool OmitIds;
        internal bool NewConnected;
        internal bool LoseOldRevokeResponse;
        internal Action? AfterAdd;
        internal List<string> Revoked { get; } = [];
        internal List<string> Events { get; } = [];

        private TunnelInfo Tunnel() => new("my-tunnel", "https://my-tunnel.groundhog.unifinity.com", "connected", DateTimeOffset.UnixEpoch,
            0, 0, 0, 0, ["rename", "delete"], OmitIds ? null :
            // The added key is deliberately first, not last. IDs are random identifiers, never derived from a key.
            (Added > 0 || TwoKeys ? new[] { new ConnectionKeyInfo(NewId, DateTimeOffset.UnixEpoch, null, NewConnected) } : [])
                .Concat([new(OldId, DateTimeOffset.UnixEpoch, null, !NewConnected)]).Where(key => !Revoked.Contains(key.Id)).ToArray());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/keys", StringComparison.Ordinal))
            { Added++; Events.Add("add"); AfterAdd?.Invoke(); return Task.FromResult(Json(new TunnelCreated(Tunnel(), NewKey))); }
            if (request.Method == HttpMethod.Delete)
            {
                var id = request.RequestUri!.Segments[^1];
                Revoked.Add(id); Events.Add(id == OldId ? "revoke-old" : "revoke-new");
                if (id == OldId && LoseOldRevokeResponse) { throw new HttpRequestException("Lost response after commit."); }
            }
            return Task.FromResult(Json(Tunnel()));
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
