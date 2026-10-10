# Relay and agent protocol

## Relay ⇄ agent protocol (the contract)

This section is precise on purpose: the relay and the agent are built in parallel against it, and must
interoperate on first contact. The protocol version is **1**. Shared constants and types live in
`Groundhog.Protocol`.

### Approach

The agent opens a WebSocket to the relay. Inside that WebSocket, the two sides speak ordinary **HTTP/2 without
TLS** ("h2c"), with the **relay acting as the HTTP client and the agent as the HTTP server**. In other words,
the WebSocket is just a pipe, and each visitor request becomes an HTTP/2 request sent by the relay through the
pipe. HTTP/2 lets many requests share one pipe at once, and has a standard way to carry WebSockets ("extended
CONNECT", RFC 8441), so we don't invent a framing format. The outer WebSocket is already encrypted by HTTPS
between the agent and the relay.

On the relay, ASP.NET Core with YARP forwards each visitor request using an `HttpClient` whose
`SocketsHttpHandler.ConnectCallback` returns a stream over one of the tunnel's open WebSockets. On the agent,
Kestrel accepts HTTP/2 over that stream (a custom connection listener), and YARP forwards each request to the
local target (downgrading to HTTP/1.1 where the local server needs it).

### Connecting

- The agent connects to `wss://<relay address>/_groundhog/v1/connect?tunnel=<name>`, where the relay address is
  the tunnel domain in normal mode, or the relay's own address in single-tunnel mode.
- Request headers:
  - `Authorization: <scheme> <token>`: a generic credential, so new authentication methods never change the
    protocol. Version 1 relays must support scheme `GroundhogKey`, whose token is the tunnel's connection key
    (a 43-character base64url string of 32 random bytes). Later schemes (for example `Bearer` for user or
    workload tokens) are added as relay handlers and agent credentials, not protocol changes.
  - `Sec-WebSocket-Protocol: groundhog.v1`
  - `X-Groundhog-Agent: <agent version>`
  - `X-Groundhog-Session: <GUID>`: one value per agent run; all of a run's connections share it.
- On success the relay answers `101 Switching Protocols` with `Sec-WebSocket-Protocol: groundhog.v1` and
  `X-Groundhog-Public-Address: <the tunnel's public address>`, for example
  `https://myapp.tunnel.example.com` (or the relay's own address in single-tunnel mode), so the agent can show
  it. An agent that doesn't get this header assumes single-tunnel mode and shows the relay's own address.
- Before upgrading, the relay can refuse with an HTTP status:
  - `401` credential missing, invalid, or of an unsupported scheme (`WWW-Authenticate` lists the accepted
    schemes)
  - `403` valid credential, but not allowed to host this tunnel (for example a key for a different tunnel, or
    a user outside the tunnel's workspace)
  - `404` unknown tunnel
  - `426` unsupported protocol version (the body names the supported versions)
  - `429` too many connection attempts (`Retry-After` header in seconds). Refusals with `401`, `403` and
    `404` count as failed attempts.
  - `400` a malformed request: not a WebSocket upgrade, or `X-Groundhog-Session` missing or not a GUID
- The agent treats `401`, `403`, `404` and `426` as permanent: stop and report. (A credential with an expiry
  is asked for a fresh token once before giving up on a `401`.) It treats `429`, `5xx`, any other status
  (such as `400`) and network errors as temporary: back off and retry. After a `429` it waits at least `Retry-After` seconds (at most
  5 minutes), even when the backoff schedule's wait is shorter.

### Connection pool

- The agent keeps **2 WebSockets** open per tunnel (configurable 1–8). Each carries one HTTP/2 connection.
- The relay spreads new requests across the open connections. If a new `X-Groundhog-Session` connects for a
  tunnel that already has a session, the **new session wins**: the relay closes the old session's sockets with
  close code `4409`. That makes "restart the agent" always work, even if the old process is stuck. The relay
  remembers replaced sessions: if the old session connects again (say, after a network blip), the relay accepts
  and immediately closes that connection with `4409`, so an old agent can't take the tunnel back.
- One session may have at most 8 connections open; the relay accepts and immediately closes a ninth with `4429`.
- If no connection is open, visitor requests wait up to 5 seconds for one before getting the HTTP 502 "not
  connected" page. This rides over a quick reconnect without errors.

### Keeping connections alive

- Both sides send WebSocket pings every 15 seconds. A side that hears nothing for 45 seconds closes the socket,
  and the agent reconnects. (Cloud front ends drop idle connections after a few minutes; this keeps them busy.)
- HTTP/2 settings: the agent allows at least 100 simultaneous streams per connection.
- Neither side closes an idle HTTP/2 connection; liveness comes only from the WebSocket pings. The relay opens the
  HTTP/2 connection (sends the client preface) only when it has a first request for that WebSocket, which may be
  long after connecting, so the agent's HTTP/2 server must wait for it indefinitely. With Kestrel, set
  `Limits.KeepAliveTimeout` to a long finite value such as a day: `Timeout.InfiniteTimeSpan` closed a connection
  that was still waiting for its first request within about a second in testing.
- The relay may close a WebSocket while the agent's HTTP/2 server is still writing (for example its `GOAWAY`).
  The agent should treat a failed write as the connection ending, not as an error that blocks shutdown.

### Requests sent through the tunnel (relay → agent)

- Method, path and query exactly as the visitor sent them.
- `Host`: the visitor's original host. The HTTP/2 `:authority` carries the same value.
- `Origin`, cookies and all other end-to-end headers: unchanged.
- Hop-by-hop headers (`Connection`, `Keep-Alive`, `Transfer-Encoding`, `Upgrade`, `Proxy-*`, `TE`) are removed,
  as any proxy does.
- Added: `X-Forwarded-Proto: https`, `X-Forwarded-Host: <host>`, `X-Forwarded-For` (appended),
  `X-Groundhog-Request-Id: <32 hex characters>`.
- Visitor WebSocket upgrades are sent as HTTP/2 extended CONNECT with `:protocol = websocket`; the agent turns
  them back into an HTTP/1.1 upgrade to the local app.

### Responses (agent → relay)

- Passed back to the visitor unchanged, streamed.
- If the local app can't be reached (nothing listening on the port), the agent answers `502` with header
  `X-Groundhog-Error: local-unreachable`. The relay shows "the tunnel is connected, but nothing is answering on
  the developer's machine", so it's distinguishable from "agent not connected".
- If the local app doesn't answer within the response timeout, the relay returns `504` with
  `X-Groundhog-Error: timeout`.

### Closing

WebSocket close codes the relay uses:

- `1000`: normal close, for example the relay is restarting. The agent reconnects with backoff.
- `4401`: the credential is no longer valid (key revoked or rotated, or membership removed). The agent stops
  and reports it.
- `4404`: the tunnel was deleted or renamed. The agent stops and reports it.
- `4409`: a newer agent session replaced this one. The agent stops quietly.
- `4429`: too many connections. The agent reconnects with backoff.

### Health

`GET /_groundhog/v1/health` on the relay's own address returns `200 OK` with
`{"status":"ok","version":"<relay version>","protocol":[1]}`. The `/_groundhog/` path prefix is reserved on
the relay's own address and never forwarded.
