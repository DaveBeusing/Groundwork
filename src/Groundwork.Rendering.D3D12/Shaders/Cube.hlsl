cbuffer Frame : register(b0)
{
    row_major float4x4 WorldViewProjection;
#if defined(TERRAIN_PBR)
    float3 PbrBaseColor;
    float PbrMetallic;
    float4 FogColorOpacity;
    float3 FogDepth;
    float PbrOcclusion;
    float3 SunDirection;
    float PbrRoughness;
    float3 SunDirect;
    float PbrEmissiveRed;
    float3 SunAmbient;
    float PbrEmissiveGreen;
    float4 LightX;
    float4 LightY;
    float3 LightZ;
    float PbrEmissiveBlue;
    float4 NearShadow;
    float4 FarShadow;
    float3 PbrHalfEye;
#else
    float4 Tint;
    float4 FogColorOpacity;
    float4 FogDepth;
    float4 SunDirection;
    float4 SunDirect;
    float4 SunAmbient;
    float4 LightX;
    float4 LightY;
    float4 LightZ;
    float4 NearShadow;
    float4 FarShadow;
#endif
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR;
    float ViewDepth : TEXCOORD0;
    float3 Normal : NORMAL;
    float3 Local : TEXCOORD1;
};

VertexOutput VSMain(float3 position : POSITION, float4 color : COLOR)
{
    VertexOutput output;
    output.Position = mul(float4(position, 1.0), WorldViewProjection);
    output.Color = color;
    output.Normal = 0;
    output.Local = position;
    // With the right-handed perspective projection, clip W is forward depth in metres.
    output.ViewDepth = output.Position.w;
    return output;
}

VertexOutput VSNormals(float3 position : POSITION, float4 color : COLOR, uint packed : NORMAL)
{
    VertexOutput output = VSMain(position, color);
    int x = (int)(packed << 22) >> 22;
    int y = (int)(packed << 12) >> 22;
    int z = (int)(packed << 2) >> 22;
    output.Normal = float3(x, y, z) / 511;
    return output;
}

#if !defined(TERRAIN_PBR)
float4 Shade(VertexOutput input, float visibility, float4 albedo)
{
    float4 color = Tint * input.Color * albedo;
    if (SunDirection.w > 0)
    {
        float magnitude = dot(input.Normal, input.Normal);
        if (magnitude > 0)
            color.rgb *= SunAmbient.rgb + SunDirect.rgb * visibility *
                saturate(dot(input.Normal * rsqrt(magnitude), SunDirection.xyz));
        else
            color.rgb *= SunAmbient.rgb;
    }
    if (FogColorOpacity.a > 0.0)
    {
        float amount = FogColorOpacity.a * saturate((input.ViewDepth * FogDepth.z - FogDepth.x) * FogDepth.y);
        color.rgb = lerp(color.rgb, FogColorOpacity.rgb, amount);
    }
    return color;
}
#endif

Texture2D<float> NearDepth : register(t0);
Texture2D<float> FarDepth : register(t1);
SamplerState ShadowSampler : register(s0);
Texture2D<float4> MaterialTexture : register(t4);
SamplerState MaterialSampler : register(s1);

float4 VSDepth(float3 position : POSITION) : SV_Position
{
    return mul(float4(position, 1), WorldViewProjection);
}

float CompareShadow(Texture2D<float> map, float4 area, float3 receiver, float2 planeSlope, float bias)
{
    float2 uv = (receiver.xy - area.xy) * area.z * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0) || any(uv >= 1) || receiver.z <= 0 || receiver.z >= 1)
        return 1;
    float2 pixel = uv * 1024 - 0.5;
    float2 base = floor(pixel);
    float2 weight = pixel - base;
    float2 depthPerTexel = planeSlope * float2(2, -2) / (1024 * area.z);
    // Sample the exact center of the four-texel footprint. Hardware returns BL, BR, TR, TL.
    float4 depths = map.Gather(ShadowSampler, (base + 1) / 1024).wzxy;
    float topLeft = receiver.z - dot(weight, depthPerTexel);
    float4 corrected = topLeft + float4(0, depthPerTexel.x, depthPerTexel.y,
        depthPerTexel.x + depthPerTexel.y);
    // Match the previous lit-border behavior even if the corrected receiver is beyond the map's depth range.
    float4 insideX = step(0, base.x + float4(0, 1, 0, 1)) * (1 - step(1024, base.x + float4(0, 1, 0, 1)));
    float4 insideY = step(0, base.y + float4(0, 0, 1, 1)) * (1 - step(1024, base.y + float4(0, 0, 1, 1)));
    float4 lit = max(step(corrected - bias, depths), 1 - insideX * insideY);
    return lerp(lerp(lit.x, lit.y, weight.x), lerp(lit.z, lit.w, weight.x), weight.y);
}

float ShadowVisibility(VertexOutput input)
{
    float depth = input.ViewDepth;
    float visibility = 1;
    // Use the rasterized triangle plane rather than its smoothed lighting normal.
    // Derivatives must run before the varying cascade branches.
    float3 plane = cross(ddx(input.Local), ddy(input.Local));
    float lightNormal = dot(plane, LightZ.xyz);
    float normalLight = dot(input.Normal, SunDirection.xyz);
    if (depth < 64 && normalLight > 0 && lightNormal * lightNormal > 1e-8 * dot(plane, plane))
    {
        float3 p = float3(dot(input.Local, LightX.xyz), dot(input.Local, LightY.xyz),
            (dot(input.Local, LightZ.xyz) - LightX.w) * LightY.w);
        float2 planeSlope = -float2(dot(plane, LightX.xyz), dot(plane, LightY.xyz)) * (LightY.w / lightNormal);
        float slope = 1 - saturate(normalLight);
        // Receiver bias converts a fixed metre offset and a slope term to shadow depth.
        float bias = (0.012 + 0.04 * slope) * LightY.w;
        if (NearShadow.w > 0 && (depth < 26 || FarShadow.w == 0))
            visibility = CompareShadow(NearDepth, NearShadow, p, planeSlope, bias);
        if (FarShadow.w > 0 && (depth > 22 || NearShadow.w == 0))
        {
            float farVisibility = CompareShadow(FarDepth, FarShadow, p, planeSlope, bias);
            visibility = NearShadow.w > 0 ? lerp(visibility, farVisibility, smoothstep(22, 26, depth)) : farVisibility;
        }
        visibility = lerp(visibility, 1, smoothstep(60, 64, depth));
    }
    return visibility;
}

#if defined(TERRAIN_PBR)

float3 UnitOrZero(float3 vector)
{
    float scale = max(max(abs(vector.x), abs(vector.y)), abs(vector.z));
    if (scale == 0)
        return 0;
    return normalize(vector / scale);
}

float3 DirectBrdf(float3 normal, float3 view, float normalLight, float normalView)
{
    const float Pi = 3.141592653589793;
    const float DielectricF0 = 0.04;
    const float MinimumRoughness = 0.08;
    float3 halfway = UnitOrZero(view + SunDirection);
    float normalHalf = saturate(dot(normal, halfway));
    float viewHalf = saturate(dot(view, halfway));

    // Perceptual roughness is squared to give alpha. This expansion stays nonzero at N.H = 1.
    float roughness = max(PbrRoughness, MinimumRoughness);
    float alpha = roughness * roughness;
    float alphaSquared = alpha * alpha;
    float normalHalfSquared = normalHalf * normalHalf;
    float denominator = (1 - normalHalfSquared) + alphaSquared * normalHalfSquared;
    float distribution = alphaSquared / (Pi * denominator * denominator);

    // Exact, separable Smith GGX. Evaluate G/(4 N.L N.V) with the cosines cancelled.
    float viewTerm = normalView + sqrt(alphaSquared + (1 - alphaSquared) * normalView * normalView);
    float lightTerm = normalLight + sqrt(alphaSquared + (1 - alphaSquared) * normalLight * normalLight);
    float visibility = 1 / (viewTerm * lightTerm);

    float3 f0 = lerp(DielectricF0, PbrBaseColor, PbrMetallic);
    float edge = 1 - viewHalf;
    float edgeSquared = edge * edge;
    float3 fresnel = f0 + (1 - f0) * edgeSquared * edgeSquared * edge;
    float3 diffuse = (1 - fresnel) * (1 - PbrMetallic) * PbrBaseColor / Pi;
    return diffuse + distribution * visibility * fresnel;
}

float4 ShadeTerrainPbr(VertexOutput input, float shadow)
{
    // Ambient is a diffuse approximation. AO acts here only. The specular
    // lobe comes from the directional light; there is no environment specular input.
    float3 color = PbrBaseColor * (1 - PbrMetallic) * PbrOcclusion * SunAmbient +
        float3(PbrEmissiveRed, PbrEmissiveGreen, PbrEmissiveBlue);
    float3 normal = UnitOrZero(input.Normal);
    float3 view = UnitOrZero(PbrHalfEye - 0.5 * input.Local);
    float normalLight = saturate(dot(normal, SunDirection));
    float normalView = saturate(dot(normal, view));
    // The terrain keeps its two-sided rasterization. Direct reflection uses the hemisphere
    // of the packed normal, while diffuse ambient and emissive remain visible from either side.
    if (normalLight > 0 && normalView > 0)
        color += DirectBrdf(normal, view, normalLight, normalView) * SunDirect * (normalLight * shadow);
    if (FogColorOpacity.a > 0)
    {
        float amount = FogColorOpacity.a * saturate((input.ViewDepth * FogDepth.z - FogDepth.x) * FogDepth.y);
        color = lerp(color, FogColorOpacity.rgb, amount);
    }
    return float4(color, 1);
}

float4 PSPbrTerrain(VertexOutput input) : SV_Target
{
    return ShadeTerrainPbr(input, 1);
}

float4 PSPbrShadowTerrain(VertexOutput input) : SV_Target
{
    return ShadeTerrainPbr(input, ShadowVisibility(input));
}

#else

float4 PSShadow(VertexOutput input) : SV_Target
{
    return Shade(input, ShadowVisibility(input), 1);
}

float4 PSMain(VertexOutput input) : SV_Target
{
    return Shade(input, 1, 1);
}

float4 SampleMaterial(VertexOutput input)
{
    // Static TEX-02 test route. Planet-canonical mapping belongs to INT-01.
    return MaterialTexture.SampleLevel(MaterialSampler, input.Local.xy + 0.5, 0);
}

float4 PSTexture(VertexOutput input) : SV_Target
{
    return Shade(input, 1, SampleMaterial(input));
}

float4 PSShadowTexture(VertexOutput input) : SV_Target
{
    return Shade(input, ShadowVisibility(input), SampleMaterial(input));
}

#endif

struct PropReference
{
    float3 CellOffset;
    uint Payload;
};
ByteAddressBuffer PropPayloads : register(t2);
StructuredBuffer<PropReference> PropReferences : register(t3);

VertexOutput VSProps(float3 position : POSITION, float4 color : COLOR, uint instance : SV_InstanceID)
{
    PropReference reference = PropReferences[instance];
    uint offset = reference.Payload * 32;
    float3 translation = asfloat(PropPayloads.Load3(offset));
    uint2 rotation = PropPayloads.Load2(offset + 12);
    int4 packedRotation = int4((int)(rotation.x << 16) >> 16, (int)rotation.x >> 16,
        (int)(rotation.y << 16) >> 16, (int)rotation.y >> 16);
    float4 q = normalize(float4(packedRotation));
    float3 v = position * asfloat(PropPayloads.Load(offset + 20));
    float3 crossTerm = 2 * cross(q.xyz, v);
    uint rgba = PropPayloads.Load(offset + 24);
    float4 tint = float4(rgba & 255, (rgba >> 8) & 255, (rgba >> 16) & 255, rgba >> 24) / 255;
    return VSMain(v + q.w * crossTerm + cross(q.xyz, crossTerm) + translation + reference.CellOffset, color * tint);
}
