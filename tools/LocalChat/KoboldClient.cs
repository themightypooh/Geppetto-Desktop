using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalChat;

/// <summary>A request/response turn in the OpenAI-compatible shape KoboldCpp exposes.</summary>
public sealed record ChatTurn( string Role, string Content );

/// <summary>
/// Client for a local KoboldCpp server. It speaks the OpenAI-compatible
/// <c>/v1/chat/completions</c> route and the native <c>/api/v1/model</c> probe.
/// </summary>
/// <remarks>
/// The transport is <see cref="LocalHttp"/> rather than HttpClient: KoboldCpp answers
/// in HTTP/1.0 with "connection: keep-alive" and no Content-Length on its streamed
/// body, which .NET reads as an empty response and which leaves a closed socket in
/// the connection pool for the next request.
/// </remarks>
public sealed class KoboldClient
{
    public Uri Endpoint { get; }

    /// <summary>How long a single request may take before it is abandoned.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds( 300 );

    public KoboldClient( Uri endpoint )
    {
        Endpoint = Normalize( endpoint );
    }

    /// <summary>Links the caller's token to a timeout so a hung server cannot block the UI forever.</summary>
    CancellationTokenSource Scope( CancellationToken cancellationToken ) =>
        CancellationTokenSource.CreateLinkedTokenSource( cancellationToken, new CancellationTokenSource( Timeout ).Token );

    /// <summary>Ensures the base URL ends with a slash so relative paths resolve predictably.</summary>
    public static Uri Normalize( Uri endpoint )
    {
        string text = endpoint.ToString();
        if ( !text.EndsWith( "/" ) ) text += "/";
        return new Uri( text );
    }

    public Uri Resolve( string path ) => new( Endpoint, path.TrimStart( '/' ) );

    /// <summary>Returns the model id the server currently has loaded, without the "koboldcpp/" prefix.</summary>
    public async Task<string?> GetLoadedModelAsync( CancellationToken cancellationToken = default )
    {
        using var scope = Scope( cancellationToken );
        await using LocalHttpReply reply = await LocalHttp.SendAsync( Endpoint, "GET", "api/v1/model", null, "application/json", scope.Token );
        await EnsureSuccessAsync( reply );
        string body = await reply.ReadBodyAsTextAsync( scope.Token );
        string id = ExtractModelId( body );
        const string prefix = "koboldcpp/";
        return id.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) ? id[prefix.Length..] : id;
    }

    /// <summary>
    /// The probe answers either with a bare string or with <c>{"result": "..."}</c>,
    /// depending on the server build.
    /// </summary>
    public static string ExtractModelId( string body )
    {
        string text = body.Trim();
        if ( text.Length == 0 ) return "";
        if ( text[0] == '{' )
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse( text );
                if ( document.RootElement.TryGetProperty( "result", out JsonElement result ) )
                    return result.GetString() ?? "";
            }
            catch ( JsonException )
            {
                // fall through to the raw text
            }
        }
        return text.Trim( '"' );
    }

    /// <summary>Lists the model ids the server reports on <c>/v1/models</c>.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync( CancellationToken cancellationToken = default )
    {
        using var scope = Scope( cancellationToken );
        await using LocalHttpReply reply = await LocalHttp.SendAsync( Endpoint, "GET", "v1/models", null, "application/json", scope.Token );
        await EnsureSuccessAsync( reply );
        string body = await reply.ReadBodyAsTextAsync( scope.Token );
        var models = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse( body );
            if ( document.RootElement.TryGetProperty( "data", out JsonElement data ) && data.ValueKind == JsonValueKind.Array )
                foreach ( JsonElement entry in data.EnumerateArray() )
                    if ( entry.TryGetProperty( "id", out JsonElement id ) )
                    {
                        string value = id.GetString() ?? "";
                        const string prefix = "koboldcpp/";
                        models.Add( value.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) ? value[prefix.Length..] : value );
                    }
        }
        catch ( JsonException e )
        {
            throw new KoboldException( $"The server sent a /v1/models payload this client cannot read: {e.Message}" );
        }
        return models;
    }

    /// <summary>Builds the JSON body for a chat completion request.</summary>
    public static byte[] BuildRequestBody( IReadOnlyList<ChatTurn> turns, SamplingOptions sampling, string? model, bool stream )
    {
        var messages = new JsonArray();
        foreach ( ChatTurn turn in turns )
            messages.Add( new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content } );

        var body = new JsonObject
        {
            ["messages"] = messages,
            ["temperature"] = sampling.Temperature,
            ["top_p"] = sampling.TopP,
            ["top_k"] = sampling.TopK,
            ["min_p"] = sampling.MinP,
            ["repeat_penalty"] = sampling.RepeatPenalty,
            ["max_tokens"] = sampling.MaxTokens,
            ["stream"] = stream,
        };
        if ( !string.IsNullOrWhiteSpace( model ) ) body["model"] = model;
        return JsonSerializer.SerializeToUtf8Bytes( body );
    }

    /// <summary>Runs a single non-streaming completion and returns the assistant text.</summary>
    public async Task<string> CompleteAsync( IReadOnlyList<ChatTurn> turns, SamplingOptions sampling, string? model, CancellationToken cancellationToken = default )
    {
        using var scope = Scope( cancellationToken );
        byte[] body = BuildRequestBody( turns, sampling, model, stream: false );
        await using LocalHttpReply reply = await LocalHttp.SendAsync( Endpoint, "POST", "v1/chat/completions", body, "application/json", scope.Token );
        await EnsureSuccessAsync( reply );
        string text = await reply.ReadBodyAsTextAsync( scope.Token );
        return ExtractContent( text );
    }

    /// <summary>
    /// Runs a streaming completion. <paramref name="onDelta"/> receives each content fragment
    /// as it arrives; the returned string is the full assembled reply.
    /// </summary>
    public async Task<string> StreamAsync( IReadOnlyList<ChatTurn> turns, SamplingOptions sampling, string? model, Action<string> onDelta, CancellationToken cancellationToken = default )
    {
        using var scope = Scope( cancellationToken );
        byte[] body = BuildRequestBody( turns, sampling, model, stream: true );
        await using LocalHttpReply reply = await LocalHttp.SendAsync( Endpoint, "POST", "v1/chat/completions", body, "application/json", scope.Token );
        await EnsureSuccessAsync( reply );

        var builder = new StringBuilder();
        using StreamReader reader = reply.OpenLineReader();
        while ( true )
        {
            string? line = await reader.ReadLineAsync( scope.Token );
            if ( line is null ) break;                 // the server closed the connection
            if ( line.Length == 0 ) continue;          // event separator
            if ( !line.StartsWith( "data:", StringComparison.Ordinal ) ) continue;
            string payload = line[5..].Trim();
            if ( payload.Length == 0 ) continue;
            if ( payload == "[DONE]" ) break;
            string? delta = ExtractDelta( payload );
            if ( delta is null ) continue;
            builder.Append( delta );
            onDelta( delta );
        }
        return builder.ToString();
    }

    /// <summary>Pulls the assistant text out of a non-streaming completion payload.</summary>
    public static string ExtractContent( string json )
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse( json );
            JsonElement root = document.RootElement;
            if ( !root.TryGetProperty( "choices", out JsonElement choices ) || choices.ValueKind != JsonValueKind.Array )
                throw new KoboldException( "The server reply has no 'choices' array." );
            foreach ( JsonElement choice in choices.EnumerateArray() )
                if ( choice.TryGetProperty( "message", out JsonElement message ) &&
                     message.TryGetProperty( "content", out JsonElement content ) )
                    return content.GetString() ?? "";
            throw new KoboldException( "The server reply has no message content." );
        }
        catch ( JsonException e )
        {
            throw new KoboldException( $"The server reply was not valid JSON: {e.Message}" );
        }
    }

    /// <summary>Pulls the content fragment out of one server-sent-event payload, or null if there is none.</summary>
    public static string? ExtractDelta( string payload )
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse( payload );
            JsonElement root = document.RootElement;
            if ( !root.TryGetProperty( "choices", out JsonElement choices ) || choices.ValueKind != JsonValueKind.Array )
                return null;
            foreach ( JsonElement choice in choices.EnumerateArray() )
            {
                if ( !choice.TryGetProperty( "delta", out JsonElement delta ) ) continue;
                if ( !delta.TryGetProperty( "content", out JsonElement content ) ) continue;
                if ( content.ValueKind == JsonValueKind.Null ) continue;
                return content.GetString() ?? "";
            }
            return null;
        }
        catch ( JsonException )
        {
            // A malformed event must not kill the stream; skip it and keep reading.
            return null;
        }
    }

    static async Task EnsureSuccessAsync( LocalHttpReply reply )
    {
        if ( reply.StatusCode >= 200 && reply.StatusCode < 300 ) return;
        string detail = "";
        try
        {
            detail = ( await reply.ReadBodyAsTextAsync() ).Trim();
            if ( detail.Length > 400 ) detail = detail[..400] + "...";
        }
        catch ( Exception ) { /* the status line is still useful on its own */ }
        throw new KoboldException( detail.Length > 0
            ? $"The server returned {reply.StatusCode} {reply.Reason}. {detail}"
            : $"The server returned {reply.StatusCode} {reply.Reason}." );
    }
}

/// <summary>A failure that carries a message the UI can show verbatim.</summary>
public sealed class KoboldException : Exception
{
    public KoboldException( string message ) : base( message ) { }
    public KoboldException( string message, Exception inner ) : base( message, inner ) { }
}
