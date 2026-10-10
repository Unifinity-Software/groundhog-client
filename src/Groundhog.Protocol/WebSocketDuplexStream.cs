using System.Net.WebSockets;

namespace Groundhog.Protocol;

/// <summary>
/// Exposes a <see cref="System.Net.WebSockets.WebSocket"/> as a duplex <see cref="Stream"/>, so HTTP/2 can run
/// over the tunnel's WebSocket as if it were a socket.
/// </summary>
/// <remarks>
/// <para>
/// Each write is sent as one binary message. Reads ignore message boundaries: they return whatever bytes have
/// arrived, so a caller can read a message in pieces or several messages one after another. When the peer closes
/// the WebSocket, reads return 0 (end of stream); the peer's close code is then on
/// <see cref="WebSocket"/>.<see cref="WebSocket.CloseStatus"/>.
/// </para>
/// <para>
/// One read and one write may be in progress at the same time, as with a socket. Disposing sends a normal close
/// (<see cref="GroundhogCloseCodes.Normal"/>) without waiting for the peer's reply. To close with another code, call
/// <see cref="WebSocket.CloseOutputAsync"/> on <see cref="WebSocket"/> first.
/// </para>
/// <para>
/// Named to avoid clashing with <c>System.Net.WebSockets.WebSocketStream</c>, which code using both namespaces
/// would otherwise have to qualify.
/// </para>
/// </remarks>
public sealed class WebSocketDuplexStream : Stream
{
    private readonly bool _ownsWebSocket;
    private bool _inputEnded;
    private int _disposed;

    /// <summary>Wraps <paramref name="webSocket"/>, which must already be open.</summary>
    /// <param name="webSocket">The connected WebSocket.</param>
    /// <param name="ownsWebSocket">Whether disposing this stream closes and disposes the WebSocket.</param>
    public WebSocketDuplexStream(WebSocket webSocket, bool ownsWebSocket = true)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        WebSocket = webSocket;
        _ownsWebSocket = ownsWebSocket;
    }

    /// <summary>The wrapped WebSocket.</summary>
    public WebSocket WebSocket { get; }

    /// <inheritdoc />
    public override bool CanRead => !IsDisposed;

    /// <inheritdoc />
    public override bool CanWrite => !IsDisposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        while (!_inputEnded)
        {
            if (WebSocket.State is WebSocketState.CloseReceived or WebSocketState.Closed)
            {
                _inputEnded = true;
                break;
            }

            ValueWebSocketReceiveResult result;
            try
            {
                result = await WebSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                throw new IOException("The tunnel's WebSocket failed while reading.", ex);
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _inputEnded = true;
                break;
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new IOException("The tunnel's WebSocket received a text message; only binary messages are allowed.");
            }

            // An empty binary message carries nothing; keep reading unless the caller asked for a zero-byte read.
            if (result.Count > 0 || buffer.IsEmpty)
            {
                return result.Count;
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (buffer.IsEmpty)
        {
            return;
        }

        try
        {
            await WebSocket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new IOException("The tunnel's WebSocket failed while writing.", ex);
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <summary>Does nothing: every write is sent immediately.</summary>
    public override void Flush()
    {
    }

    /// <summary>Does nothing: every write is sent immediately.</summary>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsWebSocket)
        {
            await CloseOutputQuietlyAsync().ConfigureAwait(false);
            WebSocket.Dispose();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsWebSocket)
        {
            CloseOutputQuietlyAsync().GetAwaiter().GetResult();
            WebSocket.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task CloseOutputQuietlyAsync()
    {
        if (WebSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException
            or InvalidOperationException)
        {
            // The connection is already broken or a write is still in flight; disposing aborts it either way.
        }
    }
}
