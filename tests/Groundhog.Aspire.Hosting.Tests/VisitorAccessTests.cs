using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Groundhog.Protocol;

namespace Groundhog.Aspire.Hosting.Tests;

/// <summary>Visitor access rules declared in the AppHost.</summary>
public sealed partial class GroundhogTunnelTests
{
    [Fact]
    public async Task WithGroundhogTunnelDeclaresVisitorAccessWithSecretApiKeyParameters()
    {
        var builder = CreateBuilder();
        var apiKeyValue = ApiKey.Generate();
        builder.Configuration["Parameters:groundhog-myapp-key"] = fixture.Key;
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var apiKey = builder.AddParameter("myapp-api-key", apiKeyValue, secret: true);
        var configured = false;

        var returned = web.WithGroundhogTunnel("myapp", access => access
                .Allow(VisitorAllow.Members)
                .Path("/webhooks", VisitorAllow.Anyone)
                .Path("/api", VisitorAllow.ApiKeys, VisitorAllow.Ips)
                .IpRange("203.0.113.0/24")
                .ApiKey(apiKey),
            configure: _ => configured = true);

        Assert.Same(web, returned);
        Assert.True(configured);
        var tunnel = Assert.Single(builder.Resources.OfType<GroundhogTunnelResource>());
        Assert.Equal("http", tunnel.Target?.EndpointName);
        Assert.Contains(tunnel.Annotations.OfType<ResourceRelationshipAnnotation>(), r => r.Resource == apiKey.Resource);

        var access = await tunnel.VisitorAccess!.ResolveAsync(Cancellation);
        var declaration = access.ToDeclaration();
        Assert.Equal(["members"], declaration.Default.Allow);
        Assert.Equal(["/webhooks", "/api"], declaration.Rules.Select(rule => rule.Path));
        Assert.Equal(["apikeys", "ips"], declaration.Rules[1].Allow);
        Assert.Equal(["203.0.113.0/24"], declaration.IpRanges);
        Assert.Equal([ApiKey.Hash(apiKeyValue)], declaration.ApiKeyHashes);
        Assert.DoesNotContain(apiKeyValue, tunnel.VisitorAccess.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithGroundhogTunnelWithoutRulesLeavesVisitorAccessAlone()
    {
        var builder = CreateBuilder();
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");

        web.WithGroundhogTunnel("myapp");

        Assert.Null(Assert.Single(builder.Resources.OfType<GroundhogTunnelResource>()).VisitorAccess);
    }

    public static TheoryData<string, Action<GroundhogVisitorAccessBuilder>> InvalidRules => new()
    {
        { "anyone can't be combined", access => access.Allow(VisitorAllow.Anyone, VisitorAllow.Members) },
        { "apikeys needs at least one API key", access => access.Allow(VisitorAllow.ApiKeys) },
        { "ips needs at least one IP range", access => access.Path("/office", VisitorAllow.Ips) },
        { "CIDR", access => access.IpRange("office") },
        { "start with /", access => access.Path("webhooks", VisitorAllow.Anyone) },
        { "allow needs", access => access.Allow() },
    };

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void InvalidVisitorAccessFailsWhenTheAppHostIsBuilt(string expected, Action<GroundhogVisitorAccessBuilder> rules)
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var tunnel = builder.AddGroundhogTunnel("tunnel", "myapp", key);

        var error = Assert.Throws<ArgumentException>(() => tunnel.WithVisitorAccess(rules));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Null(tunnel.Resource.VisitorAccess);
    }

    [Fact]
    public void ApiKeysMustBeSecretParameters()
    {
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var plain = builder.AddParameter("api-key", ApiKey.Generate());
        var tunnel = builder.AddGroundhogTunnel("tunnel", "myapp", key);

        var error = Assert.Throws<ArgumentException>(() => tunnel.WithVisitorAccess(access => access.ApiKey(plain)));

        Assert.Equal("apiKey", error.ParamName);
    }

    [Fact]
    public async Task TheAgentDeclaresTheRulesOnlyAsHashes()
    {
        var apiKeyValue = ApiKey.Generate();
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var apiKey = builder.AddParameter("api-key", apiKeyValue, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "aspire-access", key)
            .WithReference(web.GetEndpoint("http"))
            .WithVisitorAccess(access => access.Allow(VisitorAllow.ApiKeys).Path("/webhooks", VisitorAllow.Anyone).ApiKey(apiKey));
        Allocate(web.Resource, fixture.LocalApp);

        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);

        var running = await host.WaitForStateAsync("tunnel", KnownResourceStates.Running);
        var attempt = await fixture.Relay.Tunnel("aspire-access").Attempts.Reader.ReadAsync(Cancellation);
        Assert.True(VisitorAccessDeclaration.TryDecode(attempt.VisitorAccess, out var declaration, out var error), error);
        Assert.Equal([ApiKey.Hash(apiKeyValue)], declaration.ApiKeyHashes);
        Assert.DoesNotContain(apiKeyValue, attempt.VisitorAccess, StringComparison.Ordinal);
        Assert.Contains(running.Snapshot.Properties, p => p.Name == "groundhog.visitor-access"
            && p.Value?.ToString() == "apikeys; /webhooks: anyone; 1 API key(s)");
        Assert.DoesNotContain(running.Snapshot.Properties, p => p.Value?.ToString()?.Contains(apiKeyValue) == true);
    }

    [Fact]
    public async Task ARelayWithoutVisitorAccessFailsTheTunnel()
    {
        fixture.Relay.Tunnel("aspire-old-relay").AppliesVisitorAccess = false;
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "aspire-old-relay", key)
            .WithReference(web.GetEndpoint("http"))
            .WithVisitorAccess(access => access.Allow(VisitorAllow.Members));
        Allocate(web.Resource, fixture.LocalApp);

        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);

        var final = await host.WaitForFinalStateAsync("tunnel");
        Assert.Equal(KnownResourceStates.FailedToStart, final.Snapshot.State?.Text);
        var stopped = await host.ReadLogUntilAsync(tunnel.Resource, line => line.Contains("Stopped:", StringComparison.Ordinal));
        Assert.Contains("doesn't support visitor access rules", stopped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedApiKeyFailsToStartWithoutLoggingIt()
    {
        const string weak = "not-a-generated-api-key";
        var builder = CreateBuilder();
        var key = builder.AddParameter("groundhog-key", fixture.Key, secret: true);
        var apiKey = builder.AddParameter("api-key", weak, secret: true);
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var tunnel = builder.AddGroundhogTunnel("tunnel", fixture.Relay.Address.ToString(), "aspire-weak-api-key", key)
            .WithReference(web.GetEndpoint("http"))
            .WithVisitorAccess(access => access.Allow(VisitorAllow.ApiKeys).ApiKey(apiKey));
        Allocate(web.Resource, fixture.LocalApp);

        await using var host = new TestAppHost(builder);
        await host.InitializeAsync(tunnel.Resource);

        var final = await host.WaitForFinalStateAsync("tunnel");
        Assert.Equal(KnownResourceStates.FailedToStart, final.Snapshot.State?.Text);
        var logged = await host.ReadLogUntilAsync(tunnel.Resource, line => line.Contains("couldn't start", StringComparison.Ordinal));
        Assert.Contains("API keys must be", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(weak, logged, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Relay.Tunnel("aspire-weak-api-key").AttemptCount);
    }
}
