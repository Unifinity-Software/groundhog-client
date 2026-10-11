# Client package releases

The four client packages share one semantic version: `Groundhog.Protocol`, `Groundhog.Agent`,
`Groundhog.Aspire.Hosting` and `Groundhog.Cli`. `agent-v<version>` on a tested merge commit to `master`
publishes them to nuget.org and attaches packages and symbols to a GitHub Release. The server is not published
as a NuGet package. Preview versions use a suffix such as `0.4.0-preview.1`.

## One-time publishing setup

1. Choose a NuGet login profile and package owner. A user can own packages directly; an organization can own
   them with a member administering its policy. The GitHub `NUGET_USER` variable names the login profile,
   not its email address.
2. Create a NuGet trusted publishing policy for the publishing repository, `release-agent.yml`, and the
   GitHub environment `nuget`. Grant **Push new packages and package versions** for the four exact package IDs.
   Select the company organization as package owner. Do not grant unlist/relist or an unrestricted `*` package scope.
3. Create the GitHub `nuget` environment and set `NUGET_USER`. A private repository's policy may be temporarily
   active for seven days; its first successful publication binds the policy to the repository identity.
4. The initial preview's empty first-publication probe belongs to the original publishing repository.
   This repository publishes only the four client packages through reviewed version tags. Never put an API key
   into an issue, command log or source file.
5. Prefix reservation is a separate manual request and does not block a preview if the IDs can be published.

See [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

## Integration discovery

Install with `dotnet add package Groundhog.Aspire.Hosting`. Aspire CLI's current integration search filters
package IDs to `Aspire.Hosting.*` and `CommunityToolkit.Aspire.Hosting.*`; an exact ID does not bypass that filter.
NuGet search and the package README are the supported discovery path for this preview. Aspire CLI discovery
requires a separate upstream or Community Toolkit contribution; do not promise an `aspire add` alias.
See the [Aspire CLI package filter](https://github.com/microsoft/aspire/blob/main/src/Aspire.Cli/NuGet/NuGetPackageCache.cs).

For Toolkit inclusion, its [contribution process](https://github.com/CommunityToolkit/Aspire/blob/main/CONTRIBUTING.md)
requires a feature request and maintainer discussion/approval before an integration PR. A proposal should describe
the existing relay prerequisite, in-process Agent, secret parameters, public address expressions, offline startup,
protocol compatibility and fake-relay tests. Integration documentation is contributed through the aspire.dev
repository. This is a contribution track, not a package-registration form; inclusion and prefix access require
upstream acceptance. Keep the existing NuGet install instructions until that acceptance exists.

## Local preflight

Run the complete affected local suites before pushing code or creating the release PR. CI verifies passing
local work. Fix failures and repeat affected checks after corrections.

```text
dotnet build Groundhog.Client.slnx -c Release -p:Version=0.4.0-preview.1
dotnet test --solution Groundhog.Client.slnx -c Release --no-build
dotnet pack Groundhog.Client.slnx -c Release --no-build -p:Version=0.4.0-preview.1 -o artifacts/packages
```

The full client solution uses local fake relays and does not require Azurite, a server checkout or production
credentials. Inspect nuspec dependencies, license, README, icon, public SourceLink and symbols.

Use `UsePublishedClientPackages=true` and `ClientPackageVersion=<version>` to run the complete Aspire
suites against packages. For local artifacts, also set `RestoreAdditionalProjectSources=<absolute package folder>`.
The relay maintainer separately runs its integration test with the exact packaged Protocol and Agent. The Aspire suite
switches its source project reference to the hosting package and exercises forwarding, reconnect, rejected keys
and offline startup. Do not pass `--no-build` when switching between source and package modes.

Build `samples/AspireQuickstart/AppHost/AppHost.csproj` from local artifacts, then repeat using only nuget.org
after publication. Verify CLI installation into a temporary tool directory and a fresh local tool manifest;
verify `--help`, `--version` and the host command. Cross-platform smoke validation is required before stable.
Run the single-job **Build** workflow manually for Windows, Linux and macOS to verify the full client suites,
sample and packed CLI on each platform. Pull requests use GitHub-hosted Linux runners.
Set `GROUNDHOG_CLI_SMOKE_PATH` to an installed tool executable and run the full CLI suite to verify a real
packed `groundhog host` forwards a request and stops cleanly when replaced. The fixture uses loopback and
a generated key passed only in the child process environment.

## Publish and verify

1. Merge the locally verified PR after Build succeeds. The release workflow verifies that the tagged commit is
   a merge to `master` and that the latest Build run for that PR succeeded.
2. Present the exact commit, version, change list and NuGet owner for release review. Obtain confirmation if
   this precise publication has not already been approved in the session.
3. Tag the reviewed commit `agent-v<version>` and push that tag. Watch **Release agent** to completion.
4. Verify all four IDs and versions in NuGet's flat-container API. Indexing is asynchronous: a successful push
   alone does not prove anonymous restore works.
5. Restore the sample in a fresh NuGet package cache using only nuget.org, run package-mode suites against the
   exact version, and repeat local/global CLI installation from NuGet. Record package URLs and results.

Published package bytes and tags are immutable. If a push partially succeeds, retry the same workflow only
when artifacts are unchanged; `--skip-duplicate` preserves the existing versions and pushes missing ones.
For changed artifacts, use a new preview version. Confirm symbols separately after a partial release because
a duplicate main package does not guarantee its symbols were uploaded.

## Relay compatibility and stable promotion

This repository is canonical for the client source. Its policy binds publishing to this repository.
The relay references a pinned public Protocol package and its integration test references the matching
public Agent package. Protocol changes are client-first:
publish the client PR, then submit the server package-version bump and run the real-agent integration suite.
Stable promotion requires accessible public source, documentation and symbols, successful exact-version
integration tests and an owner-reviewed go/no-go.
