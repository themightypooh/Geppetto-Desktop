using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Marionette.Desktop;

namespace Marionette.Desktop.Mcp;

/// <summary>
/// MCP server over stdio for Geppetto Desktop (board task
/// <c>mcp-server-stdio-transport-and-protocol</c>): newline-delimited
/// JSON-RPC 2.0 on stdin/stdout, no HTTP in v1, no new NuGet dependency
/// (System.Text.Json is the house JSON stack). Launched as
/// <c>marionette-desktop --mcp</c> (one-line dispatch in Program.Main).
///
/// Methods: <c>initialize</c>, <c>ping</c>, <c>tools/list</c>,
/// <c>tools/call</c>, <c>resources/list</c>, <c>resources/read</c>,
/// <c>prompts/list</c>, <c>prompts/get</c>, <c>shutdown</c>. Notifications
/// (requests without an id, e.g. <c>notifications/initialized</c>) are
/// acknowledged by silence. EOF on stdin is a clean shutdown (exit 0).
///
/// Rules the server never breaks: stdout carries protocol traffic only
/// (one JSON value per line); diagnostics go to stderr. A bad line yields a
/// JSON-RPC error object, never a crash, a hang, or a stack trace on stdout.
/// There is no <c>shell</c>/<c>exec</c> tool, ever (see
/// <c>mcp-cli-security-safety-gates</c>).
///
/// Seams for the sibling MCP tickets:
/// <list type="bullet">
/// <item><description>
/// Tools: <c>tools/list</c> is <c>McpModelingTools.ListTools()</c>
/// (AiSuiteTools definitions verbatim into MCP shape — the
/// <c>parameters</c> to <c>inputSchema</c> rename, nothing more — plus
/// <c>run_script</c> appended last) plus an <c>echo</c> tool. Only
/// <c>echo</c> executes here: any other tool name is dispatched to a handler
/// registered with <see cref="RegisterToolHandler"/>, or else to
/// <c>McpModelingTools.CallAsync</c> (which <c>mcp-tools-modeling-operations</c>
/// owns). A tool that reaches neither fails tools/call with -32602.
/// </description></item>
/// <item><description>
/// Map tools (<c>hammer-ai-mcp-map-tools</c>): <c>tools/list</c> appends
/// <c>McpMapTools.ListTools()</c> (the 18 map_* tools) after the modeling
/// tools; <c>tools/call</c> routes map_* names through
/// <c>SafetyGates.BeginMapCall</c> (the map gate from
/// <c>hammer-ai-safety-gates-map</c>: known-tool check against
/// <c>SafetyGates.MapTools</c>, shell-arg refusal, destructive confirm)
/// to <c>McpMapTools.CallAsync</c>, and scrubs results with
/// <c>SafetyGates.ScrubMapOutput</c>. The two delete verbs in
/// <c>SafetyGates.MapTools</c> (map_brush_delete, map_entity_delete)
/// have no implementation yet: they pass the gate and fail as unknown
/// map tools (-32602), and are not listed.
/// </description></item>
/// <item><description>
/// Safety (<c>mcp-cli-security-safety-gates</c>, already landed): the server
/// calls <c>SafetyGates.ValidateDefinitions</c> once at startup — on the
/// live definitions AND on the full MCP list (definitions + run_script), so
/// the +1 tool is covered too — wraps every non-echo tools/call in
/// <c>BeginCall("mcp", ...)</c> (known-tool check, arg/request size caps,
/// file args inside the run dir, destructive tools need an explicit
/// <c>confirm: true</c> argument), and scrubs every tool result with
/// <c>SafetyGates.ScrubOutput</c> before it leaves. The run dir (and its
/// append-only audit log) is a per-process temp dir; <c>echo</c> bypasses
/// <c>BeginCall</c> because it is not an AiSuiteTools tool and performs no
/// operation, but its output is still scrubbed and the 1 MiB request cap in
/// the loop covers it.
/// KNOWN GAP (flagged for the integrator / security ticket, not hidden):
/// <c>BeginCall</c> builds its known-tool set from AiSuiteTools.Definitions
/// only, so it refuses <c>run_script</c> — an MCP-only tool defined by
/// <c>McpModelingTools</c>. Until <c>SafetyGates</c> learns the tool (extend
/// the known set or add a RegisterKnownTool API), run_script takes a manual
/// path: the arg/request size caps are enforced here, the forbidden check
/// runs at startup on the full list, and the call is dispatched to
/// <c>McpModelingTools.CallAsync</c> — but it gets NO GuardedCall audit
/// entry and no verb-level destructive gating (the script text is opaque to
/// the gate). Do not "fix" this by editing SafetyGates here; that file is
/// the security ticket's, and this gap is theirs / the integrator's to close.
/// </description></item>
/// <item><description>
/// Resources/prompts: the handlers delegate to <c>McpResources</c>
/// (<c>mcp-resources-prompts-document-mesh-tests</c>, already landed).
/// <see cref="McpResources.ReadResource"/> / <see cref="McpResources.GetPrompt"/>
/// throw <see cref="ArgumentException"/> for unknown URIs, prompts and
/// missing required arguments; the loop maps that to -32602, per that
/// provider's documented contract. Live state comes in through
/// <see cref="DocumentSource"/>; until the host fills it, the document
/// resources honestly report open:false / recorded:false.
/// </description></item>
/// <item><description>
/// Capabilities in <c>initialize</c> are built from the registered methods,
/// so a method registered later (by any ticket, before the first initialize)
/// is advertised automatically.
/// </description></item>
/// </list>
/// </summary>
public static class McpServer
{
	public const string ProtocolVersion = "2024-11-05";
	public const string ServerName = "geppetto-desktop";
	public const string ServerVersion = "1.0.0";

	/// <summary>
	/// A tool executor: takes the call's arguments object and returns the full
	/// tools/call result object (<c>{content:[{type:"text",text:...}],isError:bool}</c>).
	/// Registered by <c>mcp-tools-modeling-operations</c>; until then only "echo" executes.
	/// </summary>
	public delegate JsonObject McpToolHandler( JsonObject arguments );

	/// <summary>
	/// A method handler: takes the request params (or null when the request sent none)
	/// and returns the result object. Used for methods later tickets own; handlers
	/// registered here run inside the same loop.
	/// </summary>
	public delegate JsonObject McpMethodHandler( JsonNode? paramsNode );

	/// <summary>
	/// Live document/test state for the read-only resources. The host fills
	/// <c>CurrentDocument</c> / <c>LatestTestRun</c>; until it does, the document
	/// resources honestly report open:false / recorded:false.
	/// </summary>
	public static McpDocumentSource DocumentSource { get; } = new McpDocumentSource();

	static readonly Dictionary<string, McpMethodHandler> _methods = new( StringComparer.Ordinal );
	static readonly Dictionary<string, McpToolHandler> _tools = new( StringComparer.Ordinal );
	static bool _stopRequested;

	/// <summary>
	/// The shared safety guard (<c>mcp-cli-security-safety-gates</c>), built
	/// lazily over a per-process temp run dir: the audit log lives there, and
	/// tool file arguments are resolved inside it. Single-threaded loop, so
	/// lazy construction here needs no lock. The same AiSuiteTools instance
	/// feeds the modeling dispatcher, so the gate and the tools agree on the
	/// run dir.
	/// </summary>
	static SafetyGates? _gates;
	static AiSuiteTools? _suiteTools;
	static AiSuiteTools SuiteTools => _suiteTools ??= new AiSuiteTools( RunDirectory );
	static SafetyGates Gates => _gates ??= new SafetyGates( SuiteTools );
	static McpModelingTools? _modeling;
	static McpModelingTools Modeling => _modeling ??= new McpModelingTools( SuiteTools );
	static McpMapTools? _mapTools;
	static McpMapTools MapTools => _mapTools ??= new McpMapTools();
	static string RunDirectory => Path.Combine( Path.GetTempPath(), "geppetto-mcp-" + Environment.ProcessId );

	/// <summary>Raw byte count of the request line being dispatched (single-threaded loop). Passed to BeginCall as the whole-request size.</summary>
	static int _currentRequestBytes;

	static McpServer()
	{
		_methods["initialize"] = Initialize;
		_methods["ping"] = _ => new JsonObject();
		_methods["tools/list"] = _ => ListTools();
		_methods["tools/call"] = CallTool;
		_methods["resources/list"] = _ => ListResources();
		_methods["resources/read"] = ReadResource;
		_methods["prompts/list"] = _ => ListPrompts();
		_methods["prompts/get"] = GetPrompt;
		_methods["shutdown"] = _ => { _stopRequested = true; return new JsonObject(); };
	}

	/// <summary>Register (or replace) a method handler. Register before <see cref="Run"/>; the loop is single-threaded.</summary>
	public static void RegisterMethod( string method, McpMethodHandler handler )
	{
		if ( string.IsNullOrEmpty( method ) ) throw new ArgumentException( "A method name is required.", nameof( method ) );
		if ( handler is null ) throw new ArgumentNullException( nameof( handler ) );
		_methods[method] = handler;
	}

	/// <summary>Register (or replace) a tool executor for <c>tools/call</c>. Register before <see cref="Run"/>.</summary>
	public static void RegisterToolHandler( string name, McpToolHandler handler )
	{
		if ( string.IsNullOrEmpty( name ) ) throw new ArgumentException( "A tool name is required.", nameof( name ) );
		if ( handler is null ) throw new ArgumentNullException( nameof( handler ) );
		_tools[name] = handler;
	}

	public const string Usage = "Usage: marionette-desktop --mcp [--help]\n\nMCP server over stdio (newline-delimited JSON-RPC 2.0, no HTTP in v1).\nReads requests from stdin, writes one JSON response per line to stdout.\nDiagnostics go to stderr; stdout carries protocol traffic only.";

	/// <summary>Process entry point (called from Program.Main's --mcp branch). Returns the process exit code.</summary>
	public static int Run( string[] args )
	{
		if ( args.Length > 0 && ( args[0] == "--help" || args[0] == "-h" ) )
		{
			Console.WriteLine( Usage );
			return 0;
		}
		if ( args.Length > 0 )
			Console.Error.WriteLine( "mcp: ignoring unexpected arguments after --mcp (stdio has no options in v1): " + string.Join( " ", args ) );
		using var input = new StreamReader( Console.OpenStandardInput(), new UTF8Encoding( false ) );
		using var output = new StreamWriter( Console.OpenStandardOutput(), new UTF8Encoding( false ) ) { AutoFlush = true };
		return Run( input, output );
	}

	/// <summary>
	/// Serve JSON-RPC 2.0 over the given streams until EOF or a shutdown request.
	/// Returns 0 on a clean stop, 1 on a transport error. Never writes to stdout
	/// except response lines; diagnostics go to stderr.
	/// </summary>
	public static int Run( TextReader input, TextWriter output )
	{
		_stopRequested = false;
		try
		{
			// No shell/exec tool may ever be exposed: refuse to start if the
			// live definitions contain one (they do not; this is the gate).
			// Validated on the live definitions AND on the full MCP list
			// (definitions + run_script), so the +1 tool is covered too.
			Gates.ValidateDefinitions();
			Gates.ValidateDefinitions( Modeling.ListTools() );
			Gates.ValidateMapDefinitions( MapTools.ListTools() );
			string? line;
			while ( !_stopRequested && ( line = input.ReadLine() ) is not null )
			{
				if ( string.IsNullOrWhiteSpace( line ) ) continue;
				var response = HandleLine( line );
				if ( response is not null ) output.WriteLine( response );
			}
			return 0;
		}
		catch ( Exception ex )
		{
			Console.Error.WriteLine( "mcp: transport error: " + ex.Message );
			return 1;
		}
	}

	static string? HandleLine( string line )
	{
		_currentRequestBytes = Encoding.UTF8.GetByteCount( line );
		if ( _currentRequestBytes > Gates.Policy.MaxRequestBytes )
			return JsonRpc.Serialize( JsonRpc.Error( null, JsonRpcErrorCodes.InvalidParams, "Request exceeds " + Gates.Policy.MaxRequestBytes + " bytes." ) );
		JsonRpcRequest request;
		try { request = JsonRpc.ParseLine( line ); }
		catch ( JsonRpcException ex ) { return JsonRpc.Serialize( JsonRpc.Error( ex.Id, ex.Code, ex.Message ) ); }
		if ( request is null ) return null;
		if ( !request.HasId ) { HandleNotification( request ); return null; }
		try
		{
			return JsonRpc.Serialize( JsonRpc.Success( request.Id, Dispatch( request ) ) );
		}
		catch ( JsonRpcException ex ) { return JsonRpc.Serialize( JsonRpc.Error( request.Id, ex.Code, ex.Message ) ); }
		catch ( ArgumentException ex ) { return JsonRpc.Serialize( JsonRpc.Error( request.Id, JsonRpcErrorCodes.InvalidParams, ex.Message ) ); }
		catch ( SafetyRefusalException ex ) { return JsonRpc.Serialize( JsonRpc.Error( request.Id, JsonRpcErrorCodes.InvalidParams, ex.Message ) ); }
		catch ( Exception ex ) { return JsonRpc.Serialize( JsonRpc.Error( request.Id, JsonRpcErrorCodes.InternalError, "Internal error: " + ex.Message ) ); }
	}

	static void HandleNotification( JsonRpcRequest request )
	{
		if ( request.Method == "shutdown" ) _stopRequested = true;
		// Every other notification (notifications/initialized, notifications/cancelled, ...)
		// is acknowledged by silence: no response is ever written for a notification.
	}

	static JsonObject Dispatch( JsonRpcRequest request )
	{
		if ( !_methods.TryGetValue( request.Method, out var handler ) )
			throw new JsonRpcException( JsonRpcErrorCodes.MethodNotFound, "Method not found: " + request.Method );
		return handler( request.Params );
	}

	static JsonObject Initialize( JsonNode? paramsNode )
	{
		var version = ProtocolVersion;
		if ( paramsNode is JsonObject p )
		{
			var asked = JsonRpc.StringOrNull( p["protocolVersion"] );
			if ( !string.IsNullOrEmpty( asked ) ) version = asked;
		}
		var capabilities = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } };
		if ( _methods.ContainsKey( "resources/list" ) )
			capabilities["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false };
		if ( _methods.ContainsKey( "prompts/list" ) )
			capabilities["prompts"] = new JsonObject { ["listChanged"] = false };
		return new JsonObject
		{
			["protocolVersion"] = version,
			["capabilities"] = capabilities,
			["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
		};
	}

	static JsonObject ListTools()
	{
		// McpModelingTools already maps the definitions verbatim into MCP
		// shape (parameters -> inputSchema) and appends run_script; the list
		// it returns is fresh per call, so appending here is safe. Map tools
		// (McpMapTools.ListTools(), fresh per call too) go after the
		// modeling tools, echo last.
		var tools = Modeling.ListTools();
		foreach ( var tool in MapTools.ListTools() ) tools.Add( tool!.DeepClone() );
		tools.Add( EchoTool() );
		return new JsonObject { ["tools"] = tools };
	}

	static JsonObject EchoTool() => new()
	{
		["name"] = "echo",
		["description"] = "Echo the supplied arguments back as text. Transport smoke test; the only tool this ticket executes.",
		["inputSchema"] = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
		},
	};

	static JsonObject CallTool( JsonNode? paramsNode )
	{
		if ( paramsNode is not JsonObject paramsObj )
			throw new JsonRpcException( JsonRpcErrorCodes.InvalidParams, "Invalid params: tools/call expects an object with \"name\"." );
		var name = JsonRpc.StringOrNull( paramsObj["name"] );
		if ( string.IsNullOrEmpty( name ) )
			throw new JsonRpcException( JsonRpcErrorCodes.InvalidParams, "Invalid params: tools/call requires a \"name\" string." );
		var arguments = paramsObj["arguments"] as JsonObject ?? new JsonObject();
		if ( name == "echo" )
			// Echo is the transport's own smoke test, not an AiSuiteTools tool,
			// so it bypasses BeginCall (which would refuse it as unknown). It
			// performs no operation; its output is still scrubbed, and the
			// 1 MiB request cap in HandleLine covers oversized echo payloads.
			return Scrubbed( ToolResult( arguments.ToJsonString(), false ) );
		if ( name == McpModelingTools.RunScriptToolName )
			return CallRunScript( arguments );
		if ( SafetyGates.IsMapTool( name ) )
			return CallMapTool( name, arguments );
		// Every suite tool goes through the shared safety gate: known-tool
		// check, arg/request size caps, file args inside the run dir, and
		// destructive tools need an explicit confirm argument.
		var confirm = arguments["confirm"] is JsonValue confirmValue && confirmValue.TryGetValue<bool>( out var flag ) && flag;
		using var call = Gates.BeginCall( "mcp", name, arguments, destructiveConfirm: confirm, requestBytes: _currentRequestBytes );
		try
		{
			JsonObject result;
			if ( _tools.TryGetValue( name, out var handler ) )
				result = handler( arguments );
			else
				result = CallModeling( name, arguments );
			call.CheckTimeout();
			call.Complete( "ok" );
			return Scrubbed( result );
		}
		catch ( JsonRpcException ex )
		{
			call.Refuse( ex.Message );
			throw;
		}
	}

	/// <summary>Dispatch one suite tool through the modeling provider and map its result to the MCP tools/call shape.</summary>
	static JsonObject CallModeling( string name, JsonObject arguments )
	{
		// The loop is single-threaded and the process has no
		// SynchronizationContext, so blocking on the async dispatch cannot
		// deadlock; cancellation is owned by the caller (none here — the
		// GuardedCall deadline is enforced with CheckTimeout after the call).
		var run = Modeling.CallAsync( name, arguments, CancellationToken.None ).GetAwaiter().GetResult();
		return ToolResult( run.Text, run.IsError );
	}

	/// <summary>
	/// run_script takes a manual path: <c>BeginCall</c> builds its known-tool
	/// set from AiSuiteTools.Definitions only, so it refuses run_script (an
	/// MCP-only tool defined by McpModelingTools). The checks that apply are
	/// enforced here instead — the arg size cap (the request cap already ran
	/// in HandleLine) and the forbidden check (run at startup on the full
	/// list). The file-arg and destructive checks do not apply by the gate's
	/// own rules (run_script's args carry no file-param key, and run_script
	/// is not in the gate's destructive set) — but the script TEXT is opaque
	/// to the gate, so verb-level destructive gating is still open (see the
	/// class header). This call gets NO GuardedCall audit entry; closing
	/// that gap belongs to the security ticket / integrator.
	/// </summary>
	static JsonObject CallRunScript( JsonObject arguments )
	{
		var argsBytes = Encoding.UTF8.GetByteCount( arguments.ToJsonString() );
		if ( argsBytes > Gates.Policy.MaxArgsBytes )
			throw new JsonRpcException( JsonRpcErrorCodes.InvalidParams, "Arguments exceed " + Gates.Policy.MaxArgsBytes + " bytes." );
		var run = Modeling.CallAsync( McpModelingTools.RunScriptToolName, arguments, CancellationToken.None ).GetAwaiter().GetResult();
		return Scrubbed( ToolResult( run.Text, run.IsError ) );
	}

	/// <summary>
	/// Map tools take the map-gate path (hammer-ai-safety-gates-map):
	/// BeginMapCall checks the tool against SafetyGates.MapTools, refuses
	/// shell-shaped arguments, enforces the size caps and the destructive
	/// confirm, and audits the call with its map reference. The map
	/// reference comes from args["map"] or defaults to the session's open
	/// map (McpMapTools.MapName). Results are scrubbed with
	/// ScrubMapOutput, then the generic Scrubbed wrapper. The two delete
	/// verbs in SafetyGates.MapTools have no McpMapTools implementation
	/// yet: they pass the gate and fail here as unknown map tools
	/// (ArgumentException -&gt; -32602, refused in the audit log, not a crash).
	/// </summary>
	static JsonObject CallMapTool( string name, JsonObject arguments )
	{
		var map = JsonRpc.StringOrNull( arguments["map"] ) ?? McpMapTools.MapName;
		var confirm = arguments["confirm"] is JsonValue confirmValue && confirmValue.TryGetValue<bool>( out var flag ) && flag;
		using var call = Gates.BeginMapCall( "mcp", name, arguments, map, destructiveConfirm: confirm, requestBytes: _currentRequestBytes );
		try
		{
			var run = MapTools.CallAsync( name, arguments, CancellationToken.None ).GetAwaiter().GetResult();
			call.CheckTimeout();
			call.Complete( "ok" );
			var text = SafetyGates.ScrubMapOutput( run.Text ) ?? "[redacted]";
			return Scrubbed( ToolResult( text, run.IsError ) );
		}
		catch ( ArgumentException ex )
		{
			call.Refuse( ex.Message );
			throw;
		}
	}

	/// <summary>Redact secret values from a tool result before it leaves the server. Never throws: on failure returns the result untouched.</summary>
	static JsonObject Scrubbed( JsonObject result ) => SafetyGates.ScrubOutput( result ) as JsonObject ?? result;

	static JsonObject ToolResult( string text, bool isError ) => new()
	{
		["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
		["isError"] = isError,
	};

	static JsonObject ListResources()
	{
		var resources = new JsonArray();
		foreach ( var resource in McpResources.ListResources() )
		{
			resources.Add( new JsonObject
			{
				["uri"] = resource.Uri,
				["name"] = resource.Name,
				["description"] = resource.Description,
				["mimeType"] = resource.MimeType,
			} );
		}
		return new JsonObject { ["resources"] = resources };
	}

	static JsonObject ReadResource( JsonNode? paramsNode )
	{
		var uri = RequiredStringParam( paramsNode, "uri", "resources/read" );
		var content = McpResources.ReadResource( uri, DocumentSource );
		return new JsonObject
		{
			["contents"] = new JsonArray
			{
				new JsonObject { ["uri"] = content.Uri, ["mimeType"] = content.MimeType, ["text"] = content.Text },
			},
		};
	}

	static JsonObject ListPrompts()
	{
		var prompts = new JsonArray();
		foreach ( var prompt in McpResources.ListPrompts() )
		{
			var arguments = new JsonArray();
			foreach ( var argument in prompt.Arguments )
			{
				arguments.Add( new JsonObject
				{
					["name"] = argument.Name,
					["description"] = argument.Description,
					["required"] = argument.Required,
				} );
			}
			prompts.Add( new JsonObject
			{
				["name"] = prompt.Name,
				["description"] = prompt.Description,
				["arguments"] = arguments,
			} );
		}
		return new JsonObject { ["prompts"] = prompts };
	}

	static JsonObject GetPrompt( JsonNode? paramsNode )
	{
		var name = RequiredStringParam( paramsNode, "name", "prompts/get" );
		var arguments = StringArguments( paramsNode );
		var result = McpResources.GetPrompt( name, arguments );
		var messages = new JsonArray();
		foreach ( var message in result.Messages )
		{
			messages.Add( new JsonObject
			{
				["role"] = message.Role,
				["content"] = new JsonObject { ["type"] = "text", ["text"] = message.Text },
			} );
		}
		return new JsonObject { ["description"] = result.Description, ["messages"] = messages };
	}

	static string RequiredStringParam( JsonNode? paramsNode, string field, string method )
	{
		if ( paramsNode is JsonObject obj )
		{
			var value = JsonRpc.StringOrNull( obj[field] );
			if ( !string.IsNullOrEmpty( value ) ) return value;
		}
		throw new JsonRpcException( JsonRpcErrorCodes.InvalidParams, "Invalid params: " + method + " requires a \"" + field + "\" string." );
	}

	static IReadOnlyDictionary<string, string> StringArguments( JsonNode? paramsNode )
	{
		var arguments = new Dictionary<string, string>( StringComparer.Ordinal );
		if ( paramsNode is JsonObject obj && obj["arguments"] is JsonObject args )
		{
			foreach ( var pair in args )
			{
				var value = JsonRpc.StringOrNull( pair.Value );
				if ( value is null )
					throw new JsonRpcException( JsonRpcErrorCodes.InvalidParams, "Invalid params: prompt argument '" + pair.Key + "' must be a string." );
				arguments[pair.Key] = value;
			}
		}
		return arguments;
	}
}
