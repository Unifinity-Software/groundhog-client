using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Groundhog.Agent.Internal;

/// <summary>Runs the app's <see cref="GroundhogAgent"/> for as long as the host runs.</summary>
internal sealed class GroundhogAgentHostedService(GroundhogAgent agent, ILogger<GroundhogAgentHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reason = await agent.RunAsync(stoppingToken).ConfigureAwait(false);
        if (reason != GroundhogAgentStopReason.Cancelled)
        {
            logger.LogError("The Groundhog agent stopped ({StopReason}): {Reason}", reason, agent.Status.Reason);
        }
    }
}
