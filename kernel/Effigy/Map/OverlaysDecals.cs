using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P2: overlays, decals + path_track chains (hammer-overlays-decals-paths).
///
/// NATIVE Geppetto data model — no Hammer / Hammer++ / VBSP / VVIS / VRAD anywhere.
/// Source behaviour (Report §1.2 #8/#9/#20): a decal stamps a material onto one face;
/// info_overlay projects a material onto one or more faces (road markings, posters on
/// curved walls; Hammer++ preserves overlay faces after clipping); path_track chains
/// drive trains / camera paths.
///
/// SCOPE. This file is the data model + bake/export inputs, backend only, no UI:
/// <see cref="MapOverlay"/> (material + target faces + planar UV projection),
/// <see cref="MapDecal"/> (material + face + stamp position), <see cref="PathTrackChain"/>
/// (ordered nodes, links, speed) and <see cref="OverlayDecalSet"/>, the container that
/// applies a real <see cref="MapClip"/> / <see cref="MapVertexEdit"/> result to the
/// overlays and decals on a brush and reports what survived, what was re-projected and
/// what was lost — always named, never silently dropped.
///
/// MAPDOCUMENT NOTE. MapDocument landed 2026-11-13 (after this file was written,
/// when no MapDocument type existed yet). This file still does NOT invent or
/// depend on it: the types here take the brush they act on as a parameter
/// (<see cref="MapBrush"/> / <see cref="MapVertexBrush"/>), and
/// <see cref="OverlayDecalSet"/> is the thin container the document adapter
/// projects a MapDocument onto — the same pattern IssueEngine's IssueDocument
/// uses. That adapter is <see cref="MapDocumentOverlays"/>: brush resolution,
/// document validation, the ValidationHook, path_track entity export and sidecar
/// JSON persistence. No type here is redefined by a sibling.
///
/// FACE IDENTITY. A clip keeps every surviving face on its ORIGINAL plane
/// (MapClip.BuildSide reuses the original MapBrushFace structs), so an overlay target
/// is identified by its face PLANE (normal + distance), not by face index: after a
/// clip the target is found wherever it now sits, and a face clipped to zero area
/// is reported lost with <see cref="OverlayDecalIssue.OverlayFaceLost"/>. A vertex
/// edit is different — moving a vertex re-solves the adjacent planes, so the same
/// face may sit on a NEW plane: there the identity is face INDEX + material
/// (<see cref="OverlayFaceRef.ResolveByIndex(MapVertexBrush)"/>), which vertex edits
/// preserve (MoveVertex/MoveEdge/MoveFace keep face order; only SplitFace changes
/// the count, and a split target is reported as
/// <see cref="OverlayDecalIssue.OverlayFaceSplit"/>, never silently re-pointed at
/// one child face).
///
/// UNITS. Kernel inches, Z-up. An overlay projects in WORLD space: UV = the face
/// vertex's position on the overlay plane (origin + U/V axes) times the overlay
/// scale, then the overlay's <see cref="FaceUv"/> transform. Two faces sharing one
/// overlay therefore get different UVs — that is the point: one road marking
/// spanning a wall and the floor is one projection, not two textures.
///
/// DEPENDENCIES (landed, built against the real APIs, not the spec): MapBrush
/// (hammer-brush-core-plane-based-brushes: FromPlanes, Faces, FaceVertices,
/// FaceUvs, IsValid, Validate), FaceMaterial / FaceUv (hammer-face-material-uv-model:
/// PlaneAxes, ComputeUvs, ToJson/FromJson), MapClip (hammer-brush-clip-two-point:
/// Clip, MapClipResult, ClipOutcome), MapVertexEdit (MapVertexBrush, MoveVertex,
/// SplitFace, MapVertexEditResult.AffectedFaces). EntityRegistry
/// (hammer-entity-registry-fgd-json) does NOT define info_overlay / infodecal /
/// path_track yet — the registry definitions for those entity classes are that
/// ticket's follow-up, not this one; this file is the data model the registry
/// entries will point at.
/// </summary>
public readonly struct OverlayDecalIssue
{
	/// <summary>An overlay names no material.</summary>
	public const string OverlayBlankMaterial = "OverlayBlankMaterial";
	/// <summary>An overlay has no target faces.</summary>
	public const string OverlayNoTargetFaces = "OverlayNoTargetFaces";
	/// <summary>Two targets of one overlay name the same brush face.</summary>
	public const string OverlayDuplicateTarget = "OverlayDuplicateTarget";
	/// <summary>An overlay's projection axes are zero-length, non-unit, non-perpendicular, or its scale is zero.</summary>
	public const string OverlayDegenerateProjection = "OverlayDegenerateProjection";
	/// <summary>An overlay's own UV transform is invalid (see FaceUv.Validate).</summary>
	public const string OverlayBadUv = "OverlayBadUv";
	/// <summary>An overlay target face no longer exists (clipped away, or its brush is gone).</summary>
	public const string OverlayFaceLost = "OverlayFaceLost";
	/// <summary>An overlay target face was split by a vertex edit; it needs explicit re-targeting.</summary>
	public const string OverlayFaceSplit = "OverlayFaceSplit";
	/// <summary>Two overlays / decals / paths in one set share an id / name.</summary>
	public const string DuplicateId = "DuplicateId";
	/// <summary>A decal names no material.</summary>
	public const string DecalBlankMaterial = "DecalBlankMaterial";
	/// <summary>A decal's target face no longer exists (clipped away, or its brush is gone).</summary>
	public const string DecalFaceLost = "DecalFaceLost";
	/// <summary>A decal's position is not on its face plane.</summary>
	public const string DecalOffFace = "DecalOffFace";
	/// <summary>A decal's position is on the face plane but outside the face polygon (the face was clipped smaller).</summary>
	public const string DecalOutsideFace = "DecalOutsideFace";
	/// <summary>A decal's radius is zero or negative.</summary>
	public const string DecalBadRadius = "DecalBadRadius";
	/// <summary>A path_track chain has no nodes.</summary>
	public const string PathEmpty = "PathEmpty";
	/// <summary>A path_track chain has a blank name.</summary>
	public const string PathBlankName = "PathBlankName";
	/// <summary>Two nodes in one chain share a targetname.</summary>
	public const string PathDuplicateNode = "PathDuplicateNode";
	/// <summary>A node's Next names a node that is not in the chain — the chain is broken there.</summary>
	public const string PathMissingNode = "PathMissingNode";
	/// <summary>A node is not reachable from the chain start by following Next links.</summary>
	public const string PathDisconnected = "PathDisconnected";
	/// <summary>A node's speed is negative.</summary>
	public const string PathBadSpeed = "PathBadSpeed";

	/// <summary>Machine-readable reason code.</summary>
	public readonly string Code;
	/// <summary>Human-readable explanation, naming the ids involved.</summary>
	public readonly string Message;
	/// <summary>Overlay / decal / path id the issue is about, or "" when it is about the whole set.</summary>
	public readonly string Target;

	public OverlayDecalIssue( string code, string message, string target = "" )
	{
		Code = code;
		Message = message;
		Target = target ?? "";
	}

	public override string ToString() => string.IsNullOrEmpty( Target )
		? $"{Code}: {Message}"
		: $"{Code} ({Target}): {Message}";
}

/// <summary>
/// One overlay / decal target face: which brush, which face of it, and the face's plane
/// + material as they were when the target was created. The plane is the identity
/// that survives a clip; the index + material is the identity that survives a
/// vertex edit. See the file header for why there are two.
/// </summary>
public readonly struct OverlayFaceRef : IEquatable<OverlayFaceRef>
{
	/// <summary>Brush id in the owning document (the caller's key, not a MapBrush field — MapBrush carries no id).</summary>
	public readonly string BrushId;
	/// <summary>Face index in the brush when the target was created.</summary>
	public readonly int FaceIndex;
	/// <summary>Face plane normal (unit, outward) when the target was created.</summary>
	public readonly Vec3 PlaneNormal;
	/// <summary>Face plane distance (Dot(Normal, p) = PlaneDistance on the plane) when created.</summary>
	public readonly float PlaneDistance;
	/// <summary>Face material name when the target was created.</summary>
	public readonly string MaterialId;

	public OverlayFaceRef( string brushId, int faceIndex, Vec3 planeNormal, float planeDistance, string materialId )
	{
		if ( string.IsNullOrWhiteSpace( brushId ) )
			throw new ArgumentException( "An overlay/decal face target must name a brush; a blank brush id is not a brush.", nameof( brushId ) );
		if ( faceIndex < 0 )
			throw new ArgumentOutOfRangeException( nameof( faceIndex ), "A face index cannot be negative." );
		BrushId = brushId;
		PlaneNormal = planeNormal;
		PlaneDistance = planeDistance;
		MaterialId = materialId ?? "";
		FaceIndex = faceIndex;
	}

	/// <summary>Capture the face's plane + material from a real brush. Throws for a null brush, a blank brush id, or an out-of-range face index.</summary>
	public static OverlayFaceRef For( MapBrush brush, string brushId, int faceIndex )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( faceIndex < 0 || faceIndex >= brush.FaceCount )
			throw new ArgumentOutOfRangeException( nameof( faceIndex ), $"Face {faceIndex} is outside 0..{brush.FaceCount - 1}." );
		var face = brush.Faces[faceIndex];
		return new OverlayFaceRef( brushId, faceIndex, face.Normal, face.Distance, face.MaterialId );
	}

	/// <summary>True when <paramref name="face"/> sits on this target's plane: same outward normal (within 1e-6) and same distance (within 1e-3 inches, MapBrush.PlaneEps).</summary>
	public bool MatchesPlane( Effigy.MapBrushFace face )
		=> Vec3.Dot( face.Normal, PlaneNormal ) > 1f - 1e-6f
			&& Math.Abs( face.Distance - PlaneDistance ) <= 1e-3f;

	/// <summary>
	/// Clip identity: the index of the face on this target's plane in <paramref name="brush"/>,
	/// or -1 when no face sits on that plane any more (clipped away). Never falls back to
	/// the stored index — a face at the same index on a different plane is a different face.
	/// </summary>
	public int ResolveByPlane( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		for ( var i = 0; i < brush.FaceCount; i++ )
			if ( MatchesPlane( brush.Faces[i] ) )
				return i;
		return -1;
	}

	/// <summary>
	/// Vertex-edit identity for plane-form brushes: the stored index, when it is still in
	/// range and still carries the same material. A material change at that index means
	/// the face was replaced, not edited — that is a loss, not a match.
	/// </summary>
	public int ResolveByIndex( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( FaceIndex < 0 || FaceIndex >= brush.FaceCount ) return -1;
		if ( !string.IsNullOrEmpty( MaterialId ) && brush.Faces[FaceIndex].MaterialId != MaterialId ) return -1;
		return FaceIndex;
	}

	/// <summary>
	/// Vertex-edit identity for vertex-form brushes: the stored index, when it is still in
	/// range and still carries the same material (MapVertexBrush.Materials is carried
	/// through edits and splits). See <see cref="ResolveByIndex(MapBrush)"/>.
	/// </summary>
	public int ResolveByIndex( MapVertexBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( FaceIndex < 0 || FaceIndex >= brush.FaceCount ) return -1;
		if ( !string.IsNullOrEmpty( MaterialId ) && brush.Materials[FaceIndex] != MaterialId ) return -1;
		return FaceIndex;
	}

	/// <summary>JSON form: {"brush":"room","face":1,"normal":[x,y,z],"distance":d,"material":"metal/metal"}.</summary>
	public JsonObject ToJson() => new()
	{
		["brush"] = BrushId,
		["face"] = FaceIndex,
		["normal"] = OverlayDecalJson.Vec3Json( PlaneNormal ),
		["distance"] = (double)PlaneDistance,
		["material"] = MaterialId,
	};

	/// <summary>Parse <see cref="ToJson"/> output. A missing "brush" or a negative "face" throws, naming the problem; a missing "material" reads as "".</summary>
	public static OverlayFaceRef FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var brush = json["brush"]?.GetValue<string>();
		if ( string.IsNullOrWhiteSpace( brush ) )
			throw new ArgumentException( "OverlayFaceRef JSON is missing 'brush'." );
		var face = json["face"] is null ? -1 : json["face"]!.GetValue<int>();
		if ( face < 0 )
			throw new ArgumentException( "OverlayFaceRef JSON is missing 'face', or it is negative." );
		return new OverlayFaceRef(
			brush,
			face,
			OverlayDecalJson.ReadVec3( json, "normal", Vec3.Zero ),
			OverlayDecalJson.ReadFloat( json, "distance", 0f ),
			json["material"]?.GetValue<string>() ?? "" );
	}

	public bool Equals( OverlayFaceRef o ) =>
		BrushId == o.BrushId && FaceIndex == o.FaceIndex &&
		PlaneNormal.Equals( o.PlaneNormal ) && PlaneDistance == o.PlaneDistance &&
		MaterialId == o.MaterialId;

	public override bool Equals( object obj ) => obj is OverlayFaceRef other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( BrushId, FaceIndex, PlaneNormal, PlaneDistance, MaterialId );
	public override string ToString() => $"{BrushId} face {FaceIndex} (n={PlaneNormal} d={PlaneDistance:0.###} {MaterialId})";
}

/// <summary>
/// info_overlay, native: one material projected onto one or more brush faces.
/// The projection is a WORLD-space plane (origin + U/V axes + scale): every target
/// face's vertices are projected onto that plane, so a single overlay spans a wall
/// and the floor with one continuous projection instead of one texture per face.
/// The overlay's own <see cref="FaceUv"/> is applied on top of the projection
/// (offset / rotation / lightmap scale / smoothing group); its Scale composes with
/// <see cref="Scale"/> — with the default FaceUv (unit scale) the final UV is
/// exactly the projected world position times <see cref="Scale"/>.
///
/// PROJECTION BASIS. Use <see cref="Planar"/> to derive the axes from a face normal
/// via <see cref="FaceUv.PlaneAxes"/> (the same right-handed basis the face UVs use),
/// or pass explicit axes. <see cref="Validate"/> reports degenerate axes / scale as
/// <see cref="OverlayDecalIssue.OverlayDegenerateProjection"/> — a collapsed
/// projection is a named defect, never a silently stretched texture.
/// </summary>
public sealed class MapOverlay
{
	/// <summary>Overlay id, unique in its set. Blank throws in the constructor — an overlay with no id cannot be looked up or reported.</summary>
	public string Id { get; }
	/// <summary>Projected material name ("decals/roadline", "brick/brickwall001", ...). Blank is a Validate issue, not a throw — see FaceMaterial: the name is data, the defect is named.</summary>
	public string MaterialName { get; }
	/// <summary>World-space origin of the projection plane (the point that projects to UV 0,0 before the FaceUv transform).</summary>
	public Vec3 Origin { get; }
	/// <summary>Projection U axis (unit, perpendicular to <see cref="VAxis"/>).</summary>
	public Vec3 UAxis { get; }
	/// <summary>Projection V axis (unit, perpendicular to <see cref="UAxis"/>).</summary>
	public Vec3 VAxis { get; }
	/// <summary>UV per world inch on each axis (1/64 = one 64-inch texture tile per UV unit). A zero component collapses the projection and is a Validate issue.</summary>
	public Vec2 Scale { get; }
	/// <summary>UV transform applied after the projection: offset, rotation, lightmap scale, smoothing group.</summary>
	public FaceUv Uv { get; }
	/// <summary>Target faces, in the order they were added.</summary>
	public IReadOnlyList<OverlayFaceRef> Targets { get; }

	public MapOverlay( string id, string materialName, Vec3 origin, Vec3 uAxis, Vec3 vAxis, Vec2 scale, FaceUv uv,
		IEnumerable<OverlayFaceRef> targets )
	{
		if ( string.IsNullOrWhiteSpace( id ) )
			throw new ArgumentException( "An overlay must have an id; a blank id is not an overlay.", nameof( id ) );
		if ( targets is null ) throw new ArgumentNullException( nameof( targets ) );
		Id = id;
		MaterialName = materialName ?? "";
		Origin = origin;
		UAxis = uAxis;
		VAxis = vAxis;
		Scale = scale;
		Uv = uv;
		Targets = targets.ToList();
	}

	/// <summary>
	/// Planar projection from a face normal: the axes come from
	/// <see cref="FaceUv.PlaneAxes"/> (right-handed, uAxis x vAxis == normal), so the
	/// overlay projects the way the face's own UVs do. Throws for a zero-length normal,
	/// naming the problem (PlaneAxes throws).
	/// </summary>
	public static MapOverlay Planar( string id, string materialName, Vec3 origin, Vec3 normal, Vec2 scale, FaceUv uv,
		IEnumerable<OverlayFaceRef> targets )
	{
		var (uAxis, vAxis) = FaceUv.PlaneAxes( normal );
		return new MapOverlay( id, materialName, origin, uAxis, vAxis, scale, uv, targets );
	}

	/// <summary>
	/// All named defects, or empty when the overlay is usable: blank material, no targets,
	/// a duplicated target face, a degenerate projection (zero-length / non-unit /
	/// non-perpendicular axes, zero scale component), an invalid UV transform.
	/// Never throws for a bad overlay: the defects ARE the answer.
	/// </summary>
	public IReadOnlyList<OverlayDecalIssue> Validate()
	{
		var issues = new List<OverlayDecalIssue>();
		if ( string.IsNullOrWhiteSpace( MaterialName ) )
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayBlankMaterial,
				"An overlay with no material projects nothing; assign a material or delete the overlay.", Id ) );
		if ( Targets.Count == 0 )
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayNoTargetFaces,
				"An overlay with no target faces projects onto nothing.", Id ) );
		var seen = new HashSet<(string, int)>();
		foreach ( var target in Targets )
			if ( !seen.Add( (target.BrushId, target.FaceIndex) ) )
				issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayDuplicateTarget,
					$"Overlay targets brush '{target.BrushId}' face {target.FaceIndex} twice; one target per face.", Id ) );
		if ( UAxis.LengthSquared < 1e-12f || VAxis.LengthSquared < 1e-12f )
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayDegenerateProjection,
				"An overlay projection axis is zero-length; a projection with no direction has no UVs.", Id ) );
		else
		{
			if ( Math.Abs( UAxis.Length - 1f ) > 1e-3f || Math.Abs( VAxis.Length - 1f ) > 1e-3f )
				issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayDegenerateProjection,
					$"Overlay projection axes must be unit length (got |U|={UAxis.Length:0.###}, |V|={VAxis.Length:0.###}); a scaled axis stretches the texture.", Id ) );
			if ( Math.Abs( Vec3.Dot( UAxis, VAxis ) ) > 1e-3f )
				issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayDegenerateProjection,
					$"Overlay projection axes must be perpendicular (dot={Vec3.Dot( UAxis, VAxis ):0.###}); skewed axes shear the texture.", Id ) );
		}
		if ( Scale.x == 0f || Scale.y == 0f )
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayDegenerateProjection,
				$"Overlay projection scale is ({Scale.x}, {Scale.y}); a zero component collapses the texture to a line.", Id ) );
		foreach ( var uvDefect in Uv.Validate() )
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayBadUv, uvDefect, Id ) );
		return issues;
	}

	/// <summary>
	/// Project face vertices onto the overlay plane: one UV per vertex, in vertex order.
	/// uv = Uv.Transform(( Dot(v - Origin, UAxis) * Scale.x, Dot(v - Origin, VAxis) * Scale.y )).
	/// The face normal is deliberately NOT an input — the projection is world-space, not
	/// face-space, which is what lets one overlay span faces at different angles.
	/// Throws for a null or empty vertex list, naming the problem.
	/// </summary>
	public Vec2[] Project( IReadOnlyList<Vec3> vertices )
	{
		if ( vertices is null ) throw new ArgumentNullException( nameof( vertices ) );
		if ( vertices.Count == 0 )
			throw new ArgumentException( "Cannot project an overlay onto a face with no vertices." );
		var uvs = new Vec2[vertices.Count];
		for ( var i = 0; i < vertices.Count; i++ )
		{
			var d = vertices[i] - Origin;
			uvs[i] = Uv.Transform( new Vec2( Vec3.Dot( d, UAxis ) * Scale.x, Vec3.Dot( d, VAxis ) * Scale.y ) );
		}
		return uvs;
	}

	/// <summary>Project one face of a brush: its solved polygon, in polygon order. Throws for a null brush or an out-of-range face index (MapBrush.FaceVertices throws).</summary>
	public Vec2[] ProjectOnto( MapBrush brush, int faceIndex )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		return Project( brush.FaceVertices( faceIndex ) );
	}

	/// <summary>
	/// Re-project every target that still exists in <paramref name="brush"/> (resolved by
	/// plane — the clip identity), and name the targets that do not.
	/// Returns the surviving (target, new face index, current UVs) triples plus the
	/// <see cref="OverlayDecalIssue.OverlayFaceLost"/> issues for the rest. A lost face
	/// is reported, never silently dropped from the overlay: the overlay keeps its
	/// target list, and the caller decides whether to delete it.
	/// </summary>
	public (IReadOnlyList<(OverlayFaceRef Target, int FaceIndex, Vec2[] Uvs)> Survived, IReadOnlyList<OverlayDecalIssue> Lost)
		Reproject( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var survived = new List<(OverlayFaceRef, int, Vec2[])>();
		var lost = new List<OverlayDecalIssue>();
		foreach ( var target in Targets )
		{
			var index = target.ResolveByPlane( brush );
			if ( index < 0 )
				lost.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayFaceLost,
					$"Overlay '{Id}' lost face {target.FaceIndex} of brush '{target.BrushId}': no face sits on its plane any more.", Id ) );
			else
				survived.Add( (target, index, Project( brush.FaceVertices( index ) )) );
		}
		return (survived, lost);
	}

	/// <summary>JSON form: {"id","material","origin":[x,y,z],"uAxis":[x,y,z],"vAxis":[x,y,z],"scale":[x,y],"uv":{...},"targets":[{...}]}.</summary>
	public JsonObject ToJson() => new()
	{
		["id"] = Id,
		["material"] = MaterialName,
		["origin"] = OverlayDecalJson.Vec3Json( Origin ),
		["uAxis"] = OverlayDecalJson.Vec3Json( UAxis ),
		["vAxis"] = OverlayDecalJson.Vec3Json( VAxis ),
		["scale"] = OverlayDecalJson.Vec2Json( Scale ),
		["uv"] = Uv.ToJson(),
		["targets"] = new JsonArray( Targets.Select( t => (JsonNode)t.ToJson() ).ToArray() ),
	};

	/// <summary>Parse <see cref="ToJson"/> output. A missing "id" throws, naming the problem; a missing "uv" falls back to <see cref="FaceUv.Default"/>; missing axes/scale fall back to the unit X/Y axes and unit scale.</summary>
	public static MapOverlay FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var id = json["id"]?.GetValue<string>();
		if ( string.IsNullOrWhiteSpace( id ) )
			throw new ArgumentException( "MapOverlay JSON is missing 'id'." );
		var targets = new List<OverlayFaceRef>();
		if ( json["targets"] is JsonArray array )
			foreach ( var node in array )
				if ( node is JsonObject obj )
					targets.Add( OverlayFaceRef.FromJson( obj ) );
		return new MapOverlay(
			id,
			json["material"]?.GetValue<string>() ?? "",
			OverlayDecalJson.ReadVec3( json, "origin", Vec3.Zero ),
			OverlayDecalJson.ReadVec3( json, "uAxis", new Vec3( 1, 0, 0 ) ),
			OverlayDecalJson.ReadVec3( json, "vAxis", new Vec3( 0, 1, 0 ) ),
			OverlayDecalJson.ReadVec2( json, "scale", new Vec2( 1, 1 ) ),
			json["uv"] is JsonObject uvJson ? FaceUv.FromJson( uvJson ) : FaceUv.Default,
			targets );
	}

	public override string ToString() => $"overlay '{Id}' {MaterialName} on {Targets.Count} face(s)";
}

/// <summary>
/// A decal: one material stamped onto ONE face at ONE position (Hammer's infodecal:
/// a poster, a stain, a scorch mark). Unlike an overlay, which projects across
/// faces, a decal is a stamp: its centre is a point on the face, its size is
/// <see cref="Radius"/> (half the side of the square stamp), its orientation is
/// <see cref="RotationDegrees"/> in the face plane. Validation is geometric: the
/// position must sit ON the face plane and INSIDE the face polygon — a clip that
/// shrinks the face past the stamp orphans the decal with a named issue instead
/// of leaving a stamp floating in mid-air.
/// </summary>
public sealed class MapDecal
{
	/// <summary>Decal id, unique in its set. Blank throws in the constructor.</summary>
	public string Id { get; }
	/// <summary>Stamped material name ("decals/poster01", ...). Blank is a Validate issue, not a throw.</summary>
	public string MaterialName { get; }
	/// <summary>The face this decal stamps: brush + face + plane + material at creation.</summary>
	public OverlayFaceRef Face { get; }
	/// <summary>Stamp centre, in world inches. Must sit on the face plane, inside the face polygon.</summary>
	public Vec3 Position { get; }
	/// <summary>Half the side of the square stamp, in world inches. Zero or negative is a Validate issue.</summary>
	public float Radius { get; }
	/// <summary>Stamp rotation in the face plane, degrees counter-clockwise seen from outside the face.</summary>
	public float RotationDegrees { get; }

	/// <summary>Plane distance tolerance for "on the face", inches (MapBrush.PlaneEps).</summary>
	public const float OnFaceTolerance = 1e-3f;

	public MapDecal( string id, string materialName, OverlayFaceRef face, Vec3 position, float radius, float rotationDegrees = 0f )
	{
		if ( string.IsNullOrWhiteSpace( id ) )
			throw new ArgumentException( "A decal must have an id; a blank id is not a decal.", nameof( id ) );
		Id = id;
		MaterialName = materialName ?? "";
		Face = face;
		Position = position;
		Radius = radius;
		RotationDegrees = rotationDegrees;
	}

	/// <summary>Capture the target face's plane + material from a real brush. Throws for a null brush, a blank brush id, or an out-of-range face index.</summary>
	public static MapDecal For( string id, string materialName, MapBrush brush, string brushId, int faceIndex,
		Vec3 position, float radius, float rotationDegrees = 0f )
		=> new( id, materialName, OverlayFaceRef.For( brush, brushId, faceIndex ), position, radius, rotationDegrees );

	/// <summary>Self defects, no brush needed: blank material, non-positive radius.</summary>
	public IReadOnlyList<OverlayDecalIssue> Validate()
	{
		var issues = new List<OverlayDecalIssue>();
		if ( string.IsNullOrWhiteSpace( MaterialName ) )
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.DecalBlankMaterial,
				"A decal with no material stamps nothing; assign a material or delete the decal.", Id ) );
		if ( Radius <= 0f )
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.DecalBadRadius,
				$"Decal radius is {Radius}; a stamp needs a positive half-side.", Id ) );
		return issues;
	}

	/// <summary>
	/// Full validation against a brush: the self defects plus the geometric ones —
	/// the target face is gone (<see cref="OverlayDecalIssue.DecalFaceLost"/>), the
	/// position is off the face plane (<see cref="OverlayDecalIssue.DecalOffFace"/>),
	/// or the position is outside the face polygon
	/// (<see cref="OverlayDecalIssue.DecalOutsideFace"/>). A null brush is one
	/// DecalFaceLost issue ("no brush to check against"), not a throw.
	/// </summary>
	public IReadOnlyList<OverlayDecalIssue> ValidateAgainst( MapBrush brush )
	{
		var issues = new List<OverlayDecalIssue>( Validate() );
		if ( brush is null )
		{
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.DecalFaceLost,
				$"Decal '{Id}' has no brush to check against.", Id ) );
			return issues;
		}
		var index = Face.ResolveByPlane( brush );
		if ( index < 0 )
		{
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.DecalFaceLost,
				$"Decal '{Id}' targets face {Face.FaceIndex} of brush '{Face.BrushId}', which no longer exists (clipped away or re-planed).", Id ) );
			return issues;
		}
		var face = brush.Faces[index];
		var offPlane = face.SignedDistance( Position );
		if ( Math.Abs( offPlane ) > OnFaceTolerance )
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.DecalOffFace,
				$"Decal '{Id}' sits {offPlane:0.###} inches off its face plane; a decal must sit on the face it stamps.", Id ) );
		else if ( !PointInsidePolygon( Position, brush.FaceVertices( index ), face.Normal ) )
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.DecalOutsideFace,
				$"Decal '{Id}' sits outside its face polygon; the face was clipped smaller than the stamp position.", Id ) );
		return issues;
	}

	/// <summary>
	/// The four world-space corners of the square stamp, in order (+u,+v), (-u,+v),
	/// (-u,-v), (+u,-v) in the face-plane basis, rotated by
	/// <see cref="RotationDegrees"/> about the stamp centre. The corners lie on the
	/// face plane. Throws <see cref="InvalidOperationException"/> when the target face
	/// is gone — a stamp with no face has no corners.
	/// </summary>
	public Vec3[] StampCorners( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var index = Face.ResolveByPlane( brush );
		if ( index < 0 )
			throw new InvalidOperationException( $"Decal '{Id}' has no face to stamp on: its target face is gone." );
		var (uAxis, vAxis) = FaceUv.PlaneAxes( brush.Faces[index].Normal );
		var radians = RotationDegrees * MathF.PI / 180f;
		var cos = MathF.Cos( radians );
		var sin = MathF.Sin( radians );
		var corners = new Vec3[4];
		var signs = new (float, float)[] { (1, 1), (-1, 1), (-1, -1), (1, -1) };
		for ( var i = 0; i < 4; i++ )
		{
			var (su, sv) = signs[i];
			var cu = ( su * cos - sv * sin ) * Radius;
			var cv = ( su * sin + sv * cos ) * Radius;
			corners[i] = Position + uAxis * cu + vAxis * cv;
		}
		return corners;
	}

	/// <summary>
	/// Point-in-polygon in the face plane: project the point and the polygon into the
	/// <see cref="FaceUv.PlaneAxes"/> basis and ray-cast. A point on an edge counts as
	/// inside (a stamp exactly on the seam is still on the face). The polygon is
	/// expected convex (it comes from a MapBrush face); the test does not depend on it.
	/// </summary>
	internal static bool PointInsidePolygon( Vec3 point, IReadOnlyList<Vec3> polygon, Vec3 normal )
	{
		if ( polygon is null || polygon.Count < 3 ) return false;
		var (uAxis, vAxis) = FaceUv.PlaneAxes( normal );
		var origin = polygon[0];
		double Pu( Vec3 p ) => Vec3.Dot( p - origin, uAxis );
		double Pv( Vec3 p ) => Vec3.Dot( p - origin, vAxis );
		var pu = Pu( point );
		var pv = Pv( point );
		var inside = false;
		for ( var i = 0; i < polygon.Count; i++ )
		{
			var a = polygon[i];
			var b = polygon[( i + 1 ) % polygon.Count];
			var au = Pu( a ); var av = Pv( a );
			var bu = Pu( b ); var bv = Pv( b );
			// On-segment: the point is on this edge (within 1e-6 inches) — inside.
			var edgeU = bu - au;
			var edgeV = bv - av;
			var lenSq = edgeU * edgeU + edgeV * edgeV;
			if ( lenSq > 1e-12 )
			{
				var t = ( ( pu - au ) * edgeU + ( pv - av ) * edgeV ) / lenSq;
				if ( t >= 0.0 && t <= 1.0 )
				{
					var closestU = au + t * edgeU;
					var closestV = av + t * edgeV;
					var distSq = ( pu - closestU ) * ( pu - closestU ) + ( pv - closestV ) * ( pv - closestV );
					if ( distSq <= 1e-12 ) return true;
				}
			}
			if ( ( av > pv ) != ( bv > pv ) )
			{
				var crossU = au + ( pv - av ) * ( bu - au ) / ( bv - av );
				if ( crossU > pu ) inside = !inside;
			}
		}
		return inside;
	}

	/// <summary>JSON form: {"id","material","face":{...},"position":[x,y,z],"radius":r,"rotation":deg}.</summary>
	public JsonObject ToJson() => new()
	{
		["id"] = Id,
		["material"] = MaterialName,
		["face"] = Face.ToJson(),
		["position"] = OverlayDecalJson.Vec3Json( Position ),
		["radius"] = (double)Radius,
		["rotation"] = (double)RotationDegrees,
	};

	/// <summary>Parse <see cref="ToJson"/> output. A missing "id" or "face" throws, naming the problem; a missing "rotation" reads as 0.</summary>
	public static MapDecal FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var id = json["id"]?.GetValue<string>();
		if ( string.IsNullOrWhiteSpace( id ) )
			throw new ArgumentException( "MapDecal JSON is missing 'id'." );
		if ( json["face"] is not JsonObject faceJson )
			throw new ArgumentException( "MapDecal JSON is missing 'face'." );
		return new MapDecal(
			id,
			json["material"]?.GetValue<string>() ?? "",
			OverlayFaceRef.FromJson( faceJson ),
			OverlayDecalJson.ReadVec3( json, "position", Vec3.Zero ),
			OverlayDecalJson.ReadFloat( json, "radius", 0f ),
			OverlayDecalJson.ReadFloat( json, "rotation", 0f ) );
	}

	public override string ToString() => $"decal '{Id}' {MaterialName} at {Position} on {Face}";
}

/// <summary>
/// One path_track node: a named stop on a chain, at a position, with the speed the
/// train / camera travels at when it leaves this node (inches per second, Hammer's
/// path_track speed). <see cref="Next"/> is the targetname of the next node, or null
/// / "" for the end of the chain. A speed of 0 is a pause at this node, not an
/// error; a negative speed is.
/// </summary>
public sealed class PathTrackNode
{
	/// <summary>Targetname, unique in its chain. Blank throws in the constructor — a node with no name cannot be linked to.</summary>
	public string Name { get; }
	/// <summary>Node position, world inches.</summary>
	public Vec3 Position { get; }
	/// <summary>Travel speed leaving this node, inches per second. 0 = pause here; negative is a Validate issue.</summary>
	public float Speed { get; }
	/// <summary>Targetname of the next node, or null / "" for the end of the chain.</summary>
	public string Next { get; }

	public PathTrackNode( string name, Vec3 position, float speed, string next = null )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A path_track node must have a name; a blank name cannot be linked to.", nameof( name ) );
		Name = name;
		Position = position;
		Speed = speed;
		Next = string.IsNullOrWhiteSpace( next ) ? null : next;
	}

	/// <summary>Self defects: a negative speed. Never throws for a bad node: the defect IS the answer.</summary>
	public IReadOnlyList<OverlayDecalIssue> Validate()
	{
		if ( Speed < 0f )
			return new[]
			{
				new OverlayDecalIssue( OverlayDecalIssue.PathBadSpeed,
					$"path_track '{Name}' has speed {Speed}; a travel speed cannot be negative (0 pauses at the node).", Name ),
			};
		return Array.Empty<OverlayDecalIssue>();
	}

	/// <summary>JSON form: {"name","position":[x,y,z],"speed":n,"next":"name"} — "next" is omitted for the end of the chain.</summary>
	public JsonObject ToJson()
	{
		var json = new JsonObject
		{
			["name"] = Name,
			["position"] = OverlayDecalJson.Vec3Json( Position ),
			["speed"] = (double)Speed,
		};
		if ( Next is not null ) json["next"] = Next;
		return json;
	}

	/// <summary>Parse <see cref="ToJson"/> output. A missing "name" throws, naming the problem; a missing "next" reads as the end of the chain.</summary>
	public static PathTrackNode FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var name = json["name"]?.GetValue<string>();
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "PathTrackNode JSON is missing 'name'." );
		return new PathTrackNode(
			name,
			OverlayDecalJson.ReadVec3( json, "position", Vec3.Zero ),
			OverlayDecalJson.ReadFloat( json, "speed", 0f ),
			json["next"]?.GetValue<string>() );
	}

	public override string ToString() => $"path_track '{Name}' at {Position} speed {Speed:0.###} -> {Next ?? "(end)"}";
}

/// <summary>
/// A path_track chain: an ordered list of nodes where each node links to the next
/// by targetname. The ORDER of <see cref="Nodes"/> is the chain's start order — the
/// first node is the start — and the Next links must agree with it: validation
/// follows the links, it does not assume the list order is the link order.
///
/// VALIDATION (all named issues, never a silent fix):
/// an empty chain, a duplicated node name, a negative speed, a Next naming a node
/// that is not in the chain (<see cref="OverlayDecalIssue.PathMissingNode"/> — the
/// broken-chain case), a node unreachable from the start
/// (<see cref="OverlayDecalIssue.PathDisconnected"/>). A CYCLE is legal: a loop
/// path (A -&gt; B -&gt; C -&gt; A) that covers every node validates clean — trains and
/// camera loops run in circles on purpose. <see cref="RouteFromStart"/> is
/// cycle-safe: it stops at the end, at a missing link, or at the first repeated
/// node, visiting each node at most once.
/// </summary>
public sealed class PathTrackChain
{
	/// <summary>Chain name, unique in its set. Blank throws in the constructor.</summary>
	public string Name { get; }
	/// <summary>Nodes in chain order; the first node is the start.</summary>
	public IReadOnlyList<PathTrackNode> Nodes { get; }

	public PathTrackChain( string name, IEnumerable<PathTrackNode> nodes )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A path_track chain must have a name; a blank name is not a chain.", nameof( name ) );
		if ( nodes is null ) throw new ArgumentNullException( nameof( nodes ) );
		Name = name;
		Nodes = nodes.ToList();
	}

	/// <summary>The node with this targetname, or null when the chain has no such node. Name comparison is ordinal (Hammer targetnames are case-sensitive).</summary>
	public PathTrackNode NodeByName( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return null;
		foreach ( var node in Nodes )
			if ( node.Name == name )
				return node;
		return null;
	}

	/// <summary>
	/// The route from the chain start, following Next links: each node at most once,
	/// stopping at the end of the chain, at a missing link, or at the first repeated
	/// node (a loop). Empty for an empty chain. This is the order a train / camera
	/// actually visits the nodes in — the list order is only the start order.
	/// </summary>
	public IReadOnlyList<PathTrackNode> RouteFromStart()
	{
		var route = new List<PathTrackNode>();
		if ( Nodes.Count == 0 ) return route;
		var seen = new HashSet<string>( StringComparer.Ordinal );
		var current = Nodes[0];
		while ( current is not null && seen.Add( current.Name ) )
		{
			route.Add( current );
			if ( string.IsNullOrEmpty( current.Next ) ) break;
			current = NodeByName( current.Next );
		}
		return route;
	}

	/// <summary>
	/// All named defects, or empty when the chain is connected and drivable. See the
	/// class doc for the rules; a loop covering every node is NOT a defect.
	/// </summary>
	public IReadOnlyList<OverlayDecalIssue> Validate()
	{
		var issues = new List<OverlayDecalIssue>();
		if ( Nodes.Count == 0 )
		{
			issues.Add( new OverlayDecalIssue( OverlayDecalIssue.PathEmpty,
				"A path_track chain with no nodes drives nothing.", Name ) );
			return issues;
		}
		var names = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var node in Nodes )
		{
			if ( !names.Add( node.Name ) )
				issues.Add( new OverlayDecalIssue( OverlayDecalIssue.PathDuplicateNode,
					$"Two path_track nodes in chain '{Name}' are named '{node.Name}'; targetnames must be unique or the Next link is ambiguous.", Name ) );
			issues.AddRange( node.Validate() );
		}
		foreach ( var node in Nodes )
			if ( !string.IsNullOrEmpty( node.Next ) && NodeByName( node.Next ) is null )
				issues.Add( new OverlayDecalIssue( OverlayDecalIssue.PathMissingNode,
					$"path_track '{node.Name}' in chain '{Name}' links to '{node.Next}', which is not a node in the chain; the chain is broken there.", Name ) );
		var reachable = new HashSet<string>( RouteFromStart().Select( n => n.Name ), StringComparer.Ordinal );
		foreach ( var node in Nodes )
			if ( !reachable.Contains( node.Name ) )
				issues.Add( new OverlayDecalIssue( OverlayDecalIssue.PathDisconnected,
					$"path_track '{node.Name}' in chain '{Name}' is not reachable from the chain start '{Nodes[0].Name}'; it will never be visited.", Name ) );
		return issues;
	}

	/// <summary>True when <see cref="Validate"/> finds nothing wrong.</summary>
	public bool IsValid => Validate().Count == 0;

	/// <summary>JSON form: {"name":"train-loop","nodes":[{...}]}.</summary>
	public JsonObject ToJson() => new()
	{
		["name"] = Name,
		["nodes"] = new JsonArray( Nodes.Select( n => (JsonNode)n.ToJson() ).ToArray() ),
	};

	/// <summary>Parse <see cref="ToJson"/> output. A missing "name" throws, naming the problem.</summary>
	public static PathTrackChain FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var name = json["name"]?.GetValue<string>();
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "PathTrackChain JSON is missing 'name'." );
		var nodes = new List<PathTrackNode>();
		if ( json["nodes"] is JsonArray array )
			foreach ( var node in array )
				if ( node is JsonObject obj )
					nodes.Add( PathTrackNode.FromJson( obj ) );
		return new PathTrackChain( name, nodes );
	}

	public override string ToString() => $"path chain '{Name}' ({Nodes.Count} node(s))";
}

/// <summary>One overlay target that survived a clip: which overlay, which target, where it now sits, and its re-projected UVs.</summary>
public sealed class OverlayClipSurvivor
{
	public string OverlayId { get; }
	public OverlayFaceRef Target { get; }
	/// <summary>Face index of the target in the clipped brush side it survived in.</summary>
	public int NewFaceIndex { get; }
	/// <summary>"kept" (Cut mode, or the front side of a Split) or "second" (the back side of a Split).</summary>
	public string Side { get; }
	/// <summary>Current projected UVs for the surviving face polygon, in polygon order — re-projected from the clipped geometry, not carried over.</summary>
	public Vec2[] Uvs { get; }

	public OverlayClipSurvivor( string overlayId, OverlayFaceRef target, int newFaceIndex, string side, Vec2[] uvs )
	{
		OverlayId = overlayId;
		Target = target;
		NewFaceIndex = newFaceIndex;
		Side = side;
		Uvs = uvs ?? Array.Empty<Vec2>();
	}
}

/// <summary>What a <see cref="MapClip"/> did to the overlays and decals on one brush.</summary>
public sealed class OverlayClipReport
{
	/// <summary>Brush id the clip was applied to.</summary>
	public string BrushId { get; }
	/// <summary>The clip's outcome, as reported by MapClip.</summary>
	public ClipOutcome Outcome { get; }
	/// <summary>True when the clip changed nothing (no-op / rejected): no overlay was re-projected and none was lost.</summary>
	public bool Unchanged { get; internal set; }
	/// <summary>Overlay targets that survived, with their re-projected UVs.</summary>
	public IReadOnlyList<OverlayClipSurvivor> Survived => _survived;
	/// <summary>Overlay targets that did not survive, as named issues (OverlayFaceLost).</summary>
	public IReadOnlyList<OverlayDecalIssue> Lost => _lost;
	/// <summary>Decals orphaned by the clip, as named issues (DecalFaceLost / DecalOffFace / DecalOutsideFace).</summary>
	public IReadOnlyList<OverlayDecalIssue> OrphanedDecals => _orphaned;
	readonly List<OverlayClipSurvivor> _survived = new();
	readonly List<OverlayDecalIssue> _lost = new();
	readonly List<OverlayDecalIssue> _orphaned = new();

	internal OverlayClipReport( string brushId, ClipOutcome outcome )
	{
		BrushId = brushId;
		Outcome = outcome;
	}

	internal void AddSurvivor( OverlayClipSurvivor survivor ) => _survived.Add( survivor );
	internal void AddLost( OverlayDecalIssue issue ) => _lost.Add( issue );
	internal void AddOrphanedDecal( OverlayDecalIssue issue ) => _orphaned.Add( issue );
}

/// <summary>One overlay target re-projected (or left alone) by a vertex edit, with its current UVs.</summary>
public sealed class OverlayVertexReprojection
{
	public string OverlayId { get; }
	public int FaceIndex { get; }
	/// <summary>Current projected UVs for the face's vertex loop, in loop order.</summary>
	public Vec2[] Uvs { get; }

	public OverlayVertexReprojection( string overlayId, int faceIndex, Vec2[] uvs )
	{
		OverlayId = overlayId;
		FaceIndex = faceIndex;
		Uvs = uvs ?? Array.Empty<Vec2>();
	}
}

/// <summary>What a <see cref="MapVertexEdit"/> did to the overlays and decals on one brush.</summary>
public sealed class OverlayVertexEditReport
{
	/// <summary>Brush id the edit was applied to.</summary>
	public string BrushId { get; }
	/// <summary>Targets on faces the edit touched, with UVs re-projected from the moved vertices.</summary>
	public IReadOnlyList<OverlayVertexReprojection> Reprojected => _reprojected;
	/// <summary>Targets on faces the edit did not touch, with UVs recomputed (unchanged) from the current vertices.</summary>
	public IReadOnlyList<OverlayVertexReprojection> Unchanged => _unchanged;
	/// <summary>Targets that did not survive, as named issues (OverlayFaceLost).</summary>
	public IReadOnlyList<OverlayDecalIssue> Lost => _lost;
	/// <summary>Targets on faces the edit SPLIT: they resolve to the first child face only, so they need explicit re-targeting instead of a silent partial re-projection.</summary>
	public IReadOnlyList<OverlayDecalIssue> NeedsRetarget => _needsRetarget;
	/// <summary>Decals orphaned by the edit, as named issues (DecalFaceLost / DecalFaceSplit / DecalOffFace / DecalOutsideFace).</summary>
	public IReadOnlyList<OverlayDecalIssue> OrphanedDecals => _orphaned;
	readonly List<OverlayVertexReprojection> _reprojected = new();
	readonly List<OverlayVertexReprojection> _unchanged = new();
	readonly List<OverlayDecalIssue> _lost = new();
	readonly List<OverlayDecalIssue> _needsRetarget = new();
	readonly List<OverlayDecalIssue> _orphaned = new();

	internal OverlayVertexEditReport( string brushId )
	{
		BrushId = brushId;
	}

	internal void AddReprojected( OverlayVertexReprojection r ) => _reprojected.Add( r );
	internal void AddUnchanged( OverlayVertexReprojection r ) => _unchanged.Add( r );
	internal void AddLost( OverlayDecalIssue issue ) => _lost.Add( issue );
	internal void AddNeedsRetarget( OverlayDecalIssue issue ) => _needsRetarget.Add( issue );
	internal void AddOrphanedDecal( OverlayDecalIssue issue ) => _orphaned.Add( issue );
}

/// <summary>
/// The overlays, decals and path_track chains of one map, as a set.
///
/// This is the CONTAINER, not the map document: brushes live in MapDocument, so
/// every method here takes the brushes it acts on as a parameter — a dictionary
/// from brush id to <see cref="MapBrush"/> for <see cref="ValidateAll"/>, a real
/// <see cref="MapClipResult"/> for <see cref="ApplyClip"/>, real
/// <see cref="MapVertexBrush"/> before/after snapshots for
/// <see cref="ApplyVertexEdit"/>. The adapter that hands this set a document's
/// brushes is <see cref="MapDocumentOverlays"/>; the logic here does not change.
///
/// ADDING. Ids must be unique per kind: a second overlay / decal with the same id,
/// or a second chain with the same name, throws <see cref="ArgumentException"/>
/// naming the duplicate — a silent overwrite would lose a whole overlay.
///
/// PERSISTENCE. <see cref="ToJson"/> / <see cref="FromJson"/> round-trip the set.
/// Brushes are NOT serialised here — they belong to the map document, and the
/// face refs in the set carry the brush ids + planes needed to re-resolve against
/// them on load.
/// </summary>
public sealed class OverlayDecalSet
{
	readonly List<MapOverlay> _overlays = new();
	readonly List<MapDecal> _decals = new();
	readonly List<PathTrackChain> _paths = new();

	public IReadOnlyList<MapOverlay> Overlays => _overlays;
	public IReadOnlyList<MapDecal> Decals => _decals;
	public IReadOnlyList<PathTrackChain> Paths => _paths;

	/// <summary>Add an overlay. A null overlay or a duplicate id throws, naming the problem.</summary>
	public void AddOverlay( MapOverlay overlay )
	{
		if ( overlay is null ) throw new ArgumentNullException( nameof( overlay ) );
		if ( _overlays.Any( o => o.Id == overlay.Id ) )
			throw new ArgumentException( $"An overlay with id '{overlay.Id}' already exists in this set; overlay ids must be unique.", nameof( overlay ) );
		_overlays.Add( overlay );
	}

	/// <summary>Add a decal. A null decal or a duplicate id throws, naming the problem.</summary>
	public void AddDecal( MapDecal decal )
	{
		if ( decal is null ) throw new ArgumentNullException( nameof( decal ) );
		if ( _decals.Any( d => d.Id == decal.Id ) )
			throw new ArgumentException( $"A decal with id '{decal.Id}' already exists in this set; decal ids must be unique.", nameof( decal ) );
		_decals.Add( decal );
	}

	/// <summary>Add a path_track chain. A null chain or a duplicate name throws, naming the problem.</summary>
	public void AddPath( PathTrackChain chain )
	{
		if ( chain is null ) throw new ArgumentNullException( nameof( chain ) );
		if ( _paths.Any( p => p.Name == chain.Name ) )
			throw new ArgumentException( $"A path_track chain named '{chain.Name}' already exists in this set; chain names must be unique.", nameof( chain ) );
		_paths.Add( chain );
	}

	/// <summary>
	/// Validate everything in the set against the given brushes: each overlay's own
	/// defects plus whether every target face still exists (by plane), each decal's
	/// defects plus its geometry (on-face, inside the polygon), each chain's
	/// defects. A brush id the set references but the dictionary does not have is
	/// an OverlayFaceLost / DecalFaceLost issue naming the missing brush — never a
	/// throw, never a silent skip. A null dictionary throws.
	/// </summary>
	public IReadOnlyList<OverlayDecalIssue> ValidateAll( IReadOnlyDictionary<string, MapBrush> brushes )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var issues = new List<OverlayDecalIssue>();
		foreach ( var overlay in _overlays )
		{
			issues.AddRange( overlay.Validate() );
			foreach ( var target in overlay.Targets )
			{
				if ( !brushes.TryGetValue( target.BrushId, out var brush ) )
					issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayFaceLost,
						$"Overlay '{overlay.Id}' targets brush '{target.BrushId}', which is not in the document.", overlay.Id ) );
				else if ( target.ResolveByPlane( brush ) < 0 )
					issues.Add( new OverlayDecalIssue( OverlayDecalIssue.OverlayFaceLost,
						$"Overlay '{overlay.Id}' targets face {target.FaceIndex} of brush '{target.BrushId}', which no longer exists.", overlay.Id ) );
			}
		}
		foreach ( var decal in _decals )
		{
			if ( !brushes.TryGetValue( decal.Face.BrushId, out var brush ) )
			{
				issues.AddRange( decal.Validate() );
				issues.Add( new OverlayDecalIssue( OverlayDecalIssue.DecalFaceLost,
					$"Decal '{decal.Id}' targets brush '{decal.Face.BrushId}', which is not in the document.", decal.Id ) );
			}
			else
				issues.AddRange( decal.ValidateAgainst( brush ) );
		}
		foreach ( var path in _paths )
			issues.AddRange( path.Validate() );
		return issues;
	}

	/// <summary>
	/// Apply a real <see cref="MapClip"/> result to the overlays and decals on
	/// <paramref name="brushId"/>: the overlay itself survives the clip whenever ANY
	/// of its targets survives, and each surviving target gets UVs re-projected from
	/// the clipped face polygon (the polygon changed, so the old UVs would be wrong).
	/// Lost targets and orphaned decals are named in the report, never silently
	/// dropped from the set — the set keeps its entries, and the caller decides
	/// whether to delete them. No-op / rejected clips report
	/// <see cref="OverlayClipReport.Unchanged"/> and touch nothing; a whole-brush
	/// cut-away loses every target and orphans every decal on that brush; a Split
	/// resolves each target against BOTH sides (a face survives in exactly one).
	/// Overlays and decals on other brushes are untouched. Throws for a blank brush
	/// id or a null result.
	/// </summary>
	public OverlayClipReport ApplyClip( string brushId, MapClipResult result )
	{
		if ( string.IsNullOrWhiteSpace( brushId ) )
			throw new ArgumentException( "ApplyClip must name a brush; a blank brush id is not a brush.", nameof( brushId ) );
		if ( result is null ) throw new ArgumentNullException( nameof( result ) );
		var report = new OverlayClipReport( brushId, result.Outcome );
		switch ( result.Outcome )
		{
			case ClipOutcome.NoOpPlaneOutside:
			case ClipOutcome.NoOpPlaneCoplanar:
			case ClipOutcome.RejectedInvalidBrush:
				report.Unchanged = true;
				return report;
			case ClipOutcome.WholeBrushCutAway:
				foreach ( var overlay in _overlays )
					foreach ( var target in overlay.Targets )
						if ( target.BrushId == brushId )
							report.AddLost( new OverlayDecalIssue( OverlayDecalIssue.OverlayFaceLost,
								$"Overlay '{overlay.Id}' lost face {target.FaceIndex} of brush '{brushId}': the whole brush was cut away.", overlay.Id ) );
				foreach ( var decal in _decals )
					if ( decal.Face.BrushId == brushId )
						report.AddOrphanedDecal( new OverlayDecalIssue( OverlayDecalIssue.DecalFaceLost,
							$"Decal '{decal.Id}' lost its face: the whole brush '{brushId}' was cut away.", decal.Id ) );
				return report;
			case ClipOutcome.Cut:
			case ClipOutcome.Split:
				break;
			default:
				report.Unchanged = true;
				return report;
		}

		var survivedTargets = new HashSet<(string, string, int)>();
		void ResolveSide( MapBrush side, string sideName )
		{
			if ( side is null ) return;
			foreach ( var overlay in _overlays )
				foreach ( var target in overlay.Targets )
				{
					if ( target.BrushId != brushId ) continue;
					if ( survivedTargets.Contains( (overlay.Id, target.BrushId, target.FaceIndex) ) ) continue;
					var index = target.ResolveByPlane( side );
					if ( index < 0 ) continue;
					survivedTargets.Add( (overlay.Id, target.BrushId, target.FaceIndex) );
					report.AddSurvivor( new OverlayClipSurvivor( overlay.Id, target, index, sideName,
						overlay.Project( side.FaceVertices( index ) ) ) );
				}
		}
		ResolveSide( result.Kept, "kept" );
		ResolveSide( result.Second, "second" );

		foreach ( var overlay in _overlays )
			foreach ( var target in overlay.Targets )
				if ( target.BrushId == brushId && !survivedTargets.Contains( (overlay.Id, target.BrushId, target.FaceIndex) ) )
					report.AddLost( new OverlayDecalIssue( OverlayDecalIssue.OverlayFaceLost,
						$"Overlay '{overlay.Id}' lost face {target.FaceIndex} of brush '{brushId}': the clip removed it.", overlay.Id ) );

		foreach ( var decal in _decals )
		{
			if ( decal.Face.BrushId != brushId ) continue;
			MapBrush side = null;
			var index = -1;
			if ( result.Kept is not null )
				index = decal.Face.ResolveByPlane( result.Kept );
			if ( index >= 0 )
				side = result.Kept;
			else if ( result.Second is not null )
			{
				index = decal.Face.ResolveByPlane( result.Second );
				if ( index >= 0 ) side = result.Second;
			}
			if ( side is null )
			{
				report.AddOrphanedDecal( new OverlayDecalIssue( OverlayDecalIssue.DecalFaceLost,
					$"Decal '{decal.Id}' lost its face {decal.Face.FaceIndex} of brush '{brushId}': the clip removed it.", decal.Id ) );
				continue;
			}
			// The face survived, but the stamp position may now sit outside the
			// shrunken polygon: re-validate against the clipped side, not the old brush.
			foreach ( var issue in decal.ValidateAgainst( side ) )
				if ( issue.Code == OverlayDecalIssue.DecalOffFace || issue.Code == OverlayDecalIssue.DecalOutsideFace )
					report.AddOrphanedDecal( issue );
		}
		return report;
	}

	/// <summary>
	/// Apply a real <see cref="MapVertexEdit"/> to the overlays and decals on
	/// <paramref name="brushId"/>: <paramref name="before"/> and <paramref name="after"/>
	/// are the vertex-form brush snapshots, <paramref name="affectedFaces"/> is the
	/// edit result's <see cref="MapVertexEditResult.AffectedFaces"/> (null = unknown:
	/// every target is re-projected and reported as unchanged). Targets resolve by
	/// face INDEX + material — a vertex move re-solves adjacent planes, so the plane
	/// identity does not apply here. A target on a touched face gets UVs re-projected
	/// from the moved vertices; a target on an untouched face gets its UVs recomputed
	/// (unchanged) from the current loop. A target whose face was SPLIT (the face
	/// count changed and the face is affected) is reported as
	/// <see cref="OverlayDecalIssue.OverlayFaceSplit"/> in
	/// <see cref="OverlayVertexEditReport.NeedsRetarget"/> instead of being silently
	/// re-pointed at the first child face. Decals are re-validated the same way:
	/// a moved face plane can leave the stamp off-face, and a shrunken polygon can
	/// leave it outside. Throws for a blank brush id or null snapshots.
	/// </summary>
	public OverlayVertexEditReport ApplyVertexEdit( string brushId, MapVertexBrush before, MapVertexBrush after,
		IReadOnlyList<int> affectedFaces = null )
	{
		if ( string.IsNullOrWhiteSpace( brushId ) )
			throw new ArgumentException( "ApplyVertexEdit must name a brush; a blank brush id is not a brush.", nameof( brushId ) );
		if ( before is null ) throw new ArgumentNullException( nameof( before ) );
		if ( after is null ) throw new ArgumentNullException( nameof( after ) );
		var report = new OverlayVertexEditReport( brushId );
		var splitHappened = after.FaceCount != before.FaceCount;
		var affected = affectedFaces is null ? new HashSet<int>() : new HashSet<int>( affectedFaces );
		foreach ( var overlay in _overlays )
			foreach ( var target in overlay.Targets )
			{
				if ( target.BrushId != brushId ) continue;
				var index = target.ResolveByIndex( after );
				if ( index < 0 )
				{
					report.AddLost( new OverlayDecalIssue( OverlayDecalIssue.OverlayFaceLost,
						$"Overlay '{overlay.Id}' lost face {target.FaceIndex} of brush '{brushId}' after the vertex edit (face index out of range or material changed).", overlay.Id ) );
					continue;
				}
				if ( splitHappened && affected.Contains( target.FaceIndex ) )
				{
					report.AddNeedsRetarget( new OverlayDecalIssue( OverlayDecalIssue.OverlayFaceSplit,
						$"Overlay '{overlay.Id}' face {target.FaceIndex} of brush '{brushId}' was split by the vertex edit; it now covers only the first child face — re-target it explicitly.", overlay.Id ) );
					continue;
				}
				var uvs = overlay.Project( FaceLoopVertices( after, index ) );
				if ( affected.Contains( target.FaceIndex ) )
					report.AddReprojected( new OverlayVertexReprojection( overlay.Id, index, uvs ) );
				else
					report.AddUnchanged( new OverlayVertexReprojection( overlay.Id, index, uvs ) );
			}

		foreach ( var decal in _decals )
		{
			if ( decal.Face.BrushId != brushId ) continue;
			var index = decal.Face.ResolveByIndex( after );
			if ( index < 0 )
			{
				report.AddOrphanedDecal( new OverlayDecalIssue( OverlayDecalIssue.DecalFaceLost,
					$"Decal '{decal.Id}' lost face {decal.Face.FaceIndex} of brush '{brushId}' after the vertex edit (face index out of range or material changed).", decal.Id ) );
				continue;
			}
			if ( splitHappened && affected.Contains( decal.Face.FaceIndex ) )
			{
				report.AddOrphanedDecal( new OverlayDecalIssue( OverlayDecalIssue.OverlayFaceSplit,
					$"Decal '{decal.Id}' face {decal.Face.FaceIndex} of brush '{brushId}' was split by the vertex edit; re-target it explicitly.", decal.Id ) );
				continue;
			}
			var vertices = FaceLoopVertices( after, index );
			var normal = NewellNormal( vertices );
			if ( normal.LengthSquared < 1e-12f )
			{
				report.AddOrphanedDecal( new OverlayDecalIssue( OverlayDecalIssue.DecalOffFace,
					$"Decal '{decal.Id}' face {index} of brush '{brushId}' is degenerate after the vertex edit; the stamp has no face plane.", decal.Id ) );
				continue;
			}
			var offPlane = Vec3.Dot( normal, decal.Position ) - Vec3.Dot( normal, vertices[0] );
			if ( Math.Abs( offPlane ) > MapDecal.OnFaceTolerance )
				report.AddOrphanedDecal( new OverlayDecalIssue( OverlayDecalIssue.DecalOffFace,
					$"Decal '{decal.Id}' sits {offPlane:0.###} inches off its face plane after the vertex edit; the face moved out from under the stamp.", decal.Id ) );
			else if ( !MapDecal.PointInsidePolygon( decal.Position, vertices, normal ) )
				report.AddOrphanedDecal( new OverlayDecalIssue( OverlayDecalIssue.DecalOutsideFace,
					$"Decal '{decal.Id}' sits outside its face polygon after the vertex edit; the face moved out from under the stamp.", decal.Id ) );
		}
		return report;
	}

	/// <summary>The world-space vertices of one face loop of a vertex-form brush, in loop order.</summary>
	static IReadOnlyList<Vec3> FaceLoopVertices( MapVertexBrush brush, int faceIndex )
	{
		var loop = brush.Faces[faceIndex];
		var vertices = new Vec3[loop.Length];
		for ( var i = 0; i < loop.Length; i++ )
			vertices[i] = brush.Vertices[loop[i]];
		return vertices;
	}

	/// <summary>
	/// Newell normal of a vertex loop (unit, or Zero when the loop is degenerate).
	/// The vertex form carries no planes, so the face plane for decal checks is
	/// re-derived from the loop itself — the same method MapBrush's solver uses
	/// in spirit (area-weighted, winding-aware).
	/// </summary>
	static Vec3 NewellNormal( IReadOnlyList<Vec3> loop )
	{
		var n = Vec3.Zero;
		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[( i + 1 ) % loop.Count];
			n += new Vec3(( a.y - b.y ) * ( a.z + b.z ), ( a.z - b.z ) * ( a.x + b.x ), ( a.x - b.x ) * ( a.y + b.y ));
		}
		return n.LengthSquared < 1e-12f ? Vec3.Zero : n.Normal;
	}

	/// <summary>JSON form: {"overlays":[...],"decals":[...],"paths":[...]}. Brushes are not serialised (they belong to the map document).</summary>
	public JsonObject ToJson() => new()
	{
		["overlays"] = new JsonArray( _overlays.Select( o => (JsonNode)o.ToJson() ).ToArray() ),
		["decals"] = new JsonArray( _decals.Select( d => (JsonNode)d.ToJson() ).ToArray() ),
		["paths"] = new JsonArray( _paths.Select( p => (JsonNode)p.ToJson() ).ToArray() ),
	};

	/// <summary>Parse <see cref="ToJson"/> output. Missing arrays read as empty; a duplicate id / name inside the JSON throws, naming the duplicate (AddOverlay / AddDecal / AddPath throw).</summary>
	public static OverlayDecalSet FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var set = new OverlayDecalSet();
		if ( json["overlays"] is JsonArray overlays )
			foreach ( var node in overlays )
				if ( node is JsonObject obj )
					set.AddOverlay( MapOverlay.FromJson( obj ) );
		if ( json["decals"] is JsonArray decals )
			foreach ( var node in decals )
				if ( node is JsonObject obj )
					set.AddDecal( MapDecal.FromJson( obj ) );
		if ( json["paths"] is JsonArray paths )
			foreach ( var node in paths )
				if ( node is JsonObject obj )
					set.AddPath( PathTrackChain.FromJson( obj ) );
		return set;
	}

	/// <summary>Parse <see cref="ToJson"/> output from text. Blank text or a non-object throws, naming the problem.</summary>
	public static OverlayDecalSet FromJsonString( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "OverlayDecalSet JSON text is blank." );
		if ( JsonNode.Parse( json ) is not JsonObject obj )
			throw new ArgumentException( "OverlayDecalSet JSON text is not an object." );
		return FromJson( obj );
	}

	public override string ToString() => $"{_overlays.Count} overlay(s), {_decals.Count} decal(s), {_paths.Count} path chain(s)";
}

/// <summary>Small JSON helpers shared by the types in this file (Vec2 / Vec3 / float reads with named fallbacks).</summary>
static class OverlayDecalJson
{
	public static JsonArray Vec2Json( Vec2 v ) => new( (double)v.x, (double)v.y );
	public static JsonArray Vec3Json( Vec3 v ) => new( (double)v.x, (double)v.y, (double)v.z );

	public static float ReadFloat( JsonObject json, string key, float fallback )
	{
		if ( json[key] is null ) return fallback;
		try { return (float)json[key]!.GetValue<double>(); }
		catch ( Exception ex ) { throw new ArgumentException( $"Overlay/decal JSON: '{key}' is not a number.", ex ); }
	}

	public static Vec2 ReadVec2( JsonObject json, string key, Vec2 fallback )
	{
		if ( json[key] is not JsonArray pair || pair.Count != 2 ) return fallback;
		try { return new Vec2( (float)pair[0]!.GetValue<double>(), (float)pair[1]!.GetValue<double>() ); }
		catch ( Exception ex ) { throw new ArgumentException( $"Overlay/decal JSON: '{key}' is not a [x, y] pair.", ex ); }
	}

	public static Vec3 ReadVec3( JsonObject json, string key, Vec3 fallback )
	{
		if ( json[key] is not JsonArray triple || triple.Count != 3 ) return fallback;
		try
		{
			return new Vec3( (float)triple[0]!.GetValue<double>(), (float)triple[1]!.GetValue<double>(),
				(float)triple[2]!.GetValue<double>() );
		}
		catch ( Exception ex ) { throw new ArgumentException( $"Overlay/decal JSON: '{key}' is not a [x, y, z] triple.", ex ); }
	}
}
