#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// P54 (particles session, 2026-10-24): medium preset pack D.
//
// BUNDLES, NOT PRESETS. This file adds four medium bundles in P38's exact
// bundle shape (the SAME MediumBundle struct from ParticleMedia.cs, never a
// second bundle type) and nothing else: no presets, no emitters, no 2D media,
// no turbulence/vortex fields, no per-particle density correction (P66 owns
// that), no save/serialization, no UI. A preset names a bundle; a bundle is
// not a preset. Do not feed bundle names through
// ParticleEmitterPresets.ResolveName: that resolver matches substrings, so
// "smoke_filled_room" would resolve to the "smoke" preset. Bundle lookup is
// ByName below, exact-match only.
//
// THE SAME RULE AS P38 (enforced by the SAME ApplyTo, proven by test):
// MediumBundle.ApplyTo(settings, bundle) DISCARDS scene gravity entirely and
// replaces it with the bundle's net gravity (gravity + buoyancy), and sets
// Drag to DragLinear. It never adds. A scene gravity of -1000 plus the honey
// bundle gives -386.0886, not -1386.0886.
//
// UNITS (same as ParticleSystem.cs and P38): kernel inches, seconds, Z-up.
//   Gravity in/s^2, DragLinear 1/s, DragQuadratic 1/in, Buoyancy in/s^2 +Z,
//   Density kg/m^3. DragQuadratic and Density have no ParticleSystemSettings
//   field and are deliberately NOT folded into any other field by ApplyTo;
//   consumers that model quadratic drag read the bundle directly (as P38's
//   dust test does, and as the honey tests here do).
//
// THE FOUR BUNDLES (full field table; net = gravity.Z + buoyancy):
//   vacuum           gravity (0,0,0)        dragLin 0     dragQuad 0      buoy 0   dens 1e-9
//   helium           gravity (0,0,-386.0886) dragLin 0.02  dragQuad 0.0002 buoy 410  dens 0.1664
//   honey            gravity (0,0,-386.0886) dragLin 200   dragQuad 5.0   buoy 0   dens 1420
//   smoke_filled_room gravity (0,0,-386.0886) dragLin 0.5  dragQuad 0.005  buoy 0   dens 1.25
//   Nets: vacuum 0, helium +23.9114, honey -386.0886, smoke-filled room -386.0886.
//
// VACUUM vs SPACE (P38), stated honestly because the two look alike:
//   Space is a medium with residual stuff in it: micro-gravity -0.01 in/s^2,
//   trace drag 0.001 1/s, density 1e-6 kg/m^3 — an orbital environment, not
//   nothing. Vacuum is the idealized limit: exactly zero gravity, exactly
//   zero drags, density 1e-9 (three orders below space's residual). The 1e-9
//   is nonzero ONLY because P38's validator forbids density 0; it is the
//   smallest honest placeholder, not a measurement. The bundles differ in
//   gravity, dragLinear and density (buoyancy and dragQuadratic are both 0),
//   so they are never field-identical. A particle in vacuum travels exactly
//   straight; a particle in space sags 0.005 in over 1 s at rest.
//
// HELIUM, physical reasoning: helium at STP is 0.1664 kg/m^3, about 1/7 of
//   air's 1.2, so a light reference particle (a balloon-ish envelope, the
//   analogue of P38's bubble-in-water reference) is lifted. Buoyancy is the
//   reference-particle lift in P38's sense, not a value derived from density
//   (density is carried as data; per-particle density lift is P66's lane).
//   Buoyancy 410 in/s^2 gives net +23.9114 in/s^2 (~0.61 m/s^2 up): positive
//   like water's +63.9114 but deliberately weaker — a gentle balloon rise,
//   not a bubble rocket. Drags sit below air's (0.02/0.0002 vs 0.05/0.0005):
//   drag scales with medium density, but helium's viscosity is close to
//   air's, so the reduction is conservative (40% of air's), not 1/7.
//
// HONEY, terminal-velocity consequence: drags are 100x water's (200 vs 2.0
//   linear, 5.0 vs 0.05 quadratic) for a medium ~10,000x water's viscosity
//   in round terms, capped at values the semi-implicit integrator handles
//   exactly (P38: stable for any drag >= 0). Linear + quadratic terminal
//   speed solves 5v^2 + 200v - 386.0886 = 0, giving v ~= 1.845 in/s; the
//   linear-only path the real 3D sim integrates gives 386.0886/200 =
//   1.9304 in/s. Either way a dropped particle crawls ~1.9 in in its first
//   second, against ~190 in in air. Buoyancy is 0, the low end of the brief:
//   honey's story is drag, not lift, and a bubble-in-honey lift number
//   would be invented (see the density limitation below). Density 1420
//   kg/m^3 is real honey (~1.42 g/cm^3).
//
// SMOKE-FILLED ROOM: air-like gravity and near-air density (1.25 kg/m^3,
//   air plus suspended soot, nominal not measured), but drags 10x air's
//   (0.5 vs 0.05 linear, 0.005 vs 0.0005 quadratic). The room's "thickness"
//   is expressed through drags ONLY. Turbulence itself lives on settings
//   (ParticleSystemSettings.TurbulenceStrength/Frequency/Speed), never in
//   the bundle: a smoke preset pairs this bundle with turbulence settings
//   separately, exactly as P38's bubbles case pairs water with turbulence.
//   No turbulence value is smuggled into any bundle field here.
//
// LIMITS, inherited from P38 and stated again so they are not re-litigated:
//   - ApplyTo carries only Gravity (net) and DragLinear into settings.
//   - Density is data, not a force: no consumer divides by it yet, so a
//     heavy grain in helium does NOT sink by density here — its scenario
//     uses the bundle's raw gravity + drags, and the bundle's Buoyancy
//     stays the light-reference lift (same caveat P38 states for water).
//   - Headless only. No UI, no script verb, no save format. The sweep owns
//     the TestRegistry line; hookup lines are reported, not made.
public static class ParticleMediaPackD
{
	/// <summary>Bundle names, lowercase underscore, in pack order. No name collides with P38's documented four (air, water, space, zero_g) or with any preset name (asserted in test).</summary>
	public static readonly string[] Names = { "vacuum", "helium", "honey", "smoke_filled_room" };

	/// <summary>Idealized perfect vacuum: exactly zero gravity and drag. Differs from P38 Space (micro-gravity + trace drag) in gravity, dragLinear and density.</summary>
	public static MediumBundle Vacuum => new( new Vector3( 0, 0, 0f ), 0f, 0f, 0f, 1e-9f );

	/// <summary>Lifting gas: Earth gravity, light-reference lift 410 in/s^2, net +23.9114 in/s^2 — positive but weaker than water's +63.9114. Low drags, helium density 0.1664 kg/m^3.</summary>
	public static MediumBundle Helium => new( new Vector3( 0, 0, -386.0886f ), 0.02f, 0.0002f, 410f, 0.1664f );

	/// <summary>Very high drags (100x water's), normal gravity, no reference lift. Things crawl: terminal ~1.845 in/s (linear + quadratic) / 1.9304 in/s (linear only).</summary>
	public static MediumBundle Honey => new( new Vector3( 0, 0, -386.0886f ), 200f, 5.0f, 0f, 1420f );

	/// <summary>Air-like gravity and density, drags 10x air's. Thickness via drags only; turbulence lives on settings, not in this bundle.</summary>
	public static MediumBundle SmokeFilledRoom => new( new Vector3( 0, 0, -386.0886f ), 0.5f, 0.005f, 0f, 1.25f );

	/// <summary>Exact-match bundle lookup. Unknown names fail loudly; never a silent fallback, never preset resolution.</summary>
	public static MediumBundle ByName( string name ) => name switch
	{
		"vacuum" => Vacuum,
		"helium" => Helium,
		"honey" => Honey,
		"smoke_filled_room" => SmokeFilledRoom,
		_ => throw new ArgumentException( $"Unknown medium bundle '{name}'. Known bundles: vacuum, helium, honey, smoke_filled_room." ),
	};
}
