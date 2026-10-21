using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalChat;

/// <summary>A roleplay character: a persona the model plays, with an optional opening line.</summary>
public sealed class Character
{
    [JsonPropertyName( "name" )] public string Name { get; set; } = "Character";
    [JsonPropertyName( "tagline" )] public string Tagline { get; set; } = "";
    [JsonPropertyName( "system" )] public string SystemPrompt { get; set; } = "";
    [JsonPropertyName( "greeting" )] public string Greeting { get; set; } = "";

    /// <summary>
    /// Who the character is, for the tracker pass. The full system prompt carries
    /// the example messages and the whole world, which the model needs when it is
    /// writing but not when it is only reading the scene to count a few numbers.
    /// Sending all of it costs seconds of prefill for nothing.
    /// </summary>
    [JsonPropertyName( "trackerContext" )] public string TrackerContext { get; set; } = "";

    public Character Copy() => new()
    {
        Name = Name,
        Tagline = Tagline,
        SystemPrompt = SystemPrompt,
        Greeting = Greeting,
        TrackerContext = TrackerContext,
    };
}

/// <summary>
/// The characters the player can pick from. They live in a JSON file next to the
/// session so they survive restarts and can be edited in the app.
/// </summary>
public sealed class CharacterStore
{
    public List<Character> Characters { get; set; } = new();

    public static string DefaultPath =>
        Path.Combine( AppPaths.DataDirectory, "characters.json" );

    /// <summary>Loads the store, falling back to the built-in cast when the file is missing or unreadable.</summary>
    public static CharacterStore Load( string path )
    {
        try
        {
            if ( File.Exists( path ) )
            {
                string json = File.ReadAllText( path );
                CharacterStore? store = JsonSerializer.Deserialize<CharacterStore>( json );
                if ( store is not null && store.Characters.Count > 0 ) return store;
            }
        }
        catch ( Exception )
        {
            // A damaged file must not stop the app; the built-in cast is used instead.
        }
        return new CharacterStore { Characters = Defaults() };
    }

    public void Save( string path )
    {
        try
        {
            string? directory = Path.GetDirectoryName( path );
            if ( !string.IsNullOrEmpty( directory ) ) Directory.CreateDirectory( directory );
            File.WriteAllText( path, JsonSerializer.Serialize( this, new JsonSerializerOptions { WriteIndented = true } ) );
        }
        catch ( Exception )
        {
            // Losing a character file is not worth interrupting the conversation over.
        }
    }

    public int IndexOf( string name )
    {
        for ( int i = 0; i < Characters.Count; i++ )
            if ( string.Equals( Characters[i].Name, name, StringComparison.OrdinalIgnoreCase ) ) return i;
        return -1;
    }

    public Character? Find( string? name )
    {
        if ( string.IsNullOrWhiteSpace( name ) ) return null;
        int index = IndexOf( name );
        return index >= 0 ? Characters[index] : null;
    }

    /// <summary>The cast the app starts with, so the selector is never empty.</summary>
    public static List<Character> Defaults() => new()
    {
        new()
        {
            Name = "Plain",
            Tagline = "No persona. The model answers as itself.",
            SystemPrompt = "",
        },
        new()
        {
            Name = "Aria",
            Tagline = "Weary innkeeper at a crossroads waystation",
            SystemPrompt =
                "You are Aria, the innkeeper of the Waystone, a crossroads waystation on a cold northern road. " +
                "You are in your late forties, dry-humoured and unhurried, and you have seen far too many travellers. " +
                "You speak in plain, grounded dialogue, usually a sentence or two. You are warm but never effusive, " +
                "and you deflect personal questions with a short joke. You never break character and never mention being an AI.",
            Greeting = "\"Fire's low and the road's worse. Sit where the draught can't reach you. Want soup, or just the news?\"",
        },
        new()
        {
            Name = "Sable",
            Tagline = "Smuggler with a price list and a conscience",
            SystemPrompt =
                "You are Sable, a smuggler who runs a boat out of the harbour at low tide. " +
                "You are sharp, funny and a little mercenary, but you keep your word. " +
                "You answer in short, vivid sentences with a hint of street slang, and you always want something in return. " +
                "You stay in character at all times.",
            Greeting = "\"Back already? Either the road's worse than I said, or you've found something interesting. Talk.\"",
        },
        new()
        {
            Name = "Wren",
            Tagline = "Lighthouse keeper, insomniac, keeps a journal",
            SystemPrompt =
                "You are Wren, the keeper of a lonely lighthouse. You write in a journal every night and you talk to the sea. " +
                "You are thoughtful, a little wry, and you describe things in a careful, precise way. " +
                "You are never in a hurry to answer and you like to ask a question back.",
            Greeting = "\"The lamp's turning, and the glass is falling again. So. Talk to me, or listen to the weather with me.\"",
        },
        new()
        {
            Name = "Mira",
            Tagline = "Cartographer mapping a coast that keeps moving",
            SystemPrompt =
                "You are Mira, a cartographer who is mapping a coastline that will not hold still. " +
                "You are enthusiastic, precise about distances, and you get excited about odd geography. " +
                "You speak in clear sentences and occasionally apologise for correcting the player. " +
                "You stay in character.",
            Greeting = "\"Good, you're here - I've drawn the same headland three times and it moved twice. Where shall we start?\"",
        },
    };
}
