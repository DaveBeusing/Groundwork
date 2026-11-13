cbuffer Frame : register(b0)
{
    row_major float4x4 WorldViewProjection;
    float4 Tint;
    float4 FogColorOpacity;
    float4 FogDepth;
    float4 SunDirection;
    float4 SunDirect;
    float4 SunAmbient;
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR;
    float ViewDepth : TEXCOORD0;
    float3 Normal : NORMAL;
};

VertexOutput VSMain(float3 position : POSITION, float4 color : COLOR)
{
    VertexOutput output;
    output.Position = mul(float4(position, 1.0), WorldViewProjection);
    output.Color = color;
    output.Normal = 0;
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

float4 PSMain(VertexOutput input) : SV_Target
{
    float4 color = Tint * input.Color;
    if (SunDirection.w > 0)
    {
        float magnitude = dot(input.Normal, input.Normal);
        if (magnitude > 0)
            color.rgb *= SunAmbient.rgb + SunDirect.rgb *
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
