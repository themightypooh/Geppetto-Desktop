#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace Geppetto;

/// <summary>
/// What a dial tool gets when it runs: the plugin, the character and its live document, and the small
/// helpers every tool needs.
/// </summary>
public sealed class GeppettoToolContext
{
	public GeppettoPlugin Plugin { get; }
	public GeppettoCharacter? Character { get; }
	public string Workspace { get; }
	public string Tool { get; }

	public GeppettoToolContext( GeppettoPlugin plugin, GeppettoCharacter? character, string ws, string tool )
	{
		Plugin = plugin;
		Character = character;
		Workspace = ws;
		Tool = tool;
	}

	/// <summary>The character's live, editable document, or null when no character is selected.</summary>
	public GeppettoDocument? Doc => Character?.Doc;

	public EditorUndoRedoManager UndoRedo => Plugin.GetUndoRedo();

	/// <summary>Warns that the tool needs a character and returns true (the tool did run, it just had nothing to act on).</summary>
	public bool Need()
	{
		Warn( Character == null ? NoModel : "This needs a model with a body to work on." );
		return true;
	}

	public const string NoModel = "No Geppetto model in this scene yet. Pick any CAD or Model tool to start one, or drag a .effigy in.";

	public static void Say( string message ) { GD.Print( $"[Geppetto] {message}" ); Spoken?.Invoke( message, false ); }

	/// <summary>Every Say / Warn, for the window runs that check what a tool told the user.</summary>
	public static event Action<string, bool>? Spoken;

	/// <summary>Why a tool refused or what went wrong: a warning in Output and a toast in the editor, so a
	/// click that does nothing says why where the user is looking instead of only in the console.</summary>
	public static void Warn( string message )
	{
		GD.PushWarning( $"[Geppetto] {message}" );
		Spoken?.Invoke( message, true );
		GeppettoToast.Push( message, EditorToaster.Severity.Warning );
	}
}

/// <summary>
/// A tool that takes over the 3D viewport while it is active: face picking, brushes, handles. The plugin
/// forwards viewport input to <see cref="GeppettoTools.Active"/> and draws its overlay.
/// </summary>
public interface IGeppettoViewportTool
{
	string Name { get; }
	/// <summary>Return true to consume the event (the editor camera / selection then ignore it).</summary>
	bool Input( Camera3D camera, InputEvent e );
	/// <summary>Draws 2D hints over the viewport (labels, brush circle). Optional.</summary>
	void Draw( Control overlay ) { }
	/// <summary>Called when another tool replaces this one or Esc cancels it.</summary>
	void Exit() { }
}

/// <summary>
/// The tool table behind the dial. Each workspace registers its tools from its own file
/// (<c>Tools/Cad*.cs</c>, <c>Tools/Model*.cs</c> …) by implementing one of the partial Register methods,
/// so workspaces can be built in parallel without touching the same file. The dial's own
/// <c>RunTool</c> switch (Animate basics) runs first; anything it does not handle comes here.
/// </summary>
public static partial class GeppettoTools
{
	public delegate bool ToolRun( GeppettoToolContext ctx );

	static readonly Dictionary<(string Ws, string Tool), ToolRun> Table = new();
	static bool _registered;

	/// <summary>Registers the tool the dial shows as <paramref name="tool"/> (after <c>GeppettoDialData.Clean</c>,
	/// so without the trailing "…") in workspace <paramref name="ws"/>.</summary>
	public static void Register( string ws, string tool, ToolRun run ) => Table[( ws, tool )] = run;

	static partial void RegisterCad();
	static partial void RegisterCadPick();
	static partial void RegisterModel();
	static partial void RegisterPaint();
	static partial void RegisterRig();
	static partial void RegisterClothing();
	static partial void RegisterAnimate();
	static partial void RegisterShared();

	static void EnsureRegistered()
	{
		if ( _registered ) return;
		_registered = true;
		RegisterShared();
		RegisterCad();
		RegisterCadPick();
		RegisterModel();
		RegisterPaint();
		RegisterRig();
		RegisterClothing();
		RegisterAnimate();
	}

	public static bool Has( string ws, string tool ) { EnsureRegistered(); return Table.ContainsKey( ( ws, tool ) ); }

	public static IEnumerable<(string Ws, string Tool)> Registered { get { EnsureRegistered(); return Table.Keys; } }

	/// <summary>Runs a registered tool. False when none is registered.</summary>
	public static bool TryRun( GeppettoToolContext ctx )
	{
		EnsureRegistered();
		if ( !Table.TryGetValue( ( ctx.Workspace, ctx.Tool ), out var run ) ) return false;
		return run( ctx );
	}

	// =========================================================================================
	// The active viewport tool
	// =========================================================================================

	static IGeppettoViewportTool? _active;

	public static IGeppettoViewportTool? Active => _active;

	/// <summary>Raised when the active viewport tool changes (the plugin repaints the overlay).</summary>
	public static event Action? ActiveChanged;

	public static void Activate( IGeppettoViewportTool? tool )
	{
		if ( _active == tool ) return;
		var old = _active;
		_active = tool;
		old?.Exit();
		if ( tool != null ) GeppettoToolContext.Say( $"{tool.Name}: active (Esc to stop)" );
		ActiveChanged?.Invoke();
	}

	/// <summary>Asks the plugin to redraw the viewport overlay (after a hover change, a brush move).</summary>
	public static void Redraw() => ActiveChanged?.Invoke();
}
#endif
