using System.Numerics;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class MaterialChannelGpuTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		PolyMesh Plane( bool mirror = false, bool degenerate = false )
		{
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[] { new Vec3( 0, -1, -1 ), new Vec3( 0, 1, -1 ),
				new Vec3( 0, 1, 1 ), new Vec3( 0, -1, 1 ) } );
			mesh.AddFace( new[] { 0, 1, 2, 3 }, degenerate ? new[] { Vec2.Zero, Vec2.Zero, Vec2.Zero, Vec2.Zero } :
				mirror ? new[] { new Vec2( 1, 0 ), new Vec2( 0, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ) } :
				new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );
			return mesh;
		}
		string root = Path.Combine( settings.WorkDirectory, "material-channels" );
		Directory.CreateDirectory( root );
		TextureBinding Map( string name, byte r, byte g, byte b, MaterialChannel channel,
			NormalMapConvention convention = NormalMapConvention.OpenGL )
		{
			PngWriter.WriteFileRgba( Path.Combine( root, name + ".png" ), new byte[] { r, g, b, 255 }, 1, 1 );
			var reference = new AssetReference( name, name + ".png" );
			return channel switch
			{
				MaterialChannel.BaseColor => TextureBinding.ForBaseColor( reference ),
				MaterialChannel.Emission => TextureBinding.ForEmission( reference ),
				MaterialChannel.Normal => TextureBinding.ForNormal( reference, convention: convention ),
				_ => TextureBinding.ForData( reference )
			};
		}
		MaterialResolver Resolver( MaterialDescriptor descriptor )
		{
			var resolver = new MaterialResolver( root, "m3-fixture" );
			resolver.SetOverride( "test", descriptor, required: true );
			return resolver;
		}
		registry.Add( "material-channel", "bake tangents split mirrored UVs and survive degenerate UVs", () =>
		{
			using var regular = ViewportMesh.FromPolyMesh( Plane(), _ => "test" );
			using var mirror = ViewportMesh.FromPolyMesh( Plane( mirror: true ), _ => "test" );
			using var degenerate = ViewportMesh.FromPolyMesh( Plane( degenerate: true ), _ => "test" );
			Check( ViewportMesh.FloatsPerVertex == 20, "tangent vec4 stride" );
			Check( regular.Vertices[19] == -mirror.Vertices[19], "mirrored island handedness" );
			foreach ( var mesh in new[] { regular, mirror, degenerate } )
				for ( int c = 0; c < 4; c++ )
				{
					var tangent = new Vector3( mesh.Vertices[c * 20 + 16], mesh.Vertices[c * 20 + 17], mesh.Vertices[c * 20 + 18] );
					Check( float.IsFinite( tangent.Length() ) && tangent.Length() > .99f && tangent.Length() < 1.01f,
						"tangent fallback must be finite and unit length" );
				}
		} );
		registry.Add( "material-channel", "reject unsupported UV set and wrong channel color space before GPU upload", () =>
		{
			var map = Map( "metadata", 128, 128, 255, MaterialChannel.Normal );
			foreach ( var bad in new[] { map with { UvSetIndex = 1 }, map with { ColorSpace = ColorSpaceInterpretation.Srgb } } )
			{
				using var mesh = ViewportMesh.FromPolyMesh( Plane(), _ => "test" );
				try { mesh.ApplyMaterials( Resolver( new MaterialDescriptor( "test" ) { NormalMap = bad } ) );
					throw new Exception( "unsupported texture metadata was accepted" ); }
				catch ( NotSupportedException ) { }
			}
		} );
		registry.Add( "material-channel", "hardware EGL probes all maps, fallbacks, convention and reload", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			using var renderer = new SceneRenderer( gl );
			using var target = new CaptureTarget( gl, 128, 128 );
			var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, YawDegrees = 0, PitchDegrees = 0 };
			byte[] Draw( MaterialDescriptor descriptor, bool mirror = false, bool degenerate = false )
			{
				using var mesh = ViewportMesh.FromPolyMesh( Plane( mirror, degenerate ), _ => "test" );
				mesh.ApplyMaterials( Resolver( descriptor ) );
				mesh.Upload( gl );
				renderer.Draw( mesh, null, camera, 128, 128, 1, target.Handle );
				var image = new byte[128 * 128 * 3];
				gl.Finish();
				gl.ReadPixels<byte>( 0, 0, 128, 128, PixelFormat.Rgb, PixelType.UnsignedByte, image.AsSpan() );
				mesh.ReleaseGpu( gl );
				Check( gl.GetError() == GLEnum.NoError, "GL error after draw/release" );
				return image;
			}
			static int Difference( byte[] a, byte[] b )
			{
				int off = (64 * 128 + 64) * 3;
				return Math.Abs( a[off] - b[off] ) + Math.Abs( a[off + 1] - b[off + 1] ) + Math.Abs( a[off + 2] - b[off + 2] );
			}
			var baseMat = new MaterialDescriptor( "test" ) { BaseColorFactor = new Vec4( .6f, .5f, .3f, 1 ),
				RoughnessFactor = .7f, MetallicFactor = 1f, EmissionFactor = new Vec3( .5f, .1f, .05f ) };
			var flat = Draw( baseMat );
			var flatNormal = Map( "flat", 128, 128, 255, MaterialChannel.Normal );
			Check( Difference( flat, Draw( baseMat with { NormalMap = flatNormal } ) ) <= 3, "flat normal is not neutral" );
			var tilt = Map( "tilt", 240, 128, 180, MaterialChannel.Normal );
			Check( Difference( flat, Draw( baseMat with { NormalMap = tilt } ) ) > 5, "tilted normal had no lighting effect" );
			var yTilt = Map( "ytilt", 128, 240, 180, MaterialChannel.Normal );
			var dx = yTilt with { NormalConvention = NormalMapConvention.DirectX };
			Check( Difference( Draw( baseMat with { NormalMap = yTilt } ), Draw( baseMat with { NormalMap = dx } ) ) > 3,
				"green-channel convention did not affect lighting" );
			Check( Difference( Draw( baseMat with { NormalMap = tilt } ), Draw( baseMat with { NormalMap = tilt }, mirror: true ) ) > 3,
				"mirrored tangent frame did not affect lighting" );
			Check( Draw( baseMat with { NormalMap = tilt }, degenerate: true ).Length == flat.Length, "degenerate UV failed draw" );
			var lowRough = Map( "rough", 25, 255, 255, MaterialChannel.Roughness );
			var narrow = Draw( baseMat with { RoughnessMap = lowRough } );
			Check( Difference( flat, narrow ) > 2, "roughness red channel unused" );
			int Luma( byte[] image, int x, int y )
			{
				int at = (y * 128 + x) * 3;
				return image[at] + image[at + 1] + image[at + 2];
			}
			// The highlight response must change across the face, not merely shift a constant tint.
			Check( Math.Abs( (Luma( narrow, 64, 64 ) - Luma( flat, 64, 64 )) -
				(Luma( narrow, 79, 64 ) - Luma( flat, 79, 64 )) ) > 2,
				"roughness did not change spatial highlight shape" );
			var zeroMetal = Map( "metal", 0, 255, 255, MaterialChannel.Metallic );
			Check( Difference( flat, Draw( baseMat with { MetallicMap = zeroMetal } ) ) > 5, "metallic red channel unused" );
			var glow = Map( "glow", 100, 0, 0, MaterialChannel.Emission );
			Check( Difference( flat, Draw( baseMat with { EmissionMap = glow } ) ) > 3, "sRGB emission channel unused" );
			var occluded = Map( "occlusion", 0, 255, 255, MaterialChannel.AmbientOcclusion );
			Check( Difference( flat, Draw( baseMat with { AmbientOcclusionMap = occluded } ) ) > 2,
				"linear AO mask did not attenuate ambient light" );
			Check( Difference( flat, Draw( baseMat ) ) == 0, "released/reuploaded snapshot changed pixels" );
		} );
		registry.Add( "material-channel", "nonuniform bone palette transforms mapped tangent frame", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			using var renderer = new SceneRenderer( gl );
			using var target = new CaptureTarget( gl, 128, 128 );
			var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, YawDegrees = 0, PitchDegrees = 0 };
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			var player = new MarionettePlayer( rig );
			// A direct palette fixture exercises the actual renderer upload without a clip or second renderer.
			var palette = (Matrix4x4[])typeof( MarionettePlayer ).GetField( "_gpuBones",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic )!.GetValue( player )!;
			palette[0] = Matrix4x4.CreateScale( 1f, 1.5f, .65f ) * Matrix4x4.CreateRotationY( .25f );
			byte[] Draw( TextureBinding? map )
			{
				using var mesh = ViewportMesh.FromPolyMesh( Plane(), _ => "test" );
				mesh.HasSkin = true;
				mesh.ApplyMaterials( Resolver( new MaterialDescriptor( "test" )
				{
					BaseColorFactor = new Vec4( .7f, .6f, .4f, 1 ), RoughnessFactor = .7f, NormalMap = map
				} ) );
				mesh.Upload( gl );
				renderer.Draw( mesh, player, camera, 128, 128, 1, target.Handle );
				var image = new byte[128 * 128 * 3];
				gl.Finish();
				gl.ReadPixels<byte>( 0, 0, 128, 128, PixelFormat.Rgb, PixelType.UnsignedByte, image.AsSpan() );
				mesh.ReleaseGpu( gl );
				Check( gl.GetError() == GLEnum.NoError, "nonuniform skinned draw GL error" );
				return image;
			}
			var flat = Draw( null );
			var neutral = Draw( Map( "skin-flat", 128, 128, 255, MaterialChannel.Normal ) );
			var tilt = Draw( Map( "skin-tilt", 200, 160, 180, MaterialChannel.Normal ) );
			int off = (64 * 128 + 64) * 3;
			int Difference( byte[] a, byte[] b ) => Enumerable.Range( 0, 3 ).Sum( i => Math.Abs( a[off + i] - b[off + i] ) );
			Check( Difference( flat, neutral ) <= 3, "flat map changed skinned normal" );
			Check( Difference( flat, tilt ) > 4, "mapped normal did not respond to nonuniformly skinned frame" );
		} );
	}
}
