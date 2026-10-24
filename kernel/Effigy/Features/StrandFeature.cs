using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// A deterministic strand settler: the kernel port of the desktop HairStrandService's pure
/// solver, in Effigy's own coordinates (Z up, so gravity pulls -Z).
///
/// The desktop service is deliberately NOT deleted or rewritten: LiveGeometry's StrandLayer
/// still uses it, and its point tuples are a viewer-side type. This solver is the same idea in
/// kernel types — straight strands, gravity + wind displacement per step, rest-length projection
/// from the pinned root outward, sphere-collider push-out — with no randomness at all, so the
/// same roots settle to the same strands on every rebuild. Randomness only enters through
/// <see cref="Scatter"/>, which takes an explicit seed.
/// </summary>
public static class StrandSolver
{
	public sealed class Options
	{
		public float Length = 3f;
		public int Segments = 8;
		public int Steps = 30;
		public float Gravity = -9.8f;
		public Vec3 Wind = Vec3.Zero;
		public Vec3 Comb = Vec3.Zero;
	}

	/// <summary>
	/// Settle one polyline per root. The first point of every strand is its root and never
	/// moves. Directions are the initial strand direction (unit-length is not required).
	/// </summary>
	public static List<List<Vec3>> Settle( IReadOnlyList<Vec3> roots, IReadOnlyList<Vec3> directions,
		Options o, IReadOnlyList<(Vec3 Centre, float Radius)> colliders = null )
	{
		if ( roots is null || roots.Count == 0 )
			throw new ArgumentException( "Strands need at least one root.", nameof( roots ) );

		if ( directions is null || directions.Count != roots.Count )
			throw new ArgumentException( "One direction per root is required.", nameof( directions ) );

		var segments = Math.Max( 1, o.Segments );
		var length = Math.Max( 1e-4f, o.Length );
		var rest = length / segments;
		var dt = 0.1f;
		var accel = new Vec3( o.Wind.x, o.Wind.y, o.Gravity + o.Wind.z );

		var strands = new List<List<Vec3>>( roots.Count );

		for ( var s = 0; s < roots.Count; s++ )
		{
			var dir = directions[s].LengthSquared > 1e-12f ? directions[s].Normal : new Vec3( 0, 0, -1 );
			var pts = new List<Vec3>( segments + 1 );

			for ( var i = 0; i <= segments; i++ )
				pts.Add( roots[s] + dir * (rest * i) );

			for ( var step = 0; step < Math.Max( 0, o.Steps ); step++ )
			{
				for ( var i = 1; i < pts.Count; i++ )
					pts[i] += accel * (dt * dt);

				Project( pts, rest );
				Collide( pts, colliders );
				Project( pts, rest );
			}

			// Combing is a shear that grows along the strand: the root stays, the tip moves
			// most. Applied after settling so it reads as a groomed direction, not as gravity.
			if ( o.Comb.LengthSquared > 1e-12f )
			{
				for ( var i = 1; i < pts.Count; i++ )
					pts[i] += o.Comb * ((float)i / segments * 0.5f);
			}

			Project( pts, rest );
			strands.Add( pts );
		}

		return strands;
	}

	static void Project( List<Vec3> pts, float rest )
	{
		for ( var pass = 0; pass < 2; pass++ )
		{
			for ( var i = 1; i < pts.Count; i++ )
			{
				var d = pts[i] - pts[i - 1];
				var len = d.Length;

				if ( len < 1e-9f )
					continue;

				pts[i] = pts[i - 1] + d * (rest / len);
			}
		}
	}

	static void Collide( List<Vec3> pts, IReadOnlyList<(Vec3 Centre, float Radius)> colliders )
	{
		if ( colliders is null || colliders.Count == 0 )
			return;

		for ( var i = 1; i < pts.Count; i++ )
		{
			foreach ( var (centre, radius) in colliders )
			{
				if ( radius <= 0f )
					continue;

				var d = pts[i] - centre;
				var len = d.Length;

				if ( len < radius )
					pts[i] = len < 1e-9f ? centre + new Vec3( 0, 0, radius ) : centre + d * (radius / len);
			}
		}
	}

	/// <summary>Tube geometry for settled strands: tapered, UV along length, open ends.</summary>
	public static PolyMesh BuildTube( IReadOnlyList<List<Vec3>> strands, float radius, float tipRatio, int sides, int material )
	{
		var mesh = new PolyMesh();
		sides = Math.Max( 3, sides );

		foreach ( var strand in strands )
		{
			if ( strand.Count < 2 )
				continue;

			var radii = new List<float>( strand.Count );

			for ( var i = 0; i < strand.Count; i++ )
				radii.Add( radius * (1f + (Math.Clamp( tipRatio, 0f, 1f ) - 1f) * i / (strand.Count - 1)) );

			var firstFace = mesh.FaceCount;
			Tendons.Tube( mesh.Positions, mesh.Faces, strand, radii, sides );

			// Tendons.Tube leaves zero UVs and material 0; give this strand length UVs and its slot.
			for ( var fi = firstFace; fi < mesh.FaceCount; fi++ )
			{
				var f = mesh.Faces[fi];
				var seg = (fi - firstFace) / sides;
				var k = (fi - firstFace) % sides;
				var u0 = (float)seg / (strand.Count - 1);
				var u1 = (float)(seg + 1) / (strand.Count - 1);
				var v0 = (float)k / sides;
				var v1 = (float)(k + 1) / sides;
				f.UVs = new[] { new Vec2( u0, v0 ), new Vec2( u0, v1 ), new Vec2( u1, v1 ), new Vec2( u1, v0 ) };
				f.Material = material;
			}
		}

		return mesh;
	}

	/// <summary>
	/// Card geometry for settled strands: twist-minimised ribbons (parallel-transported side
	/// frame), double-sided so a hair card reads from both sides, UV u along length, v across.
	/// </summary>
	public static PolyMesh BuildCards( IReadOnlyList<List<Vec3>> strands, float width, float tipRatio, int material )
	{
		var mesh = new PolyMesh();

		foreach ( var strand in strands )
		{
			if ( strand.Count < 2 )
				continue;

			// Parallel-transport the side frame along the strand so ribbons do not twist.
			var sides = new Vec3[strand.Count];

			for ( var i = 0; i < strand.Count; i++ )
			{
				var tangent = (strand[Math.Min( i + 1, strand.Count - 1 )] - strand[Math.Max( i - 1, 0 )]).Normal;

				if ( tangent.LengthSquared < 1e-12f )
					tangent = new Vec3( 0, 0, -1 );

				if ( i == 0 )
				{
					var helper = MathF.Abs( tangent.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
					sides[i] = Vec3.Cross( tangent, helper ).Normal;
				}
				else
				{
					var s = sides[i - 1] - tangent * Vec3.Dot( sides[i - 1], tangent );
					sides[i] = s.LengthSquared > 1e-12f ? s.Normal : sides[i - 1];
				}
			}

			var left = new int[strand.Count];
			var right = new int[strand.Count];

			for ( var i = 0; i < strand.Count; i++ )
			{
				var t = (float)i / (strand.Count - 1);
				var half = width * 0.5f * (1f + (Math.Clamp( tipRatio, 0f, 1f ) - 1f) * t);
				left[i] = mesh.AddVertex( strand[i] + sides[i] * half );
				right[i] = mesh.AddVertex( strand[i] - sides[i] * half );
			}

			for ( var i = 0; i + 1 < strand.Count; i++ )
			{
				var u0 = (float)i / (strand.Count - 1);
				var u1 = (float)(i + 1) / (strand.Count - 1);
				mesh.AddFace( new[] { left[i], right[i], right[i + 1], left[i + 1] },
					new[] { new Vec2( u0, 0 ), new Vec2( u0, 1 ), new Vec2( u1, 1 ), new Vec2( u1, 0 ) }, material );
				mesh.AddFace( new[] { left[i], left[i + 1], right[i + 1], right[i] },
					new[] { new Vec2( u0, 0 ), new Vec2( u1, 0 ), new Vec2( u1, 1 ), new Vec2( u0, 1 ) }, material );
			}
		}

		return mesh;
	}

	/// <summary>
	/// Deterministic area-weighted surface samples on a list of meshes: the same seed gives the
	/// same roots and normals on every run. A sample is a face centroid nudged out along the
	/// face normal, which is exactly on the surface for planar faces and close for curved ones.
	/// </summary>
	public static List<(Vec3 Point, Vec3 Normal)> Scatter( IReadOnlyList<PolyMesh> meshes, int count, int seed )
	{
		var rng = new Random( seed );
		var faces = new List<(PolyMesh Mesh, int Face, float Area)>();
		var total = 0f;

		foreach ( var mesh in meshes )
		{
			if ( mesh is null )
				continue;

			foreach ( var f in mesh.Faces )
			{
				var area = mesh.FaceArea( f );

				if ( area <= 0f )
					continue;

				faces.Add( (mesh, mesh.Faces.IndexOf( f ), area) );
				total += area;
			}
		}

		var result = new List<(Vec3, Vec3)>();

		if ( faces.Count == 0 || total <= 0f )
			return result;

		for ( var i = 0; i < count; i++ )
		{
			var pick = (float)rng.NextDouble() * total;
			var chosen = faces[^1];

			foreach ( var candidate in faces )
			{
				pick -= candidate.Area;

				if ( pick <= 0f )
				{
					chosen = candidate;
					break;
				}
			}

			var face = chosen.Mesh.Faces[chosen.Face];
			var centre = chosen.Mesh.FaceCentroid( face );
			var normal = chosen.Mesh.FaceNormal( face );
			result.Add( (centre + normal * 0.01f, normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 0, 0, 1 )) );
		}

		return result;
	}

	public static bool TryParseVec3( string text, out Vec3 value )
	{
		value = Vec3.Zero;

		if ( string.IsNullOrWhiteSpace( text ) )
			return false;

		var parts = text.Split( ',' ).Select( p => p.Trim() ).ToArray();

		if ( parts.Length != 3 )
			return false;

		if ( !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
			|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
			|| !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
			return false;

		value = new Vec3( x, y, z );
		return true;
	}
}

/// <summary>Script: <c>add Strands name=Hair roots=0,0,5;1,0,5 length=3 segments=8 radius=0.08
/// geometry=Tube steps=30 gravity=-9.8 comb=0,1,-0.2 material=0</c>. Hair, barbels, whiskers,
/// antennae, dreads and cables as settled geometry, not a live sim: the same roots settle to
/// the same strands on every rebuild. Roots may also be scattered: <c>scatter=200 bodies=Head
/// seed=42</c> samples the selected bodies' surfaces deterministically.</summary>
public sealed class StrandsFeature : Feature
{
	public override string TypeName => "Strands";

	public List<Vec3> Roots = new();
	public List<Vec3> Directions = new();

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly ChoiceParam Geometry = new( "Geometry", new[] { "Tube", "Card" } );
	public readonly FloatParam Length = new( "Length", 3f, 0.1f, 100f, unit: "u" );
	public readonly IntParam Segments = new( "Segments", 8, 1, 64 );
	public readonly FloatParam Radius = new( "Radius", 0.08f, 0.001f, 10f, unit: "u" );
	public readonly FloatParam TipRatio = new( "Tip ratio", 0.3f, 0f, 1f );
	public readonly FloatParam Width = new( "Card width", 0.3f, 0.01f, 10f, unit: "u" );
	public readonly IntParam Sides = new( "Sides", 6, 3, 16 );
	public readonly IntParam Steps = new( "Steps", 30, 0, 200 );
	public readonly FloatParam Gravity = new( "Gravity", -9.8f, -100f, 100f );
	public readonly StringParam Wind = new( "Wind", "0,0,0" );
	public readonly StringParam Comb = new( "Comb", "0,0,0" );
	public readonly IntParam Seed = new( "Seed", 42, 0, int.MaxValue ) { Slider = false };
	public readonly IntParam Scatter = new( "Scatter", 0, 0, 10000 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Geometry, Length, Segments, Radius, TipRatio, Width, Sides, Steps, Gravity, Wind, Comb, Seed, Scatter, Material };

	protected override void Execute( FeatureContext ctx )
	{
		if ( !StrandSolver.TryParseVec3( Wind.Value, out var wind ) )
			FailOn( Wind.Label, $"'{Wind.Value}' is not a vector x,y,z.", "Wind pushes every strand sideways.", "Write it like wind=0.5,0,0" );

		if ( !StrandSolver.TryParseVec3( Comb.Value, out var comb ) )
			FailOn( Comb.Label, $"'{Comb.Value}' is not a vector x,y,z.", "Comb shears the settled strands.", "Write it like comb=0,1,-0.2" );

		var roots = new List<Vec3>();
		var directions = new List<Vec3>();

		if ( Scatter.Clamped > 0 )
		{
			var targets = RequireBodies( ctx, Bodies );
			var meshes = targets.Where( b => b.Mesh is not null ).Select( b => b.Mesh ).ToList();
			var samples = StrandSolver.Scatter( meshes, Scatter.Clamped, Seed.Value );

			if ( samples.Count == 0 )
				Fail( "Scatter found no surface to root strands on.", "The selected bodies have no faces with area.", "Pick bodies with real surface, or give roots=... instead" );

			roots.AddRange( samples.Select( s => s.Point ) );
			directions.AddRange( samples.Select( s => s.Normal ) );
		}
		else
		{
			if ( Roots.Count == 0 )
				Fail( "Strands need roots to grow from.", "No roots were given and scatter is 0.", "Give roots=0,0,5;1,0,5, or scatter=200 bodies=Head" );

			roots.AddRange( Roots );

			if ( Directions.Count == 1 )
				directions.AddRange( Enumerable.Repeat( Directions[0], roots.Count ) );
			else if ( Directions.Count == roots.Count )
				directions.AddRange( Directions );
			else if ( Directions.Count == 0 )
				directions.AddRange( Enumerable.Repeat( new Vec3( 0, 0, -1 ), roots.Count ) );
			else
				Fail( "Directions must be one vector, or one per root.", $"Got {Directions.Count} directions for {roots.Count} roots.", "Give one direction for all strands, or one per root" );
		}

		// Sphere colliders are a deliberate approximation: one bounding sphere per upstream
		// body keeps strands out of the bulk of a head or torso without a BVH walk per point.
		// Exact mesh collision is future work; the solver API takes spheres so it stays honest.
		var colliders = new List<(Vec3, float)>();

		foreach ( var body in ctx.Bodies )
		{
			if ( body.Mesh is null || body.Mesh.VertexCount == 0 )
				continue;

			var centre = Vec3.Zero;

			foreach ( var p in body.Mesh.Positions )
				centre += p;

			centre /= body.Mesh.VertexCount;
			var radius = 0f;

			foreach ( var p in body.Mesh.Positions )
				radius = Math.Max( radius, (p - centre).Length );

			colliders.Add( (centre, radius + Radius.Clamped) );
		}

		var settled = StrandSolver.Settle( roots, directions,
			new StrandSolver.Options
			{
				Length = Length.Clamped,
				Segments = Segments.Clamped,
				Steps = Steps.Clamped,
				Gravity = Gravity.Value,
				Wind = wind,
				Comb = comb,
			}, colliders );

		var mesh = Geometry.Value == "Card"
			? StrandSolver.BuildCards( settled, Width.Clamped, TipRatio.Clamped, Material.Clamped )
			: StrandSolver.BuildTube( settled, Radius.Clamped, TipRatio.Clamped, Sides.Clamped, Material.Clamped );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}
}
