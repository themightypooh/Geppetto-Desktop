#nullable enable
// GateCheck.cs — LV1 (grok-bot, 2026-11-13). The approval gate, enforced at the binary boundary.
//
// po's rule: an AI never models out of sight. Every model build plays out in a pane he approved.
// Before LV1 the gate lived only in ~/.local/bin/geppetto-live, so calling the binary directly
// (`--script --live`, `--ai create|chat`, `ops script`, `--mcp`) modelled without a yes and,
// for MCP and most CLI calls, with nothing on screen. Program.Main now calls Require() for those
// entry points before any modelling starts.
//
// What passes:
//   * a gate token (--gate-token ID, or GEPPETTO_GATE_TOKEN) whose cabinet/confirm registry
//     entry is approved (yes/launched/placed), claimed, registered to THIS run directory, and,
//     for a script run, whose approved SHA-256 matches the script being run byte-for-byte;
//   * or --test-sandbox: for tests and smoke checks only. The run directory must be explicit
//     and outside the chest's run folder (~/.local/share/geppetto/research), so a sandbox run
//     can never be found, followed or placed in a pane as an AI build (the chest only scans
//     and follows that folder; gate placement needs a registered token).
// In-process callers (ScriptLive.Execute, OpsCli.Run, McpServer in tests) are unchanged: the
// gate is the process boundary, which is the only way an agent reaches the app.
//
// Map runs (hammer-ai-safety-gates-map): RequireMapRun is the same approval
// gate for map AI work, plus the map itself must live inside the approved
// run directory — a map call may not reach outside the run it was approved
// for (no absolute map path, no ".." escape). Map runs carry no build-script
// hash, so no script argument: the approval is for the run directory, and
// the map check pins the work to that directory.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace Marionette.Desktop;

public static class GateCheck
{
	public const string SandboxFlag = "--test-sandbox";
	public const string TokenFlag = "--gate-token";
	static readonly Regex IdShape = new( "^gate-[0-9]+-[0-9a-f]{8}$", RegexOptions.CultureInvariant );
	static readonly string[] Approved = { "yes", "launched", "placed" };

	/// <summary>How long to wait for geppetto-live to register the run it just spawned
	/// (gate.py REGISTER_TIMEOUT_S is 10 s; the launcher registers right after spawning us).</summary>
	public static TimeSpan RegisterWait { get; set; } = TimeSpan.FromSeconds( 10 );

	public static string RunsRoot =>
		Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".local/share/geppetto/research" );

	/// <summary>The durable registry: GEPPETTO_CONFIRM_DIR (tests), else cabinet/confirm beside
	/// the checkout this binary was built from, else the Desktop checkout.</summary>
	/// <summary>Tests point this at a scratch registry (never the real cabinet/confirm).</summary>
	public static string? ConfirmDirOverride { get; set; }

	public static string ConfirmDir
	{
		get
		{
			if ( ConfirmDirOverride is not null ) return ConfirmDirOverride;
			string? env = Environment.GetEnvironmentVariable( "GEPPETTO_CONFIRM_DIR" );
			if ( !string.IsNullOrWhiteSpace( env ) ) return Path.GetFullPath( env );
			for ( var d = new DirectoryInfo( AppContext.BaseDirectory ); d is not null; d = d.Parent )
			{
				string c = Path.Combine( d.FullName, "cabinet", "confirm" );
				if ( Directory.Exists( Path.Combine( d.FullName, "cabinet" ) ) ) return c;
			}
			return Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "Desktop/geppetto-desktop/cabinet/confirm" );
		}
	}

	/// <summary>Removes the gate flags from <paramref name="args"/> and returns what they said.</summary>
	public static (string[] Args, string? Token, bool Sandbox) Strip( string[] args )
	{
		var rest = new List<string>();
		string? token = null;
		bool sandbox = false;
		for ( int i = 0; i < args.Length; i++ )
		{
			if ( args[i] == SandboxFlag ) { sandbox = true; continue; }
			if ( args[i] == TokenFlag )
			{
				if ( i + 1 >= args.Length ) throw new GateRefusedException( "--gate-token needs an intent id." );
				token = args[++i];
				continue;
			}
			rest.Add( args[i] );
		}
		token ??= Environment.GetEnvironmentVariable( "GEPPETTO_GATE_TOKEN" );
		if ( string.IsNullOrWhiteSpace( token ) ) token = null;
		return (rest.ToArray(), token, sandbox);
	}

	/// <summary>The value after <paramref name="flag"/>, or null.</summary>
	public static string? Option( string[] args, string flag )
	{
		for ( int i = 0; i + 1 < args.Length; i++ ) if ( args[i] == flag ) return args[i + 1];
		return null;
	}

	static bool Inside( string path, string root )
	{
		string p = Path.GetFullPath( path ).TrimEnd( '/' ) + "/";
		string r = Path.GetFullPath( root ).TrimEnd( '/' ) + "/";
		return p.StartsWith( r, StringComparison.Ordinal );
	}

	/// <summary>Refuses (throws <see cref="GateRefusedException"/>) unless this process may model.
	/// <paramref name="surface"/> names the entry point for the message; <paramref name="runDir"/>
	/// is the run/out directory the work goes to (required); <paramref name="script"/> is the
	/// build script for script runs (its hash must be the approved one), null otherwise.</summary>
	public static void Require( string surface, string? runDir, string? script, string? token, bool sandbox )
	{
		if ( sandbox )
		{
			if ( string.IsNullOrWhiteSpace( runDir ) )
				throw new GateRefusedException( $"{surface}: {SandboxFlag} needs an explicit run/out directory outside the chest's run folder." );
			if ( Inside( runDir, RunsRoot ) )
				throw new GateRefusedException( $"{surface}: a {SandboxFlag} run may not live in {RunsRoot}: the chest would show it as an AI build." );
			return;
		}
		if ( token is null )
			throw new GateRefusedException(
				$"GATE REFUSED ({surface}): no approval. AI modelling only runs in a pane po approved. " +
				"Use `geppetto-live request build.txt --subject \"...\"` (files the intent, waits for his yes, then builds in the pane), " +
				$"or pass {SandboxFlag} with an explicit --run/--out outside {RunsRoot} for tests." );
		if ( !IdShape.IsMatch( token ) ) throw new GateRefusedException( $"GATE REFUSED ({surface}): '{token}' is not a gate intent id." );
		if ( string.IsNullOrWhiteSpace( runDir ) )
			throw new GateRefusedException( $"GATE REFUSED ({surface}): a gated run needs the --run directory its approval was registered to." );
		string run = Path.GetFullPath( runDir ).TrimEnd( '/' );
		string dir = ConfirmDir;
		string intentPath = Path.Combine( dir, token + ".json" ), tokenPath = Path.Combine( dir, token + ".token.json" );
		var deadline = DateTime.UtcNow + RegisterWait;
		string last = "no approval found for " + token;
		while ( true )
		{
			string? why = Check( intentPath, tokenPath, run, script );
			if ( why is null ) return;
			last = why;
			// only "not registered yet" is worth waiting for: geppetto-live registers right after spawning us
			if ( !why.StartsWith( "waiting:", StringComparison.Ordinal ) || DateTime.UtcNow >= deadline ) break;
			Thread.Sleep( 100 );
		}
		throw new GateRefusedException( $"GATE REFUSED ({surface}): {last.Replace( "waiting: ", "" )}" );
	}

	/// <summary>Map runs need the same approval as modelling runs, plus the
	/// map itself must live inside the approved run directory: a map AI call
	/// may not reach outside the run it was approved for (no absolute map
	/// path outside the run, no ".." escape). <paramref name="script"/> is
	/// always null here — map runs carry no build-script hash; the approval
	/// is for the run directory, and the map check pins the work to it.
	/// Sandbox runs need an explicit run dir outside the chest's run folder,
	/// exactly as <see cref="Require"/> requires.</summary>
	public static void RequireMapRun( string surface, string? runDir, string? mapPath, string? token, bool sandbox )
	{
		if ( string.IsNullOrWhiteSpace( mapPath ) )
			throw new GateRefusedException( $"{surface}: a map run needs its map document reference." );
		Require( surface, runDir, null, token, sandbox );
		string run = Path.GetFullPath( runDir! ).TrimEnd( '/' );
		string map = Path.IsPathRooted( mapPath ) ? Path.GetFullPath( mapPath ) : Path.GetFullPath( Path.Combine( run, mapPath ) );
		if ( !Inside( map, run ) )
			throw new GateRefusedException( $"{surface}: the map is outside the approved run directory." );
	}

	static string? Check( string intentPath, string tokenPath, string run, string? script )
	{
		JsonNode? intent, tok;
		try
		{
			intent = JsonNode.Parse( File.ReadAllText( intentPath ) );
			tok = JsonNode.Parse( File.ReadAllText( tokenPath ) );
		}
		catch ( FileNotFoundException ) { return "no yes on record for this intent (no token file)"; }
		catch ( DirectoryNotFoundException ) { return "the approval registry is missing"; }
		catch ( Exception e ) { return "approval record unreadable: " + e.Message; }
		string status = intent?["status"]?.GetValue<string>() ?? "";
		if ( !Approved.Contains( status ) ) return $"intent is '{status}', not approved";
		if ( tok?["used_at"] is null ) return "waiting: approval not claimed yet";
		string? registered = tok?["run"]?.GetValue<string>();
		if ( string.IsNullOrEmpty( registered ) ) return "waiting: run not registered yet";
		if ( !string.Equals( Path.GetFullPath( registered ).TrimEnd( '/' ), run, StringComparison.Ordinal ) )
			return $"this approval belongs to run {registered}, not {run}";
		if ( script is not null )
		{
			string want = tok?["script_hash"]?.GetValue<string>() ?? "";
			string have;
			try
			{
				// mirror gate.py script_hash(open(path, encoding="utf-8").read()): UTF-8 decode that
				// keeps a BOM, then Python's universal-newline translation
				string text = new UTF8Encoding( false ).GetString( File.ReadAllBytes( script ) ).Replace( "\r\n", "\n" ).Replace( "\r", "\n" );
				have = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( text ) ) ).ToLowerInvariant();
			}
			catch ( Exception e ) { return "cannot read the script: " + e.Message; }
			if ( have != want ) return "the script changed after po said yes; file a fresh intent";
		}
		return null;
	}
}

public sealed class GateRefusedException : Exception
{
	public GateRefusedException( string message ) : base( message ) { }
}
