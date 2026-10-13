using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Groundhog.Agent;
using Groundhog.Protocol;

namespace Groundhog.Cli;

/// <summary>An argument problem whose message is safe to print as is: it never repeats a secret.</summary>
internal sealed class UsageException(string message) : ArgumentException(message);

/// <summary>
/// The visitor access options as typed (<c>--allow</c>, <c>--path</c>, <c>--ip</c>, <c>--api-key-env</c>,
/// <c>--access-file</c>), so a saved profile can replay them. Holds environment variable names, never API key values.
/// </summary>
internal sealed record VisitorAccessFlags
{
    /// <summary>The options this record reads; <c>--path</c>, <c>--ip</c> and <c>--api-key-env</c> repeat.</summary>
    internal static readonly string[] OptionNames = ["--allow", "--path", "--ip", "--api-key-env", "--access-file"];

    /// <summary><c>--allow</c>: the default rule, for example <c>members</c> or <c>apikeys,ips</c>.</summary>
    public string? Allow { get; init; }

    /// <summary><c>--path</c> values: <c>&lt;prefix&gt;=&lt;allow,...&gt;</c>.</summary>
    public string[] Paths { get; init; } = [];

    /// <summary><c>--ip</c> values: CIDR ranges.</summary>
    public string[] Ips { get; init; } = [];

    /// <summary><c>--api-key-env</c> values: names of environment variables holding API keys.</summary>
    public string[] ApiKeyVariables { get; init; } = [];

    /// <summary><c>--access-file</c>: a JSON file with the rules.</summary>
    public string? AccessFile { get; init; }

    [JsonIgnore]
    internal bool HasRuleFlags => Allow is not null || Paths.Length > 0 || Ips.Length > 0;

    [JsonIgnore]
    internal bool IsEmpty => !HasRuleFlags && ApiKeyVariables.Length == 0 && AccessFile is null;

    /// <summary>
    /// Takes the visitor access options out of <paramref name="args"/>. <paramref name="flags"/> is null when there
    /// are none. Messages never repeat a value.
    /// </summary>
    internal static bool TryExtract(
        IReadOnlyList<string> args,
        out VisitorAccessFlags? flags,
        out string[] remaining,
        [NotNullWhen(false)] out string? error)
    {
        string? allow = null;
        string? file = null;
        var paths = new List<string>();
        var ips = new List<string>();
        var variables = new List<string>();
        var rest = new List<string>();
        flags = null;
        remaining = [];
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inline = null;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=', StringComparison.Ordinal) is var equals and > 0)
            {
                inline = arg[(equals + 1)..];
                arg = arg[..equals];
            }

            if (!OptionNames.Contains(arg, StringComparer.Ordinal))
            {
                rest.Add(args[i]);
                continue;
            }

            var value = inline ?? (i + 1 < args.Count ? args[++i] : null);
            if (string.IsNullOrWhiteSpace(value) || (inline is null && value.StartsWith("--", StringComparison.Ordinal)))
            {
                error = $"{arg} needs a value.";
                return false;
            }

            switch (arg)
            {
                case "--allow" when allow is not null:
                    error = "Give --allow once; list several methods with commas, for example --allow apikeys,ips.";
                    return false;
                case "--allow":
                    allow = value;
                    break;
                case "--access-file" when file is not null:
                    error = "Give --access-file once.";
                    return false;
                case "--access-file":
                    file = value;
                    break;
                case "--path":
                    paths.Add(value);
                    break;
                case "--ip":
                    ips.Add(value);
                    break;
                default:
                    variables.Add(value);
                    break;
            }
        }

        var parsed = new VisitorAccessFlags
        {
            Allow = allow,
            Paths = [.. paths],
            Ips = [.. ips],
            ApiKeyVariables = [.. variables],
            AccessFile = file,
        };
        if (parsed.AccessFile is not null && parsed.HasRuleFlags)
        {
            error = "Choose --access-file or --allow, --path and --ip, not both.";
            return false;
        }

        remaining = [.. rest];
        flags = parsed.IsEmpty ? null : parsed;
        error = null;
        return true;
    }

    /// <summary>The options again, for replaying a saved profile.</summary>
    internal IEnumerable<string> ToArguments()
    {
        if (Allow is not null)
        {
            yield return "--allow";
            yield return Allow;
        }

        foreach (var path in Paths)
        {
            yield return "--path";
            yield return path;
        }

        foreach (var ip in Ips)
        {
            yield return "--ip";
            yield return ip;
        }

        foreach (var variable in ApiKeyVariables)
        {
            yield return "--api-key-env";
            yield return variable;
        }

        if (AccessFile is not null)
        {
            yield return "--access-file";
            yield return AccessFile;
        }
    }

    /// <summary>A short summary for tables, without API key values.</summary>
    internal string Describe()
    {
        var parts = new List<string>();
        if (AccessFile is not null)
        {
            parts.Add($"file {Path.GetFileName(AccessFile)}");
        }
        else
        {
            parts.Add(Allow ?? VisitorAccessMethods.Anyone);
        }

        if (Paths.Length > 0)
        {
            parts.Add($"{Paths.Length} path rule(s)");
        }

        if (Ips.Length > 0)
        {
            parts.Add($"{Ips.Length} IP range(s)");
        }

        if (ApiKeyVariables.Length > 0)
        {
            parts.Add($"{ApiKeyVariables.Length} API key(s)");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The rules from the file or the options, not yet validated. API key hashes come only from a file;
    /// the environment's API keys are added by <see cref="TryBuildAgentAccess"/>.
    /// </summary>
    internal bool TryReadRules(
        [NotNullWhen(true)] out VisitorAccessDeclaration? rules,
        [NotNullWhen(false)] out string? error)
    {
        rules = null;
        if (AccessFile is not null)
        {
            return TryReadFile(AccessFile, out rules, out error);
        }

        var defaultRule = VisitorAccessRule.Anyone;
        if (Allow is not null)
        {
            if (!TryParseAllow(Allow, "--allow", out var allow, out error))
            {
                return false;
            }

            defaultRule = new(allow);
        }

        var pathRules = new List<VisitorAccessPathRule>();
        foreach (var path in Paths)
        {
            var separator = path.LastIndexOf('=');
            if (separator <= 0)
            {
                error = "--path needs <prefix>=<methods>, for example --path /webhooks=anyone.";
                return false;
            }

            if (!TryParseAllow(path[(separator + 1)..], "--path", out var allow, out error))
            {
                return false;
            }

            pathRules.Add(new(path[..separator], allow));
        }

        rules = new VisitorAccessDeclaration { Default = defaultRule, Rules = pathRules, IpRanges = [.. Ips] };
        error = null;
        return true;
    }

    /// <summary>
    /// What <c>groundhog host</c> declares: the rules plus each <c>--api-key-env</c> variable's API key, checked
    /// with the shared validator.
    /// </summary>
    internal bool TryBuildAgentAccess(
        Func<string, string?> getEnvironmentVariable,
        [NotNullWhen(true)] out GroundhogVisitorAccess? access,
        [NotNullWhen(false)] out string? error)
    {
        access = null;
        if (!TryReadRules(out var rules, out error))
        {
            return false;
        }

        if (rules.ApiKeyHashes.Count > 0)
        {
            error = "Access files can't list API key hashes; give API keys with --api-key-env.";
            return false;
        }

        var apiKeys = new List<string>();
        foreach (var variable in ApiKeyVariables)
        {
            var apiKey = getEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(apiKey))
            {
                error = $"The environment variable {variable} (from --api-key-env) isn't set.";
                return false;
            }

            if (!ApiKey.IsWellFormed(apiKey.Trim()))
            {
                error = $"The environment variable {variable} doesn't hold an API key ({ApiKey.Length} base64url "
                    + "characters, as groundhog api-key create prints).";
                return false;
            }

            apiKeys.Add(apiKey.Trim());
        }

        var candidate = new GroundhogVisitorAccess
        {
            Allow = rules.Default?.Allow ?? [],
            Rules = rules.Rules ?? [],
            IpRanges = rules.IpRanges ?? [],
            ApiKeys = apiKeys,
        };
        try
        {
            _ = candidate.ToDeclaration();
        }
        catch (ArgumentException ex)
        {
            error = ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty);
            return false;
        }

        access = candidate;
        return true;
    }

    private static bool TryParseAllow(
        string value,
        string option,
        [NotNullWhen(true)] out string[]? allow,
        [NotNullWhen(false)] out string? error)
    {
        allow = [.. value.Split(',', StringSplitOptions.TrimEntries).Select(method => method.ToLowerInvariant())];
        if (allow.Any(string.IsNullOrEmpty))
        {
            allow = null;
            error = $"{option} takes methods separated by commas: anyone, members, apikeys, ips.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadFile(
        string file,
        [NotNullWhen(true)] out VisitorAccessDeclaration? rules,
        [NotNullWhen(false)] out string? error)
    {
        rules = null;
        try
        {
            using var stream = File.OpenRead(file);
            rules = JsonSerializer.Deserialize<VisitorAccessDeclaration>(stream, JsonSerializerOptions.Web);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "The --access-file can't be read.";
            return false;
        }
        catch (JsonException)
        {
            rules = null;
        }

        if (rules is null)
        {
            error = "The --access-file must be a JSON object such as {\"default\":{\"allow\":[\"members\"]},\"rules\":[...],\"ipRanges\":[...]}.";
            return false;
        }

        error = null;
        return true;
    }
}
