# Mesh Doctor + manual retopo — done (edits complete, board mark pending)

Board task `godlike-mesh-doctor-manual-retopo` (Wave 1 P0), worker muse-3-desktop-12.
Claimed 10:34 EST, edits complete same morning. Edits only, no build, no commit.

## What changed (Desktop only)

- `kernel/Effigy/MeshDoctor.cs` (new): one structured integrity report with exact
  counts (holes as boundary loops, non-manifold edges/vertices, flipped faces,
  degenerate vs zero-area faces, duplicate faces, loose vertices, shells, Euler,
  quad %, self-intersections); one-click fix through MeshFix.Apply, one named fix
  at a time with a pre-fix snapshot per step (per-fix undo); pre-export hook
  (blocks on non-manifold/flipped/degenerate/duplicate/self-intersections,
  warns on holes — open surfaces are legitimate here, see the file header;
  strictHoles:true for print pipelines). Built ON MeshValidator / MeshRepair /
  MeshFix / MeshClash, not a second implementation of them.
- `kernel/Effigy/ManualRetopo.cs` (new): surface-snapped quad drawing
  (BVH NearestSurface; a missed snap is refused, never silently placed),
  symmetry twins + mirrored quads with reversed winding, WeldClose via
  MeshRepair.WeldStaged, Relax (interior verts only, re-snapped), ValidateCage
  (all-quad / manifold / closed / boundary loops).
- `core/ExportService.cs`: the pre-export gate, wired into Export() after the
  capability check, before any directory/file is created. Geometry formats only
  (RigAnim skipped); visible non-reference studio bodies + FBX pieces; blocked
  exports return Success:false with "Mesh Doctor pre-export gate blocked...";
  warnings ride on CapabilityReport.Warnings. ExportAsync delegates to Export,
  so it is gated too.
- `tests/MeshDoctorRetopoTests.cs` (new, 17 checks, groups mesh-doctor +
  manual-retopo): exact report counts on hand-traced fixtures, fix-to-manifold,
  per-fix undo incl. UndoAll round-trip, hook block/warn/pass + strict holes,
  export gate blocks a non-manifold body and passes a clean cube, 250k-face
  grid check < 30 s, snap/symmetry/relax/cage/weld.

## Verification (reading + source checks only, no build per PO override)

- Roslyn parse check 4/4 OK (MeshDoctor.cs, ManualRetopo.cs,
  MeshDoctorRetopoTests.cs, ExportService.cs), md5-verified transfer.
- Every external API grep-confirmed against its declaration (MeshValidator,
  MeshRepair, MeshFix, MeshClash/TriangleSet, MeshBVH, PolyMesh, Vec3,
  PartStudio/PrimitiveFeature/DocumentSession/ExportService).
- `inventory.py check` (baseline, no regen): MeshDoctor.cs + ManualRetopo.cs
  listed ADDED desktop-kernel; baseline regen left to the final integrator.

## Integrator flags

- Add `MeshDoctorRetopoTests.Register( registry, settings );` to
  tests/TestRegistry.cs (DesktopTestHost.Run, after RemeshTests). TestRegistry
  was deliberately NOT edited here — concurrent workers were clobbering it.
- Baseline regen (`inventory.py generate` + `check`) is the integrator's step.
- Known boundary: the 1M-face timing in the acceptance is asserted at 250k
  faces in the test (< 30 s); 1M-face timing is the final build's measurement.
  Decimated-export size (<~50 MB OBJ/GLB) is Decimate + export's measurement,
  not this ticket's.

## Board status

- DONE mark BLOCKED: /home/poo/board.json is corrupt (unparseable) since
  ~10:41 EST — a worker's un-locked write truncated the desc of
  godlike-virtualized-viewport-perf-harness mid-sentence. Do NOT restore
  unilaterally (latest good backup 10:39:45 predates R69 + the godlike marks);
  coordinator call. This task is edits-complete and ready to be marked DONE
  once the board is readable again.
