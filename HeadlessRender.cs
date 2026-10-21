using System;
using System.Diagnostics;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

public static class HeadlessRender
{
	public static int Run( DesktopOptions options )
	{
		var loader = new DocumentLoader();
		loader.Start( options.Model, options.Clip, options.Animate, options.Time );
		loader.Completion.GetAwaiter().GetResult();
		if ( loader.Status != DocumentLoader.State.Ready ) throw new InvalidOperationException( loader.Error );
		var mesh = loader.Mesh;
		if ( options.Modifiers is not null )
		{
			// R9: the live modifier stack runs on the authored source before upload.
			var stack = ModifierStack.FromSpec( options.Modifiers, mesh );
			mesh = stack.Apply( mesh );
			Console.WriteLine( $"modifiers: {stack.LastSummary} in {stack.LastSeconds * 1000:F1} ms" );
		}
		var player = loader.Player;
		using var context = new HeadlessGlContext();
		var gl = context.Gl;
		using var renderer = new SceneRenderer( gl );
		renderer.View = options.View;
		renderer.AntiAliasing = options.AntiAliasing;
		renderer.ShadowRadius = options.ShadowRadius;
		renderer.GroundShadow = options.GroundShadow;
		if (options.Hdri is not null) renderer.LoadEnvironmentAsync(options.Hdri).GetAwaiter().GetResult();
		using var overlay = new SkeletonOverlay( gl );
		using var target = new CaptureTarget( gl, options.Width, options.Height );
		var camera = new OrbitCamera();
		camera.Frame( mesh.Centre, mesh.Radius );
		// A clip can move the mesh well outside its bind bounds; frame the evaluated
		// pose so the capture shows the character, not empty space.
		if ( player is not null )
		{
			player.Step( 0 );
			var posed = mesh.Picker.Bounds( player.GpuBones );
			if ( float.IsFinite( posed.Min.Length() ) && float.IsFinite( posed.Max.Length() ) )
			{
				var centre = (posed.Min + posed.Max) * 0.5f;
				float radius = Math.Max( (posed.Max - posed.Min).Length() * 0.5f, 0.1f );
				camera.Frame( centre, radius );
				float vertical = camera.FovDegrees * MathF.PI / 360;
				float horizontal = MathF.Atan( MathF.Tan( vertical ) * options.Width / Math.Max( 1f, options.Height ) );
				camera.Distance = Math.Max( camera.Distance, radius * 1.15f / MathF.Sin( Math.Min( vertical, horizontal ) ) );
			}
		}
		if ( options.Model.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) ) camera.YawDegrees = -55;
		if ( options.Iso ) { camera.YawDegrees = -38.66f; camera.PitchDegrees = 23.2f; }
		int vertexCount = mesh.Vertices.Length / ViewportMesh.FloatsPerVertex;
		mesh.Upload( gl );
		// The clip's reference props: the same lit path, their own ModelDoc and materials.
		renderer.Props = loader.Snapshot?.Props ?? Array.Empty<PropScene>();
		if ( options.Fog > 0 )
		{
			renderer.Volumes = VolumeGrid.GlobalFog( options.Fog, mesh.Centre, mesh.Radius );
			renderer.Volumes.ForceFallback = options.FogFallback;
		}
		if ( options.Live is not null ) LiveFixtures.Add( renderer.Live, options.Live, mesh.BoundsMin, mesh.BoundsMax );
		renderer.MotionBlurShutter = options.MotionBlur;
		int frames = Math.Max( Math.Max( 1, options.Frames ), options.ShotFrame + 1 );
		try
		{
			var clock = Stopwatch.StartNew();
			for ( int frame = 0; frame < frames; frame++ )
			{
				player?.Step( frame == 0 ? 0 : 1f / 60 );
				if ( frame > 0 ) renderer.Live.Advance( 1f / 60 );
				renderer.Draw( mesh, player, camera, options.Width, options.Height, options.Exposure, target.Handle );
				overlay.Draw( camera.ViewMatrix() * camera.ProjectionMatrix( options.Width / (float)options.Height ), mesh.Rig,
					player is null ? ReadOnlySpan<Xform>.Empty : player.WorldPose, mesh.BodyPicks );
				if ( frame == options.ShotFrame && options.Screenshot is not null ) Program.SaveScreenshot( gl, options.Width, options.Height, options.Screenshot );
			}
			gl.Finish(); // only this verification path blocks on the GPU; not the interactive viewport
			if ( gl.GetError() is var error && error != GLEnum.NoError ) throw new InvalidOperationException( $"Headless GL error: {error}" );
			Console.WriteLine( $"loaded   : {mesh.BodyCount} pieces/bodies, {mesh.TriangleCount} tris, {mesh.Batches.Length} material batches, {mesh.Rig?.Count ?? 0} bones" );
			if ( player is not null )
				Console.WriteLine( $"clip     : {player.ClipSpaceNote} (translation scale {player.ClipPositionScale:F4})" );
			if ( renderer.Props.Count > 0 )
			{
				var names = new System.Collections.Generic.List<string>();
				foreach ( var prop in renderer.Props ) names.Add( prop.Name );
				Console.WriteLine( $"props    : {string.Join( ", ", names )}" );
			}
			// A model that still looks grey says why here: a .vmat the project does not
			// have, an unsupported effect, a texture that would not decode.
			var diagnostics = loader.Snapshot?.MaterialDiagnostics;
			if ( diagnostics is { Count: > 0 } )
				foreach ( var diagnostic in diagnostics )
					Console.WriteLine( $"material : {diagnostic.Severity} {diagnostic.MaterialId}: {diagnostic.Message}" );
			Console.WriteLine( $"cache    : {loader.CacheNote}" );
			Console.WriteLine( $"timing   : load {loader.LoadSeconds:F3}s extract {loader.ExtractSeconds:F3}s total {loader.TotalSeconds:F3}s" );
			Console.WriteLine( $"load_s {loader.LoadSeconds:F3}" );
			Console.WriteLine( $"total_s {loader.TotalSeconds:F3}" );
			if ( options.Stats )
			{
				var st = renderer.Stats;
				foreach ( var kind in Enum.GetValues<RenderPassKind>() )
					Console.WriteLine( $"pass     : {kind,-13} cpu {st.CpuMs[(int)kind],7:F3} ms  gpu {st.GpuMs[(int)kind],7:F3} ms  alloc {st.AllocatedBytes[(int)kind]} B" );
				Console.WriteLine( $"frame_alloc_bytes {st.FrameAllocatedBytes}" );
				Console.WriteLine( $"workload : {vertexCount} verts, {mesh.TriangleCount} tris, {mesh.Batches.Length} batches, {mesh.Budget.GpuTextureCount} textures ({mesh.Budget.GpuTextureBytes / 1048576.0:F1} MiB), {options.Width}x{options.Height}" );
				Console.WriteLine( $"gl_live  : {RenderTarget.LiveObjects} render-target objects, {MsaaPass.LiveObjects} MSAA objects, {MeshResourceBudget.LiveGpuTextures} mesh textures" );
				if ( !renderer.Live.IsEmpty ) Console.WriteLine( $"live     : {renderer.Live.Layers.Count} layers, {renderer.Live.VertexCount} verts last frame" );
			}
			Console.WriteLine( $"headless : drew {frames} frame(s) in {clock.Elapsed.TotalSeconds:F3}s (GPU completion included; not present/vsync FPS)" );
		}
		finally { mesh.ReleaseGpu( gl ); }
		return 0;
	}
}
