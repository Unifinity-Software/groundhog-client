using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Cli.Tests;

public sealed class SessionAuthenticationTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "groundhog-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(s_now);
    private readonly CredentialStore _store;

    public SessionAuthenticationTests()
    {
        _store = new(_directory);
        _store.Save(Session());
    }

    [Fact]
    public async Task Fake_time_proactive_refresh_rotates_once_and_preserves_saved_workspace_and_keys()
    {
        var relay = new RefreshHandler();
        var auth = Authentication(relay);
        Assert.True(await auth.SupportsRefreshAsync("https://groundhog.unifinity.com", TestContext.Current.CancellationToken));
        Assert.Equal("old-access", (await auth.GetSessionAsync(TestContext.Current.CancellationToken))!.Token);
        _clock.Advance(TimeSpan.FromHours(7).Add(TimeSpan.FromMinutes(56)));
        var fresh = await auth.GetSessionAsync(TestContext.Current.CancellationToken);
        Assert.Equal("next-access", fresh!.Token);
        Assert.Equal("next-refresh", fresh.RefreshToken);
        Assert.Equal("web-team", fresh.Workspace);
        Assert.Equal("saved-key", fresh.TunnelKeys["web-team/my-tunnel"]);
        Assert.Equal(_clock.GetUtcNow().AddHours(8), fresh.Expires);
        Assert.Equal(1, relay.Refreshes);
        Assert.Equal("old-refresh", relay.LastRefresh);
        Assert.DoesNotContain("refresh", fresh.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthorized_refreshes_and_retries_once_and_never_retries_a_second_unauthorized_response()
    {
        var ct = TestContext.Current.CancellationToken;
        var relay = new RefreshHandler { RejectAllManagement = true };
        var auth = Authentication(relay);
        using var client = auth.CreateClient((await auth.GetSessionAsync(ct))!);
        var failure = await Assert.ThrowsAsync<ManagementException>(() => client.SendAsync<object>(HttpMethod.Get, "/api/me", null, ct));
        Assert.Equal(HttpStatusCode.Unauthorized, failure.Status);
        Assert.Equal(1, relay.Refreshes);
        Assert.Equal(["GroundhogCli old-access", "GroundhogCli next-access"], relay.ManagementTokens);
    }

    [Fact]
    public async Task Older_relay_keeps_fixed_expiry_and_does_not_retry_or_refresh()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.Save(Session() with { RefreshSupported = false, Expires = s_now.AddMinutes(1) });
        var relay = new RefreshHandler { RejectAllManagement = true, SupportsRefresh = false };
        var auth = Authentication(relay);
        Assert.False(await auth.SupportsRefreshAsync("https://groundhog.unifinity.com", ct));
        using var client = auth.CreateClient((await auth.GetSessionAsync(ct))!);
        await Assert.ThrowsAsync<ManagementException>(() => client.SendAsync<object>(HttpMethod.Get, "/api/me", null, ct));
        Assert.Equal(0, relay.Refreshes);
        Assert.Single(relay.ManagementTokens);
        Assert.Equal(s_now.AddMinutes(1), _store.Read()!.Expires);
    }

    [Fact]
    public async Task Logout_sends_chain_token_then_removes_local_credentials_and_keys()
    {
        var relay = new RefreshHandler();
        await Authentication(relay).LogoutAsync(TestContext.Current.CancellationToken);
        Assert.Equal("old-refresh", relay.LogoutRefresh);
        Assert.Null(_store.Read());
    }

    [Fact]
    public async Task Credential_mutation_waits_for_refresh_and_reads_its_successor()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.Save(Session() with { Expires = s_now.AddMinutes(1) });
        var relay = new RefreshHandler { HoldRefresh = true };
        var refreshing = Authentication(relay).GetSessionAsync(ct);
        await relay.RefreshStarted.Task.WaitAsync(ct);
        var mutation = _store.UpdateAsync(fresh => fresh! with { Workspace = "other-team" }, ct);
        Assert.False(mutation.IsCompleted);
        relay.ReleaseRefresh.TrySetResult();
        await Task.WhenAll(refreshing, mutation);
        Assert.Equal("next-access", _store.Read()!.Token);
        Assert.Equal("next-refresh", _store.Read()!.RefreshToken);
        Assert.Equal("other-team", _store.Read()!.Workspace);
    }

    [Fact(Timeout = 30_000)]
    public async Task Two_processes_waiting_on_the_same_old_token_use_one_rotated_successor()
    {
        var ct = TestContext.Current.CancellationToken;
        if (Environment.GetEnvironmentVariable("GROUNDHOG_REFRESH_TEST_DIRECTORY") is { } childDirectory)
        {
            var store = new CredentialStore(childDirectory);
            var relay = new RefreshHandler { CounterFile = Path.Combine(childDirectory, "refresh-count.txt") };
            var authentication = new SessionAuthentication(store, (address, token) => new(address, token, relay), new FakeTimeProvider(s_now));
            var pending = authentication.GetSessionAsync(ct);
            Console.WriteLine("REFRESH_WAITING");
            Console.Out.Flush();
            Assert.Equal("next-access", (await pending)!.Token);
            return;
        }
        _store.Save(Session() with { Expires = s_now.AddMinutes(1) });
        var lockPath = Path.Combine(_directory, "credentials.lock");
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var children = new List<Process>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("-method");
                start.ArgumentList.Add(typeof(SessionAuthenticationTests).FullName + "." + nameof(Two_processes_waiting_on_the_same_old_token_use_one_rotated_successor));
                start.ArgumentList.Add("-showLiveOutput");
                start.Environment["GROUNDHOG_REFRESH_TEST_DIRECTORY"] = _directory;
                children.Add(Process.Start(start)!);
            }
            await Task.WhenAll(children.Select(async process =>
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                { if (line.Contains("REFRESH_WAITING", StringComparison.Ordinal)) { return; } }
                Assert.Fail("Refresh child exited before reaching the lock: " + await process.StandardError.ReadToEndAsync(ct));
            }));
            lease.Dispose();
            foreach (var process in children)
            {
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                var error = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                Assert.True(process.ExitCode == 0, output + error);
            }
            Assert.Single(File.ReadAllLines(Path.Combine(_directory, "refresh-count.txt")));
            Assert.Equal("next-refresh", _store.Read()!.RefreshToken);
        }
        finally
        {
            lease.Dispose();
            foreach (var process in children)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                process.Dispose();
            }
        }
    }

    private SessionAuthentication Authentication(RefreshHandler relay) => new(_store, (address, token) => new(address, token, relay), _clock);

    private static LoginSession Session() => new("https://groundhog.unifinity.com", "old-access", s_now.AddHours(8), "web-team",
        new() { ["web-team/my-tunnel"] = "saved-key" }, "old-refresh", true, "stable-login-id");

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class RefreshHandler : HttpMessageHandler
    {
        internal int Refreshes;
        internal string? LastRefresh;
        internal string? LogoutRefresh;
        internal bool RejectAllManagement;
        internal bool SupportsRefresh = true;
        internal bool HoldRefresh;
        internal string? CounterFile;
        internal List<string> ManagementTokens { get; } = [];
        internal TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/service-info": return Json(new { capabilities = SupportsRefresh ? new[] { "refreshToken" } : [] });
                case "/api/cli/refresh":
                    Refreshes++;
                    LastRefresh = (await request.Content!.ReadFromJsonAsync<JsonElement>(cancellation)).GetProperty("refreshToken").GetString();
                    RefreshStarted.TrySetResult();
                    if (HoldRefresh) { await ReleaseRefresh.Task.WaitAsync(cancellation); }
                    if (CounterFile is not null) { File.AppendAllText(CounterFile, "refresh\n"); }
                    return Json(new { accessToken = "next-access", refreshToken = "next-refresh", expiresIn = 28800 });
                case "/api/cli/logout":
                    LogoutRefresh = (await request.Content!.ReadFromJsonAsync<JsonElement>(cancellation)).GetProperty("refreshToken").GetString();
                    return new(HttpStatusCode.NoContent);
                default:
                    ManagementTokens.Add(request.Headers.Authorization!.ToString());
                    return RejectAllManagement ? new(HttpStatusCode.Unauthorized) : Json(new { });
            }
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
