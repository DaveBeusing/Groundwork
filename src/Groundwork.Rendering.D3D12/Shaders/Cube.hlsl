cbuffer Frame : register(b0)
{
    row_major float4x4 WorldViewProjection;
    float4 Tint;
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR;
};

VertexOutput VSMain(float3 position : POSITION, float4 color : COLOR)
{
    VertexOutput output;
    output.Position = mul(float4(position, 1.0), WorldViewProjection);
    output.Color = color;
    return output;
}

float4 PSMain(VertexOutput input) : SV_Target
{
    return Tint * input.Color;
}
