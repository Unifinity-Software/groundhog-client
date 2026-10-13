using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
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
internal sealed class RelayConnector : IDisposable
{
    /// <summary>The longest refusal message read from the relay's response body, in characters.</summary>
    internal const int MaxRefusalMessageLength = 200;

    // How much of a refusal's body is read: enough for a message, never a whole page.
    private const int MaxRefusalBodyBytes = 2048;

    /// <summary>The longest a single connection attempt may take, so a dead network can't hang the agent.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    // Honour a relay's Retry-After, but never wait longer than this for one attempt.
    private static readonly TimeSpan s_maxRetryAfter = TimeSpan.FromMinutes(5);

    private readonly GroundhogAgentOptions _options;
    private readonly Uri _connectUri;
    private readonly Guid _sessionId;
    // Like ClientWebSocket's own handler (system proxy, no cookies, no connection reuse), but owned here so a
    // refusal's body can be read.
    private readonly SocketsHttpHandler _handler = new()
    {
        PooledConnectionLifetime = TimeSpan.Zero,
        UseCookies = false,
        AllowAutoRedirect = false,
    };

    public RelayConnector(GroundhogAgentOptions options, Guid sessionId)
    {
        _options = options;
        _sessionId = sessionId;
        _connectUri = BuildConnectUri(options.RelayAddress!, options.TunnelName!);
    }

    public void Dispose() => _handler.Dispose();

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
        if (_options.Client is { } client)
        {
            settings.SetRequestHeader(GroundhogHeaders.ClientPackage, client.PackageId);
            settings.SetRequestHeader(GroundhogHeaders.ClientVersion, client.Version);
        }
        settings.SetRequestHeader(GroundhogHeaders.Session, _sessionId.ToString("D"));
        if (_options.VisitorAccessHeader is { } visitorAccess)
        {
            settings.SetRequestHeader(GroundhogHeaders.VisitorAccess, visitorAccess);
        }

        settings.CollectHttpResponseDetails = true;

        // Ping every 15 s; a ping not answered within 30 s means 45 s without hearing from the relay, which
        // aborts the socket. That is also how a connection killed by sleep or a network change is noticed.
        settings.KeepAliveInterval = ConnectionPool.PingInterval;
        settings.KeepAliveTimeout = ConnectionPool.SilenceTimeout - ConnectionPool.PingInterval;

        using var timeout = new CancellationTokenSource(ConnectTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        // The capturing handler wraps the shared one and must not dispose it, so neither it nor the invoker is
        // disposed.
        var capture = new RefusalCapture(_handler);
        var invoker = new HttpMessageInvoker(capture, disposeHandler: false);
        try
        {
            await webSocket.ConnectAsync(_connectUri, invoker, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            webSocket.Dispose();
            return new ConnectOutcome.Retry("The relay didn't answer in time.", null);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
        {
            var outcome = Classify(webSocket, token, capture.Message);
            webSocket.Dispose();
            return outcome;
        }
        catch
        {
            webSocket.Dispose();
            throw;
        }

        if (_options.VisitorAccessHeader is not null
            && !string.Equals(Header(webSocket, GroundhogHeaders.VisitorAccess)?.Trim(), GroundhogHeaders.VisitorAccessApplied,
                StringComparison.OrdinalIgnoreCase))
        {
            // An older relay accepted the connection without the rules; hosting without them would expose the app.
            await CloseQuietlyAsync(webSocket, cancellationToken).ConfigureAwait(false);
            return new ConnectOutcome.Refused(
                (int)HttpStatusCode.SwitchingProtocols,
                GroundhogAgentStopReason.VisitorAccessUnsupported,
                "This relay doesn't support visitor access rules; update the relay or remove them.",
                false);
        }

        var publicAddress = ReadPublicAddress(webSocket) ?? DefaultPublicAddress();
        webSocket.HttpResponseHeaders = null;
        return new ConnectOutcome.Connected(webSocket, publicAddress);
    }

    private async Task CloseQuietlyAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "visitor access unsupported", linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            // The socket is disposed either way.
        }
        finally
        {
            webSocket.Dispose();
        }
    }

    private ConnectOutcome Classify(ClientWebSocket webSocket, GroundhogAccessToken token, string? relayMessage)
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
                    relayMessage is null
                        ? $"The credential isn't allowed to host tunnel '{name}' (HTTP 403)."
                        : $"The relay refused to host tunnel '{name}' (HTTP 403): {relayMessage}",
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

    // When the relay doesn't announce the public address, the relay's own address is shown instead.
    private Uri DefaultPublicAddress()
    {
        var relay = _options.RelayAddress!;
        var scheme = relay.Scheme is "wss" or "https" ? "https" : "http";
        var builder = new UriBuilder(scheme, relay.Host, relay.IsDefaultPort ? -1 : relay.Port);
        return builder.Uri;
    }

    /// <summary>
    /// The first line of a refusal's <c>text/plain</c> body, with control characters removed, at most
    /// <see cref="MaxRefusalMessageLength"/> characters, ending in a full stop; <see langword="null"/> if empty.
    /// </summary>
    internal static string? ToRefusalMessage(string body)
    {
        var line = body.Split('\n', 2)[0];
        var builder = new StringBuilder(Math.Min(line.Length, MaxRefusalMessageLength + 1));
        foreach (var c in line)
        {
            if (builder.Length == MaxRefusalMessageLength)
            {
                builder.Append('…');
                break;
            }

            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        var message = builder.ToString().Trim();
        if (message.Length == 0)
        {
            return null;
        }

        return message[^1] is '.' or '!' or '?' or '…' ? message : message + ".";
    }

    /// <summary>
    /// Passes the WebSocket upgrade through and, when the relay refuses with 403, keeps the start of its message.
    /// </summary>
    private sealed class RefusalCapture(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public string? Message { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == ConnectRefusal.Forbidden
                && response.Content.Headers.ContentType?.MediaType == "text/plain")
            {
                try
                {
                    var buffer = new byte[MaxRefusalBodyBytes];
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var read = 0;
                    int count;
                    while (read < buffer.Length
                           && (count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        read += count;
                    }

                    Message = ToRefusalMessage(Encoding.UTF8.GetString(buffer, 0, read));
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    // The status alone still says what happened.
                }
            }

            return response;
        }
    }
}
