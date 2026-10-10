using Groundhog.Protocol;

namespace Groundhog.Agent;

/// <summary>
/// A tunnel's connection key, presented with the <see cref="AgentAuthorization.KeyScheme"/> scheme
/// (<c>Authorization: GroundhogKey &lt;key&gt;</c>). The key is created in the relay's dashboard (or, in
/// single-tunnel mode, by whoever configures the relay) and is scoped to exactly one tunnel.
/// </summary>
public sealed class TunnelKeyCredential : GroundhogCredential
{
    private readonly GroundhogAccessToken _token;

    /// <summary>Creates a credential for <paramref name="key"/>.</summary>
    /// <param name="key">
    /// The connection key: 43 base64url characters. Surrounding whitespace, such as a trailing newline from a file or
    /// an environment variable, is ignored.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The key is missing or isn't shaped like a connection key. The message never contains the key.
    /// </exception>
    public TunnelKeyCredential(string key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("A connection key is required.", nameof(key));
        }

        if (!ConnectionKey.IsWellFormed(trimmed))
        {
            throw new ArgumentException(
                $"That isn't a connection key: a key is {ConnectionKey.Length} characters of letters, digits, '-' and '_'.",
                nameof(key));
        }

        _token = new GroundhogAccessToken(AgentAuthorization.KeyScheme, trimmed);
    }

    /// <inheritdoc />
    public override ValueTask<GroundhogAccessToken> GetTokenAsync(
        GroundhogTokenRequestContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(_token);
}
