using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Groundhog.Cli.Views;

/// <summary>
/// Brand colours, glyphs and formatting shared by every interactive screen. Spectre drops colours by itself when
/// <c>NO_COLOR</c> (or <c>--no-color</c>) is set, so every state also has a glyph or a word.
/// </summary>
internal static class Theme
{
    internal static readonly Color Ink = new(0x0D, 0x18, 0x21);
    internal static readonly Color Raised = new(0x16, 0x26, 0x34);
    internal static readonly Color Line = new(0x2F, 0x44, 0x5A);
    internal static readonly Color Signal = new(0x2E, 0xE5, 0xF1);
    internal static readonly Color White = new(0xFF, 0xFF, 0xFF);
    internal static readonly Color Text = new(0xD2, 0xE0, 0xF2);
    internal static readonly Color Muted = new(0x8A, 0x9C, 0xB3);
    internal static readonly Color Green = new(0x38, 0xDD, 0x7E);
    internal static readonly Color Amber = new(0xF2, 0xB7, 0x63);
    internal static readonly Color Red = new(0xF9, 0x74, 0x76);
    internal static readonly Color Blue = new(0x29, 0x9A, 0xF3);

    // Markup colour names for the same palette.
    internal const string SignalTag = "#2EE5F1";
    internal const string TextTag = "#D2E0F2";
    internal const string MutedTag = "#8A9CB3";
    internal const string GreenTag = "#38DD7E";
    internal const string AmberTag = "#F2B763";
    internal const string RedTag = "#F97476";
    internal const string BlueTag = "#299AF3";
    internal const string InkTag = "#0D1821";
    internal const string WhiteTag = "#FFFFFF";

    internal static readonly Style BorderStyle = new(Line);
    internal static readonly Style FocusBorderStyle = new(Signal);

    /// <summary>Escapes untrusted text for markup and removes terminal control characters.</summary>
    internal static string Safe(string? value) => TerminalViews.Safe(value);

    internal static string Accent(string markup) => $"[{SignalTag}]{markup}[/]";
    internal static string Dim(string markup) => $"[{MutedTag}]{markup}[/]";
    internal static string Strong(string markup) => $"[bold {WhiteTag}]{markup}[/]";

    /// <summary>A rounded panel with a cyan title, the frame every dashboard section uses.</summary>
    internal static Panel Section(string title, Spectre.Console.Rendering.IRenderable content, bool focus = false) =>
        new Panel(content)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = focus ? FocusBorderStyle : BorderStyle,
            Header = new PanelHeader($"[bold {SignalTag}] {title} [/]"),
            Expand = true,
            Padding = new Padding(1, 0, 1, 0),
        };

    /// <summary>A borderless table with dim headers over a rule, used inside sections.</summary>
    internal static Table Grid(params string[] columns)
    {
        var table = new Table { Border = new QuietTableBorder(), BorderStyle = BorderStyle, ShowHeaders = true };
        foreach (var column in columns)
        {
            table.AddColumn(new TableColumn($"[{MutedTag}]{column}[/]") { NoWrap = true });
        }

        return table;
    }

    /// <summary>A coloured state dot followed by nothing; the dot shape still differs without colour.</summary>
    internal static string Dot(string? state) => state switch
    {
        "Connected" => $"[{GreenTag}]●[/]",
        "Connecting" or "Reconnecting" => $"[{AmberTag}]◐[/]",
        "Stopped" or "Failed" => $"[{RedTag}]●[/]",
        _ => $"[{MutedTag}]○[/]",
    };

    internal static string State(string? state) => $"{Dot(state)} {Safe(state ?? "Unknown")}";

    internal static string StatusCode(int status) => status switch
    {
        >= 500 => $"[{RedTag}]{status}[/]",
        >= 400 => $"[{AmberTag}]{status}[/]",
        >= 300 => $"[{BlueTag}]{status}[/]",
        _ => $"[{GreenTag}]{status}[/]",
    };

    internal static Color ClassColor(string statusClass) => statusClass switch
    {
        "5xx" => Red,
        "4xx" => Amber,
        "3xx" => Blue,
        "2xx" => Green,
        _ => Muted,
    };

    internal static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    internal static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s"
            : $"{(int)span.TotalSeconds}s";
    }

    internal static string Milliseconds(double ms) => ms.ToString(ms < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>Removes terminal control characters from untrusted text shown as plain (non-markup) text.</summary>
    internal static string Plain(string? value) => new((value ?? "").Where(c => !char.IsControl(c)).ToArray());

    /// <summary>Untrusted plain text that is cut with an ellipsis instead of wrapping.</summary>
    internal static Text Cell(string? value) => new(Plain(value)) { Overflow = Overflow.Ellipsis };

    /// <summary>Trims text to a cell budget with an ellipsis; used where a column would otherwise wrap.</summary>
    internal static string Clip(string? value, int width)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray());
        return width <= 1 || text.Length <= width ? text : text[..(width - 1)] + "…";
    }
}

/// <summary>No frame and no blank top or bottom line: only a rule under the header, so tables sit flush in a section.</summary>
internal sealed class QuietTableBorder : TableBorder
{
    public override bool SupportsRowSeparator => false;

    public override string GetPart(TableBorderPart part) =>
        part is TableBorderPart.HeaderBottom or TableBorderPart.HeaderBottomSeparator ? "─" : string.Empty;
}
