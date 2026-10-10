namespace Groundhog.Protocol.Tests;

public class AgentAuthorizationTests
{
    [Fact]
    public void Formats_and_parses_scheme_and_token()
    {
        var header = AgentAuthorization.Format(AgentAuthorization.KeyScheme, "k3y_-");

        Assert.Equal("GroundhogKey k3y_-", header);
        Assert.True(AgentAuthorization.TryParse(header, out var scheme, out var token));
        Assert.Equal(AgentAuthorization.KeyScheme, scheme);
        Assert.Equal("k3y_-", token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GroundhogKey")]
    [InlineData("GroundhogKey ")]
    [InlineData(" GroundhogKey abc")]
    [InlineData("GroundhogKey abc def")]
    [InlineData("GroundhogKey abc\tdef")]
    [InlineData("GroundhogKey abc\r\n")]
    public void Rejects_malformed_header_values(string? header) =>
        Assert.False(AgentAuthorization.TryParse(header, out _, out _));
}
