using System.Reflection;
using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class VolumeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }

		registry.Add( "volume", "density falls off from a sphere volume's centre and multiplicative media clear it", () =>
		{
			var grid = new VolumeGrid();
			grid.Volumes.Add( new FogVolume { Shape = VolumeShape.Sphere, Centre = Vector3.Zero, Size = new Vector3( 10 ), Falloff = 1f, Density = 1f } );
			float c = grid.DensityAt( Vector3.Zero ), m = grid.DensityAt( new Vector3( 5, 0, 0 ) ), o = grid.DensityAt( new Vector3( 11, 0, 0 ) );
			Check( c > m && m > 0 && o == 0, $"falloff {c} {m} {o}" );
			grid.Volumes.Add( new FogVolume { Shape = VolumeShape.Box, Blend = VolumeBlend.Multiplicative, Size = new Vector3( 2 ), Falloff = 0, Density = 0 } );
			Check( grid.DensityAt( Vector3.Zero ) == 0 && !grid.IsEmpty, "multiplicative clear" );
			Check( new VolumeGrid().IsEmpty && VolumeGrid.GlobalFog( 0, Vector3.Zero, 50 ).IsEmpty, "empty grids must be no-ops" );
		} );

		registry.Add( "volume", "hardware EGL: a fog sphere scatters the key light, shadowed fog is darker, empty grid draws nothing", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			const int N = 64;
			using var hdr = new RenderTarget( gl, InternalFormat.Rgba16f, depth: true );
			using var shadow = new RenderTarget( gl, null, depth: true );
			hdr.Ensure( N, N ); shadow.Ensure( N, N );
			using var pass = new VolumePass( gl );
			var camera = Matrix4x4.CreateLookAt( new Vector3( 0, -100, 0 ), Vector3.Zero, Vector3.UnitZ );
			var proj = Matrix4x4.CreatePerspectiveFieldOfView( 0.8f, 1, 1, 1000 );
			var light = Matrix4x4.CreateLookAt( new Vector3( 0, 0, 100 ), Vector3.Zero, Vector3.UnitY ) * Matrix4x4.CreateOrthographic( 100, 100, 1, 200 );
			float[] Render( VolumeGrid grid, float shadowDepth )
			{
				gl.BindFramebuffer( FramebufferTarget.Framebuffer, shadow.Framebuffer );
				gl.ClearDepth( shadowDepth ); gl.Clear( ClearBufferMask.DepthBufferBit ); gl.ClearDepth( 1 );
				gl.BindFramebuffer( FramebufferTarget.Framebuffer, hdr.Framebuffer );
				gl.ClearColor( 0.1f, 0.1f, 0.1f, 1 ); gl.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit );
				pass.Apply( grid, hdr.Framebuffer, hdr.DepthTexture, shadow.DepthTexture, light, camera * proj, new Vector3( 0, -100, 0 ),
					Vector3.UnitZ, new Vector3( 3 ), 1, N, N );
				var px = new float[N * N * 4];
				gl.BindFramebuffer( FramebufferTarget.Framebuffer, hdr.Framebuffer );
				gl.ReadPixels<float>( 0, 0, N, N, PixelFormat.Rgba, PixelType.Float, px );
				Check( gl.GetError() == GLEnum.NoError, "GL error in volume pass" );
				return px;
			}
			var fog = new VolumeGrid { ReferenceRadius = 50 };
			fog.Volumes.Add( new FogVolume { Shape = VolumeShape.Sphere, Size = new Vector3( 40 ), Density = 0.05f, Falloff = 0.5f } );
			var empty = Render( new VolumeGrid(), 1 );
			Check( !pass.LastApplied && empty.All( v => MathF.Abs( v - 0.1f ) < 1e-3f || v == 1f ), "empty grid changed pixels" );
			var lit = Render( fog, 1 );
			Check( pass.LastApplied && !pass.UsingFallback, "ray march path not used" );
			var shadowed = Render( fog, 0 );
			int c = (N / 2 * N + N / 2) * 4, edge = (N / 2 * N + 1) * 4;
			Console.WriteLine( $"  centre lit {lit[c]:F3} shadowed {shadowed[c]:F3} edge {lit[edge]:F3}" );
			Check( lit[c] > 0.1f + 0.05f, "fog did not scatter the key light" );
			Check( shadowed[c] < lit[c] * 0.7f, "shadow map did not occlude volumetric light" );
			Check( lit[c] > lit[edge], "density does not fall off away from the volume centre" );
		} );
		registry.Add( "volume", "hardware EGL through SceneRenderer: fog, emission, density texture and fallback change the frame; empty grid is identical", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			const int N = 96;
			using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0 };
			using var target = new CaptureTarget( gl, N, N );
			var camera = new OrbitCamera { Distance = 5, YawDegrees = 0, PitchDegrees = 0, Near = .1f, Far = 40 };
			var f = new HeadlessImageChecks.Fixture();
			f.Quad( "wall", new Vector3( -2, 0, 0 ), new Vector3( 0, 1.5f, 0 ), new Vector3( 0, 0, 1.5f ), Vector3.UnitX, new MaterialPalette.Entry( "wall", 0, 0, .5f, 1, 0, .4f, .4f, .4f ) );
			using var mesh = f.Mesh(); mesh.Upload( gl );
			var pass = (VolumePass)typeof( SceneRenderer ).GetField( "_volume", BindingFlags.Instance | BindingFlags.NonPublic )!.GetValue( renderer )!;
			try
			{
				byte[] Draw( VolumeGrid grid )
				{
					renderer.Volumes = grid; renderer.Draw( mesh, null, camera, N, N, 1, target.Handle );
					gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, target.Handle );
					var px = new byte[N * N * 4]; gl.ReadPixels<byte>( 0, 0, N, N, PixelFormat.Rgba, PixelType.UnsignedByte, px.AsSpan() );
					Check( gl.GetError() == GLEnum.NoError, "GL error in renderer volume fixture" );
					return px;
				}
				double Mean( byte[] px, int ch ) { double t = 0; for ( int i = ch; i < px.Length; i += 4 ) t += px[i]; return t / (px.Length / 4); }
				Draw( null ); var none = Draw( null );
				var empty = Draw( new VolumeGrid() );
				Check( none.SequenceEqual( empty ), "empty volume grid changed renderer pixels" );
				typeof( VolumePass ).GetProperty( "LastApplied" )!.SetValue( pass, false ); Draw( new VolumeGrid() );
				Check( !pass.LastApplied, "empty grid still dispatched the volume pass" );
				FogVolume Sphere() => new() { Shape = VolumeShape.Sphere, Size = new Vector3( 2.5f ), Density = 0.6f, Falloff = 0.5f };
				var fogGrid = new VolumeGrid { ReferenceRadius = 2 }; fogGrid.Volumes.Add( Sphere() );
				var fog = Draw( fogGrid );
				Check( pass.LastApplied && !pass.UsingFallback, "renderer did not ray-march the fog volume" );
				var emitGrid = new VolumeGrid { ReferenceRadius = 2, KeyIntensity = 0, Ambient = Vector3.Zero };
				var emitter = Sphere(); emitter.Scattering = Vector3.Zero; emitter.Emission = new Vector3( 0, 4, 0 ); emitGrid.Volumes.Add( emitter );
				var emit = Draw( emitGrid );
				var texGrid = new VolumeGrid { ReferenceRadius = 2 }; var textured = Sphere();
				var vals = new float[8 * 8 * 8]; for ( int i = 0; i < vals.Length; i++ ) vals[i] = (i % 8) < 4 ? 1 : 0;
				textured.Texture = new DensityTexture( 8, 8, 8, vals ); texGrid.Volumes.Add( textured );
				var tex = Draw( texGrid );
				var fallbackGrid = VolumeGrid.GlobalFog( 2, Vector3.Zero, 2 ); fallbackGrid.ForceFallback = true;
				var fallback = Draw( fallbackGrid );
				Check( pass.UsingFallback, "fallback path was not taken" );
				double dFog = Mean( fog, 1 ) - Mean( none, 1 ), dEmitG = Mean( emit, 1 ) - Mean( none, 1 ), dEmitR = Mean( emit, 0 ) - Mean( none, 0 );
				Console.WriteLine( $"  none G {Mean( none, 1 ):F1} fog {dFog:+0.0} emitG {dEmitG:+0.0} emitR {dEmitR:+0.0} texture-vs-fog {!tex.SequenceEqual( fog )} fallback-vs-none {!fallback.SequenceEqual( none )}" );
				Check( dFog > 2, "fog volume did not brighten the frame through SceneRenderer" );
				Check( dEmitG > 2 && dEmitG > dEmitR + 1, "green emission did not add green light" );
				Check( !tex.SequenceEqual( fog ), "density texture had no effect" );
				Check( !fallback.SequenceEqual( none ), "fallback height fog changed no pixels" );
			}
			finally { mesh.ReleaseGpu( gl ); }
		} );
	}
}
