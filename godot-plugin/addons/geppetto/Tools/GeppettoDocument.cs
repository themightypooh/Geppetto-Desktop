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
/// One open, editable .effigy: the live <see cref="PartStudio"/>, its <see cref="DocumentSession"/> (edit
/// history, dirty state), the <see cref="EffigySaveBridge"/> that Ctrl+S writes it through, and the clips
/// opened next to it. One per file for the whole editor, so every tool, the Timeline and the save hook see
/// the same document.
///
/// Every change goes through <see cref="Edit"/>: it runs the kernel edit (which records one step in the
/// session's <see cref="EditHistory"/>), mirrors that step into Poohdot's undo stack so Ctrl+Z / Ctrl+Shift+Z
/// walk it, and rebuilds the character's mesh and skeleton in the open scene.
/// </summary>
public sealed class GeppettoDocument
{
	static readonly Dictionary<string, GeppettoDocument> Open = new( StringComparer.Ordinal );

	public string ResPath { get; }
	public string DiskPath { get; }
	public DocumentSession Session { get; }
	public PartStudio Studio => Session.Studio!;
	public EffigySaveBridge Bridge { get; }
	public MaterialResolver? Resolver { get; }
	public RebuildReport? LastReport { get; private set; }

	/// <summary>Raised on the main thread after every rebuild (an edit, an undo, a redo, a preview).</summary>
	public event Action? Changed;

	/// <summary>Raised for every applied edit with its undo label, e.g. for the feature list.</summary>
	public static event Action<GeppettoDocument>? AnyChanged;

	/// <summary>The editor's undo manager. Set by the plugin; null in headless tests (edits still record
	/// in the session history, they just don't appear on Poohdot's Ctrl+Z).</summary>
	public static EditorUndoRedoManager? UndoManager { get; set; }

	GeppettoDocument( string resPath, string diskPath, PartStudio studio )
	{
		ResPath = resPath;
		DiskPath = diskPath;
		Session = DocumentSession.CreateEffigy( diskPath, studio );
		Bridge = new EffigySaveBridge( Session );
		string? dir = Path.GetDirectoryName( diskPath );
		if ( !string.IsNullOrEmpty( dir ) && Directory.Exists( dir ) )
			Resolver = new MaterialResolver( dir, Path.GetFileNameWithoutExtension( diskPath ) );
		LastReport = studio.Rebuild();
	}

	/// <summary>The open document for a .effigy (res:// or absolute), reading it the first time.</summary>
	public static GeppettoDocument Get( string path )
	{
		string disk = Path.GetFullPath( path.StartsWith( "res://" ) || path.StartsWith( "user://" ) ? ProjectSettings.GlobalizePath( path ) : path );
		if ( Open.TryGetValue( disk, out var doc ) ) return doc;
		string res = path.StartsWith( "res://" ) ? path : ProjectSettings.LocalizePath( disk );
		doc = new GeppettoDocument( res, disk, StudioDocument.ReadFile( disk ) );
		Open[disk] = doc;
		return doc;
	}

	/// <summary>Forgets every open document (the plugin's teardown). Unsaved edits are lost, like closing the editor.</summary>
	public static void CloseAll()
	{
		foreach ( var d in Open.Values ) d.Bridge.Dispose();
		Open.Clear();
	}

	public static IEnumerable<GeppettoDocument> All => Open.Values;

	// =========================================================================================
	// Editing
	// =========================================================================================

	/// <summary>
	/// Runs one kernel edit as one undo step. <paramref name="edit"/> calls into
	/// <see cref="ParametricEditService"/> (or pushes its own <see cref="IEditCommand"/> on
	/// <c>Session.History</c>). Returns false and changes nothing when the edit recorded no step.
	/// </summary>
	public bool Edit( string label, Action<DocumentSession> edit )
	{
		int before = Session.History.UndoCount;
		edit( Session );
		if ( Session.History.UndoCount == before ) { Refresh(); return false; }
		Session.BumpRevision();
		Mirror( label );
		Refresh();
		return true;
	}

	/// <summary>Pushes an already-built command (e.g. <see cref="DelegateEditCommand"/>) as one undo step.</summary>
	public bool Execute( IEditCommand command, string? label = null ) =>
		Edit( label ?? command.Description, s => s.History.Execute( command ) );

	/// <summary>Adds a feature at the end of the tree (or at <paramref name="index"/>) as one undo step.</summary>
	public bool AddFeature( Feature feature, int? index = null ) =>
		Edit( $"Geppetto: add {feature.TypeName}", s => ParametricEditService.AddFeature( s, feature, index ) );

	void Mirror( string label )
	{
		var ur = UndoManager;
		if ( ur == null ) return;
		var undo = new EffigyActionTarget( () => { if ( Session.History.CanUndo ) { Session.History.Undo(); Session.BumpRevision(); Refresh(); } } );
		var redo = new EffigyActionTarget( () => { if ( Session.History.CanRedo ) { Session.History.Redo(); Session.BumpRevision(); Refresh(); } } );
		// The edited scene's root as context puts the step on that scene's history (and marks it unsaved,
		// so Ctrl+S runs the plugin's _SaveExternalData, which writes the .effigy).
		var context = EditorInterface.Singleton.GetEditedSceneRoot();
		ur.CreateAction( label, UndoRedo.MergeMode.Disable, context );
		ur.AddDoMethod( redo, EffigyActionTarget.MethodName.Execute );
		ur.AddUndoMethod( undo, EffigyActionTarget.MethodName.Execute );
		ur.CommitAction( execute: false );
	}

	// =========================================================================================
	// Showing the result
	// =========================================================================================

	/// <summary>Rebuilds the studio and pushes the result into every open scene showing this .effigy.
	/// Tools call it after previewing a change they have not committed yet.</summary>
	public void Refresh()
	{
		try { LastReport = Studio.Rebuild(); }
		catch ( Exception e ) { GD.PushError( $"[Geppetto] Rebuild failed: {e.Message}" ); return; }
		if ( LastReport?.Errors.Count > 0 )
			foreach ( var err in LastReport.Errors.Take( 3 ) ) GD.PushWarning( $"[Geppetto] {err}" );

		// Outside the editor (headless tool tests) there is no open scene to update.
		var root = Engine.IsEditorHint() && EditorInterface.Singleton != null ? EditorInterface.Singleton.GetEditedSceneRoot() : null;
		if ( root != null )
			foreach ( var node in CharacterRoots( root ) ) ApplyTo( node );
		Changed?.Invoke();
		AnyChanged?.Invoke( this );
	}

	IEnumerable<Node> CharacterRoots( Node root )
	{
		var stack = new Stack<Node>();
		stack.Push( root );
		while ( stack.Count > 0 )
		{
			var n = stack.Pop();
			if ( GeppettoCharacter.EffigyPathOf( n, root ) is { } p && SameFile( p ) ) { yield return n; continue; }
			foreach ( var c in n.GetChildren() ) stack.Push( c );
		}
	}

	bool SameFile( string path ) =>
		string.Equals( Path.GetFullPath( ProjectSettings.GlobalizePath( path ) ), DiskPath, StringComparison.Ordinal );

	/// <summary>Replaces the Body mesh (and the skeleton's bones, when the rig changed) under one character
	/// root with the current build. Mirrors <see cref="EffigySceneImporter"/>.</summary>
	public void ApplyTo( Node characterRoot )
	{
		var cpu = CpuMesh.FromStudio( Studio, 0, DiskPath );
		var mesh = EffigyMeshBridge.ToGodotArrayMesh( cpu, Resolver, Studio.MaterialLooks, Path.GetDirectoryName( DiskPath ) );
		var body = characterRoot.FindChild( "Body", true, false ) as MeshInstance3D;
		if ( body == null ) return;
		body.Mesh = mesh;

		if ( body.GetNodeOrNull( body.Skeleton ) is Skeleton3D skel && Studio.Rig is { Count: > 0 } rig && !SameBones( skel, rig ) )
		{
			var fresh = EffigySkeletonBridge.ToGodotSkeleton( rig );
			skel.ClearBones();
			for ( int i = 0; i < fresh.GetBoneCount(); i++ )
			{
				skel.AddBone( fresh.GetBoneName( i ) );
				skel.SetBoneParent( i, fresh.GetBoneParent( i ) );
				skel.SetBoneRest( i, fresh.GetBoneRest( i ) );
				skel.SetBonePose( i, fresh.GetBoneRest( i ) );
			}
			fresh.Free();
			body.Skin = EffigySkeletonBridge.CreateSkin( skel );
		}
	}

	static bool SameBones( Skeleton3D skel, Skeleton rig )
	{
		if ( skel.GetBoneCount() != rig.Count ) return false;
		for ( int i = 0; i < rig.Count; i++ ) if ( skel.GetBoneName( i ) != rig.Bones[i].Name ) return false;
		return true;
	}

	// =========================================================================================
	// Lookups the tools share
	// =========================================================================================

	public Feature? FeatureById( string id ) => Studio.Features.FirstOrDefault( f => f.Id == id );
}
#endif
