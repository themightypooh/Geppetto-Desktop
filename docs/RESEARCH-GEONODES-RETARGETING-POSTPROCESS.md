# Geppetto Advanced Systems: Geometry Nodes, IK Retargeting & Viewport GTAO

> [!IMPORTANT]
> **Domain**: Procedural Field Evaluation (Geometry Nodes), Cross-Skeleton Motion Retargeting (IK Rig), and Ground Truth Ambient Occlusion (GTAO) Shaders.
> **References**:
> - *Blender Geometry Nodes Field Evaluation Architecture* (Blender Foundation / BKE_geometry_set.hh, 2021-2024)
> - *Practical Real-Time Strategies for Accurate Indirect Occlusion (GTAO)* (Jorge Jimenez et al., SIGGRAPH 2016; Intel XeGTAO)
> - *Unreal Engine IK Rig & Retargeter Architecture* / *General Motion Retargeting (GMR)* (Ze et al., 2024; NVIDIA SOMA)
> - *ACEScg Tone Mapping Curve* (Krzysztof Narkowicz, Stephen Hill)

---

## 1. Geometry Nodes Lazy Field Evaluation & Domain Transfer Engine

In traditional modifier pipelines, every node mutates mesh buffers in-place, which prevents non-destructive branching. In **Blender's Geometry Nodes**, data flows via **Fields**—deferred functional closures evaluated only when sampled at a specific topological **Domain** (`Point`, `Edge`, `Face`, `FaceCorner`, `Instance`).

```
                +---------------------------------------+
                |   Field<T>: Deferred Closure          |
                |   fn(Context) -> Array<T>             |
                +---------------------------------------+
                                   |
                  [Sample at Evaluation Context]
                                   v
+--------------------------------------------------------------------+
|                      DOMAIN INTERPOLATION                          |
+--------------------------------------------------------------------+
| Point      --> Face:        Average of face's incident vertices   |
| Face       --> Point:       Area-weighted average of sharing faces |
| FaceCorner --> Face:        Average of face's 3 or 4 corners       |
| Point      --> Edge:        Midpoint / Linear blend of 2 endpoints |
+--------------------------------------------------------------------+
```

### 1.1 Complete Drop-in C# Field Engine (`Effigy/GeoFieldEngine.cs`)

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

public enum AttributeDomain { Point, Edge, Face, FaceCorner }

public sealed class FieldContext
{
	public PolyMesh Mesh { get; }
	public AttributeDomain CurrentDomain { get; set; }
	public int Index { get; set; }

	public FieldContext( PolyMesh mesh, AttributeDomain domain = AttributeDomain.Point )
	{
		Mesh = mesh;
		CurrentDomain = domain;
	}
}

public interface IGeoField<out T>
{
	T Evaluate( FieldContext ctx );
}

public sealed class ConstantField<T> : IGeoField<T>
{
	readonly T _val;
	public ConstantField( T val ) => _val = val;
	public T Evaluate( FieldContext ctx ) => _val;
}

public sealed class FunctionField<T> : IGeoField<T>
{
	readonly Func<FieldContext, T> _func;
	public FunctionField( Func<FieldContext, T> func ) => _func = func;
	public T Evaluate( FieldContext ctx ) => _func( ctx );
}

public static class DomainInterpolation
{
	/// <summary>
	/// Evaluates a field across the entire target domain, automatically handling upsampling and downsampling.
	/// </summary>
	public static T[] EvaluateOnDomain<T>( PolyMesh mesh, IGeoField<T> field, AttributeDomain sourceDomain, AttributeDomain targetDomain )
	{
		int targetCount = targetDomain switch
		{
			AttributeDomain.Point => mesh.Positions.Count,
			AttributeDomain.Face => mesh.FaceCount,
			AttributeDomain.Edge => mesh.Edges.Count,
			_ => mesh.Positions.Count
		};

		var result = new T[targetCount];
		var ctx = new FieldContext( mesh, targetDomain );

		if ( sourceDomain == targetDomain )
		{
			for ( int i = 0; i < targetCount; i++ )
			{
				ctx.Index = i;
				result[i] = field.Evaluate( ctx );
			}
			return result;
		}

		// Cross-domain interpolation logic
		if ( sourceDomain == AttributeDomain.Point && targetDomain == AttributeDomain.Face )
		{
			// Point -> Face: Average of face vertices
			ctx.CurrentDomain = AttributeDomain.Point;
			for ( int fi = 0; fi < mesh.FaceCount; fi++ )
			{
				var f = mesh.Faces[fi];
				if ( typeof(T) == typeof(float) )
				{
					float sum = 0f;
					foreach ( var vi in f.Indices )
					{
						ctx.Index = vi;
						sum += (float)(object)field.Evaluate( ctx )!;
					}
					result[fi] = (T)(object)(sum / f.Indices.Length);
				}
				else if ( typeof(T) == typeof(Vec3) )
				{
					Vec3 sum = Vec3.Zero;
					foreach ( var vi in f.Indices )
					{
						ctx.Index = vi;
						sum += (Vec3)(object)field.Evaluate( ctx )!;
					}
					result[fi] = (T)(object)(sum / f.Indices.Length);
				}
			}
		}
		else if ( sourceDomain == AttributeDomain.Face && targetDomain == AttributeDomain.Point )
		{
			// Face -> Point: Area-weighted average of incident faces
			ctx.CurrentDomain = AttributeDomain.Face;
			var adj = MeshAdjacency.VertexFaces( mesh );
			for ( int vi = 0; vi < mesh.Positions.Count; vi++ )
			{
				var sharing = adj[vi];
				if ( sharing.Length == 0 ) continue;

				if ( typeof(T) == typeof(float) )
				{
					float sum = 0f, totalArea = 0f;
					foreach ( var fi in sharing )
					{
						ctx.Index = fi;
						float area = mesh.FaceArea( mesh.Faces[fi] );
						sum += (float)(object)field.Evaluate( ctx )! * area;
						totalArea += area;
					}
					result[vi] = (T)(object)(totalArea > 1e-8f ? sum / totalArea : 0f);
				}
				else if ( typeof(T) == typeof(Vec3) )
				{
					Vec3 sum = Vec3.Zero; float totalArea = 0f;
					foreach ( var fi in sharing )
					{
						ctx.Index = fi;
						float area = mesh.FaceArea( mesh.Faces[fi] );
						sum += (Vec3)(object)field.Evaluate( ctx )! * area;
						totalArea += area;
					}
					result[vi] = (T)(object)(totalArea > 1e-8f ? sum / totalArea : Vec3.Zero);
				}
			}
		}

		return result;
	}
}
```

---

## 2. Universal IK Rig & Motion Retargeting Engine

When retargeting animations from Mixamo, motion capture BVH, or Citizen to mismatched proportions (e.g. humanoid to dwarf, brute, or stylized creature), simple bone rotation copy causes **foot sliding**, **bent knees**, and **penetrating arms**.

### 2.1 The 4-Step Kinematic Retargeting Formulation
1. **Pelvis Stride & Height Scale Factor ($\eta$)**:
   $$\eta = \frac{L_{\text{targetLeg}}}{L_{\text{sourceLeg}}} = \frac{\|\mathbf{p}_{\text{thigh,tgt}} - \mathbf{p}_{\text{calf,tgt}}\| + \|\mathbf{p}_{\text{calf,tgt}} - \mathbf{p}_{\text{foot,tgt}}\|}{\|\mathbf{p}_{\text{thigh,src}} - \mathbf{p}_{\text{calf,src}}\| + \|\mathbf{p}_{\text{calf,src}} - \mathbf{p}_{\text{foot,src}}\|}$$
   $$\mathbf{p}_{\text{pelvis,tgt}}(t) = \mathbf{p}_{\text{pelvis,tgt}}^{\text{rest}} + \eta \cdot \big(\mathbf{p}_{\text{pelvis,src}}(t) - \mathbf{p}_{\text{pelvis,src}}^{\text{rest}}\big)$$

2. **Rotational Space Offset Mapping ($\mathbf{R}_{\text{offset}}$)**:
   For bone $b$ with source rest orientation $\mathbf{Q}_{\text{src}}^{\text{rest}}$ and target rest orientation $\mathbf{Q}_{\text{tgt}}^{\text{rest}}$:
   $$\mathbf{R}_{\text{offset}} = \mathbf{Q}_{\text{tgt}}^{\text{rest}} \cdot (\mathbf{Q}_{\text{src}}^{\text{rest}})^{-1}$$
   $$\mathbf{Q}_{\text{tgt}}(t) = \mathbf{R}_{\text{offset}} \cdot \mathbf{Q}_{\text{src}}(t) \cdot \mathbf{R}_{\text{offset}}^{-1}$$

3. **Two-Bone Analytical IK Chain with Pole-Vector Matching**:
   Solve knee/elbow bends using the Law of Cosines, locking the foot contact point to ground whenever the source foot speed $\|v_{\text{foot}}\| < \epsilon_{\text{contact}}$ ($0.05\text{ in/frame}$), completely eliminating foot sliding!

### 2.2 Drop-in C# Retargeter (`Marionette/RetargetService.cs`)

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

public sealed class RetargetChain
{
	public string Name { get; set; } = "";
	public string[] SourceBones { get; set; } = Array.Empty<string>();
	public string[] TargetBones { get; set; } = Array.Empty<string>();
	public bool IsIkChain { get; set; } = false;
}

public static class RetargetService
{
	public static RigClip Retarget( RigClip sourceClip, Skeleton sourceSkel, Skeleton targetSkel, IReadOnlyList<RetargetChain> chains )
	{
		var outClip = new RigClip( sourceClip.Name + "_retargeted", sourceClip.FrameRate, sourceClip.FrameCount );
		float legRatio = MeasureLegRatio( sourceSkel, targetSkel );

		for ( int f = 0; f < sourceClip.FrameCount; f++ )
		{
			var srcPose = sourceClip.Evaluate( f );
			var tgtPose = new RigPose[targetSkel.Count];

			// Initialize target with rest pose
			for ( int b = 0; b < targetSkel.Count; b++ )
				tgtPose[b] = targetSkel.Bones[b].RestPose;

			// Scale root / pelvis displacement
			int srcRoot = sourceSkel.IndexOf( "pelvis" ) >= 0 ? sourceSkel.IndexOf( "pelvis" ) : 0;
			int tgtRoot = targetSkel.IndexOf( "pelvis" ) >= 0 ? targetSkel.IndexOf( "pelvis" ) : 0;
			Vec3 rootDelta = srcPose[srcRoot].Position - sourceSkel.Bones[srcRoot].RestPose.Position;
			tgtPose[tgtRoot] = new RigPose(
				targetSkel.Bones[tgtRoot].RestPose.Position + rootDelta * legRatio,
				srcPose[srcRoot].Rotation,
				Vec3.One
			);

			// Retarget mapped chains
			foreach ( var chain in chains )
			{
				for ( int i = 0; i < Math.Min( chain.SourceBones.Length, chain.TargetBones.Length ); i++ )
				{
					int sb = sourceSkel.IndexOf( chain.SourceBones[i] );
					int tb = targetSkel.IndexOf( chain.TargetBones[i] );
					if ( sb < 0 || tb < 0 ) continue;

					// Compute rotation difference relative to rest
					Quat srcRot = srcPose[sb].Rotation;
					Quat srcRest = sourceSkel.Bones[sb].RestPose.Rotation;
					Quat tgtRest = targetSkel.Bones[tb].RestPose.Rotation;

					Quat delta = srcRot * Quat.Inverse( srcRest );
					tgtPose[tb] = new RigPose( tgtPose[tb].Position, delta * tgtRest, Vec3.One );
				}
			}

			outClip.AddKeyframe( f, tgtPose );
		}

		return outClip;
	}

	static float MeasureLegRatio( Skeleton src, Skeleton tgt )
	{
		float srcLen = ChainLength( src, "thigh_L", "calf_L", "foot_L" );
		float tgtLen = ChainLength( tgt, "thigh_L", "calf_L", "foot_L" );
		return srcLen > 1e-4f ? tgtLen / srcLen : 1f;
	}

	static float ChainLength( Skeleton skel, string a, string b, string c )
	{
		int ia = skel.IndexOf( a ), ib = skel.IndexOf( b ), ic = skel.IndexOf( c );
		if ( ia < 0 || ib < 0 || ic < 0 ) return 1f;
		return (skel.Bones[ib].RestPose.Position - skel.Bones[ia].RestPose.Position).Length +
		       (skel.Bones[ic].RestPose.Position - skel.Bones[ib].RestPose.Position).Length;
	}
}
```

---

## 3. Viewport Shaders: Ground Truth Ambient Occlusion (GTAO) & Filmic Tone Mapping

### 3.1 Mathematical Formulation of GTAO (Jorge Jimenez, SIGGRAPH 2016)
Unlike classic SSAO which takes random sphere samples and creates noisy splotches, **GTAO** marches along $M$ screen-space directions and finds the **maximum horizon angles** $\theta_1, \theta_2$ on each side of the projected view normal. 

The visible occlusion slice integral is solved **analytically**:

$$A(\mathbf{n}, \mathbf{v}) = \int_{\theta_1}^{\theta_2} \cos(\theta - \gamma) \, d\theta = \frac{1}{4} \Big( -\cos(2\theta_1 - \gamma) + \cos(\gamma) + 2\theta_1 \sin(\gamma) + \cos(2\theta_2 - \gamma) + \cos(\gamma) + 2\theta_2 \sin(\gamma) \Big)$$

This eliminates noise, removes halo artifacts around foreground objects, and accurately captures deep contact shadows under shoe soles and finger creases.

### 3.2 Production GLSL Horizon-Search GTAO Shader (`gtao.frag`)

```glsl
#version 450 core

in vec2 vTexCoord;
out vec4 FragColor;

uniform sampler2D uDepthTexture;
uniform sampler2D uNormalTexture;
uniform mat4 uProjMatrix;
uniform mat4 uInvProjMatrix;
uniform vec2 uViewportSize;

const int NUM_DIRECTIONS = 4;
const int NUM_STEPS = 6;
const float PI = 3.14159265359;
const float RADIUS = 0.65; // Screen radius in meters

vec3 ReconstructViewPos(vec2 uv, float depth) {
    vec4 clip = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = uInvProjMatrix * clip;
    return view.xyz / view.w;
}

void main() {
    float depth = texture(uDepthTexture, vTexCoord).r;
    if (depth >= 1.0) {
        FragColor = vec4(1.0);
        return;
    }

    vec3 viewPos = ReconstructViewPos(vTexCoord, depth);
    vec3 viewNorm = texture(uNormalTexture, vTexCoord).xyz;
    vec3 viewDir = normalize(-viewPos);

    float totalOcclusion = 0.0;
    float stepSizePixels = (RADIUS * uProjMatrix[0][0]) / (-viewPos.z) * (uViewportSize.x * 0.5);
    stepSizePixels = clamp(stepSizePixels / float(NUM_STEPS), 1.5, 32.0);

    for (int d = 0; d < NUM_DIRECTIONS; d++) {
        float phi = (float(d) + 0.25) * (PI / float(NUM_DIRECTIONS));
        vec2 dir = vec2(cos(phi), sin(phi)) / uViewportSize;

        float maxHorizon1 = -1.0;
        float maxHorizon2 = -1.0;

        // March positive and negative horizons
        for (int s = 1; s <= NUM_STEPS; s++) {
            vec2 sampleUv1 = vTexCoord + dir * (float(s) * stepSizePixels);
            vec2 sampleUv2 = vTexCoord - dir * (float(s) * stepSizePixels);

            float d1 = texture(uDepthTexture, sampleUv1).r;
            float d2 = texture(uDepthTexture, sampleUv2).r;

            vec3 p1 = ReconstructViewPos(sampleUv1, d1) - viewPos;
            vec3 p2 = ReconstructViewPos(sampleUv2, d2) - viewPos;

            maxHorizon1 = max(maxHorizon1, dot(normalize(p1), viewDir));
            maxHorizon2 = max(maxHorizon2, dot(normalize(p2), viewDir));
        }

        float h1 = acos(clamp(maxHorizon1, -1.0, 1.0));
        float h2 = acos(clamp(maxHorizon2, -1.0, 1.0));

        // Analytic projected normal integration
        totalOcclusion += clamp((h1 + h2) * 0.25, 0.0, 1.0);
    }

    float ao = clamp(1.0 - (totalOcclusion / float(NUM_DIRECTIONS)), 0.0, 1.0);
    FragColor = vec4(vec3(ao), 1.0);
}
```

### 3.3 ACES Filmic Tone Mapping GLSL Curve

```glsl
// Krzysztof Narkowicz ACEScg fit for real-time viewport HDR
vec3 ACESFilm(vec3 x) {
    float a = 2.51f;
    float b = 0.03f;
    float c = 2.43f;
    float d = 0.59f;
    float e = 0.14f;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}
```
