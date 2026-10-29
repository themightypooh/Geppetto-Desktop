#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Headless test for editing a sketch that is already in the tree (Tools/CadSketch.Edit.cs): draws a
/// square on a box's top face with the Sketch tool, re-opens it, chains lines (open, closed, taken
/// back), box-selects, drags a selection and a line grip, deletes, and checks the session lands as
/// one undo step. Driven by sketchedit_test.gd.
/// </summary>
#pragma warning disable IDE1006
public partial class sketchedit_test : RefCounted
#pragma warning restore IDE1006
{
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	void Add( string name, bool ok, object? value = null )
	{
		if ( !ok ) _failed++;
		_checks.Add( new Godot.Collections.Dictionary { ["name"] = name, ["ok"] = ok, ["value"] = value?.ToString() ?? "" } );
	}

	public Godot.Collections.Dictionary Run()
	{
		try { Body(); }
		catch ( Exception ex ) { Add( "exception", false, ex.ToString() ); }
		return new Godot.Collections.Dictionary { ["checks"] = _checks, ["total"] = _checks.Count, ["failed"] = _failed };
	}

	static Vector3 G( float x, float y, float z ) => EffigyMeshBridge.TransformPosition( x, y, z );
	static Vector3 GDir( float x, float y, float z ) => EffigyMeshBridge.TransformPosition( x, y, z ).Normalized();

	GeppettoDocument _doc = null!;
	bool NoErrors => _doc.LastReport is { } r && r.Errors.Count == 0;
	string Errors => _doc.LastReport is { } r ? string.Join( "; ", r.Errors.Select( e => e.Message ) ) : "no report";
	SketchFeature Sk => _doc.Studio.Features.OfType<SketchFeature>().Last();
	int Profiles => ProfileFinder.Find( Sk.Sketch ).Profiles.Count;
	int Undos => _doc.Session.History.UndoCount;

	void Body()
	{
		var studio = new PartStudio();
		var box = new PrimitiveFeature();
		box.SizeX.Value = 4; box.SizeY.Value = 4; box.SizeZ.Value = 4;
		studio.Add( box );
		string path = ProjectSettings.GlobalizePath( "user://sketchedit_box.effigy" );
		StudioDocument.WriteFile( studio, path );
		_doc = GeppettoDocument.Get( path );
		var pos = _doc.Studio.Bodies[0].Mesh.Positions;
		float top = pos.Max( p => p.z ), cx = ( pos.Min( p => p.x ) + pos.Max( p => p.x ) ) / 2, cy = ( pos.Min( p => p.y ) + pos.Max( p => p.y ) ) / 2;
		var (o, d) = ( G( cx, cy, top + 10 ), GDir( 0, 0, -1 ) );

		// --- a square on the top face, the way the Sketch tool makes one ---
		var draw = new CadSketch( _doc );
		draw.ChoosePlane( o, d );
		var c = draw.OnPlane( o, d ) ?? new Vec2( 0, 0 );
		foreach ( var q in new[] { new Vec2( -1, -1 ), new Vec2( 1, -1 ), new Vec2( 1, 1 ), new Vec2( -1, 1 ) } )
			draw.Points.Add( c + q );
		Add( "square sketch commits", draw.Commit() && NoErrors && Sk.Sketch.Curves.Count == 4 && Profiles == 1, Errors );
		var square = Sk.Sketch.Clone();
		int u0 = Undos;

		// --- re-open it ---
		var ed = new CadSketch( _doc );
		Add( "re-opens the sketch", ed.BeginEdit( Sk ) && ed.Editing == Sk && ed.Plane != null && ed.Face != null );
		Add( "finishing with no change adds no undo step", !ed.Finish() && Undos == u0 );
		ed = new CadSketch( _doc ) { Tool = CadSketch.EditTool.Line };
		ed.BeginEdit( Sk );

		// Open chain: two lines sticking out of a corner, ended with Enter.
		int corner = Sk.Sketch.Curves.OfType<SketchLine>().First().Start;
		ed.LineClick( Sk.Sketch.Points[corner], corner );
		ed.LineClick( c + new Vec2( 3, -1 ) );
		ed.LineClick( c + new Vec2( 3, 2 ) );
		Add( "chain keeps going", ed.ChainLast >= 0 && Sk.Sketch.Curves.Count == 6 );
		ed.EndChain();
		Add( "open lines leave one closed region", ed.ChainLast < 0 && Sk.Sketch.Curves.Count == 6 && Profiles == 1 && NoErrors, Errors );

		// Backspace takes back a line.
		ed.LineClick( c + new Vec2( -3, -3 ) );
		ed.LineClick( c + new Vec2( -2, -3 ) );
		Add( "Backspace takes back the chain's last line", ed.UndoChainLine() && Sk.Sketch.Curves.Count == 6 );
		ed.EndChain();

		// Closed triangle: clicking the first point closes the chain.
		var t0 = c + new Vec2( 3, 3 );
		int first = ed.LineClick( t0 );
		ed.LineClick( c + new Vec2( 5, 3 ) );
		ed.LineClick( c + new Vec2( 4, 5 ) );
		ed.LineClick( t0, first );
		Add( "clicking the first point closes the triangle", ed.ChainLast < 0 && Sk.Sketch.Curves.Count == 9 && Profiles == 2, Profiles );

		// --- Select: box around the triangle, drag it ---
		ed.Tool = CadSketch.EditTool.Select;
		bool InTri( Vec2 p ) => p.x > c.x + 2.5f && p.y > c.y + 2.5f;
		ed.SelectWhere( InTri, touching: false, add: false );
		Add( "box (inside) takes the triangle only", ed.SelectedCurves.Count == 3 && ed.SelectedPoints.Count == 3, $"{ed.SelectedCurves.Count} curves {ed.SelectedPoints.Count} points" );
		ed.SelectWhere( p => p.x > c.x + 2.5f, touching: true, add: false );
		Add( "box (touching) also takes the open lines", ed.SelectedCurves.Count == 5, ed.SelectedCurves.Count );
		ed.SelectWhere( InTri, touching: false, add: false );
		ed.MovePoint( first, new Vec2( 1, 0 ) );
		var moved = Sk.Sketch.Points[first];
		Add( "a selected point drags the whole selection", MathF.Abs( moved.x - ( t0.x + 1 ) ) < 1e-4f && Sk.Sketch.Curves.Where( k => ed.SelectedCurves.Contains( k.Id ) ).OfType<SketchLine>().All( l => Sk.Sketch.Points[l.Start].x > c.x + 3.9f ) && Profiles == 2 );

		// Line grip: drag the square's first edge outward.
		var edge = Sk.Sketch.Curves.OfType<SketchLine>().First();
		var h = SketchHandles.For( Sk.Sketch ).First( k => k.CurveId == edge.Id );
		var mid0 = h.At;
		Add( "dragging a line's grip moves the line", ed.DragHandle( h, mid0 + new Vec2( 0, -0.5f ) ) && SketchHandles.At( Sk.Sketch, edge ) is { } m && ( m - mid0 ).Length > 0.4f && NoErrors );

		// Delete the triangle.
		ed.Toggle( Sk.Sketch.Curves[^1].Id, add: false );
		ed.SelectWhere( InTri, touching: false, add: false );
		// (The dragged edge now crosses the open line, so the square is split in two; the triangle is the third region.)
		int regions = Profiles;
		int deleted = ed.DeleteSelection();
		Add( "Delete removes the selected curves", deleted == 3 && Sk.Sketch.Curves.Count == 6 && Profiles == regions - 1 && ed.SelectedCurves.Count == 0, $"{deleted} deleted, {Sk.Sketch.Curves.Count} curves, {Profiles} regions" );

		// --- the whole session is one undo step ---
		var after = Sk.Sketch.Clone();
		Add( "Finish commits one undo step", ed.Finish() && Undos == u0 + 1 && ed.Editing == null && NoErrors, $"{Undos - u0}" );
		_doc.Session.History.Undo(); _doc.Refresh();
		Add( "undo brings the square back", Sk.Sketch.Curves.Count == 4 && Sk.Sketch.Points.Take( 4 ).SequenceEqual( square.Points.Take( 4 ) ) && Profiles == 1 );
		_doc.Session.History.Redo(); _doc.Refresh();
		Add( "redo brings the edit back", Sk.Sketch.Curves.Count == 6 && Sk.Sketch.Points.SequenceEqual( after.Points ) && NoErrors );

		// Extrude still reads the edited sketch.
		var ex = new ExtrudeFeature { SketchFeatureId = Sk.Id };
		Add( "the edited sketch extrudes", _doc.AddFeature( ex ) && ex.Error == null && NoErrors, ex.Error + Errors );

		// --- Snapping and inference ---
		var snapTool = new CadSketch( _doc ) { Tool = CadSketch.EditTool.Line };
		snapTool.BeginEdit( Sk );
		int pCountBefore = Sk.Sketch.Points.Count;
		var p0 = Sk.Sketch.Points[0];
		// 1. Point snap: click near p0
		int snappedP = snapTool.LineSnapClick( p0 + new Vec2( 0.05f, 0.05f ) );
		Add( "snap to existing point", snappedP == 0 && Sk.Sketch.Points.Count == pCountBefore );

		// 2. Alignment inference: draw near horizontal line (dx=2, dy=0.01)
		int horizP = snapTool.LineSnapClick( p0 + new Vec2( 2.0f, 0.01f ) );
		var horizLine = Sk.Sketch.Curves.OfType<SketchLine>().Last();
		bool hasHoriz = Sk.Sketch.Constraints.Any( k => k.Kind == SketchConstraintKind.Horizontal && k.CurveId == horizLine.Id );
		Add( "horizontal inference adds constraint", hasHoriz && MathF.Abs( Sk.Sketch.Points[horizP].y - p0.y ) < 1e-4f );

		// 3. Line snap: click near an existing line (the square's first edge)
		snapTool.EndChain();
		var line1 = Sk.Sketch.Curves.OfType<SketchLine>().First();
		var midLine = ( Sk.Sketch.Points[line1.Start] + Sk.Sketch.Points[line1.End] ) * 0.5f;
		int lineSnapP = snapTool.LineSnapClick( midLine + new Vec2( 0.02f, 0.02f ) );
		bool hasPointOnLine = Sk.Sketch.Constraints.Any( k => k.Kind == SketchConstraintKind.PointOnLine && k.PointA == lineSnapP );
		Add( "snap to line adds PointOnLine constraint", hasPointOnLine );
		snapTool.EndChain();
		snapTool.Finish();

		// --- Step 3 Sketch Tools ---
		var tDoc = new CadSketch( _doc );
		tDoc.BeginEdit( Sk );

		// 1. Midpoint line: centre at (10, 10), end at (12, 10) -> far end is (8, 10)
		tDoc.Tool = CadSketch.EditTool.LineMidpoint;
		tDoc.ClickTool( new Vec2( 10, 10 ) );
		tDoc.ClickTool( new Vec2( 12, 10 ) );
		var ml = Sk.Sketch.Curves.OfType<SketchLine>().Last();
		var pA = Sk.Sketch.Points[ml.Start];
		var pB = Sk.Sketch.Points[ml.End];
		bool mlOk = ( MathF.Abs( pA.x - 8f ) < 1e-4f && MathF.Abs( pB.x - 12f ) < 1e-4f )
		         || ( MathF.Abs( pA.x - 12f ) < 1e-4f && MathF.Abs( pB.x - 8f ) < 1e-4f );
		Add( "midpoint line creates symmetric line", mlOk );

		// 2. Corner rectangle: (20, 20) to (24, 23)
		tDoc.Tool = CadSketch.EditTool.Rectangle;
		int curvesBefore = Sk.Sketch.Curves.Count;
		tDoc.ClickTool( new Vec2( 20, 20 ) );
		tDoc.ClickTool( new Vec2( 24, 23 ) );
		Add( "corner rectangle adds 4 lines with H/V constraints", Sk.Sketch.Curves.Count == curvesBefore + 4 && Sk.Sketch.Constraints.Count( c => c.Kind == SketchConstraintKind.Horizontal || c.Kind == SketchConstraintKind.Vertical ) >= 4 );

		// 3. Centre rectangle: centre (30, 30), corner (32, 33) -> width 4, height 6
		tDoc.Tool = CadSketch.EditTool.RectangleCentre;
		curvesBefore = Sk.Sketch.Curves.Count;
		tDoc.ClickTool( new Vec2( 30, 30 ) );
		tDoc.ClickTool( new Vec2( 32, 33 ) );
		Add( "centre rectangle adds 4 lines", Sk.Sketch.Curves.Count == curvesBefore + 4 );

		// 4. Centre circle: centre (40, 40), rim (43, 40) -> radius 3
		tDoc.Tool = CadSketch.EditTool.Circle;
		tDoc.ClickTool( new Vec2( 40, 40 ) );
		tDoc.ClickTool( new Vec2( 43, 40 ) );
		var circle = Sk.Sketch.Curves.OfType<SketchCircle>().Last();
		Add( "centre circle created with radius 3", MathF.Abs( circle.Radius - 3f ) < 1e-4f );

		// 5. 3-point circle: (50, 50), (52, 52), (54, 50)
		tDoc.Tool = CadSketch.EditTool.CircleThreePoint;
		tDoc.ClickTool( new Vec2( 50, 50 ) );
		tDoc.ClickTool( new Vec2( 52, 52 ) );
		tDoc.ClickTool( new Vec2( 54, 50 ) );
		var c3p = Sk.Sketch.Curves.OfType<SketchCircle>().Last();
		var ccCenter = Sk.Sketch.Points[c3p.Center];
		Add( "3-point circle passes through points", MathF.Abs( ccCenter.x - 52f ) < 1e-3f && MathF.Abs( ccCenter.y - 50f ) < 1e-3f );

		// 6. Centre arc: centre (60, 60), start (62, 60), end (60, 62)
		tDoc.Tool = CadSketch.EditTool.Arc;
		tDoc.ClickTool( new Vec2( 60, 60 ) );
		tDoc.ClickTool( new Vec2( 62, 60 ) );
		tDoc.ClickTool( new Vec2( 60, 62 ) );
		var arc1 = Sk.Sketch.Curves.OfType<SketchArc>().Last();
		Add( "centre arc created", arc1.Center >= 0 && arc1.Start >= 0 && arc1.End >= 0 );

		// 7. 3-point arc: start (70, 70), end (74, 70), through (72, 72)
		tDoc.Tool = CadSketch.EditTool.ArcThreePoint;
		tDoc.ClickTool( new Vec2( 70, 70 ) );
		tDoc.ClickTool( new Vec2( 74, 70 ) );
		tDoc.ClickTool( new Vec2( 72, 72 ) );
		var arc2 = Sk.Sketch.Curves.OfType<SketchArc>().Last();
		Add( "3-point arc created", arc2.Center >= 0 );

		// 8. Point: (80, 80)
		tDoc.Tool = CadSketch.EditTool.Point;
		int ptsBefore = Sk.Sketch.Points.Count;
		tDoc.ClickTool( new Vec2( 80, 80 ) );
		Add( "point placed", Sk.Sketch.Points.Count == ptsBefore + 1 && ( Sk.Sketch.Points[^1] - new Vec2( 80, 80 ) ).Length < 1e-4f );

		// 9. Polygon (inscribed): 5 sides
		tDoc.Tool = CadSketch.EditTool.Polygon;
		tDoc.PolygonSides = 5;
		curvesBefore = Sk.Sketch.Curves.Count;
		tDoc.ClickTool( new Vec2( 90, 90 ) );
		tDoc.ClickTool( new Vec2( 93, 90 ) );
		Add( "inscribed polygon adds 5 lines", Sk.Sketch.Curves.Count == curvesBefore + 5 );

		// 10. Slot: (100, 100) to (105, 100), width point (102, 101)
		tDoc.Tool = CadSketch.EditTool.Slot;
		curvesBefore = Sk.Sketch.Curves.Count;
		tDoc.ClickTool( new Vec2( 100, 100 ) );
		tDoc.ClickTool( new Vec2( 105, 100 ) );
		tDoc.ClickTool( new Vec2( 102, 101 ) );
		Add( "slot adds 2 lines and 2 arcs", Sk.Sketch.Curves.Count == curvesBefore + 4 );

		// 11. Ellipse: centre (110, 110), major (114, 110), minor pt (110, 112)
		tDoc.Tool = CadSketch.EditTool.Ellipse;
		tDoc.ClickTool( new Vec2( 110, 110 ) );
		tDoc.ClickTool( new Vec2( 114, 110 ) );
		tDoc.ClickTool( new Vec2( 110, 112 ) );
		var el = Sk.Sketch.Curves.OfType<SketchEllipse>().Last();
		Add( "ellipse created with minor radius 2", MathF.Abs( el.MinorRadius - 2f ) < 1e-4f );

		// 12. Spline: 3 points
		tDoc.Tool = CadSketch.EditTool.Spline;
		tDoc.ClickTool( new Vec2( 120, 120 ) );
		tDoc.ClickTool( new Vec2( 122, 123 ) );
		tDoc.ClickTool( new Vec2( 125, 121 ) );
		tDoc.FinishSpline();
		var sp = Sk.Sketch.Curves.OfType<SketchSpline>().Last();
		Add( "spline created with 3 points", sp.Points.Count == 3 );

		// 13. Fillet: on rectangle corner
		tDoc.Tool = CadSketch.EditTool.Rectangle;
		tDoc.ClickTool( new Vec2( 130, 130 ) );
		tDoc.ClickTool( new Vec2( 135, 135 ) );
		tDoc.Tool = CadSketch.EditTool.Fillet;
		tDoc.ClickTool( new Vec2( 130, 130 ) );
		tDoc.ClickTool( new Vec2( 131, 130 ) );
		Add( "fillet rounds corner into arc", Sk.Sketch.Curves.OfType<SketchArc>().Any( a => ( Sk.Sketch.Points[a.Center] - new Vec2( 131, 131 ) ).Length < 1e-2f ) );

		// 14. Cut: drag line across an existing line (standalone line is trimmed/removed)
		tDoc.Tool = CadSketch.EditTool.Line;
		tDoc.ClickTool( new Vec2( 140, 140 ) );
		tDoc.ClickTool( new Vec2( 140, 146 ) );
		tDoc.EndChain();
		curvesBefore = Sk.Sketch.Curves.Count;
		int cuts = SketchCut.Cut( Sk.Sketch, new Vec2( 138, 143 ), new Vec2( 142, 143 ) );
		Add( "cut trims standalone line", cuts == 1 && Sk.Sketch.Curves.Count == curvesBefore - 1 );

		// 15. Construction mode: toggle Q, draw line
		tDoc.ConstructionMode = true;
		tDoc.Tool = CadSketch.EditTool.Line;
		tDoc.ClickTool( new Vec2( 150, 150 ) );
		tDoc.ClickTool( new Vec2( 155, 150 ) );
		tDoc.EndChain();
		var qLine = Sk.Sketch.Curves.OfType<SketchLine>().Last();
		Add( "construction line is flagged construction", qLine.Construction );
		tDoc.ConstructionMode = false;

		// 16. ProjectAll from host face reference
		int projected = tDoc.ProjectAll();
		Add( "project all copies host face edges", projected > 0 && tDoc.Reference != null );

		tDoc.Finish();

		// --- dial: every CAD > Sketch group tile runs something, and every tool has a hotkey ---
		var named = GeppettoTools.SketchTiles.Select( t => t.Tile ).Concat( new[] { "Construction", "Project", "Finish sketch" } ).ToHashSet();
		var cadWs = GeppettoDialData.Workspace( "CAD" );
		var groupTools = new[] { "Draw", "Shapes", "Modify" }.SelectMany( g => GeppettoDialData.Group( "CAD", g ) ?? Array.Empty<string>() ).ToList();
		Add( "dial: Draw/Shapes/Modify hold 24 sketch tiles, all wired", groupTools.Count == 24 && groupTools.All( named.Contains ) && groupTools.All( t => GeppettoDialData.HasTool( cadWs, t ) ), groupTools.Count );
		var keyed = new[] { Key.S, Key.L, Key.M, Key.R, Key.C, Key.A, Key.P, Key.G, Key.W, Key.E, Key.N, Key.T, Key.X, Key.F, Key.O, Key.U, Key.Z }
			.SelectMany( k => new[] { CadSketch.ToolForKey( k, false ), CadSketch.ToolForKey( k, true ) } ).Where( t => t != null ).Distinct().Count();
		Add( "hotkeys reach 22 distinct sketch tools", keyed == 22, keyed );

		// --- constraints and dimensions on the selection (hotkey path, no panel) ---
		var sq = new CadSketch( _doc );
		sq.ChoosePlane( o, d );
		foreach ( var q in new[] { new Vec2( -1, -1 ), new Vec2( 2, -1 ), new Vec2( 2, 1 ), new Vec2( -1, 1 ) } )
			sq.Points.Add( c + q );
		sq.Commit();
		var ce = new CadSketch( _doc ) { Tool = CadSketch.EditTool.Select };
		ce.BeginEdit( Sk );
		var lines = Sk.Sketch.Curves.OfType<SketchLine>().ToList();
		float Len( SketchLine l ) => ( Sk.Sketch.Points[l.Start] - Sk.Sketch.Points[l.End] ).Length;
		bool Has( SketchConstraintKind k ) => Sk.Sketch.Constraints.Any( x => x.Kind == k );
		ce.Toggle( lines[0].Id, add: false );
		Add( "H puts Horizontal on a line", ce.Constrain( "Horizontal", SketchConstraintKind.Horizontal ) && Has( SketchConstraintKind.Horizontal ) );
		ce.Toggle( lines[0].Id, add: false ); ce.Toggle( lines[2].Id, add: true );
		Add( "B puts Parallel on two lines", ce.Constrain( "Parallel", SketchConstraintKind.Parallel ) && Has( SketchConstraintKind.Parallel ) );
		ce.Toggle( lines[1].Id, add: false ); ce.Toggle( lines[3].Id, add: true );
		Add( "Shift+E puts Equal on two lines", ce.Constrain( "Equal", SketchConstraintKind.EqualLength ) && Has( SketchConstraintKind.EqualLength ) );
		ce.Toggle( lines[0].Id, add: false );
		var dimKinds = CadSketch.ConstraintKeys.First( k => k.Key == Key.D ).Kinds;
		Add( "D dimensions a line at its length", ce.Constrain( "Dimension", dimKinds ) && Has( SketchConstraintKind.Distance ) );
		Add( "typed 2.5 sets the line to 2.5", ce.SetDimension( 2.5f ) && MathF.Abs( Len( lines[0] ) - 2.5f ) < 1e-3f, Len( lines[0] ) );
		Add( "a value of 0 or less is refused, length kept", !ce.SetDimension( -1f ) && MathF.Abs( Len( lines[0] ) - 2.5f ) < 1e-3f );
		int before = Sk.Sketch.Constraints.Count;
		var par = Sk.Sketch.Constraints.First( x => x.Kind == SketchConstraintKind.Parallel );
		Add( "removing a constraint takes only that one", ce.RemoveConstraint( par ) && Sk.Sketch.Constraints.Count == before - 1 && !Has( SketchConstraintKind.Parallel ) );
		ce.Toggle( lines[0].Id, add: false );
		Add( "Shift+Delete clears the selection's constraints", ce.RemoveConstraintsOnSelection() > 0 && !Has( SketchConstraintKind.Distance ) );
		ce.Finish();

		// --- Cancel (the mode bar's): an edit goes back exactly, a new sketch is never added, no undo step ---
		int curves = Sk.Sketch.Curves.Count, points = Sk.Sketch.Points.Count, cons = Sk.Sketch.Constraints.Count, feats = _doc.Studio.Features.Count, u1 = Undos;
		var cx2 = new CadSketch( _doc ) { Tool = CadSketch.EditTool.Line };
		cx2.BeginEdit( Sk );
		Add( "in sketch mode while editing", cx2.InMode && cx2.ModeTitle.StartsWith( "Sketching on " ) );
		cx2.LineClick( c + new Vec2( -4, 4 ) );
		cx2.LineClick( c + new Vec2( -5, 5 ) );
		cx2.EndChain();
		bool grew = Sk.Sketch.Curves.Count > curves;
		cx2.Cancel();
		Add( "Cancel puts an edited sketch back as it was", grew && Sk.Sketch.Curves.Count == curves && Sk.Sketch.Points.Count == points && Sk.Sketch.Constraints.Count == cons && cx2.Editing == null && !cx2.InMode,
			$"{Sk.Sketch.Curves.Count} vs {curves}" );
		Add( "Cancel adds no undo step or feature", Undos == u1 && _doc.Studio.Features.Count == feats && NoErrors, Errors );
		var fresh = new CadSketch( _doc );
		fresh.ChoosePlane( o, d );
		fresh.BeginNew( CadSketch.EditTool.Rectangle );
		fresh.ClickTool( c + new Vec2( 6, 6 ) );
		fresh.ClickTool( c + new Vec2( 8, 8 ) );
		bool drew = fresh.Editing?.Sketch.Curves.Count == 4;
		fresh.Cancel();
		Add( "Cancel drops a new sketch", drew && _doc.Studio.Features.Count == feats && Undos == u1 && !fresh.InMode );
		var quick = new CadSketch( _doc );
		quick.ChoosePlane( o, d );
		foreach ( var q in new[] { new Vec2( 6, 6 ), new Vec2( 8, 6 ), new Vec2( 8, 8 ) } ) quick.Points.Add( c + q );
		quick.ModeDone();
		Add( "Finish on a quick outline adds it", _doc.Studio.Features.Count == feats + 1 && Sk.Sketch.Curves.Count == 3 );

		// --- a script sketch is not opened (its Curves text rebuilds it) ---
		var script = new SketchFeature();
		script.Curves.Value = "rect 0 0 1 1";
		Add( "a script sketch is refused", !new CadSketch( _doc ).BeginEdit( script ) );
	}
}
#endif
