namespace Groundhog.Agent;

/// <summary>What a <see cref="GroundhogAgent"/> is doing.</summary>
public enum GroundhogAgentState
{
    /// <summary>Making its first connection to the relay.</summary>
    Connecting,

    /// <summary>At least one connection to the relay is open, so the tunnel's public address works.</summary>
    Connected,

    /// <summary>No connection is open. The agent is waiting before its next attempt.</summary>
    Reconnecting,

    /// <summary>The agent has stopped for good. <see cref="GroundhogAgentStatusEventArgs.StopReason"/> says why.</summary>
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

/// <summary>A change in a <see cref="GroundhogAgent"/>'s state.</summary>
public sealed class GroundhogAgentStatusEventArgs : EventArgs
{
    internal GroundhogAgentStatusEventArgs(
        GroundhogAgentState state,
        Uri? publicAddress = null,
        string? reason = null,
        TimeSpan? retryDelay = null,
        GroundhogAgentStopReason? stopReason = null)
    {
        State = state;
        PublicAddress = publicAddress;
        Reason = reason;
        RetryDelay = retryDelay;
        StopReason = stopReason;
    }

    /// <summary>The new state.</summary>
    public GroundhogAgentState State { get; }

    /// <summary>The tunnel's public address. Set when <see cref="State"/> is <see cref="GroundhogAgentState.Connected"/>.</summary>
    public Uri? PublicAddress { get; }

    /// <summary>
    /// A short, human-readable explanation, for example why the connection dropped or why the agent stopped.
    /// Never contains a credential.
    /// </summary>
    public string? Reason { get; }

    /// <summary>How long the agent waits before its next attempt. Set when reconnecting.</summary>
    public TimeSpan? RetryDelay { get; }

    /// <summary>Why the agent stopped. Set when <see cref="State"/> is <see cref="GroundhogAgentState.Stopped"/>.</summary>
    public GroundhogAgentStopReason? StopReason { get; }
}

/// <summary>One request that went through the tunnel, reported when it finishes.</summary>
/// <remarks>Bodies, query strings and headers are deliberately not included.</remarks>
public sealed class GroundhogRequestEventArgs : EventArgs
{
    internal GroundhogRequestEventArgs(
        string method,
        string path,
        int statusCode,
        TimeSpan duration,
        bool isWebSocket,
        string? error)
    {
        Method = method;
        Path = path;
        StatusCode = statusCode;
        Duration = duration;
        IsWebSocket = isWebSocket;
        Error = error;
    }

    /// <summary>The request method, for example <c>GET</c>. WebSocket requests report <c>GET</c>.</summary>
    public string Method { get; }

    /// <summary>The request path, without the query string (which can carry sign-in codes and tokens).</summary>
    public string Path { get; }

    /// <summary>
    /// The status sent back to the relay. A WebSocket the local app accepted reports 101, whichever HTTP version
    /// carried it.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>From the request's arrival until its response (or WebSocket) finished.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Whether this was a WebSocket.</summary>
    public bool IsWebSocket { get; }

    /// <summary>
    /// The <c>X-Groundhog-Error</c> value when the agent answered instead of the local app, for example
    /// <c>local-unreachable</c>; otherwise <see langword="null"/>.
    /// </summary>
    public string? Error { get; }
}
