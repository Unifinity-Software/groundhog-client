namespace Groundhog.Protocol;

/// <summary>Connection pool size, keep-alive timing and HTTP/2 limits.</summary>
public static class ConnectionPool
{
    /// <summary>WebSockets the agent keeps open per tunnel unless configured otherwise.</summary>
    public const int DefaultSize = 2;

    /// <summary>Smallest configurable pool size.</summary>
    public const int MinSize = 1;

    /// <summary>Largest configurable pool size.</summary>
    public const int MaxSize = 8;

    /// <summary>Minimum simultaneous HTTP/2 streams the agent allows per connection.</summary>
    public const int MinConcurrentStreams = 100;

    /// <summary>Both sides send a WebSocket ping this often.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    /// <summary>A side that hears nothing for this long closes the socket.</summary>
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long the relay holds a visitor request while no connection is open, before answering with the
    /// "not connected" page.
    /// </summary>
    public static readonly TimeSpan ConnectionWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether <paramref name="size"/> is within <see cref="MinSize"/> and <see cref="MaxSize"/>.</summary>
    public static bool IsValidSize(int size) => size is >= MinSize and <= MaxSize;
}
