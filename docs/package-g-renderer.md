# Package G renderer implementation

The optional RenderSky frame value works on ordinary frames, paired volume cameras and held terrain masks. D3D12 clears the existing target, draws one fullscreen triangle with depth writes and tests disabled, then draws the terrain with its existing Package E fog. The sky pipeline and root signature are created on the first sky frame and retired with the device. The pass uses no additional textures, mesh uploads or resolution-dependent targets.

## Numeric limits

Radius is 1 to 1,000,000,000 m. Relative eye radius is 0.9 to 64. Shell thickness and both scale heights are 0.0001 to 1 times the planet radius. Scattering and extinction coefficients are nonnegative, with extinction at least scattering. The normalized extinction limit is 128 per channel. Vertical field of view is 0.01 to 3.13 rad, solar angular radius 0.001 to 0.25 rad, and solar radiance 0 to 64. Forward, up and sun direction accept a squared-length error of 0.0001. Forward and up accept an absolute dot product of 0.0001.

The pixel shader uses eight view samples and four solar samples per lit view sample. It applies Rayleigh and fixed forward aerosol phase functions with a bounded exponential and maps linear radiance with L/(1+L). A deterministic, seeded cube-chart star field uses the canonical ray. The solar disk uses the angular radius and a pixel footprint, and the inner sphere suppresses space sources behind it without affecting terrain depth.

The readback fixture uses a 4096 m planet, 4352 m atmosphere, 72 m Rayleigh scale height and 24 m aerosol scale height. See SkyTests for scattering values, fixture cameras and pixel regions. The performance budgets, live WYU handoff and reference qualification are tracked by the [contract](architecture/package-g-atmosphere-contract.md).
