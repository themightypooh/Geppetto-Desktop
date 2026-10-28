#!/bin/bash
rm -f /home/poo/snap-lure4.png
cd ~/Desktop/geppetto-desktop/cabinet
DISPLAY=:0 XAUTHORITY=/run/user/1000/xauth_koXdCe python3 host.py ../angler_trace.effigy
echo open_done
python3 host.py cmd "view 0 89"
python3 host.py cmd "ortho 1"
python3 host.py cmd "frame"
python3 host.py snap /home/poo/snap-lure4.png
ls -lh /home/poo/snap-lure4.png
date
