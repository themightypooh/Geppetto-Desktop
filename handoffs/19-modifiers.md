# Handoff: Modifiers Stack (non-destructive)

## Goal
Non-destructive modifier stack on meshes (like Blender modifiers).

## Prerequisites
- E1 retained document, E2 undo.
- Lease on `DocumentSession`, `ViewportMesh`.

## Scope (minimal v1)
1. Modifier list per mesh: ordered, toggleable, removable.
2. Modifiers: Subdivision (Catmull-Clark, kernel exists), Mirror, Array, Solidify.
3. Evaluation: base mesh → modifier chain → derived mesh → render.
4. Cache derived mesh; recompute on base/param change.
5. Undo: modifier add/remove/reorder = transaction.

## Existing seams
- Kernel Catmull-Clark subdivision.
- `DocumentSession` for retained state.

## Required tests
- Adding subdivision doubles face count.
- Mirror produces symmetric mesh.
- Toggle disables modifier.
- Undo restores.

## Done criteria
- Modifier visible in viewport capture.