# Groundhog for VS Code

Run hosted HTTPS tunnels beside your app. The extension runs in the workspace, including WSL, dev containers and Codespaces.

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and run `dotnet tool update --global Groundhog.Cli`. Open **Groundhog** in the activity bar to sign in. A custom executable path can be set with `groundhog.cliPath`.

## Development

Run `npm ci`, then `npm run build`. Open this folder in VS Code and press F5. Run `npm run lint`, `npm run typecheck`, `npm test`, `npm run test:smoke` and `npm run package` before shipping. Linux smoke tests need `xvfb-run -a npm run test:smoke`. The smoke test downloads and shares one VS Code installation; unit tests do not launch VS Code.

`npm run package` creates `groundhog.vsix`. Publishing requires owner-controlled Marketplace and Open VSX accounts; building a VSIX does not publish it.
