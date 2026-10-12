using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using Groundhog.Agent;
using NuGet.Versioning;
using Spectre.Console;

namespace Groundhog.Cli;

internal sealed class UpdateCommands(
    UpdateCheck check,
    ToolInstallation? installation,
    bool interactive,
    Func<string, CancellationToken, Task<bool>> confirm,
    Func<ToolInstallation, string, CancellationToken, Task<int>> install,
    TextWriter output,
    TextWriter error,
    string current,
    HttpClient? ownedClient = null) : IDisposable
{
    internal static string Version => GroundhogClientInfo.FromAssembly("Groundhog.Cli", typeof(GroundhogCommand).Assembly).Version;

    internal static UpdateCommands CreateDefault(TextWriter output, TextWriter error)
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Groundhog.Cli/{Version}");
        return new(new(http, TimeProvider.System, ProfileStore.DefaultDirectory, Version),
            ToolInstallation.Find(typeof(GroundhogCommand).Assembly.Location, Environment.CurrentDirectory, Version),
            InteractiveCli.Available,
            (message, cancellation) => AnsiConsole.PromptAsync(new ConfirmationPrompt(message) { DefaultValue = false }, cancellation),
            (scope, version, cancellation) => InstallAsync(scope, version, output, error, cancellation),
            output, error, Version, http);
    }

    internal async Task<int> RunAsync(string[] args, CancellationToken cancellation)
    {
        if (args.Skip(1).Any(arg => arg is not ("--check" or "--yes" or "--prerelease"))
            || (args.Contains("--check", StringComparer.Ordinal) && args.Contains("--yes", StringComparer.Ordinal)))
        {
            error.WriteLine("groundhog: use update [--check | --yes] [--prerelease].");
            return ExitCodes.BadArguments;
        }
        var checkOnly = args.Contains("--check", StringComparer.Ordinal);
        var yes = args.Contains("--yes", StringComparer.Ordinal);
        if (!checkOnly && !yes && !interactive)
        {
            error.WriteLine("groundhog: updating requires confirmation. Use update --check, or update --yes to install.");
            return ExitCodes.BadArguments;
        }
        try
        {
            var status = await check.CheckAsync(true, NuGetVersion.Parse(current).IsPrerelease || args.Contains("--prerelease", StringComparer.Ordinal), cancellation);
            if (status.Available is null)
            {
                output.WriteLine($"Groundhog CLI {status.Current} is up to date.");
                return ExitCodes.Stopped;
            }
            output.WriteLine($"Groundhog CLI {status.Available} is available (current: {status.Current}).");
            if (checkOnly)
            {
                return ExitCodes.Stopped;
            }

            if (installation is null)
            {
                error.WriteLine("groundhog: this executable's .NET tool installation could not be identified. Update using its original installer.");
                return ExitCodes.Failed;
            }
            if (!yes && !await confirm($"Update Groundhog CLI to {status.Available}?", cancellation))
            {
                return ExitCodes.Stopped;
            }

            return await ApplyAsync(status.Available, cancellation);
        }
        catch (UpdateUnavailableException)
        {
            error.WriteLine("groundhog: could not check NuGet for updates. Check your connection and try again.");
            return ExitCodes.Failed;
        }
    }

    /// <summary>Returns true only after an accepted, successful update; the original command should then exit.</summary>
    internal async Task<bool> OfferAsync(CancellationToken cancellation)
    {
        if (!interactive || installation is null)
        {
            return false;
        }

        try
        {
            var status = await check.CheckAsync(false, NuGetVersion.Parse(current).IsPrerelease, cancellation);
            if (status.FromCache || status.Available is null)
            {
                return false;
            }

            output.WriteLine($"Groundhog CLI {status.Available} is available (current: {status.Current}).");
            if (!await confirm($"Update now before continuing?", cancellation))
            {
                return false;
            }

            return await ApplyAsync(status.Available, cancellation) == ExitCodes.Stopped;
        }
        catch (UpdateUnavailableException)
        {
            return false;
        }
    }

    internal static bool ShouldCheck(bool interactive, string[] args, Func<string, string?> environment) =>
        interactive && environment("GROUNDHOG_NO_UPDATE_CHECK") is not ("1" or "true")
        && !args.Contains("--no-update-check", StringComparer.Ordinal)
        && !args.Contains("--no-interactive", StringComparer.Ordinal)
        && !args.Any(arg => arg is "--help" or "-h" or "help" or "--version" or "update" or "__host-background")
        && !args.Contains("json", StringComparer.Ordinal) && !args.Contains("--output=json", StringComparer.Ordinal);

    private async Task<int> ApplyAsync(string version, CancellationToken cancellation)
    {
        output.WriteLine($"Updating Groundhog CLI to {version}…");
        try
        {
            if (await install(installation!, version, cancellation) == 0)
            {
                output.WriteLine($"Updated to {version}. Run your command again to use the new version.");
                return ExitCodes.Stopped;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            error.WriteLine("groundhog: could not start the .NET tool installer. Make sure the .NET SDK is installed.");
        }
        error.WriteLine("groundhog: the update did not complete. Retry with dotnet tool update for this installation after exiting Groundhog.");
        return ExitCodes.Failed;
    }

    private static async Task<int> InstallAsync(ToolInstallation scope, string version, TextWriter output, TextWriter error, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in scope.Arguments(version))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("Could not start .NET tool installer.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            output.Write(await stdout);
            error.Write(await stderr);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    public void Dispose() => ownedClient?.Dispose();
}
