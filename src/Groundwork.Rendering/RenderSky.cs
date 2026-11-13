using System.Numerics;

namespace Groundwork.Rendering;

/// <summary>Planet-centred perspective sky in metres, with coefficients in inverse metres.</summary>
public readonly record struct RenderSky(Vector3 EyeMeters, Vector3 Forward, Vector3 Up, float VerticalFieldOfView,
    float PlanetRadiusMeters, float AtmosphereRadiusMeters, float RayleighHeightMeters, float AerosolHeightMeters,
    Vector3 RayleighScattering, Vector3 AerosolScattering, Vector3 RayleighExtinction, Vector3 AerosolExtinction,
    Vector3 SunDirection, float SunRadiance, float SunAngularRadius, uint StarSeed)
{
    internal bool IsValid
    {
        get
        {
            if (PlanetRadiusMeters is not (>= 1 and <= 1e9f))
                return false;
            var radius = PlanetRadiusMeters;
            var eye = EyeMeters / radius;
            return eye.LengthSquared() is >= 0.81f and <= 4096f &&
                ((double)AtmosphereRadiusMeters - radius) / radius is >= 0.0001 and <= 1 &&
                RayleighHeightMeters / radius is >= 0.0001f and <= 1 &&
                AerosolHeightMeters / radius is >= 0.0001f and <= 1 &&
                VerticalFieldOfView is >= 0.01f and <= 3.13f &&
                Unit(Forward) && Unit(Up) && MathF.Abs(Vector3.Dot(Forward, Up)) <= 0.0001f &&
                Unit(SunDirection) && SunRadiance is >= 0 and <= 64 &&
                SunAngularRadius is >= 0.001f and <= 0.25f &&
                Coefficients(RayleighScattering, RayleighExtinction, radius) &&
                Coefficients(AerosolScattering, AerosolExtinction, radius);
        }
    }

    private static bool Unit(Vector3 value) => MathF.Abs(value.LengthSquared() - 1) <= 0.0001f;

    private static bool Coefficients(Vector3 scattering, Vector3 extinction, float radius) =>
        Channel(scattering.X, extinction.X, radius) && Channel(scattering.Y, extinction.Y, radius) &&
        Channel(scattering.Z, extinction.Z, radius);

    private static bool Channel(float scattering, float extinction, float radius) =>
        scattering >= 0 && extinction >= scattering && (double)extinction * radius <= 128;
}
