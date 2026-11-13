cbuffer Frame : register(b0)
{
    row_major float4x4 WorldViewProjection;
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

float4 Shade(VertexOutput input, float visibility)
{
    float4 color = Tint * input.Color;
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

Texture2D<float> NearDepth : register(t0);
Texture2D<float> FarDepth : register(t1);

float4 VSDepth(float3 position : POSITION) : SV_Position
{
    return mul(float4(position, 1), WorldViewProjection);
}

float CompareShadow(Texture2D<float> map, float4 area, float3 receiver, float bias)
{
    float2 uv = (receiver.xy - area.xy) * area.z * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0) || any(uv >= 1) || receiver.z <= 0 || receiver.z >= 1)
        return 1;
    float2 pixel = uv * 1024 - 0.5;
    int2 base = (int2)floor(pixel);
    float2 weight = frac(pixel);
    float rows[2];
    [unroll] for (int y = 0; y < 2; y++)
    {
        float columns[2];
        [unroll] for (int x = 0; x < 2; x++)
        {
            int2 tap = base + int2(x, y);
            columns[x] = any(tap < 0) || any(tap >= 1024) || receiver.z - bias <= map.Load(int3(tap, 0)) ? 1 : 0;
        }
        rows[y] = lerp(columns[0], columns[1], weight.x);
    }
    return lerp(rows[0], rows[1], weight.y);
}

float4 PSShadow(VertexOutput input) : SV_Target
{
    float depth = input.ViewDepth;
    float visibility = 1;
    if (depth < 64)
    {
        float3 p = float3(dot(input.Local, LightX.xyz), dot(input.Local, LightY.xyz),
            (dot(input.Local, LightZ.xyz) - LightX.w) * LightY.w);
        float slope = 1 - saturate(dot(input.Normal, SunDirection.xyz));
        // Receiver bias converts a fixed metre offset and a slope term to shadow depth.
        float bias = (0.012 + 0.04 * slope) * LightY.w;
        if (NearShadow.w > 0 && (depth < 26 || FarShadow.w == 0))
            visibility = CompareShadow(NearDepth, NearShadow, p, bias);
        if (FarShadow.w > 0 && (depth > 22 || NearShadow.w == 0))
        {
            float farVisibility = CompareShadow(FarDepth, FarShadow, p, bias);
            visibility = NearShadow.w > 0 ? lerp(visibility, farVisibility, smoothstep(22, 26, depth)) : farVisibility;
        }
        visibility = lerp(visibility, 1, smoothstep(60, 64, depth));
    }
    return Shade(input, visibility);
}

float4 PSMain(VertexOutput input) : SV_Target
{
    return Shade(input, 1);
}
