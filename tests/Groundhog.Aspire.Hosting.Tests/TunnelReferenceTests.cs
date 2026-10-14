using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Aspire.Hosting.Tests;

/// <summary>
/// <c>WithReference(target, tunnel)</c> on a downstream resource, which points its service discovery for the target at
/// the tunnel's public address.
/// </summary>
public sealed partial class GroundhogTunnelTests
{
    [Fact]
    public async Task WithReferenceGivesServiceDiscoveryTheTunnelsHttpsAddress()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var gateway = builder.AddExecutable("api-gateway", "gateway", ".").WithHttpsEndpoint(name: "https");
        var tunnel = builder.AddGroundhogTunnel("api-gateway-tunnel", "gateway-ref", key)
            .WithReference(gateway.GetEndpoint("https"));
        var worker = builder.AddExecutable("worker", "worker", ".");
        var time = new FakeTimeProvider();
        tunnel.Resource.TimeProvider = time;

        var returned = worker.WithReference(gateway, tunnel);

        Assert.Same(worker, returned);
        Assert.Contains(worker.Resource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            r => r.Resource == tunnel.Resource && r.Type == "Reference");

        // Resolved when the resource starts, not when the AppHost is built: it waits for the tunnel's first attempt.
        var resolving = ResolveEnvironmentAsync(worker.Resource);
        Assert.False(resolving.IsCompleted);
        time.Advance(GroundhogTunnelResource.AddressWaitLimit);
        var environment = await resolving.WaitAsync(s_timeout, Cancellation);
        Assert.Equal("https://gateway-ref.groundhog.unifinity.com", environment["services__api-gateway__https__0"]);
        Assert.DoesNotContain(environment.Keys, name => name.StartsWith("services__api-gateway__http__", StringComparison.Ordinal));
        Assert.Empty(worker.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public async Task WithReferenceUsesTheAddressTheRelayAnnounces()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "ref-announced", key)
            .WithReference(web.GetEndpoint("http"));
        var worker = builder.AddExecutable("worker", "worker", ".").WithReference(web, tunnel);
        Allocate(web.Resource, fixture.LocalApp);

        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);
        await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);

        var environment = await ResolveEnvironmentAsync(worker.Resource);
        Assert.Equal("https://ref-announced.groundhog.unifinity.com", environment["services__web__https__0"]);
    }

    [Fact]
    public void GetGroundhogTunnelReturnsTheTunnelWithGroundhogTunnelAdded()
    {
        var builder = CreateBuilder();
        var gateway = builder.AddExecutable("api-gateway", "gateway", ".").WithHttpsEndpoint(name: "https")
            .WithGroundhogTunnel("gateway");
        var worker = builder.AddExecutable("worker", "worker", ".");

        var tunnel = gateway.GetGroundhogTunnel();
        worker.WithReference(gateway, tunnel);

        Assert.Same(Assert.Single(builder.Resources.OfType<GroundhogTunnelResource>()), tunnel.Resource);
        Assert.Contains(worker.Resource.Annotations.OfType<ResourceRelationshipAnnotation>(), r => r.Resource == tunnel.Resource);
    }

    [Fact]
    public void GetGroundhogTunnelNeedsExactlyOneTunnel()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");

        var none = Assert.Throws<InvalidOperationException>(() => web.GetGroundhogTunnel());
        Assert.Contains("'web'", none.Message, StringComparison.Ordinal);

        builder.AddGroundhogTunnel("one", "one", key).WithReference(web.GetEndpoint("http"));
        builder.AddGroundhogTunnel("two", "two", key).WithReference(web.GetEndpoint("http"));
        var many = Assert.Throws<InvalidOperationException>(() => web.GetGroundhogTunnel());
        Assert.Contains("one, two", many.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithReferenceRefusesATunnelToAnotherResource()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var api = builder.AddExecutable("api", "api", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", "myapp", key).WithReference(web.GetEndpoint("http"));
        var worker = builder.AddExecutable("worker", "worker", ".");

        var error = Assert.Throws<ArgumentException>(() => worker.WithReference(api, tunnel));

        Assert.Equal("tunnel", error.ParamName);
        Assert.Contains("'web'", error.Message, StringComparison.Ordinal);
        Assert.Empty(worker.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
    }

    [Fact]
    public async Task WithReferenceSetsNothingInPublishMode()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { Args = ["--publisher", "manifest", "--output-path", "manifest.json"], DisableDashboard = true });
        Assert.True(builder.ExecutionContext.IsPublishMode);
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", "myapp", key).WithReference(web.GetEndpoint("http"));
        var worker = builder.AddExecutable("worker", "worker", ".").WithReference(web, tunnel);

        var environment = await ResolveEnvironmentAsync(worker.Resource);
        Assert.DoesNotContain("services__web__https__0", environment.Keys);
    }

    [Fact]
    public void WithReferenceChecksItsArguments()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", "myapp", key);
        var worker = builder.AddExecutable("worker", "worker", ".");

        Assert.Throws<ArgumentNullException>(() => worker.WithReference(null!, tunnel));
        Assert.Throws<ArgumentNullException>(() => worker.WithReference(web, (IResourceBuilder<GroundhogTunnelResource>)null!));
        Assert.Throws<ArgumentNullException>(() => ((IResourceBuilder<ExecutableResource>)null!).WithReference(web, tunnel));
        Assert.Throws<ArgumentNullException>(() => ((IResourceBuilder<ExecutableResource>)null!).GetGroundhogTunnel());
    }
}
