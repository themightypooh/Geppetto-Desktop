using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Split Shape Key: turn one finished key into a left and a right key that add back up to it, the way
/// Valve's QC <c>flexpair</c> made right_/left_ flexes from one sculpt for the HL2 faces. Sculpt a lid
/// closer or a lip corner puller once, on both sides; split it; the face rig then pairs the two halves
/// under one slider with a balance (FaceRig, Face Poser in the chest).
///
/// Across the middle the two halves cross-fade over <see cref="Width"/> inches (smoothstep), so a key that
/// moves the middle of the lip (a pucker, a lip raise) does not crease at x = 0: at every vertex
/// left + right = the original, so both on is exactly the sculpt.
///
/// The character's left is +X by default: a face looking down −Y, Z up, has its left hand at +X. Pick
/// <see cref="LeftSide"/> +Y for a face looking down +X (Z up), and so on.
/// </summary>
public sealed class ShapeKeySplitFeature : Feature
{
	public override string TypeName => "Split Shape Key";

	public override GeometryKind Accepts => GeometryKind.Body;

	/// <summary>The finished key to split.</summary>
	public readonly StringParam Key = new( "Key name" );

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	/// <summary>How wide the cross-fade across the middle is, inches. 0 = a hard cut at x = 0.</summary>
	public readonly FloatParam Width = new( "Blend width", 0.6f, 0f, 20f, "in" );

	public readonly ChoiceParam LeftSide = new( "Left side", new[] { "+X", "-X", "+Y", "-Y" } );

	/// <summary>Keep the whole key as well as its halves (off: the halves replace it).</summary>
	public readonly BoolParam Keep = new( "Keep original", false );

	/// <summary>Suffixes for the two halves: <c>lid_closer</c> → <c>lid_closer_L</c>, <c>lid_closer_R</c>.</summary>
	public readonly StringParam LeftSuffix = new( "Left suffix", "_L" );
	public readonly StringParam RightSuffix = new( "Right suffix", "_R" );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Key, Bodies, Width, LeftSide, Keep, LeftSuffix, RightSuffix };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { LeftSuffix, RightSuffix };

	/// <summary>How much of a vertex at <paramref name="x"/> (along the left axis) goes to the left half.</summary>
	public static float LeftWeight( float x, float width )
	{
		if ( width <= 1e-6f )
			return x > 0 ? 1 : x < 0 ? 0 : 0.5f;

		var t = Math.Clamp( x / width + 0.5f, 0f, 1f );
		return t * t * (3 - 2 * t);
	}

	/// <summary>Split <paramref name="morph"/> on <paramref name="mesh"/> into (left, right).</summary>
	public static (MeshMorph Left, MeshMorph Right) Split( PolyMesh mesh, MeshMorph morph, float width, float leftSign, string leftName, string rightName, bool alongY = false )
	{
		var left = new Vec3[morph.Deltas.Length];
		var right = new Vec3[morph.Deltas.Length];

		for ( var i = 0; i < left.Length; i++ )
		{
			var w = LeftWeight( (alongY ? mesh.Positions[i].y : mesh.Positions[i].x) * leftSign, width );
			left[i] = morph.Deltas[i] * w;
			right[i] = morph.Deltas[i] * (1 - w);
		}

		return (new MeshMorph { Name = leftName, Deltas = left }, new MeshMorph { Name = rightName, Deltas = right });
	}

	protected override void Execute( FeatureContext ctx )
	{
		var key = Key.Value?.Trim() ?? "";

		if ( key.Length == 0 )
			Fail( "Split Shape Key needs a key", "It splits one finished shape key into left and right halves, and none is named.", "Set Key name, e.g. key=lid_closer" );

		var leftName = key + LeftSuffix.Value;
		var rightName = key + RightSuffix.Value;

		if ( leftName == rightName )
			Fail( "The two halves would have the same name", $"Left and right suffix are both '{LeftSuffix.Value}'.", "Give them different suffixes, e.g. _L and _R" );

		var sign = LeftSide.Index % 2 == 1 ? -1f : 1f;
		var alongY = LeftSide.Index >= 2;
		var found = 0;

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			var morph = mesh.ValidMorphs.FirstOrDefault( m => m.Name == key );

			if ( morph is null )
				continue;

			found++;
			var (l, r) = Split( mesh, morph, Width.Clamped, sign, leftName, rightName, alongY );
			mesh.Morphs.RemoveAll( m => m?.Name == leftName || m?.Name == rightName || (!Keep.Value && ReferenceEquals( m, morph )) );
			mesh.Morphs.Add( l );
			mesh.Morphs.Add( r );
		}

		if ( found == 0 )
		{
			var have = ctx.Bodies.Where( b => Bodies.Matches( b ) && b.Mesh is not null )
				.SelectMany( b => b.Mesh.ValidMorphs.Select( m => m.Name ) ).Distinct().Take( 12 ).ToList();
			Fail(
				$"No finished shape key '{key}' to split",
				have.Count == 0 ? "These bodies have no finished shape keys yet." : $"Keys there: {string.Join( ", ", have )}.",
				"Put Split Shape Key after the key's End Shape Key, and check the name" );
		}
	}
}
