using System;
using System.Collections.Generic;
using System.Globalization;

namespace Effigy;

/// <summary>
/// Particles — game-ready export to s&box (research: agent-research-particles-export).
///
/// A data-only effect definition. Geppetto has no FX particle system and no
/// billboard renderer, and s&box has no particle asset format — its effects
/// are components created in code (ParticleEffect + ParticleSpriteRenderer,
/// hand-rolled emit + update, as in PodPlume/JunkFx/GunEffects). So this
/// feature does NOT simulate or draw anything: it defines an effect in
/// Geppetto units, validates it, and publishes a seeded, deterministic
/// spawn sample table that the exporter turns into s&box code + sprite.
///
/// NAMING: this is deliberately ParticleFx, not Particle. The rendering
/// work already owns `add Particle` (cross-quad particle bodies, on/count/
/// size/colour params) in this same assembly; a second ParticleFeature
/// class would not compile and a second TypeName "Particle" would make
/// `add Particle` ambiguous. If the two are ever merged, that is a
/// parent-level merge of the two param sets, not a rename done here.
///
/// Data-only follows FoliageFeature (Accepts => GeometryKind.None): no body
/// is produced, and the bodies list is untouched. Parameters are public
/// IParam fields, so StudioDocument persists them exactly like every other
/// feature and a save/load round-trip reproduces the same effect.
///
/// Sampling is a pure function of the parameters (SampleAt(i)): the same
/// seed always gives the same table, on first build and after a reload,
/// with no stored state to go stale. Shapes: point = one origin, velocity
/// in a cone about +Z; disc = spawn on a unit disc, velocity in a cone
/// about +Z; cone = origin, velocity in a cone about +Z with the given
/// spread; box = spawn anywhere in a [-1,1]^3 box, velocity along +Z.
/// Life and speed vary symmetrically by lifevar/speedvar, clamped so a
/// particle never gets a negative life or speed.
///
/// Validation (fail fast): life &gt; 0, size1 &gt;= 0, max &gt;= 1, burst &lt;= max,
/// sprite must be a known preset (softdisc, smoke, spark) or a .png path.
/// A continuous effect whose steady-state count (rate x life) exceeds max
/// is a WARNING here, not a failure: `particles check` is the gate that
/// fails it, and failing at add time would make the gate undemonstrable.
/// A .png sprite path is validated at export time, not here — a feature
/// has no script base directory to resolve a relative path against.
/// </summary>
public sealed class ParticleFxFeature : Feature
{
	public override string TypeName => "ParticleFx";

	public override GeometryKind Accepts => GeometryKind.None;

	public static readonly string[] Modes = { "burst", "continuous" };
	public static readonly string[] Shapes = { "point", "disc", "cone", "box" };
	public static readonly string[] SpritePresets = { "softdisc", "smoke", "spark" };

	public readonly ChoiceParam Mode = new( "Mode", Modes );
	public readonly ChoiceParam Shape = new( "Shape", Shapes, 2 );
	public readonly FloatParam Rate = new( "Rate", 0f, 0f, 100000f, unit: "per s" );
	public readonly IntParam Burst = new( "Burst", 40, 0, 100000 );
	public readonly IntParam Max = new( "Max", 256, 1, 1000000 );
	public readonly FloatParam Life = new( "Life", 0.8f, 0f, 3600f, unit: "s" );
	public readonly FloatParam LifeVar = new( "LifeVar", 0.3f, 0f, 3600f, unit: "s" );
	public readonly FloatParam Speed = new( "Speed", 120f, 0f, 100000f, unit: "u/s" );
	public readonly FloatParam SpeedVar = new( "SpeedVar", 40f, 0f, 100000f, unit: "u/s" );
	public readonly FloatParam Spread = new( "Spread", 25f, 0f, 180f, unit: "deg" );
	public readonly FloatParam Size0 = new( "Size0", 0.5f, 0f, 10000f, unit: "u" );
	public readonly FloatParam Size1 = new( "Size1", 0.15f, 0f, 10000f, unit: "u" );
	public readonly StringParam Color0 = new( "Color0", "#ffd27a" );
	public readonly StringParam Color1 = new( "Color1", "#ff4400" );
	public readonly FloatParam Alpha0 = new( "Alpha0", 1f, 0f, 1f );
	public readonly FloatParam Alpha1 = new( "Alpha1", 0f, 0f, 1f );
	public readonly FloatParam Gravity = new( "Gravity", -700f, -100000f, 100000f, unit: "u/s^2" );
	public readonly FloatParam Drag = new( "Drag", 1.6f, 0f, 100f, unit: "1/s" );
	public readonly StringParam Sprite = new( "Sprite", "softdisc" );
	public readonly BoolParam Additive = new( "Additive", true );
	public readonly BoolParam Lit = new( "Lit", false );
	public readonly IntParam Seed = new( "Seed", 11, 0, 1000000 );
	public readonly FloatParam Scale = new( "Scale", 1f, 0.0001f, 10000f );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Mode, Shape, Rate, Burst, Max, Life, LifeVar, Speed, SpeedVar, Spread,
			Size0, Size1, Color0, Color1, Alpha0, Alpha1, Gravity, Drag, Sprite, Additive, Lit, Seed, Scale };

	/// <summary>One seeded spawn sample: where a particle starts, how fast, how long it lives, its birth size.</summary>
	public readonly struct Sample
	{
		public readonly Vec3 Position;
		public readonly Vec3 Velocity;
		public readonly float Life;
		public readonly float Size;
		public Sample( Vec3 p, Vec3 v, float life, float size ) { Position = p; Velocity = v; Life = life; Size = size; }
		public override string ToString() =>
			$"pos=({Position.x:0.###},{Position.y:0.###},{Position.z:0.###}) " +
			$"vel=({Velocity.x:0.###},{Velocity.y:0.###},{Velocity.z:0.###}) life={Life:0.###} size={Size:0.###}";
	}

	/// <summary>How many particles the effect holds at once: burst count, or rate x life for a continuous effect.</summary>
	public int SampleCount => Mode.Value == "burst"
		? Math.Max( 1, Burst.Clamped )
		: Math.Clamp( (int)MathF.Ceiling( Rate.Clamped * Life.Clamped ), 1, Math.Max( 1, Max.Clamped ) );

	/// <summary>Steady-state count for a continuous effect, unclamped — the number `particles check` compares against Max.</summary>
	public float SteadyCount => Mode.Value == "continuous" ? Rate.Clamped * Life.Clamped : Burst.Clamped;

	public Sample FirstSample() => SampleAt( 0 );

	public Sample SampleAt( int index )
	{
		var rng = new Random( Seed.Clamped * 1000003 + index * 7919 );
		float Rand( float a, float b ) => a + (float)rng.NextDouble() * (b - a);
		var shape = Shape.Value;
		var pos = Vec3.Zero;
		if ( shape == "disc" )
		{
			var r = MathF.Sqrt( (float)rng.NextDouble() );
			var phi = Rand( 0f, MathF.PI * 2f );
			pos = new Vec3( r * MathF.Cos( phi ), r * MathF.Sin( phi ), 0f );
		}
		else if ( shape == "box" )
		{
			pos = new Vec3( Rand( -1f, 1f ), Rand( -1f, 1f ), Rand( -1f, 1f ) );
		}
		Vec3 dir;
		if ( shape == "box" )
		{
			dir = new Vec3( 0f, 0f, 1f );
		}
		else
		{
			var spreadRad = Spread.Clamped * MathF.PI / 180f;
			var cosMax = MathF.Cos( spreadRad );
			var z = Rand( cosMax, 1f );
			var phi = Rand( 0f, MathF.PI * 2f );
			var rr = MathF.Sqrt( Math.Max( 0f, 1f - z * z ) );
			dir = new Vec3( rr * MathF.Cos( phi ), rr * MathF.Sin( phi ), z ).Normal;
		}
		var speed = Math.Max( 0f, Speed.Clamped + Rand( -SpeedVar.Clamped, SpeedVar.Clamped ) );
		var life = Math.Max( 0.05f, Life.Clamped + Rand( -LifeVar.Clamped, LifeVar.Clamped ) );
		return new Sample( pos, dir * speed, life, Size0.Clamped );
	}

	public static bool IsPreset( string sprite ) =>
		Array.Exists( SpritePresets, p => string.Equals( p, sprite, StringComparison.OrdinalIgnoreCase ) );

	public static bool IsPngPath( string sprite ) =>
		!string.IsNullOrWhiteSpace( sprite ) && sprite.Trim().EndsWith( ".png", StringComparison.OrdinalIgnoreCase );

	/// <summary>Parse #rrggbb into 0..1 components. Accepts an optional leading '#'.</summary>
	public static bool TryParseColor( string text, out float r, out float g, out float b )
	{
		r = g = b = 0f;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var t = text.Trim();
		if ( t.StartsWith( "#" ) ) t = t[1..];
		if ( t.Length != 6 ) return false;
		if ( !int.TryParse( t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v ) ) return false;
		r = ((v >> 16) & 0xFF) / 255f;
		g = ((v >> 8) & 0xFF) / 255f;
		b = (v & 0xFF) / 255f;
		return true;
	}

	protected override void Execute( FeatureContext ctx )
	{
		if ( Life.Clamped <= 0f )
			Fail( "ParticleFx life must be greater than 0.", $"life={Life.Value}.", "Set life to a positive number of seconds, e.g. life=0.8." );
		if ( Size1.Clamped < 0f )
			Fail( "ParticleFx size1 must not be negative.", $"size1={Size1.Value}.", "Set size1=0 for particles that shrink to nothing, or a positive end size." );
		if ( Max.Clamped < 1 )
			Fail( "ParticleFx max must be at least 1.", $"max={Max.Value}.", "Set max to the particle cap, e.g. max=256." );
		if ( Mode.Value == "burst" && Burst.Clamped > Max.Clamped )
			Fail( $"ParticleFx burst ({Burst.Clamped}) exceeds max ({Max.Clamped}); a burst can never fully emit.",
				"Emit returns null once the cap is hit, so the effect silently thins out.",
				$"Raise max to at least {Burst.Clamped}, or lower burst to at most {Max.Clamped}." );
		if ( !TryParseColor( Color0.Value, out _, out _, out _ ) )
			Fail( $"ParticleFx color0 '{Color0.Value}' is not a #rrggbb colour.", "Colours are written as #rrggbb, e.g. #ffd27a.", "Set color0=#rrggbb." );
		if ( !TryParseColor( Color1.Value, out _, out _, out _ ) )
			Fail( $"ParticleFx color1 '{Color1.Value}' is not a #rrggbb colour.", "Colours are written as #rrggbb, e.g. #ff4400.", "Set color1=#rrggbb." );
		var sprite = (Sprite.Value ?? "").Trim();
		if ( !IsPreset( sprite ) && !IsPngPath( sprite ) )
			Fail( $"ParticleFx sprite '{sprite}' is not a preset and not a .png path.",
				"Presets are generated; anything else must name a PNG file, checked at export time.",
				$"Use sprite={string.Join( " or sprite=", SpritePresets )}, or sprite=path/to/sprite.png." );
		// Run the sampler once so a bad seed/shape combination fails here, not at export.
		_ = FirstSample();
		if ( Mode.Value == "continuous" && SteadyCount > Max.Clamped )
			Warn( $"ParticleFx steady-state count {SteadyCount:0.#} exceeds max {Max.Clamped}.",
				"Emit returns null once the cap is hit, so a continuous effect silently thins out.",
				$"Lower rate or life so rate x life <= max, or raise max. `particles check` fails this effect until then." );
	}

	/// <summary>One describe line: parameters plus the first seeded sample, so an effect is inspectable without a renderer.</summary>
	public string DescribeLine()
	{
		var name = Name ?? TypeName;
		var count = Mode.Value == "burst" ? $"burst {Burst.Clamped}" : $"rate {Rate.Clamped}/s, steady {SteadyCount:0.#}";
		return $"- {name}: ParticleFx mode {Mode.Value}, shape {Shape.Value}, {count}, max {Max.Clamped}, " +
			$"life {Life.Clamped:0.###}±{LifeVar.Clamped:0.###}, speed {Speed.Clamped:0.###}±{SpeedVar.Clamped:0.###}, " +
			$"spread {Spread.Clamped:0.#}deg, size {Size0.Clamped:0.###}->{Size1.Clamped:0.###}, " +
			$"color {Color0.Value}->{Color1.Value}, alpha {Alpha0.Clamped:0.##}->{Alpha1.Clamped:0.##}, " +
			$"gravity {Gravity.Clamped:0.#}, drag {Drag.Clamped:0.##}, sprite {Sprite.Value}, " +
			$"additive {(Additive.Value ? 1 : 0)}, lit {(Lit.Value ? 1 : 0)}, seed {Seed.Clamped}, scale {Scale.Clamped:0.###}, " +
			$"samples {SampleCount}, first sample {FirstSample()}.";
	}
}
