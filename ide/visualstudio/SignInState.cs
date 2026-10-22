namespace Groundhog.VisualStudio;

internal sealed class SignInState
{
    private string? _code;
    private string? _verification;
    private DateTimeOffset _expires;

    internal void Begin(string code, string verification, DateTimeOffset expires)
    { _code = code; _verification = verification; _expires = expires; }

    internal string? Verification(DateTimeOffset now) => now < _expires ? _verification : null;

    internal string Notice(string? state, DateTimeOffset now)
    {
        if (state == "signedIn") { Clear(); return "Double-click a tunnel to inspect requests. Right-click for hosting and tunnel actions."; }
        if (_code is not null && now < _expires)
        { return "Approve sign-in in your browser. Confirm code: " + _code + ". Choose Open browser."; }
        Clear();
        return "Sign in to see your Groundhog workspaces. Connection keys stay in the CLI's protected store.";
    }

    internal void Clear() { _code = null; _verification = null; }
}
