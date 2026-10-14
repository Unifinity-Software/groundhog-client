using Groundhog.Cli.Interactive;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace Groundhog.Cli.Tests;

/// <summary>Drives the full-screen app with scripted keys against a test console.</summary>
public sealed class TerminalNavigationTests
{
    private static async Task<(InteractiveApp App, List<(string Screen, IRenderable Frame)> Frames)> RunAsync(ScriptedKeys keys,
        FakeServices? services = null, TestConsole? console = null, AppOptions? options = null)
    {
        console ??= Harness.Console(120, 36);
        var app = Harness.App(console, keys, services, options);
        var frames = new List<(string, IRenderable)>();
        app.Drawn = (screen, frame) => frames.Add((screen, frame));
        Assert.Equal(0, await app.RunAsync());
        return (app, frames);
    }

    [Fact]
    public async Task Enter_opens_a_screen_Esc_returns_to_the_menu_and_Q_quits()
    {
        var keys = new ScriptedKeys().Press(ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Escape, ConsoleKey.Q);
        var (app, frames) = await RunAsync(keys);

        Assert.Equal(["Menu", "Tunnels", "Menu"], app.Trace);
        Assert.Equal(0, keys.Remaining);
        // The menu is redrawn in full, not appended to: the last frame is a complete menu frame again.
        var last = Harness.Text(frames[^1].Frame, 120, 36);
        Assert.Equal("Menu", frames[^1].Screen);
        Assert.Contains("▶ Tunnels", last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backspace_also_goes_back_and_Esc_on_the_menu_stays()
    {
        var keys = new ScriptedKeys().Press(ConsoleKey.Escape, ConsoleKey.Enter, ConsoleKey.Backspace, ConsoleKey.Q);
        var (app, _) = await RunAsync(keys);
        Assert.Equal(["Menu", "Home", "Menu"], app.Trace);
    }

    [Fact]
    public async Task Q_quits_from_a_nested_screen()
    {
        var keys = new ScriptedKeys().Press(ConsoleKey.D2, ConsoleKey.Enter, ConsoleKey.Q);
        var (app, _) = await RunAsync(keys);
        Assert.Equal(["Menu", "Tunnels", "myapp"], app.Trace);
    }

    [Fact]
    public async Task Cancelled_app_exits_without_reading_keys()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var keys = new ScriptedKeys();
        using var console = Harness.Console(80, 24);
        var app = Harness.App(console, keys, stop: stop);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(app.RunAsync);
    }

    [Fact]
    public async Task Uses_the_alternate_screen_when_the_terminal_supports_it_and_leaves_it_on_exit()
    {
        using var console = Harness.Console(80, 24);
        console.Profile.Capabilities.AlternateBuffer = true;
        await RunAsync(new ScriptedKeys().Press(ConsoleKey.Q), console: console);
        var output = console.Output;
        var enter = output.IndexOf("\u001b[?1049h", StringComparison.Ordinal);
        var leave = output.LastIndexOf("\u001b[?1049l", StringComparison.Ordinal);
        Assert.True(enter >= 0 && leave > enter, "The app should enter and then leave the alternate screen.");
        Assert.Contains("GROUNDHOG", output[enter..leave], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_alternate_screen_the_app_clears_instead()
    {
        using var console = Harness.Console(80, 24);
        console.Profile.Capabilities.AlternateBuffer = false;
        await RunAsync(new ScriptedKeys().Press(ConsoleKey.Q), console: console);
        Assert.DoesNotContain("\u001b[?1049h", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Boot_screen_stays_until_enter_and_q_there_quits()
    {
        var keys = new ScriptedKeys().Press(ConsoleKey.Spacebar, ConsoleKey.A, ConsoleKey.Enter, ConsoleKey.Q);
        var (app, frames) = await RunAsync(keys, options: new AppOptions(Boot: true));
        Assert.Equal("Boot", frames[0].Screen);
        var boot = Harness.Text(frames[0].Frame, 120, 36);
        Assert.Contains("**** GROUNDHOG CLI 0.4.0-PREVIEW.1 ****", boot, StringComparison.Ordinal);
        Assert.Contains("SIGNED IN AS developer@example.com · WORKSPACE web-team · 3 TUNNELS, 2 CONNECTED", boot, StringComparison.Ordinal);
        Assert.Contains("READY.", boot, StringComparison.Ordinal);
        Assert.Contains("PRESS ENTER TO CONTINUE", boot, StringComparison.Ordinal);
        Assert.Equal(["Menu"], app.Trace);
        Assert.Equal(0, keys.Remaining);

        var quit = new ScriptedKeys().Press(ConsoleKey.Q);
        var (quitApp, _) = await RunAsync(quit, options: new AppOptions(Boot: true));
        Assert.Empty(quitApp.Trace);
    }

    [Fact]
    public async Task An_available_update_is_offered_on_the_boot_screen_and_installed_after_the_app_closes()
    {
        var services = new FakeServices { Update = "0.5.0" };
        var keys = new ScriptedKeys().Press(ConsoleKey.U, ConsoleKey.Y);
        var (app, frames) = await RunAsync(keys, services, options: new AppOptions(Boot: true));
        Assert.Contains("UPDATE AVAILABLE: 0.5.0", Harness.Text(frames.Last(f => f.Screen == "Boot").Frame, 120, 36), StringComparison.Ordinal);
        Assert.Contains("Update Groundhog CLI to 0.5.0?", Harness.Text(frames[^1].Frame, 120, 36), StringComparison.Ordinal);
        Assert.Equal(["Menu", "Confirm"], app.Trace);
        Assert.Equal("0.5.0", app.UpdateRequested);
        Assert.Empty(services.Commands);
    }

    [Fact]
    public async Task Boot_screen_is_skipped_when_the_console_is_not_interactive()
    {
        using var console = new TestConsole();
        console.Profile.Width = 80;
        console.Profile.Height = 24;
        var app = Harness.App(console, new ScriptedKeys().Press(ConsoleKey.Q), options: new AppOptions(Boot: true));
        var screens = new List<string>();
        app.Drawn = (screen, _) => screens.Add(screen);
        await app.RunAsync();
        Assert.DoesNotContain("Boot", screens);
    }

    [Fact]
    public async Task Deleting_a_tunnel_confirms_in_the_frame_runs_the_delete_command_and_returns_to_the_list()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D2, ConsoleKey.Enter, ConsoleKey.D, ConsoleKey.Y, ConsoleKey.Q);
        var (app, frames) = await RunAsync(keys, services);

        Assert.Equal(["delete", "myapp", "--workspace", "web-team", "--yes"], Assert.Single(services.Commands));
        Assert.Equal(["Menu", "Tunnels", "myapp", "Confirm", "Working", "Tunnels"], app.Trace);
        var confirm = Harness.Text(frames.Last(f => f.Screen == "Confirm").Frame, 120, 36);
        Assert.Contains("Delete myapp?", confirm, StringComparison.Ordinal);
        Assert.Contains("myapp.groundhog.unifinity.com stops working", confirm, StringComparison.Ordinal);
        Assert.Contains("Tunnel deleted.", Harness.Text(frames[^1].Frame, 120, 36), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_a_key_confirms_in_the_frame_and_runs_key_add_without_yes()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D2, ConsoleKey.Enter, ConsoleKey.A, ConsoleKey.Y, ConsoleKey.Q);
        var (app, frames) = await RunAsync(keys, services);

        Assert.Equal(["key", "add", "myapp", "--workspace", "web-team"], Assert.Single(services.Commands));
        var confirm = Harness.Text(frames.Last(f => f.Screen == "Confirm").Frame, 120, 36);
        Assert.Contains("Add a key for myapp?", confirm, StringComparison.Ordinal);
        Assert.Contains("Agents already connected stay connected", confirm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_a_confirmation_runs_nothing()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D2, ConsoleKey.Enter, ConsoleKey.K, ConsoleKey.N, ConsoleKey.Q);
        var (app, _) = await RunAsync(keys, services);
        Assert.Empty(services.Commands);
        Assert.Equal(["Menu", "Tunnels", "myapp", "Confirm", "myapp"], app.Trace);
    }

    [Fact]
    public async Task Renaming_reads_the_new_name_in_the_frame_and_opens_the_renamed_tunnel()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D2, ConsoleKey.Enter, ConsoleKey.N)
            .Press(ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Backspace)
            .Type("Bad").Press(ConsoleKey.Enter) // refused with a message; letters such as Q are text here, not shortcuts
            .Press(ConsoleKey.Backspace, ConsoleKey.Backspace, ConsoleKey.Backspace).Type("demo").Press(ConsoleKey.Enter, ConsoleKey.Q);
        var (app, frames) = await RunAsync(keys, services);

        Assert.Equal(["rename", "myapp", "demo", "--workspace", "web-team", "--yes"], Assert.Single(services.Commands));
        Assert.Equal("demo", app.Trace[^1]);
        Assert.Contains(frames, f => f.Screen == "Rename myapp"
            && Harness.Text(f.Frame, 120, 36).Contains("Use lowercase letters, digits and hyphens only.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Open_and_copy_use_the_public_address()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D2, ConsoleKey.Enter, ConsoleKey.O, ConsoleKey.C, ConsoleKey.Q);
        await RunAsync(keys, services);
        Assert.Equal(["https://myapp.groundhog.unifinity.com/"], services.Opened);
        Assert.Equal(["https://myapp.groundhog.unifinity.com"], services.Copied);
    }

    [Fact]
    public async Task Hosting_from_a_tunnel_runs_the_live_view_in_the_frame_and_Esc_returns_to_the_tunnel()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D2, ConsoleKey.Enter, ConsoleKey.H, ConsoleKey.Enter,
            ConsoleKey.D2, ConsoleKey.D3, ConsoleKey.Escape, ConsoleKey.Q);
        var (app, frames) = await RunAsync(keys, services);

        Assert.Equal(["host", "myapp", "--target", "http://localhost:5173", "--relay", Groundhog.Agent.GroundhogService.DefaultRelayAddress],
            Assert.Single(services.Commands));
        Assert.Equal(["Menu", "Tunnels", "myapp", "Host myapp from this machine", "Host", "Live", "myapp"], app.Trace);
        var live = frames.Where(f => f.Screen == "Live").Select(f => Harness.Text(f.Frame, 120, 36)).ToArray();
        Assert.Contains(live, f => f.Contains("Live requests", StringComparison.Ordinal));
        Assert.Contains(live, f => f.Contains("Requests · newest first", StringComparison.Ordinal));
        Assert.Contains(live, f => f.Contains("Session totals", StringComparison.Ordinal));
        Assert.Contains("Stopped hosting myapp.", Harness.Text(frames[^1].Frame, 120, 36), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quitting_from_help_over_a_live_host_stops_hosting_before_leaving()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D4, ConsoleKey.Enter).Type("?").Press(ConsoleKey.Q);
        var (app, _) = await RunAsync(keys, services);
        Assert.Equal(["host", "api-preview"], Assert.Single(services.Commands));
        // Hosting stops, its launcher closes, and the app then quits instead of drawing the profiles again.
        Assert.Equal(["Menu", "Saved profiles", "Host", "Live", "Help", "Saved profiles"], app.Trace);
        Assert.True(services.LastSession!.Running.IsCompleted);
        Assert.Equal(0, keys.Remaining);
    }

    [Fact]
    public async Task Choosing_a_workspace_runs_workspace_use()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D5, ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Q);
        var (_, frames) = await RunAsync(keys, services);
        Assert.Equal(["workspace", "use", "personal"], Assert.Single(services.Commands));
        var tree = Harness.Text(frames.First(f => f.Screen == "Workspaces").Frame, 120, 36);
        Assert.Contains("web-team", tree, StringComparison.Ordinal);
        Assert.Contains("● current", tree, StringComparison.Ordinal);
        Assert.Contains("myapp.groundhog.unifinity.com", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_shows_the_code_and_Esc_cancels_back_to_the_menu()
    {
        var services = new FakeServices { SignedIn = false };
        var keys = new ScriptedKeys().Press(ConsoleKey.D7, ConsoleKey.Escape, ConsoleKey.Q);
        var (app, frames) = await RunAsync(keys, services);
        Assert.Equal(["Menu", "Sign in", "Menu"], app.Trace);
        var signIn = Harness.Text(frames.First(f => f.Screen == "Sign in").Frame, 120, 36);
        Assert.Contains("https://groundhog.unifinity.com/cli-login?code=7F3A9C2B41D0", signIn, StringComparison.Ordinal);
        Assert.Contains("Waiting for approval", signIn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_a_local_agent_asks_first()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D3, ConsoleKey.S, ConsoleKey.Y, ConsoleKey.Q);
        var (app, _) = await RunAsync(keys, services);
        Assert.Equal(["stop-agent", "myapp"], Assert.Single(services.Commands));
        Assert.Equal(["Menu", "Running agents", "Confirm", "Working", "Running agents"], app.Trace);
    }

    [Fact]
    public async Task Saved_profiles_can_be_added_and_started_in_the_background()
    {
        var services = new FakeServices();
        var keys = new ScriptedKeys().Press(ConsoleKey.D4, ConsoleKey.A).Type("shop").Press(ConsoleKey.Enter)
            .Press(ConsoleKey.Enter, ConsoleKey.B, ConsoleKey.Q);
        await RunAsync(keys, services);
        Assert.Equal(["profile", "save", "shop", "--target", "http://localhost:5173"], services.Commands[0]);
        Assert.Equal(["start", "api-preview"], services.Commands[1]);
    }

    [Fact]
    public async Task Dashboard_command_requires_an_interactive_terminal()
    {
        // Test runs redirect output, so the command refuses rather than drawing.
        Assert.Equal(ExitCodes.BadArguments, await GroundhogCommand.RunAsync(["dashboard"], _ => null, new StringWriter(),
            new StringWriter(), TestContext.Current.CancellationToken));
    }
}
