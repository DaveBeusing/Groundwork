# INT-01 visual fixture

Run from the Groundwork root on Windows. Use the two invocations shown so the qualified WARP and NVIDIA cases get a fresh test host before enabling the D3D12 Debug Layer.

```powershell
$env:GROUNDWORK_INT01_CAPTURE_DIR = Join-Path $PWD 'artifacts/int01-visual-captures'
dotnet test tests/Groundwork.Rendering.D3D12.Tests -c Release --filter 'FullyQualifiedName~WarpCanonicalPlaneMappedPixelsMatchAcrossNearOrbitAndRebase'
dotnet test tests/Groundwork.Rendering.D3D12.Tests -c Release --no-build --filter 'FullyQualifiedName~CanonicalPlaneMappedPixelsMatchWithDebugLayerAndDred'
```

Each adapter and diagnostics mode has its own directory. Without `GROUNDWORK_INT01_CAPTURE_DIR`, the test compares the same GPU readbacks without writing files. An export contains five full 512×512 BMP images for mapped near, mapped orbit, both rebased views, and the unmapped orbit proxy. Pixels come from the production D3D12 offscreen path in top-down order. The BMP files store BGR8 from the linear RGBA8 target, with no gamma conversion. The test separately verifies alpha and depth.

The manifest records the fixture descriptor and seed, voxel plan hash, individual cooked asset IDs, camera and origin, projection, sunlight, adapter and driver, embedded DXIL SHA-256 values, and the SHA-256 of every BMP. The fixed plane uses a camera at `(0, 0, 16)` facing `−Z` with a vertical field of view of `2 × atan(0.5)` and ambient-only lighting. The camera and light stay fixed as the origin changes. Rock and ore probes are compared to their per-channel pixel goldens. The maximum-radius proxy case verifies numerical fallback while its maps are resident.

The six canonical cube patches also supply one shared interior control on each of the 12 edges and eight three-chart corners. The cube half extent is 8.25 metres, and the free axis of each edge control is one quarter of that extent. These locations avoid point-sampler texel boundaries. The test renders each point on the assembled cube at 65×65. It checks center color against the CPU triplanar point-sampling reference and checks first-hit depth before and after rebase and a quarter-turn change in camera roll. These are point samples of the six level-zero charts. The mixed-LOD chart correspondence is covered separately by `MatchingChartControlsAgreeOnAllTwelveEdgesAndEightCornersAcrossTwoToOneLod` in `Groundwork.Voxels.Tests`.

The 1920×1080 window tests export the initial numeric and textured near and orbit Presents, plus the first Present of an edited near generation. The Save/Reload test exports its 160×96 baseline, edited and restored images with the overlay SHA-256. These captures use the same environment variable and write separate manifests for each route.

```powershell
dotnet test tests/Groundwork.Rendering.D3D12.Tests -c Release --no-build --filter 'FullyQualifiedName~Int01MixedVoxelMaterialsDrawAt1080p|FullyQualifiedName~OrbitAppearanceUsesTheAcceptedLeaseAndWaitsForProtectedFirstPresent|FullyQualifiedName~Int01SavedEditRestoresCanonicalGeometryAndTexturedPixels'
```

The shared-window fixture presents near, orbit, near again, and a near view with an open trench in one 1920×1080 Win32 swap chain. It compares retired pixels at the shared rock and ore controls and checks the changed trench depth. The device, palette, and resident maps persist across all four Presents. The windowed fixture uses a standalone presentation lease. The near and orbit window tests above verify the respective production authority and admission paths.

```powershell
dotnet test tests/Groundwork.Rendering.D3D12.Tests -c Release --no-build --filter 'FullyQualifiedName~CanonicalWindowPresentsTheSameMixedMaterialsAcrossNearAndOrbit'
```

The BMP hashes identify a particular capture. The test tolerances govern pixel comparisons across drivers. The separate [1080p timing fixture](../../benchmarks/Int01Materials.md) records the measured cost of the legacy and textured paths.
