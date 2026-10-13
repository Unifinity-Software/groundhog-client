using Spectre.Console;
using Spectre.Console.Rendering;

namespace Groundhog.Cli.Views;

/// <summary>The header bar's content: who is signed in, where, and the path to the current screen.</summary>
internal sealed record ChromeModel(string Version, string? Account, string? Workspace, IReadOnlyList<string> Path)
{
    /// <summary>The stand-alone host view shows no account details.</summary>
    internal bool Standalone { get; init; }

    /// <summary>A newer CLI version, advertised next to the current one with the key that installs it.</summary>
    internal string? Update { get; init; }
}

/// <summary>One key hint in the footer bar, for example <c>Esc</c> <c>Back</c>.</summary>
internal sealed record KeyHint(string Keys, string Label);

internal enum StatusKind { Info, Success, Warning, Error }

/// <summary>The one-line message under the body: the outcome of the last action, or context for the screen.</summary>
internal sealed record StatusMessage(string Text, StatusKind Kind = StatusKind.Info);

/// <summary>The application frame every screen is drawn in: header bar, body, status line and key-hint footer.</summary>
internal static class Chrome
{
    internal const int HeaderHeight = 1;
    internal const int FooterHeight = 2;

    /// <summary>The size of the body area for a console of the given size.</summary>
    internal static ViewSize Body(int width, int height) =>
        new(Math.Max(20, width - 2), Math.Max(4, height - HeaderHeight - FooterHeight));

    internal static IRenderable Shell(ChromeModel chrome, IRenderable body, IReadOnlyList<KeyHint> keys, StatusMessage? status)
    {
        var layout = new Layout("app").SplitRows(
            new Layout("header", Header(chrome)).Size(HeaderHeight),
            new Layout("body", new Padder(body, new Padding(1, 0, 1, 0))),
            new Layout("status", Status(status)).Size(1),
            new Layout("footer", new KeyBar(keys)).Size(1));
        return new Backdrop(layout);
    }

    internal static IRenderable Header(ChromeModel chrome)
    {
        var path = string.Concat(chrome.Path.Select(p => $" [{Theme.MutedTag}]›[/] [{Theme.TextTag}]{Theme.Safe(p)}[/]"));
        var update = chrome.Update is null ? "" : $" [{Theme.AmberTag}]· {Theme.Safe(chrome.Update)} available, press U[/]";
        var left = $" [bold {Theme.SignalTag}]◆ GROUNDHOG[/] [{Theme.MutedTag}]{Theme.Safe(chrome.Version)}[/]{update}{path}";
        var right = chrome.Standalone ? $"[{Theme.MutedTag}]live session[/] "
            : chrome.Account is null
            ? $"[{Theme.MutedTag}]not signed in[/] "
            : $"[{Theme.TextTag}]{Theme.Safe(chrome.Account)}[/]"
                + (chrome.Workspace is null ? "" : $" [{Theme.MutedTag}]·[/] [{Theme.SignalTag}]{Theme.Safe(chrome.Workspace)}[/]") + " ";
        return new Bar(left, right, new Style(Theme.Text, Theme.Raised));
    }

    internal static IRenderable Status(StatusMessage? status)
    {
        if (status is null)
        {
            return new Text("");
        }

        var (glyph, color) = status.Kind switch
        {
            StatusKind.Success => ("✓", Theme.GreenTag),
            StatusKind.Warning => ("!", Theme.AmberTag),
            StatusKind.Error => ("✗", Theme.RedTag),
            _ => ("●", Theme.SignalTag),
        };
        return new Bar($" [{color}]{glyph}[/] {Theme.Safe(status.Text)}", null, new Style(Theme.Text));
    }

    /// <summary>A centred modal panel used for text entry, confirmations and progress.</summary>
    internal static IRenderable Modal(string title, IRenderable content, ViewSize size, bool danger = false)
    {
        var panel = new Panel(content)
        {
            Border = BoxBorder.Heavy,
            BorderStyle = new Style(danger ? Theme.Red : Theme.Signal),
            Header = new PanelHeader($"[bold {(danger ? Theme.RedTag : Theme.SignalTag)}] {Theme.Safe(title)} [/]"),
            Padding = new Padding(2, 1, 2, 1),
            Width = Math.Min(size.Width, 72),
        };
        return new Align(panel, HorizontalAlignment.Center, VerticalAlignment.Middle);
    }

    /// <summary>A text field: the value, a block cursor, then dim filler so the field reads as an input box.</summary>
    internal static IRenderable Input(string label, string value, string? hint, string? error, int width)
    {
        var field = Math.Max(10, Math.Min(width, 64) - 4);
        var shown = value.Length >= field ? "…" + value[^(field - 2)..] : value;
        var rows = new List<IRenderable>
        {
            new Markup($"[{Theme.TextTag}]{Theme.Safe(label)}[/]"),
            new Bar($" [bold {Theme.WhiteTag}]{Theme.Safe(shown)}[/][{Theme.SignalTag}]█[/]", null, new Style(Theme.Text, Theme.Raised)),
        };
        if (hint is not null)
        {
            rows.Add(new Markup(Theme.Dim(Theme.Safe(hint))));
        }

        if (error is not null)
        {
            rows.Add(new Markup($"[{Theme.RedTag}]✗ {Theme.Safe(error)}[/]"));
        }

        rows.Add(new Text(""));
        rows.Add(new Markup($"{KeyCap("Enter")} Confirm   {KeyCap("Esc")} Cancel"));
        return new Rows(rows);
    }

    internal static string KeyCap(string key) => $"[bold {Theme.InkTag} on {Theme.SignalTag}] {Theme.Safe(key)} [/]";
}

/// <summary>The width and height available to a screen's body.</summary>
internal readonly record struct ViewSize(int Width, int Height)
{
    /// <summary>Wide terminals show side-by-side panels and the menu's preview pane.</summary>
    internal bool Wide => Width >= 98;
}

/// <summary>
/// A full-width, single-line bar: left markup, right-aligned markup and a fill style whose background extends across
/// the whole line. The right part is dropped, then the left part trimmed, when the line is too narrow.
/// </summary>
internal sealed class Bar(string left, string? right, Style fill) : Renderable
{
    protected override Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var leftSegments = Line(new Markup(left), options);
        var rightSegments = right is null ? [] : Line(new Markup(right), options);
        var leftWidth = Segment.CellCount(leftSegments);
        var rightWidth = Segment.CellCount(rightSegments);
        if (leftWidth + rightWidth + 1 > maxWidth)
        {
            rightSegments = [];
            rightWidth = 0;
        }

        if (leftWidth > maxWidth)
        {
            leftSegments = Segment.Truncate(leftSegments, maxWidth).ToList();
            leftWidth = Segment.CellCount(leftSegments);
        }

        foreach (var segment in leftSegments)
        {
            yield return Backdrop.Paint(segment, fill);
        }

        yield return new Segment(new string(' ', Math.Max(0, maxWidth - leftWidth - rightWidth)), fill);
        foreach (var segment in rightSegments)
        {
            yield return Backdrop.Paint(segment, fill);
        }
    }

    // Markup without line breaks renders as one line when given room; the bar then trims it to the real width.
    private static List<Segment> Line(IRenderable markup, RenderOptions options) =>
        markup.Render(options, 4096).Where(s => !s.IsLineBreak).ToList();
}

/// <summary>The footer: as many key hints as fit the width, in order of importance.</summary>
internal sealed class KeyBar(IReadOnlyList<KeyHint> keys) : Renderable
{
    protected override Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var markup = new System.Text.StringBuilder(" ");
        var used = 1;
        foreach (var key in keys)
        {
            var width = key.Keys.Length + 2 + 1 + key.Label.Length + 2;
            if (used + width > maxWidth)
            {
                continue;
            }

            markup.Append(Chrome.KeyCap(key.Keys)).Append(' ').Append($"[{Theme.TextTag}]{Theme.Safe(key.Label)}[/]").Append("  ");
            used += width;
        }

        return ((IRenderable)new Bar(markup.ToString(), null, new Style(Theme.Text, Theme.Raised))).Render(options, maxWidth);
    }
}

/// <summary>
/// Paints the whole frame in the brand ink: segments without a background get ink, segments without a foreground get
/// the light text colour, and each line is padded to the full width. Owning the background keeps the app legible on
/// light terminal themes. Colours vanish together when colour is disabled.
/// </summary>
internal sealed class Backdrop(IRenderable content) : Renderable
{
    private static readonly Style _fill = new(Theme.Text, Theme.Ink);

    protected override Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var lines = Segment.SplitLines(content.Render(options, maxWidth), maxWidth, options.Height);
        for (var i = 0; i < lines.Count; i++)
        {
            var width = 0;
            foreach (var segment in lines[i])
            {
                if (segment.IsControlCode)
                {
                    yield return segment;
                    continue;
                }

                width += segment.CellCount();
                yield return Paint(segment, _fill);
            }

            if (width < maxWidth)
            {
                yield return new Segment(new string(' ', maxWidth - width), _fill);
            }

            if (i < lines.Count - 1)
            {
                yield return Segment.LineBreak;
            }
        }
    }

    internal static Segment Paint(Segment segment, Style fill)
    {
        if (segment.IsControlCode || segment.IsLineBreak)
        {
            return segment;
        }

        var style = segment.Style;
        var foreground = style.Foreground == Color.Default ? fill.Foreground : style.Foreground;
        var background = style.Background == Color.Default ? fill.Background : style.Background;
        return new Segment(segment.Text, new Style(foreground, background, style.Decoration), segment.Link);
    }
}
