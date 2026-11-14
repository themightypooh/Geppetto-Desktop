using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// ISO metric thread data, coarse pitch (ISO 261 / ISO 262).
///
/// ALL LENGTHS IN THIS FILE ARE MILLIMETRES, the unit ISO metric threads are specified in.
/// The kernel's "u" is unit-agnostic; a studio authored in inches converts at the call site
/// (25.4 mm/in) before asking for a hole. Nothing here silently mixes the two.
///
/// Tap drill is D - pitch (the standard 75%-thread drill). Clearances are ISO 273
/// (fine / medium / coarse). Counterbore diameter and depth are the normal series for a
/// socket-head cap screw to ISO 4762. Countersink diameter is the ISO 10642 flat-head
/// diameter, angle 90 degrees. Values are the published standard tables, not fitted.
/// </summary>
public sealed class MetricThread
{
	public string Size = "M6";
	public float NominalMm;
	public float PitchMm;
	public float TapDrillMm;
	public float ClearanceFineMm;
	public float ClearanceMediumMm;
	public float ClearanceCoarseMm;
	public float CounterboreDiaMm;
	public float CounterboreDepthMm;
	public float CountersinkDiaMm;
	public float CountersinkAngleDeg = 90f;
}

/// <summary>How a hole's thread is carried: none, a cosmetic callout, or a modelled thread.</summary>
public enum HoleThreadMode
{
	None,
	Cosmetic,
	Modelled,
}

/// <summary>
/// Whether the hole was actually cut, and by what.
///
/// A MESH BOOLEAN cut is what this kernel can do today (HoleOperation + MeshBoolean, the same
/// path HoleFeature uses): the tool solid is faceted and the result is a mesh solid, not a
/// B-rep solid. A B-rep cut — the solid edited as a solid, STEP round-tripped exactly — is the
/// OCCT ticket's (godlike-occt-brep-core-step-export) and is reported as BrepPending, never
/// claimed. NotCut is the state of a spec that has only been computed, never executed.
/// </summary>
public enum HoleCutStatus
{
	NotCut,
	MeshBooleanCut,
	BrepPending,
}

/// <summary>
/// One Hole Wizard hole, computed from a metric size and its options. This is the parametric
/// record: it survives a rebuild because it is recomputed from the parameters, and the
/// B-rep core will consume exactly this record to cut the real solid.
/// </summary>
public sealed class HoleSpec
{
	public string Size = "M6";
	public HoleStyle Style;
	public HoleThreadMode ThreadMode;
	public bool IsThrough;
	/// <summary>Diameter of the shaft that is actually cut. Tap drill for a thread, clearance otherwise.</summary>
	public float ShaftDiameterMm;
	public float TapDrillMm;
	public float PitchMm;
	/// <summary>Depth the thread runs to. 0 on a through hole means "full thickness".</summary>
	public float ThreadDepthMm;
	/// <summary>Depth the drill runs to. 0 on a through hole means "through everything".</summary>
	public float DrillDepthMm;
	public float HeadDiameterMm;
	public float HeadDepthMm;
	public float SinkAngleDeg = 90f;
	/// <summary>Depth the countersink cone runs to, from the two diameters and the angle.</summary>
	public float SinkDepthMm;
	public Vec3 Centre;
	public Vec3 Direction = new( 0, 0, -1 );
	public HoleCutStatus CutStatus = HoleCutStatus.NotCut;
	/// <summary>For a modelled thread: the helix the B-rep core sweeps (major dia, pitch, depth). The mesh cut is the tap drill.</summary>
	public string ThreadNote = "";

	public string Label => ThreadMode == HoleThreadMode.None
		? $"{Size} clearance Ø{ShaftDiameterMm:0.##}"
		: $"{Size}x{PitchMm:0.##} tap drill Ø{TapDrillMm:0.##}";
}

/// <summary>
/// The ISO metric coarse tables (ISO 261/262 tap drills, ISO 273 clearances, ISO 4762
/// counterbores, ISO 10642 countersinks) plus the ISO 286-2 H7 bore tolerance, which the
/// drawing workspace reads from here so the hole and its tolerance can never disagree.
/// </summary>
public static class IsoMetric
{
	public static readonly string[] Sizes = { "M3", "M4", "M5", "M6", "M8", "M10", "M12", "M16", "M20" };

	static readonly MetricThread[] Table =
	{
		//            nominal pitch tap   fine  med   coarse cbore cbore  csink
		//                                  clr   clr   clr    dia  depth  dia
		new() { Size = "M3",  NominalMm = 3,  PitchMm = 0.5f,  TapDrillMm = 2.5f,  ClearanceFineMm = 3.2f,  ClearanceMediumMm = 3.4f,  ClearanceCoarseMm = 3.6f,  CounterboreDiaMm = 6f,    CounterboreDepthMm = 3f,  CountersinkDiaMm = 6f },
		new() { Size = "M4",  NominalMm = 4,  PitchMm = 0.7f,  TapDrillMm = 3.3f,  ClearanceFineMm = 4.3f,  ClearanceMediumMm = 4.5f,  ClearanceCoarseMm = 4.8f,  CounterboreDiaMm = 7f,    CounterboreDepthMm = 4f,  CountersinkDiaMm = 8f },
		new() { Size = "M5",  NominalMm = 5,  PitchMm = 0.8f,  TapDrillMm = 4.2f,  ClearanceFineMm = 5.3f,  ClearanceMediumMm = 5.5f,  ClearanceCoarseMm = 5.8f,  CounterboreDiaMm = 8.5f,  CounterboreDepthMm = 5f,  CountersinkDiaMm = 10f },
		new() { Size = "M6",  NominalMm = 6,  PitchMm = 1.0f,  TapDrillMm = 5.0f,  ClearanceFineMm = 6.4f,  ClearanceMediumMm = 6.6f,  ClearanceCoarseMm = 7.0f,  CounterboreDiaMm = 10f,   CounterboreDepthMm = 6f,  CountersinkDiaMm = 11f },
		new() { Size = "M8",  NominalMm = 8,  PitchMm = 1.25f, TapDrillMm = 6.8f,  ClearanceFineMm = 8.4f,  ClearanceMediumMm = 9.0f,  ClearanceCoarseMm = 10.0f, CounterboreDiaMm = 13f,   CounterboreDepthMm = 8f,  CountersinkDiaMm = 15f },
		new() { Size = "M10", NominalMm = 10, PitchMm = 1.5f,  TapDrillMm = 8.5f,  ClearanceFineMm = 10.5f, ClearanceMediumMm = 11.0f, ClearanceCoarseMm = 12.0f, CounterboreDiaMm = 16f,   CounterboreDepthMm = 10f, CountersinkDiaMm = 18f },
		new() { Size = "M12", NominalMm = 12, PitchMm = 1.75f, TapDrillMm = 10.2f, ClearanceFineMm = 13.0f, ClearanceMediumMm = 13.5f, ClearanceCoarseMm = 14.5f, CounterboreDiaMm = 18f,   CounterboreDepthMm = 11f, CountersinkDiaMm = 21f },
		new() { Size = "M16", NominalMm = 16, PitchMm = 2.0f,  TapDrillMm = 14.0f, ClearanceFineMm = 17.0f, ClearanceMediumMm = 17.5f, ClearanceCoarseMm = 18.5f, CounterboreDiaMm = 24f,   CounterboreDepthMm = 15f, CountersinkDiaMm = 28f },
		new() { Size = "M20", NominalMm = 20, PitchMm = 2.5f,  TapDrillMm = 17.5f, ClearanceFineMm = 21.0f, ClearanceMediumMm = 22.0f, ClearanceCoarseMm = 24.0f, CounterboreDiaMm = 30f,   CounterboreDepthMm = 19f, CountersinkDiaMm = 34f },
	};

	public static MetricThread Coarse( string size )
	{
		var found = Table.FirstOrDefault( t => string.Equals( t.Size, size, StringComparison.OrdinalIgnoreCase ) );
		if ( found is null )
			throw new InvalidOperationException(
				$"No ISO metric coarse thread {size}. Available: {string.Join( ", ", Sizes )}." );
		return found;
	}

	public static bool TryCoarse( string size, out MetricThread thread )
	{
		thread = Table.FirstOrDefault( t => string.Equals( t.Size, size, StringComparison.OrdinalIgnoreCase ) );
		return thread is not null;
	}

	/// <summary>
	/// ISO 286-2 H7 upper deviation (lower is 0) for a bore of this nominal diameter, in mm.
	/// Null when the diameter is outside the table (over 50 mm) — the caller must say so rather
	/// than invent a tolerance.
	/// </summary>
	public static float? H7UpperDeviationMm( float nominalMm )
	{
		if ( nominalMm <= 0f ) return null;
		if ( nominalMm <= 3f ) return 0.010f;
		if ( nominalMm <= 6f ) return 0.012f;
		if ( nominalMm <= 10f ) return 0.015f;
		if ( nominalMm <= 18f ) return 0.018f;
		if ( nominalMm <= 30f ) return 0.021f;
		if ( nominalMm <= 50f ) return 0.025f;
		return null;
	}

	public static string H7Text( float nominalMm )
	{
		var upper = H7UpperDeviationMm( nominalMm );
		return upper is null
			? $"Ø{nominalMm:0.##} H7 (tolerance table ends at 50 mm)"
			: $"Ø{nominalMm:0.##} H7 (+{upper.Value:0.000}/0)";
	}
}

/// <summary>
/// The Hole Wizard: a parametric ISO metric hole (counterbore / countersink, modelled or
/// cosmetic thread, through or blind) on top of the hole this kernel already knows how to cut.
///
/// WHAT THIS ADDS OVER HoleFeature: HoleFeature takes raw diameters from the dialog. This one
/// takes "M6, counterbore, cosmetic thread" and derives every number from the ISO tables, so a
/// drawing, a hole table and a tap drill can never disagree with each other — they all read the
/// same HoleSpec.
///
/// WHAT IT DOES NOT DO: it cuts with the mesh boolean (HoleOperation + MeshBoolean), exactly
/// like HoleFeature. The result is a faceted mesh solid, and STEP export of it is a faceted
/// shell until the OCCT B-rep core (godlike-occt-brep-core-step-export) replays the spec as a
/// real solid. A modelled thread is recorded as a helix profile in the spec; the mesh cut is
/// the tap drill, and the helix sweep is the B-rep core's, not a second meshed helix here.
/// </summary>
public sealed class HoleWizardFeature : Feature
{
	public override string TypeName => "Hole Wizard";

	public override GeometryKind Accepts => GeometryKind.Face;

	/// <summary>Where the holes go. One per picked face, drilled along that face's own normal.</summary>
	public List<FaceRef> Faces = new();

	public readonly ChoiceParam Size = new( "Size", IsoMetric.Sizes );
	public readonly ChoiceParam Style = new( "Style", new[] { "Simple", "Counterbore", "Countersink" } );
	public readonly ChoiceParam Thread = new( "Thread", new[] { "None", "Cosmetic", "Modelled" } );
	public readonly ChoiceParam DepthKind = new( "Depth", new[] { "Through", "Blind" } );
	public readonly ChoiceParam Clearance = new( "Clearance", new[] { "Fine", "Medium", "Coarse" } );

	/// <summary>Blind depth in mm. Ignored (must be 0) for a through hole.</summary>
	public readonly FloatParam Depth = new( "Blind depth", 0f, 0f, unit: "mm" );

	/// <summary>Width of the face the hole sits on, when known. A counterbore wider than its face is refused, not cut.</summary>
	public readonly FloatParam FaceWidth = new( "Face width (0 = unknown)", 0f, 0f, unit: "mm" );

	public readonly IntParam Segments = new( "Segments", 24, 6, 256 );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Size, Style, Thread, DepthKind, Depth, Clearance, FaceWidth, Segments };

	/// <summary>The spec this rebuild computed, one per drilled face. Empty before the first run.</summary>
	public List<HoleSpec> LastSpecs = new();

	/// <summary>How many holes were cut this rebuild.</summary>
	public int HolesCut;

	/// <summary>
	/// Validate one hole without computing it. Empty means the hole is sound. Every refusal is
	/// an explicit string — a bad hole is refused out loud, never silently built wrong.
	/// </summary>
	public static IReadOnlyList<string> Validate( MetricThread thread, HoleStyle style,
		HoleThreadMode threadMode, bool through, float depthMm, float? faceWidthMm = null )
	{
		var errors = new List<string>();
		if ( thread is null ) { errors.Add( "No metric thread selected." ); return errors; }
		if ( !through )
		{
			if ( depthMm <= 0f ) errors.Add( "A blind hole needs a depth greater than zero." );
			if ( threadMode != HoleThreadMode.None )
			{
				// The drill point leaves ~1.5 pitches of incomplete thread at the bottom.
				var minDepth = thread.PitchMm * 1.5f + 0.5f;
				if ( depthMm > 0f && depthMm < minDepth )
					errors.Add( $"A blind {thread.Size} thread needs at least {minDepth:0.##} mm of depth " +
						$"(1.5 pitches of drill point plus 0.5 mm of full thread); got {depthMm:0.##} mm." );
			}
		}
		if ( style == HoleStyle.Counterbore && faceWidthMm is > 0f && thread.CounterboreDiaMm >= faceWidthMm.Value )
			errors.Add( $"A {thread.Size} counterbore is Ø{thread.CounterboreDiaMm:0.##} mm, which does not fit " +
				$"on a {faceWidthMm.Value:0.##} mm wide face." );
		if ( style == HoleStyle.Countersink && faceWidthMm is > 0f && thread.CountersinkDiaMm >= faceWidthMm.Value )
			errors.Add( $"A {thread.Size} countersink is Ø{thread.CountersinkDiaMm:0.##} mm, which does not fit " +
				$"on a {faceWidthMm.Value:0.##} mm wide face." );
		return errors;
	}

	/// <summary>
	/// Compute one hole from a metric size and its options. Throws InvalidOperationException on
	/// the first validation error — see <see cref="Validate"/> for the error list.
	/// </summary>
	public static HoleSpec Compute( MetricThread thread, HoleStyle style, HoleThreadMode threadMode,
		bool through, float depthMm, string clearance, Vec3 centre, Vec3 direction, float? faceWidthMm = null )
	{
		var errors = Validate( thread, style, threadMode, through, depthMm, faceWidthMm );
		if ( errors.Count > 0 ) throw new InvalidOperationException( errors[0] );

		var shaft = threadMode == HoleThreadMode.None
			? clearance switch
			{
				"Fine" => thread.ClearanceFineMm,
				"Coarse" => thread.ClearanceCoarseMm,
				_ => thread.ClearanceMediumMm,
			}
			: thread.TapDrillMm;

		var spec = new HoleSpec
		{
			Size = thread.Size,
			Style = style,
			ThreadMode = threadMode,
			IsThrough = through,
			ShaftDiameterMm = shaft,
			TapDrillMm = thread.TapDrillMm,
			PitchMm = thread.PitchMm,
			DrillDepthMm = through ? 0f : depthMm,
			ThreadDepthMm = through ? 0f : Math.Max( 0f, depthMm - thread.PitchMm * 1.5f ),
			HeadDiameterMm = style switch
			{
				HoleStyle.Counterbore => thread.CounterboreDiaMm,
				HoleStyle.Countersink => thread.CountersinkDiaMm,
				_ => 0f,
			},
			HeadDepthMm = style == HoleStyle.Counterbore ? thread.CounterboreDepthMm : 0f,
			SinkAngleDeg = style == HoleStyle.Countersink ? thread.CountersinkAngleDeg : 0f,
			Centre = centre,
			Direction = direction.LengthSquared < 1e-12f ? new Vec3( 0, 0, -1 ) : direction.Normal,
		};

		if ( style == HoleStyle.Countersink )
		{
			var half = spec.SinkAngleDeg * 0.5f * MathF.PI / 180f;
			spec.SinkDepthMm = (spec.HeadDiameterMm - spec.ShaftDiameterMm) * 0.5f / MathF.Tan( half );
		}

		if ( threadMode == HoleThreadMode.Modelled )
			spec.ThreadNote = $"Modelled thread {thread.Size}x{thread.PitchMm:0.##}: major Ø{thread.NominalMm:0.##}, " +
				$"minor (tap drill) Ø{thread.TapDrillMm:0.##}, helix sweep is the B-rep core's; mesh cut is the tap drill.";
		else if ( threadMode == HoleThreadMode.Cosmetic )
			spec.ThreadNote = $"Cosmetic thread {thread.Size}x{thread.PitchMm:0.##}: callout only, cut at tap drill Ø{thread.TapDrillMm:0.##}.";

		return spec;
	}

	/// <summary>Parameters as a plain string dictionary, for a document writer and for tests.</summary>
	public Dictionary<string, string> SaveParameters()
	{
		var inv = CultureInfo.InvariantCulture;
		return new Dictionary<string, string>
		{
			["Size"] = Size.Value,
			["Style"] = Style.Value,
			["Thread"] = Thread.Value,
			["DepthKind"] = DepthKind.Value,
			["DepthMm"] = Depth.Value.ToString( inv ),
			["Clearance"] = Clearance.Value,
			["FaceWidthMm"] = FaceWidth.Value.ToString( inv ),
			["Segments"] = Segments.Value.ToString( inv ),
		};
	}

	/// <summary>Restore parameters saved by <see cref="SaveParameters"/>. Unknown values are refused, not defaulted silently.</summary>
	public void LoadParameters( IReadOnlyDictionary<string, string> values )
	{
		foreach ( var (key, value) in values )
		{
			switch ( key )
			{
				case "Size": SetChoice( Size, value ); break;
				case "Style": SetChoice( Style, value ); break;
				case "Thread": SetChoice( Thread, value ); break;
				case "DepthKind": SetChoice( DepthKind, value ); break;
				case "Clearance": SetChoice( Clearance, value ); break;
				case "DepthMm": Depth.Value = ParseFloat( value, "DepthMm" ); break;
				case "FaceWidthMm": FaceWidth.Value = ParseFloat( value, "FaceWidthMm" ); break;
				case "Segments": Segments.Value = ParseInt( value, "Segments" ); break;
				default: throw new InvalidOperationException( $"Unknown Hole Wizard parameter '{key}'." );
			}
		}
	}

	static void SetChoice( ChoiceParam param, string value )
	{
		var index = Array.IndexOf( param.Options, value );
		if ( index < 0 )
			throw new InvalidOperationException( $"'{value}' is not a {param.Label} option ({string.Join( ", ", param.Options )})." );
		param.Index = index;
	}

	static float ParseFloat( string value, string name )
	{
		if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed ) )
			throw new InvalidOperationException( $"'{value}' is not a number for {name}." );
		return parsed;
	}

	static int ParseInt( string value, string name )
	{
		if ( !int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed ) )
			throw new InvalidOperationException( $"'{value}' is not an integer for {name}." );
		return parsed;
	}

	protected override void Execute( FeatureContext ctx )
	{
		LastSpecs = new List<HoleSpec>();
		HolesCut = 0;

		if ( Faces.Count == 0 )
			Fail(
				"No faces picked - click where the holes should go",
				"A Hole Wizard hole is drilled into a face along that face's own normal, and none have been chosen yet.",
				"Click a face in the viewport" );

		if ( !MeshBoolean.Available )
			Fail(
				"Drilling a hole needs the engine's boolean",
				"Taking material away means recomputing the surface, and no boolean provider is installed.",
				"Run this inside the s&box editor, where the provider is available" );

		var thread = IsoMetric.Coarse( Size.Value );
		var style = Style.Index switch { 1 => HoleStyle.Counterbore, 2 => HoleStyle.Countersink, _ => HoleStyle.Simple };
		var threadMode = Thread.Index switch { 1 => HoleThreadMode.Cosmetic, 2 => HoleThreadMode.Modelled, _ => HoleThreadMode.None };
		var through = DepthKind.Index == 0;
		var faceWidth = FaceWidth.Value > 0f ? FaceWidth.Value : (float?)null;

		var errors = Validate( thread, style, threadMode, through, Depth.Value, faceWidth );
		if ( errors.Count > 0 )
			Fail( "This Hole Wizard hole is not sound", errors[0], "Change the size, depth or style" );

		var drilled = 0;
		var lost = 0;

		foreach ( var reference in Faces )
		{
			if ( !FacePlane.TryResolveFace( ctx.Bodies, reference, out var body, out var faceIndex ) )
			{
				lost++;
				continue;
			}

			var mesh = body.Mesh;
			var face = mesh.Faces[faceIndex];
			var normal = mesh.FaceNormal( face );
			if ( normal.LengthSquared < 1e-16f ) { lost++; continue; }

			var spec = Compute( thread, style, threadMode, through, Depth.Value, Clearance.Value,
				reference.Point, normal.Normal * -1f, faceWidth );
			LastSpecs.Add( spec );

			PolyMesh tool;
			try
			{
				tool = HoleOperation.Build( style, reference.Point, spec.Direction,
					spec.ShaftDiameterMm, spec.DrillDepthMm,
					spec.HeadDiameterMm, spec.HeadDepthMm, spec.SinkAngleDeg,
					body.Mesh.BoundsDiagonal, Segments.Clamped );
			}
			catch ( InvalidOperationException e )
			{
				Fail( "This hole cannot be built", e.Message, "Make the head wider than the shaft", "Check the diameter and depth" );
				return;
			}

			body.Mesh = MeshBoolean.Apply( BooleanOp.Subtract, body.Mesh, tool );
			spec.CutStatus = HoleCutStatus.MeshBooleanCut;
			drilled++;
			SeparatePieces( ctx, body );
		}

		HolesCut = drilled;

		if ( drilled == 0 )
			Fail(
				"None of the picked faces are on the model any more",
				$"All {Faces.Count} of them named geometry the features above this one no longer produce.",
				"Pick the faces again on the current model" );

		Note = $"{drilled} x {LastSpecs[0].Label} ({LastSpecs[0].Style}, {LastSpecs[0].ThreadMode}) cut by mesh boolean; " +
			"B-rep solid edit and STEP round-trip are the OCCT ticket's.";
		if ( lost > 0 )
			Warn(
				$"{lost} of {Faces.Count} picked faces are no longer on the model",
				"They named geometry the features above this one no longer produce, so they were skipped.",
				"Pick them again on the current model" );
	}
}
