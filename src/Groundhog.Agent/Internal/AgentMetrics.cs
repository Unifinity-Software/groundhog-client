using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Groundhog.Agent.Internal;

/// <summary>The agent's instruments on the <see cref="GroundhogAgent.MeterName"/> meter.</summary>
internal sealed class AgentMetrics
{
    private static readonly Lazy<AgentMetrics> s_shared =
        new(() => new AgentMetrics(new Meter(GroundhogAgent.MeterName, GroundhogAgent.Version)));

    private readonly Histogram<double> _requestDuration;
    private readonly UpDownCounter<long> _openConnections;

    private AgentMetrics(Meter meter)
    {
        _requestDuration = meter.CreateHistogram<double>(
            "groundhog.agent.request.duration",
            unit: "s",
            description: "How long requests that came through the tunnel took, from arrival until the response finished.",
            advice: new InstrumentAdvice<double>
            {
                // The same boundaries ASP.NET Core uses for http.server.request.duration.
                HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10],
            });
        _openConnections = meter.CreateUpDownCounter<long>(
            "groundhog.agent.connections",
            unit: "{connection}",
            description: "Connections to the relay that are open.");
    }

    /// <summary>The meter factory's meter, or one shared meter for agents created without a factory.</summary>
    public static AgentMetrics Create(IMeterFactory? meterFactory) =>
        meterFactory is null
            ? s_shared.Value
            : new AgentMetrics(meterFactory.Create(GroundhogAgent.MeterName, GroundhogAgent.Version));

    public void RequestCompleted(string tunnel, GroundhogCompletedRequest request)
    {
        if (!_requestDuration.Enabled)
        {
            return;
        }

        var tags = new TagList
        {
            { "groundhog.tunnel", tunnel },
            { "http.request.method", request.Method },
            { "http.response.status_code", request.StatusCode },
            { "groundhog.websocket", request.IsWebSocket },
        };
        if (request.Error is not null)
        {
            tags.Add("error.type", request.Error);
        }

        _requestDuration.Record(request.Duration.TotalSeconds, tags);
    }

    public void ConnectionOpened(string tunnel) =>
        _openConnections.Add(1, new KeyValuePair<string, object?>("groundhog.tunnel", tunnel));

    public void ConnectionClosed(string tunnel) =>
        _openConnections.Add(-1, new KeyValuePair<string, object?>("groundhog.tunnel", tunnel));
}
