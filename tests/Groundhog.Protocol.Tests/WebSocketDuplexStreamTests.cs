using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;

namespace Groundhog.Protocol.Tests;

public class WebSocketDuplexStreamTests
{
    [Fact(Timeout = 10_000)]
    public async Task Carries_bytes_both_ways_regardless_of_message_boundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, server) = CreateConnectedPair();
        await using var agent = new WebSocketDuplexStream(client);
        await using var relay = new WebSocketDuplexStream(server);

        // Two writes (two messages) read back through a buffer smaller than either.
        await agent.WriteAsync("hello, "u8.ToArray(), ct);
        await agent.WriteAsync("relay"u8.ToArray(), ct);
        Assert.Equal("hello, relay", Encoding.UTF8.GetString(await ReadExactlyAsync(relay, 12, bufferSize: 5, ct)));

        // A large write in the other direction while a read is pending on the same stream.
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        var pendingRead = ReadExactlyAsync(agent, payload.Length, bufferSize: 16 * 1024, ct);
        await relay.WriteAsync(payload, ct);
        Assert.Equal(payload, await pendingRead);
    }

    [Fact(Timeout = 10_000)]
    public async Task Disposing_ends_the_peers_input_with_a_normal_close()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, server) = CreateConnectedPair();
        await using var relay = new WebSocketDuplexStream(server);
        var agent = new WebSocketDuplexStream(client);

        await agent.DisposeAsync();

        Assert.Equal(0, await relay.ReadAsync(new byte[16], ct));
        Assert.Equal(0, await relay.ReadAsync(new byte[16], ct));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, relay.WebSocket.CloseStatus);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_protocol_close_code_reaches_the_reader()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, server) = CreateConnectedPair();
        await using var agent = new WebSocketDuplexStream(client);
        await using var relay = new WebSocketDuplexStream(server);

        await relay.WebSocket.CloseOutputAsync(
            (WebSocketCloseStatus)GroundhogCloseCodes.SessionReplaced, "replaced", ct);

        Assert.Equal(0, await agent.ReadAsync(new byte[16], ct));
        Assert.True(GroundhogCloseCodes.IsTerminal((int)agent.WebSocket.CloseStatus!.Value));
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, int bufferSize, CancellationToken ct)
    {
        var result = new byte[length];
        var buffer = new byte[bufferSize];
        var received = 0;
        while (received < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(bufferSize, length - received)), ct);
            Assert.NotEqual(0, read);
            buffer.AsSpan(0, read).CopyTo(result.AsSpan(received));
            received += read;
        }

        return result;
    }

    /// <summary>A client and server WebSocket joined in memory, with no keep-alive timer.</summary>
    private static (WebSocket Client, WebSocket Server) CreateConnectedPair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var client = WebSocket.CreateFromStream(
            new PipeDuplexStream(toClient.Reader, toServer.Writer),
            new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        var server = WebSocket.CreateFromStream(
            new PipeDuplexStream(toServer.Reader, toClient.Writer),
            new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        return (client, server);
    }

    private sealed class PipeDuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _input = reader.AsStream();
        private readonly Stream _output = writer.AsStream();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => _output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override void Flush() => _output.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Dispose();
                _output.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
