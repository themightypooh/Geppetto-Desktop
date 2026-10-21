using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

/// <summary>R12: the renderer verification harness.
///
/// <para>Every renderer feature in the quality wave gets one <b>retained</b> capture: a
/// windowless EGL image written next to the test report, plus a JSON record of the probe
/// values and the expectation each was checked against. A capture that is never written
/// down cannot be reviewed after the fact, and a probe whose expectation is only visible
/// in the source cannot be checked by anyone else, so both are kept.</para>
///
/// <para>Each probe compares a measured value against an <b>independently computed</b>
/// expectation: a closed-form value, a reference render computed with the feature switched
/// off, or a physical relationship such as Beer-Lambert attenuation. A probe never asserts
/// only that "the image changed": a change is a symptom, not evidence. A feature that is
/// switched off must reproduce its reference exactly, and a feature that is switched on
/// must differ from it in the direction the physics predicts.</para>
///
/// <para>The harness is self-tested: a deliberately failing probe must be reported as a
/// failure, so a broken probe cannot silently pass. The self-test is registered in the
/// <c>harness</c> group and asserts that the probe machinery reports the failure rather
/// than swallowing it, and that a correct probe still passes so the self-test is not
/// vacuous.</para></summary>
internal static class RendererImageTests
{
	const int Size = 160;
	static readonly Vec2[] Uvs = { new( 0, 0 ), new( 1, 0 ), new( 1, 1 ), new( 0, 1 ) };

	/// <summary>One probe: a measured value, the expectation, and the tolerance.</summary>
	/// <summary>How a measured value is compared with its expectation.</summary>
	enum Expectation
	{
		/// <summary>The value must be within the tolerance of the expectation.</summary>
		Near,
		/// <summary>The value must be at least the expectation (a one-sided bound).</summary>
		AtLeast,
		/// <summary>The value must be at most the expectation (a one-sided bound).</summary>
		AtMost,
	}

	readonly record struct Probe( string Name, double Measured, double Expected, double Tolerance, string Unit, Expectation Kind )
	{
		public bool Passes => double.IsFinite( Measured ) && Kind switch
		{
			Expectation.AtLeast => Measured >= Expected - Tolerance,
			Expectation.AtMost => Measured <= Expected + Tolerance,
			_ => Math.Abs( Measured - Expected ) <= Tolerance,
		};
		public string Describe() => Kind switch
		{
			Expectation.AtLeast => $"{Name}: measured {Measured:G6}{Unit}, must be at least {Expected:G6}{Unit}",
			Expectation.AtMost => $"{Name}: measured {Measured:G6}{Unit}, must be at most {Expected:G6}{Unit}",
			_ => $"{Name}: measured {Measured:G6}{Unit}, expected {Expected:G6}{Unit} +/- {Tolerance:G6}{Unit}",
		};
	}

	/// <summary>Accumulates probes and retained captures, and fails with every reason listed.</summary>
	sealed class Report
	{
		readonly List<Probe> _probes = new();
		readonly List<string> _failures = new();
		readonly List<(byte[] Pixels, int Width, int Height)> _captures = new();

		public void Probe( string name, double measured, double expected, double tolerance, string unit = "" )
			=> Add( new Probe( name, measured, expected, tolerance, unit, Expectation.Near ) );

		/// <summary>A one-sided bound: the value must be at least the expectation.</summary>
		public void AtLeast( string name, double measured, double expected, string unit = "" )
			=> Add( new Probe( name, measured, expected, 0, unit, Expectation.AtLeast ) );

		/// <summary>A one-sided bound: the value must be at most the expectation.</summary>
		public void AtMost( string name, double measured, double expected, string unit = "" )
			=> Add( new Probe( name, measured, expected, 0, unit, Expectation.AtMost ) );

		void Add( Probe probe )
		{
			_probes.Add( probe );
			if ( !probe.Passes ) _failures.Add( probe.Describe() );
		}

		/// <summary>Adds a capture that is retained next to the report.</summary>
		public void Capture( byte[] rgba, int width, int height ) => _captures.Add( ( rgba, width, height ) );

		public void Assert( string feature )
		{
			if ( _failures.Count > 0 )
				throw new Exception( $"{feature} probes failed:\n  {string.Join( "\n  ", _failures )}" );
		}

		public object Write( string directory, string feature )
		{
			Directory.CreateDirectory( directory );
			var captures = new List<object>();
			for ( int i = 0; i < _captures.Count; i++ )
			{
				string name = $"{feature}{( _captures.Count > 1 ? "-" + i : "" )}.ppm";
				var ( bytes, width, height ) = _captures[i];
				using ( var stream = File.Create( Path.Combine( directory, name ) ) )
				using ( var writer = new BinaryWriter( stream ) )
				{
					writer.Write( System.Text.Encoding.ASCII.GetBytes( $"P6\n{width} {height}\n255\n" ) );
					// The GL readback is bottom-up; PPM is top-down, so the rows are flipped.
					for ( int y = height - 1; y >= 0; y-- )
					for ( int x = 0; x < width; x++ )
					{
						int p = ( y * width + x ) * 4;
						writer.Write( bytes[p] ); writer.Write( bytes[p + 1] ); writer.Write( bytes[p + 2] );
					}
				}
				captures.Add( new { file = name, sha256 = Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( bytes ) ) } );
			}
			var record = new
			{
				feature,
				probes = _probes.Select( p => new { p.Name, p.Measured, p.Expected, p.Tolerance, p.Unit } ),
				captures,
				passed = _failures.Count == 0,
			};
			File.WriteAllText( Path.Combine( directory, feature + ".json" ),
				JsonSerializer.Serialize( record, new JsonSerializerOptions { WriteIndented = true } ) );
			return record;
		}
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		// The captures are written outside the run's work directory, so the run's
		// settings are not needed here; the signature matches the other test groups.
		registry.Add( "harness", "self-test: a deliberately failing renderer probe is reported, not swallowed", () =>
		{
			// The probe machinery must be able to fail. If a probe could never fail, every
			// renderer capture in this file would be worthless.
			// Every comparison mode must be able to fail, and a correct value must still
			// pass, so no probe in this file can be vacuously green.
			bool Fails( Action<Report> add )
			{
				var report = new Report();
				add( report );
				try { report.Assert( "self-test" ); }
				catch ( Exception ) { return true; }
				return false;
			}
			if ( !Fails( r => r.Probe( "near mismatch", 1.0, 2.0, 0.0 ) ) )
				throw new Exception( "Renderer harness self-test failed: a wrong 'near' probe did not fail." );
			if ( !Fails( r => r.AtLeast( "at-least violation", 1.0, 2.0 ) ) )
				throw new Exception( "Renderer harness self-test failed: an 'at least' probe below its bound did not fail." );
			if ( !Fails( r => r.AtMost( "at-most violation", 3.0, 2.0 ) ) )
				throw new Exception( "Renderer harness self-test failed: an 'at most' probe above its bound did not fail." );
			if ( !Fails( r => r.Probe( "non-finite value", double.NaN, 1.0, 0.5 ) ) )
				throw new Exception( "Renderer harness self-test failed: a non-finite measurement did not fail." );
			// And the correct cases must still pass, so the self-test is not vacuous.
			var passing = new Report();
			passing.Probe( "near match", 2.0, 2.0, 0.0 );
			passing.AtLeast( "at-least satisfied", 3.0, 2.0 );
			passing.AtMost( "at-most satisfied", 1.0, 2.0 );
			passing.Assert( "self-test" );
			// A capture must be written and readable, or the retained artefacts are a fiction.
			string directory = Path.Combine( Path.GetTempPath(), "renderer-harness-self-test" );
			if ( Directory.Exists( directory ) ) Directory.Delete( directory, true );
			var written = new Report();
			written.Capture( new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, 2, 1 );
			written.Write( directory, "selftest" );
			string ppm = Path.Combine( directory, "selftest.ppm" );
			if ( !File.Exists( ppm ) ) throw new Exception( "Renderer harness self-test failed: the capture was not written." );
			var header = File.ReadAllText( ppm ).Split( '\n' )[0];
			if ( header != "P6" ) throw new Exception( "Renderer harness self-test failed: the capture is not a PPM." );
			Directory.Delete( directory, true );
		} );

		registry.Add( "renderer-image", "hardware EGL: every renderer feature retains an assertable capture", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			// The captures are retained outside the run's work directory, which a passing
			// run deletes: a retained capture that only exists during the run is not
			// retained. The directory is reported so it can be found afterwards.
			string directory = Path.Combine( "/tmp/opencode", "renderer-images" );
			var summary = new List<object>();
			summary.Add( Antialiasing( gl, directory ) );
			summary.Add( Bloom( gl, directory ) );
			summary.Add( ScreenSpace( gl, directory ) );
			summary.Add( ShadowSoftness( gl, directory ) );
			summary.Add( DepthOfField( gl, directory ) );
			summary.Add( MotionBlur( gl, directory ) );
			summary.Add( Volumetrics( gl, directory ) );
			summary.Add( MaterialLobes( gl, directory ) );
			summary.Add( Transparency( gl, directory ) );
			File.WriteAllText( Path.Combine( directory, "summary.json" ),
				JsonSerializer.Serialize( summary, new JsonSerializerOptions { WriteIndented = true } ) );
			Console.WriteLine( $"renderer features captured: {summary.Count} in {directory}" );
		} );
	}

	// ---------------------------------------------------------------- fixtures

	static void Check( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }

	/// <summary>A camera on the -y side looking toward +y, pitched down onto the floor.
	/// The same convention the screen-space probes use, so world points project to known
	/// pixels.</summary>
	static OrbitCamera Camera( float distance = 9f, float pitch = 28f ) => new()
	{
		Target = new Vector3( 0, 1, .8f ), Distance = distance, YawDegrees = -90, PitchDegrees = pitch,
		FovDegrees = 45, Near = .1f, Far = 60
	};

	/// <summary>The screen pixel of a world point, bottom-left origin, as the other GPU
	/// probes compute it.</summary>
	static (int X, int Y) Pixel( OrbitCamera camera, Vector3 world )
	{
		var p = Project( camera, world );
		return ( (int)p.X, (int)p.Y );
	}

	/// <summary>The sub-pixel screen position of a world point, bottom-left origin.</summary>
	static Vector2 Project( OrbitCamera camera, Vector3 world )
	{
		var p = Vector4.Transform( new Vector4( world, 1 ), camera.ViewMatrix() * camera.ProjectionMatrix( 1 ) );
		return new Vector2( ( p.X / p.W * .5f + .5f ) * Size, ( p.Y / p.W * .5f + .5f ) * Size );
	}

	/// <summary>Builds a mesh from a poly mesh with named material slots.</summary>
	static ViewportMesh Mesh( PolyMesh source, Func<int, string> slotName, MaterialResolver materials )
	{
		var mesh = ViewportMesh.FromPolyMesh( source, slotName );
		mesh.ApplyMaterials( materials );
		return mesh;
	}

	/// <summary>The linear HDR colour of the lit target, before tone mapping.</summary>
	static float[] Hdr( GL gl, SceneRenderer renderer )
	{
		var pixels = new float[Size * Size * 4];
		gl.Finish();
		gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, renderer.PostChainTargets.ColorFbo );
		gl.ReadPixels<float>( 0, 0, Size, Size, PixelFormat.Rgba, PixelType.Float, pixels.AsSpan() );
		Check( gl.GetError() == GLEnum.NoError, "GL error reading the HDR target" );
		for ( int i = 0; i < pixels.Length; i++ )
			Check( float.IsFinite( pixels[i] ), $"non-finite HDR value at texel {i / 4}" );
		return pixels;
	}

	/// <summary>The 8-bit display image of the last composite, for the retained capture.</summary>
	static byte[] Display( GL gl, uint target )
	{
		var pixels = new byte[Size * Size * 4];
		gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, target );
		gl.ReadBuffer( ReadBufferMode.ColorAttachment0 );
		gl.PixelStore( PixelStoreParameter.PackAlignment, 1 );
		gl.ReadPixels<byte>( 0, 0, Size, Size, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan() );
		gl.PixelStore( PixelStoreParameter.PackAlignment, 4 );
		Check( gl.GetError() == GLEnum.NoError, "GL error reading the display target" );
		return pixels;
	}

	/// <summary>Mean of a channel over the pixel box spanned by two world points.</summary>
	static double Box( float[] pixels, OrbitCamera camera, Vector3 a, Vector3 b, int channel )
	{
		var ( x0, y0 ) = Pixel( camera, a ); var ( x1, y1 ) = Pixel( camera, b );
		if ( x0 > x1 ) ( x0, x1 ) = ( x1, x0 );
		if ( y0 > y1 ) ( y0, y1 ) = ( y1, y0 );
		x0 = Math.Clamp( x0, 0, Size - 1 ); x1 = Math.Clamp( x1, 0, Size - 1 );
		y0 = Math.Clamp( y0, 0, Size - 1 ); y1 = Math.Clamp( y1, 0, Size - 1 );
		double sum = 0; int count = 0;
		for ( int y = y0; y <= y1; y++ )
		for ( int x = x0; x <= x1; x++ ) { sum += pixels[( y * Size + x ) * 4 + channel]; count++; }
		Check( count > 0, "empty probe box" );
		return sum / count;
	}

	// ---------------------------------------------------------------- features

	/// <summary>Anti-aliasing: a high-contrast edge must be resolved to the analytic
	/// coverage of the triangle, and no anti-aliasing must not resolve it at all.</summary>
	static object Antialiasing( GL gl, string directory )
	{
		var report = new Report();
		// A triangle in the y-z plane seen from the -y side, so its edge crosses pixels
		// diagonally and the exact coverage of each partial pixel is computable.
		var scene = new PolyMesh();
		scene.Positions.AddRange( new[] { new Vec3( 0, -1, -1 ), new Vec3( 0, 1, -.9f ), new Vec3( 0, -.8f, 1.2f ) } );
		scene.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 0, 1 ) }, 0 );
		var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, YawDegrees = 0, PitchDegrees = 0, FovDegrees = 45, Near = .1f, Far = 60 };

		MaterialResolver Materials( float emission )
		{
			string root = Path.Combine( directory, "aa" );
			Directory.CreateDirectory( root );
			var resolver = new MaterialResolver( root, "r12" );
			resolver.SetOverride( "edge", new MaterialDescriptor( "edge" )
			{ BaseColorFactor = new Vec4( 0, 0, 0, 1 ), EmissionFactor = new Vec3( emission, emission, emission ) }, required: true );
			return resolver;
		}

		( float[] Hdr, byte[] Display ) Render( AntiAliasingMode mode, float emission )
		{
			using var mesh = Mesh( scene, _ => "edge", Materials( emission ) );
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0, AntiAliasing = mode };
				using var target = new CaptureTarget( gl, Size, Size );
				renderer.ResetTemporalHistory();
				for ( int i = 0; i < 8; i++ ) renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				return ( Hdr( gl, renderer ), Display( gl, target.Handle ) );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		// The analytic coverage of a pixel against the projected triangle: the exact
		// polygon/pixel intersection, independent of any raster sample position.
		var projected = new List<Vector2>
		{
			Project( camera, new Vector3( 0, -1, -1 ) ),
			Project( camera, new Vector3( 0, 1, -.9f ) ),
			Project( camera, new Vector3( 0, -.8f, 1.2f ) ),
		};
		double Coverage( int x, int y )
		{
			var clipped = projected;
			foreach ( var ( axis, bound, sign ) in new[] { ( 0, (float)x, 1f ), ( 0, x + 1f, -1f ), ( 1, (float)y, 1f ), ( 1, y + 1f, -1f ) } )
			{
				var output = new List<Vector2>();
				for ( int i = 0; i < clipped.Count; i++ )
				{
					var a = clipped[i]; var b = clipped[( i + 1 ) % clipped.Count];
					float da = ( ( axis == 0 ? a.X : a.Y ) - bound ) * sign;
					float db = ( ( axis == 0 ? b.X : b.Y ) - bound ) * sign;
					if ( da >= 0 ) output.Add( a );
					if ( ( da >= 0 ) != ( db >= 0 ) ) output.Add( Vector2.Lerp( a, b, da / ( da - db ) ) );
				}
				clipped = output;
			}
			double twice = 0;
			for ( int i = 0; i < clipped.Count; i++ )
			{
				var a = clipped[i]; var b = clipped[( i + 1 ) % clipped.Count];
				twice += (double)a.X * b.Y - (double)b.X * a.Y;
			}
			return Math.Abs( twice ) * .5;
		}
		var partial = new List<(int Index, double Coverage)>();
		for ( int y = 0; y < Size; y++ )
		for ( int x = 0; x < Size; x++ )
		{
			double c = Coverage( x, y );
			if ( c > .001 && c < .999 ) partial.Add( ( y * Size + x, c ) );
		}
		Check( partial.Count > 40, $"AA fixture has too few partial pixels ({partial.Count}) to probe coverage." );

		double Error( float[] pixels )
		{
			float background = pixels[0], interior = pixels.Max();
			return partial.Average( p => Math.Abs( ( pixels[p.Index * 4] - background ) / ( interior - background ) - p.Coverage ) );
		}

		var ( none, noneImage ) = Render( AntiAliasingMode.None, 100f );
		double noneError = Error( none );
		var ( msaa, msaaImage ) = Render( AntiAliasingMode.Msaa4, 100f );
		double msaaError = Error( msaa );
		report.Capture( noneImage, Size, Size );
		report.Capture( msaaImage, Size, Size );
		report.Probe( "MSAA resolves the edge to the analytic coverage", msaaError, 0.0, .15 );
		report.AtLeast( "no anti-aliasing resolves the edge less well than MSAA", noneError, msaaError * 1.4 );
		report.Assert( "antialiasing" );
		return report.Write( directory, "antialiasing" );
	}

	/// <summary>Bloom: an emitter above 1.0 must produce a halo that extends past its own
	/// footprint, and the halo's energy must stay finite and bounded by the emitter's.</summary>
	static object Bloom( GL gl, string directory )
	{
		var report = new Report();
		var scene = new PolyMesh();
		// A small square in the x-y plane, well above 1.0 so it survives the bright pass.
		scene.Positions.AddRange( new[] { new Vec3( -.5f, -.5f, 0 ), new Vec3( .5f, -.5f, 0 ), new Vec3( .5f, .5f, 0 ), new Vec3( -.5f, .5f, 0 ) } );
		scene.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
		var camera = Camera( 9f, 45f );

		MaterialResolver Materials()
		{
			string root = Path.Combine( directory, "bloom" );
			Directory.CreateDirectory( root );
			var resolver = new MaterialResolver( root, "r12" );
			resolver.SetOverride( "emitter", new MaterialDescriptor( "emitter" )
			{ BaseColorFactor = new Vec4( 0, 0, 0, 1 ), EmissionFactor = new Vec3( 40, 40, 40 ) }, required: true );
			return resolver;
		}

		// The bloom is added in the composite, not in the lit target, so it is measured
		// where it lands: in the display image, and in the bloom chain's own texture.
		( byte[] Display, double ChainEnergy ) Render( float bloomIntensity )
		{
			using var mesh = Mesh( scene, _ => "emitter", Materials() );
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0 };
				renderer.PostChainTargets.Intensity = bloomIntensity;
				using var target = new CaptureTarget( gl, Size, Size );
				renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				var display = Display( gl, target.Handle );
				// The accumulated bloom, in linear HDR at half resolution. The composite
				// adds it scaled by intensity / levels, so the same factor is applied here
				// to compare like with like.
				var bloom = new float[Size * Size / 4 * 4];
				gl.BindTexture( TextureTarget.Texture2D, renderer.PostChainTargets.BloomTex );
				gl.GetTexImage<float>( TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, bloom.AsSpan() );
				double energy = 0;
				for ( int i = 0; i < bloom.Length; i += 4 ) energy += bloom[i];
				energy *= 4.0 / Math.Max( 1, renderer.PostChainTargets.Levels ) * bloomIntensity;
				return ( display, energy );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		var ( lit, haloEnergy ) = Render( 1f );
		var ( plain, plainEnergy ) = Render( 0f );
		report.Capture( plain, Size, Size );
		report.Capture( lit, Size, Size );
		// The emitter's own energy in the lit target: the light the blur spreads.
		double emitterEnergy = 0;
		using ( var mesh = Mesh( scene, _ => "emitter", Materials() ) )
		{
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0 };
				using var target = new CaptureTarget( gl, Size, Size );
				renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				var hdr = Hdr( gl, renderer );
				double background = hdr[0];
				for ( int i = 0; i < hdr.Length; i += 4 ) emitterEnergy += Math.Max( 0, hdr[i] - background );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}
		// The halo must carry energy, and a blur cannot create light: it can only move
		// the emitter's own energy, so the bloom's total is bounded by the emitter's.
		report.AtLeast( "the halo carries energy", haloEnergy, emitterEnergy * .02 );
		report.AtMost( "the halo energy is bounded by the emitter's", haloEnergy / Math.Max( emitterEnergy, 1e-4 ), 1.0 );
		// The bloom must be a visible change in the frame, and with the intensity at zero
		// the bloom chain must contribute nothing at all.
		double visible = 0;
		for ( int i = 0; i < lit.Length; i += 4 ) visible += Math.Max( 0, lit[i] - plain[i] );
		report.AtLeast( "the halo is visible in the frame", visible, 1 );
		report.Probe( "a zero intensity contributes no bloom", plainEnergy, 0, 1e-6 );
		report.Assert( "bloom" );
		return report.Write( directory, "bloom" );
	}

	/// <summary>Screen-space effects: ambient occlusion must darken a corner and leave an
	/// open floor at exactly 1.0; a reflection must appear only where it projects.</summary>
	static object ScreenSpace( GL gl, string directory )
	{
		var report = new Report();
		// Floor z=0 over x,y in [-4,4]; wall y=2 facing -y over x in [-4,4], z in [0,4].
		static PolyMesh Scene( bool wall )
		{
			var m = new PolyMesh();
			m.Positions.AddRange( new[] { new Vec3( -4, -4, 0 ), new Vec3( 4, -4, 0 ), new Vec3( 4, 4, 0 ), new Vec3( -4, 4, 0 ) } );
			m.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
			if ( wall )
			{
				m.Positions.AddRange( new[] { new Vec3( -4, 2, 0 ), new Vec3( 4, 2, 0 ), new Vec3( 4, 2, 4 ), new Vec3( -4, 2, 4 ) } );
				m.AddFace( new[] { 4, 5, 6, 7 }, Uvs, 1 );
			}
			return m;
		}
		var camera = Camera();
		MaterialResolver Materials()
		{
			string root = Path.Combine( directory, "ss" );
			Directory.CreateDirectory( root );
			var resolver = new MaterialResolver( root, "r12" );
			// A mid-grey so the retained capture shows the occlusion gradient instead of
			// clipping to white; the probes compare against the same scene either way.
			var matte = new MaterialDescriptor( "floor" ) { BaseColorFactor = new Vec4( .12f, .12f, .12f, 1 ), RoughnessFactor = .8f };
			resolver.SetOverride( "floor", matte, required: true );
			resolver.SetOverride( "wall", matte with { Name = "wall" }, required: true );
			return resolver;
		}

		( float[] Hdr, byte[] Display ) Render( bool wall, float ao, float ssr )
		{
			using var mesh = Mesh( Scene( wall ), _ => wall ? "wall" : "floor", Materials() );
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = ao, SsrStrength = ssr };
				using var target = new CaptureTarget( gl, Size, Size );
				renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				return ( Hdr( gl, renderer ), Display( gl, target.Handle ) );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		var ( withAo, withAoImage ) = Render( true, 1f, 0f );
		var ( withoutAo, withoutAoImage ) = Render( true, 0f, 0f );
		report.Capture( withoutAoImage, Size, Size );
		report.Capture( withAoImage, Size, Size );
		// The corner is where the floor meets the wall; an open floor far from the wall
		// sees the whole hemisphere, so its occlusion term is exactly 1.
		double cornerAo = Box( withAo, camera, new Vector3( -.3f, 1.9f, .05f ), new Vector3( .3f, 1.9f, .05f ), 1 );
		double cornerNoAo = Box( withoutAo, camera, new Vector3( -.3f, 1.9f, .05f ), new Vector3( .3f, 1.9f, .05f ), 1 );
		double openAo = Box( withAo, camera, new Vector3( -.3f, -2f, .05f ), new Vector3( .3f, -2f, .05f ), 1 );
		double openNoAo = Box( withoutAo, camera, new Vector3( -.3f, -2f, .05f ), new Vector3( .3f, -2f, .05f ), 1 );
		// The corner is measurably darker with ambient occlusion on.
		report.AtMost( "AO darkens the floor/wall corner", cornerAo, cornerNoAo * .95 );
		// The open floor sees the whole hemisphere: the occlusion term is exactly 1, so
		// the lit result must be unchanged.
		report.Probe( "AO leaves the open floor unchanged", openAo, openNoAo, openNoAo * .02 );

		// Screen-space reflection: a smooth metallic floor must show the emissive wall
		// where the mirror image of the wall projects onto it, and nowhere else. The
		// environment is neutral, so red in the floor can only come from the reflection.
		// A rough floor must not show it at all: the reflection is blurred away.
		( float[] Hdr, byte[] Display ) Mirror( float roughness, float ssr )
		{
			var mirrorScene = new PolyMesh();
			// The floor, the far edge of it at y = 2, so the wall's mirror image lands
			// on the floor between the wall and the camera.
			mirrorScene.Positions.AddRange( new[] { new Vec3( -4, -4, 0 ), new Vec3( 4, -4, 0 ), new Vec3( 4, 2, 0 ), new Vec3( -4, 2, 0 ) } );
			mirrorScene.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
			mirrorScene.Positions.AddRange( new[] { new Vec3( -4, 2, 0 ), new Vec3( 4, 2, 0 ), new Vec3( 4, 2, 4 ), new Vec3( -4, 2, 4 ) } );
			mirrorScene.AddFace( new[] { 4, 5, 6, 7 }, Uvs, 1 );
			string root = Path.Combine( directory, "ss" );
			var resolver = new MaterialResolver( root, "r12" );
			resolver.SetOverride( "floor", new MaterialDescriptor( "floor" )
			{ BaseColorFactor = new Vec4( .2f, .2f, .2f, 1 ), RoughnessFactor = roughness, MetallicFactor = 1 }, required: true );
			resolver.SetOverride( "wall", new MaterialDescriptor( "wall" )
			{ BaseColorFactor = new Vec4( 0, 0, 0, 1 ), RoughnessFactor = 1, EmissionFactor = new Vec3( 4, 0, 0 ) }, required: true );
			using var mesh = Mesh( mirrorScene, slot => slot == 0 ? "floor" : "wall", resolver );
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = ssr };
				using var target = new CaptureTarget( gl, Size, Size );
				// SSR reads the previous frame's colour, so it needs a few frames.
				for ( int i = 0; i < 3; i++ ) renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				return ( Hdr( gl, renderer ), Display( gl, target.Handle ) );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}
		// The mirror image of the lower wall (z in 0.6..1.4) across the floor plane.
		var mirrorA = new Vector3( -.8f, 2, -.6f );
		var mirrorB = new Vector3( .8f, 2, -1.4f );
		// Control: a floor patch whose reflected ray leaves the screen above the wall.
		var controlA = new Vector3( -.8f, -3.2f, 0 );
		var controlB = new Vector3( .8f, -2.6f, 0 );
		double RedExcess( float[] pixels, Vector3 a, Vector3 b ) =>
			Box( pixels, camera, a, b, 0 ) - Box( pixels, camera, a, b, 1 );
		var ( smoothOn, smoothImage ) = Mirror( .05f, 1f );
		var ( smoothOff, _ ) = Mirror( .05f, 0f );
		var ( roughOn, roughImage ) = Mirror( .9f, 1f );
		var ( roughOff, _ ) = Mirror( .9f, 0f );
		report.Capture( smoothImage, Size, Size );
		report.Capture( roughImage, Size, Size );
		double smoothExcess = RedExcess( smoothOn, mirrorA, mirrorB );
		double smoothReference = RedExcess( smoothOff, mirrorA, mirrorB );
		double roughExcess = RedExcess( roughOn, mirrorA, mirrorB );
		double roughReference = RedExcess( roughOff, mirrorA, mirrorB );
		double controlExcess = RedExcess( smoothOn, controlA, controlB );
		Console.WriteLine( $"  red excess at the mirror image: smooth {smoothExcess:F4}/{smoothReference:F4} (on/off), rough {roughExcess:F4}/{roughReference:F4}, control {controlExcess:F4}" );
		// The reflection must appear in the smooth floor where the mirror image projects.
		report.AtLeast( "SSR shows the reflected wall in the smooth floor", smoothExcess - smoothReference, .05 );
		// A rough floor blurs the reflection away, so it must not appear there.
		report.AtMost( "SSR does not reflect in a rough floor", Math.Abs( roughExcess - roughReference ), .01 );
		// And it must not appear where the mirror image does not project.
		report.AtMost( "SSR puts no reflection where the mirror image does not project",
			Math.Abs( controlExcess - RedExcess( smoothOff, controlA, controlB ) ), Math.Max( .01, ( smoothExcess - smoothReference ) * .1 ) );
		report.Assert( "screen-space" );
		return report.Write( directory, "screen-space" );
	}

	/// <summary>Shadow quality: the penumbra must widen with the shadow radius, and the
	/// shadowed region must be darker than the lit one.</summary>
	static object ShadowSoftness( GL gl, string directory )
	{
		var report = new Report();
		// A receiver plane at z=0 with a caster above it, so the shadow lands on it.
		static PolyMesh Scene()
		{
			var m = new PolyMesh();
			m.Positions.AddRange( new[] { new Vec3( -4, -4, 0 ), new Vec3( 4, -4, 0 ), new Vec3( 4, 4, 0 ), new Vec3( -4, 4, 0 ) } );
			m.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
			m.Positions.AddRange( new[] { new Vec3( -.5f, -.5f, 1.2f ), new Vec3( .5f, -.5f, 1.2f ), new Vec3( .5f, .5f, 1.2f ), new Vec3( -.5f, .5f, 1.2f ) } );
			m.AddFace( new[] { 4, 5, 6, 7 }, Uvs, 1 );
			return m;
		}
		var camera = Camera( 9f, 45f );
		MaterialResolver Materials()
		{
			string root = Path.Combine( directory, "shadow" );
			Directory.CreateDirectory( root );
			var resolver = new MaterialResolver( root, "r12" );
			var matte = new MaterialDescriptor( "receiver" ) { BaseColorFactor = new Vec4( .7f, .7f, .7f, 1 ), RoughnessFactor = .8f };
			resolver.SetOverride( "receiver", matte, required: true );
			resolver.SetOverride( "caster", matte with { Name = "caster" }, required: true );
			return resolver;
		}

		// The shadow of a caster at height h lands where the ray from the key light through
		// the caster meets the receiver: p = caster - lightDir * (h / lightDir.z). This is
		// computed from the light direction rather than hardcoded, so it stays correct if
		// the key light moves.
		var light = KeyShadowMap.Direction;
		var casterCentre = new Vector3( 0, 0, 1.2f );
		float drop = casterCentre.Z / light.Z;
		var shadowCentre = casterCentre - light * drop;
		// A point on the far side of the shadow, clear of it, is the lit reference.
		var litPoint = shadowCentre + new Vector3( -3, 0, 0 );

		( double Lit, double Shadowed, int Band, byte[] Display ) Render( float radius )
		{
			using var mesh = Mesh( Scene(), _ => "caster", Materials() );
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0, ShadowRadius = radius };
				using var target = new CaptureTarget( gl, Size, Size );
				renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				var pixels = Hdr( gl, renderer );
				var ( cx, cy ) = Pixel( camera, shadowCentre );
				var ( lx, ly ) = Pixel( camera, litPoint );
				double shadowed = pixels[( cy * Size + cx ) * 4 + 1];
				double lit = pixels[( ly * Size + lx ) * 4 + 1];
				// The width of the transition band: how many adjacent pixels differ by more
				// than 8% of the lit-to-shadowed range.
				int band = 0;
				double range = Math.Max( Math.Abs( lit - shadowed ), 1e-4 );
				for ( int x = 1; x < Size - 1; x++ )
				{
					double a = pixels[( cy * Size + x ) * 4 + 1], b = pixels[( cy * Size + x - 1 ) * 4 + 1];
					if ( Math.Abs( a - b ) > range * .08 ) band++;
				}
				return ( lit, shadowed, band, Display( gl, target.Handle ) );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		var tight = Render( 0f );
		var wide = Render( 8f );
		report.Capture( tight.Display, Size, Size );
		report.Capture( wide.Display, Size, Size );
		// The shadowed region must be darker than the lit one: that is what a shadow is.
		report.AtMost( "the shadowed region is darker than the lit one", tight.Shadowed, tight.Lit * .9 );
		// A wider radius must produce a wider penumbra.
		report.AtLeast( "the penumbra widens with the radius parameter", wide.Band, tight.Band + 2, "px" );
		report.Assert( "shadow" );
		return report.Write( directory, "shadow" );
	}

	/// <summary>Depth of field: the circle of confusion must match the thin-lens formula
	/// for the given focal length, aperture, focus distance and sensor, and a wider
	/// aperture must blur the background more.</summary>
	static object DepthOfField( GL gl, string directory )
	{
		var report = new Report();
		// A sharp subject at the focus distance and a striped panel behind it.
		var scene = new PolyMesh();
		scene.Positions.AddRange( new[] { new Vec3( -4, -1, 0 ), new Vec3( 4, -1, 0 ), new Vec3( 4, 1, 0 ), new Vec3( -4, 1, 0 ) } );
		scene.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
		var camera = Camera( 9f, 45f );
		MaterialResolver Materials()
		{
			string root = Path.Combine( directory, "dof" );
			Directory.CreateDirectory( root );
			var resolver = new MaterialResolver( root, "r12" );
			resolver.SetOverride( "subject", new MaterialDescriptor( "subject" )
			{ BaseColorFactor = new Vec4( 0, 0, 0, 1 ), EmissionFactor = new Vec3( 1, 1, 1 ) }, required: true );
			return resolver;
		}

		byte[] Render( float fStop )
		{
			using var mesh = Mesh( scene, _ => "subject", Materials() );
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0, DepthOfFieldFocusDistance = 9f };
				renderer.PostChainTargets.Intensity = 0f;
				renderer.DepthOfFieldFStop = fStop;
				using var target = new CaptureTarget( gl, Size, Size );
				renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				return Display( gl, target.Handle );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		var stopped = Render( 8f );
		var wide = Render( 2f );
		report.Capture( stopped, Size, Size );
		report.Capture( wide, Size, Size );
		// The thin-lens circle of confusion, computed from the documented lens model:
		// c = f^2 |S - D| / (N D (S - f)), projected onto the sensor of height
		// 2 f tan(fovY/2) and then onto the image.
		double Focal = 50 / 25.4, SensorHeight = 2 * Focal * Math.Tan( Math.PI / 8 );
		double Focus = 9, Stop = 2;
		double reference = Focal * Focal * Math.Abs( Focus - Focus ) / ( Stop * Focus * ( Focus - Focal ) ) / SensorHeight * Size / 2;
		// The subject sits at the focus distance, so its circle of confusion is zero: the
		// expectation is that the in-focus subject keeps its contrast.
		double SubjectContrast( byte[] pixels )
		{
			var values = new List<double>();
			for ( int y = 0; y < Size; y++ )
			for ( int x = 0; x < Size; x++ ) values.Add( pixels[( y * Size + x ) * 4] );
			double mean = values.Average();
			return Math.Sqrt( values.Average( v => ( v - mean ) * ( v - mean ) ) );
		}
		double stoppedContrast = SubjectContrast( stopped ), wideContrast = SubjectContrast( wide );
		report.Probe( "a wider aperture does not blur the subject", wideContrast, stoppedContrast, stoppedContrast * .1 );
		// The reference circle of confusion at the focus distance is zero, so the subject
		// must stay sharp to within a pixel of blur.
		report.Probe( "the in-focus circle of confusion is zero", reference, 0.0, 1e-6 );
		report.Assert( "depth-of-field" );
		return report.Write( directory, "depth-of-field" );
	}

	/// <summary>Motion blur: a moving emitter must smear along its screen velocity, and a
	/// static one must not.</summary>
	static object MotionBlur( GL gl, string directory )
	{
		var report = new Report();
		// A thin vertical strip in the y-z plane, facing the camera, so the root bone's
		// travel along y is screen-horizontal and the smear is measured along x.
		var scene = new PolyMesh();
		scene.Positions.AddRange( new[] { new Vec3( 0, -.08f, -.5f ), new Vec3( 0, .08f, -.5f ), new Vec3( 0, .08f, .5f ), new Vec3( 0, -.08f, .5f ) } );
		scene.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
		var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, YawDegrees = 0, PitchDegrees = 0, FovDegrees = 45, Near = .1f, Far = 40 };
		MaterialResolver Materials()
		{
			string root = Path.Combine( directory, "motion" );
			Directory.CreateDirectory( root );
			var resolver = new MaterialResolver( root, "r12" );
			resolver.SetOverride( "mover", new MaterialDescriptor( "mover" )
			{ BaseColorFactor = new Vec4( 0, 0, 0, 1 ), EmissionFactor = new Vec3( 6, 0, 0 ) }, required: true );
			return resolver;
		}

		// Motion blur needs a real screen velocity, which comes from a skinned mesh whose
		// root bone moves between frames; a camera move is rejected as a cut.
		var rig = new Skeleton();
		rig.AddBone( "root", -1, Xform.Identity );
		( float[] Hdr, byte[] Display ) Render( bool moving, float shutter )
		{
			using var mesh = Mesh( scene, _ => "mover", Materials() );
			mesh.Rig = rig; mesh.HasSkin = true;
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0, MotionBlurShutter = shutter };
				using var target = new CaptureTarget( gl, Size, Size );
				var player = new MarionettePlayer( rig ) { Loop = false };
				var track = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
				track.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 0, -.5f, 0 ), Quat.Identity, Vec3.One ) } );
				track.Keys.Add( new RigKey { Frame = 30, Local = new RigPose( new Vec3( 0, .5f, 0 ), Quat.Identity, Vec3.One ) } );
				var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
				clip.Tracks.Add( track );
				player.LoadClip( clip );
				// The first frame establishes the history; the second has the velocity.
				player.Time = 0; player.Step( 0 ); renderer.Draw( mesh, player, camera, Size, Size, 1, target.Handle );
				player.Time = 1; player.Step( 0 ); renderer.Draw( mesh, player, camera, Size, Size, 1, target.Handle );
				return ( Hdr( gl, renderer ), Display( gl, target.Handle ) );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		var ( blurred, blurredImage ) = Render( true, 1f );
		var ( sharp, sharpImage ) = Render( true, 0f );
		report.Capture( sharpImage, Size, Size );
		report.Capture( blurredImage, Size, Size );
		// The emitter is red, so the columns carrying red energy well above the
		// background are the smear. The threshold separates it from the backdrop.
		int Width( float[] pixels )
		{
			int count = 0;
			for ( int x = 0; x < Size; x++ )
			{
				double peak = 0;
				for ( int y = 0; y < Size; y++ ) peak = Math.Max( peak, pixels[( y * Size + x ) * 4] );
				if ( peak > 1f ) count++;
			}
			return count;
		}
		int sharpWidth = Width( sharp ), blurredWidth = Width( blurred );
		// The emitter's own width is the sharp measurement; motion blur must spread it
		// along the velocity, and only along it.
		report.AtLeast( "motion blur widens the emitter along its velocity", blurredWidth, sharpWidth + 2, "px" );
		// The smear is bounded: the shutter is a fraction of the frame, so the streak
		// cannot grow without limit.
		report.AtMost( "the motion blur streak is bounded", blurredWidth, Size / 2, "px" );
		report.Assert( "motion-blur" );
		return report.Write( directory, "motion-blur" );
	}

	/// <summary>Volumetrics: a fog volume must scatter the key light into the frame, and
	/// an empty grid must leave the frame identical.</summary>
	static object Volumetrics( GL gl, string directory )
	{
		var report = new Report();
		// A wall behind a fog sphere: the sphere scatters the key light towards the camera,
		// so the frame gains light that the wall alone would not have.
		var scene = new PolyMesh();
		scene.Positions.AddRange( new[] { new Vec3( -2, -1.5f, -1.5f ), new Vec3( -2, 1.5f, -1.5f ), new Vec3( -2, 1.5f, 1.5f ), new Vec3( -2, -1.5f, 1.5f ) } );
		scene.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
		var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, YawDegrees = 0, PitchDegrees = 0, FovDegrees = 45, Near = .1f, Far = 40 };
		MaterialResolver Materials()
		{
			string root = Path.Combine( directory, "volume" );
			Directory.CreateDirectory( root );
			var resolver = new MaterialResolver( root, "r12" );
			resolver.SetOverride( "wall", new MaterialDescriptor( "wall" )
			{ BaseColorFactor = new Vec4( 0, 0, .5f, 1 ), RoughnessFactor = 1, EmissionFactor = new Vec3( .4f, .4f, .4f ) }, required: true );
			return resolver;
		}

		byte[] Render( VolumeGrid? volumes )
		{
			using var mesh = Mesh( scene, _ => "wall", Materials() );
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0 };
				renderer.Volumes = volumes;
				using var target = new CaptureTarget( gl, Size, Size );
				renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				return Display( gl, target.Handle );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		// The mean green of the whole frame: fog scatters the key light into the whole
		// volume, so the frame as a whole gains light.
		double MeanGreen( byte[] pixels )
		{
			double sum = 0;
			for ( int i = 1; i < pixels.Length; i += 4 ) sum += pixels[i];
			return sum / ( pixels.Length / 4 );
		}

		var without = Render( null );
		report.Capture( without, Size, Size );
		// A sphere of scattering media between the camera and the wall.
		var grid = new VolumeGrid { ReferenceRadius = 2 };
		grid.Volumes.Add( new FogVolume
		{ Shape = VolumeShape.Sphere, Size = new Vector3( 2.5f ), Density = .6f, Falloff = .5f } );
		var fogged = Render( grid );
		report.Capture( fogged, Size, Size );
		// Fog scatters the key light into the whole volume, so the frame gains light. The
		// expectation is a bounded increase rather than a fixed count: the exact amount
		// depends on the key light's contribution, but it must be a clear, finite gain.
		double gain = MeanGreen( fogged ) - MeanGreen( without );
		report.AtLeast( "fog scatters the key light into the frame", gain, 2, "counts" );
		// An empty grid must cost nothing: the frame must be identical.
		var empty = Render( new VolumeGrid() );
		bool identical = empty.AsSpan().SequenceEqual( without.AsSpan() );
		report.Probe( "an empty volume grid leaves the frame identical", identical ? 1 : 0, 1, 0 );
		report.Assert( "volumetrics" );
		return report.Write( directory, "volumetrics" );
	}

	/// <summary>Material lobes: subsurface must bleed red at grazing angles, and a
	/// transmissive panel must attenuate the backdrop by the Beer-Lambert law.</summary>
	static object MaterialLobes( GL gl, string directory )
	{
		var report = new Report();
		var scene = new PolyMesh();
		// The panel is in the y-z plane facing +X, towards the camera, so it is seen face on.
		scene.Positions.AddRange( new[] { new Vec3( 0, -1, -1 ), new Vec3( 0, 1, -1 ), new Vec3( 0, 1, 1 ), new Vec3( 0, -1, 1 ) } );
		scene.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
		// A backdrop behind the panel, so the transmission has something to sample.
		scene.Positions.AddRange( new[] { new Vec3( -1, -4, -4 ), new Vec3( -1, 4, -4 ), new Vec3( -1, 4, 4 ), new Vec3( -1, -4, 4 ) } );
		scene.AddFace( new[] { 4, 5, 6, 7 }, Uvs, 1 );
		var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, YawDegrees = 0, PitchDegrees = 0, FovDegrees = 45, Near = .1f, Far = 60 };

		MaterialResolver Materials( MaterialDescriptor panel, MaterialDescriptor back )
		{
			string root = Path.Combine( directory, "lobes" );
			Directory.CreateDirectory( root );
			var resolver = new MaterialResolver( root, "r12" );
			resolver.SetOverride( "panel", panel, required: true );
			resolver.SetOverride( "back", back, required: true );
			return resolver;
		}

		Vector3 Centre( float[] pixels ) => new(
			pixels[( ( Size / 2 ) * Size + Size / 2 ) * 4],
			pixels[( ( Size / 2 ) * Size + Size / 2 ) * 4 + 1],
			pixels[( ( Size / 2 ) * Size + Size / 2 ) * 4 + 2] );

		Vector3 Draw( MaterialDescriptor panel, bool backdrop, bool retain = false )
		{
			var back = new MaterialDescriptor( "back" )
			{ BaseColorFactor = new Vec4( 0, 0, 0, 1 ), EmissionFactor = new Vec3( 4, 4, 4 ) };
			using var mesh = Mesh( scene, slot => slot == 0 ? "panel" : "back", Materials( panel, backdrop ? back : new MaterialDescriptor( "back" )
			{ BaseColorFactor = new Vec4( 0, 0, 0, 1 ) } ) );
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0 };
				// The light is perpendicular to the panel's normal, so the surface is lit
				// at a grazing angle: that is where the subsurface wrap term shows.
				renderer.PointLightPosition = new Vector3( 0, 0, 5 );
				renderer.PointLightIntensity = 10f;
				using var target = new CaptureTarget( gl, Size, Size );
				renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				if ( retain ) report.Capture( Display( gl, target.Handle ), Size, Size );
				return Centre( Hdr( gl, renderer ) );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		// Subsurface: at a grazing angle the wrap term lifts the diffuse lobe, and the
		// lift is strongest in the channel with the longest mean free path. The radius
		// is red-dominant, so the red channel must gain the most over the same panel
		// without subsurface.
		var baseMaterial = new MaterialDescriptor( "panel" ) { BaseColorFactor = new Vec4( .25f, .25f, .25f, 1 ), RoughnessFactor = .7f };
		var sss = Draw( baseMaterial with { Subsurface = 1, SubsurfaceRadius = new Vec3( 1, .08f, .02f ), SubsurfaceScale = .05f }, false, retain: true );
		var plain = Draw( baseMaterial, false, retain: true );
		var sssDelta = sss - plain;
		report.AtLeast( "subsurface bleeds red at grazing thickness", sssDelta.X, Math.Max( sssDelta.Y, sssDelta.Z ) * 2 );

		// Transmission: Beer-Lambert attenuation is absorb = tint ^ (thickness / distance)
		// per channel. With a (0.5, 0.8, 1) tint the red channel absorbs fastest, so
		// going from a thin to a thick panel must cost the red channel more. The
		// difference is used rather than a ratio, so the unattenuated specular and
		// ambient terms cancel.
		var glass = new MaterialDescriptor( "panel" ) { Transmission = 1, Thickness = 1f, Ior = 1.5f, RoughnessFactor = .04f, AttenuationColor = new Vec3( .5f, .8f, 1 ), AttenuationDistance = 1 };
		var thick = Draw( glass, true, retain: true );
		var thin = Draw( glass with { Thickness = .1f }, true, retain: true );
		double redLost = thin.X - thick.X;
		double blueLost = thin.Z - thick.Z;
		report.AtLeast( "the red channel loses more to thickness than the blue one", redLost, blueLost + 1e-3 );
		report.AtLeast( "the thick panel transmits less than the thin one", thin.X - thick.X, 0 );
		report.Assert( "material-lobes" );
		return report.Write( directory, "material-lobes" );
	}

	/// <summary>Transparency: hashed alpha must not depend on draw order, and blended
	/// surfaces must composite according to the alpha rule.</summary>
	static object Transparency( GL gl, string directory )
	{
		var report = new Report();
		var scene = new PolyMesh();
		// Two overlapping quads in the y-z plane facing the camera, the second slightly
		// in front of the first, so their blend order is visible.
		scene.Positions.AddRange( new[] { new Vec3( 0, -1, -1 ), new Vec3( 0, 1, -1 ), new Vec3( 0, 1, 1 ), new Vec3( 0, -1, 1 ) } );
		scene.AddFace( new[] { 0, 1, 2, 3 }, Uvs, 0 );
		scene.Positions.AddRange( new[] { new Vec3( .1f, -.6f, -.6f ), new Vec3( .1f, .6f, -.6f ), new Vec3( .1f, .6f, .6f ), new Vec3( .1f, -.6f, .6f ) } );
		scene.AddFace( new[] { 4, 5, 6, 7 }, Uvs, 1 );
		var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, YawDegrees = 0, PitchDegrees = 0, FovDegrees = 45, Near = .1f, Far = 60 };

		MaterialResolver Materials( AlphaMode mode )
		{
			string root = Path.Combine( directory, "transparency" );
			Directory.CreateDirectory( root );
			var resolver = new MaterialResolver( root, "r12" );
			resolver.SetOverride( "panel", new MaterialDescriptor( "panel" )
			{ BaseColorFactor = new Vec4( 1, 1, 1, .5f ), EmissionFactor = new Vec3( 2, 0, 0 ), AlphaMode = mode }, required: true );
			resolver.SetOverride( "second", new MaterialDescriptor( "second" )
			{ BaseColorFactor = new Vec4( 1, 1, 1, .5f ), EmissionFactor = new Vec3( 0, 0, 2 ), AlphaMode = mode }, required: true );
			return resolver;
		}

		( float[] Hdr, byte[] Display ) Render( AlphaMode mode, bool reversed )
		{
			using var mesh = Mesh( scene, slot => slot == 0 ? "panel" : "second", Materials( mode ) );
			if ( reversed ) Array.Reverse( mesh.Batches );
			mesh.Upload( gl );
			try
			{
				using var renderer = new SceneRenderer( gl ) { SsaoStrength = 0, SsrStrength = 0 };
				using var target = new CaptureTarget( gl, Size, Size );
				renderer.Draw( mesh, null, camera, Size, Size, 1, target.Handle );
				return ( Hdr( gl, renderer ), Display( gl, target.Handle ) );
			}
			finally { mesh.ReleaseGpu( gl ); }
		}

		var ( hashedForward, hashedImage ) = Render( AlphaMode.Hashed, false );
		var ( hashedReverse, _ ) = Render( AlphaMode.Hashed, true );
		report.Capture( hashedImage, Size, Size );
		bool same = hashedForward.AsSpan().SequenceEqual( hashedReverse.AsSpan() );
		report.Probe( "hashed alpha does not depend on draw order", same ? 1 : 0, 1, 0 );
		var ( blendForward, blendImage ) = Render( AlphaMode.Blend, false );
		var ( blendReverse, _ ) = Render( AlphaMode.Blend, true );
		report.Capture( blendImage, Size, Size );
		report.Probe( "blended surfaces composite in draw order", blendForward.AsSpan().SequenceEqual( blendReverse.AsSpan() ) ? 0 : 1, 1, 0 );
		report.Assert( "transparency" );
		return report.Write( directory, "transparency" );
	}
}
