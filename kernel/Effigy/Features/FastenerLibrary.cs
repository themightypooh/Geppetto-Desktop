using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>ISO 4014 hex bolt (full thread is ISO 4017; this is the part-thread bolt).</summary>
public sealed class BoltSpec
{
	public string Size = "M6";
	public float NominalMm;
	public float LengthMm;
	public float HeadAcrossFlatsMm;
	public float HeadHeightMm;
	public float ShankDiaMm;
	public string Standard = "ISO 4014";
	/// <summary>What the solid is: a spec only. The B-rep core (godlike-occt-brep-core-step-export) builds the real bolt solid from this spec.</summary>
	public string SolidDescription = "";
}

/// <summary>ISO 4762 socket-head cap screw.</summary>
public sealed class SocketHeadSpec
{
	public string Size = "M6";
	public float NominalMm;
	public float LengthMm;
	public float HeadDiaMm;
	public float HeadHeightMm;
	public float SocketAcrossFlatsMm;
	public string Standard = "ISO 4762";
	public string SolidDescription = "";
}

/// <summary>ISO 4032 hex nut, style 1.</summary>
public sealed class NutSpec
{
	public string Size = "M6";
	public float NominalMm;
	public float AcrossFlatsMm;
	public float HeightMm;
	public string Standard = "ISO 4032";
	public string SolidDescription = "";
}

/// <summary>ISO 7089 plain washer, normal series.</summary>
public sealed class WasherSpec
{
	public string Size = "M6";
	public float NominalMm;
	public float InnerDiaMm;
	public float OuterDiaMm;
	public float ThicknessMm;
	public string Standard = "ISO 7089";
	public string SolidDescription = "";
}

/// <summary>Single-row deep-groove ball bearing, ISO 15 boundary dimensions.</summary>
public sealed class BearingSpec
{
	public string Designation = "6204";
	public float BoreMm;
	public float OutsideDiaMm;
	public float WidthMm;
	public string Standard = "ISO 15";
	public string SolidDescription = "";
}

/// <summary>O-ring, ISO 3601-1 size, with nominal static face-seal groove guidance (ISO 3601-2).</summary>
public sealed class ORingSpec
{
	public float InsideDiaMm;
	public float CrossSectionMm;
	public float OutsideDiaMm;
	/// <summary>Nominal groove depth for a static face seal: ~0.75 x cross-section (75% squeeze is the middle of the ISO 3601-2 static range).</summary>
	public float GrooveDepthMm;
	/// <summary>Nominal groove width: ~1.35 x cross-section, leaving room for the squeezed ring.</summary>
	public float GrooveWidthMm;
	public string Standard = "ISO 3601";
	public string SolidDescription = "";
}

/// <summary>
/// Parametric standard-part generators: ISO hex bolts, socket heads, nuts, washers, bearings
/// and O-rings, plus linear / circular patterns and mirroring over placement lists.
///
/// ALL LENGTHS ARE MILLIMETRES. Every spec carries its driving dimensions and the standard
/// they came from, and says plainly that its solid is spec-only until the B-rep core lands:
/// this library computes what a shop orders and what a drawing dimensions. It does not pretend
/// to have cut a bolt out of a mesh. The counterbore / clearance numbers here agree with
/// <see cref="IsoMetric"/> by construction — both read the same published tables, and the
/// tests check the agreement (M6 bolt head 10 mm across flats sits in a 10 mm counterbore).
/// </summary>
public static class FastenerLibrary
{
	static readonly (string Size, float Nominal, float AcrossFlats, float HeadHeight)[] HexBolts =
	{
		("M3", 3, 5.5f, 2.0f), ("M4", 4, 7f, 2.8f), ("M5", 5, 8f, 3.5f), ("M6", 6, 10f, 4.0f),
		("M8", 8, 13f, 5.3f), ("M10", 10, 16f, 6.4f), ("M12", 12, 18f, 7.5f),
		("M16", 16, 24f, 10f), ("M20", 20, 30f, 12.5f),
	};

	static readonly (string Size, float Nominal, float HeadDia, float HeadHeight, float Socket)[] SocketHeads =
	{
		("M3", 3, 5.5f, 3f, 2.5f), ("M4", 4, 7f, 4f, 3f), ("M5", 5, 8.5f, 5f, 4f),
		("M6", 6, 10f, 6f, 5f), ("M8", 8, 13f, 8f, 6f), ("M10", 10, 16f, 10f, 8f),
		("M12", 12, 18f, 11f, 10f), ("M16", 16, 24f, 15f, 14f), ("M20", 20, 30f, 19f, 17f),
	};

	static readonly (string Size, float Nominal, float AcrossFlats, float Height)[] Nuts =
	{
		("M3", 3, 5.5f, 2.4f), ("M4", 4, 7f, 3.2f), ("M5", 5, 8f, 4.7f), ("M6", 6, 10f, 5f),
		("M8", 8, 13f, 6.8f), ("M10", 10, 16f, 8.4f), ("M12", 12, 18f, 10.8f),
		("M16", 16, 24f, 14.8f), ("M20", 20, 30f, 18f),
	};

	static readonly (string Size, float Nominal, float Inner, float Outer, float Thick)[] Washers =
	{
		("M3", 3, 3.2f, 7f, 0.5f), ("M4", 4, 4.3f, 9f, 0.8f), ("M5", 5, 5.3f, 10f, 1f),
		("M6", 6, 6.4f, 12f, 1.6f), ("M8", 8, 8.4f, 16f, 1.6f), ("M10", 10, 10.5f, 20f, 2f),
		("M12", 12, 13f, 24f, 2.5f), ("M16", 16, 17f, 30f, 3f), ("M20", 20, 21f, 37f, 3f),
	};

	static readonly (string Designation, float Bore, float Od, float Width)[] Bearings =
	{
		("6003", 17, 35, 10), ("6004", 20, 42, 12), ("6005", 25, 47, 12), ("6006", 30, 55, 13),
		("6204", 20, 47, 14), ("6205", 25, 52, 15), ("6206", 30, 62, 16), ("6305", 25, 62, 17),
	};

	static readonly (float Id, float Cs)[] ORings =
	{
		(10, 2), (20, 2), (30, 2), (50, 2), (10, 2.5f), (20, 2.5f),
		(30, 3), (50, 3), (20, 4), (50, 5), (100, 5),
	};

	public static BoltSpec HexBolt( string size, float lengthMm )
	{
		var row = HexBolts.FirstOrDefault( r => string.Equals( r.Size, size, StringComparison.OrdinalIgnoreCase ) );
		if ( row.Size is null )
			throw new InvalidOperationException( $"No ISO 4014 hex bolt {size}. Available: {string.Join( ", ", HexBolts.Select( r => r.Size ) )}." );
		if ( lengthMm <= 0f ) throw new InvalidOperationException( "A bolt needs a length greater than zero." );
		return new BoltSpec
		{
			Size = row.Size, NominalMm = row.Nominal, LengthMm = lengthMm,
			HeadAcrossFlatsMm = row.AcrossFlats, HeadHeightMm = row.HeadHeight, ShankDiaMm = row.Nominal,
			SolidDescription = $"ISO 4014 {row.Size}x{lengthMm:0.##} hex bolt, s={row.AcrossFlats:0.##}, k={row.HeadHeight:0.##} — spec only; B-rep solid is the OCCT ticket's.",
		};
	}

	public static SocketHeadSpec SocketHeadCap( string size, float lengthMm )
	{
		var row = SocketHeads.FirstOrDefault( r => string.Equals( r.Size, size, StringComparison.OrdinalIgnoreCase ) );
		if ( row.Size is null )
			throw new InvalidOperationException( $"No ISO 4762 socket-head cap screw {size}. Available: {string.Join( ", ", SocketHeads.Select( r => r.Size ) )}." );
		if ( lengthMm <= 0f ) throw new InvalidOperationException( "A screw needs a length greater than zero." );
		return new SocketHeadSpec
		{
			Size = row.Size, NominalMm = row.Nominal, LengthMm = lengthMm,
			HeadDiaMm = row.HeadDia, HeadHeightMm = row.HeadHeight, SocketAcrossFlatsMm = row.Socket,
			SolidDescription = $"ISO 4762 {row.Size}x{lengthMm:0.##} socket-head cap screw, dk={row.HeadDia:0.##}, k={row.HeadHeight:0.##}, socket={row.Socket:0.##} — spec only; B-rep solid is the OCCT ticket's.",
		};
	}

	public static NutSpec HexNut( string size )
	{
		var row = Nuts.FirstOrDefault( r => string.Equals( r.Size, size, StringComparison.OrdinalIgnoreCase ) );
		if ( row.Size is null )
			throw new InvalidOperationException( $"No ISO 4032 hex nut {size}. Available: {string.Join( ", ", Nuts.Select( r => r.Size ) )}." );
		return new NutSpec
		{
			Size = row.Size, NominalMm = row.Nominal, AcrossFlatsMm = row.AcrossFlats, HeightMm = row.Height,
			SolidDescription = $"ISO 4032 {row.Size} hex nut, s={row.AcrossFlats:0.##}, m={row.Height:0.##} — spec only; B-rep solid is the OCCT ticket's.",
		};
	}

	public static WasherSpec Washer( string size )
	{
		var row = Washers.FirstOrDefault( r => string.Equals( r.Size, size, StringComparison.OrdinalIgnoreCase ) );
		if ( row.Size is null )
			throw new InvalidOperationException( $"No ISO 7089 washer {size}. Available: {string.Join( ", ", Washers.Select( r => r.Size ) )}." );
		return new WasherSpec
		{
			Size = row.Size, NominalMm = row.Nominal, InnerDiaMm = row.Inner, OuterDiaMm = row.Outer, ThicknessMm = row.Thick,
			SolidDescription = $"ISO 7089 {row.Size} washer, d1={row.Inner:0.##}, d2={row.Outer:0.##}, s={row.Thick:0.##} — spec only; B-rep solid is the OCCT ticket's.",
		};
	}

	public static BearingSpec Bearing( string designation )
	{
		var row = Bearings.FirstOrDefault( r => string.Equals( r.Designation, designation, StringComparison.OrdinalIgnoreCase ) );
		if ( row.Designation is null )
			throw new InvalidOperationException( $"No bearing {designation} in the table. Available: {string.Join( ", ", Bearings.Select( r => r.Designation ) )}." );
		return new BearingSpec
		{
			Designation = row.Designation, BoreMm = row.Bore, OutsideDiaMm = row.Od, WidthMm = row.Width,
			SolidDescription = $"Deep-groove ball bearing {row.Designation}: {row.Bore:0.##}x{row.Od:0.##}x{row.Width:0.##} — spec only; B-rep solid is the OCCT ticket's.",
		};
	}

	public static ORingSpec ORing( float insideDiaMm, float crossSectionMm )
	{
		var row = ORings.FirstOrDefault( r => Math.Abs( r.Id - insideDiaMm ) < 1e-6f && Math.Abs( r.Cs - crossSectionMm ) < 1e-6f );
		if ( row == default )
			throw new InvalidOperationException(
				$"No ISO 3601 O-ring {insideDiaMm:0.##}x{crossSectionMm:0.##} in the table. " +
				$"Available: {string.Join( ", ", ORings.Select( r => $"{r.Id:0.##}x{r.Cs:0.##}" ) )}." );
		return new ORingSpec
		{
			InsideDiaMm = row.Id, CrossSectionMm = row.Cs,
			OutsideDiaMm = row.Id + 2f * row.Cs,
			GrooveDepthMm = row.Cs * 0.75f,
			GrooveWidthMm = row.Cs * 1.35f,
			SolidDescription = $"ISO 3601 O-ring {row.Id:0.##}x{row.Cs:0.##} — spec only; B-rep solid is the OCCT ticket's.",
		};
	}

	/// <summary>Linear pattern: every seed repeated <paramref name="count"/> times along <paramref name="direction"/>, spaced <paramref name="spacingMm"/> apart. Count includes the seed.</summary>
	public static List<Vec3> PatternLinear( IReadOnlyList<Vec3> seeds, Vec3 direction, int count, float spacingMm )
	{
		if ( seeds is null ) throw new ArgumentNullException( nameof( seeds ) );
		if ( count < 1 ) throw new InvalidOperationException( "A pattern needs at least 1 instance." );
		if ( spacingMm <= 0f ) throw new InvalidOperationException( "A linear pattern needs spacing greater than zero." );
		if ( direction.LengthSquared < 1e-12f ) throw new InvalidOperationException( "A linear pattern needs a direction." );
		var dir = direction.Normal;
		var out_ = new List<Vec3>( seeds.Count * count );
		for ( var i = 0; i < count; i++ )
			foreach ( var seed in seeds )
				out_.Add( new Vec3( seed.x + dir.x * spacingMm * i, seed.y + dir.y * spacingMm * i, seed.z + dir.z * spacingMm * i ) );
		return out_;
	}

	/// <summary>Circular pattern: every seed rotated about the axis through <paramref name="axisPoint"/> along <paramref name="axis"/>, <paramref name="count"/> times over <paramref name="angleDeg"/> (default a full turn). Count includes the seed.</summary>
	public static List<Vec3> PatternCircular( IReadOnlyList<Vec3> seeds, Vec3 axisPoint, Vec3 axis, int count, float angleDeg = 360f )
	{
		if ( seeds is null ) throw new ArgumentNullException( nameof( seeds ) );
		if ( count < 1 ) throw new InvalidOperationException( "A pattern needs at least 1 instance." );
		if ( axis.LengthSquared < 1e-12f ) throw new InvalidOperationException( "A circular pattern needs an axis." );
		var out_ = new List<Vec3>( seeds.Count * count );
		for ( var i = 0; i < count; i++ )
		{
			var step = Xform.RotateAbout( axisPoint, axis.Normal, angleDeg * MathF.PI / 180f * i / count );
			foreach ( var seed in seeds )
				out_.Add( step.TransformPoint( seed ) );
		}
		return out_;
	}

	/// <summary>Mirror every placement across the plane through <paramref name="planePoint"/> with normal <paramref name="planeNormal"/>. Returns the mirrored copies only, not the seeds.</summary>
	public static List<Vec3> MirrorPlacements( IReadOnlyList<Vec3> seeds, Vec3 planePoint, Vec3 planeNormal )
	{
		if ( seeds is null ) throw new ArgumentNullException( nameof( seeds ) );
		if ( planeNormal.LengthSquared < 1e-12f ) throw new InvalidOperationException( "Mirroring needs a plane normal." );
		var n = planeNormal.Normal;
		var out_ = new List<Vec3>( seeds.Count );
		foreach ( var seed in seeds )
		{
			var d = Vec3.Dot( seed - planePoint, n );
			out_.Add( new Vec3( seed.x - 2f * d * n.x, seed.y - 2f * d * n.y, seed.z - 2f * d * n.z ) );
		}
		return out_;
	}
}
