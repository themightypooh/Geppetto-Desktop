using System.Numerics;
using Marionette.Desktop;

// P13 tests. Sweep-owned registration (NOT added by this lane):
//   Particle2DCollidersTests.Register( registry, settings );
// placed after the Particle2DTests.Register line (itself still a held
// sweep item, after GpuParticleSystemTests.Register in TestRegistry.cs).
//
// Every expected number below is computed from the geometry documented
// in Particle2DColliders.cs, not copied from a run. Units: flat XY
// canvas pixels, Y-DOWN. A floor's free-space normal is (0,-1).

internal static class Particle2DCollidersTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearV( Vector2 got, Vector2 want, float tol, string what )
		{
			if ( (got - want).Length() > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; } // covers ArgumentOutOfRangeException too (it derives from it)
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		static Particle2DCircleCollider Circle( float cx, float cy, float r ) => new() { Center = new Vector2( cx, cy ), Radius = r };
		static Particle2DRectCollider Rect( float minX, float minY, float maxX, float maxY ) => new() { Min = new Vector2( minX, minY ), Max = new Vector2( maxX, maxY ) };
		static Particle2DCapsuleCollider Capsule( float ax, float ay, float bx, float by, float r ) => new() { A = new Vector2( ax, ay ), B = new Vector2( bx, by ), Radius = r };

		// ---- Circle Contains ----

		registry.Add( "particle-2d-colliders", "circle contains: inside point", () =>
		{
			Check( Circle( 100, 100, 5 ).Contains( new Vector2( 102, 100 ) ), "centre + 2px must be inside a radius-5 circle" );
		} );
		registry.Add( "particle-2d-colliders", "circle contains: outside point", () =>
		{
			Check( !Circle( 100, 100, 5 ).Contains( new Vector2( 106, 100 ) ), "centre + 6px must be outside a radius-5 circle" );
		} );
		registry.Add( "particle-2d-colliders", "circle contains: boundary is inside", () =>
		{
			Check( Circle( 100, 100, 5 ).Contains( new Vector2( 105, 100 ) ), "exactly radius 5 from the centre is the boundary, and boundary counts as inside" );
		} );
		registry.Add( "particle-2d-colliders", "circle contains: particle radius expands the shape", () =>
		{
			var c = Circle( 100, 100, 5 );
			var p = new Vector2( 105.5f, 100 ); // 5.5px from the centre: outside the shape, inside the radius-1 expansion (5 + 1 = 6)
			Check( !c.Contains( p ), "5.5px must be outside the unexpanded circle" );
			Check( c.Contains( p, 1f ), "5.5px must be inside the circle expanded by particle radius 1" );
		} );

		// ---- Circle Sweep ----

		registry.Add( "particle-2d-colliders", "circle sweep: axis approach contact (95,100) normal (-1,0) t 0.375", () =>
		{
			var hit = Circle( 100, 100, 5 ).Sweep( new Vector2( 80, 100 ), new Vector2( 120, 100 ), 0f );
			Check( hit.Hit, "head-on approach must hit" );
			Check( hit.Point == new Vector2( 95, 100 ), $"contact must be exactly (95,100), got {hit.Point}" );
			Check( hit.Normal == new Vector2( -1, 0 ), $"normal must be exactly (-1,0), got {hit.Normal}" );
			Near( hit.Time, 0.375f, 1e-3f, "time" ); // 15px of a 40px step
		} );
		registry.Add( "particle-2d-colliders", "circle sweep: diagonal approach contact (96.4645,96.4645) normal (-0.7071,-0.7071)", () =>
		{
			// Line y = x through the centre: entry 5px before the centre along the line.
			var hit = Circle( 100, 100, 5 ).Sweep( new Vector2( 80, 80 ), new Vector2( 120, 120 ), 0f );
			Check( hit.Hit, "diagonal approach through the centre must hit" );
			NearV( hit.Point, new Vector2( 96.4645f, 96.4645f ), 1e-3f, "diagonal contact" );
			NearV( hit.Normal, new Vector2( -0.7071f, -0.7071f ), 1e-3f, "diagonal normal" );
			Near( hit.Time, 0.4116f, 1e-3f, "diagonal time" );
		} );
		registry.Add( "particle-2d-colliders", "circle sweep: radius expansion, shape 5 + particle 1 = contact at distance 6", () =>
		{
			var hit = Circle( 100, 100, 5 ).Sweep( new Vector2( 80, 100 ), new Vector2( 120, 100 ), 1f );
			Check( hit.Hit, "expanded sweep must hit" );
			Check( hit.Point == new Vector2( 94, 100 ), $"contact must be exactly (94,100) — 6px from the centre — got {hit.Point}" );
			Check( hit.Normal == new Vector2( -1, 0 ), $"normal must be (-1,0), got {hit.Normal}" );
			Near( hit.Time, 0.35f, 1e-3f, "expanded time" ); // 14px of a 40px step
		} );
		registry.Add( "particle-2d-colliders", "circle sweep: miss passes through, Hit false", () =>
		{
			var hit = Circle( 100, 100, 5 ).Sweep( new Vector2( 80, 80 ), new Vector2( 120, 80 ), 0f );
			Check( !hit.Hit, "a path 20px above a radius-5 circle must miss" );
		} );
		registry.Add( "particle-2d-colliders", "circle sweep: start inside reports t 0 at the start point", () =>
		{
			var hit = Circle( 100, 100, 5 ).Sweep( new Vector2( 102, 100 ), new Vector2( 120, 100 ), 0f );
			Check( hit.Hit, "starting inside must report a hit" );
			Check( hit.Point == new Vector2( 102, 100 ), $"t-0 point must be the start point, got {hit.Point}" );
			Check( hit.Normal == new Vector2( 1, 0 ), $"normal must point away from the centre (1,0), got {hit.Normal}" );
			Check( hit.Time == 0f, $"time must be 0, got {hit.Time}" );
		} );
		registry.Add( "particle-2d-colliders", "circle sweep: start exactly at the centre falls back to (0,-1)", () =>
		{
			var hit = Circle( 100, 100, 5 ).Sweep( new Vector2( 100, 100 ), new Vector2( 120, 100 ), 0f );
			Check( hit.Hit && hit.Time == 0f, "starting at the centre must report a t-0 hit" );
			Check( hit.Normal == new Vector2( 0, -1 ), $"degenerate centre normal must be the Y-down up fallback (0,-1), got {hit.Normal}" );
		} );

		// ---- Rect Contains ----

		registry.Add( "particle-2d-colliders", "rect contains: inside point", () =>
		{
			Check( Rect( 100, 100, 140, 160 ).Contains( new Vector2( 120, 130 ) ), "centre of the rect must be inside" );
		} );
		registry.Add( "particle-2d-colliders", "rect contains: outside point", () =>
		{
			Check( !Rect( 100, 100, 140, 160 ).Contains( new Vector2( 99, 130 ) ), "1px left of Min.X must be outside" );
		} );
		registry.Add( "particle-2d-colliders", "rect contains: Min corner boundary is inside", () =>
		{
			Check( Rect( 100, 100, 140, 160 ).Contains( new Vector2( 100, 100 ) ), "the Min corner is the boundary, and boundary counts as inside" );
		} );
		registry.Add( "particle-2d-colliders", "rect contains: Max corner boundary is inside", () =>
		{
			Check( Rect( 100, 100, 140, 160 ).Contains( new Vector2( 140, 160 ) ), "the Max corner is the boundary, and boundary counts as inside" );
		} );
		registry.Add( "particle-2d-colliders", "rect contains: particle radius expands every face", () =>
		{
			var r = Rect( 100, 100, 140, 160 );
			Check( !r.Contains( new Vector2( 99, 130 ) ), "1px left of Min.X is outside unexpanded" );
			Check( r.Contains( new Vector2( 99, 130 ), 1f ), "1px left of Min.X is inside the radius-1 expansion (Min.X - 1 = 99)" );
		} );
		registry.Add( "particle-2d-colliders", "rect FromCenter: centre (120,130) half (20,30) gives Min (100,100) Max (140,160)", () =>
		{
			var r = Particle2DRectCollider.FromCenter( new Vector2( 120, 130 ), new Vector2( 20, 30 ) );
			Check( r.Min == new Vector2( 100, 100 ) && r.Max == new Vector2( 140, 160 ), $"FromCenter must give Min (100,100) Max (140,160), got Min {r.Min} Max {r.Max}" );
			Check( r.Contains( new Vector2( 100, 100 ) ), "FromCenter rect must contain its Min corner" );
		} );

		// ---- Rect Sweep ----

		registry.Add( "particle-2d-colliders", "rect sweep: left face contact (100,130) normal (-1,0)", () =>
		{
			var hit = Rect( 100, 100, 140, 160 ).Sweep( new Vector2( 80, 130 ), new Vector2( 200, 130 ), 0f );
			Check( hit.Hit, "left approach must hit" );
			Check( hit.Point == new Vector2( 100, 130 ), $"contact must be (100,130), got {hit.Point}" );
			Check( hit.Normal == new Vector2( -1, 0 ), $"normal must be (-1,0), got {hit.Normal}" );
			Near( hit.Time, 20f / 120f, 1e-3f, "left-face time" );
		} );
		registry.Add( "particle-2d-colliders", "rect sweep: top face contact (120,100) normal (0,-1), the Y-down floor normal", () =>
		{
			// Falling (+Y) onto the top face: free space is above, so the normal is (0,-1).
			var hit = Rect( 100, 100, 140, 160 ).Sweep( new Vector2( 120, 80 ), new Vector2( 120, 200 ), 0f );
			Check( hit.Hit, "falling onto the top face must hit" );
			Check( hit.Point == new Vector2( 120, 100 ), $"contact must be (120,100), got {hit.Point}" );
			Check( hit.Normal == new Vector2( 0, -1 ), $"top-face normal must be (0,-1), got {hit.Normal}" );
			Near( hit.Time, 20f / 120f, 1e-3f, "top-face time" );
		} );
		registry.Add( "particle-2d-colliders", "rect sweep: diagonal approach hits a face, not a corner", () =>
		{
			var hit = Rect( 100, 100, 140, 160 ).Sweep( new Vector2( 80, 110 ), new Vector2( 200, 170 ), 0f );
			Check( hit.Hit, "diagonal approach must hit" );
			Check( hit.Point == new Vector2( 100, 120 ), $"contact must be (100,120) on the left face, got {hit.Point}" );
			Check( hit.Normal == new Vector2( -1, 0 ), $"normal must be (-1,0), got {hit.Normal}" );
			Near( hit.Time, 20f / 120f, 1e-3f, "diagonal face time" );
		} );
		registry.Add( "particle-2d-colliders", "rect sweep: miss above the rect passes through", () =>
		{
			var hit = Rect( 100, 100, 140, 160 ).Sweep( new Vector2( 80, 80 ), new Vector2( 200, 80 ), 0f );
			Check( !hit.Hit, "a path above the rect must miss" );
		} );
		registry.Add( "particle-2d-colliders", "rect sweep: start inside reports t 0 with the closest-face normal", () =>
		{
			// (110,130): left face 10px away, every other face 30px — closest face is the left one.
			var hit = Rect( 100, 100, 140, 160 ).Sweep( new Vector2( 110, 130 ), new Vector2( 200, 130 ), 0f );
			Check( hit.Hit && hit.Time == 0f, "starting inside must report a t-0 hit" );
			Check( hit.Point == new Vector2( 110, 130 ), $"t-0 point must be the start point, got {hit.Point}" );
			Check( hit.Normal == new Vector2( -1, 0 ), $"closest-face normal must be (-1,0), got {hit.Normal}" );
		} );
		registry.Add( "particle-2d-colliders", "rect sweep: radius expansion moves the contact to (98,130)", () =>
		{
			var hit = Rect( 100, 100, 140, 160 ).Sweep( new Vector2( 80, 130 ), new Vector2( 200, 130 ), 2f );
			Check( hit.Hit, "expanded sweep must hit" );
			Check( hit.Point == new Vector2( 98, 130 ), $"contact must be (98,130) — Min.X minus particle radius 2 — got {hit.Point}" );
			Near( hit.Time, 18f / 120f, 1e-3f, "expanded time" );
		} );
		registry.Add( "particle-2d-colliders", "rect sweep: tunnelling — a 2px-wide rect crossed in one step still hits", () =>
		{
			// A point test at either endpoint misses: both (80,100) and (120,100)
			// are outside the rect, yet the segment crosses it. The sweep must catch it.
			var r = Rect( 100, 90, 102, 110 );
			var start = new Vector2( 80, 100 );
			var end = new Vector2( 120, 100 );
			Check( !r.Contains( start ) && !r.Contains( end ), "tunnelling setup: both endpoints must be outside the thin rect" );
			var hit = r.Sweep( start, end, 0f );
			Check( hit.Hit, "sweep must catch the thin rect the endpoint point-test misses" );
			Check( hit.Point == new Vector2( 100, 100 ), $"contact must be (100,100), got {hit.Point}" );
			Check( hit.Normal == new Vector2( -1, 0 ), $"normal must be (-1,0), got {hit.Normal}" );
			Near( hit.Time, 0.5f, 1e-3f, "tunnelling time" );
		} );

		// ---- Capsule Contains ----

		registry.Add( "particle-2d-colliders", "capsule contains: point near the segment is inside", () =>
		{
			Check( Capsule( 100, 100, 140, 100, 5 ).Contains( new Vector2( 120, 103 ) ), "3px from the segment must be inside a radius-5 capsule" );
		} );
		registry.Add( "particle-2d-colliders", "capsule contains: point far from the segment is outside", () =>
		{
			Check( !Capsule( 100, 100, 140, 100, 5 ).Contains( new Vector2( 120, 106 ) ), "6px from the segment must be outside a radius-5 capsule" );
		} );
		registry.Add( "particle-2d-colliders", "capsule contains: side boundary is inside", () =>
		{
			Check( Capsule( 100, 100, 140, 100, 5 ).Contains( new Vector2( 120, 105 ) ), "exactly 5px from the segment is the boundary, and boundary counts as inside" );
		} );
		registry.Add( "particle-2d-colliders", "capsule contains: end-cap inside including its boundary", () =>
		{
			var c = Capsule( 100, 100, 140, 100, 5 );
			Check( c.Contains( new Vector2( 96, 100 ) ), "4px past end cap A must be inside" );
			Check( c.Contains( new Vector2( 95, 100 ) ), "exactly 5px past end cap A is the boundary, and boundary counts as inside" );
		} );
		registry.Add( "particle-2d-colliders", "capsule contains: past the end cap is outside", () =>
		{
			Check( !Capsule( 100, 100, 140, 100, 5 ).Contains( new Vector2( 94, 100 ) ), "6px past end cap A must be outside" );
		} );

		// ---- Capsule Sweep ----

		registry.Add( "particle-2d-colliders", "capsule sweep: side contact (120,95) normal (0,-1) t 0.375", () =>
		{
			var hit = Capsule( 100, 100, 140, 100, 5 ).Sweep( new Vector2( 120, 80 ), new Vector2( 120, 120 ), 0f );
			Check( hit.Hit, "falling onto the capsule side must hit" );
			Check( hit.Point == new Vector2( 120, 95 ), $"contact must be (120,95), got {hit.Point}" );
			Check( hit.Normal == new Vector2( 0, -1 ), $"side normal must be (0,-1), got {hit.Normal}" );
			Near( hit.Time, 0.375f, 1e-3f, "side time" ); // 15px of a 40px step
		} );
		registry.Add( "particle-2d-colliders", "capsule sweep: diagonal side contact (110,95) normal (0,-1)", () =>
		{
			var hit = Capsule( 100, 100, 140, 100, 5 ).Sweep( new Vector2( 80, 80 ), new Vector2( 160, 120 ), 0f );
			Check( hit.Hit, "diagonal approach to the capsule side must hit" );
			Check( hit.Point == new Vector2( 110, 95 ), $"contact must be (110,95), got {hit.Point}" );
			Check( hit.Normal == new Vector2( 0, -1 ), $"normal must be (0,-1), got {hit.Normal}" );
			Near( hit.Time, 0.375f, 1e-3f, "diagonal side time" );
		} );
		registry.Add( "particle-2d-colliders", "capsule sweep: radius expansion, shape 5 + particle 1 = contact at distance 6", () =>
		{
			var hit = Capsule( 100, 100, 140, 100, 5 ).Sweep( new Vector2( 120, 80 ), new Vector2( 120, 120 ), 1f );
			Check( hit.Hit, "expanded capsule sweep must hit" );
			Check( hit.Point == new Vector2( 120, 94 ), $"contact must be (120,94) — 6px from the segment — got {hit.Point}" );
			Near( hit.Time, 0.35f, 1e-3f, "expanded capsule time" ); // 14px of a 40px step
		} );
		registry.Add( "particle-2d-colliders", "capsule sweep: end-cap contact (95,100) normal (-1,0)", () =>
		{
			var hit = Capsule( 100, 100, 140, 100, 5 ).Sweep( new Vector2( 80, 100 ), new Vector2( 120, 100 ), 0f );
			Check( hit.Hit, "approach along the segment axis must hit the end cap" );
			Check( hit.Point == new Vector2( 95, 100 ), $"contact must be (95,100), got {hit.Point}" );
			Check( hit.Normal == new Vector2( -1, 0 ), $"end-cap normal must be (-1,0), got {hit.Normal}" );
			Near( hit.Time, 0.375f, 1e-3f, "end-cap time" );
		} );
		registry.Add( "particle-2d-colliders", "capsule sweep: miss passes through", () =>
		{
			var hit = Capsule( 100, 100, 140, 100, 5 ).Sweep( new Vector2( 80, 80 ), new Vector2( 160, 80 ), 0f );
			Check( !hit.Hit, "a path 20px above a radius-5 capsule must miss" );
		} );
		registry.Add( "particle-2d-colliders", "capsule sweep: start inside reports t 0 with the side normal", () =>
		{
			var hit = Capsule( 100, 100, 140, 100, 5 ).Sweep( new Vector2( 120, 102 ), new Vector2( 120, 140 ), 0f );
			Check( hit.Hit && hit.Time == 0f, "starting inside must report a t-0 hit" );
			Check( hit.Point == new Vector2( 120, 102 ), $"t-0 point must be the start point, got {hit.Point}" );
			Check( hit.Normal == new Vector2( 0, 1 ), $"normal must point away from the segment (0,1), got {hit.Normal}" );
		} );
		registry.Add( "particle-2d-colliders", "capsule sweep: zero-length capsule is a circle", () =>
		{
			// A == B is defined as a circle at A, not an error.
			var c = Capsule( 100, 100, 100, 100, 5 );
			Check( c.Contains( new Vector2( 105, 100 ) ), "zero-length capsule must contain its boundary point like a circle" );
			Check( !c.Contains( new Vector2( 106, 100 ) ), "zero-length capsule must exclude points past its radius like a circle" );
			var hit = c.Sweep( new Vector2( 80, 100 ), new Vector2( 120, 100 ), 0f );
			Check( hit.Hit, "zero-length capsule sweep must hit like a circle" );
			Check( hit.Point == new Vector2( 95, 100 ), $"contact must be (95,100), got {hit.Point}" );
			Check( hit.Normal == new Vector2( -1, 0 ), $"normal must be (-1,0), got {hit.Normal}" );
		} );
		registry.Add( "particle-2d-colliders", "capsule sweep: zero-radius capsule is a sloped segment for P76 slide", () =>
		{
			// Ramp from (100,140) down-right to (140,100) in Y-down terms (up to the
			// right on screen). A particle falling straight down at x = 110 meets
			// the ramp at y = 130, and the normal is perpendicular to the ramp.
			var hit = Capsule( 100, 140, 140, 100, 0 ).Sweep( new Vector2( 110, 80 ), new Vector2( 110, 160 ), 0f );
			Check( hit.Hit, "falling onto the ramp segment must hit" );
			Check( hit.Point == new Vector2( 110, 130 ), $"contact must be (110,130) on the ramp, got {hit.Point}" );
			NearV( hit.Normal, new Vector2( -0.7071f, -0.7071f ), 1e-3f, "ramp normal" );
			Near( hit.Time, 0.625f, 1e-3f, "ramp time" ); // 50px of an 80px step
		} );

		// ---- Zero-length steps, SweepAll, determinism ----

		registry.Add( "particle-2d-colliders", "zero-length step: outside misses, inside hits at t 0", () =>
		{
			var c = Circle( 100, 100, 5 );
			Check( !c.Sweep( new Vector2( 80, 100 ), new Vector2( 80, 100 ), 0f ).Hit, "a stationary particle outside must miss" );
			var hit = c.Sweep( new Vector2( 102, 100 ), new Vector2( 102, 100 ), 0f );
			Check( hit.Hit && hit.Time == 0f && hit.Point == new Vector2( 102, 100 ), "a stationary particle inside must hit at t 0 at its own position" );
		} );
		registry.Add( "particle-2d-colliders", "sweep-all: earliest hit wins regardless of list order", () =>
		{
			var far = Circle( 150, 100, 5 );
			var near = Circle( 100, 100, 5 );
			var hit = Particle2DColliders.SweepAll( new IParticle2DCollider[] { far, near }, new Vector2( 80, 100 ), new Vector2( 200, 100 ), 0f );
			Check( hit.Hit, "sweep-all must hit" );
			Check( hit.Point == new Vector2( 95, 100 ), $"earliest contact must be the near circle's (95,100), got {hit.Point}" );
			Near( hit.Time, 15f / 120f, 1e-3f, "sweep-all time" );
		} );
		registry.Add( "particle-2d-colliders", "sweep-all: no collider hit means Miss", () =>
		{
			var hit = Particle2DColliders.SweepAll( new IParticle2DCollider[] { Circle( 100, 100, 5 ) }, new Vector2( 80, 80 ), new Vector2( 120, 80 ), 0f );
			Check( !hit.Hit, "sweep-all over a missing path must report no hit" );
		} );
		registry.Add( "particle-2d-colliders", "determinism: same sweep twice is bit-identical", () =>
		{
			var c = Capsule( 100, 100, 140, 100, 5 );
			var a = c.Sweep( new Vector2( 80, 80 ), new Vector2( 160, 120 ), 1f );
			var b = c.Sweep( new Vector2( 80, 80 ), new Vector2( 160, 120 ), 1f );
			Check( a == b, $"identical inputs must give identical hits, got {a} vs {b}" );
		} );

		// ---- Loud failures ----

		registry.Add( "particle-2d-colliders", "validate: negative circle radius fails loudly", () =>
		{
			Throws( () => Circle( 100, 100, -1 ).Validate(), "negative circle radius" );
			Throws( () => Circle( 100, 100, -1 ).Sweep( new Vector2( 80, 100 ), new Vector2( 120, 100 ), 0f ), "negative circle radius on Sweep" );
		} );
		registry.Add( "particle-2d-colliders", "validate: non-finite circle centre fails loudly", () =>
		{
			Throws( () => Circle( float.NaN, 100, 5 ).Validate(), "NaN circle centre" );
		} );
		registry.Add( "particle-2d-colliders", "validate: inverted rect on X fails loudly", () =>
		{
			Throws( () => Rect( 140, 100, 100, 160 ).Validate(), "Min.X > Max.X" );
		} );
		registry.Add( "particle-2d-colliders", "validate: inverted rect on Y fails loudly", () =>
		{
			Throws( () => Rect( 100, 160, 140, 100 ).Validate(), "Min.Y > Max.Y" );
		} );
		registry.Add( "particle-2d-colliders", "validate: non-finite rect corner fails loudly", () =>
		{
			Throws( () => Rect( float.PositiveInfinity, 100, 140, 160 ).Validate(), "infinite rect corner" );
		} );
		registry.Add( "particle-2d-colliders", "validate: negative half-extents fail loudly via FromCenter", () =>
		{
			Throws( () => Particle2DRectCollider.FromCenter( new Vector2( 120, 130 ), new Vector2( -20, 30 ) ).Validate(), "negative half-extent inverts the rect" );
		} );
		registry.Add( "particle-2d-colliders", "validate: negative capsule radius fails loudly", () =>
		{
			Throws( () => Capsule( 100, 100, 140, 100, -1 ).Validate(), "negative capsule radius" );
		} );
		registry.Add( "particle-2d-colliders", "validate: non-finite capsule endpoint fails loudly", () =>
		{
			Throws( () => Capsule( float.NaN, 100, 140, 100, 5 ).Validate(), "NaN capsule endpoint" );
		} );
		registry.Add( "particle-2d-colliders", "validate: negative particle radius fails loudly on Sweep and Contains", () =>
		{
			Throws( () => Circle( 100, 100, 5 ).Sweep( new Vector2( 80, 100 ), new Vector2( 120, 100 ), -1f ), "negative particle radius on Sweep" );
			Throws( () => Circle( 100, 100, 5 ).Contains( new Vector2( 100, 100 ), -1f ), "negative particle radius on Contains" );
		} );
		registry.Add( "particle-2d-colliders", "validate: non-finite sweep endpoint fails loudly", () =>
		{
			Throws( () => Circle( 100, 100, 5 ).Sweep( new Vector2( float.NaN, 100 ), new Vector2( 120, 100 ), 0f ), "NaN sweep endpoint" );
		} );
		registry.Add( "particle-2d-colliders", "validate: non-finite Contains point fails loudly", () =>
		{
			Throws( () => Circle( 100, 100, 5 ).Contains( new Vector2( 100, float.PositiveInfinity ) ), "infinite Contains point" );
		} );

		// ---- P76 mapping ----

		registry.Add( "particle-2d-colliders", "p76 mapping: P13 hit maps field-for-field to Particle2DResponseHit and Slide glides along the ramp", () =>
		{
			// The exact mapping P76's header prescribes, run for real: a falling
			// particle (0,100) meeting the ramp normal (-0.7071,-0.7071) must
			// slide with zero normal component, i.e. along the ramp.
			var hit = Capsule( 100, 140, 140, 100, 0 ).Sweep( new Vector2( 110, 80 ), new Vector2( 110, 160 ), 0f );
			Check( hit.Hit, "ramp sweep must hit for the mapping test" );
			var hit2d = new Particle2DResponseHit( hit.Hit, hit.Point, hit.Normal );
			Check( hit2d.Hit == hit.Hit && hit2d.Point == hit.Point && hit2d.Normal == hit.Normal, "P76 mapping must carry Hit, Point and Normal across unchanged" );
			var settings = new Particle2DResponseSettings { Friction = 0f, ParticleRadius = 0f };
			var result = Particle2DResponse.Slide( new Vector2( 110, 80 ), new Vector2( 0, 100 ), hit2d, settings );
			Check( result.Position == hit.Point, $"Slide must snap to the P13 contact point, got {result.Position}" );
			Near( Vector2.Dot( result.Velocity, hit.Normal ), 0f, 1e-3f, "Slide velocity must have zero normal component" );
			NearV( result.Velocity, new Vector2( -50, 50 ), 1e-3f, "Slide velocity along the ramp" );
		} );
	}
}
