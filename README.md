# Groundhog clients

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/svg/lockups/lockup-tagline-dark.svg">
  <img src="assets/brand/svg/lockups/lockup-tagline-light.svg" alt="Groundhog: HTTPS tunnels for local development" width="400">
</picture>

Groundhog gives local applications a public HTTPS address through the hosted Groundhog service at
[groundhog.unifinity.com](https://groundhog.unifinity.com).
This repository owns the MIT-licensed developer clients: the Aspire integration, CLI, Agent and Protocol.

## Aspire

The setup below uses **0.3.0-preview.3**.

Before running a client, [sign in to Groundhog](https://groundhog.unifinity.com) and create your free workspace.
Follow [Create your free workspace](https://groundhog.unifinity.com/docs/workspace), then
[Create a tunnel](https://groundhog.unifinity.com/docs/create-tunnel). Keep the tunnel's name and one-time connection
key private while you configure your app. Your workspace address is a dashboard path, not your tunnel name.

Requires .NET 10 and Aspire 13.6.1 or later. In your AppHost directory:

```text
dotnet add package Groundhog.Aspire.Hosting --version 0.3.0-preview.3
```

```csharp
var key = builder.AddParameter("groundhog-key", secret: true);
var tunnel = builder.AddGroundhogTunnel("tunnel", "myapp", key)
    .WithReference(web.GetEndpoint("http"));
web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress);
```

Set the key through the AppHost's secret parameter prompt or user-secrets editor. Turn on **Save to user secrets**
in the prompt if you want Aspire to remember it on this computer; user secrets are outside the repository and are not encrypted.
See the [package-only quickstart](samples/AspireQuickstart/README.md) and [Aspire guide](docs/aspire.md).
Install using `dotnet add package`: current Aspire CLI integration search filters package prefixes and
does not discover this package.

## CLI

```text
dotnet tool install --global Groundhog.Cli --version 0.3.0-preview.3
groundhog host myapp --port 5173
```

Run `groundhog` for keyboard menus, or use `login`, `list`, and `describe` to discover remote tunnels.
Live hosting, local status, request queries, statistics and background agents are documented in the [CLI guide](docs/cli.md).
Remote management signs in to the service through its CLI authorization API. Supply an existing tunnel's connection key through `GROUNDHOG_KEY`.
Use the same [workspace](https://groundhog.unifinity.com/docs/workspace) and
[tunnel setup](https://groundhog.unifinity.com/docs/create-tunnel) before starting the CLI. Aspire and the CLI connect to the hosted
service automatically.

## Documentation

Start with the [client handbook](docs/README.md) for client selection and an end-to-end verification checklist.
Use [troubleshooting](docs/troubleshooting.md) for key changes, HTTPS, reconnecting and public-host errors.

## Development

```text
dotnet build Groundhog.Client.slnx -c Release
dotnet test --solution Groundhog.Client.slnx -c Release --no-build
dotnet pack Groundhog.Client.slnx -c Release --no-build -o artifacts/packages
```

Tests use local fake relays and temporary credentials. No private checkout or production account is required.
See [contributing](CONTRIBUTING.md), [security reporting](SECURITY.md), and [releasing](docs/client-release.md).

Published by:

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/unifinity-lockup-dark.svg">
  <img src="assets/brand/unifinity-lockup-light.svg" alt="Unifinity" width="160">
</picture>
