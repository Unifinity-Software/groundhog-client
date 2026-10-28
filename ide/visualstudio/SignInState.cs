namespace Groundhog.VisualStudio;

internal sealed class SignInState
{
    private string? _code;
    private string? _verification;
    private DateTimeOffset _expires;
    internal int Revision { get; private set; }

    internal void Begin(string code, string verification, DateTimeOffset expires)
    { _code = code; _verification = verification; _expires = expires; Revision++; }

    internal string? Verification(DateTimeOffset now) => now < _expires ? _verification : null;

    internal string Notice(string? state, DateTimeOffset now, bool? loginPending = null, int? observedRevision = null)
    {
        // An old signed-in credential is still usable during device authorization. A status
        // request issued before Begin must not dismiss the challenge it overtook either.
        if (_code is not null && now < _expires && (loginPending != false || observedRevision is { } revision && revision != Revision))
        { return "Approve sign-in in your browser. Confirm code: " + _code + ". Choose Open browser."; }
        Clear();
        if (state == "signedIn") { return "Double-click a tunnel to inspect requests. Right-click for hosting and tunnel actions."; }
        return "Sign in to see your Groundhog workspaces. Connection keys stay in the CLI's protected store.";
    }

    internal void Clear() { if (_code is not null) { Revision++; } _code = null; _verification = null; }
}
