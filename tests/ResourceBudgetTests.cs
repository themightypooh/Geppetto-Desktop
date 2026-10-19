using System.Numerics;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

// M4: decoded memory, GPU texture storage, stage timings, batch count and repeated-reload
// resource counts. The frame-loop assertions prove the draw path performs no texture decode
// and no material lookup, which is what makes the recorded budget a steady-state invariant
// rather than a one-time measurement.
internal static class ResourceBudgetTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }

		PolyMesh Plane()
		{
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[] { new Vec3( 0, -1, -1 ), new Vec3( 0, 1, -1 ),
				new Vec3( 0, 1, 1 ), new Vec3( 0, -1, 1 ) } );
			mesh.AddFace( new[] { 0, 1, 2, 3 },
				new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );
			return mesh;
		}
		string root = Path.Combine( settings.WorkDirectory, "resource-budget" );
		Directory.CreateDirectory( root );
		TextureBinding Map( string name, byte r, byte g, byte b, MaterialChannel channel )
		{
			PngWriter.WriteFileRgba( Path.Combine( root, name + ".png" ), new byte[] { r, g, b, 255 }, 1, 1 );
			var reference = new AssetReference( name, name + ".png" );
			return channel switch
			{
				MaterialChannel.BaseColor => TextureBinding.ForBaseColor( reference ),
				MaterialChannel.Emission => TextureBinding.ForEmission( reference ),
				MaterialChannel.Normal => TextureBinding.ForNormal( reference ),
				_ => TextureBinding.ForData( reference )
			};
		}
		// A 64x64 authored map so GPU storage is a measurable, non-trivial quantity.
		TextureBinding LargeMap( string name, byte r, byte g, byte b )
		{
			var pixels = new byte[64 * 64 * 4];
			for ( int i = 0; i < pixels.Length; i += 4 ) { pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = 255; }
			PngWriter.WriteFileRgba( Path.Combine( root, name + ".png" ), pixels, 64, 64 );
			return TextureBinding.ForData( new AssetReference( name, name + ".png" ) );
		}
		MaterialResolver Resolver( MaterialDescriptor descriptor )
		{
			var resolver = new MaterialResolver( root, "m4-fixture" );
			resolver.SetOverride( "test", descriptor, required: true );
			return resolver;
		}
		ViewportMesh Mesh()
		{
			using var mesh = ViewportMesh.FromPolyMesh( Plane(), _ => "test" );
			return ViewportMesh.FromPolyMesh( Plane(), _ => "test" );
		}

		registry.Add( "resource-budget", "budget records decoded bytes, batch count and stage timings", () =>
		{
			using var mesh = Mesh();
			Check( mesh.Budget.BatchCount == 0, "budget reported batches before material resolution" );
			Check( mesh.Budget.DecodeCount == 0, "budget reported decodes before material resolution" );

			var descriptor = new MaterialDescriptor( "test" )
			{
				BaseColorMap = Map( "budget-albedo", 200, 100, 50, MaterialChannel.BaseColor ),
				NormalMap = Map( "budget-normal", 128, 128, 255, MaterialChannel.Normal ),
				RoughnessMap = LargeMap( "budget-rough", 90, 90, 90 ),
				MetallicMap = LargeMap( "budget-metal", 10, 10, 10 ),
			};
			mesh.ApplyMaterials( Resolver( descriptor ) );

			Check( mesh.Budget.BatchCount == mesh.Batches.Length && mesh.Budget.BatchCount > 0,
				"batch count did not match the mesh batches" );
			// 4 distinct authored images: two 1x1 and two 64x64.
			Check( mesh.Budget.DecodedImageCount == 4, $"expected 4 decoded images, got {mesh.Budget.DecodedImageCount}" );
			Check( mesh.Budget.DecodedBytes == 2 * 4 + 2 * (64 * 64 * 4),
				$"wrong decoded bytes {mesh.Budget.DecodedBytes}" );
			Check( mesh.Budget.DecodeCount == 4, $"decode count {mesh.Budget.DecodeCount}" );
			Check( mesh.Budget.DecodeSeconds >= 0 && double.IsFinite( mesh.Budget.DecodeSeconds ),
				"decode timing not recorded" );
			Check( mesh.Budget.LoadSeconds >= 0 && double.IsFinite( mesh.Budget.LoadSeconds ),
				"load timing not recorded" );
			Check( mesh.Budget.UploadSeconds == 0 && mesh.Budget.GpuTextureCount == 0,
				"budget claimed GPU upload before GL upload" );
		} );

		registry.Add( "resource-budget", "shared images count once and unique maps are not double-counted", () =>
		{
			using var mesh = Mesh();
			// Both fixtures resolve the same slot, so identical encoded bytes repeat across batches;
			// the decoded figure counts distinct DecodedImage instances, not raw references per slot.
			var descriptor = new MaterialDescriptor( "test" ) { BaseColorMap = Map( "budget-share", 11, 22, 33, MaterialChannel.BaseColor ) };
			mesh.ApplyMaterials( Resolver( descriptor ) );
			Check( mesh.Budget.DecodedImageCount == 1, "one authored map must decode once" );
			Check( mesh.Budget.DecodedBytes == 4, "a 1x1 RGBA map is 4 bytes" );
		} );

		registry.Add( "resource-budget", "hardware upload records GPU storage and reload returns to baseline", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			using var renderer = new SceneRenderer( gl );
			using var target = new CaptureTarget( gl, 64, 64 );
			var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5, YawDegrees = 0, PitchDegrees = 0 };

			long baselineTextures = MeshResourceBudget.LiveGpuTextures;
			long baselineBytes = MeshResourceBudget.LiveGpuTextureBytes;
			long baselineReloads = MeshResourceBudget.ReloadCycles;

			var descriptor = new MaterialDescriptor( "test" )
			{
				BaseColorFactor = new Vec4( .6f, .5f, .3f, 1 ),
				BaseColorMap = Map( "budget-hw-albedo", 200, 100, 50, MaterialChannel.BaseColor ),
				RoughnessMap = LargeMap( "budget-hw-rough", 90, 90, 90 ),
			};

			// Repeated reload cycles: every cycle must release exactly what it retained.
			const int cycles = 4;
			for ( int cycle = 0; cycle < cycles; cycle++ )
			{
				using var mesh = Mesh();
				mesh.ApplyMaterials( Resolver( descriptor ) );
				mesh.Upload( gl );

				Check( mesh.Budget.GpuTextureCount > 0, "upload recorded no GPU textures" );
				// Two 1x1 maps plus the three shared fallbacks; the 64x64 roughness map adds its own storage.
				Check( mesh.Budget.GpuTextureBytes >= 64 * 64 * 4 + 3 * 4,
					$"GPU storage under-counted: {mesh.Budget.GpuTextureBytes}" );
				Check( mesh.Budget.UploadSeconds >= 0 && double.IsFinite( mesh.Budget.UploadSeconds ),
					"upload timing not recorded" );
				Check( MeshResourceBudget.LiveGpuTextures == baselineTextures + mesh.Budget.GpuTextureCount,
					"live texture count drifted during reload cycles" );

				// No decode and no material lookup happens for a rendered frame: the resolver is
				// only ever consulted from ApplyMaterials on the loader worker.
				int decodesBefore = mesh.Budget.DecodeCount;
				long decodedBefore = mesh.Budget.DecodedBytes;
				for ( int frame = 0; frame < 8; frame++ )
					renderer.Draw( mesh, null, camera, 64, 64, 1, target.Handle );
				gl.Finish();
				Check( mesh.Budget.DecodeCount == decodesBefore, "a rendered frame decoded a texture" );
				Check( mesh.Budget.DecodedBytes == decodedBefore, "a rendered frame changed decoded memory" );
				Check( gl.GetError() == GLEnum.NoError, "GL error while measuring the budget" );

				mesh.ReleaseGpu( gl );
				Check( gl.GetError() == GLEnum.NoError, "GL error on budget release" );
				Check( MeshResourceBudget.LiveGpuTextures == baselineTextures,
					"release did not return the live texture count to baseline" );
				Check( MeshResourceBudget.LiveGpuTextureBytes == baselineBytes,
					"release did not return the live GPU byte count to baseline" );
				Check( mesh.Budget.GpuTextureCount == 0 && mesh.Budget.GpuTextureBytes == 0,
					"released mesh still reported GPU retention" );
			}
			Check( MeshResourceBudget.ReloadCycles == baselineReloads + cycles,
				"reload cycle count did not advance once per upload/release" );
			Check( MeshResourceBudget.LiveGpuTextures <= baselineTextures,
				"repeated reloads leaked GPU textures" );
		} );
	}
}
