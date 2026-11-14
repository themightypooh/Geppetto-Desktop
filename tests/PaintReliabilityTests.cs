using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Effigy;
using Marionette.Desktop;

// godlike-paint-reliability-contract (GODLIKE WAVE 1, P0): one failing-then-passing
// check per v10-v21 failure mode (wrong colour, eaten stroke, 3/4 camera, help panel,
// missed strokes), stroke colour equals picker colour for red/white/black, and 6/6
// strokes land on a head mesh from Front with zero red when none was picked.
// NOT yet registered in tests/TestRegistry.cs (coordinator-owned; the final integrator
// adds the one Register line — same as PaintChannelsTests). Implementation done is NOT
// runtime verified: no full build or app launch was run for this ticket (PO workflow
// override); checks below are verified by reading against the real session/camera APIs.
internal static class PaintReliabilityTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "paint-reliability", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		const float ViewW = 432f;
		const float ViewH = 432f;
		const float Centre = 216f;

		// The head: a sphere, radius 1 at the origin. Front is +X (the camera sits on +X
		// at yaw 0 / pitch 0 and looks at -X), so the face is the +X hemisphere.
		static PolyMesh HeadMesh() => Primitives.QuadSphere( 1f, 8, 0 );

		static OrbitCamera FrontCamera() => new()
		{
			Distance = 5f,
			YawDegrees = 0f,
			PitchDegrees = 0f,
			FovDegrees = 45f,
		};

		static OrbitCamera ThreeQuarterCamera() => new()
		{
			Distance = 5f,
			YawDegrees = 35f,
			PitchDegrees = 18f,
			FovDegrees = 45f,
		};

		(PaintReliabilityService Service, PaintSession Session, OrbitCamera Camera, OrbitCameraPaintView View) Setup()
		{
			var session = new PaintSession( HeadMesh(), 64 );
			session.Radius = 0.35f;
			session.Strength = 1f;
			var service = new PaintReliabilityService( session );
			var camera = FrontCamera();
			return (service, session, camera, new OrbitCameraPaintView( camera ));
		}

		// The six v21 strokes, as viewport points on the front of the head (432 base):
		// hair top, hair left, hair right, left eye, right eye, bridge.
		static (float X, float Y)[] FacePoints() => new (float, float)[]
		{
			(Centre, Centre - 70f),
			(Centre - 55f, Centre - 45f),
			(Centre + 55f, Centre - 45f),
			(Centre - 35f, Centre),
			(Centre + 35f, Centre),
			(Centre, Centre + 10f),
		};

		StrokeCommit StrokeAt( PaintReliabilityService service, OrbitCameraPaintView view, float x, float y )
		{
			var attempt = service.BeginStroke( view, new[] { (x, y) }, ViewW, ViewH );
			Require( attempt.Started, "stroke did not start at (" + x + ", " + y + "): " + attempt.Refusal );
			var commit = service.CommitStroke();
			Require( commit.Stroke is not null, "stroke at (" + x + ", " + y + ") committed nothing" );
			return commit;
		}

		byte[] CanvasBytes( PaintSession session ) => session.Canvas.Rgba.ToArray();

		int CountRed( PaintCanvas canvas )
		{
			var n = 0;
			for ( var i = 0; i < canvas.Rgba.Length; i += 4 )
				if ( canvas.Rgba[i] > 200 && canvas.Rgba[i + 1] < 80 && canvas.Rgba[i + 2] < 80 ) n++;
			return n;
		}

		int CountPainted( PaintCanvas canvas )
		{
			var n = 0;
			for ( var i = 0; i < canvas.Rgba.Length; i += 4 )
				if ( canvas.Rgba[i + 3] > 0 ) n++;
			return n;
		}

		int CountNearWhite( PaintCanvas canvas )
		{
			var n = 0;
			for ( var i = 0; i < canvas.Rgba.Length; i += 4 )
				if ( canvas.Rgba[i] > 250 && canvas.Rgba[i + 1] > 250 && canvas.Rgba[i + 2] > 250 && canvas.Rgba[i + 3] > 200 ) n++;
			return n;
		}

		int CountNearBlack( PaintCanvas canvas )
		{
			var n = 0;
			for ( var i = 0; i < canvas.Rgba.Length; i += 4 )
				if ( canvas.Rgba[i] < 5 && canvas.Rgba[i + 1] < 5 && canvas.Rgba[i + 2] < 5 && canvas.Rgba[i + 3] > 200 ) n++;
			return n;
		}

		Check( "red swatch drives the stroke colour", () =>
		{
			var (service, session, _, view) = Setup();
			service.SetBrushColour( 1f, 0f, 0f );
			var commit = StrokeAt( service, view, Centre, Centre );
			Require( commit.Stroke!.R == 1f && commit.Stroke.G == 0f && commit.Stroke.B == 0f,
				"stroke colour is not the picked red" );
			Require( commit.ColourVerified, "committed stroke failed colour verification" );
			Require( CountRed( session.Canvas ) > 0, "no red texel landed on the canvas" );
		} );

		Check( "white swatch drives the stroke colour", () =>
		{
			var (service, session, _, view) = Setup();
			service.SetBrushColour( 1f, 1f, 1f );
			var commit = StrokeAt( service, view, Centre, Centre );
			Require( commit.Stroke!.R == 1f && commit.Stroke.G == 1f && commit.Stroke.B == 1f,
				"stroke colour is not the picked white" );
			Require( commit.ColourVerified, "committed stroke failed colour verification" );
			Require( CountNearWhite( session.Canvas ) > 0, "no white texel landed on the canvas" );
		} );

		Check( "black swatch drives the stroke colour", () =>
		{
			var (service, session, _, view) = Setup();
			service.SetBrushColour( 0f, 0f, 0f );
			var commit = StrokeAt( service, view, Centre, Centre );
			Require( commit.Stroke!.R == 0f && commit.Stroke.G == 0f && commit.Stroke.B == 0f,
				"stroke colour is not the picked black" );
			Require( commit.ColourVerified, "committed stroke failed colour verification" );
			Require( CountNearBlack( session.Canvas ) > 0, "no black texel landed on the canvas" );
		} );

		Check( "picker wins over a stale session colour", () =>
		{
			// v14: the session held one colour while the picker said another, and the
			// stroke took the session's. The authority is re-asserted at the press.
			var (service, session, _, view) = Setup();
			session.R = 0f;
			session.G = 1f;
			session.B = 0f;
			service.SetBrushColour( 1f, 0f, 0f );
			Require( session.R == 1f && session.G == 0f && session.B == 0f,
				"authority did not overwrite the stale session colour" );
			var commit = StrokeAt( service, view, Centre, Centre );
			Require( commit.Stroke!.R == 1f && commit.Stroke.G == 0f && commit.Stroke.B == 0f,
				"stroke took the stale green instead of the picked red" );
		} );

		Check( "resilient begin tries each point until one hits", () =>
		{
			// v20: a stroke starting outside the silhouette landed nowhere. The first
			// candidate (400, 400) is outside the head's disc; the second is its centre.
			var (service, session, _, view) = Setup();
			service.SetBrushColour( 1f, 1f, 1f );
			var attempt = service.BeginStroke( view,
				new[] { (400f, 400f), (Centre, Centre) }, ViewW, ViewH );
			Require( attempt.Started, "resilient begin found no hit: " + attempt.Refusal );
			Require( attempt.HitCandidate == 1, "expected the second candidate to land, got " + attempt.HitCandidate );
			var commit = service.CommitStroke();
			Require( commit.Stroke is not null && commit.ColourVerified, "resilient stroke did not commit cleanly" );
			Require( CountPainted( session.Canvas ) > 0, "resilient stroke painted nothing" );
		} );

		Check( "all candidates missing refuses with no stroke", () =>
		{
			// v17: strokes that missed left nothing and reported nothing. A refusal must
			// say so, and the canvas must be untouched.
			var (service, session, _, view) = Setup();
			var before = CanvasBytes( session );
			var attempt = service.BeginStroke( view,
				new[] { (400f, 400f), (30f, 30f), (400f, 30f) }, ViewW, ViewH );
			Require( !attempt.Started, "a stroke started from three misses" );
			Require( attempt.Refusal == StrokeRefusal.NoCandidateHit,
				"wrong refusal: " + attempt.Refusal );
			Require( session.Strokes.Count == 0, "a missed stroke left a stroke behind" );
			Require( CanvasBytes( session ).SequenceEqual( before ), "a missed stroke changed the canvas" );
		} );

		Check( "open overlay refuses the stroke instead of eating it", () =>
		{
			// v15: a help panel opened mid-paint and ate the strokes silently. While an
			// overlay owns input, entry refuses with a reason and the canvas is untouched.
			var (service, session, _, view) = Setup();
			var before = CanvasBytes( session );
			service.OverlayOpen = true;
			var attempt = service.BeginStroke( view, new[] { (Centre, Centre) }, ViewW, ViewH );
			Require( !attempt.Started, "a stroke started while the overlay was open" );
			Require( attempt.Refusal == StrokeRefusal.OverlayOpen, "wrong refusal: " + attempt.Refusal );
			Require( CanvasBytes( session ).SequenceEqual( before ), "the refused stroke changed the canvas" );
			service.OverlayOpen = false;
			var commit = StrokeAt( service, view, Centre, Centre );
			Require( commit.Stroke is not null, "stroke did not land after the overlay closed" );
		} );

		Check( "entering paint snaps a 3/4 camera to front", () =>
		{
			// v15: the orbit left the paint camera at 3/4. Entry normalises the view first.
			var (service, _, camera, view) = Setup();
			camera.YawDegrees = 35f;
			camera.PitchDegrees = 18f;
			Require( service.EnterPaint( view ), "paint entry was refused with no overlay open" );
			Require( camera.YawDegrees == 0f && camera.PitchDegrees == 0f,
				"entry did not snap to front: yaw " + camera.YawDegrees + " pitch " + camera.PitchDegrees );
		} );

		Check( "entering paint refuses while an overlay is open", () =>
		{
			var (service, _, camera, view) = Setup();
			camera.YawDegrees = 35f;
			camera.PitchDegrees = 18f;
			service.OverlayOpen = true;
			Require( !service.EnterPaint( view ), "paint entry succeeded with the overlay open" );
			Require( camera.YawDegrees == 35f && camera.PitchDegrees == 18f,
				"a refused entry still moved the camera" );
		} );

		Check( "same viewport point paints a different place from 3/4 than from front", () =>
		{
			// v15/v17: strokes aimed at the face from a 3/4 camera landed in the wrong
			// place. The hit point for the same viewport point must move when the camera
			// does — otherwise the camera state cannot affect the stroke at all.
			var (service, _, _, _) = Setup();
			var front = new OrbitCameraPaintView( FrontCamera() );
			var threeQuarter = new OrbitCameraPaintView( ThreeQuarterCamera() );
			var moved = 0;
			foreach ( var (x, y) in FacePoints() )
			{
				var a = service.Hover( front, x, y, ViewW, ViewH );
				var b = service.Hover( threeQuarter, x, y, ViewW, ViewH );
				if ( a is null || b is null ) { moved++; continue; }
				if ( (a.Value.Point - b.Value.Point).Length > 0.3f ) moved++;
			}
			Require( moved >= 4, "only " + moved + " of 6 face points moved between front and 3/4 views" );
		} );

		Check( "front entry puts all six face points on the front hemisphere", () =>
		{
			var (service, session, _, view) = Setup();
			Require( service.EnterPaint( view ), "paint entry was refused" );
			foreach ( var (x, y) in FacePoints() )
			{
				var hit = service.Hover( view, x, y, ViewW, ViewH );
				Require( hit is not null, "face point (" + x + ", " + y + ") missed from front" );
				Require( hit!.Value.Point.x > 0.7f,
					"face point (" + x + ", " + y + ") hit x=" + hit.Value.Point.x + ", not the front hemisphere" );
			}
			Require( session.Strokes.Count == 0, "hovering left strokes behind" );
		} );

		Check( "window point converts to viewport point before the ray", () =>
		{
			// v17: Ray() expects viewport coords while touch points are screen pixels. A
			// window point must go through the viewport conversion first; feeding raw
			// window pixels as viewport coords must give a different ray.
			var (_, _, _, view) = Setup();
			var (vx, vy) = view.ToViewportPoint( 316f, 266f, 100f, 50f );
			Require( vx == 216f && vy == 216f, "viewport conversion gave (" + vx + ", " + vy + ")" );
			var (o1, d1) = view.RayFromViewport( vx, vy, ViewW, ViewH );
			var (o2, d2) = view.RayFromViewport( Centre, Centre, ViewW, ViewH );
			Require( (o1 - o2).Length < 1e-6f && (d1 - d2).Length < 1e-6f,
				"converted window point rays differently from the direct viewport point" );
			var (_, dRaw) = view.RayFromViewport( 316f, 266f, ViewW, ViewH );
			Require( (dRaw - d2).Length > 1e-3f, "raw window pixels gave the same ray as viewport coords" );
		} );

		Check( "six strokes land with the picked colour and zero red when none was picked", () =>
		{
			// v21 acceptance: 6/6 strokes, white hair + black glasses, zero red.
			var (service, session, _, view) = Setup();
			Require( service.EnterPaint( view ), "paint entry was refused" );
			var points = FacePoints();
			service.SetBrushColour( 1f, 1f, 1f );
			for ( var i = 0; i < 3; i++ )
			{
				var commit = StrokeAt( service, view, points[i].X, points[i].Y );
				Require( commit.ColourVerified, "white hair stroke " + i + " failed colour verification" );
			}
			service.SetBrushColour( 0f, 0f, 0f );
			for ( var i = 3; i < 6; i++ )
			{
				var commit = StrokeAt( service, view, points[i].X, points[i].Y );
				Require( commit.ColourVerified, "black glasses stroke " + i + " failed colour verification" );
			}
			Require( session.Strokes.Count == 6, "expected 6 committed strokes, got " + session.Strokes.Count );
			Require( CountRed( session.Canvas ) == 0, "red texels present although no red was picked" );
			Require( CountPainted( session.Canvas ) > 0, "six strokes painted nothing" );
		} );

		Check( "per-stroke undo restores the pre-stroke map, redo repaints it", () =>
		{
			var (service, session, _, view) = Setup();
			var before = CanvasBytes( session );
			service.SetBrushColour( 1f, 0f, 0f );
			StrokeAt( service, view, Centre, Centre );
			var painted = CanvasBytes( session );
			Require( !painted.SequenceEqual( before ), "the stroke changed nothing to undo" );
			Require( service.UndoLastStroke(), "undo refused with one stroke to undo" );
			Require( CanvasBytes( session ).SequenceEqual( before ), "undo did not restore the pre-stroke map" );
			Require( service.UndoDepth == 0 && service.RedoDepth == 1, "undo/redo depths wrong after undo" );
			Require( service.RedoStroke(), "redo refused after undo" );
			Require( CanvasBytes( session ).SequenceEqual( painted ), "redo did not restore the stroke" );
			Require( service.UndoLastStroke(), "undo after redo should succeed" );
			Require( CanvasBytes( session ).SequenceEqual( before ), "second undo did not restore the pre-stroke map" );
		} );

		Check( "discard abandons the preview without committing or autosaving", () =>
		{
			var (service, session, _, view) = Setup();
			var autosaves = 0;
			service.OnStrokeCommitted = () => autosaves++;
			var before = CanvasBytes( session );
			service.SetBrushColour( 1f, 0f, 0f );
			var attempt = service.BeginStroke( view, new[] { (Centre, Centre) }, ViewW, ViewH );
			Require( attempt.Started, "preview stroke did not start" );
			Require( CountPainted( session.Canvas ) > 0, "the in-flight stroke does not preview on the canvas" );
			service.DiscardStroke();
			Require( session.Strokes.Count == 0, "a discarded stroke was committed" );
			Require( CanvasBytes( session ).SequenceEqual( before ), "discard did not restore the pre-stroke canvas" );
			Require( autosaves == 0, "discard fired autosave" );
		} );

		Check( "autosave fires once per committed stroke only", () =>
		{
			var (service, _, _, view) = Setup();
			var autosaves = 0;
			service.OnStrokeCommitted = () => autosaves++;
			service.SetBrushColour( 1f, 1f, 1f );
			StrokeAt( service, view, Centre, Centre );
			StrokeAt( service, view, Centre + 40f, Centre );
			Require( autosaves == 2, "expected 2 autosaves for 2 commits, got " + autosaves );
			service.BeginStroke( view, new[] { (Centre, Centre) }, ViewW, ViewH );
			service.DiscardStroke();
			Require( autosaves == 2, "discard fired autosave" );
			service.OverlayOpen = true;
			service.BeginStroke( view, new[] { (Centre, Centre) }, ViewW, ViewH );
			Require( autosaves == 2, "a refused stroke fired autosave" );
			service.OverlayOpen = false;
			Require( service.UndoLastStroke(), "undo refused" );
			Require( autosaves == 2, "undo fired autosave" );
		} );

		Check( "one stroke completes inside the 16 ms latency target", () =>
		{
			// Acceptance target, measured here in-test (not run by this ticket's author:
			// no suite run per the PO override; the final build measures it for real).
			var (service, _, _, view) = Setup();
			service.SetBrushColour( 1f, 1f, 1f );
			var clock = Stopwatch.StartNew();
			var attempt = service.BeginStroke( view, new[] { (Centre, Centre) }, ViewW, ViewH );
			Require( attempt.Started, "timed stroke did not start" );
			service.MoveTo( view, Centre + 10f, Centre, ViewW, ViewH );
			service.MoveTo( view, Centre + 20f, Centre, ViewW, ViewH );
			var commit = service.CommitStroke();
			clock.Stop();
			Require( commit.Stroke is not null, "timed stroke committed nothing" );
			Require( clock.Elapsed.TotalMilliseconds < 16,
				"one stroke took " + clock.Elapsed.TotalMilliseconds + " ms, over the 16 ms target" );
		} );
	}
}
