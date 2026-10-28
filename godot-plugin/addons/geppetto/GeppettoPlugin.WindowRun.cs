#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Geppetto;

// The real-window check, kept out of GeppettoPlugin.cs. Only runs when GEPPETTO_WINDOW_RUN names an
// output folder: it opens GEPPETTO_WINDOW_SCENE, selects the Geppetto character, picks every dial tool the
// way a dial click does (OK-ing feature boxes, dragging viewport tools across the model), saves, reopens,
// exports a GLB, screenshots each step and checks Geppetto's labels for clipped text. Then it quits.
public partial class GeppettoPlugin
{
	string _runOut = "";
	readonly List<string> _runLog = new();
	int _runShot;
	int _runFails;

	void MaybeStartWindowRun()
	{
		_runOut = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_RUN" ) ?? "";
		if ( _runOut.Length == 0 ) return;
		Directory.CreateDirectory( _runOut );
		_ = WindowRun();
	}

	async Task Wait( double seconds ) => await ToSignal( GetTree().CreateTimer( seconds ), SceneTreeTimer.SignalName.Timeout );

	void Note( string line, bool fail = false )
	{
		if ( fail ) _runFails++;
		var l = ( fail ? "FAIL " : "ok   " ) + line;
		_runLog.Add( l );
		GD.Print( "[WindowRun] " + l );
	}

	/// <summary>Saves the editor window and every other visible window (dialogs, popups) as PNGs.</summary>
	void Shot( string name )
	{
		var safe = new string( name.Select( ch => char.IsLetterOrDigit( ch ) ? ch : '-' ).ToArray() );
		var main = EditorInterface.Singleton.GetBaseControl().GetWindow();
		var file = Path.Combine( _runOut, $"{++_runShot:D3}-{safe}.png" );
		main.GetTexture().GetImage().SavePng( file );
		foreach ( var w in OpenWindows() )
			w.GetTexture()?.GetImage()?.SavePng( Path.Combine( _runOut, $"{_runShot:D3}-{safe}-popup-{w.Title.Replace( '/', '-' )}.png" ) );
	}

	static IEnumerable<Window> OpenWindows()
	{
		var stack = new Stack<Node>();
		stack.Push( EditorInterface.Singleton.GetBaseControl() );
		while ( stack.Count > 0 )
		{
			var n = stack.Pop();
			foreach ( var c in n.GetChildren() )
			{
				if ( c is Window w && w.Visible && w is not PopupMenu ) yield return w;
				stack.Push( c );
			}
		}
	}

	/// <summary>Flags Geppetto labels and buttons whose text is wider than the room they get.</summary>
	void CheckClipping( string step )
	{
		var roots = new List<Control>();
		roots.AddRange( _dials.Where( IsInstanceValid ) );
		if ( _featureTree != null ) roots.Add( _featureTree );
		if ( _timeline != null && _timelineDocked ) roots.Add( _timeline );
		foreach ( var w in OpenWindows() ) roots.AddRange( w.GetChildren().OfType<Control>() );
		int bad = 0;
		foreach ( var root in roots )
			foreach ( var c in Walk( root ) )
			{
				if ( !c.IsVisibleInTree() || c.Size.X < 2 ) continue;
				string text; Font? font; int size; bool safe;
				switch ( c )
				{
					case Label l:
						text = l.Text; font = l.GetThemeFont( "font" ); size = l.GetThemeFontSize( "font_size" );
						safe = l.AutowrapMode != TextServer.AutowrapMode.Off || l.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming;
						break;
					case Button b when b is not OptionButton:
						text = b.Text; font = b.GetThemeFont( "font" ); size = b.GetThemeFontSize( "font_size" );
						safe = b.AutowrapMode != TextServer.AutowrapMode.Off || b.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming || b.ClipText;
						if ( b.Icon != null ) text += "    ";
						break;
					default: continue;
				}
				if ( safe || string.IsNullOrEmpty( text ) || font == null ) continue;
				float need = text.Split( '\n' ).Max( t => font.GetStringSize( t, HorizontalAlignment.Left, -1, size ).X );
				if ( need > c.Size.X + 1 )
				{
					bad++;
					Note( $"{step}: text spills: \"{text.Trim()}\" needs {need:F0}px, has {c.Size.X:F0}px ({c.GetPath()})", fail: true );
				}
			}
		if ( bad == 0 ) Note( $"{step}: no clipped text" );
	}

	/// <summary>Fails when the shown dial's ring is not inside the editor window, where Poo can see it.</summary>
	void CheckDialOnScreen( string step )
	{
		var win = EditorInterface.Singleton.GetBaseControl().GetWindow();
		var screen = new Rect2( Vector2.Zero, win.Size );
		foreach ( var d in _dials.Where( d => IsInstanceValid( d ) && d.IsVisibleInTree() ) )
		{
			var ring = d.RingRectGlobal();
			Note( $"{step}: dial ring {ring} in window {screen} (dial rect {d.GetGlobalRect()}, base {EditorInterface.Singleton.GetBaseControl().Size})", fail: !screen.Encloses( ring ) );
			return;
		}
		Note( $"{step}: no dial shown", fail: true );
	}

	static IEnumerable<Control> Walk( Node n )
	{
		if ( n is Control c ) yield return c;
		foreach ( var ch in n.GetChildren() )
			foreach ( var d in Walk( ch ) ) yield return d;
	}

	/// <summary>Drags the active viewport tool across the middle of the first 3D viewport.</summary>
	void DragActiveTool()
	{
		if ( GeppettoTools.Active is not { } tool ) return;
		var vp = EditorInterface.Singleton.GetEditorViewport3D( 0 );
		var cam = vp.GetCamera3D();
		var mid = vp.GetVisibleRect().Size / 2;
		InputEvent Press( bool down, Vector2 at ) => new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, ButtonMask = down ? MouseButtonMask.Left : 0 };
		tool.Input( cam, new InputEventMouseMotion { Position = mid } );
		tool.Input( cam, Press( true, mid ) );
		for ( int i = 1; i <= 6; i++ )
			tool.Input( cam, new InputEventMouseMotion { Position = mid + new Vector2( i * 6, i * 3 ), Relative = new Vector2( 6, 3 ), ButtonMask = MouseButtonMask.Left } );
		tool.Input( cam, Press( false, mid + new Vector2( 36, 18 ) ) );
	}

	void SelectCharacter()
	{
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		var sel = EditorInterface.Singleton.GetSelection();
		sel.Clear();
		var node = root?.FindChild( "Character", true, false ) ?? root;
		if ( node != null ) sel.AddNode( node );
		RefreshDial();
	}

	async Task WindowRun()
	{
		try
		{
			await Wait( 4 );
			string scene = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_SCENE" ) ?? "res://e2e/room.tscn";
			EditorInterface.Singleton.OpenSceneFromPath( scene );
			await Wait( 2 );
			SelectCharacter();
			await Wait( 1 );
			if ( _character?.Doc is not { } doc ) { Note( "open: no Geppetto character in " + scene, fail: true ); Finish(); return; }
			int features0 = doc.Studio.Features.Count;
			Note( $"open: {_character.EffigyPath}, {features0} features, dial visible={_dials.Any( d => d.Visible )}" );
			Shot( "open" );
			CheckClipping( "open" );
			CheckDialOnScreen( "open" );

			// GEPPETTO_WINDOW_WS=Animate runs just that workspace.
			string only = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_WS" ) ?? "";
			for ( int w = 0; w < GeppettoDialData.Workspaces.Length; w++ )
			{
				var ws = GeppettoDialData.Workspaces[w];
				if ( only.Length > 0 && ws.Name != only ) continue;
				Note( $"{ws.Name}: switching" );
				GeppettoDialState.Ws = w;
				RefreshDial();
				await Wait( 0.3 );
				Shot( $"{ws.Name}-workspace" );
				CheckClipping( ws.Name );
				foreach ( var (stage, tools) in ws.Stages )
					foreach ( var raw in tools )
					{
						string tool = GeppettoDialData.Clean( raw );
						if ( tool is "Delete" or "Remesh" or "Subdivide" ) continue; // would wreck the model for the later steps
						SelectCharacter();
						int errs = 0;
						try { if ( !RunTool( ws.Name, tool ) ) { Note( $"{ws.Name} > {tool}: not wired", fail: true ); continue; } }
						catch ( Exception e ) { errs++; Note( $"{ws.Name} > {tool}: threw {e.GetType().Name}: {e.Message}", fail: true ); }
						await Wait( 0.35 );
						DragActiveTool();
						await Wait( 0.25 );
						Shot( $"{ws.Name}-{tool}" );
						CheckClipping( $"{ws.Name} > {tool}" );
						foreach ( var win in OpenWindows().ToList() )
						{
							if ( win is GeppettoFeatureDialog fd ) fd.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
							else win.Hide();
						}
						GeppettoTools.Activate( null );
						await Wait( 0.2 );
						var report = _character?.Doc?.LastReport;
						if ( errs == 0 ) Note( $"{ws.Name} > {tool}: ran{( report is { Errors.Count: > 0 } ? $" (rebuild errors: {report.Errors.Count})" : "" )}" );
					}
			}

			// Save, reopen and check the edits came back.
			doc = _character!.Doc!;
			int featuresEdited = doc.Studio.Features.Count;
			_SaveExternalData();
			EditorInterface.Singleton.SaveScene();
			await Wait( 1 );
			Note( $"save: features {features0} -> {featuresEdited}, dirty after save={doc.Session.IsDirty}", fail: doc.Session.IsDirty );
			Shot( "saved" );
			string effigy = ProjectSettings.GlobalizePath( _character.EffigyPath );
			GeppettoDocument.CloseAll();
			EditorInterface.Singleton.ReloadSceneFromPath( scene );
			await Wait( 2 );
			SelectCharacter();
			await Wait( 1 );
			int featuresBack = _character?.Doc?.Studio.Features.Count ?? -1;
			Note( $"reopen: {featuresBack} features (saved {featuresEdited})", fail: featuresBack != featuresEdited );
			Shot( "reopened" );

			string glb = Path.Combine( _runOut, "export.glb" );
			var err = EffigyGlbExporter.ExportEffigy( effigy, glb );
			long bytes = File.Exists( glb ) ? new FileInfo( glb ).Length : 0;
			Note( $"export GLB: {err}, {bytes} bytes", fail: err != Error.Ok || bytes == 0 );
		}
		catch ( Exception e ) { Note( "run threw: " + e, fail: true ); }
		Finish();
	}

	void Finish()
	{
		_runLog.Add( $"RESULT fails={_runFails}" );
		File.WriteAllLines( Path.Combine( _runOut, "run.txt" ), _runLog );
		GD.Print( $"[WindowRun] done, {_runFails} failures, shots in {_runOut}" );
		if ( System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_KEEP" ) != "1" ) GetTree().Quit( _runFails > 0 ? 1 : 0 );
	}
}
#endif
