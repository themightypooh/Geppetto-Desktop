#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// P23: self-contained 3D collider shapes for particles. Pure geometry, no sim
// state, no RNG, no wiring into ParticleSystem — the coordinator sweep owns
// that hookup (see the integration note at the bottom of this file).
//
// Conventions, matching ParticleSystem.cs: kernel inches, Z-up, seconds.
// A particle is a sphere of the given radius. Every Sweep takes the swept
// segment previous → current position, so a fast particle cannot tunnel
// through a thin shape between substeps; a point-only test at the current
// position would miss exactly that case (see the tunnelling test).
//
// Shape semantics (uniform across all four shapes):
// - Each shape is a SOLID. The particle's radius expands the shape: sphere /
//   capsule radii grow by r, a box grows by r on every face, a plane-set
//   volume expands by r (each plane moves r outward: the centre is in
//   contact when its signed distance to a face equals +r).
// - Sweep returns the earliest boundary contact along the segment. Hit.Point
//   is the particle-CENTRE position at contact (already radius-corrected,
//   so Resolve snaps the centre to Hit.Point — the analogue of
//   ParticleSystem's mesh branch `next = hit + n * Radius`, where hit is a
//   surface point, and its ground branch pushing the centre to plane + Radius).
// - Hit.Normal is the OUTWARD surface normal (unit length) at contact.
// - If the segment starts inside the expanded shape, Sweep reports a hit at
//   Time 0 at the previous position, with the outward normal of the closest
//   boundary feature: the particle is already in contact.
// - Misses carry Hit = false; Point / Normal / Time are meaningless then.
// - Room interiors: a plane-set volume is a solid, so a room is the same
//   planes in inside-out mode (see Flipped()): the free space is inside
//   the volume, and Sweep reports the earliest EXIT crossing, with the
//   normal pointing back into the room, so Resolve bounces the particle
//   back inside. Note negating the planes would NOT do this: the
//   intersection of negated half-spaces is empty for a bounded volume,
//   not the complement.

/// <summary>One swept-collision result. Hit = false means no contact along the segment.</summary>
public readonly record struct ColliderHit( bool Hit, Vector3 Point, Vector3 Normal, float Time )
{
	public static ColliderHit Miss => new( false, Vector3.Zero, Vector3.Zero, 0f );
}

/// <summary>Common surface for the P23 collider shapes, so a future sim step can hold a mixed list.</summary>
public interface IParticleCollider
{
	ColliderHit Sweep( Vector3 previous, Vector3 current, float particleRadius );
	bool Contains( Vector3 point, float particleRadius );
	void Validate();
}

/// <summary>Shared swept-segment helpers. Internal: the shape classes are the public surface.</summary>
internal static class ColliderMath
{
	public static void CheckSegment( Vector3 previous, Vector3 current, float particleRadius )
	{
		if ( !ParticleMath.Finite( previous ) || !ParticleMath.Finite( current ) )
			throw new ArgumentException( "Swept segment endpoints must be finite." );
		if ( !float.IsFinite( particleRadius ) || particleRadius < 0 )
			throw new ArgumentOutOfRangeException( nameof( particleRadius ), "Particle radius must be >= 0." );
	}

	/// <summary>Closest point on segment AB to p, plus its parameter (0 = A, 1 = B).</summary>
	public static Vector3 ClosestOnSegment( Vector3 p, Vector3 a, Vector3 b, out float s )
	{
		var ab = b - a;
		float l2 = Vector3.Dot( ab, ab );
		s = l2 < 1e-12f ? 0f : Vector3.Dot( p - a, ab ) / l2;
		if ( s < 0 ) s = 0;
		if ( s > 1 ) s = 1;
		return a + ab * s;
	}

	/// <summary>Normal from the closest boundary point: (p - closest) normalized, with a Z-up fallback at the degenerate centre.</summary>
	public static Vector3 OutwardNormal( Vector3 p, Vector3 closest )
	{
		var n = p - closest;
		float l2 = Vector3.Dot( n, n );
		return l2 < 1e-12f ? new Vector3( 0, 0, 1 ) : n / MathF.Sqrt( l2 );
	}

	/// <summary>
	/// Earliest t in [0, 1] where segment previous → current touches the sphere
	/// (centre, radius). Returns false when the segment never reaches it.
	/// A start inside the sphere reports t = 0 at the start point.
	/// </summary>
	public static bool SegmentSphere( Vector3 previous, Vector3 current, Vector3 centre, float radius, out float t, out Vector3 point )
	{
		t = 0;
		point = previous;
		var d = current - previous;
		var m = previous - centre;
		float r2 = radius * radius;
		if ( Vector3.Dot( m, m ) <= r2 ) return true; // already in contact: t = 0 at previous
		float a = Vector3.Dot( d, d );
		if ( a < 1e-12f ) return false; // zero-length step, starting outside
		float b = 2f * Vector3.Dot( d, m );
		float c = Vector3.Dot( m, m ) - r2;
		float disc = b * b - 4f * a * c;
		if ( disc < 0 ) return false;
		t = (-b - MathF.Sqrt( disc )) / (2f * a);
		if ( t < 0 || t > 1 ) return false;
		point = previous + d * t;
		return true;
	}
}

/// <summary>A solid sphere collider: centre + radius, inches, Z-up.</summary>
public sealed class ParticleSphereCollider : IParticleCollider
{
	public Vector3 Center;
	public float Radius;

	public void Validate()
	{
		if ( !ParticleMath.Finite( Center ) )
			throw new ArgumentException( "Sphere collider centre must be finite." );
		if ( !float.IsFinite( Radius ) || Radius < 0 )
			throw new ArgumentOutOfRangeException( nameof( Radius ), "Sphere collider radius must be >= 0." );
	}

	public bool Contains( Vector3 point, float particleRadius )
	{
		Validate();
		ColliderMath.CheckSegment( point, point, particleRadius );
		float r = Radius + particleRadius;
		return Vector3.Dot( point - Center, point - Center ) <= r * r;
	}

	public ColliderHit Sweep( Vector3 previous, Vector3 current, float particleRadius )
	{
		Validate();
		ColliderMath.CheckSegment( previous, current, particleRadius );
		float r = Radius + particleRadius;
		if ( !ColliderMath.SegmentSphere( previous, current, Center, r, out float t, out var point ) )
			return ColliderHit.Miss;
		var normal = ColliderMath.OutwardNormal( point, Center );
		return new ColliderHit( true, point, normal, t );
	}
}

/// <summary>An axis-aligned solid box collider. Normal = outward normal of the contacted face.</summary>
public sealed class ParticleBoxCollider : IParticleCollider
{
	public Vector3 Min;
	public Vector3 Max;

	public void Validate()
	{
		if ( !ParticleMath.Finite( Min ) || !ParticleMath.Finite( Max ) )
			throw new ArgumentException( "Box collider corners must be finite." );
		if ( Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z )
			throw new ArgumentException( "Box collider is inverted: Min must be <= Max on every axis." );
	}

	public bool Contains( Vector3 point, float particleRadius )
	{
		Validate();
		ColliderMath.CheckSegment( point, point, particleRadius );
		var lo = Min - new Vector3( particleRadius );
		var hi = Max + new Vector3( particleRadius );
		return point.X >= lo.X && point.X <= hi.X && point.Y >= lo.Y && point.Y <= hi.Y && point.Z >= lo.Z && point.Z <= hi.Z;
	}

	public ColliderHit Sweep( Vector3 previous, Vector3 current, float particleRadius )
	{
		Validate();
		ColliderMath.CheckSegment( previous, current, particleRadius );
		var lo = Min - new Vector3( particleRadius );
		var hi = Max + new Vector3( particleRadius );

		// Already inside the expanded box: contact at t = 0, normal = closest face.
		if ( previous.X >= lo.X && previous.X <= hi.X && previous.Y >= lo.Y && previous.Y <= hi.Y && previous.Z >= lo.Z && previous.Z <= hi.Z )
		{
			float dx0 = previous.X - lo.X, dx1 = hi.X - previous.X;
			float dy0 = previous.Y - lo.Y, dy1 = hi.Y - previous.Y;
			float dz0 = previous.Z - lo.Z, dz1 = hi.Z - previous.Z;
			var normal = new Vector3( -1, 0, 0 );
			float best = dx0;
			if ( dx1 < best ) { best = dx1; normal = new Vector3( 1, 0, 0 ); }
			if ( dy0 < best ) { best = dy0; normal = new Vector3( 0, -1, 0 ); }
			if ( dy1 < best ) { best = dy1; normal = new Vector3( 0, 1, 0 ); }
			if ( dz0 < best ) { best = dz0; normal = new Vector3( 0, 0, -1 ); }
			if ( dz1 < best ) { best = dz1; normal = new Vector3( 0, 0, 1 ); }
			return new ColliderHit( true, previous, normal, 0f );
		}

		// Slab method on the expanded box; entry face gives the normal.
		var d = current - previous;
		float tEnter = 0f, tExit = 1f;
		var enterNormal = Vector3.Zero;
		for ( int axis = 0; axis < 3; axis++ )
		{
			float p0 = axis == 0 ? previous.X : axis == 1 ? previous.Y : previous.Z;
			float dd = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
			float mn = axis == 0 ? lo.X : axis == 1 ? lo.Y : lo.Z;
			float mx = axis == 0 ? hi.X : axis == 1 ? hi.Y : hi.Z;
			if ( MathF.Abs( dd ) < 1e-12f )
			{
				if ( p0 < mn || p0 > mx ) return ColliderHit.Miss;
				continue;
			}
			float t0 = (mn - p0) / dd, t1 = (mx - p0) / dd;
			// Entry face from the direction of travel alone: moving +axis
			// enters through the min face (-axis normal), moving -axis
			// through the max face (+axis normal). The swap below only
			// orders the times; it must NOT flip this normal.
			var axisNormal = dd > 0 ? -Axis( axis ) : Axis( axis );
			if ( t0 > t1 ) { (t0, t1) = (t1, t0); }
			if ( t0 > tEnter ) { tEnter = t0; enterNormal = axisNormal; }
			if ( t1 < tExit ) tExit = t1;
			if ( tEnter > tExit ) return ColliderHit.Miss;
		}
		if ( tEnter > 1 || tExit < 0 ) return ColliderHit.Miss;
		if ( tEnter < 0 ) tEnter = 0; // unreachable after the inside check, kept for safety
		return new ColliderHit( true, previous + d * tEnter, enterNormal, tEnter );
	}

	static Vector3 Axis( int axis ) => axis == 0 ? new Vector3( 1, 0, 0 ) : axis == 1 ? new Vector3( 0, 1, 0 ) : new Vector3( 0, 0, 1 );
}

/// <summary>
/// One plane of a convex volume. Signed distance is Dot(Normal, x) − Distance;
/// inside the volume means on the negative side of EVERY plane (signed distance &lt;= 0).
/// Normals need not be unit length: Sweep normalizes each plane on the fly
/// (Distance scales with the normal), so a mutated list stays valid.
/// </summary>
public readonly record struct ColliderPlane( Vector3 Normal, float Distance );

/// <summary>A convex solid volume defined by a list of planes (rooms and simple hulls).</summary>
public sealed class ParticlePlaneSetCollider : IParticleCollider
{
	public List<ColliderPlane> Planes = new();

	/// <summary>
	/// Inside-out mode: the free space is INSIDE the plane volume (a room
	/// interior). Sweep then reports the earliest exit crossing, with the
	/// normal pointing back inside, instead of an entry hit. See Flipped().
	/// </summary>
	public bool InsideOut;

	public void Validate()
	{
		if ( Planes.Count == 0 )
			throw new ArgumentException( "Plane-set collider needs at least one plane." );
		for ( int i = 0; i < Planes.Count; i++ )
		{
			if ( !ParticleMath.Finite( Planes[i].Normal ) || !float.IsFinite( Planes[i].Distance ) )
				throw new ArgumentException( $"Plane-set collider plane {i} must be finite." );
			if ( Planes[i].Normal.LengthSquared() < 1e-12f )
				throw new ArgumentException( $"Plane-set collider plane {i} normal must be non-zero." );
		}
	}

	/// <summary>
	/// Room-interior form of this volume: same planes, InsideOut = true.
	/// A particle inside the room collides when it tries to leave through
	/// a wall; the hit normal points back into the room.
	/// </summary>
	public ParticlePlaneSetCollider Flipped()
	{
		var flipped = new ParticlePlaneSetCollider { InsideOut = true };
		foreach ( var p in Planes ) flipped.Planes.Add( p );
		return flipped;
	}

	// Signed distance to plane i at x, expanded by the particle radius:
	// the centre is inside the expanded solid iff this is <= 0, i.e. the
	// raw signed distance is <= +r (contact at exactly +r outside a face).
	// Normal is normalized here so stored planes may be any non-zero scale.
	float Signed( int i, Vector3 x, float particleRadius )
	{
		var p = Planes[i];
		float len = p.Normal.Length();
		return (Vector3.Dot( p.Normal, x ) - p.Distance) / len - particleRadius;
	}

	// Room-mode clearance: the centre is clear of wall i iff the raw signed
	// distance is <= -r (r away from the wall, inside the room).
	float Clearance( int i, Vector3 x, float particleRadius )
	{
		var p = Planes[i];
		float len = p.Normal.Length();
		return (Vector3.Dot( p.Normal, x ) - p.Distance) / len + particleRadius;
	}

	Vector3 UnitNormal( int i ) => Vector3.Normalize( Planes[i].Normal );

	public bool Contains( Vector3 point, float particleRadius )
	{
		Validate();
		ColliderMath.CheckSegment( point, point, particleRadius );
		for ( int i = 0; i < Planes.Count; i++ )
			if ( (InsideOut ? Clearance( i, point, particleRadius ) : Signed( i, point, particleRadius )) > 0 ) return false;
		return true;
	}

	public ColliderHit Sweep( Vector3 previous, Vector3 current, float particleRadius )
	{
		Validate();
		ColliderMath.CheckSegment( previous, current, particleRadius );
		if ( InsideOut ) return SweepRoom( previous, current, particleRadius );
		return SweepSolid( previous, current, particleRadius );
	}

	ColliderHit SweepSolid( Vector3 previous, Vector3 current, float particleRadius )
	{
		// Already inside the expanded volume: contact at t = 0, normal = the
		// plane the centre is closest to exiting through (largest signed distance).
		bool inside = true;
		int closest = 0;
		float closestD = float.NegativeInfinity;
		for ( int i = 0; i < Planes.Count; i++ )
		{
			float sd = Signed( i, previous, particleRadius );
			if ( sd > 0 ) inside = false;
			if ( sd > closestD ) { closestD = sd; closest = i; }
		}
		if ( inside ) return new ColliderHit( true, previous, UnitNormal( closest ), 0f );

		// Convex entry: latest entry time over entered planes, earliest exit
		// over exited planes; a hit needs entry before exit. A plane the
		// segment stays outside of the whole time means it can never be
		// inside the intersection at all: instant miss.
		float tEnter = -1f, tExit = 1f;
		int enterPlane = -1;
		for ( int i = 0; i < Planes.Count; i++ )
		{
			float d0 = Signed( i, previous, particleRadius );
			float d1 = Signed( i, current, particleRadius );
			if ( d0 > 0 && d1 > 0 ) return ColliderHit.Miss;
			if ( d0 > 0 && d1 <= 0 )
			{
				float t = d0 / (d0 - d1);
				if ( t > tEnter ) { tEnter = t; enterPlane = i; }
			}
			else if ( d0 <= 0 && d1 > 0 )
			{
				float t = d0 / (d0 - d1);
				if ( t < tExit ) tExit = t;
			}
		}
		if ( enterPlane < 0 || tEnter > tExit || tEnter > 1 ) return ColliderHit.Miss;
		var d = current - previous;
		return new ColliderHit( true, previous + d * tEnter, UnitNormal( enterPlane ), tEnter );
	}

	ColliderHit SweepRoom( Vector3 previous, Vector3 current, float particleRadius )
	{
		// Already touching a wall (clearance violated): contact at t = 0,
		// normal points back into the room (negated wall normal). This also
		// covers a particle already past a wall: Resolve bounces it back
		// toward the room instead of letting it drift away outside.
		int worst = 0;
		float worstD = float.NegativeInfinity;
		for ( int i = 0; i < Planes.Count; i++ )
		{
			float e = Clearance( i, previous, particleRadius );
			if ( e > worstD ) { worstD = e; worst = i; }
		}
		if ( worstD > 0 ) return new ColliderHit( true, previous, -UnitNormal( worst ), 0f );

		// Earliest exit crossing: the first wall the centre reaches.
		float tExit = float.PositiveInfinity;
		int exitPlane = -1;
		for ( int i = 0; i < Planes.Count; i++ )
		{
			float e0 = Clearance( i, previous, particleRadius );
			float e1 = Clearance( i, current, particleRadius );
			if ( e0 <= 0 && e1 > 0 )
			{
				float t = e0 / (e0 - e1);
				if ( t < tExit ) { tExit = t; exitPlane = i; }
			}
		}
		if ( exitPlane < 0 || tExit > 1 ) return ColliderHit.Miss;
		var d = current - previous;
		return new ColliderHit( true, previous + d * tExit, -UnitNormal( exitPlane ), tExit );
	}
}

/// <summary>A capsule collider: segment A → B plus radius (cylinder + hemispherical caps).</summary>
public sealed class ParticleCapsuleCollider : IParticleCollider
{
	public Vector3 A;
	public Vector3 B;
	public float Radius;

	public void Validate()
	{
		if ( !ParticleMath.Finite( A ) || !ParticleMath.Finite( B ) )
			throw new ArgumentException( "Capsule collider endpoints must be finite." );
		if ( !float.IsFinite( Radius ) || Radius < 0 )
			throw new ArgumentOutOfRangeException( nameof( Radius ), "Capsule collider radius must be >= 0." );
		if ( (B - A).LengthSquared() < 1e-12f )
			throw new ArgumentException( "Capsule collider segment must be non-zero length." );
	}

	public bool Contains( Vector3 point, float particleRadius )
	{
		Validate();
		ColliderMath.CheckSegment( point, point, particleRadius );
		var closest = ColliderMath.ClosestOnSegment( point, A, B, out _ );
		float r = Radius + particleRadius;
		return Vector3.Dot( point - closest, point - closest ) <= r * r;
	}

	public ColliderHit Sweep( Vector3 previous, Vector3 current, float particleRadius )
	{
		Validate();
		ColliderMath.CheckSegment( previous, current, particleRadius );
		float r = Radius + particleRadius;

		// Already in contact: t = 0, normal away from the closest segment point.
		var closest0 = ColliderMath.ClosestOnSegment( previous, A, B, out _ );
		if ( Vector3.Dot( previous - closest0, previous - closest0 ) <= r * r )
			return new ColliderHit( true, previous, ColliderMath.OutwardNormal( previous, closest0 ), 0f );

		var d = current - previous;
		float bestT = float.PositiveInfinity;
		Vector3 bestPoint = previous;

		// Hemispherical caps: swept sphere vs each endpoint sphere.
		foreach ( var end in new[] { A, B } )
			if ( ColliderMath.SegmentSphere( previous, current, end, r, out float tCap, out var pCap ) && tCap < bestT )
			{
				bestT = tCap;
				bestPoint = pCap;
			}

		// Cylinder side: distance from the moving centre to the infinite line,
		// earliest root with the closest parameter inside [0, 1].
		var u = B - A;
		float l2 = Vector3.Dot( u, u );
		var w0 = previous - A;
		float a = Vector3.Dot( d, d ) - Vector3.Dot( d, u ) * Vector3.Dot( d, u ) / l2;
		if ( a >= 1e-12f )
		{
			float b = 2f * (Vector3.Dot( w0, d ) - Vector3.Dot( w0, u ) * Vector3.Dot( d, u ) / l2);
			float c = Vector3.Dot( w0, w0 ) - Vector3.Dot( w0, u ) * Vector3.Dot( w0, u ) / l2 - r * r;
			float disc = b * b - 4f * a * c;
			if ( disc >= 0 )
			{
				float t = (-b - MathF.Sqrt( disc )) / (2f * a);
				if ( t >= 0 && t <= 1 && t < bestT )
				{
					var p = previous + d * t;
					ColliderMath.ClosestOnSegment( p, A, B, out float s );
					if ( s > 0 && s < 1 ) { bestT = t; bestPoint = p; }
				}
			}
		}

		if ( float.IsPositiveInfinity( bestT ) ) return ColliderHit.Miss;
		var closest = ColliderMath.ClosestOnSegment( bestPoint, A, B, out _ );
		return new ColliderHit( true, bestPoint, ColliderMath.OutwardNormal( bestPoint, closest ), bestT );
	}
}

/// <summary>
/// Collision response, mirroring ParticleSystem.Integrate's private Respond
/// exactly (read from ParticleSystem.cs, not re-derived):
/// vn = velocity · normal; if vn &gt;= 0 (already separating) velocity is
/// unchanged. Otherwise the tangential part is kept at (1 − friction) and
/// the normal part is reflected at restitution:
/// velocity = tangential * (1 − friction) − normal * (vn * restitution).
/// Restitution / friction ranges match ParticleSystemSettings.Validate: 0..1.
/// Position snaps to the contact centre: a hit Point is already the
/// radius-corrected centre position (see the file header), so no extra
/// radius push-out is applied here. A miss returns the inputs unchanged.
/// </summary>
public static class ParticleColliderResponse
{
	public static (Vector3 Position, Vector3 Velocity) Resolve( Vector3 position, Vector3 velocity, ColliderHit hit, float restitution, float friction )
	{
		if ( !ParticleMath.Finite( position ) || !ParticleMath.Finite( velocity ) )
			throw new ArgumentException( "Resolve position/velocity must be finite." );
		if ( !float.IsFinite( restitution ) || restitution < 0 || restitution > 1 )
			throw new ArgumentOutOfRangeException( nameof( restitution ), "Restitution must be 0..1." );
		if ( !float.IsFinite( friction ) || friction < 0 || friction > 1 )
			throw new ArgumentOutOfRangeException( nameof( friction ), "Friction must be 0..1." );
		if ( !hit.Hit ) return (position, velocity);
		if ( !ParticleMath.Finite( hit.Point ) || !ParticleMath.Finite( hit.Normal ) || hit.Normal.LengthSquared() < 1e-12f )
			throw new ArgumentException( "Resolve needs a hit with a finite point and non-zero normal." );
		var n = Vector3.Normalize( hit.Normal );
		float vn = Vector3.Dot( velocity, n );
		if ( vn >= 0 ) return (hit.Point, velocity);
		var vt = velocity - n * vn;
		return (hit.Point, vt * (1f - friction) - n * (vn * restitution));
	}
}

// ---- Coordinator sweep: sim-step hookup (NOT made by this lane) ----
//
// ParticleSystem.cs Integrate currently collides in this order: mesh
// (Raycast + Respond), then ground plane (push-out + Respond). Colliders
// belong between those two blocks, so mesh keeps first-contact reporting
// and the ground stays the last word. The sweep's edit, in Integrate
// after the mesh block and before `if ( p.Ground )`:
//
//   if ( p.Colliders is not null )
//       foreach ( var collider in p.Colliders )
//       {
//           var hit = collider.Sweep( pos, next, p.Radius );
//           if ( !hit.Hit ) continue;
//           if ( !collision.Hit ) collision = new ParticleCollisionHit( true, hit.Point, hit.Normal, vel, ParticleCollisionKind.Mesh );
//           (next, vel) = ParticleColliderResponse.Resolve( next, vel, hit, p.Restitution, p.Friction );
//       }
//
// That edit ALSO needs, in the sweep's hands (not this lane's files):
// - a `List<IParticleCollider> Colliders` field on ParticleSystemSettings
//   (validated by calling each collider's Validate()),
// - a matching field on SimParams, and
// - a decision on ParticleCollisionKind: either a new Collider kind value
//   or reuse of Mesh for reporting. This lane deliberately does not pick
//   it: ParticleCollisionKind lives in ParticleSystem.cs, another lane's
//   edit surface under the freeze.
// The shape classes above are pure and need no sim changes to be tested.
