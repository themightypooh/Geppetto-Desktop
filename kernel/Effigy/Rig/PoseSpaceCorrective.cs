using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Corrective shape keys: a shape sculpted (or draped) in a POSE, stored as a bind-space morph that
/// a <see cref="MorphDriver"/> fades in as the rig reaches that pose - pose-space deformation.
///
/// WHY THE SCULPT IS UN-SKINNED. s&amp;box applies morphs before skinning (skinning_cs), as every
/// engine does. So a morph offset d on vertex v shows up posed as M_v·d, where M_v = Σ wᵢ·(poseᵢ ·
/// bindᵢ⁻¹) is the vertex's blended skin matrix. To make the posed vertex land where it was
/// sculpted, s, the bind-space offset has to be M_v⁻¹·s − bind_v: invert each vertex's blended
/// matrix (inverse LBS) and carry the sculpt back. The posed shape then reproduces exactly, to float
/// precision, at the pose it was sculpted in - and scales off smoothly as the driver lets go.
///
/// A vertex whose blended matrix is singular (two bones turned 180° apart at half weight each, the
/// candy-wrapper collapse) has no inverse. It takes its heaviest bone's inverse instead and is
/// counted in <see cref="Result.Singular"/>, so the caller can say which corner is approximate.
///
/// research-sitting.md §8.4 is the first use: pose the wearer seated, drape the trousers there, and
/// <see cref="SeatedGarment"/> turns the drape into one corrective per thigh.
/// </summary>
public static class PoseSpaceCorrective
{
	/// <summary>Posed positions and the per-vertex un-skin matrices for one pose.</summary>
	public sealed class Result
	{
		public Vec3[] Posed;
		public Xform[] Unskin;

		/// <summary>Vertices whose blended skin matrix could not be inverted; see the summary.</summary>
		public int Singular;
	}

	/// <summary>Every bone's model-space transform at the skeleton's current locals.</summary>
	public static Xform[] World( Skeleton skeleton )
	{
		var world = new Xform[skeleton.Count];

		for ( var i = 0; i < world.Length; i++ )
			world[i] = skeleton.WorldBind( i );

		return world;
	}

	/// <summary>A vertex's blended skin matrix Σ wᵢ·skinᵢ. Unweighted: identity.</summary>
	public static Xform Blend( BoneWeight[] weights, IReadOnlyList<Xform> skin )
	{
		if ( weights is not { Length: > 0 } )
			return Xform.Identity;

		Vec3 x = Vec3.Zero, y = Vec3.Zero, z = Vec3.Zero, o = Vec3.Zero;

		foreach ( var bw in weights )
		{
			var m = skin[bw.Bone];
			x += m.X * bw.Weight;
			y += m.Y * bw.Weight;
			z += m.Z * bw.Weight;
			o += m.Origin * bw.Weight;
		}

		return new Xform( x, y, z, o );
	}

	/// <summary>
	/// Skin <paramref name="bind"/> into the pose and keep each vertex's inverse blended matrix.
	/// Posed matches <see cref="SkinBinder.Deform"/> exactly.
	/// </summary>
	public static Result Pose( IReadOnlyList<Vec3> bind, SkinWeights weights, IReadOnlyList<Xform> bindWorld, IReadOnlyList<Xform> poseWorld )
	{
		if ( bind is null ) throw new ArgumentNullException( nameof( bind ) );
		if ( weights is null || weights.Count != bind.Count )
			throw new ArgumentException( $"weights cover {weights?.Count ?? 0} vertices, the mesh has {bind.Count}", nameof( weights ) );

		var skin = new Xform[bindWorld.Count];

		for ( var b = 0; b < skin.Length; b++ )
			skin[b] = poseWorld[b] * bindWorld[b].Inverse;

		var result = new Result { Posed = new Vec3[bind.Count], Unskin = new Xform[bind.Count] };

		for ( var v = 0; v < bind.Count; v++ )
		{
			var w = weights[v];

			if ( w is not { Length: > 0 } )
			{
				result.Posed[v] = bind[v];
				result.Unskin[v] = Xform.Identity;
				continue;
			}

			var m = Blend( w, skin );
			result.Posed[v] = m.TransformPoint( bind[v] );

			if ( MathF.Abs( m.Determinant ) > 1e-6f )
			{
				result.Unskin[v] = m.Inverse;
				continue;
			}

			var heaviest = w.OrderByDescending( bw => bw.Weight ).First().Bone;
			result.Unskin[v] = skin[heaviest].Inverse;
			result.Singular++;
		}

		return result;
	}

	/// <summary>
	/// Bind-space offsets for a shape given posed: each sculpted posed point carried back through its
	/// vertex's un-skin matrix, minus the bind point. An offset shorter than <paramref name="epsilon"/>
	/// is zero, so an untouched vertex stays out of the morph. <paramref name="mask"/> (0..1 per
	/// vertex, optional) scales each offset.
	/// </summary>
	public static Vec3[] Deltas( IReadOnlyList<Vec3> bindBase, IReadOnlyList<Xform> unskin, IReadOnlyList<Vec3> sculptedPosed,
		IReadOnlyList<float> mask = null, float epsilon = 1e-4f )
	{
		var deltas = new Vec3[bindBase.Count];

		for ( var v = 0; v < deltas.Length; v++ )
		{
			var d = unskin[v].TransformPoint( sculptedPosed[v] ) - bindBase[v];

			if ( mask is not null )
				d *= Math.Clamp( mask[v], 0f, 1f );

			deltas[v] = d.Length < epsilon ? Vec3.Zero : d;
		}

		return deltas;
	}

	/// <summary>
	/// Add a corrective shape key to <paramref name="mesh"/> (at bind) from where its vertices should
	/// be in a pose: un-skinned into bind-space offsets, with <paramref name="driver"/> riding the key.
	/// The mesh's own positions are not changed. Returns the morph and how many vertices were singular.
	/// </summary>
	public static (MeshMorph Morph, int Singular) Bake( PolyMesh mesh, SkinWeights weights, IReadOnlyList<Xform> bindWorld,
		IReadOnlyList<Xform> poseWorld, IReadOnlyList<Vec3> posedTarget, string name, MorphDriver driver, IReadOnlyList<float> mask = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( posedTarget is null || posedTarget.Count != mesh.VertexCount )
			throw new ArgumentException( $"the posed shape has {posedTarget?.Count ?? 0} vertices, the mesh has {mesh.VertexCount}", nameof( posedTarget ) );

		var posed = Pose( mesh.Positions, weights, bindWorld, poseWorld );
		var morph = new MeshMorph
		{
			Name = name,
			Deltas = Deltas( mesh.Positions, posed.Unskin, posedTarget, mask ),
			Driver = driver?.Clone(),
		};

		if ( morph.Driver is not null )
			morph.Driver.Morph = name;

		mesh.Morphs.RemoveAll( m => m?.Name == name );
		mesh.Morphs.Add( morph );
		return (morph, posed.Singular);
	}

	/// <summary>
	/// The mesh as the game shows it in a pose: bind positions plus each driven key at its driver's
	/// weight in that pose, then skinned. What a corrective is checked against.
	/// </summary>
	public static Vec3[] Skin( PolyMesh mesh, SkinWeights weights, Skeleton bind, Skeleton posed )
	{
		var local = posed.Bones.Select( b => b.Local ).ToArray();
		var driven = MorphDriver.WeightsAt( MorphDriver.FromMesh( mesh ), bind, local );
		var shaped = mesh.Positions.ToArray();

		foreach ( var morph in mesh.ValidMorphs )
		{
			if ( !driven.TryGetValue( morph.Name, out var w ) || w == 0f )
				continue;

			for ( var i = 0; i < shaped.Length; i++ )
				shaped[i] += morph.Deltas[i] * w;
		}

		return SkinBinder.Deform( shaped, weights, World( bind ), World( posed ) );
	}

	/// <summary>
	/// A copy of <paramref name="bind"/> with the driver's bone turned to the driver's Target, so a
	/// corrective can be sculpted where it will be at full weight. Rotation turns about Axis by Target
	/// degrees; Swing tips Axis by Target degrees about the next axis round (X→Y, Y→Z, Z→X). Distance
	/// names no turn, so it returns null.
	/// </summary>
	public static Skeleton PosedFor( Skeleton bind, MorphDriver driver, float? degrees = null )
	{
		var b = bind.IndexOf( driver.Bone );

		if ( b < 0 || driver.Channel == DriverChannel.Distance )
			return null;

		var axis = driver.Channel == DriverChannel.Swing
			? MorphDriver.AxisVector( (DriverAxis)(((int)driver.Axis + 1) % 3) )
			: MorphDriver.AxisVector( driver.Axis );

		var posed = bind.Clone();
		var local = posed.Bones[b].Local;
		var turn = Quat.FromAxisAngle( axis, (degrees ?? driver.Target) * MathF.PI / 180f );
		var rotation = Quat.FromXform( local ) * turn;
		posed.Bones[b].Local = rotation.ToXform( local.Origin, Quat.ScaleOf( local ) );
		return posed;
	}

	/// <summary>
	/// A driver for <paramref name="bone"/> that reads 0 at bind and 1 in <paramref name="posed"/>:
	/// a Rotation driver about whichever of the bone's axes it turned about most. Null when the bone
	/// is missing or barely turned (under a degree), which no corrective can be driven from.
	/// </summary>
	public static MorphDriver Measure( Skeleton bind, Skeleton posed, string bone, string morph = "" )
	{
		var b = bind.IndexOf( bone );
		var p = posed.IndexOf( bone );

		if ( b < 0 || p < 0 )
			return null;

		var delta = Quat.FromXform( bind.Bones[b].Local ).Inverse * Quat.FromXform( posed.Bones[p].Local );
		var best = DriverAxis.X;
		var bestDegrees = 0f;

		foreach ( var axis in new[] { DriverAxis.X, DriverAxis.Y, DriverAxis.Z } )
		{
			var d = MorphDriver.RotationAbout( delta, MorphDriver.AxisVector( axis ) );

			if ( MathF.Abs( d ) > MathF.Abs( bestDegrees ) )
			{
				best = axis;
				bestDegrees = d;
			}
		}

		if ( MathF.Abs( bestDegrees ) < 1f )
			return null;

		return new MorphDriver { Morph = morph, Bone = bone, Channel = DriverChannel.Rotation, Axis = best, Rest = 0f, Target = bestDegrees };
	}

	/// <summary>What <see cref="AutoBuild"/> produced: the baked morph, its driver, and how strained the raw pose was.</summary>
	public sealed class AutoBuildResult
	{
		public MeshMorph Morph;
		public MorphDriver Driver;
		public int Singular;

		/// <summary>Largest edge-length change (|ratio - 1|) between bind and the raw posed mesh.</summary>
		public float MaxStrain;

		/// <summary>Mean edge-length change over the mesh.</summary>
		public float MeanStrain;
	}

	/// <summary>
	/// Automatic joint corrective (roadmap J3): bake the morph that repairs what linear blend
	/// skinning loses when <paramref name="boneIndex"/> turns <paramref name="angleDeg"/>
	/// degrees about <paramref name="axis"/>.
	///
	/// 1. Pose the rig with <see cref="PosedFor"/> and skin the mesh raw (no morphs).
	/// 2. Relax the raw pose: neighbour-average smoothing for
	/// <paramref name="relaxIterations"/> passes, confined to the driven bone's influence and
	/// scaled by it — the delta-mush idea (smooth the deformation, keep the detail), applied
	/// as a bounded relaxation rather than a full delta-mush solve, which the kernel has no
	/// solver for. Vertices the bone does not influence do not move at all.
	/// 3. <see cref="Bake"/> the relaxed pose into a morph driven by the bone at this angle,
	/// masked by the bone's influence, named <c>bone_bendANGLE</c> like
	/// <see cref="CorrectiveMorph.Name"/>.
	///
	/// The mesh's own positions are not changed; the morph is added to its Morphs, and when
	/// the mesh was not rigged its heat weights are kept as its Skin so the corrective travels
	/// with a bind. Strain is measured directly from edge-length change (bind vs raw pose);
	/// the roadmap's "SkinStrain.Measure" does not exist under that name (SkinStrain offers
	/// StrainField) and FlexReport.Run is a clash/volume diagnostic, not a corrective input,
	/// so neither is called here — that naming gap is flagged for Argon, not papered over.
	/// </summary>
	public static AutoBuildResult AutoBuild( PolyMesh mesh, Skeleton rig, int boneIndex, DriverAxis axis,
		float angleDeg, int relaxIterations = 8 )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( rig is null )
			throw new ArgumentNullException( nameof( rig ) );

		if ( boneIndex < 0 || boneIndex >= rig.Count )
			throw new ArgumentOutOfRangeException( nameof( boneIndex ), $"The rig has {rig.Count} bones; {boneIndex} is not one." );

		if ( MathF.Abs( angleDeg ) < 1e-6f )
			throw new ArgumentException( "A corrective needs a non-zero angle.", nameof( angleDeg ) );

		var boneName = rig.Bones[boneIndex].Name;
		var driver = new MorphDriver { Bone = boneName, Channel = DriverChannel.Rotation, Axis = axis, Rest = 0f, Target = angleDeg };

		var weights = mesh.IsRigged ? mesh.Skin : AutoWeights.Heat( mesh, rig, out _ );

		if ( weights.Count != mesh.VertexCount )
			throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );

		if ( !mesh.IsRigged )
			mesh.Skin = weights;

		var posed = PosedFor( rig, driver, angleDeg )
			?? throw new InvalidOperationException( $"Bone '{boneName}' cannot be posed for a corrective." );

		var bindWorld = World( rig );
		var poseWorld = World( posed );
		var raw = SkinBinder.Deform( mesh.Positions, weights, bindWorld, poseWorld );
		var target = Relax( raw, mesh, weights, boneIndex, relaxIterations );

		var mask = new float[mesh.VertexCount];

		for ( var v = 0; v < mesh.VertexCount; v++ )
			foreach ( var w in weights[v] )
				if ( w.Bone == boneIndex )
				{
					mask[v] = w.Weight;
					break;
				}

		var (morph, singular) = Bake( mesh, weights, bindWorld, poseWorld, target,
			CorrectiveMorph.Name( boneName, angleDeg ), driver, mask );
		var (maxStrain, meanStrain) = Strain( mesh, raw );

		return new AutoBuildResult { Morph = morph, Driver = driver, Singular = singular, MaxStrain = maxStrain, MeanStrain = meanStrain };
	}

	static Vec3[] Relax( Vec3[] posed, PolyMesh mesh, SkinWeights weights, int bone, int iterations )
	{
		var current = (Vec3[])posed.Clone();

		if ( iterations <= 0 )
			return current;

		var neighbours = mesh.BuildVertexEdges();

		for ( var it = 0; it < iterations; it++ )
		{
			var next = (Vec3[])current.Clone();

			for ( var v = 0; v < mesh.VertexCount; v++ )
			{
				var w = 0f;

				foreach ( var bw in weights[v] )
					if ( bw.Bone == bone )
					{
						w = bw.Weight;
						break;
					}

				if ( w <= 0f || neighbours[v].Count == 0 )
					continue;

				var avg = Vec3.Zero;
				var count = 0;

				foreach ( var e in neighbours[v] )
				{
					var nb = e.A == v ? e.B : e.A;

					if ( nb < 0 || nb >= mesh.VertexCount )
						continue;

					avg += current[nb];
					count++;
				}

				if ( count == 0 )
					continue;

				avg /= count;
				next[v] = current[v] + (avg - current[v]) * (0.5f * w);
			}

			current = next;
		}

		return current;
	}

	static (float Max, float Mean) Strain( PolyMesh mesh, IReadOnlyList<Vec3> deformed )
	{
		var neighbours = mesh.BuildVertexEdges();
		var max = 0f;
		var sum = 0f;
		var count = 0;

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			foreach ( var e in neighbours[v] )
			{
				var nb = e.A == v ? e.B : e.A;

				if ( nb <= v || nb >= mesh.VertexCount )
					continue;

				var rest = (mesh.Positions[v] - mesh.Positions[nb]).Length;

			if ( rest < 1e-6f )
				continue;

				var s = MathF.Abs( (deformed[v] - deformed[nb]).Length / rest - 1f );

				max = Math.Max( max, s );
				sum += s;
				count++;
			}
		}

		return (max, count > 0 ? sum / count : 0f);
	}

	/// <summary>
	/// How much of each vertex belongs to one side of the body (+1 left, -1 right): its weight on that
	/// side's bones plus half its weight on middle bones. The left and right masks sum to 1 on every
	/// weighted vertex, so two one-sided correctives at full weight add back to the whole shape.
	/// </summary>
	public static float[] SideMask( SkinWeights weights, Skeleton skeleton, int side )
	{
		var sides = skeleton.Bones.Select( b => GarmentPoses.SideOf( b.Name ) ).ToArray();
		var mask = new float[weights.Count];

		for ( var v = 0; v < mask.Length; v++ )
		{
			var w = weights[v];

			if ( w is not { Length: > 0 } )
			{
				mask[v] = 0.5f;
				continue;
			}

			var total = 0f;
			var mine = 0f;

			foreach ( var bw in w )
			{
				total += bw.Weight;
				var s = bw.Bone >= 0 && bw.Bone < sides.Length ? sides[bw.Bone] : 0;
				mine += s == side ? bw.Weight : s == 0 ? bw.Weight * 0.5f : 0f;
			}

			mask[v] = total > 1e-6f ? mine / total : 0.5f;
		}

		return mask;
	}

	/// <summary>The first bone of a region's chain on one side: the thigh for UpperLeg, left or right.</summary>
	public static string RegionRoot( Skeleton skeleton, BodyRegion region, int side )
	{
		for ( var i = 0; i < skeleton.Count; i++ )
		{
			var bone = skeleton.Bones[i];

			if ( BodyRegions.Classify( bone.Name ) != region || GarmentPoses.SideOf( bone.Name ) != side )
				continue;

			if ( bone.Parent >= 0 && BodyRegions.Classify( skeleton.Bones[bone.Parent].Name ) == region )
				continue;

			return bone.Name;
		}

		return null;
	}

	/// <summary>
	/// Seated garment correctives (research-sitting.md §8.4): <paramref name="draped"/> is where the
	/// garment's vertices settled with the wearer in <paramref name="seated"/> (a drape, a sim at
	/// rest, or a hand-sculpted fix). Makes <c>{name}_L</c> and <c>{name}_R</c>, each masked to its
	/// side (<see cref="SideMask"/>) and driven by that side's thigh from 0 at bind to 1 at the seated
	/// angle, measured off the pose. Both at full weight re-skin to the drape. A side whose thigh is
	/// missing or not turned is skipped and named in the returned problems.
	/// </summary>
	public static (List<MeshMorph> Morphs, List<string> Problems) SeatedGarment( PolyMesh garment, SkinWeights weights,
		Skeleton bind, Skeleton seated, IReadOnlyList<Vec3> draped, string name = "seated" )
	{
		var morphs = new List<MeshMorph>();
		var problems = new List<string>();
		var bindWorld = World( bind );
		var poseWorld = World( seated );

		foreach ( var (side, suffix) in new[] { (+1, "_L"), (-1, "_R") } )
		{
			var thigh = RegionRoot( bind, BodyRegion.UpperLeg, side );

			if ( thigh is null )
			{
				problems.Add( $"No {(side > 0 ? "left" : "right")} thigh bone was found, so {name}{suffix} was not made. Name the leg bones the usual way (thigh_L, UpperLeg_R, …)." );
				continue;
			}

			var driver = Measure( bind, seated, thigh, name + suffix );

			if ( driver is null )
			{
				problems.Add( $"{thigh} is not turned in the seated pose, so nothing can drive {name}{suffix}. Pose the thighs forward first." );
				continue;
			}

			var (morph, singular) = Bake( garment, weights, bindWorld, poseWorld, draped, name + suffix, driver, SideMask( weights, bind, side ) );
			morphs.Add( morph );

			if ( singular > 0 )
				problems.Add( $"{singular} vertices of {name}{suffix} had no exact un-skin (opposed bones at equal weight) and follow their heaviest bone." );
		}

		return (morphs, problems);
	}
}
