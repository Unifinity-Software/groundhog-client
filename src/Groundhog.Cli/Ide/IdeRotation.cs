using System.Text.Json;

namespace Groundhog.Cli.Ide;

internal sealed partial class IdeServices
{
    private async Task<object> RotateKeyAsync(JsonElement parameters, LoginSession session, ManagementClient client,
        string workspace, string name, CancellationToken cancellation)
    {
        Confirm(parameters);
        var seconds = 30;
        if (parameters.TryGetProperty("timeoutSeconds", out var timeout) && (!timeout.TryGetInt32(out seconds) || seconds is < 1 or > 120))
        { throw Fault(-32602, "invalidParams", "Rotation timeout must be from 1 to 120 seconds."); }
        try
        {
            await new KeyRotation(_directory, client, new RotationHost(_registrations), _time)
                .RunAsync(session, workspace, name, TimeSpan.FromSeconds(seconds), cancellation);
        }
        catch (ManagementException ex) when (ex.Status is null)
        { throw Fault(-32017, "conflict", ex.Message); }
        await Notify("tunnels/changed", new { workspace });
        await Notify("agents/changed", new { agents = await AgentsAsync(cancellation) });
        return Empty;
    }
}
