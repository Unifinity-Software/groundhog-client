using System.Net;
using System.Security.Cryptography;
using System.Text;
using Groundhog.Protocol;

namespace Groundhog.Cli;

/// <summary>Moves one connected local CLI host to a second key before revoking the uniquely identified old key.</summary>
internal sealed class KeyRotation(string directory, ManagementClient client, IRotationHost? host = null, TimeProvider? clock = null)
{
    private readonly CredentialStore _credentials = new(directory);
    private readonly IRotationHost _host = host ?? new RotationHost();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal async Task RunAsync(LoginSession session, string workspace, string name, TimeSpan timeout, CancellationToken cancellation)
    {
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(2)) { throw new UsageException("Rotation timeout must be from 1 to 120 seconds."); }
        using var elapsed = new CancellationTokenSource(timeout, _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, elapsed.Token);
        var ct = deadline.Token;
        await using var operation = await LockAsync(session.Relay, workspace, name, ct);
        var path = ManagementClient.TunnelPath(workspace, name);
        var local = await _host.AgentsAsync(name, ct);
        if (local.Length == 0) { throw new ManagementException("No connected local CLI agent was found. A tunnel hosted elsewhere must receive its new key on that host first."); }
        if (local.Length != 1) { throw new ManagementException("Multiple local agents match this tunnel. Stop the obsolete process before rotating keys."); }
        var original = local[0];
        if (original.Registration.Host != "Cli" || original.Snapshot.Host != "Cli")
        { throw new ManagementException("This tunnel is managed by an Aspire AppHost or a library host. Move its new key to that host before revoking the old key."); }
        if (original.Snapshot.State != "Connected") { throw new ManagementException("Wait for the local CLI agent to connect before rotating keys."); }
        var keyEntry = RemoteCommands.KeyId(workspace, name);
        if (!session.TunnelKeys.TryGetValue(keyEntry, out var oldKey) || !ConnectionKey.IsWellFormed(oldKey)
            || !await _host.MatchesKeyAsync(original, oldKey, ct))
        { throw new ManagementException("The saved key cannot be verified against this running agent. Restart with the current CLI and saved key before rotating."); }
        var existing = await ReadAsync(path, ct);
        if (existing.Keys is null) { throw new ManagementException("This relay does not report connection key IDs. Update the service before rotating without downtime."); }
        if (existing.Keys.Length != 1)
        { throw new ManagementException("Rotation needs one unused key slot and one identifiable old key. Two live keys already occupy the slots; inspect key list and revoke an unused key first."); }
        var oldId = existing.Keys[0].Id;
        if (!ValidId(oldId) || !existing.Keys[0].Connected)
        { throw new ManagementException("The relay cannot identify the connected old key safely. No key was changed."); }
        var profile = new ProfileStore(directory).Read().SingleOrDefault(p => p.Name == name);
        if (profile is not null && new Uri(profile.Target) != new Uri(original.Snapshot.Target))
        { throw new ManagementException("The saved profile target differs from the running agent. Update the profile before rotating."); }
        if (profile is null && new Uri(original.Snapshot.Target).Scheme == "https")
        { throw new ManagementException("Save the HTTPS hosting profile, including its local certificate settings, before rotating keys."); }
        profile = (profile ?? new(name, original.Snapshot.Target)) with { Relay = session.Relay };

        string? newId = null;
        string? newKey = null;
        var saved = false;
        var oldRevocationAttempted = false;
        try
        {
            var added = await client.SendAsync<TunnelCreated>(HttpMethod.Post, path + "/keys", null, ct);
            newKey = added.ConnectionKey;
            var afterAdd = added.Tunnel.Keys ?? (await ReadAsync(path, ct)).Keys;
            var created = afterAdd?.Where(k => k.Id != oldId).ToArray();
            if (created is not [var createdKey] || !ValidId(createdKey.Id) || afterAdd?.Any(k => k.Id == oldId) != true)
            { throw new ManagementException("The relay did not identify the added key safely. The old key was not revoked; inspect key list before retrying."); }
            newId = createdKey.Id;
            if (!ConnectionKey.IsWellFormed(newKey)) { throw new ManagementException("The relay returned an invalid connection key."); }
            await SaveKeyAsync(session, keyEntry, oldKey, newKey, ct);
            saved = true;
            var replacement = await _host.ReplaceAsync(profile, ct);
            if (replacement.Registration.ProcessId == original.Registration.ProcessId || replacement.Registration.Pipe == original.Registration.Pipe
                || replacement.Registration.Host != "Cli" || replacement.Snapshot.Host != "Cli"
                || !await _host.MatchesKeyAsync(replacement, newKey, ct))
            { throw new ManagementException("The replacement agent could not be verified. The old key was not revoked."); }
            await WaitForConnectedAsync(path, name, newId, replacement, newKey, ct);
            ct.ThrowIfCancellationRequested();
            oldRevocationAttempted = true;
            await RevokeAsync(path, oldId, ct);
        }
        catch (Exception failure)
        {
            if (newId is null)
            {
                throw new ManagementException("Rotation stopped before an added key could be identified. The old key was not revoked; inspect key list before retrying.");
            }
            // Cancellation of the foreground command must not cancel its rollback or leave an unobserved restart.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                if (oldRevocationAttempted)
                {
                    var current = await ReadAsync(path, cleanup.Token);
                    if (current.Keys is null) { throw new ManagementException("The relay no longer reports key IDs."); }
                    if (!current.Keys.Any(k => k.Id == oldId) && current.Keys.Any(k => k.Id == newId)) { return; }
                }
                if (saved && newKey is not null)
                {
                    await SaveKeyAsync(session, keyEntry, newKey, oldKey, cleanup.Token);
                    var survivors = await _host.AgentsAsync(name, cleanup.Token);
                    var oldConnected = false;
                    foreach (var survivor in survivors.Where(a => a.Snapshot.State == "Connected"))
                    { oldConnected |= await _host.MatchesKeyAsync(survivor, oldKey, cleanup.Token); }
                    if (!oldConnected)
                    {
                        var restored = await _host.ReplaceAsync(profile, cleanup.Token);
                        await WaitForConnectedAsync(path, name, oldId, restored, oldKey, cleanup.Token);
                    }
                }
                if (newId is not null) { await RevokeAsync(path, newId, cleanup.Token); }
            }
            catch (Exception)
            {
                throw new ManagementException("Rotation could not verify or finish its rollback. Inspect key list, local status and the saved key before changing or revoking either key.");
            }
            if (failure is OperationCanceledException && cancellation.IsCancellationRequested) { throw; }
            throw new ManagementException(failure is OperationCanceledException
                ? "Rotation timed out. The new key was revoked and the previous saved key and hosting were restored."
                : "Rotation failed. The identified new key was revoked; the old key remains live and the saved key was restored when this operation had changed it.");
        }
    }

    private async Task WaitForConnectedAsync(string path, string name, string id, RotationAgent expected, string key, CancellationToken ct)
    {
        while (true)
        {
            var local = (await _host.AgentsAsync(name, ct)).Where(a => a.Snapshot.State == "Connected").ToArray();
            var remote = await ReadAsync(path, ct);
            if (local is [var active] && active.Registration.ProcessId == expected.Registration.ProcessId
                && active.Registration.Pipe == expected.Registration.Pipe && active.Registration.Host == "Cli"
                && remote.Keys?.Any(k => k.Id == id && k.Connected) == true && await _host.MatchesKeyAsync(active, key, ct)) { return; }
            await Task.Delay(TimeSpan.FromMilliseconds(100), _clock, ct);
        }
    }

    private Task<TunnelInfo> ReadAsync(string path, CancellationToken ct) => client.SendAsync<TunnelInfo>(HttpMethod.Get, path, null, ct);

    private async Task RevokeAsync(string path, string id, CancellationToken ct)
    {
        try { await client.SendAsync<TunnelInfo>(HttpMethod.Delete, path + "/keys/" + id, null, ct); }
        catch (ManagementException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            var current = await ReadAsync(path, ct);
            if (current.Keys is null || current.Keys.Any(k => k.Id == id)) { throw; }
        }
    }

    private Task<LoginSession?> SaveKeyAsync(LoginSession expected, string entry, string oldValue, string newValue, CancellationToken ct) =>
        _credentials.UpdateAsync(fresh =>
        {
            if (fresh is null || fresh.SessionId != expected.SessionId || fresh.Relay != expected.Relay
                || fresh.TunnelKeys.GetValueOrDefault(entry) != oldValue)
            { throw new ManagementException("Saved credentials changed during rotation. No stale key was written."); }
            fresh.TunnelKeys[entry] = newValue;
            return fresh;
        }, ct);

    private async Task<FileStream> LockAsync(string relay, string workspace, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relay + "/" + workspace + "/" + name)));
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, "rotation-" + identity + ".lock"), options); }
            catch (IOException) { await Task.Delay(TimeSpan.FromMilliseconds(20), _clock, ct); }
        }
    }

    private static bool ValidId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
