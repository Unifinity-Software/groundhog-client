using System.Globalization;
using Groundhog.Agent;

namespace Groundhog.Cli;

/// <summary>The parsed arguments of <c>groundhog host</c>.</summary>
internal sealed record HostArguments(string Name, int Port, Uri Relay, string Key, int? Pool, bool Verbose,
    Uri? Target = null, string? LocalCaFile = null, bool InsecureLocal = false,
    GroundhogVisitorAccess? VisitorAccess = null, VisitorAccessFlags? AccessFlags = null)
{
    public Uri LocalTarget => Target ?? new Uri($"http://localhost:{Port}");
    /// <summary>The environment variable the key is read from when <c>--key</c> isn't given.</summary>
    public const string KeyVariable = "GROUNDHOG_KEY";

    /// <summary>
    /// Parses the arguments after <c>host</c>. Returns the arguments, or an error message that never repeats an
    /// argument's value (a mistyped option could be followed by the key).
    /// </summary>
    public static (HostArguments? Arguments, string? Error, bool Help) Parse(
        IReadOnlyList<string> args,
        Func<string, string?> getEnvironmentVariable)
    {
        if (args.Any(arg => arg is "-h" or "--help"))
        {
            return (null, null, true);
        }

        if (!VisitorAccessFlags.TryExtract(args, out var accessFlags, out var otherArgs, out var accessError))
        {
            return (null, accessError, false);
        }

        args = otherArgs;
        string? name = null;
        string? port = null;
        string relay = GroundhogService.DefaultRelayAddress;
        string? key = null;
        string? pool = null;
        string? target = null;
        string? localCa = null;
        var insecureLocal = false;
        var verbose = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=', StringComparison.Ordinal) is var equals and > 0)
            {
                inlineValue = arg[(equals + 1)..];
                arg = arg[..equals];
            }

            switch (arg)
            {
                case "-h" or "--help":
                    return (null, null, true);
                case "-v" or "--verbose":
                    verbose = true;
                    break;
                case "--insecure-local":
                    if (inlineValue is not null)
                    {
                        return (null, "--insecure-local does not take a value.", false);
                    }

                    insecureLocal = true;
                    break;
                case "--port" or "--target" or "--local-ca" or "--relay" or "--key" or "--pool":
                    var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrEmpty(value))
                    {
                        return (null, $"{arg} needs a value.", false);
                    }

                    switch (arg)
                    {
                        case "--port":
                            port = value;
                            break;
                        case "--target":
                            target = value;
                            break;
                        case "--local-ca":
                            localCa = value;
                            break;
                        case "--relay":
                            relay = value;
                            break;
                        case "--key":
                            key = value;
                            break;
                        default:
                            pool = value;
                            break;
                    }

                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        return (null, $"Unknown option {arg}.", false);
                    }

                    if (name is not null)
                    {
                        return (null, "Expected one tunnel name, but got more than one argument without an option.", false);
                    }

                    name = arg;
                    break;
            }
        }

        if (name is null)
        {
            return (null, "Give the tunnel's name, for example: groundhog host myapp --port 5173", false);
        }

        if (port is null && target is null)
        {
            return (null, "--port or --target is required: where your local app listens.", false);
        }

        if (port is not null && target is not null)
        {
            return (null, "Choose --port or --target, not both.", false);
        }

        var portNumber = 0;
        if (port is not null && (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out portNumber)
            || portNumber is < 1 or > 65535))
        {
            return (null, "--port must be a number from 1 to 65535.", false);
        }

        Uri? targetUri = null;
        if (target is not null)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out targetUri)
                || targetUri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(targetUri.Host)
                || targetUri.Port is < 1 or > 65535 || targetUri.UserInfo.Length != 0
                || targetUri.AbsolutePath != "/" || targetUri.Query.Length != 0 || targetUri.Fragment.Length != 0)
            {
                return (null, "--target must be an http:// or https:// address with no credentials, path, query or fragment.", false);
            }

            portNumber = targetUri.Port;
        }

        if (!Uri.TryCreate(relay, UriKind.Absolute, out var relayUri))
        {
            return (null, "--relay must be an absolute address, such as an https:// origin.", false);
        }

        int? poolSize = null;
        if (pool is not null)
        {
            if (!int.TryParse(pool, NumberStyles.None, CultureInfo.InvariantCulture, out var poolNumber))
            {
                return (null, "--pool must be a number.", false);
            }

            poolSize = poolNumber;
        }

        key ??= getEnvironmentVariable(KeyVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            return (null, $"No connection key: set the {KeyVariable} environment variable (or pass --key).", false);
        }

        GroundhogVisitorAccess? visitorAccess = null;
        if (accessFlags is not null && !accessFlags.TryBuildAgentAccess(getEnvironmentVariable, out visitorAccess, out accessError))
        {
            return (null, accessError, false);
        }

        return (new HostArguments(name, portNumber, relayUri, key, poolSize, verbose, targetUri, localCa, insecureLocal,
            visitorAccess, accessFlags), null, false);
    }

    /// <summary>Never prints the key.</summary>
    public override string ToString() =>
        $"{nameof(HostArguments)} {{ Name = {Name}, Port = {Port}, Relay = {Relay}, Pool = {Pool}, Verbose = {Verbose}, "
        + $"VisitorAccess = {AccessFlags?.Describe() ?? "unchanged"} }}";
}
