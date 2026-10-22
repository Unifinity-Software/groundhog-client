using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Groundhog.Agent;
using Groundhog.Protocol;
using Spectre.Console;

namespace Groundhog.Cli;

internal static class UtilityCommands
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellation)
    {
        var command = args[0];
        if (args.Contains("--help", StringComparer.Ordinal)) { output.Write(GroundhogCommand.Usage); return 0; }
        var rest = args[1..];
        VisitorAccessFlags? access = null;
        if (command == "profile" && !VisitorAccessFlags.TryExtract(rest, out access, out rest, out var accessError))
        {
            throw new UsageException(accessError);
        }

        var options = Options.Parse(rest);
        var json = options.Get("--output") == "json";
        if (options.Get("--output") is { } format && format != "json")
        {
            throw new ArgumentException("Unsupported output format.");
        }

        if (command == "profile")
        {
            options.RequireKnown("--target", "--relay", "--output");
            var store = new ProfileStore(ProfileStore.DefaultDirectory);
            if (options.Positional is ["list"])
            {
                var profiles = store.Read();
                if (json)
                {
                    output.WriteLine(JsonSerializer.Serialize(profiles));
                }
                else
                {
                    Render(output, TerminalViews.Profiles(profiles));
                }

                return 0;
            }
            if (options.Positional is ["save", var name])
            {
                // A background host runs elsewhere, so the access file is saved as a full path.
                access = access?.AccessFile is { } file ? access with { AccessFile = Path.GetFullPath(file) } : access;
                store.Save(new(name, options.Get("--target") ?? throw new ArgumentException("Target required."), options.Get("--relay"), access));
                output.WriteLine($"Profile saved (connection keys and API keys are never stored). Host it with: groundhog host {name}"); return 0;
            }
            if (options.Positional is ["delete", var removed])
            {
                if (!store.Delete(removed)) { error.WriteLine("groundhog: no such saved profile."); return 1; }
                output.WriteLine("Profile deleted."); return 0;
            }
            throw new ArgumentException("Invalid profile command.");
        }
        if (command == "doctor")
        {
            options.RequireKnown("--target", "--relay", "--output");
            if (options.Positional.Length != 0)
            {
                throw new ArgumentException("Unexpected arguments.");
            }

            return await DoctorAsync(options, output, cancellation);
        }
        if (command == "requests")
        {
            options.RequireKnown("--output", "--follow", "--status", "--method", "--path", "--since", "--min-duration", "--limit");
        }
        else if (command == "logs") { options.RequireKnown("--output", "--follow"); }
        else if (command == "stop") { options.RequireKnown(); }
        else { options.RequireKnown("--output", "--watch"); }
        if (options.Positional.Length > 1 || (command != "ps" && options.Positional.Length != 1))
        {
            throw new ArgumentException("Give one local tunnel name.");
        }

        var nameFilter = options.Positional.FirstOrDefault();
        var watch = options.Has("--watch") || options.Has("--follow");
        if (watch && json)
        {
            throw new ArgumentException("JSON output is a snapshot.");
        }
        // Validate filters even if no agent is available.
        _ = Filter([], options, DateTimeOffset.UtcNow);
        LiveDisplayContext? liveContext = null;
        var seenRequests = new HashSet<RequestRecord>();
        var seenLogs = new HashSet<SessionLog>();
        void Display(Spectre.Console.Rendering.IRenderable content)
        {
            if (liveContext is null) { Render(output, content); }
            else { liveContext.UpdateTarget(content); }
        }
        async Task<int> ObserveAsync()
        {
            do
            {
                cancellation.ThrowIfCancellationRequested();
                var registrations = LocalSessions.Registrations().Where(r => nameFilter is null || r.Name == nameFilter).ToArray();
                var sessions = new List<SessionSnapshot>();
                var responding = new List<SessionRegistration>();
                foreach (var registration in registrations)
                {
                    var snapshot = await LocalSessions.QueryAsync(registration, false, cancellation);
                    if (snapshot is not null)
                    {
                        sessions.Add(snapshot);
                        responding.Add(registration);
                    }
                }
                if (command != "ps" && sessions.Count > 1)
                { error.WriteLine("groundhog: multiple responsive local agents match; stop the obsolete process first."); return 1; }
                if (command != "ps" && sessions.Count == 0)
                { error.WriteLine("groundhog: no responsive local agent. Start it with host; ps lists local sessions."); return 1; }
                if (command == "stop")
                {
                    var session = sessions[0];
                    if (session.Host == "Aspire")
                    {
                        error.WriteLine($"groundhog: {LocalSessions.StopRefusal(session.ProcessName, session.ProcessId)}");
                        return ExitCodes.BadArguments;
                    }
                    await LocalSessions.QueryAsync(responding[0], true, cancellation);
                    output.WriteLine("Stop requested."); return 0;
                }
                if (command == "ps")
                {
                    if (json)
                    {
                        output.WriteLine(JsonSerializer.Serialize(sessions.Select(s => new { s.Name, s.State, s.Target, s.PublicAddress, s.Started, s.Host, s.ProcessName, s.ProcessId })));
                    }
                    else
                    {
                        var table = new Table().AddColumn("Local agent").AddColumn("Host").AddColumn("State").AddColumn("Target").AddColumn("Public URL");
                        foreach (var s in sessions)
                        {
                            table.AddRow(TerminalViews.Safe(s.Name), TerminalViews.Safe(s.Host), TerminalViews.Safe(s.State), TerminalViews.Safe(s.Target), TerminalViews.Safe(s.PublicAddress));
                        }

                        Display(table);
                    }
                }
                else
                {
                    var session = sessions[0];
                    if (command == "logs")
                    {
                        if (json) { output.WriteLine(JsonSerializer.Serialize(session.Logs)); }
                        else
                        {
                            seenLogs.IntersectWith(session.Logs);
                            foreach (var log in session.Logs.Where(log => !watch || seenLogs.Add(log)))
                            {
                                output.WriteLine($"{log.Time:u} {log.State}: {new string((log.Reason ?? "").Where(c => !char.IsControl(c)).ToArray())}");
                            }
                        }
                    }
                    else if (command == "requests")
                    {
                        seenRequests.IntersectWith(session.Recent);
                        var requests = Filter(session.Recent, options, DateTimeOffset.UtcNow)
                            .Where(r => !watch || seenRequests.Add(r)).ToArray();
                        if (json)
                        {
                            output.WriteLine(JsonSerializer.Serialize(requests));
                        }
                        else
                        {
                            if (requests.Length > 0 || !watch) { Display(TerminalViews.Requests(requests)); }
                        }
                    }
                    else if (json)
                    {
                        output.WriteLine(JsonSerializer.Serialize(session));
                    }
                    else
                    {
                        Display(TerminalViews.Session(session, command));
                    }
                }
                if (watch)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
                }
            } while (watch);
            return 0;
        }
        if (watch && (command is "status" or "stats" or "ps") && ReferenceEquals(output, Console.Out) && InteractiveCli.Available)
        {
            var result = 0;
            await AnsiConsole.Live(new Text("Reading local agent…")).StartAsync(async context =>
            {
                liveContext = context;
                result = await ObserveAsync();
            });
            return result;
        }
        return await ObserveAsync();
    }

    internal static RequestRecord[] Filter(IEnumerable<RequestRecord> records, Options options, DateTimeOffset now)
    {
        var status = options.Get("--status");
        int? exact = null;
        int? group = null;
        if (status is not null)
        {
            if (status.Length == 3 && status[1..] == "xx" && status[0] is >= '1' and <= '5')
            {
                group = status[0] - '0';
            }
            else if (int.TryParse(status, out var code) && code is >= 100 and <= 599)
            {
                exact = code;
            }
            else
            {
                throw new ArgumentException("Invalid status filter.");
            }
        }
        var minimum = options.Get("--min-duration") is { } duration ? double.Parse(duration, CultureInfo.InvariantCulture) : 0;
        if (!double.IsFinite(minimum) || minimum < 0)
        {
            throw new ArgumentException("Invalid duration.");
        }

        var limit = options.Get("--limit") is { } maximum ? int.Parse(maximum, CultureInfo.InvariantCulture) : 50;
        if (limit is < 1 or > SessionTelemetry.Capacity)
        {
            throw new ArgumentException("Invalid limit.");
        }

        var since = options.Get("--since") is { } age ? now - ParseAge(age) : DateTimeOffset.MinValue;
        return records.Where(r => (exact is null || r.Status == exact) && (group is null || r.Status / 100 == group)
            && (options.Get("--method") is not { } method || r.Method.Equals(method, StringComparison.OrdinalIgnoreCase))
            && (options.Get("--path") is not { } path || r.Path.Contains(path, StringComparison.Ordinal))
            && r.DurationMs >= minimum && r.Time >= since).OrderByDescending(r => r.Time).Take(limit).ToArray();
    }

    private static TimeSpan ParseAge(string text)
    {
        if (text.Length < 2 || !double.TryParse(text[..^1], CultureInfo.InvariantCulture, out var count)
            || !double.IsFinite(count) || count <= 0 || count > 100000)
        {
            throw new ArgumentException("Invalid time window.");
        }

        return text[^1] switch
        {
            's' => TimeSpan.FromSeconds(count),
            'm' => TimeSpan.FromMinutes(count),
            'h' => TimeSpan.FromHours(count),
            _ => throw new ArgumentException("Use s, m or h.")
        };
    }

    private static async Task<int> DoctorAsync(Options options, TextWriter output, CancellationToken cancellation)
    {
        var relay = new Uri(options.Get("--relay") ?? GroundhogService.DefaultRelayAddress);
        ProfileStore.Validate(new("doctor-check", options.Get("--target") ?? "http://localhost:5173", relay.ToString()));
        using var client = DoctorClient();
        var checks = new List<DoctorCheck> { await CheckRelayAsync(client, relay, cancellation) };
        if (options.Get("--target") is { } target)
        {
            checks.Add(await CheckTargetAsync(client, target, cancellation));
        }

        if (options.Get("--output") == "json")
        {
            output.WriteLine(JsonSerializer.Serialize(checks));
        }
        else
        {
            foreach (var check in checks)
            {
                output.WriteLine(JsonSerializer.Serialize(check));
            }
        }

        return checks.All(c => c.Passed) ? 0 : 1;
    }

    internal static HttpClient DoctorClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Checks that the relay is healthy and speaks protocol 1, with normal TLS validation.</summary>
    internal static async Task<DoctorCheck> CheckRelayAsync(HttpClient client, Uri relay, CancellationToken cancellation)
    {
        try
        {
            var health = await client.GetFromJsonAsync<HealthResponse>(new Uri(relay, "/_groundhog/v1/health"), cancellation);
            var compatible = health?.Protocol.Contains(1) == true;
            return new("Relay health and protocol", compatible, compatible ? "Relay supports protocol 1." : "Relay did not advertise protocol 1.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            return new("Relay", false, "Check network, relay address and trusted TLS certificates.");
        }
    }

    /// <summary>Checks that a local target answers a HEAD request; any HTTP response proves reachability.</summary>
    internal static async Task<DoctorCheck> CheckTargetAsync(HttpClient client, string target, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            return new("Local target", true, $"HTTP {(int)response.StatusCode}; target is reachable.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            return new("Local target", false, "Start your app, verify its port, and trust its development certificate.");
        }
    }

    internal static void Render(TextWriter output, Spectre.Console.Rendering.IRenderable renderable)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected
            ? AnsiSupport.Detect : AnsiSupport.No,
            Out = new AnsiConsoleOutput(output)
        });
        console.Write(renderable);
    }
}

/// <summary>One <c>doctor</c> result; its JSON shape is part of the script-facing output.</summary>
internal sealed record DoctorCheck(string Check, bool Passed, string Detail);

internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    internal string[] Positional { get; private set; } = [];
    internal string? Get(string key) => _values.GetValueOrDefault(key);
    internal bool Has(string key) => _values.ContainsKey(key);
    internal void RequireKnown(params string[] allowed)
    {
        if (_values.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Unknown option.");
        }
    }
    internal static Options Parse(string[] args)
    {
        var options = new Options();
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-')) { positional.Add(args[i]); continue; }
            var split = args[i].Split('=', 2);
            var key = split[0];
            string? value = null;
            if (key is "--watch" or "--follow" or "--yes" or "--remote" or "--agent-managed")
            {
                if (split.Length != 1)
                {
                    throw new ArgumentException("Flag takes no value.");
                }
            }
            else
            {
                value = split.Length == 2 ? split[1] : ++i < args.Length ? args[i] : null;
                if (string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Option needs a value.");
                }
            }
            if (!options._values.TryAdd(key, value))
            {
                throw new ArgumentException("Repeated option.");
            }
        }
        options.Positional = positional.ToArray();
        return options;
    }
}
