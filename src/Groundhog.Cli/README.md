# Groundhog.Cli

The `groundhog` command of Groundhog, a self-hosted HTTPS tunnel for local development: give a dev server on your
machine a public HTTPS address through your own Groundhog relay.

```
dotnet tool install --global Groundhog.Cli --version 0.2.0-preview.1
groundhog host myapp --port 5173 --relay https://tunnel.example.com
```

```
groundhog host <name> (--port <port> | --target <url>) --relay <url> [options]
```

The key comes from `--key` or the `GROUNDHOG_KEY` environment variable and is never printed. The command prints
one line when connected (`Connected: https://myapp.tunnel.example.com → http://localhost:5173`), one per disconnect and
reconnect, and one per request with `--verbose`. It reconnects by itself after sleep or network changes.

For HTTPS apps, use `--target https://localhost:7132`. Trusted development certificates work with normal platform
validation. For loopback HTTPS only, `--local-ca <file>` trusts a PEM/DER CA or self-signed certificate;
`--insecure-local` skips validation with a warning. Neither changes relay certificate validation.

Exit codes: 0 stopped with Ctrl+C (or replaced by a newer agent for the same tunnel), 1 other failure, 2 bad
arguments, 3 key rejected or unknown tunnel.

Requires .NET 10 or later. See `docs/agent.md` in the source repository for the full guide.

The ASP.NET Core runtime is required (included with the SDK). Set `GROUNDHOG_KEY` through your shell's secure
environment configuration; avoid a literal key in command history. An existing relay and tunnel credential
are required. Before connecting, check `GET /_groundhog/v1/health` on the relay's own address: its JSON must
contain `"status":"ok"` and include protocol `1`.

For a repository-local tool, run `dotnet new tool-manifest`, then
`dotnet tool install Groundhog.Cli --version 0.2.0-preview.1` and `dotnet tool run groundhog -- --help`.
Preview source and guide links may require repository access until the public client repository is established.

MIT licensed.
