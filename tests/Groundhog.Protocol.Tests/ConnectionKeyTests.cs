namespace Groundhog.Protocol.Tests;

public class ConnectionKeyTests
{
    // SHA-256 of the ASCII text "abc", from FIPS 180-2.
    private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void Hash_is_lowercase_hex_sha256_of_the_key_text() =>
        Assert.Equal(AbcHash, ConnectionKey.Hash("abc"));

    [Fact]
    public void Verify_accepts_the_matching_key_in_either_hex_case()
    {
        Assert.True(ConnectionKey.Verify("abc", AbcHash));
        Assert.True(ConnectionKey.Verify("abc", AbcHash.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("abd", AbcHash)] // wrong key
    [InlineData("abc", "")]
    [InlineData("abc", "ba7816bf")] // truncated
    [InlineData("abc", "za7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")] // not hex
    [InlineData(null, AbcHash)]
    [InlineData("abc", null)]
    public void Verify_rejects_anything_else(string? key, string? hash) =>
        Assert.False(ConnectionKey.Verify(key, hash));

    [Fact]
    public void Generated_keys_are_43_base64url_characters_of_32_bytes_and_unique()
    {
        var first = ConnectionKey.Generate();
        var second = ConnectionKey.Generate();

        Assert.Equal(43, first.Length);
        Assert.True(ConnectionKey.IsWellFormed(first));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
        Assert.NotEqual(first, second);
        Assert.True(ConnectionKey.Verify(first, ConnectionKey.Hash(first)));
        Assert.False(ConnectionKey.Verify(second, ConnectionKey.Hash(first)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 42 characters
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // padded
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")] // standard base64, not base64url
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")] // non-zero trailing bits
    public void IsWellFormed_rejects_other_shapes(string? key) =>
        Assert.False(ConnectionKey.IsWellFormed(key));
}
