# Instant Meshes auto retopo — packaging note

## What it is

`libgminstantmeshes.so` is the Instant Meshes auto-retopo backend used by
the Desktop core's `RetopoService.InstantMeshes` and
`RetopoService.AutoRetopo` (`core/RetopoService.cs`). It is the same library
Mobile ships in `app/addons/instantmeshes/lib/`: Instant Meshes
(BSD-3-Clause, `wjakob/instant-meshes`, commit
`7b3160864a2e1025af498c84cfed91cbfb613698`) plus the Geppetto
`gm_instantmeshes.cpp` shim, built by the Mobile tree's
`tools/build-instantmeshes.sh`. The Desktop copy in this folder is that
Linux x86_64 build, copied verbatim (sha256
`bbafbc629f463f0dcc37be4f50b5927e86415a55e9dfe34a3ea31f854d926eb3` —
verify against the Mobile tree before shipping a new copy).

## License

Instant Meshes: BSD 3-Clause License (`vendor/instant-meshes-LICENSE` in the
Mobile tree). The shim (`gm_instantmeshes.cpp`) is Geppetto code. The same
`.so` bundles Eigen (MPL-2.0) and oneTBB (Apache-2.0). Binary redistribution
is allowed. Recorded in `DistributionInventory.GetLicenseAudit()` as
"Instant Meshes (libgminstantmeshes.so)", SPDX `BSD-3-Clause`, Permissive,
approved.

## Where it must land in the published bundle

The loader (`InstantMeshes.Candidates()`) searches, in order:

1. `InstantMeshes.LibraryPathOverride` / the
   `GEPPETTO_INSTANTMESHES_LIB` environment variable (explicit override;
   tests use this).
2. `runtimes/<rid>/native/libgminstantmeshes.so` next to the published
   binary (`<rid>` is `linux-x64` / `win-x64` / `osx-x64`) — the same
   bundled-native layout `DistributionInventory` probes for SDL2/GLFW.
   **This is the shipping destination: `package-linux.sh` must copy this
   file there.**
3. `libgminstantmeshes.so` next to the binary (the Mobile Linux export
   layout).
4. The bare soname on the system search path (a host-installed shim).

## Rebuilding

Do not hand-edit the binary. Rebuild from the Mobile tree:

```sh
flock /tmp/gm-native-build.lock tools/build-instantmeshes.sh linux
```

then copy `app/addons/instantmeshes/lib/linux/x86_64/libgminstantmeshes.so`
here and re-verify the sha256 and the `gm_im_*` exports (`nm -D`). The
adapter requires the six exports `gm_im_version`, `gm_im_remesh`,
`gm_im_counts`, `gm_im_copy`, `gm_im_free`, `gm_im_error`; a build missing
any of them is treated as not installed. Note the adapter checks
`gm_im_version >= 1` — a Desktop addition; Mobile does not check it.

## Holes: the result is not guaranteed watertight

Measured on this machine (see `handoffs/retopo-backend-evaluation.md`):
Instant Meshes leaves holes — a subdivided head and a chisel came out with
small holes, bookends with 800 boundary + 23 non-manifold edges and 17%
under the face target, while QuadriFlow stayed watertight on every mesh.
Instant Meshes still won the evaluation narrowly (43 vs 42) on integration +
speed + already-shipped, with this condition: the retopo layer reports holes
honestly and never labels a holed mesh clean. That is what `AutoRetopo`
does — `LastBoundaryEdges` / `LastNonManifoldEdges` (from
`MeshValidator.Validate`) and a "not watertight" note in the result message.
Repair is the host's call (`MeshHoleRepair` in the kernel); nothing is
silently filled in here. If the final build shows the holes unrepairable,
the evaluation's fallback is QuadriFlow (fixtures + build notes in the same
handoff).

Follow-up, not done here: exposing upstream's `creaseAngle` in the shim
needs a Mobile-side change (`tools/instantmeshes/gm_instantmeshes.cpp`) +
rebuild + re-copy, and this task is Desktop-only, so it stays a follow-up.

## If the library is absent

`AutoRetopo.Prepare()` refuses with "Auto retopo isn't in this build..." and
adds no step; voxel remesh and manual retopo keep working. The library is
optional (`IsRequired: false` in
`DistributionInventory.GetNativeDependencies()`).

## What the native route keeps and loses

The native ABI carries positions and triangles only. The result drops UVs,
vertex colours, skin weights and morphs — the same as Mobile.
`AutoRetopo` re-derives UVs (`UVUnwrap`) and bakes normal + AO maps onto the
result when `BakeAfter` is set, and names
`RetopoService.TransferSkinWeights` as the host handoff for skin weights.
Target faces is a target, not a promise: results land within ~25% of the
target (the tests assert 0.75–1.3x) and quad mode is >80% quads. Cancel is
honoured at stage boundaries (`Cancel[0] = 1`); `Solve` runs on a worker
thread with progress in `Progress[0]`. The solve is deterministic: the same
input, target and settings give the same output.
