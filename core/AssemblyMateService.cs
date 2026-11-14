using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Assembly;

namespace Marionette.Desktop;

// AS07: face and axis mating operations.
//
// Ports Mobile app/UiAssemblyMate.cs (AssemblyFaceMate, AssemblyFaceMateSession,
// AxisMate, AxisMateSolver) in Desktop form. The mate RECORD layer lives in the
// kernel — AssemblyMate / AssemblyMateFace / AssemblyMateAxisSource in
// kernel/Effigy/Assembly/AssemblyDocument.cs (mate-feature ticket) — and this file
// is the command/solve layer on top of it: pure solving, sessions, and commits.
// Do not duplicate those record types here; a joint written by this file must
// read back through AssemblyMate.TryRead, and a joint written by the kernel's
// AssemblyMate.ToJoint must solve here.
//
// Bot commands accept stable IDs plus explicit geometry descriptors — a face is
// (occurrenceId, faceIndex), an edge is (occurrenceId, vertexA, vertexB), a
// circular source is (occurrenceId, ring vertex indices) — and geometry resolves
// through the AS04 AssemblySourceResolver delegate; there is no picking UI and
// no second mesh-provider type. Selections become AS03 local connector frames
// (AssemblyConnectorGeometry) and commits go through the AS05/AS06 equation,
// never a second solver.
//
// Two deliberate deviations from the Mobile commit path, both forced by the
// payload: a mate's stored record lives in the joint's ExtensionData under
// "faceMate" / "axisMate", and neither AssemblyJointService.CreateFixedJoint nor
// AssemblyHingeService.CreateHinge writes a payload — they build their own
// joint. So ApplyToDocument / AxisMate.ApplyTo build the joint the same way
// Mobile does (live frames + payload) and commit it plus the occurrence move in
// ONE AssemblyHistory entry, exactly like AS05's commit (snapshot before, one
// EditHistory entry, undo restores the joint and the transform). The solve
// equation is provably the AS05 fixed-joint equation — AlignFaces is
// Ta*Fa*Trans(0,0,offset)*(opposed ? RotX(PI) : I)*Fb^-1, i.e. the fixed-joint
// target with the offset in frame-A axes — and the tests cross-check a face-mate
// result against AssemblyJointService.Preview for the same frames and offset.
//
// Mobile gap closed here: AxisMate.ApplyTo on Mobile does NOT refuse a grounded B.
// Desktop refuses it (error "grounded-part", nothing moves, nothing committed).
//
// A legacy centroid mate (AssemblyMateFeature) is never converted or deleted by
// this file; a document holding both a face mate and a legacy mate validates clean.
//
// Inches, Z-up, radians in core; units convert once at the boundary (storage stays
// inches). No CadDoc, no Godot, no Mobile namespaces. Not thread-safe.

/// <summary>One diagnostic from a face-mate solve or stored-record validation.
/// Codes are stable strings the bot layer can match on.</summary>
public sealed record FaceMateDiagnostic(string Code, string Message, string JointId = "");

/// <summary>Result of a face-mate solve. Pure preview: Solve never moves anything.
/// Ok carries the joint that ApplyToDocument would commit plus B's target transform.</summary>
public sealed class FaceMateResult
{
	public bool Ok { get; set; }
	public FaceMateDiagnostic[] Diagnostics { get; set; } = Array.Empty<FaceMateDiagnostic>();
	public Xform? NewTransformB { get; set; }
	public AssemblyJoint Joint { get; set; }
	public string Summary { get; set; } = "";
	public float SeparationInches { get; set; }
	public Vec3 WorldNormalA { get; set; }
	public Vec3 WorldNormalB { get; set; }
}

/// <summary>Face mates: bring two planar faces together (coincident, offset, or
/// same-facing) by moving B onto A. Port of Mobile's AssemblyFaceMate. Pure solve —
/// Solve never moves anything; ApplyToDocument commits the joint plus B's move in
/// one history entry.</summary>
public static class AssemblyFaceMate
{
	const float RigidEpsilon = 1e-4f;
	const float PositionEpsilon = 1e-4f;
	const float PlanarTolerance = 1e-3f;

	// ---- Geometry access: resolver delegate -> the Body list FacePlane needs ----

	/// <summary>Body id a definition's source resolves under: the fragment after
	/// the last '#' of the source reference (Mobile's SourceBodyId); the whole
	/// reference for a studio body.</summary>
	public static string SourceBodyId(AssemblyPartDefinition definition)
	{
		if (definition == null) return "";
		var reference = definition.SourceReference ?? "";
		if (definition.SourceKind == "studio-body") return reference;
		var hash = reference.LastIndexOf('#');
		return hash >= 0 ? reference.Substring(hash + 1) : "";
	}

	/// <summary>Build the Body list FacePlane resolves against from the document's
	/// definitions through the resolver. Definitions whose source is missing are
	/// simply absent — a mate on one reports missing-source-body, never a crash.</summary>
	public static List<Body> BodiesFor(AssemblyDocument doc, AssemblySourceResolver source)
	{
		var bodies = new List<Body>();
		if (doc?.Definitions == null || source == null) return bodies;
		foreach (var definition in doc.Definitions)
		{
			if (definition == null) continue;
			var resolved = source(definition);
			if (resolved == null || resolved.Value.Mesh == null) continue;
			bodies.Add( new Body( SourceBodyId( definition ), definition.Name ?? "", resolved.Value.Mesh ) );
		}
		return bodies;
	}

	/// <summary>Bot-command entry: turn an explicit geometry descriptor
	/// (occurrenceId, faceIndex) into the face a mate solves against. The
	/// captured reference carries the face's point and normal, so the mate follows
	/// a moved face and reports face-invalid when the face is gone.</summary>
	public static bool TryFromFace(AssemblyDocument doc, AssemblySourceResolver source,
		string occurrenceId, int faceIndex, out AssemblyMateFace face, out string why)
	{
		face = null;
		why = null;
		if (doc == null) { why = "No assembly document is open."; return false; }
		var occurrence = doc.Occurrences.FirstOrDefault(o => o != null && o.Id == occurrenceId);
		if (occurrence == null) { why = $"No occurrence '{occurrenceId}' is in this assembly."; return false; }
		var definition = doc.Definitions.FirstOrDefault(d => d != null && d.Id == occurrence.DefinitionId);
		if (definition == null) { why = $"Occurrence '{occurrence.Name}' has no part definition."; return false; }
		var resolved = source?.Invoke(definition);
		if (resolved == null || resolved.Value.Mesh == null)
		{
			why = $"The source mesh for '{occurrence.Name}' is missing.";
			return false;
		}
		var mesh = resolved.Value.Mesh;
		if (faceIndex < 0 || faceIndex >= mesh.Faces.Count)
		{
			why = $"Face {faceIndex} does not exist on '{occurrence.Name}'.";
			return false;
		}
		var body = new Body( SourceBodyId( definition ), definition.Name ?? "", mesh );
		var centroid = mesh.FaceCentroid(mesh.Faces[faceIndex]);
		face = new AssemblyMateFace { OccurrenceId = occurrenceId, Ref = FacePlane.Capture(body, faceIndex, centroid) };
		return true;
	}

	// ---- Face resolution ----

	/// <summary>Resolve a stored face to its CURRENT local frame from the live mesh.
	/// The mate follows a moved face and never reuses a stale transform. A face
	/// that is not planar (quad corners off the plane beyond 1e-3 in) is refused
	/// with face-not-planar.</summary>
	public static bool TryResolveFaceFrame(AssemblyDocument doc, AssemblySourceResolver source,
		AssemblyMateFace face, out Xform localFrame, out string code, out string why)
	{
		localFrame = default;
		code = null;
		why = null;
		if (face == null || string.IsNullOrWhiteSpace(face.OccurrenceId))
		{
			code = "invalid-face-reference";
			why = "The face mate needs a face on a part in this assembly.";
			return false;
		}
		var bodies = BodiesFor(doc, source);
		if (bodies.All(b => b?.Id != face.Ref.BodyId))
		{
			code = "missing-source-body";
			why = $"The source body '{face.Ref.BodyId}' is not in this document.";
			return false;
		}
		if (!FacePlane.TryResolveFace(bodies, face.Ref, out var body, out var faceIndex))
		{
			code = "face-invalid";
			why = "The stored face no longer exists. Reselect it.";
			return false;
		}
		var meshFace = body.Mesh.Faces[faceIndex];
		if (!IsPlanar(body.Mesh, meshFace, out var maxDeviation, out _))
		{
			code = "face-not-planar";
			why = $"The face is not flat (corners leave the plane by {maxDeviation:0.###} in). " +
				"Pick a planar face.";
			return false;
		}
		if (!FacePlane.TryResolve(bodies, face.Ref, out var plane))
		{
			code = "face-invalid";
			why = "The stored face no longer exists. Reselect it.";
			return false;
		}
		localFrame = new Xform(plane.XAxis, plane.YAxis, plane.Normal, plane.Origin);
		return true;
	}

	/// <summary>Flat within 1e-3 in scaled by the face's extent (Mobile's rule:
	/// maxDeviation &lt;= 1e-3 * max(1, extent)).</summary>
	public static bool IsPlanar(PolyMesh mesh, Face face, out float maxDeviation, out float extent)
	{
		maxDeviation = 0f;
		extent = 0f;
		if (mesh == null || face == null || face.Count < 3) return false;
		var centroid = mesh.FaceCentroid(face);
		var normal = mesh.FaceNormal(face);
		if (normal.LengthSquared < 0.5f) return false;
		normal = normal.Normal;
		for (var i = 0; i < face.Count; i++)
		{
			var p = mesh.Positions[face.Indices[i]];
			var delta = p - centroid;
			maxDeviation = MathF.Max(maxDeviation, MathF.Abs(Vec3.Dot(delta, normal)));
			extent = MathF.Max(extent, delta.Length);
		}
		return maxDeviation <= PlanarTolerance * MathF.Max(1f, extent);
	}

	/// <summary>Transform a normal through a transform: the inverse-transpose, so a
	/// mirrored or scaled placement still reports the true world normal.</summary>
	public static Vec3 TransformNormal(Xform transform, Vec3 localNormal)
	{
		var inverse = transform.Inverse;
		var x = new Vec3(inverse.X.x, inverse.Y.x, inverse.Z.x);
		var y = new Vec3(inverse.X.y, inverse.Y.y, inverse.Z.y);
		var z = new Vec3(inverse.X.z, inverse.Y.z, inverse.Z.z);
		var world = x * localNormal.x + y * localNormal.y + z * localNormal.z;
		return world.LengthSquared < 1e-12f ? Vec3.Zero : world.Normal;
	}

	static bool IsRigid(Xform transform, out string why)
	{
		why = null;
		if (!float.IsFinite(transform.Determinant)
			|| MathF.Abs(transform.X.LengthSquared - 1f) > RigidEpsilon
			|| MathF.Abs(transform.Y.LengthSquared - 1f) > RigidEpsilon
			|| MathF.Abs(transform.Z.LengthSquared - 1f) > RigidEpsilon
			|| MathF.Abs(Vec3.Dot(transform.X, transform.Y)) > RigidEpsilon
			|| MathF.Abs(Vec3.Dot(transform.Y, transform.Z)) > RigidEpsilon
			|| MathF.Abs(Vec3.Dot(transform.Z, transform.X)) > RigidEpsilon
			|| MathF.Abs(transform.Determinant - 1f) > RigidEpsilon)
		{
			why = "Transform must be a rigid rotation plus translation (no scale or mirror).";
			return false;
		}
		return true;
	}

	static bool TransformsAlmostEqual(Xform a, Xform b) =>
		(a.Origin - b.Origin).Length <= PositionEpsilon
		&& Vec3.Dot(a.X, b.X) > 1f - RigidEpsilon
		&& Vec3.Dot(a.Y, b.Y) > 1f - RigidEpsilon
		&& Vec3.Dot(a.Z, b.Z) > 1f - RigidEpsilon;

	// ---- Solve (pure preview) ----

	/// <summary>Solve a face mate without moving anything.
	/// AlignFaces(Ta,Fa,Fb,opposed,offset) = Ta * Fa * Trans(0,0,offset) *
	/// (opposed ? RotX(PI) : Identity) * Fb^-1. A is the anchor and never moves;
	/// solving twice is bit-identical. isGrounded defaults to the document's
	/// grounding records.</summary>
	public static FaceMateResult Solve(AssemblyDocument doc, AssemblySourceResolver source,
		AssemblyMateFace faceA, AssemblyMateFace faceB, string alignment, float offsetValue, string offsetUnit,
		Func<AssemblyOccurrence, bool> isGrounded = null, string jointId = null)
	{
		FaceMateResult Fail(string code, string message) => new()
		{
			Ok = false,
			Diagnostics = new[] { new FaceMateDiagnostic(code, message, jointId ?? "") },
		};

		var normalizedAlignment = AssemblyMate.NormalizeAlignment(alignment);
		if (normalizedAlignment == null)
			return Fail("invalid-alignment", $"Alignment '{alignment}' is not recognised. Use 'opposed' or 'same'.");
		if (!float.IsFinite(offsetValue))
			return Fail("invalid-offset", "The offset must be a finite number.");
		var normalizedUnit = AssemblyMate.NormalizeUnit(offsetUnit);
		if (normalizedUnit is not ("in" or "mm" or "cm"))
			return Fail("invalid-unit", $"Unit '{offsetUnit}' is not recognised. Use 'in', 'mm' or 'cm'.");
		if (!AssemblyMate.TryToInches(offsetValue, normalizedUnit, out var offsetInches))
			return Fail("invalid-offset", "The offset must be a finite number.");
		if (doc == null)
			return Fail("missing-document", "No assembly document is open.");
		if (faceA == null || faceB == null)
			return Fail("invalid-face-reference", "A face mate needs a face on each part.");
		if (faceA.OccurrenceId == faceB.OccurrenceId)
			return Fail("self-mate", "Both faces are on the same part. A face mate joins two different parts.");

		var occurrenceA = doc.Occurrences.FirstOrDefault(o => o != null && o.Id == faceA.OccurrenceId);
		if (occurrenceA == null)
			return Fail("missing-occurrence", $"Occurrence '{faceA.OccurrenceId}' is not in this assembly.");
		var occurrenceB = doc.Occurrences.FirstOrDefault(o => o != null && o.Id == faceB.OccurrenceId);
		if (occurrenceB == null)
			return Fail("missing-occurrence", $"Occurrence '{faceB.OccurrenceId}' is not in this assembly.");

		if (!TryResolveFaceFrame(doc, source, faceA, out var localFrameA, out var codeA, out var whyA))
			return Fail(codeA, $"Side A: {whyA}");
		if (!TryResolveFaceFrame(doc, source, faceB, out var localFrameB, out var codeB, out var whyB))
			return Fail(codeB, $"Side B: {whyB}");

		if (!occurrenceA.Transform.IsValid(out _) || !IsRigid(occurrenceA.Transform.ToXform(), out _))
			return Fail("anchor-not-rigid", $"'{occurrenceA.Name}' does not sit on a rigid transform.");
		if (!occurrenceB.Transform.IsValid(out _) || !IsRigid(occurrenceB.Transform.ToXform(), out _))
			return Fail("moving-not-rigid", $"'{occurrenceB.Name}' does not sit on a rigid transform.");
		var transformA = occurrenceA.Transform.ToXform();
		var transformB = occurrenceB.Transform.ToXform();

		var opposed = normalizedAlignment == "opposed";
		var frameB = opposed
			? new Xform(localFrameB.X * -1f, localFrameB.Y, localFrameB.Z * -1f, localFrameB.Origin)
			: localFrameB;
		var worldFrameA = transformA * localFrameA;
		var normalA = worldFrameA.Z.Normal;
		var targetFrame = new Xform(worldFrameA.X, worldFrameA.Y, worldFrameA.Z,
			worldFrameA.Origin + normalA * offsetInches);
		var newTransformB = targetFrame * frameB.Inverse;
		if (!IsRigid(newTransformB, out _) || !float.IsFinite(newTransformB.Determinant))
			return Fail("non-finite-transform", "The solved placement is not a rigid transform.");
		var worldNormalB = TransformNormal(newTransformB, localFrameB.Z);

		var grounded = isGrounded ?? (occ => AssemblyGrounding.IsGrounded(doc, occ.Id));
		var groundedA = grounded(occurrenceA);
		var groundedB = grounded(occurrenceB);
		if (groundedB)
		{
			if (groundedA)
			{
				if (TransformsAlmostEqual(newTransformB, transformB))
				{
					return BuildResult(doc, occurrenceA, occurrenceB, faceA, faceB, normalizedAlignment,
						offsetValue, normalizedUnit, offsetInches, localFrameA, frameB, newTransformB,
						normalA, worldNormalB, jointId,
						"Both parts are grounded and already sit where this mate puts them — nothing moves.");
				}
				return Fail("both-grounded",
					$"Both '{occurrenceA.Name}' and '{occurrenceB.Name}' are grounded and this mate would move " +
					"'{occurrenceB.Name}'. Unground one of them first.");
			}
			return Fail("grounded-part",
				$"'{occurrenceB.Name}' is grounded, so it cannot move. Swap A/B to anchor the grounded part, " +
				"or unground it first.");
		}

		// A second mate that would move B somewhere else is a conflict; an identical
		// one agrees. A non-face fixed joint touching B already pins it. An unreadable
		// face-mate record is skipped, like Mobile — it never blocks a new mate.
		foreach (var other in doc.Joints)
		{
			if (other == null || other.Id == jointId || other.Kind != "fixed") continue;
			if (other.OccurrenceA != occurrenceB.Id && other.OccurrenceB != occurrenceB.Id) continue;
			if (!TryReadStored(other, out var otherFaceA, out var otherFaceB,
				out var otherAlignment, out var otherOffset, out var otherUnit, out _))
			{
				if (other.ExtensionData != null && other.ExtensionData.ContainsKey(AssemblyMate.FaceExtensionKey))
					continue;
				return Fail("fixed-joint-conflict",
					$"'{occurrenceB.Name}' already has a fixed joint. Edit that joint instead of adding a mate.");
			}
			if (otherFaceB.OccurrenceId != occurrenceB.Id)
			{
				return Fail("conflicting-mates",
					$"This mate moves '{occurrenceB.Name}', but face-mate joint " +
					$"'{AssemblyJointService.JointDisplayName(doc, other)}' uses it as its anchor.");
			}
			var otherOccA = doc.Occurrences.FirstOrDefault(o => o != null && o.Id == otherFaceA.OccurrenceId);
			if (otherOccA == null || !otherOccA.Transform.IsValid(out _)
				|| !other.FrameA.IsValid(out _) || !other.FrameB.IsValid(out _)
				|| !AssemblyMate.TryToInches(otherOffset, otherUnit, out var otherOffsetInches))
				continue;
			var otherWorldA = otherOccA.Transform.ToXform() * other.FrameA.ToXform();
			var otherTarget = new Xform(otherWorldA.X, otherWorldA.Y, otherWorldA.Z,
				otherWorldA.Origin + otherWorldA.Z.Normal * otherOffsetInches);
			var otherImpliedB = otherTarget * other.FrameB.ToXform().Inverse;
			if (!TransformsAlmostEqual(otherImpliedB, newTransformB))
			{
				return Fail("conflicting-mates",
					$"This mate and face-mate joint '{AssemblyJointService.JointDisplayName(doc, other)}' " +
					$"disagree about where '{occurrenceB.Name}' should go.");
			}
		}

		var facing = opposed ? "facing each other" : "facing the same way";
		var summary = $"{occurrenceA.Name} and {occurrenceB.Name}: faces {facing}, " +
			$"{offsetValue:0.###} {normalizedUnit} apart. {occurrenceB.Name} moves; {occurrenceA.Name} stays put.";
		return BuildResult(doc, occurrenceA, occurrenceB, faceA, faceB, normalizedAlignment,
			offsetValue, normalizedUnit, offsetInches, localFrameA, frameB, newTransformB,
			normalA, worldNormalB, jointId, summary);
	}

	static FaceMateResult BuildResult(AssemblyDocument doc,
		AssemblyOccurrence occurrenceA, AssemblyOccurrence occurrenceB,
		AssemblyMateFace faceA, AssemblyMateFace faceB, string alignment,
		float offsetValue, string unit, float offsetInches,
		Xform localFrameA, Xform frameB, Xform newTransformB,
		Vec3 normalA, Vec3 worldNormalB, string jointId, string summary)
	{
		var joint = BuildJoint(occurrenceA, occurrenceB, faceA, faceB, alignment,
			offsetValue, unit, offsetInches, localFrameA, frameB, jointId);
		return new FaceMateResult
		{
			Ok = true,
			NewTransformB = newTransformB,
			Joint = joint,
			Summary = summary,
			SeparationInches = offsetInches,
			WorldNormalA = normalA,
			WorldNormalB = worldNormalB,
		};
	}

	static AssemblyJoint BuildJoint(
		AssemblyOccurrence occurrenceA, AssemblyOccurrence occurrenceB,
		AssemblyMateFace faceA, AssemblyMateFace faceB, string alignment,
		float offsetValue, string unit, float offsetInches,
		Xform localFrameA, Xform frameB, string jointId)
	{
		var payload = new JsonObject
		{
			["version"] = AssemblyMate.FaceRecordVersion,
			["alignment"] = alignment,
			["offset"] = offsetValue,
			["offsetUnit"] = unit,
			["offsetInches"] = offsetInches,
			["faceA"] = faceA.ToJsonObject(),
			["faceB"] = faceB.ToJsonObject(),
			["meshFallback"] = true,
			["createdFrom"] = "face-mate",
		};
		var joint = new AssemblyJoint
		{
			Id = string.IsNullOrWhiteSpace(jointId) ? AssemblyDocument.NewId() : jointId,
			Kind = "fixed",
			OccurrenceA = faceA.OccurrenceId,
			OccurrenceB = faceB.OccurrenceId,
			FrameA = AssemblyTransform.FromXform(localFrameA),
			FrameB = AssemblyTransform.FromXform(frameB),
			ExtensionData = new Dictionary<string, JsonElement>
			{
				["name"] = JsonSerializer.SerializeToElement($"Face mate: {occurrenceA.Name} / {occurrenceB.Name}"),
				[AssemblyMate.FaceExtensionKey] = JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone(),
			},
		};
		return joint;
	}

	// ---- Commit ----

	/// <summary>Commit a solved mate: the joint plus B's move in ONE history entry,
	/// so undo removes the joint and restores B exactly, and redo restores both.
	/// A refused result changes nothing. Re-applying the same joint id edits it in
	/// place — no duplicate joint.</summary>
	public static bool ApplyToDocument(AssemblyDocument doc, EditHistory history, FaceMateResult result)
	{
		if (doc == null || result == null || !result.Ok || result.Joint == null || result.NewTransformB == null)
			return false;
		var joint = result.Joint;
		var occurrenceB = doc.Occurrences.FirstOrDefault(o => o != null && o.Id == joint.OccurrenceB);
		if (occurrenceB == null) return false;
		var target = result.NewTransformB.Value;
		AssemblyHistory.ApplyWithHistory(doc, history, $"Apply face mate '{AssemblyJointService.GetJointName(joint)}'", () =>
		{
			var existing = doc.Joints.FirstOrDefault(j => j != null && j.Id == joint.Id);
			if (existing != null) doc.Joints.Remove(existing);
			doc.Joints.Add(joint);
			occurrenceB.Transform = AssemblyTransform.FromXform(target);
		});
		return true;
	}

	// ---- Stored records (the kernel's AssemblyMate reader) ----

	/// <summary>Restore the stored mate record from a joint's "faceMate" payload,
	/// through the kernel's AssemblyMate.TryRead.</summary>
	public static bool TryReadStored(AssemblyJoint joint,
		out AssemblyMateFace faceA, out AssemblyMateFace faceB,
		out string alignment, out float offsetValue, out string offsetUnit, out string why)
	{
		faceA = null;
		faceB = null;
		alignment = null;
		offsetValue = 0f;
		offsetUnit = "in";
		why = null;
		var mate = AssemblyMate.TryRead(joint, out var refusal);
		if (mate == null)
		{
			why = refusal != null ? refusal.Message : "This joint has no face-mate record.";
			return false;
		}
		if (mate.Kind != AssemblyMate.FaceKind || mate.FaceA == null || mate.FaceB == null)
		{
			why = "This joint is not a face mate.";
			return false;
		}
		faceA = mate.FaceA;
		faceB = mate.FaceB;
		alignment = mate.Alignment;
		offsetValue = mate.Offset;
		offsetUnit = mate.OffsetUnit;
		return true;
	}

	/// <summary>Validate a stored face-mate joint against the live document: the
	/// record parses, both occurrences exist, both faces still resolve. A stored
	/// face that no longer exists reports face-invalid so it can be reselected.</summary>
	public static FaceMateDiagnostic[] ValidateStored(AssemblyDocument doc, AssemblySourceResolver source, AssemblyJoint joint)
	{
		var jointId = joint?.Id ?? "";
		if (!TryReadStored(joint, out var faceA, out var faceB, out _, out _, out _, out var why))
			return new[] { new FaceMateDiagnostic("invalid-record", why, jointId) };
		if (doc == null)
			return new[] { new FaceMateDiagnostic("missing-document", "No assembly document is open.", jointId) };
		var issues = new List<FaceMateDiagnostic>();
		foreach (var (face, side) in new[] { (faceA, "A"), (faceB, "B") })
		{
			if (doc.Occurrences.All(o => o == null || o.Id != face.OccurrenceId))
			{
				issues.Add(new FaceMateDiagnostic("missing-occurrence",
					$"Side {side}: occurrence '{face.OccurrenceId}' is not in this assembly.", jointId));
				continue;
			}
			if (!TryResolveFaceFrame(doc, source, face, out _, out var code, out var faceWhy))
				issues.Add(new FaceMateDiagnostic(code, $"Side {side}: {faceWhy}", jointId));
		}
		return issues.ToArray();
	}
}

/// <summary>A face-mate editing session: preview a candidate, change the alignment
/// or offset, swap sides to anchor the grounded part, then apply once or cancel.
/// Preview and cancel never mutate the document; Apply commits through
/// AssemblyFaceMate.ApplyToDocument in one history entry. Applying with a stored
/// joint id edits that joint in place — no duplicate.</summary>
public sealed class AssemblyFaceMateSession
{
	public AssemblyDocument Document { get; }
	public AssemblySourceResolver Source { get; }
	public AssemblyMateFace FaceA { get; private set; }
	public AssemblyMateFace FaceB { get; private set; }
	public Func<AssemblyOccurrence, bool> IsGrounded { get; }
	public EditHistory History { get; }
	public string JointId { get; }

	public string Alignment { get; private set; } = "opposed";
	public float OffsetValue { get; private set; }
	public string OffsetUnit { get; private set; } = "in";

	public FaceMateResult Result { get; private set; }
	public bool Applied { get; private set; }
	public bool Cancelled { get; private set; }

	public bool CanApply => !Applied && !Cancelled && Result != null && Result.Ok;
	public Xform? PreviewTransform => Result?.NewTransformB;
	public string PreviewOccurrenceId => FaceB?.OccurrenceId ?? "";
	public string StatusText => Applied
		? "Applied."
		: Cancelled
			? "Cancelled."
			: Result == null
				? "No preview."
				: Result.Ok
					? Result.Summary
					: Result.Diagnostics.FirstOrDefault()?.Message ?? "No preview.";

	public AssemblyFaceMateSession(AssemblyDocument doc, AssemblySourceResolver source,
		AssemblyMateFace faceA, AssemblyMateFace faceB,
		Func<AssemblyOccurrence, bool> isGrounded = null, EditHistory history = null, string jointId = null)
	{
		Document = doc;
		Source = source;
		FaceA = faceA;
		FaceB = faceB;
		IsGrounded = isGrounded;
		History = history;
		JointId = string.IsNullOrWhiteSpace(jointId) ? AssemblyDocument.NewId() : jointId;
		Refresh();
	}

	public void Refresh() =>
		Result = AssemblyFaceMate.Solve(Document, Source, FaceA, FaceB, Alignment, OffsetValue, OffsetUnit, IsGrounded, JointId);

	public void Update(string alignment, float offsetValue, string offsetUnit)
	{
		Alignment = alignment;
		OffsetValue = offsetValue;
		OffsetUnit = offsetUnit;
		Refresh();
	}

	/// <summary>Anchor the other side instead: the grounded part becomes A and
	/// stops moving. The mate is re-solved from the swapped faces.</summary>
	public void SwapSides()
	{
		(FaceA, FaceB) = (FaceB, FaceA);
		Refresh();
	}

	public bool Apply()
	{
		if (!CanApply) return false;
		if (!AssemblyFaceMate.ApplyToDocument(Document, History, Result)) return false;
		Applied = true;
		return true;
	}

	/// <summary>Drop the preview. The document is untouched, Apply() stops working.</summary>
	public void Cancel()
	{
		if (Applied) return;
		Cancelled = true;
	}
}

// ---- Axis mates ----

/// <summary>Factories and geometry helpers for the kernel's AssemblyMateAxisSource
/// (Mobile's AxisConnectorSource factories, which the kernel record type does not
/// carry). A straight edge is (occurrenceId, vertexA, vertexB); a circular source
/// is (occurrenceId, ring vertex indices) — the axis runs through the ring centre,
/// normal to the ring plane, radius the mean ring radius. A mesh pick is never an
/// axis: UnsupportedMesh builds the record TryValidate refuses.</summary>
public static class AssemblyMateAxisSources
{
	public static AssemblyMateAxisSource FromStraightEdge(string occurrenceId, string geometryRef, Vec3 anchor, Vec3 direction) =>
		Build(occurrenceId, geometryRef, AssemblyMateAxisKind.StraightEdge, null, anchor, direction);

	public static AssemblyMateAxisSource FromCircularEdge(string occurrenceId, string geometryRef, Vec3 anchor, Vec3 direction, float radius) =>
		Build(occurrenceId, geometryRef, AssemblyMateAxisKind.CircularEdge, radius, anchor, direction);

	public static AssemblyMateAxisSource FromCylindricalFace(string occurrenceId, string geometryRef, Vec3 anchor, Vec3 direction, float radius) =>
		Build(occurrenceId, geometryRef, AssemblyMateAxisKind.CylindricalFace, radius, anchor, direction);

	public static AssemblyMateAxisSource FromExplicitAxis(string occurrenceId, string geometryRef, Vec3 anchor, Vec3 direction) =>
		Build(occurrenceId, geometryRef, AssemblyMateAxisKind.ExplicitAxis, null, anchor, direction);

	public static AssemblyMateAxisSource UnsupportedMesh(string occurrenceId, string geometryRef) =>
		new()
		{
			OccurrenceId = occurrenceId ?? "",
			GeometryRef = geometryRef ?? "",
			Kind = AssemblyMateAxisKind.UnsupportedMesh,
			Fingerprint = "unsupported",
		};

	static AssemblyMateAxisSource Build(string occurrenceId, string geometryRef, AssemblyMateAxisKind kind, float? radius, Vec3 anchor, Vec3 direction)
	{
		var source = new AssemblyMateAxisSource
		{
			OccurrenceId = occurrenceId ?? "",
			GeometryRef = geometryRef ?? "",
			Kind = kind,
			Radius = radius,
			Anchor = anchor,
			Direction = direction,
		};
		source.Fingerprint = source.ComputeFingerprint();
		return source;
	}

	public static AssemblyMateAxisSource Copy(AssemblyMateAxisSource source) => source == null ? null : new AssemblyMateAxisSource
	{
		OccurrenceId = source.OccurrenceId,
		GeometryRef = source.GeometryRef,
		Kind = source.Kind,
		Radius = source.Radius,
		Anchor = source.Anchor,
		Direction = source.Direction,
		Fingerprint = source.Fingerprint,
	};

	/// <summary>True when the stored source no longer matches the live geometry it
	/// was built from (kind, reference or fingerprint changed).</summary>
	public static bool IsStaleAgainst(AssemblyMateAxisSource stored, AssemblyMateAxisSource live) =>
		stored == null || live == null
		|| stored.Kind != live.Kind || stored.GeometryRef != live.GeometryRef
		|| stored.ComputeFingerprint() != live.ComputeFingerprint();

	public static string Describe(AssemblyMateAxisSource source)
	{
		if (source == null) return "no connector";
		return source.Kind switch
		{
			AssemblyMateAxisKind.StraightEdge => $"straight edge {source.GeometryRef}",
			AssemblyMateAxisKind.CircularEdge => $"circular edge {source.GeometryRef} (r {(source.Radius.HasValue ? source.Radius.Value.ToString("0.###", CultureInfo.InvariantCulture) : "?")} in)",
			AssemblyMateAxisKind.CylindricalFace => $"cylindrical face {source.GeometryRef} (r {(source.Radius.HasValue ? source.Radius.Value.ToString("0.###", CultureInfo.InvariantCulture) : "?")} in)",
			AssemblyMateAxisKind.ExplicitAxis => $"explicit axis {source.GeometryRef}",
			_ => $"unsupported geometry {source.GeometryRef}",
		};
	}

	/// <summary>Local frame for a connector: +Z along the axis, origin on the axis
	/// at the anchor. Null when the connector is not usable.</summary>
	public static Xform? TryToFrame(AssemblyMateAxisSource source)
	{
		if (source == null) return null;
		if (!source.TryToFrame(out var frame, out _) || frame == null) return null;
		return frame.ToXform();
	}

	public static bool TryFromEdge(AssemblyDocument doc, AssemblySourceResolver source,
		string occurrenceId, int vertexA, int vertexB, out AssemblyMateAxisSource connector, out string why)
	{
		connector = null;
		why = null;
		var mesh = ResolveMesh(doc, source, occurrenceId, out why);
		if (mesh == null) return false;
		if (vertexA < 0 || vertexA >= mesh.Positions.Count || vertexB < 0 || vertexB >= mesh.Positions.Count)
		{
			why = $"Edge vertices {vertexA}/{vertexB} do not exist on '{occurrenceId}'.";
			return false;
		}
		var a = mesh.Positions[vertexA];
		var b = mesh.Positions[vertexB];
		if ((b - a).LengthSquared < 1e-12f)
		{
			why = "The edge has zero length and defines no axis.";
			return false;
		}
		connector = FromStraightEdge(occurrenceId, $"edge:{vertexA}-{vertexB}", a, b - a);
		return true;
	}

	public static bool TryFromRing(AssemblyDocument doc, AssemblySourceResolver source,
		string occurrenceId, int[] ringVertexIndices, out AssemblyMateAxisSource connector, out string why)
	{
		connector = null;
		why = null;
		var mesh = ResolveMesh(doc, source, occurrenceId, out why);
		if (mesh == null) return false;
		if (ringVertexIndices == null || ringVertexIndices.Length < 3)
		{
			why = "A circular source needs at least three ring vertices.";
			return false;
		}
		foreach (var index in ringVertexIndices)
		{
			if (index < 0 || index >= mesh.Positions.Count)
			{
				why = $"Ring vertex {index} does not exist on '{occurrenceId}'.";
				return false;
			}
		}
		var centre = Vec3.Zero;
		foreach (var index in ringVertexIndices) centre += mesh.Positions[index];
		centre = centre * (1f / ringVertexIndices.Length);
		var radius = 0f;
		foreach (var index in ringVertexIndices) radius += (mesh.Positions[index] - centre).Length;
		radius /= ringVertexIndices.Length;
		var axis = Vec3.Zero;
		for (var i = 0; i < ringVertexIndices.Length; i++)
		{
			var p0 = mesh.Positions[ringVertexIndices[i]];
			var p1 = mesh.Positions[ringVertexIndices[(i + 1) % ringVertexIndices.Length]];
			axis += Vec3.Cross(p0 - centre, p1 - centre);
		}
		if (axis.LengthSquared < 1e-12f || radius <= 0f)
		{
			why = "The ring is degenerate and defines no axis.";
			return false;
		}
		connector = FromCircularEdge(occurrenceId, $"edge:{ringVertexIndices[0]}",
			centre, axis.Normal, radius);
		return true;
	}

	static PolyMesh ResolveMesh(AssemblyDocument doc, AssemblySourceResolver source,
		string occurrenceId, out string why)
	{
		why = null;
		if (doc == null) { why = "No assembly document is open."; return null; }
		var occurrence = doc.Occurrences.FirstOrDefault(o => o != null && o.Id == occurrenceId);
		if (occurrence == null) { why = $"No occurrence '{occurrenceId}' is in this assembly."; return null; }
		var definition = doc.Definitions.FirstOrDefault(d => d != null && d.Id == occurrence.DefinitionId);
		if (definition == null) { why = $"Occurrence '{occurrence.Name}' has no part definition."; return null; }
		var resolved = source?.Invoke(definition);
		if (resolved == null || resolved.Value.Mesh == null)
		{
			why = $"The source mesh for '{occurrence.Name}' is missing.";
			return null;
		}
		return resolved.Value.Mesh;
	}
}

/// <summary>What an axis-mate solve did: the angle it corrected, the offset it
/// applied, the two radii, and whether B's spin about the axis was preserved.</summary>
public sealed record AxisMateDiagnostics(
	float AngleCorrectedDegrees,
	float AxialOffsetApplied,
	float? RadiusA,
	float? RadiusB,
	bool SpinPreserved,
	string AnchorOccurrenceId);

/// <summary>An axis (concentric) mate: two analytic axes are made coaxial, with B
/// free to spin about the shared axis and positioned along it by AxialOffset
/// (inches from A's frame origin along A's axis; positive follows A's axis).
/// Port of Mobile's AxisMate. The stored joint is Kind "hinge" with the payload
/// under "axisMate", read back through the kernel's AssemblyMate.TryRead.</summary>
public sealed class AxisMate
{
	public string Id { get; set; } = AssemblyDocument.NewId();
	public string Name { get; set; } = "";
	public string OccurrenceA { get; set; } = "";
	public string OccurrenceB { get; set; } = "";
	public AssemblyMateAxisSource SourceA { get; set; }
	public AssemblyMateAxisSource SourceB { get; set; }
	public float AxialOffset { get; set; }
	public bool Flipped { get; set; }

	public static bool IsStableId(string id) =>
		!string.IsNullOrWhiteSpace(id) && Guid.TryParseExact(id, "N", out _);

	public AxisMate Clone() => new()
	{
		Id = Id,
		Name = Name,
		OccurrenceA = OccurrenceA,
		OccurrenceB = OccurrenceB,
		SourceA = AssemblyMateAxisSources.Copy(SourceA),
		SourceB = AssemblyMateAxisSources.Copy(SourceB),
		AxialOffset = AxialOffset,
		Flipped = Flipped,
	};

	/// <summary>"code: message" problems, empty when the mate is well-formed.</summary>
	public List<string> Validate(AssemblyDocument doc)
	{
		var errors = new List<string>();
		if (!IsStableId(Id))
			errors.Add("invalid-id: the mate needs a stable ID.");
		if (string.IsNullOrWhiteSpace(OccurrenceA) || string.IsNullOrWhiteSpace(OccurrenceB))
		{
			errors.Add("invalid-reference: the mate needs two occurrences.");
		}
		else
		{
			if (doc == null)
			{
				errors.Add("missing-document: no assembly document is open.");
			}
			else
			{
				if (doc.Occurrences.All(o => o == null || o.Id != OccurrenceA))
					errors.Add($"missing-occurrence: '{OccurrenceA}' is not in this assembly.");
				if (doc.Occurrences.All(o => o == null || o.Id != OccurrenceB))
					errors.Add($"missing-occurrence: '{OccurrenceB}' is not in this assembly.");
			}
			if (OccurrenceA == OccurrenceB)
				errors.Add("self-joint: both sides of the mate are the same part.");
		}
		if (!float.IsFinite(AxialOffset))
			errors.Add("invalid-offset: the axial offset must be finite.");
		if (SourceA == null || SourceB == null)
		{
			errors.Add("invalid-reference: the mate needs a connector on each part.");
		}
		else
		{
			if (SourceA.OccurrenceId != OccurrenceA)
				errors.Add("invalid-reference: connector A is not on occurrence A.");
			if (SourceB.OccurrenceId != OccurrenceB)
				errors.Add("invalid-reference: connector B is not on occurrence B.");
			if (!SourceA.TryValidate(out var errorA)) errors.Add(errorA);
			if (!SourceB.TryValidate(out var errorB)) errors.Add(errorB);
		}
		return errors;
	}

	/// <summary>B's current distance along A's axis from A's frame origin, in inches.</summary>
	public float CurrentAxialOffset(AssemblyDocument doc)
	{
		if (!AxisMateSolver.TryWorldFrames(doc, this, out var worldA, out var worldB, out _, out _, out _))
			return float.NaN;
		return Vec3.Dot(worldB.Origin - worldA.Origin, worldA.Z.Normal);
	}

	public bool IsStaleAgainst(AssemblyMateAxisSource liveA, AssemblyMateAxisSource liveB) =>
		AssemblyMateAxisSources.IsStaleAgainst(SourceA, liveA)
		|| AssemblyMateAxisSources.IsStaleAgainst(SourceB, liveB);

	public AssemblyJoint ToJoint()
	{
		if (!SourceA.TryToFrame(out var frameA, out var errorA) || frameA == null)
			throw new InvalidOperationException("Connector A does not define a usable axis." + (errorA != null ? " " + errorA : ""));
		if (!SourceB.TryToFrame(out var frameB, out var errorB) || frameB == null)
			throw new InvalidOperationException("Connector B does not define a usable axis." + (errorB != null ? " " + errorB : ""));
		var payload = new JsonObject
		{
			["kind"] = AssemblyMate.AxisRecordKind,
			["name"] = Name ?? "",
			["offset"] = AxialOffset,
			["flipped"] = Flipped,
			["sourceA"] = SourceA.ToJsonObject(),
			["sourceB"] = SourceB.ToJsonObject(),
		};
		return new AssemblyJoint
		{
			Id = Id,
			Kind = "hinge",
			OccurrenceA = OccurrenceA,
			OccurrenceB = OccurrenceB,
			FrameA = frameA,
			FrameB = frameB,
			ExtensionData = new Dictionary<string, JsonElement>
			{
				["name"] = JsonSerializer.SerializeToElement(Name ?? ""),
				[AssemblyMate.AxisExtensionKey] = JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone(),
			},
		};
	}

	public static AxisMate FromJoint(AssemblyJoint joint, out string error)
	{
		error = null;
		var mate = AssemblyMate.TryRead(joint, out var refusal);
		if (mate == null)
		{
			error = refusal != null ? refusal.Message : "This joint has no axis-mate record.";
			return null;
		}
		if (mate.Kind != AssemblyMate.AxisKind || mate.SourceA == null || mate.SourceB == null)
		{
			error = "This joint is not an axis mate.";
			return null;
		}
		return new AxisMate
		{
			Id = joint.Id,
			Name = mate.Name,
			OccurrenceA = joint.OccurrenceA,
			OccurrenceB = joint.OccurrenceB,
			SourceA = mate.SourceA,
			SourceB = mate.SourceB,
			AxialOffset = mate.AxialOffset,
			Flipped = mate.Flipped,
		};
	}

	/// <summary>Commit the mate: solve, refuse conflicts and a grounded B, then
	/// store/replace the hinge joint by Id and move B in ONE history entry.
	/// Re-applying with a new offset edits the stored joint in place (still one
	/// joint, B at the new offset). Desktop closes Mobile's gap: a grounded B is
	/// refused — nothing moves, nothing is committed.</summary>
	public bool ApplyTo(AssemblyDocument doc, EditHistory history,
		out Xform solvedB, out AxisMateDiagnostics diag, out string error)
	{
		solvedB = default;
		diag = null;
		error = null;
		if (doc == null)
		{
			error = "missing-document: no assembly document is open.";
			return false;
		}
		if (AssemblyGrounding.IsGrounded(doc, OccurrenceB))
		{
			var name = doc.Occurrences.FirstOrDefault(o => o != null && o.Id == OccurrenceB)?.Name ?? OccurrenceB;
			error = $"grounded-part: '{name}' is grounded, so it cannot move. Unground it first.";
			return false;
		}
		if (!AxisMateSolver.TrySolve(doc, this, out solvedB, out diag, out error))
			return false;
		if (AxisMateSolver.FindConflictingMate(doc, this, out error))
			return false;
		var occurrenceB = doc.Occurrences.FirstOrDefault(o => o != null && o.Id == OccurrenceB);
		if (occurrenceB == null)
		{
			error = $"missing-occurrence: '{OccurrenceB}' is not in this assembly.";
			return false;
		}
		AssemblyJoint joint;
		try
		{
			joint = ToJoint();
		}
		catch (InvalidOperationException ex)
		{
			error = ex.Message;
			return false;
		}
		var target = solvedB;
		AssemblyHistory.ApplyWithHistory(doc, history, $"Apply axis mate '{Name}'", () =>
		{
			var existing = doc.Joints.FirstOrDefault(j => j != null && j.Id == Id);
			if (existing != null) doc.Joints.Remove(existing);
			doc.Joints.Add(joint);
			occurrenceB.Transform = AssemblyTransform.FromXform(target);
		});
		return true;
	}
}

/// <summary>Pure axis-mate solving. TrySolve reports B's target transform and moves
/// nothing; AxisMate.ApplyTo commits. Math: AlignAxis(Ta,Fa,Fb,offset) =
/// Ta * Fa * Trans(0,0,offset) * Fb^-1; rotation about the axis stays free, so
/// B's spin is preserved.</summary>
public static class AxisMateSolver
{
	public static bool TryWorldFrames(AssemblyDocument assembly, AxisMate mate,
		out Xform worldA, out Xform worldB, out Xform frameA, out Xform frameB, out string error)
	{
		worldA = default;
		worldB = default;
		frameA = default;
		frameB = default;
		error = null;
		if (mate == null)
		{
			error = "invalid-reference: no mate was given.";
			return false;
		}
		var problems = mate.Validate(assembly);
		if (problems.Count > 0)
		{
			error = problems[0];
			return false;
		}
		var occA = assembly.Occurrences.First(o => o.Id == mate.OccurrenceA);
		var occB = assembly.Occurrences.First(o => o.Id == mate.OccurrenceB);
		if (!occA.Transform.IsValid(out _) || !occB.Transform.IsValid(out _))
		{
			error = "invalid-transform: an occurrence transform is malformed.";
			return false;
		}
		var frameAOpt = AssemblyMateAxisSources.TryToFrame(mate.SourceA);
		if (frameAOpt == null)
		{
			error = $"invalid-axis: connector A ({AssemblyMateAxisSources.Describe(mate.SourceA)}) does not define a usable axis.";
			return false;
		}
		var frameBOpt = AssemblyMateAxisSources.TryToFrame(mate.SourceB);
		if (frameBOpt == null)
		{
			error = $"invalid-axis: connector B ({AssemblyMateAxisSources.Describe(mate.SourceB)}) does not define a usable axis.";
			return false;
		}
		frameA = frameAOpt.Value;
		frameB = frameBOpt.Value;
		worldA = occA.Transform.ToXform() * frameA;
		worldB = occB.Transform.ToXform() * frameB;
		return true;
	}

	public static bool TrySolve(AssemblyDocument assembly, AxisMate mate,
		out Xform newTransformB, out AxisMateDiagnostics diag, out string error)
	{
		newTransformB = default;
		diag = null;
		error = null;
		if (!TryWorldFrames(assembly, mate, out var worldA, out var worldB, out _, out var frameB, out error))
			return false;
		var axisA = worldA.Z.Normal;
		var axisB = worldB.Z.Normal;
		if (axisA.LengthSquared < 1e-12f || axisB.LengthSquared < 1e-12f)
		{
			error = "invalid-axis: a connector axis is degenerate in assembly space.";
			return false;
		}
		var targetAxis = mate.Flipped ? axisA * -1f : axisA;
		var dot = Math.Clamp(Vec3.Dot(axisB, targetAxis), -1f, 1f);
		Xform rotation;
		float angle;
		if (dot > 1f - 1e-6f)
		{
			rotation = Xform.Identity;
			angle = 0f;
		}
		else if (dot < -1f + 1e-6f)
		{
			var reference = MathF.Abs(targetAxis.x) < 0.9f ? new Vec3(1f, 0f, 0f) : new Vec3(0f, 1f, 0f);
			var perp = Vec3.Cross(targetAxis, reference).Normal;
			rotation = Xform.Rotate(perp, MathF.PI);
			angle = MathF.PI;
		}
		else
		{
			var axis = Vec3.Cross(axisB, targetAxis).Normal;
			angle = MathF.Acos(dot);
			rotation = Xform.Rotate(axis, angle);
		}
		var targetOrigin = worldA.Origin + axisA * mate.AxialOffset;
		var worldBNew = Xform.Translate(targetOrigin)
			* rotation
			* Xform.Translate(worldB.Origin * -1f)
			* worldB;
		newTransformB = worldBNew * frameB.Inverse;
		var offLine = Vec3.Cross(worldBNew.Origin - worldA.Origin, axisA).Length;
		var checkDot = Vec3.Dot(worldBNew.Z.Normal, targetAxis);
		if (offLine > 1e-3f || MathF.Abs(checkDot - 1f) > 1e-4f)
		{
			error = "invalid-axis: the solved mate is not coaxial; the mate was not applied.";
			return false;
		}
		diag = new AxisMateDiagnostics(angle * 180f / MathF.PI, mate.AxialOffset,
			mate.SourceA.Radius, mate.SourceB.Radius, true, mate.OccurrenceA);
		return true;
	}

	public static bool TryTargetOriginForB(AssemblyDocument assembly, AxisMate mate, out Vec3 origin, out string error)
	{
		origin = Vec3.Zero;
		error = null;
		if (!TryWorldFrames(assembly, mate, out var worldA, out _, out _, out _, out error))
			return false;
		origin = worldA.Origin + worldA.Z.Normal * mate.AxialOffset;
		return true;
	}

	/// <summary>True when another stored axis mate already pins B onto a different
	/// axis line or the same line at a different offset. Sharing the A side is fine.</summary>
	public static bool FindConflictingMate(AssemblyDocument assembly, AxisMate mate, out string error)
	{
		error = null;
		if (assembly == null || mate == null) return false;
		if (!TryWorldFrames(assembly, mate, out var worldA, out _, out _, out _, out _)) return false;
		var axisA = worldA.Z.Normal;
		var bName = assembly.Occurrences.FirstOrDefault(o => o != null && o.Id == mate.OccurrenceB)?.Name ?? mate.OccurrenceB;
		foreach (var joint in assembly.Joints)
		{
			if (joint == null || joint.Id == mate.Id || joint.Kind != "hinge") continue;
			var other = AxisMate.FromJoint(joint, out _);
			if (other == null || other.OccurrenceB != mate.OccurrenceB) continue;
			if (!TryWorldFrames(assembly, other, out var otherWorldA, out _, out _, out _, out _)) continue;
			var otherAxis = otherWorldA.Z.Normal;
			var parallel = MathF.Abs(Vec3.Dot(axisA, otherAxis)) > 1f - 1e-6f;
			var offLine = Vec3.Cross(otherWorldA.Origin - worldA.Origin, axisA).Length;
			if (!parallel || offLine > 1e-3f)
			{
				error = $"conflict: '{other.Name}' already mates '{bName}' onto a different axis. " +
					"Remove or edit that mate first.";
				return true;
			}
			var impliedThis = worldA.Origin + axisA * mate.AxialOffset;
			var impliedOther = otherWorldA.Origin + otherAxis * other.AxialOffset;
			if ((impliedThis - impliedOther).Length > 1e-3f)
			{
				error = $"conflict: '{other.Name}' already mates '{bName}' onto the same axis at a different offset. " +
					"Remove or edit that mate first.";
				return true;
			}
		}
		return false;
	}
}
