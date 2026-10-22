using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Groundhog.Agent.Tests;

public sealed class LocalPipeTests
{
    [Fact]
    public async Task Queued_snapshot_client_survives_previous_client_disconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stop = new CancellationTokenSource();
        var name = "queued-pipe-" + Guid.NewGuid().ToString("N")[..8];
        await using var session = new LocalSessions(new(name, new Uri("http://localhost:5173")), stop);
        var registration = Assert.Single(LocalSessions.Registrations(), r => r.Name == name);
        await using var first = Client(registration.Pipe);
        await first.ConnectAsync(ct);
        await using var queued = Client(registration.Pipe);
        var connected = queued.ConnectAsync(ct);
        // Unix sockets queue the second connection while the first holds the server. Windows waits for Disconnect.
        // Do not release the first request on Unix until the second is definitely in that listener's queue.
        if (!OperatingSystem.IsWindows()) { await connected; }
        await first.WriteAsync(Encoding.UTF8.GetBytes("snapshot\n"), ct);
        using var firstReader = new StreamReader(first, leaveOpen: true);
        var firstSnapshot = JsonSerializer.Deserialize<SessionSnapshot>((await firstReader.ReadLineAsync(ct))!);
        Assert.Equal(name, firstSnapshot!.Name);
        await connected;
        await queued.WriteAsync(Encoding.UTF8.GetBytes("snapshot\n"), ct);
        using var queuedReader = new StreamReader(queued, leaveOpen: true);
        var nextSnapshot = JsonSerializer.Deserialize<SessionSnapshot>((await queuedReader.ReadLineAsync(ct))!);
        Assert.Equal(name, nextSnapshot!.Name);
        Assert.False(stop.IsCancellationRequested);
        // A peer that closes without a request must not prevent a subsequent snapshot either.
        await using (var abandoned = Client(registration.Pipe)) { await abandoned.ConnectAsync(ct); }
        Assert.NotNull(await LocalSessions.QueryAsync(registration, false, ct));
    }

    private static NamedPipeClientStream Client(string pipe) => new(".", pipe, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
