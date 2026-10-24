using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Assign UVs by projecting the mesh onto planes.
///
/// WHY THIS MATTERS MORE THAN IT SOUNDS. The whole argument for starting parametric is that the
/// cage arrives already unwrapped, so nobody ever has to do a retopology-and-unwrap pass — and the
/// sculpt stage's normal-map bake reads exactly these UVs. Placeholder UVs would push that work
/// back onto the user at the worst possible moment, which is the failure this pipeline exists to
/// avoid. Planar projection looks crude next to a real unwrapper and is close to ideal on hard
/// surface, which is what this tool builds.
///
/// BOX PROJECTION picks, per face, whichever of the six axis directions the face most faces, and
/// projects onto that plane. Every face gets an undistorted mapping, seams land on the corners
/// where the dominant axis changes, and because UVs live per corner a seam costs nothing — the two
/// faces simply disagree about the UV at a shared position, which is exactly what a seam is.
///
/// HANDEDNESS IS THE PART THAT GOES WRONG. Each direction needs its u and v chosen so that
/// (u, v, normal) is right-handed. Get one of the six backwards and that side of the model renders
/// its texture mirrored — text reads backwards, and nothing else looks wrong enough to notice. The
/// six pairs below are chosen for that property and there is a test that checks all six agree.
/// </summary>
public static partial class UVProjection
{
	/// <summary>
	/// Project each face onto whichever axis plane it most faces.
	///
	/// `scale` is world units per texture tile: 32 means the texture repeats every 32 units. Larger
	/// scale, larger apparent texture.
	/// </summary>
	public static void BoxProject( PolyMesh mesh, float scale = 1f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( scale <= 0f )
			throw new InvalidOperationException( "UV scale must be positive" );

		foreach ( var face in mesh.Faces )
		{
			var normal = mesh.FaceNormal( face );
			var (u, v) = AxisPair( normal );

			for ( var i = 0; i < face.Count; i++ )
			{
				var p = mesh.Positions[face.Indices[i]];
				face.UVs[i] = new Vec2( Vec3.Dot( p, u ) / scale, Vec3.Dot( p, v ) / scale );
			}
		}
	}

	/// <summary>
	/// Project the whole mesh along one direction, ignoring which way each face points.
	///
	/// Useful where box projection's seams fall somewhere unhelpful — a sign, a decal, a floor that
	/// wants one continuous texture. Faces perpendicular to the direction get their UVs squashed to
	/// a line, which is inherent to projecting rather than a bug, and is why this is not the
	/// default.
	/// </summary>
	public static void PlanarProject( PolyMesh mesh, Vec3 direction, float scale = 1f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( scale <= 0f )
			throw new InvalidOperationException( "UV scale must be positive" );

		var normal = direction.Normal;

		if ( normal.LengthSquared < 0.5f )
			throw new InvalidOperationException( "Projection direction has no length" );

		// Any axis not parallel to the direction works as a seed; picking the one it leans on least
		// keeps the cross product well conditioned.
		var seed = MathF.Abs( normal.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 );
		var u = Vec3.Cross( seed, normal ).Normal;
		var v = Vec3.Cross( normal, u );

		foreach ( var face in mesh.Faces )
		{
			for ( var i = 0; i < face.Count; i++ )
			{
				var p = mesh.Positions[face.Indices[i]];
				face.UVs[i] = new Vec2( Vec3.Dot( p, u ) / scale, Vec3.Dot( p, v ) / scale );
			}
		}
	}

	/// <summary>
	/// The u and v axes for whichever of the six directions a normal most points along.
	///
	/// Each pair satisfies cross(u, v) == the face direction, which is what keeps the texture the
	/// right way round rather than mirrored. Ties — a normal exactly 45 degrees between two axes —
	/// resolve to the earlier axis, deterministically, so the same mesh always projects the same
	/// way.
	/// </summary>
	static (Vec3 U, Vec3 V) AxisPair( Vec3 normal )
	{
		var ax = MathF.Abs( normal.x );
		var ay = MathF.Abs( normal.y );
		var az = MathF.Abs( normal.z );

		if ( ax >= ay && ax >= az )
		{
			return normal.x >= 0f
				? (new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ))    // cross(+Y, +Z) = +X
				: (new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ));   // cross(+Z, +Y) = -X
		}

		if ( ay >= az )
		{
			return normal.y >= 0f
				? (new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 0 ))    // cross(+Z, +X) = +Y
				: (new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ));   // cross(+X, +Z) = -Y
		}

		return normal.z >= 0f
			? (new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ))        // cross(+X, +Y) = +Z
			: (new Vec3( 0, 1, 0 ), new Vec3( 1, 0, 0 ));       // cross(+Y, +X) = -Z
	}

	/// <summary>
	/// Map <paramref name="faces"/> onto one horizontal strip of a trim sheet: straps,
	/// moldings, pipes and panel borders. U runs along the longest extent of the selected
	/// faces (their length), at <paramref name="uScale"/> world units per UV unit, starting
	/// at 0; V runs across the next-longest extent, normalised into
	/// [<paramref name="vMin"/>, <paramref name="vMax"/>]. A flat strap therefore lands
	/// exactly on one strip; a round pipe lands with its circumference as the V range,
	/// which is the strip the trim texture's cross-section lives on. Writes UV1
	/// (<see cref="Face.UVs"/>); UV2 is untouched.
	/// </summary>
	public static void TrimBand( PolyMesh mesh, IReadOnlyList<int> faces, float vMin, float vMax, float uScale )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( faces is null || faces.Count == 0 )
			throw new ArgumentException( "TrimBand needs at least one face.", nameof( faces ) );
		if ( uScale <= 0f || !float.IsFinite( uScale ) )
			throw new InvalidOperationException( "Trim uScale must be positive: world units per UV unit." );
		if ( vMax < vMin )
			throw new InvalidOperationException( "Trim vMax must not be below vMin." );

		// Length axis = longest bounding-box axis of the selected faces' vertices; width
		// axis = the next longest. Ties resolve X, then Y, then Z, deterministically.
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var fi in faces )
		{
			if ( fi < 0 || fi >= mesh.FaceCount )
				throw new ArgumentOutOfRangeException( nameof( faces ) );
			foreach ( var vi in mesh.Faces[fi].Indices )
			{
				var p = mesh.Positions[vi];
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
		}
		var ext = max - min;
		var axes = new[] { (ext.x, new Vec3( 1, 0, 0 ), min.x), (ext.y, new Vec3( 0, 1, 0 ), min.y), (ext.z, new Vec3( 0, 0, 1 ), min.z ) };
		Array.Sort( axes, ( a, b ) => b.Item1.CompareTo( a.Item1 ) );
		var (len, lenAxis, lenMin) = axes[0];
		var (wid, widAxis, widMin) = axes[1];

		foreach ( var fi in faces )
		{
			var face = mesh.Faces[fi];
			for ( var c = 0; c < face.Count; c++ )
			{
				var p = mesh.Positions[face.Indices[c]];
				var u = (Vec3.Dot( p, lenAxis ) - lenMin) / uScale;
				var t = wid > 1e-9f ? (Vec3.Dot( p, widAxis ) - widMin) / wid : 0.5f;
				face.UVs[c] = new Vec2( u, vMin + (vMax - vMin) * t );
			}
		}
	}
}
