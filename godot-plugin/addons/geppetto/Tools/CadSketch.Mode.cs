#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>Sketch mode's look: the amber frame and mode bar (via <see cref="IGeppettoModeTool"/>), the
/// plane grid, and a Cancel that really throws the session away.</summary>
public sealed partial class CadSketch : IGeppettoModeTool
{
	/// <summary>What the bar calls the plane: the picked plane's name, "a face", or a datum plane's name.</summary>
	public string PlaneName { get; private set; } = "";

	public bool InMode => Plane != null || Editing != null;
	public string ModeTag => "SKETCH";
	public string ModeIcon => "Sketch";
	public Color ModeColour => GeppettoModeBar.Amber;
	public string ModeTitle => $"Sketching on {ShownPlaneName()}";

	string ShownPlaneName()
	{
		if ( PlaneName.Length > 0 ) return PlaneName;
		if ( Editing is { } f )
		{
			if ( f.PlaneFeatureId.Length > 0 ) return _doc.FeatureById( f.PlaneFeatureId )?.Name is { Length: > 0 } n ? n : "a plane";
			if ( f.Face != null ) return "a face";
			return GeppettoDatumPlanes.Origin[Math.Clamp( f.Plane.Index, 0, 2 )].Name;
		}
		return "a plane";
	}

	static readonly (string Label, string Icon, EditTool Tool)[] BarTools =
	{
		( "Line", "Line", EditTool.Line ),
		( "Rectangle", "Rectangle", EditTool.Rectangle ),
		( "Circle", "Circle", EditTool.Circle ),
		( "Arc", "Arc", EditTool.Arc ),
		( "Trim", "Trim", EditTool.Trim ),
	};

	public IReadOnlyList<GeppettoModeBar.ModeButton> ModeButtons
	{
		get
		{
			var list = new List<GeppettoModeBar.ModeButton>();
			foreach ( var (label, icon, tool) in BarTools )
				list.Add( new( label, icon, Editing != null && Tool == tool, () => PickTool( tool ), $"{label} ({KeyFor( tool )})" ) );
			list.Add( new( "Dimension", "Dimension", false, Dimension, "Dimension the selection (D)" ) );
			return list;
		}
	}

	static string KeyFor( EditTool tool )
	{
		foreach ( Key k in new[] { Key.L, Key.R, Key.C, Key.A, Key.T } )
			if ( ToolForKey( k, false ) == tool ) return k.ToString();
		return "";
	}

	void PickTool( EditTool tool )
	{
		if ( Editing == null ) BeginNew( tool );
		else SetTool( tool );
		GeppettoTools.Redraw();
	}

	void Dimension()
	{
		if ( Editing == null ) BeginNew( EditTool.Select );
		var kinds = ConstraintKeys.First( c => c.Name == "Dimension" ).Kinds;
		Constrain( "Dimension", kinds );
		GeppettoTools.Redraw();
	}

	public string ModeStep => Editing != null ? EditStep()
		: Points.Count == 0 ? "Click the first point, or pick a tool above."
		: Points.Count < 3 ? "Click the next point."
		: "Click the next point; click the first point or Finish to close.";

	public string ModeHelp => Editing != null ? EditHelp()
		: "Click points for a quick outline; click the first point or Enter closes it.\n"
		+ "L line, R rectangle, C circle, A arc, T trim: switch to a full sketch (the clicked points become lines).\n"
		+ "Backspace removes the last point. Finish adds the sketch; Cancel throws it away.";

	public void ModeDone()
	{
		if ( Editing != null ) Finish();
		else if ( Points.Count >= 3 ) Commit();
		else Cancel();
	}

	public void ModeCancel() => Cancel();

	/// <summary>Throws the session away: a new sketch is never added, an edited one goes back to how it
	/// was, and nothing lands on the undo stack.</summary>
	public void Cancel()
	{
		if ( Editing is { } f && _before != null && !_isNew )
		{
			f.Sketch = _before.Clone();
			_doc.Refresh();
		}
		Editing = null; _before = null; _isNew = false; _dirty = false;
		_pending.Clear(); _splinePoints.Clear();
		_dimension = null; _typed = "";
		Points.Clear();
		Plane = null; Face = null; PlaneIndex = -1; PlaneFeatureId = ""; PlaneName = "";
		GeppettoToolContext.Say( "Sketch: cancelled, nothing changed." );
		if ( GeppettoTools.Active == this ) GeppettoTools.Activate( null );
		else GeppettoTools.Redraw();
	}

	static readonly Color FaceGrid = new( 1f, .66f, .2f );
	static readonly Color DatumGrid = new( 0.68f, 0.55f, 0.9f );

	Color GridColour()
	{
		bool datum = PlaneFeatureId.Length > 0 || ( Editing?.PlaneFeatureId.Length ?? 0 ) > 0;
		if ( datum ) return DatumGrid;
		if ( Face != null || Editing?.Face != null ) return FaceGrid;
		int i = Editing != null ? Editing.Plane.Index : PlaneIndex;
		var c = GeppettoDatumPlanes.Origin[Math.Clamp( i, 0, 2 )].Colour;
		return new Color( c.R, c.G, c.B );
	}

	/// <summary>A fine grid on the sketch plane in the plane's colour, a stronger line every fourth, at
	/// the snap grid's own step so the dots line up with where clicks land.</summary>
	void DrawGrid( Control overlay, Camera3D cam )
	{
		if ( Plane == null ) return;
		float step = SketchSnapper.AutoGridStep( UnitsPerPixel( cam, Plane.Origin ), GridPixels );
		if ( step <= 0 || !float.IsFinite( step ) ) return;
		float half = MathF.Max( OriginHalfSize, 1f );
		while ( half / step > 80 ) step *= 4;
		int n = (int)MathF.Ceiling( half / step );
		var col = GridColour();
		var minor = new Color( col, .12f );
		var major = new Color( col, .3f );
		Vector3 W( float x, float y ) => _ray.ToWorld( Plane.ToWorld( new Vec2( x, y ) ) );
		float edge = n * step;
		for ( int i = -n; i <= n; i++ )
		{
			float v = i * step;
			var c = i % 4 == 0 ? major : minor;
			GeppettoPicker.DrawWorldLine( overlay, cam, W( v, -edge ), W( v, edge ), c, 1f );
			GeppettoPicker.DrawWorldLine( overlay, cam, W( -edge, v ), W( edge, v ), c, 1f );
		}
	}
}
#endif
