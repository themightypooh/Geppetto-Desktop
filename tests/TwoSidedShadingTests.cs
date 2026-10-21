using System.Numerics;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

/// <summary>R4: backface policy, mirrored tangent frames and nonuniform skinned normals.</summary>
internal static class TwoSidedShadingTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		string root = Path.Combine( settings.WorkDirectory, "two-sided" );
		Directory.CreateDirectory( root );

		MaterialResolver Resolver( MaterialDescriptor descriptor )
		{
			var resolver = new MaterialResolver( root, "r4-fixture" );
			resolver.SetOverride( "test", descriptor, required: true );
			return resolver;
		}
		TextureBinding NormalMap( string name, byte r, byte g, byte b )
		{
			PngWriter.WriteFileRgba( Path.Combine( root, name + ".png" ), new byte[] { r, g, b, 255 }, 1, 1 );
			return TextureBinding.ForNormal( new AssetReference( name, name + ".png" ) );
		}
		// Axis-aligned cube; outward=true winds every face counter-clockwise seen from outside.
		static PolyMesh Cube( float h, bool outward )
		{
			var mesh = new PolyMesh();
			for ( int i = 0; i < 8; i++ )
				mesh.Positions.Add( new Vec3( (i & 1) != 0 ? h : -h, (i & 2) != 0 ? h : -h, (i & 4) != 0 ? h : -h ) );
			int[][] faces = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 },
				new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
			var uvs = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };
			foreach ( var f in faces )
				mesh.AddFace( outward ? f : f.Reverse().ToArray(), uvs, 0 );
			return mesh;
		}
		static PolyMesh Plane( bool mirror )
		{
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[] { new Vec3( 0, -1, -1 ), new Vec3( 0, 1, -1 ),
				new Vec3( 0, 1, 1 ), new Vec3( 0, -1, 1 ) } );
			mesh.AddFace( new[] { 0, 1, 2, 3 }, mirror
				? new[] { new Vec2( 1, 0 ), new Vec2( 0, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ) }
				: new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );
			return mesh;
		}
		// A quad whose plane normal is (1,1,0)/sqrt2, optionally pre-scaled in Y.
		static PolyMesh Slanted( float yScale )
		{
			var mesh = new PolyMesh();
			var u = new Vector3( -1, 1, 0 ); var w = new Vector3( 0, 0, 1 );
			foreach ( var p in new[] { -u - w, u - w, u + w, -u + w } )
				mesh.Positions.Add( new Vec3( p.X, p.Y * yScale, p.Z ) );
			mesh.AddFace( new[] { 0, 1, 2, 3 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );
			return mesh;
		}
		static double MeanLuma( byte[] rgb )
		{
			double sum = 0;
			for ( int i = 0; i < rgb.Length; i += 3 ) sum += .2126 * rgb[i] + .7152 * rgb[i + 1] + .0722 * rgb[i + 2];
			return sum / (rgb.Length / 3);
		}
		static double MeanAbsDiff( byte[] a, byte[] b )
		{
			double sum = 0;
			for ( int i = 0; i < a.Length; i++ ) sum += Math.Abs( a[i] - b[i] );
			return sum / a.Length;
		}

		registry.Add( "two-sided", "shader has no viewer-facing normal flip and uses rasterised facing", () =>
		{
			var src = RendererShaders.FragmentSrc;
			Check( !src.Contains( "if (dot(n, v) < 0.0) n = -n;", StringComparison.Ordinal ), "viewer-facing flip is back" );
			Check( src.Contains( "gl_FrontFacing", StringComparison.Ordinal ), "facing is not taken from the rasteriser" );
			Check( src.Contains( "uniform int uDoubleSided;", StringComparison.Ordinal ), "no per-material double-sided switch" );
		} );

		registry.Add( "two-sided", "vmat F_RENDER_BACKFACES selects double-sided; default stays single-sided", () =>
		{
			var (on, onDiag) = VmatParser.Parse( "Layer0\n{\n\tshader \"shaders/complex.shader\"\n\tF_RENDER_BACKFACES 1\n}\n", "on" );
			var (quoted, _) = VmatParser.Parse( "\"Layer0\"\n{\n\t\"shader\" \"complex.shader\"\n\t\"F_RENDER_BACKFACES\" \"1\"\n}\n", "quoted" );
			var (off, _) = VmatParser.Parse( "Layer0\n{\n\tshader \"shaders/complex.shader\"\n\tF_RENDER_BACKFACES 0\n}\n", "off" );
			var (none, _) = VmatParser.Parse( "Layer0\n{\n\tshader \"shaders/complex.shader\"\n}\n", "none" );
			Check( quoted.DoubleSided, "quoted-key vmat did not set DoubleSided" );
			Check( on.DoubleSided, "F_RENDER_BACKFACES 1 did not set DoubleSided" );
			Check( !off.DoubleSided && !none.DoubleSided, "single-sided default changed" );
			Check( onDiag.All( d => !d.Message.Contains( "F_RENDER_BACKFACES" ) ), "supported flag still reported as unsupported" );
			using var mesh = ViewportMesh.FromPolyMesh( Plane( false ), _ => "test" );
			mesh.ApplyMaterials( Resolver( new MaterialDescriptor( "test" ) { DoubleSided = true } ) );
			Check( mesh.Batches.All( b => b.DoubleSided ), "DoubleSided did not reach the draw batch" );
		} );

		string surveyPath = settings.SurveyPath;
		registry.Add( "two-sided", "Survey winding agrees with authored normals (gl_FrontFacing is meaningful)", () =>
		{
			var mesh = ViewportMesh.FromFbx( SurveyPlayerAssembly.Load( surveyPath ), 0, surveyPath );
			int stride = ViewportMesh.FloatsPerVertex, agree = 0, total = 0;
			Vector3 P( uint i ) => new( mesh.Vertices[i * stride], mesh.Vertices[i * stride + 1], mesh.Vertices[i * stride + 2] );
			Vector3 N( uint i ) => new( mesh.Vertices[i * stride + 3], mesh.Vertices[i * stride + 4], mesh.Vertices[i * stride + 5] );
			for ( int t = 0; t + 2 < mesh.Indices.Length; t += 3 )
			{
				uint a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
				var face = Vector3.Cross( P( b ) - P( a ), P( c ) - P( a ) );
				if ( face.LengthSquared() < 1e-12f ) continue;
				total++;
				if ( Vector3.Dot( face, N( a ) + N( b ) + N( c ) ) > 0 ) agree++;
			}
			double ratio = (double)agree / total;
			Console.WriteLine( $"  {agree}/{total} triangles wound with their normals ({ratio:P2})" );
			Check( ratio >= .99, $"only {ratio:P2} of Survey triangles are wound counter-clockwise about their normals" );
		}, () => File.Exists( surveyPath ) ? null : "Survey fixture missing: " + surveyPath, settings.RequireLocalFixtures );

		registry.Add( "two-sided", "hardware EGL: prop model transform places raster geometry and avoids character skinning", () =>
		{
			using var context = new HeadlessGlContext(); var gl = context.Gl;
			using var renderer = new SceneRenderer(gl) { SsaoStrength=0, SsrStrength=0 };
			using var target = new CaptureTarget(gl,128,128);
			var camera = new OrbitCamera { Distance=8, YawDegrees=0, PitchDegrees=0 };
			var rig = new Skeleton(); rig.AddBone("root",-1,Xform.Identity);
			var player = new MarionettePlayer(rig);
			player.LoadClip(RigAnimFile.Read("{\"FrameCount\":31,\"ReferenceProps\":[{\"Name\":\"placed\",\"Position\":\"0,1.8,0\",\"Rotation\":\"0,0,0\",\"Scale\":1}]}"));
			var palette = (Matrix4x4[])typeof(MarionettePlayer).GetField("_gpuBones",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(player)!;
			using var main = ViewportMesh.FromPolyMesh(Plane(false),_=>"test");
			using var propMesh = ViewportMesh.FromPolyMesh(Plane(false),_=>"test");
			main.Rig=rig;
			main.ApplyMaterials(Resolver(new MaterialDescriptor("test") { BaseColorFactor=new Vec4(0,0,.4f,1) }));
			propMesh.ApplyMaterials(Resolver(new MaterialDescriptor("test") { BaseColorFactor=new Vec4(1,0,0,1), EmissionFactor=new Vec3(2,0,0) }));
			using var prop = new PropScene("placed","owned.fbx",propMesh,.3f,true);
			renderer.Props=new[]{prop}; main.Upload(gl); propMesh.Upload(gl);
			try
			{
				foreach(bool skinned in new[]{false,true})
				{
					main.HasSkin=skinned; palette[0]=Matrix4x4.CreateTranslation(0,-2f,0);
					renderer.Draw(main,player,camera,128,128,1,target.Handle);
					var projected=Vector4.Transform(new Vector4(0,1.8f,0,1),camera.ViewMatrix()*camera.ProjectionMatrix(1));
					int x=(int)((projected.X/projected.W*.5f+.5f)*128),y=(int)((projected.Y/projected.W*.5f+.5f)*128);
					gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,renderer.PostChainTargets.ColorFbo);
					var pixel=new float[4];gl.ReadPixels<float>(x,y,1,1,PixelFormat.Rgba,PixelType.Float,pixel.AsSpan());
					Check(pixel[0]>1.5f&&pixel[0]>pixel[1]*3,$"prop not at its model placement (character skin={skinned}, red={pixel[0]})");
					Check(gl.GetError()==GLEnum.NoError,"GL error rendering transformed prop");
				}
			}
			finally { main.ReleaseGpu(gl);propMesh.ReleaseGpu(gl); }
		} );

		registry.Add( "two-sided", "hardware EGL: closed mesh seen from inside is not lit as its exterior", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			// Shading policy is judged without the AgX curve, which lifts darks by design.
			using var renderer = new SceneRenderer( gl ) { View = new ViewSettings { Transform = ViewTransform.Standard } };
			using var target = new CaptureTarget( gl, 128, 128 );
			byte[] Draw( PolyMesh source, bool doubleSided, OrbitCamera camera )
			{
				using var mesh = ViewportMesh.FromPolyMesh( source, _ => "test" );
				mesh.ApplyMaterials( Resolver( new MaterialDescriptor( "test" )
				{ BaseColorFactor = new Vec4( .7f, .7f, .7f, 1 ), RoughnessFactor = .6f, DoubleSided = doubleSided } ) );
				mesh.Upload( gl );
				renderer.Draw( mesh, null, camera, 128, 128, 1, target.Handle );
				var image = new byte[128 * 128 * 3];
				gl.Finish();
				gl.ReadPixels<byte>( 0, 0, 128, 128, PixelFormat.Rgb, PixelType.UnsignedByte, image.AsSpan() );
				mesh.ReleaseGpu( gl );
				Check( gl.GetError() == GLEnum.NoError, "GL error in interior fixture" );
				return image;
			}
			var inside = new OrbitCamera { Target = Vector3.Zero, Distance = .5f, YawDegrees = 30, PitchDegrees = 20, Near = .05f, Far = 40 };
			var outside = new OrbitCamera { Target = Vector3.Zero, Distance = 12, YawDegrees = 30, PitchDegrees = 20, Near = .1f, Far = 40 };
			var room = Draw( Cube( 2, outward: false ), false, inside );          // inward-wound: front faces seen from inside
			var single = Draw( Cube( 2, outward: true ), false, inside );         // backfaces of a single-sided box
			var both = Draw( Cube( 2, outward: true ), true, inside );            // backfaces of a double-sided box
			var exterior = Draw( Cube( 2, outward: true ), false, outside );
			double lRoom = MeanLuma( room ), lSingle = MeanLuma( single ), lBoth = MeanLuma( both ), lExt = MeanLuma( exterior );
			Console.WriteLine( $"  luma room {lRoom:F1}, single-sided interior {lSingle:F1}, double-sided interior {lBoth:F1}, exterior {lExt:F1}; double-vs-room diff {MeanAbsDiff( both, room ):F2}" );
			Check( lRoom > 20, "control room fixture is too dark to judge" );
			Check( lSingle < lRoom * .5, "single-sided interior is lit like a front face" );
			Check( MeanAbsDiff( both, room ) < 1.5, "double-sided backface does not shade as the reversed front face" );
		} );

		registry.Add( "two-sided", "hardware EGL: mirrored-UV twin keeps the normal-map response, front and back", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			using var renderer = new SceneRenderer( gl );
			using var target = new CaptureTarget( gl, 128, 128 );
			byte[] Draw( bool mirror, TextureBinding map, float yaw )
			{
				using var mesh = ViewportMesh.FromPolyMesh( Plane( mirror ), _ => "test" );
				mesh.ApplyMaterials( Resolver( new MaterialDescriptor( "test" )
				{ BaseColorFactor = new Vec4( .7f, .6f, .4f, 1 ), RoughnessFactor = .5f, NormalMap = map, DoubleSided = true } ) );
				mesh.Upload( gl );
				var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, YawDegrees = yaw, PitchDegrees = 0 };
				renderer.Draw( mesh, null, camera, 128, 128, 1, target.Handle );
				var image = new byte[128 * 128 * 3];
				gl.Finish();
				gl.ReadPixels<byte>( 0, 0, 128, 128, PixelFormat.Rgb, PixelType.UnsignedByte, image.AsSpan() );
				mesh.ReleaseGpu( gl );
				Check( gl.GetError() == GLEnum.NoError, "GL error in mirrored fixture" );
				return image;
			}
			// Mirroring U reverses the tangent, so the twin's map must tilt the other way in red
			// to describe the same surface. Green (bitangent) is unchanged.
			var tilt = NormalMap( "r4-tilt", 230, 170, 190 );
			var tiltMirrored = NormalMap( "r4-tilt-mirror", 255 - 230, 170, 190 );
			var flat = NormalMap( "r4-flat", 128, 128, 255 );
			foreach ( float yaw in new[] { 0f, 180f } )
			{
				var regular = Draw( false, tilt, yaw );
				var twin = Draw( true, tiltMirrored, yaw );
				var untilted = Draw( false, flat, yaw );
				double twinDiff = MeanAbsDiff( regular, twin ), tiltEffect = MeanAbsDiff( regular, untilted );
				Console.WriteLine( $"  yaw {yaw}: twin diff {twinDiff:F2}, tilt effect {tiltEffect:F2}" );
				Check( tiltEffect > 1, $"normal map had no effect at yaw {yaw}" );
				Check( twinDiff < tiltEffect * .1, $"mirrored-UV twin diverges at yaw {yaw}" );
			}
		} );

		registry.Add( "two-sided", "hardware EGL: nonuniformly scaled bone does not skew the normal", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			using var renderer = new SceneRenderer( gl );
			using var target = new CaptureTarget( gl, 128, 128 );
			var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 12, YawDegrees = 25, PitchDegrees = 10 };
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			var player = new MarionettePlayer( rig );
			var palette = (Matrix4x4[])typeof( MarionettePlayer ).GetField( "_gpuBones",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic )!.GetValue( player )!;
			palette[0] = Matrix4x4.CreateScale( 1f, 3f, 1f );
			byte[] Draw( ViewportMesh mesh, bool skinned )
			{
				mesh.HasSkin = skinned;
				// Double-sided so the probe is directly lit whichever side the camera sees.
				mesh.ApplyMaterials( Resolver( new MaterialDescriptor( "test" ) { BaseColorFactor = new Vec4( .3f, .3f, .3f, 1 ), RoughnessFactor = .8f, DoubleSided = true } ) );
				mesh.Upload( gl );
				// Exposure 2, not 0.35. The view transform's toe is flat: at 0.35 the whole
				// lit plane spans ten pixel levels, so a skewed normal moved the image by
				// less than the measurement floor (control 0.80 against a floor of 1) and
				// the probe could not see the response it exists to check. At 2 the plane
				// reads 197/255, far from clipping, and the same assertion sees the
				// difference it is meant to see (control 1.25). The assertion thresholds
				// are unchanged.
				renderer.Draw( mesh, skinned ? player : null, camera, 128, 128, 2, target.Handle );
				var image = new byte[128 * 128 * 3];
				gl.Finish();
				gl.ReadPixels<byte>( 0, 0, 128, 128, PixelFormat.Rgb, PixelType.UnsignedByte, image.AsSpan() );
				mesh.ReleaseGpu( gl );
				Check( gl.GetError() == GLEnum.NoError, "GL error in nonuniform fixture" );
				return image;
			}
			using var skinnedMesh = ViewportMesh.FromPolyMesh( Slanted( 1 ), _ => "test" );
			using var reference = ViewportMesh.FromPolyMesh( Slanted( 3 ), _ => "test" );   // correct normal, baked geometry
			using var skewed = ViewportMesh.FromPolyMesh( Slanted( 3 ), _ => "test" );
			var wrong = Vector3.Normalize( new Vector3( 1, 3, 0 ) );                        // normal pushed through the scale
			// Draw re-uploads from BaseVertices (morph path), so the control must edit both copies.
			foreach ( var buffer in new[] { skewed.Vertices, skewed.BaseVertices } )
				for ( int v = 0; v < buffer.Length; v += ViewportMesh.FloatsPerVertex )
				{ buffer[v + 3] = wrong.X; buffer[v + 4] = wrong.Y; buffer[v + 5] = wrong.Z; }
			var skinnedImage = Draw( skinnedMesh, true );
			var referenceImage = Draw( reference, false );
			var skewedImage = Draw( skewed, false );
			int covered = 0;   // pixels clearly unlike the corner (background) pixel
			for ( int i = 0; i < referenceImage.Length; i += 3 )
				if ( Math.Abs( referenceImage[i] - referenceImage[0] ) + Math.Abs( referenceImage[i + 1] - referenceImage[1] ) > 12 ) covered++;
			Check( covered > 300, $"slanted plane is not in view ({covered} px)" );
			double match = MeanAbsDiff( skinnedImage, referenceImage ), control = MeanAbsDiff( skewedImage, referenceImage );
			Console.WriteLine( $"  skinned vs baked {match:F2}; skewed-normal control {control:F2}" );
			Check( control > 1, "fixture cannot tell a skewed normal from the right one" );
			Check( match < control * .2, "skinned normal under nonuniform scale does not match the baked geometry" );
		} );
	}
}
