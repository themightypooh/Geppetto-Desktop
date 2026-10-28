using System;
using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

internal static class Particle2DTrailsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float actual, float expected, float tol, string what )
		{
			if ( MathF.Abs( actual - expected ) > tol ) throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		void NearVec( Vector2 actual, Vector2 expected, float tol, string what )
		{
			if ( Vector2.Distance( actual, expected ) > tol ) throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		void ThrowsAny( Action action, string what )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( $"{what}: did not throw" );
		}
		void SamePoints( Vector2[] actual, Vector2[] expected, string what )
		{
			Check( actual.Length == expected.Length, $"{what}: length {actual.Length}, expected {expected.Length}" );
			for ( int i = 0; i < expected.Length; i++ ) NearVec( actual[i], expected[i], 1e-4f, $"{what}[{i}]" );
		}

		Vector2 P( float x, float y ) => new( x, y );

		registry.Add( "particle-2d-trails", "ring wraps correctly after more steps than trail length", () =>
		{
			var buf = new Particle2DTrailBuffer( 3 );
			buf.SpawnAt( P( 0, 0 ) );
			buf.Record( P( 10, 0 ) );
			buf.Record( P( 20, 0 ) );
			buf.Record( P( 30, 0 ) );
			buf.Record( P( 40, 0 ) );
			// 5 writes into a 3-ring: only the 3 newest survive, newest-first.
			SamePoints( buf.Snapshot(), new[] { P( 40, 0 ), P( 30, 0 ), P( 20, 0 ) }, "wrapped snapshot" );
			Check( buf.Count == 3, $"count {buf.Count}, expected 3" );
		} );

		registry.Add( "particle-2d-trails", "head is always the newest recorded position", () =>
		{
			var buf = new Particle2DTrailBuffer( 4 );
			buf.SpawnAt( P( 5, 7 ) );
			NearVec( buf.Head, P( 5, 7 ), 1e-4f, "head after spawn" );
			buf.Record( P( 6, 7 ) );
			NearVec( buf.Head, P( 6, 7 ), 1e-4f, "head after record 1" );
			Check( buf.Snapshot()[0] == buf.Head, "snapshot[0] must equal Head" );
			buf.Record( P( 6, 9 ) );
			NearVec( buf.Head, P( 6, 9 ), 1e-4f, "head after record 2" );
			Check( buf.Snapshot()[0] == buf.Head, "snapshot[0] must equal Head after record 2" );
		} );

		registry.Add( "particle-2d-trails", "dead slots are never written", () =>
		{
			var set = new Particle2DTrailSet( 3, 4 );
			set.Spawn( 0, P( 10, 20 ) );
			set.Record( 0, P( 11, 20 ) );
			set.Record( 0, P( 12, 20 ) );
			Check( set.Count( 1 ) == 0, $"untouched slot 1 count {set.Count( 1 )}, expected 0" );
			Check( set.Count( 2 ) == 0, $"untouched slot 2 count {set.Count( 2 )}, expected 0" );
			Check( set.Snapshot( 1 ).Length == 0, "untouched slot snapshot must be empty" );
			ThrowsAny( () => { var _ = set.Head( 1 ); }, "head of an empty slot" );
			// A buffer that was never spawned at all is equally empty.
			var fresh = new Particle2DTrailBuffer( 3 );
			Check( fresh.IsEmpty && fresh.Count == 0 && fresh.Snapshot().Length == 0, "fresh buffer must be empty, not zero-padded" );
		} );

		registry.Add( "particle-2d-trails", "spawn-position fill leaves no streak to the origin", () =>
		{
			var buf = new Particle2DTrailBuffer( 4 );
			buf.SpawnAt( P( 100, 50 ) );
			var snap = buf.Snapshot();
			Check( snap.Length == 4, $"spawn-filled snapshot length {snap.Length}, expected 4" );
			foreach ( var p in snap ) NearVec( p, P( 100, 50 ), 1e-4f, "spawn-filled point" );
			Check( Array.TrueForAll( snap, p => p != Vector2.Zero ), "no spawn-filled point may be the origin" );
		} );

		registry.Add( "particle-2d-trails", "partial ring holds only recorded points, no zero padding", () =>
		{
			var buf = new Particle2DTrailBuffer( 5 );
			buf.Record( P( 1, 0 ) );
			buf.Record( P( 2, 0 ) );
			SamePoints( buf.Snapshot(), new[] { P( 2, 0 ), P( 1, 0 ) }, "partial snapshot" );
		} );

		registry.Add( "particle-2d-trails", "resample by spacing exact on a hand-computed point list", () =>
		{
			// Newest-first, 0.5 px apart; spacing 1 keeps 0, 1, 2.
			var raw = new[] { P( 0, 0 ), P( 0.5f, 0 ), P( 1, 0 ), P( 1.5f, 0 ), P( 2, 0 ) };
			SamePoints( Particle2DTrails.ResampleBySpacing( raw, 1f ), new[] { P( 0, 0 ), P( 1, 0 ), P( 2, 0 ) }, "spacing 1" );
			// Tail endpoint kept even when closer than spacing: last kept is 1, tail 1.8.
			var tail = Particle2DTrails.ResampleBySpacing( new[] { P( 0, 0 ), P( 1, 0 ), P( 1.8f, 0 ) }, 1f );
			SamePoints( tail, new[] { P( 0, 0 ), P( 1, 0 ), P( 1.8f, 0 ) }, "close tail kept" );
			// A duplicate tail is not appended twice.
			var dup = Particle2DTrails.ResampleBySpacing( new[] { P( 0, 0 ), P( 2, 0 ), P( 2, 0 ) }, 1f );
			SamePoints( dup, new[] { P( 0, 0 ), P( 2, 0 ) }, "duplicate tail" );
			// Diagonal: spacing is Euclidean, not per-axis.
			var diag = Particle2DTrails.ResampleBySpacing( new[] { P( 0, 0 ), P( 3, 4 ), P( 6, 8 ) }, 5f );
			SamePoints( diag, new[] { P( 0, 0 ), P( 3, 4 ), P( 6, 8 ) }, "diagonal 3-4-5 steps" );
		} );

		registry.Add( "particle-2d-trails", "resample by count keeps endpoints and count 1 is head only", () =>
		{
			var got = Particle2DTrails.ResampleByCount( new[] { P( 0, 0 ), P( 1, 0 ), P( 2, 0 ), P( 3, 0 ), P( 4, 0 ) }, 3 );
			SamePoints( got, new[] { P( 0, 0 ), P( 2, 0 ), P( 4, 0 ) }, "count 3 endpoints" );
			var one = Particle2DTrails.ResampleByCount( new[] { P( 5, 6 ), P( 6, 6 ) }, 1 );
			Check( one.Length == 1 && one[0] == P( 5, 6 ), "count 1 must return just the head point" );
			// Middle target lands inside a segment: total 4, targets 0/2/4.
			var mid = Particle2DTrails.ResampleByCount( new[] { P( 0, 0 ), P( 3, 0 ), P( 4, 0 ) }, 3 );
			NearVec( mid[1], P( 2, 0 ), 1e-4f, "interpolated mid" );
			NearVec( mid[2], P( 4, 0 ), 1e-4f, "interpolated tail" );
			// Zero-length trail keeps its count, all copies of the point.
			var flat = Particle2DTrails.ResampleByCount( new[] { P( 7, 7 ), P( 7, 7 ), P( 7, 7 ) }, 4 );
			Check( flat.Length == 4, $"zero-length trail count {flat.Length}, expected 4" );
			foreach ( var p in flat ) NearVec( p, P( 7, 7 ), 1e-4f, "flat point" );
		} );

		registry.Add( "particle-2d-trails", "smoothing identity at window 1 and clamped endpoints", () =>
		{
			var raw = new[] { P( 0, 0 ), P( 2, 0 ), P( 3, 0 ) };
			var ident = Particle2DTrails.Smooth( raw, 1 );
			SamePoints( ident, raw, "window 1 identity" );
			Check( !ReferenceEquals( ident, raw ), "window 1 must copy, not alias" );
			// Window 3, clamped endpoints: (0+0+1.1)/3, (0+1.1+1.9)/3, (1.1+1.9+3.1)/3, (1.9+3.1+4)/3, (3.1+4+4)/3.
			var jitter = new[] { P( 0, 0 ), P( 1.1f, 0 ), P( 1.9f, 0 ), P( 3.1f, 0 ), P( 4, 0 ) };
			var got = Particle2DTrails.Smooth( jitter, 3 );
			Check( got.Length == 5, $"smoothing must not shrink the list, got {got.Length}" );
			Near( got[0].X, 0.3666667f, 1e-4f, "smoothed 0" );
			Near( got[1].X, 1.0f, 1e-4f, "smoothed 1" );
			Near( got[2].X, 2.0333333f, 1e-4f, "smoothed 2" );
			Near( got[3].X, 3.0f, 1e-4f, "smoothed 3" );
			Near( got[4].X, 3.7f, 1e-4f, "smoothed 4" );
			// Wide window never shrinks either; endpoints are clamped, not dropped.
			var wide = Particle2DTrails.Smooth( raw, 9 );
			Check( wide.Length == 3, $"wide-window smoothing length {wide.Length}, expected 3" );
		} );

		registry.Add( "particle-2d-trails", "width curve exact at t 0, 0.5 and 1", () =>
		{
			var taper = Particle2DTrailWidthCurve.Taper( 0f );
			Near( taper.Evaluate( 0f ), 1f, 1e-6f, "taper t=0 head" );
			Near( taper.Evaluate( 0.5f ), 0.5f, 1e-6f, "taper t=0.5" );
			Near( taper.Evaluate( 1f ), 0f, 1e-6f, "taper t=1 tail" );
			Near( taper.Evaluate( -0.5f ), 1f, 1e-6f, "taper clamped below" );
			Near( taper.Evaluate( 1.5f ), 0f, 1e-6f, "taper clamped above" );
			var constant = Particle2DTrailWidthCurve.Constant( 2.5f );
			Near( constant.Evaluate( 0f ), 2.5f, 1e-6f, "constant t=0" );
			Near( constant.Evaluate( 0.5f ), 2.5f, 1e-6f, "constant t=0.5" );
			Near( constant.Evaluate( 1f ), 2.5f, 1e-6f, "constant t=1" );
			// WidthsFor: base 10, taper to 0, 4 points -> 10, 20/3, 10/3, 0.
			var widths = taper.WidthsFor( 4, 10f );
			Near( widths[0], 10f, 1e-4f, "width head" );
			Near( widths[1], 6.6666667f, 1e-4f, "width 1/3" );
			Near( widths[2], 3.3333333f, 1e-4f, "width 2/3" );
			Near( widths[3], 0f, 1e-4f, "width tail" );
			// Single-point trail gets the head multiplier.
			var single = taper.WidthsFor( 1, 10f );
			Check( single.Length == 1 && MathF.Abs( single[0] - 10f ) < 1e-4f, "single-point width must be head width" );
		} );

		registry.Add( "particle-2d-trails", "clear zeroes a dead trail in place and blocks leaks on respawn", () =>
		{
			var buf = new Particle2DTrailBuffer( 3 );
			buf.SpawnAt( P( 9, 9 ) );
			buf.Record( P( 10, 9 ) );
			buf.Clear();
			Check( buf.IsEmpty && buf.Count == 0 && buf.Snapshot().Length == 0, "cleared buffer must read empty" );
			// Respawn at a new position: snapshot is all new position, no old points.
			buf.SpawnAt( P( 50, 60 ) );
			foreach ( var p in buf.Snapshot() ) NearVec( p, P( 50, 60 ), 1e-4f, "respawn point" );
			// Draw-record Clear zeroes in place: length kept, points origin, widths 0.
			var rec = Particle2DTrails.BuildTrail(
				new[] { P( 1, 2 ), P( 3, 4 ) }, Particle2DTrailWidthCurve.Constant( 1f ), 5f );
			rec.Clear();
			Check( rec.Count == 2, $"cleared record keeps length 2, got {rec.Count}" );
			foreach ( var p in rec.Points ) Check( p == Vector2.Zero, "cleared record point must be origin" );
			foreach ( var w in rec.Widths ) Check( w == 0f, "cleared record width must be 0" );
			// TrailSet.Clear on one slot leaves its neighbours alone.
			var set = new Particle2DTrailSet( 2, 3 );
			set.Spawn( 0, P( 1, 1 ) );
			set.Spawn( 1, P( 2, 2 ) );
			set.Clear( 0 );
			Check( set.Count( 0 ) == 0 && set.Count( 1 ) == 3, "set clear must be per-slot" );
		} );

		registry.Add( "particle-2d-trails", "trail lengths and counts reject non-positive values loudly", () =>
		{
			ThrowsAny( () => new Particle2DTrailBuffer( 0 ), "trail length 0" );
			ThrowsAny( () => new Particle2DTrailBuffer( -2 ), "trail length negative" );
			ThrowsAny( () => new Particle2DTrailSet( 0, 3 ), "slot count 0" );
			ThrowsAny( () => new Particle2DTrailSet( 3, 0 ), "set trail length 0" );
			ThrowsAny( () => Particle2DTrails.ResampleBySpacing( new[] { P( 0, 0 ), P( 1, 0 ) }, 0f ), "spacing 0" );
			ThrowsAny( () => Particle2DTrails.ResampleBySpacing( new[] { P( 0, 0 ), P( 1, 0 ) }, -1f ), "spacing negative" );
			ThrowsAny( () => Particle2DTrails.ResampleBySpacing( new[] { P( 0, 0 ), P( 1, 0 ) }, float.NaN ), "spacing NaN" );
			ThrowsAny( () => Particle2DTrails.ResampleBySpacing( new[] { P( 0, 0 ), P( 1, 0 ) }, float.PositiveInfinity ), "spacing infinite" );
			ThrowsAny( () => Particle2DTrails.ResampleByCount( new[] { P( 0, 0 ), P( 1, 0 ) }, 0 ), "count 0" );
			ThrowsAny( () => Particle2DTrails.ResampleByCount( new[] { P( 0, 0 ), P( 1, 0 ) }, -3 ), "count negative" );
			ThrowsAny( () => Particle2DTrails.Smooth( new[] { P( 0, 0 ) }, 0 ), "window 0" );
			ThrowsAny( () => Particle2DTrails.Smooth( new[] { P( 0, 0 ) }, -1 ), "window negative" );
			ThrowsAny( () => Particle2DTrails.BuildTrail( new[] { P( 0, 0 ) }, Particle2DTrailWidthCurve.Constant( 1f ), 0f ), "base width 0" );
			ThrowsAny( () => Particle2DTrails.BuildTrail( new[] { P( 0, 0 ) }, Particle2DTrailWidthCurve.Constant( 1f ), -2f ), "base width negative" );
		} );

		registry.Add( "particle-2d-trails", "non-finite positions fail loudly everywhere", () =>
		{
			ThrowsAny( () => new Particle2DTrailBuffer( 3 ).SpawnAt( new Vector2( float.NaN, 0 ) ), "spawn NaN" );
			ThrowsAny( () => new Particle2DTrailBuffer( 3 ).SpawnAt( new Vector2( 0, float.PositiveInfinity ) ), "spawn infinite" );
			ThrowsAny( () => new Particle2DTrailBuffer( 3 ).Record( new Vector2( float.NaN, 1 ) ), "record NaN" );
			ThrowsAny( () => Particle2DTrails.ResampleBySpacing( new[] { P( 0, 0 ), new Vector2( float.NaN, 0 ) }, 1f ), "resample NaN point" );
			ThrowsAny( () => Particle2DTrails.ResampleByCount( new[] { P( 0, 0 ), new Vector2( 0, float.PositiveInfinity ) }, 2 ), "resample infinite point" );
			ThrowsAny( () => Particle2DTrails.Smooth( new[] { new Vector2( float.NaN, 0 ), P( 1, 0 ) }, 3 ), "smooth NaN point" );
			ThrowsAny( () => Particle2DTrails.BuildTrail( new[] { new Vector2( 0, float.NegativeInfinity ) }, Particle2DTrailWidthCurve.Constant( 1f ), 1f ), "build infinite point" );
			ThrowsAny( () => new Particle2DTrailDrawRecord( new[] { new Vector2( float.NaN, 0 ) }, new[] { 1f } ), "draw record NaN point" );
		} );

		registry.Add( "particle-2d-trails", "null and empty inputs fail loudly", () =>
		{
			ThrowsAny( () => Particle2DTrails.ResampleBySpacing( null!, 1f ), "resample null list" );
			ThrowsAny( () => Particle2DTrails.ResampleByCount( null!, 2 ), "resample-by-count null list" );
			ThrowsAny( () => Particle2DTrails.Smooth( null!, 3 ), "smooth null list" );
			ThrowsAny( () => Particle2DTrails.ResampleBySpacing( Array.Empty<Vector2>(), 1f ), "resample empty list" );
			ThrowsAny( () => Particle2DTrails.Smooth( Array.Empty<Vector2>(), 3 ), "smooth empty list" );
			ThrowsAny( () => Particle2DTrails.BuildTrail( new[] { P( 0, 0 ) }, null!, 1f ), "build null curve" );
			ThrowsAny( () => Particle2DTrails.BuildTrail( (Particle2DTrailBuffer)null!, Particle2DTrailWidthCurve.Constant( 1f ), 1f ), "build null buffer" );
			ThrowsAny( () => new Particle2DTrailWidthCurve( null! ), "curve null keys" );
			ThrowsAny( () => new Particle2DTrailWidthCurve( Array.Empty<Particle2DTrailWidthKey>() ), "curve empty keys" );
			ThrowsAny( () => new Particle2DTrailDrawRecord( null!, new[] { 1f } ), "draw record null points" );
			ThrowsAny( () => new Particle2DTrailDrawRecord( new[] { P( 0, 0 ) }, null! ), "draw record null widths" );
			ThrowsAny( () => new Particle2DTrailDrawRecord( new[] { P( 0, 0 ), P( 1, 0 ) }, new[] { 1f } ), "draw record mismatched lengths" );
			ThrowsAny( () => new Particle2DTrailDrawRecord( new[] { P( 0, 0 ) }, new[] { -1f } ), "draw record negative width" );
			ThrowsAny( () => new Particle2DTrailDrawRecord( new[] { P( 0, 0 ) }, new[] { float.NaN } ), "draw record NaN width" );
			// Empty buffer has no draw record: sweep must skip it, not draw it.
			var empty = new Particle2DTrailBuffer( 3 );
			ThrowsAny( () => Particle2DTrails.BuildTrail( empty, Particle2DTrailWidthCurve.Constant( 1f ), 1f ), "build from empty buffer" );
		} );

		registry.Add( "particle-2d-trails", "width curve keys and slots validate loudly", () =>
		{
			ThrowsAny( () => new Particle2DTrailWidthKey( -0.1f, 1f ), "key position below 0" );
			ThrowsAny( () => new Particle2DTrailWidthKey( 1.1f, 1f ), "key position above 1" );
			ThrowsAny( () => new Particle2DTrailWidthKey( 0.5f, -1f ), "key negative multiplier" );
			ThrowsAny( () => new Particle2DTrailWidthKey( float.NaN, 1f ), "key NaN position" );
			ThrowsAny( () => new Particle2DTrailWidthCurve( new[]
			{
				new Particle2DTrailWidthKey( 0f, 1f ), new Particle2DTrailWidthKey( 0f, 0f ),
			} ), "curve duplicate positions" );
			ThrowsAny( () => new Particle2DTrailWidthCurve( new[]
			{
				new Particle2DTrailWidthKey( 0.5f, 1f ), new Particle2DTrailWidthKey( 0.25f, 0f ),
			} ), "curve decreasing positions" );
			ThrowsAny( () => Particle2DTrailWidthCurve.Constant( -1f ), "constant negative multiplier" );
			ThrowsAny( () => Particle2DTrailWidthCurve.Taper( -0.5f ), "taper negative tail" );
			ThrowsAny( () => new Particle2DTrailWidthCurve( new[] { new Particle2DTrailWidthKey( 0f, 1f ) } ).Evaluate( float.NaN ), "evaluate NaN t" );
			var set = new Particle2DTrailSet( 2, 3 );
			ThrowsAny( () => set.Buffer( -1 ), "slot -1" );
			ThrowsAny( () => set.Buffer( 2 ), "slot == count" );
			ThrowsAny( () => set.Record( 5, P( 0, 0 ) ), "record out-of-range slot" );
		} );

		registry.Add( "particle-2d-trails", "build pipelines attach index-aligned points and widths", () =>
		{
			var buf = new Particle2DTrailBuffer( 6 );
			buf.SpawnAt( P( 0, 0 ) );
			buf.Record( P( 1, 0 ) );
			buf.Record( P( 2, 0 ) );
			buf.Record( P( 3, 0 ) );
			buf.Record( P( 4, 0 ) );
			// Snapshot newest-first: 4,3,2,1,0,0.
			var bySpacing = Particle2DTrails.BuildTrailBySpacing( buf, Particle2DTrailWidthCurve.Taper( 0f ), 8f, 2f );
			Check( bySpacing.Points.Length == bySpacing.Widths.Length, "by-spacing points/widths must align" );
			SamePoints( bySpacing.Points, new[] { P( 4, 0 ), P( 2, 0 ), P( 0, 0 ) }, "by-spacing points" );
			Near( bySpacing.Widths[0], 8f, 1e-4f, "by-spacing head width" );
			Near( bySpacing.Widths[1], 4f, 1e-4f, "by-spacing mid width" );
			Near( bySpacing.Widths[2], 0f, 1e-4f, "by-spacing tail width" );
			// Head of the record coincides with the particle's newest position.
			NearVec( bySpacing.Points[0], buf.Head, 1e-4f, "record head == buffer head" );
			var byCount = Particle2DTrails.BuildTrailByCount( buf, Particle2DTrailWidthCurve.Constant( 1f ), 3f, 3 );
			Check( byCount.Points.Length == 3 && byCount.Widths.Length == 3, "by-count must return exactly 3 stations" );
			NearVec( byCount.Points[0], P( 4, 0 ), 1e-4f, "by-count head" );
			NearVec( byCount.Points[2], P( 0, 0 ), 1e-4f, "by-count tail" );
			foreach ( var w in byCount.Widths ) Near( w, 3f, 1e-4f, "by-count constant width" );
			// Smoothing through the pipeline keeps the length contract of the resampler.
			var smooth = Particle2DTrails.BuildTrailByCount( buf, Particle2DTrailWidthCurve.Taper( 0f ), 2f, 5, smoothWindow: 3 );
			Check( smooth.Points.Length == 5, $"smoothed by-count length {smooth.Points.Length}, expected 5" );
		} );

		registry.Add( "particle-2d-trails", "same input sequence gives identical trails every time", () =>
		{
			Vector2[] Run()
			{
				var buf = new Particle2DTrailBuffer( 4 );
				buf.SpawnAt( P( 10, 10 ) );
				buf.Record( P( 12, 11 ) );
				buf.Record( P( 15, 13 ) );
				buf.Record( P( 14, 16 ) );
				return Particle2DTrails.ResampleByCount( buf.Snapshot(), 3 );
			}
			var a = Run();
			var b = Run();
			SamePoints( a, b, "determinism resample" );
			var wa = Particle2DTrails.BuildTrailBySpacing( new[] { P( 0, 0 ), P( 1, 0 ), P( 2, 0 ) }, Particle2DTrailWidthCurve.Taper( 0f ), 4f, 1f );
			var wb = Particle2DTrails.BuildTrailBySpacing( new[] { P( 0, 0 ), P( 1, 0 ), P( 2, 0 ) }, Particle2DTrailWidthCurve.Taper( 0f ), 4f, 1f );
			SamePoints( wa.Points, wb.Points, "determinism points" );
			Check( wa.Widths.Length == wb.Widths.Length, "determinism widths length" );
			for ( int i = 0; i < wa.Widths.Length; i++ ) Near( wa.Widths[i], wb.Widths[i], 0f, "determinism width" );
		} );

		registry.Add( "particle-2d-trails", "single-point trails survive every operation", () =>
		{
			var one = new[] { P( 3, 4 ) };
			SamePoints( Particle2DTrails.ResampleBySpacing( one, 1f ), one, "spacing single" );
			var counted = Particle2DTrails.ResampleByCount( one, 3 );
			Check( counted.Length == 3, $"count single length {counted.Length}, expected 3" );
			foreach ( var p in counted ) NearVec( p, P( 3, 4 ), 1e-4f, "count single copy" );
			SamePoints( Particle2DTrails.Smooth( one, 5 ), one, "smooth single" );
			var rec = Particle2DTrails.BuildTrail( one, Particle2DTrailWidthCurve.Taper( 0f ), 6f );
			Check( rec.Count == 1 && MathF.Abs( rec.Widths[0] - 6f ) < 1e-4f, "single build must carry head width" );
		} );
	}
}
