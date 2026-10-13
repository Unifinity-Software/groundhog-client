using Groundhog.Cli.Interactive;
using Spectre.Console;

namespace Groundhog.Cli;

/// <summary>Entry points of the interactive terminal experience; see <see cref="InteractiveApp"/>.</summary>
internal static class InteractiveCli
{
    internal static bool Disabled { get; set; }
    internal static bool Available => !Disabled && !Console.IsInputRedirected && !Console.IsOutputRedirected
        && AnsiConsole.Profile.Capabilities.Interactive;

    /// <summary>Runs the full-screen dashboard (<c>groundhog</c> with no arguments, or <c>groundhog dashboard</c>).</summary>
    internal static async Task<int> RunAsync(CancellationToken cancellation)
    {
        if (!Available)
        {
            Console.Error.WriteLine("groundhog: dashboard requires an interactive terminal. Use --help for commands.");
            return ExitCodes.BadArguments;
        }

        var app = new InteractiveApp(AnsiConsole.Console, new ConsoleKeySource(), TimeProvider.System, new HostedServices(), cancellation);
        return await app.RunAsync();
    }

    /// <summary><c>groundhog init</c>: a short guided prompt that saves a profile.</summary>
    internal static async Task SetupAsync(ProfileStore store, CancellationToken cancellation)
    {
        var name = await AnsiConsole.PromptAsync(new TextPrompt<string>("Tunnel name (already created on the service):"), cancellation);
        var target = await AnsiConsole.PromptAsync(new TextPrompt<string>("Local target:").DefaultValue("http://localhost:5173"), cancellation);
        try
        {
            store.Save(new(name, target));
            AnsiConsole.WriteLine($"Profile saved. Host it with: groundhog host {name}");
            AnsiConsole.WriteLine("Profiles never store keys; the key comes from GROUNDHOG_KEY or the one saved when you created the tunnel here.");
        }
        catch (ArgumentException ex) { AnsiConsole.Write(new Text(ex.Message)); }
    }
}
