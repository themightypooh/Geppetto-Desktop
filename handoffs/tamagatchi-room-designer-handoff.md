# Handoff: room designer (decorate mode), updated 2026-10-29

Thread: "Build d-pad decorate mode for the room". Project: ~/games/godot/tamagatchi/godot, run with Poohdot
(`~/src/poohdot/godot/bin/godot.linuxbsd.editor.x86_64.mono`). Always call the engine Poohdot.

## Done
- Grid room designer in `components/decorate_mode.gd`: 0.25 m cells, floor 10x13, back wall 10x13, side walls 13x13.
  A = catalogue (cycles pieces from the hidden `Cupboard` node), B = pick up / drop, C = rotate, side rocker toggles
  the mode. Small things (TOPS list) stack on Table, Nightstand, ToyChest, Stool, Fridge. Rugs ignore furniture.
  Wall pieces (KIND "wall") snap to walls, HangingPlant to the beam.
- Decorate mode never moves a camera. The overlay is `components/decor_ui.gd` (flat, semi-opaque band UI). The release
  thread owns decor_ui.gd: re-read it before any edit (hint band moves up when the piece reaches into it).
- Grid shows only on the surface the carried piece is on; the carried piece gets a white outline drawn through
  furniture (`room/decor_outline.gdshader`, `Outline` MeshInstance3D in room.tscn, export `outline`).
- All 34 rebuilt furniture scenes (`room/furniture/*.tscn`, owned by the furniture thread, do not edit) registered:
  26 hidden instances under `Cupboard`, 8 defaults in the room (Fridge back-left, Bookshelf, Table, Chair, Stool,
  Picture, Rug, FloorLamp, Plant). All 8 defaults are now scene instances (the four hand-built ones were swapped in
  on 2026-10-28 after the furniture thread fixed them). Footprints: `docs/furniture-footprints.md`.
- Walls at 112 px: `room.gdshader` pattern 2 (wallpaper) takes hard palette steps (no dither, which showed as
  shapeless speckle), and the wainscot is a plain darker panel with a lighter 0.1 m cap band (y -0.95..-0.85)
  instead of board seams and a dark rail that broke into dotted diagonals.
- Storage: first built as one flat list (shots/lcd-storage.png), then reshaped into the paged cupboard menu below.
  Everything is press-only (`press()`), no holds, so shell buttons, keyboard, gamepad, mouse and phone taps all work.
- Layout saves to `user://room_layout.cfg` = `~/.local/share/godot/app_userdata/OddKeeper/room_layout.cfg` (project name
  is OddKeeper): `[finishes] wall, floor` plus per piece `placed, surface, cell, rot`. Missing file = empty room.

## In progress / open
- Poo has not yet signed off on the LCD look of the four swapped pieces (shots/lcd-walls-four-before-after.png).
- Side walls read as flat dark panels now; if Poo wants more texture there, add a light gradient band, not dither.

### Cupboard menu (finished 2026-10-29, started by GPT)
- `C` (or `A`) opens a flat menu over the room. Pages: **Room** (Furniture (n), Store item, Store everything, Undo room change, Wall: …, Floor: …, Furnish it for me, Back) → **Furniture** (categories with counts) → one category (stored pieces by name).
- Taking a piece out is a preview: it is carried at once; `B` places it, `A` puts it back and reopens the categories. "side done" while previewing keeps the piece.
- Store everything and Furnish it for me (on a non-empty room) ask "Confirm? B yes". Every room change can be undone once (Undo room change).
- A room with nothing placed opens straight on the categories ("Pick your first piece"). Fresh installs start empty; existing `room_layout.cfg` saves load unchanged (`save_path` is overridable for tests).
- Forms: the LCD is per shell form (slab/locket/coffin 112x137, puck/relic 162x162, prism 177x153, pod 162x132, pager 159x107) and round/hex windows carry a `safe_rect` meta on the room viewport (ScreenWindow). DecorUI lays out inside that rect, at most 112 wide and centred like PetUI; bands run to the edges. Checked on all 8 (shots/lcd-designer-8-forms.png, magenta = safe rect).
- Labels: every line goes through `DecorUI._fit`; all current lines fit the 112 px LCD with no ellipsis (shots/lcd-designer-pages.png, shots/lcd-designer-store-undo.png, 4x nearest). The phone draws the same 112x137 LCD texture with nearest filtering, so 1x/2x/4x are integer upscales of those shots.
- Tests: `tests/designer_storage.gd` (GPT's, 0 failures) and `tests/decorate_fuzz.gd` (walks the pages; 0 violations). Run headless, then delete `~/.local/share/godot/app_userdata/Tamagatchi/room_layout.cfg`.
- Backups: backups/decorate-before-20261028/*.before-finish.gd. GPT's `backups/designer-cupboard` was never written.
- Code map (`components/decorate_mode.gd`): `menu` (-1 closed, else picked line), `menu_page` ("room" | "furniture" |
  a CATEGORIES name), `confirm`, `_undo_room` (one `_snapshot()`), `_preview` (snapshot taken when a piece is taken
  out; cleared on B drop / done, restored on A). `menu_items()` builds the lines per page ({id, text}); ids are
  "furniture", MENU_PUT_AWAY, MENU_CLEAR, "undo", "walls", "floors", MENU_DEFAULT, "category:<name>", a piece name,
  "back". `_menu_press()` moves/backs/does; `_menu_do(id)` acts; `_menu_back()` walks category -> furniture -> room
  -> closed. `category_of(p)` maps piece names to the 10 CATEGORIES (new furniture: add its name there or it lands
  in "Other"). Wall/floor designs: `wall_design`/`floor_design` index WALL_DESIGNS/FLOOR_DESIGNS, pushed to every
  room.gdshader material by `_apply_finishes()` (uniforms `wall_design`, `floor_design`; owned by the finishes thread).
- Flow in `press()`: with the menu closed, A = cancel preview (if any) and open the categories, C = open the Room page
  (or turn while carrying), B = pick up / drop (drop also ends a preview and saves). `set_active(true)` on a room
  with nothing placed opens the categories; `set_active(false)` keeps a previewed piece and saves.
- Overlay (`components/decor_ui.gd`): `_draw_storage()` draws title, scrolling rows round the pick (inverted),
  "Confirm? B yes" on the picked line while `confirm`, ^ / v scroll marks, hint band. `_safe()` is the layout rect
  (viewport meta `safe_rect`, max 112 wide, centred); `_fit()` ellipsizes anything too wide.
- Phone: `phone/phone_pet.gd` taps call the same `act()`/`press()` actions; the phone shows the form's LCD texture
  (nearest filter), so the per-form sheets are what the phone shows. After designer changes the phone thread must
  rebuild and install the OddKeeper APK (asked for on 2026-10-29).

## Remaining tasks (done when...)
- Phone rebuild + install with the finished menu (phone thread). Done when Poo opens the designer on the phone and sees
  the Room / Furniture pages.
- Sign-off from Poo on the menu wording (Store item / Store everything / Furnish it for me / Undo room change).
- Any piece Poo calls out as unreadable at 112 px: fix in the furniture scene (furniture thread) or revert that
  default to a backup. Done when a fresh `shotroom.gd` render shows it clearly.
- Curtains cannot overlap the window (window is an obstacle). Done when curtains can sit on the window cells.
- Bed 3x6 and Sofa 6x3 do not fit the default room ("no room" skips in the fuzz test). Done when either the floor is
  larger or the defaults leave a 6-cell run free.

## Key files
- `components/decorate_mode.gd` (KIND, NOT_PIECES, OBSTACLES, SURFACES, TOPS, `_at`, `_find_spot`, `_place_outline`,
  `_show_grid`), `components/decor_ui.gd`, `components/furniture_light.gd` (@tool, aims lamp_pos at CeilingLamp).
- `room/room.tscn` (ScreenView camera is the canonical 112x137 LCD view), `room/room.gdshader`, `room/decor_outline.gdshader`.
- `docs/furniture-footprints.md`.
- Variant-inference warnings are errors here: type every new var (`var x: String = ...`).

## Backups
- `~/games/godot/tamagatchi/backups/decorate-before-20261028/` and next to the files in `room/`:
  room.before-outline.tscn, room.before-furniture-batch.tscn, room.before-swap2.tscn, room.before-walls.tscn,
  room.before-four.tscn; decorate_mode/decor_ui/decorate_fuzz .before-storage.gd (in backups/); room.before-walls.gdshader, room.before-qa-art.gdshader; decorate_mode.before-*.gd.

## Tests
- Fuzz: `godot --headless --path . --script tests/decorate_fuzz.gd` -> expect `violations: 0`
  ("no room for Sofa"/"Bed"/"WritingDesk"... skips are expected: fill order, not bugs). It writes a layout to
  `~/.local/share/godot/app_userdata/OddKeeper/room_layout.cfg`; delete it and `screen_color.cfg` afterwards or the
  next run / shot starts from the fuzz's room. (Earlier notes said a Tamagatchi/ folder: wrong, the project is OddKeeper.)
- Storage test: `godot --headless --path . --script tests/designer_storage.gd` -> `DESIGNER STORAGE failures=0`.
  It uses its own `user://designer-storage-test.cfg` via `Decorate.save_path`, so it never touches the player's save.
- Per-form sheet: SceneTree script that loads room.tscn into a SubViewport, sets `sv.size` to the form's pixels
  (`screen_size * ScreenWindow.PIXELS / ScreenWindow.BASE`) and `sv.set_meta("safe_rect", ScreenWindow.safe_rect_of(
  screen_mesh.get_meta("window"), screen_size))` per form in devices/shells/forms/*.res, then presses through the pages
  and saves PNGs. Needs a window (`--resolution 320x240 --position 3000,3000`); headless gives no texture.
- Run `--import` headless after any furniture scene change before headless runs.
- LCD renders: windowed with `--resolution 320x240 --position 3000,3000` so focus is not stolen; shots in
  `~/games/godot/tamagatchi/shots/` (lcd-*-before-after.png).
