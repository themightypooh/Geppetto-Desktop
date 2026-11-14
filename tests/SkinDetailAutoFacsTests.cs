using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;

internal static class SkinDetailAutoFacsTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "skin-detail-autofacs", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		PartStudio BoxStudio()
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "box studio did not rebuild clean" );
			return studio;
		}

		PartStudio UvBoxStudio()
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() );
			studio.Add( new UVProjectFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "UV box studio did not rebuild clean" );
			return studio;
		}

		Check( "add SkinDetail paints vertex colours", () =>
		{
			var studio = BoxStudio();
			var skin = studio.Add( new SkinDetailFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "skin detail rebuild failed: " + string.Join( "; ", report.Errors.Select( e => e.Message ) ) );
			var mesh = studio.Bodies[0].Mesh;
			Require( mesh.HasVertexColors, "skin detail wrote no vertex colours" );
			Require( mesh.VertexColors.All( c => c.w == 1f ), "vertex colours are not opaque" );
			Require( mesh.VertexColors.Any( c => c.x > 0f ), "vertex colours are all black" );
			Require( skin.LastFreckleSpots == 0, "freckles=0 placed spots" );
		} );

		Check( "add SkinDetail freckles place spots", () =>
		{
			var studio = BoxStudio();
			var skin = studio.Add( new SkinDetailFeature() );
			skin.Freckles.Value = 1f;
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "freckled rebuild failed" );
			Require( skin.LastFreckleSpots > 0, "freckles=1 placed no spots" );
		} );

		Check( "add SkinDetail scar displaces vertices and a bad scar fails", () =>
		{
			var studio = BoxStudio();
			var mesh0 = studio.Bodies[0].Mesh;
			var before = mesh0.Positions.Select( p => (p.x, p.y, p.z) ).ToList();
			// Scar along the edge between two real vertices, so it must touch the mesh.
			string Pt( Vec3 v ) => string.Format( CultureInfo.InvariantCulture, "{0},{1},{2}", v.x, v.y, v.z );
			var skin = studio.Add( new SkinDetailFeature() );
			skin.Scar.Value = Pt( mesh0.Positions[0] ) + ".." + Pt( mesh0.Positions[1] );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "scar rebuild failed: " + string.Join( "; ", report.Errors.Select( e => e.Message ) ) );
			Require( skin.LastDisplacedVertices > 0, "scar displaced no vertices" );
			var after = studio.Bodies[0].Mesh.Positions;
			Require( before.Zip( after, (a, b) => (a, b) ).Any( t => !t.a.Equals( t.b ) ), "scar moved no vertex" );

			var bad = BoxStudio();
			var badSkin = bad.Add( new SkinDetailFeature() );
			badSkin.Scar.Value = "banana";
			var badReport = bad.Rebuild();
			Require( badReport.Errors.Any( e => e.FeatureId == badSkin.Id ), "malformed scar did not fail the rebuild" );
		} );

		Check( "add SkinDetail folder bakes colour and roughness maps", () =>
		{
			var dir = Path.Combine( Path.GetTempPath(), "skin-detail-test-" + Guid.NewGuid().ToString( "N" ) );
			try
			{
				var studio = UvBoxStudio();
				var skin = studio.Add( new SkinDetailFeature() );
				skin.Folder.Value = dir;
				skin.Size.Value = 64;
				var report = studio.Rebuild();
				Require( report.Errors.Count == 0, "folder bake rebuild failed: " + string.Join( "; ", report.Errors.Select( e => e.Message ) ) );
				Require( skin.Written.Count == 2, $"expected 2 map files, got {skin.Written.Count}" );
				foreach ( var f in skin.Written )
					Require( File.Exists( f ) && new FileInfo( f ).Length > 0, $"map missing or empty: {f}" );
			}
			finally
			{
				try { if ( Directory.Exists( dir ) ) Directory.Delete( dir, true ); } catch { }
			}
		} );

		Check( "add AutoFacs installs morphs", () =>
		{
			var studio = BoxStudio();
			var facs = studio.Add( new AutoFacsFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "auto facs rebuild failed: " + string.Join( "; ", report.Errors.Select( e => e.Message ) ) );
			Require( facs.LastMorphCount > 50, $"too few morphs installed: {facs.LastMorphCount}" );
			var morphs = studio.Bodies[0].Mesh.Morphs;
			Require( morphs.Any( m => m.Name == "lip_zip" ), "lip_zip morph missing" );
			Require( morphs.Count( m => m.Name.StartsWith( "viseme_" ) ) == 15, "viseme morphs are not 15" );
		} );

		Check( "add SkinDetail and add AutoFacs resolve through the script verb", () =>
		{
			Require( BuildScript.CreateFeature( "SkinDetail" ) is SkinDetailFeature, "CreateFeature(SkinDetail) did not resolve" );
			Require( BuildScript.CreateFeature( "AutoFacs" ) is AutoFacsFeature, "CreateFeature(AutoFacs) did not resolve" );
			var studio = BoxStudio();
			var result = BuildScript.Run( studio, "add SkinDetail freckles=1\nadd AutoFacs\n" );
			Require( result.Ok, $"script failed: {result.Error}" );
			var skin = studio.Features.OfType<SkinDetailFeature>().Single();
			var facs = studio.Features.OfType<AutoFacsFeature>().Single();
			Require( skin.LastFreckleSpots > 0, "script freckles placed no spots" );
			Require( facs.LastMorphCount > 50, "script auto facs installed too few morphs" );
		} );

		Check( "skin detail and auto facs on an empty studio fail honestly", () =>
		{
			var studio = new PartStudio();
			var skin = studio.Add( new SkinDetailFeature() );
			var facs = studio.Add( new AutoFacsFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Any( e => e.FeatureId == skin.Id ), "empty-studio skin detail did not report an error" );
			Require( report.Errors.Any( e => e.FeatureId == facs.Id ), "empty-studio auto facs did not report an error" );
		} );
	}
}
