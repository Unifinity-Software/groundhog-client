using System.Net;

namespace Groundhog.Cli;

internal sealed record ServiceCapabilities(string[]? Capabilities)
{
    internal bool Has(string capability) => Capabilities?.Contains(capability, StringComparer.Ordinal) == true;
}

/// <summary>Refreshes a saved session once, under the same cross-process lock as credential mutations.</summary>
internal sealed class SessionAuthentication(CredentialStore store,
    Func<string, string?, ManagementClient>? createClient = null, TimeProvider? clock = null)
{
    private readonly Func<string, string?, ManagementClient> _createClient = createClient ?? ((relay, token) => new(relay, token));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal async Task<LoginSession?> GetSessionAsync(CancellationToken cancellation)
    {
        var current = store.Read();
        if (current is null) { return null; }
        if (current.SessionId is null)
        {
            current = await store.UpdateAsync(fresh => fresh is null || fresh.SessionId is not null ? fresh
                : fresh with { SessionId = Guid.NewGuid().ToString("N") }, cancellation).ConfigureAwait(false);
            if (current is null) { return null; }
        }
        if (CanRefresh(current) && current.Expires - _clock.GetUtcNow() < TimeSpan.FromMinutes(5))
        {
            return await RefreshAsync(current, false, null, cancellation).ConfigureAwait(false);
        }
        return current;
    }

    internal ManagementClient CreateClient(LoginSession session)
    {
        var client = _createClient(session.Relay, session.Token);
        client.Authenticate(async (force, failedToken, cancellation) =>
        {
            var fresh = await GetSessionAsync(cancellation).ConfigureAwait(false);
            if (fresh is null || fresh.SessionId != session.SessionId || fresh.Relay != session.Relay)
            {
                throw new ManagementException("Sign-in changed. Run groundhog login.");
            }
            if (force && CanRefresh(fresh))
            {
                fresh = await RefreshAsync(fresh, true, failedToken, cancellation).ConfigureAwait(false);
            }
            return fresh.Token;
        });
        return client;
    }

    internal async Task<bool> SupportsRefreshAsync(string relay, CancellationToken cancellation)
    {
        using var client = _createClient(relay, null);
        try
        {
            var info = await client.SendAsync<ServiceCapabilities>(HttpMethod.Get, "/api/service-info", null, cancellation).ConfigureAwait(false);
            return info.Has("refreshToken");
        }
        catch (ManagementException ex) when (ex.Status == HttpStatusCode.NotFound) { return false; }
    }

    private static bool CanRefresh(LoginSession session) => session.RefreshSupported && !string.IsNullOrEmpty(session.RefreshToken);

    private Task<LoginSession> RefreshAsync(LoginSession expected, bool force, string? failedToken, CancellationToken cancellation) =>
        store.WithLockAsync(async fresh =>
        {
            if (fresh is null || fresh.SessionId != expected.SessionId || fresh.Relay != expected.Relay)
            {
                throw new ManagementException("Sign-in changed. Run groundhog login.");
            }
            // Another process may already have consumed the old rotating token. Use its successor.
            if (!CanRefresh(fresh) || (force ? fresh.Token != failedToken : fresh.Expires - _clock.GetUtcNow() >= TimeSpan.FromMinutes(5)))
            { return fresh; }
            using var client = _createClient(fresh.Relay, null);
            DeviceToken grant;
            try
            {
                grant = await client.SendAsync<DeviceToken>(HttpMethod.Post, "/api/cli/refresh", new { fresh.RefreshToken }, cancellation).ConfigureAwait(false);
            }
            catch (ManagementException ex) when (ex.Status == HttpStatusCode.Unauthorized)
            {
                store.Write(fresh with { RefreshToken = null, Expires = _clock.GetUtcNow() });
                throw;
            }
            if (string.IsNullOrEmpty(grant.AccessToken) || string.IsNullOrEmpty(grant.RefreshToken) || grant.ExpiresIn is < 1 or > 28800)
            {
                throw new ManagementException("The relay returned an invalid refresh response. Run groundhog login.");
            }
            var next = fresh with { Token = grant.AccessToken, RefreshToken = grant.RefreshToken,
                Expires = _clock.GetUtcNow().AddSeconds(grant.ExpiresIn) };
            store.Write(next);
            return next;
        }, cancellation);

    internal Task LogoutAsync(CancellationToken cancellation) => store.WithLockAsync(async session =>
    {
        try
        {
            if (session is not null)
            {
                using var client = _createClient(session.Relay, session.Token);
                await client.SendAsync<object>(HttpMethod.Post, "/api/cli/logout",
                    session.RefreshSupported && session.RefreshToken is not null ? new { session.RefreshToken } : null, cancellation).ConfigureAwait(false);
            }
        }
        finally { store.Write(null); }
        return true;
    }, cancellation);
}
