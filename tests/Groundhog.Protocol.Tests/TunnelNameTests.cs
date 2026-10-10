namespace Groundhog.Protocol.Tests;

public class TunnelNameTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("myapp")]
    [InlineData("my-app-2")]
    [InlineData("a1b")]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghij")] // 40 characters
    public void Accepts_valid_names(string name) =>
        Assert.Equal(TunnelNameValidation.Valid, TunnelName.Validate(name));

    [Theory]
    [InlineData(null, TunnelNameValidation.TooShort)]
    [InlineData("", TunnelNameValidation.TooShort)]
    [InlineData("ab", TunnelNameValidation.TooShort)]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijk", TunnelNameValidation.TooLong)] // 41 characters
    [InlineData("MyApp", TunnelNameValidation.InvalidCharacter)]
    [InlineData("my_app", TunnelNameValidation.InvalidCharacter)]
    [InlineData("my.app", TunnelNameValidation.InvalidCharacter)]
    [InlineData("café", TunnelNameValidation.InvalidCharacter)]
    [InlineData("1app", TunnelNameValidation.MustStartWithLetter)]
    [InlineData("-app", TunnelNameValidation.MustStartWithLetter)]
    [InlineData("app-", TunnelNameValidation.MustEndWithLetterOrDigit)]
    [InlineData("www", TunnelNameValidation.Reserved)]
    [InlineData("dashboard", TunnelNameValidation.Reserved)]
    public void Rejects_invalid_names(string? name, TunnelNameValidation expected) =>
        Assert.Equal(expected, TunnelName.Validate(name));

    [Fact]
    public void Default_reserved_names_match_the_specification() =>
        Assert.Equal(
            ["admin", "api", "dashboard", "mail", "relay", "status", "www"],
            TunnelName.DefaultReservedNames.Order(StringComparer.Ordinal));

    [Fact]
    public void A_configured_reserved_list_replaces_the_default()
    {
        string[] reserved = ["Staging"];

        Assert.Equal(TunnelNameValidation.Reserved, TunnelName.Validate("staging", reserved));
        Assert.Equal(TunnelNameValidation.Valid, TunnelName.Validate("www", reserved));
        Assert.True(TunnelName.IsValid("www", []));
    }
}
