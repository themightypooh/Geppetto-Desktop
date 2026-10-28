#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// The Geppetto character the dial is working on: an imported .effigy scene (the edited scene, or an
/// instance of one picked in the Scene dock), its Skeleton3D, the kernel rig rebuilt from the .effigy, and
/// the clip being animated (the first <c>&lt;base&gt;_*.riganim</c> next to it, opened through
/// <see cref="EffigySaveBridge"/> so Ctrl+S saves it; or a new, unsaved take when there is none).
/// </summary>
public sealed class GeppettoCharacter
{
	public Node Root { get; }
	public string EffigyPath { get; }                      // res://…
	public Skeleton3D? Skeleton { get; }
	public Skeleton? Rig => Doc?.Studio.Rig;
	/// <summary>The live, editable document behind this character (shared by every tool and the save hook).</summary>
	public GeppettoDocument? Doc { get; }
	public DocumentSession? ClipSession { get; private set; }
	public EffigyAutoKey? AutoKey { get; private set; }
	public RigAnimFile? Clip => ClipSession?.Clip;
	public string ClipName { get; private set; } = "";
	public bool ClipIsNew { get; private set; }
	public int Fps => Clip is { FrameRate: > 0 } c ? c.FrameRate : 30;
	public int FrameCount => Clip == null ? 60 : Math.Max( 60, Math.Max( Clip.FrameCount, Clip.LastFrame + 1 ) );

	static readonly Dictionary<string, (EffigySaveBridge Bridge, DocumentSession Clip, string Name, bool New)> Clips = new();

	GeppettoCharacter( Node root, string path )
	{
		Root = root;
		EffigyPath = path;
		Skeleton = root.FindChild( "Skeleton3D", true, false ) as Skeleton3D
			?? root.FindChildren( "*", nameof( Skeleton3D ), true, false ).OfType<Skeleton3D>().FirstOrDefault();
		try { Doc = GeppettoDocument.Get( path ); }
		catch ( Exception e ) { GD.PushWarning( $"[Geppetto] Could not rebuild {path}: {e.Message}" ); }
	}

	/// <summary>The .effigy a node was instanced or inherited from, or null. Instances carry it as their
	/// scene file; an inherited scene's root carries the importer's <c>geppetto_effigy</c> meta (Godot does
	/// not expose a node's inherited scene state to scripts).</summary>
	public static string? EffigyPathOf( Node node, Node editedRoot )
	{
		if ( node.SceneFilePath.EndsWith( ".effigy", StringComparison.OrdinalIgnoreCase ) ) return node.SceneFilePath;
		var meta = node.GetMeta( EffigySceneImporter.EffigyMeta, "" ).AsString();
		return meta.EndsWith( ".effigy", StringComparison.OrdinalIgnoreCase ) ? meta : null;
	}

	/// <summary>The character under the selection, else the edited scene when it is one.</summary>
	public static GeppettoCharacter? Find( GeppettoCharacter? current )
	{
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		if ( root == null ) return null;
		foreach ( var n in EditorInterface.Singleton.GetSelection().GetSelectedNodes() )
			for ( Node? p = n; p != null; p = p == root ? null : p.GetParent() )
				if ( EffigyPathOf( p, root ) is { } path ) return Reuse( current, p, path );
		return EffigyPathOf( root, root ) is { } rp ? Reuse( current, root, rp ) : null;
	}

	static GeppettoCharacter Reuse( GeppettoCharacter? current, Node node, string path ) =>
		current != null && current.Root == node && GodotObject.IsInstanceValid( node ) ? current : new GeppettoCharacter( node, path );

	/// <summary>Opens (once per .effigy) the clip the Timeline and the key tools work on.</summary>
	public bool EnsureClip()
	{
		if ( ClipSession != null ) return true;
		if ( Rig == null ) return false;
		string disk = ProjectSettings.GlobalizePath( EffigyPath );
		if ( !Clips.TryGetValue( disk, out var entry ) )
		{
			var studio = Doc!.Studio;
			var bridge = Doc.Bridge;
			string dir = Path.GetDirectoryName( disk ) ?? ".", baseName = Path.GetFileNameWithoutExtension( disk );
			var existing = Directory.Exists( dir ) ? Directory.GetFiles( dir, baseName + "_*.riganim" ).OrderBy( f => f ).FirstOrDefault() : null;
			if ( existing != null )
				entry = ( bridge, bridge.OpenClip( existing ), Path.GetFileNameWithoutExtension( existing )[( baseName.Length + 1 )..], false );
			else
			{
				// No clip next to the model yet: an in-memory take. TODO: save it as <base>_take.riganim on Ctrl+S.
				string take = Path.Combine( dir, baseName + "_take.riganim" );
				var clip = new RigAnimFile { FrameRate = 30, FrameCount = 60, SourceModel = Path.GetFileName( disk ) };
				entry = ( bridge, DocumentSession.CreateEffigy( take, studio, take, null, clip ), "take (new)", true );
			}
			Clips[disk] = entry;
		}
		ClipSession = entry.Clip;
		ClipName = entry.Name;
		ClipIsNew = entry.New;
		AutoKey = new EffigyAutoKey( ClipSession, new EffigyUndoRedoAdapter( ClipSession ), Rig );
		return true;
	}

	/// <summary>Bone names in rig order: the Timeline's lanes.</summary>
	public IReadOnlyList<string> BoneNames => Rig?.Bones.Select( b => b.Name ).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

	/// <summary>Keyed frames of one bone's track.</summary>
	public IEnumerable<int> KeyFrames( string bone ) =>
		Clip?.BoneTracks.FirstOrDefault( t => t.Name == bone )?.Keys.Select( k => k.Frame ) ?? Enumerable.Empty<int>();

	/// <summary>Poses the skeleton at a frame from the clip.</summary>
	public void ScrubTo( int frame )
	{
		if ( Clip == null || Rig == null || Skeleton == null ) return;
		EffigyPoseBridge.ApplyClipPose( Clip, Rig, Skeleton, frame / (double)Fps );
	}

	/// <summary>The bone two-bone IK drives: a hand / wrist / foot if the rig has one, else the first
	/// bone with a parent and a grandparent and no children.</summary>
	public string? IkEndBone()
	{
		if ( Skeleton == null || Rig == null ) return null;
		int Depth( int b ) { int d = 0; for ( int p = Skeleton.GetBoneParent( b ); p >= 0; p = Skeleton.GetBoneParent( p ) ) d++; return d; }
		var bones = Enumerable.Range( 0, Skeleton.GetBoneCount() ).Where( b => Depth( b ) >= 2 && Rig.IndexOf( Skeleton.GetBoneName( b ) ) >= 0 ).ToList();
		foreach ( var want in new[] { "hand", "wrist", "foot" } )
			foreach ( var b in bones )
				if ( Skeleton.GetBoneName( b ).Contains( want, StringComparison.OrdinalIgnoreCase ) ) return Skeleton.GetBoneName( b );
		var leaf = bones.FirstOrDefault( b => Skeleton.GetBoneChildren( b ).Length == 0, -1 );
		return leaf >= 0 ? Skeleton.GetBoneName( leaf ) : bones.Select( Skeleton.GetBoneName ).FirstOrDefault();
	}
}
#endif
