#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

// The CAD workspace's parametric tools: every dial button that s&box's NewFeature(ToolKind) turns into a
// plain feature plus its settings box. Face/edge picking tools (Fillet, Extrude, Sketch …) live in
// CadPick*.cs. Ported from EffigyWindow.NewFeature / AddFeature (~/games/projects/geppetto/Editor/EffigyEditor).
public static partial class GeppettoTools
{
	static partial void RegisterCad()
	{
		foreach ( var tool in GeppettoCad.Tools )
		{
			string name = tool;
			Register( "CAD", name, ctx => ctx.Doc is { } doc ? GeppettoCad.Run( doc, name, ctx.Character ) : ctx.Need() );
		}
	}
}

/// <summary>
/// The CAD tools that make one kernel feature and open its settings box, and the selection they consume
/// (the bodies / sketch picked in the Features dock's Parts list, like s&amp;box's idle selection).
/// </summary>
public static class GeppettoCad
{
	/// <summary>Dial names (after <c>GeppettoDialData.Clean</c>) this file handles.</summary>
	public static readonly string[] Tools =
	{
		"Primitive", "Import", "Profile", "Part", "Revolve", "Sweep", "Loft", "Spline", "Boolean",
		"Mirror", "Linear Pattern", "Circular Pattern", "Transform", "Bend Along Curve", "Simple Deform", "Lattice",
	};

	/// <summary>Bodies picked in the Parts list. A new feature with body inputs takes these, the way s&amp;box's
	/// tools consume the idle selection instead of asking again.</summary>
	public static readonly List<string> SelectedBodyIds = new();

	/// <summary>The sketch (or spline) feature selected in the feature list, used as the profile / curve.</summary>
	public static string? SelectedFeatureId { get; set; }

	// =========================================================================================
	// Building the feature (s&box NewFeature)
	// =========================================================================================

	/// <summary>The same feature s&amp;box's toolbar builds for this tool, with the same defaults.
	/// <paramref name="choice"/> is the dropdown variant (Primitive shape, Boolean operation), -1 for none.</summary>
	public static Feature? NewFeature( string tool, int choice = -1 ) => tool switch
	{
		"Primitive" => NewPrimitive( choice ),
		"Import" => new ImportFeature(),
		"Profile" => new ProfileFeature(),
		"Part" => new PartFeature(),
		"Revolve" => AwaitingPick( NewRevolve() ),
		"Sweep" => new SweepFeature(),
		"Loft" => new LoftFeature(),
		"Spline" => new SplineFeature(),
		"Boolean" => NewBoolean( choice ),
		"Mirror" => new MirrorFeature(),
		"Linear Pattern" => new LinearPatternFeature(),
		"Circular Pattern" => new CircularPatternFeature(),
		"Transform" => new TransformFeature(),
		"Bend Along Curve" => new CurveDeformFeature(),
		"Simple Deform" => new SimpleDeformFeature(),
		"Lattice" => new LatticeFeature(),
		_ => null,
	};

	static T AwaitingPick<T>( T feature ) where T : SketchConsumingFeature
	{
		feature.SketchFeatureId = SketchConsumingFeature.AwaitingPick;
		return feature;
	}

	/// <summary>A revolve made from the toolbar spins about its profile's left edge, like a lathe profile.</summary>
	static RevolveFeature NewRevolve()
	{
		var feature = new RevolveFeature();
		feature.AxisMode.Index = RevolveFeature.AxisProfileLeftEdge;
		return feature;
	}

	static BooleanFeature NewBoolean( int operation )
	{
		var feature = new BooleanFeature();
		if ( operation >= 0 ) feature.Operation.Index = operation;
		return feature;
	}

	static PrimitiveFeature NewPrimitive( int shape )
	{
		var feature = new PrimitiveFeature();
		if ( shape >= 0 ) feature.Shape.Index = shape;
		return feature;
	}

	/// <summary>Variants behind a button (s&amp;box opens a menu instead of adding straight away).</summary>
	public static string[]? Choices( string tool ) => tool switch
	{
		"Primitive" => new PrimitiveFeature().Shape.Options,
		"Boolean" => new BooleanFeature().Operation.Options,
		_ => null,
	};

	// =========================================================================================
	// Running a tool
	// =========================================================================================

	public static bool Run( GeppettoDocument doc, string tool, GeppettoCharacter? character )
	{
		if ( Choices( tool ) is { } options )
		{
			Menu( $"{tool}:", options, i => Begin( doc, tool, i, character ) );
			return true;
		}
		Begin( doc, tool, -1, character );
		return true;
	}

	static void Begin( GeppettoDocument doc, string tool, int choice, GeppettoCharacter? character )
	{
		if ( NewFeature( tool, choice ) is not { } feature )
		{
			GeppettoToolContext.Warn( $"{tool} has no feature to make." );
			return;
		}

		if ( feature is ImportFeature import )
		{
			PickImportFile( path =>
			{
				string? note = BindImport( doc, import, path );
				Open( doc, feature, character );
				if ( note != null ) GeppettoToolContext.Say( note );
			} );
			return;
		}

		if ( feature is RevolveFeature revolve )
		{
			ChooseSketch( doc, "Revolve", id => { revolve.SketchFeatureId = id; Open( doc, feature, character ); } );
			return;
		}

		if ( Prepare( doc, feature ) is { } refusal )
		{
			GeppettoToolContext.Warn( $"{refusal}" );
			return;
		}
		Open( doc, feature, character );
	}

	/// <summary>
	/// Seeds a new feature from the current selection and checks it has what it needs. Returns null when
	/// it can be opened, else why not (said instead of adding a feature that can only fail). Shared with
	/// the headless test so it exercises the same path as the dial.
	/// </summary>
	public static string? Prepare( GeppettoDocument doc, Feature feature )
	{
		var studio = doc.Studio;
		var sketches = studio.Features.OfType<SketchFeature>().ToList();
		var splines = studio.Features.OfType<SplineFeature>().ToList();
		var bodies = SelectedBodyIds.Where( id => studio.Bodies.Any( b => b.Id == id ) ).ToList();

		switch ( feature )
		{
			case SweepFeature sweep:
				if ( sketches.Count == 0 )
					return "Sweep needs a profile: draw a Sketch first, then a path (a Spline or a second sketch).";
				if ( splines.Count == 0 && sketches.Count < 2 )
					return "Sweep needs a path to follow: draw a Spline (or a second sketch) first.";
				if ( SelectedSketch( doc ) is { } profile ) sweep.SketchFeatureId = profile;
				break;
			case LoftFeature:
				if ( sketches.Count < 2 )
					return "Loft needs at least two sketches to blend between - draw them first.";
				break;
			case CurveDeformFeature bend:
				if ( splines.Count == 0 )
					return "Bend Along Curve needs a curve: draw a Spline first.";
				if ( SelectedFeatureId is { } sid && splines.Any( s => s.Id == sid ) ) bend.SplineId = sid;
				break;
			case PrimitiveFeature primitive:
				FitToModel( studio, primitive );
				break;
			case BooleanFeature boolean:
				if ( studio.Bodies.Count < 2 )
					return "Boolean needs two solids - add a Primitive or another part first.";
				if ( bodies.Count == 0 )
					return "Boolean needs the tool body: pick it in the Parts list (Geppetto Features dock) first. " +
						"Pick two or more to choose the target too (the last one picked cuts the others).";
				break;
		}

		if ( bodies.Count > 0 )
			feature.ApplyGeometrySelection( Array.Empty<FaceRef>(), bodies, studio.Bodies );

		// A boolean's last pick is the tool and the rest are the targets; one pick is the tool alone and
		// the target defaults to everything else (BooleanFeature's own reading of an empty target).
		if ( feature is BooleanFeature b2 && bodies.Count > 0 )
		{
			b2.Tools.BodyIds.Clear();
			b2.Tools.BodyIds.Add( bodies[^1] );
			b2.Targets.BodyIds.Clear();
			b2.Targets.BodyIds.AddRange( bodies.Take( bodies.Count - 1 ) );
		}
		return null;
	}

	/// <summary>
	/// Sizes a new primitive to the model and stands it beside it. The feature's own defaults are 1u
	/// (one inch), which on a character dozens of units tall is a speck buried inside the body, so
	/// adding one looked like nothing happened. Empty documents get a 12u (one foot) shape at the origin.
	/// </summary>
	public static void FitToModel( PartStudio studio, PrimitiveFeature primitive )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var body in studio.Bodies )
		{
			if ( !body.Visible || body.Mesh is null ) continue;
			foreach ( var p in body.Mesh.Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
		}
		bool empty = min.x > max.x;
		float extent = empty ? 0 : MathF.Max( max.x - min.x, MathF.Max( max.y - min.y, max.z - min.z ) );
		float size = empty ? 12f : MathF.Max( 1f, MathF.Round( extent / 4f ) );
		foreach ( var f in new[] { primitive.SizeX, primitive.SizeY, primitive.SizeZ, primitive.Radius, primitive.InnerRadius, primitive.TubeRadius } )
			f.Value *= size;
		if ( !empty )
			primitive.Position.Value = new Vec3( max.x + size, ( min.y + max.y ) / 2f, min.z + size / 2f );
	}

	static string? SelectedSketch( GeppettoDocument doc ) =>
		SelectedFeatureId is { } id && doc.Studio.Features.OfType<SketchFeature>().Any( s => s.Id == id ) ? id : null;

	/// <summary>Points a profile-consuming feature at one sketch: the one selected in the feature list, the
	/// only one, or the one picked from a menu.</summary>
	static void ChooseSketch( GeppettoDocument doc, string tool, Action<string> chosen )
	{
		var sketches = doc.Studio.Features.OfType<SketchFeature>().ToList();
		if ( sketches.Count == 0 ) { GeppettoToolContext.Warn( $"{tool} needs a profile: draw a Sketch first." ); return; }
		if ( SelectedSketch( doc ) is { } id ) { chosen( id ); return; }
		if ( sketches.Count == 1 ) { chosen( sketches[0].Id ); return; }
		Menu( $"{tool} which sketch?", sketches.Select( s => s.Name ?? s.TypeName ).ToArray(), i => chosen( sketches[i].Id ) );
	}

	static GeppettoFeatureDialog? _pending;

	/// <summary>Opens the new feature's settings box (preview + Add / Cancel as one undo step).</summary>
	static void Open( GeppettoDocument doc, Feature feature, GeppettoCharacter? character )
	{
		// Pressing the same tool while its box is still open goes back to that box instead of stacking another.
		if ( _pending != null && GodotObject.IsInstanceValid( _pending ) && _pending.Visible && _pending.Feature.GetType() == feature.GetType() )
		{
			_pending.GrabFocus();
			GeppettoToolContext.Say( $"{_pending.Feature.Name ?? _pending.Feature.TypeName} is already open - finish it with Add, or cancel it, before adding another." );
			return;
		}

		GeppettoCadSplineDraw? draw = null;
		var dialog = GeppettoFeatureDialog.ShowNew( doc, feature, null, ok =>
		{
			if ( draw != null && GeppettoTools.Active == draw ) GeppettoTools.Activate( null );
		} );
		_pending = dialog;

		// A spline is drawn, not typed: its box opening arms the viewport (s&box BeginSplineDraw).
		if ( feature is SplineFeature spline && character != null )
		{
			draw = new GeppettoCadSplineDraw( doc, spline, character.Root, dialog );
			GeppettoTools.Activate( draw );
			GeppettoToolContext.Say( "Spline: click to drop points - on a body, or in the air. Drag a point to move it, " +
				"Backspace takes the last one back, Enter or Add finishes." );
		}
	}

	// =========================================================================================
	// Import
	// =========================================================================================

	/// <summary>Points an Import at a mesh file and installs an FBX's skeleton (like s&amp;box). Returns a note to show.</summary>
	public static string? BindImport( GeppettoDocument doc, ImportFeature import, string path )
	{
		import.BindSource( path );
		if ( string.IsNullOrWhiteSpace( import.Name ) ) import.Name = Path.GetFileNameWithoutExtension( path );
		if ( !import.IsFbxSource ) return null;
		int bones = ImportFeature.InstallSkeleton( doc.Studio, import, out var message );
		if ( string.IsNullOrEmpty( message ) ) return null;
		if ( bones > 0 )
			message += " Its skin weights ride on the imported parts. Cancelling the Import keeps the bones; delete them in Rig if you don't want them.";
		return $"{import.Name}: {message}";
	}

	static void PickImportFile( Action<string> picked )
	{
		var fd = new EditorFileDialog
		{
			Title = "Import mesh...",
			FileMode = EditorFileDialog.FileModeEnum.OpenFile,
			Access = EditorFileDialog.AccessEnum.Filesystem,
			CurrentDir = ProjectSettings.GlobalizePath( "res://" ),
		};
		fd.AddFilter( "*.obj, *.fbx", "Meshes" );
		fd.AddFilter( "*.obj", "Wavefront OBJ" );
		fd.AddFilter( "*.fbx", "FBX" );
		fd.FileSelected += path => { fd.QueueFree(); if ( !string.IsNullOrWhiteSpace( path ) ) picked( path ); };
		fd.Canceled += () => fd.QueueFree();
		EditorInterface.Singleton.GetBaseControl().AddChild( fd );
		fd.PopupFileDialog();
	}

	// =========================================================================================
	// A small choice menu at the mouse (s&box's dropdown under Primitive / Boolean)
	// =========================================================================================

	static void Menu( string title, string[] options, Action<int> chosen )
	{
		var menu = new PopupMenu();
		menu.AddSeparator( title );
		for ( int i = 0; i < options.Length; i++ ) menu.AddItem( options[i], i );
		bool picked = false;
		menu.IdPressed += id => { if ( picked ) return; picked = true; chosen( (int)id ); };
		menu.PopupHide += () => menu.CallDeferred( Node.MethodName.QueueFree );
		EditorInterface.Singleton.GetBaseControl().AddChild( menu );
		menu.Position = DisplayServer.MouseGetPosition();
		menu.Popup();
	}
}

/// <summary>
/// Drawing a spline in the viewport while its settings box is open (s&amp;box EffigyViewport.Splines): click
/// drops a point on the body under the cursor, else on a plane facing the camera through the last point,
/// else on the ground; drag moves a point; Backspace removes the last; Enter accepts the box.
/// </summary>
public sealed class GeppettoCadSplineDraw : IGeppettoViewportTool
{
	readonly GeppettoDocument _doc;
	readonly SplineFeature _spline;
	readonly Node _root;
	readonly GeppettoFeatureDialog _dialog;
	Camera3D? _camera;
	int _hover = -1, _drag = -1;
	Vec3 _dragOrigin, _dragNormal;
	bool _dragMoved;

	public GeppettoCadSplineDraw( GeppettoDocument doc, SplineFeature spline, Node root, GeppettoFeatureDialog dialog )
	{
		_doc = doc; _spline = spline; _root = root; _dialog = dialog;
	}

	public string Name => "Spline";

	const float M = 0.0254f;

	/// <summary>Character-local Godot space → Effigy (inches, Z-up). Inverse of EffigyMeshBridge.TransformPosition.</summary>
	static Vec3 ToEffigy( Vector3 g ) => new( g.Z / M, g.X / M, g.Y / M );
	static Vector3 ToGodot( Vec3 e ) => new( e.y * M, e.z * M, e.x * M );

	Transform3D Space() =>
		( _root.FindChild( "Body", true, false ) as Node3D ?? _root as Node3D )?.GlobalTransform ?? Transform3D.Identity;

	(Vec3 Origin, Vec3 Dir) Ray( Camera3D cam, Vector2 screen )
	{
		var inv = Space().AffineInverse();
		var o = inv * cam.ProjectRayOrigin( screen );
		var d = ( inv.Basis * cam.ProjectRayNormal( screen ) ).Normalized();
		var eo = ToEffigy( o );
		var ed = ToEffigy( d );
		return ( eo, ed.Normal );
	}

	Vec3 CameraForward( Camera3D cam )
	{
		var f = ( Space().AffineInverse().Basis * -cam.GlobalTransform.Basis.Z ).Normalized();
		return ToEffigy( f ).Normal;
	}

	public bool Input( Camera3D camera, InputEvent e )
	{
		_camera = camera;
		if ( !GodotObject.IsInstanceValid( _dialog ) ) { GeppettoTools.Activate( null ); return false; }
		var points = _spline.Points;
		switch ( e )
		{
			case InputEventKey { Pressed: true, Keycode: Key.Backspace }:
				if ( points.Count > 0 ) { points.RemoveAt( points.Count - 1 ); _hover = -1; Edited(); }
				else GeppettoToolContext.Say( "Spline: no points to take back." );
				return true;
			case InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter }:
				if ( points.Count < 2 ) { GeppettoToolContext.Say( "Spline: drop at least two points first." ); return true; }
				_dialog.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				return true;
			case InputEventMouseMotion mm:
			{
				var (o, d) = Ray( camera, mm.Position );
				if ( _drag >= 0 && _drag < points.Count )
				{
					if ( RayPlane( o, d, _dragOrigin, _dragNormal, out var moved ) ) { points[_drag] = moved; _dragMoved = true; _doc.Studio.MarkDirty( _spline ); _doc.Refresh(); }
					return true;
				}
				int h = HoverPoint( o, d, points );
				if ( h != _hover ) { _hover = h; GeppettoTools.Redraw(); }
				return false;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
			{
				if ( !mb.Pressed )
				{
					if ( _drag < 0 ) return false;
					bool moved = _dragMoved;
					_drag = -1; _dragMoved = false;
					if ( moved ) Edited();
					return true;
				}
				var (o, d) = Ray( camera, mb.Position );
				int hover = HoverPoint( o, d, points );
				if ( hover >= 0 )
				{
					_drag = hover; _dragMoved = false; _dragNormal = CameraForward( camera ); _dragOrigin = points[hover];
					return true;
				}
				if ( Landing( o, d, points, CameraForward( camera ) ) is { } at ) { points.Add( at ); Edited(); }
				else GeppettoToolContext.Say( "Spline: nowhere to put a point there - aim at the model or the ground." );
				return true;
			}
		}
		return false;
	}

	void Edited()
	{
		_doc.Studio.MarkDirty( _spline );
		_doc.Refresh();
		GeppettoTools.Redraw();
	}

	int HoverPoint( Vec3 origin, Vec3 dir, List<Vec3> points )
	{
		int best = -1; float bestAlong = float.MaxValue;
		for ( int i = 0; i < points.Count; i++ )
		{
			var to = points[i] - origin;
			float along = Vec3.Dot( to, dir );
			if ( along <= 0f ) continue;
			float perp = ( to - dir * along ).Length;
			float handle = MathF.Max( 0.15f, along * 0.012f ) * 1.6f;
			if ( perp < handle && along < bestAlong ) { bestAlong = along; best = i; }
		}
		return best;
	}

	Vec3? Landing( Vec3 origin, Vec3 dir, List<Vec3> points, Vec3 cameraForward )
	{
		var visible = _doc.Studio.Bodies.Where( b => b?.Mesh != null && b.Visible ).ToList();
		if ( visible.Count > 0 && MeshRaycast.Raycast( visible, origin, dir ) is { } hit ) return hit.Hit.Point;
		if ( points.Count > 0 && RayPlane( origin, dir, points[^1], cameraForward, out var onView ) ) return onView;
		if ( RayPlane( origin, dir, Vec3.Zero, new Vec3( 0, 0, 1 ), out var onGround ) ) return onGround;
		return null;
	}

	static bool RayPlane( Vec3 origin, Vec3 dir, Vec3 point, Vec3 normal, out Vec3 hit )
	{
		hit = default;
		float den = Vec3.Dot( dir, normal );
		if ( MathF.Abs( den ) < 1e-6f ) return false;
		float t = Vec3.Dot( point - origin, normal ) / den;
		if ( t <= 0f ) return false;
		hit = origin + dir * t;
		return true;
	}

	public void Draw( Control overlay )
	{
		if ( _camera == null || !GodotObject.IsInstanceValid( _camera ) ) return;
		var xf = Space();
		var pts = _spline.Points;
		var screen = new List<Vector2?>( pts.Count );
		foreach ( var p in pts )
		{
			var g = xf * ToGodot( p );
			screen.Add( _camera.IsPositionBehind( g ) ? null : _camera.UnprojectPosition( g ) );
		}
		var line = new Color( 0.94f, 0.63f, 0.23f );
		for ( int i = 1; i < screen.Count; i++ )
			if ( screen[i - 1] is { } a && screen[i] is { } b ) overlay.DrawLine( a, b, line, 2f, true );
		if ( _spline.Closed.Value && screen.Count > 2 && screen[^1] is { } l && screen[0] is { } f ) overlay.DrawLine( l, f, line with { A = 0.5f }, 2f, true );
		for ( int i = 0; i < screen.Count; i++ )
		{
			if ( screen[i] is not { } s ) continue;
			var c = i == _hover ? new Color( 0.36f, 0.58f, 1f ) : i == screen.Count - 1 ? line : new Color( 0.96f, 0.96f, 0.98f );
			overlay.DrawCircle( s, 5f, c );
		}
	}
}

#endif
