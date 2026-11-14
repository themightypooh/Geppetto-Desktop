using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;

namespace Marionette.Desktop;

// AS09: slider joints with constrained travel.
// Ports Mobile app/AssemblySlider.cs (the pure solve half) into Desktop form,
// plus a thin command layer (AssemblySliderService) that routes edits through
// AssemblyHistory so each slider edit is one EditHistory entry.
//
// Travel is measured in document inches along the shared local +Z of the two
// connector frames -- the same axis the hinge rotates about. Travel zero is
// the pose where the two connector frames coincide in assembly space;
// positive travel moves occurrence B along +Z of that axis, negative travel
// moves it the opposite way.
//
// Anchor: occurrence A is the anchor -- its stored transform is authoritative
// and the solve only places B. Grounded-part state lives in AssemblyGrounding
// (AS03); the service layer refuses to slide a grounded B.
//
// Position: the current travel is stored on the joint itself as
// ExtensionData["position"] (JSON number, inches). That rides the existing
// document serializer, history checkpoints and save/reopen with no schema
// change.
//
// Asymmetry, preserved from Mobile -- do not unify:
//   TrySetPosition REJECTS travel outside the saved limits (last valid kept).
//   TrySolve CLAMPS travel outside the saved limits (a drag stops at its end,
//   reported via the clamped flag).
// No collision detection or physical simulation is implied anywhere here.
public static class AssemblySlider
{
	public const string PositionKey = "position";
	public const string SliderKind = "slider";

	static string Format( float value ) => value.ToString( "G", CultureInfo.InvariantCulture );

	/// <summary>Stored travel in inches, or 0 when the joint has no stored position.</summary>
	public static float GetPosition( AssemblyJoint joint ) =>
		joint != null && TryGetPosition( joint, out var position ) ? position : 0f;

	public static bool TryGetPosition( AssemblyJoint joint, out float position )
	{
		position = 0f;
		if ( joint?.ExtensionData == null ) return false;
		if ( !joint.ExtensionData.TryGetValue( PositionKey, out var stored ) ) return false;
		if ( stored.ValueKind != JsonValueKind.Number || !stored.TryGetSingle( out position ) ) return false;
		return float.IsFinite( position );
	}

	/// <summary>
	/// Validates a travel without storing it: the validate half of
	/// <see cref="TrySetPosition"/>, for callers (the service layer) that must
	/// check first and apply later inside one history entry. Same messages as
	/// TrySetPosition.
	/// </summary>
	public static bool TryValidatePosition( AssemblyJoint joint, float position, out string error )
	{
		error = null;
		if ( joint == null ) { error = "Slider joint is missing."; return false; }
		if ( joint.Kind != SliderKind ) { error = $"Joint '{joint.Id}' is not a slider joint."; return false; }
		if ( !float.IsFinite( position ) ) { error = "Travel must be a finite number of inches."; return false; }
		if ( !TryValidateLimits( joint, out error ) ) return false;
		if ( joint.Minimum.HasValue && position < joint.Minimum.Value )
		{
			error = joint.Maximum.HasValue
				? $"Travel {Format( position )} in is outside the saved limits [{Format( joint.Minimum.Value )}, {Format( joint.Maximum.Value )}] in."
				: $"Travel {Format( position )} in is below the saved minimum of {Format( joint.Minimum.Value )} in.";
			return false;
		}
		if ( joint.Maximum.HasValue && position > joint.Maximum.Value )
		{
			error = joint.Minimum.HasValue
				? $"Travel {Format( position )} in is outside the saved limits [{Format( joint.Minimum.Value )}, {Format( joint.Maximum.Value )}] in."
				: $"Travel {Format( position )} in is above the saved maximum of {Format( joint.Maximum.Value )} in.";
			return false;
		}
		return true;
	}

	/// <summary>
	/// Stores a travel, rejecting non-finite values, inverted limits and travel
	/// outside the saved limits. On failure nothing is stored, so the last
	/// valid position survives.
	/// </summary>
	public static bool TrySetPosition( AssemblyJoint joint, float position, out string error )
	{
		if ( !TryValidatePosition( joint, position, out error ) ) return false;
		joint.ExtensionData ??= new Dictionary<string, JsonElement>();
		joint.ExtensionData[PositionKey] = JsonSerializer.SerializeToElement( position );
		return true;
	}

	/// <summary>Travel clamped into the saved limits; unbounded sides stay open. Non-finite input clamps to 0.</summary>
	public static float ClampToLimits( AssemblyJoint joint, float position )
	{
		if ( !float.IsFinite( position ) ) return 0f;
		if ( joint == null ) return position;
		if ( joint.Minimum.HasValue && position < joint.Minimum.Value ) position = joint.Minimum.Value;
		if ( joint.Maximum.HasValue && position > joint.Maximum.Value ) position = joint.Maximum.Value;
		return position;
	}

	public static bool TryValidateLimits( AssemblyJoint joint, out string error )
	{
		error = null;
		if ( joint == null ) { error = "Slider joint is missing."; return false; }
		if ( joint.Kind != SliderKind ) { error = $"Joint '{joint.Id}' is not a slider joint."; return false; }
		if ( ( joint.Minimum.HasValue && !float.IsFinite( joint.Minimum.Value ) )
			|| ( joint.Maximum.HasValue && !float.IsFinite( joint.Maximum.Value ) ) )
		{
			error = "Joint limits must be finite.";
			return false;
		}
		if ( joint.Minimum.HasValue && joint.Maximum.HasValue && joint.Minimum.Value > joint.Maximum.Value )
		{
			error = "Joint minimum cannot exceed its maximum.";
			return false;
		}
		return true;
	}

	public static string DescribeLimits( AssemblyJoint joint )
	{
		if ( joint == null ) return "No travel limits";
		if ( joint.Minimum.HasValue && joint.Maximum.HasValue )
			return $"Travel {Format( joint.Minimum.Value )} in to {Format( joint.Maximum.Value )} in";
		if ( joint.Minimum.HasValue ) return $"Travel from {Format( joint.Minimum.Value )} in";
		if ( joint.Maximum.HasValue ) return $"Travel up to {Format( joint.Maximum.Value )} in";
		return "No travel limits";
	}

	/// <summary>
	/// Solves occurrence B for a slider at the requested travel. Pure: it
	/// never writes to the occurrences, so a failed solve leaves the last
	/// valid pose untouched. Travel outside the saved limits is clamped to the
	/// nearest limit (reported via <paramref name="clamped"/>) instead of
	/// failing -- a dragged slider stops at its end. Only broken input fails:
	/// wrong kind, missing or mismatched occurrences, non-rigid connector
	/// frames, invalid transforms, inverted or non-finite limits.
	/// Orientation is locked to A: B's relative orientation is not preserved
	/// from its current pose; it is set by the frames.
	/// </summary>
	public static bool TrySolve( AssemblyJoint joint, AssemblyOccurrence occurrenceA, AssemblyOccurrence occurrenceB, float travel, out Xform solvedB, out string error, out bool clamped )
	{
		solvedB = Xform.Identity;
		error = null;
		clamped = false;
		if ( joint == null ) { error = "Slider joint is missing."; return false; }
		if ( joint.Kind != SliderKind ) { error = $"Joint '{joint.Id}' is not a slider joint."; return false; }
		if ( occurrenceA == null || occurrenceB == null ) { error = $"Joint '{joint.Id}' is missing an occurrence."; return false; }
		if ( occurrenceA.Id != joint.OccurrenceA || occurrenceB.Id != joint.OccurrenceB )
		{
			error = $"Joint '{joint.Id}' does not connect occurrences '{occurrenceA.Id}' and '{occurrenceB.Id}'.";
			return false;
		}
		if ( !TryValidateLimits( joint, out error ) ) return false;
		if ( !float.IsFinite( travel ) ) { error = "Travel must be a finite number of inches."; return false; }
		if ( occurrenceA.Transform == null )
		{
			error = $"Occurrence '{occurrenceA.Id}' has no transform.";
			return false;
		}
		if ( !occurrenceA.Transform.IsValid( out var whyA ) )
		{
			error = $"Occurrence '{occurrenceA.Id}' has an invalid transform: {whyA}";
			return false;
		}
		if ( occurrenceB.Transform == null )
		{
			error = $"Occurrence '{occurrenceB.Id}' has no transform.";
			return false;
		}
		if ( !occurrenceB.Transform.IsValid( out var whyB ) )
		{
			error = $"Occurrence '{occurrenceB.Id}' has an invalid transform: {whyB}";
			return false;
		}
		if ( joint.FrameA == null )
		{
			error = $"Connector A frame of joint '{joint.Id}' is missing.";
			return false;
		}
		if ( !joint.FrameA.IsRigidFrame( out var whyFrameA ) )
		{
			error = $"Connector A frame of joint '{joint.Id}' must be a finite rigid transform: {whyFrameA}";
			return false;
		}
		if ( joint.FrameB == null )
		{
			error = $"Connector B frame of joint '{joint.Id}' is missing.";
			return false;
		}
		if ( !joint.FrameB.IsRigidFrame( out var whyFrameB ) )
		{
			error = $"Connector B frame of joint '{joint.Id}' must be a finite rigid transform: {whyFrameB}";
			return false;
		}

		var limited = ClampToLimits( joint, travel );
		clamped = limited != travel;
		try
		{
			var worldA = occurrenceA.Transform.ToXform() * joint.FrameA.ToXform();
			// World inches, even if the anchor occurrence is scaled: the
			// connector axis in assembly space is worldA.Z, whose length is
			// the anchor scale along that axis, so divide it out.
			var axisLength = worldA.Z.Length;
			var alongAxis = axisLength > 1e-8f ? limited / axisLength : limited;
			solvedB = worldA * Xform.Translate( new Vec3( 0, 0, alongAxis ) ) * joint.FrameB.ToXform().Inverse;
		}
		catch ( InvalidOperationException e )
		{
			solvedB = Xform.Identity;
			error = $"Connector B frame of joint '{joint.Id}' cannot be inverted: {e.Message}";
			return false;
		}
		return true;
	}

	/// <summary>Solves at the joint's stored position (travel zero when none is stored).</summary>
	public static bool TrySolveAtStoredPosition( AssemblyJoint joint, AssemblyOccurrence occurrenceA, AssemblyOccurrence occurrenceB, out Xform solvedB, out string error, out bool clamped ) =>
		TrySolve( joint, occurrenceA, occurrenceB, GetPosition( joint ), out solvedB, out error, out clamped );

	/// <summary>Looks the joint and both occurrences up by ID; missing references fail with the joint named.</summary>
	public static bool TrySolveInDocument( AssemblyDocument doc, string jointId, float travel, out Xform solvedB, out string error, out bool clamped )
	{
		solvedB = Xform.Identity;
		error = null;
		clamped = false;
		if ( doc == null ) { error = "Assembly document is missing."; return false; }
		var joint = doc.Joints?.FirstOrDefault( j => j != null && j.Id == jointId );
		if ( joint == null ) { error = $"Joint '{jointId}' does not exist."; return false; }
		var a = doc.Occurrences?.FirstOrDefault( o => o != null && o.Id == joint.OccurrenceA );
		if ( a == null ) { error = $"Joint '{joint.Id}' references missing occurrence '{joint.OccurrenceA}'."; return false; }
		var b = doc.Occurrences?.FirstOrDefault( o => o != null && o.Id == joint.OccurrenceB );
		if ( b == null ) { error = $"Joint '{joint.Id}' references missing occurrence '{joint.OccurrenceB}'."; return false; }
		return TrySolve( joint, a, b, travel, out solvedB, out error, out clamped );
	}

	/// <summary>
	/// Reads the travel back from the stored occurrence transforms: the
	/// projection of the connector-origin difference onto the shared axis,
	/// plus the off-axis residual (nonzero when the pose violates the joint,
	/// e.g. after a source edit moved a part).
	/// </summary>
	public static bool TryMeasure( AssemblyJoint joint, AssemblyOccurrence occurrenceA, AssemblyOccurrence occurrenceB, out float travel, out float offAxis, out string error )
	{
		travel = 0f;
		offAxis = 0f;
		error = null;
		if ( joint == null ) { error = "Slider joint is missing."; return false; }
		if ( joint.Kind != SliderKind ) { error = $"Joint '{joint.Id}' is not a slider joint."; return false; }
		if ( occurrenceA == null || occurrenceB == null ) { error = $"Joint '{joint.Id}' is missing an occurrence."; return false; }
		if ( occurrenceA.Id != joint.OccurrenceA || occurrenceB.Id != joint.OccurrenceB )
		{
			error = $"Joint '{joint.Id}' does not connect occurrences '{occurrenceA.Id}' and '{occurrenceB.Id}'.";
			return false;
		}
		if ( occurrenceA.Transform == null || !occurrenceA.Transform.IsValid( out _ )
			|| occurrenceB.Transform == null || !occurrenceB.Transform.IsValid( out _ ) )
		{
			error = $"Joint '{joint.Id}' has an occurrence with an invalid transform.";
			return false;
		}
		if ( joint.FrameA == null || !joint.FrameA.IsRigidFrame( out _ )
			|| joint.FrameB == null || !joint.FrameB.IsRigidFrame( out _ ) )
		{
			error = $"Joint '{joint.Id}' has a connector frame that is not a finite rigid transform.";
			return false;
		}
		var worldA = occurrenceA.Transform.ToXform() * joint.FrameA.ToXform();
		var worldB = occurrenceB.Transform.ToXform() * joint.FrameB.ToXform();
		var axis = worldA.TransformDirection( new Vec3( 0, 0, 1 ) ).Normal;
		var delta = worldB.Origin - worldA.Origin;
		travel = Vec3.Dot( delta, axis );
		offAxis = ( delta - axis * travel ).Length;
		return true;
	}
}

/// <summary>
/// What the joint sheet needs for its slider section, as data only: labels,
/// hints, and the order the controls go in. No layout, no UI controls: the
/// sheet owns the controls. The slider section lives in the SAME sheet as
/// Fixed and Hinge, reached the same way, with the same Apply/Cancel/Back.
/// </summary>
public static class AssemblySliderSheet
{
	public const string SectionTitle = "Slider";
	public const string KindSegmentHint = "Fixed | Hinge | Slider";
	public const string PartALabel = "Part A (holds still)";
	public const string PartBLabel = "Part B (slides)";
	public const string MinLabel = "Min travel";
	public const string MaxLabel = "Max travel";
	public const string CurrentLabel = "Current position";
	public const string AxisNote = "Axis: shared +Z of both connector frames. Travel 0 is the frames aligned; positive moves Part B along Part A +Z, in inches.";
	public const string FlipHint = "If the axis points the wrong way, flip connector B. The joint never flips it for you.";
	public const string UnitsNote = "Travel in inches; stored in inches.";
	public const string GroundedNote = "If no part is grounded, pick a grounded root first: the slider holds Part A still and slides Part B.";
	public const string AtLimitText = "At limit";
}

/// <summary>
/// Thin command layer over <see cref="AssemblySlider"/>: validates first,
/// then applies through <see cref="AssemblyHistory.ApplyWithHistory"/> so each
/// accepted edit is exactly one EditHistory entry. A refused operation changes
/// nothing and consumes no undo step. A is the anchor and never moves; B is
/// refused when grounded. History may be null, in which case the edit applies
/// directly with no undo entry (same as AssemblyHistory).
/// </summary>
public static class AssemblySliderService
{
	/// <summary>
	/// Creates a slider joint between two different existing occurrences.
	/// Frames must be finite rigid transforms; limits must be finite and
	/// ordered. The new joint is returned re-fetched from the document (the
	/// history apply replaces the document's collections with a snapshot
	/// round-trip); after an undo/redo, re-fetch by Id again.
	/// </summary>
	public static bool CreateSlider( AssemblyDocument doc, EditHistory history, string occurrenceAId, string occurrenceBId, AssemblyTransform frameA, AssemblyTransform frameB, float? minimum, float? maximum, out AssemblyJoint joint, out string error )
	{
		joint = null;
		error = null;
		if ( doc == null ) { error = "Assembly document is missing."; return false; }
		if ( string.IsNullOrEmpty( occurrenceAId ) || string.IsNullOrEmpty( occurrenceBId ) )
		{
			error = "Slider occurrences are missing.";
			return false;
		}
		if ( occurrenceAId == occurrenceBId )
		{
			error = "A slider joint must connect two different occurrences.";
			return false;
		}
		var a = doc.Occurrences?.FirstOrDefault( o => o != null && o.Id == occurrenceAId );
		if ( a == null ) { error = $"Slider joint references missing occurrence '{occurrenceAId}'."; return false; }
		var b = doc.Occurrences?.FirstOrDefault( o => o != null && o.Id == occurrenceBId );
		if ( b == null ) { error = $"Slider joint references missing occurrence '{occurrenceBId}'."; return false; }
		if ( frameA == null )
		{
			error = "Connector A frame is missing.";
			return false;
		}
		if ( !frameA.IsRigidFrame( out var whyFrameA ) )
		{
			error = $"Connector A frame must be a finite rigid transform: {whyFrameA}";
			return false;
		}
		if ( frameB == null )
		{
			error = "Connector B frame is missing.";
			return false;
		}
		if ( !frameB.IsRigidFrame( out var whyFrameB ) )
		{
			error = $"Connector B frame must be a finite rigid transform: {whyFrameB}";
			return false;
		}
		var probe = new AssemblyJoint { Kind = AssemblySlider.SliderKind, Minimum = minimum, Maximum = maximum };
		if ( !AssemblySlider.TryValidateLimits( probe, out error ) ) return false;

		var id = AssemblyDocument.NewId();
		AssemblyHistory.ApplyWithHistory( doc, history, "Create slider joint", () =>
		{
			doc.Joints.Add( new AssemblyJoint
			{
				Id = id,
				Kind = AssemblySlider.SliderKind,
				OccurrenceA = occurrenceAId,
				OccurrenceB = occurrenceBId,
				FrameA = frameA,
				FrameB = frameB,
				Minimum = minimum,
				Maximum = maximum,
			} );
		} );
		joint = doc.Joints.FirstOrDefault( j => j != null && j.Id == id );
		return joint != null;
	}

	/// <summary>
	/// Slides B to the requested travel (inches). Validates the position
	/// first (out-of-limit travel is rejected and the last valid position
	/// survives), refuses a grounded B, then solves B and writes the stored
	/// position plus B's new transform in ONE history entry. A failed solve
	/// changes nothing.
	/// </summary>
	public static bool SetTravel( AssemblyDocument doc, EditHistory history, string jointId, float travel, out string error )
	{
		error = null;
		if ( doc == null ) { error = "Assembly document is missing."; return false; }
		var joint = doc.Joints?.FirstOrDefault( j => j != null && j.Id == jointId );
		if ( joint == null ) { error = $"Joint '{jointId}' does not exist."; return false; }
		if ( !AssemblySlider.TryValidatePosition( joint, travel, out error ) ) return false;
		var a = doc.Occurrences?.FirstOrDefault( o => o != null && o.Id == joint.OccurrenceA );
		if ( a == null ) { error = $"Joint '{joint.Id}' references missing occurrence '{joint.OccurrenceA}'."; return false; }
		var b = doc.Occurrences?.FirstOrDefault( o => o != null && o.Id == joint.OccurrenceB );
		if ( b == null ) { error = $"Joint '{joint.Id}' references missing occurrence '{joint.OccurrenceB}'."; return false; }
		if ( AssemblyGrounding.IsGrounded( b ) )
		{
			error = $"Occurrence '{b.Id}' is grounded, so slider joint '{joint.Id}' cannot move it.";
			return false;
		}
		if ( !AssemblySlider.TrySolve( joint, a, b, travel, out var solvedB, out error, out _ ) ) return false;
		AssemblyHistory.ApplyWithHistory( doc, history, "Set slider travel", () =>
		{
			AssemblySlider.TrySetPosition( joint, travel, out _ );
			b.Transform = AssemblyTransform.FromXform( solvedB );
		} );
		return true;
	}

	/// <summary>
	/// Replaces the joint's travel limits. Limits must be finite and ordered;
	/// the stored position is left alone (a stored position outside the new
	/// limits stays stored; the next SetTravel or solve clamps or rejects
	/// against the new limits as usual).
	/// </summary>
	public static bool SetLimits( AssemblyDocument doc, EditHistory history, string jointId, float? minimum, float? maximum, out string error )
	{
		error = null;
		if ( doc == null ) { error = "Assembly document is missing."; return false; }
		var joint = doc.Joints?.FirstOrDefault( j => j != null && j.Id == jointId );
		if ( joint == null ) { error = $"Joint '{jointId}' does not exist."; return false; }
		if ( joint.Kind != AssemblySlider.SliderKind ) { error = $"Joint '{joint.Id}' is not a slider joint."; return false; }
		var probe = new AssemblyJoint { Kind = AssemblySlider.SliderKind, Minimum = minimum, Maximum = maximum };
		if ( !AssemblySlider.TryValidateLimits( probe, out error ) ) return false;
		AssemblyHistory.ApplyWithHistory( doc, history, "Set slider limits", () =>
		{
			joint.Minimum = minimum;
			joint.Maximum = maximum;
		} );
		return true;
	}
}
