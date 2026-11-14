// McpModelingTools.cs — MCP modeling tools (task mcp-tools-modeling-operations).
//
// The tools side of the Geppetto Desktop MCP server (scope:
// ~/workspace/geppetto-desktop-mcp-cli-scope.md). The stdio transport task
// (mcp-server-stdio-transport-and-protocol) owns --mcp dispatch in Program.cs
// and the JSON-RPC 2.0 framing; this file is the transport-neutral provider
// it calls:
//
//   ListTools()                    -> tools/list: the MCP tool list.
//   CallAsync(name, args, ct)      -> tools/call: run one tool.
//
// The transport maps a CallAsync result to the MCP tools/call shape:
// content [{ type: "text", text: result.Text }], isError result.IsError.
// Error channels, by MCP convention:
//   - ArgumentException = invalid params: unknown tool, arguments that are
//     not a JSON object, or a missing required argument (taken from the
//     tool's own inputSchema). The transport maps this to JSON-RPC -32602.
//   - McpToolCallResult with IsError = true = the call was well-formed but
//     the tool failed (script failed, research disabled, export refused...).
//     The text is the tool's own error message, never a stack trace.
//   - OperationCanceledException propagates untouched (the transport owns
//     cancellation).
// Safety gating is applied by the transport, not here: it calls
// McpSafety.ValidateDefinitions on the tool list once at startup and wraps
// every tools/call in BeginCall/GuardedCall (destructive confirm, timeout,
// audit log). Do NOT double-wrap here. run_script is not in
// McpSafety.ForbiddenTools, but the transport should validate ListTools()
// (definitions + run_script), not just the raw Definitions, so the +1 tool
// is covered too.
//
// VERBATIM: ListTools is AiSuiteTools.Definitions (43 tools, same order,
// same name and description, inputSchema deep-equal to the definition's
// parameters) plus run_script appended last. The one mechanical change MCP
// requires is the key: Definitions carries the schema under "parameters";
// MCP tools/list requires "inputSchema". The schema CONTENT is untouched —
// no re-invented schemas, no new operation logic. run_script's own schema
// is { script: string (required) }; the verbs it executes are the
// BuildScript catalog (ApiCatalog.GenerateJsonSchema, also served verbatim
// as the geppetto://catalog resource by the resources ticket) — use the
// `catalog` tool to list feature types and their fields.
//
// run_script executes BuildScript lines against the current studio with the
// same safe shape as AiSuiteTools.RunScriptTool: it runs on a round-tripped
// copy, so a failed line cannot corrupt the live studio; it enforces the
// 1M-face AI geometry budget; on success it commits through Live and swaps
// the studio. An unknown verb fails the run with the BuildScript error
// ("Script failed line N: ..."). run_script takes ALL BuildScript verbs —
// that is the point of the tool — so there is no per-tool verb allow-list
// here; destructive/path gating belongs to the safety layer
// (mcp-cli-security-safety-gates), not to this mapping.
//
// Research tools (search, fetch, save_image, sample_colors, write_sheet)
// are listed verbatim; calling one without Research configured returns an
// IsError result ("Research is disabled...") — no network is touched.
// render needs a working EGL renderer and fails honestly without one.
//
// Known gap (documented, not hidden): run_script mutations do not bump
// AiSuiteTools.Mutations — the counter is private to AiSuiteTools and only
// its own paths bump it. Correctness holds (the caches are invalidated on
// every run_script commit), but the reported mutation count excludes
// run_script runs.
//
// This file owns no transport, no protocol framing and no Program.cs
// dispatch. Do not add them here.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Effigy;

namespace Marionette.Desktop;

public sealed class McpModelingTools
{
    public const string RunScriptToolName = "run_script";

    public AiSuiteTools Tools { get; }

    public McpModelingTools( AiSuiteTools tools )
    {
        Tools = tools ?? throw new ArgumentNullException( nameof( tools ) );
    }

    /// <summary>The result of one tools/call: the text the transport puts in
    /// the content block, and whether the tool failed.</summary>
    public sealed record McpToolCallResult( string Text, bool IsError );

    /// <summary>MCP tools/list: AiSuiteTools.Definitions verbatim (same
    /// order, name, description; inputSchema deep-equal to the definition's
    /// parameters) plus run_script appended last.</summary>
    public JsonArray ListTools()
    {
        var tools = new JsonArray();
        foreach ( var node in AiSuiteTools.Definitions )
        {
            var def = node!.AsObject();
            tools.Add( new JsonObject
            {
                ["name"] = def["name"]!.DeepClone(),
                ["description"] = def["description"]!.DeepClone(),
                ["inputSchema"] = def["parameters"]!.DeepClone(),
            } );
        }
        tools.Add( RunScriptDefinition() );
        return tools;
    }

    /// <summary>The run_script definition on its own (the same object
    /// ListTools appends): one required string argument, `script`.</summary>
    public static JsonObject RunScriptDefinition() => new()
    {
        ["name"] = RunScriptToolName,
        ["description"] = "Execute BuildScript lines against the current studio, one command per line (add, edit, set, remove, sculpt, weight, bake, export, rig verbs). Verbs and features are the BuildScript catalog — use the `catalog` tool to list feature types and their fields. Runs on a copy: a failed line leaves the studio unchanged. Returns the script log; an unknown verb fails the run with the BuildScript error.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["script"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "BuildScript lines, one command per line.",
                },
            },
            ["required"] = new JsonArray( "script" ),
        },
    };

    /// <summary>MCP tools/call. Unknown tool, non-object arguments, or a
    /// missing required argument throws ArgumentException (invalid params,
    /// -32602). A well-formed call that fails returns IsError with the
    /// tool's own message. Cancellation propagates.</summary>
    public async Task<McpToolCallResult> CallAsync( string name, JsonNode? args, CancellationToken ct )
    {
        if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Unknown suite tool." );
        var argsObject = args as JsonObject ?? throw new ArgumentException( "Tool arguments must be a JSON object." );
        if ( name == RunScriptToolName )
        {
            if ( argsObject["script"] is null ) throw new ArgumentException( "Missing required argument 'script' for tool 'run_script'." );
            return RunScript( argsObject, ct );
        }
        var def = FindDefinition( name ) ?? throw new ArgumentException( "Unknown suite tool." );
        foreach ( var required in RequiredOf( def ) )
            if ( argsObject[required] is null )
                throw new ArgumentException( $"Missing required argument '{required}' for tool '{name}'." );
        try
        {
            var text = await Tools.ExecuteAsync( name, argsObject, ct ).ConfigureAwait( false );
            return new McpToolCallResult( text ?? "", IsError: false );
        }
        catch ( OperationCanceledException ) { throw; }
        catch ( Exception e ) { return new McpToolCallResult( e.Message, IsError: true ); }
    }

    static JsonObject? FindDefinition( string name )
    {
        foreach ( var node in AiSuiteTools.Definitions )
        {
            var def = node!.AsObject();
            if ( def["name"]?.GetValue<string>() == name ) return def;
        }
        return null;
    }

    static IEnumerable<string> RequiredOf( JsonObject def )
    {
        if ( def["parameters"] is JsonObject schema && schema["required"] is JsonArray required )
            foreach ( var r in required )
                if ( r?.GetValue<string>() is string s ) yield return s;
    }

    McpToolCallResult RunScript( JsonObject args, CancellationToken ct )
    {
        ct.ThrowIfCancellationRequested();
        if ( args["script"] is not JsonValue scriptValue || !scriptValue.TryGetValue<string>( out var script ) )
            throw new ArgumentException( "run_script 'script' must be a string." );
        if ( string.IsNullOrWhiteSpace( script ) )
            return new McpToolCallResult( "run_script needs a script: one BuildScript command per line.", IsError: true );
        if ( Tools.Isolated )
            return new McpToolCallResult( "Script tools are not implemented by the isolated worker yet; run them in a non-isolated builder.", IsError: true );
        try
        {
            // Same safe shape as AiSuiteTools.RunScriptTool: run on a
            // round-tripped copy so a failed line cannot corrupt the live
            // studio; enforce the 1M-face AI geometry budget; commit through
            // Live on success, then swap + invalidate the caches.
            var candidate = StudioDocument.Read( StudioDocument.Write( Tools.Studio ) );
            var result = BuildScript.Run( candidate, script, Tools.Root );
            if ( !result.Ok )
                return new McpToolCallResult( $"Script failed line {result.ErrorLine}: {result.Error}", IsError: true );
            if ( candidate.TotalFaceCount > 1000000 )
                return new McpToolCallResult( "AI geometry budget exceeded.", IsError: true );
            Tools.Live?.Commit( candidate, RunScriptToolName, affected: candidate.Features.Select( f => f.Id ).ToArray() );
            Tools.Studio = candidate;
            Tools.InvalidateCache();
            return new McpToolCallResult( result.ToString(), IsError: false );
        }
        catch ( OperationCanceledException ) { throw; }
        catch ( Exception e ) { return new McpToolCallResult( e.Message, IsError: true ); }
    }
}
