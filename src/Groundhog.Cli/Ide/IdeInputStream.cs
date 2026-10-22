using System.Globalization;
using System.Text;

namespace Groundhog.Cli.Ide;

/// <summary>Enforces allocation bounds before StreamJsonRpc allocates a frame body.</summary>
internal sealed class IdeInputStream(Stream input) : Stream
{
    private byte[] _frame = [];
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) { return 0; }
        if (_position == _frame.Length)
        {
            var header = new List<byte>();
            var single = new byte[1];
            while (true)
            {
                if (await input.ReadAsync(single, cancellationToken) == 0)
                {
                    if (header.Count == 0) { return 0; }
                    throw new IOException("Truncated protocol header.");
                }
                header.Add(single[0]);
                if (header.Count > 8192) { throw new IOException("Protocol header too large."); }
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) { break; }
            }
            var length = -1;
            foreach (var line in Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) { throw new IOException("Invalid protocol header."); }
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (length != -1 || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out length)
                        || length is <= 0 or > 1_048_576) { throw new IOException("Invalid protocol length."); }
                }
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                    && value.Contains("charset=", StringComparison.OrdinalIgnoreCase)
                    && !value.EndsWith("charset=utf-8", StringComparison.OrdinalIgnoreCase))
                { throw new IOException("Unsupported protocol encoding."); }
            }
            if (length < 0) { throw new IOException("Missing protocol length."); }
            // Normalize headers so downstream framing has one supported encoding and length.
            var prefix = Encoding.ASCII.GetBytes($"Content-Length: {length}\r\n\r\n");
            _frame = new byte[prefix.Length + length];
            prefix.CopyTo(_frame, 0);
            await input.ReadExactlyAsync(_frame.AsMemory(prefix.Length), cancellationToken);
            _position = 0;
        }
        var count = Math.Min(buffer.Length, _frame.Length - _position);
        _frame.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
