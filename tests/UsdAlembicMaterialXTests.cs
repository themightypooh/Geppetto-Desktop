using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Export;
using Marionette.Desktop;

// Parallel implementation note: kernel/Effigy/UsdStage.cs, MaterialX.cs and
// AlembicCache.cs define Effigy.UsdStage / UsdPrim / UsdPreviewSurface /
// MaterialXDocument / AlembicCache with the same simple names. These aliases pin
// this file to the Effigy.Export slice so both implementations can coexist in the
// tree until the integrator picks one.
using UsdStage = Effigy.Export.UsdStage;
using UsdPrim = Effigy.Export.UsdPrim;
using UsdPreviewSurface = Effigy.Export.UsdPreviewSurface;
using MaterialXDocument = Effigy.Export.MaterialXDocument;
using AlembicCache = Effigy.Export.AlembicCache;

/// <summary>
/// Targeted tests for the USD / Alembic / MaterialX interchange slice
/// (godlike-usd-stage-alembic-materialx, GODLIKE WAVE 1 P0).
///
/// Coverage: USD stage round-trip (hierarchy, mesh data, variants,
/// references/instancing, deferred payloads, sublayer strength, materials),
/// Alembic time mapping (FPS-correct, never hardcoded 24) and per-frame
/// baked geometry, MaterialX/OpenPBR mapping with named losses, and the
/// published loss lists (zero silent drops). The Blender round-trip leg is
/// gated by Missing(): it runs in CI where blender is on PATH and skips
/// locally otherwise.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// UsdAlembicMaterialXTests.Register(registry, settings); line.
/// </summary>
internal static class UsdAlembicMaterialXTests
{
	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}

	static void AssertNear( float actual, float expected, float tolerance, string message )
	{
		if ( MathF.Abs( actual - expected ) > tolerance ) throw new Exception( message + " (expected " + expected + ", got " + actual + ")" );
	}

	static PolyMesh Quad()
	{
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( 0f, 0f, 0f ) );
		mesh.AddVertex( new Vec3( 1f, 0f, 0f ) );
		mesh.AddVertex( new Vec3( 1f, 1f, 0f ) );
		mesh.AddVertex( new Vec3( 0f, 1f, 0f ) );
		mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0f, 0f ), new Vec2( 1f, 0f ), new Vec2( 1f, 1f ) } );
		mesh.AddFace( new[] { 0, 2, 3 }, new[] { new Vec2( 0f, 0f ), new Vec2( 1f, 1f ), new Vec2( 0f, 1f ) } );
		return mesh;
	}

	static Vec3[] TriangleAt( float x )
	{
		return new[] { new Vec3( x, 0f, 0f ), new Vec3( x + 1f, 0f, 0f ), new Vec3( x, 1f, 0f ) };
	}

	static void WalkPrims( UsdPrim prim, Action<UsdPrim> visit )
	{
		visit( prim );
		foreach ( var child in prim.Children ) WalkPrims( child, visit );
	}

	static string? BlenderOnPath()
	{
		string? path = Environment.GetEnvironmentVariable( "PATH" );
		if ( string.IsNullOrEmpty( path ) ) return null;
		foreach ( string dir in path.Split( ':' ) )
		{
			string candidate = Path.Combine( dir, "blender" );
			if ( File.Exists( candidate ) ) return candidate;
		}
		return null;
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		string work = settings.WorkDirectory;
		string Fresh( string ext ) => Path.Combine( work, Guid.NewGuid().ToString( "N" ) + ext );

		// ----- USD stage -----

		registry.Add( "usd", "stage round-trip preserves hierarchy and mesh data", () =>
		{
			var mesh = Quad();
			var stage = new UsdStage( "roundtrip" );
			stage.RootLayer.DefaultPrim = "World";
			var exported = stage.AddMesh( mesh, "/World/Char/Body" );
			Assert( exported.Losses.Count == 0, "clean quad export should have no losses, got: " + string.Join( "; ", exported.Losses ) );
			string file = Fresh( ".usda" );
			stage.WriteUsda( file );
			var read = UsdStage.ReadUsda( file );
			var body = read.Find( "/World/Char/Body" );
			Assert( body != null, "re-read stage is missing /World/Char/Body" );
			Assert( body!.Kind == UsdPrimKind.Mesh, "re-read prim kind is " + body.Kind + ", expected Mesh" );
			var points = body.GetAttribute( "points" )!.AsVec3Array();
			Assert( points.Length == 4, "points count " + points.Length + ", expected 4" );
			for ( int i = 0; i < 4; i++ )
			{
				var back = UsdInterchange.FromUsd( points[i] );
				Assert( back.AlmostEquals( mesh.Positions[i], 1e-4f ), "point " + i + " did not round-trip: " + back + " vs " + mesh.Positions[i] );
			}
			Assert( string.Join( ",", body.GetAttribute( "faceVertexCounts" )!.AsIntArray() ) == "3,3", "faceVertexCounts did not round-trip" );
			Assert( string.Join( ",", body.GetAttribute( "faceVertexIndices" )!.AsIntArray() ) == "0,1,2,0,2,3", "faceVertexIndices did not round-trip" );
			var uvs = body.GetAttribute( "primvars:st" )!.AsVec2Array();
			Assert( uvs.Length == 6, "face-varying UV count " + uvs.Length + ", expected 6" );
			AssertNear( uvs[1].x, 1f, 1e-6f, "second corner U" );
			AssertNear( uvs[1].y, 0f, 1e-6f, "second corner V" );
			Assert( read.RootLayer.DefaultPrim == "World", "defaultPrim did not round-trip" );
		} );

		registry.Add( "usd", "variants select different opinions", () =>
		{
			var stage = new UsdStage( "variants" );
			var prim = stage.CreatePrim( "/World/Body", UsdPrimKind.Mesh );
			prim.VariantSets["detail"] = new List<string> { "high", "low" };
			var high = new UsdPrim( "/World/Body", UsdPrimKind.Mesh );
			high.SetAttribute( "inputs:diffuseColor", UsdValue.Vec3( "color3f", new Vec3( 1f, 0f, 0f ) ) );
			var low = new UsdPrim( "/World/Body", UsdPrimKind.Mesh );
			low.SetAttribute( "inputs:diffuseColor", UsdValue.Vec3( "color3f", new Vec3( 0f, 0f, 1f ) ) );
			prim.VariantChildren["detail\nhigh"] = new List<UsdPrim> { high };
			prim.VariantChildren["detail\nlow"] = new List<UsdPrim> { low };
			prim.SelectedVariants["detail"] = "low";
			string file = Fresh( ".usda" );
			stage.WriteUsda( file );
			var read = UsdStage.ReadUsda( file );
			var reread = read.Find( "/World/Body" );
			Assert( reread != null && reread.SelectedVariants.TryGetValue( "detail", out string? selected ) && selected == "low", "variant selection did not survive the .usda round-trip" );
			Assert( reread!.VariantSets.TryGetValue( "detail", out var variants ) && variants.Contains( "high" ) && variants.Contains( "low" ), "variant set did not survive the .usda round-trip" );
			var composedLow = read.Compose( "/World/Body" );
			Assert( composedLow.Prim.GetAttribute( "inputs:diffuseColor" )!.AsVec3().AlmostEquals( new Vec3( 0f, 0f, 1f ), 1e-6f ), "low variant did not compose blue" );
			reread.SelectedVariants["detail"] = "high";
			var composedHigh = read.Compose( "/World/Body" );
			Assert( composedHigh.Prim.GetAttribute( "inputs:diffuseColor" )!.AsVec3().AlmostEquals( new Vec3( 1f, 0f, 0f ), 1e-6f ), "high variant did not compose red" );
		} );

		registry.Add( "usd", "references instance one source mesh twice", () =>
		{
			var stage = new UsdStage( "instances" );
			var source = stage.CreatePrim( "/Lib/Bolt", UsdPrimKind.Mesh );
			source.SetAttribute( "points", UsdValue.Vec3Array( "point3f[]", new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 0f, 1f, 0f ) } ) );
			var a = stage.CreatePrim( "/World/BoltA", UsdPrimKind.Xform );
			a.References.Add( "</Lib/Bolt>" );
			var b = stage.CreatePrim( "/World/BoltB", UsdPrimKind.Xform );
			b.References.Add( "</Lib/Bolt>" );
			var composedA = stage.Compose( "/World/BoltA" );
			var composedB = stage.Compose( "/World/BoltB" );
			Assert( composedA.Prim.GetAttribute( "points" ) != null, "instance A did not resolve its reference" );
			Assert( composedB.Prim.GetAttribute( "points" ) != null, "instance B did not resolve its reference" );
			source.SetAttribute( "points", UsdValue.Vec3Array( "point3f[]", new[] { new Vec3( 5f, 0f, 0f ), new Vec3( 6f, 0f, 0f ), new Vec3( 5f, 1f, 0f ) } ) );
			var editedA = stage.Compose( "/World/BoltA" );
			var editedB = stage.Compose( "/World/BoltB" );
			AssertNear( editedA.Prim.GetAttribute( "points" )!.AsVec3Array()[0].x, 5f, 1e-6f, "editing the source did not update instance A" );
			AssertNear( editedB.Prim.GetAttribute( "points" )!.AsVec3Array()[0].x, 5f, 1e-6f, "editing the source did not update instance B" );
		} );

		registry.Add( "usd", "payload deferred until requested", () =>
		{
			var heavy = new UsdLayer( "heavy" ) { FilePath = "heavy.usda" };
			var heavyMesh = new UsdPrim( "/Lib/Heavy", UsdPrimKind.Mesh );
			heavyMesh.SetAttribute( "points", UsdValue.Vec3Array( "point3f[]", new[] { new Vec3( 0f, 0f, 0f ) } ) );
			heavy.Roots.Add( heavyMesh );
			var stage = new UsdStage( "payloads" );
			stage.RegisterLayer( heavy );
			var prim = stage.CreatePrim( "/World/Heavy", UsdPrimKind.Xform );
			prim.Payload = "@heavy.usda@</Lib/Heavy>";
			var deferred = stage.Compose( "/World/Heavy", loadPayloads: false );
			Assert( deferred.Prim.GetAttribute( "points" ) == null, "deferred payload leaked geometry into the composed prim" );
			Assert( deferred.Notes.Any( n => n.Contains( "deferred", StringComparison.Ordinal ) ), "deferred payload was not noted: " + string.Join( "; ", deferred.Notes ) );
			var loaded = stage.Compose( "/World/Heavy", loadPayloads: true );
			Assert( loaded.Prim.GetAttribute( "points" ) != null, "requested payload did not resolve" );
		} );

		registry.Add( "usd", "sublayers: first listed wins", () =>
		{
			var stage = new UsdStage( "layers" );
			var rootPrim = stage.CreatePrim( "/World/Body", UsdPrimKind.Mesh );
			rootPrim.SetAttribute( "inputs:diffuseColor", UsdValue.Vec3( "color3f", new Vec3( 1f, 0f, 0f ) ) );
			var sub = new UsdLayer( "weak" );
			var subPrim = new UsdPrim( "/World/Body", UsdPrimKind.Mesh );
			subPrim.SetAttribute( "inputs:diffuseColor", UsdValue.Vec3( "color3f", new Vec3( 0f, 0f, 1f ) ) );
			sub.Roots.Add( subPrim );
			stage.RootLayer.Sublayers.Add( "weak.usda" );
			stage.RegisterLayer( sub );
			var composed = stage.Compose( "/World/Body" );
			Assert( composed.Prim.GetAttribute( "inputs:diffuseColor" )!.AsVec3().AlmostEquals( new Vec3( 1f, 0f, 0f ), 1e-6f ), "root layer did not win over the sublayer" );
			Assert( composed.ContributingLayers.Contains( "layers" ) && composed.ContributingLayers.Contains( "weak" ), "contributing layers missing: " + string.Join( ",", composed.ContributingLayers ) );
		} );

		registry.Add( "usd", "materials survive round-trip", () =>
		{
			var surface = new UsdPreviewSurface
			{
				Name = "Skin",
				DiffuseColor = new Vec3( 0.2f, 0.4f, 0.6f ),
				Metallic = 0.8f,
				Roughness = 0.25f,
				Opacity = 0.9f,
				EmissiveColor = new Vec3( 0.1f, 0f, 0f ),
				Clearcoat = 0.5f,
				Ior = 1.45f,
			};
			var stage = new UsdStage( "materials" );
			stage.AddMaterial( surface, "/Materials/Skin" );
			string file = Fresh( ".usda" );
			stage.WriteUsda( file );
			var read = UsdStage.ReadUsda( file );
			var material = read.Find( "/Materials/Skin" );
			Assert( material != null && material.Kind == UsdPrimKind.Material, "material prim did not round-trip" );
			var shader = material!.FindChild( "Skin" );
			Assert( shader != null && shader.Kind == UsdPrimKind.Shader, "shader child did not round-trip" );
			var back = UsdPreviewSurface.FromPrim( shader!, out var losses );
			Assert( losses.Count == 0, "material round-trip reported losses: " + string.Join( "; ", losses ) );
			Assert( back.DiffuseColor.AlmostEquals( new Vec3( 0.2f, 0.4f, 0.6f ), 1e-4f ), "diffuseColor did not round-trip" );
			AssertNear( back.Metallic, 0.8f, 1e-4f, "metallic" );
			AssertNear( back.Roughness, 0.25f, 1e-4f, "roughness" );
			AssertNear( back.Opacity, 0.9f, 1e-4f, "opacity" );
			Assert( back.EmissiveColor.AlmostEquals( new Vec3( 0.1f, 0f, 0f ), 1e-4f ), "emissiveColor did not round-trip" );
			AssertNear( back.Clearcoat, 0.5f, 1e-4f, "clearcoat" );
			AssertNear( back.Ior, 1.45f, 1e-4f, "ior" );
		} );

		registry.Add( "usd", "mesh losses are named, never silent", () =>
		{
			var mesh = Quad();
			mesh.AddMorph( "Smile", mesh.Positions.ToArray() );
			var stage = new UsdStage( "losses" );
			var result = stage.AddMesh( mesh, "/World/Body" );
			Assert( result.Losses.Count > 0, "morph was dropped with no loss recorded" );
			Assert( result.Losses.Any( l => l.Contains( "morph", StringComparison.OrdinalIgnoreCase ) ), "morph loss is not named: " + string.Join( "; ", result.Losses ) );
			var empty = new PolyMesh();
			try { stage.AddMesh( empty, "/World/Empty" ); throw new Exception( "empty mesh export was silently accepted" ); }
			catch ( InvalidOperationException ) { /* expected refusal */ }
		} );

		registry.Add( "usd", "malformed usda is refused with a line number", () =>
		{
			try { UsdAscii.Read( "def Xform \"Broken\" {\n\tfloat3 points = [" ); throw new Exception( "malformed .usda was silently accepted" ); }
			catch ( UsdParseException ex ) { Assert( ex.Line > 0 && ex.Message.Contains( "line", StringComparison.Ordinal ), "parse error has no line: " + ex.Message ); }
			try { UsdAscii.Read( "def Mesh \"NoClose\" {\n\tfloat x = 1\n" ); throw new Exception( "unterminated prim block was silently accepted" ); }
			catch ( UsdParseException ) { /* expected refusal */ }
		} );

		registry.Add( "usd", "blender round-trip leg", () =>
		{
			var mesh = Quad();
			var stage = new UsdStage( "blender" );
			stage.RootLayer.DefaultPrim = "World";
			stage.AddMesh( mesh, "/World/Char/Body" );
			string input = Fresh( ".usda" );
			string output = Fresh( ".usda" );
			string script = Fresh( ".py" );
			stage.WriteUsda( input );
			File.WriteAllText( script,
				"import bpy, sys\n" +
				"inp, outp = sys.argv[sys.argv.index('--') + 1:sys.argv.index('--') + 3]\n" +
				"bpy.ops.wm.read_factory_settings(use_empty=True)\n" +
				"bpy.ops.wm.usd_import(filepath=inp)\n" +
				"print('USD_IMPORT_OBJECTS:', [o.name for o in bpy.data.objects])\n" +
				"bpy.ops.wm.usd_export(filepath=outp)\n" +
				"print('USD_EXPORT_OK')\n" );
			var start = new ProcessStartInfo
			{
				FileName = "blender",
				Arguments = "-b --factory-startup --python \"" + script + "\" -- \"" + input + "\" \"" + output + "\"",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			using var process = Process.Start( start )!;
			if ( !process.WaitForExit( 120000 ) ) { process.Kill( entireProcessTree: true ); throw new Exception( "blender round-trip timed out after 120 s" ); }
			string stdout = process.StandardOutput.ReadToEnd();
			string stderr = process.StandardError.ReadToEnd();
			if ( process.ExitCode != 0 ) throw new Exception( "blender round-trip failed (exit " + process.ExitCode + "): " + ( stdout + stderr ).Substring( 0, Math.Min( 2000, ( stdout + stderr ).Length ) ) );
			if ( !stdout.Contains( "USD_EXPORT_OK", StringComparison.Ordinal ) ) throw new Exception( "blender did not reach the export step: " + stdout.Substring( 0, Math.Min( 2000, stdout.Length ) ) );
			var reread = UsdStage.ReadUsda( output );
			bool foundMesh = false;
			foreach ( var root in reread.RootLayer.Roots ) WalkPrims( root, p => { if ( p.Kind == UsdPrimKind.Mesh && p.GetAttribute( "points" ) != null ) foundMesh = true; } );
			Assert( foundMesh, "blender re-export has no Mesh prim with points" );
		}, missing: () => BlenderOnPath() == null ? "blender not on PATH (CI round-trip)" : null );

		// ----- Alembic -----

		registry.Add( "alembic", "time map uses the cache fps, never 24", () =>
		{
			var map30 = new AlembicTimeMap( 30 );
			AssertNear( (float)map30.FrameToSeconds( 15 ), 0.5f, 1e-6f, "frame 15 at 30 fps" );
			Assert( map30.SecondsToFrame( 0.5 ) == 15, "0.5 s at 30 fps is frame " + map30.SecondsToFrame( 0.5 ) + ", expected 15" );
			var map24 = new AlembicTimeMap( 24 );
			Assert( map24.SecondsToFrame( 0.5 ) == 12, "0.5 s at 24 fps is frame " + map24.SecondsToFrame( 0.5 ) + ", expected 12" );
			var map25 = new AlembicTimeMap( 25 );
			Assert( map25.SecondsToFrame( 0.5 ) == 13, "0.5 s at 25 fps is frame " + map25.SecondsToFrame( 0.5 ) + ", expected 13 (12.5 rounds away from zero)" );
			foreach ( double bad in new[] { 0.0, -30.0, double.NaN, double.PositiveInfinity } )
			{
				try { _ = new AlembicTimeMap( bad ); throw new Exception( "fps " + bad + " was accepted" ); }
				catch ( ArgumentException ) { /* expected refusal */ }
			}
		} );

		registry.Add( "alembic", "per-frame baked geometry round-trips", () =>
		{
			var cache = new AlembicCache( "/Char/Body", 30 );
			cache.AddSample( 0, TriangleAt( 0f ) );
			cache.AddSample( 15, TriangleAt( 1f ) );
			cache.AddSample( 30, TriangleAt( 2f ) );
			string file = Fresh( ".abc" );
			cache.Write( file );
			var read = AlembicCache.Read( file );
			Assert( read.ObjectPath == "/Char/Body", "object path did not round-trip" );
			AssertNear( (float)read.Fps, 30f, 1e-6f, "fps" );
			Assert( read.Samples.Count == 3, "sample count " + read.Samples.Count + ", expected 3" );
			for ( int s = 0; s < 3; s++ )
			{
				Assert( read.Samples[s].Frame == s * 15, "sample " + s + " frame " + read.Samples[s].Frame + ", expected " + ( s * 15 ) );
				for ( int v = 0; v < 3; v++ )
					Assert( read.Samples[s].Positions[v].AlmostEquals( TriangleAt( s )[v], 1e-6f ), "sample " + s + " vertex " + v + " did not round-trip" );
			}
			Assert( read.SampleAtTime( 0.5 ).Frame == 15, "SampleAtTime(0.5) is frame " + read.SampleAtTime( 0.5 ).Frame + ", expected 15" );
		} );

		registry.Add( "alembic", "vertex count change is refused naming the frame", () =>
		{
			var cache = new AlembicCache( "/Char/Body", 30 );
			cache.AddSample( 0, TriangleAt( 0f ) );
			try { cache.AddSample( 5, new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ) } ); throw new Exception( "topology change was silently accepted" ); }
			catch ( InvalidOperationException ex ) { Assert( ex.Message.Contains( "frame 5", StringComparison.Ordinal ), "refusal does not name the frame: " + ex.Message ); }
		} );

		registry.Add( "alembic", "bad magic is refused", () =>
		{
			string file = Fresh( ".abc" );
			File.WriteAllText( file, "this is not an alembic cache" );
			try { _ = AlembicCache.Read( file ); throw new Exception( "non-cache file was silently accepted" ); }
			catch ( AlembicFormatException ) { /* expected refusal */ }
		} );

		registry.Add( "alembic", "nearest sample at time", () =>
		{
			var cache = new AlembicCache( "/Char/Body", 30 );
			cache.AddSample( 0, TriangleAt( 0f ) );
			cache.AddSample( 30, TriangleAt( 2f ) );
			Assert( cache.SampleAtTime( 0.75 ).Frame == 30, "0.75 s should pick frame 30, got " + cache.SampleAtTime( 0.75 ).Frame );
			Assert( cache.SampleAtTime( 0.1 ).Frame == 0, "0.1 s should pick frame 0, got " + cache.SampleAtTime( 0.1 ).Frame );
			Assert( cache.PositionsAtTime( 0.75 )[0].AlmostEquals( new Vec3( 2f, 0f, 0f ), 1e-6f ), "PositionsAtTime returned the wrong sample" );
		} );

		registry.Add( "alembic", "empty cache refuses to write", () =>
		{
			var cache = new AlembicCache( "/Char/Body", 30 );
			try { cache.Write( Fresh( ".abc" ) ); throw new Exception( "empty cache write was silently accepted" ); }
			catch ( InvalidOperationException ) { /* expected refusal */ }
		} );

		// ----- MaterialX -----

		registry.Add( "materialx", "materialx round-trip preserves preview surface", () =>
		{
			var surface = new UsdPreviewSurface
			{
				Name = "Leather",
				DiffuseColor = new Vec3( 0.5f, 0.3f, 0.1f ),
				DiffuseMap = "tex/leather_diffuse.png",
				Metallic = 0.1f,
				Roughness = 0.7f,
				Opacity = 0.95f,
				EmissiveColor = new Vec3( 0.02f, 0f, 0f ),
				Clearcoat = 0.2f,
				Ior = 1.5f,
				NormalMap = "tex/leather_normal.png",
			};
			var doc = MaterialXDocument.FromPreviewSurface( surface );
			string xml = doc.Write();
			var parsed = MaterialXDocument.Parse( xml );
			var back = parsed.ToPreviewSurface( out var losses );
			Assert( losses.Count == 0, "materialx round-trip reported losses: " + string.Join( "; ", losses ) );
			Assert( back.DiffuseColor.AlmostEquals( surface.DiffuseColor, 1e-4f ), "diffuseColor did not round-trip" );
			AssertNear( back.Metallic, 0.1f, 1e-4f, "metallic" );
			AssertNear( back.Roughness, 0.7f, 1e-4f, "roughness" );
			AssertNear( back.Opacity, 0.95f, 1e-4f, "opacity" );
			Assert( back.EmissiveColor.AlmostEquals( new Vec3( 0.02f, 0f, 0f ), 1e-4f ), "emissiveColor did not round-trip" );
			AssertNear( back.Clearcoat, 0.2f, 1e-4f, "clearcoat" );
			Assert( back.DiffuseMap == "tex/leather_diffuse.png", "diffuse texture did not round-trip: " + back.DiffuseMap );
			Assert( back.NormalMap == "tex/leather_normal.png", "normal texture did not round-trip: " + back.NormalMap );
		} );

		registry.Add( "materialx", "openpbr transmission maps to opacity with a named loss", () =>
		{
			var doc = new MaterialXDocument();
			var node = doc.AddSurface( "Glass", "standard_surface" );
			node.Set( "base_color", "color3", "0.9, 0.9, 0.9" );
			node.Set( "transmission", "float", "0.25" );
			var preview = doc.ToPreviewSurface( out var losses );
			AssertNear( preview.Opacity, 0.75f, 1e-4f, "transmission 0.25 should map to opacity 0.75" );
			Assert( losses.Any( l => l.Contains( "transmission", StringComparison.Ordinal ) ), "transmission mapping was not a named loss: " + string.Join( "; ", losses ) );
		} );

		registry.Add( "materialx", "sheen is a named loss", () =>
		{
			var doc = new MaterialXDocument();
			var node = doc.AddSurface( "Velvet", "standard_surface" );
			node.Set( "base_color", "color3", "0.4, 0.1, 0.1" );
			node.Set( "sheen", "float", "0.5" );
			_ = doc.ToPreviewSurface( out var losses );
			Assert( losses.Any( l => l.Contains( "sheen", StringComparison.Ordinal ) ), "sheen drop was not a named loss: " + string.Join( "; ", losses ) );
		} );

		registry.Add( "materialx", "malformed xml refused", () =>
		{
			try { _ = MaterialXDocument.Parse( "<materialx><oops" ); throw new Exception( "malformed XML was silently accepted" ); }
			catch ( MaterialXException ) { /* expected refusal */ }
			try { _ = MaterialXDocument.Parse( "<foo/>" ); throw new Exception( "non-materialx root was silently accepted" ); }
			catch ( MaterialXException ) { /* expected refusal */ }
			try { _ = MaterialXDocument.Parse( "<materialx version=\"1.39\"/>" ); throw new Exception( "surfaceless document was silently accepted" ); }
			catch ( MaterialXException ) { /* expected refusal */ }
		} );

		registry.Add( "materialx", "preview surface factors agree with MaterialDescriptor", () =>
		{
			var surface = new UsdPreviewSurface
			{
				Name = "Check",
				DiffuseColor = new Vec3( 0.2f, 0.4f, 0.6f ),
				Metallic = 0.8f,
				Roughness = 0.25f,
				Opacity = 0.9f,
				EmissiveColor = new Vec3( 0.1f, 0f, 0f ),
				DiffuseMap = "tex/check.png",
			};
			var desc = UsdMaterialBridge.ToMaterialDescriptor( surface, out var losses );
			Assert( losses.Count == 1 && losses[0].Contains( "AlphaMode", StringComparison.Ordinal ), "opacity<1 should produce exactly one named loss (alpha mode), got: " + string.Join( "; ", losses ) );
			AssertNear( desc.BaseColorFactor.x, 0.2f, 1e-6f, "BaseColorFactor.r" );
			AssertNear( desc.BaseColorFactor.y, 0.4f, 1e-6f, "BaseColorFactor.g" );
			AssertNear( desc.BaseColorFactor.z, 0.6f, 1e-6f, "BaseColorFactor.b" );
			AssertNear( desc.BaseColorFactor.w, 0.9f, 1e-6f, "BaseColorFactor.a (opacity)" );
			AssertNear( desc.RoughnessFactor, 0.25f, 1e-6f, "RoughnessFactor" );
			AssertNear( desc.MetallicFactor, 0.8f, 1e-6f, "MetallicFactor" );
			Assert( desc.EmissionFactor.AlmostEquals( new Vec3( 0.1f, 0f, 0f ), 1e-6f ), "EmissionFactor" );
			Assert( desc.AlphaMode == AlphaMode.Blend, "opacity 0.9 should map to AlphaMode.Blend, got " + desc.AlphaMode );
			Assert( desc.BaseColorMap != null && desc.BaseColorMap.Reference.Path == "tex/check.png", "diffuse texture binding did not survive the bridge" );
			var back = UsdMaterialBridge.FromMaterialDescriptor( desc, out var backLosses );
			Assert( backLosses.Count == 0, "descriptor round-trip reported losses: " + string.Join( "; ", backLosses ) );
			Assert( back.DiffuseColor.AlmostEquals( surface.DiffuseColor, 1e-6f ), "descriptor round-trip lost diffuseColor" );
			AssertNear( back.Opacity, 0.9f, 1e-6f, "descriptor round-trip lost opacity" );
		} );

		// ----- interchange -----

		registry.Add( "interchange", "known limitations are published and named", () =>
		{
			Assert( UsdInterchange.KnownLimitations.Any( l => l.Contains( ".usdc", StringComparison.Ordinal ) ), "loss list does not name .usdc" );
			Assert( UsdInterchange.KnownLimitations.Any( l => l.Contains( "UsdSkel", StringComparison.Ordinal ) ), "loss list does not name UsdSkel" );
			Assert( UsdInterchange.KnownLimitations.Any( l => l.Contains( "Hydra", StringComparison.Ordinal ) ), "loss list does not name Hydra" );
			Assert( UsdInterchange.KnownLimitations.Any( l => l.Contains( "Ogawa", StringComparison.Ordinal ) ), "loss list does not name Ogawa" );
			Assert( UsdInterchange.KnownLimitations.Any( l => l.Contains( "KTX2", StringComparison.Ordinal ) ), "loss list does not name KTX2" );
			Assert( AlembicCache.KnownLimitations.Any( l => l.Contains( "Ogawa", StringComparison.Ordinal ) ), "alembic loss list does not name Ogawa" );
			Assert( MaterialXDocument.KnownLimitations.Any( l => l.Contains( "sheen", StringComparison.Ordinal ) ), "materialx loss list does not name sheen" );
		} );

		registry.Add( "interchange", "import opens a layered stage without overwriting source", () =>
		{
			var source = new UsdLayer( "source" ) { DefaultPrim = "World" };
			var body = new UsdPrim( "/World/Char/Body", UsdPrimKind.Mesh );
			body.SetAttribute( "points", UsdValue.Vec3Array( "point3f[]", new[] { new Vec3( 0f, 0f, 0f ) } ) );
			source.Roots.Add( new UsdPrim( "/World", UsdPrimKind.Xform ) );
			source.Roots[0].Children.Add( new UsdPrim( "/World/Char", UsdPrimKind.Xform ) );
			source.Roots[0].Children[0].Children.Add( body );
			string sourceFile = Fresh( ".usda" );
			File.WriteAllText( sourceFile, UsdAscii.Write( source ) );
			string before = File.ReadAllText( sourceFile );
			var stage = UsdStage.ReadUsda( sourceFile );
			stage.CreatePrim( "/World/Override", UsdPrimKind.Scope ).SetAttribute( "customNote", UsdValue.String( "local edit" ) );
			stage.WriteUsda( Fresh( ".usda" ) );
			string after = File.ReadAllText( sourceFile );
			Assert( before == after, "import + local edit overwrote the source asset" );
			Assert( stage.Find( "/World/Char/Body" ) != null, "re-read stage lost the source mesh prim" );
		} );
	}
}
