# Geppetto Desktop — Standalone Distribution Inventory

## 1. Executive Summary

This document provides a comprehensive inventory of all managed packages, native host libraries, licenses, build-time vs. runtime boundaries, system compatibility requirements, and path portability guarantees for **Geppetto Desktop**.

Geppetto Desktop is a standalone 3D animation, posing, and assembly application. It runs on **Linux x64** with an OpenGL 4.6 core profile. Headless rendering uses hardware `libEGL.so.1` (device/pbuffer), while interactive rendering uses Silk.NET's SDL2 backend.

---

## 2. Dependency Matrix

### 2.1 Managed NuGet Packages

All managed packages target `.NET 8.0` (`net8.0`).

| Package | Version | License | Direct/Transitive | Purpose |
|---|---|---|---|---|
| `Silk.NET.OpenGL` | 2.23.0 | MIT | Direct | OpenGL 4.6 core bindings and function loader |
| `Silk.NET.Windowing` | 2.23.0 | MIT | Direct | Abstract windowing abstraction |
| `Silk.NET.Windowing.Sdl` | 2.23.0 | MIT | Direct | SDL2 windowing implementation |
| `Silk.NET.Input` | 2.23.0 | MIT | Direct | Abstract input system (keyboard, mouse) |
| `Silk.NET.Input.Sdl` | 2.23.0 | MIT | Direct | SDL2 input implementation |
| `Silk.NET.Core` | 2.23.0 | MIT | Transitive | Native interop, string handling, loader primitives |
| `Silk.NET.Maths` | 2.23.0 | MIT | Transitive | Mathematical primitives for Silk abstractions |
| `Silk.NET.SDL` | 2.23.0 | MIT | Transitive | Raw SDL2 native bindings |
| `Silk.NET.GLFW` | 2.23.0 | MIT | Transitive | Fallback desktop windowing bindings |
| `Silk.NET.Windowing.Glfw` | 2.23.0 | MIT | Transitive | GLFW windowing implementation |
| `Silk.NET.Input.Glfw` | 2.23.0 | MIT | Transitive | GLFW input implementation |
| `Silk.NET.Input.Common` | 2.23.0 | MIT | Transitive | Common input abstractions |
| `Silk.NET.Windowing.Common` | 2.23.0 | MIT | Transitive | Common windowing abstractions |
| `Ultz.Native.SDL` | 2.28.5 | zlib | Transitive | Bundled native SDL2 shared library binaries |
| `Ultz.Native.GLFW` | 3.3.9.1 | zlib/libpng | Transitive | Bundled native GLFW shared library binaries |

### 2.2 Base Class Library (BCL) and Runtime Assemblies

| Assembly | Version | License | Category | Purpose |
|---|---|---|---|---|
| `System.Text.Json` | 8.0.x | MIT | BCL Component | Manifest serialization, RigAnim parser, JSON round-tripping |
| `System.IO.Pipelines` | 8.0.x | MIT | BCL Component | High-performance buffered IO streaming |
| `System.Text.Encodings.Web` | 8.0.x | MIT | BCL Component | Text encoding utilities for JSON |
| `Microsoft.Extensions.DependencyModel` | 8.0.x | MIT | BCL Component | Assembly and runtime dependency reflection |
| `Microsoft.DotNet.PlatformAbstractions` | 3.1.x | MIT | BCL Component | OS platform detection abstractions |

### 2.3 Kernel Assembly (`Effigy.Kernel`)

| Component | Assembly | License | Origin | Build vs. Runtime |
|---|---|---|---|---|
| `Effigy.Kernel` | `Effigy.Kernel.dll` | Project-owned | `../../geppetto/Effigy/**/*.cs` | Build-time compilation into standalone deterministic assembly. Zero external runtime dependencies. |

---

## 3. Native Host & Library Requirements

The runtime requires a modern Linux environment. Native libraries fall into two categories: **Bundled with Application** and **Host System Provided**.

### 3.1 Native Library Roster

| Library | Soname | Provider | Required At Runtime | Purpose | Host Package / Remediation |
|---|---|---|---|---|---|
| EGL 1.5+ | `libEGL.so.1` | Host System | **Yes** (Headless GPU) | Headless device query and pbuffer context creation | `libegl1`, `mesa-libEGL`, or NVIDIA proprietary driver |
| OpenGL 4.6 | `libGL.so.1` | Host System | **Yes** (GL Context) | GL driver dispatch and graphics execution | `libgl1`, `mesa-libGL`, or GPU vendor driver |
| SDL2 | `libSDL2-2.0.so` | Bundled (`runtimes/linux-x64/native/`) | **Yes** (Interactive Mode) | Window management, input handling, swapchain | Bundled via `Ultz.Native.SDL`; fallback: `libsdl2-2.0-0` |
| GLFW (fallback) | `libglfw.so.3` | Bundled (`runtimes/linux-x64/native/`) | Optional | Secondary desktop windowing backend | Bundled via `Ultz.Native.GLFW`; fallback: `libglfw3` |
| C Runtime | `libc.so.6`, `libm.so.6`, `libpthread.so.0` | Host System | **Yes** (Base OS) | Standard C library (glibc 2.28+) | Standard Linux distribution base |

### 3.2 Graphics Driver and Headless Constraints
- **Hardware EGL required**: Headless execution (`--headless`) uses `HeadlessGlContext`, which probes `eglQueryDevicesEXT` and creates an offscreen pbuffer context via `EGL_PLATFORM_DEVICE_EXT`.
- **Software emulation explicitly rejected**: If only `EGL_MESA_device_software` is present, `HeadlessGlContext` rejects it to ensure tests and rendering reflect genuine hardware capabilities.
- **Never bundle GPU drivers**: Proprietary vendor libraries (such as `libEGL_nvidia.so.0` or Mesa DRI drivers) must not be bundled into the package; they must match the host kernel driver.

---

## 4. Build-Time vs. Runtime Requirements

A strict boundary is maintained between build prerequisites and runtime requirements:

```
[ BUILD-TIME ONLY ]
  ├── .NET 8.0 SDK (dotnet CLI, Roslyn compiler, MSBuild)
  ├── NuGet Package Sources (api.nuget.org or local package cache)
  └── Sibling Source Repository (../../geppetto/Effigy/**/*.cs)
           │
           ▼ (dotnet build / dotnet publish)
[ RUNTIME BUNDLE ]
  ├── Self-contained net8.0 runtime (coreclr, hostfxr, BCL assemblies)
  ├── Effigy.Kernel.dll (pure managed CIL, zero source dependency)
  ├── marionette-desktop.dll & launcher executable
  ├── runtimes/linux-x64/native/ (libSDL2-2.0.so, libglfw.so.3)
  └── packaging notices & sample assets
           │
           ▼ (relies only on)
[ HOST SYSTEM ]
  ├── Linux x64 Kernel (5.x+) & Glibc (2.28+)
  ├── Hardware GPU Driver (OpenGL 4.6 Core)
  └── libEGL.so.1
```

- **Runtime Independence from SDK**: The deployed application is self-contained. The `.NET SDK` (`dotnet`), `MSBuild`, and compiler toolchains are **not** needed on the target host.
- **Runtime Independence from Source Tree**: `Effigy.Kernel.dll` is compiled deterministically into CIL. At runtime, the application does not inspect, read, or require `../../geppetto/Effigy` or any parent repository.

---

## 5. License & Redistribution Audit

All runtime components and third-party dependencies have been audited for intellectual property and licensing compliance.

### 5.1 License Audit Table

| Component / Library | License | SPDX ID | Redistribution Allowed | Notes |
|---|---|---|---|---|
| `Silk.NET.*` (Windowing, OpenGL, Input) | MIT License | `MIT` | **Yes** | Permissive open source license |
| `Ultz.Native.SDL` (`SDL2`) | zlib License | `Zlib` | **Yes** | Permissive; permits binary redistribution |
| `Ultz.Native.GLFW` (`GLFW`) | zlib/libpng License | `Zlib` | **Yes** | Permissive; permits binary redistribution |
| .NET 8 BCL (`System.*`, `Microsoft.*`) | MIT License | `MIT` | **Yes** | Standard .NET runtime license |
| `Effigy.Kernel` | Project-Owned | Proprietary / Internal | **Yes** (Owner) | Internal engine-free kernel |
| `Marionette.Desktop` | Project-Owned | Proprietary / Internal | **Yes** (Owner) | Geppetto Desktop desktop application |

### 5.2 Proprietary Asset Restrictions
- **Zero Facepunch / s&box Assets**: No proprietary `.vmdl`, `.vmat`, `.vpcf`, citizen models, or s&box engine assets may be bundled or required.
- **Zero Developer Project Assets**: Private project assets (such as `deep_sentance/Assets/models/player/player_suit.fbx`) are strictly excluded from distribution.
- **Sample Assets Policy**: All sample models, animation clips, and test fixtures included with distribution packages must be procedurally generated or authored specifically for the Geppetto open distribution under project ownership.

---

## 6. Portability and Path Audit

### 6.1 Developer Path Risks Identified

During audit of codebase options and configuration:
- `DesktopOptions.cs` defaults `Model` to:
  `"/home/poo/games/projects/deep_sentance/Assets/models/player/player_suit.fbx"`
  This is a developer-specific machine path. In a standalone distribution, opening with default options without `--model` or `--project` would fail on an end-user machine.
- **Audit Requirement**: `DistributionInventory.AuditOptionsPortability` and `DistributionInventory.AuditPathForPortability` detect and flag machine-specific prefixes (`/home/`, `/Users/`, Windows drive roots).

### 6.2 Relative Project Path Architecture (P1 / P2 Contracts)

Geppetto Desktop achieves path independence through:
1. **`AssetPathResolver` (P1)**:
   - All authored project assets use relative paths (`models/mesh.effigy`, `clips/walk.riganim`).
   - Project-relative paths use `/` as the portable directory separator.
   - Symlinks escaping the project root are rejected.
   - Case-sensitivity is validated deterministically on Linux hosts.
2. **`DesktopProject` (P2)**:
   - Project manifests (`.geppetto-project.json`) contain strictly relative `AssetReference` objects.
   - Manifests can be relocated to arbitrary directories on any host machine via `DesktopProject.Relocate` or `DesktopProject.Load`.
   - Ephemeral cache paths and GL handles are excluded from persistent manifests.

---

## 7. Supported System Compatibility Matrix

| Environment | Supported Status | Notes |
|---|---|---|
| **Linux x64 (glibc 2.28+)** | **Fully Supported** | Primary deployment target |
| **Hardware OpenGL 4.6** | **Required** | Requires dedicated or modern integrated GPU driver |
| **Hardware EGL 1.5+** | **Required for Headless** | Required for CLI headless rendering and automated validation |
| **X11 / Wayland** | **Required for Interactive** | Managed by bundled SDL2 when running without `--headless` |
| **Linux ARM64** | Untested / Future | Planned for subsequent distribution phase |
| **Windows x64** | Unsupported in D1 | Requires separate WGL/DirectX backend verification |
| **macOS** | Unsupported in D1 | Requires Metal/MoltenVK or separate OpenGL compatibility layer |
| **Software Mesa EGL** | Intentionally Blocked | Headless runner intentionally rejects software rasterization |
