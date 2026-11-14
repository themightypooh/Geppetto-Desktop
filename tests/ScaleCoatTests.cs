using System;
using System.Linq;
using Effigy;

internal static class ScaleCoatTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "scale-coat", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		PartStudio UvBoxStudio()
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() );
			studio.Add( new UVProjectFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "UV box studio did not rebuild clean: " + string.Join( "; ", report.Errors.Select( e => e.Message ) ) );
			Require( studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount > 0, "UV box studio has no body" );
			return studio;
		}

		Check( "every pattern builds a domed tile peaked at the tip", () =>
		{
			foreach ( var pattern in new[]
			{
				ScalePattern.CtenoidFish, ScalePattern.DiamondReptile, ScalePattern.HexSnake,
				ScalePattern.DragonPlate, ScalePattern.PangolinArmor, ScalePattern.ArmadilloBand,
			} )
			{
				var tile = ScaleTileBuilder.BuildTile( pattern, 2f, 1f, 0.1f );
				Require( tile.Positions.Count > 0 && tile.Faces.Count > 0, $"{pattern}: tile has no geometry" );
				var maxZ = tile.Positions.Max( p => p.z );
				var maxY = tile.Positions.Max( p => p.y );
				var minY = tile.Positions.Min( p => p.y );
				Require( maxZ > 0.05f, $"{pattern}: dome never rises (maxZ {maxZ})" );
				Require( maxY > 1.8f && minY > -0.1f, $"{pattern}: tile does not span root..tip (y {minY}..{maxY})" );
				// The dome is proudest at the free tip, not the tucked root.
				var peakY = tile.Positions.Where( p => p.z == maxZ ).Max( p => p.y );
				Require( peakY > 0.75f * maxY, $"{pattern}: dome peak at y={peakY}, not near the tip (maxY {maxY})" );
			}
		} );

		Check( "lattice overlap is won by the head-ward row", () =>
		{
			var o = new ScaleLatticeOptions { TileU = 0.08f, TileV = 0.12f, Overlap = 0.5f, Dome = 0.06f };
			// Row pitch is 0.06, so row 0 spans v 0..0.12 and row 1 spans 0.06..0.18.
			// At v = 0.09 both cover the point; the lowest row must win, so localV
			// is up near the tip of row 0's tile, not the root of row 1's.
			Require( ScaleLattice.TryEvaluate( 0.5f, 0.09f, o, out _, out var overlapId, out _, out var overlapLocalV ),
				"lattice missed a covered point" );
			Require( overlapLocalV > 0.5f, $"overlap won by the wrong row: localV {overlapLocalV}" );
			Require( ScaleLattice.TryEvaluate( 0.5f, 0.03f, o, out var h0, out var row0Id, out _, out _ ),
				"lattice missed row 0 alone" );
			Require( h0 > 0f, "lattice height is zero inside a tile" );
			Require( overlapId == row0Id, "overlap point is not on row 0's tile" );
			Require( ScaleLattice.TryEvaluate( 0.5f, 0.15f, o, out _, out var row1Id, out _, out _ ),
				"lattice missed row 1" );
			Require( row1Id != row0Id, "row 1 point reported as row 0's tile" );
			Require( !ScaleLattice.TryEvaluate( 1.5f, 0.5f, o, out _, out _, out _, out _ ),
				"lattice claims a point outside UV space" );
		} );

		Check( "lattice normal, cavity and iridescence stay in range", () =>
		{
			var o = new ScaleLatticeOptions { TileU = 0.08f, TileV = 0.12f, Overlap = 0.5f, Dome = 0.06f };
			var n = ScaleLattice.NormalAt( 0.5f, 0.5f, o, 1f );
			Require( Math.Abs( n.Length - 1f ) < 0.1f, $"lattice normal not unit: {n.Length}" );
			Require( n.z > 0.3f, $"lattice normal does not point out of the surface: {n}" );
			var cavity = ScaleLattice.CavityAt( 0.5f, 0.5f, o );
			Require( cavity >= 0f && cavity <= 1.01f, $"cavity out of range: {cavity}" );
			var irid = ScaleLattice.IridescenceAt( 0.5f, 0.5f, o, 0.5f );
			Require( irid >= 0f && irid <= 1.01f, $"iridescence out of range: {irid}" );
		} );

		Check( "add ScaleCoat mesh mode tiles a UV box", () =>
		{
			var studio = UvBoxStudio();
			var coat = studio.Add( new ScaleCoatFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "scale mesh rebuild failed: " + string.Join( "; ", report.Errors.Select( e => e.Message ) ) );
			Require( studio.Bodies.Count == 2, $"expected box + coat bodies, got {studio.Bodies.Count}" );
			Require( coat.LastTileCount > 0, "mesh mode placed no tiles" );
			Require( coat.LastCoveredFraction > 0.5f, $"mesh mode covered too little: {coat.LastCoveredFraction}" );
			Require( studio.Bodies.Max( b => b.Mesh.FaceCount ) > 100, "coat body has too few faces" );
		} );

		Check( "add ScaleCoat bake mode fills the heightfield and the paint canvas", () =>
		{
			var studio = UvBoxStudio();
			var coat = studio.Add( new ScaleCoatFeature() );
			coat.Mode.Index = 1; // Bake
			coat.Size.Value = 128; // small bake: the lattice is ~8 evaluations per texel
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "scale bake rebuild failed: " + string.Join( "; ", report.Errors.Select( e => e.Message ) ) );
			Require( coat.LastBake is not null && coat.LastBake.Height is not null, "bake produced no height map" );
			Require( coat.LastBake.Height.FilledCount > 0, "bake height map is empty" );
			Require( Math.Abs( coat.LastBake.HeightMax - 0.06f ) < 0.02f,
				$"bake height max {coat.LastBake.HeightMax} is not the dome (0.06)" );
			Require( coat.LastIridescence is not null, "bake produced no iridescence map" );
			var box = studio.Bodies[0].Mesh;
			Require( box.Paint is not null && box.Paint.HeightMap is not null,
				"bake did not write the heightfield into the paint canvas" );
		} );

		Check( "scale coat on a UV-less body fails honestly", () =>
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() );
			var coat = studio.Add( new ScaleCoatFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Any( e => e.FeatureId == coat.Id ), "UV-less scale coat did not report an error" );
		} );

		Check( "add ScaleCoat resolves through the script verb", () =>
		{
			Require( BuildScript.CreateFeature( "ScaleCoat" ) is ScaleCoatFeature, "CreateFeature(ScaleCoat) did not resolve" );
			var studio = UvBoxStudio();
			var result = BuildScript.Run( studio, "add ScaleCoat\n" );
			Require( result.Ok, $"add ScaleCoat script failed: {result.Error}" );
			Require( studio.Bodies.Count == 2, "add ScaleCoat script added no coat body" );
		} );
	}
}
