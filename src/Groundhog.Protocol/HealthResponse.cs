using System.Text.Json.Serialization;

namespace Groundhog.Protocol;

/// <summary>
/// The body of <see cref="GroundhogProtocol.HealthPath"/>:
/// <c>{"status":"ok","version":"&lt;relay version&gt;","protocol":[1]}</c>.
/// </summary>
/// <param name="Status">Always <see cref="OkStatus"/> when the relay answers 200.</param>
/// <param name="Version">The relay's version.</param>
/// <param name="Protocol">The protocol versions the relay supports.</param>
public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("protocol")] IReadOnlyList<int> Protocol)
{
    /// <summary>The status value of a healthy relay.</summary>
    public const string OkStatus = "ok";
}
