# ADR-0006 — NuGet provenance, exact pinning and release governance

**Status:** Proposed ADR; version/distribution/retention policies **user-approved** on 2026-11-02  
**Owners:** B00 / L4 / WYU L6, C00 integrator  
**Trace:** [Groundwork #1](https://github.com/DaveBeusing/Groundwork/issues/1), [#2](https://github.com/DaveBeusing/Groundwork/issues/2), [#7](https://github.com/DaveBeusing/Groundwork/issues/7), [WYU #1](https://github.com/DaveBeusing/wyu/issues/1)

## Context

Groundwork is independently released and WYU must consume exactly the intended, authentic-to-the-approved-build engine bytes. Neither a mutable local package folder nor a NuGet content hash alone proves trusted origin.

## Decision

1. Both repositories independently begin with **SemVer `0.1.0`** in their eventual `Directory.Build.props`. Normal source commits do **not** increase versions. Development packages use uniquely identifiable `0.1.0-dev.<run-id>.<attempt>`-style versions via a build-time override, without changing source version files. `PackageId + PackageVersion` always identifies **immutable bytes**; never overwrite divergent content at that coordinate.
2. Groundwork produces `.nupkg` and independently trusted machine-readable manifests containing package ID, full version, final file SHA-256, filename, commit SHA, build identity, timestamp and build configuration. Verify the **finished package bytes** against the approved manifest before copying to a configurable hierarchical local feed outside the working trees. Never treat a manifest fetched only beside an untrusted package as origin authentication.
3. WYU consumes only **exact** approved Groundwork package versions with committed `packages.lock.json` and CI `dotnet restore --locked-mode`. Its portable `NuGet.Config`/generation template uses package-source mapping; `Groundwork.*` may only resolve from the validated feed. Build must fail closed on missing/wrong/altered artifacts, changed locks or source fallback. Test restores with a fresh package cache, without shared engine checkout.
4. Retain ordinary dev/CI artifacts for **90 days where provider limits allow**, preserving/promotion-protecting versions still pinned by WYU or active reproducibility baselines before cleanup. Official releases and manifests go to durable release storage **without automatic expiration**. Provider outages/backups are separate concerns.
5. A **future**, separate release workflow will ask for Major/Minor/Patch, update and commit each repo's `Directory.Build.props` independently, run approved gates, then publish durable release artifacts. WYU must continue pinning a reviewed Groundwork version; independent repository SemVer does not imply matching versions.
6. GitHub Packages may be added later; no authenticated registry, signing, CI automation, branch protection, local feed or `Directory.Build.props` is asserted to exist as part of this documentation-only WP01-C00 PR.

## Alternatives / consequences

Rejected: floating engine versions, direct project references, per-commit SemVer increments, implicit package-source fallback and unaudited digest-only acceptance. Consequences: extra provenance verification/storage and explicit package handover, in exchange for reproducible clean builds.

## Verification / implementation gate

B00 writes root settings and IDs; L4 builds producer manifest, verified staging/CI tests and retention evidence; WYU L6 writes exact package refs, lockfiles and isolated consumer tests. Negative cases: altered hash, wrong ID/version, no manifest, cached-package source bypass, expired pinned package, stale lock, duplicate coordinate with differing bytes. The ADR specifies requirements **not executable automation**.