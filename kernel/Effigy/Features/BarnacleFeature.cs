using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Barnacle clusters on a body — the roadmap's <c>SdfCellularGrowth</c> role for the
/// Rusty Harbor Crane &amp; Buoy (§4), implemented as growth on the surface rather than
/// as a volumetric SDF, because a buoy's barnacles are a surface encrustation, not a
/// blob in a voxel grid.
///
///   add Barnacle target=Buoy count=40 size=0.5 height=0.6 cluster=0.7 seed=7
///
/// Growth is differential-growth-style, not a simulation of it: cluster centres are
/// seeded on the surface, then each new barnacle must land near an already-placed one
/// (within a radius that tightens as <see cref="Cluster"/> rises), so colonies clump
/// instead of peppering evenly. Cluster = 0 is uniform over the whole surface. Every
/// barnacle is a volcano shell — a frustum with a cratered top — standing on the
/// surface normal, its base sunk a little into the surface so it reads as attached,
/// never floating. Centres keep a minimum spacing of 0.85 × Size, so barnacles do not
/// clip each other (rule #1: no clipping, ever).
///
/// The target body is never touched: the shells go into one new body, so the colony
/// can be re-run, moved or deleted without rebuilding the buoy. If the target is
/// rigged, the shells inherit its skin weights, like Wear's fray threads do.
///
/// HONEST LIMITS. The crater is a fixed volcano profile, not a grown shell with
/// growth rings; the cluster rule is a distance gate, not cell division, so a colony
/// never splits or merges the way real growth does. Slot 0 means "next free slot"
/// (one past the highest slot in the document), so the shells never steal the
/// target's own material.
/// </summary>
public sealed class BarnacleFeature : Feature
{
	public override string TypeName => "Barnacle";
	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Target = new( "Target" );
	public readonly IntParam Count = new( "Count", 24, 0, 100000 );
	public readonly FloatParam Size = new( "Size", 0.5f, 0.01f, 1000f, "u" );
	public readonly FloatParam Height = new( "Height", 0.6f, 0.01f, 1000f, "u" );
	public readonly FloatParam Cluster = new( "Cluster", 0.7f, 0f, 1f );
	public readonly IntParam Sides = new( "Sides", 8, 5, 32 );
	public readonly IntParam Seed = new( "Seed", 7, 0, 1000000 );
	public readonly IntParam Slot = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Target, Count, Size, Height, Cluster, Sides, Seed, Slot };

	/// <summary>Where each placed barnacle sits, in target-model space, growth order.</summary>
	public List<Vec3> Centres { get; } = new();

	/// <summary>How many barnacles actually landed (may be fewer than Count if the surface ran out of room).</summary>
	public int Placed => Centres.Count;

	protected override void Execute( FeatureContext ctx )
	{
		Centres.Clear();

		if ( Target.BodyIds.Count == 0 )
			Fail( "Barnacle needs a target body.", "Target is empty, so there is no surface to encrust.", "Pick the body to grow barnacles on" );

		var targetBody = ctx.Bodies.FirstOrDefault( b => Target.BodyIds.Contains( b.Id ) );

		if ( targetBody is null )
			Fail( "Barnacle's target body is not above it.",
				$"Target names '{string.Join( ",", Target.BodyIds )}', which is not a body that has run before this one.",
				"Put the target body first", "Check the target name" );

		var target = targetBody!.Mesh;

		if ( target is null || target.VertexCount == 0 || target.FaceCount == 0 )
			Fail( "Barnacle's target body has no mesh.", "There is no surface to encrust.", "Give the target body geometry first" );

		var count = Count.Clamped;
		var size = Size.Clamped;
		var height = Height.Clamped;
		var sides = Sides.Clamped;
		var rng = new Random( Seed.Clamped );

		// Area-weighted face table, so a big face grows proportionally more barnacles
		// than a sliver — uniform over faces would pile them onto subdivided patches.
		var areas = new float[target.FaceCount];
		var totalArea = 0f;

		for ( var fi = 0; fi < target.FaceCount; fi++ )
		{
			areas[fi] = MathF.Max( 0f, target.FaceArea( target.Faces[fi] ) );
			totalArea += areas[fi];
		}

		if ( totalArea <= 0f )
			Fail( "Barnacle's target has no surface area.", "Every face is degenerate.", "Give the target a real surface first" );

		Vec3 SampleSurface( out Vec3 normal )
		{
			var pick = (float)rng.NextDouble() * totalArea;

			for ( var fi = 0; fi < target.FaceCount; fi++ )
			{
				pick -= areas[fi];

				if ( pick > 0f && fi < target.FaceCount - 1 )
					continue;

				var face = target.Faces[fi];
				var a = target.Positions[face.Indices[0]];
				var b = target.Positions[face.Indices[1 % face.Indices.Length]];
				var c = target.Positions[face.Indices[2 % face.Indices.Length]];
				var r1 = (float)rng.NextDouble();
				var r2 = (float)rng.NextDouble();

				if ( r1 + r2 > 1f ) { r1 = 1f - r1; r2 = 1f - r2; }

				normal = target.FaceNormal( face );
				return a + (b - a) * r1 + (c - a) * r2;
			}

			normal = new Vec3( 0, 0, 1 );
			return target.Positions[0];
		}

		var cluster = Cluster.Clamped;
		var minSpacing = size * 0.85f;
		// Cluster 1 keeps a newcomer within 2×Size of a neighbour; Cluster 0 has no
		// gate at all, so the colony spreads over the whole surface.
		var clusterRadius = cluster <= 0f ? float.MaxValue : size * (2f + (1f - cluster) * 10f);
		var attempts = Math.Max( 100, count * 500 );

		for ( var attempt = 0; attempt < attempts && Centres.Count < count; attempt++ )
		{
			var p = SampleSurface( out _ );

			var tooClose = false;

			foreach ( var other in Centres )
				if ( (other - p).Length < minSpacing ) { tooClose = true; break; }

			if ( tooClose )
				continue;

			if ( cluster > 0f && Centres.Count > 0 )
			{
				var nearest = float.MaxValue;

				foreach ( var other in Centres )
					nearest = MathF.Min( nearest, (other - p).Length );

				if ( nearest > clusterRadius )
					continue;
			}

			Centres.Add( p );
		}

		if ( Centres.Count == 0 )
			Fail( "Barnacle placed nothing.", "The surface, spacing or cluster gate left no point.",
				"Widen the target", "Lower Count or Size", "Lower Cluster" );

		if ( Centres.Count < count )
			Warn( $"Barnacle placed {Centres.Count} of {count} asked.",
				"The surface ran out of room at this spacing and cluster.",
				"Lower Count or Size", "Lower Cluster" );

		var slot = Slot.Clamped > 0 ? Slot.Clamped
			: ctx.Bodies.SelectMany( b => b.Mesh.Faces ).Select( f => f.Material )
				.Concat( ctx.ShellSlots ).DefaultIfEmpty( 0 ).Max() + 1;

		var result = new PolyMesh();
		var colours = new List<Vec4>();

		foreach ( var centre in Centres )
		{
			// Re-sample the normal at the centre's own face: the placement sample
			// already knows it, but centres alone do not carry it, so find it again
			// by nearest face — cheap at barnacle counts, and exact for flat faces.
			var normal = NearestNormal( target, centre );
			var scale = 0.7f + (float)rng.NextDouble() * 0.6f;
			var r = size * scale;
			var h = height * scale;
			var shell = ShellMesh( r, h, sides, slot );
			var z = normal.LengthSquared < 1e-12f ? new Vec3( 0, 0, 1 ) : normal.Normal;
			var helper = MathF.Abs( z.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
			var x = Vec3.Cross( helper, z ).Normal;
			var y = Vec3.Cross( z, x );
			var base_ = result.VertexCount;

			MeshTransform.Apply( shell, new Xform( x, y, z, centre - z * h * 0.15f ) );
			MeshTransform.Append( result, shell );

			// Shell vertices are light, crater vertices dark. ShellMesh vertex
			// order is base ring, top ring, crater ring, crater centre, base
			// centre — so the crater is the third ring plus the crater centre.
			for ( var v = base_; v < result.VertexCount; v++ )
			{
				var local = v - base_;
				var crater = (local >= sides * 2 && local < sides * 3) || local == sides * 3;
				colours.Add( crater ? new Vec4( 0.32f, 0.30f, 0.28f, 1f ) : new Vec4( 0.78f, 0.75f, 0.70f, 1f ) );
			}
		}

		result.VertexColors = colours.ToArray();

		if ( targetBody.Mesh.IsRigged )
			SkinBinder.TransferFrom( result, targetBody.Mesh );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? $"Barnacles on {targetBody.Name}", result ) );
	}

	static Vec3 NearestNormal( PolyMesh mesh, Vec3 p )
	{
		var best = -1;
		var bestD = float.MaxValue;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var d = (mesh.FaceCentroid( mesh.Faces[fi] ) - p).LengthSquared;

			if ( d < bestD ) { bestD = d; best = fi; }
		}

		return best < 0 ? new Vec3( 0, 0, 1 ) : mesh.FaceNormal( mesh.Faces[best] );
	}

	/// <summary>One volcano shell, standing on local z = 0, pointing up +z. Closed:
	/// the base is capped, so the shell has no open edges — the cap sits inside
	/// the target surface it is sunk into, invisible, and the shell stays a solid
	/// for manifold gates instead of an open cup.</summary>
	/// <param name="r">Base radius.</param>
	/// <param name="h">Height to the rim.</param>
	/// <param name="sides">Ring segments.</param>
	static PolyMesh ShellMesh( float r, float h, int sides, int slot )
	{
		var mesh = new PolyMesh();
		var twoPi = MathF.PI * 2f;

		// Ring order matters to the caller (crater vertices are the last sides+1).
		for ( var i = 0; i < sides; i++ )
		{
			var a = twoPi * i / sides;
			mesh.AddVertex( new Vec3( MathF.Cos( a ) * r, MathF.Sin( a ) * r, 0f ) );
		}

		for ( var i = 0; i < sides; i++ )
		{
			var a = twoPi * i / sides;
			mesh.AddVertex( new Vec3( MathF.Cos( a ) * r * 0.45f, MathF.Sin( a ) * r * 0.45f, h ) );
		}

		for ( var i = 0; i < sides; i++ )
		{
			var a = twoPi * i / sides;
			mesh.AddVertex( new Vec3( MathF.Cos( a ) * r * 0.20f, MathF.Sin( a ) * r * 0.20f, h * 0.72f ) );
		}

		var centre = mesh.AddVertex( new Vec3( 0f, 0f, h * 0.50f ) );
		var baseCentre = mesh.AddVertex( new Vec3( 0f, 0f, 0f ) );

		for ( var i = 0; i < sides; i++ )
		{
			var next = (i + 1) % sides;
			mesh.AddFace( new[] { i, next, sides + next, sides + i }, null, slot );
			mesh.AddFace( new[] { sides + i, sides + next, sides * 2 + next, sides * 2 + i }, null, slot );
			mesh.AddFace( new[] { sides * 2 + i, sides * 2 + next, centre }, null, slot );
			// Base cap, wound to face down (-z): reversed order against the rings.
			mesh.AddFace( new[] { next, i, baseCentre }, null, slot );
		}

		return mesh;
	}
}
