using System.Net;
using System.Text.Json;
using Groundhog.Protocol;
using Spectre.Console;

namespace Groundhog.Cli;

/// <summary>An API key as the management API lists it: never the key or its hash.</summary>
internal sealed record ApiKeyInfo(string Id, string Name, DateTimeOffset Created);

/// <summary>The stored visitor access policy, as <c>GET …/visitor-access</c> returns it.</summary>
internal sealed record VisitorAccessInfo(
    VisitorAccessRule? Default,
    VisitorAccessPathRule[]? Rules,
    string[]? IpRanges,
    ApiKeyInfo[]? ApiKeys,
    string? ManagedBy);

/// <summary>A new API key; <see cref="Key"/> is shown once.</summary>
internal sealed record ApiKeyCreated(string Id, string Name, DateTimeOffset Created, string Key)
{
    public override string ToString() => $"API key {Name} ({Id}, value hidden)";
}

/// <summary>
/// <c>groundhog access</c> and <c>groundhog api-key</c>: a tunnel's visitor access rules and API keys through the
/// management API.
/// </summary>
internal static class VisitorAccessCommands
{
    internal const string NotEnabled = "Visitor access rules aren't enabled for this workspace.";
    internal const string ManagedByAgent = "agent";
    internal const string ManagedByDashboard = "dashboard";

    /// <summary>Checks the positional arguments and returns the tunnel name.</summary>
    internal static string TunnelName(string command, string[] positional, Options options, VisitorAccessFlags? flags)
    {
        if (command == "access")
        {
            switch (positional)
            {
                case [var name] when flags is null:
                    return name;
                case [_] when flags is not null:
                    throw new UsageException("Set rules with: groundhog access set <name> --allow <methods> [--path <prefix>=<methods>] [--ip <cidr>].");
                case ["set", var name] when !options.Has("--agent-managed"):
                    if (flags is null || (!flags.HasRuleFlags && flags.AccessFile is null))
                    {
                        throw new UsageException("Give --allow, --path, --ip or --access-file.");
                    }

                    if (flags.ApiKeyVariables.Length > 0)
                    {
                        throw new UsageException("API keys for saved rules are created with: groundhog api-key create <name> <label>.");
                    }

                    return name;
                default:
                    throw new UsageException("Use access <name>, access set <name> <rules> or access <name> --agent-managed.");
            }
        }

        return positional switch
        {
            ["create", _, _] or ["list", _] or ["revoke", _, _] => positional[1],
            _ => throw new UsageException("Use api-key create <name> <label>, api-key list <name> or api-key revoke <name> <id>."),
        };
    }

    internal static async Task<int> RunAsync(string command, string[] positional, Options options, VisitorAccessFlags? flags,
        ManagementClient client, string workspace, TextWriter output, CancellationToken cancellation)
    {
        var name = TunnelName(command, positional, options, flags);
        var path = ManagementClient.TunnelPath(workspace, name);
        var json = options.Get("--output") == "json";
        try
        {
            if (command == "access")
            {
                return positional is ["set", _]
                    ? await SetAsync(client, path, name, flags!, output, cancellation)
                    : options.Has("--agent-managed")
                        ? await HandBackAsync(client, path, name, output, cancellation)
                        : await ShowAsync(client, path, name, json, output, cancellation);
            }

            switch (positional[0])
            {
                case "create":
                    var created = await client.SendAsync<ApiKeyCreated>(HttpMethod.Post, path + "/api-keys",
                        new { Name = positional[2] }, cancellation);
                    if (json)
                    {
                        output.WriteLine(JsonSerializer.Serialize(created, JsonSerializerOptions.Web));
                    }
                    else
                    {
                        output.WriteLine($"API key {Plain(created.Name)} created (id {Plain(created.Id)}). Copy it now; it isn't shown again:");
                        output.WriteLine(Plain(created.Key));
                        output.WriteLine($"Callers send it in the {GroundhogHeaders.ApiKey} header to paths that allow apikeys.");
                    }

                    return 0;
                case "list":
                    var access = await client.SendAsync<VisitorAccessInfo>(HttpMethod.Get, path + "/visitor-access", null, cancellation);
                    var apiKeys = access.ApiKeys ?? [];
                    if (json)
                    {
                        output.WriteLine(JsonSerializer.Serialize(apiKeys, JsonSerializerOptions.Web));
                    }
                    else
                    {
                        UtilityCommands.Render(output, ApiKeys(apiKeys));
                    }

                    return 0;
                default:
                    await client.SendAsync<object>(HttpMethod.Delete,
                        $"{path}/api-keys/{Uri.EscapeDataString(positional[2])}", null, cancellation);
                    output.WriteLine("API key revoked.");
                    return 0;
            }
        }
        catch (ManagementException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            throw new ManagementException(NotEnabled, HttpStatusCode.NotFound);
        }
    }

    private static async Task<int> ShowAsync(ManagementClient client, string path, string name, bool json, TextWriter output,
        CancellationToken cancellation)
    {
        var access = await client.SendAsync<VisitorAccessInfo>(HttpMethod.Get, path + "/visitor-access", null, cancellation);
        if (json)
        {
            // The relay's own shape, so the output can be edited into an --access-file.
            output.WriteLine(JsonSerializer.Serialize(access, JsonSerializerOptions.Web));
            return 0;
        }

        UtilityCommands.Render(output, new Panel(Details(access)).Header($"Visitor access · {TerminalViews.Safe(name)}").RoundedBorder());
        if (access.ManagedBy == ManagedByDashboard)
        {
            output.WriteLine($"Agents can't change these rules. Hand them back with: groundhog access {Plain(name)} --agent-managed");
        }

        return 0;
    }

    private static async Task<int> SetAsync(ManagementClient client, string path, string name, VisitorAccessFlags flags,
        TextWriter output, CancellationToken cancellation)
    {
        if (!flags.TryReadRules(out var rules, out var error))
        {
            throw new UsageException(error);
        }

        if (rules.ApiKeyHashes.Count > 0)
        {
            throw new UsageException("Saved rules can't list API key hashes; create API keys with: groundhog api-key create <name> <label>.");
        }

        // The relay holds the API keys, so the API key checks are left to it.
        if (!VisitorAccessValidator.TryValidate(rules.Default, rules.Rules, rules.IpRanges, apiKeyCount: null, out error))
        {
            throw new UsageException(error);
        }

        await PutAsync(client, path, rules.Default, rules.Rules, rules.IpRanges, ManagedByDashboard, cancellation);
        output.WriteLine($"Visitor access rules saved for {Plain(name)}. Agents can no longer change them; hand them back with: "
            + $"groundhog access {Plain(name)} --agent-managed");
        return 0;
    }

    private static async Task<int> HandBackAsync(ManagementClient client, string path, string name, TextWriter output,
        CancellationToken cancellation)
    {
        var access = await client.SendAsync<VisitorAccessInfo>(HttpMethod.Get, path + "/visitor-access", null, cancellation);
        await PutAsync(client, path, access.Default ?? VisitorAccessRule.Anyone, access.Rules ?? [], access.IpRanges ?? [],
            ManagedByAgent, cancellation);
        output.WriteLine($"The agent now manages visitor access for {Plain(name)}: rules it declares replace the current ones.");
        return 0;
    }

    private static Task<JsonElement> PutAsync(ManagementClient client, string path, VisitorAccessRule defaultRule,
        IReadOnlyList<VisitorAccessPathRule> rules, IReadOnlyList<string> ipRanges, string managedBy, CancellationToken cancellation) =>
        client.SendAsync<JsonElement>(HttpMethod.Put, path + "/visitor-access",
            new { Default = defaultRule, Rules = rules, IpRanges = ipRanges, ManagedBy = managedBy }, cancellation);

    /// <summary>The policy as a two-column table; also used by the interactive tunnel screen.</summary>
    internal static Table Details(VisitorAccessInfo access)
    {
        var table = new Table().Border(TableBorder.None).HideHeaders().AddColumn("Field").AddColumn("Value");
        table.AddRow("Managed by", access.ManagedBy == ManagedByDashboard ? "Dashboard" : "Agent");
        table.AddRow("Default", TerminalViews.Safe(Methods(access.Default?.Allow)));
        foreach (var rule in (access.Rules ?? []).OrderBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(TerminalViews.Safe(rule.Path), TerminalViews.Safe(Methods(rule.Allow)));
        }

        table.AddRow("IP ranges", TerminalViews.Safe(access.IpRanges is { Length: > 0 } ranges ? string.Join(", ", ranges) : "None"));
        table.AddRow("API keys", TerminalViews.Safe(access.ApiKeys is { Length: > 0 } apiKeys
            ? string.Join(", ", apiKeys.Select(apiKey => apiKey.Name))
            : "None"));
        return table;
    }

    /// <summary>A one-line summary for narrow views, for example <c>members · 2 path rules</c>.</summary>
    internal static string Summary(VisitorAccessInfo access)
    {
        var rules = access.Rules?.Length ?? 0;
        return rules == 0 ? Methods(access.Default?.Allow) : $"{Methods(access.Default?.Allow)} · {rules} path rule{(rules == 1 ? "" : "s")}";
    }

    private static Table ApiKeys(ApiKeyInfo[] apiKeys)
    {
        var table = new Table().RoundedBorder().AddColumn("API key").AddColumn("ID").AddColumn("Created");
        foreach (var apiKey in apiKeys)
        {
            table.AddRow(TerminalViews.Safe(apiKey.Name), TerminalViews.Safe(apiKey.Id), apiKey.Created.ToString("u"));
        }

        return table;
    }

    private static string Methods(IReadOnlyList<string>? allow) =>
        allow is { Count: > 0 } ? string.Join(" or ", allow) : VisitorAccessMethods.Anyone;

    private static string Plain(string value) => new(value.Where(c => !char.IsControl(c)).ToArray());
}
