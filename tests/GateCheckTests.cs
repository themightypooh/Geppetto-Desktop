#nullable enable
// LV1 (grok-bot, 2026-11-13): the approval gate at the binary boundary (GateCheck.cs, Program.cs).
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Marionette.Desktop;

internal static class GateCheckTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "gate-check", name, test );
		void Require( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }
		string Work( string name )
		{
			string dir = Path.Combine( settings.WorkDirectory, "gate-check", name );
			if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
			Directory.CreateDirectory( dir );
			return dir;
		}
		string Hash( string text ) => Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( text ) ) ).ToLowerInvariant();
		const string Id = "gate-1793384465-edf3c538";
		// writes a registry entry the way gate.py does after yes -> claim -> register
		(string confirm, string script, string run) Fixture( string name, string status = "launched", bool claimed = true, string? runOverride = null, string? hashOf = null )
		{
			string dir = Work( name ), confirm = Path.Combine( dir, "confirm" ), run = Path.Combine( dir, "run" );
			Directory.CreateDirectory( confirm );
			string script = Path.Combine( dir, "build.txt" );
			string text = "add box body\nsave out.effigy\n";
			File.WriteAllText( script, text );
			File.WriteAllText( Path.Combine( confirm, Id + ".json" ), new JsonObject { ["id"] = Id, ["status"] = status }.ToJsonString() );
			var tok = new JsonObject { ["id"] = Id, ["script_hash"] = Hash( hashOf ?? text ), ["used_at"] = claimed ? 1.0 : null };
			if ( claimed ) tok["run"] = runOverride ?? run;
			File.WriteAllText( Path.Combine( confirm, Id + ".token.json" ), tok.ToJsonString() );
			return (confirm, script, run);
		}
		string Refusal( Action act )
		{
			try { act(); }
			catch ( GateRefusedException ex ) { return ex.Message; }
			throw new Exception( "expected the gate to refuse, it allowed the run" );
		}
		void WithRegistry( string confirm, Action act )
		{
			var saved = GateCheck.ConfirmDirOverride;
			var wait = GateCheck.RegisterWait;
			GateCheck.ConfirmDirOverride = confirm;
			GateCheck.RegisterWait = TimeSpan.FromMilliseconds( 300 );
			try { act(); }
			finally { GateCheck.ConfirmDirOverride = saved; GateCheck.RegisterWait = wait; }
		}

		Check( "no token refuses and names the gated helper", () =>
		{
			string msg = Refusal( () => GateCheck.Require( "--script", "/tmp/x", "/tmp/x.txt", null, false ) );
			Require( msg.Contains( "geppetto-live request" ), "refusal must tell the agent how to ask: " + msg );
		} );

		Check( "approved, claimed, registered token for this run and script passes", () =>
		{
			var (confirm, script, run) = Fixture( "pass" );
			WithRegistry( confirm, () => GateCheck.Require( "--script", run, script, Id, false ) );
		} );

		Check( "placed status passes too", () =>
		{
			var (confirm, script, run) = Fixture( "placed", status: "placed" );
			WithRegistry( confirm, () => GateCheck.Require( "--script", run, script, Id, false ) );
		} );

		Check( "pending, cancelled and failed intents refuse", () =>
		{
			foreach ( string status in new[] { "pending", "cancelled", "failed", "expired", "closed" } )
			{
				var (confirm, script, run) = Fixture( "status-" + status, status: status );
				WithRegistry( confirm, () => Require( Refusal( () => GateCheck.Require( "--script", run, script, Id, false ) ).Contains( status ), status ) );
			}
		} );

		Check( "a token registered to another run refuses", () =>
		{
			var (confirm, script, run) = Fixture( "other-run", runOverride: "/tmp/somewhere-else" );
			WithRegistry( confirm, () => Require( Refusal( () => GateCheck.Require( "--script", run, script, Id, false ) ).Contains( "belongs to run" ), "other run" ) );
		} );

		Check( "an edited script refuses (hash bound at yes)", () =>
		{
			var (confirm, script, run) = Fixture( "edited", hashOf: "add sphere head\n" );
			WithRegistry( confirm, () => Require( Refusal( () => GateCheck.Require( "--script", run, script, Id, false ) ).Contains( "changed after" ), "hash" ) );
		} );

		Check( "an unclaimed yes waits, then refuses", () =>
		{
			var (confirm, script, run) = Fixture( "unclaimed", status: "yes", claimed: false );
			var t = DateTime.UtcNow;
			WithRegistry( confirm, () => Require( Refusal( () => GateCheck.Require( "--script", run, script, Id, false ) ).Contains( "not claimed" ), "unclaimed" ) );
			Require( DateTime.UtcNow - t >= TimeSpan.FromMilliseconds( 250 ), "must wait for registration before refusing" );
		} );

		Check( "a token that is not an intent id refuses", () =>
		{
			Require( Refusal( () => GateCheck.Require( "--script", "/tmp/x", "/tmp/x.txt", "../../etc/passwd", false ) ).Contains( "not a gate intent" ), "shape" );
		} );

		Check( "test sandbox allows an explicit run outside the chest run folder", () =>
		{
			string run = Path.Combine( Work( "sandbox" ), "run" );
			GateCheck.Require( "--script", run, null, null, true );
			Require( !Directory.Exists( run ), "the gate must not create the run directory (ScriptLive needs it new or empty)" );
		} );

		Check( "test sandbox refuses the chest run folder and an implicit run", () =>
		{
			Require( Refusal( () => GateCheck.Require( "--script", Path.Combine( GateCheck.RunsRoot, "script-x" ), null, null, true ) ).Contains( "may not live" ), "runs root" );
			Require( Refusal( () => GateCheck.Require( "--script", null, null, null, true ) ).Contains( "explicit" ), "implicit run" );
		} );

		Check( "binary entry points refuse without approval (exit 3)", () =>
		{
			string dir = Work( "entry" );
			string script = Path.Combine( dir, "build.txt" );
			File.WriteAllText( script, "" );
			var err = Console.Error;
			Console.SetError( TextWriter.Null );
			try
			{
				Require( Marionette.Desktop.Program.Main( new[] { "--script", script, "--live", "--run", Path.Combine( dir, "a" ) } ) == 3, "--script" );
				Require( Marionette.Desktop.Program.Main( new[] { "ops", "script", script, "--run", Path.Combine( dir, "b" ) } ) == 3, "ops script" );
				Require( Marionette.Desktop.Program.Main( new[] { "--ai", "create", "--model", "m", "--prompt", "p", "--out", Path.Combine( dir, "c" ) } ) == 3, "--ai create" );
				Require( Marionette.Desktop.Program.Main( new[] { "--ai", "chat", "--model", "m", "--out", Path.Combine( dir, "d" ) } ) == 3, "--ai chat" );
				Require( Marionette.Desktop.Program.Main( new[] { "--mcp" } ) == 3, "--mcp" );
				Require( !Directory.Exists( Path.Combine( dir, "a" ) ) && !Directory.Exists( Path.Combine( dir, "b" ) ), "a refused run must not create its run directory" );
			}
			finally { Console.SetError( err ); }
		} );

		Check( "binary script run in a test sandbox completes", () =>
		{
			string dir = Work( "entry-sandbox" );
			string script = Path.Combine( dir, "empty.txt" );
			File.WriteAllText( script, "" );
			var (o, e) = (Console.Out, Console.Error);
			Console.SetOut( TextWriter.Null ); Console.SetError( TextWriter.Null );
			try
			{
				Require( Marionette.Desktop.Program.Main( new[] { "ops", "script", script, "--run", Path.Combine( dir, "run" ), "--test-sandbox" } ) == 0, "sandboxed ops script must run" );
			}
			finally { Console.SetOut( o ); Console.SetError( e ); }
		} );
	}
}
