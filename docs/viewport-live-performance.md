# Geppetto Desktop: live AI pane viewport performance (2026-10-24)

How a pane gets edits: the writer (`--script ... --live` / AI run) commits each revision as a new
`.live/revisions/NNNNNNNN-*.effigy` plus a StudioCache entry. The pane's `--serve --watch-run`
process polls the journal (AiLivePreview, background thread), loads each revision
(StudioCache restore, ~0.3 s), builds a whole new ViewportMesh, and the render loop uploads it and
draws (rendering is already dirty-driven: no frames when nothing changed).

Root cause of lag: the full ViewportMesh rebuild per revision (~1.4 s on the 175k-tri barracuda),
done serially: tangents 0.5 s, morph corner map 0.35 s, pick table 0.15 s, normals 0.05 s.
With a 1.5 s pace the pane fell behind and the CPU burn starved the render thread (p95 frame ~70 ms).

Fix (commit af91312, ViewportMesh.cs): pick table runs beside the merged extract; tangents run
beside the morph data; morph corner map/deltas skipped when the mesh has no shape keys.
Result: ~1.4 s -> ~0.9 s per revision (machine at load 20).

Gotcha: StudioCache keys include the Effigy.Kernel assembly MVID. Changing kernel/ code makes every
cache written by a running writer miss in a newer viewer -> ~16 s full rebuild per revision, and the
viewer's TrySave overwrites the writer's entries. Keep viewport perf work out of kernel/ unless
writer and viewer restart together.

Next wins if needed: per-body cache of extract data (most edits touch 1 of ~11 bodies), reuse GL
buffers (BufferSubData) instead of new VAO/VBO per revision.

## Round 2 (commit 3250393)
- Real root causes: (1) workstation GC — the loader thread's allocations caused gen0 pauses of 30-70 ms
  on the render thread; Server GC (4 heaps) fixed the hitches and halved load time. (2) The SDK stamped
  the git sha into the kernel's InformationalVersion, so every commit changed the kernel MVID = the
  StudioCache key -> ~16 s rebuild per revision whenever viewer and writer were built at different
  commits. Now pinned in kernel/Effigy.Kernel.csproj; do not remove.
- ViewportMesh.PieceCache: per-body extract cache keyed by geometry hash (unrigged, no shape keys).
- Upload(gl, previous): reuse VAO/VBO/EBO + textures, grow only when needed, partial body writes.
- AiLivePreview (working tree, with the every-revision queue): MaxBacklog = 3, older waiting steps skipped.
- Barracuda: load ~1.5 s -> ~0.23 s; frame p95 ~60-75 ms -> ~21 ms; output pixel-identical.
