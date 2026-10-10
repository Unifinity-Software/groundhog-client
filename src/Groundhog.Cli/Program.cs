using System.Runtime.InteropServices;
using Groundhog.Cli;

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

return await GroundhogCommand.RunAsync(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, stop.Token);
