using System.Collections.Frozen;

namespace Groundhog.Protocol;

/// <summary>
/// Rules for tunnel names, the first label of a tunnel's address (<c>&lt;name&gt;.&lt;tunnel domain&gt;</c>):
/// 3–40 lowercase letters, digits and hyphens, starting with a letter and ending with a letter or digit, and
/// not a reserved name.
/// </summary>
public static class TunnelName
{
    /// <summary>Shortest allowed name.</summary>
    public const int MinLength = 3;

    /// <summary>Longest allowed name.</summary>
    public const int MaxLength = 40;

    /// <summary>
    /// Names that can't be tunnels unless the relay is configured with a different list.
    /// </summary>
    public static IReadOnlySet<string> DefaultReservedNames { get; } = new[]
    {
        "www", "api", "admin", "dashboard", "relay", "status", "mail",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Checks <paramref name="name"/> against the naming rules and <paramref name="reservedNames"/>.
    /// </summary>
    /// <param name="name">The candidate name. It isn't lowercased: uppercase letters are invalid.</param>
    /// <param name="reservedNames">
    /// Names to refuse, compared case-insensitively. <see langword="null"/> means
    /// <see cref="DefaultReservedNames"/>; pass an empty set to reserve nothing.
    /// </param>
    public static TunnelNameValidation Validate(string? name, IEnumerable<string>? reservedNames = null)
    {
        if (string.IsNullOrEmpty(name) || name.Length < MinLength)
        {
            return TunnelNameValidation.TooShort;
        }

        if (name.Length > MaxLength)
        {
            return TunnelNameValidation.TooLong;
        }

        foreach (var c in name)
        {
            if (!IsLowercaseLetter(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return TunnelNameValidation.InvalidCharacter;
            }
        }

        if (!IsLowercaseLetter(name[0]))
        {
            return TunnelNameValidation.MustStartWithLetter;
        }

        if (name[^1] == '-')
        {
            return TunnelNameValidation.MustEndWithLetterOrDigit;
        }

        foreach (var reserved in reservedNames ?? DefaultReservedNames)
        {
            if (string.Equals(reserved, name, StringComparison.OrdinalIgnoreCase))
            {
                return TunnelNameValidation.Reserved;
            }
        }

        return TunnelNameValidation.Valid;
    }

    /// <summary>Whether <paramref name="name"/> is a valid, unreserved tunnel name.</summary>
    public static bool IsValid(string? name, IEnumerable<string>? reservedNames = null) =>
        Validate(name, reservedNames) == TunnelNameValidation.Valid;

    private static bool IsLowercaseLetter(char c) => c is >= 'a' and <= 'z';
}

/// <summary>The result of <see cref="TunnelName.Validate"/>.</summary>
public enum TunnelNameValidation
{
    /// <summary>The name can be used.</summary>
    Valid,

    /// <summary>Missing or shorter than <see cref="TunnelName.MinLength"/>.</summary>
    TooShort,

    /// <summary>Longer than <see cref="TunnelName.MaxLength"/>.</summary>
    TooLong,

    /// <summary>Contains something other than lowercase ASCII letters, digits and hyphens.</summary>
    InvalidCharacter,

    /// <summary>Doesn't start with a letter.</summary>
    MustStartWithLetter,

    /// <summary>Ends with a hyphen, which isn't a valid DNS name.</summary>
    MustEndWithLetterOrDigit,

    /// <summary>One of the relay's reserved names.</summary>
    Reserved,
}
