# Groundhog.Aspire.Hosting

A .NET Aspire integration for Groundhog, a self-hosted HTTPS tunnel for local development. It runs a tunnel agent
inside your AppHost, gives one of your app's endpoints a public HTTPS address through your Groundhog relay, and lets
other resources use that address.

## Install and connect

Requires .NET 10 and Aspire 13.6.1 or later. Add the package to your AppHost:

```text
dotnet add package Groundhog.Aspire.Hosting --version 0.2.0-preview.2
```

Use `dotnet add package` for this integration. Aspire CLI integration discovery currently accepts the
`Aspire.Hosting.*` and `CommunityToolkit.Aspire.Hosting.*` prefixes, so `aspire add` does not discover this package.

You need an existing Groundhog relay, a tunnel name, and its connection key. Installation does not provision
the relay or tunnel. Set `Parameters:groundhog-key` through Aspire's secret parameter prompt or the AppHost's
**Manage User Secrets** editor. Keep the key out of source control and shell history.

Before connecting, request `GET https://tunnel.example.com/_groundhog/v1/health` on the relay's own address.
The response must have `"status":"ok"` and include `1` in `"protocol"`. Otherwise ask the operator for a
compatible relay. This preflight is optional; an unreachable relay does not prevent local development.

```csharp
var key = builder.AddParameter("groundhog-key", secret: true); // set in user secrets

var tunnel = builder.AddGroundhogTunnel("tunnel", "https://tunnel.example.com", "myapp", key)
    .WithReference(web.GetEndpoint("http"));

web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress);
```

- The relay address and tunnel name can be strings or parameters. The key must be a secret parameter; it's handed
  to the agent in memory and never shown on a command line, in the dashboard or in a log. Another overload takes
  any `GroundhogCredential`.
- The dashboard shows the tunnel as connecting, running (with its public address), reconnecting, or failed with
  the reason.
- A stopped tunnel has a **Reconnect** command in the dashboard. It resolves the current settings again and starts
  a new agent without restarting the AppHost. Active tunnels cannot be restarted with this command.
- `PublicAddress` (for example `https://myapp.tunnel.example.com`) and `PublicHost` (`myapp.tunnel.example.com`)
  can be passed to `WithEnvironment` or used in a `ReferenceExpression`.
- Nothing waits for the tunnel unless it opts in with `WaitFor`, so an unreachable relay never stops your app from
  starting locally.

## Request logging

Add `.WithRequestLogging()` to the tunnel builder to write completed requests to its Aspire console:

```csharp
var tunnel = builder.AddGroundhogTunnel("tunnel", "https://tunnel.example.com", "myapp", key)
    .WithReference(web.GetEndpoint("http"))
    .WithRequestLogging();
```

Lines look like `GET /api/health 200 12 ms` or `WS /socket 101 850 ms`. WebSockets are reported when they close.
Logging is off by default; query strings, headers and bodies are omitted. The existing `Connected:` line includes
the public tunnel URL, which opens the live app. Request lines describe completed traffic rather than linking to a
stored request.

Use an HTTP local endpoint, or an HTTPS endpoint with a certificate trusted by this machine. In publish mode
the tunnel is omitted and its agent does not start. C# AppHosts are supported; TypeScript bindings are not
currently advertised. The complete package-only example is in `samples/AspireQuickstart`.

Preview source and guide links currently require repository access; a separate public client repository
will provide accessible source before stable promotion. See `docs/aspire.md` for the full guide.

MIT licensed.
