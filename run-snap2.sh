#!/bin/bash
cd ~/Desktop/geppetto-desktop/cabinet
python3 host.py cmd "view 0 89"
python3 host.py cmd "ortho 1"
python3 host.py cmd "frame"
python3 host.py snap /home/poo/snap-lure3.png
python3 host.py snap /home/poo/snap-lure3.png
ls -lh /home/poo/snap-lure3.png
