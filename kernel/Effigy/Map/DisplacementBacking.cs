// Hammer displacement nodraw backing — Geppetto Desktop, backend only (no UI).
//
// WHAT THIS IS (Report §1.2 #9 + §2.6, r/hammer 155hlwb): displacements do not
// seal the map, so a mapper builds nodraw brushes in the same pattern as the
// displacements and groups them together — "as long as brush is the same width
// and length, it doesnt matter how tall it is". Pass 1 recorded the no-seal rule
// (Displacement.SealsMap == false) but never generated the backing. This ticket
// generates it natively and keeps it in sync.
//
// A backing brush is a box with the SAME FOOTPRINT as the displacement's base
// face (its 4 corners, in the base face's plane), extruded along the face normal
// AWAY from the room (into the solid side) by Thickness, with every face
// tools/toolsnodraw, in the same group as the displacement's base brush. Because
// nodraw is solid (ToolTexture: NoDraw without NonSolid) the box seals the leaf
// the displacement leaves open, while contributing nothing to the baked/rendered
// mesh (FaceMaterial.ContributesToBakedMesh is false for every face).
//
// DEPENDENCY STATUS (2026-11-13):
//   * Displacement (hammer-displacement-data-model) HAS landed:
//     kernel/Effigy/Map/Displacement.cs — Displacement.Create(MapBrushFace, power),
//     Face.Corners (4 ordered corners), Face.Normal, SealsMap == false. This file
//     builds against that real type. The sculpt ticket's IDisplacementSurface is
//     NOT used here: sculpt needs a mutable grid, backing needs only the base face.
//   * MapBrush (hammer-brush-core-plane-based-brushes) HAS landed (restored
//     2026-11-13 ~16:08): kernel/Effigy/Map/MapBrush.cs, namespace Effigy —
//     MapBrush.FromPlanes(IEnumerable&lt;MapBrushFace&gt;), Faces, Volume,
//     Centroid, IsValid/Validate(). Its MapBrushFace is a plane + FaceMaterial
//     (material by NAME; the default is nodraw), so nodraw IS expressible as a
//     real MapBrush: BackingBrush.ToMapBrush() below is the spec's "nodraw
//     MapBrush" return, every face tools/toolsnodraw. MapBrush itself carries
//     no group id — the group lives on BackingBrush (and on VmfBrush.GroupId
//     in the document), which is why GenerateBacking returns the BackingBrush
//     record rather than a bare MapBrush: the record holds the group, the
//     thickness, the source id and the sync state the MapBrush cannot.
//     COLLISION (integrator reconciles, not this ticket): there are now TWO
//     MapBrushFace types — Effigy.MapBrushFace (MapBrush.cs, plane + FaceMaterial)
//     and Effigy.Map.MapBrushFace (Displacement.cs, 4 corners + int MaterialId).
//     This file uses Displacement's (Effigy.Map) face because
//     Displacement.Create takes it, and fully qualifies Effigy.MapBrushFace /
//     Effigy.MapBrush in ToMapBrush() so the two never mix silently.
//   * BspBuilder (hammer-bsp-core-tree-portals-leaves) HAS landed, in namespace
//     Effigy (not Effigy.Map): BspBuilder.Build(IReadOnlyList<BspBrush>) ->
//     BspTree.IsSealed. SealGraph() below is the LeakFinder-side proof (the
//     leak ticket's ILeakPortalGraph pattern); the BSP-level proof is
//     ToBspBrush() + BspBuilder.Build, exercised by the tests.
//   * FaceMaterial / ToolTexture (hammer-face-material-uv-model) HAVE landed:
//     FaceMaterial.NoDraw, ToolTexture.NoDrawName == "tools/toolsnodraw".
//
// GROUP: a Displacement / MapBrushFace carries no group id, so the group comes
// from the caller as an int — the base brush's GroupId (VmfBrush.GroupId). The
// map-document ticket owns brush->group mapping; this file never invents a group.
// SYNC: Displacement.Face is immutable, so a "base-face change" is a new
// Displacement (or new MapBrushFace) bound to the moved/resized face.
// SyncBacking() regenerates the footprint from the current face, keeping
// GroupId / Thickness / SourceId; ValidateSync() names the break
// (footprint-moved / footprint-resized / normal-changed / backing-*) instead of
// silently following a change nobody asked for.
//
// Units: kernel inches, Z-up. Deterministic: same face + thickness -> same brush.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Effigy.Map;

/// <summary>
/// One face of a backing brush: a plane (unit outward normal + distance,
// n . x = distance) with its 4 ordered corners on the plane and its material.
/// Every backing face is tools/toolsnodraw: dropped from the baked mesh, but
/// the brush stays solid, which is what seals the map.
/// </summary>
public readonly struct BackingFace
{
	public readonly Vec3 Normal;
	public readonly float Distance;
	public readonly Vec3[] Corners;
	public readonly FaceMaterial Material;

	public BackingFace( Vec3 normal, float distance, Vec3[] corners, FaceMaterial material )
	{
		Normal = normal;
		Distance = distance;
		Corners = corners is null ? Array.Empty<Vec3>() : (Vec3[])corners.Clone();
		Material = material ?? FaceMaterial.NoDraw;
	}

	/// <summary>True when this face is tools/toolsnodraw (or an equivalent NoDraw tool texture).</summary>
	public bool IsNoDraw => Material.IsNoDraw;

	/// <summary>True when this face still blocks movement. Nodraw is solid: NoDraw without NonSolid.</summary>
	public bool IsSolid => Material.Tool?.IsSolid ?? true;

	/// <summary>Signed distance from the plane: &lt;= 0 is inside (or on) the half-space.</summary>
	public float SignedDistance( Vec3 p ) => Vec3.Dot( Normal, p ) - Distance;

	/// <summary>True when the point is inside (or on, within tolerance) this face's half-space.</summary>
	public bool ContainsPoint( Vec3 p, float tolerance = 1e-4f ) => SignedDistance( p ) <= tolerance;
}

/// <summary>
/// A nodraw backing brush for one displacement: a box with the displacement's
/// base-face footprint, extruded away from the room by <see cref="Thickness"/>,
/// every face nodraw, in the displacement's group. Excluded from rendering and
/// the baked mesh, included in the seal/compile path (see
/// <see cref="DisplacementBacking"/>).
/// </summary>
public sealed class BackingBrush
{
	/// <summary>Caller-supplied id of the displacement this backs ("" when unknown). Carried, never invented.</summary>
	public string SourceId { get; }
	/// <summary>Group of the displacement's base brush (VmfBrush.GroupId). 0 = ungrouped.</summary>
	public int GroupId { get; }
	/// <summary>Extrusion depth behind the base face, inches. Same width/length as the face; height is free.</summary>
	public float Thickness { get; }
	/// <summary>Unit normal of the base face (points into the room; the box extends the other way).</summary>
	public Vec3 Normal { get; }
	/// <summary>The 4 footprint corners, in the base face's corner order. A copy; never aliased.</summary>
	public IReadOnlyList<Vec3> BaseCorners { get; }
	/// <summary>The 6 faces: top (coplanar with the base face), bottom, and 4 sides. All nodraw.</summary>
	public IReadOnlyList<BackingFace> Faces { get; }
	/// <summary>Footprint area, square inches (Newell quad area).</summary>
	public float FootprintArea { get; }
	/// <summary>Footprint area times thickness.</summary>
	public float Volume => FootprintArea * Thickness;
	/// <summary>Average of the 8 box corners.</summary>
	public Vec3 Centroid { get; }

	/// <summary>True: every face is nodraw, so the brush contributes nothing to rendering or the baked mesh.</summary>
	public bool IsRenderExcluded => true;
	/// <summary>True: a structural (non-detail) solid for the BSP/seal path. Nodraw seals; it does not draw.</summary>
	public bool IsSealBrush => true;
	/// <summary>False: no face of a backing brush bakes.</summary>
	public bool ContributesToBakedMesh => false;

	BackingBrush( string sourceId, int groupId, float thickness, Vec3 normal,
		Vec3[] baseCorners, BackingFace[] faces, float footprintArea, Vec3 centroid )
	{
		SourceId = sourceId ?? "";
		GroupId = groupId;
		Thickness = thickness;
		Normal = normal;
		BaseCorners = (Vec3[])baseCorners.Clone();
		Faces = (BackingFace[])faces.Clone();
		FootprintArea = footprintArea;
		Centroid = centroid;
	}

	/// <summary>
	/// Build a backing box from raw footprint corners (4 ordered, planar) and
	/// their face normal. The box top is coplanar with the footprint plane; the
	/// box extends Thickness along -normal. Throws ArgumentException naming the
	/// problem for a degenerate footprint or a zero normal, and
	/// ArgumentOutOfRangeException for a non-positive thickness.
	/// </summary>
	public static BackingBrush FromFootprint( Vec3[] corners, Vec3 normal,
		int groupId = 0, float thickness = DisplacementBacking.DefaultThickness, string sourceId = "" )
	{
		if ( corners is null || corners.Length != 4 )
			throw new ArgumentException( "A backing brush needs exactly 4 footprint corners.", nameof( corners ) );
		if ( normal.LengthSquared < 1e-12f )
			throw new ArgumentException( "A backing brush needs a non-zero face normal.", nameof( normal ) );
		if ( !float.IsFinite( thickness ) || thickness <= 0f )
			throw new ArgumentOutOfRangeException( nameof( thickness ), thickness,
				"A backing brush needs a positive thickness: same width/length as the face, any positive height." );
		var n = normal.Normal;
		var area = QuadArea( corners );
		if ( area <= 1e-6f )
			throw new ArgumentException( "A backing brush needs a non-degenerate footprint (zero-area face).", nameof( corners ) );

		var c = (Vec3[])corners.Clone();
		var b = new Vec3[4];
		for ( var i = 0; i < 4; i++ ) b[i] = c[i] - n * thickness;
		var d0 = Vec3.Dot( n, c[0] );
		var d1 = Vec3.Dot( -n, b[0] ); // == -d0 + thickness for a unit normal
		var nodraw = FaceMaterial.NoDraw;
		var faces = new BackingFace[6];
		faces[0] = new BackingFace( n, d0, new[] { c[0], c[1], c[2], c[3] }, nodraw );
		faces[1] = new BackingFace( -n, d1, new[] { b[0], b[1], b[2], b[3] }, nodraw );
		var centre = ( c[0] + c[1] + c[2] + c[3] ) * 0.25f;
		for ( var i = 0; i < 4; i++ )
		{
			var j = ( i + 1 ) % 4;
			var edge = ( c[j] - c[i] ).Normal;
			var outward = Vec3.Cross( edge, n ).Normal;
			// The cross product's sign depends on corner winding; flip it when it
			// points at the quad centre instead of away from it.
			if ( Vec3.Dot( outward, centre - ( c[i] + c[j] ) * 0.5f ) > 0f ) outward = -outward;
			faces[2 + i] = new BackingFace( outward, Vec3.Dot( outward, c[i] ),
				new[] { c[i], c[j], b[j], b[i] }, nodraw );
		}
		var centroid = Vec3.Zero;
		foreach ( var p in c ) centroid += p;
		foreach ( var p in b ) centroid += p;
		centroid *= 0.125f;
		return new BackingBrush( sourceId, groupId, thickness, n, c, faces, area, centroid );
	}

	/// <summary>Quad area: half the length of the cross of the diagonals (exact for a parallelogram, Newell for a general quad).</summary>
	static float QuadArea( Vec3[] corners ) =>
		Vec3.Cross( corners[2] - corners[0], corners[3] - corners[1] ).Length * 0.5f;

	/// <summary>True when the point is inside (or on, within tolerance) all six half-spaces.</summary>
	public bool ContainsPoint( Vec3 p, float tolerance = 1e-4f )
	{
		foreach ( var face in Faces )
			if ( !face.ContainsPoint( p, tolerance ) ) return false;
		return true;
	}

	/// <summary>The indices of the faces that contribute to the baked mesh: none. The nodraw faces stay in the brush; they just do not bake.</summary>
	public IReadOnlyList<int> BakedFaceIndices() =>
		FaceMaterial.BakedFaceIndices( Faces.Select( f => f.Material ).ToList() );

	/// <summary>
	/// True when this backing still matches the displacement's current base
	/// face: same normal and same four footprint corners within
	/// <paramref name="tolerance"/> inches. A moved or resized base face makes
	/// this false — see <see cref="DisplacementBacking.ValidateSync"/>.
	/// </summary>
	public bool FootprintMatches( Displacement displacement, float tolerance = DisplacementBacking.DefaultTolerance )
	{
		if ( displacement is null ) throw new ArgumentNullException( nameof( displacement ) );
		return FootprintMatches( displacement.Face, tolerance );
	}

	/// <summary>Face-level <see cref="FootprintMatches(Displacement, float)"/>.</summary>
	public bool FootprintMatches( MapBrushFace face, float tolerance = DisplacementBacking.DefaultTolerance )
	{
		if ( Vec3.Dot( Normal, face.Normal ) < 1f - 1e-6f ) return false;
		for ( var i = 0; i < 4; i++ )
			if ( !BaseCorners[i].AlmostEquals( face.Corners[i], tolerance ) ) return false;
		return true;
	}

	/// <summary>
	/// Named defects, same convention as Displacement.Validate(): empty means
	/// valid. A backing that fails here must not enter the seal path silently.
	/// </summary>
	public IReadOnlyList<string> Validate()
	{
		var defects = new List<string>();
		if ( Faces.Count != 6 ) defects.Add( "face-count" );
		for ( var i = 0; i < Faces.Count; i++ )
		{
			if ( !Faces[i].IsNoDraw ) defects.Add( "face-not-nodraw" );
			if ( Faces[i].Normal.Length < 1e-6f ) defects.Add( "face-zero-normal" );
			foreach ( var p in Faces[i].Corners )
				if ( MathF.Abs( Faces[i].SignedDistance( p ) ) > 1e-3f ) { defects.Add( "face-non-planar" ); break; }
		}
		if ( !float.IsFinite( Thickness ) || Thickness <= 0f ) defects.Add( "thickness-not-positive" );
		if ( FootprintArea <= 1e-6f ) defects.Add( "footprint-degenerate" );
		if ( !( Volume > 0f ) || !float.IsFinite( Volume ) ) defects.Add( "volume-not-positive" );
		return defects;
	}

	public bool IsValid => Validate().Count == 0;

	/// <summary>
	/// The spec's return, as a real brush: this backing as a nodraw
	/// <see cref="Effigy.MapBrush"/> — all six faces tools/toolsnodraw, same
	/// planes, same volume. Fully qualified: <c>MapBrushFace</c> unqualified
	/// would resolve to Displacement's Effigy.Map face, not the brush core's
	/// Effigy face. The group is NOT on the MapBrush (it carries no group);
	/// it stays on this record and on <see cref="ToVmfBrush"/> output.
	/// </summary>
	public Effigy.MapBrush ToMapBrush() =>
		Effigy.MapBrush.FromPlanes( Faces.Select( f =>
			new Effigy.MapBrushFace( f.Normal, f.Distance, f.Material ) ) );

	/// <summary>
	/// The seal path TODAY: this backing as a structural (non-detail) BSP brush
	/// — its 6 planes join BspBuilder.Build, and a leaf the displacement left
	/// open becomes solid there. The name carries the source id for diagnostics.
	/// </summary>
	public BspBrush ToBspBrush() =>
		BspBrush.FromPlanes( Faces.Select( f => new BspPlane( f.Normal, f.Distance ) ),
			isDetail: false, name: "displacement-backing:" + SourceId );

	/// <summary>
	/// The VMF document path TODAY: a VmfBrush with all six faces
	/// tools/toolsnodraw, in this backing's group, carrying no displacement.
	/// Plane points are the face corners (three define the plane; the writer
	/// emits them verbatim, so no compiler drift).
	/// </summary>
	public VmfBrush ToVmfBrush( int brushId )
	{
		var brush = new VmfBrush { Id = brushId, GroupId = GroupId };
		for ( var i = 0; i < Faces.Count; i++ )
		{
			var face = Faces[i];
			var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
			brush.Faces.Add( new VmfFace
			{
				Id = i,
				PlanePoints = new[]
				{
					ToVmf( face.Corners[0] ), ToVmf( face.Corners[1] ), ToVmf( face.Corners[2] ),
				},
				Material = ToolTexture.NoDrawName,
				UAxis = new VmfUvAxis( ToVmf( uAxis ), 0, 0.25 ),
				VAxis = new VmfUvAxis( ToVmf( vAxis ), 0, 0.25 ),
				Rotation = 0,
				LightmapScale = 16,
				SmoothingGroups = 0,
			} );
		}
		return brush;
	}

	static VmfVec ToVmf( Vec3 p ) => new( p.x, p.y, p.z );

	/// <summary>JSON form: {"sourceId","groupId","thickness","normal":[x,y,z],"corners":[[x,y,z] x4]} — the map-document form.</summary>
	public JsonObject ToJson() => new()
	{
		["sourceId"] = SourceId,
		["groupId"] = GroupId,
		["thickness"] = (double)Thickness,
		["normal"] = new JsonArray( (double)Normal.x, (double)Normal.y, (double)Normal.z ),
		["corners"] = CornersJson( BaseCorners ),
	};

	static JsonArray CornersJson( IReadOnlyList<Vec3> corners )
	{
		var array = new JsonArray();
		foreach ( var p in corners )
			array.Add( new JsonArray( (double)p.x, (double)p.y, (double)p.z ) );
		return array;
	}

	static Vec3 Vec( JsonArray pair, string what )
	{
		if ( pair is null || pair.Count != 3 )
			throw new ArgumentException( $"BackingBrush JSON: '{what}' is not an [x, y, z] triple." );
		try { return new Vec3( (float)pair[0]!.GetValue<double>(), (float)pair[1]!.GetValue<double>(), (float)pair[2]!.GetValue<double>() ); }
		catch ( Exception ex ) { throw new ArgumentException( $"BackingBrush JSON: '{what}' is not numbers.", ex ); }
	}

	/// <summary>Parse <see cref="ToJson"/> output. Unknown fields are ignored so older documents keep loading.</summary>
	public static BackingBrush FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		if ( json["corners"] is not JsonArray cornerArray || cornerArray.Count != 4 )
			throw new ArgumentException( "BackingBrush JSON is missing 'corners' (4 [x, y, z] triples)." );
		var corners = new Vec3[4];
		for ( var i = 0; i < 4; i++ )
			corners[i] = Vec( cornerArray[i] as JsonArray, $"corners[{i}]" );
		var normal = json["normal"] is JsonArray normalArray ? Vec( normalArray, "normal" ) : Vec3.Zero;
		var group = json["groupId"] is null ? 0 : json["groupId"]!.GetValue<int>();
		var thickness = json["thickness"] is null ? DisplacementBacking.DefaultThickness : (float)json["thickness"]!.GetValue<double>();
		var source = json["sourceId"]?.GetValue<string>() ?? "";
		return FromFootprint( corners, normal, group, thickness, source );
	}

	public string ToJsonString() => ToJson().ToJsonString();

	/// <summary>Parse <see cref="ToJsonString"/> output.</summary>
	public static BackingBrush FromJsonString( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "BackingBrush JSON text is blank." );
		if ( JsonNode.Parse( json ) is not JsonObject obj )
			throw new ArgumentException( "BackingBrush JSON text is not an object." );
		return FromJson( obj );
	}
}

/// <summary>
/// Nodraw backing-brush generation for displacements (hammer-displacement-
/// nodraw-backing). Backend only; no UI. See the file header for the dependency
/// status: Displacement and BspBuilder have landed; MapBrush has not, so the
/// backing is carried as <see cref="BackingBrush"/> with landed adapters.
/// </summary>
public static class DisplacementBacking
{
	/// <summary>Default backing depth, inches: same width/length as the face, any positive height — 16 is a Source grid step.</summary>
	public const float DefaultThickness = 16f;

	/// <summary>Default footprint-match tolerance, inches (matches Displacement.ValidateTolerance).</summary>
	public const float DefaultTolerance = 1e-3f;

	/// <summary>
	/// GenerateBacking(displacement): the nodraw backing brush for a displacement
	/// — same footprint as its base face, same group as the base brush, every
	/// face tools/toolsnodraw. The group id comes from the caller (the base
	/// brush's GroupId); 0 is ungrouped. Throws ArgumentNullException for a null
	/// displacement, ArgumentException for an invalid base face, and
	/// ArgumentOutOfRangeException for a non-positive thickness.
	/// </summary>
	public static BackingBrush GenerateBacking( Displacement displacement,
		int groupId = 0, float thickness = DefaultThickness, string sourceId = "" )
	{
		if ( displacement is null ) throw new ArgumentNullException( nameof( displacement ) );
		return GenerateBacking( displacement.Face, groupId, thickness, sourceId );
	}

	/// <summary>Face-level GenerateBacking: for callers holding the base face rather than a bound displacement.</summary>
	public static BackingBrush GenerateBacking( MapBrushFace face,
		int groupId = 0, float thickness = DefaultThickness, string sourceId = "" )
	{
		var defects = face.Validate();
		if ( defects.Count > 0 )
			throw new ArgumentException(
				$"Cannot generate a backing brush for an invalid base face: {string.Join( ", ", defects )}.", nameof( face ) );
		return BackingBrush.FromFootprint( face.Corners, face.Normal, groupId, thickness, sourceId );
	}

	/// <summary>
	/// SyncBacking on base-face change: regenerate the backing from the
	/// displacement's CURRENT base face, keeping GroupId, Thickness and
	/// SourceId. The old brush is not mutated — a backing brush is a value-like
	/// record of the face it was built from. Throws for a null backing or a
	/// null/invalid displacement.
	/// </summary>
	public static BackingBrush SyncBacking( BackingBrush backing, Displacement displacement )
	{
		if ( backing is null ) throw new ArgumentNullException( nameof( backing ) );
		if ( displacement is null ) throw new ArgumentNullException( nameof( displacement ) );
		return SyncBacking( backing, displacement.Face );
	}

	/// <summary>Face-level SyncBacking.</summary>
	public static BackingBrush SyncBacking( BackingBrush backing, MapBrushFace face )
	{
		if ( backing is null ) throw new ArgumentNullException( nameof( backing ) );
		var defects = face.Validate();
		if ( defects.Count > 0 )
			throw new ArgumentException(
				$"Cannot sync a backing brush to an invalid base face: {string.Join( ", ", defects )}.", nameof( face ) );
		return BackingBrush.FromFootprint( face.Corners, face.Normal, backing.GroupId, backing.Thickness, backing.SourceId );
	}

	/// <summary>True when the backing still matches the displacement's current base face (see BackingBrush.FootprintMatches).</summary>
	public static bool IsInSync( BackingBrush backing, Displacement displacement, float tolerance = DefaultTolerance )
	{
		if ( backing is null ) throw new ArgumentNullException( nameof( backing ) );
		if ( displacement is null ) throw new ArgumentNullException( nameof( displacement ) );
		return backing.FootprintMatches( displacement, tolerance );
	}

	/// <summary>
	/// The sync break as named defects, never a silent follow: empty means in
	/// sync. "footprint-moved" — all four corners shifted by the same delta
	/// (the face translated); "footprint-resized" — the corners changed any
	/// other way (the face resized or reshaped); "normal-changed" — the face
	/// tilted; "backing-&lt;defect&gt;" — the backing itself is invalid. A sync
	/// break is a named issue for the map validation engine, not a quiet fix.
	/// </summary>
	public static IReadOnlyList<string> ValidateSync( BackingBrush backing, Displacement displacement, float tolerance = DefaultTolerance )
	{
		if ( backing is null ) throw new ArgumentNullException( nameof( backing ) );
		if ( displacement is null ) throw new ArgumentNullException( nameof( displacement ) );
		return ValidateSync( backing, displacement.Face, tolerance );
	}

	/// <summary>Face-level ValidateSync.</summary>
	public static IReadOnlyList<string> ValidateSync( BackingBrush backing, MapBrushFace face, float tolerance = DefaultTolerance )
	{
		if ( backing is null ) throw new ArgumentNullException( nameof( backing ) );
		var defects = new List<string>();
		foreach ( var d in backing.Validate() ) defects.Add( "backing-" + d );
		if ( Vec3.Dot( backing.Normal, face.Normal ) < 1f - 1e-6f )
			defects.Add( "normal-changed" );
		var moved = true;
		var deltas = new Vec3[4];
		for ( var i = 0; i < 4; i++ )
		{
			deltas[i] = face.Corners[i] - backing.BaseCorners[i];
			if ( !backing.BaseCorners[i].AlmostEquals( face.Corners[i], tolerance ) ) moved = false;
		}
		if ( !moved )
		{
			var uniform = true;
			for ( var i = 1; i < 4; i++ )
				if ( !deltas[i].AlmostEquals( deltas[0], tolerance ) ) uniform = false;
			defects.Add( uniform ? "footprint-moved" : "footprint-resized" );
		}
		return defects;
	}

	/// <summary>True when the backing brush's solid covers the portal's centre — the gap this backing plugs.</summary>
	public static bool SealsPortal( BackingBrush backing, LeakFinder.LeakPortal portal )
	{
		if ( backing is null ) throw new ArgumentNullException( nameof( backing ) );
		if ( portal is null ) throw new ArgumentNullException( nameof( portal ) );
		return backing.ContainsPoint( portal.Center );
	}

	/// <summary>
	/// Seal-path inclusion via the leak graph: a copy of the graph with every
	/// portal this backing covers closed (IsOpen false). A map whose only leak
	/// is the displacement's hole seals after this; the flood-fill then reports
	/// sealed. The graph itself is never mutated. This is the LeakFinder-side
	/// proof; the BSP-side inclusion is BackingBrush.ToBspBrush().
	/// </summary>
	public static LeakFinder.LeakGraph SealGraph( LeakFinder.LeakGraph graph, BackingBrush backing )
	{
		if ( graph is null ) throw new ArgumentNullException( nameof( graph ) );
		return SealGraph( graph, new[] { backing } );
	}

	/// <summary>SealGraph for several backings: a portal stays open only when no backing covers it.</summary>
	public static LeakFinder.LeakGraph SealGraph( LeakFinder.LeakGraph graph, IEnumerable<BackingBrush> backings )
	{
		if ( graph is null ) throw new ArgumentNullException( nameof( graph ) );
		if ( backings is null ) throw new ArgumentNullException( nameof( backings ) );
		var list = backings.ToList();
		foreach ( var b in list )
			if ( b is null ) throw new ArgumentException( "Backing list contains a null brush.", nameof( backings ) );
		var sealedGraph = new LeakFinder.LeakGraph();
		foreach ( var leaf in graph.Leaves ) sealedGraph.AddLeaf( leaf );
		foreach ( var portal in graph.Portals )
		{
			var covered = false;
			foreach ( var b in list )
				if ( b.ContainsPoint( portal.Center ) ) { covered = true; break; }
			sealedGraph.AddPortal( new LeakFinder.LeakPortal( portal.Id, portal.LeafA, portal.LeafB,
				portal.Center, portal.IsOpen && !covered ) );
		}
		return sealedGraph;
	}
}
