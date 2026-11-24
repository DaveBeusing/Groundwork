using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Groundwork.Rendering.Materials;

namespace Groundwork.Rendering.D3D12;

internal readonly record struct GpuTerrainMap(D3D12TextureTarget Target, GpuTextureIdentity Identity, GpuFrameTextureImport Import);

/// <summary>One immutable render-owner palette cut, including the exact native textures read by its shader.</summary>
internal sealed class GpuTerrainMaterialSet
{
    internal const int Capacity = D3D12TextureHandoff.MaximumSlots;
    private const uint Missing = uint.MaxValue;
    private const int HeaderBytes = 16;
    private const int RowBytes = 64;
    internal const int PayloadBytes = HeaderBytes + (Capacity + 1) * RowBytes;
    internal const int ConstantStride = (PayloadBytes + 255) & ~255;

    private readonly byte[] _payload = new byte[PayloadBytes];
    private readonly D3D12TerrainTextureBinding[] _bindings;
    private readonly GpuTerrainMap[] _maps;

    internal GpuTerrainMaterialSet(D3D12TextureView view, MaterialPalette palette,
        ReadOnlySpan<D3D12TerrainTextureBinding> bindings, ReadOnlySpan<GpuTerrainMap> maps)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(palette.Entries.Length, Capacity);
        View = view;
        Palette = palette;
        _bindings = bindings.ToArray();
        _maps = maps.ToArray();
        Imports = maps.IsEmpty ? [] : [.. _maps.Select(static map => map.Import)];

        var count = checked((uint)palette.Entries.Length);
        MemoryMarshal.Write(_payload, in count);
        var rows = MemoryMarshal.Cast<byte, MaterialRow>(_payload.AsSpan(HeaderBytes));
        rows[0] = CreateRow(0, MaterialPalette.MissingMaterial, []);
        for (var i = 0; i < palette.Entries.Length; i++)
        {
            var material = palette.Entries[i];
            rows[i + 1] = CreateRow(material.MaterialId, material.Descriptor, maps);
        }
    }

    internal D3D12TextureView View { get; }
    internal MaterialPalette Palette { get; }
    internal ImmutableArray<GpuFrameTextureImport> Imports { get; }
    internal ReadOnlySpan<byte> Payload => _payload;
    internal ReadOnlySpan<GpuTerrainMap> Maps => _maps;

    internal bool Matches(D3D12TextureView view, MaterialPalette palette, ReadOnlySpan<D3D12TerrainTextureBinding> bindings) =>
        View == view && ReferenceEquals(Palette, palette) && bindings.SequenceEqual(_bindings);

    private static MaterialRow CreateRow(ushort id, PbrMaterialDescriptor descriptor, ReadOnlySpan<GpuTerrainMap> maps)
    {
        uint baseColor = Missing, normal = Missing, roughness = Missing, metallic = Missing;
        foreach (ref readonly var map in maps)
        {
            if (map.Target.Slot.Material != id)
                continue;
            var index = (uint)map.Identity.Slot;
            switch (map.Target.Slot.Semantic)
            {
                case MaterialTextureSemantic.BaseColor: baseColor = index; break;
                case MaterialTextureSemantic.Normal: normal = index; break;
                case MaterialTextureSemantic.Roughness: roughness = index; break;
                case MaterialTextureSemantic.Metallic: metallic = index; break;
            }
        }

        return new(new(descriptor.BaseColor, descriptor.Metallic), new(descriptor.Emissive, descriptor.AmbientOcclusion),
            descriptor.Roughness, descriptor.NormalStrength, id, descriptor.Textures.RoughnessInNormalAlpha ? 1u : 0u,
            baseColor, normal, roughness, metallic);
    }

    // b1. Header and fallback occupy the first five registers. One material uses four registers.
    [StructLayout(LayoutKind.Sequential, Size = RowBytes)]
    private readonly record struct MaterialRow(Vector4 BaseColorAndMetallic, Vector4 EmissiveAndOcclusion,
        float Roughness, float NormalStrength, uint Id, uint RoughnessInNormalAlpha,
        uint BaseColor, uint Normal, uint RoughnessMap, uint Metallic);
}
