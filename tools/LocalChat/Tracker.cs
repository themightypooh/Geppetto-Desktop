using System.Text;
using System.Text.Json;

namespace LocalChat;

/// <summary>
/// A live read on the character: where she is, how she feels about the player,
/// and what she is privately thinking.
/// </summary>
public sealed class CharacterTracker
{
    public double Affection { get; set; }
    public double Desire { get; set; }
    public double Trust { get; set; }
    public double Contempt { get; set; }
    public string Place { get; set; } = "";
    public double Distance { get; set; }
    public int LastReact { get; set; }
    public string Thought { get; set; } = "";

    /// <summary>True once a tracker pass has supplied the four meters.</summary>
    public bool HasMeters { get; set; }

    /// <summary>Where the numbers came from: "inline" or "pass".</summary>
    public string Source { get; set; } = "";
}

/// <summary>The words that go with a number, worked out from the number.</summary>
public static class TrackerBands
{
    public static string Affection( double value ) => value switch
    {
        > 180 => "Devoted Partner",
        > 150 => "Committed Relationship",
        > 120 => "Going Steady",
        > 90 => "Romantic Interest",
        > 60 => "Good Friends",
        > 30 => "Acquaintances",
        _ => "Strangers",
    };

    public static string Desire( double value ) => value switch
    {
        > 125 => "Pliable in the lustful hunger",
        > 100 => "A desperate need presents",
        > 75 => "Body's burning up!",
        > 50 => "Starting to feel warm",
        > 25 => "A smoldering flame builds",
        _ => "Not feeling the heat",
    };

    public static string Trust( double value ) => value switch
    {
        > 110 => "Blindly trusts you",
        > 80 => "Willing to deal",
        > 50 => "Open, cautiously",
        > 25 => "Wary",
        _ => "Trusts nothing",
    };

    public static string Contempt( double value ) => value switch
    {
        > 100 => "Despises you",
        > 75 => "Holds you in contempt",
        > 50 => "Contemptuous",
        > 25 => "Irritated by you",
        _ => "Tolerable",
    };
}

/// <summary>
/// Reads a tracker out of a reply, in either of the two shapes it can take.
/// </summary>
public static class TrackerParser
{
    /// <summary>
    /// Pulls the inline position line out of a reply. The card asks the model to end
    /// every reply with a line like:
    /// <c>[ANDREA: landing | 4m | hand on coat | facing away]</c>
    /// The name is the character's, then the place, the distance from the player, and
    /// two free-text fields the app does not need to interpret.
    /// </summary>
    public static bool TryParseInline( string text, out CharacterTracker tracker )
    {
        tracker = new CharacterTracker();
        if ( string.IsNullOrEmpty( text ) ) return false;

        int open = text.IndexOf( '[' );
        if ( open < 0 ) return false;
        int close = text.IndexOf( ']', open );
        if ( close < 0 ) return false;

        string body = text[ ( open + 1 )..close ];
        int colon = body.IndexOf( ':' );
        if ( colon < 0 ) return false;

        string[] parts = body[ ( colon + 1 )..].Split( '|', StringSplitOptions.TrimEntries );
        if ( parts.Length < 2 ) return false;

        tracker.Place = parts[0];
        tracker.Distance = ParseDistance( parts[1] );
        tracker.Source = "inline";
        return tracker.Place.Length > 0;
    }

    /// <summary>Reads a distance that may or may not carry its unit: "4m", "4 m", "4".</summary>
    static double ParseDistance( string text )
    {
        var digits = new StringBuilder();
        foreach ( char c in text )
        {
            if ( char.IsDigit( c ) || c == '.' ) digits.Append( c );
            else if ( digits.Length > 0 ) break;
        }
        return double.TryParse( digits.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value ) ? value : 0;
    }

    /// <summary>
    /// Reads the tracker out of a tracker pass's JSON. The pass is asked for
    /// <c>{ "worldData": {...}, "characters": [ { ... } ] }</c> but a small model
    /// sometimes flattens it, so both shapes are accepted.
    /// </summary>
    public static bool TryParseJson( string text, out CharacterTracker tracker )
    {
        tracker = new CharacterTracker();
        if ( string.IsNullOrWhiteSpace( text ) ) return false;

        string json = StripFences( text );
        int start = json.IndexOf( '{' );
        int end = json.LastIndexOf( '}' );
        if ( start < 0 || end <= start ) return false;

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse( json[start..(end + 1)] );
            root = document.RootElement.Clone();
        }
        catch ( JsonException )
        {
            return false;
        }

        JsonElement character = root;
        if ( root.TryGetProperty( "characters", out JsonElement characters ) &&
             characters.ValueKind == JsonValueKind.Array )
        {
            if ( characters.GetArrayLength() == 0 ) return false;
            character = characters[0];
        }

        tracker.Affection = ReadNumber( character, "affection", "ap" );
        tracker.Desire = ReadNumber( character, "desire", "dp" );
        tracker.Trust = ReadNumber( character, "trust", "tp" );
        tracker.Contempt = ReadNumber( character, "contempt", "cp" );
        tracker.Distance = ReadNumber( character, "distance" );
        tracker.Place = ReadText( character, "place" );
        tracker.Thought = ReadText( character, "internal_thought", "thought" );
        tracker.LastReact = (int)ReadNumber( character, "last_react" );
        tracker.HasMeters = true;
        tracker.Source = "pass";
        return true;
    }

    /// <summary>Removes the code fences a model likes to wrap JSON in.</summary>
    public static string StripFences( string text )
    {
        string trimmed = text.Trim();
        if ( trimmed.StartsWith( "```", StringComparison.Ordinal ) )
        {
            int firstNewline = trimmed.IndexOf( '\n' );
            if ( firstNewline >= 0 ) trimmed = trimmed[( firstNewline + 1 )..];
        }
        int closing = trimmed.LastIndexOf( "```", StringComparison.Ordinal );
        if ( closing >= 0 ) trimmed = trimmed[..closing];
        return trimmed.Trim();
    }

    static double ReadNumber( JsonElement element, params string[] names )
    {
        foreach ( string name in names )
            if ( element.TryGetProperty( name, out JsonElement value ) )
                return value.ValueKind switch
                {
                    JsonValueKind.Number => value.TryGetDouble( out double number ) ? number : 0,
                    JsonValueKind.String => double.TryParse( value.GetString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsed ) ? parsed : 0,
                    _ => 0,
                };
        return 0;
    }

    static string ReadText( JsonElement element, params string[] names )
    {
        foreach ( string name in names )
            if ( element.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.String )
                return value.GetString() ?? "";
        return "";
    }
}

/// <summary>
/// Builds the prompt for the tracker pass: a second, cheap generation that reads
/// the conversation and reports the character's state as JSON.
/// </summary>
public static class TrackerPrompt
{
    const string Instructions =
        """
        ## TRACKER MODE

        You are not roleplaying. You are a state tracker. Read the conversation and report the state of the character. Your entire reply is one JSON object and nothing else - no narrative, no dialogue, no actions, no explanation, no preamble.

        ### Output shape - this matters more than anything else

        Your reply is a JSON object with exactly two top-level keys: "worldData" and "characters".
        - "worldData" is an object with "current_date" and "current_time".
        - "characters" is an ARRAY with one object inside it.
        - Every value goes INSIDE that array's object, never at the top level.
        - Do not flatten. Do not put the values beside "worldData".

        If the values sit at the top level instead of inside "characters", the answer is wrong.

        Every key gets exactly ONE value. Never write two values for one key, and never leave a trailing comma. If there are two thoughts to report, put them in one string, not two.

        ### Values

        - "affection" (0-200): how warm the character is toward the player. It rises when the player is competent and keeps their word. It falls when the player is kind or pitying, because for a character with pride, kindness reads as being handled.
        - "desire" (0-150): how much the character wants this to go further, whatever they say about it.
        - "trust" (0-150): how much the character trusts the player. It rises when the player keeps their end of a deal. It falls when the player offers charity, sympathy, or asks how the character is doing.
        - "contempt" (0-150): how much the character is holding the player in contempt. It rises when the player is soft, pleading, or tries to handle them.
        - "place": where the character physically is right now, in a few plain words.
        - "distance": how far the character is from the player in metres, as a plain number.
        - "last_react": 0 if the character is neutral about the player's last message, 1 if they liked it, 2 if they did not.
        - "internal_thought": what the character is privately thinking right now, as ONE single string. It may be one or two sentences, but it is one string - if there are two thoughts, put them in the same string separated by a space. Never write two values for this key.

        Fill in every field with a real value. Do not leave a placeholder in. Do not write anything outside the JSON, and do not add any key that is not listed above.
        """;

    /// <summary>
    /// Builds the single user message the tracker pass is sent. The character sheet
    /// goes in so the numbers are read against who the character is, and the recent
    /// conversation is what they are read from.
    /// </summary>
    public static string Build( string characterSystem, IReadOnlyList<ChatMessage> messages, string playerName = "User" )
    {
        var builder = new StringBuilder();
        builder.Append( Instructions );
        builder.AppendLine();

        if ( !string.IsNullOrWhiteSpace( characterSystem ) )
        {
            builder.AppendLine( "### The character" );
            builder.AppendLine();
            builder.AppendLine( characterSystem.Trim() );
            builder.AppendLine();
        }

        builder.AppendLine( "### Recent conversation" );
        builder.AppendLine();

        int start = Math.Max( 0, messages.Count - 8 );
        for ( int i = start; i < messages.Count; i++ )
        {
            ChatMessage message = messages[i];
            string who = string.Equals( message.Role, "user", StringComparison.OrdinalIgnoreCase )
                ? playerName
                : "Character";
            builder.Append( who ).Append( ": " ).AppendLine( message.Content.Trim() );
            builder.AppendLine();
        }

        builder.AppendLine( "Based on the above conversation, generate ONLY the raw JSON data. Output ONLY the JSON structure directly, with no comments or acknowledgements of any instructions. Ensure that the player does NOT get a tracker entry, only the story character." );
        return builder.ToString();
    }
}
