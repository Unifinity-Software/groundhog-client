using System.Net;
using System.Net.Http.Json;

namespace Groundhog.Cli.Tests;

public sealed partial class IdeBackendTests
{
    [Theory(Timeout = 10_000)]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("timeout")]
    [InlineData("expired")]
    public async Task Pending_login_preserves_existing_session_until_its_own_terminal_outcome(string outcome)
    {
        await using var fixture = new Fixture();
        fixture.Store.Update(saved => saved! with { SessionId = "original-login", TunnelKeys = new() { ["example/my-tunnel"] = fixture.Handler.Key } });
        var initialized = await fixture.InitializeAsync();
        Assert.Contains("loginPending", initialized.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        var original = fixture.Store.Read()!;
        fixture.Handler.GrantState = "pending";
        var challenge = await fixture.CallAsync("auth/login", new { });
        var pending = await fixture.CallAsync("auth/status", new { });
        Assert.True(pending.GetProperty("loginPending").GetBoolean());
        Assert.Equal("signedIn", pending.GetProperty("state").GetString());
        Assert.Equal("example", pending.GetProperty("workspace").GetString());
        Assert.Equal(challenge.GetProperty("loginId").GetString(), pending.GetProperty("loginId").GetString());
        fixture.Store.Update(saved => saved! with { Expires = saved.Expires.AddMinutes(10) });
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Handler.PollReached.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True((await fixture.CallAsync("auth/status", new { })).GetProperty("loginPending").GetBoolean());
        fixture.Handler.GrantState = outcome;
        if (outcome == "timeout") { fixture.Handler.DeviceTokenError = new TaskCanceledException("Synthetic timeout"); }
        fixture.Time.Advance(TimeSpan.FromSeconds(outcome == "expired" ? 600 : 1));
        await fixture.AuthSettled.Task.WaitAsync(TestContext.Current.CancellationToken);
        var settled = await fixture.CallAsync("auth/status", new { });
        Assert.False(settled.GetProperty("loginPending").GetBoolean());
        Assert.Equal(outcome == "approved" ? "approved" : outcome == "expired" ? "expired" : "failed", settled.GetProperty("loginOutcome").GetString());
        Assert.Equal("signedIn", settled.GetProperty("state").GetString());
        Assert.Equal("example", settled.GetProperty("workspace").GetString());
        var saved = fixture.Store.Read()!;
        Assert.Equal(Fixture.Token, saved.Token);
        Assert.Equal(fixture.Handler.Key, saved.TunnelKeys["example/my-tunnel"]);
        if (outcome == "approved") { Assert.NotEqual(original.SessionId, saved.SessionId); }
        else { Assert.Equal(original.SessionId, saved.SessionId); }
    }

    [Fact(Timeout = 10_000)]
    public async Task A_cancelled_device_request_cannot_attach_to_or_clear_the_newer_login()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        fixture.Handler.DeviceResponse = async _ =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                entered.TrySetResult();
                await release.Task; // Model a transport completing after cancellation, without a sleep.
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DeviceAuthorization("private-device-code", "ABCD-EFGH", "https://groundhog.unifinity.com/cli-login", 600, 1)) };
        };
        var old = fixture.CallAsync("auth/login", new { });
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True((await fixture.CallAsync("auth/status", new { })).GetProperty("loginPending").GetBoolean());
        var current = await fixture.CallAsync("auth/login", new { });
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => old);
        var pending = await fixture.CallAsync("auth/status", new { });
        Assert.True(pending.GetProperty("loginPending").GetBoolean());
        Assert.Equal(current.GetProperty("loginId").GetString(), pending.GetProperty("loginId").GetString());
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.AuthSettled.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False((await fixture.CallAsync("auth/status", new { })).GetProperty("loginPending").GetBoolean());
        await fixture.ToServer.Writer.CompleteAsync();
        await fixture.Server.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Server.IsCompletedSuccessfully);
    }
}
