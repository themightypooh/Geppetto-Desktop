# hammer-preview-live-lighting fixtures

- `room.json` — one sealed interior room (256x256 floor, 128 high, inward normals)
  with a centre point light, an overhead sun and a light_environment ambient.
  The `LiveLightingTests` room-fixture check loads it, evaluates Full, and asserts
  the floor is brighter than the ceiling.
- `bake-reference.json` — per-face baked average luxels for the same room, recorded
  from this engine's own Full evaluation (see the note in that file). It is a
  regression snapshot guarding the preview numbers, NOT an independent bake:
  the progressive bake (hammer-lightmap-progressive-bake) has not landed, so no
  independent baked values exist yet. The real preview-vs-bake check is the
  analytic single-light case in the tests (tolerance 0.05) plus this snapshot;
  the final integrator re-records this file from the real bake when it lands.
