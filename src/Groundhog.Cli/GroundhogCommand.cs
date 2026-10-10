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

/// <summary>
/// The command line: parses arguments, runs a <see cref="GroundhogAgent"/> and prints what it reports. All tunnel
/// behaviour lives in the library.
/// </summary>
internal static class GroundhogCommand
{
    public const string Usage = """
        groundhog: give a local app a public HTTPS address through Groundhog.

        Usage:
          groundhog host <name> (--port <port> | --target <url>) [options]
          groundhog --version
          groundhog --help

        Options for host:
          <name>           The tunnel's name, for example myapp.
          --port <port>    The local port to forward to (http://localhost:<port>).
          --target <url>   An HTTP or HTTPS local target, for example https://localhost:7132.
          --local-ca <file> Trust a PEM or DER CA/self-signed certificate for loopback HTTPS only.
          --insecure-local Skip local certificate validation for loopback HTTPS only (unsafe).
          --relay <url>    Optional service address override for self-hosting or service migrations.
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
        switch (args)
        {
            case [] or ["-h" or "--help" or "help"]:
                output.Write(Usage);
                return args.Length == 0 ? ExitCodes.BadArguments : ExitCodes.Stopped;
            case ["--version"]:
                output.WriteLine(GroundhogAgent.Version);
                return ExitCodes.Stopped;
            case ["host", .. var rest]:
                return await HostAsync(rest, getEnvironmentVariable, output, error, cancellationToken);
            default:
                error.WriteLine("groundhog: unknown command. Run 'groundhog --help' for usage.");
                return ExitCodes.BadArguments;
        }
    }

    private static async Task<int> HostAsync(
        string[] args,
        Func<string, string?> getEnvironmentVariable,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
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

        var reporter = new StatusReporter(output, error, arguments.LocalTarget, arguments.Verbose);
        agent.StatusChanged += (_, status) => reporter.OnStatus(status);
        agent.RequestCompleted += (_, request) => reporter.OnRequest(request);

        var reason = await agent.RunAsync(cancellationToken);
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
