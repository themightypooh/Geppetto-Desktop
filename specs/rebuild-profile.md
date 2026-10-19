# Spec: Cold Rebuild Static Profile (`rebuild-profile.md`)

> ## CORRECTION 2026-10-17 — the ranked table below was MEASURED and does not hold up
>
> The mechanism analysis in this document is good and mostly confirmed. **The magnitudes and
> the ranking are not**, and the ranking is inverted at both ends. Measured directly by
> instrumenting `PartStudio.Rebuild` with a `Stopwatch` around `feature.Run( ctx )` and around
> `_cache.Add( Snapshot.Of( ctx ) )` — one `describe engine.effigy`, wall 12.781 s:
>
> | Measured rank | What | Measured | Share | This doc said |
> | :---: | :--- | ---: | ---: | :--- |
> | **1** | **`Hull_uv` — `UVProjectFeature`, organic unwrap** | **3.824 s** | **30%** | 1.2 – 1.7 s, **rank 4 of 4** |
> | **2** | `Hull_lod` — `RemeshFeature`, decimate | 3.173 s | 25% | 4.6 – 5.3 s, **rank 1** |
> | **3** | `Hull` — `BlobFeature`, SurfaceNets + Taubin | 3.110 s | 24% | 2.3 – 2.8 s, rank 3 |
> | **4** | The 325 per-feature snapshots (deep clone) | 2.356 s | 18% | 3.3 – 4.0 s, rank 2 |
> | **5** | The other 322 features, all of them together | **0.065 s** | **0.5%** | 0.6 – 0.9 s, rank 5 |
> | | features total (all 325) | 10.172 s | 80% | |
> | | read + parse + final body clone + `MaterialScale.Apply` | ~0.25 s | 2% | |
> | | **total cold rebuild** | **12.781 s** | **100%** | 12.73 s |
>
> **The three "first three features" are 10.107 s, which is 79% of cold. Everything else in the
> document, all 322 remaining features, is 0.065 s — half of one percent.** The 0.6 – 0.9 s
> this document assigns to them is more than ten times the truth, and it was covering a real
> cost: the 2.356 s spent cloning every body after every feature.
>
> **The unwrap is the single biggest cost, and this document ranked it last.** Its own §4.4
> analysis is the reason: iterative LSCM with Dijkstra relief cuts and three full re-solves on a
> chart that `OrganicSeams` left as ~15,000 triangles. The analysis was right, the ranking was
> not, and only measurement separated the two.
>
> **What survives, and it is the valuable half:** the specific loops and allocations named
> throughout — `Triangulate.Face` ear-clipping every quad in `Decimate` (`Decimate.cs:580`),
> unpresized `weldMap` and `HashSet<EdgeKey>`, `PlanUVs` allocating per collapse, 351,396
> `List<(Sdf,float)>` in `sdf.Nearest`, 351,396 `HashSet<int>` in `MeshSmooth.Neighbours`,
> 16,000 `HashSet<int>` in `NormalBake.Measure`, and `Snapshot.Of` deep-cloning every face via
> `PolyMesh.Clone` (`PolyMesh.cs:478-495`, `Body.Clone` at `Feature.cs:75`). The allocation
> counts are within reason for the measured times — the snapshot estimate of ~9.33M face clones
> predicts roughly 2.3 s, and the measurement is 2.356 s. **The mechanism is confirmed; the
> ranking was a guess and it was wrong.**
>
> Practical consequence for cold builds: 79% is three features, the biggest is the unwrap, and
> the cheapest large win is the snapshots (2.356 s, 18%, and a much simpler change than the
> other three). None of it affects a warm launch, which is 0.094 s.


This document provides an independent static code analysis of why a cold load and rebuild of `/home/poo/games/projects/podracer/tools/effigy/engine.effigy` (4,973 lines, **325 features**, **419 output bodies**, **87,252 output triangles**) takes **12.73 seconds** in `StudioDocument.ReadFile` + `PartStudio.Rebuild`.

---

## 1. Executive Summary & Ranked Bottleneck Expectations

Over **90% of the 12.73-second cold rebuild time** is concentrated in **four specific mechanisms**—three of which execute on the first three features of the document (`#0 Hull`, `#1 Hull_lod`, `#2 Hull_uv`), while the fourth (`PartStudio._cache` per-feature snapshot deep-cloning) amplifies mesh memory allocations across all **325 feature steps**:

| Rank | Bottleneck | Primary Code Locations (`file:line`) | Expected Time (s) | Expected Share (%) | Root Cause Summary |
| :---: | :--- | :--- | :---: | :---: | :--- |
| **1** | **`Hull_lod` (`RemeshFeature` #1 $\to$ `Decimate.Run`: 351,394 quads $\to$ 16,000 tris)** | `Effigy/Features/RemeshFeature.cs:198-206`<br>`Effigy/Decimate.cs:531-998`<br>`Effigy/Decimate.cs:580` (`Triangulate.Face`)<br>`Effigy/Decimate.cs:844` (`PlanUVs` alloc) | **4.6 – 5.3 s** | **36 – 42%** | `WorkingMesh` runs full 3D-to-2D projection + ear-clipping (`Triangulate.Face`, 4 heap allocs/quad) on **all 351,394 quads** instead of a 2-triangle split; un-presized `weldMap` (2.1M lookups) and `HashSet<EdgeKey>` (1.05M edges); ~1.05M initial `Quadric.Solve` calls; **~343,394 edge collapses** (~2M heap pops, ~5M `Sqrt`s in `WouldFlip`, and 343,394 `List<(Vec2,Vec2,Vec2)>` allocs in `PlanUVs`). |
| **2** | **Unconditional Per-Feature Deep-Cloning into `PartStudio._cache` Across All 325 Features (+ GC Pressure)** | `Effigy/Features/PartStudio.cs:539` (`_cache.Add`)<br>`Effigy/Features/PartStudio.cs:273-282` (`Snapshot.Of`)<br>`Effigy/Features/PartStudio.cs:544` (`_bodies` clone)<br>`Effigy/PolyMesh.cs:145-182` (`PolyMesh.Clone`) | **3.3 – 4.0 s** | **26 – 31%** | At the end of **every feature (`0..324`)**, `PartStudio.Rebuild` deep-clones every `Body` and every `Face` in `ctx.Bodies` (`new Face`, `int[].Clone()`, `Vec2[].Clone()` = 3 allocs/face). Clones 351,394 quads after step #0, then clones the 16,000-tri `Hull` **324 times** (5.18M faces) + accumulating bodies (~3.8M faces) = **~9.3M face clones (~28M heap allocations)** retained in `_cache[0..324]`, forcing blocking Gen2 GC collections. |
| **3** | **`Hull` (`BlobFeature` #0 $\to$ `SurfaceNets.Mesh` + `MeshSmooth.Taubin` at `Voxel=0.42`, `Smooth=3`)** | `Effigy/Features/OrganicFeatures.cs:57-126`<br>`Effigy/Implicit.cs:1285-1518` (`SurfaceNets`)<br>`Effigy/Implicit.cs:53-71, 1430` (`sdf.Nearest`)<br>`Effigy/Implicit.cs:1571-1640` (`Taubin`) | **2.3 – 2.8 s** | **18 – 22%** | Evaluates **12,649,360 voxels** ($655 \times 136 \times 142$) against 10 blended shapes (including 13-segment and 5-segment `SdfTube`s) and 5 negative cutters; single-threaded 12.45M-cell straddling scan + single-threaded 351,394-quad emission (1.05M allocs) + single-threaded `DropSpecks`; `sdf.Nearest` allocates **351,396 `List<(Sdf,float)>` instances**; `MeshSmooth.Neighbours` runs single-threaded and allocates **351,396 `HashSet<int>` + 351,396 `int[]` arrays**. |
| **4** | **`Hull_uv` (`UVProjectFeature` #2, `Mode=3` `"Organic"` $\to$ `UVUnwrap.Unwrap` + `NormalBake.Measure`)** | `Effigy/Features/SolidFeatures.cs:453-470`<br>`Effigy/UVUnwrap.cs:316-369` (`OrganicSeams`)<br>`Effigy/UVUnwrap.cs:852-1009` (`FlattenConformal`/`Split`)<br>`Effigy/UVUnwrap.cs:1253-1534` (LSCM PCG `Solve`)<br>`Effigy/NormalBake.cs:2173-2228` (`Measure`) | **1.2 – 1.7 s** | **9 – 13%** | `OrganicSeams` seeds seams only on material boundaries, leaving ~15,000 tris of the nacelle skin as one multi-loop chart; `FlattenConformal` runs Dijkstra `CutBetweenLoops`, matrix-free LSCM PCG `Solve` (iterating all ~15k tris per CG iteration), Dijkstra `CutToMostShrunk` relief cuts + full LSCM re-solves, and recursive 3-pass Dijkstra `Split` when stretch > `1.35`; `NormalBake.Measure` allocates **16,000 `HashSet<int>` objects** (`NormalBake.cs:2203`) and software-rasterizes 16,000 tris onto $256 \times 256$. |
| **5** | **Remaining 322 Features + Un-Cached Reflection (`ReadFile`, 20 `Profile`s, 15 `Fillet`s, 63 `Spline`s, 11 `CurveDeform`s, 126 `Part`s, 39 `Primitive`s, `MaterialScale`)** | `Effigy/Features/StudioDocument.cs:1336`<br>`Effigy/Features/Feature.cs:656`<br>`Effigy/Features/ProfileFeature.cs:190-544`<br>`Effigy/EdgeBlend.cs:126-260`<br>`Effigy/Spline.cs:218-412`<br>`Effigy/Features/MaterialScale.cs:23-95` | **0.6 – 0.9 s** | **5 – 7%** | `StudioDocument.Create` calls `typeof(Feature).Assembly.GetTypes()` **148 times** without caching (`StudioDocument.cs:1336`); 20 `ProfileFeature`s solving 51 sketches + `ProfileFinder.Find` + `MeshWeld.Weld`; 15 `FilletFeature`s (`EdgeBlend.FilletReport`); 52 `SplineTube.Build`s; 11 `CurveDeform.Apply`s; 126 `PartLibrary.Build`s; post-build `MaterialScale.Evaluate` across 419 bodies. |
| **—** | **Total Cold Build (`engine.effigy`)** | `StudioDocument.ReadFile` $\to$ `PartStudio.Rebuild` | **12.73 s** | **100%** | **325 features $\to$ 419 bodies, 87,252 triangles** |

---

## 2. Breakdown of the 325 Features in `engine.effigy`

`/home/poo/games/projects/podracer/tools/effigy/engine.effigy` defines **13 materials** (lines 2–15) and **325 features** (lines 16–4972), producing **419 bodies** and **87,252 triangles**:

| Feature Type (`TypeName`) | Count | Feature Indices (`#0..#324`) | Net Bodies Added | Role & Parameters in `engine.effigy` |
| :--- | ---: | :--- | ---: | :--- |
| **`Blob`** (`BlobFeature`) | **1** | `#0` (`Hull`, lines 16–30) | **+1** (`3701639ab0`) | `Voxel=0.42`, `Smooth=3`, `Blend=3`, `CutBlend=0.8`, `Noise=0`. Evaluates **10 positive SDF shapes** (including a 14-point/13-segment nacelle spine `tube` and a 6-point/5-segment dorsal ridge `tube`) + **5 negative `-capsule` cutters** on a $655 \times 136 \times 142$ grid (**12.65M voxels**), emitting **351,394 quads** (702,788 tris). |
| **`Remesh`** (`RemeshFeature`) | **1** | `#1` (`Hull_lod`, lines 31–49) | **0** (in-place on `Hull`) | `Mode=0` (`ModeDecimate`), `Target=1` (`TargetTriangles`), `Triangles=16000`, `Weld=1`, `HoldBorders=1`. Collapses `Hull` from **351,394 quads (702,788 tris) to 16,000 triangles**. |
| **`UVProject`** (`UVProjectFeature`) | **11** | `#2` (`Hull_uv`, lines 50–62) & `#313..#322` (`UV_engine_*`, lines 4821–4950) | **0** (in-place) | `#2` (`Hull_uv`): `Mode=3` (`Organic`), `ChartAngle=66`, `Margin=0.01`, `Scale=1` on `Hull` (16,000 tris).<br>`#313..#322`: **9 Box projections** (`Mode=0`, `Scale=0.04`, 224 body refs) + **1 Planar projection** (`Mode=1`, `Scale=0.04`, 11 body refs). |
| **`Profile`** (`ProfileFeature`) | **20** | 11 cowling flaps/panels (`Flap0..1`, `PU1..PL3`) + 9 engine parts (`Fin`, `Spinner`, `Stator`, `Blade`, `Petal`, `Petal_rib`, `Seal`, `Tailcone`, `TBlade`) | **+20** | 11 cowling panels solve 3 sketches (`Curve`, `Front`, `Side`), `Along=2`, `Sides=10`, `Squareness=0.8`.<br>9 aero/turbine profiles use `Sides=8..32`, with `Twist` on `Stator`, `Blade`, `TBlade`. |
| **`Spline`** (`SplineFeature`) | **63** | 11 guide curves (`Tube=0`) + 52 tube meshes (`Tube=1`) | **+52** | 11 non-tube guide splines (`Flap0_arc..PL3_arc`, `Resolution=8, Sides=12`).<br>52 tube splines (`Tube=1`): 12 flap lips/spines/rams/rods, `Fin_edge`, `Lip` (closed 32-pt, `Res=6, Sides=14`), `Rib0..7` (8 closed 36-pt rings, `Res=4, Sides=10`), `Ram_sleeve0..5` & `Ram_rod0..5` (12), `Clevis_L/R` & `Rope_eye_L/R` (6), `Prong0..3` (4), `Emit_coil` (30 pts), `Fuel_a/b`, `Belly0..2`, `Hyd_a/b` (7). |
| **`CurveDeform`** (`CurveDeformFeature`) | **11** | `Flap0_bend`, `Flap1_bend`, `PU1_bend..PL3_bend` | **0** (in-place) | Deforms the 11 cowling panel bodies along their `*_arc` spline features (`Axis=0, Resolution=8, Stretch=1`). |
| **`Transform`** (`TransformFeature`) | **31** | 22 panel `_tilt`/`_place`, 6 `Louvre0..5_lean`, `Petal_set`, `Seal_set`, `Seal_roll` | **0** (in-place) | Rigid `MeshTransform` translations/rotations on single target bodies. |
| **`Part`** (`PartFeature`) | **126** | `Flap0_r*`, `PU1_r*`, `PU1_b*`, etc. | **+126** | Kitbash detail fasteners placed via `Place.Surface` (`Mode=1`): **92 Rivets** (`Part=3, Count=6`, 30 quads each) and **34 Hex bolts** (`Part=2, Count=6`, 38 faces each). |
| **`Primitive`** (`PrimitiveFeature`) | **39** | Hinges, `Louvre0..5`, `Fan_hub`, `Throat`, `Ram_eye0..5`, lugs/swages, emitter parts, `Clamp0..3` | **+39** | 10 boxes, 20 cylinders (`Segments=10..28`), 2 tubes (`Throat` `Segments=36`, `Emit_base` `Segments=18`), 7 quadspheres (`Ram_eye0..5` `Divisions=2`, `Emit_ball` `Divisions=3`). |
| **`Fillet`** (`FilletFeature`) | **15** | `Louvre0..5_r` (6), `Throat_round` (1), `Emit_base_r` (1), `Emit_disc0..2_r` (3), `Clamp0..3_r` (4) | **0** (in-place) | `EdgeBlend.FilletReport` beveling sharp edges (`Angle=35..40`, `Segments=1` on 10 features, `Segments=2` on 5 features). |
| **`CircularPattern`** (`CircularPatternFeature`) | **5** | `Stators` (14×), `Fan` (23×), `Petals` (12× on 2 bodies), `Seals` (12×), `Turbine` (19×) | **0** (`Merge=1` on all 5) | `Effigy/Features/BasicFeatures.cs:256-288`: Because `Merge=1`, appends `Count - 1` rotated copies directly into the target `Body.Mesh` via `MeshTransform.Append`. |
| **`Mirror`** (`MirrorFeature`) | **2** | `#323` (`Left_side`, line 4951) & `#324` (`Right_emitter`, line 4962) | **+181** (`+164`, `+17`) | Both have `KeepOriginal=1, Merge=0, Plane=1` (mirror Y). `Left_side` clones and mirrors **164 bodies**; `Right_emitter` clones and mirrors **17 bodies**. |
| **Total** | **325** | `#0 .. #324` | **419 bodies** | **87,252 triangles** (`Hull` = 16,000 tris; other 418 bodies = 71,252 tris) |

*(Note: `engine.effigy` has **zero** `SubdivideFeature`s, **zero** explicit `BooleanFeature`s, and **zero** `QuadRemesh` features—all boolean cutting on `Hull` is performed inside `BlobFeature` via `SdfSubtract`, and all subdivision on primitives is inside `PrimitiveFeature` quadspheres at `Divisions=2..3`.)*

---

## 3. Exact Execution Path of `StudioDocument.ReadFile` & `PartStudio.Rebuild`

1. **`StudioDocument.ReadFile( string path )` (`Effigy/Features/StudioDocument.cs:531-534`)**
   - Calls `Deserialize( File.ReadAllText( path ) )` (`StudioDocument.cs:859-1037`).
   - Parses `material` lines (`:891-903`) and `feature <TypeName> <Id>` blocks (`:935-975`).
   - **Un-cached `Assembly.GetTypes()` Reflection (`StudioDocument.cs:940` $\to$ `:1308-1340`):**
     `StudioDocument.Create( string typeName )` has a hardcoded `switch` (`:1310-1334`) that **omits** `Blob`, `Remesh`, `Profile`, and `Part`. Every time one of those 4 feature types appears (`1 Blob + 1 Remesh + 20 Profile + 126 Part = 148 times`), execution falls through to line 1336:
     ```csharp
     var match = typeof( Feature ).Assembly.GetTypes()
         .FirstOrDefault( t => !t.IsAbstract && typeof( Feature ).IsAssignableFrom( t ) && ... );
     ```
     This allocates and scans the assembly's `Type[]` array **148 times** during file parse.
   - Calls `doc.Studio.SetFeatures( features )` (`StudioDocument.cs:1027` $\to$ `Effigy/Features/PartStudio.cs:402-407`), which calls `InvalidateAll()` (`_dirtyFrom = 0; _cache.Clear()`) and `Rebuild()` (`:406`).

2. **`PartStudio.Rebuild()` (`Effigy/Features/PartStudio.cs:451-579`)**
   - Creates `var ctx = new BuildContext()` (`:469`).
   - Loops `for ( int i = start; i < _features.Count; i++ )` (`0 .. 324`, `:508-540`):
     - `f.EvaluateExpressions( _variables, ... )` (`:520` $\to$ `Effigy/Features/Feature.cs:652-689`), calling `GetType().GetFields(...)` reflection per feature (`:656`).
     - `ExecuteFeature( f, ctx )` (`:521` $\to$ `:581-621`), which calls `feature.Execute( ctx )` (`:586`) and assigns default body IDs and material slots (`:589-605`).
     - **Unconditional Full-Context Deep Clone (`:539`):**
       ```csharp
       _cache.Add( Snapshot.Of( ctx ) );
       ```
   - After the loop finishes (`:542-579`):
     - Clones all 419 final bodies one more time: `_bodies = ctx.Bodies.Select( b => b.Clone() ).ToList()` (`:544`).
     - Runs `MaterialScale.Evaluate( _features, _bodies )` (`:549` $\to$ `Effigy/Features/MaterialScale.cs:23-95`), iterating all faces of all 419 bodies to compute 3D-to-UV edge length ratios per material.

---

## 4. Deep Static Analysis of the Four Primary Bottlenecks

### 4.1 Bottleneck #1: `Hull_lod` — Decimating 351,394 Quads to 16,000 Triangles (`Effigy/Decimate.cs:531-998`)

When Feature #1 (`Hull_lod`, `RemeshFeature.Execute` at `Effigy/Features/RemeshFeature.cs:198-206`) runs on `Hull` (`351,394` quads = `702,788` triangles, target = `16,000` triangles):

1. **Unnecessary Ear-Clipping of 351,394 Quads (`Effigy/Decimate.cs:563-595`):**
   - `WorkingMesh` constructor (`:572-586`) checks `if ( f.Count == 3 )` and sends **all 4-vertex quads (`f.Count == 4`)** into the general polygon `else` branch:
     ```csharp
     var corners = new Vec3[f.Count];
     for ( int i = 0; i < f.Count; i++ )
         corners[i] = mesh.Positions[f.Indices[i]];
     var tris = Triangulate.Face( corners );
     ```
   - Because `SurfaceNets.Mesh` outputs **351,394 quads**, `WorkingMesh` invokes `Triangulate.Face` (`Effigy/Triangulate.cs:71-147`) **351,394 times**. Each call computes a Newell normal (`FaceNormal`, `:154-169`), builds a 2D tangent basis (`BuildBasis`), allocates `new Vec3[4]`, `new Vec2[4]`, `new List<int>(4)`, and `new List<(int, int, int)>(2)` (**4 heap allocations per quad = 1,405,576 allocations**), and runs 2D ear-clipping instead of splitting `(0, 1, 2)` and `(0, 2, 3)` in $O(1)$.
2. **Un-Presized Collections on 351k Vertices / 702k Triangles / 1.05M Edges:**
   - `weldMap = new Dictionary<(long, long, long), int>()` (`Decimate.cs:544`) starts at capacity `0` and inserts `351,396` unique vertices across `2,108,364` corner lookups (~18 full dictionary rehashes).
   - `_tris = new List<Tri>( mesh.Faces.Count )` (`Decimate.cs:534`) is sized to `351,394` (quad count) instead of `702,788` (triangle count), forcing a 702k-struct array reallocation.
   - `LiveEdges()` (`Decimate.cs:967`) allocates `new HashSet<EdgeKey>()` with capacity `0` and inserts **~1,054,182 unique edges** across `2,108,364` half-edges.
3. **1.05M Initial Quadric Solves + ~343,394 Edge Collapses (`Effigy/Decimate.cs:664-960`):**
   - Accumulates 10-double `Quadric` matrices across `702,788` triangles (`:674-697`) and scans `2,108,364` half-edges for boundary/material/UV seam constraints (`:711-754`).
   - Evaluates `TryCost` + $3 \times 3$ Cramer's rule `Quadric.Solve` (`:402-449`) on all **~1,054,182 initial edges** and pushes them into the binary min-heap (`:898-908`).
   - Executes **~343,394 edge collapses** to drop from `702,788` to `16,000` triangles (`:911-960`). Each collapse:
     - Pops stale heap entries (~1.5M–2M total heap pops) and re-runs `TryCost`.
     - Runs `LinkConditionHolds` (`:1058-1114`) and `WouldFlip` (`:1119-1158`, computing 2 normals / `MathF.Sqrt` per incident triangle $\approx 5\text{M}$ `Sqrt` calls).
     - Calls `PlanUVs` (`:844, 1165-1202`), which allocates `new List<(Vec2 uvA, Vec2 uvB, Vec2 solved)>( 2 )` **on every collapse (343,394 times)** even though `Hull` has zero UVs at step #1!
     - Re-solves `Quadric.Solve` and pushes ~6–8 updated neighbor edges per collapse (~2.2M additional heap pushes).

---

### 4.2 Bottleneck #2: Per-Feature `Snapshot.Of( ctx )` Deep-Cloning (`Effigy/Features/PartStudio.cs:273-282, 539`)

At line 539 of `Effigy/Features/PartStudio.cs`, inside the `for ( int i = start; i < _features.Count; i++ )` loop:
```csharp
_cache.Add( Snapshot.Of( ctx ) );
```
`Snapshot.Of( ctx )` (`PartStudio.cs:273-282`) calls `b.Clone()` on every `Body` in `ctx.Bodies`, which calls `PolyMesh.Clone()` (`Effigy/PolyMesh.cs:145-182`):
```csharp
foreach ( var face in Faces )
{
    clone.Faces.Add( new Face(
        (int[])face.Indices.Clone(),
        (Vec2[])face.UVs.Clone(),
        face.Material ) );
}
```
Every cloned face performs **3 heap allocations** (`new Face`, `int[].Clone()`, `Vec2[].Clone()`):
- **After Feature #0 (`Hull`):** Clones **351,394 quads** (`1,054,182` heap allocations + `351,396` `Vec3` copies) into `_cache[0]`, which is immediately obsoleted on step #1 when `Hull_lod` replaces `Hull.Mesh`.
- **After Features #1 .. #324 (`Hull` at 16,000 tris):** Clones `Hull` (`16,000` faces) **324 times** = **5,184,000 face clones (15,552,000 heap allocations)** even though 322 of those features never touch `Hull`.
- **After Features #3 .. #324 (418 other bodies accumulating):** Clones another **~3,800,000 faces (~11,400,000 heap allocations)**.
- **Total:** **~9.33 million face clones (~28 million heap allocations)** all retained alive in `_cache[0..324]` (>150 MB of live object graph), triggering repeated full Gen2 garbage collections during `Rebuild()`.

---

### 4.3 Bottleneck #3: `Hull` — 12.65M-Voxel `SurfaceNets` + `Taubin` Smoothing (`Effigy/Implicit.cs:1285-1640`)

When Feature #0 (`Hull`, `BlobFeature.Execute` at `Effigy/Features/OrganicFeatures.cs:57-126`) runs at `Voxel = 0.42`, `Smooth = 3`:
1. **Voxel Grid Evaluation (`Effigy/Implicit.cs:1285-1311`):**
   - Padded AABB span $(274.28 \times 56.68 \times 58.88)$ at `voxel = 0.42` yields a **$655 \times 136 \times 142 = 12,649,360$-voxel grid** (`float[12_649_360]` = 50.6 MB on LOH).
   - `Parallel.For( 0, nx, ... )` evaluates `SdfSubtract` (10 positive shapes in a $48^3$ `SdfBlend` grid, including 13-segment and 5-segment `SdfTube`s, minus 5 `SdfCapsule` cutters) 12.65M times.
2. **Single-Threaded SurfaceNets Extraction (`Effigy/Implicit.cs:1319-1426`):**
   - Allocates `int[12_448_890]` (`cellVertex`, 49.8 MB on LOH).
   - Single-threaded loop (`:1336-1376`) checks all 8 corners of **12,448,890 cells** (~99.6M array reads) and interpolates **351,396 vertices**.
   - Single-threaded loop (`:1389-1424`) emits **351,394 quads** via `mesh.AddQuad` (**1,054,182 heap allocations**).
   - Single-threaded `DropSpecks` (`:1426, 1452-1518`) runs union-find across `1,405,576` face corners.
3. **Per-Vertex `sdf.Nearest` Material Assignment (`Effigy/Implicit.cs:53-71, 1428-1444`):**
   - `Parallel.For( 0, 351_396, ... )` calls `sdf.Nearest( positions[i], band )`, which allocates `new List<(Sdf leaf, float dist)>()` on every call (`Implicit.cs:55`) = **351,396 `List` allocations**.
4. **Single-Threaded `MeshSmooth.Neighbours` in `Taubin` (`Effigy/Implicit.cs:1571-1640`):**
   - `MeshSmooth.Neighbours` (`:1618-1640`) runs single-threaded on `351,396` vertices and `351,394` quads, allocating **351,396 `HashSet<int>` instances**, performing `2,811,152` `HashSet.Add` calls, and calling `sets[i].ToArray()` **351,396 times** (`351,396` `int[]` allocations), followed by 6 Laplacian smoothing passes (`Smooth = 3`).

---

### 4.4 Bottleneck #4: `Hull_uv` — `UnwrapMode.Organic` LSCM + `NormalBake.Measure` (`Effigy/UVUnwrap.cs:71-1959`, `Effigy/NormalBake.cs:2173-2228`)

When Feature #2 (`Hull_uv`, `UVProjectFeature.Execute` at `Effigy/Features/SolidFeatures.cs:453-470`) runs with `Mode = 3` (`UnwrapMode.Organic`) on the 16,000-triangle `Hull`:
1. **`OrganicSeams` (`Effigy/UVUnwrap.cs:316-369`):**
   - Marks seams only along material boundaries (`10` `engine_paint` vs `2` `engine_soot` / `4` `engine_dark`) and creases $> 135^\circ$, leaving the main nacelle hull (~14,000–15,000 triangles with multiple boundary loops around intake/exhaust/louvre cavities) as a single large chart.
2. **Iterative Cutting, Matrix-Free LSCM PCG, and Recursive Splitting (`Effigy/UVUnwrap.cs:852-1534`):**
   - `FlattenConformal` (`:852-954`) cuts between boundary loops using Dijkstra (`CutBetweenLoops`, `:1075-1087`).
   - Runs LSCM Preconditioned Conjugate Gradient (`Solve`, `:1253-1534`), where every CG iteration (`Apply`, `:1338-1362`) loops directly over all triangles in the patch (`for ( int t = 0; t < triCount; t++ )`).
   - Because the curved 3D nacelle exceeds `OrganicStretchLimit = 1.35f` (`:54`), `FlattenConformal` runs `ReliefCutRounds = 3` rounds of Dijkstra relief cuts (`CutToMostShrunk`, `:1106-1163`) + full LSCM PCG re-solves (`:917-934`), and then invokes recursive 3-pass dual-graph Dijkstra chart splitting (`Split`, `:947, 968-1009`) and re-solves the sub-charts.
3. **`NormalBake.Measure` Allocations (`Effigy/NormalBake.cs:2173-2228`):**
   - Called at `SolidFeatures.cs:470`; allocates a `65,536`-cell `List<int>[]` grid (`:2182`) and allocates `new HashSet<int>()` inside the per-face loop (`:2203`, **16,000 `HashSet<int>` allocations**) while software-rasterizing all 16,000 triangles onto a $256 \times 256$ texel grid.
