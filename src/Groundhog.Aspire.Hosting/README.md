# Groundhog.Aspire.Hosting

A .NET Aspire integration for Groundhog, the hosted HTTPS tunnel service for local development. It runs a tunnel
agent inside your AppHost, gives one of your app's endpoints a public HTTPS address through the Groundhog service,
and lets other resources use that address.

## Install and connect

Requires .NET 10 and Aspire 13.6.1 or later. Add the package to your AppHost:

```text
dotnet add package Groundhog.Aspire.Hosting --version 1.0.0
```

Use `dotnet add package` for this integration. Aspire CLI integration discovery currently accepts the
`Aspire.Hosting.*` and `CommunityToolkit.Aspire.Hosting.*` prefixes, so `aspire add` does not discover this package.

Before connecting, [create your free workspace](https://groundhog.unifinity.com/docs/workspace) and
[create a tunnel](https://groundhog.unifinity.com/docs/create-tunnel). Keep its name and connection key ready;
use the name in place of `my-tunnel` below. The hosted service address is automatic.
Follow the [illustrated first-tunnel walkthrough](https://github.com/Unifinity-Software/groundhog-client/blob/master/samples/AspireQuickstart/README.md)
for the website steps, exact fields, and the response that confirms success.

```csharp
builder.AddProject<Projects.Web>("web").WithGroundhogTunnel("my-tunnel");
```

This adds a `web-tunnel` resource that forwards to the project's `https` endpoint (or `http` if it has none; pass
`endpointName` to choose), and a secret parameter `groundhog-my-tunnel-key`. Enter the key when the Aspire dashboard
asks for it and enable **Save to user secrets** to remember it, or set `Parameters:groundhog-my-tunnel-key` in the
AppHost's **Manage User Secrets** editor. Keep the key out of source control and shell history.

Use `configure` to pass the public address to your app:

```csharp
var web = builder.AddProject<Projects.Web>("web");
web.WithGroundhogTunnel("my-tunnel", configure: tunnel =>
    web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress));
```

On a team, each developer creates their own tunnel, for example `my-tunnel-dev1`. Without a tunnel name,
`WithGroundhogTunnel()` reads the name from the parameter `groundhog-tunnel-name` and the key from the secret parameter
`groundhog-tunnel-key`, so the AppHost code is the same for everyone:

```csharp
builder.AddProject<Projects.Web>("web").WithGroundhogTunnel();
```

Each developer sets `Parameters:groundhog-tunnel-name` and `Parameters:groundhog-tunnel-key` in the AppHost's user
secrets; the Groundhog CLI's `groundhog create my-tunnel-dev1 --project AppHost/AppHost.csproj` writes both. On that
developer's computer the `web-tunnel` resource's public address is `https://my-tunnel-dev1.groundhog.unifinity.com`.
Pass `endpointName` by name, for example `WithGroundhogTunnel(endpointName: "http")`.

When a gateway proxies to internal services, give those services the gateway's public address through service
discovery, as with Aspire dev tunnels:

```csharp
var serviceA = builder.AddProject<Projects.ServiceA>("service-a");
var apiGateway = builder.AddProject<Projects.ApiGateway>("api-gateway")
    .WithReference(serviceA)
    .WithGroundhogTunnel("my-tunnel");

serviceA.WithReference(apiGateway, apiGateway.GetGroundhogTunnel());
```

When `service-a` starts, `services__api-gateway__https__0` is set to `https://my-tunnel.groundhog.unifinity.com` (HTTPS only).

`AddGroundhogTunnel("web-tunnel", "my-tunnel", tunnelKey).WithReference(endpoint)` is the explicit form, for choosing
the resource name or key parameter yourself. `"web-tunnel"` is the resource name in the Aspire dashboard, `"my-tunnel"`
is the tunnel's name in Groundhog (its public host is `my-tunnel.groundhog.unifinity.com`), and `tunnelKey` is a secret
parameter holding the connection key copied when the tunnel was created. The tunnel name can also be a parameter.

- The key must be a secret parameter; it's handed to the agent in memory and never shown on a command line, in the
  dashboard or in a log.
- The dashboard shows the tunnel as connecting, running (with its public address), reconnecting, or failed with
  the reason.
- The tunnel has **Start**, **Stop** and **Restart** commands in the dashboard, like Aspire's executables and
  containers. They stop or start this tunnel's agent without restarting the AppHost; Start and Restart read the
  current settings again. Add `WithExplicitStart()` to the tunnel builder to keep it stopped until you click Start.
- **Test public address** sends a GET to the public address and shows the status and response time, which checks
  DNS, the relay, the agent and your app in one step.
- `PublicAddress` (for example `https://my-tunnel.groundhog.unifinity.com`) and `PublicHost` (`my-tunnel.groundhog.unifinity.com`)
  can be passed to `WithEnvironment` or used in a `ReferenceExpression`.
- Nothing waits for the tunnel unless it opts in with `WaitFor`, so an unreachable service never stops your app from
  starting locally.

## Visitor access rules

Declare who may visit the tunnel (anyone, workspace members, API keys, IP ranges, per path) in the AppHost:

```csharp
builder.AddProject<Projects.Web>("web")
    .WithGroundhogTunnel("my-tunnel", access => access
        .Allow(VisitorAllow.Members)
        .Path("/webhooks", VisitorAllow.Anyone)
        .IpRange("203.0.113.0/24")
        .ApiKey(builder.AddParameter("my-tunnel-api-key", secret: true)));
```

Rules are checked when the AppHost is built and sent on every connection. API keys are what callers send to reach a
path, separate from the tunnel's connection key; they come from secret parameters and only their SHA-256 hashes are
sent. The explicit form uses `tunnel.WithVisitorAccess(access => ...)`.

## Request logging

Add `.WithRequestLogging()` to the tunnel builder to write completed requests to its Aspire console:

```csharp
web.WithGroundhogTunnel("my-tunnel", configure: tunnel => tunnel.WithRequestLogging());
```

Lines look like `GET /api/health 200 12 ms` or `WS /socket 101 850 ms`. WebSockets are reported when they close.
Logging is off by default; the tunnel's **Log requests** and **Stop logging requests** dashboard commands switch it
while the AppHost runs. Query strings, headers and bodies are omitted. The existing `Connected:` line includes
the public tunnel URL, which opens the live app. Request lines describe completed traffic rather than linking to a
stored request.

HTTPS local endpoints need a certificate trusted by this machine. In publish mode
the tunnel is omitted, its agent does not start, and environment variables that use its `PublicAddress` or
`PublicHost` are left out of the manifest, so a deployed app gets its public address from its own configuration. C# AppHosts are supported; TypeScript bindings are not
currently advertised. The complete package-only example is in `samples/AspireQuickstart`.

See [the Aspire guide](https://github.com/Unifinity-Software/groundhog-client/blob/master/docs/aspire.md) for more options.

MIT licensed.
