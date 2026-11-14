# hammer-lighting-environment-sun-fog fixtures

Ticket: Hammer Desktop P2 lighting entities — light, light_spot, light_environment +
env_sun, fog, tonemap (board task `hammer-lighting-environment-sun-fog`).
Implementation: `kernel/Effigy/Map/LightEnvironment.cs` (namespace `Effigy.Map`).
Tests: `tests/LightEnvironmentTests.cs`, group `lightenvironment`.

## Files

- `lighting-source1.json` — the six lighting entity classes
  `core-source1.json` does not ship (light_environment, env_sun, light_dynamic,
  env_fog_controller, shadow_control, env_tonemap_controller), as registry JSON
  (schema v1, name `source1-lighting`). Byte-identical in content to
  `LightEnvironment.LightingRegistryJson` (a test asserts the embedded string
  parses to the same five classes). **Integrator:** copy this file into
  `kernel/Effigy/Map/registry/` — `EntityRegistry.LoadDirectory` then loads
  core classes + lighting (6) = the merged total, and the merged-registry test's
  expectation (core + 6 classes, all eight lighting class names) becomes the directory
  load's expectation. Do not edit the core file to add these classes; the
  lighting file is the additive registry the registry ticket designed for.
- `sun-room.json` — sealed 288x288x144 room (same six box brushes as the
  compilerunner `sealed-room` fixture) lit by ONE sun: a light_environment
  (pitch -90, overhead) paired with an env_sun at the same angles, plus one
  env_fog_controller, one shadow_control and one env_tonemap_controller.
  The sun-room compile test runs it through `CompileRunner` (Final preset):
  roof top face bakes lit, interior floor bakes black (shadowed by the
  ceiling — correct sun shadowing), overall lightmap mean > 0.

## Regenerating

`lighting-source1.json` is generated from the embedded string in
`LightEnvironment.cs` (extract the `LightingRegistryJson` raw string, strip
the two-tab base indent). If the two disagree, the
"embedded registry JSON matches the fixture file" test fails — fix the source,
regenerate the fixture, do not hand-edit the fixture alone.
