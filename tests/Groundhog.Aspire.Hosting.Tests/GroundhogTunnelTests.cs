using System.Text.Json;
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
    public void LifecycleCommandsFollowTheTunnelState()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("command-key", fixture.Key, secret: true);
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "command-state", key);
        var commands = tunnel.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToDictionary(c => c.Name);
        Assert.Equal(
            [KnownResourceCommands.StartCommand, KnownResourceCommands.StopCommand, KnownResourceCommands.RestartCommand,
                GroundhogTunnelBuilderExtensions.TestPublicAddressCommand, GroundhogTunnelBuilderExtensions.LogRequestsCommand, GroundhogTunnelBuilderExtensions.StopLoggingRequestsCommand],
            commands.Keys);
        using var services = builder.Services.BuildServiceProvider();

        const ResourceCommandState on = ResourceCommandState.Enabled, off = ResourceCommandState.Disabled, hidden = ResourceCommandState.Hidden;
        (string State, ResourceCommandState Start, ResourceCommandState Stop, ResourceCommandState Restart)[] expected =
        [
            (KnownResourceStates.NotStarted, on, hidden, off),
            (KnownResourceStates.Starting, hidden, on, on),
            ("Connecting", hidden, on, on),
            (KnownResourceStates.Running, hidden, on, on),
            ("Reconnecting", hidden, on, on),
            (KnownResourceStates.Stopping, off, off, off),
            (KnownResourceStates.Finished, on, hidden, off),
            (KnownResourceStates.Exited, on, hidden, off),
            (KnownResourceStates.FailedToStart, on, hidden, off),
        ];
        foreach (var row in expected)
        {
            var context = new UpdateCommandStateContext
            {
                ResourceSnapshot = GroundhogTunnelRunner.InitialSnapshot() with { State = row.State },
                Services = services,
            };
            Assert.Equal(
                (row.State, row.Start, row.Stop, row.Restart),
                (row.State,
                    commands[KnownResourceCommands.StartCommand].UpdateState(context),
                    commands[KnownResourceCommands.StopCommand].UpdateState(context),
                    commands[KnownResourceCommands.RestartCommand].UpdateState(context)));
        }
    }

    [Fact]
    public async Task StartUsesCurrentSettingsAndRejectsDuplicateCommands()
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
        var results = await Task.WhenAll(
            host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.StartCommand),
            host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.StartCommand));
        Assert.Single(results, result => result.Success);
        var running = await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        Assert.Equal("https://restart-refused.groundhog.unifinity.com/", Assert.Single(running.Snapshot.Urls).Url);
        Assert.Null(running.Snapshot.StopTimeStamp);
        Assert.Equal(updatedTarget.ToString(), running.Snapshot.Properties.Single(p => p.Name == GroundhogTunnelRunner.TargetProperty).Value);
        Assert.False((await host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.StartCommand)).Success);
        var connection = await fixture.Relay.Tunnel("restart-refused").Connections.Reader.ReadAsync(Cancellation);
        Assert.StartsWith("restart-refused.groundhog.unifinity.com|/echo|", await connection.Client.GetStringAsync(
            new Uri("http://restart-refused.groundhog.unifinity.com/echo"), Cancellation), StringComparison.Ordinal);
        await host.StopAsync();
        Assert.False((await host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.StartCommand)).Success);
        Assert.Equal(KnownResourceStates.Finished, (await host.WaitForFinalStateAsync("tunnel")).Snapshot.State?.Text);
    }

    [Fact]
    public async Task StopAndRestartChangeOneTunnelWithoutStoppingTheAppHost()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("lifecycle-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "lifecycle", key)
            .WithReference(web.GetEndpoint("http"));
        var otherTunnel = builder.AddGroundhogTunnel("other-tunnel", fixture.Relay.Address.ToString(), "lifecycle-other", key)
            .WithReference(web.GetEndpoint("http"));
        Allocate(web.Resource, fixture.LocalApp);
        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);
        await host.InitializeAsync(otherTunnel.Resource);
        await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        await host.WaitForStateAsync("other-tunnel", KnownResourceStates.Running);

        var stopStates = host.RecordStates("tunnel", until: KnownResourceStates.Finished);
        Assert.True((await host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.StopCommand)).Success);
        Assert.Equal([KnownResourceStates.Stopping, KnownResourceStates.Finished], await stopStates);
        Assert.False((await host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.StopCommand)).Success);
        Assert.Equal(KnownResourceStates.Running, host.CurrentState("other-tunnel"));

        var startStates = host.RecordStates("tunnel", until: KnownResourceStates.Running);
        Assert.True((await host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.StartCommand)).Success);
        Assert.Equal([KnownResourceStates.Starting, "Connecting", KnownResourceStates.Running], await startStates);

        var restartStates = host.RecordStates("tunnel", until: KnownResourceStates.Running);
        Assert.True((await host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.RestartCommand)).Success);
        Assert.Equal(
            [KnownResourceStates.Stopping, KnownResourceStates.Finished, KnownResourceStates.Starting, "Connecting", KnownResourceStates.Running],
            await restartStates);
        Assert.Equal(KnownResourceStates.Running, host.CurrentState("other-tunnel"));
    }

    [Fact]
    public async Task AnExplicitStartTunnelWaitsForTheStartCommand()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("explicit-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "explicit-start", key)
            .WithReference(web.GetEndpoint("http"))
            .WithExplicitStart();
        Allocate(web.Resource, fixture.LocalApp);
        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);

        // Initialization started no agent, so there's nothing to stop and no state has been published.
        Assert.False((await host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.StopCommand)).Success);
        Assert.Null(host.CurrentState("tunnel"));

        Assert.True((await host.ExecuteAsync(tunnel.Resource, KnownResourceCommands.StartCommand)).Success);
        var running = await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        Assert.Equal("https://explicit-start.groundhog.unifinity.com/", Assert.Single(running.Snapshot.Urls).Url);
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
    public async Task PublishingLeavesOutTheTunnelAndEnvironmentVariablesThatUseItsAddress()
    {
        var output = Directory.CreateTempSubdirectory("groundhog-manifest-");
        try
        {
            var manifestPath = Path.Combine(output.FullName, "manifest.json");
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = ["--publisher", "manifest", "--output-path", manifestPath],
                DisableDashboard = true,
            });
            var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http")
                .WithEnvironment("Greeting", "hello");
            web.WithGroundhogTunnel(configure: webTunnel => web
                .WithEnvironment("PublicOrigin", webTunnel.Resource.PublicAddress)
                .WithEnvironment("AllowedHost", webTunnel.Resource.PublicHost)
                .WithEnvironment("Webhooks__BaseUrl", ReferenceExpression.Create($"{webTunnel.Resource.PublicAddress}/webhooks")));

            await using (var app = builder.Build())
            {
                await app.RunAsync(Cancellation).WaitAsync(s_timeout, Cancellation);
            }

            // Without the tunnel in the manifest, the 3 variables would be expressions such as
            // {web-tunnel.outputs.publicAddress} that refer to a resource the deployment doesn't have.
            var manifestJson = await File.ReadAllTextAsync(manifestPath, Cancellation);
            using var manifest = JsonDocument.Parse(manifestJson);
            var resources = manifest.RootElement.GetProperty("resources");
            Assert.Equal(["web"], resources.EnumerateObject().Select(resource => resource.Name));
            var environment = resources.GetProperty("web").GetProperty("env");
            Assert.Equal(["Greeting"], environment.EnumerateObject().Select(variable => variable.Name));
            Assert.DoesNotContain("groundhog-tunnel-", manifestJson, StringComparison.Ordinal);
            Assert.DoesNotContain("web-tunnel", manifestJson, StringComparison.Ordinal);
        }
        finally
        {
            output.Delete(recursive: true);
        }
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

        public Task<ExecuteCommandResult> ExecuteAsync(GroundhogTunnelResource resource, string commandName) =>
            resource.Annotations.OfType<ResourceCommandAnnotation>().Single(command => command.Name == commandName)
                .ExecuteCommand(new ExecuteCommandContext
                {
                    Services = _app.Services,
                    Logger = Logs.GetLogger(resource),
                    Arguments = new InteractionInputCollection([]),
                    ResourceName = resource.Name,
                    CancellationToken = Cancellation,
                });

        public Task<ResourceEvent> WaitForPropertyAsync(string resourceName, string property, string value) =>
            Notifications.WaitForResourceAsync(
                    resourceName,
                    e => e.Snapshot.Properties.Any(p => p.Name == property && Equals(p.Value, value)),
                    Cancellation)
                .WaitAsync(s_timeout, Cancellation);

        public string? CurrentState(string resourceName) =>
            Notifications.TryGetCurrentState(resourceName, out var e) ? e.Snapshot.State?.Text : null;

        /// <summary>
        /// Records each state change of the resource, from now until it reaches <paramref name="until"/>. Repeated
        /// snapshots with the same state, such as a property update, count once.
        /// </summary>
        public Task<string[]> RecordStates(string resourceName, string until)
        {
            var current = CurrentState(resourceName);
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            timeout.CancelAfter(s_timeout);
            var events = Notifications.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

            // The watch subscribes when it's first advanced; do that before returning so no change is missed.
            var next = events.MoveNextAsync();
            return RecordAsync();

            async Task<string[]> RecordAsync()
            {
                var states = new List<string>();
                try
                {
                    while (await next)
                    {
                        // The watch replays every resource's current state first; those aren't changes.
                        var state = events.Current.Snapshot.State?.Text;
                        if (events.Current.Resource.Name == resourceName && state is not null && state != (states.LastOrDefault() ?? current))
                        {
                            states.Add(state);
                            if (state == until)
                            {
                                return [.. states];
                            }
                        }
                        next = events.MoveNextAsync();
                    }
                }
                finally
                {
                    await events.DisposeAsync();
                    timeout.Dispose();
                }

                throw new InvalidOperationException("The watch ended before the state was reached.");
            }
        }

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
