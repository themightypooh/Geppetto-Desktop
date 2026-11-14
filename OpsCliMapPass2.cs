// OpsCliMapPass2.cs — pass-2 map commands for the native OpsCli (task hammer-ai-pass2-tool-verbs).
//
// Extends the existing OpsCli "map" surface with one subcommand per pass-2
// AI verb (same verbs as BuildScript.MapPass2VerbNames / McpMapPass2Tools).
// This file defines the commands only; the final integrator wires them into
// OpsCli.Map() (see tests/fixtures/hammer/hammer-ai-pass2-tool-verbs/SEAM.md).
// No second CLI is created: no Main, no server, no listen loop — one JSON
// object per command on stdout, exit code 0 on ok, 1 on error /
// not-implemented. No shell is ever run: compile runs CompileRunner only,
// and any command/shell/exec argument is refused by the runner.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

namespace Marionette.Desktop;

public static class OpsCliMapPass2
{
	public static IReadOnlyDictionary<string, string> CliCommands { get; } = new Dictionary<string, string>( StringComparer.Ordinal )
	{
		["live-lighting"] = BuildScript.MapPass2VerbNames[0],
		["skybox"] = BuildScript.MapPass2VerbNames[1],
		["effects"] = BuildScript.MapPass2VerbNames[2],
		["compile-preset"] = BuildScript.MapPass2VerbNames[3],
		["compile-only-entities"] = BuildScript.MapPass2VerbNames[4],
		["check"] = BuildScript.MapPass2VerbNames[5],
		["autofix"] = BuildScript.MapPass2VerbNames[6],
		["layer"] = BuildScript.MapPass2VerbNames[7],
		["visgroup-ops"] = BuildScript.MapPass2VerbNames[8],
		["lock"] = BuildScript.MapPass2VerbNames[9],
		["paste-special"] = BuildScript.MapPass2VerbNames[10],
		["gizmo"] = BuildScript.MapPass2VerbNames[11],
		["direct-edit"] = BuildScript.MapPass2VerbNames[12],
		["linked-group"] = BuildScript.MapPass2VerbNames[13],
		["prop-place"] = BuildScript.MapPass2VerbNames[14],
		["propper"] = BuildScript.MapPass2VerbNames[15],
		["hotspot"] = BuildScript.MapPass2VerbNames[16],
		["browser"] = BuildScript.MapPass2VerbNames[17],
		["hotload"] = BuildScript.MapPass2VerbNames[18],
		["playtest"] = BuildScript.MapPass2VerbNames[19],
		["navmesh-generate"] = BuildScript.MapPass2VerbNames[20],
	};

	public const string MapPass2Usage =
		"usage: map <pass-2-command> --map <file.vmf|.map> [--out <file>] [--root <dir>] [--workdir <dir>] [key=value ...]\n" +
		"pass-2 commands (one per AI verb):\n" +
		"  live-lighting | skybox | effects | compile-preset | compile-only-entities\n" +
		"  check | autofix | layer | visgroup-ops | lock | paste-special | gizmo\n" +
		"  direct-edit | linked-group | prop-place | propper | hotspot | browser\n" +
		"  hotload | playtest | navmesh-generate\n" +
		"notes: --map is required except for gizmo. Destructive ops need confirm=true.\n" +
		"compile runs CompileRunner only; command/shell/exec arguments are refused.";

	public static bool HasCommand( string subcommand ) => CliCommands.ContainsKey( subcommand );

	public static string? CliCommandFor( string verb )
		=> CliCommands.FirstOrDefault( pair => pair.Value == verb ).Key;

	public static int MapPass2( string[] args )
	{
		if ( args is null || args.Length == 0 )
		{
			Console.WriteLine( MapPass2Usage );
			return 1;
		}
		var subcommand = args[0];
		if ( subcommand is "--help" or "-h" or "help" )
		{
			Console.WriteLine( MapPass2Usage );
			return 0;
		}
		if ( !CliCommands.TryGetValue( subcommand, out var verb ) )
		{
			Console.WriteLine( ErrorJson( "unknown-command", $"Unknown pass-2 map command '{subcommand}'. Run 'map --help' for the list." ) );
			return 1;
		}
		string? mapPath = null;
		string? outPath = null;
		string? root = null;
		string? workDirectory = null;
		var dict = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		for ( var i = 1; i < args.Length; i++ )
		{
			var token = args[i];
			if ( token == "--map" || token == "--out" || token == "--root" || token == "--workdir" )
			{
				if ( i + 1 >= args.Length )
				{
					Console.WriteLine( ErrorJson( "invalid-argument", $"{token} needs a value." ) );
					return 1;
				}
				var value = args[++i];
				if ( token == "--map" ) mapPath = value;
				else if ( token == "--out" ) outPath = value;
				else if ( token == "--root" ) root = value;
				else workDirectory = value;
				continue;
			}
			if ( token.StartsWith( "--", StringComparison.Ordinal ) )
			{
				Console.WriteLine( ErrorJson( "invalid-argument", $"Unknown flag '{token}'. Expected --map/--out/--root/--workdir or key=value." ) );
				return 1;
			}
			var eq = token.IndexOf( '=' );
			if ( eq <= 0 )
			{
				Console.WriteLine( ErrorJson( "invalid-argument", $"Expected key=value, got '{token}'." ) );
				return 1;
			}
			dict[token.Substring( 0, eq )] = token.Substring( eq + 1 );
		}
		if ( outPath is not null ) dict["out"] = outPath;
		if ( root is not null ) dict["root"] = root;
		if ( mapPath is null && verb != BuildScript.MapPass2VerbNames[11] )
		{
			Console.WriteLine( ErrorJson( "map-required", $"Command '{subcommand}' needs --map <file.vmf|.map>." ) );
			return 1;
		}
		try
		{
			var text = BuildScript.RunMapPass2Verb( verb, mapPath, dict, workDirectory );
			Console.WriteLine( text );
			using var doc = JsonDocument.Parse( text );
			return doc.RootElement.TryGetProperty( "status", out var status )
				&& status.GetString() == "not-implemented" ? 1 : 0;
		}
		catch ( ArgumentException e )
		{
			Console.WriteLine( ErrorJson( "invalid-argument", e.Message ) );
			return 1;
		}
		catch ( FileNotFoundException e )
		{
			Console.WriteLine( ErrorJson( "file-not-found", e.Message ) );
			return 1;
		}
		catch ( DirectoryNotFoundException e )
		{
			Console.WriteLine( ErrorJson( "file-not-found", e.Message ) );
			return 1;
		}
		catch ( Exception e )
		{
			Console.WriteLine( ErrorJson( "internal", e.Message ) );
			return 1;
		}
	}

	static string ErrorJson( string code, string message )
		=> JsonSerializer.Serialize( new { status = "error", code, message } );
}
