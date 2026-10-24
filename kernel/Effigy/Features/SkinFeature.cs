using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// A body over a stick figure typed as text (a skin modifier, or ZSpheres): joints with a thickness each, limbs between them, any
/// number of limbs at a joint. <see cref="SkeletonCage"/> makes the all-quad cage (hull joints,
/// untwisted rings), Catmull-Clark smooths it, and Fit pulls it onto the exact radii.
///
/// THE STICK FIGURE IS CHAINS, one per `;`, each a run of joints separated by `|`, each joint
/// `x,y,z radius`. Joints in the same place (within Weld) are one joint, so a branch is a chain
/// that starts where another has a joint:
///
///   0,0,40 5 | 0,0,62 6 | 0,0,74 4.5        spine and head
///   0,0,40 5 | 6,0,20 2.6 | 6,0,2 2          a leg out of the pelvis
///   0,0,62 6 | 20,0,60 2 | 31,0,58 1.5       an arm out of the chest
///
/// A joint is a point and a thickness, and the thickness may be elliptical: `x,y,z rx,rz`
/// (width, depth in the ring frame) or `x,y,z rx,rz,sq` (squareness 0 = ellipse, 1 = rounded
/// box), with an optional `up=x,y,z` word that rolls the ring so its +Y points at that
/// direction. A plain `x,y,z r` joint is a circle, exactly as before, and a graph with no
/// elliptical joints builds exactly as before. Note: Fit pulls onto round cones, so a graph
/// with elliptical joints skips Fit (reported by SkeletonCage) rather than being rounded.
///
/// The skin is one surface on one material slot. The result is an ordinary body: edit it, sculpt it, rig it. It has no UVs yet.
/// </summary>
public sealed class SkinFeature : Feature
{
	public override string TypeName => "Skin";

	public readonly StringParam Bones = new( "Bones", "0,0,0 3 | 0,0,20 3" );
	public readonly IntParam Sides = new( "Sides", 8, 4, 32 );
	public readonly IntParam Smooth = new( "Smooth levels", 2, 0, 4 );
	public readonly BoolParam Fit = new( "Fit to radii", true );
	public readonly FloatParam Fillet = new( "Fillet", -1f, -1f, unit: "u" );
	public readonly FloatParam Weld = new( "Weld", 0.05f, 0f, unit: "u" );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bones, Sides, Smooth, Fit, Fillet, Weld, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Fillet, Weld, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var graph = Parse( Bones.Value ?? "", Weld.Clamped );

		if ( graph.Edges.Count == 0 )
		{
			FailOn( "Bones",
				"There is no limb to build on",
				"A chain needs two joints or more, separated by |.",
				"Write a chain: 0,0,0 3 | 0,0,20 3" );
		}

		if ( Sides.Clamped % 2 != 0 )
		{
			FailOn( "Sides", Sides.Clamped + 1,
				"Sides must be even",
				$"A ring of {Sides.Clamped} cannot be capped with quads.",
				$"Set Sides to {Sides.Clamped + 1}" );
		}

		PolyMesh mesh;
		SkeletonCage.Report report;
		try
		{
			mesh = SkeletonCage.Build( graph, new SkeletonCage.Options
			{
				Sides = Sides.Clamped,
				SmoothLevels = Smooth.Clamped,
				Fit = Fit.Value,
				FitBlend = Fillet.Value,
			}, out report );
		}
		catch ( InvalidOperationException e )
		{
			FailOn( "Bones", "The stick figure cannot be skinned", e.Message, "Change the chain it names" );
			return;
		}

		foreach ( var f in mesh.Faces )
			f.Material = Material.Clamped;

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );

		Warning = $"{mesh.FaceCount:N0} quads over {graph.Edges.Count} limb(s)"
			+ (report.BranchJoints > 0 ? $", {report.BranchJoints} branching joint(s) (most limbs at one: {report.MaxBranches})" : "")
			+ ". It has no UVs yet: add a UV project (Unwrap) before baking.";
	}

	/// <summary>The chains as a stick figure. Bad text fails on the Bones field, naming the chain.</summary>
	internal static SkeletonCage.Graph Parse( string text, float weld )
	{
		var graph = new SkeletonCage.Graph();
		var chains = text.Split( ';' ).Select( s => s.Trim() ).Where( s => s.Length > 0 ).ToList();

		for ( var c = 0; c < chains.Count; c++ )
		{
			var prev = -1;
			foreach ( var joint in chains[c].Split( '|' ).Select( s => s.Trim() ) )
			{
				var words = joint.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );
				if ( words.Length < 2 || words.Length > 3
					|| !ModelMeasure.TryParseVec3( words[0], out var p ) )
				{
					FailOn( "Bones", $"Chain {c + 1} cannot be read",
						$"'{joint}' is not a joint; a joint is a point and a radius, like 0,0,10 2, or an elliptical joint, like 0,0,10 4,2.",
						"Fix that joint, or remove it" );
					return graph;
				}

				// Thickness: one number (a circle), or rx,rz / rx,rz,sq (an ellipse, optionally squared).
				Vec2 radii = Vec2.Zero;
				float sq = 0f;
				var bits = words[1].Split( ',' );
				bool ThickOk()
				{
					if ( bits.Length == 1 )
						return float.TryParse( bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r )
							&& (radii = new Vec2( r, r )).x > 0f;
					if ( bits.Length == 2 )
						return float.TryParse( bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rx )
							&& float.TryParse( bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rz )
							&& (radii = new Vec2( rx, rz )).x > 0f && radii.y > 0f;
					if ( bits.Length == 3 )
						return float.TryParse( bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rx )
							&& float.TryParse( bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rz )
							&& float.TryParse( bits[2], NumberStyles.Float, CultureInfo.InvariantCulture, out sq )
							&& (radii = new Vec2( rx, rz )).x > 0f && radii.y > 0f && sq >= 0f && sq <= 1f;
					return false;
				}
				if ( !ThickOk() )
				{
					FailOn( "Bones", $"Chain {c + 1} has a joint with no usable thickness",
						$"'{joint}' needs a radius above zero, or rx,rz[,sq] with both radii above zero and squareness 0..1.",
						"Give every joint a radius above zero" );
					return graph;
				}

				// Axial roll: an optional third word, up=x,y,z.
				var up = Vec3.Zero;
				if ( words.Length == 3 )
				{
					if ( !words[2].StartsWith( "up=", StringComparison.Ordinal )
						|| !ModelMeasure.TryParseVec3( words[2][3..], out up ) )
					{
						FailOn( "Bones", $"Chain {c + 1} cannot be read",
							$"'{joint}' has a third word that is not up=x,y,z.",
							"Drop the third word, or write it as up=x,y,z" );
						return graph;
					}
				}

				var n = graph.AddNode( p, radii, sq, up, MathF.Max( weld, 1e-4f ) );
				if ( prev >= 0 )
					graph.AddEdge( prev, n );
				prev = n;
			}
		}

		return graph;
	}
}
