---
name: release
description: Publish the Groundhog Aspire, Agent, Protocol and CLI packages to NuGet.
argument-hint: <version>
disable-model-invocation: true
---

# Release the client packages

All four packages share a version. See docs/client-release.md for owner and trusted-publishing configuration.
Run the full local Release build, all client tests, pack metadata inspection, package-only sample build,
package-mode Aspire tests, and temporary/local CLI installs before pushing a release PR.
Fix failures and rerun affected checks before pushing. CI verifies locally checked work.

Fetch origin and tags. Select a merged master commit whose PR Build check passed.
Confirm there is no running release and the version is greater than the previous agent-v tag.
Update version metadata, sample dependency pins and installation snippets together in the release PR.

Present the exact commit, version, four IDs, change list and NuGet company owner for release review.
Ask once before publishing unless that precise release has already been confirmed in this session.
After approval, run Release agent on master with the version (`gh workflow run release-agent.yml --ref master
-f version=<version>`, or the GitHub actions tools where gh is unavailable). It releases master's tip and creates
the tag, so no tag push is needed and it works from a cloud session. If master moved past the reviewed commit,
stop and review again. Watch Release agent to completion.

Verify all four exact versions anonymously on nuget.org, including source/symbol metadata.
Restore the sample in a fresh cache from nuget.org only, run full package-mode Aspire tests,
and verify global and local CLI installation. A client release does not need a relay release: bump the
relay's pinned packages (and run its real-Agent integration test) only when the relay must use a Protocol or Agent
change. Stable promotion requires public source, cross-platform checks, preview feedback,
and an explicit owner-reviewed go/no-go. Record URLs and validation results.

Published bytes are immutable. Retry unchanged artifacts only; use a new version after corrections.
