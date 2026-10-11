# Contributor rules

Follow CONTRIBUTING.md. This public repository contains only original Groundhog client code.
Never copy unrelated private code, production identifiers, credentials or private source links into it.
Commit with a GitHub noreply address, or `noreply@unifinity.com` for automated contributors such as coding agents.
Never use `tunnel.example.com`: docs, samples and tests use the hosted addresses (`groundhog.unifinity.com`,
`<name>.groundhog.unifinity.com`). Build locally and run the full affected test suites before pushing or
opening a PR; fix and rerun failures. Do not use CI to discover locally detectable failures.
Update docs/protocol.md with protocol behavior changes. Use fake time for timing behavior and shared fixtures
for expensive setup. Keep GitHub-hosted CI to one job unless another clearly shortens its run.
