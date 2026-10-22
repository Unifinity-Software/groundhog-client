namespace Groundhog.Agent;

internal sealed record RequestRecord(DateTimeOffset Time, string Method, string Path, int Status,
    double DurationMs, bool WebSocket, string? Error);
internal sealed record SessionLog(DateTimeOffset Time, string State, string? Reason);
internal sealed record SessionSnapshot(string Name, string Target, string? PublicAddress, string State,
    DateTimeOffset Started, long Requests, long Errors, int Reconnects, double? P95Ms,
    RequestRecord[] Recent, string? Reason, SessionLog[] Logs, string Host = "Cli", string? ProcessName = null, int ProcessId = 0);

internal sealed class SessionTelemetry(string name, Uri target, TimeProvider? clock = null, string host = "Cli")
{
    internal const int Capacity = 1000;
    internal string Host { get; } = host;
    internal string ProcessName { get; } = ReadProcessName();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly DateTimeOffset _started = (clock ?? TimeProvider.System).GetUtcNow();
    private readonly Lock _gate = new();
    private readonly Queue<RequestRecord> _recent = new();
    private readonly Queue<SessionLog> _logs = new();
    private string _state = "Connecting";
    private string? _publicAddress;
    private string? _reason;
    private long _requests;
    private long _errors;
    private int _reconnects;

    private static string ReadProcessName()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.ProcessName;
    }

    internal void OnStatus(GroundhogAgentStatus status)
    {
        if (status.State == GroundhogAgentState.NotStarted)
        {
            return;
        }

        lock (_gate)
        {
            _state = status.State.ToString();
            _reason = status.Reason;
            if (_logs.Count == 100) { _logs.Dequeue(); }
            _logs.Enqueue(new(_clock.GetUtcNow(), _state, _reason));
            if (status.PublicAddress is not null)
            {
                _publicAddress = status.PublicAddress.ToString();
            }

            if (status.State == GroundhogAgentState.Reconnecting)
            {
                _reconnects++;
            }
        }
    }

    internal void OnRequest(GroundhogCompletedRequest request)
    {
        Record(new(_clock.GetUtcNow(), request.Method, request.Path, request.StatusCode,
            request.Duration.TotalMilliseconds, request.IsWebSocket, request.Error));
    }

    internal void Record(RequestRecord request)
    {
        lock (_gate)
        {
            _requests++;
            if (request.Status >= 500)
            {
                _errors++;
            }

            if (_recent.Count == Capacity)
            {
                _recent.Dequeue();
            }

            _recent.Enqueue(request);
        }
    }

    internal SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            var recent = _recent.ToArray();
            var durations = recent.Where(r => !r.WebSocket).Select(r => r.DurationMs).Order().ToArray();
            double? p95 = durations.Length == 0 ? null : durations[(int)Math.Ceiling(durations.Length * .95) - 1];
            return new(name, target.ToString(), _publicAddress, _state, _started, _requests, _errors,
                _reconnects, p95, recent, _reason, _logs.ToArray(), Host, ProcessName, Environment.ProcessId);
        }
    }
}
