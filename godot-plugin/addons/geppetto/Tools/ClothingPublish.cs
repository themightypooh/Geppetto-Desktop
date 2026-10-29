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
/// Make clothing / Make live cloth, ported from s&box's PublishClothing and PublishLiveCloth.
///
/// Make clothing bakes the garments alone (no wearer) into a skinned .glb on the document's rig, plus the
/// .clothing definition s&box wrote (category, slots, layer). Make live cloth writes the kernel's
/// live-cloth file (cloth mesh, collision capsules on the wearer's bones, pins). Both go to
/// <c>models/effigy/</c> beside the .effigy, as in s&box.
/// </summary>
public static class ClothingPublish
{
	public const string Folder = "models/effigy";

	public static string BaseName( GeppettoDocument doc ) => Sanitise( Path.GetFileNameWithoutExtension( doc.DiskPath ) ) is { Length: > 0 } s ? s : "effigy";

	static string Sanitise( string name ) =>
		new string( ( name ?? "" ).Select( c => char.IsAsciiLetterOrDigit( c ) || c == '_' || c == '-' ? char.ToLowerInvariant( c ) : '_' ).ToArray() ).Trim( '_' );

	/// <summary>Writes the garment .glb and .clothing. Returns (message, glb path or null on refusal).</summary>
	public static (string Message, string? Glb, string? Clothing) MakeClothing( GeppettoDocument doc )
	{
		var studio = doc.Studio;
		var garments = studio.Features.OfType<GarmentFeature>().Where( g => !g.Suppressed && g.Error == null ).ToList();
		if ( garments.Count == 0 ) return ( "Nothing to publish - add a Garment first.", null, null );
		if ( studio.Rig == null || studio.Rig.Count == 0 ) return ( "Clothing needs a rig to move with its wearer - load a Wearer first.", null, null );

		string docName = BaseName( doc );
		string name = garments.Count == 1 ? Sanitise( garments[0].Name ?? garments[0].BuildRecipe().Name ) : "outfit";
		if ( name.Length == 0 || name == docName ) name = docName + "_clothing";

		string root = Path.GetDirectoryName( doc.DiskPath ) ?? ".";
		string modelRel = $"{Folder}/{name}.glb";
		string glb = Path.Combine( root, modelRel );
		string clothing = Path.Combine( root, Folder, name + ".clothing" );
		Directory.CreateDirectory( Path.GetDirectoryName( glb )! );

		// Only the garment bodies: the wearer and everything else are hidden for this one mesh build.
		var hidden = studio.Bodies.Where( b => b.Visible && !b.IsGarment ).ToList();
		CpuMesh cpu;
		foreach ( var b in hidden ) b.Visible = false;
		try { cpu = CpuMesh.FromStudio( studio, 0, doc.DiskPath ); }
		finally { foreach ( var b in hidden ) b.Visible = true; }

		var node = new Node3D { Name = name };
		try
		{
			var skeleton = EffigySkeletonBridge.ToGodotSkeleton( studio.Rig );
			node.AddChild( skeleton );
			skeleton.Owner = node;
			var mi = new MeshInstance3D
			{
				Name = "Clothing",
				Mesh = EffigyMeshBridge.ToGodotArrayMesh( cpu, doc.Resolver, studio.MaterialLooks, root ),
				Skin = EffigySkeletonBridge.CreateSkin( skeleton ),
				Skeleton = new NodePath( ".." ),
			};
			skeleton.AddChild( mi );
			mi.Owner = node;
			var err = EffigyGlbExporter.ExportScene( node, glb );
			if ( err != Error.Ok ) return ( $"Could not write {glb}: {err}.", null, null );
		}
		finally { node.Free(); }

		var layered = garments.Select( g => ( g.BuildRecipe(), g.Layer.Index ) ).ToList();
		File.WriteAllText( clothing, ClothingDefinition.Write( layered, modelRel, garments.Count == 1 ? garments[0].Name ?? name : name ) );

		var recipe = ClothingDefinition.Combine( layered.Select( l => l.Item1 ) );
		string category = recipe == null ? "" : ClothingDefinition.CategoryFor( recipe );
		string slots = recipe == null ? "" : string.Join( ", ", ClothingDefinition.SlotsFor( recipe ) );
		Rescan( glb );
		return ( $"Wrote {modelRel} and {Folder}/{name}.clothing ({category}; slots {slots}). Poohdot has no Dresser yet, so nothing puts it on for you: instance the .glb and parent it to the wearer's skeleton.", glb, clothing );
	}

	/// <summary>Writes the live-cloth file. Returns (message, path or null on refusal).</summary>
	public static (string Message, string? Path) MakeLiveCloth( GeppettoDocument doc )
	{
		var studio = doc.Studio;
		var garment = studio.Bodies.FirstOrDefault( b => b.IsGarment && !b.IsGarmentDetail );
		if ( garment == null ) return ( "Nothing to make live - add a Garment first.", null );
		if ( studio.Rig == null || studio.Rig.Count == 0 ) return ( "Live cloth hangs on the wearer's bones - load a Wearer first.", null );

		string material = garment.Mesh.Faces.Count > 0 && studio.MaterialNames.TryGetValue( garment.Mesh.Faces[0].Material, out var m ) ? m : "";
		string root = Path.GetDirectoryName( doc.DiskPath ) ?? ".";
		string rel = $"{Folder}/{BaseName( doc )}.cloth.json";
		string path = Path.Combine( root, rel );
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		if ( ClothExport.ExportLiveClothToFile( studio, material, path ) == null )
			return ( "No bones to hang the cloth on - the wearer's skeleton has no torso or arms Effigy recognises.", null );
		Rescan( path );
		return ( $"Wrote {rel}. Poohdot has no live-cloth component yet to play it; the file is the same one s&box's Garment Cloth reads.", path );
	}

	static void Rescan( string path )
	{
		if ( !Engine.IsEditorHint() || EditorInterface.Singleton == null ) return;
		string res = ProjectSettings.LocalizePath( path );
		if ( res.StartsWith( "res://" ) ) EditorInterface.Singleton.GetResourceFilesystem().Scan();
	}

	public static bool MakeClothingTool( GeppettoToolContext ctx )
	{
		var doc = ctx.Doc;
		if ( doc == null ) return ctx.Need();
		var (message, glb, _) = MakeClothing( doc );
		if ( glb == null ) GeppettoToolContext.Warn( $"{message}" ); else GeppettoToast.Info( message );
		return true;
	}

	public static bool MakeLiveClothTool( GeppettoToolContext ctx )
	{
		var doc = ctx.Doc;
		if ( doc == null ) return ctx.Need();
		var (message, path) = MakeLiveCloth( doc );
		if ( path == null ) GeppettoToolContext.Warn( $"{message}" ); else GeppettoToast.Info( message );
		return true;
	}
}
#endif
