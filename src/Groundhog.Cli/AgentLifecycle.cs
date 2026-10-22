using Groundhog.Agent;
using System.Diagnostics;
using System.Globalization;

namespace Groundhog.Cli;

internal static class AgentLifecycle
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellation)
    {
        if (args.Contains("--help", StringComparer.Ordinal)) { output.Write(GroundhogCommand.Usage); return 0; }
        var options = Options.Parse(args[1..]);
        options.RequireKnown("--condition", "--timeout");
        if (options.Positional is not [var name])
        {
            throw new ArgumentException("Give one profile or tunnel name.");
        }

        var seconds = options.Get("--timeout") is { } text ? double.Parse(text, CultureInfo.InvariantCulture) : 30;
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 3600)
        {
            throw new ArgumentException("Invalid timeout.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        if (args[0] == "wait")
        {
            var condition = options.Get("--condition") ?? "connected";
            if (condition is not ("connected" or "ready"))
            {
                throw new ArgumentException("Unknown condition.");
            }

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
            try
            {
                while (true)
                {
                    foreach (var registration in LocalSessions.Registrations().Where(r => r.Name == name))
                    {
                        var snapshot = await LocalSessions.QueryAsync(registration, false, deadline.Token);
                        if (snapshot?.State != "Connected")
                        {
                            continue;
                        }

                        if (condition == "ready")
                        {
                            try
                            {
                                using var request = new HttpRequestMessage(HttpMethod.Head, snapshot.Target);
                                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                                if (!response.IsSuccessStatusCode)
                                {
                                    continue;
                                }
                            }
                            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { continue; }
                        }
                        output.WriteLine($"Condition met: {condition}."); return 0;
                    }
                    await Task.Delay(200, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { error.WriteLine("groundhog: readiness timeout. Run status and doctor."); return 1; }
        }
        var profile = new ProfileStore(ProfileStore.DefaultDirectory).Read().SingleOrDefault(p => p.Name == name)
            ?? throw new ArgumentException("Save a profile before background hosting.");
        ProfileStore.Validate(profile);
        // The background agent has no terminal to report a missing key on, so check before starting it.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostArguments.KeyVariable))
            && GroundhogCommand.StoredKey(profile.ToHostArguments()) is null)
        {
            error.WriteLine($"groundhog: no connection key for {name}. Set the {HostArguments.KeyVariable} environment variable, " +
                "or host the tunnel once from the dashboard (groundhog dashboard), which saves its key on this computer.");
            return ExitCodes.BadArguments;
        }

        var existing = LocalSessions.Registrations().Where(r => r.Name == name).ToArray();
        foreach (var registration in existing)
        {
            var snapshot = await LocalSessions.QueryAsync(registration, false, cancellation);
            if (snapshot is null)
            {
                continue;
            }

            if (args[0] == "start") { error.WriteLine("groundhog: a local agent is already running."); return 1; }
            if (snapshot.Host == "Aspire")
            {
                error.WriteLine($"groundhog: {LocalSessions.StopRefusal(snapshot.ProcessName, snapshot.ProcessId)}");
                return ExitCodes.BadArguments;
            }
            await LocalSessions.QueryAsync(registration, true, cancellation);
            while (await LocalSessions.QueryAsync(registration, false, deadline.Token) is not null)
            {
                await Task.Delay(100, deadline.Token);
            }
        }
        var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate CLI executable.");
        // Windows shell launch detaches the agent from inherited console/pipe handles. On Unix, redirected
        // handles isolate output; the background entry point deliberately writes nothing to them.
        var windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = windows,
            CreateNoWindow = !windows,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = !windows,
            RedirectStandardOutput = !windows,
            RedirectStandardError = !windows
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(GroundhogCommand).Assembly.Location);
        }

        start.ArgumentList.Add("__host-background"); start.ArgumentList.Add(name);
        using var process = Process.Start(start) ?? throw new IOException("Could not start CLI.");
        try
        {
            while (!process.HasExited)
            {
                var registration = LocalSessions.Registrations().FirstOrDefault(r => r.ProcessId == process.Id);
                if (registration is not null && await LocalSessions.QueryAsync(registration, false, deadline.Token) is { State: "Connected" })
                { output.WriteLine($"Started local agent (PID {process.Id}). Use status, logs, or stop {name}."); return 0; }
                await Task.Delay(200, deadline.Token);
            }
            error.WriteLine("groundhog: background host exited. Run host in the foreground to see the failure."); return 1;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            cancellation.ThrowIfCancellationRequested();
            error.WriteLine("groundhog: startup timed out; background process stopped. Run host to diagnose."); return 1;
        }
    }
}
