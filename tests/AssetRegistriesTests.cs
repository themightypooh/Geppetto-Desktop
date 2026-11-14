using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

internal static class AssetRegistriesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "assetregistries", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Equal<T>( T actual, T expected, string what )
		{
			if ( !EqualityComparer<T>.Default.Equals( actual, expected ) )
				throw new Exception( $"{what}: got '{actual}', expected '{expected}'" );
		}

		string RepoRoot()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					if ( File.Exists( Path.Combine( dir.FullName, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) )
						return dir.FullName;
					dir = dir.Parent;
				}
			}
			throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
		}

		string FixtureDir()
		{
			var dir = Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-model-particle-sound-registries" );
			if ( !File.Exists( Path.Combine( dir, "crate.mdl" ) ) )
				throw new Exception( "assetregistries fixture dir not found (crate.mdl missing)" );
			return dir;
		}

		// Fresh project root per test: fixture files are copied in, the
		// tests edit the COPIES, never the fixtures.
		string NewProject( string name )
		{
			var root = Path.Combine( settings.WorkDirectory, "assetregistries", name + "-" + Guid.NewGuid().ToString( "N" ).Substring( 0, 8 ) );
			var fixture = FixtureDir();
			Directory.CreateDirectory( Path.Combine( root, "models", "props" ) );
			Directory.CreateDirectory( Path.Combine( root, "particles" ) );
			Directory.CreateDirectory( Path.Combine( root, "sounds" ) );
			File.Copy( Path.Combine( fixture, "crate.mdl" ), Path.Combine( root, "models", "props", "crate.mdl" ) );
			File.Copy( Path.Combine( fixture, "barrel.obj" ), Path.Combine( root, "models", "props", "barrel.obj" ) );
			File.Copy( Path.Combine( fixture, "spark.json" ), Path.Combine( root, "particles", "spark.json" ) );
			File.Copy( Path.Combine( fixture, "beep.wav" ), Path.Combine( root, "sounds", "beep.wav" ) );
			return root;
		}

		EntityRegistry LoadCore() =>
			EntityRegistry.Load( Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" ) );

		// Core registry + the test-only particle/sound classes (fixture
		// JSON, NOT core-source1.json — the P3 soundscapes ticket owns
		// the real info_particle_system / ambient_generic classes).
		EntityRegistry LoadExtended()
		{
			var reg = LoadCore();
			reg.LoadAdditional( Path.Combine( FixtureDir(), "registry-extra.json" ) );
			return reg;
		}

		AssetRegistries SampleRegistries( string root )
		{
			var all = new AssetRegistries( root );
			Require( all.Models.Register( new ModelEntry
			{
				Name = "Crate",
				Path = "models/props/crate.mdl",
				Attachments = new List<string> { "handle", "lid" },
				Scale = new Vec3( 1f, 1f, 1f ),
				PropType = PropType.Static,
			} ), "sample crate model did not register" );
			Require( all.Models.Register( new ModelEntry
			{
				Name = "Barrel",
				Path = "models/props/barrel.obj",
				Attachments = new List<string>(),
				Scale = new Vec3( 1f, 2f, 0.5f ),
				PropType = PropType.Physics,
			} ), "sample barrel model did not register" );
			Require( all.Particles.Register( new ParticleEntry
			{
				SystemName = "spark_burst",
				DefinitionPath = "particles/spark.json",
				Params = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
				{
					{ "count", "64" },
					{ "lifetime", "1.5" },
				},
			} ), "sample spark particle did not register" );
			Require( all.Sounds.Register( new SoundEntry
			{
				Name = "Beep",
				Path = "sounds/beep.wav",
				Volume = 0.5f,
			} ), "sample beep sound did not register" );
			return all;
		}

		ModelEntry Crate( AssetRegistries all ) =>
			all.Models.TryGetByName( "Crate", out var e ) ? e : throw new Exception( "sample crate missing" );
		ParticleEntry Spark( AssetRegistries all ) =>
			all.Particles.TryGet( "spark_burst", out var e ) ? e : throw new Exception( "sample spark missing" );
		SoundEntry Beep( AssetRegistries all ) =>
			all.Sounds.TryGetByName( "Beep", out var e ) ? e : throw new Exception( "sample beep missing" );

		// ---- Fixture map for the used-count / replace ticket ----
		// wall-brick: 6 brick faces. floor-mixed: faces -X/+X/-Y/+Y/-Z/+Z =
		// brick, concrete, brick, nodraw, concrete, brick (face 0 carries a
		// non-default UV so the replace test can prove UVs survive a swap).
		// floor-concrete: 6 concrete faces. Totals: brick 9, concrete 8,
		// nodraw 1; crate x2, barrel x1, spark x1, beep x1.

		MapDocument FixtureMap()
		{
			var doc = MapDocument.Create( "used-count" );
			doc.AddBrush( MapBrushData.Box( "wall-brick", "brick wall",
				new Vec3( 0f, 0f, 0f ), new Vec3( 128f, 16f, 128f ), "brick/brickwall001" ) );
			var mixed = MapBrushData.Box( "floor-mixed", "mixed floor",
				new Vec3( 0f, 0f, 0f ), new Vec3( 128f, 128f, 16f ),
				new[] { "brick/brickwall001", "concrete/concretefloor001", "brick/brickwall001",
					"tools/toolsnodraw", "concrete/concretefloor001", "brick/brickwall001" } );
			var customUv = new FaceUv( new Vec2( 0.25f, 0.5f ), new Vec2( 0.5f, 0.5f ), 45f, 8f, 2 );
			mixed.Faces[0] = new MapBrushFaceData( mixed.Faces[0].Normal, mixed.Faces[0].Distance,
				new FaceMaterial( "brick/brickwall001", customUv ) );
			doc.AddBrush( mixed );
			doc.AddBrush( MapBrushData.Box( "floor-concrete", "concrete floor",
				new Vec3( 0f, 0f, 128f ), new Vec3( 128f, 128f, 144f ), "concrete/concretefloor001" ) );
			AddProp( doc, "prop-crate-1", "prop_static", "models/props/crate.mdl", new Vec3( 32f, 32f, 0f ) );
			AddProp( doc, "prop-crate-2", "prop_static", "models/props/crate.mdl", new Vec3( 64f, 32f, 0f ) );
			AddProp( doc, "prop-barrel-1", "prop_physics", "models/props/barrel.obj", new Vec3( 96f, 32f, 0f ) );
			var spark = new MapEntityData { Id = "fx-spark", ClassName = "info_particle_system", Origin = new Vec3( 64f, 64f, 64f ) };
			spark.KeyValues["effect_name"] = "spark_burst";
			doc.AddEntity( spark );
			var beep = new MapEntityData { Id = "snd-beep", ClassName = "ambient_generic", Origin = new Vec3( 64f, 64f, 32f ) };
			beep.KeyValues["message"] = "sounds/beep.wav";
			doc.AddEntity( beep );
			return doc;
		}

		void AddProp( MapDocument doc, string id, string className, string model, Vec3 origin )
		{
			var entity = new MapEntityData { Id = id, ClassName = className, Origin = origin };
			entity.KeyValues["model"] = model;
			doc.AddEntity( entity );
		}

		// ---- Models: register / lookup ----

		Check( "model register + lookup by name and path, case-insensitive", () =>
		{
			var all = SampleRegistries( NewProject( "model-lookup" ) );
			Equal( all.Models.Count, 2, "model count" );
			Require( all.Models.TryGetByName( "crate", out var byName ), "name lookup (lowercase) failed" );
			Require( all.Models.TryGetByPath( "MODELS/PROPS/CRATE.MDL", out var byPath ), "path lookup (uppercase) failed" );
			Require( ReferenceEquals( byName, byPath ), "name and path lookups returned different entries" );
			Equal( byName.Attachments.Count, 2, "crate attachments" );
			Require( byName.Attachments.Contains( "handle" ), "crate is missing attachment 'handle'" );
			Equal( byName.PropType, PropType.Static, "crate prop type" );
			Require( all.Models.Contains( "Barrel" ), "Contains missed Barrel by name" );
			Require( all.Models.Contains( "models/props/barrel.obj" ), "Contains missed barrel by path" );
		} );

		Check( "duplicate model name or path is refused, existing entry kept", () =>
		{
			var all = SampleRegistries( NewProject( "model-dup" ) );
			Require( !all.Models.Register( new ModelEntry { Name = "Crate", Path = "models/props/other.mdl" } ),
				"duplicate name registered" );
			Require( !all.Models.Register( new ModelEntry { Name = "Other", Path = "models/props/crate.mdl" } ),
				"duplicate path registered" );
			Equal( all.Models.Count, 2, "model count after refused duplicates" );
			Require( all.Models.TryGetByName( "Crate", out var kept ) && kept.Path == "models/props/crate.mdl",
				"duplicate registration overwrote the kept crate" );
		} );

		Check( "model register rejects bad entries", () =>
		{
			var all = SampleRegistries( NewProject( "model-bad" ) );
			Require( !all.Models.Register( null! ), "null model registered" );
			Require( !all.Models.Register( new ModelEntry { Name = "", Path = "models/props/x.mdl" } ), "blank name registered" );
			Require( !all.Models.Register( new ModelEntry { Name = "Abs", Path = "/abs/crate.mdl" } ), "absolute path registered" );
			Require( !all.Models.Register( new ModelEntry { Name = "Esc", Path = "../escape.mdl" } ), "escaping path registered" );
			Require( !all.Models.Register( new ModelEntry { Name = "NoExt", Path = "models/props/noext" } ), "extensionless path registered" );
			Require( !all.Models.Register( new ModelEntry { Name = "Txt", Path = "models/props/x.txt" } ), "non-model extension registered" );
			Require( !all.Models.Register( new ModelEntry { Name = "Flat", Path = "models/props/flat.mdl", Scale = new Vec3( 1f, 0f, 1f ) } ),
				"zero scale component registered" );
			Require( !all.Models.Register( new ModelEntry { Name = "Neg", Path = "models/props/neg.mdl", Scale = new Vec3( -1f, 1f, 1f ) } ),
				"negative scale component registered" );
			Require( !all.Models.Register( new ModelEntry { Name = "Nan", Path = "models/props/nan.mdl", Scale = new Vec3( float.NaN, 1f, 1f ) } ),
				"NaN scale component registered" );
			Equal( all.Models.Count, 2, "model count after refused bad entries" );
		} );

		// ---- Search ----

		Check( "wildcard search finds models", () =>
		{
			var all = SampleRegistries( NewProject( "model-search" ) );
			var crates = all.Models.Search( "props/crate*" );
			Equal( crates.Count, 1, "props/crate* hits" );
			Equal( crates[0].Name, "Crate", "props/crate* hit" );
			Equal( all.Models.Search( "*barrel*" ).Count, 1, "*barrel* hits" );
			Equal( all.Models.Search( "models/props/*.obj" ).Count, 1, "*.obj hits" );
			Equal( all.Models.Search( "models/props/c?ate.mdl" ).Count, 1, "c?ate hits" );
			Equal( all.Models.Search( "models/props/c?ate.obj" ).Count, 0, "c?ate.obj must not match the .mdl crate" );
		} );

		Check( "substring search is case-insensitive; blank query returns all; no match is empty", () =>
		{
			var all = SampleRegistries( NewProject( "model-substr" ) );
			Equal( all.Models.Search( "CRATE" ).Count, 1, "CRATE substring hits" );
			Equal( all.Models.Search( "props" ).Count, 2, "'props' substring hits" );
			Equal( all.Models.Search( "" ).Count, 2, "blank query hits" );
			Equal( all.Models.Search( "   " ).Count, 2, "whitespace query hits" );
			Equal( all.Models.Search( "no-such-model" ).Count, 0, "non-matching query hits" );
			Equal( all.Particles.Search( "SPARK" ).Count, 1, "particle substring hits" );
			Equal( all.Particles.Search( "count=64" ).Count, 1, "particle param search hits" );
			Equal( all.Particles.Search( "spark*" ).Count, 1, "particle wildcard hits" );
			Equal( all.Sounds.Search( "beep*" ).Count, 1, "sound wildcard hits" );
			Equal( all.Sounds.Search( "sounds/*.ogg" ).Count, 0, "sound .ogg wildcard hits" );
		} );

		Check( "prop-type filter lists static vs physics models", () =>
		{
			var all = SampleRegistries( NewProject( "model-proptype" ) );
			Equal( all.Models.ByPropType( PropType.Static ).Count, 1, "static count" );
			Equal( all.Models.ByPropType( PropType.Static )[0].Name, "Crate", "static entry" );
			Equal( all.Models.ByPropType( PropType.Physics ).Count, 1, "physics count" );
			Equal( all.Models.ByPropType( PropType.Dynamic ).Count, 0, "dynamic count" );
		} );

		// ---- Used / Favourites / Recent ----

		Check( "favourites add, list and remove per registry", () =>
		{
			var all = SampleRegistries( NewProject( "fav" ) );
			Require( !all.Models.MarkFavourite( "no-such-model" ), "favourite marked an unknown model" );
			Require( all.Models.MarkFavourite( "Crate" ), "favourite did not mark Crate" );
			Require( all.Models.IsFavourite( "models/props/crate.mdl" ), "Crate not favourite by path" );
			Equal( all.Models.Favourites.Count, 1, "model favourites count" );
			Require( all.Sounds.MarkFavourite( "Beep" ), "favourite did not mark Beep" );
			Equal( all.Sounds.Favourites.Count, 1, "sound favourites count" );
			Require( all.Particles.MarkFavourite( "spark_burst" ), "favourite did not mark spark" );
			Equal( all.Particles.Favourites.Count, 1, "particle favourites count" );
			Require( all.Models.UnmarkFavourite( "Crate" ), "unmark did not remove Crate" );
			Require( !all.Models.IsFavourite( "Crate" ), "Crate still favourite after unmark" );
			Equal( all.Models.Favourites.Count, 0, "model favourites count after unmark" );
		} );

		Check( "used counts order Used most-used first; Recent is most-recent first", () =>
		{
			var all = SampleRegistries( NewProject( "used" ) );
			var crate = Crate( all );
			var barrel = all.Models.TryGetByName( "Barrel", out var b ) ? b : throw new Exception( "barrel missing" );
			all.Models.NoteUsed( barrel );
			all.Models.NoteUsed( crate );
			all.Models.NoteUsed( crate );
			Equal( crate.UsedCount, 2, "crate used count" );
			Equal( barrel.UsedCount, 1, "barrel used count" );
			Equal( all.Models.Used.Count, 2, "used count" );
			Equal( all.Models.Used[0].Name, "Crate", "most-used first" );
			Equal( all.Models.Recent.Count, 2, "recent count" );
			Equal( all.Models.Recent[0].Name, "Crate", "most-recent first" );
			Equal( all.Models.Recent[1].Name, "Barrel", "second recent" );
			all.Models.NoteUsed( null! );
			all.Models.NoteUsed( new ModelEntry { Name = "Ghost", Path = "models/props/ghost.mdl" } );
			Equal( all.Models.Used.Count, 2, "used count after noting null/unregistered" );
		} );

		Check( "recent list caps at 20 entries", () =>
		{
			var all = new AssetRegistries( NewProject( "recent-cap" ) );
			for ( int i = 0; i < 22; i++ )
			{
				var entry = new ModelEntry { Name = $"Prop{i:00}", Path = $"models/props/prop{i:00}.mdl" };
				Require( all.Models.Register( entry ), $"prop {i} did not register" );
				all.Models.NoteUsed( entry );
			}
			Equal( all.Models.Recent.Count, 20, "recent count after 22 uses" );
			Equal( all.Models.Recent[0].Name, "Prop21", "most recent after cap" );
			Equal( all.Models.Recent[19].Name, "Prop02", "oldest kept after cap" );
		} );

		Check( "remove drops the entry and its usage; clear empties the registry", () =>
		{
			var all = SampleRegistries( NewProject( "remove" ) );
			var crate = Crate( all );
			all.Models.NoteUsed( crate );
			all.Models.MarkFavourite( "Crate" );
			Require( all.Models.Remove( "Crate" ), "remove failed" );
			Require( !all.Models.Contains( "Crate" ), "removed crate still found" );
			Equal( all.Models.Recent.Count, 0, "recent still lists removed crate" );
			Equal( all.Models.Favourites.Count, 0, "favourites still list removed crate" );
			Require( !all.Models.Remove( "Crate" ), "second remove succeeded" );
			all.Sounds.Clear();
			Equal( all.Sounds.Count, 0, "sound count after clear" );
			Equal( all.Sounds.Favourites.Count, 0, "sound favourites after clear" );
		} );

		// ---- Validation ----

		Check( "validate reports a missing model file as MISSING_MODEL and nothing when present", () =>
		{
			var root = NewProject( "validate-model" );
			var all = SampleRegistries( root );
			Equal( all.Models.ValidateFiles( root ).Count, 0, "issues when all model files exist" );
			File.Delete( Path.Combine( root, "models", "props", "barrel.obj" ) );
			var issues = all.Models.ValidateFiles( root );
			Equal( issues.Count, 1, "issues after deleting barrel.obj" );
			Equal( issues[0].Code, AssetRegistryIssue.CodeMissingModel, "missing-model issue code" );
			Equal( issues[0].Code, "MISSING_MODEL", "missing-model issue code literal" );
			Require( issues[0].Message.Contains( "models/props/barrel.obj" ), "missing-model issue does not name the path" );
			Equal( issues[0].Kind, AssetKind.Model, "missing-model issue kind" );
		} );

		Check( "validate-all aggregates missing model, particle and sound issues", () =>
		{
			var root = NewProject( "validate-all" );
			var all = SampleRegistries( root );
			all.Particles.Register( new ParticleEntry { SystemName = "ghost_fx", DefinitionPath = "particles/ghost.json" } );
			all.Sounds.Register( new SoundEntry { Name = "Ghost", Path = "sounds/ghost.wav", Volume = 1f } );
			File.Delete( Path.Combine( root, "sounds", "beep.wav" ) );
			var issues = all.ValidateAll();
			Equal( issues.Count, 3, "validate-all issue count" );
			Require( issues.Any( i => i.Code == "MISSING_PARTICLE" && i.AssetPath == "particles/ghost.json" ), "no MISSING_PARTICLE for ghost.json" );
			Require( issues.Any( i => i.Code == "MISSING_SOUND" && i.AssetPath == "sounds/beep.wav" ), "no MISSING_SOUND for beep.wav" );
			Require( issues.Any( i => i.Code == "MISSING_SOUND" && i.AssetPath == "sounds/ghost.wav" ), "no MISSING_SOUND for ghost.wav" );
			Require( issues.All( i => i.Severity == IssueSeverity.Error ), "not all issues are errors" );
		} );

		Check( "sound register rejects bad volume and bad extension", () =>
		{
			var all = SampleRegistries( NewProject( "sound-bad" ) );
			Require( !all.Sounds.Register( new SoundEntry { Name = "Loud", Path = "sounds/loud.wav", Volume = 1.5f } ), "volume 1.5 registered" );
			Require( !all.Sounds.Register( new SoundEntry { Name = "Quiet", Path = "sounds/quiet.wav", Volume = -0.1f } ), "volume -0.1 registered" );
			Require( !all.Sounds.Register( new SoundEntry { Name = "Nan", Path = "sounds/nan.wav", Volume = float.NaN } ), "volume NaN registered" );
			Require( !all.Sounds.Register( new SoundEntry { Name = "Txt", Path = "sounds/note.txt", Volume = 1f } ), "non-sound extension registered" );
			Require( all.Sounds.Register( new SoundEntry { Name = "Silent", Path = "sounds/silent.wav", Volume = 0f } ), "volume 0 rejected" );
			Require( all.Sounds.Register( new SoundEntry { Name = "Full", Path = "sounds/full.wav", Volume = 1f } ), "volume 1 rejected" );
			Equal( all.Sounds.Count, 3, "sound count after bad/edge registrations" );
		} );

		Check( "particle register rejects duplicate system names; built-in systems need no file", () =>
		{
			var root = NewProject( "particle-dup" );
			var all = SampleRegistries( root );
			Require( !all.Particles.Register( new ParticleEntry { SystemName = "spark_burst", DefinitionPath = "particles/other.json" } ),
				"duplicate system name registered" );
			Require( !all.Particles.Register( new ParticleEntry { SystemName = "  ", DefinitionPath = "particles/x.json" } ),
				"blank system name registered" );
			Require( all.Particles.Register( new ParticleEntry { SystemName = "builtin_glow" } ),
				"built-in particle (no file) rejected" );
			File.Delete( Path.Combine( root, "particles", "spark.json" ) );
			var issues = all.Particles.ValidateFiles( root );
			Equal( issues.Count, 1,
				"exactly one missing-file issue expected (spark.json); the built-in system must not produce one" );
			Equal( issues[0].AssetPath, "particles/spark.json", "missing-file issue is not spark.json" );
		} );

		// ---- Placement via the entity API ----

		Check( "placing a prop_static from the registry validates and carries non-uniform scale", () =>
		{
			var root = NewProject( "place-static" );
			var all = SampleRegistries( root );
			var entities = new MapEntities( LoadCore() );
			var barrel = all.Models.TryGetByName( "Barrel", out var b ) ? b : throw new Exception( "barrel missing" );
			var result = all.PlaceModel( barrel, entities, new Vec3( 64f, 128f, 0f ) );
			Require( result.Ok, "placement failed: " + result.Message );
			Equal( result.ClassName, "prop_physics", "barrel class (Physics prop type)" );
			Require( result.Entity is not null, "placement returned no entity" );
			Equal( result.Entity.ClassName, "prop_physics", "entity class" );
			Require( result.Entity.Origin.AlmostEquals( new Vec3( 64f, 128f, 0f ) ), "entity origin is not the placement position" );
			Require( result.Entity.Keyvalues.TryGetValue( "model", out var model ) && model == "models/props/barrel.obj",
				"entity model keyvalue is not the registry path" );
			Require( result.Scale.AlmostEquals( new Vec3( 1f, 2f, 0.5f ) ), "placement result lost the non-uniform scale" );
			Equal( barrel.UsedCount, 1, "placement did not record one use" );
			Equal( entities.Count, 1, "entity count after placement" );
		} );

		Check( "placing a static prop uses prop_static and honours angles", () =>
		{
			var root = NewProject( "place-angles" );
			var all = SampleRegistries( root );
			var entities = new MapEntities( LoadCore() );
			var result = all.PlaceModel( Crate( all ), entities, new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 90f, 0f ) );
			Require( result.Ok, "placement failed: " + result.Message );
			Equal( result.ClassName, "prop_static", "crate class (Static prop type)" );
			Require( result.Entity!.Angles.AlmostEquals( new Vec3( 0f, 90f, 0f ) ), "entity angles are not the placement angles" );
		} );

		Check( "placing a prop whose model file is missing is a named issue and places nothing", () =>
		{
			var root = NewProject( "place-missing" );
			var all = SampleRegistries( root );
			File.Delete( Path.Combine( root, "models", "props", "crate.mdl" ) );
			var entities = new MapEntities( LoadCore() );
			var result = all.PlaceModel( Crate( all ), entities, new Vec3( 0f, 0f, 0f ) );
			Require( !result.Ok, "placement of a missing model succeeded" );
			Equal( result.Code, "MISSING_MODEL", "missing-model placement code" );
			Require( result.Message.Contains( "models/props/crate.mdl" ), "missing-model placement message does not name the path" );
			Require( result.Entity is null, "missing-model placement returned an entity" );
			Equal( entities.Count, 0, "entity count after refused placement" );
			Equal( Crate( all ).UsedCount, 0, "refused placement recorded a use" );
		} );

		Check( "placing an unregistered model is a named issue and places nothing", () =>
		{
			var root = NewProject( "place-unreg" );
			var all = SampleRegistries( root );
			var entities = new MapEntities( LoadCore() );
			var ghost = new ModelEntry { Name = "Ghost", Path = "models/props/ghost.mdl" };
			var result = all.PlaceModel( ghost, entities, new Vec3( 0f, 0f, 0f ) );
			Require( !result.Ok, "placement of an unregistered model succeeded" );
			Equal( result.Code, AssetRegistryIssue.CodeUnknownModel, "unregistered-model placement code" );
			Equal( entities.Count, 0, "entity count after refused placement" );
		} );

		Check( "particle placement against the core registry is UnknownEntityClass; extended registry places it", () =>
		{
			var root = NewProject( "place-particle" );
			var all = SampleRegistries( root );
			var coreEntities = new MapEntities( LoadCore() );
			var refused = all.PlaceParticle( Spark( all ), coreEntities, new Vec3( 10f, 20f, 30f ) );
			Require( !refused.Ok, "particle placement against the core registry succeeded" );
			Equal( refused.Code, EntityRegistry.CodeUnknownEntityClass, "particle placement refusal code" );
			Equal( coreEntities.Count, 0, "entity count after refused particle placement" );

			var entities = new MapEntities( LoadExtended() );
			var result = all.PlaceParticle( Spark( all ), entities, new Vec3( 10f, 20f, 30f ) );
			Require( result.Ok, "extended particle placement failed: " + result.Message );
			Equal( result.ClassName, "info_particle_system", "particle entity class" );
			Require( result.Entity!.Keyvalues.TryGetValue( "effect_name", out var fx ) && fx == "spark_burst",
				"particle entity effect_name is not the system name" );
			Equal( Spark( all ).UsedCount, 1, "particle placement did not record one use" );
		} );

		Check( "sound placement against the core registry is UnknownEntityClass; extended registry places it", () =>
		{
			var root = NewProject( "place-sound" );
			var all = SampleRegistries( root );
			var coreEntities = new MapEntities( LoadCore() );
			var refused = all.PlaceSound( Beep( all ), coreEntities, new Vec3( 0f, 0f, 0f ) );
			Require( !refused.Ok, "sound placement against the core registry succeeded" );
			Equal( refused.Code, EntityRegistry.CodeUnknownEntityClass, "sound placement refusal code" );
			Equal( coreEntities.Count, 0, "entity count after refused sound placement" );

			var entities = new MapEntities( LoadExtended() );
			var result = all.PlaceSound( Beep( all ), entities, new Vec3( 5f, 5f, 5f ) );
			Require( result.Ok, "extended sound placement failed: " + result.Message );
			Equal( result.ClassName, "ambient_generic", "sound entity class" );
			Require( result.Entity!.Keyvalues.TryGetValue( "message", out var msg ) && msg == "sounds/beep.wav",
				"sound entity message is not the registry path" );
			Require( result.Entity.Keyvalues.TryGetValue( "volume", out var vol ) && vol == "0.5",
				$"sound entity volume is '{vol}', expected '0.5'" );
			Equal( Beep( all ).UsedCount, 1, "sound placement did not record one use" );
		} );

		// ---- Hotload seam ----

		Check( "watch-with adapter watches every registered file for hotload", () =>
		{
			var root = NewProject( "watch" );
			var all = SampleRegistries( root );
			all.Particles.Register( new ParticleEntry { SystemName = "builtin_glow" } );
			var hotload = new AssetHotload( root );
			Equal( all.WatchWith( hotload ), 4, "watched count (2 models + 1 sound + 1 particle file; built-in has no file)" );
			Require( hotload.TryGetAsset( AssetKind.Model, "models/props/crate.mdl" ) is not null, "crate not watched" );
			Require( hotload.TryGetAsset( AssetKind.Sound, "sounds/beep.wav" ) is not null, "beep not watched" );
			Require( hotload.TryGetAsset( AssetKind.Particle, "particles/spark.json" ) is not null, "spark not watched" );
			Require( hotload.TryGetAsset( AssetKind.Particle, "builtin_glow" ) is null, "built-in system must not be watched as a file" );
			Equal( hotload.PollForChanges().Count, 0, "first poll reported changes for untouched watched files" );
		} );

		// ---- Used-count / Replace Texture / Replace Model ----

		Check( "fixture map reports the right use-counts per asset", () =>
		{
			var root = NewProject( "usecount" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			Equal( all.UseCount( doc, AssetKind.Model, "models/props/crate.mdl" ), 2, "crate model use-count" );
			Equal( all.UseCount( doc, AssetKind.Model, "Crate" ), 2, "crate use-count by display name" );
			Equal( all.UseCount( doc, AssetKind.Model, "models/props/barrel.obj" ), 1, "barrel model use-count" );
			Equal( all.UseCount( doc, AssetKind.Model, "models/props/ghost.mdl" ), 0, "unreferenced model use-count" );
			Equal( all.UseCount( doc, AssetKind.Material, "brick/brickwall001" ), 9, "brick face use-count" );
			Equal( all.UseCount( doc, AssetKind.Material, "concrete/concretefloor001" ), 8, "concrete face use-count" );
			Equal( all.UseCount( doc, AssetKind.Material, "tools/toolsnodraw" ), 1, "nodraw face use-count" );
			Equal( all.UseCount( doc, AssetKind.Material, "metal/metalwall001" ), 0, "unreferenced material use-count" );
			Equal( all.UseCount( doc, AssetKind.Particle, "spark_burst" ), 1, "spark particle use-count" );
			Equal( all.UseCount( doc, AssetKind.Particle, "builtin_glow" ), 0, "unreferenced particle use-count" );
			Equal( all.UseCount( doc, AssetKind.Sound, "sounds/beep.wav" ), 1, "beep sound use-count" );
			Equal( all.UseCount( doc, AssetKind.Sound, "Beep" ), 1, "beep use-count by display name" );
			var modelCounts = all.UseCounts( doc, AssetKind.Model );
			Equal( modelCounts.Count, 2, "model use-counts keys" );
			Equal( modelCounts["models/props/crate.mdl"], 2, "model use-counts crate" );
			Equal( modelCounts["models/props/barrel.obj"], 1, "model use-counts barrel" );
		} );

		Check( "UsedOnly excludes an unreferenced model, particle and sound", () =>
		{
			var root = NewProject( "usedonly" );
			var all = SampleRegistries( root );
			Require( all.Models.Register( new ModelEntry { Name = "Ghost", Path = "models/props/ghost.mdl" } ), "ghost model did not register" );
			Require( all.Particles.Register( new ParticleEntry { SystemName = "builtin_glow" } ), "built-in particle did not register" );
			Require( all.Sounds.Register( new SoundEntry { Name = "Silent", Path = "sounds/silent.wav", Volume = 1f } ), "silent sound did not register" );
			var doc = FixtureMap();
			var usedModels = all.Models.UsedOnly( doc );
			Equal( usedModels.Count, 2, "used models count" );
			Require( usedModels.Any( e => e.Name == "Crate" ), "UsedOnly missed Crate" );
			Require( usedModels.Any( e => e.Name == "Barrel" ), "UsedOnly missed Barrel" );
			Require( !usedModels.Any( e => e.Name == "Ghost" ), "UsedOnly kept the unreferenced Ghost" );
			var usedParticles = all.Particles.UsedOnly( doc );
			Equal( usedParticles.Count, 1, "used particles count" );
			Equal( usedParticles[0].SystemName, "spark_burst", "used particle" );
			var usedSounds = all.Sounds.UsedOnly( doc );
			Equal( usedSounds.Count, 1, "used sounds count" );
			Equal( usedSounds[0].Name, "Beep", "used sound" );
		} );

		Check( "ReplaceTexture swaps all faces with the old material and undoes cleanly", () =>
		{
			var root = NewProject( "replace-texture" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			var undo = new MapUndo( doc );
			var customUv = new FaceUv( new Vec2( 0.25f, 0.5f ), new Vec2( 0.5f, 0.5f ), 45f, 8f, 2 );
			var result = all.ReplaceTexture( doc, undo, "brick/brickwall001", "concrete/concretefloor001" );
			Require( result.Ok, "replace failed: " + result.Message );
			Equal( result.FacesChanged, 9, "faces changed" );
			Equal( result.BrushesChanged, 2, "brushes changed" );
			Equal( result.FacesSkipped, 0, "faces skipped" );
			Equal( all.UseCount( doc, AssetKind.Material, "brick/brickwall001" ), 0, "brick faces after replace" );
			Equal( all.UseCount( doc, AssetKind.Material, "concrete/concretefloor001" ), 17, "concrete faces after replace" );
			Equal( all.UseCount( doc, AssetKind.Material, "tools/toolsnodraw" ), 1, "nodraw face must survive the replace" );
			var mixed = doc.FindBrush( "floor-mixed" );
			Require( mixed is not null, "mixed brush missing after replace" );
			Require( mixed.Faces[0].Material.Uv.Equals( customUv ), "replace changed the face UV (only the material name may change)" );
			Equal( undo.UndoCount, 1, "replace must be one undo step" );
			undo.Undo();
			Equal( all.UseCount( doc, AssetKind.Material, "brick/brickwall001" ), 9, "brick faces after undo" );
			Equal( all.UseCount( doc, AssetKind.Material, "concrete/concretefloor001" ), 8, "concrete faces after undo" );
			var mixedAfterUndo = doc.FindBrush( "floor-mixed" );
			Require( mixedAfterUndo is not null, "mixed brush missing after undo" );
			Require( mixedAfterUndo.Faces[0].Material.Uv.Equals( customUv ), "undo changed the face UV" );
			undo.Redo();
			Equal( all.UseCount( doc, AssetKind.Material, "brick/brickwall001" ), 0, "brick faces after redo" );
			Equal( all.UseCount( doc, AssetKind.Material, "concrete/concretefloor001" ), 17, "concrete faces after redo" );
		} );

		Check( "ReplaceTexture with selected faces swaps only those faces", () =>
		{
			var root = NewProject( "replace-texture-selected" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			var undo = new MapUndo( doc );
			var result = all.ReplaceTexture( doc, undo, "brick/brickwall001", "concrete/concretefloor001",
				new List<(string, int)> { ("floor-mixed", 0) } );
			Require( result.Ok, "selected replace failed: " + result.Message );
			Equal( result.FacesChanged, 1, "faces changed" );
			Equal( result.BrushesChanged, 1, "brushes changed" );
			Equal( all.UseCount( doc, AssetKind.Material, "brick/brickwall001" ), 8, "brick faces after selected replace" );
			Equal( all.UseCount( doc, AssetKind.Material, "concrete/concretefloor001" ), 9, "concrete faces after selected replace" );
			undo.Undo();
			Equal( all.UseCount( doc, AssetKind.Material, "brick/brickwall001" ), 9, "brick faces after undo of selected replace" );
			Equal( all.UseCount( doc, AssetKind.Material, "concrete/concretefloor001" ), 8, "concrete faces after undo of selected replace" );
		} );

		Check( "ReplaceTexture skips entity-bound brushes and reports them", () =>
		{
			var root = NewProject( "replace-texture-bound" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			var detail = new MapEntityData { Id = "func-detail-1", ClassName = "func_detail", Origin = new Vec3( 0f, 0f, 0f ) };
			detail.BrushIds.Add( "floor-concrete" );
			doc.AddEntity( detail );
			var undo = new MapUndo( doc );
			var result = all.ReplaceTexture( doc, undo, "concrete/concretefloor001", "brick/brickwall001" );
			Require( result.Ok, "replace failed: " + result.Message );
			Equal( result.FacesChanged, 2, "faces changed (mixed floor only)" );
			Equal( result.BrushesChanged, 1, "brushes changed" );
			Equal( result.FacesSkipped, 6, "faces skipped on the bound brush" );
			Equal( result.SkippedBrushIds.Count, 1, "skipped brush ids" );
			Equal( result.SkippedBrushIds[0], "floor-concrete", "skipped brush" );
			var floor = doc.FindBrush( "floor-concrete" );
			Require( floor is not null && floor.Faces.All( f => f.Material.MaterialName == "concrete/concretefloor001" ),
				"bound brush faces must be untouched" );
			var detailAfter = doc.FindEntity( "func-detail-1" );
			Require( detailAfter is not null && detailAfter.BrushIds.Contains( "floor-concrete" ),
				"replace must not unbind the brush from its entity" );
			undo.Undo();
			Equal( all.UseCount( doc, AssetKind.Material, "concrete/concretefloor001" ), 8, "concrete faces after undo" );
			Equal( all.UseCount( doc, AssetKind.Material, "brick/brickwall001" ), 9, "brick faces after undo" );
		} );

		Check( "ReplaceTexture preserves displacements on replaced brushes", () =>
		{
			var root = NewProject( "replace-texture-displacement" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			doc.AddDisplacement( MapDisplacementData.Flat( "wall-brick", 0, Displacement.MinPower ) );
			Equal( doc.Displacements.Count, 1, "displacement count before replace" );
			var undo = new MapUndo( doc );
			var result = all.ReplaceTexture( doc, undo, "brick/brickwall001", "concrete/concretefloor001" );
			Require( result.Ok, "replace failed: " + result.Message );
			Equal( doc.Displacements.Count, 1, "displacement count after replace" );
			Equal( doc.Displacements[0].BrushId, "wall-brick", "displacement brush after replace" );
			Equal( doc.Displacements[0].FaceIndex, 0, "displacement face after replace" );
			Equal( doc.Displacements[0].Power, Displacement.MinPower, "displacement power after replace" );
			undo.Undo();
			Equal( doc.Displacements.Count, 1, "displacement count after undo" );
			Equal( doc.Displacements[0].BrushId, "wall-brick", "displacement brush after undo" );
			Equal( doc.Displacements[0].FaceIndex, 0, "displacement face after undo" );
		} );

		Check( "ReplaceModel swaps the model keyvalue on matching entities and undoes cleanly", () =>
		{
			var root = NewProject( "replace-model" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			var undo = new MapUndo( doc );
			var result = all.ReplaceModel( doc, undo, "models/props/crate.mdl", "models/props/barrel.obj" );
			Require( result.Ok, "replace failed: " + result.Message );
			Equal( result.EntitiesChanged, 2, "entities changed" );
			var crate1 = doc.FindEntity( "prop-crate-1" );
			var crate2 = doc.FindEntity( "prop-crate-2" );
			var barrel1 = doc.FindEntity( "prop-barrel-1" );
			Require( crate1 is not null && crate1.KeyValues["model"] == "models/props/barrel.obj", "crate 1 was not replaced" );
			Require( crate2 is not null && crate2.KeyValues["model"] == "models/props/barrel.obj", "crate 2 was not replaced" );
			Require( barrel1 is not null && barrel1.KeyValues["model"] == "models/props/barrel.obj", "barrel entity must keep its model" );
			Equal( undo.UndoCount, 1, "replace must be one undo step" );
			undo.Undo();
			Require( doc.FindEntity( "prop-crate-1" ) is not null
				&& doc.FindEntity( "prop-crate-1" ).KeyValues["model"] == "models/props/crate.mdl", "crate 1 was not restored by undo" );
			Require( doc.FindEntity( "prop-crate-2" ) is not null
				&& doc.FindEntity( "prop-crate-2" ).KeyValues["model"] == "models/props/crate.mdl", "crate 2 was not restored by undo" );
			undo.Redo();
			Require( doc.FindEntity( "prop-crate-1" ) is not null
				&& doc.FindEntity( "prop-crate-1" ).KeyValues["model"] == "models/props/barrel.obj", "crate 1 was not replaced by redo" );
		} );

		Check( "ReplaceModel with selected entities swaps only those", () =>
		{
			var root = NewProject( "replace-model-selected" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			var undo = new MapUndo( doc );
			var result = all.ReplaceModel( doc, undo, "models/props/crate.mdl", "models/props/barrel.obj",
				new List<string> { "prop-crate-2" } );
			Require( result.Ok, "selected replace failed: " + result.Message );
			Equal( result.EntitiesChanged, 1, "entities changed" );
			Require( doc.FindEntity( "prop-crate-1" ) is not null
				&& doc.FindEntity( "prop-crate-1" ).KeyValues["model"] == "models/props/crate.mdl", "unselected crate 1 must keep its model" );
			Require( doc.FindEntity( "prop-crate-2" ) is not null
				&& doc.FindEntity( "prop-crate-2" ).KeyValues["model"] == "models/props/barrel.obj", "selected crate 2 was not replaced" );
			undo.Undo();
			Require( doc.FindEntity( "prop-crate-2" ) is not null
				&& doc.FindEntity( "prop-crate-2" ).KeyValues["model"] == "models/props/crate.mdl", "crate 2 was not restored by undo" );
		} );

		Check( "ReplaceModel refuses an unregistered new model and changes nothing", () =>
		{
			var root = NewProject( "replace-model-unregistered" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			var undo = new MapUndo( doc );
			var revisionBefore = doc.Revision;
			var result = all.ReplaceModel( doc, undo, "models/props/crate.mdl", "models/props/ghost.mdl" );
			Require( !result.Ok, "replace to an unregistered model succeeded" );
			Equal( result.Code, AssetRegistryIssue.CodeUnknownModel, "unregistered-model replace code" );
			Equal( undo.UndoCount, 0, "failed replace must not push an undo step" );
			Equal( doc.Revision, revisionBefore, "failed replace must not bump the revision" );
			Equal( all.UseCount( doc, AssetKind.Model, "models/props/crate.mdl" ), 2, "crate count after refused replace" );
		} );

		Check( "texture alpha data records VMT translucency and manual notes", () =>
		{
			var root = NewProject( "texture-alpha" );
			var all = SampleRegistries( root );
			Require( all.TextureAlpha( "brick/brickwall001" ) is null, "unknown texture must have no alpha record" );
			Require( !all.HasAlpha( "brick/brickwall001" ), "unknown texture must read as opaque" );
			var glass = new VmtMaterial { Shader = "LightmappedGeneric" };
			glass.Set( "$basetexture", "glass/glasswindow001" );
			glass.Set( "$translucent", "1" );
			glass.Set( "$alpha", "0.5" );
			var info = all.NoteTextureAlphaFromVmt( glass );
			Equal( info.MaterialName, "glass/glasswindow001", "alpha record material" );
			Require( info.HasAlpha, "translucent VMT must record HasAlpha" );
			Equal( info.Opacity, 0.5f, "alpha record opacity" );
			Equal( info.Source, "vmt:$translucent", "alpha record source" );
			Require( all.HasAlpha( "glass/glasswindow001" ), "HasAlpha must be true after the VMT note" );
			var opaque = new VmtMaterial();
			opaque.Set( "$basetexture", "brick/brickwall001" );
			var opaqueInfo = all.NoteTextureAlphaFromVmt( opaque );
			Require( !opaqueInfo.HasAlpha, "plain VMT must record no alpha" );
			Equal( opaqueInfo.Opacity, 1f, "opaque opacity" );
			Equal( opaqueInfo.Source, "vmt:opaque", "opaque record source" );
			all.NoteTextureAlpha( "brick/brickwall001", true, 1f, "manual" );
			Require( all.HasAlpha( "brick/brickwall001" ), "manual note must record alpha" );
			var manualInfo = all.TextureAlpha( "brick/brickwall001" );
			Require( manualInfo is not null && manualInfo.Source == "manual", "manual note source" );
			Equal( all.TextureAlphas.Count, 2, "alpha record count" );
		} );

		Check( "ReplaceTexture with no matches is a no-op with no undo step", () =>
		{
			var root = NewProject( "replace-texture-noop" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			var undo = new MapUndo( doc );
			var revisionBefore = doc.Revision;
			var result = all.ReplaceTexture( doc, undo, "metal/metalwall001", "brick/brickwall001" );
			Require( result.Ok, "no-match replace failed: " + result.Message );
			Require( result.IsNoOp, "no-match replace must be a no-op" );
			Equal( result.FacesChanged, 0, "faces changed" );
			Require( result.Command is null, "no-match replace must not build a command" );
			Equal( undo.UndoCount, 0, "no-op replace must not push an undo step" );
			Equal( doc.Revision, revisionBefore, "no-op replace must not bump the revision" );
		} );

		Check( "ReplaceTexture refuses a blank new material and an unknown selected face", () =>
		{
			var root = NewProject( "replace-texture-bad" );
			var all = SampleRegistries( root );
			var doc = FixtureMap();
			var undo = new MapUndo( doc );
			var revisionBefore = doc.Revision;
			var blank = all.ReplaceTexture( doc, undo, "brick/brickwall001", "   " );
			Require( !blank.Ok, "blank new material replace succeeded" );
			Equal( blank.Code, "InvalidMaterial", "blank new material code" );
			var unknown = all.ReplaceTexture( doc, undo, "brick/brickwall001", "concrete/concretefloor001",
				new List<(string, int)> { ("wall-brick", 99) } );
			Require( !unknown.Ok, "unknown-face replace succeeded" );
			Equal( unknown.Code, "UnknownFace", "unknown-face code" );
			Equal( doc.Revision, revisionBefore, "failed replaces must not bump the revision" );
			Equal( undo.UndoCount, 0, "failed replaces must not push undo steps" );
			Equal( all.UseCount( doc, AssetKind.Material, "brick/brickwall001" ), 9, "brick faces after refused replaces" );
		} );
	}
}
