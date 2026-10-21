using System.Text.Json.Serialization;

namespace LocalChat;

/// <summary>Command line options for the local chat client.</summary>
public sealed class ChatOptions
{
    public Uri Endpoint { get; set; } = new Uri( "http://127.0.0.1:5001/" );
    public string? Model { get; set; }
    public string? SystemPrompt { get; set; }
    public string? SessionPath { get; set; }
    public bool SelfTest { get; set; }
    public bool ListModels { get; set; }
    public bool ShowHelp { get; set; }
    public int Width { get; set; } = 1180;
    public int Height { get; set; } = 840;

    public static ChatOptions Parse( string[] args )
    {
        var options = new ChatOptions();
        for ( int i = 0; i < args.Length; i++ )
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch ( arg )
            {
                case "--endpoint": case "-e":
                    string raw = Next() ?? throw new ArgumentException( "--endpoint needs a URL" );
                    if ( !Uri.TryCreate( raw, UriKind.Absolute, out Uri? uri ) )
                        throw new ArgumentException( $"--endpoint is not a valid URL: {raw}" );
                    options.Endpoint = uri;
                    break;
                case "--model": case "-m": options.Model = Next(); break;
                case "--system": case "-s": options.SystemPrompt = Next(); break;
                case "--session": options.SessionPath = Next(); break;
                case "--selftest": options.SelfTest = true; break;
                case "--list-models": options.ListModels = true; break;
                case "--width": options.Width = int.Parse( Next() ?? "1180" ); break;
                case "--height": options.Height = int.Parse( Next() ?? "840" ); break;
                case "--help": case "-h": options.ShowHelp = true; break;
                default:
                    throw new ArgumentException( $"Unknown argument: {arg}" );
            }
        }
        return options;
    }

    public static string Help =>
        """
        local-chat - native chat window for a local KoboldCpp server

        Usage:
          local-chat [options]

        Options:
          -e, --endpoint <url>   Server base URL (default http://127.0.0.1:5001/)
          -m, --model <id>       Model id to use (default: the model the server has loaded)
          -s, --system <text>    System prompt / character definition
              --session <path>   Session file (default ~/.local/share/local-chat/session.json)
              --list-models      Print the models the server reports and exit
              --selftest         Run headless protocol checks and exit
              --width <px>       Window width (default 1180)
              --height <px>      Window height (default 840)
          -h, --help             Show this help

        Keys:
          Enter          send
          Shift+Enter    newline
          Ctrl+L         clear the transcript
        """;
}

/// <summary>One message in the transcript.</summary>
public sealed class ChatMessage
{
    [JsonPropertyName( "role" )] public string Role { get; set; } = "user";
    [JsonPropertyName( "content" )] public string Content { get; set; } = "";
    [JsonPropertyName( "error" )] public bool Error { get; set; }
}

/// <summary>Sampling parameters sent with every request.</summary>
public sealed class SamplingOptions
{
    [JsonPropertyName( "temperature" )] public float Temperature { get; set; } = 0.9f;
    [JsonPropertyName( "topP" )] public float TopP { get; set; } = 0.95f;
    [JsonPropertyName( "topK" )] public int TopK { get; set; } = 40;
    [JsonPropertyName( "minP" )] public float MinP { get; set; } = 0.05f;
    [JsonPropertyName( "repeatPenalty" )] public float RepeatPenalty { get; set; } = 1.05f;
    [JsonPropertyName( "maxTokens" )] public int MaxTokens { get; set; } = 512;

    public SamplingOptions Clone() => (SamplingOptions)MemberwiseClone();
}

/// <summary>Persisted chat state.</summary>
public sealed class ChatSession
{
    [JsonPropertyName( "endpoint" )] public string Endpoint { get; set; } = "http://127.0.0.1:5001/";
    [JsonPropertyName( "model" )] public string? Model { get; set; }
    [JsonPropertyName( "character" )] public string? Character { get; set; }
    [JsonPropertyName( "system" )] public string SystemPrompt { get; set; } = "";
    [JsonPropertyName( "sampling" )] public SamplingOptions Sampling { get; set; } = new();
    [JsonPropertyName( "messages" )] public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// One transcript per character, keyed by character name. Switching characters
    /// swaps the transcript instead of throwing it away, so a conversation with one
    /// character is still there when you come back to it.
    /// </summary>
    [JsonPropertyName( "threads" )] public Dictionary<string, List<ChatMessage>> Threads { get; set; } = new();

    public static string DefaultPath =>
        Path.Combine( AppPaths.DataDirectory, "session.json" );

    public static string DefaultCharacterPath =>
        Path.Combine( AppPaths.DataDirectory, "characters.json" );
}
