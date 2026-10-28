using System;
using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU over-life tests (particles wave-6 lane P46, group "gpu-overlife").
/// Headless EGL only. GPU-vs-CPU agreement is EPSILON agreement, never
/// bit-identical (deep-research correction, lane-list section 18). GL error
/// is checked clean after every GPU test. The landed parity shader is used
/// only through its public UploadStates/DownloadStates helpers — never edited.
/// </summary>
internal static class GpuParticleOverLifeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( "gpu-overlife: " + reason ); }
		const float Eps = 1e-4f;

		static GpuParticleOverLife.GpuOverLifeCurves Curves() => new()
		{
			SizeKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeKey>
				{ new( 0, 1 ), new( 0.5f, 2 ), new( 1, 0.5f ) },
			SpeedKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeKey>
				{ new( 0, 1 ), new( 1, 0.25f ) },
			ColorKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeColorKey>
				{ new( 0, 1, 0, 0, 1 ), new( 0.5f, 0, 1, 0, 0.5f ), new( 1, 0, 0, 1, 0 ) },
		};

		static void CheckNear( float actual, float expected, float eps, string what )
		{
			if ( MathF.Abs( actual - expected ) > eps )
				throw new Exception( $"gpu-overlife: {what}: got {actual}, want {expected} (eps {eps})" );
		}

		static float MaxDev( float a, float b ) => MathF.Abs( a - b );

		registry.Add( "gpu-overlife", "cpu reference: endpoints, midpoints, clamp", () =>
		{
			var c = Curves();
			// Size: (0,1),(0.5,2),(1,0.5). Speed: (0,1),(1,0.25). Colour: red -> green -> blue.
			CheckNear( c.EvaluateNormalized( 0 ).SizeMultiplier, 1, Eps, "size t=0" );
			CheckNear( c.EvaluateNormalized( 0.25f ).SizeMultiplier, 1.5f, Eps, "size t=0.25 midpoint" );
			CheckNear( c.EvaluateNormalized( 0.5f ).SizeMultiplier, 2, Eps, "size t=0.5" );
			CheckNear( c.EvaluateNormalized( 0.75f ).SizeMultiplier, 1.25f, Eps, "size t=0.75 midpoint" );
			CheckNear( c.EvaluateNormalized( 1 ).SizeMultiplier, 0.5f, Eps, "size t=1" );
			CheckNear( c.EvaluateNormalized( 0.25f ).SpeedMultiplier, 0.8125f, Eps, "speed t=0.25" );
			CheckNear( c.EvaluateNormalized( 0.5f ).SpeedMultiplier, 0.625f, Eps, "speed t=0.5" );
			CheckNear( c.EvaluateNormalized( 0.75f ).SpeedMultiplier, 0.4375f, Eps, "speed t=0.75" );
			CheckNear( c.EvaluateNormalized( 1 ).SpeedMultiplier, 0.25f, Eps, "speed t=1" );
			var col25 = c.EvaluateNormalized( 0.25f ).Color;
			CheckNear( col25.X, 0.5f, Eps, "colour R t=0.25" ); CheckNear( col25.Y, 0.5f, Eps, "colour G t=0.25" );
			CheckNear( col25.Z, 0, Eps, "colour B t=0.25" ); CheckNear( col25.W, 0.75f, Eps, "colour A t=0.25" );
			var col75 = c.EvaluateNormalized( 0.75f ).Color;
			CheckNear( col75.X, 0, Eps, "colour R t=0.75" ); CheckNear( col75.Y, 0.5f, Eps, "colour G t=0.75" );
			CheckNear( col75.Z, 0.5f, Eps, "colour B t=0.75" ); CheckNear( col75.W, 0.25f, Eps, "colour A t=0.75" );
			// Clamp past the ends: t < 0 reads the first key, t > 1 the last key.
			CheckNear( c.EvaluateNormalized( -0.5f ).SizeMultiplier, 1, Eps, "size clamp below 0" );
			CheckNear( c.EvaluateNormalized( 1.5f ).SizeMultiplier, 0.5f, Eps, "size clamp above 1" );
			CheckNear( c.EvaluateNormalized( 1.5f ).Color.Z, 1, Eps, "colour clamp above 1 (blue)" );
			CheckNear( c.Evaluate( 3, 2 ).SizeMultiplier, 0.5f, Eps, "age 3 / lifetime 2 clamps to final key" );
			CheckNear( c.Evaluate( -1, 2 ).SizeMultiplier, 1, Eps, "negative age clamps to first key" );
			// Absent curves are identity.
			var none = new GpuParticleOverLife.GpuOverLifeCurves();
			CheckNear( none.EvaluateNormalized( 0.37f ).SizeMultiplier, 1, Eps, "absent size = 1" );
			CheckNear( none.EvaluateNormalized( 0.37f ).SpeedMultiplier, 1, Eps, "absent speed = 1" );
			Check( none.EvaluateNormalized( 0.37f ).Color == new Vector4( 1, 1, 1, 1 ), "absent colour = opaque white" );
			// Single-key curve is constant.
			var single = new GpuParticleOverLife.GpuOverLifeCurves
			{
				SizeKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeKey> { new( 0.3f, 2.5f ) },
			};
			CheckNear( single.EvaluateNormalized( 0 ).SizeMultiplier, 2.5f, Eps, "single-key size at 0" );
			CheckNear( single.EvaluateNormalized( 1 ).SizeMultiplier, 2.5f, Eps, "single-key size at 1" );
		} );

		registry.Add( "gpu-overlife", "cpu validation rejects bad curves loudly", () =>
		{
			static void ExpectThrow( GpuParticleOverLife.GpuOverLifeCurves c, string what )
			{
				try { c.Validate(); }
				catch ( ArgumentException ) { return; }
				throw new Exception( "gpu-overlife: validation accepted " + what );
			}
			ExpectThrow( new GpuParticleOverLife.GpuOverLifeCurves
				{ SizeKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeKey> { new( 0.5f, 1 ), new( 0.2f, 2 ) } }, "unsorted keys" );
			ExpectThrow( new GpuParticleOverLife.GpuOverLifeCurves
				{ SizeKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeKey> { new( 0, 1 ), new( 0, 2 ) } }, "duplicate times" );
			ExpectThrow( new GpuParticleOverLife.GpuOverLifeCurves
				{ SizeKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeKey>() }, "empty key list" );
			ExpectThrow( new GpuParticleOverLife.GpuOverLifeCurves
				{ SizeKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeKey> { new( 0, -1 ) } }, "negative size" );
			ExpectThrow( new GpuParticleOverLife.GpuOverLifeCurves
				{ SpeedKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeKey> { new( 1.2f, 1 ) } }, "time above 1" );
			ExpectThrow( new GpuParticleOverLife.GpuOverLifeCurves
				{ ColorKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeColorKey> { new( 0, 1.5f, 0, 0, 1 ) } }, "colour channel above 1" );
			// BuildCurveBuffer validates too, so a bad curve can never reach the shader.
			try { _ = GpuParticleOverLife.BuildCurveBuffer( new GpuParticleOverLife.GpuOverLifeCurves
				{ SizeKeys = new System.Collections.Generic.List<GpuParticleOverLife.GpuOverLifeKey>() } ); }
			catch ( ArgumentException ) { return; }
			throw new Exception( "gpu-overlife: BuildCurveBuffer accepted an empty key list" );
		} );

		registry.Add( "gpu-overlife", "gpu matches cpu reference at five normalized ages", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleOverLife.CreateProgram( gl );
			var curves = Curves();
			float lifetime = 2f;
			float[] ages = { 0, 0.5f, 1f, 1.5f, 2f }; // t = 0, 0.25, 0.5, 0.75, 1
			var states = new ParticleState[ages.Length];
			for ( int i = 0; i < ages.Length; i++ )
				states[i] = new ParticleState { Position = new Vector3( i, 0, 0 ), Age = ages[i], Velocity = new Vector3( 4, -2, 0 ), Alive = 1, Rotation = Quaternion.Identity, Size = 2 };
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint curveBuf = GpuParticleOverLife.CreateCurveBuffer( gl, curves );
			uint colorBuf = GpuParticleOverLife.CreateColorBuffer( gl, states.Length );
			GpuParticleOverLife.Dispatch( gl, prog, stateBuf, curveBuf, colorBuf, states.Length, lifetime );
			var back = GpuParticleSystem.DownloadStates( gl, stateBuf, states.Length );
			var colors = GpuParticleOverLife.DownloadColors( gl, colorBuf, states.Length );
			float maxSize = 0, maxVel = 0, maxCol = 0;
			for ( int i = 0; i < states.Length; i++ )
			{
				var (wantState, wantColor) = curves.ApplyToState( states[i], lifetime );
				maxSize = MathF.Max( maxSize, MaxDev( back[i].Size, wantState.Size ) );
				maxVel = MathF.Max( maxVel, MathF.Max( MaxDev( back[i].Velocity.X, wantState.Velocity.X ), MaxDev( back[i].Velocity.Y, wantState.Velocity.Y ) ) );
				maxCol = MathF.Max( maxCol, MathF.Max( MathF.Max( MaxDev( colors[i].X, wantColor.X ), MaxDev( colors[i].Y, wantColor.Y ) ), MathF.Max( MaxDev( colors[i].Z, wantColor.Z ), MaxDev( colors[i].W, wantColor.W ) ) ) );
				Check( back[i].Position == states[i].Position && back[i].Age == states[i].Age,
					$"particle {i} (t={ages[i] / lifetime}): position/age changed by over-life pass" );
			}
			Console.WriteLine( $"gpu-overlife: five-age agreement max devs: size={maxSize}, velocity={maxVel}, colour={maxCol} (eps {Eps})" );
			Check( maxSize <= Eps, $"size deviation {maxSize} exceeds eps {Eps}" );
			Check( maxVel <= Eps, $"velocity deviation {maxVel} exceeds eps {Eps}" );
			Check( maxCol <= Eps, $"colour deviation {maxCol} exceeds eps {Eps}" );
			// Spot-check the hand-computed values, not just the CPU oracle:
			// t=0.5 (particle 2): size 2*2=4, vel (4, -2)*0.625=(2.5, -1.25), colour green alpha 0.5.
			CheckNear( back[2].Size, 4, Eps, "gpu size t=0.5" );
			CheckNear( back[2].Velocity.X, 2.5f, Eps, "gpu vel.x t=0.5" );
			CheckNear( colors[2].Y, 1, Eps, "gpu colour G t=0.5" );
			CheckNear( colors[2].W, 0.5f, Eps, "gpu colour A t=0.5" );
			GpuParticleOverLife.Dispose( gl, stateBuf, curveBuf, colorBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after five-age agreement test" );
		} );

		registry.Add( "gpu-overlife", "gpu clamps past the last key", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleOverLife.CreateProgram( gl );
			var curves = Curves();
			float lifetime = 2f;
			var states = new[]
			{
				new ParticleState { Position = Vector3.Zero, Age = 3f, Velocity = new Vector3( 4, 0, 0 ), Alive = 1, Rotation = Quaternion.Identity, Size = 2 },
				new ParticleState { Position = Vector3.Zero, Age = 2f, Velocity = new Vector3( 4, 0, 0 ), Alive = 1, Rotation = Quaternion.Identity, Size = 2 },
			};
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint curveBuf = GpuParticleOverLife.CreateCurveBuffer( gl, curves );
			uint colorBuf = GpuParticleOverLife.CreateColorBuffer( gl, states.Length );
			GpuParticleOverLife.Dispatch( gl, prog, stateBuf, curveBuf, colorBuf, states.Length, lifetime );
			var back = GpuParticleSystem.DownloadStates( gl, stateBuf, states.Length );
			var colors = GpuParticleOverLife.DownloadColors( gl, colorBuf, states.Length );
			// Both particles must read the FINAL keys: size 2*0.5=1, vel 4*0.25=1, colour blue alpha 0.
			for ( int i = 0; i < 2; i++ )
			{
				CheckNear( back[i].Size, 1, Eps, $"clamped size particle {i} (age {states[i].Age})" );
				CheckNear( back[i].Velocity.X, 1, Eps, $"clamped vel particle {i} (age {states[i].Age})" );
				CheckNear( colors[i].Z, 1, Eps, $"clamped colour B particle {i}" );
				CheckNear( colors[i].W, 0, Eps, $"clamped colour A particle {i}" );
			}
			Console.WriteLine( "gpu-overlife: clamp past last key: age 3.0 (lifetime 2) == age 2.0 exactly at final keys (size 1, vel 1, blue, alpha 0)" );
			GpuParticleOverLife.Dispose( gl, stateBuf, curveBuf, colorBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after clamp test" );
		} );

		registry.Add( "gpu-overlife", "non-multiple workgroup guard leaves sentinel slots untouched", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleOverLife.CreateProgram( gl );
			var curves = Curves();
			int count = 259; // 256 + 3: two workgroups (512 threads), 253 threads past the count.
			int total = count + 16; // sentinel slots past the dispatched count.
			var states = new ParticleState[total];
			for ( int i = 0; i < count; i++ )
				states[i] = new ParticleState { Position = new Vector3( i, 0, 0 ), Age = 1f, Velocity = new Vector3( 4, 0, 0 ), Alive = 1, Rotation = Quaternion.Identity, Size = 2 };
			var sentinel = new ParticleState { Position = new Vector3( 999, 999, 999 ), Age = 0, Velocity = new Vector3( 999, 999, 999 ), Alive = 1, Rotation = Quaternion.Identity, Size = 999 };
			for ( int i = count; i < total; i++ ) states[i] = sentinel;
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint curveBuf = GpuParticleOverLife.CreateCurveBuffer( gl, curves );
			uint colorBuf = GpuParticleOverLife.CreateColorBuffer( gl, total );
			GpuParticleOverLife.Dispatch( gl, prog, stateBuf, curveBuf, colorBuf, count, 2f );
			var back = GpuParticleSystem.DownloadStates( gl, stateBuf, total );
			var colors = GpuParticleOverLife.DownloadColors( gl, colorBuf, total );
			for ( int i = count; i < total; i++ )
			{
				Check( back[i].Position == sentinel.Position && back[i].Size == sentinel.Size && back[i].Velocity == sentinel.Velocity && back[i].Age == sentinel.Age,
					$"sentinel slot {i} changed: guard failed, a thread past uCount wrote out of bounds" );
				Check( colors[i] == Vector4.Zero, $"sentinel colour slot {i} changed from zero" );
			}
			// And the real particles did get evaluated (t=0.5: size 4).
			CheckNear( back[0].Size, 4, Eps, "guard test particle 0 size" );
			CheckNear( back[258].Size, 4, Eps, "guard test last dispatched particle size" );
			Console.WriteLine( "gpu-overlife: workgroup guard: count 259, 16 sentinel slots (259..274) byte-identical, colours still zero" );
			GpuParticleOverLife.Dispose( gl, stateBuf, curveBuf, colorBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after workgroup guard test" );
		} );

		registry.Add( "gpu-overlife", "dead slots stay byte-identical, colour transparent", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleOverLife.CreateProgram( gl );
			var curves = Curves();
			var states = new[]
			{
				new ParticleState { Position = new Vector3( 1, 2, 3 ), Age = 1f, Velocity = new Vector3( 4, 5, 6 ), Alive = 0, Rotation = new Quaternion( 0.1f, 0.2f, 0.3f, 0.4f ), AngularVelocity = new Vector3( 1, 2, 3 ), Size = 7 },
				new ParticleState { Position = new Vector3( 9, 8, 7 ), Age = 1f, Velocity = new Vector3( 4, 0, 0 ), Alive = 1, Rotation = Quaternion.Identity, Size = 2 },
			};
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint curveBuf = GpuParticleOverLife.CreateCurveBuffer( gl, curves );
			uint colorBuf = GpuParticleOverLife.CreateColorBuffer( gl, states.Length );
			GpuParticleOverLife.Dispatch( gl, prog, stateBuf, curveBuf, colorBuf, states.Length, 2f );
			var back = GpuParticleSystem.DownloadStates( gl, stateBuf, states.Length );
			var colors = GpuParticleOverLife.DownloadColors( gl, colorBuf, states.Length );
			Check( back[0].Position == states[0].Position && back[0].Age == states[0].Age && back[0].Velocity == states[0].Velocity
				&& back[0].Rotation == states[0].Rotation && back[0].AngularVelocity == states[0].AngularVelocity && back[0].Size == states[0].Size,
				"dead slot state changed" );
			Check( colors[0] == Vector4.Zero, $"dead slot colour not transparent black, got {colors[0]}" );
			CheckNear( back[1].Size, 4, Eps, "live neighbour still evaluated" );
			GpuParticleOverLife.Dispose( gl, stateBuf, curveBuf, colorBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after dead-slot test" );
		} );

		registry.Add( "gpu-overlife", "absent curves are identity, colour opaque white", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleOverLife.CreateProgram( gl );
			var curves = new GpuParticleOverLife.GpuOverLifeCurves(); // all null
			var states = new[]
			{
				new ParticleState { Position = new Vector3( 1, 2, 3 ), Age = 1.7f, Velocity = new Vector3( 4, -2, 1 ), Alive = 1, Rotation = Quaternion.Identity, Size = 3 },
			};
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint curveBuf = GpuParticleOverLife.CreateCurveBuffer( gl, curves );
			uint colorBuf = GpuParticleOverLife.CreateColorBuffer( gl, states.Length );
			GpuParticleOverLife.Dispatch( gl, prog, stateBuf, curveBuf, colorBuf, states.Length, 2f );
			var back = GpuParticleSystem.DownloadStates( gl, stateBuf, states.Length );
			var colors = GpuParticleOverLife.DownloadColors( gl, colorBuf, states.Length );
			Check( back[0].Size == 3 && back[0].Velocity == states[0].Velocity, "absent curves changed state" );
			Check( colors[0] == new Vector4( 1, 1, 1, 1 ), $"absent colour not opaque white, got {colors[0]}" );
			GpuParticleOverLife.Dispose( gl, stateBuf, curveBuf, colorBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after absent-curves test" );
		} );

		registry.Add( "gpu-overlife", "count zero and count one dispatch safely", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleOverLife.CreateProgram( gl );
			var curves = Curves();
			var states = new[]
			{
				new ParticleState { Position = new Vector3( 5, 5, 5 ), Age = 1f, Velocity = new Vector3( 4, 0, 0 ), Alive = 1, Rotation = Quaternion.Identity, Size = 2 },
			};
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint curveBuf = GpuParticleOverLife.CreateCurveBuffer( gl, curves );
			uint colorBuf = GpuParticleOverLife.CreateColorBuffer( gl, states.Length );
			// Count 0: one workgroup dispatches, every thread returns, nothing changes.
			GpuParticleOverLife.Dispatch( gl, prog, stateBuf, curveBuf, colorBuf, 0, 2f );
			var back0 = GpuParticleSystem.DownloadStates( gl, stateBuf, states.Length );
			Check( back0[0].Size == 2 && back0[0].Velocity == states[0].Velocity, "count-0 dispatch changed state" );
			Check( gl.GetError() == GLEnum.NoError, "GL error after count-0 dispatch" );
			// Count 1: single particle at t=0.5 -> size 4, vel 2.5.
			GpuParticleOverLife.Dispatch( gl, prog, stateBuf, curveBuf, colorBuf, 1, 2f );
			var back1 = GpuParticleSystem.DownloadStates( gl, stateBuf, states.Length );
			var colors = GpuParticleOverLife.DownloadColors( gl, colorBuf, 1 );
			CheckNear( back1[0].Size, 4, Eps, "count-1 size" );
			CheckNear( back1[0].Velocity.X, 2.5f, Eps, "count-1 vel" );
			CheckNear( colors[0].Y, 1, Eps, "count-1 colour G" );
			GpuParticleOverLife.Dispose( gl, stateBuf, curveBuf, colorBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after count-0/count-1 test" );
		} );
	}
}
