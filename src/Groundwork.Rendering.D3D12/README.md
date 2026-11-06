# Groundwork Direct3D 12 renderer

`D3D12Renderer` implements `IRenderer` for a started `WindowsHostModule` on
Windows 11 x64. Pass the same runtime ownership and engine context used by the
host. Start the renderer before submitting frames and stop frame production
before teardown. `useWarp: true` explicitly selects the software adapter for
verification. Default construction selects a compatible hardware adapter.

Use `D3D12Renderer.CreateWithMeshes` to provide a fixed `RenderMeshPair` for
the lifetime of the renderer. The render owner uploads the pair at startup
and draws it with the camera, transform and tint in `RenderScene`. A frame
without a scene clears the target. See the [WP03 terrain contract](../../docs/architecture/wp03-two-chunk-terrain-contract.md).

Use `D3D12Renderer.CreateWithMeshReplacement` to opt in to WP04. Initialize
the renderer with Core's runtime stop control so the owner can observe its
admission fence. The initial pair is generation 1. Declare newer intent on
`MeshReplacement` before dispatching a build, then submit the complete
immutable pair for that generation. Read the installed and presented
generations from the same port. Accepted submission queues the pair. The
render owner installs it only after prior draw and presentation work retires.
See the [WP04 contract](../../docs/architecture/wp04-mesh-replacement-contract.md).

Use `D3D12Renderer.CreateWithMeshRegionReplacement` to opt in to WP05 with
a complete `RenderMeshRegion` of one to nine instances. Declare a generation
on `MeshRegionReplacement`, then submit the full ordered region. A successful
installation replaces every previous instance, including any omitted from the
new region. Use the last presented generation to track what reached the
window. The same admission fence and GPU retirement rules apply.
See the [WP05 contract](../../docs/architecture/wp05-walkable-region-contract.md).

The provider builds its cube shaders from the pinned and verified DXC tool.
The resulting package contains the embedded DXIL, the shader build provenance
and the TerraFX notice. Build and test with x64 PowerShell 7 and the repository's
.NET SDK. See the [toolchain decision](../../docs/architecture/wp02-native-toolchain.md)
and [renderer contract](../../docs/architecture/wp02-d3d12-renderer-contract.md).

```powershell
$tests = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
dotnet restore $tests --configfile NuGet.Config --locked-mode
dotnet test $tests -c Release --no-restore --filter 'TestCategory!=WindowsDesktopSmoke'
```

The standard suite includes mandatory offscreen WARP readback without a desktop
session. On an interactive desktop, also run the swap-chain smoke case.

```powershell
$desktop = 'TestCategory=WindowsDesktopSmoke&TestCategory!=D3D12RegionReplacementWarp&TestCategory!=D3D12RegionReplacementHardware'
dotnet test $tests -c Release --no-build --no-restore --filter $desktop
```

Run the terrain WARP and hardware filters in separate processes on an
interactive Windows desktop. With Windows Graphics Tools installed, verify
active Debug Layer, zero errors and zero live child objects in the TRX output.

```powershell
dotnet test $tests -c Release --no-build --no-restore --filter 'TestCategory=D3D12TerrainWarp' --logger trx
dotnet test $tests -c Release --no-build --no-restore --filter 'TestCategory=D3D12TerrainHardware' --logger trx
dotnet test $tests -c Release --no-build --no-restore --filter 'TestCategory=D3D12MeshReplacementWarp' --logger trx
dotnet test $tests -c Release --no-build --no-restore --filter 'TestCategory=D3D12MeshReplacementHardware' --logger trx
dotnet test $tests -c Release --no-build --no-restore --filter 'TestCategory=D3D12RegionReplacementWarp' --logger trx
dotnet test $tests -c Release --no-build --no-restore --filter 'TestCategory=D3D12RegionReplacementHardware' --logger trx
```

The WP05 desktop qualification requires an active Debug Layer and DRED.
Run each WP05 filter in a fresh test process so diagnostics are enabled
before its first device is created.
