using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Effigy.Assembly;

/// <summary>
/// Saved assembly metadata. Geometry stays in its source document; occurrences refer to a
/// definition and carry their own transform, so placing one occurrence never edits shared source
/// geometry.
/// </summary>
public sealed class AssemblyDocument
{
	public const int CurrentSchemaVersion = 1;
	public const string EffigyLengthUnit = "in";
	public const string RadianAngleUnit = "rad";
	public const string EffigyCoordinateSystem = "effigy-z-up-right-handed";

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	public int SchemaVersion { get; set; } = CurrentSchemaVersion;
	public string LengthUnit { get; set; } = EffigyLengthUnit;
	public string AngleUnit { get; set; } = RadianAngleUnit;
	public string CoordinateSystem { get; set; } = EffigyCoordinateSystem;
	public List<AssemblyPartDefinition> Definitions { get; set; } = new();
	public List<AssemblyOccurrence> Occurrences { get; set; } = new();
	public List<AssemblyJoint> Joints { get; set; } = new();

	[JsonExtensionData]
	public Dictionary<string, JsonElement> ExtensionData { get; set; }

	[JsonIgnore]
	public string OpaqueFutureJson { get; private set; }

	[JsonIgnore]
	public bool HasData => OpaqueFutureJson != null
		|| SchemaVersion != CurrentSchemaVersion
		|| LengthUnit != EffigyLengthUnit
		|| AngleUnit != RadianAngleUnit
		|| CoordinateSystem != EffigyCoordinateSystem
		|| Definitions == null || Occurrences == null || Joints == null
		|| (Definitions?.Count ?? 0) > 0
		|| (Occurrences?.Count ?? 0) > 0
		|| (Joints?.Count ?? 0) > 0
		|| (ExtensionData?.Count ?? 0) > 0;

	public static string NewId() => Guid.NewGuid().ToString("N");

	public string ToJson()
	{
		if (OpaqueFutureJson != null)
			return OpaqueFutureJson;

		var json = JsonSerializer.Serialize(this, JsonOptions);
		var node = JsonNode.Parse(json);
		return CanonicalJson(node);
	}

	public static AssemblyDocument FromJson(string json)
	{
		using var parsed = JsonDocument.Parse(json ?? throw new ArgumentNullException(nameof(json)));
		if (parsed.RootElement.ValueKind != JsonValueKind.Object
			|| !TryGetProperty(parsed.RootElement, "schemaVersion", out var versionElement)
			|| versionElement.ValueKind != JsonValueKind.Number
			|| !versionElement.TryGetInt32(out var version)
			|| version < 1)
			throw new FormatException("Assembly data must have a positive integer schemaVersion.");

		if (version > CurrentSchemaVersion)
			return new AssemblyDocument { SchemaVersion = version, OpaqueFutureJson = CanonicalJson(JsonNode.Parse(json)) };

		return JsonSerializer.Deserialize<AssemblyDocument>(json, JsonOptions)
			?? throw new FormatException("Assembly data is empty.");
	}

	public IReadOnlyList<AssemblyValidationIssue> Validate(Func<AssemblyPartDefinition, bool> sourceExists = null)
	{
		var issues = new List<AssemblyValidationIssue>();
		if (OpaqueFutureJson != null)
		{
			issues.Add(new("unsupported-schema", "", $"Assembly schema {SchemaVersion} is newer than this reader.", Warning: true));
			return issues;
		}

		if (SchemaVersion != CurrentSchemaVersion)
			issues.Add(new("unsupported-schema", "", $"Unsupported assembly schema {SchemaVersion}."));
		if (LengthUnit != EffigyLengthUnit)
			issues.Add(new("unsupported-length-unit", "", $"Length unit '{LengthUnit}' is not supported."));
		if (AngleUnit != RadianAngleUnit)
			issues.Add(new("unsupported-angle-unit", "", $"Angle unit '{AngleUnit}' is not supported."));
		if (CoordinateSystem != EffigyCoordinateSystem)
			issues.Add(new("unsupported-coordinate-system", "", $"Coordinate system '{CoordinateSystem}' is not supported."));
		if (Definitions == null) issues.Add(new("missing-definitions", "", "Definitions collection is missing."));
		if (Occurrences == null) issues.Add(new("missing-occurrences", "", "Occurrences collection is missing."));
		if (Joints == null) issues.Add(new("missing-joints", "", "Joints collection is missing."));

		var ids = new HashSet<string>(StringComparer.Ordinal);
		void AddId(string id, string kind)
		{
			if (!IsStableId(id))
				issues.Add(new("invalid-id", id ?? "", $"{kind} ID must be a 32-character GUID."));
			else if (!ids.Add(id))
				issues.Add(new("duplicate-id", id, $"ID is used by more than one assembly record."));
		}

		var definitions = Definitions ?? new List<AssemblyPartDefinition>();
		var occurrences = Occurrences ?? new List<AssemblyOccurrence>();
		var joints = Joints ?? new List<AssemblyJoint>();
		foreach (var definition in definitions)
		{
			if (definition == null) { issues.Add(new("null-definition", "", "Assembly contains a null definition.")); continue; }
			AddId(definition.Id, "Definition");
			if (string.IsNullOrWhiteSpace(definition.Name))
				issues.Add(new("missing-name", definition.Id ?? "", "Definition name is required."));
			if (string.IsNullOrWhiteSpace(definition.SourceKind) || string.IsNullOrWhiteSpace(definition.SourceReference))
				issues.Add(new("missing-source", definition.Id ?? "", "Definition needs a source kind and source reference."));
			else if (sourceExists != null && !sourceExists(definition))
				issues.Add(new("missing-source-reference", definition.Id ?? "", $"Source '{definition.SourceReference}' could not be resolved."));
		}

		var definitionIds = new HashSet<string>(definitions.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Id)).Select(d => d.Id), StringComparer.Ordinal);
		foreach (var occurrence in occurrences)
		{
			if (occurrence == null) { issues.Add(new("null-occurrence", "", "Assembly contains a null occurrence.")); continue; }
			AddId(occurrence.Id, "Occurrence");
			if (string.IsNullOrWhiteSpace(occurrence.Name))
				issues.Add(new("missing-name", occurrence.Id ?? "", "Occurrence name is required."));
			if (!definitionIds.Contains(occurrence.DefinitionId ?? ""))
				issues.Add(new("missing-definition", occurrence.Id ?? "", $"Definition '{occurrence.DefinitionId}' does not exist."));
			if (occurrence.Transform == null)
				issues.Add(new("invalid-transform", occurrence.Id ?? "", "Occurrence transform is missing."));
			else if (!occurrence.Transform.IsValid(out var why))
				issues.Add(new("invalid-transform", occurrence.Id ?? "", why));
		}

		var occurrenceIds = new HashSet<string>(occurrences.Where(o => o != null && !string.IsNullOrWhiteSpace(o.Id)).Select(o => o.Id), StringComparer.Ordinal);
		foreach (var joint in joints)
		{
			if (joint == null) { issues.Add(new("null-joint", "", "Assembly contains a null joint.")); continue; }
			AddId(joint.Id, "Joint");
			if (!occurrenceIds.Contains(joint.OccurrenceA ?? ""))
				issues.Add(new("missing-occurrence", joint.Id ?? "", $"Occurrence '{joint.OccurrenceA}' does not exist."));
			if (!occurrenceIds.Contains(joint.OccurrenceB ?? ""))
				issues.Add(new("missing-occurrence", joint.Id ?? "", $"Occurrence '{joint.OccurrenceB}' does not exist."));
			if (!string.IsNullOrWhiteSpace(joint.OccurrenceA) && joint.OccurrenceA == joint.OccurrenceB)
				issues.Add(new("self-joint", joint.Id ?? "", "A joint must connect two different occurrences."));

			if (joint.Kind is not ("fixed" or "hinge" or "slider"))
				issues.Add(new("unsupported-joint-kind", joint.Id ?? "", $"Joint kind '{joint.Kind}' is preserved but not supported.", Warning: true));
			else
			{
				CheckConnector(joint.FrameA, joint.Id, "A");
				CheckConnector(joint.FrameB, joint.Id, "B");
			}

			if ((joint.Minimum.HasValue && !float.IsFinite(joint.Minimum.Value))
				|| (joint.Maximum.HasValue && !float.IsFinite(joint.Maximum.Value)))
				issues.Add(new("invalid-joint-limit", joint.Id ?? "", "Joint limits must be finite."));
			if (joint.Minimum.HasValue && joint.Maximum.HasValue && joint.Minimum > joint.Maximum)
				issues.Add(new("invalid-joint-limit", joint.Id ?? "", "Joint minimum cannot exceed its maximum."));

			foreach (var mateIssue in AssemblyMate.ValidateJoint(joint))
				issues.Add(mateIssue);
		}

		return issues;

		void CheckConnector(AssemblyTransform frame, string id, string side)
		{
			if (frame == null)
				issues.Add(new("invalid-connector-frame", id ?? "", $"Connector {side} frame is missing."));
			else if (!frame.IsRigidFrame(out var why))
				issues.Add(new("invalid-connector-frame", id ?? "", $"Connector {side} frame must be a finite rigid transform: {why}"));
		}
	}

	/// <summary>Mates stored on this document's joints, plus one refusal per mate payload
	/// this build cannot implement or cannot read. A mate that fails to read is never silently
	/// dropped: it is reported here (and by <see cref="Validate"/>) naming the variant, and the
	/// joint itself stays in <see cref="Joints"/> with its payload intact, so a caller that
	/// refuses the document can still save it back unchanged.</summary>
	public void TryReadMates(out IReadOnlyList<AssemblyMate> mates, out IReadOnlyList<AssemblyValidationIssue> refusals)
	{
		var found = new List<AssemblyMate>();
		var refused = new List<AssemblyValidationIssue>();
		foreach (var joint in Joints ?? new List<AssemblyJoint>())
		{
			if (joint == null) continue;
			var mate = AssemblyMate.TryRead(joint, out var refusal);
			if (mate != null) found.Add(mate);
			else if (refusal != null) refused.Add(refusal);
		}
		mates = found;
		refusals = refused;
	}

	internal static bool IsStableId(string id) =>
		Guid.TryParseExact(id, "N", out var parsed) && parsed.ToString("N") == id;

	static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
	{
		if (element.TryGetProperty(name, out value)) return true;
		foreach (var property in element.EnumerateObject())
			if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
			{ value = property.Value; return true; }
		value = default;
		return false;
	}

	static string CanonicalJson(JsonNode node)
	{
		JsonNode Sort(JsonNode value)
		{
			if (value is JsonObject obj)
			{
				var sorted = new JsonObject();
				foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
					sorted[pair.Key] = pair.Value == null ? null : Sort(pair.Value);
				return sorted;
			}
			if (value is JsonArray array)
			{
				var sorted = new JsonArray();
				foreach (var item in array) sorted.Add(item == null ? null : Sort(item));
				return sorted;
			}
			return value?.DeepClone();
		}

		return Sort(node)?.ToJsonString() ?? "{}";
	}
}

public sealed class AssemblyPartDefinition
{
	public string Id { get; set; } = "";
	public string Name { get; set; } = "";
	/// <summary>Opaque provider token, such as effigy-file or studio-body.</summary>
	public string SourceKind { get; set; } = "";
	/// <summary>Provider-specific stable locator; not a copied or transformed mesh.</summary>
	public string SourceReference { get; set; } = "";

	[JsonExtensionData]
	public Dictionary<string, JsonElement> ExtensionData { get; set; }
}

public sealed class AssemblyOccurrence
{
	public string Id { get; set; } = "";
	public string DefinitionId { get; set; } = "";
	public string Name { get; set; } = "";
	public bool Visible { get; set; } = true;
	public AssemblyTransform Transform { get; set; } = new();

	[JsonExtensionData]
	public Dictionary<string, JsonElement> ExtensionData { get; set; }
}

/// <summary>
/// A transform whose basis columns and origin are in Effigy coordinates. Basis vectors are
/// dimensionless; origin uses the document's declared length unit. Occurrence transforms may
/// scale or mirror; connector frames must be proper rigid transforms.
/// </summary>
public sealed class AssemblyTransform
{
	public float[] X { get; set; } = new[] { 1f, 0f, 0f };
	public float[] Y { get; set; } = new[] { 0f, 1f, 0f };
	public float[] Z { get; set; } = new[] { 0f, 0f, 1f };
	public float[] Origin { get; set; } = new[] { 0f, 0f, 0f };

	[JsonExtensionData]
	public Dictionary<string, JsonElement> ExtensionData { get; set; }

	public static AssemblyTransform FromXform(Xform transform) => new()
	{
		X = new[] { transform.X.x, transform.X.y, transform.X.z },
		Y = new[] { transform.Y.x, transform.Y.y, transform.Y.z },
		Z = new[] { transform.Z.x, transform.Z.y, transform.Z.z },
		Origin = new[] { transform.Origin.x, transform.Origin.y, transform.Origin.z },
	};

	public bool IsValid(out string why)
	{
		why = null;
		if (!TryToXform(out var transform))
		{
			why = "Transform vectors must contain three finite numbers each.";
			return false;
		}
		if (!float.IsFinite(transform.Determinant) || MathF.Abs(transform.Determinant) < 1e-8f)
		{
			why = "Transform basis is singular.";
			return false;
		}
		return true;
	}

	public bool IsRigidFrame(out string why)
	{
		why = null;
		if (!TryToXform(out var transform))
		{
			why = "Transform vectors must contain three finite numbers each.";
			return false;
		}
		const float epsilon = 1e-4f;
		if (MathF.Abs(transform.X.LengthSquared - 1f) > epsilon
			|| MathF.Abs(transform.Y.LengthSquared - 1f) > epsilon
			|| MathF.Abs(transform.Z.LengthSquared - 1f) > epsilon
			|| MathF.Abs(Vec3.Dot(transform.X, transform.Y)) > epsilon
			|| MathF.Abs(Vec3.Dot(transform.Y, transform.Z)) > epsilon
			|| MathF.Abs(Vec3.Dot(transform.Z, transform.X)) > epsilon
			|| MathF.Abs(transform.Determinant - 1f) > epsilon)
		{
			why = "Connector basis must be orthonormal and right-handed.";
			return false;
		}
		return true;
	}

	public Xform ToXform() => TryToXform(out var transform)
		? transform
		: throw new InvalidOperationException("Assembly transform is malformed.");

	bool TryToXform(out Xform transform)
	{
		transform = Xform.Identity;
		if (!Vector(X, out var x) || !Vector(Y, out var y) || !Vector(Z, out var z) || !Vector(Origin, out var origin))
			return false;
		transform = new Xform(x, y, z, origin);
		return true;
	}

	static bool Vector(float[] values, out Vec3 vector)
	{
		vector = Vec3.Zero;
		if (values == null || values.Length != 3 || values.Any(v => !float.IsFinite(v)))
			return false;
		vector = new Vec3(values[0], values[1], values[2]);
		return true;
	}
}

public sealed class AssemblyJoint
{
	public string Id { get; set; } = "";
	/// <summary>fixed, hinge, slider, or an opaque future kind preserved by this reader.</summary>
	public string Kind { get; set; } = "fixed";
	public string OccurrenceA { get; set; } = "";
	public string OccurrenceB { get; set; } = "";
	/// <summary>
	/// Connector frames in each occurrence's local coordinate system. Fixed aligns both frames;
	/// hinge permits rotation about their shared local +Z; slider permits translation along it.
	/// </summary>
	public AssemblyTransform FrameA { get; set; } = new();
	public AssemblyTransform FrameB { get; set; } = new();
	/// <summary>Hinge limits use AngleUnit; slider limits use LengthUnit.</summary>
	public float? Minimum { get; set; }
	public float? Maximum { get; set; }

	[JsonExtensionData]
	public Dictionary<string, JsonElement> ExtensionData { get; set; }
}

/// <summary>One side of a face mate: which occurrence, and which face of which source body.
/// The face is a <see cref="FaceRef"/> — a point plus a normal on a body — so it can be
/// re-found after an upstream edit, exactly like a sketch on a face.</summary>
public sealed class AssemblyMateFace
{
	public string OccurrenceId { get; set; } = "";
	public FaceRef Ref { get; set; }

	public string BodyId => Ref.BodyId;

	public JsonObject ToJsonObject() => new()
	{
		["occurrenceId"] = OccurrenceId,
		["bodyId"] = Ref.BodyId,
		["point"] = AssemblyMate.VecArray(Ref.Point),
		["normal"] = AssemblyMate.VecArray(Ref.Normal),
		["anchor"] = new JsonArray(Ref.Anchor.x, Ref.Anchor.y, 0f),
		["anchorFromMaxX"] = Ref.AnchorFromMaxX,
		["anchorFromMaxY"] = Ref.AnchorFromMaxY,
		["anchored"] = Ref.Anchored,
	};

	public static bool TryParse(JsonElement element, out AssemblyMateFace? face, out string? why)
	{
		face = null;
		why = null;
		try
		{
			if (element.ValueKind != JsonValueKind.Object)
			{
				why = "Face-mate face is not an object.";
				return false;
			}
			if (!AssemblyMate.TryGetString(element, "occurrenceId", out var occurrenceId)
				|| string.IsNullOrWhiteSpace(occurrenceId))
			{
				why = "Face-mate face has no occurrence id.";
				return false;
			}
			if (!AssemblyMate.TryGetVec(element, "point", out var point)
				|| !AssemblyMate.TryGetVec(element, "normal", out var normal)
				|| normal.LengthSquared < 1e-12f)
			{
				why = "Face-mate face has no usable point or normal.";
				return false;
			}
			if (!AssemblyMate.TryGetString(element, "bodyId", out var bodyId))
				bodyId = "";
			FaceRef faceRef;
			if (element.TryGetProperty("anchored", out var anchoredElement)
				&& anchoredElement.ValueKind == JsonValueKind.True
				&& AssemblyMate.TryGetVec(element, "anchor", out var anchor))
			{
				var fromMaxX = element.TryGetProperty("anchorFromMaxX", out var xElement)
					&& xElement.ValueKind == JsonValueKind.True;
				var fromMaxY = element.TryGetProperty("anchorFromMaxY", out var yElement)
					&& yElement.ValueKind == JsonValueKind.True;
				faceRef = new FaceRef(bodyId, point, normal,
					new Vec2(anchor.x, anchor.y), fromMaxX, fromMaxY);
			}
			else
			{
				faceRef = new FaceRef(bodyId, point, normal);
			}
			face = new AssemblyMateFace { OccurrenceId = occurrenceId, Ref = faceRef };
			return true;
		}
		catch (Exception e)
		{
			why = "Face-mate face could not be read: " + e.Message;
			return false;
		}
	}
}

/// <summary>What analytic geometry an axis-mate connector came from. UnsupportedMesh is a
/// rejected pick, never a source: mesh triangles have no persistent analytic axis.</summary>
public enum AssemblyMateAxisKind
{
	StraightEdge,
	CircularEdge,
	CylindricalFace,
	ExplicitAxis,
	UnsupportedMesh,
}

/// <summary>One side of an axis (concentric) mate: the connector on one occurrence, described
/// by the analytic geometry it came from. Mirrors Mobile's AxisConnectorSource, including the
/// fingerprint, so a Desktop reader refuses a stale or mesh-only source the same way.</summary>
public sealed class AssemblyMateAxisSource
{
	public string OccurrenceId { get; set; } = "";
	public string GeometryRef { get; set; } = "";
	public AssemblyMateAxisKind Kind { get; set; } = AssemblyMateAxisKind.UnsupportedMesh;
	public float? Radius { get; set; }
	public Vec3 Anchor { get; set; }
	public Vec3 Direction { get; set; }
	public string Fingerprint { get; set; } = "";

	public string ComputeFingerprint()
	{
		if (Kind == AssemblyMateAxisKind.UnsupportedMesh) return "unsupported";
		static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);
		return $"{Kind}|{GeometryRef}|r={(Radius.HasValue ? F(Radius.Value) : "-")}"
			+ $"|a={F(Anchor.x)},{F(Anchor.y)},{F(Anchor.z)}"
			+ $"|d={F(Direction.x)},{F(Direction.y)},{F(Direction.z)}";
	}

	public bool TryValidate(out string? error)
	{
		error = null;
		if (!AssemblyDocument.IsStableId(OccurrenceId)) { error = "invalid-reference: connector needs a stable occurrence ID."; return false; }
		if (Kind == AssemblyMateAxisKind.UnsupportedMesh)
		{
			error = "unsupported-geometry: that pick has no straight edge, circular edge, cylindrical face or explicit axis. Pick analytic geometry or define an explicit axis — a mesh pick is never used as an axis.";
			return false;
		}
		if (string.IsNullOrWhiteSpace(GeometryRef)) { error = "invalid-reference: connector needs a stable geometry reference."; return false; }
		if (!IsFinite(Anchor) || !IsFinite(Direction)) { error = "invalid-axis: connector anchor and direction must be finite."; return false; }
		if (Direction.LengthSquared < 1e-12f) { error = "invalid-axis: connector direction is degenerate."; return false; }
		if ((Kind == AssemblyMateAxisKind.CircularEdge || Kind == AssemblyMateAxisKind.CylindricalFace)
			&& (!Radius.HasValue || !float.IsFinite(Radius.Value) || Radius.Value <= 0f))
		{
			error = "invalid-radius: a circular edge or cylindrical face needs a positive finite radius.";
			return false;
		}
		if (Radius.HasValue && (!float.IsFinite(Radius.Value) || Radius.Value <= 0f)) { error = "invalid-radius: radius must be positive and finite."; return false; }
		if (string.IsNullOrEmpty(Fingerprint) || Fingerprint != ComputeFingerprint())
		{
			error = "stale-reference: the connector fingerprint does not match its geometry. Reselect the connector.";
			return false;
		}
		return true;
	}

	static bool IsFinite(Vec3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

	/// <summary>The connector frame: origin on the axis, local +Z along the axis, X/Y a
	/// deterministic orthonormal basis. Same source always yields the same frame. Fails for
	/// unsupported geometry instead of inventing a frame (and never uses a centroid).</summary>
	public bool TryToFrame(out AssemblyTransform? frame, out string? error)
	{
		frame = null;
		if (!TryValidate(out error)) return false;
		var z = Direction.Normal;
		if (z.LengthSquared < 0.5f) { error = "invalid-axis: connector direction is degenerate."; return false; }
		var up = MathF.Abs(z.y) < 0.9f ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
		var x = Vec3.Cross(up, z).Normal;
		if (x.LengthSquared < 0.5f) { error = "invalid-axis: cannot build a frame for this axis."; return false; }
		var y = Vec3.Cross(z, x);
		frame = new AssemblyTransform
		{
			X = new[] { x.x, x.y, x.z },
			Y = new[] { y.x, y.y, y.z },
			Z = new[] { z.x, z.y, z.z },
			Origin = new[] { Anchor.x, Anchor.y, Anchor.z },
		};
		return true;
	}

	public JsonObject ToJsonObject() => new()
	{
		["occurrenceId"] = OccurrenceId,
		["geometryRef"] = GeometryRef,
		["kind"] = Kind switch
		{
			AssemblyMateAxisKind.StraightEdge => "straightEdge",
			AssemblyMateAxisKind.CircularEdge => "circularEdge",
			AssemblyMateAxisKind.CylindricalFace => "cylindricalFace",
			AssemblyMateAxisKind.ExplicitAxis => "explicitAxis",
			_ => "unsupportedMesh",
		},
		["radius"] = Radius.HasValue ? JsonValue.Create(Radius.Value) : null,
		["anchor"] = new JsonArray(Anchor.x, Anchor.y, Anchor.z),
		["direction"] = new JsonArray(Direction.x, Direction.y, Direction.z),
		["fingerprint"] = Fingerprint,
	};

	public static AssemblyMateAxisSource? TryParse(JsonElement element, out string? error)
	{
		error = null;
		try
		{
			var kind = element.GetProperty("kind").GetString() switch
			{
				"straightEdge" => AssemblyMateAxisKind.StraightEdge,
				"circularEdge" => AssemblyMateAxisKind.CircularEdge,
				"cylindricalFace" => AssemblyMateAxisKind.CylindricalFace,
				"explicitAxis" => AssemblyMateAxisKind.ExplicitAxis,
				_ => AssemblyMateAxisKind.UnsupportedMesh,
			};
			static Vec3 Vec(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
			var radiusElement = element.GetProperty("radius");
			return new AssemblyMateAxisSource
			{
				OccurrenceId = element.GetProperty("occurrenceId").GetString() ?? "",
				GeometryRef = element.GetProperty("geometryRef").GetString() ?? "",
				Kind = kind,
				Radius = radiusElement.ValueKind == JsonValueKind.Null ? null : radiusElement.GetSingle(),
				Anchor = Vec(element.GetProperty("anchor")),
				Direction = Vec(element.GetProperty("direction")),
				Fingerprint = element.GetProperty("fingerprint").GetString() ?? "",
			};
		}
		catch (Exception e) { error = "invalid-reference: axis mate source could not be read: " + e.Message; return null; }
	}
}

/// <summary>A mate between two assembly occurrences, as Mobile stores it: a joint plus a mate
/// record in the joint's extension data. A face mate is a fixed joint whose <c>faceMate</c>
/// record aligns two planar faces and holds them apart by an explicit offset; an axis
/// (concentric) mate is a hinge joint whose <c>axisMate</c> record makes two connector axes
/// collinear, with an axial offset and a flip. The record is the mate: the joint frames alone
/// do not carry the offset, the alignment, or the face/axis references, so a reader that only
/// understands joints silently loses the mate on save. This is NOT the tree feature
/// <see cref="AssemblyMateFeature"/> ("Mate"), the legacy centroid mate that moves one body
/// toward another on every rebuild — that feature lives in the PartStudio feature tree, this
/// record lives in the assembly document, and neither one reads or writes the other.</summary>
public sealed class AssemblyMate
{
	public const string FaceKind = "face";
	public const string AxisKind = "axis";
	public const string FaceExtensionKey = "faceMate";
	public const string AxisExtensionKey = "axisMate";
	public const int FaceRecordVersion = 1;
	public const string AxisRecordKind = "concentric";

	public string Kind { get; set; } = FaceKind;
	public string Id { get; set; } = "";
	public string Name { get; set; } = "";
	public string OccurrenceA { get; set; } = "";
	public string OccurrenceB { get; set; } = "";

	// Face mate
	public string Alignment { get; set; } = "opposed";
	public float Offset { get; set; }
	public string OffsetUnit { get; set; } = "in";
	public float OffsetInches { get; set; }
	public AssemblyMateFace? FaceA { get; set; }
	public AssemblyMateFace? FaceB { get; set; }

	// Axis mate
	public float AxialOffset { get; set; }
	public bool Flipped { get; set; }
	public AssemblyMateAxisSource? SourceA { get; set; }
	public AssemblyMateAxisSource? SourceB { get; set; }

	public static bool IsMateJoint(AssemblyJoint joint) =>
		joint?.ExtensionData != null
		&& (joint.ExtensionData.ContainsKey(FaceExtensionKey) || joint.ExtensionData.ContainsKey(AxisExtensionKey));

	/// <summary>Read the mate stored on a joint. Returns null with a null refusal for a plain
	/// joint (no mate payload); returns null with a refusal naming the variant for a mate
	/// payload this build cannot implement or cannot read — the caller must refuse the
	/// document, never silently drop the joint.</summary>
	public static AssemblyMate? TryRead(AssemblyJoint joint, out AssemblyValidationIssue? refusal)
	{
		refusal = null;
		if (joint?.ExtensionData == null) return null;
		var hasFace = joint.ExtensionData.TryGetValue(FaceExtensionKey, out var faceElement);
		var hasAxis = joint.ExtensionData.TryGetValue(AxisExtensionKey, out var axisElement);
		if (!hasFace && !hasAxis) return null;
		if (hasFace && hasAxis)
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "",
				"Joint carries both a face-mate and an axis-mate record; a joint holds at most one mate.");
			return null;
		}
		return hasFace ? TryReadFace(joint, faceElement, out refusal) : TryReadAxis(joint, axisElement, out refusal);
	}

	static AssemblyMate? TryReadFace(AssemblyJoint joint, JsonElement stored, out AssemblyValidationIssue? refusal)
	{
		refusal = null;
		if (stored.ValueKind != JsonValueKind.Object)
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Stored face mate is not an object.");
			return null;
		}
		if (stored.TryGetProperty("version", out var versionElement)
			&& (versionElement.ValueKind != JsonValueKind.Number
				|| !versionElement.TryGetInt32(out var version) || version != FaceRecordVersion))
		{
			refusal = new AssemblyValidationIssue("unsupported-mate-kind", joint.Id ?? "",
				$"Face-mate record version '{versionElement}' is not implemented; this build reads version {FaceRecordVersion}.");
			return null;
		}
		if (!TryGetString(stored, "alignment", out var alignment))
		{
			refusal = new AssemblyValidationIssue("unsupported-mate-kind", joint.Id ?? "",
				"Face-mate alignment is missing; this build reads 'opposed' and 'same'.");
			return null;
		}
		var normalized = NormalizeAlignment(alignment);
		if (normalized == null)
		{
			refusal = new AssemblyValidationIssue("unsupported-mate-kind", joint.Id ?? "",
				$"Face-mate alignment '{alignment}' is not implemented; this build reads 'opposed' and 'same'.");
			return null;
		}
		if (!TryGetFloat(stored, "offset", out var offset) || !TryGetString(stored, "offsetUnit", out var offsetUnit)
			|| !TryToInches(offset, offsetUnit, out var offsetInches))
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Stored face mate has no usable offset.");
			return null;
		}
		if (stored.TryGetProperty("offsetInches", out var inchesElement)
			&& inchesElement.ValueKind == JsonValueKind.Number && inchesElement.TryGetSingle(out var storedInches)
			&& MathF.Abs(storedInches - offsetInches) > 1e-3f)
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "",
				"Stored face mate offsetInches does not match its offset and unit.");
			return null;
		}
		string? whyA = null;
		string? whyB = null;
		if (!stored.TryGetProperty("faceA", out var aElement) || !AssemblyMateFace.TryParse(aElement, out var faceA, out whyA))
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Stored face mate side A could not be read: " + whyA);
			return null;
		}
		if (!stored.TryGetProperty("faceB", out var bElement) || !AssemblyMateFace.TryParse(bElement, out var faceB, out whyB))
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Stored face mate side B could not be read: " + whyB);
			return null;
		}
		return new AssemblyMate
		{
			Kind = FaceKind, Id = joint.Id ?? "", OccurrenceA = joint.OccurrenceA ?? "", OccurrenceB = joint.OccurrenceB ?? "",
			Alignment = normalized, Offset = offset, OffsetUnit = NormalizeUnit(offsetUnit), OffsetInches = offsetInches,
			FaceA = faceA, FaceB = faceB,
		};
	}

	static AssemblyMate? TryReadAxis(AssemblyJoint joint, JsonElement stored, out AssemblyValidationIssue? refusal)
	{
		refusal = null;
		if (stored.ValueKind != JsonValueKind.Object)
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Stored axis mate is not an object.");
			return null;
		}
		if (!TryGetString(stored, "kind", out var kind) || kind != AxisRecordKind)
		{
			refusal = new AssemblyValidationIssue("unsupported-mate-kind", joint.Id ?? "",
				$"Axis-mate kind '{kind}' is not implemented; this build reads '{AxisRecordKind}'.");
			return null;
		}
		var sourceA = AssemblyMateAxisSource.TryParse(stored.TryGetProperty("sourceA", out var aElement) ? aElement : default, out var whyA);
		if (sourceA == null)
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Stored axis-mate source A could not be read: " + whyA);
			return null;
		}
		var sourceB = AssemblyMateAxisSource.TryParse(stored.TryGetProperty("sourceB", out var bElement) ? bElement : default, out var whyB);
		if (sourceB == null)
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Stored axis-mate source B could not be read: " + whyB);
			return null;
		}
		if (sourceA.Kind == AssemblyMateAxisKind.UnsupportedMesh || sourceB.Kind == AssemblyMateAxisKind.UnsupportedMesh)
		{
			refusal = new AssemblyValidationIssue("unsupported-mate-geometry", joint.Id ?? "",
				"Axis mate names a mesh-only pick, which has no analytic axis. Pick analytic geometry or define an explicit axis — a mesh pick is never used as an axis.");
			return null;
		}
		if (!sourceA.TryValidate(out var whyValidA))
		{
			refusal = RefusalFromError(joint.Id ?? "", whyValidA ?? "Axis mate source A could not be validated.");
			return null;
		}
		if (!sourceB.TryValidate(out var whyValidB))
		{
			refusal = RefusalFromError(joint.Id ?? "", whyValidB ?? "Axis mate source B could not be validated.");
			return null;
		}
		if (!TryGetFloat(stored, "offset", out var offset))
		{
			refusal = new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Stored axis mate has no usable offset.");
			return null;
		}
		var flipped = stored.TryGetProperty("flipped", out var flippedElement) && flippedElement.ValueKind == JsonValueKind.True;
		var name = TryGetString(stored, "name", out var mateName) ? mateName : "";
		return new AssemblyMate
		{
			Kind = AxisKind, Id = joint.Id ?? "", Name = name, OccurrenceA = joint.OccurrenceA ?? "", OccurrenceB = joint.OccurrenceB ?? "",
			AxialOffset = offset, Flipped = flipped, SourceA = sourceA, SourceB = sourceB,
		};
	}

	/// <summary>Store this mate as a joint with its Mobile payload: a fixed joint with a
	/// <c>faceMate</c> record, or a hinge joint with an <c>axisMate</c> record. The frames are
	/// built from the stored faces/sources by the same deterministic rules Mobile uses, so a
	/// joint written here reads back identically there. Throws when the frames cannot be
	/// built (an axis source with no valid frame) instead of storing a mate that cannot be
	/// solved. This stores the record only; moving occurrence B onto the mate is the
	/// solver's job, and Desktop has no assembly solver yet.</summary>
	public AssemblyJoint ToJoint()
	{
		if (Kind != FaceKind && Kind != AxisKind)
			throw new InvalidOperationException($"Mate kind '{Kind}' is not implemented; this build stores 'face' and 'axis' mates.");
		if (Kind == AxisKind)
		{
			var sourceA = SourceA;
			var sourceB = SourceB;
			if (sourceA == null || sourceB == null)
				throw new InvalidOperationException("An axis mate needs both connector sources before it can be stored.");
			if (!sourceA.TryToFrame(out var frameA, out var errorA) || frameA == null)
				throw new InvalidOperationException("Axis mate connector A has no valid frame: " + errorA);
			if (!sourceB.TryToFrame(out var frameB, out var errorB) || frameB == null)
				throw new InvalidOperationException("Axis mate connector B has no valid frame: " + errorB);
			var payload = new JsonObject
			{
				["kind"] = AxisRecordKind,
				["name"] = Name ?? "",
				["offset"] = AxialOffset,
				["flipped"] = Flipped,
				["sourceA"] = sourceA.ToJsonObject(),
				["sourceB"] = sourceB.ToJsonObject(),
			};
			using var parsed = JsonDocument.Parse(payload.ToJsonString());
			return new AssemblyJoint
			{
				Id = Id, Kind = "hinge", OccurrenceA = OccurrenceA, OccurrenceB = OccurrenceB,
				FrameA = frameA, FrameB = frameB,
				ExtensionData = new Dictionary<string, JsonElement> { [AxisExtensionKey] = parsed.RootElement.Clone() },
			};
		}

		var faceA = FaceA;
		var faceB = FaceB;
		if (faceA == null || faceB == null)
			throw new InvalidOperationException("A face mate needs both faces before it can be stored.");
		var (faceFrameA, faceFrameB) = FaceFrames(faceA, faceB, Alignment);
		var record = new JsonObject
		{
			["version"] = FaceRecordVersion,
			["alignment"] = NormalizeAlignment(Alignment) ?? Alignment,
			["offset"] = Offset,
			["offsetUnit"] = NormalizeUnit(OffsetUnit),
			["offsetInches"] = OffsetInches,
			["faceA"] = faceA.ToJsonObject(),
			["faceB"] = faceB.ToJsonObject(),
			["meshFallback"] = true,
			["createdFrom"] = "face-mate",
		};
		using var parsedRecord = JsonDocument.Parse(record.ToJsonString());
		return new AssemblyJoint
		{
			Id = Id, Kind = "fixed", OccurrenceA = OccurrenceA, OccurrenceB = OccurrenceB,
			FrameA = faceFrameA, FrameB = faceFrameB,
			ExtensionData = new Dictionary<string, JsonElement> { [FaceExtensionKey] = parsedRecord.RootElement.Clone() },
		};
	}

	static (AssemblyTransform, AssemblyTransform) FaceFrames(AssemblyMateFace faceA, AssemblyMateFace faceB, string alignment)
	{
		var planeA = FacePlane.FromPointAndNormal(faceA.Ref.Point, faceA.Ref.Normal);
		var planeB = FacePlane.FromPointAndNormal(faceB.Ref.Point, faceB.Ref.Normal);
		var frameA = FrameFromPlane(planeA);
		var frameB = FrameFromPlane(planeB);
		if (NormalizeAlignment(alignment) == "opposed")
		{
			// The flip for opposed is baked into FrameB: its X and Z are negated, keeping it
			// right-handed, so aligning the two frames exactly reproduces the mate at offset 0.
			frameB = new AssemblyTransform
			{
				X = new[] { -frameB.X[0], -frameB.X[1], -frameB.X[2] },
				Y = frameB.Y,
				Z = new[] { -frameB.Z[0], -frameB.Z[1], -frameB.Z[2] },
				Origin = frameB.Origin,
			};
		}
		return (frameA, frameB);
	}

	static AssemblyTransform FrameFromPlane(SketchPlane plane) => new()
	{
		X = new[] { plane.XAxis.x, plane.XAxis.y, plane.XAxis.z },
		Y = new[] { plane.YAxis.x, plane.YAxis.y, plane.YAxis.z },
		Z = new[] { plane.Normal.x, plane.Normal.y, plane.Normal.z },
		Origin = new[] { plane.Origin.x, plane.Origin.y, plane.Origin.z },
	};

	/// <summary>Validate one joint's mate record: the record must read, its sides must name
	/// the joint's own occurrences, and a face mate must sit on a fixed joint, an axis mate
	/// on a hinge joint. Plain joints return no issues.</summary>
	internal static IReadOnlyList<AssemblyValidationIssue> ValidateJoint(AssemblyJoint joint)
	{
		var issues = new List<AssemblyValidationIssue>();
		var mate = TryRead(joint, out var refusal);
		if (refusal != null)
		{
			issues.Add(refusal);
			return issues;
		}
		if (mate == null) return issues;
		if (mate.Kind == FaceKind)
		{
			if (joint.Kind != "fixed")
				issues.Add(new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "A face mate must be stored on a fixed joint."));
			if (mate.FaceA == null || mate.FaceA.OccurrenceId != joint.OccurrenceA)
				issues.Add(new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Face-mate side A does not name the joint's occurrence A."));
			if (mate.FaceB == null || mate.FaceB.OccurrenceId != joint.OccurrenceB)
				issues.Add(new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Face-mate side B does not name the joint's occurrence B."));
		}
		else
		{
			if (joint.Kind != "hinge")
				issues.Add(new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "An axis mate must be stored on a hinge joint."));
			if (mate.SourceA == null || mate.SourceA.OccurrenceId != joint.OccurrenceA)
				issues.Add(new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Axis-mate source A does not name the joint's occurrence A."));
			if (mate.SourceB == null || mate.SourceB.OccurrenceId != joint.OccurrenceB)
				issues.Add(new AssemblyValidationIssue("invalid-mate-record", joint.Id ?? "", "Axis-mate source B does not name the joint's occurrence B."));
		}
		return issues;
	}

	static AssemblyValidationIssue RefusalFromError(string recordId, string error)
	{
		var split = (error ?? "").IndexOf(": ", StringComparison.Ordinal);
		if (split > 0)
			return new AssemblyValidationIssue(error[..split], recordId, error[(split + 2)..]);
		return new AssemblyValidationIssue("invalid-mate-record", recordId, error ?? "Mate record could not be read.");
	}

	internal static JsonArray VecArray(Vec3 v) => new() { v.x, v.y, v.z };

	internal static bool TryGetString(JsonElement element, string name, out string? value)
	{
		value = null;
		if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
			return false;
		value = property.GetString();
		return value != null;
	}

	internal static bool TryGetFloat(JsonElement element, string name, out float value)
	{
		value = 0f;
		if (!element.TryGetProperty(name, out var property)) return false;
		try
		{
			if (property.ValueKind != JsonValueKind.Number) return false;
			value = property.GetSingle();
			return float.IsFinite(value);
		}
		catch { return false; }
	}

	internal static bool TryGetVec(JsonElement element, string name, out Vec3 value)
	{
		value = Vec3.Zero;
		if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array || property.GetArrayLength() != 3)
			return false;
		try
		{
			var x = property[0].GetSingle();
			var y = property[1].GetSingle();
			var z = property[2].GetSingle();
			if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
			value = new Vec3(x, y, z);
			return true;
		}
		catch { return false; }
	}

	/// <summary>Canonical alignment: "opposed"/"opposite"/"facing" read as opposed, "same"/
	/// "aligned"/"coincident" as same. Null when the alignment names no implemented mate.</summary>
	public static string? NormalizeAlignment(string? alignment)
	{
		var a = (alignment ?? "").Trim().ToLowerInvariant();
		return a switch { "opposed" or "opposite" or "facing" => "opposed", "same" or "aligned" or "coincident" => "same", _ => null };
	}

	public static string NormalizeUnit(string? unit)
	{
		var u = (unit ?? "").Trim().ToLowerInvariant();
		return u switch
		{
			"in" or "inch" or "inches" or "" => "in",
			"mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres" => "mm",
			"cm" or "centimeter" or "centimeters" or "centimetre" or "centimetres" => "cm",
			_ => u,
		};
	}

	/// <summary>Offset value in the given unit, converted to document inches.</summary>
	public static bool TryToInches(float value, string? unit, out float inches)
	{
		inches = 0f;
		if (!float.IsFinite(value)) return false;
		switch (NormalizeUnit(unit))
		{
			case "in": inches = value; return true;
			case "mm": inches = value / 25.4f; return true;
			case "cm": inches = value * 10f / 25.4f; return true;
			default: return false;
		}
	}
}

public sealed record AssemblyValidationIssue(string Code, string RecordId, string Message, bool Warning = false);
