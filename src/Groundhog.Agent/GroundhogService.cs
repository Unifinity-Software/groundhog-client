namespace Groundhog.Agent;

/// <summary>Connection defaults for the public Groundhog service.</summary>
public static class GroundhogService
{
    /// <summary>
    /// The hosted service address for this client version. Explicit relay addresses remain supported
    /// for self-hosted services and migrations to a different service address.
    /// </summary>
    public const string DefaultRelayAddress = "https://groundhog.unifinity.com";
}
