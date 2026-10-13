using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using Groundhog.Agent;
using NuGet.Versioning;
using Spectre.Console;

namespace Groundhog.Cli;

/// <summary>How an update attempt ended.</summary>
internal enum InstallOutcome
{
    /// <summary>The new version is installed; the next run uses it.</summary>
    Installed,

    /// <summary>The installer could not finish.</summary>
    Failed,

    /// <summary>
    /// A helper finishes the update after this process exits. Windows keeps a running tool's files locked, so
    /// <c>dotnet tool update</c> can't replace a global or tool-path installation while Groundhog is open.
    /// </summary>
    AfterExit,
}

internal sealed class UpdateCommands(
    UpdateCheck check,
    ToolInstallation? installation,
    bool interactive,
    Func<string, CancellationToken, Task<bool>> confirm,
    Func<ToolInstallation, string, CancellationToken, Task<InstallOutcome>> install,
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
            (scope, version, cancellation) => FinishesAfterExit(scope)
                ? Task.FromResult(StartAfterExitHelper(scope, version) ? InstallOutcome.AfterExit : InstallOutcome.Failed)
                : InstallAsync(scope, version, output, error, cancellation),
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

    /// <summary>
    /// The newer version available to this installation, or null. Uses the once-a-day cache, so the dashboard can show
    /// an available update on every start without contacting NuGet each time. Never throws for feed problems.
    /// </summary>
    internal async Task<string?> AvailableAsync(CancellationToken cancellation)
    {
        if (installation is null)
        {
            return null;
        }

        try
        {
            return (await check.CheckAsync(false, NuGetVersion.Parse(current).IsPrerelease, cancellation)).Available;
        }
        catch (UpdateUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Before an interactive command: one line about a newer version, at most once a day. It never asks a question,
    /// so the command the user typed starts straight away.
    /// </summary>
    internal async Task NoticeAsync(CancellationToken cancellation)
    {
        if (!interactive || installation is null)
        {
            return;
        }

        try
        {
            var status = await check.CheckAsync(false, NuGetVersion.Parse(current).IsPrerelease, cancellation);
            if (!status.FromCache && status.Available is { } available)
            {
                error.WriteLine($"Groundhog CLI {available} is available (you have {status.Current}). Run groundhog update to install it.");
            }
        }
        catch (UpdateUnavailableException)
        {
        }
    }

    /// <summary>Installs <paramref name="version"/> into this installation; the dashboard calls it after closing.</summary>
    internal Task<int> InstallAsync(string version, CancellationToken cancellation) => installation is null
        ? Task.FromResult(ExitCodes.Failed)
        : ApplyAsync(version, cancellation);

    /// <summary>Whether update checks are allowed at all: an interactive terminal and no opt-out.</summary>
    internal static bool Enabled(bool interactive, string[] args, Func<string, string?> environment) =>
        interactive && environment("GROUNDHOG_NO_UPDATE_CHECK") is not ("1" or "true")
        && !args.Contains("--no-update-check", StringComparer.Ordinal)
        && !args.Contains("--no-interactive", StringComparer.Ordinal);

    /// <summary>Whether to print the once-a-day notice before running <paramref name="args"/>.</summary>
    internal static bool ShouldCheck(bool interactive, string[] args, Func<string, string?> environment) =>
        Enabled(interactive, args, environment)
        && !args.Any(arg => arg is "--help" or "-h" or "help" or "--version" or "update" or "__host-background")
        // The dashboard checks in the background and shows the result on its launch screen.
        && args.Where(arg => arg is not "--no-color").ToArray() is not ([] or ["dashboard"])
        && !args.Contains("json", StringComparer.Ordinal) && !args.Contains("--output=json", StringComparer.Ordinal);

    private async Task<int> ApplyAsync(string version, CancellationToken cancellation)
    {
        output.WriteLine($"Updating Groundhog CLI to {version}…");
        try
        {
            switch (await install(installation!, version, cancellation))
            {
                case InstallOutcome.Installed:
                    output.WriteLine($"Updated to {version}. Run your command again to use the new version.");
                    return ExitCodes.Stopped;
                case InstallOutcome.AfterExit:
                    output.WriteLine($"Groundhog closes now and a new window finishes installing {version}. Start groundhog again when it says it's done.");
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

    /// <summary>Global and tool-path installations on Windows can't be replaced while this process has them open.</summary>
    internal static bool FinishesAfterExit(ToolInstallation scope) => OperatingSystem.IsWindows() && scope.ToolPath is not null;

    /// <summary>
    /// The PowerShell script the helper window runs: wait for this process to exit, run the same <c>dotnet tool update</c>
    /// as an in-process update, and say plainly how it went before the window closes.
    /// </summary>
    internal static string AfterExitScript(ToolInstallation scope, string version, int processId)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var arguments = string.Join(' ', scope.Arguments(version).Select(Quote));
        return $$"""
            $Host.UI.RawUI.WindowTitle = 'Updating Groundhog CLI'
            Write-Host 'Updating Groundhog CLI to {{version}}. Waiting for Groundhog to close...'
            Wait-Process -Id {{processId}} -Timeout 60 -ErrorAction SilentlyContinue
            & dotnet {{arguments}}
            Write-Host ''
            if ($LASTEXITCODE -eq 0) {
                Write-Host 'Groundhog CLI {{version}} is installed. Run groundhog to start it.' -ForegroundColor Green
            } else {
                Write-Host 'The update did not complete. Another Groundhog window or a background agent may still be running.' -ForegroundColor Red
                Write-Host 'Close them (groundhog ps lists background agents; groundhog stop <name> stops one), then run groundhog update again.'
            }
            Read-Host 'Press Enter to close this window' | Out-Null
            """;
    }

    private static bool StartAfterExitHelper(ToolInstallation scope, string version)
    {
        var script = AfterExitScript(scope, version, Environment.ProcessId);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            // A window of its own: this console goes back to the shell as soon as Groundhog exits.
            UseShellExecute = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
        {
            start.ArgumentList.Add(argument);
        }

        using var helper = Process.Start(start);
        return helper is not null;
    }

    private static async Task<InstallOutcome> InstallAsync(ToolInstallation scope, string version, TextWriter output, TextWriter error, CancellationToken cancellation)
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
            return process.ExitCode == 0 ? InstallOutcome.Installed : InstallOutcome.Failed;
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
