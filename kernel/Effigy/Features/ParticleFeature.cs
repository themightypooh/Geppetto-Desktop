using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Cross-quad particle bodies (particles-rendering P1, P4, P5).
///
/// There is no particle system in the kernel, and no camera-facing quad path:
/// features run before any render, with no camera to face. So a particle is a
/// small crossed quad set (2 or 3 quads through one centre, so it reads from
/// any view), stored as one ordinary body — the same construction as a hair
/// card, just shorter. The renderer never culls backfaces
/// (ShadedRender.Rasterize keeps every triangle regardless of winding), so a
/// single winding per quad is visible from behind; no double winding needed.
///
/// Placement: faces of the emitter body are picked area-weighted, a point is
/// taken on the face, then pulled toward the emitter centroid by u = rng^(1/3)
/// so a convex emitter fills its volume uniformly instead of shelling.
/// Life is static, evaluated at build time: size, colour, emission and alpha
/// all lerp from their start value to their end value across life t in 0..1.
/// There is no animation timeline in the kernel, and none is claimed here.
///
/// Per-particle attributes ride the mesh, the way the rest of the kernel
/// already does: VertexColors (sRGB, w = alpha over life) and VertexEmission
/// (sRGB colour times emit, which ShadedRender gamma-expands, so emit &gt; 1
/// is intentionally hot, the same precedent as GlowFeature brightness to 16).
/// The soft particle post-pass reads the same channels. A mesh with
/// IsParticle set is what the renderer's soft pass draws instead of the
/// triangle pass when render particles=soft is given; without that flag the
/// body renders as ordinary lit/emissive mesh.
/// </summary>
public sealed class ParticleFeature : Feature
{
	public override string TypeName => "Particle";

	public readonly BodySelectionParam On = new( "On" );
	public readonly IntParam Count = new( "Count", 200, 0, 100000 );
	public readonly FloatParam Size = new( "Size", 0.5f, 0f, 1000f, "u" );
	/// <summary>-1 (default) means Size: no change over life unless set.</summary>
	public readonly FloatParam SizeEnd = new( "Size end", -1f, -1f, 1000f, "u" );
	public readonly StringParam Colour = new( "Colour", "#ffcc33" );
	/// <summary>Empty (default) means Colour: no change over life unless set.</summary>
	public readonly StringParam ColourEnd = new( "Colour end", "" );
	public readonly FloatParam Emit = new( "Emit", 0f, 0f, 16f );
	/// <summary>-1 (default) means Emit: no change over life unless set.</summary>
	public readonly FloatParam EmitEnd = new( "Emit end", -1f, -1f, 16f );
	public readonly FloatParam Alpha = new( "Alpha", 1f, 0f, 1f );
	/// <summary>-1 (default) means Alpha: no change over life unless set.</summary>
	public readonly FloatParam AlphaEnd = new( "Alpha end", -1f, -1f, 1f );
	public readonly IntParam Seed = new( "Seed", 12345, 0, 1000000 );
	public readonly IntParam Cross = new( "Cross", 3, 2, 3 );
	public readonly FloatParam RotJitter = new( "Rot jitter", 0f, 0f, 180f, "deg" );
	/// <summary>"random" (default), or "fixed:0.5", or a plain number for a fixed life.</summary>
	public readonly StringParam Life = new( "Life", "random" );
	/// <summary>Size multiplier curve over life, as s:multiplier pairs like Scatter's ScaleCurve. Empty = 1.</summary>
	public readonly StringParam SizeCurve = new( "Size curve", "" );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { On, Count, Size, SizeEnd, Colour, ColourEnd, Emit, EmitEnd, Alpha, AlphaEnd, Seed, Cross, RotJitter, Life, SizeCurve };

	protected override void Execute( FeatureContext ctx )
	{
		if ( On.BodyIds.Count == 0 )
			Fail( "Particle needs an emitter body.",
				"On is empty, so there is nothing to emit from.",
				"Name the body to emit from with on=Body" );

		var emitter = ctx.Bodies.FirstOrDefault( b => On.BodyIds.Contains( b.Id ) );

		if ( emitter?.Mesh is null )
			Fail( $"Particle's emitter '{string.Join( ",", On.BodyIds )}' is not a body that has run before it.",
				"A body above this feature must exist to emit from.",
				"Put the emitter body first", "Check the emitter name" );

		var mesh = emitter!.Mesh;

		if ( mesh.Faces.Count == 0 || mesh.VertexCount == 0 )
			Fail( $"Particle's emitter '{emitter.Name}' has no faces.",
				"There is no surface to sample particles from.",
				"Give the emitter body geometry first" );

		var count = Count.Clamped;

		if ( count == 0 )
		{
			// A count=0 burst is an empty particle body, not an error: the
			// render tests compare it against a real burst, so it must build.
			var empty = new PolyMesh { IsParticle = true };
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "Particle", empty ) );
			return;
		}

		var colour0 = ParseColour( Colour.Value, "colour" );
		var colour1 = string.IsNullOrWhiteSpace( ColourEnd.Value ) ? colour0 : ParseColour( ColourEnd.Value, "colourend" );
		var sizeEnd = SizeEnd.Clamped < 0f ? Size.Clamped : SizeEnd.Clamped;
		var emitEnd = EmitEnd.Clamped < 0f ? Emit.Clamped : EmitEnd.Clamped;
		var alphaEnd = AlphaEnd.Clamped < 0f ? Alpha.Clamped : AlphaEnd.Clamped;
		var curve = ParseCurve( SizeCurve.Value );
		var lifeMode = ParseLife( Life.Value );

		// Area-weighted face table for the emitter, plus its centroid.
		var areas = new List<(int Face, float Area, float Cum)>();
		var totalArea = 0f;
		var centroid = Vec3.Zero;
		foreach ( var p in mesh.Positions ) centroid += p;
		centroid = mesh.Positions.Count > 0 ? centroid * (1f / mesh.Positions.Count) : Vec3.Zero;

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			var a = 0f;
			for ( var k = 1; k + 1 < f.Indices.Length; k++ )
			{
				var p0 = mesh.Positions[f.Indices[0]];
				var p1 = mesh.Positions[f.Indices[k]];
				var p2 = mesh.Positions[f.Indices[k + 1]];
				a += Vec3.Cross( p1 - p0, p2 - p0 ).Length * 0.5f;
			}
			if ( a <= 0f ) continue;
			totalArea += a;
			areas.Add( (fi, a, totalArea) );
		}

		if ( areas.Count == 0 )
			Fail( $"Particle's emitter '{emitter.Name}' has no area.",
				"Every face is degenerate, so there is nowhere to sample from.",
				"Give the emitter body real faces first" );

		var rng = new Random( Seed.Clamped );
		var result = new PolyMesh { IsParticle = true };
		var colours = new List<Vec4>();
		var emissions = new List<Vec3>();
		var anyEmit = false;
		var skipped = 0;

		for ( var i = 0; i < count; i++ )
		{
			var t = lifeMode is { } fixedLife ? fixedLife : (float)rng.NextDouble();
			var size = Lerp( Size.Clamped, sizeEnd, t ) * EvalCurve( curve, t );

			if ( size < 1e-4f ) { skipped++; continue; }

			// Surface point, then pulled toward the centroid for volume fill.
			var pick = (float)rng.NextDouble() * totalArea;
			var face = areas[areas.Count - 1].Face;
			foreach ( var entry in areas )
				if ( pick <= entry.Cum ) { face = entry.Face; break; }

			var f = mesh.Faces[face];
			var tri = f.Indices.Length == 3 ? 0 : rng.Next( f.Indices.Length - 2 );
			var a0 = mesh.Positions[f.Indices[0]];
			var a1 = mesh.Positions[f.Indices[tri + 1]];
			var a2 = mesh.Positions[f.Indices[tri + 2]];
			var ru = MathF.Sqrt( (float)rng.NextDouble() );
			var rv = (float)rng.NextDouble();
			var surface = a0 + (a1 - a0) * (1f - ru) + (a2 - a0) * (ru * rv);
			var u = MathF.Pow( (float)rng.NextDouble(), 1f / 3f );
			var centre = centroid + (surface - centroid) * u;

			// Random orientation: a uniform random axis as the particle's
			// local Z, then an in-plane spin of up to +-RotJitter about it.
			var z = RandomUnit( rng );
			var helper = MathF.Abs( z.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
			var x = Vec3.Cross( helper, z ).Normal;
			var y = Vec3.Cross( z, x );
			if ( RotJitter.Clamped > 0f )
			{
				var spin = (float)(rng.NextDouble() * 2 - 1) * RotJitter.Clamped * MathF.PI / 180f;
				x = RotateAround( x, z, spin );
				y = RotateAround( y, z, spin );
			}

			var colour = Lerp( colour0, colour1, t );
			var emit = Lerp( Emit.Clamped, emitEnd, t );
			var alpha = Lerp( Alpha.Clamped, alphaEnd, t );
			if ( emit > 0f ) anyEmit = true;

			var half = size * 0.5f;
			var planes = Cross.Clamped == 2
				? new[] { (x, y), (x, z) }
				: new[] { (x, y), (x, z), (y, z) };

			foreach ( var (pa, pb) in planes )
			{
				var v0 = result.AddVertex( centre - pa * half - pb * half );
				var v1 = result.AddVertex( centre + pa * half - pb * half );
				var v2 = result.AddVertex( centre + pa * half + pb * half );
				var v3 = result.AddVertex( centre - pa * half + pb * half );
				result.AddFace( new[] { v0, v1, v2, v3 },
					new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );
				for ( var k = 0; k < 4; k++ )
				{
					colours.Add( new Vec4( colour.x, colour.y, colour.z, alpha ) );
					emissions.Add( colour * emit );
				}
			}
		}

		if ( result.Faces.Count == 0 )
			Fail( "Particle built no quads.",
				"Every particle came out degenerate, e.g. size and sizeend are both 0.",
				"Give Size a value above 0", "Check sizeend and the size curve" );

		result.VertexColors = colours.ToArray();
		if ( anyEmit ) result.VertexEmission = emissions.ToArray();

		if ( skipped > 0 )
			Warn( $"Particle skipped {skipped} of {count} particles with no size.",
				"Size at their life is 0, so there is no quad to draw.",
				"Raise sizeend", "Check the size curve" );

		var tris = 0;
		foreach ( var face in result.Faces ) tris += Math.Max( 0, face.Indices.Length - 2 );
		if ( tris > 200000 && Diagnostic is null )
			Warn( $"Particle emitted about {tris} triangles.",
				"Large bursts get heavy to edit, save and render.",
				"Lower Count", "Use cross=2 instead of cross=3" );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "Particle", result ) );
	}

	static Vec3 ParseColour( string text, string key )
	{
		var c = FurMaterial.ParseHex( text, new Vec3( -1, -1, -1 ) );
		if ( c.x < 0f )
			Fail( $"Particle {key}='{text}' is not a colour.",
				"Colours are #rrggbb, e.g. colour=#ffcc33.",
				"Write the colour as #rrggbb" );
		return c;
	}

	static float? ParseLife( string text )
	{
		var t = (text ?? "").Trim().ToLowerInvariant();
		if ( t is "" or "random" ) return null;
		if ( t.StartsWith( "fixed:", StringComparison.Ordinal ) ) t = t["fixed:".Length..];
		if ( float.TryParse( t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) )
			return Math.Clamp( v, 0f, 1f );
		Fail( $"Particle life='{text}' is not a life.",
			"Life is random, a number 0..1, or fixed:0.5.",
			"Use life=random or life=fixed:0.5" );
		return null;
	}

	static List<(float S, float Scale)> ParseCurve( string text )
	{
		var curve = new List<(float S, float Scale)>();
		if ( string.IsNullOrWhiteSpace( text ) ) return curve;
		foreach ( var piece in text.Split( ',' ) )
		{
			var kv = piece.Trim().Split( ':' );
			float s = 0f, scale = 1f;
			if ( kv.Length != 2
				|| !float.TryParse( kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out s )
				|| !float.TryParse( kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out scale ) )
				Fail( $"Particle sizecurve='{text}' is not a curve.",
					"Curves are s:multiplier pairs, e.g. sizecurve=0:0.4,0.5:1,1:0.3.",
					"Write the curve as s:multiplier pairs" );
			curve.Add( (s, scale) );
		}
		curve.Sort( (a, b) => a.S.CompareTo( b.S ) );
		return curve;
	}

	static float EvalCurve( List<(float S, float Scale)> curve, float t )
	{
		if ( curve.Count == 0 ) return 1f;
		if ( t <= curve[0].S ) return curve[0].Scale;
		for ( var i = 1; i < curve.Count; i++ )
			if ( t <= curve[i].S )
			{
				var (s0, v0) = curve[i - 1];
				var (s1, v1) = curve[i];
				return s1 <= s0 ? v1 : v0 + (v1 - v0) * (t - s0) / (s1 - s0);
			}
		return curve[^1].Scale;
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;

	static Vec3 Lerp( Vec3 a, Vec3 b, float t ) => new( Lerp( a.x, b.x, t ), Lerp( a.y, b.y, t ), Lerp( a.z, b.z, t ) );

	static Vec3 RandomUnit( Random rng )
	{
		var z = (float)(rng.NextDouble() * 2 - 1);
		var phi = (float)(rng.NextDouble() * 2 * Math.PI);
		var r = MathF.Sqrt( Math.Max( 0f, 1f - z * z ) );
		return new Vec3( r * MathF.Cos( phi ), r * MathF.Sin( phi ), z );
	}

	static Vec3 RotateAround( Vec3 v, Vec3 axis, float ang )
	{
		var c = MathF.Cos( ang );
		var s = MathF.Sin( ang );
		return v * c + Vec3.Cross( axis, v ) * s + axis * Vec3.Dot( axis, v ) * (1f - c);
	}
}
