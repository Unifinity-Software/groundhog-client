namespace Groundhog.Protocol;

/// <summary>
/// Protocol version, WebSocket subprotocol and the relay paths agents and tools use.
/// </summary>
public static class GroundhogProtocol
{
    /// <summary>The protocol version this library implements.</summary>
    public const int Version = 1;

    /// <summary>The WebSocket subprotocol for <see cref="Version"/>, sent in <c>Sec-WebSocket-Protocol</c>.</summary>
    public const string SubProtocol = "groundhog.v1";

    /// <summary>
    /// Path prefix reserved on the relay's own address. Requests under it are never forwarded to a tunnel.
    /// </summary>
    public const string ReservedPathPrefix = "/_groundhog/";

    /// <summary>Path of the agent's WebSocket endpoint.</summary>
    public const string ConnectPath = "/_groundhog/v1/connect";

    /// <summary>Query-string parameter on <see cref="ConnectPath"/> that names the tunnel to host.</summary>
    public const string TunnelQueryParameter = "tunnel";

    /// <summary>Path of the relay's health endpoint.</summary>
    public const string HealthPath = "/_groundhog/v1/health";
}
