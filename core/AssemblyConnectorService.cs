using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Effigy;
using Effigy.Assembly;

namespace Marionette.Desktop;

// AS03: connector frames, grounding and durable references.
// Ports Mobile app/AssemblyConnectors.cs. Geometry-local connector frames for
// planar faces, straight edges, circular geometry and explicit origin/axis.
// Right-handed orthonormal frames, stable occurrence IDs, geometry fingerprints.
// Grounding and connector records live in the AS01 extension data of the
// occurrence (keys "grounded" and "connectors"); joints name connectors by ID
// ("connectorA"/"connectorB") while the canonical copy stays on the occurrence,
// so joint and occurrence can never drift apart. Inches, Z-up, radians.
// Never mutates a mesh. A changed/missing/ambiguous reference is diagnosed,
// never silently snapped to a different face.
//
// Also home to AssemblyHistory, the one-undo-step helper the assembly services
// (AS05/AS08/AS09/AS10) share: snapshot the document JSON, apply, register one
// EditHistory entry whose undo restores the snapshot.

public static class AssemblyConnectorKind
{
	public const string PlanarFace = "planar-face";
	public const string StraightEdge = "straight-edge";
	public const string Circular = "circular";
	public const string Explicit = "explicit";
	public static bool IsKnown( string kind ) => kind is PlanarFace or StraightEdge or Circular or Explicit;
}

public sealed class AssemblyFaceRecord
{
	public float[] Normal { get; set; } = new[] { 0f, 0f, 1f };
	public float[] Centroid { get; set; } = new[] { 0f, 0f, 0f };
	public float Area { get; set; }
}

public sealed class AssemblyGeometryFingerprint
{
	public float[] Normal { get; set; }
	public float[] Centroid { get; set; }
	public float Area { get; set; }
	public int FaceCount { get; set; }
	public List<AssemblyFaceRecord> Faces { get; set; }
	public string VertexHash { get; set; }
	public float[] PointA { get; set; }
	public float[] PointB { get; set; }
	public float Length { get; set; }
	public float[] Center { get; set; }
	public float Radius { get; set; }
	public int PointCount { get; set; }
	public List<float[]> RingPoints { get; set; }
}

public sealed class AssemblyConnectorSource
{
	public string SourceKind { get; set; } = "explicit";
	public string SourceReference { get; set; } = "";
	public string GeometryKey { get; set; } = "";
	public AssemblyGeometryFingerprint Fingerprint { get; set; } = new();
}

public sealed class AssemblyConnector
{
	public string Id { get; set; } = "";
	public string OccurrenceId { get; set; } = "";
	public string Name { get; set; } = "";
	public string Kind { get; set; } = AssemblyConnectorKind.Explicit;
	/// <summary>Rigid frame in occurrence-local coordinates; +Z is the primary axis.</summary>
	public AssemblyTransform LocalFrame { get; set; } = new();

	public AssemblyConnectorSource Source { get; set; } = new();

	[JsonExtensionData]
	public Dictionary<string, JsonElement> ExtensionData { get; set; }
}

public static class AssemblyConnectorJson
{
	public static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	public static string ToJson( AssemblyConnector connector ) =>
		JsonSerializer.Serialize( connector, Options );

	public static JsonNode ToJsonNode( AssemblyConnector connector ) =>
		JsonNode.Parse( ToJson( connector ) );

	/// <summary>Null on non-object or malformed input: a bad stored entry is skipped, never a crash.</summary>
	public static AssemblyConnector FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) return null;
		try { return JsonSerializer.Deserialize<AssemblyConnector>( json, Options ); }
		catch ( JsonException ) { return null; }
		catch ( NotSupportedException ) { return null; }
	}

	public static AssemblyConnector FromJson( JsonElement element )
	{
		if ( element.ValueKind != JsonValueKind.Object ) return null;
		return FromJson( element.GetRawText() );
	}
}

public static class AssemblyGrounding
{
	public const string GroundedKey = "grounded";

	public static bool IsGrounded( AssemblyOccurrence occurrence ) =>
		occurrence?.ExtensionData != null
		&& occurrence.ExtensionData.TryGetValue( GroundedKey, out var value )
		&& value.ValueKind == JsonValueKind.True;

	public static bool IsGrounded( AssemblyDocument document, string occurrenceId )
	{
		if ( document == null || string.IsNullOrEmpty( occurrenceId ) ) return false;
		var occurrence = document.Occurrences?.FirstOrDefault( o => o != null && o.Id == occurrenceId );
		return occurrence != null && IsGrounded( occurrence );
	}

	/// <summary>False when the occurrence is unknown. Idempotent: setting the current
	/// value writes nothing. Clearing removes the key (and nulls ExtensionData when
	/// empty) so a transiently-used document keeps HasData == false.</summary>
	public static bool SetGrounded( AssemblyDocument document, string occurrenceId, bool grounded )
	{
		if ( document == null ) return false;
		var occurrence = document.Occurrences?.FirstOrDefault( o => o != null && o.Id == occurrenceId );
		if ( occurrence == null ) return false;
		if ( IsGrounded( occurrence ) == grounded ) return true;
		if ( grounded )
		{
			occurrence.ExtensionData ??= new Dictionary<string, JsonElement>();
			occurrence.ExtensionData[GroundedKey] = JsonSerializer.SerializeToElement( true );
		}
		else
		{
			occurrence.ExtensionData?.Remove( GroundedKey );
			if ( occurrence.ExtensionData?.Count == 0 ) occurrence.ExtensionData = null;
		}
		return true;
	}

	public static IReadOnlyList<AssemblyOccurrence> GroundedOccurrences( AssemblyDocument document )
	{
		if ( document?.Occurrences == null ) return Array.Empty<AssemblyOccurrence>();
		return document.Occurrences.Where( o => o != null && IsGrounded( o ) ).ToList();
	}

	/// <summary>First grounded occurrence in document order, else first occurrence,
	/// else null. "No grounded part" is not drift: the caller anchors on this root.</summary>
	public static AssemblyOccurrence ChooseRoot( AssemblyDocument document )
	{
		if ( document?.Occurrences == null ) return null;
		foreach ( var occurrence in document.Occurrences )
			if ( occurrence != null && IsGrounded( occurrence ) ) return occurrence;
		return document.Occurrences.FirstOrDefault( o => o != null );
	}

	public static bool IsMovable( AssemblyDocument document, string occurrenceId )
	{
		if ( document?.Occurrences == null ) return false;
		var occurrence = document.Occurrences.FirstOrDefault( o => o != null && o.Id == occurrenceId );
		return occurrence != null && !IsGrounded( occurrence );
	}

	public static IReadOnlyList<AssemblyValidationIssue> ValidateGrounding( AssemblyDocument document )
	{
		var issues = new List<AssemblyValidationIssue>();
		if ( document?.Occurrences == null ) return issues;
		foreach ( var occurrence in document.Occurrences )
		{
			if ( occurrence?.ExtensionData == null ) continue;
			if ( occurrence.ExtensionData.TryGetValue( GroundedKey, out var value )
				&& value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False )
				issues.Add( new AssemblyValidationIssue( "invalid-grounding", occurrence.Id ?? "",
					"Grounded flag must be a JSON boolean." ) );
		}
		return issues;
	}
}

public static class AssemblyConnectorStore
{
	public const string ConnectorsKey = "connectors";
	public const string ConnectorAKey = "connectorA";
	public const string ConnectorBKey = "connectorB";
	public const int MaxConnectorsPerOccurrence = 64;

	public static IReadOnlyList<AssemblyConnector> GetConnectors( AssemblyOccurrence occurrence )
	{
		TryGetConnectors( occurrence, out var connectors, out _ );
		return connectors;
	}

	/// <summary>Malformed stored entries are skipped and counted, never fatal.
	/// A non-array "connectors" value counts as skipped = 1.</summary>
	public static bool TryGetConnectors( AssemblyOccurrence occurrence, out List<AssemblyConnector> connectors, out int skipped )
	{
		connectors = new List<AssemblyConnector>();
		skipped = 0;
		if ( occurrence?.ExtensionData == null ) return true;
		if ( !occurrence.ExtensionData.TryGetValue( ConnectorsKey, out var value ) ) return true;
		if ( value.ValueKind != JsonValueKind.Array ) { skipped = 1; return false; }
		foreach ( var item in value.EnumerateArray() )
		{
			var connector = AssemblyConnectorJson.FromJson( item );
			if ( connector == null ) skipped++;
			else connectors.Add( connector );
		}
		return skipped == 0;
	}

	/// <summary>An empty list removes the key entirely.</summary>
	public static void SetConnectors( AssemblyOccurrence occurrence, IReadOnlyList<AssemblyConnector> connectors )
	{
		if ( occurrence == null ) throw new ArgumentNullException( nameof( occurrence ) );
		if ( connectors == null || connectors.Count == 0 )
		{
			occurrence.ExtensionData?.Remove( ConnectorsKey );
			if ( occurrence.ExtensionData?.Count == 0 ) occurrence.ExtensionData = null;
			return;
		}
		occurrence.ExtensionData ??= new Dictionary<string, JsonElement>();
		occurrence.ExtensionData[ConnectorsKey] = JsonSerializer.SerializeToElement( connectors, AssemblyConnectorJson.Options );
	}

	/// <summary>Add or replace by Id. False when the occurrence is missing, or the
	/// list is full and the Id is not already present.</summary>
	public static bool AddConnector( AssemblyDocument document, AssemblyConnector connector )
	{
		if ( document == null || connector == null || string.IsNullOrEmpty( connector.OccurrenceId ) ) return false;
		var occurrence = document.Occurrences?.FirstOrDefault( o => o != null && o.Id == connector.OccurrenceId );
		if ( occurrence == null ) return false;
		TryGetConnectors( occurrence, out var connectors, out _ );
		var index = connectors.FindIndex( c => c.Id == connector.Id );
		if ( index >= 0 ) connectors[index] = connector;
		else
		{
			if ( connectors.Count >= MaxConnectorsPerOccurrence ) return false;
			connectors.Add( connector );
		}
		SetConnectors( occurrence, connectors );
		return true;
	}

	public static bool RemoveConnector( AssemblyDocument document, string connectorId )
	{
		if ( document?.Occurrences == null || string.IsNullOrEmpty( connectorId ) ) return false;
		foreach ( var occurrence in document.Occurrences )
		{
			if ( occurrence == null ) continue;
			TryGetConnectors( occurrence, out var connectors, out _ );
			var index = connectors.FindIndex( c => c.Id == connectorId );
			if ( index < 0 ) continue;
			connectors.RemoveAt( index );
			SetConnectors( occurrence, connectors );
			return true;
		}
		return false;
	}

	public static AssemblyConnector FindConnector( AssemblyDocument document, string connectorId )
	{
		if ( document?.Occurrences == null || string.IsNullOrEmpty( connectorId ) ) return null;
		foreach ( var occurrence in document.Occurrences )
		{
			if ( occurrence == null ) continue;
			TryGetConnectors( occurrence, out var connectors, out _ );
			var found = connectors.FirstOrDefault( c => c.Id == connectorId );
			if ( found != null ) return found;
		}
		return null;
	}

	public static IReadOnlyList<AssemblyConnector> AllConnectors( AssemblyDocument document )
	{
		var all = new List<AssemblyConnector>();
		if ( document?.Occurrences == null ) return all;
		foreach ( var occurrence in document.Occurrences )
		{
			if ( occurrence == null ) continue;
			TryGetConnectors( occurrence, out var connectors, out _ );
			all.AddRange( connectors );
		}
		return all;
	}

	/// <summary>Side "A" or "B" only. False for a bad side or an unstored connector:
	/// the joint may only name a connector that lives on an occurrence.</summary>
	public static bool AttachToJoint( AssemblyDocument document, AssemblyJoint joint, string side, string connectorId )
	{
		if ( document == null || joint == null ) return false;
		if ( side != "A" && side != "B" ) return false;
		var connector = FindConnector( document, connectorId );
		if ( connector == null ) return false;
		var expectedOccurrence = side == "A" ? joint.OccurrenceA : joint.OccurrenceB;
		if ( connector.OccurrenceId != expectedOccurrence ) return false;
		joint.ExtensionData ??= new Dictionary<string, JsonElement>();
		joint.ExtensionData[side == "A" ? ConnectorAKey : ConnectorBKey] =
			JsonSerializer.SerializeToElement( new Dictionary<string, string> { ["connectorId"] = connectorId } );
		return true;
	}

	public static AssemblyConnector ConnectorForJoint( AssemblyDocument document, AssemblyJoint joint, string side )
	{
		var id = ConnectorIdForJoint( joint, side );
		return id == null ? null : FindConnector( document, id );
	}

	public static string ConnectorIdForJoint( AssemblyJoint joint, string side )
	{
		if ( joint?.ExtensionData == null ) return null;
		var key = side == "A" ? ConnectorAKey : ConnectorBKey;
		if ( side != "A" && side != "B" ) return null;
		if ( !joint.ExtensionData.TryGetValue( key, out var value ) ) return null;
		if ( value.ValueKind != JsonValueKind.Object ) return null;
		if ( !value.TryGetProperty( "connectorId", out var id ) ) return null;
		return id.ValueKind == JsonValueKind.String ? id.GetString() : null;
	}

	public static void DetachFromJoint( AssemblyJoint joint, string side )
	{
		if ( joint?.ExtensionData == null ) return;
		if ( side != "A" && side != "B" ) return;
		joint.ExtensionData.Remove( side == "A" ? ConnectorAKey : ConnectorBKey );
		if ( joint.ExtensionData.Count == 0 ) joint.ExtensionData = null;
	}
}

public sealed class AssemblySpaceFrame
{
	public Xform Frame { get; set; }
	public float Determinant => Frame.Determinant;
	public bool Mirrored => Determinant < 0f;
	/// <summary>Not mirrored, orthonormal, determinant ~1 (eps 1e-4).</summary>
	public bool Rigid
	{
		get
		{
			const float eps = 1e-4f;
			return !Mirrored
				&& MathF.Abs( Frame.X.LengthSquared - 1f ) <= eps
				&& MathF.Abs( Frame.Y.LengthSquared - 1f ) <= eps
				&& MathF.Abs( Frame.Z.LengthSquared - 1f ) <= eps
				&& MathF.Abs( Vec3.Dot( Frame.X, Frame.Y ) ) <= eps
				&& MathF.Abs( Vec3.Dot( Frame.Y, Frame.Z ) ) <= eps
				&& MathF.Abs( Vec3.Dot( Frame.Z, Frame.X ) ) <= eps
				&& MathF.Abs( Determinant - 1f ) <= eps;
		}
	}
}

public static class AssemblyConnectorFrames
{
	/// <summary>Assembly-space frame = occurrence transform * local frame. Null when malformed.</summary>
	public static AssemblySpaceFrame ToAssemblySpace( AssemblyTransform occurrenceTransform, AssemblyTransform localFrame )
	{
		if ( occurrenceTransform == null || localFrame == null ) return null;
		try
		{
			return new AssemblySpaceFrame { Frame = occurrenceTransform.ToXform() * localFrame.ToXform() };
		}
		catch ( InvalidOperationException ) { return null; }
	}

	public static bool TryFrameInAssembly( AssemblyDocument document, AssemblyConnector connector, out AssemblySpaceFrame frame, out string why )
	{
		frame = null;
		why = null;
		if ( document == null || connector == null )
		{
			why = "No connector was given.";
			return false;
		}
		var occurrence = document.Occurrences?.FirstOrDefault( o => o != null && o.Id == connector.OccurrenceId );
		if ( occurrence == null )
		{
			why = $"Occurrence '{connector.OccurrenceId}' does not exist.";
			return false;
		}
		frame = ToAssemblySpace( occurrence.Transform, connector.LocalFrame );
		if ( frame == null )
		{
			why = "The occurrence or connector frame is malformed.";
			return false;
		}
		return true;
	}

	/// <summary>Local frame = occurrence^-1 * assembly frame. Fails on a singular occurrence.</summary>
	public static bool TryToLocalSpace( AssemblyTransform occurrenceTransform, AssemblyTransform assemblyFrame, out AssemblyTransform localFrame, out string why )
	{
		localFrame = null;
		why = null;
		if ( occurrenceTransform == null || assemblyFrame == null )
		{
			why = "The occurrence or assembly frame is missing.";
			return false;
		}
		try
		{
			localFrame = AssemblyTransform.FromXform( occurrenceTransform.ToXform().Inverse * assemblyFrame.ToXform() );
			return true;
		}
		catch ( InvalidOperationException e )
		{
			why = "The occurrence transform is singular and cannot be inverted.";
			_ = e;
			return false;
		}
	}

	/// <summary>Builds a rigid frame from an origin and a primary (+Z) axis. The X
	/// hint is Gram-Schmidt orthogonalised; when it runs parallel to Z a world axis
	/// is substituted, so the result is deterministic. Y = Z x X.</summary>
	public static bool TryFrameFromOriginAndAxis( Vec3 origin, Vec3 zAxis, Vec3? xHint, out AssemblyTransform frame, out string why )
	{
		frame = null;
		why = null;
		if ( zAxis.LengthSquared < 1e-12f )
		{
			why = "The primary axis is zero-length.";
			return false;
		}
		var z = zAxis.Normal;
		var x = xHint ?? new Vec3( 1, 0, 0 );
		if ( x.LengthSquared < 1e-12f ) x = new Vec3( 1, 0, 0 );
		x -= z * Vec3.Dot( x, z );
		if ( x.LengthSquared < 1e-12f )
		{
			x = MathF.Abs( z.x ) > 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
			x -= z * Vec3.Dot( x, z );
		}
		x = x.Normal;
		var y = Vec3.Cross( z, x );
		frame = AssemblyTransform.FromXform( new Xform( x, y, z, origin ) );
		return true;
	}

	public static Vec3 FrameOrigin( AssemblyTransform frame )
	{
		if ( frame == null ) return Vec3.Zero;
		try { return frame.ToXform().Origin; }
		catch ( InvalidOperationException ) { return Vec3.Zero; }
	}

	public static Vec3 FramePrimaryAxis( AssemblyTransform frame )
	{
		if ( frame == null ) return Vec3.Zero;
		try { return frame.ToXform().Z; }
		catch ( InvalidOperationException ) { return Vec3.Zero; }
	}
}

public static class AssemblyConnectorGeometry
{
	public const float PositionTolerance = 1e-3f;
	public const float NormalDotTolerance = 0.999f;
	public const float AreaRelativeTolerance = 0.01f;
	public const int MaxPlanarFaces = 64;
	public const int MaxRingPoints = 256;

	static float[] V( Vec3 v ) => new[] { v.x, v.y, v.z };
	static Vec3 FromArray( float[] values ) => values != null && values.Length == 3
		? new Vec3( values[0], values[1], values[2] ) : Vec3.Zero;

	public static bool TryPlanarFaces( PolyMesh mesh, IReadOnlyList<int> faceIndices, string occurrenceId, string sourceReference, string name, out AssemblyConnector connector, out string why )
	{
		connector = null;
		why = null;
		if ( mesh == null ) { why = "The part has no mesh to read faces from."; return false; }
		if ( faceIndices == null || faceIndices.Count == 0 ) { why = "No faces were selected."; return false; }
		if ( faceIndices.Count > MaxPlanarFaces ) { why = $"Too many faces were selected ({faceIndices.Count} > {MaxPlanarFaces})."; return false; }
		var totalArea = 0f;
		var weightedNormal = Vec3.Zero;
		var weightedCentroid = Vec3.Zero;
		var records = new List<AssemblyFaceRecord>();
		var vertexIndices = new HashSet<int>();
		foreach ( var faceIndex in faceIndices )
		{
			if ( faceIndex < 0 || faceIndex >= mesh.Faces.Count ) { why = $"Face {faceIndex} does not exist on this part."; return false; }
			var face = mesh.Faces[faceIndex];
			var normal = mesh.FaceNormal( face );
			var area = mesh.FaceArea( face, normal );
			if ( normal.LengthSquared < 1e-12f || area <= 0f ) { why = $"Face {faceIndex} is degenerate and has no plane."; return false; }
			var centroid = mesh.FaceCentroid( face );
			totalArea += area;
			weightedNormal += normal * area;
			weightedCentroid += centroid * area;
			records.Add( new AssemblyFaceRecord { Normal = V( normal ), Centroid = V( centroid ), Area = area } );
			foreach ( var v in face.Indices ) vertexIndices.Add( v );
		}
		if ( totalArea <= 0f ) { why = "The selected faces have no area."; return false; }
		var meanNormal = weightedNormal.Normal;
		var meanCentroid = weightedCentroid / totalArea;
		foreach ( var record in records )
		{
			var normal = FromArray( record.Normal );
			var centroid = FromArray( record.Centroid );
			if ( Vec3.Dot( normal, meanNormal ) < NormalDotTolerance ) { why = "The selected faces are not coplanar, so they have no single connector plane."; return false; }
			if ( MathF.Abs( Vec3.Dot( centroid - meanCentroid, meanNormal ) ) > PositionTolerance ) { why = "The selected faces are not coplanar, so they have no single connector plane."; return false; }
		}
		var firstFace = mesh.Faces[faceIndices[0]];
		var hint = mesh.Positions[firstFace.Indices[0]] - meanCentroid;
		if ( !AssemblyConnectorFrames.TryFrameFromOriginAndAxis( meanCentroid, meanNormal, hint, out var frame, out why ) ) return false;
		var positions = vertexIndices.Select( i => mesh.Positions[i] ).ToList();
		connector = new AssemblyConnector
		{
			Id = AssemblyDocument.NewId(), OccurrenceId = occurrenceId, Name = name,
			Kind = AssemblyConnectorKind.PlanarFace, LocalFrame = frame,
			Source = new AssemblyConnectorSource
			{
				SourceKind = "effigy-body", SourceReference = sourceReference ?? "",
				GeometryKey = "planar-face",
				Fingerprint = new AssemblyGeometryFingerprint
				{
					Normal = V( meanNormal ), Centroid = V( meanCentroid ), Area = totalArea,
					FaceCount = faceIndices.Count, Faces = records, VertexHash = HashPositions( positions ),
				},
			},
		};
		return true;
	}

	public static bool TryStraightEdge( PolyMesh mesh, int vertexA, int vertexB, string occurrenceId, string sourceReference, string name, out AssemblyConnector connector, out string why )
	{
		connector = null;
		why = null;
		if ( mesh == null ) { why = "The part has no mesh to read edges from."; return false; }
		if ( vertexA < 0 || vertexA >= mesh.Positions.Count || vertexB < 0 || vertexB >= mesh.Positions.Count ) { why = "The edge endpoints do not exist on this part."; return false; }
		if ( vertexA == vertexB ) { why = "The edge endpoints must be two different vertices."; return false; }
		var edges = mesh.BuildEdgeFaces();
		var key = new EdgeKey( vertexA, vertexB );
		if ( !edges.TryGetValue( key, out var faces ) || faces == null || faces.Count == 0 ) { why = "The two vertices do not share a mesh edge."; return false; }
		var pointA = mesh.Positions[vertexA];
		var pointB = mesh.Positions[vertexB];
		var direction = pointB - pointA;
		if ( direction.Length <= PositionTolerance ) { why = "The edge is too short to define a direction."; return false; }
		var hint = Vec3.Zero;
		foreach ( var faceIndex in faces ) hint += mesh.FaceNormal( mesh.Faces[faceIndex] );
		var origin = ( pointA + pointB ) * 0.5f;
		if ( !AssemblyConnectorFrames.TryFrameFromOriginAndAxis( origin, direction, hint, out var frame, out why ) ) return false;
		connector = new AssemblyConnector
		{
			Id = AssemblyDocument.NewId(), OccurrenceId = occurrenceId, Name = name,
			Kind = AssemblyConnectorKind.StraightEdge, LocalFrame = frame,
			Source = new AssemblyConnectorSource
			{
				SourceKind = "effigy-body", SourceReference = sourceReference ?? "",
				GeometryKey = "straight-edge",
				Fingerprint = new AssemblyGeometryFingerprint
				{
					Normal = V( direction.Normal ), Centroid = V( origin ),
					PointA = V( pointA ), PointB = V( pointB ), Length = direction.Length,
					VertexHash = HashPositions( new[] { pointA, pointB } ),
				},
			},
		};
		return true;
	}

	public static bool TryCircularLoop( PolyMesh mesh, IReadOnlyList<int> vertexIndices, string occurrenceId, string sourceReference, string name, out AssemblyConnector connector, out string why )
	{
		connector = null;
		why = null;
		if ( mesh == null ) { why = "The part has no mesh to read a ring from."; return false; }
		if ( vertexIndices == null || vertexIndices.Count < 6 ) { why = "A circular connector needs at least 6 ring vertices."; return false; }
		if ( vertexIndices.Count > MaxRingPoints ) { why = $"Too many ring vertices ({vertexIndices.Count} > {MaxRingPoints})."; return false; }
		var points = new List<Vec3>( vertexIndices.Count );
		foreach ( var index in vertexIndices )
		{
			if ( index < 0 || index >= mesh.Positions.Count ) { why = $"Ring vertex {index} does not exist on this part."; return false; }
			points.Add( mesh.Positions[index] );
		}
		if ( !FitCircle( points, out var center, out var normal, out var radius, out why ) ) return false;
		var hint = points[0] - center;
		if ( !AssemblyConnectorFrames.TryFrameFromOriginAndAxis( center, normal, hint, out var frame, out why ) ) return false;
		connector = new AssemblyConnector
		{
			Id = AssemblyDocument.NewId(), OccurrenceId = occurrenceId, Name = name,
			Kind = AssemblyConnectorKind.Circular, LocalFrame = frame,
			Source = new AssemblyConnectorSource
			{
				SourceKind = "effigy-body", SourceReference = sourceReference ?? "",
				GeometryKey = "circular",
				Fingerprint = new AssemblyGeometryFingerprint
				{
					Normal = V( normal ), Center = V( center ), Radius = radius,
					PointCount = points.Count, RingPoints = points.Select( p => V( p ) ).ToList(),
					VertexHash = HashPositions( points ),
				},
			},
		};
		return true;
	}

	public static bool TryExplicit( string occurrenceId, string name, Vec3 origin, Vec3 zAxis, Vec3? xHint, out AssemblyConnector connector, out string why )
	{
		connector = null;
		why = null;
		if ( !AssemblyConnectorFrames.TryFrameFromOriginAndAxis( origin, zAxis, xHint, out var frame, out why ) ) return false;
		connector = new AssemblyConnector
		{
			Id = AssemblyDocument.NewId(), OccurrenceId = occurrenceId, Name = name,
			Kind = AssemblyConnectorKind.Explicit, LocalFrame = frame,
			Source = new AssemblyConnectorSource { SourceKind = "explicit", SourceReference = "", GeometryKey = "explicit" },
		};
		return true;
	}

	/// <summary>Circle fit: centre = mean, normal = Newell (ring winding decides its
	/// sign), radius = mean in-plane distance. Fails on non-coplanar or inconsistent rings.</summary>
	public static bool FitCircle( IReadOnlyList<Vec3> points, out Vec3 center, out Vec3 normal, out float radius, out string why )
	{
		center = Vec3.Zero;
		normal = Vec3.Zero;
		radius = 0f;
		why = null;
		if ( points == null || points.Count < 3 ) { why = "A circle needs at least 3 points."; return false; }
		foreach ( var p in points ) center += p;
		center /= points.Count;
		var newell = Vec3.Zero;
		for ( var i = 0; i < points.Count; i++ )
		{
			var a = points[i] - center;
			var b = points[( i + 1 ) % points.Count] - center;
			newell += new Vec3(
				( a.y - b.y ) * ( a.z + b.z ),
				( a.z - b.z ) * ( a.x + b.x ),
				( a.x - b.x ) * ( a.y + b.y ) );
		}
		if ( newell.LengthSquared < 1e-12f ) { why = "The ring has no plane to fit a circle in."; return false; }
		normal = newell.Normal;
		var radii = new List<float>( points.Count );
		foreach ( var p in points )
		{
			var d = p - center;
			var offPlane = MathF.Abs( Vec3.Dot( d, normal ) );
			if ( offPlane > PositionTolerance ) { why = "The ring vertices are not coplanar, so they have no single circle."; return false; }
			var inPlane = d - normal * Vec3.Dot( d, normal );
			radii.Add( inPlane.Length );
		}
		radius = radii.Sum() / radii.Count;
		if ( radius <= PositionTolerance ) { why = "The ring is too small to define a circle."; return false; }
		var allowed = MathF.Max( PositionTolerance, radius * AreaRelativeTolerance );
		foreach ( var r in radii )
			if ( MathF.Abs( r - radius ) > allowed ) { why = "The ring radii disagree, so the points do not lie on one circle."; return false; }
		return true;
	}

	/// <summary>Deterministic FNV-1a hex (16 chars) over positions quantized to 1e-3 in,
	/// sorted first so the hash does not depend on vertex order.</summary>
	public static string HashPositions( IEnumerable<Vec3> positions )
	{
		const ulong offset = 14695981039346656037UL;
		const ulong prime = 1099511628211UL;
		var hash = offset;
		var quantized = new List<(int X, int Y, int Z)>();
		foreach ( var p in positions )
			quantized.Add( ( (int)MathF.Round( p.x * 1000f ), (int)MathF.Round( p.y * 1000f ), (int)MathF.Round( p.z * 1000f ) ) );
		quantized.Sort();
		foreach ( var (x, y, z) in quantized )
		{
			foreach ( var value in new[] { x, y, z } )
			{
				var bytes = BitConverter.GetBytes( value );
				foreach ( var b in bytes )
				{
					hash ^= b;
					hash *= prime;
				}
			}
		}
		return hash.ToString( "x16" );
	}
}

public enum AssemblyConnectorStatus
{
	Resolved,
	Missing,
	Changed,
	Ambiguous,
	Invalid,
	Unresolvable,
}

public sealed class AssemblyConnectorResolution
{
	public AssemblyConnectorStatus Status { get; set; }
	public string Detail { get; set; } = "";
	/// <summary>The frame current geometry would give, when one was found.</summary>
	public AssemblyTransform ObservedFrame { get; set; }
}

public static class AssemblyConnectorResolver
{
	/// <summary>Re-resolves a stored connector against current geometry. Never mutates
	/// the connector or the mesh. Changed/missing/ambiguous never rewrite the stored
	/// connector: the user reselects; never silently snap to a different face.</summary>
	public static AssemblyConnectorResolution Resolve( AssemblyConnector connector, PolyMesh mesh )
	{
		if ( connector == null )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Invalid, Detail = "Connector is missing." };
		if ( !AssemblyConnectorKind.IsKnown( connector.Kind ) )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Unresolvable, Detail = $"Connector kind '{connector.Kind}' is preserved but not supported by this reader." };
		string frameWhy = null!;
		if ( connector.LocalFrame == null || !connector.LocalFrame.IsRigidFrame( out frameWhy ) )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Invalid, Detail = $"Connector frame must be a finite rigid transform: {frameWhy}" };
		if ( connector.Kind == AssemblyConnectorKind.Explicit )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Resolved, Detail = "Explicit connector has no geometry to go stale." };
		if ( mesh == null || mesh.Positions.Count == 0 )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Missing, Detail = "The part has no mesh to resolve against." };

		return connector.Kind switch
		{
			AssemblyConnectorKind.PlanarFace => ResolvePlanar( connector, mesh ),
			AssemblyConnectorKind.StraightEdge => ResolveEdge( connector, mesh ),
			AssemblyConnectorKind.Circular => ResolveCircular( connector, mesh ),
			_ => new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Unresolvable, Detail = $"Connector kind '{connector.Kind}' is preserved but not supported by this reader." },
		};
	}

	static AssemblyConnectorResolution ResolvePlanar( AssemblyConnector connector, PolyMesh mesh )
	{
		var fingerprint = connector.Source?.Fingerprint;
		if ( fingerprint?.Faces == null || fingerprint.Faces.Count == 0 )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Invalid, Detail = "Planar connector has no stored faces to match." };
		var matched = new List<int>();
		var usedCurrent = new HashSet<int>();
		foreach ( var stored in fingerprint.Faces )
		{
			if ( stored.Normal?.Length != 3 || stored.Centroid?.Length != 3 )
				return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Invalid, Detail = "Planar connector has a malformed stored face." };
			var normal = new Vec3( stored.Normal[0], stored.Normal[1], stored.Normal[2] );
			var centroid = new Vec3( stored.Centroid[0], stored.Centroid[1], stored.Centroid[2] );
			var hits = new List<int>();
			for ( var i = 0; i < mesh.Faces.Count; i++ )
			{
				var face = mesh.Faces[i];
				var currentNormal = mesh.FaceNormal( face );
				var currentArea = mesh.FaceArea( face, currentNormal );
				var currentCentroid = mesh.FaceCentroid( face );
				if ( Vec3.Dot( currentNormal, normal ) < AssemblyConnectorGeometry.NormalDotTolerance ) continue;
				if ( ( currentCentroid - centroid ).Length > AssemblyConnectorGeometry.PositionTolerance ) continue;
				var allowed = MathF.Max( AssemblyConnectorGeometry.PositionTolerance, stored.Area * AssemblyConnectorGeometry.AreaRelativeTolerance );
				if ( MathF.Abs( currentArea - stored.Area ) > allowed ) continue;
				hits.Add( i );
			}
			if ( hits.Count == 0 )
				return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Changed, Detail = "A stored face no longer matches any face on the part." };
			if ( hits.Count > 1 )
				return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Ambiguous, Detail = "More than one face matches a stored face." };
			if ( !usedCurrent.Add( hits[0] ) )
				return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Ambiguous, Detail = "Two stored faces match the same current face." };
			matched.Add( hits[0] );
		}
		if ( !AssemblyConnectorGeometry.TryPlanarFaces( mesh, matched, connector.OccurrenceId, connector.Source.SourceReference, connector.Name, out var rebuilt, out _ ) )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Changed, Detail = "The matched faces no longer form one connector plane." };
		if ( FramesAgree( rebuilt.LocalFrame, connector.LocalFrame ) )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Resolved, Detail = "Connector matches its stored geometry." };
		return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Changed, Detail = "The connector plane moved since it was stored.", ObservedFrame = rebuilt.LocalFrame };
	}

	static AssemblyConnectorResolution ResolveEdge( AssemblyConnector connector, PolyMesh mesh )
	{
		var fingerprint = connector.Source?.Fingerprint;
		if ( fingerprint?.PointA?.Length != 3 || fingerprint?.PointB?.Length != 3 )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Invalid, Detail = "Edge connector has no stored endpoints to match." };
		var a = new Vec3( fingerprint.PointA[0], fingerprint.PointA[1], fingerprint.PointA[2] );
		var b = new Vec3( fingerprint.PointB[0], fingerprint.PointB[1], fingerprint.PointB[2] );
		var edges = mesh.BuildEdgeFaces();
		var hits = new List<(int, int)>();
		foreach ( var key in edges.Keys )
		{
			var pa = mesh.Positions[key.A];
			var pb = mesh.Positions[key.B];
			if ( ( ( pa - a ).Length <= AssemblyConnectorGeometry.PositionTolerance && ( pb - b ).Length <= AssemblyConnectorGeometry.PositionTolerance )
				|| ( ( pa - b ).Length <= AssemblyConnectorGeometry.PositionTolerance && ( pb - a ).Length <= AssemblyConnectorGeometry.PositionTolerance ) )
				hits.Add( (key.A, key.B) );
		}
		if ( hits.Count == 0 )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Changed, Detail = "The stored edge no longer matches any edge on the part." };
		if ( hits.Count > 1 )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Ambiguous, Detail = "More than one edge matches the stored edge." };
		if ( !AssemblyConnectorGeometry.TryStraightEdge( mesh, hits[0].Item1, hits[0].Item2, connector.OccurrenceId, connector.Source.SourceReference, connector.Name, out var rebuilt, out _ ) )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Changed, Detail = "The matched edge no longer forms a connector." };
		if ( FramesAgree( rebuilt.LocalFrame, connector.LocalFrame ) )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Resolved, Detail = "Connector matches its stored geometry." };
		return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Changed, Detail = "The connector edge moved since it was stored.", ObservedFrame = rebuilt.LocalFrame };
	}

	static AssemblyConnectorResolution ResolveCircular( AssemblyConnector connector, PolyMesh mesh )
	{
		var fingerprint = connector.Source?.Fingerprint;
		if ( fingerprint?.RingPoints == null || fingerprint.RingPoints.Count == 0 )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Invalid, Detail = "Circular connector has no stored ring to match." };
		var used = new HashSet<int>();
		var matched = new List<Vec3>();
		foreach ( var stored in fingerprint.RingPoints )
		{
			if ( stored?.Length != 3 )
				return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Invalid, Detail = "Circular connector has a malformed stored ring point." };
			var point = new Vec3( stored[0], stored[1], stored[2] );
			var best = -1;
			var bestDistance = float.MaxValue;
			for ( var i = 0; i < mesh.Positions.Count; i++ )
			{
				if ( used.Contains( i ) ) continue;
				var distance = ( mesh.Positions[i] - point ).Length;
				if ( distance < bestDistance ) { bestDistance = distance; best = i; }
			}
			if ( best < 0 || bestDistance > AssemblyConnectorGeometry.PositionTolerance )
				return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Changed, Detail = "A stored ring point no longer matches any vertex on the part." };
			used.Add( best );
			matched.Add( mesh.Positions[best] );
		}
		if ( !AssemblyConnectorGeometry.FitCircle( matched, out var center, out var normal, out var radius, out _ ) )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Changed, Detail = "The matched ring no longer fits one circle." };
		var storedCenter = fingerprint.Center?.Length != 3 ? (Vec3?)null : new Vec3( fingerprint.Center[0], fingerprint.Center[1], fingerprint.Center[2] );
		var storedNormal = fingerprint.Normal?.Length != 3 ? (Vec3?)null : new Vec3( fingerprint.Normal[0], fingerprint.Normal[1], fingerprint.Normal[2] );
		if ( storedCenter == null || storedNormal == null )
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Invalid, Detail = "Circular connector has a malformed stored circle." };
		var centerDrift = ( center - storedCenter.Value ).Length;
		var radiusDrift = MathF.Abs( radius - fingerprint.Radius );
		var radiusAllowed = MathF.Max( AssemblyConnectorGeometry.PositionTolerance, fingerprint.Radius * AssemblyConnectorGeometry.AreaRelativeTolerance );
		if ( centerDrift > AssemblyConnectorGeometry.PositionTolerance || Vec3.Dot( normal, storedNormal.Value ) < AssemblyConnectorGeometry.NormalDotTolerance || radiusDrift > radiusAllowed )
		{
			AssemblyConnectorFrames.TryFrameFromOriginAndAxis( center, normal, matched[0] - center, out var observed, out _ );
			return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Changed, Detail = "The connector circle moved since it was stored.", ObservedFrame = observed };
		}
		return new AssemblyConnectorResolution { Status = AssemblyConnectorStatus.Resolved, Detail = "Connector matches its stored geometry." };
	}

	static bool FramesAgree( AssemblyTransform a, AssemblyTransform b )
	{
		if ( a == null || b == null ) return false;
		try
		{
			var xa = a.ToXform();
			var xb = b.ToXform();
			return ( xa.Origin - xb.Origin ).Length <= AssemblyConnectorGeometry.PositionTolerance
				&& Vec3.Dot( xa.Z, xb.Z ) >= AssemblyConnectorGeometry.NormalDotTolerance
				&& Vec3.Dot( xa.X, xb.X ) >= AssemblyConnectorGeometry.NormalDotTolerance;
		}
		catch ( InvalidOperationException ) { return false; }
	}
}

public static class AssemblyConnectorValidation
{
	/// <summary>Structural checks always run; geometric re-resolution only runs when
	/// a mesh provider is supplied. Returns issues; empty means clean.</summary>
	public static IReadOnlyList<AssemblyValidationIssue> Validate( AssemblyDocument document, Func<string, PolyMesh> meshByOccurrenceId = null )
	{
		var issues = new List<AssemblyValidationIssue>();
		if ( document == null ) return issues;
		issues.AddRange( AssemblyGrounding.ValidateGrounding( document ) );
		var occurrences = document.Occurrences ?? new List<AssemblyOccurrence>();
		foreach ( var occurrence in occurrences )
		{
			if ( occurrence == null ) continue;
			AssemblyConnectorStore.TryGetConnectors( occurrence, out var connectors, out var skipped );
			if ( skipped > 0 )
				issues.Add( new AssemblyValidationIssue( "invalid-connector", occurrence.Id ?? "", $"{skipped} stored connector entr{( skipped == 1 ? "y is" : "ies are" )} malformed and were skipped." ) );
			foreach ( var connector in connectors )
			{
				if ( connector.OccurrenceId != occurrence.Id )
					issues.Add( new AssemblyValidationIssue( "connector-occurrence-mismatch", connector.Id ?? "", $"Connector '{connector.Name}' names occurrence '{connector.OccurrenceId}' but is stored on '{occurrence.Id}'." ) );
				if ( !AssemblyConnectorKind.IsKnown( connector.Kind ) )
				{
					issues.Add( new AssemblyValidationIssue( "unresolvable-connector", connector.Id ?? "", $"Connector kind '{connector.Kind}' is preserved but not supported by this reader.", Warning: true ) );
					continue;
				}
				string frameWhy = null!;
		if ( connector.LocalFrame == null || !connector.LocalFrame.IsRigidFrame( out frameWhy ) )
				{
					issues.Add( new AssemblyValidationIssue( "invalid-connector-frame", connector.Id ?? "", $"Connector frame must be a finite rigid transform: {frameWhy}" ) );
					continue;
				}
				if ( connector.Kind != AssemblyConnectorKind.Explicit && string.IsNullOrWhiteSpace( connector.Source?.SourceReference ) )
					issues.Add( new AssemblyValidationIssue( "missing-source", connector.Id ?? "", "Geometry connector has no source reference." ) );
				if ( meshByOccurrenceId == null ) continue;
				var mesh = meshByOccurrenceId( occurrence.Id );
				var resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
				switch ( resolution.Status )
				{
					case AssemblyConnectorStatus.Missing:
						issues.Add( new AssemblyValidationIssue( "missing-source", connector.Id ?? "", resolution.Detail ) );
						break;
					case AssemblyConnectorStatus.Changed:
						issues.Add( new AssemblyValidationIssue( "changed-source", connector.Id ?? "", resolution.Detail ) );
						break;
					case AssemblyConnectorStatus.Ambiguous:
						issues.Add( new AssemblyValidationIssue( "ambiguous-source", connector.Id ?? "", resolution.Detail ) );
						break;
					case AssemblyConnectorStatus.Invalid:
						issues.Add( new AssemblyValidationIssue( "invalid-connector", connector.Id ?? "", resolution.Detail ) );
						break;
					case AssemblyConnectorStatus.Resolved:
						if ( AssemblyConnectorFrames.TryFrameInAssembly( document, connector, out var space, out _ ) && space.Mirrored )
							issues.Add( new AssemblyValidationIssue( "mirrored-connector", connector.Id ?? "", "Connector sits on a mirrored occurrence, so its assembly-space frame is left-handed.", Warning: true ) );
						break;
				}
			}
		}
		foreach ( var joint in document.Joints ?? new List<AssemblyJoint>() )
		{
			if ( joint == null ) continue;
			foreach ( var side in new[] { "A", "B" } )
			{
				var connectorId = AssemblyConnectorStore.ConnectorIdForJoint( joint, side );
				if ( connectorId == null ) continue;
				var connector = AssemblyConnectorStore.FindConnector( document, connectorId );
				if ( connector == null )
				{
					issues.Add( new AssemblyValidationIssue( "missing-connector", joint.Id ?? "", $"Joint side {side} names connector '{connectorId}', which is not stored on any occurrence." ) );
					continue;
				}
				var expectedOccurrence = side == "A" ? joint.OccurrenceA : joint.OccurrenceB;
				if ( connector.OccurrenceId != expectedOccurrence )
					issues.Add( new AssemblyValidationIssue( "connector-occurrence-mismatch", joint.Id ?? "", $"Joint side {side} names a connector from occurrence '{connector.OccurrenceId}', not '{expectedOccurrence}'." ) );
				var jointFrame = side == "A" ? joint.FrameA : joint.FrameB;
				if ( jointFrame != null && connector.LocalFrame != null )
				{
					try
					{
						var jf = jointFrame.ToXform();
						var cf = connector.LocalFrame.ToXform();
						var originDrift = ( jf.Origin - cf.Origin ).Length;
						if ( originDrift > AssemblyConnectorGeometry.PositionTolerance
							|| Vec3.Dot( jf.Z, cf.Z ) < AssemblyConnectorGeometry.NormalDotTolerance
							|| Vec3.Dot( jf.X, cf.X ) < AssemblyConnectorGeometry.NormalDotTolerance )
							issues.Add( new AssemblyValidationIssue( "connector-frame-mismatch", joint.Id ?? "", $"Joint side {side} frame drifted from its connector's frame.", Warning: true ) );
					}
					catch ( InvalidOperationException ) { }
				}
			}
		}
		return issues;
	}
}

/// <summary>One-undo-step helper shared by the assembly services. Snapshots the
/// document JSON, applies the edit, and registers a single EditHistory entry
/// whose undo restores the snapshot and whose redo re-applies it. Callers must
/// validate before calling: a refused edit must never reach this helper, so a
/// failed operation leaves no history entry and no change. With a null history
/// the edit applies directly. Restoring replaces the document's collections
/// in place; callers re-resolve records by stable Id afterwards, exactly as
/// Mobile's CadDoc undo replaces the whole document.</summary>
public static class AssemblyHistory
{
	public static void ApplyWithHistory( AssemblyDocument document, EditHistory history, string description, Action apply )
	{
		if ( document == null ) throw new ArgumentNullException( nameof( document ) );
		ArgumentNullException.ThrowIfNull( apply );
		if ( document.OpaqueFutureJson != null )
			throw new InvalidOperationException( "A future-schema assembly document cannot be edited by this reader." );
		if ( history == null )
		{
			apply();
			return;
		}
		var before = document.ToJson();
		apply();
		var after = document.ToJson();
		history.Execute( new DelegateEditCommand( description,
			execute: () => Restore( document, after ),
			undo: () => Restore( document, before ),
			isNoOp: () => before == after ) );
	}

	public static void Restore( AssemblyDocument document, string json )
	{
		var snapshot = AssemblyDocument.FromJson( json );
		document.SchemaVersion = snapshot.SchemaVersion;
		document.LengthUnit = snapshot.LengthUnit;
		document.AngleUnit = snapshot.AngleUnit;
		document.CoordinateSystem = snapshot.CoordinateSystem;
		document.Definitions = snapshot.Definitions ?? new List<AssemblyPartDefinition>();
		document.Occurrences = snapshot.Occurrences ?? new List<AssemblyOccurrence>();
		document.Joints = snapshot.Joints ?? new List<AssemblyJoint>();
		document.ExtensionData = snapshot.ExtensionData;
	}
}
