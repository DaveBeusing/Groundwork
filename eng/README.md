# WP01-L4 — Package integrity scripts (draft)

These scripts are a scoped first slice of Groundwork Issue #7, stacked on B00 PR #12.
They do not edit root build settings or publish a release.

## Producer

Build and pack the ten Groundwork modules, including Persistence, Voxels,
Rendering and its D3D12 provider, from the pinned source SHA. Use one unique
prerelease PackageVersion for all packages and a clean output directory.
Run eng/New-PackageManifest.ps1 with PackageDirectory, Version, SourceCommit,
BuildId and OutputManifest parameters. It validates the expected package
names, nuspec ID/version, sizes and SHA-256 hashes before writing a manifest.
It refuses to overwrite existing manifests.

## Consumer

Obtain the manifest digest and exact expected source SHA from an independently
trusted CI run/artifact record. A hash distributed only beside an untrusted
package does not authenticate the publisher. Create an external feed directory.
Run eng/Stage-VerifiedPackages.ps1 with ManifestPath, PackageDirectory,
FeedDirectory, ExpectedManifestSha256, ExpectedSourceCommit.

The staging script rejects a changed manifest, incorrect source commit,
unexpected package sets, wrong names/versions, size/hash changes, and package
coordinate overwrite with different bytes. The feed is hierarchical and outside
both worktrees. If an interrupted staging leaves a partial feed, discard the
isolated feed before retrying; multi-package staging is not transactional.
Previously approved six-, seven-, eight- and nine-package archives remain
valid inputs for staging. The isolated consumer compiles against Persistence
and its transitive Voxels dependency when present, or directly against Voxels
for older archives that contain it. The consumer verifies the exact TerraFX
package before compiling against the D3D12 provider.

## Not yet completed

The actual CI workflow/trusted manifest channel, automated negative test suite,
independent clean WYU consumer tests with locked restore and source mapping,
retention safeguards and release archival are outstanding Issue #7 gates.
No execution or CI success is claimed for these newly authored scripts.

## Repeatable local negative tests

After the producer and positive staging pass, run `eng/Test-PackageIntegrity.ps1`
with `-ManifestPath`, `-PackageDirectory`, `-ExpectedManifestSha256` and
`-ExpectedSourceCommit`. The test isolates input copies under the system temp
folder and requires rejection of modified/missing/untrusted manifests, wrong
source SHA, tampered/missing/wrong-version package and immutable feed coordinate
collision. It first requires a positive staging baseline. A negative test that
unexpectedly succeeds makes the harness fail. No source artifacts are modified.
The harness does not authenticate locally supplied hashes and does not test
NuGet lockfiles, caches, cross-repository restoration or CI provenance.

## Pinned-package archive preservation

WYU has pinned exact Groundwork package version
`0.1.0-dev.41920122570.1`, built by successful GitHub Actions run
`41920122570` (attempt 1). Its reviewed manifest SHA-256 is
`E07D23C78F922E8EBB193E03E5B7F5C2A8FDD4EF50C081B914B183D4017296F9`.
Ordinary CI artifacts may expire after the requested 90-day retention period,
and the effective retention could be shorter due to repository/org limits.
Do not assume that the version is durable merely because WYU pins it.

The opt-in `.github/workflows/archive-pinned-package.yml` workflow is a
single-candidate archive operation, not an automated publication or general
release process. Once reviewed and merged to `master`, it can be manually
dispatched from `master` with confirmation `ARCHIVE-41920122570`.
Its job targets the `groundwork-archive` environment; configure **required
reviewers** and suitable environment protections before running. It requires
a top-level `LICENSE` file because publishing a GitHub Release distributes
package bytes publicly. The owner selected Apache-2.0 and B00 PR #12 now
includes the official LICENSE and NuGet PackageLicenseExpression. The archive
workflow must run only after that B00 content reaches master; its public
archive includes a copy of LICENSE along with the package bytes.

Before writing a GitHub Release, the workflow checks the exact producer
run/head/attempt and manifest SHA-256, verifies all six finished nupkg hashes
and sizes, checks for an existing archive tag, and creates an audit record.
It then attempts to create a prerelease GitHub Release with the six packages,
the original manifest, archive record and the Apache-2.0 LICENSE. The archive asset should not
have automatic CI expiration. Nevertheless, maintainers can still delete or
replace ordinary GitHub Release assets unless immutable-release controls are
enabled, so **enable GitHub Release immutability / restrict maintainers** where
available and audit the archive before asserting immutable provenance.
The GitHub Release is not itself a signed/supply-chain attestation.

**This archive is NOT yet published.** The current PR is a draft stacked on
B00; review, merged Apache-2.0 license, environment review protection and
successful manual archive run are prerequisites. After archiving, change WYU
consumer retrieval to use the durable archive or add a verified fallback so
a clean checkout does not depend exclusively on the expiring original run.

See GitHub Actions run 41920122570 and WYU PR #3 for the exact pinned consumer.
