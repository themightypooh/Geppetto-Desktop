using System;

namespace Effigy;

/// <summary>
/// Canonical S&amp;box human proportion scaffold (Lane 2, second half).
///
/// This file is the scaffold ONLY. The geometry health checks (watertightness,
/// boundary edges, disjoint bodies, inverted normals) belong to GeometryLinter.cs,
/// owned by another agent — nothing in here checks a mesh.
///
/// Spec numbers (docs/SBOX-NATIVE-AI-ASSET-MAKER-LANES.md, Lane 2):
/// human standard 69 inches (1.75 m rounded; exactly 69 x 0.0254 = 1.7526 m),
/// 7.5 heads tall, Head 0.13-0.16 of height, Torso 0.30, Legs 0.50,
/// arm span approximately equal to height.
///
/// Bands that the spec does NOT pin are marked as this file's own tolerance
/// choice, not spec numbers: Torso and Legs get +/-0.03 around their nominal
/// (Torso 0.27-0.33, Legs 0.47-0.53) and arm span gets +/-5% (0.95-1.05),
/// because "approximately height" needs a number before a status can be
/// nominal or off. Head keeps the spec's own 0.13-0.16 band.
///
/// Vertical tiling, derived from the spec numbers (not extra spec claims):
/// Feet 0, Hip 0.50H, Shoulder 0.80H (0.50 + 0.30 torso), HeadBottom
/// (1 - 1/7.5)H, HeadTop H. The gap between shoulder and head bottom is the
/// neck residual (~0.067H); the spec's three fractions sum to ~0.933, not 1,
/// so the scaffold does NOT pretend they partition the height on their own.
/// </summary>
public enum BodySegment
{
	Head,
	Torso,
	Legs,
	ArmSpan,
}

/// <summary>Per-segment verdict: inside its band, or outside it.</summary>
public enum ProportionStatus
{
	Nominal,
	Off,
}

/// <summary>A point in scaffold space. Deliberately not Vec3: this file has no
/// dependency on the rest of the kernel, so it compiles in an isolated runner
/// with only this file and its test file.</summary>
public readonly struct ScaffoldPoint
{
	public ScaffoldPoint( double x, double y, double z )
	{
		X = x;
		Y = y;
		Z = z;
	}

	public double X { get; }
	public double Y { get; }
	public double Z { get; }
}

/// <summary>One segment's nominal fraction of total height and its band.</summary>
public readonly struct SegmentDefinition
{
	public SegmentDefinition( BodySegment segment, double nominalRatio, double minRatio, double maxRatio )
	{
		Segment = segment;
		NominalRatio = nominalRatio;
		MinRatio = minRatio;
		MaxRatio = maxRatio;
	}

	public BodySegment Segment { get; }
	public double NominalRatio { get; }
	public double MinRatio { get; }
	public double MaxRatio { get; }

	public ProportionStatus StatusFor( double measuredRatio ) =>
		measuredRatio >= MinRatio && measuredRatio <= MaxRatio ? ProportionStatus.Nominal : ProportionStatus.Off;
}

/// <summary>One segment's measured fraction of height against its band.</summary>
public readonly struct SegmentReport
{
	public SegmentReport( BodySegment segment, double measuredRatio, double nominalRatio, double minRatio, double maxRatio )
	{
		Segment = segment;
		MeasuredRatio = measuredRatio;
		NominalRatio = nominalRatio;
		MinRatio = minRatio;
		MaxRatio = maxRatio;
	}

	public BodySegment Segment { get; }
	public double MeasuredRatio { get; }
	public double NominalRatio { get; }
	public double MinRatio { get; }
	public double MaxRatio { get; }
	public ProportionStatus Status => MeasuredRatio >= MinRatio && MeasuredRatio <= MaxRatio ? ProportionStatus.Nominal : ProportionStatus.Off;
}

/// <summary>Vertical landmarks of the scaffold, in the same units as the
/// height passed in (inches for the canonical scaffold).</summary>
public readonly struct ScaffoldLandmarks
{
	public ScaffoldLandmarks( double feet, double hip, double shoulder, double headBottom, double headTop )
	{
		Feet = feet;
		Hip = hip;
		Shoulder = shoulder;
		HeadBottom = headBottom;
		HeadTop = headTop;
	}

	public double Feet { get; }
	public double Hip { get; }
	public double Shoulder { get; }
	public double HeadBottom { get; }
	public double HeadTop { get; }

	/// <summary>Shoulder-to-head-bottom gap: the neck residual.</summary>
	public double NeckSpan => HeadBottom - Shoulder;

	/// <summary>Leg span, for the tiling check: hip minus feet.</summary>
	public double LegSpan => Hip - Feet;

	/// <summary>Torso span: shoulder minus hip.</summary>
	public double TorsoSpan => Shoulder - Hip;

	/// <summary>Head span: head top minus head bottom.</summary>
	public double HeadSpan => HeadTop - HeadBottom;
}

/// <summary>The whole verdict: the spec's JSON shape is
/// proportions { head_ratio, status }, with one status per segment.</summary>
public readonly struct ProportionReport
{
	public ProportionReport( double headRatio, ProportionStatus status, SegmentReport[] segments )
	{
		HeadRatio = headRatio;
		Status = status;
		Segments = segments;
	}

	/// <summary>Measured head height divided by total height.</summary>
	public double HeadRatio { get; }

	/// <summary>Nominal only when every segment is nominal, otherwise Off.</summary>
	public ProportionStatus Status { get; }

	public SegmentReport[] Segments { get; }
	public bool IsNominal => Status == ProportionStatus.Nominal;

	public SegmentReport ReportFor( BodySegment segment )
	{
		foreach ( var report in Segments )
		{
			if ( report.Segment == segment )
				return report;
		}

		throw new ArgumentException( $"No report for segment {segment}.", nameof( segment ) );
	}
}

public static class Proportions
{
	/// <summary>Canonical human height, inches (spec: 69 inches).</summary>
	public const double CanonicalHeightInches = 69.0;

	/// <summary>Exact metres for 69 inches; the spec rounds this to 1.75 m.</summary>
	public const double CanonicalHeightMeters = CanonicalHeightInches * 0.0254;

	/// <summary>Canonical human is 7.5 heads tall (spec).</summary>
	public const double HeadsTall = 7.5;

	/// <summary>Head nominal fraction = 1 / 7.5.</summary>
	public const double HeadRatioNominal = 1.0 / HeadsTall;

	/// <summary>Head band, spec numbers.</summary>
	public const double HeadRatioMin = 0.13;

	public const double HeadRatioMax = 0.16;

	/// <summary>Torso nominal, spec number. Band is this file's +/-0.03 choice.</summary>
	public const double TorsoRatioNominal = 0.30;

	public const double TorsoRatioMin = 0.27;
	public const double TorsoRatioMax = 0.33;

	/// <summary>Legs nominal, spec number. Band is this file's +/-0.03 choice.</summary>
	public const double LegsRatioNominal = 0.50;

	public const double LegsRatioMin = 0.47;
	public const double LegsRatioMax = 0.53;

	/// <summary>Arm span nominal = height. Band is this file's +/-5% choice
	/// for the spec's "approximately height".</summary>
	public const double ArmSpanRatioNominal = 1.0;

	public const double ArmSpanRatioMin = 0.95;
	public const double ArmSpanRatioMax = 1.05;

	public static SegmentDefinition[] Definitions() => new[]
	{
		new SegmentDefinition( BodySegment.Head, HeadRatioNominal, HeadRatioMin, HeadRatioMax ),
		new SegmentDefinition( BodySegment.Torso, TorsoRatioNominal, TorsoRatioMin, TorsoRatioMax ),
		new SegmentDefinition( BodySegment.Legs, LegsRatioNominal, LegsRatioMin, LegsRatioMax ),
		new SegmentDefinition( BodySegment.ArmSpan, ArmSpanRatioNominal, ArmSpanRatioMin, ArmSpanRatioMax ),
	};

	public static SegmentDefinition DefinitionFor( BodySegment segment )
	{
		foreach ( var definition in Definitions() )
		{
			if ( definition.Segment == segment )
				return definition;
		}

		throw new ArgumentException( $"Unknown segment {segment}.", nameof( segment ) );
	}

	/// <summary>Vertical scaffold landmarks for a body of the given height.
	/// Feet is always 0; every other landmark scales linearly with height.</summary>
	public static ScaffoldLandmarks LandmarksFor( double totalHeight )
	{
		RequirePositiveHeight( totalHeight );

		return new ScaffoldLandmarks(
			feet: 0.0,
			hip: LegsRatioNominal * totalHeight,
			shoulder: (LegsRatioNominal + TorsoRatioNominal) * totalHeight,
			headBottom: (1.0 - HeadRatioNominal) * totalHeight,
			headTop: totalHeight );
	}

	/// <summary>Landmarks for the canonical 69-inch scaffold.</summary>
	public static ScaffoldLandmarks CanonicalLandmarks() => LandmarksFor( CanonicalHeightInches );

	/// <summary>Judge measured segment heights against the scaffold bands.
	/// All heights must be in the same units; totalHeight must be positive
	/// and no measurement negative, or this throws rather than guessing.</summary>
	public static ProportionReport Evaluate( double headHeight, double torsoHeight, double legHeight, double armSpan, double totalHeight )
	{
		RequirePositiveHeight( totalHeight );
		RequireNonNegative( headHeight, nameof( headHeight ) );
		RequireNonNegative( torsoHeight, nameof( torsoHeight ) );
		RequireNonNegative( legHeight, nameof( legHeight ) );
		RequireNonNegative( armSpan, nameof( armSpan ) );

		var definitions = Definitions();
		var measured = new[] { headHeight / totalHeight, torsoHeight / totalHeight, legHeight / totalHeight, armSpan / totalHeight };
		var reports = new SegmentReport[definitions.Length];
		var allNominal = true;

		for ( var i = 0; i < definitions.Length; i++ )
		{
			reports[i] = new SegmentReport( definitions[i].Segment, measured[i], definitions[i].NominalRatio, definitions[i].MinRatio, definitions[i].MaxRatio );
			if ( reports[i].Status != ProportionStatus.Nominal )
				allNominal = false;
		}

		return new ProportionReport( measured[0], allNominal ? ProportionStatus.Nominal : ProportionStatus.Off, reports );
	}

	/// <summary>The canonical body judged against itself: must be all nominal.</summary>
	public static ProportionReport EvaluateCanonical() => Evaluate(
		CanonicalHeightInches / HeadsTall,
		TorsoRatioNominal * CanonicalHeightInches,
		LegsRatioNominal * CanonicalHeightInches,
		CanonicalHeightInches,
		CanonicalHeightInches );

	/// <summary>Normalise a point into a segment's box, per axis, into
	/// [0,1]^3. Deliberately NOT clamped: a point outside the box reads
	/// below 0 or above 1, which is exactly what a linter needs to see.
	/// Use <see cref="NormalizeClamped"/> when a clamped value is wanted.</summary>
	public static ScaffoldPoint Normalize( ScaffoldPoint point, ScaffoldPoint min, ScaffoldPoint max )
	{
		RequireExtent( min.X, max.X, nameof( min ) );
		RequireExtent( min.Y, max.Y, nameof( min ) );
		RequireExtent( min.Z, max.Z, nameof( min ) );

		return new ScaffoldPoint(
			(point.X - min.X) / (max.X - min.X),
			(point.Y - min.Y) / (max.Y - min.Y),
			(point.Z - min.Z) / (max.Z - min.Z) );
	}

	/// <summary>Same as <see cref="Normalize"/> but clamped into [0,1]^3.</summary>
	public static ScaffoldPoint NormalizeClamped( ScaffoldPoint point, ScaffoldPoint min, ScaffoldPoint max )
	{
		var raw = Normalize( point, min, max );
		return new ScaffoldPoint( Clamp01( raw.X ), Clamp01( raw.Y ), Clamp01( raw.Z ) );
	}

	/// <summary>True when every axis of a normalised point sits in [0,1].</summary>
	public static bool IsInsideNormalized( ScaffoldPoint normalized ) =>
		normalized.X >= 0.0 && normalized.X <= 1.0 &&
		normalized.Y >= 0.0 && normalized.Y <= 1.0 &&
		normalized.Z >= 0.0 && normalized.Z <= 1.0;

	static double Clamp01( double value ) => value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;

	static void RequirePositiveHeight( double totalHeight )
	{
		if ( double.IsNaN( totalHeight ) || totalHeight <= 0.0 )
			throw new ArgumentException( "Total height must be a positive number.", nameof( totalHeight ) );
	}

	static void RequireNonNegative( double value, string name )
	{
		if ( double.IsNaN( value ) || value < 0.0 )
			throw new ArgumentException( "Segment measurements must not be negative.", name );
	}

	static void RequireExtent( double min, double max, string name )
	{
		if ( double.IsNaN( min ) || double.IsNaN( max ) || max <= min )
			throw new ArgumentException( "A normalisation box needs a positive extent on every axis.", name );
	}
}
