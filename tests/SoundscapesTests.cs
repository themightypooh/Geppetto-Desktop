using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

/// <summary>
/// Tests for soundscapes + env_soundscape zones + particle preview data
/// (hammer-soundscapes-particle-preview-backend): soundscape registry
/// load/validation, zone + ambient_generic + info_particle_system
/// round-trips through MapEntity, registry validation with named
/// issues, and preview data (active soundscape at a point, particle
/// evaluation at time t). Backend only — no UI, no audio playback.
/// </summary>
internal static class SoundscapesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "soundscapes", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void RequireCode( IReadOnlyList<SoundscapeIssue> issues, string code, string what )
		{
			Require( issues.Any( i => i.Code == code ), $"{what}: expected an issue with code '{code}', got [{string.Join( "; ", issues.Select( i => i.ToString() ) )}]" );
		}

		string RepoRoot()
		{
			string dir = AppContext.BaseDirectory;
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

		string Fixture( string name ) => Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-soundscapes-particle-preview-backend", name );
		string CorePath() => Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" );

		EntityRegistry LoadExtendedRegistry()
		{
			var reg = EntityRegistry.Load( CorePath() );
			reg.LoadAdditional( Fixture( "hammer-soundscapes.json" ) );
			return reg;
		}

		SoundscapeRegistry LoadFixtureSoundscapes() => SoundscapeRegistry.Load( Fixture( "soundscapes.json" ) );

		SoundscapeResolvers TestResolvers() => SoundscapeResolvers.FromSets(
			new[] { "ambient/wind_loop.wav", "ambient/birds_chirp.wav", "ambient/water_drip.wav", "ambient/alarm.wav" },
			new[] { "sparks_burst", "smoke_plume" } );

		// ---- Soundscape registry ----

		Check( "soundscape fixture loads with 3 definitions", () =>
		{
			var reg = LoadFixtureSoundscapes();
			Require( reg.Name == "hammer-soundscapes-test", $"registry name is '{reg.Name}', expected 'hammer-soundscapes-test'" );
			Require( reg.SchemaVersion == 1, $"schema version is {reg.SchemaVersion}, expected 1" );
			Require( reg.Count == 3, $"fixture has {reg.Count} soundscapes, expected 3" );
			var outdoor = reg.Find( "outdoor.ambient" );
			Require( outdoor is not null, "outdoor.ambient not found" );
			Require( outdoor.Sounds.Count == 2, $"outdoor.ambient has {outdoor.Sounds.Count} sounds, expected 2" );
			Require( outdoor.Sounds[0].Path == "ambient/wind_loop.wav", $"first sound path is '{outdoor.Sounds[0].Path}'" );
			Require( Math.Abs( outdoor.Sounds[0].Volume - 0.8f ) < 1e-6f, $"wind volume is {outdoor.Sounds[0].Volume}, expected 0.8" );
			Require( outdoor.Sounds[0].Loop, "wind loop must be looping" );
			Require( outdoor.Sounds[1].DelayMin == 5f && outdoor.Sounds[1].DelayMax == 20f, $"bird delay range is {outdoor.Sounds[1].DelayMin}..{outdoor.Sounds[1].DelayMax}, expected 5..20" );
			Require( reg.Find( "cave.drips" )?.Sounds.Count == 1, "cave.drips must have 1 sound" );
			Require( reg.Find( "empty.room" )?.Sounds.Count == 0, "empty.room must have 0 sounds" );
		} );

		Check( "soundscape lookup is case-insensitive", () =>
		{
			var reg = LoadFixtureSoundscapes();
			Require( reg.Find( "OUTDOOR.AMBIENT" )?.Name == "outdoor.ambient", "OUTDOOR.AMBIENT lookup failed" );
			Require( reg.Contains( "Cave.Drips" ), "Cave.Drips Contains failed" );
			Require( reg.TryGet( "EMPTY.ROOM", out var empty ) && empty.Name == "empty.room", "TryGet EMPTY.ROOM failed" );
			Require( reg.Find( "no.such.scape" ) is null, "unknown soundscape Find must return null" );
		} );

		Check( "duplicate soundscape name is rejected with DuplicateSoundscape", () =>
		{
			var reg = new SoundscapeRegistry();
			reg.Add( new SoundscapeDef( "outdoor.ambient" ) );
			try { reg.Add( new SoundscapeDef( "OUTDOOR.AMBIENT" ) ); throw new Exception( "Add did not throw for duplicate soundscape" ); }
			catch ( SoundscapeException ex ) { Require( ex.Code == SoundscapeException.CodeDuplicateSoundscape, $"error code is '{ex.Code}', expected DuplicateSoundscape" ); }
			Require( reg.Count == 1, $"failed Add must not change the registry (count {reg.Count})" );
		} );

		Check( "malformed soundscape JSON is rejected with InvalidSoundscape", () =>
		{
			try { SoundscapeRegistry.LoadFromJson( "{not json", "bad" ); throw new Exception( "malformed JSON did not throw" ); }
			catch ( SoundscapeException ex ) { Require( ex.Code == SoundscapeException.CodeInvalidSoundscape, $"malformed JSON code is '{ex.Code}', expected InvalidSoundscape" ); }
			try { SoundscapeRegistry.LoadFromJson( "{\"schemaVersion\":99,\"soundscapes\":[]}", "bad" ); throw new Exception( "bad schemaVersion did not throw" ); }
			catch ( SoundscapeException ex ) { Require( ex.Code == SoundscapeException.CodeInvalidSoundscape, $"bad schema code is '{ex.Code}', expected InvalidSoundscape" ); }
			try { SoundscapeRegistry.LoadFromJson( "{\"schemaVersion\":1,\"soundscapes\":[{\"name\":\"x\",\"sounds\":[{\"path\":\"a.wav\",\"volume\":1.5}]}]}", "bad" ); throw new Exception( "out-of-range volume did not throw" ); }
			catch ( SoundscapeException ex ) { Require( ex.Code == SoundscapeException.CodeInvalidSoundscape, $"bad volume code is '{ex.Code}', expected InvalidSoundscape" ); }
			try { SoundscapeRegistry.LoadFromJson( "{\"schemaVersion\":1,\"soundscapes\":[{\"name\":\"x\",\"sounds\":[{\"path\":\"a.wav\",\"delayMin\":5,\"delayMax\":2}]}]}", "bad" ); throw new Exception( "inverted delay range did not throw" ); }
			catch ( SoundscapeException ex ) { Require( ex.Code == SoundscapeException.CodeInvalidSoundscape, $"bad delay code is '{ex.Code}', expected InvalidSoundscape" ); }
			try { SoundscapeRegistry.LoadFromJson( "{\"schemaVersion\":1,\"soundscapes\":[{\"name\":\"dup\"},{\"name\":\"DUP\"}]}", "bad" ); throw new Exception( "in-file duplicate did not throw" ); }
			catch ( SoundscapeException ex ) { Require( ex.Code == SoundscapeException.CodeDuplicateSoundscape, $"in-file duplicate code is '{ex.Code}', expected DuplicateSoundscape" ); }
		} );

		// ---- Registry JSON extension ----

		Check( "soundscape registry JSON adds 4 classes without code changes", () =>
		{
			var reg = LoadExtendedRegistry();
			Require( reg.Count == 18, $"extended registry has {reg.Count} classes, expected 18 (14 core + 4)" );
			Require( reg.Get( "env_soundscape" ).Kind == EntityKind.Point, "env_soundscape must be Point" );
			Require( reg.Get( "env_soundscape" ).FindKeyValue( "soundscape" )?.Type == KeyValueType.String, "env_soundscape 'soundscape' must be string" );
			Require( reg.Get( "env_soundscape" ).FindKeyValue( "radius" )?.Type == KeyValueType.Float, "env_soundscape 'radius' must be float" );
			Require( reg.Get( "trigger_soundscape" ).Kind == EntityKind.Brush, "trigger_soundscape must be Brush" );
			Require( reg.Get( "trigger_soundscape" ).FindKeyValue( "soundscape" )?.Type == KeyValueType.String, "trigger_soundscape 'soundscape' must be string" );
			Require( reg.Get( "ambient_generic" ).Kind == EntityKind.Point, "ambient_generic must be Point" );
			Require( reg.Get( "ambient_generic" ).FindKeyValue( "message" )?.Type == KeyValueType.Sound, "ambient_generic 'message' must be sound" );
			Require( reg.Get( "ambient_generic" ).FindKeyValue( "health" )?.Type == KeyValueType.Int, "ambient_generic 'health' must be int" );
			Require( reg.Get( "info_particle_system" ).Kind == EntityKind.Point, "info_particle_system must be Point" );
			Require( reg.Get( "info_particle_system" ).FindKeyValue( "effect_name" )?.Type == KeyValueType.String, "info_particle_system 'effect_name' must be string" );
			Require( reg.Get( "info_particle_system" ).FindKeyValue( "cpoint1" )?.Type == KeyValueType.Target, "info_particle_system 'cpoint1' must be target" );
			Require( reg.Get( "info_particle_system" ).FindKeyValue( "emissionrate" )?.Type == KeyValueType.Float, "info_particle_system 'emissionrate' must be float" );
			Require( reg.Contains( "func_door" ) && reg.Contains( "light" ), "core classes must survive LoadAdditional" );
		} );

		// ---- Round-trips through MapEntity ----

		Check( "soundscape zone round-trips through a placed env_soundscape", () =>
		{
			var entities = new MapEntities( LoadExtendedRegistry() );
			var zone = new SoundscapeZone( "outdoor.ambient", new Vec3( 100, 200, 300 ), 256f, "zone_outdoor" );
			var placed = entities.PlacePointEntity( SoundscapeZone.ClassName, zone.Origin, zone.ToKeyvalues() );
			Require( placed.ClassName == "env_soundscape", $"placed class is '{placed.ClassName}'" );
			Require( placed.Origin.AlmostEquals( new Vec3( 100, 200, 300 ) ), $"placed origin is {placed.Origin}" );
			var back = SoundscapeZone.FromEntity( placed );
			Require( back.SoundscapeName == "outdoor.ambient", $"round-tripped soundscape is '{back.SoundscapeName}'" );
			Require( back.Radius == 256f, $"round-tripped radius is {back.Radius}, expected 256" );
			Require( back.Origin.AlmostEquals( new Vec3( 100, 200, 300 ) ), $"round-tripped origin is {back.Origin}" );
			Require( back.TargetName == "zone_outdoor", $"round-tripped targetname is '{back.TargetName}'" );
			Require( !back.HasBounds, "env_soundscape round-trip must not invent bounds" );
		} );

		Check( "trigger_soundscape zone round-trips without a radius key", () =>
		{
			var entities = new MapEntities( LoadExtendedRegistry() );
			var zone = new SoundscapeZone( "cave.drips", new Vec3( 0, 0, 0 ), 0f, "zone_cave" );
			var placed = entities.PlaceBrushEntity( SoundscapeZone.TriggerClassName, new[] { "brush-a" }, zone.ToTriggerKeyvalues() );
			Require( placed.ClassName == "trigger_soundscape", $"placed class is '{placed.ClassName}'" );
			Require( !placed.Keyvalues.ContainsKey( "radius" ), "trigger_soundscape must not carry a radius keyvalue" );
			var back = SoundscapeZone.FromEntity( placed );
			Require( back.SoundscapeName == "cave.drips", $"round-tripped soundscape is '{back.SoundscapeName}'" );
			Require( back.Radius == 0f, $"trigger round-trip radius is {back.Radius}, expected 0" );
			Require( !back.HasBounds, "trigger round-trip documents that bounds live on the tied brushes" );
		} );

		Check( "ambient_generic round-trips with Source health quantisation", () =>
		{
			var entities = new MapEntities( LoadExtendedRegistry() );
			var ambient = new AmbientGenericRef( "ambient/alarm.wav", 0.85f, 120f, 512f, "alarm_01" ) { StartSilent = true, Loop = true };
			var placed = entities.PlacePointEntity( AmbientGenericRef.ClassName, new Vec3( 10, 20, 30 ), ambient.ToKeyvalues() );
			Require( placed.Keyvalues["health"] == "9", $"health keyvalue is '{placed.Keyvalues["health"]}', expected '9' (round(0.85 * 10))" );
			Require( placed.Keyvalues["message"] == "ambient/alarm.wav", $"message keyvalue is '{placed.Keyvalues["message"]}'" );
			var back = AmbientGenericRef.FromEntity( placed );
			Require( back.SoundPath == "ambient/alarm.wav", $"round-tripped path is '{back.SoundPath}'" );
			Require( back.Volume == 0.9f, $"round-tripped volume is {back.Volume}, expected 0.9 (Source health granularity is 0.1)" );
			Require( back.Pitch == 120f, $"round-tripped pitch is {back.Pitch}, expected 120" );
			Require( back.Radius == 512f, $"round-tripped radius is {back.Radius}, expected 512" );
			Require( back.StartSilent && back.Loop, "round-tripped startsilent/loop must survive" );
			Require( AmbientGenericRef.VolumeToHealth( 0f ) == 0 && AmbientGenericRef.VolumeToHealth( 1f ) == 10, "volume/health endpoints must be 0 and 10" );
		} );

		Check( "particle reference round-trips with control points and params", () =>
		{
			var entities = new MapEntities( LoadExtendedRegistry() );
			var particle = new ParticleSystemRef( "smoke_plume", new Vec3( 5, 6, 7 ), true, "smoke_01" );
			particle.AddControlPoint( "target_a" );
			particle.SetParam( "emissionrate", "10" );
			particle.SetParam( "lifetime", "2" );
			particle.SetParam( "gravity", "0 0 -384" );
			var placed = entities.PlacePointEntity( ParticleSystemRef.ClassName, particle.Origin, particle.ToKeyvalues(), new Vec3( 0, 90, 0 ) );
			Require( placed.Keyvalues["effect_name"] == "smoke_plume", $"effect_name keyvalue is '{placed.Keyvalues["effect_name"]}'" );
			Require( placed.Keyvalues["cpoint1"] == "target_a", $"cpoint1 keyvalue is '{placed.Keyvalues["cpoint1"]}'" );
			var back = ParticleSystemRef.FromEntity( placed );
			Require( back.EffectName == "smoke_plume", $"round-tripped effect is '{back.EffectName}'" );
			Require( back.StartActive, "round-tripped start_active must be true" );
			Require( back.ControlPoints.Count == 1 && back.ControlPoints[0] == "target_a", "round-tripped control points are wrong" );
			Require( back.Params["emissionrate"] == "10" && back.Params["lifetime"] == "2", "round-tripped params are wrong" );
			Require( back.Params["gravity"] == "0 0 -384", $"round-tripped gravity is '{back.Params["gravity"]}'" );
			Require( back.Angles.AlmostEquals( new Vec3( 0, 90, 0 ) ), $"round-tripped angles are {back.Angles}" );
		} );

		Check( "reading a zone from the wrong class throws InvalidSoundscape", () =>
		{
			var entities = new MapEntities( LoadExtendedRegistry() );
			var light = entities.PlacePointEntity( "light", new Vec3( 0, 0, 0 ), new Dictionary<string, string> { { "_light", "255 255 255" } } );
			try { SoundscapeZone.FromEntity( light ); throw new Exception( "FromEntity did not throw for a light" ); }
			catch ( SoundscapeException ex ) { Require( ex.Code == SoundscapeException.CodeInvalidSoundscape, $"error code is '{ex.Code}', expected InvalidSoundscape" ); }
			try { AmbientGenericRef.FromEntity( light ); throw new Exception( "Ambient FromEntity did not throw for a light" ); }
			catch ( SoundscapeException ex ) { Require( ex.Code == SoundscapeException.CodeInvalidSoundscape, $"ambient error code is '{ex.Code}', expected InvalidSoundscape" ); }
			try { ParticleSystemRef.FromEntity( light ); throw new Exception( "Particle FromEntity did not throw for a light" ); }
			catch ( SoundscapeException ex ) { Require( ex.Code == SoundscapeException.CodeInvalidSoundscape, $"particle error code is '{ex.Code}', expected InvalidSoundscape" ); }
		} );

		// ---- Zone containment ----

		Check( "zone containment covers radius, bounds and global forms", () =>
		{
			var radius = new SoundscapeZone( "outdoor.ambient", new Vec3( 0, 0, 0 ), 100f );
			Require( radius.Contains( new Vec3( 0, 0, 0 ) ), "radius zone must contain its origin" );
			Require( radius.Contains( new Vec3( 100, 0, 0 ) ), "radius zone must contain its boundary (inclusive)" );
			Require( !radius.Contains( new Vec3( 100.01f, 0, 0 ) ), "radius zone must not contain a point past its radius" );
			var bounds = new SoundscapeZone( "cave.drips", new Vec3( 0, 0, 0 ) ) { HasBounds = true, Min = new Vec3( -10, -10, 0 ), Max = new Vec3( 10, 10, 64 ) };
			Require( bounds.Contains( new Vec3( 10, 10, 64 ) ), "bounds zone must contain its max corner (inclusive)" );
			Require( !bounds.Contains( new Vec3( 0, 0, 65 ) ), "bounds zone must not contain a point above its max Z" );
			var global = new SoundscapeZone( "outdoor.ambient", new Vec3( 0, 0, 0 ), 0f );
			Require( global.Contains( new Vec3( 9999, -9999, 5000 ) ), "global zone (radius 0) must contain any point" );
		} );

		// ---- Validation ----

		Check( "valid map validates clean", () =>
		{
			var entities = new MapEntities( LoadExtendedRegistry() );
			entities.PlacePointEntity( SoundscapeZone.ClassName, new Vec3( 0, 0, 0 ),
				new SoundscapeZone( "outdoor.ambient", new Vec3( 0, 0, 0 ), 512f, "zone_outdoor" ).ToKeyvalues() );
			entities.PlacePointEntity( AmbientGenericRef.ClassName, new Vec3( 10, 0, 0 ),
				new AmbientGenericRef( "ambient/alarm.wav", 1f, 100f, 0f, "alarm_01" ).ToKeyvalues() );
			entities.PlacePointEntity( ParticleSystemRef.ClassName, new Vec3( 20, 0, 0 ),
				new ParticleSystemRef( "sparks_burst", new Vec3( 20, 0, 0 ) ).ToKeyvalues() );
			entities.PlacePointEntity( "info_player_start", new Vec3( 0, 0, 0 ) );
			var issues = SoundscapeValidator.ValidateMap( entities, LoadFixtureSoundscapes(), TestResolvers(), TestResolvers() );
			Require( issues.Count == 0, $"valid map rejected: {string.Join( "; ", issues.Select( i => i.ToString() ) )}" );
		} );

		Check( "missing soundscape is a MissingSoundscape issue with the entity id", () =>
		{
			var entities = new MapEntities( LoadExtendedRegistry() );
			var placed = entities.PlacePointEntity( SoundscapeZone.ClassName, new Vec3( 0, 0, 0 ),
				new SoundscapeZone( "no.such.scape", new Vec3( 0, 0, 0 ), 100f, "zone_ghost" ).ToKeyvalues() );
			var issues = SoundscapeValidator.ValidateMap( entities, LoadFixtureSoundscapes(), TestResolvers(), TestResolvers() );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}: {string.Join( "; ", issues.Select( i => i.ToString() ) )}" );
			RequireCode( issues, SoundscapeIssue.CodeMissingSoundscape, "missing soundscape" );
			Require( issues[0].EntityId == placed.Id, $"issue entity id is {issues[0].EntityId}, expected {placed.Id}" );
			Require( issues[0].Severity == SoundscapeIssueSeverity.Error, "missing soundscape must be an Error" );
		} );

		Check( "missing particle definition is a MissingParticle issue", () =>
		{
			var entities = new MapEntities( LoadExtendedRegistry() );
			var placed = entities.PlacePointEntity( ParticleSystemRef.ClassName, new Vec3( 0, 0, 0 ),
				new ParticleSystemRef( "ghost_fx", new Vec3( 0, 0, 0 ) ).ToKeyvalues() );
			var issues = SoundscapeValidator.ValidateMap( entities, LoadFixtureSoundscapes(), TestResolvers(), TestResolvers() );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}" );
			RequireCode( issues, SoundscapeIssue.CodeMissingParticle, "missing particle" );
			Require( issues[0].EntityId == placed.Id, $"issue entity id is {issues[0].EntityId}, expected {placed.Id}" );
			Require( issues[0].Key == "effect_name", $"issue key is '{issues[0].Key}', expected 'effect_name'" );
		} );

		Check( "missing sound in a soundscape definition and in ambient_generic is MissingSound", () =>
		{
			var broken = new SoundscapeDef( "broken.refs", new[] { new SoundscapeSound( "ambient/missing.wav", 1f ) } );
			var issues = SoundscapeValidator.ValidateSoundscapeDef( broken, TestResolvers() );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}" );
			RequireCode( issues, SoundscapeIssue.CodeMissingSound, "missing sound in definition" );
			Require( issues[0].Key == "ambient/missing.wav", $"issue key is '{issues[0].Key}', expected the sound path" );
			var ambientIssues = SoundscapeValidator.ValidateAmbient( new AmbientGenericRef( "ambient/missing.wav" ), TestResolvers() );
			Require( ambientIssues.Count == 1, $"expected 1 ambient issue, got {ambientIssues.Count}" );
			RequireCode( ambientIssues, SoundscapeIssue.CodeMissingSound, "missing sound in ambient_generic" );
		} );

		Check( "empty names are named Missing*Name issues", () =>
		{
			RequireCode( SoundscapeValidator.ValidateZone( new SoundscapeZone( "", new Vec3( 0, 0, 0 ) ), LoadFixtureSoundscapes(), TestResolvers() ),
				SoundscapeIssue.CodeMissingSoundscapeName, "empty soundscape name" );
			RequireCode( SoundscapeValidator.ValidateParticle( new ParticleSystemRef( "", new Vec3( 0, 0, 0 ) ), TestResolvers() ),
				SoundscapeIssue.CodeMissingParticleName, "empty particle name" );
			RequireCode( SoundscapeValidator.ValidateAmbient( new AmbientGenericRef( "" ), TestResolvers() ),
				SoundscapeIssue.CodeMissingSoundPath, "empty ambient sound path" );
		} );

		Check( "out-of-range values are named Invalid* issues", () =>
		{
			RequireCode( SoundscapeValidator.ValidateZone( new SoundscapeZone( "outdoor.ambient", new Vec3( 0, 0, 0 ), -5f ), LoadFixtureSoundscapes(), TestResolvers() ),
				SoundscapeIssue.CodeInvalidRadius, "negative zone radius" );
			var badBounds = new SoundscapeZone( "outdoor.ambient", new Vec3( 0, 0, 0 ) ) { HasBounds = true, Min = new Vec3( 10, 0, 0 ), Max = new Vec3( 0, 0, 0 ) };
			RequireCode( SoundscapeValidator.ValidateZone( badBounds, LoadFixtureSoundscapes(), TestResolvers() ),
				SoundscapeIssue.CodeInvalidBounds, "inverted bounds" );
			RequireCode( SoundscapeValidator.ValidateAmbient( new AmbientGenericRef( "ambient/alarm.wav", 1.5f ), TestResolvers() ),
				SoundscapeIssue.CodeInvalidVolume, "ambient volume > 1" );
			RequireCode( SoundscapeValidator.ValidateAmbient( new AmbientGenericRef( "ambient/alarm.wav", 1f, 300f ), TestResolvers() ),
				SoundscapeIssue.CodeInvalidPitch, "ambient pitch > 255" );
			RequireCode( SoundscapeValidator.ValidateAmbient( new AmbientGenericRef( "ambient/alarm.wav", 1f, 100f, -10f ), TestResolvers() ),
				SoundscapeIssue.CodeInvalidRadius, "ambient negative radius" );
			var badDelay = new SoundscapeDef( "bad.delays", new[] { new SoundscapeSound( "ambient/alarm.wav", 1f, 100f, false, 5f, 2f ) } );
			RequireCode( SoundscapeValidator.ValidateSoundscapeDef( badDelay, TestResolvers() ),
				SoundscapeIssue.CodeInvalidDelay, "inverted delay range" );
		} );

		Check( "null resolvers skip existence checks but not range checks", () =>
		{
			var zone = new SoundscapeZone( "no.such.scape", new Vec3( 0, 0, 0 ), 100f );
			var skipped = SoundscapeValidator.ValidateZone( zone, null, null );
			Require( skipped.Count == 0, $"null resolvers must skip existence checks, got: {string.Join( "; ", skipped.Select( i => i.ToString() ) )}" );
			var badRadius = new SoundscapeZone( "no.such.scape", new Vec3( 0, 0, 0 ), -1f );
			RequireCode( SoundscapeValidator.ValidateZone( badRadius, null, null ), SoundscapeIssue.CodeInvalidRadius, "range check with null resolvers" );
		} );

		// ---- Preview data ----

		Check( "preview resolves the active soundscape with distance attenuation", () =>
		{
			var zones = new[]
			{
				new SoundscapeZone( "outdoor.ambient", new Vec3( 0, 0, 0 ), 100f, "zone_outdoor" ),
				new SoundscapeZone( "cave.drips", new Vec3( 500, 0, 0 ), 100f, "zone_cave" ),
			};
			var result = SoundscapePreview.ResolveActive( new Vec3( 50, 0, 0 ), zones, LoadFixtureSoundscapes() );
			Require( result.Active, $"preview must be active inside zone_outdoor (reason: {result.Reason})" );
			Require( result.Zone?.TargetName == "zone_outdoor", $"active zone is '{result.Zone?.TargetName}', expected 'zone_outdoor'" );
			Require( result.Soundscape?.Name == "outdoor.ambient", $"active soundscape is '{result.Soundscape?.Name}'" );
			Require( result.Sounds.Count == 2, $"active soundscape has {result.Sounds.Count} sounds, expected 2" );
			Require( Math.Abs( result.Attenuation - 0.5f ) < 1e-6f, $"attenuation is {result.Attenuation}, expected 0.5 at half radius" );
			Require( Math.Abs( result.Sounds[0].Volume * result.Attenuation - 0.4f ) < 1e-6f, "effective wind volume must be 0.8 * 0.5 = 0.4" );
		} );

		Check( "preview picks the nearest containing zone and reports no zone outside", () =>
		{
			var zones = new[]
			{
				new SoundscapeZone( "outdoor.ambient", new Vec3( 0, 0, 0 ), 1000f, "zone_wide" ),
				new SoundscapeZone( "cave.drips", new Vec3( 40, 0, 0 ), 100f, "zone_near" ),
			};
			var near = SoundscapePreview.ResolveActive( new Vec3( 50, 0, 0 ), zones, LoadFixtureSoundscapes() );
			Require( near.Active && near.Zone?.TargetName == "zone_near", $"nearest zone must win, got '{near.Zone?.TargetName}'" );
			var outside = SoundscapePreview.ResolveActive( new Vec3( 5000, 0, 0 ), zones, LoadFixtureSoundscapes() );
			Require( !outside.Active && outside.Zone is null && outside.Soundscape is null, "outside every zone the preview must be inactive with no zone/soundscape" );
			Require( outside.Sounds.Count == 0, "inactive preview must expose no sounds" );
			var unknown = SoundscapePreview.ResolveActive( new Vec3( 0, 0, 0 ),
				new[] { new SoundscapeZone( "ghost.scape", new Vec3( 0, 0, 0 ), 100f, "zone_ghost" ) }, LoadFixtureSoundscapes() );
			Require( !unknown.Active && unknown.Zone is not null && unknown.Soundscape is null, "a zone naming an unknown soundscape must not be active" );
			Require( unknown.Reason.Contains( "ghost.scape" ), $"unknown-soundscape reason must name it: '{unknown.Reason}'" );
		} );

		Check( "particle preview evaluates a found system and reports a missing one", () =>
		{
			var resolvers = TestResolvers();
			var particle = new ParticleSystemRef( "sparks_burst", new Vec3( 0, 0, 0 ), true );
			particle.SetParam( "emissionrate", "10" );
			particle.SetParam( "lifetime", "2" );
			var found = ParticlePreview.Preview( particle, resolvers, 1f );
			Require( found.Found, "sparks_burst must be found" );
			Require( found.Issue is null, "found preview must carry no issue" );
			Require( found.Evaluation is not null && found.Evaluation.AliveCount == 11, $"evaluation at t=1 must have 11 alive particles, got {found.Evaluation?.AliveCount}" );
			var atZero = ParticlePreview.Preview( particle, resolvers, 0f );
			Require( atZero.Evaluation is not null && atZero.Evaluation.AliveCount == 1, $"evaluation at t=0 must have 1 alive particle, got {atZero.Evaluation?.AliveCount}" );
			Require( found.Evaluation.Particles[0].Position.AlmostEquals( new Vec3( 0, 0, 0 ), 1e-3f ), $"first particle at t=1 must still be at the origin (speed 0), got {found.Evaluation.Particles[0].Position}" );
			Require( Math.Abs( found.Evaluation.Particles[0].LifeFraction - 0.5f ) < 1e-6f, $"first particle life fraction at t=1 must be 0.5 (age 1 of lifetime 2), got {found.Evaluation.Particles[0].LifeFraction}" );
			var missing = ParticlePreview.Preview( new ParticleSystemRef( "ghost_fx", new Vec3( 0, 0, 0 ) ), resolvers, 1f );
			Require( !missing.Found && missing.Evaluation is null, "missing particle must not evaluate" );
			Require( missing.Issue?.Code == SoundscapeIssue.CodeMissingParticle, $"missing preview issue code is '{missing.Issue?.Code}', expected MissingParticle" );
			var inactive = new ParticleSystemRef( "sparks_burst", new Vec3( 0, 0, 0 ), false );
			inactive.SetParam( "emissionrate", "10" );
			inactive.SetParam( "lifetime", "2" );
			var off = ParticlePreview.Preview( inactive, resolvers, 1f );
			Require( off.Found && off.Evaluation is not null && off.Evaluation.AliveCount == 0, "a start_active=false system must preview as zero particles" );
		} );

		Check( "particle adapter maps angles to direction and defaults to no emission", () =>
		{
			var forward = new ParticleSystemRef( "smoke_plume", new Vec3( 1, 2, 3 ) ) { Angles = new Vec3( 0, 0, 0 ) };
			var dto = forward.ToPreviewParticleSystem();
			Require( dto.Name == "smoke_plume", $"adapter name is '{dto.Name}'" );
			Require( dto.Origin.AlmostEquals( new Vec3( 1, 2, 3 ) ), $"adapter origin is {dto.Origin}" );
			Require( dto.Direction.AlmostEquals( new Vec3( 1, 0, 0 ) ), $"angles (0,0,0) must point +X, got {dto.Direction}" );
			var yaw90 = new ParticleSystemRef( "smoke_plume", new Vec3( 0, 0, 0 ) ) { Angles = new Vec3( 0, 90, 0 ) };
			Require( yaw90.ToPreviewParticleSystem().Direction.AlmostEquals( new Vec3( 0, 1, 0 ) ), $"angles (0,90,0) must point +Y, got {yaw90.ToPreviewParticleSystem().Direction}" );
			var pitchDown = new ParticleSystemRef( "smoke_plume", new Vec3( 0, 0, 0 ) ) { Angles = new Vec3( 90, 0, 0 ) };
			Require( pitchDown.ToPreviewParticleSystem().Direction.AlmostEquals( new Vec3( 0, 0, -1 ) ), $"angles (90,0,0) must point down, got {pitchDown.ToPreviewParticleSystem().Direction}" );
			var noParams = new ParticleSystemRef( "smoke_plume", new Vec3( 0, 0, 0 ) );
			Require( noParams.ToPreviewParticleSystem().EmissionRate == 0f, "a system with no emissionrate param must default to 0, not invent emission" );
			Require( ParticleSystemRef.DirectionFromAngles( new Vec3( 0, 0, 0 ) ).AlmostEquals( new Vec3( 1, 0, 0 ) ), "DirectionFromAngles (0,0,0) must be +X" );
		} );
	}
}
