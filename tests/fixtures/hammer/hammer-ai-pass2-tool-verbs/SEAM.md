# SEAM.md — Hammer Desktop AI P2: AI exposure for every pass-2 tool

Task `hammer-ai-pass2-tool-verbs` (worker `daxter-hammer-ai-pass2-verbs`).
Implementation done, NOT runtime verified (PO workflow override 2026-11-13:
no full project build or app launch; full build + named test-group run are
deferred to the final integration gate).

## Files added (no shared file was edited)

| File | What it is |
| --- | --- |
| `kernel/Effigy/Script/BuildScript.MapPass2.cs` | 21 pass-2 verbs on the real kernel APIs. `BuildScript.MapPass2VerbNames` (string[21]), `MapPass2VerbBacking()` (verb → real type), `RunMapPass2Verb(verb, mapPath, args, workDirectory)` → JSON. Also `RunMapPass2CompileWithArtefacts` / `RunMapPass2OnlyEntitiesWithArtefacts` for the MCP compile seam. |
| `kernel/Effigy/Script/ApiCatalogMapPass2.cs` | Catalogue side: `MapPass2VerbNames()`, `MapPass2VerbBacking()`, `MapPass2VerbDescriptions()`, `GenerateMarkdownSection()`, `VerifyBacking()` (catalogue check 5 over all 21 rows). |
| `McpMapPass2Tools.cs` (repo root) | 21 MCP tools, same shape as `McpMapTools`. `ToolNames`, `DestructiveToolNames`, `SafetyGateTools`, `RequiresConfirm(tool, args)` (reference confirm rule), `BuildScriptVerbFor` (identity), `Definitions` / instance `ListTools()`, `CallAsync`. Own session state (groups, layers, visgroup overlay, edited/pasted/removed brushes, registries, hotload, held compile artefacts). |
| `OpsCliMapPass2.cs` (repo root) | 21 CLI subcommands → the same verbs. `CliCommands` (subcommand → verb), `MapPass2Usage`, `MapPass2(args)` → exit 0 ok, 1 error / not-implemented, one JSON object per command on stdout. |
| `tests/HammerAiPass2ToolVerbsTests.cs` | Test group `hammerpass2ai` (33 checks). NOT registered in `TestRegistry.cs` — see wiring below. |
| `tests/fixtures/hammer/hammer-ai-pass2-tool-verbs/entity-registry.json` | The 10 registry classes (same set as the pass-1 fixture). |

## The 21 verbs (verb = MCP tool name = CLI subcommand target)

`map_live_lighting`, `map_skybox_preview`, `map_effects_preview`,
`map_compile_preset`, `map_compile_only_entities`, `map_check_problems`,
`map_issue_autofix`, `map_layer`, `map_visgroup_ops`, `map_lock`,
`map_paste_special`, `map_gizmo`, `map_direct_edit`, `map_linked_group`,
`map_prop_place`, `map_propper`, `map_hotspot`, `map_browser`, `map_hotload`,
`map_playtest`, `map_navmesh_generate`.

CLI subcommands: `live-lighting`, `skybox`, `effects`, `compile-preset`,
`compile-only-entities`, `check`, `autofix`, `layer`, `visgroup-ops`, `lock`,
`paste-special`, `gizmo`, `direct-edit`, `linked-group`, `prop-place`,
`propper`, `hotspot`, `browser`, `hotload`, `playtest`, `navmesh-generate`
(see `OpsCliMapPass2.CliCommands` — the single source of truth).

## Integrator wiring (shared files — integrator only)

1. **BuildScript (`kernel/Effigy/Script/BuildScript.Map.cs`, pass-1 — LANDED
   2026-11-13, 2209 lines, 20 verbs, `MapVerbNames` defined):** in
   `TryRunMapVerb` (:156, called from `BuildScript.cs:1137`), accept pass-2
   verbs too (`IsMapVerb(verb) || MapPass2VerbNames.Contains(verb)`) and
   route them to `BuildScript.RunMapPass2Verb(verb, mapPath, args, workDirectory)`.
   Two shape seams, integrator-owned: (a) pass-1 verbs take tokenised
   `map_verb [positional] key=value` args — convert the `key=value` tokens
   (split on first `=`) into the dictionary the pass-2 runner takes;
   (b) BuildScript sessions hold a `MapDocument`, not a VMF file, and the
   pass-2 runner needs a map file path — export the session doc to a temp
   VMF in the session work dir and pass that path.
   No name collisions: pass-2 names are all distinct from pass-1's 20;
   `map_navmesh_generate` supersedes pass-1 `map_navmesh`'s capability error
   under a different name; `map_skybox_preview` is the preview evaluation,
   pass-1 `map_skybox_cubemap`'s cubemap half still returns not-implemented).
   Route pass-2 verbs to `BuildScript.RunMapPass2Verb(verb, mapPath, args, workDirectory)`.
   Do NOT rename `MapPass2VerbNames` to `MapVerbNames` (that name is pass-1's).
2. **ApiCatalog (`kernel/Effigy/Script/ApiCatalog.cs`):** merge
   `ApiCatalogMapPass2.MapPass2VerbBacking()` rows into `MapVerbBacking()`
   and the 21 names into `MapVerbNames()`. All 21 backing types were verified
   against the real kernel (`VerifyBacking()` passes — no phantoms).
3. **McpServer (`Mcp/McpServer.cs`):** append a shared
   `McpMapPass2Tools` instance's `ListTools()` to tools/list and dispatch
   its 21 `map_*` names to its `CallAsync` — same pattern as the pass-1
   wiring (63 tools → 84).
4. **McpSafety (`McpSafety.cs`):** add `McpMapPass2Tools.SafetyGateTools`
   to `MapTools` and `McpMapPass2Tools.DestructiveToolNames` to the
   destructive set; the confirm rule is `McpMapPass2Tools.RequiresConfirm`
   (confirm=true required for: compile_preset final, compile_only_entities
   always, issue_autofix fix, linked_group unlink, paste_special always,
   propper always, hotload reload, layer/visgroup_ops remove).
   Outputs go through `ScrubMapOutput` like pass-1 tools.
5. **OpsCli (`OpsCli.cs`):** at the top of `Map()`, before the existing
   switch: `if ( args.Length > 0 && OpsCliMapPass2.HasCommand( args[0] ) ) return OpsCliMapPass2.MapPass2( args );`
6. **TestRegistry (`tests/TestRegistry.cs`):** one line —
   `HammerAiPass2ToolVerbsTests.Register( registry, settings );`
   (group `hammerpass2ai`, same pattern as `McpMapToolsTests`).

## Session seams the integrator must merge

- **Brush ids:** MCP tools take session ids (`brush-1`, …) and translate them
  to VMF ids before calling the runner (`TranslateBrushArgs` /
  `SessionBrushToVmfId`): world brushes are numbered sequentially in session
  order skipping tied brushes; tied (brush-entity) brushes keep their
  creation number as the VMF id. The translation is derived from the same
  `pass2-session.vmf` export the tool just wrote — merge against that file,
  not a fresh export.
- **Overlays (McpMapPass2Tools public state):** `_groups` (LinkedGroupSet),
  `_layers` + `_brushLayers`, visgroup overlay (colour/compile-visible/
  renames/removals), `_editedBrushes` (edited geometry JSON keyed by session
  brush id), `_pastedBrushes` (MapPasteBrush list), `_removedBrushes`
  (session ids propper consumed). These are the merge inputs for the
  map-document-model write-back; the file verbs deliberately do NOT write
  the map back (see gaps).
- **Compile artefacts:** the MCP tool keeps `CompileBuildArtefacts` across
  calls (`RunMapPass2CompileWithArtefacts` /
  `RunMapPass2OnlyEntitiesWithArtefacts`); the file verb runs a base preset
  in-process instead.

## Safety gates (as implemented)

- Any `command` / `shell` / `exec` argument is refused with named error
  `shell-refused` (kernel runner + MCP `CallAsync`). Compile runs
  `CompileRunner` only — no shell is ever spawned.
- Destructive ops require explicit `confirm=true`, refused otherwise with a
  named error (see `RequiresConfirm` above).
- No second MCP/CLI surface: no `Main`, no server/listen loop, no new
  transport. Tests assert this (`no second mcp or cli surface`).

## Known gaps (named, not faked)

1. `BuildScript.Map.cs` HAS landed since this task started (2209 lines, 20
   verbs, `MapVerbNames` defined, `TryRunMapVerb` dispatch live) — the old
   "missing MapVerbNames" blocker is GONE. What remains is the integrator
   merge above, plus the kernel's only remaining build errors: 4× CS0246
   `MapFace` in `MapBrushPrimitives.cs` (pre-existing stub-built API, owned
   by the brush-primitives ticket — NOT this task's files; verified
   2026-11-13 17:35, kernel-only targeted build, isolated artifacts path).
2. Locks: only texture locks exist in the kernel; no selection/object-lock
   type. Locks have no VMF representation — file verb evaluates only.
3. Texture browser: no `TextureAssetRegistry` (Model/Particle/Sound only) —
   `map_browser kind=texture` returns named capability error
   `texture-browser-no-registry`.
4. Paste / direct-edit / hotspot / propper file verbs return the computed
   result but do NOT write the map back — VmfBrush reconstruction belongs to
   the map-document-model ticket. Propper additionally never removes brushes
   from the file (the converter does not name which brushes it consumed).
5. `IssueAutoFix` on a document needs an `IMapUndoTarget` populated from it
   (unverified); the verb does real `MapCheck.Fix` + `CanFix` assessment.
6. Layer/visgroup file verbs cover the VMF visgroup subset (name/colour/id);
   visible/compile-visible live in the MCP stores.
7. Hotload reload without a project `HotloadDocument` reports poll results
   only; file-verb watches are per-call, MCP keeps them across calls.
8. Effects preview evaluates fog only from map data; particles/rope/proxy/
   renderfx/batch need explicit effect inputs (named in op=list, not faked).
9. Shared-tree breakage found while verifying (NOT this ticket's files, do
   not fix here): `MapDocument.cs` uses `EntityOutput` where it means
   `EntityIoOutput` + lacks `using System.IO;`; duplicate `InstanceIoProxy`
   (`MapInstances.cs` + `InstancesCollab.cs`); `MapInstanceLibraries.cs`
   CS0050; `MapLayers.cs:299` CS0173; OverlaysDecals/HintSkip/
   ClipPolygon/MapExportRoundtrip build against the old Displacement
   `MapBrushFace`, not the landed `MapBrush`.

## Checks run / deferred

- Kernel-only targeted build (`dotnet build kernel/Effigy.Kernel.csproj -c
  Release --artifacts-path /tmp/pass2-kernel-build`, 2026-11-13 17:35):
  4 errors, ALL pre-existing `MapFace` CS0246 in `MapBrushPrimitives.cs`;
  ZERO errors in `BuildScript.MapPass2.cs` / `ApiCatalogMapPass2.cs`.
  Brace/paren/bracket balance 0 in all three root/test files; every
  externally-referenced member (`McpMapTools.*`, `EntityRegistry.*`,
  `TestRegistry.Add`, `TestSettings.WorkDirectory`) verified to exist.
  Source checks otherwise (PO override): verb/backing catalogue verified
  against the real kernel types; every MCP/CLI/test output field name checked
  against the kernel runner source; session↔VMF brush-id translation verified
  against `McpMapTools.ToVmfDocument` / `_brushNumbers` source.
- DEFERRED to the final integration gate: full project build, named test
  group run (`hammerpass2ai`), live MCP transcript, live CLI run.
