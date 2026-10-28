#!/bin/bash
ls -lt ~/snap*.png 2>/dev/null | head -20
ls -lt ~/Desktop/geppetto-desktop/cabinet/snap*.png 2>/dev/null | head
find ~ -maxdepth 3 -name "snap-lure3*" -ls 2>/dev/null | head -20
cd ~/Desktop/geppetto-desktop/cabinet
python3 host.py snap /home/poo/snap-lure3b.png
python3 host.py snap /home/poo/snap-lure3b.png
ls -lh /home/poo/snap-lure3b.png /home/poo/snap-lure3.png
