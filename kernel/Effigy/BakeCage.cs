using System;
using System.Collections.Generic;
using System.Globalization;

namespace Effigy;

/// <summary>
/// Marmoset-style cage generation for baking: a cage is a copy of the low body pushed out
/// along its averaged vertex normals, one cage per group, each at its own distance.
///
/// WHY THIS EXISTS. The baker already bakes groups apart (each group's rays only see its own
/// high mesh) and already accepts a cage mesh per group — but until now that cage had to be
/// authored by hand as an X_cage body, or every group shared one scalar cage distance. A finger
/// needs a tight cage or its rays catch the neighbour; a horn needs a wide one or its tip bakes
/// flat. One distance cannot serve both, and hand-building a cage body per pair is the busywork
/// Marmoset automated away. Inflating the low body along its own averaged normals gives a cage
/// with exactly the low body's topology (same vertices, same faces, same order), which is what
/// <see cref="NormalBake.CageMismatch"/> demands — so a generated cage always fits, by
/// construction, and the baker's cage-to-low ray steering and per-group front window apply.
///
/// LIMITS, STATED PLAINLY. Pushing along averaged normals is the standard cage, not a magic
/// one: on a sharply concave corner a large distance can push neighbouring cage vertices
/// across each other, and then rays from the crossed region start on the wrong side. That is
/// the same failure a hand-built Marmoset cage has at the same distance, and the fix is the
/// same: a smaller distance for that group (which is exactly what per-group distances are
/// for). This builder does not smooth, relax, or untangle the cage — it inflates it.
///
/// ONE RULE THE CALLER MUST RESPECT. The cage has to enclose the high detail: rays travel
/// cage-to-low, so a cage inside the high catches nothing on that side (a 0.01 cage over
/// 0.05-proud detail bakes empty, and empty is the honest answer, not a bug). Pick the
/// distance per group to clear that group's proud detail — that is the whole point of
/// per-group distances.
/// </summary>
public static class BakeCage
{
	/// <summary>Inflate a low body along its averaged vertex normals by one distance.</summary>
	public static PolyMesh Inflate( PolyMesh low, float distance )
	{
		if ( low is null )
			throw new ArgumentNullException( nameof( low ) );

		if ( !( distance >= 0f ) || float.IsNaN( distance ) || float.IsInfinity( distance ) )
			throw new ArgumentException( $"Cage distance {distance} is not a distance at or above 0.", nameof( distance ) );

		var cage = low.Clone();

		if ( distance == 0f )
			return cage;

		var normals = low.ComputeVertexNormals();

		for ( var i = 0; i < cage.Positions.Count; i++ )
		{
			var n = normals[i];

			if ( n.LengthSquared < 1e-12f )
				continue;

			cage.Positions[i] = cage.Positions[i] + n.Normal * distance;
		}

		return cage;
	}

	/// <summary>Inflate a low body by one distance per vertex (same count as vertices).</summary>
	public static PolyMesh Inflate( PolyMesh low, float[] distances )
	{
		if ( low is null )
			throw new ArgumentNullException( nameof( low ) );

		if ( distances is null || distances.Length != low.VertexCount )
			throw new ArgumentException( $"A per-vertex cage needs one distance per vertex ({low.VertexCount}), not {(distances is null ? "none" : distances.Length.ToString())}.", nameof( distances ) );

		var cage = low.Clone();
		var normals = low.ComputeVertexNormals();

		for ( var i = 0; i < cage.Positions.Count; i++ )
		{
			var d = distances[i];

			if ( !( d >= 0f ) || float.IsNaN( d ) || float.IsInfinity( d ) )
				throw new ArgumentException( $"Cage distance {d} at vertex {i} is not a distance at or above 0.", nameof( distances ) );

			var n = normals[i];

			if ( d == 0f || n.LengthSquared < 1e-12f )
				continue;

			cage.Positions[i] = cage.Positions[i] + n.Normal * d;
		}

		return cage;
	}

	/// <summary>
	/// Parse a cagegen= value: one default distance ("0.2"), per-pair distances
	/// ("Arm:0.1,Leg:0.3"), or both ("0.2,Arm:0.1" — named pairs override the default).
	/// Names match pair names case-insensitively. False with the reason in <paramref name="why"/>
	/// on anything unparseable: a non-number, a negative, two defaults, or an empty value.
	/// </summary>
	public static bool TryParseDistances( string text, out float? defaultDistance, out Dictionary<string, float> perPair, out string why )
	{
		defaultDistance = null;
		perPair = new Dictionary<string, float>( StringComparer.OrdinalIgnoreCase );
		why = null;

		if ( string.IsNullOrWhiteSpace( text ) )
		{
			why = "cagegen= needs a distance: cagegen=0.2, or per pair cagegen=Arm:0.1,Leg:0.3.";
			return false;
		}

		foreach ( var raw in text.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var entry = raw.Trim();
			var colon = entry.IndexOf( ':' );

			if ( colon < 0 )
			{
				if ( !TryDistance( entry, out var d, out why ) )
					return false;

				if ( defaultDistance is not null )
				{
					why = $"cagegen={text}: two default distances ({defaultDistance} and {d}) — give one default, or name every pair.";
					return false;
				}

				defaultDistance = d;
			}
			else
			{
				var name = entry[..colon].Trim();

				if ( name.Length == 0 )
				{
					why = $"cagegen={text}: '{entry}' names no pair — write Name:distance.";
					return false;
				}

				if ( !TryDistance( entry[(colon + 1)..].Trim(), out var d, out why ) )
					return false;

				perPair[name] = d;
			}
		}

		if ( defaultDistance is null && perPair.Count == 0 )
		{
			why = "cagegen= needs a distance: cagegen=0.2, or per pair cagegen=Arm:0.1,Leg:0.3.";
			return false;
		}

		return true;
	}

	static bool TryDistance( string text, out float d, out string why )
	{
		why = null;
		d = 0f;

		if ( !float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out d ) || d < 0f || float.IsNaN( d ) || float.IsInfinity( d ) )
		{
			why = $"cagegen= distance '{text}' is not a number at or above 0.";
			return false;
		}

		return true;
	}

	/// <summary>
	/// Estimate a cage distance from the high mesh: the furthest any low vertex must push
	/// along its own normal to clear the high surface, times 1.25, floored at 2% of the
	/// low body's diagonal. This is the "Estimate Offset" button, as a number: a cage
	/// smaller than its proud detail bakes empty, and empty is honest but unhelpful.
	/// </summary>
	public static float EstimateDistance( PolyMesh low, PolyMesh high )
	{
		if ( low is null || high is null )
			return 0f;

		var diagonal = MathF.Max( low.BoundsDiagonal, 1e-4f );
		var normals = low.ComputeVertexNormals();
		var bvh = MeshBVH.Build( high );
		var eps = diagonal * 1e-4f;
		var furthest = 0f;

		for ( var i = 0; i < low.VertexCount; i++ )
		{
			var n = normals[i];

			if ( n.LengthSquared < 1e-12f )
				continue;

			var hit = bvh.Raycast( high, low.Positions[i] + n.Normal * eps, n.Normal );

			if ( hit is not null && hit.Value.Distance < diagonal * 2f )
				furthest = MathF.Max( furthest, hit.Value.Distance );
		}

		return MathF.Max( furthest * 1.25f, diagonal * 0.02f );
	}

	/// <summary>
	/// How many cage vertices moved further than half their shortest incident edge.
	/// Past that, neighbouring cage vertices can cross each other on a concave corner
	/// and rays from the crossed region start on the wrong side. This is the cheap
	/// proxy the research proposed (a full cage self-intersection test would need a
	/// second BVH pass); it flags the region instead of silently baking wrong.
	/// Returns the flagged count and a short description of where they are.
	/// </summary>
	public static int Validate( PolyMesh low, PolyMesh cage, out string detail )
	{
		detail = null;

		if ( low is null || cage is null || low.VertexCount != cage.VertexCount )
		{
			detail = "cage does not match the low body, so it cannot be validated.";
			return -1;
		}

		var minEdge = new float[low.VertexCount];

		for ( var i = 0; i < minEdge.Length; i++ )
			minEdge[i] = float.MaxValue;

		foreach ( var f in low.Faces )
		{
			for ( var e = 0; e < f.Count; e++ )
			{
				var a = f.Indices[e];
				var b = f.Indices[(e + 1) % f.Count];
				var len = (low.Positions[a] - low.Positions[b]).Length;

				if ( len < minEdge[a] ) minEdge[a] = len;
				if ( len < minEdge[b] ) minEdge[b] = len;
			}
		}

		var flagged = 0;
		var first = -1;
		var last = -1;

		for ( var i = 0; i < low.VertexCount; i++ )
		{
			if ( minEdge[i] == float.MaxValue )
				continue;

			if ( (cage.Positions[i] - low.Positions[i]).Length > 0.5f * minEdge[i] )
			{
				flagged++;
				if ( first < 0 ) first = i;
				last = i;
			}
		}

		detail = flagged == 0 ? "no cage vertex moved past half its shortest edge."
			: $"{flagged} cage vertices moved past half their shortest edge (first {first}, last {last}).";
		return flagged;
	}

	/// <summary>
	/// Inflate, and if validation flags vertices, retry at half distance up to 3
	/// halvings before giving up with the smallest cage. Returns the cage, the
	/// distance actually used, and the flagged count at that distance.
	/// </summary>
	public static PolyMesh InflateValidated( PolyMesh low, float distance, out float applied, out int flagged )
	{
		PolyMesh cage = null;
		applied = distance;
		flagged = 0;

		for ( var attempt = 0; attempt < 4; attempt++ )
		{
			applied = distance / MathF.Pow( 2f, attempt );
			cage = Inflate( low, applied );
			flagged = Validate( low, cage, out _ );

			if ( flagged <= 0 )
				return cage;
		}

		return cage;
	}

	/// <summary>The cage distance for a pair: its named override, else the default, else none.</summary>
	public static float? DistanceFor( string pairName, float? defaultDistance, IReadOnlyDictionary<string, float> perPair )
	{
		if ( pairName is not null && perPair is not null && perPair.TryGetValue( pairName, out var d ) )
			return d;

		return defaultDistance;
	}
}
