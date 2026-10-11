# Groundhog.Agent

The agent of Groundhog, the hosted HTTPS tunnel service for local development, as a library you can embed. It keeps
an outbound connection to the Groundhog service and forwards each public request to an app on your machine, keeping the
`Host` header, streaming bodies and passing WebSockets through. Dropped connections are reopened by themselves.

```csharp
using Groundhog.Agent;

var agent = new GroundhogAgent(new GroundhogAgentOptions
{
    RelayAddress = new Uri(GroundhogService.DefaultRelayAddress),
    TunnelName = "myapp",
    Credential = new TunnelKeyCredential(Environment.GetEnvironmentVariable("GROUNDHOG_KEY")!),
    LocalTarget = new Uri("http://localhost:5173"),
});
agent.StatusChanged += (_, e) => Console.WriteLine($"{e.State} {e.PublicAddress} {e.Reason}");
var reason = await agent.RunAsync(cancellationToken);
```

- `GroundhogCredential` is the extension point for sign-in methods; `TunnelKeyCredential` (scheme `GroundhogKey`)
  is built in.
- `LocalTarget` accepts HTTP or HTTPS. TLS uses the target hostname while the app receives the public `Host`.
  HTTPS uses platform trust; optional `LocalCaFile` or `InsecureLocal` controls apply only to loopback HTTPS.
- `StatusChanged` reports connecting, connected (with the public address), reconnecting (with the reason and the
  wait) and stopped. `RequestCompleted` reports each request's method, path (without query string), status and
  duration.
- `RunAsync` returns why the agent stopped: cancelled, credential rejected, not allowed, unknown tunnel,
  unsupported protocol, or replaced by a newer session.

Requires the ASP.NET Core shared framework (.NET 10 or later). See `docs/agent.md` in the source repository for
the full guide.

MIT licensed.
