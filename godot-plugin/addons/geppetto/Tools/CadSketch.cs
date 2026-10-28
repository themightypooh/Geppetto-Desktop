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
public sealed class CadSketch : IGeppettoViewportTool
{
	public string Name => "Sketch";
	readonly GeppettoDocument _doc;
	readonly GeppettoPicker _ray; // coordinates + face hits

	public FaceRef? Face { get; private set; }
	public int PlaneIndex { get; private set; } = -1;
	public SketchPlane? Plane { get; private set; }
	public List<Vec2> Points { get; } = new();

	Camera3D? _camera;
	Vector2 _mouse;
	Vec2? _hover;

	public CadSketch( GeppettoDocument doc )
	{
		_doc = doc;
		_ray = new GeppettoPicker( "Sketch", doc, PickKinds.Faces, "" );
	}

	public void Start( GeppettoCharacter character )
	{
		_ray.BodyTransform = GeppettoPicker.BodyTransformOf( character.Root );
		GeppettoTools.Activate( this );
	}

	public void Exit() { }

	static readonly SketchPlane[] Globals = { SketchPlane.XY, SketchPlane.XZ, SketchPlane.YZ };
	static readonly string[] GlobalNames = { "Top (XY)", "Front (XZ)", "Right (YZ)" };

	/// <summary>Chooses the plane from a ray: the face it hits, else the global plane most square to it.</summary>
	public void ChoosePlane( Vector3 origin, Vector3 dir )
	{
		var (o, d) = _ray.RayToEffigy( origin, dir );
		if ( _ray.Selection.TryPickFace( o, d, out var body, out var hit ) )
		{
			var face = FacePlane.Capture( body, hit.FaceIndex, hit.Point );
			if ( FacePlane.TryResolve( _doc.Studio.Bodies, face, out var plane ) )
			{
				Face = face; Plane = plane; PlaneIndex = 0;
				GeppettoToolContext.Say( "Sketch: on the picked face. Click points; click the first point or press Enter to close." );
				return;
			}
		}
		var ed = new Vec3( d.X, d.Y, d.Z );
		int best = 0; float bestDot = -1;
		for ( int i = 0; i < Globals.Length; i++ )
		{
			float dot = MathF.Abs( Vec3.Dot( Globals[i].Normal, ed ) );
			if ( dot > bestDot ) { bestDot = dot; best = i; }
		}
		Face = null; Plane = Globals[best]; PlaneIndex = best;
		GeppettoToolContext.Say( $"Sketch: on the {GlobalNames[best]} plane. Click points; click the first point or press Enter to close." );
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
	public static SketchFeature? BuildSketch( FaceRef? face, int planeIndex, IReadOnlyList<Vec2> pts )
	{
		if ( pts.Count < 3 ) return null;
		var f = new SketchFeature();
		if ( face is { } fr ) f.Face = fr;
		else f.Plane.Index = Math.Clamp( planeIndex, 0, 2 );
		var idx = pts.Select( p => f.Sketch.AddPoint( p ) ).ToList();
		for ( int i = 0; i < idx.Count; i++ )
			f.Sketch.Add( new SketchLine( idx[i], idx[( i + 1 ) % idx.Count] ) );
		return f;
	}

	public bool Commit()
	{
		var f = BuildSketch( Face, PlaneIndex, Points );
		if ( f == null ) { GeppettoToolContext.Say( "Sketch: click at least 3 points first." ); return false; }
		bool ok = _doc.AddFeature( f );
		if ( ok ) GeppettoToolContext.Say( $"Sketch: added a {Points.Count}-sided outline. Extrude it with Extrude." );
		if ( GeppettoTools.Active == this ) GeppettoTools.Activate( null );
		return ok;
	}

	public bool Input( Camera3D camera, InputEvent e )
	{
		_camera = camera;
		switch ( e )
		{
			case InputEventMouseMotion mm:
			{
				_mouse = mm.Position;
				var (o, d) = GeppettoPicker.Ray( camera, mm.Position );
				_hover = OnPlane( o, d );
				GeppettoTools.Redraw();
				return false;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb when !mb.AltPressed:
			{
				var (o, d) = GeppettoPicker.Ray( camera, mb.Position );
				if ( Plane == null ) { ChoosePlane( o, d ); GeppettoTools.Redraw(); return true; }
				if ( OnPlane( o, d ) is not { } p ) { GeppettoToolContext.Say( "Sketch: that click is edge-on to the plane; orbit and click again." ); return true; }
				if ( Points.Count >= 3 && NearFirst( camera, mb.Position ) ) { Commit(); return true; }
				Points.Add( p );
				GeppettoTools.Redraw();
				return true;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
				return true;
			case InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter }:
				Commit();
				return true;
			case InputEventKey { Pressed: true, Keycode: Key.Backspace }:
				if ( Points.Count > 0 ) Points.RemoveAt( Points.Count - 1 );
				else { Plane = null; Face = null; PlaneIndex = -1; }
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
		var cam = _camera;
		if ( cam != null && GodotObject.IsInstanceValid( cam ) && Plane != null )
		{
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
		}
		string where = Plane == null ? "click a face, or empty space for the plane facing you"
			: Points.Count < 3 ? $"click points ({Points.Count} so far, 3 needed)"
			: $"{Points.Count} points: click the first point or press Enter to close";
		GeppettoPicker.DrawHint( overlay, $"Sketch: {where}\nBackspace removes the last point, Esc cancels." );
	}
}
#endif
