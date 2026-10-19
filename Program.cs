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
/// Compiles the engine-free Effigy kernel from ../geppetto/Effigy.
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
	internal const string VertexSrc = @"#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in vec4 aBoneIndices;
layout(location = 3) in vec4 aBoneWeights;
layout(location = 4) in vec2 aUv;
layout(location = 5) in vec4 aTangent;

uniform mat4 uMvp;
uniform mat4 uModel;
uniform int uSkinned;
uniform mat4 uBones[220];

out vec3 vNrm;
out vec3 vWorld;
out vec2 vUv;
out vec3 vTangent;
out float vHandedness;

void main() {
	vec3 pos = aPos;
	vec3 nrm = aNrm;
	vec3 tangent = aTangent.xyz;
	mat3 deformation = mat3(1.0);

	if (uSkinned != 0) {
		int b0 = clamp(int(aBoneIndices.x + 0.5), 0, 219);
		int b1 = clamp(int(aBoneIndices.y + 0.5), 0, 219);
		int b2 = clamp(int(aBoneIndices.z + 0.5), 0, 219);
		int b3 = clamp(int(aBoneIndices.w + 0.5), 0, 219);

		mat4 skin = uBones[b0] * aBoneWeights.x
		          + uBones[b1] * aBoneWeights.y
		          + uBones[b2] * aBoneWeights.z
		          + uBones[b3] * aBoneWeights.w;

		pos = (skin * vec4(aPos, 1.0)).xyz;
		deformation = mat3(skin);
	}

	vec4 world = uModel * vec4(pos, 1.0);
	vWorld = world.xyz;
	mat3 transform = mat3(uModel) * deformation;
	// The normal uses inverse transpose; the tangent is a direction under the same skin/model.
	vNrm = transpose(inverse(transform)) * nrm;
	vTangent = transform * tangent;
	vHandedness = aTangent.w * (determinant(transform) < 0.0 ? -1.0 : 1.0);
	vUv = aUv;
	gl_Position = uMvp * vec4(pos, 1.0);
}
";

	internal const string FragmentSrc = @"#version 460 core
in vec3 vNrm;
in vec3 vWorld;
in vec2 vUv;
in vec3 vTangent;
in float vHandedness;

uniform vec3 uAlbedo;
uniform sampler2D uAlbedoMap;
uniform sampler2D uNormalMap;
uniform sampler2D uRoughnessMap;
uniform sampler2D uMetallicMap;
uniform sampler2D uEmissionMap;
uniform sampler2D uOcclusionMap;
uniform float uNormalY;
uniform float uRoughness;
uniform float uMetallic;
uniform vec3 uEmission;
uniform vec3 uEye;
uniform vec3 uKeyDirection;
uniform mat4 uLightViewProjection;
uniform sampler2D uShadowMap;
uniform float uExposure;

out vec4 FragColor;

const float PI = 3.14159265359;

float ggx(float nh, float nl, float nv, float roughness) {
	float a = max(roughness * roughness, 0.0016);
	float a2 = a * a;
	float d = nh * nh * (a2 - 1.0) + 1.0;
	float D = a2 / (PI * d * d);
	float k = a * 0.5;
	float G = (nl / (nl * (1.0 - k) + k)) * (nv / (nv * (1.0 - k) + k));
	return (D * G) / max(4.0 * nl * nv, 1e-4);
}

vec3 fresnel(vec3 f0, float c) {
	float k = pow(1.0 - clamp(c, 0.0, 1.0), 5.0);
	return f0 + (vec3(1.0) - f0) * k;
}

vec3 shadeLight(vec3 n, vec3 v, float nv, vec3 lDir, vec3 radiance, float lightSize, vec3 diffuseCol, vec3 f0, float roughness) {
	vec3 l = normalize(lDir);
	float nl = max(dot(n, l), 0.0);
	if (nl <= 0.0) return vec3(0.0);
	vec3 h = normalize(l + v);
	float nh = max(dot(n, h), 0.0);
	float vh = max(dot(v, h), 0.0);
	float r = max(roughness, lightSize * 0.6);
	float spec = ggx(nh, nl, nv, r);
	vec3 F = fresnel(f0, vh);
	// Energy-conserving diffuse: don't amplify it by 3.1 or add specular over full diffuse.
	return ((vec3(1.0) - F) * diffuseCol / PI + F * spec) * radiance * nl;
}

float keyVisibility(vec3 n) {
	vec4 clip = uLightViewProjection * vec4(vWorld, 1.0);
	vec3 p = clip.xyz / clip.w * 0.5 + 0.5;
	if (p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0 || p.z < 0.0 || p.z > 1.0) return 1.0;
	float bias = max(0.0009 * (1.0 - max(dot(n, uKeyDirection), 0.0)), 0.0003);
	vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
	float visible = 0.0;
	for (int y = -1; y <= 1; ++y)
		for (int x = -1; x <= 1; ++x)
			visible += p.z - bias <= texture(uShadowMap, p.xy + vec2(x,y) * texel).r ? 1.0 : 0.0;
	return visible / 9.0;
}

vec3 tonemapAces(vec3 c) {
	vec3 x = c * 0.85;
	return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

vec3 linearToSrgb(vec3 c) {
	vec3 lo = c * 12.92;
	vec3 hi = 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055;
	return clamp(mix(hi, lo, lessThanEqual(c, vec3(0.0031308))), 0.0, 1.0);
}

void main() {
	vec3 n = normalize(vNrm);
	vec3 t = normalize(vTangent - n * dot(n, vTangent));
	vec3 b = cross(n, t) * (vHandedness < 0.0 ? -1.0 : 1.0);
	vec3 mapNormal = texture(uNormalMap, vUv).xyz * 2.0 - 1.0;
	mapNormal.y *= uNormalY;
	n = normalize(mat3(t, b, n) * normalize(mapNormal));
	vec3 v = normalize(uEye - vWorld);
	if (dot(n, v) < 0.0) n = -n;
	float nv = max(dot(n, v), 1e-3);

	// Separate scalar maps use red (R), not implicit ORM packing. Factors are linear multipliers.
	float rough = clamp(uRoughness * texture(uRoughnessMap, vUv).r, 0.04, 1.0);
	float metal = clamp(uMetallic * texture(uMetallicMap, vUv).r, 0.0, 1.0);
	vec3 albedo = uAlbedo * texture(uAlbedoMap, vUv).rgb;
	float ao = texture(uOcclusionMap, vUv).r;
	vec3 f0 = mix(vec3(0.04), albedo, metal);
	vec3 diffuseCol = albedo * (1.0 - metal);

	// World-fixed studio: warm key with self-shadowing, cool fill, restrained rim.
	vec3 keyDir = normalize(uKeyDirection);
	vec3 fillDir = vec3(-0.7, -0.5, 0.4);
	vec3 rimDir = vec3(0.2, 0.8, 0.6);

	vec3 hdr = vec3(0.0);
	hdr += shadeLight(n, v, nv, keyDir, vec3(1.0, 0.95, 0.88)*3.0, 0.22, diffuseCol, f0, rough) * keyVisibility(n);
	hdr += shadeLight(n, v, nv, fillDir, vec3(0.65, 0.80, 1.0)*0.8, 0.40, diffuseCol, f0, rough);
	hdr += shadeLight(n, v, nv, rimDir, vec3(1.4), 0.25, diffuseCol, f0, rough);

	// Sky/ground hemisphere ambient + studio specular reflection matching ShadedRender.cs:1136-1153
	vec3 skyCol    = vec3(0.18, 0.21, 0.28);
	vec3 groundCol = vec3(0.07, 0.06, 0.055);
	vec3 hemi = mix(groundCol, skyCol, n.z * 0.5 + 0.5);
	hdr += diffuseCol * hemi * ao;

	vec3 refl = reflect(-v, n);
	vec3 envG = mix(groundCol, skyCol * 1.3, smoothstep(-0.3, 0.6, refl.z));
	vec3 envF = fresnel(f0, nv) * (1.0 - rough) * (1.0 - rough);
	hdr += envF * envG * ao;
	hdr += uEmission * texture(uEmissionMap, vUv).rgb;

	vec3 srgb = linearToSrgb(tonemapAces(hdr * uExposure));
	FragColor = vec4(srgb, 1.0);
}
";

	internal static IWindow ActiveWindow;

	public static int Main( string[] raw )
	{
		AppDomain.CurrentDomain.UnhandledException += ( _, e ) => {
			Console.Error.WriteLine( "FATAL UNHANDLED: " + e.ExceptionObject );
		};
		try
		{
			if ( raw.Length == 2 && raw[0] == "--ai-worker" ) return AiToolWorker.Run( raw[1] );
			if ( raw.Length > 0 && raw[0] == "--ai" ) return AiCli.Run( raw[1..] );
			return Run( DesktopOptions.Parse( raw ) );
		}
		catch ( Exception ex )
		{
			Console.Error.WriteLine( "error: " + ex.ToString() );
			return 1;
		}
	}

	static int Run( DesktopOptions arguments )
	{
		if ( arguments.Help ) { Console.WriteLine( DesktopOptions.Usage ); return 0; }
		int frames = arguments.Frames, width = arguments.Width, height = arguments.Height;
		string modelPath = arguments.Model, clipPath = arguments.Clip, screenshotPath = arguments.Screenshot;
		bool stats = arguments.Stats, animate = arguments.Animate, isoView = arguments.Iso;
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

		if ( arguments.Headless ) return HeadlessRender.Run( arguments );
		SdlWindowing.Use();
		SdlInput.Use();

		var options = WindowOptions.Default with
		{
			Size = new Vector2D<int>( width, height ),
			Title = "Geppetto — Camhead",
			VSync = !arguments.NoVsync,
			API = new GraphicsAPI(
				ContextAPI.OpenGL,
				ContextProfile.Core,
				ContextFlags.ForwardCompatible,
				new APIVersion( 4, 6 ) ),
		};

		var window = Window.Create( options );
		ActiveWindow = window;
		GC.SuppressFinalize( window );
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
		double lastFpsMark = 0;
		double lastFrameMark = -1;
		var clock = Stopwatch.StartNew();
		var loadClock = Stopwatch.StartNew();
		bool dragging = false;
		bool skipMouseMove = false;
		Vector2 lastMouse = default;
		Vector2 mouseDownPos = default;
		string lastPickStatus = null;
		bool lastFlying = false, lastPlaying = false, lastBones = false;
		float lastSpeed = -1;
		string modelName = System.IO.Path.GetFileNameWithoutExtension( modelPath );

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
			gl.Viewport( window.FramebufferSize );

			renderer = new SceneRenderer( gl );

			overlay = new SkeletonOverlay( gl );

			input = window.CreateInput();
			if ( input.Keyboards.Count > 0 )
			{
				keyboard = input.Keyboards[0];
				keyboard.KeyDown += ( _, key, _ ) =>
				{
					try
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
					}
					catch ( Exception ex )
					{
						Console.Error.WriteLine( "[KEY DOWN ERROR] " + ex );
					}
				};
			}
			if ( input.Mice.Count > 0 )
			{
				mouse = input.Mice[0];
				mouse.MouseMove += ( _, pos ) =>
				{
					try
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
						if ( dragging )
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
					}
					catch ( Exception ex )
					{
						Console.Error.WriteLine( "[MOUSE MOVE ERROR] " + ex );
					}
				};
				mouse.MouseDown += ( _, btn ) =>
				{
					try
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
						if ( btn == MouseButton.Right )
						{
							camera.BeginFly();
							skipMouseMove = true;
							dragging = true;
						}
						else if ( btn == MouseButton.Left && !camera.Flying )
						{
							dragging = true;
						}
						lastMouse = cur;
					}
					catch ( Exception ex )
					{
						Console.Error.WriteLine( "[MOUSE DOWN ERROR] " + ex );
					}
				};
				mouse.MouseUp += ( _, btn ) =>
				{
					try
					{
						var cur = new Vector2( mouse.Position.X, mouse.Position.Y );
						if ( btn == MouseButton.Right )
						{
							camera.EndFly();
							skipMouseMove = true;
							dragging = false;
						}
						else if ( btn == MouseButton.Left )
						{
							dragging = false;
							if ( handles is not null && handles.WantsPointerCapture )
							{
								handles.Release( cur, CurrentViewport(), CurrentRay( cur ) );
								lastMouse = cur;
								return;
							}
						// Click without drag in Orbit mode picks a 3D bone or Effigy body.
						if ( !camera.Flying && Vector2.DistanceSquared( cur, mouseDownPos ) <= 16f && mesh is not null && overlay is not null )
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
					}
					catch ( Exception ex )
					{
						Console.Error.WriteLine( "[MOUSE UP ERROR] " + ex );
					}
				};
				mouse.Scroll += ( _, wheel ) =>
				{
					try
					{
						if ( camera.Flying ) camera.AdjustFlySpeed( wheel.Y );
						else camera.Zoom( wheel.Y > 0 ? 0.9f : 1.1f );
					}
					catch ( Exception ex )
					{
						Console.Error.WriteLine( "[MOUSE SCROLL ERROR] " + ex );
					}
				};
			}

			Console.WriteLine( $"loading  : {modelPath}" );

			if ( stats )
				Console.WriteLine( "stats    : frame_ms is frame-to-frame wall time, vsync included, after 3 model frames; earlier intervals are startup_frame_ms" );

			loader.Start( modelPath, clipPath, animate, arguments.Time );
		};

		window.FramebufferResize += size =>
		{
			try { gl?.Viewport( size ); } catch { }
		};

		window.FocusChanged += focused =>
		{
			try
			{
				// G4: losing focus mid-drag must release pointer capture exactly like Escape.
				if ( !focused && handles is not null && handles.Active != GizmoHandleId.None )
				{
					handles.Cancel( HandleCancelReason.FocusLost );
					dragging = false;
				}
			}
			catch ( Exception ex )
			{
				Console.Error.WriteLine( "[FOCUS ERROR] " + ex );
			}
		};

		window.Render += dt =>
		{
			try
			{
				if ( camera.Flying && keyboard is not null )
				{
					float forward = (keyboard.IsKeyPressed( Key.W ) ? 1f : 0f) - (keyboard.IsKeyPressed( Key.S ) ? 1f : 0f);
					float right = (keyboard.IsKeyPressed( Key.D ) ? 1f : 0f) - (keyboard.IsKeyPressed( Key.A ) ? 1f : 0f);
					float up = (keyboard.IsKeyPressed( Key.E ) ? 1f : 0f) - (keyboard.IsKeyPressed( Key.Q ) ? 1f : 0f);
					camera.MoveFly( forward, right, up, (float)Math.Min( dt, 0.1 ), keyboard.IsKeyPressed( Key.ShiftLeft ) || keyboard.IsKeyPressed( Key.ShiftRight ) );
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
				mesh.Upload( gl );
				camera.Frame( mesh.Centre, mesh.Radius );
				if ( modelPath.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) ) camera.YawDegrees = -55;
				if ( isoView )
				{
					camera.YawDegrees = -38.66f;
					camera.PitchDegrees = 23.2f;
				}
				player = loader.Player;

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

				bool capturing = screenshotPath is not null && drawnWithMesh == 0;
				using var capture = capturing ? new CaptureTarget( gl, width, height ) : null;

				var size = window.FramebufferSize;
				float aspect = capturing ? width / (float)height : size.X / (float)Math.Max( 1, size.Y );
				var view = camera.ViewMatrix();
				var proj = camera.ProjectionMatrix( aspect );
				var viewProj = view * proj;
				renderer.Draw( mesh, player, camera, capturing ? width : size.X, capturing ? height : size.Y, arguments.Exposure, capture?.Handle ?? 0 );

				if ( overlay is not null )
				{
					(Vector3 Min, Vector3 Max)? selectedBounds = overlay.SelectedBody >= 0
						? mesh.Picker.Bounds( overlay.SelectedBody, player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones ) : null;
					overlay.Draw( viewProj, mesh.Rig, player is null ? ReadOnlySpan<Effigy.Xform>.Empty : player.WorldPose, mesh.BodyPicks, selectedBounds,
						handles is { Geometry.Count: > 0 } ? handles.Geometry : null );
				}

				bool playing = player is { Playing: true }, bones = overlay is { ShowBones: true };
				if ( drawnWithMesh == 0 || lastPickStatus != pickStatus || lastPlaying != playing || lastBones != bones || lastFlying != camera.Flying || lastSpeed != camera.FlySpeed )
				{
					string animTag = playing ? " · [ANIM ON]" : "";
					string boneTag = bones ? " · [BONES ON]" : "";
					try {
						window.Title = $"Geppetto — {modelName}{animTag}{boneTag}{pickStatus} — {(camera.Flying ? $"Fly {camera.FlySpeed:F1} u/s · wheel speed · WASD/QE · Shift sprint · F/RMB orbit" : "Click pick · Drag orbit · RMB/F fly · P animate · B bones · R reset")}";
					} catch { }
					lastPickStatus = pickStatus; lastPlaying = playing; lastBones = bones; lastFlying = camera.Flying; lastSpeed = camera.FlySpeed;
				}
				drawnWithMesh++;

				// One-shot acceptance capture — not part of the steady-state render loop.
				if ( capturing )
				{
					SaveScreenshot( gl, width, height, screenshotPath );
					capture.Present( size.X, size.Y );
				}
				if ( drawnWithMesh == 1 && gl.GetError() is var error && error != GLEnum.NoError )
					Console.Error.WriteLine( $"[GL WARNING] OpenGL reported {error} on the first model frame." );
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
			}
			catch ( Exception ex )
			{
				Console.Error.WriteLine( "[RENDER ERROR] " + ex );
			}
		};

		window.Closing += () =>
		{
			try
			{
				ActiveWindow = null;
				try { if ( mouse is not null ) mouse.Cursor.CursorMode = CursorMode.Normal; } catch { }
				handles?.Cancel( HandleCancelReason.WindowClose );
				handles?.Dispose();
				overlay?.Dispose();
				mesh?.ReleaseGpu( gl );
				renderer?.Dispose();
				input?.Dispose();
			}
			catch ( Exception ex )
			{
				Console.Error.WriteLine( "[CLOSING ERROR] " + ex );
			}
		};

		window.Run();
		GC.KeepAlive( window );
		try { window.Dispose(); } catch { }

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
