# Groundhog for VS Code

Run hosted HTTPS tunnels beside your app. The extension runs in the workspace, including WSL, dev containers and Codespaces.

Use VS Code 1.101 or later. Open **Groundhog** in the activity bar, sign in through the Accounts menu, and choose your workspace. Right-click a tunnel to start hosting, copy its URL, inspect traffic or edit visitor access. CLI credentials stay in the shared CLI store.

When no local target is saved, **Start hosting** asks for your app's URL, prefilled from `groundhog.json` or `launchSettings.json`. Later starts and restarts reuse the saved local target.

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and run `dotnet tool update --global Groundhog.Cli --prerelease`. This preview requires CLI 1.3.0-preview.1 or later; install it only after the matching CLI package is published. Open **Groundhog** in the activity bar to sign in. A custom executable path can be set with `groundhog.cliPath`.

Platform-specific release packages include a self-contained CLI and work without a .NET SDK. A compatible global CLI is used when it is newer; an explicit `groundhog.cliPath` always takes precedence. Bundled backends use compressed single-file publishing. Trimming is currently unsupported because the CLI and RPC dependencies use reflection; no trim warnings are suppressed.

## Debugging and requests

Put `groundhog.json` in the workspace folder with a `tunnel` and local `target`. Groundhog connects before debugging starts, reuses an existing local agent (including Aspire), and injects `GROUNDHOG_PUBLIC_URL` for .NET and Node debug configurations. Hosting continues after debugging by default; enable `groundhog.stopHostingAfterDebugging` to stop agents started by the debugger. `groundhog.debugConfigurations` limits which configurations start hosting.

The request panel streams up to 1,000 rows with method, status and path filters. Pause freezes the display while capture continues. Choose a row to inspect captured data, edit a replay, copy curl or export an untitled `.http` document. Replay sends to loopback only, requires confirmation, and is unavailable for truncated captures. Signed webhook timestamps may need updating. Body capture requires owner or tunnel-creator permission and applies to future requests.

Use the panel's QR code to open the public URL on a phone. C# calls to `WithGroundhogTunnel("my-tunnel")` gain Open and Requests CodeLens actions. Disconnect notifications are enabled by default; `groundhog.notifyOn5xx` enables the first server-error notice after five quiet minutes. Notifications are limited to one per tunnel per minute.

**Groundhog: Run diagnostics** adds actionable results to Problems on `groundhog.json`. **Edit visitor access** controls default/path rules, IP ranges and visitor API keys; a new key can be copied once or saved to .NET user secrets. The extension also contributes a read-only Groundhog MCP server to VS Code chat in trusted workspaces. Disable it with `groundhog.enableMcp`.

Groundhog's public service and help center are at [groundhog.unifinity.com](https://groundhog.unifinity.com/docs).

## Development

Run `npm ci`, then `npm run build`. Open this folder in VS Code and press F5. Run `npm run lint`, `npm run typecheck`, `npm test`, `npm run test:smoke` and `npm run package` before shipping. Linux smoke tests need `xvfb-run -a npm run test:smoke`. The smoke test downloads and shares one VS Code installation; unit tests do not launch VS Code.

`npm run package` creates `groundhog.vsix`. Publishing requires owner-controlled Marketplace and Open VSX accounts; building a VSIX does not publish it.

In an Extension Development Host, enable `groundhog.useMockBackend` for scripted `example` workspace data and live demo requests. This setting has no effect in an installed extension. The production client uses the CLI's JSON-RPC stdio backend, negotiates protocol version 1, cancels timed-out requests and reconnects with bounded backoff. Failed mutations are never retried automatically.
