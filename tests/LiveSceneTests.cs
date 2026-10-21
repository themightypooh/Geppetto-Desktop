using System.Numerics;
using System.Reflection;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

/// <summary>R9: live scene features through the shared SceneRenderer — the modifier stack evaluated
/// before upload, particle count/order, shadows cast by live layers and live-layer velocity.</summary>
internal static class LiveSceneTests
{
	const int Size = 128;
	static readonly Vec2[] Uvs = { new( 0, 0 ), new( 1, 0 ), new( 1, 1 ), new( 0, 1 ) };

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		// Camera on the -y side looking +y at the origin, z up.
		static OrbitCamera Camera( float pitch = 0 ) => new() { Target = Vector3.Zero, Distance = 6, YawDegrees = -90, PitchDegrees = pitch, Near = .1f, Far = 60 };
		static PolyMesh Cube( float h )
		{
			var m = new PolyMesh();
			for ( int i = 0; i < 8; i++ ) m.AddVertex( new Vec3( ( i & 1 ) == 0 ? -h : h, ( i & 2 ) == 0 ? -h : h, ( i & 4 ) == 0 ? -h : h ) );
			foreach ( var f in new[] { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } } ) m.AddFace( f );
			return m;
		}
		static byte[] Draw( GL gl, SceneRenderer r, ViewportMesh mesh, OrbitCamera cam, CaptureTarget target )
		{
			r.Draw( mesh, null, cam, Size, Size, 1, target.Handle );
			gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, target.Handle );
			var px = new byte[Size * Size * 4];
			gl.ReadPixels<byte>( 0, 0, Size, Size, PixelFormat.Rgba, PixelType.UnsignedByte, px.AsSpan() );
			Check( gl.GetError() == GLEnum.NoError, "GL error in live scene fixture" );
			return px;
		}
		static (int X, int Y) Pixel( OrbitCamera c, Vector3 world )
		{
			var p = Vector4.Transform( new Vector4( world, 1 ), c.ViewMatrix() * c.ProjectionMatrix( 1 ) );
			return ( (int)( ( p.X / p.W * .5f + .5f ) * Size ), (int)( ( p.Y / p.W * .5f + .5f ) * Size ) );
		}
		static bool Differs( byte[] a, byte[] b, int x, int y ) { int i = ( y * Size + x ) * 4; return Math.Abs( a[i] - b[i] ) + Math.Abs( a[i + 1] - b[i + 1] ) + Math.Abs( a[i + 2] - b[i + 2] ) > 6; }

		registry.Add( "live-scene", "hardware EGL: a live displace modifier grows the silhouette only inside the displaced bounds", () =>
		{
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var target = new CaptureTarget( gl, Size, Size );
			using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0 };
			var cam = Camera( 20 );
			using var plain = ViewportMesh.FromPolyMesh( Cube( .8f ), _ => "cube" );
			var stack = new ModifierStack();
			stack.Modifiers.Add( new SubdivisionModifier { Levels = 2 } );
			stack.Modifiers.Add( new DisplaceModifier { Distance = .4f } );
			using var modified = stack.Apply( plain );
			Check( !ReferenceEquals( modified, plain ) && modified.Vertices.Length > plain.Vertices.Length, $"modifier stack did not produce new geometry: {stack.LastSummary}" );
			stack.Modifiers.ForEach( m => m.Enabled = false );
			Check( ReferenceEquals( stack.Apply( plain ), plain ), "disabled stack must return the source mesh" );
			plain.Upload( gl ); modified.Upload( gl );
			try
			{
				var a = Draw( gl, renderer, plain, cam, target ); var b = Draw( gl, renderer, modified, cam, target );
				// Every displaced vertex lies within |p| <= sqrt(3)*.8 + .4; project that sphere's bounds.
				float reach = MathF.Sqrt( 3 ) * .8f + .45f;
				int minX = Size, maxX = 0, minY = Size, maxY = 0;
				foreach ( var corner in new[] { -1, 1 } )
					foreach ( var cy in new[] { -1, 1 } )
						foreach ( var cz in new[] { -1, 1 } )
						{
							var (x, y) = Pixel( cam, new Vector3( corner, cy, cz ) * reach );
							minX = Math.Min( minX, x ); maxX = Math.Max( maxX, x ); minY = Math.Min( minY, y ); maxY = Math.Max( maxY, y );
						}
				int inside = 0, outside = 0;
				for ( int y = 0; y < Size; y++ )
					for ( int x = 0; x < Size; x++ )
						if ( Differs( a, b, x, y ) ) { if ( x >= minX - 1 && x <= maxX + 1 && y >= minY - 1 && y <= maxY + 1 ) inside++; else outside++; }
				// A pixel just outside the original cube's right edge at the centre row is now covered.
				var (ex, ey) = Pixel( cam, new Vector3( .8f + .2f, 0, 0 ) );
				Console.WriteLine( $"  {stack.LastSummary}; changed pixels inside bounds {inside}, outside {outside}; probe ({ex},{ey}) changed {Differs( a, b, ex, ey )}" );
				Check( inside > 200, $"displaced mesh barely changed the capture ({inside} px)" );
				Check( outside == 0, $"{outside} pixels changed outside the displaced bounds" );
				Check( Differs( a, b, ex, ey ), "silhouette did not grow past the original edge" );
			}
			finally { plain.ReleaseGpu( gl ); modified.ReleaseGpu( gl ); }
		} );

		registry.Add( "live-scene", "particles: all emitted particles draw and billboards are emitted back-to-front", () =>
		{
			var particles = new ParticleSystem( new ParticleSystemSettings { Gravity = Vector3.Zero, Capacity = 64 } );
			var rng = new Random( 7 );
			for ( int i = 0; i < 20; i++ )
				particles.AddEmitter( new ParticleEmitter { Position = new Vector3( rng.NextSingle() - .5f, rng.NextSingle() * 6 - 3, rng.NextSingle() - .5f ), LifetimeMin = 10, LifetimeMax = 10, SizeMin = .1f, SizeMax = .1f, Bursts = { new ParticleBurst( 0, 1 ) } } );
			particles.Step();
			var layer = new ParticleLayer( particles );
			var eye = new Vector3( 0, -6, 0 );
			var view = new LiveView( eye, Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY );
			var builder = new LiveBuilder();
			layer.Emit( builder, view );
			Check( layer.LastDrawn == 20 && builder.VertexCount == 20 * 6, $"drew {layer.LastDrawn} particles, {builder.VertexCount} vertices" );
			var data = (float[])typeof( LiveBuilder ).GetProperty( "Data", BindingFlags.Instance | BindingFlags.NonPublic )!.GetValue( builder )!;
			float previous = float.PositiveInfinity;
			for ( int q = 0; q < 20; q++ )
			{
				float depth = 0;
				for ( int v = 0; v < 6; v++ ) depth += data[( q * 6 + v ) * ViewportMesh.FloatsPerVertex + 1] - eye.Y;
				depth /= 6;
				Check( depth <= previous + 1e-4f, $"particle {q} at depth {depth} drawn after nearer {previous}" );
				previous = depth;
			}
		} );

		registry.Add( "live-scene", "hardware EGL: an opaque live cube casts a key-light shadow on the mesh floor and blurs along its motion", () =>
		{
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var target = new CaptureTarget( gl, Size, Size );
			using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0, KeyDirection = Vector3.Normalize( new Vector3( 0, 0, 1 ) ) };
			var floor = new PolyMesh();
			floor.Positions.AddRange( new[] { new Vec3( -4, -4, 0 ), new Vec3( 4, -4, 0 ), new Vec3( 4, 4, 0 ), new Vec3( -4, 4, 0 ) } );
			floor.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
			using var mesh = ViewportMesh.FromPolyMesh( floor, _ => "floor" );
			mesh.Upload( gl );
			var cam = new OrbitCamera { Target = Vector3.Zero, Distance = 8, YawDegrees = -90, PitchDegrees = 60, Near = .1f, Far = 60 };
			var moving = new MovingCube( Cube( .5f ) ) { Material = new LiveMaterial( new Vector3( .8f, .1f, .1f ) ) };
			moving.Offset = new Vector3( 0, 0, 1.5f );
			renderer.Live.Layers.Add( moving );
			try
			{
				// Floor point directly below the cube, outside the cube's own screen footprint.
				var shadowPoint = new Vector3( 0, -.2f, 0 );
				var withCube = Draw( gl, renderer, mesh, cam, target );
				moving.Visible = false; var without = Draw( gl, renderer, mesh, cam, target ); moving.Visible = true;
				var (sx, sy) = Pixel( cam, new Vector3( 0, 0, 0 ) );
				int i = ( sy * Size + sx ) * 4;
				// The cube hides the floor centre from this camera, so sample beside its footprint.
				var cubeTop = Pixel( cam, new Vector3( 0, 0, 2f ) );
				int lit = 0, dark = 0;
				var ys = new List<int>();
				for ( int y = 0; y < Size; y++ ) for ( int x = 0; x < Size; x++ )
				{
					int k = ( y * Size + x ) * 4;
					int dw = withCube[k] + withCube[k + 1] + withCube[k + 2], dn = without[k] + without[k + 1] + without[k + 2];
					// Shadowed floor: darker and still grey (not the red cube).
					if ( dn - dw > 40 && Math.Abs( withCube[k] - withCube[k + 1] ) < 15 ) dark++;
					if ( dw - dn > 40 ) lit++;
				}
				Console.WriteLine( $"  shadowed floor pixels {dark}, brightened {lit}" );
				Check( dark > 40, $"live cube cast no shadow on the floor ({dark} px)" );
				Check( lit == 0, $"live cube brightened {lit} floor pixels" );

				// Motion: move the cube sideways between frames; motion blur must smear red past
				// the sharp silhouette along x, more than along y.
				int RedWidth( byte[] px, bool horizontal )
				{
					var (cx, cy) = Pixel( cam, moving.Offset );
					int n = 0;
					for ( int t = 0; t < Size; t++ )
					{
						int x = horizontal ? t : cx, y = horizontal ? cy : t, k = ( y * Size + x ) * 4;
						if ( px[k] > px[k + 1] + 12 ) n++;
					}
					return n;
				}
				renderer.MotionBlurShutter = 0;
				moving.Offset = new Vector3( -1.2f, 0, 1.5f ); Draw( gl, renderer, mesh, cam, target );
				moving.Offset = new Vector3( 0, 0, 1.5f ); var sharp = Draw( gl, renderer, mesh, cam, target );
				renderer.MotionBlurShutter = 1;
				moving.Offset = new Vector3( -1.2f, 0, 1.5f ); Draw( gl, renderer, mesh, cam, target );
				moving.Offset = new Vector3( 0, 0, 1.5f ); var blurred = Draw( gl, renderer, mesh, cam, target );
				// Changed pixels between the sharp and blurred frames: many, and spread wider in
				// x (the motion) than in y.
				int diff = 0, x0 = Size, x1 = -1, y0 = Size, y1 = -1;
				for ( int y = 0; y < Size; y++ ) for ( int x = 0; x < Size; x++ )
					if ( Differs( sharp, blurred, x, y ) ) { diff++; x0 = Math.Min( x0, x ); x1 = Math.Max( x1, x ); y0 = Math.Min( y0, y ); y1 = Math.Max( y1, y ); }
				int sw = RedWidth( sharp, true ), bw = RedWidth( blurred, true );
				Console.WriteLine( $"  blur changed {diff} px over x {x0}..{x1}, y {y0}..{y1}; red row width {sw} -> {bw}" );
				Check( diff > 100, $"live layer motion produced no blur ({diff} px)" );
				Check( x1 - x0 > ( y1 - y0 ) + 4, $"live blur not along the motion (x {x0}..{x1}, y {y0}..{y1})" );
				// Same frames with a static cube: the blur must vanish (no stale live velocity).
				renderer.MotionBlurShutter = 0; var still = Draw( gl, renderer, mesh, cam, target );
				renderer.MotionBlurShutter = 1; var stillBlur = Draw( gl, renderer, mesh, cam, target );
				int stale = 0; for ( int y = 0; y < Size; y++ ) for ( int x = 0; x < Size; x++ ) if ( Differs( still, stillBlur, x, y ) ) stale++;
				Check( stale == 0, $"static live cube still blurred ({stale} px)" );
			}
			finally { mesh.ReleaseGpu( gl ); }
		} );
	}

	/// <summary>An opaque cube translated by <see cref="Offset"/> each frame (dynamic, so it gets velocity).</summary>
	sealed class MovingCube : LiveLayer
	{
		readonly PolyMesh _cube;
		public Vector3 Offset;
		public MovingCube( PolyMesh cube ) => _cube = cube;
		public override void Emit( LiveBuilder b, in LiveView view )
		{
			foreach ( var f in _cube.Faces )
			{
				var p = new Vector3[4];
				for ( int i = 0; i < 4; i++ ) { var v = _cube.Positions[f.Indices[i]]; p[i] = new Vector3( v.x, v.y, v.z ) + Offset; }
				var n = LiveBuilder.SafeNormalize( Vector3.Cross( p[1] - p[0], p[2] - p[0] ), Vector3.UnitZ );
				b.Quad( p[0], p[1], p[2], p[3], n );
			}
		}
	}
}
