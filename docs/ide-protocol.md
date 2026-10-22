# IDE backend protocol

The IDE protocol is **version 1**. `groundhog ide --stdio` is a local JSON-RPC 2.0 server shared by IDE extensions.
It reuses the CLI credential store and agent sessions. This contract is separate from the
[relay and agent protocol](protocol.md); changing one does not imply a change to the other.

## Transport and lifetime

Each message is UTF-8 JSON preceded by ASCII `Content-Length: <byte count>\r\n\r\n`. The count measures JSON bytes,
not characters. There is no trailing delimiter. Readers must handle partial reads and multiple frames in one read.
Header names are case insensitive. An optional `Content-Type: application/vscode-jsonrpc; charset=utf-8` is accepted.
Messages are limited to 1 MiB and headers to 8 KiB; an invalid length, oversized frame, unsupported encoding or
truncated frame terminates the connection without echoing its contents. A valid frame containing malformed JSON
returns `-32700` with `id: null`. JSON-RPC batches are unsupported (`-32600`).

Requests have `jsonrpc: "2.0"`, a string or integer `id`, a `method` and an object `params` (omitted means `{}`).
Responses contain the same `id` and exactly one of `result` and `error`. Notifications omit `id` and never receive
responses. IDs are unique among outstanding requests. Responses may arrive out of order; output frames must never
interleave. Unknown notifications are ignored. Unknown requests return `-32601`. Clients ignore unknown object
properties and capability names. All property names are camelCase, times are UTC ISO 8601 strings, durations are
milliseconds, and absent optional values are omitted or null. Empty collections are `[]`.

Stdout contains protocol frames only. Diagnostics go to stderr and must exclude credentials and frame contents.
EOF cancels sign-in polling, subscriptions and backend-owned work and exits cleanly. Background agents remain running.
The client may cancel an outstanding request with `$/cancelRequest` and `{ "id": 1 }`; cancellation returns `-32800`
when the operation has not already completed. Cancellation is best effort and does not undo a remote mutation.
Extensions use a finite request timeout, cancel on timeout and dispose the child process on deactivation.

## Initialization and capabilities

`initialize` must be the first request and may run once. Other requests before it return `-32000` (`notInitialized`).
The backend selects the highest common protocol version. No common version returns `-32001` (`unsupportedVersion`)
with `data.supportedVersions`. A breaking shape or semantic change requires another protocol version; additive
properties and optional methods use capabilities.

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientVersion":"0.1.0","protocolVersions":[1]}}
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"cliVersion":"1.0.0","capabilities":["deviceLogin","localAgents","liveRequests"]}}
```

`capabilities` contains only working features. `deviceLogin`, `localAgents` and `liveRequests` describe baseline
backend support. `refreshToken` and `workspaceEvents` additionally require relay support discovered through
`GET /api/service-info`; a missing endpoint means neither capability is available. Without `workspaceEvents`,
refresh remote workspace/tunnel state every 30 seconds while initialized and signed in, and after mutations.
Without `refreshToken`, expired credentials require sign-in again. Optional phase 2/3 capability names are
`requestDetail`, `requestReplay`, `requestExportHttp`, `captureBodies`, `rotateWithoutDowntime` and `visitorAccess`.
Reserved methods return `-32014` until their capability is advertised; clients must not infer support from CLI version.

## Shared objects and security

A workspace is `{ "slug": "example", "name": "Example", "role": "owner" }`.
A tunnel is `{ "workspace": "example", "name": "my-tunnel", "url": "https://my-tunnel.groundhog.unifinity.com",
"state": "connected", "actions": ["rename", "delete"], "hasKey": true }`.
Relay state/action strings are passed through; clients handle unknown values without offering unknown actions.
An agent is `{ "name": "my-tunnel", "target": "http://localhost:5173", "url": "https://my-tunnel.groundhog.unifinity.com",
"state": "Connected", "source": "cli", "processId": 1234 }`. `source` is `cli` or `aspire`; agent states follow
`GroundhogAgentState` (`Connecting`, `Connected`, `Reconnecting`, `Disconnected`, `Stopped`, `Failed`).
Agent objects omit URL until known. A request summary is `{ "requestId": "request-1", "completed":
"2026-10-22T12:00:00Z", "method": "GET", "path": "/webhooks", "statusCode": 200, "durationMs": 12,
"source": "local" }`; `source` is `local` or `remote`. IDs are opaque and may be synthesized for local metadata.

Connection keys, visitor API keys, access tokens, refresh tokens and device codes never appear in responses,
notifications, errors, logs or exported settings. `keys/set` accepts a connection key and never returns it.
Tunnel creation and rotation save returned keys inside the backend and project only safe tunnel metadata.
`hasKey` reveals presence only. Authentication status excludes the stored session object. Device *user* codes
and the hosted verification URL are intended for display; the private device code stays inside the backend.
Never serialize relay responses wholesale. Error messages are fixed, safe messages; never echo server error bodies.

Stdio is an OS-user-local trust boundary, not a network endpoint. Extensions request workspace trust before starting
agents or accessing project targets. They confirm destructive actions before sending rename/delete requests; the
backend additionally requires `confirm: true`. Targets must be HTTP(S), without user info, query or fragment.
No connection key is read from project files. Browser links must be HTTPS URLs at the hosted service origin, or a
validated hosted tunnel URL. Local session pipes authorize only the current OS user.

## Phase 1 requests

The following examples show the JSON payloads without their Content-Length headers. Each pair is an independent
exchange after initialization. `workspace` is a slug and is explicit on tunnel mutations, key writes and subscriptions;
`workspaces/use` changes the shared CLI selection, never the meaning of an already-issued request.

### Authentication

`auth/status` reads the shared credential store without contacting the relay. State is `signedOut`, `signedIn` or
`expired`. Signed-in/expired status may include `workspace` and `expiresAt`, never credentials.

```json
{"jsonrpc":"2.0","id":2,"method":"auth/status","params":{}}
{"jsonrpc":"2.0","id":2,"result":{"state":"signedIn","workspace":"example","expiresAt":"2026-10-22T20:00:00Z"}}
```

`auth/login` starts hosted device authorization and immediately returns the public challenge. Polling continues in
the backend. A later `auth/changed` reports completion or expiry. A second login cancels the previous pending login.
The extension opens the verification URL only on a user gesture. Closing the backend cancels the pending login.

```json
{"jsonrpc":"2.0","id":3,"method":"auth/login","params":{}}
{"jsonrpc":"2.0","id":3,"result":{"userCode":"ABCD-EFGH","verificationUri":"https://groundhog.unifinity.com/cli-login","expiresAt":"2026-10-22T12:10:00Z"}}
```

`auth/logout` cancels pending login, revokes the remote CLI session when reachable and always deletes local credentials
and saved tunnel keys. It cancels authenticated subscriptions and emits `auth/changed`, even if remote revocation fails.

```json
{"jsonrpc":"2.0","id":4,"method":"auth/logout","params":{}}
{"jsonrpc":"2.0","id":4,"result":{}}
```

### Workspaces and tunnels

`workspaces/list` calls `/api/me` and returns accessible workspaces plus the selected slug, which can be null.

```json
{"jsonrpc":"2.0","id":5,"method":"workspaces/list","params":{}}
{"jsonrpc":"2.0","id":5,"result":{"workspaces":[{"slug":"example","name":"Example","role":"owner"}],"selected":"example"}}
{"jsonrpc":"2.0","id":6,"method":"workspaces/use","params":{"workspace":"example"}}
{"jsonrpc":"2.0","id":6,"result":{"workspace":"example"}}
```

`workspaces/use` verifies membership using `/api/me` before persisting the selection, then emits `auth/changed`.
`tunnels/list` returns safe tunnel objects and the responsive local agent objects, including Aspire agents.

```json
{"jsonrpc":"2.0","id":7,"method":"tunnels/list","params":{"workspace":"example"}}
{"jsonrpc":"2.0","id":7,"result":{"tunnels":[{"workspace":"example","name":"my-tunnel","url":"https://my-tunnel.groundhog.unifinity.com","state":"connected","actions":["rename","delete"],"hasKey":true}],"agents":[{"name":"my-tunnel","target":"http://localhost:5173","url":"https://my-tunnel.groundhog.unifinity.com","state":"Connected","source":"cli","processId":1234}]}}
{"jsonrpc":"2.0","id":8,"method":"tunnels/create","params":{"workspace":"example","name":"my-tunnel"}}
{"jsonrpc":"2.0","id":8,"result":{"workspace":"example","name":"my-tunnel","url":"https://my-tunnel.groundhog.unifinity.com","state":"disconnected","actions":["rename","delete"],"hasKey":true}}
{"jsonrpc":"2.0","id":9,"method":"tunnels/rename","params":{"workspace":"example","name":"my-tunnel","newName":"my-tunnel-renamed","confirm":true}}
{"jsonrpc":"2.0","id":9,"result":{"workspace":"example","name":"my-tunnel-renamed","url":"https://my-tunnel-renamed.groundhog.unifinity.com","state":"disconnected","actions":["rename","delete"],"hasKey":true}}
{"jsonrpc":"2.0","id":10,"method":"tunnels/delete","params":{"workspace":"example","name":"my-tunnel","confirm":true}}
{"jsonrpc":"2.0","id":10,"result":{}}
```

Create saves its key before returning. Rename moves the saved key to the new workspace/name entry. Delete removes
that entry after remote success. Both require UI confirmation because they can disconnect agents. These methods
emit `tunnels/changed` after success; clients refetch state rather than treating notifications as an authoritative cache.

### Agents and keys

`agents/start` validates the target, saves a hosting profile and starts a background CLI agent using the securely saved
key for the requested workspace/tunnel. It succeeds when connected; already-running agents return their current
agent object when the target agrees, and fail with `conflict` otherwise. Closing the backend does not stop the agent.

```json
{"jsonrpc":"2.0","id":11,"method":"agents/start","params":{"workspace":"example","name":"my-tunnel","target":"http://localhost:5173"}}
{"jsonrpc":"2.0","id":11,"result":{"name":"my-tunnel","target":"http://localhost:5173","url":"https://my-tunnel.groundhog.unifinity.com","state":"Connected","source":"cli","processId":1234}}
{"jsonrpc":"2.0","id":12,"method":"agents/stop","params":{"workspace":"example","name":"my-tunnel"}}
{"jsonrpc":"2.0","id":12,"result":{}}
{"jsonrpc":"2.0","id":13,"method":"agents/restart","params":{"workspace":"example","name":"my-tunnel","target":"http://localhost:5173"}}
{"jsonrpc":"2.0","id":13,"result":{"name":"my-tunnel","target":"http://localhost:5173","url":"https://my-tunnel.groundhog.unifinity.com","state":"Connected","source":"cli","processId":1234}}
{"jsonrpc":"2.0","id":14,"method":"keys/set","params":{"workspace":"example","name":"my-tunnel","key":"<connection-key>"}}
{"jsonrpc":"2.0","id":14,"result":{}}
```

Stop is idempotent. Start/restart fail safely if multiple responsive sessions match. Aspire sessions are observable,
but cannot be started/restarted/stopped through this protocol; the extension directs the user to the AppHost.
`keys/set` validates the connection-key format and workspace membership before saving it; it never probes the key
against a different relay. It emits `tunnels/changed`. Agent mutations emit `agents/changed`.

### Request subscriptions and doctor

`requests/subscribe` starts one tunnel subscription and returns initial bounded metadata (up to 1,000 entries).
`source` is `auto` (default), `local` or `remote`; auto chooses a responsive local pipe, then the signed-in relay
request stream. The backend sends future metadata through `requests/event`, and transport changes through
`connection/event`. Request IDs deduplicate remote reconnect backfill; local IDs remain stable within a subscription.
Re-subscribing creates an independent subscription. The backend bounds active subscriptions to 16 and retained
metadata to 1,000 per subscription. Slow consumers must not grow an unbounded queue; terminate their subscription
with a `connection/event` state `stopped` and safe reason, so the client can resubscribe and backfill.

```json
{"jsonrpc":"2.0","id":15,"method":"requests/subscribe","params":{"workspace":"example","name":"my-tunnel","source":"auto"}}
{"jsonrpc":"2.0","id":15,"result":{"subscriptionId":"subscription-1","requests":[{"requestId":"request-1","completed":"2026-10-22T12:00:00Z","method":"GET","path":"/webhooks","statusCode":200,"durationMs":12,"source":"local"}]}}
{"jsonrpc":"2.0","id":16,"method":"requests/unsubscribe","params":{"subscriptionId":"subscription-1"}}
{"jsonrpc":"2.0","id":16,"result":{}}
```

Unsubscribe is idempotent and stops the follower/pipe poller. No events for that subscription are sent after the
unsubscribe response. Request summaries contain metadata only; bodies/headers belong to phase 2.

`doctor/run` performs bounded health/target checks and returns structured results without console output.

```json
{"jsonrpc":"2.0","id":17,"method":"doctor/run","params":{"workspace":"example","name":"my-tunnel","target":"http://localhost:5173"}}
{"jsonrpc":"2.0","id":17,"result":{"checks":[{"code":"targetReachable","status":"passed","message":"Local target responded."},{"code":"connectionKey","status":"passed","message":"A connection key is saved."}]}}
```

Check statuses are `passed`, `warning` or `failed`; codes include `relayReachable`, `targetReachable`,
`localCertificate`, `connectionKey` and `protocolVersion`. Unknown codes are displayable. Diagnostic text excludes
URLs containing credentials, response bodies and key values. A failing check is a successful RPC with failed results;
invalid parameters or an unavailable diagnostic operation use RPC errors.

## Notifications

These are backend-to-client only. `auth/changed` carries the `auth/status` result. `tunnels/changed` is an invalidation
for the given workspace. `agents/changed` carries a full responsive local agent list. Request and connection events
are scoped to a subscription. Notifications may follow any successful request and also report externally made changes.

```json
{"jsonrpc":"2.0","method":"auth/changed","params":{"state":"signedIn","workspace":"example","expiresAt":"2026-10-22T20:00:00Z"}}
{"jsonrpc":"2.0","method":"tunnels/changed","params":{"workspace":"example"}}
{"jsonrpc":"2.0","method":"agents/changed","params":{"agents":[{"name":"my-tunnel","target":"http://localhost:5173","url":"https://my-tunnel.groundhog.unifinity.com","state":"Connected","source":"aspire","processId":1234}]}}
{"jsonrpc":"2.0","method":"requests/event","params":{"subscriptionId":"subscription-1","request":{"requestId":"request-2","completed":"2026-10-22T12:00:01Z","method":"POST","path":"/webhooks","statusCode":202,"durationMs":8,"source":"remote"}}}
{"jsonrpc":"2.0","method":"connection/event","params":{"subscriptionId":"subscription-1","state":"reconnecting","reason":"Relay unreachable."}}
```

Connection states are `live`, `reconnecting` and `stopped`; `reason` is optional safe text. Local agent discovery
refreshes every two seconds while needed; use injected time in tests. Workspace event streams replace the 30-second
remote poll when supported and trigger the same invalidations. Polls/streams stop on EOF/logout. No busy waiting.

## Errors

Standard JSON-RPC errors are `-32700` parse error, `-32600` invalid request, `-32601` method not found,
`-32602` invalid params and `-32603` internal error. Internal exceptions return fixed safe text without stack traces.
Application errors contain a stable `data.kind`:

| Code | Kind | Meaning |
| --- | --- | --- |
| -32000 | notInitialized | Initialize first, or already initialized. |
| -32001 | unsupportedVersion | No common version; `supportedVersions` lists backend versions. |
| -32010 | signedOut | No shared CLI sign-in. |
| -32011 | sessionExpired | Stored credentials expired or relay returned 401. |
| -32012 | noConnectionKey | Enter a connection key before hosting. |
| -32013 | permissionDenied | Relay returned 403, missing confirmation, or Aspire owns the agent. |
| -32014 | featureNotEnabled | Missing capability or relay API support. |
| -32015 | relayUnreachable | Network failure or unavailable relay. |
| -32016 | rateLimited | Relay returned 429; `retryAfterSeconds` is a nonnegative number. |
| -32017 | conflict | Name, target or multiple local agents conflict. |
| -32018 | notFound | Workspace, tunnel, request or local agent does not exist. |
| -32800 | cancelled | Request cancelled before completion. |

```json
{"jsonrpc":"2.0","id":11,"error":{"code":-32012,"message":"No connection key is saved for this tunnel.","data":{"kind":"noConnectionKey"}}}
{"jsonrpc":"2.0","id":7,"error":{"code":-32016,"message":"Rate limited. Wait before retrying.","data":{"kind":"rateLimited","retryAfterSeconds":30}}}
```

Clients respect retry-after, do not retry destructive requests automatically and show sign-in for signedOut/expired.
A 404 is `notFound` for resources; known optional endpoints returning 404 mean `featureNotEnabled`.

## Reserved methods and implementation ownership

The following methods are reserved until their corresponding capability is advertised. Their eventual request/result
shapes must be documented here alongside implementation; phase 1 clients must handle featureNotEnabled.

| Method | Capability | Owner issue |
| --- | --- | --- |
| `requests/detail` | requestDetail | #46 |
| `requests/replay` | requestReplay | #46 |
| `requests/exportHttp` | requestExportHttp | #46 |
| `captureBodies/set` | captureBodies | #46 |
| `keys/rotateWithoutDowntime` | rotateWithoutDowntime | #47 |

The contract was checked against the following existing implementation boundaries; #39 adds the stdio transport,
safe projections, device-login orchestration, subscription IDs and structured diagnostics.

| Contract area | Existing code / named follow-up |
| --- | --- |
| auth and workspace selection | `RemoteCommands.LoginAsync`, `CredentialStore`, `ManagementClient` `/api/me`; refresh lock/token reuse #41 |
| remote tunnel CRUD, secure key saving | `RemoteCommands`, `ManagementClient`, `CredentialStore` |
| local start/restart and profiles | `AgentLifecycle`, `ProfileStore`; project settings #35 |
| local discovery, stop, request metadata | `LocalSessions`, `SessionTelemetry`; public Agent-owned sessions/Aspire registration #36 |
| remote live metadata and reconnect backfill | `ManagementClient.OpenRequestStreamAsync`, `RequestFollower` |
| doctor checks | `UtilityCommands.DoctorAsync`; structured adapter #39 |
| relay service capabilities and workspace invalidation | #39; relay workspace events Unifinity-Software/groundhog#237 |
| VS Code client, mock backend and tests | #40 |
| host-neutral request inspector | #44; capture/detail/replay/export #46 |
| Visual Studio debugger/WebView2 integration | spike #38, extension #49 |
| MCP adapter and explicit action policy | #50 |
| visitor access, diagnostics and bundled backend | #51 |
