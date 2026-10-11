# Contributing

Use .NET 10. Central package versions live in `Directory.Packages.props`.
Build Release and run the full affected client suites locally before pushing or opening a PR.
Fix failures and repeat affected checks after corrections; CI verifies work already checked locally.
Pack and verify package consumers when changing public APIs, dependency metadata, or release configuration.

Write original code for this project. Never bring code from unrelated private products.
Use placeholders, and `example.com` only for arbitrary third-party hosts and people. Never commit secrets or private
deployment values.
Groundhog is a hosted service only: do not advertise self-hosting. Its owner-approved public address is defined once in
code in `GroundhogService.DefaultRelayAddress`. Docs, samples and tests use the hosted addresses,
`https://groundhog.unifinity.com` and tunnel addresses such as `https://myapp.groundhog.unifinity.com`; never use
`tunnel.example.com` or another made-up service host.
Commit with a GitHub `noreply` email, or `noreply@unifinity.com` for automated contributors such as coding agents.
Tests should guard real behavior, share expensive fixtures, and use fake time rather than waiting on real time.

## Development overrides

Customer docs never mention a service-address override. For development and testing against a local or fake relay
(for example the relay repository's local dev loop), these remain available and are hidden from IntelliSense or help:

- `GroundhogAgentOptions.RelayAddress` (defaults to `GroundhogService.DefaultRelayAddress`).
- The `AddGroundhogTunnel` overloads that take a `relayAddress` string or parameter, including the custom-credential
  overloads.
- The CLI's `--relay <url>` option on `host`, `profile save`, `doctor`, `login` and the remote commands. Profiles saved
  with it keep the address; a saved tunnel key is never sent to a different address.

Hosting and `doctor` accept plain `http://` addresses for loopback testing; account and management commands require
HTTPS. Tests use the fake relay in `tests/Groundhog.Agent.Tests`.

## Protocol changes

This repository is canonical for Protocol, Agent, CLI and Aspire source.
Change the public [protocol contract](docs/protocol.md) together with changes in behavior.
Publish matching client packages first. The relay maintainer then bumps its pinned Protocol and Agent versions
and runs the real published Agent against the relay's in-process integration fixture before merging.
Do not depend on a server checkout to build or test this repository.

## Releases

All four packages use the same version. Preview packages are for evaluation; stable promotion requires
public source and symbols, exact-version consumer validation, supported platform checks, preview feedback,
and an owner-reviewed release decision. See [release procedure](docs/client-release.md).
