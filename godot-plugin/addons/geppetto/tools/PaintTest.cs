#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Headless test of the Paint brush and Paint Weights tools (paint_test.gd). Works on a COPY of
/// g1-spike.effigy: synthetic strokes through the tools' kernel-space entry points, then checks that
/// the paint / weights landed in the document, that one undo puts them back, and that they survive
/// save + reload of the .effigy.
/// </summary>
public partial class PaintTest : RefCounted
{
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	public Godot.Collections.Dictionary Run( string fixtureRes )
	{
		string dir = Path.Combine( Path.GetTempPath(), "geppetto-paint-test-" + Guid.NewGuid().ToString( "N" )[..8] );
		Directory.CreateDirectory( dir );
		try
		{
			string src = ProjectSettings.GlobalizePath( fixtureRes );
			string copy = Path.Combine( dir, "paint.effigy" );
			File.Copy( src, copy );
			Brush( copy );
			Weights( copy );
		}
		catch ( Exception ex ) { Add( "exception", false, ex.ToString() ); }
		finally
		{
			GeppettoDocument.CloseAll();
			try { Directory.Delete( dir, true ); } catch { }
		}
		return new Godot.Collections.Dictionary { { "checks", _checks }, { "failed", _failed }, { "total", _checks.Count } };
	}

	// ---- texture paint ----------------------------------------------------------------------------

	void Brush( string path )
	{
		var doc = GeppettoDocument.Get( path );
		var studio = doc.Studio;
		var target = studio.Bodies.Where( b => b.Visible && !b.IsReference && b.Mesh is { FaceCount: > 0 } )
			.OrderByDescending( b => b.Mesh.FaceCount ).First();
		var tool = new PaintBrushTool( doc, null, null );
		PaintBrushTool.Remembered.Erasing = false;
		PaintBrushTool.Remembered.R = 1f; PaintBrushTool.Remembered.G = 0f; PaintBrushTool.Remembered.B = 0f; PaintBrushTool.Remembered.A = 1f;
		PaintBrushTool.Remembered.Strength = 1f;
		PaintBrushTool.Remembered.Radius = null;

		var (o, d, side) = AimAt( target.Mesh, target.Id, tool.BodyUnder );
		Add( "ray finds the target part", o is not null, target.Name );
		if ( o is null ) { tool.Exit(); return; }

		// First click opens the part's paint layer (adding a Paint feature, and a UV unwrap if needed).
		tool.Press( o.Value, d, ctrl: false, shift: false );
		Add( "click opens a paint session", tool.Session != null && tool.BodyId == target.Id, tool.BodyId ?? "none" );
		if ( tool.Session == null ) { tool.Exit(); return; }
		var feature = tool.Feature!;
		Add( "Paint feature in the tree, one body", studio.Features.Contains( feature ) && feature.Bodies.BodyIds.SequenceEqual( new[] { target.Id } ), feature.Name );

		int undoBefore = doc.Session.History.UndoCount;
		var paintBefore = Hash( BodyOf( studio, target.Id )?.Mesh.Paint );
		int strokesBefore = feature.Strokes?.Count ?? 0;

		Stroke( tool.Press, tool.Hover, tool.Release, o.Value, d, side, tool.Session.Radius );

		int strokesAfter = feature.Strokes?.Count ?? 0;
		Add( "stroke appended to the Paint feature", strokesAfter == strokesBefore + 1, $"{strokesBefore} -> {strokesAfter}" );
		Add( "stroke is one undo step", doc.Session.History.UndoCount == undoBefore + 1, $"{undoBefore} -> {doc.Session.History.UndoCount}" );
		var paintAfter = Hash( BodyOf( studio, target.Id )?.Mesh.Paint );
		Add( "body texture changed", paintAfter != paintBefore && paintAfter != 0, $"{paintBefore:x} -> {paintAfter:x}" );

		doc.Session.History.Undo();
		doc.Refresh();
		Add( "undo removes the stroke", (feature.Strokes?.Count ?? 0) == strokesBefore, $"{feature.Strokes?.Count ?? 0}" );
		Add( "undo restores the texture", Hash( BodyOf( studio, target.Id )?.Mesh.Paint ) == paintBefore, "" );
		Add( "brush session follows the undo", tool.Session != null && tool.Session.Strokes.Count == strokesBefore, $"{tool.Session?.Strokes.Count}" );

		doc.Session.History.Redo();
		doc.Refresh();
		Add( "redo puts it back", (feature.Strokes?.Count ?? 0) == strokesAfter && Hash( BodyOf( studio, target.Id )?.Mesh.Paint ) == paintAfter, "" );

		// Ctrl inverts to erase for one stroke, and is restored after it.
		Stroke( ( a, b, _, _ ) => tool.Press( a, b, ctrl: true, shift: false ), tool.Hover, tool.Release, o.Value, d, side, tool.Session!.Radius );
		var last = feature.Strokes?.LastOrDefault();
		Add( "Ctrl stroke erases", last != null && last.Erase && !tool.Session!.Erasing, $"{last?.Erase}" );

		tool.Exit();
		Add( "Exit restores the rollback bar", studio.RollbackIndex >= studio.Features.Count, $"{studio.RollbackIndex}" );

		var saved = doc.Bridge.SaveAll();
		Add( "save writes the .effigy", saved.Any( s => s.Outcome == EffigySaveOutcome.Written ), string.Join( ", ", saved.Select( s => $"{s.Kind}:{s.Outcome}" ) ) );
		var reread = StudioDocument.ReadFile( path );
		var rf = reread.Features.OfType<PaintFeature>().FirstOrDefault( p => p.Id == feature.Id );
		Add( "strokes survive save + reload", rf != null && (rf.Strokes?.Count ?? 0) == feature.Strokes!.Count, $"{rf?.Strokes?.Count}" );
		reread.Rebuild();
		var rb = BodyOf( reread, target.Id );
		Add( "reloaded body has the paint", rb?.Mesh.Paint != null && Hash( rb.Mesh.Paint ) == Hash( BodyOf( studio, target.Id )?.Mesh.Paint ), "" );
	}

	// ---- weight paint -----------------------------------------------------------------------------

	void Weights( string path )
	{
		GeppettoDocument.CloseAll();
		var doc = GeppettoDocument.Get( path );
		var studio = doc.Studio;
		Add( "fixture has a rig", studio.Rig.Count > 1, $"{studio.Rig.Count} bones" );
		var tool = new PaintWeightsTool( doc, null, null );
		bool opened = tool.Open( 0 );
		Add( "Paint Weights opens", opened && tool.Session != null, "" );
		if ( tool.Session == null ) { tool.Exit(); return; }
		var s = tool.Session;
		tool.SetBrush( WeightBrushKind.Add );
		tool.SetStrength( 1f );

		var (o, d, side) = AimAt( s.Mesh, "", ( a, b ) => s.Hover( a, b ) is null ? null : "" );
		if ( o is null ) { Add( "ray hits the skinned mesh", false, "" ); tool.Exit(); return; }
		var hit = s.Hover( o.Value, d )!.Value;

		// A bone that has little weight where the brush lands, so Add has something to do.
		var face = s.Mesh.Faces[hit.FaceIndex];
		int bone = Enumerable.Range( 0, s.Skeleton.Count ).OrderBy( b => face.Indices.Sum( v => s.Influence( b )[v] ) ).First();
		tool.SetBone( bone );
		float before = s.Influence( bone ).Sum();
		int undoBefore = doc.Session.History.UndoCount;
		var layerBefore = studio.WeightPaint;
		int countBefore = layerBefore?.Count ?? 0;

		Stroke( ( a, b, _, _ ) => tool.Press( a, b, false ), tool.Hover, tool.Release, o.Value, d, side, s.Radius );

		float after = tool.Session!.Influence( bone ).Sum();
		Add( "bone influence grew", after > before + 1e-3f, $"{before:0.###} -> {after:0.###}" );
		Add( "studio weight layer written", studio.WeightPaint is { Count: > 0 } && studio.WeightPaint.Count > countBefore, $"{countBefore} -> {studio.WeightPaint?.Count}" );
		Add( "weight stroke is one undo step", doc.Session.History.UndoCount == undoBefore + 1, $"{undoBefore} -> {doc.Session.History.UndoCount}" );
		int countAfter = studio.WeightPaint?.Count ?? 0;

		doc.Session.History.Undo();
		doc.Refresh();
		Add( "undo restores the weight layer", (studio.WeightPaint?.Count ?? 0) == countBefore, $"{studio.WeightPaint?.Count ?? 0}" );
		float undone = tool.Session?.Influence( bone ).Sum() ?? -1f;
		Add( "undo restores the influence", MathF.Abs( undone - before ) < 1e-3f, $"{undone:0.###}" );

		doc.Session.History.Redo();
		doc.Refresh();
		Add( "redo restores the paint", (studio.WeightPaint?.Count ?? 0) == countAfter, $"{studio.WeightPaint?.Count ?? 0}" );

		tool.Exit();
		var saved = doc.Bridge.SaveAll();
		Add( "save writes the weights", saved.Any( x => x.Outcome == EffigySaveOutcome.Written ), string.Join( ", ", saved.Select( x => $"{x.Kind}:{x.Outcome}" ) ) );
		var reread = StudioDocument.ReadFile( path );
		Add( "weights survive save + reload", reread.WeightPaint is { } wl && wl.Count == countAfter, $"{reread.WeightPaint?.Count}" );

		// Reopening on the reloaded document shows the same painted influence.
		GeppettoDocument.CloseAll();
		var doc2 = GeppettoDocument.Get( path );
		var tool2 = new PaintWeightsTool( doc2, null, null );
		tool2.Open( bone );
		float reopened = tool2.Session?.Influence( bone ).Sum() ?? -1f;
		Add( "reloaded paint applies to the binding", MathF.Abs( reopened - after ) < 1e-2f, $"{reopened:0.###} vs {after:0.###}" );
		tool2.Exit();
	}

	// ---- helpers ----------------------------------------------------------------------------------


	static void Stroke( Action<Vec3, Vec3, bool, bool> press, Func<Vec3, Vec3, MeshHit?> hover, Action release, Vec3 o, Vec3 d, Vec3 side, float radius )
	{
		hover( o, d );
		press( o, d, false, false );
		for ( int i = 1; i <= 6; i++ ) hover( o + side * (radius * 0.4f * i), d );
		release();
	}

	/// <summary>A ray onto the middle of the part's largest face, from outside along its normal, that the
	/// tool's own picking agrees lands on <paramref name="id"/>.</summary>
	static (Vec3? O, Vec3 D, Vec3 Side) AimAt( PolyMesh mesh, string id, Func<Vec3, Vec3, string?> under )
	{
		float size = MathF.Max( mesh.BoundsDiagonal, 1f );
		foreach ( var face in mesh.Faces.OrderByDescending( f => mesh.FaceArea( f ) ).Take( 40 ) )
		{
			var c = Vec3.Zero;
			foreach ( var vi in face.Indices ) c += mesh.Positions[vi];
			c /= face.Indices.Length;
			var n = mesh.FaceNormal( face ).Normal;
			var o = c + n * (size * 2f);
			if ( under( o, -n ) == id )
			{
				var refAxis = MathF.Abs( n.z ) > 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 );
				return (o, -n, Vec3.Cross( n, refAxis ).Normal);
			}
		}
		return (null, default, default);
	}

	static Body? BodyOf( PartStudio studio, string id ) => studio.Bodies.FirstOrDefault( b => b.Id == id );

	static long Hash( PaintCanvas? c )
	{
		if ( c == null ) return 0;
		long h = 1469598103934665603;
		foreach ( var b in c.Rgba ) h = (h ^ b) * 1099511628211;
		return h;
	}

	void Add( string name, bool ok, string value )
	{
		if ( !ok ) _failed++;
		_checks.Add( new Godot.Collections.Dictionary { { "name", name }, { "ok", ok }, { "value", value } } );
	}
}
#endif
