namespace Groundhog.Protocol;

/// <summary>
/// HTTP statuses the relay may answer a connect request with instead of upgrading to a WebSocket.
/// </summary>
public static class ConnectRefusal
{
    /// <summary>
    /// The credential is missing, invalid, or uses a scheme the relay doesn't accept.
    /// The response's <c>WWW-Authenticate</c> header lists the accepted schemes. Permanent.
    /// </summary>
    public const int Unauthorized = 401;

    /// <summary>The credential is valid but may not host this tunnel. Permanent.</summary>
    public const int Forbidden = 403;

    /// <summary>The tunnel doesn't exist. Permanent.</summary>
    public const int UnknownTunnel = 404;

    /// <summary>The relay doesn't support this protocol version; the body names the ones it does. Permanent.</summary>
    public const int UnsupportedVersion = 426;

    /// <summary>Too many connection attempts. The <c>Retry-After</c> header gives seconds to wait. Temporary.</summary>
    public const int TooManyAttempts = 429;

    /// <summary>
    /// Whether the agent should stop and report rather than retry: 401, 403, 404 and 426.
    /// Everything else (429, 5xx) is temporary and retried with backoff.
    /// </summary>
    /// <remarks>
    /// A credential with an expiry is asked for a fresh token once before the agent gives up on a 401.
    /// </remarks>
    public static bool IsPermanent(int statusCode) => statusCode
        is Unauthorized
        or Forbidden
        or UnknownTunnel
        or UnsupportedVersion;
}
