# WP15 sunlight and terrain normals

The [WP15 contract](architecture/wp15-sunlight-shadow-contract.md) defines the
sunlight API and the later cascades. This slice implements unshadowed direct
and ambient light for the existing D3D12 volume terrain draw.

RenderSunlight takes a direction toward the sun with squared length within
0.0001 of one and finite linear RGB gain channels in [0, 4]. An invalid
explicit value is rejected by frame validation before acceptance. With lighting
present, the terrain RGB is multiplied by ambient plus direct Lambert light,
then Package E fog is applied. Alpha and depth are unchanged. The sky remains
a background pass.

The volume path stores the unit normal as three signed 10-bit integers scaled
by 511, with two spare bits. Component rounding gives a vector error of at
most sqrt(3)/1022 before renormalization, or at most 0.10 degrees of angular
error. The shader renormalizes the interpolated vector. Zero, nonfinite or
unrepresentable source normals encode as zero and receive ambient only.
Canonical surface normals do not change under translation or rebase.
The format costs four additional GPU bytes per volume vertex. Mesh packing
charges these bytes in the retained and scratch CPU payload and the aligned
native geometry budget. Meshes without normals retain their 16-byte stride.

The WARP sunlight readback is required in Windows CI. The Windows test suite
also checks NVIDIA with Debug Layer and DRED, signed packing, LOD transition
vertices, rebase, optional frame validation, and fog ordering. Shadow maps,
caster selection and production performance qualification belong to follow-up
work under the WP15 contract.
