# WP10 Windows alpha handover

**Status: engineering rehearsal recorded. Release acceptance is open.**

The [WP10 acceptance contract](architecture/wp10-windows-alpha-contract.md)
defines the offline Windows alpha. This record captures a local
rehearsal from the approved source and separates it from clean-system
release evidence.

## Exact baselines and artifact

| Item | Identity | Evidence |
| --- | --- | --- |
| Groundwork documentation baseline | `ec81d9ea64a4236755ee969952186c7d3da68a43` | [Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/42422957057) |
| Groundwork package source | `565dd78c580419d7f0678337f443c894256cc1af` | [Producer Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/42420693798) |
| Ten exact Groundwork packages | `0.1.0-dev.42420693798.1` | [Immutable package archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42420693798.1-immutable) |
| Groundwork package manifest SHA-256 | `1F313B6C086B6C68A738210A4A2D8378941EC43BB217CE9B7357170F67DC3FBD` | Verified before locked restore |
| WYU `master` | `2f2d9fb5e8297dde37bcfa22cdd2651a575b4729` | [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/42467355941) |
| Rehearsal installer | `WYU-Alpha-0.1.0-win-x64.exe` | SHA-256 `795871887D229012C5B5749459F44CAA2B8A18CAC78B25A3579F092614351421` |
| Rehearsal installer manifest | `installer-manifest.json` | SHA-256 `F28963041B6BA32F12EC482E2DEE2637D76C62F1C937C9F1427B7006C9F26C61` |
| SDK, embedded runtime and packaging compiler | `10.0.403`, `10.0.14`, Inno Setup `6.7.3` | Clean checkout, fresh locked restore and self-contained `win-x64` Release publish |

The build used the locally verified archive and the existing pinned
installer compiler. The local compiler identifies its build as
non-commercial. The owner accepts the unsigned installer for this
WP10 alpha. The installer and verification manifest remain local
rehearsal artifacts.

## Windows rehearsal

The workstation ran Windows 11 Enterprise x64 `10.0.26300` with an
Intel Core Ultra 9 285HX, 63.5 GiB RAM and a 5120 × 2160 desktop.
Windows reported an NVIDIA RTX PRO 5000 Blackwell Laptop GPU (driver
`32.0.15.9653`) and Intel Graphics (driver `32.0.101.8724`).
The rehearsal used a medium-integrity token without elevation and
isolated test data on the existing development installation. The
network remained connected.

- The verified Release build, four portable gameplay tests and
  headless startup, shutdown and controlled-fault checks passed.
- Per-user setup, Start menu launch using the bundled CoreCLR,
  reinstall repair and uninstall passed. The settings and save
  sentinels retained their hashes. The digest check rejected an
  intentionally damaged installer fixture before execution.
- The installed client passed keyboard navigation, guidance skip and
  replay, settings Apply and Cancel, restart at 1280 × 720 and corrupt
  settings recovery with the original file retained.
- Starting from the isolated partial-construction fixture, the
  installed client saved and resumed through the ordinary menu,
  finished the beacon, committed the rescue signal, launched, completed
  a full orbit (6.28607695 rad) and landed. A fresh process restored
  generation 2, the exact landing pose and canonical terrain edits.
  Both flight checkpoint files retained their hashes. The 4,332-tick
  flight trace SHA-256 was
  `7157DA3B7F3DB7C5B303BD4E28AEBE3A86A7D15156BAF431EE1278F3810CD230`.
- A separate deterministic rescue replay from spawn on the same WYU
  Release source mined all three deposits, built all modules and
  activated the beacon. The NVIDIA adapter ran with Debug Layer and DRED.
  Shutdown reported 734 warnings, zero errors, corruption or discarded
  messages, and zero live GPU child objects. This covers the rescue
  renderer path. A unified run from an empty installed slot and the
  final flight renderer qualification remain part of release acceptance.

## Remaining release evidence

- Confirm the compiler license for the intended release and record
  the final installer digest and durable artifact location.
- Provision a fresh offline Windows 11 x64 system with a standard user
  and no SDK, development checkout, prior WYU data or separate runtime.
  Exercise installer cancel and cleanup, ordinary Start menu launch,
  keyboard and mouse guidance, camera settings and focus loss.
- Complete the ordinary installed route from a fresh mission, including
  both checkpoint restarts. Capture two deterministic replays, then
  attach WARP and NVIDIA Debug Layer/DRED results with a zero-live-child
  report to the final acceptance record.

The [installer](https://github.com/DaveBeusing/wyu/pull/22),
[first-run experience](https://github.com/DaveBeusing/wyu/pull/23),
[mission](https://github.com/DaveBeusing/wyu/pull/24) and
[diagnostics](https://github.com/DaveBeusing/wyu/pull/25) are on WYU
`master`. Final acceptance remains pending on the clean-system evidence.
