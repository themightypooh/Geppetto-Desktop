# hammer-ai-safety-gates-map fixtures

Safety gates for the map AI tools (McpSafety.cs / GateCheck.cs). The gate
never executes a tool — it only validates — so these fixtures are inputs to
refuse (or scrub), not maps to build.

- `room.vmf` — the fixture map: a copy of the starter template's sealed
  minimal room (`tests/fixtures/hammer/hammer-first-map-starter-templates/minimal-room.vmf`,
  6 brushes, 36 faces, 2 entities, skybox `sky_day01_01`). Copied verbatim;
  the safety tests only read it (the map reference it stands for is the
  string `room.vmf` inside a run dir, never this repo path).
- `shell-attempt.json` — red-team args for `map_compile`: a fast (non-final,
  non-destructive) compile carrying a `compiler_path`. The ONLY reason this
  call may be refused is the shell path — if the gate ever lets a compiler
  path through, this fixture catches it.
- `secret-output.json` — a fake `map_compile` result containing FAKE secrets
  (`sk-fake-…`, `ghp_fake…`). These are not real credentials; they exist so
  the scrub test can prove no secret-shaped value survives in tool output.

All secrets in this directory are fake and safe to keep in the repo.
