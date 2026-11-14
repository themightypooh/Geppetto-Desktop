using System;
using System.Collections.Generic;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// DynTopo brush-system integration checks (godlike-dyntopo-brush-integration).
/// NOT registered in TestRegistry.cs — coordinator-owned; the final integrator adds
/// DynTopoBrushTests.Register(registry, settings);. Group "dyntopo-brush".
///
/// Fixtures are built in-test (flat grids, a box cage): no fixture files, and every
/// assertion is an inequality that follows from the core's own documented behaviour
/// (split edges longer than the detail size, collapse edges shorter than 0.4x it),
/// not an exact count the core does not promise.
/// </summary>
internal static class DynTopoBrushTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "dyntopo-brush", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof( T ).Name}, got silent success" );
		}

		// Flat grid over [0,1] x [0,1], z = 0. Edge length = 1 / cells.
		static PolyMesh Grid( int cells )
		{
			var m = new PolyMesh();
			for ( int j = 0; j <= cells; j++ )
				for ( int i = 0; i <= cells; i++ )
					m.Positions.Add( new Vec3( (float)i / cells, (float)j / cells, 0f ) );
			int At( int i, int j ) => j * ( cells + 1 ) + i;
			for ( int j = 0; j < cells; j++ )
				for ( int i = 0; i < cells; i++ )
				{
					m.Faces.Add( new Face( new[] { At( i, j ), At( i + 1, j ), At( i + 1, j + 1 ) } ) );
					m.Faces.Add( new Face( new[] { At( i, j ), At( i + 1, j + 1 ), At( i, j + 1 ) } ) );
				}
			return m;
		}

		// Unit box cage, 6 quads.
		static PolyMesh BoxCage()
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( -0.5f, -0.5f, -0.5f ) );
			m.Positions.Add( new Vec3( 0.5f, -0.5f, -0.5f ) );
			m.Positions.Add( new Vec3( 0.5f, 0.5f, -0.5f ) );
			m.Positions.Add( new Vec3( -0.5f, 0.5f, -0.5f ) );
			m.Positions.Add( new Vec3( -0.5f, -0.5f, 0.5f ) );
			m.Positions.Add( new Vec3( 0.5f, -0.5f, 0.5f ) );
			m.Positions.Add( new Vec3( 0.5f, 0.5f, 0.5f ) );
			m.Positions.Add( new Vec3( -0.5f, 0.5f, 0.5f ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2, 3 } ) );
			m.Faces.Add( new Face( new[] { 4, 7, 6, 5 } ) );
			m.Faces.Add( new Face( new[] { 0, 4, 5, 1 } ) );
			m.Faces.Add( new Face( new[] { 3, 2, 6, 7 } ) );
			m.Faces.Add( new Face( new[] { 0, 3, 7, 4 } ) );
			m.Faces.Add( new Face( new[] { 1, 5, 6, 2 } ) );
			return m;
		}

		static BrushSample Dab( float x, float y, float radius, float strength ) =>
			new( new Vec3( x, y, 0f ), new Vec3( 0, 0, 1 ), radius, strength );

		Check( "detail size changes tessellation", () =>
		{
			var grid = Grid( 4 ); // edge 0.25, 32 faces
			var dab = new[] { Dab( 0.5f, 0.5f, 1f, 0.01f ) };
			var fine = DynTopoBrushStroke.Apply( grid, dab,
				new DynTopoBrushSettings { DetailSize = 0.05f, DynTopoEnabled = true }, DynTopoBrushMode.Draw );
			var coarse = DynTopoBrushStroke.Apply( grid, dab,
				new DynTopoBrushSettings { DetailSize = 0.4f, DynTopoEnabled = true }, DynTopoBrushMode.Draw );
			// 0.05 splits every 0.25 edge; 0.4 splits none (even the cell diagonals
			// are 0.354 < 0.4) and collapses none (0.25 > 0.4 x 0.4 = 0.16), so the
			// coarse mesh is unchanged. (0.3 would split the diagonals: 0.354 > 0.3.)
			Require( fine.FacesAfter > coarse.FacesAfter, $"fine {fine.FacesAfter} should exceed coarse {coarse.FacesAfter}" );
			Require( coarse.FacesAfter == 32, $"coarse should be unchanged at 32 faces, got {coarse.FacesAfter}" );
			Require( fine.TessellatedDabs == 1 && fine.DabsApplied == 1 );
		} );

		Check( "dyntopo off leaves topology alone", () =>
		{
			var grid = Grid( 4 );
			var r = DynTopoBrushStroke.Apply( grid, new[] { Dab( 0.5f, 0.5f, 1f, 0.5f ) },
				new DynTopoBrushSettings { DetailSize = 0.05f, DynTopoEnabled = false }, DynTopoBrushMode.Draw );
			Require( r.FacesAfter == 32, $"faces should stay 32 with DynTopo off, got {r.FacesAfter}" );
			Require( !r.TopologyChanged && r.TessellatedDabs == 0 );
		} );

		Check( "smooth decimates", () =>
		{
			var grid = Grid( 20 ); // edge 0.05, 800 faces
			var dab = new[] { Dab( 0.5f, 0.5f, 1f, 0.5f ) };
			var decimated = DynTopoBrushStroke.Apply( grid, dab,
				new DynTopoBrushSettings { DetailSize = 0.3f, DynTopoEnabled = true, SmoothDecimates = true }, DynTopoBrushMode.Smooth );
			// Detail 0.3 collapses edges under 0.12 — every interior 0.05 edge — and
			// splits none (0.05 < 0.3), so the face count must fall.
			Require( decimated.FacesAfter < decimated.FacesBefore,
				$"smooth should decimate: {decimated.FacesBefore} -> {decimated.FacesAfter}" );
			var kept = DynTopoBrushStroke.Apply( grid, dab,
				new DynTopoBrushSettings { DetailSize = 0.3f, DynTopoEnabled = true, SmoothDecimates = false }, DynTopoBrushMode.Smooth );
			Require( kept.FacesAfter == kept.FacesBefore, "SmoothDecimates=false must not change topology" );
		} );

		Check( "level switch is fast to level 5", () =>
		{
			var sculpt = new MultiresSculpt( BoxCage() );
			for ( int i = 0; i < 5; i++ ) sculpt.AddLevel();
			Require( sculpt.TopLevel == 5 );
			var toTop = DynTopoLevels.SwitchView( sculpt, 5 );
			Require( toTop.Level == 5, $"view level should be 5, got {toTop.Level}" );
			Require( toTop.Faces > 0 && toTop.Vertices > 0 );
			Require( toTop.Elapsed < TimeSpan.FromSeconds( 1 ), $"switch to level 5 took {toTop.Elapsed}" );
			Require( DynTopoLevels.ResolveSculpt( sculpt, -1 ) == 5, "-1 must resolve to the top level" );
			var back = DynTopoLevels.SwitchView( sculpt, 0 );
			Require( back.Level == 0 );
			Throws<ArgumentOutOfRangeException>( () => DynTopoLevels.SwitchView( sculpt, 6 ) );
		} );

		Check( "cap enforced", () =>
		{
			// Exact 40-face mesh: 5 columns x 4 rows of cells.
			var exact = new PolyMesh();
			for ( int j = 0; j <= 4; j++ )
				for ( int i = 0; i <= 5; i++ )
					exact.Positions.Add( new Vec3( (float)i / 5, (float)j / 4, 0f ) );
			int At( int i, int j ) => j * 6 + i;
			for ( int j = 0; j < 4; j++ )
				for ( int i = 0; i < 5; i++ )
				{
					exact.Faces.Add( new Face( new[] { At( i, j ), At( i + 1, j ), At( i + 1, j + 1 ) } ) );
					exact.Faces.Add( new Face( new[] { At( i, j ), At( i + 1, j + 1 ), At( i, j + 1 ) } ) );
				}
			Require( exact.FaceCount == 40 );
			var r = DynTopoBrushStroke.Apply( exact, new[] { Dab( 0.5f, 0.5f, 1f, 0.5f ) },
				new DynTopoBrushSettings { DetailSize = 0.05f, DynTopoEnabled = true, HardCapFaces = 40 }, DynTopoBrushMode.Draw );
			Require( r.FacesAfter <= 40, $"cap 40 violated: {r.FacesAfter} faces" );
			Require( r.Capped && r.SkippedAtCap == 1, $"expected 1 skipped dab at cap, got {r.SkippedAtCap}" );
		} );

		Check( "over-cap input is reduced", () =>
		{
			var grid = Grid( 20 ); // 800 faces
			var r = DynTopoBrushStroke.Apply( grid, new[] { Dab( 0.5f, 0.5f, 1f, 0.1f ) },
				new DynTopoBrushSettings { DynTopoEnabled = false, HardCapFaces = 100 }, DynTopoBrushMode.Draw );
			Require( r.FacesAfter < r.FacesBefore, $"over-cap input should shrink: {r.FacesBefore} -> {r.FacesAfter}" );
			Require( r.DecimatedToCap && r.Capped );
		} );

		Check( "voxel remesh key path works", () =>
		{
			var grid = Grid( 4 );
			float seenVoxel = -1;
			int seenSmooth = -1;
			RemeshFeature.VoxelBackend = ( parts, voxel, smooth ) =>
			{
				seenVoxel = voxel;
				seenSmooth = smooth;
				return parts[ 0 ];
			};
			try
			{
				var ok = DynTopoRemeshKey.Invoke( new[] { grid },
					new DynTopoBrushSettings { VoxelSize = 0.25f, VoxelSmoothPasses = 3 } );
				Require( ok.Success && ok.Mesh == grid, "fake backend should return the part" );
				Require( Math.Abs( seenVoxel - 0.25f ) < 1e-6f, $"voxel size should reach the backend, saw {seenVoxel}" );
				Require( seenSmooth == 3 );
			}
			finally { RemeshFeature.VoxelBackend = null; }

			var refused = DynTopoRemeshKey.Invoke( new[] { grid }, new DynTopoBrushSettings() );
			Require( !refused.Success && refused.Reason.Contains( "voxel" ),
				$"no backend must refuse honestly, got Success={refused.Success} Reason='{refused.Reason}'" );
		} );

		Check( "stroke engine samples drive tessellation and bvh hook", () =>
		{
			var grid = Grid( 4 );
			var points = new[] { new Vec3( 0, 0, 0 ), new Vec3( 0.5f, 0, 0 ), new Vec3( 1, 0, 0 ) };
			var samples = StrokeEngineApi.BuildSamples( StrokeEngineMode.Space, points,
				new[] { 1f, 1f, 1f }, 0.25f, 0.3f, 0.1f,
				new StrokePressureCurve(), new DashPattern(), new StrokeJitter(), 1 );
			Require( samples.Count >= 2, $"expected >= 2 dabs from the stroke engine, got {samples.Count}" );
			int callbacks = 0;
			var r = DynTopoBrushStroke.Apply( grid, samples,
				new DynTopoBrushSettings { DetailSize = 0.05f, DynTopoEnabled = true }, DynTopoBrushMode.Draw,
				onTopologyChanged: _ => callbacks++ );
			Require( r.DabsApplied == samples.Count, $"all {samples.Count} dabs should apply, got {r.DabsApplied}" );
			Require( r.TopologyChanged && callbacks == 1, "topology change must fire the BVH callback once" );

			var bvh = DynTopoBvh.Rebuild( null, r.Mesh );
			Require( bvh is not null );
			var refit = DynTopoBvh.Rebuild( bvh, r.Mesh );
			Require( ReferenceEquals( refit, bvh ) && refit.Refits == 1, "same topology must refit, not rebuild" );
			var rebuilt = DynTopoBvh.Rebuild( bvh, grid );
			Require( !ReferenceEquals( rebuilt, bvh ), "changed topology must build a new tree" );
		} );

		Check( "preset settings round-trip", () =>
		{
			var preset = new BrushPreset { Name = "Clay", DetailSize = 0.07f, DynTopoEnabled = true };
			var settings = DynTopoBrushSettings.ForPreset( preset );
			Require( Math.Abs( settings.DetailSize - 0.07f ) < 1e-6f && settings.DynTopoEnabled );
			var clone = preset.Clone();
			Require( Math.Abs( clone.DetailSize - 0.07f ) < 1e-6f && clone.DynTopoEnabled, "Clone must carry the DynTopo fields" );
			Throws<ArgumentNullException>( () => DynTopoBrushSettings.ForPreset( null ) );
			Throws<ArgumentException>( () => DynTopoBrushStroke.Apply( Grid( 2 ),
				new[] { Dab( 0.5f, 0.5f, 1f, 0.1f ) },
				new DynTopoBrushSettings { DetailSize = -1f, DynTopoEnabled = true }, DynTopoBrushMode.Draw ) );
			Throws<ArgumentNullException>( () => DynTopoBrushStroke.Apply( null,
				new[] { Dab( 0.5f, 0.5f, 1f, 0.1f ) },
				new DynTopoBrushSettings(), DynTopoBrushMode.Draw ) );
		} );
	}
}
