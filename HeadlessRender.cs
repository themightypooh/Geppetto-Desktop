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
		var player = loader.Player;
		using var context = new HeadlessGlContext();
		var gl = context.Gl;
		using var renderer = new SceneRenderer( gl );
		using var overlay = new SkeletonOverlay( gl );
		using var target = new CaptureTarget( gl, options.Width, options.Height );
		var camera = new OrbitCamera();
		camera.Frame( mesh.Centre, mesh.Radius );
		if ( options.Model.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) ) camera.YawDegrees = -55;
		if ( options.Iso ) { camera.YawDegrees = -38.66f; camera.PitchDegrees = 23.2f; }
		mesh.Upload( gl );
		int frames = Math.Max( 1, options.Frames );
		try
		{
			var clock = Stopwatch.StartNew();
			for ( int frame = 0; frame < frames; frame++ )
			{
				player?.Step( frame == 0 ? 0 : 1f / 60 );
				renderer.Draw( mesh, player, camera, options.Width, options.Height, options.Exposure, target.Handle );
				overlay.Draw( camera.ViewMatrix() * camera.ProjectionMatrix( options.Width / (float)options.Height ), mesh.Rig,
					player is null ? ReadOnlySpan<Xform>.Empty : player.WorldPose, mesh.BodyPicks );
				if ( frame == 0 && options.Screenshot is not null ) Program.SaveScreenshot( gl, options.Width, options.Height, options.Screenshot );
			}
			gl.Finish(); // only this verification path blocks on the GPU; not the interactive viewport
			if ( gl.GetError() is var error && error != GLEnum.NoError ) throw new InvalidOperationException( $"Headless GL error: {error}" );
			Console.WriteLine( $"loaded   : {mesh.BodyCount} pieces/bodies, {mesh.TriangleCount} tris, {mesh.Batches.Length} material batches, {mesh.Rig?.Count ?? 0} bones" );
			Console.WriteLine( $"cache    : {loader.CacheNote}" );
			Console.WriteLine( $"timing   : load {loader.LoadSeconds:F3}s extract {loader.ExtractSeconds:F3}s total {loader.TotalSeconds:F3}s" );
			Console.WriteLine( $"load_s {loader.LoadSeconds:F3}" );
			Console.WriteLine( $"total_s {loader.TotalSeconds:F3}" );
			Console.WriteLine( $"headless : drew {frames} frame(s) in {clock.Elapsed.TotalSeconds:F3}s (GPU completion included; not present/vsync FPS)" );
		}
		finally { mesh.ReleaseGpu( gl ); }
		return 0;
	}
}
