# Groundhog in a .NET Aspire AppHost

`Groundhog.Aspire.Hosting` adds a tunnel to a .NET Aspire AppHost. The tunnel gives one of your app's endpoints a
public HTTPS address through the hosted Groundhog service, and other resources can use that address in their settings (a
public-origin setting, a webhook base address, a dev server's allowed hosts).

The agent runs inside the AppHost process through the `Groundhog.Agent` library, so there's no tool to install and no
extra process. The connection key is a secret parameter: it's handed to the agent in memory and never appears on a
command line, in the dashboard or in a log.

Create a tunnel in the Groundhog website and keep its connection key. The service address is automatic.
See the [illustrated walkthrough](../samples/AspireQuickstart/README.md) for first-time setup and validation.
See [agent.md](agent.md) for what the agent does
once it's connected (reconnecting, what your app sees).

## Install

This guide uses **0.4.0-preview.3**.

Add the `Groundhog.Aspire.Hosting` package to the AppHost project from nuget.org:

```
dotnet add package Groundhog.Aspire.Hosting --version 0.4.0-preview.3
```

It needs Aspire 13.6.1 or later and .NET 10. The complete runnable package consumer is
[samples/AspireQuickstart](../samples/AspireQuickstart/README.md). Earlier previews were built before this
public repository existed; releases from this repository include public source links. The NuGet README
contains the essential setup.

You can optionally check `GET https://groundhog.unifinity.com/_groundhog/v1/health`. A compatible response includes
`"status":"ok"` and protocol `1`; another protocol version requires a client update.
The sample includes `check-relay.ps1` for this optional preflight. The agent still handles a refused protocol
with a permanent failure; the integration does not add a startup dependency on the health endpoint.

## Add a tunnel

In the AppHost, add a tunnel to the resource you want to share:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.MyApp_Web>("web").WithGroundhogTunnel("myapp");

builder.Build().Run();
```

`WithGroundhogTunnel(tunnelName)` connects to the hosted Groundhog service. `tunnelName` is the name created on the
website, without a URL or domain. It adds:

- a tunnel resource named `<resource name>-tunnel` (`web-tunnel` above), shown in the dashboard;
- a secret parameter named `groundhog-<tunnel name>-key` (`groundhog-myapp-key` above) for the connection key.

Enter the key when the Aspire dashboard asks for the unresolved parameter, and enable **Save to user secrets** to
remember it on this computer. Or store `Parameters:groundhog-myapp-key` in the AppHost's **Manage User Secrets**
editor. Keep the key out of `appsettings.json`, source control and shell history. If you declare a secret parameter
with that name yourself (for example to add a description), it's reused.

The tunnel forwards to the resource's `https` endpoint, or to its `http` endpoint if it has no `https` one. Pass
`endpointName` to choose another, for example `WithGroundhogTunnel("myapp", endpointName: "http")`. Building the
AppHost fails with a message naming the resource if it has no such endpoint. An HTTPS endpoint needs a certificate
this machine trusts (`dotnet dev-certs https --trust` for ASP.NET Core development certificates). TLS uses the
endpoint hostname while your app receives the public `Host`. The tunnel starts once the endpoint has its port.

Use `configure` to work with the tunnel, for example to give your app its public address or to turn on request
logging:

```csharp
var web = builder.AddProject<Projects.MyApp_Web>("web");

web.WithGroundhogTunnel("myapp", configure: tunnel =>
{
    tunnel.WithRequestLogging();
    web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress)
       .WithEnvironment("Webhooks__BaseUrl", ReferenceExpression.Create($"{tunnel.Resource.PublicAddress}/webhooks"));
});
```

### Explicit form

`AddGroundhogTunnel` adds the same tunnel when you want to choose the resource name or the key parameter yourself,
or take the tunnel name from a parameter:

```csharp
var key = builder.AddParameter("groundhog-key", secret: true);
var web = builder.AddProject<Projects.MyApp_Web>("web");

var tunnel = builder.AddGroundhogTunnel("tunnel", "myapp", key)
    .WithReference(web.GetEndpoint("http"));

web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress);
```

- `AddGroundhogTunnel(name, tunnelName, key)`: `name` is the resource's name in the dashboard; `tunnelName` is the
  name created on the website. The tunnel name can also be a parameter.
- `key` must be a secret parameter (`secret: true`); anything else is refused when the AppHost is built.
- `.WithReference(endpoint)` sets where requests go. HTTP and HTTPS endpoints work, as above.

The hosted service address is defined once in `GroundhogService.DefaultRelayAddress` and shared with the CLI. A future
address change is delivered in a client update.

### One tunnel per developer

A tunnel serves one computer at a time, so on a team each developer creates their own tunnel. Read the tunnel name and
key from parameters so the AppHost code is the same for everyone:

```csharp
var tunnelName = builder.AddParameter("groundhog-tunnel-name");
var key = builder.AddParameter("groundhog-key", secret: true);
var web = builder.AddProject<Projects.MyApp_Web>("web");

var tunnel = builder.AddGroundhogTunnel("web-tunnel", tunnelName, key)
    .WithReference(web.GetEndpoint("https"));
```

Each developer stores their own values in the AppHost's user secrets, from the AppHost project folder:

```
dotnet user-secrets set "Parameters:groundhog-tunnel-name" "myapp-dev1"
dotnet user-secrets set "Parameters:groundhog-key" "<connection key>"
```

Or leave them unset and enter both when the Aspire dashboard asks, with **Save to user secrets** on. Keep both out of
committed settings files: a shared tunnel name makes every developer take the same tunnel over from each other.

## Visitor access rules

By default anyone with the address can reach a tunnel. Declare who may visit, per path, as part of the AppHost:

```csharp
builder.AddProject<Projects.MyApp_Web>("web")
    .WithGroundhogTunnel("myapp", access => access
        .Allow(VisitorAllow.Members)
        .Path("/webhooks", VisitorAllow.Anyone)
        .Path("/api", VisitorAllow.ApiKeys, VisitorAllow.Ips)
        .IpRange("203.0.113.0/24")
        .ApiKey(builder.AddParameter("myapp-api-key", secret: true)));
```

- `Allow(...)` is the default rule for paths no `Path` rule matches; without it, anyone may visit. A request passes
  when any listed method passes.
- `VisitorAllow.Anyone` (on its own), `Members` (members of the tunnel's workspace, after signing in to Groundhog in
  the browser), `ApiKeys` (one of the tunnel's API keys in the `X-Groundhog-Api-Key` header) and `Ips` (an address in
  an `IpRange`).
- `Path(prefix, ...)`: the longest matching prefix wins, on segment boundaries and ignoring case. Keep webhook and
  OAuth callback paths on `Anyone`: those senders can't sign in.
- `ApiKey(parameter)` takes a secret parameter holding an API key, for example one printed by
  `groundhog api-key create myapp <label>`, set in user secrets like the connection key. API keys are what callers
  send; the connection key is the agent's own credential. Only the API key's SHA-256 hash is sent.
- The rules are checked when the AppHost is built, with the service's own validation (limits: 32 paths, 64 IP
  ranges, 10 API keys). The tunnel's dashboard properties show them, without API keys.
- With the explicit form, use `tunnel.WithVisitorAccess(access => ...)`. The `access` overload of
  `WithGroundhogTunnel` also takes `endpointName` and `configure`.
- The agent sends the rules on every connection and they replace the tunnel's stored rules. If the tunnel's visitor
  access is managed in the Groundhog dashboard, or the service refuses the rules, the tunnel fails with the
  service's reason; a service without visitor access support fails it too, so the app is never exposed without its
  rules. Without `access`, the tunnel's stored rules are left alone.

## Use the public address

`tunnel` is the tunnel builder passed to `configure` (or returned by `AddGroundhogTunnel`).

| Value | Example | Use it for |
| --- | --- | --- |
| `tunnel.Resource.PublicAddress` | `https://myapp.groundhog.unifinity.com` | Public-origin settings, OAuth redirect bases, webhook URLs |
| `tunnel.Resource.PublicHost` | `myapp.groundhog.unifinity.com` | A dev server's allowed hosts |

Pass either to `WithEnvironment`, or use it inside `ReferenceExpression.Create($"...")` to build a longer address.
`PublicAddress` is an origin: no path and no trailing slash.

For a Vite dev server, which refuses host names it doesn't know:

```csharp
var frontend = builder.AddViteApp("frontend", "../frontend");
frontend.WithGroundhogTunnel("myapp", configure: tunnel =>
    frontend.WithEnvironment("VITE_ALLOWED_HOST", tunnel.Resource.PublicHost));
```

```ts
// vite.config.ts
server: { allowedHosts: process.env.VITE_ALLOWED_HOST ? [process.env.VITE_ALLOWED_HOST] : [] }
```

### Service discovery through the tunnel

To make other resources call a resource through its tunnel, reference both from each caller with
`WithReference(target, tunnel)`, the same shape as Aspire's dev tunnels (available from 0.4.0-preview.6):

```csharp
var key = builder.AddParameter("groundhog-key", secret: true);
var apiGateway = builder.AddProject<Projects.ApiGateway>("api-gateway");
var tunnel = builder.AddGroundhogTunnel("api-gateway-tunnel", "myapp", key)
    .WithReference(apiGateway.GetEndpoint("https"));

builder.AddProject<Projects.Worker>("worker").WithReference(apiGateway, tunnel);
```

The worker gets `services__api-gateway__https__0=https://myapp.groundhog.unifinity.com`, so `https://api-gateway`
and `https+http://api-gateway` resolve to the public address. Only an `https` entry is set: the public address is
always HTTPS. The value is resolved when the worker starts, as described below, and the worker doesn't wait for the
tunnel. The tunnel must forward to the referenced resource; another target is refused when the AppHost is built. In
publish mode nothing is set.

With `WithGroundhogTunnel`, get the tunnel it added with `GetGroundhogTunnel()`:

```csharp
var apiGateway = builder.AddProject<Projects.ApiGateway>("api-gateway").WithGroundhogTunnel("myapp");
builder.AddProject<Projects.Worker>("worker").WithReference(apiGateway, apiGateway.GetGroundhogTunnel());
```

`GetGroundhogTunnel()` fails if no tunnel, or more than one, forwards to the resource.

### Where the value comes from

The address is the one the relay announces when the agent connects. A resource that uses it waits for the tunnel's
first connection attempt, for at most 5 seconds. If that attempt fails or takes longer, the resource gets the address
the tunnel normally has, `https://<tunnel name>.<service host>` (for example `https://myapp.groundhog.unifinity.com`),
and starts anyway.

## It never holds up your app

Using the address doesn't make a resource wait for the tunnel to connect. If the relay is down, the key is wrong or
you're offline, the tunnel shows the problem in the dashboard and everything else starts and runs locally as usual.

If a resource really can't start without the tunnel, say so explicitly:

```csharp
web.WithGroundhogTunnel("myapp", configure: tunnel => webhookWorker.WaitFor(tunnel));
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
`Connected: https://myapp.groundhog.unifinity.com/ → http://localhost:5173/`, and the reason whenever it disconnects or
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
var web = builder.AddProject<Projects.MyApp_Web>("web");

if (builder.ExecutionContext.IsRunMode)
{
    web.WithGroundhogTunnel("myapp", configure: tunnel =>
        web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress));
}
```

The key parameter that `WithGroundhogTunnel` adds is also left out of the manifest.
