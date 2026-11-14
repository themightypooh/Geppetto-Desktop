#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

namespace Effigy.Map;

/// <summary>
/// Block-tool primitives as plane brushes: box, wedge, cylinder, sphere, arch.
/// Each generator emits a convex <see cref="MapBrush"/> built from planes only — never from a
/// mesh primitive. Parameters mirror <see cref="PrimitiveFeature"/> (SizeX/SizeY/SizeZ lengths,
/// Radius, Segments, Divisions, Position, Scale, RotationAxis/RotationAngle, Material slot) so
/// CAD and map stay consistent: same numbers, same centre, same orientation.
///
/// Conventions (kernel inches, Z-up, solids centred on their own origin before Position):
///   Box     — axis-aligned, 6 faces. Same extents as <see cref="Primitives.Box"/>.
///   Wedge   — same orientation as <see cref="Primitives.Wedge"/>: full height at the -X side,
///             sloping down to zero height at the +X side; 5 faces; volume = SizeX*SizeY*SizeZ/2.
///   Cylinder— N-sided prism. Side faces are planes TANGENT to the analytic cylinder (not
///             chords through rim vertices), so the brush circumscribes the cylinder and its
///             volume is N*R^2*tan(pi/N)*H, converging to pi*R^2*H from above. The segment
///             count is respected exactly (no rounding to a multiple of 8, unlike the mesh
///             <see cref="Primitives.Cylinder"/>); faces = segments + 2.
///   Sphere  — faceted convex approximation: tangent planes at the poles plus tangent planes
///             on latitude rings. Every face plane touches the analytic sphere, so the brush
///             circumscribes it and its volume converges to 4/3*pi*R^3 from above.
///             Faces = 2 + (divisions - 1) * segments.
///   Arch    — solid sector of a cylinder (a half-cylinder at the 180-degree default), NOT a
///             hollow arch: a hollow arch (inner radius &gt; 0) is non-convex, so no single convex
///             brush can represent it — build one from several brushes instead (CSG ticket).
///             Sweep is centred on +X and must be in (0, 180] degrees; faces = segments + 4
///             (segments + 3 at exactly 180 degrees, where the two end caps coincide into one
///             diameter plane — emitting both would be a duplicate face).
///
/// Uses the landed plane brush core; integer slots map to material_N names.
/// </summary>
public static class MapBrushPrimitives
{
	/// <summary>Source default lightmap scale, used for every generated face.</summary>
	public const float DefaultLightmapScale = 16f;

	/// <summary>Axis-aligned box centred on the origin. 6 faces; exact volume SizeX*SizeY*SizeZ.</summary>
	public static MapBrush Box( float sizeX, float sizeY, float sizeZ,
		Vec3? position = null, Vec3? scale = null, Vec3? rotationAxis = null,
		float rotationAngleDegrees = 0f, int material = 0 )
	{
		RequirePositive( sizeX, nameof( sizeX ) );
		RequirePositive( sizeY, nameof( sizeY ) );
		RequirePositive( sizeZ, nameof( sizeZ ) );
		var hx = sizeX * 0.5f;
		var hy = sizeY * 0.5f;
		var hz = sizeZ * 0.5f;
		return Finish( Transform( new[]
		{
			Face( new Vec3( 1, 0, 0 ), hx, material ),
			Face( new Vec3( -1, 0, 0 ), hx, material ),
			Face( new Vec3( 0, 1, 0 ), hy, material ),
			Face( new Vec3( 0, -1, 0 ), hy, material ),
			Face( new Vec3( 0, 0, 1 ), hz, material ),
			Face( new Vec3( 0, 0, -1 ), hz, material ),
		}, position, scale, rotationAxis, rotationAngleDegrees ) );
	}

	/// <summary>
	/// Wedge centred on the origin, same orientation as <see cref="Primitives.Wedge"/>: full
	/// height at the -X side, sloping down to the base at the +X side. 5 faces; exact volume
	/// SizeX*SizeY*SizeZ/2.
	/// </summary>
	public static MapBrush Wedge( float sizeX, float sizeY, float sizeZ,
		Vec3? position = null, Vec3? scale = null, Vec3? rotationAxis = null,
		float rotationAngleDegrees = 0f, int material = 0 )
	{
		RequirePositive( sizeX, nameof( sizeX ) );
		RequirePositive( sizeY, nameof( sizeY ) );
		RequirePositive( sizeZ, nameof( sizeZ ) );
		var hx = sizeX * 0.5f;
		var hy = sizeY * 0.5f;
		var hz = sizeZ * 0.5f;
		// Slope plane: through (-hx, +hz) and (+hx, -hz) in the XZ plane, containing the Y
		// axis. hz*x + hx*z = 0, outward normal (hz, 0, hx) — interior is below the slope.
		var slope = new Vec3( hz, 0, hx ).Normal;
		return Finish( Transform( new[]
		{
			Face( new Vec3( 0, 0, -1 ), hz, material ), // base
			Face( new Vec3( -1, 0, 0 ), hx, material ), // back, full height at -X
			Face( slope, 0f, material ),                // slope
			Face( new Vec3( 0, -1, 0 ), hy, material ), // -Y end
			Face( new Vec3( 0, 1, 0 ), hy, material ),  // +Y end
		}, position, scale, rotationAxis, rotationAngleDegrees ) );
	}

	/// <summary>
	/// N-sided prism about Z, centred on the origin. Side faces are tangent planes, so the
	/// segment count is respected exactly and faces = segments + 2. Volume is
	/// segments*R^2*tan(pi/segments)*height (circumscribed; converges to pi*R^2*H).
	/// </summary>
	public static MapBrush Cylinder( float radius, float height, int segments = 16,
		Vec3? position = null, Vec3? scale = null, Vec3? rotationAxis = null,
		float rotationAngleDegrees = 0f, int material = 0 )
	{
		RequirePositive( radius, nameof( radius ) );
		RequirePositive( height, nameof( height ) );
		if ( segments < 3 || segments > 512 )
			throw new ArgumentOutOfRangeException( nameof( segments ), segments,
				"A brush cylinder needs at least 3 sides (PrimitiveFeature Segments range is 3..512)." );
		var hz = height * 0.5f;
		var faces = new List<Effigy.MapBrushFace>( segments + 2 );
		for ( var i = 0; i < segments; i++ )
		{
			var a = i / (float)segments * MathF.Tau;
			faces.Add( Face( new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 ), radius, material ) );
		}
		faces.Add( Face( new Vec3( 0, 0, 1 ), hz, material ) );
		faces.Add( Face( new Vec3( 0, 0, -1 ), hz, material ) );
		return Finish( Transform( faces, position, scale, rotationAxis, rotationAngleDegrees ) );
	}

	/// <summary>
	/// Faceted convex sphere approximation about the origin: tangent planes at the poles plus
	/// tangent planes on (divisions - 1) latitude rings of `segments` directions each.
	/// Faces = 2 + (divisions - 1) * segments. Volume converges to 4/3*pi*R^3 from above.
	/// `divisions` mirrors PrimitiveFeature's Sphere Divisions (default 4); `segments`
	/// mirrors the Cylinder Segments default (16). Divisions must be at least 2: with one
	/// band the brush would be just two parallel pole planes, i.e. an unbounded slab.
	/// </summary>
	public static MapBrush Sphere( float radius, int divisions = 4, int segments = 16,
		Vec3? position = null, Vec3? scale = null, Vec3? rotationAxis = null,
		float rotationAngleDegrees = 0f, int material = 0 )
	{
		RequirePositive( radius, nameof( radius ) );
		if ( divisions < 2 || divisions > 64 )
			throw new ArgumentOutOfRangeException( nameof( divisions ), divisions,
				"A brush sphere needs at least 2 latitude bands to close (PrimitiveFeature Divisions range is 1..64; 1 is a mesh-only value)." );
		if ( segments < 3 || segments > 512 )
			throw new ArgumentOutOfRangeException( nameof( segments ), segments,
				"A brush sphere needs at least 3 facets around (range 3..512)." );
		var faces = new List<Effigy.MapBrushFace>( 2 + ( divisions - 1 ) * segments );
		faces.Add( Face( new Vec3( 0, 0, 1 ), radius, material ) );
		faces.Add( Face( new Vec3( 0, 0, -1 ), radius, material ) );
		for ( var k = 1; k < divisions; k++ )
		{
			var lat = ( -90f + 180f * k / divisions ) * MathF.PI / 180f;
			var cosLat = MathF.Cos( lat );
			var sinLat = MathF.Sin( lat );
			for ( var j = 0; j < segments; j++ )
			{
				var az = j / (float)segments * MathF.Tau;
				faces.Add( Face( new Vec3( cosLat * MathF.Cos( az ), cosLat * MathF.Sin( az ), sinLat ),
					radius, material ) );
			}
		}
		return Finish( Transform( faces, position, scale, rotationAxis, rotationAngleDegrees ) );
	}

	/// <summary>
	/// Solid sector of a cylinder about Z — a half-cylinder at the 180-degree default — with
	/// the sweep centred on +X, extruded to +/-height/2. Outer faces are tangent planes at
	/// `segments` azimuths spread across the sweep (endpoints included); the ends are radial
	/// planes through the Z axis. Sweep must be in (0, 180]: beyond 180 degrees the sector
	/// is concave and no single convex brush can represent it. Faces = segments + 4, or
	/// segments + 3 at exactly 180 degrees where the two end caps are one diameter plane.
	/// Volume converges to (sweepRadians/2)*R^2*height from above.
	/// </summary>
	public static MapBrush Arch( float outerRadius, float height, float sweepDegrees = 180f, int segments = 8,
		Vec3? position = null, Vec3? scale = null, Vec3? rotationAxis = null,
		float rotationAngleDegrees = 0f, int material = 0 )
	{
		RequirePositive( outerRadius, nameof( outerRadius ) );
		RequirePositive( height, nameof( height ) );
		if ( !( sweepDegrees > 0f ) || sweepDegrees > 180f || !float.IsFinite( sweepDegrees ) )
			throw new ArgumentOutOfRangeException( nameof( sweepDegrees ), sweepDegrees,
				"An arch sweep must be in (0, 180] degrees: beyond 180 the sector is concave and cannot be one convex brush." );
		if ( segments < 2 || segments > 512 )
			throw new ArgumentOutOfRangeException( nameof( segments ), segments,
				"An arch needs at least 2 outer facets (range 2..512)." );
		var hz = height * 0.5f;
		var half = sweepDegrees * 0.5f * MathF.PI / 180f;
		var faces = new List<Effigy.MapBrushFace>( segments + 4 );
		for ( var j = 0; j < segments; j++ )
		{
			var a = segments == 1 ? 0f : -half + 2f * half * j / ( segments - 1 );
			faces.Add( Face( new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 ), outerRadius, material ) );
		}
		// Radial end caps through the Z axis. At azimuth +half the outward normal is
		// (-sin(half), cos(half), 0); at -half it is (-sin(half), -cos(half), 0); both
		// with distance 0. At exactly 180 degrees both are (-1, 0, 0): emit it once, or
		// the brush would carry a duplicate face.
		if ( sweepDegrees >= 180f )
		{
			faces.Add( Face( new Vec3( -1, 0, 0 ), 0f, material ) );
		}
		else
		{
			var s = MathF.Sin( half );
			var c = MathF.Cos( half );
			faces.Add( Face( new Vec3( -s, c, 0 ), 0f, material ) );
			faces.Add( Face( new Vec3( -s, -c, 0 ), 0f, material ) );
		}
		faces.Add( Face( new Vec3( 0, 0, 1 ), hz, material ) );
		faces.Add( Face( new Vec3( 0, 0, -1 ), hz, material ) );
		return Finish( Transform( faces, position, scale, rotationAxis, rotationAngleDegrees ) );
	}

	// ASSUMED: Effigy.MapBrushFace/MapPlane constructors and member names — see the class doc.
	static Effigy.MapBrushFace Face( Vec3 normal, float distance, int material )
	{
		if ( normal.LengthSquared < 1e-12f )
			throw new ArgumentException( "A brush face needs a non-zero normal.", nameof( normal ) );
		return new Effigy.MapBrushFace(normal.Normal, distance, "material_" + material);
	}

	// ASSUMED: MapBrush.FromPlanes / IsValid / Validate — see the class doc.
	static MapBrush Finish( IReadOnlyList<Effigy.MapBrushFace> faces )
	{
		var brush = MapBrush.FromPlanes( faces );
		if ( !brush.IsValid )
			throw new InvalidOperationException(
				$"MapBrushPrimitives built an invalid brush (this is a generator bug, not a caller error): {string.Join( "; ", brush.Validate() )}" );
		return brush;
	}

	/// <summary>
	/// Applies PrimitiveFeature's placement order — per-axis scale about the primitive's own
	/// origin, then rotation about an axis through the origin, then translation — to planes.
	/// For x' = M*x + t with M = R*S, a plane n.x = d becomes m.x' = d + m.t where
	/// m = M^-T*n = R*(n / scale), so the new unit normal is m/|m| and the new distance is
	/// (d + m.t)/|m|. Exact for any non-zero scale, not just uniform scale.
	/// </summary>
	static IReadOnlyList<Effigy.MapBrushFace> Transform( IReadOnlyList<Effigy.MapBrushFace> faces,
		Vec3? position, Vec3? scale, Vec3? rotationAxis, float rotationAngleDegrees )
	{
		var s = scale ?? Vec3.One;
		if ( s.x == 0f || s.y == 0f || s.z == 0f )
			throw new ArgumentOutOfRangeException( nameof( scale ), s,
				"Scale cannot be zero on any axis: it would flatten the solid to nothing." );
		var t = position ?? Vec3.Zero;
		// PrimitiveFeature's RotationAxis defaults to (0, 0, 1): an angle with no
		// explicit axis rotates about Z, exactly like the CAD primitive.
		var axisParam = rotationAxis ?? new Vec3( 0, 0, 1 );
		var rotate = rotationAngleDegrees != 0f && axisParam.LengthSquared > 1e-8f;
		if ( s.Equals( Vec3.One ) && !rotate && t.Equals( Vec3.Zero ) )
			return faces;
		var axis = rotate ? axisParam.Normal : Vec3.Zero;
		var angle = rotationAngleDegrees * MathF.PI / 180f;
		var result = new List<Effigy.MapBrushFace>( faces.Count );
		foreach ( var face in faces )
		{
			// ASSUMED: Effigy.MapBrushFace member names Plane/Material/UvOffset/UvScale/
			// UvRotationDegrees/LightmapScale/SmoothingGroup — see the class doc.
			var n = face.Normal;
			var m = new Vec3( n.x / s.x, n.y / s.y, n.z / s.z );
			if ( rotate )
				m = Rotate( m, axis, angle );
			var len = m.Length;
			var distance = ( face.Distance + Vec3.Dot( m, t ) ) / len;
			result.Add( new Effigy.MapBrushFace(m / len, distance, face.Material) );
		}
		return result;
	}

	static Vec3 Rotate( Vec3 v, Vec3 axis, float angle )
	{
		// Rodrigues' rotation formula; axis must be unit length.
		var cos = MathF.Cos( angle );
		var sin = MathF.Sin( angle );
		return v * cos + Vec3.Cross( axis, v ) * sin + axis * Vec3.Dot( axis, v ) * ( 1f - cos );
	}

	static void RequirePositive( float value, string name )
	{
		if ( !float.IsFinite( value ) || !( value > 0f ) )
			throw new ArgumentOutOfRangeException( name, value, "A primitive dimension must be a finite number greater than 0." );
	}
}
