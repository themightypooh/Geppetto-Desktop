using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Marionette.Desktop;

// Red-team fixtures for the shared MCP/CLI safety gates (McpSafety.cs). Every
// refused fixture must also leave exactly one "refused" audit entry, and no
// fixture ever executes a tool — the guard only validates; execution is the
// transport/CLI's job and is not tested here.
internal static class McpSafetyTests
{
    public static void Register( TestRegistry registry, TestSettings settings )
    {
        void Check( string name, Action test ) => registry.Add( "mcp-safety", name, test );
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
            string root = Path.Combine( settings.WorkDirectory, "mcp-safety-" + name );
            var tools = new AiSuiteTools( root );
            return ( tools, new SafetyGates( tools, policy: policy ), root );
        }
        string[] AuditLines( string root ) => File.ReadAllLines( Path.Combine( root, "audit.log" ) );
        string AuditText( string root ) => File.ReadAllText( Path.Combine( root, "audit.log" ) );

        Check( "path escape and absolute path refused and logged", () =>
        {
            var (_, gates, root) = Gates( "escape" );
            Throws<SafetyRefusalException>( () => gates.BeginCall( "mcp", "catalog", new JsonObject { ["path"] = "../escape" } ), "../escape" );
            Throws<SafetyRefusalException>( () => gates.BeginCall( "mcp", "catalog", new JsonObject { ["path"] = "/etc/passwd" } ), "/etc/passwd" );
            Throws<SafetyRefusalException>( () => gates.BeginCall( "mcp", "no_such_tool", new JsonObject() ), "unknown tool" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 3, "three refusals logged, got " + lines.Length );
            Require( lines.All( l => l.Contains( "\"refused\"" ) ), "every entry refused" );
            Require( lines.All( l => !l.Contains( "../escape" ) && !l.Contains( "/etc/passwd" ) ), "no raw arg in audit" );
        } );

        Check( "symlink inside run pointing outside refused", () =>
        {
            var (_, gates, root) = Gates( "symlink" );
            string outside = Path.Combine( settings.WorkDirectory, "mcp-safety-outside.txt" );
            File.WriteAllText( outside, "outside" );
            Directory.CreateSymbolicLink( Path.Combine( root, "link" ), outside );
            Throws<SafetyRefusalException>( () => gates.BeginCall( "cli", "catalog", new JsonObject { ["path"] = "link/out.txt" } ), "symlink" );
            Require( AuditLines( root ).Length == 1, "refusal logged" );
        } );

        Check( "child environment secrets scrubbed", () =>
        {
            var (_, gates, _) = Gates( "env" );
            var start = new ProcessStartInfo { FileName = "dotnet" };
            start.Environment["OPENAI_API_KEY"] = "sk-secret-1";
            start.Environment["FOO_API_KEY"] = "sk-secret-2";
            start.Environment["KEEP_ME"] = "yes";
            gates.ScrubChildEnvironment( start );
            Require( !start.Environment.ContainsKey( "OPENAI_API_KEY" ), "OPENAI key removed" );
            Require( !start.Environment.ContainsKey( "FOO_API_KEY" ), "other _API_KEY removed" );
            Require( start.Environment["KEEP_ME"] == "yes", "unrelated env kept" );
        } );

        Check( "output json and text secrets redacted", () =>
        {
            var scrubbed = (JsonObject)SafetyGates.ScrubOutput( JsonNode.Parse( "{\"api_key\":\"sk-live-0123456789abcdef\",\"nested\":{\"token\":\"abc123\"},\"name\":\"box\",\"count\":3}" ) )!;
            Require( scrubbed["api_key"]?.GetValue<string>() == "[redacted]", "api_key redacted" );
            Require( scrubbed["nested"]?["token"]?.GetValue<string>() == "[redacted]", "nested token redacted" );
            Require( scrubbed["name"]?.GetValue<string>() == "box", "plain value kept" );
            Require( scrubbed["count"]?.GetValue<int>() == 3, "number kept" );
            string text = SafetyGates.ScrubOutput( "key sk-abcdefghij0123456789 and Bearer abcdefghij0123456789 end" )!;
            Require( !text.Contains( "sk-abcdefghij" ), "sk- shape redacted" );
            Require( text.Contains( "Bearer [redacted]" ), "bearer redacted" );
            string kv = SafetyGates.ScrubOutput( "OPENAI_API_KEY=sk-abcdefghij0123456789 done" )!;
            Require( !kv.Contains( "sk-abcdefghij" ), "key=value assignment redacted" );
            string gh = SafetyGates.ScrubOutput( "token ghp_abcdefghij0123456789 end" )!;
            Require( !gh.Contains( "ghp_abcdefghij" ), "github token redacted" );
        } );

        Check( "audit log never contains a secret value", () =>
        {
            var (_, gates, root) = Gates( "audit-secret" );
            const string secret = "sk-testsecret-ABCDEFGH12345678";
            using ( var call = gates.BeginCall( "mcp", "catalog", new JsonObject { ["api_key"] = secret, ["type"] = "Box" } ) )
                call.Complete( "ok" );
            Throws<SafetyRefusalException>( () => gates.BeginCall( "mcp", "catalog", new JsonObject { ["path"] = "../escape", ["api_key"] = secret } ), "escape with secret" );
            string log = AuditText( root );
            Require( !log.Contains( secret ), "secret value never in audit log" );
            Require( !log.Contains( "ABCDEFGH" ), "no secret fragment either" );
        } );

        Check( "destructive remove_feature needs explicit flag", () =>
        {
            var (_, gates, root) = Gates( "destructive" );
            Throws<SafetyRefusalException>( () => gates.BeginCall( "mcp", "remove_feature", new JsonObject { ["id"] = "f1" } ), "no flag" );
            Throws<SafetyRefusalException>( () => gates.BeginCall( "cli", "remove_feature", new JsonObject { ["id"] = "f1" }, destructiveConfirm: false, allowDestructive: false ), "explicit false" );
            Require( SafetyGates.IsDestructive( "remove_feature" ), "remove_feature classified destructive" );
            Require( !SafetyGates.IsDestructive( "catalog" ), "catalog not destructive" );
            // With a flag validation passes — but nothing is executed: the guard
            // never runs a tool, so no removal happens here by construction.
            using ( var call = gates.BeginCall( "mcp", "remove_feature", new JsonObject { ["id"] = "f1" }, destructiveConfirm: true ) )
                call.Complete( "ok" );
            using ( var call = gates.BeginCall( "cli", "remove_feature", new JsonObject { ["id"] = "f1" }, allowDestructive: true ) )
                call.Complete( "ok" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 4, "two refusals + two oks, got " + lines.Length );
            Require( lines[0].Contains( "\"refused\"" ) && lines[2].Contains( "\"ok\"" ), "results in order" );
        } );

        Check( "export overwrite needs explicit flag", () =>
        {
            var (_, gates, root) = Gates( "overwrite" );
            string target = Path.Combine( root, "out.obj" );
            File.WriteAllText( target, "existing" );
            string resolved = gates.ResolveOwned( "out.obj" );
            Require( string.Equals( resolved, Path.GetFullPath( target ), StringComparison.Ordinal ), "ResolveOwned passthrough" );
            Throws<SafetyRefusalException>( () => gates.RequireOverwriteConfirm( resolved, allowDestructive: false ), "existing without flag" );
            gates.RequireOverwriteConfirm( resolved, allowDestructive: true );
            gates.RequireOverwriteConfirm( gates.ResolveOwned( "new.obj" ), allowDestructive: false );
        } );

        Check( "oversized args refused", () =>
        {
            var (_, gates, root) = Gates( "oversized" );
            var big = new JsonObject { ["data"] = new string( 'x', 300_000 ) };
            Throws<SafetyRefusalException>( () => gates.BeginCall( "mcp", "catalog", big ), "over MaxArgsBytes" );
            Require( AuditLines( root ).Length == 1, "refusal logged" );
        } );

        Check( "forged definitions with a shell tool refused", () =>
        {
            var (_, gates, _) = Gates( "definitions" );
            gates.ValidateDefinitions( AiSuiteTools.Definitions );
            gates.ValidateDefinitions();
            var forged = new JsonArray { new JsonObject { ["name"] = "catalog" }, new JsonObject { ["name"] = "shell" } };
            Throws<InvalidOperationException>( () => gates.ValidateDefinitions( forged ), "shell tool" );
            var forgedCase = new JsonArray { new JsonObject { ["name"] = "EXEC" } };
            Throws<InvalidOperationException>( () => gates.ValidateDefinitions( forgedCase ), "case-insensitive exec" );
        } );

        Check( "expired deadline refused by CheckTimeout", () =>
        {
            var (_, gates, root) = Gates( "timeout", new SafetyPolicy( defaultCallTimeout: TimeSpan.FromMilliseconds( -1 ) ) );
            using var call = gates.BeginCall( "mcp", "catalog", new JsonObject() );
            Throws<TimeoutException>( () => call.CheckTimeout(), "deadline already past" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 1 && lines[0].Contains( "\"timeout\"" ), "timeout audited exactly once" );
        } );

        Check( "already-cancelled token refused", () =>
        {
            var (_, gates, root) = Gates( "cancelled" );
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Throws<OperationCanceledException>( () => gates.BeginCall( "mcp", "catalog", new JsonObject(), ct: cts.Token ), "cancelled" );
            Require( AuditLines( root ).Length == 1, "refusal logged" );
        } );

        Check( "valid call audit carries args hash not raw args", () =>
        {
            var (_, gates, root) = Gates( "hash" );
            using ( var call = gates.BeginCall( "cli", "catalog", new JsonObject { ["type"] = "Box" } ) )
                call.Complete( "ok" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 1, "one entry" );
            using var doc = JsonDocument.Parse( lines[0] );
            var entry = doc.RootElement;
            Require( entry.GetProperty( "surface" ).GetString() == "cli", "surface" );
            Require( entry.GetProperty( "tool" ).GetString() == "catalog", "tool" );
            string hash = entry.GetProperty( "argsSha256" ).GetString()!;
            Require( hash.Length == 64 && hash.All( c => "0123456789abcdef".Contains( c ) ), "64 hex chars" );
            using var sha = SHA256.Create();
            string expected = Convert.ToHexString( sha.ComputeHash( Encoding.UTF8.GetBytes( "{\"type\":\"Box\"}" ) ) ).ToLowerInvariant();
            Require( hash == expected, "hash matches canonical args" );
            Require( !lines[0].Contains( "Box" ), "raw args never logged" );
            Require( entry.GetProperty( "result" ).GetString() == "ok", "result ok" );
        } );

        Check( "audit log append-only", () =>
        {
            var (_, gates, root) = Gates( "append" );
            using ( var first = gates.BeginCall( "mcp", "catalog", new JsonObject { ["type"] = "Box" } ) )
                first.Complete( "ok" );
            string firstLine = AuditLines( root )[0];
            using ( var second = gates.BeginCall( "cli", "inspect", new JsonObject() ) )
                second.Complete( "ok" );
            string[] lines = AuditLines( root );
            Require( lines.Length == 2, "two lines, got " + lines.Length );
            Require( lines[0] == firstLine, "first line intact" );
            Require( lines[1].Contains( "\"inspect\"" ), "second entry is the second call" );
        } );

        Check( "disposed call without Complete audits incomplete", () =>
        {
            var (_, gates, root) = Gates( "incomplete" );
            using ( var call = gates.BeginCall( "mcp", "catalog", new JsonObject() ) )
            {
                // No Complete/Refuse: Dispose must audit "error: incomplete".
            }
            string[] lines = AuditLines( root );
            Require( lines.Length == 1 && lines[0].Contains( "\"error\"" ) && lines[0].Contains( "incomplete" ), "incomplete audited once" );
        } );
    }
}
