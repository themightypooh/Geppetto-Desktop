# hammer-map-document-model — handoff (daxter-hammer-map-document, 2026-11-13 ~17:15 EST)

## Status
Implementation done, isolated-harness verified 21/21 PASS exit 0. NOT runtime verified:
no full project build, no app launch, no real-runner group run (PO override).
TestRegistry.cs NOT touched (coordinator-owned) — integrator adds
`MapDocumentTests.Register(registry, settings);`.

## What was already on disk (worker muse-3-desktop-hammer-12, reaped, never verified)
- `kernel/Effigy/Map/MapDocument.cs` (2075 lines), `MapDocumentIo.cs` (779),
  `tests/MapDocumentTests.cs` (489), fixtures `sealed-room-v0/v1.gmap`.
  Full design: versioned `.gmap` text (StudioDocument style, one op per history
  entry), history fold + rollback bar, brushes/displacements/entities/layers/
  visgroups/I-O/compile settings, validation hook on save, atomic save via
  `StudioDocument.AtomicWriteText`, `IMapUndoTarget` impl, `ToIssueDocument()`.
  `StudioDocument.cs` extended, not forked (shared Num/Vec/OneLine/Parse helpers
  promoted to internal + AtomicWriteText; its Version 2->4 / assembly / config
  changes are other workers', not this ticket's).

## Bugs found by the harness and fixed (all in this ticket's files only)
1. `EntityOutput` vs `EntityIoOutput`: MapDocument built I/O rows against the
   FGD *definition* type `EntityOutput` (Name/ArgumentType/Description,
   EntityRegistry.cs) — no SourceEntity/TargetName/Copy/Validate/8-arg ctor.
   Real row type is `EntityIoOutput` (EntityIo.cs). Renamed in MapDocument.cs,
   MapDocumentIo.cs, MapDocumentTests.cs (code + doc comments).
2. MapDocument.cs lacked `using System.IO;` — `InvalidDataException` at
   ApplySnapshot/RestoreSnapshot did not compile.
3. Tests CS0165: `out var volume` / `out var corners` used in the Assert message
   / next line while declared in a non-first `&&` conjunct. Pre-declared.
4. `AddDisplacement` checked duplicate before payload validity, so a bad-power
   displacement on an occupied face reported "already exists" instead of the
   named power defect. Now validates the payload first (InvalidOperationException
   with the named defects). `SetDisplacement` still validates only at
   save/Validate time — known, minor, left as is.
5. Test bugs (not kernel): issue-projection test indexed `Brushes[2]` (wall-x0)
   as the floor — now `[0]`; centroid asserted with exact float `Equals` on a
   plane-solve result (off by ~6e-5) — now 1e-3 epsilon; `FromMapBrush` assert
   used full `SameAs` (includes LayerId) though the documented contract is
   planes/materials/UV only, layer/visgroup come back empty — now geometry-only.
6. Sealed-room light `_light` was `"255 240 200 400"`; the core registry declares
   `_light` as `colour` R G B 0-255, so Validate flagged it. Fixture value is now
   `"255 240 200"` (builder + both fixtures). NOTE for registry owner: real Source
   `_light` is R G B + brightness (4th component unbounded) — the registry's
   Colour rule (all components 0-255) and Source semantics disagree; not resolved
   here, only the fixture was made registry-valid.

## Checks run (isolated harness /tmp/iso-mapdoc, copy of current kernel)
- Kernel copy builds clean (0 errors) after excluding 12 broken sibling files
  (copy only, real repo untouched): MapBrushPrimitives, MapClip, MapCheck,
  MapBakeExport, SkyboxPreview, RoomHelpers, ClipPolygon, MapExportRoundtrip,
  SkyboxWorldspawn, OverlaysDecals, PropFade, InstancesCollab, HintSkip,
  BrushToModel; copy-only stub BuildScript.Map.cs (MapVerbNames empty — worker 64
  still missing) + copy-only MapLayers.cs:299 ternary patch.
- `dotnet run --project harness/harness.csproj -c Release` (stub TestRegistry
  with real Add/Run/filter semantics): **21 passed, 0 failed, 0 skipped**.
  Sealed room (6 brushes + player start + light) save/reload byte-identical,
  rollback persists, v0 migrates with named note, newer version refused by name,
  save refuses invalid docs, registry validation, I/O round-trip, displacement
  round-trip, MapBrush agreement, undo-target snapshot round-trip.

## Blockers / for the final integration gate (NOT this ticket)
- Shared tree does not compile: MapBrushPrimitives.cs uses `MapFace` (does not
  exist; real API is MapBrushFace) — brush-primitives ticket; `InstanceIoProxy`
  defined twice (MapInstances.cs:631 + InstancesCollab.cs:1406); MapLayers.cs:299
  CS0173 (selection-grouping file); ApiCatalog.cs:52 `BuildScript.MapVerbNames`
  missing (BuildScript.Map.cs, worker 64); HintSkip/ClipPolygon/OverlaysDecals/
  MapExportRoundtrip/BrushToModel build against the old Displacement MapBrushFace
  or deleted MapBake types; MapInstanceLibraries.cs CS0050 (per cad-interop note).
- Add the TestRegistry line; run the real `mapdocument` group; regenerate the
  kernel-parity baseline (new files MapDocument.cs/MapDocumentIo.cs are not in
  handoffs/kernel-parity-inventory.json, so KernelParityTests will fail until then).
- No Desktop UI added (per ticket). VMF/.map/BSP remain exports, not the working
  format; `.gmap` is the source of truth.
