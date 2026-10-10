using System.Diagnostics.CodeAnalysis;

namespace Groundhog.Protocol;

/// <summary>
/// The agent's credential on the wire: <c>Authorization: &lt;scheme&gt; &lt;token&gt;</c>.
/// New authentication methods add a scheme; they never change this format.
/// </summary>
public static class AgentAuthorization
{
    /// <summary>The request header that carries the credential.</summary>
    public const string HeaderName = "Authorization";

    /// <summary>
    /// The v1 scheme: the token is the tunnel's connection key (see <see cref="ConnectionKey"/>).
    /// Every v1 relay supports it.
    /// </summary>
    public const string KeyScheme = "GroundhogKey";

    /// <summary>Formats a header value as <c>&lt;scheme&gt; &lt;token&gt;</c>.</summary>
    /// <exception cref="ArgumentException">The scheme or token is empty or contains whitespace.</exception>
    public static string Format(string scheme, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (ContainsWhitespace(scheme))
        {
            throw new ArgumentException("The scheme can't contain whitespace.", nameof(scheme));
        }

        if (ContainsWhitespace(token))
        {
            throw new ArgumentException("The token can't contain whitespace.", nameof(token));
        }

        return $"{scheme} {token}";
    }

    /// <summary>
    /// Splits a header value into its scheme and token. Returns <see langword="false"/> unless the value is
    /// exactly one scheme, one or more spaces, and one token with no further whitespace.
    /// </summary>
    /// <remarks>Scheme names are case-insensitive; compare them with <see cref="StringComparison.OrdinalIgnoreCase"/>.</remarks>
    public static bool TryParse(
        string? headerValue,
        [NotNullWhen(true)] out string? scheme,
        [NotNullWhen(true)] out string? token)
    {
        scheme = null;
        token = null;
        if (string.IsNullOrEmpty(headerValue))
        {
            return false;
        }

        var separator = headerValue.IndexOf(' ');
        if (separator <= 0)
        {
            return false;
        }

        var schemePart = headerValue[..separator];
        var tokenPart = headerValue[separator..].TrimStart(' ');
        if (tokenPart.Length == 0 || ContainsWhitespace(schemePart) || ContainsWhitespace(tokenPart))
        {
            return false;
        }

        scheme = schemePart;
        token = tokenPart;
        return true;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
