cbuffer Atmosphere : register(b0)
{
    float4 EyeOuter;
    float4 ForwardTan;
    float4 RightAspect;
    float4 UpDisk;
    float4 SunEnergy;
    float4 RayleighInverseHeight;
    float4 AerosolInverseHeight;
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
    vertex.Position = float4(vertex.Screen, 1, 1);
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
    return exp(-min(height * float2(RayleighInverseHeight.w, AerosolInverseHeight.w), 80));
}

float3 Transmission(float2 depth)
{
    return exp(-min(depth.x * RayleighLoss.rgb + depth.y * AerosolLoss.rgb, 80));
}

float SolarVisibility(float3 position)
{
    float radius = length(position);
    float horizon = sqrt(max((radius - 1) * (radius + 1), 0));
    // Approximate a finite solar disk's penumbra instead of switching the whole
    // atmosphere sample when its centre crosses the planet's tangent.
    float elevation = (dot(position, SunEnergy.xyz) + horizon) / radius;
    return smoothstep(-UpDisk.w, UpDisk.w, elevation);
}

float2 SolarDepth(float3 position, float distance)
{
    // Four-point Gauss-Legendre quadrature resolves the dense end of grazing
    // light paths without increasing the existing four solar density samples.
    const float4 nodes = float4(0.0694318442, 0.3300094782, 0.6699905218, 0.9305681558);
    const float4 weights = float4(0.1739274226, 0.3260725774, 0.3260725774, 0.1739274226);
    float2 depth = 0;
    [unroll] for (uint j = 0; j < 4; ++j)
        depth += Density(position + SunEnergy.xyz * (nodes[j] * distance)) * (weights[j] * distance);
    return depth;
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

float3 SkyEye()
{
    float3 eye = EyeOuter.xyz;
    if (dot(eye, eye) < 1)
        eye = normalize(eye);
    return eye;
}

// Shared direct oracle: both the fullscreen direct path and every lookup
// texel retain all eight view samples and four solar quadrature samples.
void IntegrateAtmosphere(float3 eye, float3 ray, float mu, float ground, out float3 light, out float2 viewDepth)
{
    float2 segment;
    light = 0;
    viewDepth = 0;
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
                float visibility = SolarVisibility(samplePosition);
                if (visibility > 0 && Sphere(samplePosition, SunEnergy.xyz, EyeOuter.w, solar))
                {
                    float2 solarDepth = SolarDepth(samplePosition, solar.y);
                    light += Transmission(viewDepth + density * 0.5 + solarDepth) *
                        (density.x * RayleighInverseHeight.rgb * rayleighPhase + density.y * AerosolInverseHeight.rgb * aerosolPhase) * visibility;
                }
                viewDepth += density;
            }
        }
    }
}

#ifdef SKY_LOOKUP
// Fixed angular tables, independent of the output target. No history is used:
// the compute dispatch refreshes both fields with this frame's constants.
static const uint LookupWidth = 256;
static const uint LookupHeight = 128;
static const float Pi = 3.14159265358979323846;
RWTexture2D<float4> LookupRadianceOut : register(u0);
RWTexture2D<float4> LookupTransmissionOut : register(u1);
Texture2D<float4> LookupRadiance : register(t0);
Texture2D<float4> LookupTransmission : register(t1);
SamplerState LookupLinearWrapClamp : register(s0);

void LookupBasis(float3 eye, out float3 up, out float3 x, out float3 y,
    out float horizon, out float ceiling, out bool outside)
{
    up = normalize(eye);
    x = SunEnergy.xyz - up * dot(up, SunEnergy.xyz);
    if (dot(x, x) < 0.000001)
        x = cross(abs(up.y) < 0.9 ? float3(0,1,0) : float3(1,0,0), up);
    x = normalize(x);
    y = cross(up, x);
    float radius = length(eye);
    horizon = -acos(saturate(1 / radius));
    outside = radius >= EyeOuter.w;
    ceiling = outside ? -acos(saturate(EyeOuter.w / radius)) : Pi * 0.5;
}

float LookupElevation(float v, float horizon, float ceiling, bool outside)
{
    float s = 2 * v - 1;
    // Outside the shell, half the rows resolve the entire thin atmospheric
    // annulus rather than spending them on empty space up to the zenith.
    if (outside && s >= 0)
        return lerp(horizon, ceiling, s);
    return horizon + s * abs(s) * (s < 0 ? horizon + Pi * 0.5 : Pi * 0.5 - horizon);
}

[numthreads(8,8,1)]
void SkyLookupCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= LookupWidth || id.y >= LookupHeight)
        return;
    float3 eye = SkyEye();
    float3 up, x, y;
    float horizon, ceiling;
    bool outside;
    LookupBasis(eye, up, x, y, horizon, ceiling, outside);
    float azimuth = (float(id.x) / LookupWidth) * (2 * Pi);
    float elevation = LookupElevation(float(id.y) / (LookupHeight - 1), horizon, ceiling, outside);
    float3 ray = cos(elevation) * (cos(azimuth) * x + sin(azimuth) * y) + sin(elevation) * up;
    float3 light;
    float2 depth;
    ray = normalize(ray);
    IntegrateAtmosphere(eye, ray, dot(ray, SunEnergy.xyz), Ground(eye, ray), light, depth);
    LookupRadianceOut[id.xy] = float4(light, 1);
    LookupTransmissionOut[id.xy] = float4(Transmission(depth), 1);
}

void SampleLookup(float3 eye, float3 ray, out float3 light, out float3 transmission)
{
    float3 up, x, y;
    float horizon, ceiling;
    bool outside;
    LookupBasis(eye, up, x, y, horizon, ceiling, outside);
    float2 horizontal = float2(dot(ray, x), dot(ray, y));
    float azimuth = dot(horizontal, horizontal) > 0 ? atan2(horizontal.y, horizontal.x) : 0;
    float u = frac(azimuth / (2 * Pi) + 1);
    float elevation = asin(clamp(dot(ray, up), -1, 1));
    if (outside && elevation >= ceiling)
    {
        light = 0;
        transmission = 1;
        return;
    }
    float delta = elevation - horizon;
    float span = delta < 0 ? horizon + Pi * 0.5 : ceiling - horizon;
    float offset = saturate(abs(delta) / span);
    if (!outside || delta < 0)
        offset = sqrt(offset);
    float v = 0.5 + (delta < 0 ? -0.5 : 0.5) * offset;
    // U samples are periodic; V includes both poles. Address texel centres so
    // fixed-function bilinear filtering matches the angular grid exactly.
    float2 coordinate = float2((u * LookupWidth + 0.5) / LookupWidth,
        (v * (LookupHeight - 1) + 0.5) / LookupHeight);
    light = LookupRadiance.SampleLevel(LookupLinearWrapClamp, coordinate, 0).rgb;
    transmission = LookupTransmission.SampleLevel(LookupLinearWrapClamp, coordinate, 0).rgb;
}
#endif

[earlydepthstencil]
float4 SkyPS(SkyVertex input) : SV_Target
{
    float3 ray = normalize(ForwardTan.xyz + ForwardTan.w *
        (input.Screen.x * RightAspect.w * RightAspect.xyz + input.Screen.y * UpDisk.xyz));
    float mu = dot(ray, SunEnergy.xyz);
    float sine = length(cross(ray, SunEnergy.xyz));
    float diskAA = max(length(fwidth(ray)), 0.00001);
    float disk = mu > 0 ? 1 - smoothstep(max(0, UpDisk.w - diskAA), UpDisk.w + diskAA, sine) : 0;
    float stars = Stars(ray);
    float3 eye = SkyEye();
    float ground = Ground(eye, ray);
    float3 light;
#ifdef SKY_LOOKUP
    float3 transmission;
    SampleLookup(eye, ray, light, transmission);
#else
    float2 viewDepth;
    IntegrateAtmosphere(eye, ray, mu, ground, light, viewDepth);
#endif
    light *= SunEnergy.w;
    if (ground > 1e9)
    {
        float fade = exp(-8 * dot(light, float3(0.2126, 0.7152, 0.0722)));
#ifndef SKY_LOOKUP
        float3 transmission = Transmission(viewDepth);
#endif
        light += transmission * (disk * SunEnergy.w + stars * fade * 1.6);
    }
    return float4(light / (1 + light), 1);
}
