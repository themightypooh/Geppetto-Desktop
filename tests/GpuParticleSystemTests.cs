using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class GpuParticleSystemTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		registry.Add( "gpu-particle", "compute shader updates positions and keeps particles in bounds", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSystem.CreateProgram( gl );
			uint buf = GpuParticleSystem.Upload( gl, 1024, 99 );
			var before = GpuParticleSystem.Download( gl, buf, 1024 );
			GpuParticleSystem.Dispatch( gl, prog, buf, 1024, 0.5f, new Vector3( 2, 2, 2 ) );
			var after = GpuParticleSystem.Download( gl, buf, 1024 );
			Check( !before.SequenceEqual( after ), "dispatch did not change particle data" );
			for ( int i = 0; i < 1024; i++ )
			{
				int b = i * GpuParticleSystem.StrideFloats;
				for ( int k = 0; k < 3; k++ )
					Check( MathF.Abs( after[b + k] ) <= 2.0001f, $"particle {i} axis {k} out of bounds: {after[b + k]}" );
			}
			GpuParticleSystem.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after particle compute" );
		} );
		registry.Add( "gpu-particle", "zero particles does not crash", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSystem.CreateProgram( gl );
			uint buf = GpuParticleSystem.Upload( gl, 0, 7 );
			GpuParticleSystem.Dispatch( gl, prog, buf, 0, 0.1f, new Vector3( 1, 1, 1 ) );
			var data = GpuParticleSystem.Download( gl, buf, 0 );
			Check( data.Length == 0, "zero-particle download should be empty" );
			GpuParticleSystem.Dispose( gl, buf, prog );
		} );
	}
}