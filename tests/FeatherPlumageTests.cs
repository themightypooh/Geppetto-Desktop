using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

internal static class FeatherPlumageTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "feather-plumage", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		PartStudio BoxStudio()
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "box studio did not rebuild clean: " + string.Join( "; ", report.Errors.Select( e => e.Message ) ) );
			Require( studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount > 0, "box studio has no body" );
			return studio;
		}

		Check( "hero feather is asymmetric with a raised rachis", () =>
		{
			var mesh = FeatherCardBuilder.BuildFeather( new FeatherOptions
			{
				Length = 3f, Width = 1f, LeadingRatio = 0.35f, Segments = 8, Across = 4, Kind = FeatherKind.Flight,
			} );
			Require( mesh.Positions.Count > 0 && mesh.Faces.Count > 0, "feather has no geometry" );
			Require( mesh.HasVertexColors, "feather has no vertex colours" );
			var minX = mesh.Positions.Min( p => p.x );
			var maxX = mesh.Positions.Max( p => p.x );
			var maxZ = mesh.Positions.Max( p => p.z );
			// Leading (narrow, -X) edge is 0.35x the trailing edge.
			Require( maxX > 0.2f, $"trailing half-width too small: {maxX}" );
			Require( -minX < 0.6f * maxX, $"leading edge not narrower: leading {-minX}, trailing {maxX}" );
			// Rachis ridge stands proud of the vane.
			Require( maxZ > 0.02f, $"no rachis ridge: maxZ {maxZ}" );
			// Vertex colours: G = 0 on the ridge (x ~ 0), G = 1 out on the vane.
			var ridge = false;
			var vane = false;
			for ( var i = 0; i < mesh.Positions.Count; i++ )
			{
				var p = mesh.Positions[i];
				var c = mesh.VertexColors[i];
				if ( Math.Abs( p.x ) < 0.02f && c.y < 0.1f ) ridge = true;
				if ( Math.Abs( p.x ) > 0.3f * maxX && c.y > 0.9f ) vane = true;
			}
			Require( ridge, "no ridge vertex with G ~ 0" );
			Require( vane, "no vane vertex with G ~ 1" );
		} );

		Check( "down feather has no rachis ridge", () =>
		{
			var flight = FeatherCardBuilder.BuildFeather( new FeatherOptions { Kind = FeatherKind.Flight } );
			var down = FeatherCardBuilder.BuildFeather( new FeatherOptions { Kind = FeatherKind.Down } );
			Require( down.Positions.Max( p => p.z ) < flight.Positions.Max( p => p.z ),
				"down feather ridge as tall as a flight feather" );
		} );

		Check( "baker writes five maps, deterministically", () =>
		{
			var dirA = Path.Combine( Path.GetTempPath(), "feather-test-a-" + Guid.NewGuid().ToString( "N" ) );
			var dirB = Path.Combine( Path.GetTempPath(), "feather-test-b-" + Guid.NewGuid().ToString( "N" ) );
			try
			{
				var opts = new FeatherBakeOptions { Size = 64, Seed = 7 };
				var filesA = FeatherCardBuilder.BakeTextures( dirA, "feather", opts );
				var filesB = FeatherCardBuilder.BakeTextures( dirB, "feather", opts );
				Require( filesA.Count == 5, $"expected 5 maps, got {filesA.Count}" );
				foreach ( var suffix in new[] { "_color", "_alpha", "_normal", "_rough", "_irid" } )
					Require( filesA.Any( f => Path.GetFileName( f ).Contains( suffix ) ), $"no {suffix} map baked" );
				foreach ( var f in filesA )
					Require( File.Exists( f ) && new FileInfo( f ).Length > 0, $"baked map missing or empty: {f}" );
				foreach ( var f in filesA )
				{
					var twin = filesB.Single( g => Path.GetFileName( g ) == Path.GetFileName( f ) );
					Require( File.ReadAllBytes( f ).SequenceEqual( File.ReadAllBytes( twin ) ),
						$"bake not deterministic: {Path.GetFileName( f )}" );
				}
			}
			finally
			{
				try { if ( Directory.Exists( dirA ) ) Directory.Delete( dirA, true ); } catch { }
				try { if ( Directory.Exists( dirB ) ) Directory.Delete( dirB, true ); } catch { }
			}
		} );

		Check( "scatter shingles rows with curvature tilt", () =>
		{
			var studio = BoxStudio();
			var mesh = studio.Bodies[0].Mesh;
			var opts = new PlumageScatterOptions { Rows = 4, PerRow = 8, Length = 3f };
			var placements = PlumageScatter.Scatter( mesh, opts );
			Require( placements.Count > 0 && placements.Count <= 32, $"unexpected placement count {placements.Count}" );
			Require( placements.Select( p => (p.Row, p.Column) ).Distinct().Count() == placements.Count,
				"duplicate row/column placements" );
			foreach ( var p in placements )
				Require( p.TiltDeg >= 8f && p.TiltDeg <= 20f, $"tilt out of 8..20 deg: {p.TiltDeg}" );
			// Kind gradient: covert rows are shorter than primary rows.
			var lengths = placements.Select( p => p.Length ).ToList();
			Require( lengths.Min() < lengths.Max(), "no length gradient across rows" );
			Require( lengths.All( l => l >= 2.0f && l <= 4.6f ), "placement length outside covert..primary range" );
			var coat = PlumageScatter.BuildCoat( mesh, placements, opts );
			Require( coat.Positions.Count > 0 && coat.Faces.Count > 0, "coat mesh is empty" );
		} );

		Check( "add Plumage grows a wing coat on a box", () =>
		{
			var studio = BoxStudio();
			var plumage = studio.Add( new PlumageFeature() );
			plumage.Rows.Value = 2;
			plumage.PerRow.Value = 4;
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "plumage rebuild failed: " + string.Join( "; ", report.Errors.Select( e => e.Message ) ) );
			Require( studio.Bodies.Count == 2, $"expected box + coat bodies, got {studio.Bodies.Count}" );
			Require( plumage.LastFeatherCount > 0 && plumage.LastFeatherCount <= 8,
				$"unexpected feather count {plumage.LastFeatherCount}" );
			foreach ( var p in plumage.LastPlacements )
				Require( p.TiltDeg >= 8f && p.TiltDeg <= 20f, $"tilt out of 8..20 deg: {p.TiltDeg}" );
		} );

		Check( "add Plumage kind=Single builds one hero feather", () =>
		{
			var studio = BoxStudio();
			var plumage = studio.Add( new PlumageFeature() );
			plumage.Kind.Index = 2; // Single
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "single-feather rebuild failed" );
			Require( plumage.LastFeatherCount == 1, $"expected 1 feather, got {plumage.LastFeatherCount}" );
			Require( studio.Bodies.Count == 2, "single feather added no body" );
		} );

		Check( "plumage on an empty studio fails honestly", () =>
		{
			var studio = new PartStudio();
			var plumage = studio.Add( new PlumageFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Any( e => e.FeatureId == plumage.Id ), "empty-studio plumage did not report an error" );
		} );

		Check( "add Plumage resolves through the script verb", () =>
		{
			Require( BuildScript.CreateFeature( "Plumage" ) is PlumageFeature, "CreateFeature(Plumage) did not resolve" );
			Require( BuildScript.CreateFeature( "Feather" ) is FeatherFeature, "CreateFeature(Feather) did not resolve" );
			var studio = BoxStudio();
			var result = BuildScript.Run( studio, "add Plumage rows=2 perrow=4\n" );
			Require( result.Ok, $"add Plumage script failed: {result.Error}" );
			Require( studio.Bodies.Count == 2, "add Plumage script added no coat body" );
		} );
	}
}
