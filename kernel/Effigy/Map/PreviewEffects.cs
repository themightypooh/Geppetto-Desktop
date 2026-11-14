using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Hammer Desktop P2: WYSIWYG preview evaluation — particles, fog, ropes,
/// material proxies, render effects. Board task hammer-preview-wysiwyg-effects.
///
/// Preview-state evaluation ONLY — no viewport code, no UI: each method
/// simulates one effect as plain data (positions, densities, curve points,
/// UV offsets, colour/alpha) that a future viewport or AI caller renders.
/// All evaluation is pure and deterministic: same inputs -&gt; same outputs,
/// no RNG state, no clock reads. Geometry is the kernel's inches and Z-up.
/// Time t is preview seconds from the effect start; negative t clamps to 0.
///
/// DEPENDENCY STATUS (2026-11-13): the three tasks this one depends on had
/// NOT landed when this was written — hammer-soundscapes-particle-preview-backend,
/// hammer-model-particle-sound-registries and hammer-lighting-environment-sun-fog
/// are all still `todo` with no files on disk, and they name no concrete member
/// names for their types. So this file defines its own minimal input DTOs
/// (PreviewParticleSystem, PreviewFogController, PreviewRopeSpan,
/// PreviewMaterialProxy, PreviewRenderFxEntity) using the Source-1 keyvalue
/// names those specs describe (fogstart/fogend/fogmaxdensity, move_rope slack,
/// TextureScroll rates, rendermode/renderamt/rendercolor/renderfx). When the
/// dependency tickets land, the integration is a thin adapter that maps their
/// types onto these DTOs — this file's public evaluation API does not change.
/// Do NOT invent a second preview evaluator against the dependency types;
/// adapt this one.
///
/// NOTE: PreviewParticleSystem is deliberately NOT the kernel's existing
/// ParticleSystemDef (the Desktop particle-authoring definition). That type
/// is the authoring model for Geppetto's own particle system; this DTO is the
/// small Source-1 info_particle_system parameter set the Hammer registry spec
/// describes. An adapter can fill one from the other when the registry lands.
///
/// Per-effect toggles: see <see cref="PreviewToggles"/> and
/// <see cref="PreviewEffects.EvaluateBatch"/>.
/// </summary>
public static class PreviewEffects
{
	/// <summary>Hard cap on particles evaluated in one call, so a huge
	/// emission-rate x lifetime product cannot allocate unboundedly.
	/// When hit, <see cref="ParticleEvaluation.Capped"/> is true.</summary>
	public const int MaxParticlesPerEvaluation = 4096;

	/// <summary>Default number of segments in a rope sag curve
	/// (<see cref="EvaluateRope"/> returns Segments + 1 points).</summary>
	public const int DefaultRopeSegments = 16;

	// ------------------------------------------------------------------
	// Shared helpers
	// ------------------------------------------------------------------

	static float Clamp01( float v ) => v < 0f ? 0f : v > 1f ? 1f : v;

	/// <summary>Wrap to [0,1). Works for negative inputs, unlike %.</summary>
	static float Wrap01( float x ) => x - MathF.Floor( x );

	/// <summary>Deterministic 32-bit integer hash (no RNG state).</summary>
	static uint Hash32( int n )
	{
		uint x = (uint)n * 2654435761u;
		x ^= x >> 15;
		x *= 2246822519u;
		x ^= x >> 13;
		x *= 3266489917u;
		x ^= x >> 16;
		return x;
	}

	/// <summary>Deterministic pseudo-random value in [0,1) from an integer seed.</summary>
	static float Unit01( int seed ) => Hash32( seed ) / 4294967295f;

	static float ClampTime( float t ) => t < 0f ? 0f : t;

	// ------------------------------------------------------------------
	// Particles: EvaluateParticles(system, t)
	// ------------------------------------------------------------------

	/// <summary>Source-1 info_particle_system parameter set, as the
	/// particle-registry spec describes it (system name + params).
	/// Origin/Direction/Speed/Spread describe the emitter cone;
	/// Gravity is a constant acceleration (Z-up, inches/s^2).</summary>
	public sealed class PreviewParticleSystem
	{
		public string Name { get; set; } = string.Empty;
		public Vec3 Origin { get; set; } = Vec3.Zero;
		/// <summary>Emitter cone axis. Zero falls back to straight up.</summary>
		public Vec3 Direction { get; set; } = new Vec3( 0, 0, 1 );
		/// <summary>Particles spawned per second. &lt;= 0 emits nothing.</summary>
		public float EmissionRate { get; set; }
		/// <summary>Seconds a particle lives. &lt;= 0 emits nothing.</summary>
		public float Lifetime { get; set; } = 1f;
		/// <summary>Initial speed along the cone axis, inches/s.</summary>
		public float Speed { get; set; }
		/// <summary>Cone spread, 0 (single direction) to 1 (full jitter).</summary>
		public float Spread { get; set; }
		public Vec3 Gravity { get; set; } = Vec3.Zero;
		public float StartSize { get; set; } = 1f;
		public float EndSize { get; set; } = 1f;
	}

	/// <summary>One live particle at the evaluated time.</summary>
	public sealed class ParticleState
	{
		/// <summary>Spawn order (0 = first spawned). Stable across t.</summary>
		public int Index { get; }
		public Vec3 Position { get; }
		public Vec3 Velocity { get; }
		public float Age { get; }
		/// <summary>Age / Lifetime, 0 at spawn, approaching 1 at death.</summary>
		public float LifeFraction { get; }
		public float Size { get; }
		/// <summary>Linear fade-out: 1 at spawn, 0 at death.</summary>
		public float Alpha { get; }

		public ParticleState( int index, Vec3 position, Vec3 velocity, float age, float lifeFraction, float size, float alpha )
		{
			Index = index;
			Position = position;
			Velocity = velocity;
			Age = age;
			LifeFraction = lifeFraction;
			Size = size;
			Alpha = alpha;
		}
	}

	/// <summary>Result of <see cref="EvaluateParticles"/>.</summary>
	public sealed class ParticleEvaluation
	{
		public IReadOnlyList<ParticleState> Particles { get; }
		public int AliveCount => Particles.Count;
		/// <summary>True when the alive set exceeded
		/// <see cref="MaxParticlesPerEvaluation"/> and was truncated
		/// (earliest alive particles kept).</summary>
		public bool Capped { get; }
		public float Time { get; }

		public ParticleEvaluation( IReadOnlyList<ParticleState> particles, bool capped, float time )
		{
			Particles = particles;
			Capped = capped;
			Time = time;
		}
	}

	/// <summary>Simulate a particle system at preview time t.
	/// Particle i spawns at i / EmissionRate and lives Lifetime seconds, so
	/// positions at t=0 and t=1 differ and dead particles drop out.
	/// Per-particle jitter comes from a deterministic hash of the index, so
	/// the same system at the same t always gives the same positions.
	/// Zero/negative rate or lifetime, or a null system, never crashes:
	/// null throws <see cref="ArgumentNullException"/>, empty params
	/// evaluate to zero alive particles.</summary>
	public static ParticleEvaluation EvaluateParticles( PreviewParticleSystem system, float t )
	{
		if ( system is null ) throw new ArgumentNullException( nameof( system ) );
		t = ClampTime( t );
		var empty = new ParticleEvaluation( Array.Empty<ParticleState>(), false, t );
		if ( system.EmissionRate <= 0f || system.Lifetime <= 0f ) return empty;

		float rate = system.EmissionRate;
		int iMax = (int)MathF.Floor( t * rate );
		int iMin = (int)MathF.Floor( ( t - system.Lifetime ) * rate ) + 1;
		if ( iMin < 0 ) iMin = 0;
		int count = iMax - iMin + 1;
		if ( count <= 0 ) return empty;

		bool capped = false;
		if ( count > MaxParticlesPerEvaluation ) { count = MaxParticlesPerEvaluation; capped = true; }

		var dir = system.Direction.LengthSquared > 1e-12f ? system.Direction.Normal : new Vec3( 0, 0, 1 );
		float spreadSpeed = system.Spread * system.Speed;
		var list = new List<ParticleState>( count );
		for ( int i = iMin; i < iMin + count; i++ )
		{
			float spawn = i / rate;
			float age = t - spawn;
			float life = age / system.Lifetime;
			// Deterministic per-index jitter in [-1,1]^3; Spread 0 -> exact cone axis.
			var jitter = new Vec3(
				Unit01( i * 3 + 0 ) * 2f - 1f,
				Unit01( i * 3 + 1 ) * 2f - 1f,
				Unit01( i * 3 + 2 ) * 2f - 1f );
			var velocity = dir * system.Speed + jitter * spreadSpeed;
			var position = system.Origin + velocity * age + system.Gravity * ( 0.5f * age * age );
			float size = system.StartSize + ( system.EndSize - system.StartSize ) * life;
			list.Add( new ParticleState( i, position, velocity, age, life, size, 1f - life ) );
		}
		return new ParticleEvaluation( list, capped, t );
	}

	// ------------------------------------------------------------------
	// Fog: EvaluateFog(controller, cameraPos)
	// ------------------------------------------------------------------

	/// <summary>Source-1 env_fog_controller parameter set
	/// (fogstart / fogend / fogmaxdensity / fogcolor). Colours are 0..1,
	/// distances inches. Origin is the controller entity's origin.</summary>
	public sealed class PreviewFogController
	{
		public Vec3 Origin { get; set; } = Vec3.Zero;
		/// <summary>Distance where fog starts to appear. &lt;= 0 means "at the origin".</summary>
		public float FogStart { get; set; }
		/// <summary>Distance where fog reaches full density.</summary>
		public float FogEnd { get; set; } = 1024f;
		/// <summary>Density at/after FogEnd, 0..1.</summary>
		public float FogMaxDensity { get; set; } = 1f;
		/// <summary>Fog colour, RGB 0..1.</summary>
		public Vec3 FogColor { get; set; } = new Vec3( 0.5f, 0.5f, 0.5f );
		public bool Enabled { get; set; } = true;
	}

	/// <summary>Result of <see cref="EvaluateFog"/> /
	/// <see cref="EvaluateFogAt"/>.</summary>
	public sealed class FogEvaluation
	{
		/// <summary>0..FogMaxDensity at the sampled distance.</summary>
		public float Density { get; }
		/// <summary>Normalised 0..1 fog factor (Density / FogMaxDensity).</summary>
		public float Factor { get; }
		public Vec3 Color { get; }
		/// <summary>Distance the factor was sampled at, inches.</summary>
		public float Distance { get; }
		public bool Enabled { get; }

		public FogEvaluation( float density, float factor, Vec3 color, float distance, bool enabled )
		{
			Density = density;
			Factor = factor;
			Color = color;
			Distance = distance;
			Enabled = enabled;
		}
	}

	static FogEvaluation EvaluateFogDistance( PreviewFogController controller, float distance )
	{
		if ( !controller.Enabled ) return new FogEvaluation( 0f, 0f, controller.FogColor, distance, false );
		float maxDensity = Clamp01( controller.FogMaxDensity );
		float factor;
		if ( controller.FogEnd <= controller.FogStart )
			factor = distance <= controller.FogStart ? 0f : 1f;
		else
			factor = Clamp01( ( distance - controller.FogStart ) / ( controller.FogEnd - controller.FogStart ) );
		return new FogEvaluation( factor * maxDensity, factor, controller.FogColor, distance, true );
	}

	/// <summary>Evaluate fog at a camera position, sampled at its distance
	/// from the controller origin: density rises with distance, 0 inside
	/// FogStart, FogMaxDensity at/after FogEnd, 0 when disabled.
	/// This is the preview-model form (the controller is a point entity);
	/// for true view-distance fog from a camera to a sample point use
	/// <see cref="EvaluateFogAt"/>.</summary>
	public static FogEvaluation EvaluateFog( PreviewFogController controller, Vec3 cameraPos )
	{
		if ( controller is null ) throw new ArgumentNullException( nameof( controller ) );
		return EvaluateFogDistance( controller, ( cameraPos - controller.Origin ).Length );
	}

	/// <summary>Evaluate fog between a camera and a sample point (the Source
	/// view-distance semantic: fogstart/fogend are distances from the
	/// camera). Density rises with |samplePos - cameraPos|.</summary>
	public static FogEvaluation EvaluateFogAt( PreviewFogController controller, Vec3 cameraPos, Vec3 samplePos )
	{
		if ( controller is null ) throw new ArgumentNullException( nameof( controller ) );
		return EvaluateFogDistance( controller, ( samplePos - cameraPos ).Length );
	}

	// ------------------------------------------------------------------
	// Ropes: EvaluateRope(endpoints, slack)
	// ------------------------------------------------------------------

	/// <summary>The two endpoints of a rope (Source move_rope /
	/// keyframe_rope). Z-up: sag pulls the curve down in -Z.</summary>
	public sealed class PreviewRopeSpan
	{
		public Vec3 Start { get; set; } = Vec3.Zero;
		public Vec3 End { get; set; } = Vec3.Zero;

		public PreviewRopeSpan() { }
		public PreviewRopeSpan( Vec3 start, Vec3 end ) { Start = start; End = end; }
	}

	/// <summary>Result of <see cref="EvaluateRope"/>.</summary>
	public sealed class RopeEvaluation
	{
		/// <summary>Segments + 1 points, first = Start, last = End.</summary>
		public IReadOnlyList<Vec3> Points { get; }
		/// <summary>Lowest dip below the straight line, inches (0 = taut).</summary>
		public float SagDepth { get; }
		/// <summary>Approximate rope length (polyline through Points).</summary>
		public float Length { get; }
		public float StraightLength { get; }
		public float Slack { get; }
		public int Segments { get; }

		public RopeEvaluation( IReadOnlyList<Vec3> points, float sagDepth, float length, float straightLength, float slack, int segments )
		{
			Points = points;
			SagDepth = sagDepth;
			Length = length;
			StraightLength = straightLength;
			Slack = slack;
			Segments = segments;
		}
	}

	/// <summary>Sag curve for a rope between endpoints with the given slack
	/// (extra length beyond the straight line, inches; Source `slack`
	/// keyvalue). Parabolic profile — the standard preview approximation
	/// of a catenary: sag depth d = sqrt(3 * span * slack / 8), points at
	/// 4*u*(1-u)*d below the straight line. Slack 0 gives a taut straight
	/// rope; coincident endpoints give a single point repeated.
	/// Negative slack throws <see cref="ArgumentOutOfRangeException"/>.</summary>
	public static RopeEvaluation EvaluateRope( PreviewRopeSpan endpoints, float slack, int segments = DefaultRopeSegments )
	{
		if ( endpoints is null ) throw new ArgumentNullException( nameof( endpoints ) );
		if ( slack < 0f ) throw new ArgumentOutOfRangeException( nameof( slack ), "rope slack cannot be negative" );
		if ( segments < 1 || segments > 256 ) throw new ArgumentOutOfRangeException( nameof( segments ), "rope segments must be 1..256" );
		float span = ( endpoints.End - endpoints.Start ).Length;
		float sag = span <= 0f || slack <= 0f ? 0f : MathF.Sqrt( 3f * span * slack / 8f );
		var points = new List<Vec3>( segments + 1 );
		for ( int i = 0; i <= segments; i++ )
		{
			float u = (float)i / segments;
			var p = Vec3.Lerp( endpoints.Start, endpoints.End, u );
			if ( sag > 0f ) p = new Vec3( p.x, p.y, p.z - sag * 4f * u * ( 1f - u ) );
			points.Add( p );
		}
		float length = 0f;
		for ( int i = 1; i < points.Count; i++ ) length += ( points[i] - points[i - 1] ).Length;
		return new RopeEvaluation( points, sag, length, span, slack, segments );
	}

	// ------------------------------------------------------------------
	// Material proxies: EvaluateProxy(material, t)
	// ------------------------------------------------------------------

	/// <summary>Animated material-proxy parameter set (Source material
	/// proxies: TextureScroll scroll rates, TextureTransform scale/rotate,
	/// animated-texture frame rate). Offsets wrap to [0,1), rotation to
	/// [0,360).</summary>
	public sealed class PreviewMaterialProxy
	{
		public string MaterialName { get; set; } = string.Empty;
		/// <summary>UV scroll, units/second (TextureScroll).</summary>
		public float ScrollRateU { get; set; }
		public float ScrollRateV { get; set; }
		/// <summary>Base UV scale (TextureTransform). 1 = unscaled.</summary>
		public float ScaleU { get; set; } = 1f;
		public float ScaleV { get; set; } = 1f;
		/// <summary>UV rotation speed, degrees/second.</summary>
		public float RotateRateDegrees { get; set; }
		/// <summary>Animated-texture frame count; &lt;= 1 disables framing.</summary>
		public int Frames { get; set; }
		/// <summary>Animated-texture frames/second.</summary>
		public float FrameRate { get; set; }
	}

	/// <summary>Result of <see cref="EvaluateProxy"/>.</summary>
	public sealed class ProxyEvaluation
	{
		/// <summary>UV offset at t, each component in [0,1).</summary>
		public float OffsetU { get; }
		public float OffsetV { get; }
		public float ScaleU { get; }
		public float ScaleV { get; }
		/// <summary>UV rotation at t, degrees in [0,360).</summary>
		public float RotationDegrees { get; }
		/// <summary>Animated-texture frame at t, or -1 when unframed.</summary>
		public int FrameIndex { get; }
		public float Time { get; }

		public ProxyEvaluation( float offsetU, float offsetV, float scaleU, float scaleV, float rotationDegrees, int frameIndex, float time )
		{
			OffsetU = offsetU;
			OffsetV = offsetV;
			ScaleU = scaleU;
			ScaleV = scaleV;
			RotationDegrees = rotationDegrees;
			FrameIndex = frameIndex;
			Time = time;
		}
	}

	/// <summary>Evaluate an animated material proxy at preview time t:
	/// the UV offset advances with time (offset = rate * t, wrapped),
	/// as does rotation; the animated-texture frame advances at FrameRate.
	/// Scale is time-independent and passes through.</summary>
	public static ProxyEvaluation EvaluateProxy( PreviewMaterialProxy material, float t )
	{
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		t = ClampTime( t );
		float offsetU = Wrap01( material.ScrollRateU * t );
		float offsetV = Wrap01( material.ScrollRateV * t );
		float rotation = Wrap01( material.RotateRateDegrees * t / 360f ) * 360f;
		int frame = -1;
		if ( material.Frames > 1 && material.FrameRate > 0f )
			frame = (int)MathF.Floor( t * material.FrameRate ) % material.Frames;
		return new ProxyEvaluation( offsetU, offsetV, material.ScaleU, material.ScaleV, rotation, frame, t );
	}

	// ------------------------------------------------------------------
	// Render effects: EvaluateRenderFx(entity)
	// ------------------------------------------------------------------

	/// <summary>Source rendermode values (0..5).</summary>
	public enum PreviewRenderMode
	{
		Normal = 0,
		TransColor = 1,
		TransTexture = 2,
		Glow = 3,
		TransAlpha = 4,
		TransAdd = 5,
	}

	/// <summary>Source kRenderFx values (0..21). Only the time-varying ones
	/// change the evaluated alpha; the rest are visual-only in the engine
	/// and pass through unchanged here (documented per case in
	/// <see cref="EvaluateRenderFx"/>).</summary>
	public enum PreviewRenderFx
	{
		None = 0,
		PulseSlow = 1,
		PulseFast = 2,
		PulseSlowWide = 3,
		PulseFastWide = 4,
		FadeSlow = 5,
		FadeFast = 6,
		SolidSlow = 7,
		SolidFast = 8,
		StrobeSlow = 9,
		StrobeFast = 10,
		StrobeFaster = 11,
		FlickerSlow = 12,
		FlickerFast = 13,
		NoDissipation = 14,
		Distort = 15,
		Hologram = 16,
		DeadPlayer = 17,
		Explode = 18,
		GlowShell = 19,
		ClampMinScale = 20,
		LightMultiplier = 21,
	}

	/// <summary>An entity's render state (Source keyvalues rendermode /
	/// renderamt / rendercolor / renderfx, plus bodygroup and modelscale).
	/// RenderColor components are 0..255, as stored; the evaluation
	/// converts to 0..1. TimeSeconds drives the time-varying renderfx;
	/// the desc's EvaluateRenderFx(entity) takes no separate t.</summary>
	public sealed class PreviewRenderFxEntity
	{
		public string ClassName { get; set; } = string.Empty;
		public PreviewRenderMode Mode { get; set; } = PreviewRenderMode.Normal;
		public PreviewRenderFx Fx { get; set; } = PreviewRenderFx.None;
		public Vec3 RenderColor { get; set; } = new Vec3( 255, 255, 255 );
		/// <summary>0..255 opacity for the translucent modes.</summary>
		public float RenderAmt { get; set; } = 255f;
		public int BodyGroup { get; set; }
		/// <summary>modelscale multiplier. &lt; 0 clamps to 0.</summary>
		public float Scale { get; set; } = 1f;
		public float TimeSeconds { get; set; }
	}

	/// <summary>Result of <see cref="EvaluateRenderFx"/>.</summary>
	public sealed class RenderFxEvaluation
	{
		/// <summary>Render colour, RGB 0..1.</summary>
		public Vec3 Color { get; }
		/// <summary>Effective opacity 0..1 after mode and renderfx.</summary>
		public float Alpha { get; }
		public int BodyGroup { get; }
		public float Scale { get; }
		public PreviewRenderMode Mode { get; }
		public PreviewRenderFx Fx { get; }
		public bool IsVisible { get; }

		public RenderFxEvaluation( Vec3 color, float alpha, int bodyGroup, float scale, PreviewRenderMode mode, PreviewRenderFx fx, bool isVisible )
		{
			Color = color;
			Alpha = alpha;
			BodyGroup = bodyGroup;
			Scale = scale;
			Mode = mode;
			Fx = fx;
			IsVisible = isVisible;
		}
	}

	/// <summary>Evaluate an entity's render effects as preview data.
	/// Colour is rendercolor / 255; bodygroup and scale pass through;
	/// alpha is 1 for Normal and renderamt / 255 for the translucent
	/// modes, modulated by the time-varying renderfx at TimeSeconds:
	/// Pulse (sine, slow 2 s / fast 0.5 s period; Wide swings the full
	/// 0..1 range), Fade (triangle wave), Solid (smoothed triangle),
	/// Strobe (square wave: fully on / fully off), Flicker (deterministic
	/// hash flicker, slow 8 Hz / fast 20 Hz). NoDissipation, Distort,
	/// Hologram, DeadPlayer, Explode, GlowShell, ClampMinScale and
	/// LightMultiplier have no alpha effect in the preview model and pass
	/// through unchanged. IsVisible is false when alpha reaches 0.</summary>
	public static RenderFxEvaluation EvaluateRenderFx( PreviewRenderFxEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		float t = ClampTime( entity.TimeSeconds );
		var color = new Vec3(
			Clamp01( entity.RenderColor.x / 255f ),
			Clamp01( entity.RenderColor.y / 255f ),
			Clamp01( entity.RenderColor.z / 255f ) );
		float alpha = entity.Mode == PreviewRenderMode.Normal ? 1f : Clamp01( entity.RenderAmt / 255f );
		float factor = FxAlphaFactor( entity.Fx, t );
		alpha = Clamp01( alpha * factor );
		float scale = entity.Scale < 0f ? 0f : entity.Scale;
		return new RenderFxEvaluation( color, alpha, entity.BodyGroup, scale, entity.Mode, entity.Fx, alpha > 0.001f );
	}

	static float FxAlphaFactor( PreviewRenderFx fx, float t )
	{
		const float twoPi = MathF.PI * 2f;
		switch ( fx )
		{
			case PreviewRenderFx.PulseSlow:
				return 0.5f + 0.5f * MathF.Sin( twoPi * t / 2f );
			case PreviewRenderFx.PulseFast:
				return 0.5f + 0.5f * MathF.Sin( twoPi * t / 0.5f );
			case PreviewRenderFx.PulseSlowWide:
			{
				float s = 0.5f + 0.5f * MathF.Sin( twoPi * t / 2f );
				return s * s;
			}
			case PreviewRenderFx.PulseFastWide:
			{
				float s = 0.5f + 0.5f * MathF.Sin( twoPi * t / 0.5f );
				return s * s;
			}
			case PreviewRenderFx.FadeSlow:
				return Triangle( t / 2f );
			case PreviewRenderFx.FadeFast:
				return Triangle( t / 0.5f );
			case PreviewRenderFx.SolidSlow:
			{
				float s = Triangle( t / 2f );
				return s * s * ( 3f - 2f * s );
			}
			case PreviewRenderFx.SolidFast:
			{
				float s = Triangle( t / 0.5f );
				return s * s * ( 3f - 2f * s );
			}
			case PreviewRenderFx.StrobeSlow:
				return Wrap01( t / 1f ) < 0.5f ? 1f : 0f;
			case PreviewRenderFx.StrobeFast:
				return Wrap01( t / 0.25f ) < 0.5f ? 1f : 0f;
			case PreviewRenderFx.StrobeFaster:
				return Wrap01( t / 0.1f ) < 0.5f ? 1f : 0f;
			case PreviewRenderFx.FlickerSlow:
				return 0.3f + 0.7f * Unit01( (int)MathF.Floor( t * 8f ) * 7919 + 13 );
			case PreviewRenderFx.FlickerFast:
				return 0.3f + 0.7f * Unit01( (int)MathF.Floor( t * 20f ) * 7919 + 13 );
			default:
				return 1f;
		}
	}

	static float Triangle( float phase )
	{
		float p = Wrap01( phase );
		return p < 0.5f ? p * 2f : 2f - 2f * p;
	}

	// ------------------------------------------------------------------
	// Toggles + batch evaluation
	// ------------------------------------------------------------------

	/// <summary>Which preview effect types this ticket evaluates.</summary>
	public enum PreviewEffectKind
	{
		Particles,
		Fog,
		Ropes,
		Proxies,
		RenderFx,
	}

	/// <summary>Per-effect-type toggles for WYSIWYG preview evaluation.
	/// All on by default; a future viewport/AI caller passes one in and
	/// the batch evaluator skips disabled kinds. Immutable by copy:
	/// <see cref="With"/> returns a new instance.</summary>
	public sealed class PreviewToggles
	{
		public bool Particles { get; set; } = true;
		public bool Fog { get; set; } = true;
		public bool Ropes { get; set; } = true;
		public bool Proxies { get; set; } = true;
		public bool RenderFx { get; set; } = true;

		public static PreviewToggles AllOff() => new PreviewToggles
		{
			Particles = false,
			Fog = false,
			Ropes = false,
			Proxies = false,
			RenderFx = false,
		};

		public bool IsEnabled( PreviewEffectKind kind ) => kind switch
		{
			PreviewEffectKind.Particles => Particles,
			PreviewEffectKind.Fog => Fog,
			PreviewEffectKind.Ropes => Ropes,
			PreviewEffectKind.Proxies => Proxies,
			PreviewEffectKind.RenderFx => RenderFx,
			_ => false,
		};

		public PreviewToggles With( PreviewEffectKind kind, bool enabled )
		{
			var copy = new PreviewToggles
			{
				Particles = Particles,
				Fog = Fog,
				Ropes = Ropes,
				Proxies = Proxies,
				RenderFx = RenderFx,
			};
			switch ( kind )
			{
				case PreviewEffectKind.Particles: copy.Particles = enabled; break;
				case PreviewEffectKind.Fog: copy.Fog = enabled; break;
				case PreviewEffectKind.Ropes: copy.Ropes = enabled; break;
				case PreviewEffectKind.Proxies: copy.Proxies = enabled; break;
				case PreviewEffectKind.RenderFx: copy.RenderFx = enabled; break;
			}
			return copy;
		}
	}

	/// <summary>Optional inputs for one batch preview evaluation.
	/// Any field left null is skipped; Fog also needs FogCameraPos.</summary>
	public sealed class PreviewBatchInput
	{
		public PreviewParticleSystem Particles { get; set; }
		public PreviewFogController Fog { get; set; }
		public Vec3 FogCameraPos { get; set; } = Vec3.Zero;
		public PreviewRopeSpan Rope { get; set; }
		public float RopeSlack { get; set; }
		public PreviewMaterialProxy Proxy { get; set; }
		public PreviewRenderFxEntity RenderFx { get; set; }
		/// <summary>Preview time in seconds, shared by Particles/Proxy.</summary>
		public float Time { get; set; }
	}

	/// <summary>Result of <see cref="EvaluateBatch"/>: a field is null
	/// when its input was absent or its effect type was toggled off.</summary>
	public sealed class PreviewBatchResult
	{
		public ParticleEvaluation Particles { get; }
		public FogEvaluation Fog { get; }
		public RopeEvaluation Rope { get; }
		public ProxyEvaluation Proxy { get; }
		public RenderFxEvaluation RenderFx { get; }

		public PreviewBatchResult( ParticleEvaluation particles, FogEvaluation fog, RopeEvaluation rope, ProxyEvaluation proxy, RenderFxEvaluation renderFx )
		{
			Particles = particles;
			Fog = fog;
			Rope = rope;
			Proxy = proxy;
			RenderFx = renderFx;
		}
	}

	/// <summary>Evaluate every present input whose effect type is enabled
	/// in <paramref name="toggles"/> (null toggles = all on). Each
	/// evaluation is exactly the single-effect method above, so batch and
	/// individual results always agree.</summary>
	public static PreviewBatchResult EvaluateBatch( PreviewBatchInput input, PreviewToggles toggles )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		toggles ??= new PreviewToggles();
		return new PreviewBatchResult(
			input.Particles is not null && toggles.IsEnabled( PreviewEffectKind.Particles )
				? EvaluateParticles( input.Particles, input.Time ) : null,
			input.Fog is not null && toggles.IsEnabled( PreviewEffectKind.Fog )
				? EvaluateFog( input.Fog, input.FogCameraPos ) : null,
			input.Rope is not null && toggles.IsEnabled( PreviewEffectKind.Ropes )
				? EvaluateRope( input.Rope, input.RopeSlack ) : null,
			input.Proxy is not null && toggles.IsEnabled( PreviewEffectKind.Proxies )
				? EvaluateProxy( input.Proxy, input.Time ) : null,
			input.RenderFx is not null && toggles.IsEnabled( PreviewEffectKind.RenderFx )
				? EvaluateRenderFx( input.RenderFx ) : null );
	}
}
