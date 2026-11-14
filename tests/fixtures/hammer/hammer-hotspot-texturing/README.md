# hammer-hotspot-texturing fixtures

- `trim-hotspots.json` — `HotspotSet` JSON for `trim/trim001`: `trim_horizontal`
  (8:1 top strip), `trim_vertical` (1:8 side strip), `panel_square` (1:1),
  `panel_wide` (2:1). A 128x16 trim face matches `trim_horizontal` exactly; a
  100x30 face (3.33:1) fits nothing — closest is `panel_wide`, aspect error
  0.511 > 0.35.
- `panel-hotspots.json` — `HotspotSet` JSON for `panel/panel001`: `panel_a` /
  `panel_b` (both 1:1, equal area — square face matches `panel_a` on the
  name-order tie-break) and `panel_strip` (4:1). Rect entries omit `material`:
  it defaults from the set's material.
- `trim.vmt` — the same trim hotspots as VMT `$hotspot_<name>` keys, plus an
  unknown `$custom_vendor_key` the bridge must preserve. `HotspotSet.FromVmt`
  reads the three hotspot keys; `WriteToVmt` + `ToText` + `Parse` round-trips them.
