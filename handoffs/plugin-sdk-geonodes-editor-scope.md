# GeoNodes editor — sub-slice scope (godlike-plugin-sdk-text-scene-collaboration)

The ticket's item (5) scopes — but does not build — the in-app Geometry Nodes /
Bifrost-style graph editor, the follow-on UI for the bot-exposed GeoNodes
(`expose-geonodes-via-bot-commands`, done). This note is the scope so a later
ticket can start without re-litigating it.

## Goal
A node-graph editor inside Desktop where an artist wires geometry operations
into a reusable asset (Houdini Digital Asset equivalent), previews it live on
the model, and shares it through the Plugin SDK store — no kernel changes by
the asset author.

## Node set, v1 (five, no more)
1. **Scatter** — instance a source mesh on points (surface / curve / volume),
   density + seed + per-instance scale/rotation jitter. Builds on the IMM
   insert work (godlike-imm-nanomesh-vdm-insert-repeat, in-progress).
2. **Deform** — displace / bend / twist / taper along an axis or falloff.
3. **Boolean** — union / subtract / intersect via the Manifold backend
   (integrate-portable-manifold-boolean-backend-with-honest-fallback, done).
4. **Retopo** — remesh / decimate via the OpenVDB + Instant Meshes backends
   (integrate-openvdb-remesh-and-instant-meshes-retopo-backends, in-progress);
   do not write a third remesher.
5. **Material assignment** — set material slots / texture sets per island or
   per selection.

## Live preview + reusable assets
- Preview evaluates the graph on a proxy (decimated) mesh while editing, full
  mesh on commit — same proxy-pose idea as
  godlike-proxy-pose-animator-grade-auto-rig.
- A saved graph is a versioned asset: manifest + node JSON, signed and
  distributed through the Plugin SDK store (this ticket's AddonPackage /
  AddonStoreCatalog). No new distribution channel.

## Builds on (do not re-implement)
- Bot-exposed GeoNodes operations (the operation layer the graph drives).
- Plugin SDK: versioned API, signed packages, store listing (this ticket).
- TextScene layers: one graph per layer is the multi-artist story.

## Explicitly out of this ticket
- The UI graph editor itself — that is a follow-on ticket, not this one.
- Any node type beyond the five above.
- USD / Houdini interop for node assets (the USD ticket owns interchange).
- A visual node debugger / profiler (later).

## Acceptance sketch (for the follow-on ticket)
A scatter+deform+boolean graph built in the editor, saved as a store asset,
installed on a clean checkout, applied to a test mesh, commits to a manifold
mesh; the graph file diffs cleanly in git.
