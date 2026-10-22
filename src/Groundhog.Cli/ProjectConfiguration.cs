using System.Text.Json;
using System.Text.Json.Serialization;
using Groundhog.Protocol;

namespace Groundhog.Cli;

/// <summary>A checked-in project setup; contains environment variable names, never credential values.</summary>
internal sealed class ProjectConfiguration
{
    internal const string SchemaUrl = "https://raw.githubusercontent.com/Unifinity-Software/groundhog-client/master/schemas/groundhog.schema.json";
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }
    public required string Tunnel { get; init; }
    public required string Target { get; init; }
    public string[]? Allow { get; init; }
    public VisitorAccessPathRule[] Rules { get; init; } = [];
    public string[] IpRanges { get; init; } = [];
    public string[] ApiKeyEnv { get; init; } = [];

    internal TunnelProfile ToProfile()
    {
        if (Rules is null || IpRanges is null || ApiKeyEnv is null
            || ApiKeyEnv.Any(name => string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
                || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new UsageException("groundhog.json apiKeyEnv must contain environment variable names; collections cannot be null.");
        }
        var access = Allow is null && Rules.Length == 0 && IpRanges.Length == 0 && ApiKeyEnv.Length == 0 ? null : new VisitorAccessFlags
        {
            Allow = Allow is null ? null : string.Join(',', Allow),
            Paths = Rules.Select(r => r is null || r.Allow is null ? throw new UsageException("groundhog.json has an invalid path rule.")
                : $"{r.Path}={string.Join(',', r.Allow)}").ToArray(),
            Ips = IpRanges,
            ApiKeyVariables = ApiKeyEnv,
        };
        var profile = new TunnelProfile(Tunnel, Target, Access: access);
        ProfileStore.Validate(profile);
        return profile;
    }

    /// <summary>Finds the nearest project file, without changing process-wide working-directory or environment state.</summary>
    internal static (string Path, TunnelProfile Profile)? Find(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "groundhog.json");
            if (!File.Exists(path)) { continue; }
            try
            {
                using var stream = File.OpenRead(path);
                var configuration = JsonSerializer.Deserialize<ProjectConfiguration>(stream, s_json)
                    ?? throw new UsageException("groundhog.json must be a JSON object.");
                return (path, configuration.ToProfile());
            }
            catch (JsonException)
            {
                throw new UsageException("groundhog.json has invalid JSON, missing fields or unsupported fields. Only tunnel, target, allow, rules, ipRanges, apiKeyEnv and $schema are supported; never put keys in it.");
            }
        }
        return null;
    }

    internal static string[] ResolveHostArguments(string[] args, ProfileStore store, string directory)
    {
        if (args.Any(a => a is "--help" or "-h")) { return args; }
        var project = Find(directory)?.Profile;
        if (!VisitorAccessFlags.TryExtract(args, out var cliAccess, out var rest, out var error))
        {
            throw new UsageException(error);
        }
        // Values of options are never mistaken for a tunnel name.
        var names = new List<string>();
        for (var i = 0; i < rest.Length; i++)
        {
            if (!rest[i].StartsWith('-')) { names.Add(rest[i]); }
            else if (!rest[i].Contains('=') && rest[i] is not ("--verbose" or "-v" or "--insecure-local")) { i++; }
        }
        if (names.Count > 1) { return args; }
        var name = names.SingleOrDefault() ?? project?.Name;
        if (name is null) { return args; }
        var profile = store.Read().SingleOrDefault(p => p.Name == name);
        var defaults = project?.Name == name ? project : null;
        var access = MergeAccess(MergeAccess(defaults?.Access, profile?.Access), cliAccess);
        var resolved = new List<string>();
        if (names.Count == 0) { resolved.Add(name); }
        var hasTarget = rest.Any(a => a is "--target" or "--port" || a.StartsWith("--target=", StringComparison.Ordinal) || a.StartsWith("--port=", StringComparison.Ordinal));
        if (!hasTarget && (profile?.Target ?? defaults?.Target) is { } target) { resolved.AddRange(["--target", target]); }
        if (!rest.Any(a => a == "--relay" || a.StartsWith("--relay=", StringComparison.Ordinal)) && profile?.Relay is { } relay)
        { resolved.AddRange(["--relay", relay]); }
        resolved.AddRange(rest);
        resolved.AddRange(access?.ToArguments() ?? []);
        return [.. resolved];
    }

    private static VisitorAccessFlags? MergeAccess(VisitorAccessFlags? defaults, VisitorAccessFlags? overrides)
    {
        if (overrides is null) { return defaults; }
        if (defaults is null) { return overrides; }
        var replaceRules = overrides.AccessFile is not null || defaults.AccessFile is not null && overrides.HasRuleFlags;
        return new()
        {
            Allow = overrides.Allow ?? (replaceRules ? null : defaults.Allow),
            Paths = overrides.Paths.Length > 0 ? overrides.Paths : replaceRules ? [] : defaults.Paths,
            Ips = overrides.Ips.Length > 0 ? overrides.Ips : replaceRules ? [] : defaults.Ips,
            ApiKeyVariables = overrides.ApiKeyVariables.Length > 0 ? overrides.ApiKeyVariables : defaults.ApiKeyVariables,
            AccessFile = overrides.AccessFile ?? (overrides.HasRuleFlags ? null : defaults.AccessFile),
        };
    }
}
