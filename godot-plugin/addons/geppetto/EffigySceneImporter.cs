#if TOOLS
using System;
using System.IO;
using Godot;
using Marionette.Desktop;
using Effigy;

namespace Geppetto;

/// <summary>
/// Imports .effigy files into Godot scenes with Node3D root, Skeleton3D, MeshInstance3D (ArrayMesh + Skin),
/// and AnimationPlayer.
/// </summary>
[Tool]
public partial class EffigySceneImporter : EditorSceneFormatImporter
{
	public override string[] _GetExtensions() => new[] { "effigy" };

	public override GodotObject _ImportScene( string path, uint flags, Godot.Collections.Dictionary options )
	{
		try
		{
			GD.Print( $"[EffigyImporter] Importing scene: {path}" );
			var studio = StudioDocument.ReadFile( path );
			studio.Rebuild();

			var cpuMesh = CpuMesh.FromStudio( studio, 0, path );
			var root = new Node3D();
			root.Name = Path.GetFileNameWithoutExtension( path );

			Skeleton3D skeleton = null;
			if ( studio.Rig != null && studio.Rig.Count > 0 )
			{
				skeleton = EffigySkeletonBridge.ToGodotSkeleton( studio.Rig );
				root.AddChild( skeleton );
				skeleton.Owner = root;
			}

			var arrayMesh = EffigyMeshBridge.ToGodotArrayMesh( cpuMesh );
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
			string dir = Path.GetDirectoryName( path );
			string baseName = Path.GetFileNameWithoutExtension( path );
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

			GD.Print( $"[EffigyImporter] Successfully imported scene: {path} (verts={(cpuMesh.Vertices?.Length ?? 0) / CpuMesh.FloatsPerVertex}, tris={cpuMesh.TriangleCount})" );
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
