using Spectre.Console;
using Spectre.Console.Testing;

namespace Groundhog.Cli.Tests;

public sealed class TerminalNavigationTests
{
    [Fact]
    public async Task Home_menu_supports_arrow_navigation_and_enter()
    {
        using var console = new TestConsole().Interactive();
        console.Profile.Width = 100;
        for (var i = 0; i < 8; i++) { console.Input.PushKey(ConsoleKey.DownArrow); }
        console.Input.PushKey(ConsoleKey.Enter);
        Assert.Equal("Quit", await InteractiveCli.HomePrompt().ShowAsync(console, TestContext.Current.CancellationToken));
        Assert.Contains("Remote tunnels", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_menu_exits_without_waiting_for_keyboard_input()
    {
        using var console = new TestConsole().Interactive();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InteractiveCli.HomePrompt().ShowAsync(console, stop.Token));
    }

    [Fact]
    public async Task Documentation_home_capture_uses_the_actual_prompt()
    {
        using var console = new TestConsole().Interactive().Colors(ColorSystem.TrueColor).SupportsUnicode(true);
        console.Profile.Width = 100;
        console.Write(new Rule("[cyan]Groundhog[/]"));
        console.WriteLine("↑↓ Navigate · Enter Select · Choose Back or Quit to leave a menu");
        console.Input.PushKey(ConsoleKey.Enter);
        using var recorder = console.CreateRecorder();
        Assert.Equal("Remote tunnels", await InteractiveCli.HomePrompt().ShowAsync(recorder, TestContext.Current.CancellationToken));
        if (Environment.GetEnvironmentVariable("GROUNDHOG_SCREENSHOT_DIR") is { } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "cli-home.html"), "<pre>" + System.Net.WebUtility.HtmlEncode(console.Output) + "</pre>");
        }
    }

    [Fact]
    public void Documentation_capture_uses_the_actual_session_renderer_and_public_sample_data()
    {
        using var console = new TestConsole().Colors(ColorSystem.TrueColor).SupportsUnicode(true);
        console.Profile.Width = 110;
        using var recorder = console.CreateRecorder();
        var started = new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero);
        RequestRecord[] recent = [new(started.AddMinutes(24), "GET", "/api/products", 200, 42, false, null),
            new(started.AddMinutes(24).AddSeconds(1), "POST", "/api/orders", 201, 118, false, null),
            new(started.AddMinutes(24).AddSeconds(3), "GET", "/api/search", 502, 3, false, "local-unreachable")];
        var snapshot = new SessionSnapshot("myapp", "http://localhost:5173/", "https://myapp.tunnel.example.com/",
            "Connected", started, 1284, 3, 0, 142, recent, null, []);
        recorder.Write(TerminalViews.Session(snapshot, now: started.AddMinutes(24).AddSeconds(18)));
        recorder.Write(new Text("1 Overview · 2 Requests · 3 Statistics · Esc/Q Stop and return"));
        Assert.Contains("myapp.tunnel.example.com", console.Output, StringComparison.Ordinal);
        Assert.Contains("/api/products", console.Output, StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("GROUNDHOG_SCREENSHOT_DIR") is { } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "cli-session.html"), recorder.ExportHtml());
            File.WriteAllText(Path.Combine(directory, "cli-session.txt"), recorder.ExportText());
        }
    }
}
