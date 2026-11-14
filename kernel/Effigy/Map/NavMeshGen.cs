using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P3: native navmesh generation + preview data + export.
/// Board task hammer-navmesh-generation-export.
///
/// Source nav is a separate .nav generated in-game, NOT authored in Hammer — Geppetto
/// generates a navmesh from baked collision the way modern engines do, natively: no
/// Hammer app, no shelling out to Hammer/Hammer++/VBSP/VVIS/VRAD. Backend/data-model
/// only; no UI.
///
/// INPUT. Generation runs from the Quick Play bake (<see cref="QuickPlay"/>): the
/// bake's collision hulls are the solid geometry and its leaves are the interior/
/// void test, so this file never re-implements the BSP, the floor search or the
/// capsule test — it reuses them. <see cref="Generate(QuickPlayMap, NavMeshGenOptions)"/>
/// bakes first (so a bad map fails with QuickPlay's named errors); the
/// <see cref="Generate(QuickPlayBakeResult, QuickPlayMap, NavMeshGenOptions)"/> overload
/// takes an already-baked result and must be given the bake of the same map.
///
/// METHOD (Recast-style, simplified, on an axis-aligned heightfield).
/// 1. Voxelise: a grid of <see cref="NavMeshGenOptions.CellSize"/> cells over the
///    union of the collision hulls. Per cell the ground is the highest exposed solid
///    top at the cell centre (a top counts only when the point half a unit above it
///    is not inside another solid — QuickPlay's exposed-top rule), or a displacement
///    heightfield sample, whichever is higher.
/// 2. Walkable: the ground slope is within MaxSlopeDegrees (brush tops are flat;
///    displacement slope comes from finite differences of the heightfield) AND the
///    agent capsule (radius AgentRadius, height AgentHeight) above the ground is
///    clear of solid. A solid whose top is within StepHeight of the ground is
///    steppable and does not block the capsule — that is what makes stairs climbable
///    instead of eroding every tread edge. This capsule test IS the agent-radius
///    erosion: a cell within AgentRadius of a wall is unwalkable.
/// 3. Regions: flood-fill over walkable cells where the neighbour height differs by
///    at most StepHeight (symmetric, so region identity never depends on flood
///    direction). A drop bigger than StepHeight splits the region; a drop within
///    MaxDrop is still linked, one-way downhill, as a Drop link.
/// 4. Polys: greedy rectangle merge of same-region, same-height cells. A flat floor
///    is one poly; each stair tread is its own poly; a ramp is a chain of strip
///    polys (one height per cell row).
/// 5. Links: grid-adjacent polys share an edge — Walk (same height), StepUp/
///    StepDown (within StepHeight) — plus Drop links, which also bridge the erosion
///    gap at a solid ledge edge (see <see cref="NavMeshGen"/>).
/// 6. Hints: map entities that need a navmesh (PlaytestLoop's rule — class
///    "info_node" or any "npc_*" class) plus explicit
///    <see cref="NavMeshGenOptions.HintPoints"/> must land on a poly within
///    StepHeight of its height; uncovered hints are reported, never silently
///    dropped. The gameplay ticket owns the NPC/node types; this ticket only reads
///    the QuickPlayEntity class + origin, so it works before that ticket lands.
///
/// CONVEXHULL IS NOT USED. Worker 21 (hammer-quick-play-bsp-only-bake) reported
/// on 2026-11-13 that ConvexHull.Build returns 6 points / 8 faces for a cube's 8
/// corners (two coplanar-cap corners dropped, so that hull does not contain the
/// box). RE-PROBED in this ticket against the current tree: Build returns all 8
/// corners — the finding does NOT reproduce (the hull code or the probe changed
/// since; see the convex-hull test, which asserts the observed behaviour). Either
/// way this file never calls ConvexHull.Build: solid bounds come from the exact 8
/// corners QuickPlay already emits per brush hull (CollisionShape.Points), and
/// displacement hulls (ConvexDecomp approximations, BodyId "displacement") are
/// skipped as solids — the displacement heightfield is the authoritative walkable
/// surface there, exactly as in QuickPlay's floor search. A brush hull is
/// recognised by matching its BodyId against a map brush id, never by the literal
/// name "displacement".
///
/// ROOF RULE. A ground candidate counts only when the point just above it sits in a
/// non-solid BSP leaf that is not the void above the world: on a sealed map that is
/// a non-void leaf; on an unsealed map a void leaf still counts, UNLESS the
/// candidate is the world's top surface (at the max solid top) — that is the
/// ceiling top, not a floor. Without that, the top of the ceiling brush would
/// voxelise as a walkable roof, and a map with no floor at all would voxelise its
/// roof. Candidates are tried highest-first, so a rejected ceiling top falls
/// through to the floor below it instead of voiding the cell. Known limits: a
/// genuine roof terrace AT the world's top surface is rejected with the ceiling
/// (it is indistinguishable from it by leaves alone), and there is one ground
/// height per cell (the highest interior top), so the floor under a bridge or
/// overhang is not represented — multi-span support is future work, not this
/// ticket.
///
/// EXPORT. <see cref="NavMeshTextExport"/> writes a TEXT form of the Valve .nav
/// area/connection layout (areas + connections + hints). It is NOT byte-compatible
/// with Valve's binary .nav — no binary writer is attempted here — and the file
/// says so in its header. Parse() round-trips it, which is what the tests verify.
///
/// SCALING. The per-cell work scans the solid list and the leaf list, so generation
/// is O(cells x (solids + leaves)); fixture maps are trivial, a huge map wants a
/// spatial index (future work, not this ticket). MaxCells caps the grid instead of
/// allocating an unbounded one.
///
/// Units are the kernel's inches, Z-up. Deterministic: same map + options always
/// produce the same polys, links and export text.
/// </summary>
public static class NavMeshError
{
	/// <summary>No walkable cell survived voxelisation (e.g. no floor, or no headroom anywhere).</summary>
	public const string NoWalkableSurface = "no-walkable-surface";

	/// <summary>The voxel grid would exceed NavMeshGenOptions.MaxCells.</summary>
	public const string TooComplex = "too-complex";
}

/// <summary>A navmesh generation failure with a stable machine-readable code.</summary>
public sealed class NavMeshException : Exception
{
	public string Code { get; }

	public NavMeshException( string code, string message ) : base( message )
	{
		Code = code;
	}
}

/// <summary>Generation knobs. Defaults are Source-scale and match QuickPlay's:
/// a 72-unit tall, 16-unit radius agent, an 18-unit step, a 72-unit max drop.</summary>
public sealed class NavMeshGenOptions
{
	/// <summary>Voxel cell size, in inches. Smaller = finer navmesh, more cells.
/// Must be positive; 8 is two cells per agent radius.</summary>
	public float CellSize = 8f;

	/// <summary>Agent collision radius, in inches. Also the erosion distance.</summary>
	public float AgentRadius = 16f;

	/// <summary>Agent height, in inches. The headroom a walkable cell needs.</summary>
	public float AgentHeight = 72f;

	/// <summary>Highest step the agent climbs, in inches. Also the region-link limit
	/// and the hint height tolerance.</summary>
	public float StepHeight = 18f;

	/// <summary>Furthest the agent drops (one-way Drop link), in inches.</summary>
	public float MaxDrop = 72f;

	/// <summary>Steepest walkable slope, in degrees. Brush tops are flat; this bites
	/// on displacement ramps.</summary>
	public float MaxSlopeDegrees = 45f;

	/// <summary>Voxel grid cap; above it Generate fails with "too-complex" rather
	/// than allocating an unbounded grid.</summary>
	public int MaxCells = 1000000;

	/// <summary>Map name written into the export header. "" exports as "unnamed".</summary>
	public string MapName = "";

	/// <summary>Extra hint points (NPC nodes / hints the caller knows about) that must
	/// land on the navmesh, in addition to the map's info_node / npc_* entities.</summary>
	public List<Vec3> HintPoints = new List<Vec3>();
}

/// <summary>How two polys are linked.</summary>
public enum NavMeshLinkKind
{
	/// <summary>Same height (within 0.01).</summary>
	Walk,

	/// <summary>Uphill within StepHeight (directed low -&gt; high).</summary>
	StepUp,

	/// <summary>Downhill within StepHeight (directed high -&gt; low).</summary>
	StepDown,

	/// <summary>Downhill past StepHeight but within MaxDrop (directed high -&gt; low,
	/// one-way: the only cross-region link kind).</summary>
	Drop,
}

/// <summary>One navmesh polygon: a maximal rectangle of same-region, same-height
/// walkable cells. Flat (one Height for all four corners).</summary>
public sealed class NavMeshPoly
{
	public int Id;
	public int RegionId;

	/// <summary>Axis-aligned bounds of the rectangle (z is the ground height).</summary>
	public Vec3 Min;

	public Vec3 Max;

	/// <summary>Ground height (== Min.z == Max.z).</summary>
	public float Height;

	public Vec3 Centre => new Vec3( ( Min.x + Max.x ) * 0.5f, ( Min.y + Max.y ) * 0.5f, Height );

	/// <summary>Four corners at Height, counter-clockwise seen from above.</summary>
	public Vec3[] Corners => new[]
	{
		new Vec3( Min.x, Min.y, Height ), new Vec3( Max.x, Min.y, Height ),
		new Vec3( Max.x, Max.y, Height ), new Vec3( Min.x, Max.y, Height ),
	};

	public float Area => ( Max.x - Min.x ) * ( Max.y - Min.y );

	public bool ContainsXY( float x, float y, float eps = 1e-3f ) =>
		x >= Min.x - eps && x <= Max.x + eps && y >= Min.y - eps && y <= Max.y + eps;
}

/// <summary>One directed link between two polys.</summary>
public sealed class NavMeshLink
{
	public int From;
	public int To;
	public NavMeshLinkKind Kind;

	/// <summary>Midpoint of the shared edge (or of the drop gap), z = mean poly height.</summary>
	public Vec3 Midpoint;

	/// <summary>To.Height - From.Height, in inches.</summary>
	public float HeightDelta;

	/// <summary>Shared edge length (or bridged gap length for a Drop), in inches.</summary>
	public float SharedLength;
}

/// <summary>One NPC node / hint and whether the navmesh covers it.</summary>
public sealed class NavMeshHint
{
	public Vec3 Point;

	/// <summary>Entity class ("info_node", "npc_*") or "" for an explicit HintPoint.</summary>
	public string ClassName;

	/// <summary>Covering poly id, or -1 when uncovered.</summary>
	public int PolyId = -1;

	public bool Covered => PolyId >= 0;
}

/// <summary>Everything a navmesh generation produced: preview data (polys + links) as
/// API output, plus the spawn/sealed status carried over from the Quick Play bake.</summary>
public sealed class NavMeshResult
{
	public IReadOnlyList<NavMeshPoly> Polys;
	public IReadOnlyList<NavMeshLink> Links;
	public IReadOnlyList<NavMeshHint> Hints;
	public int RegionCount;
	public int CellCount;
	public int WalkableCellCount;
	public Vec3 BoundsMin;
	public Vec3 BoundsMax;
	public float CellSize;

	/// <summary>Generation parameters, echoed for the export header.</summary>
	public string MapName = "";
	public float AgentRadius = 16f;
	public float AgentHeight = 72f;
	public float StepHeight = 18f;
	public float MaxDrop = 72f;
	public float MaxSlopeDegrees = 45f;

	/// <summary>True when the Quick Play bake's spawn validation passed.</summary>
	public bool SpawnOk;

	/// <summary>Null when the spawn is ok; else the Quick Play spawn reason
	/// ("spawn-blocked", "spawn-no-floor", ...).</summary>
	public string SpawnReason;

	/// <summary>True when the Quick Play bake reports the map sealed.</summary>
	public bool Sealed;

	public List<string> Warnings = new List<string>();

	/// <summary>Poly id containing the point (XY inside the poly, |z - height| within
	/// StepHeight), or -1.</summary>
	public int PolyAt( Vec3 point, float stepHeight )
	{
		foreach ( var poly in Polys )
			if ( poly.ContainsXY( point.x, point.y ) && MathF.Abs( poly.Height - point.z ) <= stepHeight + 1e-3f )
				return poly.Id;
		return -1;
	}

	/// <summary>Deterministic one-line-per-poly/link summary, for tests and logs.</summary>
	public string Describe()
	{
		var lines = new List<string>();
		lines.Add( $"navmesh regions {RegionCount} polys {Polys.Count} links {Links.Count} cells {WalkableCellCount}/{CellCount} hints {Hints.Count( h => h.Covered )}/{Hints.Count} sealed {Sealed} spawn-ok {SpawnOk}" );
		foreach ( var poly in Polys )
			lines.Add( $"poly {poly.Id} region {poly.RegionId} h {poly.Height:0.###} min {poly.Min.x:0.###},{poly.Min.y:0.###} max {poly.Max.x:0.###},{poly.Max.y:0.###} area {poly.Area:0.###}" );
		foreach ( var link in Links )
			lines.Add( $"link {link.From}->{link.To} {link.Kind} dh {link.HeightDelta:0.###} len {link.SharedLength:0.###} mid {link.Midpoint.x:0.###},{link.Midpoint.y:0.###},{link.Midpoint.z:0.###}" );
		foreach ( var hint in Hints )
			lines.Add( $"hint {hint.Point.x:0.###},{hint.Point.y:0.###},{hint.Point.z:0.###} {( string.IsNullOrEmpty( hint.ClassName ) ? "point" : hint.ClassName )} poly {hint.PolyId} covered {hint.Covered}" );
		return string.Join( "\n", lines );
	}

	/// <summary>Text .nav export of this result (see <see cref="NavMeshTextExport"/>).</summary>
	public string ToNavText() => NavMeshTextExport.Export( this );
}

/// <summary>Text .nav export: a TEXT form of the Valve .nav area/connection layout.
/// NOT byte-compatible with Valve's binary .nav — the header says so — and no
/// binary writer is attempted here. <see cref="Parse"/> round-trips it.</summary>
public static class NavMeshTextExport
{
	public const int FormatVersion = 1;

	public sealed class AreaData
	{
		public int Id;
		public int RegionId;
		public Vec3 Min;
		public Vec3 Max;
	}

	public sealed class LinkData
	{
		public int From;
		public int To;
		public string Kind;
		public float HeightDelta;
		public float SharedLength;
		public Vec3 Midpoint;
	}

	public sealed class HintData
	{
		public Vec3 Point;
		public string ClassName;
		public int PolyId;
		public bool Covered;
	}

	public sealed class Parsed
	{
		public string MapName;
		public float AgentRadius;
		public float AgentHeight;
		public float StepHeight;
		public float MaxDrop;
		public float MaxSlopeDegrees;
		public Vec3 BoundsMin;
		public Vec3 BoundsMax;
		public List<AreaData> Areas = new List<AreaData>();
		public List<LinkData> Links = new List<LinkData>();
		public List<HintData> Hints = new List<HintData>();
	}

	static string F( float v ) => v.ToString( "R", CultureInfo.InvariantCulture );

	public static string Export( NavMeshResult result )
	{
		if ( result is null ) throw new ArgumentNullException( nameof( result ) );
		var sb = new StringBuilder();
		sb.AppendLine( "# Geppetto navmesh export — text form of the Valve .nav area/connection layout." );
		sb.AppendLine( "# NOT byte-compatible with Valve binary .nav; import via NavMeshTextExport.Parse." );
		sb.AppendLine( "NAV " + FormatVersion );
		sb.AppendLine( "map " + ( string.IsNullOrWhiteSpace( result.MapName ) ? "unnamed" : result.MapName ) );
		sb.AppendLine( $"agent radius {F( result.AgentRadius )} height {F( result.AgentHeight )} step {F( result.StepHeight )} drop {F( result.MaxDrop )} slope {F( result.MaxSlopeDegrees )}" );
		sb.AppendLine( $"bounds min {F( result.BoundsMin.x )} {F( result.BoundsMin.y )} {F( result.BoundsMin.z )} max {F( result.BoundsMax.x )} {F( result.BoundsMax.y )} {F( result.BoundsMax.z )}" );
		sb.AppendLine( "areas " + result.Polys.Count );
		foreach ( var poly in result.Polys )
		{
			var corners = poly.Corners;
			sb.Append( $"area {poly.Id} region {poly.RegionId} min {F( poly.Min.x )} {F( poly.Min.y )} {F( poly.Height )} max {F( poly.Max.x )} {F( poly.Max.y )} {F( poly.Height )} corners" );
			foreach ( var c in corners )
				sb.Append( $" ({F( c.x )},{F( c.y )},{F( c.z )})" );
			sb.AppendLine();
		}
		sb.AppendLine( "connections " + result.Links.Count );
		foreach ( var link in result.Links )
			sb.AppendLine( $"link {link.From} {link.To} {link.Kind} {F( link.HeightDelta )} {F( link.SharedLength )} {F( link.Midpoint.x )} {F( link.Midpoint.y )} {F( link.Midpoint.z )}" );
		sb.AppendLine( "hints " + result.Hints.Count );
		foreach ( var hint in result.Hints )
			sb.AppendLine( $"hint {F( hint.Point.x )} {F( hint.Point.y )} {F( hint.Point.z )} {( string.IsNullOrEmpty( hint.ClassName ) ? "-" : hint.ClassName )} area {hint.PolyId} covered {( hint.Covered ? 1 : 0 )}" );
		return sb.ToString();
	}

	// Export parameters ride on the result (NavMeshResult.MapName / AgentRadius /
	// ...), set by NavMeshGen; a hand-built result exports with the defaults above,
	// which Parse reports honestly. No static mutable state: two generations in one
	// process must not be able to swap each other's export headers.

	/// <summary>Parse text previously written by <see cref="Export"/>. Throws
	/// <see cref="FormatException"/> on a bad header, a wrong version, a truncated
	/// section or a malformed line — never a partial result.</summary>
	public static Parsed Parse( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			throw new FormatException( "navmesh text is empty" );
		var lines = new List<string>();
		foreach ( var raw in text.Split( '\n' ) )
		{
			var line = raw.Trim();
			if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
			lines.Add( line );
		}
		int i = 0;
		string Next()
		{
			if ( i >= lines.Count ) throw new FormatException( "navmesh text is truncated" );
			return lines[i++];
		}
		string[] Tok( string line ) => line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		float Num( string t )
		{
			if ( !float.TryParse( t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) )
				throw new FormatException( "not a number: '" + t + "'" );
			return v;
		}
		int Int( string t )
		{
			if ( !int.TryParse( t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v ) )
				throw new FormatException( "not an integer: '" + t + "'" );
			return v;
		}

		var header = Tok( Next() );
		if ( header.Length != 2 || header[0] != "NAV" || Int( header[1] ) != FormatVersion )
			throw new FormatException( "not a Geppetto navmesh text export (expected 'NAV " + FormatVersion + "')" );
		var parsed = new Parsed();
		var mapTok = Tok( Next() );
		if ( mapTok.Length != 2 || mapTok[0] != "map" ) throw new FormatException( "expected 'map <name>'" );
		parsed.MapName = mapTok[1];
		var agentTok = Tok( Next() );
		if ( agentTok.Length != 11 || agentTok[0] != "agent" ) throw new FormatException( "expected 'agent radius .. height .. step .. drop .. slope ..'" );
		parsed.AgentRadius = Num( agentTok[2] );
		parsed.AgentHeight = Num( agentTok[4] );
		parsed.StepHeight = Num( agentTok[6] );
		parsed.MaxDrop = Num( agentTok[8] );
		parsed.MaxSlopeDegrees = Num( agentTok[10] );
		var boundsTok = Tok( Next() );
		if ( boundsTok.Length != 9 || boundsTok[0] != "bounds" || boundsTok[1] != "min" || boundsTok[5] != "max" )
			throw new FormatException( "expected 'bounds min .. max ..'" );
		parsed.BoundsMin = new Vec3( Num( boundsTok[2] ), Num( boundsTok[3] ), Num( boundsTok[4] ) );
		parsed.BoundsMax = new Vec3( Num( boundsTok[6] ), Num( boundsTok[7] ), Num( boundsTok[8] ) );
		var areasTok = Tok( Next() );
		if ( areasTok.Length != 2 || areasTok[0] != "areas" ) throw new FormatException( "expected 'areas <n>'" );
		int areaCount = Int( areasTok[1] );
		for ( int a = 0; a < areaCount; a++ )
		{
			var t = Tok( Next() );
			if ( t.Length != 17 || t[0] != "area" || t[2] != "region" || t[4] != "min" || t[8] != "max" || t[12] != "corners" )
				throw new FormatException( "malformed area line: '" + string.Join( " ", t ) + "'" );
			parsed.Areas.Add( new AreaData
			{
				Id = Int( t[1] ),
				RegionId = Int( t[3] ),
				Min = new Vec3( Num( t[5] ), Num( t[6] ), Num( t[7] ) ),
				Max = new Vec3( Num( t[9] ), Num( t[10] ), Num( t[11] ) ),
			} );
			// Corners (t[13..16], "(x,y,z)") are validated, not kept: the area is the
			// rectangle Min/Max; a corner that disagrees with the rectangle is corrupt.
			for ( int c = 0; c < 4; c++ )
			{
				var corner = ParseCorner( t[13 + c] );
				var expected = c == 0 ? new Vec3( Num( t[5] ), Num( t[6] ), Num( t[7] ) )
					: c == 1 ? new Vec3( Num( t[9] ), Num( t[6] ), Num( t[7] ) )
					: c == 2 ? new Vec3( Num( t[9] ), Num( t[10] ), Num( t[7] ) )
					: new Vec3( Num( t[5] ), Num( t[10] ), Num( t[7] ) );
				if ( !corner.AlmostEquals( expected, 1e-3f ) )
					throw new FormatException( "area corner disagrees with its rectangle" );
			}
		}
		var connTok = Tok( Next() );
		if ( connTok.Length != 2 || connTok[0] != "connections" ) throw new FormatException( "expected 'connections <n>'" );
		int linkCount = Int( connTok[1] );
		for ( int l = 0; l < linkCount; l++ )
		{
			var t = Tok( Next() );
			if ( t.Length != 9 || t[0] != "link" ) throw new FormatException( "malformed link line" );
			if ( t[3] != "Walk" && t[3] != "StepUp" && t[3] != "StepDown" && t[3] != "Drop" )
				throw new FormatException( "unknown link kind: '" + t[3] + "'" );
			parsed.Links.Add( new LinkData
			{
				From = Int( t[1] ), To = Int( t[2] ), Kind = t[3],
				HeightDelta = Num( t[4] ), SharedLength = Num( t[5] ),
				Midpoint = new Vec3( Num( t[6] ), Num( t[7] ), Num( t[8] ) ),
			} );
		}
		var hintsTok = Tok( Next() );
		if ( hintsTok.Length != 2 || hintsTok[0] != "hints" ) throw new FormatException( "expected 'hints <n>'" );
		int hintCount = Int( hintsTok[1] );
		for ( int h = 0; h < hintCount; h++ )
		{
			var t = Tok( Next() );
			if ( t.Length != 9 || t[0] != "hint" || t[5] != "area" || t[7] != "covered" || t[8] != "0" && t[8] != "1" )
				throw new FormatException( "malformed hint line" );
			parsed.Hints.Add( new HintData
			{
				Point = new Vec3( Num( t[1] ), Num( t[2] ), Num( t[3] ) ),
				ClassName = t[4] == "-" ? "" : t[4],
				PolyId = Int( t[6] ),
				Covered = t[8] == "1",
			} );
		}
		if ( i != lines.Count ) throw new FormatException( "trailing content after the hints section" );
		return parsed;
	}

	static Vec3 ParseCorner( string token )
	{
		if ( token.Length < 7 || token[0] != '(' || token[token.Length - 1] != ')' )
			throw new FormatException( "malformed corner: '" + token + "'" );
		var parts = token.Substring( 1, token.Length - 2 ).Split( ',' );
		if ( parts.Length != 3 )
			throw new FormatException( "malformed corner: '" + token + "'" );
		if ( !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x ) ||
			!float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y ) ||
			!float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
			throw new FormatException( "malformed corner: '" + token + "'" );
		return new Vec3( x, y, z );
	}
}

/// <summary>
/// Native navmesh generation from a Quick Play bake. See the file header for the
/// method, the ConvexHull rule, the roof rule and the export format.
/// </summary>
public static class NavMeshGen
{
	const float Eps = 1e-3f;

	/// <summary>Bake the map with QuickPlay, then generate the navmesh. A bad map fails
	/// with QuickPlay's named errors (no-player-start, empty-map, invalid-brush,
	/// invalid-displacement, too-complex); a map with no walkable surface fails with
	/// NavMeshException "no-walkable-surface".</summary>
	public static NavMeshResult Generate( QuickPlayMap map, NavMeshGenOptions options = null )
	{
		if ( map is null ) throw new ArgumentNullException( nameof( map ) );
		options ??= new NavMeshGenOptions();
		var bake = QuickPlay.Bake( map );
		return Generate( bake, map, options );
	}

	/// <summary>Generate from an already-baked result. <paramref name="bake"/> must be
	/// the bake of <paramref name="map"/>: the hulls come from the bake, the
	/// displacements and hint entities from the map.</summary>
	public static NavMeshResult Generate( QuickPlayBakeResult bake, QuickPlayMap map, NavMeshGenOptions options = null )
	{
		if ( bake is null ) throw new ArgumentNullException( nameof( bake ) );
		if ( map is null ) throw new ArgumentNullException( nameof( map ) );
		options ??= new NavMeshGenOptions();
		ValidateOptions( options );

		// ---- Solids: exact AABBs from the bake's brush hulls. A hull is a brush
		// hull when its BodyId matches a map brush id; anything else is a
		// displacement approximation (ConvexDecomp) and is skipped — the displacement
		// heightfield is authoritative there. The AABB comes from the hull's exact
		// corners; ConvexHull.Build is never called (it drops cube corners). ----
		var brushIds = new HashSet<string>();
		foreach ( var b in map.Brushes )
			if ( b != null && b.Id != null ) brushIds.Add( b.Id );
		var solids = new List<Solid>();
		foreach ( var hull in bake.CollisionHulls )
		{
			if ( hull is null || hull.Kind != CollisionKind.Hull || hull.Points is null || hull.Points.Count == 0 ) continue;
			if ( !brushIds.Contains( hull.BodyId ) ) continue;
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in hull.Points )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
			solids.Add( new Solid { Min = min, Max = max, BodyId = hull.BodyId } );
		}

		// ---- Grid bounds: the solid union plus the displacement patches. ----
		var bMin = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var bMax = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var s in solids )
		{
			bMin = new Vec3( MathF.Min( bMin.x, s.Min.x ), MathF.Min( bMin.y, s.Min.y ), MathF.Min( bMin.z, s.Min.z ) );
			bMax = new Vec3( MathF.Max( bMax.x, s.Max.x ), MathF.Max( bMax.y, s.Max.y ), MathF.Max( bMax.z, s.Max.z ) );
		}
		foreach ( var d in map.Displacements )
		{
			if ( d is null ) continue;
			bMin = new Vec3( MathF.Min( bMin.x, d.Origin.x ), MathF.Min( bMin.y, d.Origin.y ), MathF.Min( bMin.z, d.MinHeight() ) );
			bMax = new Vec3( MathF.Max( bMax.x, d.Origin.x + d.SizeX ), MathF.Max( bMax.y, d.Origin.y + d.SizeY ), bMax.z );
		}
		if ( solids.Count == 0 && map.Displacements.Count == 0 )
			throw new NavMeshException( NavMeshError.NoWalkableSurface,
				"no-walkable-surface: the bake has no solid hulls and no displacements to stand on" );

		float cell = options.CellSize;
		int nx = (int)MathF.Ceiling( ( bMax.x - bMin.x ) / cell );
		int ny = (int)MathF.Ceiling( ( bMax.y - bMin.y ) / cell );
		if ( nx <= 0 || ny <= 0 )
			throw new NavMeshException( NavMeshError.NoWalkableSurface,
				"no-walkable-surface: the map has no XY extent to voxelise" );
		if ( (long)nx * ny > options.MaxCells )
			throw new NavMeshException( NavMeshError.TooComplex,
				$"too-complex: the voxel grid would be {nx}x{ny} = {(long)nx * ny} cells, over the {options.MaxCells} cap" );

		var leaves = bake.Leaves;
		var cells = new Cell[nx, ny];
		int walkable = 0;
		for ( int cy = 0; cy < ny; cy++ )
		{
			for ( int cx = 0; cx < nx; cx++ )
			{
				float x = bMin.x + ( cx + 0.5f ) * cell;
				float y = bMin.y + ( cy + 0.5f ) * cell;
				var c = new Cell { X = cx, Y = cy, RegionId = -1, PolyId = -1 };
				if ( GroundAt( map, solids, leaves, bMax.z, cell, x, y, out var height, out var slopeDeg ) )
				{
					c.Height = height;
					if ( slopeDeg > options.MaxSlopeDegrees + 1e-3f )
					{
						c.Reason = CellReason.Slope;
					}
					else if ( CapsuleBlocked( solids, x, y, height, options ) )
					{
						c.Reason = CellReason.Headroom;
					}
					else
					{
						c.Walkable = true;
						walkable++;
					}
				}
				else
				{
					c.Reason = CellReason.NoGround;
					c.Height = float.NaN;
				}
				cells[cx, cy] = c;
			}
		}
		if ( walkable == 0 )
			throw new NavMeshException( NavMeshError.NoWalkableSurface,
				"no-walkable-surface: no voxel survived the slope / headroom / erosion checks" );

		// ---- Regions: flood-fill over walkable cells, |dh| <= StepHeight. ----
		int regions = 0;
		var queue = new Queue<(int, int)>();
		for ( int cy = 0; cy < ny; cy++ )
		{
			for ( int cx = 0; cx < nx; cx++ )
			{
				if ( !cells[cx, cy].Walkable || cells[cx, cy].RegionId >= 0 ) continue;
				int region = regions++;
				cells[cx, cy].RegionId = region;
				queue.Enqueue( (cx, cy) );
				while ( queue.Count > 0 )
				{
					var (x0, y0) = queue.Dequeue();
					foreach ( var (dx, dy) in Neighbours )
					{
						int x1 = x0 + dx, y1 = y0 + dy;
						if ( x1 < 0 || x1 >= nx || y1 < 0 || y1 >= ny ) continue;
						var n = cells[x1, y1];
						if ( !n.Walkable || n.RegionId >= 0 ) continue;
						if ( MathF.Abs( n.Height - cells[x0, y0].Height ) > options.StepHeight + Eps ) continue;
						cells[x1, y1].RegionId = region;
						queue.Enqueue( (x1, y1) );
					}
				}
			}
		}

		// ---- Polys: greedy rectangle merge of same-region, same-height cells. ----
		var polys = new List<NavMeshPoly>();
		var claimed = new bool[nx, ny];
		for ( int cy = 0; cy < ny; cy++ )
		{
			for ( int cx = 0; cx < nx; cx++ )
			{
				if ( !cells[cx, cy].Walkable || claimed[cx, cy] ) continue;
				int region = cells[cx, cy].RegionId;
				float height = cells[cx, cy].Height;
				int w = 1;
				while ( cx + w < nx && SameCell( cells, claimed, cx + w, cy, region, height ) ) w++;
				int h = 1;
				bool rowOk = true;
				while ( cy + h < ny && rowOk )
				{
					for ( int k = 0; k < w; k++ )
						if ( !SameCell( cells, claimed, cx + k, cy + h, region, height ) ) { rowOk = false; break; }
					if ( rowOk ) h++;
				}
				for ( int yy = cy; yy < cy + h; yy++ )
					for ( int xx = cx; xx < cx + w; xx++ )
					{
						claimed[xx, yy] = true;
						cells[xx, yy].PolyId = polys.Count;
					}
				polys.Add( new NavMeshPoly
				{
					Id = polys.Count,
					RegionId = region,
					Min = new Vec3( bMin.x + cx * cell, bMin.y + cy * cell, height ),
					Max = new Vec3( bMin.x + ( cx + w ) * cell, bMin.y + ( cy + h ) * cell, height ),
					Height = height,
				} );
			}
		}

		// ---- Links: shared grid edges between polys, plus Drop links that bridge
		// the erosion gap at a solid ledge (see DropGap below). ----
		var linkTable = new Dictionary<(int, int), LinkAccum>();
		for ( int cy = 0; cy < ny; cy++ )
		{
			for ( int cx = 0; cx < nx; cx++ )
			{
				if ( !cells[cx, cy].Walkable ) continue;
				if ( cx + 1 < nx && cells[cx + 1, cy].Walkable )
					AccumulateEdge( cells, linkTable, cx, cy, cx + 1, cy, cell, bMin.x, bMin.y );
				if ( cy + 1 < ny && cells[cx, cy + 1].Walkable )
					AccumulateEdge( cells, linkTable, cx, cy, cx, cy + 1, cell, bMin.x, bMin.y );
			}
		}
		AddDropGapLinks( cells, nx, ny, linkTable, options, bMin.x, bMin.y );
		var links = BuildLinks( polys, linkTable, options );

		// ---- Hints: map entities that need a navmesh (PlaytestLoop's rule) plus
		// the caller's explicit hint points. ----
		var hints = new List<NavMeshHint>();
		foreach ( var e in map.Entities )
		{
			if ( e is null || string.IsNullOrEmpty( e.ClassName ) ) continue;
			if ( string.Equals( e.ClassName, "info_node", StringComparison.OrdinalIgnoreCase ) ||
				e.ClassName.StartsWith( "npc_", StringComparison.OrdinalIgnoreCase ) )
				hints.Add( new NavMeshHint { Point = e.Origin, ClassName = e.ClassName } );
		}
		foreach ( var p in options.HintPoints )
			hints.Add( new NavMeshHint { Point = p, ClassName = "" } );
		var result = new NavMeshResult
		{
			Polys = polys,
			Links = links,
			RegionCount = regions,
			CellCount = nx * ny,
			WalkableCellCount = walkable,
			BoundsMin = bMin,
			BoundsMax = bMax,
			CellSize = cell,
			SpawnOk = bake.Spawn != null && bake.Spawn.Ok,
			SpawnReason = bake.Spawn != null && !bake.Spawn.Ok ? bake.Spawn.Reason : null,
			Sealed = bake.Sealed,
		};
		foreach ( var hint in hints )
			hint.PolyId = result.PolyAt( hint.Point, options.StepHeight );
		result.Hints = hints;
		result.MapName = options.MapName ?? "";
		result.AgentRadius = options.AgentRadius;
		result.AgentHeight = options.AgentHeight;
		result.StepHeight = options.StepHeight;
		result.MaxDrop = options.MaxDrop;
		result.MaxSlopeDegrees = options.MaxSlopeDegrees;
		result.Warnings.AddRange( bake.Warnings );
		if ( !result.SpawnOk )
			result.Warnings.Add( "spawn-invalid:" + result.SpawnReason );
		if ( !result.Sealed )
			result.Warnings.Add( "map-unsealed" );
		foreach ( var hint in hints )
			if ( !hint.Covered )
				result.Warnings.Add( $"uncovered-hint:{( string.IsNullOrEmpty( hint.ClassName ) ? "point" : hint.ClassName )}@{hint.Point}" );
		return result;
	}

	static void ValidateOptions( NavMeshGenOptions options )
	{
		if ( options.CellSize <= 0f ) throw new ArgumentException( "CellSize must be positive.", nameof( options ) );
		if ( options.AgentRadius < 0f ) throw new ArgumentException( "AgentRadius must not be negative.", nameof( options ) );
		if ( options.AgentHeight <= 0f ) throw new ArgumentException( "AgentHeight must be positive.", nameof( options ) );
		if ( options.StepHeight < 0f ) throw new ArgumentException( "StepHeight must not be negative.", nameof( options ) );
		if ( options.MaxDrop < 0f ) throw new ArgumentException( "MaxDrop must not be negative.", nameof( options ) );
		if ( options.MaxSlopeDegrees < 0f || options.MaxSlopeDegrees > 90f )
			throw new ArgumentException( "MaxSlopeDegrees must be between 0 and 90.", nameof( options ) );
		if ( options.MaxCells <= 0 ) throw new ArgumentException( "MaxCells must be positive.", nameof( options ) );
	}

	static readonly (int, int)[] Neighbours = { (1, 0), (-1, 0), (0, 1), (0, -1) };

	static bool SameCell( Cell[,] cells, bool[,] claimed, int x, int y, int region, float height ) =>
		cells[x, y].Walkable && !claimed[x, y] && cells[x, y].RegionId == region &&
		MathF.Abs( cells[x, y].Height - height ) <= 0.01f;

	struct Solid
	{
		public Vec3 Min;
		public Vec3 Max;
		public string BodyId;
	}

	enum CellReason
	{
		None,
		NoGround,
		Slope,
		Headroom,
	}

	struct Cell
	{
		public int X;
		public int Y;
		public float Height;
		public bool Walkable;
		public CellReason Reason;
		public int RegionId;
		public int PolyId;
	}

	/// <summary>Highest INTERIOR ground at (x, y): the highest exposed solid top or
	/// displacement height whose above-point is interior space (the roof rule).
	/// Candidates are tried highest-first, so a rejected ceiling top falls through
	/// to the floor below it instead of voiding the cell. A candidate's above-point
	/// must be in a non-solid leaf that is either non-void or below the world's top
	/// surface (a void leaf above a top AT the max solid top is the sky above the
	/// roof, not a room — but a void leaf above a lower top is a courtyard floor
	/// on an unsealed map, and counts).</summary>
	static bool GroundAt( QuickPlayMap map, List<Solid> solids, IReadOnlyList<QuickPlayLeaf> leaves,
		float worldTopZ, float cellSize, float x, float y, out float height, out float slopeDegrees )
	{
		height = float.NaN;
		slopeDegrees = 0f;

		// Candidates, highest first. A brush top and a displacement height at the
		// same height are the same ground; the brush wins the tie (slope 0).
		var candidates = new List<(float Height, bool FromDisplacement)>();
		foreach ( var s in solids )
		{
			if ( x < s.Min.x - Eps || x > s.Max.x + Eps || y < s.Min.y - Eps || y > s.Max.y + Eps ) continue;
			float top = s.Max.z;
			if ( PointInSolid( solids, x, y, top + 0.5f ) ) continue;
			candidates.Add( (top, false) );
		}
		foreach ( var d in map.Displacements )
		{
			if ( d is null || !d.ContainsXY( x, y ) ) continue;
			float h = d.HeightAt( x, y );
			if ( PointInSolid( solids, x, y, h + 0.5f ) ) continue;
			candidates.Add( (h, true) );
		}
		candidates.Sort( ( a, b ) =>
		{
			int c = b.Height.CompareTo( a.Height );
			if ( c != 0 ) return c;
			// Tie: brush (flat) before displacement.
			return a.FromDisplacement.CompareTo( b.FromDisplacement );
		} );
		// Dedup near-equal heights (a displacement flush with a brush top).
		var deduped = new List<(float Height, bool FromDisplacement)>();
		foreach ( var cand in candidates )
		{
			bool dup = false;
			foreach ( var kept in deduped )
				if ( MathF.Abs( kept.Height - cand.Height ) <= Eps ) { dup = true; break; }
			if ( !dup ) deduped.Add( cand );
		}
		foreach ( var (candHeight, fromDisplacement) in deduped )
		{
			var above = new Vec3( x, y, candHeight + 1f );
			int leaf = LeafAt( leaves, above );
			if ( leaf < 0 ) continue;
			if ( leaves[leaf].Solid ) continue;
			if ( leaves[leaf].IsVoid && candHeight >= worldTopZ - Eps ) continue;
			height = candHeight;
			slopeDegrees = fromDisplacement ? DisplacementSlope( map, x, y, cellSize ) : 0f;
			return true;
		}
		return false;
	}

	/// <summary>Slope of the displacement heightfield at (x, y), in degrees, from
	/// finite differences one cell apart. The cell scale is what the agent
	/// experiences: sampling wider than the cell smears a narrow ramp gentler than
	/// it is (a 32-wide, 56-degree ramp reads as 29 degrees at a 16-unit sample).
	/// 0 when no displacement covers the point.</summary>
	static float DisplacementSlope( QuickPlayMap map, float x, float y, float step )
	{
		float maxSlope = 0f;
		foreach ( var d in map.Displacements )
		{
			if ( d is null || !d.ContainsXY( x, y ) ) continue;
			float hx0 = d.HeightAt( x - step, y );
			float hx1 = d.HeightAt( x + step, y );
			float hy0 = d.HeightAt( x, y - step );
			float hy1 = d.HeightAt( x, y + step );
			float gx = ( hx1 - hx0 ) / ( 2f * step );
			float gy = ( hy1 - hy0 ) / ( 2f * step );
			float grad = MathF.Sqrt( gx * gx + gy * gy );
			maxSlope = MathF.Max( maxSlope, MathF.Atan( grad ) * 180f / MathF.PI );
		}
		return maxSlope;
	}

	/// <summary>Leaf containing the point. A point resting exactly on a face is inside
	/// both the solid leaf below and the empty leaf above — the empty leaf wins, the
	/// same rule QuickPlay and LeakFinder use.</summary>
	static int LeafAt( IReadOnlyList<QuickPlayLeaf> leaves, Vec3 p )
	{
		if ( leaves is null ) return -1;
		for ( int i = 0; i < leaves.Count; i++ )
			if ( !leaves[i].Solid && LeafContains( leaves[i], p ) )
				return i;
		for ( int i = 0; i < leaves.Count; i++ )
			if ( LeafContains( leaves[i], p ) )
				return i;
		return -1;
	}

	static bool LeafContains( QuickPlayLeaf l, Vec3 p ) =>
		p.x >= l.Min.x - Eps && p.x <= l.Max.x + Eps &&
		p.y >= l.Min.y - Eps && p.y <= l.Max.y + Eps &&
		p.z >= l.Min.z - Eps && p.z <= l.Max.z + Eps;

	static bool PointInSolid( List<Solid> solids, float x, float y, float z )
	{
		foreach ( var s in solids )
			if ( x > s.Min.x + Eps && x < s.Max.x - Eps &&
				y > s.Min.y + Eps && y < s.Max.y - Eps &&
				z > s.Min.z + Eps && z < s.Max.z - Eps )
				return true;
		return false;
	}

	/// <summary>True when the agent capsule at (x, y) spanning [ground, ground +
	/// AgentHeight] touches a solid. A solid whose top is within StepHeight of the
	/// ground is steppable and does not block; a solid the capsule stands on
	/// (top == ground) does not count either. This is the agent-radius erosion.</summary>
	static bool CapsuleBlocked( List<Solid> solids, float x, float y, float ground, NavMeshGenOptions options )
	{
		float z1 = ground + options.AgentHeight;
		float radius = options.AgentRadius;
		foreach ( var s in solids )
		{
			if ( s.Max.z <= ground + Eps || s.Min.z >= z1 - Eps ) continue;
			if ( s.Max.z <= ground + options.StepHeight + Eps ) continue;
			float cx = Math.Clamp( x, s.Min.x, s.Max.x );
			float cy = Math.Clamp( y, s.Min.y, s.Max.y );
			float dx = x - cx, dy = y - cy;
			if ( dx * dx + dy * dy < radius * radius ) return true;
		}
		return false;
	}

	sealed class LinkAccum
	{
		public float SharedLength;
		public float MidX;
		public float MidY;
		public int EdgeCount;
	}

	static void AccumulateEdge( Cell[,] cells, Dictionary<(int, int), LinkAccum> table,
		int x0, int y0, int x1, int y1, float cell, float originX, float originY )
	{
		var a = cells[x0, y0];
		var b = cells[x1, y1];
		if ( a.PolyId == b.PolyId ) return;
		int lo = Math.Min( a.PolyId, b.PolyId );
		int hi = Math.Max( a.PolyId, b.PolyId );
		var key = (lo, hi);
		if ( !table.TryGetValue( key, out var acc ) )
		{
			acc = new LinkAccum();
			table[key] = acc;
		}
		acc.SharedLength += cell;
		// Shared-edge midpoint, in world coordinates (grid origin + cell offset).
		acc.MidX += originX + ( x0 + x1 + 1 ) * 0.5f * cell;
		acc.MidY += originY + ( y0 + y1 + 1 ) * 0.5f * cell;
		acc.EdgeCount++;
	}

	/// <summary>Drop links across the erosion gap at a solid ledge: from a walkable
	/// high cell, look up to ceil(AgentRadius / CellSize) + 1 cells in each direction
	/// for a walkable low cell, where every intermediate cell is unwalkable ONLY by
	/// headroom (eroded) at the landing height — i.e. the gap is the eroded strip
	/// beside the ledge, not a wall (a wall cell's ground is the wall top, not the
	/// landing height, so a Drop never tunnels through solid). The drop must be past
	/// StepHeight and within MaxDrop.</summary>
	static void AddDropGapLinks( Cell[,] cells, int nx, int ny,
		Dictionary<(int, int), LinkAccum> table, NavMeshGenOptions options, float originX, float originY )
	{
		int maxGap = (int)MathF.Ceiling( options.AgentRadius / options.CellSize ) + 1;
		for ( int cy = 0; cy < ny; cy++ )
		{
			for ( int cx = 0; cx < nx; cx++ )
			{
				var from = cells[cx, cy];
				if ( !from.Walkable ) continue;
				foreach ( var (dx, dy) in Neighbours )
				{
					for ( int k = 1; k <= maxGap; k++ )
					{
						int x1 = cx + dx * k, y1 = cy + dy * k;
						if ( x1 < 0 || x1 >= nx || y1 < 0 || y1 >= ny ) break;
						var mid = cells[x1, y1];
						if ( mid.Walkable )
						{
							// A directly adjacent walkable cell is the edge pass's job;
							// a gap link only bridges unwalkable cells.
							if ( k == 1 ) break;
							float drop = from.Height - mid.Height;
							if ( drop > options.StepHeight + Eps && drop <= options.MaxDrop + Eps &&
								from.PolyId != mid.PolyId )
							{
								int lo = Math.Min( from.PolyId, mid.PolyId );
								int hi = Math.Max( from.PolyId, mid.PolyId );
								var key = (lo, hi);
								if ( !table.TryGetValue( key, out var acc ) )
								{
									acc = new LinkAccum();
									table[key] = acc;
								}
								acc.SharedLength += options.CellSize;
								acc.MidX += originX + ( cx + x1 + 1 ) * 0.5f * options.CellSize;
								acc.MidY += originY + ( cy + y1 + 1 ) * 0.5f * options.CellSize;
								acc.EdgeCount++;
							}
							break;
						}
						// Intermediate cell: only an eroded strip at the landing height
						// may be crossed. Anything else (no ground, a slope failure, a
						// wall top) ends the search.
						if ( mid.Reason != CellReason.Headroom ) break;
						if ( float.IsNaN( mid.Height ) ) break;
						float landingDrop = from.Height - mid.Height;
						if ( landingDrop <= options.StepHeight + Eps || landingDrop > options.MaxDrop + Eps ) break;
					}
				}
			}
		}
	}

	static List<NavMeshLink> BuildLinks( List<NavMeshPoly> polys,
		Dictionary<(int, int), LinkAccum> table, NavMeshGenOptions options )
	{
		var links = new List<NavMeshLink>();
		var keys = table.Keys.ToList();
		keys.Sort( ( a, b ) =>
		{
			int c = a.Item1.CompareTo( b.Item1 );
			return c != 0 ? c : a.Item2.CompareTo( b.Item2 );
		} );
		foreach ( var key in keys )
		{
			var acc = table[key];
			var a = polys[key.Item1];
			var b = polys[key.Item2];
			float dh = b.Height - a.Height;
			var mid = new Vec3(
				acc.MidX / acc.EdgeCount,
				acc.MidY / acc.EdgeCount,
				( a.Height + b.Height ) * 0.5f );
			if ( MathF.Abs( dh ) <= 0.01f )
			{
				if ( a.RegionId != b.RegionId ) continue;
				links.Add( new NavMeshLink { From = a.Id, To = b.Id, Kind = NavMeshLinkKind.Walk, Midpoint = mid, HeightDelta = 0f, SharedLength = acc.SharedLength } );
				links.Add( new NavMeshLink { From = b.Id, To = a.Id, Kind = NavMeshLinkKind.Walk, Midpoint = mid, HeightDelta = 0f, SharedLength = acc.SharedLength } );
			}
			else if ( MathF.Abs( dh ) <= options.StepHeight + Eps )
			{
				if ( a.RegionId != b.RegionId ) continue;
				if ( dh > 0 )
				{
					links.Add( new NavMeshLink { From = a.Id, To = b.Id, Kind = NavMeshLinkKind.StepUp, Midpoint = mid, HeightDelta = dh, SharedLength = acc.SharedLength } );
					links.Add( new NavMeshLink { From = b.Id, To = a.Id, Kind = NavMeshLinkKind.StepDown, Midpoint = mid, HeightDelta = -dh, SharedLength = acc.SharedLength } );
				}
				else
				{
					links.Add( new NavMeshLink { From = b.Id, To = a.Id, Kind = NavMeshLinkKind.StepUp, Midpoint = mid, HeightDelta = -dh, SharedLength = acc.SharedLength } );
					links.Add( new NavMeshLink { From = a.Id, To = b.Id, Kind = NavMeshLinkKind.StepDown, Midpoint = mid, HeightDelta = dh, SharedLength = acc.SharedLength } );
				}
			}
			else if ( MathF.Abs( dh ) <= options.MaxDrop + Eps )
			{
				// One-way downhill: the only cross-region link kind.
				if ( dh > 0 )
					links.Add( new NavMeshLink { From = b.Id, To = a.Id, Kind = NavMeshLinkKind.Drop, Midpoint = mid, HeightDelta = -dh, SharedLength = acc.SharedLength } );
				else
					links.Add( new NavMeshLink { From = a.Id, To = b.Id, Kind = NavMeshLinkKind.Drop, Midpoint = mid, HeightDelta = dh, SharedLength = acc.SharedLength } );
			}
		}
		links.Sort( ( x, y ) =>
		{
			int c = x.From.CompareTo( y.From );
			if ( c != 0 ) return c;
			c = x.To.CompareTo( y.To );
			return c != 0 ? c : x.Kind.CompareTo( y.Kind );
		} );
		return links;
	}
}
