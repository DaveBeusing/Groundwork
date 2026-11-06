# Groundwork Direct3D 12 renderer

`D3D12Renderer` implements `IRenderer` for a started `WindowsHostModule` on
Windows 11 x64. Pass the same runtime ownership and engine context used by the
host. Start the renderer before submitting frames and stop frame production
before teardown. `useWarp: true` explicitly selects the software adapter for
verification. Default construction selects a compatible hardware adapter.

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
dotnet test $tests -c Release --no-build --no-restore --filter 'TestCategory=WindowsDesktopSmoke'
```
