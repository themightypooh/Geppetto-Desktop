#if TOOLS
using Godot;

namespace Geppetto;

// G12: the editor's save hook, kept out of GeppettoPlugin.cs. Ctrl+S / Save All calls
// _SaveExternalData, which writes every dirty Geppetto document back to its .effigy / .riganim.
public partial class GeppettoPlugin
{
	public override void _SaveExternalData() => EffigySaveBridge.SaveAllOpen();

	// Closing the editor with unsaved Geppetto edits asks first (empty forScene = editor-wide check).
	public override string _GetUnsavedStatus( string forScene )
	{
		if ( !string.IsNullOrEmpty( forScene ) ) return "";
		var paths = EffigySaveBridge.UnsavedPaths();
		return paths.Count == 0 ? "" : $"Save changes to {paths.Count} Geppetto file(s) before closing?";
	}
}
#endif
