#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Headless test of the Animate workspace (tools/animate_test.gd): takes, keys, save/reload, clip
/// switching, live GLB export with an unsaved key, shape-key keying, morph/spawn events and joint limits.
/// Works on a copy of the g1-spike fixture under user://animate_test/.
/// </summary>
public partial class AnimateTest : RefCounted
{
	const string Fixtures = "res://addons/geppetto/tools/fixtures/";
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	void Add( string name, bool ok, string value = "" )
	{
		_checks.Add( new Godot.Collections.Dictionary { { "name", name }, { "ok", ok }, { "value", value } } );
		if ( !ok ) _failed++;
	}

	public Godot.Collections.Dictionary Run()
	{
		try { Body(); }
		catch ( Exception e ) { Add( "exception", false, e.ToString() ); }
		return new Godot.Collections.Dictionary { { "checks", _checks }, { "failed", _failed }, { "total", _checks.Count } };
	}

	static string Copy()
	{
		string dir = ProjectSettings.GlobalizePath( "user://animate_test" );
		if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
		Directory.CreateDirectory( dir );
		foreach ( var f in new[] { "g1-spike.effigy", "g1-spike_spin.riganim", "g1-spike_wheel-rest.riganim" } )
			File.Copy( ProjectSettings.GlobalizePath( Fixtures + f ), Path.Combine( dir, f ) );
		return Path.Combine( dir, "g1-spike.effigy" );
	}

	static RigKey? KeyAt( RigAnimFile clip, string bone, int frame, RigTrackKind kind = RigTrackKind.Bone ) =>
		clip.Tracks.FirstOrDefault( t => t.Name == bone && t.Kind == kind )?.Keys.FirstOrDefault( k => k.Frame == frame );

	static float Angle( Quat a, Quat b ) => MathF.Acos( Math.Clamp( MathF.Abs( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w ), 0f, 1f ) ) * 2f;

	void Body()
	{
		string effigy = Copy();
		var root = new EffigySceneImporter()._ImportScene( effigy, 0, new Godot.Collections.Dictionary() ) as Node3D
			?? throw new Exception( "import failed" );
		( (SceneTree)Engine.GetMainLoop() ).Root.AddChild( root );
		var c = GeppettoCharacter.For( root, effigy );
		Add( "character has document + rig", c.Doc != null && c.Rig is { Count: > 1 } && c.Skeleton != null, $"bones={c.BoneNames.Count}" );
		var doc = c.Doc!;
		string dir = Path.GetDirectoryName( effigy )!;

		// ---- 1. New take, key two bones, save, reload ------------------------------------------------
		var takePath = c.NewTake();
		Add( "new take file next to the .effigy", takePath != null && File.Exists( takePath ) && Path.GetDirectoryName( takePath ) == dir, takePath ?? "null" );
		Add( "new take is current and listed", c.ClipName == "take1" && c.Takes().Contains( takePath ), $"{c.ClipName} / {string.Join( ",", c.Takes().Select( c.TakeName ) )}" );

		string boneA = c.BoneNames[0], boneB = c.BoneNames[1];
		var rotA = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), 0.6f );
		var rotB = Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), -0.4f );
		c.ClipEdit( ClipEditService.BuildInsertOrReplaceKey( c.ClipSession!, RigTrackKind.Bone, boneA, 10, new RigPose( Vec3.Zero, rotA, Vec3.One ) ) );
		c.ClipEdit( ClipEditService.BuildInsertOrReplaceKey( c.ClipSession!, RigTrackKind.Bone, boneB, 20, new RigPose( Vec3.Zero, rotB, Vec3.One ) ) );
		Add( "two keys in the live take, take dirty", KeyAt( c.Clip!, boneA, 10 ) != null && KeyAt( c.Clip!, boneB, 20 ) != null && c.ClipSession!.IsDirty );

		var saved = doc.Bridge.SaveOne( c.ClipSession! );
		var reread = RigAnimFile.ReadFile( takePath! );
		var ka = KeyAt( reread, boneA, 10 );
		var kb = KeyAt( reread, boneB, 20 );
		Add( "save writes the take", saved.Outcome == EffigySaveOutcome.Written && !c.ClipSession!.IsDirty, saved.Outcome + " " + saved.Message );
		Add( "reloaded take has both keys", ka != null && kb != null && Angle( ka.Local.Rotation, rotA ) < 1e-3f && Angle( kb.Local.Rotation, rotB ) < 1e-3f,
			ka == null || kb == null ? "missing" : $"dA={Angle( ka.Local.Rotation, rotA ):0.0000} dB={Angle( kb.Local.Rotation, rotB ):0.0000}" );

		// ---- 2. Switch clips -----------------------------------------------------------------------
		string spin = Path.Combine( dir, "g1-spike_spin.riganim" );
		bool sw = c.SwitchClip( spin );
		Add( "switch to spin", sw && c.ClipName == "spin" && c.Clip!.Tracks.Count > 0 && KeyAt( c.Clip, boneA, 10 ) == null, $"{c.ClipName} tracks={c.Clip?.Tracks.Count}" );
		c.SwitchClip( takePath! );
		Add( "switch back to take1 keeps its keys", c.ClipName == "take1" && KeyAt( c.Clip!, boneA, 10 ) != null && KeyAt( c.Clip!, boneB, 20 ) != null, c.ClipName );

		// ---- 3. GLB export carries an unsaved key ---------------------------------------------------
		var rotU = Quat.FromAxisAngle( new Vec3( 0, 1, 0 ), 1.1f );
		c.ClipEdit( ClipEditService.BuildInsertOrReplaceKey( c.ClipSession!, RigTrackKind.Bone, boneA, 30, new RigPose( Vec3.Zero, rotU, Vec3.One ) ) );
		Add( "unsaved key not on disk", KeyAt( RigAnimFile.ReadFile( takePath! ), boneA, 30 ) == null && c.ClipSession!.IsDirty );
		string glb = Path.Combine( dir, "out.glb" );
		// Through the old entry point: an open document must export live.
		var err = EffigyGlbExporter.ExportEffigy( effigy, glb );
		Add( "GLB written", err == Error.Ok && File.Exists( glb ), err.ToString() );
		CheckGlb( glb, c, boneA, 30 );

		// OBJ / FBX
		string obj = Path.Combine( dir, "out.obj" ), fbx = Path.Combine( dir, "out.fbx" );
		var eo = AnimateExport.ExportObj( doc, c.ClipSession, 30, obj );
		Add( "OBJ export (posed at frame 30)", eo == null && File.Exists( obj ) && new FileInfo( obj ).Length > 100, eo ?? $"{new FileInfo( obj ).Length} bytes" );
		string bindObj = Path.Combine( dir, "bind.obj" );
		var eb = AnimateExport.ExportObj( doc, null, 0, bindObj );
		static string Verts( string p ) => string.Join( "\n", File.ReadLines( p ).Where( l => l.StartsWith( "v " ) ) );
		Add( "posed OBJ differs from bind OBJ", eo == null && eb == null && Verts( obj ) != Verts( bindObj ), eb ?? "" );
		var ef = AnimateExport.ExportFbx( doc, fbx );
		Add( "FBX export (binary)", ef == null && File.Exists( fbx ) && File.ReadAllBytes( fbx ).Take( 18 ).SequenceEqual( System.Text.Encoding.ASCII.GetBytes( "Kaydara FBX Binary" ) ), ef ?? "" );

		// ---- 4. Shape key slider keys a weight -------------------------------------------------------
		var sk = new ShapeKeyFeature();
		sk.Key.Value = "smile";
		doc.AddFeature( sk );
		doc.AddFeature( new SimpleDeformFeature() );
		doc.AddFeature( new ShapeKeyEndFeature() );
		var morphs = c.MorphNames();
		Add( "model has the smile shape key", morphs.Contains( "smile" ), string.Join( ",", morphs ) );
		bool keyed = AnimateShapeKeys.KeyWeight( c, "smile", 12, 0.75f );
		var w = c.Clip!.MorphWeightsAt( 12 );
		Add( "shape key slider keys the weight", keyed && w.TryGetValue( "smile", out var wv ) && MathF.Abs( wv - 0.75f ) < 1e-4f, w.TryGetValue( "smile", out var wv2 ) ? wv2.ToString( "0.000" ) : "none" );
		var panel = new AnimateShapeKeys();
		( (SceneTree)Engine.GetMainLoop() ).Root.AddChild( panel );
		panel.Show( c, 12 );
		Add( "Shape Keys panel lists the key", panel.FindChildren( "*", nameof( HSlider ), true, false ).Count == morphs.Count, $"{morphs.Count}" );
		panel.QueueFree();

		// ---- 5. Events and joint limit, saved with the take -------------------------------------------
		bool me = AnimateTools.AddMorphEvent( c, new RigMorphEvent { Morph = "smile", Start = 40, End = 50, Value = 0.5f } );
		Add( "morph event drives the weight", me && MathF.Abs( ( c.Clip!.MorphWeightsAt( 45 ).TryGetValue( "smile", out var w45 ) ? w45 : 0f ) - 0.5f ) < 1e-4f );
		bool se = AnimateTools.AddSpawnEvent( c, new AnimateSpawnEvent( "Sword", 5, 25, "res://props/sword.tscn", boneB ) );
		Add( "spawn event listed", se && AnimateTools.Events( c ).Any( e => !e.Morph && e.Start == 5 && e.Label.Contains( "sword.tscn" ) ) );
		bool lim = AnimateTools.SetJointLimit( c, boneB, new Vector3( -10, -10, -10 ), new Vector3( 10, 10, 10 ) );
		string ctrl = Path.Combine( dir, "g1-spike.ctrlrig" );
		Add( "joint limit writes the .ctrlrig and loads it", lim && File.Exists( ctrl ) && c.Clip!.Limits?.Any( l => l.Bone == boneB && l.Enabled ) == true && c.Clip.RigAsset == "g1-spike.ctrlrig" );
		c.ClipSession!.History.Undo();
		Add( "undo joint limit removes the .ctrlrig", !File.Exists( ctrl ) && c.Clip!.RigAsset == "" );
		c.ClipSession.History.Redo();

		doc.Bridge.SaveOne( c.ClipSession );
		var back = RigAnimFile.ReadFile( takePath! );
		string json = File.ReadAllText( takePath! );
		Add( "saved take keeps keys, morph key, events, rig",
			KeyAt( back, boneA, 30 ) != null && KeyAt( back, "smile", 12, RigTrackKind.Morph ) != null && back.MorphEvents.Count == 1
			&& json.Contains( "\"PropModel\": \"res://props/sword.tscn\"" ) && back.RigAsset == "g1-spike.ctrlrig",
			$"morphKey={KeyAt( back, "smile", 12, RigTrackKind.Morph ) != null} events={back.MorphEvents.Count} rig={back.RigAsset}" );

		root.QueueFree();
	}

	/// <summary>The GLB's take1 rotation for <paramref name="bone"/> at <paramref name="frame"/> must be the
	/// live clip's (with the unsaved key), not the saved file's.</summary>
	void CheckGlb( string glb, GeppettoCharacter c, string bone, int frame )
	{
		var gltf = new GltfDocument();
		var state = new GltfState();
		var e = gltf.AppendFromFile( glb, state );
		var scene = e == Error.Ok ? gltf.GenerateScene( state ) : null;
		var player = scene?.FindChildren( "*", nameof( AnimationPlayer ), true, false ).OfType<AnimationPlayer>().FirstOrDefault();
		var anim = player?.HasAnimation( "take1" ) == true ? player.GetAnimation( "take1" ) : null;
		Add( "GLB has the take1 animation", anim != null, player == null ? "no player" : string.Join( ",", player.GetAnimationList() ) );
		if ( anim == null || scene == null ) { scene?.Free(); return; }

		int Track( Animation a ) => Enumerable.Range( 0, a.GetTrackCount() )
			.FirstOrDefault( i => a.TrackGetType( i ) == Animation.TrackType.Rotation3D && a.TrackGetPath( i ).ToString().EndsWith( ":" + bone ), -1 );
		double t = frame / (double)c.Fps;
		int ti = Track( anim );
		var got = ti >= 0 ? anim.RotationTrackInterpolate( ti, t ) : Quaternion.Identity;

		var live = EffigyClipBridge.ToGodotAnimation( c.Clip!, c.Rig!, "Skeleton3D", "Skeleton3D/Body" );
		var disk = EffigyClipBridge.ToGodotAnimation( RigAnimFile.ReadFile( c.ClipDiskPath! ), c.Rig!, "Skeleton3D", "Skeleton3D/Body" );
		var want = live.RotationTrackInterpolate( Track( live ), t );
		var old = disk.RotationTrackInterpolate( Track( disk ), t );
		float dLive = got.AngleTo( want ), dDisk = want.AngleTo( old );
		Add( "GLB rotation at the unsaved key = live clip", ti >= 0 && dLive < 1e-3f, $"track={ti} d={dLive:0.00000}" );
		Add( "unsaved key changes the pose (not the saved file)", dDisk > 0.05f && got.AngleTo( old ) > 0.05f, $"d={dDisk:0.000}" );
		scene.Free();
	}
}
#endif
