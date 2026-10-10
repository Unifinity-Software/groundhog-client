# The Groundhog agent

The agent runs on your machine. It keeps an outbound connection open to your Groundhog relay, and when someone
requests your tunnel's public address (for example `https://myapp.tunnel.example.com`), it passes the request to
the app on your machine (for example `http://localhost:5173`) and sends the response back. Nothing on your machine
accepts incoming connections, so there's no firewall or router to configure.

It comes in two forms:

- **`groundhog`**, a command-line tool (the `Groundhog.Cli` package, a .NET tool).
- **`Groundhog.Agent`**, a library for embedding the same behaviour in your own tools.
- **`Groundhog.Aspire.Hosting`**, which runs the agent as a resource in a .NET Aspire AppHost: see
  [aspire.md](aspire.md).

You need a relay to connect to (see the functional specification) and, for each tunnel, its name and its
connection key. The key is shown once when the tunnel is created; in single-tunnel mode it's whatever key the
relay's operator configured.

## Install the command

You need the .NET 10 SDK or runtime (including the ASP.NET Core runtime, which the SDK has), or later.

Install the preview from nuget.org:

```text
dotnet tool install --global Groundhog.Cli --version 0.2.0-preview.2
groundhog --version
```

For a local tool scoped to your repository:

```text
dotnet new tool-manifest
dotnet tool install Groundhog.Cli --version 0.2.0-preview.2
dotnet tool run groundhog -- --help
```

Upgrade with `dotnet tool update --global Groundhog.Cli --version <version>`; remove with
`dotnet tool uninstall --global Groundhog.Cli`. The tool includes the agent and protocol libraries.

Check `GET /_groundhog/v1/health` on the relay's own address before connecting. It must return
`"status":"ok"` with protocol `1`. A healthy relay advertising only other protocol versions is incompatible.

## Use the command

```
groundhog host <name> (--port <port> | --target <url>) --relay <url> [options]
```

| Argument | Meaning |
| --- | --- |
| `<name>` | The tunnel's name, for example `myapp`. Lowercase letters, digits and hyphens. |
| `--port <port>` | The port your app listens on. Requests go to `http://localhost:<port>`. |
| `--target <url>` | An HTTP or HTTPS origin instead of `--port`, for example `https://localhost:7132`. No credentials, path, query or fragment. |
| `--local-ca <file>` | Trust a PEM or DER CA certificate or self-signed server certificate for loopback HTTPS only. |
| `--insecure-local` | Skip certificate validation for loopback HTTPS only; prints a warning. |
| `--relay <url>` | Your relay's address, for example `https://tunnel.example.com`. |
| `--key <key>` | The tunnel's connection key. Prefer the `GROUNDHOG_KEY` environment variable (below). |
| `--pool <n>` | How many connections to keep open to the relay, 1 to 8. Default 2. |
| `-v`, `--verbose` | Print one line per request. |

Pass the key through the `GROUNDHOG_KEY` environment variable, so it stays out of your shell history and the
process list. `--key` wins if you give both. The command never prints the key.

```bash
export GROUNDHOG_KEY='<your key>'
groundhog host myapp --port 5173 --relay https://tunnel.example.com
```

```powershell
$env:GROUNDHOG_KEY = '<your key>'
groundhog host myapp --port 5173 --relay https://tunnel.example.com
```

It prints one line when it connects, and one each time the connection drops and comes back:

```
Connected: https://myapp.tunnel.example.com → http://localhost:5173
Disconnected: Lost the connection to the relay. Reconnecting in 1 s.
Connected: https://myapp.tunnel.example.com → http://localhost:5173
```

With `--verbose` it also prints each request when it finishes: method, path (never the query string, which can
carry sign-in codes), status and duration. WebSockets show as `WS`.

```
GET /api/health 200 4 ms
WS /hub 101 93125 ms
```

Press Ctrl+C to stop. Requests still in flight get a few seconds to finish.

### HTTPS local apps

For an app that listens on HTTPS, pass its origin:

```
groundhog host myapp --target https://localhost:7132 --relay https://tunnel.example.com
```

Normal platform certificate validation applies. For ASP.NET Core, trust your development certificate with
`dotnet dev-certs https --trust`. HTTPS Aspire endpoint references use the same validation and need no extra
tunnel configuration when the certificate is trusted.

If your app uses a private CA or an untrusted self-signed certificate, supply the public certificate:

```
groundhog host myapp --target https://localhost:7132 --local-ca local-ca.pem --relay https://tunnel.example.com
```

This explicitly trusts that certificate for this local hop, without installing it in the operating system's
store. Hostname, expiry and server-purpose checks still apply; revocation checks are disabled for this explicit
local trust. PEM and DER certificates are accepted, without a private key. The certificate file is read when the
agent is created; restart the agent after changing it.

As a last resort, `--insecure-local` skips local certificate validation and prints a warning. It cannot be combined
with `--local-ca`. Both options require a loopback HTTPS target (`localhost`, `127.0.0.1`, or `[::1]`) and never
change validation of the relay's certificate. Non-loopback targets always use normal platform trust.

TLS uses the local target hostname for SNI and certificate matching, while your app still receives the public
`Host` header. The app must allow that public host. A certificate rejected by the agent returns 502;
`local-unreachable` indicates a failed connection to the local server instead.

### Reconnecting

The agent reconnects by itself after your computer sleeps, the network changes or the relay restarts. It waits
1 second, then 2, 4, 8 and 16, then 30 seconds between attempts, forever, each varied a little so many agents
don't retry in step. Once a connection has stayed up for 30 seconds the schedule starts again from 1 second.
While connected, it pings the relay every 15 seconds and treats 45 seconds of silence as a dropped connection,
which is how it notices a connection that died while the computer was asleep.

It stops instead of retrying when retrying can't help:

| What happened | What it prints | Exit code |
| --- | --- | --- |
| You pressed Ctrl+C | `Stopped.` | 0 |
| Another agent started hosting the same tunnel (the newer one wins) | `Stopped: Another agent started hosting this tunnel …` | 0 |
| The relay rejected the key, or the key was revoked or regenerated | `Stopped: The relay rejected the credential …` | 3 |
| The key is valid but belongs to another tunnel | `Stopped: The credential isn't allowed to host tunnel …` | 3 |
| The relay has no tunnel with that name, or it was deleted or renamed | `Stopped: The relay has no tunnel named …` | 3 |
| The relay speaks a different protocol version | `Stopped: The relay doesn't support protocol version 1 …` | 1 |
| Missing or invalid arguments, or a malformed key | `groundhog: …` and a pointer to `--help` | 2 |

### What your app sees

Requests arrive exactly as the visitor sent them, apart from the hop-by-hop headers any proxy removes:

- **`Host` is the public name**, for example `myapp.tunnel.example.com`, not `localhost`. Webhook senders that sign
  requests with the public address (Twilio, for example) depend on this. Some dev servers refuse unknown host
  names until you allow them: in Vite, add the name to `server.allowedHosts`.
- `X-Forwarded-Proto: https`, `X-Forwarded-Host` and `X-Forwarded-For` (the visitor's address) are added by the
  relay. Configure your framework to trust them if it builds absolute URLs, such as OAuth redirect addresses.
- WebSockets (Vite's live reload, SignalR and so on) pass through, and bodies stream in both directions, so
  uploads, downloads and server-sent events work.

If nothing is listening on the port, visitors get a page saying the tunnel is connected but nothing is answering,
and the response carries `X-Groundhog-Error: local-unreachable`.

## Use the library

Add the `Groundhog.Agent` package. Until it's on nuget.org, download `Groundhog.Agent.<version>.nupkg` and
`Groundhog.Protocol.<version>.nupkg` from the release into a folder and add that folder as a package source:

```
dotnet nuget add source ~/groundhog-packages --name groundhog-local
dotnet add package Groundhog.Agent --version 0.1.0
```

The library needs the ASP.NET Core shared framework (it serves the tunnel's HTTP/2 with Kestrel and forwards with
YARP), so it suits apps and tools running on .NET 10 or later.

```csharp
using Groundhog.Agent;

var agent = new GroundhogAgent(new GroundhogAgentOptions
{
    RelayAddress = new Uri("https://tunnel.example.com"),
    TunnelName = "myapp",
    Credential = new TunnelKeyCredential(Environment.GetEnvironmentVariable("GROUNDHOG_KEY")!),
    LocalTarget = new Uri("http://localhost:5173"),
    PoolSize = 2, // optional, 1 to 8
});

agent.StatusChanged += (_, e) =>
{
    switch (e.State)
    {
        case GroundhogAgentState.Connected:
            Console.WriteLine($"Public address: {e.PublicAddress}");
            break;
        case GroundhogAgentState.Reconnecting:
            Console.WriteLine($"{e.Reason} Retrying in {e.RetryDelay}.");
            break;
        case GroundhogAgentState.Stopped:
            Console.WriteLine($"Stopped ({e.StopReason}): {e.Reason}");
            break;
    }
};
agent.RequestCompleted += (_, r) => Console.WriteLine($"{r.Method} {r.Path} {r.StatusCode} {r.Duration}");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
GroundhogAgentStopReason reason = await agent.RunAsync(stop.Token);
```

- The constructor validates the options and throws `ArgumentException` with a message that says what's wrong
  (never repeating the key).
- `RunAsync` keeps the tunnel connected until you cancel it or the relay refuses the agent for good, and returns
  why it stopped. Cancelling returns `Cancelled` rather than throwing.
- Events are raised on the agent's threads; keep handlers quick. Exceptions from handlers are logged and ignored.
- Set `LoggerFactory` for diagnostic logs, and `TimeProvider` to drive the reconnection waits from a fake clock in
  tests.
- For HTTPS, set `LocalTarget` to the HTTPS origin. Optional `LocalCaFile` and `InsecureLocal` expose the same
  loopback-only controls as the CLI; insecure mode also emits a warning through `LoggerFactory`.

### Your own credential

The agent asks its credential for a token before every connection attempt and sends it as
`Authorization: <scheme> <token>`. `TunnelKeyCredential` (scheme `GroundhogKey`) is built in. To use another
sign-in method, derive from `GroundhogCredential`; the relay needs a handler for the same scheme.

```csharp
public sealed class MyTokenCredential(IMyTokenSource source) : GroundhogCredential
{
    public override async ValueTask<GroundhogAccessToken> GetTokenAsync(
        GroundhogTokenRequestContext context, CancellationToken cancellationToken)
    {
        // Get a fresh token instead of a cached one if the relay just refused the last one.
        var token = await source.GetAsync(context.TunnelName, forceRefresh: context.PreviousTokenRejected, cancellationToken);
        return new GroundhogAccessToken("Bearer", token.Value, token.ExpiresOn);
    }
}
```

When the relay answers `401` to a token that has an expiry, the agent asks once more with `PreviousTokenRejected`
set before it gives up. If `GetTokenAsync` throws, the attempt counts as a temporary failure and is retried on the
usual schedule.
