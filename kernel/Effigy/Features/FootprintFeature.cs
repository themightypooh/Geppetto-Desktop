using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Fit the selected bodies' outline seen from above to another body's outline at one height: a
/// boot sole taking the shape of the boot's foot, a base plate following the thing standing on it.
/// The selected body's own outline (at <see cref="OwnAt"/> of its height) is pulled onto the other
/// body's outline (at <see cref="At"/>) plus the clearance, and every point inside follows the
/// nearest stretch of rim, so tread, bevels and thickness keep their shape. Moves are across only.
/// </summary>
public sealed class FootprintFeature : Feature
{
	public override string TypeName => "Fit footprint";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly BodySelectionParam Onto = new( "Onto" );

	/// <summary>The height (absolute z) at which the other body's outline is taken.</summary>
	public readonly FloatParam At = new( "At", 2f, -10000f, 10000f, "in" );

	/// <summary>Where on each selected body its own outline is taken, as a fraction of its height.</summary>
	public readonly FloatParam OwnAt = new( "Own outline at", 0.5f, 0f, 1f );

	/// <summary>How far the fitted rim stands out past the other body's outline (a welt), or in when negative.</summary>
	public readonly FloatParam Clearance = new( "Clearance", 0.1f, -2f, 4f, "in" );

	/// <summary>How much of the way to the outline each point goes (1 = all the way).</summary>
	public readonly FloatParam Strength = new( "Strength", 1f, 0f, 1f );

	/// <summary>How sharply inner points follow their nearest stretch of rim rather than the whole rim.</summary>
	public readonly FloatParam Falloff = new( "Falloff", 4f, 1f, 8f );

	/// <summary>How far along the rim the body's own outline is averaged before it is fitted, so it
	/// keeps its ribs and notches and only its overall outline changes. Keep it under the corner radius you want.</summary>
	public readonly FloatParam Smooth = new( "Smooth", 0.5f, 0f, 6f, "in" );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Onto, At, OwnAt, Clearance, Strength, Falloff, Smooth };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Falloff, Smooth };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Onto.BodyIds.Count == 0 )
		{
			FailOn( Onto.Label,
				"Nothing to fit to",
				"Fit footprint needs the body whose outline the selected ones should take, and none has been picked.",
				"Pick the body on top, e.g. the boot for a sole" );
		}

		var moving = RequireBodies( ctx, Bodies ).Where( b => !Onto.Matches( b ) ).ToList();
		var under = ctx.Bodies.Where( b => Onto.Matches( b ) && b.Mesh is { FaceCount: > 0 } ).ToList();

		if ( under.Count == 0 )
		{
			FailOn( Onto.Label,
				"The body to fit to is gone",
				"The body chosen under Onto is no longer in the studio, or has no faces.",
				"Pick a body that still exists" );
		}

		if ( moving.Count == 0 )
		{
			FailOn( Bodies.Label,
				"Nothing to fit",
				"Every selected body is also the one to fit to.",
				"Pick the bodies that should take the outline, apart from the one it comes from" );
		}

		var target = new List<(Vec3 A, Vec3 B)>();
		foreach ( var b in under )
			target.AddRange( MeshSection.CrossSection( b.Mesh, new Vec3( 0, 0, At.Value ), new Vec3( 0, 0, 1 ) ) );

		if ( target.Count == 0 )
		{
			var lo = under.Min( b => ModelMeasure.Bounds( b.Mesh ).Item1.z );
			var hi = under.Max( b => ModelMeasure.Bounds( b.Mesh ).Item2.z );
			FailOn( At.Label, (lo + hi) * 0.5f,
				"No outline at that height",
				$"The body to fit to doesn't reach z = {At.Value:0.##}; it spans z = {lo:0.##} to {hi:0.##}.",
				"Set At to a height inside it, where its outline is the shape you want" );
		}

		foreach ( var body in moving )
			Fit( body.Mesh, target );
	}

	void Fit( PolyMesh mesh, List<(Vec3 A, Vec3 B)> target )
	{
		var (lo, hi) = ModelMeasure.Bounds( mesh );
		var z = lo.z + (hi.z - lo.z) * OwnAt.Clamped;
		var own = MeshSection.CrossSection( mesh, new Vec3( 0, 0, z ), new Vec3( 0, 0, 1 ) );

		if ( own.Count == 0 )
		{
			FailOn( OwnAt.Label, 0.5f,
				"The body has no outline there",
				$"Cutting the selected body at {OwnAt.Clamped:0.##} of its height (z = {z:0.##}) met nothing.",
				"Move Own outline at to where the body is solid all round" );
		}

		// Rim samples: every segment's midpoint, with the move that puts it on the target outline.
		var rim = own.Select( s => Flat( (s.A + s.B) * 0.5f ) ).ToArray();
		var len = own.Select( s => MathF.Max( (Flat( s.B ) - Flat( s.A )).Length, 1e-4f ) ).ToArray();
		// The move is measured from the body's outline with its own ribs and notches averaged out,
		// so every sample in a stretch moves alike and that detail rides along unchanged.
		var sigma = Smooth.Clamped;
		var envelope = SmoothAlong( rim, len, rim, sigma );
		var moves = new Vec3[rim.Length];
		for ( var i = 0; i < rim.Length; i++ )
		{
			var p = envelope[i];
			var q = Nearest( target, p );
			var off = p - q;
			var dist = off.Length;
			var inside = Inside( target, p );
			var gap = inside ? -dist : dist;
			var dir = dist > 1e-5f ? off * (1f / dist) * (inside ? -1f : 1f) : Vec3.Zero;
			moves[i] = dir * (-(gap - Clearance.Value) * Strength.Clamped);
		}

		moves = SmoothAlong( rim, len, moves, sigma * 0.3f );

		// Every point follows the rim near it (inverse distance), so the inside keeps its layout.
		var power = Falloff.Clamped;
		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var p = mesh.Positions[v];
			var flat = Flat( p );
			var sum = Vec3.Zero;
			var wsum = 0f;
			var exact = -1;

			for ( var i = 0; i < rim.Length; i++ )
			{
				var d = (rim[i] - flat).Length;
				if ( d < 1e-4f ) { exact = i; break; }
				var w = len[i] / MathF.Pow( d, power );
				sum += moves[i] * w;
				wsum += w;
			}

			var m = exact >= 0 ? moves[exact] : sum * (1f / wsum);
			mesh.Positions[v] = new Vec3( p.x + m.x, p.y + m.y, p.z );
		}
	}

	static Vec3 Flat( Vec3 p ) => new( p.x, p.y, 0f );

	static Vec3 Nearest( List<(Vec3 A, Vec3 B)> segments, Vec3 p )
	{
		var best = Vec3.Zero;
		var bestD = float.MaxValue;
		foreach ( var (a3, b3) in segments )
		{
			var a = Flat( a3 );
			var ab = Flat( b3 ) - a;
			var len2 = Vec3.Dot( ab, ab );
			var t = len2 > 1e-12f ? Math.Clamp( Vec3.Dot( p - a, ab ) / len2, 0f, 1f ) : 0f;
			var q = a + ab * t;
			var d = (p - q).LengthSquared;
			if ( d < bestD ) { bestD = d; best = q; }
		}
		return best;
	}

	/// <summary>Even-odd test along +x: inside the outline, holes excepted.</summary>
	static bool Inside( List<(Vec3 A, Vec3 B)> segments, Vec3 p )
	{
		var inside = false;
		foreach ( var (a, b) in segments )
		{
			if ( (a.y > p.y) == (b.y > p.y) ) continue;
			var x = a.x + (p.y - a.y) / (b.y - a.y) * (b.x - a.x);
			if ( x > p.x ) inside = !inside;
		}
		return inside;
	}

	/// <summary>Averages each rim sample's move with the samples round it, weighted by segment
	/// length and a Gaussian of width <paramref name="sigma"/>, so dense patches don't count extra.</summary>
	static Vec3[] SmoothAlong( Vec3[] rim, float[] len, Vec3[] moves, float sigma )
	{
		if ( sigma <= 0f ) return moves;

		var k = -1f / (2f * sigma * sigma);
		var cut = 9f * sigma * sigma;
		var result = new Vec3[rim.Length];
		for ( var i = 0; i < rim.Length; i++ )
		{
			var sum = Vec3.Zero;
			var wsum = 0f;
			for ( var j = 0; j < rim.Length; j++ )
			{
				var d2 = (rim[j] - rim[i]).LengthSquared;
				if ( d2 > cut ) continue;
				var w = len[j] * MathF.Exp( d2 * k );
				sum += moves[j] * w;
				wsum += w;
			}
			result[i] = sum * (1f / wsum);
		}
		return result;
	}
}
