#!/usr/bin/env python3
"""Exit 0 when a checkout build is missing or older than its production sources."""
import os
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
binary = root / 'bin/Release/net8.0/marionette-desktop.dll'
stamp = root / 'bin/Release/net8.0/.source-build.stamp'
if '--stamp' in sys.argv:
    stamp.touch()
    raise SystemExit(0)
if not binary.is_file():
    raise SystemExit(0)
cutoff = stamp.stat().st_mtime if stamp.exists() else binary.stat().st_mtime
for directory in (root, root / 'core', root / 'kernel'):
    for base, dirs, names in os.walk(directory):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin', '.git') and (pathlib.Path(base) != root or d in ('core', 'kernel'))]
        for name in names:
            p = pathlib.Path(base) / name
            if p.suffix in ('.cs', '.csproj') and p.stat().st_mtime > cutoff:
                raise SystemExit(0)
raise SystemExit(1)
