using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Filter operators for SDF fields: things you do TO a field, not shapes you fuse.
/// None of these existed before wave 5: the kernel had leaves (sphere, box, gyroid, cellular),
/// blends/subtracts, and domain warps (twist, bend, mirror), but no taper, no tiling, and no
/// growth-fold displacement. What is NOT here, on purpose:
/// - Dilate/erode as separate nodes: <see cref="SdfOffset"/> already IS dilate (amount &gt; 0)
///   and erode (amount &lt; 0). A second node would be a rename, not a feature.
/// - Morphological open/close by offset composition: for an exact SDF, dilate(erode(f)) and
///   erode(dilate(f)) cancel exactly back to f. Shipping that as a filter would be a no-op
///   dressed as a feature, so it is not shipped.
/// - A true reaction-diffusion growth simulation: <see cref="SdfGrowthFolds"/> grows fold
///   ridges along a cellular field, which reads as differential growth, but it is not a
///   reaction-diffusion solve and its docs say so.
/// </summary>
public static class SdfFilter
{
	/// <summary>
	/// Parse one Blob filter line and wrap the field. Syntax (one filter per line):
	///   taper:Z:0.5        cross-section scales by (1 + 0.5*t) along Z, t from the field centre
	///   growth:0.3:0.5:7   fold ridges, amplitude 0.3, frequency 0.5, seed 7 (seed optional)
	///   growth:0.3:0.5:7:2 same, with fold sharpness 2 (optional, default 2)
	///   repeat:X:3         tile the field 3 times along X, cell = X extent / 3
	/// Throws <see cref="ArgumentException"/> with a usable message on bad syntax; the
	/// Blob feature turns that into a FailOn against its Filter parameter.
	/// </summary>
	public static Sdf Parse( Sdf field, string text )
	{
		var parts = (text ?? "").Split( ':', StringSplitOptions.RemoveEmptyEntries )
			.Select( p => p.Trim() ).ToArray();
		if ( parts.Length == 0 )
			throw new ArgumentException( "The filter is empty." );

		var kind = parts[0].ToLowerInvariant();
		var center = (field.Min + field.Max) * 0.5f;

		if ( kind == "taper" && parts.Length == 3 )
		{
			var axis = parts[1].ToUpperInvariant() switch
			{
				"X" => new Vec3( 1, 0, 0 ),
				"Y" => new Vec3( 0, 1, 0 ),
				"Z" => new Vec3( 0, 0, 1 ),
				_ => throw new ArgumentException( $"Taper axis '{parts[1]}' is not X, Y or Z." ),
			};
			if ( !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount ) )
				throw new ArgumentException( $"Taper amount '{parts[2]}' is not a number." );
			return new SdfTaper( field, center, axis, amount );
		}

		if ( kind == "growth" && parts.Length is 3 or 4 or 5 )
		{
			if ( !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amp ) )
				throw new ArgumentException( $"Growth amplitude '{parts[1]}' is not a number." );
			if ( !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var freq ) || freq <= 0f )
				throw new ArgumentException( $"Growth frequency '{parts[2]}' is not a positive number." );
			var seed = parts.Length >= 4 && int.TryParse( parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s ) ? s : 1;
			var sharp = parts.Length >= 5 && float.TryParse( parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var sh ) ? sh : 2f;
			return new SdfGrowthFolds( field, amp, freq, 0.3f, seed, sharp );
		}

		if ( kind == "repeat" && parts.Length == 3 )
		{
			var axis = parts[1].ToUpperInvariant();
			if ( axis is not ("X" or "Y" or "Z") )
				throw new ArgumentException( $"Repeat axis '{parts[1]}' is not X, Y or Z." );
			if ( !int.TryParse( parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count ) || count < 2 )
				throw new ArgumentException( $"Repeat count '{parts[2]}' is not an integer of 2 or more (1 repeats nothing)." );
			var extent = field.Max - field.Min;
			var cell = axis switch
			{
				"X" => new Vec3( extent.x / count, 0, 0 ),
				"Y" => new Vec3( 0, extent.y / count, 0 ),
				_ => new Vec3( 0, 0, extent.z / count ),
			};
			var counts = axis switch
			{
				"X" => (count, 0, 0),
				"Y" => (0, count, 0),
				_ => (0, 0, count),
			};
			return new SdfRepeat( field, cell, counts.Item1, counts.Item2, counts.Item3 );
		}

		throw new ArgumentException(
			$"Filter '{text}' is not one I know. Use taper:AXIS:amount, growth:amp:freq[:seed[:sharpness]], or repeat:AXIS:count." );
	}
}

/// <summary>
/// Taper a field along an axis: the cross-section at distance t along the axis (from
/// <see cref="Center"/>) scales by (1 + <see cref="Amount"/> * t). A horn that widens toward
/// its tip, a vase that narrows toward its neck, a limb that thickens toward the body.
/// Positive amount fattens toward +axis and thins toward -axis; negative does the reverse.
/// The scale clamps at 0.05 so a steep negative taper pinches instead of folding the field
/// inside out (which would invert inside/outside and mesh garbage).
/// </summary>
public sealed class SdfTaper : Sdf
{
	public readonly Sdf Child;
	public readonly Vec3 Center, Axis;
	public readonly float Amount;

	public SdfTaper( Sdf child, Vec3 center, Vec3 axis, float amount )
	{
		Child = child;
		Center = center;
		Axis = axis.LengthSquared > 1e-12f ? axis.Normal : new Vec3( 0, 0, 1 );
		Amount = amount;
		Material = child.Material;
		Bone = child.Bone;
		Tag = child.Tag;
	}

	Vec3 Inverse( Vec3 p )
	{
		var rel = p - Center;
		var t = Vec3.Dot( rel, Axis );
		var s = 1f + Amount * t;
		if ( s < 0.05f ) s = 0.05f;
		return Center + Axis * t + (rel - Axis * t) / s;
	}

	public override float Eval( Vec3 p ) => Child.Eval( Inverse( p ) );

	// Bounds: the along-axis extent never changes, and the cross-section only ever grows
	// where (1 + Amount*t) exceeds 1 over the child's span, so pad by the grown fraction
	// of the half diagonal. A taper that only shrinks needs no pad: the child's box already
	// contains the shrunk shape. A superset box is safe for the mesher (it just samples
	// more empty space); a too-tight box would clip the surface, so this errs wide.
	float Pad
	{
		get
		{
			var tmin = MathF.Min( Vec3.Dot( Child.Min - Center, Axis ), Vec3.Dot( Child.Max - Center, Axis ) );
			var tmax = MathF.Max( Vec3.Dot( Child.Min - Center, Axis ), Vec3.Dot( Child.Max - Center, Axis ) );
			var smax = MathF.Max( 1f + Amount * tmin, 1f + Amount * tmax );
			return smax > 1f ? (Child.Max - Child.Min).Length * 0.5f * (smax - 1f) : 0f;
		}
	}

	public override Vec3 Min => Child.Min - Vec3.One * Pad;
	public override Vec3 Max => Child.Max + Vec3.One * Pad;
	public override void Prepare( float pad ) => Child.Prepare( pad + Pad );
	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into ) =>
		Child.Collect( Inverse( p ), band, into );
}

/// <summary>
/// Tile a field along one or more axes: fence posts, scales, teeth, rivets. Each axis takes
/// a cell size and a copy count; 0 copies means "don't repeat on this axis". Inside
/// Min/Max the field repeats with period = cell; OUTSIDE Min/Max Eval returns the distance
/// to the tiled bounds box (positive = empty), so the tiling is genuinely finite. That
/// second half matters: the mesher pads the bounds by two voxels and samples there, and a
/// purely periodic Eval grows phantom partial tiles past the ends (measured: two extra
/// 57-face components with inverted winding that cancelled 29.4 units of signed volume
/// off a 3-sphere tiling). Tiles that overlap (cell smaller than the child's extent on
/// that axis) fuse in the field, which is the caller's choice, not an error.
/// </summary>
public sealed class SdfRepeat : Sdf
{
	public readonly Sdf Child;
	public readonly Vec3 Cell;
	public readonly int CountX, CountY, CountZ;

	public SdfRepeat( Sdf child, Vec3 cell, int countX, int countY, int countZ )
	{
		Child = child;
		Cell = cell;
		CountX = Math.Max( 0, countX );
		CountY = Math.Max( 0, countY );
		CountZ = Math.Max( 0, countZ );
		Material = child.Material;
		Bone = child.Bone;
		Tag = child.Tag;
	}

	Vec3 Center => (Child.Min + Child.Max) * 0.5f;

	static float WrapAxis( float local, float cell, int count )
	{
		// The period is ONE cell, not the whole tiled span: a point at a tile centre must
		// read the child's centre, and a point at a tile edge must read the child's edge.
		// (An earlier version wrapped by span = cell * count, which left tile centres
		// reading the child's edge — the repeat tests caught it: Eval(4) came back +2
		// instead of -2 on a radius-2 sphere tiled every 4 units.)
		if ( count <= 1 || cell <= 0f ) return local;
		var shifted = local + cell * 0.5f;
		var wrapped = shifted - MathF.Floor( shifted / cell ) * cell;
		return wrapped - cell * 0.5f;
	}

	Vec3 Wrap( Vec3 p )
	{
		var c = Center;
		var rel = p - c;
		return new Vec3(
			WrapAxis( rel.x, Cell.x, CountX ),
			WrapAxis( rel.y, Cell.y, CountY ),
			WrapAxis( rel.z, Cell.z, CountZ ) ) + c;
	}

	public override float Eval( Vec3 p )
	{
		// Outside the tiled bounds the field is empty, not periodic: return the distance
		// to the bounds box so padded mesher samples never grow phantom tiles (see class
		// docs). Inside, wrap with period = cell.
		var min = Min; var max = Max;
		if ( p.x < min.x || p.x > max.x || p.y < min.y || p.y > max.y || p.z < min.z || p.z > max.z )
		{
			var dx = MathF.Max( MathF.Max( min.x - p.x, p.x - max.x ), 0f );
			var dy = MathF.Max( MathF.Max( min.y - p.y, p.y - max.y ), 0f );
			var dz = MathF.Max( MathF.Max( min.z - p.z, p.z - max.z ), 0f );
			return MathF.Sqrt( dx * dx + dy * dy + dz * dz );
		}
		return Child.Eval( Wrap( p ) );
	}

	static void AxisBounds( float cmin, float cmax, float center, float cell, int count, out float lo, out float hi )
	{
		if ( count <= 1 )
		{
			lo = cmin; hi = cmax;
			return;
		}
		var first = center - (count - 1) * 0.5f * cell;
		var last = center + (count - 1) * 0.5f * cell;
		lo = first + (cmin - center);
		hi = last + (cmax - center);
	}

	public override Vec3 Min
	{
		get
		{
			var c = Center;
			AxisBounds( Child.Min.x, Child.Max.x, c.x, Cell.x, CountX, out var x0, out _ );
			AxisBounds( Child.Min.y, Child.Max.y, c.y, Cell.y, CountY, out var y0, out _ );
			AxisBounds( Child.Min.z, Child.Max.z, c.z, Cell.z, CountZ, out var z0, out _ );
			return new Vec3( x0, y0, z0 );
		}
	}

	public override Vec3 Max
	{
		get
		{
			var c = Center;
			AxisBounds( Child.Min.x, Child.Max.x, c.x, Cell.x, CountX, out _, out var x1 );
			AxisBounds( Child.Min.y, Child.Max.y, c.y, Cell.y, CountY, out _, out var y1 );
			AxisBounds( Child.Min.z, Child.Max.z, c.z, Cell.z, CountZ, out _, out var z1 );
			return new Vec3( x1, y1, z1 );
		}
	}

	public override void Prepare( float pad ) => Child.Prepare( pad );
	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into )
	{
		if ( !InBounds( p, band ) ) return;
		Child.Collect( Wrap( p ), band, into );
	}
}

/// <summary>
/// Differential-growth-LOOK folds: ridges that grow out of a surface along the walls of a
/// cellular field, like the folds on a brain, a gut pile, or a Shar-Pei. The displacement is
/// the cellular wall distance folded into a ridge (1 at the wall, falling to 0 away from it,
/// sharpened by <see cref="Sharpness"/>), times <see cref="Amplitude"/>, pushing the surface
/// outward. Read the name honestly: this is fold ridges driven by a cellular field. It is
/// NOT a reaction-diffusion simulation — nothing here diffuses, reacts, or grows over time;
/// the folds are placed by the cellular walls in one evaluation. If a caller needs true
/// growth dynamics, that is a different node.
/// Far from the surface the field is untouched (same early-out as <see cref="SdfDisplace"/>),
/// so a growth field costs nothing where nothing is growing.
/// </summary>
public sealed class SdfGrowthFolds : Sdf
{
	public readonly Sdf Child;
	public readonly float Amplitude, Frequency, Thickness, Sharpness;
	public readonly int Seed;
	readonly SdfCellular _cells;

	public SdfGrowthFolds( Sdf child, float amplitude, float frequency, float thickness = 0.3f, int seed = 1, float sharpness = 2f )
	{
		Child = child;
		Amplitude = amplitude;
		Frequency = MathF.Max( 1e-3f, frequency );
		Thickness = MathF.Max( 0f, thickness );
		Seed = seed;
		Sharpness = MathF.Max( 0.25f, sharpness );
		var pad = Amplitude + 1f;
		_cells = new SdfCellular( Frequency, Thickness, Child.Min - Vec3.One * pad, Child.Max + Vec3.One * pad, Seed );
		Material = child.Material;
		Bone = child.Bone;
		Tag = child.Tag;
	}

	public override float Eval( Vec3 p )
	{
		var d = Child.Eval( p );
		if ( MathF.Abs( d ) > Amplitude * 2f + 1f ) return d;
		var s = _cells.Eval( p );
		var fold = MathF.Max( 0f, 1f - MathF.Abs( s ) * 2f );
		if ( Sharpness != 1f ) fold = MathF.Pow( fold, Sharpness );
		return d - fold * Amplitude;
	}

	public override Vec3 Min => Child.Min - Vec3.One * Amplitude;
	public override Vec3 Max => Child.Max + Vec3.One * Amplitude;
	public override void Prepare( float pad ) { Child.Prepare( pad + Amplitude ); _cells.Prepare( pad ); }
	public override void Collect( Vec3 p, float band, List<(Sdf Leaf, float Distance)> into ) =>
		Child.Collect( p, band + Amplitude, into );
}
