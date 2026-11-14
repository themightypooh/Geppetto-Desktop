#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

public sealed class FoliageTreeSettings
{
	public int AttractorCount = 1200;
	public float CrownRadius = 80f;
	public Vec3 CrownCenter = new( 0, 120f, 0 );
	public float TrunkHeight = 60f;
	public float SegmentLength = 6f;
	public float InfluenceRadius = 35f;
	public float KillDistance = 8f;
	public float BaseTrunkRadius = 5f;
	public float PipePower = 2.2f;
	public bool GenerateCanopyLeafCards = true;
	public float LeafCardSize = 12f;
}

public sealed class TreeNode
{
	public Vec3 Position;
	public TreeNode? Parent;
	public List<TreeNode> Children = new();
	public float Radius = 1f;
	public Vec3 GrowthDir = new( 0, 1, 0 );
	public bool IsLeafTip;

	public TreeNode( Vec3 pos, TreeNode? parent = null )
	{
		Position = pos;
		Parent = parent;
		parent?.Children.Add( this );
	}
}

/// <summary>
/// Algorithmic Botany: Space Colonization tree generator with Da Vinci pipe-model branch radius scaling
/// and spherical canopy normal transfer for lush in-game foliage rendering.
/// </summary>
public static class FoliageTree
{
	public static (PolyMesh Wood, PolyMesh Leaves) Generate( FoliageTreeSettings? settings = null )
	{
		settings ??= new FoliageTreeSettings();
		var rng = new Random( 1337 );

		// 1. Scatter attractor points inside spherical crown envelope
		var attractors = new List<Vec3>( settings.AttractorCount );
		for ( int i = 0; i < settings.AttractorCount; i++ )
		{
			Vec3 p;
			do
			{
				p = new Vec3(
					(float)(rng.NextDouble() * 2.0 - 1.0),
					(float)(rng.NextDouble() * 2.0 - 1.0),
					(float)(rng.NextDouble() * 2.0 - 1.0)
				);
			} while ( p.LengthSquared > 1.0f );

			attractors.Add( settings.CrownCenter + p * settings.CrownRadius );
		}

		// 2. Grow trunk until reaching the attraction zone
		var root = new TreeNode( Vec3.Zero );
		var current = root;
		float currentHeight = 0;
		while ( currentHeight < settings.TrunkHeight )
		{
			currentHeight += settings.SegmentLength;
			current = new TreeNode( new Vec3( 0, currentHeight, 0 ), current );
		}

		var allNodes = new List<TreeNode> { root };
		var currentWalk = root;
		while ( currentWalk.Children.Count > 0 )
		{
			currentWalk = currentWalk.Children[0];
			allNodes.Add( currentWalk );
		}

		// 3. Space Colonization iterations
		int maxIterations = 150;
		for ( int iter = 0; iter < maxIterations && attractors.Count > 0; iter++ )
		{
			var nodeAttractors = new Dictionary<TreeNode, List<Vec3>>();

			for ( int aIdx = attractors.Count - 1; aIdx >= 0; aIdx-- )
			{
				var a = attractors[aIdx];
				TreeNode? closestNode = null;
				float minDistSq = float.MaxValue;

				foreach ( var node in allNodes )
				{
					float dSq = (a - node.Position).LengthSquared;
					if ( dSq < minDistSq )
					{
						minDistSq = dSq;
						closestNode = node;
					}
				}

				if ( closestNode != null && minDistSq <= settings.InfluenceRadius * settings.InfluenceRadius )
				{
					if ( !nodeAttractors.TryGetValue( closestNode, out var list ) )
					{
						list = new List<Vec3>();
						nodeAttractors[closestNode] = list;
					}
					list.Add( a );
				}
			}

			if ( nodeAttractors.Count == 0 ) break;

			var newNodes = new List<TreeNode>();
			foreach ( var kvp in nodeAttractors )
			{
				var parent = kvp.Key;
				var targetPts = kvp.Value;

				Vec3 avgDir = Vec3.Zero;
				foreach ( var pt in targetPts )
				{
					avgDir += (pt - parent.Position).Normal;
				}
				avgDir = (avgDir / targetPts.Count).Normal;

				var child = new TreeNode( parent.Position + avgDir * settings.SegmentLength, parent );
				child.GrowthDir = avgDir;
				newNodes.Add( child );
			}

			allNodes.AddRange( newNodes );

			// Remove attractors within kill distance of any node
			for ( int aIdx = attractors.Count - 1; aIdx >= 0; aIdx-- )
			{
				var a = attractors[aIdx];
				foreach ( var node in allNodes )
				{
					if ( (a - node.Position).LengthSquared <= settings.KillDistance * settings.KillDistance )
					{
						attractors.RemoveAt( aIdx );
						break;
					}
				}
			}
		}

		// 4. Leonardo da Vinci Pipe Model Rule for Branch Radii
		ComputePipeRadii( root, settings.PipePower, settings.BaseTrunkRadius );

		// 5. Generate Wood Mesh
		var wood = GenerateBranchMesh( allNodes );

		// 6. Generate Foliage Leaf Cards
		var leaves = new PolyMesh();
		if ( settings.GenerateCanopyLeafCards )
		{
			leaves = GenerateCanopyLeaves( allNodes, settings.CrownCenter, settings.LeafCardSize );
		}

		return (wood, leaves);
	}

	static float ComputePipeRadii( TreeNode node, float power, float baseRadius )
	{
		if ( node.Children.Count == 0 )
		{
			node.Radius = 0.4f;
			node.IsLeafTip = true;
			return MathF.Pow( node.Radius, power );
		}

		float childSum = 0;
		foreach ( var child in node.Children )
		{
			childSum += ComputePipeRadii( child, power, baseRadius );
		}

		node.Radius = MathF.Pow( childSum, 1.0f / power );
		if ( node.Parent == null )
		{
			float scale = baseRadius / MathF.Max( node.Radius, 0.01f );
			ScaleRadiiRecursive( node, scale );
		}

		return childSum;
	}

	static void ScaleRadiiRecursive( TreeNode node, float scale )
	{
		node.Radius *= scale;
		foreach ( var child in node.Children ) ScaleRadiiRecursive( child, scale );
	}

	static PolyMesh GenerateBranchMesh( List<TreeNode> nodes )
	{
		var mesh = new PolyMesh();
		const int radialSegs = 6;

		foreach ( var node in nodes )
		{
			if ( node.Children.Count == 0 ) continue;

			foreach ( var child in node.Children )
			{
				Vec3 dir = (child.Position - node.Position).Normal;
				Vec3 side = MathF.Abs( dir.y ) > 0.9f ? new Vec3( 1, 0, 0 ) : Vec3.Cross( dir, new Vec3( 0, 1, 0 ) ).Normal;
				Vec3 up = Vec3.Cross( dir, side ).Normal;

				int baseV0 = mesh.Positions.Count;
				for ( int i = 0; i < radialSegs; i++ )
				{
					float angle = i * MathF.PI * 2f / radialSegs;
					Vec3 r = (side * MathF.Cos( angle ) + up * MathF.Sin( angle ));
					mesh.AddVertex( node.Position + r * node.Radius );
					mesh.AddVertex( child.Position + r * child.Radius );
				}

				for ( int i = 0; i < radialSegs; i++ )
				{
					int next = (i + 1) % radialSegs;
					int v0 = baseV0 + i * 2;
					int v1 = baseV0 + i * 2 + 1;
					int v2 = baseV0 + next * 2 + 1;
					int v3 = baseV0 + next * 2;

					float u0 = (float)i / radialSegs;
					float u1 = (float)(i + 1) / radialSegs;
					var branchUvs = new[] { new Vec2( u0, 0f ), new Vec2( u0, 1f ), new Vec2( u1, 1f ), new Vec2( u1, 0f ) };

					mesh.Faces.Add( new Face( new[] { v0, v1, v2, v3 }, branchUvs ) );
				}
			}
		}

		return mesh;
	}

	static PolyMesh GenerateCanopyLeaves( List<TreeNode> nodes, Vec3 crownCenter, float cardSize )
	{
		var leaves = new PolyMesh();
		float half = cardSize * 0.5f;

		var uvs = new[]
		{
			new Vec2( 0, 0 ),
			new Vec2( 1, 0 ),
			new Vec2( 1, 1 ),
			new Vec2( 0, 1 )
		};

		foreach ( var node in nodes )
		{
			if ( !node.IsLeafTip ) continue;

			Vec3 p = node.Position;
			int v0 = leaves.Positions.Count;

			leaves.AddVertex( p + new Vec3( -half, -half, 0 ) );
			leaves.AddVertex( p + new Vec3( half, -half, 0 ) );
			leaves.AddVertex( p + new Vec3( half, half, 0 ) );
			leaves.AddVertex( p + new Vec3( -half, half, 0 ) );

			leaves.AddVertex( p + new Vec3( 0, -half, -half ) );
			leaves.AddVertex( p + new Vec3( 0, -half, half ) );
			leaves.AddVertex( p + new Vec3( 0, half, half ) );
			leaves.AddVertex( p + new Vec3( 0, half, -half ) );

			leaves.Faces.Add( new Face( new[] { v0, v0 + 1, v0 + 2, v0 + 3 }, (Vec2[])uvs.Clone() ) );
			leaves.Faces.Add( new Face( new[] { v0 + 4, v0 + 5, v0 + 6, v0 + 7 }, (Vec2[])uvs.Clone() ) );
		}

		return leaves;
	}
}
