using Groundhog.Cli.Views;
using Groundhog.Protocol;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Groundhog.Cli.Interactive;

/// <summary>Shared key handling for screens with a highlighted row.</summary>
internal static class Selection
{
    /// <summary>Moves <paramref name="selected"/> for arrow, j/k, Home/End and page keys; returns false for other keys.</summary>
    internal static bool Move(ConsoleKeyInfo key, ref int selected, int count)
    {
        if (count == 0)
        {
            return false;
        }

        var next = key.Key switch
        {
            ConsoleKey.UpArrow or ConsoleKey.K => selected - 1,
            ConsoleKey.DownArrow or ConsoleKey.J => selected + 1,
            ConsoleKey.PageUp => selected - 10,
            ConsoleKey.PageDown => selected + 10,
            ConsoleKey.Home => 0,
            ConsoleKey.End => count - 1,
            _ => (int?)null,
        };
        if (next is null)
        {
            return false;
        }

        selected = Math.Clamp(next.Value, 0, count - 1);
        return true;
    }

    internal static int Clamp(int selected, int count) => count == 0 ? 0 : Math.Clamp(selected, 0, count - 1);

    internal static char Letter(ConsoleKeyInfo key) => char.ToUpperInvariant(key.KeyChar);
}

/// <summary>Common key hints.</summary>
internal static class Hints
{
    internal static readonly KeyHint Move = new("↑↓", "Move");
    internal static readonly KeyHint Open = new("Enter", "Open");
    internal static readonly KeyHint Refresh = new("R", "Refresh");
    internal static readonly KeyHint Back = new("Esc", "Back");
    internal static readonly KeyHint Quit = new("Q", "Quit");
    internal static readonly KeyHint Help = new("?", "Help");

    /// <summary>Hints for a list of <paramref name="count"/> rows: Move only when there is somewhere to move to.</summary>
    internal static KeyHint[] List(int count, params KeyHint[] actions) => count > 1 ? [Move, .. actions] : actions;
}

/// <summary>The main menu with a live preview of the highlighted entry.</summary>
internal sealed class MenuScreen : Screen
{
    internal static readonly MenuEntry[] Entries =
    [
        new("Home", "Account, tunnels and local agents at a glance."),
        new("Tunnels", "Remote tunnels in your workspace: inspect, host, rename, rotate keys."),
        new("Running agents", "Agents hosting from this machine, with live traffic."),
        new("Saved profiles", "Tunnels you host often, remembered with their local address."),
        new("Workspaces", "Choose the default workspace for tunnels and commands."),
        new("Diagnostics", "Check the service, your sign-in and your local targets."),
        new("Sign in", "Authorize this CLI in your browser."),
        new("Help", "Keys, commands and version."),
        new("Quit", "Leave Groundhog and restore your terminal."),
    ];

    private int _selected;

    internal override string Title => "Menu";
    internal override TimeSpan? Refresh => TimeSpan.FromSeconds(1);
    internal int Selected => _selected;

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        app.Data.RefreshIfStale();
        return Task.FromResult(Nav.Stay);
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size)
    {
        var dashboard = app.Data.Current;
        var preview = size.Wide ? Preview(app, dashboard, new ViewSize(size.Width - 31, size.Height)) : null;
        return HomeViews.Menu(Entries, _selected, dashboard, preview, size);
    }

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) =>
        [Hints.Move, Hints.Open, Hints.Refresh, Hints.Help, Hints.Quit, new("1–9", "Jump")];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        if (Selection.Move(key, ref _selected, Entries.Length))
        {
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        if (key.KeyChar is >= '1' and <= '9')
        {
            _selected = key.KeyChar - '1';
            return Task.FromResult<Nav?>(Choose());
        }

        return Task.FromResult<Nav?>(key.Key is ConsoleKey.Enter or ConsoleKey.RightArrow ? Choose() : null);
    }

    private Nav Choose() => Entries[_selected].Title switch
    {
        "Home" => Nav.Open(new HomeScreen()),
        "Tunnels" => Nav.Open(new TunnelsScreen()),
        "Running agents" => Nav.Open(new AgentsScreen()),
        "Saved profiles" => Nav.Open(new ProfilesScreen()),
        "Workspaces" => Nav.Open(new WorkspacesScreen()),
        "Diagnostics" => Nav.Open(new DiagnosticsScreen()),
        "Sign in" => Nav.Open(new SignInScreen()),
        "Help" => Nav.Open(new HelpScreen()),
        _ => Nav.Exit,
    };

    private IRenderable Preview(InteractiveApp app, Dashboard dashboard, ViewSize size) => Entries[_selected].Title switch
    {
        "Home" => HomeViews.Home(dashboard, size, app.Now, title: false),
        "Tunnels" => HomeViews.Tunnels(dashboard, size.Height - 4, size),
        "Running agents" => HomeViews.Agents(dashboard.Agents, size.Height - 4, app.Now, size),
        "Saved profiles" => LocalViews.Profiles(dashboard.Profiles, dashboard.Agents, null, size, preview: true),
        "Workspaces" => AccountViews.Workspaces(dashboard.Account,
            Array.FindIndex(dashboard.Account?.Account?.Workspaces ?? [], w => w.Slug == dashboard.Account?.Workspace),
            new Dictionary<string, TunnelInfo[]?> { [dashboard.Account?.Workspace ?? ""] = dashboard.Tunnels }, size, preview: true),
        "Diagnostics" => app.LastDiagnostics is { } rows
            ? LocalViews.Diagnostics(rows, "", true, size, preview: true)
            : Theme.Section("Diagnostics", new Markup(Theme.Dim(
                "Press Enter to check the Groundhog service, your sign-in, local agents and the targets of your saved profiles."))),
        "Sign in" => AccountViews.SignIn(new(dashboard.Account, null, false, null, "", app.Now), size, preview: true),
        "Help" => HomeViews.Help(app.Services.Version, size),
        _ => Theme.Section("Quit", new Rows(new ColorOnly(GroundhogArt.Head()), new Text(""),
            new Markup("Press Enter to leave Groundhog."), new Markup(Theme.Dim("Your terminal and its scrollback are restored as they were.")))),
    };
}

/// <summary>The home dashboard in full.</summary>
internal sealed class HomeScreen : Screen
{
    internal override string Title => "Home";
    internal override TimeSpan? Refresh => TimeSpan.FromSeconds(1);

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        app.Data.RefreshIfStale();
        return Task.FromResult(Nav.Stay);
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size) => HomeViews.Home(app.Data.Current, size, app.Now);

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) =>
        [new("T", "Tunnels"), new("A", "Agents"), Hints.Refresh, Hints.Back, Hints.Quit];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key) => Task.FromResult<Nav?>(Selection.Letter(key) switch
    {
        'T' => Nav.Replace(new TunnelsScreen()),
        'A' => Nav.Replace(new AgentsScreen()),
        _ => null,
    });
}

/// <summary>The remote tunnel inventory with a highlighted row.</summary>
internal sealed class TunnelsScreen : Screen
{
    private int _selected;

    internal override string Title => "Tunnels";
    internal override TimeSpan? Refresh => TimeSpan.FromSeconds(1);

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        app.Data.RefreshIfStale();
        return Task.FromResult(Nav.Stay);
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size)
    {
        var dashboard = app.Data.Current;
        _selected = Selection.Clamp(_selected, dashboard.Tunnels?.Length ?? 0);
        return HomeViews.Tunnels(dashboard, size.Height - 4, size, _selected);
    }

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app)
    {
        // Only keys that do something now: no list keys without tunnels, no N without a workspace to create in.
        var dashboard = app.Data.Current;
        var keys = new List<KeyHint>();
        if (dashboard.Tunnels is { Length: > 0 } tunnels && dashboard.Account?.Workspace is not null)
        {
            keys.AddRange(Hints.List(tunnels.Length, Hints.Open));
        }

        if (dashboard.Account is { SignedIn: true, Workspace: not null })
        {
            keys.Add(new("N", "New"));
        }

        keys.AddRange([Hints.Refresh, Hints.Back, Hints.Quit]);
        return keys;
    }

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        var dashboard = app.Data.Current;
        var tunnels = dashboard.Tunnels ?? [];
        if (Selection.Move(key, ref _selected, tunnels.Length))
        {
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        if (key.Key == ConsoleKey.Enter && tunnels.Length > 0 && dashboard.Account?.Workspace is { } workspace)
        {
            return Task.FromResult<Nav?>(Nav.Open(new TunnelDetailScreen(workspace, tunnels[_selected].Name)));
        }

        if (Selection.Letter(key) == 'N')
        {
            if (dashboard.Account is not { SignedIn: true, Workspace: { } target })
            {
                app.Status = new("Sign in and choose a workspace to create tunnels.", StatusKind.Warning);
                return Task.FromResult<Nav?>(Nav.Stay);
            }

            return Task.FromResult<Nav?>(Nav.Open(new PromptScreen($"New tunnel in {target}", "Tunnel name", "",
                "3–40 lowercase letters, digits and hyphens; this becomes <name>.groundhog.unifinity.com.", Names.Problem,
                name => Task.FromResult(Nav.Replace(new BusyScreen($"Creating {name}…",
                    token => app.Services.RunAsync(["create", name, "--workspace", target], token)))))));
        }

        return Task.FromResult<Nav?>(null);
    }
}

/// <summary>Validation messages for tunnel and profile names.</summary>
internal static class Names
{
    internal static string? Problem(string name) => TunnelName.Validate(name) switch
    {
        TunnelNameValidation.Valid => null,
        TunnelNameValidation.TooShort => "Use at least 3 characters.",
        TunnelNameValidation.TooLong => "Use at most 40 characters.",
        TunnelNameValidation.InvalidCharacter => "Use lowercase letters, digits and hyphens only.",
        TunnelNameValidation.MustStartWithLetter => "Start with a letter.",
        TunnelNameValidation.MustEndWithLetterOrDigit => "End with a letter or digit.",
        _ => "That name is reserved.",
    };

    internal static string? TargetProblem(string name, string target)
    {
        try
        {
            ProfileStore.Validate(new(name, target));
            return null;
        }
        catch (ArgumentException)
        {
            return "Use an http:// or https:// address such as http://localhost:5173, with no path or credentials.";
        }
    }
}

/// <summary>One tunnel in full: overview, status classes, request log and its management actions.</summary>
internal sealed class TunnelDetailScreen(string workspace, string name) : Screen
{
    private const int Kept = 200;
    private readonly Lock _gate = new();
    private TunnelInsight? _insight;
    private string? _problem;
    private Task? _loading;
    private CancellationTokenSource? _follow;
    private LiveState? _live;
    private bool _closed;

    internal override string Title => name;
    internal override TimeSpan? Refresh => TimeSpan.FromSeconds(15);

    internal override void Enter(InteractiveApp app) => Load(app);
    internal override void Resume(InteractiveApp app) => Load(app);

    internal override void Leave(InteractiveApp app)
    {
        CancellationTokenSource? follow;
        lock (_gate)
        {
            // A load still running when the screen closes must not start a stream afterwards.
            _closed = true;
            follow = _follow;
        }

        follow?.Cancel();
    }

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        app.Data.RefreshIfStale();
        return Task.FromResult(Nav.Stay);
    }

    private void Load(InteractiveApp app)
    {
        if (_loading is { IsCompleted: false })
        {
            return;
        }

        _loading = LoadAsync(app);
    }

    private async Task LoadAsync(InteractiveApp app)
    {
        try
        {
            var insight = await app.Services.TunnelAsync(workspace, name, app.Cancellation);
            lock (_gate)
            {
                // Keep requests the live stream delivered that the service's log doesn't list yet.
                _insight = _insight?.Requests is { } shown && insight.Requests is { } loaded
                    ? insight with { Requests = Merge(loaded, shown.Where(r => r.RequestId is not null)) }
                    : insight;
                _problem = null;
            }

            if (insight.Requests is not null)
            {
                Follow(app);
            }
        }
        catch (ManagementException ex)
        {
            _problem = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !app.Cancellation.IsCancellationRequested)
        {
            _problem = "The Groundhog service could not be reached.";
        }
        catch (OperationCanceledException) when (app.Cancellation.IsCancellationRequested)
        {
        }

        app.Invalidate();
    }

    /// <summary>
    /// Starts the live request stream once the log has loaded. It runs until the screen closes (the app closes every
    /// screen when it exits); one the service stopped starts again with the next load.
    /// </summary>
    private void Follow(InteractiveApp app)
    {
        IEnumerable<RemoteRequest> known;
        CancellationTokenSource follow;
        lock (_gate)
        {
            if (_closed || _follow is not null || _insight?.Requests is not { } requests)
            {
                return;
            }

            _follow = follow = new CancellationTokenSource();
            _live = null;
            known = requests;
        }

        _ = FollowAsync(app, known, follow);
    }

    private async Task FollowAsync(InteractiveApp app, IEnumerable<RemoteRequest> known, CancellationTokenSource follow)
    {
        var token = follow.Token;
        try
        {
            await app.Services.FollowRequestsAsync(workspace, name, known, request =>
            {
                lock (_gate)
                {
                    if (_insight?.Requests is { } shown)
                    {
                        _insight = _insight with { Requests = Merge([request], shown) };
                    }
                }

                app.Invalidate();
            }, state =>
            {
                lock (_gate)
                {
                    _live = state;
                }

                app.Invalidate();
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            // Live updates are an extra: the screen keeps the loaded log and says why it stopped updating.
            lock (_gate)
            {
                _live = new(LiveStatus.Stopped, ex is ManagementException ? ex.Message : "Live requests are unavailable right now.");
            }

            app.Invalidate();
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_follow, follow))
                {
                    _follow = null;
                }
            }
        }
    }

    /// <summary>Newest first, each request once (by ID), at most the log's page size.</summary>
    internal static RemoteRequest[] Merge(IEnumerable<RemoteRequest> incoming, IEnumerable<RemoteRequest> shown)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return incoming.Concat(shown).Where(r => r.RequestId is null || ids.Add(r.RequestId))
            .OrderByDescending(r => r.Completed).Take(Kept).ToArray();
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size)
    {
        var agent = app.Data.Current.Agents?.FirstOrDefault(a => a.Snapshot.Name == name);
        lock (_gate)
        {
            return TunnelViews.Detail(new(workspace, name, _insight, _problem, agent, app.Now, _live), size);
        }
    }

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => _insight is null
        ? [Hints.Refresh, Hints.Back, Hints.Quit]
        : [new("O", "Open"), new("C", "Copy URL"), new("H", "Host"), Hints.Refresh, Hints.Back, Hints.Quit];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        var letter = Selection.Letter(key);
        if (letter == 'R')
        {
            Load(app);
            app.Data.Refresh(remote: false);
            app.Status = new("Refreshing…");
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        if (_insight?.Tunnel is not { } tunnel || letter is not ('O' or 'C' or 'H' or 'N' or 'K' or 'X' or 'D'))
        {
            return Task.FromResult<Nav?>(null);
        }

        var url = tunnel.Url;
        switch (letter)
        {
            case 'O':
                app.Status = Uri.TryCreate(url, UriKind.Absolute, out var address) && app.Services.OpenBrowser(address)
                    ? new($"Opened {url} in your browser.", StatusKind.Success)
                    : new($"Couldn't open a browser here. The address is {url}", StatusKind.Warning);
                return Task.FromResult<Nav?>(Nav.Stay);
            case 'C':
                app.Status = app.Services.Copy(url)
                    ? new($"Copied {url} (through your terminal's clipboard support).", StatusKind.Success)
                    : new($"The address is {url}", StatusKind.Info);
                return Task.FromResult<Nav?>(Nav.Stay);
            case 'H':
                var profile = app.Data.Current.Profiles.FirstOrDefault(p => p.Name == name);
                var relay = app.Data.Current.Account?.Relay;
                return Task.FromResult<Nav?>(Nav.Open(new PromptScreen($"Host {name} from this machine", "Local target",
                    profile?.Target.TrimEnd('/') ?? "http://localhost:5173", "Where your app listens, for example http://localhost:5173.",
                    target => Names.TargetProblem(name, target),
                    target => Task.FromResult(Nav.Replace(new HostLaunchScreen(relay is null
                        ? ["host", name, "--target", target]
                        : ["host", name, "--target", target, "--relay", relay]))))));
        }

        var (action, verb) = letter switch
        {
            'N' => ("rename", "rename"),
            'K' => ("rotateKey", "rotate keys for"),
            'X' => ("revokeKey", "revoke keys for"),
            _ => ("delete", "delete"),
        };
        if (!tunnel.Actions.Contains(action, StringComparer.Ordinal))
        {
            app.Status = new($"Your role in {workspace} can't {verb} this tunnel.", StatusKind.Warning);
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        Screen next = letter switch
        {
            'N' => new PromptScreen($"Rename {name}", "New name", name,
                "Renaming changes the public address. Update saved profiles and app callback URLs afterwards.", Names.Problem,
                newName => Task.FromResult(Nav.Replace(new BusyScreen($"Renaming {name} to {newName}…",
                    token => app.Services.RunAsync(["rename", name, newName, "--workspace", workspace, "--yes"], token),
                    result => result.Succeeded ? new Nav(2, new TunnelDetailScreen(workspace, newName), false) : Nav.Back)))),
            'K' => new ConfirmScreen($"Rotate the key for {name}?",
                "A new connection key is created and saved securely on this machine. Agents using the old key are disconnected; restart hosting to use the new key.",
                "Rotate key", danger: false,
                () => Task.FromResult(Nav.Replace(new BusyScreen($"Rotating the key for {name}…",
                    token => app.Services.RunAsync(["key", "rotate", name, "--workspace", workspace, "--yes"], token))))),
            'X' => new ConfirmScreen($"Revoke every key for {name}?",
                "All live connection keys stop working and connected agents disconnect. Rotate a new key before hosting again.",
                "Revoke keys", danger: true,
                () => Task.FromResult(Nav.Replace(new BusyScreen($"Revoking keys for {name}…",
                    token => app.Services.RunAsync(["key", "revoke", name, "--workspace", workspace, "--yes"], token))))),
            _ => new ConfirmScreen($"Delete {name}?",
                $"The tunnel and its keys are removed from {workspace}. Connected agents disconnect and {TunnelViews.Host(url)} stops working.",
                "Delete tunnel", danger: true,
                () => Task.FromResult(Nav.Replace(new BusyScreen($"Deleting {name}…",
                    token => app.Services.RunAsync(["delete", name, "--workspace", workspace, "--yes"], token),
                    result => result.Succeeded ? new Nav(2, null, false) : Nav.Back)))),
        };
        return Task.FromResult<Nav?>(Nav.Open(next));
    }
}

/// <summary>
/// Runs <c>groundhog host</c> in the foreground from a menu. Its live view draws in the app's frame and Esc returns here,
/// after which this screen closes and reports how hosting ended.
/// </summary>
internal sealed class HostLaunchScreen(string[] args) : Screen
{
    internal override string Title => "Host";

    internal override async Task<Nav> UpdateAsync(InteractiveApp app)
    {
        var result = await app.Services.HostAsync(args, session => app.RunHostViewAsync(session, "Live"), app.Cancellation);
        app.Status = result.Succeeded
            ? new($"Stopped hosting {args[1]}.", StatusKind.Success)
            : new(result.Message, StatusKind.Error);
        app.Data.Refresh(remote: false);
        return Nav.Back;
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size) => new Text("");
    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => [];
}

/// <summary>Live session dashboard shared by foreground hosting and the running-agent view.</summary>
internal abstract class LiveSessionScreen : Screen
{
    protected int Tab { get; set; }

    protected abstract SessionSnapshot? Snapshot(InteractiveApp app);

    internal override IRenderable Render(InteractiveApp app, ViewSize size) => Snapshot(app) is { } snapshot
        ? SessionViews.Dashboard(snapshot, Tab, size, app.Now)
        : Theme.Section(Title, new Markup(Theme.Dim("Connecting to the local agent…")));

    protected bool SwitchTab(ConsoleKeyInfo key)
    {
        var tab = key.KeyChar switch { '1' => 0, '2' => 1, '3' => 2, _ => -1 };
        if (tab < 0 && key.Key is ConsoleKey.Tab or ConsoleKey.RightArrow or ConsoleKey.LeftArrow)
        {
            tab = (Tab + (key.Key == ConsoleKey.LeftArrow ? 2 : 1)) % 3;
        }

        if (tab < 0)
        {
            return false;
        }

        Tab = tab;
        return true;
    }

    protected static KeyHint[] TabHints => [new("1", "Overview"), new("2", "Requests"), new("3", "Statistics")];
}

/// <summary>The live view of a foreground host. Esc, Backspace or Q stops hosting; the view closes when the agent stops.</summary>
internal sealed class SessionScreen(HostSession session, string? title = null) : LiveSessionScreen
{
    private bool _stopping;

    internal override string Title => title ?? session.Name;
    internal override TimeSpan? Refresh => TimeSpan.FromMilliseconds(200);
    internal override bool CapturesText => true;

    internal override Task<Nav> UpdateAsync(InteractiveApp app) =>
        Task.FromResult(session.Running.IsCompleted ? Nav.Back : Nav.Stay);

    protected override SessionSnapshot? Snapshot(InteractiveApp app) => session.Snapshot();

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) =>
        [.. TabHints, new("Esc", _stopping ? "Stopping…" : "Stop hosting"), Hints.Help];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        if (SwitchTab(key))
        {
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        if (key.Key is ConsoleKey.Escape or ConsoleKey.Backspace || Selection.Letter(key) == 'Q')
        {
            _stopping = true;
            app.Status = new($"Stopping {session.Name}…");
            session.Stop();
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        return Task.FromResult<Nav?>(key.KeyChar == '?' || key.Key == ConsoleKey.F1 ? Nav.Open(new HelpScreen()) : Nav.Stay);
    }
}

/// <summary>A background or other-terminal agent on this machine, read over its local control pipe.</summary>
internal sealed class AgentScreen(LocalAgent agent) : LiveSessionScreen
{
    private SessionSnapshot? _snapshot = agent.Snapshot;
    private Task? _query;
    private bool _gone;

    internal override string Title => agent.Snapshot.Name;
    internal override TimeSpan? Refresh => TimeSpan.FromSeconds(1);

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        if (_query is null or { IsCompleted: true } && !_gone)
        {
            _query = QueryAsync(app);
        }

        return Task.FromResult(Nav.Stay);
    }

    private async Task QueryAsync(InteractiveApp app)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), app.Time, app.Cancellation);
        var snapshot = await app.Services.QueryAgentAsync(agent.Registration, false, app.Cancellation);
        if (snapshot is null)
        {
            _gone = true;
            app.Status = new($"{agent.Snapshot.Name} is no longer running.", StatusKind.Warning);
        }
        else
        {
            _snapshot = snapshot;
        }

        app.Invalidate();
    }

    protected override SessionSnapshot? Snapshot(InteractiveApp app) => _snapshot;

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => _gone
        ? [Hints.Back, Hints.Quit]
        : [.. TabHints, new("S", "Stop agent"), Hints.Back, Hints.Quit];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        if (SwitchTab(key))
        {
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        return Task.FromResult<Nav?>(Selection.Letter(key) == 'S' && !_gone
            ? Nav.Open(AgentsScreen.StopConfirmation(app, agent, closeDetail: true)) : null);
    }
}

/// <summary>Agents hosting from this machine.</summary>
internal sealed class AgentsScreen : Screen
{
    private int _selected;

    internal override string Title => "Running agents";
    internal override TimeSpan? Refresh => TimeSpan.FromSeconds(1);

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        app.Data.RefreshIfStale();
        return Task.FromResult(Nav.Stay);
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size)
    {
        var agents = app.Data.Current.Agents;
        _selected = Selection.Clamp(_selected, agents?.Length ?? 0);
        return LocalViews.Agents(agents, _selected, size, app.Now);
    }

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => app.Data.Current.Agents is { Length: > 0 } agents
        ? [.. Hints.List(agents.Length, Hints.Open, new KeyHint("S", "Stop")), Hints.Refresh, Hints.Back, Hints.Quit]
        : [Hints.Refresh, Hints.Back, Hints.Quit];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        var agents = app.Data.Current.Agents ?? [];
        if (Selection.Move(key, ref _selected, agents.Length))
        {
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        if (agents.Length == 0)
        {
            return Task.FromResult<Nav?>(null);
        }

        return Task.FromResult<Nav?>(key.Key == ConsoleKey.Enter ? Nav.Open(new AgentScreen(agents[_selected]))
            : Selection.Letter(key) == 'S' ? Nav.Open(StopConfirmation(app, agents[_selected], closeDetail: false))
            : null);
    }

    internal static Screen StopConfirmation(InteractiveApp app, LocalAgent agent, bool closeDetail) =>
        new ConfirmScreen($"Stop {agent.Snapshot.Name}?",
            "The local agent disconnects and its public address stops forwarding to this machine until you host it again.",
            "Stop agent", danger: true,
            () => Task.FromResult(Nav.Replace(new BusyScreen($"Stopping {agent.Snapshot.Name}…", async token =>
                await app.Services.QueryAgentAsync(agent.Registration, true, token) is null
                    ? new CommandResult(1, "", "The agent did not respond; it may already have stopped.")
                    : new CommandResult(0, "Stop requested.", ""),
                result => closeDetail && result.Succeeded ? new Nav(2, null, false) : Nav.Back))));
}

/// <summary>Saved profiles: host one, start it in the background, add or delete.</summary>
internal sealed class ProfilesScreen : Screen
{
    private int _selected;

    internal override string Title => "Saved profiles";
    internal override TimeSpan? Refresh => TimeSpan.FromSeconds(1);

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        app.Data.RefreshIfStale();
        return Task.FromResult(Nav.Stay);
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size)
    {
        var dashboard = app.Data.Current;
        _selected = Selection.Clamp(_selected, dashboard.Profiles.Length);
        return LocalViews.Profiles(dashboard.Profiles, dashboard.Agents, _selected, size);
    }

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => app.Data.Current.Profiles is { Length: > 0 } profiles
        ? [.. Hints.List(profiles.Length, new KeyHint("Enter", "Host"), new KeyHint("B", "Background")), new("A", "Add"), new("D", "Delete"),
            Hints.Back, Hints.Quit]
        : [new("A", "Add"), Hints.Back, Hints.Quit];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        var profiles = app.Data.Current.Profiles;
        if (Selection.Move(key, ref _selected, profiles.Length))
        {
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        var letter = Selection.Letter(key);
        if (letter == 'A')
        {
            return Task.FromResult<Nav?>(Nav.Open(new PromptScreen("Save a profile", "Tunnel name", "",
                "The name of a tunnel that already exists on the service.", Names.Problem,
                name => Task.FromResult(Nav.Replace(new PromptScreen($"Save a profile for {name}", "Local target", "http://localhost:5173",
                    "Where your app listens. Profiles never store keys.", target => Names.TargetProblem(name, target),
                    target => Task.FromResult(Nav.Replace(new BusyScreen($"Saving {name}…",
                        token => app.Services.RunAsync(["profile", "save", name, "--target", target], token))))))))));
        }

        if (profiles.Length == 0)
        {
            return Task.FromResult<Nav?>(null);
        }

        var profile = profiles[_selected];
        if (key.Key == ConsoleKey.Enter || letter == 'H')
        {
            return Task.FromResult<Nav?>(Nav.Open(new HostLaunchScreen(["host", profile.Name])));
        }

        return Task.FromResult<Nav?>(letter switch
        {
            'B' => Nav.Open(new BusyScreen($"Starting {profile.Name} in the background…",
                token => app.Services.RunAsync(["start", profile.Name], token))),
            'D' => Nav.Open(new ConfirmScreen($"Delete the profile {profile.Name}?",
                "Only the saved name and target are removed. The tunnel itself and any running agent are not affected.",
                "Delete profile", danger: true,
                () => Task.FromResult(Nav.Replace(new BusyScreen($"Deleting {profile.Name}…",
                    token => app.Services.RunAsync(["profile", "delete", profile.Name], token)))))),
            _ => null,
        });
    }
}

/// <summary>Workspaces as a tree with each workspace's tunnels; Enter makes one the default.</summary>
internal sealed class WorkspacesScreen : Screen
{
    // Written by background loads while the loop reads it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TunnelInfo[]?> _tunnels = new(StringComparer.Ordinal);
    private int _selected = -1;

    internal override string Title => "Workspaces";

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        var account = app.Data.Current.Account;
        if (account?.Account is not { } info || info.Workspaces.Length == 0)
        {
            return Task.FromResult(Nav.Stay);
        }

        if (_selected < 0)
        {
            _selected = Math.Max(0, Array.FindIndex(info.Workspaces, w => w.Slug == account.Workspace));
        }

        _selected = Selection.Clamp(_selected, info.Workspaces.Length);
        var slug = info.Workspaces[_selected].Slug;
        if (_tunnels.TryAdd(slug, null))
        {
            _ = LoadAsync(app, slug);
        }

        return Task.FromResult(Nav.Stay);
    }

    private async Task LoadAsync(InteractiveApp app, string slug)
    {
        try
        {
            _tunnels[slug] = await app.Services.TunnelsAsync(slug, app.Cancellation);
        }
        catch (Exception ex) when (ex is ManagementException or HttpRequestException
            or TaskCanceledException && !app.Cancellation.IsCancellationRequested)
        {
            _tunnels[slug] = [];
        }
        catch (OperationCanceledException) when (app.Cancellation.IsCancellationRequested)
        {
        }

        app.Invalidate();
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size) =>
        AccountViews.Workspaces(app.Data.Current.Account, _selected, _tunnels, size);

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => app.Data.Current.Account?.Account?.Workspaces is { Length: > 0 } workspaces
        ? [.. Hints.List(workspaces.Length, new KeyHint("Enter", "Use")), Hints.Refresh, Hints.Back, Hints.Quit]
        : [Hints.Refresh, Hints.Back, Hints.Quit];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        var workspaces = app.Data.Current.Account?.Account?.Workspaces ?? [];
        if (Selection.Move(key, ref _selected, workspaces.Length))
        {
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        if (Selection.Letter(key) == 'R')
        {
            _tunnels.Clear();
            return Task.FromResult<Nav?>(null);
        }

        if (key.Key == ConsoleKey.Enter && workspaces.Length > 0)
        {
            var slug = workspaces[_selected].Slug;
            return Task.FromResult<Nav?>(Nav.Open(new BusyScreen($"Switching to {slug}…",
                token => app.Services.RunAsync(["workspace", "use", slug], token))));
        }

        return Task.FromResult<Nav?>(null);
    }
}

/// <summary>The doctor checks as an animated checklist.</summary>
internal sealed class DiagnosticsScreen : Screen
{
    private CheckRow[] _rows = [];
    private Task? _running;

    internal override string Title => "Diagnostics";
    internal override TimeSpan? Refresh => _running is { IsCompleted: false } ? TimeSpan.FromMilliseconds(100) : null;

    internal override void Enter(InteractiveApp app) => Start(app);

    private void Start(InteractiveApp app)
    {
        if (_running is { IsCompleted: false })
        {
            return;
        }

        var dashboard = app.Data.Current;
        var profiles = dashboard.Profiles.Take(6).ToArray();
        var rows = new List<CheckRow> { new("Groundhog service", CheckState.Pending, null), new("Account", CheckState.Pending, null),
            new("Local agents", CheckState.Pending, null) };
        rows.AddRange(profiles.Length == 0 ? [new("Local targets", CheckState.Pending, null)]
            : profiles.Select(p => new CheckRow($"Target · {p.Name}", CheckState.Pending, null)));
        _rows = rows.ToArray();
        _running = RunAsync(app, profiles);
    }

    private async Task RunAsync(InteractiveApp app, TunnelProfile[] profiles)
    {
        try
        {
            Set(0, CheckState.Running, null);
            var account = app.Data.Current.Account ?? await app.Services.AccountAsync(app.Cancellation);
            var service = await app.Services.CheckServiceAsync(account.Relay, app.Cancellation);
            Set(0, service.Passed ? CheckState.Passed : CheckState.Failed,
                service.Passed ? $"{TunnelViews.Host(account.Relay)} is healthy and speaks protocol 1." : service.Detail);

            Set(1, CheckState.Running, null);
            account = await app.Services.AccountAsync(app.Cancellation);
            Set(1, account.SignedIn ? CheckState.Passed : CheckState.Warning, account.SignedIn
                ? $"Signed in as {account.Account!.Email}" + (account.Workspace is { } w ? $" · workspace {w}" : " · no workspace chosen")
                : $"{account.Problem} Remote tunnels need Sign in; hosting with a key does not.");

            Set(2, CheckState.Running, null);
            var agents = await app.Services.LocalAgentsAsync(app.Cancellation);
            Set(2, CheckState.Passed, agents.Length == 0 ? "None running." : $"{Dashboard.Plural(agents.Length, "agent")} running and responding.");

            if (profiles.Length == 0)
            {
                Set(3, CheckState.Passed, "No saved profiles to check.");
            }

            for (var i = 0; i < profiles.Length; i++)
            {
                Set(3 + i, CheckState.Running, null);
                var target = await app.Services.CheckTargetAsync(profiles[i].Target, app.Cancellation);
                Set(3 + i, target.Passed ? CheckState.Passed : CheckState.Failed,
                    $"{profiles[i].Target.TrimEnd('/')} · {target.Detail}");
            }
        }
        catch (OperationCanceledException) when (app.Cancellation.IsCancellationRequested)
        {
        }

        app.LastDiagnostics = _rows;
        app.Invalidate();
    }

    private void Set(int index, CheckState state, string? detail)
    {
        var rows = _rows.ToArray();
        rows[index] = rows[index] with { State = state, Detail = detail };
        _rows = rows;
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size) =>
        LocalViews.Diagnostics(_rows, app.Spinner, _running is { IsCompleted: true }, size);

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => _running is { IsCompleted: true }
        ? [new("R", "Run again"), Hints.Back, Hints.Quit]
        : [Hints.Back, Hints.Quit];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        if (Selection.Letter(key) == 'R')
        {
            Start(app);
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        return Task.FromResult<Nav?>(null);
    }
}

/// <summary>Browser-approved sign-in with the code shown large; sign out when already signed in.</summary>
internal sealed class SignInScreen : Screen
{
    private CancellationTokenSource? _cancel;
    private Task<CommandResult>? _signIn;
    private SignInChallenge? _challenge;
    private StatusMessage? _outcome;

    internal override string Title => "Sign in";
    internal override TimeSpan? Refresh => _signIn is { IsCompleted: false } ? TimeSpan.FromMilliseconds(100) : null;

    internal override void Enter(InteractiveApp app)
    {
        if (app.Data.Current.Account is { SignedIn: false })
        {
            Start(app);
        }
    }

    private void Start(InteractiveApp app)
    {
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(app.Cancellation);
        _challenge = null;
        _outcome = null;
        _signIn = app.Services.SignInAsync(challenge =>
        {
            _challenge = challenge;
            app.Invalidate();
        }, _cancel.Token);
    }

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        if (_signIn is not { IsCompleted: true } finished)
        {
            return Task.FromResult(Nav.Stay);
        }

        _signIn = null;
        _challenge = null;
        if (finished.IsCompletedSuccessfully && finished.Result.Succeeded)
        {
            app.Status = new("Signed in. Loading your workspace and tunnels…", StatusKind.Success);
            app.Data.Refresh();
            return Task.FromResult(Nav.Back);
        }

        _outcome = finished.IsCompletedSuccessfully ? new(finished.Result.Message, StatusKind.Error)
            : new("Sign-in was cancelled.", StatusKind.Info);
        return Task.FromResult(Nav.Stay);
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size) => AccountViews.SignIn(
        new(app.Data.Current.Account, _challenge, _signIn is not null, _outcome, app.Spinner, app.Now), size);

    // X is offered only when there is a session to end, and it always works when offered.
    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => _signIn is not null
        ? [new("Esc", "Cancel")]
        : app.Data.Current.Account is { SignedIn: true }
            ? [new("Enter", "Sign in again"), new("X", "Sign out"), Hints.Back, Hints.Quit]
            : [new("Enter", "Sign in"), Hints.Back, Hints.Quit];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        if (_signIn is not null)
        {
            if (key.Key is ConsoleKey.Escape or ConsoleKey.Backspace)
            {
                _cancel?.Cancel();
                // Observe the abandoned sign-in so its cancellation is never reported as unhandled.
                _signIn.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                _signIn = null;
                return Task.FromResult<Nav?>(Nav.Back);
            }

            return Task.FromResult<Nav?>(Nav.Stay);
        }

        if (key.Key == ConsoleKey.Enter)
        {
            Start(app);
            return Task.FromResult<Nav?>(Nav.Stay);
        }

        if (Selection.Letter(key) == 'X' && app.Data.Current.Account is { SignedIn: true })
        {
            return Task.FromResult<Nav?>(Nav.Open(new ConfirmScreen("Sign out?",
                "The CLI credential is revoked and removed from this machine, together with saved connection keys.",
                "Sign out", danger: true,
                () => Task.FromResult(Nav.Replace(new BusyScreen("Signing out…", token => app.Services.RunAsync(["logout"], token)))))));
        }

        return Task.FromResult<Nav?>(null);
    }
}

/// <summary>Keys, commands and version.</summary>
internal sealed class HelpScreen : Screen
{
    internal override string Title => "Help";
    internal override IRenderable Render(InteractiveApp app, ViewSize size) => HomeViews.Help(app.Services.Version, size);
    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => [Hints.Back, Hints.Quit];
}

/// <summary>Reads one line of text in a modal panel. Backspace edits; Esc cancels.</summary>
internal sealed class PromptScreen(string title, string label, string initial, string? hint, Func<string, string?> validate,
    Func<string, Task<Nav>> submit) : Screen
{
    private string _value = initial;
    private string? _error;

    internal override string Title => title;
    internal override bool CapturesText => true;
    internal string Value => _value;

    internal override IRenderable Render(InteractiveApp app, ViewSize size) =>
        Chrome.Modal(title, Chrome.Input(label, _value, hint, _error, Math.Min(size.Width, 72) - 6), size);

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => [new("Enter", "Confirm"), new("Esc", "Cancel")];

    internal override async Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                return Nav.Back;
            case ConsoleKey.Enter:
                var value = _value.Trim();
                _error = validate(value);
                return _error is null ? await submit(value) : Nav.Stay;
            case ConsoleKey.Backspace:
                _value = _value.Length == 0 ? _value : _value[..^1];
                return Nav.Stay;
        }

        if (!char.IsControl(key.KeyChar) && _value.Length < 200)
        {
            _value += key.KeyChar;
            _error = null;
        }

        return Nav.Stay;
    }
}

/// <summary>An in-frame yes/no question; destructive ones are drawn in red and need Y.</summary>
internal sealed class ConfirmScreen(string question, string detail, string action, bool danger, Func<Task<Nav>> confirm) : Screen
{
    internal override string Title => "Confirm";
    internal override bool CapturesText => true;

    internal override IRenderable Render(InteractiveApp app, ViewSize size) => Chrome.Modal(question, new Rows(
        new Markup(Theme.Safe(detail)), new Text(""),
        new Markup($"{Chrome.KeyCap("Y")} {Theme.Safe(action)}   {Chrome.KeyCap("N")} Cancel")), size, danger);

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => [new("Y", action), new("N", "Cancel")];

    internal override async Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key) => Selection.Letter(key) switch
    {
        'Y' => await confirm(),
        'N' => Nav.Back,
        _ => key.Key == ConsoleKey.Escape ? Nav.Back : Nav.Stay,
    };
}

/// <summary>
/// Runs one operation (usually one of the CLI's own commands) with a spinner, then reports its outcome on the status
/// line and refreshes the dashboard data. <c>after</c> chooses where to go next; by default it closes itself.
/// </summary>
internal sealed class BusyScreen(string label, Func<CancellationToken, Task<CommandResult>> work, Func<CommandResult, Nav>? after = null)
    : Screen
{
    private CancellationTokenSource? _cancel;
    private Task<CommandResult>? _work;

    internal override string Title => "Working";
    internal override TimeSpan? Refresh => TimeSpan.FromMilliseconds(100);
    internal override bool CapturesText => true;

    internal override void Enter(InteractiveApp app)
    {
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(app.Cancellation);
        _work = work(_cancel.Token);
    }

    internal override Task<Nav> UpdateAsync(InteractiveApp app)
    {
        if (_work is not { IsCompleted: true } done)
        {
            return Task.FromResult(Nav.Stay);
        }

        var result = done.IsCompletedSuccessfully ? done.Result
            : new CommandResult(1, "", done.IsCanceled ? "Cancelled." : "The operation failed unexpectedly.");
        app.Status = new(result.Message, result.Succeeded ? StatusKind.Success : StatusKind.Error);
        app.Data.Refresh();
        return Task.FromResult(after?.Invoke(result) ?? Nav.Back);
    }

    internal override IRenderable Render(InteractiveApp app, ViewSize size) =>
        Chrome.Modal(label, new Markup($"[{Theme.SignalTag}]{Markup.Escape(app.Spinner)}[/] {Theme.Safe(label)}"), size);

    internal override IReadOnlyList<KeyHint> Keys(InteractiveApp app) => [new("Esc", "Cancel")];

    internal override Task<Nav?> KeyAsync(InteractiveApp app, ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            _cancel?.Cancel();
            app.Status = new("Cancelling…");
        }

        return Task.FromResult<Nav?>(Nav.Stay);
    }
}
