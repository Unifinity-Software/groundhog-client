using System.Net;
using System.Net.Sockets;
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
/// Shared for a test class: one fake relay and one local app (an ordinary HTTP/1.1 server, like a dev server).
/// </summary>
public sealed class TunnelFixture : IAsyncLifetime
{
    private WebApplication? _localApp;

    public FakeRelay Relay { get; private set; } = null!;

    /// <summary>The local app's address, for example http://127.0.0.1:54321.</summary>
    public Uri LocalApp { get; private set; } = null!;

    /// <summary>Released by a test to let <c>/stream</c> write its second half.</summary>
    public TaskCompletionSource StreamGate { get; private set; } = NewGate();

    public string Key { get; } = ConnectionKey.Generate();

    public async ValueTask InitializeAsync()
    {
        Relay = await FakeRelay.StartAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(1));
        _localApp = builder.Build();
        _localApp.UseWebSockets();
        _localApp.Run(HandleLocalRequestAsync);
        await _localApp.StartAsync();
        LocalApp = new Uri(_localApp.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    public async ValueTask DisposeAsync()
    {
        await Relay.DisposeAsync();
        if (_localApp is not null)
        {
            await _localApp.StopAsync();
            await _localApp.DisposeAsync();
        }
    }

    /// <summary>Starts an agent for <paramref name="tunnelName"/> against the fake relay.</summary>
    public RunningAgent StartAgent(string tunnelName, Uri? localTarget = null, TimeProvider? timeProvider = null, GroundhogClientInfo? client = null,
        GroundhogVisitorAccess? visitorAccess = null, int poolSize = 1) =>
        new(new GroundhogAgentOptions
        {
            RelayAddress = Relay.Address,
            TunnelName = tunnelName,
            Credential = new TunnelKeyCredential(Key),
            LocalTarget = localTarget ?? LocalApp,
            PoolSize = poolSize,
            TimeProvider = timeProvider ?? TimeProvider.System,
            Client = client,
            VisitorAccess = visitorAccess,
        });

    /// <summary>An address on loopback where nothing is listening.</summary>
    public static Uri UnusedLocalAddress()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }

    public TaskCompletionSource ResetStreamGate() => StreamGate = NewGate();

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task HandleLocalRequestAsync(HttpContext context)
    {
        switch (context.Request.Path.Value)
        {
            case "/echo":
                // What the local app saw: Host, path and query, and a header the relay adds.
                await context.Response.WriteAsync(string.Join(
                    '|',
                    context.Request.Host.Value,
                    context.Request.Path + context.Request.QueryString,
                    context.Request.Headers["X-Forwarded-Host"].ToString()));
                break;

            case "/stream":
                await context.Response.WriteAsync("first\n");
                await context.Response.Body.FlushAsync();
                await StreamGate.Task;
                await context.Response.WriteAsync("second\n");
                break;

            case "/ws" when context.WebSockets.IsWebSocketRequest:
                using (var webSocket = await context.WebSockets.AcceptWebSocketAsync())
                {
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var received = await webSocket.ReceiveAsync(buffer, context.RequestAborted);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
                            break;
                        }

                        await webSocket.SendAsync(
                            buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage,
                            context.RequestAborted);
                    }
                }

                break;

            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }
}

/// <summary>An agent running in the background, with its events captured in order.</summary>
public sealed class RunningAgent : IAsyncDisposable
{
    // Generous: it only matters when something is broken, and then the test fails with a clear message.
    private static readonly TimeSpan s_eventTimeout = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<GroundhogAgentStatusEventArgs> _statuses = Channel.CreateUnbounded<GroundhogAgentStatusEventArgs>();
    private readonly Channel<GroundhogRequestEventArgs> _requests = Channel.CreateUnbounded<GroundhogRequestEventArgs>();

    public RunningAgent(GroundhogAgentOptions options)
    {
        var agent = new GroundhogAgent(options);
        agent.StatusChanged += (_, status) => _statuses.Writer.TryWrite(status);
        agent.RequestCompleted += (_, request) => _requests.Writer.TryWrite(request);
        Run = agent.RunAsync(_stop.Token);
    }

    /// <summary>Completes with the stop reason when the agent stops.</summary>
    public Task<GroundhogAgentStopReason> Run { get; }

    /// <summary>The next status change, skipping <see cref="GroundhogAgentState.Connecting"/>.</summary>
    public async Task<GroundhogAgentStatusEventArgs> NextStatusAsync()
    {
        while (true)
        {
            var status = await ReadAsync(_statuses, "status change");
            if (status.State != GroundhogAgentState.Connecting)
            {
                return status;
            }
        }
    }

    public Task<GroundhogRequestEventArgs> NextRequestAsync() => ReadAsync(_requests, "request record");

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Run.WaitAsync(s_eventTimeout);
        _stop.Dispose();
    }

    private async Task<T> ReadAsync<T>(Channel<T> channel, string what)
    {
        try
        {
            return await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(s_eventTimeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            var stopped = Run.IsCompleted ? $" The agent had stopped: {Run.Status}." : string.Empty;
            throw new TimeoutException($"No {what} arrived.{stopped}", Run.Exception);
        }
    }
}
