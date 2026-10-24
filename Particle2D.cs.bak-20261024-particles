#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// Flat 2D particle system for Geppetto's 2D side (lane 1, particles session).
// Units are canvas pixels, seconds, Y-DOWN (matches QML Canvas: +Y is down the
// screen, so gravity is +Y and bubbles rise with negative gravity). This is
// deliberately NOT the 3D ParticleSystem: it is a separate sim, not bound by
// the 3D system's 64-byte GPU state layout, so it carries per-particle color
// from day one. Deterministic fixed-step sim, seeded RNG, no System.Random.
//
// The 2D animation suite owns the canvas/UI that draws these particles; this
// file owns the sim, the presets, and the draw records the canvas consumes
// (see WriteDrawRecords). There is no rendering, no QML, and no cabinet/host
// wiring in this file — that boundary is the 2D suite's, not a gap to fill
// here by editing cabinet.qml.

public enum Particle2DEmitterShape { Point, Line, Box, Circle }

/// <summary>RGBA color, channels 0..1. Y-down canvas space does not change color.</summary>
public struct Particle2DColor
{
	public float R, G, B, A;
	public Particle2DColor( float r, float g, float b, float a ) { R = r; G = g; B = b; A = a; }
	public static Particle2DColor Lerp( Particle2DColor a, Particle2DColor b, float t ) => new(
		a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t );
	public Particle2DColor WithAlpha( float alpha ) => new( R, G, B, alpha );
	public bool ChannelsValid() => R >= 0 && R <= 1 && G >= 0 && G <= 1 && B >= 0 && B <= 1 && A >= 0 && A <= 1;
	public override string ToString() => $"({R:F3},{G:F3},{B:F3},{A:F3})";
}

/// <summary>Emit <paramref name="Count"/> particles at the start of fixed step <paramref name="Step"/>.</summary>
public readonly record struct Particle2DBurst( int Step, int Count );

/// <summary>One particle's draw record: everything a 2D canvas needs to paint it for one frame.</summary>
public struct Particle2DDrawRecord
{
	public Vector2 Position;
	public float Size;
	public float Rotation;
	public Particle2DColor Color;
	public int Frame;
}

/// <summary>Per-slot simulation state. Current size/color/frame are derived from age (see WriteDrawRecords).</summary>
public struct Particle2DState
{
	public Vector2 Position;
	public Vector2 Velocity;
	public float Age;
	public float Lifetime;
	public float SizeStart;
	public float SizeEnd;
	public float Rotation;
	public float AngularVelocity;
	public Particle2DColor ColorStart;
	public Particle2DColor ColorEnd;
	public int FrameBase;
	public int FrameMin;
	public int FrameCount;
	public float FrameAdvance;
	public bool Alive;
}

/// <summary>An emitter. Configure before or between steps; values are validated on each step.</summary>
public sealed class Particle2DEmitter
{
	public Particle2DEmitterShape Shape = Particle2DEmitterShape.Point;
	/// <summary>Point position / line start / box centre / circle centre.</summary>
	public Vector2 Position;
	/// <summary>Line end (Line shape only).</summary>
	public Vector2 LineEnd;
	/// <summary>Box half extents, axis aligned (Box shape only).</summary>
	public Vector2 BoxHalfExtents = new( 1, 1 );
	/// <summary>Circle radius (Circle shape only).</summary>
	public float CircleRadius = 1f;

	/// <summary>Particles per second (continuous, fractional remainder carried between steps).</summary>
	public float Rate;
	public List<Particle2DBurst> Bursts = new();

	public float LifetimeMin = 1f, LifetimeMax = 1f;
	public float SizeMin = 1f, SizeMax = 1f;
	/// <summary>Size at end of life = spawn size × SizeEndScale (1 = constant size, 0 = shrink to nothing).</summary>
	public float SizeEndScale = 1f;
	/// <summary>Launch direction, Y-down (e.g. (0,1) falls, (0,-1) rises). Normalized at spawn.</summary>
	public Vector2 Direction = new( 0, 1 );
	public float Speed;
	/// <summary>Speed is uniformly in Speed*(1 ± SpeedJitter).</summary>
	public float SpeedJitter;
	/// <summary>Cone half-angle around the launch direction, degrees 0..180 (180 = any direction).</summary>
	public float SpreadDegrees;
	/// <summary>Added to every launch velocity (e.g. emitter motion).</summary>
	public Vector2 BaseVelocity;
	/// <summary>Max angular speed, rad/s; spawn picks uniformly in [-max, +max].</summary>
	public float AngularSpeedMax;
	/// <summary>Particle color at birth (before jitter/palette).</summary>
	public Particle2DColor ColorStart = new( 1, 1, 1, 1 );
	/// <summary>Particle color at end of life (before jitter; palette mode overrides, see below).</summary>
	public Particle2DColor ColorEnd = new( 1, 1, 1, 0 );
	/// <summary>Per-channel spawn jitter, 0..1: one delta in [-jitter, +jitter] per channel, same delta for start and end so hue stays coherent.</summary>
	public float ColorJitter;
	/// <summary>Optional palette: when non-empty, each particle picks one entry as its start color and fades its alpha to EndAlpha. Overrides ColorStart/ColorEnd.</summary>
	public List<Particle2DColor>? Palette;
	/// <summary>Alpha at end of life in palette mode (0 = fade out).</summary>
	public float EndAlpha;
	/// <summary>Sprite frame range, inclusive. A particle's base frame is picked at spawn.</summary>
	public int FrameMin, FrameMax;
	/// <summary>Sprite frames per second cycled over life (0 = frame fixed at its spawn value).</summary>
	public float FrameAdvance;
	public bool Enabled = true;

	internal ParticleRng Rng;
	internal double RateRemainder;

	internal void Validate( int index )
	{
		string who = $"Emitter {index}";
		if ( !Particle2DMath.Finite( Position ) || !Particle2DMath.Finite( Direction ) || !Particle2DMath.Finite( BaseVelocity ) || !Particle2DMath.Finite( LineEnd ) || !Particle2DMath.Finite( BoxHalfExtents ) )
			throw new ArgumentException( $"{who}: position/direction/velocity/line/box must be finite." );
		if ( !float.IsFinite( Rate ) || Rate < 0 ) throw new ArgumentException( $"{who}: Rate must be finite and >= 0." );
		if ( !float.IsFinite( LifetimeMin ) || !float.IsFinite( LifetimeMax ) || LifetimeMin <= 0 || LifetimeMax < LifetimeMin )
			throw new ArgumentException( $"{who}: lifetime must satisfy 0 < LifetimeMin <= LifetimeMax." );
		if ( !float.IsFinite( SizeMin ) || !float.IsFinite( SizeMax ) || SizeMin <= 0 || SizeMax < SizeMin )
			throw new ArgumentException( $"{who}: size must satisfy 0 < SizeMin <= SizeMax." );
		if ( !float.IsFinite( SizeEndScale ) || SizeEndScale < 0 ) throw new ArgumentException( $"{who}: SizeEndScale must be finite and >= 0." );
		if ( !float.IsFinite( Speed ) || Speed < 0 || !float.IsFinite( SpeedJitter ) || SpeedJitter < 0 || SpeedJitter > 1 )
			throw new ArgumentException( $"{who}: Speed >= 0 and SpeedJitter in 0..1 required." );
		if ( !float.IsFinite( SpreadDegrees ) || SpreadDegrees < 0 || SpreadDegrees > 180 )
			throw new ArgumentException( $"{who}: SpreadDegrees must be in 0..180." );
		if ( !float.IsFinite( AngularSpeedMax ) || AngularSpeedMax < 0 ) throw new ArgumentException( $"{who}: AngularSpeedMax must be finite and >= 0." );
		if ( Direction.LengthSquared() < 1e-12f && Speed > 0 )
			throw new ArgumentException( $"{who}: Direction must be non-zero when Speed > 0." );
		if ( !ColorStart.ChannelsValid() || !ColorEnd.ChannelsValid() )
			throw new ArgumentException( $"{who}: ColorStart/ColorEnd channels must be in 0..1." );
		if ( !float.IsFinite( ColorJitter ) || ColorJitter < 0 || ColorJitter > 1 )
			throw new ArgumentException( $"{who}: ColorJitter must be in 0..1." );
		if ( !float.IsFinite( EndAlpha ) || EndAlpha < 0 || EndAlpha > 1 )
			throw new ArgumentException( $"{who}: EndAlpha must be in 0..1." );
		if ( Palette is not null )
		{
			if ( Palette.Count == 0 ) throw new ArgumentException( $"{who}: Palette, when set, must not be empty (leave it null for ColorStart/ColorEnd mode)." );
			for ( int i = 0; i < Palette.Count; i++ )
				if ( !Palette[i].ChannelsValid() ) throw new ArgumentException( $"{who}: Palette entry {i} channels must be in 0..1." );
		}
		if ( FrameMin < 0 || FrameMax < FrameMin ) throw new ArgumentException( $"{who}: frames must satisfy 0 <= FrameMin <= FrameMax." );
		if ( !float.IsFinite( FrameAdvance ) || FrameAdvance < 0 ) throw new ArgumentException( $"{who}: FrameAdvance must be finite and >= 0." );
		foreach ( var b in Bursts )
			if ( b.Step < 0 || b.Count < 0 ) throw new ArgumentException( $"{who}: bursts need Step >= 0 and Count >= 0." );
		switch ( Shape )
		{
			case Particle2DEmitterShape.Line:
				if ( (LineEnd - Position).LengthSquared() < 1e-12f ) throw new ArgumentException( $"{who}: Line needs LineEnd distinct from Position." );
				break;
			case Particle2DEmitterShape.Box:
				if ( BoxHalfExtents.X < 0 || BoxHalfExtents.Y < 0 ) throw new ArgumentException( $"{who}: box half extents must be >= 0." );
				break;
			case Particle2DEmitterShape.Circle:
				if ( !float.IsFinite( CircleRadius ) || CircleRadius < 0 ) throw new ArgumentException( $"{who}: CircleRadius must be finite and >= 0." );
				break;
			case Particle2DEmitterShape.Point: break;
			default: throw new ArgumentException( $"{who}: unknown shape {Shape}." );
		}
	}
}

/// <summary>Simulation settings. Units are canvas pixels, seconds, Y-down.</summary>
public sealed class Particle2DSettings
{
	public int Capacity = 10_000;
	public uint Seed = 1;
	/// <summary>Fixed step, seconds.</summary>
	public float FixedDeltaTime = 1f / 60f;
	public int Substeps = 1;

	/// <summary>Default 980 px/s² pointing +Y (down). Negative Y rises (bubbles).</summary>
	public Vector2 Gravity = new( 0, 980f );
	/// <summary>Air velocity. Only acts through <see cref="Drag"/> (drag pulls velocity toward wind).</summary>
	public Vector2 Wind;
	/// <summary>Linear drag coefficient, 1/s; integrated semi-implicitly (stable for any value >= 0).</summary>
	public float Drag;

	/// <summary>Turbulence acceleration magnitude scale (px/s²). 0 disables.</summary>
	public float TurbulenceStrength;
	/// <summary>Noise lattice frequency, 1/px.</summary>
	public float TurbulenceFrequency = 0.02f;
	/// <summary>How fast the field scrolls along +X of noise space, noise units/s.</summary>
	public float TurbulenceSpeed;

	/// <summary>Vortex: acceleration = perp(r) · Strength / (|r|² + Radius²), perp(x,y) = (-y,x). 0 disables.</summary>
	public float VortexStrength;
	public Vector2 VortexCenter;
	public float VortexRadius = 1f;

	public bool FloorEnabled;
	/// <summary>Horizontal floor line: particles are clamped to y <= FloorY and bounce.</summary>
	public float FloorY;
	/// <summary>Normal velocity retained on floor contact (0 = dead stop, 1 = perfect bounce).</summary>
	public float Restitution = 0.3f;
	/// <summary>Tangential velocity retained on floor contact is (1 − Friction).</summary>
	public float Friction = 0.2f;

	public void Validate()
	{
		if ( Capacity <= 0 || Capacity > 1_000_000 ) throw new ArgumentOutOfRangeException( nameof( Capacity ), "Capacity must be 1..1,000,000." );
		if ( !float.IsFinite( FixedDeltaTime ) || FixedDeltaTime <= 0 || FixedDeltaTime > 1 ) throw new ArgumentOutOfRangeException( nameof( FixedDeltaTime ), "Fixed step must be in (0, 1] s." );
		if ( Substeps < 1 || Substeps > 64 ) throw new ArgumentOutOfRangeException( nameof( Substeps ), "Substeps must be 1..64." );
		if ( !Particle2DMath.Finite( Gravity ) || !Particle2DMath.Finite( Wind ) || !Particle2DMath.Finite( VortexCenter ) )
			throw new ArgumentException( "Force vectors must be finite." );
		if ( !float.IsFinite( Drag ) || Drag < 0 ) throw new ArgumentOutOfRangeException( nameof( Drag ), "Drag must be >= 0." );
		if ( !float.IsFinite( TurbulenceStrength ) || TurbulenceStrength < 0 || !float.IsFinite( TurbulenceFrequency ) || TurbulenceFrequency <= 0 || !float.IsFinite( TurbulenceSpeed ) )
			throw new ArgumentException( "Turbulence needs strength >= 0, frequency > 0, finite speed." );
		if ( !float.IsFinite( VortexStrength ) || !float.IsFinite( VortexRadius ) || VortexRadius <= 0 )
			throw new ArgumentException( "Vortex needs finite strength and radius > 0." );
		if ( FloorEnabled && !float.IsFinite( FloorY ) ) throw new ArgumentException( "FloorY must be finite when FloorEnabled." );
		if ( !float.IsFinite( Restitution ) || Restitution < 0 || Restitution > 1 ) throw new ArgumentOutOfRangeException( nameof( Restitution ), "Restitution must be 0..1." );
		if ( !float.IsFinite( Friction ) || Friction < 0 || Friction > 1 ) throw new ArgumentOutOfRangeException( nameof( Friction ), "Friction must be 0..1." );
	}
}

/// <summary>Shared 2D particle math: 2D value noise and its rotated gradient (a divergence-free turbulence field).</summary>
public static class Particle2DMath
{
	public static bool Finite( Vector2 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y );

	public static uint Hash( int x, int y, uint seed )
	{
		unchecked
		{
			uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ seed;
			h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
			return h;
		}
	}

	static float Lattice( int x, int y, uint seed ) => (Hash( x, y, seed ) & 0xFFFFFFu) * (2f / 16777215f) - 1f;

	/// <summary>Value noise in [-1,1] with smoothstep fade; returns value and analytic gradient.</summary>
	public static float NoiseGrad( Vector2 p, uint seed, out Vector2 grad )
	{
		float fx = MathF.Floor( p.X ), fy = MathF.Floor( p.Y );
		int ix = (int)fx, iy = (int)fy;
		float tx = p.X - fx, ty = p.Y - fy;
		float ux = tx * tx * (3f - 2f * tx), uy = ty * ty * (3f - 2f * ty);
		float dx = 6f * tx * (1f - tx), dy = 6f * ty * (1f - ty);
		float c00 = Lattice( ix, iy, seed ), c10 = Lattice( ix + 1, iy, seed );
		float c01 = Lattice( ix, iy + 1, seed ), c11 = Lattice( ix + 1, iy + 1, seed );
		float x0 = c00 + (c10 - c00) * ux, x1 = c01 + (c11 - c01) * ux;
		float v = x0 + (x1 - x0) * uy;
		float gx = (c10 - c00) * dx + ((c11 - c01) * dx - (c10 - c00) * dx) * uy;
		float gy = (x1 - x0) * dy;
		grad = new Vector2( gx, gy );
		return v;
	}

	/// <summary>Divergence-free 2D turbulence: the gradient of a noise potential, rotated 90°.</summary>
	public static Vector2 Curl( Vector2 p, uint seed )
	{
		NoiseGrad( p, seed, out var g );
		return new Vector2( -g.Y, g.X );
	}
}

/// <summary>
/// Fixed-timestep, seeded 2D particle system. <see cref="Step"/> advances exactly one fixed step:
/// expire → emit → <see cref="Particle2DSettings.Substeps"/> × (forces, integrate, collide),
/// then age advances by one fixed step. Lifetime is quantized to whole steps at spawn
/// (death step = spawn step + ceil(L / FixedDeltaTime - 1e-6), at least 1): the particle is
/// visible for exactly that many Step() calls, then expires at the start of the next one.
/// Expiry compares integer steps, never accumulated float age, so a lifetime of exactly
/// 1s at 60 Hz dies after 61 calls, not 62. After the first step the sim does not allocate.
/// </summary>
public sealed class Particle2DSystem
{
	public Particle2DSettings Settings { get; }
	readonly List<Particle2DEmitter> _emitters = new();
	public IReadOnlyList<Particle2DEmitter> Emitters => _emitters;

	readonly Particle2DState[] _state;
	readonly long[] _deathStep;
	readonly int[] _freeStack;
	int _freeCount;
	int _highWater;
	public int AliveCount { get; private set; }
	public long StepIndex { get; private set; }
	public ReadOnlySpan<Particle2DState> States => _state.AsSpan( 0, _highWater );

	public Particle2DSystem( Particle2DSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		Settings = settings;
		int n = settings.Capacity;
		_state = new Particle2DState[n];
		_deathStep = new long[n];
		_freeStack = new int[n];
		for ( int i = 0; i < n; i++ ) _freeStack[i] = n - 1 - i;
		_freeCount = n;
	}

	public Particle2DEmitter AddEmitter( Particle2DEmitter emitter )
	{
		ArgumentNullException.ThrowIfNull( emitter );
		if ( _emitters.Contains( emitter ) ) throw new ArgumentException( "Emitter already added." );
		emitter.Validate( _emitters.Count );
		emitter.Rng = ParticleRng.For( Settings.Seed, _emitters.Count );
		emitter.RateRemainder = 0;
		_emitters.Add( emitter );
		return emitter;
	}

	public bool IsAlive( int slot ) => (uint)slot < (uint)_highWater && _state[slot].Alive;

	/// <summary>One fixed step.</summary>
	public void Step()
	{
		BeginStep();
		Simulate();
		float dt = Settings.FixedDeltaTime;
		for ( int s = 0; s < _highWater; s++ )
			if ( _state[s].Alive ) _state[s].Age += dt;
		StepIndex++;
	}

	public void Step( int count )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ) );
		for ( int i = 0; i < count; i++ ) Step();
	}

	void BeginStep()
	{
		Settings.Validate();
		long step = StepIndex;
		bool died = false;
		for ( int s = 0; s < _highWater; s++ )
		{
			if ( !_state[s].Alive || step < _deathStep[s] ) continue;
			_state[s].Alive = false;
			_state[s].Velocity = Vector2.Zero;
			_freeStack[_freeCount++] = s;
			AliveCount--;
			died = true;
		}
		// Keep the free stack popping the lowest slot so slot reuse is compact and deterministic.
		if ( died )
		{
			var span = _freeStack.AsSpan( 0, _freeCount );
			span.Sort();
			span.Reverse();
		}
		while ( _highWater > 0 && !_state[_highWater - 1].Alive ) _highWater--;

		float dt = Settings.FixedDeltaTime;
		for ( int e = 0; e < _emitters.Count; e++ )
		{
			var em = _emitters[e];
			em.Validate( e );
			if ( !em.Enabled ) continue;
			int count = 0;
			foreach ( var b in em.Bursts ) if ( b.Step == step ) count += b.Count;
			em.RateRemainder += em.Rate * (double)dt;
			int fromRate = (int)Math.Floor( em.RateRemainder );
			em.RateRemainder -= fromRate;
			count += fromRate;
			for ( int i = 0; i < count; i++ ) Spawn( em );
		}
	}

	void Spawn( Particle2DEmitter em )
	{
		// Draw RNG values in a fixed order FIRST, so a full pool drops the particle
		// without shifting later particles' streams (same discipline as the 3D system).
		ref var rng = ref em.Rng;
		Vector2 pos = em.Position;
		switch ( em.Shape )
		{
			case Particle2DEmitterShape.Line:
			{
				float t = rng.NextFloat();
				pos = em.Position + (em.LineEnd - em.Position) * t;
				break;
			}
			case Particle2DEmitterShape.Box:
				pos = em.Position + new Vector2( (rng.NextFloat() * 2 - 1) * em.BoxHalfExtents.X, (rng.NextFloat() * 2 - 1) * em.BoxHalfExtents.Y );
				break;
			case Particle2DEmitterShape.Circle:
			{
				float a = rng.NextFloat() * 2f * MathF.PI;
				float r = em.CircleRadius * MathF.Sqrt( rng.NextFloat() );
				pos = em.Position + new Vector2( MathF.Cos( a ) * r, MathF.Sin( a ) * r );
				break;
			}
		}
		Vector2 dir = em.Direction;
		if ( dir.LengthSquared() > 1e-12f ) dir /= dir.Length();
		else dir = new Vector2( 0, 1 );
		if ( em.SpreadDegrees > 0 )
		{
			float ang = (rng.NextFloat() * 2 - 1) * em.SpreadDegrees * MathF.PI / 180f;
			float c = MathF.Cos( ang ), s = MathF.Sin( ang );
			dir = new Vector2( dir.X * c - dir.Y * s, dir.X * s + dir.Y * c );
		}
		float speed = em.Speed;
		if ( em.SpeedJitter > 0 ) speed = em.Speed * (1 + (rng.NextFloat() * 2 - 1) * em.SpeedJitter);
		Vector2 vel = dir * speed + em.BaseVelocity;
		float lifetime = em.LifetimeMin;
		if ( em.LifetimeMax > em.LifetimeMin ) lifetime = rng.Range( em.LifetimeMin, em.LifetimeMax );
		float size = em.SizeMin;
		if ( em.SizeMax > em.SizeMin ) size = rng.Range( em.SizeMin, em.SizeMax );
		Particle2DColor start, end;
		if ( em.Palette is { Count: > 0 } )
		{
			var pick = em.Palette[(int)(rng.NextFloat() * em.Palette.Count) % em.Palette.Count];
			start = pick;
			end = pick.WithAlpha( em.EndAlpha );
		}
		else
		{
			float jr = 0, jg = 0, jb = 0;
			if ( em.ColorJitter > 0 )
			{
				jr = (rng.NextFloat() * 2 - 1) * em.ColorJitter;
				jg = (rng.NextFloat() * 2 - 1) * em.ColorJitter;
				jb = (rng.NextFloat() * 2 - 1) * em.ColorJitter;
			}
			start = new Particle2DColor(
				Math.Clamp( em.ColorStart.R + jr, 0, 1 ), Math.Clamp( em.ColorStart.G + jg, 0, 1 ), Math.Clamp( em.ColorStart.B + jb, 0, 1 ), em.ColorStart.A );
			end = new Particle2DColor(
				Math.Clamp( em.ColorEnd.R + jr, 0, 1 ), Math.Clamp( em.ColorEnd.G + jg, 0, 1 ), Math.Clamp( em.ColorEnd.B + jb, 0, 1 ), em.ColorEnd.A );
		}
		float angVel = 0;
		if ( em.AngularSpeedMax > 0 ) angVel = (rng.NextFloat() * 2 - 1) * em.AngularSpeedMax;
		int frame = em.FrameMin;
		if ( em.FrameMax > em.FrameMin ) frame = em.FrameMin + (int)(rng.NextFloat() * (em.FrameMax - em.FrameMin + 1)) % (em.FrameMax - em.FrameMin + 1);

		if ( _freeCount == 0 ) return;
		int slot = _freeStack[--_freeCount];
		if ( slot >= _highWater ) _highWater = slot + 1;
		_deathStep[slot] = StepIndex + Math.Max( 1, (long)Math.Ceiling( (double)lifetime / Settings.FixedDeltaTime - 1e-6 ) );
		_state[slot] = new Particle2DState
		{
			Position = pos, Velocity = vel, Age = 0, Lifetime = lifetime,
			SizeStart = size, SizeEnd = size * em.SizeEndScale,
			Rotation = 0, AngularVelocity = angVel,
			ColorStart = start, ColorEnd = end,
			FrameBase = frame, FrameMin = em.FrameMin, FrameCount = em.FrameMax - em.FrameMin + 1, FrameAdvance = em.FrameAdvance,
			Alive = true,
		};
		AliveCount++;
	}

	void Simulate()
	{
		var s = Settings;
		float dt = s.FixedDeltaTime;
		int sub = s.Substeps;
		float h = dt / sub;
		float time = (float)StepIndex * dt;
		for ( int k = 0; k < sub; k++ )
		{
			for ( int i = 0; i < _highWater; i++ )
			{
				if ( !_state[i].Alive ) continue;
				ref var p = ref _state[i];
				Vector2 acc = s.Gravity;
				if ( s.TurbulenceStrength != 0 )
					acc += Particle2DMath.Curl( p.Position * s.TurbulenceFrequency + new Vector2( time * s.TurbulenceSpeed, 0 ), s.Seed ) * s.TurbulenceStrength;
				if ( s.VortexStrength != 0 )
				{
					var r = p.Position - s.VortexCenter;
					acc += new Vector2( -r.Y, r.X ) * (s.VortexStrength / (r.LengthSquared() + s.VortexRadius * s.VortexRadius));
				}
				if ( s.Drag > 0 )
				{
					float f = 1 - MathF.Exp( -s.Drag * h );
					p.Velocity += (s.Wind - p.Velocity) * f;
				}
				p.Velocity += acc * h;
				p.Position += p.Velocity * h;
				p.Rotation += p.AngularVelocity * h;
				if ( s.FloorEnabled && p.Position.Y > s.FloorY )
				{
					p.Position.Y = s.FloorY;
					if ( p.Velocity.Y > 0 ) p.Velocity.Y = -p.Velocity.Y * s.Restitution;
					p.Velocity.X *= 1 - s.Friction;
				}
			}
		}
	}

	float NormalizedAge( in Particle2DState p ) => p.Lifetime > 0 ? Math.Clamp( p.Age / p.Lifetime, 0, 1 ) : 1;

	/// <summary>Draw record for one alive slot. Throws when the slot is dead or out of range.</summary>
	public Particle2DDrawRecord DrawRecordFor( int slot )
	{
		if ( !IsAlive( slot ) ) throw new InvalidOperationException( $"Slot {slot} holds no live particle." );
		var p = _state[slot];
		float t = NormalizedAge( p );
		int frame = p.FrameBase;
		if ( p.FrameAdvance > 0 && p.FrameCount > 1 )
			frame = p.FrameMin + (p.FrameBase - p.FrameMin + (int)(p.Age * p.FrameAdvance)) % p.FrameCount;
		return new Particle2DDrawRecord
		{
			Position = p.Position,
			Size = p.SizeStart + (p.SizeEnd - p.SizeStart) * t,
			Rotation = p.Rotation,
			Color = Particle2DColor.Lerp( p.ColorStart, p.ColorEnd, t ),
			Frame = frame,
		};
	}

	/// <summary>Writes one draw record per live particle, in slot order (2D has no depth to sort by). Returns count written; throws when the destination is too small.</summary>
	public int WriteDrawRecords( Span<Particle2DDrawRecord> destination )
	{
		if ( destination.Length < AliveCount ) throw new ArgumentException( $"Destination holds {destination.Length} records but {AliveCount} particles are alive." );
		int n = 0;
		for ( int i = 0; i < _highWater; i++ )
			if ( _state[i].Alive ) destination[n++] = DrawRecordFor( i );
		return n;
	}
}

/// <summary>Built-in 2D emitter presets. Units are canvas pixels, Y-down, seconds, like the rest of Particle2D.
/// A preset is an emitter shape plus launch numbers; <see cref="SettingsFor"/> is the medium that makes
/// it read (bubbles rise, snow drifts). Settings are per effect system: build one system per preset with
/// SettingsFor, or presets sharing a system fight over one gravity. Cue names resolve like the 3D
/// presets: an exact name, an fx:/vfx:/particle:-prefixed one, or a name carrying the preset as a whole
/// underscore-separated word (e.g. footstep_splash).</summary>
public static class Particle2DPresets
{
	public static readonly string[] Names =
		{ "sparks", "rain", "snow", "bubbles", "confetti", "magic", "dust", "splash" };

	public static string? ResolveName( string cueName )
	{
		if ( string.IsNullOrWhiteSpace( cueName ) ) return null;
		var n = cueName.Trim().ToLowerInvariant();
		foreach ( var p in new[] { "fx:", "fx_", "vfx:", "vfx_", "particle:", "particle_" } )
			if ( n.StartsWith( p ) ) { n = n[p.Length..]; break; }
		var flat = n.Replace( '-', '_' ).Replace( ':', '_' ).Replace( ' ', '_' ).Replace( '/', '_' );
		foreach ( var name in Names )
			if ( flat == name || flat.StartsWith( name + "_" ) || flat.EndsWith( "_" + name ) || flat.Contains( "_" + name + "_" ) )
				return name;
		return null;
	}

	/// <summary>Fresh emitter for a preset at <paramref name="origin"/>. A non-zero <paramref name="direction"/> re-aims the launch; otherwise the preset's own direction stands.</summary>
	public static Particle2DEmitter Create( string name, Vector2 origin, Vector2? direction = null )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown 2D particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var em = new Particle2DEmitter { Position = origin };
		switch ( key )
		{
			case "sparks":
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 420f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 60f;
				em.LifetimeMin = 0.2f; em.LifetimeMax = 0.6f; em.SizeMin = 2f; em.SizeMax = 5f;
				em.ColorStart = new Particle2DColor( 1, 0.8f, 0.2f, 1 ); em.ColorEnd = new Particle2DColor( 1, 0.3f, 0, 0 );
				em.AngularSpeedMax = 12f; em.Bursts.Add( new Particle2DBurst( 0, 70 ) );
				break;
			case "rain":
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 400, 10 );
				em.Direction = new Vector2( 0.15f, 1 );
				em.Speed = 900f; em.SpeedJitter = 0.1f; em.SpreadDegrees = 2f;
				em.LifetimeMin = 2f; em.LifetimeMax = 2f; em.SizeMin = 2f; em.SizeMax = 3f;
				em.ColorStart = new Particle2DColor( 0.6f, 0.8f, 1, 0.7f ); em.ColorEnd = new Particle2DColor( 0.6f, 0.8f, 1, 0 );
				em.Rate = 300f;
				break;
			case "snow":
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 400, 10 );
				em.Direction = new Vector2( 0, 1 );
				em.Speed = 40f; em.SpeedJitter = 0.8f; em.SpreadDegrees = 30f;
				em.LifetimeMin = 6f; em.LifetimeMax = 10f; em.SizeMin = 2f; em.SizeMax = 5f;
				em.ColorStart = new Particle2DColor( 1, 1, 1, 0.9f ); em.ColorEnd = new Particle2DColor( 1, 1, 1, 0.9f );
				em.Rate = 60f;
				break;
			case "bubbles":
				em.Shape = Particle2DEmitterShape.Circle; em.CircleRadius = 30f;
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 30f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 20f;
				em.LifetimeMin = 2f; em.LifetimeMax = 4f; em.SizeMin = 3f; em.SizeMax = 8f;
				em.ColorStart = new Particle2DColor( 0.7f, 0.9f, 1, 0.6f ); em.ColorEnd = new Particle2DColor( 0.7f, 0.9f, 1, 0 );
				em.Rate = 40f;
				break;
			case "confetti":
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 500f; em.SpeedJitter = 0.4f; em.SpreadDegrees = 80f;
				em.LifetimeMin = 1.5f; em.LifetimeMax = 3f; em.SizeMin = 4f; em.SizeMax = 8f;
				em.Palette = new List<Particle2DColor>
				{
					new( 1, 0.2f, 0.2f, 1 ), new( 1, 0.85f, 0.2f, 1 ), new( 0.2f, 0.85f, 1, 1 ),
					new( 1, 0.3f, 0.85f, 1 ), new( 0.3f, 0.9f, 0.4f, 1 ),
				};
				em.EndAlpha = 0;
				em.AngularSpeedMax = 12f; em.Bursts.Add( new Particle2DBurst( 0, 120 ) );
				break;
			case "magic":
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 60f; em.SpeedJitter = 0.6f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 0.8f; em.LifetimeMax = 1.8f; em.SizeMin = 2f; em.SizeMax = 5f;
				em.ColorStart = new Particle2DColor( 0.8f, 0.6f, 1, 1 ); em.ColorEnd = new Particle2DColor( 1, 0.9f, 0.4f, 0 );
				em.FrameMin = 0; em.FrameMax = 3; em.FrameAdvance = 8f;
				em.Rate = 30f;
				break;
			case "dust":
				em.Shape = Particle2DEmitterShape.Box; em.BoxHalfExtents = new Vector2( 200, 100 );
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 8f; em.SpeedJitter = 0.8f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 4f; em.LifetimeMax = 8f; em.SizeMin = 1f; em.SizeMax = 3f;
				em.ColorStart = new Particle2DColor( 0.9f, 0.85f, 0.7f, 0.35f ); em.ColorEnd = new Particle2DColor( 0.9f, 0.85f, 0.7f, 0 );
				em.Rate = 12f;
				break;
			case "splash":
				em.Direction = new Vector2( 0, -1 );
				em.Speed = 380f; em.SpeedJitter = 0.4f; em.SpreadDegrees = 50f;
				em.LifetimeMin = 0.4f; em.LifetimeMax = 0.9f; em.SizeMin = 2f; em.SizeMax = 5f;
				em.ColorStart = new Particle2DColor( 0.5f, 0.8f, 1, 0.9f ); em.ColorEnd = new Particle2DColor( 0.5f, 0.8f, 1, 0 );
				em.Bursts.Add( new Particle2DBurst( 0, 90 ) );
				break;
		}
		if ( direction is { } d && d.LengthSquared() > 1e-12f ) em.Direction = d;
		return em;
	}

	/// <summary>A copy of <paramref name="baseSettings"/> with this preset's medium applied. The base is never mutated.</summary>
	public static Particle2DSettings SettingsFor( string name, Particle2DSettings baseSettings )
	{
		ArgumentNullException.ThrowIfNull( baseSettings );
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown 2D particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var s = Clone( baseSettings );
		switch ( key )
		{
			case "sparks": s.Drag = 0.05f; break;
			case "rain": s.Gravity = new Vector2( 0, 1400f ); s.Drag = 0.1f; break;
			case "snow":
				s.Gravity = new Vector2( 0, 60f ); s.Drag = 1.2f;
				s.TurbulenceStrength = 30f; s.TurbulenceFrequency = 0.01f; s.TurbulenceSpeed = 0.5f;
				break;
			case "bubbles":
				s.Gravity = new Vector2( 0, -90f ); s.Drag = 0.8f;
				s.TurbulenceStrength = 12f; s.TurbulenceFrequency = 0.02f;
				break;
			case "confetti": s.Drag = 0.6f; break;
			case "magic":
				s.Gravity = new Vector2( 0, -40f ); s.Drag = 1.5f;
				s.TurbulenceStrength = 20f; s.TurbulenceFrequency = 0.02f;
				break;
			case "dust":
				s.Gravity = new Vector2( 0, -6f ); s.Drag = 0.5f;
				s.TurbulenceStrength = 6f; s.TurbulenceFrequency = 0.015f; s.Wind = new Vector2( 10, 0 );
				break;
			case "splash": s.Drag = 0.1f; break;
		}
		return s;
	}

	static Particle2DSettings Clone( Particle2DSettings s ) => new()
	{
		Capacity = s.Capacity, Seed = s.Seed, FixedDeltaTime = s.FixedDeltaTime, Substeps = s.Substeps,
		Gravity = s.Gravity, Wind = s.Wind, Drag = s.Drag,
		TurbulenceStrength = s.TurbulenceStrength, TurbulenceFrequency = s.TurbulenceFrequency, TurbulenceSpeed = s.TurbulenceSpeed,
		VortexStrength = s.VortexStrength, VortexCenter = s.VortexCenter, VortexRadius = s.VortexRadius,
		FloorEnabled = s.FloorEnabled, FloorY = s.FloorY, Restitution = s.Restitution, Friction = s.Friction,
	};
}
