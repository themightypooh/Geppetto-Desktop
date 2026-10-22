using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Pull the selected bodies in onto another body's surface until they sit a set clearance off it:
/// a boot shaft hugging the trouser leg, a cuff closing on a wrist, a strap lying on a chest. The
/// move is measured from the wall nearest the surface, so a shell keeps its thickness, and the
/// height band confines it to the part that should hug (the shaft, not the foot).
/// </summary>
public sealed class ShrinkwrapFeature : Feature
{
	public override string TypeName => "Shrinkwrap";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly BodySelectionParam Onto = new( "Onto" );

	/// <summary>The gap left between the hugging wall and the surface under it.</summary>
	public readonly FloatParam Clearance = new( "Clearance", 0.05f, -1f, 4f, "in" );

	/// <summary>How much of the way to the clearance each point goes (1 = all the way).</summary>
	public readonly FloatParam Strength = new( "Strength", 1f, 0f, 1f );

	/// <summary>Points farther off the surface than this are left alone.</summary>
	public readonly FloatParam Reach = new( "Reach", 2f, 0.01f, 24f, "in" );

	/// <summary>How far to look for the inner wall of a shell, so the outer wall moves with it
	/// instead of collapsing onto it. About the shell's thickness, a little over.</summary>
	public readonly FloatParam WallThickness = new( "Wall thickness", 0.4f, 0f, 4f, "in" );

	/// <summary>Only move across the vertical axis, so seams and hems stay at their heights.</summary>
	public readonly BoolParam KeepHeight = new( "Keep height", true );

	/// <summary>Smoothing passes over the move, so the wrap reads as cloth, not as the surface's facets.</summary>
	public readonly IntParam Smooth = new( "Smooth", 6, 0, 40 );

	/// <summary>The height band that wraps, as fractions of each body's height.</summary>
	public readonly FloatParam Lower = new( "Lower limit", 0f, 0f, 1f );
	public readonly FloatParam Upper = new( "Upper limit", 1f, 0f, 1f );

	/// <summary>How far past the band's ends the wrap fades out.</summary>
	public readonly FloatParam Blend = new( "Blend", 1f, 0f, 24f, "in" );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Onto, Clearance, Strength, Reach, WallThickness, KeepHeight, Smooth, Lower, Upper, Blend };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { WallThickness, Smooth, Blend };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Onto.BodyIds.Count == 0 )
		{
			FailOn( Onto.Label,
				"Nothing to wrap onto",
				"Shrinkwrap needs the body the selected ones should hug, and none has been picked.",
				"Pick the body underneath, e.g. the trousers for a boot" );
		}

		if ( Upper.Clamped <= Lower.Clamped )
		{
			FailOn( "Upper limit", Math.Min( 1f, Lower.Clamped + 0.5f ),
				"The band is empty",
				$"The upper limit ({Upper.Clamped:0.###}) has to be above the lower one ({Lower.Clamped:0.###}).",
				"Raise Upper limit, or lower Lower limit" );
		}

		var moving = RequireBodies( ctx, Bodies ).Where( b => !Onto.Matches( b ) ).ToList();
		var under = ctx.Bodies.Where( b => Onto.Matches( b ) && b.Mesh is { FaceCount: > 0 } ).ToList();

		if ( under.Count == 0 )
		{
			FailOn( Onto.Label,
				"The body to wrap onto is gone",
				"The body chosen under Onto is no longer in the studio, or has no faces.",
				"Pick a body that still exists" );
		}

		if ( moving.Count == 0 )
		{
			FailOn( Bodies.Label,
				"Nothing to wrap",
				"Every selected body is also the one to wrap onto.",
				"Pick the bodies that should hug, apart from the one under them" );
		}

		var trees = under.Select( b => (b.Mesh, Tree: MeshBVH.Build( b.Mesh )) ).ToList();
		var moved = 0;

		foreach ( var body in moving )
			moved += Wrap( body.Mesh, trees );

		if ( moved == 0 )
		{
			Warn(
				"Nothing came within reach",
				$"No point of the selected bodies inside the band is within {Reach.Clamped:0.##} in of the surface.",
				"Raise Reach, or widen the band with Lower and Upper limit" );
		}
	}

	int Wrap( PolyMesh mesh, List<(PolyMesh Mesh, MeshBVH Tree)> trees )
	{
		var n = mesh.VertexCount;
		if ( n == 0 ) return 0;

		var (lo, hi) = ModelMeasure.Bounds( mesh );
		var height = hi.z - lo.z;
		var bandLo = lo.z + height * Lower.Clamped;
		var bandHi = lo.z + height * Upper.Clamped;
		var blend = Blend.Clamped;
		var reach = Reach.Clamped;
		var look = reach + WallThickness.Clamped + 1f;

		var weight = new float[n];
		var gap = new float[n];
		var outward = new Vec3[n];
		var found = new bool[n];

		for ( var i = 0; i < n; i++ )
		{
			var p = mesh.Positions[i];
			var w = Ramp( bandLo - blend, bandLo, p.z ) * (1f - Ramp( bandHi, bandHi + blend, p.z ));
			weight[i] = w;
			if ( w <= 0f ) continue;

			MeshHit? best = null;
			foreach ( var (m, tree) in trees )
			{
				var hit = tree.NearestSurface( m, p, best?.Distance ?? look );
				if ( hit is { } h && (best is null || h.Distance < best.Value.Distance) ) best = h;
			}

			if ( best is not { } b ) continue;

			// Signed by the surface's own facing: negative is inside it, and gets pushed back out.
			var off = p - b.Point;
			var inside = Vec3.Dot( off, b.Normal ) < 0f;
			var dir = off.Length > 1e-5f ? off.Normal * (inside ? -1f : 1f) : b.Normal;
			found[i] = true;
			gap[i] = inside ? -b.Distance : b.Distance;
			outward[i] = dir;
		}

		// The gap that matters is the inner wall's: each point moves by the smallest gap round it,
		// so a shell's outer wall travels with its inner one and keeps the thickness between them.
		var own = MeshBVH.Build( mesh );
		var near = new List<int>();
		var move = new Vec3[n];
		var wall = WallThickness.Clamped;

		for ( var i = 0; i < n; i++ )
		{
			if ( !found[i] ) continue;

			var g = gap[i];
			if ( wall > 0f )
			{
				own.VerticesInRadius( mesh, mesh.Positions[i], wall, near );
				foreach ( var j in near )
					if ( found[j] && gap[j] < g ) g = gap[j];
			}

			if ( g > reach ) continue;

			var m = outward[i] * (-(g - Clearance.Value) * Strength.Clamped * weight[i]);
			if ( KeepHeight.Value ) m = new Vec3( m.x, m.y, 0f );
			move[i] = m;
		}

		move = SmoothField( mesh, move, Smooth.Clamped );

		var count = 0;
		for ( var i = 0; i < n; i++ )
		{
			if ( move[i].LengthSquared < 1e-12f ) continue;
			mesh.Positions[i] += move[i];
			count++;
		}

		return count;
	}

	/// <summary>Averages each point's move with its edge neighbours', so the wrap follows the shape
	/// of the surface without copying its facets. Points that weren't moving pull their neighbours
	/// back a little, which feathers the edges of the band.</summary>
	static Vec3[] SmoothField( PolyMesh mesh, Vec3[] field, int passes )
	{
		if ( passes <= 0 ) return field;

		var n = mesh.VertexCount;
		var links = new HashSet<int>[n];
		for ( var i = 0; i < n; i++ ) links[i] = new HashSet<int>();

		foreach ( var f in mesh.Faces )
		{
			var idx = f.Indices;
			for ( var k = 0; k < idx.Length; k++ )
			{
				var a = idx[k];
				var b = idx[(k + 1) % idx.Length];
				links[a].Add( b );
				links[b].Add( a );
			}
		}

		var cur = field;
		for ( var pass = 0; pass < passes; pass++ )
		{
			var next = new Vec3[n];
			for ( var i = 0; i < n; i++ )
			{
				var sum = cur[i];
				foreach ( var j in links[i] ) sum += cur[j];
				next[i] = sum * (1f / (links[i].Count + 1));
			}
			cur = next;
		}

		return cur;
	}

	static float Ramp( float a, float b, float x )
	{
		if ( b <= a ) return x >= b ? 1f : 0f;
		var t = Math.Clamp( (x - a) / (b - a), 0f, 1f );
		return t * t * (3f - 2f * t);
	}
}
