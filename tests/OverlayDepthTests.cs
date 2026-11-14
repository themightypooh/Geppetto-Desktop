using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

/// <summary>The floor grid (SkeletonOverlay) is drawn after SceneRenderer into the caller's target
/// and must depth-test against the model, whatever that target is: the serve/headless
/// single-sample CaptureTarget, and a 4x multisampled target like the interactive window's
/// default framebuffer, where a depth blit from the single-sample scene target is not legal.</summary>
internal static class OverlayDepthTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "overlay", "floor grid behind the model is hidden, in front of nothing it shows (single-sample and MSAA targets)", () =>
		{
			const int w = 256, h = 192;
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			using var renderer = new SceneRenderer( gl ) { AntiAliasing = AntiAliasingMode.None, SsaoStrength = 0, SsrStrength = 0 };
			using var overlay = new SkeletonOverlay( gl ) { ShowBones = false };
			using var readback = new CaptureTarget( gl, w, h );
			// A wall standing above the floor, facing the camera (+x). Nothing of it is below z=0,
			// so no floor line is legitimately in front of it.
			using var mesh = Wall( new Vector3( 0, -30, 5 ), new Vector3( 0, 30, 5 ), new Vector3( 0, 30, 45 ), new Vector3( 0, -30, 45 ) );
			var camera = new OrbitCamera { Target = new Vector3( 0, 0, 22 ), Distance = 150, YawDegrees = 0, PitchDegrees = 20 };
			var viewProj = camera.ViewMatrix() * camera.ProjectionMatrix( w / (float)h );
			// Fixture: the floor point (-40,0,0) is on two grid lines and seen THROUGH the wall.
			var behind = new Vector3( -40, 0, 0 );
			var eye = camera.Eye;
			float t = eye.X / (eye.X - behind.X);
			var through = eye + (behind - eye) * t;
			Check( through.Y is > -30 and < 30 && through.Z is > 5 and < 45, "fixture: the floor point is not behind the wall" );
			// ... and (60,0,0) is on the red axis line in front of the wall, with nothing over it.
			var open = new Vector3( 60, 0, 0 );
			mesh.Upload( gl );
			try
			{
				foreach ( var samples in new[] { 0, 4 } )
				{
					uint fbo = 0, color = 0, depth = 0;
					if ( samples > 0 )
					{
						fbo = gl.GenFramebuffer(); color = gl.GenRenderbuffer(); depth = gl.GenRenderbuffer();
						gl.BindFramebuffer( FramebufferTarget.Framebuffer, fbo );
						gl.BindRenderbuffer( RenderbufferTarget.Renderbuffer, color );
						gl.RenderbufferStorageMultisample( RenderbufferTarget.Renderbuffer, 4, InternalFormat.Rgba8, w, h );
						gl.FramebufferRenderbuffer( FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, color );
						gl.BindRenderbuffer( RenderbufferTarget.Renderbuffer, depth );
						gl.RenderbufferStorageMultisample( RenderbufferTarget.Renderbuffer, 4, InternalFormat.DepthComponent24, w, h );
						gl.FramebufferRenderbuffer( FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth );
						Check( gl.CheckFramebufferStatus( FramebufferTarget.Framebuffer ) == GLEnum.FramebufferComplete, "MSAA fixture target incomplete" );
					}
					uint target = samples > 0 ? fbo : readback.Handle;
					byte[] Frame( bool grid )
					{
						overlay.ShowGrid = grid;
						gl.BindFramebuffer( FramebufferTarget.Framebuffer, target );
						gl.Viewport( 0, 0, w, h );
						renderer.Draw( mesh, null!, camera, w, h, 1, target );
						overlay.Draw( viewProj, null!, ReadOnlySpan<Effigy.Xform>.Empty, Array.Empty<CpuMesh.BodyPickEntry>() );
						if ( samples > 0 )
						{
							gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, fbo );
							gl.BindFramebuffer( FramebufferTarget.DrawFramebuffer, readback.Handle );
							gl.BlitFramebuffer( 0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest );
						}
						gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, readback.Handle );
						gl.PixelStore( PixelStoreParameter.PackAlignment, 1 );
						var rgba = new byte[w * h * 4];
						gl.ReadPixels<byte>( 0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan() );
						Check( gl.GetError() == GLEnum.NoError, $"GL error drawing the grid ({samples}x)" );
						return rgba;
					}
					try
					{
						var plain = Frame( false );
						var gridded = Frame( true );
						var upright = new byte[gridded.Length];        // GL rows run bottom-up
						for ( int y = 0; y < h; y++ ) Array.Copy( gridded, (h - 1 - y) * w * 4, upright, y * w * 4, w * 4 );
						Effigy.PngWriter.WriteFileRgba( Path.Combine( settings.WorkDirectory, $"overlay-grid-{samples}x.png" ), upright, w, h );
						int Diff( Vector3 world )
						{
							var p = Project( viewProj, world, w, h );
							int worst = 0;
							for ( int y = (int)p.Y - 1; y <= (int)p.Y + 1; y++ )
							for ( int x = (int)p.X - 1; x <= (int)p.X + 1; x++ )
								for ( int c = 0; c < 3; c++ )
									worst = Math.Max( worst, Math.Abs( plain[(y * w + x) * 4 + c] - gridded[(y * w + x) * 4 + c] ) );
							return worst;
						}
						int open_ = Diff( open ), behind_ = Diff( behind );
						Check( open_ > 20, $"{samples}x: the grid is not drawn at all where nothing covers it (diff {open_})" );
						Check( behind_ <= 2, $"{samples}x: the grid shows through the wall (diff {behind_})" );
					}
					finally
					{
						if ( fbo != 0 ) { gl.BindFramebuffer( FramebufferTarget.Framebuffer, 0 ); gl.DeleteFramebuffer( fbo ); gl.DeleteRenderbuffer( color ); gl.DeleteRenderbuffer( depth ); }
					}
				}
			}
			finally { mesh.ReleaseGpu( gl ); }
		} );
	}

	static ViewportMesh Wall( Vector3 a, Vector3 b, Vector3 c, Vector3 d )
	{
		var vertices = new List<float>();
		foreach ( var p in new[] { a, b, c, d } ) vertices.AddRange( new[] { p.X, p.Y, p.Z, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1f, 1, 1, 1, 1 } ); // 24 floats/vertex (CpuMesh.FloatsPerVertex): white colour appended
		var material = new MaterialPalette.Entry( "wall", 0.6f, 0.6f, 0.6f );
		var data = vertices.ToArray();
		return new ViewportMesh
		{
			Vertices = data, BaseVertices = (float[])data.Clone(), Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
			Batches = new[] { new CpuMesh.DrawBatch( "wall", material, 0, 6 ) }, Centre = new Vector3( 0, 0, 25 ), Radius = 40, TriangleCount = 2,
		};
	}

	static Vector2 Project( Matrix4x4 viewProj, Vector3 p, int w, int h )
	{
		var v = Vector4.Transform( new Vector4( p, 1 ), viewProj );
		return new Vector2( (v.X / v.W * .5f + .5f) * w, (v.Y / v.W * .5f + .5f) * h );
	}

	static void Check( bool condition, string reason ) { if ( !condition ) throw new Exception( reason ); }
}
