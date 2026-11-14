# hammer-preview-wysiwyg-effects fixtures

`preview-scene.json` — one of each preview input (particle system, fog
controller, rope, material proxy, render-fx entity) plus the hand-computed
expected values the `previeweffects` tests check. Units are Source inches,
Z-up; fog colour is 0..1, renderFx colour is 0..255 (as stored in the
Source keyvalues). Expected values were computed by hand from the formulas
documented in `kernel/Effigy/Map/PreviewEffects.cs`, not copied from a run.
