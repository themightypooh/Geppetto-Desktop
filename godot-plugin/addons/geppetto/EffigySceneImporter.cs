#if TOOLS
using System;
using System.IO;
using Godot;
using Marionette.Desktop;
using Effigy;

namespace Geppetto;

/// <summary>
/// Imports .effigy files into Godot scenes with Node3D root, Skeleton3D, MeshInstance3D (ArrayMesh + Skin),
/// StandardMaterial3D materials, and AnimationPlayer.
/// </summary>
[Tool]
public partial class EffigySceneImporter : EditorSceneFormatImporter
{
	/// <summary>Root meta naming the .effigy a scene was imported from (the dial finds characters by it).</summary>
	public const string EffigyMeta = "geppetto_effigy";

	public override string[] _GetExtensions() => new[] { "effigy" };

	public override GodotObject _ImportScene( string path, uint flags, Godot.Collections.Dictionary options )
	{
		try
		{
			GD.Print( $"[EffigyImporter] Importing scene: {path}" );
			// Godot hands us res:// paths; the kernel and System.IO need real absolute file paths.
			string diskPath = Path.GetFullPath( ProjectSettings.GlobalizePath( path ) );
			var studio = StudioDocument.ReadFile( diskPath );
			studio.Rebuild();

			var cpuMesh = CpuMesh.FromStudio( studio, 0, diskPath );
			var root = new Node3D();
			root.Name = Path.GetFileNameWithoutExtension( diskPath );
			root.SetMeta( EffigyMeta, path );

			Skeleton3D skeleton = null;
			if ( studio.Rig != null && studio.Rig.Count > 0 )
			{
				skeleton = EffigySkeletonBridge.ToGodotSkeleton( studio.Rig );
				root.AddChild( skeleton );
				skeleton.Owner = root;
			}

			string dir = Path.GetDirectoryName( diskPath );
			MaterialResolver resolver = null;
			if ( !string.IsNullOrEmpty( dir ) && Directory.Exists( dir ) )
			{
				string fullDir = Path.GetFullPath( dir );
				resolver = new MaterialResolver( fullDir, Path.GetFileNameWithoutExtension( diskPath ) );
			}

			// Slot looks (`materiallook SLOT color=... rough=...`) win over resolver and palette, as in the desktop viewer.
			var arrayMesh = EffigyMeshBridge.ToGodotArrayMesh( cpuMesh, resolver, studio.MaterialLooks, dir );
			// Texture paint (Paint layers) bound on the slot materials as embedded ImageTextures, saved with the scene.
			PaintShow.Apply( arrayMesh, cpuMesh, studio );
			var meshInstance = new MeshInstance3D
			{
				Name = "Body",
				Mesh = arrayMesh
			};

			if ( skeleton != null )
			{
				var skin = EffigySkeletonBridge.CreateSkin( skeleton );
				meshInstance.Skin = skin;
				meshInstance.Skeleton = new NodePath( ".." );
				skeleton.AddChild( meshInstance );
			}
			else
			{
				root.AddChild( meshInstance );
			}
			meshInstance.Owner = root;

			// Look for companion .riganim or .clip.json files in the same directory
			string baseName = Path.GetFileNameWithoutExtension( diskPath );
			if ( !string.IsNullOrEmpty( dir ) && Directory.Exists( dir ) )
			{
				var clips = Directory.GetFiles( dir, $"{baseName}*.riganim" );
				if ( clips.Length > 0 && skeleton != null )
				{
					var animPlayer = new AnimationPlayer { Name = "AnimationPlayer" };
					root.AddChild( animPlayer );
					animPlayer.Owner = root;

					var lib = new AnimationLibrary();
					foreach ( var clipFile in clips )
					{
						try
						{
							var clipData = RigAnimFile.ReadFile( clipFile );
							string clipName = Path.GetFileNameWithoutExtension( clipFile );
							if ( clipName.StartsWith( baseName + "_", StringComparison.OrdinalIgnoreCase ) )
								clipName = clipName.Substring( baseName.Length + 1 );
							var anim = EffigyClipBridge.ToGodotAnimation( clipData, studio.Rig, skeleton.Name, $"{skeleton.Name}/{meshInstance.Name}" );
							lib.AddAnimation( clipName, anim );
						}
						catch ( Exception ex )
						{
							GD.PrintErr( $"[EffigyImporter] Failed importing clip {clipFile}: {ex.Message}" );
						}
					}

					if ( lib.GetAnimationList().Count > 0 )
						animPlayer.AddAnimationLibrary( "", lib );
				}
			}

			GD.Print( $"[EffigyImporter] Successfully imported scene: {path} (verts={(cpuMesh.Vertices?.Length ?? 0) / CpuMesh.FloatsPerVertex}, tris={cpuMesh.TriangleCount}, surfaces={arrayMesh.GetSurfaceCount()})" );
			return root;
		}
		catch ( Exception ex )
		{
			GD.PrintErr( $"[EffigyImporter] Error importing {path}: {ex}" );
			return null;
		}
	}
}
#endif
