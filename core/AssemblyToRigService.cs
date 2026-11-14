using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;

namespace Marionette.Desktop;

// AS13: convert supported assembly joints to a reusable rig.
//
// Faithful Desktop port of Mobile app/AssemblyToRig.cs (Assembly 17):
// one bone per rigid group of occurrences (occurrences joined by fixed
// joints merge into one bone), parented along the hinge/slider joint tree,
// with each occurrence's body pinned to its bone in BodyBoneMap. Rigid
// weights are derived on bind from those pins (see AssemblyToRigResult.Weights);
// the existing Rig > Bind path honours them.
//
// Rules the converter follows (see the Assembly 13 Hub task):
//   - The CAD assembly is the authoritative saved source and is NEVER mutated.
//     The conversion is a SNAPSHOT: later assembly edits do not update the rig.
//     Convert again to refresh. The source JSON at conversion time is kept in
//     AssemblyToRigResult.SourceAssemblyJson so a later check can prove the
//     assembly survived untouched.
//   - Only grounded, acyclic joint trees convert. The grounded (root)
//     occurrence is the first one marked grounded (AS03 AssemblyGrounding),
//     or the caller's explicit pick; every other connected component gets its
//     own root bone, and lone occurrences with no joints become standalone
//     root bones. All of it is listed in the plan.
//   - Loops, second-parent joints, unsupported kinds, missing occurrences,
//     bad frames and inverted limits never produce an invalid skeleton: the
//     offending joint is listed in Plan.Skipped with the exact reason and the
//     rest converts as an explicitly described safe subset.
//   - Hinge limits (radians) and slider limits (inches) are recorded on the
//     mapped joint. The rig has no LimitRotation constraint primitive, so hinge
//     limits are recorded data + a plan warning, NOT solver-enforced; slider
//     motion is a translation of the bone's Local origin along the joint axis,
//     clamped with ClampSliderTravel. Both limits are keyframeable through
//     the existing per-bone RigTrack pose (full Xform), so no new
//     serialization was invented.
//   - Current pose: a hinge's angle derives from the occurrence transforms
//     (AS12's convention); a slider's travel is stored on the joint as
//     ExtensionData["position"] in inches (AS09's convention) and the
//     converter applies it to the child group's rest pose at plan time,
//     clamped to the joint limits. Both are recorded on the mapped joint.
//   - Units: lengths in inches, angles in radians, Effigy Z-up right-handed
//     coordinates, exactly as AssemblyDocument declares.
//   - Occurrence -> body mapping is by convention: the definition's
//     SourceReference fragment after '#' is the studio body id
//     ("studio-body#body-1"); a full reference equal to a body id also works.
//     An occurrence whose body is not in this studio still gets a bone, but
//     no pin and no weights; that is reported, never silently skipped.
//
// Desktop deviations from Mobile (deliberate, not drift):
//   - Grounded reads use AS03 AssemblyGrounding.IsGrounded (canonical) rather
//     than a local copy; IsGrounded is re-exposed here for Mobile-API callers.
//   - HingeAngle / SliderPosition are mirrored in-file, exactly as Mobile does
//     ("mirrored here so this file never depends on another worker's type").
//     The tests write stored slider travel through the public AS09 API
//     (AssemblySlider.TrySetPosition); Plan only reads the stored value.
//   - Desktop AssemblyDocument.Validate() also reports mate-payload issues
//     (code "invalid-mate-record", AS07 records). A mate payload describes how
//     a joint was made, not the joint's frames, so those issues never block
//     conversion of the joint itself; the payload stays in the assembly
//     untouched. The joint filter therefore skips codes
//     "unsupported-joint-kind" and "invalid-mate-record" (Mobile filtered the
//     joint-kind case by message prefix; the code filter is the same rule).
//   - PartStudio.Rig is a non-null field on Desktop, so there is no "studio
//     has no rig" refusal in Apply; ExistingBoneCount is studio.Rig.Count.
//   - NEW on Desktop: ConvertToNewStudio (explicit new destination, per the
//     ticket) — a fresh PartStudio with cloned bodies, the rig built on it,
//     and a JSON round-trip copy of the assembly attached as its snapshot
//     record. The source studio and the source assembly object are untouched.
//     Apply itself keeps Mobile's shape (append to the given studio's rig;
//     the caller passes the destination explicitly).
//   - Apply is not an undoable edit: the caller wraps it in a checkpoint when
//     the destination is a live document. Plan is pure and repeatable.
//
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
public static class AssemblyToRig
{
	/// <summary>Shown in the UI wherever the conversion result appears.</summary>
	public const string SnapshotNote =
		"Snapshot: this rig was converted from the assembly as it was at conversion time. " +
		"Later assembly edits do not update it — convert again to refresh. " +
		"The assembly itself was not changed.";

	/// <summary>Shortest bone the converter will create, in inches.</summary>
	public const float MinBoneLength = 0.5f;

	// ---------------------------------------------------------------- plan

	public sealed class PlannedBone
	{
		public string Name;
		public string ParentName; // null = root bone
		public Vec3 Head; // world bind space (Effigy Z-up, inches)
		public Vec3 Tail;
		public Vec3 UpHint; // hinge axis for hinge children; Zero = no hint
		public bool HasUpHint;
		public string Kind; // "root", "hinge", "slider", "standalone"
		public string JointId; // joint that parents this bone, null for roots
		public List<string> OccurrenceIds = new();
		public List<string> OccurrenceNames = new();
		public string DisplayName => OccurrenceNames.Count > 0 ? OccurrenceNames[0] : Name;
	}

	public sealed class MappedJoint
	{
		public string JointId;
		public string Kind; // "hinge" or "slider"
		public string OccurrenceAName;
		public string OccurrenceBName;
		public string ChildBoneName;
		public Vec3 PivotWorld;
		public Vec3 AxisWorld; // shared local +Z in world space, unit length
		public float? Minimum; // radians (hinge) or inches (slider)
		public float? Maximum;
		public string Unit => Kind == "hinge" ? "rad" : "in";
		/// <summary>Hinge: current angle in radians, read from the occurrence transforms.</summary>
		public float? CurrentAngle;
		/// <summary>Slider: current travel in inches, from the joint's stored position.</summary>
		public float? CurrentTravel;
		public string CurrentText => Kind == "hinge" && CurrentAngle.HasValue
			? $"now at {CurrentAngle.Value:0.###} rad"
			: Kind == "slider" && CurrentTravel.HasValue
				? $"now at {CurrentTravel.Value:0.###} in"
				: "at rest";
		public string LimitText =>
			Minimum.HasValue || Maximum.HasValue
				? $"limits [{(Minimum.HasValue ? Minimum.Value.ToString("0.###") : "—")} .. {(Maximum.HasValue ? Maximum.Value.ToString("0.###") : "—")}] {Unit}"
				: "no limits";
	}

	public sealed class SkippedJoint
	{
		public string JointId;
		public string Kind;
		public string OccurrenceAName;
		public string OccurrenceBName;
		public string Reason;
	}

	public sealed class AssemblyToRigPlan
	{
		public bool Success;
		public string Error; // fatal reason when Success is false
		public List<string> Warnings = new();
		public List<PlannedBone> Bones = new(); // parents always precede children
		public List<MappedJoint> Joints = new();
		public List<SkippedJoint> Skipped = new();
		public string RootOccurrenceId;
		public string RootOccurrenceName;
		public int ExistingBoneCount;
		public string SourceAssemblyJson;
		public int OccurrenceCount;
		public int JointCount;

		/// <summary>Clamp a hinge angle (radians) to the joint's recorded limits.</summary>
		public static float ClampHingeAngle(MappedJoint joint, float radians)
		{
			if (joint == null) return radians;
			if (joint.Minimum.HasValue) radians = MathF.Max(radians, joint.Minimum.Value);
			if (joint.Maximum.HasValue) radians = MathF.Min(radians, joint.Maximum.Value);
			return radians;
		}

		/// <summary>Clamp a slider travel (inches) to the joint's recorded limits.</summary>
		public static float ClampSliderTravel(MappedJoint joint, float inches)
		{
			if (joint == null) return inches;
			if (joint.Minimum.HasValue) inches = MathF.Max(inches, joint.Minimum.Value);
			if (joint.Maximum.HasValue) inches = MathF.Min(inches, joint.Maximum.Value);
			return inches;
		}
	}

	public sealed class AssemblyToRigResult
	{
		public AssemblyToRigPlan Plan;
		public List<string> AppliedBoneNames = new();
		/// <summary>Body id -> bone name, for every body pinned by this conversion.</summary>
		public Dictionary<string, string> BoundBodies = new();
		/// <summary>Body id (or occurrence id when there is no body id) -> why it got no pin.</summary>
		public Dictionary<string, string> UnboundBodies = new();
		/// <summary>
		/// Rigid weights derived at conversion time from the pins (one 1.0 weight per
		/// pinned vertex). In-memory verification only: the persistent record is the
		/// BodyBoneMap pins + skeleton, and the Rig &gt; Bind path re-derives these
		/// weights from the pins whenever it binds.
		/// </summary>
		public SkinWeights Weights;
		public int WeightedVertexCount;
		public string SourceAssemblyJson;
		public string Summary;
	}

	/// <summary>Whether an occurrence is marked grounded: the "grounded" bool in its extension data (AS03's convention, canonical in AssemblyGrounding). A grounded part stays fixed; the converter anchors its joint tree on it.</summary>
	public static bool IsGrounded(AssemblyOccurrence occurrence) => AssemblyGrounding.IsGrounded(occurrence);

	/// <summary>
	/// Build a conversion plan without mutating anything. The plan is safe to show
	/// as a preview; <see cref="Apply"/> executes it.
	/// </summary>
	public static AssemblyToRigPlan Plan(AssemblyDocument assembly, PartStudio studio, string rootOccurrenceId = null)
	{
		var plan = new AssemblyToRigPlan();
		if (assembly == null) { plan.Error = "No assembly document."; return plan; }
		if (studio == null) { plan.Error = "No studio to convert into."; return plan; }
		plan.SourceAssemblyJson = assembly.ToJson();
		plan.ExistingBoneCount = studio.Rig.Count;
		plan.OccurrenceCount = assembly.Occurrences == null ? 0 : assembly.Occurrences.Count;
		plan.JointCount = assembly.Joints == null ? 0 : assembly.Joints.Count;

		if (assembly.OpaqueFutureJson != null)
		{
			plan.Error = $"Assembly schema {assembly.SchemaVersion} is newer than this reader; refusing to convert rather than guessing at it.";
			return plan;
		}
		if (assembly.SchemaVersion != AssemblyDocument.CurrentSchemaVersion
			|| assembly.LengthUnit != AssemblyDocument.EffigyLengthUnit
			|| assembly.AngleUnit != AssemblyDocument.RadianAngleUnit
			|| assembly.CoordinateSystem != AssemblyDocument.EffigyCoordinateSystem)
		{
			plan.Error = "Assembly uses an unsupported schema, length unit, angle unit or coordinate system.";
			return plan;
		}
		if (assembly.Occurrences == null || assembly.Occurrences.Count == 0)
		{
			plan.Error = "Assembly has no occurrences to convert.";
			return plan;
		}

		var occurrences = assembly.Occurrences.Where(o => o != null).ToList();
		var byId = occurrences.ToDictionary(o => o.Id, StringComparer.Ordinal);
		var joints = (assembly.Joints ?? new List<AssemblyJoint>()).Where(j => j != null).ToList();

		// Occurrence world transforms; an occurrence with a broken transform cannot
		// be placed, so it and its joints are skipped with a reason, not guessed.
		var world = new Dictionary<string, Xform>(StringComparer.Ordinal);
		foreach (var occ in occurrences)
		{
			if (occ.Transform == null || !occ.Transform.IsValid(out _))
			{
				plan.Warnings.Add($"Occurrence '{occ.Name}' has no valid transform and was left out of the rig.");
				continue;
			}
			world[occ.Id] = occ.Transform.ToXform();
		}
		var placed = occurrences.Where(o => world.ContainsKey(o.Id)).ToList();
		if (placed.Count == 0)
		{
			plan.Error = "No occurrence has a valid transform.";
			return plan;
		}

		// Slider current travel lives on the joint itself, not in the occurrence
		// transforms: ExtensionData["position"], a JSON number in inches, 0 when
		// absent (AS09's convention, mirrored here so this file never depends on
		// another service's type). Hinge angles, by contrast, derive from the
		// occurrence transforms (AS12).
		static float SliderPosition(AssemblyJoint joint)
		{
			if (joint?.ExtensionData == null
				|| !joint.ExtensionData.TryGetValue("position", out var stored)
				|| stored.ValueKind != JsonValueKind.Number
				|| !stored.TryGetSingle(out var position)
				|| !float.IsFinite(position))
				return 0f;
			return position;
		}

		// Current hinge angle (radians) from the two connector frame worlds.
		// Mirrors AssemblyHinge.TryGetHingeAngle: 0 is frames aligned, positive
		// is right-hand rule about the shared +Z.
		static float HingeAngle(Xform frameWorldA, Xform frameWorldB)
		{
			var relative = frameWorldA.Inverse * frameWorldB;
			var angle = MathF.Atan2(relative.X.y, relative.X.x);
			return MathF.Atan2(MathF.Sin(angle), MathF.Cos(angle));
		}

		// Validate joints against the saved document rules first. Mate-payload
		// issues (code "invalid-mate-record") and the unsupported-kind warning
		// never block conversion of the joint itself; see the file header.
		var issues = assembly.Validate();
		var issueByJoint = new Dictionary<string, List<AssemblyValidationIssue>>(StringComparer.Ordinal);
		foreach (var issue in issues)
			if (!string.IsNullOrEmpty(issue.RecordId))
			{
				if (!issueByJoint.TryGetValue(issue.RecordId, out var list)) issueByJoint[issue.RecordId] = list = new List<AssemblyValidationIssue>();
				list.Add(issue);
			}

		// Fixed joints merge occurrences into rigid groups (union-find).
		var fixedParent = placed.ToDictionary(o => o.Id, o => o.Id, StringComparer.Ordinal);
		string Find(string id)
		{
			while (fixedParent[id] != id) { fixedParent[id] = fixedParent[fixedParent[id]]; id = fixedParent[id]; }
			return id;
		}
		void Union(string a, string b)
		{
			a = Find(a); b = Find(b);
			if (a != b) fixedParent[b] = a;
		}

		var validJoints = new List<AssemblyJoint>();
		foreach (var joint in joints)
		{
			var aName = byId.TryGetValue(joint.OccurrenceA ?? "", out var oa) ? oa.Name : (joint.OccurrenceA ?? "?");
			var bName = byId.TryGetValue(joint.OccurrenceB ?? "", out var ob) ? ob.Name : (joint.OccurrenceB ?? "?");
			string Skip(string reason)
			{
				plan.Skipped.Add(new SkippedJoint { JointId = joint.Id, Kind = joint.Kind ?? "", OccurrenceAName = aName, OccurrenceBName = bName, Reason = reason });
				return null;
			}
			if (!byId.ContainsKey(joint.OccurrenceA ?? "") || !byId.ContainsKey(joint.OccurrenceB ?? ""))
				{ Skip("References an occurrence that does not exist in this assembly."); continue; }
			if (joint.OccurrenceA == joint.OccurrenceB)
				{ Skip("Joins an occurrence to itself."); continue; }
			if (!world.ContainsKey(joint.OccurrenceA) || !world.ContainsKey(joint.OccurrenceB))
				{ Skip("One end has no valid transform, so its rest pose is unknown."); continue; }
			if (joint.Kind is not ("fixed" or "hinge" or "slider"))
				{ Skip($"Joint kind '{joint.Kind}' is not supported; it stays in the assembly untouched."); continue; }
			string whyA = null, whyB = null;
			if (joint.FrameA == null || joint.FrameB == null
				|| !joint.FrameA.IsRigidFrame(out whyA) || !joint.FrameB.IsRigidFrame(out whyB))
				{ Skip($"Connector frame is not a rigid frame: {whyA ?? whyB}"); continue; }
			if ((joint.Minimum.HasValue && !float.IsFinite(joint.Minimum.Value))
				|| (joint.Maximum.HasValue && !float.IsFinite(joint.Maximum.Value)))
				{ Skip("Joint limits must be finite numbers."); continue; }
			if (joint.Minimum.HasValue && joint.Maximum.HasValue && joint.Minimum > joint.Maximum)
				{ Skip($"Minimum {joint.Minimum} exceeds maximum {joint.Maximum}."); continue; }
			if (issueByJoint.TryGetValue(joint.Id, out var jointIssues))
			{
				var fatal = jointIssues.Where(i => i.Code is not "unsupported-joint-kind" and not "invalid-mate-record"
					&& !i.Message.StartsWith("Joint kind", StringComparison.Ordinal)).ToList();
				if (fatal.Count > 0) { Skip(string.Join(" ", fatal.Select(i => i.Message))); continue; }
			}
			validJoints.Add(joint);
		}

		// Fixed joints merge first; a hinge/slider inside one rigid group has no
		// motion left to express.
		foreach (var joint in validJoints.Where(j => j.Kind == "fixed"))
			Union(joint.OccurrenceA, joint.OccurrenceB);

		var groups = placed.GroupBy(o => Find(o.Id)).ToList();
		var groupOf = placed.ToDictionary(o => o.Id, o => Find(o.Id), StringComparer.Ordinal);
		var groupMembers = groups.ToDictionary(
			g => g.Key,
			g => g.OrderBy(o => occurrences.IndexOf(o)).ToList(),
			StringComparer.Ordinal);

		// Spanning forest over groups via hinge/slider joints, in document order.
		// An edge that would close a loop or give a group a second parent is
		// skipped with the exact reason instead of breaking the tree.
		var dsu = groups.ToDictionary(g => g.Key, g => g.Key, StringComparer.Ordinal);
		string DsuFind(string id)
		{
			while (dsu[id] != id) { dsu[id] = dsu[dsu[id]]; id = dsu[id]; }
			return id;
		}
		var pendingEdges = new List<AssemblyJoint>();
		foreach (var joint in validJoints.Where(j => j.Kind != "fixed"))
		{
			var ga = groupOf[joint.OccurrenceA];
			var gb = groupOf[joint.OccurrenceB];
			var aName = byId[joint.OccurrenceA].Name;
			var bName = byId[joint.OccurrenceB].Name;
			if (ga == gb)
			{
				plan.Skipped.Add(new SkippedJoint { JointId = joint.Id, Kind = joint.Kind, OccurrenceAName = aName, OccurrenceBName = bName,
					Reason = "Both ends are already one rigid group (fixed joints), so there is no motion left to express." });
				continue;
			}
			if (DsuFind(ga) == DsuFind(gb))
			{
				plan.Skipped.Add(new SkippedJoint { JointId = joint.Id, Kind = joint.Kind, OccurrenceAName = aName, OccurrenceBName = bName,
					Reason = "Closes a loop in the joint graph, or would give an already-parented group a second parent; a rig is a tree, so this joint was left out." });
				continue;
			}
			// Child = the group further from the chosen root; decided below by BFS.
			dsu[DsuFind(ga)] = DsuFind(gb);
			pendingEdges.Add(joint);
		}

		// Roots: the caller's grounded occurrence anchors its component; every
		// other component is rooted at its first occurrence in document order.
		// Lone occurrences (no joints at all) become standalone root bones.
		var components = new Dictionary<string, List<string>>(StringComparer.Ordinal); // dsu root -> group ids
		foreach (var g in groups)
		{
			var root = DsuFind(g.Key);
			if (!components.TryGetValue(root, out var list)) components[root] = list = new List<string>();
			list.Add(g.Key);
		}
		var edgesByGroup = new Dictionary<string, List<AssemblyJoint>>(StringComparer.Ordinal);
		foreach (var joint in pendingEdges)
		{
			var ga = groupOf[joint.OccurrenceA];
			var gb = groupOf[joint.OccurrenceB];
			if (!edgesByGroup.TryGetValue(ga, out var la)) edgesByGroup[ga] = la = new List<AssemblyJoint>();
			if (!edgesByGroup.TryGetValue(gb, out var lb)) edgesByGroup[gb] = lb = new List<AssemblyJoint>();
			la.Add(joint); lb.Add(joint);
		}

		var usedNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (var bone in studio.Rig.Bones) usedNames.Add(bone.Name);
		string UniqueBoneName(string wanted)
		{
			var clean = new string(wanted.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray()).Trim('_');
			if (clean.Length == 0) clean = "Part";
			if (char.IsDigit(clean[0])) clean = "Bone_" + clean;
			var name = clean;
			var n = 2;
			while (usedNames.Contains(name)) { name = clean + "_" + n; n++; }
			usedNames.Add(name);
			return name;
		}

		Vec3 GroupCentroid(string groupId)
		{
			var sum = Vec3.Zero;
			var members = groupMembers[groupId];
			foreach (var occ in members) sum += world[occ.Id].Origin;
			return sum * (1f / members.Count);
		}

		// The reported root is the caller's grounded pick when it names a real
		// occurrence, else the first occurrence (document order) that takes
		// part in any joint, else the first placed occurrence. NOTE: this
		// differs from Mobile only in using AssemblyGrounding (AS03) for the
		// grounded test; the priority order is identical.
		if (rootOccurrenceId != null && byId.TryGetValue(rootOccurrenceId, out var pickedOcc) && world.ContainsKey(pickedOcc.Id))
		{
			plan.RootOccurrenceId = pickedOcc.Id;
			plan.RootOccurrenceName = pickedOcc.Name;
		}
		else
		{
			var defaultRoot = placed.FirstOrDefault(IsGrounded)
				?? placed.FirstOrDefault(o => joints.Any(j => j.OccurrenceA == o.Id || j.OccurrenceB == o.Id))
				?? placed[0];
			plan.RootOccurrenceId = defaultRoot.Id;
			plan.RootOccurrenceName = defaultRoot.Name;
		}

		foreach (var component in components.Values.OrderBy(c => occurrences.IndexOf(groupMembers[c[0]][0])))
		{
			// Root group: the caller's pick if it is in this component, else a
			// grounded occurrence in it, else the first group in document order.
			string rootGroup;
			if (rootOccurrenceId != null && groupOf.TryGetValue(rootOccurrenceId, out var picked) && component.Contains(picked))
				rootGroup = picked;
			else
				rootGroup = component.OrderBy(g => occurrences.IndexOf(groupMembers[g][0]))
					.FirstOrDefault(g => groupMembers[g].Any(IsGrounded))
					?? component.OrderBy(g => occurrences.IndexOf(groupMembers[g][0])).First();

			// BFS from the root: parents are created before children, so the bone
			// list is already in AddBoneFromPoints order.
			var queue = new Queue<string>();
			var visited = new HashSet<string>(StringComparer.Ordinal);
			var parentGroup = new Dictionary<string, string>(StringComparer.Ordinal);
			var parentJoint = new Dictionary<string, AssemblyJoint>(StringComparer.Ordinal);
			queue.Enqueue(rootGroup);
			visited.Add(rootGroup);
			var bfsOrder = new List<string>();
			while (queue.Count > 0)
			{
				var group = queue.Dequeue();
				bfsOrder.Add(group);
				if (!edgesByGroup.TryGetValue(group, out var edges)) continue;
				foreach (var joint in edges)
				{
					var other = groupOf[joint.OccurrenceA] == group ? groupOf[joint.OccurrenceB] : groupOf[joint.OccurrenceA];
					if (visited.Contains(other)) continue;
					visited.Add(other);
					parentGroup[other] = group;
					parentJoint[other] = joint;
					queue.Enqueue(other);
				}
			}

			// Effective rest pose: a slider's stored travel moves its child group
			// rigidly from the saved occurrence transforms (the slider solve is
			// pure — it returns a transform, it does not write one back). Apply
			// each slider's solved position in BFS order, parents first, so
			// downstream pivots see the moved parts. Hinges need no pass: their
			// angle already lives in the occurrence transforms. `world` is
			// mutated in place; groups are disjoint, so components never clash.
			var sliderTravelByJoint = new Dictionary<string, float>(StringComparer.Ordinal);
			foreach (var group in bfsOrder)
			{
				if (!parentJoint.TryGetValue(group, out var slideJoint) || slideJoint.Kind != "slider") continue;
				var travel = SliderPosition(slideJoint);
				var clamped = travel;
				if (slideJoint.Minimum.HasValue) clamped = MathF.Max(clamped, slideJoint.Minimum.Value);
				if (slideJoint.Maximum.HasValue) clamped = MathF.Min(clamped, slideJoint.Maximum.Value);
				if (clamped != travel)
					plan.Warnings.Add($"Slider '{byId[slideJoint.OccurrenceA].Name} ↔ {byId[slideJoint.OccurrenceB].Name}' stored travel {travel:0.###} in is outside its limits; the rig uses the clamped {clamped:0.###} in.");
				sliderTravelByJoint[slideJoint.Id] = clamped;
				if (clamped == 0f) continue;
				var parentSideIsA = groupOf[slideJoint.OccurrenceA] == parentGroup[group];
				var parentOccId = parentSideIsA ? slideJoint.OccurrenceA : slideJoint.OccurrenceB;
				var parentFrame = parentSideIsA ? slideJoint.FrameA : slideJoint.FrameB;
				var childOccId = parentSideIsA ? slideJoint.OccurrenceB : slideJoint.OccurrenceA;
				var childFrame = parentSideIsA ? slideJoint.FrameB : slideJoint.FrameA;
				var frameWorldA = world[parentOccId] * parentFrame.ToXform();
				var axisLength = MathF.Sqrt(frameWorldA.Z.LengthSquared);
				var along = axisLength > 1e-8f ? clamped / axisLength : clamped;
				Xform solvedB;
				try
				{
					solvedB = frameWorldA * Xform.Translate(new Vec3(0f, 0f, along)) * childFrame.ToXform().Inverse;
				}
				catch (InvalidOperationException)
				{
					plan.Warnings.Add($"Slider '{byId[slideJoint.OccurrenceA].Name} ↔ {byId[slideJoint.OccurrenceB].Name}' connector frame cannot be inverted; its stored travel was ignored.");
					continue;
				}
				var delta = solvedB * world[childOccId].Inverse;
				foreach (var occ in groupMembers[group]) world[occ.Id] = delta * world[occ.Id];
			}

			var boneByGroup = new Dictionary<string, PlannedBone>(StringComparer.Ordinal);
			foreach (var group in bfsOrder)
			{
				var members = groupMembers[group];
				var centroid = GroupCentroid(group);
				var bone = new PlannedBone
				{
					OccurrenceIds = members.Select(o => o.Id).ToList(),
					OccurrenceNames = members.Select(o => o.Name).ToList(),
				};
				if (!parentJoint.TryGetValue(group, out var joint))
				{
					// Root (or standalone) bone: head at the group centroid, tail
					// toward the farthest occurrence, at least MinBoneLength long.
					var head = centroid;
					var dir = Vec3.Zero;
					var best = 0f;
					foreach (var occ in members)
					{
						var d = world[occ.Id].Origin - head;
						var lenSq = d.LengthSquared;
						if (lenSq > best) { best = lenSq; dir = d; }
					}
					if (best < 1e-8f) dir = new Vec3(0f, 0f, 1f);
					else dir *= 1f / MathF.Sqrt(best);
					var len = MathF.Max(MathF.Sqrt(best), MinBoneLength);
					bone.Name = UniqueBoneName(members[0].Name);
					bone.Kind = members.Count == 1 && !edgesByGroup.ContainsKey(group) ? "standalone" : "root";
					bone.Head = head;
					bone.Tail = head + dir * len;
				}
				else
				{
					// Child bone: head exactly on the joint pivot, tail at the
					// child group's centroid (the mass the bone points at).
					var parentSideIsA = groupOf[joint.OccurrenceA] == parentGroup[group];
					var parentOccId = parentSideIsA ? joint.OccurrenceA : joint.OccurrenceB;
					var parentFrame = parentSideIsA ? joint.FrameA : joint.FrameB;
					var frameWorld = world[parentOccId] * parentFrame.ToXform();
					var pivot = frameWorld.Origin;
					var axis = frameWorld.Z;
					var tail = centroid;
					if ((tail - pivot).LengthSquared < 1e-8f)
					{
						// The part's centre sits on its own pivot (common when an
						// occurrence origin is the joint point): point the bone
						// toward the next joint downstream instead, so it still
						// runs along the limb. Only a leaf with no downstream
						// joint points straight up.
						var fallback = new Vec3(0f, 0f, 1f);
						var fallbackLen = 1f;
						if (edgesByGroup.TryGetValue(group, out var myEdges))
						{
							var bestD = 0f;
							foreach (var ej in myEdges)
							{
								if (ej.Id == joint.Id) continue;
								var mySideIsA = groupOf[ej.OccurrenceA] == group;
								var myOcc = mySideIsA ? ej.OccurrenceA : ej.OccurrenceB;
								var myFrame = mySideIsA ? ej.FrameA : ej.FrameB;
								var p = (world[myOcc] * myFrame.ToXform()).Origin - pivot;
								if (p.LengthSquared > bestD) { bestD = p.LengthSquared; fallback = p * (1f / MathF.Sqrt(bestD)); fallbackLen = MathF.Sqrt(bestD); }
							}
						}
						if (fallbackLen <= 1f && (fallback - new Vec3(0f, 0f, 1f)).LengthSquared < 1e-8f)
							plan.Warnings.Add($"Bone '{members[0].Name}' has no extent past its joint pivot; it points straight up from the pivot.");
						tail = pivot + fallback * fallbackLen;
					}
					bone.Name = UniqueBoneName(members[0].Name);
					bone.Kind = joint.Kind;
					bone.JointId = joint.Id;
					bone.Head = pivot;
					bone.Tail = tail;
					bone.UpHint = axis;
					bone.HasUpHint = true;
					bone.ParentName = boneByGroup[parentGroup[group]].Name;
					plan.Joints.Add(new MappedJoint
					{
						JointId = joint.Id,
						Kind = joint.Kind,
						OccurrenceAName = byId[joint.OccurrenceA].Name,
						OccurrenceBName = byId[joint.OccurrenceB].Name,
						ChildBoneName = bone.Name,
						PivotWorld = pivot,
						AxisWorld = axis,
						Minimum = joint.Minimum,
						Maximum = joint.Maximum,
						CurrentAngle = joint.Kind == "hinge"
							? HingeAngle(world[joint.OccurrenceA] * joint.FrameA.ToXform(), world[joint.OccurrenceB] * joint.FrameB.ToXform())
							: null,
						CurrentTravel = joint.Kind == "slider" && sliderTravelByJoint.TryGetValue(joint.Id, out var travel)
							? travel
							: null,
					});
					if (joint.Kind == "hinge")
						plan.Warnings.Add($"Hinge '{byId[joint.OccurrenceA].Name} ↔ {byId[joint.OccurrenceB].Name}' limits are recorded, not enforced: the rig has no LimitRotation constraint, so posing past a limit is possible. Clamp with AssemblyToRigPlan.ClampHingeAngle.");
				}
				// Fixed-joint frame alignment is advisory: the group is rigid
				// either way, but a misaligned rest pose deserves a warning.
				boneByGroup[group] = bone;
				plan.Bones.Add(bone);
			}

			// Fixed-joint misalignment warnings for this component.
			foreach (var joint in validJoints.Where(j => j.Kind == "fixed"))
			{
				if (groupOf[joint.OccurrenceA] != groupOf[joint.OccurrenceB]) continue;
				if (!component.Contains(groupOf[joint.OccurrenceA])) continue;
				var fa = world[joint.OccurrenceA] * joint.FrameA.ToXform();
				var fb = world[joint.OccurrenceB] * joint.FrameB.ToXform();
				var axisCos = Vec3.Dot(fa.Z, fb.Z);
				var dist = (fa.Origin - fb.Origin).LengthSquared;
				if (axisCos < 0.9999f || dist > 1e-6f)
					plan.Warnings.Add($"Fixed joint '{byId[joint.OccurrenceA].Name} ↔ {byId[joint.OccurrenceB].Name}' frames are misaligned at rest; the parts still merge into one rigid bone and the rest pose is kept as saved.");
			}
		}

		// More than one grounded occurrence in a single joint tree cannot all
		// be the anchor: the rig has one root per tree. Say so; the grounded
		// state itself stays in the assembly, untouched.
		foreach (var component in components.Values)
		{
			var groundedHere = component.SelectMany(g => groupMembers[g]).Where(IsGrounded).ToList();
			if (groundedHere.Count > 1)
				plan.Warnings.Add($"{groundedHere.Count} occurrences in one joint tree are marked grounded ('{string.Join("', '", groundedHere.Select(o => o.Name))}'); the rig anchors on one root only ('{plan.RootOccurrenceName}'). Grounded state stays in the assembly; posing the rig can move the others.");
		}

		plan.Success = plan.Bones.Count > 0;
		if (!plan.Success)
			plan.Error = plan.Skipped.Count > 0
				? "Nothing in this assembly can convert: every joint was skipped (see the skipped list)."
				: "Assembly has joints but none of them can convert.";
		return plan;
	}

	/// <summary>
	/// Execute a plan on a studio. Appends bones to the existing skeleton (never
	/// clears it), pins each resolved body in BodyBoneMap, and derives rigid
	/// weights from the pins for verification. The studio passed here is the
	/// explicit destination: pass a fresh PartStudio (see
	/// <see cref="ConvertToNewStudio"/>) to keep the rig out of the assembly's
	/// own studio. The assembly is never written to. Throws
	/// InvalidOperationException on failure.
	/// </summary>
	public static AssemblyToRigResult Apply(PartStudio studio, AssemblyToRigPlan plan)
	{
		if (studio == null) throw new ArgumentNullException(nameof(studio));
		if (plan == null || !plan.Success) throw new InvalidOperationException(plan?.Error ?? "No convertible plan.");

		var result = new AssemblyToRigResult { Plan = plan, SourceAssemblyJson = plan.SourceAssemblyJson };
		var indexByBone = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var bone in plan.Bones)
		{
			var parentIndex = bone.ParentName == null ? -1 : indexByBone[bone.ParentName];
			int index;
			try
			{
				index = bone.HasUpHint
					? studio.Rig.AddBoneFromPoints(bone.Name, parentIndex, bone.Head, bone.Tail, bone.UpHint)
					: studio.Rig.AddBoneFromPoints(bone.Name, parentIndex, bone.Head, bone.Tail);
			}
			catch (Exception e)
			{
				throw new InvalidOperationException($"Could not create bone '{bone.Name}': {e.Message}", e);
			}
			indexByBone[bone.Name] = index;
			result.AppliedBoneNames.Add(bone.Name);
		}

		// Pin each occurrence's body to its bone. The fragment after '#' in the
		// definition's SourceReference is the studio body id by convention.
		var bodyById = studio.Bodies == null
			? new Dictionary<string, Body>(StringComparer.Ordinal)
			: studio.Bodies.Where(b => b != null).ToDictionary(b => b.Id, StringComparer.Ordinal);
		var definitions = new Dictionary<string, AssemblyPartDefinition>(StringComparer.Ordinal);
		// The plan does not carry definitions; resolve them from the source
		// assembly JSON so Apply never needs a second document handle.
		AssemblyDocument source = null;
		try { source = AssemblyDocument.FromJson(plan.SourceAssemblyJson); } catch { source = null; }
		if (source != null)
			foreach (var d in source.Definitions.Where(d => d != null)) definitions[d.Id] = d;

		foreach (var bone in plan.Bones)
		{
			foreach (var occId in bone.OccurrenceIds)
			{
				var occ = source?.Occurrences.FirstOrDefault(o => o != null && o.Id == occId);
				if (occ == null) continue;
				var bodyId = ResolveBodyId(occ, definitions);
				if (bodyId == null)
				{
					result.UnboundBodies[occId] = $"Occurrence '{occ.Name}' has no body id in its source reference, so it got a bone but no pin.";
					continue;
				}
				if (!bodyById.TryGetValue(bodyId, out var body))
				{
					result.UnboundBodies[bodyId] = $"Body '{bodyId}' for occurrence '{occ.Name}' is not in this studio, so it got a bone but no pin or weights.";
					continue;
				}
				studio.BodyBoneMap[body.Id] = bone.Name;
				result.BoundBodies[body.Id] = bone.Name;
			}
		}

		// Derive rigid weights from the pins (verification + the exact weights
		// Rig > Bind re-derives from BodyBoneMap). Unpinned vertices keep
		// whatever skin the mesh already had; nothing is force-bound by
		// nearest-bone, so weights can never bleed across close parts.
		try
		{
			var (mesh, ranges) = studio.ToMeshWithBodies();
			if (mesh != null && mesh.VertexCount > 0 && ranges != null && ranges.Count > 0)
			{
				var weights = mesh.Skin != null && mesh.Skin.Count == mesh.VertexCount
					? mesh.Skin
					: new SkinWeights(mesh.VertexCount);
				foreach (var range in ranges)
				{
					if (!studio.BodyBoneMap.TryGetValue(range.BodyId, out var boneName)) continue;
					var boneIndex = indexByBone.TryGetValue(boneName, out var fresh) ? fresh : studio.Rig.IndexOf(boneName);
					if (boneIndex < 0) continue;
					var bound = new[] { new BoneWeight(boneIndex, 1f) };
					for (var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++)
						weights[i] = bound;
				}
				result.Weights = weights;
				mesh.Skin = weights;
				result.WeightedVertexCount = ranges
					.Where(r => studio.BodyBoneMap.ContainsKey(r.BodyId))
					.Sum(r => r.Count);
			}
		}
		catch (Exception e)
		{
			// Weights are derived data, never a reason to fail the conversion.
			result.UnboundBodies["__weights__"] = $"Rigid weights could not be derived: {e.Message}";
		}

		result.Summary = Describe(plan);
		return result;
	}

	/// <summary>Plan and apply on the same studio: the caller's explicit destination.</summary>
	public static AssemblyToRigResult Convert(AssemblyDocument assembly, PartStudio studio, string rootOccurrenceId = null)
	{
		var plan = Plan(assembly, studio, rootOccurrenceId);
		if (!plan.Success) throw new InvalidOperationException(plan.Error ?? "No convertible plan.");
		return Apply(studio, plan);
	}

	/// <summary>
	/// Convert into an explicit NEW destination: a fresh PartStudio holding
	/// clones of the source studio's bodies (same ids, cloned meshes), the rig
	/// built on it, and a JSON round-trip copy of the assembly attached as its
	/// snapshot record. The source studio (Rig stays untouched) and the source
	/// assembly object are never written to. Throws InvalidOperationException
	/// when the plan fails.
	/// </summary>
	public static (PartStudio Destination, AssemblyToRigResult Result) ConvertToNewStudio(AssemblyDocument assembly, PartStudio sourceStudio, string rootOccurrenceId = null)
	{
		if (assembly == null) throw new ArgumentNullException(nameof(assembly));
		if (sourceStudio == null) throw new ArgumentNullException(nameof(sourceStudio));
		var destination = new PartStudio();
		foreach (var body in sourceStudio.Bodies)
			if (body != null) destination.Bodies.Add(body.Clone());
		var plan = Plan(assembly, destination, rootOccurrenceId);
		if (!plan.Success) throw new InvalidOperationException(plan.Error ?? "No convertible plan.");
		var result = Apply(destination, plan);
		destination.Assembly = AssemblyDocument.FromJson(assembly.ToJson());
		return (destination, result);
	}

	/// <summary>Body id from an occurrence's definition reference, or null.</summary>
	public static string ResolveBodyId(AssemblyOccurrence occurrence, IReadOnlyDictionary<string, AssemblyPartDefinition> definitions)
	{
		if (occurrence == null) return null;
		if (definitions != null && definitions.TryGetValue(occurrence.DefinitionId ?? "", out var def)
			&& !string.IsNullOrWhiteSpace(def.SourceReference))
		{
			var reference = def.SourceReference.Trim();
			var hash = reference.LastIndexOf('#');
			if (hash >= 0 && hash + 1 < reference.Length) return reference[(hash + 1)..].Trim();
			return reference.Length > 0 ? reference : null;
		}
		return null;
	}

	/// <summary>One human-readable summary of a plan, for the preview and Hub notes.</summary>
	public static string Describe(AssemblyToRigPlan plan)
	{
		if (plan == null) return "No conversion plan.";
		if (!plan.Success) return "Cannot convert: " + (plan.Error ?? "unknown reason.");
		var lines = new List<string>
		{
			$"{plan.Bones.Count} bone(s) from {plan.OccurrenceCount} occurrence(s), {plan.Joints.Count} joint(s) mapped, {plan.Skipped.Count} skipped.",
			"Root: " + (plan.RootOccurrenceName ?? "?") + ".",
		};
		foreach (var bone in plan.Bones)
			lines.Add($"Bone '{bone.Name}' ({bone.Kind})" + (bone.ParentName == null ? " [root]" : $" child of '{bone.ParentName}'") + $" — {string.Join(", ", bone.OccurrenceNames)}.");
		foreach (var joint in plan.Joints)
			lines.Add($"{joint.Kind} '{joint.OccurrenceAName} ↔ {joint.OccurrenceBName}' → bone '{joint.ChildBoneName}', {joint.CurrentText}, {joint.LimitText}.");
		foreach (var skipped in plan.Skipped)
			lines.Add($"Skipped {skipped.Kind} '{skipped.OccurrenceAName} ↔ {skipped.OccurrenceBName}': {skipped.Reason}");
		foreach (var warning in plan.Warnings)
			lines.Add("Note: " + warning);
		lines.Add(SnapshotNote);
		return string.Join("\n", lines);
	}
}
