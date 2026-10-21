# Desktop verification

All checks are headless. No test may open a visible viewer as a fallback.

## One-command modes

From `geppetto-desktop/`:

```sh
python3 tests/verify.py --mode cpu
python3 tests/verify.py --mode required-egl --require-local-fixtures
```

`python3 tests/gpu-smoke.py` is a convenience wrapper for **required-egl**, not a
skip-if-no-GPU job. Both modes build the test/app/kernel graph into a fresh unique
`/tmp/opencode/geppetto-verify-*/build` root, using MSBuild's per-project artifacts layout.
A failed build stops execution even if a stale DLL exists. Every subprocess has DISPLAY
and WAYLAND_DISPLAY removed and a bounded timeout.

Outputs include `verification.json`, build/command logs, `inventory.log`, `cpu-results.json`,
and (required-EGL only) three PNGs. Reports contain commands, exit codes, modes, explicit
GPU status, test inventory/counts/results, assembly hashes and CPU-side assembly MVIDs.
CPU mode records GPU `NOT_REQUESTED`, never GPU PASS. Required-EGL fails on missing
hardware context, software rendering, missing required model files, or invalid captures.
It does not measure monitor/present FPS or prove full renderer correctness.

## Fixture inventory

| Group/check | Data | Requirement |
|---|---|---|
| `baseline` | generated rigs, clips and triangles | CPU; no game assets or EGL |
| `harness` | isolated nested registries | CPU; includes deliberately failing assertions |
| `asset-paths` | disposable owned project/source/symlink fixtures | CPU; Linux-only cases explicitly skip elsewhere |
| `survey` (2 checks) | `player_suit.fbx` and sibling `player_head.fbx` | CPU asset integration; optional unless required |
| `renderer-image` | synthetic scenes through the shared `SceneRenderer` | EGL; retains one capture per renderer feature |
| GPU bind-a/bind-b/animated | same Survey assembly | required-EGL only; required assets and hardware |
| Python orchestrator self-tests | injected process/filesystem outcomes | CPU; no actual builds or GL context |

The Survey default is the developer's read-only source asset path. Override explicitly:

```sh
python3 tests/verify.py --mode cpu --survey /path/to/player_suit.fbx
```

Missing optional fixtures produce two named SKIPs. `--require-local-fixtures` makes
them FAIL. GPU mode always requires its assets. Nothing silently falls back to another
character. The broader pod/cache and kernel suites remain in `../geppetto-verify/run.sh`.

## Renderer feature captures (R12)

`renderer-image` is the renderer verification harness. It renders one synthetic scene per
renderer feature through the **shared** `SceneRenderer` — the same one the interactive
viewer and the windowless captures use — and keeps the resulting image:

```sh
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter renderer-image
ls /tmp/opencode/renderer-images/
```

Each feature writes a `.ppm` capture and a `.json` record of its probes beside it:

| Feature | Capture | What the probes assert |
|---|---|---|
| `antialiasing` | `antialiasing-{0,1}.ppm` | the edge resolves to the exact polygon/pixel coverage; MSAA beats no anti-aliasing |
| `bloom` | `bloom-{0,1}.ppm` | the halo carries energy, is bounded by the emitter's, and vanishes at zero intensity |
| `screen-space` | `screen-space-{0,1}.ppm` | ambient occlusion darkens a corner and leaves an open floor unchanged; the reflection appears only in a smooth floor where the mirror image projects |
| `shadow` | `shadow-{0,1}.ppm` | the shadowed region is darker; the penumbra widens with the radius parameter |
| `depth-of-field` | `depth-of-field-{0,1}.ppm` | the in-focus circle of confusion is zero and the subject stays sharp |
| `motion-blur` | `motion-blur-{0,1}.ppm` | the emitter smears along its screen velocity |
| `volumetrics` | `volumetrics-{0,1}.ppm` | fog scatters the key light; an empty grid leaves the frame identical |
| `material-lobes` | `material-lobes-{0..3}.ppm` | subsurface bleeds red at grazing angles; thickness attenuates the red channel fastest |
| `transparency` | `transparency-{0,1}.ppm` | hashed alpha is order independent; blended surfaces composite in draw order |

Every probe compares a measured value with an **independently computed** expectation: a
closed-form value (the thin-lens circle of confusion, the exact polygon/pixel coverage, the
Beer-Lambert attenuation), a reference render with the feature switched off, or a
one-sided physical bound. No probe asserts only that "the image changed".

The harness is self-tested in the `harness` group: a deliberately failing probe must be
reported as a failure, and each comparison mode (`near`, `at least`, `at most`) and the
capture writer are exercised, so a broken probe cannot silently pass. Making the probe
machinery never fail turns that self-test red.

Captures are written to `/tmp/opencode/renderer-images`, **not** to the run's work
directory: a passing run deletes its work directory, so a capture kept only there would
not be retained.

## Direct CPU runner and selecting tests

```sh
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --list
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter harness --report /tmp/opencode/harness.json
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --survey /nonexistent/player_suit.fbx
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --survey /nonexistent/player_suit.fbx --require-local-fixtures
python3 -m unittest discover -s tests -p test_verification.py -v
```

The first missing-fixture command succeeds with explicit skips; the second fails.
An empty filter selection exits nonzero. Direct CPU runs emit JSON even on ordinary test
failure. Host/argument errors exit 2. Each direct run records a unique fixture output directory.

New C# test files expose `Register(TestRegistry, TestSettings)` and must be added to
`DesktopTestHost`'s explicit registration manifest in `TestRegistry.cs`. No second Main or
top-level executable body. Duplicate `(group, name)` registration fails. The aggregate
runner checks selected results against the complete registered inventory.

## V1 acceptance evidence

- CPU orchestration: `/tmp/opencode/geppetto-verify-r48kn5m1/verification.json`.
- Required EGL: `/tmp/opencode/geppetto-verify-_cwywptv/verification.json`.
- Real CLI failure/skip/list/empty-selection cases: `/tmp/opencode/geppetto-v1-cli-cases/`.
- Observed: 30 C# tests passed; 8 Python fault-injection tests passed. GPU rendered
  3 × 120 frames on NVIDIA hardware with no display connections; bind images matched
  exactly and animated image differed. Counts grow as later tickets register tests.
- Unavailable-EGL and failed-build-with-stale-DLL behaviours are orchestration fault
  injections, not claims about another physical GPU. The real success path uses hardware EGL.

## P1 acceptance and resolver API

P1 acceptance at the time (`/tmp/opencode/geppetto-verify-moo9bjtm/verification.json` CPU and
`/tmp/opencode/geppetto-verify-py80i0os/verification.json` required EGL): **45 CPU tests
passed** (15 path fixtures), 8 Python harness tests passed, three windowless hardware captures
passed. The original 26 baseline checks remain present.

The **final** all-ticket gate on this host is **343/343 CPU fixtures passed, 0 failed,
0 skipped** plus hardware EGL, in
`/tmp/opencode/geppetto-final3/geppetto-verify-x3tc_7zg/verification.json`. Run it with
`python3 tests/verify.py --mode required-egl --require-local-fixtures`.

`AssetPathResolver.cs` is a CPU-only foundation used by executable fixture tests; loader/
session/project integration is assigned to E1/P2, not silently claimed complete here.

```csharp
var resolver = new AssetPathResolver( absoluteProjectDirectory );
var reference = new AssetReference( "stable-asset-id", "textures/skin.png" );
var source = resolver.ResolveRead( reference ); // requires an existing file by default
var pending = resolver.ResolveWrite( new AssetReference( "output-id", "exports/model.fbx" ) );
// Validation only: no directories or files have been created.

var external = new AssetReference( "read-only-id", absoluteSourcePath,
    AssetReferenceKind.ExternalReadOnly );
var readOnlySource = resolver.ResolveRead( external );
// ResolveWrite(external) is always rejected, even if the path is inside the project.
```

`ResolvedAsset` contains logical `Id`, symlink-resolved `CanonicalPath`, useful
`ReferencePath`, `Kind` and `Exists`. `requireExists: false` is an explicit probe for
unavailable/missing files; ordinary reads fail with a path/case diagnostic.

For Save As, `RelocateReference(reference, destinationResolver)` preserves source
identity and refuses references outside the destination root by default. Explicit
`allowExternal: true` permits a read-only external reference; it never copies source
content. An existing external remains external even when its path lies inside the new
root. Moving a complete project tree instead keeps the original relative references.

Project-relative references accept '/' and '\\' separators. On Linux native external
paths keep their filesystem spelling and case. Logical IDs are independent of basename
and canonical path. The resolver does not change cwd, source files, permissions or directories.

Symlink targets, dangling links, loops and existing ancestors of new output paths are
validated. This is **not a race-proof filesystem sandbox**: consumers must revalidate at
IO time and use proper atomic-save/open policies. Resolution alone cannot guarantee a
future write will have sufficient permissions or disk space. Those failures belong to E4.

## Live AI modelling

`--filter ai-live/` exercises immutable publications/cache reuse, incremental/torn journal
reading, provider/native-worker cancellation, and real persistent EGL viewer processes.
It includes rapid updates, a deliberately corrupted native publication, empty geometry,
run switching, reconnect, camera retention and stable GL object counts. A 49,152-triangle
cache-eligible fixture measures five steady commit-to-frame samples at 1024x576 against
the scoped 500 ms p95 target. Provider/tool execution time and first context startup are
outside that measurement. Provider responses are scripted; native geometry and rendering
are real. No visible application is launched.

`python3 -m unittest discover -s tests -p test_live_runs.py -v` checks the Python run
protocol and the actual cabinet QObject Watch/Unwatch/Stop bindings under QCoreApplication,
without creating a GUI window. It requires the cabinet's existing PySide6 installation.
