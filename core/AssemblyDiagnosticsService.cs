using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;

// AS11: actionable constraint and reference diagnostics.
// Ports Mobile app/UiAssemblyDiagnostics.cs (the AssemblyDiagnostics static
// class; the UiAssemblyDiagnostics panel itself is NOT ported — there is no
// UI in this lane, the service returns structured diagnostics for the bot
// inspect/status surface). Pure document logic: Diagnose never mutates the
// document, repairs go through EditHistory as one undo step each, and a
// refused edit consumes no undo step. Not runtime verified: the final
// integrator runs tests/Desktop.Tests.csproj.
//
// Desktop adaptations, all deliberate:
// - Diagnostic kinds are the brief's 12-value enum (Mobile has 16 string
//   constants). Codes with no dedicated kind map as follows, and are still
//   reported, never swallowed: invalid-transform -> UnsupportedGeometry;
//   invalid-id / duplicate-id -> MissingReference; unsupported-joint-kind ->
//   UnderConstrained info (the joint adds no constraint); unsupported-schema
//   and unsupported units -> MissingReference. The default validation case
//   also maps to MissingReference, as in Mobile.
// - Diagnostic actions are the brief's 5-value enum (Mobile has 10).
//   Mobile's Inspect / EditJoint / SuppressJoint / UnsuppressJoint /
//   GroundOccurrence / UngroundOccurrence become None here; the Recovery
//   text still names the action to take.
// - DisplayName reuses AssemblyJointService.JointDisplayName for fixed
//   joints ("Fixed joint {index+1}", the Desktop-wide display name) and
//   "{Kind} joint {index+1}" for other kinds — NOT Mobile's
//   "{Kind} · {A} ↔ {B}" format. Pair-diagnostic titles AND details name
//   the parts, so the "names the parts" requirement still holds.
// - There is no CadDoc on Desktop: TryEdit snapshots doc.ToJson() and
//   restores it through AssemblyHistory.Restore, exactly like the other
//   assembly services. A rolled-back edit leaves the document byte-identical.
// - TryRenameJoint delegates to AssemblyJointService.RenameFixedJoint for
//   fixed joints (which also refuses duplicate names; Mobile does not check
//   duplicates) and renames hinge/slider joints the same way through
//   ExtensionData "name".
// - TrySuppress is flag-only, like Mobile: unsuppressing does NOT re-solve
//   the joint (unlike AS05 SetJointSuppressed, which moves parts).
// - UnderConstrained detail appends the free-DOF count from
//   AssemblyFixedJointSolver.FreeDegreesOfFreedom (brief requirement;
//   Mobile's detail carries no count).
// - SolveForDiagnostics returns a (Result, Diagnostics) tuple and runs the
//   solver once, feeding its verdicts into Diagnose. Its Diagnostics use
//   sourceExists = null; callers needing source checks call Diagnose with
//   their own sourceExists predicate.
// - The solver's unsupported-joint-kind warning is surfaced as
//   UnderConstrained info (deduped against the validation one), not
//   swallowed. Validation codes and ungrounded-component-root are skipped
//   in DiagnoseSolve because DiagnoseValidation / DiagnoseGrounding already
//   report them.

namespace Marionette.Desktop;

/// <summary>What is wrong, in the brief's 12 kinds. Matches Mobile's names.</summary>
public enum AssemblyDiagnosticKind
{
	MissingReference,
	MissingSource,
	Contradictory,
	RedundantJoint,
	UnsupportedGeometry,
	FloatingOccurrence,
	UnderConstrained,
	UnsupportedLoop,
	InvalidLimit,
	SuppressedJoint,
	NoGroundedRoot,
	PreservedLastValid,
}

public enum AssemblyDiagnosticSeverity
{
	Error,
	Warning,
	Info,
}

/// <summary>Repair the diagnostic row offers. Matches Mobile's action names.</summary>
public enum AssemblyDiagnosticAction
{
	None,
	DeleteJoint,
	DeleteOccurrence,
	Reselect,
	RepairLimits,
}

/// <summary>One assembly problem, with the people and places it touches.
/// Message is the short title (Mobile's Title); Detail carries the full
/// story. RecordId is the joint id for joint problems, the occurrence id
/// for occurrence problems, and "" for document-level problems.</summary>
public sealed record AssemblyDiagnostic(
	AssemblyDiagnosticKind Kind,
	AssemblyDiagnosticSeverity Severity,
	string RecordId,
	string Message,
	string Detail,
	AssemblyDiagnosticAction Action)
{
	/// <summary>Joint this belongs to, or "" for document / occurrence problems.</summary>
	public string JointId { get; init; } = "";
	public IReadOnlyList<string> OccurrenceIds { get; init; } = Array.Empty<string>();
	/// <summary>Human-readable names of the affected parts / joints, in order.</summary>
	public IReadOnlyList<string> AffectedNames { get; init; } = Array.Empty<string>();
	public string Recovery { get; init; } = "";
	public bool IsError => Severity == AssemblyDiagnosticSeverity.Error;
}

/// <summary>Outcome of one safe joint edit. A failed edit never leaves a
/// half-solved document behind: the checkpoint is rolled back and the
/// assembly still holds the last valid pose. Ok and Success are the same
/// value under two names (the brief's repair shape uses Ok, Mobile's
/// TryEdit shape uses Success).</summary>
public sealed class AssemblyEditResult
{
	public bool Ok { get; init; }
	public bool Success => Ok;
	/// <summary>True when a bad edit was undone and the last valid pose kept.</summary>
	public bool RolledBack { get; init; }
	public string Message { get; init; } = "";
	public IReadOnlyList<AssemblyDiagnostic> Diagnostics { get; init; } = Array.Empty<AssemblyDiagnostic>();
}

/// <summary>What deleting, suppressing or moving a joint would disturb,
/// shown for confirmation before anything changes.</summary>
public sealed class AssemblyEditImpact
{
	public string Summary { get; init; } = "";
	public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
	public IReadOnlyList<string> WouldDisconnectNames { get; init; } = Array.Empty<string>();
	public bool IsOnlyJoint { get; init; }
}

public static class AssemblyDiagnostics
{
	const float Epsilon = 1e-4f;

	static JsonElement BoolElement(bool value)
	{
		using var parsed = JsonDocument.Parse(value ? "true" : "false");
		return parsed.RootElement.Clone();
	}

	static JsonElement StringElement(string value)
	{
		using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(value ?? ""));
		return parsed.RootElement.Clone();
	}

	static bool ReadBool(Dictionary<string, JsonElement> data, string key)
		=> data != null && data.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.True;

	static string ReadString(Dictionary<string, JsonElement> data, string key)
		=> data != null && data.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String
			? element.GetString() ?? "" : "";

	static void Write(Dictionary<string, JsonElement> data, string key, JsonElement value)
	{
		if (data == null) return;
		data[key] = value;
	}

	// ---- grounded / suppressed / display name (ExtensionData, shared keys) ----

	/// <summary>True when the occurrence carries the shared "grounded" flag (AS03).</summary>
	public static bool IsGrounded(AssemblyOccurrence occurrence)
		=> AssemblyGrounding.IsGrounded(occurrence);

	/// <summary>Sets the shared "grounded" flag (AS03 rule: clearing removes
	/// the key and nulls ExtensionData when empty).</summary>
	public static void SetGrounded(AssemblyOccurrence occurrence, bool grounded)
		=> AssemblyJointService.SetOccurrenceGrounded(occurrence, grounded);

	public static bool IsSuppressed(AssemblyJoint joint)
		=> AssemblyJointService.IsJointSuppressed(joint);

	public static void SetSuppressed(AssemblyJoint joint, bool suppressed)
	{
		if (joint == null) return;
		joint.ExtensionData ??= new Dictionary<string, JsonElement>();
		if (suppressed) Write(joint.ExtensionData, AssemblyJointService.ExtSuppressed, BoolElement(true));
		else joint.ExtensionData.Remove(AssemblyJointService.ExtSuppressed);
	}

	public static void SetDisplayName(AssemblyJoint joint, string name)
	{
		if (joint == null) return;
		joint.ExtensionData ??= new Dictionary<string, JsonElement>();
		if (string.IsNullOrWhiteSpace(name)) joint.ExtensionData.Remove(AssemblyJointService.ExtName);
		else Write(joint.ExtensionData, AssemblyJointService.ExtName, StringElement(name.Trim()));
	}

	public static string OccurrenceName(AssemblyDocument doc, string occurrenceId)
	{
		var occurrence = doc?.Occurrences?.FirstOrDefault(o => o != null && o.Id == occurrenceId);
		if (occurrence == null) return string.IsNullOrWhiteSpace(occurrenceId) ? "(missing part)" : "(missing part " + ShortId(occurrenceId) + ")";
		return string.IsNullOrWhiteSpace(occurrence.Name) ? "(unnamed part " + ShortId(occurrence.Id) + ")" : occurrence.Name;
	}

	/// <summary>Joint name, or the Desktop display name: "Fixed joint N"
	/// for fixed joints (AssemblyJointService), "{Kind} joint N" otherwise.</summary>
	public static string DisplayName(AssemblyJoint joint, AssemblyDocument doc)
	{
		if (joint == null) return "(missing joint)";
		var custom = AssemblyJointService.GetJointName(joint);
		if (!string.IsNullOrWhiteSpace(custom)) return custom;
		if (joint.Kind == "fixed") return AssemblyJointService.JointDisplayName(doc, joint);
		var kind = string.IsNullOrWhiteSpace(joint.Kind) ? "joint" : joint.Kind;
		var index = doc == null ? 0 : Math.Max(0, doc.Joints.IndexOf(joint));
		return char.ToUpperInvariant(kind[0]) + kind.Substring(1) + " joint " + (index + 1);
	}

	static string ShortId(string id) => string.IsNullOrEmpty(id) ? "" : id.Length <= 8 ? id : id.Substring(0, 8);

	static bool IsSupportedKind(string kind) => kind is "fixed" or "hinge" or "slider";

	static bool OccurrenceExists(AssemblyDocument doc, string id)
		=> !string.IsNullOrWhiteSpace(id) && doc?.Occurrences?.Any(o => o != null && o.Id == id) == true;

	// Joints the solver can reason about: supported kind, both ends present,
	// not suppressed. Everything else is reported by the Validate pass below.
	static List<AssemblyJoint> ActiveJoints(AssemblyDocument doc)
		=> (doc?.Joints ?? new List<AssemblyJoint>())
			.Where(j => j != null && IsSupportedKind(j.Kind) && !IsSuppressed(j)
				&& OccurrenceExists(doc, j.OccurrenceA) && OccurrenceExists(doc, j.OccurrenceB))
			.ToList();

	static bool FramesEqual(AssemblyTransform a, AssemblyTransform b)
	{
		if (a == null || b == null) return a == b;
		return VectorsEqual(a.X, b.X) && VectorsEqual(a.Y, b.Y) && VectorsEqual(a.Z, b.Z) && VectorsEqual(a.Origin, b.Origin);
	}

	static bool VectorsEqual(float[] a, float[] b)
	{
		if (a == null || b == null || a.Length != 3 || b.Length != 3) return a == b;
		return MathF.Abs(a[0] - b[0]) <= Epsilon && MathF.Abs(a[1] - b[1]) <= Epsilon && MathF.Abs(a[2] - b[2]) <= Epsilon;
	}

	static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

	// Axis of a hinge/slider joint in the local space of `canonicalId`
	// (one of the pair's two occurrences). Hinge rotates about, and slider
	// slides along, the shared local +Z. Normalised: Mobile uses the raw
	// frame Z, which is unit length for any rigid frame; normalising only
	// changes the answer for frames validation already rejects.
	static float[] JointAxis(AssemblyJoint joint, string canonicalId)
	{
		var frame = joint.OccurrenceA == canonicalId ? joint.FrameA : joint.FrameB;
		var z = frame?.Z ?? new[] { 0f, 0f, 1f };
		var len = MathF.Sqrt(z[0] * z[0] + z[1] * z[1] + z[2] * z[2]);
		if (len < 1e-6f) return new[] { 0f, 0f, 1f };
		return new[] { z[0] / len, z[1] / len, z[2] / len };
	}

	// Frame of a joint in the local space of `canonicalId`.
	static AssemblyTransform JointFrame(AssemblyJoint joint, string canonicalId)
		=> joint.OccurrenceA == canonicalId ? joint.FrameA : joint.FrameB;

	// ---- diagnosis ----

	/// <summary>Every problem in the assembly, errors first, then warnings,
	/// then info, each in document order. Never calls an ungrounded or
	/// freely-moving assembly invalid: floating parts and free joints are
	/// warnings / info, not errors. A null document yields one error.
	/// Read-only: bad records are preserved and the last valid pose is kept.
	/// Pass the last successful solve as <paramref name="previousResult"/>
	/// so a failed solve can label the last valid pose instead of just failing.</summary>
	public static IReadOnlyList<AssemblyDiagnostic> Diagnose(AssemblyDocument doc, Func<AssemblyPartDefinition, bool> sourceExists = null, AssemblySolveResult previousResult = null)
		=> Diagnose(doc, sourceExists, previousResult, null);

	static IReadOnlyList<AssemblyDiagnostic> Diagnose(AssemblyDocument doc, Func<AssemblyPartDefinition, bool> sourceExists, AssemblySolveResult previousResult, AssemblySolveResult precomputedSolve)
	{
		var found = new List<AssemblyDiagnostic>();
		if (doc == null)
		{
			found.Add(new AssemblyDiagnostic(
				AssemblyDiagnosticKind.MissingReference, AssemblyDiagnosticSeverity.Error, "",
				"No assembly data to check",
				"The document has no assembly section at all, so there is nothing to diagnose. The part itself is untouched.",
				AssemblyDiagnosticAction.None)
			{ Recovery = "Add a part to the assembly, or reopen the document." });
			return found;
		}

		DiagnoseValidation(doc, sourceExists, found);
		if (doc.OpaqueFutureJson != null) return found;
		DiagnoseGraph(doc, found);
		DiagnoseGrounding(doc, found);
		DiagnoseSolve(doc, previousResult, found, precomputedSolve);

		var rank = new Dictionary<AssemblyDiagnosticSeverity, int>
		{
			[AssemblyDiagnosticSeverity.Error] = 0,
			[AssemblyDiagnosticSeverity.Warning] = 1,
			[AssemblyDiagnosticSeverity.Info] = 2,
		};
		return found
			.Select((d, i) => (d, i))
			.OrderBy(p => rank[p.d.Severity])
			.ThenBy(p => p.i)
			.Select(p => p.d)
			.ToList();
	}

	public static bool HasErrors(AssemblyDocument doc, Func<AssemblyPartDefinition, bool> sourceExists = null)
		=> Diagnose(doc, sourceExists).Any(d => d.IsError);

	public static int ErrorCount(AssemblyDocument doc, Func<AssemblyPartDefinition, bool> sourceExists = null)
		=> Diagnose(doc, sourceExists).Count(d => d.IsError);

	/// <summary>Runs the fixed solver once on the assembly (suppressed
	/// joints removed) and feeds its verdicts into Diagnose. The solve
	/// result is shared, never run twice.</summary>
	public static (AssemblySolveResult Result, IReadOnlyList<AssemblyDiagnostic> Diagnostics) SolveForDiagnostics(AssemblyDocument assembly, AssemblySolverOptions options = null)
	{
		if (assembly == null)
			return (null, Diagnose(null));
		var result = SolveOnce(assembly, options?.PreviousResult);
		var diagnostics = Diagnose(assembly, null, options?.PreviousResult, result);
		return (result, diagnostics);
	}

	/// <summary>Runs the fixed solver on the assembly with suppressed joints
	/// removed (a suppressed joint cannot over-constrain anything). Pure:
	/// never mutates the document. Callers keep the last successful result
	/// and pass it back as <paramref name="previousResult"/> so a failed solve
	/// can label the last valid pose.</summary>
	static AssemblySolveResult SolveOnce(AssemblyDocument doc, AssemblySolveResult previousResult = null)
	{
		if (doc == null) return null;
		try
		{
			var solveDoc = new AssemblyDocument
			{
				SchemaVersion = doc.SchemaVersion,
				LengthUnit = doc.LengthUnit,
				AngleUnit = doc.AngleUnit,
				CoordinateSystem = doc.CoordinateSystem,
				Definitions = doc.Definitions,
				Occurrences = doc.Occurrences,
				Joints = (doc.Joints ?? new List<AssemblyJoint>()).Where(j => j != null && !IsSuppressed(j)).ToList(),
			};
			return AssemblyFixedJointSolver.Solve(solveDoc, new AssemblySolverOptions { PreviousResult = previousResult });
		}
		catch (Exception)
		{
			// A solver failure never breaks diagnostics: it is caught and ignored.
			return null;
		}
	}

	// The kernel's own validation, re-worded as classified diagnostics with
	// a recovery path. Suppressed joints keep their document errors here:
	// suppression only excuses a joint from the solver, it never hides a
	// broken reference.
	static void DiagnoseValidation(AssemblyDocument doc, Func<AssemblyPartDefinition, bool> sourceExists, List<AssemblyDiagnostic> found)
	{
		Dictionary<string, AssemblyJoint> jointsById = new();
		Dictionary<string, AssemblyOccurrence> occurrencesById = new();
		foreach (var joint in doc.Joints ?? new List<AssemblyJoint>())
			if (joint != null && !string.IsNullOrWhiteSpace(joint.Id)) jointsById[joint.Id] = joint;
		foreach (var occurrence in doc.Occurrences ?? new List<AssemblyOccurrence>())
			if (occurrence != null && !string.IsNullOrWhiteSpace(occurrence.Id)) occurrencesById[occurrence.Id] = occurrence;

		string NameFor(string recordId)
		{
			if (jointsById.TryGetValue(recordId, out var joint)) return DisplayName(joint, doc);
			if (occurrencesById.TryGetValue(recordId, out var occurrence))
				return string.IsNullOrWhiteSpace(occurrence.Name) ? "(unnamed part)" : occurrence.Name;
			var definition = doc.Definitions?.FirstOrDefault(d => d != null && d.Id == recordId);
			if (definition != null) return string.IsNullOrWhiteSpace(definition.Name) ? "(unnamed part)" : definition.Name;
			return string.IsNullOrWhiteSpace(recordId) ? "(unnamed record)" : "(record " + ShortId(recordId) + ")";
		}

		foreach (var issue in doc.Validate(sourceExists))
		{
			var name = NameFor(issue.RecordId);
			var jointId = jointsById.ContainsKey(issue.RecordId) ? issue.RecordId : "";
			var occurrenceIds = jointsById.TryGetValue(issue.RecordId, out var j)
				? new[] { j.OccurrenceA, j.OccurrenceB }.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray()
				: occurrencesById.ContainsKey(issue.RecordId) ? new[] { issue.RecordId } : Array.Empty<string>();
			var severity = issue.Warning ? AssemblyDiagnosticSeverity.Warning : AssemblyDiagnosticSeverity.Error;

			AssemblyDiagnostic diag = issue.Code switch
			{
				"missing-source-reference" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.MissingSource, AssemblyDiagnosticSeverity.Error, issue.RecordId,
					"Source file missing for " + name,
					"The part definition points at '" + DetailSource(doc, issue.RecordId) + "', which cannot be found. The occurrence is kept in the document so you can relink it instead of losing the placement.",
					AssemblyDiagnosticAction.None)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Relink the part to its source file, or remove the occurrence." },
				"missing-source" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.MissingReference, AssemblyDiagnosticSeverity.Error, issue.RecordId,
					name + " has no source",
					"The part definition has no source kind or source reference, so the solver cannot load its geometry.",
					AssemblyDiagnosticAction.Reselect)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Pick the source again for this part." },
				"missing-definition" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.MissingReference, AssemblyDiagnosticSeverity.Error, issue.RecordId,
					name + " points at a missing part definition",
					"The placed occurrence refers to a part definition that is no longer in the document. Its transform is preserved so the placement can be restored.",
					AssemblyDiagnosticAction.None)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Restore the part definition, or remove the occurrence." },
				"missing-occurrence" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.MissingReference, AssemblyDiagnosticSeverity.Error, issue.RecordId,
					"Joint points at a part that no longer exists",
					"One end of '" + (jointsById.TryGetValue(issue.RecordId, out var dj) ? DisplayName(dj, doc) : name) + "' refers to an occurrence that is gone. The joint cannot solve until that end is reselected or the joint is deleted.",
					AssemblyDiagnosticAction.DeleteJoint)
				{ JointId = jointId, OccurrenceIds = occurrenceIds,
					AffectedNames = new[] { jointsById.TryGetValue(issue.RecordId, out var mj) ? DisplayName(mj, doc) : name },
					Recovery = "Reselect the missing end, or delete the joint." },
				"self-joint" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.MissingReference, AssemblyDiagnosticSeverity.Error, issue.RecordId,
					name + " joins a part to itself",
					"Both ends of the joint point at the same occurrence. A joint must connect two different parts.",
					AssemblyDiagnosticAction.Reselect)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Reselect one end onto a different part, or delete the joint." },
				"invalid-connector-frame" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.UnsupportedGeometry, AssemblyDiagnosticSeverity.Error, issue.RecordId,
					"Connector frame is not a valid rigid frame in " + name,
					"The connector's basis is not orthonormal and right-handed, or it holds a non-finite number. This usually means the face or edge it was picked from changed upstream and the stored frame no longer matches the geometry.",
					AssemblyDiagnosticAction.Reselect)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Reselect the connector on the changed geometry." },
				// No InvalidTransform kind in the brief's enum: a bad transform
				// is geometry the solver cannot use, so it is UnsupportedGeometry.
				"invalid-transform" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.UnsupportedGeometry, AssemblyDiagnosticSeverity.Error, issue.RecordId,
					name + " has an invalid transform",
					"The stored transform is singular or holds a non-finite number (" + issue.Message + "). The part is left where it was last valid; nothing was moved to compensate.",
					AssemblyDiagnosticAction.Reselect)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Re-place the part, or undo to the last good placement." },
				"invalid-joint-limit" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.InvalidLimit, AssemblyDiagnosticSeverity.Error, issue.RecordId,
					"Joint limits are invalid in " + name,
					issue.Message + " Limits are stored in " + (jointsById.TryGetValue(issue.RecordId, out var lj) && lj.Kind == "hinge" ? "radians." : "inches."),
					AssemblyDiagnosticAction.RepairLimits)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Fix the limits so the minimum does not exceed the maximum. If the two values were swapped, they can be swapped back." },
				// No InvalidId kind in the brief's enum: the record cannot be
				// trusted as a reference, so it is a MissingReference error.
				"invalid-id" or "duplicate-id" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.MissingReference, AssemblyDiagnosticSeverity.Error, issue.RecordId,
					"Unstable record ID: " + issue.Message,
					"Assembly records are matched by stable 32-character IDs. This record's ID is malformed or used twice, so edits could land on the wrong joint or part.",
					AssemblyDiagnosticAction.None)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Remove the duplicated record and recreate it." },
				// No UnsupportedJointKind kind in the brief's enum: an unknown
				// joint adds no constraint, so it is UnderConstrained info —
				// reported, never a false all-clear.
				"unsupported-joint-kind" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.UnderConstrained, AssemblyDiagnosticSeverity.Info, issue.RecordId,
					"Joint kind is not supported yet in " + name,
					issue.Message + " The joint is preserved in the document and ignored by the solver; its parts stay where they are.",
					AssemblyDiagnosticAction.None)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Replace it with a fixed, hinge or slider joint when that kind is supported." },
				"missing-name" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.MissingReference, severity, issue.RecordId,
					"A record has no name",
					"The record is valid but unnamed, so the tree and diagnostics can only show its ID. " + issue.Message,
					AssemblyDiagnosticAction.None)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Give the part or joint a name." },
				// No UnsupportedSchema kind in the brief's enum: the data cannot
				// be read as a reference, so it is a MissingReference error.
				"unsupported-schema" or "unsupported-length-unit" or "unsupported-angle-unit" or "unsupported-coordinate-system" => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.MissingReference, severity, issue.RecordId,
					"Assembly data uses an unsupported convention",
					issue.Message + " The data is preserved untouched; the solver refuses to guess at units or coordinates it does not understand.",
					AssemblyDiagnosticAction.None)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Open the document where the assembly was made, or remove the assembly data." },
				_ => new AssemblyDiagnostic(
					AssemblyDiagnosticKind.MissingReference, severity, issue.RecordId,
					issue.Message,
					"Validation reported '" + issue.Code + "' for this record. The document is unchanged.",
					AssemblyDiagnosticAction.None)
				{ JointId = jointId, OccurrenceIds = occurrenceIds, AffectedNames = new[] { name },
					Recovery = "Inspect the record and repair or remove it." },
			};
			found.Add(diag);
		}
	}

	static string DetailSource(AssemblyDocument doc, string definitionId)
	{
		var definition = doc.Definitions?.FirstOrDefault(d => d != null && d.Id == definitionId);
		return definition == null ? "(unknown)" : definition.SourceKind + " " + definition.SourceReference;
	}

	// Loops, contradictory pairs and free / floating parts, from the joint
	// graph alone. Only active joints take part: suppressed joints and
	// joints with broken references cannot over-constrain anything.
	static void DiagnoseGraph(AssemblyDocument doc, List<AssemblyDiagnostic> found)
	{
		var active = ActiveJoints(doc);
		DiagnoseLoops(doc, active, found);
		DiagnoseParallelPairs(doc, active, found);
		DiagnoseFreeOccurrences(doc, active, found);
		foreach (var joint in doc.Joints ?? new List<AssemblyJoint>())
		{
			if (joint == null || !IsSuppressed(joint)) continue;
			found.Add(new AssemblyDiagnostic(
				AssemblyDiagnosticKind.SuppressedJoint, AssemblyDiagnosticSeverity.Info, joint.Id,
				DisplayName(joint, doc) + " is suppressed",
				"A suppressed joint is excluded from loop and contradiction checks here and is not applied by the joint sheet. Its definition stays in the document, so unsuppressing brings it back exactly as it was.",
				AssemblyDiagnosticAction.None)
			{
				JointId = joint.Id,
				OccurrenceIds = new[] { joint.OccurrenceA, joint.OccurrenceB }.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray(),
				AffectedNames = new[] { DisplayName(joint, doc) },
				Recovery = "Unsuppress to use the joint again, or delete it if it is no longer needed.",
			});
		}
	}

	static void DiagnoseLoops(AssemblyDocument doc, List<AssemblyJoint> active, List<AssemblyDiagnostic> found)
	{
		// A joint is in a loop when its two ends stay connected without it.
		var inLoop = new HashSet<string>(StringComparer.Ordinal);
		foreach (var joint in active)
			if (HasAlternatePath(active, joint.OccurrenceA, joint.OccurrenceB, joint.Id))
				inLoop.Add(joint.Id);
		if (inLoop.Count == 0) return;

		// Group loop joints that touch each other into one loop each.
		var parent = inLoop.ToDictionary(id => id, id => id, StringComparer.Ordinal);
		string Find(string id)
		{
			while (parent[id] != id) { parent[id] = parent[parent[id]]; id = parent[id]; }
			return id;
		}
		void Union(string a, string b)
		{
			var ra = Find(a); var rb = Find(b);
			if (ra != rb) parent[ra] = rb;
		}
		var byOccurrence = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var joint in active.Where(j => inLoop.Contains(j.Id)))
			foreach (var end in new[] { joint.OccurrenceA, joint.OccurrenceB })
			{
				if (!byOccurrence.TryGetValue(end, out var list)) byOccurrence[end] = list = new List<string>();
				list.Add(joint.Id);
			}
		foreach (var list in byOccurrence.Values)
			for (var i = 1; i < list.Count; i++) Union(list[0], list[i]);

		foreach (var group in inLoop.GroupBy(Find).OrderBy(g => g.Key, StringComparer.Ordinal))
		{
			var joints = active.Where(j => group.Contains(j.Id)).OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
			var occurrenceIds = joints.SelectMany(j => new[] { j.OccurrenceA, j.OccurrenceB }).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
			var partNames = occurrenceIds.Select(id => OccurrenceName(doc, id)).ToList();
			var jointNames = joints.Select(j => DisplayName(j, doc)).ToList();
			found.Add(new AssemblyDiagnostic(
				AssemblyDiagnosticKind.UnsupportedLoop, AssemblyDiagnosticSeverity.Warning, joints[0].Id,
				"Joint loop: " + string.Join(" → ", partNames) + (partNames.Count > 0 ? " → " + partNames[0] : ""),
				"These joints close a loop (" + string.Join(", ", jointNames) + "). The solver does not verify loops yet, so a loop can hide a contradiction: the parts keep their last valid pose and the loop is reported instead of solved. Suppressing one joint in the loop breaks it; deleting one removes it.",
				AssemblyDiagnosticAction.None)
			{
				JointId = joints[0].Id,
				OccurrenceIds = occurrenceIds,
				AffectedNames = partNames,
				Recovery = "Suppress one joint in the loop to break it, or delete one if it is not needed.",
			});
		}
	}

	// A genuine loop needs an alternate route of at least two joints: two
	// joints between the same pair are a parallel constraint (handled by
	// DiagnoseParallelPairs), not a loop.
	static bool HasAlternatePath(List<AssemblyJoint> active, string from, string to, string excludedJointId)
	{
		var distance = new Dictionary<string, int>(StringComparer.Ordinal) { [from] = 0 };
		var queue = new Queue<string>();
		queue.Enqueue(from);
		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			foreach (var joint in active)
			{
				if (joint.Id == excludedJointId) continue;
				string next = null;
				if (joint.OccurrenceA == current) next = joint.OccurrenceB;
				else if (joint.OccurrenceB == current) next = joint.OccurrenceA;
				if (next == null) continue;
				if (next == to)
				{
					// Reaching the far end in two or more joints is a loop;
					// a single parallel joint is not.
					if (distance[current] + 1 >= 2) return true;
					continue;
				}
				if (distance.ContainsKey(next)) continue;
				distance[next] = distance[current] + 1;
				queue.Enqueue(next);
			}
		}
		return false;
	}

	static void DiagnoseParallelPairs(AssemblyDocument doc, List<AssemblyJoint> active, List<AssemblyDiagnostic> found)
	{
		var pairs = active
			.GroupBy(j => string.CompareOrdinal(j.OccurrenceA, j.OccurrenceB) < 0
				? (j.OccurrenceA, j.OccurrenceB) : (j.OccurrenceB, j.OccurrenceA))
			.Where(g => g.Count() > 1)
			.OrderBy(g => g.Key.Item1, StringComparer.Ordinal);
		foreach (var pair in pairs)
		{
			var joints = pair.OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
			var first = pair.Key.Item1; var second = pair.Key.Item2;
			var names = joints.Select(j => DisplayName(j, doc)).ToList();
			var pairName = OccurrenceName(doc, first) + " ↔ " + OccurrenceName(doc, second);
			var fixeds = joints.Where(j => j.Kind == "fixed").ToList();
			var hinges = joints.Where(j => j.Kind == "hinge").ToList();
			var sliders = joints.Where(j => j.Kind == "slider").ToList();

			if (fixeds.Count >= 2)
			{
				var same = fixeds.All(j => FramesEqual(JointFrame(j, first), JointFrame(fixeds[0], first))
					&& FramesEqual(JointFrame(j, second), JointFrame(fixeds[0], second)));
				if (same)
				{
					found.Add(new AssemblyDiagnostic(
						AssemblyDiagnosticKind.RedundantJoint, AssemblyDiagnosticSeverity.Warning, fixeds[1].Id,
						"Two fixed joints hold " + pairName + " the same way",
						"Both fixed joints demand the identical relative pose for " + pairName + ", so the second adds nothing. Keeping it is harmless, but it doubles the work on every edit and can hide which joint is really doing the holding.",
						AssemblyDiagnosticAction.None)
					{
						JointId = fixeds[1].Id,
						OccurrenceIds = new[] { first, second },
						AffectedNames = names,
						Recovery = "Suppress or delete the redundant joint.",
					});
				}
				else
				{
					found.Add(new AssemblyDiagnostic(
						AssemblyDiagnosticKind.Contradictory, AssemblyDiagnosticSeverity.Error, fixeds[1].Id,
						"Two fixed joints disagree on " + pairName,
						"'" + DisplayName(fixeds[0], doc) + "' and '" + DisplayName(fixeds[1], doc) + "' demand different relative poses for " + pairName + ". No single pose satisfies both, so the parts keep their last valid pose and neither joint is applied.",
						AssemblyDiagnosticAction.None)
					{
						JointId = fixeds[1].Id,
						OccurrenceIds = new[] { first, second },
						AffectedNames = names,
						Recovery = "Delete or suppress one of the two joints, or edit one frame to match the other.",
					});
				}
				continue;
			}
			if (fixeds.Count == 1 && (hinges.Count > 0 || sliders.Count > 0))
			{
				found.Add(new AssemblyDiagnostic(
					AssemblyDiagnosticKind.Contradictory, AssemblyDiagnosticSeverity.Error, joints[1].Id,
					"Fixed joint leaves no freedom for the other joint on " + pairName,
					"'" + DisplayName(fixeds[0], doc) + "' locks " + pairName + " together completely, so '" + DisplayName(hinges.Count > 0 ? hinges[0] : sliders[0], doc) + "' can never move. The moving joint is dead weight and its limits will never be reached.",
					AssemblyDiagnosticAction.None)
				{
					JointId = joints[1].Id,
					OccurrenceIds = new[] { first, second },
					AffectedNames = names,
					Recovery = "Delete or suppress the fixed joint to let the moving joint work, or delete the moving joint.",
				});
				continue;
			}
			if (hinges.Count >= 2)
			{
				AddAxisPairDiagnostic(doc, found, hinges, first, second, names, "hinge", "rotation axes", SameAxis(hinges, first, second));
			}
			if (sliders.Count >= 2)
			{
				AddAxisPairDiagnostic(doc, found, sliders, first, second, names, "slider", "slide axes", SameAxis(sliders, first, second));
			}
			// Hinge + slider on the same pair is a legitimate cylindrical
			// pairing (rotation about +Z, translation along +Z), so no
			// diagnostic: the two freedoms do not fight each other.
		}
	}

	// Two moving joints share one axis only when their axes agree in BOTH
	// occurrences' spaces: agreeing on one side while disagreeing on the
	// other means the joints demand different relative orientations, so a
	// one-sided check would call contradictory hinges redundant.
	static bool SameAxis(List<AssemblyJoint> joints, string first, string second)
	{
		foreach (var joint in joints)
		{
			if (MathF.Abs(MathF.Abs(Dot(JointAxis(joint, first), JointAxis(joints[0], first))) - 1f) > 1e-3f) return false;
			if (MathF.Abs(MathF.Abs(Dot(JointAxis(joint, second), JointAxis(joints[0], second))) - 1f) > 1e-3f) return false;
		}
		return true;
	}

	static void AddAxisPairDiagnostic(AssemblyDocument doc, List<AssemblyDiagnostic> found, List<AssemblyJoint> joints,
		string first, string second, List<string> names, string kindLabel, string axisLabel, bool parallel)
	{
		var pairName = OccurrenceName(doc, first) + " ↔ " + OccurrenceName(doc, second);
		if (parallel)
		{
			found.Add(new AssemblyDiagnostic(
				AssemblyDiagnosticKind.RedundantJoint, AssemblyDiagnosticSeverity.Warning, joints[1].Id,
				"Two " + kindLabel + " joints share one axis on " + pairName,
				"Both joints turn (or slide) " + pairName + " about the same axis, so the second adds nothing. The parts keep their pose; the duplicate only doubles edit work.",
				AssemblyDiagnosticAction.None)
			{
				JointId = joints[1].Id,
				OccurrenceIds = new[] { first, second },
				AffectedNames = names,
				Recovery = "Suppress or delete the redundant joint.",
			});
		}
		else
		{
			found.Add(new AssemblyDiagnostic(
				AssemblyDiagnosticKind.Contradictory, AssemblyDiagnosticSeverity.Error, joints[1].Id,
				"Two " + kindLabel + " joints fight over " + axisLabel + " on " + pairName,
				"'" + DisplayName(joints[0], doc) + "' and '" + DisplayName(joints[1], doc) + "' demand " + axisLabel + " that point in different directions on " + pairName + ". The parts cannot rotate (or slide) about two axes at once, so they keep their last valid pose.",
				AssemblyDiagnosticAction.None)
			{
				JointId = joints[1].Id,
				OccurrenceIds = new[] { first, second },
				AffectedNames = names,
				Recovery = "Delete or suppress one joint, or edit one axis to match the other.",
			});
		}
	}

	static void DiagnoseFreeOccurrences(AssemblyDocument doc, List<AssemblyJoint> active, List<AssemblyDiagnostic> found)
	{
		foreach (var occurrence in doc.Occurrences ?? new List<AssemblyOccurrence>())
		{
			if (occurrence == null) continue;
			var incident = active.Where(j => j.OccurrenceA == occurrence.Id || j.OccurrenceB == occurrence.Id).ToList();
			if (incident.Count == 0 && !IsGrounded(occurrence))
			{
				found.Add(new AssemblyDiagnostic(
					AssemblyDiagnosticKind.FloatingOccurrence, AssemblyDiagnosticSeverity.Warning, occurrence.Id,
					(string.IsNullOrWhiteSpace(occurrence.Name) ? "A part" : occurrence.Name) + " is not connected to anything",
					"This occurrence has no active joint and is not grounded, so nothing holds it in place: it can drift when the assembly is solved. That is not an error — a free part is fine while you are still placing it — but it will not move with the rest of the assembly.",
					AssemblyDiagnosticAction.None)
				{
					OccurrenceIds = new[] { occurrence.Id },
					AffectedNames = new[] { string.IsNullOrWhiteSpace(occurrence.Name) ? "(unnamed part)" : occurrence.Name },
					Recovery = "Ground the part to pin it down, or add a joint to connect it.",
				});
			}
			else if (incident.Count > 0 && incident.All(j => j.Kind is "hinge" or "slider"))
			{
				var dof = AssemblyFixedJointSolver.FreeDegreesOfFreedom(doc, occurrence.Id);
				found.Add(new AssemblyDiagnostic(
					AssemblyDiagnosticKind.UnderConstrained, AssemblyDiagnosticSeverity.Info, occurrence.Id,
					(string.IsNullOrWhiteSpace(occurrence.Name) ? "A part" : occurrence.Name) + " is free to move on its joint" + (incident.Count > 1 ? "s" : ""),
					"This part is held only by hinge or slider joints, so it still has freedom to rotate or slide. That is the joint working as designed, not a fault: add a fixed joint or a limit if the freedom is unwanted. It has " + dof + " free " + (dof == 1 ? "degree" : "degrees") + " of freedom.",
					AssemblyDiagnosticAction.None)
				{
					OccurrenceIds = new[] { occurrence.Id },
					AffectedNames = new[] { string.IsNullOrWhiteSpace(occurrence.Name) ? "(unnamed part)" : occurrence.Name, },
					Recovery = "No action needed unless the movement is unwanted.",
				});
			}
		}
	}

	static void DiagnoseGrounding(AssemblyDocument doc, List<AssemblyDiagnostic> found)
	{
		var active = ActiveJoints(doc);
		if (active.Count == 0) return;
		if ((doc.Occurrences ?? new List<AssemblyOccurrence>()).Any(IsGrounded)) return;
		var first = doc.Occurrences?.FirstOrDefault(o => o != null);
		if (first == null) return;
		found.Add(new AssemblyDiagnostic(
			AssemblyDiagnosticKind.NoGroundedRoot, AssemblyDiagnosticSeverity.Warning, first.Id,
			"No grounded part to anchor the assembly",
			"The assembly has joints but no grounded part, so the solver has to pick a root itself (currently " + (string.IsNullOrWhiteSpace(first.Name) ? "the first part" : first.Name) + "). That works, but the anchor can change as parts are added or removed, and the whole assembly can drift.",
			AssemblyDiagnosticAction.None)
		{
			OccurrenceIds = new[] { first.Id },
			AffectedNames = new[] { string.IsNullOrWhiteSpace(first.Name) ? "(unnamed part)" : first.Name },
			Recovery = "Ground the part that should never move.",
		});
	}

	// The fixed solver's verdicts, mapped onto the same kinds the graph
	// analysis uses. Validation codes are skipped here: DiagnoseValidation
	// already reported them. Ungrounded-component roots are skipped too:
	// DiagnoseGrounding already warns about the missing anchor. A solver
	// failure never breaks diagnostics: it is caught and ignored.
	static void DiagnoseSolve(AssemblyDocument doc, AssemblySolveResult previousResult, List<AssemblyDiagnostic> found, AssemblySolveResult precomputed)
	{
		var result = precomputed ?? SolveOnce(doc, previousResult);
		if (result?.Diagnostics == null) return;
		foreach (var d in result.Diagnostics)
		{
			switch (d.Code)
			{
				case "preserved-last-valid":
					found.Add(new AssemblyDiagnostic(
						AssemblyDiagnosticKind.PreservedLastValid, AssemblyDiagnosticSeverity.Info, "",
						"Showing the last valid pose",
						"The last solve failed, so the parts are shown where they were last valid instead of being moved to a partially solved pose. Nothing was changed.",
						AssemblyDiagnosticAction.None)
					{ Recovery = "Fix the joint that broke the solve, or undo the edit." });
					break;
				case "contradictory-fixed-joint":
					if (found.Any(f => f.Kind == AssemblyDiagnosticKind.Contradictory && f.JointId == d.RecordId)) break;
					var joint = doc.Joints?.FirstOrDefault(j => j != null && j.Id == d.RecordId);
					found.Add(new AssemblyDiagnostic(
						AssemblyDiagnosticKind.Contradictory, AssemblyDiagnosticSeverity.Error, d.RecordId,
						"Fixed joints disagree on " + (joint == null ? "these parts" : OccurrenceName(doc, joint.OccurrenceA) + " ↔ " + OccurrenceName(doc, joint.OccurrenceB)),
						"The solver found two fixed-joint paths that demand different poses for " + (joint == null ? "the same parts" : OccurrenceName(doc, joint.OccurrenceA) + " ↔ " + OccurrenceName(doc, joint.OccurrenceB)) + ", so no single pose satisfies them. The parts keep their last valid pose.",
						AssemblyDiagnosticAction.None)
					{
						JointId = d.RecordId,
						OccurrenceIds = joint == null ? Array.Empty<string>() : new[] { joint.OccurrenceA, joint.OccurrenceB }.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray(),
						AffectedNames = joint == null ? Array.Empty<string>() : new[] { DisplayName(joint, doc) },
						Recovery = "Delete or suppress one of the joints in the loop, or edit one frame to match the other.",
					});
					break;
				case "grounded-conflict":
					if (found.Any(f => f.Kind == AssemblyDiagnosticKind.Contradictory && f.OccurrenceIds.Contains(d.RecordId))) break;
					found.Add(new AssemblyDiagnostic(
						AssemblyDiagnosticKind.Contradictory, AssemblyDiagnosticSeverity.Error, d.RecordId,
						OccurrenceName(doc, d.RecordId) + " is grounded but the joints demand a different pose",
						"A grounded part must keep its stored transform, but the fixed joints demand a different pose for it. The parts keep their last valid pose.",
						AssemblyDiagnosticAction.None)
					{
						OccurrenceIds = new[] { d.RecordId },
						AffectedNames = new[] { OccurrenceName(doc, d.RecordId) },
						Recovery = "Unground the part, or edit the joint so it agrees with the grounded pose.",
					});
					break;
				case "unsupported-loop":
					if (found.Any(f => f.Kind == AssemblyDiagnosticKind.UnsupportedLoop && f.JointId == d.RecordId)) break;
					var loopJoint = doc.Joints?.FirstOrDefault(j => j != null && j.Id == d.RecordId);
					found.Add(new AssemblyDiagnostic(
						AssemblyDiagnosticKind.UnsupportedLoop, AssemblyDiagnosticSeverity.Warning, d.RecordId,
						(loopJoint == null ? "A moving joint" : DisplayName(loopJoint, doc)) + " is locked inside a rigidly fixed group",
						"This hinge or slider joins two parts that are already rigidly fixed together, so its freedom can never move. The fixed solver does not evaluate it.",
						AssemblyDiagnosticAction.None)
					{
						JointId = d.RecordId,
						OccurrenceIds = loopJoint == null ? Array.Empty<string>() : new[] { loopJoint.OccurrenceA, loopJoint.OccurrenceB }.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray(),
						AffectedNames = loopJoint == null ? Array.Empty<string>() : new[] { DisplayName(loopJoint, doc) },
						Recovery = "Delete or suppress the fixed joint to let the moving joint work, or delete the moving joint.",
					});
					break;
				case "unsupported-joint-kind":
					// Surfaced, not swallowed: an unknown joint kind is reported
					// as UnderConstrained info (it adds no constraint). Deduped
					// against the validation diagnostic for the same joint.
					if (found.Any(f => f.Kind == AssemblyDiagnosticKind.UnderConstrained && f.JointId == d.RecordId)) break;
					var unknownJoint = doc.Joints?.FirstOrDefault(j => j != null && j.Id == d.RecordId);
					found.Add(new AssemblyDiagnostic(
						AssemblyDiagnosticKind.UnderConstrained, AssemblyDiagnosticSeverity.Info, d.RecordId,
						"Joint kind is not supported yet in " + (unknownJoint == null ? "(unknown joint)" : DisplayName(unknownJoint, doc)),
						d.Message + " The joint is preserved in the document and ignored by the solver; its parts stay where they are.",
						AssemblyDiagnosticAction.None)
					{
						JointId = d.RecordId,
						OccurrenceIds = unknownJoint == null ? Array.Empty<string>() : new[] { unknownJoint.OccurrenceA, unknownJoint.OccurrenceB }.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray(),
						AffectedNames = unknownJoint == null ? Array.Empty<string>() : new[] { DisplayName(unknownJoint, doc) },
						Recovery = "Replace it with a fixed, hinge or slider joint when that kind is supported.",
					});
					break;
			}
		}
	}

	// ---- safe joint editing ----

	static AssemblyEditResult Fail(string message)
		=> new AssemblyEditResult { Ok = false, Message = message };

	static AssemblyEditResult Ok(string message, IReadOnlyList<AssemblyDiagnostic> diagnostics = null)
		=> new AssemblyEditResult { Ok = true, Message = message, Diagnostics = diagnostics ?? Array.Empty<AssemblyDiagnostic>() };

	/// <summary>Runs one joint edit as a single undo step. The mutation runs
	/// against the live document; if it introduces an error, or throws, the
	/// snapshot is rolled back and the assembly keeps its last valid pose.
	/// Never deletes a shared source: joint edits only touch joint records.
	/// Callers must re-resolve records by stable Id afterwards: rollback
	/// replaces the document's collections in place. A null history applies
	/// directly with no undo entry.</summary>
	public static AssemblyEditResult TryEdit(AssemblyDocument doc, EditHistory history, Action<AssemblyDocument> edit, string successMessage = "Joint edit applied.")
	{
		if (doc == null) return Fail("No assembly document is open.");
		if (edit == null) return Fail("Nothing to change.");
		var before = doc.ToJson();
		try
		{
			edit(doc);
		}
		catch (Exception e)
		{
			AssemblyHistory.Restore(doc, before);
			return new AssemblyEditResult
			{
				Ok = false, RolledBack = true,
				Message = "Edit failed (" + e.Message + ") — kept the last valid pose.",
			};
		}
		var diagnostics = Diagnose(doc);
		if (diagnostics.Any(d => d.IsError))
		{
			AssemblyHistory.Restore(doc, before);
			return new AssemblyEditResult
			{
				Ok = false, RolledBack = true, Diagnostics = diagnostics,
				Message = "Edit would break the assembly — kept the last valid pose.",
			};
		}
		var after = doc.ToJson();
		if (history != null)
			history.Execute(new DelegateEditCommand("Assembly edit",
				execute: () => AssemblyHistory.Restore(doc, after),
				undo: () => AssemblyHistory.Restore(doc, before),
				isNoOp: () => before == after));
		return new AssemblyEditResult { Ok = true, Diagnostics = diagnostics, Message = successMessage };
	}

	/// <summary>What deleting a joint would disturb. Shown for confirmation;
	/// nothing changes until the caller runs TryDeleteJoint.</summary>
	public static AssemblyEditImpact ImpactOfDeleteJoint(AssemblyDocument doc, string jointId)
	{
		var lines = new List<string>();
		var joint = doc?.Joints?.FirstOrDefault(j => j != null && j.Id == jointId);
		if (joint == null) return new AssemblyEditImpact { Summary = "That joint is already gone." };
		var active = ActiveJoints(doc);
		var disconnects = new List<string>();
		foreach (var end in new[] { joint.OccurrenceA, joint.OccurrenceB })
		{
			var others = active.Count(j => j.Id != jointId && (j.OccurrenceA == end || j.OccurrenceB == end));
			if (others == 0 && !IsGrounded(doc.Occurrences?.FirstOrDefault(o => o != null && o.Id == end)))
				disconnects.Add(OccurrenceName(doc, end));
		}
		var inLoop = HasAlternatePath(active, joint.OccurrenceA, joint.OccurrenceB, joint.Id);
		if (disconnects.Count > 0)
			lines.Add("Would leave " + string.Join(", ", disconnects) + " unconnected.");
		if (inLoop)
			lines.Add("Breaks a joint loop — the remaining joints stop being a loop.");
		if (active.Count == 1)
			lines.Add("It is the only active joint in the assembly.");
		lines.Add("The two parts stay where they are; only the joint is removed.");
		return new AssemblyEditImpact
		{
			Summary = lines.Count == 0 ? "Only the joint is removed." : string.Join(" ", lines),
			Lines = lines,
			WouldDisconnectNames = disconnects,
			IsOnlyJoint = active.Count == 1,
		};
	}

	/// <summary>Suppresses or unsuppresses a joint (flag only — unlike AS05
	/// SetJointSuppressed, unsuppressing does not re-solve or move parts).
	/// One undo step; an already-in-state joint costs no undo step.</summary>
	public static AssemblyEditResult TrySuppress(AssemblyDocument doc, EditHistory history, string jointId, bool suppress)
	{
		var joint = doc?.Joints?.FirstOrDefault(j => j != null && j.Id == jointId);
		if (joint == null) return Fail("That joint is already gone.");
		if (IsSuppressed(joint) == suppress)
			return Ok(suppress ? "Joint is already suppressed." : "Joint is already active.");
		return TryEdit(doc, history, assembly =>
		{
			var target = assembly.Joints.First(j => j.Id == jointId);
			SetSuppressed(target, suppress);
		}, suppress ? "Joint suppressed — the solver now ignores it." : "Joint active again.");
	}

	public static AssemblyEditResult TryDeleteJoint(AssemblyDocument doc, EditHistory history, string jointId)
	{
		var joint = doc?.Joints?.FirstOrDefault(j => j != null && j.Id == jointId);
		if (joint == null) return Fail("That joint is already gone.");
		return TryEdit(doc, history, assembly => { assembly.Joints.RemoveAll(j => j != null && j.Id == jointId); },
			"Joint deleted. The parts stay where they are.");
	}

	/// <summary>Deleting an occurrence refuses while any joint still points
	/// at it, so a part can never be pulled out from under its joints. The
	/// definition (shared source) is never touched.</summary>
	public static AssemblyEditResult TryDeleteOccurrence(AssemblyDocument doc, EditHistory history, string occurrenceId)
	{
		var occurrence = doc?.Occurrences?.FirstOrDefault(o => o != null && o.Id == occurrenceId);
		if (occurrence == null) return Fail("That part is already gone.");
		var users = doc.Joints?.Where(j => j != null && (j.OccurrenceA == occurrenceId || j.OccurrenceB == occurrenceId)).ToList() ?? new List<AssemblyJoint>();
		if (users.Count > 0)
		{
			return new AssemblyEditResult
			{
				Ok = false,
				Message = "Still used by " + users.Count + " joint" + (users.Count > 1 ? "s" : "") + " (" + string.Join(", ", users.Select(j => DisplayName(j, doc))) + ") — delete or suppress those joints first.",
			};
		}
		return TryEdit(doc, history, assembly => { assembly.Occurrences.RemoveAll(o => o != null && o.Id == occurrenceId); },
			"Part removed from the assembly. Its source is untouched.");
	}

	/// <summary>Joint order is solve order, so reordering is an edit like
	/// any other: one undo step, rolled back if it breaks the solve.</summary>
	public static AssemblyEditResult TryMoveJoint(AssemblyDocument doc, EditHistory history, string jointId, int newIndex)
	{
		var joints = doc?.Joints;
		var oldIndex = joints?.FindIndex(j => j != null && j.Id == jointId) ?? -1;
		if (oldIndex < 0) return Fail("That joint is already gone.");
		var clamped = Math.Clamp(newIndex, 0, joints.Count - 1);
		if (clamped == oldIndex) return Ok("Joint is already there.");
		return TryEdit(doc, history, assembly =>
		{
			var moved = assembly.Joints[oldIndex];
			assembly.Joints.RemoveAt(oldIndex);
			assembly.Joints.Insert(Math.Clamp(newIndex, 0, assembly.Joints.Count), moved);
		}, "Joint moved.");
	}

	public static AssemblyEditResult TrySetGrounded(AssemblyDocument doc, EditHistory history, string occurrenceId, bool grounded)
	{
		var occurrence = doc?.Occurrences?.FirstOrDefault(o => o != null && o.Id == occurrenceId);
		if (occurrence == null) return Fail("That part is already gone.");
		if (IsGrounded(occurrence) == grounded)
			return Ok(grounded ? "Part is already grounded." : "Part is already free.");
		return TryEdit(doc, history, assembly =>
		{
			var target = assembly.Occurrences.First(o => o.Id == occurrenceId);
			SetGrounded(target, grounded);
		}, grounded ? "Part grounded — it will not move." : "Part ungrounded.");
	}

	/// <summary>Renames a joint. Fixed joints go through
	/// AssemblyJointService.RenameFixedJoint (which also refuses duplicate
	/// names); hinge/slider joints are renamed the same way through
	/// ExtensionData "name". Blank names are refused with no undo step.</summary>
	public static AssemblyEditResult TryRenameJoint(AssemblyDocument doc, EditHistory history, string jointId, string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return Fail("A joint name cannot be empty.");
		var joint = doc?.Joints?.FirstOrDefault(j => j != null && j.Id == jointId);
		if (joint == null) return Fail("That joint is already gone.");
		if (joint.Kind == "fixed")
		{
			var renamed = AssemblyJointService.RenameFixedJoint(doc, history, jointId, name);
			return new AssemblyEditResult { Ok = renamed.Ok, Message = renamed.Message };
		}
		return TryEdit(doc, history, assembly =>
		{
			var target = assembly.Joints.First(j => j.Id == jointId);
			SetDisplayName(target, name);
		}, "Joint renamed.");
	}

	/// <summary>Limits are validated before any checkpoint is taken, so a
	/// refused value costs no undo step. Hinge limits are radians, slider
	/// limits inches — the same units the joint stores.</summary>
	public static AssemblyEditResult TrySetJointLimits(AssemblyDocument doc, EditHistory history, string jointId, float? minimum, float? maximum)
	{
		if ((minimum.HasValue && !float.IsFinite(minimum.Value)) || (maximum.HasValue && !float.IsFinite(maximum.Value)))
			return Fail("Limits must be finite numbers.");
		if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
			return Fail("Minimum cannot exceed maximum — swap them, or pick a wider range.");
		var joint = doc?.Joints?.FirstOrDefault(j => j != null && j.Id == jointId);
		if (joint == null) return Fail("That joint is already gone.");
		return TryEdit(doc, history, assembly =>
		{
			var target = assembly.Joints.First(j => j.Id == jointId);
			target.Minimum = minimum;
			target.Maximum = maximum;
		}, "Joint limits saved.");
	}

	/// <summary>True when a joint's limits are only wrong because the two
	/// values were swapped: the panel offers "Swap limits" as a one-tap fix.</summary>
	public static bool CanSwapLimits(AssemblyJoint joint)
		=> joint != null && joint.Minimum.HasValue && joint.Maximum.HasValue && joint.Minimum.Value > joint.Maximum.Value;

	public static AssemblyEditResult TrySwapJointLimits(AssemblyDocument doc, EditHistory history, string jointId)
	{
		var joint = doc?.Joints?.FirstOrDefault(j => j != null && j.Id == jointId);
		if (joint == null) return Fail("That joint is already gone.");
		if (!CanSwapLimits(joint))
			return Fail("Those limits are not simply swapped.");
		return TrySetJointLimits(doc, history, jointId, joint.Maximum, joint.Minimum);
	}
}
