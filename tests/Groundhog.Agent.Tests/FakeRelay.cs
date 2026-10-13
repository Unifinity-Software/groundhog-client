using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using Groundhog.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Groundhog.Agent.Tests;

/// <summary>
/// An in-process stand-in for the relay: answers the health endpoint, accepts agent WebSockets on the protocol's
/// connect endpoint and, like the real relay, acts as the HTTP/2 client over each one. Behaviour is scripted per
/// tunnel name, so one relay serves every test in a class.
/// </summary>
public sealed class FakeRelay : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, FakeTunnel> _tunnels = new(StringComparer.Ordinal);

    private FakeRelay(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>The relay's own address (http on loopback).</summary>
    public Uri Address { get; }

    public static async Task<FakeRelay> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(1));
        var app = builder.Build();

        FakeRelay? relay = null;
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
        app.Run(context => relay!.HandleConnectAsync(context));
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        relay = new FakeRelay(app, new Uri(address));
        return relay;
    }

    /// <summary>The script and observations for one tunnel name.</summary>
    public FakeTunnel Tunnel(string name) => _tunnels.GetOrAdd(name, static n => new FakeTunnel(n));

    public async ValueTask DisposeAsync()
    {
        foreach (var tunnel in _tunnels.Values)
        {
            tunnel.ReleaseAll();
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleConnectAsync(HttpContext context)
    {
        if (context.Request.Path == GroundhogProtocol.HealthPath)
        {
            await context.Response.WriteAsJsonAsync(
                new HealthResponse(HealthResponse.OkStatus, "fake", [GroundhogProtocol.Version]));
            return;
        }

        if (context.Request.Path != GroundhogProtocol.ConnectPath)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var tunnel = Tunnel(context.Request.Query[GroundhogProtocol.TunnelQueryParameter].ToString());
        tunnel.RecordAttempt(new ConnectAttempt(
            context.Request.Headers.Authorization.ToString(),
            context.Request.Headers[GroundhogHeaders.Session].ToString(),
            context.Request.Headers[GroundhogHeaders.Agent].ToString(),
            context.WebSockets.WebSocketRequestedProtocols.ToArray(),
            context.Request.Headers[GroundhogHeaders.ClientPackage].ToString(),
            context.Request.Headers[GroundhogHeaders.ClientVersion].ToString(),
            context.Request.Headers[GroundhogHeaders.VisitorAccess].ToString()));

        if (tunnel.TryTakeRefusal(out var status, out var message))
        {
            context.Response.StatusCode = status;
            if (message is not null)
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(message + "\n");
            }

            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        context.Response.Headers[GroundhogHeaders.PublicAddress] = $"https://{tunnel.Name}.groundhog.unifinity.com";
        if (tunnel.AppliesVisitorAccess && context.Request.Headers.ContainsKey(GroundhogHeaders.VisitorAccess))
        {
            context.Response.Headers[GroundhogHeaders.VisitorAccess] = GroundhogHeaders.VisitorAccessApplied;
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync(GroundhogProtocol.SubProtocol);
        await using var connection = new AgentConnection(webSocket);
        tunnel.AddConnection(connection);

        // The WebSocket lives only as long as this request does.
        await connection.Released;
    }
}

/// <summary>What one connect request carried.</summary>
public sealed record ConnectAttempt(string Authorization, string Session, string Agent, IReadOnlyList<string> SubProtocols,
    string ClientPackage, string ClientVersion, string VisitorAccess = "");

/// <summary>Scripted answers and observed connections for one tunnel name on the <see cref="FakeRelay"/>.</summary>
public sealed class FakeTunnel(string name)
{
    private readonly ConcurrentQueue<(int Status, string? Message)> _refusals = new();
    private readonly ConcurrentBag<AgentConnection> _all = [];
    private int _attemptCount;
    private int _alwaysRefuse;

    public string Name { get; } = name;

    /// <summary>Connect requests as they arrive.</summary>
    public Channel<ConnectAttempt> Attempts { get; } = Channel.CreateUnbounded<ConnectAttempt>();

    /// <summary>Accepted agent connections as they open.</summary>
    public Channel<AgentConnection> Connections { get; } = Channel.CreateUnbounded<AgentConnection>();

    public int AttemptCount => Volatile.Read(ref _attemptCount);

    /// <summary>
    /// Whether a connection that declares visitor access gets <c>X-Groundhog-Visitor-Access: applied</c>, as a relay
    /// that supports it answers. Set false to act like an older relay.
    /// </summary>
    public bool AppliesVisitorAccess { get; set; } = true;

    /// <summary>Answers the next connect requests with these HTTP statuses, one each, before accepting again.</summary>
    public void Refuse(params int[] statuses)
    {
        foreach (var status in statuses)
        {
            _refusals.Enqueue((status, null));
        }
    }

    /// <summary>Answers the next connect request with this status and a <c>text/plain</c> message, as the relay does.</summary>
    public void RefuseWithMessage(int status, string message) => _refusals.Enqueue((status, message));

    /// <summary>
    /// Answers every connect request from now on with this HTTP status, as the relay does for a rejected key: an
    /// agent's pooled connections each get the same answer, not just the first.
    /// </summary>
    public void RefuseAll(int status) => Volatile.Write(ref _alwaysRefuse, status);

    internal bool TryTakeRefusal(out int status, out string? message)
    {
        if (_refusals.TryDequeue(out var refusal))
        {
            (status, message) = refusal;
            return true;
        }

        message = null;
        status = Volatile.Read(ref _alwaysRefuse);
        return status != 0;
    }

    internal void RecordAttempt(ConnectAttempt attempt)
    {
        Interlocked.Increment(ref _attemptCount);
        Attempts.Writer.TryWrite(attempt);
    }

    internal void AddConnection(AgentConnection connection)
    {
        _all.Add(connection);
        Connections.Writer.TryWrite(connection);
    }

    internal void ReleaseAll()
    {
        foreach (var connection in _all)
        {
            connection.Release();
        }
    }
}

/// <summary>
/// One agent WebSocket seen from the relay's side, with an HTTP/2 client that sends requests through it, as the
/// relay does.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly WebSocket _webSocket;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _streamHandedOut;

    public AgentConnection(WebSocket webSocket)
    {
        _webSocket = webSocket;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            ConnectCallback = (_, _) => Interlocked.Exchange(ref _streamHandedOut, 1) == 0
                ? ValueTask.FromResult<Stream>(new WebSocketDuplexStream(_webSocket, ownsWebSocket: false))
                : throw new IOException("An agent WebSocket carries exactly one HTTP/2 connection."),
        };

        // http:// with HTTP/2 exactly means HTTP/2 without TLS, with prior knowledge: the protocol's "h2c".
        Client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    /// <summary>Sends requests to the agent. Use absolute http:// URIs; their host becomes <c>:authority</c>.</summary>
    public HttpClient Client { get; }

    internal Task Released => _released.Task;

    /// <summary>Closes the WebSocket with a protocol close code, as the relay does.</summary>
    public async Task CloseAsync(int closeCode, CancellationToken cancellationToken)
    {
        await _webSocket.CloseOutputAsync((WebSocketCloseStatus)closeCode, "test", cancellationToken);
        Release();
    }

    /// <summary>Drops the connection without a close, as when the relay process dies.</summary>
    public void Abort()
    {
        _webSocket.Abort();
        Release();
    }

    internal void Release() => _released.TrySetResult();

    public ValueTask DisposeAsync()
    {
        Release();
        Client.Dispose();
        return ValueTask.CompletedTask;
    }
}
