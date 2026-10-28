using System;
using System.Threading.Tasks;
using Godot;
using Marionette.Desktop;
using Effigy;

namespace Geppetto;

[Tool]
public partial class TestMeshBridge : SceneTree
{
	public override void _Initialize()
	{
		try
		{
			GD.Print( "[G3-TEST] Loading spike/g1-spike.effigy..." );
			var studio = StudioDocument.ReadFile( "spike/g1-spike.effigy" );
			studio.Rebuild();
			var cpuMesh = CpuMesh.FromStudio( studio, 0, "spike/g1-spike.effigy" );
			GD.Print( $"[G3-TEST] CpuMesh extracted: {cpuMesh.TriangleCount} tris, {cpuMesh.Batches.Length} batches, {cpuMesh.Vertices.Length / CpuMesh.FloatsPerVertex} verts" );

			var arrayMesh = EffigyMeshBridge.ToGodotArrayMesh( cpuMesh );
			int surfaceCount = arrayMesh.GetSurfaceCount();
			GD.Print( $"[G3-TEST] ArrayMesh surfaces: {surfaceCount}" );

			int totalGodotVerts = 0;
			int totalGodotTris = 0;
			Aabb aabb = new Aabb();
			bool first = true;

			for ( int s = 0; s < surfaceCount; s++ )
			{
				var arrays = arrayMesh.SurfaceGetArrays( s );
				var verts = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
				var indices = (int[])arrays[(int)Mesh.ArrayType.Index];
				var normals = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
				var uvs = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV];
				var tangents = (float[])arrays[(int)Mesh.ArrayType.Tangent];

				totalGodotVerts += verts.Length;
				totalGodotTris += indices.Length / 3;

				for ( int i = 0; i < verts.Length; i++ )
				{
					if ( first ) { aabb.Position = verts[i]; aabb.Size = Vector3.Zero; first = false; }
					else aabb = aabb.Expand( verts[i] );

					// Verify normal is unit length
					float normLen = normals[i].Length();
					if ( Math.Abs( normLen - 1f ) > 0.01f && normLen > 0.001f )
						throw new Exception( $"Normal not unit length: {normLen}" );

					// Verify tangent sign is +1 or -1
					float tw = tangents[i * 4 + 3];
					if ( Math.Abs( Math.Abs( tw ) - 1f ) > 0.01f )
						throw new Exception( $"Invalid tangent sign: {tw}" );
				}
			}

			GD.Print( $"[G3-TEST] Total Godot Verts: {totalGodotVerts}, Tris: {totalGodotTris}" );
			GD.Print( $"[G3-TEST] Godot Bounds: Min={aabb.Position}, Max={aabb.End}, Size={aabb.Size}" );

			if ( totalGodotTris != cpuMesh.TriangleCount )
				throw new Exception( $"Triangle count mismatch: Godot {totalGodotTris} vs Effigy {cpuMesh.TriangleCount}" );

			GD.Print( "[G3-TEST] SUCCESS: G3 EffigyMeshBridge verified!" );

			// G4: Test Skeleton and Skin Bridge
			Skeleton3D skeleton = null;
			Skeleton rig = studio.Rig;
			if ( rig != null && rig.Count > 0 )
			{
				GD.Print( $"[G4-TEST] Converting Rig with {rig.Count} bones..." );
				skeleton = EffigySkeletonBridge.ToGodotSkeleton( rig );
				if ( skeleton.GetBoneCount() != rig.Count )
					throw new Exception( $"Bone count mismatch: {skeleton.GetBoneCount()} vs {rig.Count}" );

				for ( int b = 0; b < rig.Count; b++ )
				{
					string name = skeleton.GetBoneName( b );
					int parent = skeleton.GetBoneParent( b );
					var rest = skeleton.GetBoneRest( b );
					GD.Print( $"[G4-TEST] Bone {b}: {name}, parent={parent}, rest origin={rest.Origin}" );
				}

				var skin = EffigySkeletonBridge.CreateSkin( skeleton );
				if ( skin.GetBindCount() != rig.Count )
					throw new Exception( $"Skin bind count mismatch: {skin.GetBindCount()} vs {rig.Count}" );

				GD.Print( $"[G4-TEST] Skin created with {skin.GetBindCount()} binds." );
				GD.Print( "[G4-TEST] SUCCESS: G4 EffigySkeletonBridge verified!" );
			}

			// G6: Test Material Bridge
			GD.Print( "[G6-TEST] Testing EffigyMaterialBridge..." );
			var desc = new MaterialDescriptor( "test_mat" )
			{
				BaseColorFactor = new Vec4( 0.2f, 0.4f, 0.8f, 1.0f ),
				RoughnessFactor = 0.35f,
				MetallicFactor = 0.85f,
				EmissionFactor = new Vec3( 1.0f, 0.5f, 0.0f ),
				DoubleSided = true,
				AlphaMode = AlphaMode.Mask,
				AlphaCutoff = 0.4f,
				Clearcoat = 0.75f,
				ClearcoatRoughness = 0.05f,
				Subsurface = 0.5f,
				SubsurfaceRadius = new Vec3( 0.9f, 0.2f, 0.1f ),
				Transmission = 0.6f,
				Sheen = 0.8f,
				SheenRoughness = 0.25f,
				Anisotropy = 0.45f,
				NormalMap = TextureBinding.ForNormal( new AssetReference( "dummy_norm", "dummy_norm.png" ) ),
				AmbientOcclusionMap = TextureBinding.ForData( new AssetReference( "dummy_ao", "dummy_ao.png" ) )
			};

			var godotMat = EffigyMaterialBridge.ToGodotMaterial( desc );
			if ( Math.Abs( godotMat.AlbedoColor.R - 0.2f ) > 0.01f ||
				 Math.Abs( godotMat.Roughness - 0.35f ) > 0.01f ||
				 Math.Abs( godotMat.Metallic - 0.85f ) > 0.01f ||
				 !godotMat.EmissionEnabled ||
				 godotMat.CullMode != BaseMaterial3D.CullModeEnum.Disabled ||
				 godotMat.Transparency != BaseMaterial3D.TransparencyEnum.AlphaScissor ||
				 Math.Abs( godotMat.AlphaScissorThreshold - 0.4f ) > 0.01f ||
				 !godotMat.ClearcoatEnabled ||
				 Math.Abs( godotMat.Clearcoat - 0.75f ) > 0.01f ||
				 !godotMat.SubsurfScatterEnabled ||
				 Math.Abs( godotMat.SubsurfScatterStrength - 0.5f ) > 0.01f ||
				 !godotMat.RefractionEnabled ||
				 Math.Abs( godotMat.RefractionScale - 0.6f ) > 0.01f ||
				 !godotMat.RimEnabled ||
				 Math.Abs( godotMat.Rim - 0.8f ) > 0.01f ||
				 !godotMat.AnisotropyEnabled ||
				 Math.Abs( godotMat.Anisotropy - 0.45f ) > 0.01f ||
				 !godotMat.NormalEnabled ||
				 !godotMat.AOEnabled )
			{
				throw new Exception( "MaterialBridge property mapping check failed!" );
			}
			GD.Print( "[G6-TEST] SUCCESS: G6 EffigyMaterialBridge verified!" );

			// G7: Test Clip Bridge
			GD.Print( "[G7-TEST] Testing EffigyClipBridge..." );
			var clip = new RigAnimFile
			{
				FrameRate = 30,
				FrameCount = 60
			};

			if ( rig != null && rig.Count > 0 )
			{
				var boneTrack = new RigTrack
				{
					Name = rig.Bones[0].Name,
					Kind = RigTrackKind.Bone
				};
				boneTrack.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( Vec3.Zero, Quat.Identity, Vec3.One ) } );
				boneTrack.Keys.Add( new RigKey { Frame = 30, Local = new RigPose( new Vec3( 10, 5, 0 ), Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), 0.5f ), Vec3.One ) } );
				boneTrack.Keys.Add( new RigKey { Frame = 60, Local = new RigPose( Vec3.Zero, Quat.Identity, Vec3.One ) } );
				clip.Tracks.Add( boneTrack );

				var lib = EffigyClipBridge.ToGodotLibrary( clip, rig, "test_clip" );
				if ( !lib.HasAnimation( "test_clip" ) )
					throw new Exception( "AnimationLibrary missing test_clip" );

				var anim = lib.GetAnimation( "test_clip" );
				GD.Print( $"[G7-TEST] Animation length={anim.Length}s, tracks={anim.GetTrackCount()}, step={anim.Step}" );

				int expectedTracks = rig.Count * 3;
				if ( anim.GetTrackCount() != expectedTracks )
					throw new Exception( $"Track count mismatch: {anim.GetTrackCount()} vs expected {expectedTracks}" );

				GD.Print( "[G7-TEST] SUCCESS: G7 EffigyClipBridge verified!" );
			}

			// G8: Test Scene Importer with materials
			GD.Print( "[G8-TEST] Testing EffigySceneImporter._ImportScene..." );
			var importer = new EffigySceneImporter();
			var importedObj = importer._ImportScene( "spike/g1-spike.effigy", 0, new Godot.Collections.Dictionary() );
			if ( importedObj is not Node3D rootNode )
				throw new Exception( $"Importer returned {importedObj?.GetType().Name ?? "null"}, expected Node3D" );

			GD.Print( $"[G8-TEST] Imported Root: {rootNode.Name}, Children count: {rootNode.GetChildCount()}" );
			var skelNode = rootNode.GetNodeOrNull<Skeleton3D>( "Skeleton3D" );
			if ( skelNode == null )
				throw new Exception( "Imported scene missing Skeleton3D node" );

			var meshNode = skelNode.GetNodeOrNull<MeshInstance3D>( "Body" );
			if ( meshNode == null )
				throw new Exception( "Imported scene missing Body MeshInstance3D" );

			if ( meshNode.Mesh == null )
				throw new Exception( "Body MeshInstance3D has null Mesh" );

			if ( meshNode.Skin == null )
				throw new Exception( "Body MeshInstance3D has null Skin" );

			var surfMat = meshNode.Mesh.SurfaceGetMaterial( 0 );
			if ( surfMat is not StandardMaterial3D stdMat )
				throw new Exception( $"Surface material is {surfMat?.GetType().Name ?? "null"}, expected StandardMaterial3D" );

			GD.Print( $"[G8-TEST] Surface 0 material: {stdMat.ResourceName}, Albedo={stdMat.AlbedoColor}, Roughness={stdMat.Roughness}, Metallic={stdMat.Metallic}" );
			GD.Print( $"[G8-TEST] Skeleton bones: {skelNode.GetBoneCount()}, Skin binds: {meshNode.Skin.GetBindCount()}, Mesh surfaces: {meshNode.Mesh.GetSurfaceCount()}" );
			GD.Print( "[G8-TEST] SUCCESS: G8 EffigySceneImporter verified!" );

			// G9: Test Live Document Service and Undo/Redo Adapter
			GD.Print( "[G9-TEST] Testing EffigyLiveDocumentService and EffigyUndoRedoAdapter..." );
			using var liveDoc = EffigyLiveDocumentService.Open( "spike/g1-spike.effigy" );
			var buildTask = liveDoc.RequestRebuildAsync();
			buildTask.Wait();
			var snapshot = buildTask.Result;
			if ( snapshot == null || snapshot.Mesh == null )
				throw new Exception( "EffigyLiveDocumentService background rebuild returned null snapshot or mesh." );
			GD.Print( $"[G9-TEST] Rebuilt snapshot: revision={snapshot.Revision}, tris={snapshot.Mesh.TriangleCount}" );

			// Test Undo/Redo adapter
			int testValue = 100;
			var godotUndoRedo = new UndoRedo();
			var adapter = new EffigyUndoRedoAdapter( liveDoc.Session, godotUndoRedo );

			var cmd = new ValueChangeCommand<int>( "Test change value", v => testValue = v, 100, 250 );
			adapter.Execute( cmd );
			if ( testValue != 250 ) throw new Exception( $"Expected testValue=250 after execute, got {testValue}" );

			bool undone = adapter.Undo();
			if ( !undone || testValue != 100 ) throw new Exception( $"Expected testValue=100 after undo, got {testValue} (undone={undone})" );

			bool redone = adapter.Redo();
			if ( !redone || testValue != 250 ) throw new Exception( $"Expected testValue=250 after redo, got {testValue} (redone={redone})" );

			GD.Print( "[G9-TEST] SUCCESS: G9 EffigyLiveDocumentService and EffigyUndoRedoAdapter verified!" );
		}
		catch ( Exception ex )
		{
			GD.PrintErr( $"[TEST] FAILED: {ex}" );
		}
		Quit( 0 );
	}
}
