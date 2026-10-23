using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Marionette.Desktop;

// The bench: the tool wheel used by hand edits a copy, one script line at a time, and never a run.
internal static class BenchTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }

	static string NewDir( string tag ) => Path.Combine( Path.GetTempPath(), $"effigy-bench-{tag}-" + Guid.NewGuid().ToString( "N" ) );

	static string Hash( string dir )
	{
		using var sha = SHA256.Create();
		foreach ( var f in Directory.GetFiles( dir, "*", SearchOption.AllDirectories ).OrderBy( f => f, StringComparer.Ordinal ) )
		{
			var name = System.Text.Encoding.UTF8.GetBytes( Path.GetRelativePath( dir, f ) );
			sha.TransformBlock( name, 0, name.Length, null, 0 );
			var bytes = File.ReadAllBytes( f );
			sha.TransformBlock( bytes, 0, bytes.Length, null, 0 );
		}
		sha.TransformFinalBlock( Array.Empty<byte>(), 0, 0 );
		return Convert.ToHexString( sha.Hash! );
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "bench", "empty doc: add, bad line, undo twice", () =>
		{
			var root = NewDir( "empty" );
			var events = new List<JsonObject>();
			var bench = new Bench.Session( root, new Effigy.PartStudio(), "empty", events.Add );
			Check( events[0]["ev"]!.GetValue<string>() == "ready", "no ready event" );
			Check( bench.Apply( "add Primitive name=A" ), "add Primitive failed: " + events[^1].ToJsonString() );
			Check( bench.Studio.Bodies.Any( b => b.Name == "A" ), "no body A after add" );
			Check( bench.Apply( "add Primitive name=B" ), "second add failed" );
			long rev = bench.Revision;
			int bodies = bench.Studio.Bodies.Count, features = bench.Studio.Features.Count;
			Check( !bench.Apply( "add NoSuchFeatureType" ), "a bad type applied" );
			Check( events[^1]["ev"]!.GetValue<string>() == "failed", "bad line did not report failed" );
			Check( bench.Revision == rev, $"failed line wrote a revision: {rev} -> {bench.Revision}" );
			Check( bench.Studio.Bodies.Count == bodies && bench.Studio.Features.Count == features, "failed line changed the doc" );
			Check( bench.Undo(), "undo 1 failed" );
			Check( bench.Script[^1] == "# undo: add Primitive name=B", "undo 1 label: " + bench.Script[^1] );
			Check( bench.Undo(), "undo 2 failed" );
			Check( bench.Script[^1] == "# undo: add Primitive name=A", "undo 2 label: " + bench.Script[^1] );
			Check( bench.Studio.Bodies.Count == 0, "undo did not get back to empty" );
			Check( !bench.Undo(), "undo past the start succeeded" );
			Check( File.ReadAllText( Path.Combine( root, "script.txt" ) ).Contains( "# undo: add Primitive name=A" ), "script.txt not saved" );
			var meta = JsonNode.Parse( File.ReadAllText( root + ".json" ) )!;
			Check( meta["bench"]!.GetValue<bool>(), "meta not marked bench" );
			bench.Close();
		} );

		registry.Add( "bench", "describe: edit ops, feature types, verbs", () =>
		{
			var bevel = Bench.Describe( "edit:bevel" );
			Check( bevel["kind"]!.GetValue<string>() == "edit" && bevel["op"]!.GetValue<string>() == "bevel", "edit:bevel kind/op: " + bevel.ToJsonString() );
			var keys = bevel["fields"]!.AsArray().Select( f => f!["key"]!.GetValue<string>() ).ToList();
			Check( keys.Contains( "body" ), "edit:bevel has no body field: " + string.Join( ",", keys ) );
			Check( !keys.Contains( "operation" ), "edit:bevel exposes Operation" );
			var prim = Bench.Describe( "primitive" );
			Check( prim["kind"]!.GetValue<string>() == "add" && prim["type"]!.GetValue<string>() == "Primitive", "primitive: " + prim.ToJsonString() );
			Check( prim["fields"]!.AsArray().Any( f => f!["key"]!.GetValue<string>() == "name" ), "primitive has no name field" );
			Check( Bench.Describe( "set" )["kind"]!.GetValue<string>() == "verb", "set is not a verb" );
			Check( Bench.Describe( "set" )["accepts"] is null, "a verb accepts geometry" );
			Check( Bench.Describe( "edit:nosuchop" )["error"] is not null, "unknown edit op has no error" );
			string[] Takes( string key )
			{
				var node = Bench.Describe( key )["accepts"] as JsonArray;
				return node is null ? Array.Empty<string>() : node.Select( x => x!.GetValue<string>() ).ToArray();
			}
			Check( Takes( "sketch" ).SequenceEqual( new[] { "face" } ), "sketch: " + string.Join( ",", Takes( "sketch" ) ) );
			Check( Takes( "plane" ).SequenceEqual( new[] { "face" } ), "plane: " + string.Join( ",", Takes( "plane" ) ) );
			Check( Takes( "shell" ).SequenceEqual( new[] { "face", "body" } ), "shell: " + string.Join( ",", Takes( "shell" ) ) );
			Check( Takes( "extrude" ).SequenceEqual( new[] { "face", "sketch" } ), "extrude: " + string.Join( ",", Takes( "extrude" ) ) );
			Check( Takes( "fillet" ).SequenceEqual( new[] { "face", "edge", "body" } ), "fillet: " + string.Join( ",", Takes( "fillet" ) ) );
			Check( Takes( "revolve" ).SequenceEqual( new[] { "sketch" } ), "revolve: " + string.Join( ",", Takes( "revolve" ) ) );
			Check( Takes( "primitive" ).Length == 0 && Bench.Describe( "primitive" )["accepts"] is JsonArray, "primitive takes a pick: " + string.Join( ",", Takes( "primitive" ) ) );
			Check( Takes( "edit:bevel" ).SequenceEqual( new[] { "body" } ), "bevel: " + string.Join( ",", Takes( "edit:bevel" ) ) );
		} );

		registry.Add( "bench", "editing a scrubbed run step leaves the run byte-identical", () =>
		{
			// a run with two revisions, as an AI run leaves it
			var run = NewDir( "run" );
			var live = new AiLiveRun( new AiSuiteTools( run ) );
			var studio = new Effigy.PartStudio();
			Check( Effigy.BuildScript.Run( studio, "add Primitive name=Hull" ).Ok, "run step 1" );
			live.Commit( studio, "line 1: add Primitive name=Hull" );
			string step1 = live.CurrentPath!;
			Check( Effigy.BuildScript.Run( studio, "add Primitive name=Lid" ).Ok, "run step 2" );
			live.Commit( studio, "line 2: add Primitive name=Lid" );
			live.Status( "completed", outcome: "done" );
			string before = Hash( run );

			// scrub to step 1 and edit it the way the cabinet does: copy the revision out, bench the copy
			string copy = NewDir( "base" ) + ".effigy";
			File.Copy( step1, copy );
			var bench = new Bench.Session( NewDir( "scrub" ), Effigy.StudioCache.Load( copy ), "run step 1", _ => { } );
			Check( bench.Studio.Bodies.Any( b => b.Name == "Hull" ) && !bench.Studio.Bodies.Any( b => b.Name == "Lid" ), "bench did not start at step 1" );
			Check( bench.Apply( "add Primitive name=Mine" ), "bench edit failed" );
			Check( bench.Undo() && bench.Apply( "add Primitive name=Mine2" ), "bench undo/apply failed" );
			bench.Close();
			Check( Hash( run ) == before, "the run's directory changed after editing a copy of its step" );
			var back = Effigy.StudioCache.Load( step1 );
			Check( !back.Bodies.Any( b => b.Name is "Mine" or "Mine2" ), "the run's revision gained bench bodies" );
		} );
	}
}
