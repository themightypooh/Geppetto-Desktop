using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Fracture: shatter a body into Voronoi shards for a destructible prop.
///
///   add Fracture target=Crate count=12 mode=impactcluster impact=0,0,5 noise=0.05
///
/// Each shard is the target clipped to the Voronoi cell of one seed point — every pair of
/// seeds contributes a bisector plane, and the shard keeps the side nearer its own seed.
/// Cuts are capped with watertight interior faces on InteriorSlot (via
/// <see cref="MeshClip.TrimPlane"/>), with planar UVs projected from the cut plane, so a
/// shard is a closed solid, not an open shell. The target is CONSUMED by default: it leaves
/// the studio and Body_shard_0..N-1 take its place, which is what
/// VmdlPhysics.BreakPieceList reads as a ready-to-shatter prop; KeepOriginal keeps it
/// alongside for inspection.
///
/// Noise roughens only the cap-centre vertices — the fan centres TrimPlane invents, used
/// by no original face — so interior stone/wood grain never moves a boundary loop and
/// never opens a seam between shards. A seed whose cell misses the body entirely makes no
/// shard, and the feature warns rather than padding the count with empty bodies.
/// </summary>
public sealed class FractureFeature : Feature
{
	public override string TypeName => "Fracture";
	public override GeometryKind Accepts => GeometryKind.Body;

	/// <summary>Body to shatter, by name or id.</summary>
	public readonly StringParam Target = new( "Target", "" );

	public readonly IntParam Count = new( "Shards", 12, 1, 256 );

	/// <summary>Uniform | ImpactCluster | Radial | Splinter.</summary>
	public readonly ChoiceParam Mode = new( "Mode", new[] { "Uniform", "ImpactCluster", "Radial", "Splinter" } );

	/// <summary>Centre for ImpactCluster/Radial/Splinter modes.</summary>
	public readonly Vec3Param ImpactPoint = new( "Impact point", Vec3.Zero );

	/// <summary>Roughness of interior faces, in model units. 0 = flat cut.</summary>
	public readonly FloatParam NoiseAmp = new( "Interior noise", 0.05f, 0f, unit: "u" );

	public readonly IntParam InteriorSlot = new( "Interior slot", 2, 0, 63 ) { Slider = false };

	/// <summary>Keep the unshattered body alongside the shards.</summary>
	public readonly BoolParam KeepOriginal = new( "Keep original", false );

	public readonly IntParam Seed = new( "Seed", 42, 0, int.MaxValue ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Target, Count, Mode, ImpactPoint, NoiseAmp, InteriorSlot, KeepOriginal, Seed };
	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { InteriorSlot, Seed };

	/// <summary>Seed points for a mode, inside the mesh bounds. Pure function of the inputs, for tests.</summary>
	internal static List<Vec3> Seeds( string mode, int count, Vec3 min, Vec3 max, Vec3 impact, int seed )
	{
		var rng = new Random( seed );
		var points = new List<Vec3>();
		var diag = (max - min).Length;
		Vec3 InBox() => new(
			(float)(min.x + rng.NextDouble() * (max.x - min.x)),
			(float)(min.y + rng.NextDouble() * (max.y - min.y)),
			(float)(min.z + rng.NextDouble() * (max.z - min.z)) );
		Vec3 Dir()
		{
			var v = new Vec3( (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1) );
			return v.LengthSquared < 1e-9f ? new Vec3( 1, 0, 0 ) : v.Normal;
		}
		for ( var i = 0; i < count; i++ )
		{
			switch ( mode.ToLowerInvariant() )
			{
				case "impactcluster":
					points.Add( rng.NextDouble() < 0.7
						? impact + Dir() * (float)(Math.Pow( rng.NextDouble(), 1.0 / 3.0 ) * diag * 0.25)
						: InBox() );
					break;
				case "radial":
				{
					// Shells out from the impact: near seeds are dense, far seeds sparse, so the
					// impact zone shatters fine and the far side breaks into big pieces.
					var shell = (float)(Math.Pow( (i + 1.0) / count, 0.7 ) * diag * 0.6);
					points.Add( impact + Dir() * shell );
					break;
				}
				case "splinter":
				{
					var axis = Dir();
					var along = (float)(rng.NextDouble() * 2 - 1) * diag * 0.5f;
					var jitter = (float)rng.NextDouble() * diag * 0.05f;
					points.Add( impact + axis * along + Dir() * jitter );
					break;
				}
				default:
					points.Add( InBox() );
					break;
			}
		}
		return points;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var body = ctx.Bodies.FirstOrDefault( b =>
			string.Equals( b.Name, Target.Value, StringComparison.OrdinalIgnoreCase ) || b.Id == Target.Value );
		if ( body is null )
			Fail( $"No body called '{Target.Value}' to fracture.", "Fracture shatters a body already in the studio.", "Add the body first, then add Fracture" );
		var mesh = body.Mesh;
		if ( mesh is null || mesh.FaceCount == 0 )
			Fail( $"'{body.Name}' has no faces to fracture.", "Fracture clips a surface.", "Point Target at a body with real surface" );

		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		var seeds = Seeds( Mode.Value, Count.Clamped, min, max, ImpactPoint.Value, Seed.Clamped );
		var noise = new Noise3( Seed.Clamped );
		var made = 0;

		for ( var i = 0; i < seeds.Count; i++ )
		{
			var shard = mesh.Clone();
			for ( var j = 0; j < seeds.Count; j++ )
			{
				if ( j == i ) continue;
				var n = (seeds[j] - seeds[i]).Normal;
				var mid = (seeds[i] + seeds[j]) * 0.5f;
				// Keep dot(p, n) <= dot(mid, n): the ClipPlane keeps Distance >= 0, so flip both.
				shard = MeshClip.TrimPlane( shard, new ClipPlane( -n, -Vec3.Dot( mid, n ) ), InteriorSlot.Clamped );
				if ( shard.FaceCount == 0 ) break;
			}
			if ( shard.FaceCount == 0 ) continue;

			// Planar UVs on interior faces, projected from each face's own normal, and a
			// deterministic roughening of cap-centre vertices only (see the class note).
			var usedByOriginal = new HashSet<int>();
			foreach ( var f in shard.Faces )
				if ( f.Material != InteriorSlot.Clamped )
					foreach ( var vi in f.Indices ) usedByOriginal.Add( vi );
			for ( var fi = 0; fi < shard.Faces.Count; fi++ )
			{
				var f = shard.Faces[fi];
				if ( f.Material != InteriorSlot.Clamped ) continue;
				var normal = shard.FaceNormal( f );
				if ( normal.LengthSquared < 1e-12f ) normal = new Vec3( 0, 0, 1 );
				normal = normal.Normal;
				var helper = MathF.Abs( normal.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
				var u = Vec3.Cross( normal, helper ).Normal;
				var v = Vec3.Cross( normal, u );
				var uvs = new Vec2[f.Count];
				for ( var k = 0; k < f.Count; k++ )
				{
					var p = shard.Positions[f.Indices[k]];
					uvs[k] = new Vec2( Vec3.Dot( p, u ), Vec3.Dot( p, v ) );
				}
				f.UVs = uvs;
			}
			if ( NoiseAmp.Clamped > 0f )
			{
				for ( var vi = 0; vi < shard.VertexCount; vi++ )
				{
					if ( usedByOriginal.Contains( vi ) ) continue;
					var usedByInterior = shard.Faces.Any( f => f.Material == InteriorSlot.Clamped && f.Indices.Contains( vi ) );
					if ( !usedByInterior ) continue;
					var p = shard.Positions[vi];
					var n = noise.Sample( p );
					shard.Positions[vi] = p + new Vec3( 0, 0, n * NoiseAmp.Clamped );
				}
			}

			ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{body.Name}_shard_{made}", shard ) );
			made++;
		}

		if ( made == 0 )
			Fail( $"No shard of '{body.Name}' survived the Voronoi clip.", "Every seed's cell missed the body.", "Move the seeds into the body: check Impact point, or use Uniform mode" );

		if ( !KeepOriginal.Value )
			ctx.Bodies.Remove( body );

		Warning = made < seeds.Count
			? $"{made} of {seeds.Count} shards built from '{body.Name}' (the rest missed the body)."
			: $"{made} shards built from '{body.Name}'.";
	}
}
