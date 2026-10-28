#!/bin/bash
ps aux | grep geppetto-live | grep -v grep || echo NO_LIVE
grep -E "name " ~/Desktop/geppetto-desktop/angler_trace.effigy
ls -lh ~/Desktop/geppetto-desktop/angler_trace.effigy
