# WP12 cross-repository handover

This record covers the [WP12 underground geology contract](architecture/wp12-underground-geology-contract.md) and the fixed seed 42 Ember Caves route on the Windows workstation. The nominal planet radius is 4,148 m and the saved V2 geology recipe SHA-256 is `6BDB11C8C473F22AD2A7904981E10DDB481EDED2C5C8FCA3FF9C5DFAFBABBAC6`.

## Tested revisions

| Input | Identity | Evidence |
| --- | --- | --- |
| Groundwork master and package source | `bf56de9347c2a99159595812968a0141bf0e8708` | [Windows CI, attempt 1](https://github.com/DaveBeusing/Groundwork/actions/runs/43118949133/attempts/1) |
| WYU master | `f79a9adf1665da4b072ab7528854f69d44aa005c` | [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/43123871918), [WP12-W01](https://github.com/DaveBeusing/wyu/pull/48) |
| Ten packages | `0.1.0-dev.43118949133.1` | [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43118949133.1-immutable) |
| Package manifest SHA-256 | `2406F20155B6CD05EA3B268DCCF256B95385E716BE7103A211850291FBB1B61D` | Exact WYU workflow pin |

Both checkouts matched remote master and had an empty tracked status before the test. WYU verified the manifest and package contents, restored with an empty external package cache in locked mode and built Release with .NET SDK 10.0.403. The seven gameplay tests and headless lifecycle checks passed. Groundwork passed all 17 filtered `VolumeCheckpointTests` on Windows.

## NVIDIA route

`eng/Test-CaveExploration.ps1` from the clean WYU checkout ran separate mining and continuation processes on an NVIDIA RTX PRO 5000 Blackwell Laptop GPU (`10DE:2C38`, driver `32.0.15.9653`). Debug Layer and DRED were enabled in both processes.

| Check | Result |
| --- | --- |
| Descent and mining | **Pass.** The player walked from the checked natural entrance to a collision-confirmed roof about 10.3 m below the reference surface, targeted a visible deposit and mined four ore. |
| Underground save and restart | **Pass.** The new process restored generation 1, terrain revision 2 and four ore at `(4137.228939746989, 49.20778064671379, 34.914613340334895)`, then committed a simulation tick. The overlay SHA-256 was `fa0c64ac8f919dbb5f185683a36e7586265952e454303ff4e44835a24dda070d`. The committed member hash was unchanged across the restart. |
| Native retirement | **Pass.** The processes presented frames, closed the windows and released Core resources. D3D12 reported 8 and 2 warnings respectively, with zero errors, corruption, discarded messages or live child objects. |

The committed underground checkpoint was copied into a separate temporary world for each return attempt. The exact same WYU Release build opened it through the normal world selector using **Continue**. The live return used the game’s Windows key input and F1 position display. F10 saved and exited without touching the source fixture.

The direct reverse input (**S**, 15 seconds) held at approximately `(4137.0, 49.2, 34.9)`. Lateral inputs did move the player. A diagonal sequence reached `(4137.4, 50.9, 33.6)`, then further reverse inputs held there. This left the player about 10 m below the nominal surface. Each attempt ended with a successful generation 2 save at terrain revision 2 and a clean process exit. The runtime log confirmed NVIDIA for the live attempts. The Debug Layer, DRED and live-object counts above are from the scripted runs.

**Return to surface: fail on the tested route.** Reproduce from the generation 1 checkpoint produced by the cave smoke test. Open a copy through the world selector and enable F1. Hold S, then try alternating S+A, A and S+D around the ramp. A follow-up must demonstrate an ordinary-control route from the persisted underground pose to walkable surface outside the entrance.

## Installer and outstanding qualification

The exact WYU revision built a self-contained offline Windows x64 installer with Inno Setup 6.7.3 and .NET runtime 10.0.14. Installer SHA-256 `6B2E0BDD916D31D7BEF58D1A88A478BFB1E18B02BEEB9CC293F327A84F81CC95`. Installer manifest SHA-256 `4173255F47D547CEA3E2C704A1F2351EC77EB847BD3C2213F66FA3BD340DB5F9`. The route evidence is from the Release client. The installed cave playtest and full route acceptance are pending.

Groundwork #89 and reference production qualification remain open. [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89)
