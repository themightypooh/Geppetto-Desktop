"""The researcher's model sheet, boiled down for a run card: sizes, colour swatches, photos, links.

A research run writes model-sheet.json (+ .md) and refs/*.png into its folder. While it is still
researching only the photos exist, so the card shows those as they arrive.
"""
import json
import os

_cache = {}


def _size(d):
    """'38.8 cm' from a sheet dimension: the unit the source gave, plus a short basis tag"""
    if not isinstance(d, dict):
        return "", ""
    v, unit = d.get("value"), d.get("unit", "")
    text = ("%g %s" % (v, unit)).strip() if isinstance(v, (int, float)) else ""
    if isinstance(d.get("in"), (int, float)) and unit not in ("in", "inch", "inches"):
        text += "  (%g in)" % d["in"]
    return text, {"measured": "M", "photo": "P", "estimated": "E"}.get(d.get("basis"), "?")


def card(run_dir):
    """{"sheet": rows or None, "refs": [...], "links": [...], "sheetMd": path} for a run folder"""
    path = os.path.join(run_dir, "model-sheet.json")
    refs_dir = os.path.join(run_dir, "refs")
    try:
        stamp = (os.path.getmtime(path) if os.path.exists(path) else 0,
                 os.path.getmtime(refs_dir) if os.path.isdir(refs_dir) else 0)
    except OSError:
        stamp = (0, 0)
    hit = _cache.get(run_dir)
    if hit and hit[0] == stamp:
        return hit[1]
    out = {"sheet": None, "refs": [], "links": [], "sheetMd": ""}
    sheet = None
    if stamp[0]:
        try:
            sheet = json.load(open(path))
        except (OSError, ValueError):
            sheet = None
    if isinstance(sheet, dict):
        dims = []
        for k, d in (sheet.get("overall") or {}).items():
            size, basis = _size(d)
            dims.append({"part": "overall", "dim": k, "size": size, "basis": basis})
        parts = []
        for p in sheet.get("parts") or []:
            for k, d in (p.get("dims") or {}).items():
                size, basis = _size(d)
                dims.append({"part": p.get("name", ""), "dim": k, "size": size, "basis": basis})
            m = p.get("material") or {}
            parts.append({"name": p.get("name", ""), "hex": m.get("hex") or "#888888", "material": m.get("name", ""),
                          "metal": bool(m.get("metal")), "roughness": m.get("roughness"), "wear": m.get("wear", ""),
                          "section": p.get("cross_section", ""), "sampled": str(m.get("sampled_from", "")).startswith("refs/")})
        out["sheet"] = {"object": sheet.get("object", ""), "dims": dims, "parts": parts,
                        "order": " → ".join(sheet.get("build_order") or []), "scale": sheet.get("scale_cue", ""),
                        "gaps": len(sheet.get("gaps") or []), "refimage": len(sheet.get("refimage") or [])}
        out["refs"] = [{"png": os.path.join(run_dir, r.get("file", "")), "view": r.get("view", ""), "licence": r.get("licence", ""),
                        "credit": r.get("attribution", ""), "page": r.get("page") or r.get("url", ""), "main": bool(r.get("main"))}
                       for r in sheet.get("refs") or []]
        out["links"] = list(dict.fromkeys(s for s in sheet.get("sources") or [] if isinstance(s, str) and s.startswith("https://")))[:30]
        md = os.path.join(run_dir, "model-sheet.md")
        out["sheetMd"] = md if os.path.exists(md) else ""
    elif stamp[1]:
        try:
            names = sorted(f for f in os.listdir(refs_dir) if f.endswith(".png") and ".orig." not in f)
        except OSError:
            names = []
        out["refs"] = [{"png": os.path.join(refs_dir, f), "view": "", "licence": "", "credit": "", "page": "", "main": False} for f in names]
        for f in sorted(os.listdir(run_dir)) if os.path.isdir(run_dir) else []:
            if f.endswith("-sources.json"):
                try:
                    out["links"] = [s for s in json.load(open(os.path.join(run_dir, f))) if isinstance(s, str)][:30]
                except (OSError, ValueError):
                    pass
    _cache[run_dir] = (stamp, out)
    return out
