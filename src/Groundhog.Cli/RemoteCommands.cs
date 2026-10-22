using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Groundhog.Agent;
using Groundhog.Cli.Interactive;
using Spectre.Console;

namespace Groundhog.Cli;

internal static class RemoteCommands
{
    internal static readonly string[] Names = ["login", "logout", "whoami", "list", "ls", "describe", "show", "create", "rename", "delete", "workspace", "key", "open", "access", "api-key"];

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellation,
        CredentialStore? credentialStore = null, Func<string, string?, ManagementClient>? createClient = null,
        Func<string, string>? userSecretsPath = null)
    {
        if (args.Contains("--help", StringComparer.Ordinal)) { output.Write(GroundhogCommand.Usage); return 0; }
        var rest = args[1..];
        VisitorAccessFlags? accessFlags = null;
        if (args[0] == "access" && !VisitorAccessFlags.TryExtract(rest, out accessFlags, out rest, out var accessError))
        {
            throw new UsageException(accessError);
        }

        var options = Options.Parse(rest);
        if (args[0] == "requests")
        {
            options.RequireKnown("--remote", "--workspace", "--output", "--status", "--method", "--path", "--since", "--min-duration", "--limit", "--follow");
            if (options.Has("--follow") && options.Get("--output") == "json")
            {
                throw new ArgumentException("JSON output is a snapshot.");
            }
        }
        else if (args[0] == "stats") { options.RequireKnown("--remote", "--workspace", "--output"); }
        else if (args[0] == "access") { options.RequireKnown("--relay", "--workspace", "--output", "--agent-managed"); }
        else { options.RequireKnown("--relay", "--workspace", "--output", "--yes", "--status", "--project", "--name-parameter", "--key-parameter"); }
        if (options.Get("--output") is { } format && format != "json")
        {
            throw new ArgumentException("Unsupported output format.");
        }

        // Checked before anything changes on the service: a project without user secrets fails before create, key add or
        // key rotate.
        var keyCopy = args[0] == "key" && options.Positional is ["copy", ..];
        var projectSecrets = ProjectSecrets.FromOptions(
            args[0] == "create" || (args[0] == "key" && options.Positional is ["add" or "rotate" or "copy", ..]),
            options, userSecretsPath ?? ProjectSecrets.DefaultSecretsPath);
        if (keyCopy && projectSecrets is null)
        {
            throw new UsageException("key copy needs --project <path to AppHost.csproj>.");
        }

        var store = credentialStore ?? new CredentialStore(ProfileStore.DefaultDirectory);
        createClient ??= (relay, token) => new ManagementClient(relay, token);
        if (args[0] == "login")
        {
            if (options.Positional.Length != 0)
            {
                throw new ArgumentException("Unexpected arguments.");
            }

            return await LoginAsync(options.Get("--relay") ?? GroundhogService.DefaultRelayAddress, store, output, cancellation);
        }
        var authentication = new SessionAuthentication(store, createClient);
        var session = store.Read();
        if (args[0] == "logout")
        {
            await authentication.LogoutAsync(cancellation);
            output.WriteLine("Signed out; saved CLI credentials and tunnel keys removed."); return 0;
        }
        // A saved key outlives the sign-in, so key copy needs neither a valid session nor the service when the
        // workspace is known.
        if (keyCopy && options.Positional is [_, var copyName] && (options.Get("--workspace") ?? session?.Workspace) is { } copyWorkspace)
        {
            return CopyKey(session, copyWorkspace, copyName, projectSecrets!, output);
        }

        session = await authentication.GetSessionAsync(cancellation);
        if (session is null || session.Expires <= DateTimeOffset.UtcNow)
        {
            throw new ManagementException("Run groundhog login to access remote tunnels.");
        }

        if (options.Get("--relay") is { } relay && new Uri(relay) != new Uri(session.Relay))
        {
            throw new ManagementException("Saved credentials belong to another relay. Run login --relay for that relay.");
        }

        using var client = authentication.CreateClient(session);
        if (args[0] is "whoami" or "workspace")
        {
            var account = await client.SendAsync<AccountInfo>(HttpMethod.Get, "/api/me", null, cancellation);
            if (args[0] == "workspace" && options.Positional is ["use", var slug])
            {
                if (!account.Workspaces.Any(w => w.Slug == slug))
                {
                    throw new ManagementException("No accessible workspace with that slug.");
                }

                await UpdateSessionAsync(store, session, fresh => fresh with { Workspace = slug }, cancellation); output.WriteLine("Workspace selected."); return 0;
            }
            if (options.Positional.Length != 0 && options.Positional is not ["list"])
            {
                throw new ArgumentException("Invalid account command.");
            }

            if (options.Get("--output") == "json")
            {
                output.WriteLine(JsonSerializer.Serialize(account));
            }
            else
            {
                output.WriteLine($"Account: {Plain(account.DisplayName)} · {Plain(account.Email)}");
                var table = new Table().AddColumn("Workspace").AddColumn("Name").AddColumn("Role").AddColumn("Selected");
                foreach (var workspace in account.Workspaces)
                {
                    table.AddRow(TerminalViews.Safe(workspace.Slug), TerminalViews.Safe(workspace.Name),
                    TerminalViews.Safe(workspace.Role), workspace.Slug == session.Workspace ? "*" : "");
                }

                UtilityCommands.Render(output, table);
            }
            return 0;
        }
        var workspaceSlug = options.Get("--workspace") ?? session.Workspace;
        if (workspaceSlug is null)
        {
            var account = await client.SendAsync<AccountInfo>(HttpMethod.Get, "/api/me", null, cancellation);
            if (account.Workspaces.Length != 1)
            {
                throw new ManagementException("Choose a workspace with workspace use <slug> or --workspace <slug>.");
            }

            workspaceSlug = account.Workspaces[0].Slug;
        }
        var collection = $"/api/w/{Uri.EscapeDataString(workspaceSlug)}/tunnels";
        if (args[0] is "access" or "api-key")
        {
            if (args[0] == "api-key" && options.Positional is ["revoke", ..] && !options.Has("--yes") && (!InteractiveCli.Available
                || !await AnsiConsole.PromptAsync(new ConfirmationPrompt("Callers using this API key lose access. Continue?") { DefaultValue = false }, cancellation)))
            {
                throw new ManagementException("Operation cancelled. Scripts must explicitly pass --yes.");
            }

            return await VisitorAccessCommands.RunAsync(args[0], options.Positional, options, accessFlags, client, workspaceSlug, output, cancellation);
        }

        if (args[0] is "list" or "ls")
        {
            if (options.Positional.Length != 0)
            {
                throw new ArgumentException("Unexpected arguments.");
            }

            var tunnels = await client.SendAsync<TunnelInfo[]>(HttpMethod.Get, collection, null, cancellation);
            if (options.Get("--status") is { } state)
            {
                tunnels = tunnels.Where(t => t.State.Equals(state, StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            if (options.Get("--output") == "json")
            {
                output.WriteLine(JsonSerializer.Serialize(tunnels));
            }
            else
            {
                var table = new Table().RoundedBorder().AddColumn("Tunnel").AddColumn("State").AddColumn("Public URL").AddColumn("Requests / hour");
                foreach (var t in tunnels)
                {
                    table.AddRow(TerminalViews.Safe(t.Name), TerminalViews.Safe(t.State), TerminalViews.Safe(t.Url), t.RequestsLastHour.ToString());
                }

                UtilityCommands.Render(output, table);
            }
            return 0;
        }
        var positional = options.Positional;
        if (args[0] == "key")
        {
            if (positional is not (["add" or "list" or "rotate" or "revoke" or "copy", _] or ["revoke", _, _]))
            {
                throw new UsageException("Use key add|list|rotate|revoke|copy <name>, or key revoke <name> <key-id>.");
            }

            if (positional is ["revoke", _, var keyId] && !IsKeyId(keyId))
            {
                throw new UsageException($"A key ID is 32 lowercase hexadecimal characters. Run groundhog key list {Plain(positional[1])} to see them.");
            }
        }
        else if (args[0] == "rename")
        {
            if (positional.Length != 2)
            {
                throw new ArgumentException("Give old and new names.");
            }
        }
        else if (positional.Length != 1)
        {
            throw new ArgumentException("Give a tunnel name.");
        }

        var name = args[0] == "key" ? positional[1] : positional[0];
        var path = ManagementClient.TunnelPath(workspaceSlug, name);
        if (args[0] == "requests")
        {
            _ = UtilityCommands.Filter([], options, DateTimeOffset.UtcNow);
            var search = $"/api/w/{Uri.EscapeDataString(workspaceSlug)}/requests?tunnel={Uri.EscapeDataString(name)}&limit=200";
            if (options.Get("--status") is { } status) { search += "&status=" + Uri.EscapeDataString(status); }
            var records = await client.SendAsync<RemoteSearchResult>(HttpMethod.Get, search, null, cancellation);
            var filtered = UtilityCommands.Filter(records.Entries.Select(r => new RequestRecord(r.Completed, r.Method, r.Path, r.StatusCode,
                r.DurationMs, false, null)), options, DateTimeOffset.UtcNow);
            if (options.Get("--output") == "json") { output.WriteLine(JsonSerializer.Serialize(filtered)); }
            else
            {
                output.WriteLine("Remote persisted request metadata · filters apply to the newest 200 matching server records.");
                UtilityCommands.Render(output, TerminalViews.Requests(filtered));
            }

            if (!options.Has("--follow"))
            {
                return 0;
            }

            return await FollowAsync(client, workspaceSlug, name, search, records.Entries, options, output, error, cancellation);
        }
        if (args[0] == "stats")
        {
            var usage = await client.SendAsync<JsonElement>(HttpMethod.Get, path + "/usage", null, cancellation);
            if (options.Get("--output") == "json") { output.WriteLine(usage.GetRawText()); }
            else
            {
                output.WriteLine($"Remote usage · {usage.GetProperty("from").GetDateTimeOffset():u} to {usage.GetProperty("to").GetDateTimeOffset():u}");
                var chart = new BarChart().Label("Requests by status class (stored minutes)");
                foreach (var group in usage.GetProperty("byStatus").EnumerateObject())
                {
                    var total = group.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Sum(v => v.GetInt64());
                    chart.AddItem(group.Name, total, group.Name == "5xx" ? Color.Red : Color.Cyan);
                }
                UtilityCommands.Render(output, chart);
                output.WriteLine("Gaps represent unavailable data; current minute may not yet be persisted.");
            }
            return 0;
        }
        if (keyCopy)
        {
            return CopyKey(session, workspaceSlug, name, projectSecrets!, output);
        }

        // Adding and listing keys disconnect nothing, so only the other key commands ask first.
        if (args[0] is "delete" or "rename" || (args[0] == "key" && positional[0] is not ("add" or "list")))
        {
            if (!options.Has("--yes") && (!InteractiveCli.Available || !await AnsiConsole.PromptAsync(
                new ConfirmationPrompt("This operation changes the remote tunnel and may disconnect its agent. Continue?") { DefaultValue = false }, cancellation)))
            {
                throw new ManagementException("Operation cancelled. Scripts must explicitly pass --yes.");
            }
        }
        switch (args[0])
        {
            case "describe" or "show":
                var tunnel = await client.SendAsync<TunnelInfo>(HttpMethod.Get, path, null, cancellation);
                if (options.Get("--output") == "json")
                {
                    output.WriteLine(JsonSerializer.Serialize(tunnel));
                }
                else
                {
                    var table = new Table().Border(TableBorder.None).AddColumn("Field").AddColumn("Value");
                    table.AddRow("Name", TerminalViews.Safe(tunnel.Name)); table.AddRow("State", TerminalViews.Safe(tunnel.State));
                    table.AddRow("Public URL", TerminalViews.Safe(tunnel.Url)); table.AddRow("Requests / hour", tunnel.RequestsLastHour.ToString());
                    table.AddRow("Bytes today (in / out)", $"{tunnel.BytesInToday} / {tunnel.BytesOutToday}");
                    table.AddRow("Available actions", TerminalViews.Safe(string.Join(", ", tunnel.Actions)));
                    UtilityCommands.Render(output, new Panel(table).Header("Remote tunnel").RoundedBorder());
                }
                break;
            case "create":
                var created = await client.SendAsync<TunnelCreated>(HttpMethod.Post, collection, new { Name = name }, cancellation);
                await SaveKeyAsync(store, session, workspaceSlug, created.Tunnel.Name, created.ConnectionKey, cancellation);
                output.WriteLine("Tunnel created. Its connection key was saved securely for hosting.");
                if (projectSecrets is null)
                {
                    output.WriteLine($"To use this tunnel from an Aspire AppHost, run: groundhog key copy {Plain(created.Tunnel.Name)} --project <path to AppHost.csproj>");
                }
                else
                {
                    projectSecrets.Write(created.Tunnel.Name, created.ConnectionKey, output);
                }
                break;
            case "rename":
                var renamed = await client.SendAsync<TunnelInfo>(HttpMethod.Patch, path, new { Name = positional[1] }, cancellation);
                await UpdateSessionAsync(store, session, fresh =>
                {
                    if (fresh.TunnelKeys.Remove(KeyId(workspaceSlug, name), out var renamedKey))
                    { fresh.TunnelKeys[KeyId(workspaceSlug, renamed.Name)] = renamedKey; }
                    return fresh;
                }, cancellation);
                output.WriteLine($"Tunnel renamed to {Plain(renamed.Name)}. Update saved hosting profiles.");
                break;
            case "delete":
                await client.SendAsync<object>(HttpMethod.Delete, path, null, cancellation);
                await UpdateSessionAsync(store, session, fresh => { fresh.TunnelKeys.Remove(KeyId(workspaceSlug, name)); return fresh; }, cancellation);
                output.WriteLine("Tunnel deleted."); break;
            case "key" when positional[0] == "add":
                var added = await AddKeyAsync(client, path, name, cancellation);
                await SaveKeyAsync(store, session, workspaceSlug, name, added.ConnectionKey, cancellation);
                output.WriteLine("Key added and saved securely on this computer, replacing the key saved here before.");
                output.WriteLine("Agents already connected stay connected, and the old key keeps working until you revoke it.");
                output.WriteLine($"Restart hosting with the new key, then run groundhog key list {Plain(name)} to find the old key's ID and " +
                    $"groundhog key revoke {Plain(name)} <old key ID> to revoke it.");
                projectSecrets?.Write(name, added.ConnectionKey, output);
                break;
            case "key" when positional[0] == "list":
                var keys = (await client.SendAsync<TunnelInfo>(HttpMethod.Get, path, null, cancellation)).Keys
                    ?? throw new ManagementException("This relay doesn't list connection keys yet.");
                if (options.Get("--output") == "json")
                {
                    output.WriteLine(JsonSerializer.Serialize(keys));
                }
                else
                {
                    // Plain columns rather than a table, which shrinks to the terminal's width: key revoke takes the full ID, so
                    // it's never truncated or wrapped.
                    output.WriteLine($"{"ID",-34}{"Created (UTC)",-18}{"Last used (UTC)",-18}Connected");
                    foreach (var key in keys)
                    {
                        output.WriteLine($"{Plain(key.Id),-34}{Minute(key.CreatedAt),-18}{(key.LastUsedAt is { } used ? Minute(used) : "Never"),-18}" +
                            (key.Connected ? "Yes" : "No"));
                    }
                }
                break;
            case "key" when positional is ["revoke", _, var revokedId]:
                await RevokeKeyAsync(client, path, name, revokedId, cancellation);
                // The saved key stays: its ID can't be worked out from the key, so it may be the key that's still live.
                output.WriteLine($"Key {revokedId[..8]} revoked. Agents that used it were disconnected.");
                break;
            case "key":
                if (positional[0] == "rotate")
                {
                    var rotated = await client.SendAsync<TunnelCreated>(HttpMethod.Post, path + "/rotate-key", null, cancellation);
                    await SaveKeyAsync(store, session, workspaceSlug, name, rotated.ConnectionKey, cancellation);
                    output.WriteLine("Key rotated and saved securely. Restart hosting to use the new key.");
                    projectSecrets?.Write(name, rotated.ConnectionKey, output);
                }
                else
                {
                    await client.SendAsync<TunnelInfo>(HttpMethod.Post, path + "/revoke-key", null, cancellation);
                    await UpdateSessionAsync(store, session, fresh => { fresh.TunnelKeys.Remove(KeyId(workspaceSlug, name)); return fresh; }, cancellation);
                    output.WriteLine("Tunnel keys revoked.");
                }
                break;
            case "open":
                var opened = await client.SendAsync<TunnelInfo>(HttpMethod.Get, path, null, cancellation);
                var url = new Uri(opened.Url);
                if (url.Scheme != "https" || url.UserInfo.Length != 0)
                {
                    throw new ManagementException("Relay returned an invalid public URL.");
                }

                Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
                output.WriteLine("Opened public URL."); break;
        }
        return 0;
    }

    internal static string KeyId(string workspace, string name) => workspace + "/" + name;

    /// <summary>
    /// The connection key saved on this computer for tunnel <paramref name="name"/>: the default workspace's entry first,
    /// then any workspace's (tunnel names are globally unique, so a name identifies one tunnel).
    /// </summary>
    internal static string? StoredKey(LoginSession session, string name) =>
        session.Workspace is { } workspace && session.TunnelKeys.TryGetValue(KeyId(workspace, name), out var key) ? key
            : session.TunnelKeys.Where(entry => entry.Key.EndsWith("/" + name, StringComparison.Ordinal))
                .Select(entry => entry.Value).FirstOrDefault();

    private static string Minute(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="value"/> has the form of a connection key ID: 32 lowercase hexadecimal characters.</summary>
    private static bool IsKeyId(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// <c>key add &lt;name&gt;</c>: makes a second live key. A relay without the keys route answers 404 or 405 for it, the
    /// same 404 as for a tunnel that doesn't exist, so the tunnel is read to tell the two apart.
    /// </summary>
    private static async Task<TunnelCreated> AddKeyAsync(ManagementClient client, string path, string name, CancellationToken cancellation)
    {
        try
        {
            return await client.SendAsync<TunnelCreated>(HttpMethod.Post, path + "/keys", null, cancellation);
        }
        // Almost always the key limit; the relay also answers 409 when the tunnel changed during the request.
        catch (ManagementException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            throw new ManagementException($"A tunnel has at most 2 connection keys. Revoke one first: groundhog key list {Plain(name)} " +
                "shows their IDs.", ex.Status);
        }
        catch (ManagementException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            _ = await client.SendAsync<TunnelInfo>(HttpMethod.Get, path, null, cancellation);
            throw new ManagementException($"This relay doesn't support adding keys yet. groundhog key rotate {Plain(name)} makes a new key, " +
                "but disconnects agents using the old key.", ex.Status);
        }
    }

    /// <summary>
    /// <c>key revoke &lt;name&gt; &lt;key-id&gt;</c>: revokes one key. A 404 means no such live key, or a relay without the
    /// keys route; a tunnel without <c>keys</c> in its JSON comes from the older relay.
    /// </summary>
    private static async Task RevokeKeyAsync(ManagementClient client, string path, string name, string keyId, CancellationToken cancellation)
    {
        try
        {
            await client.SendAsync<TunnelInfo>(HttpMethod.Delete, path + "/keys/" + keyId, null, cancellation);
        }
        catch (ManagementException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            var tunnel = await client.SendAsync<TunnelInfo>(HttpMethod.Get, path, null, cancellation);
            throw tunnel.Keys is null || ex.Status == HttpStatusCode.MethodNotAllowed
                ? new ManagementException($"This relay doesn't support revoking a single key yet. groundhog key revoke {Plain(name)} " +
                    "revokes every key of the tunnel.", ex.Status)
                : new ManagementException($"{Plain(name)} has no live key {keyId}. Run groundhog key list {Plain(name)} to see its keys.", ex.Status);
        }
    }

    /// <summary>
    /// <c>key copy &lt;name&gt; --project</c>: writes the key that <c>create</c>, <c>key add</c> or <c>key rotate</c> saved on this computer
    /// into the project's user secrets. Keys can't be read back from the service.
    /// </summary>
    private static int CopyKey(LoginSession? session, string workspace, string name, ProjectSecrets projectSecrets, TextWriter output)
    {
        if (session is null || !session.TunnelKeys.TryGetValue(KeyId(workspace, name), out var key))
        {
            throw new ManagementException(
                $"{Plain(name)}'s connection key isn't saved on this computer, for example because the tunnel was created in the " +
                $"Groundhog dashboard or on another computer. Run groundhog key rotate {Plain(name)} --project {Plain(projectSecrets.Project)} " +
                "to make a new key and write it; rotating disconnects anything still using the old key.");
        }

        projectSecrets.Write(name, key, output);
        return 0;
    }
    private static Task SaveKeyAsync(CredentialStore store, LoginSession session, string workspace, string name, string key, CancellationToken cancellation) =>
        UpdateSessionAsync(store, session, fresh => { fresh.TunnelKeys[KeyId(workspace, name)] = key; return fresh; }, cancellation);

    private static Task UpdateSessionAsync(CredentialStore store, LoginSession expected, Func<LoginSession, LoginSession> update, CancellationToken cancellation) =>
        store.UpdateAsync(fresh => fresh is not null && fresh.SessionId == expected.SessionId && fresh.Relay == expected.Relay
            ? update(fresh) : fresh, cancellation);
    private static string Plain(string value) => new(value.Where(c => !char.IsControl(c)).ToArray());

    /// <summary>
    /// <c>requests &lt;name&gt; --remote --follow</c>: after the snapshot, prints each request the service finishes, one line
    /// each, until Ctrl+C. Filters other than <c>--since</c> and <c>--limit</c> apply to every line.
    /// </summary>
    private static async Task<int> FollowAsync(ManagementClient client, string workspace, string name, string search,
        RemoteRequest[] known, Options options, TextWriter output, TextWriter error, CancellationToken cancellation)
    {
        var follower = new RequestFollower(
            token => client.OpenRequestStreamAsync(workspace, name, token),
            async token => (await client.SendAsync<RemoteSearchResult>(HttpMethod.Get, search, null, token)).Entries,
            TimeProvider.System);
        follower.Seen(known);
        output.WriteLine("Following live requests · Ctrl+C to stop.");
        output.Flush();
        string? stopped = null;
        await follower.RunAsync(request =>
        {
            var record = new RequestRecord(request.Completed, request.Method, request.Path, request.StatusCode, request.DurationMs, false, null);
            if (UtilityCommands.Filter([record], options, request.Completed).Length == 0)
            {
                return;
            }

            output.WriteLine(Line(record));
            output.Flush();
        }, state => stopped = state.Status == LiveStatus.Stopped ? state.Message : stopped, cancellation);
        error.WriteLine("groundhog: live requests stopped. " + stopped);
        return ExitCodes.Failed;
    }

    internal static string Line(RequestRecord r) =>
        $"{r.Time:HH:mm:ss}  {Plain(r.Method),-7} {r.Status,3} {r.DurationMs,8:0.##} ms  {Plain(r.Path)}";

    /// <summary>
    /// Runs the browser-approved device sign-in. The interactive app passes <paramref name="challenge"/> to draw the code
    /// itself; the command prints it.
    /// </summary>
    internal static async Task<int> LoginAsync(string relay, CredentialStore store, TextWriter output, CancellationToken cancellation,
        Action<SignInChallenge>? challenge = null)
    {
        using var client = new ManagementClient(relay);
        var authorization = await client.SendAsync<DeviceAuthorization>(HttpMethod.Post, "/api/cli/device", new { }, cancellation);
        var verification = new Uri(authorization.VerificationUri);
        if (verification.GetLeftPart(UriPartial.Authority) != new Uri(relay).GetLeftPart(UriPartial.Authority)
            || verification.AbsolutePath != "/cli-login" || authorization.ExpiresIn is < 1 or > 600 || authorization.Interval is < 1 or > 30)
        {
            throw new ManagementException("Relay returned an invalid sign-in challenge.");
        }

        if (challenge is null)
        {
            output.WriteLine($"Authorize this CLI in your browser. Confirm code: {Plain(authorization.UserCode)}");
            output.WriteLine(verification.ToString());
        }
        else
        {
            challenge(new(Plain(authorization.UserCode), verification, DateTimeOffset.UtcNow.AddSeconds(authorization.ExpiresIn)));
        }

        if (InteractiveCli.Available)
        {
            // A headless session with a terminal has no browser; the URL is shown either way.
            Browser.TryOpen(verification);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(authorization.ExpiresIn));
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(authorization.Interval), timeout.Token);
                var grant = await client.SendAsync<DeviceToken>(HttpMethod.Post, "/api/cli/token", new { authorization.DeviceCode }, timeout.Token);
                if (grant.State == "pending")
                {
                    continue;
                }

                if (grant.State != "approved" || grant.AccessToken is null || grant.ExpiresIn is < 1 or > 28800)
                {
                    throw new ManagementException("Sign-in code expired or was already used. Run login again.");
                }

                using var signedIn = new ManagementClient(relay, grant.AccessToken);
                var account = await signedIn.SendAsync<AccountInfo>(HttpMethod.Get, "/api/me", null, timeout.Token);
                var supportsRefresh = grant.RefreshToken is not null && await new SessionAuthentication(store).SupportsRefreshAsync(relay, timeout.Token);
                await store.UpdateAsync(_ => new(relay, grant.AccessToken, DateTimeOffset.UtcNow.AddSeconds(grant.ExpiresIn),
                    account.Workspaces.Length == 1 ? account.Workspaces[0].Slug : null, new(),
                    supportsRefresh ? grant.RefreshToken : null, supportsRefresh, Guid.NewGuid().ToString("N")), timeout.Token);
                output.WriteLine("Signed in. Run groundhog list or workspace list.");
                return 0;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new ManagementException("Browser approval timed out. Run login again."); }
    }
}
