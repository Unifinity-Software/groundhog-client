using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;

namespace Groundhog.Agent.Internal;

/// <summary>The endpoint Kestrel "listens" on: the agent's open connections to the relay.</summary>
internal sealed class TunnelEndPoint : EndPoint
{
    public override string ToString() => "groundhog-tunnel";
}

/// <summary>
/// A Kestrel transport whose connections are the WebSockets the agent opened to the relay, instead of sockets
/// accepted from the network. Nothing on the machine listens for incoming connections.
/// </summary>
internal sealed class TunnelConnectionListener : IConnectionListenerFactory, IConnectionListenerFactorySelector, IConnectionListener
{
    private readonly Channel<TunnelConnection> _pending = Channel.CreateUnbounded<TunnelConnection>(
        new UnboundedChannelOptions { SingleReader = true });

    public EndPoint EndPoint { get; } = new TunnelEndPoint();

    /// <summary>Hands a newly opened connection to Kestrel. Returns false once the server is shutting down.</summary>
    public bool TryAdd(TunnelConnection connection) => _pending.Writer.TryWrite(connection);

    public bool CanBind(EndPoint endpoint) => endpoint is TunnelEndPoint;

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not TunnelEndPoint)
        {
            throw new NotSupportedException($"Only {nameof(TunnelEndPoint)} is supported.");
        }

        return ValueTask.FromResult<IConnectionListener>(this);
    }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _pending.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            // Null tells Kestrel the listener is done.
            return null;
        }
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (_pending.Writer.TryComplete())
        {
            // Connections opened but never accepted are closed here; their owners see them end.
            while (_pending.Reader.TryRead(out var connection))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync() => UnbindAsync();
}
