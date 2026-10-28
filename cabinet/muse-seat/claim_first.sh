#!/usr/bin/env bash
# claim_first.sh — one local run on the Geppetto machine: list pending, check, claim first claimable.
# Usage: claim_first.sh [skip1,skip2,...]
# Output: CLAIMED <turn_id>  |  NONE
# Speed 2026-10-25: hook used to do list (1 SSH) + check per candidate (1 SSH each) + claim (1 SSH).
# This does all three locally, so the hook needs exactly one SSH call per poll.
set -uo pipefail
BASE="/home/poo/Desktop/geppetto-desktop/cabinet/muse-seat"
LIST="$BASE/list_pending.sh"
IP="$BASE/in_progress.sh"
SKIP="${1:-}"
# CLOSED MEANS CLOSED 2026-10-25, user rule, both apps: if the Geppetto
# host is not running, nothing may be claimed, by anyone — hook, warm
# worker, or cron. A turn sent just before close must not be answered
# after close. When Geppetto opens again, the host is running and claims
# resume normally.
if ! pgrep -f "cabinet/host[.]py" >/dev/null 2>&1; then
  echo "NONE"
  exit 0
fi
for t in $(bash "$LIST" 2>/dev/null); do
  case ",$SKIP," in *",$t,"*) continue ;; esac
  STATE=$(bash "$IP" check "$t" 2>/dev/null || echo "ERROR")
  case "$STATE" in
    CLAIMABLE*)
      CLAIM=$(bash "$IP" claim "$t" hook 2>/dev/null || echo "ERROR")
      case "$CLAIM" in
        CLAIMED*) echo "CLAIMED $t"; exit 0 ;;
        *) continue ;;
      esac
      ;;
    *) continue ;;
  esac
done
echo "NONE"
