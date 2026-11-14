using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// Port assembly mate feature: Mobile stores mates as joints with a mate record in the
// joint's extension data — a face mate is a fixed joint with a faceMate record, an axis
// (concentric) mate is a hinge joint with an axisMate record. The fixtures below are
// Mobile's bytes (schemaVersion 1 assembly JSON inside a format-3 .effigy document):
// the face payload is Mobile UiAssemblyMate.BuildJoint, the axis payload is
// AxisMate.ToJoint. No build, app launch or screenshot is claimed here; the full build
// and visual acceptance belong to the final integrator, and this file is not yet wired
// into TestRegistry.cs — the integrator adds the one Register line.
internal static class AssemblyMateTests
{
	static readonly string DefA = new string('a', 32);
	static readonly string DefB = new string('b', 32);
	static readonly string OccA = new string('c', 32);
	static readonly string OccB = new string('d', 32);
	static readonly string JointFace = new string('e', 32);
	static readonly string JointAxis = new string('f', 32);

	public static void Register(TestRegistry suite, TestSettings settings)
	{
		void Check(string name, Action test) => suite.Add("assembly-mates", name, test);
		void Require(bool condition, string message = "assertion failed")
		{
			if (!condition) throw new Exception(message);
		}

		Check("mobile face mate loads with its record intact", () =>
		{
			var doc = AssemblyDocument.FromJson(FaceMateJson());
			doc.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 1, $"expected 1 mate, got {mates.Count}");
			Require(refusals.Count == 0, $"expected no refusals, got {refusals.Count}");
			var mate = mates[0];
			Require(mate.Kind == AssemblyMate.FaceKind, $"expected a face mate, got '{mate.Kind}'");
			Require(mate.Id == JointFace, "mate id must be the joint id");
			Require(mate.Alignment == "opposed", $"expected opposed alignment, got '{mate.Alignment}'");
			Require(MathF.Abs(mate.Offset - 0.5f) < 1e-6f, $"expected offset 0.5, got {mate.Offset}");
			Require(mate.OffsetUnit == "in", $"expected offset unit in, got '{mate.OffsetUnit}'");
			Require(MathF.Abs(mate.OffsetInches - 0.5f) < 1e-6f, $"expected 0.5 inches, got {mate.OffsetInches}");
			var faceA = mate.FaceA;
			Require(faceA != null, "face mate side A is missing");
			Require(faceA.OccurrenceId == OccA, "face A must name occurrence A");
			Require(faceA.Ref.BodyId == "body-a", $"face A body must be body-a, got '{faceA.Ref.BodyId}'");
			Require(faceA.Ref.Point.AlmostEquals(new Vec3(0, 0, 0)), "face A point must be the origin");
			Require(faceA.Ref.Normal.AlmostEquals(new Vec3(0, 0, 1)), "face A normal must be +Z");
			var faceB = mate.FaceB;
			Require(faceB != null, "face mate side B is missing");
			Require(faceB.OccurrenceId == OccB, "face B must name occurrence B");
			Require(faceB.Ref.BodyId == "body-b", $"face B body must be body-b, got '{faceB.Ref.BodyId}'");
			Require(faceB.Ref.Point.AlmostEquals(new Vec3(0, 0, 0.5f)), "face B point must be 0.5 along +Z");
			Require(faceB.Ref.Normal.AlmostEquals(new Vec3(0, 0, -1)), "face B normal must be -Z");
			Require(doc.Validate().Count == 0, "mobile face-mate document must validate with no issues");

			var studio = new PartStudio { Assembly = doc };
			Require(studio.Mates.Count == 1, "studio must expose the one mate");
			Require(studio.MateRefusals.Count == 0, "studio must report no mate refusals");
		});

		Check("face mate survives save and reopen", () =>
		{
			var studio = new PartStudio { Assembly = AssemblyDocument.FromJson(FaceMateJson()) };
			var back = StudioDocument.Read(StudioDocument.Write(studio));
			back.Rebuild();
			back.Assembly.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 1, $"expected 1 mate after reopen, got {mates.Count}");
			Require(refusals.Count == 0, "expected no refusals after reopen");
			Require(mates[0].Alignment == "opposed", "alignment must survive save and reopen");
			Require(MathF.Abs(mates[0].OffsetInches - 0.5f) < 1e-6f, "offset must survive save and reopen");
			var faceB = mates[0].FaceB;
			Require(faceB != null && faceB.Ref.BodyId == "body-b", "face B must survive save and reopen");

			var json = studio.Assembly.ToJson();
			Require(json.Contains("faceMate"), "saved assembly JSON must still carry the faceMate record");
			var reparsed = AssemblyDocument.FromJson(json);
			reparsed.TryReadMates(out var matesAgain, out _);
			Require(matesAgain.Count == 1 && matesAgain[0].OffsetInches == mates[0].OffsetInches,
				"assembly JSON round-trip must keep the mate");
		});

		Check("face mate record writes the same joint Mobile writes", () =>
		{
			var mate = new AssemblyMate
			{
				Kind = AssemblyMate.FaceKind, Id = JointFace, OccurrenceA = OccA, OccurrenceB = OccB,
				Alignment = "opposed", Offset = 0.5f, OffsetUnit = "in", OffsetInches = 0.5f,
				FaceA = new AssemblyMateFace { OccurrenceId = OccA, Ref = new FaceRef("body-a", new Vec3(0, 0, 0), new Vec3(0, 0, 1)) },
				FaceB = new AssemblyMateFace { OccurrenceId = OccB, Ref = new FaceRef("body-b", new Vec3(0, 0, 0.5f), new Vec3(0, 0, -1)) },
			};
			var joint = mate.ToJoint();
			Require(joint.Kind == "fixed", $"face mate must be a fixed joint, got '{joint.Kind}'");
			Require(joint.ExtensionData != null && joint.ExtensionData.ContainsKey("faceMate"), "joint must carry the faceMate record");
			Require(joint.ExtensionData["faceMate"].GetProperty("createdFrom").GetString() == "face-mate",
				"record must say it was created from a face mate");
			// Opposed flip is baked into FrameB: its X and Z are negated, keeping it right-handed.
			Require(joint.FrameB.X[0] == 0f && joint.FrameB.X[1] == -1f && joint.FrameB.X[2] == 0f,
				"opposed FrameB X must be the negated face-B frame X");
			Require(joint.FrameB.Z[2] == 1f, "opposed FrameB Z must point +Z after the flip");
			Require(MathF.Abs(joint.FrameB.Origin[2] - 0.5f) < 1e-6f, "FrameB origin must sit on face B");
			var reread = AssemblyMate.TryRead(joint, out var refusal);
			Require(refusal == null, $"written joint must read back cleanly, got {refusal?.Message}");
			Require(reread != null, "written joint must read back as a mate");
			Require(reread.Alignment == "opposed" && MathF.Abs(reread.OffsetInches - 0.5f) < 1e-6f,
				"written joint must read back with the same alignment and offset");
			var rereadB = reread.FaceB;
			Require(rereadB != null && rereadB.Ref.Normal.AlmostEquals(new Vec3(0, 0, -1)),
				"written joint must read back with face B's normal");
		});

		Check("mobile axis mate loads with its concentric record intact", () =>
		{
			var doc = AssemblyDocument.FromJson(AxisMateJson());
			doc.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 1, $"expected 1 mate, got {mates.Count}");
			Require(refusals.Count == 0, $"expected no refusals, got {refusals.Count}");
			var mate = mates[0];
			Require(mate.Kind == AssemblyMate.AxisKind, $"expected an axis mate, got '{mate.Kind}'");
			Require(mate.Name == "Shaft in bore", $"expected the mate name, got '{mate.Name}'");
			Require(MathF.Abs(mate.AxialOffset - 0.25f) < 1e-6f, $"expected axial offset 0.25, got {mate.AxialOffset}");
			Require(!mate.Flipped, "mate must not be flipped");
			var sourceA = mate.SourceA;
			Require(sourceA != null, "axis mate source A is missing");
			Require(sourceA.Kind == AssemblyMateAxisKind.StraightEdge, $"source A must be a straight edge, got {sourceA.Kind}");
			Require(sourceA.GeometryRef == "edge:12", "source A must name edge:12");
			Require(sourceA.Fingerprint == sourceA.ComputeFingerprint(), "source A fingerprint must match its geometry");
			var sourceB = mate.SourceB;
			Require(sourceB != null, "axis mate source B is missing");
			Require(sourceB.Kind == AssemblyMateAxisKind.CircularEdge, $"source B must be a circular edge, got {sourceB.Kind}");
			Require(sourceB.Radius.HasValue && MathF.Abs(sourceB.Radius.Value - 0.5f) < 1e-6f,
				"source B radius must be 0.5");
			Require(sourceB.Fingerprint == sourceB.ComputeFingerprint(), "source B fingerprint must match its geometry");
			Require(doc.Validate().Count == 0, "mobile axis-mate document must validate with no issues");
		});

		Check("axis mate survives save and reopen", () =>
		{
			var studio = new PartStudio { Assembly = AssemblyDocument.FromJson(AxisMateJson()) };
			var back = StudioDocument.Read(StudioDocument.Write(studio));
			back.Rebuild();
			back.Assembly.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 1, $"expected 1 mate after reopen, got {mates.Count}");
			Require(refusals.Count == 0, "expected no refusals after reopen");
			Require(MathF.Abs(mates[0].AxialOffset - 0.25f) < 1e-6f, "axial offset must survive save and reopen");

			var mate = AxisMateRecord();
			var joint = mate.ToJoint();
			Require(joint.Kind == "hinge", $"axis mate must be a hinge joint, got '{joint.Kind}'");
			var reread = AssemblyMate.TryRead(joint, out var refusal);
			Require(refusal == null, $"written joint must read back cleanly, got {refusal?.Message}");
			Require(reread != null, "written joint must read back as a mate");
			Require(MathF.Abs(reread.AxialOffset - 0.25f) < 1e-6f && !reread.Flipped,
				"written joint must read back with the same offset and flip");
			var rereadB = reread.SourceB;
			Require(rereadB != null && rereadB.Kind == AssemblyMateAxisKind.CircularEdge
				&& rereadB.Radius.HasValue && MathF.Abs(rereadB.Radius.Value - 0.5f) < 1e-6f,
				"written joint must read back with source B's circular edge and radius");
		});

		Check("unimplemented axis mate kind is refused by name", () =>
		{
			var doc = AssemblyDocument.FromJson(AxisMateJson().Replace("\"concentric\"", "\"coaxial\""));
			doc.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 0, "an unimplemented mate kind must not read as a mate");
			Require(refusals.Count == 1, $"expected exactly one refusal, got {refusals.Count}");
			Require(refusals[0].Code == "unsupported-mate-kind", $"expected unsupported-mate-kind, got '{refusals[0].Code}'");
			Require(refusals[0].Message.Contains("coaxial"), "refusal must name the unimplemented kind");
			Require(!refusals[0].Warning, "an unimplemented mate kind is a hard refusal, not a warning");
			Require(doc.Validate().Any(i => i.Code == "unsupported-mate-kind"),
				"validation must carry the refusal");
			Require(doc.Joints.Count == 1, "refused mate must keep its joint");
			Require(doc.Joints[0].ExtensionData != null && doc.Joints[0].ExtensionData.ContainsKey("axisMate"),
				"refused mate must keep its payload, not silently drop it");
		});

		Check("refused mate keeps its joint and payload on save", () =>
		{
			var studio = new PartStudio { Assembly = AssemblyDocument.FromJson(AxisMateJson().Replace("\"concentric\"", "\"coaxial\"")) };
			var back = StudioDocument.Read(StudioDocument.Write(studio));
			Require(back.Assembly.Joints.Count == 1, "refused mate joint must survive save and reopen");
			Require(back.Assembly.Joints[0].ExtensionData["axisMate"].GetProperty("kind").GetString() == "coaxial",
				"refused mate payload must survive save and reopen with its kind");
			Require(back.Mates.Count == 0, "refused mate must still not appear as a mate");
			Require(back.MateRefusals.Count == 1 && back.MateRefusals[0].Code == "unsupported-mate-kind",
				"refusal must survive save and reopen");
		});

		Check("face mate with unknown alignment is refused by name", () =>
		{
			var doc = AssemblyDocument.FromJson(FaceMateJson().Replace("\"opposed\"", "\"perpendicular\""));
			doc.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 0, "an unknown alignment must not read as a mate");
			Require(refusals.Count == 1 && refusals[0].Code == "unsupported-mate-kind",
				$"expected unsupported-mate-kind, got '{(refusals.Count == 0 ? "none" : refusals[0].Code)}'");
			Require(refusals[0].Message.Contains("perpendicular"), "refusal must name the alignment");
		});

		Check("face mate with unknown version is refused", () =>
		{
			var doc = AssemblyDocument.FromJson(FaceMateJson().Replace("\"version\": 1, \"alignment\"", "\"version\": 2, \"alignment\""));
			doc.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 0, "an unknown record version must not read as a mate");
			Require(refusals.Count == 1 && refusals[0].Code == "unsupported-mate-kind",
				$"expected unsupported-mate-kind, got '{(refusals.Count == 0 ? "none" : refusals[0].Code)}'");
		});

		Check("mesh-only axis source is refused, never snapped to a centroid", () =>
		{
			var doc = AssemblyDocument.FromJson(AxisMateJson().Replace("\"circularEdge\"", "\"unsupportedMesh\""));
			doc.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 0, "a mesh-only pick must not read as a mate");
			Require(refusals.Count == 1 && refusals[0].Code == "unsupported-mate-geometry",
				$"expected unsupported-mate-geometry, got '{(refusals.Count == 0 ? "none" : refusals[0].Code)}'");
		});

		Check("malformed mate payload is diagnosed, not dropped", () =>
		{
			var payload = new JsonObject
			{
				["version"] = 1,
				["alignment"] = "opposed",
				["offset"] = 0.5f,
				["offsetUnit"] = "in",
				["offsetInches"] = 0.5f,
				["faceA"] = FaceSideObject(OccA, "body-a", new Vec3(0, 0, 0), new Vec3(0, 0, 1)),
				["meshFallback"] = true,
				["createdFrom"] = "face-mate",
			};
			var joint = new AssemblyJoint
			{
				Id = JointFace, Kind = "fixed", OccurrenceA = OccA, OccurrenceB = OccB,
				FrameA = new AssemblyTransform(),
				FrameB = AssemblyTransform.FromXform(Xform.Translate(new Vec3(0, 0, 0.5f))),
				ExtensionData = new Dictionary<string, JsonElement>
				{
					["faceMate"] = JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone(),
				},
			};
			var doc = DocWithJoints(joint);
			doc.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 0, "a payload missing face B must not read as a mate");
			Require(refusals.Count == 1 && refusals[0].Code == "invalid-mate-record",
				$"expected invalid-mate-record, got '{(refusals.Count == 0 ? "none" : refusals[0].Code)}'");
			Require(doc.Joints.Count == 1, "malformed mate must keep its joint");
			Require(doc.Validate().Any(i => i.Code == "invalid-mate-record"),
				"validation must carry the malformed-record diagnosis");
		});

		Check("plain joints are not mates", () =>
		{
			var doc = DocWithJoints(
				new AssemblyJoint { Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = OccA, OccurrenceB = OccB },
				new AssemblyJoint { Id = AssemblyDocument.NewId(), Kind = "hinge", OccurrenceA = OccA, OccurrenceB = OccB });
			doc.TryReadMates(out var mates, out var refusals);
			Require(mates.Count == 0, "plain joints must not read as mates");
			Require(refusals.Count == 0, "plain joints must not produce refusals");
			Require(doc.Validate().Count == 0, "plain joints must validate cleanly");
		});

		Check("tree Mate feature loads as a feature, not as an assembly mate", () =>
		{
			var text = StudioDocument.Write(new PartStudio())
				+ "feature AssemblyMateFeature\n\tid " + AssemblyDocument.NewId() + "\n\tsuppressed 0\n\tvisible 1\nend\n";
			var studio = StudioDocument.Read(text);
			Require(studio.Features.Count == 1 && studio.Features[0] is AssemblyMateFeature,
				"the legacy centroid Mate feature must load as a tree feature");
			Require(studio.Mates.Count == 0, "a tree Mate feature must not appear in assembly Mates");
			Require(studio.MateRefusals.Count == 0, "a tree Mate feature must not produce a mate refusal");
		});

		Check("assembly mate is not a tree feature", () =>
		{
			var studio = new PartStudio { Assembly = AssemblyDocument.FromJson(FaceMateJson()) };
			Require(studio.Features.Count == 0, "an assembly mate must not add a tree feature");
			Require(studio.Mates.Count == 1, "an assembly mate must be visible on the studio");
		});
	}

	static AssemblyDocument DocWithJoints(params AssemblyJoint[] joints)
	{
		var doc = new AssemblyDocument();
		doc.Definitions.Add(new AssemblyPartDefinition { Id = DefA, Name = "Bracket", SourceKind = "studio-body", SourceReference = "body-a" });
		doc.Definitions.Add(new AssemblyPartDefinition { Id = DefB, Name = "Plate", SourceKind = "studio-body", SourceReference = "body-b" });
		doc.Occurrences.Add(new AssemblyOccurrence { Id = OccA, DefinitionId = DefA, Name = "Bracket 1", Transform = new AssemblyTransform() });
		doc.Occurrences.Add(new AssemblyOccurrence { Id = OccB, DefinitionId = DefB, Name = "Plate 1", Transform = new AssemblyTransform() });
		doc.Joints.AddRange(joints);
		return doc;
	}

	static JsonObject FaceSideObject(string occ, string body, Vec3 point, Vec3 normal) => new()
	{
		["occurrenceId"] = occ,
		["bodyId"] = body,
		["point"] = new JsonArray(point.x, point.y, point.z),
		["normal"] = new JsonArray(normal.x, normal.y, normal.z),
		["anchor"] = new JsonArray(0f, 0f, 0f),
		["anchorFromMaxX"] = false,
		["anchorFromMaxY"] = false,
		["anchored"] = false,
	};

	static AssemblyMate AxisMateRecord()
	{
		var sourceA = new AssemblyMateAxisSource
		{
			OccurrenceId = OccA, GeometryRef = "edge:12", Kind = AssemblyMateAxisKind.StraightEdge,
			Radius = null, Anchor = new Vec3(0, 0, 0), Direction = new Vec3(0, 0, 1),
		};
		sourceA.Fingerprint = sourceA.ComputeFingerprint();
		var sourceB = new AssemblyMateAxisSource
		{
			OccurrenceId = OccB, GeometryRef = "edge:34", Kind = AssemblyMateAxisKind.CircularEdge,
			Radius = 0.5f, Anchor = new Vec3(1, 0, 0), Direction = new Vec3(0, 0, 1),
		};
		sourceB.Fingerprint = sourceB.ComputeFingerprint();
		return new AssemblyMate
		{
			Kind = AssemblyMate.AxisKind, Id = JointAxis, Name = "Shaft in bore",
			OccurrenceA = OccA, OccurrenceB = OccB, AxialOffset = 0.25f, Flipped = false,
			SourceA = sourceA, SourceB = sourceB,
		};
	}

	static string FaceMateJson() => """
		{
		  "schemaVersion": 1,
		  "lengthUnit": "in",
		  "angleUnit": "rad",
		  "coordinateSystem": "effigy-z-up-right-handed",
		  "definitions": [
			{"id": "__DEF_A__", "name": "Bracket", "sourceKind": "studio-body", "sourceReference": "body-a"},
			{"id": "__DEF_B__", "name": "Plate", "sourceKind": "studio-body", "sourceReference": "body-b"}
		  ],
		  "occurrences": [
			{"id": "__OCC_A__", "definitionId": "__DEF_A__", "name": "Bracket 1", "visible": true, "transform": {"x": [1,0,0], "y": [0,1,0], "z": [0,0,1], "origin": [0,0,0]}},
			{"id": "__OCC_B__", "definitionId": "__DEF_B__", "name": "Plate 1", "visible": true, "transform": {"x": [1,0,0], "y": [0,1,0], "z": [0,0,1], "origin": [0,0,0]}}
		  ],
		  "joints": [
			{"id": "__JOINT_FACE__", "kind": "fixed", "occurrenceA": "__OCC_A__", "occurrenceB": "__OCC_B__",
			 "frameA": {"x": [0,-1,0], "y": [1,0,0], "z": [0,0,1], "origin": [0,0,0]},
			 "frameB": {"x": [0,-1,0], "y": [1,0,0], "z": [0,0,1], "origin": [0,0,0.5]},
			 "faceMate": {"version": 1, "alignment": "opposed", "offset": 0.5, "offsetUnit": "in", "offsetInches": 0.5,
			   "faceA": {"occurrenceId": "__OCC_A__", "bodyId": "body-a", "point": [0,0,0], "normal": [0,0,1], "anchor": [0,0,0], "anchorFromMaxX": false, "anchorFromMaxY": false, "anchored": false},
			   "faceB": {"occurrenceId": "__OCC_B__", "bodyId": "body-b", "point": [0,0,0.5], "normal": [0,0,-1], "anchor": [0,0,0], "anchorFromMaxX": false, "anchorFromMaxY": false, "anchored": false},
			   "meshFallback": true, "createdFrom": "face-mate"}}
		  ]
		}
		""".Replace("__DEF_A__", DefA).Replace("__DEF_B__", DefB)
		.Replace("__OCC_A__", OccA).Replace("__OCC_B__", OccB)
		.Replace("__JOINT_FACE__", JointFace);

	static string AxisMateJson() => """
		{
		  "schemaVersion": 1,
		  "lengthUnit": "in",
		  "angleUnit": "rad",
		  "coordinateSystem": "effigy-z-up-right-handed",
		  "definitions": [
			{"id": "__DEF_A__", "name": "Bracket", "sourceKind": "studio-body", "sourceReference": "body-a"},
			{"id": "__DEF_B__", "name": "Plate", "sourceKind": "studio-body", "sourceReference": "body-b"}
		  ],
		  "occurrences": [
			{"id": "__OCC_A__", "definitionId": "__DEF_A__", "name": "Bracket 1", "visible": true, "transform": {"x": [1,0,0], "y": [0,1,0], "z": [0,0,1], "origin": [0,0,0]}},
			{"id": "__OCC_B__", "definitionId": "__DEF_B__", "name": "Plate 1", "visible": true, "transform": {"x": [1,0,0], "y": [0,1,0], "z": [0,0,1], "origin": [0,0,0]}}
		  ],
		  "joints": [
			{"id": "__JOINT_AXIS__", "kind": "hinge", "occurrenceA": "__OCC_A__", "occurrenceB": "__OCC_B__",
			 "frameA": {"x": [1,0,0], "y": [0,1,0], "z": [0,0,1], "origin": [0,0,0]},
			 "frameB": {"x": [1,0,0], "y": [0,1,0], "z": [0,0,1], "origin": [1,0,0]},
			 "axisMate": {"kind": "concentric", "name": "Shaft in bore", "offset": 0.25, "flipped": false,
			   "sourceA": {"occurrenceId": "__OCC_A__", "geometryRef": "edge:12", "kind": "straightEdge", "radius": null, "anchor": [0,0,0], "direction": [0,0,1], "fingerprint": "StraightEdge|edge:12|r=-|a=0.000,0.000,0.000|d=0.000,0.000,1.000"},
			   "sourceB": {"occurrenceId": "__OCC_B__", "geometryRef": "edge:34", "kind": "circularEdge", "radius": 0.5, "anchor": [1,0,0], "direction": [0,0,1], "fingerprint": "CircularEdge|edge:34|r=0.500|a=1.000,0.000,0.000|d=0.000,0.000,1.000"}}}
		  ]
		}
		""".Replace("__DEF_A__", DefA).Replace("__DEF_B__", DefB)
		.Replace("__OCC_A__", OccA).Replace("__OCC_B__", OccB)
		.Replace("__JOINT_AXIS__", JointAxis);
}
