using System;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class GpuClothTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test, Func<string?>? missing = null ) => registry.Add( "gpu-cloth", name, test, missing );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		Check( "gpu step matches cpu solver", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 5, 0 ) );
			m.Positions.Add( new Vec3( 1, 5, 0 ) );
			m.Positions.Add( new Vec3( 0, 6, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var opt = new ClothSimService.Options { Gravity = new Vec3( 0, -9.81f, 0 ), FloorY = 0f, ConstraintIterations = 4 };
			var cpu = ClothSimService.Create( m, opt );
			var gpu = ClothSimService.Create( m, opt );

			using var ctx = new HeadlessGlContext();
			ClothSimGpuService.Upload( gpu, ctx.Gl );
			ClothSimGpuService.Step( gpu, 0.1f, opt, ctx.Gl );
			// cpu step
			ClothSimService.Step( cpu, 0.1f, opt );
			for ( int i = 0; i < cpu.Positions.Length; i++ )
			{
				Require( MathF.Abs( cpu.Positions[i].x - gpu.Positions[i].x ) < 1e-3f, $"x mismatch {i}" );
				Require( MathF.Abs( cpu.Positions[i].y - gpu.Positions[i].y ) < 1e-3f, $"y mismatch {i}: cpu={cpu.Positions[i].y} gpu={gpu.Positions[i].y}" );
				Require( MathF.Abs( cpu.Positions[i].z - gpu.Positions[i].z ) < 1e-3f, $"z mismatch {i}" );
			}
		}, missing: () => Environment.GetEnvironmentVariable("V3_REQUIRE_EGL") == "1" ? null : "Set V3_REQUIRE_EGL=1 for windowless hardware image probes" );

		Check( "upload download roundtrip", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 1, 2, 3 ) );
			m.Faces.Add( new Face( new[] { 0, 0, 0 } ) );
			var s = ClothSimService.Create( m );
			using var ctx = new HeadlessGlContext();
			ClothSimGpuService.Upload( s, ctx.Gl );
			// mutate cpu positions
			s.Positions[0] = new Vec3( 9, 9, 9 );
			ClothSimGpuService.Download( s, ctx.Gl );
			// download restores gpu (original) values
			Require( MathF.Abs( s.Positions[0].x - 1f ) < 1e-5f );
		}, missing: () => Environment.GetEnvironmentVariable("V3_REQUIRE_EGL") == "1" ? null : "Set V3_REQUIRE_EGL=1 for windowless hardware image probes" );
	}
}
