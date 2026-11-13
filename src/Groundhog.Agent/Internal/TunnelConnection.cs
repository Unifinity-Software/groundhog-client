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
    private readonly TimeProvider _time;
    private readonly Pipe _input = new();
    private readonly Task _receive;
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource<ConnectionEnd> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public TunnelConnection(WebSocket webSocket, TimeProvider time)
    {
        _stream = new WebSocketDuplexStream(webSocket);
        _time = time;
        Transport = new DuplexPipe(
            _input.Reader,
            PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true)));
        ConnectionId = $"groundhog-{Interlocked.Increment(ref s_nextId)}";
        ConnectionClosed = _closed.Token;
        _receive = ReceiveAsync();
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
            await CompleteQuietlyAsync(Transport.Output.CompleteAsync).ConfigureAwait(false);

            // The relay's close code, read before the agent's own close reply.
            end = new ConnectionEnd((int?)webSocket.CloseStatus, webSocket.CloseStatusDescription);

            // Kestrel has stopped reading. Finish the WebSocket handshake before disposing the stream, which
            // also releases any receive still pending inside the pipe reader. A silent peer cannot block shutdown.
            if (webSocket.State == WebSocketState.Open)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1), _time);
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException
                    or OperationCanceledException or InvalidOperationException)
                {
                    // A disconnected or unresponsive peer is disposed below.
                }
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
            await CompleteQuietlyAsync(Transport.Input.CompleteAsync).ConfigureAwait(false);
            await _receive.ConfigureAwait(false);
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

    // The producer owns the socket receive independently of Kestrel's input cancellation. Cancelling a
    // Pipe read stops Kestrel promptly; only the close handshake (or Abort) ends the WebSocket receive.
    private async Task ReceiveAsync()
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                var count = await _stream.ReadAsync(_input.Writer.GetMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                _input.Writer.Advance(count);
                var flush = await _input.Writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            error = ex;
        }
        finally
        {
            await _input.Writer.CompleteAsync(error).ConfigureAwait(false);
        }
    }
}
