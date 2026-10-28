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
/// the take (clip) being animated. Takes are real <c>&lt;base&gt;_&lt;name&gt;.riganim</c> files next to the
/// .effigy, as Marionette saved them; each is opened through the document's <see cref="EffigySaveBridge"/>
/// so Ctrl+S writes its keys. A model with no take gets <c>&lt;base&gt;_take1.riganim</c> written on first use.
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
	/// <summary>The open take's file on disk (absolute), or null.</summary>
	public string? ClipDiskPath => ClipSession?.SourcePath;
	/// <summary>Kept for callers of the old in-memory take: takes are always files now.</summary>
	public bool ClipIsNew => false;
	public int Fps => Clip is { FrameRate: > 0 } c ? c.FrameRate : 30;
	public int FrameCount => Clip == null ? 60 : Math.Max( 60, Math.Max( Clip.FrameCount, Clip.LastFrame + 1 ) );

	/// <summary>Raised after a take edit made through <see cref="ClipEdit"/>, or an undo / redo of one.</summary>
	public static event Action<GeppettoCharacter>? ClipChanged;

	/// <summary>The take each .effigy (disk path) is on, so re-finding the character keeps the take.</summary>
	static readonly Dictionary<string, string> CurrentTake = new( StringComparer.Ordinal );

	string DiskPath => Doc?.DiskPath ?? Path.GetFullPath( ProjectSettings.GlobalizePath( EffigyPath ) );
	string BaseName => Path.GetFileNameWithoutExtension( DiskPath );
	string Dir => Path.GetDirectoryName( DiskPath ) ?? ".";

	GeppettoCharacter( Node root, string path )
	{
		Root = root;
		EffigyPath = path;
		Skeleton = root.FindChild( "Skeleton3D", true, false ) as Skeleton3D
			?? root.FindChildren( "*", nameof( Skeleton3D ), true, false ).OfType<Skeleton3D>().FirstOrDefault();
		try { Doc = GeppettoDocument.Get( path ); }
		catch ( Exception e ) { GD.PushWarning( $"[Geppetto] Could not rebuild {path}: {e.Message}" ); }
	}

	/// <summary>A character for an imported scene root and its .effigy without an editor selection
	/// (headless tests, tools that already hold the scene).</summary>
	public static GeppettoCharacter For( Node root, string effigyPath ) => new( root, effigyPath );

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

	// =========================================================================================
	// Takes
	// =========================================================================================

	/// <summary>Opens the take the Timeline and the key tools work on: the one last used for this .effigy,
	/// else the first <c>&lt;base&gt;_*.riganim</c>, else a new take file.</summary>
	public bool EnsureClip()
	{
		if ( ClipSession != null ) return true;
		if ( Rig == null || Doc == null ) return false;
		string? path = CurrentTake.TryGetValue( DiskPath, out var cur ) && File.Exists( cur ) ? cur : Takes().FirstOrDefault();
		path ??= NewTakeFile();
		return path != null && SwitchClip( path );
	}

	/// <summary>The takes next to the .effigy, by file name.</summary>
	public IReadOnlyList<string> Takes()
	{
		if ( !Directory.Exists( Dir ) ) return Array.Empty<string>();
		return Directory.GetFiles( Dir, BaseName + "_*.riganim" ).Select( Path.GetFullPath ).OrderBy( f => f, StringComparer.Ordinal ).ToList();
	}

	/// <summary>A take's name: the file name after <c>&lt;base&gt;_</c> (what the importer calls the animation).</summary>
	public string TakeName( string path )
	{
		string n = Path.GetFileNameWithoutExtension( path );
		return n.StartsWith( BaseName + "_", StringComparison.Ordinal ) ? n[( BaseName.Length + 1 )..] : n;
	}

	/// <summary>Makes <paramref name="path"/> (a .riganim next to the model) the take being animated.
	/// The take it leaves keeps its unsaved keys in the bridge, so switching back finds them and Ctrl+S saves them.</summary>
	public bool SwitchClip( string path )
	{
		if ( Doc == null || Rig == null ) return false;
		path = Path.GetFullPath( ProjectSettings.GlobalizePath( path ) );
		DocumentSession session;
		try { session = Doc.Bridge.OpenClip( path ); }
		catch ( Exception e ) { GD.PushError( $"[Geppetto] Could not open take {path}: {e.Message}" ); return false; }
		ClipSession = session;
		ClipName = TakeName( path );
		CurrentTake[DiskPath] = path;
		LoadRig();
		AutoKey = new EffigyAutoKey( ClipSession, new EffigyUndoRedoAdapter( ClipSession ), Rig );
		ClipChanged?.Invoke( this );
		return true;
	}

	/// <summary>Writes a new, empty take <c>&lt;base&gt;_take&lt;N&gt;.riganim</c> next to the model (Marionette's
	/// New clip saved a file first, too) and switches to it. Returns its path, or null.</summary>
	public string? NewTake()
	{
		var path = NewTakeFile();
		return path != null && SwitchClip( path ) ? path : null;
	}

	string? NewTakeFile()
	{
		if ( !Directory.Exists( Dir ) ) return null;
		string path;
		int n = 1;
		do path = Path.Combine( Dir, $"{BaseName}_take{n++}.riganim" ); while ( File.Exists( path ) );
		int fps = Clip is { FrameRate: > 0 } c ? c.FrameRate : 30;
		var clip = new RigAnimFile { FrameRate = fps, FrameCount = 60, SourceModel = Path.GetFileName( DiskPath ) };
		string tmp = path + ".tmp";
		File.WriteAllText( tmp, RigAnimWriter.Write( clip ) );
		File.Move( tmp, path, overwrite: false );
		GD.Print( $"[Geppetto] New take {Path.GetFileName( path )}" );
		return Path.GetFullPath( path );
	}

	/// <summary>The .ctrlrig the take names (its Limit / IK constraints), resolved next to the take, then
	/// as a res:// path. Null when it names none or the file is missing.</summary>
	public string? RigAssetPath()
	{
		if ( Clip == null || string.IsNullOrWhiteSpace( Clip.RigAsset ) ) return null;
		string a = Clip.RigAsset;
		string near = Path.Combine( Path.GetDirectoryName( ClipDiskPath ?? DiskPath ) ?? Dir, a );
		if ( File.Exists( near ) ) return Path.GetFullPath( near );
		string res = ProjectSettings.GlobalizePath( "res://" + a.TrimStart( '/' ) );
		return File.Exists( res ) ? Path.GetFullPath( res ) : null;
	}

	/// <summary>Reads the take's .ctrlrig into the clip, so scrubbing, export and IK honour its limits.</summary>
	public void LoadRig()
	{
		if ( Clip == null || RigAssetPath() is not { } rig ) return;
		try { Clip.UseRig( File.ReadAllText( rig ) ); }
		catch ( Exception e ) { GD.PushWarning( $"[Geppetto] Could not read {rig}: {e.Message}" ); }
	}

	/// <summary>Runs one edit on the take as one undo step: recorded in the take's history (so Ctrl+S saves
	/// it) and mirrored onto Poohdot's Ctrl+Z.</summary>
	public bool ClipEdit( IEditCommand command, string? label = null )
	{
		if ( ClipSession == null && !EnsureClip() ) return false;
		var s = ClipSession!;
		s.History.Execute( command );
		s.BumpRevision();
		ClipChanged?.Invoke( this );
		var ur = GeppettoDocument.UndoManager;
		if ( ur == null ) return true;
		var undo = new EffigyActionTarget( () => { if ( s.History.CanUndo ) { s.History.Undo(); s.BumpRevision(); ClipChanged?.Invoke( this ); } } );
		var redo = new EffigyActionTarget( () => { if ( s.History.CanRedo ) { s.History.Redo(); s.BumpRevision(); ClipChanged?.Invoke( this ); } } );
		// The edited scene as context marks it unsaved, so Ctrl+S runs the plugin's save hook.
		var context = EditorInterface.Singleton.GetEditedSceneRoot();
		ur.CreateAction( label ?? command.Description, UndoRedo.MergeMode.Disable, context );
		ur.AddDoMethod( redo, EffigyActionTarget.MethodName.Execute );
		ur.AddUndoMethod( undo, EffigyActionTarget.MethodName.Execute );
		ur.CommitAction( execute: false );
		return true;
	}

	// =========================================================================================
	// Bones, shape keys, posing
	// =========================================================================================

	/// <summary>The model's shape keys (morph targets), in mesh order.</summary>
	public IReadOnlyList<string> MorphNames()
	{
		if ( Doc == null ) return Array.Empty<string>();
		try { return Doc.Studio.ToMeshWithBodies().Mesh.ValidMorphs.Select( m => m.Name ).Distinct().ToList(); }
		catch ( Exception e ) { GD.PushWarning( $"[Geppetto] Could not read shape keys: {e.Message}" ); return Array.Empty<string>(); }
	}

	/// <summary>The Body mesh under the character (where shape keys show).</summary>
	public MeshInstance3D? Body => Root.FindChild( "Body", true, false ) as MeshInstance3D;

	/// <summary>Bone names in rig order: the Timeline's lanes.</summary>
	public IReadOnlyList<string> BoneNames => Rig?.Bones.Select( b => b.Name ).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

	/// <summary>Keyed frames of one bone's track.</summary>
	public IEnumerable<int> KeyFrames( string bone ) =>
		Clip?.BoneTracks.FirstOrDefault( t => t.Name == bone )?.Keys.Select( k => k.Frame ) ?? Enumerable.Empty<int>();

	/// <summary>Poses the skeleton (and the shape keys) at a frame from the clip.</summary>
	public void ScrubTo( int frame )
	{
		if ( Clip == null || Rig == null || Skeleton == null ) return;
		EffigyPoseBridge.ApplyClipPose( Clip, Rig, Skeleton, frame / (double)Fps );
		if ( Body is { Mesh: ArrayMesh m } body && m.GetBlendShapeCount() > 0 )
		{
			var w = Clip.MorphWeightsAt( frame );
			for ( int i = 0; i < m.GetBlendShapeCount(); i++ )
				body.SetBlendShapeValue( i, w.TryGetValue( m.GetBlendShapeName( i ), out var v ) ? v : 0f );
		}
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

	/// <summary>The bone First Person View looks from: one named head, else the highest bone at rest.</summary>
	public string? HeadBone()
	{
		if ( Skeleton == null || Skeleton.GetBoneCount() == 0 ) return null;
		var all = Enumerable.Range( 0, Skeleton.GetBoneCount() ).ToList();
		foreach ( var want in new[] { "head", "eye", "neck" } )
			foreach ( var b in all )
				if ( Skeleton.GetBoneName( b ).Contains( want, StringComparison.OrdinalIgnoreCase ) ) return Skeleton.GetBoneName( b );
		return Skeleton.GetBoneName( all.OrderByDescending( b => Skeleton.GetBoneGlobalRest( b ).Origin.Y ).First() );
	}
}
#endif
