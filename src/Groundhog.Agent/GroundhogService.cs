namespace Groundhog.Agent;

/// <summary>Connection defaults for the public Groundhog service.</summary>
public static class GroundhogService
{
    /// <summary>
    /// The hosted service address for this client version. An explicit relay address remains supported as an
    /// override, for example for testing or a migration to a different service address.
    /// </summary>
    public const string DefaultRelayAddress = "https://groundhog.unifinity.com";
}
