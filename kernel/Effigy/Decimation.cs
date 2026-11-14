using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Detail-preserving decimation, projection-ready: the ticket-facing operation over
/// <see cref="Decimate"/>.
///
/// WHAT ALREADY EXISTED, and what this file does NOT redo. <see cref="Decimate"/> is the
/// quadric-error decimator: target count or ratio, weld, <c>PreserveBoundary</c>,
/// <c>PreserveUVSeams</c>, <c>FreezeBoundary</c>, flip refusal, link-condition refusal. It is
/// the engine and it stays the engine — nothing here re-implements a collapse. What the
/// godlike decimation ticket asked for on top of it was three things that did not exist:
///
/// 1. PRESETS AND A NAMED OPERATION. Triangle targets 20K / 35K / 75K / 150K / 250K, a
///    target percentage, and one call that runs the decimation with the ticket's defaults
///    (borders frozen, UVs kept) and reports what happened — including the two measurements
///    the ticket's checks demand, which no API returned before: silhouette deviation and
///    the shared pre-export validation.
/// 2. SILHOUETTE DEVIATION, measured, not asserted. The decimator's own
///    <see cref="Decimate.Result.WorstError"/> is a quadric cost in squared model units,
///    not a distance from one surface to the other. <see cref="SilhouetteDeviation"/>
///    samples both surfaces and asks a BVH how far apart they really are, two-sided, so
///    the acceptance bar ("silhouette deviation within the fixture tolerance") is a number
///    a test can hold, not a feeling.
/// 3. LOD NAMES SHARED WITH THE FINISH PIPELINE. <see cref="LodChain"/> already builds
///    the LOD0–LOD3 ladder by vertex ratio; this file only names it — <c>{base}_LOD0</c>
///    through <c>{base}_LOD3</c> — so the Finish ticket (decimate + auto-UV + validation)
///    reuses the same names and the same chain instead of inventing its own. The decimation
///    core is shared with that ticket; see its desc, which says so explicitly.
///
/// VALIDATION IS SHARED WITH MESH DOCTOR, not duplicated. Every decimation result carries
/// the <see cref="MeshDoctor.PreExportCheck"/> decision for the decimated mesh — one shared
/// check, not two. If the decimated mesh is non-manifold, flipped, degenerate or otherwise
/// unexportable, the result says so in <see cref="Result.Validation"/> instead of this file
/// growing a second validator.
///
/// NOT IN THIS FILE. Projection of high-poly detail onto the decimated mesh is
/// <see cref="DetailProjection"/>: per-vertex displacement and high-poly normals sampled
/// off the source, plus the snap that puts the low mesh back on it. Voxel remesh and quad
/// retopology belong to the remesh backend task
/// (integrate-openvdb-remesh-and-instant-meshes-retopo-backends) and to
/// <see cref="QuadRemesh"/>; this file decimates triangles, it does not retopologise.
/// The 5M-to-500K-in-under-30-seconds acceptance is a final-build measurement on a real
/// 5M mesh, not something a unit test can prove — the probes here are small and exact.
/// </summary>
public static class Decimation
{
	/// <summary>Triangle target for the 20K preset.</summary>
	public const int Preset20K = 20000;

	/// <summary>Triangle target for the 35K preset.</summary>
	public const int Preset35K = 35000;

	/// <summary>Triangle target for the 75K preset.</summary>
	public const int Preset75K = 75000;

	/// <summary>Triangle target for the 150K preset.</summary>
	public const int Preset150K = 150000;

	/// <summary>Triangle target for the 250K preset.</summary>
	public const int Preset250K = 250000;

	/// <summary>All five presets, ascending.</summary>
	public static readonly int[] Presets = { Preset20K, Preset35K, Preset75K, Preset150K, Preset250K };

	/// <summary>The display name of an exact preset target ("75K"), or throws for anything else.</summary>
	public static string PresetName( int triangles ) => triangles switch
	{
		Preset20K => "20K",
		Preset35K => "35K",
		Preset75K => "75K",
		Preset150K => "150K",
		Preset250K => "250K",
		_ => throw new ArgumentOutOfRangeException( nameof( triangles ), triangles,
			$"not a decimation preset — presets are {string.Join( ", ", Presets )}" ),
	};

	/// <summary>The preset closest to a triangle count; ties go to the smaller preset.</summary>
	public static int NearestPreset( int triangles )
	{
		var best = Presets[0];
		var bestDiff = Math.Abs( triangles - best );

		for ( var i = 1; i < Presets.Length; i++ )
		{
			var diff = Math.Abs( triangles - Presets[i] );

			if ( diff < bestDiff )
			{
				best = Presets[i];
				bestDiff = diff;
			}
		}

		return best;
	}

	/// <summary>What a decimation did, including the two measurements the ticket's checks need.</summary>
	public sealed class Result
	{
		/// <summary>The decimated mesh. The source mesh is never modified.</summary>
		public PolyMesh Mesh;

		/// <summary>Triangles in, before welding.</summary>
		public int FromTriangles;

		/// <summary>Triangles out.</summary>
		public int ToTriangles;

		/// <summary>Vertices welded before the first collapse.</summary>
		public int Welded;

		/// <summary>The cost of the most expensive collapse taken (squared model units).</summary>
		public float WorstError;

		/// <summary>False when the run ran out of legal collapses before reaching the target.</summary>
		public bool ReachedTarget;

		/// <summary>Two-sided surface deviation between the source and the decimated mesh.</summary>
		public SilhouetteReport Silhouette;

		/// <summary>The shared Mesh Doctor pre-export decision for the decimated mesh.</summary>
		public PreExportDecision Validation;

		/// <summary>Human-readable notes: target missed, validation blocked, and the like.</summary>
		public List<string> Notes = new();
	}

	/// <summary>
	/// Decimate to a triangle count, borders frozen and UVs kept unless told otherwise.
	/// The source mesh is never modified; <see cref="Decimate"/> works on its own copy.
	/// </summary>
	public static Result DecimateTo( PolyMesh mesh, int targetTriangles, bool freezeBorders = true, bool keepUvs = true, string name = null )
	{
		if ( targetTriangles < 4 )
			throw new ArgumentOutOfRangeException( nameof( targetTriangles ), targetTriangles, "a decimation target needs at least 4 triangles" );

		return RunCore( mesh, new Decimate.Options
		{
			TargetTriangles = targetTriangles,
			PreserveBoundary = true,
			PreserveUVSeams = keepUvs,
			FreezeBoundary = freezeBorders,
		}, name );
	}

	/// <summary>
	/// Decimate to a fraction of what came in (0.5 = half the triangles), borders frozen
	/// and UVs kept unless told otherwise.
	/// </summary>
	public static Result DecimateToRatio( PolyMesh mesh, float ratio, bool freezeBorders = true, bool keepUvs = true, string name = null )
	{
		if ( !float.IsFinite( ratio ) || ratio <= 0f || ratio > 1f )
			throw new ArgumentOutOfRangeException( nameof( ratio ), ratio, "a decimation ratio is a fraction in (0, 1]" );

		return RunCore( mesh, new Decimate.Options
		{
			Ratio = ratio,
			PreserveBoundary = true,
			PreserveUVSeams = keepUvs,
			FreezeBoundary = freezeBorders,
		}, name );
	}

	/// <summary>Decimate to one of the five presets (20K / 35K / 75K / 150K / 250K triangles).</summary>
	public static Result DecimateToPreset( PolyMesh mesh, int presetTriangles, bool freezeBorders = true, bool keepUvs = true, string name = null )
	{
		if ( !Presets.Contains( presetTriangles ) )
			throw new ArgumentOutOfRangeException( nameof( presetTriangles ), presetTriangles,
				$"not a decimation preset — presets are {string.Join( ", ", Presets )}" );

		return DecimateTo( mesh, presetTriangles, freezeBorders, keepUvs, name );
	}

	static Result RunCore( PolyMesh mesh, Decimate.Options options, string name )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var run = Decimate.Run( mesh, options );
		var result = new Result
		{
			Mesh = run.Mesh,
			FromTriangles = run.FromTriangles,
			ToTriangles = run.ToTriangles,
			Welded = run.Welded,
			WorstError = run.WorstError,
			ReachedTarget = run.ReachedTarget,
			Silhouette = SilhouetteDeviation( mesh, run.Mesh ),
			Validation = MeshDoctor.PreExportCheck( run.Mesh, name ?? "decimated" ),
		};

		if ( !result.ReachedTarget )
			result.Notes.Add( $"target not reached: {run.ToTriangles} triangles from {run.FromTriangles} — the mesh ran out of legal collapses" );

		if ( result.Validation.Block )
			result.Notes.Add( $"decimated mesh fails pre-export validation: {result.Validation.Summary}" );
		else if ( result.Validation.Warn )
			result.Notes.Add( $"decimated mesh passes with warnings: {result.Validation.Summary}" );

		return result;
	}

	/// <summary>How far apart two surfaces are, measured both ways, on deterministic samples.</summary>
	public sealed class SilhouetteReport
	{
		/// <summary>Samples taken (both directions combined).</summary>
		public int Samples;

		/// <summary>Samples that found no surface within a bounds diagonal (should be zero here).</summary>
		public int Misses;

		/// <summary>The largest sample-to-surface distance, in model units.</summary>
		public float MaxDeviation;

		/// <summary>The mean sample-to-surface distance, in model units.</summary>
		public float MeanDeviation;

		/// <summary>The larger of the two meshes' bounds diagonals, for the relative figures.</summary>
		public float Diagonal;

		/// <summary><see cref="MaxDeviation"/> as a fraction of <see cref="Diagonal"/>.</summary>
		public float RelativeMax => Diagonal > 0f ? MaxDeviation / Diagonal : 0f;

		/// <summary>True when the worst deviation is within an absolute tolerance.</summary>
		public bool WithinTolerance( float tolerance ) => MaxDeviation <= tolerance;

		/// <summary>True when the worst deviation is within a fraction of the bounds diagonal.</summary>
		public bool WithinRelativeTolerance( float fraction ) => RelativeMax <= fraction;
	}

	/// <summary>
	/// Two-sided silhouette deviation: every vertex and face centroid of each mesh, asked
	/// how far it sits from the other mesh's surface through a BVH. Two-sided because a
	/// one-sided check only sees what the low mesh lost, not what it invented — a decimated
	/// mesh can bulge outward past the original and a high-to-low-only sample would never
	/// notice. Samples are capped at 100,000 per mesh per direction by a deterministic
	/// stride, so a 5M-triangle source costs a bounded number of queries, not 5M of them.
	/// </summary>
	public static SilhouetteReport SilhouetteDeviation( PolyMesh high, PolyMesh low )
	{
		if ( high is null )
			throw new ArgumentNullException( nameof( high ) );
		if ( low is null )
			throw new ArgumentNullException( nameof( low ) );
		if ( high.FaceCount == 0 || low.FaceCount == 0 )
			throw new ArgumentException( "silhouette deviation needs two meshes with faces" );

		const int sampleCap = 100000;
		var diagonal = MathF.Max( high.BoundsDiagonal, low.BoundsDiagonal );
		var report = new SilhouetteReport { Diagonal = diagonal };
		var sum = 0.0;

		void Measure( PolyMesh from, PolyMesh to )
		{
			var bvh = MeshBVH.Build( to );
			var samples = new List<Vec3>();
			SamplePoints( from, sampleCap, samples );

			foreach ( var p in samples )
			{
				report.Samples++;
				var hit = bvh.NearestSurface( to, p, diagonal );

				if ( hit is null )
				{
					report.Misses++;
					continue;
				}

				var d = hit.Value.Distance;
				sum += d;

				if ( d > report.MaxDeviation )
					report.MaxDeviation = d;
			}
		}

		Measure( high, low );
		Measure( low, high );

		var hits = report.Samples - report.Misses;
		report.MeanDeviation = hits > 0 ? (float)( sum / hits ) : 0f;
		return report;
	}

	/// <summary>Vertices then face centroids, strided deterministically to the cap.</summary>
	static void SamplePoints( PolyMesh mesh, int cap, List<Vec3> into )
	{
		var total = mesh.VertexCount + mesh.FaceCount;
		var stride = Math.Max( 1, total / cap );

		for ( var i = 0; i < mesh.VertexCount; i += stride )
			into.Add( mesh.Positions[i] );

		for ( var i = 0; i < mesh.FaceCount; i += stride )
			into.Add( mesh.FaceCentroid( mesh.Faces[i] ) );
	}

	/// <summary>The name of one LOD level: <c>{baseName}_LOD0</c> through <c>{baseName}_LOD3</c>.</summary>
	public static string LodName( string baseName, int level )
	{
		if ( string.IsNullOrWhiteSpace( baseName ) )
			throw new ArgumentException( "an LOD chain needs a base mesh name", nameof( baseName ) );
		if ( level < 0 || level > 3 )
			throw new ArgumentOutOfRangeException( nameof( level ), level, "LOD level must be 0, 1, 2 or 3" );

		return $"{baseName}_LOD{level}";
	}

	/// <summary>All four LOD names for a base mesh, LOD0 first.</summary>
	public static IReadOnlyList<string> LodNames( string baseName )
	{
		var names = new string[4];

		for ( var i = 0; i < 4; i++ )
			names[i] = LodName( baseName, i );

		return names;
	}

	/// <summary>One named level of a chain: the <c>{base}_LOD{i}</c> name over the level.</summary>
	public sealed class NamedLevel
	{
		/// <summary><c>{base}_LOD{index}</c>.</summary>
		public string Name;

		/// <summary>The chain level, from <see cref="LodChain.BuildLodChain"/>.</summary>
		public LodChain.Level Level;
	}

	/// <summary>
	/// The full LOD0–LOD3 chain for a mesh, named <c>{baseName}_LOD0</c> through
	/// <c>{baseName}_LOD3</c>. The chain itself is <see cref="LodChain.BuildLodChain"/> —
	/// levels decimated from the source independently, LOD0 the source untouched — this
	/// only puts the Finish pipeline's names on it, so both tickets ship the same names.
	/// </summary>
	public static List<NamedLevel> BuildNamedChain( PolyMesh mesh, string baseName )
	{
		var chain = LodChain.BuildLodChain( mesh );
		var names = LodNames( baseName );
		var named = new List<NamedLevel>( chain.Count );

		for ( var i = 0; i < chain.Count; i++ )
			named.Add( new NamedLevel { Name = names[i], Level = chain[i] } );

		return named;
	}
}
