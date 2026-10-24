using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>One bone of the hierarchy a <see cref="FoliageFeature"/> grows. A feature
/// cannot own a <see cref="Skeleton"/> — that lives at studio level, where TreeGen used
/// to build it from segment names — so the feature publishes the hierarchy it grew and
/// the exporter builds the skeleton from it, exactly as TreeGen's BuildSkeleton did from
/// the same segment list. Skin binding (SkinBinder over these bones) is the studio step
/// that follows; it is not faked inside the feature.</summary>
public sealed class FoliageBone
{
	public string Name;
	public string Parent;
	public Vec3 Head;
	public Vec3 Tail;
}

/// <summary>
/// Procedural foliage as a native feature (A4): trees, pines, palms, shrubs, kelp,
/// coral and vines from one allometric branching generator, promoted from the
/// <c>Effigy.Tests/TreeGen.cs</c> oak that used to live only in the test project.
///
/// A segment is a tapered tube (8-sided rings skinned by <see cref="Skinner"/>); leaves
/// are double-sided cards on material slot 1, wood on slot 0. Every vertex carries a
/// colour whose ALPHA is the wind weight: 0 at the base, 1 at the twig tips and leaves,
/// so a wind shader or a skinning pass can sway the canopy without moving the trunk.
/// The growth is deterministic for a given <see cref="Seed"/>: same numbers, same plant.
///
/// <see cref="Height"/> is exact: after growing, the mesh is scaled uniformly about its
/// base so its total height equals Height, so a 10-unit tree is 10 units tall no matter
/// how the preset's branching spreads it.
/// </summary>
public sealed class FoliageFeature : Feature
{
	public override string TypeName => "Foliage";

	public override GeometryKind Accepts => GeometryKind.None;

	public static readonly string[] Presets = { "Tree", "Pine", "Palm", "Shrub", "Kelp", "Coral", "Vine" };

	public readonly ChoiceParam Preset = new( "Preset", Presets );
	public readonly FloatParam Height = new( "Height", 10f, 0.1f, 10000f, unit: "u" );
	public readonly IntParam Levels = new( "Levels", 3, 0, 8 );
	public readonly FloatParam BranchAngle = new( "Branch angle", 35f, 0f, 180f, unit: "deg" );
	public readonly FloatParam Gnarl = new( "Gnarl", 0.35f, 0f, 2f );
	public readonly IntParam LeafCardCount = new( "Leaf cards", 0, 0, 100000 );
	public readonly IntParam Seed = new( "Seed", 11, 0, 1000000 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Preset, Height, Levels, BranchAngle, Gnarl, LeafCardCount, Seed, Material };

	/// <summary>The hierarchy this build grew, in growth order (parents before children).
	/// Empty until Execute runs.</summary>
	public IReadOnlyList<FoliageBone> Bones => _bones;

	List<FoliageBone> _bones = new();

	sealed class Seg
	{
		public string Name;
		public string Parent;
		public Vec3 Head, Tail;
		public float RadiusHead, RadiusTail;
		public int Depth;
		public float Wind;
	}

	sealed class Leaf
	{
		public Vec3 Position;
		public Vec3 Normal;
		public float Size;
	}

	sealed class PresetSpec
	{
		public int Trunks = 1;
		public Vec3 Up = new( 0, 0, 1 );
		public float UpBias = 0.15f;
		public float TrunkLengthFrac = 0.45f;
		public float TrunkRadiusFrac = 0.022f;
		public int[] Children = { 4, 3, 2 };
		public int ChildFallback = 2;
		public bool Leader = true;
		public float PitchMin = 22f, PitchMax = 42f;
		public float LengthMin = 0.52f, LengthMax = 0.74f;
		public float RadiusMin = 0.48f, RadiusMax = 0.66f;
		public int LeafClusterMin = 5, LeafClusterMax = 9;
		public LeafMode Leaves = LeafMode.TipClusters;
		public float LeafSize = 0.35f;
		public float Droop = 0f;
		public float HelixRadius = 0f;
	}

	enum LeafMode { TipClusters, AlongBranch, Fronds, None, TipNubs }

	static PresetSpec SpecFor( string preset ) => preset switch
	{
		"Pine" => new PresetSpec
		{
			TrunkLengthFrac = 0.6f, Children = new[] { 6, 5, 4 }, Leader = true,
			PitchMin = 62f, PitchMax = 82f, LengthMin = 0.55f, LengthMax = 0.7f,
			Leaves = LeafMode.AlongBranch, LeafSize = 0.3f,
		},
		"Palm" => new PresetSpec
		{
			TrunkLengthFrac = 0.75f, Children = new[] { 11 }, ChildFallback = 0, Leader = false,
			PitchMin = 78f, PitchMax = 95f, LengthMin = 0.9f, LengthMax = 1.05f,
			RadiusMin = 0.8f, RadiusMax = 0.95f, Leaves = LeafMode.Fronds,
			LeafSize = 0.5f, Droop = 0.6f,
		},
		"Shrub" => new PresetSpec
		{
			Trunks = 5, TrunkLengthFrac = 0.3f, Children = new[] { 3, 3, 2 }, Leader = false,
			PitchMin = 25f, PitchMax = 55f, LengthMin = 0.5f, LengthMax = 0.7f,
			LeafClusterMin = 4, LeafClusterMax = 7, LeafSize = 0.3f,
		},
		"Kelp" => new PresetSpec
		{
			TrunkLengthFrac = 0.9f, Children = new[] { 0 }, ChildFallback = 0, Leader = true, UpBias = 0.05f,
			PitchMin = 10f, PitchMax = 30f, Leaves = LeafMode.AlongBranch, LeafSize = 0.45f,
		},
		"Coral" => new PresetSpec
		{
			TrunkLengthFrac = 0.35f, Children = new[] { 4, 3, 3 }, Leader = false, UpBias = 0.02f,
			PitchMin = 20f, PitchMax = 70f, LengthMin = 0.55f, LengthMax = 0.75f,
			RadiusMin = 0.6f, RadiusMax = 0.8f, Leaves = LeafMode.TipNubs, LeafSize = 0.12f,
		},
		"Vine" => new PresetSpec
		{
			TrunkLengthFrac = 0.85f, Children = new[] { 1, 1 }, Leader = true, UpBias = 0.02f,
			PitchMin = 55f, PitchMax = 85f, LengthMin = 0.6f, LengthMax = 0.8f,
			Leaves = LeafMode.AlongBranch, LeafSize = 0.3f, HelixRadius = 0.35f,
		},
		_ => new PresetSpec(),
	};

	protected override void Execute( FeatureContext ctx )
	{
		var spec = SpecFor( Preset.Value );
		var rng = new Random( Seed.Clamped );
		var maxDepth = Levels.Clamped;
		var height = Height.Clamped;

		var segs = new List<Seg>();
		var leaves = new List<Leaf>();
		var n = 0;

		string Next( string prefix ) => $"{prefix}_{++n:00}";

		void Grow( string parent, Vec3 origin, Vec3 direction, float length, float radius, int depth )
		{
			direction = direction.Normal;
			var wobble = (depth > 0 ? 0.35f : 0.12f) * Gnarl.Clamped;
			direction = (direction + new Vec3( Rand( rng, -0.18f, 0.18f ), Rand( rng, -0.18f, 0.18f ), Rand( rng, -0.06f, 0.10f ) ) * wobble).Normal;

			if ( spec.HelixRadius > 0f && depth == 0 )
				direction = (direction + new Vec3( Rand( rng, -1f, 1f ), Rand( rng, -1f, 1f ), 0f ) * spec.HelixRadius).Normal;

			var tail = origin + direction * length;
			if ( spec.Droop > 0f && depth > 0 )
				tail += new Vec3( 0, 0, -1 ) * length * spec.Droop * 0.25f;

			var name = Next( depth == 0 ? "trunk" : depth < 2 ? "limb" : "twig" );
			var seg = new Seg
			{
				Name = name, Parent = parent, Head = origin, Tail = tail,
				RadiusHead = radius, RadiusTail = radius * Rand( rng, spec.RadiusMin, spec.RadiusMax ),
				Depth = depth, Wind = maxDepth <= 0 ? 1f : depth / (float)maxDepth,
			};
			segs.Add( seg );

			if ( depth >= maxDepth || radius < height * 0.0018f )
			{
				AddLeaves( spec, rng, seg, direction, leaves );
				return;
			}

			if ( spec.Leaves == LeafMode.AlongBranch || spec.Leaves == LeafMode.Fronds )
				AddLeaves( spec, rng, seg, direction, leaves );

			var childCount = depth < spec.Children.Length ? spec.Children[depth] : spec.ChildFallback;
			Orthonormal( direction, out var fx, out _ );
			var baseYaw = Rand( rng, 0f, 360f );

			// The preset's pitch centre is the default; moving BranchAngle off its
			// default of 35 overrides the preset, so a pine stays steep until asked not to.
			var pitchCentre = Math.Abs( BranchAngle.Value - 35f ) < 1e-6f
				? (spec.PitchMin + spec.PitchMax) / 2f
				: BranchAngle.Clamped;

			for ( var i = 0; i < childCount; i++ )
			{
				var yaw = baseYaw + 360f / Math.Max( childCount, 1 ) * i + Rand( rng, -18f, 18f );
				var pitch = Math.Clamp( pitchCentre + Rand( rng, -12f, 12f ), 5f, 175f );
				var outward = Rotate( fx, direction, yaw );
				var childDir = (direction * MathF.Cos( pitch * MathF.PI / 180f ) + outward * MathF.Sin( pitch * MathF.PI / 180f )).Normal;
				childDir = (childDir + spec.Up * spec.UpBias).Normal;
				var fork = Vec3.Lerp( origin, tail, Rand( rng, 0.72f, 0.96f ) );
				Grow( name, fork, childDir, length * Rand( rng, spec.LengthMin, spec.LengthMax ), radius * Rand( rng, spec.RadiusMin, spec.RadiusMax ), depth + 1 );
			}

			if ( spec.Leader && depth < 2 )
			{
				var leader = (direction + new Vec3( Rand( rng, -0.08f, 0.08f ), Rand( rng, -0.08f, 0.08f ), 0.18f )).Normal;
				Grow( name, tail, leader, length * Rand( rng, 0.55f, 0.70f ), radius * Rand( rng, 0.55f, 0.68f ), depth + 1 );
			}
		}

		var trunkLen = height * spec.TrunkLengthFrac;
		var trunkRad = MathF.Max( height * spec.TrunkRadiusFrac, 0.05f );

		for ( var t = 0; t < spec.Trunks; t++ )
		{
			var lean = spec.Trunks > 1
				? new Vec3( Rand( rng, -0.15f, 0.15f ), Rand( rng, -0.15f, 0.15f ), 1f )
				: new Vec3( 0.08f, -0.04f, 1f );
			Grow( null, new Vec3( t * 0.3f, 0, 0 ), lean, trunkLen, trunkRad, 0 );
		}

		if ( segs.Count == 0 )
			Fail( "Foliage grew nothing", "No segments survived the growth parameters.", "Raise Levels or Height", "Lower the branch angle spread" );

		// Leaf cap: keep an evenly spaced subset so a capped canopy stays even.
		if ( LeafCardCount.Clamped > 0 && leaves.Count > LeafCardCount.Clamped )
		{
			var keep = LeafCardCount.Clamped;
			var picked = new List<Leaf>( keep );
			for ( var i = 0; i < keep; i++ )
				picked.Add( leaves[(int)((i + 0.5f) * leaves.Count / keep)] );
			leaves = picked;
		}

		var mesh = new PolyMesh();
		var colours = new List<Vec4>();

		foreach ( var seg in segs )
		{
			var limb = LimbMesh( seg, Material.Clamped );
			var base_ = mesh.VertexCount;
			MeshTransform.Append( mesh, limb );
			for ( var v = base_; v < mesh.VertexCount; v++ )
				colours.Add( new Vec4( 0.4f, 0.3f, 0.2f, seg.Wind ) );
		}

		foreach ( var leaf in leaves )
		{
			var card = LeafCard( leaf, Material.Clamped );
			var base_ = mesh.VertexCount;
			MeshTransform.Append( mesh, card );
			for ( var v = base_; v < mesh.VertexCount; v++ )
				colours.Add( new Vec4( 0.25f, 0.55f, 0.25f, 1f ) );
		}

		mesh.VertexColors = colours.ToArray();

		// Exact height: scale uniformly about the base so Height means Height. The
		// published bones follow the same transform, or they would name points on a
		// plant of a different size than the mesh.
		var minZ = mesh.Positions.Min( p => p.z );
		var maxZ = mesh.Positions.Max( p => p.z );
		var grown = maxZ - minZ;
		var heightScale = 1f;

		if ( grown > 1e-6f )
		{
			heightScale = height / grown;
			MeshTransform.Apply( mesh, Xform.Translate( new Vec3( 0, 0, -minZ ) ));
			MeshTransform.Apply( mesh, Xform.Scale( new Vec3( heightScale, heightScale, heightScale ) ) );
		}

		_bones = segs.Select( s => new FoliageBone
		{
			Name = s.Name, Parent = s.Parent,
			Head = (s.Head + new Vec3( 0, 0, -minZ )) * heightScale,
			Tail = (s.Tail + new Vec3( 0, 0, -minZ )) * heightScale,
		} ).ToList();

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}

	static void AddLeaves( PresetSpec spec, Random rng, Seg seg, Vec3 direction, List<Leaf> leaves )
	{
		Orthonormal( direction, out var x, out var y );
		var span = (seg.Tail - seg.Head).Length;

		switch ( spec.Leaves )
		{
			case LeafMode.TipClusters:
			{
				var count = rng.Next( spec.LeafClusterMin, spec.LeafClusterMax + 1 );
				for ( var i = 0; i < count; i++ )
				{
					var jitter = x * Rand( rng, -0.11f, 0.11f ) + y * Rand( rng, -0.11f, 0.11f ) + direction * Rand( rng, -0.04f, 0.10f );
					leaves.Add( new Leaf
					{
						Position = seg.Tail + jitter * span * 0.3f,
						Normal = (direction + x * Rand( rng, -0.5f, 0.5f ) + y * Rand( rng, -0.5f, 0.5f )).Normal,
						Size = spec.LeafSize * span * 0.5f,
					} );
				}
				break;
			}
			case LeafMode.AlongBranch:
			case LeafMode.Fronds:
			{
				var count = Math.Max( 2, (int)(span / Math.Max( spec.LeafSize, 0.05f )) );
				for ( var i = 0; i < count; i++ )
				{
					var t = count <= 1 ? 0.5f : i / (float)(count - 1);
					var pos = Vec3.Lerp( seg.Head, seg.Tail, t )
						+ x * Rand( rng, -0.2f, 0.2f ) * span * 0.2f
						+ y * Rand( rng, -0.2f, 0.2f ) * span * 0.2f;
					leaves.Add( new Leaf
					{
						Position = pos,
						Normal = (x * Rand( rng, -1f, 1f ) + y * Rand( rng, -1f, 1f ) + direction * 0.3f).Normal,
						Size = spec.LeafSize * span * 0.35f,
					} );
				}
				break;
			}
			case LeafMode.TipNubs:
				leaves.Add( new Leaf { Position = seg.Tail, Normal = direction, Size = spec.LeafSize * span } );
				break;
		}
	}

	static PolyMesh LimbMesh( Seg seg, int material )
	{
		var dir = (seg.Tail - seg.Head).Normal;
		Orthonormal( dir, out var x, out var y );

		var head = new List<Vec3>();
		var tail = new List<Vec3>();

		for ( var i = 0; i < 8; i++ )
		{
			var a = i / 8f * 2f * MathF.PI;
			var off = x * MathF.Cos( a ) + y * MathF.Sin( a );
			head.Add( seg.Head + off * seg.RadiusHead );
			tail.Add( seg.Tail + off * seg.RadiusTail );
		}

		return Skinner.Skin( new List<List<Vec3>> { head, tail }, capEnds: true, material );
	}

	static PolyMesh LeafCard( Leaf leaf, int material )
	{
		Orthonormal( leaf.Normal, out var x, out var y );
		var hx = x * leaf.Size * 0.5f;
		var hy = y * leaf.Size * 0.35f;

		var mesh = new PolyMesh();
		var a = mesh.AddVertex( leaf.Position - hx - hy );
		var b = mesh.AddVertex( leaf.Position + hx - hy );
		var c = mesh.AddVertex( leaf.Position + hx + hy );
		var d = mesh.AddVertex( leaf.Position - hx + hy );

		// Double-sided: a leaf card has no thickness, so both windings are emitted and
		// the card reads from either side instead of vanishing from behind.
		// Leaves ride the next slot after the wood; clamped so a wood slot of 63 does
		// not spill into a slot that does not exist.
		var leafMaterial = Math.Min( material + 1, 63 );
		mesh.AddFace( new[] { a, b, c, d }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, leafMaterial );
		mesh.AddFace( new[] { a, d, c, b }, new[] { new Vec2( 0, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ), new Vec2( 1, 0 ) }, leafMaterial );

		return mesh;
	}

	static float Rand( Random rng, float a, float b ) => a + (float)rng.NextDouble() * (b - a);

	static void Orthonormal( Vec3 d, out Vec3 x, out Vec3 y )
	{
		d = d.Normal;
		var helper = MathF.Abs( d.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		x = Vec3.Cross( d, helper ).Normal;
		y = Vec3.Cross( d, x ).Normal;
	}

	static Vec3 Rotate( Vec3 v, Vec3 axis, float degrees ) =>
		Xform.Rotate( axis, degrees * MathF.PI / 180f ).TransformDirection( v );
}
