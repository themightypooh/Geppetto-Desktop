#!/bin/bash
cd ~/Desktop/geppetto-desktop
timeout 300 geppetto-live lure-build.txt angler_trace.effigy --once --pace 1 2>&1 | grep -E "completed|failed|error|saved|live run" | tail -20
grep -E "name " angler_trace.effigy
ls -lh angler_trace.effigy
