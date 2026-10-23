namespace Groundhog.VisualStudio;

internal sealed class TunnelSelection
{
    private bool _rebuilding;
    internal TunnelNode? Current { get; private set; }

    internal void Select(TunnelNode? tunnel)
    {
        if (!_rebuilding) { Current = tunnel; }
    }

    internal TunnelNode? Rebuild(IEnumerable<TunnelNode> tunnels, Action replaceItems)
    {
        var previous = Current;
        Current = null;
        _rebuilding = true;
        try
        {
            replaceItems();
            Current = previous is null ? null : tunnels.FirstOrDefault(t => t.Workspace == previous.Workspace && t.Name == previous.Name);
            return Current;
        }
        finally { _rebuilding = false; }
    }
}
