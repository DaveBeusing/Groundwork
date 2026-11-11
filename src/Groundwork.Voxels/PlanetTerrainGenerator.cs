namespace Groundwork.Voxels;

/// <summary>Resolves a planet's exact generator and recipe before brick work is admitted.</summary>
public static class PlanetTerrainGenerator
{
    public static IPlanetBrickSource Create(PlanetDescriptor descriptor, PlanetLandformRecipe? landform = null,
        PlanetGeologyRecipe? geology = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.GeneratorVersion switch
        {
            PlanetDensityField.GeneratorVersion when landform is not null && geology is null =>
                new PlanetDensityField(descriptor, landform),
            PlanetGeologyField.GeneratorVersion when geology is not null && landform is null =>
                new PlanetGeologyField(descriptor, geology),
            _ => throw new ArgumentException("The generator version requires its matching recipe.", nameof(descriptor)),
        };
    }
}
