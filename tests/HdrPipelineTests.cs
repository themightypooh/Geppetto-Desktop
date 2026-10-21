using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

/// <summary>R1: linear HDR, progressive bloom, AgX view transform, single display encode.</summary>
internal static class HdrPipelineTests
{
	// Blender 5.2 display values (sRGB, view AgX, look None) for scene-linear grey; sampled
	// headless from Cycles world emission.
	static readonly (float Linear, float Display)[] BlenderAgx =
		[(0.05f, 0.22721f), (0.18f, 0.46133f), (1f, 0.77096f), (4f, 0.91366f)];

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }

		registry.Add( "hdr", "fragment writes linear hdr (no in-shader tone map)", () =>
		{
			Check( RendererShaders.FragmentSrc.Contains( "R1: write linear HDR", StringComparison.Ordinal ), "R1 marker missing" );
			Check( !RendererShaders.FragmentSrc.Contains( "aces(", StringComparison.Ordinal ), "lit shader tone-maps" );
		} );
		registry.Add( "hdr", "no in-shader bloom approximation remains", () =>
		{
			var src = RendererShaders.FragmentSrc;
			Check( !src.Contains( "max(hdr-1,0)*0.25", StringComparison.Ordinal ) && !src.Contains( "max(hdr - 1", StringComparison.Ordinal ), "in-shader bloom approx still present" );
		} );
		registry.Add( "hdr", "the lit pass stays linear: no encode, no white balance, no view transform", () =>
		{
			var src = RendererShaders.FragmentSrc;
			// One encode happens, in the composite. The lit pass must not encode, and the
			// display-side colour management must not leak into the lighting.
			Check( !src.Contains( "linearToSrgb(", StringComparison.Ordinal ), "lit shader sRGB-encodes" );
			Check( !src.Contains( "uWhiteBalance", StringComparison.Ordinal ), "white balance belongs to the display stage" );
			Check( !src.Contains( "agx(", StringComparison.Ordinal ), "lit shader applies a view transform" );
			Check( !src.Contains( "uViewTransform", StringComparison.Ordinal ), "lit shader selects a view transform" );
		} );
		registry.Add( "hdr", "exactly one display encode, and it is the real sRGB transfer function", () =>
		{
			// The composite owns the only encode in the whole renderer.
			var composite = PostChainShaders.Composite;
			Check( composite.Contains( "srgbEncode", StringComparison.Ordinal ), "composite does not encode" );
			Check( composite.Contains( "0.0031308", StringComparison.Ordinal ) && composite.Contains( "12.92", StringComparison.Ordinal ),
				"composite does not use the piecewise sRGB transfer function" );
			foreach ( var source in new[] { RendererShaders.FragmentSrc, PostChainShaders.DownsampleFs, PostChainShaders.UpsampleFs,
				PostChainShaders.DofPrepFs, PostChainShaders.DofGatherFs, TaaShaders.Fragment } )
				Check( !source.Contains( "srgbEncode", StringComparison.Ordinal ), "an encode leaked outside the composite" );
		} );
		registry.Add( "hdr", "white balance is neutral at D65 and separates warm from cool", () =>
		{
			var neutral = WhiteBalance.Gain( WhiteBalance.ReferenceKelvin, 0f );
			Check( MathF.Abs( neutral.X - 1f ) < 1e-3f && MathF.Abs( neutral.Y - 1f ) < 1e-3f && MathF.Abs( neutral.Z - 1f ) < 1e-3f,
				$"D65 white balance is not neutral: {neutral}" );
			// A warm illuminant (low K) is corrected by pushing the image cooler, a cool
			// illuminant by pushing it warmer.
			var warm = WhiteBalance.Gain( 3000f, 0f );
			var cool = WhiteBalance.Gain( 12000f, 0f );
			Check( warm.Z > warm.X, $"3000 K correction is not cooler: {warm}" );
			Check( cool.X > cool.Z, $"12000 K correction is not warmer: {cool}" );
			// White balance must not change exposure.
			foreach ( var gain in new[] { warm, cool } )
			{
				float luma = 0.2126f * gain.X + 0.7152f * gain.Y + 0.0722f * gain.Z;
				Check( MathF.Abs( luma - 1f ) < 1e-3f, $"white balance changed the exposure: luma {luma}" );
			}
			var magenta = WhiteBalance.Gain( WhiteBalance.ReferenceKelvin, 1f );
			var green = WhiteBalance.Gain( WhiteBalance.ReferenceKelvin, -1f );
			Check( magenta.X > magenta.Y && magenta.Z > magenta.Y, $"tint +1 is not magenta: {magenta}" );
			Check( green.Y > green.X && green.Y > green.Z, $"tint -1 is not green: {green}" );
		} );

		registry.Add( "hdr", "CLI colour settings preserve defaults and validate ranges", () =>
		{
			Check( DesktopOptions.Parse( [] ).View == new ViewSettings(), "CLI changed the default view" );
			var options = DesktopOptions.Parse( ["--view-transform", "standard", "--look", "punchy",
				"--view-exposure", "-2", "--gamma", "1.2", "--white-balance", "3000", "--tint", "-.25", "--exposure", "2"] );
			Check( options.View == new ViewSettings { Transform = ViewTransform.Standard, Look = ViewLook.Punchy,
				Exposure = -2, Gamma = 1.2f, WhiteBalanceTemperature = 3000, WhiteBalanceTint = -.25f }, "CLI lost view settings" );
			Check( options.Exposure == 2, "scene exposure no longer separate from display exposure" );
			foreach ( var args in new string[][] {
				["--view-transform", "filmic"], ["--look", "unknown"], ["--gamma", "0"], ["--gamma", "NaN"],
				["--white-balance", "1666"], ["--white-balance", "25001"], ["--tint", "1.1"],
				["--view-exposure", "Infinity"], ["--view-exposure", "17"], ["--gamma"] } )
			{
				bool rejected = false;
				try { DesktopOptions.Parse( args ); } catch ( ArgumentException ) { rejected = true; }
				Check( rejected, $"accepted invalid option: {string.Join( ' ', args )}" );
			}
			Check( DesktopOptions.Parse( ["--white-balance", "1667", "--tint", "-1", "--view-exposure", "-16", "--gamma", "0.1"] ).View.WhiteBalanceTemperature == 1667, "lower bounds rejected" );
			Check( DesktopOptions.Parse( ["--white-balance", "25000", "--tint", "1", "--view-exposure", "16", "--gamma", "5"] ).View.WhiteBalanceTint == 1, "upper bounds rejected" );
		} );

		// Runs a synthetic linear HDR image through the real post chain and returns 8-bit RGB.
		static byte[] Post( GL gl, BloomPass post, float[] rgb, int w, int h, ViewSettings view, float bloom )
		{
			post.Ensure( w, h );
			var rgba = new float[w * h * 4];
			for ( int i = 0; i < w * h; i++ ) { rgba[i * 4] = rgb[i * 3]; rgba[i * 4 + 1] = rgb[i * 3 + 1]; rgba[i * 4 + 2] = rgb[i * 3 + 2]; rgba[i * 4 + 3] = 1; }
			gl.BindTexture( TextureTarget.Texture2D, post.ColorTex );
			gl.TexSubImage2D<float>( TextureTarget.Texture2D, 0, 0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.Float, rgba.AsSpan() );
			post.Intensity = bloom;
			post.Process();
			using var target = new CaptureTarget( gl, w, h );
			post.Composite( target.Handle, view, 0, 1, 0, .1f, 100 );
			var image = new byte[w * h * 3];
			gl.Finish();
			gl.ReadPixels<byte>( 0, 0, (uint)w, (uint)h, PixelFormat.Rgb, PixelType.UnsignedByte, image.AsSpan() );
			Check( gl.GetError() == GLEnum.NoError, "GL error in post chain" );
			return image;
		}
		static float[] Fill( int n, Func<int, float> value )
		{
			var a = new float[n * 3];
			for ( int i = 0; i < n; i++ ) a[i * 3] = a[i * 3 + 1] = a[i * 3 + 2] = value( i );
			return a;
		}

		registry.Add( "hdr", "hardware EGL: AgX maps a linear ramp monotonically and matches Blender's grey", () =>
		{
			using var context = new HeadlessGlContext();
			using var post = new BloomPass( context.Gl );
			const int W = 256;
			var ramp = Post( context.Gl, post, Fill( W * 4, i => MathF.Pow( 2, -12 + 18f * ( i % W ) / ( W - 1 ) ) ), W, 4, new ViewSettings(), 0 );
			for ( int x = 1; x < W; x++ ) Check( ramp[x * 3] >= ramp[( x - 1 ) * 3], $"ramp not monotonic at {x}" );
			Check( ramp[0] < 8 && ramp[( W - 1 ) * 3] > 245, $"ramp range {ramp[0]}..{ramp[( W - 1 ) * 3]}" );
			foreach ( var (linear, display) in BlenderAgx )
			{
				var grey = Post( context.Gl, post, Fill( 16, _ => linear ), 4, 4, new ViewSettings(), 0 );
				float got = grey[0] / 255f;
				Console.WriteLine( $"  AgX {linear} -> {got:F4} (Blender {display:F4})" );
				Check( MathF.Abs( got - display ) <= 2.5f / 255f, $"AgX {linear} gave {got:F4}, Blender {display:F4}" );
			}
			var standard = Post( context.Gl, post, Fill( 16, _ => .18f ), 4, 4, new ViewSettings { Transform = ViewTransform.Standard }, 0 );
			Check( Math.Abs( standard[0] - 118 ) <= 1, $"Standard sRGB encode of 0.18 gave {standard[0]}, expected 118" );
		} );

		registry.Add( "hdr", "hardware EGL: emitter above 1.0 blooms with a measurable, finite halo", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			using var post = new BloomPass( gl );
			const int N = 256;
			var scene = Fill( N * N, i => Math.Abs( i % N - N / 2 ) < 2 && Math.Abs( i / N - N / 2 ) < 2 ? 50f : 0f );
			var lit = Post( gl, post, scene, N, N, new ViewSettings { Transform = ViewTransform.Standard }, 1f );
			var plain = Post( gl, post, scene, N, N, new ViewSettings { Transform = ViewTransform.Standard }, 0f );
			int Radius( byte[] img ) { int r = 0; while ( r < N / 2 - 1 && img[( N / 2 * N + N / 2 + r ) * 3] > 2 ) r++; return r; }
			int rBloom = Radius( lit ), rPlain = Radius( plain );
			var bloom = new float[( N / 2 ) * ( N / 2 ) * 4];
			gl.BindTexture( TextureTarget.Texture2D, post.BloomTex );
			gl.GetTexImage<float>( TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, bloom.AsSpan() );
			double energy = 0; for ( int i = 0; i < bloom.Length; i += 4 ) energy += bloom[i];
			energy *= 4.0 / post.Levels; // half-res texels, averaged over levels as composited
			double input = 16 * 50.0;
			Console.WriteLine( $"  halo radius {rBloom}px (no bloom {rPlain}px), bloom energy {energy:F1} vs emitter {input:F0}, {post.Levels} levels" );
			Check( rPlain <= 3, "emitter itself is wider than its footprint" );
			Check( rBloom >= 16, $"bloom halo radius only {rBloom}px" );
			Check( double.IsFinite( energy ) && energy > input * .05 && energy < input * 1.5, $"bloom energy {energy} not bounded by the emitter's {input}" );
		} );

		registry.Add( "hdr", "hardware EGL: bright gradient clips less than the LDR encode", () =>
		{
			using var context = new HeadlessGlContext();
			using var post = new BloomPass( context.Gl );
			const int W = 256;
			var scene = Fill( W * 4, i => 8f * ( i % W ) / ( W - 1 ) );
			int Clipped( byte[] img ) { int c = 0; for ( int x = 0; x < W; x++ ) if ( img[x * 3] >= 254 ) c++; return c; }
			int agx = Clipped( Post( context.Gl, post, scene, W, 4, new ViewSettings(), 0 ) );
			int ldr = Clipped( Post( context.Gl, post, scene, W, 4, new ViewSettings { Transform = ViewTransform.Standard }, 0 ) );
			Console.WriteLine( $"  clipped columns over 0..8: AgX {agx}, LDR {ldr}" );
			Check( agx * 4 < ldr, $"AgX clips {agx} columns, LDR {ldr}" );
		} );

		registry.Add( "hdr", "hardware EGL: white balance reaches the display stage and keeps exposure", () =>
		{
			using var context = new HeadlessGlContext();
			using var post = new BloomPass( context.Gl );
			// Standard transform: the output is the sRGB-encoded linear value, so the channel
			// ratios are the white-balance gain and the mean is the exposure. 0.18 keeps the
			// brightest corrected channel below clipping.
			var view = new ViewSettings { Transform = ViewTransform.Standard };
			var neutral = Post( context.Gl, post, Fill( 16, _ => .18f ), 4, 4, view, 0 );
			var warm = Post( context.Gl, post, Fill( 16, _ => .18f ), 4, 4,
				view with { WhiteBalanceTemperature = 3000f }, 0 );
			var cool = Post( context.Gl, post, Fill( 16, _ => .18f ), 4, 4,
				view with { WhiteBalanceTemperature = 12000f }, 0 );
			// Byte scale (0..255), averaged over the two sampled pixels.
			float Mean( byte[] img, int channel ) => ( img[channel] + img[3 + channel] ) / 2f;
			Console.WriteLine( $"  3000 K: R {Mean( warm, 0 ):F1} B {Mean( warm, 2 ):F1}; 12000 K: R {Mean( cool, 0 ):F1} B {Mean( cool, 2 ):F1}" );
			Check( Math.Abs( Mean( neutral, 0 ) - Mean( neutral, 2 ) ) <= 1.5f, "D65 white balance is not neutral on screen" );
			Check( Mean( warm, 2 ) > Mean( warm, 0 ) + 2f, $"3000 K did not cool the image: {Mean( warm, 0 ):F1}/{Mean( warm, 2 ):F1}" );
			Check( Mean( cool, 0 ) > Mean( cool, 2 ) + 2f, $"12000 K did not warm the image: {Mean( cool, 0 ):F1}/{Mean( cool, 2 ):F1}" );
			// White balance must not change the exposure. The display value is sRGB-encoded,
			// so the linear luminance is recovered by decoding before it is compared.
			static float SrgbToLinear( float value )
			{
				float c = value / 255f;
				return c <= 0.04045f ? c / 12.92f : MathF.Pow( ( c + 0.055f ) / 1.055f, 2.4f );
			}
			float LinearLuma( byte[] img )
			{
				float r = SrgbToLinear( Mean( img, 0 ) ), g = SrgbToLinear( Mean( img, 1 ) ), b = SrgbToLinear( Mean( img, 2 ) );
				return 0.2126f * r + 0.7152f * g + 0.0722f * b;
			}
			float referenceLuma = LinearLuma( neutral );
			float warmLuma = LinearLuma( warm ), coolLuma = LinearLuma( cool );
			Console.WriteLine( $"  linear luma: neutral {referenceLuma:F4}, 3000 K {warmLuma:F4}, 12000 K {coolLuma:F4}" );
			Check( MathF.Abs( warmLuma - referenceLuma ) <= 0.002f, $"3000 K changed the exposure: {warmLuma:F4} vs {referenceLuma:F4}" );
			Check( MathF.Abs( coolLuma - referenceLuma ) <= 0.002f, $"12000 K changed the exposure: {coolLuma:F4} vs {referenceLuma:F4}" );
		} );

		// The colour-management settings must be reachable on the shared renderer, so the
		// interactive viewer and the windowless capture get the same image.
		registry.Add( "hdr", "hardware EGL: the shared renderer applies the view transform and white balance", () =>
		{
			using var context = new HeadlessGlContext();
			using var renderer = new SceneRenderer( context.Gl );
			var camera = HeadlessImageChecks.Camera();
			var material = new MaterialPalette.Entry( "neutral", .05f, .05f, .05f, .9f );
			// A render uploads the mesh and drops its CPU arrays, so each capture needs its own.
			HeadlessImageChecks.Fixture Fixture() => new HeadlessImageChecks.Fixture()
				.Quad( "neutral", Vector3.Zero, new( .38f, 0, 0 ), new( 0, .38f, 0 ), Vector3.UnitZ, material );
			renderer.View = new ViewSettings { Transform = ViewTransform.Standard };
			HeadlessImageChecks.Image neutral, warm;
			using ( var mesh = Fixture().Mesh() )
				neutral = HeadlessImageChecks.Render( context, renderer, mesh, camera );
			renderer.View = renderer.View with { WhiteBalanceTemperature = 3000f };
			using ( var mesh = Fixture().Mesh() )
				warm = HeadlessImageChecks.Render( context, renderer, mesh, camera );
			var point = HeadlessImageChecks.Project( camera, Vector3.Zero );
			var n = neutral.At( point.X, point.Y );
			var w = warm.At( point.X, point.Y );
			Console.WriteLine( $"  neutral RGB {n.R}/{n.G}/{n.B}, 3000 K RGB {w.R}/{w.G}/{w.B}" );
			Check( Math.Abs( n.R - n.B ) <= 12, $"D65 is not neutral through the renderer: {n}" );
			Check( w.B > w.R + 4, $"3000 K did not cool the rendered image: {w}" );
		} );
	}
}
