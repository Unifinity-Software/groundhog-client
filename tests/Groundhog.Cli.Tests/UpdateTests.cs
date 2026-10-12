using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Cli.Tests;

public sealed class UpdateTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("1.0.0", false, "1.1.0")]
    [InlineData("1.0.0-preview.1", true, "1.2.0-preview.10")]
    [InlineData("1.0.0", true, "1.2.0-preview.10")]
    public async Task Selects_the_latest_listed_release_on_the_requested_channel(string current, bool previews, string expected)
    {
        using var fixture = new Fixture(current);
        fixture.Versions = ["1.0.0", "1.1.0", "1.2.0-preview.2", "1.2.0-preview.10", "1.3.0"];
        fixture.Unlisted.Add("1.3.0");
        var result = await fixture.Check.CheckAsync(true, previews, Cancellation);
        Assert.Equal(expected, result.Available);
        Assert.All(fixture.Requests, url => Assert.Equal("api.nuget.org", url.Host));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0", "0.9.0")]
    [InlineData("1.0.0", "1.1.0-preview.1")]
    public async Task Does_not_offer_reinstalls_downgrades_or_preview_updates_to_stable(string current, string candidate)
    {
        using var fixture = new Fixture(current);
        fixture.Versions = [candidate];
        Assert.Null((await fixture.Check.CheckAsync(true, false, Cancellation)).Available);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task Startup_offer_is_shown_once_per_day_and_declining_does_not_update()
    {
        using var fixture = new Fixture();
        using var commands = fixture.Commands(interactive: true, accept: false);
        Assert.False(await commands.OfferAsync(Cancellation));
        Assert.Equal(1, fixture.Prompts);
        Assert.Empty(fixture.Installed);
        var requests = fixture.Requests.Count;
        Assert.False(await commands.OfferAsync(Cancellation));
        Assert.Equal(requests, fixture.Requests.Count);
        Assert.Equal(1, fixture.Prompts);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.False(await commands.OfferAsync(Cancellation));
        Assert.Equal(2, fixture.Prompts);
        Assert.Empty(fixture.Installed);
    }

    [Fact]
    public async Task Explicit_check_bypasses_cache_but_never_prompts_or_installs()
    {
        using var fixture = new Fixture();
        using var commands = fixture.Commands(interactive: false);
        await fixture.Check.CheckAsync(false, false, Cancellation);
        fixture.Versions = ["1.3.0"];
        Assert.Equal(0, await commands.RunAsync(["update", "--check"], Cancellation));
        Assert.Contains("1.3.0 is available", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Installed);
        Assert.Equal(0, fixture.Prompts);
    }

    [Fact]
    public async Task Consent_installs_the_selected_version_into_the_running_installation()
    {
        using var fixture = new Fixture();
        using var commands = fixture.Commands(interactive: true, accept: true);
        Assert.True(await commands.OfferAsync(Cancellation));
        var installed = Assert.Single(fixture.Installed);
        Assert.Equal("1.1.0", installed.Version);
        Assert.Equal(fixture.Installation, installed.Installation);
        Assert.Contains("Run your command again", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripts_must_opt_in_with_yes_and_do_not_receive_startup_checks()
    {
        using var fixture = new Fixture();
        using var commands = fixture.Commands(interactive: false);
        Assert.False(await commands.OfferAsync(Cancellation));
        Assert.Equal(2, await commands.RunAsync(["update"], Cancellation));
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Installed);
        Assert.Equal(0, await commands.RunAsync(["update", "--yes"], Cancellation));
        Assert.Single(fixture.Installed);
        Assert.Equal(0, fixture.Prompts);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--help")]
    [InlineData("--no-interactive")]
    [InlineData("--no-update-check")]
    [InlineData("__host-background")]
    [InlineData("update")]
    [InlineData("--output=json")]
    [InlineData("json")]
    public void Startup_check_skips_help_scripts_background_processes_and_explicit_checks(string option)
    {
        Assert.False(UpdateCommands.ShouldCheck(true, [option], _ => null));
        Assert.False(UpdateCommands.ShouldCheck(false, ["host"], _ => null));
        Assert.False(UpdateCommands.ShouldCheck(true, ["host"], _ => "1"));
        Assert.True(UpdateCommands.ShouldCheck(true, ["host", "myapp", "--port", "5173"], _ => null));
    }

    [Fact]
    public async Task Failed_install_is_reported_and_does_not_claim_success()
    {
        using var fixture = new Fixture { InstallerExitCode = 1 };
        using var commands = fixture.Commands(interactive: true, accept: true);
        Assert.False(await commands.OfferAsync(Cancellation));
        Assert.Single(fixture.Installed);
        Assert.DoesNotContain("Updated to", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("update did not complete", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unidentified_installation_does_not_auto_check_or_update_a_different_installation()
    {
        using var fixture = new Fixture { Installation = null };
        using var commands = fixture.Commands(interactive: true, accept: true);
        Assert.False(await commands.OfferAsync(Cancellation));
        Assert.Empty(fixture.Requests);
        Assert.Equal(1, await commands.RunAsync(["update", "--yes"], Cancellation));
        Assert.Empty(fixture.Installed);
        Assert.Equal(0, fixture.Prompts);
    }

    [Fact]
    public async Task Offline_startup_is_quiet_and_throttled_but_an_explicit_check_reports_failure()
    {
        using var fixture = new Fixture { Offline = true };
        using var commands = fixture.Commands(interactive: true);
        Assert.False(await commands.OfferAsync(Cancellation));
        Assert.Empty(fixture.Error.ToString());
        var requests = fixture.Requests.Count;
        Assert.False(await commands.OfferAsync(Cancellation));
        Assert.Equal(requests, fixture.Requests.Count);
        Assert.Equal(1, await commands.RunAsync(["update", "--check"], Cancellation));
        Assert.Contains("could not check NuGet", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_metadata_and_corrupt_cache_cannot_block_startup()
    {
        using var fixture = new Fixture { InvalidMetadata = true };
        File.WriteAllText(Path.Combine(fixture.Directory, "update-check.json"), "not json");
        using var commands = fixture.Commands(interactive: true);
        Assert.False(await commands.OfferAsync(Cancellation));
        Assert.Empty(fixture.Installed);
        Assert.Empty(fixture.Error.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Status\":null,\"Previews\":false}")]
    public async Task Incomplete_cache_entries_are_ignored(string json)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Directory, "update-check.json"), json);
        var status = await fixture.Check.CheckAsync(false, false, Cancellation);
        Assert.Equal("1.1.0", status.Available);
        Assert.False(status.FromCache);
    }

    [Fact]
    public async Task Entire_check_has_a_two_second_budget_using_fake_time()
    {
        using var fixture = new Fixture { Block = true };
        var task = fixture.Check.CheckAsync(true, false, Cancellation);
        await fixture.Started.Task.WaitAsync(Cancellation);
        fixture.Time.Advance(UpdateCheck.Timeout);
        await Assert.ThrowsAsync<UpdateUnavailableException>(() => task);
    }

    [Fact]
    public async Task A_changed_version_or_channel_invalidates_the_cache()
    {
        using var fixture = new Fixture();
        await fixture.Check.CheckAsync(false, false, Cancellation);
        var calls = fixture.Requests.Count;
        fixture.Versions = ["1.2.0-preview.1"];
        var result = await fixture.Check.CheckAsync(false, true, Cancellation);
        Assert.Equal("1.2.0-preview.1", result.Available);
        Assert.True(fixture.Requests.Count > calls);
        var upgraded = new UpdateCheck(fixture.Http, fixture.Time, fixture.Directory, "1.2.0");
        Assert.Null((await upgraded.CheckAsync(false, false, Cancellation)).Available);
    }

    [Fact]
    public async Task Invalid_options_never_contact_the_feed_or_mutate_the_installation()
    {
        using var fixture = new Fixture();
        using var commands = fixture.Commands(interactive: true);
        Assert.Equal(2, await commands.RunAsync(["update", "--check", "--yes"], Cancellation));
        Assert.Equal(2, await commands.RunAsync(["update", "--version", "1.0.0"], Cancellation));
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Installed);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal Fixture(string current = "1.0.0")
        {
            Current = current;
            System.IO.Directory.CreateDirectory(Directory);
            Http = new HttpClient(this);
            Check = new(Http, Time, Directory, current);
            Installation = new(Path.Combine(Directory, "tool path with spaces"), null);
        }
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "groundhog-update-" + Guid.NewGuid().ToString("N"));
        internal FakeTimeProvider Time { get; } = new();
        internal HttpClient Http { get; }
        internal UpdateCheck Check { get; }
        internal string Current { get; }
        internal string[] Versions { get; set; } = ["1.0.0", "1.1.0"];
        internal HashSet<string> Unlisted { get; } = [];
        internal List<Uri> Requests { get; } = [];
        internal ToolInstallation? Installation { get; set; }
        internal List<(ToolInstallation Installation, string Version)> Installed { get; } = [];
        internal int Prompts { get; private set; }
        internal int InstallerExitCode { get; set; }
        internal bool Offline { get; set; }
        internal bool InvalidMetadata { get; set; }
        internal bool Block { get; set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StringWriter Output { get; } = new();
        internal StringWriter Error { get; } = new();
        internal UpdateCommands Commands(bool interactive, bool accept = false) => new(Check, Installation, interactive,
            (_, _) => { Prompts++; return Task.FromResult(accept); },
            (installation, version, _) => { Installed.Add((installation, version)); return Task.FromResult(InstallerExitCode); },
            Output, Error, Current);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Started.TrySetResult();
            if (Block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Offline)
            {
                throw new HttpRequestException("Offline");
            }

            var index = request.RequestUri!.AbsolutePath.Contains("flatcontainer", StringComparison.Ordinal);
            var candidate = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath);
            var json = InvalidMetadata ? "{}" : index
                ? JsonSerializer.Serialize(new { versions = Versions })
                : JsonSerializer.Serialize(new { listed = !Unlisted.Contains(candidate) });
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                System.IO.Directory.Delete(Directory, true);
            }

            base.Dispose(disposing);
        }
    }
}
