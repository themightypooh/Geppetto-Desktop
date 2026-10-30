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
/// Headless test for Rig NLA strips (GeppettoRigNla), driven by tools/rig_nla_test.gd. Works on a
/// COPY of the g1-spike fixture (the g1-spike_spin.riganim take is copied alongside): add a strip,
/// refuse a missing take, evaluate the timeline, mute/weight, undo/redo, and save + reload (the
/// take reference survives and the action reloads).
/// </summary>
public partial class RigNlaTest : RefCounted
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
		string dir = Path.Combine( Path.GetTempPath(), $"geppetto-nla-test-{Guid.NewGuid():N}" );
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
		Check( "take copied alongside", File.Exists( Path.Combine( Path.GetDirectoryName( path )!, "g1-spike_spin.riganim" ) ) );
		int n0 = doc.Studio.NlaStrips.Count;

		bool added = GeppettoRigNla.AddStrip( doc, "g1-spike_spin.riganim" );
		Check( "add: strip added", added && doc.Studio.NlaStrips.Count == n0 + 1 );
		var strip = doc.Studio.NlaStrips[^1];
		Check( "add: action loaded", strip.Action != null, $"{strip.Action?.Tracks.Count ?? 0} tracks" );
		Check( "add: take stored relative", !Path.IsPathRooted( strip.TakePath ), strip.TakePath );
		Check( "add: validates clean", strip.Validate().Count == 0,
			string.Join( "; ", strip.Validate() ) );

		Check( "add: missing take refused", !GeppettoRigNla.AddStrip( doc, "no_such_take.riganim" )
			&& doc.Studio.NlaStrips.Count == n0 + 1 );
		Check( "add: empty path refused", !GeppettoRigNla.AddStrip( doc, "  " ) );

		var (poses, err) = GeppettoRigNla.Evaluate( doc, 0f );
		Check( "evaluate: no error", err == null, err ?? $"{poses!.Count} bones" );
		Check( "evaluate: poses bones", err == null && poses!.Count > 0,
			err ?? string.Join( ",", poses!.Keys ) );
		Check( "strip: weight inside range", strip.WeightAt( 0f ) > 0f, $"{strip.WeightAt( 0f ):0.###}" );

		Check( "mute", GeppettoRigNla.SetMuted( doc, n0, true ) && doc.Studio.NlaStrips[n0].Muted );
		var (muted, errM) = GeppettoRigNla.Evaluate( doc, 0f );
		Check( "evaluate: muted strip contributes nothing", errM == null
			&& muted!.TryGetValue( "Wheel", out var wheel ) && wheel.Equals( RigPose.Identity ),
			errM ?? "Wheel not at identity" );
		Check( "unmute", GeppettoRigNla.SetMuted( doc, n0, false ) && !doc.Studio.NlaStrips[n0].Muted );
		Check( "weight", GeppettoRigNla.SetWeight( doc, n0, 0.5f )
			&& MathF.Abs( doc.Studio.NlaStrips[n0].Weight - 0.5f ) < 1e-5f );

		doc.Session.History.Undo(); // weight
		Check( "undo restores weight", MathF.Abs( doc.Studio.NlaStrips[n0].Weight - 1f ) < 1e-5f );
		doc.Session.History.Redo();
		Check( "redo re-applies weight", MathF.Abs( doc.Studio.NlaStrips[n0].Weight - 0.5f ) < 1e-5f );

		Check( "remove", GeppettoRigNla.RemoveStrip( doc, n0 ) && doc.Studio.NlaStrips.Count == n0 );
		doc.Session.History.Undo();
		Check( "remove: undo restores", doc.Studio.NlaStrips.Count == n0 + 1 );

		var saved = doc.Bridge.SaveAll();
		Check( "save: written", saved.All( e => e.Outcome != EffigySaveOutcome.Failed ) );
		var reread = StudioDocument.ReadFile( path );
		Check( "save: reload keeps the strip",
			reread.NlaStrips.Count == n0 + 1 && reread.NlaStrips[^1].TakePath == "g1-spike_spin.riganim",
			string.Join( ";", reread.NlaStrips.Select( s => $"{s.Name} {s.TakePath}" ) ) );
		Check( "save: reload reloaded the take", reread.NlaStrips[^1].Action != null );

		scene.QueueFree();
	}
}
#endif
