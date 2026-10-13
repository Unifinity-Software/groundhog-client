using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Groundhog.Protocol;

/// <summary>
/// The values of an <c>allow</c> list in a tunnel's visitor access rules. A request passes when any listed method
/// passes.
/// </summary>
public static class VisitorAccessMethods
{
    /// <summary>Everyone, with no check. Can't be combined with another method in the same list.</summary>
    public const string Anyone = "anyone";

    /// <summary>Members of the tunnel's workspace, signed in through the relay.</summary>
    public const string Members = "members";

    /// <summary>Requests carrying one of the tunnel's API keys in <see cref="GroundhogHeaders.ApiKey"/>.</summary>
    public const string ApiKeys = "apikeys";

    /// <summary>Requests from one of the tunnel's IP ranges.</summary>
    public const string Ips = "ips";

    /// <summary>Every method, in the order the documentation lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Anyone, Members, ApiKeys, Ips];

    /// <summary>Whether <paramref name="value"/> is one of the methods. Values are lowercase and compared exactly.</summary>
    public static bool IsKnown(string? value) => value is Anyone or Members or ApiKeys or Ips;
}

/// <summary>Who may visit: <c>{ "allow": [ ... ] }</c>, the tunnel's default rule.</summary>
/// <param name="Allow">One or more of <see cref="VisitorAccessMethods"/>.</param>
public sealed record VisitorAccessRule([property: JsonPropertyName("allow")] IReadOnlyList<string> Allow)
{
    /// <summary>The rule every tunnel starts with: <c>{ "allow": ["anyone"] }</c>.</summary>
    public static VisitorAccessRule Anyone { get; } = new([VisitorAccessMethods.Anyone]);
}

/// <summary>
/// Who may visit one path: <c>{ "path": "/webhooks", "allow": [ ... ] }</c>. The longest matching path prefix wins,
/// on segment boundaries and ignoring case; the default rule applies when none matches.
/// </summary>
/// <param name="Path">A path prefix starting with <c>/</c>, for example <c>/webhooks</c>.</param>
/// <param name="Allow">One or more of <see cref="VisitorAccessMethods"/>.</param>
public sealed record VisitorAccessPathRule(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("allow")] IReadOnlyList<string> Allow);

/// <summary>
/// A tunnel's visitor access rules as an agent declares them in <see cref="GroundhogHeaders.VisitorAccess"/>:
/// <c>{ "default": { "allow": [...] }, "rules": [...], "ipRanges": [...], "apiKeyHashes": [...] }</c>.
/// </summary>
/// <remarks>
/// API keys never travel in a declaration, only their hashes (<see cref="ApiKey.Hash"/>).
/// </remarks>
public sealed class VisitorAccessDeclaration
{
    /// <summary>
    /// The longest encoded declaration (<see cref="Encode"/>) an agent sends and a relay accepts, in characters.
    /// </summary>
    public const int MaxEncodedLength = 16 * 1024;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>The rule for paths no <see cref="Rules"/> entry matches. Defaults to <see cref="VisitorAccessRule.Anyone"/>.</summary>
    [JsonPropertyName("default")]
    public VisitorAccessRule Default { get; init; } = VisitorAccessRule.Anyone;

    /// <summary>Path rules, at most <see cref="VisitorAccessValidator.MaxRules"/>.</summary>
    [JsonPropertyName("rules")]
    public IReadOnlyList<VisitorAccessPathRule> Rules { get; init; } = [];

    /// <summary>
    /// IPv4 and IPv6 ranges in CIDR form, for example <c>203.0.113.0/24</c>, at most
    /// <see cref="VisitorAccessValidator.MaxIpRanges"/>.
    /// </summary>
    [JsonPropertyName("ipRanges")]
    public IReadOnlyList<string> IpRanges { get; init; } = [];

    /// <summary>
    /// SHA-256 hashes of the accepted API keys as 64 lowercase hexadecimal characters, at most
    /// <see cref="VisitorAccessValidator.MaxApiKeys"/>.
    /// </summary>
    [JsonPropertyName("apiKeyHashes")]
    public IReadOnlyList<string> ApiKeyHashes { get; init; } = [];

    /// <summary>
    /// Checks the declaration against the shared rules (<see cref="VisitorAccessValidator"/>) and that every API
    /// key hash is 64 lowercase hexadecimal characters.
    /// </summary>
    /// <param name="error">A message naming the first problem; never repeats an API key hash.</param>
    public bool TryValidate([NotNullWhen(false)] out string? error)
    {
        var hashes = ApiKeyHashes ?? [];
        foreach (var hash in hashes)
        {
            if (!IsLowercaseHash(hash))
            {
                error = "API key hashes must be 64 lowercase hexadecimal characters (SHA-256).";
                return false;
            }
        }

        return VisitorAccessValidator.TryValidate(Default, Rules, IpRanges, hashes.Count, out error);
    }

    /// <summary>The header value: base64url (unpadded) of the declaration's UTF-8 JSON.</summary>
    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this, s_json));

    /// <summary>
    /// Reads a <see cref="GroundhogHeaders.VisitorAccess"/> value and validates it with <see cref="TryValidate"/>.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <param name="declaration">The declaration, when it's well formed and valid.</param>
    /// <param name="error">Why it was refused: a message safe to send back to the agent.</param>
    public static bool TryDecode(
        string? value,
        [NotNullWhen(true)] out VisitorAccessDeclaration? declaration,
        [NotNullWhen(false)] out string? error)
    {
        declaration = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxEncodedLength)
        {
            error = $"The visitor access declaration must be 1 to {MaxEncodedLength} characters.";
            return false;
        }

        if (!Base64Url.IsValid(value, out var length))
        {
            error = "The visitor access declaration isn't base64url.";
            return false;
        }

        var bytes = new byte[length];
        Base64Url.DecodeFromChars(value, bytes);
        VisitorAccessDeclaration? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<VisitorAccessDeclaration>(bytes, s_json);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null)
        {
            error = "The visitor access declaration isn't a JSON object of the expected shape.";
            return false;
        }

        if (!parsed.TryValidate(out error))
        {
            return false;
        }

        declaration = parsed;
        return true;
    }

    /// <summary>Lists the rules without API key hashes.</summary>
    public override string ToString() =>
        $"default: {string.Join(",", Default?.Allow ?? [])}; {Rules?.Count ?? 0} path rule(s), "
        + $"{IpRanges?.Count ?? 0} IP range(s), {ApiKeyHashes?.Count ?? 0} API key(s)";

    private static bool IsLowercaseHash(string? hash)
    {
        if (hash is null || hash.Length != ConnectionKey.HashLength)
        {
            return false;
        }

        foreach (var c in hash)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The rules every visitor access policy follows, shared by the relay, the agent and the CLI so bad input fails
/// before it's sent.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Every <c>allow</c> list has at least one of <see cref="VisitorAccessMethods"/>, without repeats;
/// <c>anyone</c> stands alone.</description></item>
/// <item><description><c>apikeys</c> needs at least one API key and <c>ips</c> at least one IP range.</description></item>
/// <item><description>Paths start with <c>/</c>, have no <c>?</c>, <c>#</c>, <c>%</c>, <c>\</c>, whitespace, control
/// characters or <c>.</c>/<c>..</c> segments, aren't <c>/</c> itself, are at most <see cref="MaxPathLength"/>
/// characters, and appear once (ignoring case and trailing or repeated <c>/</c>).</description></item>
/// <item><description>IP ranges are CIDR as <see cref="IPNetwork.TryParse(string, out IPNetwork)"/> reads them, for
/// example <c>203.0.113.0/24</c> or <c>2001:db8::/32</c>.</description></item>
/// <item><description>At most <see cref="MaxRules"/> rules, <see cref="MaxIpRanges"/> IP ranges and
/// <see cref="MaxApiKeys"/> API keys.</description></item>
/// </list>
/// </remarks>
public static class VisitorAccessValidator
{
    /// <summary>Most path rules a tunnel can have.</summary>
    public const int MaxRules = 32;

    /// <summary>Most IP ranges a tunnel can have.</summary>
    public const int MaxIpRanges = 64;

    /// <summary>Most API keys a tunnel can have.</summary>
    public const int MaxApiKeys = 10;

    /// <summary>Longest path in a rule, in characters.</summary>
    public const int MaxPathLength = 256;

    /// <summary>Checks a policy's rules.</summary>
    /// <param name="defaultRule">The default rule. Required.</param>
    /// <param name="rules">Path rules; <see langword="null"/> means none.</param>
    /// <param name="ipRanges">IP ranges; <see langword="null"/> means none.</param>
    /// <param name="apiKeyCount">
    /// How many API keys the tunnel has, or <see langword="null"/> when unknown (a management client that doesn't
    /// hold the API key list), which skips the API key checks.
    /// </param>
    /// <param name="error">A message naming the first problem.</param>
    public static bool TryValidate(
        VisitorAccessRule? defaultRule,
        IReadOnlyList<VisitorAccessPathRule>? rules,
        IReadOnlyList<string>? ipRanges,
        int? apiKeyCount,
        [NotNullWhen(false)] out string? error)
    {
        rules ??= [];
        ipRanges ??= [];
        if (rules.Count > MaxRules)
        {
            error = $"A tunnel can have at most {MaxRules} path rules.";
            return false;
        }

        if (ipRanges.Count > MaxIpRanges)
        {
            error = $"A tunnel can have at most {MaxIpRanges} IP ranges.";
            return false;
        }

        if (apiKeyCount > MaxApiKeys)
        {
            error = $"A tunnel can have at most {MaxApiKeys} API keys.";
            return false;
        }

        for (var i = 0; i < ipRanges.Count; i++)
        {
            if (!IsIpRange(ipRanges[i]))
            {
                error = $"IP range {i + 1} isn't in CIDR form, for example 203.0.113.0/24 or 2001:db8::/32.";
                return false;
            }
        }

        if (defaultRule is null)
        {
            error = "The default rule is required.";
            return false;
        }

        if (!TryValidateAllow(defaultRule.Allow, ipRanges.Count, apiKeyCount, out var allowError))
        {
            error = $"Default rule: {allowError}";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                error = "A path rule is empty.";
                return false;
            }

            if (!TryValidatePath(rule.Path, out var pathError))
            {
                error = pathError;
                return false;
            }

            if (!seen.Add(NormalizePath(rule.Path)))
            {
                error = $"The path {rule.Path} has more than one rule.";
                return false;
            }

            if (!TryValidateAllow(rule.Allow, ipRanges.Count, apiKeyCount, out allowError))
            {
                error = $"Rule for {rule.Path}: {allowError}";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>Checks one rule's path prefix.</summary>
    public static bool TryValidatePath(string? path, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            error = "Rule paths must start with /, for example /webhooks.";
            return false;
        }

        if (path.Length > MaxPathLength)
        {
            error = $"Rule paths must be at most {MaxPathLength} characters.";
            return false;
        }

        foreach (var c in path)
        {
            if (c is '?' or '#')
            {
                error = $"The rule path {path} can't have a query (?) or fragment (#).";
                return false;
            }

            if (char.IsControl(c))
            {
                error = "Rule paths can't contain control characters.";
                return false;
            }

            if (c is '%' or '\\' || char.IsWhiteSpace(c))
            {
                error = $"The rule path {path} must be a plain path: no spaces, backslashes or % escapes.";
                return false;
            }
        }

        var normalized = NormalizePath(path);
        if (normalized == "/")
        {
            error = "A rule for / is the default rule; set the default instead.";
            return false;
        }

        if (normalized.Split('/').Any(segment => segment is "." or ".."))
        {
            error = $"The rule path {path} can't contain . or .. segments.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// The form a rule path is matched and compared in for duplicates: lowercase, without a trailing or repeated
    /// <c>/</c>. The relay normalizes the same way.
    /// </summary>
    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return "/" + string.Join('/', path.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Whether <paramref name="value"/> is an IPv4 or IPv6 range in CIDR form.</summary>
    public static bool IsIpRange(string? value) =>
        !string.IsNullOrEmpty(value) && value.Contains('/', StringComparison.Ordinal) && IPNetwork.TryParse(value, out _);

    private static bool TryValidateAllow(
        IReadOnlyList<string>? allow,
        int ipRangeCount,
        int? apiKeyCount,
        [NotNullWhen(false)] out string? error)
    {
        if (allow is null || allow.Count == 0)
        {
            error = "allow needs at least one of anyone, members, apikeys or ips.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in allow)
        {
            if (!VisitorAccessMethods.IsKnown(method))
            {
                error = "allow values must be anyone, members, apikeys or ips.";
                return false;
            }

            if (!seen.Add(method))
            {
                error = $"{method} is listed twice.";
                return false;
            }
        }

        if (seen.Contains(VisitorAccessMethods.Anyone) && seen.Count > 1)
        {
            error = "anyone can't be combined with other methods.";
            return false;
        }

        if (seen.Contains(VisitorAccessMethods.ApiKeys) && apiKeyCount == 0)
        {
            error = "apikeys needs at least one API key.";
            return false;
        }

        if (seen.Contains(VisitorAccessMethods.Ips) && ipRangeCount == 0)
        {
            error = "ips needs at least one IP range.";
            return false;
        }

        error = null;
        return true;
    }

}

/// <summary>
/// API keys: secrets a caller presents in <see cref="GroundhogHeaders.ApiKey"/> to reach a path whose rule allows
/// <see cref="VisitorAccessMethods.ApiKeys"/>. They're separate from the tunnel's connection key (the agent's own
/// credential) but have the same shape (32 random bytes as 43 base64url characters), and only their SHA-256 hash
/// (64 lowercase hexadecimal characters) is ever stored or declared.
/// </summary>
public static class ApiKey
{
    /// <summary>Characters in an API key.</summary>
    public const int Length = ConnectionKey.Length;

    /// <summary>Creates a new API key from a cryptographically secure random source.</summary>
    public static string Generate() => ConnectionKey.Generate();

    /// <summary>Whether <paramref name="apiKey"/> is 43 base64url characters that decode to 32 bytes.</summary>
    public static bool IsWellFormed(string? apiKey) => ConnectionKey.IsWellFormed(apiKey);

    /// <summary>Lowercase hex SHA-256 of the API key's UTF-8 text: the value sent in a declaration.</summary>
    public static string Hash(string apiKey) => ConnectionKey.Hash(apiKey);
}
