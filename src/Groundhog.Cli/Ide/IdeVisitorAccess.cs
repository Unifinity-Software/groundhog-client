using System.Net;
using System.Text.Json;
using Groundhog.Protocol;

namespace Groundhog.Cli.Ide;

internal sealed partial class IdeServices
{
    private async Task<object> VisitorAccessMethodAsync(string method, JsonElement p, ManagementClient client,
        string workspace, string name, CancellationToken ct)
    {
        var path = ManagementClient.TunnelPath(workspace, name);
        try
        {
            if (method == "visitorAccess/get")
            { return await client.SendAsync<VisitorAccessInfo>(HttpMethod.Get, path + "/visitor-access", null, ct); }
            Confirm(p);
            if (method == "visitorAccess/saveKey")
            {
                var project = Required(p, "project"); var parameter = Required(p, "parameter"); var key = Required(p, "key");
                if (!Path.IsPathFullyQualified(project) || !ApiKey.IsWellFormed(key))
                { throw Fault(-32602, "invalidParams", "Choose an absolute project path and a valid visitor API key."); }
                var options = Options.Parse(["--project", project, "--key-parameter", parameter]);
                var secrets = ProjectSecrets.FromOptions(true, options, ProjectSecrets.DefaultSecretsPath)!;
                secrets.Write(name, key, TextWriter.Null);
                return Empty;
            }
            if (method == "visitorAccess/createKey")
            {
                var label = Required(p, "label");
                if (label.Length > 80 || label.Any(char.IsControl)) { throw Fault(-32602, "invalidParams", "API key label must contain 1 to 80 printable characters."); }
                // An explicit exception to ordinary secret-free projections: this value is shown once, on creation.
                var created = await client.SendAsync<ApiKeyCreated>(HttpMethod.Post, path + "/api-keys", new { Name = label }, ct);
                await Notify("tunnels/changed", new { workspace });
                return created;
            }
            if (method == "visitorAccess/deleteKey")
            {
                var id = Required(p, "id");
                if (id.Length != 32 || !id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
                { throw Fault(-32602, "invalidParams", "Invalid API key ID."); }
                await client.SendAsync<object>(HttpMethod.Delete, path + "/api-keys/" + Uri.EscapeDataString(id), null, ct);
                await Notify("tunnels/changed", new { workspace });
                return Empty;
            }
            VisitorAccessRule defaultRule; VisitorAccessPathRule[] rules; string[] ipRanges; string managedBy;
            if (method == "visitorAccess/handBack")
            {
                var access = await client.SendAsync<VisitorAccessInfo>(HttpMethod.Get, path + "/visitor-access", null, ct);
                defaultRule = access.Default ?? VisitorAccessRule.Anyone; rules = access.Rules ?? []; ipRanges = access.IpRanges ?? [];
                managedBy = "agent";
            }
            else if (method == "visitorAccess/set")
            {
                defaultRule = p.GetProperty("default").Deserialize<VisitorAccessRule>(JsonSerializerOptions.Web)
                    ?? throw Fault(-32602, "invalidParams", "Give a default visitor access rule.");
                rules = p.TryGetProperty("rules", out var paths) ? paths.Deserialize<VisitorAccessPathRule[]>(JsonSerializerOptions.Web) ?? [] : [];
                ipRanges = p.TryGetProperty("ipRanges", out var ranges) ? ranges.Deserialize<string[]>() ?? [] : [];
                if (!VisitorAccessValidator.TryValidate(defaultRule, rules, ipRanges, apiKeyCount: null, out _))
                { throw Fault(-32602, "invalidParams", "The visitor access policy is invalid."); }
                managedBy = "dashboard";
            }
            else { throw Fault(-32601, "methodNotFound", "Method not found."); }
            await client.SendAsync<JsonElement>(HttpMethod.Put, path + "/visitor-access",
                new { Default = defaultRule, Rules = rules, IpRanges = ipRanges, ManagedBy = managedBy }, ct);
            await Notify("tunnels/changed", new { workspace });
            return await client.SendAsync<VisitorAccessInfo>(HttpMethod.Get, path + "/visitor-access", null, ct);
        }
        catch (ManagementException ex) when (ex.Status == HttpStatusCode.NotFound)
        { throw Fault(-32014, "featureNotEnabled", "Visitor access rules are not enabled for this workspace."); }
    }
}
