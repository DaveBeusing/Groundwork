# WP01-B00 Groundwork build bootstrap

This branch introduces the six approved WP01 module boundaries as build-only projects.
It does not implement a runtime, scheduler, ECS, jobs, diagnostics or window handling.
The projects are for existing planned work lanes, not speculative future modules.

## Windows prerequisites and verification

Install a supported stable .NET 10 SDK and Git. global.json requires .NET
10.0.100 or a later 10.0 feature band. Run from the repository root:

- dotnet --version
- dotnet restore Groundwork.slnx --configfile NuGet.Config
- dotnet build Groundwork.slnx -c Release --no-restore
- dotnet test Groundwork.slnx -c Release --no-build
- git ls-files (verify no generated bin, obj, artifacts or nupkg)

There are no runtime/unit tests yet. A successful test-command exit alone is
not proof of executable correctness. Build products are written to ignored,
per-worktree artifacts/ directories. Do not use a shared build output directory.

## Versions, packages and feed boundaries

Source version is 0.1.0 in Directory.Build.props and must not change per commit.
Project, assembly, namespace and package identities are Groundwork.Core,
Groundwork.Platform.Windows, Groundwork.Simulation, Groundwork.Ecs,
Groundwork.Jobs and Groundwork.Diagnostics.
Only the five non-Core projects reference Groundwork.Core; nothing references WYU.

Use a unique build-time version override for each development package:
dotnet pack src/Groundwork.Core/Groundwork.Core.csproj -c Release
-p:PackageVersion=0.1.0-dev.123456.1 -o artifacts/packages
Here 123456.1 is illustrative, not an assigned real build ID. Never overwrite
different bytes at the same package ID/version. These nupkg outputs are untrusted
local build artifacts, not authorized feed releases.

WP01-L4 will create and verify an independently trusted build manifest
including exact package ID, version, filename, finished nupkg SHA-256,
source commit, build ID, timestamp and configuration before staging into
an explicitly configured feed outside all repository worktrees.
WYU must never consume an unverified nupkg or link Groundwork sources.
No feed endpoint or package publication is assumed.

NuGet lock generation is enabled. When dependencies exist, commit real
SDK-generated packages.lock.json files; CI must use locked restore.
Do not hand-author or invent dependency hashes.

Development artifacts: 90-day retention, except still-pinned versions must
be preserved before cleanup. Release archives and manifests: durable retention
until explicit removal. L4/release automation owns enforcement.

No software license is inferred from public GitHub visibility. A license
must be chosen before external redistribution. Review all external
dependencies for security and licensing before addition.
This PR does not claim an installed SDK, successful Windows build, CI checks,
executable tests, package trust verification or release automation.

## Ownership handover

B00 exclusively owns shared root settings, solution and project files.
L1 owns Platform.Windows, L2 Simulation, L3A Ecs, L3B Jobs, L5 Diagnostics.
L4 owns CI and verified package staging; WYU L6 owns exact consumer pins and
the generated lock files. Public Core API decisions require contract review.
