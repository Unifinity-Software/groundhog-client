using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Groundhog.Agent;
using Groundhog.Protocol;

namespace Groundhog.Aspire.Hosting;

/// <summary>
/// Declares a tunnel's visitor access rules: who may visit by default, per path, from which IP ranges and with which
/// API keys. The agent sends them on every connection.
/// </summary>
/// <example>
/// <code>
/// builder.AddProject&lt;Projects.MyApp_Web&gt;("web")
///     .WithGroundhogTunnel("myapp", access => access
///         .Allow(VisitorAllow.Members)
///         .Path("/webhooks", VisitorAllow.Anyone)
///         .IpRange("203.0.113.0/24")
///         .ApiKey(builder.AddParameter("myapp-api-key", secret: true)));
/// </code>
/// </example>
public sealed class GroundhogVisitorAccessBuilder
{
    private readonly List<VisitorAccessPathRule> _rules = [];
    private readonly List<string> _ipRanges = [];
    private readonly List<IResourceBuilder<ParameterResource>> _apiKeys = [];
    private string[] _allow = [VisitorAccessMethods.Anyone];

    internal GroundhogVisitorAccessBuilder()
    {
    }

    /// <summary>Sets who may visit paths no <see cref="Path"/> rule matches. Without it, anyone may.</summary>
    /// <param name="methods">One or more methods; a request passes when any of them passes.</param>
    /// <returns>This builder.</returns>
    public GroundhogVisitorAccessBuilder Allow(params VisitorAllow[] methods)
    {
        _allow = ToValues(methods);
        return this;
    }

    /// <summary>
    /// Sets who may visit a path prefix, for example <c>/webhooks</c>. The longest matching prefix wins, on segment
    /// boundaries and ignoring case.
    /// </summary>
    /// <param name="prefix">A path starting with <c>/</c>, without a query or fragment.</param>
    /// <param name="methods">One or more methods; a request passes when any of them passes.</param>
    /// <returns>This builder.</returns>
    public GroundhogVisitorAccessBuilder Path(string prefix, params VisitorAllow[] methods)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        _rules.Add(new VisitorAccessPathRule(prefix, ToValues(methods)));
        return this;
    }

    /// <summary>Adds an IP range for <see cref="VisitorAllow.Ips"/>, for example <c>203.0.113.0/24</c>.</summary>
    /// <param name="cidr">An IPv4 or IPv6 range in CIDR form.</param>
    /// <returns>This builder.</returns>
    public GroundhogVisitorAccessBuilder IpRange(string cidr)
    {
        ArgumentNullException.ThrowIfNull(cidr);
        _ipRanges.Add(cidr);
        return this;
    }

    /// <summary>
    /// Adds an API key for <see cref="VisitorAllow.ApiKeys"/>, from a secret parameter. Only its SHA-256 hash is
    /// sent to the service. API keys are what callers send to reach a path; they're not the tunnel's connection key.
    /// </summary>
    /// <param name="apiKey">
    /// A secret parameter holding an API key (43 base64url characters), for example one printed by
    /// <c>groundhog api-key create</c>.
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> isn't a secret parameter.</exception>
    public GroundhogVisitorAccessBuilder ApiKey(IResourceBuilder<ParameterResource> apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        if (!apiKey.Resource.Secret)
        {
            throw new ArgumentException(
                "An API key must be a secret parameter, for example builder.AddParameter(\"myapp-api-key\", secret: true).",
                nameof(apiKey));
        }

        _apiKeys.Add(apiKey);
        return this;
    }

    internal IReadOnlyList<IResourceBuilder<ParameterResource>> ApiKeys => _apiKeys;

    /// <summary>Checks the rules (API key values are checked when the tunnel starts) and captures them.</summary>
    internal VisitorAccessSource Build()
    {
        var defaultRule = new VisitorAccessRule(_allow);
        if (!VisitorAccessValidator.TryValidate(defaultRule, _rules, _ipRanges, _apiKeys.Count, out var error))
        {
            throw new ArgumentException($"Visitor access: {error}", "access");
        }

        return new VisitorAccessSource(_allow, [.. _rules], [.. _ipRanges], [.. _apiKeys.Select(apiKey => apiKey.Resource)]);
    }

    private static string[] ToValues(VisitorAllow[] methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        return [.. methods.Select(method => method switch
        {
            VisitorAllow.Anyone => VisitorAccessMethods.Anyone,
            VisitorAllow.Members => VisitorAccessMethods.Members,
            VisitorAllow.ApiKeys => VisitorAccessMethods.ApiKeys,
            VisitorAllow.Ips => VisitorAccessMethods.Ips,
            _ => throw new ArgumentOutOfRangeException(nameof(methods), method, "Unknown visitor access method."),
        })];
    }
}

/// <summary>A tunnel's declared rules, with API key parameters resolved when the tunnel starts.</summary>
internal sealed record VisitorAccessSource(
    IReadOnlyList<string> Allow,
    IReadOnlyList<VisitorAccessPathRule> Rules,
    IReadOnlyList<string> IpRanges,
    IReadOnlyList<ParameterResource> ApiKeys)
{
    public async ValueTask<GroundhogVisitorAccess> ResolveAsync(CancellationToken cancellationToken)
    {
        var apiKeys = new List<string>(ApiKeys.Count);
        foreach (var parameter in ApiKeys)
        {
            apiKeys.Add((await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false))?.Trim() ?? string.Empty);
        }

        return new GroundhogVisitorAccess { Allow = Allow, Rules = Rules, IpRanges = IpRanges, ApiKeys = apiKeys };
    }

    /// <summary>For the dashboard: the rules without API keys, for example <c>members; /webhooks: anyone</c>.</summary>
    public string Describe()
    {
        var parts = new List<string> { string.Join(" or ", Allow) };
        parts.AddRange(Rules.Select(rule => $"{rule.Path}: {string.Join(" or ", rule.Allow)}"));
        if (IpRanges.Count > 0)
        {
            parts.Add($"{IpRanges.Count} IP range(s)");
        }

        if (ApiKeys.Count > 0)
        {
            parts.Add($"{ApiKeys.Count} API key(s)");
        }

        return string.Join("; ", parts);
    }
}
