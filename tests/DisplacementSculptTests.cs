using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

// Displacement sculpt modes (hammer-displacement-sculpt-brush-modes).
//
// Displacement itself (hammer-displacement-data-model) HAS landed, and three checks
// below drive the real Displacement through DisplacementAdapter. The remaining checks
// run against TestDisplacement, a faithful stand-in for the spec'd grid semantics
// (power 2/3/4, scalar offsets along the face normal, alpha 0..1, Sew matching shared
// edges by base position) — kept because it lets mirror/stamp/validation checks stay
// cheap and because it proves the engine against the IDisplacementSurface seam, not
// only against one implementation.
internal static class DisplacementSculptTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "displacementsculpt", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tolerance, string what )
		{
			if ( MathF.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{what}: expected {expected:0.####}, got {actual:0.####}" );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		string FindFixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-displacement-sculpt-brush-modes" );
					if ( Directory.Exists( candidate ) )
						return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "fixture dir tests/fixtures/hammer/hammer-displacement-sculpt-brush-modes not found" );
		}

		Vec3 Vec( JsonElement array ) => new(
			array[0].GetSingle(), array[1].GetSingle(), array[2].GetSingle() );

		BrushSample Dab( JsonElement dab ) => new(
			Vec( dab.GetProperty( "centre" ) ),
			Vec( dab.GetProperty( "normal" ) ),
			dab.GetProperty( "radius" ).GetSingle(),
			dab.GetProperty( "strength" ).GetSingle() );

		BrushFalloff Falloff( string name ) => name switch
		{
			"Linear" => BrushFalloff.Linear,
			"Sharp" => BrushFalloff.Sharp,
			"Constant" => BrushFalloff.Constant,
			_ => BrushFalloff.Smooth,
		};

		Check( "power-3 grid has side 9 and 81 vertices; powers 2 and 4 give sides 5 and 17", () =>
		{
			Require( DisplacementSculpt.GridSide( 2 ) == 5, "power 2 side" );
			Require( DisplacementSculpt.GridSide( 3 ) == 9, "power 3 side" );
			Require( DisplacementSculpt.GridSide( 4 ) == 17, "power 4 side" );
			Require( DisplacementSculpt.GridVertexCount( 3 ) == 81, "power 3 count" );
			var grid = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			Require( grid.Side == 9 && grid.Offsets.Length == 81 && grid.Alpha.Length == 81, "grid arrays" );
			Require( DisplacementSculpt.ValidateSurface( grid ).Length == 0, "valid grid must validate clean" );
		} );

		Check( "a real Displacement binds through the adapter and sculpts with the same numbers", () =>
		{
			var face = Effigy.Map.MapBrushFace.FromCorners( new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 64, 64, 0 ), new Vec3( 0, 64, 0 ),
			} );
			var disp = Displacement.Create( face, 3 );
			var adapter = new DisplacementAdapter( disp );
			Require( adapter.Side == 9 && adapter.Power == 3, "adapter grid" );
			Require( ReferenceEquals( adapter.Offsets, disp.Offsets ), "adapter shares the offsets array" );
			Require( ReferenceEquals( adapter.Alpha, disp.Alphas ), "adapter shares the alphas array" );
			Require( DisplacementSculpt.GridSide( 3 ) == Displacement.GridSizeForPower( 3 ), "GridSide aliases GridSizeForPower" );
			Require( DisplacementSculpt.ValidateSurface( adapter ).Length == 0, "real displacement validates clean" );
			var dab = new BrushSample( new Vec3( 32, 32, 0 ), new Vec3( 0, 0, 1 ), 20f, 2f );
			DisplacementSculpt.ApplyDab( disp, dab, DisplacementSculptMode.Raise, BrushFalloff.Smooth, null );
			Near( disp.GetOffset( 4, 4 ), 2f, 1e-4f, "real centre offset" );
			Near( disp.GetOffset( 3, 4 ), 1.296f, 1e-3f, "real near-centre offset" );
			Near( disp.GetOffset( 2, 4 ), 0.208f, 1e-3f, "real mid offset" );
			Require( disp.GetOffset( 1, 4 ) == 0f, "real outside-radius vertex stays 0" );
		} );

		Check( "one dab raises the centre vertex most and edge vertices least (fixture dab-centre.json)", () =>
		{
			var json = JsonDocument.Parse( File.ReadAllText( Path.Combine( FindFixtureDir(), "dab-centre.json" ) ) ).RootElement;
			var grid = new TestDisplacement(
				json.GetProperty( "power" ).GetInt32(),
				Vec( json.GetProperty( "origin" ) ),
				Vec( json.GetProperty( "u" ) ),
				Vec( json.GetProperty( "v" ) ) );
			var dab = Dab( json.GetProperty( "dab" ) );
			var falloff = Falloff( json.GetProperty( "dab" ).GetProperty( "falloff" ).GetString() );
			var result = DisplacementSculpt.ApplyDab( grid, dab, DisplacementSculptMode.Raise, falloff, null );

			// Centre (32,32): weight 1 -> +2. (32,24): t=0.4, smooth 0.648 -> +1.296.
			// (32,16): t=0.8, smooth 0.104 -> +0.208. (32,8): outside the radius -> 0.
			Near( grid.Offsets[4 * 9 + 4], 2f, 1e-4f, "centre offset" );
			Near( grid.Offsets[3 * 9 + 4], 1.296f, 1e-3f, "near-centre offset" );
			Near( grid.Offsets[2 * 9 + 4], 0.208f, 1e-3f, "mid offset" );
			Require( grid.Offsets[1 * 9 + 4] == 0f, "outside-radius vertex must stay 0" );
			Require( grid.Offsets[4 * 9 + 4] > grid.Offsets[3 * 9 + 4] && grid.Offsets[3 * 9 + 4] > grid.Offsets[2 * 9 + 4],
				"centre > near-centre > mid" );
			Require( result.VerticesTouched > 0 && result.VerticesTouched < 81, $"touched {result.VerticesTouched}" );
			Near( result.MaxOffsetDelta, 2f, 1e-4f, "max delta" );
		} );

		Check( "falloff weights: smooth/linear/sharp/constant agree at 0, differ at half radius, end at 0", () =>
		{
			Near( DisplacementSculpt.FalloffWeight( 0f, 10f, BrushFalloff.Smooth ), 1f, 1e-6f, "smooth t=0" );
			Near( DisplacementSculpt.FalloffWeight( 5f, 10f, BrushFalloff.Smooth ), 0.5f, 1e-6f, "smooth t=0.5" );
			Near( DisplacementSculpt.FalloffWeight( 5f, 10f, BrushFalloff.Linear ), 0.5f, 1e-6f, "linear t=0.5" );
			Near( DisplacementSculpt.FalloffWeight( 5f, 10f, BrushFalloff.Sharp ), 0.25f, 1e-6f, "sharp t=0.5" );
			Near( DisplacementSculpt.FalloffWeight( 5f, 10f, BrushFalloff.Constant ), 1f, 1e-6f, "constant t=0.5" );
			Near( DisplacementSculpt.FalloffWeight( 10f, 10f, BrushFalloff.Constant ), 0f, 1e-6f, "constant ends at the radius" );
			Near( DisplacementSculpt.FalloffWeight( 11f, 10f, BrushFalloff.Smooth ), 0f, 1e-6f, "beyond the radius" );
		} );

		Check( "lower pushes the centre down most; raise along X on a Z-facing grid is a no-op", () =>
		{
			var grid = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			var dab = new BrushSample( new Vec3( 32, 32, 0 ), new Vec3( 0, 0, 1 ), 20f, 2f );
			DisplacementSculpt.ApplyDab( grid, dab, DisplacementSculptMode.Lower, BrushFalloff.Smooth, null );
			Near( grid.Offsets[4 * 9 + 4], -2f, 1e-4f, "lowered centre" );
			Require( grid.Offsets[4 * 9 + 4] < grid.Offsets[3 * 9 + 4] && grid.Offsets[3 * 9 + 4] < grid.Offsets[2 * 9 + 4],
				"lower ordering" );

			var flat = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			var across = new DisplacementSculptOptions { RaiseAxis = DisplacementRaiseAxis.X };
			DisplacementSculpt.ApplyDab( flat, dab, DisplacementSculptMode.Raise, BrushFalloff.Smooth, null, across );
			Require( flat.Offsets.All( o => o == 0f ), "X raise on a Z-facing grid must do nothing" );
			var along = new DisplacementSculptOptions { RaiseAxis = DisplacementRaiseAxis.Z };
			DisplacementSculpt.ApplyDab( flat, dab, DisplacementSculptMode.Raise, BrushFalloff.Smooth, null, along );
			Near( flat.Offsets[4 * 9 + 4], 2f, 1e-4f, "Z raise on a Z-facing grid is full strength" );

			// X-facing grid: raising along X is full strength, along Z is a no-op.
			var side = new TestDisplacement( 2, new Vec3( 0, 0, 0 ), new Vec3( 0, 32, 0 ), new Vec3( 0, 0, 32 ) );
			Require( side.Normal.AlmostEquals( new Vec3( 1, 0, 0 ) ), "X-facing normal" );
			var sideDab = new BrushSample( new Vec3( 0, 16, 16 ), new Vec3( 1, 0, 0 ), 20f, 2f );
			DisplacementSculpt.ApplyDab( side, sideDab, DisplacementSculptMode.Raise, BrushFalloff.Smooth, null, across );
			Near( side.Offsets[2 * 5 + 2], 2f, 1e-4f, "X raise on an X-facing grid" );
		} );

		Check( "smooth reduces offset variance without inventing height", () =>
		{
			var grid = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			for ( var r = 0; r < 9; r++ )
			for ( var c = 0; c < 9; c++ )
				grid.Offsets[r * 9 + c] = ((r * 7 + c * 13) % 5 - 2) * 1f;
			var meanBefore = grid.Offsets.Average();
			var varianceBefore = grid.Offsets.Select( o => (o - meanBefore) * (o - meanBefore) ).Average();
			Require( varianceBefore > 1f, $"noise fixture must start noisy, got {varianceBefore:0.###}" );
			var dab = new BrushSample( new Vec3( 32, 32, 0 ), new Vec3( 0, 0, 1 ), 100f, 1f );
			DisplacementSculpt.ApplyDab( grid, dab, DisplacementSculptMode.Smooth, BrushFalloff.Smooth, null );
			var meanAfter = grid.Offsets.Average();
			var varianceAfter = grid.Offsets.Select( o => (o - meanAfter) * (o - meanAfter) ).Average();
			Require( varianceAfter < varianceBefore * 0.5f, $"variance {varianceBefore:0.###} -> {varianceAfter:0.###}" );
			Require( MathF.Abs( meanAfter - meanBefore ) < 0.5f, $"mean drifted {meanBefore:0.###} -> {meanAfter:0.###}" );
		} );

		Check( "flatten pulls offsets toward the height under the dab centre", () =>
		{
			var grid = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			for ( var r = 0; r < 9; r++ )
			for ( var c = 0; c < 9; c++ )
				grid.Offsets[r * 9 + c] = r;
			var dab = new BrushSample( new Vec3( 32, 32, 0 ), new Vec3( 0, 0, 1 ), 100f, 1f );
			DisplacementSculpt.ApplyDab( grid, dab, DisplacementSculptMode.Flatten, BrushFalloff.Smooth, null );
			var maxDev = grid.Offsets.Max( o => MathF.Abs( o - 4f ) );
			Require( maxDev < 2f, $"max deviation from centre height 4 must shrink below 2, got {maxDev:0.###}" );
			Near( grid.Offsets[4 * 9 + 4], 4f, 1e-4f, "centre height is the flatten target" );
		} );

		Check( "paint alpha writes blend 0..1, converges on the target, and erases back to 0", () =>
		{
			var grid = new TestDisplacement( 2, new Vec3( 0, 0, 0 ), new Vec3( 32, 0, 0 ), new Vec3( 0, 32, 0 ) );
			var stroke = new BrushStroke { Kind = BrushKind.Draw, Falloff = BrushFalloff.Smooth };
			stroke.Samples.Add( new BrushSample( new Vec3( 16, 16, 0 ), new Vec3( 0, 0, 1 ), 20f, 0.5f ) );
			var paintIn = new DisplacementSculptOptions { PaintAlphaTarget = 1f };
			DisplacementSculpt.ApplyStroke( grid, stroke, DisplacementSculptMode.PaintAlpha, paintIn );
			Near( grid.Alpha[2 * 5 + 2], 0.5f, 1e-4f, "centre alpha after a half-strength dab" );
			Near( grid.Alpha[1 * 5 + 2], 0.324f, 1e-3f, "near-centre alpha" );
			Require( grid.Alpha[0] == 0f, "outside-radius alpha stays 0" );

			stroke.Samples[0] = new BrushSample( new Vec3( 16, 16, 0 ), new Vec3( 0, 0, 1 ), 20f, 1f );
			DisplacementSculpt.ApplyStroke( grid, stroke, DisplacementSculptMode.PaintAlpha, paintIn );
			Near( grid.Alpha[2 * 5 + 2], 1f, 1e-4f, "full-strength dab lands on the target" );
			stroke.Samples[0] = new BrushSample( new Vec3( 16, 16, 0 ), new Vec3( 0, 0, 1 ), 20f, 5f );
			DisplacementSculpt.ApplyStroke( grid, stroke, DisplacementSculptMode.PaintAlpha, paintIn );
			Near( grid.Alpha[2 * 5 + 2], 1f, 1e-4f, "repeated dabs never pass 1" );
			Require( grid.Alpha.All( a => a >= 0f && a <= 1f ), "alpha stays in 0..1" );

			stroke.Samples[0] = new BrushSample( new Vec3( 16, 16, 0 ), new Vec3( 0, 0, 1 ), 20f, -1f );
			DisplacementSculpt.ApplyStroke( grid, stroke, DisplacementSculptMode.PaintAlpha, paintIn );
			Near( grid.Alpha[2 * 5 + 2], 1f, 1e-4f, "negative strength is a no-op for paint alpha" );
			var paintOut = new DisplacementSculptOptions { PaintAlphaTarget = 0f };
			stroke.Samples[0] = new BrushSample( new Vec3( 16, 16, 0 ), new Vec3( 0, 0, 1 ), 20f, 1f );
			DisplacementSculpt.ApplyStroke( grid, stroke, DisplacementSculptMode.PaintAlpha, paintOut );
			Near( grid.Alpha[2 * 5 + 2], 0f, 1e-4f, "target 0 erases the blend" );
		} );

		Check( "paint alpha on a real Displacement writes Alphas through the adapter", () =>
		{
			var disp = Displacement.Create( Effigy.Map.MapBrushFace.FromCorners( new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 32, 0, 0 ), new Vec3( 32, 32, 0 ), new Vec3( 0, 32, 0 ),
			} ), 2 );
			var stroke = new BrushStroke { Kind = BrushKind.Draw, Falloff = BrushFalloff.Smooth };
			stroke.Samples.Add( new BrushSample( new Vec3( 16, 16, 0 ), new Vec3( 0, 0, 1 ), 20f, 0.5f ) );
			DisplacementSculpt.ApplyStroke( disp, stroke, DisplacementSculptMode.PaintAlpha,
				new DisplacementSculptOptions { PaintAlphaTarget = 1f } );
			Near( disp.GetAlpha( 2, 2 ), 0.5f, 1e-4f, "real centre alpha" );
			Require( disp.GetAlpha( 0, 0 ) == 0f, "real outside-radius alpha stays 0" );
			Require( disp.Alphas.All( a => a >= 0f && a <= 1f ), "real alphas stay in 0..1" );
		} );

		Check( "auto-sew keeps the shared edge equal after a stroke on one side (fixture two-panels-sew.json)", () =>
		{
			var json = JsonDocument.Parse( File.ReadAllText( Path.Combine( FindFixtureDir(), "two-panels-sew.json" ) ) ).RootElement;
			var power = json.GetProperty( "power" ).GetInt32();
			var u = Vec( json.GetProperty( "u" ) );
			var v = Vec( json.GetProperty( "v" ) );
			var a = new TestDisplacement( power, Vec( json.GetProperty( "panelA" ).GetProperty( "origin" ) ), u, v );
			var b = new TestDisplacement( power, Vec( json.GetProperty( "panelB" ).GetProperty( "origin" ) ), u, v );
			Require( a.Sew( b ) == 5, "the two panels must share a 5-vertex edge before sculpting" );
			var dab = Dab( json.GetProperty( "dab" ) );
			var stroke = new BrushStroke { Kind = BrushKind.Draw, Falloff = BrushFalloff.Smooth };
			stroke.Samples.Add( dab );
			var result = DisplacementSculpt.ApplyStroke( a, stroke, new DisplacementSculptOptions(), new[] { b } );
			Require( result.SewMatches == 5, $"auto-sew must match 5 shared vertices, got {result.SewMatches}" );
			for ( var r = 0; r < 5; r++ )
				Near( a.Offsets[r * 5 + 4], b.Offsets[r * 5 + 0], 1e-6f, $"shared edge row {r}" );
			Require( a.Offsets[2 * 5 + 4] > 0f, "shared edge centre must be raised" );
			Near( a.Offsets[2 * 5 + 4], 1.111f, 1e-2f, "shared edge holds the mean of raised and unraised" );
			Require( a.Offsets[0 * 5 + 4] == 0f && b.Offsets[0 * 5 + 0] == 0f, "shared edge corners were out of the dab" );

			// Without neighbours registered, a stroke on A leaves B untouched.
			var c = new TestDisplacement( power, new Vec3( 0, 0, 0 ), u, v );
			var d = new TestDisplacement( power, new Vec3( 32, 0, 0 ), u, v );
			DisplacementSculpt.ApplyStroke( c, stroke );
			Require( d.Offsets.All( o => o == 0f ), "unregistered neighbour must stay untouched" );
		} );

		Check( "auto-sew on real Displacements keeps the shared edge equal and records the sew", () =>
		{
			var a = Displacement.Create( Effigy.Map.MapBrushFace.FromCorners( new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 32, 0, 0 ), new Vec3( 32, 32, 0 ), new Vec3( 0, 32, 0 ),
			} ), 2 );
			var b = Displacement.Create( Effigy.Map.MapBrushFace.FromCorners( new[]
			{
				new Vec3( 32, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 64, 32, 0 ), new Vec3( 32, 32, 0 ),
			} ), 2 );
			var stroke = new BrushStroke { Kind = BrushKind.Draw, Falloff = BrushFalloff.Smooth };
			stroke.Samples.Add( new BrushSample( new Vec3( 28, 16, 0 ), new Vec3( 0, 0, 1 ), 12f, 3f ) );
			var result = DisplacementSculpt.ApplyStroke( a, stroke, new DisplacementSculptOptions(), new[] { b } );
			Require( result.SewMatches == 5, $"real auto-sew must match 5, got {result.SewMatches}" );
			for ( var r = 0; r < 5; r++ )
				Near( a.GetOffset( r, 4 ), b.GetOffset( r, 0 ), 1e-6f, $"real shared edge row {r}" );
			Near( a.GetOffset( 2, 4 ), 1.111f, 1e-2f, "real shared edge centre holds the mean" );
			Require( a.IsSewnTo( b ) && b.IsSewnTo( a ), "both sides record the sew" );
			Require( a.SewnEdges.Contains( 1 ) && b.SewnEdges.Contains( 3 ), "A edge 1 sewn to B edge 3" );
		} );

		Check( "mirror X symmetry raises the mirrored vertex exactly as much as the dabbed one", () =>
		{
			var grid = new TestDisplacement( 3, new Vec3( -32, -32, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			var stroke = new BrushStroke { Kind = BrushKind.Draw, Falloff = BrushFalloff.Smooth, Mirror = MirrorAxis.X };
			stroke.Samples.Add( new BrushSample( new Vec3( 16, 0, 0 ), new Vec3( 0, 0, 1 ), 10f, 2f ) );
			var result = DisplacementSculpt.ApplyStroke( grid, stroke );
			Require( result.DabsApplied == 2, $"mirror X must apply 2 dabs, got {result.DabsApplied}" );
			Require( result.CopiesMissed == 0, "both copies must land on the grid" );
			Near( grid.Offsets[4 * 9 + 6], 2f, 1e-4f, "dabbed vertex (16,0)" );
			Near( grid.Offsets[4 * 9 + 2], 2f, 1e-4f, "mirrored vertex (-16,0)" );
			Near( grid.Offsets[4 * 9 + 5], grid.Offsets[4 * 9 + 3], 1e-4f, "falloff either side of centre matches" );
			Require( grid.Offsets[4 * 9 + 4] == 0f, "vertex between the two dabs is out of both radii" );
			Require( grid.Offsets[0] == 0f, "far corner untouched" );
		} );

		Check( "brush kinds map to modes; drag-only kinds refuse instead of corrupting the grid", () =>
		{
			Require( DisplacementSculpt.ModeForKind( BrushKind.Draw ) == DisplacementSculptMode.Raise, "Draw" );
			Require( DisplacementSculpt.ModeForKind( BrushKind.Clay ) == DisplacementSculptMode.Raise, "Clay" );
			Require( DisplacementSculpt.ModeForKind( BrushKind.ClayStrips ) == DisplacementSculptMode.Raise, "ClayStrips" );
			Require( DisplacementSculpt.ModeForKind( BrushKind.Inflate ) == DisplacementSculptMode.Raise, "Inflate" );
			Require( DisplacementSculpt.ModeForKind( BrushKind.Fill ) == DisplacementSculptMode.Raise, "Fill" );
			Require( DisplacementSculpt.ModeForKind( BrushKind.Scrape ) == DisplacementSculptMode.Lower, "Scrape" );
			Require( DisplacementSculpt.ModeForKind( BrushKind.Smooth ) == DisplacementSculptMode.Smooth, "Smooth" );
			Require( DisplacementSculpt.ModeForKind( BrushKind.Flatten ) == DisplacementSculptMode.Flatten, "Flatten" );
			Throws<ArgumentException>( () => DisplacementSculpt.ModeForKind( BrushKind.Grab ), "Grab" );
			Throws<ArgumentException>( () => DisplacementSculpt.ModeForKind( BrushKind.Pinch ), "Pinch" );
			Throws<ArgumentException>( () => DisplacementSculpt.ModeForKind( BrushKind.Twist ), "Twist" );
		} );

		Check( "a two-sample stroke applies both dabs and reports counts", () =>
		{
			var grid = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			var stroke = new BrushStroke { Kind = BrushKind.Clay, Falloff = BrushFalloff.Smooth };
			stroke.Samples.Add( new BrushSample( new Vec3( 16, 16, 0 ), new Vec3( 0, 0, 1 ), 12f, 2f ) );
			stroke.Samples.Add( new BrushSample( new Vec3( 48, 48, 0 ), new Vec3( 0, 0, 1 ), 12f, 2f ) );
			var result = DisplacementSculpt.ApplyStroke( grid, stroke );
			Require( result.DabsApplied == 2, "two samples, no symmetry" );
			Require( result.VerticesTouched > 0, "vertices touched" );
			Near( result.MaxOffsetDelta, 2f, 1e-4f, "max delta" );
			Near( grid.Offsets[2 * 9 + 2], 2f, 1e-4f, "first dab centre" );
			Near( grid.Offsets[6 * 9 + 6], 2f, 1e-4f, "second dab centre" );

			var empty = new BrushStroke { Kind = BrushKind.Draw };
			var none = DisplacementSculpt.ApplyStroke( grid, empty );
			Require( none.DabsApplied == 0 && none.VerticesTouched == 0, "a stroke with no samples is a no-op" );
		} );

		Check( "a brush alpha stamp scales the dab: all-zero touches nothing, softdisc trims the rim", () =>
		{
			var plain = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			var stamped = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			var dab = new BrushSample( new Vec3( 32, 32, 0 ), new Vec3( 0, 0, 1 ), 20f, 2f );
			DisplacementSculpt.ApplyDab( plain, dab, DisplacementSculptMode.Raise, BrushFalloff.Smooth, null );
			var softdisc = BrushAlpha.FromProcedural( "softdisc" );
			Require( softdisc is not null, "softdisc stamp must exist" );
			DisplacementSculpt.ApplyDab( stamped, dab, DisplacementSculptMode.Raise, BrushFalloff.Smooth, softdisc );
			Near( stamped.Offsets[4 * 9 + 4], plain.Offsets[4 * 9 + 4], 1e-4f, "stamp is 1 at the centre" );
			Require( stamped.Offsets[2 * 9 + 4] > 0f && stamped.Offsets[2 * 9 + 4] < plain.Offsets[2 * 9 + 4],
				"stamp trims the rim vertex (0.352 of the plain weight)" );
			Near( stamped.Offsets[2 * 9 + 4], 0.208f * 0.352f, 1e-2f, "rim vertex stamped value" );

			var zero = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			var blank = new BrushAlpha { Values = new float[9], Width = 3, Height = 3 };
			var untouched = DisplacementSculpt.ApplyDab( zero, dab, DisplacementSculptMode.Raise, BrushFalloff.Smooth, blank );
			Require( untouched.VerticesTouched == 0, "all-zero stamp touches nothing" );
			Require( zero.Offsets.All( o => o == 0f ), "all-zero stamp moves nothing" );
		} );

		Check( "invalid input throws: nulls, zero radius, bad power, malformed grid, bad alpha target", () =>
		{
			var grid = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) );
			var stroke = new BrushStroke { Kind = BrushKind.Draw };
			stroke.Samples.Add( new BrushSample( new Vec3( 32, 32, 0 ), new Vec3( 0, 0, 1 ), 20f, 2f ) );
			Throws<ArgumentNullException>( () => DisplacementSculpt.ApplyStroke( (IDisplacementSurface)null, stroke, DisplacementSculptMode.Raise ), "null target" );
			Throws<ArgumentNullException>( () => DisplacementSculpt.ApplyStroke( grid, null, DisplacementSculptMode.Raise ), "null stroke" );
			Throws<ArgumentException>( () => DisplacementSculpt.ApplyDab( grid,
				new BrushSample( new Vec3( 32, 32, 0 ), new Vec3( 0, 0, 1 ), 0f, 2f ),
				DisplacementSculptMode.Raise, BrushFalloff.Smooth, null ), "zero radius" );
			Throws<ArgumentOutOfRangeException>( () => DisplacementSculpt.GridSide( 1 ), "power 1" );
			Throws<ArgumentOutOfRangeException>( () => DisplacementSculpt.GridSide( 5 ), "power 5" );
			Throws<ArgumentException>( () => DisplacementSculpt.ApplyStroke( grid, stroke, DisplacementSculptMode.PaintAlpha,
				new DisplacementSculptOptions { PaintAlphaTarget = 2f } ), "alpha target 2" );

			var shortGrid = new TestDisplacement( 3, new Vec3( 0, 0, 0 ), new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 0 ) )
			{
				OffsetsOverride = new float[10],
			};
			Require( DisplacementSculpt.ValidateSurface( shortGrid ).Length > 0, "short offsets must fail validation" );
			Throws<ArgumentException>( () => DisplacementSculpt.ApplyStroke( shortGrid, stroke, DisplacementSculptMode.Raise ), "short grid" );

			var badAlpha = new TestDisplacement( 2, new Vec3( 0, 0, 0 ), new Vec3( 32, 0, 0 ), new Vec3( 0, 32, 0 ) );
			badAlpha.Alpha[0] = 2f;
			Require( DisplacementSculpt.ValidateSurface( badAlpha ).Length > 0, "alpha 2 must fail validation" );
		} );
	}
}

/// <summary>
/// Faithful stand-in for the spec'd Displacement (hammer-displacement-data-model): a flat
/// square grid, power 2..4, scalar offsets along the face normal, blend alpha 0..1, Sew
/// matching shared edges by undisplaced base position and equalising offsets + alpha on
/// them. Kept alongside the real-Displacement checks: it proves the engine against the
/// IDisplacementSurface seam itself, and it keeps mirror/stamp/validation checks cheap.
/// </summary>
internal sealed class TestDisplacement : IDisplacementSurface
{
	readonly Vec3 _origin, _u, _v;

	public TestDisplacement( int power, Vec3 origin, Vec3 u, Vec3 v )
	{
		Power = power;
		Side = DisplacementSculpt.GridSide( power );
		_origin = origin;
		_u = u;
		_v = v;
		Normal = Vec3.Cross( u, v ).Normal;
		Offsets = new float[Side * Side];
		Alpha = new float[Side * Side];
	}

	public int Power { get; }
	public int Side { get; }
	public Vec3 Origin => _origin;
	public Vec3 UAxis => _u;
	public Vec3 VAxis => _v;
	public Vec3 Normal { get; }
	public float[] Offsets { get; private set; }
	public float[] Alpha { get; }

	/// <summary>Test hook: replace the offsets array with a malformed one for validation checks.</summary>
	public float[] OffsetsOverride { set => Offsets = value; }

	public Vec3 VertexPosition( int row, int col )
	{
		var step = 1f / (Side - 1);
		return Origin + UAxis * (col * step) + VAxis * (row * step) + Normal * Offsets[row * Side + col];
	}

	Vec3 BasePosition( int row, int col )
	{
		var step = 1f / (Side - 1);
		return Origin + UAxis * (col * step) + VAxis * (row * step);
	}

	static Vec3 OtherBase( IDisplacementSurface other, int row, int col )
	{
		var step = 1f / (other.Side - 1);
		return other.Origin + other.UAxis * (col * step) + other.VAxis * (row * step);
	}

	public int Sew( IDisplacementSurface other )
	{
		if ( other is null )
			throw new ArgumentNullException( nameof( other ) );
		if ( other.Side != Side )
			return 0;

		List<(int Row, int Col)>[] Edges( int side )
		{
			var edges = new List<(int, int)>[4];
			for ( var k = 0; k < 4; k++ )
				edges[k] = new List<(int, int)>();
			for ( var c = 0; c < side; c++ )
			{
				edges[0].Add( (0, c) );
				edges[1].Add( (side - 1, c) );
			}
			for ( var r = 0; r < side; r++ )
			{
				edges[2].Add( (r, 0) );
				edges[3].Add( (r, side - 1) );
			}
			return edges;
		}

		var matched = 0;
		foreach ( var mine in Edges( Side ) )
		{
			var done = false;
			foreach ( var theirs in Edges( other.Side ) )
			{
				if ( done )
					break;
				foreach ( var reversed in new[] { false, true } )
				{
					var ok = true;
					for ( var k = 0; k < mine.Count; k++ )
					{
						var t = reversed ? theirs[theirs.Count - 1 - k] : theirs[k];
						if ( (BasePosition( mine[k].Row, mine[k].Col ) - OtherBase( other, t.Row, t.Col )).Length > 1e-3f )
						{
							ok = false;
							break;
						}
					}
					if ( !ok )
						continue;
					for ( var k = 0; k < mine.Count; k++ )
					{
						var t = reversed ? theirs[theirs.Count - 1 - k] : theirs[k];
						var mi = mine[k].Row * Side + mine[k].Col;
						var ti = t.Row * other.Side + t.Col;
						Offsets[mi] = other.Offsets[ti] = (Offsets[mi] + other.Offsets[ti]) * 0.5f;
						Alpha[mi] = other.Alpha[ti] = (Alpha[mi] + other.Alpha[ti]) * 0.5f;
					}
					matched += mine.Count;
					done = true;
					break;
				}
			}
		}
		return matched;
	}
}
