using System.Net;
using System.Text.Json;
using Groundhog.Protocol;
using Spectre.Console;

namespace Groundhog.Cli;

/// <summary>A visitor token as the management API lists it: never the token or its hash.</summary>
internal sealed record VisitorTokenInfo(string Id, string Name, DateTimeOffset Created);

/// <summary>The stored visitor access policy, as <c>GET …/visitor-access</c> returns it.</summary>
internal sealed record VisitorAccessInfo(
    VisitorAccessRule? Default,
    VisitorAccessPathRule[]? Rules,
    string[]? IpRanges,
    VisitorTokenInfo[]? Tokens,
    string? ManagedBy);

/// <summary>A new visitor token; <see cref="Token"/> is shown once.</summary>
internal sealed record VisitorTokenCreated(string Id, string Name, DateTimeOffset Created, string Token)
{
    public override string ToString() => $"Visitor token {Name} ({Id}, value hidden)";
}

/// <summary>
/// <c>groundhog access</c> and <c>groundhog token</c>: a tunnel's visitor access rules and visitor tokens through the
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

                    if (flags.TokenVariables.Length > 0)
                    {
                        throw new UsageException("Visitor tokens for saved rules are created with: groundhog token create <name> <label>.");
                    }

                    return name;
                default:
                    throw new UsageException("Use access <name>, access set <name> <rules> or access <name> --agent-managed.");
            }
        }

        return positional switch
        {
            ["create", _, _] or ["list", _] or ["revoke", _, _] => positional[1],
            _ => throw new UsageException("Use token create <name> <label>, token list <name> or token revoke <name> <id>."),
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
                    var created = await client.SendAsync<VisitorTokenCreated>(HttpMethod.Post, path + "/visitor-tokens",
                        new { Name = positional[2] }, cancellation);
                    if (json)
                    {
                        output.WriteLine(JsonSerializer.Serialize(created, JsonSerializerOptions.Web));
                    }
                    else
                    {
                        output.WriteLine($"Visitor token {Plain(created.Name)} created (id {Plain(created.Id)}). Copy it now; it isn't shown again:");
                        output.WriteLine(Plain(created.Token));
                        output.WriteLine($"Visitors send it in the {GroundhogHeaders.VisitorToken} header to paths that allow tokens.");
                    }

                    return 0;
                case "list":
                    var access = await client.SendAsync<VisitorAccessInfo>(HttpMethod.Get, path + "/visitor-access", null, cancellation);
                    var tokens = access.Tokens ?? [];
                    if (json)
                    {
                        output.WriteLine(JsonSerializer.Serialize(tokens, JsonSerializerOptions.Web));
                    }
                    else
                    {
                        UtilityCommands.Render(output, Tokens(tokens));
                    }

                    return 0;
                default:
                    await client.SendAsync<object>(HttpMethod.Delete,
                        $"{path}/visitor-tokens/{Uri.EscapeDataString(positional[2])}", null, cancellation);
                    output.WriteLine("Visitor token revoked.");
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

        if (rules.TokenHashes.Count > 0)
        {
            throw new UsageException("Saved rules can't list token hashes; create tokens with: groundhog token create <name> <label>.");
        }

        // The relay holds the tokens, so the token checks are left to it.
        if (!VisitorAccessValidator.TryValidate(rules.Default, rules.Rules, rules.IpRanges, tokenCount: null, out error))
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
        table.AddRow("Tokens", TerminalViews.Safe(access.Tokens is { Length: > 0 } tokens
            ? string.Join(", ", tokens.Select(token => token.Name))
            : "None"));
        return table;
    }

    /// <summary>A one-line summary for narrow views, for example <c>members · 2 path rules</c>.</summary>
    internal static string Summary(VisitorAccessInfo access)
    {
        var rules = access.Rules?.Length ?? 0;
        return rules == 0 ? Methods(access.Default?.Allow) : $"{Methods(access.Default?.Allow)} · {rules} path rule{(rules == 1 ? "" : "s")}";
    }

    private static Table Tokens(VisitorTokenInfo[] tokens)
    {
        var table = new Table().RoundedBorder().AddColumn("Token").AddColumn("ID").AddColumn("Created");
        foreach (var token in tokens)
        {
            table.AddRow(TerminalViews.Safe(token.Name), TerminalViews.Safe(token.Id), token.Created.ToString("u"));
        }

        return table;
    }

    private static string Methods(IReadOnlyList<string>? allow) =>
        allow is { Count: > 0 } ? string.Join(" or ", allow) : VisitorAccessMethods.Anyone;

    private static string Plain(string value) => new(value.Where(c => !char.IsControl(c)).ToArray());
}
