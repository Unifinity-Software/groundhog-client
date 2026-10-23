using Newtonsoft.Json.Linq;

namespace Groundhog.VisualStudio;

internal sealed class DetailSelection
{
    private long _generation;
    internal JObject? Detail { get; private set; }
    internal long Begin() => ++_generation;

    internal bool Complete(long generation, JObject detail)
    {
        if (generation != _generation) { return false; }
        Detail = detail;
        return true;
    }

    internal string ActionId(string? requested)
    {
        var displayed = (string?)Detail?["requestId"];
        return displayed is not null && requested == displayed ? displayed
            : throw new InvalidOperationException("The displayed request changed. Select it again before using this action.");
    }
}
