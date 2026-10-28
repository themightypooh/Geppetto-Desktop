#!/bin/bash
cd ~/Desktop/geppetto-desktop
timeout 300 geppetto-live lure-delete.txt angler_trace.effigy --once --pace 1 2>&1 | tail -40
grep -E "name " angler_trace.effigy
ls -lh angler_trace.effigy
