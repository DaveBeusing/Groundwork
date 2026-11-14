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

## Historical workstation measurements

The original fixed-coordinate timings below are retained as historical evidence. The old ridge and junction keys did not reliably contain those features. The feature-derived comparison below supersedes those rows. All current modes use the corrected keys.

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

## Historical linear brick-column follow-up

The local linear relief depends only on X/Z within the positive-Y fixture. Compute it once for each of the 33 × 33 columns per brick and reuse it over Y. The sweep includes filling the 8,712-byte stack buffer. It checks every optimized vertex against the original evaluator before timing. The profile CSV matched the original bit-for-bit.

Run the Release DLL with paired. Each seed/scenario alternates variant order within five blocks of 192 paired sweeps in one process. The table compares the mean of block percentiles. The largest individual paired delta is reported separately. See workstation-linear-columns.csv.

| Scenario | p95 baseline / linear (ms) | p95 mean delta / worst pair | p99 baseline / linear (ms) | p99 mean delta / worst pair |
| --- | --- | --- | --- | --- |
| Open | 5.0355 / 5.1060 | +1.40% / +5.60% | 5.6464 / 5.5524 | -1.67% / +10.43% |
| Ridge | 4.5990 / 4.7217 | +2.67% / +6.40% | 5.3936 / 5.3272 | -1.23% / +14.16% |
| Junction | 5.1665 / 5.2687 | +1.98% / +8.11% | 5.6070 / 5.6321 | +0.45% / +13.74% |

All 45 original pairs met +10% p95 and +15% p99 on the fixed-coordinate bricks. The old ridge and junction results do not establish the feature budgets. The paired loops allocated zero managed bytes. The combined process peaked at 33,423,360 bytes working set and 11,575,296 bytes paged memory. This run qualifies the local density prototype on the workstation. Complete brick generation, independent reference runs and V3 integration remain open.

## Feature-derived brick comparison

For each seed, the crest at Z=-96 and the local confluence at Z=0 come from the relief evaluator. The harness brackets the actual density zero crossing in Y and checks that the selected brick contains the adjacent solid and non-solid vertices. The confluence is on the upper Z face, with the brick extending into the incoming side. This samples the local confluence rather than the entire tributary. Both variants use the same brick.

| Seed | Crest X/Y/Z (last solid) | Crest brick | Confluence X/Y/Z (last solid) | Confluence brick |
| --- | --- | --- | --- | --- |
| 42 | -52/4116/-96 | -2/128/-3 | 0/4079/0 | 0/127/-1 |
| 20261111 | -61/4116/-96 | -2/128/-3 | -4/4079/0 | -1/127/-1 |
| 18446744073709551615 | -55/4116/-96 | -2/128/-3 | -11/4079/0 | -1/127/-1 |

The repeated workstation comparison used the paired mode and 192 sweeps per variant in each of five blocks per seed and scenario. Deltas compare the means of block percentiles. The last column counts individual block overruns.

| Scenario | p95 baseline / linear (ms) | p95 delta | p99 baseline / linear (ms) | p99 delta | Blocks over +10% p95 / +15% p99 |
| --- | --- | ---: | --- | ---: | --- |
| Open | 5.6563 / 5.6043 | -0.92% | 6.3867 / 6.5368 | +2.35% | 0/15 / 1/15 |
| Crest | 7.0998 / 7.2018 | +1.44% | 8.3424 / 8.4676 | +1.50% | 0/15 / 1/15 |
| Confluence | 4.9364 / 4.9495 | +0.26% | 5.5757 / 5.5090 | -1.20% | 0/15 / 0/15 |

The largest crest p95 delta was +6.14%. Two p99 blocks exceeded +15%, both for seed 18446744073709551615. The crest reached +25.66% in block 1 and open terrain +20.38% in block 3. The confluence held both thresholds in every block. See workstation-feature-bricks.csv. Timed loops allocated zero managed bytes. Process peak working set was 34,312,192 bytes.

The combined means are within budget. The individual p99 overruns leave consistent tail compliance open. This is a single-process workstation comparison of density sweeps. Full brick generation and reference qualification are pending. The Windows workflow builds the prototype and checks fixture selection and exact column-to-point equality through the profiles mode.

## Investigation of the p99 overruns

The previous run recorded wall-clock percentiles only, so it cannot identify the cause of either original outlier. The repeat uses the same corrected brick keys and paired order. Both variants now use the same row traversal, and the candidate selects the relief branch once per row instead of once per vertex. The measured candidate still fills the column scratch and subtracts relief at each vertex. The profile CSV stays byte-identical and the column verification passes.

The harness also records Windows thread-cycle p99 alongside wall-clock p99. These percentiles are sorted independently and thread cycles are affected by clock/core behavior. They do not identify the event that caused an individual wall-time sample.

| Scenario | Mean p95 baseline / linear (ms) | Mean p99 baseline / linear (ms) | Worst block p95 / p99 | Blocks over +10% / +15% |
| --- | --- | --- | --- | --- |
| Open | 6.8347 / 6.8234 | 8.3076 / 8.2875 | +2.68% / +18.70% | 0 / 1 |
| Crest | 6.4999 / 6.4891 | 7.0524 / 7.0547 | +1.60% / +9.25% | 0 / 0 |
| Confluence | 5.3323 / 5.3370 | 5.6969 / 5.7758 | +5.41% / +20.76% | 0 / 1 |

Both previously flagged seed/block combinations passed on repetition. The new open block (maximum seed, block 2) had +18.70% wall p99 and +16.46% thread-cycle p99. The new confluence block (20261111, block 4) had +20.76% wall p99 and +0.70% thread-cycle p99. The two signatures differ and do not establish one root cause. Persistent open-ground and confluence tail compliance remains unverified. No samples were excluded. Timed loops allocated zero managed bytes. See workstation-tail-investigation.csv.
