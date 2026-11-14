using System;
using System.Collections.Generic;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Paint reliability contract — godlike-paint-reliability-contract (GODLIKE WAVE 1, P0).
///
/// The v10–v21 Jensen passes (2026-11-12, Mobile) proved the paint path fragile in four
/// specific ways, and this service is the Desktop contract that none of them can recur:
///
///  (v10/v14) WRONG COLOUR — the session's R/G/B did not drive the stroke's colour (the UI
///  Colour control did), and swatch taps never changed the brush colour either. Fix: one
///  colour authority (<see cref="Authority"/>). Every picker/swatch write goes through
///  <see cref="SetBrushColour(float,float,float)"/>, which writes the kernel session floats
///  AND any attached 2D texture brush from the same values, and the authority is re-asserted
///  onto the session immediately before every stroke begins. The committed stroke's colour
///  is verified against the authority on commit.
///  (v15) EATEN STROKE — a full-screen "Painting" help panel opened mid-paint and ate the
///  strokes (its button is "Back", not "Close"). Desktop has no such panel, but any modal or
///  overlay that captures pointer input is the same failure. Fix: <see cref="OverlayOpen"/>
///  — the host sets it while a modal/panel owns input, and stroke entry refuses with a
///  reason instead of silently eating the gesture. Paint entry itself refuses while it is open.
///  (v15/v17) 3/4 CAMERA + WRONG COORDS — the orbit left the paint camera at 3/4, and Ray()
///  expects viewport coords while touch points are screen pixels stretched 2.5x from a 432
///  base. Fix: <see cref="EnterPaint"/> snaps the view to Front (yaw 0 / pitch 0, the Num1
///  preset in Program.cs; -90 for FBX models) before any stroke, and
///  <see cref="IPaintView.ToViewportPoint"/> is the one window-to-viewport conversion
///  (Desktop's ReelToViewport): a ray is only ever built from viewport-relative coords.
///  (v20/v21) MISSED STROKES — only 3/6 strokes landed (starts outside the silhouette).
///  Fix: resilient begin — <see cref="BeginStroke"/> tries each candidate ray in order
///  until one hits, and reports which candidate landed.
///
/// What the kernel session already gives, reused rather than re-implemented: the in-flight
/// stroke previews on <see cref="PaintSession.Canvas"/> and commits on EndStroke;
/// <see cref="PaintSession.CancelStroke"/> abandons a preview; undo is the feature tree's
/// undo, so this service implements per-stroke undo as a snapshot stack over
/// <see cref="PaintSession.Reload"/> (replay is deterministic, so reloading the pre-stroke
/// list restores the pre-stroke map exactly). Autosave is a host callback
/// (<see cref="OnStrokeCommitted"/>) fired once per committed stroke — the host wires it to
/// AutosaveScheduler; the service itself never touches the filesystem.
///
/// WIRING (final integrator): construct one service per paint session; route the picker's
/// colour through SetBrushColour; call EnterPaint from the paint entry point with the
/// OrbitCameraPaintView adapter; set OverlayOpen from the host's modal/panel state; wire
/// OnStrokeCommitted to AutosaveScheduler.TriggerAutosaveAsync. No shared entry point is
/// edited by this ticket.
/// </summary>
public readonly record struct PaintColour( float R, float G, float B )
{
	public static PaintColour White => new( 1f, 1f, 1f );
	public static PaintColour Black => new( 0f, 0f, 0f );
	public static PaintColour Red => new( 1f, 0f, 0f );

	public (byte R, byte G, byte B) ToBytes() => (
		(byte)Math.Round( Math.Clamp( R, 0f, 1f ) * 255f ),
		(byte)Math.Round( Math.Clamp( G, 0f, 1f ) * 255f ),
		(byte)Math.Round( Math.Clamp( B, 0f, 1f ) * 255f ) );
}

/// <summary>Why a stroke attempt was refused. A refusal reports its reason; a stroke is
/// never silently eaten.</summary>
public enum StrokeRefusal
{
	None,
	OverlayOpen,
	AlreadyStroking,
	NoCandidateHit,
	InvalidBrush,
}

/// <summary>The outcome of a resilient stroke begin: whether a stroke started, and which
/// candidate ray landed it (-1 when none did).</summary>
public sealed record StrokeAttempt( bool Started, int HitCandidate, StrokeRefusal Refusal );

/// <summary>The outcome of a stroke commit: the committed stroke (null when there was no
/// stroke to commit), and whether its colour matches the colour authority.</summary>
public sealed record StrokeCommit( PaintStroke? Stroke, bool ColourVerified );

/// <summary>
/// The view half of paint entry, supplied by the host. The service never touches a camera
/// directly, so the contract is testable with a fake and the host adapter is three lines.
/// </summary>
public interface IPaintView
{
	/// <summary>Snap the view to Front: yaw 0 / pitch 0 (yaw -90 for FBX models, the Num1
	/// preset in Program.cs).</summary>
	void SnapToFront( bool fbxModel = false );

	/// <summary>Build a world-space ray from a VIEWPORT-relative point. Never feed this raw
	/// window pixels — convert with <see cref="ToViewportPoint"/> first.</summary>
	(Vec3 Origin, Vec3 Direction) RayFromViewport( float x, float y, float viewportWidth, float viewportHeight );

	/// <summary>The one window-to-viewport conversion (Desktop's ReelToViewport): subtract
	/// the viewport's origin in the window. The Y flip lives inside OrbitCamera.ScreenRay.</summary>
	(float X, float Y) ToViewportPoint( float windowX, float windowY, float viewportOriginX, float viewportOriginY );
}

/// <summary>The host adapter over the real <see cref="OrbitCamera"/>: Front is yaw 0 /
/// pitch 0 (Program.cs Num1; -90 for FBX models), and rays come from
/// <see cref="OrbitCamera.ScreenRay"/>, which takes viewport-relative pixels.</summary>
public sealed class OrbitCameraPaintView : IPaintView
{
	readonly OrbitCamera _camera;

	public OrbitCameraPaintView( OrbitCamera camera ) =>
		_camera = camera ?? throw new ArgumentNullException( nameof( camera ) );

	public OrbitCamera Camera => _camera;

	public void SnapToFront( bool fbxModel = false )
	{
		_camera.YawDegrees = fbxModel ? -90f : 0f;
		_camera.PitchDegrees = 0f;
	}

	public (Vec3 Origin, Vec3 Direction) RayFromViewport( float x, float y, float viewportWidth, float viewportHeight )
	{
		var (o, d) = _camera.ScreenRay( x, y, viewportWidth, viewportHeight );
		return (new Vec3( o.X, o.Y, o.Z ), new Vec3( d.X, d.Y, d.Z ));
	}

	public (float X, float Y) ToViewportPoint( float windowX, float windowY, float viewportOriginX, float viewportOriginY ) =>
		(windowX - viewportOriginX, windowY - viewportOriginY);
}

/// <summary>
/// One paint session's reliability contract. Owns the brush colour, guards stroke entry,
/// and adds the per-stroke undo + autosave the kernel session deliberately does not carry
/// (undo is the feature tree's undo; this service is the session-level snapshot of it).
/// </summary>
public sealed class PaintReliabilityService
{
	readonly PaintSession _session;
	readonly Stack<List<PaintStroke>> _undo = new();
	readonly Stack<List<PaintStroke>> _redo = new();
	List<PaintStroke>? _preStroke;
	TexturePaintService.TextureBrush? _textureBrush;

	public PaintReliabilityService( PaintSession session ) =>
		_session = session ?? throw new ArgumentNullException( nameof( session ) );

	public PaintSession Session => _session;

	/// <summary>The one brush colour. The session floats, any attached texture brush, and
	/// every committed stroke are all driven from this value — never from a second store.</summary>
	public PaintColour Authority { get; private set; } = PaintColour.White;

	/// <summary>True while a modal/panel owns pointer input. Stroke entry and paint entry
	/// refuse while this is set, instead of letting the overlay eat the gesture. The host
	/// sets this from its modal/panel state; the service cannot know it on its own.</summary>
	public bool OverlayOpen { get; set; }

	/// <summary>Fired once per committed stroke. The host wires this to autosave
	/// (AutosaveScheduler.TriggerAutosaveAsync); the service never touches the filesystem.
	/// Not fired for a discarded, refused, or undone stroke.</summary>
	public Action? OnStrokeCommitted { get; set; }

	public bool IsStroking => _session.IsStroking;
	public int UndoDepth => _undo.Count;
	public int RedoDepth => _redo.Count;

	/// <summary>The single colour write path: picker and swatch writes come here, and the
	/// session (plus any attached texture brush) is updated from the same values.</summary>
	public void SetBrushColour( float r, float g, float b )
	{
		Authority = new PaintColour( Clamp01( r ), Clamp01( g ), Clamp01( b ) );
		ApplyColourToSession();
	}

	public void SetBrushColourBytes( byte r, byte g, byte b ) =>
		SetBrushColour( r / 255f, g / 255f, b / 255f );

	/// <summary>Attach the 2D texture brush so it is driven from the same authority as the
	/// 3D session. Without this the two brush stores can disagree — the v14 failure.</summary>
	public void AttachTextureBrush( TexturePaintService.TextureBrush brush )
	{
		_textureBrush = brush ?? throw new ArgumentNullException( nameof( brush ) );
		ApplyColourToSession();
	}

	/// <summary>Re-assert the authority onto the session (and attached texture brush).
	/// Called on every colour write and immediately before every stroke begins, so a
	/// stale session colour — whatever wrote it — can never reach a stroke.</summary>
	public void ApplyColourToSession()
	{
		_session.R = Authority.R;
		_session.G = Authority.G;
		_session.B = Authority.B;

		if ( _textureBrush is not null )
		{
			var (r, g, b) = Authority.ToBytes();
			_textureBrush.R = r;
			_textureBrush.G = g;
			_textureBrush.B = b;
		}
	}

	/// <summary>Paint entry: refuse while an overlay owns input, otherwise snap the view to
	/// Front and re-assert the brush colour. Returns false when entry was refused.</summary>
	public bool EnterPaint( IPaintView view, bool fbxModel = false )
	{
		if ( view is null ) throw new ArgumentNullException( nameof( view ) );
		if ( OverlayOpen ) return false;

		view.SnapToFront( fbxModel );
		ApplyColourToSession();
		return true;
	}

	/// <summary>Resilient stroke begin (v20/v21): try each candidate viewport point in
	/// order until one's ray hits. Returns which candidate landed, or the refusal reason
	/// when none did. A stroke that began outside the silhouette is retried from the next
	/// point instead of silently landing nowhere.</summary>
	public StrokeAttempt BeginStroke( IPaintView view, IReadOnlyList<(float X, float Y)> viewportPoints, float viewportWidth, float viewportHeight )
	{
		if ( view is null ) throw new ArgumentNullException( nameof( view ) );
		if ( OverlayOpen ) return new StrokeAttempt( false, -1, StrokeRefusal.OverlayOpen );
		if ( _session.IsStroking ) return new StrokeAttempt( false, -1, StrokeRefusal.AlreadyStroking );
		if ( !float.IsFinite( _session.Radius ) || _session.Radius <= 0f ) return new StrokeAttempt( false, -1, StrokeRefusal.InvalidBrush );
		if ( viewportPoints is null || viewportPoints.Count == 0 ) return new StrokeAttempt( false, -1, StrokeRefusal.NoCandidateHit );

		// The authority is re-asserted here, at the press — the v14 fix: whatever the
		// session held before this call cannot leak into the stroke.
		ApplyColourToSession();
		_preStroke = new List<PaintStroke>( _session.Strokes );

		for ( var i = 0; i < viewportPoints.Count; i++ )
		{
			var (o, d) = view.RayFromViewport( viewportPoints[i].X, viewportPoints[i].Y, viewportWidth, viewportHeight );
			if ( _session.BeginStroke( o, d ) )
				return new StrokeAttempt( true, i, StrokeRefusal.None );
		}

		_preStroke = null;
		return new StrokeAttempt( false, -1, StrokeRefusal.NoCandidateHit );
	}

	/// <summary>Carry the in-flight stroke to a new viewport point. A ray that misses does
	/// not end the stroke — the session returns 0 samples and the drag continues.</summary>
	public int MoveTo( IPaintView view, float x, float y, float viewportWidth, float viewportHeight )
	{
		if ( view is null ) throw new ArgumentNullException( nameof( view ) );
		if ( !_session.IsStroking ) return 0;

		var (o, d) = view.RayFromViewport( x, y, viewportWidth, viewportHeight );
		return _session.MoveTo( o, d );
	}

	/// <summary>Where the cursor sits on the surface for a viewport point, or null on a miss.
	/// Read-only: no stroke state changes.</summary>
	public MeshHit? Hover( IPaintView view, float x, float y, float viewportWidth, float viewportHeight )
	{
		if ( view is null ) throw new ArgumentNullException( nameof( view ) );

		var (o, d) = view.RayFromViewport( x, y, viewportWidth, viewportHeight );
		return _session.Hover( o, d );
	}

	/// <summary>Commit the in-flight stroke: the preview becomes paint, one undo entry is
	/// recorded, the stroke colour is verified against the authority, and autosave fires.
	/// Returns a commit with a null stroke (and fires nothing) when no stroke is in flight.</summary>
	public StrokeCommit CommitStroke()
	{
		if ( !_session.IsStroking ) return new StrokeCommit( null, false );

		var stroke = _session.EndStroke();

		if ( stroke is null )
		{
			_preStroke = null;
			return new StrokeCommit( null, false );
		}

		if ( _preStroke is not null )
		{
			_undo.Push( _preStroke );
			_redo.Clear();
			_preStroke = null;
		}

		// Scalar channels paint Value as a grey carried in R/G/B, so only the colour
		// channel's stroke colour is the authority's to verify.
		var verified = _session.Channel != PaintChannel.Color
			|| (Math.Abs( stroke.R - Authority.R ) < 1e-6f
				&& Math.Abs( stroke.G - Authority.G ) < 1e-6f
				&& Math.Abs( stroke.B - Authority.B ) < 1e-6f);

		OnStrokeCommitted?.Invoke();
		return new StrokeCommit( stroke, verified );
	}

	/// <summary>Abandon the stroke in flight: the preview is dropped (the session rebuilds
	/// the visible canvas from the committed one), no undo entry is recorded, autosave does
	/// not fire.</summary>
	public void DiscardStroke()
	{
		if ( _session.IsStroking ) _session.CancelStroke();
		_preStroke = null;
	}

	/// <summary>Per-stroke undo: reload the stroke list as it stood before the last
	/// committed stroke. Replay is deterministic, so the pre-stroke map is restored
	/// exactly. Refused while a stroke is in flight, or when there is nothing to undo.</summary>
	public bool UndoLastStroke()
	{
		if ( _session.IsStroking || _undo.Count == 0 ) return false;

		_redo.Push( new List<PaintStroke>( _session.Strokes ) );
		_session.Reload( _undo.Pop() );
		return true;
	}

	/// <summary>Per-stroke redo: reload the stroke list as it stood before the last undo.</summary>
	public bool RedoStroke()
	{
		if ( _session.IsStroking || _redo.Count == 0 ) return false;

		_undo.Push( new List<PaintStroke>( _session.Strokes ) );
		_session.Reload( _redo.Pop() );
		return true;
	}

	static float Clamp01( float v ) => Math.Clamp( v, 0f, 1f );
}
