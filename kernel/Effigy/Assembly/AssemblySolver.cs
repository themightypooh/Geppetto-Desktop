using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Effigy.Assembly;

/// <summary>
/// Deterministic fixed-joint solver for saved assembly data.
///
/// A fixed joint aligns the two local connector frames of its occurrences, in position AND
/// orientation: the world frame of connector A must equal the world frame of connector B, so
/// OccB = OccA * FrameA * FrameB^-1. Only "fixed" joints constrain the solve; hinge and slider
/// joints are deliberately NOT treated as rigid links (a later solver owns their degrees of
/// freedom), and unknown joint kinds are preserved by the document reader but ignored here.
///
/// The solver is a pure function of the stored document: it never mutates the
/// <see cref="AssemblyDocument"/>, its occurrences, or any shared part definition, and it never
/// feeds a solved transform back in as input, so repeated rebuilds cannot accumulate drift.
/// Solver state (the solved transforms) lives only in the returned
/// <see cref="AssemblySolveResult"/>; callers keep the last successful result and pass it back
/// as <see cref="AssemblySolverOptions.PreviousResult"/> so a failed solve preserves the last
/// valid state instead of returning partial garbage.
///
/// Root selection, per rigidly-connected component, is explicit and deterministic:
/// the first grounded occurrence (by stable ID order) anchors the component at its stored
/// transform; with no grounded occurrence the first occurrence by stable ID order is the
/// root and the choice is reported as an informational diagnostic. Grounded occurrences
/// that disagree with the fixed joints produce a grounded-conflict error.
///
/// Conventions: Effigy Z-up right-handed coordinates, inches, radians, exactly as
/// <see cref="AssemblyDocument"/> declares. Occurrence transforms may carry scale or a
/// mirror; the fixed offset is applied in the parent occurrence's frame, so a child inherits
/// the parent's scale/mirror. That is deterministic and documented, not a silent correction.
/// </summary>
public static class AssemblyFixedJointSolver
{
	public static AssemblySolveResult Solve(AssemblyDocument document, AssemblySolverOptions options = null)
	{
		if (document == null)
			throw new ArgumentNullException(nameof(document));
		options ??= new AssemblySolverOptions();
		var tolerance = options.Tolerance > 0f ? options.Tolerance : 1e-4f;
		var diagnostics = new List<AssemblySolveDiagnostic>();

		if (document.OpaqueFutureJson != null)
		{
			diagnostics.Add(new AssemblySolveDiagnostic("unsupported-schema", "", $"Assembly schema {document.SchemaVersion} is newer than this solver.", "error"));
			return Failure(document, options, diagnostics);
		}

		// Structural problems make the fixed graph undefined: fail, preserve last valid.
		// Missing source files and missing names do not change any transform, so they are
		// reported as warnings and do not block the solve; geometry resolution is the
		// loader's job, not the solver's.
		var hardCodes = new HashSet<string>(StringComparer.Ordinal)
		{
			"unsupported-schema", "missing-definitions", "missing-occurrences", "missing-joints",
			"null-definition", "null-occurrence", "null-joint",
			"invalid-id", "duplicate-id", "missing-definition", "missing-occurrence",
			"self-joint", "invalid-transform", "invalid-connector-frame",
		};
		foreach (var issue in document.Validate())
		{
			if (hardCodes.Contains(issue.Code))
				diagnostics.Add(new AssemblySolveDiagnostic(issue.Code, issue.RecordId, issue.Message, "error"));
			else if (issue.Code == "unsupported-joint-kind")
			{
				// Reported once below, with the fixed solver's wording, not twice.
			}
			else
				diagnostics.Add(new AssemblySolveDiagnostic(issue.Code, issue.RecordId, issue.Message, "warning"));
		}

		if (diagnostics.Any(d => d.Kind == "error"))
			return Failure(document, options, diagnostics);

		var occurrences = (document.Occurrences ?? new List<AssemblyOccurrence>())
			.Where(o => o != null)
			.ToList();
		var byId = occurrences.ToDictionary(o => o.Id, StringComparer.Ordinal);
		var stored = occurrences.ToDictionary(o => o.Id, o => o.Transform.ToXform(), StringComparer.Ordinal);
		var grounded = new HashSet<string>(
			occurrences.Where(IsGrounded).Select(o => o.Id), StringComparer.Ordinal);
		if (options.GroundedOccurrenceIds != null)
			foreach (var id in options.GroundedOccurrenceIds)
				if (!string.IsNullOrEmpty(id))
					grounded.Add(id);

		bool IsGrounded(AssemblyOccurrence occurrence)
		{
			// Assembly 06 owns the persisted grounded flag; until it lands, an explicit
			// "grounded": true extension entry on the occurrence is honoured as the same
			// signal, so solver callers can already ground parts without a schema change.
			return occurrence.ExtensionData != null
				&& occurrence.ExtensionData.TryGetValue("grounded", out var value)
				&& value.ValueKind == JsonValueKind.True;
		}

		var fixedJoints = (document.Joints ?? new List<AssemblyJoint>())
			.Where(j => j != null && j.Kind == "fixed")
			.OrderBy(j => j.Id, StringComparer.Ordinal)
			.ToList();
		var otherJoints = (document.Joints ?? new List<AssemblyJoint>())
			.Where(j => j != null && j.Kind != "fixed")
			.OrderBy(j => j.Id, StringComparer.Ordinal)
			.ToList();

		var adjacency = new Dictionary<string, List<FixedLink>>(StringComparer.Ordinal);
		foreach (var occurrence in occurrences)
			adjacency[occurrence.Id] = new List<FixedLink>();
		foreach (var joint in fixedJoints)
		{
			adjacency[joint.OccurrenceA].Add(new FixedLink(joint, joint.OccurrenceB, CurrentIsA: true));
			adjacency[joint.OccurrenceB].Add(new FixedLink(joint, joint.OccurrenceA, CurrentIsA: false));
		}
		foreach (var links in adjacency.Values)
			links.Sort((a, b) => string.CompareOrdinal(a.NeighborId, b.NeighborId));

		var solved = new Dictionary<string, Xform>(StringComparer.Ordinal);
		var components = new List<AssemblySolvedComponent>();
		var errors = new List<AssemblySolveDiagnostic>();

		foreach (var startId in occurrences.Select(o => o.Id).OrderBy(id => id, StringComparer.Ordinal))
		{
			if (solved.ContainsKey(startId))
				continue;

			// The component is the fixed-graph closure of the seed; collect it first so the
			// root is the component's grounded (or first) occurrence, not just the seed's.
			var closure = new HashSet<string>(StringComparer.Ordinal);
			var walk = new Queue<string>();
			walk.Enqueue(startId);
			closure.Add(startId);
			while (walk.Count > 0)
			{
				var current = walk.Dequeue();
				foreach (var link in adjacency[current])
					if (closure.Add(link.NeighborId))
						walk.Enqueue(link.NeighborId);
			}
			// One rigidly-connected component: breadth-first from its root, in stable order.
			var componentIds = new List<string>();
			var queue = new Queue<string>();
			var groundedInComponent = closure.Where(id => grounded.Contains(id))
				.OrderBy(id => id, StringComparer.Ordinal)
				.ToList();
			string rootId;
			bool rootIsGrounded;
			if (groundedInComponent.Count > 0)
			{
				rootId = groundedInComponent[0];
				rootIsGrounded = true;
			}
			else
			{
				rootId = closure.OrderBy(id => id, StringComparer.Ordinal).First();
				rootIsGrounded = false;
				diagnostics.Add(new AssemblySolveDiagnostic("ungrounded-component-root", rootId,
					$"No grounded occurrence in this component; '{byId[rootId].Name}' is the explicit root and keeps its stored transform.", "info"));
			}

			solved[rootId] = stored[rootId];
			queue.Enqueue(rootId);
			componentIds.Add(rootId);
			while (queue.Count > 0)
			{
				var currentId = queue.Dequeue();
				var current = solved[currentId];
				foreach (var link in adjacency[currentId])
				{
					var joint = link.Joint;
					var frameCurrent = (link.CurrentIsA ? joint.FrameA : joint.FrameB).ToXform();
					var frameNeighbor = (link.CurrentIsA ? joint.FrameB : joint.FrameA).ToXform();
					var predicted = current * frameCurrent * frameNeighbor.Inverse;
					if (!solved.TryGetValue(link.NeighborId, out var assigned))
					{
						solved[link.NeighborId] = predicted;
						componentIds.Add(link.NeighborId);
						queue.Enqueue(link.NeighborId);
					}
					else if (!TransformsClose(predicted, assigned, tolerance))
					{
						errors.Add(new AssemblySolveDiagnostic("contradictory-fixed-joint", joint.Id,
							$"Fixed joint '{joint.Id}' between '{currentId}' and '{link.NeighborId}' contradicts the transform already solved for '{link.NeighborId}' through another path; the loop is over-constrained.", "error"));
					}
				}
			}

			foreach (var id in componentIds)
			{
				if (!grounded.Contains(id))
					continue;
				if (!TransformsClose(solved[id], stored[id], tolerance))
					errors.Add(new AssemblySolveDiagnostic("grounded-conflict", id,
						$"Grounded occurrence '{byId[id].Name}' cannot keep its stored transform: the fixed joints demand a different pose.", "error"));
			}

			componentIds.Sort(StringComparer.Ordinal);
			components.Add(new AssemblySolvedComponent
			{
				RootOccurrenceId = rootId,
				RootIsGrounded = rootIsGrounded,
				OccurrenceIds = componentIds,
			});
		}

		// A hinge/slider joint inside one rigid component is not evaluated by this solver:
		// it permits motion the fixed graph cannot represent. Warn, do not silently treat
		// it as rigid and do not fail the fixed solve around it.
		var componentOf = new Dictionary<string, int>(StringComparer.Ordinal);
		for (var i = 0; i < components.Count; i++)
			foreach (var id in components[i].OccurrenceIds)
				componentOf[id] = i;
		foreach (var joint in otherJoints)
		{
			if (joint.Kind is not ("hinge" or "slider"))
			{
				diagnostics.Add(new AssemblySolveDiagnostic("unsupported-joint-kind", joint.Id,
					$"Joint kind '{joint.Kind}' is preserved but not supported by the fixed solver.", "warning"));
				continue;
			}
			if (componentOf.TryGetValue(joint.OccurrenceA, out var a)
				&& componentOf.TryGetValue(joint.OccurrenceB, out var b) && a == b)
				diagnostics.Add(new AssemblySolveDiagnostic("unsupported-loop", joint.Id,
					$"{joint.Kind} joint '{joint.Id}' joins two occurrences already rigidly fixed together; its degree of freedom is not evaluated by the fixed solver.", "warning"));
		}

		if (errors.Count > 0)
		{
			diagnostics.AddRange(errors);
			return Failure(document, options, diagnostics);
		}

		components.Sort((x, y) => string.CompareOrdinal(x.RootOccurrenceId, y.RootOccurrenceId));
		return new AssemblySolveResult
		{
			Success = true,
			PreservedLastValid = false,
			SolvedTransforms = solved,
			Components = components,
			Diagnostics = diagnostics,
		};
	}

	/// <summary>
	/// Free degrees of freedom of one occurrence relative to ground: the cheapest path to a
	/// grounded occurrence, where a fixed joint costs 0, a hinge costs 1 (rotation about its
	/// shared +Z) and a slider costs 1 (translation along it). A grounded occurrence itself
	/// has 0; an occurrence with no path to ground has 6 (under-constrained, not invalid).
	/// Unknown joint kinds cost 1: the solver cannot prove they constrain anything, so it
	/// never claims false rigidity. Costs are minimised over ALL paths (0-1 BFS), not just
	/// the first path a plain breadth-first walk happens to dequeue — a parallel fixed path
	/// to ground constrains the occurrence even when a hinge path is shorter in hops.
	/// Grounding is the same signal Solve honours: the caller's grounded IDs plus an
	/// occurrence's own "grounded": true extension entry.
	/// </summary>
	public static int FreeDegreesOfFreedom(AssemblyDocument document, string occurrenceId, IReadOnlyCollection<string> groundedOccurrenceIds = null)
	{
		if (document == null)
			throw new ArgumentNullException(nameof(document));
		if (string.IsNullOrEmpty(occurrenceId))
			return 6;

		var grounded = new HashSet<string>(StringComparer.Ordinal);
		if (groundedOccurrenceIds != null)
			foreach (var id in groundedOccurrenceIds)
				if (!string.IsNullOrEmpty(id))
					grounded.Add(id);
		foreach (var occurrence in document.Occurrences ?? new List<AssemblyOccurrence>())
			if (occurrence?.ExtensionData != null
				&& occurrence.ExtensionData.TryGetValue("grounded", out var value)
				&& value.ValueKind == JsonValueKind.True
				&& !string.IsNullOrEmpty(occurrence.Id))
				grounded.Add(occurrence.Id);

		if (grounded.Contains(occurrenceId))
			return 0;

		var jointsByOcc = new Dictionary<string, List<AssemblyJoint>>(StringComparer.Ordinal);
		foreach (var joint in (document.Joints ?? new List<AssemblyJoint>()).Where(j => j != null))
		{
			if (!jointsByOcc.TryGetValue(joint.OccurrenceA, out var la)) jointsByOcc[joint.OccurrenceA] = la = new();
			if (!jointsByOcc.TryGetValue(joint.OccurrenceB, out var lb)) jointsByOcc[joint.OccurrenceB] = lb = new();
			la.Add(joint);
			lb.Add(joint);
		}
		foreach (var joints in jointsByOcc.Values)
			joints.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

		// 0-1 BFS: fixed edges cost 0 and jump the front, hinge/slider/unknown cost 1.
		var best = new Dictionary<string, int>(StringComparer.Ordinal) { [occurrenceId] = 0 };
		var deque = new LinkedList<string>();
		deque.AddFirst(occurrenceId);
		while (deque.Count > 0)
		{
			var id = deque.First.Value;
			deque.RemoveFirst();
			var cost = best[id];
			if (!jointsByOcc.TryGetValue(id, out var joints))
				continue;
			foreach (var joint in joints)
			{
				var other = joint.OccurrenceA == id ? joint.OccurrenceB : joint.OccurrenceA;
				var step = joint.Kind == "fixed" ? 0 : 1;
				var next = cost + step;
				if (best.TryGetValue(other, out var previous) && previous <= next)
					continue;
				best[other] = next;
				if (step == 0)
					deque.AddFirst(other);
				else
					deque.AddLast(other);
			}
		}

		var free = 6;
		foreach (var id in grounded)
			if (best.TryGetValue(id, out var cost))
				free = Math.Min(free, cost);
		return free;
	}

	static AssemblySolveResult Failure(AssemblyDocument document, AssemblySolverOptions options, List<AssemblySolveDiagnostic> diagnostics)
	{
		var previous = options.PreviousResult;
		var preserved = previous != null && previous.Success && previous.SolvedTransforms != null;
		if (preserved)
			diagnostics.Add(new AssemblySolveDiagnostic("preserved-last-valid", "",
				"Solve failed; the last valid solved state is preserved unchanged.", "info"));
		return new AssemblySolveResult
		{
			Success = false,
			PreservedLastValid = preserved,
			SolvedTransforms = preserved
				? new Dictionary<string, Xform>(previous.SolvedTransforms, StringComparer.Ordinal)
				: new Dictionary<string, Xform>(StringComparer.Ordinal),
			Components = preserved ? previous.Components : new List<AssemblySolvedComponent>(),
			Diagnostics = diagnostics,
		};
	}

	static bool TransformsClose(Xform a, Xform b, float tolerance)
	{
		return MathF.Abs(a.X.x - b.X.x) <= tolerance && MathF.Abs(a.X.y - b.X.y) <= tolerance && MathF.Abs(a.X.z - b.X.z) <= tolerance
			&& MathF.Abs(a.Y.x - b.Y.x) <= tolerance && MathF.Abs(a.Y.y - b.Y.y) <= tolerance && MathF.Abs(a.Y.z - b.Y.z) <= tolerance
			&& MathF.Abs(a.Z.x - b.Z.x) <= tolerance && MathF.Abs(a.Z.y - b.Z.y) <= tolerance && MathF.Abs(a.Z.z - b.Z.z) <= tolerance
			&& MathF.Abs(a.Origin.x - b.Origin.x) <= tolerance && MathF.Abs(a.Origin.y - b.Origin.y) <= tolerance && MathF.Abs(a.Origin.z - b.Origin.z) <= tolerance;
	}

	readonly record struct FixedLink(AssemblyJoint Joint, string NeighborId, bool CurrentIsA);
}

public sealed class AssemblySolverOptions
{
	/// <summary>Occurrence IDs the caller treats as grounded (anchored at stored transform).</summary>
	public IReadOnlyCollection<string> GroundedOccurrenceIds { get; set; }
	/// <summary>Last successful result, returned unchanged when a later solve fails.</summary>
	public AssemblySolveResult PreviousResult { get; set; }
	/// <summary>Max per-component difference (basis and origin) before two paths are contradictory. Default 1e-4.</summary>
	public float Tolerance { get; set; } = 1e-4f;
}

public sealed class AssemblySolveResult
{
	public bool Success { get; set; }
	public bool PreservedLastValid { get; set; }
	public IReadOnlyDictionary<string, Xform> SolvedTransforms { get; set; } = new Dictionary<string, Xform>();
	public IReadOnlyList<AssemblySolvedComponent> Components { get; set; } = new List<AssemblySolvedComponent>();
	public IReadOnlyList<AssemblySolveDiagnostic> Diagnostics { get; set; } = new List<AssemblySolveDiagnostic>();

	public bool TryGetSolvedTransform(string occurrenceId, out Xform transform)
	{
		transform = default;
		return SolvedTransforms != null && SolvedTransforms.TryGetValue(occurrenceId, out transform);
	}
}

public sealed class AssemblySolvedComponent
{
	public string RootOccurrenceId { get; set; } = "";
	public bool RootIsGrounded { get; set; }
	public IReadOnlyList<string> OccurrenceIds { get; set; } = new List<string>();
}

/// <summary>Kind is "error", "warning" or "info". Errors fail the solve; the rest do not.</summary>
public sealed record AssemblySolveDiagnostic(string Code, string RecordId, string Message, string Kind);

/// <summary>
/// Legacy centroid-mate compatibility. This is the exact translation
/// <see cref="AssemblyMateFeature"/> applies on rebuild, as a pure function of the two body
/// centroids and the mate parameters — kept here so the fixed-joint solver and its checks can
/// prove the old behaviour is unchanged without running the feature tree.
///
/// A legacy mate moves the second body by translation ONLY: it never rotates anything, and
/// "Fixed centers" captures the offset at authoring time rather than imposing a live rigid
/// constraint. It is NOT a fixed joint and the fixed solver never reads mate records; an
/// old document keeps the centroid behaviour it always had.
/// </summary>
public static class AssemblyLegacyMate
{
	public static string KindName(int kindIndex) => kindIndex switch
	{
		1 => "Coincident centers",
		2 => "Center distance",
		_ => "Fixed centers",
	};

	/// <summary>Offset the mate applies between the reference centroid and the moving centroid.</summary>
	public static Vec3 MateOffset(int kindIndex, Vec3 offset, int axisIndex, float distance) => kindIndex switch
	{
		1 => Vec3.Zero,
		2 => (axisIndex switch { 1 => new Vec3(0, 1, 0), 2 => new Vec3(0, 0, 1), _ => new Vec3(1, 0, 0) }) * distance,
		_ => offset,
	};

	/// <summary>Where the moving centroid must end up: reference centroid plus the mate offset.</summary>
	public static Vec3 MateTargetCentroid(Vec3 referenceCentroid, int kindIndex, Vec3 offset, int axisIndex, float distance) =>
		referenceCentroid + MateOffset(kindIndex, offset, axisIndex, distance);

	/// <summary>Translation that moves the moving centroid onto its mate target. Mirrors AssemblyMateFeature.Execute.</summary>
	public static Vec3 MateTranslation(Vec3 referenceCentroid, Vec3 movingCentroid, int kindIndex, Vec3 offset, int axisIndex, float distance) =>
		MateTargetCentroid(referenceCentroid, kindIndex, offset, axisIndex, distance) - movingCentroid;
}
