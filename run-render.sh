#!/bin/bash
cd ~/Desktop/geppetto-desktop
grep -h "render" *.txt 2>/dev/null | head -20
ls -lt *.png 2>/dev/null | head -20
