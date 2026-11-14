#!/usr/bin/env bash
set -euo pipefail

# D3: exercise only the installed archive, never a source-tree DLL. All temporary
# files belong to this invocation; the input archive and checkout are read-only.
if [[ $# -lt 1 || $# -gt 2 ]]; then
    echo "Usage: $0 geppetto-desktop-linux-x64.tar.gz [output-directory]" >&2
    exit 2
fi
archive="$(realpath "$1")"
[[ -f "$archive" ]] || { echo "Archive not found: $archive" >&2; exit 2; }
out="${2:-$(mktemp -d /tmp/opencode/geppetto-installed-smoke.XXXXXX)}"
mkdir -p "$out"
out="$(realpath "$out")"
install="$(mktemp -d /tmp/opencode/geppetto-installed.XXXXXX)"
trap 'chmod -R u+w "$install"; rm -rf "$install"' EXIT

if [[ -f "$archive.sha256" ]]; then
    (cd "$(dirname "$archive")" && sha256sum -c "$(basename "$archive").sha256") > "$out/checksum.log"
else
    sha256sum "$archive" > "$out/checksum.log"
fi
tar -xzf "$archive" -C "$install" --no-same-owner
[[ -x "$install/run.sh" && -x "$install/marionette-desktop" ]] || { echo 'Missing packaged executable or launcher' >&2; exit 1; }
[[ -f "$install/sample/sample.effigy" && -f "$install/sample/sample-rig.fbx" && -f "$install/sample/sample.riganim" ]] || { echo 'Missing bundled sample' >&2; exit 1; }

# Native availability must match what the packager recorded in build-info.json:
# bundled means the file is really there with the recorded hash; absent means
# it is really absent (no fake success either way).
python3 - "$install" "$out" <<'PY'
import hashlib, json, pathlib, sys
install, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
info = json.loads((install / "build-info.json").read_text())
native = info.get("nativeLibraries", {})
lines = []
manifold = native.get("manifold", {})
status = manifold.get("status")
lib = install / "runtimes" / "linux-x64" / "native" / "libgmmanifold.so"
if status == "bundled":
    if not lib.is_file():
        raise ValueError("build-info.json says Manifold is bundled but runtimes/linux-x64/native/libgmmanifold.so is missing")
    actual = hashlib.sha256(lib.read_bytes()).hexdigest()
    if manifold.get("sha256") and actual != manifold["sha256"]:
        raise ValueError(f"libgmmanifold.so sha256 {actual} != recorded {manifold['sha256']}")
    lines.append(f"manifold: bundled, sha256 {actual}")
elif status == "absent":
    if lib.exists():
        raise ValueError("build-info.json says Manifold is absent but libgmmanifold.so is present — availability record is wrong")
    lines.append("manifold: absent (built-in ExactMeshBoolean fallback applies)")
else:
    raise ValueError(f"build-info.json has unexpected manifold status: {status!r}")
lines.append(f"openvdb: {native.get('openvdb', {}).get('status', 'unknown')}")
lines.append(f"mikktspace: {native.get('mikktspace', {}).get('status', 'unknown')}")
(out / "native-availability.log").write_text("\n".join(lines) + "\n")
print("PASS native availability: " + "; ".join(lines))
PY

# No writes to the installation, even by the sample render. The unrelated cwd,
# HOME and XDG directories make accidental reliance on developer paths visible.
chmod -R a-w "$install"
mkdir -p "$out/home" "$out/work" "$out/config" "$out/cache" "$out/state" "$out/bin"
ln -s "$(command -v bash)" "$out/bin/bash"
ln -s "$(command -v dirname)" "$out/bin/dirname"
ldd "$install/marionette-desktop" > "$out/dependencies.log" 2>&1 || true
{ uname -a; cat /etc/os-release 2>/dev/null || true; command -v eglinfo >/dev/null && eglinfo -B 2>&1 || true; } > "$out/host.log"

run() {
    (cd "$out/work" && env -i \
        PATH="$out/bin" HOME="$out/home" \
        XDG_CONFIG_HOME="$out/config" XDG_CACHE_HOME="$out/cache" XDG_STATE_HOME="$out/state" \
        "$install/run.sh" "$@")
}

# --- CPU-only steps (no GPU, no display) — these must pass on every host ---
run --help > "$out/help.log" 2>&1
# --list-bones reads the bundled rig with FbxImport only; no GL context.
run --list-bones --model "$install/sample/sample-rig.fbx" > "$out/list-bones.log" 2>&1
grep -qi 'bone' "$out/list-bones.log" || { echo 'CPU-only --list-bones produced no bones' >&2; exit 1; }
# --script runs bot commands against the kernel only (ScriptLive never creates
# a GL context): create a primitive and save it, from the relocated package,
# with no source checkout.
cat > "$out/work/cpu-script.txt" <<'SCRIPT'
add Primitive name=SmokeBox shape=Box sizex=10 sizey=10 sizez=10
save smoke.effigy
SCRIPT
# LV1 2026-11-13: the binary refuses AI modelling without po's approval (GateCheck.cs, exit 3).
# A smoke check is not an AI build: --test-sandbox with an explicit, fresh --run inside this
# invocation's own evidence folder (never ~/.local/share/geppetto/research), so nothing can
# be placed in a chest pane.
run --script "$out/work/cpu-script.txt" --live --test-sandbox --run "$out/work/cpu-run" --out "$out/work" > "$out/cpu-script.log" 2>&1
[[ -f "$out/work/smoke.effigy" ]] || { echo 'CPU-only --script did not save smoke.effigy' >&2; exit 1; }
head -c 7 "$out/work/smoke.effigy" | grep -q '^effigy' || { echo 'smoke.effigy is not an .effigy document' >&2; exit 1; }

# --- GPU steps, gated on hardware EGL ---
# Headless rendering needs hardware EGL; on a host without it these steps are
# BLOCKED (recorded in gpu-blocked.log), not passed and not failed. The
# bad-input check below stays outside the gate: HeadlessRender loads the model
# before creating any GL context, so it works without a GPU.
if command -v ldconfig >/dev/null 2>&1 && ldconfig -p 2>/dev/null | grep -q 'libEGL\.so\.1'; then
    gpu_available=1
else
    gpu_available=0
    echo 'GPU acceptance BLOCKED: no libEGL.so.1 on this host (HeadlessGlContext requires hardware EGL; no window will be opened). Render/clip steps skipped; CPU-only steps above still ran.' > "$out/gpu-blocked.log"
fi
if [[ "$gpu_available" -eq 1 ]]; then
for index in 1 2; do
    run --headless --frames 1 --size 128x96 \
        --model "$install/sample/sample.effigy" --screenshot "$out/render-$index.png" \
        > "$out/render-$index.log" 2>&1
done
run --headless --frames 1 --size 128x96 --time 0.5 \
    --model "$install/sample/sample-rig.fbx" --clip "$install/sample/sample.riganim" \
    --screenshot "$out/clip.png" > "$out/clip.log" 2>&1
fi
if run --headless --frames 1 --model "$install/sample/does-not-exist.effigy" \
    > "$out/bad-input.log" 2>&1; then
    echo 'Missing model unexpectedly succeeded' >&2
    exit 1
fi
grep -qi 'Model file not found' "$out/bad-input.log" || { echo 'Missing-model diagnostic absent' >&2; exit 1; }

if [[ "$gpu_available" -eq 1 ]]; then
python3 - "$out" <<'PY'
import hashlib
import pathlib
import struct
import sys
import zlib

root = pathlib.Path(sys.argv[1])
def pixels(path):
    raw = path.read_bytes()
    if raw[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError(f'{path}: not a PNG')
    pos, data, width, height, channels = 8, bytearray(), 0, 0, 0
    while pos < len(raw):
        size = struct.unpack_from('>I', raw, pos)[0]
        name = raw[pos + 4:pos + 8]
        chunk = raw[pos + 8:pos + 8 + size]
        crc = struct.unpack_from('>I', raw, pos + 8 + size)[0]
        if zlib.crc32(name + chunk) != crc:
            raise ValueError(f'{path}: bad PNG CRC')
        pos += size + 12
        if name == b'IHDR':
            width, height, depth, color, _, _, interlace = struct.unpack('>IIBBBBB', chunk)
            channels = {2: 3, 6: 4}.get(color, 0)
            if (width, height) != (128, 96) or depth != 8 or not channels or interlace:
                raise ValueError(f'{path}: unexpected PNG format/dimensions')
        elif name == b'IDAT':
            data.extend(chunk)
        elif name == b'IEND':
            break
    scanlines = zlib.decompress(data)
    stride = width * channels
    if len(scanlines) != (stride + 1) * height:
        raise ValueError(f'{path}: truncated pixels')
    previous = bytearray(stride)
    result = bytearray()
    for y in range(height):
        offset = y * (stride + 1)
        mode = scanlines[offset]
        row = bytearray(scanlines[offset + 1:offset + 1 + stride])
        for x in range(stride):
            a = row[x - channels] if x >= channels else 0
            b = previous[x]
            c = previous[x - channels] if x >= channels else 0
            if mode == 1: row[x] = (row[x] + a) & 255
            elif mode == 2: row[x] = (row[x] + b) & 255
            elif mode == 3: row[x] = (row[x] + ((a + b) // 2)) & 255
            elif mode == 4:
                p = a + b - c
                distances = (abs(p-a), abs(p-b), abs(p-c))
                row[x] = (row[x] + (a, b, c)[distances.index(min(distances))]) & 255
            elif mode != 0: raise ValueError(f'{path}: invalid PNG filter {mode}')
        result.extend(row)
        previous = row
    if len(set(result)) < 2:
        raise ValueError(f'{path}: uniform/invalid image')
    return bytes(result)

first, second, clip = (pixels(root / name) for name in ('render-1.png', 'render-2.png', 'clip.png'))
if first != second:
    raise ValueError('Repeated installed-artifact captures differ')
(root / 'pixels.sha256').write_text(hashlib.sha256(first).hexdigest() + '  render pixels\n')
print('PASS installed artifact: 128x96, decoded nonuniform pixels, deterministic repeat, clip, bad input')
PY
echo 'PASS installed artifact (full: CPU-only steps + GPU render)'
else
echo 'PASS installed artifact (CPU-only; GPU acceptance BLOCKED — see gpu-blocked.log)'
fi
echo "Evidence: $out"
