#!/usr/bin/env python3
"""Required hardware EGL verification. Never opens a window or falls back to SDL."""
import sys
from verify import main

if __name__ == "__main__":
    raise SystemExit(main([*sys.argv[1:], "--mode", "required-egl"]))
