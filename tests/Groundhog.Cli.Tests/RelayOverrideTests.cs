using Groundhog.Agent.Tests;

namespace Groundhog.Cli.Tests;

/// <summary>
/// <c>--relay</c> is hidden from help (see <see cref="GroundhogCommandTests"/>) but remains a working development and
/// testing override, here pointed at the fake relay.
/// </summary>
public sealed class RelayOverrideTests(TunnelFixture fixture) : IClassFixture<TunnelFixture>
{
    [Fact(Timeout = 30_000)]
    public async Task Doctor_checks_the_relay_given_with_the_hidden_option()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await GroundhogCommand.RunAsync(
            ["doctor", "--relay", fixture.Relay.Address.ToString(), "--target", fixture.LocalApp.ToString(), "--output", "json"],
            _ => null, output, error, TestContext.Current.CancellationToken);

        Assert.True(exitCode == 0, output + error.ToString());
        Assert.Contains("Relay supports protocol 1.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("target is reachable", output.ToString(), StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task Profile_save_keeps_the_hidden_relay_override()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-relay-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var relay = fixture.Relay.Address.ToString();
            var saved = await CliProcess.RunAsync(
                ["profile", "save", "relay-profile", "--target", "http://localhost:5173", "--relay", relay], directory, null, cancellation);
            Assert.True(saved.ExitCode == 0, saved.Output);
            var hosted = await CliProcess.RunAsync(
                ["profile", "save", "hosted-profile", "--target", "http://localhost:5173"], directory, null, cancellation);
            Assert.True(hosted.ExitCode == 0, hosted.Output);

            var profiles = new ProfileStore(directory).Read();
            Assert.Equal(relay, Assert.Single(profiles, p => p.Name == "relay-profile").Relay);
            Assert.Null(Assert.Single(profiles, p => p.Name == "hosted-profile").Relay);
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    }
}
