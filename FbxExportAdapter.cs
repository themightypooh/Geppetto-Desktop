#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Adapter for rigged binary FBX export using kernel FbxWriter.WriteBinaryFile and FbxBinary.
/// Preserves bind skeleton, bone hierarchies, bone names, and 4-weight vertex skinning.
/// Uses declared 2.54 cm per kernel inch scale factor so 1 kernel inch stays 1 inch on export.
/// Non-destructive: does not cull or clamp bones to the 220 viewport limit.
/// Enforces atomic write staging and overwrite policy.
/// </summary>
public static class FbxExportAdapter
{
	public const double InchesToCentimetres = FbxWriter.InchesToCentimetres; // 2.54

	/// <summary>
	/// Exports a PolyMesh and optional Skeleton to binary FBX format.
	/// If skeleton is null or has 0 bones, exports as a static model with no armature.
	/// </summary>
	public static string ExportBinary(
		PolyMesh mesh,
		string destinationPath,
		Skeleton? skeleton = null,
		Func<int, string>? materialResolver = null,
		string? modelName = null,
		double unitScale = InchesToCentimetres,
		bool triangulate = false,
		bool tangents = true,
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

		string tempPath = Path.Combine( destDir, $".fbx_export_{Guid.NewGuid():N}.tmp" );
		try
		{
			Skeleton? exportSkeleton = (skeleton is { Count: > 0 }) ? skeleton : null;
			modelName ??= Path.GetFileNameWithoutExtension( fullDestPath );
			materialResolver ??= slot => $"material_{slot}";

			FbxWriter.WriteBinaryFile(
				mesh,
				tempPath,
				skeleton: exportSkeleton,
				materialName: materialResolver,
				modelName: modelName,
				unitScale: unitScale,
				triangulate: triangulate,
				tangents: tangents );

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
	/// Exports model from DocumentSession to binary FBX.
	/// </summary>
	public static ExportResult Export(
		DocumentSession session,
		string destinationPath,
		OverwritePolicy overwritePolicy = OverwritePolicy.FailIfExists,
		PoseMode poseMode = PoseMode.BindPose,
		float? playheadTime = null,
		Func<int, string>? materialResolver = null,
		string? modelName = null,
		double unitScale = InchesToCentimetres,
		bool triangulate = false,
		bool tangents = true )
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
			materialResolver ??= slot => ResolveSessionMaterial( session, slot );

			string exportedPath = ExportBinary(
				mesh,
				fullDestPath,
				skeleton,
				materialResolver,
				modelName,
				unitScale,
				triangulate,
				tangents,
				overwritePolicy );

			var report = new ExportCapabilityReport
			{
				CanExport = true,
				PreservesGeometry = true,
				PreservesRig = skeleton is { Count: > 0 },
				PreservesSkinning = mesh.Skin is { Count: > 0 } && skeleton is { Count: > 0 },
				PreservesTangents = tangents,
				PreservesTextures = true,
				PreservesAnimation = false,
				UnitsAndAxes = $"Binary FBX 7.4: {unitScale:F2} cm per inch, Z-up"
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
	/// Overload accepting an ExportRequest.
	/// </summary>
	public static ExportResult Export( ExportRequest request )
	{
		ArgumentNullException.ThrowIfNull( request );
		return Export(
			request.Session,
			request.DestinationPath,
			request.OverwritePolicy,
			request.PoseMode,
			request.PlayheadTime );
	}

	/// <summary>
	/// Prunes skin weights to top maxInfluences (default 4) and renormalizes.
	/// Influences targeting bone indices outside [0, boneCount - 1] or with non-positive weights are discarded.
	/// </summary>
	public static SkinWeights PruneToTop4Weights( SkinWeights skin, int boneCount, int maxInfluences = 4 )
	{
		if ( skin == null ) return new SkinWeights();
		var result = new SkinWeights( skin.Count );

		for ( int v = 0; v < skin.Count; v++ )
		{
			var influences = skin[v];
			if ( influences == null || influences.Length == 0 )
			{
				result[v] = Array.Empty<BoneWeight>();
				continue;
			}

			var valid = new List<BoneWeight>();
			foreach ( var bw in influences )
			{
				if ( bw.Bone >= 0 && bw.Bone < boneCount && bw.Weight > 0f && float.IsFinite( bw.Weight ) )
				{
					valid.Add( bw );
				}
			}

			valid.Sort( ( a, b ) => b.Weight.CompareTo( a.Weight ) );

			if ( valid.Count > maxInfluences )
			{
				valid.RemoveRange( maxInfluences, valid.Count - maxInfluences );
			}

			float total = 0f;
			foreach ( var bw in valid ) total += bw.Weight;

			if ( valid.Count == 0 || total <= 0f )
			{
				result[v] = boneCount > 0 ? new[] { new BoneWeight( 0, 1f ) } : Array.Empty<BoneWeight>();
			}
			else
			{
				var pruned = new BoneWeight[valid.Count];
				for ( int i = 0; i < valid.Count; i++ )
				{
					pruned[i] = new BoneWeight( valid[i].Bone, valid[i].Weight / total );
				}
				result[v] = pruned;
			}
		}

		return result;
	}

	/// <summary>
	/// Returns a clone of the mesh with its SkinWeights pruned to top maxInfluences (default 4).
	/// </summary>
	public static PolyMesh PruneMeshWeights( PolyMesh mesh, int boneCount, int maxInfluences = 4 )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		var clone = mesh.Clone();
		if ( clone.Skin != null )
		{
			clone.Skin = PruneToTop4Weights( clone.Skin, boneCount, maxInfluences );
		}
		return clone;
	}

	/// <summary>
	/// Reads back an FBX file using kernel FbxImport.Read for round-trip validation.
	/// </summary>
	public static FbxImport.Result ValidateFbxRoundTrip( string fbxPath )
	{
		if ( !File.Exists( fbxPath ) )
			throw new FileNotFoundException( $"FBX file not found: {fbxPath}" );

		byte[] bytes = File.ReadAllBytes( fbxPath );
		return FbxImport.Read( bytes );
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

			if ( hasRig && rig != null && poseMode == PoseMode.Posed )
			{
				var bindWorld = RigAnimFile.ModelSpace( rig, rig.Bones.Select( b => b.Local ).ToArray() );
				Xform[]? poseWorld = null;

				if ( session.Clip != null || (!string.IsNullOrWhiteSpace( session.ClipPath ) && File.Exists( session.ClipPath )) )
				{
					var player = new MarionettePlayer( rig, startPlaying: false );
					if ( session.Clip != null ) player.LoadClip( session.Clip );
					else player.LoadClip( session.ClipPath );
					player.Time = playheadTime ?? session.PlayheadTime;
					player.Step( 0 );
					poseWorld = player.WorldPose.ToArray();
				}

				if ( bindWorld != null && poseWorld != null && merged.Skin != null && merged.Skin.Count == merged.VertexCount )
				{
					var deformed = SkinBinder.Deform( merged.Positions, merged.Skin, bindWorld, poseWorld );
					var deformedMesh = new PolyMesh { Positions = new List<Vec3>( deformed ) };
					foreach ( var f in merged.Faces )
						deformedMesh.AddFace( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material );
					if ( merged.VertexColors != null )
						deformedMesh.VertexColors = (Vec4[])merged.VertexColors.Clone();
					deformedMesh.Skin = merged.Skin;
					return (deformedMesh, rig);
				}
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

			if ( fbx.Result.Pieces.Count == 1 )
			{
				var piece = fbx.Result.Pieces[0];
				var mesh = piece.Mesh.Clone();
				var rawWeights = fbx.Result.Weights.Count > 0 ? fbx.Result.Weights[0] : null;
				if ( mesh.Skin == null && rawWeights != null && hasRig && rig != null )
				{
					mesh.Skin = FbxImport.Resolve( rawWeights, rig, null, out _ );
				}
				return (mesh, hasRig ? rig : null);
			}

			// Combine multiple pieces into one PolyMesh
			var combined = new PolyMesh();
			var combinedSkin = new SkinWeights();
			bool hasAnySkin = false;

			for ( int pi = 0; pi < fbx.Result.Pieces.Count; pi++ )
			{
				var piece = fbx.Result.Pieces[pi];
				if ( piece.Mesh == null || piece.Mesh.VertexCount == 0 )
					continue;

				int baseVertex = combined.VertexCount;
				combined.Positions.AddRange( piece.Mesh.Positions );

				var rawWeights = pi < fbx.Result.Weights.Count ? fbx.Result.Weights[pi] : null;
				SkinWeights? pieceSkin = piece.Mesh.Skin;
				if ( pieceSkin == null && rawWeights != null && hasRig && rig != null )
				{
					pieceSkin = FbxImport.Resolve( rawWeights, rig, null, out _ );
				}

				if ( pieceSkin != null && pieceSkin.Count == piece.Mesh.VertexCount )
				{
					hasAnySkin = true;
					for ( int v = 0; v < pieceSkin.Count; v++ )
						combinedSkin.Vertices.Add( (BoneWeight[])pieceSkin[v].Clone() );
				}
				else
				{
					for ( int v = 0; v < piece.Mesh.VertexCount; v++ )
						combinedSkin.Vertices.Add( Array.Empty<BoneWeight>() );
				}

				foreach ( var face in piece.Mesh.Faces )
				{
					int[] remappedIndices = face.Indices.Select( idx => idx + baseVertex ).ToArray();
					combined.AddFace( remappedIndices, (Vec2[])face.UVs.Clone(), face.Material );
				}
			}

			if ( hasAnySkin )
				combined.Skin = combinedSkin;

			return (combined, hasRig ? rig : null);
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
