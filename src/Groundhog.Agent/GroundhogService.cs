namespace Groundhog.Agent;

/// <summary>Connection defaults for the public Groundhog service.</summary>
public static class GroundhogService
{
    /// <summary>
    /// The hosted service address for this client version. Every client uses it by default; an explicit relay address
    /// is a development and testing override only.
    /// </summary>
    public const string DefaultRelayAddress = "https://groundhog.unifinity.com";
}
