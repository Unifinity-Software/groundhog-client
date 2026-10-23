using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Agent.Tests;

public sealed class TerminalStatusStoreTests
{
    [Fact]
    public void Terminal_handoff_is_bounded_private_enum_only_and_expires_with_fake_time()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-terminal-" + Guid.NewGuid().ToString("N"));
        var clock = new FakeTimeProvider();
        try
        {
            for (var i = 0; i < TerminalStatusStore.Capacity + 1; i++)
            {
                TerminalStatusStore.Write("groundhog-" + Guid.NewGuid().ToString("N"), GroundhogAgentStopReason.CredentialRejected, clock, directory);
            }
            Assert.Equal(TerminalStatusStore.Capacity, Directory.GetFiles(directory).Length);
            var pipe = "groundhog-" + Guid.NewGuid().ToString("N");
            TerminalStatusStore.Write(pipe, GroundhogAgentStopReason.SessionReplaced, clock, directory);
            Assert.Equal(GroundhogAgentStopReason.SessionReplaced, TerminalStatusStore.Read(pipe, clock, directory));
            using var marker = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, pipe + ".json")));
            Assert.Equal(2, marker.RootElement.EnumerateObject().Count());
            TerminalStatusStore.Write(pipe, (GroundhogAgentStopReason)999, clock, directory);
            Assert.Equal(GroundhogAgentStopReason.SessionReplaced, TerminalStatusStore.Read(pipe, clock, directory));
            clock.Advance(TerminalStatusStore.Lifetime);
            Assert.Null(TerminalStatusStore.Read(pipe, clock, directory));
            TerminalStatusStore.Write("groundhog-" + Guid.NewGuid().ToString("N"), GroundhogAgentStopReason.CredentialRejected, clock, directory);
            Assert.Single(Directory.GetFiles(directory));
            File.WriteAllText(Path.Combine(directory, pipe + ".json"), "{\"Reason\":999,\"Time\":\"2000-01-01T00:00:00Z\"}");
            Assert.Null(TerminalStatusStore.Read(pipe, clock, directory));
            File.WriteAllText(Path.Combine(directory, pipe + ".json"), new string('x', 513));
            Assert.Null(TerminalStatusStore.Read(pipe, clock, directory));
            Assert.Null(TerminalStatusStore.Read("../outside", clock, directory));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
    }
}
