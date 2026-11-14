#nullable enable
// McpSafety.cs — the ONE shared safety layer for the MCP server (stdio JSON-RPC 2.0)
// and the desktop ops CLI. No per-surface copies: both entry points call the same
// SafetyGates methods, so a hole fixed here is fixed everywhere.
//
// Hook points (owned by the sibling tasks; this file only provides the guard):
//   * MCP transport (mcp-server-stdio-transport-and-protocol): call
//     ValidateDefinitions(AiSuiteTools.Definitions) once at startup, then wrap
//     every tools/call in BeginCall(surface: "mcp", ...) with destructiveConfirm
//     from the MCP confirm argument, and Complete/Refuse (or dispose) the
//     returned GuardedCall exactly once.
//   * Ops CLI (desktop-ops-cli-commands): call the same around every ops command
//     with surface "cli" and allowDestructive from its --allow-destructive flag
//     (the CLI worker owns parsing that flag).
//   * Program.cs --mcp / ops dispatch is NOT this task's.
//   * Map tools (hammer-ai-safety-gates-map): the map gate lives here too —
//     SafetyGates.BeginMapCall / ValidateMapDefinitions / ScrubMapOutput /
//     RequireMapOverwriteConfirm. The verb set (SafetyGates.MapTools) mirrors
//     the filed spec of hammer-ai-mcp-map-tools (McpMapTools.cs, worker 65);
//     at the time of writing that file has NOT landed, so the list is the
//     filed spec, not a live import. Seam for the integrator: at MCP startup
//     call ValidateMapDefinitions(McpMapTools.ListTools()); wrap every map
//     tools/call in BeginMapCall with the map reference from args["map"] (or
//     the session's active document) and destructiveConfirm from the MCP
//     confirm argument; scrub every map result with ScrubMapOutput before it
//     leaves the transport; gate map-output overwrites with
//     RequireMapOverwriteConfirm. Delete is expressed two ways and both are
//     gated: the explicit map_brush_delete / map_entity_delete verbs, and
//     action=delete/remove (or delete/remove=true) on the edit verbs.
//     Compile runs the native kernel only — a map call carrying a shell
//     command, a compiler path, or an external executable path is refused,
//     never executed.
// Root placement: this file lives at the Desktop root next to AiSuiteTools so the
// guard can call AiSuiteTools.Owned() directly instead of re-implementing the
// run sandbox. It never re-implements the Owned rules; every file argument is
// resolved through tools.Owned.
//   * packaging/CONSTRAINTS.md is a .ctrlrig constraint-parity inventory, not
//     safety constraints — nothing in this file is derived from it.
//   * core/PluginSdk.cs signing (HMAC-SHA256, unsigned add-ons rejected) is a
//     read-only reference for the "no unsigned code execution" principle; this
//     file does not depend on its types. The concrete form of that principle
//     here is ValidateDefinitions: no shell/exec tool may ever be exposed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace Marionette.Desktop;

/// <summary>Policy constants for the safety gates. Defaults are the production
/// policy; tests and embedders override individual values via the ctor.</summary>
public sealed record SafetyPolicy
{
    /// <summary>Whole-request size cap (JSON-RPC request bytes on MCP,
    /// argv+stdin cap on the CLI). 1 MiB.</summary>
    public int MaxRequestBytes { get; init; } = 1_048_576;
    /// <summary>Tool-arguments JSON size cap. 256 KiB.</summary>
    public int MaxArgsBytes { get; init; } = 262_144;
    /// <summary>Per-call deadline. 5 minutes.</summary>
    public TimeSpan DefaultCallTimeout { get; init; } = TimeSpan.FromMinutes( 5 );
    /// <summary>Master switch for destructive operations. False by default:
    /// destructive tools refuse unless the caller passes an explicit confirm
    /// (MCP) or --allow-destructive (CLI) per call.</summary>
    public bool AllowDestructive { get; init; } = false;

    public SafetyPolicy() { }

    public SafetyPolicy( int maxRequestBytes = 1_048_576, int maxArgsBytes = 262_144,
        TimeSpan defaultCallTimeout = default, bool allowDestructive = false )
    {
        MaxRequestBytes = maxRequestBytes;
        MaxArgsBytes = maxArgsBytes;
        DefaultCallTimeout = defaultCallTimeout == default ? TimeSpan.FromMinutes( 5 ) : defaultCallTimeout;
        AllowDestructive = allowDestructive;
    }
}

/// <summary>Refusal from a safety gate: the call was not executed. The transport
/// turns this into a JSON-RPC error (MCP) or a non-zero exit with the message
/// (CLI); the message never contains a resolved path or a secret value.</summary>
public sealed class SafetyRefusalException : InvalidOperationException
{
    public SafetyRefusalException( string message ) : base( message ) { }
    public SafetyRefusalException( string message, Exception inner ) : base( message, inner ) { }
}

/// <summary>The shared guard for the MCP server and the ops CLI. Constructed once
/// per run directory over an AiSuiteTools instance; every file argument is
/// resolved through that instance's Owned(), never through a second copy of
/// the sandbox rules.</summary>
public sealed class SafetyGates
{
    /// <summary>Tool names that must never exist on either surface. Checked
    /// case-insensitively, against both the live Definitions and any
    /// caller-supplied definition list.</summary>
    static readonly HashSet<string> ForbiddenTools = new( StringComparer.OrdinalIgnoreCase )
        { "shell", "exec", "run_command", "run_shell", "command", "execute", "shell_exec" };

    /// <summary>Tools that destroy user work unless explicitly confirmed.
    /// Export overwrites are path-dependent, not tool-dependent, and are
    /// gated separately by RequireOverwriteConfirm.</summary>
    static readonly HashSet<string> DestructiveTools = new( StringComparer.OrdinalIgnoreCase )
        { "remove_feature" };

    // ---- Map tools (hammer-ai-safety-gates-map) ---------------------------------
    /// <summary>Every map AI tool: the filed hammer-ai-mcp-map-tools verb set
    /// (map_brush_create/edit/clip/vertex, map_displacement_create/sculpt,
    /// map_face_material, map_entity_place/edit, map_io_add, map_group/
    /// visgroup, map_import_export, map_compile, map_validate,
    /// map_bake_lightmap, map_navmesh, map_skybox_cubemap) plus the two
    /// explicit delete verbs. McpMapTools.cs has not landed yet, so this is
    /// the filed spec, not a live import — see the file header seam note.
    /// Compile runs the native kernel only: no map tool ever takes a shell
    /// command or an external program.</summary>
    public static readonly string[] MapTools =
    {
        "map_brush_create", "map_brush_edit", "map_brush_clip", "map_brush_vertex",
        "map_displacement_create", "map_displacement_sculpt",
        "map_face_material",
        "map_entity_place", "map_entity_edit",
        "map_io_add",
        "map_group", "map_visgroup",
        "map_import_export", "map_compile", "map_validate",
        "map_bake_lightmap", "map_navmesh", "map_skybox_cubemap",
        "map_brush_delete", "map_entity_delete",
    };
    static readonly HashSet<string> MapToolNames = new( MapTools, StringComparer.OrdinalIgnoreCase );

    /// <summary>Argument keys that name an external program. Map tools take a
    /// map document reference + params; they never take a shell command, a
    /// compiler binary, or a script to run. Any such key with a non-empty
    /// value is refused (see ValidateMapShellArgs). NATIVE ONLY: compile runs
    /// the native kernel, never Hammer/Hammer++/VBSP/VVIS/VRAD.</summary>
    static readonly HashSet<string> ShellArgKeys = new( StringComparer.OrdinalIgnoreCase )
        { "command", "shell", "executable", "compiler_path", "compiler", "vbsp", "vvis", "vrad",
          "external_command", "shell_command", "cmd", "tool_path", "script_path" };

    static readonly string[] ExecutableExtensions = { ".exe", ".bat", ".sh", ".cmd", ".com", ".ps1" };
    /// <summary>Hammer compiler names, matched as whole path segments only
    /// ("tools/vbsp" is refused; "hammer/room.vmf" is not — a directory may
    /// legitimately be called hammer, and "vbsp_test.vmf" is a file name,
    /// not the compiler).</summary>
    static readonly string[] HammerBinaries = { "vbsp", "vvis", "vrad" };

    /// <summary>True when the tool name is a map AI tool.</summary>
    public static bool IsMapTool( string tool ) => tool is not null && MapToolNames.Contains( tool );

    /// <summary>True when a map call destroys user work unless explicitly
    /// confirmed: the explicit delete verbs; edit verbs with a delete/remove
    /// action (or delete/remove flag); any map tool with overwrite=true or
    /// mode=overwrite (map overwrite, export overwrite); a Final compile
    /// (map_compile with quality/mode=final or final=true), which overwrites
    /// its output. Everything else — create, plain edit, validate, fast
    /// compile — is non-destructive.</summary>
    public static bool IsMapDestructive( string tool, JsonNode? args )
    {
        if ( tool is null ) return false;
        if ( tool.Equals( "map_brush_delete", StringComparison.OrdinalIgnoreCase )
            || tool.Equals( "map_entity_delete", StringComparison.OrdinalIgnoreCase ) ) return true;
        if ( args is not JsonObject obj ) return false;
        if ( obj["overwrite"] is JsonValue ow && ow.TryGetValue<bool>( out bool overwrite ) && overwrite ) return true;
        if ( obj["mode"] is JsonValue mode && mode.TryGetValue<string>( out string? modeText )
            && modeText.Equals( "overwrite", StringComparison.OrdinalIgnoreCase ) ) return true;
        if ( tool.Equals( "map_compile", StringComparison.OrdinalIgnoreCase ) )
        {
            if ( obj["mode"] is JsonValue finalMode && finalMode.TryGetValue<string>( out string? finalModeText )
                && finalModeText.Equals( "final", StringComparison.OrdinalIgnoreCase ) ) return true;
            if ( obj["quality"] is JsonValue quality && quality.TryGetValue<string>( out string? qualityText )
                && qualityText.Equals( "final", StringComparison.OrdinalIgnoreCase ) ) return true;
            if ( obj["final"] is JsonValue final && final.TryGetValue<bool>( out bool finalFlag ) && finalFlag ) return true;
        }
        if ( tool.Equals( "map_brush_edit", StringComparison.OrdinalIgnoreCase )
            || tool.Equals( "map_entity_edit", StringComparison.OrdinalIgnoreCase ) )
        {
            if ( obj["action"] is JsonValue action && action.TryGetValue<string>( out string? actionText )
                && ( actionText.Equals( "delete", StringComparison.OrdinalIgnoreCase )
                    || actionText.Equals( "remove", StringComparison.OrdinalIgnoreCase ) ) ) return true;
            if ( obj["delete"] is JsonValue del && del.TryGetValue<bool>( out bool delete ) && delete ) return true;
            if ( obj["remove"] is JsonValue rem && rem.TryGetValue<bool>( out bool remove ) && remove ) return true;
        }
        return false;
    }

    static bool LooksLikeExecutablePath( string text )
    {
        if ( string.IsNullOrWhiteSpace( text ) ) return false;
        foreach ( string ext in ExecutableExtensions )
            if ( text.EndsWith( ext, StringComparison.OrdinalIgnoreCase ) ) return true;
        foreach ( string segment in text.Split( new[] { '/', '\\' } ) )
            if ( HammerBinaries.Contains( segment, StringComparer.OrdinalIgnoreCase ) ) return true;
        return false;
    }

    static bool LooksLikeMapPath( string text ) => LooksLikePath( text )
        || text.EndsWith( ".vmf", StringComparison.OrdinalIgnoreCase )
        || text.EndsWith( ".map", StringComparison.OrdinalIgnoreCase );

    /// <summary>Refuse any shell path in a map call's args: a shell-ish
    /// argument key with a non-empty value, or any string value naming an
    /// external executable / Hammer compiler. The refusal names the key,
    /// never the value (the value may be a path or a secret).</summary>
    void ValidateMapShellArgs( JsonNode? node )
    {
        switch ( node )
        {
            case JsonObject obj:
                foreach ( var kv in obj )
                {
                    bool empty = kv.Value is null
                        || ( kv.Value is JsonValue jv && jv.TryGetValue<string>( out string? s ) && string.IsNullOrWhiteSpace( s ) );
                    if ( ShellArgKeys.Contains( kv.Key ) && !empty )
                        throw new SafetyRefusalException( $"Map tools never execute shell commands: argument '{kv.Key}' is refused." );
                    ValidateMapShellArgs( kv.Value );
                }
                break;
            case JsonArray arr:
                foreach ( var item in arr ) ValidateMapShellArgs( item );
                break;
            case JsonValue value:
                if ( value.TryGetValue<string>( out string? text ) && LooksLikeExecutablePath( text ) )
                    throw new SafetyRefusalException( "Map tools never execute external programs: an executable path argument is refused." );
                break;
        }
    }

    /// <summary>JSON keys whose string value is always a secret. A key matches
    /// when it equals one of SecretKeyNames or ends in _api_key / _token /
    /// _secret / _password / _passwd (case-insensitive). The KnownKeys names
    /// from AiChildEnvironment are listed explicitly too (they end in
    /// _API_KEY anyway).</summary>
    static readonly string[] SecretKeySuffixes = { "_api_key", "_token", "_secret", "_password", "_passwd" };
    static readonly string[] SecretKeyNames = { "api_key", "token", "secret", "password", "passwd", "auth", "authorization",
        "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GEMINI_API_KEY", "BRAVE_SEARCH_API_KEY" };

    static readonly Regex SkKey = new( @"sk-[A-Za-z0-9\-_]{16,}", RegexOptions.Compiled );
    static readonly Regex GithubToken = new( @"(?:ghp_|gho_|ghu_|ghs_|ghr_|github_pat_)[A-Za-z0-9_]{10,}", RegexOptions.Compiled );
    static readonly Regex AwsAccessKey = new( @"AKIA[0-9A-Z]{16}", RegexOptions.Compiled );
    static readonly Regex BearerToken = new( @"Bearer\s+[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.Compiled );
    // The SecretAssignment replacement keeps the key ("password=") and redacts
    // only the value: the group is the key+separator, the value is group 2.
    // (An earlier form replaced with "$1=[redacted]" where $1 was the VALUE,
    // which echoed a non-shape secret such as password=hunter2 back into the
    // output; the shape passes only happened to catch sk-/ghp_-shaped ones.)
    static readonly Regex SecretAssignment = new( @"\b([A-Za-z0-9_]*?(?:api_key|token|secret|password|passwd|auth|authorization)\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;""']+)", RegexOptions.Compiled | RegexOptions.IgnoreCase );

    static readonly JsonSerializerOptions CanonicalOptions = new()
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly AiSuiteTools _tools;
    readonly SafetyPolicy _policy;
    readonly HashSet<string> _knownTools;
    readonly string _auditPath;
    readonly object _auditLock = new();

    public SafetyPolicy Policy => _policy;
    /// <summary>Absolute path of the append-only audit log (inside the run dir).</summary>
    public string AuditPath => _auditPath;

    public SafetyGates( AiSuiteTools tools, string auditFileName = "audit.log", SafetyPolicy? policy = null )
    {
        _tools = tools ?? throw new ArgumentNullException( nameof( tools ) );
        _policy = policy ?? new SafetyPolicy();
        _knownTools = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        foreach ( var def in AiSuiteTools.Definitions )
            if ( def is JsonObject obj && obj["name"] is JsonValue v && v.TryGetValue<string>( out string? name ) && !string.IsNullOrEmpty( name ) )
                _knownTools.Add( name );
        _auditPath = _tools.Owned( auditFileName );
    }

    /// <summary>The "no shell/exec tool, ever" gate. Throws SafetyRefusalException
    /// if any definition names a forbidden tool (case-insensitive), or
    /// ArgumentException if a definition has no name. The MCP server calls this
    /// once at startup on AiSuiteTools.Definitions; the CLI calls it before
    /// dispatch.</summary>
    public void ValidateDefinitions( JsonArray definitions )
    {
        if ( definitions is null ) throw new ArgumentNullException( nameof( definitions ) );
        foreach ( var def in definitions )
        {
            string? name = def is JsonObject obj && obj["name"] is JsonValue v && v.TryGetValue<string>( out string? n ) ? n : null;
            if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Tool definition without a name." );
            if ( ForbiddenTools.Contains( name ) )
                throw new SafetyRefusalException( $"Tool '{name}' is forbidden: no shell/exec tool may ever be exposed." );
        }
    }

    /// <summary>Validate the live tool list.</summary>
    public void ValidateDefinitions() => ValidateDefinitions( AiSuiteTools.Definitions );

    /// <summary>The "no shell/exec tool, ever" gate for the map tools, plus
    /// the "no shell-shaped argument" gate: a map tool definition that
    /// declares a shell/command/compiler argument is refused even when the
    /// tool name itself is innocent. Call once at startup on
    /// McpMapTools.ListTools() (integrator; worker 65 owns that list).</summary>
    public void ValidateMapDefinitions( JsonArray definitions )
    {
        if ( definitions is null ) throw new ArgumentNullException( nameof( definitions ) );
        foreach ( var def in definitions )
        {
            string? name = def is JsonObject obj && obj["name"] is JsonValue v && v.TryGetValue<string>( out string? n ) ? n : null;
            if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Tool definition without a name." );
            if ( ForbiddenTools.Contains( name ) )
                throw new SafetyRefusalException( $"Tool '{name}' is forbidden: no shell/exec tool may ever be exposed." );
            if ( def is JsonObject dobj && dobj["inputSchema"] is JsonObject schema && schema["properties"] is JsonObject props )
                foreach ( var prop in props )
                    if ( ShellArgKeys.Contains( prop.Key ) )
                        throw new SafetyRefusalException( $"Map tool '{name}' declares a forbidden shell argument '{prop.Key}': map tools never execute shell commands." );
        }
    }

    /// <summary>True when the tool destroys user work unless explicitly confirmed.</summary>
    public static bool IsDestructive( string tool ) => tool is not null && DestructiveTools.Contains( tool );

    /// <summary>Passthrough to tools.Owned: resolve a run-relative path inside
    /// the run dir, refusing rooted paths, escapes and symlink traversal.</summary>
    public string ResolveOwned( string relative ) => _tools.Owned( relative );

    /// <summary>Reuse AiChildEnvironment.ScrubSecrets: strip provider keys from a
    /// not-yet-started child process's environment. Never copied here.</summary>
    public void ScrubChildEnvironment( ProcessStartInfo start ) => AiChildEnvironment.ScrubSecrets( start );

    /// <summary>Refuse an export overwrite unless explicitly allowed. The caller
    /// resolves the target through Owned first; the guard refuses when the file
    /// already exists and neither the per-call flag nor the policy allows it.
    /// The refusal never echoes the full path (file-existence oracle).</summary>
    public void RequireOverwriteConfirm( string resolvedTargetPath, bool allowDestructive )
    {
        if ( string.IsNullOrWhiteSpace( resolvedTargetPath ) ) throw new ArgumentException( "Target path is required.", nameof( resolvedTargetPath ) );
        bool effectiveAllow = allowDestructive || _policy.AllowDestructive;
        if ( !effectiveAllow && File.Exists( resolvedTargetPath ) )
            throw new SafetyRefusalException( "Export target already exists; refusing to overwrite without an explicit --allow-destructive flag (CLI) or confirm (MCP): " + Path.GetFileName( resolvedTargetPath ) );
    }

    /// <summary>Map-output flavour of RequireOverwriteConfirm: refuse to
    /// overwrite an existing map/export/compile output without an explicit
    /// flag. The caller resolves the target through Owned first. The refusal
    /// names only the file name, never the full path.</summary>
    public void RequireMapOverwriteConfirm( string resolvedTargetPath, bool allowDestructive )
    {
        if ( string.IsNullOrWhiteSpace( resolvedTargetPath ) ) throw new ArgumentException( "Target path is required.", nameof( resolvedTargetPath ) );
        bool effectiveAllow = allowDestructive || _policy.AllowDestructive;
        if ( !effectiveAllow && File.Exists( resolvedTargetPath ) )
            throw new SafetyRefusalException( "Map output already exists; refusing to overwrite without an explicit --allow-destructive flag (CLI) or confirm (MCP): " + Path.GetFileName( resolvedTargetPath ) );
    }

    /// <summary>Validate a call before it executes, in order: (a) tool known and
    /// not forbidden; (b) args JSON within MaxArgsBytes and the whole request
    /// within MaxRequestBytes (pass the transport's raw request byte count; when
    /// omitted the args size is used as the request size); (c) every
    /// file-looking string arg inside the run via tools.Owned, using the same
    /// IsFileParam/LooksLikePath rule as AiSuiteTools.Assign; (d) destructive
    /// tools refused without an explicit confirm/flag; (e) an already-cancelled
    /// token refused. A refusal writes one "refused" audit entry, then throws.
    /// On success returns a GuardedCall recording StartedAt + Deadline; the
    /// caller must Complete/Refuse (or dispose) it exactly once.</summary>
    public GuardedCall BeginCall( string surface, string tool, JsonNode? args,
        bool destructiveConfirm = false, bool allowDestructive = false,
        CancellationToken ct = default, int requestBytes = -1 )
    {
        var startedAt = DateTimeOffset.UtcNow;
        string hash = ArgsSha256( args );
        try
        {
            if ( surface is not "mcp" and not "cli" )
                throw new SafetyRefusalException( "Surface must be 'mcp' or 'cli'." );
            if ( string.IsNullOrWhiteSpace( tool ) )
                throw new SafetyRefusalException( "Tool name is required." );
            if ( ForbiddenTools.Contains( tool ) )
                throw new SafetyRefusalException( $"Tool '{tool}' is forbidden: no shell/exec tool may ever be exposed." );
            if ( !_knownTools.Contains( tool ) )
                throw new SafetyRefusalException( $"Unknown tool '{tool}'." );
            string argsJson = CanonicalArgsJson( args );
            int argsBytes = Encoding.UTF8.GetByteCount( argsJson );
            if ( argsBytes > _policy.MaxArgsBytes )
                throw new SafetyRefusalException( $"Arguments exceed {_policy.MaxArgsBytes} bytes." );
            int wholeRequestBytes = requestBytes < 0 ? argsBytes : requestBytes;
            if ( wholeRequestBytes > _policy.MaxRequestBytes )
                throw new SafetyRefusalException( $"Request exceeds {_policy.MaxRequestBytes} bytes." );
            ValidateFileArgs( args );
            if ( IsDestructive( tool ) && !destructiveConfirm && !allowDestructive && !_policy.AllowDestructive )
                throw new SafetyRefusalException( $"Destructive tool '{tool}' requires an explicit confirm (MCP) or --allow-destructive (CLI)." );
            ct.ThrowIfCancellationRequested();
        }
        catch ( Exception ex ) when ( ex is SafetyRefusalException or OperationCanceledException or ArgumentException )
        {
            WriteAudit( surface ?? "?", tool ?? "?", hash, "refused", ex.Message, startedAt );
            throw;
        }
        return new GuardedCall( this, surface, tool, hash, startedAt, _policy.DefaultCallTimeout );
    }

    /// <summary>Validate a MAP tool call before it executes — the map
    /// counterpart of BeginCall, same policy, same audit log, plus the map
    /// reference: (a) tool known, in MapTools, not forbidden; (b) the map
    /// reference present and, when it names a map file, inside the run via
    /// tools.Owned; (c) no shell/command/compiler argument and no external
    /// executable path anywhere in the args (compile runs the native kernel
    /// only); (d) size limits; (e) every file-looking string arg inside the
    /// run via tools.Owned; (f) destructive map calls refused without an
    /// explicit confirm/flag; (g) an already-cancelled token refused. A
    /// refusal writes one "refused" audit entry carrying the map, then
    /// throws. On success returns a GuardedCall whose audit entries carry
    /// the map, so every map call is logged with the map + operation. The
    /// transport passes the map reference from args["map"] (or the session's
    /// active document); the guard never executes the tool.</summary>
    public GuardedCall BeginMapCall( string surface, string tool, JsonNode? args, string map,
        bool destructiveConfirm = false, bool allowDestructive = false,
        CancellationToken ct = default, int requestBytes = -1 )
    {
        var startedAt = DateTimeOffset.UtcNow;
        string hash = ArgsSha256( args );
        try
        {
            if ( surface is not "mcp" and not "cli" )
                throw new SafetyRefusalException( "Surface must be 'mcp' or 'cli'." );
            if ( string.IsNullOrWhiteSpace( tool ) )
                throw new SafetyRefusalException( "Tool name is required." );
            if ( ForbiddenTools.Contains( tool ) )
                throw new SafetyRefusalException( $"Tool '{tool}' is forbidden: no shell/exec tool may ever be exposed." );
            if ( !MapToolNames.Contains( tool ) )
                throw new SafetyRefusalException( $"Unknown map tool '{tool}'." );
            if ( string.IsNullOrWhiteSpace( map ) )
                throw new SafetyRefusalException( "A map tool call needs its map document reference." );
            string argsJson = CanonicalArgsJson( args );
            int argsBytes = Encoding.UTF8.GetByteCount( argsJson );
            if ( argsBytes > _policy.MaxArgsBytes )
                throw new SafetyRefusalException( $"Arguments exceed {_policy.MaxArgsBytes} bytes." );
            int wholeRequestBytes = requestBytes < 0 ? argsBytes : requestBytes;
            if ( wholeRequestBytes > _policy.MaxRequestBytes )
                throw new SafetyRefusalException( $"Request exceeds {_policy.MaxRequestBytes} bytes." );
            ValidateMapShellArgs( args );
            if ( LooksLikeMapPath( map ) )
            {
                try { _tools.Owned( map ); }
                catch ( ArgumentException ) { throw new SafetyRefusalException( "Map reference is outside the run directory." ); }
            }
            ValidateFileArgs( args );
            if ( IsMapDestructive( tool, args ) && !destructiveConfirm && !allowDestructive && !_policy.AllowDestructive )
                throw new SafetyRefusalException( $"Destructive map tool '{tool}' requires an explicit confirm (MCP) or --allow-destructive (CLI)." );
            ct.ThrowIfCancellationRequested();
        }
        catch ( Exception ex ) when ( ex is SafetyRefusalException or OperationCanceledException or ArgumentException )
        {
            WriteAudit( surface ?? "?", tool ?? "?", hash, "refused", ex.Message, startedAt, map );
            throw;
        }
        return new GuardedCall( this, surface, tool, hash, startedAt, _policy.DefaultCallTimeout, map );
    }

    void ValidateFileArgs( JsonNode? node ) => ValidateFileArgs( node, null );

    void ValidateFileArgs( JsonNode? node, string? name )
    {
        switch ( node )
        {
            case JsonObject obj:
                foreach ( var kv in obj ) ValidateFileArgs( kv.Value, kv.Key );
                break;
            case JsonArray arr:
                foreach ( var item in arr ) ValidateFileArgs( item, name );
                break;
            case JsonValue value:
                if ( name is not null && value.TryGetValue<string>( out string? text ) && text is not null
                    && IsFileParam( name ) && LooksLikePath( text ) )
                {
                    try { _tools.Owned( text ); }
                    catch ( ArgumentException ex ) { throw new SafetyRefusalException( "File argument refused: " + ex.Message, ex ); }
                }
                break;
        }
    }

    // Mirrored verbatim from AiSuiteTools.IsFileParam / LooksLikePath (private
    // static there): the guard must apply the exact same file-argument rule as
    // the tool layer. Do not invent a different rule.
    static bool IsFileParam( string name ) => name is not null && ( name.Contains( "Path", StringComparison.OrdinalIgnoreCase )
        || name.Contains( "File", StringComparison.OrdinalIgnoreCase ) || name.Contains( "Source", StringComparison.OrdinalIgnoreCase )
        || name.Contains( "Import", StringComparison.OrdinalIgnoreCase ) || name.Contains( "Folder", StringComparison.OrdinalIgnoreCase )
        || name.Contains( "Alpha", StringComparison.OrdinalIgnoreCase ) );
    static bool LooksLikePath( string text ) => !string.IsNullOrWhiteSpace( text ) && ( text.Contains( '/' ) || text.Contains( '\\' )
        || text.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) || text.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase )
        || text.EndsWith( ".obj", StringComparison.OrdinalIgnoreCase ) || text.EndsWith( ".effigy", StringComparison.OrdinalIgnoreCase )
        || text.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) || text.EndsWith( ".json", StringComparison.OrdinalIgnoreCase ) );

    /// <summary>Redact secret values from free text: key=value assignments for
    /// secret-named keys, then known secret shapes (sk- keys, GitHub tokens,
    /// AKIA keys, Bearer tokens). Never throws; on unexpected failure returns
    /// "[redacted]" rather than leaking the input.</summary>
    public static string? ScrubOutput( string? text )
    {
        if ( text is null ) return null;
        try
        {
            string scrubbed = SecretAssignment.Replace( text, "$1[redacted]" );
            scrubbed = SkKey.Replace( scrubbed, "[redacted]" );
            scrubbed = GithubToken.Replace( scrubbed, "[redacted]" );
            scrubbed = AwsAccessKey.Replace( scrubbed, "[redacted]" );
            scrubbed = BearerToken.Replace( scrubbed, "Bearer [redacted]" );
            return scrubbed;
        }
        catch { return "[redacted]"; }
    }

    /// <summary>Redact secret values from a JSON tree (deep clone; the input is
    /// untouched): any string value under a secret-named key becomes
    /// "[redacted]", and any other string value is passed through
    /// ScrubOutput(string) for secret shapes. Never throws.</summary>
    public static JsonNode? ScrubOutput( JsonNode? node )
    {
        if ( node is null ) return null;
        try { return ScrubNode( node.DeepClone() ); }
        catch { return JsonValue.Create( "[redacted]" ); }
    }

    /// <summary>Scrub a map tool result, then verify no secret shape
    /// survived: if anything secret-looking remains after scrubbing, the
    /// whole result is blocked ("[redacted]") rather than leaked. The
    /// transport runs every map tool result through this before it leaves.
    /// Never throws.</summary>
    public static string? ScrubMapOutput( string? text )
    {
        if ( text is null ) return null;
        string? scrubbed = ScrubOutput( text );
        if ( scrubbed is not null && HasSecretShape( scrubbed ) ) return "[redacted]";
        return scrubbed;
    }

    public static JsonNode? ScrubMapOutput( JsonNode? node )
    {
        if ( node is null ) return null;
        try
        {
            JsonNode? scrubbed = ScrubOutput( node );
            if ( scrubbed is not null && HasSecretShape( scrubbed.ToJsonString() ) ) return JsonValue.Create( "[redacted]" );
            return scrubbed;
        }
        catch { return JsonValue.Create( "[redacted]" ); }
    }

    /// <summary>True when the text still contains a secret SHAPE (sk- key,
    /// GitHub token, AKIA key, Bearer token). Deliberately not the
    /// key=value rule: a scrubbed "password=[redacted]" remnant is safe
    /// and must not trip the verifier.</summary>
    static bool HasSecretShape( string text )
        => SkKey.IsMatch( text ) || GithubToken.IsMatch( text ) || AwsAccessKey.IsMatch( text )
            || BearerToken.IsMatch( text );

    static JsonNode? ScrubNode( JsonNode? node )
    {
        switch ( node )
        {
            case JsonObject obj:
                foreach ( var kv in obj.ToList() )
                {
                    if ( IsSecretKey( kv.Key ) ) obj[kv.Key] = "[redacted]";
                    else
                    {
                        var scrubbed = ScrubNode( kv.Value );
                        if ( !ReferenceEquals( scrubbed, kv.Value ) ) obj[kv.Key] = scrubbed;
                    }
                }
                return obj;
            case JsonArray arr:
                for ( int i = 0; i < arr.Count; i++ )
                {
                    var scrubbed = ScrubNode( arr[i] );
                    if ( !ReferenceEquals( scrubbed, arr[i] ) ) arr[i] = scrubbed;
                }
                return arr;
            case JsonValue value:
                if ( value.TryGetValue<string>( out string? text ) && text is not null )
                {
                    string? scrubbed = ScrubOutput( text );
                    if ( scrubbed is not null && !string.Equals( scrubbed, text, StringComparison.Ordinal ) )
                        return JsonValue.Create( scrubbed );
                }
                return value;
            default:
                return node;
        }
    }

    static bool IsSecretKey( string key )
    {
        foreach ( string name in SecretKeyNames )
            if ( string.Equals( key, name, StringComparison.OrdinalIgnoreCase ) ) return true;
        foreach ( string suffix in SecretKeySuffixes )
            if ( key.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ) ) return true;
        return false;
    }

    /// <summary>SHA256 hex (lowercase) of the canonical args JSON. The audit log
    /// stores this hash, never raw args.</summary>
    public static string ArgsSha256( JsonNode? args )
    {
        byte[] bytes = Encoding.UTF8.GetBytes( CanonicalArgsJson( args ) );
        return Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant();
    }

    static string CanonicalArgsJson( JsonNode? args )
    {
        var sorted = SortKeys( args ?? new JsonObject() );
        return sorted?.ToJsonString( CanonicalOptions ) ?? "{}";
    }

    // Canonical form: object keys sorted recursively (Ordinal), compact JSON.
    // Same shape as core/PluginSdk.cs AddonSignature.CanonicalJson, so a hash
    // computed here is stable across re-serializations.
    static JsonNode? SortKeys( JsonNode? node )
    {
        switch ( node )
        {
            case JsonObject obj:
            {
                var sorted = new JsonObject();
                foreach ( var kv in obj.OrderBy( kv => kv.Key, StringComparer.Ordinal ) )
                {
                    var sortedValue = SortKeys( kv.Value );
                    if ( sortedValue is not null ) sorted[kv.Key] = sortedValue;
                }
                return sorted;
            }
            case JsonArray arr:
            {
                var sorted = new JsonArray();
                foreach ( var item in arr ) sorted.Add( SortKeys( item ) );
                return sorted;
            }
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>Append one JSONL audit entry: timestamp (UTC ISO-8601), surface,
    /// tool, argsSha256 (hash, never raw args), result ("ok" | "refused" |
    /// "error" | "timeout"), reason (scrubbed, truncated), durationMs, and
    /// — for map calls only — "map", the map document reference, so every
    /// map call is logged with the map + operation. Appends under a lock.
    /// Best-effort: a logging failure never masks the call outcome, and no
    /// secret value is ever written.</summary>
    internal void WriteAudit( string surface, string tool, string argsSha256, string result, string reason, DateTimeOffset startedAt, string? map = null )
    {
        try
        {
            string scrubbedReason = ScrubOutput( reason ?? string.Empty ) ?? string.Empty;
            if ( scrubbedReason.Length > 500 ) scrubbedReason = scrubbedReason[..500];
            var entry = new JsonObject
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToString( "o" ),
                ["surface"] = surface,
                ["tool"] = tool,
                ["argsSha256"] = argsSha256,
                ["result"] = result,
                ["reason"] = scrubbedReason,
                ["durationMs"] = (long)( DateTimeOffset.UtcNow - startedAt ).TotalMilliseconds,
            };
            if ( !string.IsNullOrWhiteSpace( map ) )
            {
                string scrubbedMap = ScrubOutput( map ) ?? string.Empty;
                if ( scrubbedMap.Length > 200 ) scrubbedMap = scrubbedMap[..200];
                entry["map"] = scrubbedMap;
            }
            lock ( _auditLock )
                File.AppendAllText( _auditPath, entry.ToJsonString( CanonicalOptions ) + "\n" );
        }
        catch
        {
            // Audit is best-effort: a logging failure must never mask the call outcome.
        }
    }
}

/// <summary>A validated call. The caller runs the tool, then calls Complete or
/// Refuse exactly once; disposing without either audits "error: incomplete",
/// so every validated call gets an audit entry even on exception.</summary>
public sealed class GuardedCall : IDisposable
{
    readonly SafetyGates _gates;
    bool _finished;

    public string Surface { get; }
    public string Tool { get; }
    /// <summary>Map document reference for map calls; null for modelling calls.</summary>
    public string? Map { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset Deadline { get; }
    internal string ArgsSha256 { get; }

    internal GuardedCall( SafetyGates gates, string surface, string tool, string argsSha256, DateTimeOffset startedAt, TimeSpan timeout, string? map = null )
    {
        _gates = gates;
        Surface = surface;
        Tool = tool;
        Map = map;
        ArgsSha256 = argsSha256;
        StartedAt = startedAt;
        Deadline = startedAt + timeout;
    }

    /// <summary>Throw TimeoutException (and audit one "timeout" entry) once the
    /// deadline has passed. Call before/during long work; no-op after the call
    /// has already finished.</summary>
    public void CheckTimeout()
    {
        if ( _finished ) return;
        if ( DateTimeOffset.UtcNow > Deadline )
        {
            Finish( "timeout", $"Call to '{Tool}' exceeded its deadline." );
            throw new TimeoutException( $"Call to '{Tool}' exceeded its deadline." );
        }
    }

    public void Complete( string result ) => Finish( "ok", result ?? string.Empty );
    public void Refuse( string reason ) => Finish( "refused", reason ?? string.Empty );

    public void Dispose()
    {
        if ( !_finished ) Finish( "error", "incomplete: GuardedCall disposed without Complete or Refuse." );
    }

    void Finish( string result, string reason )
    {
        if ( _finished ) return;
        _finished = true;
        _gates.WriteAudit( Surface, Tool, ArgsSha256, result, reason, StartedAt, Map );
    }
}
