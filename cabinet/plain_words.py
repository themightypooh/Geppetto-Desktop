"""Build-script lines in plain words, for people who don't read scripts.

`say(line, lines, index)` turns one script line into a short sentence ("Puff up the lower lip on Head").
The caption the modeller wrote just above it (an `echo` line) wins when there is one, since that is the
modeller saying what the step is for. The raw line stays for a tooltip."""

import re

BRUSH = {
    "crease": "Carve a groove", "dam": "Carve a groove", "drawsharp": "Carve a sharp line",
    "inflate": "Puff up", "draw": "Build up", "clay": "Lay clay on", "claystrips": "Lay clay strips on",
    "claythumb": "Press clay flat on", "layer": "Add a layer to", "blob": "Bulge", "plateau": "Flatten the top of",
    "smooth": "Smooth", "relax": "Even out", "flatten": "Flatten", "fill": "Fill the dips in",
    "scrape": "Scrape", "multiplanescrape": "Scrape", "pinch": "Pinch", "grab": "Pull",
    "elasticgrab": "Pull", "snakehook": "Drag out", "thumb": "Push along", "slide": "Slide",
    "pose": "Bend", "boundary": "Pull the edge of", "twist": "Twist",
}
PUSH_IN = {"draw": "Push in", "inflate": "Shrink", "clay": "Scoop clay off", "blob": "Dent", "layer": "Cut a layer from"}
KEY = {
    "strength": "how hard", "radius": "brush size", "levels": "smoothness", "color": "colour", "rough": "shine",
    "metal": "metal look", "translate": "position", "rotationangle": "angle", "angle": "angle", "scale": "size",
    "sizex": "width", "sizey": "depth", "sizez": "height", "thickness": "thickness", "points": "path",
    "voxel": "fineness", "faces": "face count", "preset": "look", "opacity": "opacity", "depth": "depth",
    "size": "size", "source": "file", "material": "material", "mirror": "mirroring", "invert": "direction",
}
FEATURE = {                     # add TYPE ...: a sentence with {name} (the part) and {on} (the bodies it works on)
    "import": "Load {name} from a file", "primitive": "Add a basic shape, {name}", "profile": "Add {name} from an outline",
    "spline": "Add a curve, {name}", "blob": "Add a blobby shape, {name}", "part": "Add a {part} part, {name}",
    "transform": "Move or turn {on}", "mirror": "Mirror {on}", "linearpattern": "Repeat {on} in a row",
    "circularpattern": "Repeat {on} in a circle", "boolean": "Cut or join {on}", "shell": "Hollow out {on}",
    "fillet": "Round the edges of {on}", "chamfer": "Bevel the edges of {on}", "hole": "Drill a hole in {on}",
    "moveface": "Push faces of {on}", "deleteface": "Remove faces from {on}", "draft": "Angle the sides of {on}",
    "curvedeform": "Bend {on} along a curve", "subdivide": "Make {on} smoother and denser",
    "remesh": "Rebuild the surface of {on} evenly", "detail": "Add {pass} skin texture to {on}",
    "sculpt": "Sculpt {on}", "meshedit": "Hand-edit {on}", "facematerial": "Recolour faces of {on}",
    "uvproject": "Prepare {on} for textures", "paint": "Paint on {on}", "fleshmaterial": "Give {on} a {preset} look",
    "wearer": "Load {name}, the body to dress", "garment": "Dress {on} in a {recipe}", "trim": "Add {style} trim",
    "fur": "Add fur to {on}", "fabric": "Set a {fabric} fabric", "shapekeysplit": "Split the {key} expression left/right",
}
VERB = {
    "render": "Take a picture", "turnaround": "Take pictures all round", "describe": "Describe the model",
    "save": "Save the model", "export": "Export the model for the game", "bake": "Bake the detail into textures",
    "measure": "Measure", "at": "Measure", "distance": "Measure a distance", "clearance": "Check parts don't overlap",
    "symmetry": "Check left and right match", "topology": "Check the mesh", "section": "Slice the model to measure it",
    "flex": "Bend the joints to test them", "match": "Compare with the reference picture", "rebuild": "Rebuild everything",
    "fix": "Mend the mesh", "remedy": "Apply the suggested fix", "pivot": "Set where the model stands",
    "corrective": "Add a joint fix", "paintmaps": "Write the painted textures", "refimage": "Pin up a reference photo",
}


def _args(words):
    out = {}
    for w in words:
        if "=" in w:
            k, v = w.split("=", 1)
            out[k.strip().lower().replace("_", "")] = v.strip().strip('"')
    return out


def _nice(s):
    """LowerLip -> lower lip, face___face_head -> face head"""
    s = re.sub(r"[_/]+", " ", s or "").strip()
    s = re.sub(r"(?<=[a-z])(?=[A-Z])", " ", s)
    return s.lower()


def _colour(v):
    return v if v.startswith("#") else v


def _how_hard(v):
    try:
        f = float(v)
    except ValueError:
        return ""
    return "very lightly" if f < 0.1 else "lightly" if f < 0.25 else "" if f < 0.5 else "hard"


def caption(lines, index):
    """The echo line just above script line `index` (1-based), '' when another command sits between."""
    i = index - 2
    while i >= 0:
        t = lines[i].strip()
        if not t or t.startswith("#"):
            i -= 1
            continue
        return t[5:].strip() if t.startswith("echo ") else ""
    return ""


def say(line, lines=None, index=0):
    """One script line as a short plain sentence."""
    text = (line or "").strip()
    if not text:
        return ""
    cap = caption(lines, index) if lines and index else ""
    if cap:
        return cap[0].upper() + cap[1:]
    words = text.split()
    verb = words[0].lower()
    rest = words[1:]
    a = _args(rest)
    if verb == "echo":
        return text[5:].strip()
    if verb.startswith("#"):
        return text.lstrip("# ")
    if verb == "add" and rest:
        kind = rest[0].lower().replace("feature", "")
        name = _nice(a.get("name", "")) or kind
        on = _nice(a.get("bodies", "")) or "the model"
        if kind == "stroke":
            brush = a.get("brush", "draw").lower()
            doing = (PUSH_IN.get(brush) if a.get("invert") in ("1", "true") else None) or BRUSH.get(brush, brush.title())
            where = _nice(a.get("name", "")) or ""
            s = "%s %s" % (doing, (where + " on " + on) if where and where != on else on)
            hard = _how_hard(a.get("strength", ""))
            if hard:
                s += ", " + hard
            if a.get("mirror"):
                s += ", both sides"
            return s
        tpl = FEATURE.get(kind, "Add " + kind + " {name}")
        return tpl.format(name=name, on=on, part=_nice(a.get("part", "")), pass_=a.get("pass", ""),
                          **{"pass": _nice(a.get("pass", "surface")), "preset": _nice(a.get("preset", "flesh")),
                             "recipe": _nice(a.get("recipe", "garment")), "style": _nice(a.get("style", "")),
                             "fabric": _nice(a.get("fabric", "")), "key": _nice(a.get("key", ""))})
    if verb == "set" and rest:
        who = _nice(rest[0])
        bits = []
        for k, v in a.items():
            if k == "strength":
                bits.append("press %s (%s)" % ("softer" if _how_hard(v) else "harder", v))
            elif k == "radius":
                bits.append("brush size %s" % v)
            elif k in ("color", "colour"):
                bits.append("colour " + _colour(v))
            else:
                bits.append("%s to %s" % (KEY.get(k, k), v))
        return "Adjust %s: %s" % (who, ", ".join(bits)) if bits else "Adjust " + who
    if verb == "remove" and rest:
        return "Take %s off" % _nice(rest[0])
    if verb in ("suppress", "hide") and rest:
        return "Hide %s" % _nice(rest[0])
    if verb in ("unsuppress", "show") and rest:
        return "Show %s again" % _nice(rest[0])
    if verb == "rename" and len(rest) > 1:
        return "Rename %s to %s" % (_nice(rest[0]), _nice(rest[1]))
    if verb in ("material", "materiallook") and rest:
        bits = []
        if "color" in a:
            bits.append("colour " + a["color"])
        if "preset" in a:
            bits.append(_nice(a["preset"]))
        if "rough" in a:
            try:
                r = float(a["rough"])
                bits.append("matte" if r > 0.6 else "shiny" if r < 0.3 else "satin")
            except ValueError:
                pass
        if a.get("metal") not in (None, "0"):
            bits.append("metallic")
        return "Paint material %s%s" % (rest[0], (": " + ", ".join(bits)) if bits else "")
    if verb == "edit" and len(rest) > 1:
        return "Edit %s: %s" % (_nice(rest[0]), _nice(rest[1]))
    if verb == "render":
        n = a.get("views", "1")
        return "Take a picture" + (" from %s sides" % n if n not in ("1", "") else "") + (" (clay)" if a.get("shade") == "clay" else "")
    if verb in VERB:
        return VERB[verb]
    return text
