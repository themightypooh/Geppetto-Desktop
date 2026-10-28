using System;
using System.Collections.Generic;
using System.IO;

namespace Effigy;

/// <summary>
/// Levels of detail for an export: the decimated copies of a model, written beside it, and the
/// switch distances the .vmdl's LODGroupList uses. One place for the editor's File ▸ Export with
/// LODs and the script's <c>export lods=</c>, static and skinned alike.
///
/// WHAT EACH LEVEL KEEPS. <see cref="Decimate"/> carries the skin weights (blended along each
/// collapse), the shape keys and the vertex colours, holds open borders, material borders and UV
/// seams (<see cref="Decimate.Options.PreserveUVSeams"/>), so a level is the same model with fewer
/// triangles: the same bones pull it, the same morphs move it and its texture does not smear at the
/// islands' edges.
///
/// TRIANGULATED THE BAKE'S WAY. A level is triangles already, but the full-detail mesh is not, and
/// static levels go out through OBJ with <c>triangulate: true</c> (NormalBake.FaceTriangles) like the
/// editor's main static compile, so every level is shaded across the triangles its normal map was
/// baked on. Skinned levels go through DMX, which triangulates that way by default.
/// </summary>
public static class LodExport
{
	/// <summary>The editor's pair: half and a quarter of the triangles.</summary>
	public static readonly float[] DefaultRatios = { 0.5f, 0.25f };

	/// <summary>
	/// Lane H: the full LOD0–LOD3 ladder. Each level halves the triangles of the one before
	/// it, so a distant model costs an eighth of the original. <see cref="BuildLevels"/> picks
	/// the first N of these; the old two-level DefaultRatios stay untouched for existing callers.
	/// </summary>
	public static readonly float[] DefaultRatiosLod3 = { 0.5f, 0.25f, 0.125f };

	/// <summary>Below this many triangles a model's levels save next to nothing.</summary>
	public const int MinimumTriangles = 64;

	/// <summary>
	/// Where level <paramref name="level"/> (1 = the first decimated copy) takes over, in units:
	/// 6, then 15, then doubling, times the model's size - far enough that the coarser mesh is a few
	/// pixels' difference. The editor's distances, unchanged.
	/// </summary>
	public static float DefaultDistance( float diagonal, int level )
	{
		var size = MathF.Max( diagonal, 1f );
		return size * (level <= 1 ? 6f : level == 2 ? 15f : 15f * MathF.Pow( 2f, level - 2 ));
	}

	/// <summary>One written level.</summary>
	public sealed class Level
	{
		/// <summary>1 for the first decimated copy.</summary>
		public int Index;
		public float Ratio;
		public PolyMesh Mesh;
		public int Triangles;

		/// <summary>False when the decimation ran out of legal collapses before the ratio: the level
		/// is as small as it can get without breaking the mesh, and bigger than asked.</summary>
		public bool ReachedTarget;
		public float SwitchDistance;

		/// <summary>Retopo wave 3: surface deviation of this level from the source mesh it was
		/// reduced from, measured by projecting level vertices onto the source (see
		/// <see cref="MeasureError"/>). NaN when not measured (source too large to scan).</summary>
		public float MaxError = float.NaN;
		public float MeanError = float.NaN;

		/// <summary>Retopo wave 3: worst normal deviation in degrees between a level face and
		/// the source face under its centroid. NaN when not measured.</summary>
		public float MaxNormalDeviation = float.NaN;

		public bool HasError => float.IsFinite( MaxError );

		/// <summary>The files written for this level, on disk.</summary>
		public List<string> Files = new();

		/// <summary>The mesh file the .vmdl names, under the asset folder.</summary>
		public string AssetPath;
	}

	/// <summary>The decimated copy at <paramref name="ratio"/> of the triangles, seams held.</summary>
	public static Decimate.Result Reduce( PolyMesh mesh, float ratio ) =>
		Decimate.Run( mesh, new Decimate.Options { Ratio = ratio } );

	/// <summary>
	/// Retopo wave 3: the distance at which a surface error subtends
	/// <paramref name="targetPixels"/> on screen, for a camera with the given vertical FOV and
	/// screen height. World-per-pixel at distance d is 2*d*tan(fov/2)/screenHeight, so
	/// d = error * screenHeight / (2 * targetPixels * tan(fov/2)). A level whose max error
	/// is zero can be used at distance zero — it is lossless at any range the formula can
	/// see. This replaces the size x 6/15 heuristic only where an error was actually
	/// measured; <see cref="DefaultDistance"/> stays the fallback everywhere else, so
	/// existing .vmdl distances do not silently change.
	/// </summary>
	public static float PixelDistance( float maxError, float targetPixels = 2f, float fovDeg = 50f, float screenHeight = 1080f )
	{
		if ( maxError <= 0f || targetPixels <= 0f || screenHeight <= 0f )
			return 0f;
		var halfFov = fovDeg * MathF.PI / 360f;
		var tan = MathF.Tan( halfFov );
		if ( tan <= 0f )
			return 0f;
		return maxError * screenHeight / (2f * targetPixels * tan);
	}

	/// <summary>
	/// Retopo wave 3: per-level error against the source mesh. Level vertices are projected
	/// onto the source with <see cref="MeshBVH"/> (max and mean distance); each level face's
	/// normal is compared with the source face under its centroid (max deviation, degrees).
	/// Skipped (NaN fields) when the source has more than 500,000 vertices, where a full
	/// scan stops being a log line and starts being a job.
	/// </summary>
	public static void MeasureError( PolyMesh source, Level level )
	{
		if ( source is null || level?.Mesh is null || source.FaceCount == 0 || level.Mesh.FaceCount == 0
			|| source.Positions.Count > 500_000 )
			return;
		var bvh = MeshBVH.Build( source );
		var maxDistance = MathF.Max( source.BoundsDiagonal, 1e-6f ) * 2f + 1f;
		double sum = 0;
		var count = 0;
		var max = 0f;
		foreach ( var pos in level.Mesh.Positions )
		{
			var hit = bvh.NearestSurface( source, pos, maxDistance );
			if ( hit is not { } h ) continue;
			sum += h.Distance;
			if ( h.Distance > max ) max = h.Distance;
			count++;
		}
		if ( count == 0 ) return;
		level.MaxError = max;
		level.MeanError = (float)(sum / count);
		var maxNormal = 0f;
		foreach ( var face in level.Mesh.Faces )
		{
			var centroid = Vec3.Zero;
			foreach ( var vi in face.Indices ) centroid += level.Mesh.Positions[vi];
			centroid /= Math.Max( 1, face.Count );
			var hit = bvh.NearestSurface( source, centroid, maxDistance );
			if ( hit is not { } h || h.FaceIndex < 0 || h.FaceIndex >= source.Faces.Count ) continue;
			var dot = Math.Clamp( Vec3.Dot( level.Mesh.FaceNormal( face ), source.FaceNormal( source.Faces[h.FaceIndex] ) ), -1f, 1f );
			var angle = MathF.Acos( dot ) * 180f / MathF.PI;
			if ( angle > maxNormal ) maxNormal = angle;
		}
		level.MaxNormalDeviation = maxNormal;
	}

	/// <summary>
	/// The levels as meshes, without writing anything. <paramref name="distances"/> may be null
	/// (the defaults) or one rising distance per ratio.
	/// </summary>
	/// <summary>
	/// Lane H: LOD0 plus <paramref name="lodLevels"/> decimated levels, in order, with the
	/// standard halving ratios and the standard switch distances. lodLevels is 0–3: 0 means
	/// the full model alone, 3 means the full LOD0–LOD3 ladder. Anything else throws rather
	/// than silently building a ladder nobody asked for.
	/// </summary>
	public static List<Level> BuildLevels( PolyMesh mesh, int lodLevels, IReadOnlyList<float> distances = null )
	{
		if ( lodLevels < 0 || lodLevels > 3 )
			throw new ArgumentException( "lodLevels must be 0, 1, 2 or 3", nameof( lodLevels ) );
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		var ratios = new float[lodLevels];
		for ( var i = 0; i < lodLevels; i++ )
			ratios[i] = DefaultRatiosLod3[i];
		// Build only returns the DECIMATED levels, so LOD0 — the full mesh, at distance 0 —
		// is prepended here. Without it a "LOD0–LOD3 ladder" would quietly start at LOD1.
		// Triangles, not faces: a quad face is two triangles, and the decimated levels
		// count triangles, so counting LOD0's faces would make LOD1 look unchanged on a
		// quad mesh (384 quads ARE 768 triangles; half of that is 384 triangles).
		var lod0Triangles = 0;
		foreach ( var face in mesh.Faces )
			lod0Triangles += Math.Max( 0, face.Count - 2 );
		var levels = new List<Level>
		{
			new() { Index = 0, Ratio = 1f, Mesh = mesh, Triangles = lod0Triangles,
				ReachedTarget = true, SwitchDistance = 0f },
		};
		levels.AddRange( Build( mesh, ratios, distances ) );
		return levels;
	}

	public static List<Level> Build( PolyMesh mesh, IReadOnlyList<float> ratios, IReadOnlyList<float> distances = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		ratios ??= DefaultRatios;

		if ( distances is not null && distances.Count != ratios.Count )
			throw new ArgumentException( $"{distances.Count} distance(s) for {ratios.Count} level(s): give one per level", nameof( distances ) );

		var levels = new List<Level>();

		for ( var i = 0; i < ratios.Count; i++ )
		{
			var result = Reduce( mesh, ratios[i] );
			var level = new Level
			{
				Index = i + 1,
				Ratio = ratios[i],
				Mesh = result.Mesh,
				Triangles = result.ToTriangles,
				ReachedTarget = result.ReachedTarget,
				SwitchDistance = distances?[i] ?? DefaultDistance( mesh.BoundsDiagonal, i + 1 ),
			};
			MeasureError( mesh, level );
			if ( distances is null && level.HasError && level.MaxError > 0f )
				level.SwitchDistance = PixelDistance( level.MaxError );
			levels.Add( level );
		}

		return levels;
	}

	/// <summary>
	/// Retopo wave 3: quad LODs. The same ladder as <see cref="BuildLevels"/>, but each level
	/// is a <see cref="QuadRemesh"/> at a descending face target (source faces x ratio, at
	/// least 16) instead of a triangle decimation, so a distant level still subdivides and
	/// deforms like a quad mesh. Attributes are transferred onto each level with
	/// <see cref="AttributeTransfer"/> unless <paramref name="transfer"/> is false — without
	/// it a quad LOD would lose its rig and texture exactly the way a quad remesh used to.
	/// Switch distances use the measured pixel distance where an error was measured, else
	/// the standard heuristic.
	/// </summary>
	public static List<Level> BuildQuadLevels( PolyMesh mesh, int lodLevels, IReadOnlyList<float> distances = null, bool transfer = true )
	{
		if ( lodLevels < 0 || lodLevels > 3 )
			throw new ArgumentException( "lodLevels must be 0, 1, 2 or 3", nameof( lodLevels ) );
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		var lod0Triangles = 0;
		foreach ( var face in mesh.Faces )
			lod0Triangles += Math.Max( 0, face.Count - 2 );
		var levels = new List<Level>
		{
			new() { Index = 0, Ratio = 1f, Mesh = mesh, Triangles = lod0Triangles,
				ReachedTarget = true, SwitchDistance = 0f },
		};
		for ( var i = 0; i < lodLevels; i++ )
		{
			var ratio = DefaultRatiosLod3[i];
			var targetFaces = Math.Max( 16, (int)MathF.Round( mesh.FaceCount * ratio ) );
			var result = QuadRemesh.Run( mesh, new QuadRemesh.Options { TargetFaces = targetFaces } );
			var levelMesh = result.Mesh;
			var reached = levelMesh.FaceCount > 0;
			if ( !reached )
				levelMesh = mesh;
			else if ( transfer )
				AttributeTransfer.Transfer( mesh, levelMesh );
			var level = new Level
			{
				Index = i + 1,
				Ratio = ratio,
				Mesh = levelMesh,
				Triangles = Decimate.TriangleCount( levelMesh ),
				ReachedTarget = reached,
				SwitchDistance = distances?[i] ?? DefaultDistance( mesh.BoundsDiagonal, i + 1 ),
			};
			MeasureError( mesh, level );
			if ( distances is null && level.HasError && level.MaxError > 0f )
				level.SwitchDistance = PixelDistance( level.MaxError );
			levels.Add( level );
		}
		return levels;
	}

	/// <summary>
	/// Static levels: <c>{name}_lod1.obj</c>, <c>{name}_lod2.obj</c>… in <paramref name="dir"/>,
	/// triangulated the bake's way. <paramref name="assetFolder"/> is the folder's asset path
	/// (e.g. models/effigy), for the .vmdl.
	/// </summary>
	public static List<Level> WriteStatic( PolyMesh mesh, string dir, string assetFolder, string name,
		Func<int, string> materialName, IReadOnlyList<float> ratios = null, IReadOnlyList<float> distances = null, string materialLibrary = "", bool mergeMaterials = false )
	{
		var levels = Build( mesh, ratios, distances );

		foreach ( var level in levels )
		{
			var lodName = $"{name}_lod{level.Index}";

			// Wave 3 lodmerge: distant levels use slot 0 only (one draw-call
			// material). Textures are NOT atlased here; the caller logs that.
			if ( mergeMaterials )
				foreach ( var face in level.Mesh.Faces )
					face.Material = 0;

			var path = Path.Combine( dir, lodName + ".obj" );
			ObjWriter.WriteFile( level.Mesh, path, lodName, materialName: materialName, triangulate: true, materialLibrary: materialLibrary );
			level.Files.Add( path );
			level.AssetPath = $"{assetFolder}/{lodName}.obj";
		}

		return levels;
	}

	/// <summary>
	/// Skinned levels: <c>{name}_lod1.dmx</c>… (and an .smd beside each when <paramref name="smd"/>),
	/// every one bound to the same <paramref name="skeleton"/>, its weights the decimation's blend of
	/// the full mesh's and its shape keys carried along. The mesh must carry its skin
	/// (<see cref="PolyMesh.Skin"/>); a level without one would compile as a rigid model.
	/// <paramref name="wrinkles"/> writes each level's own wrinkle values (WrinkleMaps) with its keys.
	/// </summary>
	public static List<Level> WriteSkinned( PolyMesh mesh, Skeleton skeleton, string dir, string assetFolder, string name,
		Func<int, string> materialName, IReadOnlyList<float> ratios = null, IReadOnlyList<float> distances = null, bool smd = true,
		bool wrinkles = false, bool mergeMaterials = false )
	{
		if ( skeleton is null || skeleton.Count == 0 )
			throw new InvalidOperationException( "skinned levels of detail need the skeleton the model is bound to" );

		if ( !mesh.IsRigged )
			throw new InvalidOperationException( "skinned levels of detail need the mesh's skin weights: bind it before exporting" );

		var levels = Build( mesh, ratios, distances );

		foreach ( var level in levels )
		{
			var lodName = $"{name}_lod{level.Index}";

			if ( mergeMaterials )
				foreach ( var face in level.Mesh.Faces )
					face.Material = 0;

			var dmxPath = Path.Combine( dir, lodName + ".dmx" );
			DmxWriter.WriteFile( level.Mesh, dmxPath, skeleton, materialName: materialName, modelName: lodName,
				wrinkles: wrinkles && level.Mesh.HasMorphs ? WrinkleMaps.All( level.Mesh ) : null );
			level.Files.Add( dmxPath );

			if ( smd )
			{
				var smdPath = Path.Combine( dir, lodName + ".smd" );
				SmdWriter.WriteFile( level.Mesh, smdPath, skeleton, materialName: materialName );
				level.Files.Add( smdPath );
			}

			level.AssetPath = $"{assetFolder}/{lodName}.dmx";
		}

		return levels;
	}

	/// <summary>The .vmdl's mesh list and switch distances: the full model first, at zero.</summary>
	public static (List<string> Files, List<float> Distances) VmdlLists( string fullAssetPath, IEnumerable<Level> levels )
	{
		var files = new List<string> { fullAssetPath };
		var distances = new List<float> { 0f };

		foreach ( var level in levels )
		{
			files.Add( level.AssetPath );
			distances.Add( level.SwitchDistance );
		}

		return (files, distances);
	}

	/// <summary>A line saying what each level came to, for the log: "1840 tris from 210, 920 tris from 525".</summary>
	public static string Describe( IEnumerable<Level> levels )
	{
		var parts = new List<string>();

		foreach ( var level in levels )
		{
			var note = level.ReachedTarget ? "" : " (as far as it would go)";
			var error = level.HasError ? $", max error {level.MaxError:0.###}, normal {level.MaxNormalDeviation:0.#} deg" : "";
			parts.Add( $"{level.Triangles} tris from {level.SwitchDistance.ToString( "0.#", System.Globalization.CultureInfo.InvariantCulture )}{note}{error}" );
		}

		return string.Join( ", ", parts );
	}
}
