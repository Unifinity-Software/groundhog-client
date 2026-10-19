using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Groundhog.Agent.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Aspire.Hosting.Tests;

public sealed partial class GroundhogTunnelTests
{
    [Fact]
    public async Task TestPublicAddressReportsTheStatusAVisitorGets()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "address-test", key)
            .WithReference(web.GetEndpoint("http"));

        // After the tunnel, which registers the client with its default handler.
        var relayed = RouteAddressTestsThroughTheFakeRelay(builder);
        Allocate(web.Resource, fixture.LocalApp);
        await using var host = new TestAppHost(builder);

        // Not connected yet: nothing to test.
        Assert.False((await host.ExecuteAsync(tunnel.Resource, GroundhogTunnelBuilderExtensions.TestPublicAddressCommand)).Success);

        await host.InitializeAsync(tunnel.Resource);
        await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        relayed.SetResult(await fixture.Relay.Tunnel("address-test").Connections.Reader.ReadAsync(Cancellation));

        // The local app answers 404 for /; any answer below 500 means the whole path works.
        var result = await host.ExecuteAsync(tunnel.Resource, GroundhogTunnelBuilderExtensions.TestPublicAddressCommand);
        Assert.True(result.Success, result.Message);
        Assert.Matches(@"^GET https://address-test\.groundhog\.unifinity\.com/ returned 404 Not Found in \d+ ms\.$", result.Message);
        await host.ReadLogUntilAsync(tunnel.Resource,
            line => line.Contains("Test public address: GET https://address-test.groundhog.unifinity.com/ returned 404", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestPublicAddressFailsWhenTheAppIsUnreachable()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "address-test-down", key)
            .WithReference(web.GetEndpoint("http"));

        // After the tunnel, which registers the client with its default handler.
        var relayed = RouteAddressTestsThroughTheFakeRelay(builder);
        Allocate(web.Resource, TunnelFixture.UnusedLocalAddress());
        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);
        await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        relayed.SetResult(await fixture.Relay.Tunnel("address-test-down").Connections.Reader.ReadAsync(Cancellation));

        var result = await host.ExecuteAsync(tunnel.Resource, GroundhogTunnelBuilderExtensions.TestPublicAddressCommand);
        Assert.False(result.Success);
        Assert.Matches(@"^GET https://address-test-down\.groundhog\.unifinity\.com/ returned 5\d\d ", result.Message);
    }

    [Fact]
    public async Task TestPublicAddressGivesUpAfterTheTimeout()
    {
        var time = new FakeTimeProvider();
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "address-test-slow", key)
            .WithReference(web.GetEndpoint("http"));
        builder.Services.AddHttpClient(GroundhogTunnelBuilderExtensions.TestPublicAddressHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new NeverAnswers());
        tunnel.Resource.TimeProvider = time;
        Allocate(web.Resource, fixture.LocalApp);
        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);
        await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);

        var test = host.ExecuteAsync(tunnel.Resource, GroundhogTunnelBuilderExtensions.TestPublicAddressCommand);
        await NeverAnswers.Waiting.Task.WaitAsync(s_timeout, Cancellation);
        Assert.False(test.IsCompleted);
        time.Advance(Internal.GroundhogTunnelRunner.AddressTestTimeout);
        var result = await test.WaitAsync(s_timeout, Cancellation);
        Assert.False(result.Success);
        Assert.Equal("GET https://address-test-slow.groundhog.unifinity.com/ got no response within 10 s.", result.Message);
    }

    [Fact]
    public void TestPublicAddressIsEnabledOnlyWhileRunning()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "address-test-state", key);
        var command = tunnel.Resource.Annotations.OfType<ResourceCommandAnnotation>()
            .Single(c => c.Name == GroundhogTunnelBuilderExtensions.TestPublicAddressCommand);
        using var services = builder.Services.BuildServiceProvider();
        foreach (var state in new[] { KnownResourceStates.NotStarted, KnownResourceStates.Starting, "Connecting", KnownResourceStates.Running, "Reconnecting", KnownResourceStates.Stopping, KnownResourceStates.Finished })
        {
            Assert.Equal(
                state == KnownResourceStates.Running ? ResourceCommandState.Enabled : ResourceCommandState.Disabled,
                command.UpdateState(new UpdateCommandStateContext
                {
                    ResourceSnapshot = Internal.GroundhogTunnelRunner.InitialSnapshot() with { State = state },
                    Services = services,
                }));
        }
    }

    /// <summary>
    /// Sends the command's requests through the fake relay's side of the tunnel, as the real relay would: the
    /// public https address becomes h2c on the agent's WebSocket, with the public host kept.
    /// </summary>
    private static TaskCompletionSource<AgentConnection> RouteAddressTestsThroughTheFakeRelay(
        IDistributedApplicationBuilder builder)
    {
        var connection = new TaskCompletionSource<AgentConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddHttpClient(GroundhogTunnelBuilderExtensions.TestPublicAddressHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new ThroughRelay(connection.Task));
        return connection;
    }

    private sealed class ThroughRelay(Task<AgentConnection> connection) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var relayed = new HttpRequestMessage(request.Method, new UriBuilder(request.RequestUri!) { Scheme = "http", Port = -1 }.Uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            return await (await connection).Client.SendAsync(relayed, cancellationToken);
        }
    }

    private sealed class NeverAnswers : HttpMessageHandler
    {
        public static TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
