#!/bin/bash
cd ~/Desktop/geppetto-desktop/cabinet
python3 host.py cmd "help" 2>&1 | head -100
python3 host.py cmd "TOP" 2>&1 | head -20
python3 host.py cmd "FLAT" 2>&1 | head -20
