using System.Diagnostics;
using System.Net;
using Groundhog.Agent.Tests;
using Groundhog.Protocol;

namespace Groundhog.Cli.Tests;

public sealed class HostSmokeTests(TunnelFixture fixture) : IClassFixture<TunnelFixture>
{
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
        Task<int> running;
        if (string.IsNullOrEmpty(executable))
        {
            running = GroundhogCommand.RunAsync(arguments,
                name => name == "GROUNDHOG_KEY" ? fixture.Key : null, output, error, stop.Token);
        }
        else
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["GROUNDHOG_KEY"] = fixture.Key;
            process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
            running = CaptureProcessAsync(process, output, error, cancellation);
        }

        try
        {
            var tunnel = fixture.Relay.Tunnel("cli-smoke");
            var connection = await tunnel.Connections.Reader.ReadAsync(cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://cli-smoke.tunnel.example.com/echo?code=sample")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.Add("X-Forwarded-Host", "cli-smoke.tunnel.example.com");
            using var response = await connection.Client.SendAsync(request, cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("cli-smoke.tunnel.example.com|/echo?code=sample|cli-smoke.tunnel.example.com",
                await response.Content.ReadAsStringAsync(cancellation));

            await connection.CloseAsync(GroundhogCloseCodes.SessionReplaced, cancellation);
            Assert.Equal(ExitCodes.Stopped, await running.WaitAsync(cancellation));
            Assert.Contains("https://cli-smoke.tunnel.example.com", output.ToString(), StringComparison.Ordinal);
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

            process?.Dispose();
        }
    }

    private static async Task<int> CaptureProcessAsync(Process process, StringWriter output, StringWriter error,
        CancellationToken cancellation)
    {
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        output.Write(await stdout);
        error.Write(await stderr);
        return process.ExitCode;
    }
}
