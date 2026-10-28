#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Headless test for the Rig workspace, driven by tools/rig_test.gd. Works on a COPY of the g1-spike
/// fixture: add, rename, reparent, mirror, delete bones and pin a part through <see cref="GeppettoRig"/>
/// (the same calls the panel and the viewport tool make), checking the kernel rig after each, that undo
/// puts it back and redo brings it again; then that ApplyTo rebuilds the scene's Skeleton3D, that save +
/// reload keeps the rig, and that the importer's output carries the new bone count. Also steps the soft
/// preview and checks Rest.
/// </summary>
public partial class RigTest : RefCounted
{
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	void Check( string name, bool ok, string value = "" )
	{
		if ( !ok ) _failed++;
		_checks.Add( new Godot.Collections.Dictionary { { "name", name }, { "ok", ok }, { "value", value } } );
	}

	public Godot.Collections.Dictionary Run( string fixture, Node host )
	{
		string dir = Path.Combine( Path.GetTempPath(), $"geppetto-rig-test-{Guid.NewGuid():N}" );
		try
		{
			Directory.CreateDirectory( dir );
			string src = Path.GetFullPath( ProjectSettings.GlobalizePath( fixture ) );
			foreach ( var f in Directory.GetFiles( Path.GetDirectoryName( src )!, Path.GetFileNameWithoutExtension( src ) + "*" ) )
				File.Copy( f, Path.Combine( dir, Path.GetFileName( f ) ) );
			Body( Path.Combine( dir, Path.GetFileName( src ) ), host );
		}
		catch ( Exception e ) { Check( "exception", false, e.ToString() ); }
		finally
		{
			GeppettoDocument.CloseAll();
			try { Directory.Delete( dir, true ); } catch { }
		}
		return new Godot.Collections.Dictionary { { "checks", _checks }, { "failed", _failed }, { "total", _checks.Count } };
	}

	static string Names( Skeleton r ) => string.Join( ",", r.Bones.Select( b => $"{b.Name}<{b.Parent}" ) );

	void UndoRedo( GeppettoDocument doc, string what, string before, string after )
	{
		var h = doc.Session.History;
		h.Undo();
		Check( $"{what}: undo restores the rig", Names( doc.Studio.Rig ) == before, Names( doc.Studio.Rig ) );
		h.Redo();
		Check( $"{what}: redo brings it back", Names( doc.Studio.Rig ) == after, Names( doc.Studio.Rig ) );
	}

	void Body( string path, Node host )
	{
		var importer = new EffigySceneImporter();
		var scene = importer._ImportScene( path, 0, new Godot.Collections.Dictionary() ) as Node3D;
		Check( "fixture copy imports", scene != null );
		if ( scene == null ) return;
		host.AddChild( scene );

		var doc = GeppettoDocument.Get( path );
		var rig = doc.Studio.Rig;
		int n0 = rig.Count;
		string s0 = Names( rig );
		Check( "fixture has a rig", n0 > 0, s0 );

		// ---- add ---------------------------------------------------------------------------
		var head0 = rig.TailWorld( 0 );
		int arm = GeppettoRig.AddBone( doc, 0, head0 + new Vec3( 0, 4, 0 ), head0 + new Vec3( 0, 10, 0 ), "arm_L" );
		string s1 = Names( rig );
		Check( "add: bone added under bone 0", arm >= 0 && rig.Count == n0 + 1 && rig.Bones[arm].Parent == 0, s1 );
		Check( "add: new bone is selected", GeppettoRig.SelectedIndex( doc ) == arm );
		var tail = rig.TailWorld( arm );
		Check( "add: tail where it was put", ( tail - ( head0 + new Vec3( 0, 10, 0 ) ) ).Length < 1e-3f, $"{tail}" );
		Check( "add: refuses a zero-length bone", GeppettoRig.AddBone( doc, 0, head0, head0 ) == -1 && rig.Count == n0 + 1 );
		UndoRedo( doc, "add", s0, s1 );

		// ---- rename ------------------------------------------------------------------------
		Check( "rename: done", GeppettoRig.RenameBone( doc, rig.IndexOf( "arm_L" ), "upperarm_L" ) && rig.IndexOf( "upperarm_L" ) >= 0 );
		string s2 = Names( rig );
		Check( "rename: selection follows the name", GeppettoRig.SelectedIndex( doc ) == rig.IndexOf( "upperarm_L" ) );
		Check( "rename: duplicate refused", !GeppettoRig.RenameBone( doc, rig.IndexOf( "upperarm_L" ), rig.Bones[0].Name ) && Names( rig ) == s2 );
		UndoRedo( doc, "rename", s1, s2 );

		// ---- reparent ----------------------------------------------------------------------
		int tip = GeppettoRig.AddBone( doc, -1, tail, tail + new Vec3( 0, 5, 0 ), "hand_L" );
		string s3 = Names( rig );
		var tipHeadBefore = rig.HeadWorld( tip );
		int moved = GeppettoRig.Reparent( doc, tip, rig.IndexOf( "upperarm_L" ) );
		string s4 = Names( rig );
		Check( "reparent: hand_L now under upperarm_L", moved >= 0 && rig.Bones[rig.IndexOf( "hand_L" )].Parent == rig.IndexOf( "upperarm_L" ), s4 );
		Check( "reparent: joint stays put in the world", ( rig.HeadWorld( rig.IndexOf( "hand_L" ) ) - tipHeadBefore ).Length < 1e-3f );
		int ua = rig.IndexOf( "upperarm_L" );
		Check( "reparent: a cycle is refused", GeppettoRig.Reparent( doc, ua, rig.IndexOf( "hand_L" ) ) < 0 && Names( rig ) == s4 );
		Check( "reparent: parent choices skip the bone's own subtree", !GeppettoRig.ParentChoices( rig, ua ).Contains( rig.IndexOf( "hand_L" ) ) );
		UndoRedo( doc, "reparent", s3, s4 );

		// ---- mirror ------------------------------------------------------------------------
		int mr = GeppettoRig.Mirror( doc, rig.IndexOf( "upperarm_L" ) );
		string s5 = Names( rig );
		Check( "mirror: upperarm_R + hand_R made", mr >= 0 && rig.IndexOf( "upperarm_R" ) >= 0 && rig.IndexOf( "hand_R" ) >= 0
			&& rig.Bones[rig.IndexOf( "hand_R" )].Parent == rig.IndexOf( "upperarm_R" ), s5 );
		Check( "mirror: same parent as the source", rig.IndexOf( "upperarm_R" ) >= 0 && rig.Bones[rig.IndexOf( "upperarm_R" )].Parent == rig.Bones[rig.IndexOf( "upperarm_L" )].Parent );
		var l = rig.TailWorld( rig.IndexOf( "upperarm_L" ) );
		var r = rig.TailWorld( rig.IndexOf( "upperarm_R" ) );
		Check( "mirror: reflected across y=0", MathF.Abs( l.x - r.x ) < 1e-3f && MathF.Abs( l.y + r.y ) < 1e-3f && MathF.Abs( l.z - r.z ) < 1e-3f, $"{l} vs {r}" );
		UndoRedo( doc, "mirror", s4, s5 );

		// ---- delete ------------------------------------------------------------------------
		Check( "delete: done", GeppettoRig.DeleteBone( doc, rig.IndexOf( "hand_R" ) ) && rig.IndexOf( "hand_R" ) < 0 && rig.Count == n0 + 3 );
		string s6 = Names( rig );
		UndoRedo( doc, "delete", s5, s6 );

		// ---- assign body + soft ------------------------------------------------------------
		var bodyId = doc.Studio.Bodies[0].Id;
		doc.Studio.BodyBoneMap.TryGetValue( bodyId, out var wasBone );
		GeppettoRig.ToggleBody( doc, bodyId, rig.IndexOf( "upperarm_L" ) );
		Check( "assign: part pinned to upperarm_L", doc.Studio.BodyBoneMap.TryGetValue( bodyId, out var nowBone ) && nowBone == "upperarm_L", nowBone ?? "" );
		GeppettoRig.RenameBone( doc, rig.IndexOf( "upperarm_L" ), "shoulder_L" );
		Check( "rename carries the pinned part", doc.Studio.BodyBoneMap.TryGetValue( bodyId, out var renamed ) && renamed == "shoulder_L", renamed ?? "" );
		doc.Session.History.Undo();
		doc.Session.History.Undo();
		doc.Studio.BodyBoneMap.TryGetValue( bodyId, out var undoneBone );
		Check( "assign: undo unpins", undoneBone == wasBone, undoneBone ?? "(none)" );
		doc.Session.History.Redo();
		doc.Session.History.Redo();

		int hand = rig.IndexOf( "hand_L" );
		Check( "soft: Make Soft", GeppettoRig.SetSoft( doc, hand, true ) && rig.Bones[hand].Soft != null );
		Check( "soft: value edit", GeppettoRig.SetSoftValue( doc, hand, 0, 123f ) && rig.Bones[hand].Soft!.Stiffness == 123f );

		// ---- the scene: ApplyTo rebuilds the Skeleton3D ------------------------------------
		var skel = scene.FindChild( "Skeleton3D", true, false ) as Skeleton3D;
		doc.ApplyTo( scene );
		Check( "scene: ApplyTo rebuilt the bones", skel != null && skel.GetBoneCount() == rig.Count
			&& Enumerable.Range( 0, rig.Count ).All( i => skel.GetBoneName( i ) == rig.Bones[i].Name && skel.GetBoneParent( i ) == rig.Bones[i].Parent ),
			skel == null ? "no skeleton" : $"{skel.GetBoneCount()} bones" );
		// A reparent alone (same names, same count) must rebuild too.
		int sh = rig.IndexOf( "shoulder_L" );
		GeppettoRig.Reparent( doc, rig.IndexOf( "hand_L" ), rig.Bones[sh].Parent );
		doc.ApplyTo( scene );
		int hi = rig.IndexOf( "hand_L" );
		Check( "scene: reparent-only change rebuilds", skel != null && skel.GetBoneParent( skel.FindBone( "hand_L" ) ) == rig.Bones[hi].Parent,
			skel == null ? "" : $"{skel.GetBoneParent( skel.FindBone( "hand_L" ) )} vs {rig.Bones[hi].Parent}" );

		// ---- soft preview ------------------------------------------------------------------
		var c = GeppettoCharacterForTest( scene, path );
		if ( c != null )
		{
			var preview = new RigSoftPreview( c );
			var bind = SoftSolver.BindPose( rig );
			Xform[] p = bind;
			for ( int i = 0; i < 20; i++ ) p = preview.Step( 1f / 60f );
			float sag = ( p[hi].Origin + p[hi].Y.Normal * rig.Bones[hi].Length - ( bind[hi].Origin + bind[hi].Y.Normal * rig.Bones[hi].Length ) ).Length;
			Check( "soft preview: the soft bone moves under gravity", sag > 1e-3f, $"{sag:0.###} in" );
			preview.Rest();
			var settled = preview.Step( 1f / 60f );
			float back = ( settled[hi].Y.Normal - bind[hi].Y.Normal ).Length;
			Check( "soft preview: Rest settles it on the pose", back < 1e-3f, $"{back:0.####}" );
			Check( "soft preview: never writes the skeleton", skel != null && skel.GetBonePose( skel.FindBone( "hand_L" ) ).IsEqualApprox( skel.GetBoneRest( skel.FindBone( "hand_L" ) ) ) );
			preview.Exit();
		}
		else Check( "soft preview: character", false, "could not make a GeppettoCharacter" );

		// ---- save + reload + re-import ------------------------------------------------------
		string final = Names( rig );
		var saved = doc.Bridge.SaveAll();
		Check( "save: written", saved.All( e => e.Outcome != EffigySaveOutcome.Failed ), string.Join( "; ", saved.Select( e => $"{e.Kind}:{e.Outcome} {e.Message}" ) ) );
		var reread = StudioDocument.ReadFile( path );
		Check( "save: reload keeps the rig", Names( reread.Rig ) == final, Names( reread.Rig ) );
		Check( "save: reload keeps the pinned part", reread.BodyBoneMap.TryGetValue( bodyId, out var rb ) && rb == "shoulder_L", rb ?? "" );
		Check( "save: reload keeps softness", reread.Rig.Bones[reread.Rig.IndexOf( "hand_L" )].Soft?.Stiffness == 123f );

		var again = importer._ImportScene( path, 0, new Godot.Collections.Dictionary() ) as Node3D;
		var skel2 = again?.FindChild( "Skeleton3D", true, false ) as Skeleton3D;
		Check( "import: new bone count", skel2 != null && skel2.GetBoneCount() == n0 + 3 && skel2.GetBoneCount() == reread.Rig.Count,
			skel2 == null ? "no skeleton" : $"{skel2.GetBoneCount()} (was {n0})" );
		Check( "import: mirrored bone present", skel2 != null && skel2.FindBone( "upperarm_R" ) >= 0 );
		again?.Free();
		scene.QueueFree();
	}

	/// <summary>GeppettoCharacter's constructor is private (it is found from the editor selection); the
	/// test builds one the same way Find would.</summary>
	static GeppettoCharacter? GeppettoCharacterForTest( Node root, string path ) =>
		Activator.CreateInstance( typeof( GeppettoCharacter ), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
			null, new object[] { root, path }, null ) as GeppettoCharacter;
}
#endif
