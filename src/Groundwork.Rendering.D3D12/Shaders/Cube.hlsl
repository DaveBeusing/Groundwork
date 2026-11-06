cbuffer Frame : register(b0)
{
    row_major float4x4 WorldViewProjection;
    float4 Tint;
};

float4 VSMain(float3 position : POSITION) : SV_Position
{
    return mul(float4(position, 1.0), WorldViewProjection);
}

float4 PSMain() : SV_Target
{
    return Tint;
}
