#!/usr/bin/env python3
"""The drawing tool: Linework, the 2D sketching app, as Geppetto's 2D tool.

Pure helpers only, no Qt and no window: where Linework lives, what it has drawn,
and the name of the socket it listens on. The host does the talking; this is the
part that can be tested headlessly.

  python3 -c "import drawing_tools; print(drawing_tools.drawings())"
"""
import json
import math
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


# --- Lane I-delta: 3D draw-over raycast backend ---------------------------------
# The future QML draw-over layer turns a freehand stroke on a pane into a note with a
# 3D hit attached (roadmap Lane K2: body, station_s, world_pos, normal). This module is
# only the backend half: it takes WORLD-SPACE rays and a mesh, and returns hits. It
# deliberately does NOT do camera unprojection (screen point -> ray) — that lives in the
# desktop viewport (ServeMode/QML), which owns the camera, and inventing a second camera
# here would drift from it. A caller that has a screen point asks the viewport for its
# ray, then calls resolve_ray / raycast_stroke here.
#
# Mesh format (plain data, no engine types):
#   {"bodies": [{"name": "Body", "vertices": [[x, y, z], ...],
#                "faces": [[i, j, k], ...]}]}
# A single body dict is also accepted (it is treated as a one-body mesh). Faces may be
# triangles or quads (a quad is fanned into two triangles). A hit's normal is the face
# normal flipped to face the ray, so a stroke on the visible side always gets a normal
# pointing back at the viewer even when a mesh's winding is inconsistent, and the hit
# is the CLOSEST hit along the ray regardless of winding — draw-over is about what the
# user pointed at, not about culling.
#
# station_s needs a body long axis, supplied as axis={"origin": [x, y, z],
# "direction": [x, y, z], "length": L}. With a length, station_s is the hit's projection
# along the axis divided by L and clamped to 0..1; without one it is the raw projection
# (model units along the axis). Without an axis, station_s is None.


def _vec3(value, what):
    """a finite 3-number vector, or ValueError — bad input fails loudly, never silently."""
    try:
        items = [float(v) for v in value]
    except (TypeError, ValueError):
        raise ValueError(what + " must be three numbers")
    if len(items) != 3 or not all(math.isfinite(v) for v in items):
        raise ValueError(what + " must be three finite numbers")
    return items


def _sub(a, b):
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]


def _cross(a, b):
    return [a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0]]


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _bodies(mesh):
    """normalise a mesh dict (or a single body dict) into a list of bodies."""
    if isinstance(mesh, dict) and "bodies" in mesh:
        bodies = mesh["bodies"]
    elif isinstance(mesh, dict) and "vertices" in mesh:
        bodies = [mesh]
    else:
        raise ValueError("mesh must be a dict with 'bodies', or a single body dict")
    if not isinstance(bodies, list):
        raise ValueError("mesh 'bodies' must be a list")
    out = []
    for body in bodies:
        if not isinstance(body, dict) or "vertices" not in body or "faces" not in body:
            raise ValueError("each body needs 'vertices' and 'faces'")
        verts = [_vec3(v, "vertex") for v in body["vertices"]]
        faces = []
        for face in body["faces"]:
            try:
                idx = [int(i) for i in face]
            except (TypeError, ValueError):
                raise ValueError("face indices must be integers")
            if len(idx) < 3:
                raise ValueError("a face needs at least three indices")
            if any(i < 0 or i >= len(verts) for i in idx):
                raise ValueError("face index out of range")
            faces.append(idx)
        out.append({"name": str(body.get("name") or "body"),
                    "vertices": verts, "faces": faces})
    return out


def _ray_triangle(origin, direction, a, b, c):
    """Moller-Trumbore. Returns (distance, point, normal) or None for a miss."""
    edge1 = _sub(b, a)
    edge2 = _sub(c, a)
    pvec = _cross(direction, edge2)
    det = _dot(edge1, pvec)
    if abs(det) < 1e-12:
        return None
    inv = 1.0 / det
    tvec = _sub(origin, a)
    u = _dot(tvec, pvec) * inv
    if u < -1e-9 or u > 1.0 + 1e-9:
        return None
    qvec = _cross(tvec, edge1)
    v = _dot(direction, qvec) * inv
    if v < -1e-9 or u + v > 1.0 + 1e-9:
        return None
    t = _dot(edge2, qvec) * inv
    if t <= 1e-9:
        return None
    point = [origin[i] + t * direction[i] for i in range(3)]
    normal = _cross(edge1, edge2)
    length = math.sqrt(_dot(normal, normal))
    if length == 0:
        return None
    normal = [n / length for n in normal]
    if _dot(normal, direction) > 0:
        normal = [-n for n in normal]
    return t, point, normal


def _station(point, axis):
    """station along an axis, or None without one (see the module comment)."""
    if axis is None:
        return None
    if not isinstance(axis, dict) or "origin" not in axis or "direction" not in axis:
        raise ValueError("axis needs 'origin' and 'direction'")
    origin = _vec3(axis["origin"], "axis origin")
    direction = _vec3(axis["direction"], "axis direction")
    length = math.sqrt(_dot(direction, direction))
    if length == 0:
        raise ValueError("axis direction must not be zero")
    unit = [d / length for d in direction]
    proj = _dot(_sub(point, origin), unit)
    if "length" in axis and axis["length"] is not None:
        try:
            span = float(axis["length"])
        except (TypeError, ValueError):
            raise ValueError("axis length must be a number")
        if span <= 0:
            raise ValueError("axis length must be above 0")
        return min(1.0, max(0.0, proj / span))
    return proj


def resolve_ray(mesh, ray, axis=None):
    """the closest hit for one world-space ray, or None on a miss.

    ray is {"origin": [x, y, z], "direction": [x, y, z]} (direction need not be
    unit length). A hit is {"body", "face", "world_pos", "normal", "distance",
    "station_s"} — the fields the draw-over note JSON carries.
    """
    if not isinstance(ray, dict) or "origin" not in ray or "direction" not in ray:
        raise ValueError("ray needs 'origin' and 'direction'")
    origin = _vec3(ray["origin"], "ray origin")
    direction = _vec3(ray["direction"], "ray direction")
    if _dot(direction, direction) == 0:
        raise ValueError("ray direction must not be zero")
    best = None
    for body in _bodies(mesh):
        verts = body["vertices"]
        for face_index, face in enumerate(body["faces"]):
            # A quad (or n-gon) is fanned from its first vertex.
            for k in range(1, len(face) - 1):
                hit = _ray_triangle(origin, direction,
                                    verts[face[0]], verts[face[k]], verts[face[k + 1]])
                if hit is None:
                    continue
                t, point, normal = hit
                if best is None or t < best[0]:
                    best = (t, body["name"], face_index, point, normal)
    if best is None:
        return None
    t, name, face_index, point, normal = best
    return {"body": name, "face": face_index, "world_pos": point,
            "normal": normal, "distance": t,
            "station_s": _station(point, axis)}


def raycast_stroke(mesh, rays, axis=None):
    """one resolve_ray per ray in a stroke, in order; a miss stays None in place."""
    return [resolve_ray(mesh, ray, axis=axis) for ray in rays]


def attach_hit_to_note(note, hit):
    """a copy of a note dict with the 3D hit attached, or the note unchanged on a miss."""
    out = dict(note or {})
    if hit is None:
        return out
    out["hit"] = {"body": hit["body"], "station_s": hit["station_s"],
                  "world_pos": hit["world_pos"], "normal": hit["normal"]}
    return out
