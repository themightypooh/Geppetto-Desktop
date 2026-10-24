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
