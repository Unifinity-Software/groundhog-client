using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Groundhog.Cli.Interactive;
using Groundhog.Cli.Views;

namespace Groundhog.Cli.Tests;

/// <summary>Live remote requests: the relay's server-sent request stream, reconnection, and the views that follow it.</summary>
public sealed class LiveRequestTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 14, 0, 0, TimeSpan.Zero);

    private static RemoteRequest Request(string id, int seconds, int status = 200, string? path = null) =>
        new(Start.AddSeconds(seconds), "GET", path ?? "/" + id, status, 12, id);

    private static string Event(RemoteRequest r) =>
        $"event: request\ndata: {{\"requestId\":\"{r.RequestId}\",\"tunnel\":\"myapp\",\"completed\":\"{r.Completed:O}\"," +
        $"\"method\":\"{r.Method}\",\"path\":\"{r.Path}\",\"statusCode\":{r.StatusCode},\"durationMs\":{r.DurationMs}," +
        "\"bytesIn\":0,\"bytesOut\":10,\"bodiesCaptured\":false}\n\n";

    private static HttpResponseMessage EventStream(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static RequestStream Stream(string text)
    {
        var body = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return new RequestStream(EventStream(body), body);
    }

    [Fact]
    public async Task The_stream_reads_request_events_and_the_relays_retry_and_skips_everything_else()
    {
        var expected = Request("abc", 5, 502, "/api/orders");
        await using var stream = Stream("retry: 1000\n\n: keep-alive\n\nevent: notice\ndata: {}\n\n" +
            "event: request\ndata: not json\n\n" + Event(expected));

        var read = new List<RemoteRequest>();
        await foreach (var request in stream.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            read.Add(request);
        }

        Assert.Equal(expected, Assert.Single(read));
        Assert.Equal(TimeSpan.FromSeconds(1), stream.Retry);
    }

    [Fact]
    public async Task The_follower_reconnects_backs_off_fills_gaps_once_and_stops_when_the_relay_refuses()
    {
        var known = Request("known", 0);
        var a = Request("a", 1);
        var b = Request("b", 2);
        var gap = Request("gap", 3);
        var c = Request("c", 4);
        var ancient = Request("ancient", -3600);
        var opens = new Queue<Func<RequestStream>>([
            () => Stream("retry: 2000\n\n" + Event(a) + Event(b)),
            () => throw new HttpRequestException("connection reset"),
            () => throw new ManagementException("Rate limited.", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)),
            () => Stream(Event(b) + Event(c)),
            () => throw new ManagementException("Sign-in expired or is missing. Run groundhog login.", HttpStatusCode.Unauthorized),
        ]);
        var backfills = new Queue<RemoteRequest[]>([[], [b, gap, ancient]]);
        var delays = new List<TimeSpan>();
        var follower = new RequestFollower(_ => Task.FromResult(opens.Dequeue()()), _ => Task.FromResult(backfills.Dequeue()),
            TimeProvider.System)
        {
            Delay = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
        };
        follower.Seen([known]);
        var reported = new List<string>();
        var states = new List<LiveState>();

        await follower.RunAsync(r => reported.Add(r.RequestId!), states.Add, TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b", "gap", "c"], reported);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7), RequestFollower.DefaultRetry], delays);
        Assert.Equal([LiveStatus.Live, LiveStatus.Reconnecting, LiveStatus.Reconnecting, LiveStatus.Reconnecting, LiveStatus.Live,
            LiveStatus.Reconnecting, LiveStatus.Stopped], states.Select(s => s.Status));
        Assert.Contains("groundhog login", states[^1].Message, StringComparison.Ordinal);
        Assert.Empty(opens);
        Assert.Empty(backfills);
    }

    [Fact]
    public async Task Remote_follow_prints_each_new_matching_request_once_until_stopped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-follow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CredentialStore(directory);
            store.Save(new("https://groundhog.unifinity.com", "sample-credential", DateTimeOffset.UtcNow.AddHours(1), "example", new()));
            var now = DateTimeOffset.UtcNow;
            var old = new RemoteRequest(now.AddSeconds(-30), "POST", "/api/orders", 502, 40, "old");
            var pipe = new Pipe();
            using var handler = new LiveHandler(old, pipe.Reader.AsStream());
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("retry: 1000\n\n" + Event(old) +
                Event(old with { RequestId = "fine", StatusCode = 200, Path = "/healthy" }) +
                Event(old with { RequestId = "live", Completed = now, Path = "/api/live", StatusCode = 503 })),
                TestContext.Current.CancellationToken);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var output = new WatchingWriter("/api/live", stop);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteCommands.RunAsync(
                ["requests", "myapp", "--remote", "--follow", "--status", "5xx"], output, new StringWriter(), stop.Token, store,
                (relay, token) => new(relay, token, handler)));

            var text = output.ToString().ReplaceLineEndings("\n");
            Assert.Contains("Following live requests", text, StringComparison.Ordinal);
            // After the snapshot, only new matching requests are printed: not the logged one again, not a 200.
            var followed = text[text.IndexOf("Following live requests", StringComparison.Ordinal)..].Split('\n')[1..]
                .Where(line => line.Length > 0).ToArray();
            var line = Assert.Single(followed);
            Assert.Contains(" 503 ", line, StringComparison.Ordinal);
            Assert.EndsWith("/api/live", line, StringComparison.Ordinal);
            Assert.Equal("text/event-stream", handler.StreamAccept);
            await pipe.Writer.CompleteAsync();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Remote_follow_explains_a_relay_without_live_requests_and_refuses_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-follow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CredentialStore(directory);
            store.Save(new("https://groundhog.unifinity.com", "sample-credential", DateTimeOffset.UtcNow.AddHours(1), "example", new()));
            using var handler = new LiveHandler(Request("old", 0), stream: null);
            ManagementClient Factory(string relay, string? token) => new(relay, token, handler);
            var error = new StringWriter();
            Assert.Equal(ExitCodes.Failed, await RemoteCommands.RunAsync(["requests", "myapp", "--remote", "--follow"], new StringWriter(),
                error, TestContext.Current.CancellationToken, store, Factory));
            Assert.Contains("does not offer live requests", error.ToString(), StringComparison.Ordinal);

            await Assert.ThrowsAsync<ArgumentException>(() => RemoteCommands.RunAsync(
                ["requests", "myapp", "--remote", "--follow", "--output", "json"], new StringWriter(), new StringWriter(),
                TestContext.Current.CancellationToken, store, Factory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Tunnel_detail_adds_live_requests_to_its_log_and_stops_the_stream_when_it_closes()
    {
        var services = new FakeServices();
        using var console = Harness.Console(120, 36);
        var app = Harness.App(console, new ScriptedKeys(), services);
        var live = new RemoteRequest(FakeServices.Now.AddSeconds(1), "DELETE", "/api/sessions/live", 500, 9, "live-1");
        Assert.True(services.LiveRequests.Writer.TryWrite(live));
        // The first entry of the loaded log arrives again (the stream overlaps the log); it is shown once.
        Assert.True(services.LiveRequests.Writer.TryWrite(FakeServices.RemoteRequests[0]));
        var screen = new TunnelDetailScreen("web-team", "myapp");

        screen.Enter(app);
        screen.Resume(app);
        var text = Harness.Text(screen.Render(app, new ViewSize(120, 30)), 120, 30);

        Assert.Equal(1, services.Follows);
        Assert.Contains("Recent requests · live", text, StringComparison.Ordinal);
        Assert.Contains("/api/sessions/live", text, StringComparison.Ordinal);
        var lines = text.Split('\n');
        Assert.True(Array.FindIndex(lines, l => l.Contains("/api/sessions/live", StringComparison.Ordinal))
            < Array.FindIndex(lines, l => l.Contains("/api/products", StringComparison.Ordinal)), "Live requests are listed first.");
        Assert.Single(lines, l => l.Contains(FakeServices.RemoteRequests[0].Completed.ToString("HH:mm:ss"), StringComparison.Ordinal));

        screen.Leave(app);
        await services.FollowClosed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_stopped_stream_says_why_the_log_is_not_updating()
    {
        var insight = new TunnelInsight(FakeServices.Tunnels[0], FakeServices.Usage, FakeServices.RemoteRequests, null);
        var model = new TunnelDetailModel("web-team", "myapp", insight, null, null, FakeServices.Now,
            new LiveState(LiveStatus.Stopped, "Sign-in expired or is missing. Run groundhog login."));
        var text = Harness.Text(TunnelViews.Detail(model, new ViewSize(120, 30)), 120, 30);
        Assert.Contains("Recent requests · service log", text, StringComparison.Ordinal);
        Assert.Contains("Not updating", text, StringComparison.Ordinal);
    }

    /// <summary>Signals <paramref name="stop"/> once a line containing <paramref name="marker"/> is written.</summary>
    private sealed class WatchingWriter(string marker, CancellationTokenSource stop) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains(marker, StringComparison.Ordinal) == true)
            {
                stop.Cancel();
            }
        }
    }

    /// <summary>Serves the request log and a live stream (or, with no stream, a relay that answers JSON there).</summary>
    private sealed class LiveHandler(RemoteRequest logged, Stream? stream) : HttpMessageHandler
    {
        internal string? StreamAccept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/requests/stream", StringComparison.Ordinal))
            {
                StreamAccept = request.Headers.Accept.ToString();
                return Task.FromResult(stream is null
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) }
                    : EventStream(stream));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RemoteSearchResult([logged], false)),
            });
        }

        protected override void Dispose(bool disposing) { }
    }
}
