#if TOOLS
#nullable enable
using System;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// The Rig workspace's "Paint Weights" brush, ported from s&amp;box's EffigyWindow EnterWeightPaint /
/// FinishWeightPaint and EffigyViewport.WeightPainting. It binds the studio the way the export does
/// (BindBodies + SmoothWeights), lays the document's earlier weight paint over that, and paints the picked
/// bone through the kernel <see cref="WeightPaintSession"/>. The result is the studio's
/// <see cref="PartStudio.WeightPaint"/> layer, which the .effigy saves; every stroke is one undo step.
///
/// While the tool is up, the mesh shows s&amp;box's heat-map ramp for the bone (a texture through the
/// mesh UVs, not vertex colour) on a preview that goes away on Exit. Shift+click picks the bone that
/// owns the vertices under the cursor; [ and ] resize.
/// </summary>
public sealed class PaintWeightsTool : IGeppettoViewportTool
{
	static readonly Color WeightCursorColor = new( 1f, 0.55f, 0.2f, 0.9f );
	const int RampResolution = 256;

	// Kept between activations, like s&box's bar.
	static int _rememberedBone = -1;
	static float? _rememberedRadius;
	static float _rememberedStrength = 0.15f;
	static BrushFalloff _rememberedFalloff = BrushFalloff.Smooth;
	static WeightBrushKind _rememberedBrush = WeightBrushKind.Add;

	public string Name => "Paint Weights";
	public GeppettoDocument Doc { get; }
	public WeightPaintSession? Session { get; private set; }

	readonly GeppettoPlugin? _plugin;
	readonly MeshInstance3D? _bodyNode;
	readonly GeppettoBrushPreview _preview = new();
	WeightBar? _bar;
	WeightPaintLayer? _syncedLayer;
	WeightPaintLayer? _prevLayer;
	WeightPaintLayer? _prevClone;

	Camera3D? _camera;
	MeshHit? _hover;
	bool _armed;
	bool _lockedWarned;
	bool _closed;

	public PaintWeightsTool( GeppettoDocument doc, GeppettoPlugin? plugin, MeshInstance3D? bodyNode )
	{
		Doc = doc;
		_plugin = plugin;
		_bodyNode = bodyNode;
		Doc.Changed += OnDocChanged;
		if ( _plugin != null )
		{
			_bar = new WeightBar( this );
			_plugin.AddControlToContainer( EditorPlugin.CustomControlContainer.SpatialEditorMenu, _bar );
		}
	}

	/// <summary>s&amp;box's EnterWeightPaint. Returns false (with the s&amp;box warning) when it can't start.</summary>
	public bool Open( int? bone = null, bool quiet = false )
	{
		var studio = Doc.Studio;
		var rig = studio.Rig;
		if ( rig == null || rig.Count == 0 ) return Refuse( "Paint Weights needs a skeleton — place a bone first." );

		int pick = bone ?? Session?.Bone ?? _rememberedBone;
		if ( pick < 0 || pick >= rig.Count ) pick = 0;

		var (mesh, ranges) = studio.ToMeshWithBodies();
		if ( mesh.FaceCount == 0 ) return Refuse( "Paint Weights needs a body to paint — add a primitive or extrude a sketch first." );
		// The ramp is a texture through the UVs, so a mesh without usable UVs gets the same automatic
		// unwrap as the paint brush (on this working copy only, as s&box did).
		if ( !NormalBake.Measure( mesh ).CanBake ) UVUnwrap.Unwrap( mesh );

		SkinWeights weights;
		try
		{
			weights = SkinBinder.BindBodies( mesh, ranges, studio.BodyBoneMap, rig );
			weights = SkinBinder.SmoothWeights( mesh, weights );
		}
		catch ( Exception e ) { return Refuse( $"Paint Weights could not bind the mesh to the skeleton: {e.Message}" ); }

		bool stale = false;
		if ( studio.WeightPaint is { Count: > 0 } layer )
		{
			if ( layer.CanApply( mesh, out _ ) ) layer.Apply( mesh, weights, rig, out _ );
			else stale = true;
		}

		var session = new WeightPaintSession( mesh, weights, rig, studio.WeightPaint )
		{
			Bone = pick,
			Brush = _rememberedBrush,
			Falloff = _rememberedFalloff,
			Strength = _rememberedStrength,
		};
		session.Radius = _rememberedRadius is float r && r > 0f ? r : session.SuggestedRadius;
		_rememberedRadius = session.Radius;
		_rememberedBone = pick;
		Session = session;
		_syncedLayer = studio.WeightPaint;

		ShowRamp();
		_bar?.Refresh();
		if ( !quiet ) GeppettoToolContext.Say( $"Painting {rig.Bones[pick].Name}. The ramp is a texture, not vertex colour. Drag on the mesh (Shift+click picks the bone under the cursor)." );
		if ( stale )
			Refuse( $"Your earlier weight paint ({studio.WeightPaint!.Count} vertices) isn't shown: the mesh changed shape since it was painted. "
				+ "Painting now starts over and replaces it. Leave without painting to keep it, and undo the edit that changed the mesh to get it back." );
		return true;
	}

	// ----------------------------------------------------------------- bar settings

	public void SetRadius( float v ) { _rememberedRadius = MathF.Max( v, 1e-4f ); if ( Session != null ) Session.Radius = _rememberedRadius.Value; GeppettoTools.Redraw(); }
	public void SetStrength( float v ) { _rememberedStrength = Math.Clamp( v, 0.01f, 1f ); if ( Session != null ) Session.Strength = _rememberedStrength; }
	public void SetFalloff( BrushFalloff f ) { _rememberedFalloff = f; if ( Session != null ) Session.Falloff = f; }
	public void SetBrush( WeightBrushKind k ) { _rememberedBrush = k; if ( Session != null ) Session.Brush = k; }

	public void SetBone( int bone )
	{
		if ( Session == null || bone < 0 || bone >= Session.Skeleton.Count || Session.IsStroking ) return;
		Session.Bone = bone;
		_rememberedBone = bone;
		ShowRamp();
		_bar?.Refresh();
		GeppettoToolContext.Say( $"Painting {Session.Skeleton.Bones[bone].Name}." );
	}

	// ----------------------------------------------------------------- strokes (kernel space)

	public bool IsStroking => Session?.IsStroking == true;

	public MeshHit? Hover( Vec3 origin, Vec3 direction )
	{
		if ( Session == null ) return null;
		_hover = Session.Hover( origin, direction );
		if ( _armed && !Session.IsStroking ) TryBegin( origin, direction );
		else if ( Session.IsStroking )
		{
			if ( Session.MoveTo( origin, direction ) > 0 ) ShowRamp();
			if ( _hover is { } h ) WarnLockedUnder( h.Point );
		}
		return _hover;
	}

	public void Press( Vec3 origin, Vec3 direction, bool shift )
	{
		if ( Session == null && !Open() ) return;
		var s = Session!;
		if ( shift )
		{
			if ( s.Hover( origin, direction ) is not { } hit ) { Refuse( "Shift+click on the mesh to pick the bone there." ); return; }
			SetBone( DominantBone( s, hit.FaceIndex ) );
			return;
		}
		if ( s.Bone < 0 && s.Brush != WeightBrushKind.Smooth )
		{
			Refuse( "Can't paint weights: no bone is picked. Pick one in the Bone list, then drag on the mesh." );
			return;
		}
		_armed = true;
		_lockedWarned = false;
		TryBegin( origin, direction );
	}

	void TryBegin( Vec3 origin, Vec3 direction )
	{
		var s = Session!;
		_prevLayer = Doc.Studio.WeightPaint;
		_prevClone = s.Layer.Clone();
		if ( s.BeginStroke( origin, direction ) )
		{
			_armed = false;
			ShowRamp();
			if ( _hover is { } h ) WarnLockedUnder( h.Point );
		}
	}

	public void Release()
	{
		if ( Session == null ) { _armed = false; return; }
		if ( Session.IsStroking )
		{
			var edit = Session.EndStroke();
			ShowRamp();
			if ( edit != null && edit.Count > 0 ) Commit();
		}
		else if ( _armed ) Refuse( "Missed the model — start or drag the stroke onto the part." );
		_armed = false;
	}

	// One stroke = one undo step on the document: the studio's layer before and after.
	void Commit()
	{
		var studio = Doc.Studio;
		var live = Session!.Layer;
		var before = ReferenceEquals( _prevLayer, live ) ? _prevClone : _prevLayer;
		var after = live.Clone();
		bool first = true;
		_syncedLayer = live;
		Doc.Execute( new DelegateEditCommand( "Paint weights",
			execute: () =>
			{
				studio.WeightPaint = first ? live : after.Clone();
				first = false;
			},
			undo: () => studio.WeightPaint = before?.Clone() ), "Geppetto: paint weights" );
	}

	void OnDocChanged()
	{
		if ( _closed || Session == null || Session.IsStroking ) return;
		if ( ReferenceEquals( Doc.Studio.WeightPaint, _syncedLayer ) ) return;
		// Undo / redo (or a rig edit) swapped the studio's layer: rebuild from the document.
		var bone = Session.Bone;
		Session = null;
		Open( bone, quiet: true );
	}

	static int DominantBone( WeightPaintSession s, int face )
	{
		if ( face < 0 || face >= s.Mesh.Faces.Count ) return s.Bone;
		var totals = new float[s.Skeleton.Count];
		foreach ( var vi in s.Mesh.Faces[face].Indices )
			if ( vi < s.Weights.Count )
				foreach ( var w in s.Weights[vi] )
					if ( w.Bone >= 0 && w.Bone < totals.Length ) totals[w.Bone] += w.Weight;
		int best = s.Bone;
		float bestW = 0f;
		for ( int b = 0; b < totals.Length; b++ )
			if ( totals[b] > bestW ) { bestW = totals[b]; best = b; }
		return best;
	}

	void WarnLockedUnder( Vec3 point )
	{
		if ( _lockedWarned || Session is not { } s ) return;
		bool lowering = s.Brush == WeightBrushKind.Subtract || (s.Brush == WeightBrushKind.Set && s.Target < 1f - 1e-4f);
		if ( !lowering ) return;
		int locked = s.LockedUnder( point );
		if ( locked == 0 ) return;
		_lockedWarned = true;
		var bone = s.Bone >= 0 && s.Bone < s.Skeleton.Count ? s.Skeleton.Bones[s.Bone].Name : "this bone";
		Refuse( $"Can't take weight off {locked} vertices here: {bone} is their only bone, and the weights must add up to 1. "
			+ "Add weight to a neighbouring bone there first (pick it, brush Add) — that takes it from this one." );
	}

	// ----------------------------------------------------------------- viewport

	public bool Input( Camera3D camera, InputEvent e )
	{
		_camera = camera;
		switch ( e )
		{
			case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Bracketleft or Key.Bracketright:
			{
				float r = Session?.Radius ?? _rememberedRadius ?? 0.25f;
				SetRadius( k.Keycode == Key.Bracketright ? r * 1.15f : r / 1.15f );
				_bar?.Refresh();
				return true;
			}
			case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
			{
				var (o, d) = GeppettoBrush.Ray( camera, mb.Position, _bodyNode );
				if ( mb.Pressed ) Press( o, d, mb.ShiftPressed );
				else Release();
				GeppettoTools.Redraw();
				return true;
			}
			case InputEventMouseMotion mm:
			{
				var (o, d) = GeppettoBrush.Ray( camera, mm.Position, _bodyNode );
				Hover( o, d );
				GeppettoTools.Redraw();
				return IsStroking || _armed;
			}
		}
		return false;
	}

	public void Draw( Control overlay )
	{
		if ( _camera == null || Session == null || _hover is not { } hit ) return;
		GeppettoBrush.DrawRing( overlay, _camera, _bodyNode, hit, Session.Radius, WeightCursorColor );
	}

	void ShowRamp()
	{
		if ( Session == null || _bodyNode == null ) return;
		var canvas = Session.Ramp( RampResolution );
		var tex = new GeppettoCanvasTexture( canvas.Width, canvas.Height, PaintMaterial.OpaqueRgba( canvas ) );
		_preview.Show( _bodyNode, GeppettoBrush.BuildMesh( Session.Mesh, tex.Material() ) );
	}

	public void Exit()
	{
		if ( _closed ) return;
		if ( Session is { IsStroking: true } ) Release();
		_closed = true;
		Doc.Changed -= OnDocChanged;
		_preview.Close();
		Session = null;
		if ( _bar != null && _plugin != null && GodotObject.IsInstanceValid( _bar ) )
		{
			_plugin.RemoveControlFromContainer( EditorPlugin.CustomControlContainer.SpatialEditorMenu, _bar );
			_bar.QueueFree();
		}
		_bar = null;
	}

	static bool Refuse( string message )
	{
		GeppettoToolContext.Warn( $"{message}" );
		return false;
	}
}
#endif
