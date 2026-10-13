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
    public async Task Startup_notice_is_printed_once_per_day_and_never_prompts_or_installs()
    {
        using var fixture = new Fixture();
        using var commands = fixture.Commands(interactive: true, accept: true);
        await commands.NoticeAsync(Cancellation);
        Assert.Contains("1.1.0 is available (you have 1.0.0). Run groundhog update", fixture.Error.ToString(), StringComparison.Ordinal);
        var requests = fixture.Requests.Count;
        await commands.NoticeAsync(Cancellation);
        Assert.Equal(requests, fixture.Requests.Count);
        Assert.Single(fixture.Error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        // The dashboard still sees the cached result, so it can show the update on every start.
        Assert.Equal("1.1.0", await commands.AvailableAsync(Cancellation));
        fixture.Time.Advance(TimeSpan.FromDays(1));
        await commands.NoticeAsync(Cancellation);
        Assert.Equal(2, fixture.Error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(0, fixture.Prompts);
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
        Assert.Equal(0, await commands.RunAsync(["update"], Cancellation));
        Assert.Equal(1, fixture.Prompts);
        var installed = Assert.Single(fixture.Installed);
        Assert.Equal("1.1.0", installed.Version);
        Assert.Equal(fixture.Installation, installed.Installation);
        Assert.Contains("Run your command again", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_finished_after_exit_says_so_and_waits_for_this_process_before_running_the_same_installer()
    {
        using var fixture = new Fixture { Outcome = InstallOutcome.AfterExit };
        using var commands = fixture.Commands(interactive: false);
        Assert.Equal(0, await commands.RunAsync(["update", "--yes"], Cancellation));
        Assert.Contains("a new window finishes installing 1.1.0", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Updated to", fixture.Output.ToString(), StringComparison.Ordinal);

        var scope = new ToolInstallation(@"C:\Users\o'brien\.dotnet\tools", null);
        var script = UpdateCommands.AfterExitScript(scope, "1.1.0", 4242);
        Assert.Contains("Wait-Process -Id 4242", script, StringComparison.Ordinal);
        // Every installer argument is passed single-quoted, with embedded quotes doubled.
        Assert.Contains("& dotnet " + string.Join(' ', scope.Arguments("1.1.0").Select(a => "'" + a.Replace("'", "''", StringComparison.Ordinal) + "'")),
            script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripts_must_opt_in_with_yes_and_do_not_receive_startup_checks()
    {
        using var fixture = new Fixture();
        using var commands = fixture.Commands(interactive: false);
        await commands.NoticeAsync(Cancellation);
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
    [InlineData("dashboard")]
    public void Startup_check_skips_help_scripts_background_processes_explicit_checks_and_the_dashboard(string option)
    {
        Assert.False(UpdateCommands.ShouldCheck(true, [option], _ => null));
        Assert.False(UpdateCommands.ShouldCheck(true, [], _ => null));
        Assert.True(UpdateCommands.Enabled(true, [], _ => null));
        Assert.False(UpdateCommands.Enabled(true, [], _ => "1"));
        Assert.False(UpdateCommands.ShouldCheck(false, ["host"], _ => null));
        Assert.False(UpdateCommands.ShouldCheck(true, ["host"], _ => "1"));
        Assert.True(UpdateCommands.ShouldCheck(true, ["host", "myapp", "--port", "5173"], _ => null));
    }

    [Fact]
    public async Task Failed_install_is_reported_and_does_not_claim_success()
    {
        using var fixture = new Fixture { Outcome = InstallOutcome.Failed };
        using var commands = fixture.Commands(interactive: true, accept: true);
        Assert.Equal(1, await commands.RunAsync(["update", "--yes"], Cancellation));
        Assert.Single(fixture.Installed);
        Assert.DoesNotContain("Updated to", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("update did not complete", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unidentified_installation_does_not_auto_check_or_update_a_different_installation()
    {
        using var fixture = new Fixture { Installation = null };
        using var commands = fixture.Commands(interactive: true, accept: true);
        await commands.NoticeAsync(Cancellation);
        Assert.Null(await commands.AvailableAsync(Cancellation));
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
        await commands.NoticeAsync(Cancellation);
        Assert.Empty(fixture.Error.ToString());
        var requests = fixture.Requests.Count;
        Assert.Null(await commands.AvailableAsync(Cancellation));
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
        await commands.NoticeAsync(Cancellation);
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
        internal InstallOutcome Outcome { get; set; } = InstallOutcome.Installed;
        internal bool Offline { get; set; }
        internal bool InvalidMetadata { get; set; }
        internal bool Block { get; set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StringWriter Output { get; } = new();
        internal StringWriter Error { get; } = new();
        internal UpdateCommands Commands(bool interactive, bool accept = false) => new(Check, Installation, interactive,
            (_, _) => { Prompts++; return Task.FromResult(accept); },
            (installation, version, _) => { Installed.Add((installation, version)); return Task.FromResult(Outcome); },
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
