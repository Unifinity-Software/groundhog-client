using Groundhog.Protocol;

namespace Groundhog.Agent;

/// <summary>
/// Supplies the token the agent presents to the relay as <c>Authorization: &lt;scheme&gt; &lt;token&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The agent asks for a token before every connection attempt, including every reconnection, so a credential can
/// hand out short-lived tokens and refresh them as it sees fit. When the relay refuses a token with HTTP 401 and
/// that token had an expiry, the agent asks once more with
/// <see cref="GroundhogTokenRequestContext.PreviousTokenRejected"/> set before it gives up.
/// </para>
/// <para>
/// Implement this class to add a sign-in method. The relay needs a matching handler for the scheme; the protocol
/// itself doesn't change. <see cref="TunnelKeyCredential"/> is the built-in implementation.
/// </para>
/// <para>Implementations must be safe to call from several connections at once.</para>
/// </remarks>
public abstract class GroundhogCredential
{
    /// <summary>Gets a token for one connection attempt.</summary>
    /// <param name="context">The relay and tunnel the token is for.</param>
    /// <param name="cancellationToken">Cancelled when the agent stops.</param>
    public abstract ValueTask<GroundhogAccessToken> GetTokenAsync(
        GroundhogTokenRequestContext context,
        CancellationToken cancellationToken);
}

/// <summary>A token from a <see cref="GroundhogCredential"/>: a scheme, the token itself and an optional expiry.</summary>
public sealed class GroundhogAccessToken
{
    /// <summary>Creates a token.</summary>
    /// <param name="scheme">The <c>Authorization</c> scheme, for example <see cref="AgentAuthorization.KeyScheme"/>.</param>
    /// <param name="token">The token. It's sent to the relay and never logged.</param>
    /// <param name="expiresOn">When the token stops being valid, if it expires.</param>
    /// <exception cref="ArgumentException">The scheme or token is empty or contains whitespace.</exception>
    public GroundhogAccessToken(string scheme, string token, DateTimeOffset? expiresOn = null)
    {
        // Format validates both parts the same way the wire format requires.
        _ = AgentAuthorization.Format(scheme, token);
        Scheme = scheme;
        Token = token;
        ExpiresOn = expiresOn;
    }

    /// <summary>The <c>Authorization</c> scheme.</summary>
    public string Scheme { get; }

    /// <summary>The token. Treat it as a secret.</summary>
    public string Token { get; }

    /// <summary>When the token stops being valid, or <see langword="null"/> if it doesn't expire.</summary>
    public DateTimeOffset? ExpiresOn { get; }

    /// <summary>Returns the scheme only, so the token never ends up in a log by accident.</summary>
    public override string ToString() => $"{Scheme} (token hidden)";
}

/// <summary>What a <see cref="GroundhogCredential"/> is asked for a token for.</summary>
public sealed class GroundhogTokenRequestContext
{
    /// <summary>Creates a request context.</summary>
    /// <param name="relayAddress">The relay the agent is connecting to.</param>
    /// <param name="tunnelName">The tunnel the agent wants to host.</param>
    /// <param name="previousTokenRejected">
    /// Whether the relay just refused the previous token with HTTP 401, so a cached token shouldn't be reused.
    /// </param>
    public GroundhogTokenRequestContext(Uri relayAddress, string tunnelName, bool previousTokenRejected)
    {
        ArgumentNullException.ThrowIfNull(relayAddress);
        ArgumentException.ThrowIfNullOrEmpty(tunnelName);
        RelayAddress = relayAddress;
        TunnelName = tunnelName;
        PreviousTokenRejected = previousTokenRejected;
    }

    /// <summary>The relay the agent is connecting to, as configured.</summary>
    public Uri RelayAddress { get; }

    /// <summary>The tunnel the agent wants to host.</summary>
    public string TunnelName { get; }

    /// <summary>
    /// Whether the relay just refused the previous token with HTTP 401. A credential that caches tokens should get
    /// a fresh one.
    /// </summary>
    public bool PreviousTokenRejected { get; }
}
