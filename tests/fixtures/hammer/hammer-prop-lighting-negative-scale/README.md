# hammer-prop-lighting-negative-scale fixtures

`props.json` — four `prop_static` oil drums for the PropLightingTests group
(`proplighting`):

- **1, twin-unmirrored** — scale `(1, 1, 1)`, outward normal `(0, 0, 1)`.
- **2, twin-mirrored** — same model and origin as 1, scale `(-1, 1, 1)`,
  imported normal `(0, 0, -1)` (what the flipped winding produces). Must light
  identically to 1 after the correction, and must be the only prop named in
  the bake log's corrections.
- **3, double-negative-not-mirrored** — scale `(-1, -1, 1)`, determinant `+1`:
  a proper rotation-like transform, NOT mirrored. Must light uncorrected and
  must not appear in the bake log.
- **4, zero-scale-y** — scale `(1, 0, 1)`. A named `ZeroScale` / `ZERO_SCALE_PROP`
  issue in `MapEntities.Validate` and in the `IssueEngine`, a named error from
  `EvaluatePropLighting`, and a warning in the bake result — never a silent
  black prop and never a bake failure.

Positions are world inches, Z-up; normals are the as-imported normals.
Expected outcomes live in the file's `expected` block and are asserted by the
`fixture props light and validate as recorded` test — edit the file and the
test together.
