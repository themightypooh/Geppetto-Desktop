using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Map;

// RoomHelpers — one-call sealed room + doorway/window cutout helpers
// (intent-to-geometry). Board: hammer-room-doorway-helpers (Hammer Desktop P2b).
//
// WHAT IT DOES. One call makes a room, not six brushes and a leak hunt:
//   CreateRoom(length, width, height, wallThickness) -> a sealed room
//   (floor + 4 walls + ceiling as six convex solids), nodraw on every face
//   the player can never see, a dev texture on the visible faces (or the
//   caller's materials when dev textures are off).
//   CutDoorway(room, wall, width, height) -> the same room with a doorway
//   cut in the named wall (opening from the floor up, lintel preserved).
//   CutWindow(room, wall, width, height, sillHeight) -> the same room with
//   a window cut in the named wall (sill below, lintel above, both preserved).
// Every result is validated sealed by the leak finder (LeakFinder) before
// it is returned: the validation builds a portal graph from the room's
// actual geometry (see BuildLeakGraph) and flood-fills it from the player
// start. A room that fails validation comes back with Ok false and a named
// Reason — never a silently unsealed room.
//
// GEOMETRY CONVENTIONS. Kernel inches, Z-up. The room interior is centred
// on the origin in X/Y with the floor top at Z = 0:
//   interior X in [-length/2, +length/2], Y in [-width/2, +width/2],
//   Z in [0, height]. Outer shell adds wallThickness on every side.
// Walls: North is +Y, South -Y, East +X, West -X. North/South walls span
// the full outer X extent (length + 2*thickness); East/West walls span the
// interior Y extent (width) between them; floor and ceiling span the full
// outer footprint. Along-wall coordinate u runs +X on North/South walls and
// +Y on East/West walls, measured from the wall centre; v is height above
// the floor. Player start is (0, 0, 32) clamped inside the interior.
//
// DEPENDENCY STATUS (2026-11-13, read before touching this file).
// This ticket depends on hammer-brush-primitives-block-tool,
// hammer-brush-clip-two-point, hammer-brush-csg-subtract-merge-hollow and
// hammer-face-material-uv-model. Status when this file was finished:
//   * MapBrush LANDED 2026-11-13 ~16:08 (hammer-brush-core, DONE):
//     kernel/Effigy/Map/MapBrush.cs, namespace Effigy. Faces are
//     MapBrushFace (unit outward normal + distance + FaceMaterial, i.e.
//     material NAME + FaceUv — not an int slot, not a flat MapFace type,
//     which does not exist). MapBrush.FromPlanes(IEnumerable<MapBrushFace>),
//     Volume float, Validate() -> IReadOnlyList<MapBrushDefect> (structured
//     defects with Codes, not a string list), ToMesh(materialSlot) bakes to
//     a PolyMesh (nodraw faces dropped), MapBrush.Box(min, max, material).
//     NEITHER sibling assumption matched the landed API:
//       - MapBrushPrimitives assumed MapFace(MapPlane, int material-slot,
//         ...) — that type never existed. Its file is the tree's ONLY
//         compile failure (4x CS0246 MapFace, 2026-11-13 ~16:10 full-tree
//         build); its task is marked DONE by hammer-03 but the file does
//         not compile against the landed MapBrush. Coordinator: needs a
//         rewrite to MapBrushFace (or a revert) before the final build.
//       - MapClip assumed a flat MapFace(Vec3, float, string, ...) and
//         Volume double; it was RECONCILED to the landed API 16:11
//         (MapBrushFace, FaceUv) and compiles.
//     The landed ToolTexture registry says nodraw is
//     "tools/toolsnodraw"; this file uses that name (MapClip's old
//     "tools/nodraw" string is not the registry name).
//     RoomHelpers does NOT reference MapBrush, MapBrushPrimitives or
//     MapClip: a wall with a doorway is non-convex, so it cannot be one
//     MapBrush anyway, and MapBrushPrimitives does not compile. It is built
//     on the types that DO exist and compile:
//       - MapCsg.MeshFromPlanes (the plane-intersection vertex solve — the
//         FromPlanes equivalent MapCsg's own header names) builds every
//         box from its six face planes, so the room is plane-based
//         throughout and the MapBrush swap is thin and now concrete:
//         MapBrush -> PolyMesh via ToMesh(materialSlot), PolyMesh ->
//         MapBrush via MapBrush.FromPlanes(MapBrushFace list built from
//         MapCsg.BrushPlanes + FaceMaterial names).
//       - MapCsg.TrySubtract (Subtract, the "carve that works") cuts the
//         openings; the cut faces are reclassified geometrically afterwards
//         (see ReclassifyFaces), so the result does not depend on which
//         boolean provider ran or how it labels new faces.
//       - LeakFinder.FindLeak over a graph built from the room geometry
//         (BuildLeakGraph) is the sealed-room validation.
//       - ToolTexture.NoDrawName / IsNoDraw is the nodraw authority;
//         FaceMaterial's string material names are the material identity
//         (PolyMesh Face.Material is an int slot into this room's
//         Materials name table, the same slot convention MapCsg.BrushPlane
//         uses).
//     The MapClip split path (wall -> lintel/sill/jamb pieces by clipping,
//     now against the landed MapBrushFace API) is a valid alternative
//     implementation of the same cuts and would produce the same openings;
//     CSG subtract was chosen because MapCsg is the landed op and one
//     subtract keeps the wall one solid.
//   * GreyboxDefaults (hammer-greybox-workflow-defaults) has NOT landed
//     (in-progress, hammer-40). The dev-texture option here is
//     self-contained (RoomOptions): when it lands, the caller passes its
//     dev material set in as DevMaterial / the per-part materials instead
//     of these defaults.
//   * There is NO Carve operation anywhere in this file, ever.
//
// RESULTS ARE DATA, NOT EXCEPTIONS. A failed cut returns a RoomResult with
// Ok false, a machine-readable Reason slug and a human Message, sharing
// the input room's brushes (the room is unchanged). Invalid room dimensions
// are the one exception: CreateRoom throws ArgumentOutOfRangeException, the
// same way MapBrushPrimitives throws for a non-positive dimension.
// Reason slugs:
//   room-unknown-wall            wall enum value is not a real wall
//   room-opening-bad-size        opening width/height (or sill) is not a
//                                finite positive number
//   room-opening-too-wide        the opening (+ its 1-unit jambs) does not
//                                fit the wall's length
//   room-opening-no-lintel       the opening leaves less than 1 unit of
//                                lintel above it
//   room-window-no-sill          a window sill under 1 unit (that is a
//                                doorway — use CutDoorway)
//   room-opening-overlaps-existing  the opening is within 1 unit of, or
//                                overlaps, an existing opening in that wall
//   room-wall-not-found          the named wall brush is missing (a room
//                                not built by CreateRoom)
//   room-cutter-failed           the cutter solid could not be built
//                                (internal error, not a caller error)
//   room-cut-volume-mismatch     the boolean result's volume is not the
//                                wall volume minus the opening volume
//   room-leak                    the room failed sealed validation after
//                                the cut; Message carries the gap location
//   <any CsgResult Reason>       passed through unchanged when the boolean
//                                refuses (e.g. "csg-no-boolean-provider")
//
// NO DESKTOP UI. Backend / data-model / API / headless only, per Pooh
// 2026-11-13.

/// <summary>Which wall of a room an opening goes in. North is +Y, South -Y, East +X, West -X.</summary>
public enum RoomWall
{
	North,
	South,
	East,
	West,
}

/// <summary>Whether an opening starts at the floor (doorway) or floats with a sill (window).</summary>
public enum RoomOpeningKind
{
	Doorway,
	Window,
}

/// <summary>Caller choices for a room. Defaults are the greybox defaults: dev texture on every visible face, nodraw on every unseen face.</summary>
public sealed class RoomOptions
{
	/// <summary>Material for visible wall faces when <see cref="DevTextures"/> is false.</summary>
	public string WallMaterial { get; set; } = RoomHelpers.DefaultDevMaterial;
	/// <summary>Material for the visible (top) floor face when <see cref="DevTextures"/> is false.</summary>
	public string FloorMaterial { get; set; } = RoomHelpers.DefaultDevMaterial;
	/// <summary>Material for the visible (bottom) ceiling face when <see cref="DevTextures"/> is false.</summary>
	public string CeilingMaterial { get; set; } = RoomHelpers.DefaultDevMaterial;
	/// <summary>Dev material for every visible face when <see cref="DevTextures"/> is true. The greybox dev set, once GreyboxDefaults lands, is passed in here.</summary>
	public string DevMaterial { get; set; } = RoomHelpers.DefaultDevMaterial;
	/// <summary>True (default) = visible faces get <see cref="DevMaterial"/>; false = the per-part materials above apply.</summary>
	public bool DevTextures { get; set; } = true;
	/// <summary>Material for every unseen face. Default is the tool-texture registry's nodraw.</summary>
	public string NoDrawMaterial { get; set; } = ToolTexture.NoDrawName;
}

/// <summary>One opening cut in a wall: its kind, wall, size, and wall-local rectangle (u along the wall from its centre, v height above the floor).</summary>
public sealed class RoomOpening
{
	public RoomOpening( RoomOpeningKind kind, RoomWall wall, float width, float height, float sillHeight, float offsetAlongWall )
	{
		Kind = kind;
		Wall = wall;
		Width = width;
		Height = height;
		SillHeight = sillHeight;
		OffsetAlongWall = offsetAlongWall;
		U0 = offsetAlongWall - width * 0.5f;
		U1 = offsetAlongWall + width * 0.5f;
		V0 = sillHeight;
		V1 = sillHeight + height;
	}

	public RoomOpeningKind Kind { get; }
	public RoomWall Wall { get; }
	public float Width { get; }
	public float Height { get; }
	public float SillHeight { get; }
	public float OffsetAlongWall { get; }
	/// <summary>Along-wall start of the opening (u = 0 is the wall centre).</summary>
	public float U0 { get; }
	/// <summary>Along-wall end of the opening.</summary>
	public float U1 { get; }
	/// <summary>Bottom of the opening (height above the floor; 0 for a doorway).</summary>
	public float V0 { get; }
	/// <summary>Top of the opening.</summary>
	public float V1 { get; }
}

/// <summary>One solid piece of a room: floor, one wall, or ceiling. Face.Material on <see cref="Mesh"/> is an int slot into the owning room's <see cref="RoomResult.Materials"/> table; <see cref="FaceMaterials"/> is the same list of names in face order.</summary>
public sealed class RoomBrush
{
	public RoomBrush( string name, RoomWall? wall, PolyMesh mesh, IReadOnlyList<string> faceMaterials )
	{
		Name = name;
		Wall = wall;
		Mesh = mesh;
		FaceMaterials = faceMaterials;
	}

	public string Name { get; }
	/// <summary>The wall this brush is, or null for the floor and ceiling.</summary>
	public RoomWall? Wall { get; }
	public PolyMesh Mesh { get; }
	public IReadOnlyList<string> FaceMaterials { get; }
}

/// <summary>The result of building or cutting a room: the six (or fewer, never more) solids, the openings cut in them, the material name table, and the sealed-room validation.</summary>
public sealed class RoomResult
{
	internal RoomResult( float length, float width, float height, float wallThickness,
		IReadOnlyList<RoomBrush> brushes, IReadOnlyList<RoomOpening> openings,
		IReadOnlyList<string> materials, Vec3 playerStart,
		LeakFinder.LeakResult leakCheck, bool ok, string reason, string message )
	{
		Length = length;
		Width = width;
		Height = height;
		WallThickness = wallThickness;
		Brushes = brushes;
		Openings = openings;
		Materials = materials;
		PlayerStart = playerStart;
		InteriorMin = new Vec3( -length * 0.5f, -width * 0.5f, 0f );
		InteriorMax = new Vec3( length * 0.5f, width * 0.5f, height );
		LeakCheck = leakCheck;
		Ok = ok;
		Reason = reason;
		Message = message;
	}

	public float Length { get; }
	public float Width { get; }
	public float Height { get; }
	public float WallThickness { get; }
	public IReadOnlyList<RoomBrush> Brushes { get; }
	public IReadOnlyList<RoomOpening> Openings { get; }
	/// <summary>Material name table: Mesh face slot N on any brush means Materials[N].</summary>
	public IReadOnlyList<string> Materials { get; }
	public Vec3 PlayerStart { get; }
	public Vec3 InteriorMin { get; }
	public Vec3 InteriorMax { get; }
	/// <summary>The leak-finder result the room was validated with (null only on a failed cut that returned the input room's validation — see Ok/Reason).</summary>
	public LeakFinder.LeakResult LeakCheck { get; }
	/// <summary>False when the last cut was refused (Reason names why); the brushes are then the input room's, unchanged.</summary>
	public bool Ok { get; }
	/// <summary>Named Reason slug for Ok == false, or "room-leak" when the room itself failed sealed validation; null when the room is sealed.</summary>
	public string Reason { get; }
	public string Message { get; }
	/// <summary>True when the room itself is sealed: the leak finder found no path from the player start to the void. (A refused cut leaves Ok false with a Reason, but the room underneath is still sealed — check Ok for the cut, IsSealed for the room.)</summary>
	public bool IsSealed => LeakCheck is not null && !LeakCheck.IsLeaked;

	public RoomBrush FindBrush( RoomWall wall )
	{
		foreach ( var b in Brushes ) if ( b.Wall == wall ) return b;
		return null;
	}
}

/// <summary>One-call sealed rooms and doorway/window cutouts. See the file header for the dependency status and the Reason slugs.</summary>
public static class RoomHelpers
{
	/// <summary>Dev measure texture used for visible faces by default (the greybox dev material until GreyboxDefaults lands).</summary>
	public const string DefaultDevMaterial = "dev/dev_measuregeneric01";
	/// <summary>Minimum solid left around an opening: 1 unit of jamb on each side, 1 unit of lintel above, 1 unit of sill below, 1 unit between openings.</summary>
	public const float MinRemainder = 1f;
	/// <summary>How far the opening cutter extends past each wall face, so the boolean never has to decide a flush coplanar face.</summary>
	public const float CutterOverhang = 2f;

	/// <summary>Build a sealed room: floor + 4 walls + ceiling as six convex solids. Interior centred on the origin in X/Y, floor top at Z = 0, outer shell wallThickness on every side. Visible faces get the dev material (or the per-part materials when <see cref="RoomOptions.DevTextures"/> is false); every unseen face gets nodraw. The result is validated sealed by the leak finder before it returns.</summary>
	/// <exception cref="ArgumentOutOfRangeException">Any dimension or the thickness is not a finite number greater than 0.</exception>
	public static RoomResult CreateRoom( float length, float width, float height, float wallThickness, RoomOptions options = null )
	{
		RequirePositive( length, nameof( length ) );
		RequirePositive( width, nameof( width ) );
		RequirePositive( height, nameof( height ) );
		RequirePositive( wallThickness, nameof( wallThickness ) );
		options ??= new RoomOptions();
		var table = new List<string>();
		var t = wallThickness;
		var hx = length * 0.5f;
		var hy = width * 0.5f;
		var nodraw = options.NoDrawMaterial;
		var wallVis = VisibleMaterial( options, options.WallMaterial );
		var floorVis = VisibleMaterial( options, options.FloorMaterial );
		var ceilVis = VisibleMaterial( options, options.CeilingMaterial );
		var brushes = new List<RoomBrush>
		{
			BuildBox( "Floor", null, new Vec3( -hx - t, -hy - t, -t ), new Vec3( hx + t, hy + t, 0f ),
				n => n.z > 0.5f ? floorVis : nodraw, table ),
			BuildBox( "NorthWall", RoomWall.North, new Vec3( -hx - t, hy, 0f ), new Vec3( hx + t, hy + t, height ),
				n => n.y < -0.5f ? wallVis : nodraw, table ),
			BuildBox( "SouthWall", RoomWall.South, new Vec3( -hx - t, -hy - t, 0f ), new Vec3( hx + t, -hy, height ),
				n => n.y > 0.5f ? wallVis : nodraw, table ),
			BuildBox( "EastWall", RoomWall.East, new Vec3( hx, -hy, 0f ), new Vec3( hx + t, hy, height ),
				n => n.x < -0.5f ? wallVis : nodraw, table ),
			BuildBox( "WestWall", RoomWall.West, new Vec3( -hx - t, -hy, 0f ), new Vec3( -hx, hy, height ),
				n => n.x > 0.5f ? wallVis : nodraw, table ),
			BuildBox( "Ceiling", null, new Vec3( -hx - t, -hy - t, height ), new Vec3( hx + t, hy + t, height + t ),
				n => n.z < -0.5f ? ceilVis : nodraw, table ),
		};
		var playerStart = new Vec3( 0f, 0f, height > 33f ? 32f : height * 0.5f );
		return Finish( length, width, height, t, brushes, Array.Empty<RoomOpening>(), table, playerStart, true, null, null );
	}

	/// <summary>Cut a doorway in the named wall: an opening of the given width and height from the floor up, centred offsetAlongWall from the wall centre (0 = centred). The lintel above the opening is preserved (at least <see cref="MinRemainder"/>). Returns the room with the wall replaced by the cut wall; a refused cut returns the input room unchanged with Ok false and a named Reason.</summary>
	public static RoomResult CutDoorway( RoomResult room, RoomWall wall, float width, float height, float offsetAlongWall = 0f, IMeshBoolean provider = null )
		=> CutOpening( room, wall, RoomOpeningKind.Doorway, width, height, 0f, offsetAlongWall, provider );

	/// <summary>Cut a window in the named wall: an opening of the given width and height with its bottom sillHeight above the floor, centred offsetAlongWall from the wall centre (0 = centred). The sill below and the lintel above are preserved (at least <see cref="MinRemainder"/> each). Returns the room with the wall replaced by the cut wall; a refused cut returns the input room unchanged with Ok false and a named Reason.</summary>
	public static RoomResult CutWindow( RoomResult room, RoomWall wall, float width, float height, float sillHeight, float offsetAlongWall = 0f, IMeshBoolean provider = null )
		=> CutOpening( room, wall, RoomOpeningKind.Window, width, height, sillHeight, offsetAlongWall, provider );

	/// <summary>Build the portal graph the leak finder flood-fills for this room: one interior leaf (the room void) + one void leaf, with one open portal per detected gap. A gap is a place the room's own geometry says must be solid but is not (an expected-solid probe point outside its brush), or the first boundary edge of a brush that is not a closed solid. Intentional doorway/window openings are NOT portals: they are named connections to adjoining space, not gaps to the void — the check is that the walls around them are still solid.</summary>
	public static LeakFinder.LeakGraph BuildLeakGraph( RoomResult room )
	{
		if ( room is null ) throw new ArgumentNullException( nameof( room ) );
		var graph = new LeakFinder.LeakGraph();
		var interiorCenter = ( room.InteriorMin + room.InteriorMax ) * 0.5f;
		graph.AddLeaf( new LeakFinder.LeakLeaf( 0, interiorCenter, room.InteriorMin, room.InteriorMax ) );
		var voidMin = room.InteriorMin - new Vec3( 1024f, 1024f, 1024f );
		var voidMax = room.InteriorMax + new Vec3( 1024f, 1024f, 1024f );
		graph.AddLeaf( new LeakFinder.LeakLeaf( 1, ( voidMin + voidMax ) * 0.5f, voidMin, voidMax, isVoid: true ) );
		var portalId = 0;
		var gapPoints = new List<Vec3>();
		void AddGap( Vec3 p )
		{
			foreach ( var q in gapPoints )
				if ( ( p - q ).LengthSquared < 0.25f ) return;
			gapPoints.Add( p );
			graph.AddPortal( new LeakFinder.LeakPortal( portalId++, 0, 1, p ) );
		}
		foreach ( var brush in room.Brushes )
		{
			if ( brush?.Mesh is null ) continue;
			foreach ( var probe in SolidProbePoints( room, brush ) )
				if ( !PointInSolid( probe, brush.Mesh ) ) AddGap( probe );
			if ( !MeshValidator.Validate( brush.Mesh ).IsClosed )
			{
				foreach ( var kv in brush.Mesh.BuildEdgeFaces() )
				{
					if ( kv.Value.Count == 2 ) continue;
					var a = brush.Mesh.Positions[kv.Key.A];
					var b = brush.Mesh.Positions[kv.Key.B];
					AddGap( ( a + b ) * 0.5f );
					break;
				}
			}
		}
		return graph;
	}

	/// <summary>Validate the room sealed: flood-fill the graph from <see cref="BuildLeakGraph"/> from the player start. Sealed means the void is unreachable.</summary>
	public static LeakFinder.LeakResult ValidateSealed( RoomResult room )
	{
		if ( room is null ) throw new ArgumentNullException( nameof( room ) );
		return LeakFinder.FindLeak( BuildLeakGraph( room ), room.PlayerStart );
	}

	/// <summary>3D centre of an opening, on its wall's mid-thickness plane.</summary>
	public static Vec3 OpeningCenter( RoomResult room, RoomOpening opening )
	{
		if ( room is null ) throw new ArgumentNullException( nameof( room ) );
		if ( opening is null ) throw new ArgumentNullException( nameof( opening ) );
		var t = room.WallThickness;
		var hx = room.Length * 0.5f;
		var hy = room.Width * 0.5f;
		var uc = opening.OffsetAlongWall;
		var vc = ( opening.V0 + opening.V1 ) * 0.5f;
		return opening.Wall switch
		{
			RoomWall.North => new Vec3( uc, hy + t * 0.5f, vc ),
			RoomWall.South => new Vec3( uc, -hy - t * 0.5f, vc ),
			RoomWall.East => new Vec3( hx + t * 0.5f, uc, vc ),
			RoomWall.West => new Vec3( -hx - t * 0.5f, uc, vc ),
			_ => throw new ArgumentException( $"Unknown wall {opening.Wall}.", nameof( opening ) ),
		};
	}

	/// <summary>True when the point is inside the closed solid. Ray-casting parity, majority vote over three fixed slanted rays: one ray can graze an edge or pass through a 1-unit reveal, two agreeing rays cannot. A point exactly on a face is undefined, as usual for parity tests.</summary>
	public static bool PointInSolid( Vec3 point, PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		var dirs = new[]
		{
			new Vec3( 0.374f, 0.928f, 0.113f ).Normal,
			new Vec3( 0.928f, 0.113f, 0.374f ).Normal,
			new Vec3( 0.113f, 0.374f, 0.928f ).Normal,
		};
		var insideVotes = 0;
		foreach ( var dir in dirs )
		{
			var hits = 0;
			foreach ( var face in mesh.Faces )
			{
				if ( face.Indices is null || face.Indices.Length < 3 ) continue;
				for ( var i = 1; i + 1 < face.Indices.Length; i++ )
					if ( RayHitsTriangle( point, dir, mesh.Positions[face.Indices[0]], mesh.Positions[face.Indices[i]], mesh.Positions[face.Indices[i + 1]], out var t ) && t > 1e-6 )
						hits++;
			}
			if ( ( hits & 1 ) == 1 ) insideVotes++;
		}
		return insideVotes >= 2;
	}

	static RoomResult CutOpening( RoomResult room, RoomWall wall, RoomOpeningKind kind,
		float width, float height, float sillHeight, float offsetAlongWall, IMeshBoolean provider )
	{
		if ( room is null ) throw new ArgumentNullException( nameof( room ) );
		if ( !room.Ok ) return room;
		if ( !Enum.IsDefined( typeof( RoomWall ), wall ) )
			return Fail( room, "room-unknown-wall", $"There is no wall {wall} (North/South/East/West)." );
		if ( !float.IsFinite( width ) || !( width > 0f ) || !float.IsFinite( height ) || !( height > 0f )
			|| !float.IsFinite( sillHeight ) || sillHeight < 0f || !float.IsFinite( offsetAlongWall ) )
			return Fail( room, "room-opening-bad-size",
				$"An opening needs a finite positive width and height and a finite offset/sill; got width {width:0.###}, height {height:0.###}, sill {sillHeight:0.###}, offset {offsetAlongWall:0.###}." );
		var brush = room.FindBrush( wall );
		if ( brush is null )
			return Fail( room, "room-wall-not-found", $"The room has no {wall} wall brush to cut." );
		var opening = new RoomOpening( kind, wall, width, height, sillHeight, offsetAlongWall );
		var halfLen = HalfLength( room, wall );
		if ( opening.U0 < -halfLen + MinRemainder || opening.U1 > halfLen - MinRemainder )
			return Fail( room, "room-opening-too-wide",
				$"A {width:0.###}-wide opening at offset {offsetAlongWall:0.###} does not fit the {wall} wall (half-length {halfLen:0.###}) with {MinRemainder:0.###} of jamb on each side." );
		if ( opening.V1 > room.Height - MinRemainder )
			return Fail( room, "room-opening-no-lintel",
				$"The opening tops out at {opening.V1:0.###} in a {room.Height:0.###}-high room, leaving less than {MinRemainder:0.###} of lintel." );
		if ( kind == RoomOpeningKind.Window && sillHeight < MinRemainder )
			return Fail( room, "room-window-no-sill",
				$"A window sill of {sillHeight:0.###} leaves no sill below the opening — that is a doorway; use CutDoorway." );
		foreach ( var other in room.Openings )
		{
			if ( other.Wall != wall ) continue;
			if ( opening.U0 < other.U1 + MinRemainder && other.U0 - MinRemainder < opening.U1
				&& opening.V0 < other.V1 + MinRemainder && other.V0 - MinRemainder < opening.V1 )
				return Fail( room, "room-opening-overlaps-existing",
					$"The opening overlaps (or comes within {MinRemainder:0.###} of) an existing {other.Kind} in the {wall} wall." );
		}
		var cutter = CutterMesh( room, opening );
		if ( cutter is null )
			return Fail( room, "room-cutter-failed", "The opening cutter solid could not be built (internal error)." );
		var oldVolume = MathF.Abs( brush.Mesh.SignedVolume() );
		var cut = MapCsg.TrySubtract( brush.Mesh.Clone(), cutter, provider );
		if ( !cut.Ok )
			return Fail( room, cut.Reason, cut.Message );
		var expectedVolume = oldVolume - width * height * room.WallThickness;
		var gotVolume = MathF.Abs( cut.Mesh.SignedVolume() );
		if ( MathF.Abs( gotVolume - expectedVolume ) > 1e-3f * MathF.Max( 1f, expectedVolume ) )
			return Fail( room, "room-cut-volume-mismatch",
				$"The cut wall volume is {gotVolume:0.###}, expected {expectedVolume:0.###} (wall minus opening)." );
		var materials = new List<string>( room.Materials );
		var faceMaterials = ReclassifyFaces( room, brush, cut.Mesh, materials );
		var brushes = new List<RoomBrush>( room.Brushes.Count );
		foreach ( var b in room.Brushes )
			brushes.Add( b == brush ? new RoomBrush( b.Name, b.Wall, cut.Mesh, faceMaterials ) : b );
		var openings = new List<RoomOpening>( room.Openings ) { opening };
		return Finish( room.Length, room.Width, room.Height, room.WallThickness,
			brushes, openings, materials, room.PlayerStart, true, null, null );
	}

	static RoomResult Finish( float length, float width, float height, float wallThickness,
		IReadOnlyList<RoomBrush> brushes, IReadOnlyList<RoomOpening> openings,
		IReadOnlyList<string> materials, Vec3 playerStart, bool ok, string reason, string message )
	{
		var provisional = new RoomResult( length, width, height, wallThickness, brushes, openings, materials, playerStart, null, ok, reason, message );
		var leak = ValidateSealed( provisional );
		if ( leak.IsLeaked && reason is null )
		{
			ok = false;
			reason = "room-leak";
			message = $"Sealed validation failed: the leak finder reached the void through a gap at {leak.GapLocation}.";
		}
		return new RoomResult( length, width, height, wallThickness, brushes, openings, materials, playerStart, leak, ok, reason, message );
	}

	static RoomResult Fail( RoomResult room, string reason, string message ) =>
		new RoomResult( room.Length, room.Width, room.Height, room.WallThickness,
			room.Brushes, room.Openings, room.Materials, room.PlayerStart, room.LeakCheck, false, reason, message );

	static float HalfLength( RoomResult room, RoomWall wall ) => wall switch
	{
		RoomWall.North or RoomWall.South => room.Length * 0.5f + room.WallThickness,
		RoomWall.East or RoomWall.West => room.Width * 0.5f,
		_ => throw new ArgumentException( $"Unknown wall {wall}.", nameof( wall ) ),
	};

	static PolyMesh CutterMesh( RoomResult room, RoomOpening opening )
	{
		var t = room.WallThickness;
		var hx = room.Length * 0.5f;
		var hy = room.Width * 0.5f;
		Vec3 min, max;
		switch ( opening.Wall )
		{
			case RoomWall.North:
				min = new Vec3( opening.U0, hy - CutterOverhang, opening.V0 );
				max = new Vec3( opening.U1, hy + t + CutterOverhang, opening.V1 );
				break;
			case RoomWall.South:
				min = new Vec3( opening.U0, -hy - t - CutterOverhang, opening.V0 );
				max = new Vec3( opening.U1, -hy + CutterOverhang, opening.V1 );
				break;
			case RoomWall.East:
				min = new Vec3( hx - CutterOverhang, opening.U0, opening.V0 );
				max = new Vec3( hx + t + CutterOverhang, opening.U1, opening.V1 );
				break;
			default: // West
				min = new Vec3( -hx - t - CutterOverhang, opening.U0, opening.V0 );
				max = new Vec3( -hx + CutterOverhang, opening.U1, opening.V1 );
				break;
		}
		var table = new List<string>();
		var reveal = RevealMaterial( room, opening.Wall );
		var built = BuildBox( "cutter", null, min, max, _ => reveal, table );
		return built?.Mesh;
	}

	/// <summary>The visible material of a wall (its inner face's material): new cut faces (jambs, lintel, sill reveals) are visible, so they get this.</summary>
	static string RevealMaterial( RoomResult room, RoomWall wall )
	{
		var brush = room.FindBrush( wall );
		if ( brush is null ) return DefaultDevMaterial;
		var inward = wall switch
		{
			RoomWall.North => new Vec3( 0, -1, 0 ),
			RoomWall.South => new Vec3( 0, 1, 0 ),
			RoomWall.East => new Vec3( -1, 0, 0 ),
			RoomWall.West => new Vec3( 1, 0, 0 ),
			_ => Vec3.Zero,
		};
		for ( var i = 0; i < brush.Mesh.Faces.Count; i++ )
		{
			var face = brush.Mesh.Faces[i];
			if ( face.Indices is null || face.Indices.Length < 3 ) continue;
			if ( Vec3.Dot( brush.Mesh.FaceNormal( face ).Normal, inward ) > 0.9999f )
				return brush.FaceMaterials[i];
		}
		return DefaultDevMaterial;
	}

	/// <summary>Re-material a cut wall geometrically: a result face on one of the original wall's planes keeps that face's material; any other face is a fresh cut reveal and gets the wall's visible material. Provider-independent — it does not trust how the boolean labelled its new faces.</summary>
	static IReadOnlyList<string> ReclassifyFaces( RoomResult room, RoomBrush original, PolyMesh result, List<string> table )
	{
		var originalPlanes = new List<(Vec3 Normal, float Distance, string Material)>();
		for ( var i = 0; i < original.Mesh.Faces.Count; i++ )
		{
			var face = original.Mesh.Faces[i];
			if ( face.Indices is null || face.Indices.Length < 3 ) continue;
			var n = original.Mesh.FaceNormal( face ).Normal;
			originalPlanes.Add( (n, Vec3.Dot( n, original.Mesh.Positions[face.Indices[0]] ), original.FaceMaterials[i]) );
		}
		var reveal = RevealMaterial( room, original.Wall!.Value );
		var names = new string[result.Faces.Count];
		for ( var i = 0; i < result.Faces.Count; i++ )
		{
			var face = result.Faces[i];
			string name = reveal;
			if ( face.Indices is not null && face.Indices.Length >= 3 )
			{
				var n = result.FaceNormal( face ).Normal;
				var d = Vec3.Dot( n, result.Positions[face.Indices[0]] );
				foreach ( var p in originalPlanes )
					if ( Vec3.Dot( p.Normal, n ) > 0.9999f && MathF.Abs( p.Distance - d ) < 1e-3f ) { name = p.Material; break; }
			}
			names[i] = name;
			var slot = table.IndexOf( name );
			if ( slot < 0 ) { table.Add( name ); slot = table.Count - 1; }
			face.Material = slot;
		}
		return names;
	}

	/// <summary>Probe points that must be inside their brush: the centre of every expected-solid rectangle of the brush, at mid-thickness. A wall's rectangles are its face minus its openings; floor/ceiling have one full rectangle.</summary>
	static IReadOnlyList<Vec3> SolidProbePoints( RoomResult room, RoomBrush brush )
	{
		var points = new List<Vec3>();
		var t = room.WallThickness;
		var hx = room.Length * 0.5f;
		var hy = room.Width * 0.5f;
		if ( brush.Wall is null )
		{
			var z = brush.Name == "Floor" ? -t * 0.5f : room.Height + t * 0.5f;
			points.Add( new Vec3( 0f, 0f, z ) );
			return points;
		}
		var rects = new List<Rect2> { new Rect2( -HalfLength( room, brush.Wall.Value ), HalfLength( room, brush.Wall.Value ), 0f, room.Height ) };
		foreach ( var opening in room.Openings )
		{
			if ( opening.Wall != brush.Wall ) continue;
			var next = new List<Rect2>();
			foreach ( var r in rects ) next.AddRange( SubtractRect( r, new Rect2( opening.U0, opening.U1, opening.V0, opening.V1 ) ) );
			rects = next;
		}
		foreach ( var r in rects )
		{
			var uc = ( r.U0 + r.U1 ) * 0.5f;
			var vc = ( r.V0 + r.V1 ) * 0.5f;
			points.Add( brush.Wall.Value switch
			{
				RoomWall.North => new Vec3( uc, hy + t * 0.5f, vc ),
				RoomWall.South => new Vec3( uc, -hy - t * 0.5f, vc ),
				RoomWall.East => new Vec3( hx + t * 0.5f, uc, vc ),
				RoomWall.West => new Vec3( -hx - t * 0.5f, uc, vc ),
				_ => Vec3.Zero,
			} );
		}
		return points;
	}

	sealed class Rect2
	{
		public Rect2( float u0, float u1, float v0, float v1 ) { U0 = u0; U1 = u1; V0 = v0; V1 = v1; }
		public float U0, U1, V0, V1;
	}

	static List<Rect2> SubtractRect( Rect2 r, Rect2 cut )
	{
		var result = new List<Rect2>();
		var ix0 = MathF.Max( r.U0, cut.U0 );
		var ix1 = MathF.Min( r.U1, cut.U1 );
		var iy0 = MathF.Max( r.V0, cut.V0 );
		var iy1 = MathF.Min( r.V1, cut.V1 );
		if ( ix0 >= ix1 || iy0 >= iy1 ) { result.Add( r ); return result; }
		if ( r.U0 < ix0 ) result.Add( new Rect2( r.U0, ix0, r.V0, r.V1 ) );
		if ( ix1 < r.U1 ) result.Add( new Rect2( ix1, r.U1, r.V0, r.V1 ) );
		if ( r.V0 < iy0 ) result.Add( new Rect2( ix0, ix1, r.V0, iy0 ) );
		if ( iy1 < r.V1 ) result.Add( new Rect2( ix0, ix1, iy1, r.V1 ) );
		return result;
	}

	/// <summary>One convex box from its six face planes (the MapBrush.FromPlanes equivalent is MapCsg.MeshFromPlanes). Returns null when the planes enclose no valid solid. Face material comes from the outward normal; slots are recorded in the room's material table.</summary>
	static RoomBrush BuildBox( string name, RoomWall? wall, Vec3 min, Vec3 max, Func<Vec3, string> materialForNormal, List<string> table )
	{
		var planes = new List<BrushPlane>
		{
			new BrushPlane( new Vec3( 1, 0, 0 ), max.x, Slot( table, materialForNormal( new Vec3( 1, 0, 0 ) ) ) ),
			new BrushPlane( new Vec3( -1, 0, 0 ), -min.x, Slot( table, materialForNormal( new Vec3( -1, 0, 0 ) ) ) ),
			new BrushPlane( new Vec3( 0, 1, 0 ), max.y, Slot( table, materialForNormal( new Vec3( 0, 1, 0 ) ) ) ),
			new BrushPlane( new Vec3( 0, -1, 0 ), -min.y, Slot( table, materialForNormal( new Vec3( 0, -1, 0 ) ) ) ),
			new BrushPlane( new Vec3( 0, 0, 1 ), max.z, Slot( table, materialForNormal( new Vec3( 0, 0, 1 ) ) ) ),
			new BrushPlane( new Vec3( 0, 0, -1 ), -min.z, Slot( table, materialForNormal( new Vec3( 0, 0, -1 ) ) ) ),
		};
		var mesh = MapCsg.MeshFromPlanes( planes );
		if ( mesh is null ) return null;
		var names = new string[mesh.Faces.Count];
		for ( var i = 0; i < mesh.Faces.Count; i++ ) names[i] = table[mesh.Faces[i].Material];
		return new RoomBrush( name, wall, mesh, names );
	}

	static int Slot( List<string> table, string name )
	{
		var slot = table.IndexOf( name );
		if ( slot < 0 ) { table.Add( name ); slot = table.Count - 1; }
		return slot;
	}

	static string VisibleMaterial( RoomOptions options, string partMaterial ) =>
		options.DevTextures ? options.DevMaterial : partMaterial;

	static void RequirePositive( float value, string name )
	{
		if ( !float.IsFinite( value ) || !( value > 0f ) )
			throw new ArgumentOutOfRangeException( name, value, "A room dimension must be a finite number greater than 0." );
	}

	static bool RayHitsTriangle( Vec3 origin, Vec3 dir, Vec3 a, Vec3 b, Vec3 c, out double t )
	{
		t = 0;
		var e1x = (double)b.x - a.x; var e1y = (double)b.y - a.y; var e1z = (double)b.z - a.z;
		var e2x = (double)c.x - a.x; var e2y = (double)c.y - a.y; var e2z = (double)c.z - a.z;
		var px = (double)dir.y * e2z - (double)dir.z * e2y;
		var py = (double)dir.z * e2x - (double)dir.x * e2z;
		var pz = (double)dir.x * e2y - (double)dir.y * e2x;
		var det = e1x * px + e1y * py + e1z * pz;
		if ( Math.Abs( det ) < 1e-12 ) return false;
		var inv = 1.0 / det;
		var tx = (double)origin.x - a.x; var ty = (double)origin.y - a.y; var tz = (double)origin.z - a.z;
		var u = ( tx * px + ty * py + tz * pz ) * inv;
		if ( u < -1e-9 || u > 1.0 + 1e-9 ) return false;
		var qx = ty * e1z - tz * e1y;
		var qy = tz * e1x - tx * e1z;
		var qz = tx * e1y - ty * e1x;
		var v = ( (double)dir.x * qx + (double)dir.y * qy + (double)dir.z * qz ) * inv;
		if ( v < -1e-9 || u + v > 1.0 + 1e-9 ) return false;
		t = ( e2x * qx + e2y * qy + e2z * qz ) * inv;
		return t > 1e-9;
	}
}
