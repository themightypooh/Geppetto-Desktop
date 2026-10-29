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
/// Editing a sketch that is already in the tree (double-click it in Features, or Edit sketch in its menu).
/// Parity with s&amp;box sketcher: Select, Line, Midpoint line, Corner rectangle, Centre rectangle,
/// Centre circle, 3-point circle, Centre arc, 3-point arc, Point, Inscribed/circumscribed polygon,
/// Slot, Ellipse, Spline, Trim, Extend, Fillet, Offset, Mirror, Use (Project), and Cut.
/// </summary>
public sealed partial class CadSketch
{
	public enum EditTool
	{
		Select,
		Line,
		LineMidpoint,
		Rectangle,
		RectangleCentre,
		Circle,
		CircleThreePoint,
		Arc,
		ArcThreePoint,
		Point,
		Polygon,
		PolygonCircumscribed,
		Slot,
		Ellipse,
		Spline,
		Trim,
		Extend,
		Fillet,
		Offset,
		Mirror,
		Use,
		Cut,
	}

	/// <summary>The sketch feature being edited, or null for a new sketch.</summary>
	public SketchFeature? Editing { get; private set; }
	public EditTool Tool { get; set; } = EditTool.Select;
	public HashSet<int> SelectedPoints { get; } = new();
	public HashSet<string> SelectedCurves { get; } = new();

	/// <summary>The point the line chain continues from, or -1 when no chain is open.</summary>
	public int ChainLast { get; private set; } = -1;
	int _chainFirst = -1;
	readonly List<SketchLine> _chain = new();

	// In-progress multi-click tool state.
	readonly List<Vec2> _pending = new();
	readonly List<Vec2> _splinePoints = new();
	int _filletCorner = -1;
	string? _offsetCurveId;
	Vec2? _cutFrom;

	public int PolygonSides { get; set; } = 6;
	public bool ConstructionMode { get; set; }

	string? _editingId;
	Sketch? _before;
	bool _dirty;

	// Mouse gestures in Select.
	int _dragPoint = -1;
	CurveHandle? _dragHandle;
	Vec2 _dragFrom;
	Vector2? _boxFrom;

	Sketch S => Editing!.Sketch;

	/// <summary>A sketch written by script (its Curves text) is rebuilt from that text on every build,
	/// so editing its points would be thrown away.</summary>
	public static bool CanEdit( SketchFeature f ) => string.IsNullOrWhiteSpace( f.Curves.Value );

	public bool StartEdit( GeppettoCharacter character, SketchFeature feature )
	{
		if ( !BeginEdit( feature ) ) return false;
		_ray.BodyTransform = GeppettoPicker.BodyTransformOf( character.Root );
		GeppettoTools.Activate( this );
		return true;
	}

	/// <summary>Opens <paramref name="feature"/> for editing without a viewport (the tests use this).</summary>
	public bool BeginEdit( SketchFeature feature )
	{
		if ( !CanEdit( feature ) )
		{
			GeppettoToolContext.Say( "Sketch: this sketch is written as script curves; edit its Curves text instead." );
			return false;
		}
		Editing = feature;
		_editingId = feature.Id;
		_before = feature.Sketch.Clone();
		Plane = feature.Sketch.Plane;
		Face = feature.Face;
		UpdateReference();
		_pending.Clear();
		_splinePoints.Clear();
		_dirty = false;
		return true;
	}

	void Changed()
	{
		_dirty = true;
		_doc.Refresh();
		GeppettoTools.Redraw();
	}

	/// <summary>Puts the session into the tree as one undo step (nothing when it changed nothing).</summary>
	public bool Finish()
	{
		if ( Editing == null || _before == null ) return false;
		EndChain();
		_pending.Clear();
		_splinePoints.Clear();
		var feature = Editing;
		var before = _before;
		var after = feature.Sketch.Clone();
		var id = _editingId!;
		Editing = null; _before = null;
		if ( GeppettoTools.Active == this ) GeppettoTools.Activate( null );
		if ( !_dirty ) return false;

		// Back to where it started, so the command's own Execute is what makes the change.
		feature.Sketch = before.Clone();
		void Set( Sketch s ) { if ( _doc.FeatureById( id ) is SketchFeature f ) f.Sketch = s.Clone(); }
		bool ok = _doc.Execute( new DelegateEditCommand( "Edit sketch", () => Set( after ), () => Set( before ) ), "Geppetto: edit sketch" );
		if ( ok ) GeppettoToolContext.Say( $"Sketch: saved ({after.Curves.Count} curves). Ctrl+Z undoes the whole edit." );
		return ok;
	}

	// =========================================================================================
	// Helpers & Point/Curve Tracking
	// =========================================================================================

	public int PointIndex( Vec2 p ) => SketchSnapper.PointIndex( S, p );

	public T Track<T>( T curve ) where T : SketchCurve
	{
		curve.Construction = ConstructionMode;
		return S.Add( curve );
	}

	public static Vec2[] RectangleCorners( Vec2 min, Vec2 max ) => new[]
	{
		new Vec2( min.x, min.y ),
		new Vec2( max.x, min.y ),
		new Vec2( max.x, max.y ),
		new Vec2( min.x, max.y ),
	};

	public static Vec2[] RegularPolygon( Vec2 centre, Vec2 vertex, int sides, bool circumscribed = false )
	{
		sides = Math.Clamp( sides, 3, 64 );
		var spoke = vertex - centre;
		float radius = spoke.Length;
		float start = MathF.Atan2( spoke.y, spoke.x );
		if ( circumscribed )
		{
			radius /= MathF.Cos( MathF.PI / sides );
			start -= MathF.PI / sides;
		}
		var corners = new Vec2[sides];
		for ( int i = 0; i < sides; i++ )
		{
			float a = start + i * MathF.Tau / sides;
			corners[i] = new Vec2( centre.x + MathF.Cos( a ) * radius, centre.y + MathF.Sin( a ) * radius );
		}
		return corners;
	}

	public static Vec2? Circumcentre( Vec2 a, Vec2 b, Vec2 c )
	{
		float d = 2f * ( a.x * ( b.y - c.y ) + b.x * ( c.y - a.y ) + c.x * ( a.y - b.y ) );
		if ( MathF.Abs( d ) < 1e-7f ) return null;
		float a2 = a.x * a.x + a.y * a.y;
		float b2 = b.x * b.x + b.y * b.y;
		float c2 = c.x * c.x + c.y * c.y;
		return new Vec2(
			( a2 * ( b.y - c.y ) + b2 * ( c.y - a.y ) + c2 * ( a.y - b.y ) ) / d,
			( a2 * ( c.x - b.x ) + b2 * ( a.x - c.x ) + c2 * ( b.x - a.x ) ) / d );
	}

	public static float NormAngle( float a )
	{
		while ( a < 0f ) a += MathF.Tau;
		while ( a >= MathF.Tau ) a -= MathF.Tau;
		return a;
	}

	public static float PerpendicularDistance( Vec2 a, Vec2 b, Vec2 p )
	{
		var ab = b - a;
		float len = ab.Length;
		if ( len < 1e-6f ) return ( p - a ).Length;
		return MathF.Abs( ( p.x - a.x ) * ab.y - ( p.y - a.y ) * ab.x ) / len;
	}

	float SignedOffset( SketchCurve curve, Vec2 p )
	{
		var points = curve.Tessellate( S, S.Tolerance );
		if ( points.Count < 2 ) return 0f;
		float best = float.MaxValue;
		float sign = 1f;
		for ( int i = 0; i < points.Count - 1; i++ )
		{
			var a = points[i];
			var b = points[i + 1];
			var along = b - a;
			float lenSq = along.LengthSquared;
			if ( lenSq < 1e-12f ) continue;
			float t = Math.Clamp( ( ( p - a ).x * along.x + ( p - a ).y * along.y ) / lenSq, 0f, 1f );
			var closest = new Vec2( a.x + along.x * t, a.y + along.y * t );
			float dist = ( p - closest ).Length;
			if ( dist >= best ) continue;
			best = dist;
			sign = along.x * ( p.y - a.y ) - along.y * ( p.x - a.x ) < 0f ? -1f : 1f;
		}
		return best == float.MaxValue ? 0f : best * sign;
	}

	SketchCurve? CurveUnder( Vec2 p )
	{
		float bestD = 0.5f;
		SketchCurve? best = null;
		foreach ( var c in S.Curves )
		{
			if ( SketchIntersect.OnCurve( S, c, p, out _ ) ) return c;
			var pts = c.Tessellate( S, S.Tolerance );
			for ( int i = 0; i + 1 < pts.Count; i++ )
			{
				float d = Geometry2D.GetClosestPointToSegment( new Vector2( p.x, p.y ), new Vector2( pts[i].x, pts[i].y ), new Vector2( pts[i + 1].x, pts[i + 1].y ) ).DistanceTo( new Vector2( p.x, p.y ) );
				if ( d < bestD ) { bestD = d; best = c; }
			}
		}
		return best;
	}

	int NearestPoint( Vec2 p, float maxDist = 0.6f )
	{
		int best = -1; float bestD = maxDist;
		for ( int i = 0; i < S.Points.Count; i++ )
		{
			float d = ( S.Points[i] - p ).Length;
			if ( d < bestD ) { bestD = d; best = i; }
		}
		return best;
	}

	// =========================================================================================
	// Line (chains)
	// =========================================================================================

	public int LineClick( Vec2 p, int existing = -1, SnapResult? snapOpt = null )
	{
		if ( Editing == null ) return -1;
		int idx = existing >= 0 ? existing : PointIndex( p );
		var snap = snapOpt ?? default;
		bool hasSnap = snapOpt.HasValue;
		if ( ChainLast < 0 )
		{
			ChainLast = _chainFirst = idx;
			if ( hasSnap && snap.SnappedCurveIndex >= 0 && snap.SnappedCurveIndex < S.Curves.Count && S.Curves[snap.SnappedCurveIndex] is SketchLine targetLine )
			{
				S.Constraints.Add( new SketchConstraint( SketchConstraintKind.PointOnLine, idx, targetLine.Start, targetLine.End ) );
			}
			GeppettoTools.Redraw();
			return idx;
		}
		if ( idx == ChainLast ) return idx;
		var line = Track( new SketchLine( ChainLast, idx ) );
		_chain.Add( line );
		if ( hasSnap && snap.InferenceAxis != 0 )
		{
			SketchConstraintKind? kind = ( snap.InferenceAxis & 1 ) != 0 ? SketchConstraintKind.Vertical
				: ( snap.InferenceAxis & 2 ) != 0 ? SketchConstraintKind.Horizontal
				: null;
			if ( kind is { } k ) S.AddConstraint( line, k );
		}
		if ( hasSnap && snap.SnappedCurveIndex >= 0 && snap.SnappedCurveIndex < S.Curves.Count && S.Curves[snap.SnappedCurveIndex] is SketchLine endLine )
		{
			S.Constraints.Add( new SketchConstraint( SketchConstraintKind.PointOnLine, idx, endLine.Start, endLine.End ) );
		}
		if ( hasSnap && snap.SnappedPointIndex >= 0 && existing < 0 && idx != snap.SnappedPointIndex )
		{
			S.AddConstraint( SketchConstraintKind.Coincident, idx, snap.SnappedPointIndex );
		}
		if ( idx == _chainFirst ) { ChainLast = _chainFirst = -1; _chain.Clear(); }
		else ChainLast = idx;
		Changed();
		return ChainLast;
	}

	public int LineSnapClick( Vec2 raw )
	{
		var snap = Snap( raw, isLine: ChainLast >= 0 );
		int existing = snap.SnappedPointIndex;
		return LineClick( snap.Point, existing, snap );
	}

	public void EndChain()
	{
		ChainLast = _chainFirst = -1;
		_chain.Clear();
		GeppettoTools.Redraw();
	}

	public bool UndoChainLine()
	{
		if ( Editing == null || _chain.Count == 0 ) { EndChain(); return false; }
		var last = _chain[^1];
		_chain.RemoveAt( _chain.Count - 1 );
		S.Curves.Remove( last );
		ChainLast = last.Start;
		if ( _chain.Count == 0 && ChainLast != _chainFirst ) _chainFirst = ChainLast;
		Changed();
		return true;
	}

	// =========================================================================================
	// Multi-Tool Click Dispatch
	// =========================================================================================

	public bool ClickTool( Vec2 p, SnapResult? snapOpt = null )
	{
		if ( Editing == null ) return false;
		if ( Tool == EditTool.Line )
		{
			int existing = snapOpt?.SnappedPointIndex ?? -1;
			LineClick( p, existing, snapOpt );
			return true;
		}
		_pending.Add( p );
		var snap = snapOpt ?? default;
		bool hasSnap = snapOpt.HasValue;
		string? refusal = null;

		switch ( Tool )
		{
			case EditTool.Point:
			{
				int beforeCount = S.Points.Count;
				PointIndex( p );
				_pending.Clear();
				if ( S.Points.Count == beforeCount ) refusal = "There's already a point there. Click somewhere empty to place one.";
				else Changed();
				break;
			}

			case EditTool.LineMidpoint when _pending.Count == 2:
			{
				var centre = _pending[0];
				var near = _pending[1];
				var far = centre + ( centre - near );
				if ( ( far - near ).Length <= 1e-4f )
				{
					_pending.RemoveAt( 1 );
					refusal = "That click landed on the middle, so the line would have no length. Click away from it.";
					break;
				}
				var midLine = Track( new SketchLine( PointIndex( far ), PointIndex( near ) ) );
				if ( snap.InferenceAxis != 0 )
				{
					SketchConstraintKind? kind = ( snap.InferenceAxis & 1 ) != 0 ? SketchConstraintKind.Vertical
						: ( snap.InferenceAxis & 2 ) != 0 ? SketchConstraintKind.Horizontal : null;
					if ( kind is { } k ) S.AddConstraint( midLine, k );
				}
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.Rectangle when _pending.Count == 2:
			{
				var min = new Vec2( MathF.Min( _pending[0].x, _pending[1].x ), MathF.Min( _pending[0].y, _pending[1].y ) );
				var max = new Vec2( MathF.Max( _pending[0].x, _pending[1].x ), MathF.Max( _pending[0].y, _pending[1].y ) );
				if ( max.x - min.x <= 1e-4f || max.y - min.y <= 1e-4f )
				{
					_pending.RemoveAt( 1 );
					refusal = "That corner is level with the first, so the rectangle would have no width or height.";
					break;
				}
				var corners = RectangleCorners( min, max );
				var idx = corners.Select( PointIndex ).ToArray();
				for ( int i = 0; i < 4; i++ )
				{
					var l = Track( new SketchLine( idx[i], idx[( i + 1 ) % 4] ) );
					S.AddConstraint( l, i % 2 == 0 ? SketchConstraintKind.Horizontal : SketchConstraintKind.Vertical );
				}
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.RectangleCentre when _pending.Count == 2:
			{
				var span = _pending[1] - _pending[0];
				var cmin = new Vec2( _pending[0].x - MathF.Abs( span.x ), _pending[0].y - MathF.Abs( span.y ) );
				var cmax = new Vec2( _pending[0].x + MathF.Abs( span.x ), _pending[0].y + MathF.Abs( span.y ) );
				if ( cmax.x - cmin.x <= 1e-4f || cmax.y - cmin.y <= 1e-4f )
				{
					_pending.RemoveAt( 1 );
					refusal = "That corner is level with the centre, so the rectangle would have no width or height.";
					break;
				}
				var corners = RectangleCorners( cmin, cmax );
				var idx = corners.Select( PointIndex ).ToArray();
				for ( int i = 0; i < 4; i++ )
				{
					var l = Track( new SketchLine( idx[i], idx[( i + 1 ) % 4] ) );
					S.AddConstraint( l, i % 2 == 0 ? SketchConstraintKind.Horizontal : SketchConstraintKind.Vertical );
				}
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.Circle when _pending.Count == 2:
			{
				float radius = ( _pending[1] - _pending[0] ).Length;
				if ( radius <= 1e-4f )
				{
					_pending.RemoveAt( 1 );
					refusal = "That click is on the centre, so the circle would have no radius. Click away from it.";
					break;
				}
				Track( new SketchCircle( PointIndex( _pending[0] ), radius ) );
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.CircleThreePoint when _pending.Count == 3:
			{
				if ( Circumcentre( _pending[0], _pending[1], _pending[2] ) is not { } cc )
				{
					_pending.RemoveAt( 2 );
					refusal = "Those three points are in a straight line, so no circle passes through them.";
					break;
				}
				Track( new SketchCircle( PointIndex( cc ), ( cc - _pending[0] ).Length ) );
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.Arc when _pending.Count == 2 && ( _pending[1] - _pending[0] ).Length <= 1e-4f:
			{
				_pending.RemoveAt( 1 );
				refusal = "That click is on the centre, so the arc would have no radius.";
				break;
			}

			case EditTool.Arc when _pending.Count == 3:
			{
				var centre = _pending[0];
				var start = _pending[1];
				float radius = ( start - centre ).Length;
				var toEnd = _pending[2] - centre;
				if ( toEnd.Length <= 1e-4f )
				{
					_pending.RemoveAt( 2 );
					refusal = "That click gives the arc no direction to end in.";
					break;
				}
				var end = centre + toEnd.Normal * radius;
				float cross = ( start.x - centre.x ) * ( end.y - centre.y ) - ( start.y - centre.y ) * ( end.x - centre.x );
				Track( new SketchArc( PointIndex( centre ), PointIndex( start ), PointIndex( end ), cross < 0f ) );
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.ArcThreePoint when _pending.Count == 2 && ( _pending[1] - _pending[0] ).Length <= 1e-4f:
			{
				_pending.RemoveAt( 1 );
				refusal = "An arc's two ends can't be the same point.";
				break;
			}

			case EditTool.ArcThreePoint when _pending.Count == 3:
			{
				var start = _pending[0];
				var end = _pending[1];
				var through = _pending[2];
				if ( Circumcentre( start, through, end ) is not { } cc )
				{
					_pending.RemoveAt( 2 );
					refusal = "That point is on the straight line between the ends, so the arc would be flat.";
					break;
				}
				float a0 = MathF.Atan2( start.y - cc.y, start.x - cc.x );
				float toEnd = NormAngle( MathF.Atan2( end.y - cc.y, end.x - cc.x ) - a0 );
				float toThrough = NormAngle( MathF.Atan2( through.y - cc.y, through.x - cc.x ) - a0 );
				Track( new SketchArc( PointIndex( cc ), PointIndex( start ), PointIndex( end ), toThrough > toEnd ) );
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.Polygon when _pending.Count == 2:
			{
				if ( ( _pending[1] - _pending[0] ).Length <= 1e-4f )
				{
					_pending.RemoveAt( 1 );
					refusal = "That click is on the centre, so the polygon would have no size.";
					break;
				}
				var corners = RegularPolygon( _pending[0], _pending[1], PolygonSides, circumscribed: false );
				var idx = corners.Select( PointIndex ).ToArray();
				for ( int i = 0; i < idx.Length; i++ )
					Track( new SketchLine( idx[i], idx[( i + 1 ) % idx.Length] ) );
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.PolygonCircumscribed when _pending.Count == 2:
			{
				if ( ( _pending[1] - _pending[0] ).Length <= 1e-4f )
				{
					_pending.RemoveAt( 1 );
					refusal = "That click is on the centre, so the polygon would have no size.";
					break;
				}
				var corners = RegularPolygon( _pending[0], _pending[1], PolygonSides, circumscribed: true );
				var idx = corners.Select( PointIndex ).ToArray();
				for ( int i = 0; i < idx.Length; i++ )
					Track( new SketchLine( idx[i], idx[( i + 1 ) % idx.Length] ) );
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.Slot when _pending.Count == 2 && ( _pending[1] - _pending[0] ).Length <= 1e-4f:
			{
				_pending.RemoveAt( 1 );
				refusal = "The slot's two ends can't be the same point.";
				break;
			}

			case EditTool.Slot when _pending.Count == 3:
			{
				var a = _pending[0];
				var b = _pending[1];
				var axis = b - a;
				var dir = axis.Normal;
				var perp = new Vec2( -dir.y, dir.x );
				float half = MathF.Abs( Vec2.Dot( _pending[2] - a, perp ) );
				if ( half <= 1e-4f )
				{
					_pending.RemoveAt( 2 );
					refusal = "That click is on the slot centre line, so it would have no width.";
					break;
				}
				var p1 = PointIndex( a + perp * half );
				var p2 = PointIndex( b + perp * half );
				var p3 = PointIndex( b - perp * half );
				var p4 = PointIndex( a - perp * half );
				var ca = PointIndex( a );
				var cb = PointIndex( b );
				Track( new SketchLine( p1, p2 ) );
				Track( new SketchArc( cb, p2, p3, true ) );
				Track( new SketchLine( p3, p4 ) );
				Track( new SketchArc( ca, p4, p1, true ) );
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.Ellipse when _pending.Count == 2 && ( _pending[1] - _pending[0] ).Length <= 1e-4f:
			{
				_pending.RemoveAt( 1 );
				refusal = "That click is on the centre, so the ellipse would have no long axis.";
				break;
			}

			case EditTool.Ellipse when _pending.Count == 3:
			{
				var centre = _pending[0];
				var majorEnd = _pending[1];
				float minor = PerpendicularDistance( centre, majorEnd, _pending[2] );
				if ( minor <= 1e-4f )
				{
					_pending.RemoveAt( 2 );
					refusal = "That click is on the long axis, so the ellipse would be flat.";
					break;
				}
				Track( new SketchEllipse( PointIndex( centre ), PointIndex( majorEnd ), minor ) );
				_pending.Clear();
				Changed();
				break;
			}

			case EditTool.Spline:
			{
				_pending.Clear();
				if ( _splinePoints.Count > 0 && ( _splinePoints[^1] - p ).Length < 1e-4f )
				{
					refusal = "That's the same spot as the last spline point. Press Enter to finish.";
					break;
				}
				_splinePoints.Add( p );
				Changed();
				break;
			}

			case EditTool.Trim:
			{
				_pending.Clear();
				var curve = CurveUnder( p );
				if ( curve == null ) refusal = "Trim: click on a curve to trim.";
				else if ( !SketchEdit.Trim( S, curve, p, out var err ) ) refusal = $"Trim: {err}";
				else Changed();
				break;
			}

			case EditTool.Extend:
			{
				_pending.Clear();
				var curve = CurveUnder( p );
				if ( curve == null ) refusal = "Extend: click on a curve to extend.";
				else
				{
					var (startIdx, endIdx) = curve.Endpoints;
					bool atStart = ( S.Points[startIdx] - p ).LengthSquared < ( S.Points[endIdx] - p ).LengthSquared;
					if ( !SketchEdit.Extend( S, curve, atStart, out var err ) ) refusal = $"Extend: {err}";
					else Changed();
				}
				break;
			}

			case EditTool.Fillet when _pending.Count == 1:
			{
				int corner = NearestPoint( p );
				if ( corner < 0 )
				{
					_pending.Clear();
					refusal = "Fillet: click the corner point you want rounded.";
				}
				else _filletCorner = corner;
				break;
			}

			case EditTool.Fillet when _pending.Count == 2:
			{
				int corner = _filletCorner;
				float radius = ( p - S.Points[corner] ).Length;
				if ( !SketchEdit.Fillet( S, corner, radius, out var err ) )
				{
					_pending.RemoveAt( 1 );
					refusal = $"Fillet: {err}";
				}
				else
				{
					_filletCorner = -1;
					_pending.Clear();
					Changed();
				}
				break;
			}

			case EditTool.Offset when _pending.Count == 1:
			{
				var curve = CurveUnder( p );
				if ( curve == null )
				{
					_pending.Clear();
					refusal = "Offset: click the curve you want offset.";
				}
				else _offsetCurveId = curve.Id;
				break;
			}

			case EditTool.Offset when _pending.Count == 2:
			{
				var curve = S.Curves.FirstOrDefault( c => c.Id == _offsetCurveId );
				if ( curve == null ) { _pending.Clear(); _offsetCurveId = null; break; }
				float dist = SignedOffset( curve, p );
				if ( MathF.Abs( dist ) < 1e-4f )
				{
					_pending.RemoveAt( 1 );
					refusal = "Offset: click off to the side you want it on.";
				}
				else if ( !SketchEdit.Offset( S, new[] { curve }, dist, out var created, out var err ) )
				{
					_pending.RemoveAt( 1 );
					refusal = $"Offset: {err}";
				}
				else
				{
					foreach ( var m in created ) m.Construction = ConstructionMode;
					_offsetCurveId = null;
					_pending.Clear();
					Changed();
				}
				break;
			}

			case EditTool.Mirror:
			{
				_pending.Clear();
				var mirrorLine = CurveUnder( p ) as SketchLine;
				if ( mirrorLine == null )
				{
					refusal = "Mirror: click a line to reflect across.";
					break;
				}
				var curves = S.Curves.Where( c => SelectedCurves.Contains( c.Id ) && c.Id != mirrorLine.Id ).ToList();
				var pts = SelectedPoints.ToList();
				if ( curves.Count == 0 && pts.Count == 0 )
				{
					refusal = "Mirror: select geometry first, then pick Mirror and click the mirror line.";
					break;
				}
				if ( !SketchEdit.Mirror( S, curves, pts, mirrorLine, out var created, out var err ) )
					refusal = $"Mirror: {err}";
				else
				{
					foreach ( var m in created ) m.Construction = ConstructionMode;
					Changed();
				}
				break;
			}

			case EditTool.Use:
			{
				_pending.Clear();
				if ( Reference == null )
				{
					refusal = "Project: this sketch is not on a face.";
					break;
				}
				if ( snap.ReferenceEdgeIndex < 0 )
				{
					refusal = "Project: hover a green face edge until it highlights, then click it.";
					break;
				}
				var res = Reference.UseEdge( S, snap.ReferenceEdgeIndex, ConstructionMode, out _ );
				if ( res != UseEdgeResult.Added )
					refusal = $"Project: edge was {res}.";
				else
					Changed();
				break;
			}
		}

		if ( refusal != null ) GeppettoToolContext.Say( $"Sketch: {refusal}" );
		return true;
	}

	public bool FinishSpline()
	{
		if ( _splinePoints.Count < 2 ) { _splinePoints.Clear(); return false; }
		var indices = _splinePoints.Select( PointIndex ).ToArray();
		Track( new SketchSpline( indices, closed: false ) );
		_splinePoints.Clear();
		Changed();
		return true;
	}

	public int ProjectAll()
	{
		if ( Editing == null || Reference == null ) return 0;
		int added = 0;
		for ( int i = 0; i < Reference.Edges.Count; i++ )
		{
			if ( Reference.UseEdge( S, i, ConstructionMode, out _ ) == UseEdgeResult.Added )
				added++;
		}
		if ( added > 0 ) Changed();
		return added;
	}

	// =========================================================================================
	// Select
	// =========================================================================================

	static IEnumerable<int> PointsOf( SketchCurve c ) => c switch
	{
		SketchArc a => new[] { a.Center, a.Start, a.End },
		SketchCircle ci => new[] { ci.Center },
		SketchEllipse el => new[] { el.Center, el.MajorPoint },
		SketchBezier bz => new[] { bz.P0, bz.P1, bz.P2, bz.P3 },
		SketchSpline sp => sp.Points,
		_ => c.Endpoints is var (a0, b0) && a0 >= 0 ? new[] { a0, b0 } : Array.Empty<int>(),
	};

	HashSet<int> UsedPoints() => S.Curves.SelectMany( PointsOf ).Where( i => i >= 0 && i < S.Points.Count ).ToHashSet();

	public void ClearSelection() { SelectedPoints.Clear(); SelectedCurves.Clear(); GeppettoTools.Redraw(); }

	public void Toggle( int point, bool add )
	{
		if ( !add ) ClearSelection();
		if ( !SelectedPoints.Add( point ) && add ) SelectedPoints.Remove( point );
		GeppettoTools.Redraw();
	}

	public void Toggle( string curve, bool add )
	{
		if ( !add ) ClearSelection();
		if ( !SelectedCurves.Add( curve ) && add ) SelectedCurves.Remove( curve );
		GeppettoTools.Redraw();
	}

	public void SelectWhere( Func<Vec2, bool> inside, bool touching, bool add )
	{
		if ( Editing == null ) return;
		if ( !add ) { SelectedPoints.Clear(); SelectedCurves.Clear(); }
		foreach ( var i in UsedPoints() )
			if ( inside( S.Points[i] ) ) SelectedPoints.Add( i );
		foreach ( var c in S.Curves )
		{
			var pts = c.Tessellate( S, S.Tolerance );
			if ( pts.Count == 0 ) continue;
			if ( touching ? pts.Any( inside ) : pts.All( inside ) ) SelectedCurves.Add( c.Id );
		}
		GeppettoTools.Redraw();
	}

	HashSet<int> MovingPoints( int grabbed )
	{
		var set = new HashSet<int> { grabbed };
		if ( !SelectedPoints.Contains( grabbed ) ) return set;
		set.UnionWith( SelectedPoints );
		foreach ( var c in S.Curves.Where( c => SelectedCurves.Contains( c.Id ) ) ) set.UnionWith( PointsOf( c ) );
		return set;
	}

	public void MovePoint( int grabbed, Vec2 delta )
	{
		if ( Editing == null ) return;
		foreach ( var i in MovingPoints( grabbed ) ) S.Points[i] += delta;
		Changed();
	}

	public bool DragHandle( CurveHandle h, Vec2 target )
	{
		if ( Editing == null ) return false;
		bool moved = SketchHandles.Drag( S, h.CurveId, h.Kind, target, out var refusal );
		if ( refusal != null ) GeppettoToolContext.Say( $"Sketch: {refusal}" );
		if ( moved ) Changed();
		return moved;
	}

	public int DeleteSelection()
	{
		if ( Editing == null ) return 0;
		var gone = S.Curves.Where( c => SelectedCurves.Contains( c.Id ) || PointsOf( c ).Any( SelectedPoints.Contains ) ).ToList();
		if ( gone.Count == 0 && SelectedPoints.Count == 0 ) return 0;
		var ids = gone.Select( c => c.Id ).ToHashSet();
		S.Curves.RemoveAll( c => ids.Contains( c.Id ) );
		S.Constraints.RemoveAll( k => ( k.CurveId != null && ids.Contains( k.CurveId ) )
			|| new[] { k.PointA, k.PointB, k.PointC, k.PointD }.Any( SelectedPoints.Contains ) );
		ClearSelection();
		Changed();
		GeppettoToolContext.Say( $"Sketch: deleted {gone.Count} curve(s)." );
		return gone.Count;
	}

	// =========================================================================================
	// Viewport Input & Picking
	// =========================================================================================

	public Vector2? Screen( Camera3D cam, Vec2 p )
	{
		var w = _ray.ToWorld( Plane!.ToWorld( p ) );
		return cam.IsPositionBehind( w ) ? null : cam.UnprojectPosition( w );
	}

	int PointAt( Camera3D cam, Vector2 screen )
	{
		int best = -1; float bestD = SubSelection.EdgePickPixels;
		foreach ( var i in UsedPoints() )
			if ( Screen( cam, S.Points[i] ) is { } s && s.DistanceTo( screen ) <= bestD ) { best = i; bestD = s.DistanceTo( screen ); }
		return best;
	}

	CurveHandle? HandleAt( Camera3D cam, Vector2 screen )
	{
		foreach ( var h in SketchHandles.For( S ) )
			if ( Screen( cam, h.At ) is { } s && s.DistanceTo( screen ) <= SubSelection.EdgePickPixels ) return h;
		return null;
	}

	string? CurveAt( Camera3D cam, Vector2 screen )
	{
		string? best = null; float bestD = SubSelection.EdgePickPixels;
		foreach ( var c in S.Curves )
		{
			var pts = c.Tessellate( S, S.Tolerance );
			for ( int i = 0; i + 1 < pts.Count; i++ )
			{
				if ( Screen( cam, pts[i] ) is not { } a || Screen( cam, pts[i + 1] ) is not { } b ) continue;
				float d = Geometry2D.GetClosestPointToSegment( screen, a, b ).DistanceTo( screen );
				if ( d <= bestD ) { best = c.Id; bestD = d; }
			}
		}
		return best;
	}

	bool EditInput( Camera3D camera, InputEvent e )
	{
		switch ( e )
		{
			case InputEventMouseMotion mm:
			{
				_mouse = mm.Position;
				var (o, d) = GeppettoPicker.Ray( camera, mm.Position );
				if ( OnPlane( o, d ) is { } raw )
				{
					var snap = Snap( raw, isLine: ( Tool == EditTool.Line && ChainLast >= 0 ) || ( Tool == EditTool.LineMidpoint && _pending.Count == 1 ) );
					_hover = snap.Point;
					if ( _dragPoint >= 0 ) { MovePoint( _dragPoint, snap.Point - _dragFrom ); _dragFrom = snap.Point; }
					else if ( _dragHandle is { } dh ) DragHandle( dh, snap.Point );
				}
				else
				{
					_hover = null;
					LastSnap = default;
				}
				GeppettoTools.Redraw();
				return false;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb when !mb.AltPressed:
			{
				var (o, d) = GeppettoPicker.Ray( camera, mb.Position );
				var raw = OnPlane( o, d );
				var snap = raw is { } r ? Snap( r, isLine: ( Tool == EditTool.Line && ChainLast >= 0 ) || ( Tool == EditTool.LineMidpoint && _pending.Count == 1 ) ) : default;
				var at = raw != null ? snap.Point : (Vec2?)null;
				int point = snap.SnappedPointIndex >= 0 ? snap.SnappedPointIndex : PointAt( camera, mb.Position );

				if ( Tool == EditTool.Select )
				{
					if ( point >= 0 )
					{
						if ( !SelectedPoints.Contains( point ) || mb.ShiftPressed ) Toggle( point, mb.ShiftPressed );
						_dragPoint = point; _dragFrom = at ?? S.Points[point];
					}
					else if ( HandleAt( camera, mb.Position ) is { } h ) _dragHandle = h;
					else if ( CurveAt( camera, mb.Position ) is { } c ) Toggle( c, mb.ShiftPressed );
					else _boxFrom = mb.Position;
					return true;
				}
				if ( Tool == EditTool.Cut )
				{
					_cutFrom = at;
					return true;
				}
				if ( at == null && point < 0 )
				{
					GeppettoToolContext.Say( "Sketch: that click is edge-on to the plane; orbit and click again." );
					return true;
				}
				var clickPoint = at ?? S.Points[point];
				if ( Tool == EditTool.Line )
				{
					LineClick( clickPoint, point, snap );
					return true;
				}
				ClickTool( clickPoint, snap );
				return true;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mb:
			{
				if ( Tool == EditTool.Cut && _cutFrom is { } cf )
				{
					var (o, d) = GeppettoPicker.Ray( camera, mb.Position );
					if ( OnPlane( o, d ) is { } endRaw )
					{
						int cuts = SketchCut.Cut( S, cf, endRaw );
						if ( cuts > 0 )
						{
							GeppettoToolContext.Say( $"Sketch: cut through {cuts} segment(s)." );
							Changed();
						}
					}
					_cutFrom = null;
					return true;
				}
				if ( _boxFrom is { } from )
				{
					var rect = new Rect2( from, Vector2.Zero ).Expand( mb.Position );
					if ( rect.Size.LengthSquared() < 16f ) { if ( !mb.ShiftPressed ) ClearSelection(); }
					else SelectWhere( p => Screen( camera, p ) is { } s && rect.HasPoint( s ), touching: mb.Position.X < from.X, mb.ShiftPressed );
				}
				_boxFrom = null; _dragPoint = -1; _dragHandle = null;
				return true;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
				if ( ChainLast >= 0 ) { EndChain(); return true; }
				if ( _pending.Count > 0 ) { _pending.Clear(); GeppettoTools.Redraw(); return true; }
				return false;
			case InputEventKey { Pressed: true, Echo: false } k:
				switch ( k.Keycode )
				{
					case Key.S: Tool = EditTool.Select; _pending.Clear(); EndChain(); return true;
					case Key.L: Tool = EditTool.Line; _pending.Clear(); ClearSelection(); return true;
					case Key.M: Tool = EditTool.LineMidpoint; _pending.Clear(); ClearSelection(); return true;
					case Key.R: Tool = k.ShiftPressed ? EditTool.RectangleCentre : EditTool.Rectangle; _pending.Clear(); ClearSelection(); return true;
					case Key.C: Tool = k.ShiftPressed ? EditTool.CircleThreePoint : EditTool.Circle; _pending.Clear(); ClearSelection(); return true;
					case Key.A: Tool = k.ShiftPressed ? EditTool.ArcThreePoint : EditTool.Arc; _pending.Clear(); ClearSelection(); return true;
					case Key.P: Tool = EditTool.Point; _pending.Clear(); ClearSelection(); return true;
					case Key.G: Tool = k.ShiftPressed ? EditTool.PolygonCircumscribed : EditTool.Polygon; _pending.Clear(); ClearSelection(); return true;
					case Key.O: Tool = EditTool.Offset; _pending.Clear(); return true;
					case Key.T: Tool = EditTool.Trim; _pending.Clear(); return true;
					case Key.X: Tool = EditTool.Extend; _pending.Clear(); return true;
					case Key.F: Tool = EditTool.Fillet; _pending.Clear(); return true;
					case Key.U: Tool = EditTool.Use; _pending.Clear(); return true;
					case Key.Q:
						ConstructionMode = !ConstructionMode;
						GeppettoToolContext.Say( $"Sketch: construction mode {( ConstructionMode ? "ON" : "OFF" )}." );
						GeppettoTools.Redraw();
						return true;
					case Key.Enter or Key.KpEnter:
						if ( Tool == EditTool.Spline ) { FinishSpline(); return true; }
						if ( ChainLast >= 0 ) EndChain();
						else if ( _pending.Count > 0 ) _pending.Clear();
						else Finish();
						return true;
					case Key.Escape:
						if ( Tool == EditTool.Spline && _splinePoints.Count > 0 ) { _splinePoints.Clear(); Changed(); return true; }
						if ( _pending.Count > 0 ) { _pending.Clear(); GeppettoTools.Redraw(); return true; }
						if ( ChainLast >= 0 ) { EndChain(); return true; }
						Finish();
						return true;
					case Key.Backspace when Tool == EditTool.Line && ChainLast >= 0:
						UndoChainLine();
						return true;
					case Key.Delete or Key.Backspace:
						DeleteSelection();
						return true;
				}
				return false;
		}
		return false;
	}

	// =========================================================================================
	// Drawing & Overlays
	// =========================================================================================

	static readonly Color Picked = new( 1f, .9f, .3f );
	static readonly Color Line = new( .85f, .85f, .9f );

	void DrawLine2D( Control overlay, Camera3D cam, Vec2 a, Vec2 b, Color col, float width = 1.5f )
	{
		if ( Screen( cam, a ) is { } sa && Screen( cam, b ) is { } sb )
			overlay.DrawLine( sa, sb, col, width, antialiased: true );
	}

	void DrawLoop2D( Control overlay, Camera3D cam, IReadOnlyList<Vec2> pts, Color col, float width = 1.5f )
	{
		for ( int i = 0; i < pts.Count; i++ )
			DrawLine2D( overlay, cam, pts[i], pts[( i + 1 ) % pts.Count], col, width );
	}

	void DrawCircle2D( Control overlay, Camera3D cam, Vec2 centre, float radius, Color col, float width = 1.5f )
	{
		const int segs = 32;
		Vector2? prev = null;
		Vector2? first = null;
		for ( int i = 0; i <= segs; i++ )
		{
			float a = i * MathF.Tau / segs;
			var p = centre + new Vec2( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius );
			var s = Screen( cam, p );
			if ( s is { } curr )
			{
				if ( prev is { } pv ) overlay.DrawLine( pv, curr, col, width, antialiased: true );
				else first = curr;
				prev = curr;
			}
		}
	}

	void DrawEdit( Control overlay )
	{
		var cam = _camera;
		if ( cam != null && GodotObject.IsInstanceValid( cam ) && Plane != null && Editing != null )
		{
			// 1. Shaded regions (Profile Inspector)
			var profiles = ProfileFinder.Find( S );
			foreach ( var region in ProfileFinder.ShadedRegions( S, profiles ) )
			{
				if ( region.Outer.Count >= 3 )
				{
					var screenPts = region.Outer.Select( pt => Screen( cam, pt ) ).Where( s => s != null ).Select( s => s!.Value ).ToArray();
					if ( screenPts.Length >= 3 )
						overlay.DrawColoredPolygon( screenPts, new Color( 0.2f, 0.6f, 1f, 0.12f ) );
				}
			}

			// 2. Existing curves
			foreach ( var c in S.Curves )
			{
				var pts = c.Tessellate( S, S.Tolerance );
				var col = SelectedCurves.Contains( c.Id ) ? Picked : c.Construction ? Line with { A = .4f } : Amber;
				for ( int i = 0; i + 1 < pts.Count; i++ )
					GeppettoPicker.DrawWorldLine( overlay, cam, _ray.ToWorld( Plane.ToWorld( pts[i] ) ), _ray.ToWorld( Plane.ToWorld( pts[i + 1] ) ), col, SelectedCurves.Contains( c.Id ) ? 3.5f : 2.5f );
			}

			// 3. Handles and Points
			if ( Tool == EditTool.Select )
				foreach ( var h in SketchHandles.For( S ) )
					if ( Screen( cam, h.At ) is { } s ) overlay.DrawRect( new Rect2( s - new Vector2( 3, 3 ), new Vector2( 6, 6 ) ), Line, filled: false, width: 1.5f );

			foreach ( var i in UsedPoints() )
				if ( Screen( cam, S.Points[i] ) is { } s ) overlay.DrawCircle( s, SelectedPoints.Contains( i ) ? 5.5f : 4f, SelectedPoints.Contains( i ) ? Picked : Amber );

			// 4. Loose ends flag
			var endpointCounts = new Dictionary<int, int>();
			foreach ( var c in S.Curves.Where( c => !c.Construction ) )
			{
				var (st, ed) = c.Endpoints;
				if ( st >= 0 && ed >= 0 )
				{
					endpointCounts[st] = endpointCounts.GetValueOrDefault( st ) + 1;
					endpointCounts[ed] = endpointCounts.GetValueOrDefault( ed ) + 1;
				}
			}
			foreach ( var (pt, count) in endpointCounts )
			{
				if ( count == 1 && Screen( cam, S.Points[pt] ) is { } s )
				{
					overlay.DrawCircle( s, 5.5f, new Color( 1f, 0.35f, 0.2f, 0.85f ) );
					overlay.DrawCircle( s, 3f, new Color( 0.1f, 0.1f, 0.1f, 0.9f ) );
				}
			}

			// 5. Tool Rubber-band previews
			if ( _hover is { } hv )
			{
				switch ( Tool )
				{
					case EditTool.Line when ChainLast >= 0:
						DrawLine2D( overlay, cam, S.Points[ChainLast], hv, HoverBlue, 1.5f );
						break;
					case EditTool.LineMidpoint when _pending.Count == 1:
						var far = _pending[0] + ( _pending[0] - hv );
						DrawLine2D( overlay, cam, far, hv, HoverBlue, 1.5f );
						if ( Screen( cam, ( far + hv ) * 0.5f ) is { } smid )
							overlay.DrawString( ThemeDB.FallbackFont, smid + new Vector2( 8, -8 ), $"{( hv - far ).Length:0.##}\"", HorizontalAlignment.Left, -1, 11, HoverBlue );
						break;
					case EditTool.Rectangle when _pending.Count == 1:
						var min = new Vec2( MathF.Min( _pending[0].x, hv.x ), MathF.Min( _pending[0].y, hv.y ) );
						var max = new Vec2( MathF.Max( _pending[0].x, hv.x ), MathF.Max( _pending[0].y, hv.y ) );
						DrawLoop2D( overlay, cam, RectangleCorners( min, max ), HoverBlue, 1.5f );
						if ( Screen( cam, hv ) is { } srect )
							overlay.DrawString( ThemeDB.FallbackFont, srect + new Vector2( 8, -8 ), $"{MathF.Abs( max.x - min.x ):0.##}\" × {MathF.Abs( max.y - min.y ):0.##}\"", HorizontalAlignment.Left, -1, 11, HoverBlue );
						break;
					case EditTool.RectangleCentre when _pending.Count == 1:
						var span = hv - _pending[0];
						var cmin = new Vec2( _pending[0].x - MathF.Abs( span.x ), _pending[0].y - MathF.Abs( span.y ) );
						var cmax = new Vec2( _pending[0].x + MathF.Abs( span.x ), _pending[0].y + MathF.Abs( span.y ) );
						DrawLoop2D( overlay, cam, RectangleCorners( cmin, cmax ), HoverBlue, 1.5f );
						if ( Screen( cam, hv ) is { } scenter )
							overlay.DrawString( ThemeDB.FallbackFont, scenter + new Vector2( 8, -8 ), $"{MathF.Abs( span.x ) * 2f:0.##}\" × {MathF.Abs( span.y ) * 2f:0.##}\"", HorizontalAlignment.Left, -1, 11, HoverBlue );
						break;
					case EditTool.Circle when _pending.Count == 1:
						float r = ( hv - _pending[0] ).Length;
						DrawCircle2D( overlay, cam, _pending[0], r, HoverBlue, 1.5f );
						if ( Screen( cam, ( _pending[0] + hv ) * 0.5f ) is { } scirc )
							overlay.DrawString( ThemeDB.FallbackFont, scirc + new Vector2( 8, -8 ), $"R {r:0.##}\"", HorizontalAlignment.Left, -1, 11, HoverBlue );
						break;
					case EditTool.CircleThreePoint when _pending.Count == 1:
						DrawLine2D( overlay, cam, _pending[0], hv, HoverBlue, 1.5f );
						break;
					case EditTool.CircleThreePoint when _pending.Count == 2:
						if ( Circumcentre( _pending[0], _pending[1], hv ) is { } cc )
							DrawCircle2D( overlay, cam, cc, ( cc - _pending[0] ).Length, HoverBlue, 1.5f );
						break;
					case EditTool.Arc when _pending.Count == 1:
						DrawCircle2D( overlay, cam, _pending[0], ( hv - _pending[0] ).Length, HoverBlue with { A = 0.5f }, 1f );
						DrawLine2D( overlay, cam, _pending[0], hv, HoverBlue, 1.5f );
						break;
					case EditTool.Arc when _pending.Count == 2:
						DrawCircle2D( overlay, cam, _pending[0], ( _pending[1] - _pending[0] ).Length, HoverBlue with { A = 0.5f }, 1f );
						DrawLine2D( overlay, cam, _pending[0], hv, HoverBlue, 1.5f );
						break;
					case EditTool.ArcThreePoint when _pending.Count == 1:
						DrawLine2D( overlay, cam, _pending[0], hv, HoverBlue, 1.5f );
						break;
					case EditTool.ArcThreePoint when _pending.Count == 2:
						if ( Circumcentre( _pending[0], hv, _pending[1] ) is { } arcC )
							DrawCircle2D( overlay, cam, arcC, ( arcC - _pending[0] ).Length, HoverBlue with { A = 0.5f }, 1f );
						break;
					case EditTool.Polygon when _pending.Count == 1:
						DrawLoop2D( overlay, cam, RegularPolygon( _pending[0], hv, PolygonSides, circumscribed: false ), HoverBlue, 1.5f );
						break;
					case EditTool.PolygonCircumscribed when _pending.Count == 1:
						DrawLoop2D( overlay, cam, RegularPolygon( _pending[0], hv, PolygonSides, circumscribed: true ), HoverBlue, 1.5f );
						break;
					case EditTool.Slot when _pending.Count == 1:
						DrawLine2D( overlay, cam, _pending[0], hv, HoverBlue, 1.5f );
						break;
					case EditTool.Spline:
						for ( int i = 0; i + 1 < _splinePoints.Count; i++ )
							DrawLine2D( overlay, cam, _splinePoints[i], _splinePoints[i + 1], HoverBlue, 2f );
						if ( _splinePoints.Count > 0 )
							DrawLine2D( overlay, cam, _splinePoints[^1], hv, HoverBlue, 1.5f );
						break;
					case EditTool.Cut when _cutFrom is { } cf:
						DrawLine2D( overlay, cam, cf, hv, new Color( 1f, 0.4f, 0.2f ), 2f );
						break;
				}
			}

			// 6. Box select
			if ( _boxFrom is { } from )
			{
				var rect = new Rect2( from, Vector2.Zero ).Expand( _mouse );
				bool touching = _mouse.X < from.X;
				overlay.DrawRect( rect, HoverBlue with { A = .12f } );
				overlay.DrawRect( rect, HoverBlue, filled: false, width: touching ? 1f : 1.5f );
			}

			DrawSnapMarker( overlay, cam );

			foreach ( var m in ConstraintTools.Markers( S ) )
			{
				if ( Screen( cam, m.Anchor ) is { } pt )
				{
					var glyphPos = pt + new Vector2( m.Away.x, -m.Away.y ) * 14f;
					overlay.DrawString( ThemeDB.FallbackFont, glyphPos + new Vector2( -4, 4 ), m.Label, HorizontalAlignment.Center, -1, 10, Picked );
				}
			}
		}

		string what = Tool switch
		{
			EditTool.Select => "Select (S): click points/curves (Shift adds), drag point/grip, box-select. Delete removes.",
			EditTool.Line => ChainLast >= 0 ? "Line: click next point; click first to close. Esc/Enter ends, Backspace takes back." : "Line (L): click start point.",
			EditTool.LineMidpoint => _pending.Count == 0 ? "Midpoint Line (M): click middle of line." : "Midpoint Line: click one end; grows both ways.",
			EditTool.Rectangle => _pending.Count == 0 ? "Corner Rectangle (R): click first corner." : "Corner Rectangle: click opposite corner.",
			EditTool.RectangleCentre => _pending.Count == 0 ? "Centre Rectangle (Shift+R): click centre." : "Centre Rectangle: click corner.",
			EditTool.Circle => _pending.Count == 0 ? "Centre Circle (C): click centre." : "Centre Circle: click point on rim.",
			EditTool.CircleThreePoint => $"3-Point Circle (Shift+C): click point {_pending.Count + 1} of 3 on rim.",
			EditTool.Arc => _pending.Count == 0 ? "Centre Arc (A): click centre." : _pending.Count == 1 ? "Centre Arc: click start point." : "Centre Arc: click end direction.",
			EditTool.ArcThreePoint => _pending.Count == 0 ? "3-Point Arc (Shift+A): click start point." : _pending.Count == 1 ? "3-Point Arc: click end point." : "3-Point Arc: click through-point.",
			EditTool.Point => "Point (P): click to place a reference point.",
			EditTool.Polygon => _pending.Count == 0 ? $"Polygon (G): click centre ({PolygonSides} sides)." : "Polygon: click vertex.",
			EditTool.PolygonCircumscribed => _pending.Count == 0 ? $"Circumscribed Polygon (Shift+G): click centre ({PolygonSides} sides)." : "Polygon: click edge midpoint.",
			EditTool.Slot => _pending.Count == 0 ? "Slot: click start of centre line." : _pending.Count == 1 ? "Slot: click end of centre line." : "Slot: click width point.",
			EditTool.Ellipse => _pending.Count == 0 ? "Ellipse: click centre." : _pending.Count == 1 ? "Ellipse: click major axis end." : "Ellipse: click minor radius point.",
			EditTool.Spline => $"Spline: click points ({_splinePoints.Count} placed); Enter finishes.",
			EditTool.Trim => "Trim (T): click a curve between intersections to trim.",
			EditTool.Extend => "Extend (X): click curve near endpoint to extend.",
			EditTool.Fillet => _pending.Count == 0 ? "Fillet (F): click corner point to round." : "Fillet: click radius.",
			EditTool.Offset => _pending.Count == 0 ? "Offset (O): click curve to offset." : "Offset: click offset distance.",
			EditTool.Mirror => "Mirror: click line to reflect selection across.",
			EditTool.Use => "Project (U): click highlighted face edge to copy into sketch.",
			EditTool.Cut => "Cut: drag a stroke across curves to slice them.",
			_ => "",
		};
		GeppettoPicker.DrawHint( overlay, $"Edit sketch ({S.Curves.Count} curves). {what}\nS select, L line, M midpoint, R rect, C circle, A arc, P point, Q construction ({( ConstructionMode ? "ON" : "OFF" )}). Esc/Enter finishes." );
	}
}
#endif
