# Groundhog clients

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/svg/lockups/lockup-tagline-dark.svg">
  <img src="assets/brand/svg/lockups/lockup-tagline-light.svg" alt="Groundhog: self-hosted HTTPS tunnels" width="400">
</picture>

Groundhog gives local applications a public HTTPS address through the hosted Groundhog service or a self-hosted relay.
This repository owns the MIT-licensed developer clients: the Aspire integration, CLI, Agent and Protocol.
It does not deploy or host the relay.

## Aspire

The setup below uses **0.2.0-preview.4**.

Before running a client, [sign in to Groundhog](https://tunnel.unifinity.com) and create your free workspace.
Follow [Create your free workspace](https://tunnel.unifinity.com/docs/workspace), then
[Create a tunnel](https://tunnel.unifinity.com/docs/create-tunnel). Keep the tunnel's name and one-time connection
key private while you configure your app. Your workspace address is a dashboard path, not your tunnel name.

Requires .NET 10 and Aspire 13.6.1 or later. In your AppHost directory:

```text
dotnet add package Groundhog.Aspire.Hosting --version 0.2.0-preview.4
```

```csharp
var key = builder.AddParameter("groundhog-key", secret: true);
var tunnel = builder.AddGroundhogTunnel("tunnel", "myapp", key)
    .WithReference(web.GetEndpoint("http"));
web.WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress);
```

The tunnel resource exposes `tunnel.Resource.PublicAddress` (for example `https://myapp.tunnel.unifinity.com`) and
`PublicHost` for other resources' settings; nothing waits for it unless you add `WaitFor(tunnel)`. The dashboard shows
it as Connecting, Running (with the public URL), Reconnecting or Failed, with a **Reconnect** command after it stops.
No CLI or tool needs installing: the agent runs inside the AppHost.

Set the key through the AppHost's secret parameter prompt or user-secrets editor. Turn on **Save to user secrets**
in the prompt if you want Aspire to remember it on this computer; user secrets are outside the repository and are not encrypted.
See the [package-only quickstart](samples/AspireQuickstart/README.md) and [Aspire guide](docs/aspire.md).
Install using `dotnet add package`: current Aspire CLI integration search filters package prefixes and
does not discover this package.

## CLI

```text
dotnet tool install --global Groundhog.Cli --version 0.2.0-preview.4
groundhog host myapp --port 5173
```

Supply the connection key through `GROUNDHOG_KEY`. See the [CLI guide](docs/agent.md).
Use the same [workspace](https://tunnel.unifinity.com/docs/workspace) and
[tunnel setup](https://tunnel.unifinity.com/docs/create-tunnel) before starting the CLI. Aspire and the CLI connect to the hosted
service automatically; explicit relay addresses remain available for self-hosting.

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
