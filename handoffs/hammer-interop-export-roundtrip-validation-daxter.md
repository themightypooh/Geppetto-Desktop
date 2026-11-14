# hammer-interop-export-roundtrip-validation — handoff (daxter-hammer-interop-export, 2026-11-13)

Board: geppetto-desktop / hammer-interop-export-roundtrip-validation. Status: implementation DONE,
verified by isolated harness 15/15 (see Checks). NOT runtime-verified on the official runner / full
build — PO override: full builds deferred to the final integration gate.

## What was already on disk when this session started
The implementation was already largely written (untracked, by an earlier session that never marked
the ticket done): kernel/Effigy/Map/MapExportRoundtrip.cs (1251 lines), tests/MapExportRoundtripTests.cs
(15 checks, group `exportroundtrip`), fixtures tests/fixtures/hammer/hammer-interop-export-roundtrip-validation/
(room.vmf, room.map, README.md), and the TestRegistry.cs Register line (added by hammer-76, marked TEMP).
This session VERIFIED it against the current tree, found it did NOT compile and did NOT pass, and fixed
the real defects below. No file was rewritten from scratch.

## Real defects fixed in this session (all in the ticket's own files)
1. **MapBrushFace ambiguity — MapExportRoundtrip.cs:209 (compile error, CS7036).** The file is in
   namespace Effigy.Map, so unqualified `MapBrushFace` resolved to Displacement.cs's
   `Effigy.Map.MapBrushFace` (corners + int MaterialId, 10-param ctor), not the landed
   `Effigy.MapBrushFace` (plane + FaceMaterial). `ToMapBrushes` now constructs
   `global::Effigy.MapBrushFace`. Same bug class also hits ClipPolygon.cs and HintSkip.cs — NOT this
   ticket's files, do not fix here; their owners fix the same way.
2. **Valve 220 .map carries no ids.** Map220Io.Parse assigns id 0 to every brush/entity/face — the
   format has no id fields. CompareDocuments compared ids on every path, so the map220 path (and
   the IssueEngine rule, and validate-all) could never pass. Fix: CompareDocuments / CompareFace
   skip brush/entity/face id comparison on the map220 path only; CheckMap220 records the loss as a
   result Note. VMF still compares ids exactly.
3. **OBJ topology compared reader slot numbers — but ObjReader only recovers material_N slots.**
   ExportObj writes named usemtl (`brick/brickwall001`), ObjReader.SlotFromMaterialName returns
   `current` for any non-material_N name, so every re-imported face landed on slot 0 and the
   topology multiset always differed. Fix (this file only; ObjReader.cs is NOT touched — its
   behaviour is a documented limit, changing it would move every OBJ importer): CompareObjMesh
   keys topology on vertex indices + the per-face usemtl NAME parsed from the OBJ text
   (new `ScanFaceMaterials`, new 4-arg CompareObjMesh overload used by CheckObj); the 3-arg
   overload keys on indices only and relies on the MATERIAL name-set check (the tampered-usemtl
   test still catches a dropped material that way).
4. **Stale header claims reconciled (same two files):** MapDocument HAS landed
   (kernel/Effigy/Map/MapDocument.cs, namespace Effigy) and CompileInput.FromDocument(MapDocument)
   exists; worker 17 (obj/glb export) and worker 22 (compile pipeline) are DONE, not in-progress;
   the tests ARE registered in TestRegistry.cs (hammer-76's TEMP line). Canonical input stays
   VmfDocument until the integrator reconciles MapDocument (see Blockers).

## Changed files (this session's edits only)
- kernel/Effigy/Map/MapExportRoundtrip.cs — fixes 1–3 above + header reconciliation. API (unchanged
  except additions): RoundtripIssue, RoundtripPathResult, MapExportRoundtripResult,
  MapExportRoundtrip.SealedRoom/BoxBrush/ToMapBrushes/ToCompileBrushes/ToCompileInput/
  ToIssueDocument/FromIssueDocument/CompareDocuments/CheckVmf/CheckMap220/CheckObj/CompareObjMesh
  (new 4-arg overload)/ScanNames (new ScanFaceMaterials)/CheckGlb/CheckGlbContainer/CheckCompile/
  ValidateAll/ToIssue, ExportRoundtripRule (Id "export-roundtrip", Global scope).
- tests/MapExportRoundtripTests.cs — header comments only (registration + MapDocument status).
- No other repo files touched. In particular: TestRegistry.cs NOT edited (hammer-76's TEMP Register
  line stays for the integrator to confirm or re-do); ObjReader.cs, Map220Io.cs, VmfIo.cs,
  MapBakeExport.cs, CompileRunner.cs untouched.

## Fixtures
- tests/fixtures/hammer/hammer-interop-export-roundtrip-validation/room.vmf, room.map, README.md
  (pre-existing; the "checked-in fixtures match the builder" check proves they match
  MapExportRoundtrip.SealedRoom() exactly: 7 brushes, 42 faces, 4 materials, 2 entities,
  fractional crate corner 64.1).

## Checks run (isolated harness, /tmp/exportroundtrip-harness)
- Command (cwd = repo root, so fixture lookup resolves):
  `dotnet build /tmp/exportroundtrip-harness/exportroundtrip-harness.csproj -c Release` → 0 errors,
  then `dotnet run --project /tmp/exportroundtrip-harness/exportroundtrip-harness.csproj -c Release
  --no-build -- --filter exportroundtrip` → **15/15 PASS, 0 failed** (vmf exact, map220 exact,
  VMF scale-form rejection, obj round-trip, dropped-material caught, material/plane drift caught,
  GLB container valid-but-blocked, GLB core writer round-trip, native compile round-trip,
  leak caught + .lin written, IssueEngine rule, fixtures match builder, validate-all).
- Harness includes the whole live kernel EXCEPT the shared-tree-broken files (see Blockers), plus
  live core/GlbExport.cs + core/GlbImport.cs and the real tests file; TestRegistry/TestSettings are
  faithful stubs (same Add signature as tests/TestRegistry.cs). One harness-only stub file,
  /tmp/exportroundtrip-harness/Stubs.cs: a minimal MapDocument shape (Name, CompileVisibleBrushes,
  CompileVisibleEntities) so CompileRunner.FromDocument compiles — MapDocument.cs itself is
  excluded as broken (see Blockers); no test calls FromDocument. Stubs.cs is NOT in the repo.
- Before the fixes the same harness run was 10/15 (map220 ids, obj topology, IssueEngine rule,
  validate-all failed; fixture test failed only because the first run used the wrong cwd).

## Checks deferred (blocked, not skipped)
- Prescribed command `dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter
  exportroundtrip` NOT run: shared kernel does not compile (see Blockers) and the full build is
  deferred to the final integration gate per PO override.
- No full project build, no app launch, no UI (none in scope).

## Acceptance status
- VMF path: round-trips exactly (planes/UV/materials/entities/I-O/displacements). PASS.
- Valve 220 .map path: round-trips exactly except ids, which the format cannot carry (noted,
  not hidden). PASS.
- OBJ path: round-trips (42 faces, 4 materials, vertices 1e-4, per-face material names). PASS.
- GLB path: BLOCKED, honestly reported — MapBakeExport.ExportGlb is a minimal stub (header +
  asset JSON, no mesh payload; MapBakeExport.GlbNote). CheckGlb reports BLOCKED with named issue
  EXPORT_ROUNDTRIP_GLB_PAYLOAD_MISSING; the real mesh round-trip is proven at the core seam
  (GlbExport.Write -> GlbImport.Read, one part per material). ValidateAll therefore reports
  overall Passed=false while GLB is blocked. Worker 17 must wire MapBakeMesh -> GlbPart into
  ExportGlb; then CheckGlb's block goes away with no harness change.
- Native compile path: Preview preset round-trips (BSP + PVS + lightmaps re-read from disk and
  compared; sealed room passes, leaking room fails with named EXPORT_ROUNDTRIP_COMPILE_LEAK +
  .lin). PASS.
- Broken exports caught as named issues: dropped/swapped material (MATERIAL), plane drift
  (PLANE_DRIFT), VMF-form .map line (UV / parse error), leak (COMPILE_LEAK), NaN plane normal
  via the IssueEngine rule. PASS.

## Blockers / integrator notes (NOT this ticket's files — do not fix here)
- Shared kernel does not compile. Confirmed in the harness build (whole live kernel, 0 errors
  only after excluding these): MapDocument.cs uses `EntityOutput` (FGD decl) where it means
  `EntityIoOutput` (connections row) + lacks `using System.IO;` (54 errors; worker 12's file);
  MapDocumentIo.cs same mismatch (18); MapBrushPrimitives.cs + StarterTemplates.cs use `MapFace`,
  which does not exist (real: MapBrushFace); MapInstances.cs defines InstanceIoProxy twice
  (also in InstancesCollab.cs); OverlaysDecals.cs mixes the two MapBrushFace types + readonly
  assigns; ClipPolygon.cs + HintSkip.cs have the same MapBrushFace ambiguity fixed here;
  MapLayers.cs:299 CS0173 (array vs List conditional); ApiCatalog.cs references
  BuildScript.MapVerbNames, which does not exist (worker 64's BuildScript.Map.cs missing).
- TestRegistry.cs Register line for this group is hammer-76's TEMP line — integrator confirms
  it stays or re-adds it properly, then runs the prescribed command.
- MapDocument seam: once MapDocument compiles, the integrator replaces ToMapBrushes /
  ToCompileInput (consider delegating to CompileInput.FromDocument) / ToIssueDocument with
  MapDocument mappings; the VmfDocument adapters stay until then.
