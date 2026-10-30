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
/// Headless test for Rig correctives (GeppettoRigCorrectives), driven by
/// tools/rig_corrective_test.gd. Works on a COPY of the g1-spike fixture: add a corrective, refuse a
/// duplicate, undo/redo, preview the built morph on a bound body, remove, and save + reload.
/// </summary>
public partial class RigCorrectiveTest : RefCounted
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
		string dir = Path.Combine( Path.GetTempPath(), $"geppetto-corr-test-{Guid.NewGuid():N}" );
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
		Check( "fixture has a rig", rig.Count > 0, $"{rig.Count} bones" );
		int n0 = doc.Studio.Correctives.Count;

		bool added = GeppettoRigCorrectives.AddCorrective( doc, 0, 90f );
		Check( "add: corrective added", added && doc.Studio.Correctives.Count == n0 + 1 );
		var shape = doc.Studio.Correctives[^1];
		Check( "add: keyed on bone 0 at 90°", shape.Bone == rig.Bones[0].Name && shape.Angle == 90f, shape.ToString() );
		Check( "add: auto axis is a unit vector", MathF.Abs( shape.Axis.Length - 1f ) < 1e-3f, shape.Axis.ToString() );
		Check( "add: duplicate refused", !GeppettoRigCorrectives.AddCorrective( doc, 0, 90f )
			&& doc.Studio.Correctives.Count == n0 + 1 );
		Check( "add: bad bone refused", !GeppettoRigCorrectives.AddCorrective( doc, 999 ) );

		doc.Session.History.Undo();
		Check( "undo removes it", doc.Studio.Correctives.Count == n0 );
		doc.Session.History.Redo();
		Check( "redo brings it back", doc.Studio.Correctives.Count == n0 + 1 );

		var body = doc.Studio.Bodies.FirstOrDefault( b => b.Mesh != null && b.Mesh.VertexCount > 0 );
		if ( body != null )
		{
			body.Mesh.Skin = SkinBinder.BindSmooth( body.Mesh, rig );
			var (moved, max, err) = GeppettoRigCorrectives.PreviewCorrective( doc, n0, body.Id );
			Check( "preview: morph built", err == null, err ?? $"{moved} moved" );
			Check( "preview: moves the blend zone", moved > 0, $"{moved} vertices, max {max:0.###}" );
			Check( "preview: bad body is honest", GeppettoRigCorrectives.PreviewCorrective( doc, n0, "nope" ).Error != null );
		}
		else Check( "preview: skinned body", false, "no body with a mesh in the fixture" );

		Check( "remove", GeppettoRigCorrectives.RemoveCorrective( doc, n0 )
			&& doc.Studio.Correctives.Count == n0 );
		doc.Session.History.Undo();
		Check( "remove: undo restores", doc.Studio.Correctives.Count == n0 + 1 );

		var saved = doc.Bridge.SaveAll();
		Check( "save: written", saved.All( e => e.Outcome != EffigySaveOutcome.Failed ),
			string.Join( "; ", saved.Select( e => $"{e.Kind}:{e.Outcome} {e.Message}" ) ) );
		var reread = StudioDocument.ReadFile( path );
		Check( "save: reload keeps the corrective",
			reread.Correctives.Count == n0 + 1 && reread.Correctives[^1].Bone == rig.Bones[0].Name,
			string.Join( ";", reread.Correctives.Select( c => c.ToString() ) ) );

		scene.QueueFree();
	}
}
#endif
