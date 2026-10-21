=====================================================================
TASKS - who does what
Marionette desktop app - /home/poo/games/projects/geppetto-desktop
=====================================================================

NEW WORK: read handoffs/README.md instead of assigning the historical lanes below.
It splits the eight non-UI categories into bounded implementation tickets, with current
APIs, dependencies, headless-only testing and evidence gates. handoffs/BOARD.md owns assignments.

2026-10-17 AUDIT CORRECTION (newer than the lane notes below):
  Earlier chat summaries overstated completion. The desktop remains a GPU viewer /
  playback prototype, not a complete modeller or animation editor. Axes with arrowheads
  are not draggable gizmos. Renderer parity was NOT proved by a cropped RMSE comparison.
  Startup no longer generates/caches demo clips or forces demo playback on every rig.
  Survey now includes the separate authored head/neck specified by the model documents.
  Desktop regressions live in tests/Desktop.Tests.csproj. See README.md for current
  supported behaviour and limitations; the historical lane checklist below is not an
  up-to-date acceptance report.

FOUR WORKERS, FOUR LANES. Read your lane, do not touch the others'.

  CLAUDE (the pooh session)  - lane A: kernel + measurement
  CURSOR                     - lane B: the app, the window, shaders, UI
  GEMINI                     - lane C: read-heavy analysis, writes specs only
  GPT (Codex)                       - lane D: verification, numbers, harnesses

---------------------------------------------------------------------
GROUND RULES - these exist so four agents do not collide
---------------------------------------------------------------------

1. ONE WRITER PER TREE AT A TIME.
     Effigy/                     lane A only
     geppetto-desktop/           lane B only
       geppetto-desktop/specs/   lane C only
     geppetto-verify/            lane D only
     Editor/, Code/              NOBODY. Off limits for now.
   If you need a change in someone else's tree, append it to REQUESTS at
   the bottom of this file. Do not make the edit.

2. NEVER EDIT Editor/Effigy/. It is a generated mirror of Effigy/.
   Regenerate with tools/sync-kernel.sh. Hand edits get overwritten.

3. THE KERNEL IS SHARED. geppetto-desktop compiles ../geppetto/Effigy/**
   via Compile Include, and Effigy.Tests compiles the same files. Any
   kernel edit changes both. Keep the kernel engine-free: it must never
   reference Sandbox or Godot types. If it stops compiling, that is the
   alarm working, not a bug to route around.

4. DO NOT COMMIT. Do not clean. /home/poo/games/projects/geppetto has
   uncommitted changes that belong to Joshua. `git status` to look, no
   more than that.

5. NEVER send anything on his behalf - no Discord, email or forum posts.
   Drafts as plain text only. He sends.

6. MEASURE, DO NOT ESTIMATE. If you claim something is faster or
   smoother, show the number and the command that produced it.

---------------------------------------------------------------------
MEASURED STARTING FACTS (do not re-derive these)
---------------------------------------------------------------------

  dotnet build of the kernel        : 0 errors, 6.3 s
  GL context (Silk.NET, SDL)        : 4.6.0 core on RTX 5060 Ti, working
  baseline process start            : 0.08 s

  BUILD THE POD DOCUMENT            : 12.7-13.0 s  <-- THE PROBLEM
  build it again, from the cache    : 0.094 s      <-- SOLVED, see lane A
  build + CPU render at 1024        : 13.35 s
  play + lint a 9-frame clip        : 0.09 s       <-- animation is free
  CPU shade 1024/aa=16              : ~1.7 s for 5 images

  Conclusion: 95% of the cost is the cold document rebuild. Shading and
  animation are not the bottleneck, and the cold rebuild is now paid
  once per document per kernel build instead of once per launch.

  Cold -> warm on real documents, `effigy cache <doc> cold|warm`:
    engine.effigy    12.69 -> 0.224 s    419 bodies, 87,252 tris
    pod.effigy       24.72 -> 0.193 s    132 bodies
    camhead.effigy    9.39 -> 0.431 s    617 bodies
    pilot.effigy      2.11 -> 0.141 s     70 bodies
    everything smaller       -> 0.08 s   (process start is 0.075 of it)

  Reference image for look-matching: /tmp/opencode/pod.png
  Content: /home/poo/games/projects/podracer/tools/effigy/engine.effigy
           419 bodies, 87,252 tris, 9 named materials (UV_engine_*)

---------------------------------------------------------------------
LANE A - CLAUDE - step 0, the 12.7 s                                  DONE
---------------------------------------------------------------------

RESULT: 12.7-13.0 s cold -> 0.094 s warm on the pod engine. 138x.

  effigy cache <doc.effigy>          where the entry is, what it holds
  effigy cache <doc.effigy> cold     rebuild with the cache off, and time it
  effigy cache <doc.effigy> warm     load it, and time it - the app's number
  effigy cache <doc.effigy> clear    forget it

WHAT THE 12.7 s ACTUALLY WAS (engine.effigy, 325 features, 16 threads):

  Hull       BlobFeature        3.67 s   SDF voxelize + surface nets, 351,394 quads
  Hull_lod   RemeshFeature      2.94 s   quadric decimate, 702k tris -> 16k
  Hull_uv    UVProjectFeature   3.99 s   chart unwrap of that 16k-tri result
  the other 322 features        ~2.4 s   every other thing in the model
  --------------------------------------------------------------------
  total                        ~13.0 s

  The hypothesis held: the Hull and the two features standing on it are
  10.6 of the 13.0 s. NONE OF THE THREE USES A SINGLE Parallel.For.
  Two independent things follow from that, and both are worth doing:
  parallelise the three (makes COLD builds faster), and cache the
  result (stops the app paying at all). The cache went first because
  it is the one that makes the app usable today.

THE API LANE B SHOULD CALL:

  var studio = StudioCache.Load( path );   // read + cache-or-rebuild + save
  studio.Rebuild();                        // only after an edit
  StudioCache.Enabled = false;             // when a cold number is wanted

  Already compiles into the desktop project - the csproj globs
  ../geppetto/Effigy/**/*.cs. Nothing to wire.

  - synchronous, 0.09-0.45 s on real documents. STILL CALL IT OFF THE
    UI THREAD: a MISS is a full rebuild, 12 s on the pod.
  - StudioCache.Savable( studio, out var why ) answers "can this
    document be cached at all, and if not why" - a paint atlas or a
    shape key makes it not. Use it for the progress message instead of
    claiming the load is instant.
  - the file is NOT beside the document. It is
    ~/.cache/effigy/<name>-<hash>.cache. Deleting that folder is always
    safe, and is the answer to a cache that looks wrong.
  - first edit after a restore re-evaluates the whole tree (the
    per-feature snapshots are not in the file). One full rebuild, once,
    and nothing can come back a version out of date. Tested.

VERIFIED, not asserted:
  - every .effigy on this machine (12 of them, incl. pod 24.7 s and
    camhead 9.4 s): cold vs warm `describe` byte-compared, 12/12
    identical. cold.png vs warm.png sha256 identical on the engine.
  - 43 checks in Effigy.Tests/StudioCacheTests.cs, run them with
    Effigy.Tests.dll --cache. Full suite: 7159 passed, 3 failed, and
    those 3 are garment/cloth checks that fail identically on a clean
    HEAD worktree with the same numbers.
  - the key includes which kernel build wrote the entry, stamped on the
    assembly's module version id. Recompiling the kernel with a real
    change invalidates every entry on the machine without anyone
    remembering to bump anything; recompiling it with nothing changed
    (most builds) keeps them, which is what stops the dev loop paying
    13 s per `dotnet build`. Verified both directions.
  - `effigy cache <doc>` also prints the key and the kernel stamp, which
    is the answer to "why did this entry stop hitting".
  - KNOWN COST, and it lands on lane B. The desktop app compiles the
    kernel INTO Marionette.Desktop.dll, so that assembly's id changes
    when any app source changes, not just kernel source. Editing
    Program.cs therefore invalidates the geometry cache, and the next
    launch is one 13 s cold rebuild before it is warm again. Safe
    direction (too often, never too rarely), and it is once per build
    rather than once per launch - but if it starts to bite, the fix is
    to compile the kernel into its own assembly that the app references,
    so the stamp tracks geometry and nothing else.

---------------------------------------------------------------------
LANE B - CURSOR - the app
---------------------------------------------------------------------

This is the keyboard grind, and it is where you need to see the window.

  1. Load engine.effigy through the kernel, group geometry by material
     (expect ~9 draw calls, not 419), upload to one VBO, orbit camera.
     Call lane A's load path when it lands; hardcode a stub until then.
  2. Make it look right: PBR fragment shader, 9 material slots, tonemap
     matched to the CPU renderer's family. Compare against pod.png.
  3. Butter smooth: frame-time overlay, zero per-frame allocations, no
     GPU readback, vsync capped to the monitor.
  4. Skinning: bone palette, 4 weights per vertex.
  5. ImGui: bone list, scrubber, play/pause. This is the moment it
     becomes Marionette.
  6. Gizmos and picking.

ACCEPTANCE per step: the app shows it, and the frame time is flat.
Everything lives in /home/poo/games/projects/geppetto-desktop/ except
the kernel, which is lane A's.

Known gotcha already paid for: Silk.NET's SDL backend hands back a 3.3
compatibility context unless you ask explicitly:
    API = new GraphicsAPI( ContextAPI.OpenGL, ContextProfile.Core,
                           ContextFlags.ForwardCompatible,
                           new APIVersion( 4, 6 ) )

---------------------------------------------------------------------
LANE C - GEMINI - analysis only, writes specs
---------------------------------------------------------------------

Big-context reads that would cost the others a lot of grepping. Output
is a written spec in geppetto-desktop/specs/, not a diff.

  1. Read Editor/RigControlEditor/ (34 files, 18,387 lines) and produce
     a port spec for the ImGui version: what each panel does, what state
     it owns, what the timeline and curve editor actually need from the
     kernel, and what is s&box-specific chrome that should be dropped.
  2. Read Effigy/Rig/ (the engine-free rig kernel) and list exactly what
     a GPU viewport needs from it per frame: which calls, what they
     return, and which are cheap vs expensive.
  3. Read its own way through Effigy/Features/ and CHECK lane A's
     profile rather than re-deriving it. Lane A's original prefix
     estimate, for engine.effigy (325 features):

        Hull      BlobFeature       3.67 s  ->  Effigy/Features/OrganicFeatures.cs
        Hull_lod  RemeshFeature     2.94 s  ->  Effigy/Features/RemeshFeature.cs
        Hull_uv   UVProjectFeature  3.99 s  ->  Effigy/Features/SolidFeatures.cs
        the other 322 features      ~2.4 s
        and none of the three uses Parallel.For.

     Say whether that adds up from reading the code: what the three
     actually do per call, what their asymptotic cost is in the
     parameters the document sets (Voxel 0.42, Triangles 16000,
     ChartAngle 66), and which inner loop is the one to parallelise.
     If the code says the cost should be elsewhere, say so with the
     line numbers - that is worth more than agreement.

     RESOLVED 2026-10-17, and both estimates were partly wrong. Lane A
     re-measured by instrumenting PartStudio.Rebuild with a stopwatch
     around feature.Run and around the per-feature Snapshot.Of clone:

        Hull_uv   UVProjectFeature   3.824 s   30%
        Hull_lod  RemeshFeature      3.173 s   25%
        Hull      BlobFeature        3.110 s   24%
        the 325 per-feature snapshots 2.356 s  18%
        the other 322 features        0.065 s   0.5%
        read + parse + clone + MaterialScale  ~0.25 s

     So lane A's "other 322 features ~2.4 s" was mislabelled - that
     residual was the snapshot cloning, which is a per-feature overhead
     and not feature work. And lane C's rebuild-profile.md ranked the
     decimate first and the unwrap last, which the measurement inverts:
     the unwrap is the single biggest cost. Lane C's mechanism analysis
     is confirmed by the measurement, including that ~9.33M face clones
     in Snapshot.Of predict the 2.356 s within noise. Correction header
     is at the top of specs/rebuild-profile.md.

ACCEPTANCE: a spec lane B can implement from without re-reading the
source. No source edits - only new files under geppetto-desktop/specs/.

TASK 4 - THE UI CONCEPT, added 2026-10-17 at Joshua's request. He wants
the UI reconceived rather than ported: "i think i want it different".
The brief is geppetto-desktop/prompt-gemini-ui.txt and the deliverable
is specs/ui-concept.md. It is a design brief, not a port spec - the
output is the SPINE of the tool (the five or six interactions that
carry the work), ASCII wireframes, a P0/P1/P2 mapping of every
capability from the ~40 existing panels so nothing is silently dropped,
an explicit list of what gets cut, the per-frame budget the UI may
spend, and a build order. Two facts to hand it: camhead.effigy, the
character he actually cares about, is 617 bodies / 63,236 tris / 15
material batches and 9.9 s cold but 0.17 s warm, so it is both the
iteration document and the row-count problem at its worst - and its
bodies are named by anatomical region ("Spinous 4 arch", "Ankle cheek
R", "Hose elbow a hip top L"), so a flat list is not an answer. And
engine.effigy (419 bodies, 12.8 s cold) is the rig-less stress case.
imgui-port.md section 1 already inventories the panels, so it must
build on that rather than redo it.

---------------------------------------------------------------------
LANE D - GPT (Codex) - verification and numbers
---------------------------------------------------------------------

Somebody has to be able to say "that made it slower" or "that changed
the picture". Lane D produces numbers, not opinions. Own folder:
geppetto-verify/ - it does not exist yet, create it.

  1. Kernel regression harness: time `describe <file>` (pure document
     build, no render) over real files, run the Effigy.Tests suite,
     append results to geppetto-verify/history.txt, print PASS/FAIL
     against the previous run so a regression is loud.
  2. Headless app harness: run marionette-desktop --frames 120, record
     wall time and exit code, capture a screenshot with spectacle or
     import, save to geppetto-verify/shots/.
  3. Frame-time meter: per-frame times in, mean/p50/p99/worst/spikes
     out. Needs a --stats flag from lane B - request it, do not add it.

ACCEPTANCE: every number has the command next to it, and Joshua can
re-run the whole thing in one command.

NOTE: this box has no /usr/bin/time and no bc. Use `date +%s%N` + awk.

DONE 2026-10-17, re-run against the current tree, and it needed four
corrections before its numbers could be trusted. All four were found by
comparing its output against a run from inside the repo:
  1. IT WAS RUNNING 266 CHECKS SHORT AND SAYING NOTHING. Building the
     kernel to geppetto-verify/build/kernel/bin/ meant KernelSyncTests
     and MenuIconTests (which find the repo by walking up from the
     running binary) found no repo and skipped themselves - 251 of the
     266 are the whole Editor/Effigy mirror parity section, the one that
     exists because a stale mirror already slipped through once.
     Fixed: both tests honour EFFIGY_REPO_ROOT, which run.sh sets.
     Visible now: run.sh prints the suite's "N passed, M failed" line,
     and it should match a run from inside the repo (~7200, not 6893).

  2. kernel_tests COULD NEVER PASS. Ten checks fail on a clean HEAD;
     the summary says "3 failed" while ten lines say FAIL, and they are
     not the same three. Red every day = read by nobody. Fixed:
     check-failures.py matches failures by name against
     known-failures.txt - new ones fail the run, fixed ones get reported
     so the list gets trimmed.

  3. THE APP BASELINE MEASURED NOTHING. The old app_frames120 entries
     (1.019 s, 1.232 s) came from a build whose screenshot was an empty
     window: it never loaded the document and it ran without vsync. So
     every honest run after it read as a tenfold regression against a
     number produced by doing nothing. Fixed: the step is app_run, it
     launches twice, and launch 2 must report "restored" in under 1 s.

  4. describe_* WAS TIMING THE CACHE, NOT THE KERNEL. `describe` routes
     through StudioCache.Load, so once the cache existed the "kernel
     regression harness" was reading a warm entry and calling it a build.
     It showed as describe_camhead at 0.287 s against a 9.57 s cold build
     - a 33x "improvement" recorded as a pass. Fixed: the steps force
     EFFIGY_NO_CACHE=1, are renamed describe_*_cold (the old keys hold a
     0.287 s PASS that would make every real cold build read as a
     regression), and have a wall-clock floor that fails the run if it
     finished too fast to have built anything.

Also fixed: app.sh used to screenshot the moment `GL ` appeared in the
log, which is always mid-load, so every artifact it produced was of the
empty loading window. It now uses the app's own --screenshot, taken when
the first frame with a model in it has been drawn, and feeds the log to
frame-stats.py so the frame numbers land in history.txt every run.

---------------------------------------------------------------------
ORDER - why this sequence
---------------------------------------------------------------------

  Step 0 (A)  kill the 12.7 s          <- nothing is usable until this
  Step 1 (B)  geometry on screen       <- "it works"
  Step 2 (B)  looks right vs pod.png
  Step 3 (B)  butter smooth
  Step 4 (B)  skinning + play a clip   <- cheap, animation is 0.09 s
  Step 5 (B)  minimal UI               <- "my animator"
  Step 6 (B)  gizmos, posing
  Step 7 (B)  curve editor, keying, undo
  Step 8      Effigy modelling UI      <- much bigger, do not start yet

  Lane C and lane D run in parallel throughout; C's specs feed B, D
  keeps everyone honest.

---------------------------------------------------------------------
REQUESTS - cross-lane changes go here, do not just make them
---------------------------------------------------------------------

  Lane D -> lane B: add `--stats` to marionette-desktop. Print one
  `frame_ms <milliseconds>` line to stdout per rendered frame, using a
  monotonic clock and at least 0.001 ms precision. Document whether this
  is render-callback, present/vsync, or frame-to-frame time. No GPU readback.
  Lane D's checker is geppetto-verify/frame-stats.py.

  Lane A -> lane B: replace the stub loader with

      var studio = StudioCache.Load( path );

  in the desktop project. It resolves through the csproj glob
  (../geppetto/Effigy/**/*.cs) with nothing to wire, returns a BUILT
  studio, and takes the pod engine from 12.99 s to 0.094 s. Keep it off
  the UI thread; a miss is still a full rebuild. Report
  `StudioCache.Savable` in the loading state rather than promising a
  fast load for a document that cannot be cached.

---------------------------------------------------------------------
STATUS
---------------------------------------------------------------------

  [x] environment verified, GL 4.6 window running
  [x] kernel builds on Linux, headless, 0 errors
  [x] bottleneck measured: 12.73 s document rebuild
  [x] step 0  cache                 lane A
      proved 2026-10-17: engine.effigy 12.99 s cold -> 0.094 s warm.
      12 of 12 documents on this box byte-identical cold vs warm.
      Where the 12.8 s actually goes, MEASURED (see specs/rebuild-profile.md
      for the full table and the method):
          Hull_uv   UVProjectFeature   3.824 s   30%
          Hull_lod  RemeshFeature      3.173 s   25%
          Hull      BlobFeature        3.110 s   24%
          the 325 per-feature snapshots 2.356 s  18%
          the other 322 features        0.065 s   0.5%
          read + parse + final clone + MaterialScale  ~0.25 s
      So three features are 79% of cold and the tail is free. An earlier
      note here said "other 322 features ~2.4 s" - that residual was the
      snapshot cloning, and calling it feature work was wrong.
      None of the three is parallelised. Remaining, optional: parallelise
      them for faster COLD builds, or stop snapshotting (2.36 s, and the
      simplest of the four changes). Neither speeds a warm launch.
      Effigy/Features/StudioCache.cs + Effigy.Tests/StudioCacheTests.cs
      (50 checks, Effigy.Tests.dll --cache).
  [x] step 1  geometry on screen    lane B
      proved 2026-10-17: 419 bodies / 87252 tris / 12 material draws.
      Proof: assets in geppetto-verify/shots/ (framebuffer grabs).
  [x] step 1b cache wired into the app        (closed 2026-10-17)
      DocumentLoader.cs called ReadFile + Rebuild itself, so the app paid
      12.8 s on EVERY launch while the cache sat unused next to it. Now
      StudioCache.Load. Measured, same binary, two launches:
          launch 1  load 13.083 s  total 13.165 s   (stale entry, rebuilt)
          launch 2  load  0.131 s  total  0.273 s   (cache hit)
  [x] step 3  butter smooth         lane B + D
      `--stats` now emits frame_ms per frame; geppetto-verify/frame-stats.py
      reads it. engine.effigy, 1600x900, vsync on:
          warm  samples=173 p50=16.630 ms p99=18.366 ms worst=30.763 ms spikes>33.3ms=0
          cold  samples=915 p50=16.659 ms p99=30.613 ms worst=37.130 ms spikes>33.3ms=3
      The 3 cold spikes are the one-off VBO upload after the rebuild.
      NOTE: vsync caps this at 60, so it proves "no hitches at 60", not
      "as fast as the GPU can go". Uncapped numbers need VSync=false.
  [x] specs                         lane C
      specs/viewport-frame.md, specs/imgui-port.md, specs/rebuild-profile.md.
      OPEN CONFLICT: rebuild-profile.md estimates decimate 4.6-5.3 s and
      UVProject 1.2-1.7 s. Measured: 2.94 s and 3.99 s - the ranking is
      inverted. Its per-line-loop findings are the valuable part; the
      totals need settling by measurement, not by argument.
  [x] harness                       lane D
      Re-run 2026-10-17 against the CURRENT tree. The four earlier app
      runs (10:00-10:03 local) predated Program.cs/DocumentLoader.cs/
      ViewportMesh.cs (10:08-10:12) and verified an app that no longer
      existed. app.sh also used to screenshot the moment `GL ` appeared
      in the log, which is always mid-load - so every shot it took was
      of the empty loading window. It now uses the app's own --screenshot,
      taken when the first frame with a model in it has been drawn.
  [ ] step 2  looks right vs pod.png     <- NOT done, do not tick
      The GPU render of engine.effigy is plausible but has never been
      pixel-compared against the CPU reference. compare-pod.sh needs a
      camera-aligned 1024x1024 shot; the app's --screenshot at
      --size 1024x1024 is now exactly that, so this is cheap to close.
