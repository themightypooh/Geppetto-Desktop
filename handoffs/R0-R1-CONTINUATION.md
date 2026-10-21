# R0/R1 continuation — 2026-10-20

The architecture split is implemented and exercised through production `SceneRenderer`:
shader sources live outside `Program.cs`; `Draw` dispatches Shadow, DepthPrePass, Lit,
PostChain from one immutable pass-order list. Invalid, missing and duplicate passes are
rejected. `RenderTarget` owns colour/depth attachments, resizes, disposes repeatedly,
and now releases partial allocations when framebuffer creation fails.

The interrupted R1 integration is completed: `--view-transform`, `--look`,
`--view-exposure`, `--gamma`, `--white-balance`, and `--tint` parse with finite range
validation and reach native, serve and headless renderers. Defaults remain AgX/None,
0 display stops, gamma 1, 6500 K/tint 0. Existing scene `--exposure` is separate.
README.md documents linear Rec.709, one display encode, and the white-balance convention.
Headless `--stats` retains the vertex count before upload releases CPU arrays.

Changed in this continuation: `DesktopOptions.cs`, `Program.cs`, `HeadlessRender.cs`,
`ServeMode.cs`, `RenderPass.cs`, `RenderTarget.cs`, `tests/HdrPipelineTests.cs`,
`tests/RenderArchitectureTests.cs`, `README.md`, and these handoff records.
No UI, kernel, source asset or other renderer-feature implementation edits.

## Reproducible evidence

All GPU work used hardware NVIDIA EGL, with no window or display-server connection.

- `dotnet build tests/Desktop.Tests.csproj -c Release --artifacts-path /tmp/opencode/r0-r1-final-build`
  exited 0. Two existing warnings: kernel ShadeEnvironment CA2014 and DocumentSaveTests CS8602.
- `env -u DISPLAY -u WAYLAND_DISPLAY dotnet /tmp/opencode/r0-r1-final-build/bin/Desktop.Tests/release/Desktop.Tests.dll --filter render-architecture/ --report /tmp/opencode/r0-architecture-continuation.json`
  exited 0: 2/2 pass. Covers real Draw order, resize/reload, depth-only targets,
  repeated disposal, failed allocation cleanup, and existing live-scene/allocation checks.
- The full frozen-build run with `--report /tmp/opencode/r0-r1-full-continuation.json`
  exited 1: 537 pass, 1 fail, 7 explicit skips, 545 registered. All 11 HDR and both
  architecture fixtures passed. Remaining failure: R2 `ibl-gpu/gradient diffuse, rough
  metal broadening and dielectric Fresnel` (up=0, down=0). R2 is owned by another live
  session; its production/tests were changing during this continuation and were preserved.
- `python3 tests/gpu-smoke.py` exited 1 at the aggregate test gate; initial snapshot had
  two R2 IBL failures (540 pass, 2 fail, 3 skips). Report:
  `/tmp/opencode/geppetto-verify-hmaiteke/verification.json`. This is not a passing
  aggregate smoke run.
- Ran the existing `tests/verify.py` `Verification.gpu` method separately against the
  final isolated app (no alternate lighting implementation): 3 x 120 frames, exact
  1024x1024 captures, all exited 0. Report and exact commands:
  `/tmp/opencode/r0-r1-gpu-continuation/gpu-results.json`.
  Bind-a/b SHA256: `33a77abe36a763fe3fcf808b638b6c31702980c13c45e85a5ab203d1da9e84f7`;
  animated: `507a1efec6ce3cd7f3870f0d8b7463eb48e32fc9f92f8c731d093404d24f8e87`.
- Three actual headless CLI captures (Standard/neutral, Standard/3000 K, AgX/Punchy
  with gamma/exposure/tint) exited 0 with `--stats`; all hashes differ. Commands,
  logs, and PNGs: `/tmp/opencode/r0-r1-cli-continuation/results.json`.

The full-test report records assembly identities for this frozen build. App SHA256:
`d44ca0938e536cece1e98762e6dc72546f07d6c450dcd7f25c66b6bac37d115f`.

## Re-verification after the R2 session landed (2026-10-20, later)

The R2 IBL fixtures that blocked the aggregate gate above are now green, so the whole
gate was re-run against the current tree:

- `dotnet build Marionette.Desktop.csproj -c Release` exited 0, 0 warnings / 0 errors.
- `dotnet run --project tests/Desktop.Tests.csproj -c Release` exited 0:
  **575 passed, 0 failed, 7 explicit skips, 582 registered**. The one earlier failure
  (`dependency-watcher/out-of-order job completion`) is a timing flake in a file-watcher
  test unrelated to the renderer; it passed 3/3 in isolation and in the clean re-run.
- `dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter hdr` exited 0:
  15/15 selected pass, including all 11 `hdr/` fixtures.
- `python3 tests/gpu-smoke.py` exited 0. Report
  `/tmp/opencode/geppetto-verify-aqryfrtg/verification.json`: status PASS; build,
  harness-selftests, inventory, cpu, bind-a, bind-b and animated all PASS; CPU
  579 passed / 0 failed / 3 skips / 582 registered. Bind-a and bind-b are byte-identical
  (`c1f1ef505144ca2a933e4c79aa0095ebab944e7eb5500c27b3df2a7825e11f18`) and the animated
  capture differs (`eb2ca77da765cea501bee96c56af5cab6400a027a183ee404355fc65900b7554`),
  so the deterministic capture contract holds.
- Four actual headless CLI captures at 256x256 through the real app, all exited 0, all
  SHA256 distinct: `--view-transform agx --look none`,
  `--view-transform standard --look none`, `--view-transform agx --look punchy`, and
  `--white-balance 3000`. PNGs in `/tmp/opencode/r1-cli/`.

## Remaining acceptance limits

R0's original requirement to compare pre-split bind/animated images byte-for-byte
cannot be certified from the available handoff: no matching pre-split snapshot/capture
pair was identified, and R1 plus other renderer tickets have already changed pixels.
Current determinism is proven; historical pixel preservation is not. Do not label
R0 fully verified on that basis. R1's focused acceptance tests pass and the aggregate
gate is now green, but the R0 prerequisite remains open. No broad renderer-completion
claim is made.
