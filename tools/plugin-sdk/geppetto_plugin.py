"""Geppetto Plugin SDK — Python half (godlike Wave 3 P2).

Mirrors the C# SDK (core/PluginSdk.cs, core/TextScene.cs): the same manifest
schema, the same HMAC-SHA256 signing over the same canonical form (sorted
keys, compact JSON), the same capability gate, and the same .gtscn text-scene
format. A Python add-on author validates and signs a package with this module;
the Desktop host verifies it with the C# side. Pure standard library.

Canonical form: sorted keys, compact JSON, raw UTF-8 text on both sides,
so a manifest signed here verifies on the C# side and vice versa.

Signing is tamper-evidence against a trusted-publisher key list, NOT a PKI
code-signing chain — same honesty rule as the C# side.
"""

import hashlib
import hmac
import json
import os

API_VERSION = (1, 0)

GRANTABLE_CAPABILITIES = frozenset({
    "assets.read", "assets.write",
    "brush.register", "material.register", "io.register",
    "log",
})

MANIFEST_FILE = "addon.json"


def canonical_manifest(manifest):
    """Canonical JSON: the manifest without its signature, sorted keys,
    compact separators — the form that gets signed."""
    unsigned = {k: v for k, v in manifest.items() if k != "signature"}
    # Sorted keys, compact JSON, raw UTF-8 — the same canonical form the C#
    # side uses (UnsafeRelaxedJsonEscaping), so a manifest signed here
    # verifies there and vice versa.
    return json.dumps(unsigned, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False)


def sign(manifest, publisher_key):
    """HMAC-SHA256 hex over the canonical manifest."""
    return hmac.new(
        publisher_key, canonical_manifest(manifest).encode("utf-8"),
        hashlib.sha256).hexdigest()


def verify(manifest, publisher_key):
    """True only when the manifest carries a signature that recomputes."""
    signature = manifest.get("signature")
    if not signature:
        return False
    return hmac.compare_digest(sign(manifest, publisher_key), signature)


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _safe_join(root, relative):
    """Join a manifest-listed relative path under a root, refusing absolute
    paths and '..' escapes. Returns None when unsafe."""
    if not relative or os.path.isabs(relative):
        return None
    full = os.path.normpath(os.path.join(os.path.abspath(root), relative))
    if full != os.path.abspath(root) and not full.startswith(
            os.path.abspath(root) + os.sep):
        return None
    return full


def validate_manifest(manifest, trusted_publishers, host_api=API_VERSION):
    """The install gate, minus the file checks (see validate_package).
    Returns a list of error strings; empty means the manifest passes."""
    errors = []
    for field in ("id", "name", "version", "entryPoint", "publisherId"):
        if not manifest.get(field):
            errors.append("Manifest %s is required." % field)
    if not manifest.get("signature"):
        errors.append("Unsigned add-on is rejected: manifest has no signature.")
    elif manifest.get("publisherId") not in trusted_publishers:
        errors.append("Unknown publisher '%s': not in the trusted publisher list."
                      % manifest.get("publisherId"))
    elif not verify(manifest, trusted_publishers[manifest["publisherId"]]):
        errors.append("Signature does not match the manifest: the add-on was "
                      "tampered with or signed with another key.")
    required = (manifest.get("requiredApiMajor", 1),
                manifest.get("requiredApiMinor", 0))
    if required[0] != host_api[0] or required[1] > host_api[1]:
        errors.append("Add-on needs API %d.%d, this host implements %d.%d."
                      % (required[0], required[1], host_api[0], host_api[1]))
    for capability in manifest.get("capabilities", []):
        if capability not in GRANTABLE_CAPABILITIES:
            errors.append("Capability '%s' is never granted in v1." % capability)
    return errors


def validate_package(package_dir, trusted_publishers, host_api=API_VERSION):
    """Full gate: manifest gate plus every listed file present with a
    matching SHA-256. Returns a list of error strings."""
    manifest_path = os.path.join(package_dir, MANIFEST_FILE)
    if not os.path.isfile(manifest_path):
        return ["Add-on package has no %s." % MANIFEST_FILE]
    try:
        with open(manifest_path, "r", encoding="utf-8") as handle:
            manifest = json.load(handle)
    except (OSError, ValueError) as exc:
        return ["addon.json is malformed: %s" % exc]
    errors = validate_manifest(manifest, trusted_publishers, host_api)
    for entry in manifest.get("files", []):
        full = _safe_join(package_dir, entry.get("path", ""))
        if full is None:
            errors.append("File '%s' escapes the package directory."
                          % entry.get("path", ""))
            continue
        if not os.path.isfile(full):
            errors.append("Listed file '%s' is missing from the package."
                          % entry.get("path", ""))
            continue
        if sha256_file(full) != entry.get("sha256", ""):
            errors.append("File '%s' hash mismatch: package was tampered with."
                          % entry.get("path", ""))
    return errors


# --- Text scenes (.gtscn): same tab-separated format as core/TextScene.cs ---

def _fmt_float(value):
    # Shortest round-trip repr, matching C# float "R" for ordinary values.
    return repr(float(value))


def serialize_text_scene(doc):
    """Canonical text: same document, same bytes, every time."""
    lines = ["gtscn 1"]
    for layer in sorted(doc.get("layers", []), key=lambda item: item["name"]):
        lines.append("layer\t%s\t%s\t%s" % (
            layer["name"], layer["artist"], layer["department"]))
    for asset in sorted(doc.get("assets", []), key=lambda item: item["id"]):
        lines.append("asset\t%s\t%s\t%s" % (
            asset["id"], asset["kind"], asset["path"]))
    for node in sorted(doc.get("nodes", []), key=lambda item: item["id"]):
        lines.append("node\t%s\t%s\t%s\t%s" % (
            node["id"], node.get("parentId") or "-",
            node["name"], node.get("layer") or "-"))
    for xform in sorted(doc.get("xforms", []), key=lambda item: item["nodeId"]):
        vec = lambda values: " ".join(_fmt_float(v) for v in values)
        lines.append("xform\t%s\t%s\t%s\t%s" % (
            xform["nodeId"], vec(xform["position"]),
            vec(xform["rotationDegrees"]), vec(xform["scale"])))
    lines.extend(doc.get("unknownLines", []))
    return "\n".join(lines) + "\n"


def parse_text_scene(text):
    """Parse .gtscn text. Raises ValueError with a line number on a bad
    header, duplicate id, dangling parent, unknown layer, transform for an
    unknown node, or malformed number. Unknown record types are kept."""
    doc = {"layers": [], "assets": [], "nodes": [], "xforms": [],
           "unknownLines": []}
    lines = text.replace("\r\n", "\n").split("\n")
    if not lines or lines[0] != "gtscn 1":
        raise ValueError("Line 1: text scene must start with 'gtscn 1'.")
    layer_names, asset_ids, node_ids = set(), set(), set()
    for index in range(1, len(lines)):
        line = lines[index]
        if not line:
            continue
        line_no = index + 1
        head, _, rest = line.partition("\t")
        fields = rest.split("\t") if rest else []
        if head == "layer":
            if len(fields) != 3:
                raise ValueError("Line %d: layer needs name, artist, department."
                                 % line_no)
            if fields[0] in layer_names:
                raise ValueError("Line %d: duplicate layer '%s'." % (line_no, fields[0]))
            layer_names.add(fields[0])
            doc["layers"].append({"name": fields[0], "artist": fields[1],
                                  "department": fields[2]})
        elif head == "asset":
            if len(fields) != 3:
                raise ValueError("Line %d: asset needs id, kind, path." % line_no)
            if fields[0] in asset_ids:
                raise ValueError("Line %d: duplicate asset id '%s'." % (line_no, fields[0]))
            asset_ids.add(fields[0])
            doc["assets"].append({"id": fields[0], "kind": fields[1],
                                  "path": fields[2]})
        elif head == "node":
            if len(fields) != 4:
                raise ValueError("Line %d: node needs id, parent, name, layer."
                                 % line_no)
            if fields[0] in node_ids:
                raise ValueError("Line %d: duplicate node id '%s'." % (line_no, fields[0]))
            node_ids.add(fields[0])
            doc["nodes"].append({"id": fields[0],
                                 "parentId": "" if fields[1] == "-" else fields[1],
                                 "name": fields[2],
                                 "layer": "" if fields[3] == "-" else fields[3]})
        elif head == "xform":
            if len(fields) != 4:
                raise ValueError("Line %d: xform needs node id, position, "
                                 "rotation, scale." % line_no)
            doc["xforms"].append({"nodeId": fields[0],
                                  "position": _parse_vector(fields[1], line_no),
                                  "rotationDegrees": _parse_vector(fields[2], line_no),
                                  "scale": _parse_vector(fields[3], line_no)})
        else:
            doc["unknownLines"].append(line)
    for node in doc["nodes"]:
        if node["parentId"] and node["parentId"] not in node_ids:
            raise ValueError("Node '%s' has a dangling parent '%s'."
                             % (node["id"], node["parentId"]))
        if node["layer"] and node["layer"] not in layer_names:
            raise ValueError("Node '%s' references unknown layer '%s'."
                             % (node["id"], node["layer"]))
    for xform in doc["xforms"]:
        if xform["nodeId"] not in node_ids:
            raise ValueError("Transform references unknown node '%s'."
                             % xform["nodeId"])
    return doc


def _parse_vector(text, line_no):
    parts = text.split()
    if len(parts) != 3:
        raise ValueError("Line %d: a vector needs exactly three numbers." % line_no)
    try:
        return [float(part) for part in parts]
    except ValueError:
        raise ValueError("Line %d: '%s' is not a number." % (line_no, text))


def search_catalog(entries, query):
    """Store search: blank query returns everything, name order. Otherwise
    name matches first, then tag matches, then summary matches."""
    if not query or not query.strip():
        return sorted(entries, key=lambda e: e["name"].lower())
    query = query.strip().lower()

    def score(entry):
        if query in entry["name"].lower():
            return 0
        if any(query in tag.lower() for tag in entry.get("tags", [])):
            return 1
        if query in entry.get("summary", "").lower():
            return 2
        return 3
    return sorted((e for e in entries if score(e) < 3),
                  key=lambda e: (score(e), e["name"].lower()))


if __name__ == "__main__":
    print("Geppetto Plugin SDK (Python), API %d.%d" % API_VERSION)
    print("Grantable capabilities: %s" % ", ".join(sorted(GRANTABLE_CAPABILITIES)))
