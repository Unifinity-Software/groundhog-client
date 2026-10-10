namespace Groundhog.Protocol;

/// <summary>Header names defined by the protocol.</summary>
public static class GroundhogHeaders
{
    /// <summary>Sent by the agent when connecting: the agent's version.</summary>
    public const string Agent = "X-Groundhog-Agent";

    /// <summary>
    /// Sent by the agent when connecting: one GUID per agent run, shared by all of that run's connections.
    /// </summary>
    public const string Session = "X-Groundhog-Session";

    /// <summary>
    /// Sent by the relay on its <c>101 Switching Protocols</c> answer: the tunnel's public address, for example
    /// <c>https://myapp.tunnel.example.com</c>, so the agent can show it. An agent connected to a relay that doesn't
    /// send it assumes single-tunnel mode, where the public address is the relay's own.
    /// </summary>
    public const string PublicAddress = "X-Groundhog-Public-Address";

    /// <summary>Added by the relay to every forwarded request: 32 lowercase hexadecimal characters.</summary>
    public const string RequestId = "X-Groundhog-Request-Id";

    /// <summary>
    /// Set on error responses produced by Groundhog itself rather than by the local app.
    /// Values are in <see cref="GroundhogErrors"/>.
    /// </summary>
    public const string Error = "X-Groundhog-Error";
}

/// <summary>Values of the <see cref="GroundhogHeaders.Error"/> header.</summary>
public static class GroundhogErrors
{
    /// <summary>The agent is connected, but nothing answered on the local target (sent with HTTP 502).</summary>
    public const string LocalUnreachable = "local-unreachable";

    /// <summary>The local app didn't respond within the relay's response timeout (sent with HTTP 504).</summary>
    public const string Timeout = "timeout";
}
