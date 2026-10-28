#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU over-life evaluation for 3D particles (wave-6 lane P46), a companion
/// module to the landed <see cref="GpuParticleSystem"/> parity shader. It does
/// NOT edit that shader and it is NOT wired into
/// <see cref="GpuParticleSystem.Dispatch"/> — the sweep integration lines are
/// reported by this lane, not applied here (see the sweep note at the end of
/// this header).
///
/// What it does: one compute dispatch evaluates size, colour and speed curves
/// at each particle's normalized age and applies them to the SAME 64-byte
/// <see cref="ParticleState"/> SSBO layout the parity shader uses
/// (<see cref="GpuParticleSystem.StrideFloats"/> = 16 floats, position at
/// offset 0, age at offset 3, velocity at offset 4, alive at offset 7,
/// size at offset 15). Size and velocity are written back into that state;
/// colour is written to a SEPARATE per-particle colour buffer this module
/// defines, because the GPU state has no colour field yet. That colour buffer
/// is data only — the per-particle-colour render hookup belongs to a
/// separate lane, which will read this buffer (or copy it into its own
/// layout) when it lands.
///
/// Curve semantics — identical to lane P27's CPU curves
/// (<c>Particle2DOverLife.cs</c>, read before writing this file), not
/// reinvented: linear interpolation between neighbouring keys, CLAMPED to
/// the end keys outside the key range (never extrapolated), a single-key
/// curve is constant, a null key list means "curve absent" (identity
/// default), and a non-null but EMPTY list is a loud error. Validation
/// rules match P27 exactly: times strictly increasing in [0, 1], finite;
/// size and speed values finite and &gt;= 0; colour channels in [0, 1].
/// GPU-vs-CPU agreement is EPSILON agreement, never bit-identical (the
/// deep-research correction in particles-lane-list.md section 18: GPU FP
/// rounding differs, so the tests compare with a tolerance).
///
/// Normalized age: the GPU state carries Age but NO lifetime field (the
/// CPU system keeps per-slot lifetimes in a side array,
/// <c>ParticleSystem.LifetimeOf</c>), so normalized age here is
/// <c>clamp(age / lifetime, 0, 1)</c> with <c>lifetime</c> a single
/// per-dispatch uniform. A system whose particles have DIFFERENT
/// lifetimes must dispatch once per lifetime group, or a future lane must
/// add a per-particle lifetime buffer — this module does not invent one.
/// Age past lifetime reads the final key (clamp), exactly as P27 does.
///
/// Single-application semantics (read this before wiring it in): the
/// size multiplier MULTIPLIES the state's Size field and the speed
/// multiplier MULTIPLIES the velocity, once. The dispatch is NOT
/// idempotent: running it twice on the same state compounds both
/// (size * m * m, velocity * s * s). The intended sweep wiring is a
/// once-per-step pass over states whose Size/velocity are still the
/// spawn/base values for that step — i.e. over-life runs on the state
/// BEFORE the integrate step rewrites velocity, or the integrator keeps
/// base size/velocity in a companion buffer (a future lane's job). Do not
/// run this dispatch every frame on top of the previous frame's output
/// and expect P27's curves: P27 evaluates against spawn size every time,
/// this shader has no base to evaluate against.
///
/// Speed / drag interaction: this standalone dispatch has no drag step —
/// it scales the velocity it is given. When the sweep folds over-life
/// into the parity integrate dispatch, ORDER MATTERS and must be chosen
/// deliberately: applying the speed multiplier AFTER the semi-implicit
/// drag step (vel = (vel + (acc + wind*drag)*dt) / (1 + drag*dt), then
/// vel *= speedMult) scales the post-drag velocity INCLUDING the wind
/// contribution, so a slowing particle also slows its wind drift;
/// applying it BEFORE drag lets drag then pull the scaled velocity back
/// toward the wind, partially undoing the multiplier. The recommended
/// sweep order is AFTER drag (the multiplier is the particle's own
/// over-life behaviour, wind is a medium effect that should not be
/// re-amplified by it) — but whichever order the sweep picks, it must
/// be stated in the parity shader's header, because the two orders
/// diverge measurably whenever drag &gt; 0 and wind != 0.
///
/// Dead slots: a slot with Alive == 0 is left byte-identical in the
/// state buffer (matching the parity shader) and its colour slot is
/// written transparent black (0, 0, 0, 0), so a renderer reading the
/// colour buffer never draws a dead particle from stale colour.
///
/// Workgroup guard: workgroup size is 256, matching the parity shader.
/// The dispatch count is ceil(count / 256) groups (minimum 1 group, so a
/// count of 0 still dispatches one group whose threads all return), and
/// every thread returns immediately when its global index is &gt;= uCount,
/// so counts that are not multiples of 256 never read or write
/// out of bounds. The tests prove this with sentinel slots past the
/// dispatched count, not by "it didn't crash".
/// </summary>
public static class GpuParticleOverLife
{
	/// <summary>Workgroup size, matching the landed parity shader.</summary>
	public const int WorkgroupSize = 256;

	/// <summary>Maximum keys per curve. The curve buffer is fixed-size (see layout below).</summary>
	public const int MaxKeysPerCurve = 16;

	// ---- Curve-buffer layout (floats, std430, binding 1) ----
	// [0] size key count, [1] colour key count, [2] speed key count, [3] reserved (0).
	// Size keys at offset 4,  2 floats each (t, value)  -> floats 4..35.
	// Colour keys at offset 36, 5 floats each (t, r, g, b, a) -> floats 36..115.
	// Speed keys at offset 116, 2 floats each (t, value) -> floats 116..147.
	// Total 148 floats (592 bytes). Fixed offsets mean the shader needs no
	// offset uniforms; the CPU side computes the same offsets here.
	/// <summary>Float offset of the first size key in the curve buffer.</summary>
	public const int SizeKeysOffset = 4;
	/// <summary>Float offset of the first colour key in the curve buffer.</summary>
	public const int ColorKeysOffset = 36;
	/// <summary>Float offset of the first speed key in the curve buffer.</summary>
	public const int SpeedKeysOffset = 116;
	/// <summary>Total floats in one curve buffer (592 bytes).</summary>
	public const int CurveBufferFloats = 148;
	/// <summary>Floats per particle in the colour buffer (RGBA).</summary>
	public const int ColorStrideFloats = 4;

	static readonly string ComputeSrc = @"#version 430 core
layout(local_size_x = 256) in;
struct Particle {
    vec4 posAge;
    vec4 velAlive;
    vec4 rot;
    vec4 angSize;
};
layout(std430, binding = 0) buffer Particles {
    Particle particles[];
};
layout(std430, binding = 1) buffer Curves {
    float curveData[];
};
layout(std430, binding = 2) buffer Colors {
    vec4 colors[];
};
uniform uint uCount;
uniform float uLifetime;

float evalScalar(float t, int off, int n, float def) {
    if (n <= 0) return def;
    if (t <= curveData[off]) return curveData[off + 1];
    int last = off + (n - 1) * 2;
    if (t >= curveData[last]) return curveData[last + 1];
    for (int i = 1; i < n; i++) {
        int o = off + i * 2;
        if (t == curveData[o]) return curveData[o + 1];
        if (t < curveData[o]) {
            float f = (t - curveData[o - 2]) / (curveData[o] - curveData[o - 2]);
            return curveData[o - 1] + (curveData[o + 1] - curveData[o - 1]) * f;
        }
    }
    return curveData[last + 1];
}
vec4 evalColor(float t, int off, int n) {
    if (n <= 0) return vec4(1.0, 1.0, 1.0, 1.0);
    if (t <= curveData[off]) return vec4(curveData[off + 1], curveData[off + 2], curveData[off + 3], curveData[off + 4]);
    int last = off + (n - 1) * 5;
    if (t >= curveData[last]) return vec4(curveData[last + 1], curveData[last + 2], curveData[last + 3], curveData[last + 4]);
    for (int i = 1; i < n; i++) {
        int o = off + i * 5;
        if (t == curveData[o]) return vec4(curveData[o + 1], curveData[o + 2], curveData[o + 3], curveData[o + 4]);
        if (t < curveData[o]) {
            float f = (t - curveData[o - 5]) / (curveData[o] - curveData[o - 5]);
            return mix(vec4(curveData[o - 4], curveData[o - 3], curveData[o - 2], curveData[o - 1]),
                       vec4(curveData[o + 1], curveData[o + 2], curveData[o + 3], curveData[o + 4]), f);
        }
    }
    return vec4(curveData[last + 1], curveData[last + 2], curveData[last + 3], curveData[last + 4]);
}
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uCount) return;
    Particle pt = particles[i];
    if (pt.velAlive.w == 0.0) { colors[i] = vec4(0.0); return; }
    float t = uLifetime > 0.0 ? clamp(pt.posAge.w / uLifetime, 0.0, 1.0) : 0.0;
    int sizeCount = int(curveData[0]);
    int colorCount = int(curveData[1]);
    int speedCount = int(curveData[2]);
    float sizeMult = evalScalar(t, 4, sizeCount, 1.0);
    vec4 col = evalColor(t, 36, colorCount);
    float speedMult = evalScalar(t, 116, speedCount, 1.0);
    pt.angSize.w *= sizeMult;
    pt.velAlive.xyz *= speedMult;
    particles[i] = pt;
    colors[i] = col;
}
";

	/// <summary>One scalar keyframe: value at normalized age <see cref="Time"/> (0..1). Same semantics as P27's OverLifeKey.</summary>
	public readonly record struct GpuOverLifeKey( float Time, float Value );

	/// <summary>One colour keyframe: RGBA colour at normalized age <see cref="Time"/> (0..1). Channels in [0, 1].</summary>
	public readonly record struct GpuOverLifeColorKey( float Time, float R, float G, float B, float A );

	/// <summary>One particle's over-life evaluation: size multiplier, colour, speed multiplier.</summary>
	public struct GpuOverLifeResult
	{
		public float SizeMultiplier;
		public Vector4 Color;
		public float SpeedMultiplier;
	}

	/// <summary>Over-life curves for one GPU effect. Null list = curve absent (identity default); empty list = error.</summary>
	public sealed class GpuOverLifeCurves
	{
		/// <summary>Size multiplier keys. Values must be finite and &gt;= 0.</summary>
		public List<GpuOverLifeKey>? SizeKeys;
		/// <summary>Speed multiplier keys. Values must be finite and &gt;= 0.</summary>
		public List<GpuOverLifeKey>? SpeedKeys;
		/// <summary>RGBA colour keys. Channels must be in 0..1.</summary>
		public List<GpuOverLifeColorKey>? ColorKeys;

		/// <summary>Constant curves: size 1, speed 1, opaque white.</summary>
		public static GpuOverLifeCurves Constant() => new()
		{
			SizeKeys = new List<GpuOverLifeKey> { new( 0, 1 ) },
			SpeedKeys = new List<GpuOverLifeKey> { new( 0, 1 ) },
			ColorKeys = new List<GpuOverLifeColorKey> { new( 0, 1, 1, 1, 1 ) },
		};

		/// <summary>Loudly rejects unsorted keys, duplicate times, out-of-range times or values, empty key lists, and over-long lists. Null lists are absent, not errors.</summary>
		public void Validate()
		{
			ValidateScalar( SizeKeys, v => float.IsFinite( v ) && v >= 0, "SizeKeys values must be finite and >= 0" );
			ValidateScalar( SpeedKeys, v => float.IsFinite( v ) && v >= 0, "SpeedKeys values must be finite and >= 0" );
			if ( ColorKeys is not null )
			{
				if ( ColorKeys.Count == 0 ) throw new ArgumentException( "ColorKeys must not be empty (leave it null for no colour curve)." );
				if ( ColorKeys.Count > MaxKeysPerCurve ) throw new ArgumentException( $"ColorKeys has {ColorKeys.Count} keys, max is {MaxKeysPerCurve}." );
				for ( int i = 0; i < ColorKeys.Count; i++ )
				{
					var k = ColorKeys[i];
					if ( !float.IsFinite( k.Time ) || k.Time < 0 || k.Time > 1 )
						throw new ArgumentException( $"ColorKeys[{i}]: Time must be finite and in 0..1, got {k.Time}." );
					if ( i > 0 && k.Time <= ColorKeys[i - 1].Time )
						throw new ArgumentException( $"ColorKeys: times must be strictly increasing (duplicate or unsorted at index {i}: {ColorKeys[i - 1].Time} then {k.Time})." );
					if ( !float.IsFinite( k.R ) || k.R < 0 || k.R > 1 || !float.IsFinite( k.G ) || k.G < 0 || k.G > 1
						|| !float.IsFinite( k.B ) || k.B < 0 || k.B > 1 || !float.IsFinite( k.A ) || k.A < 0 || k.A > 1 )
						throw new ArgumentException( $"ColorKeys[{i}]: colour channels must be finite and in 0..1, got ({k.R}, {k.G}, {k.B}, {k.A})." );
				}
			}
		}

		static void ValidateScalar( List<GpuOverLifeKey>? keys, Func<float, bool> valueOk, string valueMessage )
		{
			if ( keys is null ) return;
			if ( keys.Count == 0 ) throw new ArgumentException( "Key list must not be empty (leave it null for no curve)." );
			if ( keys.Count > MaxKeysPerCurve ) throw new ArgumentException( $"Key list has {keys.Count} keys, max is {MaxKeysPerCurve}." );
			for ( int i = 0; i < keys.Count; i++ )
			{
				var k = keys[i];
				if ( !float.IsFinite( k.Time ) || k.Time < 0 || k.Time > 1 )
					throw new ArgumentException( $"Key[{i}]: Time must be finite and in 0..1, got {k.Time}." );
				if ( i > 0 && k.Time <= keys[i - 1].Time )
					throw new ArgumentException( $"Keys: times must be strictly increasing (duplicate or unsorted at index {i}: {keys[i - 1].Time} then {k.Time})." );
				if ( !valueOk( k.Value ) )
					throw new ArgumentException( $"Key[{i}]: {valueMessage}, got {k.Value}." );
			}
		}

		/// <summary>CPU reference evaluation at normalized age <paramref name="t"/> (clamped to [0, 1], end keys, never extrapolation). This is the agreement oracle for the shader — same math, separate code path.</summary>
		public GpuOverLifeResult EvaluateNormalized( float t )
		{
			if ( !float.IsFinite( t ) ) throw new ArgumentException( $"Normalized age must be finite, got {t}." );
			Validate();
			float c = Math.Clamp( t, 0, 1 );
			return new GpuOverLifeResult
			{
				SizeMultiplier = SizeKeys is null ? 1 : EvalScalar( SizeKeys, c ),
				SpeedMultiplier = SpeedKeys is null ? 1 : EvalScalar( SpeedKeys, c ),
				Color = ColorKeys is null ? new Vector4( 1, 1, 1, 1 ) : EvalColor( ColorKeys, c ),
			};
		}

		/// <summary>CPU reference evaluation from age/lifetime. Lifetime must be finite and &gt; 0; age must be finite. Age past lifetime reads the final key.</summary>
		public GpuOverLifeResult Evaluate( float age, float lifetime )
		{
			if ( !float.IsFinite( age ) ) throw new ArgumentException( $"Age must be finite, got {age}." );
			if ( !float.IsFinite( lifetime ) || lifetime <= 0 ) throw new ArgumentException( $"Lifetime must be finite and > 0, got {lifetime}." );
			return EvaluateNormalized( Math.Clamp( age / lifetime, 0, 1 ) );
		}

		/// <summary>Single-application CPU reference for one particle: size *= sizeMult, velocity *= speedMult, colour returned separately. Dead slots are returned unchanged with transparent colour, matching the shader.</summary>
		public (ParticleState State, Vector4 Color) ApplyToState( ParticleState state, float lifetime )
		{
			if ( state.Alive == 0 ) return ( state, Vector4.Zero );
			var r = Evaluate( state.Age, lifetime );
			state.Size *= r.SizeMultiplier;
			state.Velocity *= r.SpeedMultiplier;
			return ( state, r.Color );
		}

		static float EvalScalar( IReadOnlyList<GpuOverLifeKey> keys, float t )
		{
			if ( t <= keys[0].Time ) return keys[0].Value;
			if ( t >= keys[keys.Count - 1].Time ) return keys[keys.Count - 1].Value;
			for ( int i = 1; i < keys.Count; i++ )
			{
				if ( t == keys[i].Time ) return keys[i].Value;
				if ( t < keys[i].Time )
				{
					float f = (t - keys[i - 1].Time) / (keys[i].Time - keys[i - 1].Time);
					return keys[i - 1].Value + (keys[i].Value - keys[i - 1].Value) * f;
				}
			}
			return keys[keys.Count - 1].Value;
		}

		static Vector4 EvalColor( IReadOnlyList<GpuOverLifeColorKey> keys, float t )
		{
			if ( t <= keys[0].Time ) return new Vector4( keys[0].R, keys[0].G, keys[0].B, keys[0].A );
			if ( t >= keys[keys.Count - 1].Time ) { var l = keys[keys.Count - 1]; return new Vector4( l.R, l.G, l.B, l.A ); }
			for ( int i = 1; i < keys.Count; i++ )
			{
				if ( t == keys[i].Time ) return new Vector4( keys[i].R, keys[i].G, keys[i].B, keys[i].A );
				if ( t < keys[i].Time )
				{
					float f = (t - keys[i - 1].Time) / (keys[i].Time - keys[i - 1].Time);
					var a = keys[i - 1]; var b = keys[i];
					return new Vector4(
						a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f,
						a.B + (b.B - a.B) * f, a.A + (b.A - a.A) * f );
				}
			}
			var last = keys[keys.Count - 1];
			return new Vector4( last.R, last.G, last.B, last.A );
		}
	}

	/// <summary>Build the 148-float curve buffer for <paramref name="curves"/> (validates first). Layout documented on <see cref="CurveBufferFloats"/>.</summary>
	public static float[] BuildCurveBuffer( GpuOverLifeCurves curves )
	{
		ArgumentNullException.ThrowIfNull( curves );
		curves.Validate();
		var data = new float[CurveBufferFloats];
		data[0] = curves.SizeKeys?.Count ?? 0;
		data[1] = curves.ColorKeys?.Count ?? 0;
		data[2] = curves.SpeedKeys?.Count ?? 0;
		if ( curves.SizeKeys is not null )
			for ( int i = 0; i < curves.SizeKeys.Count; i++ ) { data[SizeKeysOffset + i * 2] = curves.SizeKeys[i].Time; data[SizeKeysOffset + i * 2 + 1] = curves.SizeKeys[i].Value; }
		if ( curves.ColorKeys is not null )
			for ( int i = 0; i < curves.ColorKeys.Count; i++ )
			{
				int o = ColorKeysOffset + i * 5;
				data[o] = curves.ColorKeys[i].Time; data[o + 1] = curves.ColorKeys[i].R; data[o + 2] = curves.ColorKeys[i].G; data[o + 3] = curves.ColorKeys[i].B; data[o + 4] = curves.ColorKeys[i].A;
			}
		if ( curves.SpeedKeys is not null )
			for ( int i = 0; i < curves.SpeedKeys.Count; i++ ) { data[SpeedKeysOffset + i * 2] = curves.SpeedKeys[i].Time; data[SpeedKeysOffset + i * 2 + 1] = curves.SpeedKeys[i].Value; }
		return data;
	}

	public static uint CreateProgram( GL gl )
	{
		var cs = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( cs, ComputeSrc );
		gl.CompileShader( cs );
		gl.GetShader( cs, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 ) { var log = gl.GetShaderInfoLog( cs ); gl.DeleteShader( cs ); throw new InvalidOperationException( "over-life compute compile failed:\n" + log ); }
		var prog = gl.CreateProgram();
		gl.AttachShader( prog, cs );
		gl.LinkProgram( prog );
		gl.GetProgram( prog, ProgramPropertyARB.LinkStatus, out int linked );
		gl.DetachShader( prog, cs );
		gl.DeleteShader( cs );
		if ( linked == 0 ) { var log = gl.GetProgramInfoLog( prog ); gl.DeleteProgram( prog ); throw new InvalidOperationException( "over-life compute link failed:\n" + log ); }
		return prog;
	}

	static uint UploadFloats( GL gl, float[] data )
	{
		uint buf = gl.GenBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, buf );
		unsafe
		{
			fixed ( float* ptr = data )
			{
				gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof( float )), ptr, BufferUsageARB.DynamicDraw );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return buf;
	}

	/// <summary>Upload a curve buffer for <paramref name="curves"/>. Returns buffer handle.</summary>
	public static uint CreateCurveBuffer( GL gl, GpuOverLifeCurves curves ) => UploadFloats( gl, BuildCurveBuffer( curves ) );

	/// <summary>Allocate a zeroed per-particle colour buffer for <paramref name="particleCount"/> particles (RGBA floats). At least one slot is allocated so a zero-count dispatch still has a valid binding.</summary>
	public static uint CreateColorBuffer( GL gl, int particleCount )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ) );
		return UploadFloats( gl, new float[Math.Max( particleCount, 1 ) * ColorStrideFloats] );
	}

	/// <summary>
	/// Run one over-life pass for <paramref name="particleCount"/> particles:
	/// size *= sizeMult(t), velocity *= speedMult(t), colour buffer = colour(t),
	/// where t = clamp(age / <paramref name="lifetime"/>, 0, 1). Single
	/// application — see the class header on non-idempotence before wiring
	/// this into a per-step loop. State buffer layout is the parity shader's
	/// (binding 0); curve buffer is binding 1; colour buffer is binding 2.
	/// </summary>
	public static void Dispatch( GL gl, uint program, uint stateBuffer, uint curveBuffer, uint colorBuffer, int particleCount, float lifetime )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ) );
		if ( !float.IsFinite( lifetime ) || lifetime <= 0 ) throw new ArgumentException( $"Lifetime must be finite and > 0, got {lifetime}." );
		gl.UseProgram( program );
		gl.Uniform1( gl.GetUniformLocation( program, "uCount" ), (uint)particleCount );
		gl.Uniform1( gl.GetUniformLocation( program, "uLifetime" ), lifetime );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, stateBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, curveBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, colorBuffer );
		uint groups = (uint)((particleCount + WorkgroupSize - 1) / WorkgroupSize);
		if ( groups == 0 ) groups = 1;
		gl.DispatchCompute( groups, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, 0 );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, 0 );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, 0 );
	}

	/// <summary>Read back per-particle colours (RGBA) for <paramref name="particleCount"/> particles.</summary>
	public static Vector4[] DownloadColors( GL gl, uint colorBuffer, int particleCount )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ) );
		var data = new float[particleCount * ColorStrideFloats];
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, colorBuffer );
		unsafe
		{
			fixed ( float* ptr = data )
			{
				gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(data.Length * sizeof( float )), ptr );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		var colors = new Vector4[particleCount];
		for ( int i = 0; i < particleCount; i++ )
			colors[i] = new Vector4( data[i * 4], data[i * 4 + 1], data[i * 4 + 2], data[i * 4 + 3] );
		return colors;
	}

	public static void Dispose( GL gl, uint stateBuffer, uint curveBuffer, uint colorBuffer, uint program )
	{
		if ( stateBuffer != 0 ) gl.DeleteBuffer( stateBuffer );
		if ( curveBuffer != 0 ) gl.DeleteBuffer( curveBuffer );
		if ( colorBuffer != 0 ) gl.DeleteBuffer( colorBuffer );
		if ( program != 0 ) gl.DeleteProgram( program );
	}
}

// ---- SWEEP INTEGRATION LINES (coordinator only — NOT applied by lane P46) ----
// This module is intentionally not wired into GpuParticleSystem.Dispatch.
// When the sweep wires it in, the exact edits are:
//   1. TestRegistry.cs, after the GpuParticleSystemTests.Register line:
//        GpuParticleOverLifeTests.Register( registry, settings );
//   2. GpuParticleSystem.cs (a future edit by the sweep, NOT this lane):
//      EITHER (a) a separate post-integrate pass per step:
//        GpuParticleOverLife.Dispatch( gl, overLifeProgram, stateBuffer,
//          curveBuffer, colorBuffer, count, lifetime );
//      run AFTER GpuParticleSystem.Dispatch for that substep, on states
//      whose Size/velocity are still the step's base values — OR (b) fold
//      the evalScalar/evalColor functions into the parity shader itself,
//      applying the speed multiplier AFTER the drag step (see the class
//      header on drag order) and writing colour to binding 2. Option (a)
//      keeps the parity shader byte-identical; option (b) saves a dispatch
//      but changes the landed shader. Either way the colour buffer must
//      be plumbed to the per-particle-colour render lane before any
//      particle is drawn with it — until then the buffer is data only.
//   3. No BuildScript.cs, cabinet.qml, host.py, LiveGeometry.cs, or
//      ParticleSystem.cs changes are needed for this module itself.
