#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using Effigy;
using Marionette.Desktop;

internal static class ExportServiceTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		DocumentSession CreateCubeSession( string workDir )
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 10f }, SizeY = { Value = 10f }, SizeZ = { Value = 10f } } );
			studio.Rebuild();
			string effigyPath = Path.Combine( workDir, "cube_source.effigy" );
			return DocumentSession.CreateEffigy( effigyPath, studio );
		}

		(DocumentSession Session, Skeleton Rig, RigAnimFile Clip) CreateRiggedSession( string workDir )
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			rig.AddBone( "arm", 0, Xform.Translate( new Vec3( 0, 5, 0 ) ) );

			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ),
				new Vec3( 0, 5, 0 ),
				new Vec3( 2, 5, 0 )
			} );
			mesh.Skin = new SkinWeights( 3 );
			mesh.Skin[0] = new[] { new BoneWeight( 0, 1f ) };
			mesh.Skin[1] = new[] { new BoneWeight( 1, 1f ) };
			mesh.Skin[2] = new[] { new BoneWeight( 1, 1f ) };
			mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ) }, material: 0 );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var track = new RigTrack { Name = "arm", Kind = RigTrackKind.Bone };
			track.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One ) } );
			track.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 10, 5, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( track );

			var fbxResult = new FbxImport.Result { Skeleton = rig };
			fbxResult.Pieces.Add( new ObjReader.ObjPiece( "piece0", mesh ) );
			fbxResult.Weights.Add( null ); // weights resolved directly on mesh.Skin

			string fbxPath = Path.Combine( workDir, "rigged_source.fbx" );
			var session = DocumentSession.CreateFbx( fbxPath, fbxResult, clip: clip );
			return (session, rig, clip);
		}

		registry.Add( "export", "static OBJ export produces valid vertices, normals, and faces", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_static_obj" );
			Directory.CreateDirectory( testDir );

			var session = CreateCubeSession( testDir );
			string objPath = Path.Combine( testDir, "model.obj" );
			string mtlPath = Path.Combine( testDir, "model.mtl" );

			var service = new ExportService();
			var request = new ExportRequest( session, ExportFormat.Obj, objPath );
			var result = service.Export( request );

			Assert( result.Success, $"OBJ export failed: {result.ErrorMessage}" );
			Assert( File.Exists( objPath ), "Exported .obj file must exist on disk." );
			Assert( File.Exists( mtlPath ), "Exported .mtl file must exist on disk." );

			string objText = File.ReadAllText( objPath );
			Assert( objText.Contains( "v " ), "OBJ file must contain 'v ' vertex positions." );
			Assert( objText.Contains( "vn " ), "OBJ file must contain 'vn ' vertex normals." );
			Assert( objText.Contains( "f " ), "OBJ file must contain 'f ' face definitions." );
			Assert( objText.Contains( "mtllib model.mtl" ), "OBJ file must reference companion MTL file." );

			var parsedMesh = ObjReader.ReadFile( objPath );
			Assert( parsedMesh.VertexCount >= 8, $"Expected at least 8 vertices, got {parsedMesh.VertexCount}" );
			Assert( parsedMesh.FaceCount >= 6, $"Expected at least 6 faces, got {parsedMesh.FaceCount}" );
			Assert( parsedMesh.Positions.All( p => float.IsFinite( p.x ) && float.IsFinite( p.y ) && float.IsFinite( p.z ) ),
				"All exported vertex coordinates must be finite and non-NaN." );

			string mtlText = File.ReadAllText( mtlPath );
			Assert( mtlText.Contains( "newmtl " ), "MTL file must define at least one material." );
			Assert( mtlText.Contains( "Kd " ), "MTL file must define diffuse reflectance (Kd)." );
		} );

		registry.Add( "export", "posed vs bind pose OBJ export produce distinct geometry", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_posed_vs_bind" );
			Directory.CreateDirectory( testDir );

			var (session, _, _) = CreateRiggedSession( testDir );
			string bindObjPath = Path.Combine( testDir, "bind.obj" );
			string posedObjPath = Path.Combine( testDir, "posed.obj" );

			var service = new ExportService();

			// Export bind pose
			var bindReq = new ExportRequest( session, ExportFormat.Obj, bindObjPath, PoseMode.BindPose );
			var bindRes = service.Export( bindReq );
			Assert( bindRes.Success, $"Bind pose export failed: {bindRes.ErrorMessage}" );

			// Export posed at frame 30 (Time = 1.0s) with explicit bake
			var posedReq = new ExportRequest( session, ExportFormat.Obj, posedObjPath, PoseMode.Posed,
				playheadTime: 1.0f, bakePosedMesh: true );
			var posedRes = service.Export( posedReq );
			Assert( posedRes.Success, $"Posed export failed: {posedRes.ErrorMessage}" );

			var bindMesh = ObjReader.ReadFile( bindObjPath );
			var posedMesh = ObjReader.ReadFile( posedObjPath );

			Assert( bindMesh.VertexCount == posedMesh.VertexCount, "Vertex counts must match." );
			Assert( bindMesh.FaceCount == posedMesh.FaceCount, "Face counts must match." );

			// Arm bone moved from X=0 to X=10; vertex 1 was at (0, 5, 0) in bind pose
			Assert( MathF.Abs( bindMesh.Positions[1].x ) < 1e-3f, $"Bind vertex 1 X should be ~0, got {bindMesh.Positions[1].x}" );
			Assert( MathF.Abs( posedMesh.Positions[1].x - 10f ) < 1e-2f, $"Posed vertex 1 X should be deformed to ~10, got {posedMesh.Positions[1].x}" );

			float dist = (bindMesh.Positions[1] - posedMesh.Positions[1]).Length;
			Assert( dist > 5f, $"Posed and bind pose geometry must be distinct (distance = {dist})" );
		} );

		registry.Add( "export", "OBJ export capability report accurately flags lost skeletal animation", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_capability_flags" );
			Directory.CreateDirectory( testDir );

			var (session, _, _) = CreateRiggedSession( testDir );
			var service = new ExportService();

			// 1. Posed request without explicit bake must be rejected and flag lost animation
			var reqUnbaked = new ExportRequest( session, ExportFormat.Obj, Path.Combine( testDir, "unbaked.obj" ),
				PoseMode.Posed, bakePosedMesh: false );
			var repUnbaked = service.EvaluateCapabilities( reqUnbaked );

			Assert( !repUnbaked.CanExport, "OBJ export of animation without explicit BakePosedMesh must fail." );
			Assert( !repUnbaked.PreservesAnimation, "OBJ capability report must report PreservesAnimation = false." );
			Assert( repUnbaked.LostFeatures.Any( f => f.Contains( "animation", StringComparison.OrdinalIgnoreCase ) ),
				"LostFeatures must accurately report skeletal animation loss." );

			// 2. Bind pose request allows export but still accurately records lost skeletal animation
			var reqBind = new ExportRequest( session, ExportFormat.Obj, Path.Combine( testDir, "bind.obj" ), PoseMode.BindPose );
			var repBind = service.EvaluateCapabilities( reqBind );

			Assert( repBind.CanExport, "Bind pose OBJ export should be allowed." );
			Assert( !repBind.PreservesAnimation, "PreservesAnimation must remain false for OBJ format." );
			Assert( repBind.LostFeatures.Any( f => f.Contains( "animation", StringComparison.OrdinalIgnoreCase ) ),
				"LostFeatures must accurately report skeletal animation loss in bind pose." );

			// 3. Baked posed request allows export while still accurately reporting skeletal animation loss
			var reqBaked = new ExportRequest( session, ExportFormat.Obj, Path.Combine( testDir, "baked.obj" ),
				PoseMode.Posed, bakePosedMesh: true );
			var repBaked = service.EvaluateCapabilities( reqBaked );

			Assert( repBaked.CanExport, "Explicitly baked posed OBJ export should be allowed." );
			Assert( !repBaked.PreservesAnimation, "PreservesAnimation must remain false even when baked." );
			Assert( repBaked.LostFeatures.Any( f => f.Contains( "animation", StringComparison.OrdinalIgnoreCase ) ),
				"LostFeatures must accurately report skeletal animation loss when baked." );
		} );

		registry.Add( "export", "refused overwrite when destination exists and Overwrite is false", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_refused_overwrite" );
			Directory.CreateDirectory( testDir );

			var session = CreateCubeSession( testDir );
			string destFile = Path.Combine( testDir, "existing_model.obj" );
			const string originalPayload = "DO_NOT_OVERWRITE_THIS_PAYLOAD";
			File.WriteAllText( destFile, originalPayload );

			var service = new ExportService();
			var request = new ExportRequest( session, ExportFormat.Obj, destFile,
				overwritePolicy: OverwritePolicy.FailIfExists );

			var result = service.Export( request );

			Assert( !result.Success, "Export must fail when destination exists and OverwritePolicy is FailIfExists." );
			Assert( result.ErrorMessage is not null && result.ErrorMessage.Contains( "Refusing to overwrite" ),
				"ErrorMessage must indicate refused overwrite." );
			Assert( File.ReadAllText( destFile ) == originalPayload, "Original file payload must remain intact." );

			// OverwritePolicy.Overwrite must succeed
			var overwriteReq = request with { OverwritePolicy = OverwritePolicy.Overwrite };
			var overwriteRes = service.Export( overwriteReq );

			Assert( overwriteRes.Success, $"Export with OverwritePolicy.Overwrite must succeed: {overwriteRes.ErrorMessage}" );
			Assert( File.ReadAllText( destFile ) != originalPayload, "Destination file must be replaced." );
		} );

		registry.Add( "export", "path traversal in material name sanitized / blocked", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_path_traversal" );
			Directory.CreateDirectory( testDir );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 5f } } );
			studio.MaterialNames[0] = "../../malicious_escape/danger_material";
			studio.Rebuild();

			string effigyPath = Path.Combine( testDir, "source.effigy" );
			var session = DocumentSession.CreateEffigy( effigyPath, studio );

			string exportDir = Path.Combine( testDir, "out_traversal" );
			string exportObj = Path.Combine( exportDir, "model.obj" );
			string exportMtl = Path.Combine( exportDir, "model.mtl" );

			var service = new ExportService();
			var request = new ExportRequest( session, ExportFormat.Obj, exportObj );
			var result = service.Export( request );

			Assert( result.Success, $"Export should succeed with sanitized material name: {result.ErrorMessage}" );
			Assert( File.Exists( exportObj ), "Exported .obj must exist." );
			Assert( File.Exists( exportMtl ), "Exported .mtl must exist." );

			string objText = File.ReadAllText( exportObj );
			string mtlText = File.ReadAllText( exportMtl );

			Assert( !objText.Contains( "../" ) && !objText.Contains( "..\\" ), "OBJ file must not contain '../' or '..\\'." );
			Assert( !mtlText.Contains( "../" ) && !mtlText.Contains( "..\\" ), "MTL file must not contain '../' or '..\\'." );

			Assert( objText.Contains( "usemtl danger_material" ), "OBJ must use sanitized material name 'danger_material'." );
			Assert( mtlText.Contains( "newmtl danger_material" ), "MTL must define sanitized material 'newmtl danger_material'." );

			Assert( !Directory.Exists( Path.Combine( testDir, "malicious_escape" ) ),
				"No directories should be created outside the export folder." );
		} );

		registry.Add( "export", "native effigy export round-trip via ExportService", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_effigy_roundtrip" );
			Directory.CreateDirectory( testDir );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 42.5f }, SizeY = { Value = 18.0f } } );
			studio.Rebuild();

			string srcEffigy = Path.Combine( testDir, "original.effigy" );
			var session = DocumentSession.CreateEffigy( srcEffigy, studio );

			string outEffigy = Path.Combine( testDir, "exported.effigy" );
			var service = new ExportService();
			var request = new ExportRequest( session, ExportFormat.Effigy, outEffigy );
			var result = service.Export( request );

			Assert( result.Success, $"Effigy export failed: {result.ErrorMessage}" );
			Assert( File.Exists( outEffigy ), "Exported .effigy file must exist." );

			var reloadedStudio = StudioDocument.ReadFile( outEffigy );
			reloadedStudio.Rebuild();

			Assert( reloadedStudio.Features.Count == 1, "Reloaded studio must have 1 feature." );
			var prim = (PrimitiveFeature)reloadedStudio.Features[0];
			Assert( MathF.Abs( prim.SizeX.Value - 42.5f ) < 0.001f, $"Expected SizeX=42.5, got {prim.SizeX.Value}" );
			Assert( MathF.Abs( prim.SizeY.Value - 18.0f ) < 0.001f, $"Expected SizeY=18.0, got {prim.SizeY.Value}" );
		} );

		registry.Add( "export", "native riganim export preserves unknown JSON sentinels", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_riganim_sentinels" );
			Directory.CreateDirectory( testDir );

			const string originalJson = """
			{
				"SourceModel": "models/survey.vmdl",
				"RigAsset": "models/survey.rig",
				"AnimationSpeed": 30,
				"FrameCount": 10,
				"UnknownSentinelObject": { "Score": 9999, "Tags": ["alpha", "beta"] },
				"UnknownArraySentinel": [ 1, 2, 3 ],
				"CustomSentinelKey": "preserved_value",
				"BoneTracks": []
			}
			""";

			var clip = RigAnimFile.Read( originalJson );
			var session = DocumentSession.CreateEffigy(
				path: Path.Combine( testDir, "dummy.effigy" ),
				studio: new PartStudio(),
				clipPath: Path.Combine( testDir, "dummy.riganim" ),
				clipJson: originalJson,
				clip: clip
			);

			string outClipPath = Path.Combine( testDir, "exported.riganim" );
			var service = new ExportService();
			var request = new ExportRequest( session, ExportFormat.RigAnim, outClipPath );
			var result = service.Export( request );

			Assert( result.Success, $"RigAnim export failed: {result.ErrorMessage}" );
			Assert( File.Exists( outClipPath ), "Exported .riganim file must exist." );

			string exportedJson = File.ReadAllText( outClipPath );
			var jsonNode = JsonNode.Parse( exportedJson )!.AsObject();

			Assert( jsonNode.ContainsKey( "UnknownSentinelObject" ), "UnknownSentinelObject must be preserved." );
			Assert( jsonNode["UnknownSentinelObject"]!["Score"]!.GetValue<int>() == 9999, "Score value must be 9999." );
			Assert( jsonNode["UnknownSentinelObject"]!["Tags"]![0]!.GetValue<string>() == "alpha", "Tags array must be intact." );
			Assert( jsonNode.ContainsKey( "UnknownArraySentinel" ), "UnknownArraySentinel must be preserved." );
			Assert( jsonNode["CustomSentinelKey"]!.GetValue<string>() == "preserved_value", "Custom sentinel key intact." );
		} );

		registry.Add( "export", "cancellation cleans up temporary files and preserves destination", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_cancellation" );
			Directory.CreateDirectory( testDir );

			var session = CreateCubeSession( testDir );
			string destFile = Path.Combine( testDir, "cancel_target.obj" );
			const string initialPayload = "PRESERVED_TARGET_CONTENT";
			File.WriteAllText( destFile, initialPayload );

			using var cts = new CancellationTokenSource();
			cts.Cancel(); // Pre-cancelled token

			var service = new ExportService();
			var request = new ExportRequest( session, ExportFormat.Obj, destFile,
				overwritePolicy: OverwritePolicy.Overwrite );

			var result = service.Export( request, cancellationToken: cts.Token );

			Assert( !result.Success, "Export must return Success = false on cancellation." );
			Assert( File.ReadAllText( destFile ) == initialPayload, "Destination file must be preserved on cancellation." );

			var leftoverDirs = Directory.GetDirectories( testDir, ".export_staging_*" );
			Assert( leftoverDirs.Length == 0, "All temporary staging directories must be cleaned up on cancellation." );
		} );

		registry.Add( "export", "source session and source files remain completely untouched", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "export_non_destructive" );
			Directory.CreateDirectory( testDir );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 14f } } );
			studio.Rebuild();

			string srcPath = Path.Combine( testDir, "untouched_source.effigy" );
			StudioDocument.WriteFile( studio, srcPath );
			byte[] initialBytes = File.ReadAllBytes( srcPath );
			byte[] initialHash = SHA256.HashData( initialBytes );

			var session = DocumentSession.CreateEffigy( srcPath, studio );
			long initialRev = session.Revision;
			bool initialDirty = session.IsDirty;

			var service = new ExportService();
			string outObj = Path.Combine( testDir, "out_untouched.obj" );
			var resObj = service.Export( new ExportRequest( session, ExportFormat.Obj, outObj ) );
			Assert( resObj.Success, "OBJ export should succeed." );

			string outEffigy = Path.Combine( testDir, "out_untouched.effigy" );
			var resEffigy = service.Export( new ExportRequest( session, ExportFormat.Effigy, outEffigy ) );
			Assert( resEffigy.Success, "Effigy export should succeed." );

			Assert( session.Revision == initialRev, "Session revision must not change during export." );
			Assert( session.IsDirty == initialDirty, "Session dirty state must not change during export." );

			byte[] postBytes = File.ReadAllBytes( srcPath );
			byte[] postHash = SHA256.HashData( postBytes );
			Assert( initialHash.SequenceEqual( postHash ), "Source file on disk must remain bit-for-bit identical." );
		} );
	}
}
