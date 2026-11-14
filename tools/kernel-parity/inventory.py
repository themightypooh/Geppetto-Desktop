#!/usr/bin/env python3
"""Deterministic path/hash/API inventory for the shared Effigy kernel.

Compares four trees:
  desktop-kernel  /home/poo/Desktop/geppetto-desktop/kernel/Effigy
  mobile-kernel   /home/poo/geppetto-mobile/vendor/geppetto-desktop/kernel/Effigy
  desktop-core    /home/poo/Desktop/geppetto-desktop/core
  mobile-app      /home/poo/geppetto-mobile/app

Usage (on GOO, from the Desktop repo root):
  python3 tools/kernel-parity/inventory.py generate \
      --out handoffs/kernel-parity-inventory.json
  python3 tools/kernel-parity/inventory.py check \
      --baseline handoffs/kernel-parity-inventory.json

`generate` writes the inventory JSON. `check` regenerates in memory,
compares against the baseline, prints every drift and exits 1 on any
difference (0 when identical). Comparison ignores the `meta` section
(generated_at, git HEADs, command line), so re-running on unchanged
source is always a clean pass.

API surface is extracted source-level (regex over comment/string-stripped
source, whitespace-collapsed). It is a drift detector, not a semantic
API review: it lists public/protected type and member declarations per
file. A file whose bytes are identical has an identical API by
construction, so API diffs are only computed for files that differ.

Excludes: *.bak* files, bin/, obj/, .git/. Paths in the output are
posix, relative to their tree root, sorted. Hashes are sha256 of file
bytes. Output JSON is sorted-key, indent 2, so it diffs cleanly.
"""

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone

GENERATOR_VERSION = "1.0.0"

DEFAULT_DESKTOP_ROOT = "/home/poo/Desktop/geppetto-desktop"
DEFAULT_MOBILE_ROOT = "/home/poo/geppetto-mobile"

TREES = {
    "desktop-kernel": ("kernel/Effigy", DEFAULT_DESKTOP_ROOT),
    "mobile-kernel": ("vendor/geppetto-desktop/kernel/Effigy", DEFAULT_MOBILE_ROOT),
    "desktop-core": ("core", DEFAULT_DESKTOP_ROOT),
    "mobile-app": ("app", DEFAULT_MOBILE_ROOT),
}

SKIP_DIRS = {"bin", "obj", ".git"}


def is_source_file(path):
    if not path.endswith(".cs"):
        return False
    if ".bak" in path:
        return False
    parts = path.split(os.sep)
    return not any(p in SKIP_DIRS for p in parts)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# --- API extraction -------------------------------------------------------

_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
_LINE_COMMENT = re.compile(r"//[^\n]*")
_STRING = re.compile(r'"(?:\\.|[^"\\])*"')
_CHAR = re.compile(r"'(?:\\.|[^'\\])*'")
_VERBATIM_STRING = re.compile(r'@"(?:[^"]|"")*"')
_INTERPOLATED = re.compile(r'\$"(?:\\.|[^"\\])*"')

_MODIFIERS = (
    r"(?:static\s+|sealed\s+|abstract\s+|partial\s+|virtual\s+|override\s+|"
    r"async\s+|extern\s+|unsafe\s+|new\s+|readonly\s+|volatile\s+|ref\s+)*"
)

# public/internal type declaration: kind + name (+ generics).
_TYPE_RE = re.compile(
    r"\b(public|internal)\s+"
    + _MODIFIERS + r"\s*"
    r"(class|struct|interface|enum|record)\s+"
    r"(\w+(?:\s*<[^>]+>)?)"
)

# public/protected member declaration on whitespace-collapsed source:
#   <access> [modifiers] [return-type] Name ( params ) | Name ; | Name = | Name { | Name =>
# Return type is optional (constructors). `where` clauses and bodies are
# stripped by only keeping up to the parameter list / terminator.
_MEMBER_RE = re.compile(
    r"\b(public|protected)\s+"
    + _MODIFIERS + r"\s*"
    r"(?:([\w<>\[\],\s\?\.]+?)\s+)?"
    r"(\w+)\s*"
    r"(\([^)]*\)|;|=|{|=>)"
)

# Keywords that look like members but are not declarations.
_NON_MEMBER_NAMES = {
    "if", "else", "for", "foreach", "while", "do", "switch", "case",
    "return", "throw", "using", "lock", "checked", "unchecked", "fixed",
    "catch", "finally", "try", "get", "set", "add", "remove",
}


def strip_code(text):
    text = _BLOCK_COMMENT.sub(" ", text)
    text = _LINE_COMMENT.sub(" ", text)
    text = _VERBATIM_STRING.sub('""', text)
    text = _INTERPOLATED.sub('""', text)
    text = _STRING.sub('""', text)
    text = _CHAR.sub("''", text)
    return re.sub(r"\s+", " ", text)


def extract_api(source):
    """Return a sorted list of normalized public/protected declarations."""
    flat = strip_code(source)
    api = set()
    for m in _TYPE_RE.finditer(flat):
        kind, name = m.group(2), re.sub(r"\s+", "", m.group(3))
        api.add("type %s %s" % (kind, name))
    for m in _MEMBER_RE.finditer(flat):
        ret, name, tail = m.group(2), m.group(3), m.group(4)
        if name in _NON_MEMBER_NAMES:
            continue
        if ret is not None:
            ret = re.sub(r"\s+", " ", ret).strip()
            # A type declaration also matches the member pattern
            # ("public static class Foo {"); the type entry above owns it.
            if ret in ("class", "struct", "interface", "enum", "record"):
                continue
            # Drop attribute-looking and generic-constraint noise.
            if "[" in ret or ret.startswith("where"):
                continue
        params = ""
        if tail.startswith("("):
            params = re.sub(r"\s+", " ", tail).strip()
            # Drop default values and parameter names stay; keep it simple.
            params = re.sub(r"\s*=\s*[^,)]+", "", params)
        sig = ("member %s %s%s" % (ret + " " if ret else "", name, params)).strip()
        sig = re.sub(r"\s+", " ", sig)
        api.add(sig)
    return sorted(api)


# --- Tree walk ------------------------------------------------------------

def inventory_tree(root):
    files = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        for name in sorted(filenames):
            full = os.path.join(dirpath, name)
            if not is_source_file(full):
                continue
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            with open(full, "r", encoding="utf-8", errors="replace") as f:
                source = f.read()
            files.append({
                "path": rel,
                "sha256": sha256_file(full),
                "bytes": os.path.getsize(full),
                "api": extract_api(source),
            })
    files.sort(key=lambda e: e["path"])
    return files


def git_head(repo):
    try:
        out = subprocess.run(
            ["git", "-C", repo, "rev-parse", "HEAD"],
            capture_output=True, text=True, timeout=30,
        )
        return out.stdout.strip() if out.returncode == 0 else None
    except Exception:
        return None


def build_inventory(desktop_root, mobile_root):
    roots = {}
    trees = {}
    for tree, (rel, default_base) in TREES.items():
        base = desktop_root if default_base == DEFAULT_DESKTOP_ROOT else mobile_root
        root = os.path.join(base, rel)
        roots[tree] = root
        trees[tree] = inventory_tree(root)

    dk = {f["path"]: f for f in trees["desktop-kernel"]}
    mk = {f["path"]: f for f in trees["mobile-kernel"]}
    same, different, mobile_only, desktop_only = [], [], [], []
    for path in sorted(set(dk) | set(mk)):
        if path in dk and path in mk:
            if dk[path]["sha256"] == mk[path]["sha256"]:
                same.append(path)
            else:
                d_api = set(dk[path]["api"])
                m_api = set(mk[path]["api"])
                different.append({
                    "path": path,
                    "desktop_sha256": dk[path]["sha256"],
                    "mobile_sha256": mk[path]["sha256"],
                    # In Desktop but not Mobile: Desktop-only APIs to preserve.
                    "desktop_only_api": sorted(d_api - m_api),
                    # In Mobile but not Desktop: Mobile-side additions.
                    "mobile_only_api": sorted(m_api - d_api),
                })
        elif path in mk:
            mobile_only.append(path)
        else:
            desktop_only.append(path)

    # Desktop-only APIs inside shared (differing) files: the preservation list.
    preservation = []
    for entry in different:
        for sig in entry["desktop_only_api"]:
            preservation.append({"file": entry["path"], "api": sig})
    preservation.sort(key=lambda e: (e["file"], e["api"]))

    return {
        "meta": {
            "generator": "tools/kernel-parity/inventory.py",
            "generator_version": GENERATOR_VERSION,
            "generated_at_utc": datetime.now(timezone.utc).isoformat(),
            "desktop_repo_head": git_head(desktop_root),
            "mobile_repo_head": git_head(mobile_root),
            "command": " ".join(sys.argv),
        },
        "roots": roots,
        "counts": {
            "desktop-kernel": len(trees["desktop-kernel"]),
            "mobile-kernel": len(trees["mobile-kernel"]),
            "desktop-core": len(trees["desktop-core"]),
            "mobile-app": len(trees["mobile-app"]),
            "kernel_same": len(same),
            "kernel_different": len(different),
            "kernel_mobile_only": len(mobile_only),
            "kernel_desktop_only": len(desktop_only),
        },
        "trees": trees,
        "kernel_comparison": {
            "same": same,
            "different": different,
            "mobile_only": mobile_only,
            "desktop_only": desktop_only,
        },
        "preservation": {
            "desktop_only_api": preservation,
            "note": "Every entry here exists in Desktop kernel but not in "
                    "Mobile vendor kernel. A sync that overwrites either side "
                    "blindly loses it. Each entry is owned by the kernel "
                    "ticket named in handoffs/kernel-parity-contract.md.",
        },
    }


def comparable(inventory):
    """Inventory minus meta (timestamps/HEADs change without source drift)."""
    copy = {k: v for k, v in inventory.items() if k != "meta"}
    return copy


def diff_report(baseline, current):
    """Human-readable drift lines between two comparable inventories."""
    lines = []
    b_trees = {t: {f["path"]: f for f in fs} for t, fs in baseline["trees"].items()}
    c_trees = {t: {f["path"]: f for f in fs} for t, fs in current["trees"].items()}
    for tree in sorted(set(b_trees) | set(c_trees)):
        b, c = b_trees.get(tree, {}), c_trees.get(tree, {})
        for path in sorted(set(b) | set(c)):
            if path not in b:
                lines.append("ADDED %s %s" % (tree, path))
            elif path not in c:
                lines.append("REMOVED %s %s" % (tree, path))
            elif b[path]["sha256"] != c[path]["sha256"]:
                b_api = set(b[path].get("api", []))
                c_api = set(c[path].get("api", []))
                added = sorted(c_api - b_api)
                removed = sorted(b_api - c_api)
                detail = ""
                if added:
                    detail += " api+[%s]" % "; ".join(added)
                if removed:
                    detail += " api-[%s]" % "; ".join(removed)
                lines.append("CHANGED %s %s%s" % (tree, path, detail))
    b_counts, c_counts = baseline["counts"], current["counts"]
    for key in sorted(set(b_counts) | set(c_counts)):
        if b_counts.get(key) != c_counts.get(key):
            lines.append("COUNT %s: %s -> %s" % (key, b_counts.get(key), c_counts.get(key)))
    return lines


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    gen = sub.add_parser("generate")
    gen.add_argument("--out", required=True)
    gen.add_argument("--desktop-root", default=DEFAULT_DESKTOP_ROOT)
    gen.add_argument("--mobile-root", default=DEFAULT_MOBILE_ROOT)
    chk = sub.add_parser("check")
    chk.add_argument("--baseline", required=True)
    chk.add_argument("--desktop-root", default=DEFAULT_DESKTOP_ROOT)
    chk.add_argument("--mobile-root", default=DEFAULT_MOBILE_ROOT)
    args = parser.parse_args()

    if args.command == "generate":
        inv = build_inventory(args.desktop_root, args.mobile_root)
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(inv, f, indent=2, sort_keys=True)
            f.write("\n")
        c = inv["counts"]
        print("wrote %s" % args.out)
        print("desktop-kernel=%d mobile-kernel=%d desktop-core=%d mobile-app=%d" % (
            c["desktop-kernel"], c["mobile-kernel"], c["desktop-core"], c["mobile-app"]))
        print("kernel same=%d different=%d mobile-only=%d desktop-only=%d" % (
            c["kernel_same"], c["kernel_different"], c["kernel_mobile_only"],
            c["kernel_desktop_only"]))
        print("desktop-only APIs inside differing files: %d" % len(inv["preservation"]["desktop_only_api"]))
        return 0

    with open(args.baseline, "r", encoding="utf-8") as f:
        baseline = json.load(f)
    current = build_inventory(args.desktop_root, args.mobile_root)
    drift = diff_report(baseline, current)
    if not drift:
        print("OK: inventory matches baseline %s (meta excluded)" % args.baseline)
        return 0
    print("DRIFT: %d change(s) vs baseline %s" % (len(drift), args.baseline))
    for line in drift:
        print("  " + line)
    return 1


if __name__ == "__main__":
    sys.exit(main())
