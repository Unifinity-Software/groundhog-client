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
  the tunnel domain, for example `groundhog.unifinity.com`.
- Request headers:
  - `Authorization: <scheme> <token>`: a generic credential, so new authentication methods never change the
    protocol. Version 1 relays must support scheme `GroundhogKey`, whose token is the tunnel's connection key
    (a 43-character base64url string of 32 random bytes). Later schemes (for example `Bearer` for user or
    workload tokens) are added as relay handlers and agent credentials, not protocol changes.
  - `Sec-WebSocket-Protocol: groundhog.v1`
  - `X-Groundhog-Agent: <agent version>`
  - Optional `X-Groundhog-Client-Package: <NuGet package ID>` and `X-Groundhog-Client-Version: <package version>`:
    the hosting package's own version (Agent, Aspire integration or CLI), read from that package's assembly.
    All pooled connections in one session report the same metadata. These informational headers do not affect
    authentication or protocol compatibility. Relays ignore missing, malformed or unsupported metadata without
    refusing the connection. Existing clients reporting only an agent version are identified only as the Agent
    library; their Aspire/CLI package version must never be inferred from it.
  - `X-Groundhog-Session: <GUID>`: one value per agent run; all of a run's connections share it.
  - Optional `X-Groundhog-Visitor-Access: <declaration>`: the tunnel's visitor access rules, declared by the
    developer. See [Visitor access declarations](#visitor-access-declarations).
- On success the relay answers `101 Switching Protocols` with `Sec-WebSocket-Protocol: groundhog.v1` and
  `X-Groundhog-Public-Address: <the tunnel's public address>`, for example
  `https://myapp.groundhog.unifinity.com`, so the agent can show it. An agent that doesn't get this header shows
  the relay's own address.
- Before upgrading, the relay can refuse with an HTTP status:
  - `401` credential missing, invalid, or of an unsupported scheme (`WWW-Authenticate` lists the accepted
    schemes)
  - `403` valid credential, but not allowed to host this tunnel (for example a key for a different tunnel, or
    a user outside the tunnel's workspace), or a refused visitor access declaration. The `text/plain` body says
    why; the agent shows its first line (at most 200 characters, control characters removed).
  - `404` unknown tunnel
  - `426` unsupported protocol version (the body names the supported versions)
  - `429` too many connection attempts (`Retry-After` header in seconds). Refusals with `401`, `403` and
    `404` count as failed attempts.
  - `400` a malformed request: not a WebSocket upgrade, or `X-Groundhog-Session` missing or not a GUID
- The agent treats `401`, `403`, `404` and `426` as permanent: stop and report. (A credential with an expiry
  is asked for a fresh token once before giving up on a `401`.) It treats `429`, `5xx`, any other status
  (such as `400`) and network errors as temporary: back off and retry. After a `429` it waits at least `Retry-After` seconds (at most
  5 minutes), even when the backoff schedule's wait is shorter.

### Visitor access declarations

An agent may declare who can visit its tunnel, as configuration in code (CLI flags, an Aspire AppHost). This is
additive: the protocol version stays 1, and an agent that declares nothing never changes the tunnel's stored
rules.

- The header value is the unpadded base64url encoding of UTF-8 JSON, at most 16,384 characters
  (`VisitorAccessDeclaration.MaxEncodedLength`):

  ```json
  {
    "default": { "allow": ["members"] },
    "rules": [
      { "path": "/webhooks", "allow": ["anyone"] },
      { "path": "/api", "allow": ["apikeys", "ips"] }
    ],
    "ipRanges": ["203.0.113.0/24", "2001:db8::/32"],
    "apiKeyHashes": ["<64 lowercase hexadecimal characters>"]
  }
  ```

- `allow` values: `anyone`, `members` (workspace members signed in through the relay), `apikeys` (one of the
  tunnel's API keys in `X-Groundhog-Api-Key`), `ips` (the visitor's address is in `ipRanges`). A request passes
  when any listed method passes. `anyone` can't be combined with other methods in one list. `apikeys` needs at
  least one API key hash and `ips` at least one IP range. Missing `default` means `{ "allow": ["anyone"] }`; missing
  lists are empty.
- The longest matching `path` prefix wins, on segment boundaries and ignoring case; `default` applies when none
  matches. Paths start with `/`, have no `?`, `#`, `%`, `\`, whitespace, control characters or `.`/`..`
  segments, aren't `/` itself (that's `default`), are at most 256 characters, and appear once, ignoring case and
  trailing or repeated `/`.
- `ipRanges` are IPv4 or IPv6 CIDR ranges as .NET's `IPNetwork.TryParse` reads them.
- Limits: 32 rules, 64 IP ranges, 10 API key hashes.
- `apiKeyHashes` are SHA-256 of each API key's UTF-8 text, as 64 lowercase hexadecimal characters. API keys are
  what callers send to reach a path; they're separate from the tunnel's connection key (the agent's credential)
  but have the same shape (43 base64url characters). The plaintext never leaves the developer's machine. Declared
  hashes replace the API keys an earlier declaration added; API keys created in the dashboard are kept.
  The relay removes `X-Groundhog-Api-Key` before forwarding a request.
- `Groundhog.Protocol` holds the model (`VisitorAccessDeclaration`), the shared validator
  (`VisitorAccessValidator`) and the encoding, so the relay, the agent and the CLI apply the same rules.
- The agent sends the same declaration on every pooled connection. A relay that applied it (validated, and
  stored if it changed) adds `X-Groundhog-Visitor-Access: applied` to its `101`. An agent that declared rules
  and gets a `101` without that header (a relay that predates visitor access) closes the WebSocket and stops
  for good, so a declared policy is never silently ignored.
- The relay refuses a declaration with `403` and a message when it's invalid or when the tunnel's visitor access
  is managed in the dashboard. A declaration doesn't need the workspace's visitor access feature.

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

## CLI management authentication

This additive HTTPS API does not change hosting protocol version 1. Older relays can host tunnels without
supporting CLI login. Management endpoints exist only on the relay's own host, never on visitor tunnel hosts.

1. `POST /api/cli/device` (anonymous, empty JSON object) returns `deviceCode` (32 random bytes in canonical base64url),
   `userCode` (12 uppercase hex characters), `verificationUri`, `expiresIn: 600` and `interval: 5`.
   The CLI opens the same-origin verification URL and displays the code. Neither code grants API access.
2. `GET /cli-login?code=<userCode>` requires browser sign-in and shows an explicit approval page with the code.
   `POST /cli-login` requires the signed-in browser's antiforgery token and a form field `code`.
   Users approve only a code matching a CLI login they initiated. Approval binds the request to their identity.
3. `POST /api/cli/token` with `{"deviceCode":"<secret>"}` returns `state: pending|approved|expired`.
   An approved response additionally carries `accessToken` and `expiresIn: 28800`; an atomic storage transaction
   consumes the grant once. The CLI polls no faster than `interval` and stops at the device expiry.
4. Send `Authorization: GroundhogCli <accessToken>` to `/api/me` and workspace APIs under `/api/w/<slug>`.
   This scheme authorizes management requests, not the tunnel hosting WebSocket. Workspace membership, suspension
   and action permissions are checked on every request. CLI credentials cannot authorize `/api/platform`.
   A valid explicit CLI credential uses no browser cookies and does not require browser antiforgery tokens.
   Invalid or expired credentials return 401; inaccessible workspaces return 404 without exposing their existence.
5. `POST /api/cli/logout` with that credential revokes it and returns 204. Expiry is absolute at eight hours;
   no refresh token is issued. Login replaces the CLI's saved local session.

Credentials and polling secrets are never placed in URLs. Storage holds only their SHA-256 hashes, expiry,
and the approved identity claims. Authorization records are global authentication infrastructure; all tunnel,
request and usage data remains workspace-partitioned. Expired authentication records are cleaned daily in
bounded passes. Device/token calls share the relay's link-lookup IP rate limiter and may return 429 with Retry-After.

The CLI uses these existing workspace endpoints (JSON is camelCase):

| Operation | Endpoint |
| --- | --- |
| Account and accessible workspaces | `GET /api/me` |
| Tunnel inventory / create | `GET /api/w/<slug>/tunnels`, `POST` with `{"name":"<name>"}` |
| Tunnel details / rename / delete | `GET`, `PATCH` with `{"name":"<new-name>"}`, `DELETE /api/w/<slug>/tunnels/<name>` |
| Rotate / revoke connection keys | `POST /api/w/<slug>/tunnels/<name>/rotate-key`, `/revoke-key` |
| Stored usage window | `GET /api/w/<slug>/tunnels/<name>/usage` |
| Persisted request metadata | `GET /api/w/<slug>/requests?tunnel=<name>&status=<class-or-code>&limit=200` |
| Live requests (server-sent events) | `GET /api/w/<slug>/tunnels/<name>/requests/stream` |
| Visitor access rules | `GET`, `PUT /api/w/<slug>/tunnels/<name>/visitor-access` |
| API keys | `POST /api/w/<slug>/tunnels/<name>/api-keys` with `{"name":"<label>"}`, `DELETE …/api-keys/<id>` |

Request search returns `{ "entries": [...], "truncated": true|false }`; entries contain completion time,
method, path, status code and duration plus relay metadata. The CLI does not retrieve request detail bodies/headers.
The live stream sends one `request` event per finished request, its data a request entry with `requestId`. The relay
ends each stream after a few minutes (and on sign-in, membership or workspace changes) and answers 429 with
`Retry-After` when streams are at capacity. The CLI honours `retry:` (1 second by default) and `Retry-After`, backs off
up to 30 seconds after failures, stops on other 4xx responses, and after every connection reads the request log to fill
the gap, reporting each `requestId` once.
Create and rotate return `{ "tunnel": {...}, "connectionKey": "<one-time-key>" }`. The CLI stores this key securely
and never prints it in normal output. Other management errors use HTTP status codes and problem details;
the CLI does not echo arbitrary response bodies.

Visitor access. `GET` works in every workspace. Changing rules or API keys is behind a per-workspace feature; without
it `PUT` and the API key endpoints answer 404, which the CLI reports as "Visitor access rules aren't enabled for this
workspace."

- `GET …/visitor-access` returns the stored policy in the declaration's shape, with `apiKeys`
  (`[{"id","name","created"}]`, never hashes or plaintext) instead of `apiKeyHashes`, plus `managedBy`:
  `agent` (the default; an agent's declaration replaces the rules) or `dashboard` (the relay refuses agent
  declarations with 403).
- `PUT …/visitor-access` with `{ "default", "rules", "ipRanges", "managedBy" }` replaces the rules. The CLI
  validates them with `VisitorAccessValidator` first (without the API key checks, since API keys live on the relay),
  sends `managedBy: "dashboard"` when setting rules, and `managedBy: "agent"` with the stored rules to hand
  control back to agents.
- `POST …/api-keys` with `{"name":"<label>"}` returns `{"id","name","created","key"}`; the plaintext `key` is
  shown once. `DELETE …/api-keys/<id>` revokes one.
