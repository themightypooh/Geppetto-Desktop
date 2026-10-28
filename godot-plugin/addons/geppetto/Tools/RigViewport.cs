#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// The viewport side of the Rig workspace: rays from the editor camera into Effigy space, and the bone
/// gizmo drawn on the overlay (s&amp;box EffigyViewport.Rig.cs). Every bone is drawn from the kernel rig,
/// so what you see is what the .effigy holds.
/// </summary>
static class RigView
{
	public static readonly Color BoneColor = new( 0.85f, 0.85f, 0.9f, 0.9f );
	public static readonly Color SelectedColor = new( 1f, 0.62f, 0.15f, 1f );
	public static readonly Color SoftColor = new( 0.45f, 0.8f, 1f, 1f );
	public static readonly Color PreviewColor = new( 0.3f, 1f, 0.45f, 1f );

	/// <summary>The node whose space the mesh (and so the rig, converted) lives in: the importer's Body.</summary>
	public static Transform3D Frame( GeppettoCharacter c )
	{
		if ( !GodotObject.IsInstanceValid( c.Root ) ) return Transform3D.Identity;
		if ( c.Root.FindChild( "Body", true, false ) is Node3D body && body.IsInsideTree() ) return body.GlobalTransform;
		if ( c.Skeleton is { } s && GodotObject.IsInstanceValid( s ) && s.IsInsideTree() ) return s.GlobalTransform;
		return c.Root is Node3D n && n.IsInsideTree() ? n.GlobalTransform : Transform3D.Identity;
	}

	public static Camera3D? Camera( Camera3D? last )
	{
		if ( last != null && GodotObject.IsInstanceValid( last ) ) return last;
		return EditorInterface.Singleton?.GetEditorViewport3D( 0 )?.GetCamera3D();
	}

	/// <summary>The mouse ray in Effigy model space (inches, Z-up).</summary>
	public static (Vec3 Origin, Vec3 Dir) Ray( Camera3D cam, Vector2 pos, Transform3D frame )
	{
		var inv = frame.AffineInverse();
		var o = inv * cam.ProjectRayOrigin( pos );
		var d = ( inv.Basis * cam.ProjectRayNormal( pos ) ).Normalized();
		return (EffigyPoseBridge.ToEffigyPosition( o ), EffigyPoseBridge.ToEffigyDirection( d ).Normal);
	}

	/// <summary>The camera's facing in Effigy model space.</summary>
	public static Vec3 Forward( Camera3D cam, Transform3D frame ) =>
		EffigyPoseBridge.ToEffigyDirection( ( frame.Basis.Inverse() * -cam.GlobalBasis.Z ).Normalized() ).Normal;

	public static Vector2? Screen( Camera3D cam, Transform3D frame, Vec3 p )
	{
		var g = frame * EffigyPoseBridge.ToGodotPosition( p );
		if ( cam.IsPositionBehind( g ) ) return null;
		return cam.UnprojectPosition( g );
	}

	/// <summary>Draws every bone (head dot, line to the tail) from model-space transforms; the picked bone
	/// orange, soft bones blue.</summary>
	public static void DrawBones( Control overlay, Camera3D cam, Transform3D frame, Skeleton rig, Xform[] world, int selected )
	{
		for ( int i = 0; i < rig.Count && i < world.Length; i++ )
		{
			var bone = rig.Bones[i];
			var head = world[i].Origin;
			var tail = head + world[i].Y.Normal * bone.Length;
			if ( Screen( cam, frame, head ) is not { } a || Screen( cam, frame, tail ) is not { } b ) continue;
			var col = i == selected ? SelectedColor : bone.Soft != null ? SoftColor : BoneColor;
			DrawBone( overlay, a, b, col, i == selected ? 3f : 2f );
		}
	}

	/// <summary>A flat "dog bone": a tapered diamond from head to tail with a dot at each end.</summary>
	public static void DrawBone( Control overlay, Vector2 a, Vector2 b, Color col, float width )
	{
		var d = b - a;
		float len = d.Length();
		if ( len > 1f )
		{
			var n = new Vector2( -d.Y, d.X ) / len * MathF.Min( 6f, len * 0.12f );
			var mid = a + d * 0.15f;
			var pts = new[] { a, mid + n, b, mid - n, a };
			overlay.DrawPolyline( pts, col, width, true );
		}
		overlay.DrawCircle( a, 4f, col );
		overlay.DrawCircle( b, 2.5f, col );
	}

	/// <summary>The tool's prompt, top-left over the viewport, wrapped to the viewport width (never clipped).</summary>
	public static void DrawPrompt( Control overlay, string text )
	{
		if ( string.IsNullOrEmpty( text ) ) return;
		var font = overlay.GetThemeDefaultFont();
		int size = overlay.GetThemeDefaultFontSize();
		float width = MathF.Max( 120f, overlay.Size.X - 32f );
		var box = font.GetMultilineStringSize( text, HorizontalAlignment.Left, width, size );
		overlay.DrawRect( new Rect2( 10, 10, box.X + 12, box.Y + 10 ), new Color( 0, 0, 0, 0.55f ) );
		overlay.DrawMultilineString( font, new Vector2( 16, 15 + font.GetAscent( size ) ), text, HorizontalAlignment.Left, width, size, -1, Colors.White );
	}

	public static void DrawLabel( Control overlay, Vector2 at, string text, Color col )
	{
		var font = overlay.GetThemeDefaultFont();
		int size = overlay.GetThemeDefaultFontSize();
		overlay.DrawString( font, at + new Vector2( 8, -6 ), text, HorizontalAlignment.Left, -1, size, col );
	}
}

/// <summary>
/// Add Bone (s&amp;box EffigyRigPanel bone tool + EffigyViewport.Rig.cs): click the model to put a joint
/// halfway through the material (Middle) or on the skin (Surface); each further click adds a bone from the
/// last joint and chains on. With a bone picked when the tool starts, the chain branches from its tail.
/// Off the model, later joints land on the plane through the last joint, square to the view.
/// </summary>
sealed class RigBoneTool : IGeppettoViewportTool
{
	/// <summary>Middle (default) or Surface; shared with the Rig panel's placement box.</summary>
	public static bool PlaceMiddle = true;
	/// <summary>Snap every joint onto y = 0, the plane Mirror reflects across.</summary>
	public static bool SnapCentre;

	readonly GeppettoCharacter _c;
	readonly GeppettoDocument _doc;
	int _chainParent;
	Vec3? _head;
	Camera3D? _cam;
	(Vec3 Point, float? Thickness)? _hover;

	public string Name => "Add Bone";

	public RigBoneTool( GeppettoCharacter c )
	{
		_c = c;
		_doc = c.Doc!;
		int sel = GeppettoRig.SelectedIndex( _doc );
		_chainParent = sel;
		_head = sel >= 0 ? _doc.Studio.Rig.TailWorld( sel ) : null;
		GeppettoToolContext.Say( Prompt );
	}

	string Where => ( PlaceMiddle ? "through the middle of the model" : "on the surface" ) + ( SnapCentre ? " and snapped to y=0" : "" );

	string Prompt => _head == null
		? $"Click to place a bone {Where}. Click again to extend the chain. Esc when done."
		: _chainParent >= 0 && _chainParent < _doc.Studio.Rig.Count
			? $"Click to extend a bone from '{_doc.Studio.Rig.Bones[_chainParent].Name}', {Where}. Esc when done."
			: $"Click to extend the chain, {Where}. Esc when done.";

	/// <summary>Where a click at <paramref name="pos"/> lands, or null when it misses with no chain to extend.</summary>
	(Vec3 Point, float? Thickness)? Pick( Camera3D cam, Vector2 pos )
	{
		var frame = RigView.Frame( _c );
		var (o, d) = RigView.Ray( cam, pos, frame );
		Vec3 point;
		float? thick = null;
		if ( MeshRaycast.Raycast( _doc.Studio.Bodies.Where( b => b.Visible ), o, d ) is { } hit )
		{
			point = hit.Hit.Point;
			if ( PlaceMiddle && MeshRaycast.FirstSolidSpan( hit.Body.Mesh, o, d ) is { } span )
			{
				point = span.Midpoint;
				thick = span.Thickness;
			}
		}
		else
		{
			if ( _head is not { } head ) return null;
			var n = RigView.Forward( cam, frame );
			float den = Vec3.Dot( d, n );
			if ( MathF.Abs( den ) < 1e-6f ) return null;
			float t = Vec3.Dot( head - o, n ) / den;
			if ( t <= 0f ) return null;
			point = o + d * t;
		}
		if ( SnapCentre ) point = new Vec3( point.x, 0f, point.z );
		return (point, thick);
	}

	public bool Input( Camera3D camera, InputEvent e )
	{
		_cam = camera;
		if ( e is InputEventMouseMotion mm )
		{
			_hover = Pick( camera, mm.Position );
			GeppettoTools.Redraw();
			return false;
		}
		if ( e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mb ) return false;
		if ( !mb.Pressed ) return true;   // the release belongs to the click we took

		var picked = Pick( camera, mb.Position );
		if ( picked is not { } p )
			return GeppettoRig.Refuse( "Missed the model — a chain's first joint goes on the part. Click on it; later joints can go in empty space." );

		if ( _head is not { } head )
		{
			_head = p.Point;
			GeppettoToolContext.Say( Prompt );
			return true;
		}

		int index = GeppettoRig.AddBone( _doc, _chainParent, head, p.Point );
		if ( index >= 0 )
		{
			_chainParent = index;
			_head = p.Point;
			GeppettoToolContext.Say( $"Placed {_doc.Studio.Rig.Bones[index].Name}. {Prompt}" );
		}
		return true;
	}

	public void Draw( Control overlay )
	{
		var cam = RigView.Camera( _cam );
		if ( cam == null ) return;
		var frame = RigView.Frame( _c );
		var rig = _doc.Studio.Rig;
		RigView.DrawBones( overlay, cam, frame, rig, SoftSolver.BindPose( rig ), GeppettoRig.SelectedIndex( _doc ) );

		if ( _hover is { } h && RigView.Screen( cam, frame, h.Point ) is { } hp )
		{
			if ( _head is { } head && RigView.Screen( cam, frame, head ) is { } hs && ( h.Point - head ).Length > 0.01f )
				RigView.DrawBone( overlay, hs, hp, RigView.PreviewColor, 2f );
			else
				overlay.DrawCircle( hp, 4f, RigView.PreviewColor );
			if ( h.Thickness is { } t ) RigView.DrawLabel( overlay, hp, $"{t:0.##}", RigView.PreviewColor );
		}
		else if ( _head is { } head && RigView.Screen( cam, frame, head ) is { } hs )
			overlay.DrawCircle( hs, 4f, RigView.PreviewColor );

		RigView.DrawPrompt( overlay, Prompt );
	}

	public void Exit() => GeppettoToolContext.Say( "Add Bone: stopped placing." );
}

/// <summary>
/// Click a part in the viewport, for the Rig tools that act on a part (s&amp;box took these from its part
/// selection; Poohdot has no Geppetto part selection, so the tool asks for the click instead). The
/// callback returns true to keep picking (Assign Body toggles part after part) or false to stop.
/// </summary>
sealed class RigBodyPickTool : IGeppettoViewportTool
{
	readonly GeppettoCharacter _c;
	readonly GeppettoDocument _doc;
	readonly Func<string> _prompt;
	readonly Func<Body, bool> _picked;
	Camera3D? _cam;
	string? _hoverName;
	bool _done;

	public string Name { get; }

	public RigBodyPickTool( GeppettoCharacter c, string name, Func<string> prompt, Func<Body, bool> picked )
	{
		_c = c;
		_doc = c.Doc!;
		Name = name;
		_prompt = prompt;
		_picked = picked;
		GeppettoToolContext.Say( $"{name}: {prompt()}" );
	}

	Body? Hit( Camera3D cam, Vector2 pos )
	{
		var (o, d) = RigView.Ray( cam, pos, RigView.Frame( _c ) );
		return MeshRaycast.Raycast( _doc.Studio.Bodies.Where( b => b.Visible ), o, d )?.Body;
	}

	public bool Input( Camera3D camera, InputEvent e )
	{
		_cam = camera;
		if ( e is InputEventMouseMotion mm )
		{
			var name = Hit( camera, mm.Position )?.Name;
			if ( name != _hoverName ) { _hoverName = name; GeppettoTools.Redraw(); }
			return false;
		}
		if ( e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mb ) return false;
		if ( !mb.Pressed ) return true;
		if ( _done ) return true;
		var body = Hit( camera, mb.Position );
		if ( body == null ) return GeppettoRig.Refuse( $"{Name}: missed the model — click on a part." );
		if ( !_picked( body ) )
		{
			_done = true;
			// Leave the viewport to the editor again once the one-shot pick is used (deferred: we are
			// inside the plugin's input call for this very tool).
			Callable.From( () => { if ( GeppettoTools.Active == this ) GeppettoTools.Activate( null ); } ).CallDeferred();
		}
		return true;
	}

	public void Draw( Control overlay )
	{
		var cam = RigView.Camera( _cam );
		if ( cam == null ) return;
		var rig = _doc.Studio.Rig;
		RigView.DrawBones( overlay, cam, RigView.Frame( _c ), rig, SoftSolver.BindPose( rig ), GeppettoRig.SelectedIndex( _doc ) );
		RigView.DrawPrompt( overlay, _hoverName == null ? _prompt() : $"{_prompt()}\nUnder the cursor: {_hoverName}" );
	}
}

/// <summary>
/// The soft-bone preview (s&amp;box EffigyViewport.SoftPreview.cs): every frame the pose the skeleton has
/// right now is the animated input, the solver lags the soft bones behind it under gravity, and the result
/// is drawn as bones over the viewport. Like s&amp;box it never writes the skeleton or the document, so
/// nothing it shows can be keyed or saved by accident. Moving the character node or posing a bone pushes
/// the soft bones.
/// </summary>
sealed class RigSoftPreview : IGeppettoViewportTool
{
	/// <summary>The longest step the solver is handed: a stalled editor frame must not explode the springs.</summary>
	const float MaxStep = 1f / 30f;

	readonly GeppettoCharacter _c;
	readonly GeppettoDocument _doc;
	SoftPose _pose;
	Xform[] _solved;
	ulong _lastTicks;
	Camera3D? _cam;
	readonly SceneTree? _tree;

	public string Name => "Soft Preview";

	public RigSoftPreview( GeppettoCharacter c )
	{
		_c = c;
		_doc = c.Doc!;
		_pose = new SoftPose( _doc.Studio.Rig.Count );
		_solved = SoftSolver.BindPose( _doc.Studio.Rig );
		_lastTicks = Time.GetTicksUsec();
		_tree = Engine.GetMainLoop() as SceneTree;
		if ( _tree != null ) _tree.ProcessFrame += Step;
	}

	/// <summary>s&amp;box RestSoftPreview: forget the motion; the next solve places every soft bone on its pose.</summary>
	public void Rest()
	{
		_pose.Rest();
		_solved = SoftSolver.BindPose( _doc.Studio.Rig );
		GeppettoTools.Redraw();
	}

	/// <summary>One solver step of <paramref name="dt"/> seconds (clamped). Public for the headless test.</summary>
	public Xform[] Step( float dt )
	{
		var rig = _doc.Studio.Rig;
		if ( _pose.Tail.Length != rig.Count ) { _pose = new SoftPose( rig.Count ); }

		// The animated pose: whatever the Skeleton3D is posed to now (bind pose where it can't say), in the
		// world, so moving the character swings the bones too.
		Xform[] local = _c.Skeleton is { } skel && GodotObject.IsInstanceValid( skel )
			? EffigyPoseBridge.ReadLocalPose( rig, skel )
			: EffigyPoseBridge.RestPose( rig );
		var world = EffigyPoseBridge.ToEffigyXform( RigView.Frame( _c ) );
		var animated = RigAnimFile.ModelSpace( rig, local ).Select( m => world * m ).ToArray();
		SoftSolver.Solve( rig, animated, _pose, MathF.Min( dt, MaxStep ) );
		var back = world.Inverse;
		_solved = animated.Select( a => back * a ).ToArray();
		return _solved;
	}

	void Step()
	{
		ulong now = Time.GetTicksUsec();
		float dt = ( now - _lastTicks ) / 1_000_000f;
		_lastTicks = now;
		if ( !GodotObject.IsInstanceValid( _c.Root ) ) { GeppettoTools.Activate( null ); return; }
		Step( dt );
		GeppettoTools.Redraw();
	}

	public bool Input( Camera3D camera, InputEvent e ) { _cam = camera; return false; }

	public void Draw( Control overlay )
	{
		var cam = RigView.Camera( _cam );
		if ( cam == null ) return;
		var rig = _doc.Studio.Rig;
		if ( _solved.Length != rig.Count ) return;
		RigView.DrawBones( overlay, cam, RigView.Frame( _c ), rig, _solved, GeppettoRig.SelectedIndex( _doc ) );
		RigView.DrawPrompt( overlay, "Soft preview: soft bones (blue) hang and swing behind the pose. Pose a bone or move the character to push them; Rest settles them. Esc or Preview again stops it." );
	}

	public void Exit()
	{
		if ( _tree != null ) _tree.ProcessFrame -= Step;
	}
}
#endif
