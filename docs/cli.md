# Groundhog CLI

Groundhog provides a full-screen terminal app, live tunnel dashboards and commands for scripts.
Requires .NET 10 and the ASP.NET Core runtime (both are included with the SDK).

```sh
dotnet tool install --global Groundhog.Cli --version 0.4.0-preview.2
groundhog
```

In an interactive terminal, `groundhog` (or `groundhog dashboard`) opens a full-screen app. It runs in the terminal's
alternate screen, so every view is drawn in full and replaced rather than printed below the last one, and leaving the
app restores your terminal and its scrollback exactly as they were. Terminals without an alternate screen are cleared
instead. Redirected input or output, and `--no-interactive`, print help instead of opening the app; scripts are unaffected.

![The Groundhog boot screen](images/cli-boot.png)

A short boot screen shows the version, your account and workspace and how many tunnels are connected. Any key skips it,
and it never lasts longer than a second.

### The menu

![The main menu with a preview of the highlighted entry](images/cli-menu.png)

Every screen has the same frame: a header bar with the version, where you are, your account and workspace; the body; a
status line for the outcome of the last action; and a footer listing the keys that work on that screen. The menu lists
the app's screens on the left. On terminals at least 100 columns wide, the right pane previews the highlighted entry
(hovering **Tunnels** shows the tunnel table). A preview is for looking only: open the screen with Enter to use its
keys. The number beside each entry opens it directly. Narrower terminals, down to 80×24, show the menu with a summary instead,
and dashboards stack their panels and drop less important columns.

![The menu at 80×24](images/cli-narrow.png)

| Screen | Shows |
| --- | --- |
| Home | Account and workspace, tunnels with state, address, requests and data, agents on this machine, and requests per tunnel in the last hour |
| Tunnels | The workspace's tunnels; Enter opens a tunnel, N creates one |
| Tunnel detail | Public URL, state, traffic totals, a status-class breakdown, requests per minute and the service's recent request log, with actions |
| Running agents | Agents hosting from this machine; Enter opens a live view of one, S stops it |
| Saved profiles | [Saved profiles](#saved-profiles) (a tunnel name plus your app's local address) and whether each is running; Enter hosts one here, B starts it in the background, A adds, D deletes |
| Workspaces | Your workspaces as a tree with the highlighted workspace's tunnels; Enter makes it the default |
| Diagnostics | The doctor checks as a live checklist: service health, sign-in, local agents and each saved profile's target |
| Sign in | Browser-approved sign-in with the code shown large; sign out when already signed in |
| Help | Every key, and the version |

![The home dashboard](images/cli-home.png)

### Keys

| Key | Action |
| --- | --- |
| ↑ ↓ (or j k), Home, End, PgUp, PgDn | Move the highlight |
| Enter | Open the highlighted item |
| 1–9 | Open a menu entry directly |
| Esc or Backspace | Back to the previous screen |
| R | Refresh |
| ? or F1 | Help |
| Q | Quit (Ctrl+C also quits) |

Data refreshes by itself while a dashboard is open: local agents every few seconds, the account and tunnels every 30
seconds. Text entry (a tunnel name, a local target) and confirmations appear inside the frame; while you type, letters
are text rather than shortcuts.

### Tunnels and their actions

![The tunnel list](images/cli-tunnels.png)

![A tunnel's dashboard](images/cli-tunnel-detail.png)

The tunnel dashboard lists the actions your role permits:

| Key | Action |
| --- | --- |
| O | Open the public URL in your browser (the address is shown when no browser is available) |
| C | Copy the public URL through your terminal's clipboard support (OSC 52); the address is shown either way |
| H | Host the tunnel from this machine: enter the local target, then the live view opens |
| N | Rename (asks for the new name) |
| K | Rotate the connection key and save the new one on this machine |
| X | Revoke every key |
| D | Delete |

Rotate, revoke and delete ask for confirmation inside the frame; Y confirms and N or Esc cancels. Each action runs the
same code as its command (`open`, `rename`, `key rotate`, `key revoke`, `delete`), so permissions, saved keys and
messages are identical.

![Deleting a tunnel asks first](images/cli-confirm.png)

The screenshots are the real renderer's output with synthetic sample data. See
[screenshot generation](#refresh-the-screenshots) to reproduce them.

## Check for updates and update the CLI

Interactive commands check NuGet for a newer CLI at most once every 24 hours. When one is available, Groundhog
offers **Update now before continuing?**, with **No** as the default. Declining continues your command. Accepting
runs the .NET tool installer for the installation you are using, then exits so you can run your command again
with the new version. Running tunnels keep their current client until you restart them.

```sh
groundhog update --check     # Check now without installing or prompting.
groundhog update             # Check now and ask before installing.
groundhog update --yes       # Check and install without a prompt.
```

Stable installations are offered stable releases. Preview installations include previews, and stable users can
opt in with `groundhog update --prerelease`. Only newer, listed Groundhog.Cli packages from NuGet.org are offered.
Updates use the exact selected version and preserve your profiles and credentials.

Global tools, custom tool paths, and local tools with a matching manifest are supported. An unidentified executable
is never used to guess another installation to update. You need the .NET SDK for the installer. If installation
fails, Groundhog reports the failure; retry with `dotnet tool update` for your installation after exiting the CLI.

Startup checks have a 2-second budget and stay quiet when offline. A declined offer or failed check is cached for
24 hours; explicit `update` commands check again immediately. Redirected commands, JSON output, help, version,
background hosting and `--no-interactive` skip startup checks. Add `--no-update-check` or set
`GROUNDHOG_NO_UPDATE_CHECK=1` to disable the periodic offer. Neither option disables an explicit update command.

## Sign in and find tunnels

Remote commands sign in through the service's CLI authorization API described in [protocol.md](protocol.md#cli-management-authentication).
`host` with a connection key, [saved profiles](#saved-profiles) and local monitoring work without account login.

```sh
groundhog login
groundhog workspace list
groundhog workspace use example
groundhog list
groundhog describe myapp
```

Login displays an approval code and opens the service's sign-in page when interactive. In the app, **Sign in** shows
the code in large type with the address and waits for approval; Esc cancels.

![Signing in from the app](images/cli-sign-in.png) Sign in through the browser,
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
Keys created or rotated through this CLI are saved securely and are used when the selected workspace matches.
An explicit `--key` or `GROUNDHOG_KEY` takes precedence. A saved key is never sent to a different service address.

![Live hosting dashboard with requests, status classes and connection events](images/cli-session.png)

In a terminal, `host` replaces its line-by-line output with a live dashboard in the alternate screen: the public URL,
local target, state, totals, HTTP p95 and reconnects; a status-class breakdown; the latest requests; and connection
events. Press **1** for the overview, **2** for a full-height request list and **3** for statistics (session totals,
p50/p95, status classes and requests per minute). **Esc**, **Backspace** or **Q** stops hosting. Started from the
command line, the terminal is then restored and the command prints `Stopped.` and exits as before; started from the
app (a tunnel's **H** or a saved profile), you return to the screen you came from. Ctrl+C stops the command.
Noninteractive hosting retains the original connection/disconnection output; `--verbose` adds request lines. Use
`--no-interactive` to select that behavior in a terminal.

The host's options are `--port`, `--target`, `--local-ca`, `--insecure-local`, `--key`, `--pool` and `--verbose`.
See [the Agent guide](agent.md) for forwarding and certificate behavior.

## Saved profiles

A **saved profile** remembers two things about a tunnel you host often: its **name** and the **local address your
app listens on** (for example `myapp` → `http://localhost:5173`). With a profile saved, hosting that tunnel again is
one word on the command line, or Enter on **Saved profiles** in the app, instead of retyping the name and target.
A profile never stores a connection key.

What a profile lets you do:

- **Host in the foreground**: `groundhog host myapp` uses the profile's target, and in the app **Saved profiles** →
  Enter opens the live hosting view.
- **Run in the background**: `groundhog start myapp` (or **B** in the app) starts an agent that keeps running after you
  close the terminal; `groundhog restart myapp` restarts it and `groundhog stop myapp` stops it. `start` and `restart`
  only accept saved profiles.
- **Check it**: the app's **Diagnostics** screen checks that each saved profile's local target answers.

Where the key comes from: a profile only names the tunnel, so hosting still needs its connection key. Groundhog uses,
in order, `--key`, the `GROUNDHOG_KEY` environment variable, or the key this CLI saved when you created the tunnel or
rotated its key while signed in (for the selected workspace and the same service address). A tunnel created on the
website has no saved key here; supply it through `GROUNDHOG_KEY`, or rotate its key from the CLI to save one (agents using the old key disconnect).

Create and manage profiles:

```sh
groundhog init                                       # Asks for the tunnel name and local target, then saves them.
groundhog profile save myapp --target http://localhost:5173
groundhog profile list
groundhog host myapp                                 # Foreground, with the live view.
groundhog start myapp                                # Background; returns once connected.
groundhog ps
groundhog restart myapp
groundhog stop myapp
groundhog profile delete myapp                       # Removes only the saved name and target.
```

In the app, **Saved profiles** lists each profile with whether it is running on this machine: Enter hosts it here,
**B** starts it in the background, **A** adds one and **D** deletes one. Deleting a profile never touches the tunnel
on the service or a running agent.

A profile's name must be an existing tunnel's name. Saving checks that the target is an `http://` or `https://`
address without a path or credentials; it does not contact the service. Profiles save only the name and target, so use
foreground `host` with options for a custom CA, connection pool size or `--insecure-local`. After renaming a tunnel,
delete the old profile and save a new one under the new name.

`host <profile>` runs in the foreground. `start` and `restart` return after the agent connects. The background
process is owned by your OS user and runs until stopped, replaced, or terminated; it is not a boot-time service. A
failed or timed-out start stops its newly launched process; run `host` in the foreground to see its error.

Profiles are stored in `profiles.json`, and CLI credentials and saved keys in a separate encrypted file, under the OS
local application-data directory in `Groundhog` (`%LOCALAPPDATA%\Groundhog` on Windows,
`~/.local/share/Groundhog` on Linux). Set `GROUNDHOG_HOME` to use a separate configuration directory (also useful for
isolated automation). Windows credentials use DPAPI for the current user. Unix credential files are created with
owner-only read/write permissions. Saved connection keys remain useful after account session expiry; logout removes
them.

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
The app's **Diagnostics** screen runs the same checks, plus your sign-in, local agents and every saved profile's
target, as an animated checklist.

![Diagnostics as a checklist](images/cli-diagnostics.png)
Any HTTP response proves target reachability; it does not prove application health. `wait --condition connected`
requires a live relay connection; `ready` additionally requires a successful local HEAD response. Readiness is local
and does not test public DNS or end-to-end visitor forwarding. Diagnostics preserve normal TLS validation.

Use `--output json` for read commands, `--no-interactive` to disable the app and live views, and `--no-color` (or
`NO_COLOR`) to disable colors; the app keeps its markers (▶, ●, ○, ✓, ✗) without color. Errors go to stderr. Exit codes: 0 success/clean stop, 1 operational failure, 2 invalid arguments/local configuration,
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
printing it. Rename moves a CLI-saved key to the new name, but you must update [saved profiles](#saved-profiles) and app callback URLs.
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
directory, then set `NODE_PATH` to that directory's `node_modules`). It paints each exported frame cell by cell, like a
terminal. The captures use public sample data, not a production account.

The pixel-art groundhog on the boot and help screens is generated from the brand icon with
`node tools/groundhog-pixels.cjs`, which rewrites `src/Groundhog.Cli/Views/GroundhogPixels.cs`.
