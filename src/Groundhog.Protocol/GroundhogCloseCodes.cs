namespace Groundhog.Protocol;

/// <summary>WebSocket close codes the relay uses on agent connections.</summary>
public static class GroundhogCloseCodes
{
    /// <summary>Normal close, for example the relay is restarting. The agent reconnects with backoff.</summary>
    public const int Normal = 1000;

    /// <summary>
    /// The credential is no longer valid (key revoked or rotated, or membership removed). The agent stops and
    /// reports it.
    /// </summary>
    public const int CredentialRevoked = 4401;

    /// <summary>The tunnel was deleted or renamed. The agent stops and reports it.</summary>
    public const int TunnelGone = 4404;

    /// <summary>A newer agent session replaced this one. The agent stops quietly.</summary>
    public const int SessionReplaced = 4409;

    /// <summary>Too many connections. The agent reconnects with backoff.</summary>
    public const int TooManyConnections = 4429;

    /// <summary>
    /// Whether the agent must stop instead of reconnecting: <see cref="CredentialRevoked"/>,
    /// <see cref="TunnelGone"/> and <see cref="SessionReplaced"/>. Any other close, or a connection lost
    /// without a close, is temporary.
    /// </summary>
    public static bool IsTerminal(int closeCode) => closeCode
        is CredentialRevoked
        or TunnelGone
        or SessionReplaced;
}
