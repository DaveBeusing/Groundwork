# WP15 D3D12 terrain cascades

Set TerrainShadows on a sunlit VolumeCamera to use the installed volume
geometry as the caster set. Generic identity-world terrain frames can supply
RenderScene.ShadowOrigin in canonical metres. Unshadowed sunlight and legacy
frames preserve their existing rendering paths.

The two 1024-square D32 maps cover 0–26 m and 22–64 m clipped to the camera
interval. Comparison-filtered visibility blends across 22–26 m and fades
out over 60–64 m. A clipped or empty band uses the remaining map where
available. Each orthographic fit uses a camera-slice bounding sphere with
a two-texel guard band and canonical light-space snapping with ties to even.
The fitted XY extent is invariant under turns at a fixed lens. Projected
batch bounds select resident casters, including batches outside the camera
frustum. A shared depth range encloses the selected bounds with a metre of
padding on each side. The filter is a 2-by-2 bilinear depth comparison with
lit border taps and receiver bias of 0.012 m plus up to 0.04 m for slope.

The renderer records both cascades and the color pass from the same installed
mesh buffers in one command list. Shadow resources are lazy, surface-size
independent and share the existing graphics queue. The maps transition
between depth-write and pixel-shader-resource states. They stay in custody
until queue retirement or confirmed removal. Each depth allocation is capped
at 8 MiB. This is a per-map limit. Reference-class total peak memory and
performance qualification remain in the WP15 acceptance work.

The Windows tests use paired synthetic casters to compare sun direction,
split, rebase, replacement and Package E fog. The brush fixture renders
the first offscreen draw of each published volume revision with its cavity
roof excluded to isolate the edit's shadow on unchanged receivers. The
live edit regression separately checks successful first Present of the
edited generation with cascades enabled.
