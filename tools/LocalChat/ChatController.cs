using System.Text;

namespace LocalChat;

/// <summary>
/// Owns the chat transcript, the connection to the server and the in-flight generation.
/// Rendering never blocks: the request runs on a background task and the reply is
/// appended to <see cref="Inflight"/> as fragments arrive.
/// </summary>
public sealed class ChatController
{
    readonly object gate = new();
    readonly StringBuilder inflight = new();

    public ChatSession Session { get; }
    public string SessionPath { get; }
    public bool AutoSave { get; set; } = true;

    /// <summary>The cast the player can choose from.</summary>
    public CharacterStore Characters { get; }
    public string CharacterPath { get; }
    public Character? SelectedCharacter { get; private set; }

    KoboldClient? client;
    CancellationTokenSource? cancellation;
    Task? generation;

    public bool IsGenerating { get; private set; }
    public string Status { get; private set; } = "Not connected.";
    public bool Connected { get; private set; }
    public string? ServerModel { get; private set; }
    public List<string> Models { get; private set; } = new();
    public string? Error { get; private set; }
    public double TokensPerSecond { get; private set; }

    /// <summary>
    /// The character's live state, if the card writes a tracker line or a tracker
    /// pass has run. It is null until something has been read out of a reply.
    /// </summary>
    public CharacterTracker? Tracker
    {
        get { lock ( gate ) return tracker; }
    }

    /// <summary>True while a tracker pass is running.</summary>
    public bool TrackerBusy { get; private set; }

    /// <summary>Whether to run a tracker pass after each reply.</summary>
    public bool TrackerEnabled { get; set; } = true;

    CharacterTracker? tracker;

    public ChatController( ChatSession session, string sessionPath, CharacterStore? characters = null, string? characterPath = null )
    {
        Session = session;
        SessionPath = sessionPath;
        CharacterPath = characterPath ?? ChatSession.DefaultCharacterPath;
        Characters = characters ?? CharacterStore.Load( CharacterPath );
        SelectedCharacter = Characters.Find( session.Character );
        if ( SelectedCharacter is null && Characters.Characters.Count > 0 )
        {
            // The session names a character the cast no longer has - the store was
            // replaced, or the card was renamed. Start with the first one and let
            // go of the transcript that belonged to the missing character.
            SelectedCharacter = Characters.Characters[0];
            session.Character = SelectedCharacter.Name;
            session.SystemPrompt = SelectedCharacter.SystemPrompt;
            var fresh = new List<ChatMessage>();
            session.Threads[SelectedCharacter.Name] = fresh;
            SeedGreeting( SelectedCharacter, fresh );
            session.Messages = fresh;
        }
        else if ( SelectedCharacter is not null )
        {
            session.SystemPrompt = SelectedCharacter.SystemPrompt;

            // A session file written before per-character transcripts has one
            // transcript and no threads. It belongs to the selected character.
            if ( session.Threads.TryGetValue( SelectedCharacter.Name, out List<ChatMessage>? existing ) && existing is not null )
            {
                session.Messages = existing;
            }
            else
            {
                session.Threads[SelectedCharacter.Name] = session.Messages;
                SeedGreeting( SelectedCharacter, session.Messages );
            }
        }
    }

    /// <summary>Puts a character's opening line in a transcript that has nothing in it yet.</summary>
    static void SeedGreeting( Character character, List<ChatMessage> thread )
    {
        if ( thread.Count > 0 ) return;
        if ( character.Greeting.Trim().Length == 0 ) return;
        thread.Add( new ChatMessage { Role = "assistant", Content = character.Greeting } );
    }

    public string InflightText
    {
        get { lock ( gate ) return inflight.ToString(); }
    }

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock ( gate ) return Session.Messages.ToArray(); }
    }

    public string? Model
    {
        get => Session.Model;
        set => Session.Model = value;
    }

    /// <summary>Points the client at a server and reads back what it has loaded.</summary>
    public async Task<bool> ConnectAsync( Uri endpoint, CancellationToken cancellationToken = default )
    {
        client = new KoboldClient( endpoint );
        Session.Endpoint = endpoint.ToString();
        Error = null;
        Status = $"Connecting to {endpoint}...";
        try
        {
            ServerModel = await client.GetLoadedModelAsync( cancellationToken );
            if ( string.IsNullOrWhiteSpace( Session.Model ) && ServerModel is not null )
                Session.Model = ServerModel;
            Models = ( await client.ListModelsAsync( cancellationToken ) ).ToList();
            Connected = true;
            Status = ServerModel is null
                ? $"Connected to {endpoint}."
                : $"Connected to {endpoint} - {ServerModel}";
            Console.WriteLine( $"local-chat: {Status}" );
            return true;
        }
        catch ( Exception e )
        {
            Connected = false;
            ServerModel = null;
            Error = e.Message;
            Status = "Not connected.";
            Console.WriteLine( $"local-chat: {e.Message}" );
            return false;
        }
    }

    /// <summary>Refreshes the model list and the loaded-model name.</summary>
    public async Task RefreshAsync( CancellationToken cancellationToken = default )
    {
        if ( client is null ) return;
        try
        {
            ServerModel = await client.GetLoadedModelAsync( cancellationToken );
            Models = ( await client.ListModelsAsync( cancellationToken ) ).ToList();
            Error = null;
        }
        catch ( Exception e )
        {
            Error = e.Message;
        }
    }

    /// <summary>Sends a user message and starts generating the reply.</summary>
    public bool Send( string text )
    {
        if ( IsGenerating ) return false;
        if ( client is null )
        {
            Error = "Not connected to a server.";
            return false;
        }
        text = text.Trim();
        if ( text.Length == 0 ) return false;

        lock ( gate )
        {
            Session.Messages.Add( new ChatMessage { Role = "user", Content = text } );
            inflight.Clear();
        }

        var turns = new List<ChatTurn>();
        if ( !string.IsNullOrWhiteSpace( Session.SystemPrompt ) )
            turns.Add( new ChatTurn( "system", Session.SystemPrompt ) );
        foreach ( ChatMessage message in Messages )
            turns.Add( new ChatTurn( message.Role, message.Content ) );

        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        IsGenerating = true;
        Error = null;
        TokensPerSecond = 0;
        Status = "Generating...";

        var sampling = Session.Sampling.Clone();
        string? model = Session.Model;
        KoboldClient active = client;
        var started = DateTime.UtcNow;

        generation = Task.Run( async () =>
        {
            try
            {
                await active.StreamAsync( turns, sampling, model, delta =>
                {
                    lock ( gate ) inflight.Append( delta );
                }, cancellation.Token );
                Finish( null, started );
                if ( TrackerEnabled ) _ = RunTrackerAsync( active, model, cancellation.Token );
            }
            catch ( OperationCanceledException )
            {
                Finish( "Stopped.", started );
            }
            catch ( Exception e )
            {
                Finish( e.Message, started );
            }
        }, cancellation.Token );
        return true;
    }

    /// <summary>
    /// What the tracker pass is told about the character. The card can carry a
    /// trimmed version for exactly this; if it does not, the full system prompt
    /// is used and the pass is simply slower.
    /// </summary>
    string TrackerContext()
    {
        string context = SelectedCharacter?.TrackerContext ?? "";
        return context.Length > 0 ? context : Session.SystemPrompt;
    }

    /// <summary>
    /// Reads the character's position straight out of the reply, if the card asks for
    /// a tracker line. This is free - it is already in the text - and it puts the
    /// position on screen before the slower tracker pass has run.
    /// </summary>
    void ReadInlineTracker( string text )
    {
        if ( !TrackerParser.TryParseInline( text, out CharacterTracker inline ) ) return;
        lock ( gate )
        {
            tracker ??= new CharacterTracker();
            tracker.Place = inline.Place;
            tracker.Distance = inline.Distance;
            tracker.Source = "inline";
        }
    }

    /// <summary>
    /// Runs a second, cheap generation that reads the conversation and reports the
    /// character's state as JSON. This is what fills in the four meters; the inline
    /// line only carries the position.
    /// </summary>
    async Task RunTrackerAsync( KoboldClient active, string? model, CancellationToken cancellationToken )
    {
        try
        {
            TrackerBusy = true;
            string prompt = TrackerPrompt.Build( TrackerContext(), Messages );

            var sampling = Session.Sampling.Clone();
            sampling.Temperature = 0.7f;
            sampling.MaxTokens = 400;

            string raw = await active.CompleteAsync(
                new List<ChatTurn> { new( "user", prompt ) }, sampling, model, cancellationToken );

            if ( TrackerParser.TryParseJson( raw, out CharacterTracker parsed ) )
            {
                lock ( gate )
                {
                    tracker ??= new CharacterTracker();
                    tracker.Affection = parsed.Affection;
                    tracker.Desire = parsed.Desire;
                    tracker.Trust = parsed.Trust;
                    tracker.Contempt = parsed.Contempt;
                    tracker.Place = parsed.Place.Length > 0 ? parsed.Place : tracker.Place;
                    tracker.Distance = parsed.Distance > 0 ? parsed.Distance : tracker.Distance;
                    tracker.LastReact = parsed.LastReact;
                    tracker.Thought = parsed.Thought;
                    tracker.HasMeters = true;
                    tracker.Source = "pass";
                }
            }
        }
        catch ( OperationCanceledException )
        {
            // A new message arrived and cancelled the pass. Nothing to report.
        }
        catch ( Exception e )
        {
            // A failed tracker pass is not worth interrupting the conversation over.
            Console.WriteLine( $"local-chat: tracker pass failed: {e.Message}" );
        }
        finally
        {
            TrackerBusy = false;
        }
    }

    void Finish( string? error, DateTime started )
    {
        string text;
        lock ( gate )
        {
            text = inflight.ToString();
            inflight.Clear();
            if ( text.Length > 0 )
                Session.Messages.Add( new ChatMessage { Role = "assistant", Content = text } );
        }

        ReadInlineTracker( text );

        double seconds = Math.Max( 0.001, ( DateTime.UtcNow - started ).TotalSeconds );
        TokensPerSecond = text.Length / 4.0 / seconds;

        IsGenerating = false;
        if ( error is null )
            Status = $"Done - {TokensPerSecond:0.0} tok/s.";
        else
        {
            Error = error;
            Status = error;
        }
        if ( AutoSave ) Save();
    }

    /// <summary>Cancels the in-flight generation. The partial reply is kept.</summary>
    public void Stop()
    {
        if ( !IsGenerating ) return;
        cancellation?.Cancel();
        Status = "Stopping...";
    }

    /// <summary>
    /// Switches to a character. Each character keeps its own transcript: the
    /// conversation you are leaving is filed under the character it belongs to
    /// and the one you are joining is picked back up where it was left.
    /// </summary>
    public void SelectCharacter( Character character )
    {
        // File the transcript you are leaving under the character it belongs to.
        if ( SelectedCharacter is not null )
            Session.Threads[SelectedCharacter.Name] = Session.Messages;

        SelectedCharacter = character;
        Session.Character = character.Name;
        Session.SystemPrompt = character.SystemPrompt;

        // A character never talked to before starts with its greeting; one that has
        // a transcript already keeps every word of it.
        if ( !Session.Threads.TryGetValue( character.Name, out List<ChatMessage>? thread ) || thread is null )
        {
            thread = new List<ChatMessage>();
            Session.Threads[character.Name] = thread;
            SeedGreeting( character, thread );
        }

        Session.Messages = thread;

        lock ( gate )
        {
            inflight.Clear();
            // The tracker belongs to the character it was read from.
            tracker = null;
        }
        Error = null;
        if ( AutoSave ) Save();
    }

    /// <summary>Adds a character to the cast and selects it.</summary>
    public Character AddCharacter( string name )
    {
        var character = new Character { Name = string.IsNullOrWhiteSpace( name ) ? "New character" : name.Trim() };
        Characters.Characters.Add( character );
        Characters.Save( CharacterPath );
        SelectCharacter( character );
        return character;
    }

    /// <summary>Moves to the next character in the cast, wrapping round at the end.</summary>
    public void SelectNextCharacter()
    {
        if ( Characters.Characters.Count == 0 ) return;
        int index = SelectedCharacter is null ? -1 : Characters.IndexOf( SelectedCharacter.Name );
        index = ( index + 1 ) % Characters.Characters.Count;
        SelectCharacter( Characters.Characters[index] );
    }

    /// <summary>Writes the edited fields of the selected character back to the store.</summary>
    public void SaveCharacter( string name, string tagline, string systemPrompt, string greeting, string? trackerContext = null )
    {
        if ( SelectedCharacter is null ) return;
        string previousName = SelectedCharacter.Name;
        SelectedCharacter.Name = string.IsNullOrWhiteSpace( name ) ? SelectedCharacter.Name : name.Trim();
        SelectedCharacter.Tagline = tagline;
        SelectedCharacter.SystemPrompt = systemPrompt;
        SelectedCharacter.Greeting = greeting;
        if ( trackerContext is not null ) SelectedCharacter.TrackerContext = trackerContext;
        Session.Character = SelectedCharacter.Name;
        Session.SystemPrompt = systemPrompt;

        // A renamed character keeps its transcript; the old key is dropped.
        if ( !string.Equals( previousName, SelectedCharacter.Name, StringComparison.Ordinal ) )
        {
            if ( Session.Threads.TryGetValue( previousName, out List<ChatMessage>? moved ) && moved is not null )
            {
                Session.Threads[SelectedCharacter.Name] = moved;
                Session.Threads.Remove( previousName );
            }
        }
        Session.Threads[SelectedCharacter.Name] = Session.Messages;

        Characters.Save( CharacterPath );
        if ( AutoSave ) Save();
    }

    /// <summary>Removes the selected character from the cast.</summary>
    public void DeleteSelectedCharacter()
    {
        if ( SelectedCharacter is null ) return;
        string name = SelectedCharacter.Name;
        Characters.Characters.Remove( SelectedCharacter );
        Characters.Save( CharacterPath );
        Session.Threads.Remove( name );
        SelectedCharacter = null;
        Session.Character = null;
        Session.SystemPrompt = "";
        Session.Messages = new List<ChatMessage>();
        if ( AutoSave ) Save();
    }

    /// <summary>Empties the transcript of the selected character, and only that one.</summary>
    public void Clear()
    {
        lock ( gate )
        {
            // Session.Messages is the selected character's own thread, so this
            // empties that conversation and leaves every other one alone.
            Session.Messages.Clear();
            inflight.Clear();
            tracker = null;
        }
        Error = null;
        if ( AutoSave ) Save();
    }

    /// <summary>Writes the session to disk. Failures are reported, never thrown at the UI.</summary>
    public bool Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName( SessionPath );
            if ( !string.IsNullOrEmpty( directory ) ) Directory.CreateDirectory( directory );
            string json = System.Text.Json.JsonSerializer.Serialize( Session, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } );
            File.WriteAllText( SessionPath, json );
            return true;
        }
        catch ( Exception e )
        {
            Error = $"Could not save the session: {e.Message}";
            return false;
        }
    }

    /// <summary>Loads a session file if it exists. Returns false when there is nothing to load.</summary>
    public static bool TryLoad( string path, out ChatSession? session )
    {
        session = null;
        try
        {
            if ( !File.Exists( path ) ) return false;
            string json = File.ReadAllText( path );
            session = System.Text.Json.JsonSerializer.Deserialize<ChatSession>( json );
            return session is not null;
        }
        catch ( Exception )
        {
            session = null;
            return false;
        }
    }
}
