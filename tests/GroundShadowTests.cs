using System.Numerics;
using System.Reflection;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

/// <summary>Ground shadow catcher: the invisible z = 0 floor multiplies the lit HDR target by
/// key-light visibility and SSAO. Each probe compares the catcher on against the same frame off,
/// in regions computed from the fixture geometry and <see cref="KeyShadowMap.Direction"/>.</summary>
internal static class GroundShadowTests
{
	const int Size = 160;
	static readonly Vec2[] Uvs = { new( 0, 0 ), new( 1, 0 ), new( 1, 1 ), new( 0, 1 ) };

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		string root = Path.Combine( settings.WorkDirectory, "ground-shadow" );
		Directory.CreateDirectory( root );
		var materials = new MaterialResolver( root, "ground-fixture" );
		materials.SetOverride( "floor", new MaterialDescriptor( "floor" ) { BaseColorFactor = new Vec4( .7f, .7f, .7f, 1 ), RoughnessFactor = .8f }, required: true );
		materials.SetOverride( "box", new MaterialDescriptor( "box" ) { BaseColorFactor = new Vec4( .7f, .7f, .7f, 1 ), RoughnessFactor = .8f }, required: true );

		// Box x,y in [-.5,.5], z in [0,2] (slot 1, no bottom face), optional floor quad at z = 0
		// over [-4,4] (slot 0). Faces wound counter-clockwise seen from outside.
		static PolyMesh Scene( bool floor )
		{
			var m = new PolyMesh();
			m.Positions.AddRange( new[] { new Vec3( -.5f, -.5f, 0 ), new Vec3( .5f, -.5f, 0 ), new Vec3( .5f, .5f, 0 ), new Vec3( -.5f, .5f, 0 ),
				new Vec3( -.5f, -.5f, 2 ), new Vec3( .5f, -.5f, 2 ), new Vec3( .5f, .5f, 2 ), new Vec3( -.5f, .5f, 2 ) } );
			foreach ( var f in new[] { new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 } } )
				m.AddFace( f, Uvs, 1 );
			if ( floor )
			{
				int b = m.Positions.Count;
				m.Positions.AddRange( new[] { new Vec3( -4, -4, 0 ), new Vec3( 4, -4, 0 ), new Vec3( 4, 4, 0 ), new Vec3( -4, 4, 0 ) } );
				m.AddFace( new[] { b, b + 1, b + 2, b + 3 }, Uvs, 0 );
			}
			return m;
		}
		static string Slot( int s ) => s == 0 ? "floor" : "box";
		// Camera on the -x,+y side: the side the key light (+x,-y,+z) casts the box shadow onto.
		static OrbitCamera Camera() => new() { Target = new Vector3( -.5f, .5f, .5f ), Distance = 8, YawDegrees = 120, PitchDegrees = 45, Near = .1f, Far = 60 };
		static (int X, int Y) Pixel( OrbitCamera c, Vector3 world )
		{
			var p = Vector4.Transform( new Vector4( world, 1 ), c.ViewMatrix() * c.ProjectionMatrix( 1 ) );
			return ( (int)( ( p.X / p.W * .5f + .5f ) * Size ), (int)( ( p.Y / p.W * .5f + .5f ) * Size ) );
		}
		static float Luma( float[] px, int i ) => .2126f * px[i] + .7152f * px[i + 1] + .0722f * px[i + 2];
		static float Box( float[] px, OrbitCamera c, Vector3 a, Vector3 b )
		{
			var (x0, y0) = Pixel( c, a ); var (x1, y1) = Pixel( c, b );
			if ( x0 > x1 ) (x0, x1) = (x1, x0); if ( y0 > y1 ) (y0, y1) = (y1, y0);
			double sum = 0; int n = 0;
			for ( int y = Math.Max( 0, y0 ); y <= Math.Min( Size - 1, y1 ); y++ )
				for ( int x = Math.Max( 0, x0 ); x <= Math.Min( Size - 1, x1 ); x++ ) { sum += Luma( px, ( y * Size + x ) * 4 ); n++; }
			Check( n > 0, "empty probe box" );
			return (float)( sum / n );
		}
		static float[] Render( GL gl, SceneRenderer r, CaptureTarget target, PolyMesh source, MaterialResolver materials )
		{
			using var mesh = ViewportMesh.FromPolyMesh( source, Slot );
			mesh.ApplyMaterials( materials );
			mesh.Upload( gl );
			try
			{
				r.ResetTemporalHistory();
				r.Draw( mesh, null, Camera(), Size, Size, 1, target.Handle );
				Check( gl.GetError() == GLEnum.NoError, "GL error drawing the ground fixture" );
				var px = new float[Size * Size * 4];
				gl.Finish();
				gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, r.PostChainTargets.ColorFbo );
				gl.ReadPixels<float>( 0, 0, Size, Size, PixelFormat.Rgba, PixelType.Float, px.AsSpan() );
				Check( gl.GetError() == GLEnum.NoError, "GL error reading the HDR target" );
				for ( int i = 0; i < px.Length; i++ ) Check( float.IsFinite( px[i] ), $"non-finite HDR value at texel {i / 4}" );
				return px;
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		// Floor under the box's mid-height silhouette, offset along -Direction (outside the box
		// footprint); a strip hugging the box's -x base; open floor far from the box.
		Vector3 sA = new( -.9f, .65f, 0 ), sB = new( -.6f, 1f, 0 );
		Vector3 cA = new( -.6f, -.3f, 0 ), cB = new( -.53f, .3f, 0 );
		Vector3 oA = new( -3.5f, -1.5f, 0 ), oB = new( -2.5f, -.5f, 0 );

		registry.Add( "ground", "hardware EGL: the floor catcher darkens the key shadow and base contact, and leaves open floor unchanged", () =>
		{
			var dir = KeyShadowMap.Direction;
			var mid = new Vector3( -.75f, .825f, 0 ) + dir * ( .5f / dir.Z );
			Check( MathF.Abs( mid.X ) < .5f && MathF.Abs( mid.Y ) < .5f, $"probe is not in the box's key shadow ({mid})" );
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var target = new CaptureTarget( gl, Size, Size );
			using var renderer = new SceneRenderer( gl ) { SsrStrength = 0 };
			var cam = Camera();
			float[] Frame( bool on, float shadow, float ao )
			{
				renderer.GroundShadow = on; renderer.Ground.ShadowStrength = shadow; renderer.Ground.AoStrength = ao;
				return Render( gl, renderer, target, Scene( false ), materials );
			}
			var off = Frame( false, 0, 0 ); var shadowOnly = Frame( true, .6f, 0 ); var aoOnly = Frame( true, 0, .85f ); var both = Frame( true, .6f, .85f );
			var shade = ( on: Box( shadowOnly, cam, sA, sB ), off: Box( off, cam, sA, sB ) );
			var contact = ( on: Box( aoOnly, cam, cA, cB ), off: Box( off, cam, cA, cB ) );
			var open = ( on: Box( both, cam, oA, oB ), off: Box( off, cam, oA, oB ) );
			Console.WriteLine( $"  key shadow {shade.on:F4}/{shade.off:F4}, base contact {contact.on:F4}/{contact.off:F4}, open floor {open.on:F4}/{open.off:F4} (on/off)" );
			Check( shade.off > .01f && contact.off > .01f && open.off > .01f, "background too dark to judge" );
			Check( shade.on < shade.off * .85f, $"catcher did not darken the key shadow ({shade.on:F4} vs {shade.off:F4})" );
			Check( shade.on > shade.off * .35f, $"key shadow exceeds ShadowStrength 0.6 ({shade.on:F4} vs {shade.off:F4})" );
			Check( contact.on < contact.off * .97f, $"catcher did not add contact AO at the box base ({contact.on:F4} vs {contact.off:F4})" );
			Check( MathF.Abs( open.on - open.off ) <= open.off * 1e-4f, $"catcher changed open floor ({open.on:F4} vs {open.off:F4})" );
		} );

		registry.Add( "ground", "hardware EGL: a coplanar floor mesh at z = 0 is not darkened twice by the catcher", () =>
		{
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var target = new CaptureTarget( gl, Size, Size );
			using var renderer = new SceneRenderer( gl ) { SsrStrength = 0 };
			var cam = Camera();
			renderer.GroundShadow = false; var off = Render( gl, renderer, target, Scene( true ), materials );
			renderer.GroundShadow = true; var on = Render( gl, renderer, target, Scene( true ), materials );
			var shade = ( on: Box( on, cam, sA, sB ), off: Box( off, cam, sA, sB ) );
			var contact = ( on: Box( on, cam, cA, cB ), off: Box( off, cam, cA, cB ) );
			double sum = 0; float worst = 0;
			for ( int i = 0; i < on.Length; i += 4 )
			{
				float a = Luma( on, i ), b = Luma( off, i );
				float d = MathF.Abs( a - b ) / MathF.Max( b, 1e-3f ); sum += d; worst = MathF.Max( worst, d );
			}
			float mean = (float)( sum / ( Size * Size ) );
			Console.WriteLine( $"  key shadow {shade.on:F4}/{shade.off:F4}, base contact {contact.on:F4}/{contact.off:F4} (on/off); mean |diff| {mean:P3}, worst {worst:P2}" );
			Check( shade.off < Box( off, cam, oA, oB ) * .9f, "floor mesh shows no key shadow of its own; fixture is wrong" );
			Check( MathF.Abs( shade.on - shade.off ) < shade.off * .01f, $"catcher darkened the floor mesh's key shadow again ({shade.on:F4} vs {shade.off:F4})" );
			Check( MathF.Abs( contact.on - contact.off ) < contact.off * .01f, $"catcher darkened the floor mesh's contact again ({contact.on:F4} vs {contact.off:F4})" );
			Check( mean < .002f, $"catcher changed a floor-mesh frame by {mean:P3} on average" );
		} );

		registry.Add( "ground", "hardware EGL: with GroundShadow off the renderer never creates the catcher pass", () =>
		{
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var target = new CaptureTarget( gl, Size, Size );
			using var renderer = new SceneRenderer( gl ) { SsrStrength = 0 };
			Render( gl, renderer, target, Scene( false ), materials );
			var field = typeof( SceneRenderer ).GetField( "_ground", BindingFlags.NonPublic | BindingFlags.Instance );
			Check( field is not null, "SceneRenderer._ground field not found" );
			Check( field!.GetValue( renderer ) is null, "GroundShadowPass was created while GroundShadow is off" );
		} );
	}
}
