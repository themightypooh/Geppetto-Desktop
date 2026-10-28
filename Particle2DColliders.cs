#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// P13: self-contained 2D collider SHAPES for particles. Detection only —
// this file never changes a velocity or a position. Responses (Bounce /
// Sticky / Slide) are P76's Particle2DResponse.cs, which owns what happens
// AFTER a hit is known. This file never touches Particle2D.cs (lane 1's
// sim, including its floor line: these colliders sit BESIDE the floor,
// they do not replace it), ParticleColliders.cs (P23, 3D), or
// Particle2DResponse.cs (P76). Pure geometry, no sim state, no RNG, no
// wiring into the sim step — the coordinator sweep owns that hookup
// (see the integration note at the bottom of this file).
//
// Units, matching Particle2D.cs: flat XY canvas pixels, Y-DOWN, seconds.
// Positions are px.
//
// Y-DOWN SIGN CONVENTIONS (same as P76 proved):
// - "Down" is +Y. A particle falling onto a flat floor moves with
//   velocity.Y > 0.
// - A hit normal points INTO FREE SPACE, away from the surface, toward
//   where the particle should end up. A flat floor (surface below the
//   particle, free space above it) has normal (0,-1): "up" in Y-down
//   space is (0,-1). A ceiling (surface above, free space below) has
//   normal (0,+1). The rect top-face sweep test proves the (0,-1) case;
//   the rect bottom-face case is the mirror and is tested via the
//   start-inside closest-face rule, not assumed.
// - Hit.Point is the PARTICLE-CENTRE position at contact, already
//   radius-corrected: the shape is expanded by the particle radius
//   before the sweep (circle radius grows by r, a rect grows by r on
//   every face, a capsule radius grows by r — exactly P23's convention
//   in 2D). A response therefore snaps the centre to Hit.Point with no
//   extra radius push-out, and Hit.Time is the fraction along the swept
//   segment (0 = previous position, 1 = current position).
//
// Shape semantics (uniform across all three shapes):
// - Each shape is a SOLID. Contains(point) tests the geometric shape
//   itself (particle radius 0); Contains(point, particleRadius) tests
//   the radius-expanded shape, i.e. whether a particle centred at the
//   point touches or overlaps the shape.
// - Boundary = inside: Contains uses <= everywhere, so a point exactly
//   on the boundary counts as inside (contact, not a miss).
// - Sweep takes the swept segment previous -> current, so a fast
//   particle cannot tunnel through a thin shape between steps; a
//   point-only test at the current position would miss exactly that
//   case (see the tunnelling test: a 2px-wide rect crossed in one step).
// - If the segment starts inside the expanded shape, Sweep reports a
//   hit at Time 0 at the previous position, with the outward normal of
//   the closest boundary feature: the particle is already in contact.
// - Misses carry Hit = false; Point / Normal / Time are meaningless
//   then — no fabricated point or normal is ever returned on a miss.
// - A zero-length step (previous == current) hits only when it starts
//   inside the expanded shape, and then at Time 0.
//
// The third shape is the CAPSULE (segment + radius), chosen over a bare
// sloped segment for two reasons: it mirrors P23's capsule, and a
// capsule with radius 0 IS a sloped segment — so P76's slope-slide
// tests get their ramp by using a zero-radius capsule (proved in the
// slope test: a falling particle meets a diagonal segment, and the
// returned normal is perpendicular to the ramp). A zero-length capsule
// segment (A == B) is NOT an error: it is defined as a circle centred
// at A with the capsule radius, and behaves exactly like one (proved
// in tests). Every other degenerate input fails loudly instead.
//
// P76 MAPPING (this is the hit type P76's sweep mapping expects):
// P76 landed first and defined its own Particle2DResponseHit struct
// because P13 had not landed. This lane's hit type is named after
// colliders, not responses, so the two names never collide, and its
// fields map straight across, in order:
//   new Particle2DResponseHit( hit.Hit, hit.Point, hit.Normal )
// where hit.Point is this file's radius-corrected centre-at-contact
// and hit.Normal is its outward (free-space) normal. Time is dropped
// by the mapping because P76's responses do not use it. The mapping
// test in Particle2DCollidersTests.cs runs the conversion and feeds
// the result to Particle2DResponse.Slide, proving the fields line up.

/// <summary>One swept-collision result. Hit = false means no contact along the segment; Point / Normal / Time are meaningless then.</summary>
public readonly record struct Particle2DColliderHit( bool Hit, Vector2 Point, Vector2 Normal, float Time )
{
	public static Particle2DColliderHit Miss => new( false, Vector2.Zero, Vector2.Zero, 0f );
}

/// <summary>Common surface for the P13 collider shapes, mirroring P23's IParticleCollider in 2D, so a sim step can hold a mixed list.</summary>
public interface IParticle2DCollider
{
	Particle2DColliderHit Sweep( Vector2 previous, Vector2 current, float particleRadius );
	bool Contains( Vector2 point, float particleRadius );
	void Validate();
}

/// <summary>Shared swept-segment helpers. Internal: the shape classes are the public surface.</summary>
internal static class Particle2DColliderMath
{
	public static bool Finite( Vector2 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y );

	public static void CheckSegment( Vector2 previous, Vector2 current, float particleRadius )
	{
		if ( !Finite( previous ) || !Finite( current ) )
			throw new ArgumentException( "Swept segment endpoints must be finite." );
		if ( !float.IsFinite( particleRadius ) || particleRadius < 0 )
			throw new ArgumentOutOfRangeException( nameof( particleRadius ), "Particle radius must be >= 0." );
	}

	public static void CheckPoint( Vector2 point, float particleRadius )
	{
		if ( !Finite( point ) )
			throw new ArgumentException( "Contains point must be finite." );
		if ( !float.IsFinite( particleRadius ) || particleRadius < 0 )
			throw new ArgumentOutOfRangeException( nameof( particleRadius ), "Particle radius must be >= 0." );
	}

	/// <summary>Closest point on segment AB to p, plus its parameter (0 = A, 1 = B). A == B gives A at s = 0.</summary>
	public static Vector2 ClosestOnSegment( Vector2 p, Vector2 a, Vector2 b, out float s )
	{
		var ab = b - a;
		float l2 = Vector2.Dot( ab, ab );
		s = l2 < 1e-12f ? 0f : Vector2.Dot( p - a, ab ) / l2;
		if ( s < 0 ) s = 0;
		if ( s > 1 ) s = 1;
		return a + ab * s;
	}

	/// <summary>Normal from the closest boundary point: (p - closest) normalized. At the degenerate centre (p == closest) the Y-down "up" fallback (0,-1) is used.</summary>
	public static Vector2 OutwardNormal( Vector2 p, Vector2 closest )
	{
		var n = p - closest;
		float l2 = Vector2.Dot( n, n );
		return l2 < 1e-12f ? new Vector2( 0, -1 ) : n / MathF.Sqrt( l2 );
	}

	/// <summary>
	/// Earliest t in [0, 1] where segment previous -> current touches the circle
	/// (centre, radius). Returns false when the segment never reaches it.
	/// A start inside the circle reports t = 0 at the start point.
	/// </summary>
	public static bool SegmentCircle( Vector2 previous, Vector2 current, Vector2 centre, float radius, out float t, out Vector2 point )
	{
		t = 0;
		point = previous;
		var d = current - previous;
		var m = previous - centre;
		float r2 = radius * radius;
		if ( Vector2.Dot( m, m ) <= r2 ) return true; // already in contact: t = 0 at previous
		float a = Vector2.Dot( d, d );
		if ( a < 1e-12f ) return false; // zero-length step, starting outside
		float b = 2f * Vector2.Dot( d, m );
		float c = Vector2.Dot( m, m ) - r2;
		float disc = b * b - 4f * a * c;
		if ( disc < 0 ) return false;
		t = (-b - MathF.Sqrt( disc )) / (2f * a);
		if ( t < 0 || t > 1 ) return false;
		point = previous + d * t;
		return true;
	}
}

/// <summary>A solid circle collider: centre + radius, canvas px, Y-down.</summary>
public sealed class Particle2DCircleCollider : IParticle2DCollider
{
	public Vector2 Center;
	public float Radius;

	public void Validate()
	{
		if ( !Particle2DColliderMath.Finite( Center ) )
			throw new ArgumentException( "Circle collider centre must be finite." );
		if ( !float.IsFinite( Radius ) || Radius < 0 )
			throw new ArgumentOutOfRangeException( nameof( Radius ), "Circle collider radius must be >= 0." );
	}

	/// <summary>Geometric shape test (particle radius 0): boundary counts as inside.</summary>
	public bool Contains( Vector2 point ) => Contains( point, 0f );

	public bool Contains( Vector2 point, float particleRadius )
	{
		Validate();
		Particle2DColliderMath.CheckPoint( point, particleRadius );
		float r = Radius + particleRadius;
		return Vector2.Dot( point - Center, point - Center ) <= r * r;
	}

	public Particle2DColliderHit Sweep( Vector2 previous, Vector2 current, float particleRadius )
	{
		Validate();
		Particle2DColliderMath.CheckSegment( previous, current, particleRadius );
		float r = Radius + particleRadius;
		if ( !Particle2DColliderMath.SegmentCircle( previous, current, Center, r, out float t, out var point ) )
			return Particle2DColliderHit.Miss;
		var normal = Particle2DColliderMath.OutwardNormal( point, Center );
		return new Particle2DColliderHit( true, point, normal, t );
	}
}

/// <summary>
/// An axis-aligned solid rect collider, stored as Min/Max corners (mirroring
/// P23's box). Min must be &lt;= Max on both axes; use FromCenter for a
/// centre + half-extents construction. Normal = outward normal of the
/// contacted face: the top face (free space above, in Y-down) is (0,-1),
/// the bottom face is (0,+1), the left face (-1,0), the right face (+1,0).
/// </summary>
public sealed class Particle2DRectCollider : IParticle2DCollider
{
	public Vector2 Min;
	public Vector2 Max;

	/// <summary>Centre + half-extents construction. Half-extents must be finite and &gt;= 0 on both axes (validated by the returned collider's Validate).</summary>
	public static Particle2DRectCollider FromCenter( Vector2 center, Vector2 halfExtents )
		=> new() { Min = center - halfExtents, Max = center + halfExtents };

	public void Validate()
	{
		if ( !Particle2DColliderMath.Finite( Min ) || !Particle2DColliderMath.Finite( Max ) )
			throw new ArgumentException( "Rect collider corners must be finite." );
		if ( Min.X > Max.X || Min.Y > Max.Y )
			throw new ArgumentException( "Rect collider is inverted: Min must be <= Max on every axis." );
	}

	/// <summary>Geometric shape test (particle radius 0): boundary counts as inside.</summary>
	public bool Contains( Vector2 point ) => Contains( point, 0f );

	public bool Contains( Vector2 point, float particleRadius )
	{
		Validate();
		Particle2DColliderMath.CheckPoint( point, particleRadius );
		var lo = Min - new Vector2( particleRadius );
		var hi = Max + new Vector2( particleRadius );
		return point.X >= lo.X && point.X <= hi.X && point.Y >= lo.Y && point.Y <= hi.Y;
	}

	public Particle2DColliderHit Sweep( Vector2 previous, Vector2 current, float particleRadius )
	{
		Validate();
		Particle2DColliderMath.CheckSegment( previous, current, particleRadius );
		var lo = Min - new Vector2( particleRadius );
		var hi = Max + new Vector2( particleRadius );

		// Already inside the expanded rect: contact at t = 0, normal = closest face.
		// Ties are broken in axis order Min-X, Max-X, Min-Y, Max-Y (first minimum wins).
		if ( previous.X >= lo.X && previous.X <= hi.X && previous.Y >= lo.Y && previous.Y <= hi.Y )
		{
			float dx0 = previous.X - lo.X, dx1 = hi.X - previous.X;
			float dy0 = previous.Y - lo.Y, dy1 = hi.Y - previous.Y;
			var normal = new Vector2( -1, 0 );
			float best = dx0;
			if ( dx1 < best ) { best = dx1; normal = new Vector2( 1, 0 ); }
			if ( dy0 < best ) { best = dy0; normal = new Vector2( 0, -1 ); }
			if ( dy1 < best ) { best = dy1; normal = new Vector2( 0, 1 ); }
			return new Particle2DColliderHit( true, previous, normal, 0f );
		}

		// Slab method on the expanded rect; entry face gives the normal.
		var d = current - previous;
		float tEnter = 0f, tExit = 1f;
		var enterNormal = Vector2.Zero;
		for ( int axis = 0; axis < 2; axis++ )
		{
			float p0 = axis == 0 ? previous.X : previous.Y;
			float dd = axis == 0 ? d.X : d.Y;
			float mn = axis == 0 ? lo.X : lo.Y;
			float mx = axis == 0 ? hi.X : hi.Y;
			if ( MathF.Abs( dd ) < 1e-12f )
			{
				if ( p0 < mn || p0 > mx ) return Particle2DColliderHit.Miss;
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
			if ( tEnter > tExit ) return Particle2DColliderHit.Miss;
		}
		if ( tEnter > 1 || tExit < 0 ) return Particle2DColliderHit.Miss;
		if ( tEnter < 0 ) tEnter = 0; // unreachable after the inside check, kept for safety
		return new Particle2DColliderHit( true, previous + d * tEnter, enterNormal, tEnter );
	}

	static Vector2 Axis( int axis ) => axis == 0 ? new Vector2( 1, 0 ) : new Vector2( 0, 1 );
}

/// <summary>
/// A solid capsule collider: the segment A -> B thickened by Radius, canvas px,
/// Y-down. Radius 0 is a bare segment (the sloped-segment / ramp shape P76's
/// slope-slide tests use). A == B is NOT an error: it is defined as a circle
/// centred at A with the capsule radius, and behaves exactly like one.
/// Normal = outward normal of the contacted feature: perpendicular to the
/// segment on the side the particle came from, or radial from an end cap.
/// </summary>
public sealed class Particle2DCapsuleCollider : IParticle2DCollider
{
	public Vector2 A;
	public Vector2 B;
	public float Radius;

	public void Validate()
	{
		if ( !Particle2DColliderMath.Finite( A ) || !Particle2DColliderMath.Finite( B ) )
			throw new ArgumentException( "Capsule collider endpoints must be finite." );
		if ( !float.IsFinite( Radius ) || Radius < 0 )
			throw new ArgumentOutOfRangeException( nameof( Radius ), "Capsule collider radius must be >= 0." );
		// Deliberately NO zero-length rejection: A == B is a circle at A (see header).
	}

	/// <summary>Geometric shape test (particle radius 0): boundary counts as inside.</summary>
	public bool Contains( Vector2 point ) => Contains( point, 0f );

	public bool Contains( Vector2 point, float particleRadius )
	{
		Validate();
		Particle2DColliderMath.CheckPoint( point, particleRadius );
		float r = Radius + particleRadius;
		var closest = Particle2DColliderMath.ClosestOnSegment( point, A, B, out _ );
		return Vector2.Dot( point - closest, point - closest ) <= r * r;
	}

	public Particle2DColliderHit Sweep( Vector2 previous, Vector2 current, float particleRadius )
	{
		Validate();
		Particle2DColliderMath.CheckSegment( previous, current, particleRadius );
		float r = Radius + particleRadius;
		var d = current - previous;

		// Already inside the expanded capsule: contact at t = 0.
		var startClosest = Particle2DColliderMath.ClosestOnSegment( previous, A, B, out _ );
		if ( Vector2.Dot( previous - startClosest, previous - startClosest ) <= r * r )
		{
			var normal = NormalFromClosest( previous, startClosest );
			return new Particle2DColliderHit( true, previous, normal, 0f );
		}

		// Earliest contact is either an end-cap sphere or a side (line) contact.
		// Taking the minimum candidate time is exact: at the true earliest
		// contact the closest feature is an end cap or the side, so that
		// feature's equation holds there, and no candidate can precede it
		// (a candidate at t means distance-to-segment <= r at t).
		float? bestT = null;
		Vector2 bestPoint = previous, bestNormal = Vector2.Zero;

		void Consider( float t, Vector2 point )
		{
			if ( t < 0 || t > 1 ) return;
			if ( bestT is not null && t >= bestT.Value ) return;
			var closest = Particle2DColliderMath.ClosestOnSegment( point, A, B, out _ );
			bestT = t;
			bestPoint = point;
			bestNormal = NormalFromClosest( previous, closest, point );
		}

		if ( Particle2DColliderMath.SegmentCircle( previous, current, A, r, out float tA, out var pA ) ) Consider( tA, pA );
		if ( Particle2DColliderMath.SegmentCircle( previous, current, B, r, out float tB, out var pB ) ) Consider( tB, pB );

		// Side contact: distance to the infinite line through AB equals r,
		// with the contact point's projection inside the segment.
		var ab = B - A;
		float len = ab.Length();
		if ( len >= 1e-12f && d.LengthSquared() >= 1e-12f )
		{
			var u = ab / len;
			var n = new Vector2( -u.Y, u.X );
			float d0 = Vector2.Dot( n, previous - A );
			float dn = Vector2.Dot( n, d );
			if ( MathF.Abs( dn ) >= 1e-12f )
			{
				foreach ( float target in new float[] { r, -r } )
				{
					float t = (target - d0) / dn;
					if ( t < 0 || t > 1 ) continue;
					var point = previous + d * t;
					float s = Vector2.Dot( point - A, u );
					if ( s < 0 || s > len ) continue;
					Consider( t, point );
				}
			}
		}

		if ( bestT is null ) return Particle2DColliderHit.Miss;
		return new Particle2DColliderHit( true, bestPoint, bestNormal, bestT.Value );
	}

	// Outward normal for a contact whose closest segment point is known.
	// Normally (point - closest) normalized. Degenerate cases (contact point
	// exactly ON the segment, only possible at radius 0, or a start point
	// exactly on the segment): fall back to the side the particle came
	// from — the perpendicular (-u.Y, u.X) side for a segment, (0,-1) for a
	// zero-length (circle) segment — chosen by the approach direction.
	Vector2 NormalFromClosest( Vector2 approachFrom, Vector2 closest, Vector2? contactPoint = null )
	{
		var reference = contactPoint ?? approachFrom;
		var n = reference - closest;
		if ( Vector2.Dot( n, n ) >= 1e-12f ) return Vector2.Normalize( n );
		var ab = B - A;
		float len = ab.Length();
		if ( len < 1e-12f ) return new Vector2( 0, -1 ); // zero-length capsule = circle: Y-down "up" fallback
		var u = ab / len;
		var perp = new Vector2( -u.Y, u.X );
		// Side the particle came from: sign of the approach direction
		// relative to the contact point, projected on the perpendicular.
		float side = Vector2.Dot( approachFrom - reference, perp );
		return side >= 0 ? perp : -perp;
	}
}

/// <summary>
/// Mixed-list helper for a sim step: the earliest hit across a list of
/// colliders, or Miss when none hit. Ties (same Time) go to the first
/// collider in list order. A null list or a null entry fails loudly.
/// </summary>
public static class Particle2DColliders
{
	public static Particle2DColliderHit SweepAll( IEnumerable<IParticle2DCollider> colliders, Vector2 previous, Vector2 current, float particleRadius )
	{
		ArgumentNullException.ThrowIfNull( colliders );
		Particle2DColliderHit best = Particle2DColliderHit.Miss;
		foreach ( var collider in colliders )
		{
			ArgumentNullException.ThrowIfNull( collider );
			var hit = collider.Sweep( previous, current, particleRadius );
			if ( hit.Hit && ( !best.Hit || hit.Time < best.Time ) ) best = hit;
		}
		return best;
	}
}

// ---- Coordinator sweep: collider hookup (NOT made by this lane) ----
//
// Test registration (sweep-owned, NOT added by this lane):
//   Particle2DCollidersTests.Register( registry, settings );
// placed after the Particle2DTests.Register line (itself still a held
// sweep item, after GpuParticleSystemTests.Register in TestRegistry.cs).
// The P76 response tests sit nearby: Particle2DResponseTests.Register
// after the GpuParticleSystemTests.Register line, and after the
// Particle2DTests.Register line once the sweep adds it — this lane's
// line goes after Particle2DTests.Register as well; exact relative
// order between the P13 and P76 lines does not matter (no shared state).
//
// Sim-step hookup (sweep-owned, in Particle2DSystem's step, after
// position integration, ALONGSIDE the existing floor check — the floor
// at Particle2D.cs ~line 722 (if FloorEnabled, clamp y <= FloorY and
// bounce) stays exactly as-is, it is NOT replaced by these colliders):
//
//   var hit = Particle2DColliders.SweepAll( colliders, previousPosition, p.Position, particleRadius );
//   if ( hit.Hit )
//   {
//       var hit2d = new Particle2DResponseHit( hit.Hit, hit.Point, hit.Normal ); // P13 hit -> P76 response hit, field for field
//       var response = Particle2DResponse.Bounce( p.Position, p.Velocity, hit2d, responseSettings );
//       p.Position = response.Position;
//       p.Velocity = response.Velocity;
//       // if ( response.Stuck ) the caller stops integrating this particle (Sticky / threshold-degraded Bounce only).
//   }
//
// Swap Bounce for Slide or Sticky for a surface that should glide or
// grab. responseSettings is a Particle2DResponseSettings built from the
// system's Restitution/Friction (and StickyThreshold/Stickiness where
// the effect wants them). `colliders` is a List<IParticle2DCollider>
// owned by the system/settings; `previousPosition` is the position
// before this step's integration; `particleRadius` is the particle's
// radius in px. Hit.Time is available on the P13 hit for substep
// ordering but is intentionally dropped by the P76 mapping (P76's
// responses do not use it).
