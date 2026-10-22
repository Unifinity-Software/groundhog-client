using System.Diagnostics;
using Groundhog.Agent;

namespace Groundhog.Cli;

internal sealed record RotationAgent(SessionRegistration Registration, SessionSnapshot Snapshot);

internal interface IRotationHost
{
    Task<RotationAgent[]> AgentsAsync(string name, CancellationToken cancellation);
    Task<bool> MatchesKeyAsync(RotationAgent agent, string key, CancellationToken cancellation);
    Task<RotationAgent> ReplaceAsync(TunnelProfile profile, CancellationToken cancellation);
}

internal sealed class RotationHost(Func<SessionRegistration[]>? registrations = null) : IRotationHost
{
    private readonly Func<SessionRegistration[]> _registrations = registrations ?? LocalSessions.Registrations;

    public async Task<RotationAgent[]> AgentsAsync(string name, CancellationToken cancellation)
    {
        var answers = await Task.WhenAll(_registrations().Where(r => r.Name == name).Select(async registration =>
            await LocalSessions.QueryAsync(registration, false, cancellation) is { } snapshot ? new RotationAgent(registration, snapshot) : null));
        return answers.OfType<RotationAgent>().ToArray();
    }

    public Task<bool> MatchesKeyAsync(RotationAgent agent, string key, CancellationToken cancellation) =>
        LocalSessions.MatchesKeyAsync(agent.Registration, key, cancellation);

    public async Task<RotationAgent> ReplaceAsync(TunnelProfile profile, CancellationToken cancellation)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the CLI executable.");
        var windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = windows, CreateNoWindow = !windows, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = !windows, RedirectStandardOutput = !windows, RedirectStandardError = !windows,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        { start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Groundhog.Cli.dll")); }
        start.ArgumentList.Add("__host-background-stored");
        foreach (var argument in profile.ToHostArguments()) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new IOException("Could not start a replacement agent.");
        try
        {
            while (!process.HasExited)
            {
                var registration = _registrations().FirstOrDefault(r => r.ProcessId == process.Id && r.Name == profile.Name);
                if (registration is not null && await LocalSessions.QueryAsync(registration, false, cancellation) is { State: "Connected" } snapshot)
                { return new(registration, snapshot); }
                await Task.Delay(100, cancellation);
            }
            throw new ManagementException("The replacement agent exited. The old key has not been revoked.");
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            throw;
        }
    }
}
