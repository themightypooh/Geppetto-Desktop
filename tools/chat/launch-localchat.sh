#!/usr/bin/env bash
# Launch the native LocalChat window against the local KoboldCpp server.
#
#   tools/chat/launch-localchat.sh            # start what is missing, open the window
#   tools/chat/launch-localchat.sh --check    # verify the setup, open nothing
#   tools/chat/launch-localchat.sh --no-build # do not build the client if it is missing
#
# KoboldCpp is started only if nothing is serving the API yet; an already
# running server is left alone. The desktop icon lives at
# ~/Desktop/Local Chat.desktop.

set -uo pipefail

ROOT="/home/poo/Desktop/geppetto-desktop"
APP_DIR="$ROOT/tools/LocalChat"
BIN_DIR="$APP_DIR/bin/Release/net8.0"
BIN="$BIN_DIR/local-chat"
KCPP_PORT="${KCPP_PORT:-5001}"
ENDPOINT="${ENDPOINT:-http://127.0.0.1:$KCPP_PORT/}"
LOG_DIR="${LOG_DIR:-$HOME/.cache/local-chat}"

CHECK_ONLY=0
ALLOW_BUILD=1
for arg in "$@"; do
    case "$arg" in
        --check) CHECK_ONLY=1 ;;
        --no-build) ALLOW_BUILD=0 ;;
    esac
done

mkdir -p "$LOG_DIR"

fail() {
    echo "local-chat: $1" >&2
    exit 1
}

# --- KoboldCpp ---------------------------------------------------------------
if ! curl -fs -m 2 -o /dev/null "http://127.0.0.1:$KCPP_PORT/api/v1/model"; then
    if (( CHECK_ONLY )); then
        fail "KoboldCpp is not running on port $KCPP_PORT"
    fi
    echo "KoboldCpp is not running, starting it..."
    setsid nohup "$ROOT/tools/chat/koboldcpp-roleplay.sh" \
        > "$LOG_DIR/koboldcpp.log" 2>&1 < /dev/null &
    limit=240 i=0
    until curl -fs -m 2 -o /dev/null "http://127.0.0.1:$KCPP_PORT/api/v1/model"; do
        (( i++ ))
        if (( i >= limit )); then
            fail "KoboldCpp did not answer within 240s. See $LOG_DIR/koboldcpp.log"
        fi
        sleep 1
    done
    echo "KoboldCpp is up."
else
    echo "KoboldCpp is already running."
fi

# --- the client --------------------------------------------------------------
# The apphost cannot find the runtime on this machine (it lives in ~/.dotnet,
# not /usr/share/dotnet and DOTNET_ROOT is unset), so the launcher goes
# through the dotnet muxer and runs the managed entry point directly.
DOTNET="${DOTNET:-$(command -v dotnet || true)}"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

run_client() {
    if [[ -n "$DOTNET" && -f "$BIN_DIR/local-chat.dll" ]]; then
        DOTNET_ROOT="$DOTNET_ROOT" "$DOTNET" "$BIN_DIR/local-chat.dll" "$@"
    elif [[ -x "$BIN" ]]; then
        DOTNET_ROOT="$DOTNET_ROOT" "$BIN" "$@"
    else
        return 1
    fi
}

if [[ ! -f "$BIN_DIR/local-chat.dll" && ! -x "$BIN" ]]; then
    if (( ! ALLOW_BUILD )); then
        fail "the client is not built: $BIN is missing"
    fi
    if (( CHECK_ONLY )); then
        fail "the client is not built: $BIN is missing (run the build to create it)"
    fi
    echo "Building the client..."
    dotnet build "$APP_DIR/LocalChat.csproj" -c Release --nologo -v quiet \
        || fail "the client build failed"
fi

if (( CHECK_ONLY )); then
    echo "client  : $BIN_DIR/local-chat.dll"
    echo "endpoint: $ENDPOINT"
    run_client --endpoint "$ENDPOINT" --list-models || fail "the client cannot reach the server"
    exit 0
fi

# --list-models proves the transport works before the window opens, so a
# connection problem shows up as an error here rather than as an empty window.
run_client --endpoint "$ENDPOINT" --list-models \
    || fail "the client cannot reach the server at $ENDPOINT"

cd "$BIN_DIR" || fail "cannot enter $BIN_DIR"
if [[ -n "$DOTNET" ]]; then
    exec env DOTNET_ROOT="$DOTNET_ROOT" "$DOTNET" "$BIN_DIR/local-chat.dll" --endpoint "$ENDPOINT"
fi
exec env DOTNET_ROOT="$DOTNET_ROOT" "$BIN" --endpoint "$ENDPOINT"
