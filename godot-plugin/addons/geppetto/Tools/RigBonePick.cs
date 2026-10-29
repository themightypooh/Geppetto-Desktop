#if TOOLS
#nullable enable
using System;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Click a bone in the viewport, for the Rig tools that act on the picked bone (Delete, Assign Body, Mirror
/// bones, Make Soft) when none is picked in the Rig panel's tree yet. The bone nearest the click (within a
/// few pixels of its drawn line) is picked, exactly as picking it in the tree, then the tool's action runs.
/// </summary>
sealed class RigBonePickTool : IGeppettoViewportTool
{
	/// <summary>How far (screen pixels) from a bone's line a click still picks it.</summary>
	public const float Reach = 14f;

	readonly GeppettoCharacter _c;
	readonly GeppettoDocument _doc;
	readonly string _prompt;
	readonly Action<int> _picked;
	Camera3D? _cam;
	int _hover = -1;
	bool _done;

	public string Name { get; }

	public RigBonePickTool( GeppettoCharacter c, string name, string prompt, Action<int> picked )
	{
		_c = c;
		_doc = c.Doc!;
		Name = name;
		_prompt = prompt;
		_picked = picked;
		GeppettoToast.Info( $"{name}: {prompt}" );
	}

	/// <summary>The bone whose drawn line is nearest <paramref name="pos"/>, or -1 when none is within reach.</summary>
	public static int Nearest( Camera3D cam, GeppettoCharacter c, Vector2 pos )
	{
		var rig = c.Doc!.Studio.Rig;
		var frame = RigView.Frame( c );
		var world = SoftSolver.BindPose( rig );
		int best = -1;
		float bestD = Reach;
		for ( int i = 0; i < rig.Count && i < world.Length; i++ )
		{
			var head = world[i].Origin;
			var tail = head + world[i].Y.Normal * rig.Bones[i].Length;
			if ( RigView.Screen( cam, frame, head ) is not { } a || RigView.Screen( cam, frame, tail ) is not { } b ) continue;
			float d = DistToSegment( pos, a, b );
			if ( d < bestD ) { bestD = d; best = i; }
		}
		return best;
	}

	static float DistToSegment( Vector2 p, Vector2 a, Vector2 b )
	{
		var ab = b - a;
		float len2 = ab.LengthSquared();
		float t = len2 < 1e-6f ? 0f : Math.Clamp( ( p - a ).Dot( ab ) / len2, 0f, 1f );
		return p.DistanceTo( a + ab * t );
	}

	public bool Input( Camera3D camera, InputEvent e )
	{
		_cam = camera;
		if ( e is InputEventMouseMotion mm )
		{
			int h = Nearest( camera, _c, mm.Position );
			if ( h != _hover ) { _hover = h; GeppettoTools.Redraw(); }
			return false;
		}
		if ( e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mb ) return false;
		if ( !mb.Pressed || _done ) return true;
		int bone = Nearest( camera, _c, mb.Position );
		if ( bone < 0 ) return GeppettoRig.Refuse( $"{Name}: no bone there - click on a bone's line." );
		_done = true;
		GeppettoRig.Select( _doc, bone );
		// Hand the viewport back first: the action may start its own tool (Assign Body does).
		Callable.From( () =>
		{
			if ( GeppettoTools.Active == this ) GeppettoTools.Activate( null );
			_picked( bone );
		} ).CallDeferred();
		return true;
	}

	public void Draw( Control overlay )
	{
		var cam = RigView.Camera( _cam );
		if ( cam == null ) return;
		var rig = _doc.Studio.Rig;
		int shown = _hover >= 0 ? _hover : GeppettoRig.SelectedIndex( _doc );
		RigView.DrawBones( overlay, cam, RigView.Frame( _c ), rig, SoftSolver.BindPose( rig ), shown );
		RigView.DrawPrompt( overlay, _hover >= 0 && _hover < rig.Count ? $"{_prompt}\nUnder the cursor: {rig.Bones[_hover].Name}" : _prompt );
	}

	public void Exit() { }
}
#endif
