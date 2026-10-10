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
    /// Adds a tunnel that gives an endpoint a public HTTPS address through a Groundhog relay, authenticating with
    /// the tunnel's connection key.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource's name in the AppHost, for example <c>tunnel</c>.</param>
    /// <param name="relayAddress">The relay's address, for example <c>https://tunnel.example.com</c>.</param>
    /// <param name="tunnelName">The tunnel to host, for example <c>myapp</c>.</param>
    /// <param name="key">
    /// The tunnel's connection key, as a secret parameter (for example <c>builder.AddParameter("groundhog-key",
    /// secret: true)</c>, set in user secrets). It's handed to the agent in memory, never on a command line or in a log.
    /// </param>
    /// <returns>The tunnel. Point it at an endpoint with <see cref="WithReference"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't a secret parameter.</exception>
    /// <remarks>
    /// The relay address and tunnel name are checked when the tunnel starts; a bad value shows the tunnel as failed in
    /// the dashboard rather than stopping the AppHost.
    /// </remarks>
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
    /// Adds a tunnel whose relay address and tunnel name come from parameters, authenticating with the tunnel's
    /// connection key.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource's name in the AppHost, for example <c>tunnel</c>.</param>
    /// <param name="relayAddress">A parameter holding the relay's address, for example <c>https://tunnel.example.com</c>.</param>
    /// <param name="tunnelName">A parameter holding the tunnel to host, for example <c>myapp</c>.</param>
    /// <param name="key">The tunnel's connection key, as a secret parameter.</param>
    /// <returns>The tunnel. Point it at an endpoint with <see cref="WithReference"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't a secret parameter.</exception>
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
    /// Adds a tunnel that authenticates with any <see cref="GroundhogCredential"/>, for sign-in methods other than a
    /// connection key.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource's name in the AppHost, for example <c>tunnel</c>.</param>
    /// <param name="relayAddress">The relay's address, for example <c>https://tunnel.example.com</c>.</param>
    /// <param name="tunnelName">The tunnel to host, for example <c>myapp</c>.</param>
    /// <param name="credential">What the agent presents to the relay. It's asked for a token before every connection.</param>
    /// <returns>The tunnel. Point it at an endpoint with <see cref="WithReference"/>.</returns>
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
    /// Adds a tunnel whose relay address and tunnel name come from parameters, authenticating with any
    /// <see cref="GroundhogCredential"/>.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource's name in the AppHost, for example <c>tunnel</c>.</param>
    /// <param name="relayAddress">A parameter holding the relay's address.</param>
    /// <param name="tunnelName">A parameter holding the tunnel to host.</param>
    /// <param name="credential">What the agent presents to the relay. It's asked for a token before every connection.</param>
    /// <returns>The tunnel. Point it at an endpoint with <see cref="WithReference"/>.</returns>
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

    private static Func<CancellationToken, ValueTask<string?>> Constant(string value) =>
        _ => ValueTask.FromResult<string?>(value);

    private static Func<CancellationToken, ValueTask<string?>> FromParameter(IResourceBuilder<ParameterResource> parameter) =>
        parameter.Resource.GetValueAsync;
}
