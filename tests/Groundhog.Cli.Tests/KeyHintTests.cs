using Groundhog.Agent;
using Groundhog.Cli.Interactive;

namespace Groundhog.Cli.Tests;

/// <summary>The footer only offers keys that do something in the screen's current state.</summary>
public sealed class KeyHintTests
{
    /// <summary>One screen in one state. <paramref name="Load"/> false leaves the account still loading.</summary>
    private sealed record Case(string Name, Func<FakeServices> Services, Func<InteractiveApp, Screen> Screen, bool Load = true);

    private static readonly Case[] _cases =
    [
        new("menu", () => new(), _ => new MenuScreen()),
        new("home", () => new(), _ => new HomeScreen()),
        new("tunnels", () => new(), _ => new TunnelsScreen()),
        new("tunnels signed out", () => new() { SignedIn = false }, _ => new TunnelsScreen()),
        new("tunnel detail", () => new(), _ => new TunnelDetailScreen("web-team", "myapp")),
        new("running agents", () => new(), _ => new AgentsScreen()),
        new("no running agents", () => new() { AgentList = [] }, _ => new AgentsScreen()),
        new("agent", () => new(), _ => new AgentScreen(FakeServices.Agent())),
        new("live session", () => new(), _ => new SessionScreen(Session())),
        new("saved profiles", () => new(), _ => new ProfilesScreen()),
        new("no saved profiles", () => new() { ProfileList = [] }, _ => new ProfilesScreen()),
        new("workspaces", () => new(), _ => new WorkspacesScreen()),
        new("workspaces signed out", () => new() { SignedIn = false }, _ => new WorkspacesScreen()),
        new("diagnostics", () => new(), _ => new DiagnosticsScreen()),
        new("sign in, signed in", () => new(), _ => new SignInScreen()),
        new("sign in, signed out", () => new() { SignedIn = false }, _ => new SignInScreen()),
        new("sign in, account loading", () => new(), _ => new SignInScreen(), Load: false),
        new("help", () => new(), _ => new HelpScreen()),
        new("prompt", () => new(), _ => new PromptScreen("Name", "Name", "demo", null, _ => null, _ => Task.FromResult(Nav.Back))),
        new("confirm", () => new(), _ => new ConfirmScreen("Sure?", "Detail", "Do it", false, () => Task.FromResult(Nav.Back))),
        new("busy", () => new(), _ => new BusyScreen("Working", _ => new TaskCompletionSource<CommandResult>().Task)),
    ];

    [Fact]
    public async Task Every_footer_hint_names_a_key_that_does_something_in_that_state()
    {
        var useless = new List<string>();
        foreach (var c in _cases)
        {
            var (app, screen, _) = await OpenAsync(c);
            foreach (var hint in screen.Keys(app))
            {
                // A fresh screen for every key, so one key's effect can't hide another's.
                if (!await HasEffectAsync(c, hint.Keys))
                {
                    useless.Add($"{c.Name}: {hint.Keys} {hint.Label}");
                }
            }
        }

        Assert.Empty(useless);
    }

    [Fact]
    public async Task The_menu_preview_of_sign_in_offers_no_key_that_only_works_inside_it()
    {
        var (app, menu, _) = await OpenAsync(_cases[0]);
        while (MenuScreen.Entries[((MenuScreen)menu).Selected].Title != "Sign in")
        {
            await menu.KeyAsync(app, Key("↑↓"));
        }

        var preview = Harness.Text(menu.Render(app, app.Size), 120, 36);
        Assert.Contains("Press Enter to open Sign in, where you can sign in again or sign out.", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign out and remove saved keys", preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task X_on_the_sign_in_screen_signs_out_after_confirmation()
    {
        var services = new FakeServices();
        using var console = Harness.Console(120, 36);
        var app = Harness.App(console, new ScriptedKeys().Press(ConsoleKey.D7, ConsoleKey.X, ConsoleKey.Y, ConsoleKey.Q), services);
        await app.RunAsync();
        Assert.Equal(["logout"], Assert.Single(services.Commands));
        Assert.Equal(["Menu", "Sign in", "Confirm", "Working", "Sign in"], app.Trace);
    }

    private static async Task<(InteractiveApp App, Screen Screen, FakeServices Services)> OpenAsync(Case c)
    {
        var services = c.Services();
        var app = Harness.App(Harness.Console(120, 36), new ScriptedKeys(), services);
        if (c.Load)
        {
            app.Data.Refresh();
        }

        var screen = c.Screen(app);
        screen.Enter(app);
        await screen.UpdateAsync(app);
        return (app, screen, services);
    }

    /// <summary>
    /// True when the key navigates, changes what the screen shows, reports something on the status line, runs a command
    /// or reads the service again. Keys the screen leaves to the app (Esc, Q, R, ?) are handled by its defaults.
    /// </summary>
    private static async Task<bool> HasEffectAsync(Case c, string keys)
    {
        var (app, screen, services) = await OpenAsync(c);
        var key = Key(keys);
        if (screen is LiveSessionScreen && char.IsAsciiDigit(key.KeyChar))
        {
            // Tab keys: start from another tab so choosing this one is visible.
            await screen.KeyAsync(app, Key(key.KeyChar == '1' ? "2" : "1"));
        }

        var before = Harness.Text(screen.Render(app, app.Size), 120, 36);
        var (commands, calls) = (services.Commands.Count, services.Calls);
        app.Status = null;
        var nav = await screen.KeyAsync(app, key);
        if (nav is null)
        {
            return key.Key is ConsoleKey.Escape or ConsoleKey.Backspace || char.ToUpperInvariant(key.KeyChar) is 'Q' or 'R' or '?';
        }

        if (!nav.Value.IsStay)
        {
            return true;
        }

        await screen.UpdateAsync(app);
        return app.Status is not null || services.Commands.Count != commands || services.Calls != calls
            || Harness.Text(screen.Render(app, app.Size), 120, 36) != before;
    }

    private static ConsoleKeyInfo Key(string keys) => keys switch
    {
        "↑↓" => new('\0', ConsoleKey.DownArrow, false, false, false),
        "Enter" => new('\r', ConsoleKey.Enter, false, false, false),
        "Esc" => new('\u001b', ConsoleKey.Escape, false, false, false),
        "?" => new('?', ConsoleKey.Oem2, false, false, false),
        "1–9" => new('2', ConsoleKey.D2, false, false, false),
        [var digit] when char.IsAsciiDigit(digit) => new(digit, ConsoleKey.D0 + (digit - '0'), false, false, false),
        [var letter] when char.IsAsciiLetter(letter) => new(char.ToLowerInvariant(letter), (ConsoleKey)char.ToUpperInvariant(letter), false, false, false),
        _ => throw new ArgumentException($"No key for the hint '{keys}'. Teach this test how to press it."),
    };

    private static HostSession Session()
    {
        var stopped = new TaskCompletionSource<GroundhogAgentStopReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        return new HostSession("myapp", () => FakeServices.Session(), stopped.Task, () => stopped.TrySetResult(GroundhogAgentStopReason.Cancelled));
    }
}
