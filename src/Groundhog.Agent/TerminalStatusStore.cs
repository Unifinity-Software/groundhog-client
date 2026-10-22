using System.Text.Json;

namespace Groundhog.Agent;

// Private, short-lived handoff for IDEs that observed this exact random pipe while it was alive.
// This is separate from discovery and never records names, targets, traffic or raw relay reasons.
internal static class TerminalStatusStore
{
    internal const int Capacity = 64;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private sealed record Marker(GroundhogAgentStopReason Reason, DateTimeOffset Time);
    private static string DirectoryFor(string? directory) => directory ?? Path.Combine(LocalSessions.DirectoryPath, "terminal");
    private static bool ValidPipe(string pipe) => pipe.Length == 42 && pipe.StartsWith("groundhog-", StringComparison.Ordinal)
        && pipe.AsSpan(10).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    internal static void Write(string pipe, GroundhogAgentStopReason reason, TimeProvider clock, string? directory = null)
    {
        if (!ValidPipe(pipe) || !Enum.IsDefined(reason) || reason == GroundhogAgentStopReason.Cancelled) { return; }
        var folder = DirectoryFor(directory);
        var temporary = Path.Combine(folder, $"{pipe}-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Marker(reason, clock.GetUtcNow())));
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            File.Move(temporary, Path.Combine(folder, pipe + ".json"), overwrite: true);
            // Inspect at most the retained set's bodies. Extra or expired markers are removed without reading traffic.
            var retained = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "groundhog-*.json")
                .Where(file => ValidPipe(Path.GetFileNameWithoutExtension(file))).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (retained < Capacity && Read(Path.GetFileNameWithoutExtension(file), clock, folder) is not null) { retained++; }
                else { File.Delete(file); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static GroundhogAgentStopReason? Read(string pipe, TimeProvider clock, string? directory = null)
    {
        if (!ValidPipe(pipe)) { return null; }
        try
        {
            using var file = new FileStream(Path.Combine(DirectoryFor(directory), pipe + ".json"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 512) { return null; }
            var bytes = new byte[513];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = file.Read(bytes, count, bytes.Length - count);
                if (read == 0) { break; }
                count += read;
            }
            if (count > 512) { return null; }
            var marker = JsonSerializer.Deserialize<Marker>(bytes.AsSpan(0, count));
            var age = marker is null ? TimeSpan.MaxValue : clock.GetUtcNow() - marker.Time;
            return marker is not null && Enum.IsDefined(marker.Reason) && marker.Reason != GroundhogAgentStopReason.Cancelled
                && age >= TimeSpan.Zero && age < Lifetime ? marker.Reason : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static void Forget(string pipe, string? directory = null)
    {
        if (!ValidPipe(pipe)) { return; }
        try { File.Delete(Path.Combine(DirectoryFor(directory), pipe + ".json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
