#if TOOLS
#nullable enable
using Godot;

namespace Geppetto;

/// <summary>Say, plus a toast in the editor corner, for results Poo has to see (Check model, Key, Auto-Key)
/// rather than ones that only belong in the Output log.</summary>
public static class GeppettoToast
{
	public static void Info( string message )
	{
		GeppettoToolContext.Say( message );
		Push( message, EditorToaster.Severity.Info );
	}

	/// <summary>Pushes a toast that fits whole. The editor's toaster cuts anything wider than half the window
	/// with "…" (the rest only in a tooltip), so a long message is cut at the last sentence that fits and
	/// points at the Output panel, where the whole message already is.</summary>
	public static void Push( string message, EditorToaster.Severity severity )
	{
		if ( !Engine.IsEditorHint() || EditorInterface.Singleton == null ) return;
		Callable.From( () => EditorInterface.Singleton.GetEditorToaster()?.PushToast( Fit( message ), severity ) ).CallDeferred();
	}

	const string More = " (more in Output)";

	static string Fit( string message )
	{
		var bc = EditorInterface.Singleton.GetBaseControl();
		var font = bc.GetThemeDefaultFont();
		int size = bc.GetThemeDefaultFontSize();
		float limit = bc.GetViewportRect().Size.X / 2f - 8f;
		float Width( string s ) => font.GetStringSize( s, HorizontalAlignment.Left, -1, size ).X;
		if ( Width( message ) <= limit ) return message;
		// Cut after the last sentence end (". ", "; ", ": ") that still fits with the pointer to Output.
		for ( int i = message.Length - 1; i > 0; i-- )
		{
			char ch = message[i - 1];
			if ( message[i] != ' ' || ( ch != '.' && ch != ';' && ch != ':' ) ) continue;
			string head = message[..( i - ( ch == '.' ? 0 : 1 ) )].TrimEnd();
			if ( Width( head + More ) <= limit ) return head + More;
		}
		// No sentence end fits: cut at the last word that fits.
		string cut = message;
		while ( cut.Length > 0 && Width( cut + "…" + More ) > limit )
		{
			int sp = cut.LastIndexOf( ' ' );
			cut = sp > 0 ? cut[..sp] : cut[..^1];
		}
		return cut + "…" + More;
	}
}
#endif
