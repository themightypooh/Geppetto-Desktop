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
/// The Animate workspace's exports, from the LIVE document: the model as it is in memory and every take
/// as it is in memory (unsaved keys included), not the files on disk. The format-agnostic exports of
/// Marionette / Effigy: GLB (mesh, materials, skeleton, skin, every take as an animation), OBJ (the mesh,
/// posed at the playhead or at bind) and FBX (binary, mesh + skeleton + skin, for Blender).
///
/// Not ported, because they only mean something to s&amp;box / Source 2:
/// <list type="bullet">
/// <item>.vmdl compile (RigAnimExport / EffigyAnimExport): needs ModelDoc and resourcecompiler.</item>
/// <item>DMX model / DMX animation: ModelDoc's input format; nothing outside Source 2 reads it.</item>
/// <item>SMD: Source 1 / ModelDoc input, same story.</item>
/// <item>Frame-anim and sprite-sheet bakes: render through s&amp;box's scene renderer into s&amp;box assets.</item>
/// </list>
/// </summary>
public static class AnimateExport
{
	public const string SboxOnlyNote =
		"Not available here (s&box only): .vmdl compile (needs ModelDoc / resourcecompiler), DMX and SMD " +
		"(Source 2 ModelDoc input), frame-anim and sprite-sheet bakes (rendered by s&box).";

	/// <summary>Every take of the model by animation name: the .riganim files next to it, with each one
	/// the document has open replaced by its in-memory clip (unsaved keys included).</summary>
	public static Dictionary<string, RigAnimFile> LiveClips( GeppettoDocument doc )
	{
		var result = new Dictionary<string, RigAnimFile>( StringComparer.Ordinal );
		string dir = Path.GetDirectoryName( doc.DiskPath ) ?? ".", baseName = Path.GetFileNameWithoutExtension( doc.DiskPath );
		var open = doc.Bridge.Clips.Where( s => s.Clip != null ).ToDictionary( s => Path.GetFullPath( s.SourcePath ), s => s.Clip!, StringComparer.Ordinal );
		var files = Directory.Exists( dir ) ? Directory.GetFiles( dir, baseName + "*.riganim" ).Select( Path.GetFullPath ) : Enumerable.Empty<string>();
		foreach ( var f in files.Concat( open.Keys ).Distinct().OrderBy( f => f, StringComparer.Ordinal ) )
		{
			string name = Path.GetFileNameWithoutExtension( f );
			if ( name.StartsWith( baseName + "_", StringComparison.OrdinalIgnoreCase ) ) name = name[( baseName.Length + 1 )..];
			try { result[name] = open.TryGetValue( f, out var live ) ? live : RigAnimFile.ReadFile( f ); }
			catch ( Exception e ) { GeppettoToolContext.Warn( $"Skipping take {f}: {e.Message}" ); }
		}
		return result;
	}

	/// <summary>The importer's scene (root → Skeleton3D → Body, AnimationPlayer) built from the live
	/// document and its live takes. The caller frees it.</summary>
	public static Node3D BuildLiveScene( GeppettoDocument doc )
	{
		var studio = doc.Studio;
		var cpu = CpuMesh.FromStudio( studio, 0, doc.DiskPath );
		var root = new Node3D { Name = Path.GetFileNameWithoutExtension( doc.DiskPath ) };
		root.SetMeta( EffigySceneImporter.EffigyMeta, doc.ResPath );

		Skeleton3D? skeleton = null;
		if ( studio.Rig is { Count: > 0 } )
		{
			skeleton = EffigySkeletonBridge.ToGodotSkeleton( studio.Rig );
			root.AddChild( skeleton );
			skeleton.Owner = root;
		}

		var body = new MeshInstance3D
		{
			Name = "Body",
			Mesh = EffigyMeshBridge.ToGodotArrayMesh( cpu, doc.Resolver, studio.MaterialLooks, Path.GetDirectoryName( doc.DiskPath ) ),
		};
		if ( skeleton != null )
		{
			body.Skin = EffigySkeletonBridge.CreateSkin( skeleton );
			body.Skeleton = new NodePath( ".." );
			skeleton.AddChild( body );
		}
		else root.AddChild( body );
		body.Owner = root;

		if ( skeleton != null )
		{
			var lib = new AnimationLibrary();
			foreach ( var (name, clip) in LiveClips( doc ) )
				lib.AddAnimation( name, EffigyClipBridge.ToGodotAnimation( clip, studio.Rig, skeleton.Name, $"{skeleton.Name}/{body.Name}" ) );
			if ( lib.GetAnimationList().Count > 0 )
			{
				var player = new AnimationPlayer { Name = "AnimationPlayer" };
				root.AddChild( player );
				player.Owner = root;
				player.AddAnimationLibrary( "", lib );
			}
		}
		return root;
	}

	/// <summary>GLB of the live model and every live take.</summary>
	public static Error ExportGlb( GeppettoDocument doc, string glbPath )
	{
		var root = BuildLiveScene( doc );
		try { return EffigyGlbExporter.ExportScene( root, glbPath ); }
		finally { root.Free(); }
	}

	/// <summary>OBJ of the mesh, posed by <paramref name="take"/> at <paramref name="frame"/> (bind pose when
	/// <paramref name="take"/> is null). Effigy's ExportService: inches, Z-up, like the s&amp;box export.</summary>
	public static string? ExportObj( GeppettoDocument doc, DocumentSession? take, int frame, string objPath )
	{
		string dest = Path.GetFullPath( ProjectSettings.GlobalizePath( objPath ) );
		var studio = doc.Studio;
		if ( take?.Clip is not { } clip || studio.Rig is not { Count: > 0 } rig )
		{
			var result = new ExportService().Export( new ExportRequest( doc.Session, ExportFormat.Obj, dest, PoseMode.BindPose, overwritePolicy: OverwritePolicy.Overwrite ) );
			return result.Success ? null : result.ErrorMessage ?? "export failed";
		}
		// Posed: skin the merged mesh the way the viewport does (SkinBinder.BindBodies, CpuMesh.FromStudio)
		// and deform it to the take's pose. ExportService's per-body path only poses bodies that carry their
		// own skin or a body→bone entry, so it would write bind pose for a model skinned by proximity.
		try
		{
			var (merged, ranges) = studio.ToMeshWithBodies( b => b.Visible );
			var skin = SkinBinder.BindBodies( merged, ranges, studio.BodyBoneMap, rig );
			var bindWorld = RigAnimFile.ModelSpace( rig, rig.Bones.Select( b => b.Local ).ToArray() );
			var player = new MarionettePlayer( rig, startPlaying: false );
			player.LoadClip( clip );
			player.Time = frame / (float)( clip.FrameRate > 0 ? clip.FrameRate : 30 );
			player.Step( 0 );
			var posed = new PolyMesh { Positions = new List<Vec3>( SkinBinder.Deform( merged.Positions, skin, bindWorld, player.WorldPose.ToArray() ) ) };
			foreach ( var f in merged.Faces ) posed.AddFace( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material );
			Directory.CreateDirectory( Path.GetDirectoryName( dest )! );
			ObjWriter.WriteFile( posed, dest, Path.GetFileNameWithoutExtension( doc.DiskPath ) );
			return null;
		}
		catch ( Exception e ) { return e.Message; }
	}

	/// <summary>Binary FBX (mesh, skeleton, skin weights) at real size, for Blender. The kernel's FBX
	/// writer carries no animation stack, so takes go out as GLB.</summary>
	public static string? ExportFbx( GeppettoDocument doc, string fbxPath )
	{
		try
		{
			string dest = Path.GetFullPath( ProjectSettings.GlobalizePath( fbxPath ) );
			Directory.CreateDirectory( Path.GetDirectoryName( dest )! );
			var rig = doc.Studio.Rig is { Count: > 0 } r ? r : null;
			FbxWriter.WriteBinaryFile( doc.Studio.ToMeshWithBodies().Mesh, dest, rig, modelName: Path.GetFileNameWithoutExtension( doc.DiskPath ) );
			return null;
		}
		catch ( Exception e ) { return e.Message; }
	}

	// =========================================================================================
	// The dial's Export Animation… box
	// =========================================================================================

	static readonly string[] Formats = { "GLB (model + every take)", "OBJ (mesh posed at the playhead)", "FBX (mesh + skeleton, binary)" };
	static readonly string[] Exts = { ".glb", ".obj", ".fbx" };

	/// <summary>Export Animation…: pick a format and a file, then write it from the live document.</summary>
	public static bool ShowDialog( GeppettoToolContext ctx, int frame )
	{
		var c = ctx.Character;
		if ( c?.Doc == null ) return ctx.Need();
		var doc = c.Doc;
		string stem = doc.ResPath.EndsWith( ".effigy" ) ? doc.ResPath[..^".effigy".Length] : doc.ResPath;

		var box = AnimateUi.Box( "Export Animation", "Export" );
		var grid = AnimateUi.Grid( box );
		var format = new OptionButton { FitToLongestItem = true };
		foreach ( var f in Formats ) format.AddItem( f );
		AnimateUi.Row( grid, "Format", format );
		var path = new LineEdit { Text = stem + ".glb", CustomMinimumSize = new Vector2( 320, 0 ), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		AnimateUi.Row( grid, "File", path );
		format.ItemSelected += i => path.Text = Path.ChangeExtension( path.Text, Exts[i] );
		AnimateUi.Note( box, AnimateExport.SboxOnlyNote );

		box.Confirmed += () =>
		{
			int i = format.Selected;
			string? err = i switch
			{
				0 => ExportGlb( doc, path.Text ) is var e && e == Error.Ok ? null : e.ToString(),
				1 => ExportObj( doc, c.EnsureClip() ? c.ClipSession : null, frame, path.Text ),
				_ => ExportFbx( doc, path.Text ),
			};
			if ( err == null )
			{
				GeppettoToast.Info( $"Exported {path.Text}" );
				if ( path.Text.StartsWith( "res://" ) ) EditorInterface.Singleton.GetResourceFilesystem().Scan();
			}
			else GeppettoToolContext.Warn( $"Export {path.Text} failed: {err}" );
		};
		AnimateUi.Show( box );
		return true;
	}
}
#endif
