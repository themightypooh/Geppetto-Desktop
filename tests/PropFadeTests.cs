#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Map;

// Hammer Desktop P2: prop fade distances + per-face lightmap scale
// (hammer-optimisation-prop-fade-lightmap-scale).
// PropFade (fade distances on prop entities, validated non-negative with
// max >= min, persisted through the registry-checked fademindist /
// fademaxdist keyvalues) + LightmapLuxels (the bake-side reading of the
// per-face lightmap scale: the luxel grid the native bake allocates).
// Built against the real APIs: MapEntities/MapEntity + the core registry
// (prop_static declares both fade keys), FaceUv/FaceMaterial (the scale
// lives on FaceUv.LightmapScale), MapBakeExport (Bake records the scale per
// face on MapBakeFaceInfo).
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned
// and the final integrator adds the one PropFadeTests.Register(registry, settings); line.
internal static class PropFadeTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }
	static bool Near( float a, float b, float eps = 1e-5f ) => MathF.Abs( a - b ) <= eps;

	static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while ( dir is not null )
		{
			if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
			dir = Directory.GetParent( dir )?.FullName;
		}
		dir = Directory.GetCurrentDirectory();
		while ( dir is not null )
		{
			if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
			dir = Directory.GetParent( dir )?.FullName;
		}
		throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
	}

	static string CorePath() => Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" );
	static EntityRegistry LoadCore() => EntityRegistry.Load( CorePath() );
	static MapEntities NewEntities() => new MapEntities( LoadCore() );

	static string? FindFixture()
	{
		const string rel = "hammer-optimisation-prop-fade-lightmap-scale/prop-fade.json";
		var candidates = new[]
		{
			Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", rel ) ),
			Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", rel ) ),
		};
		foreach ( var candidate in candidates )
			if ( File.Exists( candidate ) ) return candidate;
		var dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
		for ( var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent )
		{
			var probe = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", rel );
			if ( File.Exists( probe ) ) return probe;
		}
		return null;
	}

	static PropFadeException ThrowsFade( Action action )
	{
		try { action(); }
		catch ( PropFadeException ex ) { return ex; }
		throw new Exception( "expected PropFadeException, got silent success" );
	}

	static FaceMaterial FaceWithScale( string material, float lightmapScale ) =>
		new( material, new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, lightmapScale, 0 ) );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "propfade", "disabled prop reads as Disabled when no fade keys are set", () =>
		{
			var entities = NewEntities();
			var prop = entities.PlacePointEntity( "prop_static", new Vec3( 128f, 64f, 0f ),
				new Dictionary<string, string> { ["model"] = "props/crate001.mdl" } );
			var fade = PropFade.FromEntity( prop );
			Check( fade.IsDisabled, $"fresh prop must read Disabled, got {fade}" );
			Check( fade.IsValid, "Disabled must validate clean" );
			Check( Near( fade.VisibilityAt( 0f ), 1f ) && Near( fade.VisibilityAt( 100000f ), 1f ),
				"a disabled prop is fully visible at any distance" );
			Check( PropFade.SupportsFade( entities, prop ), "prop_static must support fade in the core registry" );
		} );

		registry.Add( "propfade", "apply fade persists through registry keyvalues", () =>
		{
			var entities = NewEntities();
			var prop = entities.PlacePointEntity( "prop_static", new Vec3( 128f, 64f, 0f ),
				new Dictionary<string, string> { ["model"] = "props/crate001.mdl" } );
			new PropFade( 500f, 1200f ).ApplyTo( entities, prop );
			Check( entities.GetKeyvalue( prop, "fademindist" ) == "500",
				$"fademindist stored: {entities.GetKeyvalue( prop, "fademindist" )}" );
			Check( entities.GetKeyvalue( prop, "fademaxdist" ) == "1200",
				$"fademaxdist stored: {entities.GetKeyvalue( prop, "fademaxdist" )}" );
			var readBack = PropFade.FromEntity( prop );
			Check( readBack.Equals( new PropFade( 500f, 1200f ) ), $"round-trip fade: {readBack}" );
			Check( readBack.IsValid, "round-tripped fade must validate clean" );
		} );

		registry.Add( "propfade", "negative fade min is rejected", () =>
		{
			var ex = ThrowsFade( () => _ = new PropFade( -1f, 1200f ) );
			Check( ex.Code == PropFadeException.CodeNegativeFadeMin, $"code: {ex.Code}" );
		} );

		registry.Add( "propfade", "negative fade max is rejected", () =>
		{
			var ex = ThrowsFade( () => _ = new PropFade( 500f, -10f ) );
			Check( ex.Code == PropFadeException.CodeNegativeFadeMax, $"code: {ex.Code}" );
		} );

		registry.Add( "propfade", "max below min is rejected", () =>
		{
			var ex = ThrowsFade( () => _ = new PropFade( 1200f, 500f ) );
			Check( ex.Code == PropFadeException.CodeFadeMaxBelowMin, $"code: {ex.Code}" );
		} );

		registry.Add( "propfade", "fade on a non-prop class is rejected", () =>
		{
			var entities = NewEntities();
			var start = entities.PlacePointEntity( "info_player_start", new Vec3( 0f, 0f, 0f ) );
			Check( !PropFade.SupportsFade( entities, start ), "info_player_start declares no fade keys" );
			var ex = ThrowsFade( () => new PropFade( 500f, 1200f ).ApplyTo( entities, start ) );
			Check( ex.Code == PropFadeException.CodeFadeNotSupported, $"code: {ex.Code}" );
			var light = entities.PlacePointEntity( "light", new Vec3( 0f, 0f, 64f ) );
			Check( !PropFade.SupportsFade( entities, light ), "light declares no fade keys either" );
		} );

		registry.Add( "propfade", "visibility ramps linearly between min and max", () =>
		{
			var fade = new PropFade( 500f, 1200f );
			Check( Near( fade.VisibilityAt( 0f ), 1f ), $"at 0: {fade.VisibilityAt( 0f )}" );
			Check( Near( fade.VisibilityAt( 500f ), 1f ), $"at min: {fade.VisibilityAt( 500f )}" );
			Check( Near( fade.VisibilityAt( 850f ), 0.5f ), $"at midpoint: {fade.VisibilityAt( 850f )}" );
			Check( Near( fade.VisibilityAt( 1200f ), 0f ), $"at max: {fade.VisibilityAt( 1200f )}" );
			Check( Near( fade.VisibilityAt( 10000f ), 0f ), $"beyond max: {fade.VisibilityAt( 10000f )}" );
			Check( Near( fade.VisibilityAt( float.PositiveInfinity ), 0f ), "at infinity the prop is gone" );
		} );

		registry.Add( "propfade", "min equal to max is a step at that distance", () =>
		{
			var fade = new PropFade( 800f, 800f );
			Check( Near( fade.VisibilityAt( 799f ), 1f ), $"below the step: {fade.VisibilityAt( 799f )}" );
			Check( Near( fade.VisibilityAt( 800f ), 0f ), $"at the step: {fade.VisibilityAt( 800f )}" );
			Check( Near( fade.VisibilityAt( 801f ), 0f ), $"above the step: {fade.VisibilityAt( 801f )}" );
		} );

		registry.Add( "propfade", "registry default min -1 reads as Disabled", () =>
		{
			var entities = NewEntities();
			var prop = entities.PlacePointEntity( "prop_static", new Vec3( 0f, 0f, 0f ),
				new Dictionary<string, string> { ["model"] = "props/crate001.mdl", ["fademindist"] = "-1" } );
			var fade = PropFade.FromEntity( prop );
			Check( fade.IsDisabled, $"min -1 must read Disabled, got {fade}" );
			Check( Near( fade.VisibilityAt( 100000f ), 1f ), "a -1-min prop never fades" );
		} );

		registry.Add( "propfade", "min without max is flagged, never silently hidden", () =>
		{
			var entities = NewEntities();
			var prop = entities.PlacePointEntity( "prop_static", new Vec3( 0f, 0f, 0f ),
				new Dictionary<string, string> { ["model"] = "props/crate001.mdl", ["fademindist"] = "500" } );
			var fade = PropFade.FromEntity( prop );
			Check( !fade.IsDisabled, "a stored min is a fade, not Disabled" );
			var defects = fade.Validate();
			Check( defects.Count == 1 && defects[0].Contains( "fademaxdist" ),
				$"expected one fademaxdist defect, got: {string.Join( "; ", defects )}" );
			Check( !fade.IsValid, "min-without-max must not validate clean" );
			Check( Near( fade.VisibilityAt( 100000f ), 1f ),
				"an invalid fade fails visible — it must never hide the prop" );
		} );

		registry.Add( "propfade", "applying Disabled writes the registry defaults", () =>
		{
			var entities = NewEntities();
			var prop = entities.PlacePointEntity( "prop_static", new Vec3( 0f, 0f, 0f ),
				new Dictionary<string, string> { ["model"] = "props/crate001.mdl" } );
			new PropFade( 500f, 1200f ).ApplyTo( entities, prop );
			PropFade.Disabled.ApplyTo( entities, prop );
			Check( entities.GetKeyvalue( prop, "fademindist" ) == "-1",
				$"disabled fademindist: {entities.GetKeyvalue( prop, "fademindist" )}" );
			Check( entities.GetKeyvalue( prop, "fademaxdist" ) == "0",
				$"disabled fademaxdist: {entities.GetKeyvalue( prop, "fademaxdist" )}" );
			Check( PropFade.FromEntity( prop ).IsDisabled, "defaults must read back as Disabled" );
		} );

		registry.Add( "propfade", "lightmap scale 4 vs 1 changes linear luxel density 4x and total count 16x", () =>
		{
			var fine = LightmapLuxels.ForFace( 64f, 64f, 1f );
			var coarse = LightmapLuxels.ForFace( 64f, 64f, 4f );
			Check( fine.Cols == 64 && fine.Rows == 64 && fine.Total == 4096,
				$"scale 1 grid: {fine}" );
			Check( coarse.Cols == 16 && coarse.Rows == 16 && coarse.Total == 256,
				$"scale 4 grid: {coarse}" );
			Check( Near( fine.LuxelsPerUnitLength, 1f ) && Near( coarse.LuxelsPerUnitLength, 0.25f ),
				$"linear densities: {fine.LuxelsPerUnitLength} vs {coarse.LuxelsPerUnitLength}" );
			Check( Near( LightmapLuxels.DensityRatio( 1f, 4f ), 4f ),
				$"density ratio 1 vs 4: {LightmapLuxels.DensityRatio( 1f, 4f )}" );
			Check( Near( LightmapLuxels.CountRatio( 1f, 4f ), 16f ),
				$"count ratio 1 vs 4: {LightmapLuxels.CountRatio( 1f, 4f )}" );
			// A face smaller than one luxel still gets exactly one sample.
			var tiny = LightmapLuxels.ForFace( 2f, 3f, 16f );
			Check( tiny.Cols == 1 && tiny.Rows == 1 && tiny.Total == 1, $"tiny face grid: {tiny}" );
		} );

		registry.Add( "propfade", "luxel grid follows the face's own lightmap scale", () =>
		{
			var verts = new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 0f, 0f ),
				new Vec3( 64f, 64f, 0f ), new Vec3( 0f, 64f, 0f ),
			};
			var normal = new Vec3( 0f, 0f, 1f );
			var fine = LightmapLuxels.ForFace( FaceWithScale( "brick/brickwall001", 1f ), verts, normal );
			var coarse = LightmapLuxels.ForFace( FaceWithScale( "brick/brickwall001", 4f ), verts, normal );
			Check( fine.Total == 4096, $"scale-1 face grid: {fine}" );
			Check( coarse.Total == 256, $"scale-4 face grid: {coarse}" );
			var def = LightmapLuxels.ForFace( FaceMaterial.ForMaterial( "brick/brickwall001" ), verts, normal );
			Check( Near( def.LightmapScale, FaceUv.DefaultLightmapScale ),
				$"default face scale: {def.LightmapScale}" );
			Check( def.Cols == 4 && def.Rows == 4 && def.Total == 16, $"default scale-16 grid: {def}" );
		} );

		registry.Add( "propfade", "bake records per-face lightmap scale and the grid follows it", () =>
		{
			var materials = new[]
			{
				FaceWithScale( "brick/brickwall001", 1f ),
				FaceWithScale( "brick/brickwall001", 4f ),
				FaceWithScale( "brick/brickwall001", 16f ),
				FaceWithScale( "brick/brickwall001", 16f ),
				FaceWithScale( "brick/brickwall001", 16f ),
				FaceWithScale( "brick/brickwall001", 16f ),
			};
			var box = MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), materials );
			var baked = MapBakeExport.Bake( new[] { box }, Array.Empty<MapBakeDisplacement>() );
			Check( baked.FaceInfo.Count == 6, $"baked faces: {baked.FaceInfo.Count}" );
			var byFace = baked.FaceInfo.ToDictionary( f => f.FaceIndex );
			Check( Near( byFace[0].LightmapScale, 1f ), $"face 0 recorded scale: {byFace[0].LightmapScale}" );
			Check( Near( byFace[1].LightmapScale, 4f ), $"face 1 recorded scale: {byFace[1].LightmapScale}" );
			Check( Near( byFace[2].LightmapScale, 16f ), $"face 2 recorded scale: {byFace[2].LightmapScale}" );
			// Every face of the 64^3 box is 64x64: the recorded scale sizes its lightmap.
			var grid1 = LightmapLuxels.ForBakedFace( byFace[0], 64f, 64f );
			var grid4 = LightmapLuxels.ForBakedFace( byFace[1], 64f, 64f );
			Check( grid1.Total == 4096, $"baked scale-1 grid: {grid1}" );
			Check( grid4.Total == 256, $"baked scale-4 grid: {grid4}" );
			Check( baked.Issues.Count == 0, $"bake issues: {string.Join( "; ", baked.Issues )}" );
		} );

		registry.Add( "propfade", "fixture values match the model", () =>
		{
			var path = FindFixture();
			Check( path is not null, "fixture tests/fixtures/hammer/hammer-optimisation-prop-fade-lightmap-scale/prop-fade.json not found" );
			var json = JsonNode.Parse( File.ReadAllText( path! ) ) as JsonObject;
			Check( json is not null, "fixture is not a JSON object" );
			var propJson = json!["prop"] as JsonObject;
			Check( propJson is not null, "fixture has no 'prop' object" );
			var fade = new PropFade(
				(float)propJson!["fadeMinDist"]!.GetValue<double>(),
				(float)propJson["fadeMaxDist"]!.GetValue<double>() );
			var entities = NewEntities();
			var prop = entities.PlacePointEntity( propJson["class"]!.GetValue<string>(), new Vec3( 128f, 64f, 0f ),
				new Dictionary<string, string> { ["model"] = propJson["model"]!.GetValue<string>() } );
			fade.ApplyTo( entities, prop );
			Check( entities.GetKeyvalue( prop, "fademindist" ) == "500", "fixture min must persist" );
			Check( entities.GetKeyvalue( prop, "fademaxdist" ) == "1200", "fixture max must persist" );
			foreach ( var row in propJson["visibility"]!.AsArray() )
			{
				var obj = row!.AsObject();
				var at = (float)obj["distance"]!.GetValue<double>();
				var want = (float)obj["expected"]!.GetValue<double>();
				Check( Near( fade.VisibilityAt( at ), want ),
					$"fixture visibility at {at}: {fade.VisibilityAt( at )}, expected {want}" );
			}
			foreach ( var row in json["faces"]!.AsArray() )
			{
				var obj = row!.AsObject();
				var grid = LightmapLuxels.ForFace(
					(float)obj["width"]!.GetValue<double>(),
					(float)obj["height"]!.GetValue<double>(),
					(float)obj["lightmapScale"]!.GetValue<double>() );
				Check( grid.Cols == obj["expectedCols"]!.GetValue<int>() && grid.Rows == obj["expectedRows"]!.GetValue<int>()
					&& grid.Total == obj["expectedTotal"]!.GetValue<int>(),
					$"fixture face scale {obj["lightmapScale"]}: {grid}" );
				Check( Near( grid.LuxelsPerUnitLength, (float)obj["expectedLinearDensity"]!.GetValue<double>() ),
					$"fixture face density: {grid.LuxelsPerUnitLength}" );
			}
			Check( Near( LightmapLuxels.DensityRatio( 1f, 4f ), (float)json["densityRatioScale1Vs4"]!.GetValue<double>() ),
				"fixture density ratio must be 4" );
			Check( Near( LightmapLuxels.CountRatio( 1f, 4f ), (float)json["countRatioScale1Vs4"]!.GetValue<double>() ),
				"fixture count ratio must be 16" );
		} );

		registry.Add( "propfade", "keyvalue and JSON round-trips preserve the fade", () =>
		{
			var fade = new PropFade( 500f, 1200f );
			var back = PropFade.FromKeyvalues( fade.ToKeyvalues() );
			Check( back.Equals( fade ), $"keyvalue round-trip: {back}" );
			Check( fade.ToKeyvalues()["fademindist"] == "500" && fade.ToKeyvalues()["fademaxdist"] == "1200",
				"keyvalue names must be the Source fademindist/fademaxdist" );
			var jsonBack = PropFade.FromJson( fade.ToJson() );
			Check( jsonBack.Equals( fade ), $"JSON round-trip: {jsonBack}" );
			var disabledBack = PropFade.FromJson( PropFade.Disabled.ToJson() );
			Check( disabledBack.IsDisabled, "Disabled must survive JSON" );
			var disabledKv = PropFade.FromKeyvalues( PropFade.Disabled.ToKeyvalues() );
			Check( disabledKv.IsDisabled, "Disabled must survive keyvalues" );
		} );

		registry.Add( "propfade", "luxel grid rejects a non-positive lightmap scale", () =>
		{
			try { _ = LightmapLuxels.ForFace( 64f, 64f, 0f ); }
			catch ( ArgumentException ex ) { Check( ex.Message.Contains( "Lightmap scale" ), $"zero scale error: {ex.Message}" ); return; }
			throw new Exception( "scale 0 must throw" );
		} );

		registry.Add( "propfade", "luxel grid rejects a negative face extent", () =>
		{
			try { _ = LightmapLuxels.ForFace( -1f, 64f, 16f ); }
			catch ( ArgumentException ex ) { Check( ex.Message.Contains( "width" ), $"negative width error: {ex.Message}" ); return; }
			throw new Exception( "negative width must throw" );
		} );
	}
}
