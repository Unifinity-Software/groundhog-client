using System.Diagnostics.Metrics;
using Groundhog.Agent;
using Groundhog.Agent.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Runs a <see cref="GroundhogAgent"/> as a hosted service in a .NET generic host app.</summary>
public static class GroundhogAgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="GroundhogAgent"/> singleton and a hosted service that runs it from when the host starts
    /// until it stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agent uses the app's <see cref="ILoggerFactory"/>, <see cref="IMeterFactory"/> and
    /// <see cref="TimeProvider"/> when they're registered and <paramref name="configure"/> doesn't set its own.
    /// Invalid options fail the host's start with an <see cref="ArgumentException"/>.
    /// </para>
    /// <para>
    /// Inject <see cref="GroundhogAgent"/> to read its <see cref="GroundhogAgent.Status"/> or
    /// <see cref="GroundhogAgent.WatchStatusAsync"/>. If the relay refuses the agent for good, the error is logged and
    /// the app keeps running.
    /// </para>
    /// </remarks>
    /// <param name="services">The app's services.</param>
    /// <param name="configure">Sets the tunnel name, credential and local target, and any other options.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddGroundhogAgent(
        this IServiceCollection services,
        Action<GroundhogAgentOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton(provider =>
        {
            var options = new GroundhogAgentOptions
            {
                LoggerFactory = provider.GetService<ILoggerFactory>(),
                MeterFactory = provider.GetService<IMeterFactory>(),
                TimeProvider = provider.GetService<TimeProvider>() ?? TimeProvider.System,
            };
            configure(options);
            return new GroundhogAgent(options);
        });
        services.AddHostedService<GroundhogAgentHostedService>();
        return services;
    }
}
