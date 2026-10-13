// VisitorAllow sits with the builder extensions, so an AppHost names it without a using.
namespace Aspire.Hosting;

/// <summary>Who may visit a Groundhog tunnel's address, in its visitor access rules.</summary>
public enum VisitorAllow
{
    /// <summary>Everyone, for example webhook senders and OAuth providers. Can't be combined with other methods.</summary>
    Anyone,

    /// <summary>Members of the tunnel's workspace, after signing in to Groundhog in the browser.</summary>
    Members,

    /// <summary>
    /// Requests with one of the tunnel's API keys in the <c>X-Groundhog-Api-Key</c> header (not the tunnel's
    /// connection key). Add API keys with <see cref="Groundhog.Aspire.Hosting.GroundhogVisitorAccessBuilder.ApiKey"/>.
    /// </summary>
    ApiKeys,

    /// <summary>
    /// Requests from one of the tunnel's IP ranges. Add ranges with
    /// <see cref="Groundhog.Aspire.Hosting.GroundhogVisitorAccessBuilder.IpRange"/>.
    /// </summary>
    Ips,
}
