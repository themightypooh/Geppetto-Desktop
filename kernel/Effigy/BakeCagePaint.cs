using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A paintable bake cage: per-vertex offsets (out along the low mesh's averaged vertex
/// normals) and tangential skews, painted with a brush and undone stroke by stroke.
///
/// WHY THIS EXISTS. <see cref="BakeCage"/> already inflates a cage by one distance, or by one
/// distance per vertex — but nothing could <i>paint</i> those distances. A finger needs a tight
/// cage or its rays catch the neighbouring finger; a horn needs a wide one or its tip bakes
/// flat. Marmoset's cage tools solve this by letting the artist paint the offset (and the skew)
/// where it is needed, in the viewport, and this is that session: brush strokes over the cage
/// vertices, an undo stack of strokes, and a <see cref="BuildCage"/> that hands the baker a
/// mesh with the low body's own topology (same vertex count, same faces, same UVs), which is
/// what <see cref="NormalBake"/> requires of a cage mesh.
///
/// Offsets are distances, so they are never negative: painting inward stops at the low surface.
/// Skew is a tangential displacement and can point either way; it steers rays around concave
/// corners the way a hand-pushed cage does. The painted cage never mutates the low mesh.
/// </summary>
public sealed class BakeCagePaint
{
	/// <summary>The low body the cage is built from. Owned by the caller, never mutated.</summary>
	public PolyMesh Low { get; }

	/// <summary>Per-vertex offset out along the low mesh's averaged vertex normal, model units, &gt;= 0.</summary>
	public float[] Offsets { get; }

	/// <summary>Per-vertex tangential displacement, model units. Applied after the offset.</summary>
	public Vec3[] Skews { get; }

	/// <summary>Brush radius in model units for <see cref="PaintOffset"/>, <see cref="PaintSkew"/>, <see cref="Smooth"/> and <see cref="Reset"/>.</summary>
	public float Radius { get; set; } = 0.5f;

	/// <summary>Brush strength multiplier, 0..1. A stroke's full <paramref name="amount"/> lands at the brush centre.</summary>

	/// <summary>Strokes painted since the last <see cref="Clear"/> or construction. Each entry is one undo step.</summary>
	public int StrokeCount => _undo.Count;
	public float Strength { get; set; } = 1f;


	sealed class Stroke
	{
		public (int Vertex, float Offset, Vec3 Skew)[] Before;
	}

	readonly List<Stroke> _undo = new();

	/// <param name="low">The low body. Its vertex count fixes the offset/skew arrays.</param>
	/// <param name="baseOffset">Starting offset for every vertex, usually <see cref="BakeCage.EstimateDistance(PolyMesh, PolyMesh)"/> against the high mesh.</param>
	public BakeCagePaint( PolyMesh low, float baseOffset = 0f )
	{
		Low = low ?? throw new ArgumentNullException( nameof( low ) );

		if ( !( baseOffset >= 0f ) || float.IsNaN( baseOffset ) || float.IsInfinity( baseOffset ) )
			throw new ArgumentException( $"Base cage offset {baseOffset} is not a distance at or above 0.", nameof( baseOffset ) );

		Offsets = new float[low.VertexCount];
		Skews = new Vec3[low.VertexCount];

		for ( var i = 0; i < Offsets.Length; i++ )
			Offsets[i] = baseOffset;
	}

	/// <summary>Rebuild a paint session from saved per-vertex data (offsets and skews must match the low mesh's vertex count).</summary>
	public static BakeCagePaint FromData( PolyMesh low, float[] offsets, Vec3[] skews )
	{
		if ( low is null ) throw new ArgumentNullException( nameof( low ) );
		if ( offsets is null || offsets.Length != low.VertexCount ) throw new ArgumentException( $"Need one offset per low vertex ({low.VertexCount}).", nameof( offsets ) );
		if ( skews is null || skews.Length != low.VertexCount ) throw new ArgumentException( $"Need one skew per low vertex ({low.VertexCount}).", nameof( skews ) );

		var paint = new BakeCagePaint( low );
		Array.Copy( offsets, paint.Offsets, offsets.Length );
		Array.Copy( skews, paint.Skews, skews.Length );
		return paint;
	}

	/// <summary>Copy of the painted offsets, for saving.</summary>
	public float[] OffsetsCopy()
	{
		var copy = new float[Offsets.Length];
		Array.Copy( Offsets, copy, Offsets.Length );
		return copy;
	}

	/// <summary>Copy of the painted skews, for saving.</summary>
	public Vec3[] SkewsCopy()
	{
		var copy = new Vec3[Skews.Length];
		Array.Copy( Skews, copy, Skews.Length );
		return copy;
	}

	/// <summary>How far out the cage sits for the high mesh, Marmoset's Estimate Offset: the largest low-to-high gap.</summary>
	public float EstimateOffset( PolyMesh high ) => BakeCage.EstimateDistance( Low, high );

	/// <summary>
	/// Paint the cage outward (positive <paramref name="amount"/>) or back toward the low surface
	/// (negative) around <paramref name="centre"/>. Vertices within <paramref name="radius"/> move,
	/// with a smoothstep falloff to zero at the brush edge. Returns how many vertices the stroke touched.
	/// </summary>
	public int PaintOffset( Vec3 centre, float radius, float amount )
	{
		if ( !( radius > 0f ) ) throw new ArgumentException( "Brush radius must be above 0.", nameof( radius ) );

		var stroke = BeginStroke( centre, radius );
		var touched = 0;

		for ( var i = 0; i < Low.VertexCount; i++ )
		{
			var w = Falloff( Low.Positions[i], centre, radius );
			if ( w <= 0f ) continue;
			Offsets[i] = MathF.Max( 0f, Offsets[i] + amount * Strength * w );
			touched++;
		}

		EndStroke( stroke, touched );
		return touched;
	}

	/// <summary>
	/// Paint a tangential skew around <paramref name="centre"/>, pushing the cage sideways along
	/// <paramref name="direction"/> (which is normalised here). Skew steers the bake rays: a cage
	/// pushed out at an angle also aims its rays around a concave corner. Returns how many vertices touched.
	/// </summary>
	public int PaintSkew( Vec3 centre, Vec3 direction, float radius, float amount )
	{
		if ( !( radius > 0f ) ) throw new ArgumentException( "Brush radius must be above 0.", nameof( radius ) );
		if ( direction.LengthSquared < 1e-12f ) return 0;

		var dir = direction.Normal;
		var stroke = BeginStroke( centre, radius );
		var touched = 0;

		for ( var i = 0; i < Low.VertexCount; i++ )
		{
			var w = Falloff( Low.Positions[i], centre, radius );
			if ( w <= 0f ) continue;
			Skews[i] += dir * ( amount * Strength * w );
			touched++;
		}

		EndStroke( stroke, touched );
		return touched;
	}

	/// <summary>Average the offsets and skews around <paramref name="centre"/>: a smoothing brush for a lumpy hand-painted cage. Returns vertices touched.</summary>
	public int Smooth( Vec3 centre, float radius )
	{
		if ( !( radius > 0f ) ) throw new ArgumentException( "Brush radius must be above 0.", nameof( radius ) );

		var touched = new List<int>();
		var weights = new List<float>();

		for ( var i = 0; i < Low.VertexCount; i++ )
		{
			var w = Falloff( Low.Positions[i], centre, radius );
			if ( w <= 0f ) continue;
			touched.Add( i );
			weights.Add( w );
		}

		if ( touched.Count == 0 ) return 0;

		var meanOffset = 0f;
		var meanSkew = Vec3.Zero;
		var total = 0f;

		for ( var t = 0; t < touched.Count; t++ )
		{
			meanOffset += Offsets[touched[t]] * weights[t];
			meanSkew += Skews[touched[t]] * weights[t];
			total += weights[t];
		}

		meanOffset /= total;
		meanSkew /= total;

		var stroke = BeginStroke( centre, radius );

		for ( var t = 0; t < touched.Count; t++ )
		{
			var i = touched[t];
			var blend = weights[t] * Strength;
			Offsets[i] = MathF.Max( 0f, Offsets[i] + ( meanOffset - Offsets[i] ) * blend );
			Skews[i] += ( meanSkew - Skews[i] ) * blend;
		}

		EndStroke( stroke, touched.Count );
		return touched.Count;
	}

	/// <summary>Erase paint around <paramref name="centre"/>: offsets and skews back to zero there. Returns vertices touched.</summary>
	public int Reset( Vec3 centre, float radius )
	{
		if ( !( radius > 0f ) ) throw new ArgumentException( "Brush radius must be above 0.", nameof( radius ) );

		var stroke = BeginStroke( centre, radius );
		var touched = 0;

		for ( var i = 0; i < Low.VertexCount; i++ )
		{
			if ( Falloff( Low.Positions[i], centre, radius ) <= 0f ) continue;
			Offsets[i] = 0f;
			Skews[i] = Vec3.Zero;
			touched++;
		}

		EndStroke( stroke, touched );
		return touched;
	}

	/// <summary>Drop all paint and all undo history: offsets and skews back to zero everywhere.</summary>
	public void Clear()
	{
		Array.Clear( Offsets, 0, Offsets.Length );
		Array.Clear( Skews, 0, Skews.Length );
		_undo.Clear();
	}

	/// <summary>Undo the last stroke. False when there is nothing to undo.</summary>
	public bool Undo()
	{
		if ( _undo.Count == 0 ) return false;

		var stroke = _undo[_undo.Count - 1];
		_undo.RemoveAt( _undo.Count - 1 );

		foreach ( var (vertex, offset, skew) in stroke.Before )
		{
			Offsets[vertex] = offset;
			Skews[vertex] = skew;
		}

		return true;
	}

	/// <summary>
	/// The painted cage mesh: the low body inflated by the painted offsets, then displaced by the
	/// painted skews. Same topology and UVs as the low body, so the baker accepts it as a cage mesh.
	/// The low mesh itself is never touched.
	/// </summary>
	public PolyMesh BuildCage()
	{
		var cage = BakeCage.Inflate( Low, Offsets );

		for ( var i = 0; i < cage.Positions.Count; i++ )
		{
			if ( Skews[i].LengthSquared > 0f )
				cage.Positions[i] = cage.Positions[i] + Skews[i];
		}

		cage.InvalidateVertexCache();
		return cage;
	}

	/// <summary>Validate the painted cage the way <see cref="BakeCage.Validate"/> does: returns flagged vertices and a detail string; 0 flagged means the cage is usable.</summary>
	public int ValidateCage( out string detail )
	{
		var cage = BuildCage();
		return BakeCage.Validate( Low, cage, out detail );
	}

	/// <summary>Smoothstep falloff: 1 at the brush centre, 0 at and past <paramref name="radius"/>.</summary>
	static float Falloff( Vec3 vertex, Vec3 centre, float radius )
	{
		var d = ( vertex - centre ).Length / radius;
		if ( d >= 1f ) return 0f;
		var t = 1f - d;
		return t * t * ( 3f - 2f * t );
	}

	/// <summary>Snapshot the vertices a stroke may touch, so Undo restores exactly those.</summary>
	Stroke BeginStroke( Vec3 centre, float radius )
	{
		var before = new List<(int, float, Vec3)>();

		for ( var i = 0; i < Low.VertexCount; i++ )
		{
			if ( Falloff( Low.Positions[i], centre, radius ) > 0f )
				before.Add( (i, Offsets[i], Skews[i]) );
		}

		return new Stroke { Before = before.ToArray() };
	}

	void EndStroke( Stroke stroke, int touched )
	{
		if ( touched > 0 )
			_undo.Add( stroke );
	}
}
