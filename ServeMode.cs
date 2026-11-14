using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// --serve: the viewport for the cabinet UI (geppetto-desktop/cabinet/host.py). Renders headless
/// (EGL, no window) into an offscreen target and hands each frame to the host through a shared
/// memory file, top-down RGBA. Commands come in on stdin, one per line; events go out on stdout as
/// lines starting with '@' followed by JSON (anything else on stdout is log noise).
///
///   orbit DX DY | pan DX DY | zoom F | dolly N | size W H | frame | play 0|1 | time T | bones 0|1 | grid 0|1
///   wire N | select BONE | pick X Y | clip PATH | clip off | rest | loop 0|1 | ack | quit
///   clip PATH on an .effigy (or a live-run pane) builds a player on the pane's own rig
///   when there is none yet, so a .riganim previews without an FBX pane; `rest` drops
///   back to the bind pose. A clip set in a live pane survives live revisions until `rest`.
///   keys | kmove D T:F.. | kdel T:F.. | kease E T:F.. | kins F T.. | undo | redo | save
///   texpaint BODY path=X,Y;X,Y.. [channel=color|rough|metal|height|mask] [color=R,G,B]
///     [radius=TEXELS] [strength=0..1] [falloff=smooth|linear|sharp|constant] [spacing=0.5]
///     [erase=0|1]: commit one texel stroke to the Paint layer on BODY (see the
///     texpaint script verb for the same commit in a script). The stroke goes into the
///     layer's log, the studio rebuilds, and the pane swaps to a fresh snapshot -- the
///     fingerprint mechanism, not a dirty-rect push: a dirty rect only fixes the GL
///     upload, while a committed stroke + fresh snapshot is what survives a rebuild, a
///     save, and the next snapshot promotion. Refused on a live-run pane (its studio
///     changes every revision) and when the body has no Paint layer naming it.
///   ortho 0|1 | refadd VIEW PATH | refset I key=value.. | refdel I | refsave | refs
///
/// A new frame is only written after the host acks the last one, so it never reads a half-written
/// frame and a slow host simply gets fewer frames.
/// </summary>
public static class ServeMode
{
	static readonly object OutLock = new();

	static void Emit( JsonObject ev )
	{
		lock ( OutLock ) { Console.Out.Write( "@" + ev.ToJsonString() + "\n" ); Console.Out.Flush(); }
	}

	public static int Run( DesktopOptions options )
	{
		int width = options.Width, height = options.Height;
		var commands = new BlockingCollection<string>();
		var reader = new Thread( () =>
		{
			string line;
			while ( (line = Console.In.ReadLine()) is not null ) commands.Add( line.Trim() );
			commands.Add( "quit" );
		} ) { IsBackground = true };
		reader.Start();

		// --empty: no model at all. Clear the default model path first, so nothing
		// downstream mistakes the default FBX for the scene, then skip the loader
		// entirely and use the same empty mesh the live-run viewers use.
		if ( options.Empty ) options.Model = "";
		Emit( new JsonObject { ["ev"] = "loading", ["model"] = options.Model } );
		var loader = new DocumentLoader();
		if ( options.WatchRun is null && !options.Empty )
		{
			loader.Start( options.Model, options.Clip, options.Animate, options.Time );
			loader.Completion.GetAwaiter().GetResult();
			if ( loader.Status != DocumentLoader.State.Ready )
			{
				Emit( new JsonObject { ["ev"] = "error", ["msg"] = (loader.Error ?? "load failed").Split( '\n' )[0] } );
				return 1;
			}
		}
		ViewportMesh mesh = options.WatchRun is not null || options.Empty ? AiLivePreview.EmptyMesh() : (ViewportMesh)loader.Mesh;
		var player = options.WatchRun is null && !options.Empty ? loader.Player : null;
		// Lane E: the clip currently playing in this pane, if any — including one
		// set with `clip` after load, so a live revision can re-apply it instead of
		// silently dropping back to the rest pose (player is reset per revision).
		string activeClipPath = options.Clip;
		bool activeClipPlaying = true;
		AiLivePreview follow = options.WatchRun is null ? null : new AiLivePreview( options.WatchRun );
		long displayedRevision = 0, eventSequence = 0;
		string displayedRun = null;
		bool framedLive = false;
		bool everFramedLive = false;           // set once: later runs keep the camera the user left
		int holdFrame = 0;                     // revisions still to come in without moving the camera (a drop into the scene)
		bool autoFrame = false;                // live: reframe every revision, keeping the angle, so a growing model stays in view
		float spin = 0;                        // turntable, degrees of yaw per second (0 = still)
		// the part a live step touched (`focus NAME`) glows; `framebody` glides the camera onto it
		var focus = new FocusLayer();
		string focusName = "";
		ViewportMesh focusMesh = null;
		Vector3 glideFromTarget = default, glideToTarget = default;
		float glideFromDistance = 0, glideToDistance = 0, glideSeconds = .8f;
		float glideFromYaw = 0, glideToYaw = 0, glideFromPitch = 0, glideToPitch = 0;
		double glideAt = -1;
		// fly camera (s&box: hold RMB): the held WASD/QE as forward/right/up, Shift for fast, and the wheel
		// notches on the speed; the speed itself goes with the model's size, so a dagger and a body both feel right
		var flyMove = Vector3.Zero;
		bool flyFast = false;
		int flySteps = 0;
		// send-off: a finished run's model is pressed flat onto a card facing the camera and the card turns
		// once to show it's paper-thin. The last frame is the card alone, square on, without grid or ground
		// shadow and flagged "card", for the cabinet to cut out as a sticker and file away; then the viewport
		// is left empty. Display only (SceneRenderer.DisplayTransform): the camera, lighting and mesh are untouched.
		double sendoffAt = -1;
		bool sendoffCard = false, gridBeforeCard = false, groundBeforeCard = false;
		Vector3 sendoffTarget = default;
		float sendoffDistance = 0, sendoffYaw = 0;
		const float SendoffPress = 0.35f, SendoffTurn = 0.6f;
		string previewPath = options.WatchRun is null ? options.Model : "";
		string previewError = null, publishedAt = null;

		using var context = new HeadlessGlContext();
		var gl = context.Gl;
		using var renderer = new SceneRenderer( gl );
		renderer.View = options.View;
		// The clip's reference props: the same lit path, their own ModelDoc and materials.
		renderer.Props = loader.Snapshot?.Props ?? Array.Empty<PropScene>();
		using var overlay = new SkeletonOverlay( gl );
		// The document's reference pictures, drawn behind the model. The list here is the one being
		// shown: the cabinet edits it, and a new revision only replaces it when the document's own list
		// changed, so a picture nudged by hand during a live run stays where it was put.
		using var refs = new ReferenceImagePass( gl );
		string refsSignature = null, refsProblems = "";
		static string Signature( IEnumerable<ReferenceImage> list ) =>
			string.Join( "\n", list.Select( StudioDocument.ReferenceImageLine ) );
		void SyncRefs( IEnumerable<ReferenceImage> fromDocument, string documentPath )
		{
			var list = (fromDocument ?? Array.Empty<ReferenceImage>()).Where( i => i is not null ).ToList();
			var signature = Signature( list );
			refs.BaseDirectory = string.IsNullOrEmpty( documentPath ) ? null : Path.GetDirectoryName( Path.GetFullPath( documentPath ) );
			if ( signature == refsSignature ) return;
			refsSignature = signature;
			refs.Images.Clear();
			refs.Images.AddRange( list.Select( i => i.Clone() ) );
			EmitRefs();
		}
		void EmitRefs( string note = null )
		{
			var list = new JsonArray();
			foreach ( var i in refs.Images )
				list.Add( new JsonObject
				{
					["view"] = i.View, ["path"] = i.Path, ["name"] = Path.GetFileName( i.Path ), ["height"] = i.Height,
					["ox"] = i.Offset.x, ["oy"] = i.Offset.y, ["depth"] = i.Depth, ["opacity"] = i.Opacity, ["visible"] = i.Visible,
					["line"] = BuildLine( i ),
				} );
			Emit( new JsonObject
			{
				["ev"] = "refs", ["images"] = list, ["problems"] = refsProblems, ["note"] = note ?? "",
				["saveable"] = follow is null && options.Model.EndsWith( ".effigy", StringComparison.OrdinalIgnoreCase ),
				["changed"] = Signature( refs.Images ) != refsSignature,
			} );
		}
		static string BuildLine( ReferenceImage i ) => string.Create( System.Globalization.CultureInfo.InvariantCulture,
			$"refimage {i.View} {(i.Path.Contains( ' ' ) ? "\"" + i.Path + "\"" : i.Path)} height={i.Height:0.###} offset={i.Offset.x:0.###},{i.Offset.y:0.###} depth={i.Depth:0.###} opacity={i.Opacity:0.##}{(i.Visible ? "" : " visible=0")}" );
		ReferenceImage RefAt( string index )
		{
			int n = int.Parse( index, System.Globalization.CultureInfo.InvariantCulture );
			if ( n < 0 || n >= refs.Images.Count ) throw new ArgumentException( $"No reference picture {n}; there are {refs.Images.Count}." );
			return refs.Images[n];
		}
		// Face / edge / vertex picks on the shown document's bodies, lit through renderer.Live.
		var sub = new SubSelection();
		string subPath = null;
		renderer.Live.Layers.AddRange( sub.Layers() );
		renderer.Live.Layers.Add( focus );
		// 2026-11-14 step flash: the bodies a shown step made (green) or changed (amber) light up, then fade,
		// worked out from the meshes themselves, so paint, sculpt, material steps and scrubs show too.
		var flashNew = new FocusLayer( new Vector3( 1f, .7f, .2f ), new Vector3( .75f, .45f, .08f ), .55f );   // the tint is amber for both
		var flashEdit = new FocusLayer( new Vector3( 1f, .7f, .2f ), new Vector3( .75f, .45f, .08f ), .55f );
		var outlineNew = new FocusOutline( flashNew, new Vector3( .3f, 1f, .4f ) );      // green: new part
		var outlineEdit = new FocusOutline( flashEdit, new Vector3( 1f, .65f, .1f ) );   // amber: edited part
		renderer.Live.Layers.Add( flashNew ); renderer.Live.Layers.Add( flashEdit );
		renderer.Live.Layers.Add( outlineNew ); renderer.Live.Layers.Add( outlineEdit );
		double flashAt = -1;
		const double FlashHold = 1.5, FlashFade = 0.8;
		List<int> PicksOf( string[] ids )
		{
			var found = new List<int>();
			for ( int i = 0; i < mesh.BodyPicks.Length; i++ )
				if ( Array.IndexOf( ids, mesh.BodyPicks[i].Id ) >= 0 ) found.Add( i );
			return found;
		}
		JsonArray Names( string[] ids )
		{
			var names = new JsonArray();
			foreach ( int i in PicksOf( ids ) ) names.Add( mesh.BodyPicks[i].Name ?? mesh.BodyPicks[i].Id );
			return names;
		}
		void Refocus()
		{
			focusMesh = mesh;
			focus.Set( mesh.Picker, FocusLayer.Match( mesh.BodyPicks, focusName ) );
			renderer.ResetTemporalHistory();
		}
		void SubLoad()
		{
			if ( (sub.Mode == SubMode.Body && !sub.Rig) || subPath == previewPath ) return;
			subPath = previewPath;
			var studio = follow is null ? loader.Session?.Studio
				: string.IsNullOrEmpty( previewPath ) ? null : StudioCache.Load( previewPath, out _ );
			sub.Reload( studio?.Bodies ?? (IEnumerable<Body>)Array.Empty<Body>() );
		}
		void SubEmit()
		{
			var ev = sub.ToJson();
			ev["path"] = previewPath; ev["revision"] = displayedRevision; ev["runId"] = displayedRun;
			Emit( ev );
		}
		var target = new CaptureTarget( gl, width, height );
		var camera = new OrbitCamera();
		// s&box idle pick: a click within EdgePickPixels of a face's rim takes the edge
		float EdgeRadius( Vector3 p ) =>
			MathF.Tan( camera.FovDegrees * MathF.PI / 360f ) * Vector3.Distance( camera.Eye, p ) / (Math.Max( 1, height ) * .5f) * SubSelection.EdgePickPixels;
		// box / lasso select: a world point in viewport pixels, null behind the eye
		Vector2? ToScreen( Vector3 p )
		{
			var clip = Vector4.Transform( new Vector4( p, 1 ), camera.ViewMatrix() * camera.ProjectionMatrix( width / (float)Math.Max( 1, height ) ) );
			if ( clip.W <= 1e-6f ) return null;
			return new Vector2( (clip.X / clip.W + 1) * .5f * width, (1 - clip.Y / clip.W) * .5f * height );
		}
		bool fbx = options.Model.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase );
		void Reframe()
		{
			// Clips can move the arms far outside the FBX bind-pose bounds. Frame
			// the evaluated mesh so the cabinet does not crop the posed character.
			player?.Step(0);
			var bounds = mesh.Picker.Bounds(player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones);
			var centre = mesh.Centre;
			float radius = mesh.Radius;
			if (float.IsFinite(bounds.Min.Length()) && float.IsFinite(bounds.Max.Length()))
			{
				centre = (bounds.Min + bounds.Max) * 0.5f;
				radius = Math.Max((bounds.Max - bounds.Min).Length() * 0.5f, 0.1f);
			}
			FrameAt( centre, radius );
		}
		void FrameAt( Vector3 centre, float radius )
		{
			camera.Frame( centre, radius );
			float vertical = camera.FovDegrees * MathF.PI / 360;
			float horizontal = MathF.Atan(MathF.Tan(vertical) * width / Math.Max(1f, height));
			camera.Distance = Math.Max(camera.Distance, radius * 1.15f / MathF.Sin(Math.Min(vertical, horizontal)));
			camera.YawDegrees = fbx ? -55 : 35;
			camera.PitchDegrees = 18;
			renderer.ResetTemporalHistory();
		}
		// Nothing built yet but a reference picture (a build's first line): frame the picture, or it is a
		// speck from the default 200-unit camera. Once per picture list, so an orbit while empty stays.
		string framedRefs = null;
		void FrameRefs()
		{
			if ( mesh.TriangleCount > 0 || everFramedLive || refsSignature == framedRefs || refs.Bounds() is not { } box ) return;
			framedRefs = refsSignature;
			FrameAt( (box.Min + box.Max) * 0.5f, Math.Max( (box.Max - box.Min).Length() * 0.5f, 0.1f ) );
		}
		// glide to these bodies (none: the whole model): close enough that the part fills even a small pane
		void Glide( IReadOnlyList<int> bodies, float seconds )
		{
			var all = mesh.Picker.Bounds( player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones );
			var (min, max) = all;
			if ( bodies.Count > 0 )
			{
				min = new Vector3( float.PositiveInfinity ); max = new Vector3( float.NegativeInfinity );
				foreach ( int b in bodies )
				{
					var bb = mesh.Picker.Bounds( b, player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones );
					min = Vector3.Min( min, bb.Min ); max = Vector3.Max( max, bb.Max );
				}
			}
			if ( !float.IsFinite( min.Length() ) || !float.IsFinite( max.Length() ) ) return;
			float whole = Math.Max( (all.Max - all.Min).Length() * .5f, .1f );
			// a part fills the pane (a small pane still shows it), the whole model keeps a margin
			float radius = bodies.Count > 0 ? Math.Max( (max - min).Length() * .55f, whole * .07f ) : Math.Max( (max - min).Length() * .5f, .1f );
			float vertical = camera.FovDegrees * MathF.PI / 360;
			float horizontal = MathF.Atan( MathF.Tan( vertical ) * width / Math.Max( 1f, height ) );
			glideFromTarget = camera.Target; glideFromDistance = camera.Distance;
			glideToTarget = (min + max) * .5f;
			float fit = radius * (bodies.Count > 0 ? 1.0f : 1.15f) / MathF.Sin( Math.Min( vertical, horizontal ) );
			glideToDistance = Math.Clamp( Math.Max( radius * (bodies.Count > 0 ? 1.4f : 2.4f), fit ), camera.MinDistance, camera.MaxDistance );
			// a part on the far side of the model: swing round to the side it sticks out of, so it isn't hidden
			glideFromYaw = glideToYaw = camera.YawDegrees; glideFromPitch = glideToPitch = camera.PitchDegrees;
			var side = glideToTarget - (all.Min + all.Max) * .5f;
			side.Z = 0;
			if ( bodies.Count > 0 && side.Length() > whole * .12f )
			{
				float yaw = camera.YawDegrees * MathF.PI / 180f;
				var look = new Vector3( MathF.Cos( yaw ), MathF.Sin( yaw ), 0 );
				if ( Vector3.Dot( Vector3.Normalize( side ), look ) < .2f )
				{
					// out towards the part's side, but kept a quarter turn off it so it still reads as a 3/4 view
					float outward = MathF.Atan2( side.Y, side.X ) * 180f / MathF.PI;
					float turn = ((outward - camera.YawDegrees) % 360 + 540) % 360 - 180;
					glideToYaw = camera.YawDegrees + turn - Math.Sign( turn ) * 30f;
				}
			}
			glideToPitch = Math.Clamp( camera.PitchDegrees, 8f, 35f );
			if ( camera.Flying ) return;
			camera.Far = Math.Max( camera.Far, Math.Max( glideToDistance * 20f, whole * 40f ) );
			glideSeconds = Math.Clamp( seconds, 0f, 5f );
			glideAt = -2;                          // starts on the next frame
		}
		// about a model-width and a bit a second, so a 15 in dagger and a 6 ft body take the same time to cross
		bool FlyMoving() => camera.Flying && flyMove != Vector3.Zero;
		void FlySpeedFromSize()
		{
			float size = mesh.Radius > 0.01f ? mesh.Radius : camera.Distance * 0.4f;
			camera.FlySpeed = Math.Clamp( size * 1.2f * MathF.Pow( 1.25f, flySteps ), 0.05f, 2000f );
		}
		Reframe();
		if ( options.Empty )
		{
			// Reframe on the empty mesh's radius 1 would park the camera 5 units
			// out, showing only the grid's centre cross. The grid floor is 200x200
			// (step 10, half 10), so frame it at the default 200-unit distance instead,
			// same angle as a fresh model view. Orbit/pan still work from there.
			camera.Target = Vector3.Zero;
			camera.Distance = 200f;
			camera.YawDegrees = 35f;
			camera.PitchDegrees = 18f;
			// Reframe() above set Far from the empty mesh's radius 1 (Far 100),
			// which clips everything at Distance 200 — only the grid's near
			// edge survived, a sliver at the bottom of the frame. Push Far
			// out with the new distance, like Frame() does.
			camera.Near = Math.Max( 0.01f, camera.Distance * 0.001f );
			camera.Far = Math.Max( camera.Distance * 20f, 4000f );
		}
		if ( mesh.TriangleCount > 0 ) mesh.Upload( gl );
		renderer.WireframeMode = options.Wireframe ? 1 : 0;

		string shmPath = $"/dev/shm/geppetto-view-{Environment.ProcessId}.rgba";
		MemoryMappedFile map = null;
		MemoryMappedViewAccessor view = null;
		void Remap()
		{
			view?.Dispose(); map?.Dispose();
			using ( var fs = new FileStream( shmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite ) )
				fs.SetLength( (long)width * height * 4 );
			map = MemoryMappedFile.CreateFromFile( shmPath, FileMode.Open, null, (long)width * height * 4, MemoryMappedFileAccess.ReadWrite );
			view = map.CreateViewAccessor();
		}
		Remap();
		var pixels = new byte[width * height * 4];
		var flipped = new byte[width * height * 4];

		Emit( Ready( options, mesh, player, shmPath, loader ) );
		SyncRefs( loader.Session?.Studio?.ReferenceImages, options.WatchRun is null ? options.Model : null );
		if ( refsSignature is null ) EmitRefs();
		FrameRefs();
		ClipKeyEditor keys = null;
		void OpenKeys( string path )
		{
			keys = player?.Clip is { } clip ? new ClipKeyEditor( clip, path, () => player.InvalidateClipCache( preservePlayhead: true ) ) : null;
			EmitKeys();
		}
		var face = new FacePoserService();         // the Face Poser panel's flex sliders over this pane's model
		// Wave-2 verbs: attach the morph document for `face symmetrize` / `face split`.
		// Only an opened document with exactly one editable body gets one: a live-run pane's
		// studio changes every revision (and MorphEditService throws if the vertex/face count
		// moves under it), and a multi-body model has no single mesh to edit. Anything else
		// leaves Morphs null, and those face commands fail clearly instead of pretending.
		void AttachMorphs()
		{
			try
			{
				var bodies = (follow is null ? loader.Session?.Studio : null)?.Bodies
					.Where( b => !b.IsReference && b.Mesh is { VertexCount: > 0 } ).ToList();
				face.Morphs = bodies is { Count: 1 }
					? new MorphEditService( bodies[0].Mesh, new EditHistory() )
					: null;
			}
			catch { face.Morphs = null; }
		}
		AttachMorphs();
		void EmitKeys() => Emit( keys?.Snapshot( mesh.Rig ) ?? new JsonObject { ["ev"] = "keys", ["tracks"] = new JsonArray(), ["undo"] = 0, ["redo"] = 0, ["dirty"] = false, ["saveable"] = false, ["frames"] = 0 } );
		ClipKeyEditor Keys() => keys ?? throw new InvalidOperationException( "No clip to edit keys on." );
		OpenKeys( options.Clip );

		bool dirty = true, acked = true, running = true;
		int frameNo = 0;
		var clock = Stopwatch.StartNew();
		double last = clock.Elapsed.TotalSeconds;
		try
		{
			while ( running )
			{
				// wait for work: block when idle, poll at ~60 Hz while playing
				bool busy = dirty || player is { Playing: true } || spin != 0 || sendoffAt >= 0 || FlyMoving();
				if ( commands.TryTake( out var cmd, busy && acked ? 0 : busy ? 4 : follow is not null ? 25 : 250 ) )
				{
					do
					{
						try { running = Apply( cmd ); }
						catch ( Exception ex ) { Emit( new JsonObject { ["ev"] = "error", ["msg"] = ex.Message } ); }
					} while ( running && commands.TryTake( out cmd ) );
				}
				if ( !running ) break;
				if ( follow is not null )
				{
					if ( follow.Error is { } error && error != previewError )
					{
						previewError = error;
						Emit( new JsonObject { ["ev"] = "ai-preview-error", ["msg"] = error } );
					}
					var activity = follow.State;
					if ( activity is not null && (activity["sequence"]?.GetValue<long>() ?? 0) > eventSequence )
					{
						eventSequence = activity["sequence"]!.GetValue<long>();
						activity["ev"] = "ai-activity";
						Emit( activity );
					}
					if ( follow.Take() is { } update )
					{
						if ( displayedRun is not null && (displayedRun != update.RunId || update.Revision <= displayedRevision && !follow.Pinned) ) update.Mesh.Dispose();
						else
						{
							try
							{
								var upload = Stopwatch.StartNew();
								if ( update.Mesh.TriangleCount > 0 ) update.Mesh.Upload( gl, mesh );   // in place over the last revision's buffers
								if ( gl.GetError() is var uploadError && uploadError != GLEnum.NoError ) throw new InvalidOperationException( $"Preview upload: {uploadError}" );
								var old = mesh; mesh = update.Mesh;
								// Lane E: a clip set in this pane survives the revision on
								// the new mesh's rig; without this, every live step dropped
								// a playing clip back to the rest pose.
								var wasPlaying = player?.Playing ?? activeClipPlaying;
								player = null;
								if ( activeClipPath is not null && mesh.Rig is { Count: > 0 } )
								{
									try
									{
										player = new MarionettePlayer( mesh.Rig, activeClipPath );
										player.Playing = wasPlaying;
									}
									catch ( Exception clipEx )
									{
										player = null;
										Emit( new JsonObject { ["ev"] = "clip", ["path"] = activeClipPath, ["error"] = clipEx.Message } );
									}
								}
								if ( face.Sync( mesh, update.Path ) && face.HasRig ) Emit( face.Snapshot() );
								face.Apply( mesh );               // the next revision keeps the face
								foreach ( var prop in renderer.Props ) { prop.Mesh.ReleaseGpu( gl ); prop.Dispose(); }
								renderer.Props = Array.Empty<PropScene>();
								overlay.SelectedBone = overlay.SelectedBody = -1;
								previewPath = update.Path; publishedAt = update.PublishedAt; previewError = null;
								displayedRun = update.RunId; displayedRevision = update.Revision;
								SyncRefs( update.Refs, update.Path );
								FrameRefs();
								if ( sub.Mode != SubMode.Body ) { SubLoad(); SubEmit(); }
								old.ReleaseGpu( gl ); old.Dispose();
								// Only the first live model gets the default view; a new run (a remodel) keeps the
								// user's camera, re-fitted at their angle when TRACK is on, untouched when it's off.
								if ( holdFrame > 0 && mesh.TriangleCount > 0 ) { holdFrame--; framedLive = everFramedLive = true; }   // dropped in: the camera stays put
								else if ( mesh.TriangleCount > 0 && !everFramedLive ) { fbx = false; Reframe(); framedLive = everFramedLive = true; }
								else if ( mesh.TriangleCount > 0 && (autoFrame || !framedLive) )
								{
									if ( autoFrame )
									{
										float yaw = camera.YawDegrees, pitch = camera.PitchDegrees;
										Reframe();
										camera.YawDegrees = yaw; camera.PitchDegrees = pitch;
									}
									framedLive = true;
								}
								flashNew.Set( mesh.Picker, PicksOf( update.Added ) );
								flashEdit.Set( mesh.Picker, PicksOf( update.Changed ) );
								flashAt = flashNew.Visible || flashEdit.Visible ? clock.Elapsed.TotalSeconds : -1;
								outlineNew.Visible = flashNew.Visible; outlineEdit.Visible = flashEdit.Visible;
								if ( flashAt >= 0 ) { flashNew.Fade( .55f ); flashEdit.Fade( .55f ); outlineNew.Fade( 1f ); outlineEdit.Fade( 1f ); }
								renderer.ResetTemporalHistory(); dirty = true;
								Emit( new JsonObject { ["ev"] = "ai-preview", ["added"] = Names( update.Added ), ["changed"] = Names( update.Changed ), ["removed"] = update.Removed.Length, ["runId"] = update.RunId, ["revision"] = update.Revision,
									["path"] = update.Path, ["tris"] = mesh.TriangleCount, ["load_ms"] = update.LoadMs,
									["upload_ms"] = upload.Elapsed.TotalMilliseconds, ["cache"] = update.CacheNote } );
							}
							catch ( Exception ex )
							{
								update.Mesh.ReleaseGpu( gl ); update.Mesh.Dispose();
								Emit( new JsonObject { ["ev"] = "ai-preview-error", ["msg"] = ex.Message } );
							}
						}
					}
				}


				double now = clock.Elapsed.TotalSeconds;
				float dt = (float)Math.Min( now - last, 0.1 );
				if ( !acked || !(dirty || player is { Playing: true } || spin != 0 || sendoffAt >= 0 || FlyMoving() || glideAt != -1) ) { last = now; continue; }
				if ( spin != 0 ) camera.YawDegrees = (camera.YawDegrees + spin * dt) % 360;
				if ( glideAt == -2 ) glideAt = now;
				if ( glideAt >= 0 )
				{
					float g = glideSeconds <= 0 ? 1 : Math.Clamp( (float)(now - glideAt) / glideSeconds, 0, 1 );
					float e = g * g * (3 - 2 * g);
					camera.Target = Vector3.Lerp( glideFromTarget, glideToTarget, e );
					camera.Distance = glideFromDistance + (glideToDistance - glideFromDistance) * e;
					camera.YawDegrees = glideFromYaw + (glideToYaw - glideFromYaw) * e;
					camera.PitchDegrees = glideFromPitch + (glideToPitch - glideFromPitch) * e;
					camera.Near = Math.Max( 0.01f, camera.Distance * 0.001f );
					if ( g >= 1 ) glideAt = -1;
				}
				if ( focusName.Length > 0 && focusMesh != mesh ) Refocus();
				if ( flashAt >= 0 )
				{
					double age = clock.Elapsed.TotalSeconds - flashAt;
					float k = age < FlashHold ? 1f : (float)Math.Max( 0, 1 - (age - FlashHold) / FlashFade );
					// two soft pulses while it holds, so the eye catches it
					float pulse = age < FlashHold ? .75f + .25f * MathF.Cos( (float)age * MathF.PI * 2f / .7f ) : 1f;
					flashNew.Fade( .55f * k * pulse ); flashEdit.Fade( .55f * k * pulse ); outlineNew.Fade( k ); outlineEdit.Fade( k );
					if ( k <= 0 ) { flashNew.Visible = flashEdit.Visible = outlineNew.Visible = outlineEdit.Visible = false; flashAt = -1; }
					renderer.ResetTemporalHistory(); dirty = true;
				}
				if ( FlyMoving() ) { camera.MoveFly( flyMove.X, flyMove.Y, flyMove.Z, dt, flyFast ); dirty = true; }
				if ( sendoffAt >= 0 )
				{
					float t = (float)(now - sendoffAt);
					if ( sendoffCard )                    // the card frame has gone out: done
					{
						EndSendoff();
						var gone = mesh; mesh = AiLivePreview.EmptyMesh();
						gone.ReleaseGpu( gl ); gone.Dispose();
						follow?.Dispose(); follow = null;
						displayedRevision = eventSequence = 0; displayedRun = null; framedLive = false;
						previewPath = ""; previewError = publishedAt = null;
						Emit( new JsonObject { ["ev"] = "sent-off" } );
					}
					else if ( t >= SendoffPress + SendoffTurn )
					{
						renderer.DisplayTransform = SendoffCard( SendoffPress + SendoffTurn );
						sendoffCard = true;
						gridBeforeCard = overlay.ShowGrid; overlay.ShowGrid = false;
						groundBeforeCard = renderer.GroundShadow; renderer.GroundShadow = false;
					}
					else renderer.DisplayTransform = SendoffCard( t );
					renderer.ResetTemporalHistory();
					dirty = true;
				}
				if ( dt < 1f / 60 && !dirty ) { Thread.Sleep( 2 ); continue; }
				last = now;

				player?.Step( player.Playing ? dt : 0 );
				gl.BindFramebuffer( FramebufferTarget.Framebuffer, target.Handle );
				gl.Viewport( 0, 0, (uint)width, (uint)height );
				if ( mesh.TriangleCount > 0 ) renderer.Draw( mesh, player, camera, width, height, options.Exposure, target.Handle );
				else { gl.ClearColor( .14f, .145f, .16f, 1 ); gl.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit ); }
				var viewProj = camera.ViewMatrix() * camera.ProjectionMatrix( width / (float)height );
				if ( !sendoffCard ) refs.Draw( viewProj );
				if ( string.Join( "\n", refs.Problems ) is var problems && problems != refsProblems ) { refsProblems = problems; EmitRefs(); }
				(Vector3 Min, Vector3 Max)? selBounds = overlay.SelectedBody >= 0
					? mesh.Picker.Bounds( overlay.SelectedBody, player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones ) : null;
				overlay.Draw( viewProj, mesh.Rig,
					player is null ? ReadOnlySpan<Xform>.Empty : player.WorldPose, mesh.BodyPicks, selBounds );
				ReadFrame();
				view.WriteArray( 0, flipped, 0, flipped.Length );
				view.Flush();
				frameNo++;
				dirty = false;
				acked = false;
				Emit( new JsonObject
				{
					["ev"] = "frame", ["n"] = frameNo, ["w"] = width, ["h"] = height,
					["time"] = player?.Time ?? 0, ["playing"] = player?.Playing ?? false, ["loop"] = player?.Loop ?? true,
					["frame"] = ClipFrame( player ),
					["tris"] = mesh.TriangleCount, ["revision"] = displayedRevision, ["runId"] = displayedRun, ["path"] = previewPath,
					["commit_to_frame_ms"] = publishedAt is null ? 0 : (DateTimeOffset.UtcNow - DateTimeOffset.Parse( publishedAt )).TotalMilliseconds,
					["render_target_objects"] = RenderTarget.LiveObjects, ["mesh_textures"] = MeshResourceBudget.LiveGpuTextures,
					["yaw"] = camera.YawDegrees, ["pitch"] = camera.PitchDegrees, ["distance"] = camera.Distance,
					["ortho"] = camera.Orthographic,
					["card"] = sendoffAt >= 0 && sendoffCard,
				} );
			}
		}
		finally
		{
			follow?.Dispose();
			view?.Dispose(); map?.Dispose();
			try { File.Delete( shmPath ); } catch { }
			target.Dispose();
			foreach ( var prop in renderer.Props ) { prop.Mesh.ReleaseGpu( gl ); prop.Dispose(); }
			mesh.ReleaseGpu( gl );
		}
		return 0;

		unsafe void ReadFrame()
		{
			gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, target.Handle );
			gl.PixelStore( PixelStoreParameter.PackAlignment, 1 );
			fixed ( byte* raw = pixels )
				gl.ReadPixels( 0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, raw );
			int row = width * 4;
			for ( int y = 0; y < height; y++ )
				System.Buffer.BlockCopy( pixels, (height - 1 - y) * row, flipped, y * row, row );
		}

		void EndSendoff()
		{
			if ( sendoffAt < 0 ) return;
			sendoffAt = -1;
			if ( sendoffCard ) { overlay.ShowGrid = gridBeforeCard; renderer.GroundShadow = groundBeforeCard; }
			sendoffCard = false;
			camera.Target = sendoffTarget; camera.Distance = sendoffDistance; camera.YawDegrees = sendoffYaw;
			renderer.DisplayTransform = Matrix4x4.Identity;
			renderer.ResetTemporalHistory(); dirty = true;
		}

		// where the model is drawn t seconds into the send-off, as a world transform about the view's centre
		Matrix4x4 SendoffCard( float t )
		{
			static float Smooth( float u ) { u = Math.Clamp( u, 0, 1 ); return u * u * (3 - 2 * u); }
			var f = Vector3.Normalize( camera.Forward );
			var right = Vector3.Normalize( Vector3.Cross( f, Vector3.UnitZ ) );
			var up = Vector3.Cross( right, f );
			// press: depth along the view collapses to 3% (not 0, so overlapping parts keep their order), with a
			// little squash-and-recover on the face so the press reads even though the front view barely changes
			float press = Smooth( t / SendoffPress );
			float k = 0.97f * press;
			float squash = 1 - 0.06f * MathF.Sin( press * MathF.PI );
			// turn: the card swings about screen-up to nearly edge-on, overshoots back past square and settles
			float u = Math.Clamp( (t - SendoffPress) / SendoffTurn, 0, 1 );
			float turn = u < 0.4f ? 74 * Smooth( u / 0.4f ) : u < 0.75f ? 74 - 82 * Smooth( (u - 0.4f) / 0.35f ) : -8 * (1 - Smooth( (u - 0.75f) / 0.25f ));
			var flatten = new Matrix4x4(
				1 - k * f.X * f.X, -k * f.X * f.Y, -k * f.X * f.Z, 0,
				-k * f.Y * f.X, 1 - k * f.Y * f.Y, -k * f.Y * f.Z, 0,
				-k * f.Z * f.X, -k * f.Z * f.Y, 1 - k * f.Z * f.Z, 0,
				0, 0, 0, 1 );
			var c = sendoffTarget;
			return Matrix4x4.CreateTranslation( -c ) * flatten
				* Matrix4x4.CreateFromAxisAngle( up, turn * MathF.PI / 180 )
				* Matrix4x4.CreateScale( squash )
				* Matrix4x4.CreateTranslation( c );
		}

		bool Apply( string line )
		{
			if ( line.Length == 0 ) return true;
			var a = line.Split( ' ', 2 );
			string arg = a.Length > 1 ? a[1] : "";
			float F( int i ) => float.Parse( arg.Split( ' ' )[i], System.Globalization.CultureInfo.InvariantCulture );
			switch ( a[0] )
			{
				case "follow":
					EndSendoff();
					follow?.Dispose(); follow = null;
					displayedRevision = eventSequence = 0; displayedRun = null; framedLive = false; previewError = publishedAt = null;
					if ( arg.Length > 0 ) follow = new AiLivePreview( arg );
					else if ( mesh.TriangleCount > 0 )     // "follow" nothing: the pane is emptied, drop the model too
					{
						var gone = mesh; mesh = AiLivePreview.EmptyMesh();
						gone.ReleaseGpu( gl ); gone.Dispose();
						foreach ( var prop in renderer.Props ) { prop.Mesh.ReleaseGpu( gl ); prop.Dispose(); }
						renderer.Props = Array.Empty<PropScene>();
						player = null; previewPath = "";
						renderer.ResetTemporalHistory(); dirty = true;   // push an empty frame out
					}
					subPath = null;                        // re-resolve on the next shown revision: refs on bodies that carried over stay (s&box prunes by body id)
					refsSignature = null; SyncRefs( null, null );
					Emit( new JsonObject { ["ev"] = "ai-follow", ["path"] = arg } );
					return true;
				case "seek":                              // replay: pin revision N of the followed run, 0 = back to latest
					follow?.Seek( (long)F( 0 ) );
					return true;
				case "quit": return false;
				case "sendoff":
					if ( sendoffAt >= 0 || mesh.TriangleCount == 0 ) { Emit( new JsonObject { ["ev"] = "sent-off" } ); return true; }
					sendoffAt = clock.Elapsed.TotalSeconds;
					sendoffTarget = camera.Target; sendoffDistance = camera.Distance; sendoffYaw = camera.YawDegrees;
					return true;
				case "ack": acked = true; return true;
				case "orbit": camera.Orbit( F( 0 ) * 0.35f, -F( 1 ) * 0.35f ); camera.Orthographic = false; break;
				case "ortho": camera.Orthographic = arg == "1"; renderer.ResetTemporalHistory(); break;
				case "refs": EmitRefs(); return true;
				case "refadd":                            // refadd VIEW PATH: one picture per view, sized to the model
				{
					var p = arg.Split( ' ', 2, StringSplitOptions.RemoveEmptyEntries );
					if ( p.Length < 2 ) throw new ArgumentException( "refadd needs a view (front, side, top) and a picture." );
					var v = p[0].ToLowerInvariant() == "right" ? "side" : p[0].ToLowerInvariant();
					if ( Array.IndexOf( BuildScript.RefImageViews, v ) < 0 ) throw new ArgumentException( $"No view called '{p[0]}'. Views: front, side, top." );
					var file = refs.Resolve( p[1].Trim().Trim( '"' ) );
					if ( !File.Exists( file ) ) throw new ArgumentException( $"No picture at {file}." );
					var image = new ReferenceImage { View = v, Path = file };
					// Start it the size of the model, centred on it, so it lands in view; then nudge.
					var (right, up, _) = ReferenceImagePass.Axes( v );
					if ( mesh.TriangleCount > 0 )
					{
						var size = mesh.BoundsMax - mesh.BoundsMin;
						var centre = (mesh.BoundsMin + mesh.BoundsMax) * 0.5f;
						float aspect = refs.Aspect( image );
						float across = MathF.Abs( Vector3.Dot( size, right ) ), tall = MathF.Abs( Vector3.Dot( size, up ) );
						image.Height = MathF.Round( MathF.Max( tall, across / MathF.Max( aspect, 1e-3f ) ) * 1.1f, 2 );
						image.Offset = new Vec2( MathF.Round( Vector3.Dot( centre, right ), 2 ), MathF.Round( Vector3.Dot( centre, up ), 2 ) );
					}
					int at = refs.Images.FindIndex( i => i.View == v );
					if ( at >= 0 ) refs.Images[at] = image; else refs.Images.Add( image );
					EmitRefs( $"{Path.GetFileName( file )} is up on the {v} view" );
					break;
				}
				case "refset":                            // refset I height=H ox=X oy=Y depth=D opacity=O visible=0|1
				{
					var p = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
					if ( p.Length < 2 ) throw new ArgumentException( "refset needs a picture number and key=value." );
					var image = RefAt( p[0] );
					foreach ( var kv in p.Skip( 1 ) )
					{
						var bits = kv.Split( '=', 2 );
						if ( bits.Length != 2 ) throw new ArgumentException( $"refset: '{kv}' is not key=value." );
						float n() => float.Parse( bits[1], System.Globalization.CultureInfo.InvariantCulture );
						switch ( bits[0] )
						{
							case "height": image.Height = MathF.Max( ReferenceImagePass.MinHeight, n() ); break;
							case "ox": image.Offset = new Vec2( n(), image.Offset.y ); break;
							case "oy": image.Offset = new Vec2( image.Offset.x, n() ); break;
							case "depth": image.Depth = n(); break;
							case "opacity": image.Opacity = Math.Clamp( n(), ReferenceImagePass.MinOpacity, 1f ); break;
							case "visible": image.Visible = bits[1] != "0"; break;
							default: throw new ArgumentException( $"refset: no setting '{bits[0]}' (height, ox, oy, depth, opacity, visible)." );
						}
					}
					EmitRefs();
					break;
				}
				case "refdel":
				{
					var image = RefAt( arg.Trim() );
					refs.Images.Remove( image );
					EmitRefs( $"took down {Path.GetFileName( image.Path )}" );
					break;
				}
				case "refsave":                           // write the pictures into the open .effigy
				{
					if ( follow is not null || !options.Model.EndsWith( ".effigy", StringComparison.OrdinalIgnoreCase ) )
						throw new InvalidOperationException( "Only an opened .effigy saves its pictures; for a build, copy the refimage lines into the script." );
					var lines = File.ReadAllLines( options.Model ).Where( l => !l.StartsWith( "refimage " ) ).ToList();
					while ( lines.Count > 0 && lines[^1].Length == 0 ) lines.RemoveAt( lines.Count - 1 );
					lines.AddRange( refs.Images.Select( StudioDocument.ReferenceImageLine ) );
					var backup = options.Model + ".bak";
					File.Copy( options.Model, backup, overwrite: true );
					File.WriteAllText( options.Model, string.Join( "\n", lines ) + "\n" );
					refsSignature = Signature( refs.Images );
					EmitRefs( $"saved {refs.Images.Count} picture(s) into {Path.GetFileName( options.Model )}" );
					return true;
				}
				case "pan":
					var side = Vector3.Normalize( Vector3.Cross( camera.Forward, Vector3.UnitZ ) );
					camera.Target += (-side * F( 0 ) + Vector3.UnitZ * F( 1 )) * (camera.Distance * 0.0015f);
					break;
				case "zoom": camera.Zoom( F( 0 ) ); break;
				case "dolly":                           // dolly N: wheel-fly, N notches forward (+) / back (-)
					camera.Dolly( F( 0 ) );
					camera.Orthographic = false;          // like orbit: a travelled view is a perspective view
					break;
				case "fly":                                // fly 1|0: RMB down / up
					if ( arg == "1" )
					{
						camera.Orthographic = false;
						camera.BeginFly();
						FlySpeedFromSize();
					}
					else if ( camera.Flying )
					{
						// back to orbiting around what's in front: the model's depth along the view, so nothing jumps
						float depth = Vector3.Dot( mesh.Centre - camera.FlyPosition, camera.Forward );
						if ( depth > 0.5f ) camera.Distance = depth;
						camera.EndFly();
						flyMove = Vector3.Zero;
					}
					break;
				case "look":                               // look DX DY: mouse-look while flying, pixels
					if ( camera.Flying ) camera.Orbit( -F( 0 ) * 0.15f, F( 1 ) * 0.15f );
					break;
				case "flymove":                            // flymove FORWARD RIGHT UP FAST: the held keys, each -1..1
					flyMove = new Vector3( F( 0 ), F( 1 ), F( 2 ) );
					flyFast = arg.Split( ' ' ).Length > 3 && arg.Split( ' ' )[3] == "1";
					return true;
				case "flyspeed":                           // flyspeed N: wheel notches, each one x1.25
					flySteps = (int)F( 0 );
					FlySpeedFromSize();
					return true;
				case "frame": Reframe(); break;
				case "focus":                              // focus NAME: the part a step touched glows teal (no name: off)
					focusName = arg.Trim().Trim( '"' );
					Refocus();
					Emit( new JsonObject { ["ev"] = "focus", ["name"] = focusName, ["bodies"] = focus.Bodies.Count } );
					break;
				case "framebody":                          // framebody [NAME] [SECS]: glide onto that part, or the whole model
				{
					// the name may have spaces and commas ("Suit / boot_L,Suit / boot_R"); a trailing number is SECS
					var bits = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
					float secs = .8f;
					var inv = System.Globalization.CultureInfo.InvariantCulture;
					int nameWords = bits.Length;
					if ( bits.Length > 0 && float.TryParse( bits[^1], System.Globalization.NumberStyles.Float, inv, out var s ) ) { secs = s; nameWords--; }
					string name = string.Join( ' ', bits, 0, nameWords ).Trim( '"' );
					if ( mesh.TriangleCount == 0 ) return true;
					var bodies = name.Length > 0 ? FocusLayer.Match( mesh.BodyPicks, name ) : new List<int>();
					if ( name.Length > 0 && bodies.Count == 0 ) return true;   // no such part: the camera stays
					Glide( bodies, secs );
					break;
				}
				// fixed angles: front is +x, left +y, so yaw 0 looks at the front
				case "view": camera.YawDegrees = F( 0 ); camera.PitchDegrees = Math.Clamp( F( 1 ), camera.MinPitch, camera.MaxPitch ); break;
				case "spin": spin = Math.Clamp( F( 0 ), -360, 360 ); break;
				case "autoframe": autoFrame = arg == "1"; break;
				case "holdframe": holdFrame = int.TryParse( arg, out var hf ) ? hf : 0; break;   // holdframe N: the next N revisions keep the camera
				case "size":
					int w = Math.Clamp( (int)F( 0 ), 16, 4096 ), h = Math.Clamp( (int)F( 1 ), 16, 4096 );
					if ( w == width && h == height ) return true;
					width = w; height = h;
					target.Dispose();
					target = new CaptureTarget( gl, width, height );
					pixels = new byte[width * height * 4];
					flipped = new byte[width * height * 4];
					Remap();
					acked = true;
					break;
				case "play": if ( player is not null ) SetPlaying( player, arg == "1" ); break;
				case "time": if ( player is not null ) { player.Playing = false; player.Time = Math.Clamp( F( 0 ), 0, ClipSeconds( player ) ); } break;
				case "loop": if ( player is not null ) { player.Loop = arg == "1"; if ( !player.Playing ) player.Time = Math.Clamp( player.Time, 0, ClipSeconds( player ) ); } break;
				case "bones": overlay.ShowBones = arg == "1"; break;
				case "grid": overlay.ShowGrid = arg == "1"; break;
				case "wire": renderer.WireframeMode = Math.Clamp( (int)F( 0 ), 0, 2 ); break;
				case "select":
					int bone = (int)F( 0 );
					overlay.SelectedBone = mesh.Rig is not null && bone >= 0 && bone < mesh.Rig.Count ? bone : -1;
					overlay.SelectedBody = -1;
					if ( overlay.SelectedBone >= 0 ) overlay.ShowBones = true;
					Emit( new JsonObject { ["ev"] = "selected", ["bone"] = overlay.SelectedBone, ["body"] = -1 } );
					break;
				case "selmode":                            // body | face | edge | vertex
				{
					var mode = arg.Trim().ToLowerInvariant() switch
					{
						"body" or "bodies" => SubMode.Body, "face" or "faces" => SubMode.Face,
						"edge" or "edges" => SubMode.Edge, "vertex" or "vertices" or "vert" => SubMode.Vertex,
						_ => throw new ArgumentException( "selmode: body, face, edge or vertex" ),
					};
					sub.SetMode( mode );
					SubLoad();
					SubEmit();
					break;
				}
				case "selclear":
					sub.Clear();
					SubEmit();
					break;
				case "selbox":                             // selbox X0 Y0 X1 Y1 [add|remove] [xray]: drag on empty space
				case "sellasso":                           // sellasso [add|remove] [xray] X Y X Y …: the free-hand drag
				{
					SubLoad();
					var words = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
					var flags = words.Where( w => !float.TryParse( w, NumberStyles.Float, CultureInfo.InvariantCulture, out _ ) ).ToHashSet();
					var nums = words.Where( w => float.TryParse( w, NumberStyles.Float, CultureInfo.InvariantCulture, out _ ) )
						.Select( w => float.Parse( w, CultureInfo.InvariantCulture ) ).ToList();
					bool add = flags.Contains( "add" ), remove = flags.Contains( "remove" ), xray = flags.Contains( "xray" );
					if ( a[0] == "selbox" )
					{
						if ( nums.Count < 4 ) throw new ArgumentException( "selbox X0 Y0 X1 Y1 [add|remove] [xray]" );
						var min = new Vector2( Math.Min( nums[0], nums[2] ), Math.Min( nums[1], nums[3] ) );
						var max = new Vector2( Math.Max( nums[0], nums[2] ), Math.Max( nums[1], nums[3] ) );
						// barely moved: that was a click on nothing, which clears unless a modifier said otherwise
						if ( (max - min).Length() < 3 ) sub.Click( null, add, remove );
						else sub.SelectRegion( camera.Eye, ToScreen, q => q.X >= min.X && q.X <= max.X && q.Y >= min.Y && q.Y <= max.Y, add, remove, xray );
					}
					else
					{
						if ( nums.Count < 6 ) throw new ArgumentException( "sellasso [add|remove] [xray] X Y X Y X Y …" );
						var lasso = new List<Vector2>();
						for ( int i = 0; i + 1 < nums.Count; i += 2 ) lasso.Add( new Vector2( nums[i], nums[i + 1] ) );
						sub.SelectRegion( camera.Eye, ToScreen, q => SubSelection.InsideLasso( lasso, q ), add, remove, xray );
					}
					SubEmit();
					break;
				}
				case "selgrow": SubLoad(); sub.Apply( SubSelection.Topology.Grow ); SubEmit(); break;
				case "selshrink": SubLoad(); sub.Apply( SubSelection.Topology.Shrink ); SubEmit(); break;
				case "sellinked": SubLoad(); sub.Apply( SubSelection.Topology.Linked ); SubEmit(); break;
				case "seltool":                            // seltool paint|sculpt|meshedit|materialbrush|weight|none: hide the highlight, keep the picks
					sub.SetTool( arg );
					SubEmit();
					break;
				case "selrig":                             // selrig 1|0: the rig workspace, picks take whole bodies
					sub.SetRig( arg.Trim() == "1" );
					SubLoad();
					SubEmit();
					break;
				case "drop":                               // drop X Y [MATERIAL]: re-pick the face at the drop pixel
				{
					SubLoad();
					if ( subPath != previewPath ) { subPath = previewPath; var st = follow is null ? loader.Session?.Studio : string.IsNullOrEmpty( previewPath ) ? null : StudioCache.Load( previewPath, out _ ); sub.Reload( st?.Bodies ?? (IEnumerable<Body>)Array.Empty<Body>() ); }
					var (dox, dod) = camera.ScreenRay( F( 0 ), F( 1 ), width, height );
					var words = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
					var ev = new JsonObject { ["ev"] = "drop", ["x"] = F( 0 ), ["y"] = F( 1 ), ["material"] = words.Length > 2 ? string.Join( " ", words.Skip( 2 ) ) : null };
					if ( sub.PickDrop( dox, dod ) is { } dh )
					{
						ev["ok"] = true; ev["body"] = dh.Body.Name ?? dh.Body.Id; ev["face"] = dh.Face; ev["faces"] = sub.FaceTokenOf( dh );
					}
					else { ev["ok"] = false; ev["refusal"] = "The drop missed the model — let go over a face to put the material on it."; }
					ev["path"] = previewPath; ev["revision"] = displayedRevision; ev["runId"] = displayedRun;
					Emit( ev );
					return true;
				}
				case "place":                              // place X Y: where a model dropped at this pixel stands — the face under it, else the ground
				{
					SubLoad();
					if ( subPath != previewPath ) { subPath = previewPath; var st = follow is null ? loader.Session?.Studio : string.IsNullOrEmpty( previewPath ) ? null : StudioCache.Load( previewPath, out _ ); sub.Reload( st?.Bodies ?? (IEnumerable<Body>)Array.Empty<Body>() ); }
					var (po, pd) = camera.ScreenRay( F( 0 ), F( 1 ), width, height );
					var ev = new JsonObject { ["ev"] = "place" };
					if ( sub.PickDrop( po, pd ) is { } ph )
					{
						ev["ok"] = true; ev["on"] = ph.Body.Name ?? ph.Body.Id;
						ev["at"] = new JsonArray( ph.Point.x, ph.Point.y, ph.Point.z );
					}
					else if ( MathF.Abs( pd.Z ) > 1e-4f && -po.Z / pd.Z > 0 )
					{
						var g = po + pd * (-po.Z / pd.Z);
						// let go near the skyline: keep it within reach of what's being looked at, not miles out
						var c = new System.Numerics.Vector3( camera.Target.X, camera.Target.Y, 0f );
						float reach = MathF.Max( camera.Distance, 1f ), far = (g - c).Length();
						if ( far > reach ) g = c + (g - c) * (reach / far);
						ev["ok"] = true; ev["on"] = "ground"; ev["at"] = new JsonArray( g.X, g.Y, 0f );
					}
					else { ev["ok"] = false; ev["refusal"] = "That's sky: let go over the model or the floor."; }
					if ( ev["at"] is JsonArray pa )
					{
						// a readable size there: about a quarter of the view's height at that depth
						var hit = new System.Numerics.Vector3( (float)pa[0]!, (float)pa[1]!, (float)pa[2]! );
						float depth = camera.Orthographic ? camera.Distance : (hit - camera.Eye).Length();
						ev["size"] = 0.25f * 2f * depth * MathF.Tan( camera.FovDegrees * MathF.PI / 360f );
					}
					Emit( ev );
					return true;
				}
				case "hover":                              // hover X Y: light the element under the cursor
				{
					if ( sub.Mode == SubMode.Body && !sub.Rig ) return true;
					SubLoad();
					var (ho, hd) = camera.ScreenRay( F( 0 ), F( 1 ), width, height );
					int before = sub.Version;
					sub.SetHover( sub.Pick( ho, hd, EdgeRadius ) );
					if ( sub.Version == before ) return true;
					break;
				}
				case "pick":
				{
					var (ro, rd) = camera.ScreenRay( F( 0 ), F( 1 ), width, height );
					// rig workspace: a bone under the cursor is a bone click, even with the mesh in front (s&box)
					bool rigBone = sub.Rig && player is not null && overlay.ShowBones && SkeletonOverlay.PickBone( ro, rd, mesh.Rig, player.WorldPose ) >= 0;
					if ( (sub.Mode != SubMode.Body || sub.Rig) && !rigBone )
					{
						// pick X Y [add|remove|pair|loop|ring]: shift adds/toggles, ctrl removes, alt takes the
						// opposite face too — or, on an edge, its loop; ctrl+alt on an edge its ring. Rig: whole bodies.
						SubLoad();
						var how = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Skip( 2 ).ToHashSet();
						bool ring = how.Contains( "ring" ), loop = how.Contains( "loop" ) || ring;
						var hit = sub.Pick( ro, rd, EdgeRadius );
						if ( loop && hit is { Kind: SubMode.Face } f )   // an explicit loop/ring wants the face's nearest rim edge
							hit = sub.Pick( ro, rd, SubMode.Edge ) ?? hit;
						sub.Click( hit, how.Contains( "add" ), how.Contains( "remove" ) || ring, how.Contains( "pair" ) || how.Contains( "alt" ) || loop );
						SubEmit();
						break;
					}
					int boneHit = player is not null && overlay.ShowBones ? SkeletonOverlay.PickBone( ro, rd, mesh.Rig, player.WorldPose ) : -1;
					int bodyHit = -1;
					if ( boneHit < 0 )
					{
						bodyHit = mesh.Picker.Pick( ro, rd, player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones );
						if ( bodyHit >= 0 && !string.IsNullOrEmpty( mesh.BodyPicks[bodyHit].BoneName ) && mesh.Rig is not null )
							boneHit = mesh.Rig.IndexOf( mesh.BodyPicks[bodyHit].BoneName );
					}
					overlay.SelectedBone = boneHit;
					overlay.SelectedBody = bodyHit;
					Emit( new JsonObject { ["ev"] = "selected", ["bone"] = boneHit, ["body"] = bodyHit } );
					break;
				}
				case "clip":
				{
					// Lane E: an .effigy pane (and a live-run pane) may have a rig but no
					// player — the loader only builds one for an FBX-era clip, and live
					// panes reset player per revision. Build one on the pane's own rig
					// instead of refusing, so a .riganim previews where the model is.
					var off = arg.Trim().ToLowerInvariant() is "" or "off" or "none" or "rest";
					if ( off )
					{
						if ( mesh.Rig is not { Count: > 0 } ) throw new InvalidOperationException( "This model has no skeleton to rest on." );
						activeClipPath = null;
						player = new MarionettePlayer( mesh.Rig );
						player.Playing = false;
						player.Time = 0;
						player.Step( 0 );
						Reframe();
						Emit( new JsonObject { ["ev"] = "clip", ["path"] = "", ["rest"] = true, ["frames"] = 0, ["tracks"] = 0 } );
						OpenKeys( null );
						break;
					}
					if ( mesh.Rig is not { Count: > 0 } && player is null )
						throw new InvalidOperationException( "This model has no skeleton to play a clip on." );
					player ??= new MarionettePlayer( mesh.Rig );
					var clipFile = arg.Trim().Trim( '"' );
					if ( !File.Exists( clipFile ) && !string.IsNullOrEmpty( options.Model ) )
					{
						var besideModel = Path.Combine( Path.GetDirectoryName( Path.GetFullPath( options.Model ) ) ?? ".", Path.GetFileName( clipFile ) );
						if ( File.Exists( besideModel ) ) clipFile = besideModel;
					}
					player.LoadClip( clipFile );
					activeClipPath = clipFile;
					activeClipPlaying = true;
					Reframe();
					player.Playing = true;
					Emit( ClipInfo( player, clipFile ) );
					OpenKeys( clipFile );
					break;
				}
				case "rest":
				{
					// Lane E: the bind pose, by name — the toggle the other way from `clip`.
					if ( mesh.Rig is not { Count: > 0 } ) throw new InvalidOperationException( "This model has no skeleton to rest on." );
					activeClipPath = null;
					player = new MarionettePlayer( mesh.Rig );
					player.Playing = false;
					player.Time = 0;
					player.Step( 0 );
					Reframe();
					Emit( new JsonObject { ["ev"] = "clip", ["path"] = "", ["rest"] = true, ["frames"] = 0, ["tracks"] = 0 } );
					OpenKeys( null );
					break;
				}
				// Wave 3: gait — a PlanGait clip, generated from this pane's own rig and
				// played in the pane (AdvancedIkService had a planner and no caller).
				// gait TYPE feet=F1,F2 [stride=] [height=] [frames=] [root=] [save=PATH]
				case "gait":
				{
					if ( mesh.Rig is not { Count: > 0 } ) throw new InvalidOperationException( "This model has no skeleton to walk on." );
					var gt = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault() ?? throw new ArgumentException( "gait needs a type: gait walk feet=foot_L,foot_R" );
					var go = AnimCallers.Opts( arg );
					var feet = (go.TryGetValue( "feet", out var fv ) ? fv : "").Split( ',', StringSplitOptions.RemoveEmptyEntries );
					if ( feet.Length == 0 ) throw new ArgumentException( "gait needs feet=: gait walk feet=foot_L,foot_R" );
					var gait = AnimCallers.BuildGaitClip( mesh.Rig, gt, feet, AnimCallers.OptF( go, "stride", 20f ), AnimCallers.OptF( go, "height", 4f ), AnimCallers.OptI( go, "frames", 60 ), go.TryGetValue( "root", out var rv ) ? rv : null );
					player ??= new MarionettePlayer( mesh.Rig );
					player.LoadClip( gait );
					activeClipPath = null;
					player.Playing = true;
					player.Time = 0;
					if ( go.TryGetValue( "save", out var gsave ) ) { File.WriteAllText( gsave, RigAnimWriter.Write( gait ) ); }
					Emit( new JsonObject { ["ev"] = "gait", ["type"] = gt, ["feet"] = feet.Length, ["frames"] = gait.FrameCount, ["tracks"] = gait.Tracks.Count, ["saved"] = go.TryGetValue( "save", out var gs ) ? gs : "" } );
					OpenKeys( null );
					break;
				}
				// Wave 3: blend — the active clip blended with another clip file, baked
				// per frame through ClipBlendService and played in the pane.
				// blend FILE [weight=0..1] [mode=override|additive] [save=PATH]
				case "blend":
				{
					if ( player?.Clip is null ) throw new InvalidOperationException( "No clip is playing to blend from. Load one with `clip PATH` first." );
					if ( mesh.Rig is not { Count: > 0 } ) throw new InvalidOperationException( "This model has no skeleton to blend on." );
					var bp = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault() ?? throw new ArgumentException( "blend needs a clip file: blend other.riganim weight=0.5" );
					var bo = AnimCallers.Opts( arg );
					var bfile = bp;
					if ( !File.Exists( bfile ) && !string.IsNullOrEmpty( options.Model ) )
					{
						var beside = Path.Combine( Path.GetDirectoryName( Path.GetFullPath( options.Model ) ) ?? ".", Path.GetFileName( bfile ) );
						if ( File.Exists( beside ) ) bfile = beside;
					}
					var layer = RigAnimFile.ReadFile( bfile );
					var mode = (bo.TryGetValue( "mode", out var mv ) ? mv : "override").ToLowerInvariant() == "additive" ? BlendMode.Additive : BlendMode.Override;
					var blended = AnimCallers.BuildBlendClip( player.Clip, layer, AnimCallers.OptF( bo, "weight", 0.5f ), mode, mesh.Rig );
					player.LoadClip( blended );
					activeClipPath = null;
					player.Playing = true;
					player.Time = 0;
					if ( bo.TryGetValue( "save", out var bsave ) ) { File.WriteAllText( bsave, RigAnimWriter.Write( blended ) ); }
					Emit( new JsonObject { ["ev"] = "blend", ["file"] = bfile, ["weight"] = AnimCallers.OptF( bo, "weight", 0.5f ), ["mode"] = mode.ToString(), ["frames"] = blended.FrameCount, ["tracks"] = blended.Tracks.Count, ["saved"] = bo.TryGetValue( "save", out var bs ) ? bs : "" } );
					OpenKeys( null );
					break;
				}
				// the dope sheet: keys are "track:frame" refs, track being the index in the keys event
				case "keys": EmitKeys(); break;
				// face [set NAME VALUE [BALANCE] | preset NAME | zero | random | scale S | save NAME | export PATH]
				case "face": Emit( face.Command( arg, mesh, previewPath ) ); break;
				case "kmove": { var p = arg.Split( ' ' ); Keys().Move( ClipKeyEditor.ParseSelection( p.Skip( 1 ) ), int.Parse( p[0] ) ); EmitKeys(); break; }
				case "kdel": Keys().Delete( ClipKeyEditor.ParseSelection( arg.Split( ' ' ) ) ); EmitKeys(); break;
				case "kease": { var p = arg.Split( ' ' ); Keys().SetEase( ClipKeyEditor.ParseSelection( p.Skip( 1 ) ), (RigEase)Math.Clamp( int.Parse( p[0] ), 0, 6 ) ); EmitKeys(); break; }
				case "kins": { var p = arg.Split( ' ' ); Keys().Insert( int.Parse( p[0] ), p.Skip( 1 ).Where( x => x.Length > 0 ).Select( int.Parse ) ); EmitKeys(); break; }
				// kset TRACK FRAME px py pz qx qy qz qw: key a track with that local pose, and park the
				// playhead on the frame so the viewport shows the pose it made
				case "kset":
				{
					var p = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
					float G( int i ) => float.Parse( p[i], CultureInfo.InvariantCulture );
					int frame = int.Parse( p[1] );
					Keys().Set( int.Parse( p[0] ), frame, new RigPose( new Vec3( G( 2 ), G( 3 ), G( 4 ) ), new Quat( G( 5 ), G( 6 ), G( 7 ), G( 8 ) ), Vec3.One ) );
					if ( player?.Clip is { FrameRate: > 0 } c ) { player.Playing = false; player.Time = frame / (float)c.FrameRate; }
					EmitKeys();
					break;
				}
				case "undo": Keys().Undo(); EmitKeys(); break;
				case "redo": Keys().Redo(); EmitKeys(); break;
				case "texpaint":
				{
					// Commit one texel stroke to the Paint layer on a body, then show it.
					// FINGERPRINT, NOT DIRTY-RECT: the stroke is appended to the layer's log
					// (AddTexelStroke bumps PaintFeature.Revision), the studio rebuilds, and
					// the pane swaps to a snapshot built fresh from that studio -- so the new
					// snapshot's fingerprint already includes the stroke and TryPromoteSnapshot
					// accepts it. A dirty-rect GL push would show the stroke until the next
					// rebuild replaced the canvas; this survives it. The player is left alone:
					// the rig is studio-owned and a paint rebuild does not move it.
					if ( follow is not null || loader.Session?.Studio is null )
						throw new InvalidOperationException( "texpaint needs an opened .effigy pane, not a live-run pane." );

					var parts = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
					if ( parts.Length < 2 )
						throw new ArgumentException( "texpaint needs a body and a path: texpaint Body path=10,20;30,20" );

					var studio = loader.Session.Studio;
					var body = studio.Bodies.FirstOrDefault( b => b.Name == parts[0] || b.Id == parts[0] );
					if ( body?.Mesh is null )
						throw new ArgumentException( $"No body called '{parts[0]}'." );

					var opts = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
					foreach ( var kv in parts.Skip( 1 ) )
					{
						var eq = kv.IndexOf( '=' );
						if ( eq > 0 ) opts[kv[..eq]] = kv[( eq + 1 )..];
					}

					if ( !opts.TryGetValue( "path", out var pathText ) || string.IsNullOrWhiteSpace( pathText ) )
						throw new ArgumentException( "texpaint needs path=: texpaint Body path=10,20;30,20 -- texel coordinates in the layer's canvas, row 0 at the top." );

					var feature = studio.Features.OfType<Effigy.PaintFeature>()
						.LastOrDefault( f => f.Bodies.BodyIds.Contains( body.Id ) );
					if ( feature is null )
						throw new InvalidOperationException( $"{body.Name ?? body.Id} has no Paint layer naming it. Add one first (add Paint bodies={body.Name ?? body.Id}), then texpaint commits strokes to it." );

					var channel = opts.GetValueOrDefault( "channel", "color" ).ToLowerInvariant() switch
					{
						"color" or "colour" or "base" => Effigy.PaintChannel.Color,
						"rough" or "roughness" => Effigy.PaintChannel.Roughness,
						"metal" or "metalness" or "metallic" => Effigy.PaintChannel.Metalness,
						"height" => Effigy.PaintChannel.Height,
						"mask" => Effigy.PaintChannel.Mask,
						_ => throw new ArgumentException( $"channel={opts.GetValueOrDefault( "channel", "" )}: use color, rough, metal, height or mask." ),
					};

					var color = opts.GetValueOrDefault( "color", "1,0,0" ).Split( ',' );
					if ( color.Length < 3 || !float.TryParse( color[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cr )
						|| !float.TryParse( color[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cg )
						|| !float.TryParse( color[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cb ) )
						throw new ArgumentException( $"color={opts.GetValueOrDefault( "color", "" )}: three numbers 0..1, e.g. color=1,0,0." );

					if ( !float.TryParse( opts.GetValueOrDefault( "radius", "12" ), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radius ) || radius <= 0 )
						throw new ArgumentException( $"radius={opts.GetValueOrDefault( "radius", "" )}: a positive texel radius." );

					if ( !float.TryParse( opts.GetValueOrDefault( "strength", "1" ), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var strength ) || strength <= 0 || strength > 1 )
						throw new ArgumentException( $"strength={opts.GetValueOrDefault( "strength", "" )}: 0..1." );

					var falloff = opts.GetValueOrDefault( "falloff", "smooth" ).ToLowerInvariant() switch
					{
						"smooth" => Effigy.BrushFalloff.Smooth,
						"linear" => Effigy.BrushFalloff.Linear,
						"sharp" => Effigy.BrushFalloff.Sharp,
						"constant" => Effigy.BrushFalloff.Constant,
						_ => throw new ArgumentException( $"falloff={opts.GetValueOrDefault( "falloff", "" )}: smooth, linear, sharp or constant." ),
					};

					if ( !float.TryParse( opts.GetValueOrDefault( "spacing", "0.5" ), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var spacing ) || spacing <= 0 )
						throw new ArgumentException( $"spacing={opts.GetValueOrDefault( "spacing", "" )}: a positive fraction of the radius." );

					// Wave-5 papercut: built directly from parsed args ON PURPOSE, not via
					// TexturePaintService.Session.ToTexelStroke -- no Session exists on this
					// command path (no canvas, no dab stream), and the durable commit is
					// AddTexelStroke below. See the TexturePaintService header for the full
					// routing decision.
					var stroke = new Effigy.TexelStroke
					{
						Channel = channel,
						R = cr, G = cg, B = cb, A = 1f,
						Radius = radius, Strength = strength, Falloff = falloff, Spacing = spacing,
						Erase = opts.GetValueOrDefault( "erase", "0" ) is "1" or "true" or "on" or "yes",
					};

					foreach ( var point in pathText.Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
					{
						var xy = point.Split( ',' );
						if ( xy.Length < 2 || !float.TryParse( xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px )
							|| !float.TryParse( xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var py ) )
							throw new ArgumentException( $"path={pathText}: points are x,y pairs separated by ';', e.g. path=10,20;30,20." );
						stroke.Path.Add( new Effigy.TexelStrokePoint( px, py ) );
					}

					if ( stroke.Path.Count == 0 )
						throw new ArgumentException( "texpaint needs at least one path point: path=10,20." );

					feature.AddTexelStroke( stroke );
					var report = studio.Rebuild();
					if ( feature.Error is not null )
						throw new InvalidOperationException( $"texpaint rebuild failed: {feature.Error}" );
					if ( report.HasErrors )
						throw new InvalidOperationException( $"texpaint rebuild failed: {string.Join( "; ", report.Errors.Select( e => e.FeatureId + ": " + e.Message ) )}" );

					var snapshot = loader.Session.BuildSnapshot();
					if ( !loader.TryPromoteSnapshot( snapshot ) )
					{
						snapshot.Mesh.Dispose();
						throw new InvalidOperationException( "texpaint: fresh snapshot was stale on promotion (fingerprint moved under it); the stroke is in the document, re-open the pane to see it." );
					}

					// Swap to the fresh snapshot's mesh the way a live revision does: upload
					// in place over the old buffers, then release the old mesh.
					var fresh = snapshot.Mesh;
					if ( fresh.TriangleCount > 0 ) fresh.Upload( gl, mesh );
					var oldMesh = mesh; mesh = fresh;
					oldMesh.ReleaseGpu( gl ); oldMesh.Dispose();
					Refocus();
					renderer.ResetTemporalHistory(); dirty = true;
					Emit( new JsonObject { ["ev"] = "texpaint", ["body"] = body.Name ?? body.Id, ["points"] = stroke.Path.Count,
						["channel"] = channel.ToString(), ["revision"] = feature.Revision,
						["texelStrokes"] = feature.TexelStrokes?.Count ?? 0 } );
					break;
				}

				case "save":
				{
					var backup = Keys().Save( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify ), "geppetto", "clip-backups" ) );
					Emit( new JsonObject { ["ev"] = "saved", ["path"] = keys.Path, ["backup"] = backup } );
					EmitKeys();
					break;
				}
				default: throw new ArgumentException( "unknown command: " + a[0] );
			}
			dirty = true;
			return true;
		}
	}

	/// <summary>
	/// The frame the player is showing, by the player's own rule: a looping clip that is playing
	/// wraps its ever-running time, a paused (or one-shot) clip clamps it, so the last frame can be held.
	/// </summary>
	static int ClipFrame( MarionettePlayer player )
	{
		var clip = player?.Clip;
		if ( clip is not { FrameRate: > 0 } || clip.LastFrame <= 0 ) return 0;
		float raw = player.Time * clip.FrameRate;
		float frame = player.Loop && player.Playing ? ((raw % clip.LastFrame) + clip.LastFrame) % clip.LastFrame : Math.Clamp( raw, 0, clip.LastFrame );
		return (int)MathF.Round( frame );
	}

	static float ClipSeconds( MarionettePlayer player )
		=> player.Clip is { FrameRate: > 0, LastFrame: > 0 } clip ? clip.LastFrame / (float)clip.FrameRate : float.MaxValue;

	/// <summary>
	/// Pausing a looping clip folds its running time back into the clip, or the paused pose (which
	/// clamps) would jump to the last frame; playing a one-shot clip that has ended starts it over.
	/// </summary>
	static void SetPlaying( MarionettePlayer player, bool playing )
	{
		float length = ClipSeconds( player );
		if ( playing == player.Playing || length == float.MaxValue ) { player.Playing = playing; return; }
		if ( !playing && player.Loop && player.Time > length ) player.Time %= length;
		if ( playing && !player.Loop && player.Time >= length ) player.Time = 0;
		player.Playing = playing;
	}

	static JsonObject ClipInfo( MarionettePlayer player, string path )
	{
		var clip = player?.Clip;
		return new JsonObject
		{
			["ev"] = "clip",
			["path"] = path ?? "",
			["name"] = path is null ? (player is null ? "" : "procedural") : Path.GetFileNameWithoutExtension( path ),
			["rate"] = clip?.FrameRate ?? 30,
			["frames"] = clip?.LastFrame ?? 0,
			["tracks"] = clip?.Tracks.Count ?? 0,
			["space"] = player?.ClipSpaceNote ?? "",
			["space_scale"] = player?.ClipPositionScale ?? 1f,
		};
	}

	static JsonObject Ready( DesktopOptions options, ViewportMesh mesh, MarionettePlayer player, string shm, DocumentLoader loader )
	{
		var bones = new JsonArray();
		if ( mesh.Rig is not null )
			foreach ( var b in mesh.Rig.Bones ) bones.Add( new JsonObject { ["name"] = b.Name, ["parent"] = b.Parent } );
		var bodies = new JsonArray();
		foreach ( var b in mesh.BodyPicks ?? Array.Empty<CpuMesh.BodyPickEntry>() )
			bodies.Add( new JsonObject { ["name"] = b.Name, ["bone"] = b.BoneName ?? "" } );
		var materials = new JsonArray();
		foreach ( var b in mesh.Batches ) materials.Add( b.MaterialName );
		return new JsonObject
		{
			["ev"] = "ready",
			["shm"] = shm,
			["model"] = options.WatchRun ?? options.Model,
			["tris"] = mesh.TriangleCount,
			["bodies"] = bodies,
			["bones"] = bones,
			["materials"] = materials,
			["load_s"] = Math.Round( loader.TotalSeconds, 2 ),
			["cache"] = loader.CacheNote ?? "",
			["clip"] = ClipInfo( player, options.Clip ),
			["clip_space"] = player?.ClipSpaceNote ?? "",
			["clip_space_scale"] = player?.ClipPositionScale ?? 1f,
		};
	}
}
