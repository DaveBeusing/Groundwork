cbuffer Atmosphere : register(b0)
{
    float4 EyeOuter;
    float4 ForwardTan;
    float4 RightAspect;
    float4 UpDisk;
    float4 SunEnergy;
    float4 RayleighHeight;
    float4 AerosolHeight;
    float4 RayleighLoss;
    float4 AerosolLoss;
    uint4 Star;
};

struct SkyVertex
{
    float4 Position : SV_Position;
    float2 Screen : TEXCOORD0;
};

SkyVertex SkyVS(uint id : SV_VertexID)
{
    SkyVertex vertex;
    vertex.Screen = float2(id == 1 ? 3 : -1, id == 2 ? 3 : -1);
    vertex.Position = float4(vertex.Screen, 0, 1);
    return vertex;
}

// Stable roots for a normalized ray, with no interval for a tangent.
bool Sphere(float3 eye, float3 ray, float radius, out float2 roots)
{
    float b = dot(eye, ray);
    float c = dot(eye, eye) - radius * radius;
    float distance = length(cross(eye, ray));
    float discriminant = (radius - distance) * (radius + distance);
    roots = 0;
    if (discriminant <= 0)
        return false;
    float d = sqrt(discriminant);
    float q = -b - (b < 0 ? -d : d);
    float other = c / q;
    roots = float2(min(q, other), max(q, other));
    return true;
}

float Ground(float3 eye, float3 ray)
{
    float2 hit;
    if (dot(eye, eye) <= 1.0000003 && dot(eye, ray) < 0)
        return 0;
    return Sphere(eye, ray, 1, hit) && hit.x >= 0 && hit.y > 0 ? hit.x : 1e10;
}

float2 Density(float3 samplePosition)
{
    float height = max(length(samplePosition) - 1, 0);
    return exp(-min(height / float2(RayleighHeight.w, AerosolHeight.w), 80));
}

float3 Transmission(float2 depth)
{
    return exp(-min(depth.x * RayleighLoss.rgb + depth.y * AerosolLoss.rgb, 80));
}

uint Hash(uint v)
{
    v = (v ^ (v >> 16)) * 0x7feb352du;
    v = (v ^ (v >> 15)) * 0x846ca68bu;
    return v ^ (v >> 16);
}

float Stars(float3 ray)
{
    // Cube chart selection gives stable canonical directions without longitude singularities.
    float3 a = abs(ray);
    float3 chart = a.x >= max(a.y,a.z) ? ray.yzx : a.y >= a.z ? ray.zxy : ray;
    uint face = a.x >= max(a.y,a.z) ? 0 : a.y >= a.z ? 2 : 4;
    face += chart.z < 0 ? 1 : 0;
    float2 p = (chart.xy / abs(chart.z) * 0.5 + 0.5) * 128;
    uint2 cell = (uint2)min(floor(p), 127);
    uint h = Hash(cell.x + 128 * cell.y + 16384 * face + Star.x);
    float2 center = 0.25 + 0.5 * float2(h & 255, (h >> 8) & 255) / 255;
    float distance = length(p - (float2(cell) + center));
    float aa = clamp(max(length(ddx(ray)), length(ddy(ray))) * 64, 0.01, 0.1);
    return (h >> 24) < 9 ? 1 - smoothstep(0.045 - aa * 0.3, 0.045 + aa, distance) : 0;
}

float4 SkyPS(SkyVertex input) : SV_Target
{
    float3 ray = normalize(ForwardTan.xyz + ForwardTan.w *
        (input.Screen.x * RightAspect.w * RightAspect.xyz + input.Screen.y * UpDisk.xyz));
    float mu = dot(ray, SunEnergy.xyz);
    float sine = length(cross(ray, SunEnergy.xyz));
    float diskAA = max(length(fwidth(ray)), 0.00001);
    float disk = mu > 0 ? 1 - smoothstep(max(0, UpDisk.w - diskAA), UpDisk.w + diskAA, sine) : 0;
    float stars = Stars(ray);
    float3 eye = EyeOuter.xyz;
    if (dot(eye, eye) < 1)
        eye = normalize(eye);
    float ground = Ground(eye, ray);
    float2 segment;
    float3 light = 0;
    float2 viewDepth = 0;
    if (Sphere(eye, ray, EyeOuter.w, segment))
    {
        float start = max(segment.x, 0);
        float stepSize = max(0, min(segment.y, ground) - start) / 8;
        if (stepSize > 0)
        {
            float rayleighPhase = 0.0596831 * (1 + mu * mu);
            float aerosolPhase = 0.0286479 * pow(max(1.64 - 1.6 * mu, 0.04), -1.5);
            [loop] for (uint i = 0; i < 8; ++i)
            {
                float3 samplePosition = eye + ray * (start + (i + 0.5) * stepSize);
                float2 density = Density(samplePosition) * stepSize;
                float2 solar;
                if (Sphere(samplePosition, SunEnergy.xyz, EyeOuter.w, solar) && Ground(samplePosition, SunEnergy.xyz) >= solar.y)
                {
                    float sunStep = solar.y / 4;
                    float2 solarDepth = 0;
                    [unroll] for (uint j = 0; j < 4; ++j)
                        solarDepth += Density(samplePosition + SunEnergy.xyz * ((j + 0.5) * sunStep)) * sunStep;
                    light += Transmission(viewDepth + density * 0.5 + solarDepth) *
                        (density.x * RayleighHeight.rgb * rayleighPhase + density.y * AerosolHeight.rgb * aerosolPhase);
                }
                viewDepth += density;
            }
        }
    }
    light *= SunEnergy.w;
    if (ground > 1e9)
    {
        float fade = exp(-8 * dot(light, float3(0.2126, 0.7152, 0.0722)));
        light += Transmission(viewDepth) * (disk * SunEnergy.w + stars * fade * 1.6);
    }
    return float4(light / (1 + light), 1);
}
