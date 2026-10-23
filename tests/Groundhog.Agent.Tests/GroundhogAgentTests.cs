using System.Net;
using System.Net.WebSockets;
using System.Text;
using Groundhog.Agent.Internal;
using Groundhog.Protocol;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Agent.Tests;

public class GroundhogAgentTests(TunnelFixture fixture) : IClassFixture<TunnelFixture>
{
    [Fact(Timeout = 30_000)]
    public async Task Hosting_package_reports_its_own_version_instead_of_the_agent_version()
    {
        await using var agent = fixture.StartAgent("client-metadata", client: new GroundhogClientInfo("Groundhog.Aspire.Hosting", "1.2.3-preview.4"));
        var attempt = await fixture.Relay.Tunnel("client-metadata").Attempts.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("Groundhog.Aspire.Hosting", "1.2.3-preview.4", GroundhogAgent.Version),
            (attempt.ClientPackage, attempt.ClientVersion, attempt.Agent));
    }
    [Fact(Timeout = 30_000)]
    public async Task Connects_with_the_protocol_headers_and_forwards_with_the_visitors_host()
    {
        var ct = TestContext.Current.CancellationToken;
        var tunnel = fixture.Relay.Tunnel("host-check");
        await using var agent = fixture.StartAgent("host-check");

        var attempt = await tunnel.Attempts.Reader.ReadAsync(ct);
        Assert.Equal($"{AgentAuthorization.KeyScheme} {fixture.Key}", attempt.Authorization);
        Assert.True(Guid.TryParse(attempt.Session, out _));
        Assert.Equal(GroundhogAgent.Version, attempt.Agent);
        Assert.Equal(("Groundhog.Agent", GroundhogAgent.Version), (attempt.ClientPackage, attempt.ClientVersion));
        Assert.Equal([GroundhogProtocol.SubProtocol], attempt.SubProtocols);

        var connected = await agent.NextStatusAsync();
        Assert.Equal(GroundhogAgentState.Connected, connected.State);
        Assert.Equal(new Uri("https://host-check.groundhog.unifinity.com"), connected.PublicAddress);

        var relay = await tunnel.Connections.Reader.ReadAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://host-check.groundhog.unifinity.com/echo?code=abc")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Add("X-Forwarded-Host", "host-check.groundhog.unifinity.com");
        request.Headers.Add(GroundhogHeaders.RequestId, "0123456789abcdef0123456789abcdef");
        using var response = await relay.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "host-check.groundhog.unifinity.com|/echo?code=abc|host-check.groundhog.unifinity.com",
            await response.Content.ReadAsStringAsync(ct));

        var record = await agent.NextRequestAsync();
        Assert.Equal(("GET", "/echo", 200, false), (record.Method, record.Path, record.StatusCode, record.IsWebSocket));
        Assert.Equal("0123456789abcdef0123456789abcdef", record.RequestId);
    }

    [Fact(Timeout = 30_000)]
    public async Task Streams_the_response_as_the_local_app_writes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = fixture.ResetStreamGate();
        var tunnel = fixture.Relay.Tunnel("streaming");
        await using var agent = fixture.StartAgent("streaming");
        var relay = await tunnel.Connections.Reader.ReadAsync(ct);

        using var response = await relay.Client.GetAsync(
            "http://streaming.groundhog.unifinity.com/stream", HttpCompletionOption.ResponseHeadersRead, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));

        // The first half arrives while the local app is still holding the response open.
        Assert.Equal("first", await reader.ReadLineAsync(ct));
        gate.SetResult();
        Assert.Equal("second", await reader.ReadLineAsync(ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task Passes_a_websocket_through_to_the_local_app()
    {
        var ct = TestContext.Current.CancellationToken;
        var tunnel = fixture.Relay.Tunnel("sockets");
        await using var agent = fixture.StartAgent("sockets");
        var relay = await tunnel.Connections.Reader.ReadAsync(ct);

        // The relay sends visitor WebSockets as HTTP/2 extended CONNECT; the local app only speaks HTTP/1.1.
        using var visitor = new ClientWebSocket();
        visitor.Options.HttpVersion = HttpVersion.Version20;
        visitor.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        await visitor.ConnectAsync(new Uri("ws://sockets.groundhog.unifinity.com/ws"), relay.Client, ct);

        await visitor.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: true, ct);
        var buffer = new byte[64];
        var received = await visitor.ReceiveAsync(buffer, ct);
        Assert.Equal("hello", Encoding.UTF8.GetString(buffer, 0, received.Count));

        await visitor.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        var record = await agent.NextRequestAsync();
        Assert.Equal(("GET", "/ws", 101, true), (record.Method, record.Path, record.StatusCode, record.IsWebSocket));
    }

    [Fact(Timeout = 30_000)]
    public async Task Answers_502_local_unreachable_when_nothing_listens_locally()
    {
        var ct = TestContext.Current.CancellationToken;
        var tunnel = fixture.Relay.Tunnel("nobody-home");
        await using var agent = fixture.StartAgent("nobody-home", localTarget: TunnelFixture.UnusedLocalAddress());
        var relay = await tunnel.Connections.Reader.ReadAsync(ct);

        using var response = await relay.Client.GetAsync("http://nobody-home.groundhog.unifinity.com/", ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(GroundhogErrors.LocalUnreachable, Assert.Single(response.Headers.GetValues(GroundhogHeaders.Error)));
        Assert.Equal(GroundhogErrors.LocalUnreachable, (await agent.NextRequestAsync()).Error);
    }

    [Fact(Timeout = 30_000)]
    public async Task Reconnects_on_the_backoff_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var tunnel = fixture.Relay.Tunnel("backoff");
        tunnel.Refuse(503, 503, 503, 503, 503, 503);
        await using var agent = fixture.StartAgent("backoff", timeProvider: time);

        // 1, 2, 4, 8, 16, then 30 seconds, each varied by up to 20% but never past 30.
        double[] expectedSeconds = [1, 2, 4, 8, 16, 30];
        for (var i = 0; i < expectedSeconds.Length; i++)
        {
            var retry = await agent.NextStatusAsync();
            Assert.Equal(GroundhogAgentState.Reconnecting, retry.State);
            Assert.Equal(i + 1, tunnel.AttemptCount);
            AssertDelay(expectedSeconds[i], retry.RetryDelay);
            time.Advance(retry.RetryDelay!.Value);
        }

        Assert.Equal(GroundhogAgentState.Connected, (await agent.NextStatusAsync()).State);
        var relay = await tunnel.Connections.Reader.ReadAsync(ct);

        // A connection that stayed up starts the schedule again from 1 second.
        time.Advance(ReconnectBackoff.MaxDelay);
        await relay.CloseAsync(GroundhogCloseCodes.Normal, ct);
        var afterDrop = await agent.NextStatusAsync();
        Assert.Equal(GroundhogAgentState.Reconnecting, afterDrop.State);
        AssertDelay(1, afterDrop.RetryDelay);

        time.Advance(afterDrop.RetryDelay!.Value);
        Assert.Equal(GroundhogAgentState.Connected, (await agent.NextStatusAsync()).State);
        Assert.Equal(8, tunnel.AttemptCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Reconnects_when_the_relay_drops_without_closing()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var tunnel = fixture.Relay.Tunnel("relay-died");
        await using var agent = fixture.StartAgent("relay-died", timeProvider: time);
        Assert.Equal(GroundhogAgentState.Connected, (await agent.NextStatusAsync()).State);
        var relay = await tunnel.Connections.Reader.ReadAsync(ct);
        using (await relay.Client.GetAsync("http://relay-died.groundhog.unifinity.com/echo", ct))
        {
        }

        // The agent's HTTP/2 server still has something to write (its GOAWAY) when it finds the socket gone.
        relay.Abort();

        var dropped = await agent.NextStatusAsync();
        Assert.Equal(GroundhogAgentState.Reconnecting, dropped.State);
        time.Advance(dropped.RetryDelay!.Value);
        Assert.Equal(GroundhogAgentState.Connected, (await agent.NextStatusAsync()).State);
    }

    [Fact]
    public void Uses_the_hosted_service_unless_a_relay_address_is_set()
    {
        var options = new GroundhogAgentOptions
        {
            TunnelName = "myapp",
            Credential = new TunnelKeyCredential(fixture.Key),
            LocalTarget = new Uri("http://localhost:5173"),
        };

        Assert.Equal(new Uri(GroundhogService.DefaultRelayAddress), options.RelayAddress);
        options.Validate();
        _ = new GroundhogAgent(options); // Nothing connects until RunAsync.
        Assert.Equal(
            new Uri($"wss://groundhog.unifinity.com{GroundhogProtocol.ConnectPath}?{GroundhogProtocol.TunnelQueryParameter}=myapp"),
            RelayConnector.BuildConnectUri(options.RelayAddress!, "myapp"));

        // An explicit override is still checked like any other setting.
        options.RelayAddress = fixture.Relay.Address;
        options.Validate();
        foreach (var invalid in new Uri?[] { null, new("ftp://groundhog.unifinity.com"), new("/relay", UriKind.Relative) })
        {
            options.RelayAddress = invalid;
            var error = Assert.Throws<ArgumentException>(options.Validate);
            Assert.Equal(nameof(GroundhogAgentOptions.RelayAddress), error.ParamName);
        }
    }

    [Fact]
    public void Waits_indefinitely_for_the_relay_to_start_http2_on_an_idle_connection()
    {
        // The relay sends its first request (and the HTTP/2 preface) whenever it needs to, possibly much later.
        // Kestrel's idle check runs on the real clock, so the setting is checked rather than waited out:
        // Timeout.InfiniteTimeSpan looks right but makes Kestrel drop such a connection within about a second.
        var kestrel = new KestrelServerOptions();
        LocalServer.ConfigureKestrel(kestrel, new TunnelEndPoint());

        Assert.NotEqual(Timeout.InfiniteTimeSpan, kestrel.Limits.KeepAliveTimeout);
        Assert.True(kestrel.Limits.KeepAliveTimeout >= TimeSpan.FromDays(1));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(ConnectRefusal.Unauthorized)]
    [InlineData(GroundhogCloseCodes.CredentialRevoked)]
    public async Task Stops_for_good_when_the_relay_rejects_the_credential(int refusal)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"rejected-{refusal}";
        var tunnel = fixture.Relay.Tunnel(name);
        if (refusal == ConnectRefusal.Unauthorized)
        {
            tunnel.Refuse(refusal);
        }

        await using var agent = fixture.StartAgent(name);
        if (refusal == GroundhogCloseCodes.CredentialRevoked)
        {
            var relay = await tunnel.Connections.Reader.ReadAsync(ct);
            await relay.CloseAsync(refusal, ct);
        }

        Assert.Equal(GroundhogAgentStopReason.CredentialRejected, await agent.Run.WaitAsync(ct));
        Assert.Equal(1, tunnel.AttemptCount);
    }

    private static void AssertDelay(double expectedSeconds, TimeSpan? actual)
    {
        Assert.NotNull(actual);
        var seconds = actual.Value.TotalSeconds;
        Assert.InRange(seconds, expectedSeconds * (1 - ReconnectBackoff.JitterFraction), Math.Min(expectedSeconds * (1 + ReconnectBackoff.JitterFraction), 30));
    }
}
