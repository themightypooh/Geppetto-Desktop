#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class FbxDmxExportTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void Throws<TException>( Action action, string message = "expected exception" ) where TException : Exception
		{
			try
			{
				action();
			}
			catch ( TException )
			{
				return;
			}
			catch ( Exception ex )
			{
				throw new Exception( $"{message} - expected {typeof( TException ).Name}, got {ex.GetType().Name}: {ex.Message}" );
			}
			throw new Exception( $"{message} - expected {typeof( TException ).Name}, got silent success" );
		}

		(PolyMesh Mesh, Skeleton Rig) CreateSimpleRiggedModel()
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity, 5f );
			rig.AddBone( "arm", 0, Xform.Translate( new Vec3( 0, 5, 0 ) ), 5f );

			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( -2, 0, 0 ),
				new Vec3( 2, 0, 0 ),
				new Vec3( 2, 5, 0 ),
				new Vec3( -2, 5, 0 ),
				new Vec3( 0, 10, 0 )
			} );

			mesh.Skin = new SkinWeights( 5 );
			mesh.Skin[0] = new[] { new BoneWeight( 0, 1f ) };
			mesh.Skin[1] = new[] { new BoneWeight( 0, 1f ) };
			mesh.Skin[2] = new[] { new BoneWeight( 0, 0.5f ), new BoneWeight( 1, 0.5f ) };
			mesh.Skin[3] = new[] { new BoneWeight( 0, 0.5f ), new BoneWeight( 1, 0.5f ) };
			mesh.Skin[4] = new[] { new BoneWeight( 1, 1f ) };

			mesh.AddFace( new[] { 0, 1, 2, 3 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 0.5f ), new Vec2( 0, 0.5f ) }, material: 0 );
			mesh.AddFace( new[] { 3, 2, 4 }, new[] { new Vec2( 0, 0.5f ), new Vec2( 1, 0.5f ), new Vec2( 0.5f, 1f ) }, material: 0 );

			return (mesh, rig);
		}

		PolyMesh CreateStaticBox( float sizeX, float sizeY, float sizeZ )
		{
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ),
				new Vec3( sizeX, 0, 0 ),
				new Vec3( sizeX, sizeY, 0 ),
				new Vec3( 0, sizeY, 0 ),
				new Vec3( 0, 0, sizeZ ),
				new Vec3( sizeX, 0, sizeZ ),
				new Vec3( sizeX, sizeY, sizeZ ),
				new Vec3( 0, sizeY, sizeZ )
			} );

			var uvs4 = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };
			mesh.AddFace( new[] { 0, 3, 2, 1 }, uvs4, 0 ); // Bottom (-Z)
			mesh.AddFace( new[] { 4, 5, 6, 7 }, uvs4, 0 ); // Top (+Z)
			mesh.AddFace( new[] { 0, 1, 5, 4 }, uvs4, 0 ); // Front (-Y)
			mesh.AddFace( new[] { 2, 3, 7, 6 }, uvs4, 0 ); // Back (+Y)
			mesh.AddFace( new[] { 3, 0, 4, 7 }, uvs4, 0 ); // Left (-X)
			mesh.AddFace( new[] { 1, 2, 6, 5 }, uvs4, 0 ); // Right (+X)
			return mesh;
		}

		registry.Add( "fbx-dmx", "rigged binary FBX export creates valid non-empty file", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "fbx_binary_nonempty" );
			Directory.CreateDirectory( testDir );
			string fbxPath = Path.Combine( testDir, "rigged_model.fbx" );

			var (mesh, rig) = CreateSimpleRiggedModel();
			string resultPath = FbxExportAdapter.ExportBinary( mesh, fbxPath, rig );

			Assert( File.Exists( resultPath ), "Exported FBX file must exist on disk." );
			byte[] bytes = File.ReadAllBytes( resultPath );
			Assert( bytes.Length > 0, "Exported FBX file must be non-empty." );
			Assert( FbxNode.IsBinary( bytes ), "Exported file must have binary FBX header signature." );
			Assert( FbxImport.IsFbx( bytes ), "FbxImport must recognize the bytes as a valid FBX format." );
		} );

		registry.Add( "fbx-dmx", "FBX round-trip through FbxImport verifies skeleton hierarchy and bones", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "fbx_roundtrip_hierarchy" );
			Directory.CreateDirectory( testDir );
			string fbxPath = Path.Combine( testDir, "hierarchy_test.fbx" );

			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 1, 2, 3 ) ), 4f );
			rig.AddBone( "spine", 0, Xform.Translate( new Vec3( 0, 10, 0 ) ), 6f );
			rig.AddBone( "arm_l", 1, Xform.Translate( new Vec3( -5, 0, 0 ) ), 3f );
			rig.AddBone( "arm_r", 1, Xform.Translate( new Vec3( 5, 0, 0 ) ), 3f );

			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 1, 2, 3 ),
				new Vec3( 1, 12, 3 ),
				new Vec3( -4, 12, 3 ),
				new Vec3( 6, 12, 3 )
			} );
			mesh.Skin = new SkinWeights( 4 );
			mesh.Skin[0] = new[] { new BoneWeight( 0, 1f ) };
			mesh.Skin[1] = new[] { new BoneWeight( 1, 1f ) };
			mesh.Skin[2] = new[] { new BoneWeight( 2, 1f ) };
			mesh.Skin[3] = new[] { new BoneWeight( 3, 1f ) };
			mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ) }, 0 );
			mesh.AddFace( new[] { 0, 3, 1 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ) }, 0 );

			FbxExportAdapter.ExportBinary( mesh, fbxPath, rig );
			var import = FbxExportAdapter.ValidateFbxRoundTrip( fbxPath );

			Assert( import.Skeleton != null, "Round-trip FBX must have an imported skeleton." );
			var importedSkeleton = import.Skeleton!;
			Assert( importedSkeleton.Count == rig.Count, $"Bone count mismatch: expected {rig.Count}, got {importedSkeleton.Count}." );

			for ( int i = 0; i < rig.Count; i++ )
			{
				string expectedName = rig.Bones[i].Name;
				int importedIdx = importedSkeleton.IndexOf( expectedName );
				Assert( importedIdx >= 0, $"Bone '{expectedName}' missing from imported skeleton." );

				var origBone = rig.Bones[i];
				var impBone = importedSkeleton.Bones[importedIdx];

				if ( origBone.Parent >= 0 )
				{
					string expectedParent = rig.Bones[origBone.Parent].Name;
					Assert( impBone.Parent >= 0, $"Bone '{expectedName}' lost its parent." );
					string importedParent = importedSkeleton.Bones[impBone.Parent].Name;
					Assert( importedParent == expectedParent, $"Bone '{expectedName}' parent mismatch: expected '{expectedParent}', got '{importedParent}'." );
				}
				else
				{
					Assert( impBone.Parent == -1, $"Root bone '{expectedName}' must have parent -1." );
				}

				Vec3 origWorldOrigin = rig.WorldBind( i ).Origin;
				Vec3 impWorldOrigin = importedSkeleton.WorldBind( importedIdx ).Origin;
				float diff = (origWorldOrigin - impWorldOrigin).Length;
				Assert( diff < 0.05f, $"World bind origin mismatch for bone '{expectedName}': diff={diff}." );
			}

			Assert( import.Pieces.Count > 0, "Import must contain at least one mesh piece." );
			Assert( import.Pieces[0].Mesh.VertexCount == mesh.VertexCount, "Vertex count must be preserved across FBX round trip." );
		} );

		registry.Add( "fbx-dmx", "1 kernel inch preserved", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "fbx_unit_scale_preserved" );
			Directory.CreateDirectory( testDir );
			string fbxPath = Path.Combine( testDir, "inch_cube.fbx" );

			float widthInches = 12f;
			float depthInches = 6f;
			float heightInches = 24f;

			var box = CreateStaticBox( widthInches, depthInches, heightInches );
			FbxExportAdapter.ExportBinary( box, fbxPath, skeleton: null, unitScale: FbxExportAdapter.InchesToCentimetres );

			var import = FbxExportAdapter.ValidateFbxRoundTrip( fbxPath );

			Assert( Math.Abs( import.UnitScale - 2.54 ) < 1e-4, $"Unit scale must be declared as 2.54 cm/inch: got {import.UnitScale}." );
			Assert( Math.Abs( import.AppliedScale - 1.0 ) < 1e-4, $"Applied scale factor must be 1.0: got {import.AppliedScale}." );

			var importedMesh = import.Pieces[0].Mesh;
			var (min, max) = ModelMeasure.Bounds( importedMesh );

			float measuredWidth = max.x - min.x;
			float measuredDepth = max.y - min.y;
			float measuredHeight = max.z - min.z;

			Assert( MathF.Abs( measuredWidth - widthInches ) < 0.01f, $"Width in inches not preserved: expected {widthInches}, got {measuredWidth}." );
			Assert( MathF.Abs( measuredDepth - depthInches ) < 0.01f, $"Depth in inches not preserved: expected {depthInches}, got {measuredDepth}." );
			Assert( MathF.Abs( measuredHeight - heightInches ) < 0.01f, $"Height in inches not preserved: expected {heightInches}, got {measuredHeight}." );
		} );

		registry.Add( "fbx-dmx", ">4 weights normalized to top 4", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "fbx_prune_weights" );
			Directory.CreateDirectory( testDir );
			string fbxPath = Path.Combine( testDir, "prune_test.fbx" );

			var rig = new Skeleton();
			for ( int i = 0; i < 6; i++ )
			{
				rig.AddBone( $"bone_{i}", i == 0 ? -1 : 0, Xform.Translate( new Vec3( 0, i * 2, 0 ) ) );
			}

			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ),
				new Vec3( 0, 1, 0 )
			} );

			mesh.Skin = new SkinWeights( 3 );
			// Vertex 0 has 6 influences: top 4 are bones 0 (0.35), 1 (0.25), 2 (0.15), 3 (0.15); bones 4 (0.06) and 5 (0.04) should be pruned
			mesh.Skin[0] = new[]
			{
				new BoneWeight( 0, 0.35f ),
				new BoneWeight( 1, 0.25f ),
				new BoneWeight( 2, 0.15f ),
				new BoneWeight( 3, 0.15f ),
				new BoneWeight( 4, 0.06f ),
				new BoneWeight( 5, 0.04f )
			};
			mesh.Skin[1] = new[] { new BoneWeight( 0, 1f ) };
			mesh.Skin[2] = new[] { new BoneWeight( 1, 1f ) };
			mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 0, 1 ) }, 0 );

			// Check adapter's pruning logic directly
			var directPruned = FbxExportAdapter.PruneToTop4Weights( mesh.Skin, rig.Count, maxInfluences: 4 );
			Assert( directPruned[0].Length == 4, $"Expected 4 weights after pruning, got {directPruned[0].Length}." );
			float directSum = directPruned[0].Sum( w => w.Weight );
			Assert( MathF.Abs( directSum - 1f ) < 1e-4f, $"Pruned weights must sum to 1.0, got {directSum}." );

			// Export to binary FBX and inspect round trip
			FbxExportAdapter.ExportBinary( mesh, fbxPath, rig );
			var import = FbxExportAdapter.ValidateFbxRoundTrip( fbxPath );

			Assert( import.Skeleton != null, "Imported skeleton must not be null." );
			var impSkeleton = import.Skeleton!;
			Assert( import.Weights.Count > 0 && import.Weights[0] != null, "Weights must be present in FBX import." );
			var resolvedSkin = FbxImport.Resolve( import.Weights[0], impSkeleton, null, out int unweighted );
			Assert( unweighted == 0, $"Expected 0 unweighted vertices, got {unweighted}." );
			Assert( resolvedSkin != null, "Resolved skin must not be null." );

			var v0Weights = resolvedSkin![0];
			Assert( v0Weights.Length == 4, $"Exported FBX must cap vertex influences to 4; got {v0Weights.Length}." );

			float weightSum = v0Weights.Sum( w => w.Weight );
			Assert( MathF.Abs( weightSum - 1f ) < 1e-4f, $"Weights must sum to 1.0; got {weightSum}." );

			// Verify bones 4 and 5 were pruned and top 4 remain
			var boneNames = v0Weights.Select( w => impSkeleton.Bones[w.Bone].Name ).ToHashSet();
			Assert( boneNames.Contains( "bone_0" ), "bone_0 must be retained in top 4." );
			Assert( boneNames.Contains( "bone_1" ), "bone_1 must be retained in top 4." );
			Assert( boneNames.Contains( "bone_2" ), "bone_2 must be retained in top 4." );
			Assert( boneNames.Contains( "bone_3" ), "bone_3 must be retained in top 4." );
			Assert( !boneNames.Contains( "bone_4" ), "bone_4 must be pruned." );
			Assert( !boneNames.Contains( "bone_5" ), "bone_5 must be pruned." );

			// Check normalized value for bone_0 (0.35 / 0.90 ~ 0.38888f)
			var b0Weight = v0Weights.First( w => impSkeleton.Bones[w.Bone].Name == "bone_0" ).Weight;
			float expectedB0 = 0.35f / 0.90f;
			Assert( MathF.Abs( b0Weight - expectedB0 ) < 0.01f, $"Normalized weight for bone_0 mismatch: expected {expectedB0:F4}, got {b0Weight:F4}." );
		} );

		registry.Add( "fbx-dmx", "static mesh export without rig", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "fbx_static_no_rig" );
			Directory.CreateDirectory( testDir );
			string fbxPath = Path.Combine( testDir, "static_box.fbx" );
			string dmxPath = Path.Combine( testDir, "static_box.dmx" );

			var box = CreateStaticBox( 10, 10, 10 );

			// Export FBX without rig
			FbxExportAdapter.ExportBinary( box, fbxPath, skeleton: null );
			var import = FbxExportAdapter.ValidateFbxRoundTrip( fbxPath );

			Assert( import.Skeleton == null, "Static FBX export must NOT invent a fake skeleton." );
			Assert( import.Pieces.Count == 1, "Static FBX must contain exactly 1 piece." );
			Assert( import.Pieces[0].Mesh.VertexCount == box.VertexCount, "Vertex count must match static box." );
			Assert( import.Pieces[0].Mesh.FaceCount == box.FaceCount, "Face count must match static box." );

			// Export DMX without rig (DMX writer automatically provides SingleRoot per format requirement)
			DmxExportAdapter.ExportMesh( box, dmxPath, skeleton: null );
			Assert( File.Exists( dmxPath ), "DMX mesh file must exist." );
			string dmxText = File.ReadAllText( dmxPath );
			Assert( dmxText.Contains( "<!-- dmx encoding keyvalues2 1 format model 22 -->" ), "DMX must contain model 22 header." );
			Assert( dmxText.Contains( "\"DmeMesh\"" ), "DMX must contain DmeMesh definition." );
		} );

		registry.Add( "fbx-dmx", "optional DMX model write", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "dmx_model_write" );
			Directory.CreateDirectory( testDir );
			string dmxPath = Path.Combine( testDir, "rigged_model.dmx" );

			var (mesh, rig) = CreateSimpleRiggedModel();
			string result = DmxExportAdapter.ExportMesh( mesh, dmxPath, rig, modelName: "test_rigged" );

			Assert( File.Exists( result ), "Exported DMX file must exist." );
			string dmxText = File.ReadAllText( result );

			Assert( dmxText.Contains( "<!-- dmx encoding keyvalues2 1 format model 22 -->" ), "Must contain format model 22 header." );
			Assert( dmxText.Contains( "\"DmeModel\"" ), "Must define DmeModel." );
			Assert( dmxText.Contains( "\"DmeMesh\"" ), "Must define DmeMesh." );
			Assert( dmxText.Contains( "\"DmeVertexData\"" ), "Must define DmeVertexData." );
			Assert( dmxText.Contains( "blendweights$0" ), "Must contain blendweights$0 vertex attribute." );
			Assert( dmxText.Contains( "blendindices$0" ), "Must contain blendindices$0 vertex attribute." );

			// Verify bones appear in the DMX joint definitions
			foreach ( var bone in rig.Bones )
			{
				Assert( dmxText.Contains( $"\"{bone.Name}\"" ), $"DMX must contain bone name '{bone.Name}'." );
			}
		} );

		registry.Add( "fbx-dmx", "optional DMX animation clip export with sampled poses", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "dmx_anim_sampled" );
			Directory.CreateDirectory( testDir );
			string dmxAnimPath = Path.Combine( testDir, "test_clip.dmx" );
			string playerDmxPath = Path.Combine( testDir, "player_clip.dmx" );

			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity, 5f );
			rig.AddBone( "arm", 0, Xform.Translate( new Vec3( 0, 5, 0 ) ), 5f );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var track = new RigTrack { Name = "arm", Kind = RigTrackKind.Bone };
			track.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One ) } );
			track.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 10, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( track );

			// 1. Export from RigAnimFile
			string path1 = DmxExportAdapter.ExportAnimation( dmxAnimPath, rig, clip, modelName: "arm_model" );
			Assert( File.Exists( path1 ), "DMX animation file must exist." );
			string animText = File.ReadAllText( path1 );

			Assert( animText.Contains( "<!-- dmx encoding keyvalues2 1 format model 22 -->" ), "Must contain model 22 header." );
			Assert( animText.Contains( "\"DmeAnimationList\"" ), "Must define DmeAnimationList." );
			Assert( animText.Contains( "\"DmeChannelsClip\"" ), "Must define DmeChannelsClip." );
			Assert( animText.Contains( "\"DmeChannel\"" ), "Must define DmeChannel." );
			Assert( animText.Contains( "\"arm_p\"" ), "Must contain position channel 'arm_p'." );
			Assert( animText.Contains( "\"arm_o\"" ), "Must contain orientation channel 'arm_o'." );

			// 2. Export from MarionettePlayer sampled poses
			var player = new MarionettePlayer( rig, startPlaying: false );
			var sampledClip = DmxExportAdapter.SampleClipFromPlayer( player, durationSeconds: 0.5f, frameRate: 30f );
			Assert( sampledClip.FrameCount == 16, $"Expected 16 sampled frames for 0.5s @ 30fps (0..15), got {sampledClip.FrameCount}." );

			string path2 = DmxExportAdapter.ExportAnimation( playerDmxPath, player, durationSeconds: 0.5f, frameRate: 30f );
			Assert( File.Exists( path2 ), "Player sampled DMX anim file must exist." );
			string playerAnimText = File.ReadAllText( path2 );
			Assert( playerAnimText.Contains( "\"DmeAnimationList\"" ), "Sampled player anim must define DmeAnimationList." );
		} );

		registry.Add( "fbx-dmx", "overwrite policy respected", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_overwrite_policy" );
			Directory.CreateDirectory( testDir );

			string fbxPath = Path.Combine( testDir, "overwrite_test.fbx" );
			string dmxPath = Path.Combine( testDir, "overwrite_test.dmx" );

			var (mesh, rig) = CreateSimpleRiggedModel();

			// Initial exports succeed
			FbxExportAdapter.ExportBinary( mesh, fbxPath, rig );
			DmxExportAdapter.ExportMesh( mesh, dmxPath, rig );

			Assert( File.Exists( fbxPath ) && File.Exists( dmxPath ) );

			// OverwritePolicy.FailIfExists must throw IOException
			Throws<IOException>( () => FbxExportAdapter.ExportBinary( mesh, fbxPath, rig, overwritePolicy: OverwritePolicy.FailIfExists ),
				"FBX export must refuse overwrite when OverwritePolicy.FailIfExists is set." );

			Throws<IOException>( () => DmxExportAdapter.ExportMesh( mesh, dmxPath, rig, overwritePolicy: OverwritePolicy.FailIfExists ),
				"DMX export must refuse overwrite when OverwritePolicy.FailIfExists is set." );

			// OverwritePolicy.Overwrite must succeed and replace
			FbxExportAdapter.ExportBinary( mesh, fbxPath, rig, overwritePolicy: OverwritePolicy.Overwrite );
			DmxExportAdapter.ExportMesh( mesh, dmxPath, rig, overwritePolicy: OverwritePolicy.Overwrite );

			Assert( File.Exists( fbxPath ) && File.Exists( dmxPath ) );
		} );

		registry.Add( "fbx-dmx", "export does not cull bones exceeding 220 display limit", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "fbx_over_220_bones" );
			Directory.CreateDirectory( testDir );
			string fbxPath = Path.Combine( testDir, "many_bones.fbx" );

			int targetBoneCount = 240; // > 220 viewport limit
			var rig = new Skeleton();
			for ( int i = 0; i < targetBoneCount; i++ )
			{
				int parent = i == 0 ? -1 : (i - 1);
				rig.AddBone( $"bone_{i:D3}", parent, Xform.Translate( new Vec3( 0, 0, 1 ) ), 1f );
			}

			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ),
				new Vec3( 0, 1, 0 )
			} );
			mesh.Skin = new SkinWeights( 3 );
			mesh.Skin[0] = new[] { new BoneWeight( 0, 1f ) };
			mesh.Skin[1] = new[] { new BoneWeight( 100, 1f ) };
			mesh.Skin[2] = new[] { new BoneWeight( 239, 1f ) };
			mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 0, 1 ) }, 0 );

			FbxExportAdapter.ExportBinary( mesh, fbxPath, rig );
			var import = FbxExportAdapter.ValidateFbxRoundTrip( fbxPath );

			Assert( import.Skeleton != null, "Imported skeleton must not be null." );
			var impSkeleton = import.Skeleton!;
			Assert( impSkeleton.Count == targetBoneCount,
				$"Export must not cull bones to 220 viewport limit: expected {targetBoneCount}, got {impSkeleton.Count}." );
			Assert( impSkeleton.Bones[239].Name == "bone_239", "Highest index bone must be preserved." );
		} );
	}
}
