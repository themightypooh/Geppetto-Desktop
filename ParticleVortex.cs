#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// Lane P64 (particles session, 2026-10-24): vortex variants for the CPU particle sim.
// SELF-CONTAINED EVALUATORS ONLY — this file does not touch ParticleSystem.cs and is
// NOT wired into its force step. The coordinator sweep owns the hookup; the exact
// integration lines are listed at the bottom of this file.
//
// Conventions (same as ParticleSystem.cs): kernel inches, Z-up, seconds.
// Every evaluator is a pure function of particle POSITION ONLY returning an
// ACCELERATION in inches/s^2. A variant never moves a particle by itself: the sim
// would add the returned acceleration to its per-substep acceleration sum and
// integrate it at the 60 Hz fixed step, exactly like gravity and the existing
// vortex term in ParticleSystem.Integrate.
//
// Relationship to the existing vortex force (ParticleSystem.cs owns it — this lane
// extends it, it does not replace it):
//   existing: acc = cross(axis, r_perp) * Strength / (|r_perp|^2 + Radius^2).
//   That term is already finite at the axis (it goes to zero there) and peaks at
//   |r_perp| = Radius. The variants below keep that spirit but give each shape its
//   own documented profile instead of reusing the one formula for everything.
//
// SHARED CORE-CLAMP RULE (all three variants, item 4 of the brief):
//   Peak acceleration is capped at the variant's Strength (per component — see
//   below for the whirlpool's separate component strengths). Inside the core
//   radius the rotational (and, for the whirlpool, inward) magnitude ramps
//   LINEARLY to zero at the axis/filament: magnitude = Strength * (d / CoreRadius)
//   for d <= CoreRadius, so a particle exactly on the axis feels NO rotational
//   acceleration — never an infinity, never a 1/d blow-up. Outside the core the
//   magnitude decays (tornado/whirlpool: CoreRadius / d; vortex ring: the cube
//   law in its tube-distance section). The cap is therefore exact, not a clamp
//   bolted on after the fact: no input distance can produce a component larger
//   than its Strength, and the maximum is attained exactly at the core edge.
//   The one exception to "zero at the axis" is a variant's AXIAL draft: a
//   tornado's updraft and a whirlpool's drain are strongest AT the axis (that is
//   the point of a funnel/drain) and decay outward; they are capped by their own
//   strengths (UpdraftStrength / DrainStrength) at the axis, never above them.
//
// Axes: every Axis is normalised inside Acceleration, so a non-unit axis input
// behaves identically to the same direction at unit length. Validate() rejects a
// zero axis loudly; Acceleration on an unvalidated zero axis returns zero rather
// than producing NaN (defensive, not a substitute for Validate).
//
// Out of scope, by lane boundary: the existing vortex force itself, force fields
// generally (lane P22), GPU vortices, wiring variants into the force step (sweep
// integration lines only, below), and save/serialization (lane 5).

/// <summary>Shared validation helpers for this file (kept local so it stays self-contained).</summary>
internal static class ParticleVortexChecks
{
	public static bool Finite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );

	public static void NeedFinite( Vector3 v, string name )
	{
		if ( !Finite( v ) ) throw new ArgumentException( $"{name} must be finite." );
	}

	public static void NeedNonNegative( float value, string name )
	{
		if ( !float.IsFinite( value ) || value < 0 ) throw new ArgumentOutOfRangeException( name, $"{name} must be finite and >= 0." );
	}

	public static void NeedPositive( float value, string name )
	{
		if ( !float.IsFinite( value ) || value <= 0 ) throw new ArgumentOutOfRangeException( name, $"{name} must be finite and > 0." );
	}

	public static Vector3 SafeAxis( Vector3 axis )
		=> axis.LengthSquared() >= 1e-12f ? Vector3.Normalize( axis ) : Vector3.Zero;

	/// <summary>Rankine profile, the shared core rule made concrete: linear ramp
	/// inside the core, 1/d decay outside. Returns a factor in [0, 1] times
	/// Strength's scale — i.e. magnitude = Strength * Rankine(d, core).</summary>
	public static float Rankine( float d, float core )
		=> d <= core ? d / core : core / d;
}

/// <summary>
/// Tornado: a vertical vortex column. Axis defaults to +Z (vertical, Z-up);
/// rotation is about that axis, and an upward draft runs along it.
/// <para>
/// Tangential magnitude: Rankine profile with <see cref="Strength"/> — linear
/// ramp to zero at the axis, peak <see cref="Strength"/> at
/// <see cref="CoreRadius"/>, CoreRadius/d decay beyond it.
/// </para>
/// <para>
/// Updraft: <see cref="UpdraftStrength"/> * CoreRadius / (d + CoreRadius),
/// where d is the perpendicular distance from the axis. It is STRONGEST at the
/// axis (the full <see cref="UpdraftStrength"/>) and grows toward the core
/// from outside — at the core edge it is half strength, at 3 core radii a
/// quarter. The column is independent of height along the axis (an infinite
/// column); a finite-height funnel is out of scope.
/// </para>
/// </summary>
public sealed class ParticleTornadoVortex
{
	public Vector3 Center;
	public Vector3 Axis = new( 0, 0, 1 );
	/// <summary>Peak tangential acceleration (in/s^2), attained at <see cref="CoreRadius"/>.</summary>
	public float Strength;
	/// <summary>Core radius in inches (&gt; 0). Inside it, rotation ramps linearly to zero at the axis.</summary>
	public float CoreRadius = 1f;
	/// <summary>Updraft acceleration (in/s^2) at the axis; decays as CoreRadius / (d + CoreRadius).</summary>
	public float UpdraftStrength;

	public void Validate()
	{
		ParticleVortexChecks.NeedFinite( Center, nameof( Center ) );
		ParticleVortexChecks.NeedFinite( Axis, nameof( Axis ) );
		if ( Axis.LengthSquared() < 1e-12f ) throw new ArgumentException( "Tornado axis must be non-zero." );
		ParticleVortexChecks.NeedNonNegative( Strength, nameof( Strength ) );
		ParticleVortexChecks.NeedNonNegative( UpdraftStrength, nameof( UpdraftStrength ) );
		ParticleVortexChecks.NeedPositive( CoreRadius, nameof( CoreRadius ) );
	}

	/// <summary>Pure function of position: tangential Rankine rotation + axial updraft.</summary>
	public Vector3 Acceleration( Vector3 position )
	{
		Vector3 axis = ParticleVortexChecks.SafeAxis( Axis );
		if ( axis == Vector3.Zero ) return Vector3.Zero;
		Vector3 r = position - Center;
		Vector3 rp = r - axis * Vector3.Dot( r, axis );
		float d = rp.Length();
		Vector3 acc = Vector3.Zero;
		if ( d > 0 )
		{
			// cross(axis, rp) has length d, so dividing by d gives the unit tangent.
			acc += Vector3.Cross( axis, rp ) / d * (Strength * ParticleVortexChecks.Rankine( d, CoreRadius ));
		}
		// Updraft along +Axis: strongest at the axis, half at the core edge.
		acc += axis * (UpdraftStrength * CoreRadius / (d + CoreRadius));
		return acc;
	}
}

/// <summary>
/// Whirlpool: a vortex whose rotation lies in a HORIZONTAL plane — i.e. its
/// axis is vertical by default (+Z, Z-up) — with an inward pull toward the axis
/// and a drain downward, so particles spiral inward and down.
/// <para>
/// Axis is configurable: any axis works, and the drain always runs along
/// −Axis (the documented "Z-down drain" is simply the default-axis case:
/// Axis = +Z, drain = −Z). Rotation sense follows the right-hand rule about
/// +Axis, exactly like the existing vortex term.
/// </para>
/// <para>
/// Tangential magnitude: Rankine profile with <see cref="Strength"/>.
/// Inward magnitude: Rankine profile with <see cref="InwardStrength"/> — same
/// linear-to-zero-at-the-axis core rule, same CoreRadius/d decay outside, so
/// the pull never exceeds <see cref="InwardStrength"/>.
/// Drain: <see cref="DrainStrength"/> * CoreRadius / (d + CoreRadius) along
/// −Axis — strongest at the axis (full <see cref="DrainStrength"/>), half at
/// the core edge, decaying outward. At the axis itself the particle feels
/// pure drain (rotation and inward pull are both zero there, by the core rule).
/// </para>
/// </summary>
public sealed class ParticleWhirlpoolVortex
{
	public Vector3 Center;
	/// <summary>Rotation axis; default +Z, so the rotation plane is horizontal and the drain is −Z (down).</summary>
	public Vector3 Axis = new( 0, 0, 1 );
	/// <summary>Peak tangential acceleration (in/s^2), attained at <see cref="CoreRadius"/>.</summary>
	public float Strength;
	/// <summary>Peak inward acceleration (in/s^2) toward the axis, attained at <see cref="CoreRadius"/>.</summary>
	public float InwardStrength;
	/// <summary>Drain acceleration (in/s^2) at the axis, along −Axis.</summary>
	public float DrainStrength;
	/// <summary>Core radius in inches (&gt; 0). Inside it, rotation and inward pull ramp linearly to zero at the axis.</summary>
	public float CoreRadius = 1f;

	public void Validate()
	{
		ParticleVortexChecks.NeedFinite( Center, nameof( Center ) );
		ParticleVortexChecks.NeedFinite( Axis, nameof( Axis ) );
		if ( Axis.LengthSquared() < 1e-12f ) throw new ArgumentException( "Whirlpool axis must be non-zero." );
		ParticleVortexChecks.NeedNonNegative( Strength, nameof( Strength ) );
		ParticleVortexChecks.NeedNonNegative( InwardStrength, nameof( InwardStrength ) );
		ParticleVortexChecks.NeedNonNegative( DrainStrength, nameof( DrainStrength ) );
		ParticleVortexChecks.NeedPositive( CoreRadius, nameof( CoreRadius ) );
	}

	/// <summary>Pure function of position: tangential rotation + inward pull + axial drain.</summary>
	public Vector3 Acceleration( Vector3 position )
	{
		Vector3 axis = ParticleVortexChecks.SafeAxis( Axis );
		if ( axis == Vector3.Zero ) return Vector3.Zero;
		Vector3 r = position - Center;
		Vector3 rp = r - axis * Vector3.Dot( r, axis );
		float d = rp.Length();
		Vector3 acc = axis * (-DrainStrength * CoreRadius / (d + CoreRadius));
		if ( d > 0 )
		{
			Vector3 tangent = Vector3.Cross( axis, rp ) / d;
			float profile = ParticleVortexChecks.Rankine( d, CoreRadius );
			acc += tangent * (Strength * profile);
			acc += (-rp / d) * (InwardStrength * profile);
		}
		return acc;
	}
}

/// <summary>
/// Vortex ring: a toroidal vortex. <see cref="RingRadius"/> is the radius of
/// the ring's centre circle (in the plane through <see cref="Center"/>
/// perpendicular to <see cref="Axis"/>); <see cref="TubeRadius"/> is the radius
/// of the tube around that circle. Particles near the ring circulate around
/// its tube; particles far from the ring feel ~nothing.
/// <para>
/// Geometry: let a = (radial distance from the axis) − RingRadius (the
/// in-plane offset from the tube centre) and b = axial offset from the ring
/// plane, so s = sqrt(a² + b²) is the distance to the nearest point on the
/// ring filament. In the (radial, axial) cross-section the circulation
/// direction is (−b, a) / s — a 90° rotation of the tube-centre→particle
/// offset — so a particle radially outside the ring (a &gt; 0, b = 0) is pushed
/// along +Axis, and a particle above the ring plane (a = 0, b &gt; 0) is pushed
/// radially inward. Positive <see cref="Strength"/> uses that sense; the
/// opposite sense is a negative strength, which Validate() rejects loudly
/// instead of silently flipping the circulation.
/// </para>
/// <para>
/// Magnitude (the shared core rule, plus locality): inside the tube
/// (s &lt;= TubeRadius) it ramps linearly, Strength * s / TubeRadius — zero
/// exactly on the filament, peak Strength at the tube edge. Outside the tube
/// it falls as Strength * (TubeRadius / s)^3: at 2 tube radii it is already
/// 1/8 strength, at 5 tube radii 1/125, at 25 tube radii 1/15625. That cubic
/// falloff (not the 1/d Rankine tail) is what makes the ring LOCAL: a particle
/// far from the ring feels ~nothing, as tested.
/// </para>
/// <para>
/// Degenerate points return zero, never NaN: exactly on the filament (s = 0,
/// no circulation direction exists) and exactly on the ring axis (radial
/// distance 0, so no radial direction exists from which to build the
/// cross-section). The axis case is also ≥ RingRadius from the filament, so
/// the cubic falloff would already have made it small.
/// </para>
/// </summary>
public sealed class ParticleVortexRing
{
	public Vector3 Center;
	/// <summary>Ring normal; default +Z, so the ring lies in the horizontal XY plane.</summary>
	public Vector3 Axis = new( 0, 0, 1 );
	/// <summary>Radius of the ring's centre circle in inches; must be &gt; <see cref="TubeRadius"/>.</summary>
	public float RingRadius = 10f;
	/// <summary>Radius of the tube around the ring circle in inches (&gt; 0); the core radius of this variant.</summary>
	public float TubeRadius = 1f;
	/// <summary>Peak circulation acceleration (in/s^2), attained at the tube edge (s = TubeRadius).</summary>
	public float Strength;

	public void Validate()
	{
		ParticleVortexChecks.NeedFinite( Center, nameof( Center ) );
		ParticleVortexChecks.NeedFinite( Axis, nameof( Axis ) );
		if ( Axis.LengthSquared() < 1e-12f ) throw new ArgumentException( "Vortex-ring axis must be non-zero." );
		ParticleVortexChecks.NeedNonNegative( Strength, nameof( Strength ) );
		ParticleVortexChecks.NeedPositive( TubeRadius, nameof( TubeRadius ) );
		ParticleVortexChecks.NeedPositive( RingRadius, nameof( RingRadius ) );
		if ( RingRadius <= TubeRadius ) throw new ArgumentException( "Vortex-ring RingRadius must be greater than TubeRadius (otherwise the tube reaches the ring centre)." );
	}

	/// <summary>Pure function of position: circulation around the ring's tube, cubic falloff outside it.</summary>
	public Vector3 Acceleration( Vector3 position )
	{
		Vector3 axis = ParticleVortexChecks.SafeAxis( Axis );
		if ( axis == Vector3.Zero ) return Vector3.Zero;
		Vector3 r = position - Center;
		float axial = Vector3.Dot( r, axis );
		Vector3 rp = r - axis * axial;
		float radial = rp.Length();
		if ( radial <= 0 ) return Vector3.Zero; // On the ring axis: no radial direction, see doc.
		Vector3 eRadial = rp / radial;
		float a = radial - RingRadius; // In-plane offset from the tube centre.
		float b = axial; // Axial offset from the ring plane.
		float s = MathF.Sqrt( a * a + b * b );
		if ( s <= 0 ) return Vector3.Zero; // Exactly on the filament: no circulation direction.
		// Circulation direction in the cross-section: rotate (a, b) by 90°.
		Vector3 dir = (-b * eRadial + a * axis) / s;
		float magnitude = s <= TubeRadius
			? Strength * s / TubeRadius
			: Strength * MathF.Pow( TubeRadius / s, 3 );
		return dir * magnitude;
	}
}

// Exact integration lines for the coordinator sweep (NOT applied by this lane):
//   In ParticleSystem.cs, inside Integrate, after the existing vortex block
//   (the `if ( p.VortexStrength != 0 )` block, ~line 1089), a variant hookup
//   would add one line per active variant, e.g.:
//     acc += tornado.Acceleration( pos );      // if a tornado variant is configured
//     acc += whirlpool.Acceleration( pos );    // if a whirlpool variant is configured
//     acc += vortexRing.Acceleration( pos );   // if a vortex-ring variant is configured
//   Each variant instance would live on ParticleSystemSettings (with a
//   Validate() call from Settings.Validate()) and be copied into SimParams.
//   No such settings fields, SimParams fields, or Integrate lines were added
//   by lane P64 — the sweep owns them, because ParticleSystem.cs is lane 9's
//   file and this lane's boundary is new files only.
