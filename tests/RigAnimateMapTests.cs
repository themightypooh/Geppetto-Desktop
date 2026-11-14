#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Tests for rig / animate on map props (hammer-interop-rig-animate-map-props):
/// prop_dynamic DefaultAnim clip validation (named clip exists, loop-seam check),
/// prop_static / prop_physics rejection, door / button I/O -&gt; clip playback
/// binding (headless: Fire an EntityIoInput, step frames, read the pose), root
/// motion (extract / strip / as-authored), and the animated-prop export
/// manifest. Clips are built in code (like GameClipPipelineTests); the JSON
/// fixture drives placement / binding / playback names only.
/// </summary>
internal static class RigAnimateMapTests
{
	sealed class TestBrush
	{
		public string Name = "";
		public TestBrush( string name ) { Name = name; }
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "riganimatemap", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void RequireNear( float actual, float expected, float tolerance, string what )
		{
			Require( MathF.Abs( actual - expected ) <= tolerance, $"{what}: expected {expected}, got {actual} (tolerance {tolerance})" );
		}
		PropClipException ThrowsProp( Action action, string code )
		{
			try { action(); }
			catch ( PropClipException ex )
			{
				Require( ex.Code == code, $"expected PropClipException {code}, got {ex.Code}: {ex.Message}" );
				return ex;
			}
			throw new Exception( $"expected PropClipException {code}, got silent success" );
		}

		string RepoRoot()
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

		string CorePath() => Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" );
		string Fixture( string name ) => Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-interop-rig-animate-map-props", name );
		EntityRegistry LoadCore() => EntityRegistry.Load( CorePath() );
		MapEntities NewEntities() => new MapEntities( LoadCore() );

		RigPose At( Vec3 pos ) => new( pos, Quat.Identity, Vec3.One );
		RigPose AtRot( Vec3 pos, Quat rot ) => new( pos, rot, Vec3.One );

		Skeleton TwoBoneRig()
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			rig.AddBone( "arm", 0, Xform.Translate( new Vec3( 0, 5, 0 ) ) );
			rig.RootBone = "root";
			return rig;
		}

		// A 1-second loop that closes: the root walks out 5 and back, no turn.
		RigAnimFile IdleLoopClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 15, Local = At( new Vec3( 5, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear } } } );
			return clip;
		}

		// A 1-second loop that does NOT close: the root walks +10 in X and turns 30 degrees.
		RigAnimFile WalkClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = AtRot( new Vec3( 10, 0, 0 ), Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), MathF.PI / 6f ) ), Ease = RigEase.Linear } } } );
			return clip;
		}

		// A door swing: the arm rotates 0 -> 90 degrees over 15 frames, no root motion.
		RigAnimFile DoorOpenClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 16 };
			clip.Tracks.Add( new RigTrack { Name = "arm", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = AtRot( new Vec3( 0, 5, 0 ), Quat.Identity ), Ease = RigEase.Linear },
				  new RigKey { Frame = 15, Local = AtRot( new Vec3( 0, 5, 0 ), Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), MathF.PI / 2f ) ), Ease = RigEase.Linear } } } );
			return clip;
		}

		RigAnimFile DoorCloseClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 16 };
			clip.Tracks.Add( new RigTrack { Name = "arm", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = AtRot( new Vec3( 0, 5, 0 ), Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), MathF.PI / 2f ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 15, Local = AtRot( new Vec3( 0, 5, 0 ), Quat.Identity ), Ease = RigEase.Linear } } } );
			return clip;
		}

		Skeleton HipsRig()
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			rig.AddBone( "hips", 0, Xform.Translate( new Vec3( 0, 10, 0 ) ) );
			rig.RootBone = "root";
			return rig;
		}

		// Hips walk +10 in X over 30 frames; the root stands still. Root-motion
		// settings say the travel lives on the hips and belongs on the root.
		RigAnimFile HipsClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip.RootMotion = new RootMotionSettings { Bone = "root", Source = "hips", Enabled = true };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear } } } );
			clip.Tracks.Add( new RigTrack { Name = "hips", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 10, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( new Vec3( 10, 10, 0 ) ), Ease = RigEase.Linear } } } );
			return clip;
		}

		// Root walks +10 in X over 30 frames; the travel is already on the root.
		RigAnimFile RootWalkClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip.RootMotion = new RootMotionSettings { Bone = "root", Enabled = true };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( new Vec3( 10, 0, 0 ) ), Ease = RigEase.Linear } } } );
			return clip;
		}

		ClipLibrary Library( params (string Name, RigAnimFile Clip)[] clips )
		{
			var library = new ClipLibrary();
			foreach ( var (name, clip) in clips )
				library.Add( new ClipLibraryEntry( name, clip ) );
			return library;
		}

		MapEntity PlaceProp( MapEntities entities, string targetName, string? defaultAnim = "guard_idle" )
		{
			var keyvalues = new Dictionary<string, string>
			{
				["model"] = "models/props/guard.mdl",
				["targetname"] = targetName,
			};
			if ( defaultAnim is not null ) keyvalues["DefaultAnim"] = defaultAnim;
			return entities.PlacePointEntity( "prop_dynamic", new Vec3( 0, 0, 0 ), keyvalues );
		}

		(MapEntities Entities, PropClipBindings Bindings, MapEntity Door) PlaceDoor( MapEntities entities, ClipLibrary clips,
			string targetName = "door_01" )
		{
			var door = entities.PlaceBrushEntity( "func_door",
				new[] { new TestBrush( "panel-a" ), new TestBrush( "panel-b" ) },
				new Dictionary<string, string> { ["targetname"] = targetName, ["speed"] = "100" },
				new Vec3( 128, 0, 0 ) );
			var bindings = new PropClipBindings();
			bindings.BindInput( door, "Open", "door_open", clips );
			bindings.BindInput( door, "Close", "door_close", clips );
			return ( entities, bindings, door );
		}

		ClipLibrary StandardLibrary() => Library(
			( "guard_idle", IdleLoopClip() ),
			( "guard_walk", WalkClip() ),
			( "door_open", DoorOpenClip() ),
			( "door_close", DoorCloseClip() ),
			( "hips_walk", HipsClip() ),
			( "root_walk", RootWalkClip() ) );

		// ---- Validation ----

		Check( "prop_dynamic with a looping default clip validates clean", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var prop = PlaceProp( entities, "guard_01" );
			var bindings = new PropClipBindings();
			var bound = bindings.BindDefault( prop, clips, loop: true );
			Require( bound is not null, "BindDefault must return the default binding" );
			Require( bound.ClipName == "guard_idle", $"default clip is '{bound.ClipName}', expected 'guard_idle'" );
			Require( bound.Model == "models/props/guard.mdl", $"model is '{bound.Model}'" );
			var issues = PropClipValidator.Validate( entities, bindings, clips, rig );
			Require( issues.Count == 0, $"expected no issues, got: {string.Join( "; ", issues.Select( i => i.ToString() ) )}" );
		} );

		Check( "prop_dynamic with an unknown default clip is an UnknownClip error", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var prop = PlaceProp( entities, "guard_01", defaultAnim: "no_such_clip" );
			var bindings = new PropClipBindings();
			ThrowsProp( () => bindings.BindDefault( prop, clips ), PropClipException.CodeUnknownClip );
			var issues = PropClipValidator.Validate( entities, bindings, clips, rig );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}" );
			Require( issues[0].Code == PropClipIssue.CodeUnknownClip, $"code is {issues[0].Code}, expected UnknownClip" );
			Require( issues[0].Severity == PropClipIssueSeverity.Error, "an unknown clip must be an error" );
			Require( issues[0].EntityId == prop.Id, "the issue must name the prop" );
		} );

		Check( "looping clip with a popping seam is a LoopSeamPops error; one-shot is not", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var prop = PlaceProp( entities, "guard_01", defaultAnim: "guard_walk" );
			var bindings = new PropClipBindings();
			bindings.BindDefault( prop, clips, loop: true );
			var issues = PropClipValidator.Validate( entities, bindings, clips, rig );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}: {string.Join( "; ", issues.Select( i => i.ToString() ) )}" );
			Require( issues[0].Code == PropClipIssue.CodeLoopSeamPops, $"code is {issues[0].Code}, expected LoopSeamPops" );

			var once = new PropClipBindings();
			once.BindDefault( prop, clips, loop: false );
			var onceIssues = PropClipValidator.Validate( entities, once, clips, rig );
			Require( onceIssues.Count == 0, $"a one-shot clip needs no seam check, got: {string.Join( "; ", onceIssues.Select( i => i.ToString() ) )}" );
		} );

		Check( "looping default clip wraps on Advance and keeps playing", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var prop = PlaceProp( entities, "guard_01" );
			var bindings = new PropClipBindings();
			bindings.BindDefault( prop, clips, loop: true );
			var player = new PropClipPlayer( entities, clips, bindings, rig );
			var state = player.Fire( prop.Id, "TurnOn" );
			Require( state.Playing, "the default clip must be playing after TurnOn" );
			Require( state.ClipName == "guard_idle", $"playing '{state.ClipName}', expected 'guard_idle'" );
			player.Advance( prop.Id, 45 );
			Require( state.Playing, "a looping clip must keep playing past its last frame" );
			Require( state.Frame == 14, $"frame is {state.Frame}, expected 14 (45 wraps over a 31-frame loop)" );
			var pose = player.Sample( prop.Id, rig );
			Require( pose is not null && pose.Length == 2, "Sample must return the played pose while looping" );
		} );

		Check( "prop_static and prop_physics cannot bind a clip", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var bindings = new PropClipBindings();
			var @static = entities.PlacePointEntity( "prop_static", new Vec3( 0, 0, 0 ),
				new Dictionary<string, string> { ["model"] = "models/props_c17/oildrum001.mdl" } );
			ThrowsProp( () => bindings.BindDefault( @static, clips ), PropClipException.CodeNotAnimatableProp );
			ThrowsProp( () => bindings.BindInput( @static, "Open", "door_open", clips ), PropClipException.CodeNotAnimatableProp );
			var physics = entities.PlacePointEntity( "prop_physics", new Vec3( 0, 0, 0 ),
				new Dictionary<string, string> { ["model"] = "models/props/crate.mdl" } );
			ThrowsProp( () => bindings.BindInput( physics, "Wake", "guard_idle", clips ), PropClipException.CodeNotAnimatableProp );
		} );

		Check( "prop_dynamic with no model warns MissingModel but still validates its clip", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var prop = entities.PlacePointEntity( "prop_dynamic", new Vec3( 0, 0, 0 ),
				new Dictionary<string, string> { ["targetname"] = "guard_01", ["DefaultAnim"] = "guard_idle" } );
			var bindings = new PropClipBindings();
			bindings.BindDefault( prop, clips, loop: true );
			var issues = PropClipValidator.Validate( entities, bindings, clips, rig );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}" );
			Require( issues[0].Code == PropClipIssue.CodeMissingModel, $"code is {issues[0].Code}, expected MissingModel" );
			Require( issues[0].Severity == PropClipIssueSeverity.Warning, "a missing model must be a warning, not an error" );
		} );

		// ---- Door / mover bindings ----

		Check( "door Open and Close bind and validate; unknown input and unknown clip rejected", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var (map, bindings, door) = PlaceDoor( entities, clips );
			Require( bindings.Count == 2, $"expected 2 bindings, got {bindings.Count}" );
			Require( bindings.Find( door.Id, "Open" )?.ClipName == "door_open", "Open must bind door_open" );
			Require( bindings.Find( door.Id, "Close" )?.ClipName == "door_close", "Close must bind door_close" );
			var issues = PropClipValidator.Validate( map, bindings, clips, rig );
			Require( issues.Count == 0, $"expected no issues, got: {string.Join( "; ", issues.Select( i => i.ToString() ) )}" );
			ThrowsProp( () => bindings.BindInput( door, "Fly", "door_open", clips ), PropClipException.CodeUnknownInput );
			ThrowsProp( () => bindings.BindInput( door, "Open", "no_such_clip", clips ), PropClipException.CodeUnknownClip );
			ThrowsProp( () => bindings.BindInput( door, "Lock", "door_open", clips ), PropClipException.CodeUnknownInput );
		} );

		Check( "door with an open clip plays the clip on the Open input", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var (map, bindings, door) = PlaceDoor( entities, clips );
			var player = new PropClipPlayer( map, clips, bindings, rig );
			var state = player.Fire( new EntityIoInput( "door_01", "Open" ) );
			Require( state.Playing, "the open clip must be playing after the Open input" );
			Require( state.ClipName == "door_open", $"playing '{state.ClipName}', expected 'door_open'" );
			Require( state.Frame == 0, $"frame is {state.Frame}, expected 0" );
			var pose = player.Sample( door.Id, rig );
			Require( pose is not null && pose.Length == 2, "Sample must return the played pose" );
			Require( pose[1].Origin.AlmostEquals( new Vec3( 0, 5, 0 ) ), $"arm origin is {pose[1].Origin}, expected (0, 5, 0)" );
			player.Advance( door.Id, 20 );
			Require( state.Finished, "the one-shot open clip must finish past its last frame" );
			Require( !state.Playing, "a finished one-shot must stop playing" );
			Require( state.Frame == 15, $"frame is {state.Frame}, expected the last frame 15" );
			Require( state.DoorOpen, "the door must end open" );
			Require( state.LastFiredOutput == "OnFullyOpen", $"fired output is '{state.LastFiredOutput}', expected 'OnFullyOpen'" );
		} );

		Check( "door Close after Open ends closed and fires OnFullyClosed", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var (map, bindings, door) = PlaceDoor( entities, clips );
			var player = new PropClipPlayer( map, clips, bindings, rig );
			player.Fire( new EntityIoInput( "door_01", "Open" ) );
			player.Advance( door.Id, 20 );
			Require( player.State( door.Id ).DoorOpen, "the door must be open before Close" );
			var state = player.Fire( new EntityIoInput( "door_01", "Close" ) );
			Require( state.ClipName == "door_close", $"playing '{state.ClipName}', expected 'door_close'" );
			player.Advance( door.Id, 20 );
			Require( state.Finished, "the close clip must finish" );
			Require( !state.DoorOpen, "the door must end closed" );
			Require( state.LastFiredOutput == "OnFullyClosed", $"fired output is '{state.LastFiredOutput}', expected 'OnFullyClosed'" );
		} );

		Check( "door Toggle alternates open and close", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var (map, bindings, door) = PlaceDoor( entities, clips );
			var player = new PropClipPlayer( map, clips, bindings, rig );
			var first = player.Fire( new EntityIoInput( "door_01", "Toggle" ) );
			Require( first.ClipName == "door_open", $"first Toggle must open, playing '{first.ClipName}'" );
			player.Advance( door.Id, 20 );
			var second = player.Fire( new EntityIoInput( "door_01", "Toggle" ) );
			Require( second.ClipName == "door_close", $"second Toggle must close, playing '{second.ClipName}'" );
			player.Advance( door.Id, 20 );
			Require( !player.State( door.Id ).DoorOpen, "the door must end closed after Toggle, Toggle" );
		} );

		Check( "door Open with no binding is a NoBinding error", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var door = entities.PlaceBrushEntity( "func_door",
				new[] { new TestBrush( "panel-a" ) },
				new Dictionary<string, string> { ["targetname"] = "door_01" },
				new Vec3( 128, 0, 0 ) );
			var player = new PropClipPlayer( entities, clips, new PropClipBindings(), rig );
			ThrowsProp( () => player.Fire( new EntityIoInput( "door_01", "Open" ) ), PropClipException.CodeNoBinding );
			Require( door.Id > 0, "door placement must succeed before the binding check" );
		} );

		// ---- I/O -> clip playback on props ----

		Check( "SetAnimation plays the clip its parameter names; TurnOff stops it", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var prop = PlaceProp( entities, "guard_01" );
			var bindings = new PropClipBindings();
			bindings.BindDefault( prop, clips, loop: true );
			var player = new PropClipPlayer( entities, clips, bindings, rig );
			var state = player.Fire( new EntityIoInput( "guard_01", "SetAnimation", "guard_walk" ) );
			Require( state.Playing, "SetAnimation must start playback" );
			Require( state.ClipName == "guard_walk", $"playing '{state.ClipName}', expected 'guard_walk'" );
			Require( !state.Loop, "a SetAnimation clip plays once unless bound looping" );
			var stopped = player.Fire( new EntityIoInput( "guard_01", "TurnOff" ) );
			Require( !stopped.Playing, "TurnOff must stop playback" );
			ThrowsProp( () => player.Fire( new EntityIoInput( "guard_01", "SetAnimation", "no_such_clip" ) ),
				PropClipException.CodeUnknownClip );
		} );

		Check( "SetAnimation on a door is an UnknownInput error", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var (map, bindings, _) = PlaceDoor( entities, clips );
			var player = new PropClipPlayer( map, clips, bindings, rig );
			ThrowsProp( () => player.Fire( new EntityIoInput( "door_01", "SetAnimation", "door_open" ) ),
				PropClipException.CodeUnknownInput );
		} );

		Check( "SetDefaultAnimation re-points DefaultAnim and rebinds the default clip", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var prop = PlaceProp( entities, "guard_01", defaultAnim: null );
			var bindings = new PropClipBindings();
			Require( bindings.BindDefault( prop, clips ) is null, "no DefaultAnim means no default binding" );
			var player = new PropClipPlayer( entities, clips, bindings, rig );
			player.Fire( new EntityIoInput( "guard_01", "SetDefaultAnimation", "guard_idle" ) );
			Require( entities.GetKeyvalue( prop, "DefaultAnim" ) == "guard_idle",
				$"DefaultAnim is '{entities.GetKeyvalue( prop, "DefaultAnim" )}', expected 'guard_idle'" );
			Require( bindings.Find( prop.Id, "" )?.ClipName == "guard_idle", "the default binding must follow the new DefaultAnim" );
			var issues = PropClipValidator.Validate( entities, bindings, clips, rig );
			Require( issues.Count == 0, $"expected no issues, got: {string.Join( "; ", issues.Select( i => i.ToString() ) )}" );
		} );

		Check( "Fire to a missing target is UnknownTarget; a shared targetname is AmbiguousTarget", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var (map, bindings, _) = PlaceDoor( entities, clips );
			var player = new PropClipPlayer( map, clips, bindings, rig );
			ThrowsProp( () => player.Fire( new EntityIoInput( "no_such_door", "Open" ) ), PropClipException.CodeUnknownTarget );
			map.PlaceBrushEntity( "func_door",
				new[] { new TestBrush( "panel-c" ) },
				new Dictionary<string, string> { ["targetname"] = "door_01" },
				new Vec3( 256, 0, 0 ) );
			ThrowsProp( () => player.Fire( new EntityIoInput( "door_01", "Open" ) ), PropClipException.CodeAmbiguousTarget );
			var byId = player.Fire( new EntityIoInput( "#1", "Open" ) );
			Require( byId.ClipName == "door_open", $"#1 must resolve to the first door, playing '{byId.ClipName}'" );
		} );

		// ---- Root motion + export ----

		Check( "root motion extract moves hips travel onto the root; strip plays in place", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = HipsRig();
			var prop = PlaceProp( entities, "guard_01", defaultAnim: "hips_walk" );
			var bindings = new PropClipBindings();
			bindings.BindDefault( prop, clips, rootMotion: PropClipBinding.RootMotionExtract );
			var player = new PropClipPlayer( entities, clips, bindings, rig );
			var state = player.Fire( prop.Id, "TurnOn" );
			player.Advance( prop.Id, 30 );
			var pose = player.Sample( prop.Id, rig );
			Require( pose is not null, "Sample must return a pose after extract" );
			RequireNear( pose[0].Origin.x, 10f, 0.05f, "extracted root travel at the last frame" );
			RequireNear( pose[1].Origin.x, 0f, 0.05f, "hips travel after extract at the last frame" );

			var stripProp = PlaceProp( entities, "guard_02", defaultAnim: "root_walk" );
			var stripBindings = new PropClipBindings();
			stripBindings.BindDefault( stripProp, clips, rootMotion: PropClipBinding.RootMotionStrip );
			var stripPlayer = new PropClipPlayer( entities, clips, stripBindings, rig );
			var stripState = stripPlayer.Fire( stripProp.Id, "TurnOn" );
			Require( stripState.Clip.RootMotion is null, "a stripped clip must carry no root-motion settings" );
			stripPlayer.Advance( stripProp.Id, 30 );
			var inPlace = stripPlayer.Sample( stripProp.Id, rig );
			Require( inPlace is not null, "Sample must return a pose after strip" );
			RequireNear( inPlace[0].Origin.x, 0f, 0.05f, "stripped root travel at the last frame" );
		} );

		Check( "export manifest lists the prop and the door with clips, frames, root bone and seam", () =>
		{
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var prop = PlaceProp( entities, "guard_01" );
			var bindings = new PropClipBindings();
			bindings.BindDefault( prop, clips, loop: true );
			var (map, doorBindings, door) = PlaceDoor( entities, clips );
			foreach ( var b in doorBindings.All ) bindings.BindInput( door, b.Input, b.ClipName, clips );
			var physics = entities.PlacePointEntity( "prop_physics", new Vec3( 0, 0, 0 ),
				new Dictionary<string, string> { ["model"] = "models/props/crate.mdl" } );
			var rows = PropClipExport.Manifest( map, bindings, clips, rig );
			Require( rows.Count == 3, $"expected 3 rows (prop default + door open + door close), got {rows.Count}" );
			var propRow = rows.FirstOrDefault( r => r.EntityId == prop.Id );
			Require( propRow is not null, "the manifest must list the prop" );
			Require( propRow.Model == "models/props/guard.mdl", $"prop model is '{propRow.Model}'" );
			Require( propRow.ClipName == "guard_idle", $"prop clip is '{propRow.ClipName}'" );
			Require( propRow.Frames == 31, $"prop frames are {propRow.Frames}, expected 31" );
			Require( propRow.FrameRate == 30, $"prop frame rate is {propRow.FrameRate}, expected 30" );
			Require( propRow.RootBone == "root", $"prop root bone is '{propRow.RootBone}', expected 'root'" );
			Require( propRow.SeamPass, "the clean looping clip must pass its seam in the manifest" );
			var openRow = rows.FirstOrDefault( r => r.EntityId == door.Id && r.Input == "Open" );
			Require( openRow is not null, "the manifest must list the door's Open clip" );
			Require( openRow.ClipName == "door_open", $"door Open clip is '{openRow.ClipName}'" );
			Require( openRow.Frames == 16, $"door Open frames are {openRow.Frames}, expected 16" );
			Require( openRow.Model == "", "a func_door has no model keyvalue" );
			Require( rows.All( r => r.EntityId != physics.Id ), "an unbound prop_physics must not appear in the manifest" );
		} );

		// ---- Fixture-driven end to end ----

		Check( "fixture props.json drives placement, binding, validation and door playback", () =>
		{
			var path = Fixture( "props.json" );
			Require( File.Exists( path ), $"fixture missing: {path}" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var entities = NewEntities();
			var clips = StandardLibrary();
			var rig = TwoBoneRig();
			var byTarget = new Dictionary<string, MapEntity>( StringComparer.OrdinalIgnoreCase );

			foreach ( var element in root.GetProperty( "entities" ).EnumerateArray() )
			{
				var className = element.GetProperty( "class" ).GetString()!;
				var targetName = element.GetProperty( "targetname" ).GetString()!;
				var origin = element.GetProperty( "origin" );
				var at = new Vec3( origin[0].GetSingle(), origin[1].GetSingle(), origin[2].GetSingle() );
				var keyvalues = new Dictionary<string, string> { ["targetname"] = targetName };
				foreach ( var pair in element.GetProperty( "keyvalues" ).EnumerateObject() )
					keyvalues[pair.Name] = pair.Value.GetString()!;
				MapEntity placed;
				if ( element.TryGetProperty( "brushes", out var brushCount ) )
				{
					var brushes = Enumerable.Range( 0, brushCount.GetInt32() ).Select( i => new TestBrush( $"panel-{i}" ) ).ToArray();
					placed = entities.PlaceBrushEntity( className, brushes, keyvalues, at );
				}
				else
				{
					placed = entities.PlacePointEntity( className, at, keyvalues );
				}
				byTarget[targetName] = placed;
			}

			var bindings = new PropClipBindings();
			foreach ( var element in root.GetProperty( "bindings" ).EnumerateArray() )
			{
				var target = element.GetProperty( "target" ).GetString()!;
				var input = element.GetProperty( "input" ).GetString()!;
				var loop = element.TryGetProperty( "loop", out var loopElement ) && loopElement.GetBoolean();
				if ( input.Length == 0 ) bindings.BindDefault( byTarget[target], clips, loop: loop );
				else bindings.BindInput( byTarget[target], input, element.GetProperty( "clip" ).GetString()!, clips, loop: loop );
			}

			var issues = PropClipValidator.Validate( entities, bindings, clips, rig );
			Require( issues.Count == 0, $"fixture must validate clean, got: {string.Join( "; ", issues.Select( i => i.ToString() ) )}" );

			var player = new PropClipPlayer( entities, clips, bindings, rig );
			var state = player.Fire( new EntityIoInput( "door_01", "Open" ) );
			Require( state.ClipName == "door_open", $"fixture door plays '{state.ClipName}', expected 'door_open'" );
			player.Advance( byTarget["door_01"].Id, 20 );
			Require( state.Finished && state.DoorOpen, "the fixture door must end open" );
			Require( state.LastFiredOutput == "OnFullyOpen", $"fixture door fires '{state.LastFiredOutput}', expected 'OnFullyOpen'" );

			var rows = PropClipExport.Manifest( entities, bindings, clips, rig );
			Require( rows.Count == 3, $"fixture manifest must have 3 rows, got {rows.Count}" );
		} );
	}
}
