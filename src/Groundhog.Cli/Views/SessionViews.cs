using Groundhog.Agent;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Groundhog.Cli.Views;

/// <summary>The live view of a local session: the foreground host and the running-agent screen share it.</summary>
internal static class SessionViews
{
    internal static readonly string[] Tabs = ["Overview", "Requests", "Statistics"];

    internal static IRenderable Dashboard(SessionSnapshot session, int tab, ViewSize size, DateTimeOffset now)
    {
        var tabs = new Layout("tabs", TabBar(tab)).Size(1);
        var body = tab switch
        {
            1 => Requests(session, size.Height - 4, size, "Requests · newest first"),
            2 => Statistics(session, size, now),
            _ => Overview(session, size, now),
        };
        return new Layout("session").SplitRows(tabs, new Layout("content", body));
    }

    private static IRenderable TabBar(int selected)
    {
        var parts = Tabs.Select((name, i) => i == selected
            ? $"[bold {Theme.InkTag} on {Theme.SignalTag}] {i + 1} {name} [/]"
            : $"[{Theme.TextTag} on #16263A] {i + 1} {name} [/]");
        return new Markup(string.Join(" ", parts));
    }

    private static IRenderable Overview(SessionSnapshot session, ViewSize size, DateTimeOffset now)
    {
        var overviewHeight = 8;
        var events = Math.Clamp(session.Logs.Length, 1, 3);
        var eventsHeight = events + 3;
        if (size.Wide)
        {
            var top = new Layout("top").Size(overviewHeight).SplitColumns(
                new Layout("overview", Summary(session, now)).Ratio(3),
                new Layout("classes", Classes(session)).Ratio(2));
            var rows = Math.Max(1, size.Height - 1 - overviewHeight - eventsHeight - 3);
            return new Layout("overview").SplitRows(top,
                new Layout("requests", Requests(session, rows, size, "Live requests")),
                new Layout("events", Events(session, events)).Size(eventsHeight));
        }

        var narrowRows = Math.Max(1, size.Height - 1 - overviewHeight - 3);
        return new Layout("overview").SplitRows(
            new Layout("summary", Summary(session, now)).Size(overviewHeight),
            new Layout("requests", Requests(session, narrowRows, size, "Live requests")));
    }

    private static IRenderable Summary(SessionSnapshot session, DateTimeOffset now)
    {
        var grid = new Grid().AddColumn(new GridColumn { NoWrap = true, Width = 13 }).AddColumn();
        void Row(string label, string value) => grid.AddRow(new Markup(Theme.Dim(label)), new Markup(value));
        Row("Public URL", session.PublicAddress is { } url ? Theme.Accent(Theme.Safe(url.TrimEnd('/'))) : Theme.Dim("waiting for the service…"));
        Row("Local target", Theme.Safe(session.Target.TrimEnd('/')));
        Row("State", $"{Theme.State(session.State)} {Theme.Dim("· up " + Theme.Duration(now - session.Started))}");
        var rate = session.Requests == 0 ? "" : $" ({100d * session.Errors / session.Requests:0.##}%)";
        Row("Requests", $"{Theme.Number(session.Requests)} {Theme.Dim("·")} " + (session.Errors > 0
            ? $"[{Theme.RedTag}]{Theme.Number(session.Errors)} 5xx{rate}[/]" : Theme.Dim("no 5xx")));
        Row("HTTP p95", session.P95Ms is { } p95 ? $"{Theme.Milliseconds(p95)} {Theme.Dim("retained history")}" : Theme.Dim("—"));
        Row("Reconnects", session.Reconnects == 0 ? Theme.Dim("0") : $"[{Theme.AmberTag}]{session.Reconnects}[/]");
        return Theme.Section(session.Name, grid, focus: true);
    }

    private static IRenderable Classes(SessionSnapshot session)
    {
        var groups = session.Recent.GroupBy(r => r.Status / 100).OrderBy(g => g.Key).ToArray();
        if (groups.Length == 0)
        {
            return Theme.Section("Status classes", new Markup(Theme.Dim("No requests yet. Open the public URL to try it.")));
        }

        var chart = new BreakdownChart { ShowTags = true, ShowTagValues = true }.UseValueFormatter(v => Theme.Number((long)v));
        foreach (var group in groups)
        {
            chart.AddItem($"{group.Key}xx", group.Count(), Theme.ClassColor($"{group.Key}xx"));
        }

        var durations = session.Recent.Where(r => !r.WebSocket).Select(r => r.DurationMs).Order().ToArray();
        var median = durations.Length == 0 ? "—" : Theme.Milliseconds(durations[durations.Length / 2]);
        return Theme.Section("Status classes · retained", new Rows(chart, new Text(""),
            new Markup($"{Theme.Dim("p50")} {median}  {Theme.Dim("p95")} {(session.P95Ms is { } p95 ? Theme.Milliseconds(p95) : "—")}")));
    }

    internal static IRenderable Requests(SessionSnapshot session, int maxRows, ViewSize size, string title)
    {
        if (session.Recent.Length == 0)
        {
            return Theme.Section(title, new Markup(Theme.Dim("Requests appear here as they complete.")));
        }

        var table = Theme.Grid("Time (UTC)", "Method", "Path", "Status", "Duration");
        table.Columns[3].RightAligned();
        table.Columns[4].RightAligned();
        var pathWidth = Math.Max(10, size.Width - 48);
        foreach (var r in Enumerable.Reverse(session.Recent).Take(Math.Max(1, maxRows)))
        {
            var path = Theme.Safe(Theme.Clip(r.Path, pathWidth - (r.Error is null || !size.Wide ? 0 : r.Error.Length + 1)));
            if (r.Error is not null && size.Wide)
            {
                path += " " + Theme.Dim(Theme.Safe(r.Error));
            }

            table.AddRow(r.Time.ToString("HH:mm:ss"), r.WebSocket ? $"[{Theme.BlueTag}]WS[/]" : Theme.Safe(r.Method), path,
                Theme.StatusCode(r.Status), r.WebSocket ? Theme.Dim("socket") : Theme.Milliseconds(r.DurationMs));
        }

        return Theme.Section(title, table);
    }

    private static IRenderable Events(SessionSnapshot session, int rows)
    {
        if (session.Logs.Length == 0)
        {
            return Theme.Section("Connection events", new Markup(Theme.Dim("No connection events yet.")));
        }

        var table = new Table { Border = TableBorder.None, ShowHeaders = false, Expand = true };
        table.AddColumn(new TableColumn("") { NoWrap = true, Width = 9 });
        table.AddColumn(new TableColumn("") { NoWrap = true, Width = 15 });
        table.AddColumn(new TableColumn(""));
        foreach (var log in Enumerable.Reverse(session.Logs).Take(rows))
        {
            table.AddRow(Theme.Dim(log.Time.ToString("HH:mm:ss")), Theme.State(log.State), Theme.Safe(log.Reason ?? ""));
        }

        return Theme.Section("Connection events", table);
    }

    private static IRenderable Statistics(SessionSnapshot session, ViewSize size, DateTimeOffset now)
    {
        var totals = new Grid().AddColumn(new GridColumn { NoWrap = true, Width = 18 }).AddColumn(new GridColumn().RightAligned());
        void Row(string label, string value) => totals.AddRow(new Markup(Theme.Dim(label)), new Markup(value));
        var http = session.Recent.Where(r => !r.WebSocket).Select(r => r.DurationMs).Order().ToArray();
        Row("Completed requests", Theme.Number(session.Requests));
        Row("5xx responses", session.Errors == 0 ? "0" : $"[{Theme.RedTag}]{Theme.Number(session.Errors)}[/]");
        Row("5xx rate", session.Requests == 0 ? "—" : $"{100d * session.Errors / session.Requests:0.##}%");
        Row("HTTP p50", http.Length == 0 ? "—" : Theme.Milliseconds(http[http.Length / 2]));
        Row("HTTP p95", session.P95Ms is { } p95 ? Theme.Milliseconds(p95) : "—");
        Row("Slowest retained", http.Length == 0 ? "—" : Theme.Milliseconds(http[^1]));
        Row("WebSockets", Theme.Number(session.Recent.Count(r => r.WebSocket)));
        Row("Reconnects", Theme.Number(session.Reconnects));
        Row("Uptime", Theme.Duration(now - session.Started));

        var classes = new BarChart().UseValueFormatter(v => Theme.Number((long)v));
        foreach (var group in session.Recent.GroupBy(r => r.Status / 100).OrderBy(g => g.Key))
        {
            classes.AddItem($"{group.Key}xx", group.Count(), Theme.ClassColor($"{group.Key}xx"));
        }

        var minutes = new BarChart().UseValueFormatter(v => Theme.Number((long)v));
        for (var minute = 9; minute >= 0; minute--)
        {
            var from = now.AddMinutes(-minute - 1);
            var to = now.AddMinutes(-minute);
            minutes.AddItem(minute == 0 ? "now" : $"-{minute}m", session.Recent.Count(r => r.Time > from && r.Time <= to), Theme.Signal);
        }

        IRenderable classesPanel = Theme.Section("Status classes · retained",
            session.Recent.Length == 0 ? new Markup(Theme.Dim("No requests yet.")) : classes);
        IRenderable minutesPanel = Theme.Section("Requests per minute", minutes);
        var footnote = new Markup(Theme.Dim("Totals cover the session · the last 1,000 requests are retained · WebSocket lifetimes are excluded from HTTP latency"));
        if (size.Wide)
        {
            return new Layout("stats").SplitRows(
                new Layout("row").SplitColumns(
                    new Layout("totals", Theme.Section("Session totals", totals)).Ratio(2),
                    new Layout("classes", classesPanel).Ratio(3)).Size(11),
                new Layout("minutes", minutesPanel),
                new Layout("note", footnote).Size(1));
        }

        return new Layout("stats").SplitRows(
            new Layout("totals", Theme.Section("Session totals", totals)).Size(11),
            new Layout("classes", classesPanel),
            new Layout("note", footnote).Size(2));
    }
}
