using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;

// AS05: transactional fixed joints and grounded roots.
// Ports Mobile app/AssemblyJointCommands.cs (Assembly 09) in Desktop form.
// Pure document logic: every method either computes a disposable preview or
// commits exactly one persistent change through AssemblyHistory, so undo/redo
// and save/reopen work the same way they do for the rest of the document.
// No CadDoc on Desktop: operations take (AssemblyDocument, EditHistory) and a
// null history applies directly. There is no rebuild concept on Desktop — the
// occurrence-transform write IS the effect. Not runtime verified: the final
// integrator runs tests/Desktop.Tests.csproj.
//
// Conventions (Effigy Z-up, inches, radians — see AssemblyDocument):
// - A fixed joint aligns connector frame B onto connector frame A. Connector
//   frames live in each occurrence's LOCAL coordinates; the occurrence transform
//   maps local -> assembly space, so the world frame of a connector is
//   occurrence.Transform * connector.Frame (Xform multiplication applies the
//   right-hand side first).
// - Solved relation:  TA * FA * Translate(offset) == TB * FB'
//   where FB' is FB flipped 180 degrees about its own local X axis when the
//   joint is flipped. The grounded (root) occurrence keeps its transform; the
//   other one is solved:  TB = TA * FA * Translate(offset) * Inverse(FB').
//   Offset is in inches, expressed in frame-A local axes.
// - AssemblyJoint (AS01) has no Name / Suppressed / Offset / Flipped fields,
//   so those live in ExtensionData under the keys below. "grounded" lives on
//   the occurrence via AssemblyGrounding (AS03). The values round-trip through
//   AssemblyDocument.ToJson/FromJson and unknown keys are preserved, never dropped.
// - The legacy centroid mate is untouched: a fixed joint never rewrites a mate,
//   and a mate is never reinterpreted as a fixed joint.

namespace Marionette.Desktop;

/// <summary>A connector picked by the user (AS07 supplies these; an explicit
/// origin/axis fallback is a connector too, with SourceKind "origin" and an empty
/// SourceReference). The frame is in the occurrence's local coordinates.</summary>
public sealed record AssemblyConnectorRef(
	string OccurrenceId,
	AssemblyTransform Frame,
	string SourceKind,
	string SourceLabel,
	string SourceReference)
{
	public string DisplayLabel =>
		string.IsNullOrWhiteSpace(SourceLabel) ? SourceKind : SourceLabel;
}

/// <summary>Everything the fixed-joint sheet edits. A draft is disposable: building
/// or changing one never touches the document until Apply (Create/Update).</summary>
public sealed class AssemblyJointDraft
{
	public string JointId = "";
	public AssemblyConnectorRef ConnectorA;
	public AssemblyConnectorRef ConnectorB;
	public string Name = "";
	public Vec3 Offset = Vec3.Zero;
	public bool Flipped;
	public bool Suppressed;
	/// <summary>Occurrence id that stays put. "" means "decide": the grounded side,
	/// else A. The sheet shows the resolved root before Apply.</summary>
	public string RootOccurrenceId = "";

	/// <summary>Occurrence transforms as they were when the sheet opened, so Cancel
	/// can prove nothing moved. Previews never mutate the document, so restoring
	/// is a verification, not a repair.</summary>
	public Dictionary<string, Xform> PriorTransforms = new();

	public void CapturePrior(AssemblyDocument doc)
	{
		PriorTransforms.Clear();
		foreach (var o in doc?.Occurrences ?? Enumerable.Empty<AssemblyOccurrence>())
			if (o?.Transform != null && o.Transform.IsValid(out _))
				PriorTransforms[o.Id] = o.Transform.ToXform();
	}

	public bool PriorTransformsStillHold(AssemblyDocument doc)
	{
		if (doc == null) return PriorTransforms.Count == 0;
		foreach (var (id, prior) in PriorTransforms)
		{
			var o = doc.Occurrences.FirstOrDefault(x => x.Id == id);
			if (o?.Transform == null || !o.Transform.IsValid(out _)) return false;
			var now = o.Transform.ToXform();
			if (!now.Origin.AlmostEquals(prior.Origin)
				|| !now.X.AlmostEquals(prior.X) || !now.Y.AlmostEquals(prior.Y) || !now.Z.AlmostEquals(prior.Z))
				return false;
		}
		return true;
	}
}

public sealed class AssemblyJointResult
{
	public bool Ok;
	public string JointId = "";
	public string ErrorCode = "";
	public string Message = "";
	public string MovingOccurrenceId = "";
	public string RootOccurrenceId = "";
	public Xform ProposedTransform;
	public bool HasProposed;

	public static AssemblyJointResult Fail(string code, string message) =>
		new() { Ok = false, ErrorCode = code, Message = message };

	public static AssemblyJointResult Success(string jointId, string message) =>
		new() { Ok = true, JointId = jointId, Message = message };
}

public sealed class AssemblyJointPreview
{
	public bool Ok;
	public string ErrorCode = "";
	public string Message = "";
	public string MovingOccurrenceId = "";
	public string RootOccurrenceId = "";
	public Xform ProposedTransform;
	public bool HasProposed;
	public string AnchorText = "";
	public string RelationText = "";
}

public static class AssemblyJointService
{
	// ExtensionData keys. Shared with AS03 (grounding, connector store) and AS06
	// (solver): do not rename without updating those tasks.
	public const string ExtName = "name";
	public const string ExtSuppressed = "suppressed";
	public const string ExtOffset = "offset";
	public const string ExtFlipped = "flipped";
	public const string ExtGrounded = "grounded";
	// Stored per side so editing a joint shows the connector the user picked
	// (face / edge / axis / origin + label), not just the part name. AS03 also
	// names stored connectors by ID ("connectorId") on the joint; both shapes
	// read back through GetJointConnector below.
	public const string ExtConnectorA = "connectorA";
	public const string ExtConnectorB = "connectorB";

	public static readonly string[] ConnectorKinds = { "face", "edge", "axis", "origin" };

	// ---- ExtensionData helpers ----

	static JsonElement ToElement<T>(T value)
	{
		using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
		return doc.RootElement.Clone();
	}

	static void SetExt(Dictionary<string, JsonElement> ext, string key, JsonElement value)
	{
		if (ext == null) return;
		ext[key] = value;
	}

	static string GetStringExt(Dictionary<string, JsonElement> ext, string key, string fallback = "")
	{
		if (ext != null && ext.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
			return el.GetString() ?? fallback;
		return fallback;
	}

	static bool GetBoolExt(Dictionary<string, JsonElement> ext, string key, bool fallback = false)
	{
		if (ext != null && ext.TryGetValue(key, out var el)
			&& (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
			return el.GetBoolean();
		return fallback;
	}

	static Vec3 GetVec3Ext(Dictionary<string, JsonElement> ext, string key, Vec3 fallback)
	{
		if (ext != null && ext.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Array)
		{
			var items = el.EnumerateArray().ToArray();
			if (items.Length == 3 && items.All(i => i.ValueKind == JsonValueKind.Number))
			{
				var v = new Vec3(items[0].GetSingle(), items[1].GetSingle(), items[2].GetSingle());
				if (float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z)) return v;
			}
		}
		return fallback;
	}

	// ---- Stored joint / occurrence state ----

	public static string GetJointName(AssemblyJoint joint) =>
		GetStringExt(joint?.ExtensionData, ExtName);

	public static string JointDisplayName(AssemblyDocument doc, AssemblyJoint joint)
	{
		var name = GetJointName(joint);
		if (!string.IsNullOrWhiteSpace(name)) return name;
		var index = doc == null ? 0 : Math.Max(0, doc.Joints.IndexOf(joint));
		return $"Fixed joint {index + 1}";
	}

	public static bool IsJointSuppressed(AssemblyJoint joint) =>
		GetBoolExt(joint?.ExtensionData, ExtSuppressed);

	public static Vec3 GetJointOffset(AssemblyJoint joint) =>
		GetVec3Ext(joint?.ExtensionData, ExtOffset, Vec3.Zero);

	public static bool IsJointFlipped(AssemblyJoint joint) =>
		GetBoolExt(joint?.ExtensionData, ExtFlipped);

	public static bool IsOccurrenceGrounded(AssemblyOccurrence occurrence) =>
		AssemblyGrounding.IsGrounded(occurrence);

	static void SetJointName(AssemblyJoint joint, string name)
	{
		joint.ExtensionData ??= new Dictionary<string, JsonElement>();
		SetExt(joint.ExtensionData, ExtName, ToElement(name ?? ""));
	}

	static void SetJointSuppressed(AssemblyJoint joint, bool suppressed)
	{
		joint.ExtensionData ??= new Dictionary<string, JsonElement>();
		SetExt(joint.ExtensionData, ExtSuppressed, ToElement(suppressed));
	}

	static void SetJointOffset(AssemblyJoint joint, Vec3 offset)
	{
		joint.ExtensionData ??= new Dictionary<string, JsonElement>();
		SetExt(joint.ExtensionData, ExtOffset, ToElement(new[] { offset.x, offset.y, offset.z }));
	}

	static void SetJointFlipped(AssemblyJoint joint, bool flipped)
	{
		joint.ExtensionData ??= new Dictionary<string, JsonElement>();
		SetExt(joint.ExtensionData, ExtFlipped, ToElement(flipped));
	}

	static void SetJointConnector(AssemblyJoint joint, string key, AssemblyConnectorRef connector)
	{
		joint.ExtensionData ??= new Dictionary<string, JsonElement>();
		SetExt(joint.ExtensionData, key, ToElement(new
		{
			kind = connector.SourceKind,
			label = connector.SourceLabel ?? "",
			reference = connector.SourceReference ?? "",
		}));
	}

	static (string Kind, string Label, string Reference) GetJointConnector(AssemblyDocument doc, AssemblyJoint joint, string key, string fallbackLabel)
	{
		if (joint?.ExtensionData != null && joint.ExtensionData.TryGetValue(key, out var el)
			&& el.ValueKind == JsonValueKind.Object)
		{
			var kind = el.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
				? k.GetString() ?? "origin" : "origin";
			var label = el.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
				? l.GetString() ?? "" : "";
			var reference = el.TryGetProperty("reference", out var r) && r.ValueKind == JsonValueKind.String
				? r.GetString() ?? "" : "";
			if (el.TryGetProperty("kind", out _))
				return (kind, label, reference);
			// AS03 shape: {"connectorId": "..."} — resolve the stored connector.
			if (el.TryGetProperty("connectorId", out var idEl) && idEl.ValueKind == JsonValueKind.String)
			{
				var stored = AssemblyConnectorStore.FindConnector(doc, idEl.GetString());
				if (stored != null)
					return (stored.Kind, stored.Name, stored.Source?.SourceReference ?? "");
			}
			return (kind, label, reference);
		}
		return ("origin", fallbackLabel, "");
	}

	public static void SetOccurrenceGrounded(AssemblyOccurrence occurrence, bool grounded)
	{
		if (occurrence == null) return;
		// Same storage rule as AssemblyGrounding.SetGrounded: clearing removes the
		// key (and nulls ExtensionData when empty) so a transiently-used document
		// keeps HasData == false.
		if (AssemblyGrounding.IsGrounded(occurrence) == grounded) return;
		if (grounded)
		{
			occurrence.ExtensionData ??= new Dictionary<string, JsonElement>();
			SetExt(occurrence.ExtensionData, ExtGrounded, ToElement(true));
		}
		else
		{
			occurrence.ExtensionData?.Remove(ExtGrounded);
			if (occurrence.ExtensionData?.Count == 0) occurrence.ExtensionData = null;
		}
	}

	public static string DefaultJointName(AssemblyDocument doc) =>
		$"Fixed joint {(doc?.Joints.Count ?? 0) + 1}";

	// ---- Validation ----

	public static string ValidateConnector(AssemblyDocument doc, AssemblyConnectorRef connector)
	{
		if (doc == null) return "No assembly document is open.";
		if (connector == null) return "A connector is missing. Pick a connector on each part first.";
		if (string.IsNullOrWhiteSpace(connector.OccurrenceId))
			return "A connector has no part. Pick the connector again.";
		var occurrence = doc.Occurrences.FirstOrDefault(o => o.Id == connector.OccurrenceId);
		if (occurrence == null)
			return $"The part for connector '{connector.DisplayLabel}' is no longer in the assembly. Pick a connector on a part that is still there.";
		if (connector.Frame == null)
			return $"Connector '{connector.DisplayLabel}' has no frame. Pick the connector again.";
		if (!connector.Frame.IsRigidFrame(out var why))
			return $"Connector '{connector.DisplayLabel}' is not a usable frame: {why} Pick a flat face, straight edge, axis or origin instead.";
		if (!ConnectorKinds.Contains(connector.SourceKind))
			return $"Connector '{connector.DisplayLabel}' has unknown kind '{connector.SourceKind}'.";
		if (occurrence.Transform == null)
			return $"Part '{occurrence.Name}' has no placement. Fix the part placement first.";
		if (!occurrence.Transform.IsValid(out var tWhy))
			return $"Part '{occurrence.Name}' has an invalid placement: {tWhy} Fix the part placement first.";
		return null;
	}

	/// <summary>Which occurrence stays put, and why. Fails only when both sides are
	/// grounded — that is a real conflict, not a choice to guess at.</summary>
	public static AssemblyJointResult ResolveRoot(AssemblyDocument doc, AssemblyJointDraft draft)
	{
		var a = doc.Occurrences.FirstOrDefault(o => o.Id == draft.ConnectorA.OccurrenceId);
		var b = doc.Occurrences.FirstOrDefault(o => o.Id == draft.ConnectorB.OccurrenceId);
		var aGrounded = a != null && IsOccurrenceGrounded(a);
		var bGrounded = b != null && IsOccurrenceGrounded(b);
		if (aGrounded && bGrounded)
			return AssemblyJointResult.Fail("conflicting-constraint",
				$"Both '{a.Name}' and '{b.Name}' are grounded, so neither can move to meet the other. Unground one part first, then apply the joint.");
		if (aGrounded) return new AssemblyJointResult { Ok = true, RootOccurrenceId = a.Id, Message = $"'{a.Name}' is grounded, so it stays put." };
		if (bGrounded) return new AssemblyJointResult { Ok = true, RootOccurrenceId = b.Id, Message = $"'{b.Name}' is grounded, so it stays put." };
		if (!string.IsNullOrWhiteSpace(draft.RootOccurrenceId)
			&& (draft.RootOccurrenceId == a.Id || draft.RootOccurrenceId == b.Id))
			return new AssemblyJointResult { Ok = true, RootOccurrenceId = draft.RootOccurrenceId, Message = "No part is grounded, so the part you chose as root stays put." };
		return new AssemblyJointResult
		{
			Ok = true,
			RootOccurrenceId = a.Id,
			Message = $"No part is grounded. '{a.Name}' stays put by default; pick '{b.Name}' as the root instead if that is the fixed part.",
		};
	}

	/// <summary>Root choices the sheet offers when nothing is grounded: each side
	/// with what choosing it means. Empty when a grounded part already decides it.</summary>
	public static IReadOnlyList<(string OccurrenceId, string Label)> GroundedRootOptions(AssemblyDocument doc, AssemblyJointDraft draft)
	{
		var options = new List<(string, string)>();
		if (doc == null || draft?.ConnectorA == null || draft?.ConnectorB == null) return options;
		var a = doc.Occurrences.FirstOrDefault(o => o.Id == draft.ConnectorA.OccurrenceId);
		var b = doc.Occurrences.FirstOrDefault(o => o.Id == draft.ConnectorB.OccurrenceId);
		if (a == null || b == null) return options;
		if (IsOccurrenceGrounded(a) || IsOccurrenceGrounded(b)) return options;
		options.Add((a.Id, $"Keep '{a.Name}' fixed, move '{b.Name}'"));
		options.Add((b.Id, $"Keep '{b.Name}' fixed, move '{a.Name}'"));
		return options;
	}

	static string FindConflict(AssemblyDocument doc, AssemblyJointDraft draft)
	{
		// A second non-suppressed fixed joint between parts that are already fixed
		// together — directly or through a chain — would over-constrain the assembly.
		// The full loop solver is AS06; this is the check the sheet needs
		// before it lets Apply commit a broken relation.
		var joints = doc.Joints
			.Where(j => j != null && j.Kind == "fixed" && !IsJointSuppressed(j) && j.Id != draft.JointId)
			.ToList();
		var neighbours = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		void Link(string x, string y)
		{
			if (!neighbours.TryGetValue(x, out var list)) neighbours[x] = list = new List<string>();
			if (!list.Contains(y)) list.Add(y);
		}
		foreach (var j in joints)
		{
			if (string.IsNullOrWhiteSpace(j.OccurrenceA) || string.IsNullOrWhiteSpace(j.OccurrenceB)) continue;
			Link(j.OccurrenceA, j.OccurrenceB);
			Link(j.OccurrenceB, j.OccurrenceA);
		}
		var start = draft.ConnectorA.OccurrenceId;
		var target = draft.ConnectorB.OccurrenceId;
		var seen = new HashSet<string>(StringComparer.Ordinal) { start };
		var queue = new Queue<string>();
		queue.Enqueue(start);
		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			if (!neighbours.TryGetValue(current, out var next)) continue;
			foreach (var n in next)
			{
				if (n == target)
				{
					var direct = joints.Any(j =>
						(j.OccurrenceA == start && j.OccurrenceB == target)
						|| (j.OccurrenceA == target && j.OccurrenceB == start));
					var aName = doc.Occurrences.FirstOrDefault(o => o.Id == start)?.Name ?? start;
					var bName = doc.Occurrences.FirstOrDefault(o => o.Id == target)?.Name ?? target;
					return direct
						? $"'{aName}' and '{bName}' already have a fixed joint. Edit that joint instead of adding another one."
						: $"'{aName}' and '{bName}' are already fixed together through other joints. Adding this joint would over-constrain them — suppress or delete one of the existing joints first.";
				}
				if (seen.Add(n)) queue.Enqueue(n);
			}
		}
		return null;
	}

	public static string ValidateDraft(AssemblyDocument doc, AssemblyJointDraft draft)
	{
		if (doc == null) return "No assembly document is open.";
		if (draft == null) return "No joint is being edited.";
		if (draft.ConnectorA == null || draft.ConnectorB == null)
			return "Pick a connector on each part first. A fixed joint needs two connectors.";
		if (draft.ConnectorA.OccurrenceId == draft.ConnectorB.OccurrenceId)
			return "Both connectors are on the same part. A fixed joint joins two different parts.";
		var error = ValidateConnector(doc, draft.ConnectorA) ?? ValidateConnector(doc, draft.ConnectorB);
		if (error != null) return error;
		if (!float.IsFinite(draft.Offset.x) || !float.IsFinite(draft.Offset.y) || !float.IsFinite(draft.Offset.z))
			return "The offset must be three finite numbers, in inches.";
		if (!string.IsNullOrWhiteSpace(draft.Name))
		{
			var clash = doc.Joints.FirstOrDefault(j => j != null && j.Id != draft.JointId
				&& string.Equals(GetJointName(j), draft.Name.Trim(), StringComparison.OrdinalIgnoreCase));
			if (clash != null)
				return $"There is already a joint called '{draft.Name.Trim()}'. Give this joint a different name.";
		}
		var root = ResolveRoot(doc, draft);
		if (!root.Ok) return root.Message;
		return FindConflict(doc, draft);
	}

	// ---- Preview (disposable, never mutates the document) ----

	public static AssemblyJointPreview Preview(AssemblyDocument doc, AssemblyJointDraft draft)
	{
		var preview = new AssemblyJointPreview();
		var error = ValidateDraft(doc, draft);
		if (error != null)
		{
			preview.ErrorCode = "invalid-joint";
			preview.Message = error;
			return preview;
		}
		var root = ResolveRoot(doc, draft);
		var a = doc.Occurrences.First(o => o.Id == draft.ConnectorA.OccurrenceId);
		var b = doc.Occurrences.First(o => o.Id == draft.ConnectorB.OccurrenceId);
		var moving = root.RootOccurrenceId == a.Id ? b : a;
		var rootOcc = root.RootOccurrenceId == a.Id ? a : b;
		var frameA = draft.ConnectorA.Frame.ToXform();
		var frameB = draft.ConnectorB.Frame.ToXform();
		var flippedB = draft.Flipped
			? frameB * Xform.Rotate(new Vec3(1, 0, 0), MathF.PI)
			: frameB;
		// World frames today, so the sheet can show the current relation.
		var worldA = a.Transform.ToXform() * frameA;
		var worldB = b.Transform.ToXform() * frameB;
		var gap = worldB.Origin - worldA.Origin;
		var angleDeg = MathF.Acos(Math.Clamp(Vec3.Dot(worldA.Z.Normal, worldB.Z.Normal), -1f, 1f)) * 180f / MathF.PI;
		preview.AnchorText = $"A {a.Name} — {draft.ConnectorA.DisplayLabel} at {Fmt(worldA.Origin)} in, +Z {Fmt(worldA.Z.Normal)}"
			+ $"\nB {b.Name} — {draft.ConnectorB.DisplayLabel} at {Fmt(worldB.Origin)} in, +Z {Fmt(worldB.Z.Normal)}"
			+ (draft.Flipped ? "\nFlipped: B is turned 180° about its own X axis before it is aligned." : "");
		preview.RelationText = gap.Length < 1e-4f && angleDeg < 0.1f
			? "Connectors already line up. Apply keeps both parts where they are."
			: $"Connectors are {gap.Length:0.###} in apart and their +Z axes are {angleDeg:0.#}° apart. Apply moves '{moving.Name}' to line them up."
			+ (draft.Offset.LengthSquared > 0f ? $" Then it shifts '{moving.Name}' by offset {Fmt(draft.Offset)} in along the A frame axes." : "");
		try
		{
			Xform solved;
			if (root.RootOccurrenceId == a.Id)
			{
				// TA * FA * Translate(offset) == TB * FB'  =>  TB = TA*FA*T(offset) * Inverse(FB')
				var target = a.Transform.ToXform() * frameA * Xform.Translate(draft.Offset);
				solved = target * flippedB.Inverse;
			}
			else
			{
				// TA = TB*FB' * Translate(-offset) * Inverse(FA)
				var target = b.Transform.ToXform() * flippedB * Xform.Translate(-draft.Offset);
				solved = target * frameA.Inverse;
			}
			preview.Ok = true;
			preview.RootOccurrenceId = root.RootOccurrenceId;
			preview.MovingOccurrenceId = moving.Id;
			preview.ProposedTransform = solved;
			preview.HasProposed = true;
			preview.Message = root.Message + $" Moving '{moving.Name}' to {Fmt(solved.Origin)} in.";
			if (rootOcc.Transform.ToXform().Determinant < 0f)
				preview.Message += $" Note: '{rootOcc.Name}' is mirrored, so the solved position keeps that mirror.";
		}
		catch (InvalidOperationException e)
		{
			preview.ErrorCode = "invalid-frame";
			preview.Message = "The joint cannot be solved from these frames: " + e.Message;
		}
		return preview;
	}

	public static string Fmt(Vec3 v) =>
		$"({v.x.ToString("0.###", CultureInfo.InvariantCulture)}, {v.y.ToString("0.###", CultureInfo.InvariantCulture)}, {v.z.ToString("0.###", CultureInfo.InvariantCulture)})";

	// ---- Commit paths (one undo step each, via AssemblyHistory) ----

	public static AssemblyJointResult CreateFixedJoint(AssemblyDocument doc, EditHistory history, AssemblyJointDraft draft)
	{
		if (doc == null) return AssemblyJointResult.Fail("no-document", "No assembly document is open.");
		var preview = Preview(doc, draft);
		if (!preview.Ok) return AssemblyJointResult.Fail(preview.ErrorCode, preview.Message);
		var name = string.IsNullOrWhiteSpace(draft.Name) ? DefaultJointName(doc) : draft.Name.Trim();
		var again = doc.Joints.FirstOrDefault(j => j != null
			&& string.Equals(GetJointName(j), name, StringComparison.OrdinalIgnoreCase));
		if (again != null) return AssemblyJointResult.Fail("duplicate-name", $"There is already a joint called '{name}'. Give this joint a different name.");
		var movingName = doc.Occurrences.First(o => o.Id == preview.MovingOccurrenceId).Name;

		var joint = new AssemblyJoint
		{
			Id = AssemblyDocument.NewId(),
			Kind = "fixed",
			OccurrenceA = draft.ConnectorA.OccurrenceId,
			OccurrenceB = draft.ConnectorB.OccurrenceId,
			FrameA = draft.ConnectorA.Frame,
			FrameB = draft.ConnectorB.Frame,
		};
		AssemblyHistory.ApplyWithHistory(doc, history, $"Create fixed joint '{name}'", () =>
		{
			SetJointName(joint, name);
			SetJointOffset(joint, draft.Offset);
			SetJointFlipped(joint, draft.Flipped);
			SetJointSuppressed(joint, draft.Suppressed);
			SetJointConnector(joint, ExtConnectorA, draft.ConnectorA);
			SetJointConnector(joint, ExtConnectorB, draft.ConnectorB);
			doc.Joints.Add(joint);
			if (!draft.Suppressed)
			{
				var moving = doc.Occurrences.First(o => o.Id == preview.MovingOccurrenceId);
				moving.Transform = AssemblyTransform.FromXform(preview.ProposedTransform);
			}
		});
		return new AssemblyJointResult
		{
			Ok = true,
			JointId = joint.Id,
			MovingOccurrenceId = preview.MovingOccurrenceId,
			RootOccurrenceId = preview.RootOccurrenceId,
			ProposedTransform = preview.ProposedTransform,
			HasProposed = preview.HasProposed,
			Message = draft.Suppressed
				? $"Created '{name}' (suppressed — parts did not move)."
				: $"Created '{name}'. Moved '{movingName}' to line the connectors up.",
		};
	}

	public static AssemblyJointResult UpdateFixedJoint(AssemblyDocument doc, EditHistory history, AssemblyJointDraft draft)
	{
		if (doc == null) return AssemblyJointResult.Fail("no-document", "No assembly document is open.");
		var joint = doc.Joints.FirstOrDefault(j => j != null && j.Id == draft?.JointId);
		if (joint == null) return AssemblyJointResult.Fail("missing-joint", "That joint is no longer in the assembly. It may have been deleted.");
		if (joint.Kind != "fixed") return AssemblyJointResult.Fail("wrong-kind", $"Only fixed joints can be edited here; this one is '{joint.Kind}'.");
		var preview = Preview(doc, draft);
		if (!preview.Ok) return AssemblyJointResult.Fail(preview.ErrorCode, preview.Message);
		var name = string.IsNullOrWhiteSpace(draft.Name) ? JointDisplayName(doc, joint) : draft.Name.Trim();
		var movingName = doc.Occurrences.First(o => o.Id == preview.MovingOccurrenceId).Name;

		AssemblyHistory.ApplyWithHistory(doc, history, $"Update fixed joint '{name}'", () =>
		{
			joint.OccurrenceA = draft.ConnectorA.OccurrenceId;
			joint.OccurrenceB = draft.ConnectorB.OccurrenceId;
			joint.FrameA = draft.ConnectorA.Frame;
			joint.FrameB = draft.ConnectorB.Frame;
			SetJointName(joint, name);
			SetJointOffset(joint, draft.Offset);
			SetJointFlipped(joint, draft.Flipped);
			SetJointSuppressed(joint, draft.Suppressed);
			SetJointConnector(joint, ExtConnectorA, draft.ConnectorA);
			SetJointConnector(joint, ExtConnectorB, draft.ConnectorB);
			if (!draft.Suppressed)
			{
				var moving = doc.Occurrences.First(o => o.Id == preview.MovingOccurrenceId);
				moving.Transform = AssemblyTransform.FromXform(preview.ProposedTransform);
			}
		});
		return new AssemblyJointResult
		{
			Ok = true,
			JointId = joint.Id,
			MovingOccurrenceId = preview.MovingOccurrenceId,
			RootOccurrenceId = preview.RootOccurrenceId,
			ProposedTransform = preview.ProposedTransform,
			HasProposed = preview.HasProposed,
			Message = draft.Suppressed
				? $"Updated '{name}' (suppressed — parts did not move)."
				: $"Updated '{name}'.",
		};
	}

	public static AssemblyJointResult RenameFixedJoint(AssemblyDocument doc, EditHistory history, string jointId, string newName)
	{
		if (doc == null) return AssemblyJointResult.Fail("no-document", "No assembly document is open.");
		var joint = doc.Joints.FirstOrDefault(j => j != null && j.Id == jointId);
		if (joint == null) return AssemblyJointResult.Fail("missing-joint", "That joint is no longer in the assembly.");
		if (joint.Kind != "fixed") return AssemblyJointResult.Fail("wrong-kind", $"Only fixed joints can be renamed here; this one is '{joint.Kind}'.");
		if (string.IsNullOrWhiteSpace(newName)) return AssemblyJointResult.Fail("invalid-name", "A joint name cannot be empty.");
		var trimmed = newName.Trim();
		if (doc.Joints.Any(j => j != null && j.Id != jointId
			&& string.Equals(GetJointName(j), trimmed, StringComparison.OrdinalIgnoreCase)))
			return AssemblyJointResult.Fail("duplicate-name", $"There is already a joint called '{trimmed}'. Give this joint a different name.");
		AssemblyHistory.ApplyWithHistory(doc, history, $"Rename fixed joint to '{trimmed}'", () =>
		{
			SetJointName(joint, trimmed);
		});
		// Renaming moves no geometry, so no rebuild.
		return AssemblyJointResult.Success(joint.Id, $"Renamed the joint to '{trimmed}'.");
	}

	public static AssemblyJointResult SetJointSuppressed(AssemblyDocument doc, EditHistory history, string jointId, bool suppressed)
	{
		if (doc == null) return AssemblyJointResult.Fail("no-document", "No assembly document is open.");
		var joint = doc.Joints.FirstOrDefault(j => j != null && j.Id == jointId);
		if (joint == null) return AssemblyJointResult.Fail("missing-joint", "That joint is no longer in the assembly.");
		if (joint.Kind != "fixed") return AssemblyJointResult.Fail("wrong-kind", $"Only fixed joints can be suppressed here; this one is '{joint.Kind}'.");
		if (IsJointSuppressed(joint) == suppressed)
			return AssemblyJointResult.Success(joint.Id, suppressed ? "That joint is already suppressed." : "That joint is already active.");
		if (!suppressed)
		{
			// Unsuppressing re-solves the joint from its stored frames, so the
			// parts line up again instead of silently staying where they drifted.
			var draft = DraftFromJoint(doc, joint);
			if (draft == null) return AssemblyJointResult.Fail("invalid-joint", "That joint's connectors are missing, so it cannot be turned back on. Edit it and pick connectors again.");
			var preview = Preview(doc, draft);
			if (!preview.Ok) return AssemblyJointResult.Fail(preview.ErrorCode, preview.Message);
			var movingName = doc.Occurrences.First(o => o.Id == preview.MovingOccurrenceId).Name;
			AssemblyHistory.ApplyWithHistory(doc, history, $"Unsuppress fixed joint '{GetJointName(joint)}'", () =>
			{
				SetJointSuppressed(joint, false);
				var moving = doc.Occurrences.First(o => o.Id == preview.MovingOccurrenceId);
				moving.Transform = AssemblyTransform.FromXform(preview.ProposedTransform);
			});
			return AssemblyJointResult.Success(joint.Id, $"Turned '{GetJointName(joint)}' back on and moved '{movingName}' to line the connectors up.");
		}
		var suppressedName = GetJointName(joint);
		AssemblyHistory.ApplyWithHistory(doc, history, $"Suppress fixed joint '{suppressedName}'", () =>
		{
			SetJointSuppressed(joint, true);
		});
		// Suppressing moves no geometry, so no rebuild.
		return AssemblyJointResult.Success(joint.Id, $"Suppressed '{suppressedName}'. The parts stay where they are.");
	}

	public static AssemblyJointResult DeleteFixedJoint(AssemblyDocument doc, EditHistory history, string jointId)
	{
		if (doc == null) return AssemblyJointResult.Fail("no-document", "No assembly document is open.");
		var joint = doc.Joints.FirstOrDefault(j => j != null && j.Id == jointId);
		if (joint == null) return AssemblyJointResult.Fail("missing-joint", "That joint is already gone.");
		if (joint.Kind != "fixed") return AssemblyJointResult.Fail("wrong-kind", $"Only fixed joints can be deleted here; this one is '{joint.Kind}'.");
		var name = JointDisplayName(doc, joint);
		AssemblyHistory.ApplyWithHistory(doc, history, $"Delete fixed joint '{name}'", () =>
		{
			doc.Joints.Remove(joint);
		});
		// Deleting a joint moves no geometry, so no rebuild.
		return AssemblyJointResult.Success(jointId, $"Deleted '{name}'. The parts stay where they are.");
	}

	/// <summary>Ground one occurrence and unground the rest, in one undo step.</summary>
	public static AssemblyJointResult ChooseGroundedRoot(AssemblyDocument doc, EditHistory history, string occurrenceId)
	{
		if (doc == null) return AssemblyJointResult.Fail("no-document", "No assembly document is open.");
		var chosen = doc.Occurrences.FirstOrDefault(o => o.Id == occurrenceId);
		if (chosen == null) return AssemblyJointResult.Fail("missing-occurrence", "That part is no longer in the assembly.");
		AssemblyHistory.ApplyWithHistory(doc, history, $"Ground '{chosen.Name}'", () =>
		{
			foreach (var o in doc.Occurrences) AssemblyGrounding.SetGrounded(doc, o.Id, o.Id == occurrenceId);
		});
		return AssemblyJointResult.Success(occurrenceId, $"'{chosen.Name}' is now the grounded part. It will not move when joints are applied.");
	}

	/// <summary>Rebuild an editable draft from a stored joint. Null when a connector
	/// reference is gone — the sheet then asks the user to pick again instead of
	/// guessing.</summary>
	public static AssemblyJointDraft DraftFromJoint(AssemblyDocument doc, AssemblyJoint joint)
	{
		if (doc == null || joint == null) return null;
		var a = doc.Occurrences.FirstOrDefault(o => o.Id == joint.OccurrenceA);
		var b = doc.Occurrences.FirstOrDefault(o => o.Id == joint.OccurrenceB);
		if (a == null || b == null || joint.FrameA == null || joint.FrameB == null) return null;
		var (kindA, labelA, refA) = GetJointConnector(doc, joint, ExtConnectorA, a.Name);
		var (kindB, labelB, refB) = GetJointConnector(doc, joint, ExtConnectorB, b.Name);
		var draft = new AssemblyJointDraft
		{
			JointId = joint.Id,
			ConnectorA = new AssemblyConnectorRef(a.Id, joint.FrameA, kindA, labelA, refA),
			ConnectorB = new AssemblyConnectorRef(b.Id, joint.FrameB, kindB, labelB, refB),
			Name = GetJointName(joint),
			Offset = GetJointOffset(joint),
			Flipped = IsJointFlipped(joint),
			Suppressed = IsJointSuppressed(joint),
		};
		draft.CapturePrior(doc);
		return draft;
	}

	/// <summary>Joint-level issues on top of AssemblyDocument.Validate: bad frames,
	/// bad offsets, duplicate names. Suppressed joints are still validated — a
	/// suppressed joint that rots silently is a surprise when it is turned back on.</summary>
	public static IReadOnlyList<AssemblyValidationIssue> ValidateFixedJoints(AssemblyDocument doc)
	{
		var issues = new List<AssemblyValidationIssue>();
		if (doc == null) return issues;
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var joint in doc.Joints.Where(j => j != null && j.Kind == "fixed"))
		{
			var name = GetJointName(joint);
			if (!string.IsNullOrWhiteSpace(name) && !names.Add(name))
				issues.Add(new("duplicate-joint-name", joint.Id, $"More than one fixed joint is called '{name}'."));
			var offset = GetJointOffset(joint);
			if (!float.IsFinite(offset.x) || !float.IsFinite(offset.y) || !float.IsFinite(offset.z))
				issues.Add(new("invalid-joint-offset", joint.Id, "Joint offset must be three finite numbers."));
			if (joint.FrameA != null && !joint.FrameA.IsRigidFrame(out var whyA))
				issues.Add(new("invalid-connector-frame", joint.Id, $"Connector A frame must be a finite rigid transform: {whyA}"));
			if (joint.FrameB != null && !joint.FrameB.IsRigidFrame(out var whyB))
				issues.Add(new("invalid-connector-frame", joint.Id, $"Connector B frame must be a finite rigid transform: {whyB}"));
		}
		return issues;
	}
}
