# hammer-compile-packing-cubemap-build fixtures

Sealed room (same 288x288x128 shell as the native-pipeline `sealed-room.json`
and the cubemap `room-with-probe.json`), plus the asset references the pack
stage resolves:

- floor `concrete/concretefloor`, ceiling + walls `brick/brickwall` (2 materials)
- 1 light, player start at (0, 0, 32)
- 1 probe `room_probe` at (0, 0, 64), resolution 32
- models: `models/props/crate.mdl`
- sounds: `sounds/beep.wav`
- particles: `spark_burst` (definition `particles/spark.json`)

The asset files in this directory are existence anchors only — a few
placeholder bytes, not parseable models/sounds. The pack stage checks
that a registered asset's file EXISTS under the project root; it never
parses the file. Unused registry entries (registered in the tests, never
referenced here) must stay out of the package.
