using System.Numerics;
using ImGuiNET;

namespace LocalChat;

/// <summary>
/// Draws the chat window with ImGui: a status bar, the transcript, the composer and a
/// settings panel. All state lives in <see cref="ChatController"/>; this class only reads it.
/// </summary>
public sealed class ChatUi
{
    const uint InputCapacity = 8192;
    const float SettingsWidth = 320f;
    const float TrackerWidth = 250f;

    readonly ChatController controller;

    string input = "";
    int promptVersion;
    bool clearRequested;
    bool showSettings;
    int modelIndex;
    string endpointText = "";
    bool endpointEdited;
    bool focusInput = true;
    string banner = "";
    bool connectStarted;
    ImFontPtr? font;

    public ChatUi( ChatController controller, string endpointText )
    {
        this.controller = controller;
        this.endpointText = endpointText;
    }

    /// <summary>Uses a real typeface instead of ImGui's built-in bitmap font.</summary>
    public void SetFont( ImFontPtr value ) => font = value;

    static uint Rgb( byte r, byte g, byte b ) => (uint)( r | ( g << 8 ) | ( b << 16 ) | 0xFF000000 );

    /// <summary>The same packing as <see cref="Rgb"/> but with an alpha byte, for the draw list.</summary>
    static uint RgbaU( byte r, byte g, byte b, byte a ) => (uint)( r | ( g << 8 ) | ( b << 16 ) | ( a << 24 ) );

    static Vector4 Rgba( byte r, byte g, byte b, float a = 1f ) => new( r / 255f, g / 255f, b / 255f, a );

    public void Draw()
    {
        if ( !connectStarted )
        {
            connectStarted = true;
            _ = ConnectAsync();
        }

        if ( font.HasValue ) ImGui.PushFont( font.Value );
        DrawWindow();
        if ( font.HasValue ) ImGui.PopFont();
    }

    async Task ConnectAsync()
    {
        if ( !Uri.TryCreate( endpointText, UriKind.Absolute, out Uri? uri ) )
        {
            banner = "That endpoint is not a URL.";
            return;
        }
        banner = "";
        await controller.ConnectAsync( uri );
    }

    void DrawWindow()
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos( Vector2.Zero );
        ImGui.SetNextWindowSize( display );
        bool open = true;
        ImGui.Begin( "##root", ref open,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus );

        DrawTopBar();

        float inputHeight = MathF.Min( 190f, MathF.Max( 110f, display.Y * 0.24f ) );
        float bodyHeight = display.Y - 34f - inputHeight;

        ImGui.BeginChild( "##body", new Vector2( 0f, bodyHeight ) );
        float right = showSettings ? SettingsWidth : 0f;
        float trackerWidth = controller.Tracker is not null ? TrackerWidth : 0f;
        ImGui.BeginChild( "##transcript", new Vector2( MathF.Max( 120f, ImGui.GetContentRegionAvail().X - right - trackerWidth ), 0f ),
            ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse );
        DrawTranscript();
        ImGui.EndChild();

        if ( trackerWidth > 0f )
        {
            ImGui.SameLine();
            ImGui.BeginChild( "##tracker", new Vector2( trackerWidth, 0f ), ImGuiChildFlags.Border );
            DrawTracker();
            ImGui.EndChild();
        }

        if ( showSettings )
        {
            ImGui.SameLine();
            ImGui.BeginChild( "##settings", new Vector2( SettingsWidth, 0f ), ImGuiChildFlags.Border );
            DrawSettings();
            ImGui.EndChild();
        }
        ImGui.EndChild();

        DrawComposer( inputHeight );

        ImGui.End();
    }

    void DrawTopBar()
    {
        ImGui.PushStyleColor( ImGuiCol.ChildBg, Rgba( 24, 26, 32 ) );
        ImGui.BeginChild( "##topbar", new Vector2( 0f, 34f ), ImGuiChildFlags.Border );
        ImGui.PopStyleColor();

        bool online = controller.Connected;
        var dot = ImGui.GetWindowDrawList();
        Vector2 cursor = ImGui.GetCursorScreenPos();
        dot.AddRectFilled( cursor, cursor + new Vector2( 10f, 10f ), online ? Rgb( 88, 196, 104 ) : Rgb( 208, 92, 92 ) );
        ImGui.Dummy( new Vector2( 14f, 0f ) );
        ImGui.SameLine();

        ImGui.SetNextItemWidth( 240f );
        if ( ImGui.InputText( "##endpoint", ref endpointText, 256 ) ) endpointEdited = true;
        if ( endpointEdited )
        {
            ImGui.SameLine();
            if ( ImGui.Button( "Connect" ) )
            {
                endpointEdited = false;
                _ = ConnectAsync();
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled( "|" );
        ImGui.SameLine();

        // Character picker.
        List<Character> cast = controller.Characters.Characters;
        if ( cast.Count == 0 ) cast = controller.Characters.Characters = CharacterStore.Defaults();
        string[] names = cast.Select( c => c.Name ).ToArray();
        int selected = controller.SelectedCharacter is null ? -1 : cast.IndexOf( controller.SelectedCharacter );
        if ( selected < 0 ) selected = 0;
        ImGui.SetNextItemWidth( 210f );
        if ( ImGui.Combo( "##character", ref selected, names, names.Length ) )
            controller.SelectCharacter( cast[selected] );

        // Step to the next character without losing the conversation you are in.
        ImGui.SameLine();
        if ( ImGui.Button( "Next >" ) ) controller.SelectNextCharacter();

        ImGui.SameLine();
        ImGui.SetNextItemWidth( 220f );
        List<string> models = controller.Models.Count > 0 ? controller.Models.ToList() : new();
        if ( models.Count == 0 && controller.ServerModel is not null ) models.Add( controller.ServerModel );
        if ( models.Count == 0 ) models.Add( "(no model)" );
        if ( modelIndex >= models.Count ) modelIndex = 0;
        if ( ImGui.Combo( "##model", ref modelIndex, models.ToArray(), models.Count ) )
        {
            string chosen = models[modelIndex];
            if ( chosen != "(no model)" ) controller.Model = chosen;
        }

        ImGui.SameLine();
        if ( ImGui.Button( "Refresh" ) ) _ = controller.RefreshAsync();

        ImGui.SameLine();
        if ( ImGui.Button( "Settings" ) ) showSettings = !showSettings;

        ImGui.SameLine();
        if ( ImGui.Button( "New chat" ) ) controller.Clear();

        if ( !string.IsNullOrEmpty( banner ) )
        {
            ImGui.SameLine();
            ImGui.TextColored( Rgba( 240, 190, 90 ), banner );
        }

        // Right aligned status text.
        string status = controller.Status;
        Vector2 textSize = ImGui.CalcTextSize( status );
        float rightEdge = ImGui.GetWindowWidth() - 12f;
        ImGui.SetCursorPosX( MathF.Max( ImGui.GetCursorPosX(), rightEdge - textSize.X ) );
        ImGui.TextColored( controller.Error is not null ? Rgba( 232, 120, 120 ) : Rgba( 170, 178, 192 ), status );
        ImGui.EndChild();
    }

    void DrawTranscript()
    {
        bool streaming = controller.IsGenerating;
        string inflight = controller.InflightText;

        // Decide whether to follow the bottom before the content moves it.
        bool atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 6f;

        IReadOnlyList<ChatMessage> messages = controller.Messages;
        if ( messages.Count == 0 && !streaming && inflight.Length == 0 )
        {
            ImGui.TextDisabled( "No messages yet. Say something to the model." );
            return;
        }

        for ( int i = 0; i < messages.Count; i++ )
            DrawBubble( messages[i] );

        if ( streaming || inflight.Length > 0 )
            DrawBubble( new ChatMessage { Role = "assistant", Content = inflight } );

        if ( streaming || atBottom ) ImGui.SetScrollHereY( 1f );
    }

    void DrawBubble( ChatMessage message )
    {
        bool user = string.Equals( message.Role, "user", StringComparison.OrdinalIgnoreCase );
        ImGui.PushStyleColor( ImGuiCol.ChildBg, user ? Rgba( 38, 52, 78 ) : Rgba( 30, 32, 38 ) );
        ImGui.PushStyleVar( ImGuiStyleVar.ChildRounding, 6f );
        ImGui.PushStyleVar( ImGuiStyleVar.ChildBorderSize, 0f );
        ImGui.BeginChild( $"##msg{message.GetHashCode()}", new Vector2( 0f, 0f ), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar );
        ImGui.PopStyleVar( 2 );
        ImGui.PopStyleColor();

        if ( user )
        {
            ImGui.TextColored( Rgba( 130, 180, 255 ), "you" );
        }
        else
        {
            ImGui.TextColored( Rgba( 150, 220, 170 ), "model" );
        }
        ImGui.TextWrapped( message.Content.Length == 0 ? "..." : message.Content );
        ImGui.EndChild();
        ImGui.Spacing();
    }

    /// <summary>
    /// The tracker panel: where the character is, how she feels about the player,
    /// and what she is privately thinking. It appears once a reply has been read for
    /// a tracker line, and the four meters fill in when the tracker pass lands.
    /// </summary>
    void DrawTracker()
    {
        CharacterTracker? tracker = controller.Tracker;
        if ( tracker is null ) return;

        ImGui.TextColored( Rgba( 236, 230, 222 ), controller.SelectedCharacter?.Name ?? "Character" );
        ImGui.Separator();

        if ( tracker.Place.Length > 0 )
            ImGui.TextColored( Rgba( 195, 185, 171 ), $"● {tracker.Place}  ·  {tracker.Distance:0} m away" );

        if ( tracker.HasMeters )
        {
            ImGui.Spacing();
            DrawMeter( "Affection", tracker.Affection, 200, TrackerBands.Affection( tracker.Affection ), RgbaU( 201, 138, 149, 255 ) );
            DrawMeter( "Desire", tracker.Desire, 150, TrackerBands.Desire( tracker.Desire ), RgbaU( 211, 160, 92, 255 ) );
            DrawMeter( "Trust", tracker.Trust, 150, TrackerBands.Trust( tracker.Trust ), RgbaU( 111, 165, 160, 255 ) );
            DrawMeter( "Contempt", tracker.Contempt, 150, TrackerBands.Contempt( tracker.Contempt ), RgbaU( 143, 142, 168, 255 ) );
        }
        else if ( controller.TrackerBusy )
        {
            ImGui.Spacing();
            ImGui.TextDisabled( "reading the room..." );
        }

        if ( tracker.Thought.Length > 0 )
        {
            ImGui.Separator();
            ImGui.TextColored( Rgba( 111, 102, 92 ), "SHE IS THINKING" );
            ImGui.TextWrapped( tracker.Thought );
        }
    }

    /// <summary>One meter: the name, the number, the band, and a bar.</summary>
    void DrawMeter( string label, double value, double max, string band, uint colour )
    {
        ImGui.TextColored( Rgba( 125, 116, 106 ), label.ToUpperInvariant() );

        float x = ImGui.GetCursorPosX();
        float avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX( x + MathF.Max( 0f, avail - 26f ) );
        ImGui.TextColored( Rgba( 236, 230, 222 ), $"{value:0}" );

        ImGui.TextColored( Rgba( 154, 145, 134 ), band );

        var draw = ImGui.GetWindowDrawList();
        Vector2 p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        const float height = 4f;
        draw.AddRectFilled( p, new Vector2( p.X + w, p.Y + height ), RgbaU( 255, 255, 255, 20 ), 2f );
        float fill = (float)Math.Clamp( value / max, 0d, 1d ) * w;
        if ( fill > 0f )
            draw.AddRectFilled( p, new Vector2( p.X + fill, p.Y + height ), colour, 2f );
        ImGui.Dummy( new Vector2( 0f, height + 7f ) );
    }

    void DrawSettings()
    {
        ChatSession session = controller.Session;

        ImGui.TextUnformatted( "Character" );
        ImGui.Separator();

        Character? character = controller.SelectedCharacter;
        if ( character is null )
        {
            ImGui.TextDisabled( "No character selected." );
            if ( ImGui.Button( "New character" ) )
            {
                controller.AddCharacter( "New character" );
                character = controller.SelectedCharacter;
            }
        }
        else
        {
            string name = character.Name;
            string tagline = character.Tagline;
            string system = character.SystemPrompt;
            string greeting = character.Greeting;
            string trackerContext = character.TrackerContext;

            ImGui.SetNextItemWidth( -1f );
            ImGui.TextUnformatted( "Name" );
            ImGui.InputText( "##charname", ref name, 64 );

            ImGui.TextUnformatted( "Tagline" );
            ImGui.InputText( "##chartag", ref tagline, 128 );

            ImGui.TextUnformatted( "System prompt" );
            // A card with a lorebook and a set of example messages flattens to
            // well over 8k characters, so the box has to be big enough to hold it
            // without silently truncating what the model is being told.
            ImGui.InputTextMultiline( "##charsys", ref system, 65536, new Vector2( 0f, 140f ) );
            ImGui.TextDisabled( "Who the model is, how it speaks, what it must not do." );

            ImGui.TextUnformatted( "Tracker context" );
            ImGui.InputTextMultiline( "##chartrk", ref trackerContext, 65536, new Vector2( 0f, 90f ) );
            ImGui.TextDisabled( "A shorter version of the same, used when reading the scene for the tracker." );

            ImGui.TextUnformatted( "Greeting" );
            ImGui.InputTextMultiline( "##chargreet", ref greeting, 2048, new Vector2( 0f, 70f ) );
            ImGui.TextDisabled( "Shown as the first line when you pick this character." );

            if ( ImGui.Button( "Save character" ) )
                controller.SaveCharacter( name, tagline, system, greeting, trackerContext );
            ImGui.SameLine();
            if ( ImGui.Button( "New" ) )
            {
                controller.AddCharacter( "New character" );
            }
            ImGui.SameLine();
            if ( ImGui.Button( "Delete" ) )
            {
                controller.DeleteSelectedCharacter();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted( "Transcript" );
        ImGui.TextWrapped( $"{controller.Messages.Count} messages with {character?.Name ?? "this character"}." );
        ImGui.TextDisabled( "Every character keeps its own conversation, so switching back does not lose it." );
        if ( ImGui.Button( "Clear this chat" ) ) controller.Clear();

        ImGui.Separator();
        ImGui.TextUnformatted( "Tracker" );
        bool tracking = controller.TrackerEnabled;
        if ( ImGui.Checkbox( "Track character state", ref tracking ) ) controller.TrackerEnabled = tracking;
        ImGui.TextDisabled( "Reads the character's position from her reply, and runs a second pass for the meters." );

        ImGui.Separator();
        ImGui.TextUnformatted( "Sampling" );
        ImGui.SetNextItemWidth( -1f );

        SamplingOptions sampling = session.Sampling;

        // ImGui sliders need a ref target, so each value is edited through a local and written back.
        float temperature = sampling.Temperature;
        if ( ImGui.SliderFloat( "Temperature", ref temperature, 0f, 2f, "%.2f" ) ) sampling.Temperature = temperature;
        float topP = sampling.TopP;
        if ( ImGui.SliderFloat( "Top P", ref topP, 0f, 1f, "%.2f" ) ) sampling.TopP = topP;
        float minP = sampling.MinP;
        if ( ImGui.SliderFloat( "Min P", ref minP, 0f, 1f, "%.2f" ) ) sampling.MinP = minP;
        float repeat = sampling.RepeatPenalty;
        if ( ImGui.SliderFloat( "Repeat penalty", ref repeat, 0.5f, 2f, "%.2f" ) ) sampling.RepeatPenalty = repeat;
        int topK = sampling.TopK;
        if ( ImGui.SliderInt( "Top K", ref topK, 0, 200, "%d" ) ) sampling.TopK = topK;
        int maxTokens = sampling.MaxTokens;
        if ( ImGui.SliderInt( "Max tokens", ref maxTokens, 64, 4096, "%d" ) ) sampling.MaxTokens = maxTokens;

        ImGui.Separator();
        if ( controller.ServerModel is not null )
            ImGui.TextWrapped( $"Server model: {controller.ServerModel}" );
        if ( controller.TokensPerSecond > 0 )
            ImGui.TextWrapped( $"Last reply: {controller.TokensPerSecond:0.0} tok/s" );
        ImGui.TextWrapped( $"Session file: {controller.SessionPath}" );
        ImGui.TextWrapped( $"Characters: {controller.CharacterPath}" );
    }

    void DrawComposer( float inputHeight )
    {
        ImGui.PushStyleColor( ImGuiCol.ChildBg, Rgba( 24, 26, 32 ) );
        ImGui.BeginChild( "##composer", new Vector2( 0f, inputHeight ), ImGuiChildFlags.Border );
        ImGui.PopStyleColor();

        bool enterPressed = ImGui.IsKeyPressed( ImGuiKey.Enter, false );
        bool shift = ImGui.GetIO().KeyShift;

        // A clear asked for by the previous frame is applied before the widget is
        // drawn. ImGui ignores the value we hand it while the widget holds the
        // focus and writes its own buffer back over ours, so the box has to be
        // given a new id to make it read the empty string from us.
        if ( clearRequested )
        {
            clearRequested = false;
            input = "";
            promptVersion++;
            focusInput = true;
        }

        ImGui.SetNextItemWidth( -1f );
        ImGui.InputTextMultiline( $"##prompt{promptVersion}", ref input, InputCapacity, new Vector2( 0f, inputHeight - 30f ),
            ImGuiInputTextFlags.AllowTabInput );
        bool inputActive = ImGui.IsItemActive();

        if ( focusInput )
        {
            ImGui.SetKeyboardFocusHere( -1 );
            focusInput = false;
        }

        if ( enterPressed && inputActive && !shift && !controller.IsGenerating )
        {
            // ImGui already inserted the newline that Enter produced; drop it and send.
            if ( input.EndsWith( "\n" ) ) input = input[..^1];
            Submit();
        }

        if ( ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed( ImGuiKey.L ) )
        {
            controller.Clear();
            ClearPrompt();
        }

        // Step to the next character. Every character keeps its own conversation.
        if ( ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed( ImGuiKey.K ) )
        {
            controller.SelectNextCharacter();
        }

        bool canSend = !controller.IsGenerating && input.Trim().Length > 0 && controller.Connected;
        ImGui.SameLine();
        if ( controller.IsGenerating )
        {
            if ( ImGui.Button( "Stop" ) ) controller.Stop();
        }
        else
        {
            ImGui.BeginDisabled( !canSend );
            if ( ImGui.Button( "Send" ) ) Submit();
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        if ( ImGui.Button( "Clear input" ) ) ClearPrompt();
        ImGui.EndChild();
    }

    /// <summary>
    /// Empties the composer. The clear is applied at the start of the next frame,
    /// before the widget is drawn, so the box is given a new id and reads the
    /// empty string from us. ImGui holds the text in a buffer of its own and
    /// writes that buffer back over our variable every frame, so setting the
    /// variable to an empty string on its own does nothing.
    /// </summary>
    void ClearPrompt()
    {
        clearRequested = true;
    }

    void Submit()
    {
        string text = input.Trim();
        if ( text.Length == 0 ) return;
        if ( controller.Send( text ) ) ClearPrompt();
    }
}
