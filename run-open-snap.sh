#!/bin/bash
cd ~/Desktop/geppetto-desktop/cabinet
DISPLAY=:0 XAUTHORITY=/run/user/1000/xauth_koXdCe python3 host.py ../angler_trace.effigy
python3 host.py cmd "view 0 89"
python3 host.py cmd "ortho 1"
python3 host.py cmd "frame"
python3 host.py snap ~/snap-lure3.png
python3 host.py snap ~/snap-lure3.png
ls -lh ~/snap-lure3.png
