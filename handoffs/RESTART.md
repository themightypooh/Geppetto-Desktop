# Restart here — V1 paused at user's request

## Final state: all 35 backend tickets verified; only UI remains

Every ticket on `handoffs/BOARD.md` is `verified` (35/35) and every shared-file lease is
released. There is no `assigned`, `blocked` or `implemented-unverified` work left.

Final gate, run from `/home/poo/games/projects/geppetto-desktop`:

```sh
python3 tests/verify.py --mode required-egl --require-local-fixtures --output /tmp/opencode/geppetto-final3
V3_REQUIRE_EGL=1 V4_REQUIRE_EGL=1 python3 tests/gpu-smoke.py
```

Both PASS. Evidence: `/tmp/opencode/geppetto-final3/geppetto-verify-x3tc_7zg/verification.json`.
**343/343 CPU fixtures pass (0 failed, 0 skipped)** against the real Survey `player_suit.fbx`;
GPU bind-a and bind-b captures are byte-identical (SHA256 `513163ad…`) and the animated capture
differs (`4430d835…`), all on an NVIDIA RTX 5060 Ti through windowless EGL with no display
connection. The last slice closed out M3, M4, G4, I4, V3, A4 and V4.

### Audit note: G4 auto-key was a stub, now implemented

An acceptance-criteria audit after the first green run found that **G4 item 4 was not
actually done**. The service failed closed on auto-key with the message "Fail closed until
E2/E3 can group a pose and clip key atomically" — but E2 and E3 were both verified, so the
justification had expired and the required behaviour (write keys when auto-key is
deliberately enabled) was missing. It was certified anyway, which was wrong.

It is now implemented: auto-key off keeps the pure unkeyed pose override, auto-key on writes
FK local bone keys and live IK goal/weight keys through `ClipEditService`, and the key is
joined into the drag's own `EditTransaction` so pose and key undo/redo as **one** entry.
`K` toggles the mode in the viewer, seeded from the playhead. The sweep for the same pattern
(`fail closed until`, `pending`, `TODO`) found **no other stub** in non-test code; the
remaining "unsupported" strings are deliberate diagnostics for genuinely unsupported input
(paletted PNG, unknown shader, unsupported FBX constructs), which the specs require.

### What the closing slice added

- **M3** — 20-float vertex stride (`pos3+nrm3+boneIdx4+boneWt4+uv2+tangent4`),
  `layout(location = 5)`, sampler units 0-6, per-batch channel binds, fragment shader
  sampling all six maps.
- **M4** — `SurveyMaterialMap.ForSourcePath`/`Resolver` plus
  `DocumentSession.SurveyModelReference`/`ResolveSurveyReference`; `ResourceBudget.cs`
  (`MeshResourceBudget`) with process-wide live texture/byte/reload counters hooked into
  `ApplyMaterials`/`Upload`/`ReleaseGpu`/`Texture`/`Extract`; `tests/PoseAgreementTests.cs`
  proving skinning, picking and the shadow pass read one posed mesh; README section on
  unsupported `ps1_lit` palette/wear/glow/filtering effects.
- **G4** — `ViewportHandleService` drawn through `SkeletonOverlay.EmitHandleLines` and
  routed in `Program.cs`: hover/press/move honoring `WantsPointerCapture`, Escape/focus-loss/
  window-close cancel, source-reload cancel+dispose, camera-relative gizmo scale.
- **I4** — `DocumentLoader.ConvertFbxClip` reads clip bytes up front, defers conversion and
  rejects an FBX clip on a non-FBX document.
- **V3** — `HeadlessImageChecks.cs` + `tests/HeadlessImageTests.cs`, gated on `V3_REQUIRE_EGL`.
- **A4 / V4** — recertified against the M3 layout, gated on `V4_REQUIRE_EGL`.

### Gotchas worth keeping

- `ViewportMesh.Upload` nulls `Vertices`/`Indices` after uploading (deliberate CPU-memory
  saving). `ReleaseGpu` does not restore them, so a mesh cannot be re-uploaded after release.
  Re-rendering under a new palette needs one upload and one release around every draw.
- `FromPolyMesh` hardcodes `hasSkin:false`/`rig:null`, so any skinned test fixture must build
  its 20-float vertices by hand and set `HasSkin`/`Rig` explicitly.
- Rotation rings close through `Cos(2π)`/`Sin(2π)`, which are not exactly 1/0 in float —
  assert ring closure to tolerance, not with `==`.
- Gizmo scale must track camera distance; a world-fixed scale puts a 1-unit handle inside a
  single pixel on hundred-unit models.
- To make two edits undo as one action, join them: `EditTransaction.JoinCommand` (reached via
  `DragSession.JoinCommand` / `IkDragSession.JoinCommand`) composes the apply and undo run so
  the pair shares one history entry. `ClipEditService.BuildInsertOrReplaceKey` and
  `BuildSetIkGoals` exist precisely for this — their `InsertOrReplaceKey`/`SetIkGoals`
  counterparts each create their own entry and cannot group.

### Remaining work (UI only)

Presentation, which was explicitly out of scope for every ticket here: a selection/bone
outliner, the clip timeline and transport, the material/texture slot editor that surfaces
`MaterialDiagnostic` warnings, undo/redo buttons bound to the already-wired `EditHistory`,
an on-screen auto-key indicator for the `K` toggle, export/save dialogs, and preferences UI for
`DesktopPreferences`/`AutosaveScheduler`.
`DependencyWatcher`, `ExportService` and `DocumentSaveService` have verified seams and tests
but no viewer-loop hookup, because each one is only reachable through those UI affordances.

Keep the existing invariants: no UI work was performed, no visible window was ever opened,
and verification stays CPU + windowless EGL only.

## Update after continuation: V1 verified

The paused work below is historical. V1 now has isolated-build orchestration with
`--mode cpu` and `--mode required-egl`, explicit fixture policies, assembly identities,
complete inventory reports and fail-closed build/GPU handling. 30 CPU tests and 8 Python
harness fault-injection tests passed; real NVIDIA EGL checks passed without windows.
See `tests/README.md` for exact commands/evidence. Next ticket: P1, asset resolution.

## Latest continuation: P1 verified too

P1 now exists in `AssetPathResolver.cs`, with 15 CPU path-policy fixtures registered in
`tests/AssetPathTests.cs`. Combined verification: **45 passed, 0 failed, 0 skipped** on
this Linux host, plus 8 orchestration self-tests and hardware EGL captures.

Reports: `/tmp/opencode/geppetto-verify-moo9bjtm/verification.json` (CPU) and
`/tmp/opencode/geppetto-verify-py80i0os/verification.json` (required EGL).
See `tests/README.md` for the resolver API and limitations.

## Latest continuation: Swarm tickets V2, A2, A6, A7, I3, and D2 verified too

- **V2 (Deformation & Animation Invariants)**: Implemented `tests/DeformationInvariantTests.cs` (11 fixtures) testing hierarchical rigs, antipodal quaternion equivalence, multi-root skeletons, singular bind rejection, >220 bone rejection, CPU/GPU palette agreement, and seeded fuzzer.
- **A2 (Morph Keys & Pose Correctives)**: Implemented `MorphEvaluator.cs` and `tests/MorphEvaluationTests.cs` (11 fixtures) supporting corner vertex delta mapping preserving UV seams, precedence matching (driven/keyed/events), pre-skinning deformation, and neutral resets.
- **A6 (Part & Camera Track Evaluation)**: Implemented `PartTrackEvaluator.cs` and `tests/PartTrackTests.cs` (10 fixtures) handling qualified object/part names, follow-bone and parent-part hierarchies with cycle detection, object-space IK alignment, and opt-in camera track evaluation without mutating OrbitCamera.
- **A7 (Constraint Parity Inventory)**: Implemented `ConstraintParityInventory.cs`, `packaging/CONSTRAINTS.md`, and `tests/ConstraintParityTests.cs` (8 fixtures) auditing .ctrlrig constraints, deterministic 120Hz secondary spring simulation with seek reset, look-at, and copy transforms.
- **I3 (FBX Animation Feasibility & Reference)**: Implemented `FbxAnimationFeasibility.cs` and `tests/FbxAnimationFeasibilityTests.cs` (9 fixtures) analyzing animation subset requirements, time/unit conversions, Euler rotation conversion, structured error reporting, and analytical reference fixture with golden values.
- **D2 (Self-Contained Publish & Archive)**: Implemented `packaging/package-linux.sh` and `tests/PackagingTests.cs` (9 fixtures) automating self-contained Linux x64 publish, launcher scripts, bundled sample project, build manifest, and tar.gz + sha256 archive.

Combined verification: **294 passed, 0 failed, 0 skipped** on this host, plus 8 orchestration self-tests and hardware EGL captures without window/display connections.
Report: `/tmp/opencode/geppetto-verify-j63uc_l2/verification.json`.

All shared leases released, no background agents remain running, strictly zero UI panel work performed.
Next tickets ready for assignment: **M2** (UV stream + GPU albedo sampling), **M3** (Tangents and normal/roughness/metallic/emission maps), **M4** (Headless material/texture fallback), **A4** (Live IK solver buffers), **G4** (3D Viewport handle draw & pointer dispatch), **I4** (Native clip conversion from approved FBX subset), **D3** (Installed-artifact smoke test), **V3** (EGL lighting/material/shadow image checks), **V4** (Performance/lifecycle matrix).

User asked to begin implementation, then explicitly stopped to restart. No agents are running.
Do not start all 35 tickets. Continue V1 first, following handoffs/README.md and AGENTS.md.
No UI work; never open visible windows. CPU tests and windowless EGL only.

## Completed in this slice

- `tests/Program.cs`: preserved the original 26 checks, moved into explicit
  `BaselineTests.Register`; one top-level entry delegates to `DesktopTestHost`.
- New `tests/TestRegistry.cs`: explicit registration, duplicate rejection, named groups,
  optional fixture SKIP vs required fixture FAIL, nonzero empty selection, JSON report
  with inventory, results, counts, assembly paths/MVIDs/SHA256 hashes.
- New `tests/HarnessTests.cs`: four harness self-tests (intentional failure propagation,
  optional/required missing fixtures, duplicate registration and empty selection).
- Survey tests are always registered, no longer silently disappear when assets are absent.
- `tests/Desktop.Tests.csproj`: nullable annotations enabled for the harness.

## Verified before stopping

From `/home/poo/games/projects/geppetto-desktop`:

```sh
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --report /tmp/opencode/geppetto-v1-partial-results.json
```

Exit 0: **30 passed, 0 failed, 0 skipped; 30/30 registered/selected** on this machine.
This is CPU testing; no app windows opened. Production app/rendering was not changed.

New runner options: `--list`, `--filter <substring>`, `--survey <path>`,
`--require-local-fixtures`, `--report <json-path>`.

## Still required for V1 (do not mark verified yet)

1. Exercise actual CLI missing Survey fixture in optional and required modes, plus listing
   and empty filter, inspecting reports and process exit codes.
2. Add explicit CPU-only vs required-EGL orchestration and result aggregation. Keep
   `tests/gpu-smoke.py` windowless; it was NOT changed in this partial slice.
3. Test orchestration refuses to launch an old DLL after a failed build, isolates MSBuild
   outputs per project, and fails required EGL unavailability without any visible fallback.
4. Document inventory/fixture selection and commands; update BOARD.md after those checks.
5. Current runner uses explicit registration calls, not reflection. New test files must
   expose Register and be added to DesktopTestHost's manifest. Do not add another Main.

All implementation handoff specs are already under `handoffs/` (35 tickets, 8 categories).
The next prerequisite after V1 is P1 asset resolution, then E1 retained document sessions.
Nothing in P1/E1 or the other feature tickets was implemented in this slice.
