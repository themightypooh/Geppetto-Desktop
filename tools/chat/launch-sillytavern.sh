#!/usr/bin/env bash
# Launch the local chat stack and open SillyTavern.
#
#   tools/chat/launch-sillytavern.sh          # start what is missing, open the browser
#   tools/chat/launch-sillytavern.sh --no-open  # same, but do not open a browser tab
#
# Nothing is ever restarted: if KoboldCpp or SillyTavern is already listening,
# it is left alone. The desktop icon for this lives at ~/Desktop/SillyTavern.desktop.

set -uo pipefail

ROOT="/home/poo/Desktop/geppetto-desktop"
ST_DIR="$ROOT/SillyTavern"
ST_PORT="${ST_PORT:-8000}"
KCPP_PORT="${KCPP_PORT:-5001}"
LOG_DIR="${LOG_DIR:-$HOME/.cache/local-chat}"
OPEN_BROWSER=1
[[ "${1:-}" == "--no-open" ]] && OPEN_BROWSER=0

mkdir -p "$LOG_DIR"

wait_for() {
    local url=$1 limit=$2 i=0
    while (( i < limit )); do
        curl -fs -m 2 -o /dev/null "$url" && return 0
        sleep 1
        ((i++))
    done
    return 1
}

# --- KoboldCpp: only start it if nothing is serving the API yet ---------------
if ! curl -fs -m 2 -o /dev/null "http://127.0.0.1:$KCPP_PORT/api/v1/model"; then
    echo "KoboldCpp is not running, starting it..."
    setsid nohup "$ROOT/tools/chat/koboldcpp-roleplay.sh" \
        > "$LOG_DIR/koboldcpp.log" 2>&1 < /dev/null &
    if wait_for "http://127.0.0.1:$KCPP_PORT/api/v1/model" 240; then
        echo "KoboldCpp is up."
    else
        echo "KoboldCpp did not answer within 240s. See $LOG_DIR/koboldcpp.log" >&2
    fi
else
    echo "KoboldCpp is already running."
fi

# --- SillyTavern --------------------------------------------------------------
if ! curl -fs -m 2 -o /dev/null "http://127.0.0.1:$ST_PORT/"; then
    echo "SillyTavern is not running, starting it..."
    (cd "$ST_DIR" && setsid nohup node server.js \
        > "$LOG_DIR/sillytavern.log" 2>&1 < /dev/null &)
    if wait_for "http://127.0.0.1:$ST_PORT/" 120; then
        echo "SillyTavern is up."
    else
        echo "SillyTavern did not answer within 120s. See $LOG_DIR/sillytavern.log" >&2
    fi
else
    echo "SillyTavern is already running."
fi

# --- Browser ------------------------------------------------------------------
if (( OPEN_BROWSER )); then
    xdg-open "http://127.0.0.1:$ST_PORT/" >/dev/null 2>&1 &
fi

exit 0
