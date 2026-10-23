using Newtonsoft.Json.Linq;

namespace Groundhog.VisualStudio;

/// <summary>Retains bounded early notifications until a subscribe response identifies their owner.</summary>
internal sealed class SubscriptionEvents
{
    private readonly object _gate = new();
    private readonly Queue<(string Method, JObject Value)> _early = new();
    private long _generation;
    private bool _pending;
    private string? _subscription;

    internal long Begin()
    {
        lock (_gate)
        {
            _generation++; _pending = true; _subscription = null; _early.Clear();
            return _generation;
        }
    }

    internal bool IsCurrent(long generation)
    { lock (_gate) { return generation == _generation; } }

    internal (string Method, JObject Value)[] Complete(long generation, string subscription)
    {
        lock (_gate)
        {
            if (generation != _generation) { return []; }
            _subscription = subscription; _pending = false;
            var messages = _early.Where(item => (string?)item.Value["subscriptionId"] == subscription).ToArray();
            _early.Clear(); return messages;
        }
    }

    internal bool Accept(string method, JObject value)
    {
        if (method is not ("requests/event" or "connection/event")) { return false; }
        lock (_gate)
        {
            if (_pending)
            {
                if (_early.Count == 1000) { _early.Dequeue(); }
                _early.Enqueue((method, (JObject)value.DeepClone()));
                return false;
            }
            return _subscription is not null && (string?)value["subscriptionId"] == _subscription;
        }
    }

    internal void Cancel()
    { lock (_gate) { _generation++; _pending = false; _subscription = null; _early.Clear(); } }
}
