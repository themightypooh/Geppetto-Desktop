#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Collections.Generic;
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

		// ---- auto weights ------------------------------------------------------------------
		{
			var studioW = doc.Studio;
			bool ran = GeppettoRig.AutoWeights( doc );
			Check( "auto weights: ran", ran );
			var layer = studioW.WeightPaint;
			Check( "auto weights: layer exists", layer != null && layer.Count > 0, layer == null ? "null" : layer.Count.ToString() );
			if ( layer != null )
			{
				var (am, aranges) = studioW.ToMeshWithBodies();
				Check( "auto weights: every vertex got a weight", layer.Count == am.VertexCount, $"{layer.Count}/{am.VertexCount}" );
				float worst = 0f;
				int over = 0;
				foreach ( var (vertex, ws) in layer.Painted )
				{
					float sum = 0f;
					foreach ( var (_, w) in ws ) sum += w;
					worst = MathF.Max( worst, MathF.Abs( sum - 1f ) );
					if ( ws.Count > 4 ) over++;
				}
				Check( "auto weights: partition of unity", worst < 1e-3f, worst.ToString( "0.#####" ) );
				Check( "auto weights: max 4 influences", over == 0, over.ToString() );
				var r0 = aranges.First( r => r.BodyId == bodyId );
				bool rigid = true;
				foreach ( var (vertex, ws) in layer.Painted )
					if ( vertex >= r0.Start && vertex < r0.Start + r0.Count
						&& ( ws.Count != 1 || ws[0].Bone != "shoulder_L" || MathF.Abs( ws[0].Weight - 1f ) > 1e-4f ) )
					{ rigid = false; break; }
				Check( "auto weights: pinned body stays rigid to shoulder_L", rigid );
			}
			doc.Session.History.Undo();
			Check( "auto weights: undo clears the layer", studioW.WeightPaint is not { Count: > 0 } );
			doc.Session.History.Redo();
			Check( "auto weights: redo brings it back", studioW.WeightPaint is { Count: > 0 } );
		}

		// ---- skeleton import + template ------------------------------------------------------
		{
			var gskel = new Skeleton3D();
			gskel.AddBone( "groot" );
			gskel.AddBone( "gchild" );
			gskel.SetBoneParent( 1, 0 );
			gskel.SetBoneRest( 0, new Transform3D( Basis.Identity, new Vector3( 0, 1, 0 ) ) );
			gskel.SetBoneRest( 1, new Transform3D( Basis.Identity, new Vector3( 0, 1, 0 ) ) );
			var conv = GeppettoRig.SkeletonFromGodot( gskel );
			Check( "import: names + parents", conv.Count == 2 && conv.Bones[0].Name == "groot" && conv.Bones[1].Parent == 0,
				$"{conv.Count} bones" );
			var ch = conv.HeadWorld( 1 );
			Check( "import: child head in Effigy inches", MathF.Abs( ch.x ) < 0.01f && MathF.Abs( ch.y ) < 0.01f && MathF.Abs( ch.z - 78.74f ) < 0.05f, ch.ToString() );
			Check( "import: parent length spans to the child", MathF.Abs( conv.Bones[0].Length - 39.37f ) < 0.05f, conv.Bones[0].Length.ToString( "0.##" ) );
			gskel.Free();

			int bn = rig.Count;
			var g2 = new Skeleton3D();
			g2.AddBone( "imp_root" );
			g2.AddBone( "imp_tip" );
			g2.SetBoneParent( 1, 0 );
			g2.SetBoneRest( 0, new Transform3D( Basis.Identity, Vector3.Zero ) );
			g2.SetBoneRest( 1, new Transform3D( Basis.Identity, new Vector3( 0, 0.5f, 0 ) ) );
			bool imp = GeppettoRig.ImportSkeleton( doc, g2 );
			Check( "import: appended as new roots", imp && rig.Count == bn + 2 && rig.Bones[bn].Name == "imp_root"
				&& rig.Bones[bn + 1].Parent == bn, Names( rig ) );
			doc.Session.History.Undo();
			Check( "import: undo removes them", rig.Count == bn );
			doc.Session.History.Redo();
			Check( "import: redo brings them back", rig.Count == bn + 2 );
			g2.Free();

			bool tpl = GeppettoRig.ApplyTemplate( doc, "biped" );
			Check( "template: refused on a non-empty rig", !tpl && rig.Count == bn + 2 );
		}

		// ---- mirror / fix / copy-paste weights ------------------------------------------------
		{
			// Pure-core tests on a tiny symmetric mesh.
			var tmesh = new PolyMesh();
			int v0 = tmesh.AddVertex( new Vec3( 1, 1, 0 ) );
			int v1 = tmesh.AddVertex( new Vec3( 1, 2, 0 ) );
			int v2 = tmesh.AddVertex( new Vec3( 1, 1, 1 ) );
			int v3 = tmesh.AddVertex( new Vec3( 1, -1, 0 ) );
			int v4 = tmesh.AddVertex( new Vec3( 1, -2, 0 ) );
			int v5 = tmesh.AddVertex( new Vec3( 1, -1, 1 ) );
			tmesh.AddFace( new[] { v0, v1, v2 } );
			tmesh.AddFace( new[] { v3, v4, v5 } );
			var trig = new Skeleton();
			trig.AddBone( "arm_L", -1, Xform.Identity, 1f );
			trig.AddBone( "arm_R", -1, Xform.Identity, 1f );
			var tlayer = new WeightPaintLayer( tmesh );
			tlayer.Capture( v0, new[] { new BoneWeight( 0, 1f ) }, trig );
			var (mlayer, nmirrored, npairs) = GeppettoRig.MirrorLayer( tlayer, tmesh, trig );
			bool mhit = false;
			foreach ( var (vertex, ws) in mlayer.Painted )
				if ( vertex == v3 && ws.Count == 1 && ws[0].Bone == "arm_R" && MathF.Abs( ws[0].Weight - 1f ) < 1e-4f )
					mhit = true;
			Check( "mirror: +Y weight lands on −Y with the flipped name", mhit && nmirrored == 1 && npairs == 1,
				$"{nmirrored}/{npairs}" );

			var flayer = new WeightPaintLayer( tmesh );
			var six = new BoneWeight[6];
			for ( int i = 0; i < 6; i++ ) six[i] = new BoneWeight( i % 2, 0.5f - i * 0.05f );
			flayer.Capture( v1, six, trig );
			var (fl, frep) = GeppettoRig.FixLayer( flayer, trig, tmesh );
			bool fok = false;
			foreach ( var (vertex, ws) in fl.Painted )
				if ( vertex == v1 )
				{
					float s = 0f;
					foreach ( var (_, w) in ws ) s += w;
					fok = ws.Count == 4 && MathF.Abs( s - 1f ) < 1e-3f && ws[0].Weight >= ws[1].Weight;
				}
			Check( "fix: prunes to 4, renormalises, strongest first", fok && frep.Changed == 1,
				$"changed={frep.Changed}" );

			// Copy/paste on the live document (auto-weights layer is up from the earlier test).
			int sh2 = rig.IndexOf( "shoulder_L" );
			int hand2 = rig.IndexOf( "hand_L" );
			GeppettoRig.Select( doc, sh2 );
			Check( "copy: remembers the picked bone", GeppettoRig.CopyWeights( doc ) );
			GeppettoRig.Select( doc, hand2 );
			var snapBefore = doc.Studio.WeightPaint!.Clone();
			bool pasted = GeppettoRig.PasteWeights( doc );
			Check( "paste: ran", pasted );
			bool pok = false;
			foreach ( var (vertex, ws) in doc.Studio.WeightPaint!.Painted )
			{
				float afterW = 0f, beforeW = 0f;
				foreach ( var (bone, w) in ws ) if ( bone == "hand_L" ) afterW = w;
				foreach ( var (bvertex, bws) in snapBefore.Painted )
					if ( bvertex == vertex )
						foreach ( var (bone, w) in bws ) if ( bone == "hand_L" ) beforeW = w;
				if ( afterW > beforeW + 1e-6f ) { pok = true; break; }
			}
			Check( "paste: the target bone gained weight somewhere", pok );
			doc.Session.History.Undo();
			Check( "paste: undo restores the layer", LayersEqual( doc.Studio.WeightPaint!, snapBefore ) );
			doc.Session.History.Redo();
		}

		// ---- chains, root/hips ---------------------------------------------------------------
		{
			var (mpl, mpr) = GeppettoRig.AddBoneMirror( doc, -1, new Vec3( 0, 0, 0 ), new Vec3( 2, 4, 0 ), -1 );
			string mln = mpl >= 0 ? rig.Bones[mpl].Name : "", mrn = mpr >= 0 ? rig.Bones[mpr].Name : "";
			Check( "mirror place: pair added", mpl >= 0 && mpr >= 0 && mln.EndsWith( "_L" ) && mrn.EndsWith( "_R" )
				&& rig.Bones[mpl].Parent == -1 && rig.Bones[mpr].Parent == -1, $"{mln},{mrn}" );
			var hl = rig.HeadWorld( mpl ); var hr = rig.HeadWorld( mpr );
			Check( "mirror place: reflected across y=0", MathF.Abs( hl.x - hr.x ) < 1e-3f && MathF.Abs( hl.y + hr.y ) < 1e-3f
				&& MathF.Abs( hl.z - hr.z ) < 1e-3f, $"{hl} vs {hr}" );
			doc.Session.History.Undo();
			Check( "mirror place: one undo removes both", rig.IndexOf( mln ) < 0 && rig.IndexOf( mrn ) < 0 );
			doc.Session.History.Redo();
			Check( "mirror place: redo brings both back", rig.IndexOf( mln ) >= 0 && rig.IndexOf( mrn ) >= 0 );

			int cr = GeppettoRig.AddBone( doc, -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 5, 0 ), "chain_root" );
			int cm = GeppettoRig.AddBone( doc, cr, new Vec3( 0, 5, 0 ), new Vec3( 0, 10, 0 ), "chain_mid" );
			GeppettoRig.AddBone( doc, cm, new Vec3( 0, 10, 0 ), new Vec3( 0, 15, 0 ), "chain_tip" );
			Check( "delete chain: removes the subtree", GeppettoRig.DeleteChain( doc, cr )
				&& rig.IndexOf( "chain_root" ) < 0 && rig.IndexOf( "chain_mid" ) < 0 && rig.IndexOf( "chain_tip" ) < 0 );
			doc.Session.History.Undo();
			Check( "delete chain: undo restores", rig.IndexOf( "chain_tip" ) >= 0 );
			doc.Session.History.Redo();
			Check( "delete chain: redo removes again", rig.IndexOf( "chain_tip" ) < 0 );

			int sh3 = rig.IndexOf( "shoulder_L" );
			GeppettoRig.SetRootBone( doc, sh3 );
			GeppettoRig.SetHipsBone( doc, rig.IndexOf( "hand_L" ) );
			Check( "root/hips: set", doc.Studio.Rig.RootBone == "shoulder_L" && doc.Studio.Rig.HipsBone == "hand_L",
				$"'{doc.Studio.Rig.RootBone}'/'{doc.Studio.Rig.HipsBone}'" );
			GeppettoRig.RenameBone( doc, sh3, "shoulder_renamed" );
			Check( "root: rename carries", doc.Studio.Rig.RootBone == "shoulder_renamed", doc.Studio.Rig.RootBone );
			var saved2 = doc.Bridge.SaveAll();
			var reread2 = StudioDocument.ReadFile( path );
			Check( "root/hips: save+reload keeps them",
				reread2.Rig.RootBone == "shoulder_renamed" && reread2.Rig.HipsBone == "hand_L",
				$"'{reread2.Rig.RootBone}'/'{reread2.Rig.HipsBone}'" );
			doc.Session.History.Undo(); // rename
			doc.Session.History.Undo(); // hips
			doc.Session.History.Undo(); // root
			Check( "root/hips: undo clears", doc.Studio.Rig.RootBone == "" && doc.Studio.Rig.HipsBone == "" );
		}

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
		Check( "import: new bone count", skel2 != null && skel2.GetBoneCount() == n0 + 7 && skel2.GetBoneCount() == reread.Rig.Count,
			skel2 == null ? "no skeleton" : $"{skel2.GetBoneCount()} (was {n0})" );
		Check( "import: mirrored bone present", skel2 != null && skel2.FindBone( "upperarm_R" ) >= 0 );
		again?.Free();
		scene.QueueFree();
	}

	static bool LayersEqual( WeightPaintLayer a, WeightPaintLayer b )
	{
		if ( a.Count != b.Count ) return false;
		var da = new Dictionary<int, List<(string, float)>>();
		foreach ( var (v, ws) in a.Painted ) da[v] = new List<(string, float)>( ws );
		foreach ( var (v, ws) in b.Painted )
		{
			if ( !da.TryGetValue( v, out var aws ) || aws.Count != ws.Count ) return false;
			for ( int i = 0; i < ws.Count; i++ )
				if ( aws[i].Item1 != ws[i].Bone || MathF.Abs( aws[i].Item2 - ws[i].Weight ) > 1e-5f ) return false;
		}
		return true;
	}

	/// <summary>GeppettoCharacter's constructor is private (it is found from the editor selection); the
	/// test builds one the same way Find would.</summary>
	static GeppettoCharacter? GeppettoCharacterForTest( Node root, string path ) =>
		Activator.CreateInstance( typeof( GeppettoCharacter ), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
			null, new object[] { root, path }, null ) as GeppettoCharacter;
}
#endif
