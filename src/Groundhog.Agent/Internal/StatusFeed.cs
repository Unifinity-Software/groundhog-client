using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Groundhog.Agent.Internal;

/// <summary>
/// The agent's current status and its watchers. Each watcher has its own channel, so the agent's threads only ever
/// queue a status and never run a watcher's code.
/// </summary>
internal sealed class StatusFeed
{
    private readonly Lock _gate = new();
    private readonly List<ChannelWriter<GroundhogAgentStatus>> _watchers = [];
    private GroundhogAgentStatus _current = GroundhogAgentStatus.NotStarted;

    public GroundhogAgentStatus Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Publish(GroundhogAgentStatus status)
    {
        lock (_gate)
        {
            _current = status;
            foreach (var watcher in _watchers)
            {
                watcher.TryWrite(status);
            }

            // Stopped is final for this run: every watcher's stream ends after it.
            if (status.State == GroundhogAgentState.Stopped)
            {
                foreach (var watcher in _watchers)
                {
                    watcher.TryComplete();
                }

                _watchers.Clear();
            }
        }
    }

    /// <summary>Subscribes now, so nothing published after this call is missed even before enumeration starts.</summary>
    public IAsyncEnumerable<GroundhogAgentStatus> Watch(CancellationToken cancellationToken)
    {
        // Status changes are rare (a reconnect at most every 30 seconds), so an unbounded queue stays small.
        var channel = Channel.CreateUnbounded<GroundhogAgentStatus>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        lock (_gate)
        {
            channel.Writer.TryWrite(_current);
            if (_current.State == GroundhogAgentState.Stopped)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _watchers.Add(channel.Writer);
            }
        }

        return ReadAsync(channel, cancellationToken);
    }

    private async IAsyncEnumerable<GroundhogAgentStatus> ReadAsync(
        Channel<GroundhogAgentStatus> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var status in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return status;
            }
        }
        finally
        {
            lock (_gate)
            {
                _watchers.Remove(channel.Writer);
            }
        }
    }
}
