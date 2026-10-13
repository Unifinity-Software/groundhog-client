using System.Threading.Channels;
using Groundhog.Agent;
using Groundhog.Cli.Interactive;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace Groundhog.Cli.Tests;

/// <summary>Keys for the interactive app. Running out of keys fails the test instead of waiting for input.</summary>
internal sealed class ScriptedKeys : IKeySource
{
    private readonly Queue<ConsoleKeyInfo> _keys = new();

    internal ScriptedKeys Press(params ConsoleKey[] keys)
    {
        foreach (var key in keys)
        {
            _keys.Enqueue(new ConsoleKeyInfo(key switch
            {
                ConsoleKey.Enter => '\r',
                ConsoleKey.Escape => '\u001b',
                ConsoleKey.Backspace => '\b',
                >= ConsoleKey.D0 and <= ConsoleKey.D9 => (char)('0' + (key - ConsoleKey.D0)),
                >= ConsoleKey.A and <= ConsoleKey.Z => (char)('a' + (key - ConsoleKey.A)),
                _ => '\0',
            }, key, false, false, false));
        }

        return this;
    }

    internal ScriptedKeys Type(string text)
    {
        foreach (var c in text)
        {
            _keys.Enqueue(new ConsoleKeyInfo(c, char.IsAsciiLetter(c) ? (ConsoleKey)char.ToUpperInvariant(c) : ConsoleKey.NoName, false, false, false));
        }

        return this;
    }

    internal int Remaining => _keys.Count;

    // Always "available": the app never idles in a test, so nothing waits on real or fake time.
    public bool KeyAvailable => true;

    public ConsoleKeyInfo ReadKey() =>
        _keys.TryDequeue(out var key) ? key : throw new InvalidOperationException("The test's key script ran out.");
}

/// <summary>
/// Synthetic account, tunnels, traffic and local agents for the interactive app, using the hosted addresses. Records
/// the commands the app runs instead of contacting a service.
/// </summary>
internal sealed class FakeServices : IInteractiveServices
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 10, 14, 24, 18, TimeSpan.Zero);
    internal static readonly string[] AllActions = ["rename", "rotateKey", "revokeKey", "delete"];

    internal bool SignedIn { get; set; } = true;
    internal List<string[]> Commands { get; } = [];
    internal List<string> Opened { get; } = [];
    internal List<string> Copied { get; } = [];
    internal SignInChallenge? Challenge { get; set; } = new("7F3A9C2B41D0", new Uri("https://groundhog.unifinity.com/cli-login?code=7F3A9C2B41D0"), Now.AddMinutes(9).AddSeconds(42));
    internal HostSession? LastSession { get; private set; }
    internal TunnelProfile[] ProfileList { get; set; } =
        [new("api-preview", "https://localhost:7132"), new("myapp", "http://localhost:5173")];
    internal LocalAgent[] AgentList { get; set; } = [Agent()];

    /// <summary>How many service reads and checks ran; a key that reloads or rechecks something changes it.</summary>
    internal int Calls { get; private set; }

    public string Version => "0.4.0-preview.1";

    internal static AccountInfo Account => new("Avery Developer", "developer@example.com",
        [new("web-team", "Web Team", "Owner"), new("personal", "Personal", "Owner")]);

    internal static TunnelInfo[] Tunnels =>
    [
        Tunnel("myapp", "Connected", 12, 1284, 12_400_000, 48_200_000, TimeSpan.FromHours(2)),
        Tunnel("api-preview", "Connected", 4, 412, 2_100_000, 3_900_000, TimeSpan.FromMinutes(38)),
        Tunnel("docs", "Disconnected", 0, 3, 18_000, 260_000, TimeSpan.FromDays(1)),
    ];

    internal static TunnelInfo Tunnel(string name, string state, long minute, long hour, long bytesIn, long bytesOut, TimeSpan age) =>
        new(name, $"https://{name}.groundhog.unifinity.com", state, Now - age, minute, hour, bytesIn, bytesOut, AllActions);

    internal static UsageSummary Usage
    {
        get
        {
            var perMinute = Enumerable.Range(0, 60).Select(i => (long?)(i is 7 or 8 ? null : 8 + (i * 7 % 23) + (i > 45 ? 14 : 0))).ToArray();
            return new(Now.AddMinutes(-60), Now, new SortedDictionary<string, long>(StringComparer.Ordinal)
            { ["2xx"] = 1121, ["3xx"] = 96, ["4xx"] = 61, ["5xx"] = 6 }, perMinute);
        }
    }

    internal static RemoteRequest[] RemoteRequests
    {
        get
        {
            string[] paths = ["/api/products", "/api/orders", "/", "/assets/app.js", "/api/search?q=", "/login", "/api/cart"];
            string[] methods = ["GET", "POST", "GET", "GET", "GET", "POST", "PUT"];
            int[] statuses = [200, 201, 200, 304, 200, 302, 200, 404, 200, 502];
            return Enumerable.Range(0, 30).Select(i => new RemoteRequest(Now.AddSeconds(-i * 17 - 3), methods[i % methods.Length],
                paths[i % paths.Length].Replace("?q=", "", StringComparison.Ordinal), statuses[i % statuses.Length], 12 + i * 37 % 240,
                $"req-{i:D3}")).ToArray();
        }
    }

    internal static SessionSnapshot Session(string name = "myapp", string target = "http://localhost:5173/")
    {
        var started = Now.AddHours(-2).AddMinutes(-14);
        string[] paths = ["/api/products", "/api/orders", "/", "/assets/app.js", "/api/search", "/hub", "/api/cart"];
        var recent = Enumerable.Range(0, 60).Select(i =>
        {
            var time = Now.AddSeconds(-(59 - i) * 9);
            var path = paths[i % paths.Length];
            var socket = path == "/hub";
            var status = socket ? 101 : i % 19 == 0 ? 502 : i % 11 == 0 ? 404 : i % 7 == 0 ? 304 : i % 5 == 0 ? 201 : 200;
            return new RequestRecord(time, i % 5 == 0 ? "POST" : "GET", path, status, socket ? 540_000 : 6 + i * 13 % 180, socket,
                status == 502 ? "local-unreachable" : null);
        }).ToArray();
        SessionLog[] logs = [new(started, "Connecting", null), new(started.AddSeconds(1), "Connected", null),
            new(Now.AddMinutes(-41), "Reconnecting", "The connection to the relay was lost."), new(Now.AddMinutes(-41).AddSeconds(2), "Connected", null)];
        return new(name, target, $"https://{name}.groundhog.unifinity.com/", "Connected", started, 1284, 3, 1, 142, recent, null, logs);
    }

    internal static LocalAgent Agent(string name = "myapp") =>
        new(new SessionRegistration(name, "groundhog-" + new string('0', 32), 4242), Session(name));

    public Task<AccountState> AccountAsync(CancellationToken cancellation)
    {
        Calls++;
        return Task.FromResult(SignedIn
            ? new AccountState(Account, "web-team", Now.AddHours(7).AddMinutes(12), GroundhogService.DefaultRelayAddress, null)
            : new AccountState(null, null, null, GroundhogService.DefaultRelayAddress, "Not signed in."));
    }

    public Task<TunnelInfo[]> TunnelsAsync(string workspace, CancellationToken cancellation) =>
        Task.FromResult(workspace == "web-team" ? Tunnels : [Tunnel("blog", "Disconnected", 0, 0, 0, 0, TimeSpan.FromDays(3))]);

    public Task<TunnelInsight> TunnelAsync(string workspace, string name, CancellationToken cancellation) =>
        Task.FromResult(new TunnelInsight(Tunnels.FirstOrDefault(t => t.Name == name) ?? Tunnel(name, "Connected", 0, 0, 0, 0, TimeSpan.Zero),
            Usage, RemoteRequests, null));

    /// <summary>Requests the live stream delivers, in order; the stream stays open until the screen closes it.</summary>
    internal Channel<RemoteRequest> LiveRequests { get; } = Channel.CreateUnbounded<RemoteRequest>();

    /// <summary>How many live streams were opened, and completes each time one is closed.</summary>
    internal int Follows { get; private set; }
    internal TaskCompletionSource FollowClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task FollowRequestsAsync(string workspace, string name, IEnumerable<RemoteRequest> known,
        Action<RemoteRequest> onRequest, Action<LiveState> onState, CancellationToken cancellation)
    {
        Follows++;
        try
        {
            onState(new(LiveStatus.Live));
            await foreach (var request in LiveRequests.Reader.ReadAllAsync(cancellation))
            {
                onRequest(request);
            }
        }
        finally
        {
            FollowClosed.TrySetResult();
        }
    }

    public Task<LocalAgent[]> LocalAgentsAsync(CancellationToken cancellation)
    {
        Calls++;
        return Task.FromResult(AgentList);
    }

    public Task<SessionSnapshot?> QueryAgentAsync(SessionRegistration registration, bool stop, CancellationToken cancellation)
    {
        if (stop)
        {
            Commands.Add(["stop-agent", registration.Name]);
        }

        return Task.FromResult<SessionSnapshot?>(Session(registration.Name));
    }

    public TunnelProfile[] Profiles() => ProfileList;

    public Task<CommandResult> RunAsync(string[] args, CancellationToken cancellation)
    {
        Commands.Add(args);
        return Task.FromResult(new CommandResult(0, args[0] switch
        {
            "delete" => "Tunnel deleted.",
            "rename" => $"Tunnel renamed to {args[2]}. Update saved hosting profiles.",
            "workspace" => "Workspace selected.",
            _ => "Done.",
        }, ""));
    }

    public async Task<CommandResult> HostAsync(string[] args, HostView view, CancellationToken cancellation)
    {
        Commands.Add(args);
        var stopped = new TaskCompletionSource<GroundhogAgentStopReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new HostSession(args[1], () => Session(args[1]), stopped.Task, () => stopped.TrySetResult(GroundhogAgentStopReason.Cancelled));
        LastSession = session;
        await view(session);
        Assert.True(session.Running.IsCompleted, "The live view returned while the agent was still running.");
        return new CommandResult(0, "Stopped.", "");
    }

    public Task<CommandResult> SignInAsync(Action<SignInChallenge> challenge, CancellationToken cancellation)
    {
        if (Challenge is { } shown)
        {
            challenge(shown);
        }

        var pending = new TaskCompletionSource<CommandResult>();
        cancellation.Register(() => pending.TrySetCanceled(cancellation));
        return pending.Task;
    }

    public Task<DoctorCheck> CheckServiceAsync(string relay, CancellationToken cancellation)
    {
        Calls++;
        return Task.FromResult(new DoctorCheck("Relay health and protocol", true, "Relay supports protocol 1."));
    }

    public Task<DoctorCheck> CheckTargetAsync(string target, CancellationToken cancellation) => Task.FromResult(target.Contains("7132", StringComparison.Ordinal)
        ? new DoctorCheck("Local target", false, "Start your app, verify its port, and trust its development certificate.")
        : new DoctorCheck("Local target", true, "HTTP 200; target is reachable."));

    public bool OpenBrowser(Uri address)
    {
        Opened.Add(address.ToString());
        return true;
    }

    public bool Copy(string text)
    {
        Copied.Add(text);
        return true;
    }

    internal string? Update { get; set; }

    public Task<string?> AvailableUpdateAsync(CancellationToken cancellation) => Task.FromResult(Update);
}

/// <summary>Builds the app against a Spectre test console and renders frames to text.</summary>
internal static class Harness
{
    internal static TestConsole Console(int width, int height, bool color = true)
    {
        var console = new TestConsole().Interactive().Colors(color ? ColorSystem.TrueColor : ColorSystem.NoColors).SupportsUnicode(true);
        console.Profile.Width = width;
        console.Profile.Height = height;
        return console;
    }

    internal static FakeTimeProvider Clock() => new(FakeServices.Now);

    /// <summary>The app under test; it stops with the test, or with <paramref name="stop"/> when given.</summary>
    internal static InteractiveApp App(TestConsole console, ScriptedKeys keys, FakeServices? services = null,
        AppOptions? options = null, CancellationTokenSource? stop = null) =>
        new(console, keys, Clock(), services ?? new FakeServices(), stop?.Token ?? TestContext.Current.CancellationToken,
            options ?? new AppOptions(Boot: false));

    /// <summary>Renders one frame and returns its lines, as the user would see them.</summary>
    internal static string[] Lines(IRenderable frame, int width, int height)
    {
        using var console = Console(width, height);
        console.Write(frame);
        return console.Output.NormalizeLineEndings().Split('\n');
    }

    internal static string Text(IRenderable frame, int width, int height) => string.Join('\n', Lines(frame, width, height));

    /// <summary>Writes a frame as HTML (for the screenshot script) and text when GROUNDHOG_SCREENSHOT_DIR is set.</summary>
    internal static void Export(string name, IRenderable frame, int width, int height)
    {
        if (Environment.GetEnvironmentVariable("GROUNDHOG_SCREENSHOT_DIR") is not { Length: > 0 } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var console = Console(width, height);
        using var recorder = console.CreateRecorder();
        recorder.Write(frame);
        File.WriteAllText(Path.Combine(directory, name + ".html"), recorder.ExportHtml());
        File.WriteAllText(Path.Combine(directory, name + ".txt"), recorder.ExportText());
    }
}
