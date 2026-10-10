using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Groundhog.Protocol;

/// <summary>
/// Per-tunnel connection keys (the <see cref="AgentAuthorization.KeyScheme"/> token): 32 random bytes written as
/// 43 base64url characters. The relay stores only the key's hash: SHA-256 of the key's text, written as 64
/// lowercase hexadecimal characters (the same as <c>printf %s "$KEY" | sha256sum</c>).
/// </summary>
public static class ConnectionKey
{
    /// <summary>Random bytes in a key.</summary>
    public const int ByteLength = 32;

    /// <summary>Characters in a key: 32 bytes in unpadded base64url.</summary>
    public const int Length = 43;

    /// <summary>Characters in a key hash: 32 bytes of SHA-256 in hexadecimal.</summary>
    public const int HashLength = 64;

    /// <summary>Creates a new key from a cryptographically secure random source.</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        RandomNumberGenerator.Fill(bytes);
        var key = Base64Url.EncodeToString(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return key;
    }

    /// <summary>
    /// Whether <paramref name="key"/> has the shape of a key: 43 base64url characters that decode to 32 bytes.
    /// </summary>
    public static bool IsWellFormed(string? key)
    {
        if (key is null || key.Length != Length)
        {
            return false;
        }

        if (!Base64Url.IsValid(key, out var decodedLength) || decodedLength != ByteLength)
        {
            return false;
        }

        // Re-encoding rejects keys whose last character carries non-zero padding bits.
        Span<byte> bytes = stackalloc byte[ByteLength];
        Base64Url.DecodeFromChars(key, bytes);
        return string.Equals(Base64Url.EncodeToString(bytes), key, StringComparison.Ordinal);
    }

    /// <summary>The value the relay stores for <paramref name="key"/>: lowercase hex SHA-256 of its UTF-8 text.</summary>
    public static string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Whether <paramref name="key"/> hashes to <paramref name="expectedHash"/>, compared in constant time.
    /// The expected hash may use upper- or lowercase hex; anything that isn't a 64-character hex string never
    /// matches.
    /// </summary>
    public static bool Verify(string? key, string? expectedHash)
    {
        if (key is null || expectedHash is null || expectedHash.Length != HashLength)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        if (Convert.FromHexString(expectedHash, expected, out _, out var written) != OperationStatus.Done
            || written != expected.Length)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), actual);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
