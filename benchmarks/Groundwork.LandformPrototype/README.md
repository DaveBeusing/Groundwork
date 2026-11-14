# WP17-P00 local landform prototype

Both candidates use the uncarved WP12 Ember Caves landform recipe (zero elevation and zero relief amplitudes), with the same 1 m lattice and a 4096 m fixture radius. The shipped WP12 cave world uses 4148 m and a different route. The harness evaluates candidate elevation and density checksums without changing either shipped generator.

The candidates share a seeded triangular-wave crest and main channel, a shorter branch converging into the main channel, and a four-metre local cut. The branch fades at both ends. The main and branch use the deeper depression where they overlap, keeping channel depth bounded. Linear bands have sharp shoulders. Smooth bands use a quantized cubic smoothstep. Only the bounded +Y fixture at |X| ≤ 256 and |Z| ≤ 192 receives relief. The band widths, amplitudes, phase streams and clamped envelope are in Relief.cs. Work is bounded to four band evaluations with integer arithmetic and no lookup tables. This patch is a prototype for comparing shapes and cost. Whole-planet topology and the V3 identity are pending.

## Profiles

Transects follow each feature at 8 m intervals across -128 to +128 m in Z, with the tributary sampled through its junction at zero. Across the three seeds the baseline is flat at 0 m.

| Transect | Linear elevation | Smooth elevation |
| --- | ---: | ---: |
| Ridge crest | +21.00 to +22.00 m | +21.38 to +22.00 m |
| Main valley | -16.00 m | -16.00 m |
| Tributary | -16.00 to -9.25 m | -16.00 to -9.81 m |
| Local cut fixture | +10.35 m | +11.81 m |
| Off-feature control | 0.00 m | 0.00 m |

The profile CSV matched bit-for-bit across two fresh processes. A reversed Parallel.For reproduced each serial signature.

| Seed | Profile SHA-256 |
| --- | --- |
| 42 | ede0efe13f63a3348e79c2e5169616e2ba9521e7c27b9039d5afe5d8615637c4 |
| 20261111 | 620c9a6957f881c5c9eed82836a2646ffb3e805eea409a9609ab7ba611164e83 |
| 18446744073709551615 | ece718f646689905cfea1adfdcbb07592fdb3a6a64030ce9ea20ddb20668e767 |

## Workstation measurements

Windows x64 Release, .NET SDK 10.0.403, Intel Core Ultra 9 285HX (24 cores) and 63.5 GiB reported RAM. Single-threaded brick-density sweeps use the same 33³ canonical vertices per brick across variants, with 12 warm-up iterations and 192 timed sweeps per seed/scenario in each process. Two fresh processes per variant ran sequentially in baseline/linear/smooth/smooth/linear/baseline order. Each cell below is the mean of the six run/seed percentiles, in milliseconds. See workstation-timings.csv for the individual statistics.

| Scenario | Baseline p50 / p95 / p99 | Linear p50 / p95 / p99 | Smooth p50 / p95 / p99 |
| --- | --- | --- | --- |
| Open | 4.836 / 6.831 / 7.877 | 4.677 / 5.657 / 6.208 | 4.928 / 6.504 / 7.280 |
| Ridge | 4.271 / 5.115 / 5.402 | 4.693 / 5.992 / 6.627 | 5.069 / 6.596 / 7.111 |
| Junction | 5.035 / 7.148 / 7.837 | 4.744 / 6.220 / 7.033 | 5.019 / 6.517 / 7.189 |

All timed loops allocated zero managed bytes. Maximum process peak working set was 40,620,032 bytes for baseline, 40,673,280 for linear and 40,706,048 for smooth. Maximum peak paged memory was 19,542,016, 19,496,960 and 19,460,096 bytes respectively. These process totals include the runtime and the common coordinate array and cannot isolate concurrent engine scratch.

Linear is the first candidate to carry forward for optimization. It preserves the control profiles and has lower ridge cost than smooth. Even linear exceeds the target +10% p95 and +15% p99 on the ridge in this exploratory run. Open and junction timings vary with run order and do not demonstrate a speedup. Full V2/V3 brick builds including material and carving, orbital and streaming routes, seam tests and reference-class budgets remain open. WP15 default activation and Groundwork #89 remain open.

## Reproduce

Build this project in Release. From the repository root run its DLL in artifacts/bin/Groundwork.LandformPrototype/release with profiles, baseline, linear or smooth. Profile mode writes seed, feature, X, Z and the baseline, linear and smooth heights in 1/256-cell units. Benchmark modes write the columns listed in workstation-timings.csv. Diagnostics go to stderr. The existing 12 PlanetDensityFieldTests and PlanetGeologyFieldTests passed.

## Linear brick-column follow-up

The local linear relief depends only on X/Z within the positive-Y fixture. Compute it once for each of the 33 × 33 columns per brick and reuse it over Y. The sweep includes filling the 8,712-byte stack buffer. It checks every optimized vertex against the original evaluator before timing. The profile CSV matched the original bit-for-bit.

Run the Release DLL with paired. Each seed/scenario alternates variant order within five blocks of 192 paired sweeps in one process. The table compares the mean of block percentiles. The largest individual paired delta is reported separately. See workstation-linear-columns.csv.

| Scenario | p95 baseline / linear (ms) | p95 mean delta / worst pair | p99 baseline / linear (ms) | p99 mean delta / worst pair |
| --- | --- | --- | --- | --- |
| Open | 5.0355 / 5.1060 | +1.40% / +5.60% | 5.6464 / 5.5524 | -1.67% / +10.43% |
| Ridge | 4.5990 / 4.7217 | +2.67% / +6.40% | 5.3936 / 5.3272 | -1.23% / +14.16% |
| Junction | 5.1665 / 5.2687 | +1.98% / +8.11% | 5.6070 / 5.6321 | +0.45% / +13.74% |

All 45 pairs meet +10% p95 and +15% p99. The paired loops allocated zero managed bytes. The combined process peaked at 33,423,360 bytes working set and 11,575,296 bytes paged memory. This run qualifies the local density prototype on the workstation. Complete brick generation, independent reference runs and V3 integration remain open.
