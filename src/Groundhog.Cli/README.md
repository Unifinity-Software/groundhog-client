# Groundhog.Cli

The `groundhog` command of Groundhog, the hosted HTTPS tunnel service for local development: give a dev server on
your machine a public HTTPS address through the Groundhog service.

```
dotnet tool install --global Groundhog.Cli --version 0.3.0-preview.3
groundhog host myapp --port 5173
```

```
groundhog host <name> (--port <port> | --target <url>) [options]
```

Run `groundhog` for keyboard menus, or `groundhog dashboard` explicitly. Arrow keys and Enter navigate menus.
Use `login`, `workspace list`, `list` and `describe <name>` to discover your remote tunnels.
Local `ps`, `status`, `stats`, `requests`, `logs`, `stop`, `doctor` and `wait` work independently of account login.
Use `init` or `profile save` to save a target, then `host <profile>` or `start <profile>` to run it.
Read commands support `--output json`; `--no-interactive` selects script behavior and `--no-color` disables colors.
See the [illustrated CLI guide](https://github.com/Unifinity-Software/groundhog-client/blob/master/docs/cli.md).

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
and use its name and connection key. The hosted service address is automatic. `--relay <url>` is an
optional override, for example for testing or a service-address migration.

For a repository-local tool, run `dotnet new tool-manifest`, then
`dotnet tool install Groundhog.Cli --version 0.3.0-preview.3` and `dotnet tool run groundhog -- --help`.
Source and the full guide are available in the public Groundhog client repository.

MIT licensed.
