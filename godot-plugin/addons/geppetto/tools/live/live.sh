#!/bin/bash
# Real-input audit harness for the Geppetto plugin (see handoffs/AUDIT-AND-SKETCH-NEXT.md).
#   live.sh start   - (re)start the Live editor headless on display :2, wait for "ready"
#   live.sh x ARGS  - XTest input into :2 (never :0): c X Y click, r X Y right-click, m X Y move,
#                     k KEY (ctrl+a, Return, Escape...), t TEXT type, w SECS wait
#   live.sh shot N  - in-engine screenshot to $LIVE_OUT/N.png + N.txt (tool, features, mesh AABBs, errors)
#   live.sh crop N  - $LIVE_OUT/Nc.png, the viewport only
#   live.sh chain   - plane on the pillar face (offset 3), triangle sketch on it, Extrude dialog open
# Env: LIVE_PROJ = a throwaway copy of the test project whose addons/geppetto links to this worktree
#      LIVE_OUT  = output folder.  POOHDOT = the Poohdot editor binary.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
: "${LIVE_PROJ:?set LIVE_PROJ}"; : "${LIVE_OUT:?set LIVE_OUT}"
POOHDOT="${POOHDOT:-/home/poo/src/poohdot/godot/bin/godot.linuxbsd.editor.x86_64.mono}"
x() { DISPLAY="${LIVE_DISPLAY:-:2}" python3 "$HERE/xc.py" "$@"; }
case "$1" in
  start)
    for p in $(pgrep gamescope); do grep -qa 'GEPPETTO_WINDOW_WS=Live' /proc/$p/environ 2>/dev/null && kill $p; done; sleep 2
    mkdir -p "$LIVE_OUT"; rm -f "$LIVE_OUT/ready"
    cd "$LIVE_PROJ"
    env -u WAYLAND_DISPLAY GEPPETTO_WINDOW_RUN="$LIVE_OUT" GEPPETTO_WINDOW_WS=Live GEPPETTO_WINDOW_SCENE="${LIVE_SCENE:-res://sketch.tscn}" \
      setsid timeout 3000 gamescope --backend headless -W 1920 -H 1080 -w 1920 -h 1080 -- \
      "$POOHDOT" --editor --path . --display-driver x11 --resolution 1920x1080 > "$LIVE_OUT/log.txt" 2>&1 &
    for i in $(seq 90); do [ -f "$LIVE_OUT/ready" ] && break; sleep 1; done
    grep -o 'Starting Xwayland on :[0-9]*' "$LIVE_OUT/log.txt" || true ;;
  x) shift; x "$@" ;;
  shot)
    rm -f "$LIVE_OUT/$2.txt"; touch "$LIVE_OUT/$2.req"
    for i in $(seq 20); do [ -f "$LIVE_OUT/$2.txt" ] && break; sleep 0.3; done; sleep 0.3; cat "$LIVE_OUT/$2.txt" ;;
  crop)
    python3 -c "from PIL import Image; Image.open('$LIVE_OUT/$2.png').crop((290,130,1610,830)).save('$LIVE_OUT/${2}c.png')" ;;
  chain)
    x c 436 595 w 1 c 514 640 w 1 c 514 725 w 1 c 940 410 w 1 m 912 200 k Return w 2 c 280 95 k ctrl+a t 3 k Return w 1.5 c 122 217 w 2
    x c 514 648 w 1 c 910 390 w 1 c 905 360 w .5 c 980 360 w .5 c 945 415 w .5 k Return w 2
    x c 436 595 w 1 c 515 715 w 1 c 526 685 w 1 m 945 378 w .5 c 945 378 w 1 m 912 200 k Return w 2 ;;
  *) sed -n 2,11p "$0"; exit 1 ;;
esac
