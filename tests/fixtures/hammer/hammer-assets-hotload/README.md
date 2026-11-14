# hammer-assets-hotload fixtures

Source fixtures only — tests copy them into their own work directory and
edit the COPIES, never these files.

- `crate.obj` — unit-cube prop, 8 vertices / 6 faces.
- `crate-v2.obj` — same prop after an external edit, 12 vertices / 8 faces.
  Tests overwrite the watched copy of `crate.obj` with this content.
- `metal.vmt` / `metal-v2.vmt` — material definition; v2 changes
  `$basetexture` to `dev/metal2` and `$color` to `[1 0 0]`.
- `spark.json` / `spark-v2.json` — particle definition; v2 raises
  `count` 64 -> 128 and `lifetime` 1.5 -> 2.0.
- `beep.wav` / `beep-v2.wav` — tiny generated 8 kHz mono WAVs (binary
  assets: hotload tracks hash + length only, no parsed fields).
  Regenerate with: `python3 -c` (see `gen note` below) — the files are
  checked in, do not regenerate unless the format changes.

Watched project-relative paths used by the tests:
`models/props/crate.obj`, `materials/metal.vmt`,
`particles/spark.json`, `sounds/beep.wav`.

WAV gen note (8-bit mono, 8000 Hz, 0.1 s): RIFF header + 800 samples
of a 440 Hz sine (v1) / 880 Hz sine (v2).
