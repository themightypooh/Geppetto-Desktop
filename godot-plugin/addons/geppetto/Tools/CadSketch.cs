#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;
using NVec3 = System.Numerics.Vector3;

namespace Geppetto;

/// <summary>
/// Basic Sketch: click a face (or empty space, for the global plane that faces the camera most),
/// then click points on that plane; clicking the first point again or pressing Enter closes the
/// outline into a polyline <see cref="SketchFeature"/> that goes into the tree as one undo step.
/// Backspace removes the last point, Esc cancels. Lines only: arcs, splines and constraints are
/// for the full sketcher.
/// </summary>
public sealed partial class CadSketch : IGeppettoViewportTool
{
	public string Name => "Sketch";
	readonly GeppettoDocument _doc;
	readonly GeppettoPicker _ray; // coordinates + face hits

	public FaceRef? Face { get; private set; }
	public int PlaneIndex { get; private set; } = -1;
	/// <summary>The Plane feature the sketch goes on, when a datum plane was clicked; else empty.</summary>
	public string PlaneFeatureId { get; private set; } = "";
	/// <summary>Half-size of the datum plane squares (as drawn), for picking them.</summary>
	public float DatumHalfSize { get; set; } = 12f;
	/// <summary>Half-size of the Top/Front/Right squares (as drawn), for picking them.</summary>
	public float OriginHalfSize { get; set; } = 12f;
	public SketchPlane? Plane { get; private set; }
	public List<Vec2> Points { get; } = new();

	readonly SketchSnapper _snapper = new();
	public SketchReference? Reference { get; private set; }
	public SnapResult LastSnap { get; private set; }

	public const float SnapPixels = 12f;
	public const float AlignmentPixels = 7f;
	public const float EdgeSnapPixels = 8f;
	public const float GridPixels = 14f;

	Camera3D? _camera;
	Vector2 _mouse;
	Vec2? _hover;

	public CadSketch( GeppettoDocument doc )
	{
		_doc = doc;
		_ray = new GeppettoPicker( "Sketch", doc, PickKinds.Faces, "" );
	}

	public void UpdateReference()
	{
		if ( Face is { } fr && Plane != null )
		{
			Reference = SketchReference.FromFace( _doc.Studio.Bodies, fr, Plane );
			_snapper.Reference = Reference;
		}
		else
		{
			Reference = null;
			_snapper.Reference = null;
		}
	}

	public float UnitsPerPixel( Camera3D cam, Vec3 worldPlaneOrigin )
	{
		var worldPos = _ray.ToWorld( worldPlaneOrigin );
		float depth = ( cam.GlobalPosition - worldPos ).Length();
		float fovRad = Mathf.DegToRad( cam.Fov );
		float vh = cam.GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
		float metresPerPixel = 2f * depth * MathF.Tan( fovRad * 0.5f ) / MathF.Max( vh, 1f );
		return metresPerPixel / EffigyMeshBridge.ScaleToMetres;
	}

	public SnapResult Snap( Vec2 raw, IReadOnlyList<Vec2>? pending = null, bool isLine = false )
	{
		if ( Plane == null ) return new SnapResult( raw, -1, 0 );
		float units = _camera != null && GodotObject.IsInstanceValid( _camera ) ? UnitsPerPixel( _camera, Plane.Origin ) : 0.05f;
		_snapper.PointRadius = SnapPixels * units;
		_snapper.AlignmentRadius = AlignmentPixels * units;
		_snapper.GridStep = SketchSnapper.AutoGridStep( units, GridPixels );
		_snapper.ReferencePointRadius = Reference != null ? SnapPixels * units : 0f;
		_snapper.ReferenceEdgeRadius = Reference != null ? EdgeSnapPixels * units : 0f;
		_snapper.CurveRadius = EdgeSnapPixels * units;

		var sketch = Editing?.Sketch;
		if ( sketch == null )
		{
			sketch = new Sketch();
			foreach ( var p in Points ) sketch.AddPoint( p );
			for ( int i = 0; i + 1 < Points.Count; i++ ) sketch.Add( new SketchLine( i, i + 1 ) );
		}

		var pList = pending ?? ( Editing != null && ChainLast >= 0 ? new[] { S.Points[ChainLast] } : Points );
		LastSnap = _snapper.Snap( sketch, raw, pList, isLine );
		return LastSnap;
	}

	public void Start( GeppettoCharacter character )
	{
		_ray.BodyTransform = GeppettoPicker.BodyTransformOf( character.Root );
		DatumHalfSize = GeppettoDatumPlanes.HalfSize( character.Root );
		OriginHalfSize = GeppettoDatumPlanes.OriginHalfSize( character.Root );
		_camera = EditorInterface.Singleton.GetEditorViewport3D( 0 )?.GetCamera3D();
		GeppettoTools.Activate( this );
	}

	public void Exit() { GeppettoDatumPlanes.Hovered = null; if ( Editing != null ) Finish(); }


	/// <summary>The origin or datum plane a ray is on that's nearer than any face it hits, else null.</summary>
	GeppettoDatumPlanes.Hit? PlaneUnder( Vector3 origin, Vector3 dir, out bool onFace, out Body? body, out MeshHit hit )
	{
		var (o, d) = _ray.RayToEffigy( origin, dir );
		onFace = _ray.Selection.TryPickFace( o, d, out body, out hit );
		var p = GeppettoDatumPlanes.PickAny( _doc.Studio, new Vec3( o.X, o.Y, o.Z ), new Vec3( d.X, d.Y, d.Z ), DatumHalfSize, OriginHalfSize );
		return p is { } h && ( !onFace || h.T < hit.Distance ) ? p : null;
	}

	/// <summary>Chooses the plane from a ray: the nearest of a face, a Top/Front/Right plane or a datum
	/// plane. Empty space picks nothing: every plane is drawn, so there's always one to click.</summary>
	public void ChoosePlane( Vector3 origin, Vector3 dir )
	{
		PlaneFeatureId = "";
		var pick = PlaneUnder( origin, dir, out bool onFace, out var body, out var hit );
		GeppettoDatumPlanes.Hovered = null;
		if ( pick is { } p )
		{
			Face = null; Plane = p.Plane; PlaneIndex = Math.Max( 0, p.Index ); PlaneName = p.Name;
			if ( p.Index < 0 ) PlaneFeatureId = p.Id;
			UpdateReference();
			GeppettoToolContext.Say( $"Sketch: on {p.Name}. Click points; click the first point or press Enter to close." );
			if ( Armed is { } t ) BeginNew( t );
			return;
		}
		if ( onFace )
		{
			var face = FacePlane.Capture( body!, hit.FaceIndex, hit.Point );
			if ( FacePlane.TryResolve( _doc.Studio.Bodies, face, out var plane ) )
			{
				Face = face; Plane = plane; PlaneIndex = 0; PlaneName = "a face";
				UpdateReference();
				GeppettoToolContext.Say( "Sketch: on the picked face. Click points; click the first point or press Enter to close." );
				if ( Armed is { } t ) BeginNew( t );
				return;
			}
		}
		GeppettoToolContext.Say( "Sketch: click the Top, Front or Right plane, a Plane, or a face." );
	}

	/// <summary>Where a Godot world ray crosses the sketch plane, in plane coordinates.</summary>
	public Vec2? OnPlane( Vector3 origin, Vector3 dir )
	{
		if ( Plane == null ) return null;
		var (o, d) = _ray.RayToEffigy( origin, dir );
		var ro = new Vec3( o.X, o.Y, o.Z ); var rd = new Vec3( d.X, d.Y, d.Z );
		var n = Plane.Normal;
		float den = Vec3.Dot( n, rd );
		if ( MathF.Abs( den ) < 1e-6f ) return null;
		float t = Vec3.Dot( Plane.Origin - ro, n ) / den;
		if ( t < 0 ) return null;
		return Plane.ToPlane( ro + rd * t );
	}

	/// <summary>The outline as a feature (lines only, closed), or null under three points.</summary>
	public static SketchFeature? BuildSketch( FaceRef? face, int planeIndex, IReadOnlyList<Vec2> pts, string planeFeatureId = "" )
	{
		if ( pts.Count < 3 ) return null;
		var f = new SketchFeature();
		if ( planeFeatureId.Length > 0 ) f.PlaneFeatureId = planeFeatureId;
		else if ( face is { } fr ) f.Face = fr;
		else f.Plane.Index = Math.Clamp( planeIndex, 0, 2 );
		var idx = pts.Select( p => f.Sketch.AddPoint( p ) ).ToList();
		for ( int i = 0; i < idx.Count; i++ )
			f.Sketch.Add( new SketchLine( idx[i], idx[( i + 1 ) % idx.Count] ) );
		return f;
	}

	public bool Commit()
	{
		var f = BuildSketch( Face, PlaneIndex, Points, PlaneFeatureId );
		if ( f == null ) { GeppettoToolContext.Say( "Sketch: click at least 3 points first." ); return false; }
		bool ok = _doc.AddFeature( f );
		if ( ok ) GeppettoToolContext.Say( $"Sketch: added a {Points.Count}-sided outline. Extrude it with Extrude." );
		if ( GeppettoTools.Active == this ) GeppettoTools.Activate( null );
		return ok;
	}

	public bool Input( Camera3D camera, InputEvent e )
	{
		_camera = camera;
		if ( Editing != null ) return EditInput( camera, e );
		switch ( e )
		{
			case InputEventMouseMotion mm:
			{
				_mouse = mm.Position;
				var (o, d) = GeppettoPicker.Ray( camera, mm.Position );
				if ( Plane == null )
				{
					GeppettoDatumPlanes.Hovered = PlaneUnder( o, d, out _, out _, out _ )?.Id;
					GeppettoDatumPlanes.HoverAt = mm.Position;
					_hover = null;
					LastSnap = default;
				}
				else
				{
					GeppettoDatumPlanes.Hovered = null;
					if ( OnPlane( o, d ) is { } raw )
					{
						LastSnap = Snap( raw, Points, isLine: Points.Count > 0 );
						_hover = LastSnap.Point;
					}
					else { _hover = null; LastSnap = default; }
				}
				GeppettoTools.Redraw();
				return false;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb when !mb.AltPressed:
			{
				var (o, d) = GeppettoPicker.Ray( camera, mb.Position );
				if ( Plane == null ) { ChoosePlane( o, d ); GeppettoTools.Redraw(); return true; }
				if ( OnPlane( o, d ) is not { } raw ) { GeppettoToolContext.Say( "Sketch: that click is edge-on to the plane; orbit and click again." ); return true; }
				var snap = Snap( raw, Points, isLine: Points.Count > 0 );
				if ( Points.Count >= 3 && ( NearFirst( camera, mb.Position ) || snap.SnappedPointIndex == 0 ) ) { Commit(); return true; }
				Points.Add( snap.Point );
				GeppettoTools.Redraw();
				return true;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
				return true;
			case InputEventKey { Pressed: true, Echo: false } k when Plane != null && !k.CtrlPressed && !k.MetaPressed && !k.AltPressed
				&& ToolForKey( k.Keycode, k.ShiftPressed ) is { } tool:
				// A tool key turns the quick outline into a full sketch session (the clicked points become lines).
				BeginNew( tool );
				return true;
			case InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter }:
				Commit();
				return true;
			case InputEventKey { Pressed: true, Keycode: Key.Backspace }:
				if ( Points.Count > 0 ) Points.RemoveAt( Points.Count - 1 );
				else { Plane = null; Face = null; PlaneIndex = -1; PlaneFeatureId = ""; }
				GeppettoTools.Redraw();
				return true;
		}
		return false;
	}

	bool NearFirst( Camera3D cam, Vector2 screen )
	{
		if ( Plane == null || Points.Count == 0 ) return false;
		var w = _ray.ToWorld( Plane.ToWorld( Points[0] ) );
		return !cam.IsPositionBehind( w ) && cam.UnprojectPosition( w ).DistanceTo( screen ) <= SubSelection.EdgePickPixels;
	}

	static readonly Color Amber = new( 1f, .66f, .2f );
	static readonly Color HoverBlue = new( .35f, .75f, 1f );

	public void Draw( Control overlay )
	{
		if ( Editing != null ) { DrawEdit( overlay ); return; }
		var cam = _camera;
		if ( cam != null && GodotObject.IsInstanceValid( cam ) && Plane != null )
		{
			DrawGrid( overlay, cam );
			Vector3 W( Vec2 p ) => _ray.ToWorld( Plane.ToWorld( p ) );
			for ( int i = 0; i + 1 < Points.Count; i++ )
				GeppettoPicker.DrawWorldLine( overlay, cam, W( Points[i] ), W( Points[i + 1] ), Amber, 2.5f );
			if ( Points.Count > 0 && _hover is { } h )
			{
				GeppettoPicker.DrawWorldLine( overlay, cam, W( Points[^1] ), W( h ), HoverBlue, 1.5f );
				if ( Points.Count >= 2 ) GeppettoPicker.DrawWorldLine( overlay, cam, W( h ), W( Points[0] ), HoverBlue with { A = 0.4f }, 1f );
			}
			foreach ( var p in Points )
			{
				var w = W( p );
				if ( !cam.IsPositionBehind( w ) ) overlay.DrawCircle( cam.UnprojectPosition( w ), 4f, Amber );
			}
			DrawSnapMarker( overlay, cam );
		}
		if ( Plane == null )
		{
			// Choosing a plane: one short line, so the planes are the thing to look at.
			GeppettoPicker.DrawHint( overlay, "Sketch: click a plane  ·  Esc to cancel" );
		}
		// With a plane, the mode bar says what to do (ModeStep) and "?" has the keys.
	}

	public void DrawSnapMarker( Control overlay, Camera3D cam )
	{
		if ( Plane == null ) return;
		var snap = LastSnap;

		// 1. Reference geometry (corners & edges)
		if ( Reference is { IsEmpty: false } r )
		{
			for ( int i = 0; i < r.Edges.Count; i++ )
			{
				var (a, b) = r.Segment( i );
				bool lit = snap.ReferenceEdgeIndex == i;
				Color col = lit ? new Color( 0.45f, 1f, 0.6f, 1f ) : new Color( 0.35f, 0.85f, 0.5f, 0.55f );
				GeppettoPicker.DrawWorldLine( overlay, cam, _ray.ToWorld( Plane.ToWorld( a ) ), _ray.ToWorld( Plane.ToWorld( b ) ), col, lit ? 3f : 1.5f );
			}
			for ( int i = 0; i < r.Points.Count; i++ )
			{
				var w = _ray.ToWorld( Plane.ToWorld( r.Points[i] ) );
				if ( !cam.IsPositionBehind( w ) )
				{
					var sp = cam.UnprojectPosition( w );
					bool lit = snap.ReferencePointIndex == i;
					overlay.DrawCircle( sp, lit ? 6f : 3.5f, lit ? new Color( 0.45f, 1f, 0.6f, 1f ) : new Color( 0.35f, 0.85f, 0.5f, 0.55f ) );
				}
			}
		}

		// 2. Alignment inference guides
		if ( snap.InferenceAxis != 0 )
		{
			Vec2? start = Editing != null ? ( ChainLast >= 0 ? S.Points[ChainLast] : _pending.Count > 0 ? _pending[0] : null ) : Points.Count > 0 ? Points[^1] : null;
			if ( start is { } st )
			{
				float size = 100f;
				if ( ( snap.InferenceAxis & 1 ) != 0 ) // Vertical
				{
					var pA = _ray.ToWorld( Plane.ToWorld( new Vec2( st.x, st.y - size ) ) );
					var pB = _ray.ToWorld( Plane.ToWorld( new Vec2( st.x, st.y + size ) ) );
					GeppettoPicker.DrawWorldLine( overlay, cam, pA, pB, HoverBlue with { A = 0.7f }, 1.5f );
				}
				if ( ( snap.InferenceAxis & 2 ) != 0 ) // Horizontal
				{
					var pA = _ray.ToWorld( Plane.ToWorld( new Vec2( st.x - size, st.y ) ) );
					var pB = _ray.ToWorld( Plane.ToWorld( new Vec2( st.x + size, st.y ) ) );
					GeppettoPicker.DrawWorldLine( overlay, cam, pA, pB, HoverBlue with { A = 0.7f }, 1.5f );
				}
			}
		}

		// 3. Snapped point / curve ring
		if ( snap.SnappedPointIndex >= 0 )
		{
			var pt = Editing != null ? ( snap.SnappedPointIndex < S.Points.Count ? S.Points[snap.SnappedPointIndex] : Vec2.Zero )
				: ( snap.SnappedPointIndex < Points.Count ? Points[snap.SnappedPointIndex] : Vec2.Zero );
			var w = _ray.ToWorld( Plane.ToWorld( pt ) );
			if ( !cam.IsPositionBehind( w ) )
				overlay.DrawArc( cam.UnprojectPosition( w ), 8f, 0, Mathf.Tau, 24, Picked, 2f, antialiased: true );
		}
		else if ( snap.SnappedCurveIndex >= 0 && _hover is { } h )
		{
			var w = _ray.ToWorld( Plane.ToWorld( h ) );
			if ( !cam.IsPositionBehind( w ) )
				overlay.DrawArc( cam.UnprojectPosition( w ), 7f, 0, Mathf.Tau, 24, HoverBlue, 2f, antialiased: true );
		}
		else if ( snap.ReferencePointIndex >= 0 && Reference != null && snap.ReferencePointIndex < Reference.Points.Count )
		{
			var w = _ray.ToWorld( Plane.ToWorld( Reference.Points[snap.ReferencePointIndex] ) );
			if ( !cam.IsPositionBehind( w ) )
				overlay.DrawArc( cam.UnprojectPosition( w ), 8f, 0, Mathf.Tau, 24, new Color( 0.45f, 1f, 0.6f, 1f ), 2f, antialiased: true );
		}
	}
}
#endif
