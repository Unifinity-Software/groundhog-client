using Groundhog.Cli.Interactive;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Groundhog.Cli.Views;

/// <summary>What the home screen and the menu summarise. Null members are still loading.</summary>
internal sealed record Dashboard(AccountState? Account, TunnelInfo[]? Tunnels, string? TunnelsProblem, LocalAgent[]? Agents,
    TunnelProfile[] Profiles)
{
    internal static Dashboard Empty { get; } = new(null, null, null, null, []);

    internal string Summary()
    {
        if (Account is null)
        {
            return "Loading…";
        }

        var parts = new List<string>();
        parts.Add(Account.SignedIn ? $"{Account.Account!.Email}" : Account.Problem ?? "Not signed in.");
        if (Account.Workspace is { } workspace && Account.SignedIn)
        {
            parts.Add($"workspace {workspace}");
        }

        if (Tunnels is { } tunnels && Account.SignedIn)
        {
            parts.Add($"{Plural(tunnels.Length, "tunnel")}, {tunnels.Count(t => t.State == "Connected")} connected");
        }

        if (Agents is { } agents)
        {
            parts.Add($"{Plural(agents.Length, "local agent")}");
        }

        return string.Join(" · ", parts);
    }

    internal static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}

/// <summary>One entry of the main menu.</summary>
internal sealed record MenuEntry(string Title, string Description);

internal static class HomeViews
{
    /// <summary>The main menu: a navigation list and, on wide terminals, a live preview of the highlighted entry.</summary>
    internal static IRenderable Menu(IReadOnlyList<MenuEntry> entries, int selected, Dashboard dashboard, IRenderable? preview, ViewSize size)
    {
        var items = new List<IRenderable>();
        for (var i = 0; i < entries.Count; i++)
        {
            // The number is the entry's shortcut: pressing it opens the entry directly.
            var number = i < 9 ? $"{i + 1} " : "";
            items.Add(i == selected
                ? new Bar($" [bold]▶ {Theme.Safe(entries[i].Title)}[/]", number, new Style(Theme.Ink, Theme.Signal))
                : new Bar($"   {Theme.Safe(entries[i].Title)}", Theme.Dim(number), new Style(Theme.Text)));
        }

        var description = new Markup(Theme.Dim(Theme.Safe(entries[selected].Description)));
        if (size.Wide && preview is not null)
        {
            var nav = Theme.Section("Menu", new Rows(items.Append(new Text("")).Append(description)), focus: true);
            return new Layout("menu").SplitColumns(
                new Layout("nav", nav).Size(30),
                new Layout("preview", new Padder(preview, new Padding(1, 0, 0, 0))));
        }

        var menuHeight = Math.Min(entries.Count + 4, size.Height - 4);
        return new Layout("menu").SplitRows(
            new Layout("nav", Theme.Section("Menu", new Rows(items.Append(new Text("")).Append(description)), focus: true)).Size(menuHeight),
            new Layout("summary", Theme.Section("At a glance", Glance(dashboard))));
    }

    /// <summary>A compact summary for narrow terminals, where the menu has no preview pane.</summary>
    internal static IRenderable Glance(Dashboard dashboard)
    {
        var grid = new Spectre.Console.Grid().AddColumn(new GridColumn { NoWrap = true, Width = 13 }).AddColumn();
        void Row(string label, string value) => grid.AddRow(new Markup(Theme.Dim(label)), new Markup(value));
        var account = dashboard.Account;
        Row("Account", account is null ? Theme.Dim("loading…")
            : account.SignedIn ? Theme.Safe(account.Account!.Email) : $"[{Theme.AmberTag}]{Theme.Safe(account.Problem ?? "Not signed in.")}[/]");
        Row("Workspace", account?.SignedIn == true && account.Workspace is { } workspace ? Theme.Accent(Theme.Safe(workspace)) : Theme.Dim("—"));
        Row("Tunnels", dashboard.Tunnels is not { } tunnels ? Theme.Dim("loading…")
            : $"{tunnels.Length} {Theme.Dim("·")} [{Theme.GreenTag}]{tunnels.Count(t => t.State == "Connected")} connected[/]");
        Row("This machine", dashboard.Agents is not { } agents ? Theme.Dim("loading…") : Dashboard.Plural(agents.Length, "agent") + " running");
        return grid;
    }

    /// <summary>The home dashboard: account, tunnels, local agents and recent traffic.</summary>
    internal static IRenderable Home(Dashboard dashboard, ViewSize size, DateTimeOffset now, bool title = true)
    {
        var showTitle = title && size.Height >= 30 && size.Width >= 70;
        var agentRows = Math.Clamp(dashboard.Agents?.Length ?? 1, 1, 4);
        var agentsHeight = dashboard.Agents is { Length: > 0 } ? agentRows + 4 : 3;
        var topHeight = size.Wide ? 6 : 4;
        var rows = new List<Layout>();
        if (showTitle)
        {
            rows.Add(new Layout("title", new Padder(Title(), new Padding(1, 0, 0, 0))).Size(7));
        }

        if (size.Wide)
        {
            rows.Add(new Layout("top").Size(topHeight).SplitColumns(
                new Layout("account", Account(dashboard.Account, now, compact: false)).Ratio(1),
                new Layout("traffic", Traffic(dashboard)).Ratio(1)));
        }
        else
        {
            rows.Add(new Layout("account", Account(dashboard.Account, now, compact: true)).Size(topHeight));
        }

        var tunnelRows = Math.Max(1, size.Height - topHeight - agentsHeight - (showTitle ? 7 : 0) - 4);
        rows.Add(new Layout("tunnels", Tunnels(dashboard, tunnelRows, size)));
        rows.Add(new Layout("agents", Agents(dashboard.Agents, agentRows, now, size)).Size(agentsHeight));
        return new Layout("home").SplitRows(rows.ToArray());
    }

    internal static IRenderable Title() => new FigletBanner("GROUNDHOG", Theme.Signal);

    internal static IRenderable Account(AccountState? account, DateTimeOffset now, bool compact)
    {
        IRenderable content;
        if (account is null)
        {
            content = new Markup(Theme.Dim("Loading account…"));
        }
        else if (!account.SignedIn)
        {
            content = new Markup($"[{Theme.AmberTag}]{Theme.Safe(account.Problem ?? "Not signed in.")}[/] {Theme.Dim("Open Sign in to see remote tunnels.")}\n"
                + Theme.Dim("Hosting with a connection key works without signing in."));
        }
        else
        {
            var info = account.Account!;
            var workspace = account.CurrentWorkspace;
            var expires = account.Expires is { } at ? "expires in " + Theme.Duration(at - now) : "";
            content = compact
                ? new Markup($"{Theme.Strong(Theme.Safe(info.DisplayName))} {Theme.Dim(Theme.Safe(info.Email))}\n"
                    + (workspace is null ? $"[{Theme.AmberTag}]Choose a workspace in Workspaces.[/]"
                        : $"Workspace {Theme.Accent(Theme.Safe(workspace.Slug))} {Theme.Dim("· " + Theme.Safe(workspace.Role) + " · session " + expires)}"))
                : new Markup($"{Theme.Strong(Theme.Safe(info.DisplayName))}\n{Theme.Dim(Theme.Safe(info.Email))}\n"
                    + (workspace is null ? $"[{Theme.AmberTag}]Choose a workspace in Workspaces.[/]"
                        : $"Workspace {Theme.Accent(Theme.Safe(workspace.Slug))} {Theme.Dim("· " + Theme.Safe(workspace.Name) + " · " + Theme.Safe(workspace.Role))}")
                    + $"\n{Theme.Dim("Session " + expires)}");
        }

        return Theme.Section("Account", content);
    }

    internal static IRenderable Traffic(Dashboard dashboard)
    {
        var busiest = (dashboard.Tunnels ?? []).OrderByDescending(t => t.RequestsLastHour).Take(4).ToArray();
        IRenderable content = dashboard.Tunnels is null ? new Markup(Theme.Dim("Loading…"))
            : busiest.Length == 0 || busiest.All(t => t.RequestsLastHour == 0) ? new Markup(Theme.Dim("No requests in the last hour."))
            : new BarChart { Width = null, ShowValues = true }
                .AddItems(busiest, t => new BarChartItem(Theme.Clip(t.Name, 14), t.RequestsLastHour, t.State == "Connected" ? Theme.Signal : Theme.Muted))
                .UseValueFormatter(v => Theme.Number((long)v));
        return Theme.Section("Requests · last hour", content);
    }

    internal static IRenderable Tunnels(Dashboard dashboard, int maxRows, ViewSize size, int? selected = null)
    {
        var workspace = dashboard.Account?.Workspace;
        var title = workspace is null ? "Tunnels" : $"Tunnels · {workspace}";
        if (dashboard.Tunnels is null)
        {
            return Theme.Section(title, new Markup(Theme.Dim("Loading tunnels…")));
        }

        if (dashboard.Tunnels.Length == 0)
        {
            var message = dashboard.TunnelsProblem ?? "No tunnels in this workspace yet. Press N in Tunnels to create one.";
            return Theme.Section(title, new Markup(Theme.Dim(Theme.Safe(message))));
        }

        return Theme.Section(title, TunnelViews.Table(dashboard.Tunnels, maxRows, size, selected), focus: selected is not null);
    }

    internal static IRenderable Agents(LocalAgent[]? agents, int maxRows, DateTimeOffset now, ViewSize size, int? selected = null)
    {
        if (agents is null)
        {
            return Theme.Section("Running on this machine", new Markup(Theme.Dim("Looking for local agents…")));
        }

        if (agents.Length == 0)
        {
            return Theme.Section("Running on this machine",
                new Markup(Theme.Dim("No local agents. Host a tunnel or start a saved profile to see it here.")));
        }

        return Theme.Section("Running on this machine", LocalViews.AgentTable(agents, maxRows, now, size, selected), focus: selected is not null);
    }

    /// <summary>Keys, commands and version.</summary>
    internal static IRenderable Help(string version, ViewSize size)
    {
        var keys = new Table { Border = TableBorder.None, Expand = false, ShowHeaders = false };
        keys.AddColumn(new TableColumn("") { NoWrap = true, Width = 16 });
        keys.AddColumn(new TableColumn(""));
        void Group(string name) => keys.AddRow(new Markup($"[bold {Theme.SignalTag}]{name}[/]"), new Text(""));
        void Key(string key, string action) => keys.AddRow(new Markup($"[bold {Theme.WhiteTag}]{Theme.Safe(key)}[/]"), new Markup(Theme.Safe(action)));
        Group("Everywhere");
        Key("↑ ↓  j k", "Move the highlight");
        Key("Enter", "Open the highlighted item");
        Key("1–9", "Open a menu entry by its number");
        Key("Esc  Backspace", "Back to the previous screen");
        Key("R", "Refresh");
        Key("?  F1", "This help");
        Key("Q", "Quit (Ctrl+C also quits)");
        Key("U", "Install a newer CLI when the top bar shows one");
        Group("Tunnel detail");
        Key("O  C", "Open the public URL · copy it");
        Key("H", "Host the tunnel from this machine");
        Key("N  A  K  X  D", "Rename · add key · rotate key · revoke keys · delete");
        Group("Live session");
        Key("1  2  3", "Overview · requests · statistics");
        Key("Esc  Q", "Stop hosting and return");
        Key("S", "Stop a background agent (Running agents)");

        var about = new Rows(
            new Markup($"[bold {Theme.SignalTag}]Groundhog CLI[/] {Theme.Dim(Theme.Safe(version))}"),
            new Markup("HTTPS tunnels for localhost."),
            new Markup(Theme.Dim("Service  ") + "https://groundhog.unifinity.com"),
            new Markup(Theme.Dim("Tunnels  ") + "https://<name>.groundhog.unifinity.com"),
            new Text(""),
            new Markup(Theme.Dim("Every screen has a command for scripts: groundhog --help.")),
            new Markup(Theme.Dim("Use --no-interactive to skip menus and --no-color for plain text.")));
        if (size.Wide)
        {
            var side = new Rows(new ColorOnly(GroundhogArt.Head()), new Text(""), about);
            return new Layout("help").SplitColumns(
                new Layout("keys", Theme.Section("Keys", keys)).Ratio(3),
                new Layout("about", Theme.Section("About", side)).Ratio(2));
        }

        // Narrow terminals keep the keys and one line about the version.
        return new Rows(Theme.Section("Keys", keys),
            new Markup($" [bold {Theme.SignalTag}]Groundhog CLI[/] {Theme.Dim(Theme.Safe(version) + " · https://groundhog.unifinity.com")}"));
    }
}

/// <summary>The launch screen in the idiom of an 8-bit home computer: banner, account summary, READY and a cursor.</summary>
internal static class BootView
{
    internal static IRenderable Render(string version, string? status, bool ready, bool cursor, int width, int height, string? update = null)
    {
        var lines = new List<IRenderable>();
        if (height >= 26)
        {
            lines.Add(new Text(""));
            lines.Add(new ColorOnly(GroundhogArt.Head()));
        }

        lines.Add(new Text(""));
        lines.Add(Left($"[bold {Theme.SignalTag}]    **** GROUNDHOG CLI {Theme.Safe(version.ToUpperInvariant())} ****[/]"));
        lines.Add(Left($"[{Theme.TextTag}] HTTPS TUNNELS FOR LOCALHOST · GROUNDHOG.UNIFINITY.COM[/]"));
        lines.Add(Left($"[{Theme.TextTag}] {Theme.Safe(status ?? "CHECKING ACCOUNT…")}[/]"));
        lines.Add(new Text(""));
        if (update is not null)
        {
            lines.Add(Left($"[bold {Theme.AmberTag}] UPDATE AVAILABLE: {Theme.Safe(update.ToUpperInvariant())} · PRESS U TO INSTALL IT[/]"));
            lines.Add(new Text(""));
        }

        lines.Add(Left(ready ? $"[{Theme.TextTag}]READY.[/]" : " "));
        lines.Add(Left($"[bold {Theme.WhiteTag}]PRESS ENTER TO CONTINUE[/][{Theme.TextTag}] · Q TO QUIT[/]"));
        lines.Add(Left(cursor ? $"[{Theme.SignalTag}]█[/]" : " "));
        var panel = new Panel(new CenteredBlock(new Rows(lines)))
        {
            Border = BoxBorder.Heavy,
            BorderStyle = new Style(Theme.Signal),
            Expand = true,
            Height = height,
            Padding = new Padding(2, 1, 2, 1),
        };
        return new Backdrop(panel);
    }

    // Lines keep their own left alignment inside the centred block, like text on an 8-bit screen.
    private static Markup Left(string markup) => new(markup) { Justification = Justify.Left };
}

/// <summary>The groundhog head as a Spectre canvas, from the generated pixel map.</summary>
internal static class GroundhogArt
{
    internal static Canvas Head()
    {
        var rows = GroundhogPixels.Rows;
        var canvas = new Canvas(rows[0].Length, rows.Length);
        for (var y = 0; y < rows.Length; y++)
        {
            for (var x = 0; x < rows[y].Length; x++)
            {
                Color? color = rows[y][x] switch
                {
                    'n' => Theme.Ink,
                    'c' => Theme.Signal,
                    'w' => Theme.White,
                    _ => null,
                };
                if (color is { } pixel)
                {
                    canvas.SetPixel(x, y, pixel);
                }
            }
        }

        return canvas;
    }
}

/// <summary>Renders its content only when colour is available; pixel art is noise without it.</summary>
internal sealed class ColorOnly(IRenderable content) : Renderable
{
    protected override Measurement Measure(RenderOptions options, int maxWidth) =>
        options.ColorSystem == ColorSystem.NoColors ? new(0, 0) : content.Measure(options, maxWidth);

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
        options.ColorSystem == ColorSystem.NoColors ? [] : content.Render(options, maxWidth);
}

/// <summary>
/// A FIGlet banner that falls back to spaced bold text when the terminal is too narrow for it or it would need more than
/// <paramref name="maxLines"/> lines.
/// </summary>
internal sealed class FigletBanner(string text, Color color, int maxLines = 6) : Renderable
{
    protected override Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(maxWidth, 1), maxWidth);

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        IRenderable figlet = new FigletText(text) { Color = color };
        // Fit when every word fits on a line (no word broken mid-way) and the wrapped banner stays within maxLines.
        var widest = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Max(word => Segment.SplitLines(((IRenderable)new FigletText(word)).Render(options, 4096)).Max(l => l.CellCount()));
        var lines = Segment.SplitLines(figlet.Render(options, maxWidth)).Count(l => l.CellCount() > 0 || l.Count > 0);
        var fits = widest < maxWidth && lines <= maxLines;
        IRenderable chosen = fits
            ? figlet
            : new Markup($"[bold]{Markup.Escape(string.Join(' ', text.ToCharArray()))}[/]", new Style(color));
        return chosen.Render(options, maxWidth);
    }
}

/// <summary>Centres a block as a whole while its lines keep their left edge, unlike <see cref="Align"/>, which centres each line.</summary>
internal sealed class CenteredBlock(IRenderable content) : Renderable
{
    protected override Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var lines = Segment.SplitLines(content.Render(options, maxWidth));
        var block = lines.Count == 0 ? 0 : lines.Max(l => l.CellCount());
        var indent = Math.Max(0, (maxWidth - block) / 2);
        for (var i = 0; i < lines.Count; i++)
        {
            if (indent > 0)
            {
                yield return Segment.Padding(indent);
            }

            foreach (var segment in lines[i])
            {
                yield return segment;
            }

            if (i < lines.Count - 1)
            {
                yield return Segment.LineBreak;
            }
        }
    }
}
