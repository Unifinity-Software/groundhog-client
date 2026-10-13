namespace Groundhog.Protocol;

/// <summary>Header names defined by the protocol.</summary>
public static class GroundhogHeaders
{
    /// <summary>Sent by the agent when connecting: the agent's version.</summary>
    public const string Agent = "X-Groundhog-Agent";

    /// <summary>Optional: the NuGet package hosting the agent (library, Aspire integration or CLI).</summary>
    public const string ClientPackage = "X-Groundhog-Client-Package";

    /// <summary>Optional: the hosting package's own version, not its agent dependency's version.</summary>
    public const string ClientVersion = "X-Groundhog-Client-Version";

    /// <summary>
    /// Sent by the agent when connecting: one GUID per agent run, shared by all of that run's connections.
    /// </summary>
    public const string Session = "X-Groundhog-Session";

    /// <summary>
    /// Sent by the relay on its <c>101 Switching Protocols</c> answer: the tunnel's public address, for example
    /// <c>https://myapp.groundhog.unifinity.com</c>, so the agent can show it. An agent connected to a relay that
    /// doesn't send it shows the relay's own address.
    /// </summary>
    public const string PublicAddress = "X-Groundhog-Public-Address";

    /// <summary>Added by the relay to every forwarded request: 32 lowercase hexadecimal characters.</summary>
    public const string RequestId = "X-Groundhog-Request-Id";

    /// <summary>
    /// Set on error responses produced by Groundhog itself rather than by the local app.
    /// Values are in <see cref="GroundhogErrors"/>.
    /// </summary>
    public const string Error = "X-Groundhog-Error";

    /// <summary>
    /// Optional, sent by the agent when connecting: the tunnel's visitor access rules as declared by the developer,
    /// encoded with <see cref="VisitorAccessDeclaration.Encode"/>. A relay that applied them answers its
    /// <c>101 Switching Protocols</c> with this header set to <see cref="VisitorAccessApplied"/>.
    /// </summary>
    public const string VisitorAccess = "X-Groundhog-Visitor-Access";

    /// <summary>The value of <see cref="VisitorAccess"/> on the relay's <c>101</c> once it applied a declaration.</summary>
    public const string VisitorAccessApplied = "applied";

    /// <summary>
    /// Sent by a caller to a tunnel path whose rule allows <see cref="VisitorAccessMethods.ApiKeys"/>: one of the
    /// tunnel's API keys (see <see cref="Protocol.ApiKey"/>), not its connection key. The relay removes it before
    /// forwarding the request.
    /// </summary>
    public const string ApiKey = "X-Groundhog-Api-Key";
}

/// <summary>Values of the <see cref="GroundhogHeaders.Error"/> header.</summary>
public static class GroundhogErrors
{
    /// <summary>The agent is connected, but nothing answered on the local target (sent with HTTP 502).</summary>
    public const string LocalUnreachable = "local-unreachable";

    /// <summary>The local app didn't respond within the relay's response timeout (sent with HTTP 504).</summary>
    public const string Timeout = "timeout";
}
