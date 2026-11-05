# WP01-B00 Groundwork build bootstrap

The solution keeps the six approved WP01 shipping projects and adds separate
contract test projects as modules land. Each implementation lane owns its tests.

## Windows prerequisites and verification

Install a supported stable .NET 10 SDK, Git and PowerShell 7. `global.json`
requires .NET 10.0.100 or a later 10.0 feature band. Run from the repository root.

```powershell
$testResults = Join-Path $env:TEMP ('groundwork-tests-' + [guid]::NewGuid().ToString('N'))
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory $testResults
```

The script checks the production and test project boundaries, restores the tracked
NuGet locks, builds Release and verifies that each selected test project executed
passing tests. Windows CI uses a fresh package cache and publishes the TRX results.
Build products go to the checkout's ignored `artifacts/` directory.

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

Groundwork is licensed under Apache License, Version 2.0 (SPDX: Apache-2.0).
The root LICENSE contains the complete license text; Directory.Build.props
sets PackageLicenseExpression=Apache-2.0 for newly built NuGet packages.
This license decision does not retroactively modify already-built packages.
Review third-party dependencies for security, license obligations and required
NOTICE attribution before any redistribution.
## Ownership handover

B00 exclusively owns shared root settings, solution and project files.
L1 owns Platform.Windows, L2 Simulation, L3A Ecs, L3B Jobs, L5 Diagnostics.
L4 owns CI and verified package staging; WYU L6 owns exact consumer pins and
the generated lock files. Public Core API decisions require contract review.
