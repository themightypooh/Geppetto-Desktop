# Bot map playtest loop fixtures (hammer-ai-bot-commands-map-playtest-loop)

Copies of the ops-CLI map-commands fixtures
(`tests/fixtures/hammer/hammer-ai-ops-cli-map-commands/`), copied verbatim
2026-11-13 so this ticket's tests do not depend on another ticket's
fixture directory. Do not edit the geometry; the leak is the test.

Room (all three files): 288x288 inches (-144..144), floor -16..0,
ceiling 128..144, walls 16 thick, `info_player_start` at `0 0 32`
(except no-spawn-room), one point `light` at `0 0 100`.

- `sealed-room.vmf` — 6 world brushes, sealed. The loop must run all six
  steps clean with no fix.
- `leaking-room.vmf` — the east wall (x 128..144) is split at y = +/-0.5,
  leaving a 1-inch slit (y -0.5..0.5, full height z 0..128). The loop must
  report LEAK on validate, `leak` on build, apply the `seal-leak-portal`
  fix (portal at x=144, box x 112..176, y -8.5..8.5, z -8..136), and
  re-validate clean.
- `no-spawn-room.vmf` — sealed room with no `info_player_start`. The loop
  must fail the build step with the named code `no-player-start` and run
  no later step.
