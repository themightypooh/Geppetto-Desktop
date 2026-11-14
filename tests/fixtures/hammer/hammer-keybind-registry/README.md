# hammer-keybind-registry fixtures

## `catalogue.json`

The default keybind catalogue, mirrored from
`kernel/Effigy/Map/KeybindRegistry.cs` (`KeybindRegistry.DefaultCatalogue()`).
One entry per map action: `id`, `name`, `category`, `description`,
`default` (canonical chord text, e.g. `Shift+B`). The test
`catalogue matches the fixture catalogue.json exactly` asserts the two agree
entry for entry, so a drift in either direction fails loudly instead of
silently shipping a catalogue the UI (later) would show differently from
what the registry holds.

50 actions, 10 categories: Tool (8), Brush (8), Displacement (6), Face (4),
Entity (5), Group (3), File (5), Compile (4), Validate (3), Bake (4).

## Where the catalogue comes from

The canonical action ids come from the AI BuildScript verbs ticket
(`hammer-ai-buildscript-verbs-map`), which defines the map action
catalogue: brush create/edit/clip/vertex, displacement create/sculpt,
per-face material/UV, entity place/edit + I/O add, grouping/visgroups,
VMF/.map import/export, native compile (Quick/Preview/Final),
validation/Issue list, lightmap bake, navmesh, skybox/cubemap.

That ticket had NOT landed when this registry was written (2026-11-13:
still `todo`, no `BuildScript.Map.cs` in the tree), so the catalogue here
is the spec-derived one covering exactly that scope, one action per
capability. `KeybindRegistry.ImportCatalogue()` is the integration point:
when the verbs ticket lands it imports its verb list (new ids are added
with their defaults; existing ids are replaced only with
`overwriteExisting: true`, and a user remap is never silently
overwritten). If the landed verbs catalogue uses different ids, the import
— not the registry file — is where the two are reconciled.

## Which defaults are documented Hammer hotkeys

Exactly five defaults are the documented Hammer hotkeys named in the
ticket:

- `Shift+B` — Block Tool (`map.tool.block`)
- `Shift+X` — Clip Tool (`map.tool.clip`)
- `Shift+V` — Vertex Edit Tool (`map.tool.vertex`)
- `Shift+A` — Face Edit Tool (`map.tool.face`)
- `Shift+E` — Entity Tool (`map.tool.entity`)

Every other default is a Geppetto-chosen default, not a claimed Hammer
hotkey (e.g. `F9` Quick Compile follows Hammer's F9 Run Map; `Alt+P`
Check for Problems follows Hammer's Alt+P; the rest are chosen for
uniqueness and mnemonics). No two actions share a chord.

## API (backend only — no editor UI, no input handling)

`kernel/Effigy/Map/KeybindRegistry.cs`, namespace `Effigy.Map`:

- `KeybindRegistry.CreateDefault()` / `DefaultCatalogue()` / `Catalogue`
- `Bind(actionId, chord)` / `Remap(actionId, chord)` / `Unbind(actionId)` —
  all return `KeybindResult` (`Ok`, `Error`, `Conflict`, `Message`); a
  conflict (two actions on one chord) is refused, registry unchanged, and
  the `KeybindConflict` names the chord and both action ids
- `ListBindings()` — every action with its current binding, description
  and `IsDefault`, sorted by category then name; `ListBindings(category)`
  filters; `Search(text)` matches id / name / category / description
- `GetBinding(actionId)`, `BindingFor(chord)`, `IsDefault(actionId)`
- `ResetAction(actionId)`, `ResetToDefaults()`, `Validate()`
- `ImportCatalogue(catalogue, overwriteExisting: false)` — atomic verbs
  import (see above)
- `KeyChord.Parse` / `TryParse` — canonical form is modifiers in
  Ctrl, Alt, Shift order + one key (`Shift+B`, `Ctrl+Shift+X`, `Enter`,
  `F9`); letters, digits, F1–F12 and Enter/Escape/Space/Tab/Delete/
  Backspace/Insert/Home/End/PageUp/PageDown/arrows
