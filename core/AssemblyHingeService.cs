using System;
using System.Collections.Generic;
using System.Globalization;
using Effigy;
using Effigy.Assembly;

namespace Marionette.Desktop;

// AS08: hinge joints with measured angle limits.
// Ports Mobile app/AssemblyHinge.cs (kernel types only, no Godot) into Desktop
// form. The pure port (AssemblyHinge) is a faithful copy of the Mobile logic;
// the command layer (AssemblyHingeService) wraps it in AssemblyHistory so one
// call is one undo step, and the sheet constants (AssemblyHingeSheet) are data
// only, no UI.
//
// Conventions, fixed by the saved AssemblyJoint record (AS01 AssemblyDocument):
// - A hinge shares ONE axis: the local +Z of both connector frames. Angle zero
//   is the frames coincident (relative rotation identity). Positive angles
//   follow the right-hand rule about Part A frame +Z. Angles are stored in
//   radians (the document AngleUnit is "rad"); degrees are display/typed input
//   only, converted once. Limits live on the joint (Minimum/Maximum, radians,
//   either may be absent).
// - Part A is the root: it never moves. Part B rotates. Anything connected to
//   B through other joints moves rigidly with B, so those joints keep their
//   current angle/offset. A grounded occurrence never moves; a move that would
//   need one fails and moves nothing.
// - The axis is never silently flipped. Frames whose +Z axes point opposite,
//   are skewed, or sit off the shared line are conflicts, reported with the
//   reason, and the joint is left exactly as it was.
//
// Undo/persistence: a hinge edit is one discrete operation when the caller goes
// through AssemblyHingeService, which checkpoints via AssemblyHistory first
// (one EditHistory entry per call; a refused edit adds no entry). The joint,
// its limits and its angle all live in the saved AssemblyDocument: the angle
// derives from the occurrence transforms, which persist through ToJson/FromJson,
// so save/reopen keeps the hinge where it was.
// Preview: clone the document (ToJson/FromJson round-trip), call
// TrySetHingeAngle on the clone, show it, discard it. Nothing permanent is
// created until Apply.
//
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
public static class AssemblyHinge
{
	/// <summary>The hinge axis in each connector frame's local coordinates.</summary>
	public static readonly Vec3 AxisLocal = new(0, 0, 1);

	/// <summary>How far the two world +Z axes may diverge and still count as one shared axis.</summary>
	public const float AxisToleranceRadians = 1e-3f;

	/// <summary>How far connector origins may sit off the shared axis line (document inches).</summary>
	public const float LineToleranceInches = 1e-3f;

	/// <summary>Rigid-placement tolerance, same epsilon as AssemblyTransform.IsRigidFrame.</summary>
	public const float RigidTolerance = 1e-4f;

	// ---- Limits ----

	/// <summary>Limits must be finite, and a minimum may not exceed its maximum.</summary>
	public static bool TryValidateLimits(float? minimum, float? maximum, out string why)
	{
		why = null;
		if ((minimum.HasValue && !float.IsFinite(minimum.Value))
			|| (maximum.HasValue && !float.IsFinite(maximum.Value)))
		{
			why = "Joint limits must be finite numbers.";
			return false;
		}
		if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
		{
			why = $"Minimum angle ({FormatAngle(minimum.Value)}) cannot exceed maximum angle ({FormatAngle(maximum.Value)}).";
			return false;
		}
		return true;
	}

	/// <summary>
	/// Replace a hinge joint's limits. An invalid pair is rejected and the previous valid
	/// limits are left exactly as they were.
	/// </summary>
	public static bool TrySetLimits(AssemblyJoint joint, float? minimum, float? maximum, out string why)
	{
		why = null;
		if (joint == null)
		{
			why = "Joint is missing.";
			return false;
		}
		if (!TryValidateLimits(minimum, maximum, out why))
			return false;
		joint.Minimum = minimum;
		joint.Maximum = maximum;
		return true;
	}

	/// <summary>Angle clamped to the joint's limits. No limits means no clamping. An inverted
	/// stored pair clamps nothing; ValidateHinge reports it instead.</summary>
	public static float ClampToLimits(float angleRadians, float? minimum, float? maximum)
	{
		if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
			return angleRadians;
		if (minimum.HasValue && angleRadians < minimum.Value)
			return minimum.Value;
		if (maximum.HasValue && angleRadians > maximum.Value)
			return maximum.Value;
		return angleRadians;
	}

	/// <summary>
	/// Build a hinge joint between two occurrences. The caller owns the connector frames
	/// (AS04 picking); this only checks they are usable and the limits are valid.
	/// </summary>
	public static AssemblyJoint CreateHinge(AssemblyDocument doc, string occurrenceA, string occurrenceB,
		AssemblyTransform frameA, AssemblyTransform frameB, float? minimum, float? maximum, out string why)
	{
		why = null;
		if (doc == null)
		{
			why = "Assembly document is missing.";
			return null;
		}
		if (string.IsNullOrWhiteSpace(occurrenceA) || string.IsNullOrWhiteSpace(occurrenceB))
		{
			why = "A hinge needs two occurrences.";
			return null;
		}
		if (occurrenceA == occurrenceB)
		{
			why = "A hinge must connect two different occurrences.";
			return null;
		}
		if (doc.Occurrences == null
			|| doc.Occurrences.Find(o => o != null && o.Id == occurrenceA) == null
			|| doc.Occurrences.Find(o => o != null && o.Id == occurrenceB) == null)
		{
			why = "Both hinge occurrences must exist in the assembly.";
			return null;
		}
		if (frameA == null || frameB == null)
		{
			why = "A hinge needs a connector frame on both parts.";
			return null;
		}
		if (!frameA.IsRigidFrame(out var frameWhy))
		{
			why = $"Connector A frame must be a finite rigid transform: {frameWhy}";
			return null;
		}
		if (!frameB.IsRigidFrame(out frameWhy))
		{
			why = $"Connector B frame must be a finite rigid transform: {frameWhy}";
			return null;
		}
		if (!TryValidateLimits(minimum, maximum, out why))
			return null;
		return new AssemblyJoint
		{
			Id = AssemblyDocument.NewId(),
			Kind = "hinge",
			OccurrenceA = occurrenceA,
			OccurrenceB = occurrenceB,
			FrameA = frameA,
			FrameB = frameB,
			Minimum = minimum,
			Maximum = maximum,
		};
	}

	// ---- Angle ----

	/// <summary>Wrap to (-pi, pi]: 270 degrees reads as -90, never as 270.</summary>
	public static float WrapAngle(float radians) => MathF.Atan2(MathF.Sin(radians), MathF.Cos(radians));

	static bool IsRigidPlacement(Xform transform) =>
		AssemblyTransform.FromXform(transform).IsRigidFrame(out _);

	static bool TryWorldFrames(AssemblyDocument doc, AssemblyJoint joint,
		out Xform worldA, out Xform worldB, out string why)
	{
		worldA = Xform.Identity;
		worldB = Xform.Identity;
		why = null;
		if (doc == null)
		{
			why = "Assembly document is missing.";
			return false;
		}
		if (joint == null)
		{
			why = "Joint is missing.";
			return false;
		}
		if (joint.Kind != "hinge")
		{
			why = $"Joint '{joint.Id}' is a '{joint.Kind}' joint, not a hinge.";
			return false;
		}
		if (!string.IsNullOrWhiteSpace(joint.OccurrenceA) && joint.OccurrenceA == joint.OccurrenceB)
		{
			why = "A hinge must connect two different occurrences.";
			return false;
		}
		var occurrenceA = doc.Occurrences?.Find(o => o != null && o.Id == joint.OccurrenceA);
		if (occurrenceA == null)
		{
			why = $"Occurrence '{joint.OccurrenceA}' does not exist.";
			return false;
		}
		var occurrenceB = doc.Occurrences?.Find(o => o != null && o.Id == joint.OccurrenceB);
		if (occurrenceB == null)
		{
			why = $"Occurrence '{joint.OccurrenceB}' does not exist.";
			return false;
		}
		if (joint.FrameA == null)
		{
			why = "Connector A frame is missing.";
			return false;
		}
		if (joint.FrameB == null)
		{
			why = "Connector B frame is missing.";
			return false;
		}
		if (!joint.FrameA.IsRigidFrame(out var frameWhy))
		{
			why = $"Connector A frame must be a finite rigid transform: {frameWhy}";
			return false;
		}
		if (!joint.FrameB.IsRigidFrame(out frameWhy))
		{
			why = $"Connector B frame must be a finite rigid transform: {frameWhy}";
			return false;
		}
		if (occurrenceA.Transform == null)
		{
			why = $"Occurrence '{occurrenceA.Name}' transform is missing.";
			return false;
		}
		if (!occurrenceA.Transform.IsValid(out var transformWhy))
		{
			why = $"Occurrence '{occurrenceA.Name}' transform is invalid: {transformWhy}";
			return false;
		}
		if (occurrenceB.Transform == null)
		{
			why = $"Occurrence '{occurrenceB.Name}' transform is missing.";
			return false;
		}
		if (!occurrenceB.Transform.IsValid(out transformWhy))
		{
			why = $"Occurrence '{occurrenceB.Name}' transform is invalid: {transformWhy}";
			return false;
		}
		var placementA = occurrenceA.Transform.ToXform();
		var placementB = occurrenceB.Transform.ToXform();
		if (!IsRigidPlacement(placementA))
		{
			why = $"Occurrence '{occurrenceA.Name}' carries scale or a mirror; hinge solving needs a rigid placement.";
			return false;
		}
		if (!IsRigidPlacement(placementB))
		{
			why = $"Occurrence '{occurrenceB.Name}' carries scale or a mirror; hinge solving needs a rigid placement.";
			return false;
		}
		worldA = placementA * joint.FrameA.ToXform();
		worldB = placementB * joint.FrameB.ToXform();
		return true;
	}

	/// <summary>
	/// The two world +Z axes must be one shared line: same direction, same line. Returns false
	/// with a code the sheet can show instead of a silent axis flip. Codes:
	/// hinge-axis-flipped, hinge-axis-misaligned, hinge-axis-offset.
	/// </summary>
	static bool TryCheckAxis(Xform worldA, Xform worldB, out string code, out string why)
	{
		code = null;
		why = null;
		var axisA = worldA.TransformDirection(AxisLocal);
		var axisB = worldB.TransformDirection(AxisLocal);
		var dot = Vec3.Dot(axisA, axisB);
		if (dot < 0f)
		{
			code = "hinge-axis-flipped";
			why = "Hinge axis is flipped: connector B points opposite to connector A. Flip connector B instead of continuing.";
			return false;
		}
		var misalignment = MathF.Acos(Math.Clamp(dot, -1f, 1f));
		if (misalignment > AxisToleranceRadians)
		{
			code = "hinge-axis-misaligned";
			why = $"Hinge axes are misaligned by {FormatAngle(misalignment)}: both connector frames must share one axis.";
			return false;
		}
		var offset = worldB.Origin - worldA.Origin;
		var along = Vec3.Dot(offset, axisA);
		var offAxis = offset - axisA * along;
		if (offAxis.Length > LineToleranceInches)
		{
			code = "hinge-axis-offset";
			why = $"Connector frames sit {offAxis.Length:0.###} in off the shared hinge axis: the frames must meet on one line.";
			return false;
		}
		return true;
	}

	/// <summary>
	/// The hinge's current angle in radians: 0 is frames aligned, positive is the right-hand
	/// rule about Part A +Z. Fails, changing nothing, when the frames do not share one axis.
	/// </summary>
	public static bool TryGetHingeAngle(AssemblyDocument doc, AssemblyJoint joint, out float angleRadians, out string why)
	{
		angleRadians = 0f;
		why = null;
		if (!TryWorldFrames(doc, joint, out var worldA, out var worldB, out why))
			return false;
		if (!TryCheckAxis(worldA, worldB, out _, out why))
			return false;
		var relative = worldA.Inverse * worldB;
		if (MathF.Abs(relative.X.z) > 1e-2f || MathF.Abs(relative.Y.z) > 1e-2f
			|| (relative.Z - AxisLocal).Length > 1e-2f)
		{
			why = "Hinge frames do not share one rotation axis: the relative rotation is not about their +Z.";
			return false;
		}
		angleRadians = WrapAngle(MathF.Atan2(relative.X.y, relative.X.x));
		return true;
	}

	// ---- Motion ----

	/// <summary>
	/// Set the hinge to an angle (radians), clamped to its limits. Part A is the root and never
	/// moves; Part B rotates about the shared world axis, and anything joined to B moves rigidly
	/// with it so other joints keep their current values. <paramref name="isGrounded"/> names
	/// occurrences that must not move (null means nothing is grounded). A move blocked by a
	/// grounded occurrence, a closed loop back to the root, or invalid geometry fails and moves
	/// nothing. Apply through AssemblyHingeService so one call is one undo step.
	/// </summary>
	public static bool TrySetHingeAngle(AssemblyDocument doc, AssemblyJoint joint, float angleRadians,
		Func<string, bool> isGrounded, out string why)
	{
		why = null;
		if (!float.IsFinite(angleRadians))
		{
			why = "Hinge angle must be a finite number.";
			return false;
		}
		if (joint != null && !TryValidateLimits(joint.Minimum, joint.Maximum, out why))
			return false;
		if (!TryGetHingeAngle(doc, joint, out var current, out why))
			return false;
		var target = ClampToLimits(angleRadians, joint.Minimum, joint.Maximum);
		if (MathF.Abs(WrapAngle(target - current)) < 1e-6f)
			return true;
		if (!TryWorldFrames(doc, joint, out var worldA, out var worldB, out why))
			return false;

		var axisWorld = worldA.TransformDirection(AxisLocal);
		var alongOffset = Vec3.Dot(worldB.Origin - worldA.Origin, axisWorld);
		var spin = Xform.Rotate(AxisLocal, target);
		var worldBNew = new Xform(
			worldA.TransformDirection(spin.X),
			worldA.TransformDirection(spin.Y),
			worldA.TransformDirection(spin.Z),
			worldA.Origin + axisWorld * alongOffset);
		var occurrenceB = doc.Occurrences.Find(o => o != null && o.Id == joint.OccurrenceB);
		var placementBNew = worldBNew * joint.FrameB.ToXform().Inverse;
		var placementB = occurrenceB.Transform.ToXform();
		var delta = placementBNew * placementB.Inverse;

		// Collect everything that must move with B, and refuse the whole move first if any of
		// it cannot move. Nothing is assigned until this walk succeeds.
		var move = new List<AssemblyOccurrence>();
		var seen = new HashSet<string>(StringComparer.Ordinal) { joint.OccurrenceA };
		var queue = new Queue<string>();
		if (isGrounded?.Invoke(joint.OccurrenceB) == true)
		{
			why = $"Occurrence '{occurrenceB.Name}' is grounded and cannot move.";
			return false;
		}
		seen.Add(joint.OccurrenceB);
		queue.Enqueue(joint.OccurrenceB);
		move.Add(occurrenceB);
		while (queue.Count > 0)
		{
			var currentId = queue.Dequeue();
			foreach (var other in doc.Joints ?? new List<AssemblyJoint>())
			{
				if (other == null || other == joint)
					continue;
				string neighbour = null;
				if (other.OccurrenceA == currentId)
					neighbour = other.OccurrenceB;
				else if (other.OccurrenceB == currentId)
					neighbour = other.OccurrenceA;
				if (neighbour == null || seen.Contains(neighbour))
				{
					if (neighbour != null && neighbour == joint.OccurrenceA)
					{
						why = "Hinge motion is blocked: another joint holds this chain to the root (closed loop).";
						return false;
					}
					continue;
				}
				seen.Add(neighbour);
				if (isGrounded?.Invoke(neighbour) == true)
				{
					why = $"Hinge motion is blocked: occurrence '{neighbour}' is grounded.";
					return false;
				}
				var next = doc.Occurrences.Find(o => o != null && o.Id == neighbour);
				if (next == null)
				{
					why = $"Occurrence '{neighbour}' does not exist.";
					return false;
				}
				if (next.Transform == null || !next.Transform.IsValid(out _))
				{
					why = $"Occurrence '{next.Name}' transform is missing or invalid.";
					return false;
				}
				if (!IsRigidPlacement(next.Transform.ToXform()))
				{
					why = $"Occurrence '{next.Name}' carries scale or a mirror; hinge solving needs a rigid placement.";
					return false;
				}
				move.Add(next);
				queue.Enqueue(neighbour);
			}
		}

		var fresh = new Dictionary<string, Xform>(StringComparer.Ordinal);
		foreach (var occurrence in move)
			fresh[occurrence.Id] = delta * occurrence.Transform.ToXform();
		foreach (var occurrence in move)
			occurrence.Transform = AssemblyTransform.FromXform(fresh[occurrence.Id]);
		return true;
	}

	// ---- Hinge-specific validation ----

	/// <summary>
	/// Issues this hinge has beyond AssemblyDocument.Validate: kind, references, frames,
	/// rigid placements, the shared axis, and the current angle against its limits. Empty
	/// means the hinge is healthy. Combine with doc.Validate() for the whole document.
	/// </summary>
	public static IReadOnlyList<AssemblyValidationIssue> ValidateHinge(AssemblyDocument doc, AssemblyJoint joint)
	{
		var issues = new List<AssemblyValidationIssue>();
		if (joint == null)
		{
			issues.Add(new("invalid-joint", "", "Joint is missing."));
			return issues;
		}
		if (joint.Kind != "hinge")
		{
			issues.Add(new("not-a-hinge", joint.Id ?? "", $"Joint kind '{joint.Kind}' is not a hinge."));
			return issues;
		}
		if (!TryValidateLimits(joint.Minimum, joint.Maximum, out var limitWhy))
			issues.Add(new("invalid-joint-limit", joint.Id ?? "", limitWhy));
		if (!TryWorldFrames(doc, joint, out var worldA, out var worldB, out var why))
		{
			var code = why != null && why.Contains("does not exist", StringComparison.Ordinal)
				? "missing-occurrence"
				: why != null && why.Contains("different occurrences", StringComparison.Ordinal)
					? "self-joint"
					: why != null && why.Contains("Connector", StringComparison.Ordinal)
						? "invalid-connector-frame"
						: "invalid-occurrence-placement";
			issues.Add(new(code, joint.Id ?? "", why));
			return issues;
		}
		if (!TryCheckAxis(worldA, worldB, out var axisCode, out var axisWhy))
		{
			issues.Add(new(axisCode, joint.Id ?? "", axisWhy));
			return issues;
		}
		if (!TryGetHingeAngle(doc, joint, out var angle, out why))
		{
			issues.Add(new("hinge-angle-unreadable", joint.Id ?? "", why));
			return issues;
		}
		var clamped = ClampToLimits(angle, joint.Minimum, joint.Maximum);
		if (MathF.Abs(WrapAngle(angle - clamped)) > 1e-6f)
			issues.Add(new("hinge-angle-outside-limits", joint.Id ?? "",
				$"Current hinge angle {FormatAngle(angle)} is outside limits {LimitText(joint.Minimum, joint.Maximum)}."));
		return issues;
	}

	// ---- UI text: degrees for display, radians stay in storage ----

	/// <summary>Degrees with no trailing noise: 90, -45, 0.5. Always ASCII digits and '-'.</summary>
	public static string FormatDegrees(float degrees)
	{
		var rounded = MathF.Round(degrees);
		return MathF.Abs(degrees - rounded) < 0.05f
			? $"{rounded:0}\u00b0"
			: $"{degrees:0.0}\u00b0";
	}

	/// <summary>Radians (storage) as display degrees.</summary>
	public static string FormatAngle(float radians) => FormatDegrees(radians * 180f / MathF.PI);

	/// <summary>"No limits", "-90° to 90°", "Min -90°", or "Max 90°".</summary>
	public static string LimitText(float? minimum, float? maximum)
	{
		if (!minimum.HasValue && !maximum.HasValue)
			return "No limits";
		if (minimum.HasValue && maximum.HasValue)
			return $"{FormatAngle(minimum.Value)} to {FormatAngle(maximum.Value)}";
		if (minimum.HasValue)
			return $"Min {FormatAngle(minimum.Value)}";
		return $"Max {FormatAngle(maximum.Value)}";
	}
}

/// <summary>
/// What the joint sheet needs for its hinge section, as data only: labels, hints,
/// and the order the controls go in. No layout, no controls: the sheet owns the
/// controls. The hinge section lives in the SAME sheet as Fixed, reached the same
/// way, with the same Apply/Cancel/Back. Controls stay at least 48 logical units;
/// the angle fields show degrees and reject a minimum above its maximum without
/// touching the saved limits.
/// </summary>
public static class AssemblyHingeSheet
{
	public const string SectionTitle = "Hinge";
	public const string KindSegmentHint = "Fixed | Hinge";
	public const string PartALabel = "Part A (holds still)";
	public const string PartBLabel = "Part B (rotates)";
	public const string MinLabel = "Min angle";
	public const string MaxLabel = "Max angle";
	public const string CurrentLabel = "Current angle";
	public const string AxisNote = "Axis: shared +Z of both connector frames. Angle 0 is the frames aligned; positive follows the right-hand rule about Part A +Z.";
	public const string FlipHint = "If the axis points the wrong way, flip connector B. The joint never flips it for you.";
	public const string UnitsNote = "Angles in degrees; stored in radians.";
	public const string GroundedNote = "If no part is grounded, pick a grounded root first: the hinge holds Part A still and turns Part B.";
	public const string AtLimitText = "At limit";
}

/// <summary>
/// Command layer over <see cref="AssemblyHinge"/>: every call validates first and
/// applies through <see cref="AssemblyHistory.ApplyWithHistory"/>, so one call is
/// one undo step and a refused operation changes nothing and adds no history
/// entry. Grounding comes from the stored flag (<see cref="AssemblyGrounding"/>).
/// With a null history the edit applies directly, with no undo entry.
/// </summary>
public static class AssemblyHingeService
{
	/// <summary>
	/// Create a hinge joint between two occurrences and add it to the document as
	/// one undo step. Returns false with <paramref name="why"/> when the joint
	/// cannot be built; the document is untouched.
	/// </summary>
	public static bool CreateHinge(AssemblyDocument doc, EditHistory history,
		string occurrenceA, string occurrenceB,
		AssemblyTransform frameA, AssemblyTransform frameB,
		float? minimum, float? maximum,
		out AssemblyJoint joint, out string why)
	{
		joint = null;
		why = null;
		var built = AssemblyHinge.CreateHinge(doc, occurrenceA, occurrenceB, frameA, frameB, minimum, maximum, out why);
		if (built == null)
			return false;
		if (doc.Joints == null)
		{
			why = "Assembly joints collection is missing.";
			return false;
		}
		joint = built;
		var addedJoint = joint;
		AssemblyHistory.ApplyWithHistory(doc, history, "Create hinge joint", () => doc.Joints.Add(addedJoint));
		return true;
	}

	/// <summary>
	/// Turn a hinge joint (by id) to an angle in radians, clamped to its limits,
	/// as one undo step. Returns false with <paramref name="why"/> when the joint
	/// is missing, is not a hinge, or the move is blocked; nothing moves then.
	/// </summary>
	public static bool SetAngle(AssemblyDocument doc, EditHistory history, string jointId, float angleRadians, out string why)
	{
		why = null;
		if (doc == null)
		{
			why = "Assembly document is missing.";
			return false;
		}
		var joint = doc.Joints?.Find(j => j != null && j.Id == jointId);
		if (joint == null)
		{
			why = $"Joint '{jointId}' does not exist.";
			return false;
		}
		string failure = null;
		AssemblyHistory.ApplyWithHistory(doc, history, "Set hinge angle", () =>
		{
			if (!AssemblyHinge.TrySetHingeAngle(doc, joint, angleRadians, id => AssemblyGrounding.IsGrounded(doc, id), out var setWhy))
				failure = setWhy;
		});
		if (failure != null)
		{
			why = failure;
			return false;
		}
		return true;
	}

	/// <summary>
	/// Turn a hinge joint (by id) to an angle in degrees, converted once to
	/// radians for storage, as one undo step. On success <paramref name="message"/>
	/// names the angle that actually landed, in degrees (the stored angle may be
	/// clamped to the joint's limits); on failure it carries the reason.
	/// </summary>
	public static bool SetAngleDegrees(AssemblyDocument doc, EditHistory history, string jointId, float degrees, out string message)
	{
		message = null;
		if (!float.IsFinite(degrees))
		{
			message = "Hinge angle must be a finite number of degrees.";
			return false;
		}
		if (!SetAngle(doc, history, jointId, degrees * MathF.PI / 180f, out var why))
		{
			message = why;
			return false;
		}
		var joint = doc.Joints?.Find(j => j != null && j.Id == jointId);
		if (joint != null && AssemblyHinge.TryGetHingeAngle(doc, joint, out var current, out _))
			message = $"Hinge angle set to {AssemblyHinge.FormatAngle(current)}.";
		else
			message = $"Hinge angle set to {AssemblyHinge.FormatDegrees(degrees)}.";
		return true;
	}

	/// <summary>
	/// Replace a hinge joint's limits (radians, either may be absent) as one undo
	/// step. An invalid pair is rejected and the previous limits are kept; nothing
	/// is recorded in history then.
	/// </summary>
	public static bool SetLimits(AssemblyDocument doc, EditHistory history, string jointId, float? minimum, float? maximum, out string why)
	{
		why = null;
		if (doc == null)
		{
			why = "Assembly document is missing.";
			return false;
		}
		var joint = doc.Joints?.Find(j => j != null && j.Id == jointId);
		if (joint == null)
		{
			why = $"Joint '{jointId}' does not exist.";
			return false;
		}
		string failure = null;
		AssemblyHistory.ApplyWithHistory(doc, history, "Set hinge limits", () =>
		{
			if (!AssemblyHinge.TrySetLimits(joint, minimum, maximum, out var setWhy))
				failure = setWhy;
		});
		if (failure != null)
		{
			why = failure;
			return false;
		}
		return true;
	}

	/// <summary>
	/// Parse a typed degrees field: empty means no limit, a number is degrees,
	/// anything else (including NaN and Infinity) is rejected.
	/// </summary>
	public static bool TryParseDegrees(string text, out float? degrees)
	{
		degrees = null;
		if (string.IsNullOrWhiteSpace(text))
			return true;
		if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
			|| !float.IsFinite(parsed))
			return false;
		degrees = parsed;
		return true;
	}
}
