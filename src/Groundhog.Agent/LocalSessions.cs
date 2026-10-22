using System.IO.Pipes;
using System.Text.Json;

namespace Groundhog.Agent;

internal sealed record SessionRegistration(string Name, string Pipe, int ProcessId, string Host = "Cli", string? ProcessName = null);

// Named pipes authorize the current OS user; registration files contain no traffic or credentials.
internal sealed class LocalSessions : IAsyncDisposable
{
    private readonly string _file;
    private readonly string _pipe = $"groundhog-{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _server;
    private readonly SessionTelemetry _telemetry;
    private readonly CancellationTokenSource _hostStop;
    internal static string DirectoryPath => Path.Combine(Environment.GetEnvironmentVariable("GROUNDHOG_HOME") is { Length: > 0 } configured
        ? Path.GetFullPath(configured) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Groundhog"), "sessions");

    internal LocalSessions(SessionTelemetry telemetry, CancellationTokenSource hostStop)
    {
        _telemetry = telemetry;
        _hostStop = hostStop;
        Directory.CreateDirectory(DirectoryPath);
        _file = Path.Combine(DirectoryPath, $"{_pipe}.json");
        File.WriteAllText(_file, JsonSerializer.Serialize(new SessionRegistration(telemetry.Snapshot().Name, _pipe, Environment.ProcessId, telemetry.Host, telemetry.ProcessName)));
        _server = ServeAsync();
    }

    internal static SessionRegistration[] Registrations() => !Directory.Exists(DirectoryPath) ? [] :
        Directory.EnumerateFiles(DirectoryPath, "groundhog-*.json").Select(ReadRegistration)
            .OfType<SessionRegistration>().ToArray();

    private static SessionRegistration? ReadRegistration(string file)
    {
        try
        {
            var registration = JsonSerializer.Deserialize<SessionRegistration>(File.ReadAllText(file));
            return registration is not null && registration.Pipe is { Length: 42 } && registration.Pipe.StartsWith("groundhog-", StringComparison.Ordinal)
                && registration.Pipe[10..].All(char.IsAsciiHexDigit) ? registration : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task<SessionSnapshot?> QueryAsync(SessionRegistration registration, bool stop,
        CancellationToken cancellation)
    {
        if (stop && registration.Host == "Aspire")
        {
            throw new ArgumentException(StopRefusal(registration.ProcessName, registration.ProcessId));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", registration.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync((stop ? "stop" : "snapshot").AsMemory(), timeout.Token);
            var response = await reader.ReadLineAsync(timeout.Token);
            var snapshot = response is null ? null : JsonSerializer.Deserialize<SessionSnapshot>(response);
            if (stop && snapshot?.Host == "Aspire")
            {
                throw new ArgumentException(StopRefusal(snapshot.ProcessName, snapshot.ProcessId));
            }
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            return null;
        }
    }

    internal static string StopRefusal(string? processName, int processId) =>
        $"The tunnel is hosted by Aspire AppHost {processName ?? "unknown"} (PID {processId}). Stop it from Aspire.";

    private async Task ServeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                // Fixed-size request: an idle or malicious local client cannot occupy this pipe indefinitely.
                var bytes = new byte[16];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await pipe.ReadAsync(bytes.AsMemory(count, 1), timeout.Token);
                    if (read == 0 || bytes[count] == '\n')
                    {
                        break;
                    }

                    count++;
                }
                var command = System.Text.Encoding.UTF8.GetString(bytes, 0, count).TrimEnd('\r');
                if (command is not ("snapshot" or "stop"))
                {
                    continue;
                }

                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(_telemetry.Snapshot()).AsMemory(), timeout.Token);
                if (command == "stop" && _telemetry.Host != "Aspire")
                {
                    _hostStop.Cancel();
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await _server;
        _shutdown.Dispose();
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }
}
