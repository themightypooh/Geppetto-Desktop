CURRENT IMPLEMENTATION HANDOFFS: handoffs/README.md
This file preserves the original porting brief. New workers must use the current bounded
tickets, contracts and headless-only rules under handoffs/; do not treat old milestones as verified.

=====================================================================
HANDOFF - Marionette / Effigy as a native Linux desktop app
Written 17 Oct 2026 by Joshua Diggs' assistant (pooh session)
=====================================================================

READ THIS FIRST
---------------
This document is a complete brief. You should not need anything else to
start work, but the repository itself is the source of truth and you
should verify claims here against the code before acting on them.

The person you are working with is Joshua Diggs (themightypooh). Read
"WORKING WITH HIM" below before you write a single line. It matters.

=====================================================================
1. THE GOAL
=====================================================================

Turn "Geppetto" - his s&box modeller + animation suite - into a native
Linux desktop application that runs with NO game engine and NO editor
dependency.

  Effigy      = the parametric modeller (CAD-like feature tree, sculpt,
                retopo, paint, rig, clothing). Exports .vmdl for s&box.
  Marionette  = the animation editor (pose bones, keyframes, easing
                curves, two-bone IK, joint limits, bake clips).
  Geppetto    = the umbrella name for both.

The desktop app must be GPU-first. He explicitly rejected any plan that
involves a low-framerate viewport, even temporarily.

WHY HE WANTS THIS (not just a lark):
  - It unlocks selling the tool to people who do not own s&box.
  - It unlocks driving other engines: Godot, and Roblox (see the
    Roblox backend idea, section 9).
  - It removes the s&box install requirement entirely.

=====================================================================
2. WORKING WITH HIM - NON-NEGOTIABLE RULES
=====================================================================

- NEVER type into, paste into, or press send on any message box on his
  behalf. Not Discord, not email, not forums. Drafts only, given to him
  as plain text he can copy. He hit send himself; you do not. This was
  a hard correction and he was angry about it.
- Write drafts in HIS voice for anything he sends: lowercase, short
  sentences, no em-dashes, no "I hope this finds you well". Anything
  that reads like a press release gets called out as "sounding AI".
- Never claim experience or skill he cannot back up. He has corrected
  this repeatedly. If something is unfamiliar, say so plainly in the
  draft rather than hiding it.
- He hates process for its own sake, and hates being asked to confirm
  things he already said. Move.
- Do not edit his live repository when a sibling folder will do.
- Do not present unverified numbers. Measure, then report. He responds
  well to real measurements and badly to confident guesses.
- He is direct and swears a lot. That is not a signal to be careful
  with him; it is just how he talks. Match his pace, not his profanity.

=====================================================================
3. VERIFIED ENVIRONMENT
=====================================================================

Machine        : Linux, KDE, Wayland session (wayland-1), XWayland on :0
CPU            : AMD Ryzen 7 5800X, 8 cores / 16 threads
GPU            : NVIDIA GeForce RTX 5060 Ti, 8151 MB, driver 615.78.08
Graphics       : OpenGL 4.6 core / GLSL 4.60, Vulkan 1.4
Libraries      : libGL.so, libvulkan.so, libSDL2-2.0.so present
dotnet         : 10.0.402 (default) and 8.0.426 SDKs
qmake6         : present.  gcc/g++ present.
NOT installed  : cmake, node/npm, mono, godot, cargo, zig, scrot, grim
Present for
screenshots    : spectacle (KDE), import (ImageMagick)
Also missing   : /usr/bin/time, bc, glxinfo, vulkaninfo (partly)
NuGet          : reachable, api.nuget.org HTTP 200 in ~0.39 s

NOTE: the shell tool has no bc. Do timing maths with awk.
NOTE: benchmarks should run the built DLL directly, not `dotnet run`,
      because `dotnet run` adds ~0.4 s of startup. Baseline measured
      for `describe` on an empty doc: 0.08 s.

=====================================================================
4. REPOSITORY FACTS - MEASURED, NOT GUESSED
=====================================================================

MAIN REPO: /home/poo/games/projects/geppetto   (git, active, has
modified files already - DO NOT commit or clean anything without asking)

File and line counts:
  Effigy/                   244 files   117,065 lines   KERNEL
  Code/                      32 files     8,301 lines   s&box runtime
  Editor/                   372 files   194,734 lines   s&box editor
    Editor/Effigy/          240 files   115,660 lines   KERNEL MIRROR
    Editor/EffigyEditor/     91 files                 Effigy modelling UI
    Editor/RigControlEditor/ 34 files    18,387 lines   Marionette UI
  UnitTests/                  5 files        67 lines

THE KERNEL IS GENUINELY ENGINE-FREE. Verified two ways:
  a) Only 3 of 244 kernel files even mention "Sandbox", and all three
     are COMMENTS explaining why they do NOT use engine types:
       Effigy/Vec.cs:10,72  - "The moment it references Sandbox.Vector3
                              or Godot.Vector3 that stops being true"
       Effigy/Rig/RigLimit.cs:19    - references Rotation.Lerp for parity
       Effigy/GarmentFit.cs:2965    - references an s&box enum path
  b) It compiles and runs on Linux with no engine present. Done today:
       dotnet build Effigy.Tests/Effigy.Tests.csproj -c Release
       -> Build succeeded, 0 errors, 6.3 s
       -> Effigy.Tests: 95 bones / 6272 tris from citizen.vmdl (simrig)
       -> Effigy.Tests: played a real clip and linted its motion, found
          a genuine pop: hand_R @ frame 4, 44 deg/frame vs 9.1 around it

THERE IS NO ENGINE PHYSICS IN THE PROJECT. The simulation is all in the
kernel: Effigy/Rig/SimRig.cs, ChainIk, ChainSpring, SoftBone, RigLimit,
RigSoftCollision. Good news for a port.

KERNEL ALREADY OWNS THESE EXPORTERS/READERS:
  FbxWriter / FbxBinary / FbxReader   (Roblox + general interchange)
  DmxWriter / DmxAnimWriter           (Source 2 / s&box - engine specific)
  ObjWriter, SmdWriter, PngReader, PngWriter

KERNEL ALREADY OWNS A CPU RENDERER (4,263 lines, Effigy/Render/):
  Raster, ShadedRender (1417), ModelRender, ShadeEnvironment, ShadeMaps,
  CoverageResolve, Turnaround, EyeRender, ReferenceMatch
  Already multithreaded with Parallel.For throughout.

s&box coupling hotspots, by reference count in Marionette's UI:
  Model 123, Gizmo 112, Widget 106, Editor.Label 75, GameObject 39,
  Scene 36, Material 8, DockWindow 6, CameraComponent 5, Editor.Mcp 4

Code/RigControl: 16 of 20 files are engine-bound. Engine-FREE ones are
exactly: RigCurve.cs, RigDriverMath.cs, RigSpringPresets.cs,
RigTrackName.cs. The heavy engine-bound ones:
  RigAnimPlayerComponent.cs 1369 lines  (live playback)
  RigConstraintSolver.cs     673 lines
  RigSoftChain.cs            602 lines
  RigAnimDocument.cs        1111 lines
  RigKeyOps.cs               332 lines
Note these mirror maths the kernel already has engine-free.

Also important: the kernel deliberately matches engine behaviour where
it matters. EFFIGY-API.md states RigAnimFile "matches s&box's own
Transform.Lerp to float precision", and the kernel "can play a Marionette
clip on a real model and check it, with no editor running". There is a
CLI (Effigy.Tests/MarionetteCli.cs) with solvers ccd|fabrik|spline, and
MarionetteParityTests keeps kernel and editor honest.

Effigy.Tests.csproj compiles the kernel from source on purpose, with a
comment explaining it is "meant to be copied into an s&box or Godot
project as loose .cs files". tools/sync-kernel.sh states that keeping
Effigy/ outside any engine folder "is what keeps the Godot option open".
So the engine-free design is intentional, not accidental.

=====================================================================
5. WHAT HAS ALREADY BEEN BUILT (today)
=====================================================================

NEW FOLDER, separate from the repo so nothing live was touched:
  /home/poo/games/projects/geppetto-desktop/

  Marionette.Desktop.csproj   net8.0, Exe, AllowUnsafeBlocks
    <Compile Include="../geppetto/Effigy/**/*.cs" />   <- kernel, unedited
    PackageReference Silk.NET.Windowing            2.23.0
    PackageReference Silk.NET.Windowing.Sdl        2.23.0
    PackageReference Silk.NET.OpenGL               2.23.0
    PackageReference Silk.NET.Input                2.23.0

  Program.cs   window + GL context + frame counter
    flags: --frames N (draw N frames then exit), --size WxH

VERIFIED WORKING:
  GL       : 4.6.0 NVIDIA 615.78.08
  renderer : NVIDIA GeForce RTX 5060 Ti/PCIe/SSE2
  GLSL     : 4.60 NVIDIA
  120 frames drawn, exit 0, native Wayland

GOTCHA FOUND AND FIXED: Silk.NET's SDL backend returns a 3.3
compatibility context by default. You must request it explicitly:
    API = new GraphicsAPI( ContextAPI.OpenGL, ContextProfile.Core,
                           ContextFlags.ForwardCompatible,
                           new APIVersion( 4, 6 ) )
Without that you get GLSL 3.30 and no modern shader features.

BUILD/RUN COMMANDS
  cd /home/poo/games/projects/geppetto-desktop
  dotnet build -c Release
  dotnet bin/Release/net8.0/marionette-desktop.dll --frames 120

=====================================================================
6. CONTENT TO PUT ON SCREEN (his own, not engine assets)
=====================================================================

He explicitly rejected the s&box "citizen" model - it is Facepunch's
asset and it would be the last s&box thing in the app.

HIS OWN MODELS, all confirmed on disk:
  /home/poo/games/projects/podracer/tools/effigy/engine.effigy
      Podracer engine. "that mf pretty" - HE ASKED FOR THIS ONE.
      419 bodies, 281 x 70.4 x 76.5 units, 87,252 triangles.
      Declares its materials as named UV_* features already:
        UV_engine_paint, UV_engine_metal, UV_engine_chrome,
        UV_engine_soot, UV_engine_rubber, UV_engine_brass,
        UV_engine_ceramic, UV_engine_hot, UV_rein_cable
      Known issue noted by the tool itself: "Hull: 351,394 quads ...
      It has no UVs yet: add a UV project (Unwrap) before baking."
  /home/poo/games/projects/cookin/Assets/models/players/*.effigy
      camhead, stringbean, gearhead, line_cook, egghead, brute
      camhead: 617 bodies, 63,236 tris, 75 units tall. He called it
      "complicated af ... idk might be a good test".
  /home/poo/games/projects/podracer/tools/effigy/pod.effigy, pilot.effigy
  /home/poo/games/projects/cookin/Assets/models/effigy/flattop.effigy

Useful for reference only: a CPU render of the pod engine was produced
today at /tmp/opencode/pod.png (13.19 s wall, including building all 325
features of the document). The 13 s is the DOCUMENT BUILD, not shading -
this is the likely source of any hitching in the app, not the GPU.

=====================================================================
7. DECISIONS ALREADY MADE - DO NOT RELITIGATE
=====================================================================

1. GPU-FIRST, from the start. He rejected a CPU viewport outright.
2. NO engine, NO editor framework. Silk.NET + OpenGL, not Godot.
   An OpenGL binding is a library; Godot would be an editor dependency.
3. We write our own shaders. GLSL 4.60. No .vmat at runtime.
4. Keep the CPU renderer untouched for final "judging" renders. Two
   renderers, like Eevee vs Cycles. Do NOT port ShadedRender to shaders.
5. Materials: batch by material. The pod engine should be about 9 draw
   calls, not 419. Group in the VBO.
6. Start with flat material values (base colour, roughness, metallic
   per material name). Texture maps come later; the Hull cannot bake
   yet anyway because it has no UVs.
7. Match the CPU renderer's tonemap family in the viewport (AgX / ACES
   style) so the viewport does not lie about colour. Skip bloom,
   vignette and floor reflection in the viewport.
8. PRIORITY: "butter smooth". No hitching, no GC churn in the render
   loop, no per-frame allocations, no GPU readback.
9. Build in a sibling folder, never edit the kernel from the app.

=====================================================================
8. THE AUDIT - WHAT IS LOST, RECOVERED, GAINED
=====================================================================

WHAT THE DESKTOP APP WILL LACK
  - The whole editor UI: 18,387 lines of Marionette UI plus 91 files of
    Effigy modelling UI, built on s&box's Widget / Editor.Label /
    DockWindow. All gone. This is the largest single loss.
  - Gizmos: 112 references to s&box's Gizmo system. Ray-picking, drag
    handles, IK handles, snapping - all new code.
  - The viewport: today it draws s&box scenes (Model/Scene/
    CameraComponent). Replaced, not ported. Only its behaviour is
    salvageable (camera, selection, hover, framing).
  - The asset pipeline: .vmdl compile, .vmat materials, hot-reload, and
    Marionette's "writes a walkable test scene in one click" feature.
  - In-engine truth: s&box's renderer was the visual reference. Your own
    renderer becomes the reference, so drift is possible. Animation
    maths is safe (kernel already matches Transform.Lerp).
  - 16 of 20 Code/RigControl files, including the 1369-line playback
    component.
  - Freebies s&box provided: input, file dialogs, undo/redo, icons,
    asset browser, Steam delivery, crash reporting, driver variance.

WHAT IT MUST RECOVER
  - A UI toolkit. ImGui.NET fits (GPU-drawn, dockable) and replaces
    Widget/Editor.Label/DockWindow in one move, but it is immediate mode,
    so the 18k lines get rewritten, not translated.
  - Gizmos, picking, hover, selection. ~500-900 lines for a solid set.
  - A GL renderer: VBO upload, material batching, skinning shader, orbit
    camera, grid, bone lines, wireframe. ~1000-1500 lines to start.
  - Playback glue: sample clip -> build bone matrices -> upload. The
    maths already exists.
  - Undo/redo, file watching, preferences, packaging (AppImage/deb).

WHAT IS GAINED
  - The mirror dies: Editor/Effigy is 240 files / 115,660 lines that
    exist only because s&box cannot compile a top-level folder. Deleted
    in a desktop app. tools/sync-kernel.sh and KernelSyncTests become
    unnecessary.
  - Nothing to install but the app: no Steam, no s&box, no editor, no
    project setup, no engine version pinning.
  - It becomes sellable. An s&box addon can only be sold to s&box
    owners.
  - Cross-engine: the same tool drives s&box, Godot and Roblox.
  - The renderer is ours, so viewport modes s&box cannot offer become
    possible: influence heat maps, weight-colour, UV checker,
    silhouette-only, and the CPU judging renderer side by side with the
    live viewport in one window.
  - Frame rate is ours to control.
  - Headless survives: CLI, tests, batch renders and agent automation
    keep working with no window.
  - Linux native.

THE HONEST COST: roughly 19,000 lines of UI plus a renderer and gizmos
to reach parity with today's Marionette. This is not a weekend. But the
117,065 lines of kernel - modelling, rigging, skinning, retargeting,
cloth, baking, motion linting, six exporters - come across untouched.

=====================================================================
9. THE LARGER PLAN THIS SERVES
=====================================================================

Because the kernel is engine-free, the same work opens a Roblox target.
Notes live in:
  /home/poo/Documents/Default Project/effigy-roblox-backend.md
and an earlier note file: marionette-linux.md (same folder).

Short version: Roblox terrain is voxel data writable by script
(Terrain:WriteVoxels / FillRegion), and Roblox imports FBX, which the
kernel already writes. So Effigy could emit Roblox terrain and meshes,
and Marionette could emit FBX animations. Roblox animation work is
otherwise gated behind age checks and trust levels that he cannot pass.

=====================================================================
10. NEXT STEPS, IN ORDER
=====================================================================

1. Load engine.effigy with the kernel, group geometry by material, and
   draw it. Verify against the CPU reference render. Do the parse ONCE
   at startup, off the render thread, with a loading state - the
   document build is the expensive part and it must never hitch a frame.

   THE LOAD PATH IS ALREADY SOLVED (lane A, 2026-10-17). Do not write a
   loader by hand; call:

       var studio = StudioCache.Load( path );

   It returns a BUILT studio, from a binary cache when there is a valid
   entry for the document and by rebuilding when there is not. On the pod
   engine that is 12.99 s the first time and 0.094 s every time after;
   every real document on this box is 0.07-0.45 s warm. Still call it off
   the UI thread - a miss is a full rebuild, and a document carrying a
   paint atlas or a shape key is deliberately not cached at all, so it is
   12 s every launch until that is extended.

       StudioCache.Savable( studio, out var why )   // can this doc be cached?
       StudioCache.Enabled = false;                 // when a cold number is wanted

   The file lives in ~/.cache/effigy/, never beside the .effigy. It
   invalidates itself on the source text, on any file the document reads
   from disk, and on the kernel build it came from - stamped on the
   assembly's module version id, so a rebuild that changes the kernel
   invalidates every entry on the machine by itself while a rebuild that
   does not change it (which is most of them) keeps the cache warm.
2. Orbit camera + grid + basic lighting (one key, one rim, ambient).
3. Skinning pipeline: bone palette, 4 weights per vertex, vertex shader.
   The kernel has SkinWeights / SkinBinder / Skeleton.
4. Playback: drive the viewport from RigAnimFile / SimRig, the same code
   path `effigy sim` uses, so the viewport and the motion linter can
   never disagree.
5. ImGui.NET shell: bone list, timeline, curve editor. Start with the
   bone list and a scrubber - that is the minimum useful editor.
6. Gizmos and picking.
7. Then, and only then, evaluate whether to fold the CPU renderer into
   the same window.

=====================================================================
11. OPEN QUESTIONS FOR HIM
=====================================================================

- Package format for distribution: AppImage, Flatpak, .deb, or plain
  tarball?
- Should the app keep writing DMX (s&box/Source 2), or drop it now that
  the tool is standalone?
- Does he want the modelling side (Effigy) in the same app as Marionette,
  or Marionette first as a smaller, finishable target? The Effigy UI is
  far larger (91 files vs 34). RECOMMENDED: Marionette first.

=====================================================================
12. LIVE UNRELATED WORK - DO NOT LOSE TRACK OF IT
=====================================================================

He is simultaneously chasing paid contract work to fund this. Files in
/home/poo/Documents/Default Project/:
  leads.txt   9 live paid leads, contacts, the Roblox forum gate, tracker
  outbox.txt  exact send text per lead, plus a rule at the top saying the
              assistant never sends anything
  effigy-roblox-backend.md
  marionette-linux.md

Status as of this handoff: two Discord messages sent to AxisForge
(asset wrangler, and R15 rigger). A message to maoreos bounced with a
Clyde "send a friend request first" error. Drafts are ready for the
New Mexico map job (Discord minimedve_96914) and the FORGED character
artist job (Unreal forums, needs a free Epic account).
