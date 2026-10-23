using System.Diagnostics;
using System.Net;
using Groundhog.Agent.Tests;
using Groundhog.Protocol;

namespace Groundhog.Cli.Tests;

public sealed class HostSmokeTests(TunnelFixture fixture) : IClassFixture<TunnelFixture>
{
    [Fact(Timeout = 30_000)]
    public async Task Replacement_background_entry_uses_the_saved_key_instead_of_an_inherited_old_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-replacement-" + Guid.NewGuid().ToString("N"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<(int ExitCode, string Output)>? running = null;
        var replacementKey = ConnectionKey.Generate();
        new CredentialStore(directory).Save(new(fixture.Relay.Address.ToString(), "unused-management-token", DateTimeOffset.UtcNow.AddHours(1),
            "example", new() { ["example/replacement-smoke"] = replacementKey }));
        try
        {
            running = CliProcess.RunAsync(["__host-background-stored", "replacement-smoke", "--target", fixture.LocalApp.ToString(),
                "--relay", fixture.Relay.Address.ToString(), "--pool", "1"], directory, fixture.Key, stop.Token);
            var tunnel = fixture.Relay.Tunnel("replacement-smoke");
            var connection = await tunnel.Connections.Reader.ReadAsync(ct);
            var attempt = await tunnel.Attempts.Reader.ReadAsync(ct);
            Assert.Equal("GroundhogKey " + replacementKey, attempt.Authorization);
            await connection.CloseAsync(GroundhogCloseCodes.SessionReplaced, ct);
            var result = await running.WaitAsync(ct);
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(replacementKey, result.Output, StringComparison.Ordinal);
        }
        finally
        {
            await stop.CancelAsync();
            if (running is not null)
            {
                try { await running; }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Host_readiness_reports_an_exited_CLI_without_waiting_for_a_connection()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AwaitReadinessAsync(ready.Task,
            Task.FromResult(ExitCodes.BadArguments), TestContext.Current.CancellationToken));
        Assert.Contains("exited with code 2 before connecting", error.Message, StringComparison.Ordinal);
        Assert.False(ready.Task.IsCompleted);
    }

    [Fact(Timeout = 30_000)]
    public async Task Host_forwards_a_request_and_stops_when_replaced()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var output = new StringWriter();
        var error = new StringWriter();
        var executable = Environment.GetEnvironmentVariable("GROUNDHOG_CLI_SMOKE_PATH");
        string[] arguments = ["host", "cli-smoke", "--target", fixture.LocalApp.ToString(),
            "--relay", fixture.Relay.Address.ToString(), "--pool", "1"];

        Process? process = null;
        string? home = null;
        var phase = "starting CLI";
        var tunnel = fixture.Relay.Tunnel("cli-smoke");
        Task<int> running;
        if (string.IsNullOrEmpty(executable))
        {
            running = GroundhogCommand.RunAsync(arguments,
                name => name == "GROUNDHOG_KEY" ? fixture.Key : null, output, error, stop.Token);
        }
        else
        {
            home = Path.Combine(Path.GetTempPath(), "groundhog-host-smoke-" + Guid.NewGuid().ToString("N"));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["GROUNDHOG_KEY"] = fixture.Key;
            start.Environment["GROUNDHOG_HOME"] = home;
            process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
            running = CaptureProcessAsync(process, output, error, cancellation);
        }

        try
        {
            phase = "waiting for relay connection";
            var connection = await AwaitReadinessAsync(tunnel.Connections.Reader.ReadAsync(cancellation).AsTask(), running, cancellation);
            phase = "reading connect metadata";
            var attempt = await tunnel.Attempts.Reader.ReadAsync(cancellation);
            Assert.Equal("Groundhog.Cli", attempt.ClientPackage);
            Assert.False(string.IsNullOrEmpty(attempt.ClientVersion));
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://cli-smoke.groundhog.unifinity.com/echo?code=sample")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.Add("X-Forwarded-Host", "cli-smoke.groundhog.unifinity.com");
            phase = "forwarding HTTP request";
            using var response = await connection.Client.SendAsync(request, cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("cli-smoke.groundhog.unifinity.com|/echo?code=sample|cli-smoke.groundhog.unifinity.com",
                await response.Content.ReadAsStringAsync(cancellation));

            phase = "sending SessionReplaced";
            await connection.CloseAsync(GroundhogCloseCodes.SessionReplaced, cancellation);
            phase = "waiting for CLI exit";
            Assert.Equal(ExitCodes.Stopped, await running.WaitAsync(cancellation));
            Assert.Contains("https://cli-smoke.groundhog.unifinity.com", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Key, output.ToString() + error, StringComparison.Ordinal);
        }
        finally
        {
            stop.Cancel();
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            try { await running; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                // The child has a private home; all captured traffic belongs to this synthetic fixture. Keep the
                // original raw-output secret assertions above, and redact before emitting bounded diagnostics.
                static string Safe(string text, string key) => new string(text.Replace(key, "[redacted]", StringComparison.Ordinal)
                    .Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').Take(8192).ToArray());
                TestContext.Current.TestOutputHelper?.WriteLine($"Host smoke phase: {phase}; attempts: {tunnel.AttemptCount}; "
                    + $"process exit: {(process is null ? "in-process" : process.HasExited ? process.ExitCode : "running")}. "
                    + $"stdout: {Safe(output.ToString(), fixture.Key)} stderr: {Safe(error.ToString(), fixture.Key)}");
                process?.Dispose();
                if (home is not null && Directory.Exists(home))
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await CliProcess.DeleteExitedDirectoryAsync(home, cleanup.Token);
                }
            }
        }
    }

    private static async Task<T> AwaitReadinessAsync<T>(Task<T> readiness, Task<int> running, CancellationToken cancellation)
    {
        var completed = await Task.WhenAny(readiness, running).WaitAsync(cancellation);
        if (completed == readiness || readiness.IsCompletedSuccessfully) { return await readiness; }
        var exit = await running;
        throw new InvalidOperationException($"CLI exited with code {exit} before connecting to the fake relay.");
    }

    private static async Task<int> CaptureProcessAsync(Process process, StringWriter output, StringWriter error,
        CancellationToken cancellation)
    {
        static async Task CopyAsync(StreamReader reader, StringWriter destination)
        {
            while (await reader.ReadLineAsync() is { } line) { destination.WriteLine(line); }
        }
        var stdout = CopyAsync(process.StandardOutput, output);
        var stderr = CopyAsync(process.StandardError, error);
        try { await process.WaitForExitAsync(cancellation); return process.ExitCode; }
        finally { await Task.WhenAll(stdout, stderr); }
    }
}
