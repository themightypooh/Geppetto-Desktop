using System.Text.Json;

namespace LocalChat;

/// <summary>
/// Headless checks for the server protocol. This runs without a window so the client
/// can be verified without taking over the user's display.
/// </summary>
public static class SelfTest
{
    public static async Task<int> Run( ChatOptions options )
    {
        int failures = 0;

        failures += Check( "sse delta extraction", () =>
        {
            string payload = "{\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"}}]}";
            return KoboldClient.ExtractDelta( payload ) == "Hello";
        } );

        failures += Check( "sse delta with null content is ignored", () =>
        {
            string payload = "{\"choices\":[{\"index\":0,\"delta\":{\"content\":null}}]}";
            return KoboldClient.ExtractDelta( payload ) is null;
        } );

        failures += Check( "sse delta with no choices is ignored", () =>
            KoboldClient.ExtractDelta( "{\"id\":\"x\"}" ) is null );

        failures += Check( "malformed sse payload does not throw", () =>
            KoboldClient.ExtractDelta( "{not json" ) is null );

        failures += Check( "completion content extraction", () =>
        {
            string body = "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Hi there\"},\"finish_reason\":\"stop\"}]}";
            return KoboldClient.ExtractContent( body ) == "Hi there";
        } );

        failures += Check( "request body carries the turns and sampling", () =>
        {
            var turns = new[] { new ChatTurn( "system", "be brief" ), new ChatTurn( "user", "hello" ) };
            byte[] bytes = KoboldClient.BuildRequestBody( turns, new SamplingOptions { Temperature = 0.7f, MaxTokens = 64 }, "some-model", stream: true );
            using JsonDocument document = JsonDocument.Parse( bytes );
            JsonElement root = document.RootElement;
            bool messages = root.GetProperty( "messages" ).GetArrayLength() == 2;
            bool stream = root.GetProperty( "stream" ).GetBoolean();
            bool model = root.GetProperty( "model" ).GetString() == "some-model";
            bool temperature = Math.Abs( root.GetProperty( "temperature" ).GetSingle() - 0.7f ) < 0.001f;
            return messages && stream && model && temperature;
        } );

        // Tracker checks. The tracker has to survive whatever a small model does to
        // the reply, because the numbers on the panel come from it.
        failures += Check( "the inline tracker line is read out of a reply", () =>
        {
            string reply = "*She leans on the wall.*\n\n[ANDREA: landing | 4m | hand on coat | facing away]";
            if ( !TrackerParser.TryParseInline( reply, out CharacterTracker tracker ) ) return false;
            return tracker.Place == "landing"
                && Math.Abs( tracker.Distance - 4 ) < 0.001
                && tracker.Source == "inline";
        } );

        failures += Check( "a distance without its unit still reads", () =>
        {
            if ( !TrackerParser.TryParseInline( "[ANDREA: kitchen | 3 | hands on bag]", out CharacterTracker tracker ) ) return false;
            return tracker.Place == "kitchen" && Math.Abs( tracker.Distance - 3 ) < 0.001;
        } );

        failures += Check( "a reply with no tracker line is not mistaken for one", () =>
            !TrackerParser.TryParseInline( "Just a normal reply about a kitchen.", out _ ) );

        failures += Check( "the tracker json is read in the shape the model is asked for", () =>
        {
            string json = "{\"worldData\":{\"current_date\":\"2026-10-20\",\"current_time\":\"04:12\"},"
                + "\"characters\":[{\"name\":\"Andrea\",\"affection\":20,\"desire\":80,\"trust\":30,\"contempt\":50,"
                + "\"place\":\"landing\",\"distance\":3,\"last_react\":2,"
                + "\"internal_thought\":\"He's not budging.\"}]}";
            if ( !TrackerParser.TryParseJson( json, out CharacterTracker tracker ) ) return false;
            return tracker.HasMeters
                && Math.Abs( tracker.Affection - 20 ) < 0.001
                && Math.Abs( tracker.Desire - 80 ) < 0.001
                && Math.Abs( tracker.Trust - 30 ) < 0.001
                && Math.Abs( tracker.Contempt - 50 ) < 0.001
                && tracker.Place == "landing"
                && tracker.LastReact == 2
                && tracker.Thought == "He's not budging.";
        } );

        failures += Check( "a flattened tracker json is still read", () =>
        {
            // Small models flatten the characters array often enough to be worth
            // handling: the values sit at the top level instead of inside it.
            string json = "{\"name\":\"Andrea\",\"affection\":35,\"desire\":60,\"trust\":25,\"contempt\":85,"
                + "\"place\":\"doorway\",\"distance\":2,\"last_react\":0,\"internal_thought\":\"Fine.\"}";
            if ( !TrackerParser.TryParseJson( json, out CharacterTracker tracker ) ) return false;
            return Math.Abs( tracker.Affection - 35 ) < 0.001
                && Math.Abs( tracker.Contempt - 85 ) < 0.001
                && tracker.Place == "doorway";
        } );

        failures += Check( "a tracker json in a code fence is read", () =>
        {
            string json = "```json\n{\"characters\":[{\"affection\":10,\"desire\":10,\"trust\":10,\"contempt\":10}]}\n```";
            return TrackerParser.TryParseJson( json, out CharacterTracker tracker )
                && Math.Abs( tracker.Affection - 10 ) < 0.001;
        } );

        failures += Check( "rubbish is not read as a tracker", () =>
            !TrackerParser.TryParseJson( "I'm not JSON at all, I'm a person having a bad night.", out _ ) );

        failures += Check( "the bands follow the number", () =>
            TrackerBands.Affection( 10 ) == "Strangers"
            && TrackerBands.Affection( 40 ) == "Acquaintances"
            && TrackerBands.Affection( 200 ) == "Devoted Partner"
            && TrackerBands.Trust( 30 ) == "Wary"
            && TrackerBands.Trust( 120 ) == "Blindly trusts you"
            && TrackerBands.Contempt( 85 ) == "Holds you in contempt"
            && TrackerBands.Contempt( 5 ) == "Tolerable" );

        failures += Check( "the tracker prompt asks for the shape the app reads", () =>
        {
            string prompt = TrackerPrompt.Build( "You are Andrea.", new List<ChatMessage>
            {
                new() { Role = "user", Content = "Come inside, then." },
                new() { Role = "assistant", Content = "No." },
            } );
            return prompt.Contains( "\"characters\"" )
                && prompt.Contains( "INSIDE that array" )
                && prompt.Contains( "You are Andrea." )
                && prompt.Contains( "Come inside, then." );
        } );

        // Character store checks. These are pure local logic and need no server.
        failures += Check( "the built-in cast is not empty", () =>
            CharacterStore.Defaults().Count > 1 );

        failures += Check( "the app and the card installer agree on the store path", () =>
        {
            // tools/cards/install-into-localchat.mjs writes to
            // ~/.local/share/local-chat/characters.json. If the app looks
            // anywhere else, the cards it installs never show up.
            string expected = Path.Combine(
                Environment.GetEnvironmentVariable( "XDG_DATA_HOME" )
                    ?? Path.Combine(
                        Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ),
                        ".local", "share" ),
                "local-chat", "characters.json" );
            return CharacterStore.DefaultPath == expected
                && ChatSession.DefaultCharacterPath == expected
                && ChatSession.DefaultPath == Path.Combine( Path.GetDirectoryName( expected )!, "session.json" );
        } );

        failures += Check( "a missing character file falls back to the built-in cast", () =>
        {
            string path = Path.Combine( Path.GetTempPath(), "local-chat-selftest-missing.json" );
            if ( File.Exists( path ) ) File.Delete( path );
            CharacterStore store = CharacterStore.Load( path );
            return store.Characters.Count > 1;
        } );

        failures += Check( "characters survive a save and load", () =>
        {
            string path = Path.Combine( Path.GetTempPath(), "local-chat-selftest-characters.json" );
            var store = new CharacterStore();
            store.Characters.Add( new Character { Name = "Tester", Tagline = "t", SystemPrompt = "You are a test.", Greeting = "Hello." } );
            store.Save( path );
            CharacterStore loaded = CharacterStore.Load( path );
            Character? found = loaded.Find( "Tester" );
            if ( File.Exists( path ) ) File.Delete( path );
            return found is not null && found.SystemPrompt == "You are a test." && found.Greeting == "Hello.";
        } );

        failures += Check( "selecting a character sets the prompt and starts a new conversation", () =>
        {
            var session = new ChatSession();
            session.Messages.Add( new ChatMessage { Role = "user", Content = "old" } );
            var store = new CharacterStore();
            var character = new Character { Name = "Nova", SystemPrompt = "You are Nova.", Greeting = "Well met." };
            store.Characters.Add( character );
            var controller = new ChatController( session, Path.Combine( Path.GetTempPath(), "local-chat-selftest-session.json" ), store )
            {
                AutoSave = false,
            };
            controller.SelectCharacter( character );
            return session.SystemPrompt == "You are Nova."
                && session.Character == "Nova"
                && session.Messages.Count == 1
                && session.Messages[0].Role == "assistant"
                && session.Messages[0].Content == "Well met.";
        } );

        failures += Check( "switching characters keeps both conversations", () =>
        {
            var session = new ChatSession();
            var store = new CharacterStore();
            var one = new Character { Name = "One", SystemPrompt = "You are One.", Greeting = "First." };
            var two = new Character { Name = "Two", SystemPrompt = "You are Two.", Greeting = "Second." };
            store.Characters.Add( one );
            store.Characters.Add( two );
            var controller = new ChatController( session, Path.Combine( Path.GetTempPath(), "local-chat-selftest-session.json" ), store )
            {
                AutoSave = false,
            };

            controller.SelectCharacter( one );
            session.Messages.Add( new ChatMessage { Role = "user", Content = "only for one" } );

            controller.SelectCharacter( two );
            bool twoIsFresh = session.Messages.Count == 1 && session.Messages[0].Content == "Second.";

            controller.SelectCharacter( one );
            bool oneIsIntact = session.Messages.Count == 2
                && session.Messages[1].Content == "only for one"
                && session.SystemPrompt == "You are One.";

            return twoIsFresh && oneIsIntact;
        } );

        failures += Check( "clearing a chat leaves the other characters alone", () =>
        {
            var session = new ChatSession();
            var store = new CharacterStore();
            var one = new Character { Name = "One", SystemPrompt = "You are One.", Greeting = "First." };
            var two = new Character { Name = "Two", SystemPrompt = "You are Two.", Greeting = "Second." };
            store.Characters.Add( one );
            store.Characters.Add( two );
            var controller = new ChatController( session, Path.Combine( Path.GetTempPath(), "local-chat-selftest-session.json" ), store )
            {
                AutoSave = false,
            };

            controller.SelectCharacter( one );
            session.Messages.Add( new ChatMessage { Role = "user", Content = "keep me" } );
            controller.SelectCharacter( two );
            controller.Clear();
            controller.SelectCharacter( one );

            return session.Messages.Count == 2 && session.Messages[1].Content == "keep me";
        } );

        failures += Check( "a session file without threads keeps its one conversation", () =>
        {
            var session = new ChatSession();
            session.Character = "One";
            session.Messages.Add( new ChatMessage { Role = "user", Content = "old session" } );
            var store = new CharacterStore();
            store.Characters.Add( new Character { Name = "One", SystemPrompt = "You are One.", Greeting = "First." } );
            var controller = new ChatController( session, Path.Combine( Path.GetTempPath(), "local-chat-selftest-session.json" ), store )
            {
                AutoSave = false,
            };
            return session.Threads.ContainsKey( "One" )
                && session.Threads["One"].Count == 1
                && session.Threads["One"][0].Content == "old session";
        } );

        // Live checks against the running server.
        var client = new KoboldClient( options.Endpoint ) { Timeout = TimeSpan.FromSeconds( 120 ) };

        string? loaded = null;
        failures += await CheckAsync( "server reports a loaded model", async () =>
        {
            loaded = await client.GetLoadedModelAsync();
            return !string.IsNullOrWhiteSpace( loaded );
        } );

        failures += await CheckAsync( "server lists models", async () =>
        {
            IReadOnlyList<string> models = await client.ListModelsAsync();
            return models.Count > 0;
        } );

        failures += await CheckAsync( "non-streaming completion returns text", async () =>
        {
            var turns = new[] { new ChatTurn( "user", "Reply with the single word: ready" ) };
            string text = await client.CompleteAsync( turns, new SamplingOptions { MaxTokens = 24, Temperature = 0.7f }, loaded );
            return text.Trim().Length > 0;
        } );

        failures += await CheckAsync( "streaming completion returns the same text", async () =>
        {
            var turns = new[] { new ChatTurn( "user", "Reply with the single word: ready" ) };
            var pieces = new List<string>();
            string text = await client.StreamAsync( turns, new SamplingOptions { MaxTokens = 24, Temperature = 0.7f }, loaded, pieces.Add );
            return text.Trim().Length > 0 && pieces.Count > 0 && string.Concat( pieces ) == text;
        } );

        failures += await CheckAsync( "an unreachable endpoint reports a readable error", async () =>
        {
            var dead = new KoboldClient( new Uri( "http://127.0.0.1:59999/" ) ) { Timeout = TimeSpan.FromSeconds( 5 ) };
            try
            {
                await dead.GetLoadedModelAsync();
                return false;
            }
            catch ( KoboldException e )
            {
                return e.Message.Length > 0;
            }
            catch ( Exception )
            {
                return true; // a socket error is still a handled failure, not a crash
            }
        } );

        Console.WriteLine( failures == 0 ? "selftest: PASS" : $"selftest: FAIL ({failures} failed)" );
        return failures == 0 ? 0 : 1;
    }

    static int Check( string name, Func<bool> test )
    {
        try
        {
            bool ok = test();
            Console.WriteLine( $"{( ok ? "pass" : "FAIL" )}  {name}" );
            return ok ? 0 : 1;
        }
        catch ( Exception e )
        {
            Console.WriteLine( $"FAIL  {name} (threw {e.GetType().Name}: {e.Message})" );
            return 1;
        }
    }

    static async Task<int> CheckAsync( string name, Func<Task<bool>> test )
    {
        try
        {
            bool ok = await test();
            Console.WriteLine( $"{( ok ? "pass" : "FAIL" )}  {name}" );
            return ok ? 0 : 1;
        }
        catch ( Exception e )
        {
            Console.WriteLine( $"FAIL  {name} (threw {e.GetType().Name}: {e.Message})" );
            for ( Exception? inner = e.InnerException; inner is not null; inner = inner.InnerException )
                Console.WriteLine( $"        caused by {inner.GetType().Name}: {inner.Message}" );
            return 1;
        }
    }
}
