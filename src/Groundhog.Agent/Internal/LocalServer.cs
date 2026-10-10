using System.Net;
using Groundhog.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Forwarder;

namespace Groundhog.Agent.Internal;

/// <summary>
/// The HTTP/2 server inside the agent: Kestrel serving the tunnel's connections, with every request forwarded to
/// the local target. It accepts no network connections of its own.
/// </summary>
internal sealed class LocalServer : IAsyncDisposable
{
    /// <summary>How long requests in flight get to finish when the agent stops.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a tunnel connection may sit without requests before Kestrel closes it: practically never.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromDays(365);

    // At least the protocol's minimum, with room for a busy dev server's parallel asset requests.
    private const int MaxStreamsPerConnection = 250;

    private readonly WebApplication _app;
    private readonly LocalForwarder _forwarder;

    private LocalServer(WebApplication app, LocalForwarder forwarder)
    {
        _app = app;
        _forwarder = forwarder;
    }

    public static async Task<LocalServer> StartAsync(
        TunnelConnectionListener listener,
        GroundhogAgentOptions options,
        Action<GroundhogRequestEventArgs> onRequest,
        CancellationToken cancellationToken)
    {
        // The empty builder reads no configuration or environment variables, so settings such as ASPNETCORE_URLS
        // in the developer's shell can't make the agent listen on a port.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel => ConfigureKestrel(kestrel, listener.EndPoint));

        builder.Services.AddSingleton<IConnectionListenerFactory>(listener);
        builder.Services.AddHttpForwarder();
        builder.Services.AddSingleton<ILoggerFactory>(options.LoggerFactory ?? NullLoggerFactory.Instance);
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);

        var app = builder.Build();
        var forwarder = new LocalForwarder(
            app.Services.GetRequiredService<IHttpForwarder>(), options, onRequest);
        app.Run(forwarder.HandleAsync);

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            forwarder.Dispose();
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new LocalServer(app, forwarder);
    }

    /// <summary>Kestrel settings for serving the tunnel's connections.</summary>
    internal static void ConfigureKestrel(KestrelServerOptions kestrel, EndPoint endPoint)
    {
        kestrel.AddServerHeader = false;

        // The relay enforces body size and timeouts; the agent passes whatever arrives.
        kestrel.Limits.MaxRequestBodySize = null;
        kestrel.Limits.MinRequestBodyDataRate = null;
        kestrel.Limits.MinResponseDataRate = null;

        // The relay starts HTTP/2 on a connection only when it has a first request for it, which may be long after
        // connecting, and never closes an idle one; the WebSocket's pings detect dead connections. So the agent
        // waits practically forever. Not Timeout.InfiniteTimeSpan: with it, Kestrel closes a connection still
        // waiting for its first request within about a second.
        kestrel.Limits.KeepAliveTimeout = IdleTimeout;
        kestrel.Limits.Http2.MaxStreamsPerConnection =
            Math.Max(MaxStreamsPerConnection, ConnectionPool.MinConcurrentStreams);

        kestrel.Listen(endPoint, endpoint => endpoint.Protocols = HttpProtocols.Http2);
    }

    /// <summary>
    /// Stops accepting connections, gives requests in flight <see cref="ShutdownTimeout"/> to finish, then closes
    /// every tunnel connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _forwarder.Dispose();
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
