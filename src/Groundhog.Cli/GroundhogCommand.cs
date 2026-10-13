using System.Globalization;
using Groundhog.Agent;
using Groundhog.Protocol;

namespace Groundhog.Cli;

/// <summary>Exit codes of the <c>groundhog</c> command.</summary>
internal static class ExitCodes
{
    /// <summary>Stopped with Ctrl+C, or quietly replaced by a newer agent for the same tunnel.</summary>
    public const int Stopped = 0;

    /// <summary>Anything else that stopped the agent, for example a relay that doesn't speak this protocol version.</summary>
    public const int Failed = 1;

    /// <summary>The arguments were missing or invalid.</summary>
    public const int BadArguments = 2;

    /// <summary>The relay rejected the key, or the tunnel doesn't exist.</summary>
    public const int Rejected = 3;
}

/// <summary>Draws a foreground host's live view; it returns once <see cref="HostSession.Running"/> completes.</summary>
internal delegate Task HostView(HostSession session);

/// <summary>A running foreground host as its live view sees it.</summary>
/// <param name="Name">The tunnel name.</param>
/// <param name="Snapshot">Reads the session's current status, totals and retained requests.</param>
/// <param name="Running">Completes with the agent's stop reason.</param>
/// <param name="Stop">Asks the agent to stop.</param>
internal sealed record HostSession(string Name, Func<SessionSnapshot> Snapshot, Task<GroundhogAgentStopReason> Running, Action Stop);

/// <summary>
/// The command line: parses arguments, runs a <see cref="GroundhogAgent"/> and prints what it reports. All tunnel
/// behaviour lives in the library.
/// </summary>
internal static class GroundhogCommand
{
    /// <summary>
    /// The help text. <c>--relay</c>, accepted by host, profile save, doctor, login and the remote commands, is a
    /// development and testing override of the hosted service address and deliberately isn't listed.
    /// </summary>
    public const string Usage = """
        groundhog: give a local app a public HTTPS address through Groundhog.

        Usage:
          groundhog host <name> (--port <port> | --target <url>) [options]
          groundhog --version
          groundhog --help
          groundhog update [--check | --yes] [--prerelease]
          groundhog dashboard
          groundhog init
          groundhog profile list|save|delete [name] [--target <url>]
          groundhog ps [--output json]
          groundhog status|stats <name> [--output json] [--watch]
          groundhog requests <name> [--output json] [--follow] [filters]
          groundhog stop <name>
          groundhog start|restart <saved-profile>
          groundhog logs <name> [--follow] [--output json]
          groundhog wait <name> [--condition connected|ready] [--timeout <seconds>]
          groundhog doctor [--target <url>]
          groundhog login
          groundhog logout|whoami
          groundhog workspace list|use <slug>
          groundhog list [--workspace <slug>] [--status <state>] [--output json]
          groundhog describe <name> [--workspace <slug>] [--output json]
          groundhog create <name> [--workspace <slug>]
          groundhog rename <old-name> <new-name> [--workspace <slug>] [--yes]
          groundhog delete <name> [--workspace <slug>] [--yes]
          groundhog key rotate|revoke <name> [--workspace <slug>] [--yes]
          groundhog open <name> [--workspace <slug>]
          groundhog stats|requests <name> --remote [--workspace <slug>] [--output json]

        Requests options: --status 5xx|<code>, --method <method>, --path <substring>,
                          --since <number>s|m|h, --min-duration <milliseconds>, --limit <1..1000>
        Saved profiles remember a tunnel name and its local target, so host <profile> and
        start <profile> need no other options. Profiles never store keys; the key comes from
        GROUNDHOG_KEY, --key, or the key saved when you created or rotated the tunnel here.
        Status and traffic commands describe local sessions; history retains the last 1,000 requests.
        Global options: --no-interactive (disable menus), --no-color (disable colors; also NO_COLOR),
                        --no-update-check (skip the periodic update offer; also GROUNDHOG_NO_UPDATE_CHECK=1).

        Options for host:
          <name>           The tunnel's name, for example myapp.
          --port <port>    The local port to forward to (http://localhost:<port>).
          --target <url>   An HTTP or HTTPS local target, for example https://localhost:7132.
          --local-ca <file> Trust a PEM or DER CA/self-signed certificate for loopback HTTPS only.
          --insecure-local Skip local certificate validation for loopback HTTPS only (unsafe).
          --key <key>      The tunnel's connection key. Prefer the GROUNDHOG_KEY environment variable,
                           which keeps the key out of your shell history and process list.
          --pool <n>       Connections to keep open to the relay, 1 to 8 (default 2).
          -v, --verbose    Print one line per request.

        Exit codes: 0 stopped (Ctrl+C), 1 other failure, 2 bad arguments, 3 key rejected or unknown tunnel.

        """;

    public static async Task<int> RunAsync(
        string[] args,
        Func<string, string?> getEnvironmentVariable,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            if (args is ["update", ..])
            {
                if (args.Any(arg => arg is "--help" or "-h")) { output.Write(Usage); return ExitCodes.Stopped; }
                using var updates = UpdateCommands.CreateDefault(output, error);
                return await updates.RunAsync(args, cancellationToken);
            }
            if (args.Length > 0 && (RemoteCommands.Names.Contains(args[0], StringComparer.Ordinal)
                || (args[0] is "stats" or "requests" && args.Contains("--remote", StringComparer.Ordinal))))
            {
                return await RemoteCommands.RunAsync(args, output, error, cancellationToken);
            }
            if (args is ["dashboard"])
            {
                return await InteractiveCli.RunAsync(cancellationToken);
            }

            if (args is ["init"])
            {
                if (!InteractiveCli.Available) { error.WriteLine("groundhog: init requires an interactive terminal. Use profile save instead."); return ExitCodes.BadArguments; }
                await InteractiveCli.SetupAsync(new(ProfileStore.DefaultDirectory), cancellationToken);
                return 0;
            }
            if (args.Length > 0 && args[0] is "start" or "restart" or "wait")
            {
                return await AgentLifecycle.RunAsync(args, output, error, cancellationToken);
            }
            if (args.Length > 0 && args[0] is "profile" or "ps" or "status" or "stats" or "requests" or "logs" or "stop" or "doctor")
            {
                return await UtilityCommands.RunAsync(args, output, error, cancellationToken);
            }
        }
        catch (ManagementException ex)
        {
            error.WriteLine($"groundhog: {ex.Message}");
            return ExitCodes.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            error.WriteLine("groundhog: the relay could not be reached. Check network, address and trusted TLS certificates.");
            return ExitCodes.Failed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException
            or FormatException or OverflowException or System.Security.Cryptography.CryptographicException)
        {
            error.WriteLine("groundhog: invalid settings or inaccessible local state. Check --help and your profile file permissions.");
            return ExitCodes.BadArguments;
        }
        switch (args)
        {
            case [] or ["-h" or "--help" or "help"]:
                output.Write(Usage);
                return args.Length == 0 ? ExitCodes.BadArguments : ExitCodes.Stopped;
            case ["--version"]:
                output.WriteLine(UpdateCommands.Version);
                return ExitCodes.Stopped;
            case ["host", .. var rest]:
                return await HostAsync(rest, getEnvironmentVariable, output, error, cancellationToken);
            default:
                error.WriteLine("groundhog: unknown command. Run 'groundhog --help' for usage.");
                return ExitCodes.BadArguments;
        }
    }

    /// <summary>
    /// Runs <c>groundhog host</c>. In a terminal, or when the interactive app passes <paramref name="view"/>, the live
    /// view replaces the line-by-line output until the user stops hosting.
    /// </summary>
    internal static async Task<int> HostAsync(
        string[] args,
        Func<string, string?> getEnvironmentVariable,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        HostView? view = null)
    {
        if (args.Length == 1)
        {
            var profile = new ProfileStore(ProfileStore.DefaultDirectory).Read().SingleOrDefault(p => p.Name == args[0]);
            if (profile is not null)
            {
                args = profile.Relay is null ? [profile.Name, "--target", profile.Target]
                    : [profile.Name, "--target", profile.Target, "--relay", profile.Relay];
            }
        }
        var credential = getEnvironmentVariable(HostArguments.KeyVariable);
        if (string.IsNullOrWhiteSpace(credential) && args.Length > 0)
        {
            var saved = new CredentialStore(ProfileStore.DefaultDirectory).Read();
            if (saved?.Workspace is { } workspace)
            {
                // Never send a stored key to a different relay chosen on the command line or in a profile.
                var (candidate, _, _) = HostArguments.Parse(args, _ => ConnectionKey.Generate());
                if (candidate is not null && candidate.Relay == new Uri(saved.Relay)
                    && saved.TunnelKeys.TryGetValue(RemoteCommands.KeyId(workspace, args[0]), out var storedKey))
                {
                    var originalEnvironment = getEnvironmentVariable;
                    getEnvironmentVariable = variable => variable == HostArguments.KeyVariable ? storedKey : originalEnvironment(variable);
                }
            }
        }
        var (arguments, problem, help) = HostArguments.Parse(args, getEnvironmentVariable);
        if (help)
        {
            output.Write(Usage);
            return ExitCodes.Stopped;
        }

        if (arguments is null)
        {
            error.WriteLine($"groundhog: {problem}");
            error.WriteLine("Run 'groundhog --help' for usage.");
            return ExitCodes.BadArguments;
        }

        GroundhogAgent agent;
        try
        {
            agent = new GroundhogAgent(new GroundhogAgentOptions
            {
                RelayAddress = arguments.Relay,
                TunnelName = arguments.Name,
                Credential = new TunnelKeyCredential(arguments.Key),
                LocalTarget = arguments.LocalTarget,
                LocalCaFile = arguments.LocalCaFile,
                InsecureLocal = arguments.InsecureLocal,
                PoolSize = arguments.Pool ?? ConnectionPool.DefaultSize,
                Client = GroundhogClientInfo.FromAssembly("Groundhog.Cli", typeof(GroundhogCommand).Assembly),
            });
        }
        catch (ArgumentException ex)
        {
            // The library's messages say what's wrong without repeating the value.
            var message = ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty);
            error.WriteLine($"groundhog: {message}");
            return ExitCodes.BadArguments;
        }

        if (arguments.InsecureLocal)
        {
            error.WriteLine("WARNING: --insecure-local disables TLS certificate validation for the local target.");
        }

        using var hostStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var telemetry = new SessionTelemetry(arguments.Name, arguments.LocalTarget);
        await using var sessions = new LocalSessions(telemetry, hostStop);
        var interactive = view is not null || (ReferenceEquals(output, Console.Out) && InteractiveCli.Available);
        var reporter = new StatusReporter(output, error, arguments.LocalTarget, arguments.Verbose);
        agent.StatusChanged += (_, status) => { telemetry.OnStatus(status); if (!interactive) { reporter.OnStatus(status); } };
        agent.RequestCompleted += (_, request) => { telemetry.OnRequest(request); if (!interactive) { reporter.OnRequest(request); } };
        GroundhogAgentStopReason reason;
        if (interactive)
        {
            var running = agent.RunAsync(hostStop.Token);
            var session = new HostSession(arguments.Name, telemetry.Snapshot, running, hostStop.Cancel);
            try
            {
                // The view returns once the agent has stopped: Esc/Q, Ctrl+C, or the relay ending the session.
                await (view ?? Interactive.InteractiveApp.RunHostAsync)(session);
            }
            catch
            {
                await hostStop.CancelAsync();
                await running;
                throw;
            }

            reason = await running;
            var final = telemetry.Snapshot();
            if (reason is GroundhogAgentStopReason.Cancelled or GroundhogAgentStopReason.SessionReplaced)
            {
                output.WriteLine("Stopped.");
            }
            else
            {
                error.WriteLine($"Stopped: {final.Reason}");
            }
        }
        else
        {
            reason = await agent.RunAsync(hostStop.Token);
        }

        return reason switch
        {
            GroundhogAgentStopReason.Cancelled or GroundhogAgentStopReason.SessionReplaced => ExitCodes.Stopped,
            GroundhogAgentStopReason.CredentialRejected
                or GroundhogAgentStopReason.NotAllowed
                or GroundhogAgentStopReason.TunnelNotFound => ExitCodes.Rejected,
            _ => ExitCodes.Failed,
        };
    }

    /// <summary>Turns the agent's events into the command's output lines.</summary>
    private sealed class StatusReporter(TextWriter output, TextWriter error, Uri target, bool verbose)
    {
        private readonly Lock _gate = new();
        private readonly string _arrow = CanWrite(output, "→") ? "→" : "->";
        private bool _wasConnected;

        public void OnStatus(GroundhogAgentStatusEventArgs status)
        {
            lock (_gate)
            {
                switch (status.State)
                {
                    case GroundhogAgentState.Connected:
                        _wasConnected = true;
                        output.WriteLine($"Connected: {Display(status.PublicAddress)} {_arrow} {Display(target)}");
                        break;

                    case GroundhogAgentState.Reconnecting:
                        var delay = status.RetryDelay?.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) ?? "?";
                        output.WriteLine(_wasConnected
                            ? $"Disconnected: {status.Reason} Reconnecting in {delay} s."
                            : $"Couldn't connect: {status.Reason} Retrying in {delay} s.");
                        _wasConnected = false;
                        break;

                    case GroundhogAgentState.Stopped:
                        WriteStopped(status);
                        break;
                }
            }
        }

        public void OnRequest(GroundhogRequestEventArgs request)
        {
            if (!verbose)
            {
                return;
            }

            var method = request.IsWebSocket ? "WS" : request.Method;
            var note = request.Error == GroundhogErrors.LocalUnreachable
                ? $" (couldn't connect to {Display(target)})"
                : string.Empty;
            var duration = request.Duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
            lock (_gate)
            {
                output.WriteLine($"{method} {request.Path} {request.StatusCode} {duration} ms{note}");
            }
        }

        private void WriteStopped(GroundhogAgentStatusEventArgs status)
        {
            switch (status.StopReason)
            {
                case GroundhogAgentStopReason.Cancelled:
                    output.WriteLine("Stopped.");
                    break;
                case GroundhogAgentStopReason.SessionReplaced:
                    output.WriteLine($"Stopped: {status.Reason}");
                    break;
                case GroundhogAgentStopReason.CredentialRejected or GroundhogAgentStopReason.NotAllowed:
                    error.WriteLine($"Stopped: {status.Reason} Check the key in {HostArguments.KeyVariable} or --key.");
                    break;
                default:
                    error.WriteLine($"Stopped: {status.Reason}");
                    break;
            }
        }

        private static string Display(Uri? address) =>
            address is null ? "(unknown address)" : address.GetLeftPart(UriPartial.Authority);

        private static bool CanWrite(TextWriter writer, string text)
        {
            var encoding = writer.Encoding;
            return encoding.GetString(encoding.GetBytes(text)) == text;
        }
    }
}
