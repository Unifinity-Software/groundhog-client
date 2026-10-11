using Spectre.Console;

namespace Groundhog.Cli;

internal static class InteractiveCli
{
    internal static bool Disabled { get; set; }
    internal static SelectionPrompt<string> HomePrompt() => new SelectionPrompt<string>()
        .Title("[bold]Choose an action[/]")
        .MoreChoicesText("[dim]↑↓ Navigate · Enter Select · Choose Quit to exit[/]")
        .AddChoices("Remote tunnels", "Sign in", "Workspaces", "Host a saved profile", "Running agents", "Saved profiles", "Set up a profile", "Diagnostics", "Quit");
    internal static bool Available => !Disabled && !Console.IsInputRedirected && !Console.IsOutputRedirected
        && AnsiConsole.Profile.Capabilities.Interactive;

    internal static async Task<int> RunAsync(CancellationToken cancellation)
    {
        if (!Available)
        {
            Console.Error.WriteLine("groundhog: dashboard requires an interactive terminal. Use --help for commands.");
            return ExitCodes.BadArguments;
        }
        var profiles = new ProfileStore(ProfileStore.DefaultDirectory);
        while (!cancellation.IsCancellationRequested)
        {
            AnsiConsole.Write(new Rule("[cyan]Groundhog[/]"));
            AnsiConsole.WriteLine("↑↓ Navigate · Enter Select · Choose Back or Quit to leave a menu");
            var action = await AnsiConsole.PromptAsync(HomePrompt(), cancellation);
            if (action == "Quit")
            {
                return 0;
            }
            if (action is "Remote tunnels" or "Sign in" or "Workspaces")
            {
                if (action == "Remote tunnels")
                {
                    await BrowseTunnelsAsync(cancellation);
                }
                else if (action == "Workspaces") { await BrowseTunnelsAsync(cancellation, selectWorkspaceOnly: true); }
                else
                {
                    await GroundhogCommand.RunAsync(["login"],
                    Environment.GetEnvironmentVariable, Console.Out, Console.Error, cancellation);
                }

                continue;
            }

            if (action == "Set up a profile") { await SetupAsync(profiles, cancellation); continue; }
            if (action == "Saved profiles") { AnsiConsole.Write(TerminalViews.Profiles(profiles.Read())); continue; }
            if (action == "Diagnostics")
            {
                await GroundhogCommand.RunAsync(["doctor"], Environment.GetEnvironmentVariable, Console.Out, Console.Error, cancellation);
                continue;
            }
            if (action == "Host a saved profile")
            {
                var names = profiles.Read().Select(p => p.Name).Append("Back").ToArray();
                var name = await AnsiConsole.PromptAsync(new SelectionPrompt<string>().Title("Select a saved profile")
                    .UseConverter(TerminalViews.Safe).AddChoices(names), cancellation);
                if (name != "Back")
                {
                    await GroundhogCommand.RunAsync(["host", name], Environment.GetEnvironmentVariable,
                    Console.Out, Console.Error, cancellation);
                }

                continue;
            }
            var registrations = LocalSessions.Registrations();
            var choices = registrations.Select((r, i) => $"{i + 1}: {r.Name}").Append("Back").ToArray();
            var selected = await AnsiConsole.PromptAsync(new SelectionPrompt<string>().Title("Select a local agent")
                .UseConverter(TerminalViews.Safe).AddChoices(choices), cancellation);
            if (selected == "Back")
            {
                continue;
            }

            var registration = registrations[Array.IndexOf(choices, selected)];
            var snapshot = await LocalSessions.QueryAsync(registration, false, cancellation);
            if (snapshot is null) { AnsiConsole.WriteLine("Agent is no longer available."); continue; }
            AnsiConsole.Write(TerminalViews.Session(snapshot));
            var next = await AnsiConsole.PromptAsync(new SelectionPrompt<string>().Title("Agent actions")
                .AddChoices("Refresh", "View requests", "View statistics", "Stop local agent", "Back"), cancellation);
            if (next == "Stop local agent")
            {
                if (await AnsiConsole.PromptAsync(new ConfirmationPrompt("Stop this local agent?") { DefaultValue = false }, cancellation))
                {
                    await LocalSessions.QueryAsync(registration, true, cancellation);
                }
            }
            else if (next != "Back")
            {
                snapshot = await LocalSessions.QueryAsync(registration, false, cancellation);
                if (snapshot is not null)
                {
                    AnsiConsole.Write(next == "View requests"
                    ? TerminalViews.Requests(snapshot.Recent) : TerminalViews.Session(snapshot, next));
                }
            }
        }
        return 0;
    }

    internal static async Task SetupAsync(ProfileStore store, CancellationToken cancellation)
    {
        var name = await AnsiConsole.PromptAsync(new TextPrompt<string>("Tunnel name (already created on the service):"), cancellation);
        var target = await AnsiConsole.PromptAsync(new TextPrompt<string>("Local target:").DefaultValue("http://localhost:5173"), cancellation);
        try
        {
            store.Save(new(name, target));
            AnsiConsole.WriteLine("Profile saved. Supply its key through GROUNDHOG_KEY before hosting.");
        }
        catch (ArgumentException ex) { AnsiConsole.Write(new Text(ex.Message)); }
    }

    private static async Task BrowseTunnelsAsync(CancellationToken cancellation, bool selectWorkspaceOnly = false)
    {
        try
        {
            var session = new CredentialStore(ProfileStore.DefaultDirectory).Read();
            if (session is null || session.Expires <= DateTimeOffset.UtcNow)
            {
                throw new ManagementException("Run login first.");
            }

            using var client = new ManagementClient(session.Relay, session.Token);
            var account = await client.SendAsync<AccountInfo>(HttpMethod.Get, "/api/me", null, cancellation);
            if (account.Workspaces.Length == 0) { AnsiConsole.WriteLine("No accessible workspaces."); return; }
            var workspace = await AnsiConsole.PromptAsync(new SelectionPrompt<string>().Title("Workspace")
                .UseConverter(TerminalViews.Safe).AddChoices(account.Workspaces.Select(w => w.Slug).Append("Back")), cancellation);
            if (workspace == "Back")
            {
                return;
            }
            var credentialStore = new CredentialStore(ProfileStore.DefaultDirectory);
            credentialStore.Save((credentialStore.Read() ?? session) with { Workspace = workspace });
            if (selectWorkspaceOnly) { AnsiConsole.WriteLine("Workspace selected."); return; }

            while (!cancellation.IsCancellationRequested)
            {
                var tunnels = await client.SendAsync<TunnelInfo[]>(HttpMethod.Get, $"/api/w/{Uri.EscapeDataString(workspace)}/tunnels", null, cancellation);
                var selected = await AnsiConsole.PromptAsync(new SelectionPrompt<string>().Title("Select a remote tunnel (type to search)")
                    .EnableSearch().UseConverter(TerminalViews.Safe).AddChoices(tunnels.Select(t => t.Name).Append("Create a tunnel").Append("Back")), cancellation);
                if (selected == "Back")
                {
                    return;
                }
                if (selected == "Create a tunnel")
                {
                    var createdName = await AnsiConsole.PromptAsync(new TextPrompt<string>("New tunnel name:"), cancellation);
                    await GroundhogCommand.RunAsync(["create", createdName, "--workspace", workspace], Environment.GetEnvironmentVariable,
                        Console.Out, Console.Error, cancellation);
                    continue;
                }

                await GroundhogCommand.RunAsync(["describe", selected, "--workspace", workspace], Environment.GetEnvironmentVariable, Console.Out, Console.Error, cancellation);
                var choices = new List<string> { "Open in browser", "Host locally", "Remote requests", "Remote usage", "Local status", "Local requests", "Local statistics" };
                var permitted = tunnels.Single(t => t.Name == selected).Actions;
                if (permitted.Contains("rename", StringComparer.Ordinal)) { choices.Add("Rename tunnel"); }
                if (permitted.Contains("rotateKey", StringComparer.Ordinal)) { choices.Add("Rotate key"); }
                if (permitted.Contains("revokeKey", StringComparer.Ordinal)) { choices.Add("Revoke keys"); }
                if (permitted.Contains("delete", StringComparer.Ordinal)) { choices.Add("Delete tunnel"); }
                choices.Add("Back");
                var operation = await AnsiConsole.PromptAsync(new SelectionPrompt<string>().Title("Tunnel actions").AddChoices(choices), cancellation);
                if (operation is "Rename tunnel" or "Rotate key" or "Revoke keys" or "Delete tunnel")
                {
                    string[] mutation = operation switch
                    {
                        "Rename tunnel" => ["rename", selected, await AnsiConsole.PromptAsync(new TextPrompt<string>("New tunnel name:"), cancellation)],
                        "Rotate key" => ["key", "rotate", selected],
                        "Revoke keys" => ["key", "revoke", selected],
                        _ => ["delete", selected],
                    };
                    await GroundhogCommand.RunAsync([.. mutation, "--workspace", workspace], Environment.GetEnvironmentVariable,
                        Console.Out, Console.Error, cancellation);
                    continue;
                }
                var command = operation switch
                {
                    "Open in browser" => "open",
                    "Host locally" => "host",
                    "Local status" => "status",
                    "Local requests" => "requests",
                    "Local statistics" => "stats",
                    "Remote requests" => "requests",
                    "Remote usage" => "stats",
                    _ => null
                };
                if (command is null)
                {
                    continue;
                }

                string[] arguments;
                if (command == "host")
                {
                    var target = await AnsiConsole.PromptAsync(new TextPrompt<string>("Local target:").DefaultValue("http://localhost:5173"), cancellation);
                    credentialStore.Save((credentialStore.Read() ?? session) with { Workspace = workspace });
                    arguments = ["host", selected, "--target", target, "--relay", session.Relay];
                }
                else if (operation.StartsWith("Remote", StringComparison.Ordinal))
                {
                    arguments = [command, selected, "--remote", "--workspace", workspace];
                }
                else
                {
                    arguments = command == "open" ? [command, selected, "--workspace", workspace] : [command, selected];
                }

                await GroundhogCommand.RunAsync(arguments, Environment.GetEnvironmentVariable, Console.Out, Console.Error, cancellation);
            }
        }
        catch (ManagementException ex) { AnsiConsole.Write(new Text(ex.Message)); }
        catch (HttpRequestException) { AnsiConsole.WriteLine("Relay unavailable. Check your network and relay settings."); }
    }
}
