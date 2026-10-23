using Groundhog.Agent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Groundhog.Cli.Tests;

public sealed class RemoteCommandTests
{
    [Fact]
    public async Task List_describe_and_remote_search_read_the_service_contract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-remote-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CredentialStore(directory);
            store.Save(new("https://groundhog.unifinity.com", "sample-credential", DateTimeOffset.UtcNow.AddHours(1), "example", new()));
            using var handler = new ContractHandler();
            ManagementClient Factory(string relay, string? token) => new(relay, token, handler);
            var output = new StringWriter();
            Assert.Equal(0, await RemoteCommands.RunAsync(["list", "--output", "json"], output, new StringWriter(),
                TestContext.Current.CancellationToken, store, Factory));
            Assert.Equal("myapp", Assert.Single(JsonSerializer.Deserialize<TunnelInfo[]>(output.ToString())!).Name);
            Assert.DoesNotContain("sample-credential", output.ToString(), StringComparison.Ordinal);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await RemoteCommands.RunAsync(["describe", "myapp", "--output", "json"], output,
                new StringWriter(), TestContext.Current.CancellationToken, store, Factory));
            Assert.Equal("Connected", JsonSerializer.Deserialize<TunnelInfo>(output.ToString())!.State);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await RemoteCommands.RunAsync(["requests", "myapp", "--remote", "--status", "5xx", "--output", "json"],
                output, new StringWriter(), TestContext.Current.CancellationToken, store, Factory));
            Assert.Equal(502, Assert.Single(JsonSerializer.Deserialize<RequestRecord[]>(output.ToString())!).Status);
            Assert.Contains("status=5xx", handler.LastPath, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Creating_a_tunnel_saves_the_one_time_key_and_rename_moves_it_without_printing_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CredentialStore(directory);
            store.Save(new("https://groundhog.unifinity.com", "sample-credential", DateTimeOffset.UtcNow.AddHours(1), "example", new()));
            using var handler = new ContractHandler();
            ManagementClient Factory(string relay, string? token) => new(relay, token, handler);
            var output = new StringWriter();
            await RemoteCommands.RunAsync(["create", "myapp"], output, new StringWriter(), TestContext.Current.CancellationToken, store, Factory);
            Assert.Equal("one-time-key", store.Read()!.TunnelKeys["example/myapp"]);
            Assert.DoesNotContain("one-time-key", output.ToString(), StringComparison.Ordinal);
            await RemoteCommands.RunAsync(["rename", "myapp", "renamed", "--yes"], output, new StringWriter(), TestContext.Current.CancellationToken, store, Factory);
            Assert.Equal("one-time-key", store.Read()!.TunnelKeys["example/renamed"]);
            Assert.False(store.Read()!.TunnelKeys.ContainsKey("example/myapp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Remote_commands_accept_the_hidden_relay_override_only_for_the_signed_in_service()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-remote-relay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CredentialStore(directory);
            store.Save(new(Groundhog.Agent.GroundhogService.DefaultRelayAddress, "sample-credential",
                DateTimeOffset.UtcNow.AddHours(1), "example", new()));
            using var handler = new ContractHandler();
            ManagementClient Factory(string relay, string? token) => new(relay, token, handler);
            var output = new StringWriter();
            Assert.Equal(0, await RemoteCommands.RunAsync(
                ["list", "--relay", Groundhog.Agent.GroundhogService.DefaultRelayAddress, "--output", "json"], output,
                new StringWriter(), TestContext.Current.CancellationToken, store, Factory));
            Assert.Equal("myapp", Assert.Single(JsonSerializer.Deserialize<TunnelInfo[]>(output.ToString())!).Name);

            var other = await Assert.ThrowsAsync<ManagementException>(() => RemoteCommands.RunAsync(
                ["list", "--relay", "https://localhost:8443"], new StringWriter(), new StringWriter(),
                TestContext.Current.CancellationToken, store, Factory));
            Assert.Contains("another relay", other.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    private const string OldKeyId = "0123456789abcdef0123456789abcdef";
    private const string NewKeyId = "fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task Key_add_saves_the_new_key_without_confirmation_and_says_the_old_key_still_works()
    {
        var directory = SignedIn(out var store, savedKey: "old-key");
        try
        {
            var relay = new KeysHandler();
            var output = new StringWriter();
            Assert.Equal(0, await RemoteCommands.RunAsync(["key", "add", "my-tunnel"], output, new StringWriter(),
                TestContext.Current.CancellationToken, store, relay.Factory));

            Assert.Equal(["POST /api/w/example/tunnels/my-tunnel/keys"], relay.Requests);
            Assert.Equal("added-key", store.Read()!.TunnelKeys["example/my-tunnel"]);
            Assert.Contains("old key keeps working until you revoke it", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("groundhog key revoke my-tunnel <old key ID>", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("added-key", output.ToString(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Key_add_at_the_key_limit_says_to_revoke_one_first_and_keeps_the_saved_key()
    {
        var directory = SignedIn(out var store, savedKey: "old-key");
        try
        {
            var relay = new KeysHandler { AddStatus = HttpStatusCode.Conflict };
            var limit = await Assert.ThrowsAsync<ManagementException>(() => RemoteCommands.RunAsync(["key", "add", "my-tunnel"],
                new StringWriter(), new StringWriter(), TestContext.Current.CancellationToken, store, relay.Factory));

            Assert.Contains("at most 2 connection keys. Revoke one first", limit.Message, StringComparison.Ordinal);
            Assert.Equal("old-key", store.Read()!.TunnelKeys["example/my-tunnel"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Key_add_on_a_relay_without_the_keys_route_says_the_relay_does_not_support_it()
    {
        var directory = SignedIn(out var store, savedKey: "old-key");
        try
        {
            var relay = new KeysHandler { AddStatus = HttpStatusCode.NotFound, ReportKeys = false };
            var old = await Assert.ThrowsAsync<ManagementException>(() => RemoteCommands.RunAsync(["key", "add", "my-tunnel"],
                new StringWriter(), new StringWriter(), TestContext.Current.CancellationToken, store, relay.Factory));

            Assert.Contains("This relay doesn't support adding keys yet", old.Message, StringComparison.Ordinal);
            Assert.Equal(["POST /api/w/example/tunnels/my-tunnel/keys", "GET /api/w/example/tunnels/my-tunnel"], relay.Requests);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Key_list_shows_each_live_key_and_whether_an_agent_uses_it()
    {
        var directory = SignedIn(out var store, savedKey: null);
        try
        {
            var relay = new KeysHandler();
            var output = new StringWriter();
            Assert.Equal(0, await RemoteCommands.RunAsync(["key", "list", "my-tunnel"], output, new StringWriter(),
                TestContext.Current.CancellationToken, store, relay.Factory));
            Assert.Equal(
                $"""
                ID                                Created (UTC)     Last used (UTC)   Connected
                {OldKeyId}  2026-10-01 09:00  2026-10-02 09:00  Yes
                {NewKeyId}  2026-10-03 09:00  Never             No

                """, output.ToString().ReplaceLineEndings("\n"));

            output.GetStringBuilder().Clear();
            Assert.Equal(0, await RemoteCommands.RunAsync(["key", "list", "my-tunnel", "--output", "json"], output, new StringWriter(),
                TestContext.Current.CancellationToken, store, relay.Factory));
            var keys = JsonSerializer.Deserialize<ConnectionKeyInfo[]>(output.ToString())!;
            Assert.Equal([OldKeyId, NewKeyId], keys.Select(k => k.Id));
            Assert.Equal([true, false], keys.Select(k => k.Connected));
            Assert.Null(keys[1].LastUsedAt);

            relay.ReportKeys = false;
            var old = await Assert.ThrowsAsync<ManagementException>(() => RemoteCommands.RunAsync(["key", "list", "my-tunnel"],
                new StringWriter(), new StringWriter(), TestContext.Current.CancellationToken, store, relay.Factory));
            Assert.Contains("doesn't list connection keys", old.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Key_revoke_with_an_id_revokes_that_key_and_leaves_the_saved_key()
    {
        var directory = SignedIn(out var store, savedKey: "added-key");
        try
        {
            var relay = new KeysHandler();
            var output = new StringWriter();
            Assert.Equal(0, await RemoteCommands.RunAsync(["key", "revoke", "my-tunnel", OldKeyId, "--yes"], output, new StringWriter(),
                TestContext.Current.CancellationToken, store, relay.Factory));

            Assert.Equal([$"DELETE /api/w/example/tunnels/my-tunnel/keys/{OldKeyId}"], relay.Requests);
            Assert.Equal("Key 01234567 revoked. Agents that used it were disconnected.", output.ToString().Trim());
            Assert.Equal("added-key", store.Read()!.TunnelKeys["example/my-tunnel"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Key_revoke_with_an_unknown_or_malformed_id_says_what_is_wrong()
    {
        var directory = SignedIn(out var store, savedKey: null);
        try
        {
            var relay = new KeysHandler { RevokeStatus = HttpStatusCode.NotFound };
            var unknown = await Assert.ThrowsAsync<ManagementException>(() => RemoteCommands.RunAsync(
                ["key", "revoke", "my-tunnel", NewKeyId, "--yes"], new StringWriter(), new StringWriter(),
                TestContext.Current.CancellationToken, store, relay.Factory));
            Assert.Contains($"my-tunnel has no live key {NewKeyId}", unknown.Message, StringComparison.Ordinal);

            relay.ReportKeys = false;
            var old = await Assert.ThrowsAsync<ManagementException>(() => RemoteCommands.RunAsync(
                ["key", "revoke", "my-tunnel", NewKeyId, "--yes"], new StringWriter(), new StringWriter(),
                TestContext.Current.CancellationToken, store, relay.Factory));
            Assert.Contains("doesn't support revoking a single key", old.Message, StringComparison.Ordinal);

            relay.Requests.Clear();
            await Assert.ThrowsAsync<UsageException>(() => RemoteCommands.RunAsync(["key", "revoke", "my-tunnel", "01234567", "--yes"],
                new StringWriter(), new StringWriter(), TestContext.Current.CancellationToken, store, relay.Factory));
            Assert.Empty(relay.Requests);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Key_revoke_without_an_id_still_revokes_every_key_and_forgets_the_saved_key()
    {
        var directory = SignedIn(out var store, savedKey: "old-key");
        try
        {
            var relay = new KeysHandler();
            Assert.Equal(0, await RemoteCommands.RunAsync(["key", "revoke", "my-tunnel", "--yes"], new StringWriter(), new StringWriter(),
                TestContext.Current.CancellationToken, store, relay.Factory));

            Assert.Equal(["POST /api/w/example/tunnels/my-tunnel/revoke-key"], relay.Requests);
            Assert.Empty(store.Read()!.TunnelKeys);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>A signed-in credential store in workspace <c>example</c>, with <paramref name="savedKey"/> saved for <c>my-tunnel</c>.</summary>
    private static string SignedIn(out CredentialStore store, string? savedKey)
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-keys-" + Guid.NewGuid().ToString("N"));
        store = new CredentialStore(directory);
        var keys = new Dictionary<string, string>();
        if (savedKey is not null)
        {
            keys["example/my-tunnel"] = savedKey;
        }

        store.Save(new("https://groundhog.unifinity.com", "sample-credential", DateTimeOffset.UtcNow.AddHours(1), "example", keys));
        return directory;
    }

    /// <summary>
    /// Answers the connection key routes for <c>my-tunnel</c>, which has two live keys: <see cref="OldKeyId"/> with an agent
    /// connected and <see cref="NewKeyId"/>, never used. Every request is recorded as "METHOD /path".
    /// </summary>
    private sealed class KeysHandler : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        /// <summary>The status <c>POST …/keys</c> answers with.</summary>
        internal HttpStatusCode AddStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>The status <c>DELETE …/keys/{id}</c> answers with.</summary>
        internal HttpStatusCode RevokeStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>False to answer like a relay from before overlapping keys, whose tunnel JSON has no <c>keys</c>.</summary>
        internal bool ReportKeys { get; set; } = true;

        internal ManagementClient Factory(string relay, string? token) => new(relay, token, this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");
            var created = DateTimeOffset.Parse("2026-10-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            object tunnel = ReportKeys
                ? new
                {
                    name = "my-tunnel", url = "https://my-tunnel.groundhog.unifinity.com", state = "Connected", since = created,
                    requestsLastMinute = 0, requestsLastHour = 0, bytesInToday = 0, bytesOutToday = 0, actions = Array.Empty<string>(),
                    keys = new object[]
                    {
                        new { id = OldKeyId, createdAt = created, lastUsedAt = (DateTimeOffset?)created.AddDays(1), connected = true },
                        new { id = NewKeyId, createdAt = created.AddDays(2), lastUsedAt = (DateTimeOffset?)null, connected = false },
                    },
                }
                : new
                {
                    name = "my-tunnel", url = "https://my-tunnel.groundhog.unifinity.com", state = "Connected", since = created,
                    requestsLastMinute = 0, requestsLastHour = 0, bytesInToday = 0, bytesOutToday = 0, actions = Array.Empty<string>(),
                };
            var status = request.Method == HttpMethod.Post && path.EndsWith("/keys", StringComparison.Ordinal) ? AddStatus
                : request.Method == HttpMethod.Delete ? RevokeStatus
                : HttpStatusCode.OK;
            if (status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(status));
            }

            object body = request.Method == HttpMethod.Post && path.EndsWith("/keys", StringComparison.Ordinal)
                ? new { tunnel, connectionKey = "added-key" }
                : tunnel;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        // Each client borrows this test transport; clients still dispose their own HttpClient.
        protected override void Dispose(bool disposing) { }
    }

    private sealed class ContractHandler : HttpMessageHandler
    {
        internal string LastPath { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.PathAndQuery;
            object Tunnel(string name) => new
            {
                name,
                url = $"https://{name}.groundhog.unifinity.com",
                state = "Connected",
                since = DateTimeOffset.UtcNow,
                requestsLastMinute = 1,
                requestsLastHour = 10,
                bytesInToday = 100,
                bytesOutToday = 1000,
                actions = new[] { "rename" }
            };
            object body = LastPath.Contains("/requests", StringComparison.Ordinal)
                ? new { entries = new[] { new { completed = DateTimeOffset.UtcNow, method = "GET", path = "/api/orders", statusCode = 502, durationMs = 42L } }, truncated = false }
                : request.Method == HttpMethod.Post ? new { tunnel = Tunnel("myapp"), connectionKey = "one-time-key" }
                : request.Method == HttpMethod.Patch ? Tunnel("renamed")
                : LastPath.EndsWith("/tunnels", StringComparison.Ordinal) ? new[] { Tunnel("myapp") } : Tunnel("myapp");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }
        // Each client borrows this test transport; clients still dispose their own HttpClient.
        protected override void Dispose(bool disposing) { }
    }
}
