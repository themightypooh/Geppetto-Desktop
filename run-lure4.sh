#!/bin/bash
cd ~/Desktop/geppetto-desktop
timeout 300 geppetto-live lure-build4.txt angler_trace.effigy --once --pace 1 2>&1 | grep -E "completed|rendered|saved|live run|failed" | tail -20
grep -E "name " angler_trace.effigy
ls -lh angler_trace.effigy lure4_top.png
