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
		if ( !Engine.IsEditorHint() || EditorInterface.Singleton == null ) return;
		Callable.From( () => EditorInterface.Singleton.GetEditorToaster()?.PushToast( message, EditorToaster.Severity.Info ) ).CallDeferred();
	}
}
#endif
