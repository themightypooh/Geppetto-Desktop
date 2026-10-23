# Tool picker concepts (2026-10-22)

What this is about: the pencil in the cabinet's tools drawer needs a popup for picking a tool. The
tool wheel (`cabinet/ToolWheel.qml`, 11 groups, ~75 tools) exists already; this is a second, faster
way in. The first build (`cabinet/SketchCarousel.qml`, wired to the pencil in `cabinet/cabinet.qml`)
is a cover-flow of the 9 FORM tools. The owner found it too card-like, then asked for ideas covering
all the tools, not just sketch. Everything below is an offscreen mockup over the real cabinet
(`QT_QPA_PLATFORM=offscreen`), not working UI. Open the PNGs.

- `00-sketch-carousel.png` — the built one: cover-flow glass tiles of the FORM group, fading at the edges. Pinned.
- `01-spine.png` — the 11 groups as tabs down the right edge, the open group's tools as pills beside them. Owner said "iterate" on this one.
- `02-sheet.png` — frosted sheet of every tool with a type-to-filter search.
- `03-lens.png` — all tools on one line, magnified under the cursor like a dock.
- `04-spine-outliner.png` — spine where each tab counts what the model already has; the panel lists those first (click to edit), then tools you can add, with greyed-out reasons ("needs a profile").
- `05-spine-drag-onto-model.png` — drag a tool off the spine onto the model; the target body lights, a chip says what it will do, dropped chips stay as tags.
- `06-spine-ghost-preview.png` — hover a tool and the model shows the result as a ghost (mirror twin), one slider in the view, click to keep.
- `07/08/09-spine-tabs-*.png` — the tab column as toolbox metal: engraved riveted rail with a lit window; card-index steel dividers; a toggle-switch plate. Owner: "yawn".
- `10-pegboard-all-on-one.png` — shop pegboard over the view, every tool on a hook under painted group labels; a tool used on the model is gone, only its outline left. Owner liked this one.
- `11-foam-drawer.png` — group names as drawer fronts on the right edge; the open drawer slides out, foam cutouts per tool.
- `12-pegboard-four-boards.png` — pegboard split into four boards by stage: MAKE (form, build), SHAPE (refine, deform, mesh), DRESS (cloth, surface), FINISH (rig, tweak, check, output). Each group a shelf row.
- `13-pegboard-icon-treatment.png` — icons reworked to hang: hook hole in the silhouette, shadow on the board; used tools as a painted outline. Owner's reaction to 12+13: "eh idk".

Cabinet facts that matter: the viewport area is 1024x576 and may be split into up to 4 panes; the
cabinet is red riveted steel with light steel plates (RESEARCH / TOOLS / TIMELINE); colours amber
#f2a91c, steel #2b2f36, paper #f4f7ee, ink #1d2318; font DejaVu Sans Mono. Tool icons are 60x60 PNGs
in `cabinet/art/tools/`, rendered from 3D models. Picking a tool must end in the same place the wheel
ends: `toolCard.label = label; toolCard.open = true; host.benchDescribe(key)` (material opens
`materialRing` instead).
