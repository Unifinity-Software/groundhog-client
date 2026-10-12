using Groundhog.Cli.Interactive;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Groundhog.Cli.Views;

internal enum CheckState { Pending, Running, Passed, Warning, Failed }

/// <summary>One line of the diagnostics checklist.</summary>
internal sealed record CheckRow(string Name, CheckState State, string? Detail);

/// <summary>Views of this machine: running agents, saved profiles and diagnostics.</summary>
internal static class LocalViews
{
    internal static IRenderable AgentTable(IReadOnlyList<LocalAgent> agents, int maxRows, DateTimeOffset now, ViewSize size, int? selected = null)
    {
        var full = size.Width >= 110;
        var table = Theme.Grid(full ? ["", "Agent", "State", "Local target", "Public URL", "Requests", "Uptime"]
            : ["", "Agent", "Local target", "Requests", "Up"]);
        table.Columns[0].Width = 1;
        table.Columns[full ? 5 : 3].RightAligned();
        table.Columns[full ? 6 : 4].RightAligned();
        var (start, count) = TunnelViews.Window(agents.Count, selected ?? 0, maxRows);
        for (var i = start; i < start + count; i++)
        {
            var s = agents[i].Snapshot;
            var pointer = i == selected ? $"[bold {Theme.SignalTag}]▶[/]" : full ? " " : Theme.Dot(s.State);
            var name = new Markup(i == selected ? $"[bold {Theme.SignalTag}]{Theme.Safe(s.Name)}[/]" : Theme.Strong(Theme.Safe(s.Name)))
            {
                Overflow = Overflow.Ellipsis,
            };
            var target = Theme.Cell(s.Target.TrimEnd('/'));
            if (full)
            {
                table.AddRow(new Markup(pointer), name, new Markup(Theme.State(s.State)), target,
                    Theme.Cell(s.PublicAddress is null ? "—" : TunnelViews.Host(s.PublicAddress)),
                    new Text(Theme.Number(s.Requests)), new Text(Theme.Duration(now - s.Started)));
            }
            else
            {
                table.AddRow(new Markup(pointer), name, target, new Text(Theme.Number(s.Requests)), new Text(Theme.Duration(now - s.Started)));
            }
        }

        return table;
    }

    internal static IRenderable Agents(LocalAgent[]? agents, int selected, ViewSize size, DateTimeOffset now)
    {
        if (agents is null)
        {
            return Theme.Section("Running agents", new Markup(Theme.Dim("Looking for local agents…")));
        }

        if (agents.Length == 0)
        {
            return Theme.Section("Running agents", new Markup(Theme.Dim(
                "No agents are running on this machine.\nHost a tunnel from Tunnels or Saved profiles, or run groundhog start <profile>.")));
        }

        return Theme.Section("Running agents", AgentTable(agents, size.Height - 4, now, size, selected), focus: true);
    }

    internal static IRenderable Profiles(TunnelProfile[] profiles, LocalAgent[]? agents, int? selected, ViewSize size)
    {
        if (profiles.Length == 0)
        {
            return Theme.Section("Saved profiles", new Markup(Theme.Dim(
                "No saved profiles. Press A to save a tunnel name with its local target.\nProfiles never contain keys; supply them through GROUNDHOG_KEY or sign in.")));
        }

        var table = Theme.Grid("", "Profile", "Local target", "Service", "On this machine");
        table.Columns[0].Width = 1;
        var (start, count) = TunnelViews.Window(profiles.Length, selected ?? 0, size.Height - 4);
        for (var i = start; i < start + count; i++)
        {
            var p = profiles[i];
            var running = agents?.FirstOrDefault(a => a.Snapshot.Name == p.Name);
            table.AddRow(i == selected ? $"[bold {Theme.SignalTag}]▶[/]" : " ",
                i == selected ? $"[bold {Theme.SignalTag}]{Theme.Safe(p.Name)}[/]" : Theme.Strong(Theme.Safe(p.Name)),
                Theme.Safe(p.Target.TrimEnd('/')),
                p.Relay is null ? Theme.Dim("hosted") : Theme.Safe(p.Relay),
                running is null ? Theme.Dim("not running") : Theme.State(running.Snapshot.State));
        }

        return Theme.Section("Saved profiles", table, focus: selected is not null);
    }

    internal static IRenderable Diagnostics(IReadOnlyList<CheckRow> rows, string spinner, bool done, ViewSize size)
    {
        var table = new Table { Border = TableBorder.None, ShowHeaders = false };
        table.AddColumn(new TableColumn("") { Width = 2, NoWrap = true });
        table.AddColumn(new TableColumn("") { NoWrap = true });
        table.AddColumn(new TableColumn(""));
        foreach (var row in rows)
        {
            var glyph = row.State switch
            {
                CheckState.Running => $"[{Theme.SignalTag}]{Markup.Escape(spinner)}[/]",
                CheckState.Passed => $"[bold {Theme.GreenTag}]✓[/]",
                CheckState.Warning => $"[bold {Theme.AmberTag}]![/]",
                CheckState.Failed => $"[bold {Theme.RedTag}]✗[/]",
                _ => Theme.Dim("·"),
            };
            var detail = row.State switch
            {
                CheckState.Running => Theme.Dim("checking…"),
                CheckState.Pending => Theme.Dim("waiting"),
                CheckState.Failed => $"[{Theme.RedTag}]{Theme.Safe(row.Detail)}[/]",
                CheckState.Warning => $"[{Theme.AmberTag}]{Theme.Safe(row.Detail)}[/]",
                _ => Theme.Safe(row.Detail),
            };
            table.AddRow(new Markup(glyph), new Markup(row.State == CheckState.Pending ? Theme.Dim(Theme.Safe(row.Name)) : Theme.Strong(Theme.Safe(row.Name))),
                new Markup(detail));
        }

        var failed = rows.Count(r => r.State == CheckState.Failed);
        var warnings = rows.Count(r => r.State == CheckState.Warning);
        var summary = !done ? Theme.Dim("Running checks…")
            : failed > 0 ? $"[{Theme.RedTag}]{Dashboard.Plural(failed, "check")} failed.[/] {Theme.Dim("Fix the items marked ✗, then press R to run the checks again.")}"
            : warnings > 0 ? $"[{Theme.AmberTag}]All required checks passed, with {Dashboard.Plural(warnings, "warning")}.[/]"
            : $"[{Theme.GreenTag}]All checks passed.[/]";
        var note = Theme.Dim("Target checks send a HEAD request; any HTTP response proves the app is reachable, not that it is healthy.");
        return Theme.Section("Diagnostics", new Rows(table, new Text(""), new Markup(summary), new Markup(note)), focus: true);
    }
}

/// <summary>Views of the account: workspaces and sign-in.</summary>
internal static class AccountViews
{
    internal static IRenderable Workspaces(AccountState? account, int selected, IReadOnlyDictionary<string, TunnelInfo[]?> tunnels, ViewSize size)
    {
        if (account is null)
        {
            return Theme.Section("Workspaces", new Markup(Theme.Dim("Loading account…")));
        }

        if (!account.SignedIn)
        {
            return Theme.Section("Workspaces", new Markup($"[{Theme.AmberTag}]{Theme.Safe(account.Problem ?? "Not signed in.")}[/]\n"
                + Theme.Dim("Sign in to choose a workspace.")));
        }

        var info = account.Account!;
        var tree = new Tree(new Markup($"{Theme.Strong(Theme.Safe(info.DisplayName))} {Theme.Dim(Theme.Safe(info.Email))}"))
        {
            Style = Theme.BorderStyle,
            Guide = TreeGuide.Line,
        };
        if (info.Workspaces.Length == 0)
        {
            tree.AddNode(new Markup(Theme.Dim("No accessible workspaces.")));
        }

        for (var i = 0; i < info.Workspaces.Length; i++)
        {
            var w = info.Workspaces[i];
            var current = w.Slug == account.Workspace;
            var label = (i == selected ? $"[bold {Theme.SignalTag}]▶ {Theme.Safe(w.Slug)}[/]" : $"  {Theme.Strong(Theme.Safe(w.Slug))}")
                + $" {Theme.Dim(Theme.Safe(w.Name) + " · " + Theme.Safe(w.Role))}"
                + (current ? $" [{Theme.GreenTag}]● current[/]" : "");
            var node = tree.AddNode(new Markup(label));
            if (i != selected)
            {
                continue;
            }

            tunnels.TryGetValue(w.Slug, out var list);
            if (list is null)
            {
                node.AddNode(new Markup(Theme.Dim("Loading tunnels…")));
            }
            else if (list.Length == 0)
            {
                node.AddNode(new Markup(Theme.Dim("No tunnels yet.")));
            }
            else
            {
                foreach (var t in list.Take(Math.Max(1, size.Height - info.Workspaces.Length - 6)))
                {
                    node.AddNode(new Markup($"{Theme.Dot(t.State)} {Theme.Safe(t.Name)} {Theme.Dim(Theme.Safe(TunnelViews.Host(t.Url)))}"));
                }
            }
        }

        return Theme.Section("Workspaces", new Rows(tree, new Text(""),
            new Markup(Theme.Dim("Enter makes the highlighted workspace your default for tunnels and commands."))), focus: true);
    }

    internal sealed record SignInModel(AccountState? Account, SignInChallenge? Challenge, bool Waiting, StatusMessage? Outcome,
        string Spinner, DateTimeOffset Now);

    internal static IRenderable SignIn(SignInModel model, ViewSize size)
    {
        var rows = new List<IRenderable>();
        if (model.Challenge is { } challenge)
        {
            rows.Add(new Markup($"{Theme.Dim("1.")} Your browser opens {Theme.Accent(Theme.Safe(challenge.Verification.ToString()))}"));
            rows.Add(new Markup($"{Theme.Dim("   If it doesn't, open that address on any device.")}"));
            rows.Add(new Markup($"{Theme.Dim("2.")} Check that the page shows this code:"));
            var code = challenge.Code.Length == 12 ? challenge.Code[..6] + " " + challenge.Code[6..] : challenge.Code;
            rows.Add(new Padder(new FigletBanner(code, Theme.Signal, maxLines: size.Height >= 30 ? 12 : 6), new Padding(3, 1, 0, 0)));
            rows.Add(new Markup($"   {Theme.Dim("Code")} {Theme.Strong(Theme.Safe(challenge.Code))}"));
            rows.Add(new Markup($"{Theme.Dim("3.")} Approve CLI access."));
            rows.Add(new Text(""));
            var left = challenge.Expires - model.Now;
            rows.Add(new Markup($"[{Theme.SignalTag}]{Markup.Escape(model.Spinner)}[/] Waiting for approval… {Theme.Dim("code expires in " + Theme.Duration(left))}"));
        }
        else if (model.Waiting)
        {
            rows.Add(new Markup($"[{Theme.SignalTag}]{Markup.Escape(model.Spinner)}[/] Requesting a sign-in code from groundhog.unifinity.com…"));
        }
        else
        {
            if (model.Account is { SignedIn: true } account)
            {
                rows.Add(new Markup($"Signed in as {Theme.Strong(Theme.Safe(account.Account!.DisplayName))} {Theme.Dim(Theme.Safe(account.Account.Email))}"));
                if (account.Expires is { } expires)
                {
                    rows.Add(new Markup(Theme.Dim($"This CLI session expires in {Theme.Duration(expires - model.Now)}.")));
                }

                rows.Add(new Text(""));
                rows.Add(new Markup($"{Chrome.KeyCap("Enter")} Sign in again   {Chrome.KeyCap("X")} Sign out and remove saved keys"));
            }
            else
            {
                rows.Add(new Markup("Sign in to manage your remote tunnels from this terminal."));
                rows.Add(new Markup(Theme.Dim("Your browser approves the CLI; the CLI never sees your password. Sessions last eight hours.")));
                rows.Add(new Text(""));
                rows.Add(new Markup($"{Chrome.KeyCap("Enter")} Sign in"));
            }
        }

        if (model.Outcome is { } outcome)
        {
            rows.Add(new Text(""));
            var color = outcome.Kind == StatusKind.Error ? Theme.RedTag : outcome.Kind == StatusKind.Success ? Theme.GreenTag : Theme.TextTag;
            rows.Add(new Markup($"[{color}]{Theme.Safe(outcome.Text)}[/]"));
        }

        return Theme.Section("Sign in to Groundhog", new Rows(rows), focus: true);
    }
}
