using Groundhog.Cli.Interactive;
using Groundhog.Cli.Views;
using Spectre.Console.Rendering;

namespace Groundhog.Cli.Tests;

/// <summary>
/// Renders every screen of the interactive app at the minimum (80×24) and a common wide size (120×36), checks that each
/// frame fills the terminal exactly and shows the right content, and exports the documentation screenshots.
/// </summary>
public sealed class ScreenRenderingTests
{
    /// <summary>How to reach a screen from the menu, and text that must appear on it.</summary>
    public static TheoryData<string, int, int> Screens()
    {
        var data = new TheoryData<string, int, int>();
        foreach (var name in Journeys.Keys)
        {
            data.Add(name, 80, 24);
            data.Add(name, 120, 36);
        }

        return data;
    }

    private static readonly Dictionary<string, (ConsoleKey[] Keys, string Screen, bool SignedIn, string[] Expected)> Journeys = new()
    {
        ["menu"] = ([], "Menu", true, ["Tunnels", "Running agents", "Diagnostics", "developer@example.com"]),
        ["home"] = ([ConsoleKey.Enter], "Home", true, ["Avery Developer", "myapp", "Running on this machine"]),
        ["tunnels"] = ([ConsoleKey.D2], "Tunnels", true, ["myapp.groundhog.unifinity.com", "api-preview", "▶"]),
        ["tunnel-detail"] = ([ConsoleKey.D2, ConsoleKey.Enter], "myapp", true, ["https://myapp.groundhog.unifinity.com", "Recent requests", "/api/products"]),
        ["agents"] = ([ConsoleKey.D3], "Running agents", true, ["myapp", "localhost:5173"]),
        ["agent"] = ([ConsoleKey.D3, ConsoleKey.Enter], "myapp", true, ["Overview", "Live requests", "/api/orders"]),
        ["profiles"] = ([ConsoleKey.D4], "Saved profiles", true, ["api-preview", "https://localhost:7132"]),
        ["workspaces"] = ([ConsoleKey.D5], "Workspaces", true, ["web-team", "personal", "● current"]),
        ["diagnostics"] = ([ConsoleKey.D6], "Diagnostics", true, ["Groundhog service", "✓", "✗", "1 check failed."]),
        ["sign-in"] = ([ConsoleKey.D7], "Sign in", false, ["groundhog.unifinity.com/cli-login?code=7F3A9C2B41D0", "Waiting for approval"]),
        ["help"] = ([ConsoleKey.D8], "Help", true, ["Esc", "Back to the previous screen"]),
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Every_screen_fills_the_terminal_and_shows_its_content(string journey, int width, int height)
    {
        var (keys, screen, signedIn, expected) = Journeys[journey];
        var frame = await CaptureAsync(keys, screen, width, height, new FakeServices { SignedIn = signedIn });
        var lines = Harness.Lines(frame, width, height);

        Assert.Equal(height, lines.Length);
        Assert.All(lines, line => Assert.Equal(width, line.Length));
        var text = string.Join('\n', lines);
        Assert.Contains("◆ GROUNDHOG", text, StringComparison.Ordinal);
        foreach (var content in expected)
        {
            Assert.Contains(content, text, StringComparison.Ordinal);
        }

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Live_session_tabs_fit_both_sizes(int tab)
    {
        foreach (var (width, height) in new[] { (80, 24), (120, 36) })
        {
            var size = Chrome.Body(width, height);
            var frame = Chrome.Shell(new ChromeModel("0.4.0-preview.1", null, null, ["myapp"]) { Standalone = true },
                SessionViews.Dashboard(FakeServices.Session(), tab, size, FakeServices.Now), [new("Esc", "Stop hosting")], null);
            var lines = Harness.Lines(frame, width, height);
            Assert.Equal(height, lines.Length);
            Assert.Contains(SessionViews.Tabs[tab], lines[1], StringComparison.Ordinal);
            Assert.Contains(tab switch { 0 => "Live requests", 1 => "newest first", _ => "Session totals" }, string.Join('\n', lines),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_color_output_has_no_colour_codes_but_keeps_the_markers()
    {
        using var console = Harness.Console(120, 36, color: false);
        console.EmitAnsiSequences = true;
        var size = Chrome.Body(120, 36);
        var dashboard = new Dashboard(new AccountState(FakeServices.Account, "web-team", FakeServices.Now.AddHours(7), "https://groundhog.unifinity.com", null),
            FakeServices.Tunnels, null, [FakeServices.Agent()], []);
        console.Write(Chrome.Shell(new ChromeModel("0.4.0-preview.1", "developer@example.com", "web-team", ["Tunnels"]),
            HomeViews.Tunnels(dashboard, 10, size, selected: 0), [new("Esc", "Back")], null));
        Assert.DoesNotContain("\u001b[38;", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[48;", console.Output, StringComparison.Ordinal);
        Assert.Contains("▶", console.Output, StringComparison.Ordinal);
        Assert.Contains("○", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_screen_fits_the_minimum_terminal()
    {
        var frame = BootView.Render("0.4.0-preview.1", "SIGNED IN AS developer@example.com · WORKSPACE web-team · 3 TUNNELS, 2 CONNECTED",
            ready: true, cursor: true, 80, 24);
        var lines = Harness.Lines(frame, 80, 24);
        Assert.Equal(24, lines.Length);
        Assert.Contains(lines, l => l.Contains("READY.", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains('█', StringComparison.Ordinal));
    }

    [Fact]
    public void Untrusted_names_cannot_inject_markup_or_controls()
    {
        var hostile = FakeServices.Tunnel("[red]x[/]\u001b[2J", "Connected", 1, 1, 1, 1, TimeSpan.Zero);
        var text = Harness.Text(TunnelViews.Table([hostile], 5, new ViewSize(118, 30)), 120, 10);
        Assert.Contains("[red]x[/]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exports the documentation screenshots (HTML and text) when GROUNDHOG_SCREENSHOT_DIR is set; tools/cli-screenshots.cjs
    /// turns them into PNGs. The frames are the real renderer's output with the synthetic data above.
    /// </summary>
    [Fact]
    public async Task Documentation_screenshots_use_the_actual_screens_and_public_sample_data()
    {
        Harness.Export("cli-boot", BootView.Render("0.4.0-preview.1",
            "SIGNED IN AS developer@example.com · WORKSPACE web-team · 3 TUNNELS, 2 CONNECTED", ready: true, cursor: true, 100, 30), 100, 30);
        Harness.Export("cli-home", await CaptureAsync([ConsoleKey.Enter], "Home", 120, 36), 120, 36);
        Harness.Export("cli-menu", await CaptureAsync([ConsoleKey.DownArrow], "Menu", 140, 40), 140, 40);
        Harness.Export("cli-narrow", await CaptureAsync([], "Menu", 80, 24), 80, 24);
        Harness.Export("cli-tunnels", await CaptureAsync([ConsoleKey.D2], "Tunnels", 120, 36), 120, 36);
        Harness.Export("cli-tunnel-detail", await CaptureAsync([ConsoleKey.D2, ConsoleKey.Enter], "myapp", 120, 36), 120, 36);
        Harness.Export("cli-session", await CaptureAsync([ConsoleKey.D2, ConsoleKey.Enter, ConsoleKey.H, ConsoleKey.Enter], "myapp", 120, 36,
            pick: frames => frames.First(f => f.Screen == "Live").Frame), 120, 36);
        Harness.Export("cli-diagnostics", await CaptureAsync([ConsoleKey.D6], "Diagnostics", 120, 36), 120, 36);
        Harness.Export("cli-sign-in", await CaptureAsync([ConsoleKey.D7], "Sign in", 120, 36, new FakeServices { SignedIn = false }), 120, 36);
        Harness.Export("cli-confirm", await CaptureAsync([ConsoleKey.D2, ConsoleKey.Enter, ConsoleKey.D], "Confirm", 120, 36), 120, 36);
    }

    private static async Task<IRenderable> CaptureAsync(ConsoleKey[] keys, string screen, int width, int height, FakeServices? services = null,
        Func<List<(string Screen, IRenderable Frame, int Index)>, IRenderable>? pick = null)
    {
        using var console = Harness.Console(width, height);
        var script = new ScriptedKeys().Press(keys).Press(ConsoleKey.Escape, ConsoleKey.Q);
        var app = Harness.App(console, script, services);
        var frames = new List<(string Screen, IRenderable Frame, int Index)>();
        app.Drawn = (title, frame) => frames.Add((title, frame, frames.Count));
        await app.RunAsync();
        return pick?.Invoke(frames) ?? frames.Last(f => f.Screen == screen).Frame;
    }
}
