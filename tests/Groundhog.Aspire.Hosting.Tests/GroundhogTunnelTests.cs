using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Groundhog.Agent.Tests;
using Groundhog.Aspire.Hosting.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Aspire.Hosting.Tests;

/// <summary>
/// The tunnel resource in an AppHost model, run against the agent tests' fake relay. The AppHost is built but not
/// started (that needs Aspire's orchestrator); each test raises the tunnel's initialization event itself, which is
/// what starts the agent in a real AppHost. Every test uses its own tunnel name on the shared relay.
/// </summary>
public sealed partial class GroundhogTunnelTests(TunnelFixture fixture) : IClassFixture<TunnelFixture>
{
    // Generous: it only matters when something is broken, and then the test fails with a clear message.
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HostedServiceNeedsOnlyTunnelNameAndSecretKey()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("hosted-key", fixture.Key, secret: true);
        var name = builder.AddParameter("hosted-name", "quickstart");
        var constant = builder.AddGroundhogTunnel("constant", "quickstart", key);
        var parameter = builder.AddGroundhogTunnel("parameter", name, key);

        foreach (var tunnel in new[] { constant, parameter })
        {
            Assert.Equal(Groundhog.Agent.GroundhogService.DefaultRelayAddress,
                await tunnel.Resource.RelayAddressSource(Cancellation));
            Assert.Equal("quickstart", await tunnel.Resource.TunnelNameSource(Cancellation));
        }
        var plainKey = builder.AddParameter("public-key", "not-a-secret");
        Assert.Throws<ArgumentException>(() => builder.AddGroundhogTunnel("rejected", name, plainKey));
        Assert.Throws<ArgumentException>(() => builder.AddGroundhogTunnel("also-rejected", "quickstart", plainKey));
    }

    [Fact]
    public void ReconnectIsEnabledOnlyForTerminalStates()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("command-key", fixture.Key, secret: true);
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "command-state", key);
        var command = Assert.Single(tunnel.Resource.Annotations.OfType<ResourceCommandAnnotation>());
        using var services = builder.Services.BuildServiceProvider();
        foreach (var state in new[] { KnownResourceStates.NotStarted, KnownResourceStates.Starting, "Connecting", KnownResourceStates.Running, "Reconnecting", KnownResourceStates.FailedToStart, KnownResourceStates.Exited, KnownResourceStates.Finished })
        {
            Assert.Equal(KnownResourceStates.TerminalStates.Contains(state) ? ResourceCommandState.Enabled : ResourceCommandState.Disabled,
                command.UpdateState(new UpdateCommandStateContext
                {
                    ResourceSnapshot = GroundhogTunnelRunner.InitialSnapshot() with { State = state },
                    Services = services,
                }));
        }
    }

    [Fact]
    public async Task ReconnectUsesCurrentSettingsAndRejectsDuplicateCommands()
    {
        fixture.Relay.Tunnel("restart-refused").RefuseAll(401);
        var builder = CreateBuilder();
        var key = builder.AddParameter("restart-key", fixture.Key, secret: true);
        var name = builder.AddParameter("restart-name", "restart-refused");
        var relay = builder.AddParameter("restart-relay", fixture.Relay.Address.ToString());
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", relay, name, key).WithReference(web.GetEndpoint("http"));
        Allocate(web.Resource, fixture.LocalApp);
        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);
        Assert.Equal(KnownResourceStates.FailedToStart, (await host.WaitForFinalStateAsync("tunnel")).Snapshot.State?.Text);

        fixture.Relay.Tunnel("restart-refused").RefuseAll(0);
        var updatedTarget = new UriBuilder(fixture.LocalApp) { Host = "localhost" }.Uri;
        Allocate(web.Resource, updatedTarget);
        var results = await Task.WhenAll(host.ReconnectAsync(tunnel.Resource), host.ReconnectAsync(tunnel.Resource));
        Assert.Single(results, result => result.Success);
        var running = await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        Assert.Equal("https://restart-refused.groundhog.unifinity.com/", Assert.Single(running.Snapshot.Urls).Url);
        Assert.Null(running.Snapshot.StopTimeStamp);
        Assert.Equal(updatedTarget.ToString(), running.Snapshot.Properties.Single(p => p.Name == GroundhogTunnelRunner.TargetProperty).Value);
        Assert.False((await host.ReconnectAsync(tunnel.Resource)).Success);
        var connection = await fixture.Relay.Tunnel("restart-refused").Connections.Reader.ReadAsync(Cancellation);
        Assert.StartsWith("restart-refused.groundhog.unifinity.com|/echo|", await connection.Client.GetStringAsync(
            new Uri("http://restart-refused.groundhog.unifinity.com/echo"), Cancellation), StringComparison.Ordinal);
        await host.StopAsync();
        Assert.False((await host.ReconnectAsync(tunnel.Resource)).Success);
        Assert.Equal(KnownResourceStates.Finished, (await host.WaitForFinalStateAsync("tunnel")).Snapshot.State?.Text);
    }

    [Fact]
    public void TheKeyMustBeASecretParameter()
    {
        var builder = CreateBuilder();
        var plainKey = builder.AddParameter("plain-key", "not-a-secret");

        var error = Assert.Throws<ArgumentException>(
            () => builder.AddGroundhogTunnel("tunnel", "myapp", plainKey));
        Assert.Equal("key", error.ParamName);
    }

    [Fact]
    public async Task ConnectsAndGivesOtherResourcesItsPublicAddress()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "aspire-up", key)
            .WithReference(web.GetEndpoint("http"));
        var api = builder.AddExecutable("api", "api", ".")
            .WithEnvironment("PUBLIC_ORIGIN", tunnel.Resource.PublicAddress)
            .WithEnvironment("ALLOWED_HOST", tunnel.Resource.PublicHost)
            .WithEnvironment("WEBHOOK_URL", ReferenceExpression.Create($"{tunnel.Resource.PublicAddress}/webhooks"));
        Allocate(web.Resource, fixture.LocalApp);

        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);

        var running = await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        Assert.Equal("https://aspire-up.groundhog.unifinity.com/", Assert.Single(running.Snapshot.Urls).Url);
        var attempt = await fixture.Relay.Tunnel("aspire-up").Attempts.Reader.ReadAsync(Cancellation);
        Assert.Equal("Groundhog.Aspire.Hosting", attempt.ClientPackage);
        Assert.Equal(Groundhog.Agent.GroundhogClientInfo.FromAssembly("Groundhog.Aspire.Hosting",
            typeof(GroundhogTunnelRunner).Assembly).Version, attempt.ClientVersion);
        Assert.DoesNotContain(running.Snapshot.Properties, p => p.Value?.ToString()?.Contains(fixture.Key) == true);

        var environment = await ResolveEnvironmentAsync(api.Resource);
        Assert.Equal("https://aspire-up.groundhog.unifinity.com", environment["PUBLIC_ORIGIN"]);
        Assert.Equal("aspire-up.groundhog.unifinity.com", environment["ALLOWED_HOST"]);
        Assert.Equal("https://aspire-up.groundhog.unifinity.com/webhooks", environment["WEBHOOK_URL"]);

        // A request through the relay reaches the referenced endpoint, with the public Host kept.
        var connection = await fixture.Relay.Tunnel("aspire-up").Connections.Reader.ReadAsync(Cancellation);
        var echo = await connection.Client.GetStringAsync(
            new Uri("http://aspire-up.groundhog.unifinity.com/echo"), Cancellation);
        Assert.StartsWith("aspire-up.groundhog.unifinity.com|/echo|", echo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableRelayDoesNotHoldUpResourcesThatUseTheAddress()
    {
        var relay = TunnelFixture.UnusedLocalAddress();
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", relay.ToString(), "offline", key)
            .WithReference(web.GetEndpoint("http"));
        var api = builder.AddExecutable("api", "api", ".")
            .WithEnvironment("PUBLIC_ORIGIN", tunnel.Resource.PublicAddress);
        Allocate(web.Resource, fixture.LocalApp);

        // The address's own time limit never passes, so only the failed first attempt can let it resolve.
        tunnel.Resource.TimeProvider = new FakeTimeProvider();

        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);

        Assert.Empty(api.Resource.Annotations.OfType<WaitAnnotation>());
        var environment = await ResolveEnvironmentAsync(api.Resource).WaitAsync(s_timeout, Cancellation);
        Assert.Equal($"http://offline.127.0.0.1:{relay.Port}", environment["PUBLIC_ORIGIN"]);
        await host.WaitForStateAsync("tunnel", "Reconnecting");
    }

    [Fact]
    public async Task ARejectedKeyFailsWithTheReasonAndNeverLogsTheKey()
    {
        // Every attempt, not just the next: the agent opens a pool of connections, and a relay that refused only one
        // would let another connect, so the tunnel would run and then exit instead of failing to start.
        fixture.Relay.Tunnel("refused").RefuseAll(401);
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "refused", key)
            .WithReference(web.GetEndpoint("http"));
        Allocate(web.Resource, fixture.LocalApp);

        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);

        // Wait for whichever final state comes first, so a wrong outcome fails at once and says what it was.
        var final = await host.WaitForFinalStateAsync("tunnel");
        Assert.Equal(KnownResourceStates.FailedToStart, final.Snapshot.State?.Text);
        var stopped = await host.ReadLogUntilAsync(tunnel.Resource, line => line.Contains("Stopped:", StringComparison.Ordinal));
        Assert.Contains("rejected the credential", stopped, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Key, stopped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAddressFallsBackWhenTheTunnelHasNotAnsweredInTime()
    {
        var time = new FakeTimeProvider();
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var tunnel = builder.AddGroundhogTunnel("tunnel", "slowpoke", key);
        tunnel.Resource.TimeProvider = time;

        // Never started, like a tunnel whose first connection attempt hangs.
        var address = tunnel.Resource.PublicAddress.GetValueAsync(Cancellation).AsTask();
        Assert.False(address.IsCompleted);

        time.Advance(GroundhogTunnelResource.AddressWaitLimit);
        Assert.Equal("https://slowpoke.groundhog.unifinity.com", await address.WaitAsync(s_timeout, Cancellation));
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });

    /// <summary>Resolves a resource's environment variables as the AppHost does when it starts the resource.</summary>
    private static Task<Dictionary<string, string>> ResolveEnvironmentAsync(IResourceWithEnvironment resource)
    {
        // Obsolete in favour of the execution-configuration builder, but still the simplest way to resolve the values
        // without starting the orchestrator.
#pragma warning disable CS0618
        return resource.GetEnvironmentVariableValuesAsync().AsTask();
#pragma warning restore CS0618
    }

    /// <summary>Gives the resource's only endpoint a port, as the orchestrator would.</summary>
    private static void Allocate(IResourceWithEndpoints resource, Uri address)
    {
        var endpoint = resource.Annotations.OfType<EndpointAnnotation>().Single();
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, address.Host, address.Port);
    }

    /// <summary>A built, never started AppHost, with the services a tunnel's initialization event carries.</summary>
    private sealed class TestAppHost(IDistributedApplicationBuilder builder) : IAsyncDisposable
    {
        private readonly DistributedApplication _app = builder.Build();

        private ResourceNotificationService Notifications => _app.Services.GetRequiredService<ResourceNotificationService>();

        private ResourceLoggerService Logs => _app.Services.GetRequiredService<ResourceLoggerService>();

        public Task InitializeAsync(IResource resource)
        {
            var eventing = _app.Services.GetRequiredService<IDistributedApplicationEventing>();
            return eventing.PublishAsync(
                new InitializeResourceEvent(resource, eventing, Logs, Notifications, _app.Services), Cancellation);
        }

        public Task<ExecuteCommandResult> ReconnectAsync(GroundhogTunnelResource resource) =>
            resource.Annotations.OfType<ResourceCommandAnnotation>().Single(command => command.Name == "reconnect")
                .ExecuteCommand(new ExecuteCommandContext
                {
                    Services = _app.Services,
                    Logger = Logs.GetLogger(resource),
                    Arguments = new InteractionInputCollection([]),
                    ResourceName = resource.Name,
                    CancellationToken = Cancellation,
                });

        public Task StopAsync() => _app.Services.GetRequiredService<GroundhogTunnelRunner>().StopAsync(Cancellation);

        public Task<ResourceEvent> WaitForStateAsync(string resourceName, string state) =>
            Notifications.WaitForResourceAsync(resourceName, e => e.Snapshot.State?.Text == state, Cancellation)
                .WaitAsync(s_timeout, Cancellation);

        /// <summary>Waits for the resource's first final state (finished, exited or failed to start).</summary>
        public Task<ResourceEvent> WaitForFinalStateAsync(string resourceName) =>
            Notifications.WaitForResourceAsync(
                    resourceName,
                    e => KnownResourceStates.TerminalStates.Contains(e.Snapshot.State?.Text),
                    Cancellation)
                .WaitAsync(s_timeout, Cancellation);

        public async Task<string> ReadLogUntilAsync(IResource resource, Func<string, bool> match)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            timeout.CancelAfter(s_timeout);
            await foreach (var batch in Logs.WatchAsync(resource).WithCancellation(timeout.Token))
            {
                foreach (var line in batch)
                {
                    if (match(line.Content))
                    {
                        return line.Content;
                    }
                }
            }

            throw new InvalidOperationException("The log ended without a matching line.");
        }

        public async Task<string[]> ReadAllLogsAfterStopAsync(IResource resource)
        {
            await _app.Services.GetRequiredService<GroundhogTunnelRunner>().StopAsync(CancellationToken.None);
            Logs.Complete(resource);
            var lines = new List<string>();
            await foreach (var batch in Logs.WatchAsync(resource).WithCancellation(Cancellation))
            {
                lines.AddRange(batch.Select(line => line.Content));
            }

            return [.. lines];
        }

        public async ValueTask DisposeAsync()
        {
            await _app.Services.GetRequiredService<GroundhogTunnelRunner>().StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
