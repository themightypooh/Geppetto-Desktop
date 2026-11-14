# Geppetto in Poohdot vs s&box: what works (2026-10-28)

Checked against the dial on the `geppetto-dial` branch. In Poohdot today a model opens, but it is read-only. No tool can change the model yet, because the plugin never opens a live, editable copy of it. The kernel already has every feature and every mesh operation s&box used, so what's left is wiring and editor UI.

Status: **works**, **partial**, or **stub** (the dial button only logs "TODO").

## CAD
- **Partial:** Face Material. It colours the whole body and isn't saved.
- **Stub:** Sketch, Plane, Primitive, Import, Profile, Part, Extrude, Revolve, Sweep, Loft, Spline, Boolean, Fillet, Chamfer, Shell, Move Face, Delete Face, Draft, Hole, Mirror, Linear Pattern, Circular Pattern, Transform, Bend Along Curve, Simple Deform, Lattice.

## Model
- **Stub:** all of it. That's vertex, edge and face select; grow, shrink, linked and invert; move, rotate and scale; edge slide, shrink/fatten and bend; extrude, inset, duplicate, bridge, spin, fill and pipe; merge, dissolve, delete, smooth, relax, flip and triangulate; crease; mark seam; and the UV projections.

## Paint
- **Stub:** the channels (colour, roughness, metal, normal), layers, the brush settings, and painting in the viewport.

## Rig
- **Partial:** Rest. It puts the whole skeleton back to its bind pose instead of resetting the soft bones.
- **Stub:** Add Bone, Bone from Part, Delete, Assign Body, Mirror bones, Make Soft, Preview, Paint Weights, Subdivide, Remesh, Check model, Wrinkle Maps.

## Clothing
- **Stub:** all of it. That's Wearer, Garment, Trim, Fur, Shape garment, Fabric, Pocket, Hardware, Wear, Glow, Check, Push out, Clear marks, Make clothing and Make live cloth.

## Animate
- **Works:** Move, Rotate and Scale a bone; Key; Auto-Key; Two-bone IK; playback.
- **Partial:**
  - Prop, Light and Camera are added to the scene but not animated.
  - Export Animation writes a GLB but leaves out unsaved keys.
  - The timeline shows only the first clip, with no clip switching and no key dragging.
- **Stub:** Joint limit, First Person View, Spawn model, Morph event.
- **Missing:** the shape keys panel.

## Saving
- **Works:** Ctrl+S saves keys into an existing .riganim.
- **Not working:**
  - A brand-new take is never saved.
  - Ctrl+Z doesn't undo keys.
  - The .effigy itself never gets written, because nothing edits it yet.

## Export
- **Works:** GLB.
- **Missing:** the s&box exports (vmdl, playermodel, clothing, DMX animation).

## Build order
1. Shared pieces first:
   - Open each model as a live, editable document, with rebuild, Ctrl+Z and Ctrl+S.
   - A settings box for each feature, like the s&box feature dialog.
   - Clicking faces, edges and bodies in the viewport.
   - The feature list.
2. Then each workspace's tools on top of those pieces, in parallel.
3. Then a scripted run in a real Poohdot window: open a model, use each tool, save, reopen, export.
