# hammer-radiosity-quality-bake fixtures

Analytic fixtures for the native radiosity bake. No fixture here is a snapshot
recorded from an engine's own output: every expected value in the tests is derived
by hand or from a textbook result, so a wrong bake cannot pass by agreeing with
itself. (worker 23's `hammer-preview-live-lighting/bake-reference.json` is
explicitly NOT ground truth for radiosity — it is a snapshot from LiveLighting's
own Full evaluation. The radiosity tests never read it.)

## two-room.json

Two 128x128x128 rooms sharing the wall at x=128, with a doorway (y 64..104,
z 0..96). One point light, intensity 4, low in the south-west corner of room A
at (32,16,32). No light_environment: ambient is zero, so any light in room B
that is not direct can ONLY be bounced light.

Room B's floor is a 2x2 luxel grid (lightmap scale 64), cell centres:

| luxel | centre | direct light? |
|-------|--------|---------------|
| (row 0, col 0) | (160, 32) | NO — the segment to the light crosses x=128 at y=28, inside wall piece y 0..64 |
| (row 0, col 1) | (224, 32) | NO — crosses x=128 at y=24 |
| (row 1, col 0) | (160, 96) | YES — crosses x=128 at y=76, z=8, inside the doorway |
| (row 1, col 1) | (224, 96) | NO — crosses x=128 at y=56, just south of the doorway (64) |

The tests assert: the three dark luxels have ~zero direct and non-zero indirect
(bounce), the lit luxel has direct > 0, and room A's floor is brighter than room
B's. The (1,1) case is the tightest (8 units from the doorway edge) and is exact
— the luxel centre is fixed, there is no sampling jitter.

## parallel-plates.json

Two 64x64 plates, 64 apart, facing each other (normals toward each other),
lightmap scale 4 (16x16 patches each). No lights: the form-factor test uses
`RadiosityBake.FormFactor` directly, not `Bake`.

Analytic anchor: summing the differential form factor over the patches of one
plate to the other converges to the exact double-integral value, ~0.1999 for
this geometry (verified against an independent fine-grid integration, and
against the two exact invariants the test also checks: a differential element
above a parallel disk of radius R at distance S has form factor exactly
R^2/(R^2+S^2), and form factors from one patch over a closed box sum to exactly
1). A commonly-quoted closed form for aligned parallel plates was checked
against the same numerical integration and did NOT reproduce it, so it is
deliberately not used here — do not reintroduce it without verifying it first.

## Entry point (P3, 2026-11-13)

The quality bake is reached from the progressive bake through
`LightmapBakeQuality` (kernel/Effigy/Map/LightmapBakeQuality.cs):
`BakeRadiosity(job, settings)` runs the radiosity engine on the job's
faces + lights; `PackAtlas(baked, atlasSize, padding)` packs its face grids
multi-page with a padding gutter; `LightPropVertices` lights static-prop
vertices from the bake; `VerifyGpuTrust` / `BakeGpuRadiosity` enforce the
1K test-bake trust rule on the quality path; `CompareToProgressive`
cross-checks the quality bake against the progressive refined bake. The
"radiosityquality" tests in tests/RadiosityQualityIntegrationTests.cs
exercise the bake through the job on this fixture; the engine's own analytic
tests are "radiositybake" in tests/RadiosityBakeTests.cs.
