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

		public Snapshot( PartStudio s )
		{
			Bones = s.Rig.Bones.Select( b => b.Clone() ).ToList();
			BodyBoneMap = new Dictionary<string, string>( s.BodyBoneMap );
			Correctives = s.Correctives.Select( c => (c, c.Bone) ).ToList();
		}

		public void Restore( PartStudio s )
		{
			s.Rig.Bones.Clear();
			s.Rig.Bones.AddRange( Bones.Select( b => b.Clone() ) );
			s.BodyBoneMap.Clear();
			foreach ( var kv in BodyBoneMap ) s.BodyBoneMap[kv.Key] = kv.Value;
			foreach ( var (shape, bone) in Correctives ) shape.Bone = bone;
		}

		public bool Same( Snapshot o )
		{
			if ( Bones.Count != o.Bones.Count || BodyBoneMap.Count != o.BodyBoneMap.Count ) return false;
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
