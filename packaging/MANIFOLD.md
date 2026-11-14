# Manifold native boolean — packaging note

## What it is

`libgmmanifold.so` is the Manifold mesh-boolean backend (union / difference /
intersect) used by the Desktop kernel's `ManifoldBoolean` provider
(`kernel/Effigy/ManifoldBoolean.cs`). It is the same library Mobile ships in
`app/addons/manifold/lib/`: Manifold v3.5.4 (Apache 2.0) plus the Geppetto
`gm_manifold.cpp` shim, built by the Mobile tree's `tools/build-manifold.sh`.
The Desktop copy in this folder is that Linux x86_64 build, copied verbatim
(sha256 `4d2a0c01ff59b00c6d75c5b258b74f841a7fa86d3744abcae04c7d259e6557fc` —
verify against the Mobile tree before shipping a new copy).

## License

Manifold: Apache License 2.0 (`elalish/manifold`, v3.5.4). The shim
(`gm_manifold.cpp`) is Geppetto code. Binary redistribution is allowed.
Recorded in `DistributionInventory.GetLicenseAudit()` as
"Manifold (libgmmanifold.so)", SPDX `Apache-2.0`, Permissive, approved.

## Where it must land in the published bundle

The loader (`ManifoldBoolean.Candidates()`) searches, in order:

1. `ManifoldBoolean.LibraryPathOverride` / the `GEPPETTO_MANIFOLD_LIB`
   environment variable (explicit override; tests use this).
2. `runtimes/<rid>/native/libgmmanifold.so` next to the published binary
   (`<rid>` is `linux-x64` / `win-x64` / `osx-x64`) — the same bundled-native
   layout `DistributionInventory` probes for SDL2/GLFW. **This is the
   shipping destination: `package-linux.sh` must copy this file there.**
3. `libgmmanifold.so` next to the binary (the Mobile Linux export layout).
4. The bare soname on the system search path (a host-installed shim).

## Rebuilding

Do not hand-edit the binary. Rebuild from the Mobile tree:

```sh
flock /tmp/gm-native-build.lock tools/build-manifold.sh linux
```

then copy `app/addons/manifold/lib/linux/x86_64/libgmmanifold.so` here and
re-verify the sha256 and the `gm_manifold_*` exports (`nm -D`). The adapter
requires the five exports `gm_manifold_boolean`, `gm_manifold_counts`,
`gm_manifold_copy`, `gm_manifold_free`, `gm_manifold_error`; a build
missing any of them is treated as not installed.

## If the library is absent

Nothing breaks: the provider falls back to the kernel's own
`ExactMeshBoolean` and `ManifoldBoolean.LastRoute` records
`built-in (library not installed)`. The library is optional
(`IsRequired: false` in `DistributionInventory.GetNativeDependencies()`).

## What the native route keeps and loses

The native ABI carries positions, triangles and one face id per triangle,
so per-face `Material` survives. Per-corner UVs do NOT cross the ABI — a
Manifold-routed result has zero UVs, exactly as in Mobile. The built-in
fallback keeps UVs where a face passes through unsplit.
