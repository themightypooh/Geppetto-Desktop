using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// First-person viewmodel presentation for a weapon (roadmap Lane L + §4 Talon
/// Plasma Pistol): take a weapon body into viewmodel space and carry the
/// viewmodel data the engine side needs, in the feature itself so a document
/// round-trips it.
///
///   add Viewmodel name=TalonView weapon=Talon fov=65 depthscale=0.35
///     offset=0.2,-0.1,0 preset=OliveGlove dominant=hand_R offhand=hand_L
///
/// What the feature DOES: clones the weapon (and picked hands, if any) into a
/// new body, compresses its depth by DepthScale around the weapon centroid
/// (the roadmap's 0.35 guard against the barrel clipping through walls), then
/// shifts it by Offset. Depth is measured along ViewAxis, which defaults to Y
/// — the front view's depth axis in Effigy, the axis a weapon points along.
///
/// What it does NOT do, honestly: FOV is a camera property, not geometry, so
/// the fov= value is carried, not applied — the cabinet's viewmodel_cam
/// preview and the s&box camera are what read it. Likewise the preset's
/// SlotLook colour and pixel-shader knobs live in
/// <see cref="ViewmodelRig"/> as data; applying them to a material is
/// <c>Code/PixelArmsStyle.ApplyTo</c>'s job, and the two-handed IK
/// (dominant/offhand space-switching, detach during mag_out/mag_in) is
/// RigAnimPlayerComponent's job — the dominant=/offhand=/detachrange=
/// parameters carry that data in the document until those callers read it.
/// A feature that silently claimed to have posed hands would be lying about
/// the thing the pistol demo most needs to be true.
///
/// The weapon body itself is never touched: the viewmodel is a presentation
/// copy, so deleting it cannot eat the world-model weapon.
/// </summary>
public sealed class ViewmodelFeature : Feature
{
	public override string TypeName => "Viewmodel";

	public readonly BodySelectionParam Weapon = new( "Weapon" );
	public readonly BodySelectionParam Hands = new( "Hands" );
	public readonly FloatParam Fov = new( "Fov", 65f, 55f, 75f, "deg" );
	public readonly Vec3Param Offset = new( "Offset", Vec3.Zero );
	public readonly FloatParam DepthScale = new( "Depth scale", 0.35f, 0.05f, 1f );
	public readonly ChoiceParam ViewAxis = new( "View axis", new[] { "Y", "X", "Z" } );
	public readonly ChoiceParam Preset = new( "Preset", new[]
		{ "OliveGlove", "WarmSkin", "DoomMarine", "QuakeRanger", "DuskCoat", "DivingSleeve", "custom" } );
	public readonly StringParam DominantHand = new( "Dominant hand", "hand_R" );
	public readonly StringParam Offhand = new( "Offhand", "hand_L" );
	public readonly StringParam DetachRange = new( "Detach range", "" );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Weapon, Hands, Fov, Offset, DepthScale, ViewAxis, Preset, DominantHand, Offhand, DetachRange };

	protected override void Execute( FeatureContext ctx )
	{
		var weapons = RequireBodies( ctx, Weapon );
		var weapon = weapons[0];
		if ( weapon.Mesh is null || weapon.Mesh.VertexCount == 0 )
			Fail( "Viewmodel's weapon has no mesh.",
				"The picked weapon body is empty.",
				"Build the weapon first, then add the viewmodel" );

		var centroid = Centroid( weapon.Mesh );
		var view = Transform( weapon.Mesh, centroid, ViewAxis.Value, DepthScale.Clamped, Offset.Value );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "Viewmodel", view ) );

		var hands = ctx.Bodies.Where( b => Hands.BodyIds.Contains( b.Id ) ).ToList();
		foreach ( var hand in hands )
		{
			if ( hand.Mesh is null || hand.Mesh.VertexCount == 0 ) continue;
			var moved = Transform( hand.Mesh, centroid, ViewAxis.Value, DepthScale.Clamped, Offset.Value );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{Name ?? "Viewmodel"} hands", moved ) );
		}

		if ( Preset.Value != "custom" && !ViewmodelRig.TryPreset( Preset.Value, out _ ) )
			Warn( $"Viewmodel preset '{Preset.Value}' is not a known preset",
				"The preset name does not match a ViewmodelRig preset, so the engine side will fall back to its own look.",
				"Pick OliveGlove, WarmSkin, DoomMarine, QuakeRanger, DuskCoat or DivingSleeve", "Or use preset=custom" );
	}

	static Vec3 Centroid( PolyMesh mesh )
	{
		var sum = Vec3.Zero;
		foreach ( var p in mesh.Positions ) sum += p;
		return mesh.Positions.Count == 0 ? Vec3.Zero : sum / mesh.Positions.Count;
	}

	static PolyMesh Transform( PolyMesh mesh, Vec3 centroid, string axis, float depthScale, Vec3 offset )
	{
		var copy = mesh.Clone();
		for ( var i = 0; i < copy.Positions.Count; i++ )
		{
			var d = copy.Positions[i] - centroid;
			d = axis switch
			{
				"X" => new Vec3( d.x * depthScale, d.y, d.z ),
				"Z" => new Vec3( d.x, d.y, d.z * depthScale ),
				_ => new Vec3( d.x, d.y * depthScale, d.z ),
			};
			copy.Positions[i] = centroid + d + offset;
		}
		return copy;
	}
}
