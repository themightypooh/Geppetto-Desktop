using System.Numerics;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

/// <summary>R5: screen-space AO and SSR, measured in the linear HDR target of the shared
/// SceneRenderer. Every probe compares the effect switched on against the same frame with it
/// switched off, in a region computed from the fixture geometry.</summary>
internal static class ScreenSpaceTests
{
	const int Size = 160;
	static readonly Vec2[] Uvs = { new( 0, 0 ), new( 1, 0 ), new( 1, 1 ), new( 0, 1 ) };

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		string root = Path.Combine( settings.WorkDirectory, "screen-space" );
		Directory.CreateDirectory( root );

		MaterialResolver Resolver( params MaterialDescriptor[] descriptors )
		{
			var resolver = new MaterialResolver( root, "r5-fixture" );
			foreach ( var d in descriptors ) resolver.SetOverride( d.Name, d, required: true );
			return resolver;
		}
		// Floor z=0 over x,y in [-4,4] (slot 0, "floor"); optional wall y=2 facing -y over
		// x in [-4,4], z in [0,4] (slot 1, "wall"). Both wound counter-clockwise toward the camera.
		static PolyMesh Scene( bool wall, float floorFarY = 4f )
		{
			var m = new PolyMesh();
			m.Positions.AddRange( new[] { new Vec3( -4, -4, 0 ), new Vec3( 4, -4, 0 ), new Vec3( 4, floorFarY, 0 ), new Vec3( -4, floorFarY, 0 ) } );
			m.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
			if ( wall )
			{
				m.Positions.AddRange( new[] { new Vec3( -4, 2, 0 ), new Vec3( 4, 2, 0 ), new Vec3( 4, 2, 4 ), new Vec3( -4, 2, 4 ) } );
				m.AddFace( new[] { 4, 5, 6, 7 }, Uvs, 1 );
			}
			return m;
		}
		static string Slot( int s ) => s == 0 ? "floor" : "wall";
		// Camera on the -y side looking at the corner, pitched down onto the floor.
		static OrbitCamera Camera() => new() { Target = new Vector3( 0, 1, .8f ), Distance = 9, YawDegrees = -90, PitchDegrees = 28, Near = .1f, Far = 60 };
		static (int X, int Y) Pixel( OrbitCamera c, Vector3 world )
		{
			var p = Vector4.Transform( new Vector4( world, 1 ), c.ViewMatrix() * c.ProjectionMatrix( 1 ) );
			return ( (int)( ( p.X / p.W * .5f + .5f ) * Size ), (int)( ( p.Y / p.W * .5f + .5f ) * Size ) );
		}
		// Linear HDR colour of the lit target (before tone mapping), bottom-left origin.
		static float[] Hdr( GL gl, SceneRenderer r )
		{
			var px = new float[Size * Size * 4];
			gl.Finish();
			gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, r.PostChainTargets.ColorFbo );
			gl.ReadPixels<float>( 0, 0, Size, Size, PixelFormat.Rgba, PixelType.Float, px.AsSpan() );
			Check( gl.GetError() == GLEnum.NoError, "GL error reading the HDR target" );
			for ( int i = 0; i < px.Length; i++ ) Check( float.IsFinite( px[i] ), $"non-finite HDR value at texel {i / 4}" );
			return px;
		}
		// The blurred AO term itself (R8: 1 = open), bottom-left origin.
		static float[] Ao( GL gl, SceneRenderer r )
		{
			var px = new byte[Size * Size];
			gl.Finish();
			gl.BindTexture( TextureTarget.Texture2D, r.Ssao.Texture );
			gl.PixelStore( PixelStoreParameter.PackAlignment, 1 );
			gl.GetTexImage<byte>( TextureTarget.Texture2D, 0, PixelFormat.Red, PixelType.UnsignedByte, px.AsSpan() );
			gl.PixelStore( PixelStoreParameter.PackAlignment, 4 );
			Check( gl.GetError() == GLEnum.NoError, "GL error reading the AO texture" );
			return Array.ConvertAll( px, b => b / 255f );
		}
		static float AoBox( float[] ao, OrbitCamera c, Vector3 a, Vector3 b )
		{
			var (x0, y0) = Pixel( c, a ); var (x1, y1) = Pixel( c, b );
			if ( x0 > x1 ) (x0, x1) = (x1, x0); if ( y0 > y1 ) (y0, y1) = (y1, y0);
			double sum = 0; int n = 0;
			for ( int y = Math.Max( 0, y0 ); y <= Math.Min( Size - 1, y1 ); y++ )
				for ( int x = Math.Max( 0, x0 ); x <= Math.Min( Size - 1, x1 ); x++ ) { sum += ao[y * Size + x]; n++; }
			Check( n > 0, "empty AO probe box" );
			return (float)( sum / n );
		}
		static float Luma( float[] px, int x, int y ) { int i = ( y * Size + x ) * 4; return .2126f * px[i] + .7152f * px[i + 1] + .0722f * px[i + 2]; }
		// Mean over a pixel box spanned by world points a..b (inclusive, clamped to the image).
		static (float Luma, float Red, float Green) Box( float[] px, OrbitCamera c, Vector3 a, Vector3 b )
		{
			var (x0, y0) = Pixel( c, a ); var (x1, y1) = Pixel( c, b );
			if ( x0 > x1 ) (x0, x1) = (x1, x0); if ( y0 > y1 ) (y0, y1) = (y1, y0);
			x0 = Math.Clamp( x0, 0, Size - 1 ); x1 = Math.Clamp( x1, 0, Size - 1 ); y0 = Math.Clamp( y0, 0, Size - 1 ); y1 = Math.Clamp( y1, 0, Size - 1 );
			double l = 0, r = 0, g = 0; int n = 0;
			for ( int y = y0; y <= y1; y++ )
				for ( int x = x0; x <= x1; x++ ) { int i = ( y * Size + x ) * 4; l += Luma( px, x, y ); r += px[i]; g += px[i + 1]; n++; }
			Check( n > 0, "empty probe box" );
			return ( (float)( l / n ), (float)( r / n ), (float)( g / n ) );
		}
		float[]? lastAo = null;
		float[] Render( GL gl, SceneRenderer r, CaptureTarget target, PolyMesh source, MaterialResolver materials, int frames )
		{
			using var mesh = ViewportMesh.FromPolyMesh( source, Slot );
			mesh.ApplyMaterials( materials );
			mesh.Upload( gl );
			try
			{
				r.ResetTemporalHistory();
				// SSR reads the previous frame's HDR colour, so the first frame has nothing to reflect.
				for ( int i = 0; i < frames; i++ ) r.Draw( mesh, null, Camera(), Size, Size, 1, target.Handle );
				Check( gl.GetError() == GLEnum.NoError, "GL error drawing the R5 fixture" );
				lastAo = r.SsaoStrength > 0 ? Ao( gl, r ) : null;
				return Hdr( gl, r );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		var matte = new MaterialDescriptor( "floor" ) { BaseColorFactor = new Vec4( .7f, .7f, .7f, 1 ), RoughnessFactor = .8f };
		var matteWall = new MaterialDescriptor( "wall" ) { BaseColorFactor = new Vec4( .7f, .7f, .7f, 1 ), RoughnessFactor = .8f };

		registry.Add( "ss", "hardware EGL: SSAO darkens the floor/wall corner and leaves an open flat floor unchanged", () =>
		{
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var target = new CaptureTarget( gl, Size, Size );
			using var renderer = new SceneRenderer( gl ) { SsrStrength = 0 };
			var cam = Camera();
			var materials = Resolver( matte, matteWall );
			float[] Frame( PolyMesh m, float strength ) { renderer.SsaoStrength = strength; return Render( gl, renderer, target, m, materials, 1 ); }
			var cornerOn = Frame( Scene( true ), .5f ); var cornerAo = lastAo!; var cornerOff = Frame( Scene( true ), 0 );
			var flatOn = Frame( Scene( false ), .5f ); var flatAo = lastAo!; var flatOff = Frame( Scene( false ), 0 );
			// Floor strip within ~0.25 units of the wall, and an open floor patch far from it.
			Vector3 cA = new( -1, 1.88f, 0 ), cB = new( 1, 1.97f, 0 );
			Vector3 oA = new( -1, -1.5f, 0 ), oB = new( 1, -.5f, 0 );
			var corner = ( on: Box( cornerOn, cam, cA, cB ).Luma, off: Box( cornerOff, cam, cA, cB ).Luma );
			var open = ( on: Box( cornerOn, cam, oA, oB ).Luma, off: Box( cornerOff, cam, oA, oB ).Luma );
			var flat = ( on: Box( flatOn, cam, oA, oB ).Luma, off: Box( flatOff, cam, oA, oB ).Luma );
			var flatCorner = ( on: Box( flatOn, cam, cA, cB ).Luma, off: Box( flatOff, cam, cA, cB ).Luma );
			Console.WriteLine( $"  corner {corner.on:F4}/{corner.off:F4} (on/off), open floor {open.on:F4}/{open.off:F4}, wall-less floor {flat.on:F4}/{flat.off:F4} and at the corner position {flatCorner.on:F4}/{flatCorner.off:F4}" );
			float aoCorner = AoBox( cornerAo, cam, cA, cB ), aoOpen = AoBox( cornerAo, cam, oA, oB );
			float aoFlat = AoBox( flatAo, cam, oA, oB ), aoFlatCorner = AoBox( flatAo, cam, cA, cB );
			Console.WriteLine( $"  AO term: corner {aoCorner:F3}, open floor {aoOpen:F3}; wall-less floor near {aoFlat:F3}, far {aoFlatCorner:F3}" );
			Check( corner.off > .01f, "corner fixture is too dark to judge" );
			// The strip is 0.03-0.12 units from the wall with a 0.27 radius (3% of the 9-unit
			// orbit). Monte Carlo over SsaoPass's kernel distribution, counting samples that
			// cross the wall, gives a squared AO of 0.63 / 0.84 / 0.96 at 0.03 / 0.07 / 0.12,
			// mean ~0.81. Blur and the range falloff move it a little, never back to 1.
			// Open ground must stay at 1: no self-occlusion at grazing angles.
			Check( aoCorner > .72f && aoCorner < .9f, $"AO term at the corner is {aoCorner:F3}, expected ~0.81" );
			Check( aoOpen > .99f && aoFlat > .99f && aoFlatCorner > .99f, $"flat ground occludes itself (open {aoOpen:F3}, wall-less {aoFlat:F3}/{aoFlatCorner:F3})" );
			// AO scales only ambient light, so the lit corner dims by the ambient share; open
			// ground must be unchanged to within float noise.
			Check( corner.on < corner.off * .99f, $"SSAO did not darken the lit corner ({corner.on:F4} vs {corner.off:F4})" );
			Check( MathF.Abs( open.on - open.off ) < open.off * .002f, $"SSAO changed the open floor next to the wall ({open.on:F4} vs {open.off:F4})" );
			Check( MathF.Abs( flat.on - flat.off ) < flat.off * .002f, $"SSAO changed a wall-less flat floor ({flat.on:F4} vs {flat.off:F4})" );
			Check( MathF.Abs( flatCorner.on - flatCorner.off ) < flatCorner.off * .002f, $"SSAO darkened the flat floor where the corner would be ({flatCorner.on:F4} vs {flatCorner.off:F4})" );
		} );

		registry.Add( "ss", "hardware EGL: SSR reflects an emissive wall in a smooth floor where the mirror image projects, and not in a rough one", () =>
		{
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var target = new CaptureTarget( gl, Size, Size );
			using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0 };
			var cam = Camera();
			// The wall is pure emissive red; floor is a neutral dielectric. The environment is
			// neutral, so red in the floor can only come from the screen-space reflection.
			var redWall = new MaterialDescriptor( "wall" ) { BaseColorFactor = new Vec4( 0, 0, 0, 1 ), RoughnessFactor = 1, EmissionFactor = new Vec3( 4, 0, 0 ) };
			MaterialDescriptor Floor( float rough ) => new( "floor" ) { BaseColorFactor = new Vec4( .2f, .2f, .2f, 1 ), RoughnessFactor = rough, MetallicFactor = 1 };
			float[] Frame( float rough, float ssr ) { renderer.SsrStrength = ssr; return Render( gl, renderer, target, Scene( true, 2 ), Resolver( Floor( rough ), redWall ), 3 ); }
			// Mirror image of the lower wall (z in 0.6..1.4) across the floor plane, seen on the floor.
			Vector3 mA = new( -.8f, 2, -.6f ), mB = new( .8f, 2, -1.4f );
			// Control: a floor patch whose reflected ray leaves the screen above the wall.
			Vector3 nA = new( -.8f, -3.2f, 0 ), nB = new( .8f, -2.6f, 0 );
			float RedExcess( float[] px, Vector3 a, Vector3 b ) { var s = Box( px, cam, a, b ); return s.Red - s.Green; }
			var smoothOn = Frame( .05f, 1 ); var smoothOff = Frame( .05f, 0 );
			var roughOn = Frame( .9f, 1 ); var roughOff = Frame( .9f, 0 );
			float sOn = RedExcess( smoothOn, mA, mB ), sOff = RedExcess( smoothOff, mA, mB );
			float rOn = RedExcess( roughOn, mA, mB ), rOff = RedExcess( roughOff, mA, mB );
			float nOn = RedExcess( smoothOn, nA, nB ), nOff = RedExcess( smoothOff, nA, nB );
			Console.WriteLine( $"  red excess at mirror image: smooth {sOn:F4}/{sOff:F4} (on/off), rough {rOn:F4}/{rOff:F4}; near-floor control {nOn:F4}/{nOff:F4}" );
			Check( sOn > sOff + .05f, $"SSR shows no reflected wall in the smooth floor ({sOn:F4} vs {sOff:F4})" );
			Check( MathF.Abs( rOn - rOff ) < .01f, $"SSR reflected in a rough floor ({rOn:F4} vs {rOff:F4})" );
			Check( MathF.Abs( nOn - nOff ) < ( sOn - sOff ) * .1f, $"SSR put red where the mirror image does not project ({nOn:F4} vs {nOff:F4})" );
		} );

		registry.Add( "ss", "hardware EGL: with AO and SSR off the frame ignores history; SSR adds no colour on the first frame after a reset", () =>
		{
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var target = new CaptureTarget( gl, Size, Size );
			using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0 };
			var redWall = new MaterialDescriptor( "wall" ) { BaseColorFactor = new Vec4( 0, 0, 0, 1 ), EmissionFactor = new Vec3( 4, 0, 0 ) };
			var mirror = new MaterialDescriptor( "floor" ) { BaseColorFactor = new Vec4( .2f, .2f, .2f, 1 ), RoughnessFactor = .05f, MetallicFactor = 1 };
			var a = Render( gl, renderer, target, Scene( true, 2 ), Resolver( mirror, redWall ), 3 );
			var b = Render( gl, renderer, target, Scene( true, 2 ), Resolver( mirror, redWall ), 1 );
			// With both effects off, the frame does not depend on history at all.
			for ( int i = 0; i < a.Length; i++ ) Check( a[i] == b[i], $"frame depends on history with AO/SSR off (texel {i / 4})" );
			// First frame after a reset with SSR on: history is black, so SSR may only darken
			// the env reflection, never add colour; the result must stay finite (checked in Hdr).
			renderer.SsrStrength = 1;
			var first = Render( gl, renderer, target, Scene( true, 2 ), Resolver( mirror, redWall ), 1 );
			var cam = Camera();
			var s = Box( first, cam, new Vector3( -.8f, 2, -.6f ), new Vector3( .8f, 2, -1.4f ) );
			var o = Box( a, cam, new Vector3( -.8f, 2, -.6f ), new Vector3( .8f, 2, -1.4f ) );
			Console.WriteLine( $"  first-frame SSR red {s.Red:F4} vs SSR-off {o.Red:F4}" );
			Check( s.Red <= o.Red + .005f, $"SSR reflected colour on the first frame after a history reset (red {s.Red:F4} vs {o.Red:F4})" );
			// Invalid history means SSR falls back to the environment: the first frame matches
			// SSR off exactly instead of replacing the env reflection with black.
			renderer.SsrStrength = 0;
			var firstOff = Render( gl, renderer, target, Scene( true, 2 ), Resolver( mirror, redWall ), 1 );
			for ( int i = 0; i < first.Length; i++ ) Check( first[i] == firstOff[i], $"first frame after reset with SSR on differs from SSR off at texel {i / 4} ({first[i]} vs {firstOff[i]})" );
		} );

		registry.Add( "ss", "hardware EGL: a tilted glass panel refracts the backdrop by its IOR and leaves pixels outside it unchanged", () =>
		{
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var target = new CaptureTarget( gl, Size, Size );
			using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0 };
			var cam = new OrbitCamera { Target = new Vector3( 0, 0, 1 ), Distance = 8, YawDegrees = -90, PitchDegrees = 0, Near = .1f, Far = 60 };
			// Backdrop at y=3: emissive red for x<0, emissive green for x>0. Glass panel near y=0,
			// rotated 35 degrees about z, covering x in about [-1,1], z in [.4,1.6].
			var m = new PolyMesh();
			m.Positions.AddRange( new[] { new Vec3( -6, 3, -3 ), new Vec3( 0, 3, -3 ), new Vec3( 0, 3, 5 ), new Vec3( -6, 3, 5 ) } );
			m.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
			m.Positions.AddRange( new[] { new Vec3( 0, 3, -3 ), new Vec3( 6, 3, -3 ), new Vec3( 6, 3, 5 ), new Vec3( 0, 3, 5 ) } );
			m.AddFace( new[] { 4, 5, 6, 7 }, Uvs, 1 );
			float c = MathF.Cos( .61f ), sn = MathF.Sin( .61f );
			Vec3 G( float u, float z ) => new( u * c, u * sn, z );
			m.Positions.AddRange( new[] { G( -1.2f, .4f ), G( 1.2f, .4f ), G( 1.2f, 1.6f ), G( -1.2f, 1.6f ) } );
			m.AddFace( new[] { 8, 9, 10, 11 }, Uvs, 2 );
			string[] slots = { "red", "green", "glass" };
			MaterialDescriptor Emit( string n, Vec3 e ) => new( n ) { BaseColorFactor = new Vec4( 0, 0, 0, 1 ), RoughnessFactor = 1, EmissionFactor = e };
			float[] Frame( float ior )
			{
				var glass = new MaterialDescriptor( "glass" ) { BaseColorFactor = new Vec4( 1, 1, 1, 1 ), RoughnessFactor = .02f, Transmission = 1, Thickness = 1.5f, Ior = ior };
				var materials = Resolver( Emit( "red", new Vec3( 2, 0, 0 ) ), Emit( "green", new Vec3( 0, 2, 0 ) ), glass );
				using var mesh = ViewportMesh.FromPolyMesh( m, i => slots[i] );
				mesh.ApplyMaterials( materials ); mesh.Upload( gl );
				try
				{
					renderer.ResetTemporalHistory();
					for ( int i = 0; i < 2; i++ ) renderer.Draw( mesh, null, cam, Size, Size, 1, target.Handle );
					Check( gl.GetError() == GLEnum.NoError, "GL error drawing the refraction fixture" );
					return Hdr( gl, renderer );
				}
				finally { mesh.ReleaseGpu( gl ); }
			}
			// Red/green boundary column along a scan row: first x where green dominates.
			int Edge( float[] px, int y ) { for ( int x = 0; x < Size; x++ ) { int i = ( y * Size + x ) * 4; if ( px[i + 1] > px[i] ) return x; } return Size; }
			var flat = Frame( 1f ); var bent = Frame( 1.5f ); var dense = Frame( 2f );
			int inside = Pixel( cam, new Vector3( 0, 0, 1 ) ).Y, outside = Pixel( cam, new Vector3( 0, 0, 2.6f ) ).Y;
			int eOut = Edge( flat, outside ), eFlat = Edge( flat, inside ), eBent = Edge( bent, inside ), eDense = Edge( dense, inside );
			Console.WriteLine( $"  boundary x: outside {eOut}, glass ior1.0 {eFlat}, ior1.5 {eBent}, ior2.0 {eDense}" );
			Check( Math.Abs( eFlat - eOut ) <= 1, $"IOR 1 glass displaced the backdrop ({eFlat} vs {eOut})" );
			Check( Math.Abs( eBent - eFlat ) >= 3, $"IOR 1.5 glass did not refract the backdrop ({eBent} vs {eFlat})" );
			Check( Math.Abs( eDense - eFlat ) > Math.Abs( eBent - eFlat ) && Math.Sign( eDense - eFlat ) == Math.Sign( eBent - eFlat ), $"refraction offset not monotonic in IOR ({eFlat}, {eBent}, {eDense})" );
			for ( int x = 0; x < Size; x++ ) { int i = ( outside * Size + x ) * 4; Check( bent[i] == flat[i] && bent[i + 1] == flat[i + 1], $"IOR changed a pixel outside the glass at x={x}" ); }
		} );
	}
}
