using System;
using System.Diagnostics;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Input.Sdl;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;

namespace Marionette.Desktop;

/// <summary>
/// Native Linux desktop shell for Geppetto / Marionette.
/// Compiles the engine-free Effigy kernel from kernel/Effigy (a standalone copy; no s&box reference).
///
///   marionette-desktop [--frames N] [--size WxH] [--model path.effigy] [--screenshot out.png] [--stats]
///
/// --stats writes one `frame_ms <milliseconds>` line per frame to stdout: frame-to-frame wall time
/// between Render callbacks, which includes the vsync wait and the previous frame's work. It is the
/// number geppetto-verify/frame-stats.py reads, and it is deliberately the whole interval rather
/// than the drawing time alone — the frame the user waits for is the whole interval.
///
/// Step 1: load a document once at startup (off the render thread), upload geometry
/// grouped by material into one VBO, orbit with the mouse.
/// </summary>
public static class Program
{

	public static int Main( string[] raw )
	{
		try
		{
			if ( raw.Length == 2 && raw[0] == "--ai-worker" ) return AiToolWorker.Run( raw[1] );
			if ( raw.Length > 0 && raw[0] == "--ai" ) return AiCli.Run( raw[1..] );
			if ( raw.Length > 0 && raw[0] == "--script" ) return ScriptLive.Run( raw[1..] );
			if ( raw.Length > 0 && raw[0] == "--bench" ) return Bench.Run( raw[1..] );
			return Run( DesktopOptions.Parse( raw ) );
		}
		catch ( Exception ex )
		{
			Console.Error.WriteLine( "error: " + ex.Message );
			return 1;
		}
	}

	static int Run( DesktopOptions arguments )
	{
		if ( arguments.Help ) { Console.WriteLine( DesktopOptions.Usage ); return 0; }
		int frames = arguments.Frames, width = arguments.Width, height = arguments.Height;
		string modelPath = arguments.Model, clipPath = arguments.Clip, screenshotPath = arguments.Screenshot;
		bool stats = arguments.Stats, animate = arguments.Animate, isoView = arguments.Iso;
		if ( arguments.WatchRun is not null ) return ServeMode.Run( arguments );
		if ( !System.IO.File.Exists( modelPath ) ) throw new System.IO.FileNotFoundException( "Model file not found.", modelPath );
		if ( clipPath is not null && !System.IO.File.Exists( clipPath ) ) throw new System.IO.FileNotFoundException( "Clip file not found.", clipPath );
		if ( !modelPath.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) && !modelPath.EndsWith( ".effigy", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( "--model must name an .effigy or .fbx source file." );
		if ( arguments.ListBones )
		{
			var rig = modelPath.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase )
				? Effigy.FbxImport.Read( System.IO.File.ReadAllBytes( modelPath ) ).Skeleton
				: Effigy.StudioCache.Load( modelPath ).Rig;
			if ( rig is not null )
				for ( int b = 0; b < rig.Count; b++ ) Console.WriteLine( $"bone {b}: {rig.Bones[b].Name}" );
			return 0;
		}

		if ( arguments.Serve ) return ServeMode.Run( arguments );
		if ( arguments.Headless ) return HeadlessRender.Run( arguments );
		SdlWindowing.Use();
		SdlInput.Use();

		var options = WindowOptions.Default with
		{
			Size = new Vector2D<int>( width, height ),
			Title = "Geppetto — Camhead",
			VSync = !arguments.NoVsync,
			Samples = 4,
			API = new GraphicsAPI(
				ContextAPI.OpenGL,
				ContextProfile.Core,
				ContextFlags.ForwardCompatible,
				new APIVersion( 4, 6 ) ),
		};

		using var window = Window.Create( options );
		GL gl = null;
		SceneRenderer renderer = null;
		SkeletonOverlay overlay = null;
		ViewportHandleService handles = null;
		string handlesBone = null;
		// G4 auto-key is a deliberate, sticky mode: it survives selection changes and is only
		// turned on by the user, so a default drag stays an unkeyed pose override.
		bool autoKey = false;
		IInputContext input = null;
		IMouse mouse = null;
		IKeyboard keyboard = null;

		var camera = new OrbitCamera();
		var loader = new DocumentLoader();
		ViewportMesh mesh = null;
		MarionettePlayer player = null;
		bool meshLogged = false;
		string pickStatus = "";

		int drawn = 0;
		int drawnWithMesh = 0;
		ViewportMesh liveFor = null;
		double lastFpsMark = 0;
		double lastFrameMark = -1;
		var clock = Stopwatch.StartNew();
		var loadClock = Stopwatch.StartNew();
		bool dragging = false, panning = false;
		bool skipMouseMove = false;
		Vector2 lastMouse = default;
		Vector2 mouseDownPos = default;
		string lastPickStatus = null;
		bool lastFlying = false, lastPlaying = false, lastBones = false;
		float lastSpeed = -1;
		string modelName = System.IO.Path.GetFileNameWithoutExtension( modelPath );

		bool turntable = arguments.Turntable;
		int wireframeMode = arguments.Wireframe ? 1 : 0;
		float currentExposure = arguments.Exposure;
		float keyLightAngle = 0f;
		float lastExposure = currentExposure;
		int lastWire = wireframeMode;
		bool lastTurn = turntable;
		float lastLight = keyLightAngle;
		bool lastGrid = true;

		// G4: keep the typed handle set in sync with the current selection. Hover/press re-drive
		// the hit test from the live geometry each event; the service itself is rebuilt only when
		// the selection or the camera scale drifts, so WantsPointerCapture stays authoritative.
		// FK is the deliberate default: G4 only enters IK through an explicit user gesture, never
		// inferred from camera state, so a gizmo drag always writes local bone keys.
		void SyncHandles()
		{
			int bone = overlay?.SelectedBone ?? -1;
			string want = bone >= 0 && mesh?.Rig is { Count: > 0 } && player is not null
				? mesh.Rig.Bones[bone].Name : null;
			if ( want is null )
			{
				handles?.Dispose();
				handles = null;
				handlesBone = null;
				return;
			}
			// Constant angular size: these models are hundreds of world units tall, so a fixed
			// scale would put a 1-unit gizmo inside a single pixel. Rebuild when the camera
			// zooms past a tenth of the current scale so geometry and hit radii always agree.
			float scale = Math.Clamp( camera.Distance * 0.08f, 0.01f, 200f );
			if ( handles is not null && want == handlesBone &&
				Math.Abs( handles.Scale - scale ) <= Math.Max( 1e-3f, handles.Scale * 0.1f ) ) return;

			handles?.Dispose();
			handles = null;
			handlesBone = want;
			var session = loader.Session;
			if ( session is null ) { handlesBone = null; return; }
			handles = new ViewportHandleService( session, want, player );
			handles.AutoKey = autoKey;
			handles.KeyFrame = CurrentKeyFrame();
			handles.Rebuild( GizmoFrame.World, scale, GizmoHandleFilter.All );
		}

		// Auto-key writes at the playhead, so the key lands where the user is looking. Without a
		// clip or a frame rate there is nothing to key against and the service fails closed.
		int CurrentKeyFrame()
		{
			var clip = loader.Session?.Clip;
			if ( player is null || clip is null || clip.FrameRate <= 0 ) return 0;
			return Math.Max( 0, (int)MathF.Round( (float)player.Time * clip.FrameRate ) );
		}

		HandleViewport CurrentViewport()
		{
			var w = window.Size;
			return new HandleViewport( 0, 0, w.X, w.Y );
		}
		Ray CurrentRay( Vector2 point )
		{
			var w = window.Size;
			var (ro, rd) = camera.ScreenRay( point.X, point.Y, w.X, w.Y );
			return new Ray( ro, rd );
		}

		window.Load += () =>
		{
			gl = GL.GetApi( window );
			Console.WriteLine( $"GL       : {gl.GetStringS( StringName.Version )}" );
			Console.WriteLine( $"renderer : {gl.GetStringS( StringName.Renderer )}" );
			Console.WriteLine( $"vendor   : {gl.GetStringS( StringName.Vendor )}" );
			Console.WriteLine( $"GLSL     : {gl.GetStringS( StringName.ShadingLanguageVersion )}" );

			gl.Enable( EnableCap.DepthTest );
			gl.Enable( EnableCap.Multisample );
			gl.Viewport( window.FramebufferSize );

			renderer = new SceneRenderer( gl );
			renderer.View = arguments.View;
			renderer.AntiAliasing = arguments.AntiAliasing;
			renderer.ShadowRadius = arguments.ShadowRadius;
			renderer.GroundShadow = arguments.GroundShadow;
			if (arguments.Hdri is not null) renderer.LoadEnvironmentAsync(arguments.Hdri).GetAwaiter().GetResult();
			renderer.WireframeMode = wireframeMode;

			overlay = new SkeletonOverlay( gl );

			input = window.CreateInput();
			if ( input.Keyboards.Count > 0 )
			{
				keyboard = input.Keyboards[0];
				keyboard.KeyDown += ( _, key, _ ) =>
				{
					if ( key == Key.Escape && handles is not null && handles.Active != GizmoHandleId.None )
					{
						// G4: Escape aborts an in-progress gizmo drag and releases capture.
						handles.Cancel( HandleCancelReason.Escape );
						dragging = false;
					}
					else if ( key == Key.F )
					{
						if ( camera.Flying ) camera.EndFly();
						else camera.BeginFly();
					}
					else if ( (key == Key.R || key == Key.Home) && mesh is not null )
					{
						camera.Frame( mesh.Centre, mesh.Radius );
					}
					else if ( key == Key.B && overlay is not null )
					{
						overlay.ShowBones = !overlay.ShowBones;
					}
					else if ( key == Key.G && overlay is not null )
					{
						overlay.ShowGrid = !overlay.ShowGrid;
					}
					else if ( key == Key.T )
					{
						turntable = !turntable;
					}
					else if ( key == Key.W && renderer is not null )
					{
						wireframeMode = (wireframeMode + 1) % 3;
						renderer.WireframeMode = wireframeMode;
					}
					else if ( key is Key.Number1 or Key.Keypad1 )
					{
						camera.YawDegrees = modelPath.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) ? -90f : 0f;
						camera.PitchDegrees = 0f;
					}
					else if ( key is Key.Number3 or Key.Keypad3 )
					{
						camera.YawDegrees = modelPath.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) ? 0f : 90f;
						camera.PitchDegrees = 0f;
					}
					else if ( key is Key.Number7 or Key.Keypad7 )
					{
						camera.PitchDegrees = 88.5f;
					}
					else if ( key is Key.Number9 or Key.Keypad9 )
					{
						camera.YawDegrees = modelPath.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) ? 90f : 180f;
						camera.PitchDegrees = 0f;
					}
					else if ( (key is Key.Number0 or Key.Keypad0) && mesh is not null )
					{
						camera.Frame( mesh.Centre, mesh.Radius );
					}
					else if ( key is Key.Minus or Key.KeypadSubtract )
					{
						currentExposure = Math.Clamp( currentExposure - 0.1f, 0.1f, 8f );
					}
					else if ( key is Key.Equal or Key.KeypadAdd )
					{
						currentExposure = Math.Clamp( currentExposure + 0.1f, 0.1f, 8f );
					}
					else if ( key == Key.LeftBracket )
					{
						keyLightAngle -= 15f;
					}
					else if ( key == Key.RightBracket )
					{
						keyLightAngle += 15f;
					}
					else if ( key == Key.L )
					{
						keyLightAngle = 0f;
					}
					else if ( key == Key.F12 && gl is not null )
					{
						string timeStamp = DateTime.Now.ToString( "yyyyMMdd_HHmmss" );
						string shotFile = System.IO.Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), $"geppetto_{timeStamp}.png" );
						SaveScreenshot( gl, window.FramebufferSize.X, window.FramebufferSize.Y, shotFile );
						Console.WriteLine( $"[CAPTURE] Saved high-res screenshot to: {shotFile}" );
					}
					else if ( key == Key.K )
					{
						// G4: opt into auto-key deliberately; the next gizmo commit also writes a
						// key through E3, atomically with the pose override.
						autoKey = !autoKey;
						if ( handles is not null )
						{
							handles.AutoKey = autoKey;
							handles.KeyFrame = CurrentKeyFrame();
						}
					}
					else if ( (key == Key.P || key == Key.Space) && player is not null && !camera.Flying )
					{
						player.Playing = !player.Playing;
					}
				};
			}
			if ( input.Mice.Count > 0 )
			{
				mouse = input.Mice[0];
				mouse.MouseMove += ( _, pos ) =>
				{
					var cur = new Vector2( pos.X, pos.Y );
					if ( skipMouseMove )
					{
						skipMouseMove = false;
						lastMouse = cur;
						return;
					}
					SyncHandles();
					// G4: an active gizmo drag owns the pointer; hover otherwise.
					if ( handles is not null && ( handles.WantsPointerCapture || !dragging ) )
					{
						bool consumed = handles.WantsPointerCapture
							? handles.Move( cur, CurrentViewport(), CurrentRay( cur ) )
							: handles.Hover( cur, CurrentViewport(), CurrentRay( cur ) );
						if ( consumed )
						{
							lastMouse = cur;
							return;
						}
					}
					if ( panning )
					{
						var d = cur - lastMouse;
						var side = Vector3.Normalize( Vector3.Cross( camera.Forward, Vector3.UnitZ ) );
						var up = Vector3.UnitZ;
						camera.Target += (-side * d.X + up * d.Y) * (camera.Distance * 0.0015f);
					}
					else if ( dragging )
					{
						var d = cur - lastMouse;
						if ( camera.Flying ) camera.Orbit( -d.X * 0.12f, d.Y * 0.12f );
						else camera.Orbit( d.X * 0.35f, -d.Y * 0.35f );
					}
					else if ( mesh?.Rig is { Count: > 0 } && player is not null && overlay is not null && overlay.ShowBones )
					{
						var fb = window.Size; // mouse coordinates are logical pixels, not framebuffer pixels
						var (ro, rd) = camera.ScreenRay( cur.X, cur.Y, fb.X, fb.Y );
						overlay.HoveredBone = SkeletonOverlay.PickBone( ro, rd, mesh.Rig, player.WorldPose );
					}
					lastMouse = cur;
				};
				mouse.MouseDown += ( _, btn ) =>
				{
					var cur = new Vector2( mouse.Position.X, mouse.Position.Y );
					mouseDownPos = cur;
					if ( btn == MouseButton.Left )
					{
						SyncHandles();
						// G4: a press that lands on a handle captures the pointer for the drag.
						if ( handles is not null && handles.Press( cur, CurrentViewport(), CurrentRay( cur ) ) )
						{
							lastMouse = cur;
							return;
						}
					}
					if ( btn == MouseButton.Middle )
					{
						panning = true;
					}
					else if ( btn == MouseButton.Left || btn == MouseButton.Right )
					{
						dragging = true;
					}
					lastMouse = cur;
				};
				mouse.MouseUp += ( _, btn ) =>
				{
					var cur = new Vector2( mouse.Position.X, mouse.Position.Y );
					if ( btn == MouseButton.Middle )
					{
						panning = false;
					}
					if ( btn == MouseButton.Left || btn == MouseButton.Right )
					{
						dragging = false;
						if ( btn == MouseButton.Left && handles is not null && handles.WantsPointerCapture )
						{
							handles.Release( cur, CurrentViewport(), CurrentRay( cur ) );
							lastMouse = cur;
							return;
						}
						// Click without drag in Orbit mode picks a 3D bone or Effigy body.
						if ( btn == MouseButton.Left && !camera.Flying && Vector2.DistanceSquared( cur, mouseDownPos ) <= 16f && mesh is not null && overlay is not null )
						{
							var fb = window.Size;
							var (ro, rd) = camera.ScreenRay( cur.X, cur.Y, fb.X, fb.Y );
							int boneHit = player is not null && overlay.ShowBones ? SkeletonOverlay.PickBone( ro, rd, mesh.Rig, player.WorldPose ) : -1;
							if ( boneHit >= 0 )
							{
								overlay.SelectedBone = boneHit;
								overlay.SelectedBody = -1;
								pickStatus = $" · Bone [{mesh.Rig.Bones[boneHit].Name}]";
								Console.WriteLine( $"picked   : bone #{boneHit} '{mesh.Rig.Bones[boneHit].Name}'" );
							}
							else
							{
								int bodyHit = mesh.Picker.Pick( ro, rd, player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones );
								overlay.SelectedBody = bodyHit;
								if ( bodyHit >= 0 )
								{
									var bp = mesh.BodyPicks[bodyHit];
									int boundBone = !string.IsNullOrEmpty( bp.BoneName ) && mesh.Rig is not null
										? mesh.Rig.IndexOf( bp.BoneName ) : -1;
									overlay.SelectedBone = boundBone;
									pickStatus = !string.IsNullOrEmpty( bp.BoneName )
										? $" · Body [{bp.Name} → {bp.BoneName}]"
										: $" · Body [{bp.Name}]";
									Console.WriteLine( $"picked   : body '{bp.Name}' ({bp.Id}) bone='{bp.BoneName}'" );
								}
								else
								{
									overlay.SelectedBone = -1;
									pickStatus = "";
								}
							}
						}
					}
				};
				mouse.Scroll += ( _, wheel ) =>
				{
					if ( camera.Flying ) camera.AdjustFlySpeed( wheel.Y );
					else camera.Dolly( wheel.Y > 0 ? 1 : -1 );
				};
			}

			Console.WriteLine( $"loading  : {modelPath}" );

			if ( stats )
				Console.WriteLine( "stats    : frame_ms is frame-to-frame wall time, vsync included, after 3 model frames; earlier intervals are startup_frame_ms" );

			loader.Start( modelPath, clipPath, animate, arguments.Time );
		};

		window.FramebufferResize += size =>
		{
			gl?.Viewport( size );
		};

		window.FocusChanged += focused =>
		{
			// G4: losing focus mid-drag must release pointer capture exactly like Escape.
			if ( !focused )
			{
				dragging = false;
				panning = false;
				if ( handles is not null && handles.Active != GizmoHandleId.None )
					handles.Cancel( HandleCancelReason.FocusLost );
			}
		};

		window.Render += dt =>
		{
			if ( camera.Flying && keyboard is not null )
			{
				float forward = (keyboard.IsKeyPressed( Key.W ) ? 1f : 0f) - (keyboard.IsKeyPressed( Key.S ) ? 1f : 0f);
				float right = (keyboard.IsKeyPressed( Key.D ) ? 1f : 0f) - (keyboard.IsKeyPressed( Key.A ) ? 1f : 0f);
				float up = (keyboard.IsKeyPressed( Key.E ) ? 1f : 0f) - (keyboard.IsKeyPressed( Key.Q ) ? 1f : 0f);
				camera.MoveFly( forward, right, up, (float)Math.Min( dt, 0.1 ), keyboard.IsKeyPressed( Key.ShiftLeft ) || keyboard.IsKeyPressed( Key.ShiftRight ) );
			}
			else if ( turntable && !dragging )
			{
				camera.Orbit( (float)(dt * 25.0), 0 );
			}

			if ( stats )
			{
				var now = clock.Elapsed.TotalSeconds;

				if ( lastFrameMark >= 0 )
					Console.WriteLine( $"{(drawnWithMesh >= 3 ? "frame_ms" : "startup_frame_ms")} {(now - lastFrameMark) * 1000.0:F3}" );

				lastFrameMark = now;
			}

			if ( gl is null || renderer is null ) return;

			// Promote a finished load onto the GL thread.
			if ( mesh is null && loader.Status == DocumentLoader.State.Ready && loader.Mesh is not null )
			{
				// G4: the document changed underneath any live gizmo; abort before swapping.
				handles?.Cancel( HandleCancelReason.SourceReload );
				handles?.Dispose();
				handles = null;
				handlesBone = null;
				mesh = loader.Mesh;
				if ( arguments.Modifiers is not null )
				{
					// R9: evaluate the live modifier stack on the loaded source before upload.
					var stack = ModifierStack.FromSpec( arguments.Modifiers, mesh );
					mesh = stack.Apply( mesh );
					Console.WriteLine( $"modifiers: {stack.LastSummary} in {stack.LastSeconds * 1000:F1} ms" );
				}
				mesh.Upload( gl );
				camera.Frame( mesh.Centre, mesh.Radius );
				camera.ApplyStartView( modelPath, isoView, arguments.Yaw, arguments.Pitch );
				player = loader.Player;
				// The clip's reference props are part of the scene: the same lit path, their
				// own ModelDoc and materials.
				renderer.Props = loader.Snapshot?.Props ?? Array.Empty<PropScene>();

				if ( !meshLogged )
				{
					meshLogged = true;
					Console.WriteLine(
						$"loaded   : {mesh.BodyCount} {(modelPath.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) ? "FBX pieces" : "Effigy bodies")}{(System.IO.Path.GetFileName( modelPath ).Equals( "camhead.effigy", StringComparison.OrdinalIgnoreCase ) ? " + 6 authored FBX meshes" : "")}, {mesh.TriangleCount} tris, {mesh.Batches.Length} material batches, {mesh.Rig?.Count ?? 0} bones" );
					Console.WriteLine( $"cache    : {loader.CacheNote}" );
					Console.WriteLine(
						$"timing   : load {loader.LoadSeconds:F3}s  extract {loader.ExtractSeconds:F3}s  total {loader.TotalSeconds:F3}s" );
					foreach ( var b in mesh.Batches )
						Console.WriteLine( $"  batch   : {b.MaterialName}  indices={b.IndexCount}  rgb=({b.R:F2},{b.G:F2},{b.B:F2})  rough={b.Roughness:F2}  metal={b.Metallic:F2}" );
					Console.WriteLine( $"bounds   : min={mesh.BoundsMin} max={mesh.BoundsMax} radius={mesh.Radius:F1}" );
					if ( player is { UsesKernelSolver: true } ) Console.WriteLine( "playback : live IK/root motion uses the kernel sampler (not allocation-free)" );
				}
			}
			else if ( loader.Status == DocumentLoader.State.Failed && !meshLogged )
			{
				meshLogged = true;
				Console.Error.WriteLine( "load failed:\n" + loader.Error );
			}

			float t = (float)clock.Elapsed.TotalSeconds;
			if ( mesh is null )
			{
				// Progress indicator: pulsing slate while rebuild runs.
				float pulse = 0.08f + 0.04f * (0.5f + 0.5f * MathF.Sin( t * 3f ));
				gl.ClearColor( pulse, pulse, pulse + 0.02f, 1f );
				gl.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit );
				window.Title = $"Geppetto — loading {System.IO.Path.GetFileNameWithoutExtension( modelPath )} ({loader.Phase}) {loadClock.Elapsed.TotalSeconds:F1}s…";
			}
			else
			{
				player?.Step( drawnWithMesh == 0 ? 0 : (float)Math.Min( dt, 0.1 ) );
				if ( arguments.Live is not null && !ReferenceEquals( liveFor, mesh ) )
				{
					renderer.Live.Layers.Clear();
					LiveFixtures.Add( renderer.Live, arguments.Live, mesh.BoundsMin, mesh.BoundsMax );
					liveFor = mesh;
				}
				if ( drawnWithMesh > 0 ) renderer.Live.Advance( (float)Math.Min( dt, 0.1 ) );

				bool capturing = screenshotPath is not null && drawnWithMesh == 0;
				using var capture = capturing ? new CaptureTarget( gl, width, height ) : null;

				var size = window.FramebufferSize;
				float aspect = capturing ? width / (float)height : size.X / (float)Math.Max( 1, size.Y );
				var view = camera.ViewMatrix();
				var proj = camera.ProjectionMatrix( aspect );
				var viewProj = view * proj;

				if ( Math.Abs( keyLightAngle ) > 1e-3f )
				{
					var baseKey = KeyShadowMap.Direction;
					float rad = keyLightAngle * MathF.PI / 180f;
					renderer.KeyDirection = new Vector3(
						baseKey.X * MathF.Cos( rad ) - baseKey.Y * MathF.Sin( rad ),
						baseKey.X * MathF.Sin( rad ) + baseKey.Y * MathF.Cos( rad ),
						baseKey.Z );
				}
				else
				{
					renderer.KeyDirection = KeyShadowMap.Direction;
				}

				renderer.Draw( mesh, player, camera, capturing ? width : size.X, capturing ? height : size.Y, currentExposure, capture?.Handle ?? 0 );

				if ( overlay is not null )
				{
					(Vector3 Min, Vector3 Max)? selectedBounds = overlay.SelectedBody >= 0
						? mesh.Picker.Bounds( overlay.SelectedBody, player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones ) : null;
					overlay.Draw( viewProj, mesh.Rig, player is null ? ReadOnlySpan<Effigy.Xform>.Empty : player.WorldPose, mesh.BodyPicks, selectedBounds,
						handles is { Geometry.Count: > 0 } ? handles.Geometry : null );
				}

				bool playing = player is { Playing: true }, bones = overlay is { ShowBones: true }, grid = overlay is { ShowGrid: true };
				if ( drawnWithMesh == 0 || lastPickStatus != pickStatus || lastPlaying != playing || lastBones != bones || lastFlying != camera.Flying || lastSpeed != camera.FlySpeed
					|| Math.Abs( lastExposure - currentExposure ) > 1e-3f || lastWire != wireframeMode || lastTurn != turntable || Math.Abs( lastLight - keyLightAngle ) > 1e-3f || lastGrid != grid )
				{
					string animTag = playing ? " · [ANIM]" : "";
					string boneTag = bones ? " · [BONES]" : "";
					string turnTag = turntable ? " · [TURNTABLE]" : "";
					string wireTag = wireframeMode == 1 ? " · [WIRE OVERLAY]" : wireframeMode == 2 ? " · [WIREFRAME]" : "";
					string gridTag = !grid ? " · [GRID OFF]" : "";
					string expTag = Math.Abs( currentExposure - 1.0f ) > 0.01f ? $" · [EXP {currentExposure:F1}x]" : "";
					string lightTag = Math.Abs( keyLightAngle ) > 0.1f ? $" · [LIGHT {keyLightAngle:F0}°]" : "";
					window.Title = $"Geppetto — {modelName}{animTag}{boneTag}{turnTag}{wireTag}{gridTag}{expTag}{lightTag}{pickStatus} — {(camera.Flying ? $"Fly {camera.FlySpeed:F1} u/s · WASD/QE · Shift sprint · F orbit" : "Click pick · Drag orbit · MMB pan · RMB/F fly · P anim · B bones · T turntable · W wire · G grid · 1/3/7/9/0 views · [-/+] exp · [[/]] light · F12 shot")}";
					lastPickStatus = pickStatus; lastPlaying = playing; lastBones = bones; lastFlying = camera.Flying; lastSpeed = camera.FlySpeed;
					lastExposure = currentExposure; lastWire = wireframeMode; lastTurn = turntable; lastLight = keyLightAngle; lastGrid = grid;
				}
				drawnWithMesh++;

				// One-shot acceptance capture — not part of the steady-state render loop.
				if ( capturing )
				{
					SaveScreenshot( gl, width, height, screenshotPath );
					capture.Present( size.X, size.Y );
				}
				if ( drawnWithMesh == 1 && gl.GetError() is var error && error != GLEnum.NoError )
					throw new InvalidOperationException( $"OpenGL reported {error} on the first model frame." );
			}

			drawn++;
			if ( drawn % 60 == 0 )
			{
				double now = clock.Elapsed.TotalSeconds;
				double fps = 60.0 / Math.Max( 1e-6, now - lastFpsMark );
				lastFpsMark = now;
				string state = mesh is null ? $"loading:{loader.Phase}" : camera.Flying ? "fly" : "orbit";
				Console.WriteLine( $"frame {drawn}  {fps:F1} fps  [{state}]" );
			}

			// --frames counts only after the mesh is on the GPU, so a 12 s rebuild does not
			// make the smoke test exit on an empty window.
			if ( frames > 0 && loader.Status == DocumentLoader.State.Failed ) window.Close();
			if ( frames > 0 && drawnWithMesh >= frames ) window.Close();
		};

		window.Closing += () =>
		{
			try { if ( mouse is not null && mouse.Cursor != null ) mouse.Cursor.CursorMode = CursorMode.Normal; } catch { }
			handles?.Cancel( HandleCancelReason.WindowClose );
			handles?.Dispose();
			overlay?.Dispose();
			mesh?.ReleaseGpu( gl );
			renderer?.Dispose();
			input?.Dispose();
		};

		window.Run();

		Console.WriteLine( $"drew {drawn} frame(s)" );

		if ( loader.Status == DocumentLoader.State.Ready )
		{
			Console.WriteLine( $"load_s {loader.LoadSeconds:F3}" );
			Console.WriteLine( $"total_s {loader.TotalSeconds:F3}" );
		}

		return loader.Status == DocumentLoader.State.Failed ? 1 : 0;
	}

	internal static unsafe void SaveScreenshot( GL gl, int width, int height, string path )
	{
		if ( width <= 0 || height <= 0 ) return;
		var pixels = new byte[width * height * 3];
		gl.PixelStore( PixelStoreParameter.PackAlignment, 1 );
		fixed ( byte* raw = pixels )
			gl.ReadPixels( 0, 0, (uint)width, (uint)height, PixelFormat.Rgb, PixelType.UnsignedByte, raw );

		// GL is bottom-up; flip for a normal PNG.
		var row = width * 3;
		var flipped = new byte[pixels.Length];
		for ( int y = 0; y < height; y++ )
			System.Buffer.BlockCopy( pixels, (height - 1 - y) * row, flipped, y * row, row );

		System.IO.Directory.CreateDirectory( System.IO.Path.GetDirectoryName( System.IO.Path.GetFullPath( path ) ) );
		Effigy.PngWriter.WriteFile( path, flipped, width, height );
		Console.WriteLine( $"screenshot: {path}" );
	}
}
