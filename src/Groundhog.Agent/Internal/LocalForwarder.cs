using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Groundhog.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;

namespace Groundhog.Agent.Internal;

/// <summary>
/// Forwards each request that arrives through the tunnel to the local target, keeping the <c>Host</c> header the
/// visitor sent. HTTP/2 WebSockets (extended CONNECT) become HTTP/1.1 upgrades to the local app.
/// </summary>
internal sealed class LocalForwarder : IDisposable
{
    // WebSockets and server-sent events can sit idle for a long time. The relay times out requests that never
    // get a response, and cancels them here when it does, so the forwarder's own idle limit is set very long.
    private static readonly ForwarderRequestConfig _requestConfig = new()
    {
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        ActivityTimeout = TimeSpan.FromDays(1),
    };

    private readonly IHttpForwarder _forwarder;
    private readonly HttpMessageInvoker _client;
    private readonly string _destinationPrefix;
    private readonly TimeProvider _timeProvider;
    private readonly Action<GroundhogCompletedRequest> _onRequest;
    private readonly X509Certificate2? _localCa;

    public LocalForwarder(
        IHttpForwarder forwarder,
        GroundhogAgentOptions options,
        Action<GroundhogCompletedRequest> onRequest)
    {
        _forwarder = forwarder;
        var localTarget = options.LocalTarget!;
        _destinationPrefix = localTarget.GetLeftPart(UriPartial.Authority);
        _timeProvider = options.TimeProvider;
        _onRequest = onRequest;
        _localCa = options.LocalCaCertificateData is { } data ? X509CertificateLoader.LoadCertificate(data) : null;
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator = null,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var stream = await ConnectToAnyAddressAsync(context, cancellationToken,
                    loopbackOnly: options.InsecureLocal || _localCa is not null).ConfigureAwait(false);
                if (localTarget.Scheme != "https")
                {
                    return stream;
                }

                // SocketsHttpHandler otherwise uses the preserved public Host for SNI and certificate matching.
                // Returning an authenticated SslStream keeps TLS tied to the local target instead.
                var tls = new SslStream(stream);
                try
                {
                    var authentication = new SslClientAuthenticationOptions
                    {
                        TargetHost = localTarget.IdnHost,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                    };
                    if (options.InsecureLocal)
                    {
                        authentication.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
                    }
                    else if (_localCa is not null)
                    {
                        authentication.CertificateChainPolicy = new X509ChainPolicy
                        {
                            TrustMode = X509ChainTrustMode.CustomRootTrust,
                            RevocationMode = X509RevocationMode.NoCheck,
                        };
                        authentication.CertificateChainPolicy.CustomTrustStore.Add(_localCa);
                        authentication.CertificateChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                    }

                    await tls.AuthenticateAsClientAsync(authentication, cancellationToken).ConfigureAwait(false);
                    return tls;
                }
                catch
                {
                    await tls.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
        });
    }

    public async Task HandleAsync(HttpContext context)
    {
        var started = _timeProvider.GetTimestamp();
        var extendedConnect = context.Features.Get<IHttpExtendedConnectFeature>();
        var isWebSocket = extendedConnect?.IsExtendedConnect == true
            && string.Equals(extendedConnect.Protocol, "websocket", StringComparison.OrdinalIgnoreCase);

        var result = await _forwarder.SendAsync(
            context, _destinationPrefix, _client, _requestConfig, PreserveHostTransformer.Instance).ConfigureAwait(false);

        string? groundhogError = null;
        if (result != ForwarderError.None && !context.Response.HasStarted
            && IsConnectFailure(context.Features.Get<IForwarderErrorFeature>()?.Exception))
        {
            groundhogError = GroundhogErrors.LocalUnreachable;
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.Headers[GroundhogHeaders.Error] = groundhogError;
        }

        var status = context.Response.StatusCode;
        if (isWebSocket && status == StatusCodes.Status200OK)
        {
            status = StatusCodes.Status101SwitchingProtocols;
        }

        _onRequest(new GroundhogCompletedRequest(
            isWebSocket ? HttpMethods.Get : context.Request.Method,
            context.Request.Path.HasValue ? context.Request.Path.Value! : "/",
            status,
            _timeProvider.GetElapsedTime(started),
            isWebSocket,
            groundhogError)
        {
            RequestId = context.Request.Headers[GroundhogHeaders.RequestId].ToString() is { Length: 32 } requestId
                && requestId.All(char.IsAsciiHexDigit) ? requestId : null
        });
    }

    public void Dispose()
    {
        _client.Dispose();
        _localCa?.Dispose();
    }

    private static bool IsConnectFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException
                || current is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Connects to every address the host name resolves to at once and keeps the first that answers. A dev server
    /// on <c>localhost</c> may listen on IPv4 only or IPv6 only; trying the addresses one by one can cost seconds per
    /// connection on some systems.
    /// </summary>
    private static async ValueTask<Stream> ConnectToAnyAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken,
        bool loopbackOnly)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (loopbackOnly)
        {
            // Validate the resolved addresses as well as the URI, even if the machine's localhost mapping changes.
            addresses = addresses.Where(IPAddress.IsLoopback).ToArray();
        }

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = addresses.Select(address => ConnectAsync(address, port, raceCancellation.Token)).ToList();
        Exception? lastError = null;
        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);
            if (finished.IsCompletedSuccessfully)
            {
                await raceCancellation.CancelAsync().ConfigureAwait(false);
                foreach (var loser in attempts)
                {
                    // A slower attempt that still connects is closed; one that fails is observed and ignored.
                    _ = loser.ContinueWith(
                        static t =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                t.Result.Dispose();
                            }
                            else
                            {
                                _ = t.Exception;
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                return new NetworkStream(finished.Result, ownsSocket: true);
            }

            lastError = finished.Exception?.InnerException ?? lastError;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw lastError ?? new SocketException((int)SocketError.ConnectionRefused);
    }

    private static async Task<Socket> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The default request transform, except that <c>Host</c> stays as the visitor sent it.</summary>
    private sealed class PreserveHostTransformer : HttpTransformer
    {
        public static readonly PreserveHostTransformer Instance = new();

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken)
                .ConfigureAwait(false);

            // Over HTTP/2 the visitor's host arrives as :authority, which Kestrel exposes as Host.
            proxyRequest.Headers.Host = httpContext.Request.Host.Value;
        }
    }
}
