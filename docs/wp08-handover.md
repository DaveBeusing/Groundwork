# WP08 spherical planet handover

The [spherical planet contract](architecture/wp08-spherical-planet-contract.md)
defines the 64 m reference planet and equatorial walking route. Groundwork
provides cube charts, stitched terrain, radial movement, bounded terrain
publication and D3D12 resource retirement. WYU supplies the terrain recipe,
route and controls. Owner acceptance follows
[ADR-0007](adr/ADR-0007-solo-review-governance.md).

## Verified baselines

Both repositories were cloned independently from `master` with clean
working trees. The tests ran on Windows 11 Enterprise `10.0.26300` x64
with .NET SDK `10.0.403`, locked restores and fresh NuGet caches.

| Baseline | Exact identity | Evidence |
| --- | --- | --- |
| Groundwork `master` and package source | `141a62ab6b22246d21cc48d74f45397cf902c910` | [Windows CI, attempt 1](https://github.com/DaveBeusing/Groundwork/actions/runs/42411077659/attempts/1) |
| WYU `master` | `0189e4c17104ac42ba33faeb2fc8577478f774cf` | [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/42412683403), [WYU PR #19](https://github.com/DaveBeusing/wyu/pull/19) |
| Exact NuGet pin, all ten modules | `[0.1.0-dev.42411077659.1]` | [Immutable Groundwork archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42411077659.1-immutable) |

All 13 fresh release assets matched their published SHA-256 digests.
The WYU consumer verified the package manifest, all ten package hashes
and exact pins before restoring from an external feed.

| Provenance file | SHA-256 |
| --- | --- |
| Original producer ZIP | `895747DD801E1532418159AFFF609A61ECEBE5F6E6A0E220E022CF4DA78BF34A` |
| Package manifest | `EE49C1228ED2D7D1DDD579F851119DEB8949F70AFD8579E05CC5084F0F283623` |
| Archive record | `FEFD25854BE28C5B72ABDCB2A8711B0D7A7EE50953CEE714388C2EC22E27B1E5` |

## Clean Windows results

Both Release builds completed with zero warnings and errors. The
Groundwork Windows gate passed all 368 tests across ten suites. It exercised
cube charts, seam topology, surface support, origin changes and late
terrain work alongside the runtime regressions. The WYU gate passed the
four portable gameplay tests,
headless startup, controlled-fault shutdown and WP06/WP07 recovery from
interrupted writes.

The focused D3D12 suite passed two WARP readbacks and one NVIDIA readback.
The readbacks checked color and depth through LOD refinement and coarsening,
the shared camera and terrain origin change, and the stitched seam at steep
and shallow view angles. WARP selected Microsoft Basic Render Driver
(`1414:008C`, driver `10.0.26100.9711`). All three cases ran with Debug
Layer and DRED active. Each reported eight warnings, zero errors,
corruption or discarded messages, HRESULT `0x00000000` and zero live GPU
child objects.

### Packaged WYU route

The fixture walked the 64 m planet from 35° to 145°, turned and walked
back to 35°. It used seed `0x57505538`, generator version 1 and a 60 Hz
simulation. The camera-only diagnostic samples ran at 10 Hz to keep the
bounded D3D12 message queue intact. NVIDIA selected RTX PRO 5000 Blackwell
Generation Laptop GPU (`10DE:2C38`, driver `32.0.15.9653`) with Debug Layer
and DRED enabled.

| Checkpoint | Motion tick | Cube face | LOD 5 tile | Origin revision | Presented terrain generation |
| --- | ---: | --- | --- | ---: | ---: |
| First grounded step | 1 | +X | 4,16 | 1 | 1 |
| Outbound first face crossing | 170 | +Z | 31,16 | 1 | 3 |
| Outbound second face crossing | 1678 | −X | 31,16 | 8 | 32 |
| Turnaround | 1843 | −X | 27,16 | 9 | 35 |
| Return first face crossing | 2009 | +Z | 0,16 | 9 | 37 |
| Return second face crossing | 3517 | +X | 0,16 | 16 | 65 |
| Back at the start | 3686 | +X | 4,16 | 17 | 68 |

The scripted walker stayed grounded and stayed within the 0.25 m waypoint
tolerance. Reported peak penetration and support separation both rounded
to 0.00000 m. Terrain refined and coarsened and peaked at 366 of 384
retained patch records.

The route passed twice from the same clean WYU build. All seven checkpoints
matched in motion tick, face, tile, LOD, origin revision, generation,
position and contact. Initial loading took 25 ticks in the first run and
14 in the second. Both runs presented the final camera frame before native
close and released Core resources. Each live-object report recorded 682
warnings, zero errors, corruption or discarded messages, HRESULT
`0x00000000` and zero live GPU child objects. DRED reported no removal.

This handover records the scripted Windows route. The interactive mode
uses the window title for waypoint guidance. The fixture keeps a single
fixed planet and starts a fresh session on launch.

## Reproduce

On an interactive Windows desktop with Graphics Tools installed, run the
Groundwork gate and the targeted color/depth readbacks from the Groundwork
checkout.

```powershell
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory $results
$project = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
dotnet test $project -c Release --no-build --no-restore --filter 'FullyQualifiedName~PlanetTerrainReadback' --logger 'console;verbosity=detailed'
```

From the WYU checkout, download the archive into a fresh directory and run
the verified consumer gate on NVIDIA.

```powershell
$archive = Join-Path ([IO.Path]::GetTempPath()) ('wyu-wp08-' + [guid]::NewGuid().ToString('N'))
gh release download archive-groundwork-0.1.0-dev.42411077659.1-immutable `
  --repo DaveBeusing/Groundwork --dir $archive
./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $archive `
  -ExpectedManifestSha256 EE49C1228ED2D7D1DDD579F851119DEB8949F70AFD8579E05CC5084F0F283623 `
  -ExpectedSourceCommit 141a62ab6b22246d21cc48d74f45397cf902c910 `
  -ExpectedVersion 0.1.0-dev.42411077659.1 -PlanetSmoke -SaveSmoke -PrototypeSaveSmoke
dotnet ./artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll --planet-smoke
```

For manual walking, start the built WYU client with `--planet`. Use WASD
to walk, left and right arrows to turn, and Esc to leave. The window title
gives the waypoint bearing and distance.
