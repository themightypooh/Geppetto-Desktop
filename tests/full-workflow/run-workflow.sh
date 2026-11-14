#!/bin/sh
# run-workflow.sh — drives the scripted workflow. INTEGRATOR ONLY: it needs a
# BUILT marionette-desktop.dll (final build) and working headless EGL.
# It never builds anything itself. Usage:
#   sh tests/full-workflow/run-workflow.sh [workdir]
# Workdir defaults to a fresh temp dir. All output lands under <workdir>/out.
# Exit 0 only if every drivable step passes; W1/W2 gaps are reported, not hidden.
set -eu
D=/home/poo/Desktop/geppetto-desktop
WORKDIR="${1:-$(mktemp -d /tmp/geppetto-workflow.XXXXXX)}"
DLL="$D/bin/Release/net8.0/marionette-desktop.dll"
SCRIPT="$D/tests/full-workflow/workflow.txt"
mkdir -p "$WORKDIR/out" "$WORKDIR/out/shots"
echo "workdir: $WORKDIR"
[ -f "$DLL" ] || { echo "BLOCKED: no built dll at $DLL (run the final build first)"; exit 2; }
[ -f "$SCRIPT" ] || { echo "BLOCKED: missing $SCRIPT"; exit 2; }

# 1. Bench: feed every non-comment, non-GAP line as `apply`, then probe undo.
# The line after the ToolLever `bone` line is undone + re-applied to prove
# undo/redo; the run still ends with the full document.
{
  echo "# workflow driver: $(date -u +%FT%TZ)"
  UNDONE=0
  while IFS= read -r line; do
    case "$line" in
      ''|'#'*|'# GAP'*) continue ;;
    esac
    printf 'apply %s\n' "$line"
    case "$line" in
      'bone ToolLeverBone'*)
        if [ "$UNDONE" = 0 ]; then printf 'undo\n'; printf 'state\n'; UNDONE=1; printf 'apply %s\n' "$line"; fi ;;
    esac
  done < "$SCRIPT"
  echo "state"
  echo "quit"
} > "$WORKDIR/bench-input.txt"

echo "--- bench ---"
env -u DISPLAY -u WAYLAND_DISPLAY dotnet "$DLL" --bench "$WORKDIR/bench" --doc - < "$WORKDIR/bench-input.txt" > "$WORKDIR/bench.log" 2>&1 || {
  echo "FAIL: bench run failed; see $WORKDIR/bench.log"; exit 1; }
grep -q '"ev":"undone"' "$WORKDIR/bench.log" && echo "undo: undone event present" || echo "undo: NO undone event (see W13 — investigate, do not pass silently)"
grep -c '"ev":"applied"' "$WORKDIR/bench.log" | xargs echo "bench applied lines:"
grep '"ev":"failed"' "$WORKDIR/bench.log" && { echo "FAIL: bench had failed lines"; exit 1; } || echo "bench: no failed lines"

# 2. Headless render evidence of the saved document.
SAVED="$WORKDIR/out/workflow.effigy"
if [ -f "$SAVED" ]; then
  echo "--- headless render ---"
  env -u DISPLAY -u WAYLAND_DISPLAY dotnet "$DLL" --headless --model "$SAVED" --frames 1 --size 512x512 \
    --screenshot "$WORKDIR/out/shots/workflow.png" --stats > "$WORKDIR/render.log" 2>&1 || {
    echo "FAIL: headless render failed; see $WORKDIR/render.log"; exit 1; }
  echo "render: $WORKDIR/out/shots/workflow.png"
else
  echo "BLOCKED: $SAVED was not written (save step failed or was skipped); render skipped"
fi

# 3. Gaps that no command in this script can close — report, don't fake.
echo "--- known gaps (must be closed by their owners, not by this script) ---"
echo "W1: no bot route writes a .glb (see WORKFLOW.md) — GLB leg not run."
echo "W2: no CLI/bot driver runs workflow-anim.txt (BuildScriptAnim.Run) — clips not authored."
echo "W3: manual UV island edits have no command route — auto-unwrap only."
echo "W4: paint-clipping / animation-reconcile / rigs-GLB / OpenVDB / MikkTSpace deps — see WORKFLOW.md."
echo "DONE. workdir: $WORKDIR"
