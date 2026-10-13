# Groundhog.Cli

The `groundhog` command of Groundhog, the hosted HTTPS tunnel service for local development: give a dev server on
your machine a public HTTPS address through the Groundhog service.

```
dotnet tool install --global Groundhog.Cli --version 0.4.0-preview.3
groundhog host myapp --port 5173
```

```
groundhog host <name> (--port <port> | --target <url>) [options]
```

Run `groundhog` (or `groundhog dashboard`) for a full-screen terminal app: account, tunnels and local agents as
dashboards, tunnel actions, sign-in, diagnostics and a live hosting view. Arrow keys and Enter navigate, Esc goes back
and Q quits; your terminal is restored on exit.
Use `login`, `workspace list`, `list` and `describe <name>` to discover your remote tunnels.
Local `ps`, `status`, `stats`, `requests`, `logs`, `stop`, `doctor` and `wait` work independently of account login.
A saved profile remembers a tunnel name and your app's local address (never a key): save one with `init` or
`profile save <name> --target <url>`, then run it with `host <name>` or in the background with `start <name>`.
Read commands support `--output json`; `--no-interactive` selects script behavior and `--no-color` disables colors.
See the [illustrated CLI guide](https://github.com/Unifinity-Software/groundhog-client/blob/master/docs/cli.md).

Use `groundhog update --check` to check for a newer CLI, `groundhog update` to confirm and install it,
or `groundhog update --yes` for an explicitly requested noninteractive update. Interactive commands also offer
updates at most once a day, with No as the default; accepting updates the tool installation you are running and
asks you to rerun your command. Stable installs follow stable releases, and preview installs also include previews;
`--prerelease` opts a stable install into previews. Updates use listed NuGet.org packages and preserve local settings.
Startup checks stay quiet offline and never run for scripts, JSON, help, version or background hosts.
Disable the periodic offer with `--no-update-check` or `GROUNDHOG_NO_UPDATE_CHECK=1`.
The .NET SDK is required to install an update. Running tunnels keep their current client until restarted.

The key comes from `--key`, `GROUNDHOG_KEY`, or a matching CLI-saved key and is never printed. In noninteractive mode the command prints
one line when connected (`Connected: https://myapp.groundhog.unifinity.com → http://localhost:5173`), one per disconnect and
reconnect, and one per request with `--verbose`. It reconnects by itself after sleep or network changes.

For HTTPS apps, use `--target https://localhost:7132`. Trusted development certificates work with normal platform
validation. For loopback HTTPS only, `--local-ca <file>` trusts a PEM/DER CA or self-signed certificate;
`--insecure-local` skips validation with a warning. Neither changes relay certificate validation.

Exit codes: 0 stopped with Ctrl+C (or replaced by a newer agent for the same tunnel), 1 other failure, 2 bad
arguments, 3 key rejected or unknown tunnel.

Requires .NET 10 or later. See `docs/agent.md` in the source repository for the full guide.

The ASP.NET Core runtime is required (included with the SDK). Set `GROUNDHOG_KEY` through your shell's secure
environment configuration; avoid a literal key in command history. Create a tunnel in the Groundhog website
and use its name and connection key. The hosted service address is automatic.

For a repository-local tool, run `dotnet new tool-manifest`, then
`dotnet tool install Groundhog.Cli --version 0.4.0-preview.3` and `dotnet tool run groundhog -- --help`.
Source and the full guide are available in the public Groundhog client repository.

MIT licensed.
