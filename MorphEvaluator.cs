#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Policy controlling morph target weight bounds and limits.
/// </summary>
public enum MorphWeightPolicy
{
	/// <summary>Clamp all evaluated weights to [0, 1].</summary>
	Clamp01,

	/// <summary>Allow weights outside [0, 1] (overdrive and negative weights).</summary>
	AllowOverdrive,

	/// <summary>Clamp weights to custom [Min, Max] bounds.</summary>
	Custom
}

/// <summary>
/// Configuration for morph weight clamping and active target limits.
/// </summary>
public sealed record MorphPolicy
{
	public MorphWeightPolicy Kind { get; init; } = MorphWeightPolicy.Clamp01;
	public float Min { get; init; } = 0f;
	public float Max { get; init; } = 1f;
	public int MaxActiveTargets { get; init; } = MorphEvaluator.DefaultMaxActiveTargets;

	public static readonly MorphPolicy Default = new()
	{
		Kind = MorphWeightPolicy.Clamp01,
		Min = 0f,
		Max = 1f,
		MaxActiveTargets = MorphEvaluator.DefaultMaxActiveTargets
	};

	public static readonly MorphPolicy Overdrive = new()
	{
		Kind = MorphWeightPolicy.AllowOverdrive,
		Min = float.NegativeInfinity,
		Max = float.PositiveInfinity,
		MaxActiveTargets = MorphEvaluator.DefaultMaxActiveTargets
	};

	public static MorphPolicy CustomBounds( float min, float max, int maxActiveTargets = MorphEvaluator.DefaultMaxActiveTargets ) =>
		new() { Kind = MorphWeightPolicy.Custom, Min = min, Max = max, MaxActiveTargets = maxActiveTargets };

	public float Apply( float weight )
	{
		if ( float.IsNaN( weight ) || float.IsInfinity( weight ) )
			return 0f;

		return Kind switch
		{
			MorphWeightPolicy.Clamp01 => Math.Clamp( weight, 0f, 1f ),
			MorphWeightPolicy.Custom => Math.Clamp( weight, Min, Max ),
			MorphWeightPolicy.AllowOverdrive => weight,
			_ => Math.Clamp( weight, 0f, 1f )
		};
	}
}

/// <summary>
/// A retained shape key with position deltas, optional normal deltas, and optional pose-space driver.
/// </summary>
public sealed class MorphTarget
{
	public string Name { get; }
	public Vec3[] PositionDeltas { get; }
	public Vec3[]? NormalDeltas { get; }
	public MorphDriver? Driver { get; }

	public MorphTarget( string name, Vec3[] positionDeltas, Vec3[]? normalDeltas = null, MorphDriver? driver = null )
	{
		Name = name ?? throw new ArgumentNullException( nameof( name ) );
		PositionDeltas = positionDeltas ?? throw new ArgumentNullException( nameof( positionDeltas ) );
		NormalDeltas = normalDeltas;
		Driver = driver;
	}

	public MorphTarget Clone() => new(
		Name,
		(Vec3[])PositionDeltas.Clone(),
		NormalDeltas != null ? (Vec3[])NormalDeltas.Clone() : null,
		Driver?.Clone()
	);
}

/// <summary>
/// Collection of retained morph targets extracted from a PolyMesh or imported asset.
/// </summary>
public sealed class RetainedMeshMorphs
{
	public int SourceVertexCount { get; }
	public IReadOnlyList<MorphTarget> Targets { get; }
	public IReadOnlyDictionary<string, MorphTarget> TargetLookup { get; }

	public RetainedMeshMorphs( int sourceVertexCount, IEnumerable<MorphTarget> targets )
	{
		SourceVertexCount = sourceVertexCount;
		var list = new List<MorphTarget>( targets );
		Targets = list;
		var dict = new Dictionary<string, MorphTarget>( StringComparer.Ordinal );
		foreach ( var t in list )
		{
			if ( !string.IsNullOrWhiteSpace( t.Name ) )
				dict[t.Name] = t;
		}
		TargetLookup = dict;
	}

	public bool TryGetTarget( string name, out MorphTarget target )
	{
		if ( name != null )
			return TargetLookup.TryGetValue( name, out target! );
		target = null!;
		return false;
	}

	/// <summary>
	/// Extracts and retains valid morphs from a PolyMesh. When computeNormalDeltas is true,
	/// area-weighted normal deltas are precomputed for each target.
	/// </summary>
	public static RetainedMeshMorphs FromMesh( PolyMesh mesh, bool computeNormalDeltas = true )
	{
		if ( mesh == null ) throw new ArgumentNullException( nameof( mesh ) );
		var list = new List<MorphTarget>();
		var vertexCount = mesh.Positions.Count;

		Vec3[]? baseNormals = null;
		if ( computeNormalDeltas && mesh.FaceCount > 0 && vertexCount > 0 )
		{
			baseNormals = SkinStrain.VertexNormals( mesh.Positions, mesh.Faces );
		}

		foreach ( var m in mesh.ValidMorphs )
		{
			if ( m.Deltas == null || m.Deltas.Length != vertexCount || string.IsNullOrWhiteSpace( m.Name ) )
				continue;

			Vec3[]? normDeltas = null;
			if ( computeNormalDeltas && baseNormals != null )
			{
				var shapedPos = new Vec3[vertexCount];
				for ( int i = 0; i < vertexCount; i++ )
					shapedPos[i] = mesh.Positions[i] + m.Deltas[i];
				var shapedNormals = SkinStrain.VertexNormals( shapedPos, mesh.Faces );
				normDeltas = new Vec3[vertexCount];
				for ( int i = 0; i < vertexCount; i++ )
					normDeltas[i] = shapedNormals[i] - baseNormals[i];
			}

			list.Add( new MorphTarget( m.Name, (Vec3[])m.Deltas.Clone(), normDeltas, m.Driver?.Clone() ) );
		}

		return new RetainedMeshMorphs( vertexCount, list );
	}
}

/// <summary>
/// Maps extracted corner vertices (e.g. from ViewportMesh / GPU VBO) back to source PolyMesh vertices,
/// preserving UV seams and hard-normal vertex duplication so every duplicate corner receives the identical morph delta.
/// </summary>
public sealed class CornerVertexMap
{
	public int CornerCount { get; }
	public int SourceVertexCount { get; }
	public int[] CornerToSource { get; }
	public int[][] SourceToCorners { get; }
	public Vec3[] BasePositions { get; }
	public Vec3[] BaseNormals { get; }
	public Vec2[] BaseUVs { get; }

	public CornerVertexMap(
		int sourceVertexCount,
		int[] cornerToSource,
		Vec3[]? basePositions = null,
		Vec3[]? baseNormals = null,
		Vec2[]? baseUVs = null )
	{
		SourceVertexCount = sourceVertexCount;
		CornerToSource = cornerToSource ?? throw new ArgumentNullException( nameof( cornerToSource ) );
		CornerCount = cornerToSource.Length;

		BasePositions = basePositions ?? new Vec3[CornerCount];
		BaseNormals = baseNormals ?? new Vec3[CornerCount];
		BaseUVs = baseUVs ?? new Vec2[CornerCount];

		var groups = new List<int>[sourceVertexCount];
		for ( int i = 0; i < sourceVertexCount; i++ )
			groups[i] = new List<int>();

		for ( int c = 0; c < CornerCount; c++ )
		{
			int src = CornerToSource[c];
			if ( src >= 0 && src < sourceVertexCount )
				groups[src].Add( c );
		}

		SourceToCorners = new int[sourceVertexCount][];
		for ( int i = 0; i < sourceVertexCount; i++ )
			SourceToCorners[i] = groups[i].ToArray();
	}

	/// <summary>
	/// Builds a CornerVertexMap from a PolyMesh in sequential face order.
	/// </summary>
	public static CornerVertexMap FromMesh( PolyMesh mesh )
	{
		if ( mesh == null ) throw new ArgumentNullException( nameof( mesh ) );

		int totalCorners = 0;
		for ( int fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			if ( face.Count >= 3 )
				totalCorners += face.Count;
		}

		var cornerToSource = new int[totalCorners];
		var basePositions = new Vec3[totalCorners];
		var baseNormals = new Vec3[totalCorners];
		var baseUVs = new Vec2[totalCorners];

		var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, MeshNormals.DefaultSmoothingAngleDegrees );

		int cIdx = 0;
		for ( int fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			if ( face.Count < 3 ) continue;

			var cNorms = cornerNormals[fi];
			for ( int c = 0; c < face.Count; c++ )
			{
				int vi = face.Indices[c];
				cornerToSource[cIdx] = vi;
				basePositions[cIdx] = mesh.Positions[vi];
				baseNormals[cIdx] = normals[cNorms[c]];
				baseUVs[cIdx] = (face.UVs != null && c < face.UVs.Length) ? face.UVs[c] : Vec2.Zero;
				cIdx++;
			}
		}

		return new CornerVertexMap( mesh.Positions.Count, cornerToSource, basePositions, baseNormals, baseUVs );
	}

	/// <summary>
	/// Builds a CornerVertexMap matching ViewportMesh's material-bucketed corner vertex layout.
	/// </summary>
	public static CornerVertexMap FromMeshBucketed( PolyMesh mesh, Func<int, string>? nameForSlot = null )
	{
		if ( mesh == null ) throw new ArgumentNullException( nameof( mesh ) );
		nameForSlot ??= slot => $"mat_{slot}";

		var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, MeshNormals.DefaultSmoothingAngleDegrees );
		var buckets = new Dictionary<string, List<int>>( StringComparer.Ordinal );
		int totalCorners = 0;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			if ( face.Count < 3 ) continue;
			var name = nameForSlot( face.Material );
			if ( !buckets.TryGetValue( name, out var list ) )
				buckets[name] = list = new List<int>();
			list.Add( fi );
			totalCorners += face.Count;
		}

		var cornerToSource = new int[totalCorners];
		var basePositions = new Vec3[totalCorners];
		var baseNormals = new Vec3[totalCorners];
		var baseUVs = new Vec2[totalCorners];

		int cIdx = 0;
		foreach ( var (_, faces) in buckets )
		{
			foreach ( var fi in faces )
			{
				var face = mesh.Faces[fi];
				var cNorms = cornerNormals[fi];
				for ( var c = 0; c < face.Count; c++ )
				{
					int vi = face.Indices[c];
					cornerToSource[cIdx] = vi;
					basePositions[cIdx] = mesh.Positions[vi];
					baseNormals[cIdx] = normals[cNorms[c]];
					baseUVs[cIdx] = (face.UVs != null && c < face.UVs.Length) ? face.UVs[c] : Vec2.Zero;
					cIdx++;
				}
			}
		}

		return new CornerVertexMap( mesh.Positions.Count, cornerToSource, basePositions, baseNormals, baseUVs );
	}

	/// <summary>
	/// Maps source vertex deltas to corner vertex offsets.
	/// </summary>
	public void ExpandToCorners( ReadOnlySpan<Vec3> sourceDeltas, Span<Vec3> outCornerDeltas )
	{
		if ( outCornerDeltas.Length < CornerCount )
			throw new ArgumentException( "Output buffer smaller than corner count." );

		for ( int c = 0; c < CornerCount; c++ )
		{
			int vi = CornerToSource[c];
			outCornerDeltas[c] = (vi >= 0 && vi < sourceDeltas.Length) ? sourceDeltas[vi] : Vec3.Zero;
		}
	}
}

/// <summary>
/// Evaluates morph target weights and applies bind-space shape deltas before skinning.
/// Supports precedence evaluation matching kernel RigAnimFile.MorphWeightsAt / MorphDriver:
/// driven values (pose space correctives), keyed curves, and active events.
/// </summary>
public sealed class MorphEvaluator
{
	public const int DefaultMaxActiveTargets = 64;

	readonly Dictionary<string, float> _weights = new( StringComparer.Ordinal );
	readonly MorphPolicy _policy;

	public RetainedMeshMorphs Morphs { get; }
	public CornerVertexMap? CornerMap { get; }
	public MorphPolicy Policy => _policy;
	public IReadOnlyDictionary<string, float> CurrentWeights => _weights;

	public MorphEvaluator( RetainedMeshMorphs morphs, CornerVertexMap? cornerMap = null, MorphPolicy? policy = null )
	{
		Morphs = morphs ?? throw new ArgumentNullException( nameof( morphs ) );
		CornerMap = cornerMap;
		_policy = policy ?? MorphPolicy.Default;
	}

	public static MorphEvaluator FromMesh( PolyMesh mesh, MorphPolicy? policy = null, bool computeNormalDeltas = true )
	{
		var morphs = RetainedMeshMorphs.FromMesh( mesh, computeNormalDeltas );
		var cornerMap = CornerVertexMap.FromMesh( mesh );
		return new MorphEvaluator( morphs, cornerMap, policy );
	}

	public void Reset()
	{
		_weights.Clear();
	}

	/// <summary>
	/// Evaluates morph weights from a RigAnimFile clip, respecting precedence:
	/// 1. Driven values (MorphDrivers / pose space correctives)
	/// 2. Keyed curves (RigTrackKind.Morph) override driven values
	/// 3. Active morph events override keyed curves
	/// Clamps according to policy and reports/rejects target overflow.
	/// </summary>
	public Dictionary<string, float> Evaluate(
		RigAnimFile? clip,
		float frame,
		Skeleton? skeleton = null,
		IReadOnlyList<Xform>? localPose = null )
	{
		return EvaluateWeights( clip, frame, skeleton, localPose, _policy, _weights );
	}

	/// <summary>
	/// Evaluates morph weights from explicit driver, track, and event lists.
	/// </summary>
	public Dictionary<string, float> Evaluate(
		IReadOnlyList<MorphDriver>? drivers,
		IReadOnlyList<RigTrack>? tracks,
		IReadOnlyList<RigMorphEvent>? morphEvents,
		float frame,
		Skeleton? skeleton = null,
		IReadOnlyList<Xform>? localPose = null )
	{
		return EvaluateWeights( drivers, tracks, morphEvents, frame, skeleton, localPose, _policy, _weights );
	}

	/// <summary>
	/// Deforms source mesh positions into destination buffer. Base geometry is immutable.
	/// Zero weights restore base positions exactly bitwise.
	/// </summary>
	public void DeformPositions( ReadOnlySpan<Vec3> basePositions, Span<Vec3> outPositions )
	{
		DeformPositions( basePositions, _weights, Morphs, outPositions );
	}

	/// <summary>
	/// Deforms source mesh positions (Vector3) into destination buffer for picking or bounding box updates.
	/// </summary>
	public void DeformPositions( ReadOnlySpan<Vector3> basePositions, Span<Vector3> outPositions )
	{
		DeformPositions( basePositions, _weights, Morphs, outPositions );
	}

	/// <summary>
	/// Deforms extracted corner vertices (positions and normals) preserving UV and normal duplication.
	/// </summary>
	public void DeformCorners( Span<Vec3> outCornerPositions, Span<Vec3> outCornerNormals, bool normalizeNormals = true )
	{
		if ( CornerMap == null )
			throw new InvalidOperationException( "No CornerVertexMap was configured on this MorphEvaluator." );

		DeformCorners( CornerMap, _weights, Morphs, outCornerPositions, outCornerNormals, normalizeNormals );
	}

	/// <summary>
	/// Deforms an interleaved float vertex buffer (e.g. pos(3) + nrm(3) + weights).
	/// </summary>
	public void DeformInterleaved(
		ReadOnlySpan<float> baseVertices,
		Span<float> outVertices,
		int floatsPerVertex = 14,
		int positionOffset = 0,
		int normalOffset = 3,
		bool normalizeNormals = true )
	{
		if ( CornerMap == null )
			throw new InvalidOperationException( "No CornerVertexMap was configured on this MorphEvaluator." );

		DeformInterleaved( baseVertices, CornerMap, _weights, Morphs, outVertices, floatsPerVertex, positionOffset, normalOffset, normalizeNormals );
	}

	// --- Static Evaluation & Deformation Algorithms -----------------------------------------------

	/// <summary>
	/// Evaluates morph target weights matching kernel RigAnimFile.MorphWeightsAt / MorphDriver precedence.
	/// </summary>
	public static Dictionary<string, float> EvaluateWeights(
		IReadOnlyList<MorphDriver>? drivers,
		IReadOnlyList<RigTrack>? tracks,
		IReadOnlyList<RigMorphEvent>? morphEvents,
		float frame,
		Skeleton? skeleton = null,
		IReadOnlyList<Xform>? localPose = null,
		MorphPolicy? policy = null,
		Dictionary<string, float>? into = null )
	{
		policy ??= MorphPolicy.Default;
		into ??= new Dictionary<string, float>( StringComparer.Ordinal );
		into.Clear();

		// 1. Driven values: evaluated from local/world pose
		if ( drivers is { Count: > 0 } && skeleton != null && localPose != null )
		{
			MorphDriver.WeightsAt( drivers, skeleton, localPose, into );
		}

		// 2. Keyed curves (RigTrackKind.Morph): override driven values
		if ( tracks != null )
		{
			for ( int i = 0; i < tracks.Count; i++ )
			{
				var track = tracks[i];
				if ( track != null && track.Kind == RigTrackKind.Morph && track.Keys.Count > 0 && !string.IsNullOrWhiteSpace( track.Name ) )
				{
					into[track.Name] = track.Evaluate( frame ).Scale.x;
				}
			}
		}

		// 3. Active morph events: override keyed curves (later event in file wins)
		if ( morphEvents != null )
		{
			for ( int i = 0; i < morphEvents.Count; i++ )
			{
				var e = morphEvents[i];
				if ( e != null && !string.IsNullOrWhiteSpace( e.Morph ) && frame >= e.Start && frame <= e.End )
				{
					into[e.Morph] = e.Value * e.WeightAt( frame );
				}
			}
		}

		// 4. Policy application, clamp/overdrive, and NaN sanitization
		int activeCount = 0;
		var keys = into.Keys.ToArray();
		foreach ( var key in keys )
		{
			float val = into[key];
			float applied = policy.Apply( val );
			if ( float.IsNaN( applied ) || float.IsInfinity( applied ) || MathF.Abs( applied ) < 1e-6f )
			{
				into[key] = 0f;
			}
			else
			{
				into[key] = applied;
				activeCount++;
			}
		}

		// 5. Overflow check: reject if active targets exceed supported limit
		if ( activeCount > policy.MaxActiveTargets )
		{
			throw new InvalidOperationException(
				$"Active morph targets ({activeCount}) exceeded supported maximum limit ({policy.MaxActiveTargets})." );
		}

		return into;
	}

	public static Dictionary<string, float> EvaluateWeights(
		RigAnimFile? clip,
		float frame,
		Skeleton? skeleton = null,
		IReadOnlyList<Xform>? localPose = null,
		MorphPolicy? policy = null,
		Dictionary<string, float>? into = null )
	{
		if ( clip == null )
		{
			into ??= new Dictionary<string, float>( StringComparer.Ordinal );
			into.Clear();
			return into;
		}

		return EvaluateWeights( clip.Drivers, clip.Tracks, clip.MorphEvents, frame, skeleton, localPose, policy, into );
	}

	/// <summary>
	/// Applies linear combination of shape deltas to base positions. Base positions remain immutable.
	/// </summary>
	public static void DeformPositions(
		ReadOnlySpan<Vec3> basePositions,
		IReadOnlyDictionary<string, float>? weights,
		RetainedMeshMorphs? morphs,
		Span<Vec3> outPositions )
	{
		if ( outPositions.Length < basePositions.Length )
			throw new ArgumentException( "Output buffer is smaller than base positions buffer." );

		basePositions.CopyTo( outPositions );

		if ( weights == null || morphs == null || weights.Count == 0 )
			return;

		foreach ( var (name, weight) in weights )
		{
			if ( MathF.Abs( weight ) < 1e-6f || float.IsNaN( weight ) || float.IsInfinity( weight ) )
				continue;

			if ( morphs.TryGetTarget( name, out var target ) && target.PositionDeltas != null )
			{
				var deltas = target.PositionDeltas;
				int limit = Math.Min( deltas.Length, basePositions.Length );
				for ( int i = 0; i < limit; i++ )
				{
					outPositions[i] += deltas[i] * weight;
				}
			}
		}
	}

	/// <summary>
	/// Applies linear combination of shape deltas to base Vector3 positions.
	/// </summary>
	public static void DeformPositions(
		ReadOnlySpan<Vector3> basePositions,
		IReadOnlyDictionary<string, float>? weights,
		RetainedMeshMorphs? morphs,
		Span<Vector3> outPositions )
	{
		if ( outPositions.Length < basePositions.Length )
			throw new ArgumentException( "Output buffer is smaller than base positions buffer." );

		basePositions.CopyTo( outPositions );

		if ( weights == null || morphs == null || weights.Count == 0 )
			return;

		foreach ( var (name, weight) in weights )
		{
			if ( MathF.Abs( weight ) < 1e-6f || float.IsNaN( weight ) || float.IsInfinity( weight ) )
				continue;

			if ( morphs.TryGetTarget( name, out var target ) && target.PositionDeltas != null )
			{
				var deltas = target.PositionDeltas;
				int limit = Math.Min( deltas.Length, basePositions.Length );
				for ( int i = 0; i < limit; i++ )
				{
					var d = deltas[i];
					outPositions[i] += new Vector3( d.x, d.y, d.z ) * weight;
				}
			}
		}
	}

	/// <summary>
	/// Deforms extracted corner positions and normals. Every duplicate corner sharing a source vertex
	/// receives the exact identical shape offset.
	/// </summary>
	public static void DeformCorners(
		CornerVertexMap map,
		IReadOnlyDictionary<string, float>? weights,
		RetainedMeshMorphs? morphs,
		Span<Vec3> outCornerPositions,
		Span<Vec3> outCornerNormals,
		bool normalizeNormals = true )
	{
		if ( map == null ) throw new ArgumentNullException( nameof( map ) );
		if ( outCornerPositions.Length < map.CornerCount )
			throw new ArgumentException( "Output positions buffer is smaller than corner count." );
		if ( outCornerNormals.Length < map.CornerCount )
			throw new ArgumentException( "Output normals buffer is smaller than corner count." );

		new ReadOnlySpan<Vec3>( map.BasePositions ).CopyTo( outCornerPositions );
		new ReadOnlySpan<Vec3>( map.BaseNormals ).CopyTo( outCornerNormals );

		if ( weights == null || morphs == null || weights.Count == 0 )
			return;

		var cornerToSource = map.CornerToSource;
		int cornerCount = map.CornerCount;

		foreach ( var (name, weight) in weights )
		{
			if ( MathF.Abs( weight ) < 1e-6f || float.IsNaN( weight ) || float.IsInfinity( weight ) )
				continue;

			if ( morphs.TryGetTarget( name, out var target ) )
			{
				var posDeltas = target.PositionDeltas;
				var normDeltas = target.NormalDeltas;

				if ( posDeltas != null )
				{
					for ( int c = 0; c < cornerCount; c++ )
					{
						int vi = cornerToSource[c];
						if ( vi >= 0 && vi < posDeltas.Length )
						{
							outCornerPositions[c] += posDeltas[vi] * weight;
						}
					}
				}

				if ( normDeltas != null )
				{
					for ( int c = 0; c < cornerCount; c++ )
					{
						int vi = cornerToSource[c];
						if ( vi >= 0 && vi < normDeltas.Length )
						{
							outCornerNormals[c] += normDeltas[vi] * weight;
						}
					}
				}
			}
		}

		if ( normalizeNormals )
		{
			for ( int c = 0; c < cornerCount; c++ )
			{
				var n = outCornerNormals[c];
				outCornerNormals[c] = n.LengthSquared > 1e-12f ? n.Normal : map.BaseNormals[c];
			}
		}
	}

	/// <summary>
	/// Deforms an interleaved vertex buffer (such as pos(3) + nrm(3) + ...), updating position and normal components in-place.
	/// </summary>
	public static void DeformInterleaved(
		ReadOnlySpan<float> baseVertices,
		CornerVertexMap map,
		IReadOnlyDictionary<string, float>? weights,
		RetainedMeshMorphs? morphs,
		Span<float> outVertices,
		int floatsPerVertex = 14,
		int positionOffset = 0,
		int normalOffset = 3,
		bool normalizeNormals = true )
	{
		if ( map == null ) throw new ArgumentNullException( nameof( map ) );
		int expectedFloats = map.CornerCount * floatsPerVertex;
		if ( baseVertices.Length < expectedFloats )
			throw new ArgumentException( "Base vertices buffer is smaller than expected." );
		if ( outVertices.Length < expectedFloats )
			throw new ArgumentException( "Output vertices buffer is smaller than expected." );

		baseVertices.Slice( 0, expectedFloats ).CopyTo( outVertices );

		if ( weights == null || morphs == null || weights.Count == 0 )
			return;

		int cornerCount = map.CornerCount;
		var cornerToSource = map.CornerToSource;

		for ( int c = 0; c < cornerCount; c++ )
		{
			int vi = cornerToSource[c];
			int vBase = c * floatsPerVertex;

			Vec3 pos = new( outVertices[vBase + positionOffset], outVertices[vBase + positionOffset + 1], outVertices[vBase + positionOffset + 2] );
			Vec3 nrm = new( outVertices[vBase + normalOffset], outVertices[vBase + normalOffset + 1], outVertices[vBase + normalOffset + 2] );

			foreach ( var (name, weight) in weights )
			{
				if ( MathF.Abs( weight ) < 1e-6f || float.IsNaN( weight ) || float.IsInfinity( weight ) )
					continue;

				if ( morphs.TryGetTarget( name, out var target ) )
				{
					if ( target.PositionDeltas != null && vi >= 0 && vi < target.PositionDeltas.Length )
						pos += target.PositionDeltas[vi] * weight;

					if ( target.NormalDeltas != null && vi >= 0 && vi < target.NormalDeltas.Length )
						nrm += target.NormalDeltas[vi] * weight;
				}
			}

			if ( normalizeNormals )
				nrm = nrm.LengthSquared > 1e-12f ? nrm.Normal : map.BaseNormals[c];

			outVertices[vBase + positionOffset] = pos.x;
			outVertices[vBase + positionOffset + 1] = pos.y;
			outVertices[vBase + positionOffset + 2] = pos.z;

			outVertices[vBase + normalOffset] = nrm.x;
			outVertices[vBase + normalOffset + 1] = nrm.y;
			outVertices[vBase + normalOffset + 2] = nrm.z;
		}
	}

	/// <summary>
	/// Applies shape deltas in model/bind space BEFORE skinning, then evaluates linear blend skinning (LBS).
	/// </summary>
	public static Vec3[] DeformAndSkin(
		IReadOnlyList<Vec3> basePositions,
		IReadOnlyDictionary<string, float>? weights,
		RetainedMeshMorphs? morphs,
		SkinWeights skinWeights,
		IReadOnlyList<Xform> bindWorld,
		IReadOnlyList<Xform> poseWorld )
	{
		if ( basePositions == null ) throw new ArgumentNullException( nameof( basePositions ) );

		// 1. Deform in bind/model space first
		var morphed = new Vec3[basePositions.Count];
		DeformPositions(
			basePositions is Vec3[] arr ? arr : basePositions.ToArray(),
			weights,
			morphs,
			morphed );

		// 2. Perform skinning after morph deformation
		if ( skinWeights == null || bindWorld == null || poseWorld == null )
			return morphed;

		return SkinBinder.Deform( morphed, skinWeights, bindWorld, poseWorld );
	}
}
