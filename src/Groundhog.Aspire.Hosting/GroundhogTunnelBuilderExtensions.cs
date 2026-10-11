using System.ComponentModel;
using Aspire.Hosting.ApplicationModel;
using Groundhog.Agent;
using Groundhog.Aspire.Hosting;
using Groundhog.Aspire.Hosting.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// In the Aspire.Hosting namespace, like Aspire's own builder extensions, so an AppHost finds them without a using.
namespace Aspire.Hosting;

/// <summary>Adds Groundhog tunnels to a .NET Aspire AppHost.</summary>
public static class GroundhogTunnelBuilderExtensions
{
    /// <summary>
    /// Gives this resource a public HTTPS address through the hosted Groundhog service: adds a tunnel named
    /// <c>&lt;resource name&gt;-tunnel</c> that forwards to one of its endpoints, and a secret parameter
    /// <c>groundhog-&lt;tunnel name&gt;-key</c> for the tunnel's connection key.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resource">The resource to expose, for example a project.</param>
    /// <param name="tunnelName">The name created in the Groundhog website, for example <c>myapp</c>.</param>
    /// <param name="endpointName">
    /// The endpoint to forward to. Defaults to the resource's <c>https</c> endpoint, or its <c>http</c> endpoint when
    /// it has no <c>https</c> one. An <c>https</c> endpoint needs a certificate this machine trusts.
    /// </param>
    /// <param name="configure">
    /// Configures the tunnel, for example to pass <see cref="GroundhogTunnelResource.PublicAddress"/> to a resource
    /// or to turn on <see cref="WithRequestLogging"/>.
    /// </param>
    /// <returns>The resource builder, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// The resource has no endpoint named <paramref name="endpointName"/>, or, without an endpoint name, no
    /// <c>https</c> or <c>http</c> endpoint; or an existing <c>groundhog-&lt;tunnel name&gt;-key</c> parameter isn't
    /// secret.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Set the key as <c>Parameters:groundhog-&lt;tunnel name&gt;-key</c> in the AppHost's user secrets, or enter it
    /// when the Aspire dashboard asks for the unresolved parameter. A parameter with that name that already exists is
    /// reused, so it can be declared first, for example to add a description.
    /// </para>
    /// <para>
    /// This is the same tunnel as <see cref="AddGroundhogTunnel(IDistributedApplicationBuilder, string, string, IResourceBuilder{ParameterResource})"/>
    /// with <see cref="WithReference"/>; use those for other resource names or keys.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<T> WithGroundhogTunnel<T>(
        this IResourceBuilder<T> resource,
        string tunnelName,
        string? endpointName = null,
        Action<IResourceBuilder<GroundhogTunnelResource>>? configure = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(tunnelName);
        var endpoint = resource.GetEndpoint(SelectEndpoint(resource.Resource, endpointName));
        var builder = resource.ApplicationBuilder;
        var tunnel = builder.AddGroundhogTunnel($"{resource.Resource.Name}-tunnel", tunnelName, KeyParameter(builder, tunnelName))
            .WithReference(endpoint);
        configure?.Invoke(tunnel);
        return resource;
    }

    /// <summary>Adds a tunnel through the public Groundhog service using a secret connection key.</summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource name in the Aspire dashboard.</param>
    /// <param name="tunnelName">The name created in the Groundhog website.</param>
    /// <param name="key">The tunnel's connection key, as a secret parameter.</param>
    /// <returns>The tunnel. Point it at a local endpoint with <see cref="WithReference"/>.</returns>
    public static IResourceBuilder<GroundhogTunnelResource> AddGroundhogTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string tunnelName,
        IResourceBuilder<ParameterResource> key)
        => builder.AddGroundhogTunnel(name, GroundhogService.DefaultRelayAddress, tunnelName, key);

    /// <summary>Adds a tunnel through the public Groundhog service with a parameter for its tunnel name.</summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource name in the Aspire dashboard.</param>
    /// <param name="tunnelName">A parameter containing the name created in the Groundhog website.</param>
    /// <param name="key">The tunnel's connection key, as a secret parameter.</param>
    /// <returns>The tunnel. Point it at a local endpoint with <see cref="WithReference"/>.</returns>
    public static IResourceBuilder<GroundhogTunnelResource> AddGroundhogTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource> tunnelName,
        IResourceBuilder<ParameterResource> key)
    {
        ArgumentNullException.ThrowIfNull(tunnelName);
        var credential = KeyCredential(key);
        return AddTunnel(builder, name, Constant(GroundhogService.DefaultRelayAddress), FromParameter(tunnelName), credential)
            .WithReferenceRelationship(tunnelName.Resource)
            .WithReferenceRelationship(key.Resource);
    }

    /// <summary>
    /// Adds a tunnel through an explicit Groundhog service address, authenticating with the tunnel's connection key.
    /// A development and testing override of <see cref="GroundhogService.DefaultRelayAddress"/> only, for example to
    /// run against a local or fake relay.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource's name in the AppHost, for example <c>tunnel</c>.</param>
    /// <param name="relayAddress">
    /// The service address to use instead of <see cref="GroundhogService.DefaultRelayAddress"/>, for example a local
    /// relay.
    /// </param>
    /// <param name="tunnelName">The tunnel to host, for example <c>myapp</c>.</param>
    /// <param name="key">
    /// The tunnel's connection key, as a secret parameter (for example <c>builder.AddParameter("groundhog-key",
    /// secret: true)</c>, set in user secrets). It's handed to the agent in memory, never on a command line or in a log.
    /// </param>
    /// <returns>The tunnel. Point it at an endpoint with <see cref="WithReference"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't a secret parameter.</exception>
    /// <remarks>
    /// The service address and tunnel name are checked when the tunnel starts; a bad value shows the tunnel as failed
    /// in the dashboard rather than stopping the AppHost.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<GroundhogTunnelResource> AddGroundhogTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string relayAddress,
        string tunnelName,
        IResourceBuilder<ParameterResource> key)
    {
        ArgumentNullException.ThrowIfNull(relayAddress);
        ArgumentNullException.ThrowIfNull(tunnelName);
        var credential = KeyCredential(key);
        return AddTunnel(builder, name, Constant(relayAddress), Constant(tunnelName), credential)
            .WithReferenceRelationship(key.Resource);
    }

    /// <summary>
    /// Adds a tunnel whose service address and tunnel name come from parameters, authenticating with the tunnel's
    /// connection key. A development and testing override of <see cref="GroundhogService.DefaultRelayAddress"/> only,
    /// for example to run against a local or fake relay.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource's name in the AppHost, for example <c>tunnel</c>.</param>
    /// <param name="relayAddress">A parameter holding the service address.</param>
    /// <param name="tunnelName">A parameter holding the tunnel to host, for example <c>myapp</c>.</param>
    /// <param name="key">The tunnel's connection key, as a secret parameter.</param>
    /// <returns>The tunnel. Point it at an endpoint with <see cref="WithReference"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't a secret parameter.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<GroundhogTunnelResource> AddGroundhogTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource> relayAddress,
        IResourceBuilder<ParameterResource> tunnelName,
        IResourceBuilder<ParameterResource> key)
    {
        ArgumentNullException.ThrowIfNull(relayAddress);
        ArgumentNullException.ThrowIfNull(tunnelName);
        var credential = KeyCredential(key);
        return AddTunnel(builder, name, FromParameter(relayAddress), FromParameter(tunnelName), credential)
            .WithReferenceRelationship(relayAddress.Resource)
            .WithReferenceRelationship(tunnelName.Resource)
            .WithReferenceRelationship(key.Resource);
    }

    /// <summary>
    /// Adds a tunnel through an explicit Groundhog service address that authenticates with any
    /// <see cref="GroundhogCredential"/>, for sign-in methods other than a connection key. The service address is a
    /// development and testing override only; pass <see cref="GroundhogService.DefaultRelayAddress"/> for the hosted
    /// service.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource's name in the AppHost, for example <c>tunnel</c>.</param>
    /// <param name="relayAddress">
    /// The service address to use instead of <see cref="GroundhogService.DefaultRelayAddress"/>, for example a local
    /// relay.
    /// </param>
    /// <param name="tunnelName">The tunnel to host, for example <c>myapp</c>.</param>
    /// <param name="credential">What the agent presents to the relay. It's asked for a token before every connection.</param>
    /// <returns>The tunnel. Point it at an endpoint with <see cref="WithReference"/>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<GroundhogTunnelResource> AddGroundhogTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string relayAddress,
        string tunnelName,
        GroundhogCredential credential)
    {
        ArgumentNullException.ThrowIfNull(relayAddress);
        ArgumentNullException.ThrowIfNull(tunnelName);
        ArgumentNullException.ThrowIfNull(credential);
        return AddTunnel(builder, name, Constant(relayAddress), Constant(tunnelName), _ => ValueTask.FromResult(credential));
    }

    /// <summary>
    /// Adds a tunnel whose service address and tunnel name come from parameters, authenticating with any
    /// <see cref="GroundhogCredential"/>. The service address is a development and testing override only.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource's name in the AppHost, for example <c>tunnel</c>.</param>
    /// <param name="relayAddress">A parameter holding the service address.</param>
    /// <param name="tunnelName">A parameter holding the tunnel to host.</param>
    /// <param name="credential">What the agent presents to the relay. It's asked for a token before every connection.</param>
    /// <returns>The tunnel. Point it at an endpoint with <see cref="WithReference"/>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<GroundhogTunnelResource> AddGroundhogTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource> relayAddress,
        IResourceBuilder<ParameterResource> tunnelName,
        GroundhogCredential credential)
    {
        ArgumentNullException.ThrowIfNull(relayAddress);
        ArgumentNullException.ThrowIfNull(tunnelName);
        ArgumentNullException.ThrowIfNull(credential);
        return AddTunnel(
                builder, name, FromParameter(relayAddress), FromParameter(tunnelName), _ => ValueTask.FromResult(credential))
            .WithReferenceRelationship(relayAddress.Resource)
            .WithReferenceRelationship(tunnelName.Resource);
    }

    /// <summary>
    /// Sets the endpoint the tunnel forwards requests to, for example <c>web.GetEndpoint("http")</c>. The tunnel
    /// starts once the endpoint has its port.
    /// </summary>
    /// <param name="builder">The tunnel.</param>
    /// <param name="endpoint">
    /// The endpoint. Prefer an <c>http</c> endpoint: the agent connects to the local app directly, and an <c>https</c>
    /// one needs a certificate this machine trusts.
    /// </param>
    /// <returns>The tunnel.</returns>
    public static IResourceBuilder<GroundhogTunnelResource> WithReference(
        this IResourceBuilder<GroundhogTunnelResource> builder,
        EndpointReference endpoint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        builder.Resource.Target = endpoint;
        return builder.WithRelationship(endpoint.Resource, "Target");
    }

    /// <summary>
    /// Writes each completed request's method, path, status and duration to the tunnel's console log.
    /// Query strings, headers and bodies are omitted. Request logging is off by default.
    /// </summary>
    /// <param name="builder">The tunnel.</param>
    /// <returns>The tunnel.</returns>
    public static IResourceBuilder<GroundhogTunnelResource> WithRequestLogging(
        this IResourceBuilder<GroundhogTunnelResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.RequestLoggingEnabled = true;
        return builder;
    }

    private static IResourceBuilder<GroundhogTunnelResource> AddTunnel(
        IDistributedApplicationBuilder builder,
        string name,
        Func<CancellationToken, ValueTask<string?>> relayAddress,
        Func<CancellationToken, ValueTask<string?>> tunnelName,
        Func<CancellationToken, ValueTask<GroundhogCredential>> credential)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var tunnel = builder.AddResource(new GroundhogTunnelResource(name, relayAddress, tunnelName, credential))
            .WithInitialState(GroundhogTunnelRunner.InitialSnapshot())
            // A tunnel is for local development; deployments get their public address from their own configuration.
            .ExcludeFromManifest();

        if (builder.ExecutionContext.IsRunMode)
        {
            AddRunner(builder.Services);

            tunnel.WithCommand("reconnect", "Reconnect", context =>
                context.Services.GetRequiredService<GroundhogTunnelRunner>().ReconnectAsync(
                    tunnel.Resource,
                    context.Services.GetRequiredService<ResourceNotificationService>(),
                    context.Services.GetRequiredService<ResourceLoggerService>().GetLogger(tunnel.Resource),
                    context.CancellationToken),
                new CommandOptions
                {
                    Description = "Reconnect a stopped tunnel using its current settings.",
                    UpdateState = context => KnownResourceStates.TerminalStates.Contains(context.ResourceSnapshot.State?.Text)
                        ? ResourceCommandState.Enabled : ResourceCommandState.Disabled,
                });

            // Initialization runs before any resource starts. The agent runs in the background, so nothing waits for
            // it unless a resource asks to with WaitFor.
            tunnel.OnInitializeResource((resource, e, _) =>
            {
                e.Services.GetRequiredService<GroundhogTunnelRunner>().Start(resource, e.Notifications, e.Logger);
                return Task.CompletedTask;
            });
        }

        return tunnel;
    }

    private static void AddRunner(IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(GroundhogTunnelRunner)))
        {
            return;
        }

        services.AddSingleton<GroundhogTunnelRunner>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<GroundhogTunnelRunner>());
    }

    private static Func<CancellationToken, ValueTask<GroundhogCredential>> KeyCredential(
        IResourceBuilder<ParameterResource> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.Resource.Secret)
        {
            throw new ArgumentException(
                "The connection key must be a secret parameter, for example builder.AddParameter(\"groundhog-key\", secret: true).",
                nameof(key));
        }

        // A malformed key throws an ArgumentException whose message never contains the key.
        return async cancellationToken =>
            new TunnelKeyCredential(await key.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty);
    }

    /// <summary>
    /// The endpoint a tunnel added with WithGroundhogTunnel forwards to: the named one, otherwise <c>https</c>,
    /// otherwise <c>http</c>.
    /// </summary>
    private static string SelectEndpoint(IResourceWithEndpoints resource, string? endpointName)
    {
        var names = resource.Annotations.OfType<EndpointAnnotation>().Select(endpoint => endpoint.Name).ToArray();
        string? Find(string wanted) => names.FirstOrDefault(name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase));

        if (endpointName is not null)
        {
            return Find(endpointName) ?? throw new ArgumentException(
                $"The resource '{resource.Name}' has no endpoint named '{endpointName}'.{DescribeEndpoints(names)}",
                nameof(endpointName));
        }

        return Find("https") ?? Find("http") ?? throw new ArgumentException(
            $"The resource '{resource.Name}' has no https or http endpoint to forward to. Add one, or pass the "
            + $"endpoint's name as endpointName.{DescribeEndpoints(names)}",
            nameof(resource));
    }

    private static string DescribeEndpoints(string[] names) =>
        names.Length == 0 ? " It has no endpoints." : $" Its endpoints are: {string.Join(", ", names)}.";

    /// <summary>The secret <c>groundhog-&lt;tunnel name&gt;-key</c> parameter, reusing one that already exists.</summary>
    private static IResourceBuilder<ParameterResource> KeyParameter(IDistributedApplicationBuilder builder, string tunnelName)
    {
        var name = $"groundhog-{tunnelName}-key";
        if (builder.Resources.OfType<ParameterResource>().FirstOrDefault(
                parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            return builder.CreateResourceBuilder(existing);
        }

        return builder.AddParameter(name, secret: true)
            .WithDescription(
                $"Paste the connection key shown when you created the Groundhog tunnel '{tunnelName}'. "
                + "It is not your account password.")
            // Only the tunnel uses it, and tunnels are for local development.
            .ExcludeFromManifest();
    }

    private static Func<CancellationToken, ValueTask<string?>> Constant(string value) =>
        _ => ValueTask.FromResult<string?>(value);

    private static Func<CancellationToken, ValueTask<string?>> FromParameter(IResourceBuilder<ParameterResource> parameter) =>
        parameter.Resource.GetValueAsync;
}
