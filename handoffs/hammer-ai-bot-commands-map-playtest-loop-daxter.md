# Hammer AI bot commands for maps + AI Quick Play / walk / fix loop — daxter

Ticket: `hammer-ai-bot-commands-map-playtest-loop` (geppetto-desktop).
Worker: `daxter-hammer-ai-bot-commands`. Status: implementation done,
NOT runtime-verified in the real tree (see Checks). No UI. No full build
or app launch, per the PO workflow override.

## What it is

`BotMapPlaytest.Run(mapPath, options)` — one scripted flow, headless,
six steps with a named result per step:

1. `build` — load the .vmf / Valve 220 .map + BSP-only QuickPlay bake
   (the same calls `ops map build` makes)
2. `validate` — IssueEngine full check (the same call `ops map validate`
   / `ops map issues` make)
3. `play` — spawn + walk validation from the bake
4. `report` — aggregated issue codes, bake warnings, walk block reason
5. `fix` — scripted fix for a leak, or "no fix needed"
6. `re-validate` — validate + bake again on the fixed document

`BotMapPlaytestResult.Passed` is true only when every step ran, the fix
step succeeded, and re-validate is clean. Build / validate are
diagnostic — on a leaking map they correctly report the leak, so they
are not required Ok (the harness caught the first version requiring
all steps Ok, which could never pass on a leaking map).

Named codes: `leak` (build unsealed / re-validate still leaking),
`LEAK` (validate issue code), `no-player-start`, `walk-blocked` /
`walk-left-map` / `walk-no-floor` / `walk-step-too-high` /
`walk-drop-too-far` (pass-throughs from the bake), `no-scripted-fix`
(error issues with no scripted fix), `leak-no-location` (leak reported
but neither a portal nor a probe escape located it),
`non-axis-aligned-brush` (projection refusal, pass-through).

## Delegation, not a new surface

The loop calls existing layers and re-implements none of them: load +
projections via `OpsCli.LoadMap` / `ToIssueDocument` / `ToQuickPlayMap`
(the ops-CLI map commands' own helpers), bake + walk via
`QuickPlay.Bake`, validation via `IssueEngine.Run`, fixed-map
persistence via `VmfIo.WriteFile`. It does NOT edit `AiSuiteTools.cs`
(held by the capability-responses ticket), `OpsCli.cs` (held by the
ops-CLI map-commands ticket), or `BuildScript.cs` (map verbs not
landed). The wiring — `map_playtest` tool / BuildScript verb /
`ops map playtest` subcommand — belongs to those tickets and the final
integration gate. Script input (`ops map build <script>`) likewise
needs `BuildScript.Map.cs` to land first; this loop takes a map path.

## Leak fix design

IssueAutoFix deliberately never auto-seals a leak, so the loop does
NOT route leaks through it. The scripted fix adds ONE explicit sealing
brush to the VmfDocument (a real brush a human would add by hand) and
re-validate judges it:

- Primary: the bake's `LeakPortal` is the exact gap quad. Seal box =
  portal AABB + 8" margin, thin (portal-plane) axis widened to +/-32"
  so the patch straddles the full wall thickness. Fixture: portal
  (144,-0.5,0)..(144,0.5,128) -> box (112,-8.5,-8)..(176,8.5,136).
- Fallback (no portal): LeakProbe's escape gives the leaking
  direction. Its Exit is NOT the gap — `RayExitPoint` expands the
  solids bounds by 64", so the exit sits 64" past the wall, in the
  void; a plate centred on it would seal nothing (the harness caught
  this: first fallback plate was x 176..240, floating outside the
  wall). The fallback seals the whole leaking SIDE: a plate straddling
  the document bounds' outer face on the escape axis (+/-32") and
  spanning the full tangential bounds (+8"). Crude, deterministic,
  re-validate judges it.
- Non-leak error issues have no scripted fix in this loop
  (IssueAutoFix needs a MapDocument undo target; no VmfDocument ->
  MapDocument import exists). The report names them; fix reports
  `no-scripted-fix`.

Two real bugs the harness caught in my own code (fixed, not hidden):
`Vec3` is a readonly struct (no field assignment in `SealBoxForPortal`),
and `MapCommandException` is nested inside `OpsCli` (catches must say
`OpsCli.MapCommandException`).

## Files (new only; no tracked file modified by this ticket)

- `BotMapPlaytest.cs` — the loop, seal-box geometry, box-brush builder
  (`BuildBoxBrush`, outward-wound, same plane-point pattern as the
  sealed-room fixture), `NextIds`, `DocumentBounds`. Namespace
  `Marionette.Desktop`, desktop-side: it calls OpsCli, so it cannot
  live in the kernel (kernel must not reference the app).
- `tests/BotMapPlaytestTests.cs` — group `botmapplaytest`, 9 checks:
  sealed clean / leaking leak->fix->clean / portal seal box /
  escape fallback + side-plate seal / escape side plate /
  box-brush projection round-trip + NextIds / no-spawn named code /
  fixed-map VmfIo round-trip / missing file + bad extension.
- `tests/fixtures/hammer/hammer-ai-bot-commands-map-playtest-loop/` —
  `sealed-room.vmf`, `leaking-room.vmf` (1" slit in the east wall,
  y -0.5..0.5, z 0..128), `no-spawn-room.vmf`, `README.md`. Verbatim
  copies of the ops-CLI fixtures, so this ticket does not depend on
  another ticket's fixture directory.
- This handoff.

NOT touched: `tests/TestRegistry.cs` is coordinator-owned (per the
inter-op worker's note); the integrator adds one line:
`BotMapPlaytestTests.Register( registry, settings );` (near
`OpsCliTests.Register`).

## Checks run (isolated harness, real code)

`/tmp/iso-botmapplaytest/` — verbatim kernel copies (571 files) minus
8 known-broken files (ApiCatalog, MapBrushPrimitives,
InstancesCollab, MapInstanceLibraries, OverlaysDecals, HintSkip,
ClipPolygon, MapExportRoundtrip) plus MapDocumentOverlays (depends on
the excluded OverlaysDecals), plus a `MapLayers.cs:299` CS0173 fix
APPLIED IN THE HARNESS COPY ONLY (array-vs-List ternary; repo file
untouched, owner to fix), plus a stub OpsCli carrying the VERBATIM
LoadMap / ToIssueDocument / ToQuickPlayMap / MapIssues bodies from
OpsCli.cs, plus the verbatim `BotMapPlaytest.cs` and the REAL
`BotMapPlaytestTests.cs` (run through a minimal TestRegistry /
TestSettings stand-in of the same shape).

Result: `dotnet build` 0 errors; runner **58 passed, 0 failed, exit 0**
(49 mirror checks + 9 checks from the real test file). The loop was
exercised end to end on the real fixtures: sealed runs clean; leaking
reports `LEAK`, applies `seal-leak-portal` with the exact predicted
box, re-validates clean; the fallback side plate seals when added and
re-baked; the fixed map round-trips through VmfIo and re-bakes sealed.

## Checks deferred (gate)

- Real `Desktop.Tests --filter botmapplaytest/` group run (needs the
  TestRegistry line above + a compiling tree).
- Full tree build: the shared tree does not compile at this writing
  (`BuildScript.MapVerbNames` referenced by ApiCatalog but
  `BuildScript.Map.cs` does not exist; `MapFace` vs `MapBrushFace`;
  `MapLayers.cs:299` CS0173; others per the inter-op worker's note).
  None of those are this ticket's files; do not attribute them here.

## Notes for the integrator / gate

- `MapLayers.cs:299` CS0173 is pre-existing and blocks any build that
  includes the kernel: `group is null ? new[] { r } : group.Members`
  (array vs List). Harness fixed it in-copy only.
- Tunnel note 2026-11-13 ~22:09: `/root/.ssh/config` was found missing
  again (same recurrence as 2026-10-23); restored from
  `~/workspace/ssh-access/config`, tunnel verified back up. Not a
  machine outage.
- `PlaytestLoop.cs` (hot-reload loop) is a different loop from this
  one; its file header still claims MapDocument / QuickPlay /
  CompileRunner "have NOT landed" — stale, but not this ticket's file.
