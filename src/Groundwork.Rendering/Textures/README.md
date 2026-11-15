# Portable cooked textures, version 1

`TextureCooker` operates on memory spans in the portable .NET 10 rendering library. The caller owns the input and keeps it stable for the duration of a call. Successful calls return an immutable texture with read-only spans for the exact envelope, payload, and per-mip layout. Save `CanonicalBytes` and its `AssetId` together. `ReadCooked` rederives the layout and checks the envelope against the expected ID before copying it. Obtain the expected ID from the asset reference. This check establishes content identity. Source provenance is the SHA-256 of the original source bytes held separately in the manifest.

## Source and recipe

- **Tga32, source code 1.** The supported TGA 1.0 subset has an exact 18-byte header and tightly packed BGRA8 pixels. Image type is 2, pixel depth is 32, and the descriptor byte is `0x28` (top-left, left-to-right, eight alpha bits). The image ID length, color map type and fields, and X/Y origins are zero. Width and height are nonzero little-endian 16-bit integers. Extra bytes, including footers, are outside this subset. The cooker reorders BGRA to RGBA and retains the unfiltered top level.
- **Bc1Blocks, source code 2.** Supply width, height, and mip count with the raw BC1 blocks. The bytes contain every block of every declared level in level order, then row order, without row or level padding. Each 64-bit BC1 block encoding is legal, so source validation checks layout and exact byte count. The cooker preserves the compressed blocks, including the supplied lower mips.
- **Recipe 1.** Specify the color space (Linear = 1, Srgb = 2), map semantic (Color = 1, Data = 2, Normal = 3), and mip count. sRGB applies to Color RGB. Data, Normals, and all alpha channels use linear values. Mip count covers a contiguous prefix starting at level zero and ending at or before 1 × 1. Each successive dimension is the previous dimension halved with floor rounding, clamped to one. Row bytes are `width * 4` for RGBA8 and `ceil(width / 4) * 8` for BC1. The respective row counts are `height` and `ceil(height / 4)`. Each level starts directly after its predecessor.

TGA lower mips use a box filter weighted by the exact area each source texel occupies in the destination texel. This includes fractional coverage for odd dimensions. Linear Color and Data average each channel independently and round to nearest, with ties toward the higher byte. For sRGB Color, a fixed table decodes RGB to the IEC sRGB linear curve on `[0, 65535]`. The filter takes a weighted mean and selects the nearest linear table entry, with ties toward the higher byte. Alpha is averaged separately as straight alpha.

Normal RGB decodes each component as `2 * (byte / 255) - 1`. The filter averages the decoded vectors with the same area weights and normalizes the resulting vector. A zero sum uses +Z, encoded as `(128, 128, 255)`. Components are encoded by rounding `127.5 * (component + 1)` to the nearest byte, with ties toward the higher byte. Integer threshold comparisons give the same result without floating-point operations. For BC1, the source must already contain mips filtered for the declared semantic.

## Canonical envelope

Fields occur in the order below. Integers are nonnegative 32-bit little-endian values limited to `Int32.MaxValue`. Byte lengths count bytes and offsets are relative to the beginning of the payload. There is no padding.

| Field | Encoding |
| --- | --- |
| Magic, manifest version | ASCII `GWTX`, integer 1 |
| Importer | Byte length 24, ASCII `Groundwork.TextureCooker`, importer version 1 |
| Source | Source code, digest byte length 32, 32 raw SHA-256 digest bytes |
| Recipe version | Integer 1 |
| Texture metadata | Format (Rgba8 = 1, Bc1 = 2), color space, semantic, base width, base height, layer count fixed at 1 |
| Mip table | Level count, table byte length (20 × count), then one record per level from largest to smallest |
| Mip record | Width, height, offset, row bytes, level byte length |
| Payload | Total payload byte length followed by the tightly packed mip bytes |

Fixed overhead is 120 bytes, plus 20 bytes per mip. The asset ID is `sha256:` followed by the 64 lowercase hexadecimal SHA-256 digits of the entire canonical envelope. The recipe, tool identity and versions, source digest, layout, interpretation, and exact payload all participate in the ID. Paths, timestamps, SDK and machine details do not enter the envelope. Changes that affect canonical bytes require a versioned recipe, importer, or manifest update as appropriate.

## Limits and fixtures

`TextureLimits` requires positive caps for source bytes, decoded bytes, total canonical cooked bytes, each base dimension, and mip count. RGBA8 additionally inherits the TGA dimension limit of 65,535. Before allocation, the cooker checks actual input length, dimensions, mip count, every level's exact byte layout, and the complete envelope size against the cap and array length limit. TGA also charges the entire RGBA8 chain to the decoded cap. BGRA conversion and filtering write directly into the single owned cooked buffer. BC1 copies directly into that buffer without decoding. Caller-held input remains live during cooking, so variable-sized byte storage peaks at input bytes plus complete cooked bytes. When reading, it peaks at the caller's cooked input plus the immutable copy. Source and decoded caps apply to imports, while cooked reads enforce cooked bytes, dimensions, and mips. Bookkeeping adds a fixed 31-entry stack layout, one retained layout entry per mip, fixed-size digest work and strings, and object metadata. These limits are per call. A caller coordinating reads or imports in parallel must budget input retention and overlapping calls separately.

The Release test fixtures generate a 2 × 2 TGA in code from literal texels and a 5 × 7 BC1 mip chain from byte values 0 through 47. Both are synthetic, have no third-party source, and are covered by this repository's Apache-2.0 license. The MSTest goldens pin the source digest, asset ID, layout and payload and can be rerun in fresh test processes using the repository's .NET SDK and package locks.
