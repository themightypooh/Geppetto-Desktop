#nullable enable
using System;
using System.IO;
using System.Text.Json.Nodes;
using Marionette.Desktop;
using Marionette.Desktop.Mcp;

/// <summary>
/// MCP stdio transport + protocol: JSON-RPC 2.0 framing, initialize,
/// tools/list, tools/call (echo), resources/prompts passthrough, ping and
/// shutdown, all driven through in-memory transcripts
/// (<c>mcp-server-stdio-transport-and-protocol</c>). No process spawn, no
/// window, no network: the server loop runs on a StringReader/StringWriter.
/// 15 checks; the last one registers handlers, so it runs last.
/// </summary>
internal static class McpServerTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		JsonNode? Field( JsonNode? node, string field ) => ( node as JsonObject )?[field];

		string? Str( JsonNode? node ) => node is JsonValue value && value.TryGetValue<string>( out var text ) ? text : null;

		bool Bool( JsonNode? node ) => node is JsonValue value && value.TryGetValue<bool>( out var flag ) && flag;

		int Int( JsonNode? node ) => node is JsonValue value && value.TryGetValue<int>( out var number ) ? number : -1;

		( int exit, string[] lines ) RunTranscript( string transcript )
		{
			using var input = new StringReader( transcript );
			var output = new StringWriter();
			int exit = McpServer.Run( input, output );
			var lines = output.ToString().Split( new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries );
			return ( exit, lines );
		}

		JsonObject Response( string line )
		{
			var parsed = JsonNode.Parse( line ) as JsonObject;
			Assert( parsed is not null, "response line is not a JSON object: " + line );
			return parsed!;
		}

		JsonObject Result( string line )
		{
			var response = Response( line );
			Assert( response["error"] is null, "expected a result, got error: " + line );
			var result = response["result"] as JsonObject;
			Assert( result is not null, "result is not an object: " + line );
			return result!;
		}

		JsonObject Error( string line )
		{
			var response = Response( line );
			var error = response["error"] as JsonObject;
			Assert( error is not null, "expected an error object: " + line );
			return error!;
		}

		JsonObject? FindTool( JsonArray tools, string name )
		{
			foreach ( var tool in tools )
				if ( tool is JsonObject obj && Str( Field( obj, "name" ) ) == name ) return obj;
			return null;
		}

		registry.Add( "mcp-server", "initialize tools/list tools/call shutdown transcript exits 0", () =>
		{
			var ( exit, lines ) = RunTranscript(
				"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"clientInfo\":{\"name\":\"test\",\"version\":\"0\"}}}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"hello\"}}}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"shutdown\"}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 4, "expected 4 response lines, got " + lines.Length );
			var init = Result( lines[0] );
			Assert( Str( Field( init, "protocolVersion" ) ) == "2024-11-05", "protocolVersion" );
			Assert( Str( Field( Field( init, "serverInfo" ), "name" ) ) == "geppetto-desktop", "serverInfo.name" );
			Assert( Field( init, "capabilities" ) is not null, "capabilities present" );
			Assert( Field( Field( init, "capabilities" ), "tools" ) is not null, "capabilities.tools" );
			Assert( Field( Field( init, "capabilities" ), "resources" ) is not null, "capabilities.resources" );
			Assert( Field( Field( init, "capabilities" ), "prompts" ) is not null, "capabilities.prompts" );
			var tools = Result( lines[1] )["tools"] as JsonArray;
			Assert( tools is not null && tools.Count >= 45, "tools/list should return 43 AiSuiteTools + run_script + echo, got " + ( tools?.Count ?? 0 ) );
			var call = Result( lines[2] );
			Assert( Bool( Field( call, "isError" ) ) == false, "echo isError" );
			var content = Field( call, "content" ) as JsonArray;
			Assert( content is not null && content.Count == 1, "echo content has one block" );
			Assert( Str( Field( content![0], "text" ) )?.Contains( "hello" ) == true, "echo text carries the argument" );
			Assert( Result( lines[3] ).Count == 0, "shutdown result is empty" );
		} );

		registry.Add( "mcp-server", "malformed JSON returns a parse error and the server keeps running", () =>
		{
			var ( exit, lines ) = RunTranscript(
				"{\"jsonrpc\": \"2.0\", \"id\": 1, \"method\": \"ping\"\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 2, "expected 2 response lines, got " + lines.Length );
			var parseError = Error( lines[0] );
			Assert( Int( Field( parseError, "code" ) ) == -32700, "parse error code" );
			Assert( lines[0].Contains( "\"id\":null" ), "parse error id is null" );
			Assert( Result( lines[1] ).Count == 0, "ping after the bad line still answers" );
		} );

		registry.Add( "mcp-server", "unknown method returns method-not-found", () =>
		{
			var ( exit, lines ) = RunTranscript( "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"frobnicate\"}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 1, "one response line" );
			var error = Error( lines[0] );
			Assert( Int( Field( error, "code" ) ) == -32601, "method-not-found code" );
			Assert( Int( Field( Response( lines[0] ), "id" ) ) == 7, "error echoes the request id" );
		} );

		registry.Add( "mcp-server", "unknown tool returns invalid params", () =>
		{
			var ( exit, lines ) = RunTranscript( "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\"}}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 1, "one response line" );
			Assert( Int( Field( Error( lines[0] ), "code" ) ) == -32602, "invalid-params code" );
		} );

		registry.Add( "mcp-server", "notification produces no response", () =>
		{
			var ( exit, lines ) = RunTranscript(
				"{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 1, "only the ping gets a response, got " + lines.Length );
			Assert( Result( lines[0] ).Count == 0, "ping result is empty" );
		} );

		registry.Add( "mcp-server", "tools/list maps AiSuiteTools definitions to MCP inputSchema", () =>
		{
			var ( exit, lines ) = RunTranscript( "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n" );
			Assert( exit == 0, "exit " + exit );
			var tools = Result( lines[0] )["tools"] as JsonArray;
			Assert( tools is not null && tools.Count >= 45, "43 definitions + run_script + echo" );
			var catalog = FindTool( tools!, "catalog" );
			Assert( catalog is not null, "catalog is listed" );
			Assert( Field( catalog, "inputSchema" ) is not null, "catalog has inputSchema" );
			Assert( Field( catalog, "parameters" ) is null, "catalog has no leftover parameters field" );
			Assert( Str( Field( Field( catalog, "inputSchema" ), "type" ) ) == "object", "catalog inputSchema is an object schema" );
			var runScript = FindTool( tools!, "run_script" );
			Assert( runScript is not null, "run_script is listed" );
			var required = Field( Field( runScript, "inputSchema" ), "required" ) as JsonArray;
			Assert( required is not null && required.Count == 1 && Str( required[0] ) == "script", "run_script requires script" );
			var echo = FindTool( tools!, "echo" );
			Assert( echo is not null, "echo is listed" );
			Assert( Str( Field( Field( echo, "inputSchema" ), "type" ) ) == "object", "echo inputSchema is an object schema" );
		} );

		registry.Add( "mcp-server", "ping returns an empty result", () =>
		{
			var ( exit, lines ) = RunTranscript( "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 1, "one response line" );
			Assert( Result( lines[0] ).Count == 0, "ping result is empty" );
		} );

		registry.Add( "mcp-server", "EOF without shutdown exits 0", () =>
		{
			var ( exit, lines ) = RunTranscript(
				"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 2, "both requests answered before EOF" );
		} );

		registry.Add( "mcp-server", "a batch array is rejected, not executed", () =>
		{
			var ( exit, lines ) = RunTranscript( "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 1, "one response line" );
			Assert( Int( Field( Error( lines[0] ), "code" ) ) == -32600, "invalid-request code" );
		} );

		registry.Add( "mcp-server", "tools/call without a name is invalid params", () =>
		{
			var ( exit, lines ) = RunTranscript( "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{}}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( Int( Field( Error( lines[0] ), "code" ) ) == -32602, "invalid-params code" );
		} );

		registry.Add( "mcp-server", "initialize without protocolVersion defaults to 2024-11-05", () =>
		{
			var ( exit, lines ) = RunTranscript( "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( Str( Field( Result( lines[0] ), "protocolVersion" ) ) == "2024-11-05", "default protocolVersion" );
		} );

		registry.Add( "mcp-server", "resources and prompts round-trip through the provider", () =>
		{
			var ( exit, lines ) = RunTranscript(
				"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/list\"}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/read\",\"params\":{\"uri\":\"geppetto://catalog\"}}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/read\",\"params\":{\"uri\":\"geppetto://nope\"}}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"prompts/list\"}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"prompts/get\",\"params\":{\"name\":\"export-for-godot\",\"arguments\":{\"format\":\"glb\"}}}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"prompts/get\",\"params\":{\"name\":\"nope\"}}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 6, "expected 6 response lines, got " + lines.Length );
			var resources = Result( lines[0] )["resources"] as JsonArray;
			Assert( resources is not null && resources.Count == 8, "eight resources" );
			var catalog = Result( lines[1] )["contents"] as JsonArray;
			Assert( catalog is not null && catalog.Count == 1, "one content block" );
			Assert( Str( Field( catalog![0], "mimeType" ) ) == "application/json", "catalog mime type" );
			Assert( ( Str( Field( catalog[0], "text" ) )?.Length ?? 0 ) > 100, "catalog text is non-empty" );
			Assert( Int( Field( Error( lines[2] ), "code" ) ) == -32602, "unknown resource is invalid params" );
			var prompts = Result( lines[3] )["prompts"] as JsonArray;
			Assert( prompts is not null && prompts.Count == 7, "seven prompts" );
			var get = Result( lines[4] );
			Assert( Str( Field( get, "description" ) )?.Length > 0, "prompt description" );
			var messages = Field( get, "messages" ) as JsonArray;
			Assert( messages is not null && messages.Count == 1, "one prompt message" );
			Assert( Str( Field( messages![0], "role" ) ) == "user", "prompt message role" );
			Assert( ( Str( Field( Field( messages[0], "content" ), "text" ) )?.Length ?? 0 ) > 0, "prompt message text" );
			Assert( Int( Field( Error( lines[5] ), "code" ) ) == -32602, "unknown prompt is invalid params" );
		} );

		registry.Add( "mcp-server", "run_script without a script argument is invalid params", () =>
		{
			// The missing-script check fires inside the modeling dispatcher
			// before any execution, so this exercises the run_script path
			// without spawning a child process.
			var ( exit, lines ) = RunTranscript( "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"run_script\",\"arguments\":{}}}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 1, "one response line" );
			var error = Error( lines[0] );
			Assert( Int( Field( error, "code" ) ) == -32602, "invalid-params code" );
			Assert( Str( Field( error, "message" ) )?.Contains( "script" ) == true, "message names the missing script argument" );
		} );

		registry.Add( "mcp-server", "echo output is scrubbed of secret shapes", () =>
		{
			var ( exit, lines ) = RunTranscript( "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"key sk-abcdefghij1234567890\"}}}\n" );
			Assert( exit == 0, "exit " + exit );
			var call = Result( lines[0] );
			var content = Field( call, "content" ) as JsonArray;
			Assert( content is not null && content.Count == 1, "echo content has one block" );
			var text = Str( Field( content![0], "text" ) ) ?? "";
			Assert( text.Contains( "[redacted]" ), "secret shape is redacted" );
			Assert( !text.Contains( "sk-abcdefghij1234567890" ), "raw secret never leaves the server" );
		} );

		registry.Add( "mcp-server", "registered method and tool handlers are reachable", () =>
		{
			McpServer.RegisterMethod( "test.ping", _ => new JsonObject { ["ok"] = true } );
			// A real AiSuiteTools name: the safety gate refuses unknown tools
			// before any handler runs, so the seam is tested on "catalog".
			McpServer.RegisterToolHandler( "catalog", args => new JsonObject
			{
				["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "test-tool-ok" } },
				["isError"] = false,
			} );
			var ( exit, lines ) = RunTranscript(
				"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"test.ping\"}\n" +
				"{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"catalog\",\"arguments\":{}}}\n" );
			Assert( exit == 0, "exit " + exit );
			Assert( lines.Length == 2, "both requests answered" );
			Assert( Bool( Field( Result( lines[0] ), "ok" ) ), "registered method answers" );
			var content = Field( Result( lines[1] ), "content" ) as JsonArray;
			Assert( content is not null && content.Count == 1, "registered tool content" );
			Assert( Str( Field( content![0], "text" ) ) == "test-tool-ok", "registered tool answers" );
		} );
	}
}
