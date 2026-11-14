# hammer-model-particle-sound-registries fixtures

Source fixtures only — tests copy them into their own work directory and
edit the COPIES, never these files.

- `crate.mdl` — placeholder model bytes. Only existence is ever checked
  (registry validation + placement); the bytes are never parsed as a
  model. Registered as "Crate", `models/props/crate.mdl`, prop_static,
  attachments `handle` + `lid`, uniform scale.
- `barrel.obj` — barrel prop, 8 vertices / 6 faces. Registered as
  "Barrel", `models/props/barrel.obj`, prop_physics, non-uniform scale
  (1, 2, 0.5).
- `spark.json` — particle definition for system `spark_burst`
  (`particles/spark.json`, params count=64, lifetime=1.5).
- `beep.wav` — tiny generated 8 kHz mono WAV, copied from the
  hammer-assets-hotload fixture (same bytes: 0.1 s, 440 Hz sine).
  Registered as "Beep", `sounds/beep.wav`, volume 0.5.
- `registry-extra.json` — TEST-ONLY entity classes
  (info_particle_system with `effect_name`, ambient_generic with
  `message` + `volume`). These classes are NOT in core-source1.json;
  the P3 soundscapes ticket owns the real ones. The placement tests
  load this file with `EntityRegistry.LoadAdditional` to prove the
  placement path works when the class exists, and use the bare core
  registry to prove the UnknownEntityClass refusal when it does not.

Watched project-relative paths used by the tests:
`models/props/crate.mdl`, `models/props/barrel.obj`,
`particles/spark.json`, `sounds/beep.wav`.
