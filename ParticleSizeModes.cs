#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>Which 3D size mode a particle renders with (wave-6 lane P31).</summary>
public enum ParticleSizeMode
{
	/// <summary>One scalar size on all axes — the existing behaviour.</summary>
	Uniform,
	/// <summary>Independent X/Y/Z multipliers on the base size.</summary>
	PerAxis,
	/// <summary>Size along the velocity axis grows with speed; other axes stay at base size.</summary>
	StretchBySpeed,
}

/// <summary>
/// Self-contained 3D size-mode evaluators: given one particle's base size,
/// velocity, and an over-life multiplier, return its rendered size as a
/// per-axis <see cref="Vector3"/>.
///
/// Conventions, taken from <see cref="ParticleSystem"/> (read, never edited here):
/// <list type="bullet">
/// <item>Units are kernel inches, Z-up, seconds — the same units as
/// <see cref="ParticleState.Size"/> (a scalar, drawn at spawn from the emitter's
/// SizeMin..SizeMax range).</item>
/// <item>Uniform mode reproduces the existing behaviour exactly: the old
/// render paths scale uniformly by the scalar size
/// (<c>Matrix4x4.CreateScale( p.Size )</c> for mesh instances, and a square
/// billboard of half-size <c>p.Size * .5f * fade</c> in
/// <c>ParticleLayer</c>). With an over-life multiplier of 1, Uniform returns
/// exactly (base, base, base) — no extra factor is smuggled in.</item>
/// <item>The over-life multiplier is an INPUT, not a curve: it is whatever lane
/// P27's over-life curves evaluate to for this particle's age/lifetime. This
/// file never sees age or lifetime, owns no curve, and applies the multiplier
/// multiplicatively on every axis, in every mode.</item>
/// <item>The layer's own fade-out shrink (last <c>FadeOut</c> of life in
/// <c>ParticleLayer</c>) is a separate, render-layer concern. Do NOT feed a
/// fade factor in as the over-life multiplier as well, or particles shrink
/// twice. The sweep integration must pick one shrink source per layer.</item>
/// </list>
///
/// Stretch-by-speed frame, stated plainly because it is the one place this
/// file can be misread: the returned vector is in a VELOCITY-ALIGNED frame,
/// X = along the velocity axis, Y/Z = the two cross axes at base size.
/// Orientation (which world direction "along velocity" points) belongs to
/// lane P30's orientation modes, which this lane does not own — so a caller
/// that applies this vector as a world-axis scale without P30's orientation
/// will stretch the wrong world axis for non-axis-aligned velocities. For an
/// axis-aligned velocity the reading is literal: velocity (10, 0, 0) stretches
/// X, velocity (0, 0, 10) stretches X in the velocity frame, i.e. Z in the
/// world once P30 orients it. The stretched AXIS LENGTH is unambiguous in
/// every frame and is what the tests pin down.
///
/// Stretch formula (documented, configurable):
/// <c>stretch = 1 + StretchFactor * speed</c>, where speed = |velocity| in
/// inches/second and StretchFactor is in seconds/inch (so the product is
/// dimensionless). At speed 0, stretch is exactly 1 — the result equals
/// Uniform, with no normalisation and therefore no division by zero and no
/// NaN. StretchBySpeed ignores <see cref="ParticleSizeModeSettings.AxisMultipliers"/>
/// (documented, tested); per-axis shaping and speed stretching are separate
/// modes, not stacked — stacking them is a sweep decision, not a silent default.
///
/// NOT built here (other lanes' scope, named so it does not creep in):
/// over-life curves themselves (P27), orientation modes (P30), wiring into
/// the sim step or any render layer (sweep integration lines only), 2D size
/// modes (P06's incrX/incrY covers 2D axis growth), save/serialisation (lane 5).
/// </summary>
public static class ParticleSizeModes
{
	/// <summary>Uniform mode: (base, base, base) times the over-life multiplier.</summary>
	public static Vector3 Uniform( float baseSize, float overLifeMultiplier = 1f )
	{
		ValidateScalar( baseSize, nameof( baseSize ) );
		ValidateScalar( overLifeMultiplier, nameof( overLifeMultiplier ) );
		float s = baseSize * overLifeMultiplier;
		return new Vector3( s, s, s );
	}

	/// <summary>
	/// Per-axis mode: (base * mult.X, base * mult.Y, base * mult.Z) times the
	/// over-life multiplier. Flat disc: (1, 1, 0.25). Long streak: (3, 0.5, 0.5).
	/// Tall column: (0.5, 0.5, 3). A zero component flattens that axis to 0.
	/// </summary>
	public static Vector3 PerAxis( float baseSize, Vector3 axisMultipliers, float overLifeMultiplier = 1f )
	{
		ValidateScalar( baseSize, nameof( baseSize ) );
		ValidateVector( axisMultipliers, nameof( axisMultipliers ) );
		ValidateScalar( overLifeMultiplier, nameof( overLifeMultiplier ) );
		float m = overLifeMultiplier;
		return new Vector3( baseSize * axisMultipliers.X * m, baseSize * axisMultipliers.Y * m, baseSize * axisMultipliers.Z * m );
	}

	/// <summary>
	/// Stretch-by-speed mode, velocity-aligned frame (see class docs):
	/// X = base * (1 + stretchFactor * |velocity|) * overLifeMultiplier,
	/// Y = Z = base * overLifeMultiplier. Zero velocity returns Uniform exactly.
	/// </summary>
	public static Vector3 StretchBySpeed( float baseSize, Vector3 velocity, float stretchFactor, float overLifeMultiplier = 1f )
	{
		ValidateScalar( baseSize, nameof( baseSize ) );
		ValidateFiniteVector( velocity, nameof( velocity ) );
		ValidateScalar( stretchFactor, nameof( stretchFactor ) );
		ValidateScalar( overLifeMultiplier, nameof( overLifeMultiplier ) );
		float stretch = 1f + stretchFactor * velocity.Length();
		float m = overLifeMultiplier;
		return new Vector3( baseSize * stretch * m, baseSize * m, baseSize * m );
	}

	/// <summary>Stretched-axis length alone, for callers that only need the scalar: base * (1 + stretchFactor * speed) * overLifeMultiplier.</summary>
	public static float StretchLength( float baseSize, Vector3 velocity, float stretchFactor, float overLifeMultiplier = 1f )
		=> StretchBySpeed( baseSize, velocity, stretchFactor, overLifeMultiplier ).X;

	/// <summary>Dispatches on <paramref name="settings"/>.Mode; every mode validates its inputs loudly.</summary>
	public static Vector3 Evaluate( ParticleSizeModeSettings settings, float baseSize, Vector3 velocity, float overLifeMultiplier = 1f )
	{
		if ( settings is null ) throw new ArgumentNullException( nameof( settings ) );
		settings.Validate();
		ValidateScalar( baseSize, nameof( baseSize ) );
		ValidateFiniteVector( velocity, nameof( velocity ) );
		ValidateScalar( overLifeMultiplier, nameof( overLifeMultiplier ) );
		return settings.Mode switch
		{
			ParticleSizeMode.Uniform => Uniform( baseSize, overLifeMultiplier ),
			ParticleSizeMode.PerAxis => PerAxis( baseSize, settings.AxisMultipliers, overLifeMultiplier ),
			ParticleSizeMode.StretchBySpeed => StretchBySpeed( baseSize, velocity, settings.StretchFactor, overLifeMultiplier ),
			_ => throw new ArgumentException( $"Unknown ParticleSizeMode: {(int)settings.Mode}." ),
		};
	}

	/// <summary>
	/// Loud validation for a single evaluation's inputs (also used by
	/// <see cref="ParticleSizeModeSettings.Validate"/> for its own fields).
	/// Rejects: negative base sizes, negative multipliers, negative stretch
	/// factors, negative over-life multipliers, and any NaN or infinite input.
	/// Zero is allowed everywhere (a zero base or multiplier renders as zero
	/// size on that axis; it is not an error). Throws
	/// <see cref="ArgumentException"/> naming the offending value.
	/// </summary>
	public static void Validate( float baseSize, Vector3 axisMultipliers, Vector3 velocity, float stretchFactor, float overLifeMultiplier )
	{
		ValidateScalar( baseSize, nameof( baseSize ) );
		ValidateVector( axisMultipliers, nameof( axisMultipliers ) );
		// Velocity is finite-only: its components may of course be negative.
		ValidateFiniteVector( velocity, nameof( velocity ) );
		ValidateScalar( stretchFactor, nameof( stretchFactor ) );
		ValidateScalar( overLifeMultiplier, nameof( overLifeMultiplier ) );
	}

	static void ValidateScalar( float value, string name )
	{
		if ( !float.IsFinite( value ) )
			throw new ArgumentException( $"{name} must be finite, got {value}." );
		if ( value < 0 )
			throw new ArgumentException( $"{name} must be >= 0, got {value}." );
	}

	static void ValidateVector( Vector3 value, string name )
	{
		ValidateFiniteVector( value, name );
		if ( value.X < 0 || value.Y < 0 || value.Z < 0 )
			throw new ArgumentException( $"{name} components must be >= 0, got {value}." );
	}

	static void ValidateFiniteVector( Vector3 value, string name )
	{
		if ( !float.IsFinite( value.X ) || !float.IsFinite( value.Y ) || !float.IsFinite( value.Z ) )
			throw new ArgumentException( $"{name} must be finite, got {value}." );
	}
}

/// <summary>
/// Settings for one size mode. Defaults: Uniform, unit multipliers, stretch
/// factor 0.1 s/inch (so a particle moving at 10 in/s stretches 2x).
/// Note: velocity is a physical input, not a setting — it is not validated
/// for negativity component-wise (a velocity component may of course be
/// negative); only its finiteness is checked at evaluation time.
/// </summary>
public sealed class ParticleSizeModeSettings
{
	public ParticleSizeMode Mode = ParticleSizeMode.Uniform;
	/// <summary>PerAxis only; ignored by Uniform and StretchBySpeed (documented).</summary>
	public Vector3 AxisMultipliers = Vector3.One;
	/// <summary>StretchBySpeed only, seconds/inch; ignored by Uniform and PerAxis.</summary>
	public float StretchFactor = 0.1f;

	public void Validate()
	{
		if ( !Enum.IsDefined( Mode ) )
			throw new ArgumentException( $"Unknown ParticleSizeMode: {(int)Mode}." );
		ParticleSizeModes.Validate( 1f, AxisMultipliers, Vector3.Zero, StretchFactor, 1f );
	}
}
