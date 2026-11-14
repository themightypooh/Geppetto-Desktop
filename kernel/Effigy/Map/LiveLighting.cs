using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// Hammer Desktop P2: live lighting preview evaluation engine (no compile).
/// Board task hammer-preview-live-lighting.
///
/// WHAT THIS IS. The data a future viewport/AI caller renders: given faces + lights,
/// compute per-face luxel grids (HDR, linear) + per-vertex colours — direct light plus a
/// cheap bounce approximation — fast enough to re-evaluate when a light moves, with a
/// Draft/Full quality switch and invalidation keyed on changed lights/faces. No viewport
/// or panel code lives here; no compile step, no VRAD, no external tool.
///
/// DEPENDENCY STATUS (2026-11-13): all three dependencies had NOT landed when this was
/// written — hammer-lighting-environment-sun-fog is still todo (no light entity types
/// exist in the tree), hammer-lightmap-progressive-bake is still todo (no
/// LightmapBake.cs), and hammer-face-material-uv-model is in-progress but its
/// MapDocument/MapBrush types do not exist on disk yet. So this file does NOT reference
/// any of them. It evaluates over the minimal input types defined here
/// (<see cref="LiveFace"/>, <see cref="LiveLight"/>), which are exactly the shape the
/// filed specs describe: a face is a polygon + per-face material (FaceUv lightmap scale,
/// nodraw) and a light is light / light_spot / light_environment / env_sun with
/// colour/intensity/range/cone. When the owners land:
///   - MapDocument adapter: faces come from the document's brushes (one LiveFace per
///     brush face, Material = its FaceMaterial); this file's public API does not change.
///   - Lighting-entities adapter: light/light_spot/light_environment/env_sun entities
///     convert to LiveLight via the factories below; this file's public API does not change.
///   - Progressive-bake adapter: pass the bake's per-face average luxels as
///     <c>bakedAveragePerFace</c> and Full uses them as the bounce source; compare a
///     preview against a bake with <see cref="LiveLighting.CompareToBake"/>.
/// Do not invent a second lighting evaluator against those types; adapt this one.
///
/// UNITS AND CONVENTIONS. Kernel inches, Z-up. Colours are LINEAR RGB (the kernel's
/// shader convention: lighting in linear, tone-map/encode once at the end); values are
/// HDR and may exceed 1. Light Direction is the direction light TRAVELS: a sun straight
/// overhead has Direction (0,0,-1); a spot's Direction is its cone axis. Point/spot
/// falloff is 1 / (1 + (d/64)^2) — half the intensity at 64 units — a preview
/// approximation, NOT VRAD's inverse-square law; the numbers are for comparing faces
/// and tracking light moves in-editor, not for shipping a bake.
/// </summary>
public enum LiveLightingQuality
{
	/// <summary>Direct light + environment ambient only. Coarse luxel grid (max 8 per axis).</summary>
	Draft,
	/// <summary>Direct + ambient + cheap bounce approximation. Finer luxel grid (max 32 per axis).</summary>
	Full,
}

/// <summary>The Source lighting entity kinds this engine evaluates, per the
/// hammer-lighting-environment-sun-fog spec: light (point), light_spot (cone),
/// light_environment (sun direction/colour/ambient, paired with env_sun) and env_sun
/// (the sun itself). <see cref="LiveLightKind.Sun"/> is the directional sun;
/// <see cref="LiveLightKind.Environment"/> carries the light_environment ambient term.</summary>
public enum LiveLightKind
{
	/// <summary>Source entity "light": omnidirectional point light.</summary>
	Point,
	/// <summary>Source entity "light_spot": cone light.</summary>
	Spot,
	/// <summary>Source entity "env_sun" / the sun half of "light_environment": directional.</summary>
	Sun,
	/// <summary>Source entity "light_environment": the ambient half (no position/direction).</summary>
	Environment,
}

/// <summary>
/// One light for the preview evaluator. Immutable; factories are named after the Source
/// entity classes so the lighting-entities adapter is a mechanical mapping.
/// Intensity is the irradiance a fully-facing surface receives at the light itself
/// (point/spot) or from the direction (sun/ambient); see the file header for falloff.
/// </summary>
public sealed class LiveLight : IEquatable<LiveLight>
{
	public int Id { get; }
	public LiveLightKind Kind { get; }
	/// <summary>Point/spot position. Ignored for Sun and Environment.</summary>
	public Vec3 Position { get; }
	/// <summary>Travel direction (unit). Sun: direction the sun shines; Spot: cone axis.
/// Ignored for Point and Environment.</summary>
	public Vec3 Direction { get; }
	/// <summary>Linear RGB colour, components >= 0.</summary>
	public Vec3 Color { get; }
	/// <summary>Irradiance scale, >= 0.</summary>
	public float Intensity { get; }
	/// <summary>Point/spot cutoff distance; 0 means no cutoff (infinite range).</summary>
	public float Range { get; }
	/// <summary>Spot inner cone half-angle in degrees; ignored for other kinds.</summary>
	public float InnerConeDegrees { get; }
	/// <summary>Spot outer cone half-angle in degrees; ignored for other kinds.</summary>
	public float OuterConeDegrees { get; }

	LiveLight( int id, LiveLightKind kind, Vec3 position, Vec3 direction, Vec3 color,
		float intensity, float range, float innerConeDegrees, float outerConeDegrees )
	{
		if ( intensity < 0f )
			throw new ArgumentException( $"Light {id} ({kind}): intensity {intensity} is negative." );
		if ( range < 0f )
			throw new ArgumentException( $"Light {id} ({kind}): range {range} is negative." );
		if ( color.x < 0f || color.y < 0f || color.z < 0f )
			throw new ArgumentException( $"Light {id} ({kind}): colour {color} has a negative component." );
		if ( kind == LiveLightKind.Spot )
		{
			if ( innerConeDegrees < 0f || innerConeDegrees > 180f || outerConeDegrees <= 0f || outerConeDegrees > 180f )
				throw new ArgumentException( $"Spot light {id}: cone angles inner {innerConeDegrees} / outer {outerConeDegrees} must be within 0..180 with outer > 0." );
			if ( innerConeDegrees >= outerConeDegrees )
				throw new ArgumentException( $"Spot light {id}: inner cone {innerConeDegrees} must be smaller than outer cone {outerConeDegrees}." );
			if ( direction.LengthSquared <= 1e-12f )
				throw new ArgumentException( $"Spot light {id}: direction is zero-length." );
		}
		if ( kind == LiveLightKind.Sun && direction.LengthSquared <= 1e-12f )
			throw new ArgumentException( $"Sun light {id}: direction is zero-length." );
		Id = id;
		Kind = kind;
		Position = position;
		Direction = direction.LengthSquared > 1e-12f ? direction.Normal : Vec3.Zero;
		Color = color;
		Intensity = intensity;
		Range = range;
		InnerConeDegrees = innerConeDegrees;
		OuterConeDegrees = outerConeDegrees;
	}

	/// <summary>Source entity "light": omnidirectional point light at <paramref name="position"/>.</summary>
	public static LiveLight Point( int id, Vec3 position, Vec3 color, float intensity, float range = 0f ) =>
		new( id, LiveLightKind.Point, position, Vec3.Zero, color, intensity, range, 0f, 0f );

	/// <summary>Source entity "light_spot": cone light; <paramref name="direction"/> is the cone axis (travel direction).</summary>
	public static LiveLight Spot( int id, Vec3 position, Vec3 direction, Vec3 color, float intensity,
		float innerConeDegrees, float outerConeDegrees, float range = 0f ) =>
		new( id, LiveLightKind.Spot, position, direction, color, intensity, range, innerConeDegrees, outerConeDegrees );

	/// <summary>Source entity "env_sun" / the sun half of "light_environment": directional light;
	/// <paramref name="direction"/> is the direction the sun shines (e.g. (0,0,-1) overhead).</summary>
	public static LiveLight Sun( int id, Vec3 direction, Vec3 color, float intensity ) =>
		new( id, LiveLightKind.Sun, Vec3.Zero, direction, color, intensity, 0f, 0f, 0f );

	/// <summary>Source entity "light_environment": the ambient half. No position or direction;
	/// contributes <c>color * intensity</c> to every face.</summary>
	public static LiveLight Environment( int id, Vec3 ambientColor, float intensity ) =>
		new( id, LiveLightKind.Environment, Vec3.Zero, Vec3.Zero, ambientColor, intensity, 0f, 0f, 0f );

	public bool Equals( LiveLight other ) =>
		other is not null && Id == other.Id && Kind == other.Kind && Position.Equals( other.Position ) &&
		Direction.Equals( other.Direction ) && Color.Equals( other.Color ) && Intensity == other.Intensity &&
		Range == other.Range && InnerConeDegrees == other.InnerConeDegrees && OuterConeDegrees == other.OuterConeDegrees;

	public override bool Equals( object obj ) => obj is LiveLight other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( HashCode.Combine( Id, Kind, Position, Direction, Color, Intensity ), Range, InnerConeDegrees );
	public override string ToString() => $"{Kind} {Id} colour {Color} intensity {Intensity:0.###}";
}

/// <summary>
/// One brush face as the preview evaluator sees it: a polygon (3+ vertices), its
/// per-face material (FaceUv lightmap scale + nodraw, from hammer-face-material-uv-model)
/// and a linear-RGB albedo (white by default). The normal is computed from the polygon
/// by Newell's method; a zero-area face has a zero normal and is rejected by
/// <see cref="LiveLighting.Evaluate"/>.
/// </summary>
public sealed class LiveFace
{
	public int Id { get; }
	public IReadOnlyList<Vec3> Vertices { get; }
	/// <summary>Never null; defaults to a plain "preview/default" material (NOT nodraw).</summary>
	public FaceMaterial Material { get; }
	/// <summary>Linear RGB albedo the face reflects; white by default.</summary>
	public Vec3 Albedo { get; }
	/// <summary>Unit normal from Newell's method, or Zero for a degenerate polygon.</summary>
	public Vec3 Normal { get; }
	public Vec3 Center { get; }
	/// <summary>World units per luxel, from the face's FaceUv (Hammer default 16).</summary>
	public float LightmapScale => Material.Uv.LightmapScale;
	/// <summary>True for tools/toolsnodraw faces: excluded from the baked mesh, kept in the brush.</summary>
	public bool IsNoDraw => Material.IsNoDraw;

	public LiveFace( int id, IReadOnlyList<Vec3> vertices )
		: this( id, vertices, FaceMaterial.ForMaterial( "preview/default" ), Vec3.One ) { }

	public LiveFace( int id, IReadOnlyList<Vec3> vertices, FaceMaterial material, Vec3 albedo )
	{
		if ( vertices is null ) throw new ArgumentNullException( nameof( vertices ) );
		if ( vertices.Count < 3 )
			throw new ArgumentException( $"Face {id}: needs at least 3 vertices, has {vertices.Count}." );
		if ( albedo.x < 0f || albedo.y < 0f || albedo.z < 0f )
			throw new ArgumentException( $"Face {id}: albedo {albedo} has a negative component." );
		Id = id;
		Vertices = vertices.ToArray();
		Material = material ?? throw new ArgumentNullException( nameof( material ) );
		Albedo = albedo;
		Normal = NewellNormal( Vertices );
		var c = Vec3.Zero;
		foreach ( var v in Vertices ) c += v;
		Center = c / Vertices.Count;
	}

	static Vec3 NewellNormal( IReadOnlyList<Vec3> verts )
	{
		var n = Vec3.Zero;
		for ( var i = 0; i < verts.Count; i++ )
		{
			var a = verts[i];
			var b = verts[(i + 1) % verts.Count];
			n += new Vec3(
				(a.y - b.y) * (a.z + b.z),
				(a.z - b.z) * (a.x + b.x),
				(a.x - b.x) * (a.y + b.y) );
		}
		return n.Normal;
	}
}

/// <summary>
/// The evaluated lighting of one face: a per-face luxel grid (HDR, linear, row-major)
/// plus one colour per vertex. A nodraw face is skipped: empty grid, zero vertex colours,
/// <see cref="SkippedNoDraw"/> true — it contributes nothing to the baked mesh but stays
/// in the brush, so the preview shows nothing for it rather than inventing light.
/// </summary>
public sealed class FaceLighting
{
	public int FaceId { get; }
	/// <summary>Luxel rows (along the face V axis). 0 for a skipped face.</summary>
	public int Rows { get; }
	/// <summary>Luxel columns (along the face U axis). 0 for a skipped face.</summary>
	public int Cols { get; }
	/// <summary>Nominal world units per luxel (the face's lightmap scale).</summary>
	public float LuxelSize { get; }
	/// <summary>Rows*Cols HDR linear luxels, row-major (row 0 is the smallest V).</summary>
	public IReadOnlyList<Vec3> Luxels { get; }
	/// <summary>One HDR linear colour per face vertex, same order as the face's vertices.</summary>
	public IReadOnlyList<Vec3> VertexColors { get; }
	public bool SkippedNoDraw { get; }
	/// <summary>Mean luxel colour; Zero for a skipped face.</summary>
	public Vec3 Average { get; }
	/// <summary>Component-wise maximum luxel colour; Zero for a skipped face.</summary>
	public Vec3 Max { get; }

	public FaceLighting( int faceId, int rows, int cols, float luxelSize,
		IReadOnlyList<Vec3> luxels, IReadOnlyList<Vec3> vertexColors, bool skippedNoDraw )
	{
		FaceId = faceId;
		Rows = rows;
		Cols = cols;
		LuxelSize = luxelSize;
		Luxels = luxels;
		VertexColors = vertexColors;
		SkippedNoDraw = skippedNoDraw;
		var sum = Vec3.Zero;
		var max = Vec3.Zero;
		foreach ( var l in luxels )
		{
			sum += l;
			max = new Vec3( MathF.Max( max.x, l.x ), MathF.Max( max.y, l.y ), MathF.Max( max.z, l.z ) );
		}
		Average = luxels.Count == 0 ? Vec3.Zero : sum / luxels.Count;
		Max = max;
	}

	/// <summary>Luxel (row, col), row-major. Throws for a skipped face or out-of-range indices.</summary>
	public Vec3 LuxelAt( int row, int col )
	{
		if ( row < 0 || row >= Rows || col < 0 || col >= Cols )
			throw new ArgumentOutOfRangeException( $"Face {FaceId}: no luxel at row {row}, col {col} (grid is {Rows}x{Cols})." );
		return Luxels[row * Cols + col];
	}

	/// <summary>Vertex colour for vertex <paramref name="index"/> (face vertex order).</summary>
	public Vec3 VertexColorAt( int index )
	{
		if ( index < 0 || index >= VertexColors.Count )
			throw new ArgumentOutOfRangeException( $"Face {FaceId}: no vertex {index} (has {VertexColors.Count})." );
		return VertexColors[index];
	}
}

/// <summary>One evaluation run: per-face lighting plus the key that invalidates it.</summary>
public sealed class LiveLightingResult
{
	public IReadOnlyList<FaceLighting> Faces { get; }
	public LiveLightingQuality Quality { get; }
	/// <summary>The invalidation key this result was computed under (see ComputeInvalidationKey).</summary>
	public string InvalidationKey { get; }
	/// <summary>Non-fatal notes, e.g. a sun with no light_environment ambient.</summary>
	public IReadOnlyList<string> Warnings { get; }
	public int LuxelCount { get; }
	/// <summary>Component-wise maximum luxel over all faces.</summary>
	public Vec3 MaxLuxel { get; }
	/// <summary>True when this result includes the bounce approximation (Full only).</summary>
	public bool HasBounce => Quality == LiveLightingQuality.Full;

	public LiveLightingResult( IReadOnlyList<FaceLighting> faces, LiveLightingQuality quality,
		string invalidationKey, IReadOnlyList<string> warnings )
	{
		Faces = faces;
		Quality = quality;
		InvalidationKey = invalidationKey;
		Warnings = warnings;
		var count = 0;
		var max = Vec3.Zero;
		foreach ( var f in faces )
		{
			count += f.Luxels.Count;
			max = new Vec3( MathF.Max( max.x, f.Max.x ), MathF.Max( max.y, f.Max.y ), MathF.Max( max.z, f.Max.z ) );
		}
		LuxelCount = count;
		MaxLuxel = max;
	}

	/// <summary>The evaluated face with this id, or null when the run had no such face.</summary>
	public FaceLighting Face( int id )
	{
		foreach ( var f in Faces )
			if ( f.FaceId == id ) return f;
		return null;
	}
}

/// <summary>One face's preview-vs-bake comparison (per-face average luxels).</summary>
public sealed class BakeFaceComparison
{
	public int FaceId { get; }
	public Vec3 PreviewAverage { get; }
	public Vec3 BakedAverage { get; }
	/// <summary>Max over components of |preview-baked| / max(|baked|, 0.01).</summary>
	public float RelativeDifference { get; }

	public BakeFaceComparison( int faceId, Vec3 previewAverage, Vec3 bakedAverage, float relativeDifference )
	{
		FaceId = faceId;
		PreviewAverage = previewAverage;
		BakedAverage = bakedAverage;
		RelativeDifference = relativeDifference;
	}
}

/// <summary>Preview-vs-bake comparison over every face both sides know about.</summary>
public sealed class BakeComparison
{
	public IReadOnlyList<BakeFaceComparison> Faces { get; }
	public float MaxRelativeDifference { get; }
	public float Tolerance { get; }
	public bool WithinTolerance => MaxRelativeDifference <= Tolerance;
	public int ComparedFaceCount => Faces.Count;

	public BakeComparison( IReadOnlyList<BakeFaceComparison> faces, float tolerance )
	{
		Faces = faces;
		Tolerance = tolerance;
		var max = 0f;
		foreach ( var f in faces )
			if ( f.RelativeDifference > max ) max = f.RelativeDifference;
		MaxRelativeDifference = max;
	}
}

/// <summary>
/// The live lighting preview evaluator. Pure function over faces + lights: no document
/// mutation, no IO, no GL, no UI. Deterministic — same faces + lights + quality +
/// baked averages give the same luxels, bit for bit (no clock, no randomness, no
/// hash-order dependence; faces are evaluated in input order, the key sorts by id).
///
/// LIGHT MODEL (preview approximation, documented in the file header):
///   direct  = point/spot/sun irradiance at the sample point, NdotL * colour * intensity
///             * falloff (point/spot: 1/(1+(d/64)^2), cut off past Range; spot: smooth
///             cone falloff between the inner and outer angles; sun: no falloff).
///   ambient = sum of light_environment colour * intensity, on every face, both qualities.
///   bounce  = Full only: BounceFactor * the face's baked average luxel where the
///             progressive bake has one, else AmbientBounceFactor * ambient colour
///             (grey 0.05 when there is no light_environment at all).
///   luxel   = (direct + ambient + bounce) * albedo, per component, HDR linear.
/// A face whose normal points away from a light gets nothing from that light; a face
/// with no lights at all gets zero (plus the "no lights" warning).
/// </summary>
public static class LiveLighting
{
	/// <summary>Falloff denominator: point/spot intensity halves at this distance (units).</summary>
	public const float FalloffHalfDistance = 64f;
	/// <summary>Full bounce: fraction of a face's baked average re-emitted as bounce.</summary>
	public const float BounceFactor = 0.35f;
	/// <summary>Full bounce without a bake: fraction of the ambient colour used as bounce.</summary>
	public const float AmbientBounceFactor = 0.2f;
	/// <summary>Ambient colour used when no light_environment exists (linear grey).</summary>
	public const float DefaultAmbient = 0.05f;
	/// <summary>Draft luxel grid cap per axis (coarse = fast re-evaluation on light moves).</summary>
	public const int DraftMaxGrid = 8;
	/// <summary>Full luxel grid cap per axis.</summary>
	public const int FullMaxGrid = 32;

	static Vec3 Mul( Vec3 a, Vec3 b ) => new( a.x * b.x, a.y * b.y, a.z * b.z );

	static float Smoothstep( float t )
	{
		if ( t <= 0f ) return 0f;
		if ( t >= 1f ) return 1f;
		return t * t * (3f - 2f * t);
	}

	/// <summary>
	/// Evaluate(document, lights, quality) — the spec's entry point, over the face list a
	/// MapDocument adapter supplies (one LiveFace per brush face) and the lights the
	/// lighting-entities adapter supplies. <paramref name="bakedAveragePerFace"/> is the
	/// progressive bake's per-face average luxel (face id -&gt; HDR linear average) where one
	/// exists; Full uses it as the bounce source, Draft ignores it. Null means no bake yet.
	/// Throws <see cref="ArgumentNullException"/> for null faces/lights, and
	/// <see cref="ArgumentException"/> naming the face for duplicate ids, zero-area faces
	/// (zero normal) or a non-positive lightmap scale.
	/// </summary>
	public static LiveLightingResult Evaluate( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
		LiveLightingQuality quality, IReadOnlyDictionary<int, Vec3> bakedAveragePerFace = null )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( lights is null ) throw new ArgumentNullException( nameof( lights ) );
		var seen = new HashSet<int>();
		foreach ( var face in faces )
		{
			if ( face is null ) throw new ArgumentException( "Evaluate: the face list contains a null face." );
			if ( !seen.Add( face.Id ) )
				throw new ArgumentException( $"Evaluate: duplicate face id {face.Id}." );
			if ( face.Normal.LengthSquared <= 1e-12f )
				throw new ArgumentException( $"Evaluate: face {face.Id} is zero-area (its normal is zero-length)." );
			if ( face.LightmapScale <= 0f )
				throw new ArgumentException( $"Evaluate: face {face.Id} has lightmap scale {face.LightmapScale}: it must be positive." );
		}
		foreach ( var light in lights )
			if ( light is null ) throw new ArgumentException( "Evaluate: the light list contains a null light." );

		var key = ComputeInvalidationKey( faces, lights, quality, bakedAveragePerFace );
		var warnings = new List<string>();
		if ( lights.Count == 0 )
			warnings.Add( "No lights: every face evaluates to black." );
		var hasSun = lights.Any( l => l.Kind == LiveLightKind.Sun );
		var hasEnvironment = lights.Any( l => l.Kind == LiveLightKind.Environment );
		if ( hasSun && !hasEnvironment )
			warnings.Add( "Sun light present with no light_environment: the sun has no ambient term (light_environment + env_sun pair is incomplete)." );

		var ambient = Vec3.Zero;
		foreach ( var light in lights )
			if ( light.Kind == LiveLightKind.Environment )
				ambient += light.Color * light.Intensity;

		var evaluated = new List<FaceLighting>( faces.Count );
		foreach ( var face in faces )
			evaluated.Add( EvaluateFace( face, lights, quality, ambient, bakedAveragePerFace ) );
		return new LiveLightingResult( evaluated, quality, key, warnings );
	}

	static FaceLighting EvaluateFace( LiveFace face, IReadOnlyList<LiveLight> lights,
		LiveLightingQuality quality, Vec3 ambient, IReadOnlyDictionary<int, Vec3> bakedAveragePerFace )
	{
		if ( face.IsNoDraw )
		{
			var zeros = new Vec3[face.Vertices.Count];
			return new FaceLighting( face.Id, 0, 0, face.LightmapScale,
				Array.Empty<Vec3>(), zeros, skippedNoDraw: true );
		}

		var cap = quality == LiveLightingQuality.Draft ? DraftMaxGrid : FullMaxGrid;
		var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
		var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( face.Vertices, face.Normal );
		var uExtent = uMax - uMin;
		var vExtent = vMax - vMin;
		var cols = Math.Clamp( (int)MathF.Ceiling( uExtent / face.LightmapScale ), 1, cap );
		var rows = Math.Clamp( (int)MathF.Ceiling( vExtent / face.LightmapScale ), 1, cap );
		var origin = face.Vertices[0];

		// Cheap bounce (Full only): the baked average where the progressive bake has one,
		// else a fraction of the ambient colour (grey when there is no environment light).
		var bounce = Vec3.Zero;
		if ( quality == LiveLightingQuality.Full )
		{
			if ( bakedAveragePerFace is not null && bakedAveragePerFace.TryGetValue( face.Id, out var baked ) )
				bounce = baked * BounceFactor;
			else
			{
				var bounceSource = ambient.LengthSquared > 1e-12f
					? ambient
					: new Vec3( DefaultAmbient, DefaultAmbient, DefaultAmbient );
				bounce = bounceSource * AmbientBounceFactor;
			}
		}

		var luxels = new Vec3[rows * cols];
		for ( var row = 0; row < rows; row++ )
		{
			for ( var col = 0; col < cols; col++ )
			{
				var u = uMin + (col + 0.5f) * uExtent / cols;
				var v = vMin + (row + 0.5f) * vExtent / rows;
				var p = origin + uAxis * u + vAxis * v;
				luxels[row * cols + col] = Shade( p, face.Normal, face.Albedo, lights, ambient, bounce );
			}
		}
		var vertexColors = new Vec3[face.Vertices.Count];
		for ( var i = 0; i < face.Vertices.Count; i++ )
			vertexColors[i] = Shade( face.Vertices[i], face.Normal, face.Albedo, lights, ambient, bounce );
		return new FaceLighting( face.Id, rows, cols, face.LightmapScale, luxels, vertexColors, skippedNoDraw: false );
	}

	static Vec3 Shade( Vec3 point, Vec3 normal, Vec3 albedo, IReadOnlyList<LiveLight> lights, Vec3 ambient, Vec3 bounce )
	{
		var irradiance = ambient + bounce;
		foreach ( var light in lights )
			irradiance += Direct( point, normal, light );
		return Mul( irradiance, albedo );
	}

	/// <summary>Direct irradiance at a point from one light (environment lights contribute 0 here — they are the ambient term).</summary>
	static Vec3 Direct( Vec3 point, Vec3 normal, LiveLight light )
	{
		switch ( light.Kind )
		{
			case LiveLightKind.Environment:
				return Vec3.Zero;
			case LiveLightKind.Sun:
			{
				var nDotL = MathF.Max( 0f, Vec3.Dot( normal, -light.Direction ) );
				return light.Color * (light.Intensity * nDotL);
			}
			case LiveLightKind.Point:
			case LiveLightKind.Spot:
			{
				var toLight = light.Position - point;
				var d = toLight.Length;
				if ( d <= 1e-6f ) return light.Color * light.Intensity;
				if ( light.Range > 0f && d > light.Range ) return Vec3.Zero;
				var dir = toLight / d;
				var nDotL = MathF.Max( 0f, Vec3.Dot( normal, dir ) );
				if ( nDotL <= 0f ) return Vec3.Zero;
				var falloff = 1f / (1f + (d * d) / (FalloffHalfDistance * FalloffHalfDistance));
				var amount = light.Intensity * nDotL * falloff;
				if ( light.Kind == LiveLightKind.Spot )
				{
					// dir points from the sample TOWARD the light; the cone opens along
					// Direction (light -> scene), so the cone angle uses -dir.
					var cosAngle = Vec3.Dot( -dir, light.Direction );
					var cosInner = MathF.Cos( light.InnerConeDegrees * MathF.PI / 180f );
					var cosOuter = MathF.Cos( light.OuterConeDegrees * MathF.PI / 180f );
					if ( cosAngle <= cosOuter ) return Vec3.Zero;
					if ( cosAngle < cosInner )
						amount *= Smoothstep( (cosAngle - cosOuter) / (cosInner - cosOuter) );
				}
				return light.Color * amount;
			}
			default:
				throw new ArgumentException( $"Unknown light kind: {light.Kind}." );
		}
	}

	/// <summary>
	/// The invalidation key: a stable FNV-1a hash (hex) over the quality, every face's id /
	/// vertices / material name / lightmap scale / albedo, every light's full parameters and
	/// the baked averages, all quantized to 0.001 and in id order — so the key changes exactly
	/// when something the evaluation depends on changes, and never for input reordering.
	/// A cached result is valid while its key equals the key of the current inputs.
	/// </summary>
	public static string ComputeInvalidationKey( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
		LiveLightingQuality quality, IReadOnlyDictionary<int, Vec3> bakedAveragePerFace = null )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( lights is null ) throw new ArgumentNullException( nameof( lights ) );
		var sb = new StringBuilder();
		sb.Append( "livelighting/v1 quality=" ).Append( (int)quality ).Append( ';' );
		foreach ( var face in faces.OrderBy( f => f.Id ) )
		{
			sb.Append( "face " ).Append( face.Id ).Append( ' ' ).Append( face.Material.MaterialName )
				.Append( " lm=" ).Append( Q( face.LightmapScale ) )
				.Append( " albedo=" ).Append( Q( face.Albedo ) );
			foreach ( var v in face.Vertices )
				sb.Append( ' ' ).Append( Q( v ) );
			sb.Append( ';' );
		}
		foreach ( var light in lights.OrderBy( l => l.Id ) )
		{
			sb.Append( "light " ).Append( light.Id ).Append( ' ' ).Append( light.Kind )
				.Append( " pos=" ).Append( Q( light.Position ) )
				.Append( " dir=" ).Append( Q( light.Direction ) )
				.Append( " col=" ).Append( Q( light.Color ) )
				.Append( " i=" ).Append( Q( light.Intensity ) )
				.Append( " r=" ).Append( Q( light.Range ) )
				.Append( " cone=" ).Append( Q( light.InnerConeDegrees ) ).Append( '/' ).Append( Q( light.OuterConeDegrees ) )
				.Append( ';' );
		}
		if ( bakedAveragePerFace is not null )
			foreach ( var pair in bakedAveragePerFace.OrderBy( p => p.Key ) )
				sb.Append( "baked " ).Append( pair.Key ).Append( '=' ).Append( Q( pair.Value ) ).Append( ';' );
		ulong hash = 14695981039346656037UL;
		foreach ( var b in Encoding.UTF8.GetBytes( sb.ToString() ) )
		{
			hash ^= b;
			hash *= 1099511628211UL;
		}
		return hash.ToString( "x16", CultureInfo.InvariantCulture );
	}

	static string Q( float v ) => MathF.Round( v, 3 ).ToString( "R", CultureInfo.InvariantCulture );
	static string Q( Vec3 v ) => Q( v.x ) + "," + Q( v.y ) + "," + Q( v.z );

	/// <summary>
	/// Compare a preview result against the progressive bake on a shared fixture: per-face
	/// average luxels, relative difference per face = max over components of
	/// |preview-baked| / max(|baked|, 0.01). Only faces present in both sides are compared;
	/// throws when no face is comparable. The tolerance is the caller's (the spec's shared
	/// fixture uses 0.05 for the analytic single-light case; a Full-vs-bake check with the
	/// bounce approximation in play needs a looser tolerance, e.g. 0.25).
	/// </summary>
	public static BakeComparison CompareToBake( LiveLightingResult result,
		IReadOnlyDictionary<int, Vec3> bakedAveragePerFace, float tolerance )
	{
		if ( result is null ) throw new ArgumentNullException( nameof( result ) );
		if ( bakedAveragePerFace is null ) throw new ArgumentNullException( nameof( bakedAveragePerFace ) );
		if ( tolerance < 0f )
			throw new ArgumentException( $"CompareToBake: tolerance {tolerance} is negative." );
		var compared = new List<BakeFaceComparison>();
		foreach ( var face in result.Faces )
		{
			if ( face.SkippedNoDraw ) continue;
			if ( !bakedAveragePerFace.TryGetValue( face.FaceId, out var baked ) ) continue;
			var worst = 0f;
			var preview = new[] { face.Average.x, face.Average.y, face.Average.z };
			var bakedArr = new[] { baked.x, baked.y, baked.z };
			for ( var i = 0; i < 3; i++ )
			{
				var rel = MathF.Abs( preview[i] - bakedArr[i] ) / MathF.Max( MathF.Abs( bakedArr[i] ), 0.01f );
				if ( rel > worst ) worst = rel;
			}
			compared.Add( new BakeFaceComparison( face.FaceId, face.Average, baked, worst ) );
		}
		if ( compared.Count == 0 )
			throw new ArgumentException( "CompareToBake: no evaluated face has a baked average to compare against." );
		return new BakeComparison( compared, tolerance );
	}
}

/// <summary>
/// A one-entry preview cache: re-evaluating with unchanged inputs (same invalidation key)
/// returns the previous result instead of recomputing, so a viewport that re-asks every
/// frame pays nothing while nothing moved. Single-threaded by design — the preview caller
/// owns the thread. <see cref="Invalidate"/> drops the entry (call it when the document
/// revision changes outside the face/light lists, e.g. a material edit the key cannot see).
/// </summary>
public sealed class LiveLightingCache
{
	string _key;
	LiveLightingResult _result;

	/// <summary>The key the cached result was computed under, or null when empty.</summary>
	public string CachedKey => _key;
	public bool HasCached => _result is not null;

	/// <summary>Evaluate, returning the cached result when the invalidation key matches.</summary>
	public LiveLightingResult Evaluate( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
		LiveLightingQuality quality, IReadOnlyDictionary<int, Vec3> bakedAveragePerFace = null )
	{
		var key = LiveLighting.ComputeInvalidationKey( faces, lights, quality, bakedAveragePerFace );
		if ( _result is not null && key == _key ) return _result;
		_result = LiveLighting.Evaluate( faces, lights, quality, bakedAveragePerFace );
		_key = key;
		return _result;
	}

	public void Invalidate()
	{
		_key = null;
		_result = null;
	}
}
