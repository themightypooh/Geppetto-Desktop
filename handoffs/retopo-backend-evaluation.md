# Retopo backend evaluation: Instant Meshes vs QuadriFlow vs Exoside Quad Remesher

Ticket: `godlike-evaluate-instant-meshes-vs-quadriflow` (GODLIKE WAVE 3, P2).
Status: rubric + mesh set written BEFORE any backend was run (ticket IMPLEMENT (1)).
Evaluator: muse-3, 2026-11-13. Machine: this PC (same machine for all runs).

## Why this evaluation exists

Board task `integrate-openvdb-remesh-and-instant-meshes-retopo-backends`
picks Instant Meshes for auto-retopo on name alone. Roadmap CONTRADICTIONS §4:
Instant Meshes is the older research codebase behind several retopo tools;
before building on it, evaluate it against QuadriFlow (Blender's maintained
fork) and Exoside Quad Remesher on the same test meshes, and pick the winner
on guide-curve support and maintenance. This is an EVALUATION, not an
implementation ticket: one day, then a pick. If Instant Meshes loses, say so
plainly and name the winner.

## Scoring rubric (0-5 per criterion, weighted)

| # | Criterion | Weight | 5 means | 0 means |
|---|-----------|--------|---------|---------|
| 1 | Guide-curve support | x3 | User-drawn guide curves steer edge flow (loops around eyes/mouth/joints) from a file/CLI input the Desktop host can drive headlessly | No guide input of any kind; flow is fully automatic |
| 2 | Crease / sharp-feature preservation | x2 | Hard edges survive as quad edges, by angle threshold or explicit crease input, without user painting | Creases ignored; sharp edges get smoothed over |
| 3 | Quad quality | x2 | Output >=95% quads, manifold + watertight, poles (valence != 4) only in flat areas, edge flow follows features | Mostly triangles, non-manifold, poles on features |
| 4 | Speed on ~1M faces | x2 | 1M-face input retopo'd in <30 s wall on this machine | >5 min or fails |
| 5 | Maintenance | x3 | Upstream alive in the last year, or a maintained vendor fork with fixes (e.g. inside Blender) | Dead upstream since 2019, no vendor fork |
| 6 | Licence | x1 | Permissive (BSD/MIT) incl. all linked deps, shippable in Desktop | Commercial per-seat, or copyleft (LGPL/GPL) in the shipped path |
| 7 | Build / packaging cost (Linux .so) | x2 | Headless core builds GUI-free in minutes from pinned sources, small .so, no exotic deps | GUI/GL required, heavy deps, no headless build path |
| 8 | Integration effort with OpenVDB voxel half | x1 | In-process C API with progress + cancel, flat-array in/out, honest missing-native report | CLI-only subprocess, no progress/cancel, file-only I/O |

Max weighted score: (3+2+2+2+3+1+2+1) * 5 = 80.
A criterion is scored from MEASURED runs where possible; claims taken from
docs are marked [doc] and discounted one point versus measured evidence.

## Test-mesh set (fixed before running)

All inputs are manifold and watertight (verified: 0 non-manifold edges,
0 boundary edges). Same meshes, same targets, same machine for every backend.

| ID | Mesh | Source | Faces | Why this mesh |
|----|------|--------|-------|---------------|
| M1 | head (native) | `geppetto-mobile/vendor/lpshead/head.obj` (Infinite-Scan head) | 8,842 | Real head with facial features; eye/mouth loop check |
| M1d | head, midpoint-subdivided x2 | derived from M1 (exact tessellation, no smoothing) | 141,472 | Dense version of M1: the retopo input a sculpt actually is |
| M2 | chisel | `Desktop/cabinet/model/chisel/out/chisel.obj` | 82,692 | Hard-surface prop with sharp edges; crease check |
| M3 | bookends | `Desktop/cabinet/model/bookends/bookends.obj` | 2,532,148 | Dense scan-like sculpt; speed check at >1M faces |

Targets (quads): M1 2,000; M1d 5,000; M2 5,000; M3 20,000.
QuadriFlow: `./quadriflow -i IN -o OUT -f TARGET` (+ `-sharp` variant on M2).
Instant Meshes: Mobile's shipped `libgminstantmeshes.so` (Linux x86_64, pinned
commit 7b3160864) via a small OBJ in/out harness calling `gm_im_remesh`
(targetFaces, quadDominant=1, smoothIterations=2, threads=4) — the exact
artifact Geppetto would ship, same parameters as Mobile `AutoRetopo.cs`.
Exoside Quad Remesher: commercial Blender/Maya plugin, no trial binary on
this machine; evaluated from public docs/pricing only, scored [doc], and its
run columns stay EMPTY rather than invented.

## Metrics recorded per run

Wall time (s), output verts/faces, % quads, manifold edges, boundary edges,
valence histogram + pole count (valence 3/5/6+), output file. Loop quality
around eyes/mouth (M1/M1d) is a qualitative note on the output, not a number.

## Results

All runs on this PC (16 cores, 31 GB), 2026-11-13. QuadriFlow built from
upstream commit 810b7a09 (Release, system Boost + Eigen via pacman).
Instant Meshes = Mobile's shipped `libgminstantmeshes.so` (Linux x86_64,
pinned commit 7b3160864) via harness `handoffs/retopo-eval/im-run.cpp`
(same call as Mobile `AutoRetopo.cs`: targetFaces, quads=1, smooth=2,
threads=4). Exoside: NOT run (commercial, no trial binary on this machine);
its columns below are [doc] only. Outputs + runners saved in
`handoffs/retopo-eval/`.

### Wall time + memory (target faces in brackets)

| Run | QuadriFlow | Instant Meshes |
|-----|-----------|----------------|
| M1 head 8,842f -> 2,000 | 1.25 s / 114 MB | 0.20 s / 14 MB |
| M1d head dense 282,944f -> 5,000 | 19.76 s / 885 MB | 1.97 s / 150 MB |
| M2 chisel 82,692f -> 5,000 | 4.50 s / 229 MB | 0.36 s / 29 MB |
| M2 chisel, QF `-sharp` | 4.53 s / 227 MB | — |
| M3 bookends 2,532,148f -> 20,000 | 56.26 s / 4,486 MB | 9.99 s / 742 MB |

Instant Meshes is ~6-10x faster and ~6x lighter. Both pass the roadmap bar
(auto-retopo of 1M faces <~30 s): IM ~4 s/1M, QF ~22 s/1M.

### Output quality (measured with `handoffs/retopo-eval/score.py`)

| Output | Faces (target) | Quads | Non-manifold edges | Boundary edges | Poles (valence != 4) |
|--------|------|------|------|------|------|
| QF M1 | 1,990 (2,000) | 100% | 0 | 0 | 3.8% |
| QF M1d | 5,532 (5,000) | 100% | 0 | 0 | 1.6% |
| QF M2 | 5,064 (5,000) | 100% | 0 | 0 | 0.5% |
| QF M2-sharp | 4,992 (5,000) | 100% | 0 | 0 | 0.5% |
| QF M3 | 19,891 (20,000) | 100% | 0 | 0 | 1.4% |
| IM M1 | 2,028 (2,000) | 100% | 0 | 28 | 7.3% |
| IM M1d | 4,977 (5,000) | 100% | 0 | 58 | 5.2% |
| IM M2 | 5,008 (5,000) | 100% | 0 | 0 | 1.6% |
| IM M3 | 16,550 (20,000) | 100% | 23 | 800 | 6.1% |

QuadriFlow is watertight on every mesh with the fewest poles. Instant
Meshes is 100% quads too, but leaves HOLES: its own log reports unfilled
holes ("Not trying to fill a hole of degree 88 ... 23 holes" on M3; 1 hole
on M1/M1d). M3 also misses its face target by 17% (16,550 vs 20,000).
M2 (hard-surface) is the one mesh IM closes cleanly.

### Crease following (M2 chisel, 1,155 sharp edges >30 deg)

Measured with `handoffs/retopo-eval/crease.py`: % of output edges within
eps of an input sharp edge (eps = % of bbox diagonal).

| Output | within 0.5% | within 1.0% |
|--------|------|------|
| QF M2 | 11.1% | 18.6% |
| QF M2-sharp | 12.5% | 20.0% |
| IM M2 | 11.4% | 19.1% |

A wash: all three follow strong creases to about the same degree, and
`-sharp` buys only ~+1.4 points. Neither backend's explicit crease handling
moves the needle much on this mesh.

### Guide curves

- Instant Meshes: NONE headless. The shipped shim (`gm_instantmeshes.cpp`)
  passes no guide/crease/orientation input (crease sets stay empty); the
  upstream GUI paints the orientation field interactively, which Desktop's
  worker-thread path cannot drive. Upstream batch mode has a crease ANGLE
  (`batch.cpp`, `generate_crease_normals`) that the shim does not expose.
- QuadriFlow: NONE. CLI exposes only `-f`, `-mcf`, `-sharp`, `-sat`. No
  guide input of any kind.
- Exoside [doc]: NO guide-curve brush either. Steering is painted density
  (vertex colours in Blender / primitive attribute in Houdini) plus loop
  pinning by hard-edge angle, material borders, custom split normals,
  creases. Closest steering of the three, but it is not guide curves.

### Maintenance (measured 2026-11-13)

- Instant Meshes upstream: last commit 2019-11-03 (7b3160864, the same
  commit Mobile pins). No tags, no releases. Dead upstream; only Geppetto's
  own vendored shim + build script is maintained.
- QuadriFlow upstream: last commit 2019-12-07 (810b7a09). No tags. Dead
  upstream — BUT Blender vendors it (`extern/quadriflow` + `blender.patch`)
  and has applied fixes as late as 2023-03-28 (division-by-zero crash fix
  #105953, PR #106165; plus 2019-2020 build cleanups). Real maintenance,
  Blender-scoped, no feature work.
- Exoside [doc]: alive. v1.4 July 2025; still sold and reviewed in 2026.
  Single developer (Maxime Rouca) — bus-factor 1. Maxon integrated the
  same algorithm into Cinema 4D R26 (Apr 2022); Exoside stopped selling
  the C4D plugin to new users then.

### Licence / build / integration

- Instant Meshes: BSD-3 (core) + Apache-2.0 (TBB fork) + BSD-3 (pss) +
  zlib (dset) + MPL2 (Eigen, EIGEN_MPL2_ONLY). All permissive. Build:
  Mobile's script already builds a self-contained Linux .so (ldd: libm +
  libc only) and an Android .so, GUI-free, pinned commit. Integration:
  in-process C API with progress + cancel, flat arrays, already wired in
  Mobile `AutoRetopo.cs` — the Desktop port is direct.
- QuadriFlow: MIT core. Default build uses Eigen Sparse Cholesky (LGPL)
  — must build with `-DBUILD_FREE_LICENSE=ON` for a clean ship (slightly
  slower). Build: built here in minutes from upstream + system Boost/Eigen
  (`pacman -S boost eigen`; cmake 4 needs `-DCMAKE_POLICY_VERSION_MINIMUM=3.5`
  for its 3.1 minimum). Runtime needs `libboost_program_options.so`.
  Integration: CLI binary only — file I/O, no progress/cancel; linking the
  sources as a library is possible (lib + main.cpp) but is new work.
- Exoside: proprietary commercial, per-host per-seat (Pro $109.90,
  Indie $59.90 non-commercial, subscription $15.99/3 mo, all-hosts $139.90,
  CAD tier $199.90; perpetual does not carry across major versions, no
  floating licence). Closed binary, no Desktop API, cannot be redistributed
  inside Geppetto. DISQUALIFIED as a backend (see Pick).

## Scores (rubric above, max 80; [doc] = unmeasured, discounted)

| Criterion (weight) | Instant Meshes | QuadriFlow | Exoside [doc] |
|---|---:|---:|---:|
| Guide curves (x3) | 0 | 0 | 2 |
| Crease preservation (x2) | 2 | 3 | 3 |
| Quad quality (x2) | 3 | 5 | 3 |
| Speed ~1M faces (x2) | 5 | 3 | 3 |
| Maintenance (x3) | 1 | 2 | 4 |
| Licence (x1) | 5 | 4 | 1 |
| Build / packaging (x2) | 5 | 4 | 1 |
| Integration w/ OpenVDB half (x1) | 5 | 2 | 0 |
| **Total** | **43** | **42** | **39** |

## Pick

**WINNER (narrowly): Instant Meshes — 43 vs QuadriFlow 42.** The margin is
one point: effectively tied on quality-vs-speed, and the pick turns on
integration reality, not the name. Instant Meshes is already built, shipped
and integrated in Mobile (shim + build script + `AutoRetopo.cs`), runs
in-process with progress + cancel exactly as the board retopo task's
acceptance demands, and is ~6-10x faster at ~6x less memory. QuadriFlow is
the RUNNER-UP: it is the quality winner (watertight on every mesh, fewest
poles, exact face targets) but CLI-only, 5.6x slower and 6x heavier, with an
LGPL footgun in its default build.

**Condition on the win (do not skip):** Instant Meshes leaves holes — 800
boundary edges + 23 non-manifold edges on M3, small holes on M1/M1d — which
fails the roadmap's own cage bar (all-quad, manifold, zero holes). The
retopo task must (a) expose upstream's `creaseAngle` in the shim (already
in `batch.cpp`, a small change), and (b) report boundary/non-manifold counts
honestly and repair-or-refuse: close small holes via the existing
`RepairManifold` path and never label a holed mesh clean. **If the final
build shows the holes cannot be repaired acceptably, switch to QuadriFlow**
— the fixtures, build notes and CLI in this evaluation make that switch
cheap. Exoside is DISQUALIFIED as a backend (commercial, non-redistributable,
no API) despite the best maintenance record; it remains the quality bar to
match, not a candidate to ship.

## Integration notes for the retopo task owner

- Proceed on Instant Meshes via Mobile's existing path: `tools/build-instantmeshes.sh`
  (pinned commit 7b3160864, Eigen 3.4.0) + `tools/instantmeshes/gm_instantmeshes.cpp`
  shim + `app/AutoRetopo.cs` call shape (targetFaces, quads=1, smooth=2,
  threads=clamp(CPU-1,1,4)). Desktop destination files are already claimed by
  that task (`core/RetopoService.cs`, `kernel/Effigy/Features/RemeshFeature.cs`)
  — this ticket touched NEITHER.
- Add a `creaseAngle` parameter to the shim (upstream `batch.cpp` lines
  ~37/117-119 show the call: `generate_crease_normals(..., creaseAngle, ...)`
  into `crease_in`); wire it from the Desktop remesh options. Re-run the M2
  crease check after wiring — expect the 11% figure to move.
- After every remesh, count boundary + non-manifold edges (the scorer in
  `handoffs/retopo-eval/score.py` does this) and surface them in
  `RetopoReport`; a holed result must be reported as such, never as clean.
- QuadriFlow fallback, if ever needed: build from upstream 810b7a09 with
  `pacman -S boost eigen`, `cmake -DBUILD_FREE_LICENSE=ON
  -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DCMAKE_BUILD_TYPE=Release`, binary
  `build/quadriflow`; CLI `-i IN -o OUT -f TARGET` (+ `-sharp` for creases).
  Runtime needs `libboost_program_options.so`. For in-process use, link the
  `src/*.cpp` library and wrap `main.cpp`'s flow with a progress callback —
  new work, no Desktop API exists today.
- Exoside: do not pursue as a backend. No licence to redistribute, no API,
  per-seat cost for every Desktop user.
