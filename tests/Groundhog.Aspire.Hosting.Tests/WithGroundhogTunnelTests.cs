using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Groundhog.Agent;

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

        Assert.Equal("tunnelName", Assert.Throws<ArgumentNullException>(() => web.WithGroundhogTunnel(null!)).ParamName);
        Assert.Equal("resource", Assert.Throws<ArgumentNullException>(
            () => ((IResourceBuilder<ExecutableResource>)null!).WithGroundhogTunnel("myapp")).ParamName);
        Assert.Empty(builder.Resources.OfType<GroundhogTunnelResource>());
    }
}
