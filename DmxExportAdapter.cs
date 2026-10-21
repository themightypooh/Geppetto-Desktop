#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Adapter for optional Source 2 DMX model and skeletal animation clip export.
/// Uses kernel DmxWriter (for static/rigged mesh) and DmxAnimWriter (for skeletal clips).
/// Samples animated poses via RigAnimFile or MarionettePlayer.
/// Non-destructive: does not cull or clamp bones to the 220 viewport limit.
/// Enforces atomic write staging and overwrite policy.
/// </summary>
public static class DmxExportAdapter
{
	/// <summary>
	/// Exports mesh geometry and optional skeleton to Source 2 DMX format using kernel DmxWriter.
	/// If skeleton is null or has 0 bones, Skeleton.SingleRoot() is used as required by DMX specification.
	/// </summary>
	public static string ExportMesh(
		PolyMesh mesh,
		string destinationPath,
		Skeleton? skeleton = null,
		float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees,
		Func<int, string>? materialResolver = null,
		string? modelName = null,
		DmxWriter.AuthoredNormals? authoredNormals = null,
		bool triangulate = true,
		bool tangents = true,
		IReadOnlyDictionary<string, float[]>? wrinkles = null,
		OverwritePolicy overwritePolicy = OverwritePolicy.FailIfExists )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		if ( string.IsNullOrWhiteSpace( destinationPath ) )
			throw new ArgumentException( "Destination path cannot be empty.", nameof( destinationPath ) );

		string fullDestPath = Path.GetFullPath( destinationPath );
		string destDir = Path.GetDirectoryName( fullDestPath )!;
		if ( !Directory.Exists( destDir ) )
			Directory.CreateDirectory( destDir );

		if ( File.Exists( fullDestPath ) && overwritePolicy == OverwritePolicy.FailIfExists )
			throw new IOException( $"Refusing to overwrite existing destination file: '{fullDestPath}'" );

		string tempPath = Path.Combine( destDir, $".dmx_export_{Guid.NewGuid():N}.tmp" );
		try
		{
			skeleton ??= Skeleton.SingleRoot();
			if ( skeleton.Count == 0 )
				skeleton = Skeleton.SingleRoot();

			modelName ??= Path.GetFileNameWithoutExtension( fullDestPath );
			materialResolver ??= slot => $"material_{slot}";

			DmxWriter.WriteFile(
				mesh,
				tempPath,
				skeleton: skeleton,
				smoothingAngleDegrees: smoothingAngleDegrees,
				materialName: materialResolver,
				modelName: modelName,
				authoredNormals: authoredNormals,
				triangulate: triangulate,
				tangents: tangents,
				wrinkles: wrinkles );

			File.Move( tempPath, fullDestPath, overwrite: overwritePolicy == OverwritePolicy.Overwrite );
			return fullDestPath;
		}
		finally
		{
			if ( File.Exists( tempPath ) )
			{
				try { File.Delete( tempPath ); } catch { }
			}
		}
	}

	/// <summary>
	/// Exports an evaluated AnimClip to Source 2 DMX animation format using kernel DmxAnimWriter.
	/// </summary>
	public static string ExportAnimation(
		string destinationPath,
		Skeleton skeleton,
		AnimClip clip,
		string? modelName = null,
		OverwritePolicy overwritePolicy = OverwritePolicy.FailIfExists )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		ArgumentNullException.ThrowIfNull( clip );
		if ( string.IsNullOrWhiteSpace( destinationPath ) )
			throw new ArgumentException( "Destination path cannot be empty.", nameof( destinationPath ) );

		string fullDestPath = Path.GetFullPath( destinationPath );
		string destDir = Path.GetDirectoryName( fullDestPath )!;
		if ( !Directory.Exists( destDir ) )
			Directory.CreateDirectory( destDir );

		if ( File.Exists( fullDestPath ) && overwritePolicy == OverwritePolicy.FailIfExists )
			throw new IOException( $"Refusing to overwrite existing destination file: '{fullDestPath}'" );

		string tempPath = Path.Combine( destDir, $".dmx_anim_export_{Guid.NewGuid():N}.tmp" );
		try
		{
			modelName ??= Path.GetFileNameWithoutExtension( fullDestPath );

			DmxAnimWriter.WriteFile(
				tempPath,
				skeleton,
				clip,
				modelName: modelName );

			File.Move( tempPath, fullDestPath, overwrite: overwritePolicy == OverwritePolicy.Overwrite );
			return fullDestPath;
		}
		finally
		{
			if ( File.Exists( tempPath ) )
			{
				try { File.Delete( tempPath ); } catch { }
			}
		}
	}

	/// <summary>
	/// Exports a native RigAnimFile by sampling poses through RigAnimFile.ToClip.
	/// </summary>
	public static string ExportAnimation(
		string destinationPath,
		Skeleton skeleton,
		RigAnimFile clipFile,
		string? modelName = null,
		string clipName = "anim",
		bool looping = false,
		OverwritePolicy overwritePolicy = OverwritePolicy.FailIfExists )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		ArgumentNullException.ThrowIfNull( clipFile );

		var evaluatedClip = clipFile.ToClip( skeleton, clipName, looping );
		return ExportAnimation( destinationPath, skeleton, evaluatedClip, modelName, overwritePolicy );
	}

	/// <summary>
	/// Samples poses from MarionettePlayer across a time range and exports to DMX animation.
	/// </summary>
	public static string ExportAnimation(
		string destinationPath,
		MarionettePlayer player,
		float durationSeconds,
		float frameRate = 30f,
		string? modelName = null,
		string clipName = "anim",
		bool looping = false,
		OverwritePolicy overwritePolicy = OverwritePolicy.FailIfExists )
	{
		ArgumentNullException.ThrowIfNull( player );
		var sampledClip = SampleClipFromPlayer( player, durationSeconds, frameRate, clipName, looping );
		return ExportAnimation( destinationPath, player.Rig, sampledClip, modelName, overwritePolicy );
	}

	/// <summary>
	/// Evaluates and samples frames from a MarionettePlayer into an AnimClip.
	/// </summary>
	public static AnimClip SampleClipFromPlayer(
		MarionettePlayer player,
		float durationSeconds,
		float frameRate = 30f,
		string clipName = "anim",
		bool looping = false )
	{
		ArgumentNullException.ThrowIfNull( player );
		if ( durationSeconds < 0f || !float.IsFinite( durationSeconds ) )
			throw new ArgumentOutOfRangeException( nameof( durationSeconds ), "Duration must be positive and finite." );
		if ( frameRate <= 0f || !float.IsFinite( frameRate ) )
			throw new ArgumentOutOfRangeException( nameof( frameRate ), "Frame rate must be positive and finite." );

		int frameCount = Math.Max( 1, (int)MathF.Round( durationSeconds * frameRate ) );
		var clip = new AnimClip { Name = clipName, FrameRate = frameRate, Looping = looping };

		float originalTime = player.Time;
		bool originalPlaying = player.Playing;

		try
		{
			player.Playing = false;
			for ( int f = 0; f <= frameCount; f++ )
			{
				player.Time = f / frameRate;
				player.Step( 0 );

				var world = player.WorldPose.ToArray();
				var local = RigAnimFile.LocalSpace( player.Rig, world );
				clip.AddFrame( local );
			}
		}
		finally
		{
			player.Time = originalTime;
			player.Playing = originalPlaying;
		}

		return clip;
	}

	/// <summary>
	/// Exports model from DocumentSession to Source 2 DMX format.
	/// </summary>
	public static ExportResult ExportModel(
		DocumentSession session,
		string destinationPath,
		OverwritePolicy overwritePolicy = OverwritePolicy.FailIfExists,
		PoseMode poseMode = PoseMode.BindPose,
		float? playheadTime = null,
		string? modelName = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( string.IsNullOrWhiteSpace( destinationPath ) )
			return new ExportResult( false, destinationPath, null, null, "Destination path cannot be empty.", session.Revision );

		string fullDestPath = Path.GetFullPath( destinationPath );
		if ( File.Exists( fullDestPath ) && overwritePolicy == OverwritePolicy.FailIfExists )
		{
			return new ExportResult(
				false,
				fullDestPath,
				null,
				null,
				$"Refusing to overwrite existing destination file: '{fullDestPath}'",
				session.Revision );
		}

		try
		{
			var (mesh, skeleton) = ExtractSessionModel( session, poseMode, playheadTime );
			if ( mesh == null || mesh.VertexCount == 0 )
			{
				return new ExportResult(
					false,
					fullDestPath,
					null,
					null,
					"Session contains no mesh geometry to export.",
					session.Revision );
			}

			modelName ??= Path.GetFileNameWithoutExtension( fullDestPath );
			Func<int, string> materialResolver = slot => ResolveSessionMaterial( session, slot );

			string exportedPath = ExportMesh(
				mesh,
				fullDestPath,
				skeleton,
				materialResolver: materialResolver,
				modelName: modelName,
				overwritePolicy: overwritePolicy );

			var report = new ExportCapabilityReport
			{
				CanExport = true,
				PreservesGeometry = true,
				PreservesRig = skeleton is { Count: > 0 },
				PreservesSkinning = mesh.Skin is { Count: > 0 } && skeleton is { Count: > 0 },
				PreservesTangents = true,
				PreservesTextures = true,
				PreservesAnimation = false,
				UnitsAndAxes = "Source 2 DMX format model 22: inches, Z-up"
			};

			return new ExportResult(
				true,
				exportedPath,
				new[] { exportedPath },
				report,
				null,
				session.Revision );
		}
		catch ( Exception ex )
		{
			return new ExportResult(
				false,
				fullDestPath,
				null,
				null,
				ex.Message,
				session.Revision );
		}
	}

	/// <summary>
	/// Exports skeletal animation from DocumentSession to Source 2 DMX animation format.
	/// </summary>
	public static ExportResult ExportAnimation(
		DocumentSession session,
		string destinationPath,
		OverwritePolicy overwritePolicy = OverwritePolicy.FailIfExists,
		string? modelName = null,
		string clipName = "anim",
		bool looping = false )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( string.IsNullOrWhiteSpace( destinationPath ) )
			return new ExportResult( false, destinationPath, null, null, "Destination path cannot be empty.", session.Revision );

		string fullDestPath = Path.GetFullPath( destinationPath );
		if ( File.Exists( fullDestPath ) && overwritePolicy == OverwritePolicy.FailIfExists )
		{
			return new ExportResult(
				false,
				fullDestPath,
				null,
				null,
				$"Refusing to overwrite existing destination file: '{fullDestPath}'",
				session.Revision );
		}

		Skeleton? skeleton = session.Studio?.Rig ?? session.FbxSource?.Skeleton;
		if ( skeleton == null || skeleton.Count == 0 )
		{
			return new ExportResult(
				false,
				fullDestPath,
				null,
				null,
				"Cannot export DMX animation: session contains no skeleton.",
				session.Revision );
		}

		RigAnimFile? clip = session.Clip;
		if ( clip == null && !string.IsNullOrWhiteSpace( session.ClipPath ) && File.Exists( session.ClipPath ) )
		{
			try
			{
				clip = RigAnimFile.ReadFile( session.ClipPath );
			}
			catch ( Exception ex )
			{
				return new ExportResult(
					false,
					fullDestPath,
					null,
					null,
					$"Failed to parse animation clip from '{session.ClipPath}': {ex.Message}",
					session.Revision );
			}
		}

		if ( clip == null )
		{
			return new ExportResult(
				false,
				fullDestPath,
				null,
				null,
				"Cannot export DMX animation: session contains no animation clip.",
				session.Revision );
		}

		try
		{
			modelName ??= Path.GetFileNameWithoutExtension( fullDestPath );
			string exportedPath = ExportAnimation(
				fullDestPath,
				skeleton,
				clip,
				modelName,
				clipName,
				looping,
				overwritePolicy );

			var report = new ExportCapabilityReport
			{
				CanExport = true,
				PreservesGeometry = false,
				PreservesRig = true,
				PreservesSkinning = false,
				PreservesTangents = false,
				PreservesTextures = false,
				PreservesAnimation = true,
				UnitsAndAxes = "Source 2 DMX format model 22: animation clip channels"
			};

			return new ExportResult(
				true,
				exportedPath,
				new[] { exportedPath },
				report,
				null,
				session.Revision );
		}
		catch ( Exception ex )
		{
			return new ExportResult(
				false,
				fullDestPath,
				null,
				null,
				ex.Message,
				session.Revision );
		}
	}

	static (PolyMesh? Mesh, Skeleton? Skeleton) ExtractSessionModel( DocumentSession session, PoseMode poseMode, float? playheadTime )
	{
		if ( session.Studio != null )
		{
			var studio = session.Studio;
			var (merged, ranges) = studio.ToMeshWithBodies( b => b.Visible );
			if ( merged == null || merged.VertexCount == 0 )
				return (null, null);

			var rig = studio.Rig;
			bool hasRig = rig is { Count: > 0 };

			if ( hasRig && rig != null && studio.BodyBoneMap.Count > 0 )
			{
				var weights = SkinBinder.BindBodies( merged, ranges, studio.BodyBoneMap, rig );
				weights = SkinBinder.SmoothWeights( merged, weights );
				merged.Skin = weights;
			}

			return (merged, hasRig ? rig : null);
		}
		else if ( session.FbxSource != null )
		{
			var fbx = session.FbxSource;
			var rig = fbx.Skeleton;
			bool hasRig = rig is { Count: > 0 };

			if ( fbx.Result.Pieces.Count == 0 )
				return (null, null);

			var piece = fbx.Result.Pieces[0];
			var mesh = piece.Mesh.Clone();
			var rawWeights = fbx.Result.Weights.Count > 0 ? fbx.Result.Weights[0] : null;
			if ( mesh.Skin == null && rawWeights != null && hasRig && rig != null )
			{
				mesh.Skin = FbxImport.Resolve( rawWeights, rig, null, out _ );
			}
			return (mesh, hasRig ? rig : null);
		}

		return (null, null);
	}

	static string ResolveSessionMaterial( DocumentSession session, int slot )
	{
		if ( session.Studio != null )
		{
			string name = session.Studio.NameForSlot( slot );
			if ( !string.IsNullOrWhiteSpace( name ) ) return name;
		}
		else if ( session.FbxSource?.Result?.MaterialSlots != null &&
			session.FbxSource.Result.MaterialSlots.TryGetValue( slot, out var fbxMat ) &&
			!string.IsNullOrWhiteSpace( fbxMat ) )
		{
			return fbxMat;
		}

		return $"material_{slot}";
	}
}
