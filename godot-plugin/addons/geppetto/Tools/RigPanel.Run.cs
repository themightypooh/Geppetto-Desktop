#if TOOLS
#nullable enable
using Godot;

namespace Geppetto;

// What the Rig audit window run (GeppettoPlugin.RigRun.cs) needs to click in the Rig panel: its bone tree,
// the right-click menu with its "Parent to" submenu, and the Head/Tail boxes. Read-only handles, no logic.
public partial class RigPanel
{
	internal Tree RunTree => _tree;
	internal PopupMenu RunMenu => _menu;
	internal PopupMenu RunParentMenu => _parentMenu;
	internal SpinBox[] RunHead => _head;
	internal SpinBox[] RunTail => _tail;
}
#endif
