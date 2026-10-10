# Groundhog clients

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/svg/lockups/lockup-tagline-dark.svg">
  <img src="assets/brand/svg/lockups/lockup-tagline-light.svg" alt="Groundhog: self-hosted HTTPS tunnels" width="400">
</picture>

Groundhog gives local applications a public HTTPS address through an existing Groundhog relay.
This repository owns the MIT-licensed developer clients: the Aspire integration, CLI, Agent and Protocol.
It does not deploy or host the relay.

## Aspire

Requires .NET 10 and Aspire 13.6.1 or later. In your AppHost directory:

```text
dotnet add package Groundhog.Aspire.Hosting --version 0.2.0-preview.2
```

```csharp
var key = builder.AddParameter("groundhog-key", secret: true);
var tunnel = builder.AddGroundhogTunnel("tunnel", "https://tunnel.example.com", "myapp", key)
    .WithReference(web.GetEndpoint("http"));
web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress);
```

Set the key through the AppHost's secret parameter prompt or user-secrets editor.
See the [package-only quickstart](samples/AspireQuickstart/README.md) and [Aspire guide](docs/aspire.md).
Install using `dotnet add package`: current Aspire CLI integration search filters package prefixes and
does not discover this package.

## CLI

```text
dotnet tool install --global Groundhog.Cli --version 0.2.0-preview.2
groundhog host myapp --port 5173 --relay https://tunnel.example.com
```

Supply the connection key through `GROUNDHOG_KEY`. See the [CLI guide](docs/agent.md).
All clients require an existing relay and operator-provided tunnel credentials.

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
