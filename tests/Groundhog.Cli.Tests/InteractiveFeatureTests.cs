using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Groundhog.Agent.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Cli.Tests;

public sealed class InteractiveFeatureTests
{
    [Fact]
    public void History_is_bounded_but_totals_cover_the_session_and_websocket_lifetimes_do_not_skew_latency()
    {
        var time = new FakeTimeProvider();
        var telemetry = new SessionTelemetry("sample", new Uri("http://localhost:5173"), time);
        for (var i = 0; i < 1100; i++)
        {
            telemetry.Record(new(time.GetUtcNow(), "GET", "/sample", i % 2 == 0 ? 500 : 200, 10, false, null));
            time.Advance(TimeSpan.FromSeconds(1));
        }
        telemetry.Record(new(time.GetUtcNow(), "GET", "/socket", 101, 600000, true, null));
        var snapshot = telemetry.Snapshot();
        Assert.Equal(1101, snapshot.Requests);
        Assert.Equal(550, snapshot.Errors);
        Assert.Equal(1000, snapshot.Recent.Length);
        Assert.Equal(10, snapshot.P95Ms);
        Assert.Equal("/socket", snapshot.Recent[^1].Path);
    }

    [Fact]
    public void Filters_combine_status_method_path_age_and_duration_and_return_newest_first()
    {
        var time = new FakeTimeProvider();
        var now = time.GetUtcNow();
        RequestRecord[] records = [new(now.AddMinutes(-20), "POST", "/api/orders", 502, 900, false, null),
            new(now.AddSeconds(-1), "POST", "/api/orders", 503, 600, false, null),
            new(now, "GET", "/api/orders", 500, 600, false, null),
            new(now.AddSeconds(-2), "POST", "/api/orders", 500, 800, false, null)];
        var result = UtilityCommands.Filter(records, Options.Parse(["--status", "5xx", "--method", "post", "--path", "/api/",
            "--since", "10m", "--min-duration", "500", "--limit", "1"]), now);
        Assert.Single(result);
        Assert.Equal(503, result[0].Status);
    }

    [Theory]
    [InlineData("--since", "-1m")]
    [InlineData("--since", "NaNh")]
    [InlineData("--limit", "1001")]
    [InlineData("--min-duration", "NaN")]
    [InlineData("--status", "6xx")]
    public async Task Invalid_filters_fail_without_an_agent(string option, string value)
    {
        var error = new StringWriter();
        Assert.Equal(2, await GroundhogCommand.RunAsync(["requests", "missing", option, value], _ => null,
            new StringWriter(), error, TestContext.Current.CancellationToken));
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public void Profiles_validate_targets_and_never_store_keys()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory);
            store.Save(new("sample", "http://localhost:5173"));
            Assert.Equal("sample", Assert.Single(store.Read()).Name);
            Assert.Throws<ArgumentException>(() => store.Save(new("sample", "https://secret@example.com")));
            Assert.DoesNotContain("key", File.ReadAllText(Path.Combine(directory, "profiles.json")), StringComparison.OrdinalIgnoreCase);
            Assert.True(store.Delete("sample")); Assert.Empty(store.Read());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Credentials_round_trip_and_do_not_appear_in_record_display()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-credentials-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CredentialStore(directory);
            var session = new LoginSession("https://tunnel.example.com", "sample-secret", DateTimeOffset.UtcNow.AddHours(1), "example", new());
            store.Save(session);
            Assert.Equal(session.Token, store.Read()!.Token);
            Assert.DoesNotContain(session.Token, session.ToString(), StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                Assert.DoesNotContain(session.Token, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "credentials.dat"))), StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "credentials.dat")));
            }

            store.Delete(); Assert.Null(store.Read());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Management_uses_explicit_credentials_and_does_not_echo_server_secrets()
    {
        using var handler = new RecordingHandler();
        using var client = new ManagementClient("https://tunnel.example.com", "sample-token", handler);
        var result = await client.SendAsync<AccountInfo>(HttpMethod.Get, "/api/me", null, TestContext.Current.CancellationToken);
        Assert.Equal("Example", result.DisplayName);
        Assert.Equal("GroundhogCli sample-token", handler.Authorization);
        handler.Fail = true;
        var exception = await Assert.ThrowsAsync<ManagementException>(() => client.SendAsync<object>(HttpMethod.Get,
            "/api/w/missing/tunnels", null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("server-secret", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ManagementClient("http://example.com", "sample-token"));
        Assert.Equal("/api/w/example/tunnels/sample", ManagementClient.TunnelPath("example", "sample"));
    }

    [Fact]
    public void Untrusted_markup_and_terminal_controls_are_escaped()
    {
        Assert.Equal("[[red]]name[[/]]", TerminalViews.Safe("\u001b[red]name[/]\n"));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal string? Authorization { get; private set; }
        internal bool Fail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(Fail ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("server-secret") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { displayName = "Example", email = "person@example.com", workspaces = Array.Empty<object>() }) });
        }
    }
}

public sealed class LocalSessionTests(TunnelFixture fixture) : IClassFixture<TunnelFixture>
{
    [Fact(Timeout = 30000)]
    public async Task Real_cli_background_start_status_and_stop_use_the_saved_profile()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-background-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(directory);
        store.Save(new("background-smoke", fixture.LocalApp.ToString(), fixture.Relay.Address.ToString()));
        var stopped = false;
        try
        {
            var started = await RunProcessAsync(["start", "background-smoke", "--timeout", "5"]);
            Assert.True(started.ExitCode == 0, started.Output);
            await fixture.Relay.Tunnel("background-smoke").Connections.Reader.ReadAsync(cancellation);
            var status = await RunProcessAsync(["status", "background-smoke", "--output", "json"]);
            Assert.Equal(0, status.ExitCode);
            Assert.Equal("Connected", JsonSerializer.Deserialize<SessionSnapshot>(status.Output)!.State);
            Assert.DoesNotContain(fixture.Key, status.Output, StringComparison.Ordinal);
            var stop = await RunProcessAsync(["stop", "background-smoke"]);
            Assert.Equal(0, stop.ExitCode);
            stopped = true;
        }
        finally
        {
            if (!stopped) { await RunProcessAsync(["stop", "background-smoke"]); }
            // The agent owns its registration file until it exits; retain this tiny temporary directory on a race.
            if (!Directory.Exists(Path.Combine(directory, "sessions")) || !Directory.EnumerateFiles(Path.Combine(directory, "sessions")).Any())
            { Directory.Delete(directory, true); }
        }

        async Task<(int ExitCode, string Output)> RunProcessAsync(string[] args)
        {
            var executable = Environment.GetEnvironmentVariable("GROUNDHOG_CLI_SMOKE_PATH");
            var start = new ProcessStartInfo(executable ?? "dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (executable is null) { start.ArgumentList.Add(typeof(GroundhogCommand).Assembly.Location); }
            foreach (var arg in args) { start.ArgumentList.Add(arg); }
            start.Environment["GROUNDHOG_HOME"] = directory;
            start.Environment["GROUNDHOG_KEY"] = fixture.Key;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            try
            {
                await process.WaitForExitAsync(cancellation);
                return (process.ExitCode, await stdout + await stderr);
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        }
    }
    [Fact(Timeout = 30000)]
    public async Task Another_invocation_can_query_traffic_and_stop_the_host_without_seeing_keys_or_query_strings()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var running = GroundhogCommand.RunAsync(["host", "control-smoke", "--target", fixture.LocalApp.ToString(),
            "--relay", fixture.Relay.Address.ToString(), "--pool", "1"],
            name => name == "GROUNDHOG_KEY" ? fixture.Key : null, new StringWriter(), new StringWriter(), stop.Token);
        try
        {
            var connection = await fixture.Relay.Tunnel("control-smoke").Connections.Reader.ReadAsync(cancellation);
            var registration = Assert.Single(LocalSessions.Registrations(), r => r.Name == "control-smoke");
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://control-smoke.tunnel.example.com/echo?token=secret-query")
            { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await connection.Client.SendAsync(request, cancellation);
            await response.Content.ReadAsStringAsync(cancellation);
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, await GroundhogCommand.RunAsync(["status", "control-smoke", "--output", "json"], _ => null, output, error, cancellation));
            Assert.DoesNotContain(fixture.Key, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-query", output.ToString(), StringComparison.Ordinal);
            Assert.NotNull(JsonSerializer.Deserialize<SessionSnapshot>(output.ToString()));
            Assert.NotNull(await LocalSessions.QueryAsync(registration, true, cancellation));
            Assert.Equal(0, await running.WaitAsync(cancellation));
            Assert.DoesNotContain(LocalSessions.Registrations(), r => r.Pipe == registration.Pipe);
        }
        finally { stop.Cancel(); await running.WaitAsync(cancellation); }
    }
}
