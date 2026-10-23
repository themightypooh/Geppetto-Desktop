# Cabinet layout variants (2026-10-22)

Three takes on the cabinet with the same bones (viewport with 1/2/4 panes, live-run status,
chat/research panels, tool wheel, run pile) and the current palette (enamel red chest, paper
#f4f7ee, ink #1d2318, ink red #b3261c, amber #f2a91c, DejaVu Sans Mono). Each folder has
`mockup.qml` (a standalone QML mockup over the real baked art in `cabinet/art/`), the rendered
`variant-*.png`, and `NOTE.md` on what changed and why. `_shared/` holds the pieces they share and
two viewport crops taken from the running chest. Render one with
`QT_QPA_PLATFORM=offscreen QT_QUICK_BACKEND=software` through a QQuickView grab (see the render
script noted in the session), never with a visible window.

- `a/` Apron: drawers gone; chat, live tape (status + steps) and run pile on a cream board under the chest.
- `b/` Margins: one wider chest, paper columns left (research) and right (chat + shelf), slim bezel, status as a tape between lid and viewport.
- `c/` Loose: only lid + frame remain the chest; panels are pinned paper sheets on the desktop, status is a dymo label on the bottom lip.
