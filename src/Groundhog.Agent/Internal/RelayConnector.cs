using System.Globalization;
using System.Net.WebSockets;
using Groundhog.Protocol;

namespace Groundhog.Agent.Internal;

/// <summary>The result of one attempt to open a WebSocket to the relay.</summary>
internal abstract record ConnectOutcome
{
    /// <summary>The relay accepted; the WebSocket is open.</summary>
    public sealed record Connected(WebSocket WebSocket, Uri PublicAddress) : ConnectOutcome;

    /// <summary>A temporary failure: wait and try again, no sooner than <paramref name="RetryAfter"/> if set.</summary>
    public sealed record Retry(string Reason, TimeSpan? RetryAfter) : ConnectOutcome;

    /// <summary>The relay refused for good.</summary>
    public sealed record Refused(int StatusCode, GroundhogAgentStopReason StopReason, string Reason, bool TokenExpires)
        : ConnectOutcome;
}

/// <summary>Opens WebSockets to the relay's connect endpoint and classifies the result.</summary>
internal sealed class RelayConnector
{
    /// <summary>The longest a single connection attempt may take, so a dead network can't hang the agent.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    // Honour a relay's Retry-After, but never wait longer than this for one attempt.
    private static readonly TimeSpan s_maxRetryAfter = TimeSpan.FromMinutes(5);

    private readonly GroundhogAgentOptions _options;
    private readonly Uri _connectUri;
    private readonly Guid _sessionId;
    private readonly Func<HttpMessageInvoker?> _invokerFactory;

    public RelayConnector(GroundhogAgentOptions options, Guid sessionId, Func<HttpMessageInvoker?>? invokerFactory = null)
    {
        _options = options;
        _sessionId = sessionId;
        _connectUri = BuildConnectUri(options.RelayAddress!, options.TunnelName!);
        _invokerFactory = invokerFactory ?? (() => null);
    }

    public static Uri BuildConnectUri(Uri relayAddress, string tunnelName)
    {
        var scheme = relayAddress.Scheme switch
        {
            "https" or "wss" => "wss",
            _ => "ws",
        };
        var builder = new UriBuilder(scheme, relayAddress.Host, relayAddress.Port)
        {
            Path = GroundhogProtocol.ConnectPath,
            Query = $"{GroundhogProtocol.TunnelQueryParameter}={Uri.EscapeDataString(tunnelName)}",
        };
        if (relayAddress.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    public async Task<ConnectOutcome> ConnectAsync(bool previousTokenRejected, CancellationToken cancellationToken)
    {
        var token = await _options.Credential!.GetTokenAsync(
            new GroundhogTokenRequestContext(_options.RelayAddress!, _options.TunnelName!, previousTokenRejected),
            cancellationToken).ConfigureAwait(false);

        var webSocket = new ClientWebSocket();
        var settings = webSocket.Options;
        settings.AddSubProtocol(GroundhogProtocol.SubProtocol);
        settings.SetRequestHeader(AgentAuthorization.HeaderName, AgentAuthorization.Format(token.Scheme, token.Token));
        settings.SetRequestHeader(GroundhogHeaders.Agent, GroundhogAgent.Version);
        settings.SetRequestHeader(GroundhogHeaders.Session, _sessionId.ToString("D"));
        settings.CollectHttpResponseDetails = true;

        // Ping every 15 s; a ping not answered within 30 s means 45 s without hearing from the relay, which
        // aborts the socket. That is also how a connection killed by sleep or a network change is noticed.
        settings.KeepAliveInterval = ConnectionPool.PingInterval;
        settings.KeepAliveTimeout = ConnectionPool.SilenceTimeout - ConnectionPool.PingInterval;

        using var timeout = new CancellationTokenSource(ConnectTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await webSocket.ConnectAsync(_connectUri, _invokerFactory(), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            webSocket.Dispose();
            return new ConnectOutcome.Retry("The relay didn't answer in time.", null);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
        {
            var outcome = Classify(webSocket, token);
            webSocket.Dispose();
            return outcome;
        }
        catch
        {
            webSocket.Dispose();
            throw;
        }

        var publicAddress = ReadPublicAddress(webSocket) ?? DefaultPublicAddress();
        webSocket.HttpResponseHeaders = null;
        return new ConnectOutcome.Connected(webSocket, publicAddress);
    }

    private ConnectOutcome Classify(ClientWebSocket webSocket, GroundhogAccessToken token)
    {
        var status = (int)webSocket.HttpStatusCode;
        if (status < 400)
        {
            // No HTTP answer at all: DNS, TCP or TLS failed.
            return new ConnectOutcome.Retry("Couldn't reach the relay.", null);
        }

        var name = _options.TunnelName;
        switch (status)
        {
            case ConnectRefusal.Unauthorized:
                var accepted = Header(webSocket, "WWW-Authenticate");
                return new ConnectOutcome.Refused(
                    status,
                    GroundhogAgentStopReason.CredentialRejected,
                    accepted is null
                        ? "The relay rejected the credential (HTTP 401)."
                        : $"The relay rejected the credential (HTTP 401). It accepts: {accepted}.",
                    token.ExpiresOn is not null);
            case ConnectRefusal.Forbidden:
                return new ConnectOutcome.Refused(
                    status,
                    GroundhogAgentStopReason.NotAllowed,
                    $"The credential isn't allowed to host tunnel '{name}' (HTTP 403).",
                    false);
            case ConnectRefusal.UnknownTunnel:
                return new ConnectOutcome.Refused(
                    status,
                    GroundhogAgentStopReason.TunnelNotFound,
                    $"The relay has no tunnel named '{name}' (HTTP 404).",
                    false);
            case ConnectRefusal.UnsupportedVersion:
                return new ConnectOutcome.Refused(
                    status,
                    GroundhogAgentStopReason.UnsupportedProtocol,
                    $"The relay doesn't support protocol version {GroundhogProtocol.Version} (HTTP 426). Update the agent or the relay.",
                    false);
            case ConnectRefusal.TooManyAttempts:
                return new ConnectOutcome.Retry("The relay is limiting connection attempts (HTTP 429).", ReadRetryAfter(webSocket));
            default:
                return new ConnectOutcome.Retry($"The relay answered HTTP {status}.", null);
        }
    }

    private static string? Header(ClientWebSocket webSocket, string name) =>
        webSocket.HttpResponseHeaders is { } headers && headers.TryGetValue(name, out var values)
            ? string.Join(", ", values)
            : null;

    private static TimeSpan? ReadRetryAfter(ClientWebSocket webSocket)
    {
        var value = Header(webSocket, "Retry-After");
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Min(seconds, s_maxRetryAfter.TotalSeconds))
            : null;
    }

    private static Uri? ReadPublicAddress(ClientWebSocket webSocket) =>
        Uri.TryCreate(Header(webSocket, GroundhogHeaders.PublicAddress), UriKind.Absolute, out var address)
        && address.Scheme is "https" or "http"
            ? address
            : null;

    // A relay that doesn't announce the address is taken to be in single-tunnel mode, where the tunnel is
    // reached at the relay's own address.
    private Uri DefaultPublicAddress()
    {
        var relay = _options.RelayAddress!;
        var scheme = relay.Scheme is "wss" or "https" ? "https" : "http";
        var builder = new UriBuilder(scheme, relay.Host, relay.IsDefaultPort ? -1 : relay.Port);
        return builder.Uri;
    }
}
