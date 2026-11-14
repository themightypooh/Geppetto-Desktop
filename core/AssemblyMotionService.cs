using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;

namespace Marionette.Desktop;

// Assembly 10 — Reversible motion previews and saved poses.
//
// Port of Mobile's AssemblyMotionSession (app/UiAssemblyMotion.cs, pure
// session half only; the Godot panel/input half is out of scope). The session
// uses no UI types, so the checks exercise the math without a viewport.
//
// RULES (from Mobile AssemblyHinge / AssemblySlider / AssemblyDocument):
// - Value is ABSOLUTE, not a delta from rest. Hinge zero is frames coincident
//   (AssemblyHinge.TryGetHingeAngle); slider zero is frame origins coincident
//   along the shared axis. Rest is the value at Begin; Apply bakes the previewed
//   occurrence transforms into the document (the new rest), Cancel puts the old
//   rest back. A hinge saved at 30 degrees reopens at 30 degrees.
// - Hinge rotates about the shared local +Z axis; slider translates along it.
//   Hinge motion is delegated to AssemblyHinge.TrySetHingeAngle (single source
//   of truth); slider motion is delegated to AssemblySlider.TrySolve, with this
//   file adding only the chain walk (grounded/loop refusal) around it. Slider
//   position is stored on the joint (ExtensionData "position", AssemblySlider);
//   the drag starts from the MEASURED travel (geometry is what the user sees),
//   and the stored value is synced on commit (EndSession), never per drag sample.
// - Part A is the root and never moves. A grounded occurrence never moves; a
//   move that would need one fails and moves nothing. Anything attached to the
//   anchor by fixed joints only is grounded to it. Every other joint holds its
//   current value while dragging, so a connected part is carried rigidly. If
//   carrying reaches the anchor side the assembly is a closed loop: the drag is
//   refused with a plain message, never simulated wrong.
// - A joint with Minimum == Maximum is fully constrained (locked): it is
//   listed as locked and cannot be dragged.
// - Preview never corrupts rest: the session only writes occurrence transforms
//   when told to (ApplyPreview/RestoreRest). ApplyPreview mutates the live doc
//   directly with NO history entry; EndSession(commit) registers exactly ONE
//   EditHistory entry (rest JSON -> current JSON), EndSession(cancel) restores
//   the rest JSON directly and leaves no undo step.
//
// SAVED POSES (no Mobile counterpart — Desktop-side design, defined here):
// a named pose is a snapshot of absolute joint values, stored in
// AssemblyDocument ExtensionData under key "poses" as a JSON array of
// {name, values: {jointId: value}}, where value is the absolute hinge angle
// (radians) or slider travel (inches). The live document holds the current
// pose; the session's RestValue is the rest. See AssemblySavedPoses.
//
// DEGRADED MODE (port of Mobile AssemblyPerformanceBudget.Evaluate, mirrored
// here because the budget thresholds are 25 parts / 24 joints / 150k faces /
// 1200 MB RSS): large assemblies solve on finger-up instead of live. Numeric
// sweeps now, future pointer adapter: Sweep drives SetValue+ApplyPreview
// stepwise and returns the applied values.

/// <summary>
/// Pure test-motion session over an AssemblyDocument. No UI types. Values are
/// absolute: radians for a hinge (frames-coincident zero), inches for a slider
/// (frame-origins-coincident zero).
/// </summary>
public sealed class AssemblyMotionSession
{
	public AssemblyDocument Doc { get; }
	public AssemblyJoint Joint { get; private set; }
	public bool IsActive => Joint != null;
	public bool IsHinge => Joint != null && Joint.Kind == "hinge";
	public string Problem { get; private set; }

	/// <summary>Current preview value: absolute radians (hinge) or inches (slider).</summary>
	public float Value { get; private set; }
	/// <summary>Value when this session began (the rest pose).</summary>
	public float RestValue { get; private set; }
	public float? Minimum => Joint?.Minimum;
	public float? Maximum => Joint?.Maximum;
	public bool AtMinLimit { get; private set; }
	public bool AtMaxLimit { get; private set; }
	public bool IsDirty => MathF.Abs(Value - RestValue) > 1e-6f;

	/// <summary>Occurrence ids that move with the drag (B side plus its chain).</summary>
	public IReadOnlyList<string> MovingOccurrenceIds => _moving;

	/// <summary>Degraded (solve on finger-up) when the assembly is large. Set at Begin.</summary>
	public bool IsDegraded { get; private set; }
	/// <summary>Why the session is degraded, or "" when it runs in full live mode.</summary>
	public string DegradedMessage { get; private set; } = "";

	/// <summary>Optional face count source for the degraded-mode budget. The session
	/// cannot count mesh faces without a resolver; null means 0 faces.</summary>
	public Func<int> FaceCountProvider { get; set; }
	/// <summary>Optional process RSS (MB) source for the degraded-mode budget. Null means 0 MB.</summary>
	public Func<int> RssMbProvider { get; set; }

	readonly List<string> _moving = new List<string>();
	Dictionary<string, Xform> _rest = new Dictionary<string, Xform>(StringComparer.Ordinal);
	float _appliedValue;
	string _restJson;
	EditHistory _history;

	public AssemblyMotionSession(AssemblyDocument doc) => Doc = doc ?? throw new ArgumentNullException(nameof(doc));

	bool IsGroundedId(string id) => AssemblyGrounding.IsGrounded(Doc, id);

	/// <summary>Joints that can be dragged right now: hinge/slider, both ends
	/// present, usable frames/axis, and not locked (Minimum != Maximum).</summary>
	public static IReadOnlyList<AssemblyJoint> MovableJoints(AssemblyDocument doc)
	{
		if (doc == null) return Array.Empty<AssemblyJoint>();
		var occ = Index(doc);
		var joints = new List<AssemblyJoint>();
		foreach (var joint in doc.Joints ?? Enumerable.Empty<AssemblyJoint>())
		{
			if (joint == null || joint.Kind is not ("hinge" or "slider")) continue;
			if (!occ.ContainsKey(joint.OccurrenceA ?? "") || !occ.ContainsKey(joint.OccurrenceB ?? "")) continue;
			if (joint.OccurrenceA == joint.OccurrenceB) continue;
			if (joint.Minimum.HasValue && joint.Maximum.HasValue && joint.Minimum.Value >= joint.Maximum.Value) continue;
			if (joint.Kind == "hinge")
			{
				if (!AssemblyHinge.TryGetHingeAngle(doc, joint, out _, out _)) continue;
			}
			else if (!TryGetSliderPosition(doc, joint, out _, out _)) continue;
			joints.Add(joint);
		}
		return joints;
	}

	/// <summary>Why a joint cannot be dragged, or null when it can.</summary>
	public static string LockedReason(AssemblyDocument doc, AssemblyJoint joint)
	{
		if (joint == null) return "No joint selected.";
		if (joint.Kind is not ("hinge" or "slider")) return $"A {joint.Kind} joint cannot be dragged.";
		var occ = Index(doc);
		if (!occ.ContainsKey(joint.OccurrenceA ?? "") || !occ.ContainsKey(joint.OccurrenceB ?? ""))
			return "One end of this joint is missing from the assembly.";
		if (joint.OccurrenceA == joint.OccurrenceB) return "A joint must connect two different parts.";
		if (joint.Minimum.HasValue && joint.Maximum.HasValue && joint.Minimum.Value > joint.Maximum.Value)
			return "This joint's limits are inverted.";
		if (joint.Minimum.HasValue && joint.Maximum.HasValue && joint.Minimum.Value == joint.Maximum.Value)
			return "This joint is locked: its limits are equal, so it cannot move.";
		if (AssemblyGrounding.IsGrounded(doc, joint.OccurrenceB))
			return $"Part '{occ[joint.OccurrenceB].Name}' is grounded and cannot move.";
		if (joint.Kind == "hinge")
		{
			if (!AssemblyHinge.TryGetHingeAngle(doc, joint, out _, out var why)) return why;
		}
		else if (!TryGetSliderPosition(doc, joint, out _, out var why)) return why;
		return null;
	}

	static Dictionary<string, AssemblyOccurrence> Index(AssemblyDocument doc)
	{
		var d = new Dictionary<string, AssemblyOccurrence>(StringComparer.Ordinal);
		if (doc == null) return d;
		foreach (var o in doc.Occurrences ?? Enumerable.Empty<AssemblyOccurrence>())
			if (o != null && !string.IsNullOrEmpty(o.Id) && !d.ContainsKey(o.Id)) d[o.Id] = o;
		return d;
	}

	static Dictionary<string, List<(string Other, string Kind, AssemblyJoint Joint)>> Adjacency(AssemblyDocument doc)
	{
		var adj = new Dictionary<string, List<(string, string, AssemblyJoint)>>(StringComparer.Ordinal);
		void Link(string from, string to, string kind, AssemblyJoint joint)
		{
			if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
			if (!adj.TryGetValue(from, out var list)) adj[from] = list = new List<(string, string, AssemblyJoint)>();
			list.Add((to, kind, joint));
		}
		foreach (var joint in doc?.Joints ?? Enumerable.Empty<AssemblyJoint>())
		{
			if (joint == null || joint.Kind is not ("fixed" or "hinge" or "slider")) continue;
			Link(joint.OccurrenceA, joint.OccurrenceB, joint.Kind, joint);
			Link(joint.OccurrenceB, joint.OccurrenceA, joint.Kind, joint);
		}
		return adj;
	}

	static bool IsRigidPlacement(Xform transform) =>
		AssemblyTransform.FromXform(transform).IsRigidFrame(out _);

	/// <summary>
	/// Start testing a joint. Validates everything first and only commits on
	/// success, so a failed Begin leaves the previous joint active (at its
	/// rest — a preview in progress is restored first, so the new rest is
	/// measured from the rest pose, not from a previewed one).
	/// </summary>
	public bool Begin(AssemblyJoint joint) => Begin(joint, null);

	/// <summary>
	/// Start testing a joint with an EditHistory for EndSession to register its
	/// single commit entry against. A null history means the session applies
	/// without history registration.
	/// </summary>
	public bool Begin(AssemblyJoint joint, EditHistory history)
	{
		Problem = null;
		if (joint == null) { Problem = "No joint selected."; return false; }
		// Switching joints restores the rest pose first, so the new session's
		// rest value is measured from the rest pose, not from a preview in
		// progress. The previous session stays active (at rest) if this Begin
		// then fails validation.
		if (IsActive) RestoreRest();
		var blocked = LockedReason(Doc, joint);
		if (blocked != null) { Problem = blocked; return false; }

		var occ = Index(Doc);
		if (IsGroundedId(joint.OccurrenceB))
		{
			Problem = $"Part '{occ[joint.OccurrenceB].Name}' is grounded and cannot move.";
			return false;
		}

		// Current absolute value: hinge angle from AssemblyHinge, slider
		// position from the frame origins along the shared axis.
		float restValue = 0;
		if (joint.Kind == "hinge")
		{
			if (!AssemblyHinge.TryGetHingeAngle(Doc, joint, out restValue, out var why)) { Problem = why; return false; }
		}
		else if (!TryGetSliderPosition(Doc, joint, out restValue, out var why)) { Problem = why; return false; }

		// Anchored side: the root, everything grounded, and everything fixed
		// to the root. Moving side: everything reachable from B through any
		// joint, holding every other joint's value. Reaching the anchored
		// side is a closed loop; reaching a grounded part names it.
		var adj = Adjacency(Doc);
		var anchored = new HashSet<string>(StringComparer.Ordinal) { joint.OccurrenceA };
		foreach (var o in Doc.Occurrences ?? Enumerable.Empty<AssemblyOccurrence>())
			if (o != null && AssemblyGrounding.IsGrounded(o)) anchored.Add(o.Id);
		var queue = new Queue<string>();
		queue.Enqueue(joint.OccurrenceA);
		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			if (!adj.TryGetValue(current, out var edges)) continue;
			foreach (var (other, kind, _) in edges)
				if (kind == "fixed" && anchored.Add(other)) queue.Enqueue(other);
		}

		var moving = new List<string>();
		var movingSet = new HashSet<string>(StringComparer.Ordinal) { joint.OccurrenceB };
		moving.Add(joint.OccurrenceB);
		string problem = null;
		queue.Enqueue(joint.OccurrenceB);
		while (queue.Count > 0 && problem == null)
		{
			var current = queue.Dequeue();
			if (!adj.TryGetValue(current, out var edges)) continue;
			foreach (var (other, kind, edgeJoint) in edges)
			{
				if (edgeJoint == joint) continue; // the dragged joint itself
				if (anchored.Contains(other))
				{
					problem = IsGroundedId(other)
						? $"Motion is blocked: part '{occ[other].Name}' is grounded."
						: "This joint is part of a closed loop: another joint holds its moving part to the anchor. Loops need the full joint solver (Assembly 08) and are not dragged.";
					break;
				}
				if (movingSet.Add(other)) { moving.Add(other); queue.Enqueue(other); }
			}
		}
		if (problem != null) { Problem = problem; return false; }

		// Rigid placements only: a scaled or mirrored occurrence has no
		// single axis to rotate about or slide along.
		foreach (var id in moving)
		{
			var placement = occ[id].Transform;
			string why = null;
			if (placement == null || !placement.IsValid(out why))
			{
				Problem = $"Part '{occ[id].Name}' placement is not valid: {why ?? "Transform is missing."}";
				return false;
			}
			if (!IsRigidPlacement(placement.ToXform()))
			{
				Problem = $"Part '{occ[id].Name}' carries scale or a mirror; drag-testing needs a rigid placement.";
				return false;
			}
		}

		// Validation passed. Capture the new session's rest from the (restored)
		// document state.
		Joint = joint;
		Value = RestValue = restValue;
		_appliedValue = restValue;
		AtMinLimit = AtMaxLimit = false;
		_moving.Clear();
		_moving.AddRange(moving);
		_rest = new Dictionary<string, Xform>(StringComparer.Ordinal);
		foreach (var id in _moving) _rest[id] = occ[id].Transform.ToXform();
		_restJson = Doc.ToJson();
		_history = history;
		RefreshDegraded();
		return true;
	}

	/// <summary>Recompute IsDegraded / DegradedMessage from the current document
	/// counts and the optional face-count / RSS providers.</summary>
	public void RefreshDegraded()
	{
		var parts = Doc.Occurrences?.Count ?? 0;
		var joints = Doc.Joints?.Count ?? 0;
		var faces = FaceCountProvider?.Invoke() ?? 0;
		var rssMb = RssMbProvider?.Invoke() ?? 0;
		var result = AssemblyMotionBudget.Evaluate(parts, joints, faces, rssMb, lowMemoryWarning: false);
		IsDegraded = result.Level == AssemblyMotionBudgetLevel.Degraded;
		DegradedMessage = result.Message;
	}

	/// <summary>Set the preview value (absolute), clamped to the joint limits.
	/// Pinned exactly at a limit, so holding past it never jitters. Writes
	/// nothing: call ApplyPreview to move the document.</summary>
	public float SetValue(float value)
	{
		if (!IsActive) return 0;
		AtMinLimit = AtMaxLimit = false;
		if (Joint.Kind == "hinge") value = AssemblyHinge.ClampToLimits(value, Minimum, Maximum);
		else value = AssemblySlider.ClampToLimits(Joint, value);
		if (Minimum.HasValue && value == Minimum.Value) AtMinLimit = true;
		if (Maximum.HasValue && value == Maximum.Value) AtMaxLimit = true;
		Value = value;
		return value;
	}

	/// <summary>Drag in screen pixels: the full limit range maps to 400 px,
	/// or a full turn / 20 in when the joint has no limits. Sets the preview
	/// value only; it never applies to the document. In degraded mode callers
	/// should drag with this and call ApplyDeferred on finger-up.</summary>
	public float DragByPixels(float pixels)
	{
		if (!IsActive) return 0;
		var range = Minimum.HasValue && Maximum.HasValue
			? Maximum.Value - Minimum.Value
			: IsHinge ? MathF.PI * 2f : 20f;
		return SetValue(Value + pixels * range / 400f);
	}

	/// <summary>
	/// Write the current preview value into the document. Hinge motion is
	/// AssemblyHinge's; slider motion is the chain walk around
	/// AssemblySlider.TrySolve. Atomic per call: a failed apply moves nothing,
	/// and the value reverts to the last applied one.
	/// </summary>
	public bool ApplyPreview()
	{
		if (!IsActive) return false;
		bool ok;
		string why = null;
		if (IsHinge) ok = AssemblyHinge.TrySetHingeAngle(Doc, Joint, Value, IsGroundedId, out why);
		else ok = TrySetSliderPosition(Doc, Joint, Value, IsGroundedId, out why);
		if (!ok)
		{
			Problem = why ?? "This motion could not be applied.";
			Value = _appliedValue;
			return false;
		}
		Problem = null;
		_appliedValue = Value;
		return true;
	}

	/// <summary>Finger-up apply for degraded mode: applies the deferred preview
	/// value once. Identical to ApplyPreview; named for the drag path.</summary>
	public bool ApplyDeferred() => ApplyPreview();

	/// <summary>Write the rest pose back into the document.</summary>
	public void RestoreRest()
	{
		if (!IsActive) return;
		var occ = Index(Doc);
		foreach (var (id, rest) in _rest)
			if (occ.TryGetValue(id, out var occurrence))
				occurrence.Transform = AssemblyTransform.FromXform(rest);
		Value = RestValue;
		_appliedValue = RestValue;
		Problem = null;
	}

	/// <summary>Back to the rest value and apply it: SetValue(RestValue) + ApplyPreview.</summary>
	public bool Reset()
	{
		if (!IsActive) return false;
		SetValue(RestValue);
		return ApplyPreview();
	}

	/// <summary>
	/// End the session. Commit applies the previewed pose and registers exactly
	/// ONE EditHistory entry (rest JSON -> current JSON) when the pose is dirty
	/// and a history was passed to Begin; an unchanged commit registers nothing.
	/// Cancel restores the rest document JSON directly and leaves no undo step.
	/// On commit of a slider joint the stored position is synced first; if the
	/// pose applied but the stored position could not be written, the whole
	/// apply is rolled back to rest rather than leaving geometry and stored
	/// position disagreeing, and this returns false. Returns false when no
	/// session is active.
	/// </summary>
	public bool EndSession(bool commit)
	{
		if (!IsActive) return false;
		var joint = Joint;
		var restJson = _restJson;
		var history = _history;
		var value = Value;
		var dirty = IsDirty;
		var isSlider = !IsHinge;

		Joint = null;
		Value = RestValue = 0f;
		_appliedValue = 0f;
		AtMinLimit = AtMaxLimit = false;
		_moving.Clear();
		_rest.Clear();
		_restJson = null;
		_history = null;
		Problem = null;
		IsDegraded = false;
		DegradedMessage = "";

		if (!commit)
		{
			AssemblyHistory.Restore(Doc, restJson);
			return true;
		}
		if (!dirty) return true;
		if (isSlider)
		{
			if (!AssemblySlider.TrySetPosition(joint, value, out var storeErr))
			{
				AssemblyHistory.Restore(Doc, restJson);
				return false;
			}
		}
		RegisterHistory(Doc, history, $"Test motion: {DisplayName(joint)}", restJson);
		return true;
	}

	/// <summary>
	/// Drive SetValue + ApplyPreview stepwise from <paramref name="from"/> to
	/// <paramref name="to"/> in <paramref name="steps"/> equal steps, returning
	/// the value applied at each step (steps + 1 entries, endpoints included).
	/// Stops at the first failed apply and returns the values applied so far.
	/// No history entries are added per sweep; only EndSession(commit) registers
	/// the session's single entry. Numeric sweeps now, future pointer adapter.
	/// </summary>
	public IReadOnlyList<float> Sweep(float from, float to, int steps)
	{
		var applied = new List<float>();
		if (!IsActive || steps < 1) return applied;
		for (var i = 0; i <= steps; i++)
		{
			var target = from + (to - from) * i / steps;
			SetValue(target);
			if (!ApplyPreview()) break;
			applied.Add(Value);
		}
		return applied;
	}

	public float ToDisplay(float value) => IsHinge ? value * 180f / MathF.PI : value;
	public float FromDisplay(float display) => IsHinge ? display * MathF.PI / 180f : display;

	public string ValueText() => IsHinge ? AssemblyHinge.FormatAngle(Value) : $"{Value:0.00} in";

	public string LimitsText() => IsHinge
		? AssemblyHinge.LimitText(Minimum, Maximum)
		: AssemblySlider.DescribeLimits(Joint);

	static string DisplayName(AssemblyJoint joint)
	{
		if (joint == null) return "joint";
		if (joint.ExtensionData != null
			&& joint.ExtensionData.TryGetValue("name", out var nameElement)
			&& nameElement.ValueKind == JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(nameElement.GetString()))
			return nameElement.GetString();
		return $"{joint.Kind} joint";
	}

	static void RegisterHistory(AssemblyDocument doc, EditHistory history, string description, string beforeJson)
	{
		if (history == null) return;
		var afterJson = doc.ToJson();
		history.Execute(new DelegateEditCommand(description,
			execute: () => AssemblyHistory.Restore(doc, afterJson),
			undo: () => AssemblyHistory.Restore(doc, beforeJson),
			isNoOp: () => beforeJson == afterJson));
	}

	// ---- Slider: delegated to AssemblySlider (Assembly 09) ----
	//
	// The stored position (ExtensionData "position") is the joint's record, but
	// the drag starts from the MEASURED travel: geometry is what the user sees,
	// and a stale stored value must never teleport a part on first drag. The
	// stored value is synced on commit (EndSession), never per drag sample, so
	// preview cannot corrupt rest. AssemblySlider.TrySolve places B; this file
	// only adds the chain walk (grounded/loop refusal) around it.

	/// <summary>
	/// Slider position in inches, read back from the stored occurrence
	/// transforms (AssemblySlider.TryMeasure). Zero is frame origins
	/// coincident, positive is along Part A +Z. Fails when the frames are not
	/// usable, the axes do not agree, or the parts sit off the axis.
	/// </summary>
	public static bool TryGetSliderPosition(AssemblyDocument doc, AssemblyJoint joint, out float position, out string why)
	{
		position = 0f;
		why = null;
		if (doc == null) { why = "Assembly document is missing."; return false; }
		if (joint == null) { why = "Joint is missing."; return false; }
		var occA = doc.Occurrences?.Find(o => o != null && o.Id == joint.OccurrenceA);
		var occB = doc.Occurrences?.Find(o => o != null && o.Id == joint.OccurrenceB);
		if (occA == null || occB == null) { why = "Both slider occurrences must exist in the assembly."; return false; }
		if (!AssemblySlider.TryMeasure(joint, occA, occB, out position, out var offAxis, out why)) return false;
		if (offAxis > 1e-3f)
		{
			why = $"This slider's parts sit {offAxis:0.###} in off its axis — fix the placement before dragging it.";
			return false;
		}
		var worldA = occA.Transform.ToXform() * joint.FrameA.ToXform();
		var worldB = occB.Transform.ToXform() * joint.FrameB.ToXform();
		var axisA = worldA.TransformDirection(new Vec3(0, 0, 1)).Normal;
		var axisB = worldB.TransformDirection(new Vec3(0, 0, 1)).Normal;
		if (Vec3.Dot(axisA, axisB) < 0.999f)
		{
			why = "Slider axes are misaligned: both connector frames must point along one axis.";
			return false;
		}
		// AssemblySlider.TrySolve locks B's orientation to A, so a B that is
		// rotated relative to A would snap on first drag. Refuse instead of
		// silently rewriting the rest orientation.
		var relative = worldA.Inverse * worldB;
		if ((relative.X - new Vec3(1, 0, 0)).Length > 1e-2f
			|| (relative.Y - new Vec3(0, 1, 0)).Length > 1e-2f
			|| (relative.Z - new Vec3(0, 0, 1)).Length > 1e-2f)
		{
			why = "Slider parts are rotated relative to each other: the slider holds Part B's orientation to Part A.";
			return false;
		}
		return true;
	}

	/// <summary>
	/// Set a slider to an absolute position (inches), clamped to its limits.
	/// Part A is the root and never moves; Part B slides along the shared axis
	/// (AssemblySlider.TrySolve) and anything joined to B moves rigidly with
	/// it. A move blocked by a grounded occurrence, a closed loop back to the
	/// root, or invalid geometry fails and moves nothing.
	/// </summary>
	public static bool TrySetSliderPosition(AssemblyDocument doc, AssemblyJoint joint, float position,
		Func<string, bool> isGrounded, out string why)
	{
		why = null;
		if (!float.IsFinite(position)) { why = "Slider position must be a finite number."; return false; }
		if (joint != null && joint.Minimum.HasValue && joint.Maximum.HasValue && joint.Minimum.Value > joint.Maximum.Value)
		{ why = "Slider limits are inverted."; return false; }
		if (!TryGetSliderPosition(doc, joint, out var current, out why)) return false;
		var target = AssemblySlider.ClampToLimits(joint, position);
		if (MathF.Abs(target - current) < 1e-6f) return true;

		var occA = doc.Occurrences?.Find(o => o != null && o.Id == joint.OccurrenceA);
		var occB = doc.Occurrences?.Find(o => o != null && o.Id == joint.OccurrenceB);
		if (!AssemblySlider.TrySolve(joint, occA, occB, target, out var solvedB, out why, out _)) return false;
		var delta = solvedB * occB.Transform.ToXform().Inverse;

		var move = new List<AssemblyOccurrence>();
		var seen = new HashSet<string>(StringComparer.Ordinal) { joint.OccurrenceA };
		var queue = new Queue<string>();
		if (isGrounded?.Invoke(joint.OccurrenceB) == true)
		{
			why = $"Occurrence '{occB.Name}' is grounded and cannot move.";
			return false;
		}
		seen.Add(joint.OccurrenceB);
		queue.Enqueue(joint.OccurrenceB);
		move.Add(occB);
		while (queue.Count > 0)
		{
			var currentId = queue.Dequeue();
			foreach (var other in doc.Joints ?? new List<AssemblyJoint>())
			{
				if (other == null || other == joint) continue;
				string neighbour = null;
				if (other.OccurrenceA == currentId) neighbour = other.OccurrenceB;
				else if (other.OccurrenceB == currentId) neighbour = other.OccurrenceA;
				if (neighbour == null || seen.Contains(neighbour))
				{
					if (neighbour != null && neighbour == joint.OccurrenceA)
					{
						why = "Slider motion is blocked: another joint holds this chain to the root (closed loop).";
						return false;
					}
					continue;
				}
				seen.Add(neighbour);
				if (isGrounded?.Invoke(neighbour) == true)
				{
					why = $"Slider motion is blocked: occurrence '{neighbour}' is grounded.";
					return false;
				}
				var next = doc.Occurrences?.Find(o => o != null && o.Id == neighbour);
				if (next == null) { why = $"Occurrence '{neighbour}' does not exist."; return false; }
				if (next.Transform == null || !next.Transform.IsValid(out _))
				{ why = $"Occurrence '{next.Name}' transform is missing or invalid."; return false; }
				if (!IsRigidPlacement(next.Transform.ToXform()))
				{ why = $"Occurrence '{next.Name}' carries scale or a mirror; slider solving needs a rigid placement."; return false; }
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
}

/// <summary>Degraded-mode level for motion previews: Full is live drag, Degraded
/// solves on finger-up only.</summary>
public enum AssemblyMotionBudgetLevel
{
	Full,
	Degraded,
}

/// <summary>Result of AssemblyMotionBudget.Evaluate: the level and the message
/// to show the user ("" when Full).</summary>
public sealed record AssemblyMotionBudgetResult(AssemblyMotionBudgetLevel Level, string Message);

/// <summary>
/// Performance budget for motion previews, mirroring Mobile's
/// AssemblyPerformanceBudget thresholds (25 parts / 24 joints / 150k faces /
/// 1200 MB RSS). Over budget the pose updates on finger-up instead of live;
/// nothing is removed.
/// </summary>
public static class AssemblyMotionBudget
{
	public const int MaxPartsFull = 25;
	public const int MaxJointsFull = 24;
	public const int BudgetFaces = 150000;
	public const int BudgetRamMb = 1200;

	public static AssemblyMotionBudgetResult Evaluate(int parts, int joints, int faces, int rssMb, bool lowMemoryWarning)
	{
		var reasons = new List<string>();
		if (parts > MaxPartsFull || joints > MaxJointsFull)
			reasons.Add($"Large assembly ({parts} parts). Dragging solves when you lift your finger, to stay smooth. Nothing is removed.");
		if (faces > BudgetFaces)
			reasons.Add($"This assembly has {faces / 1000}k faces, over the {BudgetFaces / 1000}k phone budget. Hide parts you are not moving.");
		if (rssMb > BudgetRamMb || lowMemoryWarning)
			reasons.Add("Memory is low. Close other apps or hide parts you are not moving.");
		if (reasons.Count == 0) return new AssemblyMotionBudgetResult(AssemblyMotionBudgetLevel.Full, "");
		var message = string.Join(" ", reasons);
		if (message.Length > 240) message = message.Substring(0, 239) + "…";
		return new AssemblyMotionBudgetResult(AssemblyMotionBudgetLevel.Degraded, message);
	}
}

/// <summary>
/// Named saved poses. No Mobile counterpart — Desktop-side design (Assembly 10).
///
/// A pose is a named snapshot of absolute joint values, stored in
/// AssemblyDocument ExtensionData under key "poses" as a JSON array of
/// {name, values: {jointId: value}}, where value is the absolute hinge angle
/// (radians) or slider travel (inches). The live document holds the current
/// pose; the session's RestValue is the rest. Poses round-trip through
/// AssemblyDocument ToJson/FromJson like any other extension data.
///
/// Saving a pose with an existing name REPLACES the stored pose (documented).
/// Applying a pose is atomic: any failure restores the pre-apply document and
/// reports the failure; nothing moves. Limits win over stored values: a pose
/// value outside a joint's current limits is clamped by the same setters the
/// session uses, and a slider's stored position is synced to the value that
/// actually landed.
/// </summary>
public static class AssemblySavedPoses
{
	public const string PosesKey = "poses";

	static readonly JsonSerializerOptions PoseJsonOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	/// <summary>A single named pose: absolute joint values by joint id.</summary>
	public sealed class PoseRecord
	{
		public string Name { get; set; } = "";
		public Dictionary<string, float> Values { get; set; } = new Dictionary<string, float>(StringComparer.Ordinal);
	}

	/// <summary>Names of the saved poses, in stored order.</summary>
	public static IReadOnlyList<string> ListPoses(AssemblyDocument doc)
	{
		var names = new List<string>();
		foreach (var pose in ReadPoses(doc))
			if (!string.IsNullOrEmpty(pose.Name)) names.Add(pose.Name);
		return names;
	}

	/// <summary>
	/// Capture the CURRENT absolute value of every movable joint (hinge via
	/// TryGetHingeAngle, slider via measured TryGetSliderPosition) under
	/// <paramref name="name"/>. Blank name refused. Duplicate name replaces
	/// the stored pose. One history entry. Fails when no movable joint has a
	/// readable value.
	/// </summary>
	public static bool SavePose(AssemblyDocument doc, EditHistory history, string name, out string why)
	{
		why = null;
		if (doc == null) { why = "Assembly document is missing."; return false; }
		if (string.IsNullOrWhiteSpace(name)) { why = "A pose name cannot be empty."; return false; }
		var values = new Dictionary<string, float>(StringComparer.Ordinal);
		foreach (var joint in AssemblyMotionSession.MovableJoints(doc))
		{
			if (joint.Kind == "hinge")
			{
				if (AssemblyHinge.TryGetHingeAngle(doc, joint, out var angle, out _)) values[joint.Id] = angle;
			}
			else if (AssemblyMotionSession.TryGetSliderPosition(doc, joint, out var travel, out _))
			{
				values[joint.Id] = travel;
			}
		}
		if (values.Count == 0) { why = "No movable joint has a readable value, so there is nothing to save."; return false; }
		var poses = ReadPoses(doc);
		poses.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal));
		poses.Add(new PoseRecord { Name = name, Values = values });
		try
		{
			AssemblyHistory.ApplyWithHistory(doc, history, $"Save pose '{name}'", () => WritePoses(doc, poses));
		}
		catch (InvalidOperationException e)
		{
			why = e.Message;
			return false;
		}
		return true;
	}

	/// <summary>
	/// Apply a saved pose through the same setters the session uses (hinge
	/// TrySetHingeAngle, slider TrySetSliderPosition with its chain walk).
	/// Atomic: any failure restores the pre-apply document JSON and reports
	/// the failure; nothing moves. One history entry on success.
	/// </summary>
	public static bool ApplyPose(AssemblyDocument doc, EditHistory history, string name, out string why)
	{
		why = null;
		if (doc == null) { why = "Assembly document is missing."; return false; }
		var pose = ReadPoses(doc).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
		if (pose == null) { why = $"There is no saved pose called '{name}'."; return false; }
		var beforeJson = doc.ToJson();
		Func<string, bool> isGrounded = id => AssemblyGrounding.IsGrounded(doc, id);
		foreach (var (jointId, value) in pose.Values)
		{
			var joint = doc.Joints?.Find(j => j != null && j.Id == jointId);
			if (joint == null)
			{
				AssemblyHistory.Restore(doc, beforeJson);
				why = $"Saved pose '{name}' names joint '{jointId}', which is no longer in the assembly.";
				return false;
			}
			if (joint.Kind == "hinge")
			{
				if (!AssemblyHinge.TrySetHingeAngle(doc, joint, value, isGrounded, out why))
				{
					AssemblyHistory.Restore(doc, beforeJson);
					return false;
				}
			}
			else if (joint.Kind == "slider")
			{
				if (!AssemblyMotionSession.TrySetSliderPosition(doc, joint, value, isGrounded, out why))
				{
					AssemblyHistory.Restore(doc, beforeJson);
					return false;
				}
				if (!AssemblyMotionSession.TryGetSliderPosition(doc, joint, out var landed, out why))
				{
					AssemblyHistory.Restore(doc, beforeJson);
					return false;
				}
				if (!AssemblySlider.TrySetPosition(joint, landed, out var storeErr))
				{
					AssemblyHistory.Restore(doc, beforeJson);
					why = storeErr;
					return false;
				}
			}
			else
			{
				AssemblyHistory.Restore(doc, beforeJson);
				why = $"Saved pose '{name}' names joint '{jointId}', a {joint.Kind} joint that cannot be posed.";
				return false;
			}
		}
		var afterJson = doc.ToJson();
		if (history != null)
		{
			history.Execute(new DelegateEditCommand($"Apply pose '{name}'",
				execute: () => AssemblyHistory.Restore(doc, afterJson),
				undo: () => AssemblyHistory.Restore(doc, beforeJson),
				isNoOp: () => beforeJson == afterJson));
		}
		return true;
	}

	/// <summary>Delete a saved pose. One history entry. False when the name
	/// does not exist.</summary>
	public static bool DeletePose(AssemblyDocument doc, EditHistory history, string name)
	{
		if (doc == null) return false;
		var poses = ReadPoses(doc);
		if (!poses.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal))) return false;
		poses.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal));
		try
		{
			AssemblyHistory.ApplyWithHistory(doc, history, $"Delete pose '{name}'", () => WritePoses(doc, poses));
		}
		catch (InvalidOperationException)
		{
			return false;
		}
		return true;
	}

	static List<PoseRecord> ReadPoses(AssemblyDocument doc)
	{
		var poses = new List<PoseRecord>();
		if (doc?.ExtensionData == null) return poses;
		if (!doc.ExtensionData.TryGetValue(PosesKey, out var stored)) return poses;
		if (stored.ValueKind != JsonValueKind.Array) return poses;
		foreach (var element in stored.EnumerateArray())
		{
			try
			{
				var pose = element.Deserialize<PoseRecord>(PoseJsonOptions);
				if (pose != null && !string.IsNullOrEmpty(pose.Name)) poses.Add(pose);
			}
			catch (JsonException)
			{
				// Malformed stored entry is skipped, never a crash.
			}
		}
		return poses;
	}

	static void WritePoses(AssemblyDocument doc, List<PoseRecord> poses)
	{
		if (doc.ExtensionData == null) doc.ExtensionData = new Dictionary<string, JsonElement>();
		if (poses.Count == 0)
		{
			doc.ExtensionData.Remove(PosesKey);
			if (doc.ExtensionData.Count == 0) doc.ExtensionData = null;
			return;
		}
		doc.ExtensionData[PosesKey] = JsonSerializer.SerializeToElement(poses, PoseJsonOptions);
	}
}
