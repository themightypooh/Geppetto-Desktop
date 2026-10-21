#!/usr/bin/env bash
# Launch KoboldCpp tuned for local adult roleplay with SillyTavern.
#
#   tools/chat/koboldcpp-roleplay.sh
#
# SillyTavern talks to the KoboldAI native API, so this script keeps the
# default port 5001 and leaves the OpenAI-compatible layer alone.
#
# Tuned for: RTX 5060 Ti (8 GB VRAM), 12B IQ4_XS model, 8k-12k context.
#
# Override with environment variables, e.g.
#   CONTEXT=8192 tools/chat/koboldcpp-roleplay.sh
#   MODEL=~/koboldcpp/MN-Violet-Lotus-12B.i1-IQ4_XS.gguf tools/chat/koboldcpp-roleplay.sh

set -euo pipefail

KOBOLDCPP_DIR="${KOBOLDCPP_DIR:-$HOME/koboldcpp}"
BIN="$KOBOLDCPP_DIR/koboldcpp-linux-x64"
MODEL="${MODEL:-$KOBOLDCPP_DIR/MN-12B-Mag-Mell-R1-Uncensored-Scale1.2.i1-IQ4_XS.gguf}"
PORT="${PORT:-5001}"
CONTEXT="${CONTEXT:-12288}"

if [[ ! -x "$BIN" ]]; then
    echo "KoboldCpp binary not found or not executable: $BIN" >&2
    exit 1
fi

if [[ ! -f "$MODEL" ]]; then
    echo "Model file not found: $MODEL" >&2
    exit 1
fi

if command -v ss >/dev/null 2>&1 && ss -ltn "sport = :$PORT" 2>/dev/null | grep -q ":$PORT"; then
    echo "Port $PORT is already in use. Stop the running KoboldCpp first:" >&2
    echo "  pkill -f koboldcpp-linux-x64" >&2
    exit 1
fi

echo "model   : $MODEL"
echo "port    : $PORT"
echo "context : $CONTEXT"
echo "kv cache: q8_0 (needs Flash Attention, which is on by default in 1.123.x)"
echo

exec "$BIN" \
    --model "$MODEL" \
    --usecuda \
    --gpulayers -1 \
    --contextsize "$CONTEXT" \
    --blasbatchsize 512 \
    --quantkv q8_0 \
    --host 127.0.0.1 \
    --port "$PORT" \
    --quiet \
    --skiplauncher
