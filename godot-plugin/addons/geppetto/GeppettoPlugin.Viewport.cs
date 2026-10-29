#if TOOLS
#nullable enable
using Godot;

namespace Geppetto;

// Viewport input for Geppetto's tools: while a tool is active (GeppettoTools.Active) the 3D viewport's
// mouse and keys go to it first, and its overlay is drawn over the viewport. Esc stops the tool.
// Kept out of GeppettoPlugin.cs and GeppettoPlugin.Dial.cs so tool work doesn't touch those files.
public partial class GeppettoPlugin
{
	void SetupViewportTools()
	{
		GeppettoDocument.UndoManager = GetUndoRedo();
		GeppettoTools.ActiveChanged += Repaint;
		GeppettoDocument.AnyChanged += RepaintDoc;
		SetForceDrawOverForwardingEnabled();
	}

	void Repaint() => UpdateOverlays();

	void RepaintDoc( GeppettoDocument _ ) => UpdateOverlays();

	void TeardownViewportTools()
	{
		GeppettoTools.ActiveChanged -= Repaint;
		GeppettoDocument.AnyChanged -= RepaintDoc;
		GeppettoTools.Activate( null );
		GeppettoDocument.UndoManager = null;
	}

	// Handle every 3D node so viewport input reaches us; we pass it straight on unless a tool is active.
	// (Godot lets several plugins handle one node, so this does not take the Skeleton3D editor's place.)
	public override bool _Handles( GodotObject @object ) => @object is Node3D;

	public override int _Forward3DGuiInput( Camera3D viewportCamera, InputEvent @event )
	{
		var tool = GeppettoTools.Active;
		if ( tool == null ) return (int)AfterGuiInput.Pass;
		if ( @event is InputEventKey { Pressed: true, Keycode: Key.Escape } )
		{
			GeppettoTools.Activate( null );
			return (int)AfterGuiInput.Stop;
		}
		bool used;
		try { used = tool.Input( viewportCamera, @event ); }
		catch ( System.Exception e ) { GD.PushError( $"[Geppetto] {tool.Name} failed: {e.Message}" ); GeppettoTools.Activate( null ); return (int)AfterGuiInput.Stop; }
		if ( used ) Repaint();
		return used ? (int)AfterGuiInput.Stop : (int)AfterGuiInput.Pass;
	}

	public override void _Forward3DDrawOverViewport( Control viewportControl ) => DrawOver( viewportControl );

	public override void _Forward3DForceDrawOverViewport( Control viewportControl ) => DrawOver( viewportControl );

	// Datum planes are always shown (they make no geometry, so this is the only sign one exists),
	// then the running tool's overlay on top.
	void DrawOver( Control viewportControl )
	{
		if ( _character is { } ch && EditorInterface.Singleton.GetEditorViewport3D( 0 )?.GetCamera3D() is { } cam )
			GeppettoDatumPlanes.Draw( viewportControl, cam, ch );
		GeppettoTools.Active?.Draw( viewportControl );
	}
}
#endif
