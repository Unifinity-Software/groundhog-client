using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Groundhog.Cli;

/// <summary>One open live request stream: the relay's server-sent <c>request</c> events, each a request log entry.</summary>
internal sealed class RequestStream(HttpResponseMessage response, Stream body) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The reconnection delay the relay asked for with <c>retry:</c>, once the stream has been read.</summary>
    internal TimeSpan? Retry { get; private set; }

    internal async IAsyncEnumerable<RemoteRequest> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellation)
    {
        var parser = SseParser.Create(body);
        try
        {
            await foreach (var item in parser.EnumerateAsync(cancellation))
            {
                if (parser.ReconnectionInterval > TimeSpan.Zero)
                {
                    Retry = parser.ReconnectionInterval;
                }

                if (item.EventType != "request" || Parse(item.Data) is not { } request)
                {
                    continue;
                }

                yield return request;
            }
        }
        finally
        {
            if (parser.ReconnectionInterval > TimeSpan.Zero)
            {
                Retry = parser.ReconnectionInterval;
            }
        }
    }

    private static RemoteRequest? Parse(string data)
    {
        try
        {
            var request = JsonSerializer.Deserialize<RemoteRequest>(data, Json);
            return request is { Method: not null, Path: not null } ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await body.DisposeAsync();
        response.Dispose();
    }
}

/// <summary>Whether a followed request log is receiving live events.</summary>
internal enum LiveStatus
{
    Live,
    Reconnecting,
    Stopped,
}

/// <summary>A change in a followed request log's connection; <paramref name="Message"/> explains a stop.</summary>
internal sealed record LiveState(LiveStatus Status, string? Message = null);

/// <summary>
/// Follows a tunnel's live request stream for as long as the caller wants. The relay ends every stream after a few
/// minutes and may drop it sooner, so the follower reconnects, and after each connection fetches the recent log to fill
/// any gap. Requests are reported once each (by request ID), oldest first within a backfill.
/// </summary>
internal sealed class RequestFollower(
    Func<CancellationToken, Task<RequestStream>> open,
    Func<CancellationToken, Task<RemoteRequest[]>> recent,
    TimeProvider time)
{
    internal static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumRetry = TimeSpan.FromSeconds(30);

    // Requests finishing around a reconnection can complete slightly before the newest one already seen.
    private static readonly TimeSpan BackfillOverlap = TimeSpan.FromSeconds(10);
    private const int Remembered = 2_000;

    /// <summary>Waits between connections; tests substitute their own to observe the delays without waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (delay, cancellation) => Task.Delay(delay, time, cancellation);

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private DateTimeOffset? _latest;

    /// <summary>Marks requests the caller already shows, so they are not reported again.</summary>
    internal void Seen(IEnumerable<RemoteRequest> requests)
    {
        foreach (var request in requests)
        {
            Remember(request);
        }
    }

    /// <summary>
    /// Runs until <paramref name="cancellation"/> fires or the relay refuses the stream for good (sign-in, permission,
    /// no such tunnel), which is reported as <see cref="LiveStatus.Stopped"/>.
    /// </summary>
    internal async Task RunAsync(Action<RemoteRequest> onRequest, Action<LiveState> onState, CancellationToken cancellation)
    {
        var failures = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            TimeSpan wait;
            RequestStream stream;
            try
            {
                stream = await open(cancellation);
            }
            catch (ManagementException ex) when (ex.Status == HttpStatusCode.TooManyRequests)
            {
                onState(new(LiveStatus.Reconnecting));
                await Delay(ex.RetryAfter is { } after && after > TimeSpan.Zero ? after : TimeSpan.FromSeconds(5), cancellation);
                continue;
            }
            catch (ManagementException ex) when (ex.Status is { } status && (int)status < 500)
            {
                onState(new(LiveStatus.Stopped, ex.Message));
                return;
            }
            catch (Exception ex) when (Transient(ex, cancellation))
            {
                onState(new(LiveStatus.Reconnecting));
                failures++;
                await Delay(Backoff(failures), cancellation);
                continue;
            }

            await using (stream)
            {
                failures = 0;
                onState(new(LiveStatus.Live));
                await BackfillAsync(onRequest, cancellation);
                try
                {
                    await foreach (var request in stream.ReadAllAsync(cancellation))
                    {
                        Report(request, onRequest);
                    }
                }
                catch (Exception ex) when (Transient(ex, cancellation))
                {
                }

                wait = stream.Retry ?? DefaultRetry;
            }

            onState(new(LiveStatus.Reconnecting));
            await Delay(wait, cancellation);
        }
    }

    private async Task BackfillAsync(Action<RemoteRequest> onRequest, CancellationToken cancellation)
    {
        if (_latest is not { } latest)
        {
            return;
        }

        RemoteRequest[] entries;
        try
        {
            entries = await recent(cancellation);
        }
        catch (Exception ex) when (ex is ManagementException || Transient(ex, cancellation))
        {
            return;
        }

        foreach (var request in entries.Where(r => r.Completed >= latest - BackfillOverlap).OrderBy(r => r.Completed))
        {
            Report(request, onRequest);
        }
    }

    private void Report(RemoteRequest request, Action<RemoteRequest> onRequest)
    {
        if (Remember(request))
        {
            onRequest(request);
        }
    }

    private bool Remember(RemoteRequest request)
    {
        if (_latest is null || request.Completed > _latest)
        {
            _latest = request.Completed;
        }

        if (request.RequestId is not { } id)
        {
            return true;
        }

        if (!_seen.Add(id))
        {
            return false;
        }

        _order.Enqueue(id);
        if (_order.Count > Remembered)
        {
            _seen.Remove(_order.Dequeue());
        }

        return true;
    }

    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(MaximumRetry.TotalSeconds, Math.Pow(2, Math.Min(failures - 1, 5))));

    private static bool Transient(Exception ex, CancellationToken cancellation) =>
        !cancellation.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException
            or ManagementException { Status: null or >= HttpStatusCode.InternalServerError };
}
