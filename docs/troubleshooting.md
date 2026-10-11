# Troubleshoot a Groundhog client

Start with the local URL, then the client connection, then the public response. That order separates an application
problem from a relay or credential problem. Record the client version with `groundhog --version` or the AppHost's
package reference; include sanitized error text when asking for help.

## Connection symptoms

| Symptom | Check | Recovery |
| --- | --- | --- |
| CLI exits 2 | Arguments, key format, local target | Use `groundhog host --help`; provide one of `--port` and `--target` |
| CLI exits 3 / credential rejected | Correct relay, exact name, key for that tunnel | Update `GROUNDHOG_KEY` or Aspire secret and restart |
| Unknown tunnel after rename/delete | Name on website | Update the name; recreate only if the tunnel was intentionally deleted |
| Reconnecting after sleep/network change | Network and relay reachability | Let temporary retries run; starts at 1 s and backs off to 30 s with jitter |
| Earlier client stops when another starts | Same tunnel hosted twice | Use distinct names or stop the duplicate client |
| Connected but public request fails | Local app listener, Host allowance, certificate | Fix local origin, allowed public host or trust configuration |
| Relay reports `local-unreachable` | App started and correct port | Open the local URL directly, then retry public URL |
| Relay reports `timeout` | App returns response headers promptly | Investigate app delays; relay's default header wait is 100 seconds |
| Incompatible protocol | Relay health advertises protocol 1 | Install compatible client/relay versions; repeated retries cannot fix it |

A client automatically retries temporary failures. Permanent credential, unknown-tunnel and protocol failures stop
it. CLI Ctrl+C and replacement by a newer agent exit 0; protocol incompatibility exits 1; credential and tunnel
refusals exit 3. See [the full Agent behavior](agent.md#reconnecting).

## Recover Aspire without restarting everything

When a tunnel is permanently stopped, use its **Reconnect** command after correcting its parameters. The command
reads the current name, relay, credential and endpoint. It is disabled while the client is connected or automatically
retrying; it cannot start a second overlapping agent. Other resources' environment variables already resolved from
`PublicAddress` or `PublicHost` retain their values. Restart an affected resource if those values changed.

For a rotated key, update `Parameters:groundhog-key` through Manage User Secrets or your secret configuration.
For a parameterized renamed tunnel, update `Parameters:groundhog-name` too. A sample asking for three parameters
is older source; installing a newer package does not rewrite `Program.cs`. Download the current sample into a fresh
folder and follow its two-parameter walkthrough.

## HTTPS and public hosts

For local HTTPS use `--target https://localhost:7132` with the actual origin. Trust the development certificate or
provide its public CA/self-signed certificate through `--local-ca`. Name, expiry and purpose checks remain active.
These custom certificate controls apply to loopback HTTPS only and never weaken the relay's TLS validation.
Restart after replacing the certificate file: it is read when the Agent is constructed.

The local hostname is used for TLS certificate matching; HTTP Host stays public. Permit the public hostname in your
app's host validation. Configure trusted forwarding headers so generated redirects use public HTTPS.
For Vite HMR, inspect the browser's WebSocket URL if the page works but live reload fails. The browser must connect
to the public host with `wss` and port 443; the local dev server still listens on its own port.

## Package restore and upgrades

These guides use `0.3.0-preview.3`, published for CLI, Agent, Protocol and Aspire. For a stale feed response, run:

```text
dotnet restore --source https://api.nuget.org/v3/index.json --no-http-cache
```

Run in the relevant project/sample directory and verify the selected version in its package reference or central
versions file. For the tool, use `dotnet tool update --global Groundhog.Cli --version 0.3.0-preview.3`. Restart the
client after updating and verify the public response again. Dashboard last-seen metadata is not proof of a current
running version, and cached update hints may lag the feed.

## Sharing diagnostics

Include the package/tool version, whether you use CLI/Aspire/Agent, HTTP or HTTPS locally, client state, sanitized
error and which verification step failed. Include a request ID when useful. Remove keys, invitation tokens, private
URLs, account identifiers and captured request contents. Verbose CLI and Aspire request logging omit query strings;
your app's own logs may still contain private data.
