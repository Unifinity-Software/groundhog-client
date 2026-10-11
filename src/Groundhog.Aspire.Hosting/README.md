# Groundhog.Aspire.Hosting

A .NET Aspire integration for Groundhog, the hosted HTTPS tunnel service for local development. It runs a tunnel
agent inside your AppHost, gives one of your app's endpoints a public HTTPS address through the Groundhog service,
and lets other resources use that address.

## Install and connect

Requires .NET 10 and Aspire 13.6.1 or later. Add the package to your AppHost:

```text
dotnet add package Groundhog.Aspire.Hosting --version 0.4.0-preview.1
```

Use `dotnet add package` for this integration. Aspire CLI integration discovery currently accepts the
`Aspire.Hosting.*` and `CommunityToolkit.Aspire.Hosting.*` prefixes, so `aspire add` does not discover this package.

Create a tunnel in the Groundhog website and copy its connection key. The hosted service address is automatic.
Follow the [illustrated first-tunnel walkthrough](https://github.com/Unifinity-Software/groundhog-client/blob/master/samples/AspireQuickstart/README.md)
for the website steps, exact fields, and the response that confirms success.

```csharp
builder.AddProject<Projects.MyApp_Web>("web").WithGroundhogTunnel("myapp");
```

This adds a `web-tunnel` resource that forwards to the project's `https` endpoint (or `http` if it has none; pass
`endpointName` to choose), and a secret parameter `groundhog-myapp-key`. Enter the key when the Aspire dashboard
asks for it and enable **Save to user secrets** to remember it, or set `Parameters:groundhog-myapp-key` in the
AppHost's **Manage User Secrets** editor. Keep the key out of source control and shell history.

Use `configure` to pass the public address to your app:

```csharp
var web = builder.AddProject<Projects.MyApp_Web>("web");
web.WithGroundhogTunnel("myapp", configure: tunnel =>
    web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress));
```

`AddGroundhogTunnel("tunnel", "myapp", key).WithReference(endpoint)` is the explicit form, for choosing the resource
name or key parameter yourself; its tunnel name can also be a parameter.

- The key must be a secret parameter; it's handed to the agent in memory and never shown on a command line, in the
  dashboard or in a log.
- The dashboard shows the tunnel as connecting, running (with its public address), reconnecting, or failed with
  the reason.
- A stopped tunnel has a **Reconnect** command in the dashboard. It resolves the current settings again and starts
  a new agent without restarting the AppHost. Active tunnels cannot be restarted with this command.
- `PublicAddress` (for example `https://myapp.groundhog.unifinity.com`) and `PublicHost` (`myapp.groundhog.unifinity.com`)
  can be passed to `WithEnvironment` or used in a `ReferenceExpression`.
- Nothing waits for the tunnel unless it opts in with `WaitFor`, so an unreachable service never stops your app from
  starting locally.

## Request logging

Add `.WithRequestLogging()` to the tunnel builder to write completed requests to its Aspire console:

```csharp
web.WithGroundhogTunnel("myapp", configure: tunnel => tunnel.WithRequestLogging());
```

Lines look like `GET /api/health 200 12 ms` or `WS /socket 101 850 ms`. WebSockets are reported when they close.
Logging is off by default; query strings, headers and bodies are omitted. The existing `Connected:` line includes
the public tunnel URL, which opens the live app. Request lines describe completed traffic rather than linking to a
stored request.

HTTPS local endpoints need a certificate trusted by this machine. In publish mode
the tunnel is omitted and its agent does not start. C# AppHosts are supported; TypeScript bindings are not
currently advertised. The complete package-only example is in `samples/AspireQuickstart`.

See [the Aspire guide](https://github.com/Unifinity-Software/groundhog-client/blob/master/docs/aspire.md) for more options.

MIT licensed.
