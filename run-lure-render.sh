#!/bin/bash
cd ~/Desktop/geppetto-desktop
timeout 300 geppetto-live lure-render.txt angler_trace.effigy --once --pace 1 2>&1 | grep -E "completed|render|saved|live run" | tail -20
ls -lh lure3_top.png
