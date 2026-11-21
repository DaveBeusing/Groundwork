using System.Numerics;
using System.Diagnostics;
using TerraFX.Interop.DirectX;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    private const uint SkyConstantCount = 40;
    private ID3D12RootSignature* _skyRoot;
    private ID3D12PipelineState* _skyPipeline;

    private void DrawSky(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderSky sky,
        ref ShadowTimingLayout timingLayout)
    {
        if (_skyPipeline == null)
        {
            var started = _retirementHooks?.AfterSkyPipelineReady is not null ? Stopwatch.GetTimestamp() : 0;
            var reused = false;
            if (SkyLookupPrototypeEnabledForTests && _session is not null &&
                _session.TryAcquireSkyLookupPipelines(out var lookupRoot, out var lookupGraphics, out var lookupCompute))
            {
                _skyRoot = lookupRoot;
                _skyPipeline = lookupGraphics;
                _skyLookupCompute = lookupCompute;
                reused = true;
            }
            else if (!SkyLookupPrototypeEnabledForTests && _session is not null && _session.TryAcquireSkyPipeline(out var root, out var pipeline))
            {
                _skyRoot = root;
                _skyPipeline = pipeline;
                reused = true;
            }
            else
            {
                if (_skyRoot == null)
                    _skyRoot = SkyLookupPrototypeEnabledForTests ? CreateSkyLookupRoot() : CreateFrameRoot(SkyConstantCount);
                _skyPipeline = CreateFramePipeline(_skyRoot, sky: true, skyLookup: SkyLookupPrototypeEnabledForTests);
                if (!SkyLookupPrototypeEnabledForTests)
                    _session?.RetainSkyPipeline(_skyRoot, _skyPipeline);
            }
            _retirementHooks?.AfterSkyPipelineReady?.Invoke(started, Stopwatch.GetTimestamp(), reused);
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
            // Validated normalized scale heights are in [0.0001, 1], so
            // their reciprocals are finite and can replace per-sample divides.
            new(sky.RayleighScattering * radius, 1f / (sky.RayleighHeightMeters / radius)),
            new(sky.AerosolScattering * radius, 1f / (sky.AerosolHeightMeters / radius)),
            new(sky.RayleighExtinction * radius, 0),
            new(sky.AerosolExtinction * radius, 0), sky.StarSeed, _skyLookupFallbackMaskForTests ? 1u : 0u, 0, 0);
        if (SkyLookupPrototypeEnabledForTests)
            BuildSkyLookup(commands, in constants, ref timingLayout);
        commands->SetPipelineState(_skyPipeline);
        commands->SetGraphicsRootSignature(_skyRoot);
        commands->SetGraphicsRoot32BitConstants(0, SkyConstantCount, &constants, 0);
        if (SkyLookupPrototypeEnabledForTests)
            commands->SetGraphicsRootDescriptorTable(2, SkyLookupSrv());
        commands->DrawInstanced(3, 1, 0, 0);
        if (SkyLookupPrototypeEnabledForTests)
            FinishSkyLookup(commands);
        _retirementHooks?.SkyDrawRecorded?.Invoke();
        if (_retirementHooks?.AfterSkyRecordMeasured is not null)
            _skyRecordDrawCount++;
    }

    private readonly record struct SkyConstants(Vector4 EyeOuter, Vector4 ForwardTan, Vector4 RightAspect,
        Vector4 UpDisk, Vector4 SunEnergy, Vector4 RayleighInverseHeight, Vector4 AerosolInverseHeight,
        Vector4 RayleighLoss, Vector4 AerosolLoss, uint StarSeed, uint Pad0, uint Pad1, uint Pad2);
}
