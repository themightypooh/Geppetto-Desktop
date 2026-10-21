# 7. Standalone Linux distribution — D1 to D3

This is packaging the supported app, not inventing support for unimplemented features.
No UI redesign, no s&box installation, no automatic source downloads, no visible test launches.

## Existing build facts

`Marionette.Desktop.csproj` targets net8.0 and references a separate deterministic
`kernel/Effigy.Kernel.csproj`. The latter currently compiles `../../geppetto/Effigy/**/*.cs`.
That sibling source is a **build-time** dependency; it must not be a runtime requirement.
The app uses OpenGL 4.6; headless rendering uses Linux `libEGL.so.1` device/pbuffer.
Interactive input/windowing uses Silk.NET SDL. Verify actual published native dependencies;
do not assume NuGet bundles every system library or that publishing bundles a GPU driver.

The first packaging target is a self-contained Linux x64 directory/tarball. This is the
minimal verifiable deliverable, not a permanent decision against AppImage/Flatpak/deb.
Other formats/platforms need separate tickets after the runtime is demonstrably portable.

## D1 — Dependency, license and portability inventory

**Prerequisite:** none for inventory; P1/P2 before claiming source-path independence.
**Own:** packaging notes/scripts under a new packaging directory, not renderer code.

1. Inventory managed packages, native SDL/GL/EGL dependencies and actual host requirements.
   Inspect publish output and native library dependencies, not just `.csproj` declarations.
2. Separate build requirements (SDK, kernel source) from runtime requirements (driver/system
   graphics libraries). Do not bundle proprietary system driver libraries to hide missing EGL.
3. Inventory runtime absolute paths and external assets. Plan explicit project/model input
   or a bundled owned sample; do not ship `/home/poo/games/projects/...` as the only startup path.
4. Check redistribution rights/licenses for dependencies and every proposed sample asset.
   Use synthetic/explicitly owned fixtures by default. Do not bundle Facepunch models or
   proprietary game content because they happen to be installed on the developer's machine.
5. State the honest supported matrix: currently Linux x64 + compatible GL 4.6, with hardware
   EGL required for headless GPU checks. Do not claim macOS/Windows or arbitrary GPU support.

**Done:** checked dependency/license manifest, portable-path blockers with owners, and a
reproducible publish plan. This inventory alone is not a release artifact.

## D2 — Reproducible self-contained publish and archive

**Prerequisites:** D1; integrated P1/P2 or another explicitly approved portable input path.

1. Add a packaging script that stages a clean, uniquely owned output directory and invokes:
   `dotnet publish Marionette.Desktop.csproj -c Release -r linux-x64 --self-contained true`.
   Select its output directory explicitly. Do not recursively delete an arbitrary caller path.
2. Initially avoid single-file/trimming shortcuts: reflection-based document serializers and
   native-library loading need separate compatibility tests before enabling either feature.
3. Include launcher/instructions, application/runtime files, dependency notices, a capability
   list and an owned sample project. Exclude source-worktree detritus, secrets, caches, private
   game assets, test histories and machine-specific paths/configuration.
4. Runtime content paths resolve relative to project or application content roots, not cwd.
   Writable config/cache/state goes to user locations, never into a read-only installation.
5. Make an archive with checksums and a version/build identifier. Record the exact SDK/runtime
   used. Re-running must not pick up unrelated previous publish files.

**Tests:** archive extracts to a directory with spaces; application files intact; sample opens
headlessly; no installed SDK required by the launcher; read-only installation still renders;
missing graphics libraries produce a clear error rather than starting a downloader/installer.

## D3 — Installed-artifact smoke test, not a source-tree smoke test

**Prerequisite:** D2. Test only the staged/extracted artifact.

1. Run the packaged executable directly from an unrelated cwd with `DOTNET_ROOT`, DISPLAY
   and WAYLAND_DISPLAY unset. Do not invoke `dotnet run` or fall back to the development DLL.
2. Use the bundled sample/project and `--headless`; verify exact-size output, finite valid
   pixels and a deterministic repeat. Run a native clip fixture and a bad-input fixture too.
3. In a controlled disposable environment, make the original source/SDK paths unavailable.
   Do not rename/delete the user's live repositories or uninstall their SDK to do this test.
4. Inspect opened dependency paths when possible: no original game/source checkout or
   private home directory asset may be required. Verify external texture/rig references are
   either bundled legitimately or clearly reported as missing.
5. Record environment/driver/GL/EGL versions and failure diagnostics. Lack of a second GPU or
   clean-machine environment is an untested matrix entry, not implied compatibility.

**Done:** packaged-binary logs + checksummed archive + exact reproduction command + supported
matrix. A successful `dotnet build` inside this checkout is not evidence of distribution readiness.
