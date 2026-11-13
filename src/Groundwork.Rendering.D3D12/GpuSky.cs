using System.Numerics;
using TerraFX.Interop.DirectX;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    private const uint SkyConstantCount = 40;
    private ID3D12RootSignature* _skyRoot;
    private ID3D12PipelineState* _skyPipeline;

    private void DrawSky(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderSky sky)
    {
        if (_skyPipeline == null)
        {
            if (_skyRoot == null)
                _skyRoot = CreateFrameRoot(SkyConstantCount);
            _skyPipeline = CreateFramePipeline(_skyRoot, sky: true);
        }

        var radius = sky.PlanetRadiusMeters;
        var forward = Vector3.Normalize(sky.Forward);
        var right = Vector3.Normalize(Vector3.Cross(forward, sky.Up));
        var up = Vector3.Cross(right, forward);
        var constants = new SkyConstants(
            new(sky.EyeMeters / radius, sky.AtmosphereRadiusMeters / radius),
            new(forward, MathF.Tan(sky.VerticalFieldOfView * 0.5f)),
            new(right, (float)target.Width / target.Height),
            new(up, MathF.Sin(sky.SunAngularRadius)),
            new(Vector3.Normalize(sky.SunDirection), sky.SunRadiance),
            new(sky.RayleighScattering * radius, sky.RayleighHeightMeters / radius),
            new(sky.AerosolScattering * radius, sky.AerosolHeightMeters / radius),
            new(sky.RayleighExtinction * radius, 0),
            new(sky.AerosolExtinction * radius, 0), sky.StarSeed, 0, 0, 0);
        commands->SetPipelineState(_skyPipeline);
        commands->SetGraphicsRootSignature(_skyRoot);
        commands->SetGraphicsRoot32BitConstants(0, SkyConstantCount, &constants, 0);
        commands->DrawInstanced(3, 1, 0, 0);
    }

    private readonly record struct SkyConstants(Vector4 EyeOuter, Vector4 ForwardTan, Vector4 RightAspect,
        Vector4 UpDisk, Vector4 SunEnergy, Vector4 RayleighHeight, Vector4 AerosolHeight,
        Vector4 RayleighLoss, Vector4 AerosolLoss, uint StarSeed, uint Pad0, uint Pad1, uint Pad2);
}
