using Marionette.Desktop;
using Silk.NET.OpenGL;

/// <summary>Wave 5, item 30: NPR ink outlines + halftone/crosshatch/Bayer passes in the
/// desktop composite. GL tests reuse the HdrPipelineTests post-chain pattern.</summary>
internal static class NprTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }

		registry.Add( "npr", "composite shader carries the NPR stages", () =>
		{
			var src = PostChainShaders.Composite;
			Check( src.Contains( "uNprInk", StringComparison.Ordinal ), "no uNprInk uniform" );
			Check( src.Contains( "uNprTone", StringComparison.Ordinal ), "no uNprTone uniform" );
			Check( src.Contains( "uHasDepth", StringComparison.Ordinal ), "no depth guard" );
			Check( src.Contains( "nprInkEdge", StringComparison.Ordinal ), "no ink edge function" );
			Check( src.Contains( "nprHalftone", StringComparison.Ordinal ), "no halftone function" );
			Check( src.Contains( "nprCrosshatch", StringComparison.Ordinal ), "no crosshatch function" );
			Check( src.Contains( "nprBayer", StringComparison.Ordinal ), "no Bayer function" );
			Check( src.Contains( "nprToneApply", StringComparison.Ordinal ), "no tone dispatch" );
		} );

		registry.Add( "npr", "CLI --npr flags parse and reject bad values", () =>
		{
			var d = DesktopOptions.Parse( [] );
			Check( !d.NprInk && d.NprTone == NprTone.None && d.NprStrength == 1f && d.NprInkWidth == 1.5f, "NPR defaults changed" );
			var ink = DesktopOptions.Parse( ["--npr", "ink"] );
			Check( ink.NprInk && ink.NprTone == NprTone.None, "--npr ink lost" );
			var ih = DesktopOptions.Parse( ["--npr", "ink-halftone", "--npr-strength", "0.5", "--npr-ink-width", "2"] );
			Check( ih.NprInk && ih.NprTone == NprTone.Halftone && ih.NprStrength == 0.5f && ih.NprInkWidth == 2f, "--npr ink-halftone lost" );
			var cb = DesktopOptions.Parse( ["--npr", "ink-crosshatch"] );
			Check( cb.NprInk && cb.NprTone == NprTone.Crosshatch, "--npr ink-crosshatch lost" );
			var b = DesktopOptions.Parse( ["--npr", "bayer"] );
			Check( !b.NprInk && b.NprTone == NprTone.Bayer, "--npr bayer lost" );
			foreach ( var args in new string[][] { ["--npr", "sepia"], ["--npr"], ["--npr-strength", "1.5"],
				["--npr-strength", "-0.1"], ["--npr-ink-width", "0.1"], ["--npr-ink-width", "9"] } )
			{
				bool rejected = false;
				try { DesktopOptions.Parse( args ); } catch ( ArgumentException ) { rejected = true; }
				Check( rejected, $"accepted invalid option: {string.Join( ' ', args )}" );
			}
		} );

		// Runs a synthetic linear HDR image through the real post chain and returns 8-bit RGB.
		static byte[] Post( GL gl, BloomPass post, float[] rgb, int w, int h, uint depthTex = 0 )
		{
			post.Ensure( w, h );
			var rgba = new float[w * h * 4];
			for ( int i = 0; i < w * h; i++ ) { rgba[i * 4] = rgb[i * 3]; rgba[i * 4 + 1] = rgb[i * 3 + 1]; rgba[i * 4 + 2] = rgb[i * 3 + 2]; rgba[i * 4 + 3] = 1; }
			gl.BindTexture( TextureTarget.Texture2D, post.ColorTex );
			gl.TexSubImage2D<float>( TextureTarget.Texture2D, 0, 0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.Float, rgba.AsSpan() );
			post.Intensity = 0;
			post.Process();
			using var target = new CaptureTarget( gl, w, h );
			post.Composite( target.Handle, new ViewSettings { Transform = ViewTransform.Standard }, depthTex, 1, 0, .1f, 100 );
			var image = new byte[w * h * 3];
			gl.Finish();
			gl.ReadPixels<byte>( 0, 0, (uint)w, (uint)h, PixelFormat.Rgb, PixelType.UnsignedByte, image.AsSpan() );
			Check( gl.GetError() == GLEnum.NoError, "GL error in post chain" );
			return image;
		}

		static float[] GreyGradient( int w, int h )
		{
			var rgb = new float[w * h * 3];
			for ( int y = 0; y < h; y++ ) for ( int x = 0; x < w; x++ )
			{
				float v = 0.02f + 0.98f * x / Math.Max( 1, w - 1 );
				int i = ( y * w + x ) * 3;
				rgb[i] = rgb[i + 1] = rgb[i + 2] = v;
			}
			return rgb;
		}

		static float DiffFraction( byte[] a, byte[] b )
		{
			int diff = 0;
			for ( int i = 0; i < a.Length; i++ ) if ( Math.Abs( a[i] - b[i] ) > 10 ) diff++;
			return (float)diff / a.Length;
		}

		registry.Add( "npr", "hardware EGL: halftone and crosshatch visibly replace shading", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			const int w = 64, h = 64;
			var gradient = GreyGradient( w, h );
			byte[] off;
			using ( var post = new BloomPass( gl ) ) off = Post( gl, post, gradient, w, h );
			byte[] halftone, crosshatch;
			using ( var post = new BloomPass( gl ) ) { post.NprTone = NprTone.Halftone; halftone = Post( gl, post, gradient, w, h ); }
			using ( var post = new BloomPass( gl ) ) { post.NprTone = NprTone.Crosshatch; crosshatch = Post( gl, post, gradient, w, h ); }
			Check( DiffFraction( off, halftone ) > 0.3f, $"halftone barely changed the image (diff {DiffFraction( off, halftone ):0.00})" );
			Check( DiffFraction( off, crosshatch ) > 0.3f, $"crosshatch barely changed the image (diff {DiffFraction( off, crosshatch ):0.00})" );
			bool HasExtreme( byte[] img, int lo, int hi ) { bool l = false, hh = false; foreach ( var v in img ) { if ( v <= lo ) l = true; if ( v >= hi ) hh = true; } return l && hh; }
			Check( HasExtreme( halftone, 16, 240 ), "halftone has no dots-on-paper extremes" );
			Check( HasExtreme( crosshatch, 16, 240 ), "crosshatch has no ink-on-paper extremes" );
			using ( var post = new BloomPass( gl ) ) { post.NprTone = NprTone.Halftone; post.NprStrength = 0f; var zero = Post( gl, post, gradient, w, h ); Check( DiffFraction( off, zero ) == 0f, "strength 0 did not reproduce the plain composite" ); }
		} );

		registry.Add( "npr", "hardware EGL: Bayer quantises tone levels", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			const int w = 64, h = 64;
			var gradient = GreyGradient( w, h );
			byte[] off, bayer;
			using ( var post = new BloomPass( gl ) ) off = Post( gl, post, gradient, w, h );
			using ( var post = new BloomPass( gl ) ) { post.NprTone = NprTone.Bayer; bayer = Post( gl, post, gradient, w, h ); }
			var offLevels = new HashSet<int>(); var bayerLevels = new HashSet<int>();
			for ( int i = 0; i < off.Length; i += 3 ) { offLevels.Add( off[i] ); bayerLevels.Add( bayer[i] ); }
			Check( DiffFraction( off, bayer ) > 0.1f, "Bayer barely changed the image" );
			Check( bayerLevels.Count <= 8, $"Bayer left {bayerLevels.Count} levels, expected <= 8" );
			Check( offLevels.Count >= 40, $"plain gradient only had {offLevels.Count} levels, test is vacuous" );
		} );

		registry.Add( "npr", "hardware EGL: ink outlines fire on a depth step and stay off elsewhere", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			const int w = 32, h = 32;
			var flat = new float[w * h * 3];
			for ( int i = 0; i < flat.Length; i++ ) flat[i] = 0.18f;
			var depths = new float[w * h];
			for ( int y = 0; y < h; y++ ) for ( int x = 0; x < w; x++ ) depths[y * w + x] = x < w / 2 ? 0.5f : 0.9f;
			uint depthTex = gl.GenTexture();
			gl.BindTexture( TextureTarget.Texture2D, depthTex );
			gl.TexImage2D<float>( TextureTarget.Texture2D, 0, InternalFormat.DepthComponent32f, (uint)w, (uint)h, 0, PixelFormat.DepthComponent, PixelType.Float, depths.AsSpan() );
			gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest );
			gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest );
			gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge );
			gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge );
			try
			{
				byte[] plain, inked;
				using ( var post = new BloomPass( gl ) ) plain = Post( gl, post, flat, w, h, depthTex );
				using ( var post = new BloomPass( gl ) ) { post.NprInk = true; inked = Post( gl, post, flat, w, h, depthTex ); }
				float ColMean( byte[] img, int x0, int x1 ) { long s = 0; int n = 0; for ( int y = 0; y < h; y++ ) for ( int x = x0; x <= x1; x++ ) { s += img[( y * w + x ) * 3]; n++; } return (float)s / n; }
				float edge = ColMean( inked, w / 2 - 2, w / 2 + 1 );
				float far = Math.Min( ColMean( inked, 2, 5 ), ColMean( inked, w - 6, w - 3 ) );
				float plainEdge = ColMean( plain, w / 2 - 2, w / 2 + 1 );
				Check( edge < 40, $"ink did not darken the depth step (edge mean {edge:0})" );
				Check( far > 90, $"ink leaked far from the step (far mean {far:0})" );
				Check( Math.Abs( plainEdge - far ) < 25, "plain composite is not flat across the step" );
			}
			finally { gl.DeleteTexture( depthTex ); }
		} );
	}
}
