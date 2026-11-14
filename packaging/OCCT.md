# OCCT B-rep core + STEP exchange — packaging note

## What it is

The Desktop B-rep solid layer (`kernel/Effigy/BrepSolid.cs`, `StepExchange.cs`,
`OcctNative.cs`, tests in `tests/BrepSolidTests.cs`, group `brep-solid`).
It is the OCCT ticket (`godlike-occt-brep-core-step-export`, Wave 1 P0) — the
B-rep solid core alongside the mesh sculpt layer, never a replacement for it.

Two routes exist, and they are not the same thing:

1. **Managed analytic core (working now).** `BrepSolid` is a closed shell of
   analytic faces (plane / cylinder / cone / sphere / torus) with exact
   parameters in millimetres. A 50 mm bore IS 50 mm: the H7 check passes on
   the face's radius, and the measures are exact closed-form values from the
   primitive parameters. Tessellation is display/export only, with explicit
   chordal deflection (16 sides on a 25 mm bore cut 0.48 mm and fail H7 by
   ~19x; 96 sides cut 0.013 mm). STEP AP214 import/export (`StepExchange`)
   covers the analytic subset and round-trips it.
2. **OCCT native operations (NOT routed yet).** Booleans, fillets and
   STEP-through-OCCT need a C++ shim exposing a C ABI over OCCT (OCCT is C++,
   not callable from C# directly) — the same pattern as Mobile's
   `gm_manifold.cpp` shim for Manifold. Until the shim lands,
   `BrepSolid.Boolean` / `Fillet` return NAMED recoverable errors
   (`occt-boolean-unavailable`, `occt-fillet-unavailable`), never a silent
   non-watertight solid. The faceted-shell label and the Mobile truth gate
   (CadOcct: valid + closed + mesh-within-tolerance + volume agreement) stay
   in place until OCCT operations actually route.

## The loader

`OcctNative` (`kernel/Effigy/OcctNative.cs`) mirrors `ManifoldBoolean`'s
loader pattern. It finds and loads `libTKernel` and reports the version it
actually loaded (`OCCT_Version_String`), plus whether `libTKSTEP` is present.

Search order for `libTKernel.so` (and `libTKSTEP.so` for the STEP module):

1. `OcctNative.LibraryPathOverride` / the `GEPPETTO_OCCT_LIB` environment
   variable (explicit override; tests use this to force the missing path).
2. `runtimes/<rid>/native/libTKernel.so` next to the published binary
   (`<rid>` is `linux-x64` / `win-x64` / `osx-x64`) — the same bundled-native
   layout `DistributionInventory` probes for SDL2/GLFW. **This is the
   shipping destination for a bundled OCCT.**
3. Next to the binary.
4. The system search path (a host-installed OCCT — on this PC, FreeCAD's
   OCCT 7.9: `/usr/lib/libTKernel.so.7.9`, headers in
   `/usr/include/opencascade`, `freecadcmd --version` reports FreeCAD 1.1.4).
   The versioned soname (`libTKernel.so.7.9`) is also tried.

NOTE: the STEP module is NOT installed on this PC — no `libTKSTEP*` exists
under `/usr` or `/opt` and it is not in the ld cache (only TKernel, TKBRep,
TKXSBase, TKTopAlgo are). `StepAvailable()` therefore returns false here,
honestly. The shim build needs the full OCCT dev install
(`libocct-data-exchange-dev` for TKSTEP) or FreeCAD's bundled TKSTEP.

When the library is missing, `Available()` is false and `LastRoute` names
the missing library. Nothing claims an OCCT operation works when it does not.

## Units and tolerance

- B-rep geometry is **millimetres**, Z-up. The Desktop kernel is inches
  elsewhere; convert at the boundary (`BrepSolid.MmPerInch = 25.4`,
  `BrepSolid.Scaled`).
- Document tolerance is **0.01 mm** (`BrepSolid.DocumentToleranceMm`).
  Precision tessellation deflection is 0.005 mm, print 0.05 mm.
- Geometry is stored as `Vec3` (float) with scalar parameters in `double`;
  measures are computed in `double`. Effective precision is ~1e-5 mm at part
  scale — far inside the 0.025 mm H7 band.

## STEP details

- AP214, SI millimetres, one `ADVANCED_BREP_SHAPE_REPRESENTATION` per solid.
- Faces carry their bounds as shared `EDGE_CURVE`s: a rim circle is one
  entity used by both adjacent faces (opposite `ORIENTED_EDGE` flags), so the
  file has real shared topology, not duplicated curves.
- Spheres are two hemispherical faces sharing the equator circle; tori are
  two half faces sharing the two tube circles. A full sphere/torus has no
  boundary, and a single face with no bounds is not valid STEP.
- Cone placement: origin at the base centre, axis from base toward the apex,
  radius = base radius, semi-angle = half-angle. Apex = origin + axis *
  (radius / tan(semi-angle)); the reader inverts exactly this.
- Each `ADVANCED_FACE` is preceded by a `/* GEPPETTO_BREPFACE ... */`
  comment carrying the exact U/V spans, area and Reversed flag, so the reader
  restores them instead of guessing. It is a Geppetto annotation, not standard
  STEP; readers without it still get a valid solid (spans derived from the
  bounds geometry, torus halves defaulting to full spans with a warning).
- Volume on read is recognised per primitive combination (box / cylinder /
  tube / cone / sphere / torus) from the exact formulas; anything else leaves
  volume unknown with a warning, never a faked number.

## Known risks (honest, not hidden)

- **Torus half bound orientation.** The two tube circles bounding a torus
  half are emitted with `.T.` flags on both bounds. The spans round-trip
  exactly via the Geppetto comment, but whether OCCT's own STEP reader
  accepts the orientation is unproven — the final build's OCCT/FreeCAD
  round-trip check is the arbiter. If OCCT rejects it, the fix is in the
  `FaceLoop` torus branch (`StepExchange.cs`), not in the model.
- **Volume is not re-integrated from the face set.** Factory volumes are
  exact closed-form values from the primitive parameters; validity is
  structural (closed shell, shared edges) plus those exact measures. A
  face-set divergence integration (and an OCCT cross-check of it) is the
  follow-on, together with the shim.
- **No OCCT operations yet.** See the shim section below; do not present the
  managed core as OCCT.

## The C++ shim (follow-on, not this ticket)

What the shim must expose (C ABI, `gm_occt.cpp`, built against the system
OCCT 7.9 headers in `/usr/include/opencascade`, linked to `libTKernel`,
`libTKBRep`, `libTKTopAlgo`, `libTKSTEP`, `libTKXSBase`):

- `gm_occt_version()` — the `OCCT_Version_String` passthrough (already
  probed directly by the loader; the shim re-uses it).
- `gm_occt_make_box/cylinder/cone/sphere/torus(...)` — build a B-rep shape
  from parameters, return a handle.
- `gm_occt_boolean(op, a, b)` — exact boolean, returning a handle or a named
  error string.
- `gm_occt_fillet(shape, radius, edgeIds)` — fillet, same error contract.
- `gm_occt_step_write(shape, path)` / `gm_occt_step_read(path)` — STEP
  through OCCT's `STEPControl_Writer` / `STEPControl_Reader`.
- `gm_occt_tessellate(shape, chordal, angular)` — triangulation out, so the
  managed tessellator can be cross-checked against OCCT's own.
- `gm_occt_measure(shape)` — volume / area / validity (`BRepCheck_Analyzer`,
  `BRep_Tool`, `GProp_GProps`) for the truth-gate cross-check.
- `gm_occt_free(handle)` / `gm_occt_error()` — the Manifold shim's
  ownership/error pattern, copied.

Bundle destination: `packaging/native/linux-x86_64/libgmocct.so`, shipped to
`runtimes/linux-x64/native/` by `package-linux.sh` (the same step that ships
`libgmmanifold.so`). Record the license in `DistributionInventory`
(OCCT is LGPL-2.1-or-later with an exception for the Open CASCADE Technology
Public License terms — the integrator confirms the exact SPDX entry before
shipping; system OCCT on this PC is the Debian `libocct-*` packaging of 7.9).

## Files

- `kernel/Effigy/BrepSolid.cs` — analytic solid, factories, validity, measure, tessellation.
- `kernel/Effigy/StepExchange.cs` — STEP AP214 writer + reader.
- `kernel/Effigy/OcctNative.cs` — native loader (probe only until the shim).
- `tests/BrepSolidTests.cs` — 12 checks, group `brep-solid`, registered in `tests/TestRegistry.cs`.
- `packaging/OCCT.md` — this file.
