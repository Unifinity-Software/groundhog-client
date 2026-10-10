# Groundhog in a .NET Aspire AppHost

`Groundhog.Aspire.Hosting` adds a tunnel to a .NET Aspire AppHost. The tunnel gives one of your app's endpoints a
public HTTPS address through your Groundhog relay, and other resources can use that address in their settings (a
public-origin setting, a webhook base address, a dev server's allowed hosts).

The agent runs inside the AppHost process through the `Groundhog.Agent` library, so there's no tool to install and no
extra process. The connection key is a secret parameter: it's handed to the agent in memory and never appears on a
command line, in the dashboard or in a log.

You need a relay, a tunnel on it, and the tunnel's connection key. See [agent.md](agent.md) for what the agent does
once it's connected (reconnecting, what your app sees).

## Install

Add the `Groundhog.Aspire.Hosting` package to the AppHost project from nuget.org:

```
dotnet add package Groundhog.Aspire.Hosting --version 0.2.0-preview.2
```

It needs Aspire 13.6.1 or later and .NET 10. The complete runnable package consumer is
[samples/AspireQuickstart](../samples/AspireQuickstart/README.md). Earlier previews were built before this
public repository existed; releases from this repository include public source links. The NuGet README
contains the essential setup.

Before connecting, check `GET /_groundhog/v1/health` on the relay's own address. A compatible response includes
`"status":"ok"` and protocol `1`; another protocol version requires a compatible relay or client.
The sample includes `check-relay.ps1` for this optional preflight. The agent still handles a refused protocol
with a permanent failure; the integration does not add a startup dependency on the health endpoint.

## Add a tunnel

Store `Parameters:groundhog-key` in the AppHost's **Manage User Secrets** editor, or use the Aspire secret
parameter prompt. Keep the key out of `appsettings.json`, source control and shell history.

Then, in the AppHost:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var key = builder.AddParameter("groundhog-key", secret: true);

var web = builder.AddProject<Projects.MyApp_Web>("web");

var tunnel = builder.AddGroundhogTunnel("tunnel", "https://tunnel.example.com", "myapp", key)
    .WithReference(web.GetEndpoint("http"));

web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress)
   .WithEnvironment("Webhooks__BaseUrl", ReferenceExpression.Create($"{tunnel.Resource.PublicAddress}/webhooks"));

builder.Build().Run();
```

- `AddGroundhogTunnel(name, relayAddress, tunnelName, key)` adds the tunnel. `name` is the resource's name in the
  dashboard; `relayAddress` and `tunnelName` are your relay and tunnel. Both can also be parameters, for example
  `builder.AddParameter("groundhog-relay")`, so each developer can point at their own tunnel through user secrets.
- `key` must be a secret parameter (`secret: true`); anything else is refused when the AppHost is built.
- `.WithReference(endpoint)` sets where requests go. HTTP and HTTPS endpoints work; an HTTPS endpoint needs a
  certificate this machine trusts (`dotnet dev-certs https --trust` for ASP.NET Core development certificates).
  TLS uses the endpoint hostname while your app receives the public `Host`. The tunnel starts once the endpoint has its
  port.

To sign in some other way than a connection key, pass any `GroundhogCredential` instead of the key parameter (see
"Your own credential" in [agent.md](agent.md)):

```csharp
builder.AddGroundhogTunnel("tunnel", "https://tunnel.example.com", "myapp", new MyTokenCredential(tokens));
```

## Use the public address

| Value | Example | Use it for |
| --- | --- | --- |
| `tunnel.Resource.PublicAddress` | `https://myapp.tunnel.example.com` | Public-origin settings, OAuth redirect bases, webhook URLs |
| `tunnel.Resource.PublicHost` | `myapp.tunnel.example.com` | A dev server's allowed hosts |

Pass either to `WithEnvironment`, or use it inside `ReferenceExpression.Create($"...")` to build a longer address.
`PublicAddress` is an origin: no path and no trailing slash.

For a Vite dev server, which refuses host names it doesn't know:

```csharp
var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithEnvironment("VITE_ALLOWED_HOST", tunnel.Resource.PublicHost);
```

```ts
// vite.config.ts
server: { allowedHosts: process.env.VITE_ALLOWED_HOST ? [process.env.VITE_ALLOWED_HOST] : [] }
```

### Where the value comes from

The address is the one the relay announces when the agent connects. A resource that uses it waits for the tunnel's
first connection attempt, for at most 5 seconds. If that attempt fails or takes longer, the resource gets the address
the tunnel would have in the relay's normal mode, `https://<tunnel name>.<relay host>`, and starts anyway.

In single-tunnel mode the public address is the relay's own address, so that fallback is wrong when the relay can't
be reached. Nothing works through the tunnel then anyway; restart the resource once the tunnel is running if it
needs the right value.

## It never holds up your app

Using the address doesn't make a resource wait for the tunnel to connect. If the relay is down, the key is wrong or
you're offline, the tunnel shows the problem in the dashboard and everything else starts and runs locally as usual.

If a resource really can't start without the tunnel, say so explicitly:

```csharp
webhookWorker.WaitFor(tunnel);
```

`WaitFor` waits until the tunnel is connected, and fails the waiting resource if the tunnel stops for good (for
example because the relay rejected the key).

## In the dashboard

| State | Meaning |
| --- | --- |
| Starting | Waiting for the target endpoint's port, or for a parameter that has no value yet. |
| Connecting | Making the first connection to the relay. |
| Running | Connected. The public address is shown as the resource's URL. |
| Reconnecting | The connection dropped (sleep, network change, relay restart). The agent retries by itself: 1, 2, 4, 8, 16, then every 30 seconds. |
| Failed to start | The tunnel stopped without ever connecting: a bad setting, a rejected key, an unknown tunnel. The reason is in its console log. |
| Exited | The tunnel was connected but stopped for good, for example because the key was regenerated or another agent took the tunnel over. |

After a tunnel fails to start or exits, use its **Reconnect** command in the Aspire dashboard to start a new agent
without restarting the AppHost. It reads the current relay, tunnel name, credential and target endpoint again.
The command is disabled while the tunnel is active, including automatic reconnection. Duplicate commands cannot
start overlapping agents. Environment variables already resolved for other resources retain their existing values.

The tunnel's console log has one line per connection change, for example
`Connected: https://myapp.tunnel.example.com/ → http://localhost:5173/`, and the reason whenever it disconnects or
stops. Its details show the relay, the tunnel name and where requests go; the key is never shown.

### Request logging

Opt in with `.WithRequestLogging()` on the tunnel builder to add one console line per completed request:

```text
GET /api/health 200 12 ms
WS /socket 101 850 ms
```

Logging is off by default. Lines contain the method (`WS` for WebSockets), path, status and duration in milliseconds;
query strings, headers and bodies are omitted. WebSocket duration covers the full connection and is logged when it
closes. The `Connected:` line includes the public URL for opening the live app; individual lines describe completed
traffic and do not link to stored request details.

## Deploying

A tunnel is for local development. It's left out of the deployment manifest, and in publish mode the agent never
starts. A deployed app has its own public address: give it that through its own configuration rather than through
`PublicAddress`, for example by adding the tunnel only when running locally:

```csharp
if (builder.ExecutionContext.IsRunMode)
{
    var tunnel = builder.AddGroundhogTunnel("tunnel", "https://tunnel.example.com", "myapp", key)
        .WithReference(web.GetEndpoint("http"));
    web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress);
}
```
