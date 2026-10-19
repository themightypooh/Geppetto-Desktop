# Mandatory rules for every desktop ticket

## Scope and safety

1. Read project `AGENTS.md`, this file, `CONTRACTS.md`, and your assigned ticket before editing.
2. Do only the assigned ticket. Do not design UI, add panels, re-theme anything, generate
   replacement models, or rewrite unrelated tools. Viewport drawing/input integration
   is allowed only when your ticket specifically owns it.
3. **Never launch a visible viewer for tests.** Use `--headless`, CPU tests or the
   windowless GPU smoke script. Do not use screenshots of the user's desktop.
4. There is no automatic visible fallback if EGL fails. Return a clear error and mark
   the GPU acceptance check blocked. Do not label an unrun GPU test “passed.”
5. Do not edit the user's source `.fbx`, `.effigy`, textures, `.vmdl`, scenes or animation
   files in other game projects. Use copied/generated fixtures in your test output directory.
6. No `git reset`, stash, broad deletion, or cleanup of unknown work. This desktop folder
   currently has no Git repository of its own. Preserve other workers' changes.
7. Do not delete `bin`/`obj` to make builds pass. Fix output-root collisions. For isolated
   multi-project builds use `dotnet build -c Release --artifacts-path <unique-root>`.
8. Do not edit `../geppetto/Editor/Effigy`: it is a generated mirror. Never introduce
   Sandbox, Godot, Unity, desktop GL, or UI types into the engine-free kernel.
9. No procedural “preview scene” spawners or hidden startup content generators. Offline
   generators must write explicit saved files. Opening a model must not generate motion libraries.
10. No source-asset overwrite, lossy conversion, export overwrite or dependency copying
    without an explicit destination and documented policy. Tests own only their fixtures.

## Read, then reuse

- Verify method signatures in source. Do not invent a `ReadAnimation`, `SaveProject`,
  `Undo`, etc. and claim it was already provided by the kernel.
- Do not port the s&box editor wholesale. Read it for behaviour where necessary, but
  use the engine-free APIs listed in your brief.
- Do not make a second implementation of lighting, skinning or timing just for tests.
  Headless and interactive rendering must continue to share `SceneRenderer`.
- A new abstraction must have an actual caller, or be explicitly marked a foundation
  sub-ticket with an executable fixture harness. “We will wire it later” is not a feature.

## Performance and threading

- File IO, image decoding, document rebuilds, clip parsing and import/export run off
  the render thread. GL resource creation/deletion runs on the owning GL thread.
- A reload job produces a separate complete result; it never mutates arrays being rendered.
  Publish with a clear ownership boundary and reject stale job results.
- Keep bone-key playback allocation-free. Do not accidentally query LINQ-backed
  `Clip.LastFrame` per frame or rebuild track lookups in the hot path.
- Live IK currently allocates. Keep correctness first and report that honestly until
  A4 provides a measured equivalent reusable-buffer path.
- No per-frame GPU readbacks, texture decoding, string formatting for unchanged state,
  or geometry rebuilding. `gl.Finish` belongs only in explicit verification measurements.
- State what is timed: cold rebuild, cache restore, CPU submission, GPU completion, or
  present/vsync. Headless GPU duration is **not** monitor FPS.

## Tests and reporting

From `/home/poo/games/projects/geppetto-desktop`:

```sh
dotnet run --project tests/Desktop.Tests.csproj -c Release
python3 tests/gpu-smoke.py
```

For a specific GPU fixture, after building, with no display connections:

```sh
env -u DISPLAY -u WAYLAND_DISPLAY dotnet bin/Release/net8.0/marionette-desktop.dll --headless --frames 1 --size 512x512 --screenshot /tmp/opencode/geppetto-ticket.png
```

`--time` is in seconds. Fixtures for a specific model need an explicit `--model`.
`../geppetto-verify/run.sh` is the slower full integration suite. Its known kernel
failures are not desktop fixes: do not expand the allowlist or hide new failures.

- Write a failing regression before the implementation where possible.
- Test normal, boundary and invalid input cases. Never fix a failing test by dropping
  its assertion, increasing its tolerance without explanation, or skipping the case silently.
- Do not hardcode production logic to Survey's 94 bones/23 pieces; those counts belong
  only to named Survey fixture checks.
- Keep tests independently selectable and registered in the existing executable test
  project. Do not introduce a second accidental `Main` via top-level C# statements.
- Leave commands and artifacts a reviewer can reproduce. Include a concise report:
  ticket; files changed; supported/unsupported cases; commands and exits; measurements;
  remaining blockers. Never write “all done” for a partially supported category.

## Stop and report, rather than improvise

Stop if you need an unowned shared file, an engine dependency, a new package, a public
kernel API change, an unspecified lossy policy, or a fixture you cannot obtain. Name
the exact blocker and propose the smallest change. Do not silently replace the task.
