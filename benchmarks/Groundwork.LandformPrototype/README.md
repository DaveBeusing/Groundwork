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

## Scheduler capture

Run Trace-Wp17Scheduler.ps1 from an administrative PowerShell on Windows. It builds Release, records context-switch and ready-thread stacks plus QPC anchors, and saves the ETL, per-sweep CSV and block percentiles. A successful capture prints [FERTIG] with the output directory. The optional second argument to the paired benchmark appends raw sweep evidence to a CSV. Each row identifies the process and native thread, start/end QPC, seed, brick scenario, block, variant and thread-cycle delta. Data is written after each block, outside the timed sweeps.

A validation run without ETW emitted 17,280 sweep rows across 45 blocks. All 45 passed p95 and 44 passed p99. Seed 42 open block 3 exceeded p99 by 27.33%. Its baseline p99 was sweep 161 (4.6391 ms, 14,191,104 cycles) and its linear p99 was sweep 115 (5.9070 ms, 18,001,306 cycles). The two previously flagged blocks passed in this run. These counters show additional charged cycles for the new slow sweep but cannot attribute them to placement, frequency, or scheduler events. Kernel ETW capture is pending.

## Scheduler capture and zero-relief shortcut

The workstation recording WP17-ETW-20261114-182801 contains all 17,280 sweeps and decoded with zero lost ETW events. Nine application anchors align QPC with ETW. The previous confluence block did not reproduce (4.1737 ms baseline and 4.1628 ms linear at p99, with zero measured off-CPU time in the p99 sweeps).

| Recorded overrun | Baseline / linear p99 | Baseline / linear p99-sweep cycles | Linear off CPU |
| --- | --- | --- | --- |
| Open, maximum seed, block 5 | 4.6475 / 5.7522 ms | 14,255,003 / 17,169,100 | 0.0056 ms |
| Crest, maximum seed, block 4 | 5.2307 / 6.4146 ms | 14,279,884 / 18,727,834 | 0.0222 ms |

CSwitch stacks include kernel frames and the benchmark thread migrated in both intervals. NVIDIA Display Container was switched in when the open sweep left CPU 23. Windows Terminal was switched in when the crest sweep left CPU 1. The measured off-CPU intervals account for little of the wall gap. The trace does not isolate an on-CPU source such as core frequency, migration cost or contention. It has no repeating confluence overrun to analyze.

The candidate now uses the baseline density loop when all precomputed columns have zero relief. This removes unnecessary per-vertex subtraction in the open fixture. The point-versus-column checks passed and the profile CSV remained unchanged. A repeat without ETW retained all sweep samples.

| Scenario | Mean p95 baseline / linear (ms) | Mean p99 baseline / linear (ms) | Blocks over +10% / +15% |
| --- | --- | --- | --- |
| Open | 6.3815 / 6.3868 | 6.9751 / 7.2289 | 0 / 1 |
| Crest | 5.3345 / 5.2082 | 6.5158 / 6.4495 | 0 / 1 |
| Confluence | 4.4517 / 4.5444 | 5.3373 / 5.4649 | 0 / 3 |

See workstation-zero-relief.csv for all 45 blocks. The repeat passed all p95 blocks and 40 p99 blocks. The tail cause and consistent p99 compliance remain open.

## Fixed P-core scheduler and power repeat

WP17-ETW-20261114-183651 used processor 12 in group 0, Windows efficiency class 1 (performance class on this host). All 22,210 context-switch events involving the sweep thread used processor 12. ETW decoded with zero lost events. The WPR Power profile recorded 4,173 delivered-frequency reports for processor 12. The delivered-frequency field is a normalized interval indicator, so the values below are reported as recorded rather than converted to MHz.

| Scenario | Worst block p95 | Worst block p99 | Over +10% p95 | Over +15% p99 |
| --- | ---: | ---: | ---: | ---: |
| Open | +2.93% | +2.66% | 0/15 | 0/15 |
| Crest | +5.76% | +14.94% | 0/15 | 0/15 |
| Confluence | +28.21% | +20.45% | 2/15 | 2/15 |

The same two confluence blocks exceeded p99. The table joins each variant's actual wall-p99 sweep to its charged cycles and elapsed off-CPU time. Comparing independently sorted cycle percentiles would obscure this distinction.

| Seed / block | Wall baseline / linear (ms) | Off CPU baseline / linear (ms) | Thread cycles baseline / linear (million) | Delivered frequency indicators baseline / linear |
| --- | --- | --- | --- | --- |
| 42 / 5 | 175.8647 / 205.3109 | 165.2518 / 194.2257 | 31.67 / 33.51 | 52–75 / 42–76 |
| 18446744073709551615 / 2 | 135.6831 / 163.4304 | 102.1759 / 124.7336 | 98.38 / 111.29 | 20–23 / 16–22 |

The context-switch records show predominantly Ready state on outgoing linear sweeps. Other runnable work on the same core included desktop and system processes. Off-CPU differences explain about 29.0 of 29.4 ms in the first block and 22.6 of 27.7 ms in the second. The second block also has more charged cycles and low frequency reports in both variants. The trace does not establish how much of the residual is due to frequency versus other on-CPU effects. No sweeps were excluded from wall-clock acceptance. The full-workload p95/p99 gates and reference qualification remain open.

Use Trace-Wp17Scheduler.ps1 -Processor 12 from an administrative PowerShell to reproduce on this workstation. The script validates the performance class, pins the sweep thread and records scheduler stacks and Power events. See workstation-fixed-pcore.csv for the full block data.

## Quiet desktop confluence repeat

WP17-ETW-20261114-184804 repeated the corrected confluence bricks only, with the desktop left idle and the sweep thread pinned to group 0 processor 12. All 254 context-switch records involving the sweep thread used processor 12. ETW decoded with zero lost events, three application QPC anchors and 736 delivered-frequency reports for processor 12.

All 15 blocks across the three seeds passed both wall-clock limits, with no sweeps excluded. The worst p95 delta was +2.77% (seed 42, block 5) against +10%. The worst p99 delta was +2.35% (maximum seed, block 1) against +15%. See workstation-quiet-junction.csv for all blocks.

| Seed / block | Wall p99 baseline / linear (ms) | Off CPU baseline / linear (ms) | Actual p99 sweep cycles baseline / linear (millions) | Delivered frequency indicator values baseline / linear |
| --- | --- | --- | --- | --- |
| 42 / 5, previously over budget | 6.1935 / 6.1775 | 0 / 0 | 18.91 / 18.86 | 93 / 92 |
| 18446744073709551615 / 2, previously over budget | 5.6820 / 5.7232 | 0 / 0.145 | 17.11 / 17.05 | 99, 102 / 99 |
| 18446744073709551615 / 1, largest p99 delta in this repeat | 4.5977 / 4.7059 | 0 / 0 | 14.06 / 14.39 | 124 / 124 |

Frequency indicators are normalized Windows delivered-frequency reports near each sweep, rather than instantaneous MHz. The two former outlier blocks showed little or no off-CPU time and comparable frequency indicators between variants. Their long scheduler delays did not recur in this capture. In the worst p99 block of the quiet repeat, the extra wall time coincided with higher charged cycles at the same reported frequency indicator and no measured off-CPU time. These results cover the focused workstation density sweep. Full brick generation and reference qualification stay open.

Reproduce using Trace-Wp17Scheduler.ps1 -Processor 12 -JunctionOnly in an administrative PowerShell and leave the desktop idle until [FERTIG].
