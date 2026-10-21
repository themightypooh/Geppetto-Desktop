# 1. Real material/texture rendering — M1 to M4

Do not assign this entire file to one small worker. Read RULES.md and CONTRACTS.md first.
No UI. Do not redesign the lights to hide a material bug.

## Existing seams to inspect

- `ViewportMesh.cs`: `Extract`, `DrawBatch`, `FloatsPerVertex`, `Upload`, `DrawBatched`.
- `Program.cs`: shared `VertexSrc` / `FragmentSrc`; `SceneRenderer.cs`: shader/sampler setup.
- `KeyShadowMap.cs`: depth-only pass using the same skinning vertex shader.
- `MaterialPalette.cs`: temporary fallback values, not actual texture support.
- Kernel `../geppetto/Effigy/PolyMesh.cs`: `Face.UVs` per corner.
- Kernel `PngReader.Read(byte[])`: RGBA, top row first; supports only the documented PNG subset.
- `SurveyPlayerAssembly.cs` and the read-only source `.vmdl` material remaps. Raw FBX material
  slot names alone do not provide texture paths or reproduce a game's custom shader.

## M1 — CPU-only material descriptors and resolution

**Prerequisite:** P1's resolver contract for integration; fixtures may use a temporary root.
**Own:** new material descriptor/resolver files and a separate CPU test file. No shader edits.

1. Implement the proposed `MaterialDescriptor` / `TextureBinding` contract: stable material
   identity, base color factor, roughness/metallic factors, emission factor, optional channel
   asset references, UV set index, wrap/filter and normal-map convention. No GL handles here.
2. Resolve material slot → descriptor explicitly. Preserve original slot ID/name. Material
   names that happen to match across two documents must not share overrides accidentally.
3. Use explicit project overrides/known adapters first. If supporting `.vmat` as source data,
   parse only declared supported keys (`TextureColor`, `TextureNormal`, `TextureRoughness`,
   `TextureMetalness` etc.); report unsupported shader semantics. Never load an engine shader.
4. Decode files on a worker thread. Initially support the PNG subset the kernel actually
   reads; other formats get a clear diagnostic, not a wrong-color placeholder marked success.
5. Missing optional maps use documented neutral defaults with a one-time diagnostic naming
   the material and missing path. Missing required overrides fail that material resolution.
   Distinguish “no map authored” from “authored map could not be read.”

**Required tests:** two same-named materials in different roots; renamed project directory;
missing/corrupt PNG; unsupported paletted PNG; explicit relative override; unknown shader;
albedo vs data color-space metadata; no source file writes. No GPU needed.
**Done:** descriptor resolution is used by a fixture harness; no guessed filename-only maps.

## M2 — UV stream + GPU albedo sampling

**Prerequisites:** M1; coordinator owns the shared vertex-layout integration.
**Own when leased:** `ViewportMesh.cs`, shared shaders, texture resource class, renderer bindings.

1. Carry `Face.UVs[c]` alongside corner normals in `Extract`; never read UVs by vertex index.
2. Add a UV attribute without renumbering position=0, normal=1, bone IDs=2, weights=3.
   Proposed UV location is 4. Update stride, offsets, upload size and every layout test together.
3. Decode CPU pixels off-thread; upload/share/delete GL textures only on the GL owner thread.
   Cache by resolved asset identity + content revision + color-space/sampler interpretation,
   not just by basename. Release resources when the last snapshot stops referencing them.
4. Use an sRGB internal format for albedo so sampling supplies linear RGB. Keep lighting and
   factors linear. Do not manually decode an already sRGB-decoded sample a second time.
5. Bind albedo per material batch, with a neutral white fallback texture. Texture unit 0 is
   already the key-light shadow map; reserve a different unit and keep bindings explicit.
6. Define image-row/UV orientation once. Do not flip the FBX reader, image upload AND shader.
   Keep the depth-only pass working after the stride changes.

**Required tests:** asymmetric four-corner texture orientation; per-corner UV seam with shared
position; two materials with different maps; mapless model unchanged; two textures sharing a
filename but different content; reload does not leak or use deleted GL textures. Compare
selected pixels/regions in a windowless EGL fixture, not only whole-image “different” hashes.
**Done:** an authored color map is demonstrably sampled in both shared-renderer entry points.

## M3 — Tangents and normal/roughness/metallic/emission maps

**Prerequisite:** M2. Keep this separate from the initial UV/albedo ticket.

1. Add per-corner tangent + handedness (proposed attribute 5, vec4) respecting hard-normal
   splits and mirrored UV islands. Inspect kernel `NormalBake.ExportTangents` before writing
   another tangent generator. Handle degenerate/missing UVs without NaNs.
2. Skin/transform the tangent frame consistently with the normal. Re-orthogonalize the tangent;
   bitangent sign matters. Nonuniform scale needs the correct normal transformation.
3. Decode tangent-space normals as linear data; choose/document OpenGL +Y or invert the green
   channel only where the descriptor explicitly asks. A flat normal map must be neutral.
4. Roughness/metallic/masks use linear formats. Define which source channel is sampled;
   do not assume an ORM-packed map when the material specifies separate maps.
5. Apply factor × sample exactly once. Preserve existing clamps and energy-conserving lighting.
   Emission is not a light source and must not bypass output color conversion accidentally.
6. Keep neutral fallbacks: flat normal, roughness/metallic neutral multiplier, black emission.
   Do not infer transparency solely from a PNG alpha channel; blending is another capability.

**Required tests:** flat normal equivalence; tilted normal changes the intended light-facing
side; mirrored UVs; degenerate UVs; nonuniformly scaled bone; roughness highlight-width change;
metallic 0/1 diffuse/specular behaviour; independent emission; no NaNs/GL errors.
**Done:** all channels are actually consumed, not just loaded/bound and left unused.

## M4 — Real asset integration and resource-budget check

**Prerequisites:** M1–M3. This is review/integration, not a new shader rewrite.

1. Create explicit read-only material mappings for Survey suit + separate head/neck, and test
   a non-Survey Effigy document. Remove only superseded fallback mappings, not other work.
2. Confirm skinning, picking and shadow pass still use the same mesh/pose after the layout change.
3. Record decoded memory + GPU texture storage, load/decode/upload times, batch count and
   repeated reload resource counts. No texture decode or material lookup per rendered frame.
4. Retain headless captures + pixel probes. Document unsupported game-specific shader features
   (Survey's custom palette/wear effects are not automatically native PBR features).

**Acceptance:** CPU tests + `python3 tests/gpu-smoke.py` + new material EGL fixture checks pass.
No `.vmat_c`, engine install or runtime download required; no authored source texture changes.
Do not mark renderer parity with the game/CPU renderer proved by this ticket.
