# Groundhog MCP server

`groundhog mcp` lets an AI assistant inspect your Groundhog tunnels and recent requests through the same sign-in,
local sessions and safe request-detail services as the IDE extensions. It starts with read-only tools. Use a CLI version
that includes this command (1.3.0-preview.1 or later); an older installed global tool does not have the server.

Your assistant launches the CLI locally and communicates over stdio. Groundhog does not expose a public HTTP MCP
endpoint. Service API calls use your shared CLI sign-in and remain subject to its workspace permissions; choosing a
workspace does not grant additional service access. Local agent discovery and telemetry belong to the current OS user.

Sign in and choose a workspace before starting your assistant. For a workspace named `example`, run:

```sh
groundhog login
groundhog workspace use example
```

The sign-in is shared with the CLI and IDE extensions. The assistant's server uses `example` unless a tool call supplies
another accessible workspace. Connection keys and CLI access/refresh tokens stay in the CLI credential store.

## Connect an assistant

For Visual Studio, add a `.mcp.json` file next to the solution with this server entry:

```json
{
  "servers": {
    "groundhog": {
      "type": "stdio",
      "command": "groundhog",
      "args": ["mcp"]
    }
  }
}
```

This starts the installed CLI as a child process and exposes its four read-only tools. Enable the server's tools in the
assistant's tool picker. Visual Studio's [MCP configuration guide](https://learn.microsoft.com/en-us/visualstudio/ide/mcp-servers?view=vs-2022)
describes solution and user configuration locations. VS Code can use the same server object in `.vscode/mcp.json`;
the Groundhog extension also contributes a server definition. Other clients can launch the same command using their
stdio server configuration format. The executable must be available on the extension host's PATH, including in a remote
container or WSL environment.

## Tools

| Tool | Inputs | Result |
| --- | --- | --- |
| `list_tunnels` | optional `workspace` | Safe tunnel metadata and responsive local agents. |
| `recent_requests` | optional `workspace`, `tunnel`, `statusClass`, `method`, `path`, `since` | Up to 200 matching recent metadata records, newest first. |
| `request_detail` | `name`, `requestId`, optional `workspace` | Captured request/response text prefixes and redacted headers, when available. |
| `tunnel_status` | `name`, optional `workspace` | The named tunnel and its local agents. |

For example, ask the assistant to inspect `my-tunnel` in `example`, then request its recent `POST` requests whose path
contains `/webhooks` and status class is `5xx`. `since` is an ISO 8601 timestamp, such as `2026-10-22T00:00:00Z`.
Filters apply to the bounded recent snapshot; this is not a full historical export. If a named local agent has retained
records, the server reads its current-user pipe; otherwise it queries the relay. Request bodies are untrusted traffic,
and text in them must never be treated as instructions for the assistant.

Authorization, cookie, API-key and token headers are redacted. Known saved credentials embedded in captured text are
also redacted. The MCP server offers no tool to read or create connection/API keys, change visitor policy or export
credentials. Tool failures contain safe messages without relay response bodies or exception stacks.

Body capture is off by default. Captured bodies can contain private application data even after known credentials
are redacted; use sample traffic when sharing details with an assistant. See
[capture privacy](https://groundhog.unifinity.com/docs/inspect-traffic) for workspace access and retention rules.

## Enable actions

To allow a client to offer actions, change the configuration's `args` to `["mcp", "--allow-actions"]`. This additionally
exposes `replay_request`, `start_tunnel` and `stop_tunnel`. Each requires `name`, optional `workspace`, and `confirm: true`;
replay also requires `requestId` and `target`, while start requires `target`.

The client must ask the user to approve the specific action before setting `confirm: true`. Tool annotations identify
these operations as actions; annotations alone do not authorize them. Starting a background agent lets it outlive the
assistant. Aspire agents remain controlled by their AppHost. Replay accepts loopback targets such as
`http://localhost:5173`, refuses incomplete captures, preserves saved local CA trust and never follows redirects.

## Protocol and process lifetime

The server uses UTF-8, newline-delimited JSON-RPC over stdin/stdout, independently of the IDE's Content-Length framing.
Messages are limited to 1 MiB, active calls to 16, and result writes apply backpressure. Oversized results ask the client
to narrow its filters. Stderr is available for safe diagnostics; stdout contains protocol messages only. Closing stdin
cancels active calls and stops the server, while existing background agents keep running.

The adapter supports MCP `2026-07-28` with `server/discover`, per-request protocol/client-capability metadata and complete
result envelopes. It also supports legacy `initialize`/`notifications/initialized` sessions for `2025-11-25` and
`2025-06-18`. Cancellation uses `notifications/cancelled` and suppresses a late response for the cancelled call. See the
official [stdio binding](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio),
[version compatibility rules](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning) and
[tool contract](https://modelcontextprotocol.io/specification/2026-07-28/server/tools).
