# Immutable archive migration for WYU-pinned Groundwork packages

Groundwork Core (and five companion modules) remain pinned at exactly
`0.1.0-dev.41920122570.1`. The existing public release
`archive-groundwork-0.1.0-dev.41920122570.1` is retained and not modified.
Its GitHub API field `immutable` was false on 2026-11-05.

## Proposed new public archive

New release tag: `archive-groundwork-0.1.0-dev.41920122570.1-immutable`.
The tag differs; the package identity, versions and bytes **must not**.
Source CI run: 41920122570 (attempt 1).
Required manifest SHA-256:
`E07D23C78F922E8EBB193E03E5B7F5C2A8FDD4EF50C081B914B183D4017296F9`.

The separate workflow `.github/workflows/archive-immutable-pinned-package.yml`
is **manual only**, restricted to `master`, and uses the existing
`groundwork-archive` environment for owner/reviewer authorization.
It reads the trusted original producer run, refuses to overwrite an existing
new tag, downloads the original six package bytes and validates their sizes,
identities and SHA-256 values against the separately pinned manifest digest.
It creates a new archive record referencing the previous release and includes
the root Apache 2.0 LICENSE. Exactly nine assets are expected.

The workflow checks the GitHub API `immutable == true` **after** creation.
If the check fails, the workflow fails and the new release must be inspected:
the public release may already exist even though the job failed. Do **not**
rerun and overwrite assets or republish modified package bytes. If release
immutability cannot be achieved, keep the old verified release and stop.

## Release and downstream acceptance

1. Review and merge this workflow to `master`.
2. Confirm Groundwork repository release immutability is enabled and environment
   `groundwork-archive` requires the intended reviewers/branch restriction.
3. Obtain separate approval to dispatch the workflow with
   `confirmation=IMMUTABLE-41920122570`.
4. Inspect the completed GitHub release and API `immutable: true`, all nine
   assets, and confirm all six nupkg SHA-256 digests exactly match the existing
   reviewed manifest.
5. Update WYU's verified-artifact CI workflow to pin the **new release tag** and
   archive-record SHA-256. Do not change WYU's `PackageReference` or NuGet
   `packages.lock.json`; perform a fresh-cache locked restore and Windows
   Release build.
6. Keep the original archive for audit history. Only reconsider its removal
   after independent retention policy and downstream reproducibility review.

This preparation does **not** publish a release. The archive remains subject
to GitHub repository settings and workflow/environment access policies.
