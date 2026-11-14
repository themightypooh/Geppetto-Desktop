using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Pose-driven correctives as one unit: the drivers that fire them plus the morphs
/// they drive.
///
/// The pieces already exist — <see cref="MorphDriver"/> reads a joint angle in a pose and
/// maps it through Rest → Target, <see cref="MeshMorph"/> carries the sculpted deltas, and
/// <see cref="MorphDriver.WeightsAt"/> evaluates a whole driver list at once. What was
/// missing was the set that holds them together and turns a pose into deformed positions in
/// one call, with the mismatch checks (a driver with no morph, a morph with no driver, deltas
/// that do not match the mesh) reported instead of silently skipped.</summary>
public sealed class CorrectiveSet
{
	public List<MorphDriver> Drivers { get; } = new();
	public List<MeshMorph> Morphs { get; } = new();

	/// <summary>Every morph's weight in a pose: 0 at rest, 1 at the driver's target angle,
	/// eased in between. Drivers on one morph multiply (see
	/// <see cref="MorphDriver.WeightsAt"/>). <paramref name="posedLocal"/> is every bone's
	/// parent-space transform in the pose; <paramref name="bind"/> is the rest skeleton the
	/// drivers measure against.</summary>
	public Dictionary<string, float> Evaluate( Skeleton bind, IReadOnlyList<Xform> posedLocal )
	{
		if ( bind is null )
			throw new ArgumentNullException( nameof( bind ) );
		if ( posedLocal is null )
			throw new ArgumentNullException( nameof( posedLocal ) );
		return MorphDriver.WeightsAt( Drivers, bind, posedLocal );
	}

	/// <summary>Apply evaluated morph weights to bind positions, pure: a new array, the
	/// input untouched. A morph whose deltas do not match the vertex count is refused
	/// rather than applied to the wrong vertices.</summary>
	public Vec3[] Apply( IReadOnlyList<Vec3> positions, IReadOnlyDictionary<string, float> weights )
	{
		if ( positions is null )
			throw new ArgumentNullException( nameof( positions ) );
		if ( weights is null )
			throw new ArgumentNullException( nameof( weights ) );
		var result = new Vec3[positions.Count];
		for ( var i = 0; i < result.Length; i++ )
			result[i] = positions[i];
		foreach ( var morph in Morphs )
		{
			if ( morph is null || !weights.TryGetValue( morph.Name, out var w ) || w == 0f )
				continue;
			if ( morph.Deltas.Length != positions.Count )
				throw new ArgumentException( $"Morph '{morph.Name}' has {morph.Deltas.Length} deltas for a {positions.Count}-vertex mesh." );
			for ( var i = 0; i < result.Length; i++ )
				result[i] += morph.Deltas[i] * w;
		}
		return result;
	}

	/// <summary>Apply evaluated morph weights to a mesh in place: the mesh's positions move,
	/// everything else (faces, weights, the morphs themselves) is untouched.</summary>
	public void ApplyTo( PolyMesh mesh, IReadOnlyDictionary<string, float> weights )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		var moved = Apply( mesh.Positions, weights );
		for ( var i = 0; i < moved.Length; i++ )
			mesh.Positions[i] = moved[i];
	}

	/// <summary>What is wrong with this set against a skeleton, one sentence each. Empty when
	/// every driver will evaluate and every morph has a driver.</summary>
	public List<string> Problems( Skeleton skeleton )
	{
		var problems = new List<string>();
		if ( skeleton is null )
		{
			problems.Add( "No skeleton to check the drivers against." );
			return problems;
		}
		foreach ( var driver in Drivers )
		{
			if ( driver is null )
				continue;
			problems.AddRange( driver.Problems( skeleton ) );
			if ( !string.IsNullOrWhiteSpace( driver.Morph ) && Morphs.Find( m => m is not null && m.Name == driver.Morph ) is null )
				problems.Add( $"Driver for '{driver.Morph}' has no morph of that name in this set." );
		}
		foreach ( var morph in Morphs )
		{
			if ( morph is null )
				continue;
			if ( Drivers.Find( d => d is not null && d.Morph == morph.Name ) is null )
				problems.Add( $"Morph '{morph.Name}' has no driver, so it never fires on its own." );
		}
		return problems;
	}
}
