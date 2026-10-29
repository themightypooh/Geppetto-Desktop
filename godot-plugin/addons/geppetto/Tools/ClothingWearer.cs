#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Clothing > Wearer, ported from s&box's LoadWearer: pick a model in the project (a .glb / .gltf / .fbx /
/// .obj / scene, or another Geppetto .effigy), and its surface becomes a Wearer body to cut garments from
/// and collide with, its skeleton the document's rig if the document has none yet.
///
/// s&box read a compiled .vmdl through the engine; here the model is loaded through Poohdot's own
/// importers and converted back to Effigy space (inches, Z up). The mesh is kept in the kernel's import
/// side-car (<c>{doc}.import/{id}.obj</c>), as the Wearer feature expects.
/// </summary>
public static class ClothingWearer
{
	public static bool Run( GeppettoToolContext ctx )
	{
		var doc = ctx.Doc;
		if ( doc == null ) return ctx.Need();

		var dialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenFile,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Load a wearer (a rigged body to dress)",
		};
		foreach ( var f in new[] { "*.glb, *.gltf ; glTF", "*.fbx ; FBX", "*.obj ; OBJ", "*.tscn, *.scn ; Scene", "*.effigy ; Geppetto model" } )
			dialog.AddFilter( f );
		dialog.FileSelected += path =>
		{
			var message = LoadFromResource( doc, path );
			if ( message.StartsWith( "!" ) ) GeppettoToolContext.Warn( $"{message[1..]}" );
			else GeppettoToolContext.Say( message );
			dialog.QueueFree();
		};
		dialog.Canceled += () => dialog.QueueFree();
		EditorInterface.Singleton.GetBaseControl().AddChild( dialog );
		dialog.PopupFileDialog();
		return true;
	}

	/// <summary>Loads a project resource as the wearer. Returns the message; a leading "!" marks a refusal.</summary>
	public static string LoadFromResource( GeppettoDocument doc, string path )
	{
		string name = Path.GetFileNameWithoutExtension( path );
		Node? root = null;
		try
		{
			var res = ResourceLoader.Load( path );
			switch ( res )
			{
				case PackedScene scene: root = scene.Instantiate(); break;
				case Mesh mesh: root = new MeshInstance3D { Mesh = mesh }; break;
				default: return $"!{name} is not a model Poohdot can load (expected a scene or a mesh).";
			}
			var (poly, rig) = FromGodot( root );
			return LoadWearer( doc, poly, name, path, rig );
		}
		finally
		{
			root?.Free();
		}
	}

	/// <summary>The welded triangle surface of every MeshInstance3D under <paramref name="root"/> and the first
	/// Skeleton3D's rest pose, both in Effigy space.</summary>
	public static (PolyMesh Mesh, Skeleton? Rig) FromGodot( Node root )
	{
		var mesh = new PolyMesh();
		var weld = new Dictionary<(long, long, long), int>();
		Skeleton3D? skeleton = null;

		foreach ( var node in Walk( root ) )
		{
			if ( node is Skeleton3D s && skeleton == null ) skeleton = s;
			if ( node is not MeshInstance3D { Mesh: { } m } mi ) continue;
			var xf = RelativeTransform( mi, root );
			for ( int surface = 0; surface < m.GetSurfaceCount(); surface++ )
			{
				if ( m is ArrayMesh am && am.SurfaceGetPrimitiveType( surface ) != Mesh.PrimitiveType.Triangles ) continue;
				var arrays = m.SurfaceGetArrays( surface );
				var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
				var idx = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
				var remap = new int[verts.Length];
				for ( int i = 0; i < verts.Length; i++ )
				{
					var e = ToEffigy( xf * verts[i] );
					var key = ((long)MathF.Round( e.x * 1000f ), (long)MathF.Round( e.y * 1000f ), (long)MathF.Round( e.z * 1000f ));
					if ( !weld.TryGetValue( key, out var at ) )
					{
						at = mesh.Positions.Count;
						weld[key] = at;
						mesh.Positions.Add( e );
					}
					remap[i] = at;
				}
				int count = idx?.Length ?? verts.Length;
				for ( int t = 0; t + 2 < count; t += 3 )
				{
					int ia = idx?[t] ?? t, ib = idx?[t + 1] ?? t + 1, ic = idx?[t + 2] ?? t + 2;
					if ( (uint)ia >= remap.Length || (uint)ib >= remap.Length || (uint)ic >= remap.Length ) continue;
					int a = remap[ia], b = remap[ib], c = remap[ic];
					if ( a == b || b == c || a == c ) continue;
					// Godot's front faces wind the other way (the mesh bridge writes A, C, B).
					mesh.Faces.Add( new Face( new[] { a, c, b } ) );
				}
			}
		}

		return ( mesh, skeleton == null || skeleton.GetBoneCount() == 0 ? null : ToRig( skeleton, RelativeTransform( skeleton, root ) ) );
	}

	/// <summary>The skeleton's rest pose as a kernel rig. Root bones take the skeleton node's own placement,
	/// so the rig lines up with the surface. Bones are re-ordered so every parent comes before its children.</summary>
	public static Skeleton ToRig( Skeleton3D s, Transform3D skeletonXf )
	{
		int n = s.GetBoneCount();
		var order = new List<int>();
		var placed = new bool[n];
		void Place( int i )
		{
			if ( placed[i] ) return;
			int p = s.GetBoneParent( i );
			if ( p >= 0 ) Place( p );
			placed[i] = true;
			order.Add( i );
		}
		for ( int i = 0; i < n; i++ ) Place( i );
		var newIndex = new int[n];
		for ( int k = 0; k < order.Count; k++ ) newIndex[order[k]] = k;

		var rig = new Skeleton();
		foreach ( int i in order )
		{
			int p = s.GetBoneParent( i );
			var rest = s.GetBoneRest( i );
			if ( p < 0 ) rest = skeletonXf * rest;
			var local = ToXform( rest );

			// Length: to the first child, as the kernel measures a bone; a leaf gets a short stub.
			float length = 0f;
			foreach ( int c in s.GetBoneChildren( i ) )
			{
				var o = ToEffigy( s.GetBoneRest( c ).Origin );
				length = MathF.Sqrt( o.x * o.x + o.y * o.y + o.z * o.z );
				if ( length > 1e-3f ) break;
			}
			if ( length <= 1e-3f ) length = p >= 0 ? MathF.Max( 0.5f, rig.Bones[newIndex[p]].Length * 0.5f ) : 2f;
			rig.Bones.Add( new Bone( s.GetBoneName( i ), p < 0 ? -1 : newIndex[p], local, length ) );
		}
		return rig;
	}

	/// <summary>Inverse of EffigySkeletonBridge.TransformXform.</summary>
	public static Xform ToXform( Transform3D t )
	{
		var b = t.Basis.Orthonormalized();
		Vector3 c0 = b.Column0, c1 = b.Column1, c2 = b.Column2;
		var y = new Vec3( c0.Z, c0.X, c0.Y );
		var z = new Vec3( c1.Z, c1.X, c1.Y );
		var x = new Vec3( c2.Z, c2.X, c2.Y );
		return new Xform( x, y, z, ToEffigy( t.Origin ) );
	}

	/// <summary>Inverse of EffigyMeshBridge.TransformPosition: Godot metres (Y up) to Effigy inches (Z up).</summary>
	public static Vec3 ToEffigy( Vector3 g ) => new Vec3( g.Z / 0.0254f, g.X / 0.0254f, g.Y / 0.0254f );

	static IEnumerable<Node> Walk( Node n )
	{
		yield return n;
		foreach ( var c in n.GetChildren() )
			foreach ( var d in Walk( c ) ) yield return d;
	}

	static Transform3D RelativeTransform( Node node, Node root )
	{
		var xf = Transform3D.Identity;
		for ( var n = node; n != null && n != root; n = n.GetParent() )
			if ( n is Node3D n3 ) xf = n3.Transform * xf;
		return xf;
	}

	/// <summary>
	/// Puts <paramref name="mesh"/> on the document as the wearer, as one undo step: a new Wearer feature,
	/// or the existing one re-pointed at this model (trying the same jacket on somebody else). The rig is
	/// installed only when the document has none, as in s&box. Returns the message; "!" marks a refusal.
	/// </summary>
	public static string LoadWearer( GeppettoDocument doc, PolyMesh mesh, string name, string source, Skeleton? rig )
	{
		if ( mesh.Faces.Count == 0 )
			return $"!{name} has no render mesh to dress - a garment needs a surface to be cut from.";

		var studio = doc.Studio;
		var bytes = Encoding.UTF8.GetBytes( ObjWriter.Write( mesh, name ) );
		bool installRig = rig != null && rig.Count > 0 && ( studio.Rig == null || studio.Rig.Count == 0 );
		var oldRig = studio.Rig;
		var newRig = installRig ? rig! : oldRig;
		var existing = studio.Features.OfType<WearerFeature>().LastOrDefault( w => !w.Suppressed );

		if ( existing == null )
		{
			var wearer = new WearerFeature { Name = name };
			wearer.Model.Value = source;
			wearer.LoadMesh( bytes );
			doc.Execute( new DelegateEditCommand( $"Load wearer '{name}'",
				execute: () =>
				{
					studio.Rig = newRig;
					studio.Add( wearer );
					WriteSidecar( doc, wearer.Id, bytes );
					studio.MarkAllDirty();
				},
				undo: () =>
				{
					studio.Remove( wearer );
					studio.Rig = oldRig;
					studio.MarkAllDirty();
				} ) );
		}
		else
		{
			var oldBytes = existing.SaveMesh();
			string oldModel = existing.Model.Value, oldName = existing.Name ?? "";
			doc.Execute( new DelegateEditCommand( $"Load wearer '{name}'",
				execute: () =>
				{
					studio.Rig = newRig;
					existing.Name = name;
					existing.Model.Value = source;
					existing.LoadMesh( bytes );
					WriteSidecar( doc, existing.Id, bytes );
					studio.MarkAllDirty();
				},
				undo: () =>
				{
					studio.Rig = oldRig;
					existing.Name = oldName;
					existing.Model.Value = oldModel;
					if ( oldBytes != null ) { existing.LoadMesh( oldBytes ); WriteSidecar( doc, existing.Id, oldBytes ); }
					studio.MarkAllDirty();
				} ) );
		}

		if ( installRig )
			return $"{name} is in, {rig!.Count} bones. Add a Garment to dress it.";
		if ( studio.Rig != null && studio.Rig.Count > 0 )
			return $"{name} is in. The rig already here was kept ({studio.Rig.Count} bones) - delete those bones first if you want this model's skeleton.";
		return $"!{name} is in, but it has no skeleton - a garment needs bones to find the torso and arms.";
	}

	/// <summary>The wearer's mesh lives in the kernel's import side-car beside the .effigy, written now so it
	/// is there whenever the document is saved (the side-car is what a reload reads back).</summary>
	static void WriteSidecar( GeppettoDocument doc, string id, byte[] bytes )
	{
		try
		{
			var path = ImportSidecar.PathFor( doc.DiskPath, id, bytes );
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			File.WriteAllBytes( path, bytes );
		}
		catch ( Exception e )
		{
			GD.PushError( $"[Geppetto] Could not write the wearer's mesh beside the document: {e.Message}" );
		}
	}
}
#endif
