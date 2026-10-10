# Contributing

Use .NET 10. Central package versions live in `Directory.Packages.props`.
Build Release and run the full affected client suites locally before pushing or opening a PR.
Fix failures and repeat affected checks after corrections; CI verifies work already checked locally.
Pack and verify package consumers when changing public APIs, dependency metadata, or release configuration.

Write original code for this project. Never bring code from unrelated private products.
Use `example.com` and placeholders in documentation and tests. Never commit secrets or private deployment values.
The owner-approved public service address is an intentional exception: define it once in
`GroundhogService.DefaultRelayAddress`. Keep an explicit service override available for self-hosting and migrations.
Use a GitHub `noreply` commit email. Tests should guard real behavior, share expensive fixtures,
and use fake time rather than waiting on real time.

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
