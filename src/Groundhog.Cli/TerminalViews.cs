using Spectre.Console;
using Spectre.Console.Rendering;

namespace Groundhog.Cli;

internal static class TerminalViews
{
    internal static string Safe(string? value) => Markup.Escape(new string((value ?? "—")
        .Where(c => !char.IsControl(c)).ToArray()));

    internal static IRenderable Session(SessionSnapshot session, string view = "Overview", DateTimeOffset? now = null)
    {
        var details = new Table().Border(TableBorder.None).AddColumn("Field").AddColumn("Value");
        details.AddRow("State", Safe(session.State));
        details.AddRow("Public URL", Safe(session.PublicAddress));
        details.AddRow("Local target", Safe(session.Target));
        details.AddRow("Started (UTC)", session.Started.ToString("u"));
        var uptime = (now ?? DateTimeOffset.UtcNow) - session.Started;
        details.AddRow("Uptime", $"{Math.Max(0, (int)uptime.TotalDays)}d {uptime:hh\\:mm\\:ss}");
        details.AddRow("Completed requests / 5xx", $"{session.Requests} / {session.Errors}");
        details.AddRow("Session 5xx rate", session.Requests == 0 ? "—" : $"{100d * session.Errors / session.Requests:0.##}%");
        details.AddRow("Reconnect attempts", session.Reconnects.ToString());
        details.AddRow("HTTP p95 (retained history)", session.P95Ms is { } ms ? $"{ms:0.##} ms" : "—");
        if (session.Reason is not null)
        {
            details.AddRow("Last connection event", Safe(session.Reason));
        }

        var requests = Requests(session.Recent.TakeLast(10));
        var heading = $"Groundhog · {Safe(session.Name)} · {Safe(view)}";
        var content = view.Equals("Requests", StringComparison.OrdinalIgnoreCase) ? (IRenderable)requests : details;
        IRenderable extra = view.Equals("Overview", StringComparison.OrdinalIgnoreCase) ? requests : new Text("");
        if (view is "Statistics" or "stats" or "View statistics")
        {
            var chart = new BarChart().Label("Status classes · retained request history");
            foreach (var group in session.Recent.GroupBy(r => r.Status / 100).OrderBy(g => g.Key))
            { chart.AddItem($"{group.Key}xx", group.Count(), group.Key == 5 ? Color.Red : Color.Cyan); }
            extra = chart;
        }
        return new Rows(new Panel(content).Header(heading).RoundedBorder(), extra,
            new Text("Session totals · last 1,000 requests retained · WebSocket durations excluded from HTTP p95"));
    }

    internal static Table Requests(IEnumerable<RequestRecord> records)
    {
        var table = new Table().RoundedBorder().AddColumn("Time (UTC)").AddColumn("Method")
            .AddColumn("Path").AddColumn("Status").AddColumn("Duration");
        foreach (var r in records)
        {
            table.AddRow(r.Time.ToString("HH:mm:ss"), Safe(r.WebSocket ? "WS" : r.Method), Safe(r.Path),
                r.Status >= 500 ? $"[red]{r.Status}[/]" : r.Status.ToString(), $"{r.DurationMs:0.##} ms");
        }

        return table;
    }

    internal static Table Profiles(IEnumerable<TunnelProfile> profiles)
    {
        var table = new Table().RoundedBorder().AddColumn("Saved profile").AddColumn("Local target").AddColumn("Relay");
        foreach (var p in profiles)
        {
            table.AddRow(Safe(p.Name), Safe(p.Target), Safe(p.Relay ?? "Hosted service"));
        }

        return table;
    }
}
