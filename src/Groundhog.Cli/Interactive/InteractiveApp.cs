using Groundhog.Cli.Views;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Groundhog.Cli.Interactive;

/// <summary>What a screen asks the app to do after a key: stay, go back, open another screen, or quit.</summary>
/// <param name="Pop">How many screens to close.</param>
/// <param name="Push">A screen to open after closing them.</param>
/// <param name="Quit">Leave the app.</param>
internal readonly record struct Nav(int Pop, Screen? Push, bool Quit)
{
    internal static Nav Stay => default;
    internal static Nav Back => new(1, null, false);
    internal static Nav Exit => new(0, null, true);
    internal static Nav Open(Screen screen) => new(0, screen, false);
    internal static Nav Replace(Screen screen) => new(1, screen, false);
    internal bool IsStay => Pop == 0 && Push is null && !Quit;
}

/// <summary>
/// One full-screen view of the app. Rendering is pure (state in, renderable out); keys and timers change state.
/// </summary>
internal abstract class Screen
{
    /// <summary>The breadcrumb label in the header bar.</summary>
    internal abstract string Title { get; }

    /// <summary>How often to redraw without a key press, for live data and spinners.</summary>
    internal virtual TimeSpan? Refresh => null;

    /// <summary>True while the screen reads typed text, so letters are input rather than shortcuts.</summary>
    internal virtual bool CapturesText => false;

    /// <summary>Called when the screen opens.</summary>
    internal virtual void Enter(InteractiveApp app) { }

    /// <summary>Called when the screen is shown again after the screens above it closed.</summary>
    internal virtual void Resume(InteractiveApp app) { }

    /// <summary>
    /// Called before every frame; background work reports completion through it. A screen that runs something in the
    /// foreground (hosting from a menu) awaits it here.
    /// </summary>
    internal virtual Task<Nav> UpdateAsync(InteractiveApp app) => Task.FromResult(Nav.Stay);

    internal abstract IRenderable Render(InteractiveApp app, ViewSize size);

    internal abstract IReadOnlyList<KeyHint> Keys(InteractiveApp app);

    /// <summary>Handles a key; null leaves it to the app's defaults (Esc back, Q quit, ? help, R refresh).</summary>
    internal virtual Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key) => Task.FromResult<Nav?>(null);
}

/// <summary>Options for <see cref="InteractiveApp"/>; tests turn the boot screen off and fix the clock.</summary>
internal sealed record AppOptions(bool Boot = true, bool AlternateScreen = true);

/// <summary>
/// The full-screen interactive app. Owns a single live display (inside the terminal's alternate screen when available),
/// a stack of screens and the dashboard data they share. Every frame is drawn in full and replaces the previous one.
/// </summary>
internal sealed class InteractiveApp
{
    private static readonly TimeSpan _poll = TimeSpan.FromMilliseconds(30);
    private readonly List<Screen> _screens = [];
    private readonly AppOptions _options;
    private Action<IRenderable>? _present;
    private int _dirty;
    private (int Width, int Height) _drawnSize;
    private bool _quit;

    internal InteractiveApp(IAnsiConsole console, IKeySource keys, TimeProvider time, IInteractiveServices services,
        CancellationToken cancellation, AppOptions? options = null)
    {
        Console = console;
        Keys = keys;
        Time = time;
        Services = services;
        Cancellation = cancellation;
        _options = options ?? new();
        Data = new DashboardData(this);
    }

    internal IAnsiConsole Console { get; }
    internal IKeySource Keys { get; }
    internal TimeProvider Time { get; }
    internal IInteractiveServices Services { get; }
    internal CancellationToken Cancellation { get; }
    internal DashboardData Data { get; }

    /// <summary>The message on the status line; cleared by the next key press.</summary>
    internal StatusMessage? Status { get; set; }

    /// <summary>True for the stand-alone host view, which has no menu and doesn't query the account.</summary>
    internal bool Standalone { get; private init; }

    /// <summary>A newer CLI version found by the background check, shown on the launch screen and in the header.</summary>
    internal string? AvailableUpdate { get; private set; }

    /// <summary>The version to install after the app closes, chosen with U.</summary>
    internal string? UpdateRequested { get; private set; }

    /// <summary>The latest diagnostics results, previewed in the menu.</summary>
    internal CheckRow[]? LastDiagnostics { get; set; }

    /// <summary>Titles of the screens in the order they were shown; tests assert navigation with it.</summary>
    internal List<string> Trace { get; } = [];

    /// <summary>Called with every frame drawn and the screen it shows; tests capture what the user sees.</summary>
    internal Action<string, IRenderable>? Drawn { get; set; }

    internal DateTimeOffset Now => Time.GetUtcNow();

    internal ViewSize Size => Chrome.Body(Console.Profile.Width, Console.Profile.Height);

    /// <summary>Asks for a redraw from background work (data arrived, an operation finished).</summary>
    internal void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>The spinner frame for the current time.</summary>
    internal string Spinner
    {
        get
        {
            var spinner = Console.Profile.Capabilities.Unicode ? Spectre.Console.Spinner.Known.Dots : Spectre.Console.Spinner.Known.Ascii;
            var frame = (int)(Time.GetUtcNow().ToUnixTimeMilliseconds() / Math.Max(1, (long)spinner.Interval.TotalMilliseconds));
            return spinner.Frames[frame % spinner.Frames.Count];
        }
    }

    /// <summary>Runs the dashboard: the launch screen until Enter, then the main menu until the user quits.</summary>
    internal async Task<int> RunAsync()
    {
        Data.Refresh();
        _ = CheckForUpdateAsync();
        await InFrameAsync(async () =>
        {
            var next = BootChoice.Continue;
            if (_options.Boot && Console.Profile.Capabilities.Interactive)
            {
                next = await BootAsync();
            }

            if (next != BootChoice.Quit)
            {
                await RunScreensAsync(new MenuScreen(), next == BootChoice.Update ? UpdateScreen() : null);
            }
        });
        return 0;
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            AvailableUpdate = await Services.AvailableUpdateAsync(Cancellation);
            Invalidate();
        }
        catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>Asks before closing the app to install <see cref="AvailableUpdate"/>.</summary>
    internal Screen UpdateScreen()
    {
        var version = AvailableUpdate!;
        var agents = Data.Current.Agents?.Length ?? 0;
        var detail = OperatingSystem.IsWindows()
            ? $"Groundhog closes and a new window installs {version}; it takes about 15 seconds. Start groundhog again when that window says it's done."
            : $"Groundhog closes and installs {version} in this terminal. Start groundhog again when it's done.";
        if (agents > 0)
        {
            detail += OperatingSystem.IsWindows()
                ? $" {Dashboard.Plural(agents, "agent")} running on this machine keep the old version open, so the update will fail until you stop them in Running agents."
                : $" {Dashboard.Plural(agents, "agent")} running on this machine keep the old version until you restart them.";
        }

        return new ConfirmScreen($"Update Groundhog CLI to {version}?", detail, "Update now", danger: false, () =>
        {
            UpdateRequested = version;
            return Task.FromResult(Nav.Exit);
        });
    }

    /// <summary>The live view of <c>groundhog host</c> started from the command line; Esc or Q stops hosting.</summary>
    internal static Task RunHostAsync(HostSession session)
    {
        // No cancellation token: Ctrl+C stops the agent through its own token, and the view ends when the agent stops,
        // so the command still prints "Stopped." and returns its exit code as before.
        var app = new InteractiveApp(AnsiConsole.Console, new ConsoleKeySource(), TimeProvider.System, new HostedServices(),
            CancellationToken.None) { Standalone = true };
        return app.InFrameAsync(() => app.RunHostViewAsync(session));
    }

    /// <summary>
    /// Shows a foreground host's live view until the agent stops. Quitting from a screen opened on top of it (Q in Help)
    /// stops hosting first, so the view never returns while the agent still runs; the app then quits.
    /// </summary>
    internal async Task RunHostViewAsync(HostSession session, string? title = null)
    {
        var quit = await RunScreensAsync(new SessionScreen(session, title));
        if (!session.Running.IsCompleted)
        {
            session.Stop();
            await session.Running;
        }

        _quit |= quit;
    }

    /// <summary>
    /// Runs screens starting at <paramref name="root"/> until it closes. Nested calls (hosting from a menu) draw into
    /// the same live display, so the host view replaces the menu and the menu comes back afterwards.
    /// </summary>
    internal async Task<bool> RunScreensAsync(Screen root, Screen? above = null)
    {
        var floor = _screens.Count;
        Open(root);
        if (above is not null)
        {
            Open(above);
        }

        try
        {
            while (true)
            {
                if (_quit)
                {
                    return true;
                }

                var screen = _screens[^1];
                var nav = await screen.UpdateAsync(this);
                if (nav.IsStay)
                {
                    Draw();
                    var key = await NextKeyAsync(screen.Refresh);
                    if (key is not { } pressed)
                    {
                        continue;
                    }

                    Status = null;
                    nav = await screen.KeyAsync(this, pressed) ?? Default(screen, pressed);
                }

                if (nav.Quit)
                {
                    return true;
                }

                for (var i = 0; i < nav.Pop; i++)
                {
                    _screens.RemoveAt(_screens.Count - 1);
                    if (_screens.Count == floor)
                    {
                        return false;
                    }
                }

                if (nav.Push is { } next)
                {
                    Open(next);
                }
                else if (nav.Pop > 0)
                {
                    Trace.Add(_screens[^1].Title);
                    _screens[^1].Resume(this);
                }
            }
        }
        finally
        {
            _screens.RemoveRange(floor, _screens.Count - floor);
        }
    }

    private void Open(Screen screen)
    {
        _screens.Add(screen);
        Trace.Add(screen.Title);
        screen.Enter(this);
    }

    private Nav Default(Screen screen, ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.Escape or ConsoleKey.Backspace)
        {
            return _screens.Count > 1 ? Nav.Back : Nav.Stay;
        }

        if (screen.CapturesText)
        {
            return Nav.Stay;
        }

        return char.ToUpperInvariant(key.KeyChar) switch
        {
            'Q' => Nav.Exit,
            '?' when screen is not HelpScreen => Nav.Open(new HelpScreen()),
            'R' when !Standalone => Refresh(),
            'U' when !Standalone && AvailableUpdate is not null && screen is not ConfirmScreen => Nav.Open(UpdateScreen()),
            _ => key.Key == ConsoleKey.F1 && screen is not HelpScreen ? Nav.Open(new HelpScreen()) : Nav.Stay,
        };

        Nav Refresh()
        {
            Data.Refresh();
            Status = new("Refreshing…");
            return Nav.Stay;
        }
    }

    private void Draw()
    {
        var screen = _screens[^1];
        var size = Size;
        var path = _screens.Skip(Standalone ? 0 : 1).Select(s => s.Title).ToArray();
        var account = Standalone ? null : Data.Current.Account;
        var chrome = new ChromeModel(Services.Version, account?.Account?.Email, account?.SignedIn == true ? account.Workspace : null, path)
        {
            Standalone = Standalone,
            Update = Standalone ? null : AvailableUpdate,
        };
        var frame = Chrome.Shell(chrome, screen.Render(this, size), screen.Keys(this), Status);
        Drawn?.Invoke(screen.Title, frame);
        Present(frame);
    }

    private void Present(IRenderable frame)
    {
        _drawnSize = (Console.Profile.Width, Console.Profile.Height);
        _present?.Invoke(frame);
    }

    /// <summary>
    /// Waits for a key, a redraw request or the screen's refresh interval. Polls rather than blocking in ReadKey so the
    /// display stays live and Ctrl+C (the cancellation token) is noticed promptly.
    /// </summary>
    private async Task<ConsoleKeyInfo?> NextKeyAsync(TimeSpan? refresh)
    {
        Cancellation.ThrowIfCancellationRequested();
        var started = Time.GetTimestamp();
        while (true)
        {
            if (Keys.KeyAvailable)
            {
                return Keys.ReadKey();
            }

            if (Interlocked.Exchange(ref _dirty, 0) == 1
                || (refresh is { } interval && Time.GetElapsedTime(started) >= interval)
                || _drawnSize != (Console.Profile.Width, Console.Profile.Height))
            {
                return null;
            }

            await Task.Delay(_poll, Time, Cancellation);
        }
    }

    private enum BootChoice { Continue, Quit, Update }

    /// <summary>
    /// The C64-style launch screen. It stays until Enter so its summary (version, account, tunnels, an available update)
    /// can be read; Q or Esc quits and U starts an available update. The frame is redrawn only when something on it
    /// changes: data arriving or the cursor blinking.
    /// </summary>
    private async Task<BootChoice> BootAsync()
    {
        var started = Time.GetTimestamp();
        string? shown = null;
        while (true)
        {
            var dashboard = Data.Current;
            var loaded = dashboard.Account is not null && (dashboard.Tunnels is not null || !dashboard.Account.SignedIn);
            var cursor = (int)(Time.GetElapsedTime(started).TotalMilliseconds / 500) % 2 == 0;
            var update = AvailableUpdate;
            var status = BootStatus(dashboard);
            var key = $"{status}|{loaded}|{cursor}|{update}|{Console.Profile.Width}x{Console.Profile.Height}";
            if (key != shown)
            {
                shown = key;
                var frame = BootView.Render(Services.Version, status, loaded, cursor, Console.Profile.Width, Console.Profile.Height, update);
                Drawn?.Invoke("Boot", frame);
                Present(frame);
            }

            if (Keys.KeyAvailable)
            {
                var pressed = Keys.ReadKey();
                if (pressed.Key == ConsoleKey.Enter)
                {
                    return BootChoice.Continue;
                }

                if (pressed.Key == ConsoleKey.Escape || char.ToUpperInvariant(pressed.KeyChar) == 'Q')
                {
                    return BootChoice.Quit;
                }

                if (update is not null && char.ToUpperInvariant(pressed.KeyChar) == 'U')
                {
                    return BootChoice.Update;
                }

                continue;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), Time, Cancellation);
        }
    }

    internal static string? BootStatus(Dashboard dashboard)
    {
        if (dashboard.Account is not { } account)
        {
            return null;
        }

        if (!account.SignedIn)
        {
            return "NOT SIGNED IN · HOSTING WITH A KEY WORKS WITHOUT AN ACCOUNT";
        }

        var text = $"SIGNED IN AS {account.Account!.Email}";
        if (account.Workspace is { } workspace)
        {
            text += $" · WORKSPACE {workspace}";
        }

        if (dashboard.Tunnels is { } tunnels)
        {
            text += $" · {tunnels.Length} TUNNEL{(tunnels.Length == 1 ? "" : "S")}, {tunnels.Count(t => t.State == "Connected")} CONNECTED";
        }

        return text;
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside one live display, in the alternate screen when the terminal has one so the
    /// user's scrollback is untouched; otherwise the screen is cleared first and the display clears itself on exit.
    /// </summary>
    internal async Task InFrameAsync(Func<Task> body)
    {
        async Task LiveAsync() => await Console.Live(new Text(""))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Crop)
            .Cropping(VerticalOverflowCropping.Bottom)
            .StartAsync(async context =>
            {
                _present = frame =>
                {
                    context.UpdateTarget(frame);
                    context.Refresh();
                };
                try
                {
                    await body();
                }
                finally
                {
                    _present = null;
                }
            });

        if (_options.AlternateScreen && Console.Profile.Capabilities.Ansi && Console.Profile.Capabilities.AlternateBuffer)
        {
            // AlternateScreen takes a synchronous callback. Run the loop on the thread pool and wait, so no
            // synchronization context can deadlock it; the alternate screen is left even when the loop throws.
            Console.AlternateScreen(() => Task.Run(LiveAsync).GetAwaiter().GetResult());
        }
        else
        {
            Console.Clear(true);
            await LiveAsync();
        }
    }
}

/// <summary>The account, tunnels, local agents and profiles shared by the menu and dashboards.</summary>
internal sealed class DashboardData(InteractiveApp app)
{
    private static readonly TimeSpan _remoteAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _localAge = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private Dashboard _current = Dashboard.Empty;
    private Task _remote = Task.CompletedTask;
    private Task _local = Task.CompletedTask;
    private long _remoteLoaded;
    private long _localLoaded;

    internal Dashboard Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Reloads everything (or one side); loads already in flight are not duplicated.</summary>
    internal void Refresh(bool remote = true, bool local = true)
    {
        if (!remote && !local)
        {
            return;
        }

        Update(d => d with { Profiles = app.Services.Profiles() });
        if (remote && _remote.IsCompleted)
        {
            _remoteLoaded = app.Time.GetTimestamp();
            _remote = LoadRemoteAsync();
        }

        if (local && _local.IsCompleted)
        {
            _localLoaded = app.Time.GetTimestamp();
            _local = LoadLocalAsync();
        }
    }

    /// <summary>Refreshes local agents every few seconds and remote data every half minute while a dashboard is open.</summary>
    internal void RefreshIfStale() => Refresh(
        remote: app.Time.GetElapsedTime(_remoteLoaded) >= _remoteAge,
        local: app.Time.GetElapsedTime(_localLoaded) >= _localAge);

    private async Task LoadRemoteAsync()
    {
        try
        {
            var account = await app.Services.AccountAsync(app.Cancellation);
            Update(d => d with { Account = account });
            if (!account.SignedIn)
            {
                Update(d => d with { Tunnels = [], TunnelsProblem = account.Problem });
                return;
            }

            if (account.Workspace is not { } workspace)
            {
                Update(d => d with { Tunnels = [], TunnelsProblem = "Choose a workspace in Workspaces to see its tunnels." });
                return;
            }

            try
            {
                var tunnels = await app.Services.TunnelsAsync(workspace, app.Cancellation);
                Update(d => d with { Tunnels = tunnels, TunnelsProblem = null });
            }
            catch (ManagementException ex)
            {
                Update(d => d with { Tunnels = [], TunnelsProblem = ex.Message });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !app.Cancellation.IsCancellationRequested)
            {
                Update(d => d with { Tunnels = d.Tunnels ?? [], TunnelsProblem = "The Groundhog service could not be reached." });
            }
        }
        catch (OperationCanceledException) when (app.Cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task LoadLocalAsync()
    {
        try
        {
            var agents = await app.Services.LocalAgentsAsync(app.Cancellation);
            Update(d => d with { Agents = agents });
        }
        catch (OperationCanceledException) when (app.Cancellation.IsCancellationRequested)
        {
        }
    }

    private void Update(Func<Dashboard, Dashboard> change)
    {
        lock (_gate)
        {
            _current = change(_current);
        }

        app.Invalidate();
    }
}
