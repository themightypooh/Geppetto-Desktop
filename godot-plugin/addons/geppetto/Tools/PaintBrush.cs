#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>Brush settings kept between brush sessions (s&amp;box kept them on the bar).</summary>
public sealed class PaintBrushSettings
{
	public PaintChannel Channel = PaintChannel.Color;
	public float R = 1f, G = 1f, B = 1f, A = 1f;
	public float Value = 1f;
	public float Strength = 1f;
	public float? Radius;
	public BrushFalloff Falloff = BrushFalloff.Smooth;
	public bool Erasing;
}

/// <summary>
/// The Paint workspace's texture brush, ported from s&amp;box's EffigyViewport.Painting + EffigyWindow
/// AddPaint/EnterPaint/OnPaintStrokeFinished. It drives the kernel <see cref="PaintSession"/> on one body
/// and commits each finished stroke (mouse down → up) into the body's <see cref="PaintFeature"/> as one
/// undo step, so strokes land in the .effigy exactly as s&amp;box wrote them.
///
/// Click a part to open its paint layer (its last Paint feature, or a new one; a UV Project (Unwrap) is
/// added above it first when the part's UVs can't carry paint, as s&amp;box did). Shift+click another part
/// switches to it. Drag paints; Ctrl held at the press erases instead (or paints, when Erase is on);
/// [ and ] shrink and grow the brush.
/// </summary>
public sealed class PaintBrushTool : IGeppettoViewportTool
{
	public static readonly PaintBrushSettings Remembered = new();

	static readonly Color PaintCursorColor = new( 1f, 0.45f, 0.75f, 0.9f );
	static readonly Color PaintEraseCursorColor = new( 1f, 0.35f, 0.32f, 0.9f );

	public string Name => "Paint brush";
	public GeppettoDocument Doc { get; }
	public PaintBrushSettings Settings => Remembered;
	public PaintFeature? Feature { get; private set; }
	public PaintSession? Session { get; private set; }
	public string? BodyId { get; private set; }

	/// <summary>The next part clicked gets a new layer (false = normal, true = fill) instead of its last one.</summary>
	public bool? PendingNewLayer { get; set; }

	readonly GeppettoPlugin? _plugin;
	readonly MeshInstance3D? _bodyNode;
	readonly GeppettoBrushPreview _preview = new();
	GeppettoCanvasTexture? _texture;
	PaintBar? _bar;
	int? _rollbackBefore;

	Camera3D? _camera;
	MeshHit? _hover;
	bool _armed;
	bool _eraseMode;
	bool _ctrlHeld;
	bool _closed;

	/// <param name="plugin">Null in headless tests (no toolbar).</param>
	/// <param name="bodyNode">The character's Body node (rays and preview); null in headless tests.</param>
	public PaintBrushTool( GeppettoDocument doc, GeppettoPlugin? plugin, MeshInstance3D? bodyNode )
	{
		Doc = doc;
		_plugin = plugin;
		_bodyNode = bodyNode;
		Doc.Changed += OnDocChanged;
		if ( _plugin != null )
		{
			_bar = new PaintBar( this );
			_plugin.AddControlToContainer( EditorPlugin.CustomControlContainer.SpatialEditorMenu, _bar );
			_bar.Refresh();
		}
	}

	public void FocusBar( string field ) => _bar?.Focus( field );

	// =========================================================================================
	// Opening a layer
	// =========================================================================================

	/// <summary>Every visible part's id under the ray, nearest first (kernel space).</summary>
	public string? BodyUnder( Vec3 origin, Vec3 direction )
	{
		string? best = null;
		float bestD = float.MaxValue;
		foreach ( var b in Doc.Studio.Bodies )
		{
			if ( !b.Visible || b.IsReference || b.Mesh is null || b.Mesh.FaceCount == 0 ) continue;
			if ( MeshRaycast.Raycast( b.Mesh, origin, direction ) is { } hit && hit.Distance < bestD )
			{
				bestD = hit.Distance;
				best = b.Id;
			}
		}
		return best;
	}

	/// <summary>Opens the paint layer for a part: a new one when <see cref="PendingNewLayer"/> is set, else
	/// the part's last Paint feature, else a new one (s&amp;box's AddPaint). Returns false with a warning.</summary>
	public bool OpenBody( string bodyId )
	{
		var studio = Doc.Studio;
		var body = studio.Bodies.FirstOrDefault( b => b.Id == bodyId );
		if ( body == null ) return Refuse( "That part is gone; click a part on the model." );

		bool? fresh = PendingNewLayer;
		PendingNewLayer = null;

		var existing = fresh == null
			? studio.Features.OfType<PaintFeature>().LastOrDefault( p => p.Bodies.BodyIds.Count == 1 && p.Bodies.BodyIds[0] == bodyId )
			: null;
		if ( existing != null ) return OpenFeature( existing );

		var layer = new PaintFeature();
		layer.Bodies.BodyIds = new List<string> { bodyId };
		layer.Fill.Value = fresh == true;
		int? at = null;
		if ( Feature != null && studio.Features.IndexOf( Feature ) is int ci and >= 0 && Feature.Bodies.BodyIds.SequenceEqual( layer.Bodies.BodyIds ) )
		{
			// New Layer over the open one: right after it, same resolution (s&box's AddPaintLayer).
			layer.Resolution.Value = Feature.Resolution.Value;
			at = ci + 1;
		}
		CloseSession();
		if ( !Doc.AddFeature( layer, at ) ) return Refuse( "Could not add a Paint layer." );
		bool ok = OpenFeature( layer );
		if ( ok && fresh == true )
			GeppettoToolContext.Say( "Fill layer added: it covers the whole surface. Pick a Mask (Edge Wear, Dirt, Gradient, Material ID) in the layer settings to confine it." );
		return ok;
	}

	/// <summary>s&amp;box's EnterPaint: roll the model to just after the layer, refuse unless it paints one
	/// part, auto-unwrap a part whose UVs can't carry paint, then open a session replaying its strokes.</summary>
	public bool OpenFeature( PaintFeature feature )
	{
		if ( ReferenceEquals( Feature, feature ) && Session != null ) return true;
		CloseSession();
		var studio = Doc.Studio;
		int index = studio.Features.IndexOf( feature );
		if ( index < 0 ) return Refuse( "That Paint layer is no longer in the feature tree." );

		if ( index + 1 < studio.Features.Count && studio.RollbackIndex != index + 1 )
		{
			_rollbackBefore ??= studio.RollbackIndex;
			studio.RollbackIndex = index + 1;
			Doc.Refresh();
		}

		var targets = studio.Bodies.Where( b => feature.Bodies.Matches( b ) ).ToList();
		if ( targets.Count != 1 )
			return Refuse( targets.Count == 0
				? "Paint needs a body to paint on — add a primitive or extrude a sketch first."
				: "Paint paints one body at a time — click one part with the brush." );

		bool unwrapped = false;
		if ( !NormalBake.Measure( targets[0].Mesh ).CanBake )
		{
			// AUTO-UNWRAP, NOT A GATE (s&box): an ordinary feature above the layer, one Ctrl+Z removes it.
			var uv = new UVProjectFeature();
			uv.Mode.Index = Array.IndexOf( uv.Mode.Options, "Unwrap" );
			uv.Bodies.BodyIds = new List<string>( feature.Bodies.BodyIds );
			bool rolled = studio.RollbackIndex == index + 1;
			if ( Doc.AddFeature( uv, index ) && rolled ) { studio.RollbackIndex = index + 2; Doc.Refresh(); }
			targets = studio.Bodies.Where( b => feature.Bodies.Matches( b ) ).ToList();
			unwrapped = targets.Count == 1;
			if ( !unwrapped ) return Refuse( "The UV unwrap left this layer without exactly one part to paint." );
		}

		Feature = feature;
		BodyId = targets[0].Id;
		var session = new PaintSession( targets[0].Mesh, feature.Resolution.Value, feature.Strokes );
		session.Radius = Remembered.Radius is float r && r > 0f && session.SuggestedRadius > 0f ? r : session.SuggestedRadius;
		Remembered.Radius = session.Radius;
		// The session opens on the channel last painted (s&box); a channel picked on the dial wins.
		if ( _pendingChannel is { } ch ) { session.Channel = ch; _pendingChannel = null; }
		Remembered.Channel = session.Channel;
		ApplySettings( session );
		ApplyLayer( session, feature );
		Session = session;

		ShowPreview( full: true );
		_bar?.Refresh();
		GeppettoToolContext.Say( unwrapped
			? $"Paint {targets[0].Name}: drag on the model, hold Ctrl to erase. Added a UV Project so the paint has its own texels (one Ctrl+Z removes it)."
			: $"Paint {targets[0].Name} ({PaintChannels.Label( session.Channel )}): drag on the model, hold Ctrl to erase, [ ] resize." );
		return true;
	}

	PaintChannel? _pendingChannel;

	static void ApplyLayer( PaintSession session, PaintFeature feature )
	{
		session.Below = feature.Below;
		session.BlendMode = feature.BlendMode;
		session.Opacity = feature.Opacity.Clamped;
		session.GeneratedMask = feature.GeneratorMask;
		session.InvertMask = feature.InvertMask.Value;
	}

	static void ApplySettings( PaintSession s )
	{
		var m = Remembered;
		s.R = m.R; s.G = m.G; s.B = m.B; s.A = m.A;
		s.Value = m.Value;
		s.Strength = Math.Clamp( m.Strength, 0.01f, 1f );
		s.Falloff = m.Falloff;
		s.Erasing = m.Erasing;
	}

	// =========================================================================================
	// Bar / dial settings
	// =========================================================================================

	public void SetChannel( PaintChannel ch )
	{
		if ( Session == null ) { _pendingChannel = ch; Remembered.Channel = ch; _bar?.Refresh(); return; }
		// Switching mid-stroke would split one mark across two maps; the session refuses it (s&box).
		if ( Session.IsStroking ) return;
		Session.Channel = ch;
		Remembered.Channel = ch;
		ShowPreview( full: true );
		_bar?.Refresh();
	}

	public void SetColour( Color c ) { Remembered.R = c.R; Remembered.G = c.G; Remembered.B = c.B; Remembered.A = c.A; if ( Session != null ) ApplySettings( Session ); }
	public void SetValue( float v ) { Remembered.Value = Math.Clamp( v, 0f, 1f ); if ( Session != null ) Session.Value = Remembered.Value; }
	public void SetStrength( float v ) { Remembered.Strength = Math.Clamp( v, 0.01f, 1f ); if ( Session != null ) Session.Strength = Remembered.Strength; }
	public void SetFalloff( BrushFalloff f ) { Remembered.Falloff = f; if ( Session != null ) Session.Falloff = f; }

	public void SetRadius( float v )
	{
		// Clamped rather than refused: a zero radius makes BeginStroke throw (s&box).
		Remembered.Radius = MathF.Max( v, 1e-4f );
		if ( Session != null ) Session.Radius = Remembered.Radius.Value;
		GeppettoTools.Redraw();
	}

	public void SetErasing( bool on )
	{
		Remembered.Erasing = on;
		if ( Session != null && !Session.IsStroking && !_armed ) Session.Erasing = on;
		_bar?.Refresh();
		GeppettoTools.Redraw();
	}

	// =========================================================================================
	// Strokes (kernel space, so tests can drive them without a camera)
	// =========================================================================================

	public bool IsStroking => Session?.IsStroking == true;

	public MeshHit? Hover( Vec3 origin, Vec3 direction )
	{
		_hover = Session?.Hover( origin, direction );
		if ( _armed && Session != null && !Session.IsStroking ) TryBegin( origin, direction );
		else if ( Session is { IsStroking: true } && Session.MoveTo( origin, direction ) > 0 ) ShowPreview( full: false );
		return _hover;
	}

	/// <summary>Left button down. Without an open layer (or with Shift) it opens the part under the ray.</summary>
	public void Press( Vec3 origin, Vec3 direction, bool ctrl, bool shift )
	{
		if ( Session == null || shift || PendingNewLayer != null )
		{
			var id = BodyUnder( origin, direction );
			if ( id == null ) { Refuse( "Click a part of the model to paint it." ); return; }
			if ( id != BodyId || Session == null || PendingNewLayer != null ) OpenBody( id );
			return;
		}

		if ( Session.A <= 0.001f || Session.Strength <= 0f )
		{
			Refuse( Session.A <= 0.001f
				? "Can't paint: the colour's alpha is 0, so the brush lays down nothing (erasing too). Raise the alpha in the colour swatch."
				: "Can't paint: Strength is 0. Raise it in the paint bar." );
			return;
		}

		_eraseMode = Session.Erasing;
		Session.Erasing = ctrl ? !_eraseMode : _eraseMode;
		_armed = true;
		TryBegin( origin, direction );
	}

	void TryBegin( Vec3 origin, Vec3 direction )
	{
		if ( Session!.BeginStroke( origin, direction ) )
		{
			_armed = false;
			ShowPreview( full: false );
		}
	}

	/// <summary>Left button up: the stroke becomes one undo step on the document.</summary>
	public void Release()
	{
		if ( Session == null ) { _armed = false; return; }
		if ( Session.IsStroking )
		{
			var stroke = Session.EndStroke();
			Session.Erasing = _eraseMode;
			if ( stroke != null ) Commit( stroke );
		}
		else if ( _armed )
		{
			_armed = false;
			Session.Erasing = _eraseMode;
			Refuse( "Missed the model — start or drag the stroke onto the part." );
		}
		_armed = false;
	}

	void Commit( PaintStroke stroke )
	{
		var f = Feature!;
		var before = f.Strokes?.ToList();
		var after = new List<PaintStroke>( before ?? new List<PaintStroke>() ) { stroke };
		Doc.Execute( new DelegateEditCommand( "Paint stroke",
			execute: () => f.ReplaceStrokes( after ),
			undo: () => f.ReplaceStrokes( before ) ), "Geppetto: paint stroke" );
	}

	// An undo / redo (or any other edit) changed the document: the session's canvas is its own copy, so
	// adopt the feature's stroke list again (s&box's Reload), or close if the layer went away.
	void OnDocChanged()
	{
		if ( _closed || Session == null || Feature == null || Session.IsStroking ) return;
		if ( !Doc.Studio.Features.Contains( Feature ) )
		{
			CloseSession();
			GeppettoToolContext.Say( "Paint: the layer was removed (undo); click a part to paint again." );
			return;
		}
		ApplyLayer( Session, Feature );
		var mine = Session.Strokes;
		var doc = (IReadOnlyList<PaintStroke>?)Feature.Strokes ?? Array.Empty<PaintStroke>();
		if ( mine.Count != doc.Count || !mine.Zip( doc ).All( p => ReferenceEquals( p.First, p.Second ) ) )
		{
			Session.Reload( doc );
			ShowPreview( full: true );
		}
	}

	// =========================================================================================
	// Viewport
	// =========================================================================================

	public bool Input( Camera3D camera, InputEvent e )
	{
		_camera = camera;
		switch ( e )
		{
			case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Bracketleft or Key.Bracketright:
			{
				float r = Session?.Radius ?? Remembered.Radius ?? 0.25f;
				SetRadius( k.Keycode == Key.Bracketright ? r * 1.15f : r / 1.15f );
				_bar?.Refresh();
				return true;
			}
			case InputEventKey ek when ek.Keycode == Key.Ctrl:
				_ctrlHeld = ek.Pressed;
				GeppettoTools.Redraw();
				return false;
			case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
			{
				var (o, d) = GeppettoBrush.Ray( camera, mb.Position, _bodyNode );
				_ctrlHeld = mb.CtrlPressed;
				if ( mb.Pressed ) Press( o, d, mb.CtrlPressed, mb.ShiftPressed );
				else Release();
				GeppettoTools.Redraw();
				return true;
			}
			case InputEventMouseMotion mm:
			{
				var (o, d) = GeppettoBrush.Ray( camera, mm.Position, _bodyNode );
				_ctrlHeld = mm.CtrlPressed;
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
		bool erasing = Session.IsStroking || _armed ? Session.Erasing : Session.Erasing ^ _ctrlHeld;
		GeppettoBrush.DrawRing( overlay, _camera, _bodyNode, hit, Session.Radius, erasing ? PaintEraseCursorColor : PaintCursorColor );
	}

	void ShowPreview( bool full )
	{
		if ( Session == null || _bodyNode == null ) { Session?.Canvas.ClearDirty(); return; }
		int res = Session.Resolution;
		if ( full || _texture == null || _texture.Width != res )
		{
			var all = new byte[res * res * 4];
			Session.BakePreview( all, 0, 0, res, res );
			if ( _texture == null || _texture.Width != res )
			{
				_texture = new GeppettoCanvasTexture( res, res, all );
				_preview.Show( _bodyNode, GeppettoBrush.BuildMesh( Session.Mesh, _texture.Material() ) );
			}
			else _texture.UpdateAll( all );
			if ( !_preview.IsShowing ) _preview.Show( _bodyNode, GeppettoBrush.BuildMesh( Session.Mesh, _texture.Material() ) );
		}
		else if ( Session.Canvas.HasDirty )
		{
			var c = Session.Canvas;
			int w = c.MaxX - c.MinX + 1, h = c.MaxY - c.MinY + 1;
			var sub = new byte[w * h * 4];
			Session.BakePreview( sub, c.MinX, c.MinY, w, h );
			_texture.UpdateRect( c.MinX, c.MinY, w, h, sub );
		}
		Session.Canvas.ClearDirty();
	}

	void CloseSession()
	{
		if ( Session is { IsStroking: true } ) Session.CancelStroke();
		if ( Session != null && _armed ) Session.Erasing = _eraseMode;
		_armed = false;
		Session = null;
		Feature = null;
		BodyId = null;
		_hover = null;
		_texture = null;
		_preview.Close();
	}

	public void Exit()
	{
		if ( _closed ) return;
		// A stroke in flight when the tool is put down is kept, not dropped (one undo step).
		if ( Session is { IsStroking: true } ) Release();
		CloseSession();
		_closed = true;
		Doc.Changed -= OnDocChanged;
		if ( _rollbackBefore is int rb )
		{
			Doc.Studio.RollbackIndex = rb;
			_rollbackBefore = null;
			Doc.Refresh();
		}
		if ( _bar != null && _plugin != null && GodotObject.IsInstanceValid( _bar ) )
		{
			_plugin.RemoveControlFromContainer( EditorPlugin.CustomControlContainer.SpatialEditorMenu, _bar );
			_bar.QueueFree();
		}
		_bar = null;
	}

	static bool Refuse( string message )
	{
		GD.PushWarning( $"[Geppetto] {message}" );
		return false;
	}
}
#endif
