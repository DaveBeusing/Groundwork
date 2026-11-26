# Package G · D3D12 sky and atmosphere contract

Package G adds one optional sky pass to the Groundwork D3D12 renderer. WYU supplies a single atmosphere and solar state from the surface through orbit and back. [Groundwork #112](https://github.com/DaveBeusing/Groundwork/issues/112) · [WYU #42](https://github.com/DaveBeusing/wyu/issues/42)

The [Package E handover](../package-e-handover.md) establishes the shared environment clock, canonical eye position, near/orbit handoff, exact package provenance and local terrain fog baseline. The two renderer owners continue to settle and transfer the same window/device/queue in sequence. [WP13](../wp13-handover.md) supplies the shared biome baseline.

## Frame inputs and ownership

- Groundwork exposes an additive, optional fixed-size sky value on the frame and forwards it through the paired volume-camera entry points, including camera-only updates and terrain publication. An optional sky on a frame without a scene carries its own camera and projection so the handoff mask can draw the same sky while terrain is held. Existing constructors, clear color and disabled output remain compatible. An invalid explicit value follows existing frame or paired-region rejection before sequence, pending frame or mesh state changes.
- The sky view carries the canonical planet-centred eye, an orthonormal right-handed forward/up basis expressed in the same axes, perspective vertical field of view and the existing target aspect ratio. WYU computes the planet-centred eye in double precision before conversion and keeps the same axes across rebases. The ray uses NDC with pixel centres and the same projection convention as terrain. It must point along forward at screen centre. Near/far clip planes do not truncate sky rays. For masks and paired terrain frames derive the sky and terrain views from the same accepted camera sample.
- The fixed scattering profile carries inner reference radius and outer atmosphere radius in metres, positive Rayleigh and aerosol scale heights in metres, nonnegative RGB Rayleigh and aerosol scattering coefficients and extinction coefficients in inverse metres, unit direction toward the sun in canonical axes, nonnegative solar radiance, solar angular radius, and a stable star seed. Extinction must be at least scattering componentwise. Validate finiteness, a strictly positive shell thickness and scale heights, a finite nonzero eye position, a valid perspective and orthonormal view, a normalized sun direction, and an angular radius strictly between zero and π/2. Use the same validation on both submission paths. The constant conversion must also reject profiles whose normalized values cannot be represented safely on the shader path.
- Normalize distances by reference radius before building shader constants and scale inverse-metre coefficients by the same radius. Define and test limits for the normalized shell thickness, eye radius, scale heights, coefficients and solar radiance in the first renderer PR. Include the reference profile and full flight altitude range in those limits. Validate bounds before casting to GPU constants. Use stable quadratic roots, bounded exponent arguments and a nonnegative ray interval. A tangent or no-hit ray takes the space path with no spurious atmosphere segment. For the small below-reference-radius positions allowed by terrain, project only the atmospheric integration origin outward to the reference sphere. Preserve the original camera view and terrain coordinates.

## D3D12 pass

- Clear existing color/depth, draw one fullscreen triangle into the same color target with depth testing and writes disabled, then record terrain as today. Preserve the sky image where terrain has no fragments. Terrain covers sky at any terrain depth and continues to apply Package E distance fog to its own RGB after tint. Sky adds no terrain draw, mesh upload, resolution-dependent target, or depth resource. Disabled frames skip the pass. Use the existing queue and frame retirement fences for pipeline resources and preserve device-loss custody.
- Use fixed single-scattering quadrature as the baseline, with at most eight midpoint samples along the visible shell and four along each sample-to-sun path. Intersect the outer sphere and clip the integration interval to its positive ray segment and the first inner-sphere hit. At each view sample, use exponential density profiles and RGB extinction for view and solar transmittance. Use four-point Gauss-Legendre solar-path quadrature to resolve the dense end of grazing rays. Approximate the finite solar disk's planetary penumbra with a smooth transition over its angular radius at each sample's local horizon; samples beyond that transition receive no direct solar scattering. This is a bounded single-scattering approximation, not a multiple-scattering or exact finite-disk integration model. Use a bounded Rayleigh phase and a fixed forward aerosol phase. Add sunlight and star contributions through view transmittance in the same pass. Groundwork defines the exact shader constants and output mapping with the readback fixtures. Daylight, twilight and the limb must follow the same equations across the shell boundary.
- Use a deterministic direction-based star field in canonical axes, with fixed work per pixel and a bounded anti-aliased footprint. Fade stars by sky luminance and extinction. Stars stay fixed for the same ray and seed across rebases and renderer changes. Render an anti-aliased angular solar disk from ray·sun, attenuated by transmittance and hidden when the planet blocks the sun ray. For rays that hit the inner sphere, suppress space sources behind it while retaining foreground scattering. The reference sphere models optical ground occlusion and does not write depth. Actual terrain determines terrain occlusion.
- Reuse existing color conventions. Calculate scattering and transmittance in linear RGB, map the resulting radiance into the existing color target with a fixed bounded transfer, and write opaque alpha. Do not add frame-dependent exposure. Keep clear fallback, terrain tint and fog values from E. Parameter changes publish frame data alone.

## WYU integration

WYU owns the versioned Far Horizon profile and freezes its numeric table, star seed, and fixture states before running acceptance. Use the Package E accepted 60 Hz clock and its presentation fraction. The sun uses the existing canonical direction (0, sin(2πp), cos(2πp)) with p from the 36,000-tick phase. Clock, sun, eye and view come from one presentation sample. Use that sample for the sky and terrain projection, including the held-publication mask. The handoff can change the player's view as part of flight control; compare the two renderers at the same pose, clock and projection to distinguish a sky discontinuity from camera motion.

Reuse the Package E checkpoint. Continue reconstructs sky from seed, saved tick and restored pose. The sky has no independent clock, persistent generator state or save version. Keep Package B and E active during comparisons. The Ember Caves profile and presentation stay on their existing path. Clouds, volumetric weather, direct terrain lighting and terrain shadows are outside G.

## Acceptance

The [reference measurement policy](../adr/ADR-0011-reference-measurement-policy.md) makes the full reference run optional for merge and unqualified preview. The hardware qualification and production rows below govern release and default activation. The archive prerequisite in this section applies to production-qualified release packages. Preview and diagnostic packages follow ADR-0011 and the existing package-provenance and publication rules. Functional and resource-lifetime tests remain required for the candidate.

| Gate | Evidence |
| --- | --- |
| Renderer image checks | WARP readback in Windows CI and NVIDIA with Debug Layer and DRED. Fixed camera/sun fixtures for daytime, twilight, night stars, horizon, orbital limb and empty space. Check planet and terrain occlusion, correct depth with sky enabled, continuity just below/above the outer shell, disabled clear-path regression, and Package E near fog. For equivalent camera and sun before/after a shared rebase, measure disk centroid displacement ≤1 pixel at 1920 × 1080. Include exact/near tangency, a camera inside/outside the shell, and invalid explicit frames. Keep numeric interior-pixel checks and recorded image regions with the test inputs. |
| Integrated Windows route | Two fixed Far Horizon flight runs on seeds 42 and 20261111. Capture surface twilight, ascent, first orbital frame, limb, descent and first near frame on return. Check star stability at the same canonical rays, disk/horizon alignment at matched handoff poses, local fog on descent, landing, Continue and shutdown. Exercise same-view sky updates, rebases, and held publication. Confirm renderer settlements, retained session queue only at intermediate handoff and zero live children on final shutdown. |
| Sky pass time | WP11 reference-class Windows x64 Release at 1920 × 1080. Timestamp commands around the sky pass on the same graphics queue and read after retirement. Surface, ascent and orbit each require p95 ≤0.8 ms and p99 ≤1.2 ms. |
| Total GPU frame and handoff | Same Release build, archive, seed, route, resolution, driver and presentation mode within each G-off/on pair, B and E enabled in both. For each phase use at least five independent runs and at least 2,000 samples per variant after warm-up. Total GPU frame timestamps must meet +5% p95 and +8% p99 per phase. Separately time handoff start through the first successful Present of the new view, including waits, with at least 30 handoffs per direction per variant and p95 ≤+10%. Retain raw timings and per-run and pooled summaries. |
| CPU, GPU memory and draws | Combined atmosphere preparation and pass recording p99 ≤0.10 ms and zero atmosphere heap allocations per frame after warm-up. At most one additional draw per sky frame, zero additional terrain/mesh uploads and resolution-dependent targets, and ≤4 MiB incremental peak GPU memory. Count at native draw/upload sites and account for driver-aligned allocations and overlapping handoff resources. Keep pass cost distinct from GPU frame time and Present latency. |
| Production | Repeat with the WP11 reference limits and pressure cases, including full speculative CPU queue, blocked speculative GPU uploads and protected movement/edit progress. Record holds, rejections, stale work and retirement. Workstation results are reported separately. [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88) · [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89) |

Review this contract before implementation. The first Groundwork slice adds frame validation, draw and image tests. Hardware qualification precedes publication of the exact immutable package archive from green Groundwork master. The WYU slice pins that archive, integrates the reference profile and flight route, and records workstation and reference qualification independently.

## Dawn and dusk renderer slice

### Preview activation

Start the process with `GROUNDWORK_TWILIGHT_PREVIEW=1` to enable the unqualified Twilight Preview in Groundwork and WYU.
The normal path uses the existing Direct shaders, sky before scene depth, indexed terrain, prop tint and frame pacing.
Preview selects its own sky and PBR shader variants, read-only depth culling, flat-prop irradiance, index-free expanded
terrain, packed normal/roughness, and the swapchain wake, mip-refinement yield and shutdown retirement wake changes.
Lookup and retired lookup-resource reuse require their additional diagnostic flags. The existing Direct build artifacts
remain available, and staged DLL overlays provide functional and diagnostic evidence with recorded source and binary identities.

The matched Groundwork rendering contracts and D3D12 provider both attest to Preview ABI 1. An unchanged approved WYU
package pin remains valid for the normal path. A paired Preview includes both matched renderer assemblies.
The new presentation waiter API, cleanup deadline synchronization and stale-acknowledgment protection apply in either mode.
The [reference measurement policy](../adr/ADR-0011-reference-measurement-policy.md) governs merge and release evidence.

### Approved visual decisions

On 20 November 2026 the owner approved TW1–TW3 for Groundwork #193 and WYU #83:
the captured warm low-sun/horizon treatment with cooler ambient light and a
shared morning/evening elevation curve (TW1), the existing 0.18 night-strength
floor with cool bias while night remains darker than day (TW2), and automatic
adoption by existing Far Horizon saves on Continue without changing seed,
clock or terrain (TW3). In this Preview, the shared opt-in gates that adoption.
WYU owns the lighting profile and save-adoption rule;
the renderer remains stateless. These approvals do not waive any performance,
provenance, installed handoff or final technical-review gate and do not themselves
authorize a merge or immutable package publication.

### Renderer implementation and evidence

The dawn/dusk update for #112 keeps the existing `RenderSky` input, normalized constant layout, single fullscreen draw, eight view samples and maximum four solar density samples per view sample. It adds no target, texture, upload or persistent state. Low-angle red/orange glow comes from the longer wavelength-dependent optical path, rather than an overlay color or a separate clock. The same canonical sun direction therefore produces the same image during sunrise and sunset. WYU remains responsible for the shared sun/time sample and its terrain/fog presentation.

`TwilightTests` exercises production D3D12 readback at 321 × 181 on WARP and NVIDIA: a surface eye one metre above the 4,096 m reference sphere, the existing `SkyFixture` profile, and solar elevations from −90° through −12°, −6°, −3°, −1°, 0°, 1°, 3°, 6°, 12° to 45°. The tests compare unobstructed 9 × 5 horizon and upper-sky regions, require a warmer low-angle horizon than at 45°, a fading twilight and dark shadowed night, unchanged scene depth, and matching images when the same sun states are submitted in reverse order. A −18° to 3° sweep in 0.25° steps rejects abrupt sample-shadow bands: the maximum allowed regional channel step is 12/255. This is an image-regression tolerance for this fixed profile, not a performance limit or an accuracy claim for other profiles. Existing `SkyTests` retain dark-night star, planet/terrain occlusion, disabled output, grazing-ray, outer-shell and rebase checks.

The local 2026-11-20 calibration measured a maximum regional step of 9/255 on both Microsoft Basic Render Driver 10.0.26100.9711 and NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU driver 32.0.15.9653. At −6° the horizon mean RGB was approximately (119, 60, 12), at 0° (202, 156, 57), and at −90° (0, 0, 0). These workstation image observations do not establish the reference-class timing, full-route, handoff, memory or pressure gates above. All existing numeric gates remain required, including direct GPU timestamps; readback elapsed time does not substitute for them.

### Qualification tooling and diagnostic results

`eng/Measure-SkyQualification.ps1` requires `GROUNDWORK_TWILIGHT_PREVIEW=1`, builds a clean reviewed source checkout and
collects direct 1920 × 1080 sky/frame query results after fence retirement.
Strict mode fixes five alternating pairs and 2,000 retained samples per variant
and phase in each pair, with 64 warmups. It retains raw samples, summaries,
failed/partial counts, adapter/driver/CPU/source identity, build/test logs and
hashes in a fresh output directory. `-LocalProbe` and `-AllowDirty` are explicit
development modes, not reference qualification; `-SkipBuild` is restricted to
local probes. Limits remain 0.8/1.2 ms sky p95/p99 and 0.10 ms recording CPU p99,
zero warmed recording allocations and one native sky draw when enabled.

The fixture uses the existing `SkyFixture` profile (4,096 m radius, 256 m
shell, 72/24 m scale heights), not WYU's 112 m shell/40/14 m profile. Its CPU
interval includes target binding, sky constants and recording but excludes
application preparation. Its resource ledger does not observe all driver
PSO/root-signature memory. Even a passing strict fixture cannot close installed
route, total-frame relative, handoff, full memory or protected-work gates.

A short, ordered RTX PRO 5000 workstation comparison retained 768/768 measured
rows and 384/384 warmups for each baseline/candidate run. It used one pair and
128 samples per variant/phase, not the reference protocol. Actual sky costs:

| Phase | Baseline p95 / p99 ms | Candidate p95 / p99 ms |
| --- | ---: | ---: |
| Surface | 3.826 / 4.991 | 3.673 / 4.306 |
| Ascent | 3.915 / 5.543 | 4.364 / 5.970 |
| Orbit | 3.236 / 7.802 | 2.967 / 3.219 |

Both exceed the unchanged 0.8/1.2 ms fixture thresholds. The first, separate
candidate probe also exceeded them (surface 2.250/4.187 ms, ascent
4.364/4.635 ms, orbit 1.366/3.939 ms). All runs remain retained under
`artifacts/sky-qualification/`, including the ordered comparison directory
`comparison-20261120T003959Z`. Distinct shader binary hashes verified the
baseline/candidate builds, and the exact candidate shader was restored.
These exploratory runs do not establish regression causality or reference
acceptance. No shader quality or acceptance limit was reduced to obtain a pass.

The real WYU integration now has a separate opt-in reference runner in
DaveBeusing/wyu#83. It keeps canonical cameras and B/E inputs fixed while
removing only the submitted sky in the off variant. Owner-thread hooks report
retired direct-query timings and successful native-Present timestamps; these
hooks remain inactive in ordinary use and preserve the public API and existing
WP15 CSV format. See WYU's `docs/dawn-twilight/reference-qualification.md` for
the owner's reference-machine run, its exact sample/handoff denominators and
remaining evidence gates. A smoke result is plumbing validation, not budget
acceptance, and an isolated DLL is not a master-produced immutable archive.

The combined renderer includes the full PR #186 presentation/refinement work.
Private qualification hook schema 2 includes both logical sequence and actual
native draw fence in CPU recording and retired GPU observations. Appearance
updates can redraw an acknowledged logical frame: each native execution must
remain identifiable instead of being conflated by logical sequence. WYU retains
every execution. Smoke may report extras, but a strict controlled phase with
additional executions remains blocked instead of selecting or dropping a row.
