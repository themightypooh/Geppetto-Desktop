#!/bin/sh
# effigy.sh — Geppetto script API + headless script runner, built 2026-10-25.
#   effigy.sh api                    list feature types and script verbs
#   effigy.sh api <Feature>          one feature exact parameters
#   effigy.sh api --markdown         full catalog as Markdown
#   effigy.sh api --write            regenerate docs/EFFIGY-API.md
#   effigy.sh api --check            FAILS if docs are out of sync
#   effigy.sh api script build.txt [outdir]  run a build script headlessly, save test.effigy
set -e
DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
TOOL="$DIR/tools/effigy-api"
BIN="$TOOL/bin/Release/net8.0/effigy-api.dll"
if [ ! -f "$BIN" ]; then dotnet build "$TOOL/effigy-api.csproj" -c Release -v q; fi
export DOTNET_ROOT="${DOTNET_ROOT:-/home/poo/.dotnet}"
exec dotnet "$BIN" "$@"
