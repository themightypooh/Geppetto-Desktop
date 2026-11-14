# OpenVDB voxel remesh — packaging note

## What it is

`libgmvdb.so` is the OpenVDB voxel remesh backend used by the Desktop core's
`RetopoService.OpenVdb` (`core/RetopoService.cs`), wired into the kernel's
`RemeshFeature.VoxelBackend` hook
(`kernel/Effigy/Features/RemeshFeature.cs`). It is the same library Mobile
ships in `app/addons/openvdb/lib/`: OpenVDB v12.1.1 (Apache 2.0) plus the
Geppetto `gm_vdb.cpp` shim, built by the Mobile tree's
`tools/build-openvdb.sh`. The Desktop copy in this folder is that Linux
x86_64 build, copied verbatim (sha256
`c4589b434dcff8aabfe59dca40b9e4f160a4e1c20a2fe5a50d5630b251d4e895` —
verify against the Mobile tree before shipping a new copy).

## License

OpenVDB: Apache License 2.0 (`vendor/openvdb-LICENSE` in the Mobile tree).
The shim (`gm_vdb.cpp`) is Geppetto code. The same `.so` statically bundles
oneTBB (Apache 2.0, `vendor/onetbb-LICENSE`). Binary redistribution is
allowed. Recorded in `DistributionInventory.GetLicenseAudit()` as
"OpenVDB (libgmvdb.so)", SPDX `Apache-2.0`, Permissive, approved.

## Where it must land in the published bundle

The loader (`OpenVdb.Candidates()`) searches, in order:

1. `OpenVdb.LibraryPathOverride` / the `GEPPETTO_OPENVDB_LIB` environment
   variable (explicit override; tests use this).
2. `runtimes/<rid>/native/libgmvdb.so` next to the published binary
   (`<rid>` is `linux-x64` / `win-x64` / `osx-x64`) — the same bundled-native
   layout `DistributionInventory` probes for SDL2/GLFW. **This is the
   shipping destination: `package-linux.sh` must copy this file there.**
3. `libgmvdb.so` next to the binary (the Mobile Linux export layout).
4. The bare soname on the system search path (a host-installed shim).

## Rebuilding

Do not hand-edit the binary. Rebuild from the Mobile tree:

```sh
flock /tmp/gm-native-build.lock tools/build-openvdb.sh linux
```

then copy `app/addons/openvdb/lib/linux/x86_64/libgmvdb.so` here and
re-verify the sha256 and the `gm_vdb_*` exports (`nm -D`). The adapter
requires the five exports `gm_vdb_version`, `gm_vdb_error`,
`gm_vdb_remesh`, `gm_vdb_fetch`, `gm_vdb_free`; a build missing any of
them is treated as not installed.

## If the library is absent

Nothing breaks: voxel remesh falls back to the kernel's built-in
SurfaceNets path and `OpenVdb.LastRoute` records
`built-in (library not installed)`. The library is optional
(`IsRequired: false` in `DistributionInventory.GetNativeDependencies()`).

## What the native route keeps and loses

The native ABI carries positions and triangles per part; the kernel hook
re-applies the part's `Material` on the result (single-part voxel runs keep
the part's material). Per-corner UVs do NOT cross the ABI — an OpenVDB-routed
result has zero UVs, exactly as in Mobile; use the AutoRetopo bake handoff
(`UVUnwrap` + `NormalBake`) to re-derive maps. The built-in SurfaceNets
fallback also stays closed, but follows the SDF grid rather than the VDB
level set. `Skin()` is worker-thread safe (a pure function of its inputs);
`Stash`/`Key` lets a prepared skin be replayed at rebuild time.
