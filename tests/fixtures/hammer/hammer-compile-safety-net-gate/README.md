# hammer-compile-safety-net-gate fixtures

Hammer Desktop P2b: pre-compile safety-net gate. Same sealed room as
`hammer-compile-native-pipeline/sealed-room.json` (288x288 room, floor
z -16..0, ceiling z 128..144, player start (0,0,32)), in
MapCheckDocumentJson format (brushes as bounds + one material + face
count; entities as classname / origin / properties). Loaded by
`PreCompileGateTests` via `MapCheckDocumentJson.LoadFromFile`.

| File | Expected gate outcome |
|---|---|
| `map-clean.json` | Passes: no issues. |
| `map-invalid-brush.json` | BLOCKED: 1 error, InvalidBrush on brush `bad` (zero-volume box, min == max == (0,0,0)). |
| `map-giant-skybox.json` | Passes with 1 warning: GiantSkybox on brush `skybox` (4096 vs the map's 288 on the longest side, 14.2x the 4x limit). |
| `map-warnings-only.json` | Passes with 2 warnings: DuplicateTargetName (`door1` on `door_a` + `door_b`) and BadKeyValue (`speed: fast` on `relay1`). |

The mixed-issues leak-graph case is not duplicated here: the gate test
loads `../hammer-check-problems-fix-actions/map-mixed-issues.json`
instead (it carries a LeakGraph, so the gate runs the real LeakFinder
verdict and blocks on the leak).
