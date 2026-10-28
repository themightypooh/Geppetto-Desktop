#!/bin/bash
cd ~/Desktop/geppetto-desktop
grep -E "name " angler_trace.effigy
ls -lh angler_trace.effigy
ps aux | grep -E "geppetto-live|host.py" | grep -v grep
cd cabinet
python3 host.py cmd "view 0 89"
python3 host.py cmd "ortho 1"
python3 host.py cmd "frame"
python3 host.py snap ~/snap-lure3b.png
python3 host.py snap ~/snap-lure3b.png
ls -lh ~/snap-lure3b.png
