using System.IO.Pipelines;
using System.Net.WebSockets;
using Groundhog.Protocol;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace Groundhog.Agent.Internal;

/// <summary>How a tunnel connection ended: the relay's close code, or none when the connection was simply lost.</summary>
internal readonly record struct ConnectionEnd(int? CloseCode, string? CloseDescription);

/// <summary>
/// One WebSocket to the relay, presented to Kestrel as a transport connection. Kestrel speaks HTTP/2 over it and
/// disposes it when the HTTP/2 connection is over, which completes <see cref="Completion"/>.
/// </summary>
internal sealed class TunnelConnection : ConnectionContext
{
    private static long s_nextId;

    private readonly WebSocketDuplexStream _stream;
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource<ConnectionEnd> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public TunnelConnection(WebSocket webSocket)
    {
        _stream = new WebSocketDuplexStream(webSocket);
        Transport = new DuplexPipe(
            PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: true)),
            PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true)));
        ConnectionId = $"groundhog-{Interlocked.Increment(ref s_nextId)}";
        ConnectionClosed = _closed.Token;
    }

    /// <summary>Completes when the connection is over, with the relay's close code if it sent one.</summary>
    public Task<ConnectionEnd> Completion => _completion.Task;

    public override string ConnectionId { get; set; }

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

    public override IDuplexPipe Transport { get; set; }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        // Ends any pending read or write; Kestrel then completes the pipes and disposes the connection.
        _stream.WebSocket.Abort();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var webSocket = _stream.WebSocket;
        ConnectionEnd? end = null;
        try
        {
            // Completing the output flushes Kestrel's last buffered write (its GOAWAY, say), which fails when the
            // relay is already gone. That is simply the connection ending; it must never keep the owner from
            // seeing the end, or the agent would wait forever instead of reconnecting.
            await CompleteQuietlyAsync(Transport.Input.CompleteAsync).ConfigureAwait(false);
            await CompleteQuietlyAsync(Transport.Output.CompleteAsync).ConfigureAwait(false);

            // The relay's close code, read before the agent's own close reply.
            end = new ConnectionEnd((int?)webSocket.CloseStatus, webSocket.CloseStatusDescription);

            // Answers the relay's close, or closes normally if the agent ended the connection.
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _completion.TrySetResult(end ?? new ConnectionEnd(null, null));
            try
            {
                await _closed.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                _closed.Dispose();
            }
        }
    }

    private static async ValueTask CompleteQuietlyAsync(Func<Exception?, ValueTask> complete)
    {
        try
        {
            await complete(null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            // The WebSocket is already broken; there's nothing left to flush to.
        }
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}
