using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// FROZEN FRAME: a particle preset is baked, frozen-frame geometry, NOT a live
/// particle simulation. There is no emitter, solver, per-particle life state,
/// or runtime billboarding in the kernel (see agent-research-particles-presets
/// FINDINGS.md §0/§6, parked items stay parked). What this feature builds is a
/// burst cloud of real geometry and cards, placed deterministically from a
/// seed, with age/velocity faked by position along a rise/parabola, size
/// falloff, and colour bands — one body per band, so a cloud can be re-run,
/// moved, or deleted band by band. Modelled on FoliageFeature: a choice list
/// of named presets, a seed, a count, and a size/height/spread, all proven as
/// a feature shape. Steam is a Smoke parameter set, not new code; Explosion
/// is a composite of Fire + Smoke + Sparks + debris, not a new mechanism.
/// Bundled PNG textures are NOT shipped here: cards carry UVs, so a soft
/// blob/streak PNG can be assigned through the existing material map slots.
/// </summary>
public sealed class ParticlePresetFeature : Feature
{
	public override string TypeName => "ParticlePreset";

	public override GeometryKind Accepts => GeometryKind.None;

	public static readonly string[] Presets =
	{
		"Fire", "Smoke", "Sparks", "Dust", "Spray", "Rain", "Snow",
		"Magic", "Explosion", "Steam", "Leaves", "Confetti",
	};

	public readonly ChoiceParam Preset = new( "Preset", Presets );
	public readonly IntParam Count = new( "Count", 100, 1, 100000 );
	public readonly IntParam Seed = new( "Seed", 12345, 0, 1000000 );
	public readonly FloatParam Size = new( "Size", 2f, 0.01f, 1000f, unit: "u" );
	public readonly FloatParam Height = new( "Height", 10f, 0.1f, 10000f, unit: "u" );
	public readonly FloatParam Spread = new( "Spread", 5f, 0.1f, 10000f, unit: "u" );
	/// <summary>Comma-separated #rrggbb list replacing band colours in order; a
	/// single colour replaces every band. Empty = the preset's default bands.</summary>
	public readonly StringParam Colour = new( "Colour", "" );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Preset, Count, Seed, Size, Height, Spread, Colour, Material };

	enum Shape { Card, Cone, Streak, Sphere, Box }
	enum Place { Rise, Volume, Disc, Orbit, Parabola }

	sealed class Band
	{
		public string Name;
		public float Fraction;
		public Shape Shape;
		public Place Place;
		public string[] Colours; // per-particle palette; first = default
		public string Emit; // hex or null
		public float EmitStrength = 1f;
		public float SizeMul = 1f;
		public float SizeAt0 = 1f, SizeAt1 = 1f;
		public float RadiusAt0 = 0.15f, RadiusAt1 = 1f;
		public float LengthMul = 1f; // streak length, in particle sizes
	}

	static Band B( string name, float frac, Shape shape, Place place, string[] colours,
		string emit = null, float emitStrength = 1f, float sizeMul = 1f,
		float sizeAt0 = 1f, float sizeAt1 = 1f, float radiusAt0 = 0.15f, float radiusAt1 = 1f,
		float lengthMul = 1f ) => new()
	{
		Name = name, Fraction = frac, Shape = shape, Place = place, Colours = colours,
		Emit = emit, EmitStrength = emitStrength, SizeMul = sizeMul,
		SizeAt0 = sizeAt0, SizeAt1 = sizeAt1, RadiusAt0 = radiusAt0, RadiusAt1 = radiusAt1,
		LengthMul = lengthMul,
	};

	static Band[] SpecFor( string preset ) => preset switch
	{
		"Fire" => new[]
		{
			B( "core", 0.35f, Shape.Cone, Place.Rise, new[] { "#ffd23f" }, "#ffd23f", 2f, sizeAt0: 0.6f, sizeAt1: 0.2f ),
			B( "mid", 0.40f, Shape.Cone, Place.Rise, new[] { "#ff7a1a" }, "#ff5500", 1f, sizeAt0: 1.2f, sizeAt1: 0.5f ),
			B( "outer", 0.25f, Shape.Cone, Place.Rise, new[] { "#c81e00" }, null, sizeAt0: 1.2f, sizeAt1: 0.3f ),
		},
		"Smoke" => new[]
		{
			B( "young", 0.33f, Shape.Card, Place.Rise, new[] { "#2a2a2a" }, null, sizeAt0: 0.5f, sizeAt1: 1.0f, radiusAt0: 0.3f, radiusAt1: 0.7f ),
			B( "mid", 0.34f, Shape.Card, Place.Rise, new[] { "#888888" }, null, sizeAt0: 1.0f, sizeAt1: 1.8f, radiusAt0: 0.5f, radiusAt1: 1.1f ),
			B( "old", 0.33f, Shape.Card, Place.Rise, new[] { "#dddddd" }, null, sizeAt0: 1.8f, sizeAt1: 2.5f, radiusAt0: 0.8f, radiusAt1: 1.4f ),
		},
		// Steam is a Smoke parameter set: whiter, smaller, taller per width, fewer, faster growth.
		"Steam" => new[]
		{
			B( "steam", 1f, Shape.Card, Place.Rise, new[] { "#f2f2f2" }, null, sizeAt0: 0.4f, sizeAt1: 3f, radiusAt0: 0.2f, radiusAt1: 0.5f ),
		},
		"Sparks" => new[]
		{
			B( "core", 0.6f, Shape.Streak, Place.Parabola, new[] { "#ffcc33" }, "#ffcc33", 2f, sizeAt0: 1f, sizeAt1: 0.2f, lengthMul: 3f ),
			B( "tail", 0.4f, Shape.Streak, Place.Parabola, new[] { "#ff5500" }, "#ff5500", 1f, sizeAt0: 0.7f, sizeAt1: 0.2f, lengthMul: 2f ),
		},
		"Dust" => new[]
		{
			B( "dust", 1f, Shape.Card, Place.Disc, new[] { "#b8a888" }, null, sizeAt0: 1f, sizeAt1: 1.8f ),
		},
		"Spray" => new[]
		{
			B( "crown", 0.5f, Shape.Sphere, Place.Rise, new[] { "#cfe8ff" }, null, sizeAt0: 0.6f, sizeAt1: 1.2f, radiusAt0: 0.2f, radiusAt1: 1f ),
			B( "droplets", 0.5f, Shape.Sphere, Place.Parabola, new[] { "#eaf6ff" }, null, sizeAt0: 1f, sizeAt1: 0.5f ),
		},
		"Rain" => new[]
		{
			B( "rain", 1f, Shape.Streak, Place.Volume, new[] { "#a8c0d8" }, null, sizeAt0: 1f, sizeAt1: 1f, lengthMul: 3f ),
		},
		"Snow" => new[]
		{
			B( "snow", 1f, Shape.Card, Place.Volume, new[] { "#ffffff" }, null, sizeAt0: 1f, sizeAt1: 1f ),
		},
		"Magic" => new[]
		{
			B( "motes", 0.85f, Shape.Sphere, Place.Orbit, new[] { "#00ddff" }, "#00ddff", 3f, sizeAt0: 1f, sizeAt1: 1f ),
			B( "halo", 0.15f, Shape.Card, Place.Orbit, new[] { "#00ddff" }, "#00ddff", 2f, sizeMul: 5f, sizeAt0: 1f, sizeAt1: 1f ),
		},
		"Leaves" => new[]
		{
			B( "green", 0.4f, Shape.Card, Place.Volume, new[] { "#3f7a2a" }, null, sizeAt0: 1f, sizeAt1: 1f ),
			B( "amber", 0.35f, Shape.Card, Place.Volume, new[] { "#c8871a" }, null, sizeAt0: 1f, sizeAt1: 1f ),
			B( "brown", 0.25f, Shape.Card, Place.Volume, new[] { "#6b4423" }, null, sizeAt0: 1f, sizeAt1: 1f ),
		},
		"Confetti" => new[]
		{
			// One band, four-colour palette picked per particle: the Variants
			// mechanism's job, done internally because Scatter's Variants are
			// body names, not colours, and no per-instance colour path exists.
			B( "confetti", 1f, Shape.Box, Place.Volume,
				new[] { "#d8232a", "#2456c8", "#f2c200", "#2a9d4f" }, null, sizeAt0: 1f, sizeAt1: 1f ),
		},
		"Explosion" => new[]
		{
			B( "fire", 0.30f, Shape.Cone, Place.Rise, new[] { "#ffd23f" }, "#ffd23f", 2f, sizeAt0: 0.6f, sizeAt1: 0.2f ),
			B( "smoke", 0.35f, Shape.Card, Place.Rise, new[] { "#555555" }, null, sizeAt0: 1.0f, sizeAt1: 2.5f, radiusAt0: 0.5f, radiusAt1: 1.4f ),
			B( "sparks", 0.25f, Shape.Streak, Place.Parabola, new[] { "#ffcc33" }, "#ffcc33", 2f, sizeAt0: 1f, sizeAt1: 0.2f, lengthMul: 3f ),
			B( "debris", 0.10f, Shape.Box, Place.Parabola, new[] { "#3a332e" }, null, sizeAt0: 1f, sizeAt1: 1f ),
		},
		_ => new[]
		{
			B( "particles", 1f, Shape.Card, Place.Volume, new[] { "#888888" }, null ),
		},
	};

	protected override void Execute( FeatureContext ctx )
	{
		Warning = "Frozen frame: baked geometry, not a live particle simulation";
		var bands = SpecFor( Preset.Value );
		var count = Count.Clamped;
		var rng = new Random( Seed.Clamped );
		var size = Size.Clamped;
		var height = Height.Clamped;
		var spread = Spread.Clamped;
		var baseName = string.IsNullOrWhiteSpace( Name ) ? Preset.Value : Name;

		// Colour override: comma list replaces band colours in order; one
		// colour replaces every band. Invalid hex entries are ignored, so a
		// typo never fails the whole preset.
		var overrides = ( Colour.Value ?? "" ).Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( overrides.Length == 1 )
			foreach ( var b in bands ) b.Colours = new[] { overrides[0] };
		else if ( overrides.Length > 1 )
			for ( var i = 0; i < Math.Min( overrides.Length, bands.Length ); i++ )
				bands[i].Colours = new[] { overrides[i] };

		// Allocate counts: exact total, at least one per band when possible.
		var alloc = new int[bands.Length];
		var assigned = 0;
		for ( var i = 0; i < bands.Length; i++ )
		{
			alloc[i] = (int)MathF.Round( count * bands[i].Fraction );
			assigned += alloc[i];
		}
		for ( var i = 0; assigned > count && i < bands.Length; i++ )
			if ( alloc[i] > 1 ) { alloc[i]--; assigned--; }
		for ( var i = 0; assigned < count; i = ( i + 1 ) % bands.Length ) { alloc[i]++; assigned++; }
		if ( count < bands.Length )
			for ( var i = count; i < bands.Length; i++ ) alloc[i] = 0;

		var addedBodies = 0;
		for ( var bi = 0; bi < bands.Length; bi++ )
		{
			if ( alloc[bi] <= 0 ) continue;
			var band = bands[bi];
			var mesh = new PolyMesh();
			var colours = new List<Vec4>();
			var emission = new List<Vec3>();
			var slot = Math.Clamp( Material.Clamped + bi, 0, 63 );
			for ( var p = 0; p < alloc[bi]; p++ )
			{
				var (pos, dir, t) = PlaceParticle( band, rng, height, spread );
				var s = size * band.SizeMul * Lerp( band.SizeAt0, band.SizeAt1, t )
					* Rand( rng, 0.7f, 1.3f );
				var hex = band.Colours[rng.Next( band.Colours.Length )];
				var col = ParseHex( hex, new Vec3( 0.5f, 0.5f, 0.5f ) );
				var emit = band.Emit is null ? Vec3.Zero : ParseHex( band.Emit, Vec3.Zero ) * band.EmitStrength;
				var before = mesh.VertexCount;
				switch ( band.Shape )
				{
					case Shape.Card: AddCard( mesh, pos, RandDir( rng ), s, slot ); break;
					case Shape.Cone: AddCone( mesh, pos, dir, s, slot ); break;
					case Shape.Streak: AddStreak( mesh, pos, dir, s * band.LengthMul, s * 0.18f, slot ); break;
					case Shape.Sphere: AddOcta( mesh, pos, s * 0.5f, slot ); break;
					case Shape.Box: AddOrientedBox( mesh, pos, RandDir( rng ), s, slot ); break;
				}
				for ( var v = before; v < mesh.VertexCount; v++ )
				{
					colours.Add( new Vec4( col.x, col.y, col.z, 1f ) );
					emission.Add( emit );
				}
			}
			mesh.VertexColors = colours.ToArray();
			mesh.VertexEmission = emission.ToArray();
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{baseName}_{band.Name}", mesh ) );
			addedBodies++;
		}

		if ( addedBodies == 0 )
			Fail( "ParticlePreset built nothing", "Count was below 1 after clamping.",
				"Raise Count", "Pick a preset with fewer bands" );
	}

	// Returns position, main direction (cone/streak axis), and age t in 0..1
	// (0 = born, 1 = dying) — the baked substitute for life/velocity.
	( Vec3, Vec3, float ) PlaceParticle( Band band, Random rng, float height, float spread )
	{
		var angle = Rand( rng, 0f, 360f ) * MathF.PI / 180f;
		var ca = MathF.Cos( angle ); var sa = MathF.Sin( angle );
		switch ( band.Place )
		{
			case Place.Rise:
			{
				var t = MathF.Pow( (float)rng.NextDouble(), 0.9f );
				var r = spread * Lerp( band.RadiusAt0, band.RadiusAt1, t ) * MathF.Sqrt( (float)rng.NextDouble() );
				return ( new Vec3( ca * r, sa * r, height * t ),
					( new Vec3( ca * 0.15f, sa * 0.15f, 1f ) ).Normal, t );
			}
			case Place.Volume:
			{
				var t = (float)rng.NextDouble();
				// Rain streaks tilt with wind; everything else falls/drifs upright.
				var tilt = Preset.Value == "Rain" ? new Vec3( 0.25f, 0f, -1f ).Normal : new Vec3( 0, 0, -1 );
				return ( new Vec3( Rand( rng, -spread, spread ), Rand( rng, -spread, spread ), height * t ),
					tilt, t );
			}
			case Place.Disc:
			{
				var t = (float)rng.NextDouble();
				var r = spread * MathF.Sqrt( (float)rng.NextDouble() );
				return ( new Vec3( ca * r, sa * r, height * 0.25f * t ), new Vec3( 0, 0, 1 ), t );
			}
			case Place.Orbit:
			{
				var t = (float)rng.NextDouble();
				var r = spread * ( 0.7f + 0.6f * (float)rng.NextDouble() );
				return ( new Vec3( ca * r, sa * r, height * 0.2f * t ),
					new Vec3( -sa, ca, 0 ), t );
			}
			case Place.Parabola:
			{
				var t = (float)rng.NextDouble();
				var horiz = spread * t;
				// Ballistic arc: rises to height/2 at mid-flight, back to ground.
				var z = height * 2f * t * ( 1f - t );
				var dir = new Vec3( ca, sa, ( 1f - 2f * t ) * 0.8f ).Normal;
				return ( new Vec3( ca * horiz, sa * horiz, MathF.Max( 0f, z ) ), dir, t );
			}
			default:
				return ( Vec3.Zero, new Vec3( 0, 0, 1 ), 0f );
		}
	}

	static void AddCard( PolyMesh m, Vec3 centre, Vec3 normal, float size, int slot )
	{
		Orthonormal( normal, out var x, out var y );
		var hx = x * size * 0.5f; var hy = y * size * 0.35f;
		var a = m.AddVertex( centre - hx - hy );
		var b = m.AddVertex( centre + hx - hy );
		var c = m.AddVertex( centre + hx + hy );
		var d = m.AddVertex( centre - hx + hy );
		m.AddFace( new[] { a, b, c, d }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, slot );
		m.AddFace( new[] { a, d, c, b }, new[] { new Vec2( 0, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ), new Vec2( 1, 0 ) }, slot );
	}

	static void AddCone( PolyMesh m, Vec3 centre, Vec3 dir, float size, int slot )
	{
		Orthonormal( dir, out var x, out var y );
		var baseC = centre - dir * size * 0.5f;
		var apex = centre + dir * size * 0.5f;
		var radius = size * 0.35f;
		var ring = new int[8];
		for ( var i = 0; i < 8; i++ )
		{
			var a = i / 8f * 2f * MathF.PI;
			ring[i] = m.AddVertex( baseC + ( x * MathF.Cos( a ) + y * MathF.Sin( a ) ) * radius );
		}
		var ap = m.AddVertex( apex );
		var bc = m.AddVertex( baseC );
		for ( var i = 0; i < 8; i++ )
		{
			var j = ( i + 1 ) % 8;
			m.AddFace( new[] { ring[i], ring[j], ap }, null, slot );
			m.AddFace( new[] { ring[j], ring[i], bc }, null, slot );
		}
	}

	static void AddStreak( PolyMesh m, Vec3 centre, Vec3 dir, float length, float thick, int slot )
	{
		Orthonormal( dir, out var x, out var y );
		var h = dir * length * 0.5f; var hx = x * thick * 0.5f; var hy = y * thick * 0.5f;
		var c = new Vec3[8];
		c[0] = centre - h - hx - hy; c[1] = centre - h + hx - hy;
		c[2] = centre - h + hx + hy; c[3] = centre - h - hx + hy;
		c[4] = centre + h - hx - hy; c[5] = centre + h + hx - hy;
		c[6] = centre + h + hx + hy; c[7] = centre + h - hx + hy;
		var idx = new int[8];
		for ( var i = 0; i < 8; i++ ) idx[i] = m.AddVertex( c[i] );
		var faces = new[] { new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 }, new[] { 0, 4, 5, 1 },
			new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 } };
		foreach ( var f in faces )
			m.AddFace( new[] { idx[f[0]], idx[f[1]], idx[f[2]], idx[f[3]] }, null, slot );
	}

	static void AddOrientedBox( PolyMesh m, Vec3 centre, Vec3 dir, float size, int slot )
		=> AddStreak( m, centre, dir, size, size, slot );

	static void AddOcta( PolyMesh m, Vec3 centre, float r, int slot )
	{
		var t = m.AddVertex( centre + new Vec3( 0, 0, r ) );
		var b = m.AddVertex( centre - new Vec3( 0, 0, r ) );
		var xp = m.AddVertex( centre + new Vec3( r, 0, 0 ) );
		var xn = m.AddVertex( centre - new Vec3( r, 0, 0 ) );
		var yp = m.AddVertex( centre + new Vec3( 0, r, 0 ) );
		var yn = m.AddVertex( centre - new Vec3( 0, r, 0 ) );
		var tris = new[] { new[] { t, xp, yp }, new[] { t, yp, xn }, new[] { t, xn, yn }, new[] { t, yn, xp },
			new[] { b, yp, xp }, new[] { b, xn, yp }, new[] { b, yn, xn }, new[] { b, xp, yn } };
		foreach ( var f in tris ) m.AddFace( f, null, slot );
	}

	static void Orthonormal( Vec3 d, out Vec3 x, out Vec3 y )
	{
		d = d.Normal;
		var helper = MathF.Abs( d.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		x = Vec3.Cross( d, helper ).Normal;
		y = Vec3.Cross( d, x ).Normal;
	}

	static Vec3 RandDir( Random rng )
	{
		var z = Rand( rng, -1f, 1f );
		var a = Rand( rng, 0f, 360f ) * MathF.PI / 180f;
		var s = MathF.Sqrt( MathF.Max( 0f, 1f - z * z ) );
		return new Vec3( s * MathF.Cos( a ), s * MathF.Sin( a ), z );
	}

	static float Rand( Random rng, float a, float b ) => a + (float)rng.NextDouble() * ( b - a );
	static float Lerp( float a, float b, float t ) => a + ( b - a ) * t;

	static Vec3 ParseHex( string hex, Vec3 fallback )
	{
		try
		{
			var h = hex.Trim().TrimStart( '#' );
			if ( h.Length != 6 ) return fallback;
			var r = int.Parse( h[..2], NumberStyles.HexNumber );
			var g = int.Parse( h[2..4], NumberStyles.HexNumber );
			var b = int.Parse( h[4..6], NumberStyles.HexNumber );
			return new Vec3( r / 255f, g / 255f, b / 255f );
		}
		catch { return fallback; }
	}
}
