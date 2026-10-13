using Groundhog.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Groundhog.Agent.Internal;

/// <summary>
/// One call to <see cref="GroundhogAgent.RunAsync"/>: a session ID, the local server, and one loop per pooled
/// connection that keeps its WebSocket open, reconnecting with backoff until the agent stops.
/// </summary>
internal sealed class AgentRun
{
    private readonly GroundhogAgentOptions _options;
    private readonly Action<GroundhogAgentStatusEventArgs> _onStatus;
    private readonly Action<GroundhogRequestEventArgs> _onRequest;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly RelayConnector _connector;
    private readonly TunnelConnectionListener _listener = new();
    private readonly CancellationTokenSource _stopping;
    private readonly Lock _gate = new();

    private int _openConnections;
    private GroundhogAgentStopReason? _stopReason;
    private string? _stopMessage;

    public AgentRun(
        GroundhogAgentOptions options,
        Action<GroundhogAgentStatusEventArgs> onStatus,
        Action<GroundhogRequestEventArgs> onRequest,
        CancellationToken cancellationToken)
    {
        _options = options;
        _onStatus = onStatus;
        _onRequest = onRequest;
        _time = options.TimeProvider;
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Groundhog.Agent");
        SessionId = Guid.NewGuid();
        _connector = new RelayConnector(options, SessionId);
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public Guid SessionId { get; }

    public async Task<GroundhogAgentStopReason> RunAsync()
    {
        try
        {
            _onStatus(new GroundhogAgentStatusEventArgs(GroundhogAgentState.Connecting));
            var server = await LocalServer.StartAsync(_listener, _options, _onRequest, _stopping.Token)
                .ConfigureAwait(false);
            await using (server.ConfigureAwait(false))
            {
                var slots = new Task[_options.PoolSize];
                for (var i = 0; i < slots.Length; i++)
                {
                    slots[i] = RunSlotAsync(i, _stopping.Token);
                }

                await Task.WhenAll(slots).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopped while the local server was starting.
        }
        finally
        {
            _stopping.Dispose();
            _connector.Dispose();
        }

        GroundhogAgentStopReason reason;
        string message;
        lock (_gate)
        {
            reason = _stopReason ?? GroundhogAgentStopReason.Cancelled;
            message = _stopMessage ?? "Stopped.";
        }

        _onStatus(new GroundhogAgentStatusEventArgs(
            GroundhogAgentState.Stopped, reason: message, stopReason: reason));
        return reason;
    }

    private async Task RunSlotAsync(int slot, CancellationToken cancellationToken)
    {
        try
        {
            await KeepConnectedAsync(slot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // A bug, not a network problem: stop the other connections too, then let RunAsync report it.
            await _stopping.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task KeepConnectedAsync(int slot, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            string reason;
            TimeSpan? retryAfter = null;
            var lostConnection = false;
            var lostLastConnection = false;
            switch (outcome)
            {
                case ConnectOutcome.Refused refused:
                    await StopAsync(refused.StopReason, refused.Reason).ConfigureAwait(false);
                    return;

                case ConnectOutcome.Retry retry:
                    reason = retry.Reason;
                    retryAfter = retry.RetryAfter;
                    break;

                case ConnectOutcome.Connected connected:
                    var hosted = await HostAsync(connected, cancellationToken).ConfigureAwait(false);
                    if (hosted is not { } closed)
                    {
                        return;
                    }

                    var (end, stable, wasLast) = closed;

                    if (end.CloseCode is int code && GroundhogCloseCodes.IsTerminal(code))
                    {
                        var (stopReason, message) = DescribeTerminalClose(code);
                        await StopAsync(stopReason, message).ConfigureAwait(false);
                        return;
                    }

                    reason = DescribeClose(end);
                    lostConnection = true;
                    lostLastConnection = wasLast;

                    // A connection that stayed up resets the schedule; one the relay keeps closing right away
                    // (for example with 4429) doesn't, so the agent keeps backing off.
                    if (stable)
                    {
                        attempt = 0;
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unexpected connect outcome {outcome.GetType().Name}.");
            }

            var delay = ReconnectBackoff.GetDelay(attempt, Random.Shared);
            if (retryAfter > delay)
            {
                delay = retryAfter.Value;
            }

            attempt++;

            // Start the wait before reporting it, so whoever sees the report (a test advancing a fake clock, say)
            // can rely on the wait already counting.
            var wait = Task.Delay(delay, _time, cancellationToken);
            ReportRetry(slot, reason, delay, lostConnection, lostLastConnection);
            await wait.ConfigureAwait(false);
        }
    }

    private async Task<ConnectOutcome> ConnectAsync(CancellationToken cancellationToken)
    {
        var outcome = await ConnectOnceAsync(previousTokenRejected: false, cancellationToken).ConfigureAwait(false);

        // A token with an expiry may simply have gone stale: ask the credential once more before giving up.
        if (outcome is ConnectOutcome.Refused { StatusCode: ConnectRefusal.Unauthorized, TokenExpires: true })
        {
            outcome = await ConnectOnceAsync(previousTokenRejected: true, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<ConnectOutcome> ConnectOnceAsync(bool previousTokenRejected, CancellationToken cancellationToken)
    {
        try
        {
            return await _connector.ConnectAsync(previousTokenRejected, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Most likely the credential failed to produce a token (for example its token service is unreachable),
            // which is worth retrying like any other temporary failure.
            _logger.LogWarning(ex, "Connection attempt failed before reaching the relay.");
            return new ConnectOutcome.Retry($"Couldn't connect: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Hands an open WebSocket to the local server and waits until it closes. Returns null if the agent is
    /// stopping, which closes the connection.
    /// </summary>
    private async Task<(ConnectionEnd End, bool Stable, bool WasLast)?> HostAsync(
        ConnectOutcome.Connected connected,
        CancellationToken cancellationToken)
    {
        var connection = new TunnelConnection(connected.WebSocket);
        if (!_listener.TryAdd(connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        var openedAt = _time.GetTimestamp();
        OnConnected(connected.PublicAddress);
        ConnectionEnd end;
        try
        {
            end = await connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _openConnections--;
            }

            return null;
        }

        bool wasLast;
        lock (_gate)
        {
            wasLast = --_openConnections == 0;
        }

        return (end, _time.GetElapsedTime(openedAt) >= ReconnectBackoff.MaxDelay, wasLast);
    }

    private void OnConnected(Uri publicAddress)
    {
        bool first;
        lock (_gate)
        {
            first = ++_openConnections == 1;
        }

        _logger.LogDebug("Tunnel connection opened.");
        if (first && !_stopping.IsCancellationRequested)
        {
            _onStatus(new GroundhogAgentStatusEventArgs(GroundhogAgentState.Connected, publicAddress: publicAddress));
        }
    }

    private void ReportRetry(int slot, string reason, TimeSpan delay, bool lostConnection, bool lostLastConnection)
    {
        // The status describes the tunnel as a whole: while any connection is open it still works. Report the
        // drop of the last open connection, then only the first slot's failed attempts while all are down, so the
        // pool doesn't repeat itself.
        bool report;
        lock (_gate)
        {
            report = _openConnections == 0 && (lostLastConnection || (slot == 0 && !lostConnection));
        }

        _logger.LogDebug("Tunnel connection {Slot}: {Reason} Retrying in {Delay}.", slot, reason, delay);
        if (report && !_stopping.IsCancellationRequested)
        {
            _onStatus(new GroundhogAgentStatusEventArgs(
                GroundhogAgentState.Reconnecting, reason: reason, retryDelay: delay));
        }
    }

    private async Task StopAsync(GroundhogAgentStopReason reason, string message)
    {
        lock (_gate)
        {
            if (_stopReason is null && !_stopping.IsCancellationRequested)
            {
                _stopReason = reason;
                _stopMessage = message;
            }
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
    }

    private static (GroundhogAgentStopReason Reason, string Message) DescribeTerminalClose(int code) => code switch
    {
        GroundhogCloseCodes.CredentialRevoked => (
            GroundhogAgentStopReason.CredentialRejected,
            "The relay revoked the credential, for example because the key was regenerated (close code 4401)."),
        GroundhogCloseCodes.TunnelGone => (
            GroundhogAgentStopReason.TunnelNotFound,
            "The tunnel was deleted or renamed (close code 4404)."),
        _ => (
            GroundhogAgentStopReason.SessionReplaced,
            "Another agent started hosting this tunnel (close code 4409)."),
    };

    private static string DescribeClose(ConnectionEnd end) => end.CloseCode switch
    {
        null => "Lost the connection to the relay.",
        GroundhogCloseCodes.Normal => "The relay closed the connection.",
        GroundhogCloseCodes.TooManyConnections => "The relay has too many connections (close code 4429).",
        var code => $"The relay closed the connection (close code {code}).",
    };
}
