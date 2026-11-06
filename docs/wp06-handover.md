# WP06 world save and recovery handover

Groundwork and Worlds Yet Unseen (WYU) were verified on Windows from separate
clean `master` clones on Windows 11 Enterprise x64 build `10.0.26300`.
Owner acceptance follows
[ADR-0007](adr/ADR-0007-solo-review-governance.md).

## Tested baselines

| Baseline | Exact identity | Evidence |
| --- | --- | --- |
| Groundwork `master` and package source | `5ab62bbbee7498a178b75a246804709a63238e07` | [Groundwork Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/42303559178/attempts/1) |
| WYU `master` | `b6eb245d8cf49ff652687d4a209ec360a5fd426a` | [WYU Windows consumer CI](https://github.com/DaveBeusing/wyu/actions/runs/42305721594) |
| WYU exact pins | `[0.1.0-dev.42303559178.1]` for all ten packages | [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42303559178.1-immutable) |

All 13 freshly downloaded release assets matched their published sizes and
SHA-256 digests. The package manifest links the exact binaries to the producer
commit and run. The WYU verification rejected a wrong version, a missing
package and an altered package before restoring from the verified local feed.

| Provenance file | SHA-256 |
| --- | --- |
| Producer CI artifact ZIP | `62863269EBD0C46C4B44250A4072A1D135DB5996CA009FA8A320E54692477A1E` |
| Package manifest | `D82D42F2271A5A885E18937B7B0124854268EB3CDDFD1820FCBF84EBE50580E8` |
| Archive record | `E8AD1A170D3CD7FE93BD0F32AF22BB6E2794DF28BEBB6BDDF77980E850592C20` |
| Apache-2.0 license | `B1D2870F1A00E4D7F56576E5F0870CBA109E041F8AF66866CF5B499478E654E7` |

## Groundwork format and NTFS recovery

All 81 Persistence tests passed. They cover the schema-1 golden frame,
canonical order and round trips, bounded framing and malformed input,
compatibility, and reconstruction of the terrain overlay. Slot tests cover
alternating files, corrupt-newest fallback, preservation of incompatible or
damaged files, stale bases, blocked access, cancellation and retry.

The child-process test used a local NTFS slot and fresh readers. A killed first
save left the slot empty. Terminating generation 2 before publication
preserved generation 1. Terminating generation 3 after publication and before
acknowledgement restored generation 3. The fixture also checked busy-writer
exclusion, idempotent retry and stale-generation rejection.

## WYU recovery

WYU restored with .NET SDK 10.0.403 from the verified package feed and a
fresh NuGet cache. Its Release build reported zero warnings and errors.
The Windows consumer smoke committed generation 1 with a removed cell at
world `(-1,3,4)`, a non-default player position and a nonempty inventory.
It terminated a second writer after flushing `save.pending` and before
installing it. Generation 1 retained the same SHA-256, and a fresh process
loaded its edit, position and inventory. The edited cell was air in the
streamed region, and the neighboring cell across the seam remained solid.
This check also passed in the post-merge WYU Windows workflow.

In `--save-world [slot-directory]`, **T** toggles the boundary cell and **F5**
queues a checkpoint after a published tick. Restarting the mode restores the
selected generation before the region is streamed.

## Chunk boundary and native shutdown

A local fixture built a separate executable from the unchanged WYU client
sources and the same locked package feed. It compared the generated region
with the region streamed from generation 1. The edit exposed two triangles
on the neighboring chunk's face at X 0, where the original region had none.
A downward sweep centered on the edited voxel settled at Y 3 instead of the
original Y 4.

The fixture presented the restored region on NVIDIA and blocked generation 2
after the candidate flush. It invoked the production host shutdown path.
The native window closed while the save worker still held the exclusive slot
lease. A competing reader received `Busy`. After the fixture released the
barrier, the save returned `Canceled`, Core released its resources with no
primary fault, and the original checkpoint retained its SHA-256.
No generation 2 committed file appeared.

| Desktop check | Result |
| --- | --- |
| Groundwork WARP region | Three passes covering color/depth readback and stop with a staged region |
| Groundwork NVIDIA region | Two passes covering color/depth readback and stop with a staged region |
| WYU native window, D3D12 cube and WP05 walking route | Passed in separate processes |
| WYU shutdown during a staged save | Passed on NVIDIA with Debug Layer and DRED active |

The NVIDIA adapter was the RTX PRO 5000 Blackwell Generation Laptop GPU
(`10DE:2C38`, driver `32.0.15.9653`). Debug Layer and DRED were active in
the WYU cube, route and staged-save runs. They reported 2, 911 and 1 debug
warnings respectively, with zero errors, corruption, discarded messages or
live child objects and live-object report HRESULT `0x00000000`.

In the first combined Groundwork run, 369 of 373 cases passed and four strict
D3D12 cases reported an inactive Debug Layer. All four passed when their
categories ran in fresh test processes. The first WYU D3D12 startup exceeded
its deadline while the Groundwork suite was running. The separate WYU rerun
passed without changes to either checkout.

## Scope

The qualified storage backend is local Windows NTFS. The child-process
results establish recovery from process interruption. Machine power-loss
durability and other filesystems remain outside the
[WP06 contract](architecture/wp06-save-recovery-contract.md).

## Reproduce

From the tested Groundwork checkout, with a fresh NuGet cache:

```powershell
dotnet restore Groundwork.slnx --locked-mode
dotnet test tests/Groundwork.Persistence.Tests/Groundwork.Persistence.Tests.csproj -c Release --no-restore
```

Run the WARP and NVIDIA diagnostic categories in separate processes as shown
in the [WP05 handover](wp05-handover.md#reproduce).

From the tested WYU checkout, download the archive above into a new directory
and supply that path as `$archive`:

```powershell
& ./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $archive `
    -ExpectedManifestSha256 D82D42F2271A5A885E18937B7B0124854268EB3CDDFD1820FCBF84EBE50580E8 `
    -ExpectedSourceCommit 5ab62bbbee7498a178b75a246804709a63238e07 `
    -ExpectedVersion 0.1.0-dev.42303559178.1 -SaveSmoke
```

The desktop switches `-WindowSmoke -D3D12Smoke -WalkableSmoke` require an
interactive Windows session with Graphics Tools for the diagnostic checks.
