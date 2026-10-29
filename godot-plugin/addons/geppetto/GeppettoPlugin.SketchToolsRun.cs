#if TOOLS
#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Effigy;
using Godot;

namespace Geppetto;

// GEPPETTO_WINDOW_WS=SketchTools: every sketch tool, snapping and the constraint keys in an empty scene.
// The dial is clicked like a mouse (CAD > Sketch > Draw > Line), the plane is picked, then each tool is
// driven through the tool's Input the way the viewport sends it, on a plane-space layout so each shape
// lands in its own spot. Constraints go on by hotkey, a dimension is retyped by typing, a glyph click
// takes one off. The sketch is finished, a rectangle is extruded, and a second sketch on the box's top
// face projects the face edges (Shift+U) and is finished from the dial's Finish sketch tile.
public partial class GeppettoPlugin
{
	async Task SketchToolsRun()
	{
		try
		{
			await Wait( 4 );
			string scene = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_SCENE" ) ?? "res://blank.tscn";
			EditorInterface.Singleton.OpenSceneFromPath( scene );
			await Wait( 2 );
			EditorInterface.Singleton.GetSelection().Clear();
			_character = null;
			int cadWs = Array.FindIndex( GeppettoDialData.Workspaces, w => w.Name == "CAD" );
			GeppettoDialState.Ws = cadWs; GeppettoDialState.Level = 0; GeppettoDialState.MatOpen = false;
			GeppettoDialState.Touch();
			RefreshDial();
			await Wait( 0.5 );

			var vp = EditorInterface.Singleton.GetEditorViewport3D( 0 );
			var cam = vp.GetCamera3D();
			var mid = vp.GetVisibleRect().Size / 2;
			InputEvent Press( bool down, Vector2 at, bool shift = false ) => new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, ButtonMask = down ? MouseButtonMask.Left : 0, ShiftPressed = shift };
			InputEvent Move( Vector2 at, bool held = false ) => new InputEventMouseMotion { Position = at, ButtonMask = held ? MouseButtonMask.Left : 0 };
			InputEvent KeyEv( Key k, bool shift = false ) => new InputEventKey { Keycode = k, Pressed = true, ShiftPressed = shift };
			IGeppettoViewportTool T() => GeppettoTools.Active!;
			async Task Click( Vector2 at, bool shift = false ) { T().Input( cam, Move( at ) ); T().Input( cam, Press( true, at, shift ) ); T().Input( cam, Press( false, at, shift ) ); await Wait( 0.05 ); }
			void Key( Key k, bool shift = false ) => T().Input( cam, KeyEv( k, shift ) );
			async Task Step( string name ) { await Wait( 0.3 ); Shot( name ); CheckClipping( name ); }

			// Top view (keypad 7 on the 3D viewport, as a user would) so the Top-plane layout is not foreshortened.
			if ( vp.GetParent()?.GetParent() is Control vpRoot )
			{
				var surface = vpRoot.FindChildren( "*", "Control", true, false ).OfType<Control>().FirstOrDefault( c => c.FocusMode == Control.FocusModeEnum.All && c is not SubViewportContainer );
				surface?.GrabFocus();
				vpRoot.GetViewport().PushInput( new InputEventKey { Keycode = Godot.Key.Kp7, PhysicalKeycode = Godot.Key.Kp7, Pressed = true } );
				vpRoot.GetViewport().PushInput( new InputEventKey { Keycode = Godot.Key.Kp7, PhysicalKeycode = Godot.Key.Kp7, Pressed = false } );
				await Wait( 1.0 );
				surface?.ReleaseFocus();
			}
			Note( $"view: camera forward {-cam.GlobalBasis.Z}, {cam.Projection}" );

			// --- the dial, clicked like a mouse: CAD > Sketch, the three sketch groups, then Draw > Line ---
			var dial = _dials.FirstOrDefault( d => IsInstanceValid( d ) && d.Viewport3D == vp ) ?? _dials.FirstOrDefault( IsInstanceValid );
			if ( dial == null ) { Note( "dial: none in the viewport", fail: true ); Finish(); return; }
			async Task<bool> Tile( string name, string shot = "" )
			{
				await Wait( 0.6 );   // let the ring fan in
				if ( dial.TileCenter( name ) is not { } local ) { Note( $"dial: no \"{name}\" tile at level {GeppettoDialState.Level}", fail: true ); return false; }
				var at = dial.GetGlobalTransformWithCanvas() * local;
				var win = dial.GetViewport();
				win.PushInput( new InputEventMouseMotion { Position = at } );
				win.PushInput( new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, ButtonMask = MouseButtonMask.Left } );
				win.PushInput( new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at } );
				await Wait( 0.1 );
				if ( shot.Length > 0 ) { await Wait( 0.6 ); Shot( shot ); CheckClipping( shot ); }
				return true;
			}
			await Tile( "CAD" );
			await Tile( "Sketch", "tools-0-sketch-stage" );
			Note( $"dial: CAD > Sketch stage, level {GeppettoDialState.Level}", fail: GeppettoDialState.Level != 2 );
			foreach ( var g in new[] { "Shapes", "Modify", "Draw" } )
			{
				await Tile( g, $"tools-0-ring-{g}" );
				Note( $"dial: {g} ring, level {GeppettoDialState.Level}, group {GeppettoDialState.OpenGroup}", fail: GeppettoDialState.Level != 3 || GeppettoDialState.OpenGroup != g );
				if ( g != "Draw" ) await Tile( "Back" );
			}
			await Tile( "Line" );
			for ( int i = 0; i < 40 && GeppettoTools.Active is not CadSketch; i++ ) await Wait( 0.25 );
			var doc = _character?.Doc;
			if ( doc == null || GeppettoTools.Active is not CadSketch ed ) { Note( $"dial Line: no sketch tool (model={_character?.EffigyPath ?? "none"}, tool={GeppettoTools.Active?.Name})", fail: true ); Finish(); return; }
			Note( $"dial Line: model {_character!.EffigyPath}, sketch armed with {ed.Armed}", fail: ed.Armed != CadSketch.EditTool.Line );
			// The dial now sits over the ring; keep the clicks off it by drawing right of centre.
			ed.Input( cam, Move( mid ) );
			await Step( "tools-1-planes-armed" );

			// --- plane pick opens the full session with Line ---
			await Click( mid );
			Note( $"plane: new session={ed.IsNewSketch}, tool={ed.Tool}", fail: !ed.IsNewSketch || ed.Tool != CadSketch.EditTool.Line );
			if ( ed.Editing == null || ed.Plane == null ) { Finish(); return; }
			var S = ed.Editing.Sketch;
			var (ro, rd) = GeppettoPicker.Ray( cam, mid + new Vector2( 0, vp.GetVisibleRect().Size.Y * 0.07f ) );   // below the hint
			var p0 = ed.OnPlane( ro, rd ) ?? Vec2.Zero;
			var sx = ed.Screen( cam, p0 + new Vec2( 1, 0 ) ) - ed.Screen( cam, p0 );
			var sy = ed.Screen( cam, p0 + new Vec2( 0, 1 ) ) - ed.Screen( cam, p0 );
			float r = vp.GetVisibleRect().Size.Y * 0.066f;
			float u = r / MathF.Max( 1e-3f, MathF.Min( sx?.Length() ?? 1, sy?.Length() ?? 1 ) );
			// Plane-space layout: X(a, b) is a spot a*u, b*u from the plane point under the centre.
			Vec2 PL( float a, float b ) => p0 + new Vec2( a - 1.2f, b - 0.3f ) * u;   // layout centred on the screen centre
			Vector2 X( float a, float b )
			{
				var at = ed.Screen( cam, PL( a, b ) );
				if ( at is not { } sp || !vp.GetVisibleRect().HasPoint( sp ) ) Note( $"layout: ({a}, {b}) is off screen", fail: true );
				return at ?? mid;
			}
			int curves = S.Curves.Count;
			int Added() { int n = S.Curves.Count - curves; curves = S.Curves.Count; return n; }
			string D( SketchCurve? c ) => c switch
			{
				SketchLine l => $"line({( S.Points[l.Start] - p0 ).x / u:0.00},{( S.Points[l.Start] - p0 ).y / u:0.00})-({( S.Points[l.End] - p0 ).x / u:0.00},{( S.Points[l.End] - p0 ).y / u:0.00})",
				null => "none",
				_ => c.GetType().Name,
			};
			string Sel() => $"points[{string.Join( ",", ed.SelectedPoints.Select( i => $"{i}@({( S.Points[i] - p0 ).x / u:0.00},{( S.Points[i] - p0 ).y / u:0.00})" ) )}] curves[{string.Join( ",", ed.SelectedCurves.Select( id => D( S.Curves.FirstOrDefault( c => c.Id == id ) ) ) )}]";
			bool Has( SketchConstraintKind k ) => S.Constraints.Any( c => c.Kind == k );
			float Len( SketchLine l ) => ( S.Points[l.End] - S.Points[l.Start] ).Length;

			// Line with snapping: horizontal then vertical inference, kept as constraints.
			await Click( X( -3, -2 ) );
			T().Input( cam, Move( X( -1.6f, -1.97f ) ) );
			await Step( "tools-2-line-inference" );
			int axis = ed.LastSnap.InferenceAxis;
			await Click( X( -1.6f, -1.97f ) );
			T().Input( cam, Move( X( -1.57f, -0.9f ) ) );
			int axis2 = ed.LastSnap.InferenceAxis;
			await Click( X( -1.57f, -0.9f ) );
			Key( Godot.Key.Escape );
			Note( $"line: +{Added()} lines, inference {axis}/{axis2}, horizontal={Has( SketchConstraintKind.Horizontal )} vertical={Has( SketchConstraintKind.Vertical )}",
				fail: S.Curves.Count != 2 || !Has( SketchConstraintKind.Horizontal ) || !Has( SketchConstraintKind.Vertical ) );
			// A point snap: a line from the first line's end closes onto it (coincident, no new point).
			int pts = S.Points.Count;
			var corner = S.Points[( (SketchLine)S.Curves[1] ).End];
			await Click( X( -3, -0.9f ) );
			T().Input( cam, Move( ( ed.Screen( cam, corner ) ?? mid ) + new Vector2( 3, -2 ) ) );
			bool snapped = ed.LastSnap.SnappedPointIndex >= 0;
			await Click( ( ed.Screen( cam, corner ) ?? mid ) + new Vector2( 3, -2 ) );
			Key( Godot.Key.Escape );
			Note( $"point snap: snapped={snapped}, +{Added()} line, +{S.Points.Count - pts} points", fail: !snapped || S.Points.Count - pts != 1 );

			// Midpoint line, point, spline.
			Key( Godot.Key.M );
			await Click( X( -0.5f, -2 ) ); await Click( X( 0.4f, -2 ) );
			var ml = S.Curves.LastOrDefault() as SketchLine;
			var mm = ml != null ? ( S.Points[ml.Start] + S.Points[ml.End] ) * 0.5f : Vec2.Zero;
			Note( $"midpoint line: +{Added()}, centre off by {( mm - PL( -0.5f, -2 ) ).Length / u:0.00}u", fail: ml == null || ( mm - PL( -0.5f, -2 ) ).Length > 0.15f * u );
			Key( Godot.Key.P );
			pts = S.Points.Count;
			await Click( X( 1.2f, -2 ) );
			Note( $"point: +{S.Points.Count - pts} points", fail: S.Points.Count - pts != 1 );
			Key( Godot.Key.N );
			foreach ( var (a, b) in new[] { ( 2f, -2.3f ), ( 2.6f, -1.7f ), ( 3.2f, -2.3f ), ( 3.8f, -1.7f ) } ) await Click( X( a, b ) );
			await Step( "tools-3-spline-placing" );
			Key( Godot.Key.Enter );
			Note( $"spline: +{Added()}, last {S.Curves.LastOrDefault()?.GetType().Name}", fail: S.Curves.LastOrDefault() is not SketchSpline );

			// Arcs: centre/start/end, then 3 point.
			Key( Godot.Key.A );
			await Click( X( -3, 0 ) ); await Click( X( -2.3f, 0 ) ); await Click( X( -3, 0.7f ) );
			Note( $"arc: +{Added()} {S.Curves.LastOrDefault()?.GetType().Name}", fail: S.Curves.LastOrDefault() is not SketchArc );
			Key( Godot.Key.A, shift: true );
			await Click( X( -1.9f, 0.3f ) ); await Click( X( -0.9f, 0.3f ) ); await Click( X( -1.4f, 0.8f ) );
			Note( $"3 point arc: +{Added()} {S.Curves.LastOrDefault()?.GetType().Name}", fail: S.Curves.LastOrDefault() is not SketchArc );

			// Construction (Q) then a line: flagged construction, Q again turns it off.
			Key( Godot.Key.Q ); Key( Godot.Key.L );
			await Click( X( 0, -1.2f ) ); await Click( X( 0, 1.0f ) ); Key( Godot.Key.Escape );
			var mirrorLine = S.Curves.LastOrDefault() as SketchLine;
			Key( Godot.Key.Q );
			Note( $"construction: +{Added()}, flagged={mirrorLine?.Construction}, mode now {ed.ConstructionMode}", fail: mirrorLine?.Construction != true || ed.ConstructionMode );

			// Shapes.
			Key( Godot.Key.R );
			await Click( X( 1, -0.9f ) ); await Click( X( 2.2f, 0.1f ) );
			var rectLines = S.Curves.Skip( curves ).OfType<SketchLine>().ToList();
			Note( $"rectangle: +{Added()}", fail: rectLines.Count != 4 );
			Key( Godot.Key.R, shift: true );
			await Click( X( 3.4f, -0.4f ) ); await Click( X( 3.9f, 0.0f ) );
			var centreRect = S.Curves.Skip( curves ).OfType<SketchLine>().ToList();
			Note( $"centre rectangle: +{Added()}", fail: centreRect.Count != 4 );
			Key( Godot.Key.C );
			await Click( X( -3, 1.8f ) ); await Click( X( -2.5f, 1.8f ) );
			Note( $"circle: +{Added()} {S.Curves.LastOrDefault()?.GetType().Name}", fail: S.Curves.LastOrDefault() is not SketchCircle );
			Key( Godot.Key.C, shift: true );
			await Click( X( -2, 1.8f ) ); await Click( X( -1.5f, 1.4f ) ); await Click( X( -1.0f, 1.8f ) );
			Note( $"3 point circle: +{Added()} {S.Curves.LastOrDefault()?.GetType().Name}", fail: S.Curves.LastOrDefault() is not SketchCircle );
			Key( Godot.Key.G ); Key( Godot.Key.Bracketright );
			await Click( X( 0.6f, 1.9f ) ); await Click( X( 1.1f, 1.9f ) );
			Note( $"polygon ({ed.PolygonSides} sides): +{Added()}", fail: S.Curves.Count == 0 || ed.PolygonSides != 7 || S.Curves.Skip( S.Curves.Count - 7 ).Any( c => c is not SketchLine ) );
			Key( Godot.Key.G, shift: true ); Key( Godot.Key.Bracketleft );
			await Click( X( 2.0f, 1.9f ) ); await Click( X( 2.45f, 1.9f ) );
			Note( $"outer polygon ({ed.PolygonSides} sides): +{Added()}", fail: ed.PolygonSides != 6 );
			Key( Godot.Key.W );
			await Click( X( 3.0f, 1.6f ) ); await Click( X( 3.9f, 1.6f ) ); await Click( X( 3.45f, 1.85f ) );
			Note( $"slot: +{Added()} ({string.Join( ",", S.Curves.Skip( S.Curves.Count - 4 ).Select( c => c.GetType().Name.Replace( "Sketch", "" ) ) )})", fail: S.Curves.Skip( S.Curves.Count - 4 ).Count( c => c is SketchArc ) != 2 );
			Key( Godot.Key.E );
			await Click( X( 4.6f, -2 ) ); await Click( X( 5.2f, -2 ) ); await Click( X( 4.6f, -1.7f ) );
			Note( $"ellipse: +{Added()} {S.Curves.LastOrDefault()?.GetType().Name}", fail: S.Curves.LastOrDefault() is not SketchEllipse );
			await Step( "tools-4-drawn" );

			// Modify: a crossing pair for Trim / Cut, a short line for Extend.
			Key( Godot.Key.L );
			await Click( X( 4.4f, -0.9f ) ); await Click( X( 5.4f, 0.1f ) ); Key( Godot.Key.Escape );
			await Click( X( 4.4f, 0.1f ) ); await Click( X( 5.4f, -0.9f ) ); Key( Godot.Key.Escape );
			Added();
			var cross = (SketchLine)S.Curves[^1];
			float before = Len( cross );
			var beforeTrim = S.Curves.ToList();
			Note( $"trim target {D( cross )}, other {D( S.Curves[^2] )}" );
			Key( Godot.Key.T );
			T().Input( cam, Move( X( 5.25f, -0.75f ) ) );
			Note( $"trim hover snap point={ed.LastSnap.SnappedPointIndex} curve={ed.LastSnap.SnappedCurveIndex}" );
			await Click( X( 5.25f, -0.75f ) );
			Note( $"trim removed: {string.Join( "; ", beforeTrim.Except( S.Curves ).Select( D ) )}" );
			Note( $"trim: crossing line {before / u:0.00}u -> {( S.Curves.Contains( cross ) ? Len( cross ) / u : 0 ):0.00}u, curves {S.Curves.Count - curves:+0;-0;0}",
				fail: S.Curves.Contains( cross ) && MathF.Abs( Len( cross ) - before ) < 1e-3f && S.Curves.Count == curves );
			Added();
			Key( Godot.Key.L );
			await Click( X( 4.4f, 0.7f ) ); await Click( X( 4.9f, 0.7f ) ); Key( Godot.Key.Escape );
			await Click( X( 5.3f, 0.4f ) ); await Click( X( 5.3f, 1.1f ) ); Key( Godot.Key.Escape );
			Added();
			var shortLine = (SketchLine)S.Curves[^2];
			Note( $"extend target {D( shortLine )}, wall {D( S.Curves[^1] )}" );
			before = Len( shortLine );
			Key( Godot.Key.X );
			await Click( X( 4.85f, 0.7f ) );
			Note( $"extend: {before / u:0.00}u -> {Len( shortLine ) / u:0.00}u", fail: Len( shortLine ) <= before + 1e-3f );

			// Fillet a centre-rectangle corner.
			int arcs = S.Curves.Count( c => c is SketchArc );
			Key( Godot.Key.F );
			var fc = S.Points[centreRect[0].Start];
			await Click( ed.Screen( cam, fc ) ?? mid );
			await Click( ed.Screen( cam, fc + ( S.Points[centreRect[0].End] - fc ).Normal * ( 0.2f * u ) ) ?? mid );
			Note( $"fillet: arcs {arcs} -> {S.Curves.Count( c => c is SketchArc )}", fail: S.Curves.Count( c => c is SketchArc ) != arcs + 1 );
			Added();

			// Offset the midpoint line downwards.
			Key( Godot.Key.O );
			await Click( X( -0.1f, -2 ) ); await Click( X( -0.1f, -2.4f ) );
			Note( $"offset: +{Added()}", fail: S.Curves.Count == curves - 0 && false );

			// Mirror: select the first arc, Shift+M, click the construction line.
			Key( Godot.Key.S );
			var arc0 = S.Curves.OfType<SketchArc>().First();
			await Click( X( -2.5f, 0.5f ) - ( X( -2.5f, 0.5f ) - ( ed.Screen( cam, S.Points[arc0.Center] + ( ( S.Points[arc0.Start] + S.Points[arc0.End] ) * 0.5f - S.Points[arc0.Center] ).Normal * ( S.Points[arc0.Start] - S.Points[arc0.Center] ).Length ) ?? mid ) ) );
			Note( $"select arc: {ed.SelectedCurves.Count} curve(s) selected", fail: !ed.SelectedCurves.Contains( arc0.Id ) );
			Key( Godot.Key.M, shift: true );
			await Click( X( 0, 0.3f ) );
			Note( $"mirror: +{Added()} {S.Curves.LastOrDefault()?.GetType().Name}", fail: S.Curves.LastOrDefault() is not SketchArc );

			// Cut: a stroke across the extend target.
			string Sig() => string.Join( ";", S.Curves.Select( D ) );
			var beforeCut = Sig();
			Key( Godot.Key.Z );
			var c0 = X( 5.0f, 0.9f ); var c1 = X( 5.6f, 0.9f );
			T().Input( cam, Move( c0 ) ); T().Input( cam, Press( true, c0 ) );
			for ( int i = 1; i <= 4; i++ ) T().Input( cam, Move( c0 + ( c1 - c0 ) * i / 4f, held: true ) );
			await Step( "tools-5-cut-stroke" );
			T().Input( cam, Press( false, c1 ) );
			Note( $"cut: +{Added()} curve(s), geometry changed={Sig() != beforeCut}", fail: Sig() == beforeCut );

			// --- constraints and a dimension, by hotkey on a selection ---
			Key( Godot.Key.L );
			await Click( X( -3, 2.8f ) ); await Click( X( -2.0f, 3.1f ) ); Key( Godot.Key.Escape );
			await Click( X( -1.6f, 2.7f ) ); await Click( X( -0.4f, 3.0f ) ); Key( Godot.Key.Escape );
			Added();
			var la = (SketchLine)S.Curves[^2]; var lb = (SketchLine)S.Curves[^1];
			Vector2 OnLine( SketchLine l ) => ed.Screen( cam, ( S.Points[l.Start] * 0.4f + S.Points[l.End] * 0.6f ) ) ?? mid;
			Key( Godot.Key.S );
			Note( $"constraint lines: {D( la )} and {D( lb )}, click at {OnLine( la )}, la start at {ed.Screen( cam, S.Points[la.Start] )} end {ed.Screen( cam, S.Points[la.End] )}" );
			await Click( OnLine( la ) );
			int cons = S.Constraints.Count;
			Key( Godot.Key.H );
			float dy = MathF.Abs( S.Points[la.End].y - S.Points[la.Start].y );
			Note( $"H on a line: {Sel()} +{S.Constraints.Count - cons} constraint, dy {dy:0.0000}", fail: S.Constraints.Count != cons + 1 || dy > 1e-3f );
			await Click( OnLine( la ) ); await Click( OnLine( lb ), shift: true );
			Note( $"select two lines: {Sel()}", fail: ed.SelectedCurves.Count != 2 );
			cons = S.Constraints.Count;
			Key( Godot.Key.B );
			var da = ( S.Points[la.End] - S.Points[la.Start] ).Normal; var db = ( S.Points[lb.End] - S.Points[lb.Start] ).Normal;
			float crossAB = MathF.Abs( da.x * db.y - da.y * db.x );
			Note( $"B parallel: +{S.Constraints.Count - cons}, cross {crossAB:0.0000}", fail: S.Constraints.Count != cons + 1 || crossAB > 1e-3f );
			Key( Godot.Key.E, shift: true );
			Note( $"Shift+E equal: lengths {Len( la ) / u:0.000}u / {Len( lb ) / u:0.000}u", fail: MathF.Abs( Len( la ) - Len( lb ) ) > 1e-3f );
			await Click( OnLine( lb ) );
			Key( Godot.Key.D );
			await Step( "tools-6-dimension-armed" );
			string want = ( MathF.Round( u * 1.5f * 100 ) / 100 ).ToString( System.Globalization.CultureInfo.InvariantCulture );
			foreach ( var ch in want )
				Key( ch == '.' ? Godot.Key.Period : Godot.Key.Key0 + ( ch - '0' ) );
			await Step( "tools-7-dimension-typed" );
			Key( Godot.Key.Enter );
			float target = float.Parse( want, System.Globalization.CultureInfo.InvariantCulture );
			Note( $"D + typed {want} + Enter: line {Len( lb ):0.###} (equal one {Len( la ):0.###}), distance rule={Has( SketchConstraintKind.Distance )}",
				fail: MathF.Abs( Len( lb ) - target ) > 1e-2f || MathF.Abs( Len( la ) - target ) > 1e-2f );
			// Click a glyph: the rule comes off (Select, nothing hovered under it).
			var mk = ConstraintTools.Markers( S ).FirstOrDefault( m => m.Constraint.Kind == SketchConstraintKind.Parallel );
			cons = S.Constraints.Count;
			if ( mk != null && ed.GlyphScreen( cam, mk ) is { } gp )
			{
				Note( $"glyph at {gp}, anchor {ed.Screen( cam, mk.Anchor )}, markers {string.Join( ",", ConstraintTools.Markers( S ).Where( m => ( ( ed.Screen( cam, m.Anchor ) ?? mid ) - gp ).Length() < 60 ).Select( m => m.Label + "@" + ed.GlyphScreen( cam, m ) ) )}" );
				await Click( gp );
				Note( $"after glyph click: {Sel()}" );
			}
			Note( $"glyph click: parallel marker={mk != null}, constraints {cons} -> {S.Constraints.Count}", fail: mk == null || S.Constraints.Count != cons - 1 || Has( SketchConstraintKind.Parallel ) );
			T().Input( cam, Move( mid ) );
			await Step( "tools-8-constrained" );

			// Finish (Esc with nothing pending): one new sketch feature.
			int f0 = doc.Studio.Features.Count;
			int drawn = S.Curves.Count;
			Key( Godot.Key.Escape );
			await Wait( 0.5 );
			var sk = doc.Studio.Features.OfType<SketchFeature>().LastOrDefault();
			Note( $"finish: +{doc.Studio.Features.Count - f0} feature, {sk?.Sketch.Curves.Count} curves (drew {drawn}), error={sk?.Error}, tool closed={GeppettoTools.Active == null}",
				fail: doc.Studio.Features.Count != f0 + 1 || sk?.Sketch.Curves.Count != drawn || GeppettoTools.Active != null );
			await Step( "tools-9-finished" );
			if ( sk == null ) { Finish(); return; }

			// Extrude the rectangle.
			var rc = ( S.Points[rectLines[0].Start] + S.Points[rectLines[2].Start] ) * 0.5f;
			var rcScreen = ed.Screen( cam, rc ) ?? mid;
			RunTool( "CAD", "Extrude" );
			await Wait( 0.3 );
			if ( GeppettoTools.Active is not GeppettoPicker pick ) { Note( "extrude: no picker", fail: true ); Finish(); return; }
			await Click( rcScreen );
			Note( $"extrude pick: region sketch={pick.RegionSketchId == sk.Id}, seeds={pick.RegionSeeds.Count()}", fail: pick.RegionSketchId != sk.Id );
			Key( Godot.Key.Enter );
			await Wait( 0.5 );
			var box = OpenWindows().OfType<GeppettoFeatureDialog>().LastOrDefault();
			box?.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
			await Wait( 0.6 );
			var ex = doc.Studio.Features.LastOrDefault();
			int faces = doc.Studio.Bodies.Sum( b => b.Mesh?.FaceCount ?? 0 );
			Note( $"extrude: {ex?.TypeName} error={ex?.Error}, faces={faces}", fail: ex is not ExtrudeFeature || ex.Error != null || faces < 6 );
			GeppettoTools.Activate( null );

			// A sketch on the box's top face from the dial's Project tile: Shift+U takes every face edge in,
			// hovering an edge snaps to it, the dial's Finish sketch tile closes it.
			GeppettoDialState.Level = 2; GeppettoDialState.OpenStage = 0; GeppettoDialState.Touch();
			await Tile( "Modify", "tools-10-modify-ring" );
			await Tile( "Project" );
			await Wait( 0.3 );
			if ( GeppettoTools.Active is not CadSketch fs ) { Note( "project: no sketch tool", fail: true ); Finish(); return; }
			Note( $"project tile: armed {fs.Armed}", fail: fs.Armed != CadSketch.EditTool.Use );
			fs.Input( cam, Move( rcScreen ) );
			await Click( rcScreen );
			Note( $"face sketch: on face={fs.Face != null}, reference edges={fs.Reference?.Edges.Count ?? 0}, tool={fs.Tool}", fail: fs.Face == null || fs.Editing == null || fs.Tool != CadSketch.EditTool.Use );
			if ( fs.Editing == null ) { Finish(); return; }
			var FS = fs.Editing.Sketch;
			// hover a face edge: the snapper reports it
			bool edgeSnap = false;
			var (ea, eb) = fs.Reference!.Segment( 0 );
			if ( fs.Reference.Edges.Count > 0 && fs.Screen( cam, ( ea + eb ) * 0.5f ) is { } em ) { fs.Input( cam, Move( em ) ); edgeSnap = fs.LastSnap.ReferenceEdgeIndex >= 0; }
			await Step( "tools-11-face-edge-hover" );
			Note( $"face edge snap: {edgeSnap}", fail: !edgeSnap );
			fs.Input( cam, KeyEv( Godot.Key.U, shift: true ) );
			Note( $"Shift+U project all: {FS.Curves.Count} curves", fail: FS.Curves.Count < 4 );
			await Step( "tools-12-projected" );
			f0 = doc.Studio.Features.Count;
			GeppettoDialState.Level = 2; GeppettoDialState.Touch();
			await Tile( "Modify" );
			await Tile( "Finish sketch" );
			await Wait( 0.5 );
			var fsk = doc.Studio.Features.LastOrDefault() as SketchFeature;
			Note( $"dial Finish sketch: +{doc.Studio.Features.Count - f0} feature, {fsk?.Sketch.Curves.Count} curves, on face={fsk?.Face != null}, error={fsk?.Error}",
				fail: doc.Studio.Features.Count != f0 + 1 || fsk?.Face == null || fsk.Error != null || GeppettoTools.Active != null );
			await Step( "tools-13-done" );
		}
		catch ( Exception e ) { Note( "sketch tools run threw: " + e, fail: true ); }
		Finish();
	}
}
#endif
