#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

public static partial class GeppettoTools
{
	static partial void RegisterAnimate()
	{
		Register( "Animate", "Joint limit", AnimateTools.JointLimitBox );
		Register( "Animate", "First Person View", AnimateTools.FirstPersonView );
		Register( "Animate", "Spawn model", AnimateTools.SpawnModelBox );
		Register( "Animate", "Morph event", AnimateTools.MorphEventBox );
		Register( "Animate", "Export Animation…", ctx => AnimateExport.ShowDialog( ctx, GeppettoTimeline.CurrentFrame ) );
		Register( "Animate", "Export Animation", ctx => AnimateExport.ShowDialog( ctx, GeppettoTimeline.CurrentFrame ) );
	}
}

/// <summary>A Spawn model event (Marionette's RigEvent with a PropModel): a model that appears on a bone
/// from StartFrame to EndFrame.</summary>
public sealed record AnimateSpawnEvent( string Name, int Start, int End, string Model, string Bone,
	bool Follow = true, string Side = "Client", Vector3 Offset = default, Vector3 Angles = default, float Scale = 1f );

/// <summary>
/// The Animate tools that are not the dial's basics: joint limits (written to the take's .ctrlrig),
/// First Person View (a camera on the head bone), Spawn model / Morph events (written into the take),
/// all one undo step each, saved by Ctrl+S with the take.
/// </summary>
public static class AnimateTools
{
	static string F( float v ) => v.ToString( "0.###", CultureInfo.InvariantCulture );
	static string V( Vector3 v ) => $"{F( v.X )},{F( v.Y )},{F( v.Z )}";

	// =========================================================================================
	// Events: the take's "Events" / "MorphEvents" lists
	// =========================================================================================

	/// <summary>Edits the take's JSON (what Ctrl+S writes for the parts the kernel does not model) as one
	/// undo step. <paramref name="edit"/> changes the JSON object; <paramref name="apply"/> /
	/// <paramref name="revert"/> change the in-memory clip to match.</summary>
	public static bool EditTake( GeppettoCharacter c, string label, Action<JsonObject> edit, Action<RigAnimFile>? apply = null, Action<RigAnimFile>? revert = null )
	{
		if ( !c.EnsureClip() || c.ClipSession?.Clip is not { } clip ) { GeppettoToolContext.Warn( "This character has no take to edit (it needs a rig)." ); return false; }
		var s = c.ClipSession;
		string oldJson = s.ClipSourceJson ?? RigAnimWriter.Write( clip );
		var root = JsonNode.Parse( oldJson.TrimStart( '﻿' ) )?.AsObject() ?? new JsonObject();
		edit( root );
		string newJson = root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } );
		var cmd = new DelegateEditCommand( label,
			() => { apply?.Invoke( clip ); s.SetClip( s.ClipPath, newJson, clip ); },
			() => { revert?.Invoke( clip ); s.SetClip( s.ClipPath, oldJson, clip ); } );
		return c.ClipEdit( cmd, label );
	}

	static JsonArray List( JsonObject root, string name )
	{
		if ( root[name] is JsonArray a ) return a;
		var n = new JsonArray();
		root[name] = n;
		return n;
	}

	/// <summary>Adds a Morph event (s&amp;box MorphEvent: MorphName, Value, FadeIn, FadeOut over a frame range).</summary>
	public static bool AddMorphEvent( GeppettoCharacter c, RigMorphEvent e, string? name = null )
	{
		if ( string.IsNullOrWhiteSpace( e.Morph ) ) { GeppettoToolContext.Warn( "Morph event: pick a shape key." ); return false; }
		if ( e.End < e.Start ) { GeppettoToolContext.Warn( "Morph event: the end frame is before the start frame." ); return false; }
		return EditTake( c, $"Geppetto: morph event {e.Morph}", root => List( root, "MorphEvents" ).Add( new JsonObject
		{
			["Name"] = name ?? e.Morph,
			["StartFrame"] = e.Start,
			["EndFrame"] = e.End,
			["MorphName"] = e.Morph,
			["Value"] = e.Value,
			["FadeIn"] = e.FadeIn,
			["FadeOut"] = e.FadeOut,
		} ), clip => clip.MorphEvents.Add( e ), clip => clip.MorphEvents.Remove( e ) );
	}

	/// <summary>Adds a Spawn model event (s&amp;box RigEvent with PropModel, AttachBone, FollowBone, offsets).</summary>
	public static bool AddSpawnEvent( GeppettoCharacter c, AnimateSpawnEvent e )
	{
		if ( string.IsNullOrWhiteSpace( e.Model ) ) { GeppettoToolContext.Warn( "Spawn model: choose the model to spawn." ); return false; }
		if ( e.End < e.Start ) { GeppettoToolContext.Warn( "Spawn model: the end frame is before the start frame." ); return false; }
		if ( !string.IsNullOrEmpty( e.Bone ) && !c.BoneNames.Contains( e.Bone ) ) { GeppettoToolContext.Warn( $"Spawn model: no bone {e.Bone}." ); return false; }
		return EditTake( c, $"Geppetto: spawn {Path.GetFileName( e.Model )}", root => List( root, "Events" ).Add( new JsonObject
		{
			["Name"] = string.IsNullOrWhiteSpace( e.Name ) ? "Spawn" : e.Name,
			["StartFrame"] = e.Start,
			["EndFrame"] = e.End,
			["Side"] = e.Side,
			["PropModel"] = e.Model,
			["AttachBone"] = e.Bone,
			["FollowBone"] = e.Follow,
			["PositionOffset"] = V( e.Offset ),
			["RotationOffset"] = V( e.Angles ),
			["ScaleOffset"] = e.Scale,
		} ) );
	}

	/// <summary>The take's events for the Timeline's Events row: (label, start, end, isMorph).</summary>
	public static List<(string Label, int Start, int End, bool Morph)> Events( GeppettoCharacter? c )
	{
		var list = new List<(string, int, int, bool)>();
		if ( c?.ClipSession?.ClipSourceJson is not { } json ) return list;
		try
		{
			using var doc = JsonDocument.Parse( json.TrimStart( '﻿' ) );
			int I( JsonElement e, string n, int d ) => e.TryGetProperty( n, out var v ) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
			string S( JsonElement e, string n ) => e.TryGetProperty( n, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
			if ( doc.RootElement.TryGetProperty( "Events", out var ev ) && ev.ValueKind == JsonValueKind.Array )
				foreach ( var e in ev.EnumerateArray() )
				{
					string model = S( e, "PropModel" );
					list.Add( ( model.Length > 0 ? $"{S( e, "Name" )}: {Path.GetFileName( model )}" : S( e, "Name" ), I( e, "StartFrame", 0 ), I( e, "EndFrame", 1 ), false ) );
				}
			if ( doc.RootElement.TryGetProperty( "MorphEvents", out var me ) && me.ValueKind == JsonValueKind.Array )
				foreach ( var e in me.EnumerateArray() )
					list.Add( ( S( e, "MorphName" ) is { Length: > 0 } m ? m : S( e, "Name" ), I( e, "StartFrame", 0 ), I( e, "EndFrame", 1 ), true ) );
		}
		catch ( JsonException ) { }
		return list;
	}

	// =========================================================================================
	// Joint limit: a Limit constraint in the take's .ctrlrig
	// =========================================================================================

	/// <summary>Adds or replaces the Limit constraint on <paramref name="bone"/> (min/max pitch, yaw, roll in
	/// degrees) in the take's .ctrlrig, creating <c>&lt;base&gt;.ctrlrig</c> next to the model when the take
	/// names none. Scrubbing, IK and export then clamp that bone. One undo step.</summary>
	public static bool SetJointLimit( GeppettoCharacter c, string bone, Vector3 min, Vector3 max )
	{
		if ( !c.EnsureClip() || c.Clip is not { } clip || c.Doc == null ) { GeppettoToolContext.Warn( "Joint limit: this character has no take (it needs a rig)." ); return false; }
		if ( !c.BoneNames.Contains( bone ) ) { GeppettoToolContext.Warn( $"Joint limit: no bone {bone}." ); return false; }
		if ( min.X > max.X || min.Y > max.Y || min.Z > max.Z ) { GeppettoToolContext.Warn( "Joint limit: a minimum is above its maximum." ); return false; }

		string oldAsset = clip.RigAsset ?? "";
		string? path = c.RigAssetPath();
		string asset = oldAsset;
		if ( path == null )
		{
			asset = Path.GetFileNameWithoutExtension( c.Doc.DiskPath ) + ".ctrlrig";
			path = Path.Combine( Path.GetDirectoryName( c.ClipDiskPath ?? c.Doc.DiskPath )!, asset );
		}
		string? oldText = File.Exists( path ) ? File.ReadAllText( path ) : null;
		var root = ( oldText != null ? JsonNode.Parse( oldText.TrimStart( '﻿' ) )?.AsObject() : null ) ?? new JsonObject();
		var limits = List( root, "LimitConstraints" );
		foreach ( var old in limits.OfType<JsonObject>().Where( l => l["TargetBone"]?.GetValue<string>() == bone ).ToList() ) limits.Remove( old );
		limits.Add( new JsonObject
		{
			["Name"] = $"Limit {bone}",
			["TargetBone"] = bone,
			["Weight"] = 1f,
			["Enabled"] = true,
			["MinAngles"] = V( min ),
			["MaxAngles"] = V( max ),
		} );
		string newText = root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } );
		var oldLimits = clip.Limits;

		var cmd = new DelegateEditCommand( $"Geppetto: limit {bone}",
			() =>
			{
				File.WriteAllText( path, newText );
				clip.RigAsset = asset;
				c.LoadRig();
			},
			() =>
			{
				if ( oldText == null ) File.Delete( path ); else File.WriteAllText( path, oldText );
				clip.RigAsset = oldAsset;
				clip.Limits = oldLimits;
				if ( oldText != null ) c.LoadRig();
			} );
		return c.ClipEdit( cmd );
	}

	public static bool JointLimitBox( GeppettoToolContext ctx )
	{
		var c = ctx.Character;
		if ( c?.Skeleton == null ) return ctx.Need();
		if ( !c.EnsureClip() ) { GeppettoToolContext.Warn( "Joint limit: this character has no take (it needs a rig)." ); return true; }
		var selected = EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<BoneAttachment3D>().FirstOrDefault()?.BoneName ?? c.IkEndBone();

		var box = AnimateUi.Box( "Joint limit", "Set limit" );
		var g = AnimateUi.Grid( box );
		var bone = AnimateUi.Row( g, "Bone", AnimateUi.Pick( c.BoneNames, selected ) );
		SpinBox[] lo = new SpinBox[3], hi = new SpinBox[3];
		string[] axes = { "Pitch", "Yaw", "Roll" };
		for ( int i = 0; i < 3; i++ )
		{
			lo[i] = AnimateUi.Row( g, $"{axes[i]} min", AnimateUi.Spin( -45, -180, 180, 1, "°" ) );
			hi[i] = AnimateUi.Row( g, $"{axes[i]} max", AnimateUi.Spin( 45, -180, 180, 1, "°" ) );
		}
		AnimateUi.Note( box, "Written to the take's .ctrlrig as a Limit constraint: posing, IK and export keep the bone inside these angles." );
		box.Confirmed += () =>
		{
			if ( SetJointLimit( c, AnimateUi.Text( bone ),
				new Vector3( (float)lo[0].Value, (float)lo[1].Value, (float)lo[2].Value ),
				new Vector3( (float)hi[0].Value, (float)hi[1].Value, (float)hi[2].Value ) ) )
				GeppettoToolContext.Say( $"Joint limit on {AnimateUi.Text( bone )}" );
		};
		AnimateUi.Show( box );
		return true;
	}

	// =========================================================================================
	// First Person View: a camera riding the head bone
	// =========================================================================================

	public const string FirstPersonName = "First Person View";

	/// <summary>Toggles a camera on the head bone (BoneAttachment3D + Camera3D looking down the model's
	/// forward), saved in the scene, and turns on the viewport's camera preview. Pressing it again removes it.</summary>
	public static bool FirstPersonView( GeppettoToolContext ctx )
	{
		var c = ctx.Character;
		if ( c?.Skeleton == null ) return ctx.Need();
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		if ( root == null ) return ctx.Need();
		var ur = ctx.UndoRedo;

		if ( c.Skeleton.GetNodeOrNull<BoneAttachment3D>( FirstPersonName ) is { } existing )
		{
			ur.CreateAction( "Geppetto: remove First Person View", UndoRedo.MergeMode.Disable, root );
			ur.AddDoMethod( c.Skeleton, Node.MethodName.RemoveChild, existing );
			ur.AddUndoMethod( c.Skeleton, Node.MethodName.AddChild, existing, true );
			ur.AddUndoProperty( existing, Node.PropertyName.Owner, root );
			ur.AddUndoReference( existing );
			ur.CommitAction();
			SetPreview( false );
			GeppettoToast.Info( "First Person View off" );
			return true;
		}

		var head = c.HeadBone();
		if ( head == null ) { GeppettoToolContext.Warn( "First Person View: this character has no bones." ); return true; }
		var (attach, cam) = MakeFirstPerson( c.Skeleton, head );
		ur.CreateAction( "Geppetto: First Person View", UndoRedo.MergeMode.Disable, root );
		ur.AddDoMethod( c.Skeleton, Node.MethodName.AddChild, attach, true );
		ur.AddDoProperty( attach, Node.PropertyName.Owner, root );
		ur.AddDoProperty( cam, Node.PropertyName.Owner, root );
		ur.AddDoReference( attach );
		ur.AddUndoMethod( c.Skeleton, Node.MethodName.RemoveChild, attach );
		ur.CommitAction();

		var sel = EditorInterface.Singleton.GetSelection();
		sel.Clear();
		sel.AddNode( cam );
		// The viewport only learns about the selected camera on its next selection update; ticking Preview
		// before that hits "p_activate && !preview" and leaves the box unticked. Wait a moment.
		if ( Engine.GetMainLoop() is SceneTree tree )
			tree.CreateTimer( 0.15 ).Timeout += () => { if ( GodotObject.IsInstanceValid( cam ) && cam.IsInsideTree() ) SetPreview( true ); };
		GeppettoToast.Info( $"First Person View from {head}: the viewport now looks through the head camera. Press First Person View again to stop." );
		return true;
	}

	/// <summary>The attachment + camera (not yet in the tree): the camera sits a little forward of the head
	/// bone and looks along the model's forward (Effigy +X = Poohdot +Z).</summary>
	public static (BoneAttachment3D Attach, Camera3D Cam) MakeFirstPerson( Skeleton3D skel, string bone )
	{
		var attach = new BoneAttachment3D { Name = FirstPersonName, BoneName = bone };
		var cam = new Camera3D { Name = "Camera", Fov = 75, Near = 0.02f };
		// Camera3D looks down its -Z; turning it half a turn about Y looks down +Z, the model's forward.
		// The attachment follows the bone's rotation, so undo it to keep the view level at rest.
		int b = skel.FindBone( bone );
		var rest = b >= 0 ? skel.GetBoneGlobalRest( b ) : Transform3D.Identity;
		var level = new Transform3D( Basis.FromEuler( new Vector3( 0, Mathf.Pi, 0 ) ), new Vector3( 0, 0.05f, 0.08f ) );
		cam.Transform = new Transform3D( rest.Basis.Inverse(), Vector3.Zero ) * level;
		attach.AddChild( cam );
		return ( attach, cam );
	}

	/// <summary>Presses the 3D viewport's "Preview" check (shown while a camera is selected).</summary>
	static void SetPreview( bool on )
	{
		var vp = EditorInterface.Singleton.GetEditorViewport3D( 0 )?.GetParent()?.GetParent();
		var check = vp == null ? null : FindCheck( vp, "Preview" );
		if ( check == null ) { if ( on ) GeppettoToolContext.Say( "First Person View: tick the viewport's Preview box to look through it" ); return; }
		if ( on && !check.IsVisibleInTree() ) { GeppettoToolContext.Say( "First Person View: tick the viewport's Preview box to look through it" ); return; }
		check.ButtonPressed = on;
	}

	static CheckBox? FindCheck( Node n, string text )
	{
		if ( n is CheckBox cb && cb.Text == text ) return cb;
		foreach ( var ch in n.GetChildren( true ) ) if ( FindCheck( ch, text ) is { } hit ) return hit;
		return null;
	}

	// =========================================================================================
	// Boxes
	// =========================================================================================

	public static bool SpawnModelBox( GeppettoToolContext ctx )
	{
		var c = ctx.Character;
		if ( c?.Skeleton == null ) return ctx.Need();
		if ( !c.EnsureClip() ) { GeppettoToolContext.Warn( "Spawn model: this character has no take (it needs a rig)." ); return true; }
		int frame = GeppettoTimeline.CurrentFrame;

		var box = AnimateUi.Box( "Spawn model", "Add event" );
		var g = AnimateUi.Grid( box );
		var name = AnimateUi.Row( g, "Name", new LineEdit { Text = "Spawn" } );
		var start = AnimateUi.Row( g, "Start frame", AnimateUi.Spin( frame, 0, 100000 ) );
		var end = AnimateUi.Row( g, "End frame", AnimateUi.Spin( frame + 10, 0, 100000 ) );
		var modelRow = new HBoxContainer();
		var model = new LineEdit { PlaceholderText = "res://… .tscn / .glb / .effigy", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2( 240, 0 ) };
		var browse = new Button { Text = "Browse…" };
		modelRow.AddChild( model );
		modelRow.AddChild( browse );
		AnimateUi.Row( g, "Model", modelRow );
		var bone = AnimateUi.Row( g, "Attach bone", AnimateUi.Pick( c.BoneNames, c.IkEndBone() ) );
		var follow = AnimateUi.Row( g, "Follow bone", new CheckBox { ButtonPressed = true, Text = "On" } );
		var side = AnimateUi.Row( g, "Side", AnimateUi.Pick( new[] { "Client", "Server" }, "Client" ) );
		SpinBox[] pos = { AnimateUi.Spin( 0, -1000, 1000, 0.1 ), AnimateUi.Spin( 0, -1000, 1000, 0.1 ), AnimateUi.Spin( 0, -1000, 1000, 0.1 ) };
		SpinBox[] ang = { AnimateUi.Spin( 0, -180, 180, 1, "°" ), AnimateUi.Spin( 0, -180, 180, 1, "°" ), AnimateUi.Spin( 0, -180, 180, 1, "°" ) };
		AnimateUi.Row( g, "Position offset", Triple( pos ) );
		AnimateUi.Row( g, "Rotation offset", Triple( ang ) );
		var scale = AnimateUi.Row( g, "Scale", AnimateUi.Spin( 1, 0.01, 100, 0.01 ) );

		browse.Pressed += () =>
		{
			var fd = new EditorFileDialog { FileMode = EditorFileDialog.FileModeEnum.OpenFile, Access = EditorFileDialog.AccessEnum.Resources, Title = "Model to spawn" };
			foreach ( var f in new[] { "*.tscn", "*.scn", "*.glb", "*.gltf", "*.effigy", "*.obj", "*.fbx" } ) fd.AddFilter( f );
			fd.FileSelected += p => model.Text = p;
			fd.VisibilityChanged += () => { if ( !fd.Visible ) fd.QueueFree(); };
			EditorInterface.Singleton.GetBaseControl().AddChild( fd );
			fd.PopupFileDialog();
		};
		box.Confirmed += () =>
		{
			AddSpawnEvent( c, new AnimateSpawnEvent( name.Text, (int)start.Value, (int)end.Value, model.Text, AnimateUi.Text( bone ), follow.ButtonPressed,
				AnimateUi.Text( side ), new Vector3( (float)pos[0].Value, (float)pos[1].Value, (float)pos[2].Value ),
				new Vector3( (float)ang[0].Value, (float)ang[1].Value, (float)ang[2].Value ), (float)scale.Value ) );
		};
		AnimateUi.Show( box );
		return true;
	}

	static HBoxContainer Triple( SpinBox[] s )
	{
		var h = new HBoxContainer();
		foreach ( var b in s ) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; h.AddChild( b ); }
		return h;
	}

	public static bool MorphEventBox( GeppettoToolContext ctx )
	{
		var c = ctx.Character;
		if ( c?.Skeleton == null ) return ctx.Need();
		var morphs = c.MorphNames();
		if ( morphs.Count == 0 ) { GeppettoToolContext.Warn( "Morph event: this model has no shape keys. Add one in the Model workspace (Shape Key) first." ); return true; }
		if ( !c.EnsureClip() ) { GeppettoToolContext.Warn( "Morph event: this character has no take (it needs a rig)." ); return true; }
		int frame = GeppettoTimeline.CurrentFrame;

		var box = AnimateUi.Box( "Morph event", "Add event" );
		var g = AnimateUi.Grid( box );
		var morph = AnimateUi.Row( g, "Shape key", AnimateUi.Pick( morphs ) );
		var start = AnimateUi.Row( g, "Start frame", AnimateUi.Spin( frame, 0, 100000 ) );
		var end = AnimateUi.Row( g, "End frame", AnimateUi.Spin( frame + 10, 0, 100000 ) );
		var value = AnimateUi.Row( g, "Value", AnimateUi.Spin( 1, 0, 1, 0.01 ) );
		var fin = AnimateUi.Row( g, "Fade in", AnimateUi.Spin( 0, 0, 10000, 1, " fr" ) );
		var fout = AnimateUi.Row( g, "Fade out", AnimateUi.Spin( 0, 0, 10000, 1, " fr" ) );
		box.Confirmed += () =>
		{
			if ( AddMorphEvent( c, new RigMorphEvent
			{
				Morph = AnimateUi.Text( morph ), Start = (int)start.Value, End = (int)end.Value,
				Value = (float)value.Value, FadeIn = (int)fin.Value, FadeOut = (int)fout.Value,
			} ) ) c.ScrubTo( GeppettoTimeline.CurrentFrame );
		};
		AnimateUi.Show( box );
		return true;
	}
}
#endif
