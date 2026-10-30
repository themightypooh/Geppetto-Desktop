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
/// Headless test for Rig IK chains (GeppettoRigIk), driven by tools/rig_ik_test.gd. Works on a COPY
/// of the ik-arm fixture (Root/Upper/Lower/Hand/Finger): add a chain, solve once toward a reachable
/// target (the end must get closer), the kernel two-bone solve reaches, toggle enabled, undo/redo,
/// remove, and save + reload.
/// </summary>
public partial class RigIkTest : RefCounted
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
		string dir = Path.Combine( Path.GetTempPath(), $"geppetto-ik-test-{Guid.NewGuid():N}" );
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

	void Body( string path, Node host )
	{
		var importer = new EffigySceneImporter();
		var scene = importer._ImportScene( path, 0, new Godot.Collections.Dictionary() ) as Node3D;
		Check( "fixture copy imports", scene != null );
		if ( scene == null ) return;
		host.AddChild( scene );

		var doc = GeppettoDocument.Get( path );
		var rig = doc.Studio.Rig;
		int hand = rig.IndexOf( "Hand" );
		int upper = rig.IndexOf( "Upper" );
		Check( "fixture has the arm", hand >= 0 && upper >= 0, $"{rig.Count} bones" );
		int n0 = doc.Studio.IkChains.Count;

		bool added = GeppettoRigIk.AddIk( doc, hand, 2 );
		Check( "add: chain added", added && doc.Studio.IkChains.Count == n0 + 1 );
		var ik = doc.Studio.IkChains[^1];
		Check( "add: fields", ik.EndBone == "Hand" && ik.ChainLength == 2 && ik.Enabled
			&& ik.Name == "IK_Hand", $"{ik.Name} {ik.EndBone} x{ik.ChainLength}" );
		Check( "add: duplicate name refused", !GeppettoRigIk.AddIk( doc, hand, 2 ) );
		Check( "add: bad bone refused", !GeppettoRigIk.AddIk( doc, 999 ) );

		// Kernel two-bone solve, direct: a reachable target is reached.
		var root = rig.WorldBind( upper ).Origin;
		var middle = rig.WorldBind( rig.IndexOf( "Lower" ) ).Origin;
		var end = rig.WorldBind( hand ).Origin;
		var target = root + (end - root) * 0.8f; // inside the reachable sphere
		bool ok = TwoBoneIk.TrySolve( root, middle, end, target, new Vec3( 0, 0, 1 ), 0f, out var sol );
		Check( "kernel: two-bone reaches", ok && sol.Reached
			&& (sol.End - target).Length < 1e-2f, $"reached={sol.Reached} err={(sol.End - target).Length:0.####}" );

		// Solve once through the tool: the end must get closer to the target.
		float before = (end - target).Length;
		var (after, err) = GeppettoRigIk.SolveOnce( doc, n0, target );
		Check( "solve: no error", err == null, err ?? after.ToString() );
		Check( "solve: end moves toward the goal", err == null && (after - target).Length < before,
			err ?? $"before {before:0.###} after {(after - target).Length:0.###}" );

		Check( "pole: set", GeppettoRigIk.SetPole( doc, n0, new Vec3( 0, 1, 0 ) )
			&& doc.Studio.IkChains[n0].Pole.Equals( new Vec3( 0, 1, 0 ) ) );
		Check( "toggle: disable", GeppettoRigIk.SetEnabled( doc, n0, false )
			&& !doc.Studio.IkChains[n0].Enabled );
		Check( "toggle: enable", GeppettoRigIk.SetEnabled( doc, n0, true )
			&& doc.Studio.IkChains[n0].Enabled );

		doc.Session.History.Undo(); // enable
		doc.Session.History.Undo(); // disable
		doc.Session.History.Undo(); // pole
		Check( "undo restores the pole", doc.Studio.IkChains[n0].Pole.Equals( new Vec3( 0, 0, 1 ) ),
			doc.Studio.IkChains[n0].Pole.ToString() );
		doc.Session.History.Redo();
		doc.Session.History.Redo();
		doc.Session.History.Redo();
		Check( "redo re-applies", doc.Studio.IkChains[n0].Pole.Equals( new Vec3( 0, 1, 0 ) )
			&& doc.Studio.IkChains[n0].Enabled );

		Check( "remove", GeppettoRigIk.RemoveIk( doc, n0 ) && doc.Studio.IkChains.Count == n0 );
		doc.Session.History.Undo();
		Check( "remove: undo restores", doc.Studio.IkChains.Count == n0 + 1 );

		var saved = doc.Bridge.SaveAll();
		Check( "save: written", saved.All( e => e.Outcome != EffigySaveOutcome.Failed ) );
		var reread = StudioDocument.ReadFile( path );
		Check( "save: reload keeps the chain",
			reread.IkChains.Count == n0 + 1 && reread.IkChains[^1].EndBone == "Hand"
				&& reread.IkChains[^1].ChainLength == 2 && reread.IkChains[^1].Pole.Equals( new Vec3( 0, 1, 0 ) ),
			string.Join( ";", reread.IkChains.Select( k => $"{k.Name} {k.EndBone}" ) ) );

		scene.QueueFree();
	}
}
#endif
