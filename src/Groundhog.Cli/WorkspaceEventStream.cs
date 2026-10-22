using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Groundhog.Cli;

internal sealed class WorkspaceEventStream(HttpResponseMessage response, Stream body) : IAsyncDisposable
{
    internal TimeSpan? Retry { get; private set; }
    internal async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellation)
    {
        var parser = SseParser.Create(body);
        await foreach (var item in parser.EnumerateAsync(cancellation))
        {
            if (parser.ReconnectionInterval > TimeSpan.Zero) { Retry = parser.ReconnectionInterval; }
            if (item.EventType is not ("tunnel.connected" or "tunnel.disconnected" or "tunnel.created" or "tunnel.renamed"
                or "tunnel.deleted" or "tunnel.keysChanged")) { continue; }
            string? name;
            try
            {
                using var data = JsonDocument.Parse(item.Data);
                name = data.RootElement.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
            }
            catch (JsonException) { continue; }
            if (Groundhog.Protocol.TunnelName.IsValid(name)) { yield return name!; }
        }
    }
    public async ValueTask DisposeAsync() { await body.DisposeAsync(); response.Dispose(); }
}
