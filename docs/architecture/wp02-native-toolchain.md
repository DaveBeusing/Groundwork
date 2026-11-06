# WP02 D3D12 native interop and shader toolchain

**Status** Proposed for owner review under the accepted
[WP02 renderer contract](wp02-d3d12-renderer-contract.md). The renderer
foundation landed in [PR #31](https://github.com/DaveBeusing/Groundwork/pull/31).
This proposal qualifies the first D3D12 provider toolchain on Windows 11 x64.

## Exact inputs

| Purpose | Proposed input | Package SHA-256 |
| --- | --- | --- |
| C# ABI bindings | [TerraFX.Interop.Windows `10.0.26100.6`](https://www.nuget.org/packages/TerraFX.Interop.Windows/10.0.26100.6) | `A02D560C0EF0D64541F5A746661D8A0C2BC98AC45C3A4C380B2F3BC038E1DAA7` |
| Build-time HLSL compiler and validator | [Microsoft.Direct3D.DXC `1.9.2609.5`](https://www.nuget.org/packages/Microsoft.Direct3D.DXC/1.9.2609.5) | `73CFA082245918C2CC1EC6740250EB7377E83A757B3B3A3E16E0D71535BFBFC3` |

The TerraFX package declares MIT and contains a .NET 10 managed assembly
with no package dependencies in its `net10.0` group. Its nuspec records
source commit `7d3e679e74be9da3584b4d4c4689e10422cc485f`. Carry its MIT notice
with the provider's third-party notices. DXC reports compiler build `01b62ad47`
from Microsoft's September 2026 release. Preserve the DXC package's
`LICENSE-LLVM.txt` and `LICENSE-MS.txt` with the build-tool record. The product
package ships the engine's compiled shader bytecode. The DXC binaries stay
in the build-tool directory. [TerraFX source](https://github.com/terrafx/terrafx.interop.windows/tree/7d3e679e74be9da3584b4d4c4689e10422cc485f) · [Microsoft DXC release](https://github.com/microsoft/DirectXShaderCompiler/releases/tag/v1.9.2609)

## Native boundary and restore

- Add the exact TerraFX `PackageReference` to `Groundwork.Rendering.D3D12`
  and commit the SDK-generated lockfile. Keep the binding types and unsafe
  code inside that provider. Its public API uses the accepted managed renderer
  and Windows target capability types.
- Use Windows' inbox D3D12 and DXGI. Verify resolution to the Windows system
  binaries. If bootstrap imports need explicit search policy, restrict them
  to System32. Use only the inbox ABI required for this milestone. Provider
  deployment requires neither Agility SDK binaries nor a native C++ helper.
- The render owner acquires and releases native references explicitly. Each
  successful COM creation or query transfers one reference to an identified
  owner. Release partial initialization in reverse order and hold the runtime
  lease until final cleanup or custody. Keep native pointers out of managed
  frame values. Borrow the HWND only under an admitted Windows operation.
- Require feature level 11_0 and separately query shader model 6.0 with
  `CheckFeatureSupport`. Report missing hardware capability clearly. WARP
  remains an explicit mode for qualification. [D3D12 feature levels](https://learn.microsoft.com/en-us/windows/win32/direct3d12/hardware-feature-levels) · [Shader model query](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_feature_data_shader_model)
- Extend the shipping dependency gate with the single exact TerraFX reference
  for this provider. In isolated consumption, map `Groundwork.*` to the
  verified local feed and TerraFX's exact ID to NuGet.org. Use a fresh cache,
  the committed lock and NuGet audit. Validate the TerraFX package against the
  reviewed digest and NuGet signature. Keep both the portable package and
  existing six-package archives consumable under their original rules.

## Shader build

Keep HLSL source and the explicit entry-point/profile table in the provider.
For the first pipeline use `VSMain` and `PSMain` with `vs_6_0` and `ps_6_0`,
HLSL 2021, Release optimization, warnings as errors and explicit row-major
matrix packing. The reviewed probe used `-HV 2021 -O3 -WX -Zpr`. Fix the
source/include set and compile without embedded debug paths in Release.

Fetch the exact signed DXC package from NuGet.org, verify its SHA-256 and
signature, then extract the x64 compiler, matching DLLs and validator to an
isolated tool directory. Invoke that compiler by absolute path. Avoid native
NuGet build imports and ambient SDK or `PATH` selection. Fail on missing or
changed tool bytes, compilation failure or invalid DXIL. Keep generated
bytecode in build outputs and embed it in the provider. Record the exact
compiler identity, arguments, source hashes and DXIL hashes in the build
provenance. Check byte equality across two clean checkout paths in Windows CI.

## Probe evidence and next gate

On Windows 11 x64 with SDK `10.0.403`, `dotnet nuget verify --all` passed for
both packages. An isolated, locked TerraFX restore created an inbox WARP
device at feature level 11_0 and reported shader model 6.0. The DXC probe
compiled both profiles from two paths and `dxv` validated all four outputs.
Vertex output SHA-256 was `2F8CA09EDA2E6BB7A976616038D5C4DF11B7DEAB985B3DDC35D10F5ED005F9B5`
and pixel output SHA-256 was `DB9E36980070AAB78CBB7FCB2550C98892CDFA6DF349F9554A590453D0945598`.
Those hashes describe the throwaway probe shaders.

After approval, add the provider and run the accepted contract's fake GPU
retirement tests, WARP offscreen clear/transform/depth readback, and interactive
hardware tests. The probe establishes compiler and device capability. The
provider still needs its own shader and resource-lifetime verification.
