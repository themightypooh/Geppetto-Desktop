#!/usr/bin/env bash
# List Geppetto Muse turns that are pending, recent, and unanswered.
# Output: one turn_id per line, oldest request first (FIFO by request mtime).
# Window: requests modified <= 20 min ago. Seat timeout is 19 min
# (muse_seat.py TIMEOUT_SECS = 19*60), so 20 min gives 1 min grace for
# poll interval / clock granularity without listing long-dead turns.
# BASE override is for testing only; production callers do not set it.
BASE="${MUSE_SEAT_BASE:-/home/poo/Desktop/geppetto-desktop/cabinet/muse-seat}"
WINDOW_SECS=1200
now=$(date +%s)
for f in "$BASE"/requests/*.json; do
  [ -e "$f" ] || continue
  t=$(basename "$f" .json)
  [ -e "$BASE/responses/$t.json" ] && continue
  mtime=$(stat -c %Y "$f" 2>/dev/null) || continue
  age=$((now - mtime))
  [ "$age" -lt 0 ] && age=0
  [ "$age" -le "$WINDOW_SECS" ] || continue
  printf '%s %s\n' "$mtime" "$t"
done | sort -k1,1n -k2,2 | cut -d' ' -f2-
