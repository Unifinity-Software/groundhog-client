using System.Net.WebSockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Groundhog.Aspire.Hosting.Tests;

public sealed partial class GroundhogTunnelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestLoggingIsOptInAndOmitsSensitiveRequestData(bool enabled)
    {
        var name = enabled ? "logged-http" : "quiet-http";
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), name, key)
            .WithReference(web.GetEndpoint("http"));
        if (enabled)
        {
            Assert.Same(tunnel, tunnel.WithRequestLogging());
        }
        Allocate(web.Resource, fixture.LocalApp);

        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);
        await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        var connection = await fixture.Relay.Tunnel(name).Connections.Reader.ReadAsync(Cancellation);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri($"http://{name}.groundhog.unifinity.com/echo?token=query-secret"))
        {
            Version = new Version(2, 0),
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("body-secret"),
        };
        request.Headers.Add("Authorization", "Bearer header-secret");
        using var response = await connection.Client.SendAsync(request, Cancellation);
        Assert.True(response.IsSuccessStatusCode);
        await response.Content.ReadAsStringAsync(Cancellation);
        if (enabled)
        {
            var line = await host.ReadLogUntilAsync(tunnel.Resource,
                line => line.Contains("POST /echo", StringComparison.Ordinal));
            Assert.Matches(@"POST /echo 200 \d+ ms", line);
        }

        var logs = await host.ReadAllLogsAfterStopAsync(tunnel.Resource);
        Assert.Equal(enabled, logs.Any(line => line.Contains("POST /echo", StringComparison.Ordinal)));
        foreach (var secret in new[] { "query-secret", "body-secret", "header-secret", fixture.Key })
        {
            Assert.DoesNotContain(logs, line => line.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task VisitorPathControlsCannotCreateExtraLogLines()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "logged-controls", key)
            .WithReference(web.GetEndpoint("http"))
            .WithRequestLogging();
        Allocate(web.Resource, fixture.LocalApp);
        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);
        await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        var connection = await fixture.Relay.Tunnel("logged-controls").Connections.Reader.ReadAsync(Cancellation);
        using var response = await connection.Client.GetAsync(
            "http://logged-controls.groundhog.unifinity.com/hostile%1B%0Apath", Cancellation);
        await response.Content.ReadAsStringAsync(Cancellation);
        var line = await host.ReadLogUntilAsync(tunnel.Resource,
            line => line.Contains("GET /hostile", StringComparison.Ordinal));
        Assert.DoesNotContain(line, char.IsControl);
        Assert.Contains("path 404", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocketLoggingReportsTheCompletedConnection()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "logged-ws", key)
            .WithReference(web.GetEndpoint("http"))
            .WithRequestLogging();
        Allocate(web.Resource, fixture.LocalApp);

        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);
        await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        var connection = await fixture.Relay.Tunnel("logged-ws").Connections.Reader.ReadAsync(Cancellation);
        using var visitor = new ClientWebSocket();
        visitor.Options.HttpVersion = new Version(2, 0);
        visitor.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        await visitor.ConnectAsync(new Uri("ws://logged-ws.groundhog.unifinity.com/ws?token=socket-secret"),
            connection.Client, Cancellation);
        await visitor.SendAsync("payload"u8.ToArray(), WebSocketMessageType.Text, true, Cancellation);
        var buffer = new byte[32];
        var received = await visitor.ReceiveAsync(buffer, Cancellation);
        Assert.Equal(7, received.Count);
        await visitor.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Cancellation);

        var line = await host.ReadLogUntilAsync(tunnel.Resource,
            line => line.Contains("WS /ws", StringComparison.Ordinal));
        Assert.Matches(@"WS /ws 101 \d+ ms", line);
        Assert.DoesNotContain("socket-secret", line, StringComparison.Ordinal);
    }
}
