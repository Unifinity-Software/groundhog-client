# Groundhog.Protocol

Shared constants and types for the relay ⇄ agent protocol of Groundhog, a self-hosted HTTPS tunnel for local
development.
The relay and the agent both reference this package, so they agree on every value.

| Type | What it holds |
| --- | --- |
| `GroundhogProtocol` | Protocol version `1`, subprotocol `groundhog.v1`, the connect and health paths |
| `GroundhogHeaders`, `GroundhogErrors` | `X-Groundhog-*` header names and `X-Groundhog-Error` values |
| `AgentAuthorization` | The `Authorization: <scheme> <token>` format and the v1 scheme `GroundhogKey` |
| `ConnectRefusal` | Statuses the relay may refuse a connection with, and which ones are permanent |
| `GroundhogCloseCodes` | WebSocket close codes, and which ones stop the agent |
| `ConnectionPool` | Pool size bounds, ping interval and silence timeout |
| `ReconnectBackoff` | The reconnect schedule (1, 2, 4, 8 … 30 s, with jitter) as a pure function |
| `TunnelName` | Tunnel-name rules and the default reserved names |
| `ConnectionKey` | Key generation, SHA-256 hashing and constant-time verification |
| `WebSocketDuplexStream` | A `WebSocket` as a duplex `Stream`, for HTTP/2 to run over |
| `HealthResponse` | The health endpoint's JSON body |

The protocol itself is described in `docs/protocol.md` in the source repository (linked from this
package's project page).

MIT licensed.
