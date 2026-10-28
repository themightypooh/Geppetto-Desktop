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

/// <summary>What a <see cref="GeppettoPicker"/> may take.</summary>
[Flags]
public enum PickKinds
{
	Faces = 1,
	/// <summary>Rim edges: a click within <see cref="SubSelection.EdgePickPixels"/> of a face's outline takes the edge.</summary>
	Edges = 2,
	/// <summary>Closed regions of sketches already in the tree (Extrude's profile).</summary>
	Regions = 4,
}

/// <summary>
/// Face / edge / sketch-region picking in the 3D viewport for the CAD tools, the way s&amp;box's idle
/// selection works (it is <see cref="SubSelection"/>, the port of EffigyViewport.Selection.cs, driven
/// from Godot's camera): hover lights the surface or rim edge under the cursor, a click picks it,
/// Shift adds / toggles, Ctrl removes, Enter confirms, Esc cancels (the plugin's Esc). The result is
/// kernel references (FaceRef / EdgeRef / body ids / a sketch region) ready for
/// <see cref="Feature.ApplyGeometrySelection"/>.
///
/// Rays come in Godot world space and are taken into the character's Effigy space through the Body
/// mesh's global transform and the bridge's axis swap (Godot = 0.0254 * (y, z, x) of Effigy).
/// </summary>
public sealed class GeppettoPicker : IGeppettoViewportTool
{
	public string Name { get; }
	public GeppettoDocument Doc { get; }
	public PickKinds Kinds { get; }
	public SubSelection Selection { get; } = new() { Mode = SubMode.Face };

	/// <summary>Effigy (Godot-axis, metres) → Godot world: the Body mesh's global transform.</summary>
	public Transform3D BodyTransform { get; set; } = Transform3D.Identity;

	/// <summary>The prompt shown over the viewport.</summary>
	public string Prompt { get; set; }

	/// <summary>Enter. Return false to keep picking (e.g. after warning that nothing is picked).</summary>
	public Func<GeppettoPicker, bool>? Confirmed { get; set; }

	/// <summary>Called when the picker stops for any reason (confirm, Esc, another tool).</summary>
	public Action? Stopped { get; set; }

	// The sketch region picked (Extrude), or the one under the cursor.
	public string? RegionSketchId { get; private set; }
	public List<Vec2> RegionSeeds { get; } = new();
	(string Id, Vec2 Seed, Profile Profile, SketchPlane Plane)? _regionHover;
	readonly List<(string Id, Profile Profile, SketchPlane Plane)> _regionPicked = new();

	Camera3D? _camera;
	Vector2 _mouse;
	bool _listening;

	public GeppettoPicker( string name, GeppettoDocument doc, PickKinds kinds, string prompt )
	{
		Name = name;
		Doc = doc;
		Kinds = kinds;
		Prompt = prompt;
		Selection.Load( doc.Studio.Bodies );
	}

	/// <summary>Makes this the active viewport tool for a character: picks on its Body mesh.</summary>
	public void Start( GeppettoCharacter character )
	{
		BodyTransform = BodyTransformOf( character.Root );
		if ( !_listening ) { Doc.Changed += OnDocChanged; _listening = true; }
		GeppettoTools.Activate( this );
	}

	public static Transform3D BodyTransformOf( Node root )
	{
		if ( root.FindChild( "Body", true, false ) is Node3D body && body.IsInsideTree() ) return body.GlobalTransform;
		return root is Node3D n && n.IsInsideTree() ? n.GlobalTransform : Transform3D.Identity;
	}

	void OnDocChanged() => Selection.Reload( Doc.Studio.Bodies );

	public void Exit()
	{
		if ( _listening ) { Doc.Changed -= OnDocChanged; _listening = false; }
		Stopped?.Invoke();
	}

	public bool IsEmpty => Selection.Faces.Count == 0 && Selection.Edges.Count == 0 && RegionSketchId == null;
	public IReadOnlyList<FaceRef> Faces => Selection.Faces;
	public IReadOnlyList<EdgeRef> Edges => Selection.Edges;

	public string Summary()
	{
		var parts = new List<string>();
		if ( Selection.Faces.Count > 0 ) parts.Add( Plural( Selection.Faces.Count, "face", "faces" ) );
		if ( Selection.Edges.Count > 0 ) parts.Add( Plural( Selection.Edges.Count, "edge", "edges" ) );
		if ( RegionSketchId != null ) parts.Add( Plural( RegionSeeds.Count, "sketch region", "sketch regions" ) );
		return parts.Count == 0 ? "nothing picked" : string.Join( ", ", parts ) + " picked";
	}

	static string Plural( int n, string one, string many ) => $"{n} {( n == 1 ? one : many )}";

	// =========================================================================================
	// Coordinates
	// =========================================================================================

	/// <summary>Godot local (metres, Y up) → Effigy (inches, Z up). Inverse of EffigyMeshBridge.TransformPosition.</summary>
	public static NVec3 ToEffigy( Vector3 g ) =>
		new NVec3( g.Z, g.X, g.Y ) / EffigyMeshBridge.ScaleToMetres;

	/// <summary>Godot local direction → Effigy direction (normalised).</summary>
	public static NVec3 DirToEffigy( Vector3 g ) => NVec3.Normalize( new NVec3( g.Z, g.X, g.Y ) );

	public Vector3 ToWorld( Vec3 e ) => BodyTransform * EffigyMeshBridge.TransformPosition( e.x, e.y, e.z );

	/// <summary>A Godot world ray as an Effigy ray.</summary>
	public (NVec3 Origin, NVec3 Dir) RayToEffigy( Vector3 origin, Vector3 dir )
	{
		var inv = BodyTransform.AffineInverse();
		var o = inv * origin;
		var d = inv.Basis * dir;
		return ( ToEffigy( o ), DirToEffigy( d ) );
	}

	// =========================================================================================
	// Picking (camera-free: the headless test drives these directly)
	// =========================================================================================

	/// <summary>What a click along this Godot world ray would take. <paramref name="edgeRadius"/>
	/// is the rim-snap distance in Effigy units at a hit point (null: never snap to an edge).</summary>
	public SubTarget? PickRay( Vector3 origin, Vector3 dir, Func<NVec3, float>? edgeRadius = null )
	{
		if ( ( Kinds & ( PickKinds.Faces | PickKinds.Edges ) ) == 0 ) return null;
		var (o, d) = RayToEffigy( origin, dir );
		if ( ( Kinds & PickKinds.Faces ) == 0 )
			return Selection.Pick( o, d, SubMode.Edge );
		return Selection.Pick( o, d, SubMode.Face, ( Kinds & PickKinds.Edges ) != 0 ? edgeRadius : null );
	}

	/// <summary>The sketch region along a ray, if it is nearer than <paramref name="faceDistance"/>
	/// (a sketch drawn on a face wins over that face).</summary>
	public (string Id, Vec2 Seed, Profile Profile, SketchPlane Plane)? PickRegion( Vector3 origin, Vector3 dir, float faceDistance = float.MaxValue )
	{
		if ( ( Kinds & PickKinds.Regions ) == 0 ) return null;
		var (o, d) = RayToEffigy( origin, dir );
		var ro = new Vec3( o.X, o.Y, o.Z );
		var rd = new Vec3( d.X, d.Y, d.Z );
		(string, Vec2, Profile, SketchPlane)? best = null;
		float bestT = faceDistance + 1e-3f;
		foreach ( var sketch in Doc.Studio.Features.OfType<SketchFeature>() )
		{
			if ( sketch.Suppressed || sketch.Error != null ) continue;
			var plane = sketch.Sketch.Plane;
			var n = plane.Normal;
			float den = Vec3.Dot( n, rd );
			if ( MathF.Abs( den ) < 1e-6f ) continue;
			float t = Vec3.Dot( plane.Origin - ro, n ) / den;
			if ( t < 0 || t > bestT ) continue;
			var p2 = plane.ToPlane( ro + rd * t );
			var profiles = ProfileFinder.Find( sketch.Sketch ).Profiles;
			var hit = ProfileFinder.SmallestContaining( profiles, p2 );
			if ( hit == null ) continue;
			bestT = t;
			best = ( sketch.Id, p2, hit, plane );
		}
		return best;
	}

	/// <summary>A click along a ray: Shift adds / toggles, Ctrl removes, a plain click replaces.</summary>
	public void ClickRay( Vector3 origin, Vector3 dir, bool shift, bool ctrl, Func<NVec3, float>? edgeRadius = null )
	{
		var target = PickRay( origin, dir, edgeRadius );
		float faceT = float.MaxValue;
		if ( target is { } t )
		{
			var (o, _) = RayToEffigy( origin, dir );
			faceT = ( new NVec3( t.Point.x, t.Point.y, t.Point.z ) - o ).Length();
		}
		if ( PickRegion( origin, dir, faceT ) is { } region )
		{
			ClickRegion( region, shift, ctrl );
			return;
		}
		// A face or edge replaces a region pick (a selection is one kind of thing).
		if ( target != null && RegionSketchId != null && !ctrl ) ClearRegions();
		Selection.Click( target, add: shift, remove: ctrl );
		if ( target == null && !shift && !ctrl ) ClearRegions();
		if ( Selection.Notice is { } notice ) GeppettoToolContext.Say( notice );
	}

	void ClickRegion( (string Id, Vec2 Seed, Profile Profile, SketchPlane Plane) r, bool shift, bool ctrl )
	{
		int existing = _regionPicked.FindIndex( p => p.Id == r.Id && p.Profile.Contains( r.Seed ) );
		if ( ctrl || ( shift && existing >= 0 ) )
		{
			if ( existing >= 0 ) { _regionPicked.RemoveAt( existing ); RegionSeeds.RemoveAt( existing ); }
			if ( RegionSeeds.Count == 0 ) RegionSketchId = null;
			return;
		}
		// Regions of one sketch only: an Extrude reads one sketch.
		if ( !shift || RegionSketchId != r.Id ) ClearRegions();
		Selection.Clear();
		RegionSketchId = r.Id;
		RegionSeeds.Add( r.Seed );
		_regionPicked.Add( ( r.Id, r.Profile, r.Plane ) );
	}

	void ClearRegions() { RegionSketchId = null; RegionSeeds.Clear(); _regionPicked.Clear(); }

	public void Clear() { Selection.Clear(); ClearRegions(); }

	/// <summary>Enter: hands the pick to the tool. The tool decides whether picking ends.</summary>
	public void Confirm()
	{
		bool done = Confirmed?.Invoke( this ) ?? true;
		if ( done && GeppettoTools.Active == this ) GeppettoTools.Activate( null );
	}

	// =========================================================================================
	// Viewport
	// =========================================================================================

	public bool Input( Camera3D camera, InputEvent e )
	{
		_camera = camera;
		switch ( e )
		{
			case InputEventMouseMotion mm:
			{
				_mouse = mm.Position;
				var (o, d) = Ray( camera, mm.Position );
				var hover = PickRay( o, d, EdgeRadius( camera ) );
				float faceT = float.MaxValue;
				if ( hover is { } h ) { var (eo, _) = RayToEffigy( o, d ); faceT = ( new NVec3( h.Point.x, h.Point.y, h.Point.z ) - eo ).Length(); }
				_regionHover = PickRegion( o, d, faceT );
				Selection.SetHover( _regionHover != null ? null : hover );
				GeppettoTools.Redraw();
				return false; // the camera still orbits / pans
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb when !mb.AltPressed:
			{
				var (o, d) = Ray( camera, mb.Position );
				ClickRay( o, d, mb.ShiftPressed, mb.CtrlPressed || mb.MetaPressed, EdgeRadius( camera ) );
				return true;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
				return true;
			case InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter }:
				Confirm();
				return true;
		}
		return false;
	}

	public static (Vector3 Origin, Vector3 Dir) Ray( Camera3D camera, Vector2 screen )
	{
		var p = ToViewport( camera, screen );
		return ( camera.ProjectRayOrigin( p ), camera.ProjectRayNormal( p ) );
	}

	// The overlay and the 3D viewport can differ in size (half-resolution / 3D scaling).
	static Vector2 ToViewport( Camera3D camera, Vector2 overlayPos ) => overlayPos;

	/// <summary>EdgePickPixels as a distance in Effigy units at a hit point.</summary>
	Func<NVec3, float> EdgeRadius( Camera3D camera ) => hit =>
	{
		var world = ToWorld( new Vec3( hit.X, hit.Y, hit.Z ) );
		float h = Math.Max( 1f, camera.GetViewport().GetVisibleRect().Size.Y );
		float perPixel = camera.Projection == Camera3D.ProjectionType.Orthogonal
			? camera.Size / h
			: 2f * camera.GlobalPosition.DistanceTo( world ) * MathF.Tan( Mathf.DegToRad( camera.Fov ) * 0.5f ) / h;
		float scale = Math.Max( 1e-6f, BodyTransform.Basis.Scale.Length() / MathF.Sqrt( 3f ) );
		return perPixel * SubSelection.EdgePickPixels / ( EffigyMeshBridge.ScaleToMetres * scale );
	};

	static readonly Color Amber = new( 1f, .66f, .2f );
	static readonly Color HoverBlue = new( .35f, .75f, 1f );

	public void Draw( Control overlay )
	{
		var cam = _camera;
		if ( cam != null && GodotObject.IsInstanceValid( cam ) )
		{
			foreach ( var r in Selection.Resolved ) DrawTarget( overlay, cam, r, Amber, 3f );
			foreach ( var r in _regionPicked ) DrawLoop( overlay, cam, r.Profile.Outer, r.Plane, Amber, 3f );
			if ( Selection.Hover is { } h ) DrawTarget( overlay, cam, h, HoverBlue, 2f );
			if ( _regionHover is { } rh ) DrawLoop( overlay, cam, rh.Profile.Outer, rh.Plane, HoverBlue, 2f );
		}
		DrawHint( overlay, $"{Name}: {Prompt}\n{Summary()}. Shift adds, Ctrl removes, Enter confirms, Esc cancels." );
	}

	void DrawTarget( Control overlay, Camera3D cam, SubTarget t, Color c, float width )
	{
		if ( t.Mesh is null ) return;
		switch ( t.Kind )
		{
			case SubMode.Face:
				var surface = Selection.SurfaceOf( t.Mesh, t.Face );
				if ( surface.IsEmpty )
				{
					var idx = t.Mesh.Faces[t.Face].Indices;
					for ( int i = 0; i < idx.Length; i++ )
						Line( overlay, cam, t.Mesh.Positions[idx[i]], t.Mesh.Positions[idx[( i + 1 ) % idx.Length]], c, width );
				}
				else
					for ( int i = 0; i < surface.Boundary.Count; i++ )
					{
						var (a, b) = surface.Segment( i );
						Line( overlay, cam, a, b, c, width );
					}
				break;
			case SubMode.Edge:
				if ( t.Edge.A < t.Mesh.Positions.Count && t.Edge.B < t.Mesh.Positions.Count )
					Line( overlay, cam, t.Mesh.Positions[t.Edge.A], t.Mesh.Positions[t.Edge.B], c, width + 1.5f );
				break;
		}
	}

	void DrawLoop( Control overlay, Camera3D cam, List<Vec2> loop, SketchPlane plane, Color c, float width )
	{
		for ( int i = 0; i < loop.Count; i++ )
			Line( overlay, cam, plane.ToWorld( loop[i] ), plane.ToWorld( loop[( i + 1 ) % loop.Count] ), c, width );
	}

	void Line( Control overlay, Camera3D cam, Vec3 a, Vec3 b, Color c, float width ) =>
		DrawWorldLine( overlay, cam, ToWorld( a ), ToWorld( b ), c, width );

	public static void DrawWorldLine( Control overlay, Camera3D cam, Vector3 a, Vector3 b, Color c, float width )
	{
		if ( cam.IsPositionBehind( a ) || cam.IsPositionBehind( b ) ) return;
		overlay.DrawLine( cam.UnprojectPosition( a ), cam.UnprojectPosition( b ), c, width, true );
	}

	/// <summary>The tool's prompt over the viewport: a dark box sized to the text, which wraps to the
	/// overlay's width rather than running off it.</summary>
	public static void DrawHint( Control overlay, string text )
	{
		var font = overlay.GetThemeDefaultFont();
		int size = overlay.GetThemeDefaultFontSize();
		float maxW = Math.Max( 120f, Math.Min( 640f, overlay.Size.X - 32f ) );
		var ts = font.GetMultilineStringSize( text, HorizontalAlignment.Left, maxW, size );
		var pos = new Vector2( Math.Max( 16f, ( overlay.Size.X - ts.X ) * 0.5f ), 44f );
		overlay.DrawRect( new Rect2( pos - new Vector2( 8, 6 ), ts + new Vector2( 16, 12 ) ), new Color( 0.08f, 0.09f, 0.11f, 0.85f ) );
		overlay.DrawMultilineString( font, pos + new Vector2( 0, font.GetAscent( size ) ), text, HorizontalAlignment.Left, maxW, size, -1, new Color( 0.9f, 0.91f, 0.93f ) );
	}
}
#endif
