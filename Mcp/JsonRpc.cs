using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Marionette.Desktop.Mcp;

/// <summary>JSON-RPC 2.0 error codes (spec section 5.1).</summary>
public static class JsonRpcErrorCodes
{
	public const int ParseError = -32700;
	public const int InvalidRequest = -32600;
	public const int MethodNotFound = -32601;
	public const int InvalidParams = -32602;
	public const int InternalError = -32603;
}

/// <summary>
/// An error a method handler raises to fail its request with a JSON-RPC
/// error object instead of a result. The server loop converts it to an
/// error response and keeps running; it never escapes the loop.
/// </summary>
public sealed class JsonRpcException : Exception
{
	public JsonRpcException( int code, string message ) : base( message ) { Code = code; }

	public int Code { get; }

	/// <summary>Request id to echo in the error response, when the id could be read (null for parse errors).</summary>
	public JsonNode? Id { get; set; }

	/// <summary>Optional extra error data, serialized under "data".</summary>
	public JsonNode? Data { get; set; }
}

/// <summary>
/// One parsed JSON-RPC 2.0 request. <see cref="HasId"/> is false for a
/// notification, which never gets a response. Id may itself be JSON null for a
/// request that sent "id": null; that still gets a response (with id null).
/// </summary>
public sealed class JsonRpcRequest
{
	public JsonRpcRequest( JsonNode? id, bool hasId, string method, JsonNode? paramsNode )
	{
		Id = id;
		HasId = hasId;
		Method = method;
		Params = paramsNode;
	}

	public JsonNode? Id { get; }
	public bool HasId { get; }
	public string Method { get; }
	public JsonNode? Params { get; }
}

/// <summary>
/// JSON-RPC 2.0 message shapes for the MCP stdio transport: parse one
/// newline-delimited line, build success/error responses, serialize compactly.
/// No I/O here; the server owns the streams.
/// </summary>
public static class JsonRpc
{
	/// <summary>
	/// Parse one NDJSON line. Blank lines return null (the caller skips them).
	/// Malformed JSON, a JSON array (batches are not supported over stdio),
	/// a missing/non-string method, or a "jsonrpc" other than "2.0" raise
	/// <see cref="JsonRpcException"/> with the matching spec error code.
	/// The exception carries the request id whenever it could be read, so the
	/// error response echoes it instead of null.
	/// </summary>
	public static JsonRpcRequest? ParseLine( string line )
	{
		if ( string.IsNullOrWhiteSpace( line ) ) return null;
		JsonNode? root;
		try { root = JsonNode.Parse( line ); }
		catch ( JsonException ex ) { throw new JsonRpcException( JsonRpcErrorCodes.ParseError, "Parse error: " + ex.Message ); }
		if ( root is JsonArray )
			throw new JsonRpcException( JsonRpcErrorCodes.InvalidRequest, "Invalid Request: batch arrays are not supported over stdio (one JSON value per line)." );
		if ( root is not JsonObject obj )
			throw new JsonRpcException( JsonRpcErrorCodes.InvalidRequest, "Invalid Request: expected a JSON object." );
		var hasId = obj.ContainsKey( "id" );
		var id = hasId ? obj["id"]?.DeepClone() : null;
		var version = StringOrNull( obj["jsonrpc"] );
		if ( version != "2.0" )
			throw new JsonRpcException( JsonRpcErrorCodes.InvalidRequest, "Invalid Request: \"jsonrpc\" must be \"2.0\"." ) { Id = id };
		var method = StringOrNull( obj["method"] );
		if ( string.IsNullOrEmpty( method ) )
			throw new JsonRpcException( JsonRpcErrorCodes.InvalidRequest, "Invalid Request: \"method\" must be a non-empty string." ) { Id = id };
		return new JsonRpcRequest( id, hasId, method, obj["params"]?.DeepClone() );
	}

	/// <summary>Value of a JSON string node, or null for anything else (number, bool, null, object, array).</summary>
	internal static string? StringOrNull( JsonNode? node )
	{
		if ( node is JsonValue value && value.TryGetValue<string>( out var text ) ) return text;
		return null;
	}

	public static JsonObject Success( JsonNode? id, JsonNode? result ) => new()
	{
		["jsonrpc"] = "2.0",
		["id"] = id?.DeepClone(),
		["result"] = result?.DeepClone(),
	};

	public static JsonObject Error( JsonNode? id, int code, string message ) => Error( id, code, message, null );

	public static JsonObject Error( JsonNode? id, int code, string message, JsonNode? data )
	{
		var error = new JsonObject { ["code"] = code, ["message"] = message };
		if ( data is not null ) error["data"] = data.DeepClone();
		return new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = id?.DeepClone(),
			["error"] = error,
		};
	}

	public static string Serialize( JsonNode? node ) => node is null ? "null" : node.ToJsonString();
}
