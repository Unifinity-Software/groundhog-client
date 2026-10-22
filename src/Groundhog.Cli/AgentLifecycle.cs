using Groundhog.Agent;
using System.Diagnostics;
using System.Globalization;

namespace Groundhog.Cli;

internal static class AgentLifecycle
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellation)
    {
        if (args.Contains("--help", StringComparer.Ordinal)) { output.Write(GroundhogCommand.Usage); return 0; }
        var hostArgs = new List<string>();
        string? timeoutText = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--timeout")
            {
                if (timeoutText is not null || ++i >= args.Length) { throw new UsageException("Give --timeout once with seconds."); }
                timeoutText = args[i];
            }
            else if (args[i].StartsWith("--timeout=", StringComparison.Ordinal))
            {
                if (timeoutText is not null) { throw new UsageException("Give --timeout once."); }
                timeoutText = args[i][10..];
            }
            else { hostArgs.Add(args[i]); }
        }
        var options = args[0] == "wait" ? Options.Parse([.. hostArgs]) : Options.Parse([]);
        options.RequireKnown("--condition");
        var resolved = args[0] == "wait" ? [.. hostArgs] : ProjectConfiguration.ResolveHostArguments([.. hostArgs], new(ProfileStore.DefaultDirectory), Environment.CurrentDirectory);
        string name;
        if (args[0] == "wait")
        {
            name = options.Positional is [var waitName] ? waitName : throw new UsageException("Give one tunnel name.");
        }
        else
        {
            var (parsed, problem, _) = HostArguments.Parse(resolved, variable => Environment.GetEnvironmentVariable(variable)
                ?? (variable == HostArguments.KeyVariable ? GroundhogCommand.StoredKey(resolved) : null));
            if (parsed is null)
            {
                if (problem?.StartsWith("No connection key:", StringComparison.Ordinal) == true)
                {
                    var candidate = HostArguments.Parse(resolved, _ => Groundhog.Protocol.ConnectionKey.Generate()).Arguments;
                    throw new UsageException($"no connection key for {candidate?.Name ?? "this tunnel"}. Set {HostArguments.KeyVariable}, "
                        + "or host the tunnel once from groundhog dashboard to save its key on this computer.");
                }
                throw new UsageException(problem ?? "Invalid host options.");
            }
            name = parsed.Name;
            _ = new Groundhog.Agent.GroundhogAgent(new() { TunnelName = name, LocalTarget = parsed.LocalTarget,
                RelayAddress = parsed.Relay, Credential = new Groundhog.Agent.TunnelKeyCredential(parsed.Key), VisitorAccess = parsed.VisitorAccess });
        }
        var seconds = timeoutText is { } text ? double.Parse(text, CultureInfo.InvariantCulture) : 30;
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
            start.ArgumentList.Add(CliPaths.AssemblyFile);
        }

        start.ArgumentList.Add("__host-background");
        foreach (var argument in resolved) { start.ArgumentList.Add(argument); }
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
