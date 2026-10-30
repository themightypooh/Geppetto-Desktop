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
/// Headless test for Rig morph drivers (GeppettoRigDrivers), driven by tools/rig_driver_test.gd.
/// Works on a COPY of the g1-spike fixture: add a driver, refuse duplicates, preview the weight at
/// rest (~0) and at the target angle (~1), undo/redo, remove, and save + reload.
/// </summary>
public partial class RigDriverTest : RefCounted
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
		string dir = Path.Combine( Path.GetTempPath(), $"geppetto-drv-test-{Guid.NewGuid():N}" );
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
		string bone = rig.Bones[0].Name;
		int n0 = doc.Studio.Drivers.Count;

		bool added = GeppettoRigDrivers.AddDriver( doc, "test_morph", bone );
		Check( "add: driver added", added && doc.Studio.Drivers.Count == n0 + 1 );
		var driver = doc.Studio.Drivers[^1];
		Check( "add: fields", driver.Morph == "test_morph" && driver.Bone == bone
			&& driver.Channel == DriverChannel.Rotation && driver.Axis == DriverAxis.Y
			&& driver.Rest == 0f && driver.Target == 90f, driver.Describe() );
		Check( "add: duplicate refused", !GeppettoRigDrivers.AddDriver( doc, "test_morph", bone )
			&& doc.Studio.Drivers.Count == n0 + 1 );
		Check( "add: bad bone refused", !GeppettoRigDrivers.AddDriver( doc, "test_morph", "no_such_bone" ) );
		Check( "add: empty morph refused", !GeppettoRigDrivers.AddDriver( doc, "  ", bone ) );

		var (wTarget, errT) = GeppettoRigDrivers.PreviewDriver( doc, n0, 90f );
		Check( "preview: weight ~1 at target", errT == null && MathF.Abs( wTarget - 1f ) < 0.05f,
			errT ?? $"{wTarget:0.###}" );
		var (wRest, errR) = GeppettoRigDrivers.PreviewDriver( doc, n0, 0f );
		Check( "preview: weight ~0 at rest", errR == null && MathF.Abs( wRest ) < 0.05f,
			errR ?? $"{wRest:0.###}" );
		var (wMid, errM) = GeppettoRigDrivers.PreviewDriver( doc, n0, 45f );
		Check( "preview: mid weight between", errM == null && wMid > 0.05f && wMid < 0.95f,
			errM ?? $"{wMid:0.###}" );

		doc.Session.History.Undo();
		Check( "undo removes it", doc.Studio.Drivers.Count == n0 );
		doc.Session.History.Redo();
		Check( "redo brings it back", doc.Studio.Drivers.Count == n0 + 1 );

		Check( "remove", GeppettoRigDrivers.RemoveDriver( doc, n0 ) && doc.Studio.Drivers.Count == n0 );
		doc.Session.History.Undo();
		Check( "remove: undo restores", doc.Studio.Drivers.Count == n0 + 1 );

		var saved = doc.Bridge.SaveAll();
		Check( "save: written", saved.All( e => e.Outcome != EffigySaveOutcome.Failed ) );
		var reread = StudioDocument.ReadFile( path );
		Check( "save: reload keeps the driver",
			reread.Drivers.Count == n0 + 1 && reread.Drivers[^1].Morph == "test_morph"
				&& reread.Drivers[^1].Bone == bone && reread.Drivers[^1].Target == 90f,
			string.Join( ";", reread.Drivers.Select( d => d.Describe() ) ) );

		scene.QueueFree();
	}
}
#endif
