# hammer-preview-skybox-scene fixtures

Board: `hammer-preview-skybox-scene` (SkyboxPreview, `kernel/Effigy/Map/SkyboxPreview.cs`).
Tests: `tests/SkyboxPreviewTests.cs`, group `skyboxpreview`.

## Files

- `skybox-room.vmf` — a room with a 2D sky (`worldspawn` `skyname`
  `sky_day01_01`), a `sky_camera` at `512 512 512` with scale `16`, a
  3D-skybox section box `512..672` on each axis in the `3D Skybox`
  visgroup (id 7), a `light_environment` + `env_sun` pair, and an
  `env_fog_controller`. The main room box (`-512..512`, `0..256`) has
  its centroid ~818 units from the sky_camera — inside the 4096 spatial
  radius — so the fixture proves the visgroup rule wins over the spatial
  rule: only the section box previews (6 faces).
- `missing-sky-camera.vmf` — the same map with the `sky_camera` entity
  removed. The preview must carry the named warning
  `skybox-missing-sky-camera` and no faces, not crash.
- `cases.json` — expected values for `skybox-room.vmf`, read by the
  fixture check in `SkyboxPreviewTests` (sky name, camera origin,
  scale, preview face count, transformed bounds).

## Expected preview for skybox-room.vmf

Section box `512..672` about origin `512 512 512` at 1/16:
bounds `512..522` on each axis (160 / 16 = 10). Sun direction from
`light_environment` angles `0 90 0` is `(0, 1, 0)`; fog from
`env_fog_controller` (`128`–`8192`, colour `180 200 255`, density `0.8`).
Zero warnings.
