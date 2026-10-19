#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Geppetto Desktop — Linux x64 Standalone Packaging Script (Ticket D2)
#
# Produces a self-contained, reproducible Linux x64 distribution bundle:
# - Self-contained .NET 8 application runtime and binaries
# - Native Silk.NET/SDL2/GLFW shared libraries
# - Launcher script (run.sh) configuring clean environment and library paths
# - Bundled owned synthetic sample project (.effigy + .riganim + project manifest)
# - Distribution README, capability matrix, and dependency/license notices
# - Build manifest (build-info.json) with SDK version, timestamp, target arch
# - Compressed .tar.gz archive and companion .sha256 checksum
# - Strict exclusion of developer detritus, source files, and machine-specific paths
# ==============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Output directory defaults to isolated staging directory /tmp/opencode/geppetto-dist
TARGET_DIR="${1:-/tmp/opencode/geppetto-dist}"

echo "=== Geppetto Desktop Standalone Packaging ==="
echo "Project directory: ${PROJECT_DIR}"
echo "Target directory:  ${TARGET_DIR}"

# Ensure output directory exists (do NOT delete arbitrary caller paths)
mkdir -p "${TARGET_DIR}"
TARGET_DIR="$(cd "${TARGET_DIR}" && pwd)"

# Create an isolated temporary staging root to avoid picking up previous build files
STAGE_DIR="$(mktemp -d /tmp/opencode/geppetto-stage.XXXXXX)"
BUILD_ROOT="$(mktemp -d /tmp/opencode/geppetto-package-build.XXXXXX)"
trap 'rm -rf "${STAGE_DIR}" "${BUILD_ROOT}"' EXIT

echo "1. Publishing self-contained net8.0 application (linux-x64)..."
dotnet publish "${PROJECT_DIR}/Marionette.Desktop.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --artifacts-path "${BUILD_ROOT}" \
    -o "${STAGE_DIR}"

# Ensure permissions on published native executable
chmod +x "${STAGE_DIR}/marionette-desktop"

echo "2. Generating launcher script (run.sh)..."
cat << 'EOF' > "${STAGE_DIR}/run.sh"
#!/usr/bin/env bash
set -euo pipefail

# Geppetto Desktop Standalone Linux Launcher
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Configure library search paths for self-contained runtime and bundled SDL/GLFW
export LD_LIBRARY_PATH="${SCRIPT_DIR}:${SCRIPT_DIR}/runtimes/linux-x64/native:${LD_LIBRARY_PATH:-}"

# Prevent conflicting host .NET SDK or runtime environments
unset DOTNET_ROOT || true
unset DOTNET_MULTILEVEL_LOOKUP || true

BIN_PATH="${SCRIPT_DIR}/marionette-desktop"
if [[ ! -f "${BIN_PATH}" ]]; then
    echo "Error: marionette-desktop executable not found in ${SCRIPT_DIR}" >&2
    exit 1
fi

if [[ ! -x "${BIN_PATH}" ]]; then
    chmod +x "${BIN_PATH}" 2>/dev/null || true
fi

# Execute self-contained executable directly (does not require dotnet SDK on PATH)
exec "${BIN_PATH}" "$@"
EOF
chmod +x "${STAGE_DIR}/run.sh"

echo "3. Creating bundled owned sample project..."
mkdir -p "${STAGE_DIR}/sample"
cp "${SCRIPT_DIR}/sample-rig.fbx" "${STAGE_DIR}/sample/sample-rig.fbx"

# 3a. Synthetic .effigy model fixture
cat << 'EOF' > "${STAGE_DIR}/sample/sample.effigy"
effigy 1
rollback 2147483647
feature PrimitiveFeature
	id 00000001
	name SampleCube
	suppressed 0
	visible 1
	param Divisions 4
	param InnerRadius 0.3
	param Material 0
	param Position 0 0 0
	param Radius 0.5
	param RotationAngle 0
	param RotationAxis 0 0 1
	param Scale 1 1 1
	param Segments 16
	param Shape 0
	param SizeX 10
	param SizeY 10
	param SizeZ 10
	param UniformScale 0
end
EOF

# 3b. Synthetic .riganim clip fixture
cat << 'EOF' > "${STAGE_DIR}/sample/sample.riganim"
{
  "ReferenceProps": [],
  "Objects": [],
  "Lights": [],
  "Cameras": [],
  "Events": [],
  "MorphEvents": [],
  "Marks": [],
  "SourceModel": "sample-rig.fbx",
  "RigAsset": "",
  "AnimationSpeed": 30,
  "FrameCount": 31,
  "BoneTracks": [
    {
      "BoneName": "root",
      "Target": "Bone",
      "Keyframes": [
        {
          "Frame": 0,
          "Local": {
            "Position": "0,0,0",
            "Rotation": "0,0,0,1",
            "Scale": "1,1,1"
          },
          "Interpolation": "Linear"
        },
        {
          "Frame": 30,
          "Local": {
            "Position": "0,0,10",
            "Rotation": "0,0,0,1",
            "Scale": "1,1,1"
          },
          "Interpolation": "Smooth"
        }
      ]
    }
  ]
}
EOF

# 3c. Bundled DesktopProject manifest (P2 compliant, portable relative paths)
cat << 'EOF' > "${STAGE_DIR}/sample/sample.geppetto-project.json"
{
  "schemaVersion": 1,
  "id": "geppetto_sample_project",
  "name": "Geppetto Standalone Sample",
  "sourceDocuments": [
    {
      "id": "doc_sample_cube",
      "asset": {
        "id": "asset_sample_cube",
        "path": "sample.effigy"
      },
      "format": "effigy"
    },
    {
      "id": "doc_sample_rig",
      "asset": {
        "id": "asset_sample_rig",
        "path": "sample-rig.fbx"
      },
      "format": "fbx"
    }
  ],
  "clips": [
    {
      "id": "clip_sample_anim",
      "asset": {
        "id": "asset_sample_anim",
        "path": "sample.riganim"
      },
      "targetRigId": "doc_sample_rig",
      "frameRate": 30.0,
      "name": "Sample Hover Animation"
    }
  ],
  "assemblyParts": [
    {
      "id": "part_cube",
      "sourceDocumentId": "doc_sample_cube",
      "name": "Sample Cube Body"
    },
    {
      "id": "part_rig",
      "sourceDocumentId": "doc_sample_rig",
      "name": "Sample Two-Bone Rig"
    }
  ],
  "materialOverrides": []
}
EOF

cat << 'EOF' > "${STAGE_DIR}/sample/README.md"
# Bundled Sample Project

This directory contains a standalone, project-owned synthetic sample project for Geppetto Desktop:
- `sample.geppetto-project.json`: Project manifest referencing relative portable assets.
- `sample.effigy`: Parametric 3D solid primitive model authored in the Effigy kernel.
- `sample-rig.fbx`: Owned synthetic two-bone skinned mesh for clip playback.
- `sample.riganim`: Standard keyframe animation clip targeting `sample-rig.fbx`.

To launch Geppetto Desktop with this sample model:
```sh
./run.sh --model sample/sample.effigy
```

For headless validation:
```sh
./run.sh --headless --frames 1 --size 512x512 --screenshot output.png --model sample/sample.effigy

# To exercise the skeletal clip:
./run.sh --headless --frames 1 --size 512x512 --time 0.5 --model sample/sample-rig.fbx --clip sample/sample.riganim --screenshot clip.png
```
EOF

echo "4. Adding distribution documentation and notices..."
cat << 'EOF' > "${STAGE_DIR}/README.md"
# Geppetto Desktop (Standalone Linux Distribution)

Geppetto Desktop is a standalone 3D animation, parametric modeling, and assembly editor.
This distribution bundle contains a self-contained runtime requiring no external .NET SDK.

## System Requirements
- Linux x64 (glibc 2.28 or later)
- Hardware OpenGL 4.6 Core Profile
- Hardware EGL 1.5+ driver (for offscreen / headless GPU execution)
- X11 or Wayland display server (for interactive desktop windowing)

## Running Geppetto Desktop

To launch interactive desktop mode:
```sh
./run.sh --model sample/sample.effigy
```

To run headless verification or batch rendering without an active X11/Wayland display:
```sh
env -u DISPLAY -u WAYLAND_DISPLAY ./run.sh --headless --frames 1 --size 512x512 --screenshot screenshot.png --model sample/sample.effigy
```

To view CLI command line options:
```sh
./run.sh --help
```
EOF

cat << 'EOF' > "${STAGE_DIR}/NOTICES.md"
# Geppetto Desktop — Third-Party Notices and Licenses

This package includes third-party software components licensed under permissive open-source licenses:

1. Silk.NET Suite (Windowing, OpenGL, Input)
   - Copyright (c) Dylan Perks and Silk.NET contributors
   - License: MIT License
   - https://github.com/dotnet/Silk.NET

2. SDL2 (Simple DirectMedia Layer) via Ultz.Native.SDL
   - Copyright (c) Sam Lantinga and SDL contributors
   - License: zlib License
   - https://www.libsdl.org/

3. GLFW via Ultz.Native.GLFW
   - Copyright (c) Marcus Geelnard and Camilla Löwy
   - License: zlib/libpng License
   - https://www.glfw.org/

4. .NET Runtime & Base Class Libraries
   - Copyright (c) .NET Foundation and Contributors
   - License: MIT License
   - https://github.com/dotnet/runtime

5. Effigy Kernel & Marionette Desktop
   - Copyright (c) Geppetto Project Contributors
   - License: Project-Owned Internal Software
EOF

cat << 'EOF' > "${STAGE_DIR}/CAPABILITIES.md"
# Geppetto Desktop — Capabilities Matrix

- Target Platform: Linux x64 (glibc 2.28+)
- Graphics Pipeline: OpenGL 4.6 Core Profile
- Headless Backend: Hardware EGL device/pbuffer (no display required)
- Windowing Backend: SDL2 (Silk.NET windowing)
- 3D Geometry: Effigy parametric modeling (.effigy), FBX 2014-2020 import/export
- Animation: RigAnim keyframe tracks (.riganim) with bone posing and timeline events
- Project System: Portable DesktopProject manifests (.geppetto-project.json)
- Runtime Independence: 100% self-contained; zero dependency on .NET SDK or host development tools
EOF

echo "5. Generating build-info.json manifest..."
SDK_VERSION="$(dotnet --version 2>/dev/null || echo "unknown")"
BUILD_TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
GIT_COMMIT="$(git -C "${PROJECT_DIR}" rev-parse HEAD 2>/dev/null || echo "unspecified")"

cat << EOF > "${STAGE_DIR}/build-info.json"
{
  "application": "marionette-desktop",
  "version": "1.0.0",
  "buildTimestamp": "${BUILD_TIMESTAMP}",
  "timestamp": "${BUILD_TIMESTAMP}",
  "createdUtc": "${BUILD_TIMESTAMP}",
  "targetArchitecture": "linux-x64",
  "architecture": "x64",
  "runtime": "linux-x64",
  "targetFramework": "net8.0",
  "sdkVersion": "${SDK_VERSION}",
  "gitCommit": "${GIT_COMMIT}"
}
EOF

echo "6. Scrubbing developer detritus, source files, and caches..."
rm -rf "${STAGE_DIR}/.git" "${STAGE_DIR}/obj" "${STAGE_DIR}/bin"
find "${STAGE_DIR}" -type f \( -name "*.cs" -o -name "*.csproj" -o -name "*.sln" -o -name "*~" -o -name "*.tmp" \) -delete 2>/dev/null || true

echo "7. Creating compressed archive and SHA256 checksum..."
ARCHIVE_NAME="geppetto-desktop-linux-x64.tar.gz"
TMP_TAR_DIR="$(mktemp -d /tmp/opencode/geppetto-tar.XXXXXX)"
tar --exclude="${ARCHIVE_NAME}" --exclude="${ARCHIVE_NAME}.sha256" -czf "${TMP_TAR_DIR}/${ARCHIVE_NAME}" -C "${STAGE_DIR}" .
mv "${TMP_TAR_DIR}/${ARCHIVE_NAME}" "${STAGE_DIR}/${ARCHIVE_NAME}"
rm -rf "${TMP_TAR_DIR}"

(cd "${STAGE_DIR}" && sha256sum "${ARCHIVE_NAME}" > "${ARCHIVE_NAME}.sha256")

echo "8. Staging files into target directory..."
cp -a "${STAGE_DIR}/." "${TARGET_DIR}/"

# Also place companion archive in parent of target directory if distinct
TARGET_PARENT="$(dirname "${TARGET_DIR}")"
if [[ "${TARGET_PARENT}" != "${TARGET_DIR}" && -w "${TARGET_PARENT}" ]]; then
    cp -a "${STAGE_DIR}/${ARCHIVE_NAME}" "${TARGET_PARENT}/${ARCHIVE_NAME}" 2>/dev/null || true
    cp -a "${STAGE_DIR}/${ARCHIVE_NAME}.sha256" "${TARGET_PARENT}/${ARCHIVE_NAME}.sha256" 2>/dev/null || true
fi

echo "=== Packaging Complete ==="
echo "Staged directory: ${TARGET_DIR}"
echo "Archive:          ${TARGET_DIR}/${ARCHIVE_NAME}"
echo "SHA256:           $(cat "${TARGET_DIR}/${ARCHIVE_NAME}.sha256")"
