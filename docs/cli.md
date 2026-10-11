# Groundhog CLI

Groundhog provides keyboard menus, live tunnel monitoring and commands for scripts.
Requires .NET 10 and the ASP.NET Core runtime (both are included with the SDK).

```sh
dotnet tool install --global Groundhog.Cli --version 0.4.0-preview.1
groundhog
```

In an interactive terminal, `groundhog` opens the home menu. `groundhog dashboard` opens it explicitly.
Use the arrow keys and Enter. Select **Back** to return or **Quit** to exit; Ctrl+C cancels a prompt.
The remote tunnel picker supports typing to search. Redirected input/output prints help instead of opening a menu.
The workspace menu selects your default workspace. The tunnel menu offers creation and permitted rename, delete,
and key-management actions; destructive operations still require confirmation.

![Groundhog home menu rendered by the CLI](images/cli-home.png)

The screenshots use the actual Spectre renderer and synthetic `example.com` data. See
[screenshot generation](#refresh-the-screenshots) to reproduce them.

## Sign in and find tunnels

Remote commands sign in through the service's CLI authorization API described in [protocol.md](protocol.md#cli-management-authentication).
`host` with a connection key, profiles and local monitoring work without account login.

```sh
groundhog login
groundhog workspace list
groundhog workspace use example
groundhog list
groundhog describe myapp
```

Login displays an approval code and opens the service's sign-in page when interactive. Sign in through the browser,
check that its code matches your terminal, and approve CLI access. In a headless shell, open the printed URL on
another device. The code expires after ten minutes; the resulting CLI session expires after eight hours.
Signing in replaces the saved session.

`list` includes connected and disconnected remote tunnels in the selected workspace. `ls` is an alias.
`describe` shows a tunnel's remote state, public URL, traffic totals and permitted actions; `show` is an alias.
`ps` lists processes on this machine. A remote tunnel can be connected by an agent on a different machine.

```sh
groundhog list --status connected --output json
groundhog describe myapp --workspace example --output json
groundhog whoami
groundhog logout
```

Logout revokes the saved remote credential and removes local credentials and saved connection keys.
If the relay cannot be reached, the local copy is still removed, but remote revocation cannot be confirmed;
the credential expires at its original eight-hour deadline.

## Run a tunnel

```sh
groundhog host myapp --port 5173
groundhog host myapp --target https://localhost:7132
```

Supply an existing tunnel's key through `GROUNDHOG_KEY`. Login does not retrieve existing one-time connection keys.
Keys created or rotated through this CLI are saved securely and are used when the selected workspace and relay match.
An explicit `--key` or `GROUNDHOG_KEY` takes precedence. A saved key is never sent to a different service address.

![Live hosting overview with requests and session statistics](images/cli-session.png)

Interactive hosting shows a live overview. Press **1** for overview, **2** for recent requests, **3** for statistics,
and **Esc** or **Q** to stop hosting and return. Ctrl+C stops the command. Noninteractive hosting retains the original
connection/disconnection output; `--verbose` adds request lines. Use `--no-interactive` to select that behavior in a terminal.

The host's existing options remain supported: `--pool`, `--relay`, `--target`, `--port`, `--local-ca`,
`--insecure-local`, `--key`, and `--verbose`. See [the Agent guide](agent.md) for forwarding and certificate behavior.

## Save settings and run in the background

`init` guides you through saving a profile for an existing tunnel. Profiles contain a name, target and optional
service-address override, and contain no keys. Use commands for noninteractive setup:

```sh
groundhog profile save myapp --target http://localhost:5173
groundhog profile list
groundhog host myapp
groundhog start myapp
groundhog ps
groundhog restart myapp
groundhog stop myapp
groundhog profile delete myapp
```

`host <profile>` runs in the foreground. `start` and `restart` require a saved profile and return after the agent connects.
The background process is owned by your OS user and runs until stopped, replaced, or terminated; it is not a boot-time
service. A failed or timed-out start stops its newly launched process; run `host` in the foreground to see its error.
Profiles currently save only target and service address; use foreground `host` for custom CA, pool or insecure-local options.

Profiles and credentials are stored under the OS local application-data directory in `Groundhog`.
Set `GROUNDHOG_HOME` to use a separate configuration directory (also useful for isolated automation).
Windows credentials use DPAPI for the current user. Unix credential files are created with owner-only read/write permissions.
Saved connection keys remain useful after account session expiry; logout removes them.

## Observe and query local traffic

From another terminal:

```sh
groundhog status myapp
groundhog stats myapp --watch
groundhog logs myapp --follow
groundhog requests myapp --follow
groundhog requests myapp --status 5xx --since 10m
groundhog requests myapp --method POST --path /api/ --min-duration 500 --limit 50
groundhog requests myapp --output json
```

Local sessions retain the last **1,000 completed requests** and **100 connection events** in memory. They disappear when
the host process exits. Session request and 5xx totals cover the whole run; p95 covers retained HTTP requests and excludes
WebSocket lifetimes. Requests are reported when they finish. Headers, query strings and bodies are excluded.
Paths use substring matching, durations are milliseconds, and `--since` accepts positive numbers with `s`, `m`, or `h`.
Queries return newest first, default to 50 rows, and accept a maximum limit of 1,000.

`--follow` prints newly observed events and `--watch` refreshes status/statistics in place when interactive.
JSON output is a single snapshot and cannot be combined with follow/watch. Local commands communicate with the running
agent over a named pipe restricted to the same OS user; they do not require account login or disclose credentials.

## Inspect remote traffic

```sh
groundhog stats myapp --remote
groundhog requests myapp --remote --status 5xx
groundhog requests myapp --remote --method POST --path /api/ --output json
```

Remote stats show the relay's stored usage window with status-class counts. Gaps indicate unavailable data;
the current minute may not yet be persisted. Remote requests query the persisted request log and return metadata only.
Status filtering runs on the service; other filters apply to its newest 200 matching records. Remote queries are snapshots,
and their availability and retention depend on the service configuration. They work even when no local agent is running.

## Diagnose and automate

```sh
groundhog doctor --target http://localhost:5173
groundhog doctor --output json
groundhog wait myapp --condition connected --timeout 30
groundhog wait myapp --condition ready --timeout 30
```

Doctor checks relay health/protocol compatibility and, when provided, the local target with a HEAD request.
Any HTTP response proves target reachability; it does not prove application health. `wait --condition connected`
requires a live relay connection; `ready` additionally requires a successful local HEAD response. Readiness is local
and does not test public DNS or end-to-end visitor forwarding. Diagnostics preserve normal TLS validation.

Use `--output json` for read commands, `--no-interactive` to disable menus, and `--no-color` (or `NO_COLOR`) to disable
colors. Errors go to stderr. Exit codes: 0 success/clean stop, 1 operational failure, 2 invalid arguments/local configuration,
3 a rejected hosting key or unknown hosted tunnel. Every command supports `--help`.

## Manage tunnel definitions

```sh
groundhog create myapp
groundhog rename myapp demo
groundhog key rotate demo
groundhog key revoke demo
groundhog delete demo
groundhog open demo
```

Mutations use the same workspace permissions as the website. Create and rotate save their one-time key locally without
printing it. Rename moves a CLI-saved key to the new name, but you must update saved profiles and app callback URLs.
Rename, deletion, rotation and revocation can disconnect agents; the CLI asks for confirmation. Scripts must pass `--yes`.
Revocation removes all live keys for the tunnel. Rotation saves a replacement key; restart hosting to use it.

## Refresh the screenshots

The CLI tests export renderer output when `GROUNDHOG_SCREENSHOT_DIR` is set. Generate the PNGs with the companion script:

```powershell
$env:GROUNDHOG_SCREENSHOT_DIR = "$PWD/artifacts/screenshots"
dotnet test --project tests/Groundhog.Cli.Tests/Groundhog.Cli.Tests.csproj -c Release
node tools/cli-screenshots.cjs artifacts/screenshots docs/images
```

The screenshot script requires the `playwright` Node package and its Chromium browser (install in a temporary tooling
directory, then set `NODE_PATH` to that directory's `node_modules`). The captures are renderer screenshots using public
sample data, not screenshots of a production account.
