using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Groundhog.Agent;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Aspire.Hosting.Tests;

/// <summary>The one-line <c>WithGroundhogTunnel</c> helper on a resource with endpoints.</summary>
public sealed partial class GroundhogTunnelTests
{
    [Fact]
    public async Task WithGroundhogTunnelAddsANamedTunnelToTheHostedServiceWithASecretKeyParameter()
    {
        var builder = CreateBuilder();
        builder.Configuration["Parameters:groundhog-myapp-key"] = fixture.Key;
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var calls = new List<IResourceBuilder<GroundhogTunnelResource>>();

        var returned = web.WithGroundhogTunnel("myapp", configure: calls.Add);

        Assert.Same(web, returned);
        var tunnel = Assert.Single(builder.Resources.OfType<GroundhogTunnelResource>());
        Assert.Equal("web-tunnel", tunnel.Name);
        Assert.Same(tunnel, Assert.Single(calls).Resource);
        Assert.Equal(GroundhogService.DefaultRelayAddress, await tunnel.RelayAddressSource(Cancellation));
        Assert.Equal("myapp", await tunnel.TunnelNameSource(Cancellation));
        Assert.Same(web.Resource, tunnel.Target?.Resource);
        Assert.Equal("http", tunnel.Target?.EndpointName);

        var key = Assert.Single(builder.Resources.OfType<ParameterResource>());
        Assert.Equal("groundhog-myapp-key", key.Name);
        Assert.True(key.Secret);
        Assert.Contains(ManifestPublishingCallbackAnnotation.Ignore, key.Annotations);
        Assert.Contains(tunnel.Annotations.OfType<ResourceRelationshipAnnotation>(), r => r.Resource == key);

        // The key comes from Parameters:groundhog-myapp-key, as user secrets or the dashboard's prompt supply it.
        var credential = Assert.IsType<TunnelKeyCredential>(await tunnel.CredentialSource(Cancellation));
        var token = await credential.GetTokenAsync(
            new GroundhogTokenRequestContext(new Uri(GroundhogService.DefaultRelayAddress), "myapp", false), Cancellation);
        Assert.Equal(fixture.Key, token.Token);
    }

    [Theory]
    [InlineData(null, "https")]
    [InlineData(null, "https", "http")]
    [InlineData(null, "http")]
    [InlineData(null, "http", "admin")]
    [InlineData("admin", "admin", "https", "http")]
    [InlineData("http", "http", "https")]
    public void WithGroundhogTunnelPrefersHttpsThenHttpUnlessAnEndpointIsNamed(
        string? endpointName, string expected, params string[] others)
    {
        var builder = CreateBuilder();
        var web = builder.AddExecutable("web", "web", ".");
        foreach (var name in new[] { expected }.Concat(others).Distinct())
        {
            web = name == "https" ? web.WithHttpsEndpoint(name: name) : web.WithHttpEndpoint(name: name);
        }

        web.WithGroundhogTunnel("myapp", endpointName);

        var tunnel = Assert.Single(builder.Resources.OfType<GroundhogTunnelResource>());
        Assert.Equal(expected, tunnel.Target?.EndpointName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(null, "admin")]
    [InlineData("missing", "https", "http")]
    public void WithGroundhogTunnelNeedsAnEndpointToForwardTo(string? endpointName, params string[] endpoints)
    {
        var builder = CreateBuilder();
        var web = builder.AddExecutable("web", "web", ".");
        foreach (var name in endpoints)
        {
            web = web.WithHttpEndpoint(name: name);
        }

        var error = Assert.Throws<ArgumentException>(() => web.WithGroundhogTunnel("myapp", endpointName));

        Assert.Equal(endpointName is null ? "resource" : "endpointName", error.ParamName);
        Assert.Contains("'web'", error.Message, StringComparison.Ordinal);
        // Nothing is added when the endpoint is missing.
        Assert.Empty(builder.Resources.OfType<GroundhogTunnelResource>());
        Assert.Empty(builder.Resources.OfType<ParameterResource>());
    }

    [Fact]
    public void WithGroundhogTunnelReusesAnExistingKeyParameter()
    {
        var builder = CreateBuilder();
        var declared = builder.AddParameter("groundhog-shared-key", secret: true).WithDescription("Declared first.");
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var api = builder.AddExecutable("api", "api", ".").WithHttpsEndpoint(name: "https");

        web.WithGroundhogTunnel("shared");
        api.WithGroundhogTunnel("shared");

        Assert.Same(declared.Resource, Assert.Single(builder.Resources.OfType<ParameterResource>()));
        Assert.Equal("Declared first.", declared.Resource.Description);
        Assert.Equal(new[] { "api-tunnel", "web-tunnel" },
            builder.Resources.OfType<GroundhogTunnelResource>().Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void WithGroundhogTunnelRefusesAnExistingKeyParameterThatIsNotSecret()
    {
        var builder = CreateBuilder();
        builder.AddParameter("groundhog-plain-key", "not-a-secret");
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");

        var error = Assert.Throws<ArgumentException>(() => web.WithGroundhogTunnel("plain"));

        Assert.Equal("key", error.ParamName);
        Assert.Empty(builder.Resources.OfType<GroundhogTunnelResource>());
    }

    [Fact]
    public void WithGroundhogTunnelChecksItsArguments()
    {
        var builder = CreateBuilder();
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");

        Assert.Equal("tunnelName", Assert.Throws<ArgumentNullException>(() => web.WithGroundhogTunnel((string)null!)).ParamName);
        Assert.Equal("resource", Assert.Throws<ArgumentNullException>(
            () => ((IResourceBuilder<ExecutableResource>)null!).WithGroundhogTunnel("myapp")).ParamName);
        Assert.Empty(builder.Resources.OfType<GroundhogTunnelResource>());
    }

    [Fact]
    public void TunnelsShowTheAnimalIconInTheDashboardUnlessTheAppHostChoosesAnother()
    {
        var builder = CreateBuilder();
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");

        web.WithGroundhogTunnel("my-tunnel");

        var tunnel = web.GetGroundhogTunnel();
        Assert.Equal("AnimalPawPrint", Assert.Single(tunnel.Resource.Annotations.OfType<ResourceIconAnnotation>()).IconName);

        tunnel.WithIconName("PlugConnected");

        Assert.Equal("PlugConnected", Assert.Single(tunnel.Resource.Annotations.OfType<ResourceIconAnnotation>()).IconName);
    }

    [Fact]
    public async Task WithGroundhogTunnelWithoutATunnelNameReadsTheNameAndKeyFromParameters()
    {
        var builder = CreateBuilder();
        builder.Configuration["Parameters:groundhog-tunnel-name"] = "my-tunnel-dev1";
        builder.Configuration["Parameters:groundhog-tunnel-key"] = fixture.Key;
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var calls = new List<IResourceBuilder<GroundhogTunnelResource>>();

        var returned = web.WithGroundhogTunnel(configure: calls.Add);

        Assert.Same(web, returned);
        var tunnel = Assert.Single(builder.Resources.OfType<GroundhogTunnelResource>());
        Assert.Equal("web-tunnel", tunnel.Name);
        Assert.Same(tunnel, Assert.Single(calls).Resource);
        Assert.Equal(GroundhogService.DefaultRelayAddress, await tunnel.RelayAddressSource(Cancellation));
        Assert.Equal("my-tunnel-dev1", await tunnel.TunnelNameSource(Cancellation));
        Assert.Same(web.Resource, tunnel.Target?.Resource);
        Assert.Equal("http", tunnel.Target?.EndpointName);
        Assert.Equal("AnimalPawPrint", Assert.Single(tunnel.Annotations.OfType<ResourceIconAnnotation>()).IconName);

        var parameters = builder.Resources.OfType<ParameterResource>().ToDictionary(parameter => parameter.Name);
        Assert.Equal(["groundhog-tunnel-key", "groundhog-tunnel-name"], parameters.Keys.Order(StringComparer.Ordinal));
        var name = parameters["groundhog-tunnel-name"];
        var key = parameters["groundhog-tunnel-key"];
        Assert.False(name.Secret);
        Assert.True(key.Secret);
        Assert.Equal(
            "The name of your Groundhog tunnel, for example my-tunnel-dev1. Each developer uses their own.", name.Description);
        Assert.Contains("groundhog-tunnel-name", key.Description, StringComparison.Ordinal);
        foreach (var parameter in new[] { name, key })
        {
            Assert.Contains(ManifestPublishingCallbackAnnotation.Ignore, parameter.Annotations);
            Assert.Contains(tunnel.Annotations.OfType<ResourceRelationshipAnnotation>(), r => r.Resource == parameter);
        }

        // The key comes from Parameters:groundhog-tunnel-key, as the CLI's --project option or the dashboard's prompt
        // supply it.
        var credential = Assert.IsType<TunnelKeyCredential>(await tunnel.CredentialSource(Cancellation));
        var token = await credential.GetTokenAsync(
            new GroundhogTokenRequestContext(new Uri(GroundhogService.DefaultRelayAddress), "my-tunnel-dev1", false),
            Cancellation);
        Assert.Equal(fixture.Key, token.Token);

        // Without a connection, PublicAddress falls back to the address the tunnel normally has.
        web.WithEnvironment("PublicOrigin", tunnel.PublicAddress);
        var time = new FakeTimeProvider();
        tunnel.TimeProvider = time;
        var resolving = ResolveEnvironmentAsync(web.Resource);
        time.Advance(GroundhogTunnelResource.AddressWaitLimit);
        var environment = await resolving.WaitAsync(s_timeout, Cancellation);
        Assert.Equal("https://my-tunnel-dev1.groundhog.unifinity.com", environment["PublicOrigin"]);
    }

    [Fact]
    public async Task WithGroundhogTunnelBindsAStringToTheTunnelNameAndNamedArgumentsToTheParameterForm()
    {
        var builder = CreateBuilder();
        builder.Configuration["Parameters:groundhog-tunnel-name"] = "my-tunnel-dev1";
        var named = builder.AddExecutable("named", "named", ".").WithHttpsEndpoint(name: "https").WithHttpEndpoint(name: "http");
        var bare = builder.AddExecutable("bare", "bare", ".").WithHttpsEndpoint(name: "https").WithHttpEndpoint(name: "http");
        var endpoint = builder.AddExecutable("endpoint", "endpoint", ".").WithHttpsEndpoint(name: "https").WithHttpEndpoint(name: "http");
        var configured = builder.AddExecutable("configured", "configured", ".").WithHttpEndpoint(name: "http");
        var restricted = builder.AddExecutable("restricted", "restricted", ".").WithHttpEndpoint(name: "http");

        named.WithGroundhogTunnel("my-tunnel");
        bare.WithGroundhogTunnel();
        endpoint.WithGroundhogTunnel(endpointName: "http");
        configured.WithGroundhogTunnel(tunnel => tunnel.WithRequestLogging());
        restricted.WithGroundhogTunnel(access => access.Allow(VisitorAllow.Members));

        var tunnels = builder.Resources.OfType<GroundhogTunnelResource>().ToDictionary(tunnel => tunnel.Name);
        Assert.Equal("my-tunnel", await tunnels["named-tunnel"].TunnelNameSource(Cancellation));
        Assert.Equal("https", tunnels["named-tunnel"].Target?.EndpointName);
        foreach (var name in new[] { "bare-tunnel", "endpoint-tunnel", "configured-tunnel", "restricted-tunnel" })
        {
            Assert.Equal("my-tunnel-dev1", await tunnels[name].TunnelNameSource(Cancellation));
        }

        Assert.Equal("https", tunnels["bare-tunnel"].Target?.EndpointName);
        Assert.Equal("http", tunnels["endpoint-tunnel"].Target?.EndpointName);
        Assert.True(tunnels["configured-tunnel"].RequestLoggingEnabled);
        Assert.NotNull(tunnels["restricted-tunnel"].VisitorAccess);
        Assert.Null(tunnels["bare-tunnel"].VisitorAccess);

        // The named form adds only its own key parameter; the others share groundhog-tunnel-name and -key.
        Assert.Equal(["groundhog-my-tunnel-key", "groundhog-tunnel-key", "groundhog-tunnel-name"],
            builder.Resources.OfType<ParameterResource>().Select(parameter => parameter.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WithGroundhogTunnelWithoutATunnelNameReusesDeclaredParameters()
    {
        var builder = CreateBuilder();
        var tunnelName = builder.AddParameter("groundhog-tunnel-name").WithDescription("Declared name.");
        var tunnelKey = builder.AddParameter("groundhog-tunnel-key", secret: true).WithDescription("Declared key.");
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");
        var api = builder.AddExecutable("api", "api", ".").WithHttpsEndpoint(name: "https");

        web.WithGroundhogTunnel();
        api.WithGroundhogTunnel();

        Assert.Equal([tunnelKey.Resource, tunnelName.Resource],
            builder.Resources.OfType<ParameterResource>().OrderBy(parameter => parameter.Name, StringComparer.Ordinal));
        Assert.Equal("Declared name.", tunnelName.Resource.Description);
        Assert.Equal("Declared key.", tunnelKey.Resource.Description);
        Assert.Equal(["api-tunnel", "web-tunnel"],
            builder.Resources.OfType<GroundhogTunnelResource>().Select(tunnel => tunnel.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WithGroundhogTunnelWithoutATunnelNameRefusesADeclaredKeyParameterThatIsNotSecret()
    {
        var builder = CreateBuilder();
        builder.AddParameter("groundhog-tunnel-key", "not-a-secret");
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");

        var error = Assert.Throws<ArgumentException>(() => web.WithGroundhogTunnel());

        Assert.Equal("key", error.ParamName);
        Assert.Empty(builder.Resources.OfType<GroundhogTunnelResource>());
        Assert.DoesNotContain(builder.Resources.OfType<ParameterResource>(), parameter => parameter.Name == "groundhog-tunnel-name");
    }

    [Fact]
    public async Task WithGroundhogTunnelWithoutATunnelNameTakesVisitorAccessRules()
    {
        var builder = CreateBuilder();
        var web = builder.AddExecutable("web", "web", ".").WithHttpsEndpoint(name: "https").WithHttpEndpoint(name: "http");
        var configured = false;

        var returned = web.WithGroundhogTunnel(
            access => access.Allow(VisitorAllow.Members).Path("/webhooks", VisitorAllow.Anyone),
            endpointName: "http",
            configure: _ => configured = true);

        Assert.Same(web, returned);
        Assert.True(configured);
        var tunnel = Assert.Single(builder.Resources.OfType<GroundhogTunnelResource>());
        Assert.Equal("web-tunnel", tunnel.Name);
        Assert.Equal("http", tunnel.Target?.EndpointName);
        var declaration = (await tunnel.VisitorAccess!.ResolveAsync(Cancellation)).ToDeclaration();
        Assert.Equal(["members"], declaration.Default.Allow);
        Assert.Equal(["/webhooks"], declaration.Rules.Select(rule => rule.Path));
    }

    [Fact]
    public void WithGroundhogTunnelWithoutATunnelNameChecksItsArguments()
    {
        var builder = CreateBuilder();
        var web = builder.AddExecutable("web", "web", ".").WithHttpEndpoint(name: "http");

        Assert.Equal("resource", Assert.Throws<ArgumentNullException>(
            () => ((IResourceBuilder<ExecutableResource>)null!).WithGroundhogTunnel()).ParamName);
        Assert.Equal("access", Assert.Throws<ArgumentNullException>(
            () => web.WithGroundhogTunnel((Action<GroundhogVisitorAccessBuilder>)null!)).ParamName);
        Assert.Equal("endpointName", Assert.Throws<ArgumentException>(
            () => web.WithGroundhogTunnel(endpointName: "missing")).ParamName);
        Assert.Empty(builder.Resources.OfType<GroundhogTunnelResource>());
        Assert.Empty(builder.Resources.OfType<ParameterResource>());
    }
}
