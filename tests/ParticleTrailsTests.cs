using System;
using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleTrailsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float actual, float expected, float tol, string what )
		{
			if ( MathF.Abs( actual - expected ) > tol ) throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		void NearVec( Vector3 actual, Vector3 expected, float tol, string what )
		{
			if ( Vector3.Distance( actual, expected ) > tol ) throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		void ThrowsAny( Action action, string what )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( $"{what}: did not throw" );
		}

		Vector3 X( float x ) => new( x, 0, 0 );

		registry.Add( "particle-trails", "resample by spacing keeps minimum-distance points and the tail", () =>
		{
			var raw = new[] { X( 0 ), X( 0.5f ), X( 1 ), X( 1.5f ), X( 2 ) };
			var got = ParticleTrails.ResampleBySpacing( raw, 1f );
			Check( got.Length == 3, $"expected 3 points, got {got.Length}" );
			NearVec( got[0], X( 0 ), 1e-4f, "point 0" );
			NearVec( got[1], X( 1 ), 1e-4f, "point 1" );
			NearVec( got[2], X( 2 ), 1e-4f, "point 2 (tail kept)" );
			// Tail endpoint kept even when closer than spacing: last kept is 1, tail 1.8.
			var tail = ParticleTrails.ResampleBySpacing( new[] { X( 0 ), X( 1 ), X( 1.8f ) }, 1f );
			Check( tail.Length == 3, $"expected 3 points incl. close tail, got {tail.Length}" );
			NearVec( tail[2], X( 1.8f ), 1e-4f, "close tail" );
		} );

		registry.Add( "particle-trails", "resample by count spaces evenly by arc length", () =>
		{
			var got = ParticleTrails.ResampleByCount( new[] { X( 0 ), X( 1 ), X( 2 ), X( 3 ), X( 4 ) }, 3 );
			Check( got.Length == 3, $"expected 3 points, got {got.Length}" );
			NearVec( got[0], X( 0 ), 1e-4f, "head" );
			NearVec( got[1], X( 2 ), 1e-4f, "mid" );
			NearVec( got[2], X( 4 ), 1e-4f, "tail" );
		} );

		registry.Add( "particle-trails", "resample by count interpolates inside segments", () =>
		{
			// Total length 4; targets 0, 2, 4 — the middle target lands inside the 0→3 segment.
			var got = ParticleTrails.ResampleByCount( new[] { X( 0 ), X( 3 ), X( 4 ) }, 3 );
			NearVec( got[1], X( 2 ), 1e-4f, "interpolated mid" );
			NearVec( got[2], X( 4 ), 1e-4f, "tail" );
			var one = ParticleTrails.ResampleByCount( new[] { X( 5 ), X( 6 ) }, 1 );
			Check( one.Length == 1 && one[0] == X( 5 ), "count 1 should return just the head point" );
			var flat = ParticleTrails.ResampleByCount( new[] { X( 7 ), X( 7 ), X( 7 ) }, 4 );
			Check( flat.Length == 4, $"zero-length trail must keep its count, got {flat.Length}" );
			foreach ( var p in flat ) NearVec( p, X( 7 ), 1e-4f, "flat point" );
		} );

		registry.Add( "particle-trails", "smoothing averages a jittered trail with clamped endpoints", () =>
		{
			var raw = new[] { X( 0 ), X( 1.1f ), X( 1.9f ), X( 3.1f ), X( 4 ) };
			var got = ParticleTrails.Smooth( raw, 3 );
			Check( got.Length == 5, $"smoothing must not shrink the list, got {got.Length}" );
			// Window 3, clamped endpoints: (0+0+1.1)/3, (0+1.1+1.9)/3, (1.1+1.9+3.1)/3, (1.9+3.1+4)/3, (3.1+4+4)/3.
			Near( got[0].X, 0.3666667f, 1e-4f, "smoothed 0" );
			Near( got[1].X, 1.0f, 1e-4f, "smoothed 1" );
			Near( got[2].X, 2.0333333f, 1e-4f, "smoothed 2" );
			Near( got[3].X, 3.0f, 1e-4f, "smoothed 3" );
			Near( got[4].X, 3.7f, 1e-4f, "smoothed 4" );
		} );

		registry.Add( "particle-trails", "smoothing never shrinks and window 1 is identity", () =>
		{
			var raw = new[] { X( 0 ), X( 2 ), X( 3 ) };
			var wide = ParticleTrails.Smooth( raw, 9 );
			Check( wide.Length == 3, $"window wider than the trail must still return 3 points, got {wide.Length}" );
			var ident = ParticleTrails.Smooth( raw, 1 );
			Check( ident.Length == 3 && ident[0] == X( 0 ) && ident[1] == X( 2 ) && ident[2] == X( 3 ), "window 1 must be identity" );
		} );

		registry.Add( "particle-trails", "width curve gives head 1, mid and tail multipliers", () =>
		{
			var curve = new TrailWidthCurve( new[]
			{
				new TrailWidthKey( 0f, 1f ), new TrailWidthKey( 0.5f, 0.6f ), new TrailWidthKey( 1f, 0.2f ),
			} );
			Near( curve.Evaluate( 0f ), 1f, 1e-4f, "head multiplier" );
			Near( curve.Evaluate( 0.5f ), 0.6f, 1e-4f, "mid multiplier" );
			Near( curve.Evaluate( 1f ), 0.2f, 1e-4f, "tail multiplier" );
			Near( curve.Evaluate( 0.25f ), 0.8f, 1e-4f, "quarter interpolation" );
			Near( curve.Evaluate( -1f ), 1f, 1e-4f, "below head clamps to head" );
			Near( curve.Evaluate( 2f ), 0.2f, 1e-4f, "past tail clamps to tail" );
		} );

		registry.Add( "particle-trails", "widths for a trail scale base width by the curve", () =>
		{
			var curve = new TrailWidthCurve( new[]
			{
				new TrailWidthKey( 0f, 1f ), new TrailWidthKey( 0.5f, 0.6f ), new TrailWidthKey( 1f, 0.2f ),
			} );
			var widths = curve.WidthsFor( 5, 2f );
			// t = 0, .25, .5, .75, 1 → multipliers 1, .8, .6, .4, .2 → widths 2, 1.6, 1.2, .8, .4.
			Near( widths[0], 2f, 1e-4f, "head width" );
			Near( widths[1], 1.6f, 1e-4f, "width 1" );
			Near( widths[2], 1.2f, 1e-4f, "mid width" );
			Near( widths[3], 0.8f, 1e-4f, "width 3" );
			Near( widths[4], 0.4f, 1e-4f, "tail width" );
			var taper = TrailWidthCurve.Taper( 0.3f );
			Near( taper.Evaluate( 0f ), 1f, 1e-4f, "default taper head" );
			Near( taper.Evaluate( 1f ), 0.3f, 1e-4f, "default taper tail" );
		} );

		registry.Add( "particle-trails", "build by spacing returns aligned points and widths", () =>
		{
			var raw = new[] { X( 0 ), X( 0.5f ), X( 1 ), X( 1.5f ), X( 2 ) };
			var data = ParticleTrails.BuildTrailBySpacing( raw, TrailWidthCurve.Taper( 0.3f ), 2f, 1f );
			Check( data.Count == 3, $"expected 3 stations, got {data.Count}" );
			Check( data.Points.Length == data.Widths.Length, "points and widths must align" );
			NearVec( data.Points[0], X( 0 ), 1e-4f, "head point" );
			NearVec( data.Points[2], X( 2 ), 1e-4f, "tail point" );
			Near( data.Widths[0], 2f, 1e-4f, "head width (2 × 1)" );
			Near( data.Widths[1], 1.3f, 1e-4f, "mid width (2 × 0.65)" );
			Near( data.Widths[2], 0.6f, 1e-4f, "tail width (2 × 0.3)" );
			var byCount = ParticleTrails.BuildTrailByCount( raw, TrailWidthCurve.Constant( 0.5f ), 4f, 3 );
			Check( byCount.Count == 3, $"by-count build must return exactly 3, got {byCount.Count}" );
			foreach ( var w in byCount.Widths ) Near( w, 2f, 1e-4f, "constant-curve width" );
		} );

		registry.Add( "particle-trails", "clear zeroes a trail and reuse leaves no old points", () =>
		{
			var buffer = new TrailBuffer();
			var first = ParticleTrails.BuildTrail( new[] { X( 0 ), X( 1 ), X( 2 ) }, TrailWidthCurve.Taper( 0.3f ), 2f );
			buffer.Set( first );
			Check( buffer.Count == 3, "buffer should hold 3 before clear" );
			buffer.Clear();
			Check( buffer.Count == 0, $"cleared buffer must read empty, got {buffer.Count}" );
			foreach ( var w in first.Widths ) Near( w, 0f, 1e-4f, "cleared width must be 0" );
			foreach ( var p in first.Points ) NearVec( p, Vector3.Zero, 1e-4f, "cleared point must be origin" );
			// Replacement trail: only its own points, nothing from the dead particle.
			var second = ParticleTrails.BuildTrail( new[] { X( 10 ), X( 11 ) }, TrailWidthCurve.Taper( 0.5f ), 1f );
			buffer.Set( second );
			Check( buffer.Count == 2, $"reused buffer must hold 2, got {buffer.Count}" );
			NearVec( buffer.Current.Points[0], X( 10 ), 1e-4f, "replacement head" );
			NearVec( buffer.Current.Points[1], X( 11 ), 1e-4f, "replacement tail" );
			var direct = ParticleTrails.BuildTrail( new[] { X( 3 ), X( 4 ) }, TrailWidthCurve.Taper( 0.5f ), 1f );
			direct.Clear();
			foreach ( var w in direct.Widths ) Near( w, 0f, 1e-4f, "TrailData.Clear width" );
		} );

		registry.Add( "particle-trails", "validate rejects non-positive spacing count and window", () =>
		{
			var raw = new[] { X( 0 ), X( 1 ) };
			ThrowsAny( () => ParticleTrails.ResampleBySpacing( raw, 0f ), "spacing 0" );
			ThrowsAny( () => ParticleTrails.ResampleBySpacing( raw, -1f ), "spacing -1" );
			ThrowsAny( () => ParticleTrails.ResampleBySpacing( raw, float.NaN ), "spacing NaN" );
			ThrowsAny( () => ParticleTrails.ResampleByCount( raw, 0 ), "count 0" );
			ThrowsAny( () => ParticleTrails.ResampleByCount( raw, -3 ), "count -3" );
			ThrowsAny( () => ParticleTrails.Smooth( raw, 0 ), "window 0" );
			ThrowsAny( () => ParticleTrails.Smooth( raw, -2 ), "window -2" );
			ThrowsAny( () => ParticleTrails.Validate( raw, 0f, 1, 1 ), "combined Validate spacing 0" );
			ThrowsAny( () => ParticleTrails.Validate( raw, 1f, 0, 1 ), "combined Validate count 0" );
			ThrowsAny( () => ParticleTrails.Validate( raw, 1f, 1, 0 ), "combined Validate window 0" );
			ThrowsAny( () => ParticleTrails.BuildTrail( raw, TrailWidthCurve.Taper( 0.3f ), 0f ), "base width 0" );
			ThrowsAny( () => ParticleTrails.BuildTrail( raw, TrailWidthCurve.Taper( 0.3f ), -1f ), "base width -1" );
		} );

		registry.Add( "particle-trails", "validate rejects empty lists bad curves and mismatched trail data", () =>
		{
			var empty = Array.Empty<Vector3>();
			ThrowsAny( () => ParticleTrails.ResampleBySpacing( empty, 1f ), "empty resample spacing" );
			ThrowsAny( () => ParticleTrails.ResampleByCount( empty, 3 ), "empty resample count" );
			ThrowsAny( () => ParticleTrails.Smooth( empty, 3 ), "empty smooth" );
			ThrowsAny( () => ParticleTrails.BuildTrail( empty, TrailWidthCurve.Taper( 0.3f ), 1f ), "empty build" );
			ThrowsAny( () => ParticleTrails.BuildTrail( new[] { X( 0 ) }, null!, 1f ), "null curve" );
			ThrowsAny( () => new TrailWidthCurve( Array.Empty<TrailWidthKey>() ), "empty curve" );
			ThrowsAny( () => new TrailWidthCurve( new[] { new TrailWidthKey( 0.5f, 1f ), new TrailWidthKey( 0.5f, 0.5f ) } ), "duplicate key positions" );
			ThrowsAny( () => new TrailWidthCurve( new[] { new TrailWidthKey( 1f, 1f ), new TrailWidthKey( 0f, 1f ) } ), "decreasing key positions" );
			ThrowsAny( () => new TrailWidthKey( 1.5f, 1f ), "key position > 1" );
			ThrowsAny( () => new TrailWidthKey( 0f, -0.5f ), "negative multiplier" );
			ThrowsAny( () => new TrailData( new[] { X( 0 ), X( 1 ) }, new[] { 1f } ), "mismatched points/widths" );
			ThrowsAny( () => new TrailData( new[] { new Vector3( float.NaN, 0, 0 ) }, new[] { 1f } ), "non-finite point" );
			ThrowsAny( () => new TrailData( new[] { X( 0 ) }, new[] { -1f } ), "negative width" );
		} );

		registry.Add( "particle-trails", "processing preserves newest-first order and never mutates input", () =>
		{
			var raw = new[] { new Vector3( 0, 0, 5 ), new Vector3( 0, 0, 4 ), new Vector3( 0, 0, 3 ) };
			var smoothed = ParticleTrails.Smooth( raw, 1 );
			Check( smoothed[0].Z == 5 && smoothed[2].Z == 3, "newest-first order must be preserved" );
			ParticleTrails.ResampleBySpacing( raw, 0.5f );
			ParticleTrails.Smooth( raw, 3 );
			Check( raw[0].Z == 5 && raw[1].Z == 4 && raw[2].Z == 3, "input list must be unmutated" );
		} );
	}
}
