# WP15 shadow-only workstation benchmark

This source benchmark measures the D3D12 shadow path for [#113](https://github.com/DaveBeusing/Groundwork/issues/113). The underlying implementation is Groundwork master `f10ab3fc51ef4a0d0796d6d3a2cf9c252dde7fb8). The timing hooks activate only for this test and existing explicit timing fixtures.

## Reproduction

Run from a clean checkout in a Windows x64 desktop session. Use a new results directory and keep the adapter, driver and power plan fixed.

```powershell
pwsh -NoProfile -File benchmarks/Measure-Wp15Shadows.ps1 -Adapter Hardware -Pairs 5 -Frames 2000 -OutputDirectory ./artifacts/wp15-workstation
```

Each pair starts a fresh Release test host. Both variants use the same deterministic 32-step route, 32 warm-up frames per variant and phase, and 2,000 measured frames per variant and phase by default. Odd pairs run shadows off first. Even pairs run shadows on first. The runner preserves metadata, locked restore/build logs, raw per-frame JSON, and TRX results. It checks the adapter and driver, shader and scene hashes, sample count, route step alignment, diagnostic state and variant order before pooling. Use `-Adapter Warp -Pairs 1 -Frames 16` with another output directory for a short harness check.

## Scene and measured intervals

The 1920 × 1080 offscreen fixture uses four immutable packed-normal meshes with 4,612 triangles. Receiver grids sit at 24 m and 48 m with geometry that occludes light on both. Both variants retain the same geometry, tint, directional sunlight and distance fog. Exactly one input changes within a pair, the shadow-enabled flag. Midday uses a higher moving light, low sun a shallower moving light and quick turns alternate camera yaw while the light moves. Each shadow-enabled measured frame records two native cascade passes. The disabled variant records zero. Two untimed pixel probes per phase check matched depth and visible darkening.

- The direct queue GPU frame interval starts before Clear and ends before the color/depth readback transfer. The terrain interval covers terrain recording commands. Each shadow interval includes the cascade transitions, clear and draw. The fixture uses the same GPU readback copy and waits for retirement in each variant. CPU pixel extraction occurs only at the two untimed probe points.
- CPU recording starts before shadow preparation and ends after command-list close. The resource ledger stays enabled for both routes to report native backing. The timings include this instrumentation and whole-frame recording. The benchmark leaves shadow-specific allocations and isolated cascade-setup CPU cost to the remaining qualification.
- Per-pair and pooled summaries use nearest-rank p50, p95 and p99. Incremental GPU time is the sum of the two enabled cascade times and the enabled terrain time minus the disabled terrain time at the same step of the same pair. The relative GPU frame change compares the enabled and disabled percentiles of the recorded frame interval.

This comparison isolates the shadow flag in a serialized source fixture. The installed WYU Far Horizon comparison with Package B, E and fixed G state, real Present and the #88 reference hardware remains open. The relative thresholds from #113 provide context for the measured frame interval. The production budget, the full WP15 toggle and default activation require their own qualification.
