#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

// The Rig workspace (everything but Paint Weights): s&box's EffigyRigPanel and the Rig bar's tool cases
// (EffigyWindow.Workspaces.cs BuildRigStages), ported onto the live GeppettoDocument. Every rig change is
// one undo step on the document's history (mirrored onto Poohdot's Ctrl+Z), saves into the .effigy, and
// GeppettoDocument.ApplyTo rebuilds the Skeleton3D/Skin in the open scene.
public static partial class GeppettoTools
{
	static partial void RegisterRig()
	{
		// --- Bones ---
		Register( "Rig", "Add Bone", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			if ( Active is RigBoneTool ) { Activate( null ); return true; }   // checkable, like s&box
			Activate( new RigBoneTool( c ) );
			return true;
		} );

		Register( "Rig", "Bone from Part", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			Activate( new RigBodyPickTool( c, "Bone from Part",
				() => GeppettoRig.SelectedIndex( doc ) is var p && p >= 0
					? $"Click a part to lay a bone down its longest axis, parented to '{doc.Studio.Rig.Bones[p].Name}'. Esc when done."
					: "Click a part to lay a bone down its longest axis. Select a bone first to parent it. Esc when done.",
				body => { GeppettoRig.BonesFromBodies( doc, new[] { body } ); return true; } ) );
			return true;
		} );

		Register( "Rig", "Delete", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			int sel = GeppettoRig.SelectedIndex( doc );
			if ( sel < 0 ) return PickBone( c, "Delete", "Click the bone to delete. Esc to cancel.", b => GeppettoRig.DeleteBone( doc, b ) );
			GeppettoRig.DeleteBone( doc, sel );
			return true;
		} );

		// --- Bind ---
		Register( "Rig", "Assign Body", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			if ( Active is RigBodyPickTool { Name: "Assign Body" } ) { Activate( null ); return true; }
			if ( GeppettoRig.SelectedIndex( doc ) < 0 )
				return PickBone( c, "Assign Body", "Click the bone to assign bodies to. Esc to cancel.", _ => StartAssign( c, doc ) );
			StartAssign( c, doc );
			return true;
		} );

		Register( "Rig", "Mirror bones", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			int sel = GeppettoRig.SelectedIndex( doc );
			if ( sel < 0 ) return PickBone( c, "Mirror bones", "Click the bone to mirror (it and everything under it). Esc to cancel.", b => GeppettoRig.Mirror( doc, b ) );
			GeppettoRig.Mirror( doc, sel );
			return true;
		} );

		Register( "Rig", "Auto Weights", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			return GeppettoRig.AutoWeights( doc );
		} );

		Register( "Rig", "Import Skeleton", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			foreach ( var n in EditorInterface.Singleton.GetSelection().GetSelectedNodes() )
				if ( n is Skeleton3D skel )
					return GeppettoRig.ImportSkeleton( doc, skel );
			return GeppettoRig.Refuse( "Import Skeleton: select a Skeleton3D in the scene dock first — "
				+ "import a character scene, select its Skeleton3D, then run this." );
		} );

		Register( "Rig", "Rig Template", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			GeppettoToolContext.Say( "Pick a template in the Rig panel's Template row, then press Template." );
			return true;
		} );

		// --- Weights ---
		Register( "Rig", "Mirror Weights", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			return GeppettoRig.MirrorWeights( doc );
		} );

		Register( "Rig", "Fix Weights", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			return GeppettoRig.FixWeights( doc );
		} );

		Register( "Rig", "Copy Weights", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			return GeppettoRig.CopyWeights( doc );
		} );

		Register( "Rig", "Paste Weights", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			return GeppettoRig.PasteWeights( doc );
		} );

		Register( "Rig", "Delete Chain", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			int sel = GeppettoRig.SelectedIndex( doc );
			if ( sel < 0 ) return PickBone( c, "Delete Chain", "Click the bone whose chain to delete. Esc to cancel.", b => GeppettoRig.DeleteChain( doc, b ) );
			GeppettoRig.DeleteChain( doc, sel );
			return true;
		} );

		Register( "Rig", "Export Playermodel", ctx =>
		{
			if ( ctx.Doc is not { } doc ) return ctx.Need();
			return GeppettoRig.ExportPlayermodel( doc );
		} );

		// --- Soft ---
		Register( "Rig", "Make Soft", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			RigPanel.Open( ctx.Plugin, c );
			int sel = GeppettoRig.SelectedIndex( doc );
			if ( sel < 0 ) return PickBone( c, "Make Soft", "Click the bone to make soft (or rigid again). Esc to cancel.", b => GeppettoRig.SetSoft( doc, b, doc.Studio.Rig.Bones[b].Soft == null ) );
			GeppettoRig.SetSoft( doc, sel, doc.Studio.Rig.Bones[sel].Soft == null );
			return true;
		} );

		Register( "Rig", "Preview", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			if ( Active is RigSoftPreview ) { Activate( null ); GeppettoToast.Info( "Soft preview off." ); return true; }
			if ( doc.Studio.Rig.Count == 0 ) return GeppettoRig.Refuse( "Preview: there are no bones to swing yet. Place a bone first." );
			Activate( new RigSoftPreview( c ) );
			GeppettoToast.Info( SoftBoneCount( doc ) == 0
				? "Soft preview on, but no bone is soft yet: pick a bone and press Make Soft to see it swing."
				: "Soft preview on: soft bones hang and lag behind the pose. Pose a bone to push them; Rest settles them." );
			return true;
		} );

		// s&box's Rest: "Forget the motion and put every soft bone back on its pose" (RestSoftPreview).
		// It only means something while the soft preview runs.
		Register( "Rig", "Rest", ctx =>
		{
			if ( ctx.Doc == null ) return ctx.Need();
			if ( Active is RigSoftPreview preview )
			{
				preview.Rest();
				GeppettoToast.Info( "Soft bones back on their pose." );
				return true;
			}
			// No preview running: put the skeleton itself back on its rest pose (what Rest means to an
			// animator), so a posed character can be bound and checked from rest.
			if ( ctx.Character?.Skeleton is not { } skel || !GodotObject.IsInstanceValid( skel ) ) return ctx.Need();
			var before = new List<(Vector3 P, Quaternion R, Vector3 S)>();
			for ( int i = 0; i < skel.GetBoneCount(); i++ ) before.Add( ( skel.GetBonePosePosition( i ), skel.GetBonePoseRotation( i ), skel.GetBonePoseScale( i ) ) );
			var ur = EditorInterface.Singleton.GetEditorUndoRedo();
			ur.CreateAction( "Geppetto: rest pose", UndoRedo.MergeMode.Disable, skel );
			ur.AddDoMethod( skel, Skeleton3D.MethodName.ResetBonePoses );
			for ( int i = 0; i < before.Count; i++ )
			{
				ur.AddUndoMethod( skel, Skeleton3D.MethodName.SetBonePosePosition, i, before[i].P );
				ur.AddUndoMethod( skel, Skeleton3D.MethodName.SetBonePoseRotation, i, before[i].R );
				ur.AddUndoMethod( skel, Skeleton3D.MethodName.SetBonePoseScale, i, before[i].S );
			}
			ur.CommitAction();
			GeppettoToast.Info( "Rest: every bone is back on its rest pose (the soft preview isn't running, so that is what Rest did)." );
			return true;
		} );

		// --- Mesh ---
		Register( "Rig", "Subdivide", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			Activate( new RigBodyPickTool( c, "Subdivide",
				() => "Click the part to subdivide. It gets a Subdivide set to All Faces: density without moving the shape.",
				body =>
				{
					var f = new SubdivideFeature();
					f.ApplyGeometrySelection( Array.Empty<FaceRef>(), new[] { body.Id }, doc.Studio.Bodies );
					f.AllFaces.Value = true;
					GeppettoFeatureDialog.ShowNew( doc, f );
					return false;
				} ) );
			return true;
		} );

		Register( "Rig", "Remesh", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } c ) return ctx.Need();
			Activate( new RigBodyPickTool( c, "Remesh",
				() => "Click the part to remesh down to a triangle budget so it can be weighted and posed.",
				body =>
				{
					var f = new RemeshFeature();
					f.ApplyGeometrySelection( Array.Empty<FaceRef>(), new[] { body.Id }, doc.Studio.Bodies );
					GeppettoFeatureDialog.ShowNew( doc, f );
					return false;
				} ) );
			return true;
		} );

		// --- Export ---
		Register( "Rig", "Check model", ctx =>
		{
			if ( ctx.Doc is not { } doc ) return ctx.Need();
			GeppettoRig.CheckModel( doc );
			return true;
		} );

		Register( "Rig", "Wrinkle Maps", ctx =>
			ctx.Doc == null ? ctx.Need() : GeppettoRig.Refuse( "Wrinkle Maps is an s&box .vmdl compile switch. Poohdot has no .vmdl compile, so nothing to switch." ) );
	}

	/// <summary>No bone picked in the tree: ask for a click on one in the viewport, then run the action on it.</summary>
	static bool PickBone( GeppettoCharacter c, string tool, string prompt, Action<int> action )
	{
		if ( c.Doc!.Studio.Rig.Count == 0 ) return GeppettoRig.Refuse( $"{tool}: there are no bones yet. Add Bone or Bone from Part first." );
		if ( Active is RigBonePickTool { } p && p.Name == tool ) { Activate( null ); return true; }
		Activate( new RigBonePickTool( c, tool, prompt, action ) );
		return true;
	}

	static void StartAssign( GeppettoCharacter c, GeppettoDocument doc )
	{
		Activate( new RigBodyPickTool( c, "Assign Body",
			() => GeppettoRig.SelectedIndex( doc ) is var b && b >= 0
				? $"Click bodies to assign to '{doc.Studio.Rig.Bones[b].Name}' - click again to unassign one. Esc when done."
				: "No bone is picked any more. Pick one in the Rig panel's tree, or press Esc.",
			body =>
			{
				int bone = GeppettoRig.SelectedIndex( doc );
				if ( bone < 0 ) return GeppettoRig.Refuse( "Can't assign: no bone is picked. Pick the bone in the Rig panel's tree first." );
				GeppettoRig.ToggleBody( doc, body.Id, bone );
				return true;
			} ) );
	}

	static int SoftBoneCount( GeppettoDocument doc ) => SoftSolver.SoftBones( doc.Studio.Rig ).Count();
}

/// <summary>
/// The rig edits behind the Rig tools and the Rig panel, each one undo step on the document. Static and
/// UI-free so the headless test drives exactly what the tools run.
/// </summary>
public static class GeppettoRig
{
	// =========================================================================================
	// Selection (the Rig panel's tree; kept by name so it survives reorders, undo and reloads)
	// =========================================================================================

	static readonly Dictionary<string, string> Selected = new();

	/// <summary>Raised when the picked bone changes.</summary>
	public static event Action<GeppettoDocument>? SelectionChanged;

	public static int SelectedIndex( GeppettoDocument doc ) =>
		Selected.TryGetValue( doc.DiskPath, out var name ) ? doc.Studio.Rig.IndexOf( name ) : -1;

	public static void Select( GeppettoDocument doc, int index )
	{
		var rig = doc.Studio.Rig;
		string? name = index >= 0 && index < rig.Count ? rig.Bones[index].Name : null;
		Selected.TryGetValue( doc.DiskPath, out var was );
		if ( name == was ) return;
		if ( name == null ) Selected.Remove( doc.DiskPath ); else Selected[doc.DiskPath] = name;
		SelectionChanged?.Invoke( doc );
		GeppettoTools.Redraw();
	}

	/// <summary>Warns (in the Output panel and the editor toast) and returns true: the tool ran, it just
	/// refused. Same wording s&amp;box's Refuse put in its prompt.</summary>
	public static bool Refuse( string message )
	{
		GeppettoToolContext.Warn( $"{message}" );
		return true;
	}

	// =========================================================================================
	// One undo step for any rig change
	// =========================================================================================

	/// <summary>Everything a rig edit can touch: the bones, which part is pinned to which bone, and the
	/// bone names the corrective shapes are keyed on.</summary>
	sealed class Snapshot
	{
		public readonly List<Bone> Bones;
		public readonly Dictionary<string, string> BodyBoneMap;
		public readonly List<(CorrectiveShape Shape, string Bone)> Correctives;
		public readonly string RootBone;
		public readonly string HipsBone;

		public Snapshot( PartStudio s )
		{
			Bones = s.Rig.Bones.Select( b => b.Clone() ).ToList();
			BodyBoneMap = new Dictionary<string, string>( s.BodyBoneMap );
			Correctives = s.Correctives.Select( c => (c, c.Bone) ).ToList();
			RootBone = s.Rig.RootBone;
			HipsBone = s.Rig.HipsBone;
		}

		public void Restore( PartStudio s )
		{
			s.Rig.Bones.Clear();
			s.Rig.Bones.AddRange( Bones.Select( b => b.Clone() ) );
			s.BodyBoneMap.Clear();
			foreach ( var kv in BodyBoneMap ) s.BodyBoneMap[kv.Key] = kv.Value;
			foreach ( var (shape, bone) in Correctives ) shape.Bone = bone;
			s.Rig.RootBone = RootBone;
			s.Rig.HipsBone = HipsBone;
		}

		public bool Same( Snapshot o )
		{
			if ( Bones.Count != o.Bones.Count || BodyBoneMap.Count != o.BodyBoneMap.Count ) return false;
			if ( RootBone != o.RootBone || HipsBone != o.HipsBone ) return false;
			for ( int i = 0; i < Bones.Count; i++ )
			{
				Bone a = Bones[i], b = o.Bones[i];
				if ( a.Name != b.Name || a.Parent != b.Parent || a.Length != b.Length || !a.Local.Equals( b.Local ) ) return false;
				if ( ( a.Soft == null ) != ( b.Soft == null ) ) return false;
				if ( a.Soft != null && ( a.Soft.Stiffness != b.Soft!.Stiffness || a.Soft.Damping != b.Soft.Damping
					|| a.Soft.Weight != b.Soft.Weight || a.Soft.MaxAngle != b.Soft.MaxAngle ) ) return false;
			}
			foreach ( var kv in BodyBoneMap )
				if ( !o.BodyBoneMap.TryGetValue( kv.Key, out var v ) || v != kv.Value ) return false;
			for ( int i = 0; i < Correctives.Count; i++ ) if ( Correctives[i].Bone != o.Correctives[i].Bone ) return false;
			return true;
		}
	}

	/// <summary>
	/// Runs <paramref name="mutate"/> on the live studio as one undo step. The mutation runs once to
	/// find out what it does (it may throw ArgumentException, which is turned into a refusal and leaves
	/// the rig untouched); the before/after snapshots then become a command on the document's history,
	/// so undo and redo restore whole rig states rather than replaying the edit.
	/// </summary>
	public static bool RigEdit( GeppettoDocument doc, string label, Func<PartStudio, bool> mutate )
	{
		var studio = doc.Studio;
		var before = new Snapshot( studio );
		bool did;
		try { did = mutate( studio ); }
		catch ( Exception e ) when ( e is ArgumentException or InvalidOperationException )
		{
			before.Restore( studio );
			return !Refuse( $"{label}: {e.Message}" );
		}
		var after = new Snapshot( studio );
		if ( !did || after.Same( before ) ) { before.Restore( studio ); return false; }
		before.Restore( studio );
		return doc.Execute( new DelegateEditCommand( label, () => after.Restore( studio ), () => before.Restore( studio ) ), $"Geppetto: {label}" );
	}

	// =========================================================================================
	// Bones
	// =========================================================================================

	/// <summary>"bone_{n}", the lowest n not taken (s&amp;box NextBoneName).</summary>
	public static string NextBoneName( Skeleton rig )
	{
		int n = rig.Count + 1;
		while ( rig.IndexOf( $"bone_{n}" ) >= 0 ) n++;
		return $"bone_{n}";
	}

	/// <summary>Adds a bone from a head and a tail (Effigy space, inches) under <paramref name="parent"/>
	/// (-1 for a root) and selects it. Returns the new index, or -1 when refused.</summary>
	public static int AddBone( GeppettoDocument doc, int parent, Vec3 head, Vec3 tail, string? name = null )
	{
		if ( ( tail - head ).Length < 0.01f ) { Refuse( "Bone too short — click further from the last joint." ); return -1; }
		var rig = doc.Studio.Rig;
		name ??= NextBoneName( rig );
		if ( !RigEdit( doc, $"add bone {name}", s => { s.Rig.AddBoneFromPoints( name, parent, head, tail ); return true; } ) ) return -1;
		int index = rig.IndexOf( name );
		Select( doc, index );
		return index;
	}

	/// <summary>s&amp;box MakeBonesFromBodies: a bone down each part's longest axis, pinned to it, parented
	/// to the picked bone (whose tail tells the new bone which end is the root).</summary>
	public static int BonesFromBodies( GeppettoDocument doc, IReadOnlyList<Body> bodies )
	{
		var rig = doc.Studio.Rig;
		int parent = SelectedIndex( doc );
		Vec3? anchor = parent >= 0 ? rig.TailWorld( parent ) : null;

		var derived = new List<(Body Body, Vec3 Head, Vec3 Tail, Vec3 Up)>();
		var skipped = new List<string>();
		foreach ( var body in bodies )
		{
			if ( BoneFromBody.TryDerive( body.Mesh, out var head, out var tail, out var up, anchor ) ) derived.Add( (body, head, tail, up) );
			else skipped.Add( body.Name );
		}

		if ( derived.Count == 0 )
		{
			Refuse( $"Can't make a bone from {string.Join( ", ", skipped )}: {( skipped.Count == 1 ? "it has" : "they have" )} no long axis to lay a bone along (a sphere or a cube has none). Place the bone by hand instead." );
			return 0;
		}

		string? last = null;
		bool ok = RigEdit( doc, derived.Count == 1 ? $"bone from {derived[0].Body.Name}" : $"{derived.Count} bones from parts", s =>
		{
			foreach ( var (body, head, tail, up) in derived )
			{
				var name = s.Rig.UniqueName( BoneNameFor( body.Name ) );
				s.Rig.AddBoneFromPoints( name, parent, head, tail, up );
				s.BodyBoneMap[body.Id] = name;
				last = name;
			}
			return true;
		} );
		if ( !ok ) return 0;
		if ( last != null ) Select( doc, rig.IndexOf( last ) );

		if ( skipped.Count > 0 )
			Refuse( $"Made {derived.Count} bone{( derived.Count == 1 ? "" : "s" )}, but not for {string.Join( ", ", skipped )}: no long axis to lay a bone along. Place {( skipped.Count == 1 ? "that one" : "those" )} by hand." );
		else
			GeppettoToolContext.Say( derived.Count == 1 ? $"Made bone {last} from the part's shape." : $"Made {derived.Count} bones from the parts' shapes." );
		return derived.Count;
	}

	/// <summary>A part's name as a bone name: whitespace runs become "_", nothing else changes.</summary>
	public static string BoneNameFor( string bodyName )
	{
		if ( string.IsNullOrWhiteSpace( bodyName ) ) return "bone";
		return string.Join( "_", bodyName.Split( (char[]?)null, StringSplitOptions.RemoveEmptyEntries ) );
	}

	/// <summary>Deletes a bone; its children re-parent to its parent and its parts fall back to the
	/// nearest bone.</summary>
	public static bool DeleteBone( GeppettoDocument doc, int index )
	{
		var rig = doc.Studio.Rig;
		if ( index < 0 || index >= rig.Count ) return !Refuse( "Delete: that bone no longer exists." );
		string name = rig.Bones[index].Name;
		bool ok = RigEdit( doc, $"delete bone {name}", s =>
		{
			s.Rig.RemoveBone( index );
			foreach ( var body in s.BodyBoneMap.Where( kv => kv.Value == name ).Select( kv => kv.Key ).ToList() )
				s.BodyBoneMap.Remove( body );
			if ( s.Rig.RootBone == name ) s.Rig.RootBone = "";
			if ( s.Rig.HipsBone == name ) s.Rig.HipsBone = "";
			return true;
		} );
		if ( ok ) { Select( doc, -1 ); GeppettoToolContext.Say( $"Deleted bone {name}." ); }
		return ok;
	}

	/// <summary>Renames a bone. Pinned parts, corrective shapes and painted weights follow it.</summary>
	public static bool RenameBone( GeppettoDocument doc, int index, string? newName )
	{
		var studio = doc.Studio;
		var rig = studio.Rig;
		if ( index < 0 || index >= rig.Count ) return !Refuse( "Rename: that bone no longer exists." );
		string oldName = rig.Bones[index].Name;
		string name = newName?.Trim() ?? "";
		if ( name.Length == 0 ) return !Refuse( $"Can't rename '{oldName}' to nothing: every bone needs a name. Kept '{oldName}'." );
		if ( name == oldName ) return false;
		int existing = rig.IndexOf( name );
		if ( existing >= 0 && existing != index ) return !Refuse( $"Can't rename to '{name}': another bone already has that name. Pick a different one." );

		// Painted weights are keyed by bone name too. The layer is too big to snapshot, so the rename is
		// its own command that renames there and back.
		void Apply( string from, string to )
		{
			int i = rig.IndexOf( from );
			if ( i >= 0 ) rig.RenameBone( i, to );
			foreach ( var body in studio.BodyBoneMap.Where( kv => kv.Value == from ).Select( kv => kv.Key ).ToList() ) studio.BodyBoneMap[body] = to;
			foreach ( var c in studio.Correctives.Where( c => c.Bone == from ) ) c.Bone = to;
			if ( rig.RootBone == from ) rig.RootBone = to;
			if ( rig.HipsBone == from ) rig.HipsBone = to;
			studio.WeightPaint?.RenameBone( from, to );
			if ( Selected.TryGetValue( doc.DiskPath, out var sel ) && sel == from ) Selected[doc.DiskPath] = to;
		}

		bool ok = doc.Execute( new DelegateEditCommand( $"rename bone {oldName} to {name}", () => Apply( oldName, name ), () => Apply( name, oldName ) ),
			$"Geppetto: rename bone {oldName} to {name}" );
		if ( ok ) SelectionChanged?.Invoke( doc );
		return ok;
	}

	/// <summary>Hangs a bone under another (-1 makes it a root), keeping its world pose. Returns the
	/// bone's new index (the list may reorder), or -1 when refused.</summary>
	public static int Reparent( GeppettoDocument doc, int index, int newParent )
	{
		var rig = doc.Studio.Rig;
		if ( index < 0 || index >= rig.Count ) { Refuse( "Parent to: that bone no longer exists." ); return -1; }
		string name = rig.Bones[index].Name;
		string target = newParent >= 0 && newParent < rig.Count ? rig.Bones[newParent].Name : "none (root)";
		if ( !RigEdit( doc, $"parent {name} to {target}", s => { s.Rig.SetParent( index, newParent ); return true; } ) ) return -1;
		int moved = rig.IndexOf( name );
		Select( doc, moved );
		return moved;
	}

	/// <summary>Bones a bone may be parented to without making a cycle (everything outside its own subtree).</summary>
	public static List<int> ParentChoices( Skeleton rig, int index )
	{
		var list = new List<int>();
		for ( int i = 0; i < rig.Count; i++ )
		{
			bool inSubtree = false;
			for ( int p = i; p >= 0; p = rig.Bones[p].Parent ) if ( p == index ) { inSubtree = true; break; }
			if ( !inSubtree ) list.Add( i );
		}
		return list;
	}

	/// <summary>Mirrors a bone and everything under it across Y=0 (Effigy's left/right axis), onto the
	/// same parent, swapping _L/_R and .L/.R. Selects and returns the new root, or -1.</summary>
	public static int Mirror( GeppettoDocument doc, int index )
	{
		var rig = doc.Studio.Rig;
		if ( index < 0 || index >= rig.Count ) { Refuse( "Mirror bones: that bone no longer exists." ); return -1; }
		int parent = rig.Bones[index].Parent;
		int newRoot = -1;
		if ( !RigEdit( doc, $"mirror {rig.Bones[index].Name}", s => { newRoot = s.Rig.MirrorSubtree( index, new Vec3( 0, 1, 0 ), parent ); return true; } ) )
			return -1;
		// RigEdit re-applied the after-snapshot, which has the same order MirrorSubtree produced.
		Select( doc, newRoot );
		GeppettoToolContext.Say( $"Mirrored to {rig.Bones[newRoot].Name}." );
		return newRoot;
	}

	/// <summary>Moves a bone's head and tail (Effigy space), the inspector's typed fields.</summary>
	public static bool SetHeadTail( GeppettoDocument doc, int index, Vec3 head, Vec3 tail )
	{
		var rig = doc.Studio.Rig;
		if ( index < 0 || index >= rig.Count ) return false;
		if ( ( tail - head ).Length < 1e-4f ) return !Refuse( "A bone's head and tail can't be in the same place. Kept the bone as it was." );
		return RigEdit( doc, $"move bone {rig.Bones[index].Name}", s => { s.Rig.SetHeadTail( index, head, tail ); return true; } );
	}

	// =========================================================================================
	// Bodies
	// =========================================================================================

	/// <summary>s&amp;box OnBodyPicked: pins a part to the bone, or unpins it if it is already there.</summary>
	public static bool ToggleBody( GeppettoDocument doc, string bodyId, int bone )
	{
		var rig = doc.Studio.Rig;
		if ( bone < 0 || bone >= rig.Count || string.IsNullOrEmpty( bodyId ) ) return false;
		string boneName = rig.Bones[bone].Name;
		bool remove = doc.Studio.BodyBoneMap.TryGetValue( bodyId, out var cur ) && cur == boneName;
		string partName = doc.Studio.Bodies.FirstOrDefault( b => b.Id == bodyId )?.Name ?? bodyId;
		bool ok = RigEdit( doc, remove ? $"unpin {partName}" : $"pin {partName} to {boneName}", s =>
		{
			if ( remove ) s.BodyBoneMap.Remove( bodyId ); else s.BodyBoneMap[bodyId] = boneName;
			return true;
		} );
		if ( ok ) GeppettoToolContext.Say( remove ? $"Unpinned {partName} from '{boneName}'." : $"Pinned {partName} to '{boneName}'." );
		return ok;
	}

	public static bool UnassignBody( GeppettoDocument doc, string bodyId ) =>
		RigEdit( doc, "unpin part", s => s.BodyBoneMap.Remove( bodyId ) );

	public static List<Body> BodiesOnBone( GeppettoDocument doc, string boneName ) =>
		doc.Studio.Bodies.Where( b => doc.Studio.BodyBoneMap.TryGetValue( b.Id, out var n ) && n == boneName ).ToList();

	// =========================================================================================
	// Weights
	// =========================================================================================

	/// <summary>Automatic skin weights: bone heat over the surface (no thigh-to-thigh leak),
	/// falling back to geodesic voxel where heat has no answer, with a BindReport saying which
	/// method ran where. Unlike s&amp;box, Heat is wired in from day one (s&amp;box's
	/// RigFeatures declared a HeatBinder hook and never assigned it). The result is baked into
	/// the studio's WeightPaint layer as one undo step, so Paint Weights paints over it and the
	/// .effigy saves it. Bodies pinned with Assign Body stay rigid to their pinned bone.</summary>
	public static bool AutoWeights( GeppettoDocument doc, BindMethod method = BindMethod.Heat )
	{
		var studio = doc.Studio;
		var rig = studio.Rig;
		if ( rig.Count == 0 ) return !Refuse( "Auto Weights needs a skeleton — place a bone first." );

		var (mesh, ranges) = studio.ToMeshWithBodies();
		if ( mesh.VertexCount == 0 ) return !Refuse( "Auto Weights needs a body to weight — add a primitive or extrude a sketch first." );

		SkinWeights weights;
		BindReport report;
		try
		{
			weights = method == BindMethod.Voxel
				? SkinBinder.BindVoxel( mesh, rig, out report, new AutoWeightOptions() )
				: SkinBinder.BindHeat( mesh, rig, out report, new AutoWeightOptions(), ranges );
		}
		catch ( Exception e ) { return !Refuse( $"Auto Weights could not bind the mesh: {e.Message}" ); }

		// Assign Body pins win: a pinned body's vertices go 100% to their pinned bone.
		foreach ( var range in ranges )
			if ( studio.BodyBoneMap.TryGetValue( range.BodyId, out var boneName ) )
			{
				int bone = rig.IndexOf( boneName );
				if ( bone < 0 ) continue;
				var pinned = new[] { new BoneWeight( bone, 1f ) };
				for ( int v = range.Start; v < range.Start + range.Count && v < weights.Count; v++ )
					weights[v] = pinned;
			}

		var layer = new WeightPaintLayer( mesh );
		for ( int v = 0; v < weights.Count; v++ )
			layer.Capture( v, weights[v], rig );

		bool had = studio.WeightPaint is { Count: > 0 };
		var before = studio.WeightPaint;
		var after = layer;
		bool ok = doc.Execute( new DelegateEditCommand( "Auto weights",
			execute: () => studio.WeightPaint = after,
			undo: () => studio.WeightPaint = before ), "Geppetto: auto weights" );
		if ( !ok ) return false;

		foreach ( var note in report.Notes ) GD.Print( $"[weights] {note}" );
		GeppettoToolContext.Say( $"Auto weights: {report.Used} ran over {mesh.VertexCount} vertices"
			+ ( report.RigidVertices > 0 ? $", {report.RigidVertices} rigid" : "" )
			+ ( had ? ", replacing your earlier paint (undo brings it back)" : "" )
			+ ". See the Output panel for the bind report." );
		return true;
	}

	// =========================================================================================
	// Skeleton import + templates
	// =========================================================================================

	/// <summary>Inverse of EffigySkeletonBridge.TransformXform: a Godot local bone rest back to
	/// Effigy space (inches, Effigy axes). The bridge maps (x,y,z)->(y,z,x)*0.0254, so this maps
	/// (X,Y,Z)->(Z,X,Y)/0.0254 on both the basis columns and the origin.</summary>
	static Xform XformFromGodotRest( Transform3D rest )
	{
		const float m2in = 1f / 0.0254f;
		var b = rest.Basis;
		var o = rest.Origin;
		return new Xform(
			new Vec3( b.Z.Z, b.Z.X, b.Z.Y ),
			new Vec3( b.X.Z, b.X.X, b.X.Y ),
			new Vec3( b.Y.Z, b.Y.X, b.Y.Y ),
			new Vec3( o.Z, o.X, o.Y ) * m2in );
	}

	/// <summary>A Godot Skeleton3D as an Effigy skeleton: names, parents and exact rest poses
	/// survive; lengths are geometric (head to the average child head, 4 inches for a leaf),
	/// because Godot stores no bone length.</summary>
	public static Skeleton SkeletonFromGodot( Skeleton3D skel )
	{
		int n = skel.GetBoneCount();
		if ( n == 0 ) throw new ArgumentException( "that Skeleton3D has no bones" );
		var names = new string[n];
		var parents = new int[n];
		var locals = new Xform[n];
		for ( int i = 0; i < n; i++ )
		{
			names[i] = skel.GetBoneName( i );
			if ( string.IsNullOrWhiteSpace( names[i] ) ) throw new ArgumentException( $"bone {i} has no name" );
			parents[i] = skel.GetBoneParent( i );
			if ( parents[i] < -1 || parents[i] >= n ) throw new ArgumentException( $"bone '{names[i]}' has an impossible parent {parents[i]}" );
			locals[i] = XformFromGodotRest( skel.GetBoneRest( i ) );
		}
		// World xforms, parents before children.
		var worlds = new Xform[n];
		var done = new bool[n];
		for ( int pass = 0; pass <= n; pass++ )
		{
			bool progress = false;
			for ( int i = 0; i < n; i++ )
			{
				if ( done[i] ) continue;
				int p = parents[i];
				if ( p >= 0 && !done[p] ) continue;
				worlds[i] = p < 0 ? locals[i] : worlds[p] * locals[i];
				done[i] = true;
				progress = true;
			}
			if ( done.All( d => d ) ) break;
			if ( !progress ) throw new InvalidOperationException( "a bone cycle in the Skeleton3D" );
		}
		var sk = new Skeleton();
		var map = new int[n];
		for ( int i = 0; i < n; i++ ) map[i] = -1;
		int added = 0;
		while ( added < n )
		{
			bool progress = false;
			for ( int i = 0; i < n; i++ )
			{
				if ( map[i] >= 0 ) continue;
				int p = parents[i];
				if ( p >= 0 && map[p] < 0 ) continue;
				map[i] = sk.AddBone( names[i], p < 0 ? -1 : map[p], locals[i], 1f );
				added++;
				progress = true;
			}
			if ( !progress ) throw new InvalidOperationException( "a bone cycle in the Skeleton3D" );
		}
		for ( int i = 0; i < n; i++ )
		{
			var kids = new List<int>();
			for ( int k = 0; k < n; k++ ) if ( parents[k] == i ) kids.Add( k );
			Vec3 head = worlds[i].Origin;
			Vec3 tail = head + worlds[i].Y.Normal * 4f;
			if ( kids.Count > 0 )
			{
				var avg = Vec3.Zero;
				foreach ( int k in kids ) avg += worlds[k].Origin;
				tail = avg * ( 1f / kids.Count );
			}
			float len = ( tail - head ).Length;
			sk.Bones[map[i]].Length = len > 1e-4f ? len : 4f;
		}
		return sk;
	}

	/// <summary>Appends a Skeleton3D's bones to the rig as new roots (names made unique), one
	/// undo step. Select in the scene dock, then run.</summary>
	public static bool ImportSkeleton( GeppettoDocument doc, Skeleton3D skel )
	{
		Skeleton conv;
		try { conv = SkeletonFromGodot( skel ); }
		catch ( Exception e ) when ( e is ArgumentException or InvalidOperationException )
		{ return !Refuse( $"Import Skeleton: {e.Message}" ); }
		var rig = doc.Studio.Rig;
		int first = rig.Count;
		bool ok = RigEdit( doc, $"import skeleton ({conv.Count} bones)", s =>
		{
			foreach ( var b in conv.Bones )
				s.Rig.AddBone( s.Rig.UniqueName( b.Name ), b.Parent < 0 ? -1 : first + b.Parent, b.Local, b.Length );
			return true;
		} );
		if ( !ok ) return false;
		Select( doc, first );
		RigPanel.RefreshOpen();
		GeppettoToolContext.Say( $"Imported {conv.Count} bones from '{skel.Name}'. Clashing names were made unique." );
		return true;
	}

	/// <summary>Starts the rig from a template (human, biped, quadruped, fish, avian, arachnid,
	/// serpentine, ...). Needs an empty rig — templates replace, they don't merge.</summary>
	public static bool ApplyTemplate( GeppettoDocument doc, string name )
	{
		var rig = doc.Studio.Rig;
		if ( rig.Count > 0 ) return !Refuse( "Rig Template needs an empty rig — delete the bones first, then apply the template." );
		Skeleton tpl;
		try { tpl = RigTemplates.BuildTemplate( name ); }
		catch ( Exception e ) { return !Refuse( $"Rig Template: {e.Message}" ); }
		if ( tpl.Count == 0 ) return !Refuse( $"Rig Template '{name}': the template built no bones." );
		bool ok = RigEdit( doc, $"rig template {name}", s =>
		{
			s.Rig.Bones.Clear();
			s.Rig.Bones.AddRange( tpl.Bones.Select( b => b.Clone() ) );
			s.BodyBoneMap.Clear();
			return true;
		} );
		if ( !ok ) return false;
		Select( doc, 0 );
		RigPanel.RefreshOpen();
		GeppettoToolContext.Say( $"Template '{name}': {tpl.Count} bones. Reshape them to the character, then run Auto Weights." );
		return true;
	}

	// =========================================================================================
	// Weight tools: mirror, fix, copy/paste
	// =========================================================================================

	/// <summary>Mirrors painted weights across Y=0 (Effigy's left/right axis): every painted
	/// vertex on the +Y side donates its weights to the nearest vertex on the −Y side, with
	/// _L/_R (and .L/.R, _l/_r) name flipping via the same convention the bone mirror uses.
	/// Pure and UI-free so the headless test drives exactly what the tool runs.</summary>
	public static (WeightPaintLayer Layer, int Mirrored, int Pairs) MirrorLayer(
		WeightPaintLayer layer, PolyMesh mesh, Skeleton rig )
	{
		if ( layer is not { Count: > 0 } ) throw new ArgumentException( "no painted weights to mirror" );
		if ( mesh.VertexCount == 0 ) throw new ArgumentException( "no mesh" );
		rig.MirrorPartners( out int pairs );
		if ( pairs == 0 ) throw new ArgumentException( "no _L/_R bone pairs — name bones with _L/_R suffixes first" );

		// Uniform grid so the nearest-vertex search isn't O(n^2) on a real character.
		var pos = mesh.Positions;
		var lo = pos[0]; var hi = pos[0];
		foreach ( var p in pos )
		{
			lo = new Vec3( MathF.Min( lo.x, p.x ), MathF.Min( lo.y, p.y ), MathF.Min( lo.z, p.z ) );
			hi = new Vec3( MathF.Max( hi.x, p.x ), MathF.Max( hi.y, p.y ), MathF.Max( hi.z, p.z ) );
		}
		float cell = MathF.Max( ( hi - lo ).Length / 128f, 1e-6f );
		var grid = new Dictionary<(int, int, int), List<int>>();
		static (int, int, int) Key( Vec3 p, Vec3 lo, float cell ) =>
			((int)MathF.Floor( ( p.x - lo.x ) / cell ), (int)MathF.Floor( ( p.y - lo.y ) / cell ), (int)MathF.Floor( ( p.z - lo.z ) / cell ));
		for ( int v = 0; v < pos.Count; v++ )
		{
			var k = Key( pos[v], lo, cell );
			if ( !grid.TryGetValue( k, out var list ) ) grid[k] = list = new List<int>();
			list.Add( v );
		}
		int Nearest( Vec3 q )
		{
			var (ix, iy, iz) = Key( q, lo, cell );
			for ( int r = 0; r < 1000; r++ )
			{
				int best = -1; float bestD = float.MaxValue;
				for ( int ax = ix - r; ax <= ix + r; ax++ )
					for ( int ay = iy - r; ay <= iy + r; ay++ )
						for ( int az = iz - r; az <= iz + r; az++ )
						{
							if ( Math.Max( Math.Abs( ax - ix ), Math.Max( Math.Abs( ay - iy ), Math.Abs( az - iz ) ) ) != r ) continue;
							if ( !grid.TryGetValue( (ax, ay, az), out var list ) ) continue;
							foreach ( int v in list )
							{
								float d = ( pos[v] - q ).LengthSquared;
								if ( d < bestD ) { bestD = d; best = v; }
							}
						}
				if ( best >= 0 ) return best;
			}
			throw new InvalidOperationException( "mirror search failed" );
		}

		var result = layer.Clone();
		int mirrored = 0;
		foreach ( var (vertex, weights) in layer.Painted )
		{
			if ( vertex < 0 || vertex >= pos.Count ) continue;
			var p = pos[vertex];
			if ( p.y <= 1e-6f ) continue; // source side is +Y; the centre keeps its own paint
			int u = Nearest( new Vec3( p.x, -p.y, p.z ) );
			var bw = new List<BoneWeight>( weights.Count );
			foreach ( var (bone, w) in weights )
			{
				string f = Skeleton.FlipSideSuffix( bone ) ?? bone;
				int bi = rig.IndexOf( f );
				if ( bi < 0 ) { f = bone; bi = rig.IndexOf( f ); }
				if ( bi < 0 ) continue;
				bw.Add( new BoneWeight( bi, w ) );
			}
			if ( bw.Count == 0 ) continue;
			result.Capture( u, bw.ToArray(), rig );
			mirrored++;
		}
		return (result, mirrored, pairs);
	}

	/// <summary>Copies the finished +Y side's weights onto the −Y side, one undo step.</summary>
	public static bool MirrorWeights( GeppettoDocument doc )
	{
		var studio = doc.Studio;
		if ( studio.Rig.Count == 0 ) return !Refuse( "Mirror Weights needs a skeleton." );
		if ( studio.WeightPaint is not { Count: > 0 } )
			return !Refuse( "Mirror Weights needs painted weights — run Auto Weights or paint first." );
		var (mesh, _) = studio.ToMeshWithBodies();
		WeightPaintLayer mirrored; int n, pairs;
		try { (mirrored, n, pairs) = MirrorLayer( studio.WeightPaint, mesh, studio.Rig ); }
		catch ( Exception e ) when ( e is ArgumentException or InvalidOperationException )
		{ return !Refuse( $"Mirror Weights: {e.Message}" ); }
		var before = studio.WeightPaint;
		bool ok = doc.Execute( new DelegateEditCommand( "Mirror weights",
			execute: () => studio.WeightPaint = mirrored,
			undo: () => studio.WeightPaint = before ), "Geppetto: mirror weights" );
		if ( !ok ) return false;
		GeppettoToolContext.Say( $"Mirrored {n} vertices across Y=0 (+Y to −Y) over {pairs} bone pairs." );
		return true;
	}

	/// <summary>What Fix Weights changed, for the report.</summary>
	public sealed class FixReport
	{
		public int Vertices;
		public int Changed;
		public float WorstDropped;
		public int Unresolvable;
	}

	/// <summary>One click of hygiene on a weight layer: prune to 4 influences, drop weights
	/// under 0.01, renormalise to a partition of unity, strongest first. Pure and UI-free.</summary>
	public static (WeightPaintLayer Layer, FixReport Report) FixLayer(
		WeightPaintLayer layer, Skeleton rig, PolyMesh mesh )
	{
		if ( layer is not { Count: > 0 } ) throw new ArgumentException( "no painted weights to fix" );
		var result = new WeightPaintLayer( mesh );
		var rep = new FixReport();
		foreach ( var (vertex, weights) in layer.Painted )
		{
			rep.Vertices++;
			var bw = new List<BoneWeight>();
			foreach ( var (bone, w) in weights )
			{
				int bi = rig.IndexOf( bone );
				if ( bi < 0 ) { rep.Unresolvable++; rep.WorstDropped = MathF.Max( rep.WorstDropped, w ); continue; }
				if ( w > 0f ) bw.Add( new BoneWeight( bi, w ) );
			}
			if ( bw.Count == 0 ) continue;
			var pruned = SkinWeights.Prune( bw.ToArray(), 4 );
			float droppedPrune = bw.Count > 4 ? bw.OrderByDescending( w => w.Weight ).Skip( 4 ).Max( w => w.Weight ) : 0f;
			rep.WorstDropped = MathF.Max( rep.WorstDropped, droppedPrune );
			var kept = pruned.Where( w => w.Weight >= 0.01f ).ToArray();
			if ( kept.Length == 0 ) kept = new[] { new BoneWeight( pruned[0].Bone, 1f ) };
			float sum = 0f;
			foreach ( var w in kept ) sum += w.Weight;
			var tidy = kept.Select( w => new BoneWeight( w.Bone, w.Weight / sum ) ).ToArray();
			rep.WorstDropped = MathF.Max( rep.WorstDropped,
				pruned.Where( w => w.Weight < 0.01f ).Select( w => w.Weight ).DefaultIfEmpty( 0f ).Max() );
			bool same = tidy.Length == weights.Count;
			if ( same )
				for ( int i = 0; i < tidy.Length && same; i++ )
				{
					int bi = rig.IndexOf( weights[i].Bone );
					same = bi == tidy[i].Bone && MathF.Abs( weights[i].Weight - tidy[i].Weight ) < 1e-4f;
				}
			if ( !same ) rep.Changed++;
			result.Capture( vertex, tidy, rig );
		}
		return (result, rep);
	}

	/// <summary>Prune to 4, drop the negligible, renormalise — the "make it actually work in the
	/// game" button. One undo step, with a report of what changed.</summary>
	public static bool FixWeights( GeppettoDocument doc )
	{
		var studio = doc.Studio;
		if ( studio.WeightPaint is not { Count: > 0 } )
			return !Refuse( "Fix Weights needs painted weights — run Auto Weights or paint first." );
		var (mesh, _) = studio.ToMeshWithBodies();
		WeightPaintLayer fixed_; FixReport rep;
		try { (fixed_, rep) = FixLayer( studio.WeightPaint, studio.Rig, mesh ); }
		catch ( Exception e ) when ( e is ArgumentException or InvalidOperationException )
		{ return !Refuse( $"Fix Weights: {e.Message}" ); }
		var before = studio.WeightPaint;
		bool ok = doc.Execute( new DelegateEditCommand( "Fix weights",
			execute: () => studio.WeightPaint = fixed_,
			undo: () => studio.WeightPaint = before ), "Geppetto: fix weights" );
		if ( !ok ) return false;
		GeppettoToolContext.Say( $"Fix Weights: {rep.Changed} of {rep.Vertices} vertices tidied"
			+ ( rep.WorstDropped > 0 ? $", worst dropped influence {rep.WorstDropped:0.###}" : ", nothing dropped" )
			+ ( rep.Unresolvable > 0 ? $", {rep.Unresolvable} influences named bones that no longer exist" : "" )
			+ "." );
		return true;
	}

	static readonly Dictionary<string, string> CopiedWeightBone = new();

	/// <summary>Remembers the picked bone's weights; Paste Weights stamps them onto another bone.</summary>
	public static bool CopyWeights( GeppettoDocument doc )
	{
		int sel = SelectedIndex( doc );
		if ( sel < 0 ) return !Refuse( "Copy Weights: pick a bone in the Rig panel first." );
		string name = doc.Studio.Rig.Bones[sel].Name;
		CopiedWeightBone[doc.DiskPath] = name;
		GeppettoToolContext.Say( $"Copied the weights of '{name}'. Pick another bone and press Paste Weights." );
		return true;
	}

	/// <summary>Stamps the copied bone's per-vertex weights onto the picked bone, renormalising.
	/// Paint once (a finger, a collar segment), propagate. One undo step.</summary>
	public static bool PasteWeights( GeppettoDocument doc )
	{
		var studio = doc.Studio;
		var rig = studio.Rig;
		int sel = SelectedIndex( doc );
		if ( sel < 0 ) return !Refuse( "Paste Weights: pick the bone to paste onto first." );
		if ( !CopiedWeightBone.TryGetValue( doc.DiskPath, out var from ) )
			return !Refuse( "Paste Weights: copy a bone's weights first." );
		string to = rig.Bones[sel].Name;
		if ( from == to ) return !Refuse( "Paste Weights: that's the bone the weights came from." );
		int fromI = rig.IndexOf( from ), toI = rig.IndexOf( to );
		if ( fromI < 0 ) return !Refuse( $"Paste Weights: '{from}' is no longer in the rig. Copy again." );
		var layer = studio.WeightPaint;
		if ( layer is not { Count: > 0 } )
			return !Refuse( "Paste Weights needs painted weights — run Auto Weights or paint first." );
		var (mesh, _) = studio.ToMeshWithBodies();
		var pasted = layer.Clone();
		int touched = 0;
		foreach ( var (vertex, weights) in layer.Painted )
		{
			float sw = 0f; bool has = false;
			foreach ( var (bone, w) in weights )
				if ( bone == from ) { sw = w; has = true; break; }
			if ( !has || sw <= 0f ) continue;
			var bw = new List<BoneWeight>();
			foreach ( var (bone, w) in weights )
			{
				int bi = rig.IndexOf( bone );
				if ( bi < 0 || bi == toI ) continue;
				bw.Add( new BoneWeight( bi, w ) );
			}
			bw.Add( new BoneWeight( toI, sw ) );
			float sum = 0f;
			foreach ( var w in bw ) sum += w.Weight;
			if ( sum <= 1e-9f ) continue;
			pasted.Capture( vertex, bw.Select( w => new BoneWeight( w.Bone, w.Weight / sum ) ).ToArray(), rig );
			touched++;
		}
		if ( touched == 0 ) return !Refuse( $"Paste Weights: '{from}' has no painted weight anywhere to copy." );
		var before = layer;
		bool ok = doc.Execute( new DelegateEditCommand( "Paste weights",
			execute: () => studio.WeightPaint = pasted,
			undo: () => studio.WeightPaint = before ), "Geppetto: paste weights" );
		if ( !ok ) return false;
		GeppettoToolContext.Say( $"Pasted '{from}' onto '{to}' on {touched} vertices." );
		return true;
	}

	// =========================================================================================
	// Chains, root/hips, export
	// =========================================================================================

	/// <summary>Deletes a bone and its whole subtree. Children do not re-parent — the chain goes.
	/// One undo step.</summary>
	public static bool DeleteChain( GeppettoDocument doc, int index )
	{
		var rig = doc.Studio.Rig;
		if ( index < 0 || index >= rig.Count ) return !Refuse( "Delete chain: that bone no longer exists." );
		var kill = new List<int> { index };
		for ( int i = 0; i < kill.Count; i++ )
			for ( int c = 0; c < rig.Count; c++ )
				if ( rig.Bones[c].Parent == kill[i] && !kill.Contains( c ) )
					kill.Add( c );
		var names = kill.Select( i => rig.Bones[i].Name ).ToList();
		bool ok = RigEdit( doc, $"delete chain {rig.Bones[index].Name} ({kill.Count} bones)", s =>
		{
			foreach ( int i in kill.OrderByDescending( x => x ) )
				s.Rig.RemoveBone( i );
			foreach ( var body in s.BodyBoneMap.Where( kv => names.Contains( kv.Value ) ).Select( kv => kv.Key ).ToList() )
				s.BodyBoneMap.Remove( body );
			if ( names.Contains( s.Rig.RootBone ) ) s.Rig.RootBone = "";
			if ( names.Contains( s.Rig.HipsBone ) ) s.Rig.HipsBone = "";
			return true;
		} );
		if ( ok ) { Select( doc, -1 ); GeppettoToolContext.Say( $"Deleted {names[0]} and its chain ({kill.Count} bones)." ); }
		return ok;
	}

	/// <summary>Places a bone and its mirror across Y=0 in one undo step: name_L under parent,
	/// name_R under mirrorParent. Returns both indices, or (-1, -1) when refused.</summary>
	public static (int Left, int Right) AddBoneMirror( GeppettoDocument doc, int parent, Vec3 head, Vec3 tail, int mirrorParent )
	{
		var rig = doc.Studio.Rig;
		if ( ( tail - head ).Length < 0.01f ) { Refuse( "Bone too short — click further from the last joint." ); return (-1, -1); }
		string b = rig.UniqueName( NextBoneName( rig ) );
		Vec3 mhead = new Vec3( head.x, -head.y, head.z );
		Vec3 mtail = new Vec3( tail.x, -tail.y, tail.z );
		int left = -1, right = -1;
		bool ok = RigEdit( doc, $"add bone {b}_L + {b}_R", s =>
		{
			left = s.Rig.AddBoneFromPoints( b + "_L", parent, head, tail );
			right = s.Rig.AddBoneFromPoints( b + "_R", mirrorParent, mhead, mtail );
			return true;
		} );
		if ( !ok ) return (-1, -1);
		Select( doc, left );
		return (left, right);
	}

	/// <summary>Names a bone the character root (index &lt; 0 clears it). The exporter and a future
	/// retargeter agree on this name. One undo step.</summary>
	public static bool SetRootBone( GeppettoDocument doc, int index )
	{
		var rig = doc.Studio.Rig;
		string name = index >= 0 && index < rig.Count ? rig.Bones[index].Name : "";
		if ( rig.RootBone == name ) return false;
		bool ok = RigEdit( doc, name == "" ? "clear root bone" : $"root bone {name}",
			s => { s.Rig.RootBone = name; return true; } );
		if ( ok ) GeppettoToolContext.Say( name == "" ? "Root bone cleared." : $"'{name}' is the root bone now." );
		return ok;
	}

	/// <summary>Names a bone the hips bone (index &lt; 0 clears it). One undo step.</summary>
	public static bool SetHipsBone( GeppettoDocument doc, int index )
	{
		var rig = doc.Studio.Rig;
		string name = index >= 0 && index < rig.Count ? rig.Bones[index].Name : "";
		if ( rig.HipsBone == name ) return false;
		bool ok = RigEdit( doc, name == "" ? "clear hips bone" : $"hips bone {name}",
			s => { s.Rig.HipsBone = name; return true; } );
		if ( ok ) GeppettoToolContext.Say( name == "" ? "Hips bone cleared." : $"'{name}' is the hips bone now." );
		return ok;
	}

	/// <summary>Ships the rigged character as .glb — Godot-native (Skeleton3D + Skin + takes),
	/// with the real painted weights. Runs Check model first and refuses when a problem would
	/// break the export, so a rig you can't ship never silently ships.</summary>
	public static bool ExportPlayermodel( GeppettoDocument doc, string? path = null )
	{
		var studio = doc.Studio;
		if ( studio.Rig.Count == 0 ) return !Refuse( "Export Playermodel: there is no rig yet." );
		List<Playermodel.Finding> findings;
		try { findings = Playermodel.Check( Playermodel.Build( studio ), DmxWriter.MaxInfluences ); }
		catch ( Exception e ) { return !Refuse( $"Export Playermodel: the playermodel could not even be built — {e.Message}" ); }
		var problems = findings.Where( f => f.Severity == Playermodel.Severity.Problem ).ToList();
		if ( problems.Count > 0 )
			return !Refuse( $"Export Playermodel refused: {problems.Count} problem(s) — worst: {problems[0].Problem}. {problems[0].Remedy}" );
		path ??= ( doc.ResPath.EndsWith( ".effigy" ) ? doc.ResPath[..^".effigy".Length] : doc.ResPath ) + ".glb";
		Error err;
		try { err = AnimateExport.ExportGlb( doc, path ); }
		catch ( Exception e ) { return !Refuse( $"Export Playermodel failed: {e.Message}" ); }
		if ( err != Error.Ok ) return !Refuse( $"Export Playermodel failed: {err}" );
		int warnings = findings.Count( f => f.Severity == Playermodel.Severity.Warning );
		if ( path.StartsWith( "res://" ) ) EditorInterface.Singleton.GetResourceFilesystem().Scan();
		GeppettoToolContext.Say( $"Exported playermodel to {path}"
			+ ( warnings > 0 ? $" with {warnings} warning(s) — see the Output panel" : "" )
			+ $". Root: '{( string.IsNullOrEmpty( studio.Rig.RootBone ) ? "(first root)" : studio.Rig.RootBone )}'"
			+ ( string.IsNullOrEmpty( studio.Rig.HipsBone ) ? "" : $", hips: '{studio.Rig.HipsBone}'" ) + "." );
		return true;
	}

	// =========================================================================================
	// Soft bones
	// =========================================================================================

	public static bool SetSoft( GeppettoDocument doc, int index, bool soft )
	{
		var rig = doc.Studio.Rig;
		if ( index < 0 || index >= rig.Count ) return false;
		string name = rig.Bones[index].Name;
		bool ok = RigEdit( doc, soft ? $"make {name} soft" : $"make {name} rigid", s =>
		{
			s.Rig.Bones[index].Soft = soft ? s.Rig.Bones[index].Soft ?? new SoftBone() : null;
			return true;
		} );
		if ( ok ) GeppettoToolContext.Say( soft
			? $"'{name}' is soft. Stiffness, damping, weight and cone are in the Rig panel; Preview shows the swing."
			: $"'{name}' follows the pose rigidly again." );
		return ok;
	}

	/// <summary>Sets one soft value: 0 Stiffness, 1 Damping, 2 Weight, 3 Cone (degrees).</summary>
	public static bool SetSoftValue( GeppettoDocument doc, int index, int which, float value )
	{
		var rig = doc.Studio.Rig;
		if ( index < 0 || index >= rig.Count || rig.Bones[index].Soft == null ) return false;
		string[] names = { "stiffness", "damping", "weight", "cone" };
		return RigEdit( doc, $"{rig.Bones[index].Name} {names[which]}", s =>
		{
			var soft = s.Rig.Bones[index].Soft!;
			switch ( which )
			{
				case 0: soft.Stiffness = MathF.Max( 0, value ); break;
				case 1: soft.Damping = Math.Clamp( value, 0, 1 ); break;
				case 2: soft.Weight = value; break;
				default: soft.MaxAngle = Math.Clamp( value, 0, 180 ); break;
			}
			return true;
		} );
	}

	// =========================================================================================
	// Check model
	// =========================================================================================

	/// <summary>s&amp;box CheckPlayermodel: builds the real playermodel and reports what would stop it
	/// working, worst first, in the Output panel.</summary>
	public static List<Playermodel.Finding>? CheckModel( GeppettoDocument doc )
	{
		var studio = doc.Studio;
		if ( studio.Rig.Count == 0 ) { Refuse( "Check model: there is no rig yet. Make bones from the bodies first." ); return null; }

		List<Playermodel.Finding> findings;
		try { findings = Playermodel.Check( Playermodel.Build( studio ), DmxWriter.MaxInfluences ); }
		catch ( Exception e ) { Refuse( $"Check model: the playermodel could not even be built — {e.Message}" ); return null; }

		if ( findings.Count == 0 )
		{
			GeppettoToast.Info( "Check model: nothing to report. This is ready to be a playermodel." );
			return findings;
		}

		foreach ( var f in findings )
		{
			var line = $"[pm] {f.Severity}: {f.Problem}. {f.Remedy}";
			if ( f.Severity == Playermodel.Severity.Note ) GD.Print( line ); else GD.PushWarning( line );
		}

		int problems = findings.Count( f => f.Severity == Playermodel.Severity.Problem );
		int warnings = findings.Count( f => f.Severity == Playermodel.Severity.Warning );
		GeppettoToast.Info( problems > 0
			? $"Check model: {problems} problem(s) would stop this working as a playermodel — see the Output panel. Worst: {findings[0].Problem}."
			: warnings > 0
				? $"Check model: it will work, but {warnings} thing(s) will look wrong — see the Output panel. First: {findings[0].Problem}."
				: $"Check model: {findings.Count} note(s) in the Output panel, nothing wrong." );
		return findings;
	}
}
#endif
