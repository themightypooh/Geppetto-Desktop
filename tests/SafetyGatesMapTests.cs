#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Marionette.Desktop;

// Safety gates for the map AI tools (McpSafety.cs BeginMapCall /
// ValidateMapDefinitions / ScrubMapOutput / RequireMapOverwriteConfirm and
// GateCheck.cs RequireMapRun). Group name 'safetygatesmap'.
//
// The gate never executes a tool — it only validates — so every "succeeds"
// check below means validation passed and the call was audited; nothing is
// created, deleted, compiled or overwritten by these tests. Fixtures live in
// tests/fixtures/hammer/hammer-ai-safety-gates-map/ (a sealed starter room,
// a shell-attempt red-team call, a fake-secret tool result).
//
// Seam: McpMapTools.cs (worker 65) has not landed yet, so the map verb set
// under test is the filed spec in SafetyGates.MapTools, not a live import.
// When it lands, the integrator wires ValidateMapDefinitions over its
// ListTools() and BeginMapCall around every map tools/call; these checks
// must keep passing unchanged.
internal static class SafetyGatesMapTests
{
    public static void Register( TestRegistry registry, TestSettings settings )
    {
        void Check( string name, Action test ) => registry.Add( "safetygatesmap", name, test );
        void Require( bool condition, string message = "assertion failed" )
        {
            if ( !condition ) throw new Exception( message );
        }
        void Throws<T>( Action body, string what ) where T : Exception
        {
            try { body(); }
            catch ( T ) { return; }
            catch ( Exception ex ) { throw new Exception( "Expected " + typeof( T ).Name + " for " + what + ", got " + ex.GetType().Name + ": " + ex.Message ); }
            throw new Exception( "Expected " + typeof( T ).Name + ": " + what );
        }

        (AiSuiteTools tools, SafetyGates gates, string root) Gates( string name, SafetyPolicy? policy = null )
        {
            string root = Path.Combine( settings.WorkDirectory, "safetygatesmap-" + name );
            var tools = new AiSuiteTools( root );
            return ( tools, new SafetyGates( tools, policy: policy ), root );
        }
        string[] AuditLines( string root ) => File.ReadAllLines( Path.Combine( root, "audit.log" ) );
        string AuditText( string root ) => File.ReadAllText( Path.Combine( root, "audit.log" ) );

        // Walk up from the cwd and from the test binary looking for a repo-relative path.
        string FindRepoPath( string relative )
        {
            foreach ( string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory } )
            {
                string dir = Path.GetFullPath( start );
                while ( dir is not null )
                {
                    string candidate = Path.Combine( dir, relative );
                    if ( File.Exists( candidate ) || Directory.Exists( candidate ) ) return candidate;
                    string? parent = Path.GetDirectoryName( dir );
                    if ( parent is null || parent == dir ) break;
                    dir = parent;
                }
            }
            throw new Exception( $"repo path not found under cwd or AppContext.BaseDirectory: {relative}" );
        }
        string FixtureDir() => FindRepoPath( "tests/fixtures/hammer/hammer-ai-safety-gates-map" );
        string Work( string name )
        {
            string dir = Path.Combine( settings.WorkDirectory, "safetygatesmap", name );
            if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
            Directory.CreateDirectory( dir );
            return dir;
        }

        Check( "fixture map and shell-attempt fixture load, shell attempt refused", () =>
        {
            string dir = FixtureDir();
            string room = Path.Combine( dir, "room.vmf" );
            Require( File.Exists( room ), "room.vmf fixture missing" );
            string vmf = File.ReadAllText( room );
            Require( vmf.Contains( "worldspawn" ), "fixture map must be a worldspawn map" );
            Require( vmf.Contains( "sky_day01_01" ), "fixture map must carry the starter skybox" );
            var attempt = JsonNode.Parse( File.ReadAllText( Path.Combine( dir, "shell-attempt.json" ) ) )!.AsObject();
            Require( attempt["tool"]?.GetValue<string>() == "map_compile", "shell-attempt fixture is a map_compile call" );
            var (_, gates, root) = Gates( "fixture" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_compile",
                attempt["args"]!.AsObject(), attempt["map"]!.GetValue<string>() ), "fixture compiler_path" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 1 && lines[0].Contains( "\"refused\"" ), "shell attempt logged once as refused" );
        } );

        Check( "map compile with a compiler_path is refused and logged with the map", () =>
        {
            var (_, gates, root) = Gates( "compiler-path" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_compile",
                new JsonObject { ["map"] = "room.vmf", ["quality"] = "fast", ["compiler_path"] = "C:/tools/vbsp.exe" }, "room.vmf" ), "compiler_path" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 1, "one refusal logged, got " + lines.Length );
            Require( lines[0].Contains( "\"refused\"" ), "entry is a refusal" );
            Require( lines[0].Contains( "\"map\":\"room.vmf\"" ), "entry carries the map" );
            Require( lines[0].Contains( "\"tool\":\"map_compile\"" ), "entry carries the operation" );
            Require( !lines[0].Contains( "vbsp" ), "no shell path leaks into the audit log" );
        } );

        Check( "map call with a shell command arg is refused", () =>
        {
            var (_, gates, root) = Gates( "shell-arg" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_compile",
                new JsonObject { ["map"] = "room.vmf", ["command"] = "vbsp room.vmf" }, "room.vmf" ), "command arg" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "cli", "map_compile",
                new JsonObject { ["map"] = "room.vmf", ["shell"] = "vbsp room.vmf" }, "room.vmf" ), "shell arg" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_bake_lightmap",
                new JsonObject { ["map"] = "room.vmf", ["external_command"] = "vrad room.vmf" }, "room.vmf" ), "external_command arg" );
            Require( AuditLines( root ).Length == 3, "three shell refusals logged" );
        } );

        Check( "map call with an executable path value is refused", () =>
        {
            var (_, gates, root) = Gates( "exe-value" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_import_export",
                new JsonObject { ["map"] = "room.vmf", ["output_path"] = "out/vbsp.exe" }, "room.vmf" ), "vbsp.exe value" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_compile",
                new JsonObject { ["map"] = "room.vmf", ["output_path"] = "tools/vvis" }, "room.vmf" ), "vvis path segment" );
            // A directory legitimately called hammer, and a map file, are not executables.
            using ( var call = gates.BeginMapCall( "mcp", "map_validate",
                new JsonObject { ["map"] = "hammer/room.vmf" }, "hammer/room.vmf" ) )
                call.Complete( "ok" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 3, "two refusals + one ok, got " + lines.Length );
            Require( lines[2].Contains( "\"ok\"" ), "hammer-dir map validates" );
        } );

        Check( "map reference escaping the run is refused", () =>
        {
            var (_, gates, root) = Gates( "map-escape" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_validate",
                new JsonObject { ["map"] = "../escape.vmf" }, "../escape.vmf" ), "../escape" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_validate",
                new JsonObject { ["map"] = "/tmp/evil.vmf" }, "/tmp/evil.vmf" ), "absolute map" );
            Require( AuditLines( root ).Length == 2, "two refusals logged" );
        } );

        Check( "unknown and non-map tools are refused by the map gate", () =>
        {
            var (_, gates, root) = Gates( "unknown-map-tool" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_teleport",
                new JsonObject { ["map"] = "room.vmf" }, "room.vmf" ), "unknown map tool" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "catalog",
                new JsonObject { ["type"] = "Box" }, "room.vmf" ), "modelling tool via the map gate" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "shell",
                new JsonObject { ["map"] = "room.vmf" }, "room.vmf" ), "shell tool" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_compile",
                new JsonObject { ["map"] = "room.vmf" }, "" ), "missing map reference" );
            Require( SafetyGates.IsMapTool( "map_compile" ), "map_compile is a map tool" );
            Require( !SafetyGates.IsMapTool( "catalog" ), "catalog is not a map tool" );
            Require( SafetyGates.MapTools.Length == 20, "20 map verbs in the filed set, got " + SafetyGates.MapTools.Length );
            Require( AuditLines( root ).Length == 4, "four refusals logged" );
        } );

        Check( "delete brush without the flag is refused, with the flag it validates", () =>
        {
            var (_, gates, root) = Gates( "delete-brush" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_brush_edit",
                new JsonObject { ["id"] = "brush-1", ["action"] = "delete" }, "room.vmf" ), "edit+delete, no flag" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_brush_delete",
                new JsonObject { ["id"] = "brush-1" }, "room.vmf" ), "explicit delete verb, no flag" );
            Require( SafetyGates.IsMapDestructive( "map_brush_edit", new JsonObject { ["action"] = "delete" } ), "edit+delete is destructive" );
            Require( !SafetyGates.IsMapDestructive( "map_brush_edit", new JsonObject { ["action"] = "move" } ), "edit+move is not destructive" );
            // With a flag validation passes — but nothing is executed: the guard
            // never runs a tool, so no brush is deleted here by construction.
            using ( var call = gates.BeginMapCall( "mcp", "map_brush_edit",
                new JsonObject { ["id"] = "brush-1", ["action"] = "delete" }, "room.vmf", destructiveConfirm: true ) )
                call.Complete( "ok" );
            using ( var call = gates.BeginMapCall( "cli", "map_brush_delete",
                new JsonObject { ["id"] = "brush-1" }, "room.vmf", allowDestructive: true ) )
                call.Complete( "ok" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 4, "two refusals + two oks, got " + lines.Length );
            Require( lines.All( l => l.Contains( "\"map\":\"room.vmf\"" ) ), "every entry carries the map" );
            Require( lines[0].Contains( "\"refused\"" ) && lines[3].Contains( "\"ok\"" ), "results in order" );
        } );

        Check( "delete entity via action=remove needs the flag", () =>
        {
            var (_, gates, root) = Gates( "delete-entity" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_entity_edit",
                new JsonObject { ["id"] = "light_1", ["action"] = "remove" }, "room.vmf" ), "remove, no flag" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_entity_edit",
                new JsonObject { ["id"] = "light_1", ["remove"] = true }, "room.vmf" ), "remove=true, no flag" );
            using ( var call = gates.BeginMapCall( "mcp", "map_entity_delete",
                new JsonObject { ["id"] = "light_1" }, "room.vmf", destructiveConfirm: true ) )
                call.Complete( "ok" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 3, "two refusals + one ok, got " + lines.Length );
        } );

        Check( "overwrite without the flag is refused, with the flag it validates", () =>
        {
            var (_, gates, root) = Gates( "map-overwrite" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_import_export",
                new JsonObject { ["map"] = "room.vmf", ["overwrite"] = true }, "room.vmf" ), "overwrite=true, no flag" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_import_export",
                new JsonObject { ["map"] = "room.vmf", ["mode"] = "overwrite" }, "room.vmf" ), "mode=overwrite, no flag" );
            using ( var call = gates.BeginMapCall( "mcp", "map_import_export",
                new JsonObject { ["map"] = "room.vmf", ["overwrite"] = true }, "room.vmf", destructiveConfirm: true ) )
                call.Complete( "ok" );
            using ( var call = gates.BeginMapCall( "cli", "map_import_export",
                new JsonObject { ["map"] = "room.vmf" }, "room.vmf" ) )
                call.Complete( "ok" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 4, "two refusals + two oks, got " + lines.Length );
        } );

        Check( "final compile without the flag is refused, fast compile passes", () =>
        {
            var (_, gates, root) = Gates( "final-compile" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_compile",
                new JsonObject { ["map"] = "room.vmf", ["quality"] = "final" }, "room.vmf" ), "final, no flag" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_compile",
                new JsonObject { ["map"] = "room.vmf", ["mode"] = "final" }, "room.vmf" ), "mode=final, no flag" );
            Require( SafetyGates.IsMapDestructive( "map_compile", new JsonObject { ["quality"] = "final" } ), "final compile is destructive" );
            Require( !SafetyGates.IsMapDestructive( "map_compile", new JsonObject { ["quality"] = "fast" } ), "fast compile is not destructive" );
            using ( var call = gates.BeginMapCall( "mcp", "map_compile",
                new JsonObject { ["map"] = "room.vmf", ["quality"] = "final" }, "room.vmf", destructiveConfirm: true ) )
                call.Complete( "ok" );
            using ( var call = gates.BeginMapCall( "mcp", "map_compile",
                new JsonObject { ["map"] = "room.vmf", ["quality"] = "fast" }, "room.vmf" ) )
                call.Complete( "ok" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 4, "two refusals + two oks, got " + lines.Length );
        } );

        Check( "non-destructive map call passes and audits the map", () =>
        {
            var (_, gates, root) = Gates( "map-audit" );
            using ( var call = gates.BeginMapCall( "mcp", "map_brush_create",
                new JsonObject { ["map"] = "room.vmf", ["type"] = "Box" }, "room.vmf" ) )
                call.Complete( "ok" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 1, "one entry" );
            using var doc = JsonDocument.Parse( lines[0] );
            var entry = doc.RootElement;
            Require( entry.GetProperty( "surface" ).GetString() == "mcp", "surface" );
            Require( entry.GetProperty( "tool" ).GetString() == "map_brush_create", "tool" );
            Require( entry.GetProperty( "map" ).GetString() == "room.vmf", "map logged with the operation" );
            string hash = entry.GetProperty( "argsSha256" ).GetString()!;
            Require( hash.Length == 64 && hash.All( c => "0123456789abcdef".Contains( c ) ), "64 hex chars" );
            Require( !lines[0].Contains( "Box" ), "raw args never logged" );
            Require( entry.GetProperty( "result" ).GetString() == "ok", "result ok" );
        } );

        Check( "map output secrets are scrubbed, plain values kept", () =>
        {
            var fixture = JsonNode.Parse( File.ReadAllText( Path.Combine( FixtureDir(), "secret-output.json" ) ) )!.AsObject();
            var scrubbed = (JsonObject)SafetyGates.ScrubMapOutput( fixture["result"] )!;
            Require( scrubbed["api_key"]?.GetValue<string>() == "[redacted]", "api_key redacted" );
            Require( scrubbed["output"]?.GetValue<string>() == "maps/room.bsp", "plain output path kept" );
            Require( !scrubbed.ToJsonString().Contains( "sk-fake" ), "fake sk- secret gone" );
            Require( !scrubbed.ToJsonString().Contains( "ghp_fake" ), "fake github token gone" );
            string text = SafetyGates.ScrubMapOutput( "key sk-abcdefghij0123456789 and Bearer abcdefghij0123456789 end" )!;
            Require( !text.Contains( "sk-abcdefghij" ), "sk- shape redacted" );
            Require( text.Contains( "Bearer [redacted]" ), "bearer redacted" );
            // Regression: the old key=value redaction echoed the VALUE back
            // ("password=hunter2" -> "hunter2=[redacted]"); the key must be
            // kept and the value redacted.
            string kv = SafetyGates.ScrubMapOutput( "password=hunter2 done" )!;
            Require( kv.Contains( "password=[redacted]" ), "key kept, value redacted: " + kv );
            Require( !kv.Contains( "hunter2" ), "plain secret value never survives scrubbing" );
            Require( SafetyGates.ScrubMapOutput( (string?)null ) is null, "null stays null" );
            Require( SafetyGates.ScrubMapOutput( (JsonNode?)null ) is null, "null node stays null" );
        } );

        Check( "map audit log never contains a secret value", () =>
        {
            var (_, gates, root) = Gates( "map-audit-secret" );
            const string secret = "sk-testsecret-ABCDEFGH12345678";
            using ( var call = gates.BeginMapCall( "mcp", "map_validate",
                new JsonObject { ["map"] = "room.vmf", ["api_key"] = secret }, "room.vmf" ) )
                call.Complete( "ok" );
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_validate",
                new JsonObject { ["map"] = "../escape.vmf", ["api_key"] = secret }, "../escape.vmf" ), "escape with secret" );
            string log = AuditText( root );
            Require( !log.Contains( secret ), "secret value never in audit log" );
            Require( !log.Contains( "ABCDEFGH" ), "no secret fragment either" );
        } );

        Check( "map overwrite confirm refuses an existing target", () =>
        {
            var (_, gates, root) = Gates( "map-overwrite-confirm" );
            string target = Path.Combine( root, "room.bsp" );
            File.WriteAllText( target, "existing" );
            string resolved = gates.ResolveOwned( "room.bsp" );
            Require( string.Equals( resolved, Path.GetFullPath( target ), StringComparison.Ordinal ), "ResolveOwned passthrough" );
            Throws<SafetyRefusalException>( () => gates.RequireMapOverwriteConfirm( resolved, allowDestructive: false ), "existing without flag" );
            gates.RequireMapOverwriteConfirm( resolved, allowDestructive: true );
            gates.RequireMapOverwriteConfirm( gates.ResolveOwned( "new.bsp" ), allowDestructive: false );
        } );

        Check( "map definitions with a shell property are refused", () =>
        {
            var (_, gates, _) = Gates( "map-definitions" );
            var clean = new JsonArray
            {
                new JsonObject { ["name"] = "map_brush_create", ["inputSchema"] = new JsonObject { ["properties"] = new JsonObject { ["map"] = new JsonObject(), ["type"] = new JsonObject() } } },
                new JsonObject { ["name"] = "map_compile", ["inputSchema"] = new JsonObject { ["properties"] = new JsonObject { ["map"] = new JsonObject(), ["quality"] = new JsonObject() } } },
            };
            gates.ValidateMapDefinitions( clean );
            var forgedShellProp = new JsonArray
            {
                new JsonObject { ["name"] = "map_compile", ["inputSchema"] = new JsonObject { ["properties"] = new JsonObject { ["map"] = new JsonObject(), ["compiler_path"] = new JsonObject() } } },
            };
            Throws<SafetyRefusalException>( () => gates.ValidateMapDefinitions( forgedShellProp ), "compiler_path property" );
            var forgedShellTool = new JsonArray { new JsonObject { ["name"] = "map_compile" }, new JsonObject { ["name"] = "shell" } };
            Throws<SafetyRefusalException>( () => gates.ValidateMapDefinitions( forgedShellTool ), "shell tool" );
            var nameless = new JsonArray { new JsonObject { ["description"] = "no name" } };
            Throws<ArgumentException>( () => gates.ValidateMapDefinitions( nameless ), "definition without a name" );
        } );

        Check( "oversized map args refused", () =>
        {
            var (_, gates, root) = Gates( "map-oversized" );
            var big = new JsonObject { ["data"] = new string( 'x', 300_000 ) };
            Throws<SafetyRefusalException>( () => gates.BeginMapCall( "mcp", "map_validate", big, "room.vmf" ), "over MaxArgsBytes" );
            Require( AuditLines( root ).Length == 1, "refusal logged" );
        } );

        Check( "already-cancelled token refused by the map gate", () =>
        {
            var (_, gates, root) = Gates( "map-cancelled" );
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Throws<OperationCanceledException>( () => gates.BeginMapCall( "mcp", "map_validate",
                new JsonObject { ["map"] = "room.vmf" }, "room.vmf", ct: cts.Token ), "cancelled" );
            Require( AuditLines( root ).Length == 1, "refusal logged" );
        } );

        Check( "gate refuses a map run outside the run dir", () =>
        {
            string dir = Work( "gate-outside" );
            string run = Path.Combine( dir, "run" );
            Directory.CreateDirectory( run );
            GateCheck.RequireMapRun( "mcp", run, "room.vmf", null, sandbox: true );
            string Refusal( Action act )
            {
                try { act(); }
                catch ( GateRefusedException ex ) { return ex.Message; }
                throw new Exception( "expected the gate to refuse, it allowed the run" );
            }
            string outside = Refusal( () => GateCheck.RequireMapRun( "mcp", run, Path.Combine( dir, "other.vmf" ), null, sandbox: true ) );
            Require( outside.Contains( "outside the approved run" ), "absolute outside map refused: " + outside );
            string escape = Refusal( () => GateCheck.RequireMapRun( "mcp", run, "../outside.vmf", null, sandbox: true ) );
            Require( escape.Contains( "outside the approved run" ), "dot-dot escape refused: " + escape );
            string noApproval = Refusal( () => GateCheck.RequireMapRun( "mcp", run, "room.vmf", null, sandbox: false ) );
            Require( noApproval.Contains( "no approval" ), "no token and no sandbox refused: " + noApproval );
            string noMap = Refusal( () => GateCheck.RequireMapRun( "mcp", run, null, null, sandbox: true ) );
            Require( noMap.Contains( "needs its map" ), "missing map refused: " + noMap );
        } );

        Check( "approved token lets a map run inside its run dir through", () =>
        {
            const string id = "gate-1793384465-edf3c538";
            string dir = Work( "gate-token" );
            string confirm = Path.Combine( dir, "confirm" ), run = Path.Combine( dir, "run" );
            Directory.CreateDirectory( confirm );
            Directory.CreateDirectory( run );
            File.WriteAllText( Path.Combine( confirm, id + ".json" ), new JsonObject { ["id"] = id, ["status"] = "launched" }.ToJsonString() );
            File.WriteAllText( Path.Combine( confirm, id + ".token.json" ),
                new JsonObject { ["id"] = id, ["used_at"] = 1.0, ["run"] = run }.ToJsonString() );
            var savedDir = GateCheck.ConfirmDirOverride;
            var savedWait = GateCheck.RegisterWait;
            GateCheck.ConfirmDirOverride = confirm;
            GateCheck.RegisterWait = TimeSpan.FromMilliseconds( 300 );
            try
            {
                GateCheck.RequireMapRun( "mcp", run, "room.vmf", id, sandbox: false );
                // The same approval does NOT cover a map outside its run dir.
                try { GateCheck.RequireMapRun( "mcp", run, "../outside.vmf", id, sandbox: false ); }
                catch ( GateRefusedException ) { return; }
                throw new Exception( "approved token must not cover a map outside its run dir" );
            }
            finally { GateCheck.ConfirmDirOverride = savedDir; GateCheck.RegisterWait = savedWait; }
        } );
    }
}
