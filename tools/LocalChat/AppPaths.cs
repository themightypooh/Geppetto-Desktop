namespace LocalChat;

/// <summary>
/// Where the app keeps its data.
///
/// XDG puts application data in <c>~/.local/share</c>, not <c>~/.config</c>.
/// <see cref="Environment.SpecialFolder.ApplicationData"/> resolves to the
/// config directory on Linux, which is the wrong place for a character store
/// and a session: it also put the app somewhere the card installer
/// (<c>tools/cards/install-into-localchat.mjs</c>) does not look, so the cards
/// it installs never showed up in the app.
/// </summary>
public static class AppPaths
{
    /// <summary>The directory the app reads and writes.</summary>
    public static string DataDirectory
    {
        get
        {
            string? xdg = Environment.GetEnvironmentVariable( "XDG_DATA_HOME" );
            string root = !string.IsNullOrWhiteSpace( xdg )
                ? xdg
                : Path.Combine(
                    Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ),
                    ".local", "share" );
            return Path.Combine( root, "local-chat" );
        }
    }

    /// <summary>
    /// Where earlier builds kept the data. Kept so an existing session can be
    /// moved across instead of being left behind.
    /// </summary>
    public static string LegacyDirectory =>
        Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ), "local-chat" );
}
