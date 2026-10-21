#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Effigy;
using Marionette.Desktop;

internal static class DocumentSaveTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "save", "save_effigy_atomic_roundtrip", () =>
		{
			string dir = Path.Combine( settings.WorkDirectory, "save_effigy_atomic" );
			Directory.CreateDirectory( dir );
			string path = Path.Combine( dir, "box.effigy" );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 15f } } );
			studio.Rebuild();

			var session = DocumentSession.CreateEffigy( path, studio );
			session.History.Execute( new DelegateEditCommand( "edit size",
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 25f,
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 15f
			) );

			Assert( session.IsDirty, "Session must be dirty after edit." );

			var service = new DocumentSaveService();
			var result = service.Save( session );

			Assert( result.IsSuccess, $"Save failed: {result.ErrorMessage}" );
			Assert( File.Exists( path ), "Target file should exist." );
			Assert( !session.IsDirty, "Session should be clean after successful save." );

			var readStudio = StudioDocument.ReadFile( path );
			readStudio.Rebuild();
			var prim = (PrimitiveFeature)readStudio.Features[0];
			Assert( Math.Abs( prim.SizeX.Value - 25f ) < 0.001f, $"Expected SizeX=25, got {prim.SizeX.Value}" );
		} );

		registry.Add( "save", "unknown_metadata_sentinel_survives", () =>
		{
			string dir = Path.Combine( settings.WorkDirectory, "save_riganim_metadata" );
			Directory.CreateDirectory( dir );
			string path = Path.Combine( dir, "test.riganim" );

			string baseJson = """
			{
				"SourceModel": "models/survey.vmdl",
				"RigAsset": "models/survey.rig",
				"AnimationSpeed": 30,
				"FrameCount": 10,
				"UnknownSentinelObject": { "Score": 9999, "Tags": ["a", "b"] },
				"CustomMarkerList": [ "marker1", "marker2" ],
				"BoneTracks": []
			}
			""";

			var clip = RigAnimFile.Read( baseJson );
			var skeleton = new Skeleton();
			skeleton.AddBone( "root", -1, Xform.Identity );

			var session = DocumentSession.CreateEffigy(
				path: Path.Combine( dir, "model.effigy" ),
				studio: new PartStudio(),
				clipPath: path,
				clipJson: baseJson,
				clip: clip
			);

			// Edit clip by adding a keyframe
			ClipEditService.InsertOrReplaceKey(
				session,
				RigTrackKind.Bone,
				"root",
				frame: 0,
				pose: new RigPose( new Vec3( 1, 2, 3 ), Quat.Identity, Vec3.One )
			);

			Assert( session.IsDirty, "Session must be dirty after clip edit." );

			var service = new DocumentSaveService();
			var result = service.Save( session, destinationPath: path );

			Assert( result.IsSuccess, $"Clip save failed: {result.ErrorMessage}" );
			Assert( File.Exists( path ), "Saved riganim file must exist." );

			string savedJson = File.ReadAllText( path );
			var jsonNode = JsonNode.Parse( savedJson )!.AsObject();

			Assert( jsonNode.ContainsKey( "UnknownSentinelObject" ), "Unknown sentinel object must survive save." );
			Assert( jsonNode["UnknownSentinelObject"]!["Score"]!.GetValue<int>() == 9999, "Sentinel value intact." );
			Assert( jsonNode.ContainsKey( "CustomMarkerList" ), "Custom marker list must survive save." );
		} );

		registry.Add( "save", "save_as_relocation", () =>
		{
			string rootDir = Path.Combine( settings.WorkDirectory, "save_as_root" );
			Directory.CreateDirectory( rootDir );
			string origPath = Path.Combine( rootDir, "original.effigy" );
			string newPath = Path.Combine( rootDir, "subfolder", "relocated.effigy" );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 10f } } );
			studio.Rebuild();
			File.WriteAllText( origPath, StudioDocument.Write( studio ) );

			var resolver = new AssetPathResolver( rootDir );
			var assetRef = new AssetReference( "model_1", "original.effigy" );

			var session = DocumentSession.CreateEffigy( origPath, studio, assetRef: assetRef );
			session.History.Execute( new DelegateEditCommand( "modify",
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 20f,
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 10f
			) );

			var service = new DocumentSaveService();
			var result = service.Save( session, destinationPath: newPath, resolver: resolver );

			Assert( result.IsSuccess, $"Save As failed: {result.ErrorMessage}" );
			Assert( session.SourcePath == newPath, $"Session source path should be updated to {newPath}" );
			Assert( session.AssetReference != null, "Session asset reference should not be null." );
			Assert( session.AssetReference.Path == "subfolder/relocated.effigy",
				$"Asset reference path should be relocated to subfolder/relocated.effigy, got {session.AssetReference.Path}" );
			Assert( File.Exists( newPath ), "New file must exist." );
			Assert( File.Exists( origPath ), "Original file must remain intact." );
		} );

		registry.Add( "save", "write_flush_failure_preserves_destination", () =>
		{
			string dir = Path.Combine( settings.WorkDirectory, "write_failure" );
			Directory.CreateDirectory( dir );
			string path = Path.Combine( dir, "target.effigy" );
			string originalContent = "effigy 1\norigin 0,0,0\n";
			File.WriteAllText( path, originalContent );
			byte[] origHash = SHA256.HashData( File.ReadAllBytes( path ) );

			var mockFs = new FaultInjectingFileSystem( PhysicalFileSystem.Instance )
			{
				FailOpenWrite = true
			};

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() );
			var session = DocumentSession.CreateEffigy( path, studio );

			var service = new DocumentSaveService( mockFs );
			var result = service.Save( session );

			Assert( result.Status == SaveStatus.IoError, "Save should report IoError on write failure." );
			Assert( File.Exists( path ), "Target file must still exist." );
			byte[] postHash = SHA256.HashData( File.ReadAllBytes( path ) );
			Assert( Convert.ToHexString( origHash ) == Convert.ToHexString( postHash ),
				"Target file bytes must be unchanged after write failure." );

			// Verify no leftover temp files
			var temps = Directory.GetFiles( dir, "*.tmp*" );
			Assert( temps.Length == 0, $"Expected 0 temp files left, found {temps.Length}" );
		} );

		registry.Add( "save", "rename_failure_preserves_destination", () =>
		{
			string dir = Path.Combine( settings.WorkDirectory, "rename_failure" );
			Directory.CreateDirectory( dir );
			string path = Path.Combine( dir, "target.effigy" );
			string originalContent = "effigy 1\norigin 1,2,3\n";
			File.WriteAllText( path, originalContent );
			byte[] origHash = SHA256.HashData( File.ReadAllBytes( path ) );

			var mockFs = new FaultInjectingFileSystem( PhysicalFileSystem.Instance )
			{
				FailReplace = true
			};

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() );
			var session = DocumentSession.CreateEffigy( path, studio );

			var service = new DocumentSaveService( mockFs );
			var result = service.Save( session );

			Assert( result.Status == SaveStatus.IoError, "Save should report IoError on replace failure." );
			byte[] postHash = SHA256.HashData( File.ReadAllBytes( path ) );
			Assert( Convert.ToHexString( origHash ) == Convert.ToHexString( postHash ),
				"Target file must remain byte-identical after replace failure." );

			var temps = Directory.GetFiles( dir, "*.tmp*" );
			Assert( temps.Length == 0, "Temp file must be cleaned up after replace failure." );
		} );

		registry.Add( "save", "save_cancellation_preserves_target", () =>
		{
			string dir = Path.Combine( settings.WorkDirectory, "save_cancel" );
			Directory.CreateDirectory( dir );
			string path = Path.Combine( dir, "target.effigy" );
			File.WriteAllText( path, "effigy 1\n" );

			var cts = new CancellationTokenSource();
			cts.Cancel(); // Pre-cancel

			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( path, studio );

			var service = new DocumentSaveService();
			var result = service.SaveAsync( session, cancellationToken: cts.Token ).GetAwaiter().GetResult();

			Assert( result.Status == SaveStatus.Cancelled, "Save result should be Cancelled." );
			Assert( File.ReadAllText( path ) == "effigy 1\n", "Original target must be preserved." );
		} );

		registry.Add( "save", "edits_during_save_do_not_mark_clean", () =>
		{
			string dir = Path.Combine( settings.WorkDirectory, "save_concurrency" );
			Directory.CreateDirectory( dir );
			string path = Path.Combine( dir, "model.effigy" );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 10f } } );
			studio.Rebuild();

			var session = DocumentSession.CreateEffigy( path, studio );

			var delayedFs = new FaultInjectingFileSystem( PhysicalFileSystem.Instance )
			{
				PreReplaceCallback = () =>
				{
					// Concurrent edit while save worker is running
					session.History.Execute( new DelegateEditCommand( "concurrent edit",
						() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 99f,
						() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 10f
					) );
				}
			};

			var service = new DocumentSaveService( delayedFs );
			var result = service.Save( session );

			Assert( result.IsSuccess, "Save of original revision succeeded." );
			Assert( session.IsDirty, "Session must still be dirty because an edit occurred during save!" );
			Assert( session.Revision > session.SavedRevision, "Current revision must exceed SavedRevision." );
		} );

		registry.Add( "save", "unsupported_fbx_authoring_refused", () =>
		{
			string dir = Path.Combine( settings.WorkDirectory, "fbx_unsupported" );
			Directory.CreateDirectory( dir );
			string fbxPath = Path.Combine( dir, "model.fbx" );
			File.WriteAllText( fbxPath, "Kaydara FBX Binary  \0" );

			var fakeResult = new FbxImport.Result
			{
				Skeleton = new Skeleton()
			};

			var session = DocumentSession.CreateFbx( fbxPath, fakeResult );
			var service = new DocumentSaveService();

			var result = service.Save( session );

			Assert( result.Status == SaveStatus.Unsupported,
				$"Saving FBX session directly should be Unsupported, got {result.Status}" );
			Assert( File.ReadAllText( fbxPath ) == "Kaydara FBX Binary  \0", "Source FBX must not be modified." );
		} );

		registry.Add( "save", "staged_recovery_can_be_parsed", () =>
		{
			string recoveryDir = Path.Combine( settings.WorkDirectory, "recovery_store" );
			Directory.CreateDirectory( recoveryDir );

			string docPath = Path.Combine( settings.WorkDirectory, "original_active.effigy" );
			File.WriteAllText( docPath, "effigy 1\norigin 0,0,0\n" );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 42f } } );
			studio.Rebuild();

			var session = DocumentSession.CreateEffigy( docPath, studio );
			session.BumpRevision();

			var service = new DocumentSaveService();
			var metadata = service.CreateRecoverySnapshot( session, recoveryDir );

			Assert( metadata.Revision == session.Revision, "Metadata revision must match session revision." );
			Assert( File.ReadAllText( docPath ) == "effigy 1\norigin 0,0,0\n",
				"Recovery snapshot must never touch or overwrite user's active file." );

			var recoveries = service.ListRecoverySnapshots( recoveryDir );
			Assert( recoveries.Count >= 1, "Should list at least 1 recovery snapshot." );
			Assert( recoveries.Any( r => r.SnapshotId == metadata.SnapshotId ), "Listed snapshots must contain our snapshot." );

			var (readMeta, payload) = service.ReadRecoverySnapshot( recoveryDir, metadata.SnapshotId );
			Assert( readMeta.DocumentId == session.DocumentId, "Document ID matches in recovery snapshot." );
			var reparsedStudio = StudioDocument.Read( payload );
			reparsedStudio.Rebuild();
			var prim = (PrimitiveFeature)reparsedStudio.Features[0];
			Assert( Math.Abs( prim.SizeX.Value - 42f ) < 0.001f, "Recovery payload reconstructed the exact state." );
		} );

		registry.Add( "save", "readonly_directory_fails_safely", () =>
		{
			string readOnlyDir = Path.Combine( settings.WorkDirectory, "readonly_dir" );
			Directory.CreateDirectory( readOnlyDir );
			string targetFile = Path.Combine( readOnlyDir, "model.effigy" );

			var mockFs = new FaultInjectingFileSystem( PhysicalFileSystem.Instance )
			{
				FailOpenWrite = true
			};

			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( targetFile, studio );

			var service = new DocumentSaveService( mockFs );
			var result = service.Save( session );

			Assert( result.Status == SaveStatus.IoError, "Save to read-only location should fail with IoError." );
			Assert( !File.Exists( targetFile ), "Target file should not exist." );
		} );
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new InvalidOperationException( message );
	}
}

internal sealed class FaultInjectingFileSystem : IFileSystem
{
	readonly IFileSystem _inner;

	public bool FailOpenWrite { get; set; }
	public bool FailReplace { get; set; }
	public Action? PreReplaceCallback { get; set; }

	public FaultInjectingFileSystem( IFileSystem inner )
	{
		_inner = inner;
	}

	public bool FileExists( string path ) => _inner.FileExists( path );
	public bool DirectoryExists( string path ) => _inner.DirectoryExists( path );
	public void CreateDirectory( string path ) => _inner.CreateDirectory( path );
	public void WriteAllText( string path, string content ) => _inner.WriteAllText( path, content );
	public string ReadAllText( string path ) => _inner.ReadAllText( path );
	public void DeleteFile( string path ) => _inner.DeleteFile( path );
	public void MoveFile( string sourceFile, string destinationFile, bool overwrite = false )
	{
		PreReplaceCallback?.Invoke();
		if ( FailReplace ) throw new IOException( "Simulated move/replace failure." );
		_inner.MoveFile( sourceFile, destinationFile, overwrite );
	}

	public void ReplaceFile( string sourceFile, string destinationFile, string? backupFile = null )
	{
		PreReplaceCallback?.Invoke();
		if ( FailReplace ) throw new IOException( "Simulated replace failure." );
		_inner.ReplaceFile( sourceFile, destinationFile, backupFile );
	}

	public Stream OpenWrite( string path )
	{
		if ( FailOpenWrite ) throw new UnauthorizedAccessException( "Simulated permission denied / read-only directory." );
		return _inner.OpenWrite( path );
	}

	public IEnumerable<string> EnumerateFiles( string path, string searchPattern ) =>
		_inner.EnumerateFiles( path, searchPattern );
}
