using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class GpuParticleTurbulenceTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }

		static ParticleState[] SampleStates( int n )
		{
			var states = new ParticleState[n];
			for ( int i = 0; i < n; i++ )
				states[i] = new ParticleState
				{
					Position = new Vector3( (i % 6) * 37f - 90f, (i / 6 % 5) * 53f - 110f, 20f + i * 11f ),
					Velocity = new Vector3( 10f - i, i * 2f - 5f, 30f - i * 3f ),
					Alive = 1, Age = 0.25f * i,
					Rotation = Quaternion.Identity, AngularVelocity = Vector3.Zero, Size = 1f + i * 0.1f,
				};
			return states;
		}

		// One force pass on the GPU; returns post-pass states. Caller owns disposal of program/buffer via the out handles.
		static ParticleState[] GpuPass( GL gl, uint prog, ParticleState[] states, GpuParticleTurbulence.TurbulenceParams p, float dt, float time )
		{
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			GpuParticleTurbulence.Dispatch( gl, prog, buf, states.Length, p, dt, time );
			var back = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
			gl.DeleteBuffer( buf );
			return back;
		}

		static float MaxVelDev( ParticleState[] gpu, ParticleState[] init, GpuParticleTurbulence.TurbulenceParams p, float dt, float time )
		{
			float max = 0;
			for ( int i = 0; i < gpu.Length; i++ )
			{
				Vector3 expect = init[i].Velocity + GpuParticleTurbulence.CpuAcceleration( init[i].Position, p, time ) * dt;
				Vector3 d = gpu[i].Velocity - expect;
				max = MathF.Max( max, MathF.Max( MathF.Abs( d.X ), MathF.Max( MathF.Abs( d.Y ), MathF.Abs( d.Z ) ) ) );
			}
			return max;
		}

		void GlClean( GL gl, uint prog )
		{
			if ( prog != 0 ) gl.DeleteProgram( prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after turbulence test" );
		}

		registry.Add( "gpu-particle-turbulence", "fbm octaves=1 matches single curl, gpu vs cpu", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTurbulence.CreateProgram( gl );
			var p = GpuParticleTurbulence.TurbulenceParams.Default;
			p.Octaves = 1; p.Strength = 30f;
			var init = SampleStates( 24 );
			var gpu = GpuPass( gl, prog, init, p, 1f / 60f, 0.5f );
			float dev = MaxVelDev( gpu, init, p, 1f / 60f, 0.5f );
			Console.WriteLine( $"turb fbm oct1 gpu-vs-cpu maxVelDev={dev:G6}" );
			Check( dev < 2e-3f, $"fbm oct1 gpu-vs-cpu velocity deviation too large: {dev:G6}" );
			// Octave 1 must also equal the lane-3 single curl exactly (CPU side).
			for ( int i = 0; i < init.Length; i++ )
			{
				Vector3 single = ParticleMath.Curl( init[i].Position * p.Frequency + new Vector3( 0, 0, 0.5f * p.Speed ), p.Seed ) * p.Strength;
				Vector3 fbm = GpuParticleTurbulence.CpuAcceleration( init[i].Position, p, 0.5f );
				Check( (single - fbm).Length() < 1e-6f, $"octave-1 fbm != single curl at {i}" );
			}
			GlClean( gl, prog );
		} );

		registry.Add( "gpu-particle-turbulence", "fbm default 4 octaves gpu vs cpu", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTurbulence.CreateProgram( gl );
			var p = GpuParticleTurbulence.TurbulenceParams.Default;
			p.Strength = 30f;
			var init = SampleStates( 32 );
			var gpu = GpuPass( gl, prog, init, p, 1f / 60f, 1.25f );
			float dev = MaxVelDev( gpu, init, p, 1f / 60f, 1.25f );
			Console.WriteLine( $"turb fbm oct4 gpu-vs-cpu maxVelDev={dev:G6}" );
			Check( dev < 5e-3f, $"fbm oct4 gpu-vs-cpu velocity deviation too large: {dev:G6}" );
			GlClean( gl, prog );
		} );

		registry.Add( "gpu-particle-turbulence", "fbm octave behaviour: more octaves change field, amplitude stays bounded", () =>
		{
			var p1 = GpuParticleTurbulence.TurbulenceParams.Default; p1.Octaves = 1; p1.Strength = 30f;
			var p4 = GpuParticleTurbulence.TurbulenceParams.Default; p4.Octaves = 4; p4.Strength = 30f;
			var p8 = GpuParticleTurbulence.TurbulenceParams.Default; p8.Octaves = 8; p8.Strength = 30f;
			var init = SampleStates( 48 );
			float maxDiff14 = 0, maxAmp8 = 0, meanDiff14 = 0;
			for ( int i = 0; i < init.Length; i++ )
			{
				Vector3 a1 = GpuParticleTurbulence.CpuAcceleration( init[i].Position, p1, 0.5f );
				Vector3 a4 = GpuParticleTurbulence.CpuAcceleration( init[i].Position, p4, 0.5f );
				Vector3 a8 = GpuParticleTurbulence.CpuAcceleration( init[i].Position, p8, 0.5f );
				float d = (a4 - a1).Length();
				maxDiff14 = MathF.Max( maxDiff14, d ); meanDiff14 += d;
				maxAmp8 = MathF.Max( maxAmp8, a8.Length() );
				Check( float.IsFinite( a8.X ) && float.IsFinite( a8.Y ) && float.IsFinite( a8.Z ), $"fbm oct8 non-finite at {i}" );
			}
			meanDiff14 /= init.Length;
			Console.WriteLine( $"turb fbm octave behaviour: meanDiff(oct1,oct4)={meanDiff14:G6} maxDiff={maxDiff14:G6} maxAmp(oct8)={maxAmp8:G6}" );
			Check( meanDiff14 > 1f, $"octaves 1->4 barely changed the field (mean diff {meanDiff14:G6}); fbm is a no-op" );
			Check( maxAmp8 < 300f, $"fbm oct8 amplitude exploded: {maxAmp8:G6} at strength 30" );
			// Determinism: same input twice is identical (CPU side).
			Vector3 r1 = GpuParticleTurbulence.CpuAcceleration( init[3].Position, p4, 0.5f );
			Vector3 r2 = GpuParticleTurbulence.CpuAcceleration( init[3].Position, p4, 0.5f );
			Check( r1 == r2, "fbm cpu reference not deterministic" );
		} );

		registry.Add( "gpu-particle-turbulence", "domain warp gpu vs cpu", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTurbulence.CreateProgram( gl );
			var p = GpuParticleTurbulence.TurbulenceParams.Default;
			p.Variant = GpuParticleTurbulence.TurbulenceVariant.DomainWarp;
			p.Strength = 30f; p.WarpAmplitude = 0.5f;
			var init = SampleStates( 32 );
			var gpu = GpuPass( gl, prog, init, p, 1f / 60f, 0.75f );
			float dev = MaxVelDev( gpu, init, p, 1f / 60f, 0.75f );
			Console.WriteLine( $"turb warp gpu-vs-cpu maxVelDev={dev:G6}" );
			Check( dev < 5e-3f, $"warp gpu-vs-cpu velocity deviation too large: {dev:G6}" );
			GlClean( gl, prog );
		} );

		registry.Add( "gpu-particle-turbulence", "warp amplitude 0 equals single curl, 0.5 moves field", () =>
		{
			var p0 = GpuParticleTurbulence.TurbulenceParams.Default;
			p0.Variant = GpuParticleTurbulence.TurbulenceVariant.DomainWarp; p0.WarpAmplitude = 0f; p0.Strength = 30f;
			var p5 = GpuParticleTurbulence.TurbulenceParams.Default;
			p5.Variant = GpuParticleTurbulence.TurbulenceVariant.DomainWarp; p5.WarpAmplitude = 0.5f; p5.Strength = 30f;
			var init = SampleStates( 32 );
			float maxDiff = 0;
			for ( int i = 0; i < init.Length; i++ )
			{
				Vector3 w0 = GpuParticleTurbulence.CpuAcceleration( init[i].Position, p0, 0.5f );
				Vector3 single = ParticleMath.Curl( init[i].Position * p0.Frequency + new Vector3( 0, 0, 0.5f * p0.Speed ), p0.Seed ) * p0.Strength;
				Check( (w0 - single).Length() < 1e-5f, $"warp 0 != single curl at {i}" );
				Vector3 w5 = GpuParticleTurbulence.CpuAcceleration( init[i].Position, p5, 0.5f );
				maxDiff = MathF.Max( maxDiff, (w5 - w0).Length() );
			}
			Console.WriteLine( $"turb warp effect: maxDiff(warp0.5, warp0)={maxDiff:G6}" );
			Check( maxDiff > 1f, $"warp amplitude 0.5 barely moved the field (max diff {maxDiff:G6})" );
		} );

		registry.Add( "gpu-particle-turbulence", "time mode continuous: adjacent-t change far smaller than seconds-apart change", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTurbulence.CreateProgram( gl );
			var p = GpuParticleTurbulence.TurbulenceParams.Default;
			p.Strength = 30f;
			var init = SampleStates( 32 );
			float dt = 1f / 60f;
			var gpu0 = GpuPass( gl, prog, init, p, dt, 2.0f );
			var gpuAdj = GpuPass( gl, prog, init, p, dt, 2.0f + dt );
			var gpuFar = GpuPass( gl, prog, init, p, dt, 7.0f );
			float maxAdj = 0, maxFar = 0;
			for ( int i = 0; i < init.Length; i++ )
			{
				maxAdj = MathF.Max( maxAdj, (gpuAdj[i].Velocity - gpu0[i].Velocity).Length() );
				maxFar = MathF.Max( maxFar, (gpuFar[i].Velocity - gpu0[i].Velocity).Length() );
			}
			Console.WriteLine( $"turb time continuity (gpu): maxAdjacentFrameChange={maxAdj:G6} maxSecondsApartChange={maxFar:G6}" );
			Check( maxAdj < 0.05f, $"adjacent-frame velocity jump too large (popping?): {maxAdj:G6}" );
			Check( maxFar > 0.1f, $"field does not actually vary over seconds (far change {maxFar:G6})" );
			Check( maxAdj < 0.5f * maxFar, $"adjacent change {maxAdj:G6} not far smaller than seconds-apart change {maxFar:G6}" );
			GlClean( gl, prog );
		} );

		registry.Add( "gpu-particle-turbulence", "time mode varies over seconds, gpu vs cpu at several times", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTurbulence.CreateProgram( gl );
			var p = GpuParticleTurbulence.TurbulenceParams.Default;
			p.Variant = GpuParticleTurbulence.TurbulenceVariant.DomainWarp;
			p.Strength = 30f;
			var init = SampleStates( 24 );
			float worst = 0;
			foreach ( float t in new[] { 0f, 1.7f, 5.0f } )
			{
				var gpu = GpuPass( gl, prog, init, p, 1f / 60f, t );
				worst = MathF.Max( worst, MaxVelDev( gpu, init, p, 1f / 60f, t ) );
			}
			Console.WriteLine( $"turb warp time sweep gpu-vs-cpu worstMaxVelDev={worst:G6}" );
			Check( worst < 5e-3f, $"warp time sweep gpu-vs-cpu deviation too large: {worst:G6}" );
			GlClean( gl, prog );
		} );

		registry.Add( "gpu-particle-turbulence", "force pass touches velocity only, dead slots untouched", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTurbulence.CreateProgram( gl );
			var p = GpuParticleTurbulence.TurbulenceParams.Default;
			p.Strength = 30f;
			var dead = new ParticleState { Position = new Vector3( 1e6f, -1e6f, 3e5f ), Age = 5, Velocity = new Vector3( 999, -999, 999 ), Alive = 0, Rotation = new Quaternion( 0.3f, 0.4f, 0.5f, 0.6f ), AngularVelocity = new Vector3( 7, 8, 9 ), Size = 42 };
			var live = new ParticleState { Position = new Vector3( 10, -20, 100 ), Velocity = new Vector3( 1, 2, 3 ), Alive = 1, Age = 2, Rotation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, 0.7f ), AngularVelocity = new Vector3( 1, 1, 1 ), Size = 3 };
			var gpu = GpuPass( gl, prog, new[] { dead, live }, p, 1f / 60f, 0.5f );
			Check( gpu[0].Position == dead.Position && gpu[0].Velocity == dead.Velocity && gpu[0].Age == dead.Age && gpu[0].Rotation == dead.Rotation && gpu[0].AngularVelocity == dead.AngularVelocity && gpu[0].Size == dead.Size, "dead slot changed by turbulence pass" );
			Check( gpu[1].Position == live.Position && gpu[1].Age == live.Age && gpu[1].Rotation == live.Rotation && gpu[1].AngularVelocity == live.AngularVelocity && gpu[1].Size == live.Size, "live particle position/age/rotation/size changed by velocity-only pass" );
			Check( gpu[1].Velocity != live.Velocity, "live particle velocity did not change under turbulence" );
			GlClean( gl, prog );
		} );

		registry.Add( "gpu-particle-turbulence", "ssbo layout matches parity shader: stride 16, position at offset 0", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTurbulence.CreateProgram( gl );
			var states = SampleStates( 3 );
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			var raw = GpuParticleSystem.Download( gl, buf, states.Length );
			gl.DeleteBuffer( buf );
			Check( raw.Length == states.Length * GpuParticleSystem.StrideFloats, $"raw length {raw.Length} != {states.Length * GpuParticleSystem.StrideFloats}" );
			Check( GpuParticleSystem.StrideFloats == 16 && GpuParticleSystem.PosOffset == 0, "parity stride/offset changed under this lane" );
			Check( raw[0] == states[0].Position.X && raw[1] == states[0].Position.Y && raw[2] == states[0].Position.Z, "position not at offset 0" );
			Check( raw[GpuParticleSystem.VelOffset] == states[0].Velocity.X && raw[GpuParticleSystem.AgeOffset] == states[0].Age, "vel/age offsets wrong" );
			GlClean( gl, prog );
		} );

		registry.Add( "gpu-particle-turbulence", "invalid params throw loudly, zero particles does not crash", () =>
		{
			void Throws( Action a, string what )
			{
				try { a(); }
				catch ( ArgumentException ) { return; }
				throw new Exception( $"expected ArgumentException for {what}" );
			}
			var bad = GpuParticleTurbulence.TurbulenceParams.Default;
			bad.Octaves = 0; Throws( () => bad.Validate(), "octaves 0" );
			bad = GpuParticleTurbulence.TurbulenceParams.Default;
			bad.Octaves = 9; Throws( () => bad.Validate(), "octaves 9" );
			bad = GpuParticleTurbulence.TurbulenceParams.Default;
			bad.Lacunarity = 0; Throws( () => bad.Validate(), "lacunarity 0" );
			bad = GpuParticleTurbulence.TurbulenceParams.Default;
			bad.Gain = -0.1f; Throws( () => bad.Validate(), "gain negative" );
			bad = GpuParticleTurbulence.TurbulenceParams.Default;
			bad.Frequency = 0; Throws( () => bad.Validate(), "frequency 0" );
			bad = GpuParticleTurbulence.TurbulenceParams.Default;
			bad.WarpAmplitude = -1; Throws( () => bad.Validate(), "warp negative" );
			bad = GpuParticleTurbulence.TurbulenceParams.Default;
			bad.Strength = -1; Throws( () => bad.Validate(), "strength negative" );

			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTurbulence.CreateProgram( gl );
			uint buf = GpuParticleSystem.UploadStates( gl, Array.Empty<ParticleState>() );
			GpuParticleTurbulence.Dispatch( gl, prog, buf, 0, GpuParticleTurbulence.TurbulenceParams.Default, 1f / 60f, 0 );
			gl.DeleteBuffer( buf );
			GlClean( gl, prog );
		} );
	}
}
