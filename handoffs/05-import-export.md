# 5. Interchange without silent data loss — I1 to I4

Read RULES.md / CONTRACTS.md. No engine installation, no asset conversion in-place, no UI.

## Existing APIs, actually checked

- Kernel `FbxImport.Read(byte[])` → pieces, materials, skeleton, named weights, blendshapes
  and import notes. `Resolve` maps named weights to a skeleton. `FbxScene.Read` is not a
  skeletal animation/take sampler; it does not currently expose animation curves/stacks.
- `FbxWriter.WriteBinaryFile(mesh, path, skeleton, materialName, modelName, unitScale, ...)`
  exports mesh/rig. Default binary units are declared as **2.54 cm per kernel inch**.
- `ObjWriter.WriteFile` exports static geometry/material names, not rigs or animation.
- `DmxWriter.WriteFile` and `DmxAnimWriter.WriteFile(path, skeleton, AnimClip, modelName)`
  are existing optional Source 2 targets. They do not require the desktop to become s&box.
- `RigAnimWriter.Write(clip, baseJson)` writes native clips; `RigAnimFile.ToClip` samples
  kernel-evaluated poses for animation export. Check its uniform-scale handling before use.
- `ViewportMesh.Upload` frees its CPU vertex/index arrays. **Export retained source data,
  not the flattened transient GPU buffers.**

## I1 — Export service, capability report and native/static outputs

**Prerequisites:** E1, P1; E4's staged/atomic write primitive where applicable.
**Own:** proposed `ExportService`, format adapters, CPU fixture tests.

1. Define export request: document/session revision, target format, destination, bind vs
   posed/static output, material/texture policy and overwrite policy. No implicit destination.
2. Return a capability/loss report before writing: geometry, units/axes, rig, skin, morphs,
   animation, tangents, textures and unsupported features. A format extension is not a promise.
3. Wire native `.effigy`/`.riganim` where valid and static OBJ as a small first slice.
   OBJ requests for animation must fail or require explicit “bake this pose to static mesh.”
4. Stage multi-file OBJ/material/texture outputs into a temp directory. Validate names/paths
   so a material name cannot escape the export directory. Do not overwrite source textures.
5. Export a captured revision off-thread, report progress/cancellation, and leave the session
   and original assets unchanged. Byte-identical inputs should produce deterministic output
   except for documented metadata such as a user-requested timestamp.

**Tests:** static triangle/UV/material fixture; explicit posed vs bind export; unsupported rig
request to OBJ; refused overwrite; cancellation/error preserves old output; path traversal in
material name; unknown clip JSON metadata; source/session hashes unchanged.

## I2 — Rigged binary FBX + optional DMX adapters

**Prerequisite:** I1. Not permission to claim FBX animation export.

1. Use the existing kernel writer with explicit bind skeleton/skin and material resolver.
   Prefer binary FBX for general interchange. Do not use ASCII writer defaults accidentally:
   engine-facing historical unit assumptions differ from declared binary units.
2. Round-trip through `FbxImport` and compare units, orientation, hierarchy, bind transforms,
   weights, materials and UVs. Compare semantic geometry, not serialized text ordering alone.
3. Preserve supported morphs only after checking the actual writer path and a round-trip
   fixture; otherwise report unsupported. Do not silently strip them from a requested export.
4. DMX remains an optional adapter using existing writers. For animation, sample using the
   kernel path with IK/limits/root-motion policy explicit. Record scale limitations of the
   destination. Do not advertise nonuniform bone scale if the destination cannot represent it.
5. Do not cull bones/rebind vertices merely to fit the desktop's 220-bone display limit.
   File-format/export limits and viewport limits are different capabilities.

**Tests:** one kernel inch stays one inch; asymmetric axis fixture; rotated bind hierarchy;
four-weight round-trip; >4 influences explicit pruning policy; named materials; static file
does not invent a skeleton; optional DMX parsed/inspected with a numeric sampled-pose fixture.
**Done:** capability table truthfully states which outputs include animation; no invented FBX take.

## I3 — FBX animation import feasibility + a reference fixture

**Prerequisite:** none beyond source inspection. **Expert/research ticket**; it produces a
bounded design/fixture contract, not a half-working importer for arbitrary FBX.

1. Inspect `FbxReader.cs`/`FbxImport.cs` and enumerate missing animation structures:
   stacks/layers, time ranges/rates, curves, node transform evaluation, pivots, pre/post
   rotations, rotation order, inheritance and scale. Bind matrices alone do not solve this.
2. Choose a supported first subset or propose a vetted importer dependency with license,
   deployment cost and engine-free suitability. New dependency/kernel edits need coordinator
   approval before implementation. Do not download/execute arbitrary asset-provided code.
3. Produce/reuse a tiny owned animation fixture plus independently verified poses at stated
   times, including a nontrivial parent rotation and an axis/unit conversion case. Golden
   transforms must not be generated by the very importer under test.
4. Record precisely how FBX time units, source rates and Euler order become kernel-local
   `RigPose` keys. State unsupported layered/pivot cases and how they will be rejected.

**Done:** approved design + fixture/golden data + import capability boundary. No production
claim until I4. In particular, do not call an imagined `FbxImport.ReadAnimation` API.

## I4 — Implement the approved animation subset and native clip conversion

**Prerequisites:** I3 approved; I1 for safe native output; kernel scope explicitly authorized
if required. Keep this assigned to an experienced worker.

1. Implement only I3's declared transform/timing subset. Unsupported constructs produce a
   structured error listing node/take/feature; they must not be flattened to identity silently.
2. Select the take explicitly when ambiguous. Map targets by skeleton identity/name with
   a report of missing/duplicate targets; do not guess retargeting from similar bone names.
3. Convert units/axes once, then derive local animation from the evaluated parent/world
   transforms consistently. Bind pose, source model placement and animated local pose are
   separate transforms. Preserve handedness/scale without improvised axis rotations.
4. Generate `.riganim` to an explicit new destination; preserve fractional-time pose
   accuracy within documented tolerance. Report resampling/key-reduction loss separately.
5. Exercise the resulting clip in the headless desktop and compare the sampled world poses
   against I3's independent reference. A changing screenshot is supplementary evidence only.

**Tests:** all I3 goldens; different source rate; first/last frame; multiple takes; missing bone;
nonidentity parent; unit/axis asymmetry; unsupported pivots/layers; save/reopen; original FBX
unchanged. Do not claim arbitrary FBX animation interoperability from the first subset.
