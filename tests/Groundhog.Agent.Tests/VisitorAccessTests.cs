using System.Buffers.Text;
using System.Text;
using Groundhog.Agent.Internal;
using Groundhog.Protocol;

namespace Groundhog.Agent.Tests;

public class VisitorAccessTests(TunnelFixture fixture) : IClassFixture<TunnelFixture>
{
    private static readonly string s_apiKey = ApiKey.Generate();

    private static GroundhogVisitorAccess Rules() => new()
    {
        Allow = [VisitorAccessMethods.Members],
        Rules =
        [
            new("/webhooks", [VisitorAccessMethods.Anyone]),
            new("/api", [VisitorAccessMethods.ApiKeys, VisitorAccessMethods.Ips]),
        ],
        IpRanges = ["203.0.113.0/24"],
        ApiKeys = [s_apiKey],
    };

    [Fact(Timeout = 30_000)]
    public async Task Declares_the_rules_on_every_pooled_connection_with_api_key_hashes_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var tunnel = fixture.Relay.Tunnel("access-declared");
        await using var agent = fixture.StartAgent("access-declared", visitorAccess: Rules(), poolSize: 2);

        var first = await tunnel.Attempts.Reader.ReadAsync(ct);
        var second = await tunnel.Attempts.Reader.ReadAsync(ct);
        Assert.NotEmpty(first.VisitorAccess);
        Assert.Equal(first.VisitorAccess, second.VisitorAccess);

        var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(first.VisitorAccess));
        Assert.DoesNotContain(s_apiKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(s_apiKey, first.VisitorAccess, StringComparison.Ordinal);
        Assert.True(VisitorAccessDeclaration.TryDecode(first.VisitorAccess, out var declaration, out var error), error);
        Assert.Equal([ApiKey.Hash(s_apiKey)], declaration.ApiKeyHashes);
        Assert.Equal(["members"], declaration.Default.Allow);
        Assert.Equal("/webhooks", declaration.Rules[0].Path);

        var connected = await agent.NextStatusAsync();
        Assert.Equal(GroundhogAgentState.Connected, connected.State);
    }

    [Fact(Timeout = 30_000)]
    public async Task Sends_no_declaration_when_none_is_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var tunnel = fixture.Relay.Tunnel("access-absent");
        await using var agent = fixture.StartAgent("access-absent");

        var attempt = await tunnel.Attempts.Reader.ReadAsync(ct);
        Assert.Equal(string.Empty, attempt.VisitorAccess);
        Assert.Equal(GroundhogAgentState.Connected, (await agent.NextStatusAsync()).State);
    }

    [Fact(Timeout = 30_000)]
    public async Task Stops_for_good_when_the_relay_does_not_confirm_the_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var tunnel = fixture.Relay.Tunnel("access-old-relay");
        tunnel.AppliesVisitorAccess = false;
        await using var agent = fixture.StartAgent("access-old-relay", visitorAccess: Rules());

        var stopped = await agent.NextStatusAsync();

        Assert.Equal(GroundhogAgentState.Stopped, stopped.State);
        Assert.Equal(GroundhogAgentStopReason.VisitorAccessUnsupported, stopped.StopReason);
        Assert.Equal("This relay doesn't support visitor access rules; update the relay or remove them.", stopped.Reason);
        Assert.Equal(GroundhogAgentStopReason.VisitorAccessUnsupported, await agent.Run.WaitAsync(ct));
        Assert.Equal(1, tunnel.AttemptCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Shows_the_relays_message_when_it_refuses_the_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var tunnel = fixture.Relay.Tunnel("access-refused");
        tunnel.RefuseWithMessage(ConnectRefusal.Forbidden, "Visitor access for this tunnel is managed in the dashboard.");
        await using var agent = fixture.StartAgent("access-refused", visitorAccess: Rules());

        var stopped = await agent.NextStatusAsync();

        Assert.Equal(GroundhogAgentStopReason.NotAllowed, stopped.StopReason);
        Assert.Equal(
            "The relay refused to host tunnel 'access-refused' (HTTP 403): Visitor access for this tunnel is managed in the dashboard.",
            stopped.Reason);
        Assert.Equal(GroundhogAgentStopReason.NotAllowed, await agent.Run.WaitAsync(ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task Keeps_the_generic_message_for_a_403_without_a_body()
    {
        var tunnel = fixture.Relay.Tunnel("access-bare-403");
        tunnel.Refuse(ConnectRefusal.Forbidden);
        await using var agent = fixture.StartAgent("access-bare-403");

        var stopped = await agent.NextStatusAsync();

        Assert.Equal("The credential isn't allowed to host tunnel 'access-bare-403' (HTTP 403).", stopped.Reason);
        Assert.Equal(GroundhogAgentStopReason.NotAllowed, await agent.Run.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Managed in the dashboard\nsecond line", "Managed in the dashboard.")]
    [InlineData("Bad \u001b[31mrule\u0007.", "Bad [31mrule.")]
    [InlineData("  \n", null)]
    public void Refusal_messages_are_one_clean_line(string body, string? expected) =>
        Assert.Equal(expected, RelayConnector.ToRefusalMessage(body));

    [Fact]
    public void Refusal_messages_are_shortened()
    {
        var message = RelayConnector.ToRefusalMessage(new string('x', 500));

        Assert.Equal(RelayConnector.MaxRefusalMessageLength + 1, message!.Length);
        Assert.EndsWith("…", message);
    }

    [Fact]
    public void Invalid_rules_are_refused_before_connecting()
    {
        var options = new GroundhogAgentOptions
        {
            TunnelName = "myapp",
            Credential = new TunnelKeyCredential(ConnectionKey.Generate()),
            LocalTarget = new Uri("http://localhost:5173"),
            VisitorAccess = new GroundhogVisitorAccess { Allow = [VisitorAccessMethods.ApiKeys] },
        };

        var problem = Assert.Throws<ArgumentException>(() => new GroundhogAgent(options));
        Assert.Contains("apikeys needs at least one API key", problem.Message);
    }

    [Theory]
    [InlineData("too-short-secret")]
    [InlineData("password with spaces and a fair bit of length to it")]
    public void Malformed_api_keys_are_refused_without_repeating_them(string apiKey)
    {
        var access = new GroundhogVisitorAccess { Allow = [VisitorAccessMethods.ApiKeys], ApiKeys = [apiKey] };

        var problem = Assert.Throws<ArgumentException>(access.ToDeclaration);
        Assert.DoesNotContain(apiKey, problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, access.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_to_anyone_with_no_rules()
    {
        var declaration = new GroundhogVisitorAccess().ToDeclaration();

        Assert.Equal(["anyone"], declaration.Default.Allow);
        Assert.Empty(declaration.Rules);
    }
}
