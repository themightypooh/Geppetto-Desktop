# hammer-interop-paint-layers-on-faces fixtures

## face-paint.json

One painted brush face, in the `FacePaint` JSON form
(`kernel/Effigy/Map/PaintOnMap.cs`, `FacePaint.ToJson` / `FromJson`):

- base material `brick/brickwall001`, default UV (offset 0, scale 1, no rotation,
  lightmap 16, smoothing group 0)
- second material `grass/grass001` — the material a displacement blends toward
  at alpha 1
- one layer, `red`: a solid red fill, 4x4, Normal blend, opacity 1, no mask

The colour canvas travels as base64 RGBA (straight alpha, row-major, row 0 at the
top): 16 texels of `(255, 0, 0, 255)`. The `paintonmap` test
"fixture face-paint.json loads and composites red" loads this file, composites the
stack, and reads the centre texel back as red with the base material intact.
