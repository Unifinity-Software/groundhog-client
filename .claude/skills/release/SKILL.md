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
After approval, tag the reviewed commit agent-v<version>, push the tag, and watch Release agent to completion.

Verify all four exact versions anonymously on nuget.org, including source/symbol metadata.
Restore the sample in a fresh cache from nuget.org only, run full package-mode Aspire tests,
and verify global and local CLI installation. Coordinate the relay's pinned package bump and real-Agent
integration test. Stable promotion requires public source, cross-platform checks, preview feedback,
and an explicit owner-reviewed go/no-go. Record URLs and validation results.

Published bytes are immutable. Retry unchanged artifacts only; use a new version after corrections.
