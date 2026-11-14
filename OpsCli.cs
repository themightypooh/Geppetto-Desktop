#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

namespace Marionette.Desktop;

/// <summary>
/// Headless Geppetto-ops commands for scripts and agents: the CLI the packaging task
/// assumed but no ticket built (`marionette-desktop ops ...`).
///
///   ops script &lt;file&gt; [--doc in.effigy] [--out DIR] [--run DIR] [--pace S]
///   ops check &lt;model.effigy|model.fbx&gt;
///   ops export &lt;model&gt; --format obj|glb|effigy|riganim --out &lt;path&gt; [--force] [--pose bind|posed]
///   ops render &lt;model&gt; --out &lt;png&gt; [--size WxH] [--iso] [--frames N] [--force]
///   ops test [--filter &lt;name&gt;] [--report &lt;path&gt;] [--list] [--keep-work]
///            [--require-local-fixtures] [--survey &lt;path&gt;] [--tests-bin &lt;path&gt;]
///
/// REUSE ONLY — no new render/export/test/script logic lives here. Each command is a thin
/// wrapper over an existing path: script over ScriptLive.Run (the --script --live path),
/// check over MeshDoctor.PreExportCheck + ExportService.EvaluateCapabilities, export over
/// ExportService.Export, render over HeadlessRender.Run (the --headless path), test over the
/// Desktop.Tests binary (DesktopTestHost.Run inside it). The single allowed entry-point
/// touch is one dispatch branch in Program.cs Main; nothing else in Program.cs is edited.
///
/// PATHS. This is a local CLI, the user's own shell equivalent: file arguments resolve
/// against the current directory, absolute or relative, and are NOT run through the AI
/// run-sandbox (AiSuiteTools.Owned rejects absolute paths and confines reads to one run
/// directory — that sandbox belongs to the networked MCP surface, and the
/// mcp-cli-security-safety-gates ticket owns it). What the ops CLI does enforce: inputs
/// must exist, an --out must never overwrite the source model, and an existing --out is
/// refused unless --force is passed (the scope doc's destructive-op rule).
///
/// OPS TEST AND THE PACKAGE. Desktop.Tests is a separate executable the app cannot link
/// (the tests project references the app, not the other way round), so `ops test` finds
/// the test binary next to the running executable and spawns it, forwarding the filter
/// and report flags and returning its exit code unchanged. The packaging task does NOT
/// publish Desktop.Tests today (package-linux.sh ships only marionette-desktop), so from
/// a package `ops test` reports the probed paths and exits 2 instead of faking a green
/// run; --tests-bin points at a built copy. Publishing the test binary is a packaging
/// gap for the integrator, not a second implementation here.
///
/// EXIT CODES. 0 = the command did what it said. 1 = usage error, missing input, a
/// refused export, a failed check (a mesh hard-fails MeshDoctor or the model has no
/// geometry), a failed script, or a render that did not finish. 2 = `ops test` could
/// not find or start the Desktop.Tests binary, or the test host itself errored (its own
/// exit code, passed through unchanged).
///
/// MAP COMMANDS (`ops map ...`, hammer-ai-ops-cli-map-commands). One more thin wrapper
/// layer, same rule: no new compile/validate/export logic lives here. Each map
/// subcommand loads a .vmf or Valve 220 .map file into the shared VmfDocument model
/// (VmfIo / Map220Io) and projects it onto the kernel input type the command needs:
///
///   map build    .vmf/.map -> QuickPlay.Bake() (worker 21's headless entry point,
///                wrapped, never re-implemented); a script input runs headless via
///                ScriptLive, exactly like `ops script`.
///   map validate / issues
///                VmfDocument -> IssueDocument -> IssueEngine.Run().
///   map compile  VmfDocument -> CompileInput -> CompileRunner.Run().
///   map export   vmf/map -> VmfIo / Map220Io writers; obj/glb -> MapBakeExport
///                (MapBrush -> MapBakeBrush -> bake -> ExportObj / ExportGlb).
///
/// SEAM WITH BuildScript.Map.cs (hammer-ai-buildscript-verbs-map, worker 64, in
/// progress at implementation time — the file does NOT exist on disk yet). That ticket
/// owns the script-level surface: BuildScript verbs over MapDocument, one verb per
/// map capability. This ticket owns the shell-level surface: `ops map ...` over a
/// .vmf/.map file. Both call the same native kernel APIs; neither re-implements the
/// other. When the verbs land, `map build <script>` runs them through ScriptLive with
/// no CLI change.
///
/// MAPDOCUMENT (landed 2026-11-13 16:28, during this ticket:
/// kernel/Effigy/Map/MapDocument.cs + MapDocumentIo.cs). The native working file is
/// the .gmap history document; MapDocumentIo reads/writes .gmap ONLY — it has no
/// VMF/.map import, and MapDocument has exactly one kernel projection
/// (ToIssueDocument). There is no VmfDocument -&gt; MapDocument import and no
/// MapDocument -&gt; CompileInput / QuickPlayMap / MapBakeDocument projection
/// anywhere. So the CLI still loads .vmf/.map into VmfDocument (the only VMF/.map
/// model) and projects VmfDocument onto each kernel input type directly — the
/// VmfDocument projections here are the single adapter. When a VmfDocument -&gt;
/// MapDocument import lands, validate/issues can route through
/// MapDocument.ToIssueDocument (same issues; brush/entity ids become document
/// indices instead of the VMF ids used here), and the other projections collapse
/// to MapDocument -&gt; kernel type. No such collapse is attempted here.
///
/// NAMED ERRORS. Every map command prints one JSON object on stdout (result or
/// failure) and exits 0/1; the failure object carries error.code, one of:
/// missing-player-start (compile: no info_player_start — the CLI hard-fails this,
/// where CompileRunner only warns and skips the leak check), no-player-start /
/// empty-map / invalid-brush / invalid-displacement / too-complex (QuickPlay
/// codes, passed through from QuickPlayException), leak (build unsealed, or the
/// compile BSP stage's leak issue), non-axis-aligned-brush (the QuickPlay
/// projection rejects a non-axis-aligned brush instead of silently flattening
/// it, per QuickPlay's own adapter rule), invalid-brush (an export whose bake
/// reports MapBrush defects), bake-issue (any other bake problem on export).
/// Usage errors, missing files and unparsable maps throw like the other ops
/// commands (ArgumentException / FileNotFoundException / MapParseException).
///
/// ISSUES vs VALIDATE. Both run the same IssueEngine pass. `map issues` is a
/// listing: it exits 0 whenever the check ran, even with errors present.
/// `map validate` is a gate: it exits 1 when any error-severity issue exists.
/// A map with no player start validates clean on the leak rules (LeakProbe
/// reports nothing with no origin to flood from) — that gap is the compile
/// gate's job, not validate's.
///
/// KNOWN LOSSES (reported, never silent). Per-face Valve UV axes are not yet
/// projected onto FaceUv, so obj/glb export bakes with the default UV; the
/// material names and planes are exact. Displacement offsets are not projected
/// (no VmfDisplacement -> Displacement adapter exists yet): displacement faces
/// bake as their flat brush faces, and the export JSON warns when any brush
/// has a displacement. Only point `light` entities are projected to
/// CompileLight (colour = _light rgb/255, intensity = its 4th component);
/// light_spot / light_environment are not. GLB export goes through
/// MapBakeExport.ExportGlb, whose writer is minimal by its own GlbNote — the
/// note is repeated in the export JSON.
/// </summary>
public static class OpsCli
{
	public const string Usage = "marionette-desktop ops <command> [options]\n" +
		"\n" +
		"Commands:\n" +
		"  script <file> [--doc in.effigy] [--out DIR] [--run DIR] [--pace S]\n" +
		"      Run a BuildScript file headless (same as --script <file> --live).\n" +
		"  check <model.effigy|model.fbx>\n" +
		"      Validate + topology + export-validate as a JSON report on stdout.\n" +
		"  export <model> --format obj|glb|effigy|riganim --out <path> [--force] [--pose bind|posed]\n" +
		"      --force is also accepted as --allow-destructive (the safety-gates spelling).\n" +
		"      Export via ExportService (capability-checked; vmdl/dmx/fbx are refused).\n" +
		"  render <model> --out <png> [--size WxH] [--iso] [--frames N] [--force]\n" +
		"      Headless EGL render (same path as --headless).\n" +
		"  test [--filter <name>] [--report <path>] [--list] [--keep-work]\n" +
		"       [--require-local-fixtures] [--survey <path>] [--tests-bin <path>]\n" +
		"      Run the Desktop.Tests subset (exit code is 0 only when every selected check passes).\n" +
		"  map <subcommand> [options]\n" +
		"      Hammer map commands (native kernel, no Hammer/VBSP/VVIS/VRAD):\n" +
		"      map build <script|map> [--doc in.effigy] [--out DIR] [--run DIR] [--pace S]\n" +
		"          Build a map: a script runs headless (BuildScript map verbs when they\n" +
		"          land); a .vmf/.map runs the QuickPlay bake (sealed + spawn + walk).\n" +
		"      map validate <map>\n" +
		"          IssueEngine full check as JSON; exit 1 when any error-severity issue.\n" +
		"      map compile <map> --preset quick|preview|final [--out DIR]\n" +
		"          Native CompileRunner; exit 1 on failure. Missing player start is a\n" +
		"          named error (the runner only warns about it).\n" +
		"      map export <map> --format vmf|map|obj|glb --out <path> [--force]\n" +
		"          VMF/.map round-trip, or a baked OBJ/GLB via MapBakeExport.\n" +
		"      map issues <map>\n" +
		"          IssueEngine issue list as JSON; exit 0 when the check ran.\n" +
		"\n" +
		"Paths resolve against the current directory (absolute or relative). An --out never\n" +
		"overwrites the source model, and an existing --out needs --force.\n" +
		"ops --help (or ops with no command) shows this text.\n";

	public static int Run( string[] args )
	{
		if ( args.Length == 0 || args[0] == "--help" || args[0] == "help" ) { Console.WriteLine( Usage ); return 0; }
		return args[0] switch
		{
			"script" => Script( args[1..] ),
			"check" => Check( args[1..] ),
			"export" => Export( args[1..] ),
			"render" => Render( args[1..] ),
			"test" => Test( args[1..] ),
			"map" => Map( args[1..] ),
			_ => throw new ArgumentException( $"Unknown ops command '{args[0]}'. {Usage}" ),
		};
	}

	static string Value( string[] args, ref int i, string flag )
	{
		if ( i + 1 >= args.Length || args[i + 1].StartsWith( "--", StringComparison.Ordinal ) )
			throw new ArgumentException( $"{flag} needs a value." );
		return args[++i];
	}

	static int Positive( string[] args, ref int i, string flag )
	{
		string text = Value( args, ref i, flag );
		if ( !int.TryParse( text, out int n ) || n <= 0 ) throw new ArgumentException( $"{flag} requires a positive integer." );
		return n;
	}

	/// <summary>The four real export targets. vmdl/dmx/fbx are refused by name, never
	/// silently mapped to something else: FBX has no animation export here (only the static
	/// FbxWriter), and vmdl/dmx are engine targets, not ops exports.</summary>
	public static ExportFormat ParseFormat( string value ) => value.ToLowerInvariant() switch
	{
		"obj" => ExportFormat.Obj,
		"glb" => ExportFormat.Glb,
		"effigy" => ExportFormat.Effigy,
		"riganim" => ExportFormat.RigAnim,
		"vmdl" or "dmx" or "fbx" => throw new ArgumentException( $"--format '{value}' is honestly refused: {value} is not an export target. Supported: obj, glb, effigy, riganim." ),
		_ => throw new ArgumentException( $"Unknown --format '{value}'. Supported: obj, glb, effigy, riganim." ),
	};

	/// <summary>Load a model the way the viewer does: .effigy through StudioCache, .fbx
	/// through FbxImport. Returns the retained session plus the visible, non-empty meshes
	/// (same gate ExportService applies before export: no references, no hidden bodies,
	/// no empty meshes). Anything else is refused, not guessed.</summary>
	public static (DocumentSession Session, List<KeyValuePair<string, PolyMesh>> Meshes) LoadModel( string path )
	{
		string full = Path.GetFullPath( path );
		if ( !File.Exists( full ) ) throw new FileNotFoundException( "Model file not found.", full );
		if ( full.EndsWith( ".effigy", StringComparison.OrdinalIgnoreCase ) )
		{
			var studio = StudioCache.Load( full );
			var meshes = new List<KeyValuePair<string, PolyMesh>>();
			foreach ( var body in studio.Bodies )
				if ( body is not null && !body.IsReference && body.Visible && body.Mesh is not null && body.Mesh.FaceCount > 0 )
					meshes.Add( new KeyValuePair<string, PolyMesh>( body.Name ?? body.Id ?? "body", body.Mesh ) );
			return (DocumentSession.CreateEffigy( full, studio ), meshes);
		}
		if ( full.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) )
		{
			var result = FbxImport.Read( File.ReadAllBytes( full ) );
			var meshes = new List<KeyValuePair<string, PolyMesh>>();
			foreach ( var piece in result.Pieces )
				if ( piece.Mesh is not null && piece.Mesh.FaceCount > 0 )
					meshes.Add( new KeyValuePair<string, PolyMesh>( piece.Name ?? "piece", piece.Mesh ) );
			return (DocumentSession.CreateFbx( full, result ), meshes);
		}
		throw new ArgumentException( "Model must be an .effigy or .fbx source file." );
	}

	/// <summary>The check report: per-body MeshDoctor decisions plus per-format export
	/// capability. The report FAILS when a mesh hard-fails MeshDoctor or the model has no
	/// geometry at all. Export capability is reported, not blocking: a static cube cannot
	/// export a RigAnim (it has no clip) and that is not a broken model.</summary>
	public static (string Json, bool Blocked) CheckReport( DocumentSession session, string modelPath, IReadOnlyList<KeyValuePair<string, PolyMesh>> meshes )
	{
		bool blocked = meshes.Count == 0;
		var issues = new List<string>();
		if ( meshes.Count == 0 ) issues.Add( "no meshes in model" );
		var bodyReports = new List<object>();
		foreach ( var (name, mesh) in meshes )
		{
			var decision = MeshDoctor.PreExportCheck( mesh, name );
			if ( decision.Block ) blocked = true;
			var r = decision.Report;
			bodyReports.Add( new
			{
				name,
				vertices = r.Vertices, faces = r.Faces, triangles = r.Triangles, quads = r.Quads, ngons = r.Ngons,
				boundaryEdges = r.BoundaryEdges, holes = r.Holes, closedLoops = r.ClosedLoops, openChains = r.OpenChains,
				nonManifoldEdges = r.NonManifoldEdges, nonManifoldVertices = r.NonManifoldVertices,
				degenerateFaces = r.DegenerateFaces, zeroAreaFaces = r.ZeroAreaFaces, duplicateFaces = r.DuplicateFaces,
				flippedFaces = r.FlippedFaces, looseVertices = r.LooseVertices, shells = r.Shells, euler = r.Euler,
				quadPercent = r.QuadPercent, isClosed = r.IsClosed, isManifold = r.IsManifold, isOutward = r.IsOutward,
				hardFails = r.HardFails, warnings = r.Warnings, blocksExport = r.BlocksExport, isClean = r.IsClean,
				summary = decision.Summary,
			} );
		}
		var formats = new List<object>();
		var service = new ExportService();
		string dir = Path.GetDirectoryName( Path.GetFullPath( modelPath ) ) ?? ".";
		foreach ( var format in new[] { ExportFormat.Obj, ExportFormat.Glb, ExportFormat.Effigy, ExportFormat.RigAnim } )
		{
			string ext = format switch { ExportFormat.Obj => "obj", ExportFormat.Glb => "glb", ExportFormat.Effigy => "effigy", _ => "riganim" };
			// Dummy destination, never written: capability evaluation only needs the request.
			var caps = service.EvaluateCapabilities( new ExportRequest( session, format, Path.Combine( dir, "ops-check." + ext ) ) );
			formats.Add( new
			{
				format = ext, canExport = caps.CanExport, errorMessage = caps.ErrorMessage,
				preservesGeometry = caps.PreservesGeometry, preservesRig = caps.PreservesRig,
				preservesSkinning = caps.PreservesSkinning, preservesMorphs = caps.PreservesMorphs,
				preservesAnimation = caps.PreservesAnimation, preservesTangents = caps.PreservesTangents,
				preservesTextures = caps.PreservesTextures, unitsAndAxes = caps.UnitsAndAxes,
				preserved = caps.PreservedFeatures, lost = caps.LostFeatures,
				warnings = caps.Warnings, errors = caps.Errors,
			} );
		}
		var report = new
		{
			command = "check",
			model = Path.GetFullPath( modelPath ),
			kind = Path.GetFullPath( modelPath ).EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) ? "fbx" : "effigy",
			bodies = meshes.Count,
			issues,
			topology = new { blocked, bodies = bodyReports },
			export = formats,
			status = blocked ? "failed" : "passed",
		};
		return (JsonSerializer.Serialize( report, new JsonSerializerOptions { WriteIndented = true } ), blocked);
	}

	static int Script( string[] args )
	{
		string? script = null;
		var pass = new List<string>();
		for ( int i = 0; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
				case "--doc": pass.Add( "--doc" ); pass.Add( Value( args, ref i, "--doc" ) ); break;
				case "--out": pass.Add( "--out" ); pass.Add( Value( args, ref i, "--out" ) ); break;
				case "--run": pass.Add( "--run" ); pass.Add( Value( args, ref i, "--run" ) ); break;
				case "--pace": pass.Add( "--pace" ); pass.Add( Value( args, ref i, "--pace" ) ); break;
				default:
					if ( args[i].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Unknown script option: {args[i]}" );
					if ( script is not null ) throw new ArgumentException( "script takes exactly one file." );
					script = args[i];
					break;
			}
		}
		if ( script is null ) throw new ArgumentException( "usage: ops script <file> [--doc in.effigy] [--out DIR] [--run DIR] [--pace S]" );
		// ops script is always a live run: --live is implied, never asked for.
		pass.Insert( 0, script );
		pass.Add( "--live" );
		return ScriptLive.Run( pass.ToArray() );
	}

	static int Check( string[] args )
	{
		string? model = null;
		for ( int i = 0; i < args.Length; i++ )
		{
			if ( args[i].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Unknown check option: {args[i]}" );
			if ( model is not null ) throw new ArgumentException( "check takes exactly one model." );
			model = args[i];
		}
		if ( model is null ) throw new ArgumentException( "usage: ops check <model.effigy|model.fbx>" );
		var (session, meshes) = LoadModel( model );
		var (json, blocked) = CheckReport( session, model, meshes );
		Console.WriteLine( json );
		return blocked ? 1 : 0;
	}

	static int Export( string[] args )
	{
		string? model = null, formatText = null, outPath = null, poseText = null;
		bool force = false;
		for ( int i = 0; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
				case "--format": formatText = Value( args, ref i, "--format" ); break;
				case "--out": outPath = Value( args, ref i, "--out" ); break;
				case "--pose": poseText = Value( args, ref i, "--pose" ); break;
				case "--force":
				case "--allow-destructive": force = true; break;
				default:
					if ( args[i].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Unknown export option: {args[i]}" );
					if ( model is not null ) throw new ArgumentException( "export takes exactly one model." );
					model = args[i];
					break;
			}
		}
		if ( model is null || formatText is null || outPath is null )
			throw new ArgumentException( "usage: ops export <model> --format obj|glb|effigy|riganim --out <path> [--force] [--pose bind|posed]" );
		var format = ParseFormat( formatText );
		var pose = poseText is null ? PoseMode.BindPose : poseText.ToLowerInvariant() switch
		{
			"bind" => PoseMode.BindPose,
			"posed" => PoseMode.Posed,
			_ => throw new ArgumentException( "--pose must be bind or posed." ),
		};
		string outFull = Path.GetFullPath( outPath );
		if ( string.Equals( outFull, Path.GetFullPath( model ), StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( "--out must not overwrite the source model." );
		if ( File.Exists( outFull ) && !force )
			throw new ArgumentException( $"--out already exists: {outFull}. Pass --force to overwrite." );
		var (session, _) = LoadModel( model );
		var request = new ExportRequest( session, format, outFull, pose,
			overwritePolicy: force ? OverwritePolicy.Overwrite : OverwritePolicy.FailIfExists );
		var result = new ExportService().Export( request );
		if ( !result.Success )
		{
			Console.Error.WriteLine( "export refused: " + ( result.ErrorMessage ?? "capability evaluation failed." ) );
			var caps = result.CapabilityReport;
			if ( caps is not null )
			{
				foreach ( var error in caps.Errors ) Console.Error.WriteLine( "  error: " + error );
				if ( caps.LostFeatures.Count > 0 ) Console.Error.WriteLine( "  lost: " + string.Join( ", ", caps.LostFeatures ) );
			}
			return 1;
		}
		Console.WriteLine( "exported: " + result.DestinationPath );
		if ( result.GeneratedFiles is { Count: > 0 } )
			foreach ( var file in result.GeneratedFiles ) Console.WriteLine( "  " + file );
		return 0;
	}

	static int Render( string[] args )
	{
		string? model = null, outPath = null, sizeText = null;
		bool force = false, iso = false;
		int frames = 1;
		for ( int i = 0; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
				case "--out": outPath = Value( args, ref i, "--out" ); break;
				case "--size": sizeText = Value( args, ref i, "--size" ); break;
				case "--iso": iso = true; break;
				case "--force":
				case "--allow-destructive": force = true; break;
				case "--frames": frames = Positive( args, ref i, "--frames" ); break;
				default:
					if ( args[i].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Unknown render option: {args[i]}" );
					if ( model is not null ) throw new ArgumentException( "render takes exactly one model." );
					model = args[i];
					break;
			}
		}
		if ( model is null || outPath is null )
			throw new ArgumentException( "usage: ops render <model> --out <png> [--size WxH] [--iso] [--frames N] [--force]" );
		int width = 1600, height = 900;
		if ( sizeText is not null )
		{
			var size = sizeText.Split( 'x' );
			if ( size.Length != 2 || !int.TryParse( size[0], out width ) || !int.TryParse( size[1], out height )
				|| width <= 0 || height <= 0 || width > 8192 || height > 8192 )
				throw new ArgumentException( "--size must be WxH, each side 1..8192." );
		}
		string outFull = Path.GetFullPath( outPath );
		if ( string.Equals( outFull, Path.GetFullPath( model ), StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( "--out must not overwrite the source model." );
		if ( File.Exists( outFull ) && !force )
			throw new ArgumentException( $"--out already exists: {outFull}. Pass --force to overwrite." );
		var options = new DesktopOptions
		{
			Model = Path.GetFullPath( model ), Headless = true, Screenshot = outFull,
			Width = width, Height = height, Iso = iso, Frames = frames,
		};
		return HeadlessRender.Run( options );
	}

	/// <summary>Where the Desktop.Tests binary lives: next to this executable in the
	/// package, then the dev-checkout build outputs. Returns null when nothing is found;
	/// the caller reports the probed paths and exits 2 rather than faking a green run.</summary>
	public static string? DiscoverTestsBinary( out IReadOnlyList<string> probed )
	{
		string baseDir = AppContext.BaseDirectory;
		var candidates = new List<string>
		{
			Path.Combine( baseDir, "Desktop.Tests" ),
			Path.Combine( baseDir, "Desktop.Tests.exe" ),
			Path.Combine( baseDir, "tests", "Desktop.Tests" ),
			Path.GetFullPath( Path.Combine( baseDir, "..", "..", "..", "tests", "bin", "Debug", "net8.0", "Desktop.Tests" ) ),
			Path.GetFullPath( Path.Combine( baseDir, "..", "..", "..", "tests", "bin", "Release", "net8.0", "Desktop.Tests" ) ),
		};
		foreach ( var candidate in candidates )
			if ( File.Exists( candidate ) ) { probed = candidates; return candidate; }
		probed = candidates;
		return null;
	}

	static int Test( string[] args )
	{
		string? filter = null, report = null, survey = null, testsBin = null;
		bool list = false, keepWork = false, requireLocalFixtures = false;
		for ( int i = 0; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
				case "--filter": filter = Value( args, ref i, "--filter" ); break;
				case "--report": report = Value( args, ref i, "--report" ); break;
				case "--survey": survey = Value( args, ref i, "--survey" ); break;
				case "--tests-bin": testsBin = Value( args, ref i, "--tests-bin" ); break;
				case "--list": list = true; break;
				case "--keep-work": keepWork = true; break;
				case "--require-local-fixtures": requireLocalFixtures = true; break;
				default: throw new ArgumentException( $"Unknown test option: {args[i]}" );
			}
		}
		// The test host lives in the Desktop.Tests executable, which this assembly cannot
		// link (the tests project references the app). Spawn it, forward the flags, and
		// return its exit code unchanged. No shell: fixed binary path, ArgumentList only.
		string? binary;
		IReadOnlyList<string> probed = Array.Empty<string>();
		if ( testsBin is not null ) binary = Path.GetFullPath( testsBin );
		else binary = DiscoverTestsBinary( out probed );
		if ( binary is null )
		{
			Console.Error.WriteLine( "ops test: Desktop.Tests binary not found. Probed:" );
			foreach ( var path in probed ) Console.Error.WriteLine( "  " + path );
			Console.Error.WriteLine( "The packaging task must publish Desktop.Tests next to marionette-desktop for ops test to run from the package; pass --tests-bin <path> to point at a built copy." );
			return 2;
		}
		if ( !File.Exists( binary ) ) throw new FileNotFoundException( "Desktop.Tests binary not found.", binary );
		var start = new ProcessStartInfo { FileName = binary, UseShellExecute = false };
		if ( filter is not null ) { start.ArgumentList.Add( "--filter" ); start.ArgumentList.Add( filter ); }
		if ( report is not null ) { start.ArgumentList.Add( "--report" ); start.ArgumentList.Add( report ); }
		if ( survey is not null ) { start.ArgumentList.Add( "--survey" ); start.ArgumentList.Add( survey ); }
		if ( list ) start.ArgumentList.Add( "--list" );
		if ( keepWork ) start.ArgumentList.Add( "--keep-work" );
		if ( requireLocalFixtures ) start.ArgumentList.Add( "--require-local-fixtures" );
		using var process = Process.Start( start ) ?? throw new InvalidOperationException( "Could not start Desktop.Tests." );
		process.WaitForExit();
		return process.ExitCode;
	}

	// ========================================================================
	// ops map — Hammer map commands over a .vmf / Valve 220 .map file.
	// See the class header for the seam, the named errors and the known losses.
	// ========================================================================

	public const string MapUsage = "marionette-desktop ops map <subcommand> [options]\n" +
		"\n" +
		"Subcommands:\n" +
		"  map build <script|map> [--doc in.effigy] [--out DIR] [--run DIR] [--pace S]\n" +
		"      Build a map: a script runs headless (BuildScript map verbs when they land);\n" +
		"      a .vmf/.map runs the QuickPlay bake (sealed + spawn + walk).\n" +
		"  map validate <map>\n" +
		"      IssueEngine full check as JSON; exit 1 when any error-severity issue.\n" +
		"  map compile <map> --preset quick|preview|final [--out DIR]\n" +
		"      Native CompileRunner; exit 1 on failure. Missing player start is a\n" +
		"      named error (the runner only warns about it).\n" +
		"  map export <map> --format vmf|map|obj|glb --out <path> [--force]\n" +
		"      VMF/.map round-trip, or a baked OBJ/GLB via MapBakeExport.\n" +
		"  map issues <map>\n" +
		"      IssueEngine issue list as JSON; exit 0 when the check ran.\n" +
		"\n" +
		"Every map command prints one JSON object on stdout (result or failure).\n" +
		"ops map --help (or ops map with no subcommand) shows this text.\n";

	/// <summary>A named map failure: <see cref="Code"/> is the machine-readable error
	/// (missing-player-start, non-axis-aligned-brush, invalid-brush, bake-issue),
	/// <see cref="Exception.Message"/> says it in words. QuickPlay failures arrive as
	/// <see cref="QuickPlayException"/> instead, which already carries a Code.</summary>
	public sealed class MapCommandException : Exception
	{
		public string Code { get; }
		public MapCommandException( string code, string message ) : base( message ) { Code = code; }
	}

	/// <summary>The four map export targets. vmf/map are the interchange writers;
	/// obj/glb go through the MapBakeExport bake.</summary>
	public enum MapExportFormat { Vmf, Map, Obj, Glb }

	public static MapExportFormat ParseMapFormat( string value ) => value.ToLowerInvariant() switch
	{
		"vmf" => MapExportFormat.Vmf,
		"map" => MapExportFormat.Map,
		"obj" => MapExportFormat.Obj,
		"glb" => MapExportFormat.Glb,
		_ => throw new ArgumentException( $"Unknown map --format '{value}'. Supported: vmf, map, obj, glb." ),
	};

	/// <summary>quick = BSP only, preview = BSP + fast visibility + low-res lightmap,
	/// final = BSP + full visibility + full-res lightmap (CompilePresetDef).</summary>
	public static CompilePreset ParsePreset( string value ) => value.ToLowerInvariant() switch
	{
		"quick" => CompilePreset.QuickPlay,
		"preview" => CompilePreset.Preview,
		"final" => CompilePreset.Final,
		_ => throw new ArgumentException( $"Unknown --preset '{value}'. Supported: quick, preview, final." ),
	};

	static string Json( object value ) => JsonSerializer.Serialize( value, new JsonSerializerOptions { WriteIndented = true } );

	static object VecJson( Vec3 v ) => new { x = v.x, y = v.y, z = v.z };

	static string ErrorJson( string command, string code, string message, string? map = null ) => Json( new
	{
		command,
		map = map is null ? null : Path.GetFullPath( map ),
		status = "failed",
		error = new { code, message },
	} );

	static object IssueJson( Issue issue ) => new
	{
		kind = issue.Kind.ToString(),
		severity = issue.Severity.ToString(),
		code = issue.Code,
		message = issue.Message,
		fixHint = issue.FixHint,
		brushId = issue.BrushId,
		entityId = issue.EntityId,
		location = issue.HasLocation ? VecJson( issue.Location ) : null,
	};

	/// <summary>Parse a Source "x y z" vector (entity origin / angles). Null, missing
	/// or unparsable input is not a value: the caller decides the default.</summary>
	public static bool TryParseVec3( string? text, out Vec3 value )
	{
		value = Vec3.Zero;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return false;
		if ( !double.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x ) ||
			!double.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y ) ||
			!double.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z ) ) return false;
		value = new Vec3( (float)x, (float)y, (float)z );
		return true;
	}

	static Vec3 EntityOrigin( VmfEntity entity ) =>
		entity is not null && TryParseVec3( entity.Get( "origin" ), out var origin ) ? origin : Vec3.Zero;

	static bool IsDetailEntity( VmfEntity entity ) =>
		entity is not null && string.Equals( entity.ClassName, "func_detail", StringComparison.OrdinalIgnoreCase );

	/// <summary>Load a map file into the shared transfer model: .vmf through VmfIo,
	/// Valve 220 .map through Map220Io. Anything else is refused, not guessed.</summary>
	public static VmfDocument LoadMap( string path )
	{
		string full = Path.GetFullPath( path );
        if (!full.EndsWith(".vmf", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Map must be a .vmf or .map source file.");
		if ( !File.Exists( full ) ) throw new FileNotFoundException( "Map file not found.", full );
		if ( full.EndsWith( ".vmf", StringComparison.OrdinalIgnoreCase ) ) return VmfIo.ParseFile( full );
		if ( full.EndsWith( ".map", StringComparison.OrdinalIgnoreCase ) ) return Map220Io.ParseFile( full );
		throw new ArgumentException( "Map must be a .vmf or .map source file." );
	}

	/// <summary>Origin of the first info_player_start, or null when the map has none.
	/// The compile command hard-fails on null; the IssueEngine leak rules report
	/// nothing without an origin to flood from (LeakProbe.PlayerStart).</summary>
	public static Vec3? PlayerStartOf( VmfDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		foreach ( var entity in doc.Entities )
			if ( entity is not null && string.Equals( entity.ClassName, "info_player_start", StringComparison.OrdinalIgnoreCase ) )
				return EntityOrigin( entity );
		return null;
	}

	/// <summary>Project a map file onto the IssueEngine's document: one IssueBrush per
	/// brush (world, then brush entities; func_detail brushes marked detail), one
	/// IssueEntity per entity with its keyvalues and brush ids. Face polygons are
	/// solved from the planes first (VmfBrush.ComputeAndStoreVertices, the no-drift
	/// record) so the engine's geometry rules and the leak probe see real vertices.
	/// A face whose polygon cannot be solved contributes no vertices; the engine
	/// reports that brush as invalid instead of the projection inventing any.</summary>
	public static IssueDocument ToIssueDocument( VmfDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var outDoc = new IssueDocument { Name = "map" };
		void AddBrush( VmfBrush brush, bool isDetail )
		{
			brush.ComputeAndStoreVertices();
			var faces = new List<IssueFace>();
			foreach ( var face in brush.Faces )
			{
				var verts = new List<Vec3>();
				foreach ( var v in face.StoredVertices ) verts.Add( new Vec3( (float)v.X, (float)v.Y, (float)v.Z ) );
				faces.Add( new IssueFace(
					new Vec3( (float)face.Normal.X, (float)face.Normal.Y, (float)face.Normal.Z ),
					face.Distance, face.Material ?? "", verts ) );
			}
			outDoc.Brushes.Add( new IssueBrush( brush.Id, faces, isDetail ) );
		}
		foreach ( var brush in doc.WorldBrushes ) AddBrush( brush, false );
		foreach ( var entity in doc.Entities )
			foreach ( var brush in entity.Brushes ) AddBrush( brush, IsDetailEntity( entity ) );
		foreach ( var entity in doc.Entities )
		{
			var issueEntity = new IssueEntity( entity.Id, entity.ClassName ?? "", EntityOrigin( entity ) );
			foreach ( var kv in entity.KeyValues ) issueEntity.SetKeyValue( kv.Key, kv.Value );
			foreach ( var brush in entity.Brushes ) issueEntity.BrushIds.Add( brush.Id );
			outDoc.Entities.Add( issueEntity );
		}
		return outDoc;
	}

	/// <summary>Run the IssueEngine over a map file. New engine, default rules.</summary>
	public static IReadOnlyList<Issue> MapIssues( VmfDocument doc ) => new IssueEngine().Run( ToIssueDocument( doc ) );

	/// <summary>Project a map file onto a CompileInput: one CompileBrush per brush
	/// (plane faces with per-face material + lightmap scale), the player start, and
	/// one CompileLight per point `light` entity (_light "r g b [intensity]" ->
	/// colour rgb/255, intensity 4th component or 1). light_spot and
	/// light_environment are NOT projected (a spot without a direction is wrong,
	/// not approximate). A null PlayerStart is preserved: the runner warns and
	/// skips the leak check, and the compile command hard-fails before running.</summary>
	public static CompileInput ToCompileInput( VmfDocument doc, string name = "map" )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var input = new CompileInput( name );
		void AddBrush( VmfBrush brush, bool isDetail )
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
			foreach ( var brush in entity.Brushes ) AddBrush( brush, IsDetailEntity( entity ) );
			if ( !string.Equals( entity.ClassName, "light", StringComparison.OrdinalIgnoreCase ) ) continue;
			var colour = new Vec3( 1, 1, 1 );
			float intensity = 1f;
			// _light is "r g b [brightness]": 3 or 4 components. TryParseVec3
			// takes exactly 3, so split here — a 4-component value must not
			// fall through to the white/1 defaults.
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
			input.Lights.Add( new CompileLight( EntityOrigin( entity ), intensity, colour ) );
		}
		var start = PlayerStartOf( doc );
		if ( start.HasValue ) input.PlayerStart = start.Value;
		return input;
	}

	/// <summary>Project a map file onto a QuickPlayMap: one axis-aligned box per
	/// brush. A brush is an axis-aligned box exactly when it has 6 faces whose
	/// normals are +/-X, +/-Y, +/-Z each once; min/max come from the plane
	/// distances (solid is n.p &lt;= d, so max.x = d(+X) and min.x = -d(-X)).
	/// Anything else throws a named <see cref="MapCommandException"/>
	/// (non-axis-aligned-brush) instead of being silently flattened — QuickPlay's
	/// own adapter rule. func_detail brushes are detail; other brush entities
	/// count as world (they are solid in the map). Displacements are NOT
	/// projected: the brush planes still bake, the heightfield does not.</summary>
	public static QuickPlayMap ToQuickPlayMap( VmfDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var map = new QuickPlayMap();
		void AddBrush( VmfBrush brush, bool isDetail )
		{
			map.Brushes.Add( ToQuickPlayBrush( brush, isDetail ) );
		}
		foreach ( var brush in doc.WorldBrushes ) AddBrush( brush, false );
		foreach ( var entity in doc.Entities )
		{
			foreach ( var brush in entity.Brushes ) AddBrush( brush, IsDetailEntity( entity ) );
			float yaw = 0f;
			if ( TryParseVec3( entity.Get( "angles" ), out var angles ) ) yaw = angles.y;
			map.Entities.Add( new QuickPlayEntity( entity.ClassName ?? "", EntityOrigin( entity ), yaw ) );
		}
		return map;
	}

	static QuickPlayBrush ToQuickPlayBrush( VmfBrush brush, bool isDetail )
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
			throw new MapCommandException( "non-axis-aligned-brush",
				$"non-axis-aligned-brush: brush {brush.Id} has {brush.Faces.Count} faces, not the 6 of an axis-aligned box." );
		foreach ( var face in brush.Faces )
		{
			var n = face.Normal;
			if ( !IsAxis( n.X, n.Y, n.Z, out int axis, out double sign ) || !seen.Add( axis + ":" + sign ) )
				throw new MapCommandException( "non-axis-aligned-brush",
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

	/// <summary>Project a map file onto the bake-side brush list: one MapBrush per
	/// brush, planes + material names, default UV (per-face Valve UV axes are not
	/// projected yet — see the class header). MapBrush.Validate() defects are
	/// reported by the bake itself; this method does not pre-filter.
	/// The face type is fully qualified (Effigy.MapBrushFace): Displacement.cs
	/// defines a different Effigy.Map.MapBrushFace, so the unqualified name is
	/// ambiguous in this file — same rule CadToMap.cs / MapBakeExport.cs follow.</summary>
	public static IReadOnlyList<MapBrush> ToMapBrushes( VmfDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var brushes = new List<MapBrush>();
		void AddBrush( VmfBrush brush )
		{
			var faces = new List<Effigy.MapBrushFace>();
			foreach ( var face in brush.Faces )
				faces.Add( new Effigy.MapBrushFace(
					new Vec3( (float)face.Normal.X, (float)face.Normal.Y, (float)face.Normal.Z ),
					(float)face.Distance, face.Material ?? "" ) );
			brushes.Add( MapBrush.FromPlanes( faces ) );
		}
		foreach ( var brush in doc.WorldBrushes ) AddBrush( brush );
		foreach ( var entity in doc.Entities )
			foreach ( var brush in entity.Brushes ) AddBrush( brush );
		return brushes;
	}

	static int Map( string[] args )
	{
		if ( args.Length == 0 || args[0] == "--help" || args[0] == "help" ) { Console.WriteLine( MapUsage ); return 0; }
		return args[0] switch
		{
			"build" => MapBuild( args[1..] ),
			"validate" => MapValidate( args[1..] ),
			"compile" => MapCompile( args[1..] ),
			"export" => MapExport( args[1..] ),
			"issues" => MapIssuesCommand( args[1..] ),
			_ => throw new ArgumentException( $"Unknown ops map command '{args[0]}'. {MapUsage}" ),
		};
	}

	static int MapBuild( string[] args )
	{
		string? input = null;
		var pass = new List<string>();
		bool scriptOption = false;
		for ( int i = 0; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
				case "--doc": pass.Add( "--doc" ); pass.Add( Value( args, ref i, "--doc" ) ); scriptOption = true; break;
				case "--out": pass.Add( "--out" ); pass.Add( Value( args, ref i, "--out" ) ); scriptOption = true; break;
				case "--run": pass.Add( "--run" ); pass.Add( Value( args, ref i, "--run" ) ); scriptOption = true; break;
				case "--pace": pass.Add( "--pace" ); pass.Add( Value( args, ref i, "--pace" ) ); scriptOption = true; break;
				default:
					if ( args[i].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Unknown map build option: {args[i]}" );
					if ( input is not null ) throw new ArgumentException( "map build takes exactly one file." );
					input = args[i];
					break;
			}
		}
		if ( input is null ) throw new ArgumentException( "usage: ops map build <script|map> [--doc in.effigy] [--out DIR] [--run DIR] [--pace S]" );
		bool isMap = input.EndsWith( ".vmf", StringComparison.OrdinalIgnoreCase ) ||
			input.EndsWith( ".map", StringComparison.OrdinalIgnoreCase );
		if ( !isMap )
		{
			// A script runs headless through the same live path as `ops script`.
			// The BuildScript map verbs (worker 64) are what a map script calls;
			// until they land, a script can only call the verbs that exist.
			pass.Insert( 0, input );
			pass.Add( "--live" );
			return ScriptLive.Run( pass.ToArray() );
		}
		if ( scriptOption ) throw new ArgumentException( "--doc/--out/--run/--pace apply to a script input, not to a map." );
		var doc = LoadMap( input );
		QuickPlayBakeResult baked;
		try { baked = QuickPlay.Bake( ToQuickPlayMap( doc ) ); }
		catch ( QuickPlayException ex )
		{
			Console.WriteLine( ErrorJson( "map build", ex.Code, ex.Message, input ) );
			return 1;
		}
		catch ( MapCommandException ex )
		{
			Console.WriteLine( ErrorJson( "map build", ex.Code, ex.Message, input ) );
			return 1;
		}
		string? errorCode = null, errorMessage = null;
		if ( !baked.Sealed ) { errorCode = "leak"; errorMessage = "Map leaks to the void; the QuickPlay bake flood-filled from the player start to a void leaf."; }
		else if ( baked.Spawn is null || !baked.Spawn.Ok ) { errorCode = baked.Spawn?.Reason ?? "spawn-outside-map"; errorMessage = "Player spawn is not usable: " + errorCode + "."; }
		else if ( baked.Walk is not null && baked.Walk.Blocked ) { errorCode = baked.Walk.BlockReason ?? "walk-blocked"; errorMessage = "Headless forward walk is blocked: " + errorCode + "."; }
		var report = new
		{
			command = "map build",
			map = Path.GetFullPath( input ),
			status = errorCode is null ? "passed" : "failed",
			error = errorCode is null ? null : new { code = errorCode, message = errorMessage },
			sealedMap = baked.Sealed,
			spawn = baked.Spawn is null ? null : new
			{
				ok = baked.Spawn.Ok, reason = baked.Spawn.Reason,
				origin = VecJson( baked.Spawn.Origin ), floorHeight = baked.Spawn.FloorHeight, hasFloor = baked.Spawn.HasFloor,
			},
			walk = baked.Walk is null ? null : new
			{
				blocked = baked.Walk.Blocked, blockReason = baked.Walk.BlockReason, blockedBy = baked.Walk.BlockedBy,
				distanceWalked = baked.Walk.DistanceWalked, stepsTaken = baked.Walk.StepsTaken,
				floorHeightAtEnd = baked.Walk.FloorHeightAtEnd, insideAtEnd = baked.Walk.InsideAtEnd,
			},
			leaves = new
			{
				empty = baked.EmptyLeafCount, solid = baked.SolidLeafCount, voidLeaves = baked.VoidLeafCount,
				portals = baked.PortalCount,
			},
			brushes = new { world = baked.WorldBrushCount, detail = baked.DetailBrushCount },
			collisionHulls = baked.CollisionHullCount,
			warnings = baked.Warnings,
			leakPath = baked.LeakPath is null ? Array.Empty<object>() :
				baked.LeakPath.Select( p => VecJson( p ) ).ToArray(),
		};
		Console.WriteLine( Json( report ) );
		return errorCode is null ? 0 : 1;
	}

	static int MapValidate( string[] args )
	{
		string? map = null;
		for ( int i = 0; i < args.Length; i++ )
		{
			if ( args[i].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Unknown map validate option: {args[i]}" );
			if ( map is not null ) throw new ArgumentException( "map validate takes exactly one map." );
			map = args[i];
		}
		if ( map is null ) throw new ArgumentException( "usage: ops map validate <map>" );
		var doc = LoadMap( map );
		var issues = MapIssues( doc );
		int errors = issues.Count( i => i.Severity == IssueSeverity.Error );
		int warnings = issues.Count( i => i.Severity == IssueSeverity.Warning );
		Console.WriteLine( Json( new
		{
			command = "map validate",
			map = Path.GetFullPath( map ),
			status = errors == 0 ? "passed" : "failed",
			errors,
			warnings,
			issues = issues.Select( IssueJson ).ToArray(),
		} ) );
		return errors == 0 ? 0 : 1;
	}

	static int MapIssuesCommand( string[] args )
	{
		string? map = null;
		for ( int i = 0; i < args.Length; i++ )
		{
			if ( args[i].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Unknown map issues option: {args[i]}" );
			if ( map is not null ) throw new ArgumentException( "map issues takes exactly one map." );
			map = args[i];
		}
		if ( map is null ) throw new ArgumentException( "usage: ops map issues <map>" );
		var doc = LoadMap( map );
		var issues = MapIssues( doc );
		// A listing, not a gate: exit 0 whenever the check ran, even with errors.
		Console.WriteLine( Json( new
		{
			command = "map issues",
			map = Path.GetFullPath( map ),
			status = "listed",
			count = issues.Count,
			errors = issues.Count( i => i.Severity == IssueSeverity.Error ),
			warnings = issues.Count( i => i.Severity == IssueSeverity.Warning ),
			issues = issues.Select( IssueJson ).ToArray(),
		} ) );
		return 0;
	}

	static int MapCompile( string[] args )
	{
		string? map = null, presetText = null, outDir = null;
		for ( int i = 0; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
				case "--preset": presetText = Value( args, ref i, "--preset" ); break;
				case "--out": outDir = Value( args, ref i, "--out" ); break;
				default:
					if ( args[i].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Unknown map compile option: {args[i]}" );
					if ( map is not null ) throw new ArgumentException( "map compile takes exactly one map." );
					map = args[i];
					break;
			}
		}
		if ( map is null || presetText is null )
			throw new ArgumentException( "usage: ops map compile <map> --preset quick|preview|final [--out DIR]" );
		var preset = ParsePreset( presetText );
		string presetName = presetText.ToLowerInvariant();
		if ( outDir is not null && File.Exists( Path.GetFullPath( outDir ) ) )
			throw new ArgumentException( $"--out must be a directory, not an existing file: {outDir}" );
		var doc = LoadMap( map );
		// The runner only warns about a missing player start and skips the leak
		// check; the CLI gates on it instead, with the named error the ticket's
		// acceptance test requires. A start outside any empty leaf still fails
		// inside the runner, as a missing-player-start error issue.
		if ( PlayerStartOf( doc ) is null )
		{
			Console.WriteLine( ErrorJson( "map compile", "missing-player-start",
				"missing-player-start: the map has no info_player_start entity, so the compile cannot leak-check it.", map ) );
			return 1;
		}
		var input = ToCompileInput( doc, Path.GetFileNameWithoutExtension( Path.GetFullPath( map ) ) );
		var options = new CompileOptions();
		if ( outDir is not null ) options.OutputFolder = Path.GetFullPath( outDir );
		var result = CompileRunner.Run( input, preset, options );
		string? errorCode = null;
		if ( !result.Succeeded )
		{
			var firstError = result.Issues.FirstOrDefault( i => i.Severity == CompileSeverity.Error );
			errorCode = firstError?.Code ?? "compile-failed";
		}
		Console.WriteLine( Json( new
		{
			command = "map compile",
			map = Path.GetFullPath( map ),
			preset = presetName,
			status = result.Succeeded ? "succeeded" : "failed",
			error = result.Succeeded ? null : new { code = errorCode, message = result.Error ?? "Compile failed." },
			failedStage = result.FailedStage?.ToString(),
			completedStages = result.CompletedStages.Select( s => s.ToString() ).ToArray(),
			bsp = result.Bsp is null ? null : new
			{
				leaves = result.Bsp.LeafCount, emptyLeaves = result.Bsp.EmptyLeafCount,
				solidLeaves = result.Bsp.SolidLeafCount, voidLeaves = result.Bsp.VoidLeafCount,
				portals = result.Bsp.PortalCount,
			},
			pvs = result.Pvs is null ? null : new { sets = result.Pvs.Sets.Count, quality = result.Pvs.Quality.ToString() },
			lightmaps = result.Lightmaps is null ? null : new
			{
				faces = result.Lightmaps.Faces.Count, resolution = result.Lightmaps.Resolution, mean = result.Lightmaps.Mean,
			},
			leak = result.Leak is null ? null : new { isLeaked = result.Leak.IsLeaked },
			issues = result.Issues.Select( i => new
			{
				code = i.Code, severity = i.Severity.ToString(), message = i.Message,
				subject = i.Subject, fixHint = i.FixHint, location = VecJson( i.Location ),
			} ).ToArray(),
			outputFiles = result.OutputFiles,
			logPath = result.LogPath,
			durationMs = result.Duration.TotalMilliseconds,
		} ) );
		return result.Succeeded ? 0 : 1;
	}

	static int MapExport( string[] args )
	{
		string? map = null, formatText = null, outPath = null;
		bool force = false;
		for ( int i = 0; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
				case "--format": formatText = Value( args, ref i, "--format" ); break;
				case "--out": outPath = Value( args, ref i, "--out" ); break;
				case "--force":
				case "--allow-destructive": force = true; break;
				default:
					if ( args[i].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Unknown map export option: {args[i]}" );
					if ( map is not null ) throw new ArgumentException( "map export takes exactly one map." );
					map = args[i];
					break;
			}
		}
		if ( map is null || formatText is null || outPath is null )
			throw new ArgumentException( "usage: ops map export <map> --format vmf|map|obj|glb --out <path> [--force]" );
		var format = ParseMapFormat( formatText );
		string outFull = Path.GetFullPath( outPath );
		if ( string.Equals( outFull, Path.GetFullPath( map ), StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( "--out must not overwrite the source map." );
		if ( File.Exists( outFull ) && !force )
			throw new ArgumentException( $"--out already exists: {outFull}. Pass --force to overwrite." );
		var doc = LoadMap( map );
		var warnings = new List<string>();
		var files = new List<string>();
		string? note = null;
		if ( doc.AllBrushes().Any( b => b.HasDisplacement ) )
			warnings.Add( "Displacements bake as their flat brush faces; displacement offsets are not projected yet." );
		switch ( format )
		{
			case MapExportFormat.Vmf:
				VmfIo.WriteFile( doc, outFull );
				files.Add( outFull );
				break;
			case MapExportFormat.Map:
				// Write(doc, out warnings) names every dropped displacement / I/O row;
				// WriteFile would swallow them.
				string text = Map220Io.Write( doc, out var mapWarnings );
				warnings.AddRange( mapWarnings );
				File.WriteAllText( outFull, text );
				files.Add( outFull );
				break;
			case MapExportFormat.Obj:
			case MapExportFormat.Glb:
				var baked = MapBakeExport.Bake( ToMapBrushes( doc ), Array.Empty<MapBakeDisplacement>() );
				if ( baked.Issues.Count > 0 )
				{
					Console.WriteLine( ErrorJson( "map export", "bake-issue",
						"bake-issue: the map did not bake cleanly: " + string.Join( "; ", baked.Issues ), map ) );
					return 1;
				}
				if ( format == MapExportFormat.Obj )
				{
					var objResult = MapBakeExport.ExportObj( baked, outFull );
					files.Add( objResult.ObjPath );
					if ( !string.IsNullOrWhiteSpace( objResult.MtlPath ) ) files.Add( objResult.MtlPath );
					if ( !string.IsNullOrWhiteSpace( objResult.Note ) ) warnings.Add( objResult.Note );
				}
				else
				{
					var glbResult = MapBakeExport.ExportGlb( baked, outFull );
					files.Add( glbResult.GlbPath );
					// The GLB writer is minimal by its own note; repeat it, never hide it.
					note = MapBakeExport.GlbNote;
					if ( !glbResult.GlbWritten ) warnings.Add( "GLB writer reported the file was not written." );
				}
				break;
		}
		Console.WriteLine( Json( new
		{
			command = "map export",
			map = Path.GetFullPath( map ),
			format = formatText.ToLowerInvariant(),
			@out = outFull,
			status = "exported",
			files,
			warnings,
			note,
		} ) );
		return 0;
	}
}
