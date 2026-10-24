using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Effigy;

/// <summary>
/// A signed distance field: negative inside, positive outside, in model units. The organic
/// modelling path that has no booleans in it. Spheres, capsules, round cones, ellipsoids, tubes
/// and whole meshes go in; smooth union fuses them with a fillet instead of a seam, smooth
/// subtraction carves cavities with rounded lips, noise roughens the lot, and
/// <see cref="SurfaceNets"/> turns the field into one closed quad mesh.
///
/// EVERY LEAF CARRIES A MATERIAL AND A BONE. The mesher asks the field which leaf is nearest to
/// each vertex it makes, so the fused surface still knows which part it came from: that is how a
/// vertex gets its material slot and its skin weights without anyone painting them.
///
/// DISTANCES ONLY NEED TO BE RIGHT NEAR THE SURFACE. Blends skip children whose padded bounds
/// the point is outside of, which makes a far distance wrong by a little; the mesher only reads
/// the field where it crosses zero, so nothing downstream notices. Keep that in mind before
/// using one of these as an exact distance for anything else.
/// </summary>
public abstract class Sdf
{
	/// <summary>Material slot for surface made by this leaf.</summary>
	public int Material;

	/// <summary>Bone that moves surface made by this leaf, or -1 for none.</summary>
	public int Bone = -1;

	/// <summary>A free tag for the caller: which design part this is.</summary>
	public string Tag;

	public abstract float Eval( Vec3 p );

	/// <summary>Conservative axis-aligned bounds of the surface this field can produce.</summary>
	public abstract Vec3 Min { get; }
	public abstract Vec3 Max { get; }

	/// <summary>
	/// Every leaf within <paramref name="band"/> of <paramref name="p"/>, with its unsigned
	/// distance. The nearest one labels a vertex; the lot of them weight it.
	/// </summary>
	public abstract void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into );

	/// <summary>Build any acceleration the node needs, padded for blends and noise, before the
	/// field is evaluated from several threads at once.</summary>
	public virtual void Prepare( float pad ) { }

	/// <summary>The nearest leaf to a point, by unsigned distance, or null when none is within band.</summary>
	public Sdf Nearest( Vec3 p, float band )
	{
		var list = new List<(Sdf, float)>();
		Collect( p, band, list );

		Sdf best = null;
		var bestD = float.MaxValue;

		foreach ( var (leaf, d) in list )
		{
			if ( d < bestD )
			{
				bestD = d;
				best = leaf;
			}
		}

		return best;
	}

	public bool InBounds( Vec3 p, float pad ) =>
		p.x >= Min.x - pad && p.x <= Max.x + pad
		&& p.y >= Min.y - pad && p.y <= Max.y + pad
		&& p.z >= Min.z - pad && p.z <= Max.z + pad;

	// --- the smooth operators (Quilez) --------------------------------------------------------

	public static float SmoothMin( float a, float b, float k )
	{
		if ( k <= 1e-6f )
			return MathF.Min( a, b );

		var h = MathF.Max( k - MathF.Abs( a - b ), 0f ) / k;
		return MathF.Min( a, b ) - h * h * k * 0.25f;
	}

	public static float SmoothMax( float a, float b, float k )
	{
		if ( k <= 1e-6f )
			return MathF.Max( a, b );

		var h = MathF.Max( k - MathF.Abs( a - b ), 0f ) / k;
		return MathF.Max( a, b ) + h * h * k * 0.25f;
	}
}

// --- leaves ----------------------------------------------------------------------------------

public abstract class SdfLeaf : Sdf
{
	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into )
	{
		if ( !InBounds( p, band ) )
			return;

		var d = MathF.Abs( Eval( p ) );

		if ( d <= band )
			into.Add( (this, d) );
	}
}

public sealed class SdfSphere : SdfLeaf
{
	public Vec3 Center;
	public float Radius;

	public SdfSphere( Vec3 center, float radius )
	{
		Center = center;
		Radius = radius;
	}

	public override float Eval( Vec3 p ) => (p - Center).Length - Radius;
	public override Vec3 Min => Center - Vec3.One * Radius;
	public override Vec3 Max => Center + Vec3.One * Radius;
}

/// <summary>A sphere swept between two radii: the organic limb primitive.</summary>
public sealed class SdfRoundCone : SdfLeaf
{
	public Vec3 A, B;
	public float Ra, Rb;

	public SdfRoundCone( Vec3 a, float ra, Vec3 b, float rb )
	{
		A = a;
		B = b;
		Ra = ra;
		Rb = rb;
	}

	public override float Eval( Vec3 p )
	{
		var ba = B - A;
		var l2 = Vec3.Dot( ba, ba );
		var rr = Ra - Rb;
		var a2 = l2 - rr * rr;
		var il2 = 1f / MathF.Max( l2, 1e-9f );

		var pa = p - A;
		var y = Vec3.Dot( pa, ba );
		var z = y - l2;
		var x2 = Vec3.Dot( pa * l2 - ba * y, pa * l2 - ba * y );
		var y2 = y * y * l2;
		var z2 = z * z * l2;

		var k = MathF.Sign( rr ) * rr * rr * x2;

		if ( MathF.Sign( z ) * a2 * z2 > k )
			return MathF.Sqrt( x2 + z2 ) * il2 - Rb;
		if ( MathF.Sign( y ) * a2 * y2 < k )
			return MathF.Sqrt( x2 + y2 ) * il2 - Ra;

		return (MathF.Sqrt( x2 * a2 * il2 ) + y * rr) * il2 - Ra;
	}

	public override Vec3 Min => new(
		MathF.Min( A.x - Ra, B.x - Rb ), MathF.Min( A.y - Ra, B.y - Rb ), MathF.Min( A.z - Ra, B.z - Rb ) );

	public override Vec3 Max => new(
		MathF.Max( A.x + Ra, B.x + Rb ), MathF.Max( A.y + Ra, B.y + Rb ), MathF.Max( A.z + Ra, B.z + Rb ) );
}

public sealed class SdfCapsule : SdfLeaf
{
	public Vec3 A, B;
	public float Radius;

	public SdfCapsule( Vec3 a, Vec3 b, float radius )
	{
		A = a;
		B = b;
		Radius = radius;
	}

	public override float Eval( Vec3 p )
	{
		var pa = p - A;
		var ba = B - A;
		var h = Math.Clamp( Vec3.Dot( pa, ba ) / MathF.Max( Vec3.Dot( ba, ba ), 1e-9f ), 0f, 1f );
		return (pa - ba * h).Length - Radius;
	}

	public override Vec3 Min => new(
		MathF.Min( A.x, B.x ) - Radius, MathF.Min( A.y, B.y ) - Radius, MathF.Min( A.z, B.z ) - Radius );

	public override Vec3 Max => new(
		MathF.Max( A.x, B.x ) + Radius, MathF.Max( A.y, B.y ) + Radius, MathF.Max( A.z, B.z ) + Radius );
}

/// <summary>An ellipsoid with its own orientation. The distance is Quilez's bound, not exact,
/// which is close enough to mesh and to blend.</summary>
public sealed class SdfEllipsoid : SdfLeaf
{
	public Vec3 Center;
	public Vec3 Radii;
	/// <summary>Rotation of the ellipsoid's axes; identity means axis aligned.</summary>
	public Xform Rotation;
	readonly Xform _inverse;
	readonly float _maxR;

	public SdfEllipsoid( Vec3 center, Vec3 radii, Xform? rotation = null )
	{
		Center = center;
		Radii = radii;
		Rotation = rotation ?? Xform.Identity;
		_inverse = Rotation.Inverse;
		_maxR = MathF.Max( radii.x, MathF.Max( radii.y, radii.z ) );
	}

	public override float Eval( Vec3 p )
	{
		var q = _inverse.TransformDirection( p - Center );
		var k0 = new Vec3( q.x / Radii.x, q.y / Radii.y, q.z / Radii.z ).Length;
		var k1 = new Vec3( q.x / (Radii.x * Radii.x), q.y / (Radii.y * Radii.y), q.z / (Radii.z * Radii.z) ).Length;
		return k0 * (k0 - 1f) / MathF.Max( k1, 1e-9f );
	}

	public override Vec3 Min => Center - Vec3.One * _maxR;
	public override Vec3 Max => Center + Vec3.One * _maxR;
}

/// <summary>
/// A box, its edges rounded by <see cref="Round"/>, turned by <see cref="Rotation"/> about its centre.
/// Exact distance (Quilez). The rounding is cut from inside the size, so a 4 u box stays 4 u wide.
/// </summary>
public sealed class SdfBox : SdfLeaf
{
	public Vec3 Center;
	/// <summary>Half the size on each local axis.</summary>
	public Vec3 Half;
	public float Round;
	public Xform Rotation;
	readonly Xform _inverse;
	readonly float _reach;

	public SdfBox( Vec3 center, Vec3 size, float round = 0f, Xform? rotation = null )
	{
		Center = center;
		Half = size * 0.5f;
		Round = Math.Clamp( round, 0f, MathF.Min( Half.x, MathF.Min( Half.y, Half.z ) ) );
		Rotation = rotation ?? Xform.Identity;
		_inverse = Rotation.Inverse;
		_reach = Half.Length;
	}

	public override float Eval( Vec3 p )
	{
		var q = _inverse.TransformDirection( p - Center );
		var dx = MathF.Abs( q.x ) - Half.x + Round;
		var dy = MathF.Abs( q.y ) - Half.y + Round;
		var dz = MathF.Abs( q.z ) - Half.z + Round;
		var outside = new Vec3( MathF.Max( dx, 0f ), MathF.Max( dy, 0f ), MathF.Max( dz, 0f ) ).Length;
		return outside + MathF.Min( MathF.Max( dx, MathF.Max( dy, dz ) ), 0f ) - Round;
	}

	public override Vec3 Min => Center - Vec3.One * _reach;
	public override Vec3 Max => Center + Vec3.One * _reach;
}

/// <summary>A cylinder between two ends with flat caps, the cap rim rounded by <see cref="Round"/>.
/// Exact distance (Quilez). Unlike a capsule its ends stop at A and B.</summary>
public sealed class SdfCylinder : SdfLeaf
{
	public Vec3 A, B;
	public float Radius, Round;

	public SdfCylinder( Vec3 a, Vec3 b, float radius, float round = 0f )
	{
		A = a;
		B = b;
		Radius = radius;
		Round = Math.Clamp( round, 0f, MathF.Min( radius, (b - a).Length * 0.5f ) );
	}

	public override float Eval( Vec3 p )
	{
		var axis = B - A;
		var length = MathF.Max( axis.Length, 1e-9f );
		var dir = axis / length;
		var pa = p - A;
		var along = Vec3.Dot( pa, dir );
		var across = (pa - dir * along).Length;

		// 2D box distance in (radial, axial), with the rim rounded.
		var dr = across - Radius + Round;
		var dh = MathF.Abs( along - length * 0.5f ) - length * 0.5f + Round;
		var outside = MathF.Sqrt( MathF.Max( dr, 0f ) * MathF.Max( dr, 0f ) + MathF.Max( dh, 0f ) * MathF.Max( dh, 0f ) );
		return outside + MathF.Min( MathF.Max( dr, dh ), 0f ) - Round;
	}

	public override Vec3 Min => new(
		MathF.Min( A.x, B.x ) - Radius, MathF.Min( A.y, B.y ) - Radius, MathF.Min( A.z, B.z ) - Radius );

	public override Vec3 Max => new(
		MathF.Max( A.x, B.x ) + Radius, MathF.Max( A.y, B.y ) + Radius, MathF.Max( A.z, B.z ) + Radius );
}

/// <summary>A ring: <see cref="Major"/> from the centre to the middle of the tube, <see cref="Minor"/>
/// the tube's radius. It lies in the local XY plane (around Z) before <see cref="Rotation"/>.</summary>
public sealed class SdfTorus : SdfLeaf
{
	public Vec3 Center;
	public float Major, Minor;
	public Xform Rotation;
	readonly Xform _inverse;

	public SdfTorus( Vec3 center, float major, float minor, Xform? rotation = null )
	{
		Center = center;
		Major = major;
		Minor = minor;
		Rotation = rotation ?? Xform.Identity;
		_inverse = Rotation.Inverse;
	}

	public override float Eval( Vec3 p )
	{
		var q = _inverse.TransformDirection( p - Center );
		var ring = MathF.Sqrt( q.x * q.x + q.y * q.y ) - Major;
		return MathF.Sqrt( ring * ring + q.z * q.z ) - Minor;
	}

	public override Vec3 Min => Center - Vec3.One * (Major + Minor);
	public override Vec3 Max => Center + Vec3.One * (Major + Minor);
}

/// <summary>A polyline swept with a radius at every point: a tendril, a gut, a vein.</summary>
public sealed class SdfTube : SdfLeaf
{
	public readonly Vec3[] Points;
	public readonly float[] Radii;
	readonly Vec3 _min, _max;

	public SdfTube( IReadOnlyList<Vec3> points, IReadOnlyList<float> radii )
	{
		if ( points.Count < 2 || points.Count != radii.Count )
			throw new ArgumentException( "A tube needs at least two points and one radius per point" );

		Points = points.ToArray();
		Radii = radii.ToArray();

		_min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		_max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		for ( var i = 0; i < Points.Length; i++ )
		{
			var r = Radii[i];
			_min = new Vec3( MathF.Min( _min.x, Points[i].x - r ), MathF.Min( _min.y, Points[i].y - r ), MathF.Min( _min.z, Points[i].z - r ) );
			_max = new Vec3( MathF.Max( _max.x, Points[i].x + r ), MathF.Max( _max.y, Points[i].y + r ), MathF.Max( _max.z, Points[i].z + r ) );
		}
	}

	public override float Eval( Vec3 p )
	{
		var best = float.MaxValue;

		for ( var i = 0; i + 1 < Points.Length; i++ )
		{
			var a = Points[i];
			var b = Points[i + 1];
			var ra = Radii[i];
			var rb = Radii[i + 1];

			// Cheap reject: the segment's padded box.
			var pad = MathF.Max( ra, rb );
			if ( p.x < MathF.Min( a.x, b.x ) - pad - 2f || p.x > MathF.Max( a.x, b.x ) + pad + 2f
				|| p.y < MathF.Min( a.y, b.y ) - pad - 2f || p.y > MathF.Max( a.y, b.y ) + pad + 2f
				|| p.z < MathF.Min( a.z, b.z ) - pad - 2f || p.z > MathF.Max( a.z, b.z ) + pad + 2f )
			{
				// Still need a sane distance when nothing is near: use the segment's capsule.
				var pa = p - a;
				var ba = b - a;
				var h = Math.Clamp( Vec3.Dot( pa, ba ) / MathF.Max( Vec3.Dot( ba, ba ), 1e-9f ), 0f, 1f );
				best = MathF.Min( best, (pa - ba * h).Length - (ra + (rb - ra) * h) );
				continue;
			}

			best = MathF.Min( best, RoundCone( p, a, ra, b, rb ) );
		}

		return best;
	}

	static float RoundCone( Vec3 p, Vec3 a, float ra, Vec3 b, float rb )
	{
		var ba = b - a;
		var l2 = Vec3.Dot( ba, ba );
		var rr = ra - rb;
		var a2 = l2 - rr * rr;
		var il2 = 1f / MathF.Max( l2, 1e-9f );

		var pa = p - a;
		var y = Vec3.Dot( pa, ba );
		var z = y - l2;
		var w = pa * l2 - ba * y;
		var x2 = Vec3.Dot( w, w );
		var y2 = y * y * l2;
		var z2 = z * z * l2;

		var k = MathF.Sign( rr ) * rr * rr * x2;

		if ( MathF.Sign( z ) * a2 * z2 > k )
			return MathF.Sqrt( x2 + z2 ) * il2 - rb;
		if ( MathF.Sign( y ) * a2 * y2 < k )
			return MathF.Sqrt( x2 + y2 ) * il2 - ra;

		return (MathF.Sqrt( x2 * a2 * il2 ) + y * rr) * il2 - ra;
	}

	public override Vec3 Min => _min;
	public override Vec3 Max => _max;
}

/// <summary>
/// A mesh as a field. Signed by the nearest face's normal, so the mesh must be closed and
/// outward-facing to read as a solid; a shell (a garment) comes in through
/// <see cref="Thickness"/>, which makes the field the unsigned distance minus half of it.
/// Beyond <see cref="Band"/> the distance is clamped, which is why a mesh can only blend with
/// things that come within the band of it.
/// </summary>
public sealed class SdfMesh : SdfLeaf
{
	public readonly PolyMesh Mesh;
	public readonly float Band;
	/// <summary>Zero for a closed solid; a shell's thickness otherwise.</summary>
	public readonly float Thickness;
	readonly MeshBVH _bvh;
	readonly Vec3 _min, _max;

	public SdfMesh( PolyMesh mesh, float band, float thickness = 0f )
	{
		Mesh = mesh;
		Band = band;
		Thickness = thickness;
		// One tree for every thread: NearestSurface only reads it.
		_bvh = MeshBVH.Build( mesh );

		_min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		_max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			_min = new Vec3( MathF.Min( _min.x, p.x ), MathF.Min( _min.y, p.y ), MathF.Min( _min.z, p.z ) );
			_max = new Vec3( MathF.Max( _max.x, p.x ), MathF.Max( _max.y, p.y ), MathF.Max( _max.z, p.z ) );
		}

		_min -= Vec3.One * thickness;
		_max += Vec3.One * thickness;

		// Only a closed solid has an inside to find. An open patch (a sleeve of faces cut off a body)
		// keeps the old answer, outside past the band, which is what its callers rely on.
		_closed = thickness <= 0f && band > 0f && mesh.FaceCount > 0 && MeshValidator.Validate( mesh ).IsClosed;
		_inside = new Lazy<InsideLattice>( () => new InsideLattice( Mesh, _bvh, _min, _max, Band ),
			System.Threading.LazyThreadSafetyMode.ExecutionAndPublication );
	}

	readonly bool _closed;
	readonly Lazy<InsideLattice> _inside;

	public override float Eval( Vec3 p )
	{
		if ( !InBounds( p, Band ) )
			return Band;

		var hit = _bvh.NearestSurface( Mesh, p, Band + Thickness );

		// PAST THE BAND IS NOT "OUTSIDE" FOR A SOLID. This used to return +Band for any point more
		// than a band from the surface, so the middle of anything thicker than two bands read as
		// empty and the mesher wrapped a second, inward skin round it: a 10 in box remeshed at
		// 0.25 came back as two shells holding 434 cubic inches instead of 1000.
		if ( hit is not { } h )
			return _closed && _inside.Value.Inside( p ) ? -Band : Band;

		if ( Thickness > 0f )
			return h.Distance - Thickness * 0.5f;

		var outside = Vec3.Dot( p - h.Point, h.Normal ) >= 0f;
		return outside ? h.Distance : -h.Distance;
	}

	public override Vec3 Min => _min;
	public override Vec3 Max => _max;
}

/// <summary>
/// Which side of a closed mesh a point FAR from its surface is on, answered from a coarse lattice.
///
/// Spacing is just under the band, so a point more than a band from the surface is within that
/// distance of its nearest node with no surface between them: the node's answer is the point's.
/// Each node is decided by parity - one ray along +x per lattice column, every crossing counted -
/// with the column nudged off the lattice so it never runs along a face. That is one ray per column
/// rather than one per voxel, which is what keeps Blob and Voxel remesh as fast as they were.
/// </summary>
sealed class InsideLattice
{
	readonly bool[] _inside;
	readonly Vec3 _origin;
	readonly float _cell;
	readonly int _nx, _ny, _nz;

	public InsideLattice( PolyMesh mesh, MeshBVH bvh, Vec3 min, Vec3 max, float band )
	{
		_cell = band * 0.85f;
		_origin = min - Vec3.One * (band + _cell);
		var size = max + Vec3.One * (band + _cell) - _origin;
		_nx = Math.Max( 2, (int)MathF.Ceiling( size.x / _cell ) + 1 );
		_ny = Math.Max( 2, (int)MathF.Ceiling( size.y / _cell ) + 1 );
		_nz = Math.Max( 2, (int)MathF.Ceiling( size.z / _cell ) + 1 );
		_inside = new bool[(long)_nx * _ny * _nz];

		var dir = new Vec3( 1, 0, 0 );
		var startX = _origin.x - _cell;

		Parallel.For( 0, _ny, y =>
		{
			var crossings = new List<float>();

			for ( var z = 0; z < _nz; z++ )
			{
				crossings.Clear();
				var from = new Vec3( startX, _origin.y + (y + 0.0137f) * _cell, _origin.z + (z + 0.0071f) * _cell );

				for ( var guard = 0; guard < 100_000; guard++ )
				{
					if ( bvh.Raycast( mesh, from, dir ) is not { } hit )
						break;

					crossings.Add( hit.Point.x );
					from = new Vec3( hit.Point.x + _cell * 1e-4f, from.y, from.z );
				}

				var c = 0;

				for ( var x = 0; x < _nx; x++ )
				{
					var px = _origin.x + x * _cell;
					while ( c < crossings.Count && crossings[c] < px ) c++;
					_inside[((long)x * _ny + y) * _nz + z] = (c & 1) == 1;
				}
			}
		} );
	}

	public bool Inside( Vec3 p )
	{
		var x = (int)MathF.Round( (p.x - _origin.x) / _cell );
		var y = (int)MathF.Round( (p.y - _origin.y) / _cell );
		var z = (int)MathF.Round( (p.z - _origin.z) / _cell );

		if ( x < 0 || y < 0 || z < 0 || x >= _nx || y >= _ny || z >= _nz )
			return false;

		return _inside[((long)x * _ny + y) * _nz + z];
	}
}

// --- instancing and warping -----------------------------------------------------------------

/// <summary>
/// A domain warp: where to read the child field from for a point in the warped space. Warps
/// are written as inverse maps (warped point to source point) because that is what evaluating
/// a field needs; the visible effect is the opposite of the map, so a Stretch with factor 2
/// makes the part twice as long.
/// </summary>
public abstract class SdfWarp
{
	/// <summary>Source-space point for a warped-space point.</summary>
	public abstract Vec3 Inverse( Vec3 p );

	/// <summary>Warped-space point for a source-space point: the warp applied to a real vertex.</summary>
	public abstract Vec3 Forward( Vec3 p );

	/// <summary>How far, at most, this warp moves the surface: pads the bounds.</summary>
	public abstract float Reach { get; }

	/// <summary>Smooth 0..1 falloff: 1 at the centre, 0 at radius.</summary>
	public static float Falloff( float d, float radius )
	{
		if ( radius <= 0f ) return 1f;
		var t = Math.Clamp( 1f - d / radius, 0f, 1f );
		return t * t * (3f - 2f * t);
	}
}

/// <summary>Stretch along an axis about a point, inside a radius (or everywhere when radius is 0).</summary>
public sealed class WarpStretch : SdfWarp
{
	public Vec3 Center, Axis;
	public float Factor, Radius;

	public WarpStretch( Vec3 center, Vec3 axis, float factor, float radius = 0f )
	{
		Center = center; Axis = axis.Normal; Factor = factor; Radius = radius;
	}

	public override Vec3 Inverse( Vec3 p )
	{
		var d = p - Center;
		var along = Vec3.Dot( d, Axis );
		var w = Radius > 0f ? Falloff( (d - Axis * along).Length, Radius ) : 1f;
		var f = 1f + (Factor - 1f) * w;
		return p - Axis * along * (1f - 1f / f);
	}

	public override Vec3 Forward( Vec3 p )
	{
		var d = p - Center;
		var along = Vec3.Dot( d, Axis );
		var w = Radius > 0f ? Falloff( (d - Axis * along).Length, Radius ) : 1f;
		return p + Axis * along * (Factor - 1f) * w;
	}

	public override float Reach => Radius > 0f ? Radius * MathF.Abs( Factor - 1f ) : 0f;
}

/// <summary>Pull a region: everything within radius of Center moves by Offset, fading out.
/// The sculptor's grab brush, as a field warp.</summary>
public sealed class WarpGrab : SdfWarp
{
	public Vec3 Center, Offset;
	public float Radius;

	public WarpGrab( Vec3 center, Vec3 offset, float radius )
	{
		Center = center; Offset = offset; Radius = radius;
	}

	public override Vec3 Inverse( Vec3 p )
	{
		// Fixed-point iteration: find q with q + Offset * w(q) = p.
		var q = p - Offset * Falloff( (p - Center - Offset).Length, Radius );
		for ( var i = 0; i < 3; i++ )
			q = p - Offset * Falloff( (q - Center).Length, Radius );
		return q;
	}

	public override Vec3 Forward( Vec3 p ) => p + Offset * Falloff( (p - Center).Length, Radius );

	public override float Reach => Offset.Length;
}

/// <summary>Twist about an axis: degrees per unit along it, inside a radius along the axis.</summary>
public sealed class WarpTwist : SdfWarp
{
	public Vec3 Center, Axis;
	public float DegreesPerUnit, Length;

	public WarpTwist( Vec3 center, Vec3 axis, float degreesPerUnit, float length = 0f )
	{
		Center = center; Axis = axis.Normal; DegreesPerUnit = degreesPerUnit; Length = length;
	}

	public override Vec3 Inverse( Vec3 p )
	{
		var d = p - Center;
		var t = Vec3.Dot( d, Axis );
		if ( Length > 0f ) t = Math.Clamp( t, -Length, Length );
		return Center + Xform.Rotate( Axis, -t * DegreesPerUnit * MathF.PI / 180f ).TransformDirection( d );
	}

	public override Vec3 Forward( Vec3 p )
	{
		var d = p - Center;
		var t = Vec3.Dot( d, Axis );
		if ( Length > 0f ) t = Math.Clamp( t, -Length, Length );
		return Center + Xform.Rotate( Axis, t * DegreesPerUnit * MathF.PI / 180f ).TransformDirection( d );
	}

	public override float Reach => 0f;
}

/// <summary>Swell or pinch everything within a radius of a point (factor above 1 swells).</summary>
public sealed class WarpBulge : SdfWarp
{
	public Vec3 Center;
	public float Factor, Radius;

	public WarpBulge( Vec3 center, float factor, float radius )
	{
		Center = center; Factor = factor; Radius = radius;
	}

	public override Vec3 Inverse( Vec3 p )
	{
		var d = p - Center;
		var w = Falloff( d.Length, Radius );
		var f = 1f + (Factor - 1f) * w;
		return Center + d / f;
	}

	public override Vec3 Forward( Vec3 p )
	{
		var d = p - Center;
		var w = Falloff( d.Length, Radius );
		return Center + d * (1f + (Factor - 1f) * w);
	}

	public override float Reach => Radius * MathF.Abs( Factor - 1f ) * 0.5f;
}

/// <summary>
/// A hinge: everything on the negative side of a separating plane rotates about an axis, with
/// a soft band at the plane so the skin stretches across it. A jaw opening on its joint, not a
/// chin dragged down.
/// </summary>
public sealed class WarpHinge : SdfWarp
{
	public Vec3 AxisPoint, Axis, PlanePoint, PlaneNormal;
	public float Degrees, Band;
	/// <summary>Optional gate: only points for which this returns above 0 move, scaled by it.</summary>
	public Func<Vec3, float> Gate;
	/// <summary>Optional band width per point, in place of the constant Band: tight where the
	/// parts must separate cleanly, wide where skin has to stretch across.</summary>
	public Func<Vec3, float> BandAt;
	/// <summary>A translation applied with the same weight as the turn: the condyle sliding
	/// forward onto the eminence while the jaw opens.</summary>
	public Vec3 Slide;
	/// <summary>Optional reshaping of the weight per point, after the plane and gate: lets a
	/// region (the mouth corners) take a fixed share of the jaw's motion.</summary>
	public Func<Vec3, float, float> Remap;

	public WarpHinge( Vec3 axisPoint, Vec3 axis, float degrees, Vec3 planePoint, Vec3 planeNormal, float band = 0.4f )
	{
		AxisPoint = axisPoint; Axis = axis.Normal; Degrees = degrees;
		PlanePoint = planePoint; PlaneNormal = planeNormal.Normal; Band = band;
	}

	float Weight( Vec3 p )
	{
		var s = Vec3.Dot( p - PlanePoint, PlaneNormal );
		var band = BandAt?.Invoke( p ) ?? Band;
		var t = Math.Clamp( (band * 0.4f - s) / band, 0f, 1f );
		var w = t * t * (3f - 2f * t);
		if ( Gate is not null ) w *= Math.Clamp( Gate( p ), 0f, 1f );
		return Remap is null ? w : Math.Clamp( Remap( p, w ), 0f, 1f );
	}

	Vec3 Turn( Vec3 p, float degrees ) =>
		AxisPoint + Xform.Rotate( Axis, degrees * MathF.PI / 180f ).TransformDirection( p - AxisPoint );

	public override Vec3 Forward( Vec3 p )
	{
		var w = Weight( p );
		return Turn( p, Degrees * w ) + Slide * w;
	}

	public override Vec3 Inverse( Vec3 p )
	{
		var q = p;
		for ( var i = 0; i < 4; i++ )
		{
			var w = Weight( q );
			q = Turn( p - Slide * w, -Degrees * w );
		}
		return q;
	}

	public override float Reach => 6f * MathF.Sin( MathF.Abs( Degrees ) * MathF.PI / 180f );
}

/// <summary>
/// A directional drag with a tail: everything near Center moves along Dir, fading across the
/// drag within Width and trailing behind it over Tail, with a little noise so it is not a clean
/// slide. A face smeared sideways, not widened.
/// </summary>
public sealed class WarpDrag : SdfWarp
{
	public Vec3 Center, Dir;
	public float Amount, Tail, Width;
	readonly Noise3 _noise;

	public WarpDrag( Vec3 center, Vec3 dir, float amount, float tail, float width, int seed = 1 )
	{
		Center = center; Dir = dir.Normal; Amount = amount; Tail = tail; Width = width; _noise = new Noise3( seed );
	}

	float Weight( Vec3 p )
	{
		var d = p - Center;
		var t = Vec3.Dot( d, Dir );
		var q = (d - Dir * t).Length;
		var across = Falloff( q, Width );
		float along;
		if ( t < 0f ) { var u = Math.Clamp( 1f + t / Tail, 0f, 1f ); along = u * u * (3f - 2f * u); }
		else along = MathF.Exp( -(t * t) / MathF.Max( 0.09f * Tail * Tail, 1e-4f ) );
		return across * along * (0.7f + 0.3f * _noise.Fbm( p * 2f, 2 ));
	}

	public override Vec3 Forward( Vec3 p ) => p + Dir * Amount * Weight( p );

	public override Vec3 Inverse( Vec3 p )
	{
		var q = p;
		for ( var i = 0; i < 4; i++ )
			q = p - Dir * Amount * Weight( q );
		return q;
	}

	public override float Reach => MathF.Abs( Amount );
}

/// <summary>Noise in the domain: wobbles a clean sculpt into something sick.</summary>
public sealed class WarpNoise : SdfWarp
{
	public float Amplitude, Frequency;
	readonly Noise3 _noise;

	public WarpNoise( float amplitude, float frequency, int seed )
	{
		Amplitude = amplitude; Frequency = frequency; _noise = new Noise3( seed );
	}

	public override Vec3 Inverse( Vec3 p ) => p + _noise.Vector( p * Frequency ) * Amplitude;
	public override Vec3 Forward( Vec3 p ) => p - _noise.Vector( p * Frequency ) * Amplitude;
	public override float Reach => Amplitude * 1.2f;
}

/// <summary>
/// A part placed in the world: a child field (usually a mesh) moved by a rigid transform and a
/// uniform scale, then distorted by warps in its own space. The instance is what labels the
/// surface - its Material, Bone and Tag - so one source mesh can be placed many times as
/// different parts of different men.
/// </summary>
public sealed class SdfInstance : Sdf
{
	public readonly Sdf Child;
	/// <summary>Local to world, rotation and translation only.</summary>
	public readonly Xform Placement;
	public readonly float Scale;
	public readonly List<SdfWarp> Warps = new();
	readonly Xform _inverse;
	Vec3 _min, _max;
	bool _bounded;

	public SdfInstance( Sdf child, Xform placement, float scale = 1f )
	{
		Child = child;
		Placement = placement;
		Scale = scale;
		_inverse = placement.Inverse;
		Material = child.Material;
		Bone = child.Bone;
		Tag = child.Tag;
	}

	public SdfInstance Warp( SdfWarp warp )
	{
		Warps.Add( warp );
		_bounded = false;
		return this;
	}

	Vec3 ToChild( Vec3 world )
	{
		var local = _inverse.TransformPoint( world ) / Scale;
		for ( var i = Warps.Count - 1; i >= 0; i-- )
			local = Warps[i].Inverse( local );
		return local;
	}

	public override float Eval( Vec3 p ) => Child.Eval( ToChild( p ) ) * Scale;

	void Bound()
	{
		if ( _bounded ) return;
		var cmin = Child.Min;
		var cmax = Child.Max;
		var pad = Warps.Sum( w => w.Reach );
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		for ( var i = 0; i < 8; i++ )
		{
			var c = new Vec3( (i & 1) == 0 ? cmin.x - pad : cmax.x + pad, (i & 2) == 0 ? cmin.y - pad : cmax.y + pad, (i & 4) == 0 ? cmin.z - pad : cmax.z + pad );
			var w = Placement.TransformPoint( c * Scale );
			lo = new Vec3( MathF.Min( lo.x, w.x ), MathF.Min( lo.y, w.y ), MathF.Min( lo.z, w.z ) );
			hi = new Vec3( MathF.Max( hi.x, w.x ), MathF.Max( hi.y, w.y ), MathF.Max( hi.z, w.z ) );
		}
		// Stretch warps can push past their nominal reach; be generous.
		var extra = (hi - lo) * 0.1f;
		_min = lo - extra;
		_max = hi + extra;
		_bounded = true;
	}

	public override Vec3 Min { get { Bound(); return _min; } }
	public override Vec3 Max { get { Bound(); return _max; } }

	public override void Prepare( float pad )
	{
		Child.Prepare( pad / Scale );
		_bounded = false;
		Bound();
	}

	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into )
	{
		if ( !InBounds( p, band ) )
			return;

		var d = MathF.Abs( Eval( p ) );
		if ( d <= band )
			into.Add( (this, d) );
	}
}

/// <summary>A field pushed out (or in, when negative) by a constant: the same shape, thicker.</summary>
public sealed class SdfOffset : Sdf
{
	public readonly Sdf Child;
	public float Amount;

	public SdfOffset( Sdf child, float amount )
	{
		Child = child; Amount = amount;
		Material = child.Material; Bone = child.Bone; Tag = child.Tag;
	}

	public override float Eval( Vec3 p ) => Child.Eval( p ) - Amount;
	public override Vec3 Min => Child.Min - Vec3.One * Amount;
	public override Vec3 Max => Child.Max + Vec3.One * Amount;
	public override void Prepare( float pad ) => Child.Prepare( pad + Amount );
	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into ) => Child.Collect( p, band + Amount, into );
}

/// <summary>Keep only where both fields are inside: cut a piece out of a bigger mesh with a region.</summary>
public sealed class SdfIntersect : Sdf
{
	public readonly Sdf A, B;
	public float K;

	public SdfIntersect( Sdf a, Sdf b, float k = 0f )
	{
		A = a; B = b; K = k;
		Material = a.Material; Bone = a.Bone; Tag = a.Tag;
	}

	public override float Eval( Vec3 p ) => SmoothMax( A.Eval( p ), B.Eval( p ), K );
	public override Vec3 Min => new( MathF.Max( A.Min.x, B.Min.x ), MathF.Max( A.Min.y, B.Min.y ), MathF.Max( A.Min.z, B.Min.z ) );
	public override Vec3 Max => new( MathF.Min( A.Max.x, B.Max.x ), MathF.Min( A.Max.y, B.Max.y ), MathF.Min( A.Max.z, B.Max.z ) );
	public override void Prepare( float pad ) { A.Prepare( pad ); B.Prepare( pad ); }

	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into )
	{
		if ( !InBounds( p, band ) ) return;
		var d = MathF.Abs( Eval( p ) );
		if ( d <= band ) into.Add( (this, d) );
	}
}

// --- modifiers -------------------------------------------------------------------------------

/// <summary>Fractal noise along the field: lumps at the first octave, skin at the last.</summary>
public sealed class SdfDisplace : Sdf
{
	public readonly Sdf Child;
	public float Amplitude;
	public float Frequency;
	public int Octaves;
	public float Gain = 0.5f;
	public float Lacunarity = 2f;
	/// <summary>Domain warp strength, as a fraction of the wavelength. Breaks the "CG noise" look.</summary>
	public float Warp;
	/// <summary>Squash the noise along one axis: 1 is isotropic, 3 gives striations across it.</summary>
	public Vec3 Stretch = Vec3.One;
	public readonly Noise3 Noise;

	public SdfDisplace( Sdf child, float amplitude, float frequency, int octaves = 3, int seed = 1, float warp = 0f )
	{
		Child = child;
		Amplitude = amplitude;
		Frequency = frequency;
		Octaves = octaves;
		Warp = warp;
		Noise = new Noise3( seed );
		Material = child.Material;
		Bone = child.Bone;
	}

	public override float Eval( Vec3 p )
	{
		var d = Child.Eval( p );

		// No point roughening a point that is nowhere near the surface.
		if ( MathF.Abs( d ) > Amplitude * 2f + 1f )
			return d;

		var q = new Vec3( p.x * Stretch.x, p.y * Stretch.y, p.z * Stretch.z ) * Frequency;

		if ( Warp > 0f )
		{
			var w = Noise.Vector( q * 0.5f ) * (Warp / Frequency);
			q += w * Frequency;
		}

		return d + Noise.Fbm( q, Octaves, Gain, Lacunarity ) * Amplitude;
	}

	Vec3 _min, _max;
	bool _bounded;

	void Bound()
	{
		if ( _bounded ) return;
		_min = Child.Min - Vec3.One * Amplitude;
		_max = Child.Max + Vec3.One * Amplitude;
		_bounded = true;
	}

	public override Vec3 Min { get { Bound(); return _min; } }
	public override Vec3 Max { get { Bound(); return _max; } }

	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into ) =>
		Child.Collect( p, band + Amplitude, into );

	public override void Prepare( float pad )
	{
		Child.Prepare( pad + Amplitude );
		_bounded = false;
		Bound();
	}
}

/// <summary>Smooth union of its children. With k = 0 it is a plain union.</summary>
public sealed class SdfBlend : Sdf
{
	public readonly List<Sdf> Children = new();
	public float K;

	// A coarse grid over the bounds, each cell listing the children whose padded bounds touch
	// it. Built on first evaluation; a hundred tendrils against four million voxels is otherwise
	// four hundred million bounds tests before a single distance.
	int _nx, _ny, _nz;
	float _cell;
	Vec3 _gridMin;
	int[][] _cells;
	float _pad;

	public SdfBlend( float k, params Sdf[] children )
	{
		K = k;
		Children.AddRange( children );
	}

	public SdfBlend Add( Sdf child )
	{
		Children.Add( child );
		_cells = null;
		_bounded = false;
		return this;
	}

	/// <summary>Build the acceleration grid. Called automatically; call it yourself before
	/// evaluating from several threads so the build does not race.</summary>
	public override void Prepare( float extraPad )
	{
		foreach ( var child in Children )
			child.Prepare( extraPad );

		_bounded = false;
		Bound();

		if ( Children.Count == 0 )
		{
			_cells = Array.Empty<int[]>();
			return;
		}

		_pad = K + extraPad + 0.5f;
		var min = Min - Vec3.One * _pad;
		var max = Max + Vec3.One * _pad;
		var size = max - min;
		var longest = MathF.Max( size.x, MathF.Max( size.y, size.z ) );
		_cell = MathF.Max( longest / 48f, 1e-3f );
		_nx = Math.Max( 1, (int)MathF.Ceiling( size.x / _cell ) );
		_ny = Math.Max( 1, (int)MathF.Ceiling( size.y / _cell ) );
		_nz = Math.Max( 1, (int)MathF.Ceiling( size.z / _cell ) );
		_gridMin = min;

		var lists = new List<int>[_nx * _ny * _nz];

		for ( var c = 0; c < Children.Count; c++ )
		{
			var child = Children[c];
			var cmin = child.Min - Vec3.One * _pad;
			var cmax = child.Max + Vec3.One * _pad;

			var x0 = Math.Clamp( (int)((cmin.x - min.x) / _cell), 0, _nx - 1 );
			var y0 = Math.Clamp( (int)((cmin.y - min.y) / _cell), 0, _ny - 1 );
			var z0 = Math.Clamp( (int)((cmin.z - min.z) / _cell), 0, _nz - 1 );
			var x1 = Math.Clamp( (int)((cmax.x - min.x) / _cell), 0, _nx - 1 );
			var y1 = Math.Clamp( (int)((cmax.y - min.y) / _cell), 0, _ny - 1 );
			var z1 = Math.Clamp( (int)((cmax.z - min.z) / _cell), 0, _nz - 1 );

			for ( var x = x0; x <= x1; x++ )
				for ( var y = y0; y <= y1; y++ )
					for ( var z = z0; z <= z1; z++ )
					{
						var i = (x * _ny + y) * _nz + z;
						(lists[i] ??= new List<int>()).Add( c );
					}
		}

		_cells = new int[lists.Length][];
		for ( var i = 0; i < lists.Length; i++ )
			_cells[i] = lists[i]?.ToArray() ?? Array.Empty<int>();
	}

	int[] CellFor( Vec3 p )
	{
		if ( _cells is null )
			lock ( Children )
				if ( _cells is null )
					Prepare( 0f );

		if ( _cells.Length == 0 )
			return Array.Empty<int>();

		var x = (int)((p.x - _gridMin.x) / _cell);
		var y = (int)((p.y - _gridMin.y) / _cell);
		var z = (int)((p.z - _gridMin.z) / _cell);

		if ( x < 0 || y < 0 || z < 0 || x >= _nx || y >= _ny || z >= _nz )
			return Array.Empty<int>();

		return _cells[(x * _ny + y) * _nz + z];
	}

	public override float Eval( Vec3 p )
	{
		var cell = CellFor( p );
		var d = 1e6f;

		foreach ( var c in cell )
		{
			var child = Children[c];

			if ( !child.InBounds( p, _pad ) )
				continue;

			d = SmoothMin( d, child.Eval( p ), K );
		}

		return d;
	}

	Vec3 _min, _max;
	bool _bounded;

	void Bound()
	{
		if ( _bounded ) return;
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var c in Children )
		{
			var cmin = c.Min;
			var cmax = c.Max;
			lo = new Vec3( MathF.Min( lo.x, cmin.x ), MathF.Min( lo.y, cmin.y ), MathF.Min( lo.z, cmin.z ) );
			hi = new Vec3( MathF.Max( hi.x, cmax.x ), MathF.Max( hi.y, cmax.y ), MathF.Max( hi.z, cmax.z ) );
		}
		_min = lo;
		_max = hi;
		_bounded = true;
	}

	public override Vec3 Min { get { Bound(); return _min; } }
	public override Vec3 Max { get { Bound(); return _max; } }

	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into )
	{
		foreach ( var c in CellFor( p ) )
			Children[c].Collect( p, band, into );
	}
}

/// <summary>A base field with cavities carved out of it by the cutters, with rounded lips.</summary>
public sealed class SdfSubtract : Sdf
{
	public readonly Sdf Base;
	public readonly List<Sdf> Cutters = new();
	public float K;

	public SdfSubtract( Sdf @base, float k, params Sdf[] cutters )
	{
		Base = @base;
		K = k;
		Cutters.AddRange( cutters );
	}

	public SdfSubtract Add( Sdf cutter )
	{
		Cutters.Add( cutter );
		return this;
	}

	public override void Prepare( float extraPad )
	{
		Base.Prepare( extraPad );
		foreach ( var c in Cutters )
			c.Prepare( extraPad );
		_bounded = false;
	}

	public override float Eval( Vec3 p )
	{
		var d = Base.Eval( p );

		foreach ( var c in Cutters )
		{
			if ( !c.InBounds( p, K + 0.5f ) )
				continue;

			d = SmoothMax( d, -c.Eval( p ), K );
		}

		return d;
	}

	Vec3 _min, _max;
	bool _bounded;

	public override Vec3 Min { get { if ( !_bounded ) { _min = Base.Min; _max = Base.Max; _bounded = true; } return _min; } }
	public override Vec3 Max { get { if ( !_bounded ) { _min = Base.Min; _max = Base.Max; _bounded = true; } return _max; } }

	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into )
	{
		Base.Collect( p, band, into );

		// A cutter's surface is the cavity wall; it labels the wall with its own material.
		foreach ( var c in Cutters )
			c.Collect( p, band, into );
	}
}

// --- Lane C (v4 roadmap): onion shell, gyroid lattice, domain operators ------------------
// These are field NODES, not warps: they wrap a whole Sdf the way SdfOffset/SdfBlend do, so
// a Blob or a SurfaceNets mesh can use them without touching SdfInstance's warp list. The
// domain operators (twist/bend/mirror) only ever EVALUATE the child through an inverse map,
// which is all a signed-distance mesher needs: it samples Eval, never Forward.

/// <summary>
/// A hollow shell of <see cref="Thickness"/> around the child's surface: the set where the
/// child's distance is within half the thickness. Onion a sphere and you get a bubble wall, not
/// a ball; onion a Blob creature and you get a skin you can cut open. Material/Bone/Tag are
/// the child's, so the shell still labels and weights like the thing it wraps.
/// </summary>
public sealed class SdfOnion : Sdf
{
	public readonly Sdf Child;
	public readonly float Thickness;

	public SdfOnion( Sdf child, float thickness )
	{
		Child = child;
		Thickness = MathF.Max( 0f, thickness );
		Material = child.Material;
		Bone = child.Bone;
		Tag = child.Tag;
	}

	public override float Eval( Vec3 p ) => MathF.Abs( Child.Eval( p ) ) - Thickness * 0.5f;
	public override Vec3 Min => Child.Min - Vec3.One * (Thickness * 0.5f);
	public override Vec3 Max => Child.Max + Vec3.One * (Thickness * 0.5f);
	public override void Prepare( float pad ) => Child.Prepare( pad + Thickness );

	// Labelling reads the child, widened by the shell: a vertex on the shell is near the
	// child's surface by at most half the thickness plus the band.
	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into ) =>
		Child.Collect( p, band + Thickness, into );
}

/// <summary>
/// A gyroid lattice: the triply-periodic surface sin(sx)cos(sy) + sin(sy)cos(sz) +
/// sin(sz)cos(sx) = 0, thickened into struts of <see cref="Thickness"/>. Porous trabecular
/// bone, coral, alien hive. The field is periodic and unbounded, so the constructor takes
/// the box it is allowed to fill — a gyroid with no bounds would mesh forever.
/// Distance is the field value divided by its analytic gradient length, which is the
/// first-order distance to the surface and exact enough at voxel scale.
/// </summary>
public sealed class SdfGyroid : SdfLeaf
{
	public readonly float Scale;
	public readonly float Thickness;
	readonly Vec3 _min, _max;

	public SdfGyroid( float scale, float thickness, Vec3 min, Vec3 max )
	{
		Scale = MathF.Max( 1e-3f, scale );
		Thickness = MathF.Max( 0f, thickness );
		_min = min;
		_max = max;
	}

	public override Vec3 Min => _min;
	public override Vec3 Max => _max;

	public override float Eval( Vec3 p )
	{
		var s = Scale;
		var sx = s * p.x; var sy = s * p.y; var sz = s * p.z;
		var g = MathF.Sin( sx ) * MathF.Cos( sy ) + MathF.Sin( sy ) * MathF.Cos( sz ) + MathF.Sin( sz ) * MathF.Cos( sx );
		// Analytic gradient of g, so the distance is not a guessed constant.
		var gx = s * (MathF.Cos( sx ) * MathF.Cos( sy ) - MathF.Sin( sz ) * MathF.Sin( sx ));
		var gy = s * (-MathF.Sin( sx ) * MathF.Sin( sy ) + MathF.Cos( sy ) * MathF.Cos( sz ));
		var gz = s * (-MathF.Sin( sy ) * MathF.Sin( sz ) + MathF.Cos( sz ) * MathF.Cos( sx ));
		var grad = MathF.Sqrt( gx * gx + gy * gy + gz * gz );
		var d = grad < 1e-6f ? MathF.Abs( g ) : g / grad;
		return MathF.Abs( d ) - Thickness * 0.5f;
	}
}

/// <summary>
/// <summary>
/// Cellular (Worley) growth folds — the roadmap's <c>SdfCellularGrowth</c>: the field
/// is a thin shell around the borders between Worley cells, so a meshed blob of it
/// reads as folded, veined growth rather than a smooth lump. F1 is the distance to
/// the nearest seed point, F2 to the second-nearest; on a cell border F1 == F2, so
/// |F2 - F1| is 0 there and grows inside a cell. The shell is |F2 - F1| / Scale
/// minus half the thickness, in world units, the same shell convention as
/// <see cref="SdfGyroid"/>. Seed points are one deterministic hash point per lattice
/// cell, so the same seed always grows the same folds, and a different seed grows
/// different ones. Reachable from Blob shapes as <c>cellular min max scale
/// thickness [seed]</c>.
/// </summary>
public sealed class SdfCellular : SdfLeaf
{
	public readonly float Scale;
	public readonly float Thickness;
	public readonly int Seed;
	readonly Vec3 _min, _max;

	public SdfCellular( float scale, float thickness, Vec3 min, Vec3 max, int seed = 0 )
	{
		Scale = MathF.Max( 1e-3f, scale );
		Thickness = MathF.Max( 0f, thickness );
		_min = min;
		_max = max;
		Seed = seed;
	}

	public override Vec3 Min => _min;
	public override Vec3 Max => _max;

	static float Hash01( int x, int y, int z, int seed )
	{
		unchecked
		{
			var h = (uint)( x * 374761393 + y * 668265263 + z * 2147483647 + seed * 974634211 );
			h = (h ^ (h >> 13)) * 1274126177u;
			h ^= h >> 16;
			return (h & 0xFFFFFFu) / (float)0x1000000u;
		}
	}

	public override float Eval( Vec3 p )
	{
		var q = p * Scale;
		var bx = MathF.Floor( q.x );
		var by = MathF.Floor( q.y );
		var bz = MathF.Floor( q.z );
		var f1 = float.MaxValue;
		var f2 = float.MaxValue;

		for ( var dx = -1; dx <= 1; dx++ )
		for ( var dy = -1; dy <= 1; dy++ )
		for ( var dz = -1; dz <= 1; dz++ )
		{
			var cx = (int)bx + dx;
			var cy = (int)by + dy;
			var cz = (int)bz + dz;
			var px = cx + Hash01( cx, cy, cz, Seed );
			var py = cy + Hash01( cy, cx, cz, Seed + 101 );
			var pz = cz + Hash01( cz, cy, cx, Seed + 211 );
			var d = MathF.Sqrt( (q.x - px) * (q.x - px) + (q.y - py) * (q.y - py) + (q.z - pz) * (q.z - pz) );

			if ( d < f1 ) { f2 = f1; f1 = d; }
			else if ( d < f2 ) { f2 = d; }
		}

		return (f2 - f1) / Scale - Thickness * 0.5f;
	}
}

/// Twist a field about an axis: a point at distance t along the axis is read from the child
/// rotated by -t * <see cref="DegreesPerUnit"/> degrees, so the meshed shape twists by that
/// rate along its length. <see cref="Length"/> clamps the twisted span (0 = whole axis).
/// This is the Sdf-node twin of WarpTwist, for fields that never pass through SdfInstance.
/// </summary>
public sealed class SdfTwist : Sdf
{
	public readonly Sdf Child;
	public readonly Vec3 Center, Axis;
	public readonly float DegreesPerUnit, Length;

	public SdfTwist( Sdf child, Vec3 center, Vec3 axis, float degreesPerUnit, float length = 0f )
	{
		Child = child;
		Center = center;
		Axis = axis.LengthSquared > 1e-12f ? axis.Normal : new Vec3( 0, 0, 1 );
		DegreesPerUnit = degreesPerUnit;
		Length = MathF.Max( 0f, length );
		Material = child.Material;
		Bone = child.Bone;
		Tag = child.Tag;
	}

	Vec3 Inverse( Vec3 p )
	{
		var t = Vec3.Dot( p - Center, Axis );
		if ( Length > 0f ) t = Math.Clamp( t, -Length, Length );
		return Xform.RotateAbout( Center, Axis, -t * DegreesPerUnit * MathF.PI / 180f ).TransformPoint( p );
	}

	public override float Eval( Vec3 p ) => Child.Eval( Inverse( p ) );

	// A twist never moves a point off its circle about the axis, but the axis-aligned box of
	// a twisted shape is still the box of the untwisted one rotated through the twist range,
	// so pad by half the child's diagonal rather than claim a tight box and clip a corner.
	float Pad => (Child.Max - Child.Min).Length * 0.5f;
	public override Vec3 Min => Child.Min - Vec3.One * Pad;
	public override Vec3 Max => Child.Max + Vec3.One * Pad;
	public override void Prepare( float pad ) => Child.Prepare( pad + Pad );
	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into ) =>
		Child.Collect( Inverse( p ), band, into );
}

/// <summary>
/// Bend a field about an axis: a point offset <c>along</c> along <see cref="BendDirection"/>
/// from <see cref="Center"/> is read from the child rotated by -along/<see cref="Radius"/>
/// radians about the axis, so a straight limb curls into an arc of that radius. BendDirection
/// is orthogonalised against the axis at construction; a radius of zero would be a fold, so
/// it clamps to a hair above zero instead of dividing by it.
/// </summary>
public sealed class SdfBend : Sdf
{
	public readonly Sdf Child;
	public readonly Vec3 Center, Axis, BendDirection;
	public readonly float Radius;

	public SdfBend( Sdf child, Vec3 center, Vec3 axis, Vec3 bendDirection, float radius )
	{
		Child = child;
		Center = center;
		Axis = axis.LengthSquared > 1e-12f ? axis.Normal : new Vec3( 0, 0, 1 );
		var d = bendDirection - Axis * Vec3.Dot( bendDirection, Axis );
		BendDirection = d.LengthSquared > 1e-12f ? d.Normal : new Vec3( 1, 0, 0 );
		Radius = MathF.Max( 1e-3f, radius );
		Material = child.Material;
		Bone = child.Bone;
		Tag = child.Tag;
	}

	Vec3 Inverse( Vec3 p )
	{
		var along = Vec3.Dot( p - Center, BendDirection );
		return Xform.RotateAbout( Center, Axis, -along / Radius ).TransformPoint( p );
	}

	public override float Eval( Vec3 p ) => Child.Eval( Inverse( p ) );
	float Pad => (Child.Max - Child.Min).Length * 0.5f;
	public override Vec3 Min => Child.Min - Vec3.One * Pad;
	public override Vec3 Max => Child.Max + Vec3.One * Pad;
	public override void Prepare( float pad ) => Child.Prepare( pad + Pad );
	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into ) =>
		Child.Collect( Inverse( p ), band, into );
}

/// <summary>
/// Mirror a field across a plane: the field on the far side is the child's reflection. This
/// is a DOMAIN operator, not a union — blend the original with its mirror to get the pair.
/// A mirrored shape's box is the child's box reflected, computed from its eight corners so a
/// diagonal plane does not clip a corner.
/// </summary>
public sealed class SdfMirror : Sdf
{
	public readonly Sdf Child;
	public readonly Vec3 PlanePoint, PlaneNormal;

	public SdfMirror( Sdf child, Vec3 planePoint, Vec3 planeNormal )
	{
		Child = child;
		PlanePoint = planePoint;
		PlaneNormal = planeNormal.LengthSquared > 1e-12f ? planeNormal.Normal : new Vec3( 1, 0, 0 );
		Material = child.Material;
		Bone = child.Bone;
		Tag = child.Tag;
	}

	Vec3 Reflect( Vec3 p ) => p - PlaneNormal * (2f * Vec3.Dot( p - PlanePoint, PlaneNormal ));
	public override float Eval( Vec3 p ) => Child.Eval( Reflect( p ) );

	(Vec3 Lo, Vec3 Hi) ReflectedBounds()
	{
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		var cmin = Child.Min; var cmax = Child.Max;
		for ( var i = 0; i < 8; i++ )
		{
			var c = new Vec3( (i & 1) == 0 ? cmin.x : cmax.x, (i & 2) == 0 ? cmin.y : cmax.y, (i & 4) == 0 ? cmin.z : cmax.z );
			var r = Reflect( c );
			lo = new Vec3( MathF.Min( lo.x, r.x ), MathF.Min( lo.y, r.y ), MathF.Min( lo.z, r.z ) );
			hi = new Vec3( MathF.Max( hi.x, r.x ), MathF.Max( hi.y, r.y ), MathF.Max( hi.z, r.z ) );
		}
		return (lo, hi);
	}

	public override Vec3 Min => ReflectedBounds().Lo;
	public override Vec3 Max => ReflectedBounds().Hi;
	public override void Prepare( float pad ) => Child.Prepare( pad );
	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into ) =>
		Child.Collect( Reflect( p ), band, into );
}

// --- noise -----------------------------------------------------------------------------------

/// <summary>Perlin gradient noise in three dimensions, seeded, in -1..1.</summary>
public sealed class Noise3
{
	readonly int[] _perm = new int[512];

	static readonly Vec3[] Gradients =
	{
		new( 1, 1, 0 ), new( -1, 1, 0 ), new( 1, -1, 0 ), new( -1, -1, 0 ),
		new( 1, 0, 1 ), new( -1, 0, 1 ), new( 1, 0, -1 ), new( -1, 0, -1 ),
		new( 0, 1, 1 ), new( 0, -1, 1 ), new( 0, 1, -1 ), new( 0, -1, -1 ),
		new( 1, 1, 0 ), new( -1, 1, 0 ), new( 0, -1, 1 ), new( 0, -1, -1 ),
	};

	public Noise3( int seed )
	{
		var p = Enumerable.Range( 0, 256 ).ToArray();
		var rng = new Random( seed );

		for ( var i = 255; i > 0; i-- )
		{
			var j = rng.Next( i + 1 );
			(p[i], p[j]) = (p[j], p[i]);
		}

		for ( var i = 0; i < 512; i++ )
			_perm[i] = p[i & 255];
	}

	static float Fade( float t ) => t * t * t * (t * (t * 6f - 15f) + 10f);

	public float Sample( Vec3 v )
	{
		var xi = (int)MathF.Floor( v.x );
		var yi = (int)MathF.Floor( v.y );
		var zi = (int)MathF.Floor( v.z );
		var xf = v.x - xi;
		var yf = v.y - yi;
		var zf = v.z - zi;
		xi &= 255;
		yi &= 255;
		zi &= 255;

		var u = Fade( xf );
		var w = Fade( yf );
		var t = Fade( zf );

		float G( int hash, float x, float y, float z )
		{
			var g = Gradients[hash & 15];
			return g.x * x + g.y * y + g.z * z;
		}

		var aaa = _perm[_perm[_perm[xi] + yi] + zi];
		var aba = _perm[_perm[_perm[xi] + yi + 1] + zi];
		var aab = _perm[_perm[_perm[xi] + yi] + zi + 1];
		var abb = _perm[_perm[_perm[xi] + yi + 1] + zi + 1];
		var baa = _perm[_perm[_perm[xi + 1] + yi] + zi];
		var bba = _perm[_perm[_perm[xi + 1] + yi + 1] + zi];
		var bab = _perm[_perm[_perm[xi + 1] + yi] + zi + 1];
		var bbb = _perm[_perm[_perm[xi + 1] + yi + 1] + zi + 1];

		var x1 = Lerp( G( aaa, xf, yf, zf ), G( baa, xf - 1, yf, zf ), u );
		var x2 = Lerp( G( aba, xf, yf - 1, zf ), G( bba, xf - 1, yf - 1, zf ), u );
		var y1 = Lerp( x1, x2, w );
		x1 = Lerp( G( aab, xf, yf, zf - 1 ), G( bab, xf - 1, yf, zf - 1 ), u );
		x2 = Lerp( G( abb, xf, yf - 1, zf - 1 ), G( bbb, xf - 1, yf - 1, zf - 1 ), u );
		var y2 = Lerp( x1, x2, w );

		return Lerp( y1, y2, t );
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;

	public float Fbm( Vec3 v, int octaves, float gain = 0.5f, float lacunarity = 2f )
	{
		var sum = 0f;
		var amp = 1f;
		var norm = 0f;

		for ( var i = 0; i < octaves; i++ )
		{
			sum += Sample( v ) * amp;
			norm += amp;
			amp *= gain;
			v = v * lacunarity + new Vec3( 17.3f, 31.1f, 7.7f );
		}

		return sum / MathF.Max( norm, 1e-6f );
	}

	/// <summary>Ridged noise: sharp creases, for striations and stretched skin.</summary>
	public float Ridged( Vec3 v, int octaves )
	{
		var sum = 0f;
		var amp = 1f;
		var norm = 0f;

		for ( var i = 0; i < octaves; i++ )
		{
			var n = 1f - MathF.Abs( Sample( v ) );
			sum += n * n * amp;
			norm += amp;
			amp *= 0.5f;
			v = v * 2f + new Vec3( 5.2f, 1.3f, 9.9f );
		}

		return sum / MathF.Max( norm, 1e-6f ) * 2f - 1f;
	}

	public Vec3 Vector( Vec3 v ) => new(
		Sample( v ),
		Sample( v + new Vec3( 101.7f, 43.1f, 7.3f ) ),
		Sample( v + new Vec3( 13.9f, 291.3f, 59.1f ) ) );
}

// --- meshing ---------------------------------------------------------------------------------

/// <summary>
/// Naive surface nets: one vertex per voxel that straddles the surface, at the mean of its edge
/// crossings, and a quad across every grid edge the surface crosses. All quads, closed, and
/// every vertex within half a voxel of the true surface, which is what Catmull-Clark and a
/// smoothing pass want. Sharp features round off at the voxel size; organics have none.
/// </summary>
public static class SurfaceNets
{
	public sealed class Result
	{
		public PolyMesh Mesh;
		/// <summary>The leaf nearest each vertex, parallel to the mesh's positions.</summary>
		public Sdf[] Nearest;
		/// <summary>Auto-computed skin weights, or null when no leaf in the field carries a bone.</summary>
		public SkinWeights Skin;
		public int VoxelsSampled;
		public TimeSpan Sampling, Building;
	}

	/// <summary>
	/// Mesh the field at <paramref name="voxel"/> spacing over its bounds (padded by a voxel).
	/// <paramref name="band"/> is how far from a vertex the labeller looks for the leaf that made
	/// it; a couple of voxels is right.
	/// </summary>
	public static Result Mesh( Sdf sdf, float voxel, float band = -1f, Vec3? boundsMin = null, Vec3? boundsMax = null )
	{
		sdf.Prepare( voxel );

		if ( band < 0f )
			band = voxel * 2.5f;

		var min = (boundsMin ?? sdf.Min) - Vec3.One * voxel * 2f;
		var max = (boundsMax ?? sdf.Max) + Vec3.One * voxel * 2f;
		var size = max - min;

		var nx = Math.Max( 2, (int)MathF.Ceiling( size.x / voxel ) + 1 );
		var ny = Math.Max( 2, (int)MathF.Ceiling( size.y / voxel ) + 1 );
		var nz = Math.Max( 2, (int)MathF.Ceiling( size.z / voxel ) + 1 );

		var clock = System.Diagnostics.Stopwatch.StartNew();
		var values = new float[(long)nx * ny * nz];

		Parallel.For( 0, nx, x =>
		{
			for ( var y = 0; y < ny; y++ )
				for ( var z = 0; z < nz; z++ )
				{
					var p = new Vec3( min.x + x * voxel, min.y + y * voxel, min.z + z * voxel );
					values[((long)x * ny + y) * nz + z] = sdf.Eval( p );
				}
		} );

		var sampling = clock.Elapsed;
		clock.Restart();

		long Index( int x, int y, int z ) => ((long)x * ny + y) * nz + z;

		// One vertex per crossed cell.
		var cellVertex = new int[(long)(nx - 1) * (ny - 1) * (nz - 1)];
		Array.Fill( cellVertex, -1 );
		var positions = new List<Vec3>();

		long Cell( int x, int y, int z ) => ((long)x * (ny - 1) + y) * (nz - 1) + z;

		var corners = new (int dx, int dy, int dz)[]
		{
			(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1),
		};
		var edges = new (int a, int b)[]
		{
			(0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7),
		};

		var cornerValues = new float[8];

		// ONE VERTEX PER PATCH, NOT PER CELL. Where two sheets of surface pass through one cell (a
		// wall thinner than a voxel, two shapes nearly touching) the naive net gave both sheets the
		// same vertex, and every edge out of it then had four faces: non-manifold, which Shell,
		// booleans and export all choke on. The crossed edges of a cell are grouped into the loops
		// marching cubes would draw on its faces, each loop gets its own vertex. On a face with two
		// diagonal inside corners the loops cut each inside corner off — both cells sharing the
		// face read the same four values, so they always agree, and the result is manifold.
		var cornerIndex = new int[8];
		for ( var c = 0; c < 8; c++ ) cornerIndex[corners[c].dx + corners[c].dy * 2 + corners[c].dz * 4] = c;
		var edgeOf = new int[64];
		for ( var e = 0; e < edges.Length; e++ ) { edgeOf[edges[e].a * 8 + edges[e].b] = e; edgeOf[edges[e].b * 8 + edges[e].a] = e; }
		var cubeFaces = new[] { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 3, 2, 6, 7 }, new[] { 0, 3, 7, 4 }, new[] { 1, 2, 6, 5 } };
		var multi = new Dictionary<long, byte[]>();
		var patchOf = new int[12];
		var patchSum = new Vec3[12];
		var patchCount = new int[12];
		var crossedEdge = new int[4];

		int Root( int e ) { while ( patchOf[e] != e ) e = patchOf[e] = patchOf[patchOf[e]]; return e; }

		for ( var x = 0; x < nx - 1; x++ )
			for ( var y = 0; y < ny - 1; y++ )
				for ( var z = 0; z < nz - 1; z++ )
				{
					var inside = 0;

					for ( var c = 0; c < 8; c++ )
					{
						var v = values[Index( x + corners[c].dx, y + corners[c].dy, z + corners[c].dz )];
						cornerValues[c] = v;
						if ( v < 0f )
							inside++;
					}

					if ( inside == 0 || inside == 8 )
						continue;

					for ( var e = 0; e < 12; e++ ) { patchOf[e] = e; patchSum[e] = Vec3.Zero; patchCount[e] = 0; }

					foreach ( var f in cubeFaces )
					{
						// The face's crossed edges join into contour segments. Two crossings are one
						// segment. Four (two diagonal inside corners) are two, each cutting an inside corner off.
						var crossed = 0;
						for ( var k = 0; k < 4; k++ )
							if ( (cornerValues[f[k]] < 0f) != (cornerValues[f[(k + 1) & 3]] < 0f) )
								crossedEdge[crossed++] = edgeOf[f[k] * 8 + f[(k + 1) & 3]];

						if ( crossed == 2 )
						{
							var a = Root( crossedEdge[0] );
							var b = Root( crossedEdge[1] );
							if ( a != b ) patchOf[a] = b;
						}
						else if ( crossed == 4 )
						{
							for ( var k = 0; k < 4; k++ )
							{
								if ( cornerValues[f[k]] >= 0f ) continue;
								var a = Root( edgeOf[f[(k + 3) & 3] * 8 + f[k]] );
								var b = Root( edgeOf[f[k] * 8 + f[(k + 1) & 3]] );
								if ( a != b ) patchOf[a] = b;
							}
						}
					}

					for ( var e = 0; e < 12; e++ )
					{
						var (a, b) = edges[e];
						var va = cornerValues[a];
						var vb = cornerValues[b];

						if ( (va < 0f) == (vb < 0f) )
							continue;

						var t = va / (va - vb);
						var ca = corners[a];
						var cb = corners[b];
						var pa = new Vec3( x + ca.dx, y + ca.dy, z + ca.dz );
						var pb = new Vec3( x + cb.dx, y + cb.dy, z + cb.dz );
						var r = Root( e );
						patchSum[r] += pa + (pb - pa) * t;
						patchCount[r]++;
					}

					var first = positions.Count;
					byte[] map = null;
					var patches = 0;

					for ( var e = 0; e < 12; e++ )
					{
						if ( patchCount[e] == 0 ) continue;
						var local = patchSum[e] / patchCount[e];
						positions.Add( new Vec3( min.x + local.x * voxel, min.y + local.y * voxel, min.z + local.z * voxel ) );
						patchCount[e] = -(++patches); // remember the patch's number on its root
					}

					cellVertex[Cell( x, y, z )] = first;

					if ( patches > 1 )
					{
						map = new byte[12];
						for ( var e = 0; e < 12; e++ )
						{
							var r = Root( e );
							if ( patchCount[r] < 0 ) map[e] = (byte)(-patchCount[r] - 1);
						}

						multi[Cell( x, y, z )] = map;
					}
				}

		// The vertex a cell has for one grid edge: the edge's two corners, in the cell's own numbering.
		int At( int cx, int cy, int cz, int gx, int gy, int gz, int ax, int ay, int az )
		{
			var cell = Cell( cx, cy, cz );
			var v = cellVertex[cell];
			if ( v < 0 || !multi.TryGetValue( cell, out var map ) ) return v;
			var a = cornerIndex[(gx - cx) + (gy - cy) * 2 + (gz - cz) * 4];
			var b = cornerIndex[(gx + ax - cx) + (gy + ay - cy) * 2 + (gz + az - cz) * 4];
			return v + map[edgeOf[a * 8 + b]];
		}

		// A quad across every crossed grid edge, wound so the normal points out of the solid.
		var faces = new List<Face>();

		void Quad( int c00, int c10, int c11, int c01, bool flip )
		{
			if ( c00 < 0 || c10 < 0 || c11 < 0 || c01 < 0 )
				return;

			faces.Add( flip
				? new Face( new[] { c01, c11, c10, c00 } )
				: new Face( new[] { c00, c10, c11, c01 } ) );
		}

		for ( var x = 0; x < nx; x++ )
			for ( var y = 0; y < ny; y++ )
				for ( var z = 0; z < nz; z++ )
				{
					var v0 = values[Index( x, y, z )];
					var in0 = v0 < 0f;

					// Edge along x, cells vary in (y, z).
					if ( x + 1 < nx && y > 0 && z > 0 && y < ny - 1 && z < nz - 1 )
					{
						var in1 = values[Index( x + 1, y, z )] < 0f;
						if ( in0 != in1 )
							Quad( At( x, y - 1, z - 1, x, y, z, 1, 0, 0 ), At( x, y, z - 1, x, y, z, 1, 0, 0 ),
								At( x, y, z, x, y, z, 1, 0, 0 ), At( x, y - 1, z, x, y, z, 1, 0, 0 ), flip: !in0 );
					}

					// Edge along y, cells vary in (z, x).
					if ( y + 1 < ny && z > 0 && x > 0 && z < nz - 1 && x < nx - 1 )
					{
						var in1 = values[Index( x, y + 1, z )] < 0f;
						if ( in0 != in1 )
							Quad( At( x - 1, y, z - 1, x, y, z, 0, 1, 0 ), At( x - 1, y, z, x, y, z, 0, 1, 0 ),
								At( x, y, z, x, y, z, 0, 1, 0 ), At( x, y, z - 1, x, y, z, 0, 1, 0 ), flip: !in0 );
					}

					// Edge along z, cells vary in (x, y).
					if ( z + 1 < nz && x > 0 && y > 0 && x < nx - 1 && y < ny - 1 )
					{
						var in1 = values[Index( x, y, z + 1 )] < 0f;
						if ( in0 != in1 )
							Quad( At( x - 1, y - 1, z, x, y, z, 0, 0, 1 ), At( x, y - 1, z, x, y, z, 0, 0, 1 ),
								At( x, y, z, x, y, z, 0, 0, 1 ), At( x - 1, y, z, x, y, z, 0, 0, 1 ), flip: !in0 );
					}
				}

		DropSpecks( positions, faces, voxel );

		var mesh = new PolyMesh( positions, faces );

		var nearest = new Sdf[positions.Count];
		// A big blend pushes the skin further than the band from every leaf that made it. Such a vertex
		// used to come back unlabelled and the caller's default slot showed through as a ragged
		// fringe where the blend was deepest; widen the search until something owns it.
		var reach = (sdf.Max - sdf.Min).Length + band;
		Parallel.For( 0, positions.Count, i =>
		{
			var wide = band;
			var leaf = sdf.Nearest( positions[i], wide );
			while ( leaf is null && wide < reach )
			{
				wide *= 2f;
				leaf = sdf.Nearest( positions[i], wide );
			}
			nearest[i] = leaf;
		} );

		// AUTO-SKIN, Lane C: when the field's leaves carry bones, the meshed surface comes
		// out pre-weighted instead of making the caller remember to call Weights() — and
		// forgetting it was exactly how fused Blob creatures used to arrive unrigged. The
		// fallback is the bone most vertices already belong to, never -1, so no vertex ends
		// up weighted to a bone that does not exist. A boneless field is untouched: Skin
		// stays null, exactly as every existing caller expects.
		SkinWeights skin = null;
		{
			var counts = new Dictionary<int, int>();
			foreach ( var leaf in nearest )
				if ( leaf is not null && leaf.Bone >= 0 )
					counts[leaf.Bone] = counts.TryGetValue( leaf.Bone, out var c ) ? c + 1 : 1;
			if ( counts.Count > 0 )
			{
				var fallback = counts.OrderByDescending( kv => kv.Value ).First().Key;
				skin = Weights( sdf, mesh, band, fallback );
				mesh.Skin = skin;
			}
		}

		return new Result
		{
			Mesh = mesh,
			Nearest = nearest,
			Skin = skin,
			VoxelsSampled = nx * ny * nz,
			Sampling = sampling,
			Building = clock.Elapsed,
		};
	}

	/// <summary>
	/// Take out islands smaller than two voxels across, with their vertices.
	///
	/// A lone inside sample (a noise peak, a field that just grazes zero) meshes as a closed cube
	/// one voxel wide - six quads, perfectly legal - and smoothing then shrinks it to six faces of
	/// zero area on one point. Nothing can see it, and it still exported, broke Shell ("face 1363
	/// has no inner surface left") and counted as a second part. Detail that small is below what
	/// the voxel can represent anyway, so the speck is dropped rather than kept as a point.
	/// </summary>
	static void DropSpecks( List<Vec3> positions, List<Face> faces, float voxel )
	{
		if ( faces.Count == 0 )
			return;

		var parent = new int[positions.Count];
		for ( var i = 0; i < parent.Length; i++ ) parent[i] = i;

		int Find( int v )
		{
			while ( parent[v] != v ) { parent[v] = parent[parent[v]]; v = parent[v]; }
			return v;
		}

		foreach ( var f in faces )
			for ( var i = 1; i < f.Indices.Length; i++ )
			{
				var a = Find( f.Indices[0] );
				var b = Find( f.Indices[i] );
				if ( a != b ) parent[a] = b;
			}

		var lo = new Dictionary<int, Vec3>();
		var hi = new Dictionary<int, Vec3>();

		foreach ( var f in faces )
			foreach ( var v in f.Indices )
			{
				var r = Find( v );
				var p = positions[v];
				lo[r] = lo.TryGetValue( r, out var l ) ? new Vec3( MathF.Min( l.x, p.x ), MathF.Min( l.y, p.y ), MathF.Min( l.z, p.z ) ) : p;
				hi[r] = hi.TryGetValue( r, out var h ) ? new Vec3( MathF.Max( h.x, p.x ), MathF.Max( h.y, p.y ), MathF.Max( h.z, p.z ) ) : p;
			}

		var small = new HashSet<int>();

		foreach ( var (r, l) in lo )
		{
			var size = hi[r] - l;
			if ( MathF.Max( size.x, MathF.Max( size.y, size.z ) ) < voxel * 2f )
				small.Add( r );
		}

		// Never the whole mesh: a field that is all speck is still an answer the caller should see.
		if ( small.Count == 0 || small.Count == lo.Count )
			return;

		faces.RemoveAll( f => small.Contains( Find( f.Indices[0] ) ) );

		var remap = new int[positions.Count];
		Array.Fill( remap, -1 );
		var kept = new List<Vec3>();

		foreach ( var f in faces )
			foreach ( var v in f.Indices )
				if ( remap[v] < 0 ) { remap[v] = -2; }

		for ( var v = 0; v < positions.Count; v++ )
			if ( remap[v] == -2 ) { remap[v] = kept.Count; kept.Add( positions[v] ); }

		foreach ( var f in faces )
			for ( var i = 0; i < f.Indices.Length; i++ )
				f.Indices[i] = remap[f.Indices[i]];

		positions.Clear();
		positions.AddRange( kept );
	}

	/// <summary>
	/// Skin weights from the field: every leaf within <paramref name="band"/> of a vertex pulls
	/// it toward that leaf's bone, falling off with distance, so a tendril's root blends into the
	/// mass it grows from and its tip is all its own. Vertices no leaf claims go to
	/// <paramref name="fallbackBone"/>.
	/// </summary>
	public static SkinWeights Weights( Sdf sdf, PolyMesh mesh, float band, int fallbackBone, float softness = 0.35f )
	{
		var weights = new SkinWeights( mesh.VertexCount );
		var n = mesh.VertexCount;
		var result = new BoneWeight[n][];

		Parallel.For( 0, n, i =>
		{
			var list = new List<(Sdf Leaf, float Distance)>();
			sdf.Collect( mesh.Positions[i], band, list );

			var byBone = new Dictionary<int, float>();

			foreach ( var (leaf, d) in list )
			{
				if ( leaf.Bone < 0 )
					continue;

				var w = MathF.Exp( -d / MathF.Max( band * softness, 1e-4f ) );
				byBone[leaf.Bone] = byBone.TryGetValue( leaf.Bone, out var prev ) ? MathF.Max( prev, w ) : w;
			}

			if ( byBone.Count == 0 )
			{
				result[i] = new[] { new BoneWeight( fallbackBone, 1f ) };
				return;
			}

			var arr = byBone.Select( kv => new BoneWeight( kv.Key, kv.Value ) ).ToArray();
			arr = SkinWeights.Prune( arr, 4 );
			var total = arr.Sum( b => b.Weight );
			result[i] = arr.Select( b => new BoneWeight( b.Bone, b.Weight / total ) ).ToArray();
		} );

		for ( var i = 0; i < n; i++ )
			weights.Vertices[i] = result[i];

		return weights;
	}
}

/// <summary>Taubin smoothing: a shrink step and an inflate step, so the mesh loses its voxel
/// stairs without losing its volume.</summary>
public static class MeshSmooth
{
	public static void Taubin( PolyMesh mesh, int iterations = 4, float lambda = 0.5f, float mu = -0.53f )
	{
		var neighbours = Neighbours( mesh );

		for ( var it = 0; it < iterations; it++ )
		{
			Step( mesh, neighbours, lambda );
			Step( mesh, neighbours, mu );
		}
	}

	/// <summary>Plain Laplacian smoothing, which does shrink; for relaxing stairs when a little
	/// loss of volume is fine.</summary>
	public static void Laplacian( PolyMesh mesh, int iterations = 2, float strength = 0.5f )
	{
		var neighbours = Neighbours( mesh );

		for ( var it = 0; it < iterations; it++ )
			Step( mesh, neighbours, strength );
	}

	static void Step( PolyMesh mesh, int[][] neighbours, float factor )
	{
		var positions = mesh.Positions;
		var next = new Vec3[positions.Count];

		Parallel.For( 0, positions.Count, i =>
		{
			var nb = neighbours[i];

			if ( nb.Length == 0 )
			{
				next[i] = positions[i];
				return;
			}

			var sum = Vec3.Zero;
			foreach ( var j in nb )
				sum += positions[j];

			next[i] = positions[i] + (sum / nb.Length - positions[i]) * factor;
		} );

		for ( var i = 0; i < positions.Count; i++ )
			positions[i] = next[i];
	}

	public static int[][] Neighbours( PolyMesh mesh )
	{
		var sets = new HashSet<int>[mesh.VertexCount];

		foreach ( var face in mesh.Faces )
		{
			var idx = face.Indices;

			for ( var i = 0; i < idx.Length; i++ )
			{
				var a = idx[i];
				var b = idx[(i + 1) % idx.Length];
				(sets[a] ??= new HashSet<int>()).Add( b );
				(sets[b] ??= new HashSet<int>()).Add( a );
			}
		}

		var result = new int[mesh.VertexCount][];
		for ( var i = 0; i < result.Length; i++ )
			result[i] = sets[i]?.ToArray() ?? Array.Empty<int>();

		return result;
	}
}
