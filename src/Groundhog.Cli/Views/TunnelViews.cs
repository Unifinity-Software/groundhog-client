using Groundhog.Cli.Interactive;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Groundhog.Cli.Views;

/// <summary>The tunnel detail screen's model. <see cref="Insight"/> is null while loading.</summary>
internal sealed record TunnelDetailModel(string Workspace, string Name, TunnelInsight? Insight, string? Problem,
    LocalAgent? LocalAgent, DateTimeOffset Now, LiveState? Live = null);

internal static class TunnelViews
{
    /// <summary>The tunnel inventory as a table, optionally with a highlighted row.</summary>
    internal static IRenderable Table(IReadOnlyList<TunnelInfo> tunnels, int maxRows, ViewSize size, int? selected = null)
    {
        var (start, count) = Window(tunnels.Count, selected ?? 0, maxRows);
        var widest = Enumerable.Range(start, count).Select(i => Host(tunnels[i].Url).Length).DefaultIfEmpty(0).Max();
        return new FittedTable(host => Table(tunnels, start, count, size, selected, host), widest);
    }

    // Columns drop out as the width shrinks; addresses are clipped to the budget FittedTable finds, so numbers never wrap.
    private static Table Table(IReadOnlyList<TunnelInfo> tunnels, int start, int count, ViewSize size, int? selected, int hostWidth)
    {
        var full = size.Width >= 110;
        var table = Theme.Grid(full ? ["", "Tunnel", "State", "Public URL", "Req/min", "Req/hour", "Data today"]
            : ["", "Tunnel", "Public URL", "Req/h", "Today"]);
        table.Columns[0].Width = 1;
        for (var index = full ? 4 : 3; index < table.Columns.Count; index++)
        {
            table.Columns[index].RightAligned();
        }

        for (var i = start; i < start + count; i++)
        {
            var t = tunnels[i];
            var pointer = i == selected ? $"[bold {Theme.SignalTag}]▶[/]" : full ? " " : Theme.Dot(t.State);
            var name = new Markup(i == selected ? $"[bold {Theme.SignalTag}]{Theme.Safe(t.Name)}[/]" : Theme.Strong(Theme.Safe(t.Name)))
            {
                Overflow = Overflow.Ellipsis,
            };
            var host = Theme.Cell(Theme.Clip(Host(t.Url), hostWidth));
            var data = Theme.Bytes(t.BytesInToday + t.BytesOutToday);
            if (full)
            {
                table.AddRow(new Markup(pointer), name, new Markup(Theme.State(t.State)), host, new Text(Theme.Number(t.RequestsLastMinute)),
                    new Text(Theme.Number(t.RequestsLastHour)), new Text(data));
            }
            else
            {
                table.AddRow(new Markup(pointer), name, host, new Text(Theme.Number(t.RequestsLastHour)), new Text(data));
            }
        }

        if (count < tunnels.Count)
        {
            table.Caption = new TableTitle(Theme.Dim($"{start + 1}–{start + count} of {tunnels.Count}"));
        }

        return table;
    }

    /// <summary>A full tunnel dashboard: overview, status-class breakdown, traffic per minute and the request log.</summary>
    internal static IRenderable Detail(TunnelDetailModel model, ViewSize size)
    {
        if (model.Insight is null)
        {
            var text = model.Problem is null ? Theme.Dim($"Loading {Theme.Safe(model.Name)}…") : $"[{Theme.RedTag}]{Theme.Safe(model.Problem)}[/]";
            return Theme.Section(model.Name, new Markup(text));
        }

        var insight = model.Insight;
        var overview = Overview(model);
        var breakdown = Breakdown(insight.Usage, compact: !size.Wide);
        var overviewHeight = (size.Wide ? 9 : 8) + (insight.Access is null ? 0 : 1);
        if (size.Wide)
        {
            var top = new Layout("top").Size(overviewHeight).SplitColumns(
                new Layout("overview", overview).Ratio(2),
                new Layout("status", breakdown).Ratio(1));
            var requestRows = Math.Max(1, size.Height - overviewHeight - 4);
            return new Layout("detail").SplitRows(top, new Layout("requests", Requests(insight, model.Live, requestRows, size)));
        }

        var narrowRows = Math.Max(1, size.Height - overviewHeight - 4 - 4);
        return new Layout("detail").SplitRows(
            new Layout("overview", overview).Size(overviewHeight),
            new Layout("status", breakdown).Size(4),
            new Layout("requests", Requests(insight, model.Live, narrowRows, size)));
    }

    private static IRenderable Overview(TunnelDetailModel model)
    {
        var t = model.Insight!.Tunnel;
        var grid = new Spectre.Console.Grid().AddColumn(new GridColumn { NoWrap = true, Width = 13 }).AddColumn();
        void Row(string label, string value) => grid.AddRow(new Markup(Theme.Dim(label)), new Markup(value));
        Row("Public URL", $"[link={Theme.Safe(t.Url)}]{Theme.Accent(Theme.Safe(t.Url))}[/]");
        Row("State", $"{Theme.State(t.State)} {Theme.Dim("since " + Theme.Duration(model.Now - t.Since) + " ago")}");
        Row("Requests", $"{Theme.Number(t.RequestsLastMinute)} {Theme.Dim("last minute")} · {Theme.Number(t.RequestsLastHour)} {Theme.Dim("last hour")}");
        Row("Data today", $"{Theme.Bytes(t.BytesInToday)} {Theme.Dim("in")} · {Theme.Bytes(t.BytesOutToday)} {Theme.Dim("out")}");
        Row("This machine", model.LocalAgent is { } agent
            ? $"{Theme.State(agent.Snapshot.State)} {Theme.Dim("→ " + Theme.Safe(agent.Snapshot.Target))}"
            : Theme.Dim("not hosted from here (H to host)"));
        if (model.Insight.Access is { } access)
        {
            Row("Visitors", $"{Theme.Safe(VisitorAccessCommands.Summary(access))} {Theme.Dim(access.ManagedBy == VisitorAccessCommands.ManagedByDashboard ? "· set in the dashboard" : "· set by the agent")}");
        }

        Row("Actions", Actions(t.Actions));
        return Theme.Section(t.Name, grid, focus: true);
    }

    internal static string Actions(IReadOnlyCollection<string> permitted)
    {
        var keys = new List<string> { Key("O", "open"), Key("C", "copy"), Key("H", "host") };
        if (permitted.Contains("rename"))
        {
            keys.Add(Key("N", "rename"));
        }

        if (permitted.Contains("rotateKey"))
        {
            keys.Add(Key("A", "add key"));
            keys.Add(Key("K", "rotate"));
        }

        if (permitted.Contains("revokeKey"))
        {
            keys.Add(Key("X", "revoke"));
        }

        if (permitted.Contains("delete"))
        {
            keys.Add(Key("D", "delete"));
        }

        return string.Join(" ", keys);

        static string Key(string key, string label) => $"[bold {Theme.SignalTag}]{key}[/] {Theme.Dim(label)}";
    }

    private static IRenderable Breakdown(UsageSummary? usage, bool compact)
    {
        if (usage is null || usage.ByClass.Values.Sum() == 0)
        {
            return Theme.Section("Status classes", new Markup(Theme.Dim(usage is null ? "Usage is unavailable." : "No stored requests in this window.")));
        }

        var chart = new BreakdownChart { ShowTags = true, ShowTagValues = true, Expand = true }
            .UseValueFormatter(v => Theme.Number((long)v));
        foreach (var (statusClass, total) in usage.ByClass.Where(c => c.Value > 0))
        {
            chart.AddItem(statusClass, total, Theme.ClassColor(statusClass));
        }

        var minutes = usage.PerMinute.Length;
        var rows = new List<IRenderable> { chart };
        if (minutes > 0 && !compact)
        {
            rows.Add(new Text(""));
            rows.Add(new Sparkline(usage.PerMinute, Theme.Signal));
            rows.Add(new Markup(Theme.Dim($"Requests per minute · {usage.From:HH:mm}–{usage.To:HH:mm} UTC")));
        }

        return Theme.Section("Status classes", new Rows(rows));
    }

    private static IRenderable Requests(TunnelInsight insight, LiveState? live, int maxRows, ViewSize size)
    {
        var title = live?.Status switch
        {
            LiveStatus.Live => "Recent requests · live",
            LiveStatus.Reconnecting => "Recent requests · reconnecting…",
            _ => "Recent requests · service log",
        };

        if (insight.Requests is null)
        {
            return Theme.Section("Recent requests", new Markup(Theme.Dim(Theme.Safe(insight.Problem ?? "The request log is unavailable."))));
        }

        if (insight.Requests.Length == 0)
        {
            return Theme.Section(title, new Markup(Theme.Dim(live?.Status == LiveStatus.Live
                ? "No requests recorded yet. New requests appear here as they finish." : "No requests recorded yet.")));
        }

        var table = Theme.Grid("Time (UTC)", "Method", "Path", "Status", "Duration");
        table.Columns[3].RightAligned();
        table.Columns[4].RightAligned();
        table.Columns[2].NoWrap = true;
        var pathWidth = Math.Max(10, size.Width - 48);
        var stopped = live is { Status: LiveStatus.Stopped, Message: not null };
        // The caption takes one of the rows, so it stays visible.
        foreach (var r in insight.Requests.OrderByDescending(r => r.Completed).Take(Math.Max(1, stopped ? maxRows - 1 : maxRows)))
        {
            table.AddRow(r.Completed.ToString("HH:mm:ss"), Theme.Safe(r.Method), Theme.Safe(Theme.Clip(r.Path, pathWidth)),
                Theme.StatusCode(r.StatusCode), Theme.Milliseconds(r.DurationMs));
        }

        if (stopped)
        {
            table.Caption = new TableTitle(Theme.Dim("Not updating: " + Theme.Safe(live!.Message!) + " Press R to reload."));
        }

        return Theme.Section(title, table);
    }

    internal static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;

    /// <summary>The first row and row count of a scrolling window that keeps <paramref name="selected"/> visible.</summary>
    internal static (int Start, int Count) Window(int total, int selected, int rows)
    {
        rows = Math.Max(1, rows);
        if (total <= rows)
        {
            return (0, total);
        }

        var start = Math.Clamp(selected - rows / 2, 0, total - rows);
        return (start, rows);
    }
}

/// <summary>A one-line chart of values using block heights; gaps (missing values) are drawn as dots.</summary>
internal sealed class Sparkline(IReadOnlyList<long?> values, Color color) : Renderable
{
    private const string Blocks = "▁▂▃▄▅▆▇█";

    protected override Measurement Measure(RenderOptions options, int maxWidth) => new(1, maxWidth);

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        // Merge neighbouring minutes so the series fits the width.
        var buckets = Math.Max(1, (int)Math.Ceiling(values.Count / (double)Math.Max(1, maxWidth)));
        var merged = values.Chunk(buckets).Select(c => c.All(v => v is null) ? (long?)null : c.Sum(v => v ?? 0)).ToArray();
        var max = Math.Max(1, merged.Max(v => v ?? 0));
        var text = new System.Text.StringBuilder();
        foreach (var value in merged)
        {
            text.Append(value is null ? '·' : value == 0 ? '▁' : Blocks[(int)Math.Round((Blocks.Length - 1) * (double)value / max)]);
        }

        yield return new Segment(text.ToString(), new Style(color));
    }
}

/// <summary>
/// A table whose one clippable column (a public address) shrinks until the whole table fits its width. Spectre
/// otherwise narrows every column of an over-wide table, which wraps the short number columns.
/// </summary>
internal sealed class FittedTable(Func<int, Table> build, int natural) : Renderable
{
    protected override Measurement Measure(RenderOptions options, int maxWidth) =>
        ((IRenderable)Fit(options, maxWidth)).Measure(options, maxWidth);

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
        ((IRenderable)Fit(options, maxWidth)).Render(options, maxWidth);

    private Table Fit(RenderOptions options, int maxWidth)
    {
        var width = natural;
        var table = build(width);
        while (width > 8 && ((IRenderable)table).Measure(options, maxWidth).Max > maxWidth)
        {
            table = build(--width);
        }

        return table;
    }
}
