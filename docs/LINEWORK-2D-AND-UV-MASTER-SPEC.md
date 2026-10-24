# Linework 2D DCC, 2D Animation, UV Texture Painting & 2D-to-3D Master Specification

> [!IMPORTANT]
> **Domain**: Native 2D Drawing Engine (`~/games/projects/linework`), Multi-Channel PBR Texture Authoring, 2D Skeletal Animation (Spine 2D / DragonBones style), 3D UV Painting, and 2D-to-3D Geometric Inflation (Monster Mash / Teddy / ShadowBox).
> **Target Applications**:
> - `~/games/projects/linework` (Qt6 C++ native 2D drawing app)
> - `~/games/projects/geppetto/Effigy/` & `~/Desktop/geppetto-desktop/` (C# 3D modeling, UV painting & XPBD cloth engine)
> - `sbox-dev` (Source 2 engine for 2D/2.5D sprite sheets, 3D models, materials, and viewmodels)

---

## 1. Ground-Truth Audit of `Linework` Today (`~/games/projects/linework/src/`)

| File in `~/games/projects/linework/src/` | Current State | What It Needs to Become a Pro 2D/2.5D DCC |
|---|---|---|
| [`model.h`](file:///home/poo/games/projects/linework/src/model.h#L21-L58) / `model.cpp` | `Stroke` stores raw `QVector<double> pts` `(x, y, pressure)` with basic `tool` strings (`pen`, `pencil`, `ink`, `marker`, `airbrush`, `fill`, `shape`, `text`, `image`). `Layer` has only `visible`, `locked`, `ref`, `opacity`. | • **Cubic Bézier & Tangent Handles**: Vector curve editing mode with node types (`Corner`, `Smooth`, `Symmetric`).<br>• **Multi-Channel PBR Layers**: Add `channel` (`Color`, `Height/Normal`, `Roughness`, `Metallic`, `Emission`, `Alpha`, `Cutout`, `Stitch`).<br>• **Seamless Wrap-Around Mode**: Canvas-modulo coordinate wrapping (`wrapTile=true`) for seamless tiling textures and trim sheets.<br>• **2D Bone Rig & Mesh**: `Bone2D` tree and `Mesh2D` Delaunay vertex weights for Spine-style skeletal animation. |
| [`canvas.h`](file:///home/poo/games/projects/linework/src/canvas.h) / `canvas.cpp` | Interactive drawing canvas with basic pan/zoom, brush drawing, and selection. | • **Stroke Stabilizer**: 2nd-order critically damped spring-mass stabilizer (Lazynezumi style) for silky-smooth inking.<br>• **Radial & Mirror Symmetry**: $N$-fold kaleidoscope and bilateral symmetry lines.<br>• **3D UV Map Wireframe Overlay**: Load 3D model UV layouts and paint directly onto 2D texture islands with live sync to Geppetto Desktop! |
| [`shapes.h`](file:///home/poo/games/projects/linework/src/shapes.h) / `shapes.cpp` | Simple geometric primitives (`rectPts`, `ellipsePts`, `roundRectPts`, `arcPts`). | • **Vector Booleans via Clipper2**: Path `Union`, `Subtract`, `Intersect`, `XOR`, and `Offset` (dilation/inset).<br>• **2D Sewing Pattern Primitives**: Dart cutouts, seam notches, and seam-matching tags for Marvelous Designer-style tailoring. |
| [`store.h`](file:///home/poo/games/projects/linework/src/store.h) / `store.cpp` | Saves `.linework` JSON, `.jpg` thumbnails, and reference images to `~/.local/share/linework/drawings/`. | • **Headless CLI Exporter**: Export PNG texture channels, SVG vector outlines, and JSON contours.<br>• **Live IPC Sync**: Write `~/.local/share/linework/last_saved.json` on save to trigger instant hot-reload in Geppetto Desktop. |

---

## 2. Pillar I — Next-Gen 2D Vector Tools, Stabilizers & Clipper2 Booleans

```
   Raw Mouse/Tablet Input
             |
             v
+--------------------------+
|  Spring-Mass Stabilizer  |  --> Filters hand jitter & micro-shakes
+--------------------------+
             |
             v
+--------------------------+
|  Cubic Bézier Fitting    |  --> Schneider curve fitting (G1/G2 continuity)
+--------------------------+
             |
             v
+--------------------------+
|  Clipper2 Vector CSG     |  --> 64-bit integer exact Union / Difference / Inset
+--------------------------+
```

### 2.1 2nd-Order Spring-Mass Inking Stabilizer (`linework/src/stabilizer.h`)
Replaces raw mouse/stylus jitter with critically damped physical lag:

$$\mathbf{a}(t) = \frac{k}{m} \big(\mathbf{p}_{\text{stylus}}(t) - \mathbf{p}_{\text{pen}}(t)\big) - 2\sqrt{\frac{k}{m}} \, \mathbf{v}_{\text{pen}}(t)$$

```cpp
// File: ~/games/projects/linework/src/stabilizer.h
#pragma once
#include <QPointF>
#include <cmath>

class StrokeStabilizer {
public:
    double stiffness = 180.0;
    double damping = 24.0;
    QPointF penPos;
    QPointF penVel;
    bool active = false;

    void reset(QPointF p) {
        penPos = p;
        penVel = QPointF(0, 0);
        active = true;
    }

    QPointF step(QPointF target, double dt) {
        if (!active) { reset(target); return target; }
        dt = std::clamp(dt, 0.001, 0.033);
        QPointF disp = target - penPos;
        QPointF springForce = disp * stiffness;
        QPointF dampingForce = penVel * damping;
        QPointF accel = springForce - dampingForce;

        penVel += accel * dt;
        penPos += penVel * dt;
        return penPos;
    }
};
```

### 2.2 Vector Boolean CSG & Path Offsetting via Clipper2 (`linework/src/vector_bool.cpp`)
Integrates the header-only C++ Clipper2 engine (`clipper2/clipper.h`) directly into Linework for vector path operations:

```cpp
#include "model.h"
#include <clipper2/clipper.h>

using namespace Clipper2Lib;

// Scale float coordinates to 64-bit integers for exact robust CSG
const double CLIPPER_SCALE = 1000.0;

Paths64 ToClipperPaths(const QVector<double> &pts) {
    Path64 path;
    int n = pts.size() / 3;
    for (int i = 0; i < n; ++i) {
        path.push_back(Point64(pts[i * 3] * CLIPPER_SCALE, pts[i * 3 + 1] * CLIPPER_SCALE));
    }
    Paths64 result;
    result.push_back(path);
    return result;
}

QVector<double> FromClipperPaths(const Paths64 &paths) {
    QVector<double> pts;
    if (paths.empty() || paths[0].empty()) return pts;
    for (const auto &pt : paths[0]) {
        pts.push_back(static_cast<double>(pt.x) / CLIPPER_SCALE);
        pts.push_back(static_cast<double>(pt.y) / CLIPPER_SCALE);
        pts.push_back(1.0); // full pressure
    }
    return pts;
}

SP BooleanPaths(SP strokeA, SP strokeB, ClipType op) {
    Paths64 subj = ToClipperPaths(strokeA->pts);
    Paths64 clip = ToClipperPaths(strokeB->pts);
    Paths64 solution = BooleanOp(op, FillRule::NonZero, subj, clip);

    SP out = std::make_shared<Stroke>(*strokeA);
    out->id = uid();
    out->pts = FromClipperPaths(solution);
    out->closed = true;
    return prepare(out);
}
```

---

## 3. Pillar II — 2D Skeletal Animation & Delaunay Mesh Warping (Spine 2D Style)

To turn `Linework` into a full 2D character animation and cutout tool:

```
        2D Vector Art / Sprite
                  |
                  v
    +---------------------------+
    |  Constrained Delaunay     |  --> Generates triangulated 2D mesh cage
    +---------------------------+
                  |
                  v
    +---------------------------+
    |  2D Bone Hierarchy (FK/IK)|  --> Root -> Hip -> Spine -> Head / Limbs
    +---------------------------+
                  |
                  v
    +---------------------------+
    |  Linear/Dual-Quat Skinning|  --> Deforms 2D vertices smoothly along bones
    +---------------------------+
```

### 3.1 2D Bone & Mesh Model (`linework/src/anim2d.h`)

```cpp
// File: ~/games/projects/linework/src/anim2d.h
#pragma once
#include <QString>
#include <QVector>
#include <QPointF>
#include <cmath>

struct Bone2D {
    QString name;
    int parent = -1;
    QPointF restPos;
    double restAngle = 0; // radians
    double length = 50.0; // pixels / units

    // Animated state
    QPointF pos;
    double angle = 0;
    QPointF worldPos;
    double worldAngle = 0;
};

struct SkinWeight2D {
    int boneIndex;
    double weight;
};

struct Vertex2D {
    QPointF restPos;
    QPointF pos;
    QPointF uv;
    QVector<SkinWeight2D> weights; // max 4 weights summing to 1.0
};

struct Mesh2D {
    QVector<Vertex2D> vertices;
    QVector<int> indices; // Triangle indices (triples)

    void deform(const QVector<Bone2D> &bones) {
        for (auto &v : vertices) {
            QPointF blended(0, 0);
            for (const auto &w : v.weights) {
                if (w.boneIndex < 0 || w.boneIndex >= bones.size()) continue;
                const auto &b = bones[w.boneIndex];
                // Transform restPos relative to bone rest frame into current bone world frame
                double cosA = std::cos(b.worldAngle - b.restAngle);
                double sinA = std::sin(b.worldAngle - b.restAngle);
                QPointF local = v.restPos - b.restPos;
                QPointF rotated(local.x() * cosA - local.y() * sinA, local.x() * sinA + local.y() * cosA);
                blended += (b.worldPos + rotated) * w.weight;
            }
            v.pos = blended;
        }
    }
};
```

### 3.2 2D Analytical 2-Bone IK Solver (`anim2d.cpp`)
Given Hip/Shoulder $\mathbf{p}_{\text{root}}$, Effector Target $\mathbf{p}_{\text{target}}$, upper length $L_1$, and lower length $L_2$:

$$\cos(\theta_2) = \frac{\|\mathbf{p}_{\text{target}} - \mathbf{p}_{\text{root}}\|^2 - L_1^2 - L_2^2}{2 L_1 L_2}$$
$$\theta_1 = \operatorname{atan2}(y_{\text{tgt}} - y_{\text{root}}, x_{\text{tgt}} - x_{\text{root}}) \pm \operatorname{acos}\left(\frac{\|\mathbf{p}_{\text{target}} - \mathbf{p}_{\text{root}}\|^2 + L_1^2 - L_2^2}{2 L_1 \|\mathbf{p}_{\text{target}} - \mathbf{p}_{\text{root}}\|}\right)$$

---

## 4. Pillar III — 3D UV Texture Painting & Seamless Wrap-Around Mode

### 4.1 Direct 3D UV Wireframe Overlay Painting in Linework
1. **Load UV Wireframe**:
   - Add `Layer::uvWireframe = true` and `QVector<QPointF> uvLines` to `Layer` in `model.h`.
   - When an artist imports a 3D model into Linework (`linework --paint-model <path.vmdl|path.obj>`), Linework parses the UV layout, scales UV coords $[0, 1] \times [0, 1]$ to canvas bounds $[0, W] \times [0, H]$, and renders the faint blue mesh wireframe on a locked reference layer.
2. **Live Viewport Inter-Process Sync (`~/.local/share/linework/ipc.sock` / `last_saved.json`)**:
   - Whenever Linework saves or commits a stroke, it writes `<id>_color.png`, `<id>_normal.png`, etc. into the model's texture folder and updates `last_saved.json`.
   - Geppetto Desktop’s `DependencyWatcher.cs` catches the filesystem change and immediately re-uploads the texture to the OpenGL `RetainedBody`, letting the artist draw in 2D Linework while watching the 3D model update live in Geppetto Desktop beside it!

### 4.2 Seamless Tileable Canvas Mode (`wrapTile=true`)
When `Doc::wrapTile = true` is enabled (for trim sheets, tiling bricks, fabrics, and terrain textures):
- Every point $(x, y)$ in stroke drawing and rendering wraps modulo canvas size:

$$x_{\text{wrapped}} = x \pmod W, \quad y_{\text{wrapped}} = y \pmod H$$

- When drawing a stroke from $(x_1, y_1)$ to $(x_2, y_2)$ that crosses the canvas border, Linework splits the stroke segment at the border and automatically draws the continuing segment wrapped on the opposite edge!

---

## 5. Pillar IV — 2D-to-3D Geometric Modeling Pipeline

### 5.1 Monster-Mash / Teddy Chordal Axis 2D-to-3D Inflation (`Effigy/Inflate2DFeature.cs`)
Converts closed 2D Linework vector drawings into smooth 3D volumetric solids:

```
  Closed 2D Linework Contour
             |
             v
+--------------------------+
| Constrained Triangulation|
+--------------------------+
             |
             v
+--------------------------+
| Distance Transform /     |  --> d(x, y) = distance to nearest boundary
| Chordal Axis Skeleton    |
+--------------------------+
             |
             v
+--------------------------+
| Spherical Dome Z-Profile |  --> z(x, y) = sqrt(max(0, R^2 - (R - d(x,y))^2))
+--------------------------+
             |
             v
+--------------------------+
| 3D Watertight Solid Mesh |  --> Front dome + Back mirrored dome welded at rim
+--------------------------+
```

```csharp
// File: Effigy/Features/Inflate2DFeature.cs
#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

public static class Inflate2DMeshBuilder
{
	/// <summary>
	/// Inflates a closed 2D polygon contour into a smooth 3D manifold solid (front + back dome).
	/// </summary>
	public static PolyMesh Inflate( IReadOnlyList<Vec2> contour, float maxDepth, int internalSubdivisions )
	{
		var mesh = new PolyMesh();
		// 1. Constrained Delaunay Triangulation of interior
		var triangles = Triangulate2D.TriangulatePolygonWithHoles( contour );

		// 2. Compute geodesic distance field to boundary edges for every internal vertex
		var distToBorder = ComputeBoundaryDistances( triangles, contour );

		// 3. Extrude front and back vertices using circular cross-section profile
		int numVerts = triangles.Positions.Count;
		var frontMap = new int[numVerts];
		var backMap = new int[numVerts];

		for ( int i = 0; i < numVerts; i++ )
		{
			var p2 = triangles.Positions[i];
			float d = distToBorder[i];
			// Hemispherical elevation profile: z = sqrt(d * (2R - d))
			float z = MathF.Sqrt( MathF.Max( 0f, d * (2f * maxDepth - d) ) );
			z = MathF.Min( z, maxDepth );

			frontMap[i] = mesh.AddVertex( new Vec3( p2.x, p2.y, z ) );
			backMap[i] = mesh.AddVertex( new Vec3( p2.x, p2.y, -z ) );
		}

		// 4. Add front faces (standard winding) and back faces (reversed winding)
		foreach ( var tri in triangles.Faces )
		{
			mesh.AddFace( new[] { frontMap[tri.a], frontMap[tri.b], frontMap[tri.c] } );
			mesh.AddFace( new[] { backMap[tri.a], backMap[tri.c], backMap[tri.b] } );
		}

		return mesh;
	}

	static float[] ComputeBoundaryDistances( TriangulationResult tri, IReadOnlyList<Vec2> border )
	{
		var dists = new float[tri.Positions.Count];
		for ( int i = 0; i < tri.Positions.Count; i++ )
		{
			float minDist = float.MaxValue;
			var p = tri.Positions[i];
			for ( int j = 0; j < border.Count; j++ )
			{
				var a = border[j];
				var b = border[(j + 1) % border.Count];
				minDist = MathF.Min( minDist, DistanceToSegment( p, a, b ) );
			}
			dists[i] = minDist;
		}
		return dists;
	}

	static float DistanceToSegment( Vec2 p, Vec2 a, Vec2 b )
	{
		var ab = b - a;
		float l2 = ab.LengthSquared;
		if ( l2 < 1e-12f ) return (p - a).Length;
		float t = Math.Clamp( Vec2.Dot( p - a, ab ) / l2, 0f, 1f );
		return (p - (a + ab * t)).Length;
	}
}
```

### 5.2 ZBrush `ShadowBox` 3-View Silhouette Orthogonal CSG (`Effigy/ShadowBoxFeature.cs`)
Draw three 2D Linework drawings:
- `front.linework` (projected down $+Y$, extruded along $Y$)
- `side.linework` (projected down $+X$, extruded along $X$)
- `top.linework` (projected down $+Z$, extruded along $Z$)

Using `ExactManifoldCSG` (`RESEARCH-CAD-MESH-TOPOLOGY.md`), evaluate:

$$\operatorname{Solid} = \operatorname{Extrude}(Front, Y) \;\cap\; \operatorname{Extrude}(Side, X) \;\cap\; \operatorname{Extrude}(Top, Z)$$

This allows hard-surface guns, props, and mechanical machinery to be blocked out in 30 seconds by sketching three 2D orthographic silhouettes!

---

## 6. Summary of New Commands & Integration Points

| Application | Tool / Command | Function |
|---|---|---|
| **Linework** | `linework --export-png <doc> <out.png> --channel color\|height\|rough` | Headless PBR channel texture baking |
| **Linework** | `linework --paint-model <model.vmdl>` | Loads 3D model UV wireframe into Linework with live IPC sync |
| **Linework** | `Doc::wrapTile = true` | Seamless canvas wrap-around mode for trim sheets & tiling fabrics |
| **Linework** | `BooleanPaths(A, B, op)` | Exact Clipper2 vector CSG (Union, Subtract, Intersect, Offset) |
| **Effigy** | `add Inflate2D file=drawing.linework depth=1.5` | Inflates 2D closed linework contours into smooth 3D solids (Teddy / Monster Mash) |
| **Effigy** | `add ShadowBox front=f.linework side=s.linework top=t.linework` | 3-view orthogonal silhouette intersection into a clean 3D solid |
| **Effigy** | `add Garment pattern=drawing.linework stitch=...` | 2D vector sewing patterns mapped directly to 3D XPBD cloth simulation |
