using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Script: <c>add Character name=Hero preset=citizen_male height=72 muscularity=0.65
/// body_fat=0.15 age=35 shoulder_width=0.58 jaw_width=0.62 nose_bridge=0.45 brow_ridge=0.55
/// eyes=hazel skin_tone=olive</c>.
///
/// A parametric base character (spec 4.1), not a primitive: <see cref="CharacterDna"/>
/// builds a clean-quad, pre-rigged, pre-UV'd body for the preset, the DNA sliders deform
/// it along anatomical masks, and this feature installs the skeleton as the studio rig
/// (see <see cref="CharacterDna.InstallRig"/>) and binds the mesh to it, so the result
/// animates. The body is one <see cref="Body"/> named after the feature.
///
/// HOW THE VERB REACHES HERE. No dispatch edit: <c>BuildScript.Create</c> discovers every
/// <see cref="Feature"/> subclass by class name, so <c>add Character</c> finds this class
/// (bare name "Character") and <c>Assign</c> maps each <c>key=value</c> pair onto a
/// parameter by its normalised label.
///
/// THE RIG RULE. A document keeps one rig. If the studio already has a rig with different
/// bones, this feature refuses instead of replacing it — the same guard an FBX import
/// uses. Rebuilding with the same preset re-applies the scaled pose instead of failing:
/// the feature's own rig is recognised by its bone names, in order.
///
/// WHAT IT DOES NOT DO. No eyes (task 51, multipart eye assembly), no mouth kit (task 54),
/// no FACS morphs (task 53) — age is blended into the mesh, not delivered as a morph.
/// Skin tone only picks the skin look and its tint; the skin synthesiser (task 52) is
/// separate. Iris colour is recorded on the build (<see cref="CharacterDnaOptions.Eyes"/>)
/// for the eye task, which reads the eye-centre landmarks.
///
/// ADULT SEX ANATOMY. On adult (Age >= 18) known-sex citizens the build carries real
/// geometry: citizen_male gets penis + scrotum (<c>genital_size</c> scales them),
/// citizen_female gets breasts + vulva/labia (<c>bust</c> scales the breasts), both
/// sexes get buttocks with a gluteal cleft. Neutral archetypes and under-18 builds
/// get none of it — see <see cref="CharacterDna"/>.
/// </summary>
public sealed class CharacterFeature : Feature
{
	public override string TypeName => "Character";

	public readonly ChoiceParam Preset = new( "Preset", CharacterDna.Presets );
	public readonly FloatParam Height = new( "Height", 72f, 12f, 120f, unit: "u" );
	public readonly FloatParam Muscularity = new( "Muscularity", 0.5f, 0f, 1f );
	public readonly FloatParam BodyFat = new( "Body fat", 0.15f, 0f, 1f );
	public readonly FloatParam Bust = new( "Bust", 0.5f, 0f, 1f );
	public readonly FloatParam GenitalSize = new( "Genital size", 0.5f, 0f, 1f );
	public readonly FloatParam Age = new( "Age", 35f, 0f, 120f );
	public readonly FloatParam ShoulderWidth = new( "Shoulder width", 0.5f, 0f, 1f );
	public readonly FloatParam ChestDepth = new( "Chest depth", 0.5f, 0f, 1f );
	public readonly FloatParam WaistWidth = new( "Waist width", 0.5f, 0f, 1f );
	public readonly FloatParam HipWidth = new( "Hip width", 0.5f, 0f, 1f );
	public readonly FloatParam ArmLength = new( "Arm length", 1f, 0.5f, 1.6f );
	public readonly FloatParam LegLength = new( "Leg length", 1f, 0.5f, 1.6f );
	public readonly FloatParam HandScale = new( "Hand scale", 1f, 0.5f, 1.6f );
	public readonly FloatParam FootScale = new( "Foot scale", 1f, 0.5f, 1.6f );
	public readonly FloatParam PostureSlouch = new( "Posture slouch", 0f, 0f, 1f );
	public readonly BoolParam Digitigrade = new( "Digitigrade", false );
	public readonly FloatParam HeadScale = new( "Head scale", 1f, 0.5f, 1.6f );
	public readonly FloatParam CraniumVault = new( "Cranium vault", 0.5f, 0f, 1f );
	public readonly FloatParam JawWidth = new( "Jaw width", 0.5f, 0f, 1f );
	public readonly FloatParam JawAngle = new( "Jaw angle", 0.5f, 0f, 1f );
	public readonly FloatParam ChinProjection = new( "Chin projection", 0.5f, 0f, 1f );
	public readonly FloatParam ChinCleft = new( "Chin cleft", 0f, 0f, 1f );
	public readonly FloatParam CheekboneProminence = new( "Cheekbone prominence", 0.5f, 0f, 1f );
	public readonly FloatParam CheekGauntness = new( "Cheek gauntness", 0.5f, 0f, 1f );
	public readonly FloatParam BrowRidge = new( "Brow ridge", 0.5f, 0f, 1f );
	public readonly FloatParam EyeSpacing = new( "Eye spacing", 0.5f, 0f, 1f );
	public readonly FloatParam EyeSize = new( "Eye size", 0.5f, 0f, 1f );
	public readonly FloatParam EyeTilt = new( "Eye tilt", 0.5f, 0f, 1f );
	public readonly FloatParam NoseLength = new( "Nose length", 0.5f, 0f, 1f );
	public readonly FloatParam NoseWidth = new( "Nose width", 0.5f, 0f, 1f );
	public readonly FloatParam NoseBridgeHump = new( "Nose bridge hump", 0.5f, 0f, 1f );
	public readonly FloatParam NoseTipTurn = new( "Nose tip turn", 0.5f, 0f, 1f );
	public readonly FloatParam MouthWidth = new( "Mouth width", 0.5f, 0f, 1f );
	public readonly FloatParam UpperLipFullness = new( "Upper lip fullness", 0.5f, 0f, 1f );
	public readonly FloatParam LowerLipFullness = new( "Lower lip fullness", 0.5f, 0f, 1f );
	public readonly FloatParam EarSize = new( "Ear size", 0.5f, 0f, 1f );
	public readonly FloatParam EarPoint = new( "Ear point", 0f, 0f, 1f );
	public readonly StringParam Eyes = new( "Eyes", "hazel" );
	public readonly ChoiceParam SkinTone = new( "Skin tone", new[] { "fair", "olive", "tan", "brown", "deep" }, 1 );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Preset, Height, Muscularity, BodyFat, Bust, GenitalSize, Age,
		ShoulderWidth, ChestDepth, WaistWidth, HipWidth,
		ArmLength, LegLength, HandScale, FootScale, PostureSlouch, Digitigrade,
		HeadScale, CraniumVault, JawWidth, JawAngle, ChinProjection, ChinCleft,
		CheekboneProminence, CheekGauntness, BrowRidge, EyeSpacing, EyeSize, EyeTilt,
		NoseLength, NoseWidth, NoseBridgeHump, NoseTipTurn,
		MouthWidth, UpperLipFullness, LowerLipFullness, EarSize, EarPoint,
		Eyes, SkinTone,
	};

	/// <summary>The head-and-face sliders, folded behind Advanced: they are answered once and
	/// then left alone, while preset/height/muscularity/age stay in front.</summary>
	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[]
	{
		HeadScale, CraniumVault, JawWidth, JawAngle, ChinProjection, ChinCleft,
		CheekboneProminence, CheekGauntness, BrowRidge, EyeSpacing, EyeSize, EyeTilt,
		NoseLength, NoseWidth, NoseBridgeHump, NoseTipTurn,
		MouthWidth, UpperLipFullness, LowerLipFullness, EarSize, EarPoint,
	};

	/// <summary>The build from the last Execute, for tests. Empty until Execute runs.</summary>
	public CharacterBuild LastBuild => _build;

	CharacterBuild _build = null!;

	static Vec3 SkinToneColor( string tone ) => tone.Trim().ToLowerInvariant() switch
	{
		"fair" => new Vec3( 0.91f, 0.77f, 0.66f ),
		"olive" => new Vec3( 0.79f, 0.63f, 0.42f ),
		"tan" => new Vec3( 0.66f, 0.48f, 0.31f ),
		"brown" => new Vec3( 0.48f, 0.32f, 0.19f ),
		"deep" => new Vec3( 0.29f, 0.18f, 0.11f ),
		_ => new Vec3( 0.79f, 0.63f, 0.42f ),
	};

	protected override void Execute( FeatureContext ctx )
	{
		var options = new CharacterDnaOptions
		{
			Preset = Preset.Value,
			Name = Name ?? "Character",
			Height = Height.Value,
			Muscularity = Muscularity.Value,
			BodyFat = BodyFat.Value,
			Bust = Bust.Value,
			GenitalSize = GenitalSize.Value,
			Age = Age.Value,
			ShoulderWidth = ShoulderWidth.Value,
			ChestDepth = ChestDepth.Value,
			WaistWidth = WaistWidth.Value,
			HipWidth = HipWidth.Value,
			ArmLength = ArmLength.Value,
			LegLength = LegLength.Value,
			HandScale = HandScale.Value,
			FootScale = FootScale.Value,
			PostureSlouch = PostureSlouch.Value,
			Digitigrade = Digitigrade.Value,
			HeadScale = HeadScale.Value,
			CraniumVault = CraniumVault.Value,
			JawWidth = JawWidth.Value,
			JawAngle = JawAngle.Value,
			ChinProjection = ChinProjection.Value,
			ChinCleft = ChinCleft.Value,
			CheekboneProminence = CheekboneProminence.Value,
			CheekGauntness = CheekGauntness.Value,
			BrowRidge = BrowRidge.Value,
			EyeSpacing = EyeSpacing.Value,
			EyeSize = EyeSize.Value,
			EyeTilt = EyeTilt.Value,
			NoseLength = NoseLength.Value,
			NoseWidth = NoseWidth.Value,
			NoseBridgeHump = NoseBridgeHump.Value,
			NoseTipTurn = NoseTipTurn.Value,
			MouthWidth = MouthWidth.Value,
			UpperLipFullness = UpperLipFullness.Value,
			LowerLipFullness = LowerLipFullness.Value,
			EarSize = EarSize.Value,
			EarPoint = EarPoint.Value,
			Eyes = Eyes.Value,
			SkinTone = SkinTone.Value,
		};

		// The raw script values, not the clamped ones: an out-of-range slider is a failure
		// with the range in the message, not a silent clamp to the nearest legal value.
		CharacterBuild build = null!;

		try
		{
			build = CharacterDna.Build( options );
		}
		catch ( Exception e ) when ( e is ArgumentException or InvalidOperationException )
		{
			Fail( e.Message, "The DNA sliders describe a character that cannot be built.",
				"Check the preset name, height and slider ranges, then rebuild." );
		}

		try
		{
			CharacterDna.InstallRig( ctx.Rig, build.Skeleton );
		}
		catch ( InvalidOperationException e )
		{
			Fail( e.Message, "This document already has a different rig.",
				"Delete those bones before adding a character, or add the character to an empty document." );
		}

		_build = build;

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "Character", build.Mesh ) );

		// Skin tone: the skin SSS preset, tinted to the chosen tone. Slot 0 is the body.
		var skinPreset = options.SkinTone.Trim().ToLowerInvariant() == "fair"
			? "skin_caucasian_sss" : "skin_burley";

		if ( SlotLook.TryPreset( skinPreset, out var look ) )
		{
			look.Color = SkinToneColor( options.SkinTone );
			ctx.SlotLooks[0] = look;
		}

		Info( $"{build.Template.Preset}: {build.Mesh.VertexCount} vertices, {build.Mesh.FaceCount} quad faces, "
			+ $"{build.Skeleton.Count} bones, {build.Measurements["height"]:0.0} in tall. "
			+ $"Eyes: {options.Eyes}; skin: {options.SkinTone}." );
	}
}
