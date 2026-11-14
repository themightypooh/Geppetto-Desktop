using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>FACS morphs as a feature: <c>add AutoFacs</c>.
///
/// What it does, per body: estimates face landmarks from the body's bounds and runs
/// <see cref="FaceRig.SynthesizeFacsMorphs"/> — the 32 flex controllers' keys, the 52
/// ARKit morphs, the 15 visemes, the fleshy lid-follow morphs and <c>lip_zip</c> — as
/// real <see cref="MeshMorph"/> deltas installed on the body's mesh. Landmarks are
/// estimated, not measured: on a real head they land on the face; on a plain block
/// they land on the block, and the morphs follow the block. The lid morphs carry the
/// 35% pitch follow and the corneal-bulge ride, so a closing lid never clips the
/// cornea; the fleshy eye-tracking itself is per-pose (see
/// <see cref="FaceRig.FleshyEyeTrackDeltas"/>) and lives in the viewer, not here.</summary>
public sealed class AutoFacsFeature : Feature
{
	public override string TypeName => "Auto FACS";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly BoolParam Arkit52 = new( "ARKit 52", true );
	public readonly BoolParam Visemes = new( "Visemes", true );
	public readonly BoolParam FleshyLids = new( "Fleshy lids", true );
	public readonly BoolParam LipZip = new( "Lip zip", true );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Arkit52, Visemes, FleshyLids, LipZip };

	/// <summary>Morphs installed by the last rebuild, across all bodies.</summary>
	public int LastMorphCount { get; private set; }

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );
		var meshes = targets.Where( b => b.Mesh is not null && b.Mesh.VertexCount > 0 && b.Mesh.FaceCount > 0 ).ToList();

		if ( meshes.Count == 0 )
			Fail( "Auto FACS needs a head to rig.", "The selected bodies have no mesh with faces.", "Add a head body first, or pick bodies=Head" );

		var options = new FaceRig.SynthesizeOptions
		{
			Arkit52 = Arkit52.Value,
			Visemes = Visemes.Value,
			FleshyLids = FleshyLids.Value,
			LipZip = LipZip.Value,
		};

		var count = 0;
		foreach ( var body in meshes )
		{
			var mesh = body.Mesh;
			var (min, max) = ModelMeasure.Bounds( mesh );
			var landmarks = FaceBounds.FromHeadBounds( min, max );
			FaceRig.SynthesizeFacsMorphs( mesh, landmarks, options );
			count += mesh.Morphs.Count;
		}

		LastMorphCount = count;
	}
}
