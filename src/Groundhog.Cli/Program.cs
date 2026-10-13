using System.Runtime.InteropServices;
using Groundhog.Cli;

// Before anything touches the console: Spectre reads the encoding once, when it first creates its console.
using var encoding = ConsoleEncoding.UseUtf8();
using var stop = new CancellationTokenSource();

// Ctrl+C (and a polite termination request) stops the agent cleanly; a second Ctrl+C ends the process at once.
void OnSignal(PosixSignalContext context)
{
    if (!stop.IsCancellationRequested)
    {
        context.Cancel = true;
        stop.Cancel();
    }
}

using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

InteractiveCli.Disabled = args.Contains("--no-interactive", StringComparer.Ordinal);
if (args.Contains("--no-color", StringComparer.Ordinal))
{
    Environment.SetEnvironmentVariable("NO_COLOR", "1");
}
var checkForUpdates = UpdateCommands.ShouldCheck(InteractiveCli.Available, args, Environment.GetEnvironmentVariable);
InteractiveCli.UpdateChecks = UpdateCommands.Enabled(InteractiveCli.Available, args, Environment.GetEnvironmentVariable);
args = args.Where(arg => arg is not ("--no-interactive" or "--no-color" or "--no-update-check")).ToArray();

try
{
    if (checkForUpdates)
    {
        // A broken cache or unavailable NuGet feed must never prevent the requested command from running.
        try
        {
            using var updates = UpdateCommands.CreateDefault(Console.Out, Console.Error);
            await updates.NoticeAsync(stop.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            // The explicit update command reports these errors; a startup offer is optional.
        }
    }
    if (args is ["__host-background", .. var backgroundArgs])
    {
        return await GroundhogCommand.RunAsync(["host", .. backgroundArgs], Environment.GetEnvironmentVariable, TextWriter.Null, TextWriter.Null, stop.Token);
    }
    if (args.Length == 0 && InteractiveCli.Available)
    {
        return await InteractiveCli.RunAsync(stop.Token);
    }

    return await GroundhogCommand.RunAsync(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
    or System.Security.Cryptography.CryptographicException or ArgumentException)
{
    Console.Error.WriteLine("groundhog: invalid or inaccessible local configuration. Check profile and credential file permissions.");
    return 2;
}
