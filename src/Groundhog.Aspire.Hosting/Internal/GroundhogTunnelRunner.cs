using System.Collections.Immutable;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Groundhog.Agent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Groundhog.Aspire.Hosting.Internal;

/// <summary>
/// Runs each tunnel's agent in the AppHost process for as long as the AppHost runs, and reflects the agent's status
/// in the tunnel's dashboard state, URL and console log.
/// </summary>
internal sealed class GroundhogTunnelRunner : IHostedService, IDisposable
{
    internal const string ResourceType = "GroundhogTunnel";
    internal const string RelayProperty = "groundhog.relay";
    internal const string TunnelProperty = "groundhog.tunnel";
    internal const string TargetProperty = "groundhog.target";
    internal const string VisitorAccessProperty = "groundhog.visitor-access";

    private static readonly ResourceStateSnapshot s_connecting = new("Connecting", KnownResourceStateStyles.Info);
    private static readonly ResourceStateSnapshot s_reconnecting = new("Reconnecting", KnownResourceStateStyles.Warn);

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<GroundhogTunnelResource, Task> _runs = [];
    private readonly HashSet<GroundhogTunnelResource> _terminal = [];
    private bool _stopped;

    /// <summary>Starts the tunnel's agent in the background and returns at once.</summary>
    public Task Start(GroundhogTunnelResource resource, ResourceNotificationService notifications, ILogger logger)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return Task.CompletedTask;
            }
            if (_runs.TryGetValue(resource, out var existing))
            {
                return existing;
            }
            return StartCore(resource, notifications, logger);
        }
    }

    private Task StartCore(GroundhogTunnelResource resource, ResourceNotificationService notifications, ILogger logger)
    {
        _terminal.Remove(resource);
        var run = Task.Run(() => RunAsync(resource, notifications, logger, _stopping.Token));
        _runs[resource] = run;
        return run;
    }

    internal async Task<ExecuteCommandResult> ReconnectAsync(GroundhogTunnelResource resource,
        ResourceNotificationService notifications, ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task previous;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopped)
            {
                return CommandResults.Failure("The AppHost is stopping.");
            }
            if (!_terminal.Contains(resource) || !_runs.TryGetValue(resource, out previous!))
            {
                return CommandResults.Failure("The tunnel is still running.");
            }
        }

        // Finish the old run's final snapshot before a replacement can publish any state.
        await previous.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_stopped)
            {
                return CommandResults.Failure("The AppHost is stopping.");
            }
            if (_runs[resource] != previous)
            {
                return CommandResults.Failure("The tunnel is already reconnecting.");
            }
            StartCore(resource, notifications, logger);
            return CommandResults.Success();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] runs;
        lock (_gate)
        {
            _stopped = true;
            runs = [.. _runs.Values];
        }
        await _stopping.CancelAsync().ConfigureAwait(false);

        // Each agent gives requests in flight a few seconds to finish; the host's own shutdown timeout still wins.
        await Task.WhenAll(runs).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => _stopping.Dispose();

    /// <summary>The dashboard state a tunnel starts with, before the AppHost starts it.</summary>
    internal static CustomResourceSnapshot InitialSnapshot() => new()
    {
        ResourceType = ResourceType,
        State = KnownResourceStates.NotStarted,
        Properties = [],
        IconName = "Globe",
    };

    private async Task RunAsync(
        GroundhogTunnelResource resource,
        ResourceNotificationService notifications,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        GroundhogAgent agent;
        Uri target;
        try
        {
            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Starting,
                StartTimeStamp = DateTime.UtcNow,
                StopTimeStamp = null,
                Urls = [],
            }).ConfigureAwait(false);

            var relayAddress = await resource.RelayAddressSource(cancellationToken).ConfigureAwait(false);
            var tunnelName = await resource.TunnelNameSource(cancellationToken).ConfigureAwait(false);
            if (resource.Target is null)
            {
                throw new InvalidOperationException(
                    "The tunnel has no target. Point it at an endpoint with WithReference(endpoint).");
            }

            // Waits until the endpoint has its port.
            var targetUrl = await resource.Target.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsedTarget))
            {
                throw new InvalidOperationException($"The target endpoint's address '{targetUrl}' isn't a URL.");
            }

            target = parsedTarget;

            var options = new GroundhogAgentOptions
            {
                RelayAddress = Uri.TryCreate(relayAddress, UriKind.Absolute, out var relay) ? relay : null,
                TunnelName = tunnelName,
                Credential = await resource.CredentialSource(cancellationToken).ConfigureAwait(false),
                LocalTarget = target,
                VisitorAccess = resource.VisitorAccess is { } access
                    ? await access.ResolveAsync(cancellationToken).ConfigureAwait(false)
                    : null,
                Client = GroundhogClientInfo.FromAssembly("Groundhog.Aspire.Hosting", typeof(GroundhogTunnelRunner).Assembly),
            };

            // Validates every setting; messages never repeat the credential.
            agent = new GroundhogAgent(options);

            await notifications.PublishUpdateAsync(resource, s => s with
            {
                Properties = WithProperties(
                    s.Properties,
                    new ResourcePropertySnapshot(RelayProperty, relay!.ToString()) { DisplayName = "Relay" },
                    new ResourcePropertySnapshot(TunnelProperty, tunnelName) { DisplayName = "Tunnel name" },
                    new ResourcePropertySnapshot(TargetProperty, target.ToString()) { DisplayName = "Forwards to" },
                    new ResourcePropertySnapshot(VisitorAccessProperty, resource.VisitorAccess?.Describe() ?? "Unchanged")
                    {
                        DisplayName = "Visitor access",
                    }),
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            resource.ReportFirstOutcome(null);
            return;
        }
        catch (Exception ex)
        {
            resource.ReportFirstOutcome(null);
            logger.LogError("The tunnel couldn't start: {Reason}", ex.Message);
            await PublishStoppedAsync(notifications, resource, KnownResourceStates.FailedToStart).ConfigureAwait(false);
            return;
        }

        if (resource.RequestLoggingEnabled)
        {
            agent.RequestCompleted += (_, request) => logger.LogInformation(
                "{Method} {Path} {StatusCode} {Duration} ms",
                request.IsWebSocket ? "WS" : request.Method,
                EscapeLogPath(request.Path),
                request.StatusCode,
                request.Duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture));
        }

        var connectedOnce = false;
        agent.StatusChanged += (_, status) =>
        {
            switch (status.State)
            {
                case GroundhogAgentState.Connecting:
                    Publish(notifications, resource, s => s with { State = s_connecting });
                    break;

                case GroundhogAgentState.Connected:
                    connectedOnce = true;
                    var address = status.PublicAddress!;
                    resource.ReportFirstOutcome(address);
                    logger.LogInformation("Connected: {PublicAddress} → {Target}", address, target);
                    Publish(notifications, resource, s => s with
                    {
                        State = KnownResourceStates.Running,
                        Urls = [new UrlSnapshot("public", address.ToString(), IsInternal: false)],
                    });
                    break;

                case GroundhogAgentState.Reconnecting:
                    resource.ReportFirstOutcome(null);
                    logger.LogWarning("Disconnected: {Reason} Reconnecting in {Delay}.", status.Reason, Describe(status.RetryDelay));
                    Publish(notifications, resource, s => s with
                    {
                        State = s_reconnecting,
                        Urls = [.. s.Urls.Select(url => url with { IsInactive = true })],
                    });
                    break;

                case GroundhogAgentState.Stopped:
                    resource.ReportFirstOutcome(null);
                    if (status.StopReason == GroundhogAgentStopReason.Cancelled)
                    {
                        break;
                    }

                    logger.LogError("Stopped: {Reason}", status.Reason);
                    break;
            }
        };

        var cancelled = false;
        try
        {
            cancelled = await agent.RunAsync(cancellationToken).ConfigureAwait(false) == GroundhogAgentStopReason.Cancelled;
        }
        catch (Exception ex)
        {
            resource.ReportFirstOutcome(null);
            logger.LogError(ex, "The tunnel stopped unexpectedly.");
        }

        // A tunnel that never connected failed to start; one that did has exited. Both are final, so a resource that
        // opted in with WaitFor stops waiting.
        var state = cancelled ? KnownResourceStates.Finished
            : connectedOnce ? KnownResourceStates.Exited
            : KnownResourceStates.FailedToStart;
        await PublishStoppedAsync(notifications, resource, state).ConfigureAwait(false);
    }

    private Task PublishStoppedAsync(
        ResourceNotificationService notifications,
        GroundhogTunnelResource resource,
        string state)
    {
        lock (_gate)
        {
            _terminal.Add(resource);
        }
        return notifications.PublishUpdateAsync(resource, s => s with
        {
            State = new ResourceStateSnapshot(
                state,
                state == KnownResourceStates.Finished ? KnownResourceStateStyles.Info : KnownResourceStateStyles.Error),
            StopTimeStamp = DateTime.UtcNow,
            Urls = [.. s.Urls.Select(url => url with { IsInactive = true })],
        });
    }

    // Status events are raised on the agent's threads and must return quickly. PublishUpdateAsync applies the update
    // before its first await, so updates keep the order the events were raised in.
    private static void Publish(
        ResourceNotificationService notifications,
        GroundhogTunnelResource resource,
        Func<CustomResourceSnapshot, CustomResourceSnapshot> update) =>
        _ = notifications.PublishUpdateAsync(resource, update);

    private static ImmutableArray<ResourcePropertySnapshot> WithProperties(
        ImmutableArray<ResourcePropertySnapshot> existing,
        params ResourcePropertySnapshot[] properties)
    {
        var names = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return [.. existing.Where(p => !names.Contains(p.Name)), .. properties];
    }

    // Paths come from visitors; keep control characters from creating extra console lines or terminal commands.
    private static string EscapeLogPath(string path) => string.Concat(path.Select(character =>
        char.IsControl(character) ? $"\\u{(int)character:x4}" : character.ToString()));

    private static string Describe(TimeSpan? delay) =>
        delay is { } d ? $"{Math.Max(1, (int)Math.Round(d.TotalSeconds))} s" : "a moment";
}
