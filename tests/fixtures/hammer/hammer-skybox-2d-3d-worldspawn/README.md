# hammer-skybox-2d-3d-worldspawn fixtures

Board: `hammer-skybox-2d-3d-worldspawn` (SkyboxWorldspawn,
`kernel/Effigy/Map/SkyboxWorldspawn.cs`).
Tests: `tests/SkyboxWorldspawnTests.cs`, group `skyboxworldspawn`.

## Files

- `skybox-worldspawn.vmf` — a room with full worldspawn skybox data:
  `skyname` `sky_day01_01`, `chaptertitle` "CHAPTER 1: FIRST LIGHT",
  `geppetto_title` "First Light" (Geppetto-native title key — see
  SkyboxWorldspawn.cs), `detailvbsp` `detail.vbsp` + `detailmaterial`
  `detail/detailsprites`, worldspawn fog keys (`fogstart` 128, `fogend`
  8192, `fogcolor` "180 200 255", `fogmaxdensity` 0.8, `fogenable` 1),
  a `sky_camera` at `512 512 512` with scale `16`, a 3D-skybox section
  box `512..672` on each axis in the `3D Skybox` visgroup (id 7), and an
  `info_player_start`. The main room box (`-512..512`, `0..256`) has its
  centroid ~818 units from the sky_camera — inside the 4096 spatial
  radius — so the fixture proves the visgroup rule wins over the spatial
  rule: only the section box is section data (1 brush, 6 faces).
  Solid/entity blocks follow the proven syntax of the
  hammer-preview-skybox-scene fixture (worker 24); the light_environment
  / env_sun / env_fog_controller entities were removed (lighting ticket's,
  not this ticket's) and the worldspawn skybox keys added.
- `missing-sky-camera.vmf` — the same map with the `sky_camera` entity
  removed. Validation must carry the named warning
  `skybox-missing-sky-camera` and no crash.
- `cases.json` — expected values for `skybox-worldspawn.vmf`, read by the
  fixture check in `SkyboxWorldspawnTests` (sky name, chapter, title,
  detail props, fog, camera origin/scale, section brush/face counts, bake
  bounds).

## Expected bake for skybox-worldspawn.vmf

Section box `512..672` about the sky_camera origin `512 512 512` at
scale 16: world bounds `512..3072` on each axis
(512 + (672 - 512) * 16 = 3072), 6 faces, material `NATURE/CLIFF`,
via `MapBakeExport.Bake` over `SkyboxBakeData.ToMapBakeBrushes`.
