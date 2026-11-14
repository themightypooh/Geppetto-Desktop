// BuildScript.MapPass2.cs — Hammer Desktop AI pass 2: BuildScript verbs for every
// pass-2 map tool (board task hammer-ai-pass2-tool-verbs).
//
// Extends the pass-1 AI surfaces (same ones as pass 1 — BuildScript verbs,
// OpsCli commands, MCP tools/resources, AiSuiteTools/ApiCatalog entries) with
// verbs for every pass-2 operation. NATIVE Geppetto only: this file calls the
// landed kernel APIs directly; it never shells out, never invents a stub API,
// and never touches UI.
//
// SEAM (integrator-owned, see tests/fixtures/hammer/hammer-ai-pass2-tool-verbs/SEAM.md):
//   - BuildScript.Command gains `case` arms that forward `map_*` verbs here
//     (this partial class must NOT define `MapVerbNames` — the pass-1
//     BuildScript.Map.cs owns it; the integrator aggregates pass-1 + pass-2).
//   - ApiCatalog merges MapPass2VerbNames()/MapPass2VerbBacking() (see
//     ApiCatalogMapPass2.cs in this folder).
//   - McpMapPass2Tools.cs (repo root) and OpsCliMapPass2.cs (repo root) call
//     RunMapPass2Verb; McpServer/OpsCli dispatch is integrator-owned.
//   - Tests: tests/HammerAiPass2ToolVerbsTests.cs, group "hammerpass2ai"
//     (NOT registered in TestRegistry.cs — integrator adds one line).
//
// IMPLEMENTATION DONE, NOT RUNTIME VERIFIED (PO workflow override 2026-11-13):
// implemented + targeted checks only, no full project build or app launch.
// Full build + named test-group run are deferred to the final integration gate.
//
// Known gaps (named, not faked):
//   - map_compile_only_entities is REAL (CompileRunner.RunOnlyEntities landed
//     with the compile-presets ticket): the file verb runs a base preset
//     in-process, keeps its artefacts, then re-runs only-entities. The MCP
//     tool keeps artefacts across calls instead.
//   - map_lock / map_hotspot / map_paste_special / map_direct_edit /
//     map_propper file verbs compute the real operation and return the
//     result, but do NOT write the map file back: locks have no VMF
//     representation, and paste/direct-edit/propper write-back needs the
//     VmfBrush reconstruction the map-document-model ticket owns. The MCP
//     tools apply those ops to the live session instead.
//   - map_layer / map_visgroup_ops file verbs operate on the VMF visgroup
//     subset (name/colour/id); full MapLayer/MapVisGroup semantics
//     (visible/compile-visible) live in the MapDocument session (MCP).
//   - map_hotload file verb watches/lists/polls one-shot; definition reload
//     needs the project's HotloadDocument (integrator seam); the MCP tool
//     keeps watches across calls where poll/reload are meaningful.
//   - map_issue_autofix applies real MapCheck fixes; IssueAutoFix execution
//     on a MapDocument needs the map-document-model adapter (its target must
//     be populated from the document) — the verb reports IssueEngine CanFix
//     per issue so the follow-up is visible.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using MapKit = Effigy.Map;

namespace Effigy;

public static partial class BuildScript
{
	/// <summary>Pass-2 map verbs. Same name as the MCP tool and the OpsCli subcommand.</summary>
	public static readonly string[] MapPass2VerbNames = new[]
	{
		"map_live_lighting",
		"map_skybox_preview",
		"map_effects_preview",
		"map_compile_preset",
		"map_compile_only_entities",
		"map_check_problems",
		"map_issue_autofix",
		"map_layer",
		"map_visgroup_ops",
		"map_lock",
		"map_paste_special",
		"map_gizmo",
		"map_direct_edit",
		"map_linked_group",
		"map_prop_place",
		"map_propper",
		"map_hotspot",
		"map_browser",
		"map_hotload",
		"map_playtest",
		"map_navmesh_generate",
	};

	/// <summary>Verb -&gt; backing kernel type (fully qualified). Every name here is a
	/// real type: verified against the kernel sources 2026-11-13 (see ApiCatalog
	/// check 5 — no phantom entries).</summary>
	public static IReadOnlyDictionary<string, string> MapPass2VerbBacking() => new Dictionary<string, string>( StringComparer.Ordinal )
	{
		["map_live_lighting"] = "Effigy.LiveLighting",
		["map_skybox_preview"] = "Effigy.Map.SkyboxPreview",
		["map_effects_preview"] = "Effigy.PreviewEffects",
		["map_compile_preset"] = "Effigy.CompileRunner",
		["map_compile_only_entities"] = "Effigy.CompileRunner",
		["map_check_problems"] = "Effigy.MapCheck",
		["map_issue_autofix"] = "Effigy.Map.IssueAutoFix",
		["map_layer"] = "Effigy.MapLayer",
		["map_visgroup_ops"] = "Effigy.MapVisGroup",
		["map_lock"] = "Effigy.FaceMaterial",
		["map_paste_special"] = "Effigy.Map.MapPaste",
		["map_gizmo"] = "Effigy.GizmoModel",
		["map_direct_edit"] = "Effigy.DirectEdit3D",
		["map_linked_group"] = "Effigy.Map.LinkedGroupSet",
		["map_prop_place"] = "Effigy.Map.PropPlacement",
		["map_propper"] = "Effigy.Map.BrushToModel",
		["map_hotspot"] = "Effigy.HotspotTexturing",
		["map_browser"] = "Effigy.Map.AssetRegistries",
		["map_hotload"] = "Effigy.Map.AssetHotload",
		["map_playtest"] = "Effigy.Map.PlaytestLoop",
		["map_navmesh_generate"] = "Effigy.Map.NavMeshGen",
	};

	/// <summary>Run one pass-2 map verb against a .vmf/.map file. Returns one JSON
	/// object (as text) with "tool" + "status" ("ok" or "not-implemented") plus the
	/// verb's fields. Throws ArgumentException for an unknown verb or bad
	/// arguments (named, never a bare failure), FileNotFoundException for a
	/// missing map. Verbs that do not need a map (map_gizmo, map_browser,
	/// map_hotload, map_linked_group without a map) accept a null mapPath.
	/// Mutating verbs write the map file back only when args carry "out";
	/// otherwise they return the computed result with written=false.</summary>
	public static string RunMapPass2Verb( string verb, string mapPath, IReadOnlyDictionary<string, string> args, string workDirectory = null )
	{
		if ( string.IsNullOrWhiteSpace( verb ) ) throw new ArgumentException( "A pass-2 verb name is required.", nameof( verb ) );
		verb = verb.Trim().ToLowerInvariant();
		var a = Pass2Args( args );
		foreach ( var forbidden in new[] { "command", "shell", "exec" } )
			if ( a.ContainsKey( forbidden ) )
				throw new ArgumentException( $"shell-refused: pass-2 verbs never run a shell; '{forbidden}' is not an argument. Compile runs CompileRunner only.", nameof( args ) );
		JsonObject result = verb switch
		{
			"map_live_lighting" => Pass2LiveLighting( Pass2LoadVmf( mapPath ), a ),
			"map_skybox_preview" => Pass2SkyboxPreview( Pass2LoadVmf( mapPath ) ),
			"map_effects_preview" => Pass2EffectsPreview( Pass2LoadVmf( mapPath ), a ),
			"map_compile_preset" => Pass2CompilePreset( Pass2LoadVmf( mapPath ), mapPath, a, workDirectory ),
			"map_compile_only_entities" => Pass2CompileOnlyEntities( Pass2LoadVmf( mapPath ), mapPath, a, workDirectory ),
			"map_check_problems" => Pass2CheckProblems( Pass2LoadVmf( mapPath ) ),
			"map_issue_autofix" => Pass2Autofix( Pass2LoadVmf( mapPath ), a ),
			"map_layer" => Pass2Layer( Pass2LoadVmf( mapPath ), a ),
			"map_visgroup_ops" => Pass2VisGroup( Pass2LoadVmf( mapPath ), a ),
			"map_lock" => Pass2Lock( Pass2LoadVmf( mapPath ), a ),
			"map_paste_special" => Pass2PasteSpecial( Pass2LoadVmf( mapPath ), a ),
			"map_gizmo" => Pass2Gizmo( a ),
			"map_direct_edit" => Pass2DirectEdit( Pass2LoadVmf( mapPath ), a ),
			"map_linked_group" => Pass2LinkedGroup( string.IsNullOrWhiteSpace( mapPath ) ? null : Pass2LoadVmf( mapPath ), a ),
			"map_prop_place" => Pass2PropPlace( Pass2LoadVmf( mapPath ), a ),
			"map_propper" => Pass2Propper( Pass2LoadVmf( mapPath ), a ),
			"map_hotspot" => Pass2Hotspot( Pass2LoadVmf( mapPath ), a ),
			"map_browser" => Pass2Browser( a, workDirectory, mapPath ),
			"map_hotload" => Pass2Hotload( a, workDirectory, mapPath ),
			"map_playtest" => Pass2Playtest( Pass2LoadVmf( mapPath ), a ),
			"map_navmesh_generate" => Pass2Navmesh( Pass2LoadVmf( mapPath ), a ),
			_ => throw new ArgumentException( $"Unknown pass-2 map verb '{verb}'. Known verbs: {string.Join( ", ", MapPass2VerbNames )}.", nameof( verb ) ),
		};
		return result.ToJsonString();
	}

	// ----- argument helpers -------------------------------------------------

	static Dictionary<string, string> Pass2Args( IReadOnlyDictionary<string, string> args )
		=> args is null
			? new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
			: new Dictionary<string, string>( args, StringComparer.OrdinalIgnoreCase );

	static string Pass2Req( Dictionary<string, string> a, string key, string verb )
	{
		if ( !a.TryGetValue( key, out var v ) || string.IsNullOrWhiteSpace( v ) )
			throw new ArgumentException( $"{verb}: missing required argument '{key}'.", nameof( a ) );
		return v.Trim();
	}

	static string Pass2Opt( Dictionary<string, string> a, string key, string fallback = null )
		=> a.TryGetValue( key, out var v ) && !string.IsNullOrWhiteSpace( v ) ? v.Trim() : fallback;

	static bool Pass2Bool( Dictionary<string, string> a, string key, bool fallback, string verb )
	{
		var v = Pass2Opt( a, key );
		if ( v is null ) return fallback;
		if ( bool.TryParse( v, out var b ) ) return b;
		throw new ArgumentException( $"{verb}: argument '{key}' must be true or false; got '{v}'.", nameof( a ) );
	}

	static int Pass2Int( Dictionary<string, string> a, string key, int fallback, string verb )
	{
		var v = Pass2Opt( a, key );
		if ( v is null ) return fallback;
		if ( int.TryParse( v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n ) ) return n;
		throw new ArgumentException( $"{verb}: argument '{key}' must be an integer; got '{v}'.", nameof( a ) );
	}

	static float Pass2Float( Dictionary<string, string> a, string key, float fallback, string verb )
	{
		var v = Pass2Opt( a, key );
		if ( v is null ) return fallback;
		if ( float.TryParse( v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n ) ) return n;
		throw new ArgumentException( $"{verb}: argument '{key}' must be a number; got '{v}'.", nameof( a ) );
	}

	static double Pass2Double( Dictionary<string, string> a, string key, double fallback, string verb )
	{
		var v = Pass2Opt( a, key );
		if ( v is null ) return fallback;
		if ( double.TryParse( v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n ) ) return n;
		throw new ArgumentException( $"{verb}: argument '{key}' must be a number; got '{v}'.", nameof( a ) );
	}

	static Vec3 Pass2Vec3( Dictionary<string, string> a, string key, Vec3 fallback, string verb )
	{
		var v = Pass2Opt( a, key );
		if ( v is null ) return fallback;
		if ( Pass2TryParseVec3( v, out var parsed ) ) return parsed;
		throw new ArgumentException( $"{verb}: argument '{key}' must be three numbers 'x y z'; got '{v}'.", nameof( a ) );
	}

	static bool Pass2TryParseVec3( string text, out Vec3 value )
	{
		value = Vec3.Zero;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		text = text.Trim();
		if ( text.StartsWith( "[", StringComparison.Ordinal ) && text.EndsWith( "]", StringComparison.Ordinal ) )
			text = text.Substring( 1, text.Length - 2 );
		var parts = text.Split( new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return false;
		if ( !double.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x )
			|| !double.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y )
			|| !double.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z ) ) return false;
		value = new Vec3( (float)x, (float)y, (float)z );
		return true;
	}

	static List<int> Pass2IdList( Dictionary<string, string> a, string key, string verb )
	{
		var v = Pass2Req( a, key, verb ).Trim();
		if ( v.StartsWith( "[", StringComparison.Ordinal ) && v.EndsWith( "]", StringComparison.Ordinal ) )
			v = v.Substring( 1, v.Length - 2 );
		var ids = new List<int>();
		foreach ( var part in v.Split( new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( !int.TryParse( part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n ) )
				throw new ArgumentException( $"{verb}: argument '{key}' must be comma-separated brush/entity ids; got '{v}'.", nameof( a ) );
			ids.Add( n );
		}
		return ids;
	}

	// ----- JSON helpers -----------------------------------------------------

	static JsonObject Pass2Obj( string tool ) => new() { ["tool"] = tool, ["status"] = "ok" };

	static JsonObject Pass2NotImplemented( string tool, string code, string reason ) => new()
	{
		["tool"] = tool,
		["status"] = "not-implemented",
		["code"] = code,
		["reason"] = reason,
	};

	static JsonArray Pass2JVec( Vec3 v ) => new( (double)v.x, (double)v.y, (double)v.z );
	static JsonArray Pass2JVecN( Vector3 v ) => new( (double)v.X, (double)v.Y, (double)v.Z );
	static JsonArray Pass2JVec2( Vec2 v ) => new( (double)v.x, (double)v.y );

	static JsonArray Pass2JStrs( IEnumerable<string> items )
	{
		var arr = new JsonArray();
		foreach ( var s in items ) arr.Add( s );
		return arr;
	}

	static JsonArray Pass2JVecs( IEnumerable<Vec3> items )
	{
		var arr = new JsonArray();
		foreach ( var v in items ) arr.Add( Pass2JVec( v ) );
		return arr;
	}

	static JsonObject Pass2JFaceMaterial( FaceMaterial m ) => new()
	{
		["material"] = m.MaterialName,
		["uv"] = new JsonObject
		{
			["offset"] = Pass2JVec2( m.Uv.Offset ),
			["scale"] = Pass2JVec2( m.Uv.Scale ),
			["rotationDegrees"] = (double)m.Uv.RotationDegrees,
			["lightmapScale"] = (double)m.Uv.LightmapScale,
			["smoothingGroup"] = m.Uv.SmoothingGroup,
			["locks"] = m.Uv.Locks.ToString(),
		},
	};

	// ----- map loading / writing -------------------------------------------

	static MapKit.VmfDocument Pass2LoadVmf( string mapPath )
	{
		if ( string.IsNullOrWhiteSpace( mapPath ) ) throw new ArgumentException( "A map file path is required for this verb.", nameof( mapPath ) );
		string full = Path.GetFullPath( mapPath );
		if ( !File.Exists( full ) ) throw new FileNotFoundException( "Map file not found.", full );
		if ( full.EndsWith( ".vmf", StringComparison.OrdinalIgnoreCase ) ) return MapKit.VmfIo.ParseFile( full );
		if ( full.EndsWith( ".map", StringComparison.OrdinalIgnoreCase ) ) return MapKit.Map220Io.ParseFile( full );
		throw new ArgumentException( "Map must be a .vmf or .map source file." );
	}

	static void Pass2WriteVmf( MapKit.VmfDocument doc, string path )
	{
		string full = Path.GetFullPath( path );
		if ( full.EndsWith( ".vmf", StringComparison.OrdinalIgnoreCase ) ) MapKit.VmfIo.WriteFile( doc, full );
		else if ( full.EndsWith( ".map", StringComparison.OrdinalIgnoreCase ) ) File.WriteAllText( full, MapKit.Map220Io.Write( doc ) );
		else throw new ArgumentException( "Output map must be a .vmf or .map source file." );
	}

	/// <summary>Output folder guard (same rule as the pass-1 compile tool): the
	/// requested folder must stay inside the root (work dir, else the map's
	/// folder). An escaping path is refused, not silently redirected.</summary>
	static string Pass2ResolveOutputFolder( string root, string requested, string verb )
	{
		string rootFull = Path.GetFullPath( root );
		string full = string.IsNullOrWhiteSpace( requested )
			? rootFull
			: Path.GetFullPath( Path.Combine( rootFull, requested ) );
		string prefix = rootFull.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar;
		if ( !full.Equals( rootFull, StringComparison.OrdinalIgnoreCase ) && !full.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( $"{verb}: output folder '{requested}' escapes the work directory '{rootFull}'.", nameof( requested ) );
		return full;
	}

	// ----- adapters (kernel copies of OpsCli's projections) -----------------
	// OpsCli (app assembly) cannot be called from the kernel, so the kernel
	// carries these projections itself. They are verbatim copies of
	// OpsCli.LoadMap / PlayerStartOf / ToIssueDocument / ToCompileInput /
	// ToQuickPlayMap / ToMapBrushes, except the QuickPlay projection throws
	// ArgumentException (named "non-axis-aligned-brush") instead of the
	// app-layer MapCommandException.

	static Vec3? Pass2PlayerStartOf( MapKit.VmfDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		foreach ( var entity in doc.Entities )
			if ( entity is not null && string.Equals( entity.ClassName, "info_player_start", StringComparison.OrdinalIgnoreCase ) )
				return Pass2EntityOrigin( entity );
		return null;
	}

	static Vec3 Pass2EntityOrigin( MapKit.VmfEntity entity ) =>
		entity is not null && Pass2TryParseVec3( entity.Get( "origin" ), out var origin ) ? origin : Vec3.Zero;

	static bool Pass2IsDetail( MapKit.VmfEntity entity ) =>
		entity is not null && string.Equals( entity.ClassName, "func_detail", StringComparison.OrdinalIgnoreCase );

	static MapKit.IssueDocument Pass2ToIssueDocument( MapKit.VmfDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var outDoc = new MapKit.IssueDocument { Name = "map" };
		void AddBrush( MapKit.VmfBrush brush, bool isDetail )
		{
			brush.ComputeAndStoreVertices();
			var faces = new List<MapKit.IssueFace>();
			foreach ( var face in brush.Faces )
			{
				var verts = new List<Vec3>();
				foreach ( var v in face.StoredVertices ) verts.Add( new Vec3( (float)v.X, (float)v.Y, (float)v.Z ) );
				faces.Add( new MapKit.IssueFace(
					new Vec3( (float)face.Normal.X, (float)face.Normal.Y, (float)face.Normal.Z ),
					face.Distance, face.Material ?? "", verts ) );
			}
			outDoc.Brushes.Add( new MapKit.IssueBrush( brush.Id, faces, isDetail ) );
		}
		foreach ( var brush in doc.WorldBrushes ) AddBrush( brush, false );
		foreach ( var entity in doc.Entities )
			foreach ( var brush in entity.Brushes ) AddBrush( brush, Pass2IsDetail( entity ) );
		foreach ( var entity in doc.Entities )
		{
			var issueEntity = new MapKit.IssueEntity( entity.Id, entity.ClassName ?? "", Pass2EntityOrigin( entity ) );
			foreach ( var kv in entity.KeyValues ) issueEntity.SetKeyValue( kv.Key, kv.Value );
			foreach ( var brush in entity.Brushes ) issueEntity.BrushIds.Add( brush.Id );
			outDoc.Entities.Add( issueEntity );
		}
		return outDoc;
	}

	static CompileInput Pass2ToCompileInput( MapKit.VmfDocument doc, string name = "map" )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var input = new CompileInput( name );
		void AddBrush( MapKit.VmfBrush brush, bool isDetail )
		{
			var faces = new List<CompileFace>();
			foreach ( var face in brush.Faces )
			{
				var compileFace = new CompileFace(
					new Vec3( (float)face.Normal.X, (float)face.Normal.Y, (float)face.Normal.Z ),
					(float)face.Distance, face.Material ?? "" )
				{ LightmapScale = face.LightmapScale };
				faces.Add( compileFace );
			}
			input.Brushes.Add( new CompileBrush( "brush" + brush.Id, brush.Id, faces, isDetail ) );
		}
		foreach ( var brush in doc.WorldBrushes ) AddBrush( brush, false );
		foreach ( var entity in doc.Entities )
		{
			foreach ( var brush in entity.Brushes ) AddBrush( brush, Pass2IsDetail( entity ) );
			if ( !string.Equals( entity.ClassName, "light", StringComparison.OrdinalIgnoreCase ) ) continue;
			var colour = new Vec3( 1, 1, 1 );
			float intensity = 1f;
			var lightParts = ( entity.Get( "_light" ) ?? "" ).Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
			if ( lightParts.Length >= 3
				&& float.TryParse( lightParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r )
				&& float.TryParse( lightParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g )
				&& float.TryParse( lightParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b ) )
			{
				colour = new Vec3( r / 255f, g / 255f, b / 255f );
				if ( lightParts.Length >= 4 && float.TryParse( lightParts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed ) )
					intensity = parsed;
			}
			input.Lights.Add( new CompileLight( Pass2EntityOrigin( entity ), intensity, colour ) );
		}
		var start = Pass2PlayerStartOf( doc );
		if ( start.HasValue ) input.PlayerStart = start.Value;
		return input;
	}

	static QuickPlayMap Pass2ToQuickPlayMap( MapKit.VmfDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var map = new QuickPlayMap();
		void AddBrush( MapKit.VmfBrush brush, bool isDetail )
		{
			map.Brushes.Add( Pass2ToQuickPlayBrush( brush, isDetail ) );
		}
		foreach ( var brush in doc.WorldBrushes ) AddBrush( brush, false );
		foreach ( var entity in doc.Entities )
		{
			foreach ( var brush in entity.Brushes ) AddBrush( brush, Pass2IsDetail( entity ) );
			float yaw = 0f;
			if ( Pass2TryParseVec3( entity.Get( "angles" ), out var angles ) ) yaw = angles.y;
			map.Entities.Add( new QuickPlayEntity( entity.ClassName ?? "", Pass2EntityOrigin( entity ), yaw ) );
		}
		return map;
	}

	static QuickPlayBrush Pass2ToQuickPlayBrush( MapKit.VmfBrush brush, bool isDetail )
	{
		const double eps = 1e-6;
		var seen = new HashSet<string>( StringComparer.Ordinal );
		float minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
		bool IsAxis( double x, double y, double z, out int axis, out double sign )
		{
			axis = -1; sign = 0;
			var components = new[] { Math.Abs( x ), Math.Abs( y ), Math.Abs( z ) };
			int nonZero = 0;
			for ( int i = 0; i < 3; i++ ) if ( components[i] > eps ) nonZero++;
			if ( nonZero != 1 ) return false;
			for ( int i = 0; i < 3; i++ )
				if ( components[i] > eps )
				{
					if ( Math.Abs( components[i] - 1.0 ) > 1e-3 ) return false;
					axis = i;
					sign = ( i == 0 ? x : i == 1 ? y : z ) > 0 ? 1 : -1;
					return true;
				}
			return false;
		}
		if ( brush.Faces.Count != 6 )
			throw new ArgumentException(
				$"non-axis-aligned-brush: brush {brush.Id} has {brush.Faces.Count} faces, not the 6 of an axis-aligned box." );
		foreach ( var face in brush.Faces )
		{
			var n = face.Normal;
			if ( !IsAxis( n.X, n.Y, n.Z, out int axis, out double sign ) || !seen.Add( axis + ":" + sign ) )
				throw new ArgumentException(
					$"non-axis-aligned-brush: brush {brush.Id} has a non-axis-aligned face (normal {n.X:0.###} {n.Y:0.###} {n.Z:0.###}) or a repeated axis face." );
			float d = (float)face.Distance;
			if ( axis == 0 ) { if ( sign > 0 ) maxX = d; else minX = -d; }
			else if ( axis == 1 ) { if ( sign > 0 ) maxY = d; else minY = -d; }
			else { if ( sign > 0 ) maxZ = d; else minZ = -d; }
		}
		string material = brush.Faces.Count > 0 ? brush.Faces[0].Material ?? "" : "";
		return new QuickPlayBrush( "brush" + brush.Id,
			new Vec3( minX, minY, minZ ), new Vec3( maxX, maxY, maxZ ), isDetail, material );
	}

	static IReadOnlyList<MapBrush> Pass2ToMapBrushes( MapKit.VmfDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var brushes = new List<MapBrush>();
		foreach ( var brush in doc.WorldBrushes ) brushes.Add( Pass2MapBrushFromVmf( brush ) );
		foreach ( var entity in doc.Entities )
			foreach ( var brush in entity.Brushes ) brushes.Add( Pass2MapBrushFromVmf( brush ) );
		return brushes;
	}

	static MapBrush Pass2MapBrushFromVmf( MapKit.VmfBrush brush )
	{
		var faces = new List<MapBrushFace>();
		foreach ( var face in brush.Faces )
			faces.Add( new MapBrushFace(
				new Vec3( (float)face.Normal.X, (float)face.Normal.Y, (float)face.Normal.Z ),
				(float)face.Distance, face.Material ?? "" ) );
		return MapBrush.FromPlanes( faces );
	}

	static InMemoryMapCheckDocument Pass2ToCheckDocument( MapKit.VmfDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var check = new InMemoryMapCheckDocument { Name = "map" };
		void AddBrush( MapKit.VmfBrush brush )
		{
			brush.ComputeAndStoreVertices();
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var face in brush.Faces )
				foreach ( var v in face.StoredVertices )
				{
					var p = new Vec3( (float)v.X, (float)v.Y, (float)v.Z );
					min = new Vec3( Math.Min( min.x, p.x ), Math.Min( min.y, p.y ), Math.Min( min.z, p.z ) );
					max = new Vec3( Math.Max( max.x, p.x ), Math.Max( max.y, p.y ), Math.Max( max.z, p.z ) );
				}
			check.AddBrush( new CheckBrush
			{
				Id = brush.Id.ToString( CultureInfo.InvariantCulture ),
				Min = min,
				Max = max,
				Material = brush.Faces.Count > 0 ? brush.Faces[0].Material ?? "" : "",
				FaceCount = brush.Faces.Count,
			} );
			foreach ( var face in brush.Faces )
				if ( !string.IsNullOrWhiteSpace( face.Material ) ) check.AddKnownMaterial( face.Material );
		}
		foreach ( var brush in doc.WorldBrushes ) AddBrush( brush );
		foreach ( var entity in doc.Entities )
		{
			foreach ( var brush in entity.Brushes ) AddBrush( brush );
			var checkEntity = new CheckEntity
			{
				Id = entity.Id.ToString( CultureInfo.InvariantCulture ),
				ClassName = entity.ClassName ?? "",
				Origin = Pass2EntityOrigin( entity ),
			};
			foreach ( var kv in entity.KeyValues ) checkEntity.Properties[kv.Key] = kv.Value;
			check.AddEntity( checkEntity );
		}
		var start = Pass2PlayerStartOf( doc );
		if ( start.HasValue )
		{
			check.HasPlayerStart = true;
			check.PlayerStart = start.Value;
		}
		return check;
	}

	// ----- brush / entity lookup --------------------------------------------

	static MapKit.VmfBrush Pass2FindBrush( MapKit.VmfDocument doc, int id, string verb )
	{
		foreach ( var brush in doc.WorldBrushes ) if ( brush.Id == id ) return brush;
		foreach ( var entity in doc.Entities )
			foreach ( var brush in entity.Brushes ) if ( brush.Id == id ) return brush;
		throw new ArgumentException( $"{verb}: brush {id} not found in the map.", nameof( id ) );
	}

	static MapKit.VmfEntity Pass2FindEntity( MapKit.VmfDocument doc, int id, string verb )
	{
		foreach ( var entity in doc.Entities ) if ( entity.Id == id ) return entity;
		throw new ArgumentException( $"{verb}: entity {id} not found in the map.", nameof( id ) );
	}

	static int Pass2NextEntityId( MapKit.VmfDocument doc )
	{
		int max = 0;
		foreach ( var entity in doc.Entities ) max = Math.Max( max, entity.Id );
		return max + 1;
	}

	static int Pass2NextVisGroupId( MapKit.VmfDocument doc )
	{
		int max = 0;
		foreach ( var g in doc.VisGroups ) max = Math.Max( max, g.Id );
		return max + 1;
	}

	// ----- pass-2 verbs -----------------------------------------------------

	static JsonObject Pass2LiveLighting( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_live_lighting";
		var qualityText = Pass2Opt( a, "quality", "draft" );
		LiveLightingQuality quality = qualityText.ToLowerInvariant() switch
		{
			"draft" => LiveLightingQuality.Draft,
			"full" => LiveLightingQuality.Full,
			_ => throw new ArgumentException( $"{verb}: quality must be draft or full; got '{qualityText}'.", nameof( a ) ),
		};
		var faces = new List<LiveFace>();
		int faceId = 0;
		int skippedFaces = 0;
		foreach ( var brush in Pass2AllBrushes( doc ) )
		{
			brush.ComputeAndStoreVertices();
			foreach ( var face in brush.Faces )
			{
				var verts = new List<Vec3>();
				foreach ( var v in face.StoredVertices ) verts.Add( new Vec3( (float)v.X, (float)v.Y, (float)v.Z ) );
				if ( verts.Count < 3 ) { skippedFaces++; continue; }
				var uv = new FaceUv( new Vec2( 0f, 0f ), new Vec2( 1f, 1f ), 0f, face.LightmapScale, 0 );
				faces.Add( new LiveFace( faceId++, verts, new FaceMaterial( face.Material ?? "", uv ), Vec3.One ) );
			}
		}
		var lights = new List<LiveLight>();
		int lightId = 0;
		int skippedSpots = 0;
		foreach ( var entity in doc.Entities )
		{
			if ( string.Equals( entity.ClassName, "light", StringComparison.OrdinalIgnoreCase ) )
			{
				Pass2ParseLightValue( entity.Get( "_light" ), out var colour, out var intensity );
				lights.Add( LiveLight.Point( lightId++, Pass2EntityOrigin( entity ), colour, intensity ) );
			}
			else if ( string.Equals( entity.ClassName, "light_environment", StringComparison.OrdinalIgnoreCase ) )
			{
				Pass2ParseLightValue( entity.Get( "_light" ), out var sunColour, out var sunIntensity );
				Pass2ParseLightValue( entity.Get( "_ambient" ), out var ambientColour, out var ambientIntensity );
				lights.Add( LiveLight.Sun( lightId++, Pass2SunDirection( entity ), sunColour, sunIntensity ) );
				lights.Add( LiveLight.Environment( lightId++, ambientColour, ambientIntensity ) );
			}
			else if ( string.Equals( entity.ClassName, "light_spot", StringComparison.OrdinalIgnoreCase ) )
			{
				// Same rule as the compile projection: a spot without a
				// direction is wrong, not approximate — counted, never faked.
				skippedSpots++;
			}
		}
		var result = LiveLighting.Evaluate( faces, lights, quality );
		var faceArr = new JsonArray();
		foreach ( var f in result.Faces )
		{
			faceArr.Add( new JsonObject
			{
				["faceId"] = f.FaceId,
				["rows"] = f.Rows,
				["cols"] = f.Cols,
				["luxelSize"] = (double)f.LuxelSize,
				["skippedNoDraw"] = f.SkippedNoDraw,
				["average"] = Pass2JVec( f.Average ),
				["vertexColors"] = f.VertexColors.Count,
				["luxels"] = f.Luxels.Count,
			} );
		}
		var obj = Pass2Obj( verb );
		obj["quality"] = quality.ToString();
		obj["invalidationKey"] = result.InvalidationKey;
		obj["warnings"] = Pass2JStrs( result.Warnings );
		obj["luxelCount"] = result.LuxelCount;
		obj["maxLuxel"] = Pass2JVec( result.MaxLuxel );
		obj["hasBounce"] = result.HasBounce;
		obj["faceCount"] = faces.Count;
		obj["lightCount"] = lights.Count;
		obj["skippedFaces"] = skippedFaces;
		obj["skippedSpots"] = skippedSpots;
		obj["faces"] = faceArr;
		return obj;
	}

	static IEnumerable<MapKit.VmfBrush> Pass2AllBrushes( MapKit.VmfDocument doc )
	{
		foreach ( var brush in doc.WorldBrushes ) yield return brush;
		foreach ( var entity in doc.Entities )
			foreach ( var brush in entity.Brushes ) yield return brush;
	}

	static void Pass2ParseLightValue( string text, out Vec3 colour, out float intensity )
	{
		colour = new Vec3( 1, 1, 1 );
		intensity = 1f;
		var parts = ( text ?? "" ).Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length >= 3
			&& float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r )
			&& float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g )
			&& float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b ) )
		{
			colour = new Vec3( r / 255f, g / 255f, b / 255f );
			if ( parts.Length >= 4 && float.TryParse( parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed ) )
				intensity = parsed;
		}
	}

	static Vec3 Pass2SunDirection( MapKit.VmfEntity entity )
	{
		if ( Pass2TryParseVec3( entity.Get( "angles" ), out var angles ) )
		{
			float pitch = angles.x * MathF.PI / 180f;
			float yaw = angles.y * MathF.PI / 180f;
			var forward = new Vec3( MathF.Cos( pitch ) * MathF.Cos( yaw ), MathF.Cos( pitch ) * MathF.Sin( yaw ), -MathF.Sin( pitch ) );
			return new Vec3( -forward.x, -forward.y, -forward.z );
		}
		return new Vec3( 0f, 0f, -1f );
	}

	static JsonObject Pass2SkyboxPreview( MapKit.VmfDocument doc )
	{
		const string verb = "map_skybox_preview";
		var scene = MapKit.SkyboxPreview.BuildPreview( doc, true );
		var warnings = new JsonArray();
		foreach ( var w in scene.Warnings )
			warnings.Add( new JsonObject { ["code"] = w.Code, ["message"] = w.Message } );
		var obj = Pass2Obj( verb );
		obj["skyName"] = scene.SkyName;
		obj["hasSkyCamera"] = scene.HasSkyCamera;
		obj["skyCameraOrigin"] = Pass2JVec( scene.SkyCameraOrigin );
		obj["scale"] = (double)scene.Scale;
		obj["has3DSection"] = scene.Has3DSection;
		obj["faceCount"] = scene.Faces.Count;
		obj["warnings"] = warnings;
		return obj;
	}

	static JsonObject Pass2EffectsPreview( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_effects_preview";
		var op = ( Pass2Opt( a, "op", "fog" ) ?? "fog" ).ToLowerInvariant();
		if ( op == "list" )
		{
			bool hasFog = false;
			foreach ( var entity in doc.Entities )
				if ( string.Equals( entity.ClassName, "env_fog_controller", StringComparison.OrdinalIgnoreCase ) ) hasFog = true;
			var obj = Pass2Obj( verb );
			obj["op"] = "list";
			obj["hasFogController"] = hasFog;
			obj["evaluators"] = Pass2JStrs( new[] { "particles", "fog", "rope", "proxy", "renderfx", "batch" } );
			obj["note"] = "This verb evaluates fog from the map's env_fog_controller (or explicit fog* args). Particles/rope/proxy/renderfx/batch need explicit effect inputs — see PreviewEffects.EvaluateBatch; wiring them from map entities is follow-up work, not a fake evaluation.";
			return obj;
		}
		if ( op != "fog" )
			throw new ArgumentException( $"{verb}: op must be fog or list; got '{op}'.", nameof( a ) );
		var controller = new PreviewEffects.PreviewFogController();
		foreach ( var entity in doc.Entities )
		{
			if ( !string.Equals( entity.ClassName, "env_fog_controller", StringComparison.OrdinalIgnoreCase ) ) continue;
			controller.Origin = Pass2EntityOrigin( entity );
			if ( float.TryParse( entity.Get( "fogstart" ), NumberStyles.Float, CultureInfo.InvariantCulture, out var s ) ) controller.FogStart = s;
			if ( float.TryParse( entity.Get( "fogend" ), NumberStyles.Float, CultureInfo.InvariantCulture, out var e ) ) controller.FogEnd = e;
			if ( float.TryParse( entity.Get( "fogmaxdensity" ), NumberStyles.Float, CultureInfo.InvariantCulture, out var d ) ) controller.FogMaxDensity = d;
			if ( Pass2TryParseVec3( entity.Get( "fogcolor" ), out var c ) ) controller.FogColor = new Vec3( c.x / 255f, c.y / 255f, c.z / 255f );
			break;
		}
		controller.FogStart = Pass2Float( a, "fogstart", controller.FogStart, verb );
		controller.FogEnd = Pass2Float( a, "fogend", controller.FogEnd, verb );
		controller.FogMaxDensity = Pass2Float( a, "fogmaxdensity", controller.FogMaxDensity, verb );
		if ( Pass2Opt( a, "fogcolor" ) is not null )
		{
			var c = Pass2Vec3( a, "fogcolor", Vec3.Zero, verb );
			controller.FogColor = new Vec3( c.x / 255f, c.y / 255f, c.z / 255f );
		}
		var camera = Pass2Vec3( a, "camera", Pass2PlayerStartOf( doc ) ?? Vec3.Zero, verb );
		var eval = PreviewEffects.EvaluateFog( controller, camera );
		var obj2 = Pass2Obj( verb );
		obj2["op"] = "fog";
		obj2["density"] = (double)eval.Density;
		obj2["factor"] = (double)eval.Factor;
		obj2["color"] = Pass2JVec( eval.Color );
		obj2["distance"] = (double)eval.Distance;
		obj2["enabled"] = eval.Enabled;
		obj2["controller"] = new JsonObject
		{
			["origin"] = Pass2JVec( controller.Origin ),
			["fogStart"] = (double)controller.FogStart,
			["fogEnd"] = (double)controller.FogEnd,
			["fogMaxDensity"] = (double)controller.FogMaxDensity,
			["fogColor"] = Pass2JVec( controller.FogColor ),
		};
		obj2["camera"] = Pass2JVec( camera );
		return obj2;
	}

	static CompilePreset Pass2ParsePreset( Dictionary<string, string> a, string verb )
	{
		var text = ( Pass2Opt( a, "preset", "quickplay" ) ?? "quickplay" ).ToLowerInvariant();
		return text switch
		{
			"quick" or "quickplay" => CompilePreset.QuickPlay,
			"preview" => CompilePreset.Preview,
			"final" => CompilePreset.Final,
			_ => throw new ArgumentException( $"{verb}: preset must be quickplay, preview or final; got '{text}'.", nameof( a ) ),
		};
	}

	static CompileOptions Pass2CompileOptions( string mapPath, Dictionary<string, string> a, string workDirectory, string verb )
	{
		string root = workDirectory;
		if ( string.IsNullOrWhiteSpace( root ) && !string.IsNullOrWhiteSpace( mapPath ) ) root = Path.GetDirectoryName( Path.GetFullPath( mapPath ) );
		if ( string.IsNullOrWhiteSpace( root ) ) root = Directory.GetCurrentDirectory();
		var options = new CompileOptions
		{
			OutputFolder = Pass2ResolveOutputFolder( root, Pass2Opt( a, "outputFolder" ), verb ),
			MaxBrushes = Pass2Int( a, "maxBrushes", 16384, verb ),
			MaxPlanesPerBrush = Pass2Int( a, "maxPlanesPerBrush", 128, verb ),
			LightmapResolutionOverride = Pass2Int( a, "lightmapResolution", 0, verb ),
		};
		var visibility = Pass2Opt( a, "visibility" );
		if ( visibility is not null )
		{
			if ( !Enum.TryParse<VisibilityQuality>( visibility, true, out var v ) )
				throw new ArgumentException( $"{verb}: visibility must be none, fast or full; got '{visibility}'.", nameof( a ) );
			options.VisibilityOverride = v;
		}
		return options;
	}

	static JsonObject Pass2JCompileResult( string verb, CompileResult result )
	{
		var issues = new JsonArray();
		foreach ( var i in result.Issues )
			issues.Add( new JsonObject
			{
				["code"] = i.Code,
				["severity"] = i.Severity.ToString(),
				["message"] = i.Message,
				["subject"] = i.Subject,
			} );
		var logTail = new JsonArray();
		int tailStart = Math.Max( 0, result.Log.Count - 20 );
		for ( int i = tailStart; i < result.Log.Count; i++ ) logTail.Add( result.Log[i] );
		var obj = Pass2Obj( verb );
		obj["preset"] = result.Preset.ToString();
		obj["succeeded"] = result.Succeeded;
		obj["cancelled"] = result.Cancelled;
		obj["error"] = result.Error;
		obj["failedStage"] = result.FailedStage?.ToString();
		obj["completedStages"] = Pass2JStrs( result.CompletedStages.Select( s => s.ToString() ) );
		obj["issues"] = issues;
		obj["logLineCount"] = result.Log.Count;
		obj["logTail"] = logTail;
		obj["outputFiles"] = Pass2JStrs( result.OutputFiles );
		obj["leak"] = result.Leak is null ? null : new JsonObject
		{
			["isLeaked"] = result.Leak.IsLeaked,
			["pathCount"] = result.Leak.Path.Count,
		};
		obj["bspLeafCount"] = result.Bsp is null ? null : JsonValue.Create( result.Bsp.LeafCount );
		obj["lightmapFaces"] = result.Lightmaps is null || result.Lightmaps.Faces is null ? null : JsonValue.Create( result.Lightmaps.Faces.Count );
		obj["durationMs"] = result.Duration.TotalMilliseconds;
		obj["inputFingerprint"] = result.InputFingerprint;
		obj["onlyEntities"] = result.OnlyEntities;
		obj["reusedPriorArtefacts"] = result.ReusedPriorArtefacts;
		obj["reusedFiles"] = Pass2JStrs( result.ReusedFiles );
		obj["namedPreset"] = result.NamedPreset;
		return obj;
	}

	static JsonObject Pass2CompilePreset( MapKit.VmfDocument doc, string mapPath, Dictionary<string, string> a, string workDirectory )
	{
		const string verb = "map_compile_preset";
		if ( Pass2PlayerStartOf( doc ) is null )
			throw new ArgumentException( $"{verb}: compile requires an info_player_start in the map.", nameof( a ) );
		var preset = Pass2ParsePreset( a, verb );
		var options = Pass2CompileOptions( mapPath, a, workDirectory, verb );
		var result = CompileRunner.Run( Pass2ToCompileInput( doc ), preset, options );
		return Pass2JCompileResult( verb, result );
	}

	static JsonObject Pass2CompileOnlyEntities( MapKit.VmfDocument doc, string mapPath, Dictionary<string, string> a, string workDirectory )
	{
		const string verb = "map_compile_only_entities";
		if ( Pass2PlayerStartOf( doc ) is null )
			throw new ArgumentException( $"{verb}: compile requires an info_player_start in the map.", nameof( a ) );
		var basePreset = Pass2ParsePreset( a, verb );
		var options = Pass2CompileOptions( mapPath, a, workDirectory, verb );
		var input = Pass2ToCompileInput( doc );
		var baseResult = CompileRunner.Run( input, basePreset, options );
		var obj = Pass2Obj( verb );
		obj["base"] = Pass2JCompileResult( verb, baseResult );
		if ( !baseResult.Succeeded )
		{
			obj["onlyEntitiesRan"] = false;
			obj["onlyEntitiesReason"] = "Base build failed, so there are no artefacts to reuse.";
			return obj;
		}
		var artefacts = baseResult.ToArtefacts( input );
		var oeResult = CompileRunner.RunOnlyEntities( input, artefacts, options );
		obj["onlyEntitiesRan"] = true;
		obj["onlyEntities"] = Pass2JCompileResult( verb, oeResult );
		return obj;
	}

	static JsonObject Pass2JMapIssue( MapIssue issue ) => new()
	{
		["id"] = issue.Id,
		["type"] = issue.Type.ToString(),
		["severity"] = issue.Severity.ToString(),
		["message"] = issue.Message,
		["fixHint"] = issue.FixHint,
		["brushId"] = issue.BrushId,
		["entityId"] = issue.EntityId,
		["key"] = issue.Key,
		["detail"] = issue.Detail,
		["position"] = Pass2JVec( issue.Position ),
		["gapPointFile"] = issue.GapPointFile,
		["gapLeafId"] = issue.GapLeafId,
		["gapPortalId"] = issue.GapPortalId,
	};

	static JsonObject Pass2CheckProblems( MapKit.VmfDocument doc )
	{
		const string verb = "map_check_problems";
		var check = Pass2ToCheckDocument( doc );
		var report = MapCheck.RunFullCheck( check );
		var issues = new JsonArray();
		foreach ( var issue in report.Issues ) issues.Add( Pass2JMapIssue( issue ) );
		var obj = Pass2Obj( verb );
		obj["documentName"] = report.DocumentName;
		obj["issueCount"] = report.IssueCount;
		obj["hasErrors"] = report.HasErrors;
		obj["skippedRules"] = Pass2JStrs( report.SkippedRules );
		obj["issues"] = issues;
		return obj;
	}

	static JsonObject Pass2Autofix( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_issue_autofix";
		var op = ( Pass2Opt( a, "op", "list" ) ?? "list" ).ToLowerInvariant();
		var check = Pass2ToCheckDocument( doc );
		var report = MapCheck.RunFullCheck( check );
		var engine = new MapKit.IssueEngine();
		var engineIssues = engine.Run( Pass2ToIssueDocument( doc ) );
		var fixable = new JsonArray();
		foreach ( var ei in engineIssues )
			fixable.Add( new JsonObject
			{
				["code"] = ei.Code,
				["kind"] = ei.Kind.ToString(),
				["severity"] = ei.Severity.ToString(),
				["message"] = ei.Message,
				["canFix"] = MapKit.IssueAutoFix.CanFix( ei.Kind ),
			} );
		if ( op == "list" )
		{
			var issues = new JsonArray();
			foreach ( var issue in report.Issues ) issues.Add( Pass2JMapIssue( issue ) );
			var obj = Pass2Obj( verb );
			obj["op"] = "list";
			obj["issues"] = issues;
			obj["engineFixability"] = fixable;
			obj["note"] = "Fixes apply to the in-memory check document (MapCheck.Fix). Persisting a fix to the .vmf/.map file needs the check-doc to VmfDocument write-back the map-document-model ticket owns; IssueAutoFix on a MapDocument needs its IMapUndoTarget populated from the document (same ticket).";
			return obj;
		}
		if ( op != "fix" )
			throw new ArgumentException( $"{verb}: op must be list or fix; got '{op}'.", nameof( a ) );
		if ( !Pass2Bool( a, "confirm", false, verb ) )
			throw new ArgumentException( $"{verb}: applying a fix is destructive — pass confirm=true.", nameof( a ) );
		var issueId = Pass2Req( a, "issue", verb );
		MapIssue target = null;
		foreach ( var issue in report.Issues )
			if ( string.Equals( issue.Id, issueId, StringComparison.OrdinalIgnoreCase ) ) target = issue;
		if ( target is null )
			throw new ArgumentException( $"{verb}: issue '{issueId}' not found. Known ids: {string.Join( ", ", report.Issues.Select( i => i.Id ) )}.", nameof( a ) );
		var log = new List<string>();
		var fix = MapCheck.Fix( check, target, log );
		var obj2 = Pass2Obj( verb );
		obj2["op"] = "fix";
		obj2["issueId"] = fix.IssueId;
		obj2["type"] = fix.Type.ToString();
		obj2["fixName"] = fix.FixName;
		obj2["status"] = fix.Status.ToString();
		obj2["applied"] = fix.Applied;
		obj2["detail"] = fix.Detail;
		obj2["log"] = Pass2JStrs( log );
		obj2["engineFixability"] = fixable;
		return obj2;
	}

	// ----- visgroup / layer store (VMF-backed subset) -----------------------

	static string Pass2HexToRgb( string hex, string verb )
	{
		if ( !MapLayer.IsValidColour( hex ) )
			throw new ArgumentException( $"{verb}: colour must be '#RRGGBB'; got '{hex}'.", nameof( hex ) );
		int r = int.Parse( hex.Substring( 1, 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture );
		int g = int.Parse( hex.Substring( 3, 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture );
		int b = int.Parse( hex.Substring( 5, 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture );
		return $"{r} {g} {b}";
	}

	static string Pass2RgbToHex( string rgb )
	{
		var parts = ( rgb ?? "" ).Split( new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length == 3
			&& int.TryParse( parts[0], out var r ) && int.TryParse( parts[1], out var g ) && int.TryParse( parts[2], out var b ) )
			return $"#{r:X2}{g:X2}{b:X2}";
		return "#808080";
	}

	static JsonObject Pass2JVisGroup( MapKit.VmfVisGroup g ) => new()
	{
		["id"] = g.Id,
		["name"] = g.Name,
		["colour"] = Pass2RgbToHex( g.Color ),
		["colorRgb"] = g.Color,
	};

	static bool Pass2WriteIfOut( MapKit.VmfDocument doc, Dictionary<string, string> a, JsonObject obj )
	{
		var outPath = Pass2Opt( a, "out" );
		if ( outPath is null ) { obj["written"] = false; return false; }
		Pass2WriteVmf( doc, outPath );
		obj["written"] = true;
		obj["out"] = Path.GetFullPath( outPath );
		return true;
	}

	static JsonObject Pass2VisGroupOp( MapKit.VmfDocument doc, Dictionary<string, string> a, string verb, bool layerMode )
	{
		var op = ( Pass2Opt( a, "op", "list" ) ?? "list" ).ToLowerInvariant();
		if ( op == "list" )
		{
			var arr = new JsonArray();
			foreach ( var g in doc.VisGroups ) arr.Add( Pass2JVisGroup( g ) );
			var obj = Pass2Obj( verb );
			obj["op"] = "list";
			obj["visgroups"] = arr;
			if ( layerMode )
				obj["note"] = "VMF visgroups carry name/colour/id only. MapLayer visible/compile-visible live in the MapDocument session — see the map_layer MCP tool.";
			return obj;
		}
		if ( op == "define" )
		{
			var name = Pass2Req( a, "name", verb );
			foreach ( var g in doc.VisGroups )
				if ( string.Equals( g.Name, name, StringComparison.OrdinalIgnoreCase ) )
					throw new ArgumentException( $"{verb}: visgroup '{name}' already exists.", nameof( a ) );
			var colour = Pass2Opt( a, "colour", "#808080" );
			var created = new MapKit.VmfVisGroup { Id = Pass2NextVisGroupId( doc ), Name = name, Color = Pass2HexToRgb( colour, verb ) };
			doc.VisGroups.Add( created );
			var obj = Pass2Obj( verb );
			obj["op"] = "define";
			obj["visgroup"] = Pass2JVisGroup( created );
			if ( layerMode )
			{
				obj["visible"] = Pass2Bool( a, "visible", true, verb );
				obj["compileVisible"] = Pass2Bool( a, "compileVisible", true, verb );
				obj["note"] = "visible/compileVisible are accepted and reported but not stored in the VMF visgroup block; they live in the MapDocument session (map_layer MCP tool).";
			}
			Pass2WriteIfOut( doc, a, obj );
			return obj;
		}
		if ( op == "remove" )
		{
			if ( !Pass2Bool( a, "confirm", false, verb ) )
				throw new ArgumentException( $"{verb}: removing a visgroup is destructive — pass confirm=true.", nameof( a ) );
			var name = Pass2Req( a, "name", verb );
			MapKit.VmfVisGroup found = null;
			foreach ( var g in doc.VisGroups )
				if ( string.Equals( g.Name, name, StringComparison.OrdinalIgnoreCase ) ) found = g;
			if ( found is null ) throw new ArgumentException( $"{verb}: visgroup '{name}' not found.", nameof( a ) );
			doc.VisGroups.Remove( found );
			var obj = Pass2Obj( verb );
			obj["op"] = "remove";
			obj["removed"] = name;
			Pass2WriteIfOut( doc, a, obj );
			return obj;
		}
		if ( op == "rename" )
		{
			var name = Pass2Req( a, "name", verb );
			var newName = Pass2Req( a, "newName", verb );
			MapKit.VmfVisGroup found = null;
			foreach ( var g in doc.VisGroups )
				if ( string.Equals( g.Name, name, StringComparison.OrdinalIgnoreCase ) ) found = g;
			if ( found is null ) throw new ArgumentException( $"{verb}: visgroup '{name}' not found.", nameof( a ) );
			found.Name = newName;
			var obj = Pass2Obj( verb );
			obj["op"] = "rename";
			obj["visgroup"] = Pass2JVisGroup( found );
			Pass2WriteIfOut( doc, a, obj );
			return obj;
		}
		if ( op == "setcolour" || op == "setcolor" )
		{
			var name = Pass2Req( a, "name", verb );
			var colour = Pass2Req( a, "colour", verb );
			MapKit.VmfVisGroup found = null;
			foreach ( var g in doc.VisGroups )
				if ( string.Equals( g.Name, name, StringComparison.OrdinalIgnoreCase ) ) found = g;
			if ( found is null ) throw new ArgumentException( $"{verb}: visgroup '{name}' not found.", nameof( a ) );
			found.Color = Pass2HexToRgb( colour, verb );
			var obj = Pass2Obj( verb );
			obj["op"] = "setcolour";
			obj["visgroup"] = Pass2JVisGroup( found );
			Pass2WriteIfOut( doc, a, obj );
			return obj;
		}
		if ( op == "assign" )
		{
			if ( layerMode )
				throw new ArgumentException( $"{verb}: assign is a visgroup op (map_visgroup_ops), not a layer op.", nameof( a ) );
			var brushId = Pass2Int( a, "brush", -1, verb );
			if ( brushId < 0 ) throw new ArgumentException( $"{verb}: assign needs brush=<id>.", nameof( a ) );
			var name = Pass2Req( a, "visgroup", verb );
			MapKit.VmfVisGroup found = null;
			foreach ( var g in doc.VisGroups )
				if ( string.Equals( g.Name, name, StringComparison.OrdinalIgnoreCase ) ) found = g;
			if ( found is null ) throw new ArgumentException( $"{verb}: visgroup '{name}' not found.", nameof( a ) );
			var brush = Pass2FindBrush( doc, brushId, verb );
			brush.VisGroupId = found.Id;
			var obj = Pass2Obj( verb );
			obj["op"] = "assign";
			obj["brush"] = brushId;
			obj["visgroup"] = Pass2JVisGroup( found );
			Pass2WriteIfOut( doc, a, obj );
			return obj;
		}
		if ( op == "setvisible" )
		{
			return Pass2NotImplemented( verb, "visgroup-visibility-not-in-vmf",
				"VMF visgroups carry no visibility flag (name/colour/id only). Visibility lives on MapLayer/MapVisGroup in the MapDocument session — use the map_layer / map_visgroup_ops MCP tool." );
		}
		throw new ArgumentException( $"{verb}: op must be list, define, remove, rename, setcolour{( layerMode ? "" : ", assign, setvisible" )}; got '{op}'.", nameof( a ) );
	}

	static JsonObject Pass2Layer( MapKit.VmfDocument doc, Dictionary<string, string> a )
		=> Pass2VisGroupOp( doc, a, "map_layer", layerMode: true );

	static JsonObject Pass2VisGroup( MapKit.VmfDocument doc, Dictionary<string, string> a )
		=> Pass2VisGroupOp( doc, a, "map_visgroup_ops", layerMode: false );

	static LockFlags Pass2ParseLocks( Dictionary<string, string> a, string verb )
	{
		var text = ( Pass2Opt( a, "locks", "uv" ) ?? "uv" ).ToLowerInvariant();
		return text switch
		{
			"none" => LockFlags.None,
			"scale" => LockFlags.ScaleLock,
			"uv" => LockFlags.UvLock,
			"both" => LockFlags.ScaleLock | LockFlags.UvLock,
			_ => throw new ArgumentException( $"{verb}: locks must be none, scale, uv or both; got '{text}'.", nameof( a ) ),
		};
	}

	static JsonObject Pass2Lock( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_lock";
		int brushId = Pass2Int( a, "brush", -1, verb );
		if ( brushId < 0 ) throw new ArgumentException( $"{verb}: brush=<id> is required.", nameof( a ) );
		int faceIndex = Pass2Int( a, "face", 0, verb );
		var brush = Pass2FindBrush( doc, brushId, verb );
		if ( faceIndex < 0 || faceIndex >= brush.Faces.Count )
			throw new ArgumentException( $"{verb}: face {faceIndex} out of range for brush {brushId} ({brush.Faces.Count} faces).", nameof( a ) );
		var face = brush.Faces[faceIndex];
		var flags = Pass2ParseLocks( a, verb );
		var uv = new FaceUv( new Vec2( 0f, 0f ), new Vec2( 1f, 1f ), 0f, face.LightmapScale, 0 );
		var material = new FaceMaterial( face.Material ?? "", uv );
		var locked = material.WithLocks( flags );
		var obj = Pass2Obj( verb );
		obj["brush"] = brushId;
		obj["face"] = faceIndex;
		obj["original"] = Pass2JFaceMaterial( material );
		obj["locked"] = Pass2JFaceMaterial( locked );
		obj["note"] = "Locks are per-face FaceMaterial state; they have no VMF representation, so this verb evaluates and reports the locked material. Applying locks to the live session is the map_lock MCP tool.";
		var translate = Pass2Opt( a, "translate" );
		var rotate = Pass2Opt( a, "rotate" );
		if ( translate is not null || rotate is not null )
		{
			var matrix = Matrix4x4.Identity;
			if ( rotate is not null )
			{
				float degrees = Pass2Float( a, "rotate", 0f, verb );
				var axisText = ( Pass2Opt( a, "axis", "z" ) ?? "z" ).ToLowerInvariant();
				Vector3 axis = axisText switch
				{
					"x" => Vector3.UnitX,
					"y" => Vector3.UnitY,
					"z" => Vector3.UnitZ,
					_ => throw new ArgumentException( $"{verb}: axis must be x, y or z; got '{axisText}'.", nameof( a ) ),
				};
				matrix = Matrix4x4.CreateFromAxisAngle( axis, degrees * MathF.PI / 180f ) * matrix;
			}
			if ( translate is not null )
			{
				var t = Pass2Vec3( a, "translate", Vec3.Zero, verb );
				matrix = matrix * Matrix4x4.CreateTranslation( t.x, t.y, t.z );
			}
			var normal = new Vec3( (float)face.Normal.X, (float)face.Normal.Y, (float)face.Normal.Z );
			var (uAxis, vAxis) = FaceUv.PlaneAxes( normal );
			var mapping = new MapUvMapping(
				new Vector3( uAxis.x, uAxis.y, uAxis.z ), new Vector3( vAxis.x, vAxis.y, vAxis.z ),
				new Vector2( uv.Offset.x, uv.Offset.y ), new Vector2( uv.Scale.x, uv.Scale.y ), uv.RotationDegrees );
			var lockedMapping = MapTransform.ApplyTextureLock( mapping, matrix );
			brush.ComputeAndStoreVertices();
			var centroid = Vec3.Zero;
			int centroidCount = 0;
			foreach ( var v in face.StoredVertices )
			{
				centroid += new Vec3( (float)v.X, (float)v.Y, (float)v.Z );
				centroidCount++;
			}
			if ( centroidCount > 0 ) centroid = new Vec3( centroid.x / centroidCount, centroid.y / centroidCount, centroid.z / centroidCount );
			var centroidN = new Vector3( centroid.x, centroid.y, centroid.z );
			var origSample = mapping.Sample( centroidN );
			var lockedSample = lockedMapping.Sample( Vector3.Transform( centroidN, matrix ) );
			var preserved = ( origSample - lockedSample ).Length() < 1e-4f;
			obj["sampleCheck"] = new JsonObject
			{
				["originalUv"] = new JsonArray( (double)origSample.X, (double)origSample.Y ),
				["lockedUv"] = new JsonArray( (double)lockedSample.X, (double)lockedSample.Y ),
				["preserved"] = preserved,
			};
		}
		return obj;
	}

	static MapKit.MapPasteBrush Pass2PasteBrushFromVmf( MapKit.VmfBrush brush )
	{
		var planes = new List<Plane>();
		var uvMappings = new List<MapUvMapping>();
		var materials = new List<string>();
		foreach ( var face in brush.Faces )
		{
			var normal = new Vec3( (float)face.Normal.X, (float)face.Normal.Y, (float)face.Normal.Z );
			planes.Add( new Plane( new Vector3( normal.x, normal.y, normal.z ), (float)face.Distance ) );
			var (uAxis, vAxis) = FaceUv.PlaneAxes( normal );
			uvMappings.Add( new MapUvMapping(
				new Vector3( uAxis.x, uAxis.y, uAxis.z ), new Vector3( vAxis.x, vAxis.y, vAxis.z ),
				new Vector2( 0f, 0f ), new Vector2( 1f, 1f ), 0f ) );
			materials.Add( face.Material ?? "" );
		}
		return new MapKit.MapPasteBrush( planes, uvMappings, materials );
	}

	static JsonObject Pass2PasteSpecial( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_paste_special";
		List<int> ids;
		if ( a.ContainsKey( "brushes" ) ) ids = Pass2IdList( a, "brushes", verb );
		else if ( a.ContainsKey( "brush" ) ) ids = new List<int> { Pass2Int( a, "brush", -1, verb ) };
		else throw new ArgumentException( $"{verb}: brushes=<id,...> is required.", nameof( a ) );
		int copies = Pass2Int( a, "copies", 1, verb );
		if ( copies < 1 || copies > 1024 )
			throw new ArgumentException( $"{verb}: copies must be 1..1024; got {copies}.", nameof( a ) );
		var axisText = ( Pass2Opt( a, "axis", "z" ) ?? "z" ).ToLowerInvariant();
		Vector3 axis = axisText switch
		{
			"x" => Vector3.UnitX,
			"y" => Vector3.UnitY,
			"z" => Vector3.UnitZ,
			_ => throw new ArgumentException( $"{verb}: axis must be x, y or z; got '{axisText}'.", nameof( a ) ),
		};
		var off = Pass2Vec3( a, "offset", Vec3.Zero, verb );
		var options = new MapKit.PasteSpecialOptions
		{
			Copies = copies,
			RotationDegrees = Pass2Float( a, "rotation", 0f, verb ),
			RotationAxis = axis,
			Offset = new Vector3( off.x, off.y, off.z ),
			GridSize = Pass2Double( a, "grid", GizmoModel.DefaultGridSize, verb ),
			AngleSnapDegrees = Pass2Double( a, "anglesnap", 0, verb ),
			TextureLock = Pass2Bool( a, "texturelock", true, verb ),
		};
		if ( Pass2Opt( a, "pivot" ) is not null )
		{
			var p = Pass2Vec3( a, "pivot", Vec3.Zero, verb );
			options.Pivot = new Vector3( p.x, p.y, p.z );
		}
		var selection = new List<MapKit.MapPasteBrush>();
		foreach ( var id in ids ) selection.Add( Pass2PasteBrushFromVmf( Pass2FindBrush( doc, id, verb ) ) );
		var result = MapKit.MapPaste.PasteSpecial( selection, options );
		var pasted = new JsonArray();
		int shown = 0;
		foreach ( var b in result.Brushes )
		{
			if ( shown >= 10 ) break;
			var planes = new JsonArray();
			for ( int i = 0; i < b.Planes.Count; i++ )
			{
				var p = b.Planes[i];
				planes.Add( new JsonObject
				{
					["normal"] = new JsonArray( (double)p.Normal.X, (double)p.Normal.Y, (double)p.Normal.Z ),
					["distance"] = (double)p.D,
					["material"] = i < b.Materials.Count ? b.Materials[i] : "",
				} );
			}
			pasted.Add( new JsonObject { ["planes"] = planes } );
			shown++;
		}
		var obj = Pass2Obj( verb );
		obj["ok"] = result.Ok;
		obj["reason"] = result.Reason;
		obj["message"] = result.Message;
		obj["pivotUsed"] = Pass2JVecN( result.PivotUsed );
		obj["copiesRequested"] = result.CopiesRequested;
		obj["pastedCount"] = result.Brushes.Count;
		obj["pasted"] = pasted;
		obj["note"] = "Pasted brushes are returned as planes; writing them back into the .vmf/.map file needs the VmfBrush reconstruction the map-document-model ticket owns (integrator seam).";
		return obj;
	}

	static JsonObject Pass2Gizmo( Dictionary<string, string> a )
	{
		const string verb = "map_gizmo";
		var modeText = Pass2Opt( a, "mode", "translate" ) ?? "translate";
		var spaceText = Pass2Opt( a, "space", "global" ) ?? "global";
		var axisText = Pass2Opt( a, "axis", "none" ) ?? "none";
		if ( !Enum.TryParse<GizmoMode>( modeText, true, out var mode ) )
			throw new ArgumentException( $"{verb}: mode must be translate, rotate or scale; got '{modeText}'.", nameof( a ) );
		if ( !Enum.TryParse<GizmoSpace>( spaceText, true, out var space ) )
			throw new ArgumentException( $"{verb}: space must be global or local; got '{spaceText}'.", nameof( a ) );
		if ( !Enum.TryParse<GizmoAxisLock>( axisText, true, out var axis ) )
			throw new ArgumentException( $"{verb}: axis must be none, x, y, z, xy, yz or xz; got '{axisText}'.", nameof( a ) );
		var pivot = Pass2Vec3( a, "pivot", Vec3.Zero, verb );
		var state = new GizmoState( mode, space, axis,
			new Vector3( pivot.x, pivot.y, pivot.z ), Quaternion.Identity,
			Pass2Bool( a, "snap", false, verb ),
			Pass2Double( a, "grid", GizmoModel.DefaultGridSize, verb ),
			Pass2Double( a, "anglesnap", 0, verb ) );
		var health = GizmoModel.GizmoHealthCheck( state );
		var obj = Pass2Obj( verb );
		obj["state"] = new JsonObject
		{
			["mode"] = state.Mode.ToString(),
			["space"] = state.Space.ToString(),
			["lockedAxis"] = state.LockedAxis.ToString(),
			["pivot"] = Pass2JVecN( state.Pivot ),
			["snapEnabled"] = state.SnapEnabled,
			["gridSize"] = state.GridSize,
			["angleSnapDegrees"] = state.AngleSnapDegrees,
		};
		obj["healthPassed"] = health.Passed;
		obj["healthReason"] = health.Reason;
		if ( Pass2Opt( a, "point" ) is not null )
		{
			var p = Pass2Vec3( a, "point", Vec3.Zero, verb );
			var pn = new Vector3( p.x, p.y, p.z );
			var snapped = state.SnapEnabled ? MapTransform.SnapToGrid( pn, state.GridSize ) : pn;
			obj["point"] = Pass2JVecN( pn );
			obj["snappedPoint"] = Pass2JVecN( snapped );
		}
		if ( Pass2Opt( a, "angle" ) is not null )
		{
			double angle = Pass2Double( a, "angle", 0, verb );
			double snapped = state.SnapEnabled ? MapTransform.SnapAngle( angle, state.AngleSnapDegrees ) : angle;
			obj["angle"] = angle;
			obj["snappedAngle"] = snapped;
		}
		obj["note"] = "GizmoModel is state + math: there is no apply-to-document method. Transforms apply via MapTransform / DirectEdit3D (see map_direct_edit). Drag-to-transform needs a GizmoDrag/GizmoView pair — the MCP tool evaluates it against the live session.";
		return obj;
	}

	static MapVertexBrush Pass2VertexBrushFromVmf( MapKit.VmfBrush brush )
	{
		var mapBrush = Pass2MapBrushFromVmf( brush );
		var indexByVertex = new Dictionary<(float, float, float), int>();
		var vertices = new List<Vec3>();
		var faces = new List<int[]>();
		var materials = new List<string>();
		for ( int i = 0; i < mapBrush.Faces.Count; i++ )
		{
			var loop = mapBrush.FaceVertices( i );
			var indices = new int[loop.Count];
			for ( int j = 0; j < loop.Count; j++ )
			{
				var v = loop[j];
				var key = (v.x, v.y, v.z);
				if ( !indexByVertex.TryGetValue( key, out var idx ) )
				{
					idx = vertices.Count;
					indexByVertex[key] = idx;
					vertices.Add( v );
				}
				indices[j] = idx;
			}
			faces.Add( indices );
			materials.Add( mapBrush.Faces[i].Material.MaterialName );
		}
		return new MapVertexBrush( vertices.ToArray(), faces.ToArray(), materials.ToArray() );
	}

	static JsonObject Pass2DirectEdit( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_direct_edit";
		int brushId = Pass2Int( a, "brush", -1, verb );
		if ( brushId < 0 ) throw new ArgumentException( $"{verb}: brush=<id> is required.", nameof( a ) );
		var op = ( Pass2Opt( a, "op", "extrude" ) ?? "extrude" ).ToLowerInvariant();
		var brush = Pass2VertexBrushFromVmf( Pass2FindBrush( doc, brushId, verb ) );
		int face = Pass2Int( a, "face", 0, verb );
		if ( face < 0 || face >= brush.FaceCount )
			throw new ArgumentException( $"{verb}: face {face} out of range for brush {brushId} ({brush.FaceCount} faces).", nameof( a ) );
		DirectEditResult result = op switch
		{
			"extrude" => DirectEdit3D.ExtrudeFace( brush, face, Pass2Float( a, "distance", 16f, verb ) ),
			"dragface" => DirectEdit3D.DragFace( brush, face, Pass2Vec3( a, "delta", Vec3.Zero, verb ) ),
			"dragfacealongnormal" => DirectEdit3D.DragFaceAlongNormal( brush, face, Pass2Float( a, "distance", 16f, verb ) ),
			"dragedge" => DirectEdit3D.DragEdge( brush, Pass2Int( a, "a", 0, verb ), Pass2Int( a, "b", 1, verb ), Pass2Vec3( a, "delta", Vec3.Zero, verb ) ),
			"dragvertex" => DirectEdit3D.DragVertex( brush, Pass2Int( a, "vertex", 0, verb ), Pass2Vec3( a, "to", Vec3.Zero, verb ) ),
			"dragvertexby" => DirectEdit3D.DragVertexBy( brush, Pass2Int( a, "vertex", 0, verb ), Pass2Vec3( a, "delta", Vec3.Zero, verb ) ),
			"shear" => DirectEdit3D.Shear3D( brush, Pass2Vec3( a, "pivot", Vec3.Zero, verb ), Pass2Vec3( a, "axis", new Vec3( 0f, 0f, 1f ), verb ), Pass2Vec3( a, "direction", new Vec3( 1f, 0f, 0f ), verb ), Pass2Float( a, "factor", 0.5f, verb ) ),
			"scale" => DirectEdit3D.Scale3D( brush, Pass2Vec3( a, "pivot", Vec3.Zero, verb ), Pass2Vec3( a, "factors", new Vec3( 1f, 1f, 1f ), verb ) ),
			_ => throw new ArgumentException( $"{verb}: op must be extrude, dragface, dragfacealongnormal, dragedge, dragvertex, dragvertexby, shear or scale; got '{op}'.", nameof( a ) ),
		};
		var obj = Pass2Obj( verb );
		obj["brush"] = brushId;
		obj["op"] = op;
		obj["isValid"] = result.IsValid;
		obj["defects"] = Pass2JStrs( (IEnumerable<string>?)result.Validity?.Defects ?? Array.Empty<string>() );
		obj["affectedFaces"] = new JsonArray( result.AffectedFaces.Select( i => (JsonNode?)i ).ToArray() );
		obj["volumeBefore"] = result.VolumeBefore;
		obj["volumeAfter"] = result.VolumeAfter;
		obj["volumeDelta"] = result.VolumeDelta;
		obj["invalidFaceIndices"] = new JsonArray( result.InvalidFaceIndices.Select( i => (JsonNode?)i ).ToArray() );
		obj["editedBrush"] = new JsonObject
		{
			["vertexCount"] = result.Brush.VertexCount,
			["faceCount"] = result.Brush.FaceCount,
			["vertices"] = Pass2JVecs( result.Brush.Vertices ),
			["faces"] = new JsonArray( result.Brush.Faces.Select( f => (JsonNode?)new JsonArray( f.Select( i => (JsonNode?)i ).ToArray() ) ).ToArray() ),
			["materials"] = Pass2JStrs( result.Brush.Materials ),
		};
		obj["note"] = "The edited brush is returned as geometry; writing it back into the .vmf/.map file needs the VmfBrush reconstruction the map-document-model ticket owns (integrator seam). The MCP tool keeps the edited brush in its session overlay.";
		return obj;
	}

	static JsonObject Pass2LinkedGroup( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_linked_group";
		var op = ( Pass2Opt( a, "op", "list" ) ?? "list" ).ToLowerInvariant();
		var set = Pass2Opt( a, "state" ) is string stateJson && !string.IsNullOrWhiteSpace( stateJson )
			? MapKit.LinkedGroupSet.FromJson( stateJson )
			: new MapKit.LinkedGroupSet();
		JsonObject Finish( JsonObject obj )
		{
			obj["state"] = set.ToJson();
			return obj;
		}
		if ( op == "list" || op == "tojson" )
		{
			var defs = new JsonArray();
			foreach ( var d in set.Definitions )
			{
				var members = new JsonArray();
				foreach ( var m in d.Members )
					members.Add( new JsonObject { ["id"] = m.Id, ["kind"] = m.Kind.ToString(), ["name"] = m.Name, ["material"] = m.Material } );
				defs.Add( new JsonObject { ["id"] = d.Id, ["name"] = d.Name, ["members"] = members, ["layer"] = d.Layer, ["visGroup"] = d.VisGroup } );
			}
			var insts = new JsonArray();
			foreach ( var inst in set.Instances )
				insts.Add( new JsonObject { ["id"] = inst.Id, ["definitionId"] = inst.DefinitionId, ["offset"] = Pass2JVec( inst.Offset ), ["layer"] = inst.Layer, ["visGroup"] = inst.VisGroup } );
			var obj = Pass2Obj( verb );
			obj["op"] = op;
			obj["definitions"] = defs;
			obj["instances"] = insts;
			return Finish( obj );
		}
		if ( op == "define" )
		{
			var name = Pass2Req( a, "name", verb );
			var members = new List<MapKit.LinkedGroupMember>();
			if ( doc is not null && ( a.ContainsKey( "brushIds" ) || a.ContainsKey( "entityIds" ) ) )
			{
				if ( a.ContainsKey( "brushIds" ) )
					foreach ( var id in Pass2IdList( a, "brushIds", verb ) )
					{
						var b = Pass2FindBrush( doc, id, verb );
						var mb = Pass2MapBrushFromVmf( b );
						members.Add( new MapKit.LinkedGroupMember
						{
							Id = "brush" + id,
							Kind = MapKit.LinkedGroupMemberKind.Brush,
							Name = "brush" + id,
							Material = b.Faces.Count > 0 ? b.Faces[0].Material ?? "" : "",
							Origin = Vec3.Zero,
						} );
						members[members.Count - 1].Vertices.AddRange( mb.Vertices );
					}
				if ( a.ContainsKey( "entityIds" ) )
					foreach ( var id in Pass2IdList( a, "entityIds", verb ) )
					{
						var e = Pass2FindEntity( doc, id, verb );
						members.Add( new MapKit.LinkedGroupMember
						{
							Id = "entity" + id,
							Kind = MapKit.LinkedGroupMemberKind.Entity,
							Name = e.ClassName ?? "",
							Origin = Pass2EntityOrigin( e ),
						} );
					}
			}
			else if ( Pass2Opt( a, "members" ) is string membersJson )
			{
				var parsed = JsonNode.Parse( membersJson )?.AsArray()
					?? throw new ArgumentException( $"{verb}: members must be a JSON array.", nameof( a ) );
				foreach ( var node in parsed )
				{
					var mObj = node?.AsObject() ?? throw new ArgumentException( $"{verb}: each member must be a JSON object.", nameof( a ) );
					var kindText = mObj["kind"]?.GetValue<string>() ?? "brush";
					if ( !Enum.TryParse<MapKit.LinkedGroupMemberKind>( kindText, true, out var kind ) )
						throw new ArgumentException( $"{verb}: member kind must be brush or entity; got '{kindText}'.", nameof( a ) );
					var member = new MapKit.LinkedGroupMember
					{
						Id = mObj["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString( "N" ),
						Kind = kind,
						Name = mObj["name"]?.GetValue<string>() ?? "",
						Material = mObj["material"]?.GetValue<string>() ?? "",
						Origin = mObj["origin"] is JsonArray originArr && originArr.Count == 3
							? new Vec3( (float)originArr[0]!.GetValue<double>(), (float)originArr[1]!.GetValue<double>(), (float)originArr[2]!.GetValue<double>() )
							: Vec3.Zero,
					};
					if ( mObj["vertices"] is JsonArray verts )
						foreach ( var vNode in verts )
							if ( vNode is JsonArray vArr && vArr.Count == 3 )
								member.Vertices.Add( new Vec3( (float)vArr[0]!.GetValue<double>(), (float)vArr[1]!.GetValue<double>(), (float)vArr[2]!.GetValue<double>() ) );
					members.Add( member );
				}
			}
			else throw new ArgumentException( $"{verb}: define needs brushIds=/entityIds= (with a map) or members=<json>.", nameof( a ) );
			var result = set.DefineLinkedGroup( name, members, Pass2Opt( a, "layer", "" ), Pass2Opt( a, "visgroup", "" ) );
			var obj = Pass2Obj( verb );
			obj["op"] = "define";
			obj["ok"] = result.Ok;
			obj["message"] = result.Message;
			obj["definitionId"] = result.Definition?.Id;
			obj["definitionName"] = result.Definition?.Name;
			return Finish( obj );
		}
		if ( op == "link" )
		{
			var definitionId = Pass2Req( a, "definition", verb );
			var offset = Pass2Vec3( a, "offset", Vec3.Zero, verb );
			var result = set.LinkCopy( definitionId, offset, Pass2Opt( a, "layer", "" ), Pass2Opt( a, "visgroup", "" ) );
			var obj = Pass2Obj( verb );
			obj["op"] = "link";
			obj["ok"] = result.Ok;
			obj["message"] = result.Message;
			obj["instanceId"] = result.Instance?.Id;
			obj["instanceOffset"] = result.Instance is null ? null : Pass2JVec( result.Instance.Offset );
			return Finish( obj );
		}
		if ( op == "edit" || op == "editviainstance" )
		{
			var memberId = Pass2Req( a, "member", verb );
			var editKind = ( Pass2Opt( a, "edit", "move" ) ?? "move" ).ToLowerInvariant();
			MapKit.LinkedMemberEdit edit = editKind switch
			{
				"move" => MapKit.LinkedMemberEdit.Move( Pass2Vec3( a, "delta", Vec3.Zero, verb ) ),
				"setmaterial" => MapKit.LinkedMemberEdit.SetMaterial( Pass2Req( a, "material", verb ) ),
				"setkeyvalue" => MapKit.LinkedMemberEdit.SetKeyValue( Pass2Req( a, "key", verb ), Pass2Req( a, "value", verb ) ),
				"rename" => MapKit.LinkedMemberEdit.Rename( Pass2Req( a, "newname", verb ) ),
				_ => throw new ArgumentException( $"{verb}: edit must be move, setmaterial, setkeyvalue or rename; got '{editKind}'.", nameof( a ) ),
			};
			MapKit.EditMemberResult editResult;
			if ( op == "edit" )
				editResult = set.EditMember( Pass2Req( a, "definition", verb ), memberId, edit );
			else
				editResult = set.EditMemberViaInstance( Pass2Req( a, "instance", verb ), memberId, edit );
			var obj = Pass2Obj( verb );
			obj["op"] = op;
			obj["ok"] = editResult.Ok;
			obj["message"] = editResult.Message;
			obj["affectedInstances"] = editResult.AffectedInstances;
			obj["affectedInstanceIds"] = Pass2JStrs( editResult.AffectedInstanceIds );
			return Finish( obj );
		}
		if ( op == "unlink" )
		{
			if ( !Pass2Bool( a, "confirm", false, verb ) )
				throw new ArgumentException( $"{verb}: unlinking a group is destructive — pass confirm=true.", nameof( a ) );
			var result = set.Unlink( Pass2Req( a, "instance", verb ) );
			var obj = Pass2Obj( verb );
			obj["op"] = "unlink";
			obj["ok"] = result.Ok;
			obj["message"] = result.Message;
			obj["groupId"] = result.Group?.Id;
			return Finish( obj );
		}
		throw new ArgumentException( $"{verb}: op must be list, tojson, define, link, edit, editviainstance or unlink; got '{op}'.", nameof( a ) );
	}

	static JsonObject Pass2PropPlace( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_prop_place";
		var className = Pass2Opt( a, "class", "prop_static" ) ?? "prop_static";
		var model = Pass2Opt( a, "model" );
		if ( model is not null )
		{
			var okExt = new[] { ".mdl", ".vmdl", ".glb", ".obj", ".fbx", ".effigy" };
			if ( model.Any( char.IsWhiteSpace ) || !okExt.Any( ext => model.EndsWith( ext, StringComparison.OrdinalIgnoreCase ) ) )
				throw new ArgumentException( $"{verb}: model must be a project-relative path ending in .mdl/.vmdl/.glb/.obj/.fbx/.effigy with no whitespace; got '{model}'.", nameof( a ) );
		}
		var vertices = new List<Vec3>();
		var indices = new List<int>();
		foreach ( var brush in Pass2AllBrushes( doc ) )
		{
			brush.ComputeAndStoreVertices();
			foreach ( var face in brush.Faces )
			{
				var loop = new List<Vec3>();
				foreach ( var v in face.StoredVertices ) loop.Add( new Vec3( (float)v.X, (float)v.Y, (float)v.Z ) );
				if ( loop.Count < 3 ) continue;
				int baseIndex = vertices.Count;
				vertices.AddRange( loop );
				for ( int i = 1; i + 1 < loop.Count; i++ )
				{
					indices.Add( baseIndex );
					indices.Add( baseIndex + i );
					indices.Add( baseIndex + i + 1 );
				}
			}
		}
		var surface = MapKit.PropPlacementSurface.FromTriangles( vertices, indices );
		float maxDistance = Pass2Float( a, "maxdistance", 4096f, verb );
		var yaw = Pass2Float( a, "yaw", 0f, verb );
		var roll = Pass2Float( a, "roll", 0f, verb );
		Vec3 origin;
		bool hasRayHit = false;
		var rayHit = default( MapKit.PropPlacementHit );
		if ( Pass2Opt( a, "dropfrom" ) is not null )
		{
			var dropFrom = Pass2Vec3( a, "dropfrom", Vec3.Zero, verb );
			if ( !MapKit.PropPlacement.TryRaycastDown( surface, dropFrom, maxDistance, out rayHit ) )
			{
				var fail = Pass2Obj( verb );
				fail["seated"] = false;
				fail["reason"] = "No floor below the drop point within maxdistance.";
				fail["dropFrom"] = Pass2JVec( dropFrom );
				return fail;
			}
			hasRayHit = true;
			origin = rayHit.Point;
		}
		else origin = Pass2Vec3( a, "origin", Vec3.Zero, verb );
		var pose = MapKit.PropPose.Upright( origin, yaw, roll );
		var options = new MapKit.PropPlacementOptions { MaxDistance = maxDistance };
		var result = MapKit.PropPlacement.MoveToFloor( surface, pose, options );
		var obj = Pass2Obj( verb );
		obj["seated"] = result.Seated;
		obj["reason"] = result.Reason;
		obj["stepsTaken"] = result.StepsTaken;
		obj["distanceDropped"] = (double)result.DistanceDropped;
		obj["pose"] = new JsonObject
		{
			["origin"] = Pass2JVec( result.Pose.Origin ),
			["up"] = Pass2JVec( result.Pose.Up ),
			["yawDegrees"] = (double)result.Pose.YawDegrees,
			["rollDegrees"] = (double)result.Pose.RollDegrees,
		};
		if ( result.Hit is MapKit.PropPlacementHit resultHit )
			obj["hit"] = new JsonObject
			{
				["point"] = Pass2JVec( resultHit.Point ),
				["normal"] = Pass2JVec( resultHit.Normal ),
				["distance"] = (double)resultHit.Distance,
				["triangleIndex"] = resultHit.TriangleIndex,
				["slopeDegrees"] = (double)resultHit.SlopeDegrees,
			};
		else if ( hasRayHit )
			obj["hit"] = new JsonObject
			{
				["point"] = Pass2JVec( rayHit.Point ),
				["normal"] = Pass2JVec( rayHit.Normal ),
				["distance"] = (double)rayHit.Distance,
				["triangleIndex"] = rayHit.TriangleIndex,
				["slopeDegrees"] = (double)rayHit.SlopeDegrees,
			};
		bool seat = Pass2Bool( a, "seat", false, verb );
		if ( seat && result.Seated )
		{
			if ( model is null )
				throw new ArgumentException( $"{verb}: seat=true needs model=<path> for the prop entity.", nameof( a ) );
			var entity = new MapKit.VmfEntity { Id = Pass2NextEntityId( doc ), ClassName = className };
			entity.Set( "origin", $"{result.Pose.Origin.x.ToString( CultureInfo.InvariantCulture )} {result.Pose.Origin.y.ToString( CultureInfo.InvariantCulture )} {result.Pose.Origin.z.ToString( CultureInfo.InvariantCulture )}" );
			entity.Set( "angles", $"0 {result.Pose.YawDegrees.ToString( CultureInfo.InvariantCulture )} 0" );
			entity.Set( "model", model );
			doc.Entities.Add( entity );
			obj["entityId"] = entity.Id;
			Pass2WriteIfOut( doc, a, obj );
			if ( Pass2Opt( a, "out" ) is null )
				obj["note"] = "Pose computed and entity staged in memory, but no file was written: pass out=<path> to persist it.";
		}
		return obj;
	}

	static JsonObject Pass2Propper( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_propper";
		var ids = Pass2IdList( a, "brushes", verb );
		var options = new MapKit.BrushToModelOptions
		{
			ModelName = Pass2Opt( a, "modelName", "prop" ) ?? "prop",
			Scale = Pass2Vec3( a, "scale", new Vec3( 1f, 1f, 1f ), verb ),
			DensityKgPerCubicInch = Pass2Float( a, "density", 0.01f, verb ),
			BakeAo = Pass2Bool( a, "bakeAo", true, verb ),
			AoSize = Pass2Int( a, "aoSize", 64, verb ),
			AoRays = Pass2Int( a, "aoRays", 8, verb ),
			BuildPhysics = Pass2Bool( a, "buildPhysics", true, verb ),
		};
		var brushes = new List<MapBrush>();
		foreach ( var id in ids ) brushes.Add( Pass2MapBrushFromVmf( Pass2FindBrush( doc, id, verb ) ) );
		var result = MapKit.BrushToModel.Convert( brushes, options );
		var obj = Pass2Obj( verb );
		obj["ok"] = result.Ok;
		obj["reason"] = result.Reason;
		obj["message"] = result.Message;
		obj["removedBrushCount"] = result.RemovedBrushCount;
		obj["keptBrushCount"] = result.KeptBrushCount;
		obj["notes"] = Pass2JStrs( result.Notes ?? Array.Empty<string>() );
		if ( result.Asset is not null )
			obj["asset"] = new JsonObject
			{
				["modelName"] = result.Asset.ModelName,
				["materials"] = Pass2JStrs( result.Asset.Materials ?? Array.Empty<string>() ),
			};
		if ( result.Prop is not null )
			obj["prop"] = new JsonObject
			{
				["className"] = MapKit.PropEntity.ClassName,
				["modelName"] = result.Prop.ModelName,
				["origin"] = Pass2JVec( result.Prop.Origin ),
				["scale"] = Pass2JVec( result.Prop.Scale ),
				["targetName"] = result.Prop.TargetName,
			};
		if ( result.Registry is not null ) obj["registry"] = result.Registry.ToString();
		obj["note"] = "Convert does not report WHICH brushes it consumed, so the file verb cannot safely remove them from the map: no file is written. The MCP tool keeps the conversion in its session overlay (integrator seam).";
		return obj;
	}

	static List<HotspotRect> Pass2ParseHotspots( Dictionary<string, string> a, string verb )
	{
		var text = Pass2Req( a, "hotspots", verb );
		JsonNode parsed;
		try { parsed = JsonNode.Parse( text ); }
		catch ( Exception e ) { throw new ArgumentException( $"{verb}: hotspots must be a JSON array: {e.Message}", nameof( a ) ); }
		var arr = parsed?.AsArray() ?? throw new ArgumentException( $"{verb}: hotspots must be a JSON array.", nameof( a ) );
		var rects = new List<HotspotRect>();
		foreach ( var node in arr )
		{
			var o = node?.AsObject() ?? throw new ArgumentException( $"{verb}: each hotspot must be a JSON object.", nameof( a ) );
			rects.Add( new HotspotRect(
				o["name"]?.GetValue<string>() ?? throw new ArgumentException( $"{verb}: each hotspot needs a name.", nameof( a ) ),
				o["materialName"]?.GetValue<string>() ?? "",
				(float)( o["uMin"]?.GetValue<double>() ?? 0 ),
				(float)( o["vMin"]?.GetValue<double>() ?? 0 ),
				(float)( o["uMax"]?.GetValue<double>() ?? 1 ),
				(float)( o["vMax"]?.GetValue<double>() ?? 1 ) ) );
		}
		if ( rects.Count == 0 ) throw new ArgumentException( $"{verb}: hotspots must list at least one rect.", nameof( a ) );
		return rects;
	}

	static JsonObject Pass2JHotspotRect( HotspotRect r ) => new()
	{
		["name"] = r.Name,
		["materialName"] = r.MaterialName,
		["uMin"] = (double)r.UMin,
		["vMin"] = (double)r.VMin,
		["uMax"] = (double)r.UMax,
		["vMax"] = (double)r.VMax,
		["width"] = (double)r.Width,
		["height"] = (double)r.Height,
	};

	static JsonObject Pass2Hotspot( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_hotspot";
		var op = ( Pass2Opt( a, "op", "match" ) ?? "match" ).ToLowerInvariant();
		int brushId = Pass2Int( a, "brush", -1, verb );
		if ( brushId < 0 ) throw new ArgumentException( $"{verb}: brush=<id> is required.", nameof( a ) );
		int faceIndex = Pass2Int( a, "face", 0, verb );
		var brush = Pass2FindBrush( doc, brushId, verb );
		if ( faceIndex < 0 || faceIndex >= brush.Faces.Count )
			throw new ArgumentException( $"{verb}: face {faceIndex} out of range for brush {brushId} ({brush.Faces.Count} faces).", nameof( a ) );
		var face = brush.Faces[faceIndex];
		brush.ComputeAndStoreVertices();
		var verts = new List<Vec3>();
		foreach ( var v in face.StoredVertices ) verts.Add( new Vec3( (float)v.X, (float)v.Y, (float)v.Z ) );
		var normal = new Vec3( (float)face.Normal.X, (float)face.Normal.Y, (float)face.Normal.Z );
		var faceMaterial = new FaceMaterial( face.Material ?? "", new FaceUv( new Vec2( 0f, 0f ), new Vec2( 1f, 1f ), 0f, face.LightmapScale, 0 ) );
		var rects = Pass2ParseHotspots( a, verb );
		var obj = Pass2Obj( verb );
		obj["brush"] = brushId;
		obj["face"] = faceIndex;
		obj["op"] = op;
		if ( op == "match" )
		{
			var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( verts, normal );
			var match = HotspotTexturing.Match( rects, uMax - uMin, vMax - vMin, face.Material ?? "" );
			obj["faceWidth"] = (double)( uMax - uMin );
			obj["faceHeight"] = (double)( vMax - vMin );
			obj["outcome"] = match.Outcome.ToString();
			obj["found"] = match.Found;
			obj["score"] = (double)match.Score;
			obj["rotated"] = match.Rotated;
			obj["faceAspect"] = (double)match.FaceAspect;
			if ( match.Rect is not null ) obj["rect"] = Pass2JHotspotRect( match.Rect );
			return obj;
		}
		FaceMaterial applied;
		HotspotRect used;
		if ( op == "apply" )
		{
			var rectName = Pass2Req( a, "rect", verb );
			used = null;
			foreach ( var r in rects )
				if ( string.Equals( r.Name, rectName, StringComparison.OrdinalIgnoreCase ) ) used = r;
			if ( used is null )
				throw new ArgumentException( $"{verb}: hotspot '{rectName}' not found. Known: {string.Join( ", ", rects.Select( r => r.Name ) )}.", nameof( a ) );
			applied = HotspotTexturing.ApplyHotspot( faceMaterial, verts, normal, used, Pass2Bool( a, "rotated", false, verb ) );
		}
		else if ( op == "applynamed" )
		{
			var hotspotName = Pass2Req( a, "hotspot", verb );
			used = null;
			foreach ( var r in rects )
				if ( string.Equals( r.Name, hotspotName, StringComparison.OrdinalIgnoreCase ) ) used = r;
			applied = HotspotTexturing.ApplyNamedHotspot( faceMaterial, verts, normal, hotspotName, rects );
		}
		else throw new ArgumentException( $"{verb}: op must be match, apply or applynamed; got '{op}'.", nameof( a ) );
		obj["faceMaterial"] = Pass2JFaceMaterial( applied );
		if ( used is not null ) obj["rect"] = Pass2JHotspotRect( used );
		obj["note"] = "The hotspot-applied FaceMaterial is returned; writing its UV back into the .vmf/.map face needs the VmfUvAxis round-trip the map-document-model ticket owns (integrator seam). The MCP tool applies it to the live session face.";
		return obj;
	}

	static JsonObject Pass2Browser( Dictionary<string, string> a, string workDirectory, string mapPath )
	{
		const string verb = "map_browser";
		var kind = ( Pass2Req( a, "kind", verb ) ).ToLowerInvariant();
		if ( kind == "texture" )
			return Pass2NotImplemented( verb, "texture-browser-no-registry",
				"There is no TextureAssetRegistry in the kernel (only Model/Particle/Sound registries). A texture browser needs the asset-registries ticket to land one." );
		if ( kind != "model" && kind != "particle" && kind != "sound" )
			throw new ArgumentException( $"{verb}: kind must be model, particle, sound or texture; got '{kind}'.", nameof( a ) );
		var query = Pass2Req( a, "query", verb );
		int limit = Pass2Int( a, "limit", 20, verb );
		string root = Pass2Opt( a, "root" ) ?? workDirectory;
		if ( string.IsNullOrWhiteSpace( root ) && !string.IsNullOrWhiteSpace( mapPath ) ) root = Path.GetDirectoryName( Path.GetFullPath( mapPath ) );
		if ( string.IsNullOrWhiteSpace( root ) ) root = Directory.GetCurrentDirectory();
		var registries = new MapKit.AssetRegistries( root );
		var rejected = new List<string>();
		if ( Pass2Opt( a, "entries" ) is string entriesJson )
		{
			var parsed = JsonNode.Parse( entriesJson )?.AsArray()
				?? throw new ArgumentException( $"{verb}: entries must be a JSON array.", nameof( a ) );
			foreach ( var node in parsed )
			{
				var o = node?.AsObject() ?? throw new ArgumentException( $"{verb}: each entry must be a JSON object.", nameof( a ) );
				bool ok = kind switch
				{
					"model" => registries.Models.Register( new MapKit.ModelEntry
					{
						Name = o["name"]?.GetValue<string>() ?? "",
						Path = o["path"]?.GetValue<string>() ?? "",
						Attachments = o["attachments"] is JsonArray att ? att.Select( x => x?.GetValue<string>() ?? "" ).ToList() : new List<string>(),
						Scale = o["scale"] is JsonArray sc && sc.Count == 3
							? new Vec3( (float)sc[0]!.GetValue<double>(), (float)sc[1]!.GetValue<double>(), (float)sc[2]!.GetValue<double>() )
							: Vec3.One,
						PropType = o["propType"]?.GetValue<string>() is string pt && Enum.TryParse<MapKit.PropType>( pt, true, out var parsedPt ) ? parsedPt : MapKit.PropType.Static,
					} ),
					"particle" => registries.Particles.Register( new MapKit.ParticleEntry
					{
						SystemName = o["systemName"]?.GetValue<string>() ?? "",
						DefinitionPath = o["definitionPath"]?.GetValue<string>() ?? "",
						Params = o["params"] is JsonObject po ? po.ToDictionary( kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "" ) : new Dictionary<string, string>(),
					} ),
					_ => registries.Sounds.Register( new MapKit.SoundEntry
					{
						Name = o["name"]?.GetValue<string>() ?? "",
						Path = o["path"]?.GetValue<string>() ?? "",
						Volume = (float)( o["volume"]?.GetValue<double>() ?? 1.0 ),
					} ),
				};
				if ( !ok ) rejected.Add( o["name"]?.GetValue<string>() ?? o["systemName"]?.GetValue<string>() ?? "(unnamed)" );
			}
		}
		var results = new JsonArray();
		int count = 0;
		if ( kind == "model" )
		{
			foreach ( var e in registries.Models.Search( query ) )
			{
				if ( count >= limit ) break;
				results.Add( new JsonObject
				{
					["name"] = e.Name,
					["path"] = e.Path,
					["propType"] = e.PropType.ToString(),
					["scale"] = Pass2JVec( e.Scale ),
					["attachments"] = Pass2JStrs( e.Attachments ),
					["usedCount"] = e.UsedCount,
				} );
				count++;
			}
		}
		else if ( kind == "particle" )
		{
			foreach ( var e in registries.Particles.Search( query ) )
			{
				if ( count >= limit ) break;
				var paramsObj = new JsonObject();
				foreach ( var kv in e.Params ) paramsObj[kv.Key] = kv.Value;
				results.Add( new JsonObject
				{
					["systemName"] = e.SystemName,
					["definitionPath"] = e.DefinitionPath,
					["params"] = paramsObj,
					["usedCount"] = e.UsedCount,
				} );
				count++;
			}
		}
		else
		{
			foreach ( var e in registries.Sounds.Search( query ) )
			{
				if ( count >= limit ) break;
				results.Add( new JsonObject
				{
					["name"] = e.Name,
					["path"] = e.Path,
					["volume"] = (double)e.Volume,
					["usedCount"] = e.UsedCount,
				} );
				count++;
			}
		}
		var obj = Pass2Obj( verb );
		obj["kind"] = kind;
		obj["query"] = query;
		obj["resultCount"] = count;
		obj["results"] = results;
		obj["rejectedEntries"] = Pass2JStrs( rejected );
		if ( Pass2Opt( a, "entries" ) is null )
			obj["note"] = "The registry starts empty in a one-shot call: pass entries=[...] to register assets first, or use the MCP tool, whose registry persists across calls. AssetRegistries has no file loader.";
		return obj;
	}

	static MapKit.AssetKind Pass2ParseAssetKind( string text, string verb )
	{
		return text.ToLowerInvariant() switch
		{
			"model" => MapKit.AssetKind.Model,
			"material" => MapKit.AssetKind.Material,
			"sound" => MapKit.AssetKind.Sound,
			"particle" => MapKit.AssetKind.Particle,
			_ => throw new ArgumentException( $"{verb}: kind must be model, material, sound or particle; got '{text}'.", nameof( text ) ),
		};
	}

	static JsonObject Pass2JHotloadAsset( MapKit.HotloadAsset asset ) => new()
	{
		["kind"] = asset.Kind.ToString(),
		["path"] = asset.Path,
		["fullPath"] = asset.FullPath,
		["exists"] = asset.Exists,
		["lastWriteTimeUtc"] = asset.LastWriteTimeUtc.ToString( "o", CultureInfo.InvariantCulture ),
		["length"] = asset.Length,
	};

	static JsonObject Pass2Hotload( Dictionary<string, string> a, string workDirectory, string mapPath )
	{
		const string verb = "map_hotload";
		var op = ( Pass2Opt( a, "op", "list" ) ?? "list" ).ToLowerInvariant();
		string root = Pass2Opt( a, "root" ) ?? workDirectory;
		if ( string.IsNullOrWhiteSpace( root ) && !string.IsNullOrWhiteSpace( mapPath ) ) root = Path.GetDirectoryName( Path.GetFullPath( mapPath ) );
		if ( string.IsNullOrWhiteSpace( root ) ) root = Directory.GetCurrentDirectory();
		var hotload = new MapKit.AssetHotload( root );
		var watched = new List<MapKit.HotloadAsset>();
		if ( Pass2Opt( a, "watches" ) is string watchesJson )
		{
			var parsed = JsonNode.Parse( watchesJson )?.AsArray()
				?? throw new ArgumentException( $"{verb}: watches must be a JSON array.", nameof( a ) );
			foreach ( var node in parsed )
			{
				var o = node?.AsObject() ?? throw new ArgumentException( $"{verb}: each watch must be a JSON object.", nameof( a ) );
				var kind = Pass2ParseAssetKind( o["kind"]?.GetValue<string>() ?? throw new ArgumentException( $"{verb}: each watch needs kind.", nameof( a ) ), verb );
				var path = o["path"]?.GetValue<string>() ?? throw new ArgumentException( $"{verb}: each watch needs path.", nameof( a ) );
				watched.Add( hotload.Watch( kind, path ) );
			}
		}
		var obj = Pass2Obj( verb );
		obj["op"] = op;
		obj["root"] = root;
		if ( op == "watch" || op == "list" )
		{
			var arr = new JsonArray();
			foreach ( var asset in hotload.WatchedAssets ) arr.Add( Pass2JHotloadAsset( asset ) );
			obj["watched"] = arr;
			obj["note"] = "Watches are per-call in the file verb: a one-shot poll can only report changes since this call's watch (usually none). The MCP tool keeps watches across calls, where poll/reload are meaningful.";
			return obj;
		}
		if ( op == "poll" )
		{
			var changes = new JsonArray();
			foreach ( var c in hotload.PollForChanges() )
				changes.Add( new JsonObject
				{
					["asset"] = Pass2JHotloadAsset( c.Asset ),
					["kind"] = c.Kind.ToString(),
					["detectedAtUtc"] = c.DetectedAtUtc.ToString( "o", CultureInfo.InvariantCulture ),
				} );
			obj["changes"] = changes;
			obj["changeCount"] = changes.Count;
			return obj;
		}
		if ( op == "reload" )
		{
			if ( !Pass2Bool( a, "confirm", false, verb ) )
				throw new ArgumentException( $"{verb}: reload is destructive — pass confirm=true.", nameof( a ) );
			var report = hotload.PollAndReload();
			var reloads = new JsonArray();
			foreach ( var r in report.Reloads )
				reloads.Add( new JsonObject
				{
					["asset"] = Pass2JHotloadAsset( r.Asset ),
					["success"] = r.Success,
					["definitionChanged"] = r.DefinitionChanged,
					["error"] = r.Error,
					["changeKind"] = r.ChangeKind.ToString(),
				} );
			obj["changes"] = report.Changes.Count;
			obj["reloads"] = reloads;
			obj["issues"] = Pass2JStrs( report.Issues.Select(i => i.Code + ": " + i.Message) );
			obj["hasIssues"] = report.HasIssues;
			obj["note"] = "Reload without a project HotloadDocument reports what the poll found; full definition reload needs the document the integrator wires in.";
			return obj;
		}
		throw new ArgumentException( $"{verb}: op must be watch, list, poll or reload; got '{op}'.", nameof( a ) );
	}

	static JsonObject Pass2JPlaytestBake( MapKit.PlaytestBake bake ) => new()
	{
		["documentName"] = bake.DocumentName,
		["mapPath"] = bake.MapPath,
		["revision"] = bake.Revision,
		["totalLeaves"] = bake.TotalLeaves,
		["totalFaces"] = bake.TotalFaces,
		["isFull"] = bake.IsFull,
	};

	static JsonObject Pass2JPreLaunch( MapKit.PreLaunchResult validation )
	{
		var issues = new JsonArray();
		foreach ( var i in validation.Issues )
			issues.Add( new JsonObject
			{
				["code"] = i.Code,
				["severity"] = i.Severity.ToString(),
				["message"] = i.Message,
				["entityId"] = i.EntityId,
			} );
		return new JsonObject
		{
			["passed"] = validation.Passed,
			["issues"] = issues,
			["navmeshRequired"] = validation.NavmeshRequired,
			["hasPlayerStart"] = validation.HasPlayerStart,
			["playerStartCount"] = validation.PlayerStartCount,
		};
	}

	static JsonObject Pass2Playtest( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_playtest";
		var op = ( Pass2Opt( a, "op", "validate" ) ?? "validate" ).ToLowerInvariant();
		var gamePath = Pass2Opt( a, "map", "maps/map.bsp" ) ?? "maps/map.bsp";
		bool navmesh = Pass2Bool( a, "navmesh", false, verb );
		bool sessionRunning = Pass2Bool( a, "sessionRunning", false, verb );
		var issueDoc = Pass2ToIssueDocument( doc );
		var obj = Pass2Obj( verb );
		obj["op"] = op;
		obj["map"] = gamePath;
		if ( op == "bake" )
		{
			obj["bake"] = Pass2JPlaytestBake( MapKit.PlaytestLoop.Bake( issueDoc, gamePath ) );
			obj["note"] = "Bake is pure/headless: it computes the bake, it launches nothing.";
			return obj;
		}
		if ( op == "validate" )
		{
			obj["validation"] = Pass2JPreLaunch( MapKit.PlaytestLoop.PreLaunchValidation( issueDoc, navmesh ) );
			return obj;
		}
		if ( op == "rerun" )
		{
			var bake = MapKit.PlaytestLoop.Bake( issueDoc, gamePath );
			var rerun = MapKit.PlaytestLoop.RequestReRun( issueDoc, bake, gamePath, navmesh, sessionRunning );
			obj["issued"] = rerun.Issued;
			obj["bake"] = Pass2JPlaytestBake( bake );
			obj["validation"] = Pass2JPreLaunch( rerun.Validation );
			if ( rerun.Command is not null )
				obj["command"] = new JsonObject
				{
					["mapPath"] = rerun.Command.MapPath,
					["signal"] = new JsonObject
					{
						["changeLevel"] = (rerun.Command.Signal == Effigy.Map.ReRunSignal.ChangeLevel),
						["reloadMap"] = (rerun.Command.Signal == Effigy.Map.ReRunSignal.ReloadMap),
					},
					["reason"] = rerun.Command.Reason,
					["bakeRevision"] = rerun.Command.BakeRevision,
				};
			if ( rerun.Rebake?.Bake is not null ) obj["rebake"] = Pass2JPlaytestBake( rerun.Rebake.Bake );
			obj["note"] = "The re-run command is data (the in-game console command the signal corresponds to), not a process launch. Delivery to a running game is the ops-CLI ticket's job.";
			return obj;
		}
		throw new ArgumentException( $"{verb}: op must be bake, validate or rerun; got '{op}'.", nameof( a ) );
	}

	static JsonObject Pass2Navmesh( MapKit.VmfDocument doc, Dictionary<string, string> a )
	{
		const string verb = "map_navmesh_generate";
		var options = new MapKit.NavMeshGenOptions
		{
			CellSize = Pass2Float( a, "cellSize", 8f, verb ),
			AgentRadius = Pass2Float( a, "agentRadius", 16f, verb ),
		};
		var result = MapKit.NavMeshGen.Generate( Pass2ToQuickPlayMap( doc ), options );
		var obj = Pass2Obj( verb );
		obj["polyCount"] = result.Polys.Count;
		obj["linkCount"] = result.Links.Count;
		obj["hintCount"] = result.Hints.Count;
		obj["regionCount"] = result.RegionCount;
		obj["cellCount"] = result.CellCount;
		obj["walkableCellCount"] = result.WalkableCellCount;
		obj["boundsMin"] = Pass2JVec( result.BoundsMin );
		obj["boundsMax"] = Pass2JVec( result.BoundsMax );
		obj["cellSize"] = (double)result.CellSize;
		obj["agentRadius"] = (double)result.AgentRadius;
		var warnings = new JsonArray();
		foreach ( var w in result.Warnings ) warnings.Add( w.ToString() );
		obj["warnings"] = warnings;
		obj["describe"] = result.Describe();
		bool wantText = Pass2Bool( a, "navtext", false, verb );
		if ( wantText ) obj["navText"] = result.ToNavText();
		else obj["navTextLength"] = result.ToNavText().Length;
		obj["note"] = "This supersedes the pass-1 map_navmesh capability error: NavMeshGen.Generate is the real builder. Non-axis-aligned brushes are refused by name (non-axis-aligned-brush) instead of being silently flattened.";
		return obj;
	}

	// ----- MCP session seams ------------------------------------------------
	// The MCP tool (McpMapPass2Tools.cs) holds a CompileBuildArtefacts across
	// calls so only-entities reuses a prior full build instead of rebuilding
	// it every time. These two methods are that seam: the compile one runs a
	// base preset and hands the artefacts back; the only-entities one runs
	// RunOnlyEntities against held artefacts. Both return the same JSON the
	// verbs return.

	/// <summary>Run a base preset and return its CompileResult JSON, handing
	/// the build artefacts back for a later only-entities run (null when the
	/// base build failed).</summary>
	public static string RunMapPass2CompileWithArtefacts( string mapPath, IReadOnlyDictionary<string, string> args, string workDirectory, out CompileBuildArtefacts artefacts )
	{
		const string verb = "map_compile_preset";
		var a = Pass2Args( args );
		foreach ( var forbidden in new[] { "command", "shell", "exec" } )
			if ( a.ContainsKey( forbidden ) )
				throw new ArgumentException( $"shell-refused: pass-2 verbs never run a shell; '{forbidden}' is not an argument.", nameof( args ) );
		var doc = Pass2LoadVmf( mapPath );
		if ( Pass2PlayerStartOf( doc ) is null )
			throw new ArgumentException( $"{verb}: compile requires an info_player_start in the map.", nameof( args ) );
		var preset = Pass2ParsePreset( a, verb );
		var options = Pass2CompileOptions( mapPath, a, workDirectory, verb );
		var input = Pass2ToCompileInput( doc );
		var result = CompileRunner.Run( input, preset, options );
		artefacts = result.Succeeded ? result.ToArtefacts( input ) : null;
		return Pass2JCompileResult( verb, result ).ToJsonString();
	}

	/// <summary>Run only-entities against artefacts from a prior full build
	/// (see RunMapPass2CompileWithArtefacts). Returns the Only-Entities
	/// CompileResult JSON.</summary>
	public static string RunMapPass2OnlyEntitiesWithArtefacts( string mapPath, CompileBuildArtefacts artefacts, IReadOnlyDictionary<string, string> args, string workDirectory )
	{
		const string verb = "map_compile_only_entities";
		if ( artefacts is null ) throw new ArgumentException( $"{verb}: no prior build artefacts were supplied. Run a base preset first.", nameof( artefacts ) );
		var a = Pass2Args( args );
		foreach ( var forbidden in new[] { "command", "shell", "exec" } )
			if ( a.ContainsKey( forbidden ) )
				throw new ArgumentException( $"shell-refused: pass-2 verbs never run a shell; '{forbidden}' is not an argument.", nameof( args ) );
		var doc = Pass2LoadVmf( mapPath );
		if ( Pass2PlayerStartOf( doc ) is null )
			throw new ArgumentException( $"{verb}: compile requires an info_player_start in the map.", nameof( args ) );
		var options = Pass2CompileOptions( mapPath, a, workDirectory, verb );
		var input = Pass2ToCompileInput( doc );
		var result = CompileRunner.RunOnlyEntities( input, artefacts, options );
		return Pass2JCompileResult( verb, result ).ToJsonString();
	}
}
