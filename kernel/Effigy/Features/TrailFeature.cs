using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A visible trail along a chain of bones — the one particle system
/// Geppetto already simulates, made visible.
///
/// A `chain` builds bones along a path and then vanishes into bone
/// transforms: its particle positions (the joints) are never geometry.
/// This feature reads the chain's bones back out of the rig
/// (HeadWorld/TailWorld, in skeleton order) and builds a tapered tube
/// along them with StrandSolver.BuildTube, the same tube the hair
/// strands use. It is a rest-pose trail, not a simulated motion trail:
/// the kernel has no per-frame particle tick to sample, and ChainSpring
/// baking is clip-time, not feature-time, so a feature cannot honestly
/// sample a moving rope. What it can do, and does, is turn the chain
/// into a body a modeller can see, move, and export.
///
/// The `chain` verb's bake=trail option creates exactly this feature,
/// so `chain Tail along=... count=6 bake=trail` and
/// `add Trail chain=Tail` are the same trail by two spellings.
///
/// Note on ends: with the default TipRatio 0 the tip end closes as a
/// triangle fan and only the root end ring stays open (Sides open edges);
/// with TipRatio > 0 both end rings stay open. A topology check reporting
/// those open edges is the tube's construction, not a broken trail — a
/// trail is a tube, not a closed solid.
/// </summary>
public sealed class TrailFeature : Feature
{
	public override string TypeName => "Trail";

	/// <summary>Chain name: bones named Chain_00, Chain_01, … (RigBuild.Chain's
	/// naming), in skeleton order. A bone named exactly Chain also counts.</summary>
	public readonly StringParam Chain = new( "Chain", "" );
	public readonly FloatParam Radius = new( "Radius", 0.3f, 0.01f, 1000f, "u" );
	public readonly FloatParam TipRatio = new( "Tip ratio", 0f, 0f, 1f );
	public readonly IntParam Sides = new( "Sides", 8, 3, 128 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Chain, Radius, TipRatio, Sides, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var chain = Chain.Value;
		if ( string.IsNullOrWhiteSpace( chain ) )
			Fail( "Trail needs a chain.", "Chain is empty, so there are no bones to follow.",
				"Name the chain with chain=Tail" );

		var rig = ctx.Rig;
		if ( rig is null || rig.Count == 0 )
			Fail( $"Trail '{chain}' has no rig to read.",
				"A trail follows chain bones, and this document has no skeleton.",
				"Build a chain first", "Check the chain name" );

		var points = new List<Vec3>();
		for ( var i = 0; i < rig.Count; i++ )
		{
			var bone = rig.Bones[i];
			if ( !string.Equals( bone.Name, chain, StringComparison.OrdinalIgnoreCase )
				&& !bone.Name.StartsWith( chain + "_", StringComparison.OrdinalIgnoreCase ) )
				continue;
			if ( points.Count == 0 )
				points.Add( rig.HeadWorld( i ) );
			points.Add( rig.TailWorld( i ) );
		}

		if ( points.Count < 2 )
			Fail( $"Trail found no chain called '{chain}'.",
				"Chain bones are named Chain_00, Chain_01, …; nothing in the rig matches that name.",
				"Check the chain name", "Build the chain before the trail" );

		var mesh = StrandSolver.BuildTube( new[] { points }, Radius.Clamped, TipRatio.Clamped, Sides.Clamped, Material.Clamped );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? $"{chain} trail", mesh ) );
	}
}
