# Groundhog.Agent

The agent of Groundhog, the hosted HTTPS tunnel service for local development, as a library you can embed. It keeps
an outbound connection to the Groundhog service and forwards each public request to an app on your machine, keeping the
`Host` header, streaming bodies and passing WebSockets through. Dropped connections are reopened by themselves.

```csharp
using Groundhog.Agent;

var agent = new GroundhogAgent(new GroundhogAgentOptions
{
    TunnelName = "myapp",
    Credential = new TunnelKeyCredential(Environment.GetEnvironmentVariable("GROUNDHOG_KEY")!),
    LocalTarget = new Uri("http://localhost:5173"),
});
var run = agent.RunAsync(cancellationToken);
await foreach (var status in agent.WatchStatusAsync())
{
    Console.WriteLine($"{status.State} {status.PublicAddress} {status.Reason}");
}
var reason = await run;
```

In an ASP.NET Core or worker app, let the host run it instead:

```csharp
builder.Services.AddGroundhogAgent(options =>
{
    options.TunnelName = "myapp";
    options.Credential = new TunnelKeyCredential(builder.Configuration["Groundhog:Key"]!);
    options.LocalTarget = new Uri("http://localhost:5000");
});
```

- The agent connects to the hosted Groundhog service; there's no service address to configure.
- `GroundhogCredential` is the extension point for sign-in methods; `TunnelKeyCredential` (scheme `GroundhogKey`)
  is built in.
- `LocalTarget` accepts HTTP or HTTPS. TLS uses the target hostname while the app receives the public `Host`.
  HTTPS uses platform trust; optional `LocalCaFile` or `InsecureLocal` controls apply only to loopback HTTPS.
- Optional `VisitorAccess` declares who may visit the tunnel (anyone, workspace members, API keys, IP ranges, per
  path). API keys, which callers send to reach a path, are separate from the tunnel's connection key. Pass them as
  plaintext from your secrets; only their SHA-256 hashes are sent.
- `Status` is the current state; `WatchStatusAsync` streams it and every change: connecting, connected (with the
  public address), reconnecting (with the reason and the wait) and stopped. Each stream is buffered separately and
  never holds up the agent.
- `OnRequestCompleted` reports each request's method, path (without query string), status and duration.
- Metrics go to the `Groundhog.Agent` meter: `groundhog.agent.request.duration` and `groundhog.agent.connections`.
- `RunAsync` returns why the agent stopped: cancelled, credential rejected, not allowed, unknown tunnel,
  unsupported protocol, replaced by a newer session, or a service that doesn't support declared visitor access rules.

Requires the ASP.NET Core shared framework (.NET 10 or later). See `docs/agent.md` in the source repository for
the full guide.

MIT licensed.
