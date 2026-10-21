#!/usr/bin/env python3
"""The drawing tool: Linework, the 2D sketching app, as Geppetto's 2D tool.

Pure helpers only, no Qt and no window: where Linework lives, what it has drawn,
and the name of the socket it listens on. The host does the talking; this is the
part that can be tested headlessly.

  python3 -c "import drawing_tools; print(drawing_tools.drawings())"
"""
import json
import os

ROOT = os.path.expanduser("~/.local/share/linework")
DRAWINGS = os.path.join(ROOT, "drawings")
APP = os.path.join(ROOT, "linework")
LAUNCHER = os.path.expanduser("~/.local/bin/linework")


def socket_key(user=None):
    """the socket Linework listens on (its main.cpp: "linework-" + USER)"""
    return "linework-" + (os.environ.get("USER", "") if user is None else user)


def installed():
    """is the drawing tool on this machine?"""
    return os.path.isfile(program())


def program():
    """what to run: the launcher if it is there, else the app itself"""
    return LAUNCHER if os.path.isfile(LAUNCHER) else APP


def safe_id(ident):
    """a gallery id that cannot point out of the drawings folder"""
    ident = str(ident or "")
    if not ident or ident.startswith(".") or "/" in ident or "\\" in ident:
        return ""
    return ident


def resolve(directory, ident):
    """the drawing file for a gallery id, or None if there is no such drawing"""
    ident = safe_id(ident)
    if not ident:
        return None
    path = os.path.join(directory, ident + ".linework")
    return path if os.path.isfile(path) else None


def drawings(directory=DRAWINGS):
    """every drawing in the gallery, newest first: id, name, updated, path, thumb"""
    out = []
    try:
        with open(os.path.join(directory, "index.json"), encoding="utf-8") as f:
            index = json.load(f)
    except (OSError, ValueError):
        return out
    if not isinstance(index, list):
        return out
    for entry in index:
        if not isinstance(entry, dict):
            continue
        ident = safe_id(entry.get("id"))
        if not ident:
            continue
        path = os.path.join(directory, ident + ".linework")
        if not os.path.isfile(path):
            continue                       # the index can outlive a deleted drawing
        try:
            updated = int(entry.get("updated") or 0)
        except (TypeError, ValueError):
            updated = 0
        out.append({"id": ident,
                    "name": str(entry.get("name") or "Drawing"),
                    "updated": updated,
                    "path": path,
                    "thumb": os.path.join(directory, ident + ".jpg")})
    out.sort(key=lambda d: -d["updated"])
    return out
