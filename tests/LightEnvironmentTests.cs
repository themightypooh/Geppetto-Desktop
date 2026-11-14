#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

// Hammer Desktop P2: lighting entities — light, light_spot, light_environment + env_sun,
// light_dynamic, env_fog_controller, shadow_control, env_tonemap_controller
// (hammer-lighting-environment-sun-fog).
//
// The ticket's "seven classes" counts light_environment + env_sun as one pair: the
// registry actually carries EIGHT class names for it (light, light_spot,
// light_environment, env_sun, light_dynamic, env_fog_controller, shadow_control,
// env_tonemap_controller) — the merged-registry test asserts all eight and names the
// count here so the discrepancy is visible, not hidden.
//
// DEPENDENCY NOTE (2026-11-13): CompileRunner (hammer-compile-native-pipeline) has
// landed on disk (kernel/Effigy/Map/CompileRunner.cs) and its ticket is done, so
// these tests build against what is actually there —
// CompileLight is a POINT light only (Position/Color/Intensity/Range) and
// CompileInput.Lights is List<CompileLight> — and document the mapping in
// LightEnvironment's header (sun = distant-light approximation, spot cone dropped,
// ambient/dynamic dropped with counts). If the runner gains a directional/ambient
// light, the sun-approximation test is the one that changes. LiveLighting
// (hammer-preview-live-lighting) has landed and is consumed through its real API.
// EntityRegistry (hammer-entity-registry-fgd-json) is DONE; core-source1.json ships
// light + light_spot and this ticket's six classes merge in from
// lighting-source1.json. NOT registered in tests/TestRegistry.cs yet — that file is
// coordinator-owned and the final integrator adds the one line.
internal static class LightEnvironmentTests
{
	static bool Near( float a, float b, float eps = 1e-4f ) => MathF.Abs( a - b ) <= eps;
	static bool Near( Vec3 a, Vec3 b, float eps = 1e-4f ) =>
		Near( a.x, b.x, eps ) && Near( a.y, b.y, eps ) && Near( a.z, b.z, eps );

	static string RepoRoot()
	{
		foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
		{
			var dir = new DirectoryInfo( start );
			while ( dir is not null )
			{
				if ( File.Exists( Path.Combine( dir.FullName, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir.FullName;
				dir = dir.Parent;
			}
		}
		throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
	}

	static string FixtureDir( string name )
	{
		var dir = Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-lighting-environment-sun-fog", name );
		if ( !File.Exists( dir ) ) throw new Exception( $"fixture not found: {dir}" );
		return dir;
	}

	static EntityRegistry LoadCore() =>
		EntityRegistry.Load( Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" ) );

	static EntityRegistry MergedRegistry()
	{
		var registry = LoadCore();
		LightEnvironment.MergeInto( registry );
		return registry;
	}

	static Dictionary<string, string> Kv( params (string key, string value)[] pairs )
	{
		var dict = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (key, value) in pairs ) dict[key] = value;
		return dict;
	}

	static MapEntity Place( MapEntities map, string className, Vec3 origin,
		Dictionary<string, string>? keyvalues = null, Vec3? angles = null ) =>
		map.PlacePointEntity( className, origin, keyvalues, angles );

	static Vec3 Angles( string text )
	{
		var parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) throw new Exception( $"bad angles fixture value '{text}'" );
		return new Vec3( float.Parse( parts[0], CultureInfo.InvariantCulture ), float.Parse( parts[1], CultureInfo.InvariantCulture ), float.Parse( parts[2], CultureInfo.InvariantCulture ) );
	}

	/// <summary>Six outward faces of an axis box; ids base..base+5 (same winding as LiveLightingTests).</summary>
	static IEnumerable<LiveFace> BoxFaces( int @base, Vec3 min, Vec3 size )
	{
		var (x0, y0, z0) = (min.x, min.y, min.z);
		var (x1, y1, z1) = (min.x + size.x, min.y + size.y, min.z + size.z);
		yield return new LiveFace( @base + 0, new[] { new Vec3( x0, y0, z0 ), new Vec3( x1, y0, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x0, y1, z0 ) } );
		yield return new LiveFace( @base + 1, new[] { new Vec3( x0, y0, z1 ), new Vec3( x0, y1, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y0, z1 ) } );
		yield return new LiveFace( @base + 2, new[] { new Vec3( x0, y0, z0 ), new Vec3( x0, y0, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y0, z0 ) } );
		yield return new LiveFace( @base + 3, new[] { new Vec3( x1, y1, z0 ), new Vec3( x1, y1, z1 ), new Vec3( x0, y1, z1 ), new Vec3( x0, y1, z0 ) } );
		yield return new LiveFace( @base + 4, new[] { new Vec3( x0, y0, z0 ), new Vec3( x0, y1, z0 ), new Vec3( x0, y1, z1 ), new Vec3( x0, y0, z1 ) } );
		yield return new LiveFace( @base + 5, new[] { new Vec3( x1, y0, z0 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y1, z0 ) } );
	}

	/// <summary>The sealed-room brushes from sun-room.json (same six boxes as the compilerunner sealed-room fixture).</summary>
	static List<CompileBrush> RoomBrushes() => new()
	{
		CompileBrush.Box( "floor", 0, new Vec3( -144f, -144f, -16f ), new Vec3( 144f, 144f, 0f ), "concrete" ),
		CompileBrush.Box( "ceiling", 1, new Vec3( -144f, -144f, 128f ), new Vec3( 144f, 144f, 144f ), "concrete" ),
		CompileBrush.Box( "wall_west", 2, new Vec3( -144f, -144f, 0f ), new Vec3( -128f, 144f, 128f ), "concrete" ),
		CompileBrush.Box( "wall_east", 3, new Vec3( 128f, -144f, 0f ), new Vec3( 144f, 144f, 128f ), "concrete" ),
		CompileBrush.Box( "wall_south", 4, new Vec3( -128f, -144f, 0f ), new Vec3( 128f, -128f, 128f ), "concrete" ),
		CompileBrush.Box( "wall_north", 5, new Vec3( -128f, 128f, 0f ), new Vec3( 128f, 144f, 128f ), "concrete" ),
	};

	static (List<CompileBrush> brushes, Vec3 playerStart, List<(string cls, Vec3 origin, Vec3 angles, Dictionary<string, string> kv)> entities) LoadSunRoom()
	{
		using var doc = JsonDocument.Parse( File.ReadAllText( FixtureDir( "sun-room.json" ) ) );
		var root = doc.RootElement;
		var brushes = new List<CompileBrush>();
		foreach ( var brush in root.GetProperty( "brushes" ).EnumerateArray() )
		{
			var box = brush.GetProperty( "box" );
			Vec3 ReadVec( string key )
			{
				var a = box.GetProperty( key );
				return new Vec3( a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle() );
			}
			brushes.Add( CompileBrush.Box( brush.GetProperty( "name" ).GetString() ?? "brush",
				brush.GetProperty( "id" ).GetInt32(), ReadVec( "min" ), ReadVec( "max" ),
				brush.GetProperty( "material" ).GetString() ?? "concrete" ) );
		}
		var start = root.GetProperty( "playerStart" );
		var playerStart = new Vec3( start[0].GetSingle(), start[1].GetSingle(), start[2].GetSingle() );
		var entities = new List<(string, Vec3, Vec3, Dictionary<string, string>)>();
		foreach ( var entity in root.GetProperty( "entities" ).EnumerateArray() )
		{
			Vec3 origin = Vec3.Zero, angles = Vec3.Zero;
			if ( entity.TryGetProperty( "origin", out var o ) )
				origin = new Vec3( o[0].GetSingle(), o[1].GetSingle(), o[2].GetSingle() );
			if ( entity.TryGetProperty( "angles", out var a ) ) angles = Angles( a.GetString() ?? "0 0 0" );
			var kv = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			if ( entity.TryGetProperty( "keyvalues", out var kvEl ) )
				foreach ( var pair in kvEl.EnumerateObject() ) kv[pair.Name] = pair.Value.GetString() ?? "";
			entities.Add( (entity.GetProperty( "classname" ).GetString() ?? "", origin, angles, kv) );
		}
		return (brushes, playerStart, entities);
	}

	static LightingSetup SetupFromSunRoom()
	{
		var (_, _, specs) = LoadSunRoom();
		var map = new MapEntities( MergedRegistry() );
		foreach ( var (cls, origin, angles, kv) in specs )
			Place( map, cls, origin, kv, angles );
		return LightEnvironment.FromEntities( map.Entities );
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "lightenvironment", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		string WorkDir( string name )
		{
			var dir = Path.Combine( settings.WorkDirectory, "lightenvironment", name );
			Directory.CreateDirectory( dir );
			return dir;
		}

		Check( "lighting registry file loads with 6 classes", () =>
		{
			var reg = EntityRegistry.Load( FixtureDir( "lighting-source1.json" ) );
			Require( reg.Name == "source1-lighting", $"lighting registry name is '{reg.Name}', expected 'source1-lighting'" );
			Require( reg.SchemaVersion == 1, $"schema version is {reg.SchemaVersion}, expected 1" );
			Require( reg.Count == 6, $"lighting registry has {reg.Count} classes, expected 6" );
			foreach ( var name in new[] { "light_environment", "env_sun", "light_dynamic", "env_fog_controller", "shadow_control", "env_tonemap_controller" } )
				Require( reg.Contains( name ), $"lighting registry is missing '{name}'" );
			Require( !reg.Contains( "light" ) && !reg.Contains( "light_spot" ),
				"lighting registry must not redeclare light/light_spot (they ship in core-source1.json)" );
		} );

		Check( "embedded registry JSON matches the fixture file", () =>
		{
			var embedded = LightEnvironment.CreateLightingRegistry();
			var fromFile = EntityRegistry.Load( FixtureDir( "lighting-source1.json" ) );
			Require( embedded.Count == fromFile.Count, $"embedded has {embedded.Count} classes, file has {fromFile.Count}" );
			Require( embedded.Name == fromFile.Name, $"embedded name '{embedded.Name}' != file name '{fromFile.Name}'" );
			foreach ( var cls in fromFile.Classes )
			{
				var match = embedded.Find( cls.Name );
				Require( match is not null, $"embedded registry is missing '{cls.Name}'" );
				Require( match!.KeyValues.Length == cls.KeyValues.Length,
					$"'{cls.Name}': embedded has {match.KeyValues.Length} keyvalues, file has {cls.KeyValues.Length}" );
			}
		} );

		Check( "merged registry contains all seven lighting classes", () =>
		{
			var reg = MergedRegistry();
			// Core class count is owned by the entity-registry ticket (it grew 14 -> 15
			// while this ticket ran); assert relative to it, not to a pinned number.
			var coreCount = LoadCore().Count;
			Require( reg.Count == coreCount + 6, $"merged registry has {reg.Count} classes, expected core {coreCount} + lighting 6" );
			// The ticket's "seven" counts light_environment + env_sun as one pair; the
			// registry carries eight class names for it. Assert all eight, not seven.
			foreach ( var name in LightEnvironment.LightingClassNames )
				Require( reg.Contains( name ), $"merged registry is missing '{name}'" );
			Require( LightEnvironment.LightingClassNames.Length == 8,
				$"LightingClassNames has {LightEnvironment.LightingClassNames.Length} entries; the ticket's 'seven' is eight names with the sun pair counted once" );
		} );

		Check( "key types per lighting class", () =>
		{
			var reg = MergedRegistry();
			void KeyType( string cls, string key, KeyValueType type )
			{
				var found = reg.Get( cls ).FindKeyValue( key );
				Require( found is not null, $"'{cls}' is missing key '{key}'" );
				Require( found!.Type == type, $"'{cls}' key '{key}' is {found.Type}, expected {type}" );
			}
			void Kind( string cls ) => Require( reg.Get( cls ).Kind == EntityKind.Point, $"'{cls}' must be a point entity" );

			Kind( "light" );
			KeyType( "light", "_light", KeyValueType.Colour );
			KeyType( "light", "_distance", KeyValueType.Float );
			KeyType( "light", "style", KeyValueType.Int );

			Kind( "light_spot" );
			KeyType( "light_spot", "_light", KeyValueType.Colour );
			KeyType( "light_spot", "target", KeyValueType.Target );
			KeyType( "light_spot", "_cone", KeyValueType.Float );
			KeyType( "light_spot", "_inner_cone", KeyValueType.Float );
			KeyType( "light_spot", "_distance", KeyValueType.Float );

			Kind( "light_environment" );
			KeyType( "light_environment", "_light", KeyValueType.Colour );
			KeyType( "light_environment", "_ambient", KeyValueType.Colour );
			KeyType( "light_environment", "pitch", KeyValueType.Int );

			Kind( "env_sun" );
			KeyType( "env_sun", "rendercolor", KeyValueType.Colour );
			KeyType( "env_sun", "size", KeyValueType.Int );
			KeyType( "env_sun", "overlaycolor", KeyValueType.Colour );
			KeyType( "env_sun", "overlaysize", KeyValueType.Int );

			Kind( "light_dynamic" );
			KeyType( "light_dynamic", "_light", KeyValueType.Colour );
			KeyType( "light_dynamic", "_distance", KeyValueType.Float );
			KeyType( "light_dynamic", "style", KeyValueType.Int );

			Kind( "env_fog_controller" );
			KeyType( "env_fog_controller", "fogcolor", KeyValueType.Colour );
			KeyType( "env_fog_controller", "fogcolor2", KeyValueType.Colour );
			KeyType( "env_fog_controller", "fogstart", KeyValueType.Float );
			KeyType( "env_fog_controller", "fogend", KeyValueType.Float );
			KeyType( "env_fog_controller", "fogmaxdensity", KeyValueType.Float );
			KeyType( "env_fog_controller", "farz", KeyValueType.Float );
			KeyType( "env_fog_controller", "fogenable", KeyValueType.Int );

			Kind( "shadow_control" );
			KeyType( "shadow_control", "color", KeyValueType.Colour );
			KeyType( "shadow_control", "distance", KeyValueType.Float );
			KeyType( "shadow_control", "disableallshadows", KeyValueType.Int );

			Kind( "env_tonemap_controller" );
			KeyType( "env_tonemap_controller", "bloom_scale", KeyValueType.Float );
			KeyType( "env_tonemap_controller", "autoexposure_min", KeyValueType.Float );
			KeyType( "env_tonemap_controller", "autoexposure_max", KeyValueType.Float );
			KeyType( "env_tonemap_controller", "tonemap_rate", KeyValueType.Float );
			KeyType( "env_tonemap_controller", "tonemap_percent_bright_pixels", KeyValueType.Float );
			KeyType( "env_tonemap_controller", "tonemap_min_avglum", KeyValueType.Float );
			KeyType( "env_tonemap_controller", "tonemap_percent_target", KeyValueType.Float );
		} );

		Check( "merging lighting twice is rejected with DuplicateClass", () =>
		{
			var reg = MergedRegistry();
			var before = reg.Count;
			try { LightEnvironment.MergeInto( reg ); throw new Exception( "MergeInto did not throw for duplicate classes" ); }
			catch ( EntityRegistryException ex ) { Require( ex.Code == EntityRegistry.CodeDuplicateClass, $"error code is '{ex.Code}', expected DuplicateClass" ); }
			Require( reg.Count == before, $"failed merge must not change the registry (count {reg.Count}, was {before})" );
		} );

		Check( "point light resolves with colour and brightness", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			var light = Place( map, "light", new Vec3( 0f, 0f, 96f ),
				Kv( ("_light", "255 128 0 200"), ("_distance", "512") ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			Require( setup.Lights.Count == 1, $"expected 1 light, got {setup.Lights.Count}" );
			var resolved = setup.Lights[0];
			Require( resolved.Kind == BakeLightKind.Point, $"kind is {resolved.Kind}, expected Point" );
			Require( resolved.SourceEntityId == light.Id, $"source id is {resolved.SourceEntityId}, expected {light.Id}" );
			Require( Near( resolved.Color, new Vec3( 1f, 128f / 255f, 0f ) ), $"colour: {resolved.Color}" );
			Require( Near( resolved.Intensity, 200f ), $"intensity: {resolved.Intensity}" );
			Require( Near( resolved.Range, 512f ), $"range: {resolved.Range}" );
			Require( resolved.Position.Equals( new Vec3( 0f, 0f, 96f ) ), $"position: {resolved.Position}" );
			Require( setup.Issues.Count == 0, $"unexpected issues: {string.Join( "; ", setup.Issues.Select( i => i.ToString() ) )}" );

			var bare = new MapEntities( MergedRegistry() );
			Place( bare, "light", new Vec3( 1f, 2f, 3f ) );
			var bareSetup = LightEnvironment.FromEntities( bare.Entities );
			Require( Near( bareSetup.Lights[0].Color, Vec3.One ), $"bare light colour: {bareSetup.Lights[0].Color}" );
			Require( Near( bareSetup.Lights[0].Intensity, 1f ), $"bare light intensity: {bareSetup.Lights[0].Intensity}" );
			Require( Near( bareSetup.Lights[0].Range, 0f ), $"bare light range: {bareSetup.Lights[0].Range}" );
		} );

		Check( "spot aims at its target, falls back to angles", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "info_player_start", new Vec3( 100f, 0f, 0f ), Kv( ("targetname", "lamp_target") ) );
			Place( map, "light_spot", new Vec3( 0f, 0f, 64f ),
				Kv( ("_light", "255 255 255 100"), ("target", "lamp_target"), ("_cone", "45"), ("_inner_cone", "30") ),
				new Vec3( 0f, 0f, 0f ) );
			// The resolver maps the targetname to the placed entity's origin.
			var setup = LightEnvironment.FromEntities( map.Entities,
				name => map.FindByTargetName( name ).FirstOrDefault()?.Origin );
			Require( setup.Lights.Count == 1, $"expected 1 light, got {setup.Lights.Count}" );
			var resolved = setup.Lights[0];
			Require( resolved.Kind == BakeLightKind.Spot, $"kind is {resolved.Kind}, expected Spot" );
			var expected = new Vec3( 100f, 0f, -64f ).Normal;
			Require( Near( resolved.Direction, expected, 1e-3f ), $"spot must aim at its target: {resolved.Direction} vs {expected}" );
			Require( Near( resolved.InnerConeDegrees, 30f ) && Near( resolved.OuterConeDegrees, 45f ),
				$"cone inner/outer: {resolved.InnerConeDegrees}/{resolved.OuterConeDegrees}" );
			Require( Near( resolved.Intensity, 100f ), $"intensity: {resolved.Intensity}" );
			Require( !setup.HasIssue( LightEnvironment.CodeSpotTargetUnresolved ), "resolved target must not warn" );

			var dangling = new MapEntities( MergedRegistry() );
			Place( dangling, "light_spot", new Vec3( 0f, 0f, 64f ),
				Kv( ("target", "ghost_target") ), new Vec3( 45f, 0f, 0f ) );
			var danglingSetup = LightEnvironment.FromEntities( dangling.Entities );
			Require( danglingSetup.HasIssue( LightEnvironment.CodeSpotTargetUnresolved ),
				$"dangling target must warn: {string.Join( "; ", danglingSetup.Issues.Select( i => i.ToString() ) )}" );
			// Angles fallback: pitch +45 (positive-down) shines down-forward at 45 degrees.
			Require( Near( danglingSetup.Lights[0].Direction, new Vec3( MathF.Cos( MathF.PI / 4f ), 0f, -MathF.Sin( MathF.PI / 4f ) ), 1e-3f ),
				$"angles fallback direction: {danglingSetup.Lights[0].Direction}" );

			var badCone = new MapEntities( MergedRegistry() );
			Place( badCone, "light_spot", new Vec3( 0f, 0f, 64f ),
				Kv( ("_cone", "45"), ("_inner_cone", "60") ) );
			var badSetup = LightEnvironment.FromEntities( badCone.Entities );
			Require( badSetup.HasIssue( LightEnvironment.CodeSpotConeInvalid ), "inner >= outer cone must warn" );
			Require( Near( badSetup.Lights[0].InnerConeDegrees, 22.5f ) && Near( badSetup.Lights[0].OuterConeDegrees, 45f ),
				$"clamped cone: {badSetup.Lights[0].InnerConeDegrees}/{badSetup.Lights[0].OuterConeDegrees}" );
		} );

		Check( "sun direction follows pitch and angles", () =>
		{
			// SkyboxPreview convention: angles "0 90 0" points at the sun at (0, 1, 0).
			Require( Near( LightEnvironment.DirectionFromAngles( 0f, 90f ), new Vec3( 0f, 1f, 0f ) ),
				$"DirectionFromAngles(0, 90): {LightEnvironment.DirectionFromAngles( 0f, 90f )}" );
			Require( Near( LightEnvironment.SunTravelDirection( new Vec3( 0f, 90f, 0f ) ), new Vec3( 0f, -1f, 0f ) ),
				$"sun travel for angles 0 90 0: {LightEnvironment.SunTravelDirection( new Vec3( 0f, 90f, 0f ) )}" );
			// Pitch -90: sun straight overhead, light travels straight down.
			var overhead = new MapEntities( MergedRegistry() );
			Place( overhead, "light_environment", Vec3.Zero, Kv( ("_light", "255 255 255 200") ), new Vec3( -90f, 0f, 0f ) );
			Place( overhead, "env_sun", Vec3.Zero, null, new Vec3( -90f, 0f, 0f ) );
			var overheadSetup = LightEnvironment.FromEntities( overhead.Entities );
			Require( Near( overheadSetup.Sun!.Direction, new Vec3( 0f, 0f, -1f ) ),
				$"overhead sun travel: {overheadSetup.Sun.Direction}" );
			Require( overheadSetup.Issues.Count == 0, $"paired overhead sun must not warn: {string.Join( "; ", overheadSetup.Issues.Select( i => i.ToString() ) )}" );
			// pitch key overrides angles.x: angles pitch 0 + pitch -45 shines down at 45 degrees.
			var pitched = new MapEntities( MergedRegistry() );
			Place( pitched, "light_environment", Vec3.Zero, Kv( ("pitch", "-45") ), new Vec3( 0f, 0f, 0f ) );
			Place( pitched, "env_sun", Vec3.Zero, null, new Vec3( 0f, 0f, 0f ) );
			var pitchedSetup = LightEnvironment.FromEntities( pitched.Entities );
			var expected = new Vec3( -MathF.Cos( MathF.PI / 4f ), 0f, -MathF.Sin( MathF.PI / 4f ) );
			Require( Near( pitchedSetup.Sun!.Direction, expected, 1e-3f ),
				$"pitch -45 sun travel: {pitchedSetup.Sun.Direction} vs {expected}" );
		} );

		Check( "sun without env_sun warns, paired sun does not", () =>
		{
			var alone = new MapEntities( MergedRegistry() );
			Place( alone, "light_environment", Vec3.Zero, Kv( ("_light", "255 255 255 200") ), new Vec3( -45f, 0f, 0f ) );
			var aloneSetup = LightEnvironment.FromEntities( alone.Entities );
			Require( aloneSetup.HasIssue( LightEnvironment.CodeSunWithoutEnvSun ),
				$"light_environment alone must warn: {string.Join( "; ", aloneSetup.Issues.Select( i => i.ToString() ) )}" );
			Require( aloneSetup.Sun is not null && aloneSetup.Environment is null,
				"bare light_environment resolves a sun and (with default black _ambient) no ambient term" );

			var paired = new MapEntities( MergedRegistry() );
			Place( paired, "light_environment", Vec3.Zero, Kv( ("_light", "255 255 255 200"), ("_ambient", "51 51 51 1") ), new Vec3( -45f, 0f, 0f ) );
			Place( paired, "env_sun", Vec3.Zero, null, new Vec3( -45f, 0f, 0f ) );
			var pairedSetup = LightEnvironment.FromEntities( paired.Entities );
			Require( pairedSetup.Issues.Count == 0, $"paired sun must not warn: {string.Join( "; ", pairedSetup.Issues.Select( i => i.ToString() ) )}" );
			Require( pairedSetup.HasEnvSun && pairedSetup.Sun is not null && pairedSetup.Environment is not null,
				"paired setup must have sun + ambient + HasEnvSun" );
			Require( Near( pairedSetup.Environment!.Color, new Vec3( 0.2f, 0.2f, 0.2f ) ),
				$"ambient colour: {pairedSetup.Environment.Color}" );
			Require( Near( pairedSetup.Environment.Intensity, 1f ), $"ambient intensity: {pairedSetup.Environment.Intensity}" );

			var spriteOnly = new MapEntities( MergedRegistry() );
			Place( spriteOnly, "env_sun", Vec3.Zero, Kv( ("rendercolor", "255 244 214"), ("size", "32") ), new Vec3( -45f, 0f, 0f ) );
			var spriteSetup = LightEnvironment.FromEntities( spriteOnly.Entities );
			Require( spriteSetup.HasIssue( LightEnvironment.CodeEnvSunWithoutLightEnvironment ),
				$"env_sun alone must warn: {string.Join( "; ", spriteSetup.Issues.Select( i => i.ToString() ) )}" );
			Require( Near( spriteSetup.EnvSunColor, new Vec3( 1f, 244f / 255f, 214f / 255f ) ), $"env_sun colour: {spriteSetup.EnvSunColor}" );
			Require( spriteSetup.EnvSunSize == 32, $"env_sun size: {spriteSetup.EnvSunSize}" );
		} );

		Check( "env_sun direction mismatch warns", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "light_environment", Vec3.Zero, null, new Vec3( -90f, 0f, 0f ) );
			Place( map, "env_sun", Vec3.Zero, null, new Vec3( 0f, 180f, 0f ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			Require( setup.HasIssue( LightEnvironment.CodeEnvSunDirectionMismatch ),
				$"90-degree sun/sprite mismatch must warn: {string.Join( "; ", setup.Issues.Select( i => i.ToString() ) )}" );
			Require( !setup.HasIssue( LightEnvironment.CodeSunWithoutEnvSun ), "a present (if misaimed) env_sun still pairs" );
		} );

		Check( "multiple light_environment first wins", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "light_environment", Vec3.Zero, Kv( ("_light", "255 0 0 100") ), new Vec3( -90f, 0f, 0f ) );
			Place( map, "env_sun", Vec3.Zero, null, new Vec3( -90f, 0f, 0f ) );
			Place( map, "light_environment", Vec3.Zero, Kv( ("_light", "0 0 255 100") ), new Vec3( -90f, 0f, 0f ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			Require( setup.HasIssue( LightEnvironment.CodeMultipleLightEnvironments ), "second light_environment must warn" );
			Require( setup.Lights.Count == 1, $"first wins: expected 1 light, got {setup.Lights.Count}" );
			Require( Near( setup.Sun!.Color, new Vec3( 1f, 0f, 0f ) ), $"first sun colour kept: {setup.Sun.Color}" );
		} );

		Check( "dynamic light reaches preview but not the bake", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "light_dynamic", new Vec3( 0f, 0f, 64f ), Kv( ("_light", "255 255 255 50"), ("_distance", "256") ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			Require( setup.Lights.Count == 1 && setup.Lights[0].Kind == BakeLightKind.Dynamic, "light_dynamic must resolve as Dynamic" );
			Require( Near( setup.Lights[0].Range, 256f ), $"dynamic range: {setup.Lights[0].Range}" );
			var live = setup.ToLiveLights();
			Require( live.Count == 1 && live[0].Kind == LiveLightKind.Point, "dynamic light reaches the preview as a point" );
			Require( Near( live[0].Intensity, 50f ), $"preview intensity: {live[0].Intensity}" );
			var conversion = setup.ToCompileLights( Vec3.Zero );
			Require( conversion.Lights.Count == 0, $"bake must take no lights from a dynamic-only map, got {conversion.Lights.Count}" );
			Require( conversion.DroppedDynamic == 1, $"DroppedDynamic: {conversion.DroppedDynamic}" );
			Require( conversion.Notes.Count == 1 && conversion.Notes[0].Contains( "realtime" ), $"drop note: {string.Join( "; ", conversion.Notes )}" );
		} );

		Check( "fog resolves and FogFactor is linear", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "env_fog_controller", Vec3.Zero,
				Kv( ("fogcolor", "180 200 220"), ("fogstart", "512"), ("fogend", "4096"), ("fogmaxdensity", "0.8"), ("fogenable", "1") ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			Require( setup.Fog is not null, "fog controller must resolve" );
			Require( setup.Fog!.Enabled, "fogenable 1 must enable fog" );
			Require( Near( setup.Fog.Start, 512f ) && Near( setup.Fog.End, 4096f ) && Near( setup.Fog.MaxDensity, 0.8f ),
				$"fog range: {setup.Fog.Start}..{setup.Fog.End} density {setup.Fog.MaxDensity}" );
			Require( Near( setup.Fog.Color, new Vec3( 180f / 255f, 200f / 255f, 220f / 255f ) ), $"fog colour: {setup.Fog.Color}" );
			Require( Near( setup.Fog.FogFactor( 0f ), 0f ), "fog factor before start is 0" );
			Require( Near( setup.Fog.FogFactor( 512f ), 0f ), "fog factor at start is 0" );
			Require( Near( setup.Fog.FogFactor( 2304f ), 0.4f ), $"fog factor midway: {setup.Fog.FogFactor( 2304f )}" );
			Require( Near( setup.Fog.FogFactor( 4096f ), 0.8f ), "fog factor at end is max density" );
			Require( Near( setup.Fog.FogFactor( 9999f ), 0.8f ), "fog factor past end stays at max density" );
			Require( setup.Issues.Count == 0, $"clean fog must not warn: {string.Join( "; ", setup.Issues.Select( i => i.ToString() ) )}" );

			var off = new MapEntities( MergedRegistry() );
			Place( off, "env_fog_controller", Vec3.Zero, Kv( ("fogenable", "0"), ("fogstart", "0"), ("fogend", "1000") ) );
			Require( !LightEnvironment.FromEntities( off.Entities ).Fog!.Enabled, "fogenable 0 must disable fog" );
			Require( Near( LightEnvironment.FromEntities( off.Entities ).Fog!.FogFactor( 500f ), 0f ), "disabled fog factor is 0" );

			var bad = new MapEntities( MergedRegistry() );
			Place( bad, "env_fog_controller", Vec3.Zero, Kv( ("fogstart", "1000"), ("fogend", "500"), ("fogmaxdensity", "1.5") ) );
			var badSetup = LightEnvironment.FromEntities( bad.Entities );
			Require( badSetup.HasIssue( LightEnvironment.CodeFogEndNotAfterStart ), "fogend <= fogstart must warn" );
			Require( badSetup.HasIssue( LightEnvironment.CodeFogDensityOutOfRange ), "fogmaxdensity 1.5 must warn" );
			Require( Near( badSetup.Fog!.FogFactor( 750f ), 0f ), "fog with no visible range has factor 0" );
		} );

		Check( "shadow and tonemap resolve and validate", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "shadow_control", Vec3.Zero, Kv( ("color", "32 32 32"), ("distance", "2048"), ("disableallshadows", "1") ) );
			Place( map, "env_tonemap_controller", Vec3.Zero,
				Kv( ("bloom_scale", "1.5"), ("autoexposure_min", "0.5"), ("autoexposure_max", "2"),
					("tonemap_rate", "1"), ("tonemap_percent_bright_pixels", "2"),
					("tonemap_min_avglum", "3"), ("tonemap_percent_target", "60") ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			Require( setup.Shadow is not null && setup.Tonemap is not null, "shadow + tonemap must resolve" );
			Require( Near( setup.Shadow!.Color, new Vec3( 32f / 255f, 32f / 255f, 32f / 255f ) ), $"shadow colour: {setup.Shadow.Color}" );
			Require( Near( setup.Shadow.Distance, 2048f ) && setup.Shadow.DisableAllShadows, "shadow distance / disable flag" );
			Require( Near( setup.Tonemap!.BloomScale, 1.5f ), $"bloom scale: {setup.Tonemap.BloomScale}" );
			Require( Near( setup.Tonemap.ClampExposure( 0.1f ), 0.5f ), "exposure clamps to min" );
			Require( Near( setup.Tonemap.ClampExposure( 5f ), 2f ), "exposure clamps to max" );
			Require( Near( setup.Tonemap.ClampExposure( 1f ), 1f ), "exposure inside the range is unchanged" );
			Require( setup.Issues.Count == 0, $"clean shadow/tonemap must not warn: {string.Join( "; ", setup.Issues.Select( i => i.ToString() ) )}" );

			var bad = new MapEntities( MergedRegistry() );
			Place( bad, "env_tonemap_controller", Vec3.Zero,
				Kv( ("autoexposure_min", "4"), ("autoexposure_max", "2"), ("tonemap_percent_target", "150") ) );
			var badSetup = LightEnvironment.FromEntities( bad.Entities );
			Require( badSetup.HasIssue( LightEnvironment.CodeTonemapExposureRangeInvalid ), "min > max exposure must warn" );
			Require( badSetup.HasIssue( LightEnvironment.CodeTonemapPercentOutOfRange ), "percent 150 must warn" );
			Require( Near( badSetup.Tonemap!.ClampExposure( 3f ), 3f ), "ClampExposure with an invalid range returns the exposure unchanged" );
		} );

		Check( "non-lighting entities are ignored, null input throws", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "info_player_start", new Vec3( 0f, 0f, 32f ) );
			Place( map, "light", new Vec3( 0f, 0f, 96f ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			Require( setup.Lights.Count == 1, $"non-lighting entities must be ignored, got {setup.Lights.Count} lights" );
			Require( setup.Issues.Count == 0, "ignored entities must not warn" );
			var empty = LightEnvironment.FromEntities( new MapEntities( MergedRegistry() ).Entities );
			Require( empty.Lights.Count == 0 && empty.Sun is null && empty.Fog is null, "an empty map resolves an empty setup" );
			var threw = false;
			try { LightEnvironment.FromEntities( null! ); }
			catch ( ArgumentNullException ) { threw = true; }
			Require( threw, "null entities must throw ArgumentNullException" );
		} );

		Check( "light style flicker warns", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "light", Vec3.Zero, Kv( ("style", "10") ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			Require( setup.HasIssue( LightEnvironment.CodeLightStyleIgnored ),
				$"style 10 must warn: {string.Join( "; ", setup.Issues.Select( i => i.ToString() ) )}" );
			Require( setup.Lights.Count == 1, "a flickering light still resolves (steady value)" );
		} );

		Check( "sun lights the preview floor analytically", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "light_environment", Vec3.Zero,
				Kv( ("_light", "255 255 255 2"), ("_ambient", "51 51 51 1") ), new Vec3( -90f, 0f, 0f ) );
			Place( map, "env_sun", Vec3.Zero, null, new Vec3( -90f, 0f, 0f ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			Require( setup.Issues.Count == 0, $"paired sun must not warn: {string.Join( "; ", setup.Issues.Select( i => i.ToString() ) )}" );
			var faces = BoxFaces( 0, new Vec3( 0f, 0f, 0f ), new Vec3( 256f, 256f, 128f ) ).ToArray();
			var result = LiveLighting.Evaluate( faces, setup.ToLiveLights(), LiveLightingQuality.Draft );
			var floor = result.Face( 0 )!;
			var ceiling = result.Face( 1 )!;
			Require( Near( floor.Average, new Vec3( 2.2f, 2.2f, 2.2f ), 1e-3f ),
				$"floor gets sun 2.0 + ambient 0.2: {floor.Average}" );
			Require( Near( ceiling.Average, new Vec3( 0.2f, 0.2f, 0.2f ), 1e-3f ),
				$"ceiling faces away from the sun and gets ambient only: {ceiling.Average}" );
			Require( result.Warnings.Count == 0, $"paired preview must not warn: {string.Join( "; ", result.Warnings )}" );
		} );

		Check( "sun-room fixture resolves cleanly", () =>
		{
			var setup = SetupFromSunRoom();
			Require( setup.Lights.Count == 2, $"sun-room has sun + ambient = 2 lights, got {setup.Lights.Count}" );
			Require( setup.Sun is not null && setup.Environment is not null && setup.HasEnvSun, "sun-room must have sun + ambient + env_sun" );
			Require( setup.Fog is not null && setup.Shadow is not null && setup.Tonemap is not null,
				"sun-room must have fog + shadow + tonemap" );
			Require( Near( setup.Fog!.Start, 512f ) && Near( setup.Fog.End, 4096f ), "sun-room fog range" );
			Require( setup.Issues.Count == 0, $"sun-room must resolve clean: {string.Join( "; ", setup.Issues.Select( i => i.ToString() ) )}" );
		} );

		Check( "one sun bakes a lit non-black lightmap through CompileRunner", () =>
		{
			var (brushes, playerStart, specs) = LoadSunRoom();
			var map = new MapEntities( MergedRegistry() );
			foreach ( var (cls, origin, angles, kv) in specs )
				Place( map, cls, origin, kv, angles );
			var setup = LightEnvironment.FromEntities( map.Entities );
			var conversion = setup.ToCompileLights( new Vec3( 0f, 0f, 64f ) );
			Require( conversion.SunApproximations == 1, $"sun approximations: {conversion.SunApproximations}" );
			Require( conversion.DroppedAmbient == 1, $"dropped ambient: {conversion.DroppedAmbient}" );
			Require( conversion.SpotConeLosses == 0 && conversion.DroppedDynamic == 0, "sun-room has no spots or dynamic lights" );
			var input = new CompileInput( "sun-room" ) { PlayerStart = playerStart };
			input.Brushes.AddRange( brushes );
			input.Lights.AddRange( conversion.Lights );
			var result = CompileRunner.Run( input, CompilePreset.Final,
				new CompileOptions { OutputFolder = WorkDir( "sun-room" ) } );
			Require( result.Lightmaps is not null, "Final must bake lightmaps" );
			Require( result.CompletedStages.Contains( CompileStage.LightmapBake ), "LightmapBake stage must complete" );
			Require( result.Lightmaps!.Mean > 0f, $"one sun must bake a lit (non-black) lightmap, mean is {result.Lightmaps.Mean}" );
			var roof = result.Lightmaps.Faces.FirstOrDefault( f => f.BrushId == 1 && f.FaceIndex == 4 );
			Require( roof is not null, "ceiling brush must have a +Z (top) lightmap face" );
			Require( roof!.Mean > 0.9f, $"roof top face must bake lit, mean is {roof.Mean}" );
			var floor = result.Lightmaps.Faces.FirstOrDefault( f => f.BrushId == 0 && f.FaceIndex == 4 );
			Require( floor is not null, "floor brush must have a +Z (top) lightmap face" );
			Require( floor!.Mean == 0f, $"interior floor must bake black (shadowed by the ceiling), mean is {floor.Mean}" );
		} );

		Check( "point light bakes a lit lightmap through CompileRunner", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "light", new Vec3( 0f, 0f, 96f ), Kv( ("_light", "255 255 255 200") ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			var conversion = setup.ToCompileLights( new Vec3( 0f, 0f, 64f ) );
			Require( conversion.Lights.Count == 1 && conversion.SunApproximations == 0,
				"a point light maps to exactly one CompileLight with no approximation" );
			var input = new CompileInput( "point-room" ) { PlayerStart = new Vec3( 0f, 0f, 32f ) };
			input.Brushes.AddRange( RoomBrushes() );
			input.Lights.AddRange( conversion.Lights );
			var result = CompileRunner.Run( input, CompilePreset.Final,
				new CompileOptions { OutputFolder = WorkDir( "point-room" ) } );
			Require( result.Lightmaps is not null && result.Lightmaps.Mean > 0f,
				$"point light must bake a lit lightmap, mean is {result.Lightmaps?.Mean}" );
			var floor = result.Lightmaps!.Faces.FirstOrDefault( f => f.BrushId == 0 && f.FaceIndex == 4 );
			Require( floor is not null && floor.Mean > 0f, $"interior floor must be lit by the point light, mean is {floor?.Mean}" );
		} );

		Check( "sun approximation math is exact", () =>
		{
			var sun = BakeLight.Sun( 7, new Vec3( 0f, 0f, -1f ), Vec3.One, 2f );
			var compiled = sun.ToCompileLight( new Vec3( 0f, 0f, 64f ) );
			Require( compiled is not null, "a sun must produce a CompileLight through the approximation" );
			Require( compiled!.Position.Equals( new Vec3( 0f, 0f, 8256f ) ),
				$"sun CompileLight position: {compiled.Position} (map centre minus travel * 8192)" );
			Require( Near( compiled.Intensity, 2f * 4097f ), $"sun CompileLight intensity: {compiled.Intensity} (2 * (1 + (8192/128)^2))" );
			Require( Near( compiled.Range, 0f ), "sun CompileLight has no range cutoff" );
			Require( compiled.Color.Equals( Vec3.One ), $"sun CompileLight colour: {compiled.Color}" );
			// The compensation is exact: the runner's attenuator divides it straight back.
			var atten = compiled.Intensity / ( 1f + ( 8192f / 128f ) * ( 8192f / 128f ) );
			Require( Near( atten, 2f ), $"compensated attenuator returns the sun intensity: {atten}" );
			// A spot maps to a point at its position (cone dropped); ambient/dynamic map to nothing.
			var spot = BakeLight.Spot( 8, new Vec3( 1f, 2f, 3f ), new Vec3( 0f, 0f, -1f ), Vec3.One, 5f, 10f, 30f, 100f );
			var spotCompiled = spot.ToCompileLight( Vec3.Zero );
			Require( spotCompiled is not null && spotCompiled.Position.Equals( new Vec3( 1f, 2f, 3f ) )
				&& Near( spotCompiled.Intensity, 5f ) && Near( spotCompiled.Range, 100f ),
				"a spot maps to a point CompileLight at its position" );
			Require( BakeLight.Environment( 9, Vec3.One, 1f ).ToCompileLight( Vec3.Zero ) is null,
				"ambient maps to no CompileLight" );
			Require( BakeLight.Dynamic( 10, Vec3.Zero, Vec3.One, 1f ).ToCompileLight( Vec3.Zero ) is null,
				"a dynamic light maps to no CompileLight" );
		} );

		Check( "BakeLight rejects invalid construction", () =>
		{
			var threw = false;
			try { _ = BakeLight.Point( 0, Vec3.Zero, Vec3.One, -1f ); }
			catch ( ArgumentException ex ) { threw = ex.Message.Contains( "intensity" ); }
			Require( threw, "negative intensity must throw naming intensity" );
			threw = false;
			try { _ = BakeLight.Spot( 0, Vec3.Zero, new Vec3( 0f, 0f, -1f ), Vec3.One, 1f, 30f, 10f ); }
			catch ( ArgumentException ex ) { threw = ex.Message.Contains( "inner cone" ); }
			Require( threw, "a spot with inner >= outer cone must throw" );
			threw = false;
			try { _ = BakeLight.Sun( 0, Vec3.Zero, Vec3.One, 1f ); }
			catch ( ArgumentException ex ) { threw = ex.Message.Contains( "direction" ); }
			Require( threw, "a sun with a zero direction must throw" );
			threw = false;
			try { _ = new BakeLight( BakeLightKind.Point, 0, "", Vec3.Zero, Vec3.Zero, Vec3.One, 1f ); }
			catch ( ArgumentException ) { threw = true; }
			Require( threw, "an empty class name must throw" );
		} );

		Check( "lighting setup applies to a CompileInput and bakes lit", () =>
		{
			var map = new MapEntities( MergedRegistry() );
			Place( map, "light_environment", Vec3.Zero,
				Kv( ("_light", "255 255 255 200"), ("_ambient", "51 51 51 1") ), new Vec3( -90f, 0f, 0f ) );
			Place( map, "env_sun", Vec3.Zero, null, new Vec3( -90f, 0f, 0f ) );
			var setup = LightEnvironment.FromEntities( map.Entities );
			var input = new CompileInput( "apply-room" ) { PlayerStart = new Vec3( 0f, 0f, 32f ) };
			input.Brushes.AddRange( RoomBrushes() );
			var conversion = setup.ApplyToCompileInput( input, new Vec3( 0f, 0f, 64f ) );
			Require( conversion.SunApproximations == 1 && conversion.DroppedAmbient == 1,
				$"apply conversion: suns {conversion.SunApproximations}, dropped ambient {conversion.DroppedAmbient}" );
			Require( input.Lights.Count == 1, $"applied input must hold 1 bake light, got {input.Lights.Count}" );
			var result = CompileRunner.Run( input, CompilePreset.Final,
				new CompileOptions { OutputFolder = WorkDir( "apply-room" ) } );
			Require( result.Lightmaps is not null && result.Lightmaps.Mean > 0f,
				$"applied sun must bake a lit lightmap, mean is {result.Lightmaps?.Mean}" );
			var threw = false;
			try { setup.ApplyToCompileInput( null!, Vec3.Zero ); }
			catch ( ArgumentNullException ) { threw = true; }
			Require( threw, "null input must throw ArgumentNullException" );
		} );
	}
}
