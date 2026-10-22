namespace Groundhog.Agent;

/// <summary>What a <see cref="GroundhogAgent"/> is doing.</summary>
public enum GroundhogAgentState
{
    /// <summary><see cref="GroundhogAgent.RunAsync"/> hasn't been called yet.</summary>
    NotStarted,

    /// <summary>Making its first connection to the relay.</summary>
    Connecting,

    /// <summary>At least one connection to the relay is open, so the tunnel's public address works.</summary>
    Connected,

    /// <summary>No connection is open. The agent is waiting before its next attempt.</summary>
    Reconnecting,

    /// <summary>The agent has stopped for good. <see cref="GroundhogAgentStatus.StopReason"/> says why.</summary>
    Stopped,
}

/// <summary>Why a <see cref="GroundhogAgent"/> stopped.</summary>
public enum GroundhogAgentStopReason
{
    /// <summary>The caller cancelled <see cref="GroundhogAgent.RunAsync"/>.</summary>
    Cancelled,

    /// <summary>
    /// The relay refused the credential (HTTP 401), or revoked it while connected (close code 4401), for example
    /// because the key was regenerated.
    /// </summary>
    CredentialRejected,

    /// <summary>The credential is valid but may not host this tunnel (HTTP 403).</summary>
    NotAllowed,

    /// <summary>The relay has no such tunnel (HTTP 404), or it was deleted or renamed while connected (close code 4404).</summary>
    TunnelNotFound,

    /// <summary>The relay doesn't support this agent's protocol version (HTTP 426).</summary>
    UnsupportedProtocol,

    /// <summary>A newer agent session for the same tunnel replaced this one (close code 4409).</summary>
    SessionReplaced,

    /// <summary>
    /// The agent declared <see cref="GroundhogAgentOptions.VisitorAccess"/>, but the relay didn't confirm it applied
    /// the rules: it predates visitor access.
    /// </summary>
    VisitorAccessUnsupported,
}

/// <summary>
/// A <see cref="GroundhogAgent"/>'s state and the details that go with it, from <see cref="GroundhogAgent.Status"/>
/// or <see cref="GroundhogAgent.WatchStatusAsync"/>.
/// </summary>
/// <param name="State">What the agent is doing.</param>
/// <param name="PublicAddress">
/// The tunnel's public address. Set when <paramref name="State"/> is <see cref="GroundhogAgentState.Connected"/>.
/// </param>
/// <param name="Reason">
/// A short, human-readable explanation, for example why the connection dropped or why the agent stopped. Never
/// contains a credential.
/// </param>
/// <param name="RetryDelay">How long the agent waits before its next attempt. Set when reconnecting.</param>
/// <param name="StopReason">
/// Why the agent stopped. Set when <paramref name="State"/> is <see cref="GroundhogAgentState.Stopped"/>, unless
/// <see cref="GroundhogAgent.RunAsync"/> failed with an exception.
/// </param>
public sealed record GroundhogAgentStatus(
    GroundhogAgentState State,
    Uri? PublicAddress = null,
    string? Reason = null,
    TimeSpan? RetryDelay = null,
    GroundhogAgentStopReason? StopReason = null)
{
    internal static GroundhogAgentStatus NotStarted { get; } = new(GroundhogAgentState.NotStarted);
}

/// <summary>
/// One request that went through the tunnel, passed to <see cref="GroundhogAgentOptions.OnRequestCompleted"/> when it
/// finishes. Bodies, query strings and headers are deliberately not included.
/// </summary>
/// <param name="Method">The request method, for example <c>GET</c>. WebSocket requests report <c>GET</c>.</param>
/// <param name="Path">The request path, without the query string (which can carry sign-in codes and tokens).</param>
/// <param name="StatusCode">
/// The status sent back to the relay. A WebSocket the local app accepted reports 101, whichever HTTP version carried
/// it.
/// </param>
/// <param name="Duration">From the request's arrival until its response (or WebSocket) finished.</param>
/// <param name="IsWebSocket">Whether this was a WebSocket.</param>
/// <param name="Error">
/// The <c>X-Groundhog-Error</c> value when the agent answered instead of the local app, for example
/// <c>local-unreachable</c>; otherwise <see langword="null"/>.
/// </param>
public sealed record GroundhogCompletedRequest(
    string Method,
    string Path,
    int StatusCode,
    TimeSpan Duration,
    bool IsWebSocket,
    string? Error)
{
    /// <summary>The relay's opaque request ID, when present, for looking up captured detail through management APIs.</summary>
    public string? RequestId { get; init; }
}
