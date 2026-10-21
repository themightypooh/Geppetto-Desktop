using System.Reflection;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Input.Sdl;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;

namespace LocalChat;

static class Program
{
    static int Main( string[] args )
    {
        ChatOptions options;
        try
        {
            options = ChatOptions.Parse( args );
        }
        catch ( Exception e )
        {
            Console.Error.WriteLine( e.Message );
            Console.Error.WriteLine();
            Console.Error.WriteLine( ChatOptions.Help );
            return 2;
        }

        if ( options.ShowHelp )
        {
            Console.WriteLine( ChatOptions.Help );
            return 0;
        }

        if ( options.SelfTest )
            return SelfTest.Run( options ).GetAwaiter().GetResult();

        if ( options.ListModels )
            return ListModels( options );

        return RunWindow( options );
    }

    static int ListModels( ChatOptions options )
    {
        try
        {
            var client = new KoboldClient( options.Endpoint );
            IReadOnlyList<string> models = client.ListModelsAsync().GetAwaiter().GetResult();
            string? loaded = client.GetLoadedModelAsync().GetAwaiter().GetResult();
            Console.WriteLine( $"loaded: {loaded ?? "(unknown)"}" );
            foreach ( string model in models ) Console.WriteLine( model );
            return 0;
        }
        catch ( Exception e )
        {
            Console.Error.WriteLine( e.Message );
            return 1;
        }
    }

    static int RunWindow( ChatOptions options )
    {
        string sessionPath = options.SessionPath ?? ChatSession.DefaultPath;
        MigrateLegacyData( sessionPath, ChatSession.DefaultCharacterPath );

        ChatSession session;
        if ( !ChatController.TryLoad( sessionPath, out ChatSession? loaded ) || loaded is null )
        {
            session = new ChatSession();
        }
        else
        {
            session = loaded;
        }

        if ( options.Model is not null ) session.Model = options.Model;
        session.Endpoint = options.Endpoint.ToString();

        CharacterStore characters = CharacterStore.Load( ChatSession.DefaultCharacterPath );
        if ( options.SystemPrompt is not null )
        {
            // A command line prompt becomes an ordinary character so the picker stays truthful.
            Character? custom = characters.Characters.FirstOrDefault( c => c.Name == "Custom" );
            if ( custom is null )
            {
                custom = new Character { Name = "Custom" };
                characters.Characters.Add( custom );
            }
            custom.SystemPrompt = options.SystemPrompt;
            characters.Save( ChatSession.DefaultCharacterPath );
            session.Character = "Custom";
        }

        var controller = new ChatController( session, sessionPath, characters );
        var ui = new ChatUi( controller, options.Endpoint.ToString() );

        SdlWindowing.Use();
        SdlInput.Use();

        var windowOptions = WindowOptions.Default with
        {
            Size = new Vector2D<int>( options.Width, options.Height ),
            Title = "Local Chat",
            VSync = true,
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.ForwardCompatible,
                new APIVersion( 3, 3 ) ),
        };

        using var window = Window.Create( windowOptions );
        GL? gl = null;
        ImGuiController? imgui = null;
        bool fontChecked = false;

        window.Load += () =>
        {
            gl = GL.GetApi( window );
            gl.Viewport( window.FramebufferSize );

            IInputContext input = window.CreateInput();

            // The controller can load a TrueType face itself; fall back to ImGui's built-in font.
            string? fontPath = FindFont();
            ImGuiFontConfig? fontConfig = fontPath is null ? null : new ImGuiFontConfig( fontPath, 17, null );
            imgui = new ImGuiController( gl, window, input, fontConfig );
            if ( fontPath is not null ) Console.WriteLine( $"font     : {fontPath}" );
        };

        window.FramebufferResize += size => gl?.Viewport( size );

        window.Render += dt =>
        {
            if ( imgui is null || gl is null ) return;
            gl.Viewport( window.FramebufferSize );
            imgui.Update( (float)dt );
            if ( !fontChecked )
            {
                // The effective font is only known once a frame is open. If the controller
                // did not adopt the TrueType face, scale the built-in one up to stay readable.
                fontChecked = true;
                float size = ImGui.GetFont().FontSize;
                if ( size < 16f ) ImGui.GetIO().FontGlobalScale = 16f / MathF.Max( 1f, size );
                Console.WriteLine( $"font     : {size:F0}px" );
            }
            ui.Draw();
            gl.ClearColor( 0.09f, 0.10f, 0.12f, 1f );
            gl.Clear( ClearBufferMask.ColorBufferBit );
            imgui.Render();
        };

        window.Closing += () =>
        {
            controller.Save();
        };

        try
        {
            window.Run();
        }
        finally
        {
            controller.Save();
            try
            {
                imgui?.Dispose();
            }
            catch ( Exception )
            {
                // Closing the window tears the SDL context down before this runs,
                // so the controller's GL cleanup fails. The window is already gone
                // at that point, so there is nothing left to release and the
                // failure must not take the whole process down with it.
            }
        }
        return 0;
    }

    /// <summary>
    /// Older builds kept the session and the character store in the config
    /// directory, where the card installer never looked, so the installed cards
    /// never appeared in the app. The data now lives in the XDG data directory;
    /// anything left behind in the old place is moved across once.
    /// </summary>
    static void MigrateLegacyData( string sessionPath, string characterPath )
    {
        try
        {
            string legacy = AppPaths.LegacyDirectory;
            string? target = Path.GetDirectoryName( sessionPath );
            if ( target is null ) return;
            if ( string.Equals( Path.GetFullPath( legacy ), Path.GetFullPath( target ), StringComparison.Ordinal ) ) return;

            foreach ( ( string name, string destination ) in new[] { ( "session.json", sessionPath ), ( "characters.json", characterPath ) } )
            {
                string source = Path.Combine( legacy, name );
                if ( !File.Exists( source ) ) continue;
                if ( File.Exists( destination ) ) continue;
                Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
                File.Move( source, destination );
                Console.WriteLine( $"local-chat: moved {source} to {destination}" );
            }
        }
        catch ( Exception e )
        {
            Console.WriteLine( $"local-chat: could not move the old data across: {e.Message}" );
        }
    }

    static string? FindFont()    {
        string[] candidates =
        {
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/Adwaita/AdwaitaSans-Regular.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        };
        foreach ( string path in candidates )
            if ( File.Exists( path ) ) return path;
        return null;
    }
}
