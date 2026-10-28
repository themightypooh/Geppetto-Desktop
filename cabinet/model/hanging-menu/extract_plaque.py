import sys
src, dst = sys.argv[1], sys.argv[2]
verts, faces, cur = [], [], None
for line in open(src):
    if line.startswith("v "):
        verts.append(line)
    elif line.startswith("usemtl"):
        cur = line.split()[1]
    elif line.startswith("f "):
        if cur in ("material_0", "material_1"):
            faces.append((cur, line))
used = set()
for _, line in faces:
    for tok in line.split()[1:]:
        used.add(int(tok.split("/")[0]) - 1)
remap = {old: i + 1 for i, old in enumerate(sorted(used))}
with open(dst, "w") as f:
    for old in sorted(used):
        f.write(verts[old])
    last = None
    for mat, line in faces:
        if mat != last:
            f.write("usemtl %s\n" % mat)
            last = mat
        parts = []
        for tok in line.split()[1:]:
            idx = tok.split("/")
            idx[0] = str(remap[int(idx[0]) - 1])
            parts.append("/".join(idx))
        f.write("f " + " ".join(parts) + "\n")
print("verts", len(used), "faces", len(faces))
