using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace Groundhog.Protocol.Tests;

public class VisitorAccessTests
{
    private static readonly string s_hash = VisitorToken.Hash(VisitorToken.Generate());

    // The example policy from the relay's specification, as an agent declares it.
    private static VisitorAccessDeclaration SpecExample() => new()
    {
        Default = new([VisitorAccessMethods.Members]),
        Rules =
        [
            new("/webhooks", [VisitorAccessMethods.Anyone]),
            new("/api", [VisitorAccessMethods.Tokens, VisitorAccessMethods.Ips]),
        ],
        IpRanges = ["203.0.113.0/24", "2001:db8::/32"],
        TokenHashes = [s_hash],
    };

    [Fact]
    public void The_specification_example_is_valid()
    {
        Assert.True(SpecExample().TryValidate(out var error), error);
    }

    [Fact]
    public void An_empty_declaration_means_anyone()
    {
        var declaration = new VisitorAccessDeclaration();

        Assert.True(declaration.TryValidate(out _));
        Assert.Equal([VisitorAccessMethods.Anyone], declaration.Default.Allow);
    }

    [Fact]
    public void Encodes_as_base64url_camel_case_json_and_round_trips()
    {
        var encoded = SpecExample().Encode();

        Assert.Matches("^[A-Za-z0-9_-]+$", encoded);
        var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("members", root.GetProperty("default").GetProperty("allow")[0].GetString());
        Assert.Equal("/webhooks", root.GetProperty("rules")[0].GetProperty("path").GetString());
        Assert.Equal("2001:db8::/32", root.GetProperty("ipRanges")[1].GetString());
        Assert.Equal(s_hash, root.GetProperty("tokenHashes")[0].GetString());

        Assert.True(VisitorAccessDeclaration.TryDecode(encoded, out var decoded, out var error), error);
        Assert.Equal(["tokens", "ips"], decoded.Rules[1].Allow);
        Assert.Equal(SpecExample().IpRanges, decoded.IpRanges);
        Assert.Equal([s_hash], decoded.TokenHashes);
    }

    [Fact]
    public void Decodes_a_declaration_with_only_a_default_rule()
    {
        var encoded = Base64Url.EncodeToString("""{"default":{"allow":["members"]}}"""u8);

        Assert.True(VisitorAccessDeclaration.TryDecode(encoded, out var decoded, out var error), error);
        Assert.Equal(["members"], decoded.Default.Allow);
        Assert.Empty(decoded.Rules);
        Assert.Empty(decoded.IpRanges);
        Assert.Empty(decoded.TokenHashes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("bm90IGpzb24")] // "not json"
    [InlineData("WzFd")] // [1]
    public void Refuses_malformed_header_values(string value) =>
        Assert.False(VisitorAccessDeclaration.TryDecode(value, out _, out _));

    [Fact]
    public void Refuses_an_over_long_header_value() =>
        Assert.False(VisitorAccessDeclaration.TryDecode(new string('A', VisitorAccessDeclaration.MaxEncodedLength + 1), out _, out _));

    [Fact]
    public void Decoding_validates()
    {
        var encoded = Base64Url.EncodeToString("""{"default":{"allow":["everyone"]}}"""u8);

        Assert.False(VisitorAccessDeclaration.TryDecode(encoded, out _, out var error));
        Assert.Contains("allow values must be", error);
    }

    public static TheoryData<string, VisitorAccessDeclaration> Invalid => new()
    {
        { "allow needs", new() { Default = new([]) } },
        { "allow values must be", new() { Default = new(["everyone"]) } },
        { "allow values must be", new() { Default = new(["Members"]) } },
        { "listed twice", new() { Default = new(["members", "members"]) } },
        { "anyone can't be combined", new() { Default = new(["anyone", "members"]) } },
        { "tokens needs", new() { Default = new(["tokens"]) } },
        { "ips needs", new() { Default = new(["ips"]) } },
        { "ips needs", new() { Rules = [new("/api", ["ips"])] } },
        { "start with /", new() { Rules = [new("api", ["members"])] } },
        { "start with /", new() { Rules = [new("", ["members"])] } },
        { "query", new() { Rules = [new("/api?x=1", ["members"])] } },
        { "fragment", new() { Rules = [new("/api#top", ["members"])] } },
        { "control characters", new() { Rules = [new("/a\nb", ["members"])] } },
        { "at most 256", new() { Rules = [new("/" + new string('a', 256), ["members"])] } },
        { "more than one rule", new() { Rules = [new("/api", ["members"]), new("/API/", ["anyone"])] } },
        { "more than one rule", new() { Rules = [new("/", ["members"]), new("//", ["anyone"])] } },
        { "at most 32 path rules", new() { Rules = [.. Enumerable.Range(0, 33).Select(i => new VisitorAccessPathRule($"/p{i}", ["members"]))] } },
        { "at most 64 IP ranges", new() { IpRanges = [.. Enumerable.Range(0, 65).Select(i => $"10.0.{i}.0/24")] } },
        { "at most 10 visitor tokens", new() { TokenHashes = [.. Enumerable.Range(0, 11).Select(i => VisitorToken.Hash($"t{i}"))] } },
        { "IP range 1 isn't in CIDR form", new() { IpRanges = ["203.0.113.0"] } },
        { "CIDR", new() { IpRanges = ["203.0.113.0/33"] } },
        { "CIDR", new() { IpRanges = ["office"] } },
        { "64 lowercase", new() { TokenHashes = [s_hash.ToUpperInvariant()] } },
        { "64 lowercase", new() { TokenHashes = ["abc"] } },
        { "64 lowercase", new() { TokenHashes = [VisitorToken.Generate()] } }, // a plaintext token
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Refuses_what_the_relay_refuses(string expected, VisitorAccessDeclaration declaration)
    {
        Assert.False(declaration.TryValidate(out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Accepts_the_limits_exactly()
    {
        var declaration = new VisitorAccessDeclaration
        {
            Default = new(["members", "tokens", "ips"]),
            Rules = [.. Enumerable.Range(0, 32).Select(i => new VisitorAccessPathRule($"/p{i}/" + new string('x', 200), ["anyone"]))],
            IpRanges = [.. Enumerable.Range(0, 64).Select(i => $"10.0.{i}.0/24")],
            TokenHashes = [.. Enumerable.Range(0, 10).Select(i => VisitorToken.Hash($"t{i}"))],
        };

        Assert.True(declaration.TryValidate(out var error), error);
        Assert.True(declaration.Encode().Length <= VisitorAccessDeclaration.MaxEncodedLength);
    }

    [Fact]
    public void Paths_differing_beyond_a_segment_are_distinct_rules()
    {
        Assert.True(new VisitorAccessDeclaration
        {
            Rules = [new("/api", ["members"]), new("/apix", ["anyone"]), new("/", ["members"])],
        }.TryValidate(out var error), error);
    }

    [Fact]
    public void Management_clients_can_skip_token_checks()
    {
        Assert.True(VisitorAccessValidator.TryValidate(new(["tokens"]), null, null, tokenCount: null, out var error), error);
        Assert.False(VisitorAccessValidator.TryValidate(new(["tokens"]), null, null, tokenCount: 0, out _));
    }

    [Fact]
    public void Visitor_tokens_have_the_connection_key_shape()
    {
        var token = VisitorToken.Generate();

        Assert.True(VisitorToken.IsWellFormed(token));
        Assert.Equal(VisitorToken.Length, token.Length);
        Assert.Matches("^[0-9a-f]{64}$", VisitorToken.Hash(token));
        Assert.False(VisitorToken.IsWellFormed("short"));
    }

    [Fact]
    public void Describes_itself_without_token_hashes() =>
        Assert.DoesNotContain(s_hash, SpecExample().ToString());
}
