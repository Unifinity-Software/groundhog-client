using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Groundhog.Agent.Tests;

public class AgentStatusTests(TunnelFixture fixture) : IClassFixture<TunnelFixture>
{
    [Fact(Timeout = 30_000)]
    public async Task Status_stream_reports_every_change_and_ends_after_stopping()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new GroundhogAgent(Options("status-stream"));
        Assert.Equal(GroundhogAgentState.NotStarted, agent.Status.State);

        var statuses = agent.WatchStatusAsync(ct);
        using var stop = new CancellationTokenSource();
        var run = agent.RunAsync(stop.Token);
        var seen = new List<GroundhogAgentStatus>();
        await foreach (var status in statuses)
        {
            seen.Add(status);
            if (status.State == GroundhogAgentState.Connected)
            {
                Assert.Equal(status, agent.Status);
                await stop.CancelAsync();
            }
        }

        Assert.Equal(
            [GroundhogAgentState.NotStarted, GroundhogAgentState.Connecting, GroundhogAgentState.Connected, GroundhogAgentState.Stopped],
            seen.Select(s => s.State));
        Assert.Equal(new Uri("https://status-stream.groundhog.unifinity.com"), seen[2].PublicAddress);
        Assert.Equal(GroundhogAgentStopReason.Cancelled, seen[3].StopReason);
        Assert.Equal(GroundhogAgentStopReason.Cancelled, await run);

        // A stream started after the agent stopped yields the final status and ends.
        Assert.Equal([seen[3]], await agent.WatchStatusAsync(ct).ToListAsync(ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task A_late_stream_starts_from_the_current_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var running = fixture.StartAgent("status-late");
        Assert.Equal(GroundhogAgentState.Connected, (await running.NextStatusAsync()).State);

        await using var late = running.Agent.WatchStatusAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await late.MoveNextAsync());
        Assert.Equal(GroundhogAgentState.Connected, late.Current.State);
    }

    [Fact(Timeout = 30_000)]
    public async Task Records_request_and_connection_metrics()
    {
        var ct = TestContext.Current.CancellationToken;
        var measurements = new ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == GroundhogAgent.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, object?>(tags.ToArray());
            if (Equals(map.GetValueOrDefault("groundhog.tunnel"), "metrics"))
            {
                measurements.Enqueue((instrument.Name, value, map));
            }
        }
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();

        var tunnel = fixture.Relay.Tunnel("metrics");
        await using (var agent = fixture.StartAgent("metrics"))
        {
            Assert.Equal(GroundhogAgentState.Connected, (await agent.NextStatusAsync()).State);
            var relay = await tunnel.Connections.Reader.ReadAsync(ct);
            using (await relay.Client.GetAsync("http://metrics.groundhog.unifinity.com/echo", ct))
            {
            }

            await agent.NextRequestAsync();
        }

        var duration = Assert.Single(measurements, m => m.Instrument == "groundhog.agent.request.duration");
        Assert.Equal("GET", duration.Tags["http.request.method"]);
        Assert.Equal(200, duration.Tags["http.response.status_code"]);
        Assert.Equal(false, duration.Tags["groundhog.websocket"]);
        Assert.False(duration.Tags.ContainsKey("error.type"));
        Assert.Equal([1d, -1d], measurements.Where(m => m.Instrument == "groundhog.agent.connections").Select(m => m.Value));
    }

    [Fact(Timeout = 30_000)]
    public async Task A_throwing_request_callback_does_not_break_the_tunnel()
    {
        var ct = TestContext.Current.CancellationToken;
        var tunnel = fixture.Relay.Tunnel("callback-throws");
        var options = Options("callback-throws");
        options.OnRequestCompleted = _ => throw new InvalidOperationException("boom");
        var agent = new GroundhogAgent(options);
        var statuses = agent.WatchStatusAsync(ct).GetAsyncEnumerator(ct);
        using var stop = new CancellationTokenSource();
        var run = agent.RunAsync(stop.Token);
        while (await statuses.MoveNextAsync() && statuses.Current.State != GroundhogAgentState.Connected)
        {
        }

        var relay = await tunnel.Connections.Reader.ReadAsync(ct);
        for (var i = 0; i < 2; i++)
        {
            using var response = await relay.Client.GetAsync("http://callback-throws.groundhog.unifinity.com/echo", ct);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        await stop.CancelAsync();
        Assert.Equal(GroundhogAgentStopReason.Cancelled, await run);
    }

    [Fact(Timeout = 30_000)]
    public async Task Hosted_agent_runs_while_the_host_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGroundhogAgent(options =>
        {
            options.RelayAddress = fixture.Relay.Address;
            options.TunnelName = "hosted";
            options.Credential = new TunnelKeyCredential(fixture.Key);
            options.LocalTarget = fixture.LocalApp;
        });
        using var host = builder.Build();
        var agent = host.Services.GetRequiredService<GroundhogAgent>();
        var statuses = agent.WatchStatusAsync(ct).GetAsyncEnumerator(ct);

        await host.StartAsync(ct);
        while (await statuses.MoveNextAsync() && statuses.Current.State != GroundhogAgentState.Connected)
        {
        }

        Assert.Equal(new Uri("https://hosted.groundhog.unifinity.com"), statuses.Current.PublicAddress);
        await host.StopAsync(ct);
        Assert.Equal(GroundhogAgentStopReason.Cancelled, agent.Status.StopReason);
    }

    [Fact]
    public async Task Hosted_agent_with_invalid_options_fails_the_hosts_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGroundhogAgent(options => options.TunnelName = "no-credential");
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Credential", error.ParamName);
    }

    private GroundhogAgentOptions Options(string tunnelName) => new()
    {
        RelayAddress = fixture.Relay.Address,
        TunnelName = tunnelName,
        Credential = new TunnelKeyCredential(fixture.Key),
        LocalTarget = fixture.LocalApp,
    };
}
