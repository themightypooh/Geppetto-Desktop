using System;
using System.Collections.Generic;
using System.Linq;
using Marionette.Desktop;

internal static class ParticleDefinitionValidateTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "particle-definition-validate", name, test );
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		bool Has( List<ParticleDefinitionError> errors, string path, ParticleDefinitionSeverity severity )
			=> errors.Any( e => e.FieldPath == path && e.Severity == severity );
		bool HasPath( List<ParticleDefinitionError> errors, string path )
			=> errors.Any( e => e.FieldPath == path );
		void AssertFinding( List<ParticleDefinitionError> errors, string path, ParticleDefinitionSeverity severity )
		{
			if ( !Has( errors, path, severity ) )
				throw new Exception( $"expected a {severity} at '{path}', got: {string.Join( "; ", errors.Select( e => e.Severity + "@" + e.FieldPath + ": " + e.Message ) )}" );
		}
		void AssertNoFinding( List<ParticleDefinitionError> errors, string path )
		{
			if ( HasPath( errors, path ) )
				throw new Exception( $"expected no finding at '{path}', got: {string.Join( "; ", errors.Where( e => e.FieldPath == path ).Select( e => e.Severity + ": " + e.Message ) )}" );
		}
		void Throws( Func<object?> action, string label )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( $"expected an error for {label}, got silent success" );
		}

		ParticleSystemDef ValidDef()
		{
			var def = new ParticleSystemDef
			{
				Settings = new ParticleSettingsDef
				{
					Capacity = 500,
					Seed = 42,
					Drag = 0.4f,
					RenderType = "Billboard",
					StrandPoints = 0,
				},
			};
			def.Emitters.Add( new ParticleEmitterDef
			{
				Shape = "Point",
				Rate = 12,
				LifetimeMin = 3,
				LifetimeMax = 6,
				SizeMin = 1,
				SizeMax = 2,
				Speed = 10,
				SpeedJitter = 0.1f,
			} );
			return def;
		}

		Check( "valid definition yields zero errors and zero warnings", () =>
		{
			var errors = ParticleDefinitionValidate.ValidateDefinition( ValidDef() );
			Assert( errors.Count == 0, $"valid def should yield zero findings, got {errors.Count}: {string.Join( "; ", errors.Select( e => e.Severity + "@" + e.FieldPath ) )}" );
		} );

		Check( "valid preset line yields zero findings (preset bursts count)", () =>
		{
			// sparks is a burst preset: rate 0, step-0 burst of 70. A
			// validator that read raw nullable fields would warn about
			// a zero rate; effective values must not.
			var def = new ParticleSystemDef { Settings = new ParticleSettingsDef { Capacity = 500, Seed = 7 } };
			def.Emitters.Add( new ParticleEmitterDef { Preset = "sparks", Position = new[] { 0f, 0f, 10f } } );
			var errors = ParticleDefinitionValidate.ValidateDefinition( def );
			Assert( errors.Count == 0, $"preset sparks line should yield zero findings, got: {string.Join( "; ", errors.Select( e => e.Severity + "@" + e.FieldPath + ": " + e.Message ) )}" );
		} );

		Check( "capacity zero is an Error at settings.capacity", () =>
		{
			var def = ValidDef();
			def.Settings.Capacity = 0;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.capacity", ParticleDefinitionSeverity.Error );
		} );

		Check( "capacity over 4M is an Error at settings.capacity", () =>
		{
			var def = ValidDef();
			def.Settings.Capacity = 4_000_001;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.capacity", ParticleDefinitionSeverity.Error );
		} );

		Check( "lifetime zero is an Error at emitters[0].lifetimeMin", () =>
		{
			var def = ValidDef();
			def.Emitters[0].LifetimeMin = 0;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].lifetimeMin", ParticleDefinitionSeverity.Error );
		} );

		Check( "inverted lifetime is an Error at emitters[0].lifetimeMax", () =>
		{
			var def = ValidDef();
			def.Emitters[0].LifetimeMin = 6;
			def.Emitters[0].LifetimeMax = 3;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].lifetimeMax", ParticleDefinitionSeverity.Error );
		} );

		Check( "lifetime over one hour is a Warning at emitters[0].lifetimeMax", () =>
		{
			var def = ValidDef();
			def.Emitters[0].LifetimeMin = 4000;
			def.Emitters[0].LifetimeMax = 4000;
			var errors = ParticleDefinitionValidate.ValidateDefinition( def );
			AssertFinding( errors, "emitters[0].lifetimeMax", ParticleDefinitionSeverity.Warning );
			var found = errors.First( e => e.FieldPath == "emitters[0].lifetimeMax" );
			Assert( found.Message.Contains( "240000" ), $"hour-lifetime warning should carry the 60 Hz step count, got: {found.Message}" );
		} );

		Check( "inverted size is an Error at emitters[0].sizeMax", () =>
		{
			var def = ValidDef();
			def.Emitters[0].SizeMin = 2;
			def.Emitters[0].SizeMax = 1;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].sizeMax", ParticleDefinitionSeverity.Error );
		} );

		Check( "negative size is an Error at emitters[0].sizeMin", () =>
		{
			var def = ValidDef();
			def.Emitters[0].SizeMin = -1;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].sizeMin", ParticleDefinitionSeverity.Error );
		} );

		Check( "size zero passes this pack (sim bound stays in lane 5)", () =>
		{
			// Deliberate split, pinned so a future reader does not
			// "fix" it: the brief's range is >= 0, and the sim's > 0
			// bound is lane 5's build gate, not this pack's.
			var def = ValidDef();
			def.Emitters[0].SizeMin = 0;
			def.Emitters[0].SizeMax = 1;
			var errors = ParticleDefinitionValidate.ValidateDefinition( def );
			AssertNoFinding( errors, "emitters[0].sizeMin" );
			AssertNoFinding( errors, "emitters[0].sizeMax" );
		} );

		Check( "negative speed is an Error at emitters[0].speed", () =>
		{
			var def = ValidDef();
			def.Emitters[0].Speed = -5;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].speed", ParticleDefinitionSeverity.Error );
		} );

		Check( "speed jitter over 1 is an Error at emitters[0].speedJitter", () =>
		{
			var def = ValidDef();
			def.Emitters[0].SpeedJitter = 1.5f;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].speedJitter", ParticleDefinitionSeverity.Error );
		} );

		Check( "negative rate is an Error at emitters[0].rate", () =>
		{
			var def = ValidDef();
			def.Emitters[0].Rate = -1;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].rate", ParticleDefinitionSeverity.Error );
		} );

		Check( "negative drag is an Error at settings.drag", () =>
		{
			var def = ValidDef();
			def.Settings.Drag = -0.5f;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.drag", ParticleDefinitionSeverity.Error );
		} );

		Check( "negative turbulence is an Error at settings.turbulenceStrength", () =>
		{
			var def = ValidDef();
			def.Settings.TurbulenceStrength = -1;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.turbulenceStrength", ParticleDefinitionSeverity.Error );
		} );

		Check( "negative vortex strength is an Error at settings.vortexStrength", () =>
		{
			var def = ValidDef();
			def.Settings.VortexStrength = -10;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.vortexStrength", ParticleDefinitionSeverity.Error );
		} );

		Check( "non-finite gravity is an Error at settings.gravity", () =>
		{
			var def = ValidDef();
			def.Settings.Gravity = new[] { 0f, 0f, float.NaN };
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.gravity", ParticleDefinitionSeverity.Error );
		} );

		Check( "unknown render type is an Error at settings.renderType", () =>
		{
			var def = ValidDef();
			def.Settings.RenderType = "Hologram";
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.renderType", ParticleDefinitionSeverity.Error );
		} );

		Check( "unknown shape is an Error at emitters[0].shape", () =>
		{
			var def = ValidDef();
			def.Emitters[0].Shape = "Torus";
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].shape", ParticleDefinitionSeverity.Error );
		} );

		Check( "Strand render with 1 strand point is an Error at settings.strandPoints", () =>
		{
			var def = ValidDef();
			def.Settings.RenderType = "Strand";
			def.Settings.StrandPoints = 1;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.strandPoints", ParticleDefinitionSeverity.Error );
		} );

		Check( "Strand render with 2 strand points passes the strand check", () =>
		{
			var def = ValidDef();
			def.Settings.RenderType = "Strand";
			def.Settings.StrandPoints = 2;
			AssertNoFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.strandPoints" );
		} );

		Check( "over-capacity peak is a Warning at settings.capacity carrying both numbers", () =>
		{
			// 100/s x 10 s = 1000 expected live, capacity 500.
			var def = ValidDef();
			def.Settings.Capacity = 500;
			def.Emitters[0].Rate = 100;
			def.Emitters[0].LifetimeMax = 10;
			var errors = ParticleDefinitionValidate.ValidateDefinition( def );
			AssertFinding( errors, "settings.capacity", ParticleDefinitionSeverity.Warning );
			var found = errors.First( e => e.FieldPath == "settings.capacity" && e.Severity == ParticleDefinitionSeverity.Warning );
			Assert( found.Message.Contains( "1000" ) && found.Message.Contains( "500" ),
				$"over-capacity warning must carry expected count and capacity, got: {found.Message}" );
		} );

		Check( "bursts count toward the expected peak", () =>
		{
			// 10/s x 5 s = 50 steady state + 600 burst = 650 > 500.
			var def = ValidDef();
			def.Settings.Capacity = 500;
			def.Emitters[0].Rate = 10;
			def.Emitters[0].LifetimeMax = 5;
			def.Emitters[0].Bursts = new List<ParticleBurstDef> { new() { Step = 0, Count = 600 } };
			var errors = ParticleDefinitionValidate.ValidateDefinition( def );
			AssertFinding( errors, "settings.capacity", ParticleDefinitionSeverity.Warning );
			Assert( errors.First( e => e.FieldPath == "settings.capacity" ).Message.Contains( "650" ),
				"burst-inclusive peak should be 650" );
		} );

		Check( "peak exactly at capacity is not a warning", () =>
		{
			var def = ValidDef();
			def.Settings.Capacity = 1000;
			def.Emitters[0].Rate = 100;
			def.Emitters[0].LifetimeMax = 10;
			AssertNoFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.capacity" );
		} );

		Check( "MeshSurface without mesh is NOT duplicated here but lane 5 still throws", () =>
		{
			// Composition pin: this pack must report the missing mesh
			// exactly zero times; lane 5's Build is the single reporter.
			var def = ValidDef();
			def.Emitters[0].Shape = "MeshSurface";
			def.Emitters[0].Mesh = null;
			var errors = ParticleDefinitionValidate.ValidateDefinition( def );
			Assert( !errors.Any( e => e.Message.Contains( "mesh", StringComparison.OrdinalIgnoreCase ) ),
				$"validator must not duplicate the mesh check, got: {string.Join( "; ", errors.Select( e => e.FieldPath + ": " + e.Message ) )}" );
			Throws( () => def.Build(), "MeshSurface without mesh must still fail in lane 5's Build" );
		} );

		Check( "declared burst with count 0 is an Error at emitters[0].bursts[0].count", () =>
		{
			var def = ValidDef();
			def.Emitters[0].Bursts = new List<ParticleBurstDef> { new() { Step = 0, Count = 0 } };
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].bursts[0].count", ParticleDefinitionSeverity.Error );
		} );

		Check( "declared burst with negative count is an Error at emitters[0].bursts[0].count", () =>
		{
			var def = ValidDef();
			def.Emitters[0].Bursts = new List<ParticleBurstDef> { new() { Step = 0, Count = -3 } };
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].bursts[0].count", ParticleDefinitionSeverity.Error );
		} );

		Check( "enabled emitter with rate 0 and no bursts is a Warning at emitters[0]", () =>
		{
			var def = ValidDef();
			def.Emitters[0].Rate = 0;
			def.Emitters[0].Bursts = new List<ParticleBurstDef>();
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0]", ParticleDefinitionSeverity.Warning );
		} );

		Check( "disabled emitter with rate 0 is not warned", () =>
		{
			var def = ValidDef();
			def.Emitters[0].Rate = 0;
			def.Emitters[0].Bursts = new List<ParticleBurstDef>();
			def.Emitters[0].Enabled = false;
			AssertNoFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0]" );
		} );

		Check( "unresolvable preset is an Error at emitters[0].preset carrying the name", () =>
		{
			var def = ValidDef();
			def.Emitters[0].Preset = "no_such_preset_xyz";
			var errors = ParticleDefinitionValidate.ValidateDefinition( def );
			AssertFinding( errors, "emitters[0].preset", ParticleDefinitionSeverity.Error );
			Assert( errors.First( e => e.FieldPath == "emitters[0].preset" ).Message.Contains( "no_such_preset_xyz" ),
				"preset error must carry the unresolvable name" );
			// And exactly one finding for the bad name: no knock-on
			// zero-emission warning from the fallback defaults.
			Assert( errors.Count( e => e.FieldPath == "emitters[0]" ) == 0, "bad preset name must not also warn about silence" );
		} );

		Check( "pack B and pack C presets resolve", () =>
		{
			foreach ( string name in new[] { "explosion", "lava", "fx:fire", "ink" } )
			{
				var def = new ParticleSystemDef { Settings = new ParticleSettingsDef { Capacity = 10_000, Seed = 1 } };
				def.Emitters.Add( new ParticleEmitterDef { Preset = name } );
				AssertNoFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "emitters[0].preset" );
			}
		} );

		Check( "version 2 object is an Error at version", () =>
		{
			var def = ValidDef();
			def.Version = 2;
			AssertFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "version", ParticleDefinitionSeverity.Error );
		} );

		Check( "unknown JSON fields pass (forward compat is not tightened)", () =>
		{
			string json = "{\"version\":1,\"name\":\"t\",\"futureTopField\":{\"x\":1},"
				+ "\"settings\":{\"capacity\":500,\"seed\":42,\"renderType\":\"Billboard\",\"strandPoints\":0,\"futureSettingsField\":true},"
				+ "\"emitters\":[{\"shape\":\"Point\",\"rate\":12,\"lifetimeMin\":3,\"lifetimeMax\":6,\"sizeMin\":1,\"sizeMax\":2,\"futureEmitterField\":\"yes\"}]}";
			var errors = ParticleDefinitionValidate.ValidateJson( json );
			Assert( errors.Count == 0, $"unknown fields must not be findings, got: {string.Join( "; ", errors.Select( e => e.Severity + "@" + e.FieldPath ) )}" );
		} );

		Check( "missing seed in JSON is an Error at settings.seed", () =>
		{
			string json = "{\"version\":1,\"settings\":{\"capacity\":500},"
				+ "\"emitters\":[{\"shape\":\"Point\",\"rate\":12,\"lifetimeMin\":3,\"lifetimeMax\":6,\"sizeMin\":1,\"sizeMax\":2}]}";
			AssertFinding( ParticleDefinitionValidate.ValidateJson( json ), "settings.seed", ParticleDefinitionSeverity.Error );
		} );

		Check( "seed presence is JSON-only: the parsed object cannot show it", () =>
		{
			// The same seedless JSON, parsed by lane 5, defaults seed
			// to 1 — so the object form honestly reports nothing about
			// seed. This pins the documented limitation, not a bug.
			string json = "{\"version\":1,\"settings\":{\"capacity\":500},"
				+ "\"emitters\":[{\"shape\":\"Point\",\"rate\":12,\"lifetimeMin\":3,\"lifetimeMax\":6,\"sizeMin\":1,\"sizeMax\":2}]}";
			var def = ParticleAuthoring.FromJson( json );
			AssertNoFinding( ParticleDefinitionValidate.ValidateDefinition( def ), "settings.seed" );
		} );

		Check( "malformed JSON throws instead of returning a soft error", () =>
		{
			Throws( () => ParticleDefinitionValidate.ValidateJson( "{ not json" ), "malformed JSON" );
			Throws( () => ParticleDefinitionValidate.ValidateJson( "" ), "empty JSON" );
			Throws( () => ParticleDefinitionValidate.ValidateJson( "[1, 2]" ), "non-object top level" );
		} );

		Check( "wrong or missing version in JSON throws (lane 5 load failure)", () =>
		{
			Throws( () => ParticleDefinitionValidate.ValidateJson( "{\"version\":2,\"settings\":{\"capacity\":500,\"seed\":1},\"emitters\":[]}" ), "version 2 JSON" );
			Throws( () => ParticleDefinitionValidate.ValidateJson( "{\"settings\":{\"capacity\":500,\"seed\":1},\"emitters\":[]}" ), "versionless JSON" );
		} );
	}
}
