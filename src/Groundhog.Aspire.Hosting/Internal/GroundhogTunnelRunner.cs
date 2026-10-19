using System.Collections.Immutable;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Groundhog.Agent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Groundhog.Aspire.Hosting.Internal;

/// <summary>
/// Runs each tunnel's agent in the AppHost process, and reflects the agent's status in the tunnel's dashboard state,
/// URL and console log. An agent runs until the AppHost stops or the tunnel's Stop or Restart command stops it.
/// </summary>
internal sealed class GroundhogTunnelRunner : IHostedService, IDisposable
{
    internal const string ResourceType = "GroundhogTunnel";
    internal const string RelayProperty = "groundhog.relay";
    internal const string TunnelProperty = "groundhog.tunnel";
    internal const string TargetProperty = "groundhog.target";
    internal const string VisitorAccessProperty = "groundhog.visitor-access";
    internal const string RequestLoggingProperty = "groundhog.request-logging";

    internal const string ConnectingState = "Connecting";
    internal const string ReconnectingState = "Reconnecting";

    private static readonly ResourceStateSnapshot s_connecting = new(ConnectingState, KnownResourceStateStyles.Info);
    private static readonly ResourceStateSnapshot s_reconnecting = new(ReconnectingState, KnownResourceStateStyles.Warn);

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<GroundhogTunnelResource, TunnelRun> _runs = [];
    private readonly HashSet<GroundhogTunnelResource> _terminal = [];
    private readonly HashSet<GroundhogTunnelResource> _changing = [];
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
                return existing.Task;
            }
            return StartCore(resource, notifications, logger);
        }
    }

    private Task StartCore(GroundhogTunnelResource resource, ResourceNotificationService notifications, ILogger logger)
    {
        if (_runs.Remove(resource, out var previous))
        {
            previous.Cancellation.Dispose();
        }
        _terminal.Remove(resource);

        // Each run has its own cancellation, so one tunnel can stop while the AppHost and other tunnels keep running.
        var run = new TunnelRun(CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
        run.Task = Task.Run(() => RunAsync(run, resource, notifications, logger, run.Cancellation.Token));
        _runs[resource] = run;
        return run.Task;
    }

    /// <summary>The Start command: starts a new agent for a tunnel that isn't running, with its current settings.</summary>
    internal async Task<ExecuteCommandResult> StartTunnelAsync(GroundhogTunnelResource resource,
        ResourceNotificationService notifications, ILogger logger, CancellationToken cancellationToken)
    {
        if (BeginChange(resource) is { } refused)
        {
            return refused;
        }
        try
        {
            Task? previous;
            lock (_gate)
            {
                if (_runs.TryGetValue(resource, out var run) && !_terminal.Contains(resource))
                {
                    return CommandResults.Failure("The tunnel is already running.");
                }
                previous = run?.Task;
            }

            // Finish the old run's final snapshot before a replacement can publish any state.
            if (previous is not null)
            {
                await previous.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return StartAfterChange(resource, notifications, logger);
        }
        finally
        {
            EndChange(resource);
        }
    }

    /// <summary>The Stop command: stops one tunnel's agent and leaves the AppHost and other tunnels running.</summary>
    internal async Task<ExecuteCommandResult> StopTunnelAsync(GroundhogTunnelResource resource,
        ResourceNotificationService notifications, CancellationToken cancellationToken)
    {
        if (BeginChange(resource) is { } refused)
        {
            return refused;
        }
        try
        {
            return await StopCoreAsync(resource, notifications, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndChange(resource);
        }
    }

    /// <summary>The Restart command: stops the tunnel's agent, then starts a new one with the current settings.</summary>
    internal async Task<ExecuteCommandResult> RestartTunnelAsync(GroundhogTunnelResource resource,
        ResourceNotificationService notifications, ILogger logger, CancellationToken cancellationToken)
    {
        if (BeginChange(resource) is { } refused)
        {
            return refused;
        }
        try
        {
            var stopped = await StopCoreAsync(resource, notifications, cancellationToken).ConfigureAwait(false);
            return stopped.Success ? StartAfterChange(resource, notifications, logger) : stopped;
        }
        finally
        {
            EndChange(resource);
        }
    }

    private async Task<ExecuteCommandResult> StopCoreAsync(GroundhogTunnelResource resource,
        ResourceNotificationService notifications, CancellationToken cancellationToken)
    {
        TunnelRun? run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(resource, out run) || _terminal.Contains(resource))
            {
                return CommandResults.Failure("The tunnel isn't running.");
            }

            // From here the run publishes no more active states, so Stopping can't be overwritten by Running.
            run.StopRequested = true;
        }

        // A run that ended on its own in the meantime keeps its final state.
        await notifications.PublishUpdateAsync(resource, s => KnownResourceStates.TerminalStates.Contains(s.State?.Text)
            ? s
            : s with { State = KnownResourceStates.Stopping }).ConfigureAwait(false);
        await run.Cancellation.CancelAsync().ConfigureAwait(false);

        // The run publishes Finished before it completes.
        await run.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return CommandResults.Success();
    }

    private ExecuteCommandResult StartAfterChange(GroundhogTunnelResource resource,
        ResourceNotificationService notifications, ILogger logger)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return CommandResults.Failure("The AppHost is stopping.");
            }
            StartCore(resource, notifications, logger);
            return CommandResults.Success();
        }
    }

    // One command at a time per tunnel, so two clicks can't start overlapping agents.
    private ExecuteCommandResult? BeginChange(GroundhogTunnelResource resource)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return CommandResults.Failure("The AppHost is stopping.");
            }
            return _changing.Add(resource) ? null : CommandResults.Failure("Another command is already changing the tunnel.");
        }
    }

    private void EndChange(GroundhogTunnelResource resource)
    {
        lock (_gate)
        {
            _changing.Remove(resource);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] runs;
        lock (_gate)
        {
            _stopped = true;
            runs = [.. _runs.Values.Select(run => run.Task)];
        }
        await _stopping.CancelAsync().ConfigureAwait(false);

        // Each agent gives requests in flight a few seconds to finish; the host's own shutdown timeout still wins.
        await Task.WhenAll(runs).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values)
            {
                run.Cancellation.Dispose();
            }
        }
        _stopping.Dispose();
    }

    /// <summary>The dashboard state a tunnel starts with, before the AppHost starts it.</summary>
    internal static CustomResourceSnapshot InitialSnapshot() => new()
    {
        ResourceType = ResourceType,
        State = KnownResourceStates.NotStarted,
        Properties = [],
        IconName = "Globe",
    };

    private async Task RunAsync(
        TunnelRun run,
        GroundhogTunnelResource resource,
        ResourceNotificationService notifications,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        GroundhogAgent agent;
        Uri target;
        try
        {
            await PublishActiveAsync(notifications, resource, run, s => s with
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
                // Checked per request, so the dashboard's request logging commands take effect without a restart.
                OnRequestCompleted = request =>
                {
                    if (resource.RequestLoggingEnabled)
                    {
                        LogRequest(logger, request);
                    }
                },
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
                    },
                    RequestLoggingSnapshot(resource.RequestLoggingEnabled)),
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            resource.ReportFirstOutcome(null);
            await PublishStoppedAsync(notifications, resource, KnownResourceStates.Finished).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            resource.ReportFirstOutcome(null);
            logger.LogError("The tunnel couldn't start: {Reason}", ex.Message);
            await PublishStoppedAsync(notifications, resource, KnownResourceStates.FailedToStart).ConfigureAwait(false);
            return;
        }

        // Watch before running so every change is seen. Each update is published before the next is read, so the
        // dashboard shows them in order.
        var statuses = agent.WatchStatusAsync(CancellationToken.None);
        var running = agent.RunAsync(cancellationToken);
        var connectedOnce = false;
        await foreach (var status in statuses.ConfigureAwait(false))
        {
            switch (status.State)
            {
                case GroundhogAgentState.Connecting:
                    await PublishActiveAsync(notifications, resource, run, s => s with { State = s_connecting }).ConfigureAwait(false);
                    break;

                case GroundhogAgentState.Connected:
                    connectedOnce = true;
                    var address = status.PublicAddress!;
                    resource.ReportFirstOutcome(address);
                    logger.LogInformation("Connected: {PublicAddress} → {Target}", address, target);
                    await PublishActiveAsync(notifications, resource, run, s => s with
                    {
                        State = KnownResourceStates.Running,
                        Urls = [new UrlSnapshot("public", address.ToString(), IsInternal: false)],
                    }).ConfigureAwait(false);
                    break;

                case GroundhogAgentState.Reconnecting:
                    resource.ReportFirstOutcome(null);
                    logger.LogWarning("Disconnected: {Reason} Reconnecting in {Delay}.", status.Reason, Describe(status.RetryDelay));
                    await PublishActiveAsync(notifications, resource, run, s => s with
                    {
                        State = s_reconnecting,
                        Urls = [.. s.Urls.Select(url => url with { IsInactive = true })],
                    }).ConfigureAwait(false);
                    break;

                case GroundhogAgentState.Stopped:
                    resource.ReportFirstOutcome(null);

                    // Cancelled is the AppHost stopping or a Stop command; without a stop reason the agent failed and RunAsync says how.
                    if (status.StopReason is { } stopReason && stopReason != GroundhogAgentStopReason.Cancelled)
                    {
                        logger.LogError("Stopped: {Reason}", status.Reason);
                    }

                    break;
            }
        }

        var cancelled = false;
        try
        {
            cancelled = await running.ConfigureAwait(false) == GroundhogAgentStopReason.Cancelled;
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

    // Skips the update once a command has asked the run to stop, so the dashboard keeps showing Stopping.
    private static Task PublishActiveAsync(
        ResourceNotificationService notifications,
        GroundhogTunnelResource resource,
        TunnelRun run,
        Func<CustomResourceSnapshot, CustomResourceSnapshot> update) =>
        notifications.PublishUpdateAsync(resource, s => run.StopRequested ? s : update(s));

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

    private static void LogRequest(ILogger logger, GroundhogCompletedRequest request) => logger.LogInformation(
        "{Method} {Path} {StatusCode} {Duration} ms",
        request.IsWebSocket ? "WS" : request.Method,
        EscapeLogPath(request.Path),
        request.StatusCode,
        request.Duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture));

    /// <summary>The longest the Test public address command waits for a response.</summary>
    internal static readonly TimeSpan AddressTestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The Test public address command: sends a GET to the tunnel's public address, the way a visitor would, and reports
    /// the status and how long it took. Any response proves DNS, the relay and the agent work; a 5xx is reported as a
    /// failure because the relay or the app couldn't answer.
    /// </summary>
    internal static async Task<ExecuteCommandResult> TestPublicAddressAsync(GroundhogTunnelResource resource,
        ResourceNotificationService notifications, HttpClient client, ILogger logger, CancellationToken cancellationToken)
    {
        var address = notifications.TryGetCurrentState(resource.Name, out var current)
            ? current.Snapshot.Urls.FirstOrDefault(url => url.Name == "public" && !url.IsInactive)?.Url
            : null;
        if (address is null)
        {
            return CommandResults.Failure("The tunnel isn't connected.");
        }

        var time = resource.TimeProvider;
        using var timeout = new CancellationTokenSource(AddressTestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = time.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            var elapsed = time.GetElapsedTime(started);
            var message = string.Create(CultureInfo.InvariantCulture,
                $"GET {address} returned {(int)response.StatusCode} {response.ReasonPhrase} in {elapsed.TotalMilliseconds:0} ms.");
            if ((int)response.StatusCode >= 500)
            {
                logger.LogWarning("Test public address: {Result}", message);
                return CommandResults.Failure(message);
            }
            logger.LogInformation("Test public address: {Result}", message);
            return CommandResults.Success(message);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var message = $"GET {address} got no response within {AddressTestTimeout.TotalSeconds:0} s.";
            logger.LogWarning("Test public address: {Result}", message);
            return CommandResults.Failure(message);
        }
        catch (HttpRequestException ex)
        {
            var message = $"GET {address} failed: {ex.Message}";
            logger.LogWarning("Test public address: {Result}", message);
            return CommandResults.Failure(message);
        }
    }

    /// <summary>Switches request logging on or off for the tunnel's current and later agents.</summary>
    internal static async Task<ExecuteCommandResult> SetRequestLoggingAsync(
        GroundhogTunnelResource resource, ResourceNotificationService notifications, bool enabled)
    {
        resource.RequestLoggingEnabled = enabled;

        // The new snapshot also makes the dashboard re-evaluate which of the two commands to show.
        await notifications.PublishUpdateAsync(resource, s => s with
        {
            Properties = WithProperties(s.Properties, RequestLoggingSnapshot(enabled)),
        }).ConfigureAwait(false);
        return CommandResults.Success();
    }

    private static ResourcePropertySnapshot RequestLoggingSnapshot(bool enabled) =>
        new(RequestLoggingProperty, enabled ? "On" : "Off") { DisplayName = "Request logging" };

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

    /// <summary>One agent run of a tunnel: from Start (or AppHost start) until it stops.</summary>
    private sealed class TunnelRun(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Task { get; set; } = Task.CompletedTask;

        private volatile bool _stopRequested;

        public bool StopRequested
        {
            get => _stopRequested;
            set => _stopRequested = value;
        }
    }
}
