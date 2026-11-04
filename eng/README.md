# WP01-L4 — Package integrity scripts (draft)

These scripts are a scoped first slice of Groundwork Issue #7, stacked on B00 PR #12.
They do not edit root build settings or publish a release.

## Producer

Build and pack all six Groundwork modules from the pinned source SHA using an
identical unique prerelease PackageVersion into a clean output directory.
Run eng/New-PackageManifest.ps1 with PackageDirectory, Version, SourceCommit,
BuildId and OutputManifest parameters. It validates the six expected package
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

## Not yet completed

The actual CI workflow/trusted manifest channel, automated negative test suite,
independent clean WYU consumer tests with locked restore and source mapping,
retention safeguards and release archival are outstanding Issue #7 gates.
No execution or CI success is claimed for these newly authored scripts.
