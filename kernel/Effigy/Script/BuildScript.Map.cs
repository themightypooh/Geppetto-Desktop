// Hammer Desktop AI: BuildScript verbs for native map tools.
// Board: hammer-ai-buildscript-verbs-map. Group name 'buildscriptmap' (tests/BuildScriptMapTests.cs).
//
// The 20 verbs, in SEAM order (tests/fixtures/hammer/hammer-ai-aisuitetools-apicatalog-map/SEAM.md):
//   map_brush_create, map_brush_edit, map_brush_clip, map_brush_vertex,
//   map_displacement_create, map_displacement_sculpt, map_face_material, map_face_uv,
//   map_entity_place, map_entity_edit, map_entity_io_add, map_group, map_visgroup,
//   map_import, map_export, map_compile, map_validate, map_lightmap_bake, map_navmesh, map_skybox
// They live in MapVerbNames only — NOT in VerbNames (see ApiCatalog.cs, which reads
// MapVerbNames). Each verb returns a named result in r.Log or a named error via Fail;
// no verb ever silently succeeds on a no-op: a kernel no-op outcome (e.g. clip plane
// outside the brush) is logged with its outcome name.
//
// STATE. Map state lives in per-name MapSession objects keyed by map=name (default
// "map"), OUTSIDE the PartStudio — AiSuiteTools.RunScriptTool round-trips the
// PartStudio through StudioDocument.Write/Read between verbs, so anything stored
// on the studio would not survive a map_command call. Sessions are process-wide
// and survive across BuildScript.Run calls in the same session.
//
// HISTORY. MapDocument is history-based: Add* clones data into history entries and
// re-folds. In-place mutation of a folded object is silently reverted on the next
// rebuild, so every mutation goes through a doc method. ReplaceBrush below is the
// one sanctioned whole-brush swap (snapshot displacements + tied entities, remove,
// re-add with the same id, restore displacements and ties).
//
// LANDMINES (verified against the landed kernel; do NOT "fix" by inventing stubs):
//  - MapBrushPrimitives (Effigy.Map) is built against a nonexistent MapFace/MapPlane
//    stub. Shapes are built here instead, from MapBrush + MapBrushFace, with plane
//    builders adapted from McpMapTools.cs (BuildWedge/BuildCylinder/BuildSphere/
//    BuildArch) until that file is reconciled with the landed MapBrush.
//  - MapDocument.cs / MapDocumentIo.cs: the `EntityOutput` -> `EntityIoOutput`
//    rename has LANDED (verified 2026-11-13: zero bare `EntityOutput` in either
//    file; `using System.IO;` present; both compile in the harness). I/O rows
//    still live in a session EntityIoList, NOT doc.AddIo — that is a design
//    choice now (MCP pattern), not a workaround; doc.AddIo may be usable and
//    the integrator can move rows into the doc. The doc's I/O cascade on
//    RemoveEntity therefore has nothing to cascade; session I/O is unaffected.
//  - MapDocument.cs fixes made by THIS ticket (2026-11-13, backup
//    /tmp/MapDocument.cs.bak-daxter-20261113): (1) MapBrushData.Validate
//    tested |d1-d2| for opposite normals too, so every symmetric box failed
//    its own Validate — directions are now split (same-dir |d1-d2|,
//    opposite-dir |d1+d2|), matching MapBrush.Validate; (2) the
//    RemoveDisplacement fold matched d.Id == TargetId ("brushId#faceIndex"),
//    which could never match, so RemoveDisplacement always threw in the
//    fold — it now matches (BrushId, FaceIndex).
//  - `Effigy.Map.MapBrushFace` (Displacement's 10-param corner face) is NOT
//    `Effigy.MapBrushFace` (the kernel brush face). This file is in namespace
//    Effigy and never does `using Effigy.Map;` — Effigy.Map types are spelled
//    out in full. An unqualified MapBrushFace is always the kernel brush face.
//  - CompilePreset has no Quick: quick -> QuickPlay, preview -> Preview,
//    final -> Final. `quickplay` is a preset value of map_compile (QuickPlay.Bake),
//    NOT a separate verb.
//  - NavMeshGen and CubemapBake DO exist in the kernel (SEAM's "no builder"
//    claim is stale): map_navmesh calls NavMeshGen.Generate; map_skybox cubemap=1
//    calls CubemapBake.Bake. MapBakeExport.ExportGlb is a minimal stub (header +
//    asset JSON, no mesh payload) — map_lightmap_bake refuses glb with a named
//    error until the core GlbExport wiring lands.
//  - MapBrushData.SetDisplacement round-trips through MapDisplacementState, which
//    drops Alphas. Sculpt persistence therefore uses RemoveDisplacement +
//    AddDisplacement of the updated clone, never SetDisplacement.
//  - MapEntityData.TryParseTriple splits on SPACES. map_entity_edit converts
//    comma triples to space triples for origin=/angles= keys.
//  - MapTransform planes are System.Numerics.Plane (n.p + D = 0), so D = -distance.
//    FaceUv Locks default to None, which ApplyLocks leaves unchanged: rotate/scale
//    keep UVs as-is unless the face carries UvLock/ScaleLock.
//
// GRAMMAR. `map_verb [positional] key=value ...`; vectors are `x,y,z` (Vec2 `u,v`);
// unknown options are rejected with the verb's usage text. The first bare token
// (no '=') is the verb's target (brush/entity/displacement id, or name for creates).
// Values with spaces or ';' need double quotes (Tokenise is escape-aware).
//
// Registry. Entity validation needs an EntityRegistry. Resolution order:
// registry= path, then tests/fixtures/hammer/hammer-ai-mcp-map-tools/entity-registry.json
// found by walking up from baseDir, then kernel/Effigy/Map/registry/core-source1.json,
// then an embedded 11-class registry (the fixture's 10 classes, plus env_cubemap,
// with fuller keyvalues/inputs than the fixture declares). The chosen source is
// logged once per session.
// GAP, MERGED: neither file registry declares env_cubemap (fixture: 10 classes;
// core-source1: 15 classes), so whichever registry resolves gets the embedded
// env_cubemap class merged in (EntityRegistry.Add) — otherwise map_skybox
// cubemap=1 could never place a probe. Conversely the fixture does NOT declare
// light._light (core-source1 does), so a script that sets _light needs
// registry= pointing at core-source1.json or a fuller registry; the sealed-room
// fixture script does not set it.

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Effigy;

public static partial class BuildScript
{
	/// <summary>The 20 map verbs, in SEAM order. Read by ApiCatalog; NOT in VerbNames.</summary>
	public static readonly string[] MapVerbNames =
	{
		"map_brush_create", "map_brush_edit", "map_brush_clip", "map_brush_vertex",
		"map_displacement_create", "map_displacement_sculpt", "map_face_material", "map_face_uv",
		"map_entity_place", "map_entity_edit", "map_entity_io_add", "map_group", "map_visgroup",
		"map_import", "map_export", "map_compile", "map_validate", "map_lightmap_bake",
		"map_navmesh", "map_skybox",
	};

	static bool IsMapVerb( string verb ) => MapVerbNames.Contains( verb );

	// ---- session state ---------------------------------------------------------

	/// <summary>One map's live state. Lives outside the PartStudio (see header).</summary>
	sealed class MapSession
	{
		public string Name = "map";
		public MapDocument Doc = MapDocument.Create( "map" );
		public Map.LinkedGroupSet Groups = new();
		public EntityIoList Io = new();
		public EntityRegistry? Registry;
		public string RegistrySource = "";
		public int NextBrush = 1;
		public int NextEntity = 1;
		public int NextDisp = 1;
		public HashSet<string> DetailBrushes = new( StringComparer.Ordinal );
		public Map.NavMeshResult? Navmesh;
		public Map.CubemapBakeSet? Cubemaps;
		public QuickPlayBakeResult? LastQuickPlay;
		public CompileResult? LastCompile;
	}

	static readonly Dictionary<string, MapSession> MapSessions = new( StringComparer.Ordinal );
	static readonly object MapSessionsLock = new();

	static MapSession SessionFor( string name )
	{
		lock ( MapSessionsLock )
		{
			if ( !MapSessions.TryGetValue( name, out var session ) )
			{
				session = new MapSession { Name = name, Doc = MapDocument.Create( name ) };
				MapSessions[name] = session;
			}
			return session;
		}
	}

	static void ResetSession( MapSession session, MapDocument doc )
	{
		session.Doc = doc;
		session.Groups = new Map.LinkedGroupSet();
		session.Io = new EntityIoList();
		session.NextBrush = 1;
		session.NextEntity = 1;
		session.NextDisp = 1;
		session.DetailBrushes.Clear();
		session.Navmesh = null;
		session.Cubemaps = null;
		session.LastQuickPlay = null;
		session.LastCompile = null;
	}

	// ---- entry point (called from BuildScript.Command's default branch) --------

	/// <summary>RigCommand-shaped hook: false = not a map verb, true = handled (ok = success).</summary>
	static bool TryRunMapVerb( PartStudio studio, string verb, List<string> args, string baseDir, Result r, out bool ok )
	{
		ok = false;
		if ( !IsMapVerb( verb ) )
			return false;
		try
		{
			ok = DispatchMapVerb( studio, verb, args, baseDir, r );
		}
		catch ( ArgumentException e )
		{
			// Named input errors become the line's error, same as Run's per-line catch.
			// Handled (true) with ok=false: returning false here would make Command
			// overwrite the named error with "Unknown command".
			Fail( r, e.Message );
			return true;
		}
		return true;
	}

	static bool DispatchMapVerb( PartStudio studio, string verb, List<string> args, string baseDir, Result r )
	{
		switch ( verb )
		{
			case "map_brush_create": return MapBrushCreate( args, baseDir, r );
			case "map_brush_edit": return MapBrushEdit( args, baseDir, r );
			case "map_brush_clip": return MapBrushClip( args, baseDir, r );
			case "map_brush_vertex": return MapBrushVertex( args, baseDir, r );
			case "map_displacement_create": return MapDisplacementCreate( args, baseDir, r );
			case "map_displacement_sculpt": return MapDisplacementSculpt( args, baseDir, r );
			case "map_face_material": return MapFaceMaterial( args, baseDir, r );
			case "map_face_uv": return MapFaceUv( args, baseDir, r );
			case "map_entity_place": return MapEntityPlace( args, baseDir, r );
			case "map_entity_edit": return MapEntityEdit( args, baseDir, r );
			case "map_entity_io_add": return MapEntityIoAdd( args, baseDir, r );
			case "map_group": return MapGroup( args, baseDir, r );
			case "map_visgroup": return MapVisGroup( args, baseDir, r );
			case "map_import": return MapImport( args, baseDir, r );
			case "map_export": return MapExport( args, baseDir, r );
			case "map_compile": return MapCompile( args, baseDir, r );
			case "map_validate": return MapValidate( args, baseDir, r );
			case "map_lightmap_bake": return MapLightmapBake( args, baseDir, r );
			case "map_navmesh": return MapNavmesh( args, baseDir, r );
			case "map_skybox": return MapSkybox( args, baseDir, r );
			default: return Fail( r, $"map verb '{verb}' is listed in MapVerbNames but has no handler." );
		}
	}

	// ---- argument helpers ------------------------------------------------------

	sealed class MapArgs
	{
		public List<string> Positional = new();
		public Dictionary<string, string> Opts = new( StringComparer.OrdinalIgnoreCase );
		public MapSession Session = null!;
		public string BaseDir = "";
	}

	static MapArgs ParseMapArgs( List<string> args, string baseDir, string usage )
	{
		var parsed = new MapArgs { BaseDir = baseDir ?? Directory.GetCurrentDirectory() };
		foreach ( var token in args )
		{
			var eq = token.IndexOf( '=' );
			if ( eq <= 0 )
				parsed.Positional.Add( token );
			else
				parsed.Opts[token[..eq].Trim().ToLowerInvariant()] = token[(eq + 1)..].Trim();
		}
		var mapName = parsed.Opts.TryGetValue( "map", out var m ) && m.Length > 0 ? m : "map";
		parsed.Session = SessionFor( mapName );
		return parsed;
	}

	static bool RejectUnknownOpts( MapArgs a, Result r, string usage, params string[] allowed )
	{
		foreach ( var key in a.Opts.Keys )
		{
			if ( key == "map" )
				continue;
			if ( !allowed.Contains( key, StringComparer.OrdinalIgnoreCase ) )
				return Fail( r, $"unknown option '{key}' for this map verb. usage: {usage}" );
		}
		return true;
	}

	static string Opt( MapArgs a, string key, string fallback = "" ) =>
		a.Opts.TryGetValue( key, out var v ) ? v : fallback;

	static bool HasOpt( MapArgs a, string key ) => a.Opts.ContainsKey( key );

	static string ReqOpt( MapArgs a, string key, string usage ) =>
		a.Opts.TryGetValue( key, out var v ) && v.Length > 0 ? v : throw new ArgumentException( $"missing '{key}='. usage: {usage}" );

	static bool ParseVec3( string text, out Vec3 v ) => ModelMeasure.TryParseVec3( text, out v );

	static Vec3 ReqVec3( MapArgs a, string key, string usage )
	{
		var text = ReqOpt( a, key, usage );
		if ( !ParseVec3( text, out var v ) )
			throw new ArgumentException( $"'{key}={text}' is not x,y,z." );
		return v;
	}

	static Vec3 OptVec3( MapArgs a, string key, Vec3 fallback, string usage )
	{
		if ( !HasOpt( a, key ) )
			return fallback;
		return ReqVec3( a, key, usage );
	}

	static bool ParseVec2( string text, out Vec2 v )
	{
		v = Vec2.Zero;
		if ( string.IsNullOrWhiteSpace( text ) )
			return false;
		var p = text.Split( ',' );
		if ( p.Length != 2 )
			return false;
		if ( !float.TryParse( p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x ) )
			return false;
		if ( !float.TryParse( p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y ) )
			return false;
		v = new Vec2( x, y );
		return float.IsFinite( x ) && float.IsFinite( y );
	}

	static Vec2 ReqVec2( MapArgs a, string key, string usage )
	{
		var text = ReqOpt( a, key, usage );
		if ( !ParseVec2( text, out var v ) )
			throw new ArgumentException( $"'{key}={text}' is not u,v." );
		return v;
	}

	static int ReqInt( MapArgs a, string key, string usage )
	{
		var text = ReqOpt( a, key, usage );
		if ( !int.TryParse( text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v ) )
			throw new ArgumentException( $"'{key}={text}' is not an integer." );
		return v;
	}

	static int OptInt( MapArgs a, string key, int fallback, string usage )
	{
		if ( !HasOpt( a, key ) )
			return fallback;
		return ReqInt( a, key, usage );
	}

	static float ReqFloat( MapArgs a, string key, string usage )
	{
		var text = ReqOpt( a, key, usage );
		if ( !TryNumber( text, out var v ) )
			throw new ArgumentException( $"'{key}={text}' is not a number." );
		return v;
	}

	static float OptFloat( MapArgs a, string key, float fallback, string usage )
	{
		if ( !HasOpt( a, key ) )
			return fallback;
		return ReqFloat( a, key, usage );
	}

	static bool OptBool( MapArgs a, string key, bool fallback, string usage )
	{
		if ( !HasOpt( a, key ) )
			return fallback;
		return Truthy( Opt( a, key ) );
	}

	static string FmtSp( Vec3 v ) =>
		$"{v.x.ToString( CultureInfo.InvariantCulture )} {v.y.ToString( CultureInfo.InvariantCulture )} {v.z.ToString( CultureInfo.InvariantCulture )}";

	static string Fmt( Vec3 v ) => $"{v.x.ToString( CultureInfo.InvariantCulture )},{v.y.ToString( CultureInfo.InvariantCulture )},{v.z.ToString( CultureInfo.InvariantCulture )}";

	static string FmtF( float v ) => v.ToString( CultureInfo.InvariantCulture );

	// System.Numerics conversions (MapTransform works in Numerics types).
	static Vector3 N3( Vec3 v ) => new( v.x, v.y, v.z );
	static Vec3 E3( Vector3 v ) => new( v.X, v.Y, v.Z );

	// ---- registry --------------------------------------------------------------

	static EntityRegistry RegistryFor( MapArgs a )
	{
		var session = a.Session;
		if ( session.Registry is not null )
			return session.Registry;

		// 1. registry= path under baseDir.
		if ( HasOpt( a, "registry" ) )
		{
			var path = Resolve( a.BaseDir, Opt( a, "registry" ) );
			try
			{
				session.Registry = EntityRegistry.Load( path );
				session.RegistrySource = $"registry= {path}";
				MergeEnvCubemap( session );
				return session.Registry;
			}
			catch ( Exception e )
			{
				throw new ArgumentException( $"registry-load-failed: cannot load registry '{path}': {e.Message}" );
			}
		}

		// 2. Walk up from baseDir for the MCP fixture registry, then the kernel one.
		var dir = new DirectoryInfo( a.BaseDir );
		while ( dir is not null )
		{
			foreach ( var candidate in new[]
			{
				Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-ai-mcp-map-tools", "entity-registry.json" ),
				Path.Combine( dir.FullName, "kernel", "Effigy", "Map", "registry", "core-source1.json" ),
			} )
			{
				if ( !File.Exists( candidate ) )
					continue;
				try
				{
					session.Registry = EntityRegistry.Load( candidate );
					session.RegistrySource = $"file {candidate}";
					MergeEnvCubemap( session );
					return session.Registry;
				}
				catch
				{
					// A corrupt candidate falls through to the embedded registry.
				}
			}
			dir = dir.Parent;
		}

		// 3. Embedded registry (the fixture's 10 classes + env_cubemap, fuller kvs).
		session.Registry = EntityRegistry.LoadFromJson( EmbeddedRegistryJson, "embedded-minimal" );
		session.RegistrySource = "embedded registry (11 classes)";
		return session.Registry;
	}

	/// <summary>
	/// Neither file registry declares env_cubemap; merge the embedded class into
	/// the resolved registry so cubemap probes can be placed under any source.
	/// </summary>
	static void MergeEnvCubemap( MapSession session )
	{
		if ( session.Registry is null || session.Registry.TryGet( "env_cubemap", out _ ) )
			return;
		var embedded = EntityRegistry.LoadFromJson( EmbeddedRegistryJson, "embedded-minimal" );
		if ( embedded.TryGet( "env_cubemap", out var cube ) )
		{
			session.Registry.Add( cube );
			session.RegistrySource += " + env_cubemap (embedded)";
		}
	}

	const string EmbeddedRegistryJson = """
		{"classes":[
		{"name":"info_player_start","kind":"point","description":"Player spawn point.","helpers":[{"kind":"origin"},{"kind":"angles"}],"keyvalues":[],"inputs":[],"outputs":[]},
		{"name":"info_node","kind":"point","description":"AI node.","helpers":[{"kind":"origin"}],"keyvalues":[],"inputs":[],"outputs":[]},
		{"name":"light","kind":"point","description":"Omnidirectional point light.","helpers":[{"kind":"origin"}],"keyvalues":[{"name":"_light","type":"color"}],"inputs":[{"name":"TurnOn"},{"name":"TurnOff"},{"name":"Toggle"}],"outputs":[]},
		{"name":"light_environment","kind":"point","description":"Environment light.","helpers":[{"kind":"origin"},{"kind":"angles"}],"keyvalues":[{"name":"_light","type":"color"}],"inputs":[],"outputs":[]},
		{"name":"sky_camera","kind":"point","description":"Skybox camera.","helpers":[{"kind":"origin"}],"keyvalues":[{"name":"scale","type":"float"}],"inputs":[],"outputs":[]},
		{"name":"env_cubemap","kind":"point","description":"Cubemap probe.","helpers":[{"kind":"origin"}],"keyvalues":[],"inputs":[],"outputs":[]},
		{"name":"func_door","kind":"brush","description":"Sliding/rotating door.","helpers":[{"kind":"origin"}],"keyvalues":[{"name":"targetname","type":"string"}],"inputs":[{"name":"Open"},{"name":"Close"},{"name":"Toggle"}],"outputs":[{"name":"OnFullyOpen"},{"name":"OnFullyClosed"}]},
		{"name":"trigger_multiple","kind":"brush","description":"Trigger volume.","helpers":[],"keyvalues":[{"name":"targetname","type":"string"}],"inputs":[{"name":"Enable"},{"name":"Disable"}],"outputs":[{"name":"OnTrigger"}]},
		{"name":"logic_relay","kind":"point","description":"Logic relay.","helpers":[{"kind":"origin"}],"keyvalues":[{"name":"targetname","type":"string"}],"inputs":[{"name":"Trigger"}],"outputs":[{"name":"OnTrigger"}]},
		{"name":"logic_auto","kind":"point","description":"Fires on map spawn.","helpers":[{"kind":"origin"}],"keyvalues":[],"inputs":[],"outputs":[{"name":"OnMapSpawn"}]},
		{"name":"prop_static","kind":"point","description":"Static prop.","helpers":[{"kind":"origin"},{"kind":"angles"}],"keyvalues":[{"name":"model","type":"string"}],"inputs":[],"outputs":[]}
		]}
		""";

	// ---- lookups -----------------------------------------------------------------

	static MapBrushData ReqBrush( MapArgs a, string id )
	{
		var brush = a.Session.Doc.FindBrush( id );
		if ( brush is null )
			throw new ArgumentException( $"brush-not-found: no brush '{id}' in map '{a.Session.Name}'." );
		return brush;
	}

	static MapEntityData ReqEntity( MapArgs a, string idOrTarget )
	{
		var doc = a.Session.Doc;
		var entity = doc.FindEntity( idOrTarget ) ?? doc.FindEntityByTargetName( idOrTarget );
		if ( entity is null )
			throw new ArgumentException( $"entity-not-found: no entity '{idOrTarget}' (id or targetname) in map '{a.Session.Name}'." );
		return entity;
	}

	static MapDisplacementData ReqDisp( MapArgs a, string id )
	{
		// FindDisplacement is keyed by (brushId, faceIndex), not by id.
		var disp = a.Session.Doc.Displacements.FirstOrDefault( d => d.Id == id );
		if ( disp is null )
			throw new ArgumentException( $"displacement-not-found: no displacement '{id}' in map '{a.Session.Name}'." );
		return disp;
	}

	static string NextBrushId( MapSession s ) => $"brush-{s.NextBrush++}";
	static string NextEntityId( MapSession s ) => $"ent-{s.NextEntity++}";
	static string NextDispId( MapSession s ) => $"disp-{s.NextDisp++}";

	static string LogRegistrySource( MapArgs a )
	{
		RegistryFor( a );
		return $"registry: {a.Session.RegistrySource}";
	}

	// ---- history-safe replacement --------------------------------------------------

	/// <summary>
	/// Whole-brush swap that survives the history fold: snapshot the brush's
	/// displacements and tied entities, remove the brush (which cascades them),
	/// re-add the new brush data under the same id, then restore displacements
	/// and entity ties from the snapshots.
	/// </summary>
	static void ReplaceBrush( MapSession session, MapBrushData replacement )
	{
		var doc = session.Doc;
		var id = replacement.Id;
		var disps = doc.Displacements.Where( d => d.BrushId == id ).Select( d => d.Clone() ).ToList();
		var tied = doc.Entities.Where( e => e.BrushIds.Contains( id ) ).Select( e => e.Clone() ).ToList();
		doc.RemoveBrush( id );
		doc.AddBrush( replacement );
		foreach ( var d in disps )
			doc.AddDisplacement( d );
		foreach ( var e in tied )
		{
			doc.RemoveEntity( e.Id );
			doc.AddEntity( e );
		}
	}

	/// <summary>Sculpt persistence: remove + re-add the displacement clone so
	/// Offsets AND Alphas survive (SetDisplacement drops Alphas).</summary>
	static void ReplaceDisplacement( MapSession session, MapDisplacementData updated )
	{
		var doc = session.Doc;
		doc.RemoveDisplacement( updated.BrushId, updated.FaceIndex );
		doc.AddDisplacement( updated );
	}

	static MapBrushData BrushDataFromBrush( MapBrush brush, string id, string name )
	{
		var data = MapBrushData.FromMapBrush( brush, id, name );
		var defects = data.Validate();
		if ( defects.Count > 0 )
			throw new ArgumentException( $"brush-invalid: {string.Join( "; ", defects )}" );
		return data;
	}

	// ---- projections ---------------------------------------------------------------

	/// <summary>One doc face -> one VMF face. PlanePoints come from the brush's
	/// computed face vertices (first 3); StoredVertices carries the full loop as
	/// the no-drift record. UV axes come from FaceUv.PlaneAxes + the face's UV.</summary>
	static Map.VmfFace FaceToVmfFace( MapBrushData brush, int faceIndex )
	{
		var face = brush.Faces[faceIndex];
		var uv = face.Material.Uv;
		var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
		var vmfFace = new Map.VmfFace
		{
			Material = face.Material.MaterialName,
			UAxis = new Map.VmfUvAxis( new Map.VmfVec( uAxis.x, uAxis.y, uAxis.z ), uv.Offset.x, uv.Scale.x ),
			VAxis = new Map.VmfUvAxis( new Map.VmfVec( vAxis.x, vAxis.y, vAxis.z ), uv.Offset.y, uv.Scale.y ),
			Rotation = uv.RotationDegrees,
			LightmapScale = (int)uv.LightmapScale,
			SmoothingGroups = uv.SmoothingGroup,
		};
		List<Vec3>? loop = null;
		if ( brush.TryComputeFaceVertices( faceIndex, out var ordered ) )
			loop = ordered;
		if ( loop is not null && loop.Count >= 3 )
		{
			vmfFace.PlanePoints = new[]
			{
				new Map.VmfVec( loop[0].x, loop[0].y, loop[0].z ),
				new Map.VmfVec( loop[1].x, loop[1].y, loop[1].z ),
				new Map.VmfVec( loop[2].x, loop[2].y, loop[2].z ),
			};
			vmfFace.StoredVertices = loop.Select( v => new Map.VmfVec( v.x, v.y, v.z ) ).ToList();
		}
		else
		{
			// No computable loop (should not happen on a valid brush): synthesize
			// three points on the plane from its normal and distance.
			var p0 = face.Normal * face.Distance;
			var p1 = p0 + uAxis;
			var p2 = p0 + vAxis;
			vmfFace.PlanePoints = new[]
			{
				new Map.VmfVec( p0.x, p0.y, p0.z ),
				new Map.VmfVec( p1.x, p1.y, p1.z ),
				new Map.VmfVec( p2.x, p2.y, p2.z ),
			};
		}
		return vmfFace;
	}

	static Map.VmfBrush BrushDataToVmfBrush( MapBrushData b ) =>
		new() { Id = ParseIdNumber( b.Id ), Faces = Enumerable.Range( 0, b.Faces.Count ).Select( i => FaceToVmfFace( b, i ) ).ToList() };

	/// <summary>MapDocument -> VmfDocument (MCP ImportDocument/ToVmfDocument shape).</summary>
	static Map.VmfDocument ToVmfDocument( MapSession session )
	{
		var doc = session.Doc;
		var vmf = new Map.VmfDocument();
		foreach ( var b in doc.Brushes )
			vmf.WorldBrushes.Add( BrushDataToVmfBrush( b ) );
		foreach ( var e in doc.Entities )
		{
			var vmfEntity = new Map.VmfEntity { ClassName = e.ClassName };
			foreach ( var kv in e.KeyValues )
				vmfEntity.Set( kv.Key, kv.Value );
			vmfEntity.Set( "origin", FmtSp( e.Origin ) );
			if ( e.Angles.x != 0 || e.Angles.y != 0 || e.Angles.z != 0 )
				vmfEntity.Set( "angles", FmtSp( e.Angles ) );
			foreach ( var brushId in e.BrushIds )
			{
				var b = doc.FindBrush( brushId );
				if ( b is null )
					continue;
				vmfEntity.Brushes.Add( BrushDataToVmfBrush( b ) );
			}
			vmf.Entities.Add( vmfEntity );
		}
		foreach ( var io in session.Io.Outputs )
		{
			var source = doc.FindEntity( io.SourceEntity );
			if ( source is null || string.IsNullOrWhiteSpace( source.TargetName ) )
				continue;
			var vmfEntity = vmf.Entities.FirstOrDefault( e => e.Get( "targetname" ) == source.TargetName && e.ClassName == source.ClassName );
			if ( vmfEntity is null )
				continue;
			vmfEntity.Outputs.Add( new Map.VmfOutput
			{
				Event = io.Event,
				Target = io.TargetName,
				Input = io.Input,
				Parameter = io.Parameter,
				Delay = io.DelaySeconds,
				FireCount = io.MaxFires,
			} );
		}
		foreach ( var vg in doc.VisGroups )
			vmf.VisGroups.Add( new Map.VmfVisGroup { Id = ParseIdNumber( vg.Id ), Name = vg.Name, Color = HexToRgb( vg.Colour ) } );
		var settings = doc.CompileSettings;
		if ( !string.IsNullOrWhiteSpace( settings.SkyName ) )
			vmf.WorldspawnSet( "skyname", settings.SkyName );
		return vmf;
	}

	/// <summary>MapVisGroup.Colour is hex "#rrggbb"; VmfVisGroup.Color is "r g b".</summary>
	static string HexToRgb( string hex )
	{
		if ( string.IsNullOrWhiteSpace( hex ) )
			return "128 128 128";
		hex = hex.Trim().TrimStart( '#' );
		if ( hex.Length == 6
			&& int.TryParse( hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r )
			&& int.TryParse( hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g )
			&& int.TryParse( hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b ) )
			return $"{r} {g} {b}";
		return "128 128 128";
	}

	/// <summary>"r g b" -> hex "#rrggbb"; unparsable input falls back to grey.</summary>
	static string RgbToHex( string rgb )
	{
		var parts = ( rgb ?? "" ).Split( new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length >= 3
			&& int.TryParse( parts[0], out var r ) && int.TryParse( parts[1], out var g ) && int.TryParse( parts[2], out var b ) )
			return $"#{Math.Clamp( r, 0, 255 ):x2}{Math.Clamp( g, 0, 255 ):x2}{Math.Clamp( b, 0, 255 ):x2}";
		return "#808080";
	}

	static int ParseIdNumber( string id )
	{
		if ( string.IsNullOrWhiteSpace( id ) )
			return 0;
		var digits = new string( id.Where( char.IsDigit ).ToArray() );
		return int.TryParse( digits, out var n ) ? n : 0;
	}

	/// <summary>MapDocument -> VmfDocument -> MapDocument import (VMF/.map path).</summary>
	static MapDocument ImportVmfDocument( Map.VmfDocument vmf, List<string> warnings )
	{
		var doc = MapDocument.Create( "import" );
		var brushIndex = 0;
		foreach ( var vmfBrush in vmf.WorldBrushes )
		{
			brushIndex++;
			var faces = new List<MapBrushFace>();
			var bad = false;
			foreach ( var vmfFace in vmfBrush.Faces )
			{
				var (normal, distance) = vmfFace.ToPlane();
				var n = new Vec3( (float)normal.X, (float)normal.Y, (float)normal.Z );
				if ( n.LengthSquared < 1e-12f )
				{
					warnings.Add( $"brush {brushIndex}: skipped a face with a zero normal." );
					bad = true;
					continue;
				}
				faces.Add( new MapBrushFace( n.Normal, (float)distance, vmfFace.Material ?? "" ) );
			}
			if ( bad || faces.Count < 4 )
			{
				warnings.Add( $"brush {brushIndex}: skipped (needs >= 4 usable faces, got {faces.Count})." );
				continue;
			}
			MapBrush brush;
			try
			{
				brush = MapBrush.FromPlanes( faces );
			}
			catch ( Exception e )
			{
				warnings.Add( $"brush {brushIndex}: skipped ({e.Message})." );
				continue;
			}
			var defects = brush.Validate();
			if ( defects.Count > 0 )
			{
				warnings.Add( $"brush {brushIndex}: skipped (invalid: {string.Join( "; ", defects )})." );
				continue;
			}
			doc.AddBrush( MapBrushData.FromMapBrush( brush, $"brush-{brushIndex}", $"brush-{brushIndex}" ) );
		}
		var entityIndex = 0;
		foreach ( var vmfEntity in vmf.Entities )
		{
			entityIndex++;
			var data = new MapEntityData
			{
				Id = $"ent-{entityIndex}",
				ClassName = vmfEntity.ClassName,
			};
			foreach ( var kv in vmfEntity.KeyValues )
			{
				if ( kv.Key.Equals( "origin", StringComparison.OrdinalIgnoreCase ) )
				{
					if ( MapEntityData.TryParseTriple( kv.Value, out var origin ) )
						data.Origin = origin;
					else
						warnings.Add( $"entity {entityIndex} '{vmfEntity.ClassName}': origin '{kv.Value}' is not three numbers, kept at 0,0,0." );
					continue;
				}
				if ( kv.Key.Equals( "angles", StringComparison.OrdinalIgnoreCase ) )
				{
					if ( MapEntityData.TryParseTriple( kv.Value, out var angles ) )
						data.Angles = angles;
					else
						warnings.Add( $"entity {entityIndex} '{vmfEntity.ClassName}': angles '{kv.Value}' is not three numbers, kept at 0,0,0." );
					continue;
				}
				data.KeyValues[kv.Key] = kv.Value;
			}
			foreach ( var vmfBrush in vmfEntity.Brushes )
			{
				var faces = new List<MapBrushFace>();
				foreach ( var vmfFace in vmfBrush.Faces )
				{
					var (normal, distance) = vmfFace.ToPlane();
					var n = new Vec3( (float)normal.X, (float)normal.Y, (float)normal.Z );
					if ( n.LengthSquared < 1e-12f )
						continue;
					faces.Add( new MapBrushFace( n.Normal, (float)distance, vmfFace.Material ?? "" ) );
				}
				if ( faces.Count < 4 )
				{
					warnings.Add( $"entity {entityIndex} '{vmfEntity.ClassName}': skipped a brush with {faces.Count} usable faces." );
					continue;
				}
				try
				{
					var brush = MapBrush.FromPlanes( faces );
					var bid = $"brush-{++brushIndex}";
					doc.AddBrush( MapBrushData.FromMapBrush( brush, bid, bid ) );
					data.BrushIds.Add( bid );
				}
				catch ( Exception e )
				{
					warnings.Add( $"entity {entityIndex} '{vmfEntity.ClassName}': skipped a brush ({e.Message})." );
					continue;
				}
			}
			doc.AddEntity( data );
		}
		// Displacements on VMF faces are NOT imported (VmfDisplacement grids need
		// a face-corner reconstruction this ticket does not attempt); warn once,
		// don't fake them.
		var hasDisplacement = vmf.WorldBrushes.Any( b => b.Faces.Any( f => f.Displacement is not null ) )
			|| vmf.Entities.Any( e => e.Brushes.Any( b => b.Faces.Any( f => f.Displacement is not null ) ) );
		if ( hasDisplacement )
			warnings.Add( "displacement grids are not imported (face-corner reconstruction not implemented); affected brushes were imported flat." );
		foreach ( var vg in vmf.VisGroups )
			doc.AddVisGroup( new MapVisGroup { Id = $"vis-{vg.Id}", Name = vg.Name, Colour = RgbToHex( vg.Color ) } );
		var sky = vmf.WorldspawnGet( "skyname" );
		if ( !string.IsNullOrWhiteSpace( sky ) )
		{
			var settings = doc.CompileSettings.Clone();
			settings.SkyName = sky;
			doc.SetCompileSettings( settings );
		}
		return doc;
	}

	/// <summary>MapDocument -> QuickPlayMap (compile/quickplay/navmesh projection).</summary>
	static QuickPlayMap ToQuickPlayMap( MapSession session )
	{
		var doc = session.Doc;
		var map = new QuickPlayMap();
		foreach ( var b in doc.VisibleBrushes() )
		{
			if ( !b.TryBounds( out var min, out var max ) )
				continue;
			map.Brushes.Add( new QuickPlayBrush( b.Id, min, max,
				session.DetailBrushes.Contains( b.Id ),
				b.Faces.Count > 0 ? b.Faces[0].Material.MaterialName : "" ) );
		}
		foreach ( var e in doc.VisibleEntities() )
		{
			map.Entities.Add( new QuickPlayEntity( e.ClassName, e.Origin, e.Angles.y ) );
		}
		foreach ( var d in doc.Displacements )
		{
			var brush = doc.FindBrush( d.BrushId );
			if ( brush is null || !brush.TryComputeFaceVertices( d.FaceIndex, out var corners ) || corners.Count < 4 )
				continue;
			var size = d.GridSize;
			var heights = new float[size, size];
			for ( var r = 0; r < size; r++ )
				for ( var c = 0; c < size; c++ )
					heights[r, c] = d.Offsets[r * size + c];
			map.Displacements.Add( new QuickPlayDisplacement( corners[0],
				( corners[1] - corners[0] ).Length,
				( corners[3] - corners[0] ).Length,
				size, size, heights ) );
		}
		return map;
	}

	/// <summary>MapDocument -> InMemoryMapCheckDocument (validation projection).</summary>
	static InMemoryMapCheckDocument ToCheckDocument( MapSession session )
	{
		var doc = session.Doc;
		var check = new InMemoryMapCheckDocument { Name = doc.Name };
		var known = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var b in doc.Brushes )
		{
			if ( !b.TryBounds( out var min, out var max ) )
				continue;
			check.AddBrush( new CheckBrush
			{
				Id = b.Id,
				Min = min,
				Max = max,
				Material = b.Faces.Count > 0 ? b.Faces[0].Material.MaterialName : "",
				FaceCount = b.Faces.Count,
			} );
			foreach ( var f in b.Faces )
				known.Add( f.Material.MaterialName );
		}
		foreach ( var e in doc.Entities )
		{
			var checkEntity = new CheckEntity { Id = e.Id, ClassName = e.ClassName, Origin = e.Origin };
			foreach ( var kv in e.KeyValues )
				checkEntity.Properties[kv.Key] = kv.Value;
			check.AddEntity( checkEntity );
			if ( e.ClassName == "info_player_start" )
			{
				check.HasPlayerStart = true;
				check.PlayerStart = e.Origin;
			}
		}
		foreach ( var material in known )
			check.AddKnownMaterial( material );
		return check;
	}

	// ---- plane builders (adapted from McpMapTools.cs; MapBrushPrimitives is stub-broken) ----

	static MapBrushFace PlaneFace( Vec3 normal, float distance, string material ) =>
		new( normal, distance, new FaceMaterial( material ) );

	static MapBrush FromPlanesTranslated( IReadOnlyList<MapBrushFace> faces, Vec3 position ) =>
		MapBrush.FromPlanes( faces.Select( f => new MapBrushFace( f.Normal, f.Distance + Vec3.Dot( f.Normal, position ), f.Material ) ) );

	static IReadOnlyList<MapBrushFace> BuildWedge( Vec3 size, string material )
	{
		var hx = size.x * 0.5f;
		var hy = size.y * 0.5f;
		var hz = size.z * 0.5f;
		var slope = new Vec3( hz, 0, hx ).Normal;
		return new[]
		{
			PlaneFace( new Vec3( 0, 0, -1 ), hz, material ),
			PlaneFace( new Vec3( -1, 0, 0 ), hx, material ),
			PlaneFace( slope, 0f, material ),
			PlaneFace( new Vec3( 0, -1, 0 ), hy, material ),
			PlaneFace( new Vec3( 0, 1, 0 ), hy, material ),
		};
	}

	static IReadOnlyList<MapBrushFace> BuildCylinder( float radius, float height, int segments, string material )
	{
		if ( !( radius > 0f ) || !float.IsFinite( radius ) ) throw new ArgumentException( "Cylinder radius must be positive and finite." );
		if ( !( height > 0f ) || !float.IsFinite( height ) ) throw new ArgumentException( "Cylinder height must be positive and finite." );
		if ( segments < 3 || segments > 512 ) throw new ArgumentOutOfRangeException( nameof( segments ), segments, "A brush cylinder needs 3..512 sides." );
		var hz = height * 0.5f;
		var faces = new List<MapBrushFace>( segments + 2 );
		for ( var i = 0; i < segments; i++ )
		{
			var a = i / (float)segments * MathF.Tau;
			faces.Add( PlaneFace( new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 ), radius, material ) );
		}
		faces.Add( PlaneFace( new Vec3( 0, 0, 1 ), hz, material ) );
		faces.Add( PlaneFace( new Vec3( 0, 0, -1 ), hz, material ) );
		return faces;
	}

	static IReadOnlyList<MapBrushFace> BuildSphere( float radius, int divisions, int segments, string material )
	{
		if ( !( radius > 0f ) || !float.IsFinite( radius ) ) throw new ArgumentException( "Sphere radius must be positive and finite." );
		if ( divisions < 2 || divisions > 64 ) throw new ArgumentOutOfRangeException( nameof( divisions ), divisions, "A brush sphere needs 2..64 latitude bands." );
		if ( segments < 3 || segments > 512 ) throw new ArgumentOutOfRangeException( nameof( segments ), segments, "A brush sphere needs 3..512 facets around." );
		var faces = new List<MapBrushFace>( 2 + ( divisions - 1 ) * segments );
		faces.Add( PlaneFace( new Vec3( 0, 0, 1 ), radius, material ) );
		faces.Add( PlaneFace( new Vec3( 0, 0, -1 ), radius, material ) );
		for ( var k = 1; k < divisions; k++ )
		{
			var lat = ( -90f + 180f * k / divisions ) * MathF.PI / 180f;
			var cosLat = MathF.Cos( lat );
			var sinLat = MathF.Sin( lat );
			for ( var j = 0; j < segments; j++ )
			{
				var az = j / (float)segments * MathF.Tau;
				faces.Add( PlaneFace( new Vec3( cosLat * MathF.Cos( az ), cosLat * MathF.Sin( az ), sinLat ), radius, material ) );
			}
		}
		return faces;
	}

	static IReadOnlyList<MapBrushFace> BuildArch( float outerRadius, float height, float sweepDegrees, int segments, string material )
	{
		if ( !( outerRadius > 0f ) || !float.IsFinite( outerRadius ) ) throw new ArgumentException( "Arch outerRadius must be positive and finite." );
		if ( !( height > 0f ) || !float.IsFinite( height ) ) throw new ArgumentException( "Arch height must be positive and finite." );
		if ( !( sweepDegrees > 0f ) || sweepDegrees > 180f || !float.IsFinite( sweepDegrees ) )
			throw new ArgumentOutOfRangeException( nameof( sweepDegrees ), sweepDegrees, "An arch sweep must be in (0, 180] degrees." );
		if ( segments < 2 || segments > 512 ) throw new ArgumentOutOfRangeException( nameof( segments ), segments, "An arch needs 2..512 outer facets." );
		var hz = height * 0.5f;
		var half = sweepDegrees * 0.5f * MathF.PI / 180f;
		var faces = new List<MapBrushFace>( segments + 4 );
		for ( var j = 0; j < segments; j++ )
		{
			var a = segments == 1 ? 0f : -half + 2f * half * j / ( segments - 1 );
			faces.Add( PlaneFace( new Vec3( MathF.Cos( a ), MathF.Sin( a ), 0 ), outerRadius, material ) );
		}
		if ( MathF.Abs( sweepDegrees - 180f ) < 1e-6f )
			faces.Add( PlaneFace( new Vec3( -1, 0, 0 ), 0f, material ) );
		else
		{
			faces.Add( PlaneFace( new Vec3( -MathF.Sin( half ), MathF.Cos( half ), 0 ), 0f, material ) );
			faces.Add( PlaneFace( new Vec3( -MathF.Sin( half ), -MathF.Cos( half ), 0 ), 0f, material ) );
		}
		faces.Add( PlaneFace( new Vec3( 0, 0, 1 ), hz, material ) );
		faces.Add( PlaneFace( new Vec3( 0, 0, -1 ), hz, material ) );
		return faces;
	}

	// ---- verb handlers -----------------------------------------------------------

	const string DefaultMaterial = "dev/dev_measuregeneric01";

	static bool MapBrushCreate( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_brush_create [name] shape=box|wedge|cylinder|sphere|arch|planes min=0,0,0 max=512,512,16 | center=0,0,0 size=64,64,64 | radius=64 height=128 segments=8 divisions=8 sweep=180 | faces=\"nx,ny,nz,d[,material];...\" [material=...] [detail=0|1] [id=...] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "shape", "min", "max", "center", "position", "size", "radius", "height", "segments", "divisions", "sweep", "faces", "material", "detail", "id" ) )
			return false;
		var shape = Opt( a, "shape", "box" ).ToLowerInvariant();
		var material = Opt( a, "material", DefaultMaterial );
		if ( string.IsNullOrWhiteSpace( material ) )
			return Fail( r, "map_brush_create: material must not be blank." );
		// Positional token is the brush id (and its name unless id= overrides).
		// Scripts name a brush on create and refer to that name in later verbs.
		var name = a.Positional.Count > 0 ? a.Positional[0] : "";
		var id = Opt( a, "id", "" );
		if ( id.Length == 0 )
			id = name.Length > 0 ? name : NextBrushId( a.Session );
		if ( name.Length == 0 )
			name = id;
		if ( a.Session.Doc.FindBrush( id ) is not null )
			return Fail( r, $"brush-duplicate-id: brush '{id}' already exists in map '{a.Session.Name}'." );

		MapBrushData data;
		switch ( shape )
		{
			case "box":
			{
				Vec3 min, max;
				if ( HasOpt( a, "min" ) || HasOpt( a, "max" ) )
				{
					min = ReqVec3( a, "min", usage );
					max = ReqVec3( a, "max", usage );
				}
				else if ( HasOpt( a, "size" ) )
				{
					var center = OptVec3( a, "position", OptVec3( a, "center", Vec3.Zero, usage ), usage );
					var size = ReqVec3( a, "size", usage );
					min = center - size * 0.5f;
					max = center + size * 0.5f;
				}
				else
					return Fail( r, $"map_brush_create: box needs min=/max= or center=/size=. usage: {usage}" );
				try
				{
					data = MapBrushData.Box( id, name, min, max, material );
				}
				catch ( ArgumentException e )
				{
					return Fail( r, $"brush-invalid: {e.Message}" );
				}
				break;
			}
			case "wedge":
			{
				var size = ReqVec3( a, "size", usage );
				var position = OptVec3( a, "position", OptVec3( a, "center", Vec3.Zero, usage ), usage );
				data = BrushDataFromBrush( FromPlanesTranslated( BuildWedge( size, material ), position ), id, name );
				break;
			}
			case "cylinder":
			{
				var radius = ReqFloat( a, "radius", usage );
				var height = ReqFloat( a, "height", usage );
				var segments = OptInt( a, "segments", 8, usage );
				var position = OptVec3( a, "position", OptVec3( a, "center", Vec3.Zero, usage ), usage );
				data = BrushDataFromBrush( FromPlanesTranslated( BuildCylinder( radius, height, segments, material ), position ), id, name );
				break;
			}
			case "sphere":
			{
				var radius = ReqFloat( a, "radius", usage );
				var divisions = OptInt( a, "divisions", 8, usage );
				var segments = OptInt( a, "segments", 8, usage );
				var position = OptVec3( a, "position", OptVec3( a, "center", Vec3.Zero, usage ), usage );
				data = BrushDataFromBrush( FromPlanesTranslated( BuildSphere( radius, divisions, segments, material ), position ), id, name );
				break;
			}
			case "arch":
			{
				var radius = ReqFloat( a, "radius", usage );
				var height = ReqFloat( a, "height", usage );
				var sweep = OptFloat( a, "sweep", 180f, usage );
				var segments = OptInt( a, "segments", 8, usage );
				var position = OptVec3( a, "position", OptVec3( a, "center", Vec3.Zero, usage ), usage );
				data = BrushDataFromBrush( FromPlanesTranslated( BuildArch( radius, height, sweep, segments, material ), position ), id, name );
				break;
			}
			case "planes":
			{
				var facesText = ReqOpt( a, "faces", usage );
				var faces = new List<MapBrushFace>();
				foreach ( var spec in facesText.Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
				{
					var parts = spec.Split( ',', StringSplitOptions.RemoveEmptyEntries );
					if ( parts.Length < 4 || parts.Length > 5 )
						return Fail( r, $"map_brush_create: bad face spec '{spec.Trim()}': need nx,ny,nz,distance[,material]." );
					if ( !TryNumber( parts[0], out var nx ) || !TryNumber( parts[1], out var ny ) || !TryNumber( parts[2], out var nz ) || !TryNumber( parts[3], out var d ) )
						return Fail( r, $"map_brush_create: bad face spec '{spec.Trim()}': numbers expected." );
					var normal = new Vec3( nx, ny, nz );
					if ( normal.LengthSquared < 1e-12f )
						return Fail( r, $"map_brush_create: bad face spec '{spec.Trim()}': zero normal." );
					var faceMaterial = parts.Length == 5 ? parts[4].Trim() : material;
					faces.Add( new MapBrushFace( normal.Normal, d, faceMaterial ) );
				}
				if ( faces.Count < 4 )
					return Fail( r, $"map_brush_create: planes shape needs >= 4 faces, got {faces.Count}." );
				data = BrushDataFromBrush( MapBrush.FromPlanes( faces ), id, name );
				break;
			}
			default:
				return Fail( r, $"map_brush_create: unknown shape '{shape}'. shapes: box, wedge, cylinder, sphere, arch, planes." );
		}

		var defects = data.Validate();
		if ( defects.Count > 0 )
			return Fail( r, $"brush-invalid: {string.Join( "; ", defects )}" );
		a.Session.Doc.AddBrush( data );
		if ( OptBool( a, "detail", false, usage ) )
			a.Session.DetailBrushes.Add( id );
		var volume = data.TryComputeVolume( out var vol ) ? FmtF( vol ) : "?";
		r.Log.Add( $"map_brush_create: {id} '{name}' {shape} {data.Faces.Count} faces volume {volume} material {material} (map '{a.Session.Name}')" );
		return true;
	}

	/// <summary>Rebuild a brush's faces, keeping its id, name, layer and visgroups.</summary>
	static void ReplaceBrushFaces( MapSession session, MapBrushData old, MapBrush newBrush )
	{
		var replacement = MapBrushData.FromMapBrush( newBrush, old.Id, old.Name );
		replacement.LayerId = old.LayerId;
		replacement.VisGroupIds = old.VisGroupIds.ToList();
		var defects = replacement.Validate();
		if ( defects.Count > 0 )
			throw new ArgumentException( $"brush-invalid: {string.Join( "; ", defects )}" );
		ReplaceBrush( session, replacement );
	}

	static bool MapBrushEdit( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_brush_edit brush=id op=move|translate|rotate|scale|snap|setmaterial [delta=x,y,z] [axis=x|y|z|x,y,z angle=deg] [factor=n|scale=x,y,z] [pivot=x,y,z] [grid=n] [material=...] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "brush", "op", "delta", "axis", "angle", "factor", "scale", "pivot", "grid", "material" ) )
			return false;
		var brushId = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "brush", "" );
		if ( brushId.Length == 0 )
			return Fail( r, $"map_brush_edit: needs a brush id. usage: {usage}" );
		var op = Opt( a, "op", "" ).ToLowerInvariant();
		if ( op.Length == 0 )
			return Fail( r, $"map_brush_edit: needs op=. usage: {usage}" );
		var old = ReqBrush( a, brushId );
		var doc = a.Session.Doc;

		switch ( op )
		{
			case "move":
			case "translate":
			{
				var delta = ReqVec3( a, "delta", usage );
				if ( !old.TryBounds( out var min, out var max ) )
					return Fail( r, $"brush-invalid: brush '{brushId}' has no computable bounds." );
				doc.MoveBrush( brushId, min + delta, max + delta );
				r.Log.Add( $"map_brush_edit: {brushId} moved by {Fmt( delta )} to {Fmt( min + delta )}..{Fmt( max + delta )}" );
				return true;
			}
			case "snap":
			{
				var grid = OptFloat( a, "grid", 1f, usage );
				if ( !( grid > 0f ) || !float.IsFinite( grid ) )
					return Fail( r, "map_brush_edit: grid must be positive and finite." );
				if ( !old.TryBounds( out var min, out var max ) )
					return Fail( r, $"brush-invalid: brush '{brushId}' has no computable bounds." );
				var snapped = new Vec3(
					MathF.Round( min.x / grid ) * grid,
					MathF.Round( min.y / grid ) * grid,
					MathF.Round( min.z / grid ) * grid );
				var delta = snapped - min;
				doc.MoveBrush( brushId, snapped, max + delta );
				r.Log.Add( $"map_brush_edit: {brushId} snapped to grid {FmtF( grid )} (delta {Fmt( delta )})" );
				return true;
			}
			case "setmaterial":
			{
				var material = ReqOpt( a, "material", usage );
				doc.SetBrushMaterial( brushId, material );
				r.Log.Add( $"map_brush_edit: {brushId} all faces -> {material}" );
				return true;
			}
			case "rotate":
			case "scale":
			{
				var pivot = OptVec3( a, "pivot", old.Centroid(), usage );
				Matrix4x4 matrix;
				if ( op == "rotate" )
				{
					var axisText = Opt( a, "axis", "z" ).ToLowerInvariant();
					var axis = axisText switch
					{
						"x" => new Vec3( 1, 0, 0 ),
						"y" => new Vec3( 0, 1, 0 ),
						"z" => new Vec3( 0, 0, 1 ),
						_ => ParseVec3( axisText, out var v ) ? v : throw new ArgumentException( $"map_brush_edit: axis '{axisText}' is not x, y, z or x,y,z." ),
					};
					if ( axis.LengthSquared < 1e-12f )
						return Fail( r, "map_brush_edit: rotate axis must not be zero." );
					var angle = ReqFloat( a, "angle", usage );
					matrix = MapTransform.RotationMatrix( N3( axis ), angle, N3( pivot ) );
				}
				else
				{
					Vec3 factors;
					if ( HasOpt( a, "factor" ) )
					{
						var f = ReqFloat( a, "factor", usage );
						factors = new Vec3( f, f, f );
					}
					else
						factors = ReqVec3( a, "scale", usage );
					if ( factors.x == 0 || factors.y == 0 || factors.z == 0 )
						return Fail( r, "map_brush_edit: scale factors must not be zero." );
					matrix = MapTransform.ScaleMatrix( N3( factors ), N3( pivot ) );
				}
				var brush = old.ToMapBrush();
				var planes = new Plane[brush.FaceCount];
				var mappings = new MapUvMapping[brush.FaceCount];
				var locks = new LockFlags[brush.FaceCount];
				for ( var i = 0; i < brush.FaceCount; i++ )
				{
					var face = brush.Faces[i];
					planes[i] = new Plane( N3( face.Normal ), -face.Distance );
					var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
					mappings[i] = new MapUvMapping( N3( uAxis ), N3( vAxis ),
						new Vector2( face.Material.Uv.Offset.x, face.Material.Uv.Offset.y ),
						new Vector2( face.Material.Uv.Scale.x, face.Material.Uv.Scale.y ),
						face.Material.Uv.RotationDegrees );
					locks[i] = face.Material.Uv.Locks;
				}
				var (newPlanes, newMappings) = MapTransform.TransformFaces( planes, mappings, locks, matrix, N3( pivot ) );
				var newFaces = new List<MapBrushFace>( brush.FaceCount );
				for ( var i = 0; i < brush.FaceCount; i++ )
				{
					var oldFace = brush.Faces[i];
					var plane = newPlanes[i];
					var mapping = newMappings[i];
					var normal = E3( plane.Normal ).Normal;
					var uv = new FaceUv(
						new Vec2( mapping.Offset.X, mapping.Offset.Y ),
						new Vec2( mapping.Scale.X, mapping.Scale.Y ),
						mapping.RotationDegrees,
						oldFace.Material.Uv.LightmapScale,
						oldFace.Material.Uv.SmoothingGroup,
						oldFace.Material.Uv.Locks );
					newFaces.Add( new MapBrushFace( normal, -plane.D, new FaceMaterial( oldFace.Material.MaterialName, uv ) ) );
				}
				ReplaceBrushFaces( a.Session, old, MapBrush.FromPlanes( newFaces ) );
				r.Log.Add( $"map_brush_edit: {brushId} {op} applied about pivot {Fmt( pivot )} (UV locks: {string.Join( ",", brush.Faces.Select( f => f.Material.Uv.Locks ) )} — None keeps UVs as-is)" );
				return true;
			}
			default:
				return Fail( r, $"map_brush_edit: unknown op '{op}'. ops: move, translate, rotate, scale, snap, setmaterial." );
		}
	}

	static bool MapBrushClip( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_brush_clip brush=id a=x,y,z b=x,y,z axis=x|y|z | normal=x,y,z distance=n [mode=cut|split] [keep=front|back] [cutmaterial=...] [cutnodraw=0|1] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "brush", "a", "b", "axis", "normal", "distance", "mode", "keep", "cutmaterial", "cutnodraw" ) )
			return false;
		var brushId = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "brush", "" );
		if ( brushId.Length == 0 )
			return Fail( r, $"map_brush_clip: needs a brush id. usage: {usage}" );
		var old = ReqBrush( a, brushId );
		var mode = Opt( a, "mode", "cut" ).ToLowerInvariant();
		if ( mode != "cut" && mode != "split" )
			return Fail( r, $"map_brush_clip: mode must be cut or split, got '{mode}'." );
		var keepFront = Opt( a, "keep", "front" ).ToLowerInvariant() switch
		{
			"front" => true,
			"back" => false,
			var other => throw new ArgumentException( $"map_brush_clip: keep must be front or back, got '{other}'." ),
		};
		var cutNoDraw = OptBool( a, "cutnodraw", true, usage );
		var cutMaterial = Opt( a, "cutmaterial", "" );
		if ( cutMaterial.Length == 0 && !cutNoDraw )
			cutMaterial = old.Faces.Count > 0 ? old.Faces[0].Material.MaterialName : DefaultMaterial;
		var options = new MapClipOptions
		{
			Mode = mode == "split" ? ClipMode.Split : ClipMode.Cut,
			KeepFront = keepFront,
			CutFaceNoDraw = cutNoDraw,
			CutFaceMaterial = cutMaterial,
		};
		MapClipResult result;
		var brush = old.ToMapBrush();
		if ( HasOpt( a, "normal" ) || HasOpt( a, "distance" ) )
		{
			var normal = ReqVec3( a, "normal", usage );
			var distance = ReqFloat( a, "distance", usage );
			if ( normal.LengthSquared < 1e-12f )
				return Fail( r, "map_brush_clip: clip normal must not be zero." );
			result = MapClip.Clip( brush, new ClipPlane( normal, distance ), options );
		}
		else
		{
			var pA = ReqVec3( a, "a", usage );
			var pB = ReqVec3( a, "b", usage );
			var axis = Opt( a, "axis", "z" ).ToLowerInvariant() switch
			{
				"x" => ClipViewAxis.X,
				"y" => ClipViewAxis.Y,
				"z" => ClipViewAxis.Z,
				var other => throw new ArgumentException( $"map_brush_clip: axis must be x, y or z, got '{other}'." ),
			};
			result = MapClip.Clip( brush, pA, pB, axis, options );
		}
		switch ( result.Outcome )
		{
			case ClipOutcome.Cut:
				ReplaceBrushFaces( a.Session, old, result.Kept );
				r.Log.Add( $"map_brush_clip: {brushId} cut ({result.Reason}) — kept {result.Kept.FaceCount} faces" );
				return true;
			case ClipOutcome.Split:
			{
				ReplaceBrushFaces( a.Session, old, result.Kept );
				var secondId = NextBrushId( a.Session );
				var second = MapBrushData.FromMapBrush( result.Second, secondId, secondId );
				second.LayerId = old.LayerId;
				second.VisGroupIds = old.VisGroupIds.ToList();
				a.Session.Doc.AddBrush( second );
				r.Log.Add( $"map_brush_clip: {brushId} split ({result.Reason}) — kept {result.Kept.FaceCount} faces, second piece is {secondId} ({result.Second.FaceCount} faces)" );
				return true;
			}
			case ClipOutcome.WholeBrushCutAway:
				a.Session.Doc.RemoveBrush( brushId );
				r.Log.Add( $"map_brush_clip: {brushId} whole-brush-cut-away — brush removed ({result.Reason})" );
				return true;
			case ClipOutcome.NoOpPlaneOutside:
			case ClipOutcome.NoOpPlaneCoplanar:
				// A named no-op outcome, not a silent success: logged with its name.
				r.Log.Add( $"map_brush_clip: {brushId} no-op {result.Outcome} ({result.Reason}) — brush unchanged" );
				return true;
			default:
				return Fail( r, $"map_brush_clip: {brushId} rejected ({result.Outcome}): {result.Reason}" );
		}
	}

	static bool MapBrushVertex( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_brush_vertex brush=id vertex=n to=x,y,z | delta=x,y,z [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "brush", "vertex", "to", "delta" ) )
			return false;
		var brushId = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "brush", "" );
		if ( brushId.Length == 0 )
			return Fail( r, $"map_brush_vertex: needs a brush id. usage: {usage}" );
		var vertex = ReqInt( a, "vertex", usage );
		var old = ReqBrush( a, brushId );
		var brush = old.ToMapBrush();
		if ( !brush.IsValid )
			return Fail( r, $"brush-invalid: brush '{brushId}': {string.Join( "; ", brush.Validate() )}" );
		var materialNames = new List<string>();
		var slotOf = new Dictionary<string, int>( StringComparer.Ordinal );
		int Slot( string name )
		{
			if ( !slotOf.TryGetValue( name, out var slot ) ) { slot = materialNames.Count; slotOf[name] = slot; materialNames.Add( name ); }
			return slot;
		}
		var mesh = brush.ToMesh( Slot );
		var vertexBrush = new MapVertexBrush(
			mesh.Positions.ToArray(),
			mesh.Faces.Select( f => (int[])f.Indices.Clone() ).ToArray(),
			mesh.Faces.Select( f => materialNames[f.Material] ).ToArray() );
		MapVertexEditResult moved;
		if ( HasOpt( a, "to" ) )
			moved = MapVertexEdit.MoveVertex( vertexBrush, vertex, ReqVec3( a, "to", usage ) );
		else if ( HasOpt( a, "delta" ) )
			moved = MapVertexEdit.MoveVertexBy( vertexBrush, vertex, ReqVec3( a, "delta", usage ) );
		else
			return Fail( r, $"map_brush_vertex: needs to= or delta=. usage: {usage}" );
		var edited = moved.Brush;
		var newFaces = edited.Faces.Select( ( loop, i ) =>
		{
			var (normal, distance) = edited.FacePlane( i );
			var oldUv = i < brush.FaceCount ? brush.Faces[i].Material.Uv : FaceUv.Default;
			return new MapBrushFace( normal, distance, new FaceMaterial( edited.Materials[i], oldUv ) );
		} ).ToList();
		ReplaceBrushFaces( a.Session, old, MapBrush.FromPlanes( newFaces ) );
		var defects = moved.Validity?.Defects ?? new List<string>();
		r.Log.Add( $"map_brush_vertex: {brushId} vertex {vertex} moved (affected faces {string.Join( ",", moved.AffectedFaces )}, valid={moved.IsValid}, defects: {( defects.Count == 0 ? "none" : string.Join( "; ", defects ) )})" );
		if ( !moved.IsValid )
			return Fail( r, $"brush-invalid: vertex move made brush '{brushId}' invalid: {string.Join( "; ", defects )}" );
		return true;
	}

	static bool MapDisplacementCreate( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_displacement_create brush=id face=n [power=2|3|4] | corners=\"x,y,z;x,y,z;x,y,z;x,y,z\" [materialid=0] [power=3] [id=...] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "brush", "face", "corners", "power", "materialid", "id" ) )
			return false;
		var power = OptInt( a, "power", 3, usage );
		if ( power < Map.Displacement.MinPower || power > Map.Displacement.MaxPower )
			return Fail( r, $"map_displacement_create: power must be {Map.Displacement.MinPower}..{Map.Displacement.MaxPower}, got {power}." );
		var materialId = OptInt( a, "materialid", 0, usage );
		var id = Opt( a, "id", "" );
		if ( id.Length == 0 )
			id = NextDispId( a.Session );
		if ( a.Session.Doc.Displacements.Any( d => d.Id == id ) )
			return Fail( r, $"displacement-duplicate-id: displacement '{id}' already exists in map '{a.Session.Name}'." );

		Map.Displacement disp;
		string brushId;
		int faceIndex;
		if ( HasOpt( a, "corners" ) )
		{
			var points = ParseVec3List( Opt( a, "corners" ), 4, "corners" );
			var face = Map.MapBrushFace.FromCorners( points.ToArray(), materialId );
			try
			{
				disp = Map.Displacement.Create( face, power );
			}
			catch ( ArgumentException e )
			{
				return Fail( r, $"displacement-invalid: {e.Message}" );
			}
			brushId = Opt( a, "brush", "" );
			faceIndex = OptInt( a, "face", -1, usage );
		}
		else
		{
			brushId = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "brush", "" );
			if ( brushId.Length == 0 )
				return Fail( r, $"map_displacement_create: needs brush= + face=, or corners=. usage: {usage}" );
			faceIndex = ReqInt( a, "face", usage );
			var brushData = ReqBrush( a, brushId );
			if ( faceIndex < 0 || faceIndex >= brushData.Faces.Count )
				return Fail( r, $"displacement-invalid: brush '{brushId}' has {brushData.Faces.Count} faces, face {faceIndex} is out of range." );
			if ( !brushData.TryComputeFaceVertices( faceIndex, out var corners ) || corners.Count < 4 )
				return Fail( r, $"displacement-invalid: face {faceIndex} of brush '{brushId}' has no computable quad corners." );
			var face = brushData.Faces[faceIndex].ToDisplacementFace( corners.Take( 4 ).ToArray(), materialId );
			try
			{
				disp = Map.Displacement.Create( face, power );
			}
			catch ( ArgumentException e )
			{
				return Fail( r, $"displacement-invalid: {e.Message}" );
			}
		}
		var data = MapDisplacementData.FromDisplacement( disp, id, brushId, faceIndex );
		a.Session.Doc.AddDisplacement( data );
		r.Log.Add( $"map_displacement_create: {id} on brush '{brushId}' face {faceIndex} power {power} grid {data.GridSize}x{data.GridSize} (materialid {materialId})" );
		return true;
	}

	static List<Vec3> ParseVec3List( string text, int expected, string what )
	{
		var parts = text.Split( ';', StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != expected )
			throw new ArgumentException( $"'{what}' needs {expected} ;-separated x,y,z points, got {parts.Length}." );
		var list = new List<Vec3>( expected );
		foreach ( var part in parts )
		{
			if ( !ParseVec3( part.Trim(), out var v ) )
				throw new ArgumentException( $"'{what}' point '{part.Trim()}' is not x,y,z." );
			list.Add( v );
		}
		return list;
	}

	static bool MapDisplacementSculpt( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_displacement_sculpt disp=id mode=raise|lower|smooth|flatten|paintalpha position=x,y,z [radius=64] [strength=0.5] [falloff=smooth|linear|sharp|constant] [raiseaxis=facenormal|x|y|z] [paintalpha=0..1] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "disp", "mode", "position", "radius", "strength", "falloff", "raiseaxis", "paintalpha" ) )
			return false;
		var dispId = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "disp", "" );
		if ( dispId.Length == 0 )
			return Fail( r, $"map_displacement_sculpt: needs a displacement id. usage: {usage}" );
		var modeText = Opt( a, "mode", "raise" ).ToLowerInvariant();
		var mode = modeText switch
		{
			"raise" => Map.DisplacementSculptMode.Raise,
			"lower" => Map.DisplacementSculptMode.Lower,
			"smooth" => Map.DisplacementSculptMode.Smooth,
			"flatten" => Map.DisplacementSculptMode.Flatten,
			"paintalpha" => Map.DisplacementSculptMode.PaintAlpha,
			_ => throw new ArgumentException( $"map_displacement_sculpt: unknown mode '{modeText}'. modes: raise, lower, smooth, flatten, paintalpha." ),
		};
		var position = ReqVec3( a, "position", usage );
		var radius = OptFloat( a, "radius", 64f, usage );
		var strength = OptFloat( a, "strength", 0.5f, usage );
		var falloff = Opt( a, "falloff", "smooth" ).ToLowerInvariant() switch
		{
			"smooth" => BrushFalloff.Smooth,
			"linear" => BrushFalloff.Linear,
			"sharp" => BrushFalloff.Sharp,
			"constant" => BrushFalloff.Constant,
			var other => throw new ArgumentException( $"map_displacement_sculpt: unknown falloff '{other}'. falloffs: smooth, linear, sharp, constant." ),
		};
		var raiseAxis = Opt( a, "raiseaxis", "facenormal" ).ToLowerInvariant() switch
		{
			"facenormal" => Map.DisplacementRaiseAxis.FaceNormal,
			"x" => Map.DisplacementRaiseAxis.X,
			"y" => Map.DisplacementRaiseAxis.Y,
			"z" => Map.DisplacementRaiseAxis.Z,
			var other => throw new ArgumentException( $"map_displacement_sculpt: unknown raiseaxis '{other}'. axes: facenormal, x, y, z." ),
		};
		var paintAlpha = OptFloat( a, "paintalpha", 1f, usage );
		if ( paintAlpha < 0f || paintAlpha > 1f )
			return Fail( r, "map_displacement_sculpt: paintalpha must be 0..1." );

		var data = ReqDisp( a, dispId );
		if ( string.IsNullOrWhiteSpace( data.BrushId ) || data.FaceIndex < 0 )
			return Fail( r, $"displacement-invalid: displacement '{dispId}' is not tied to a brush face (corners-only); sculpt needs a brush face." );
		var brushData = ReqBrush( a, data.BrushId );
		if ( data.FaceIndex >= brushData.Faces.Count )
			return Fail( r, $"displacement-invalid: displacement '{dispId}' face {data.FaceIndex} is out of range on brush '{data.BrushId}'." );
		if ( !brushData.TryComputeFaceVertices( data.FaceIndex, out var corners ) || corners.Count < 4 )
			return Fail( r, $"displacement-invalid: face {data.FaceIndex} of brush '{data.BrushId}' has no computable quad corners." );
		var face = brushData.Faces[data.FaceIndex].ToDisplacementFace( corners.Take( 4 ).ToArray(), 0 );
		var disp = data.ToDisplacement( face );
		var normal = brushData.Faces[data.FaceIndex].Normal;
		var sample = new BrushSample( position, normal, radius, strength );
		var stamp = new BrushAlpha { Values = new[] { 1f }, Width = 1, Height = 1 };
		var options = new Map.DisplacementSculptOptions { RaiseAxis = raiseAxis, PaintAlphaTarget = paintAlpha };
		var result = Map.DisplacementSculpt.ApplyDab( disp, sample, mode, falloff, stamp, options );
		var updated = MapDisplacementData.FromDisplacement( disp, data.Id, data.BrushId, data.FaceIndex );
		ReplaceDisplacement( a.Session, updated );
		r.Log.Add( $"map_displacement_sculpt: {dispId} {modeText} at {Fmt( position )} radius {FmtF( radius )} strength {FmtF( strength )} — {result.VerticesTouched} vertices touched, max offset delta {FmtF( result.MaxOffsetDelta )}, max alpha delta {FmtF( result.MaxAlphaDelta )}" );
		return true;
	}

	static bool MapFaceMaterial( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_face_material brush=id face=n|all material=... [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "brush", "face", "material" ) )
			return false;
		var brushId = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "brush", "" );
		if ( brushId.Length == 0 )
			return Fail( r, $"map_face_material: needs a brush id. usage: {usage}" );
		var material = ReqOpt( a, "material", usage );
		var faceText = Opt( a, "face", "all" );
		var old = ReqBrush( a, brushId );
		if ( faceText.Equals( "all", StringComparison.OrdinalIgnoreCase ) )
		{
			a.Session.Doc.SetBrushMaterial( brushId, material );
			r.Log.Add( $"map_face_material: {brushId} all {old.Faces.Count} faces -> {material}" );
			return true;
		}
		if ( !int.TryParse( faceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var faceIndex ) )
			return Fail( r, $"map_face_material: face '{faceText}' is not a number or 'all'." );
		if ( faceIndex < 0 || faceIndex >= old.Faces.Count )
			return Fail( r, $"face-out-of-range: brush '{brushId}' has {old.Faces.Count} faces, face {faceIndex} is out of range." );
		var brush = old.ToMapBrush();
		var faces = brush.Faces.Select( ( f, i ) => i == faceIndex ? new MapBrushFace( f.Normal, f.Distance, f.Material.WithMaterial( material ) ) : f ).ToList();
		ReplaceBrushFaces( a.Session, old, MapBrush.FromPlanes( faces ) );
		r.Log.Add( $"map_face_material: {brushId} face {faceIndex} -> {material}" );
		return true;
	}

	static bool MapFaceUv( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_face_uv brush=id face=n|all [offset=u,v] [scale=u,v] [rotation=deg] [lightmapscale=n] [smoothing=n] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "brush", "face", "offset", "scale", "rotation", "lightmapscale", "smoothing" ) )
			return false;
		var brushId = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "brush", "" );
		if ( brushId.Length == 0 )
			return Fail( r, $"map_face_uv: needs a brush id. usage: {usage}" );
		var faceText = Opt( a, "face", "all" );
		if ( !HasOpt( a, "offset" ) && !HasOpt( a, "scale" ) && !HasOpt( a, "rotation" ) && !HasOpt( a, "lightmapscale" ) && !HasOpt( a, "smoothing" ) )
			return Fail( r, $"map_face_uv: nothing to change — give at least one of offset=, scale=, rotation=, lightmapscale=, smoothing=. usage: {usage}" );
		var old = ReqBrush( a, brushId );
		var newUvFor = new Func<FaceUv, FaceUv>( uv =>
		{
			var offset = HasOpt( a, "offset" ) ? ReqVec2( a, "offset", usage ) : uv.Offset;
			var scale = HasOpt( a, "scale" ) ? ReqVec2( a, "scale", usage ) : uv.Scale;
			var rotation = HasOpt( a, "rotation" ) ? ReqFloat( a, "rotation", usage ) : uv.RotationDegrees;
			var lightmap = HasOpt( a, "lightmapscale" ) ? ReqFloat( a, "lightmapscale", usage ) : uv.LightmapScale;
			var smoothing = HasOpt( a, "smoothing" ) ? ReqInt( a, "smoothing", usage ) : uv.SmoothingGroup;
			return new FaceUv( offset, scale, rotation, lightmap, smoothing, uv.Locks );
		} );
		var brush = old.ToMapBrush();
		List<MapBrushFace> faces;
		if ( faceText.Equals( "all", StringComparison.OrdinalIgnoreCase ) )
		{
			faces = brush.Faces.Select( f => new MapBrushFace( f.Normal, f.Distance, f.Material.WithUv( newUvFor( f.Material.Uv ) ) ) ).ToList();
		}
		else
		{
			if ( !int.TryParse( faceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var faceIndex ) )
				return Fail( r, $"map_face_uv: face '{faceText}' is not a number or 'all'." );
			if ( faceIndex < 0 || faceIndex >= old.Faces.Count )
				return Fail( r, $"face-out-of-range: brush '{brushId}' has {old.Faces.Count} faces, face {faceIndex} is out of range." );
			faces = brush.Faces.Select( ( f, i ) => i == faceIndex ? new MapBrushFace( f.Normal, f.Distance, f.Material.WithUv( newUvFor( f.Material.Uv ) ) ) : f ).ToList();
		}
		ReplaceBrushFaces( a.Session, old, MapBrush.FromPlanes( faces ) );
		r.Log.Add( $"map_face_uv: {brushId} face {faceText} UV updated" );
		return true;
	}

	static bool MapEntityPlace( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_entity_place class=info_player_start origin=x,y,z [angles=x,y,z] [targetname=...] [brush=id1,id2] [key=value ...] [map=name] [registry=path]";
		var a = ParseMapArgs( args, baseDir, usage );
		// Entity keyvalues are open-ended: only the structural keys are reserved.
		var className = Opt( a, "class", Opt( a, "classname", "" ) );
		if ( className.Length == 0 )
			return Fail( r, $"map_entity_place: needs class=. usage: {usage}" );
		var origin = OptVec3( a, "origin", Vec3.Zero, usage );
		var angles = OptVec3( a, "angles", Vec3.Zero, usage );
		var brushIds = Opt( a, "brush", "" ).Split( ',', StringSplitOptions.RemoveEmptyEntries ).Select( s => s.Trim() ).ToList();
		var keyvalues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var kv in a.Opts )
		{
			if ( kv.Key is "map" or "registry" or "class" or "classname" or "origin" or "angles" or "brush" )
				continue;
			keyvalues[kv.Key] = kv.Value;
		}
		var registry = RegistryFor( a );
		var entities = new MapEntities( registry );
		try
		{
			if ( brushIds.Count > 0 )
			{
				var brushes = new List<MapBrush>();
				foreach ( var bid in brushIds )
					brushes.Add( ReqBrush( a, bid ).ToMapBrush() );
				entities.PlaceBrushEntity( className, brushes, keyvalues, origin );
			}
			else
			{
				entities.PlacePointEntity( className, origin, keyvalues, angles );
			}
		}
		catch ( MapEntityException e )
		{
			return Fail( r, $"entity-invalid ({e.Code}): {e.Message}" );
		}
		var id = NextEntityId( a.Session );
		var data = new MapEntityData
		{
			Id = id,
			ClassName = className,
			Origin = origin,
			Angles = angles,
			BrushIds = brushIds,
		};
		foreach ( var kv in keyvalues )
			data.KeyValues[kv.Key] = kv.Value;
		a.Session.Doc.AddEntity( data );
		r.Log.Add( $"map_entity_place: {id} {className} at {Fmt( origin )} (map '{a.Session.Name}', {LogRegistrySource( a )})" );
		return true;
	}

	static bool MapEntityEdit( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_entity_edit entity=id|targetname [origin=x,y,z] [angles=x,y,z] [remove=key] [key=value ...] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		var entityId = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "entity", "" );
		if ( entityId.Length == 0 )
			return Fail( r, $"map_entity_edit: needs an entity id or targetname. usage: {usage}" );
		var entity = ReqEntity( a, entityId );
		var doc = a.Session.Doc;
		var changes = new List<string>();
		if ( HasOpt( a, "origin" ) )
		{
			var origin = ReqVec3( a, "origin", usage );
			doc.SetEntityProperty( entity.Id, "origin", FmtSp( origin ) );
			changes.Add( $"origin={Fmt( origin )}" );
		}
		if ( HasOpt( a, "angles" ) )
		{
			var angles = ReqVec3( a, "angles", usage );
			doc.SetEntityProperty( entity.Id, "angles", FmtSp( angles ) );
			changes.Add( $"angles={Fmt( angles )}" );
		}
		if ( HasOpt( a, "remove" ) )
		{
			var key = Opt( a, "remove" );
			doc.RemoveEntityProperty( entity.Id, key );
			changes.Add( $"removed {key}" );
		}
		foreach ( var kv in a.Opts )
		{
			if ( kv.Key is "map" or "entity" or "origin" or "angles" or "remove" )
				continue;
			doc.SetEntityProperty( entity.Id, kv.Key, kv.Value );
			changes.Add( $"{kv.Key}={kv.Value}" );
		}
		if ( changes.Count == 0 )
			return Fail( r, $"map_entity_edit: nothing to change on '{entity.Id}'. usage: {usage}" );
		r.Log.Add( $"map_entity_edit: {entity.Id} {entity.ClassName}: {string.Join( ", ", changes )}" );
		return true;
	}

	static bool MapEntityIoAdd( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_entity_io_add source=id|targetname event=OnTrigger target=name|!special input=Open [parameter=...] [delay=0] [maxfires=-1] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "source", "event", "target", "input", "parameter", "delay", "maxfires" ) )
			return false;
		var sourceRef = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "source", "" );
		if ( sourceRef.Length == 0 )
			return Fail( r, $"map_entity_io_add: needs source=. usage: {usage}" );
		var eventName = ReqOpt( a, "event", usage );
		var target = ReqOpt( a, "target", usage );
		var input = ReqOpt( a, "input", usage );
		var parameter = Opt( a, "parameter", "" );
		var delay = OptFloat( a, "delay", 0f, usage );
		var maxFires = OptInt( a, "maxfires", -1, usage );
		var source = ReqEntity( a, sourceRef );
		// The target must resolve to a targetname in the doc, or be a !-special
		// (e.g. !player, !activator) — otherwise the row would be dead on arrival.
		if ( !target.StartsWith( "!" ) && a.Session.Doc.FindEntityByTargetName( target ) is null )
			return Fail( r, $"io-target-not-found: no entity with targetname '{target}' in map '{a.Session.Name}' (use !special for engine targets)." );
		EntityIoOutput output;
		try
		{
			output = new EntityIoOutput( source.Id, eventName, target, input, delay, maxFires, parameter );
		}
		catch ( ArgumentException e )
		{
			return Fail( r, $"io-invalid: {e.Message}" );
		}
		var error = a.Session.Io.Add( output );
		if ( error is not null )
			return Fail( r, $"io-invalid: {error}" );
		r.Log.Add( $"map_entity_io_add: {source.ClassName} '{source.TargetName}' ({source.Id}) {eventName} -> {target} {input} delay {FmtF( delay )} maxfires {maxFires} (session I/O; see header note on doc.AddIo)" );
		return true;
	}

	static bool MapGroup( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_group op=define|link|editmember|unlink|list name=... brush=id1,id2 entity=id1,id2 [definition=id] [offset=x,y,z] [member=id] [edit=move|setmaterial|setkeyvalue|rename] [delta=x,y,z] [value=...] [key=...] [layer=...] [visgroup=...] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "op", "name", "brush", "entity", "definition", "offset", "member", "edit", "delta", "value", "key", "layer", "visgroup" ) )
			return false;
		var op = Opt( a, "op", "list" ).ToLowerInvariant();
		var groups = a.Session.Groups;
		switch ( op )
		{
			case "list":
			{
				r.Log.Add( $"map_group: {groups.Definitions.Count} definitions, {groups.Instances.Count} instances in map '{a.Session.Name}'" );
				foreach ( var d in groups.Definitions )
					r.Log.Add( $"  definition {d.Id} '{d.Name}' {d.Members.Count} members layer '{d.Layer}' visgroup '{d.VisGroup}'" );
				return true;
			}
			case "define":
			{
				var name = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "name", "" );
				if ( name.Length == 0 )
					return Fail( r, $"map_group: define needs name=. usage: {usage}" );
				var members = new List<Map.LinkedGroupMember>();
				foreach ( var bid in Opt( a, "brush", "" ).Split( ',', StringSplitOptions.RemoveEmptyEntries ).Select( s => s.Trim() ) )
				{
					var b = ReqBrush( a, bid );
					var member = new Map.LinkedGroupMember
					{
						Id = b.Id,
						Kind = Map.LinkedGroupMemberKind.Brush,
						Name = b.Name,
						Material = b.Faces.Count > 0 ? b.Faces[0].Material.MaterialName : "",
					};
					if ( b.TryComputeVertices( out var verts ) )
						member.Vertices.AddRange( verts );
					members.Add( member );
				}
				foreach ( var eid in Opt( a, "entity", "" ).Split( ',', StringSplitOptions.RemoveEmptyEntries ).Select( s => s.Trim() ) )
				{
					var e = ReqEntity( a, eid );
					var member = new Map.LinkedGroupMember
					{
						Id = e.Id,
						Kind = Map.LinkedGroupMemberKind.Entity,
						Name = e.TargetName.Length > 0 ? e.TargetName : e.ClassName,
						Origin = e.Origin,
					};
					foreach ( var kv in e.KeyValues )
						member.KeyValues[kv.Key] = kv.Value;
					members.Add( member );
				}
				if ( members.Count == 0 )
					return Fail( r, "map_group: define needs at least one brush= or entity= member." );
				var result = groups.DefineLinkedGroup( name, members, Opt( a, "layer", "" ), Opt( a, "visgroup", "" ) );
				if ( !result.Ok )
					return Fail( r, $"group-define-failed ({result.Reason}): {result.Message}" );
				r.Log.Add( $"map_group: definition {result.Definition!.Id} '{name}' with {members.Count} members" );
				return true;
			}
			case "link":
			{
				var definitionId = Opt( a, "definition", "" );
				if ( definitionId.Length == 0 )
					return Fail( r, $"map_group: link needs definition=. usage: {usage}" );
				var offset = ReqVec3( a, "offset", usage );
				var result = groups.LinkCopy( definitionId, offset, Opt( a, "layer", "" ), Opt( a, "visgroup", "" ) );
				if ( !result.Ok )
					return Fail( r, $"group-link-failed ({result.Reason}): {result.Message}" );
				r.Log.Add( $"map_group: linked copy instance {result.Instance!.Id} of definition {definitionId} at offset {Fmt( offset )} (group-set only; not added to the document)" );
				return true;
			}
			case "editmember":
			{
				var definitionId = Opt( a, "definition", "" );
				var memberId = Opt( a, "member", "" );
				if ( definitionId.Length == 0 || memberId.Length == 0 )
					return Fail( r, $"map_group: editmember needs definition= and member=. usage: {usage}" );
				var editKind = Opt( a, "edit", "" ).ToLowerInvariant();
				Map.LinkedMemberEdit edit = editKind switch
				{
					"move" => Map.LinkedMemberEdit.Move( ReqVec3( a, "delta", usage ) ),
					"setmaterial" => Map.LinkedMemberEdit.SetMaterial( ReqOpt( a, "value", usage ) ),
					"setkeyvalue" => Map.LinkedMemberEdit.SetKeyValue( ReqOpt( a, "key", usage ), ReqOpt( a, "value", usage ) ),
					"rename" => Map.LinkedMemberEdit.Rename( ReqOpt( a, "value", usage ) ),
					_ => throw new ArgumentException( $"map_group: unknown edit '{editKind}'. edits: move, setmaterial, setkeyvalue, rename." ),
				};
				var result = groups.EditMember( definitionId, memberId, edit );
				if ( !result.Ok )
					return Fail( r, $"group-edit-failed ({result.Reason}): {result.Message}" );
				r.Log.Add( $"map_group: definition {definitionId} member {memberId} {editKind} applied to {result.AffectedInstances} instances ({string.Join( ",", result.AffectedInstanceIds )})" );
				return true;
			}
			case "unlink":
			{
				var instanceId = Opt( a, "instance", "" );
				if ( instanceId.Length == 0 )
					return Fail( r, $"map_group: unlink needs instance=. usage: {usage}" );
				var result = groups.Unlink( instanceId );
				if ( !result.Ok )
					return Fail( r, $"group-unlink-failed ({result.Reason}): {result.Message}" );
				r.Log.Add( $"map_group: instance {instanceId} unlinked to independent group {result.Group!.Id}" );
				return true;
			}
			default:
				return Fail( r, $"map_group: unknown op '{op}'. ops: define, link, editmember, unlink, list." );
		}
	}

	static bool MapVisGroup( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_visgroup op=define|setvisible|assign|list name=... [color=#rrggbb|\"r g b\"] [visible=0|1] [brush=id1,id2] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "op", "name", "color", "visible", "brush" ) )
			return false;
		var op = Opt( a, "op", "list" ).ToLowerInvariant();
		var doc = a.Session.Doc;
		switch ( op )
		{
			case "list":
			{
				r.Log.Add( $"map_visgroup: {doc.VisGroups.Count} visgroups in map '{a.Session.Name}'" );
				foreach ( var vg in doc.VisGroups )
					r.Log.Add( $"  visgroup {vg.Id} '{vg.Name}' {vg.Colour} visible={vg.Visible} compile={vg.CompileVisible} ({doc.Brushes.Count( b => b.VisGroupIds.Contains( vg.Id ) )} brushes)" );
				return true;
			}
			case "define":
			{
				var name = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "name", "" );
				if ( name.Length == 0 )
					return Fail( r, $"map_visgroup: define needs name=. usage: {usage}" );
				var color = Opt( a, "color", "#808080" );
				var colour = color.Contains( ' ' ) ? RgbToHex( color ) : color;
				var id = $"vis-{doc.VisGroups.Count + 1}";
				if ( doc.FindVisGroup( id ) is not null )
					id = $"vis-{Guid.NewGuid().ToString( "N" )[..8]}";
				doc.AddVisGroup( new MapVisGroup { Id = id, Name = name, Colour = colour, Visible = true, CompileVisible = true } );
				r.Log.Add( $"map_visgroup: defined {id} '{name}' {colour}" );
				return true;
			}
			case "setvisible":
			{
				var name = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "name", "" );
				if ( name.Length == 0 )
					return Fail( r, $"map_visgroup: setvisible needs name=. usage: {usage}" );
				var vg = doc.FindVisGroup( name ) ?? doc.VisGroups.FirstOrDefault( v => v.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) );
				if ( vg is null )
					return Fail( r, $"visgroup-not-found: no visgroup '{name}' in map '{a.Session.Name}'." );
				var visible = OptBool( a, "visible", true, usage );
				doc.SetVisGroupVisible( vg.Id, visible );
				r.Log.Add( $"map_visgroup: {vg.Id} '{vg.Name}' visible={visible}" );
				return true;
			}
			case "assign":
			{
				var name = a.Positional.Count > 0 ? a.Positional[0] : Opt( a, "name", "" );
				if ( name.Length == 0 )
					return Fail( r, $"map_visgroup: assign needs name=. usage: {usage}" );
				var vg = doc.FindVisGroup( name ) ?? doc.VisGroups.FirstOrDefault( v => v.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) );
				if ( vg is null )
					return Fail( r, $"visgroup-not-found: no visgroup '{name}' in map '{a.Session.Name}'." );
				var brushIds = Opt( a, "brush", "" ).Split( ',', StringSplitOptions.RemoveEmptyEntries ).Select( s => s.Trim() ).ToList();
				if ( brushIds.Count == 0 )
					return Fail( r, $"map_visgroup: assign needs brush=. usage: {usage}" );
				foreach ( var bid in brushIds )
				{
					var old = ReqBrush( a, bid );
					if ( old.VisGroupIds.Contains( vg.Id ) )
						continue;
					var replacement = old.Clone();
					replacement.VisGroupIds.Add( vg.Id );
					ReplaceBrush( a.Session, replacement );
				}
				r.Log.Add( $"map_visgroup: {brushIds.Count} brushes assigned to {vg.Id} '{vg.Name}'" );
				return true;
			}
			default:
				return Fail( r, $"map_visgroup: unknown op '{op}'. ops: define, setvisible, assign, list." );
		}
	}

	static bool MapImport( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_import format=vmf|map|gmap file=path | text=\"...\" [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "format", "file", "text" ) )
			return false;
		var format = Opt( a, "format", "" ).ToLowerInvariant();
		if ( format is not ("vmf" or "map" or "gmap") )
			return Fail( r, $"map_import: format must be vmf, map or gmap, got '{format}'. usage: {usage}" );
		string text;
		string source;
		if ( HasOpt( a, "text" ) )
		{
			text = Opt( a, "text" );
			source = "inline text";
		}
		else if ( HasOpt( a, "file" ) )
		{
			var path = Resolve( baseDir, Opt( a, "file" ) );
			if ( !File.Exists( path ) )
				return Fail( r, $"import-file-not-found: no file at '{path}'." );
			text = File.ReadAllText( path );
			source = path;
		}
		else
			return Fail( r, $"map_import: needs file= or text=. usage: {usage}" );

		MapDocument doc;
		var warnings = new List<string>();
		try
		{
			switch ( format )
			{
				case "vmf":
					doc = ImportVmfDocument( Map.VmfIo.Parse( text ), warnings );
					break;
				case "map":
					doc = ImportVmfDocument( Map.Map220Io.Parse( text ), warnings );
					break;
				default: // gmap
					doc = MapDocumentIo.Read( text );
					break;
			}
		}
		catch ( Exception e )
		{
			return Fail( r, $"import-parse-failed ({format}): {e.Message}" );
		}
		// Import replaces the named map: fresh doc, fresh groups/io/navmesh/cubemap.
		ResetSession( a.Session, doc );
		r.Log.Add( $"map_import: {format} from {source} -> {doc.Brushes.Count} brushes, {doc.Entities.Count} entities, {doc.Displacements.Count} displacements, {doc.VisGroups.Count} visgroups (map '{a.Session.Name}')" );
		foreach ( var w in warnings )
			r.Log.Add( $"  import warning: {w}" );
		return true;
	}

	static bool MapExport( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_export format=vmf|map|gmap file=path [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "format", "file" ) )
			return false;
		var format = Opt( a, "format", "" ).ToLowerInvariant();
		if ( format is not ("vmf" or "map" or "gmap") )
			return Fail( r, $"map_export: format must be vmf, map or gmap, got '{format}'. usage: {usage}" );
		var path = Resolve( baseDir, ReqOpt( a, "file", usage ) );
		var vmf = ToVmfDocument( a.Session );
		string text;
		var warnings = new List<string>();
		switch ( format )
		{
			case "vmf":
				text = Map.VmfIo.Write( vmf );
				break;
			case "map":
				text = Map.Map220Io.Write( vmf, out warnings );
				break;
			default:
				text = MapDocumentIo.Write( a.Session.Doc );
				break;
		}
		var dir = Path.GetDirectoryName( path );
		if ( !string.IsNullOrEmpty( dir ) )
			Directory.CreateDirectory( dir );
		File.WriteAllText( path, text );
		r.Written.Add( path );
		r.Log.Add( $"map_export: {format} -> {path} ({text.Length} bytes, {a.Session.Doc.Brushes.Count} brushes, {a.Session.Doc.Entities.Count} entities)" );
		foreach ( var w in warnings )
			r.Log.Add( $"  export warning: {w}" );
		return true;
	}

	static bool MapCompile( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_compile preset=quick|preview|final|quickplay [out=folder] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "preset", "out" ) )
			return false;
		var presetText = Opt( a, "preset", "quickplay" ).ToLowerInvariant();
		var session = a.Session;
		if ( session.Doc.Brushes.Count == 0 )
			return Fail( r, $"compile-empty-map: map '{session.Name}' has no brushes; build something first." );

		if ( presetText == "quickplay" )
		{
			QuickPlayBakeResult bake;
			try
			{
				bake = QuickPlay.Bake( ToQuickPlayMap( session ) );
			}
			catch ( Exception e )
			{
				return Fail( r, $"quickplay-failed: {e.Message}" );
			}
			session.LastQuickPlay = bake;
			if ( !bake.Sealed )
			{
				var leak = bake.LeakPath is not null && bake.LeakPath.Count > 0
					? $" leak path: {string.Join( " -> ", bake.LeakPath.Select( Fmt ) )}"
					: "";
				return Fail( r, $"quickplay-unsealed: the map leaks or has no sealed hull.{leak}" );
			}
			if ( bake.Spawn is not null && !bake.Spawn.Ok )
				return Fail( r, $"quickplay-no-spawn: {bake.Spawn.Reason}" );
			r.Log.Add( $"map_compile: quickplay sealed ({bake.EmptyLeafCount} empty / {bake.SolidLeafCount} solid leaves, {bake.PortalCount} portals, {bake.CollisionHullCount} hulls, spawn ok={bake.Spawn?.Ok}, walk blocked={bake.Walk?.Blocked})" );
			foreach ( var w in bake.Warnings )
				r.Log.Add( $"  quickplay warning: {w}" );
			return true;
		}

		var preset = presetText switch
		{
			"quick" => CompilePreset.QuickPlay,
			"preview" => CompilePreset.Preview,
			"final" => CompilePreset.Final,
			_ => throw new ArgumentException( $"map_compile: unknown preset '{presetText}'. presets: quick, preview, final, quickplay." ),
		};
		var options = new CompileOptions();
		if ( HasOpt( a, "out" ) )
			options.OutputFolder = Resolve( baseDir, Opt( a, "out" ) );
		CompileResult result;
		try
		{
			result = CompileRunner.Run( CompileInput.FromDocument( session.Doc ), preset, options );
		}
		catch ( Exception e )
		{
			return Fail( r, $"compile-failed: {e.Message}" );
		}
		session.LastCompile = result;
		if ( !result.Succeeded )
			return Fail( r, $"compile-failed ({presetText}, stage {result.FailedStage}): {result.Error ?? "no error message"}" );
		r.Log.Add( $"map_compile: {presetText} succeeded (stages: {string.Join( ",", result.CompletedStages )}, {result.OutputFiles.Count} output files)" );
		foreach ( var f in result.OutputFiles )
			r.Log.Add( $"  output: {f}" );
		if ( result.Leak is not null )
			r.Log.Add( $"  leak: {result.Leak}" );
		foreach ( var issue in result.Issues )
			r.Log.Add( $"  compile issue: {issue}" );
		return true;
	}

	static bool MapValidate( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_validate [map=name] [registry=path]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "registry" ) )
			return false;
		var session = a.Session;
		var registry = RegistryFor( a );
		var messages = new List<string>();

		// Primary: the full MapCheck over the check projection.
		var report = MapCheck.RunFullCheck( ToCheckDocument( session ) );
		foreach ( var issue in report.Issues )
			messages.Add( $"[{issue.Severity}] {issue.Type}: {issue.Message}" + ( issue.FixHint.Length > 0 ? $" (fix: {issue.FixHint})" : "" ) );

		// Live issue engine over the doc's own projection.
		var engine = new Map.IssueEngine();
		foreach ( var issue in engine.Run( session.Doc.ToIssueDocument() ) )
		{
			var line = $"[{issue.Severity}] {issue.Code}: {issue.Message}" + ( issue.FixHint.Length > 0 ? $" (fix: {issue.FixHint})" : "" );
			if ( !messages.Contains( line ) )
				messages.Add( line );
		}

		// Structural + registry validation of the doc itself.
		foreach ( var defect in session.Doc.Validate( registry ) )
		{
			var line = $"[Error] doc-validate: {defect}";
			if ( !messages.Contains( line ) )
				messages.Add( line );
		}

		var errors = messages.Count( m => m.StartsWith( "[Error]", StringComparison.Ordinal ) );
		r.Log.Add( $"map_validate: {session.Doc.Brushes.Count} brushes, {session.Doc.Entities.Count} entities, {session.Doc.Displacements.Count} displacements — {messages.Count} issues ({errors} errors) ({LogRegistrySource( a )})" );
		foreach ( var m in messages )
			r.Log.Add( $"  {m}" );
		// Validation reports; it does not fail the script. A leaky room can still
		// proceed to map_compile preset=quickplay in the same script and fail there
		// with the named quickplay-unsealed error.
		return true;
	}

	static bool MapLightmapBake( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_lightmap_bake [export=none|obj] [file=basename] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "export", "file" ) )
			return false;
		var export = Opt( a, "export", "none" ).ToLowerInvariant();
		if ( export is "glb" or "both" )
			return Fail( r, "glb-export-stub: MapBakeExport.ExportGlb writes a minimal GLB (header + asset JSON, no mesh payload) until the core GlbExport wiring lands — use export=obj." );
		if ( export is not ("none" or "obj") )
			return Fail( r, $"map_lightmap_bake: export must be none or obj, got '{export}'." );
		var session = a.Session;
		var brushes = new List<MapBakeBrush>();
		foreach ( var b in session.Doc.Brushes )
			brushes.Add( MapBakeBrush.FromMapBrush( b.ToMapBrush(), b.Name ) );
		var displacements = new List<MapBakeDisplacement>();
		foreach ( var d in session.Doc.Displacements )
		{
			var brush = session.Doc.FindBrush( d.BrushId );
			if ( brush is null || d.FaceIndex < 0 || d.FaceIndex >= brush.Faces.Count )
				continue;
			if ( !brush.TryComputeFaceVertices( d.FaceIndex, out var corners ) || corners.Count < 4 )
				continue;
			var face = brush.Faces[d.FaceIndex].ToDisplacementFace( corners.Take( 4 ).ToArray(), 0 );
			var material = brush.Faces[d.FaceIndex].Material;
			displacements.Add( MapBakeDisplacement.FromDisplacement( d.ToDisplacement( face ), material, material, d.Id ) );
		}
		MapBakeMesh baked;
		try
		{
			baked = MapBakeExport.Bake( new MapBakeDocument( session.Doc.Name, brushes, displacements ) );
		}
		catch ( Exception e )
		{
			return Fail( r, $"lightmap-bake-failed: {e.Message}" );
		}
		r.Log.Add( $"map_lightmap_bake: {baked.FaceCount} faces, {baked.VertexCount} vertices, {baked.Materials.Count} materials ({baked.BrushFaceCount} brush faces, {baked.DisplacementFaceCount} displacement faces, {baked.ExcludedNoDrawFaces} nodraw + {baked.ExcludedDegenerateFaces} degenerate excluded)" );
		foreach ( var issue in baked.Issues )
			r.Log.Add( $"  bake issue: {issue}" );
		if ( export == "obj" )
		{
			var baseName = Opt( a, "file", session.Doc.Name );
			var objPath = Resolve( baseDir, baseName + ".obj" );
			var objDir = Path.GetDirectoryName( objPath );
			if ( !string.IsNullOrEmpty( objDir ) )
				Directory.CreateDirectory( objDir );
			var result = MapBakeExport.ExportObj( baked, objPath );
			r.Written.Add( objPath );
			r.Log.Add( $"  obj exported: {objPath} (written={result.ObjWritten})" );
		}
		return true;
	}

	static bool MapNavmesh( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_navmesh [cellsize=8] [agentradius=16] [agentheight=72] [stepheight=18] [maxdrop=72] [maxslope=45] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "cellsize", "agentradius", "agentheight", "stepheight", "maxdrop", "maxslope" ) )
			return false;
		var options = new Map.NavMeshGenOptions
		{
			CellSize = OptFloat( a, "cellsize", 8f, usage ),
			AgentRadius = OptFloat( a, "agentradius", 16f, usage ),
			AgentHeight = OptFloat( a, "agentheight", 72f, usage ),
			StepHeight = OptFloat( a, "stepheight", 18f, usage ),
			MaxDrop = OptFloat( a, "maxdrop", 72f, usage ),
			MaxSlopeDegrees = OptFloat( a, "maxslope", 45f, usage ),
			MapName = a.Session.Name,
		};
		Map.NavMeshResult nav;
		try
		{
			nav = Map.NavMeshGen.Generate( ToQuickPlayMap( a.Session ), options );
		}
		catch ( Map.NavMeshException e )
		{
			return Fail( r, $"navmesh-failed ({e.Code}): {e.Message}" );
		}
		catch ( QuickPlayException e )
		{
			// Generate bakes with QuickPlay first; a bad map fails there
			// (empty-map, no-player-start, ...) before any navmesh work.
			return Fail( r, $"navmesh-failed ({e.Code}): {e.Message}" );
		}
		a.Session.Navmesh = nav;
		var prelaunch = Map.PlaytestLoop.PreLaunchValidation( a.Session.Doc.ToIssueDocument(), navmeshPresent: true );
		r.Log.Add( $"map_navmesh: {nav.Describe()}" );
		r.Log.Add( $"  pre-launch: passed={prelaunch.Passed} navmesh-required={prelaunch.NavmeshRequired} player-starts={prelaunch.PlayerStartCount}" );
		foreach ( var issue in prelaunch.Issues )
			r.Log.Add( $"  pre-launch issue: [{issue.Severity}] {issue.Code}: {issue.Message}" );
		if ( !prelaunch.Passed )
			return Fail( r, $"navmesh-prelaunch-failed: {prelaunch.Issues.Count} pre-launch issues (see log)." );
		return true;
	}

	static bool MapSkybox( List<string> args, string baseDir, Result r )
	{
		const string usage = "map_skybox [sky=name] [camera=0|1] [cameraorigin=x,y,z] [camerascale=16] [cubemap=0|1] [resolution=32] [map=name]";
		var a = ParseMapArgs( args, baseDir, usage );
		if ( !RejectUnknownOpts( a, r, usage, "sky", "camera", "cameraorigin", "camerascale", "cubemap", "resolution" ) )
			return false;
		var session = a.Session;
		var doc = session.Doc;
		if ( HasOpt( a, "sky" ) )
		{
			var settings = doc.CompileSettings.Clone();
			settings.SkyName = Opt( a, "sky" );
			doc.SetCompileSettings( settings );
			r.Log.Add( $"map_skybox: skyname set to '{settings.SkyName}'" );
		}
		if ( OptBool( a, "camera", false, usage ) )
		{
			var origin = OptVec3( a, "cameraorigin", new Vec3( 0, 0, 1024 ), usage );
			var scale = OptFloat( a, "camerascale", 16f, usage );
			var registry = RegistryFor( a );
			var entities = new MapEntities( registry );
			try
			{
				entities.PlacePointEntity( "sky_camera", origin, new Dictionary<string, string> { ["scale"] = FmtF( scale ) } );
			}
			catch ( MapEntityException e )
			{
				return Fail( r, $"skybox-camera-invalid ({e.Code}): {e.Message}" );
			}
			var data = new MapEntityData
			{
				Id = NextEntityId( session ),
				ClassName = "sky_camera",
				Origin = origin,
			};
			data.KeyValues["scale"] = FmtF( scale );
			doc.AddEntity( data );
			r.Log.Add( $"map_skybox: sky_camera placed at {Fmt( origin )} scale {FmtF( scale )} ({data.Id})" );
		}
		if ( OptBool( a, "cubemap", false, usage ) )
		{
			var probes = doc.Entities.Where( e => e.ClassName == "env_cubemap" ).ToList();
			if ( probes.Count == 0 )
				return Fail( r, "cubemap-no-probes: no env_cubemap entities in the map; place one first (map_entity_place class=env_cubemap origin=...)." );
			var resolution = OptInt( a, "resolution", Map.CubemapBake.DefaultResolution, usage );
			var registry = new Map.CubemapProbeRegistry();
			foreach ( var probe in probes )
				registry.Add( new Map.CubemapProbe( probe.TargetName.Length > 0 ? probe.TargetName : probe.Id, probe.Origin, resolution ) );
			Map.CubemapBakeResult bake;
			try
			{
				bake = Map.CubemapBake.Bake( registry, Map.CubemapBakeScene.FromCompileInput( CompileInput.FromDocument( doc ) ) );
			}
			catch ( Exception e )
			{
				return Fail( r, $"cubemap-bake-failed: {e.Message}" );
			}
			if ( !bake.Succeeded )
				return Fail( r, $"cubemap-bake-failed: {bake.Error ?? "no error message"}" );
			session.Cubemaps = bake.Set;
			r.Log.Add( $"map_skybox: cubemap baked — {bake.Set.ProbeCount} probes, {bake.Set.FaceCount} faces ({bake.Duration.TotalMilliseconds:0}ms)" );
			foreach ( var log in bake.Log )
				r.Log.Add( $"  cubemap: {log}" );
			return true;
		}
		var preview = Map.SkyboxPreview.BuildPreview( ToVmfDocument( session ) );
		r.Log.Add( $"map_skybox: sky '{preview.SkyName}' camera={preview.HasSkyCamera} origin={Fmt( preview.SkyCameraOrigin )} scale={FmtF( preview.Scale )} faces={preview.Faces.Count} warnings={preview.Warnings.Count}" );
		foreach ( var w in preview.Warnings )
			r.Log.Add( $"  skybox warning [{w.Code}]: {w.Message}" );
		return true;
	}
}
