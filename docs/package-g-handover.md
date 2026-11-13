# Package G Windows integration handover

The workstation replay covers both Far Horizon seeds from surface twilight through ascent, orbit, descent and Continue. The [Package G contract](architecture/package-g-atmosphere-contract.md) defines the renderer and acceptance limits. Package G consumes the shared clock, canonical camera and local terrain fog from the [Package E handover](package-e-handover.md).

| Input | Tested identity |
| --- | --- |
| Groundwork master | `ec27ba372768085371e14f2cc37d6ffcec322a28`. [Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/43469775303) |
| WYU master | `e80e97e7dd9a570a251b1ea6392ab1d0d059c4af`. [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/43477524089) |
| Groundwork package set | `0.1.0-dev.43469775303.1`. [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43469775303.1-immutable) |
| Manifest SHA-256 | `287C42229DFA78B146A076890ED8D879C6F5195C047F9C42DB1A834829466CA3` |
| Integrations | [Groundwork #132](https://github.com/DaveBeusing/Groundwork/pull/132) · [WYU #55](https://github.com/DaveBeusing/wyu/pull/55) |

Both repositories were clean at the fetched master commits. Groundwork rebuilt its D3D12 tests from source. WYU used a fresh locked restore from the ten verified packages whose producer commit matches the tested Groundwork master. The verified consumer also ran the nine gameplay tests and the headless and controlled-fault lifecycle tests.

## Windows results

| Seed | Surface clock | First far presentation (flight tick) | Orbit (flight tick) | First near presentation on return (flight tick) | Continued environment tick |
| --- | ---: | ---: | ---: | ---: | ---: |
| 42 | 35500 | 659 | 2640 | 19797 | 56164 |
| 20261111 | 35500 | 543 | 2640 | 19917 | 56164 |

- Both seeds passed on WARP and NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU with Debug Layer and DRED. The flight route checked twilight, ascent and the far renderer handoff, orbit above the shell, the near renderer on descent, return of local fog, and touchdown. Continue matched the saved sky inputs. Handoff retained the session queue by design. Final settlement reported zero live child objects.
- The integrated desktop run compared 24 matched views on the presented 944 × 501 client surface across the two seeds, two adapters and two renderer handoff directions. Controlled horizon, solar disk and star views used the same eye, tick and projection across each pair. No compared RGB channel differed by more than one code value and the solar centroid stayed within one pixel.
- Eleven focused Groundwork D3D12 tests passed, covering clear fallback, Package E fog, scattering, twilight, seeded stars, limb, terrain occlusion, shell continuity and distant thin-shell hit/tangent/miss. The solar disk centroid stayed at (959.5, 539.5) across the 1920 × 1080 rebase on both adapters. All four DXIL shaders passed deterministic validation.
- With G disabled, the Package E flight regression passed on WARP and NVIDIA, including the edited landing pad, Continue, aborted-flight recovery and pre-install NTFS interruption.

## Reproduction and remaining qualification

Run WYU's `eng/Test-VerifiedGroundworkConsumer.ps1` against the exact archive, commit and manifest above. On an unobstructed Windows desktop run `eng/Test-ReferenceSky.ps1` for hardware and again with `-Adapter warp`. Set `WYU_PACKAGE_G=off` for `eng/Test-ProceduralFlight.ps1`. For Groundwork, run the D3D12 test project in Release with `GROUNDWORK_SKY_REQUIRE_DEBUG=1`, `ContinuousIntegrationBuild=true` and the filter `FullyQualifiedName~SkyReadback|FullyQualifiedName~InvalidSky|FullyQualifiedName~DistanceFog|FullyQualifiedName~ClearWritesTheExpectedRgbaAndDepth|FullyQualifiedName~AsymmetricWorldViewAndProjection|FullyQualifiedName~DepthReadbackPreservesTheNearCube`.

The workstation replay qualifies integration and pixel continuity. The contract's 1080p reference-class sky-pass and total-frame timestamps, matched-camera A/B, handoff, CPU and GPU memory budgets and WP11 pressure and production qualification remain open. [Groundwork #112](https://github.com/DaveBeusing/Groundwork/issues/112) · [WYU #42](https://github.com/DaveBeusing/wyu/issues/42) · [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88) · [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89)
