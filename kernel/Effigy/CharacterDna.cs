using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Anatomical mask of one mesh vertex, for the DNA sliders. A vertex's region is decided
/// when its loft ring is built (torso stations, limb stations, head stations), and
/// <see cref="CharacterDna.ApplyDna"/> reads it back to inflate muscle, shape the face and
/// blend age — so a slider moves real anatomy, never a whole-mesh scale.
/// </summary>
public enum DnaRegion
{
	None, Torso, Pelvis, Chest, Abdomen, Back, Neck,
	Head, Cranium, Jaw, Chin, Cheek, Brow, Eye, Nose, Mouth, LipUpper, LipLower, Ear,
	Nasolabial, NeckCord,
	Deltoid, UpperArm, Forearm, Hand, Finger,
	Quadriceps, Hamstring, Calf, Foot,
	Shoulder, Hip, Tail, Wing, Fin, Snout, Beak, Belly,
	// Adult sex anatomy + fine limb segmentation. Appended, never inserted: the
	// values above must not move (tests and saved builds read them by number).
	// Sex anatomy is ADULT ONLY: BuildSexAnatomy builds it only when Age >= 18
	// and the preset is a known-sex citizen (citizen_male / citizen_female).
	Breast, Nipple, Penis, Glans, Scrotum, Vulva, Labia, Buttocks,
	Toe, Elbow, Knee, Ankle, Wrist,
	Thumb, IndexFinger, MiddleFinger, RingFinger, PinkyFinger,
}

/// <summary>
/// The 30+ DNA sliders of spec 4.1, plus preset, name, eyes and skin tone.
///
/// PROPORTION SLIDERS ARE 0..1, 0.5 IS THE TEMPLATE AVERAGE. The spec's example
/// (<c>shoulder_width=0.58 jaw_width=0.62 nose_bridge=0.45 brow_ridge=0.55</c>) only makes
/// sense that way: 0.58 is not 58% of the height and not 0.58 inches, it is "a bit wider
/// than average". Each 0..1 slider maps to a real dimension inside the builder, and the
/// template adds its own bias (a female citizen is narrower in the shoulder at the same
/// slider value than a male one). The exceptions are the plain physical ones:
/// <see cref="Height"/> is inches, <see cref="Age"/> is years, and
/// <see cref="ArmLength"/>, <see cref="LegLength"/>, <see cref="HandScale"/>,
/// <see cref="FootScale"/> and <see cref="HeadScale"/> are multipliers (1 = template).
/// </summary>
public sealed class CharacterDnaOptions
{
	public string Preset = "citizen_male";
	public string Name = "Character";
	public float Height = 72f;
	public float Muscularity = 0.5f;
	public float BodyFat = 0.15f;
	public float Age = 35f;
	/// <summary>Bust, 0..1 (0.5 = template average). Drives breast size on adult
	/// female citizens only; inert on every other preset.</summary>
	public float Bust = 0.5f;
	/// <summary>Genital size, 0..1 (0.5 = template average). Drives penis/scrotum
	/// size on adult male citizens only; inert on every other preset.</summary>
	public float GenitalSize = 0.5f;
	public float ShoulderWidth = 0.5f;
	public float ChestDepth = 0.5f;
	public float WaistWidth = 0.5f;
	public float HipWidth = 0.5f;
	public float ArmLength = 1f;
	public float LegLength = 1f;
	public float HandScale = 1f;
	public float FootScale = 1f;
	public float PostureSlouch = 0f;
	public bool Digitigrade = false;
	public float HeadScale = 1f;
	public float CraniumVault = 0.5f;
	public float JawWidth = 0.5f;
	public float JawAngle = 0.5f;
	public float ChinProjection = 0.5f;
	public float ChinCleft = 0f;
	public float CheekboneProminence = 0.5f;
	public float CheekGauntness = 0.5f;
	public float BrowRidge = 0.5f;
	public float EyeSpacing = 0.5f;
	public float EyeSize = 0.5f;
	public float EyeTilt = 0.5f;
	public float NoseLength = 0.5f;
	public float NoseWidth = 0.5f;
	public float NoseBridgeHump = 0.5f;
	public float NoseTipTurn = 0.5f;
	public float MouthWidth = 0.5f;
	public float UpperLipFullness = 0.5f;
	public float LowerLipFullness = 0.5f;
	public float EarSize = 0.5f;
	public float EarPoint = 0f;
	public string Eyes = "hazel";
	public string SkinTone = "olive";

	public void Validate()
	{
		if ( string.IsNullOrWhiteSpace( Preset ) )
			throw new ArgumentException( "A character needs a preset.", nameof( Preset ) );

		if ( !(Height > 12f) || Height > 120f )
			throw new ArgumentOutOfRangeException( nameof( Height ), $"height={Height}: a character is 12 to 120 inches tall." );

		foreach ( var (name, v) in new[]
		{
			("muscularity", Muscularity), ("body_fat", BodyFat), ("bust", Bust), ("genital_size", GenitalSize),
			("shoulder_width", ShoulderWidth), ("chest_depth", ChestDepth),
			("waist_width", WaistWidth), ("hip_width", HipWidth),
			("posture_slouch", PostureSlouch), ("cranium_vault", CraniumVault),
			("jaw_width", JawWidth), ("jaw_angle", JawAngle),
			("chin_projection", ChinProjection), ("chin_cleft", ChinCleft),
			("cheekbone_prominence", CheekboneProminence), ("cheek_gauntness", CheekGauntness),
			("brow_ridge", BrowRidge), ("eye_spacing", EyeSpacing), ("eye_size", EyeSize),
			("eye_tilt", EyeTilt), ("nose_length", NoseLength), ("nose_width", NoseWidth),
			("nose_bridge_hump", NoseBridgeHump), ("nose_tip_turn", NoseTipTurn),
			("mouth_width", MouthWidth), ("upper_lip_fullness", UpperLipFullness),
			("lower_lip_fullness", LowerLipFullness), ("ear_size", EarSize), ("ear_point", EarPoint),
		} )
			if ( !(v >= 0f) || !(v <= 1f) )
				throw new ArgumentOutOfRangeException( name, $"{name}={v}: a 0..1 DNA slider." );

		foreach ( var (name, v) in new[]
		{
			("arm_length", ArmLength), ("leg_length", LegLength),
			("hand_scale", HandScale), ("foot_scale", FootScale), ("head_scale", HeadScale),
		} )
			if ( !(v >= 0.5f) || !(v <= 1.6f) )
				throw new ArgumentOutOfRangeException( name, $"{name}={v}: a length multiplier, 0.5 to 1.6." );

		if ( !(Age >= 0f) || Age > 120f )
			throw new ArgumentOutOfRangeException( nameof( Age ), $"age={Age}: 0 to 120 years." );

		if ( SkinTone.Trim().ToLowerInvariant() is not ("fair" or "olive" or "tan" or "brown" or "deep") )
			throw new ArgumentException( $"No skin tone '{SkinTone}'. Tones: fair, olive, tan, brown, deep.", nameof( SkinTone ) );
	}
}

public enum CharacterKind { Humanoid, Quadruped, Fish, Avian }

/// <summary>
/// One archetype template: its proportions, its skeleton source and the bias each 0..1
/// slider gets on top of the user's value, so templates differ in the MESH, not just in
/// a scale factor. A template's <see cref="SkeletonNote"/> says where its bones come from,
/// because "not a rescaled citizen" is a claim the reader should be able to check.
/// </summary>
public sealed class CharacterTemplate
{
	public string Preset = "";
	public CharacterKind Kind;
	public float Heads = 7.5f;
	public string Style = "realistic";
	public string Legs = "digitigrade";
	public float ShoulderBias;
	public float ChestBias;
	public float WaistBias;
	public float HipBias;
	public float MuscularityBias;
	public float HeadScaleBias;
	public float EyeSizeBias;
	public float NoseLengthBias;
	public float Muzzle = 1f;
	public float TailLength = 1f;
	public float EarHeight = 1f;
	public float BodySlim = 1f;
	public bool Tail;
	public bool Claws;
	public string SkeletonNote = "";
	public string Description = "";
}

/// <summary>The finished character: mesh, skeleton, per-vertex regions, landmarks, measurements.</summary>
public sealed class CharacterBuild
{
	public PolyMesh Mesh = null!;
	public Skeleton Skeleton = null!;
	/// <summary>One <see cref="DnaRegion"/> per mesh vertex, parallel to <see cref="PolyMesh.Positions"/>.</summary>
	public int[] Regions = Array.Empty<int>();
	public Dictionary<string, Vec3> Landmarks = new();
	public Dictionary<string, float> Measurements = new();
	public CharacterTemplate Template = null!;
	public CharacterDnaOptions Options = null!;
	/// <summary>Eye centres, for the eye-assembly ticket that follows this one.</summary>
	public (Vec3 Left, Vec3 Right) EyeCenters;
	public string Notes = "";
}

/// <summary>
/// Parametric character DNA (spec 4.1): a clean-quad, pre-rigged, pre-UV'd base body per
/// archetype, with the DNA sliders driving real anatomical deformation.
///
/// COORDINATES. Inches, Z-up, ground at z 0, forward +X, left +Y — the kernel's frame and
/// <see cref="RigTemplates"/>'s. Every mesh is built AROUND its skeleton's joints (the torso
/// stations sit on the spine joints, the limb tubes follow the arm/leg joints), so the mesh
/// and the rig agree by construction instead of by a fitting pass afterwards.
///
/// SKINNING. <see cref="SkinBinder.BindSmooth"/> weights by distance to bone segments taken
/// from <c>HeadWorld</c>→<c>TailWorld</c>, but citizen's table carries no bone lengths
/// (Length defaults to 1), so its "segments" would be 1-inch stubs and the weights would
/// degrade to nearest-joint. <see cref="BindSkin"/> therefore builds its own segments from
/// joint to child-joint and runs the same inverse-distance-squared weighting
/// (<see cref="SkinBinder.DistanceToSegment"/>, cutoff 0.02 of the strongest, partition of
/// unity via <see cref="SkinWeights.Finish"/>), so every template gets true segment weights.
///
/// WHAT THIS IS NOT. It is a base body, not a finished character: no eyes (task 51), no
/// mouth kit (task 54), no FACS morphs (task 53) — age is blended straight into the mesh,
/// not delivered as a morph target. The head has edge loops at brow / eye / nose / mouth /
/// chin heights and the facial features are real displacements, not texture.
/// </summary>
public static class CharacterDna
{
	public static readonly string[] Presets =
	{
		"citizen_male", "citizen_female", "realistic_human", "stylized_hero", "anime_base",
		"heavy_brute", "creature_biped", "quadruped_canine", "quadruped_feline",
		"ichthyoid_fish", "avian_bird",
	};

	/// <summary>Citizen's nominal height in inches: eyes measured at 63.45, crown ~68.5-69.
/// <see cref="Build"/> re-measures the finished mesh and corrects the scale, so this only
/// sets the first approximation.</summary>
	public const float CitizenNominalHeight = 68.9f;

	static float Clamp01( float v ) => Math.Clamp( v, 0f, 1f );
	static float Lerp( float a, float b, float t ) => a + (b - a) * t;
	static float Eff( float v, float bias ) => Clamp01( v + bias );

	public static string NormalisePreset( string preset ) =>
		(preset ?? "").Trim().ToLowerInvariant().Replace( ' ', '_' ).Replace( '-', '_' );

	public static CharacterTemplate GetTemplate( string preset )
	{
		var key = NormalisePreset( preset );

		return key switch
		{
			"citizen_male" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Humanoid, Heads = 7.5f,
				ShoulderBias = 0.12f, ChestBias = 0.05f, WaistBias = 0f, HipBias = -0.05f,
				Description = "s&box Citizen man: exact Citizen bone names and hierarchy.",
				SkeletonNote = "CitizenSkeleton.Build(): the engine-exact 95-bone citizen table.",
			},
			"citizen_female" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Humanoid, Heads = 7.5f,
				ShoulderBias = -0.12f, ChestBias = -0.05f, WaistBias = -0.12f, HipBias = 0.15f,
				HeadScaleBias = -0.04f,
				Description = "s&box Citizen woman: same 95 Citizen bones, female mesh proportions.",
				SkeletonNote = "CitizenSkeleton.Build(): citizen ships one skeleton; the sexes differ in the DNA mesh, proportions and soft-tissue masks, not in bone names.",
			},
			"realistic_human" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Humanoid, Heads = 8f, Style = "heroic",
				Description = "8-head heroic human: full facial loop topology (brow/eye/nose/mouth/chin loops), 5-finger hands.",
				SkeletonNote = "RigTemplates.Biped(8, heroic): human REF bone names, 8-head layout, 5 fingers incl. pinky.",
			},
			"stylized_hero" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Humanoid, Heads = 6f, Style = "stylized",
				ShoulderBias = 0.1f, MuscularityBias = 0.1f,
				Description = "Clean-plane stylized hero (Overwatch / Arcane / Fortnite proportions).",
				SkeletonNote = "RigTemplates.Biped(6, stylized): 6-head layout, human bone names.",
			},
			"anime_base" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Humanoid, Heads = 6f, Style = "stylized",
				EyeSizeBias = 0.3f, NoseLengthBias = -0.2f, HeadScaleBias = 0.08f,
				Description = "Anime base: large eye sockets, simplified nose plane, cel-shading normal flow (big smooth head planes).",
				SkeletonNote = "RigTemplates.Biped(6, stylized): 6-head layout; the anime difference is the mesh (enlarged eye loops, flattened nose), not a new bone set.",
			},
			"heavy_brute" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Humanoid, Heads = 7f, Style = "heroic",
				ShoulderBias = 0.28f, ChestBias = 0.2f, WaistBias = 0.15f, HipBias = 0.15f,
				MuscularityBias = 0.2f, HeadScaleBias = -0.05f,
				Description = "Heavy brute: broad trapezius/ribcage, thick digits.",
				SkeletonNote = "RigTemplates.Biped(7, heroic): 7-head layout, human bone names; digits thickened in the mesh.",
			},
			"creature_biped" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Humanoid, Heads = 7f, Style = "realistic",
				Tail = true, Claws = true,
				Description = "Creature biped: digitigrade hind legs, elongated torso/neck, clawed hands, tail chain.",
				SkeletonNote = "RigTemplates.Biped(7, realistic) with digitigrade ankle/ball re-placement and an added 5-bone tail chain under pelvis.",
			},
			"quadruped_canine" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Quadruped, Legs = "digitigrade",
				Muzzle = 1f, TailLength = 1f, EarHeight = 1f,
				Description = "Quadruped canine: scapula-humerus-radius-carpus forelimb, pelvis-femur-tibia-metatarsus hindlimb, tail chain.",
				SkeletonNote = "RigTemplates.Quadruped(scale, digitigrade): scapula_L/arm_upper_L (humerus)/arm_lower_L (radius)/hand_L (carpus) and leg_upper_L (femur)/leg_lower_L (tibia)/ankle_L (metatarsus)/ball_L, 5-bone tail.",
			},
			"quadruped_feline" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Quadruped, Legs = "digitigrade",
				Muzzle = 0.6f, TailLength = 1.4f, EarHeight = 0.8f, BodySlim = 0.88f,
				Description = "Quadruped feline: same quadruped rig family as the canine, slimmer body, shorter muzzle, longer tail.",
				SkeletonNote = "RigTemplates.Quadruped(scale, digitigrade): the canine/feline difference is the body plan (slim, muzzle, ears, tail), not a new bone set.",
			},
			"ichthyoid_fish" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Fish,
				Description = "Ichthyoid fish: fusiform body, spine chain, dorsal/pectoral/pelvic/caudal fins.",
				SkeletonNote = "RigTemplates.BuildTemplate(fish): root + spine chain, head/jaw/eyes/opercles, caudal and dorsal fin bones.",
			},
			"avian_bird" => new CharacterTemplate
			{
				Preset = key, Kind = CharacterKind.Avian,
				Description = "Avian bird: standing body, neck/head/beak, three-segment folding wings, talons, tail fan.",
				SkeletonNote = "RigTemplates.BuildTemplate(avian): pelvis/spine/neck/head/beak, wing_shoulder/elbow/wrist + feather chains, leg/talon/toe, tail fan.",
			},
			_ => throw new ArgumentException(
				$"No character preset '{preset}'. Presets: {string.Join( ", ", Presets )}.", nameof( preset ) ),
		};
	}

	/// <summary>Build the skeleton for a template at the requested height.</summary>
	public static Skeleton BuildSkeleton( CharacterTemplate t, CharacterDnaOptions o )
	{
		Skeleton s;

		switch ( t.Kind )
		{
			case CharacterKind.Humanoid when t.Preset.StartsWith( "citizen", StringComparison.Ordinal ):
				s = CitizenSkeleton.Build();
				ScaleSkeleton( s, o.Height / CitizenNominalHeight );
				break;
			case CharacterKind.Humanoid:
				s = RigTemplates.Biped( t.Heads, t.Style, o.Height, fingers: true, twist: false );
				break;
			case CharacterKind.Quadruped:
				s = RigTemplates.Quadruped( o.Height / 24f, t.Legs );
				break;
			case CharacterKind.Fish:
				s = RigTemplates.BuildTemplate( "fish",
					new RigTemplates.TemplateOptions { Scale = o.Height / 24f, SpineCount = 6, DorsalCount = 3 } );
				break;
			case CharacterKind.Avian:
				s = RigTemplates.BuildTemplate( "avian",
					new RigTemplates.TemplateOptions { Scale = o.Height / 36f } );
				break;
			default:
				throw new ArgumentException( $"No skeleton builder for kind {t.Kind}." );
		}

		if ( t.Kind == CharacterKind.Humanoid && (t.Preset == "creature_biped" || o.Digitigrade) )
			MakeDigitigrade( s, o );

		return s;
	}

	/// <summary>Uniform-scale a skeleton about the ground origin: bone local origins and
	/// lengths scale, orientations do not. Used for the citizen table (no height parameter)
	/// and for the final height correction after measuring the finished mesh.</summary>
	public static void ScaleSkeleton( Skeleton s, float factor )
	{
		if ( !(factor > 0f) || float.IsInfinity( factor ) )
			throw new ArgumentOutOfRangeException( nameof( factor ), $"scale={factor}: it has to be positive." );

		foreach ( var b in s.Bones )
		{
			b.Local = new Xform( b.Local.X, b.Local.Y, b.Local.Z, b.Local.Origin * factor );
			b.Length *= factor;
		}
	}

	/// <summary>Lengthen/shorten one chain segment by scaling the bone's local origin about
	/// its parent: the segment gets longer, its direction and every orientation stay put,
	/// and the children ride along. This is how the arm/leg length sliders move the rig
	/// instead of only the mesh — at the default 1.0 nothing is touched, so the citizen
	/// bind pose stays engine-exact unless the user explicitly asks for longer limbs.</summary>
	static void ScaleSegment( Skeleton s, string bone, float factor )
	{
		if ( MathF.Abs( factor - 1f ) < 1e-6f )
			return;

		var i = s.IndexOf( bone );

		if ( i < 0 )
			return;

		var b = s.Bones[i];
		b.Local = new Xform( b.Local.X, b.Local.Y, b.Local.Z, b.Local.Origin * factor );
	}

	static void ScaleLimbs( Skeleton s, CharacterDnaOptions o )
	{
		foreach ( var side in new[] { "L", "R" } )
		{
			ScaleSegment( s, $"arm_lower_{side}", o.ArmLength );
			ScaleSegment( s, $"hand_{side}", o.ArmLength );
			ScaleSegment( s, $"leg_lower_{side}", o.LegLength );
			ScaleSegment( s, $"ankle_{side}", o.LegLength );
		}
	}

	/// <summary>Digitigrade stance for the creature biped: raise the ankle and shift it back
	/// so the foot stands on the ball/toes with the heel (hock) high, and re-place the ball
	/// and toe on the ground. Positions are derived from the built skeleton, so the numbers
	/// scale with the character instead of being baked-in inches.</summary>
	static void MakeDigitigrade( Skeleton s, CharacterDnaOptions o )
	{
		foreach ( var side in new[] { "L", "R" } )
		{
			var ankle = s.IndexOf( $"ankle_{side}" );
			var ball = s.IndexOf( $"ball_{side}" );

			if ( ankle < 0 || ball < 0 )
				continue;

			var knee = s.HeadWorld( s.IndexOf( $"leg_lower_{side}" ) );
			var ankleHead = s.HeadWorld( ankle );
			var ballHead = s.HeadWorld( ball );
			var shin = Math.Max( (ankleHead - knee).Length, 1f );

			var newAnkle = ankleHead + new Vec3( -0.06f * shin, 0, 0.15f * shin );
			var newBall = new Vec3( ballHead.x + 0.02f * o.Height, ballHead.y, Math.Max( 0.4f, ballHead.z ) );
			var newToe = new Vec3( newBall.x + 0.03f * o.Height, newBall.y, 0f );

			s.SetHeadTail( ankle, newAnkle, newBall );
			s.SetHeadTail( ball, newBall, newToe );
		}

		// Tail chain under the pelvis, five bones running back (-X) and slightly down.
		var pelvis = s.IndexOf( "pelvis" );

		if ( pelvis < 0 )
			return;

		var root = s.HeadWorld( pelvis );
		var parent = pelvis;
		var at = root + new Vec3( -0.02f * o.Height, 0, 0.01f * o.Height );

		for ( var i = 0; i < 5; i++ )
		{
			var next = at + new Vec3( -0.055f * o.Height, 0, -0.012f * o.Height * (i + 1) / 5f );
			parent = s.AddBoneFromPoints( $"tail_{i:00}", parent, at, next );
			at = next;
		}
	}

	/// <summary>Install a built skeleton into a studio rig (the <see cref="FeatureContext.Rig"/>
	/// instance, which IS the studio's rig object). Re-applying the same template re-scales
	/// the pose; a rig with different bones is refused rather than silently replaced — the
	/// same guard <c>ImportFeature.InstallSkeleton</c> uses for an imported file.</summary>
	public static void InstallRig( Skeleton studioRig, Skeleton source )
	{
		if ( studioRig is null )
			throw new ArgumentNullException( nameof( studioRig ) );

		if ( source is null || source.Count == 0 )
			throw new ArgumentException( "The character build has no skeleton to install." );

		if ( studioRig.Count > 0 )
		{
			var same = studioRig.Count == source.Count
				&& studioRig.Bones.Zip( source.Bones, ( a, b ) => a.Name == b.Name ).All( x => x );

			if ( !same )
				throw new InvalidOperationException(
					$"This document already has a rig ({studioRig.Count} bones, first '{studioRig.Bones[0].Name}'), "
					+ $"not the character's ({source.Count} bones, first '{source.Bones[0].Name}'). "
					+ "Delete those bones before adding a character, or add the character to an empty document." );
		}

		studioRig.Bones.Clear();

		foreach ( var b in source.Bones )
			studioRig.Bones.Add( b.Clone() );

		studioRig.HipsBone = studioRig.IndexOf( "pelvis" ) >= 0 ? "pelvis" : studioRig.HipsBone;
		studioRig.RootBone = studioRig.IndexOf( "pelvis" ) >= 0 ? "pelvis"
			: studioRig.IndexOf( "root" ) >= 0 ? "root" : studioRig.RootBone;
	}

	/// <summary>Skin weights from joint-to-child-joint segments (see the class notes): a bone
	/// owns the segment from its head to each child's head, leaves get a short stub along
	/// their own +Y. Same inverse-distance-squared weighting, cutoff and partition of unity
	/// as <see cref="SkinBinder.BindSmooth"/>.</summary>
	public static SkinWeights BindSkin( PolyMesh mesh, Skeleton skeleton )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( skeleton is null || skeleton.Count == 0 )
			throw new ArgumentException( "Skinning needs a skeleton with bones.", nameof( skeleton ) );

		var segments = new List<(Vec3 Head, Vec3 Tail, int Bone)>();

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			var children = skeleton.Children( i ).ToList();

			if ( children.Count == 0 )
			{
				var w = skeleton.WorldBind( i );
				segments.Add( (skeleton.HeadWorld( i ), w.TransformPoint( new Vec3( 0, 2f, 0 ) ), i) );
				continue;
			}

			foreach ( var c in children )
				segments.Add( (skeleton.HeadWorld( i ), skeleton.HeadWorld( c ), i) );
		}

		var weights = new SkinWeights();

		foreach ( var p in mesh.Positions )
		{
			var distances = new float[segments.Count];
			var nearest = float.MaxValue;

			for ( var b = 0; b < segments.Count; b++ )
			{
				distances[b] = SkinBinder.DistanceToSegment( p, segments[b].Head, segments[b].Tail );
				nearest = MathF.Min( nearest, distances[b] );
			}

			if ( nearest < 1e-6f )
			{
				var onBone = Array.IndexOf( distances, nearest );
				weights.Vertices.Add( new[] { new BoneWeight( onBone < 0 ? 0 : segments[onBone].Bone, 1f ) } );
				continue;
			}

			var acc = new Dictionary<int, float>();
			var strongest = 0f;

			for ( var b = 0; b < segments.Count; b++ )
			{
				var influence = 1f / (distances[b] * distances[b]);
				acc[segments[b].Bone] = acc.TryGetValue( segments[b].Bone, out var had ) ? had + influence : influence;
				strongest = MathF.Max( strongest, influence );
			}

			var floor = strongest * 0.02f;

			foreach ( var b in new List<int>( acc.Keys ) )
				if ( acc[b] < floor )
					acc.Remove( b );

			weights.Vertices.Add( SkinWeights.Finish( acc ) );
		}

		return weights;
	}

	// --- mesh builder ---------------------------------------------------------------------

	/// <summary>One ring of a loft: an ellipse in the (U,V) plane around Center.</summary>
	sealed class Ring
	{
		public Vec3 Center;
		public Vec3 U;
		public Vec3 V;
		public float Ru;
		public float Rv;
		public DnaRegion Region;
		public float VCoord;
	}

	/// <summary>
	/// Quad-loft builder. Every part is a stack of rings joined by quads; the first and last
	/// rings are tiny (0.02x) instead of pole vertices, so a closed part has no triangle fans
	/// and no n-gon caps — the whole mesh is quads, which is what the spec's "clean-quad"
	/// means here and what Catmull-Clark wants. Winding follows <see cref="Primitives"/>:
	/// rings run along +axis, angles increase in the (U,V) plane with U x V = axis, faces
	/// (ring0[j], ring0[j+1], ring1[j+1], ring1[j]) point outward.
	///
	/// UVs are per face corner (a ring vertex is shared by faces that want different U at the
	/// wrap seam), laid out in atlas strips: torso [0, .38], head [.38, .58], arms [.58, .72],
	/// legs [.72, .86], hands/feet/tail/fins [.86, 1]. V runs 0..1 along each part.
	/// </summary>
	sealed class MeshBuilder
	{
		public List<Vec3> P = new();
		public List<Vec2> Uv = new();
		public List<DnaRegion> Region = new();
		public List<Vec3> Axis = new();
		public List<Vec3> Radial = new();
		public List<int[]> Faces = new();
		public List<Vec2[]> FaceUvs = new();

		public int AddVertex( Vec3 p, Vec2 uv, DnaRegion region, Vec3 axis, Vec3 radial )
		{
			P.Add( p );
			Uv.Add( uv );
			Region.Add( region );
			Axis.Add( axis );
			Radial.Add( radial );
			return P.Count - 1;
		}

		public int[] AddLoft( IReadOnlyList<Ring> rings, int sides, float u0, float uSpan )
		{
			var starts = new int[rings.Count];

			for ( var ri = 0; ri < rings.Count; ri++ )
			{
				var ring = rings[ri];
				starts[ri] = P.Count;

				for ( var j = 0; j < sides; j++ )
				{
					var a = j / (float)sides * MathF.Tau;
					var ca = MathF.Cos( a );
					var sa = MathF.Sin( a );
					var pos = ring.Center + ring.U * (ca * ring.Ru) + ring.V * (sa * ring.Rv);
					var off = pos - ring.Center;
					var radial = off.LengthSquared > 1e-12f ? off.Normal : ring.U;
					AddVertex( pos, new Vec2( u0 + uSpan * j / sides, ring.VCoord ), ring.Region, ring.Center, radial );
				}
			}

			for ( var ri = 0; ri + 1 < rings.Count; ri++ )
			{
				for ( var j = 0; j < sides; j++ )
				{
					var j1 = (j + 1) % sides;
					var a = starts[ri] + j;
					var b = starts[ri] + j1;
					var c = starts[ri + 1] + j1;
					var d = starts[ri + 1] + j;
					Faces.Add( new[] { a, b, c, d } );
					FaceUvs.Add( new[]
					{
						Uv[a],
						new Vec2( u0 + uSpan * (j + 1) / sides, rings[ri].VCoord ),
						new Vec2( u0 + uSpan * (j + 1) / sides, rings[ri + 1].VCoord ),
						Uv[d],
					} );
				}
			}

			return starts;
		}

		/// <summary>A loft whose rings sit perpendicular to a polyline (limbs, neck, tail,
		/// fingers): the ring frame is rebuilt at every station from the local direction, so
		/// the tube follows the bones instead of assuming a straight axis.</summary>
		public int[] AddTube( IReadOnlyList<Vec3> points, IReadOnlyList<float> radiiU,
			IReadOnlyList<float> radiiV, IReadOnlyList<DnaRegion> regions, int sides,
			float u0, float uSpan, Vec3? upHint = null )
		{
			var rings = new List<Ring>();

			for ( var i = 0; i < points.Count; i++ )
			{
				var prev = points[Math.Max( 0, i - 1 )];
				var next = points[Math.Min( points.Count - 1, i + 1 )];
				var axis = next - prev;

				if ( axis.LengthSquared < 1e-12f )
					axis = new Vec3( 0, 0, 1 );

				axis = axis.Normal;

				Vec3 u;

				if ( upHint.HasValue )
				{
					u = upHint.Value - axis * Vec3.Dot( upHint.Value, axis );

					if ( u.LengthSquared < 1e-6f )
						u = new Vec3( 1, 0, 0 );

					u = u.Normal;
				}
				else
				{
					var refV = MathF.Abs( axis.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
					u = Vec3.Cross( axis, refV ).Normal;
				}

				var v = Vec3.Cross( axis, u );

				rings.Add( new Ring
				{
					Center = points[i], U = u, V = v,
					Ru = radiiU[i], Rv = radiiV[i], Region = regions[i],
					VCoord = points.Count == 1 ? 0f : i / (float)(points.Count - 1),
				} );
			}

			return AddLoft( rings, sides, u0, uSpan );
		}

		public PolyMesh Emit()
		{
			var m = new PolyMesh();

			foreach ( var p in P )
				m.AddVertex( p );

			for ( var i = 0; i < Faces.Count; i++ )
				m.AddFace( Faces[i], FaceUvs[i] );

			return m;
		}
	}

	/// <summary>Per-build working state: the builder, the template/options/skeleton, and the
	/// landmarks the face sliders and the measurements read back.</summary>
	sealed class BuildCtx
	{
		public MeshBuilder Mb = new();
		public CharacterTemplate Template = null!;
		public CharacterDnaOptions Options = null!;
		public Skeleton Skeleton = null!;
		public Dictionary<string, Vec3> Landmarks = new();
		public Dictionary<string, float> Measurements = new();
		/// <summary>Height measure used for the final correction (crown, withers, or length).</summary>
		public float HeightMeasure;
		public string Notes = "";
	}

	/// <summary>Build a character: validate, skeleton, body plan, DNA sliders, skin, then a
	/// final uniform correction so the finished height is exactly <see cref="CharacterDnaOptions.Height"/>.</summary>
	public static CharacterBuild Build( CharacterDnaOptions options )
	{
		if ( options is null )
			throw new ArgumentNullException( nameof( options ) );

		options.Validate();

		var template = GetTemplate( options.Preset );
		var skeleton = BuildSkeleton( template, options );

		if ( template.Kind == CharacterKind.Humanoid )
			ScaleLimbs( skeleton, options );

		var ctx = new BuildCtx { Template = template, Options = options, Skeleton = skeleton };

		switch ( template.Kind )
		{
			case CharacterKind.Humanoid: BuildHumanoid( ctx ); break;
			case CharacterKind.Quadruped: BuildQuadruped( ctx ); break;
			case CharacterKind.Fish: BuildFish( ctx ); break;
			case CharacterKind.Avian: BuildAvian( ctx ); break;
			default: throw new ArgumentException( $"No body planner for kind {template.Kind}." );
		}

		// What "height" means per kind: crown for humanoids/birds, withers for
		// quadrupeds, nose-to-tail length for fish.
		ctx.HeightMeasure = template.Kind switch
		{
			CharacterKind.Fish => ctx.Mb.P.Max( p => p.x ) - ctx.Mb.P.Min( p => p.x ),
			CharacterKind.Quadruped => ctx.Measurements.TryGetValue( "withers", out var w ) ? w : ctx.Mb.P.Max( p => p.z ),
			_ => ctx.Mb.P.Max( p => p.z ) - ctx.Mb.P.Min( p => p.z ),
		};

		ApplyDna( ctx );

		var mesh = ctx.Mb.Emit();
		mesh.Skin = BindSkin( mesh, skeleton );

		// Final correction: the skeleton was built at the requested height, but the mesh
		// crown/withers/length is what the user measures — scale mesh and skeleton together
		// about the ground origin so the finished height is exactly Height.
		var measure = ctx.HeightMeasure;

		if ( !(measure > 0f) )
			throw new InvalidOperationException( $"The {template.Preset} body plan produced no height measure." );

		var factor = options.Height / measure;

		if ( MathF.Abs( factor - 1f ) > 1e-6f )
		{
			for ( var i = 0; i < mesh.Positions.Count; i++ )
				mesh.Positions[i] = mesh.Positions[i] * factor;

			ScaleSkeleton( skeleton, factor );

			foreach ( var key in ctx.Landmarks.Keys.ToList() )
				ctx.Landmarks[key] = ctx.Landmarks[key] * factor;

			// Planner measurements are lengths off the pre-correction mesh; scale them
			// with it so the returned measurements describe the finished character.
			foreach ( var key in ctx.Measurements.Keys.ToList() )
				if ( key is "withers" or "body_length" or "head_height" or "cranium_radius"
					or "eye_spacing" or "nose_length" or "nose_width" or "mouth_half" )
					ctx.Measurements[key] = ctx.Measurements[key] * factor;
		}

		Measure( ctx, mesh );
		FinalMeasurements( ctx, mesh );
		ValidateBuild( ctx, mesh );

		return new CharacterBuild
		{
			Mesh = mesh,
			Skeleton = skeleton,
			Regions = ctx.Mb.Region.Select( r => (int)r ).ToArray(),
			Landmarks = ctx.Landmarks,
			Measurements = ctx.Measurements,
			Template = template,
			Options = options,
			EyeCenters = ctx.Landmarks.TryGetValue( "eye_L", out var el ) && ctx.Landmarks.TryGetValue( "eye_R", out var er )
				? (el, er) : (Vec3.Zero, Vec3.Zero),
			Notes = ctx.Notes,
		};
	}

	static void Measure( BuildCtx ctx, PolyMesh mesh )
	{
		var min = new Vec3( float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity );
		var max = new Vec3( float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		ctx.Measurements["height"] = max.z - min.z;
		ctx.Measurements["length"] = max.x - min.x;
		ctx.Measurements["width"] = max.y - min.y;
		ctx.Measurements["vertices"] = mesh.VertexCount;
		ctx.Measurements["faces"] = mesh.FaceCount;
		ctx.Measurements["bones"] = ctx.Skeleton.Count;
	}

	/// <summary>Width (Y span) of the mesh inside a Z band, for the shoulder/chest/waist/hip
	/// measurements and the slider checks.</summary>
	static float BandWidth( BuildCtx ctx, float z0, float z1 )
	{
		var lo = float.PositiveInfinity;
		var hi = float.NegativeInfinity;

		for ( var i = 0; i < ctx.Mb.P.Count; i++ )
		{
			var p = ctx.Mb.P[i];

			if ( p.z < z0 || p.z > z1 )
				continue;

			lo = MathF.Min( lo, p.y );
			hi = MathF.Max( hi, p.y );
		}

		return float.IsInfinity( lo ) ? 0f : hi - lo;
	}

	static void ValidateBuild( BuildCtx ctx, PolyMesh mesh )
	{
		if ( mesh.VertexCount < 100 )
			throw new InvalidOperationException( $"The {ctx.Template.Preset} mesh has only {mesh.VertexCount} vertices." );

		if ( mesh.FaceCount == 0 )
			throw new InvalidOperationException( $"The {ctx.Template.Preset} mesh has no faces." );

		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( mesh.Faces[i].Count != 4 )
				throw new InvalidOperationException( $"The {ctx.Template.Preset} mesh is not all quads: face {i} has {mesh.Faces[i].Count} corners." );

			foreach ( var uv in mesh.Faces[i].UVs )
				if ( float.IsNaN( uv.x ) || float.IsNaN( uv.y ) || uv.x < -1e-6f || uv.x > 1f + 1e-6f || uv.y < -1e-6f || uv.y > 1f + 1e-6f )
					throw new InvalidOperationException( $"The {ctx.Template.Preset} mesh has a UV outside [0,1] on face {i}." );
		}

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];

			if ( float.IsNaN( p.x ) || float.IsNaN( p.y ) || float.IsNaN( p.z )
				|| float.IsInfinity( p.x ) || float.IsInfinity( p.y ) || float.IsInfinity( p.z ) )
				throw new InvalidOperationException( $"The {ctx.Template.Preset} mesh has a non-finite vertex {i}." );
		}

		if ( !mesh.IsRigged )
			throw new InvalidOperationException( $"The {ctx.Template.Preset} mesh is not rigged: skin weights cover {mesh.Skin?.Count ?? 0} of {mesh.VertexCount} vertices." );

		for ( var i = 0; i < mesh.Skin.Vertices.Count; i++ )
		{
			var sum = 0f;

			foreach ( var w in mesh.Skin[i] )
			{
				if ( w.Bone < 0 || w.Bone >= ctx.Skeleton.Count )
					throw new InvalidOperationException( $"Vertex {i} is weighted to bone {w.Bone}, which the skeleton does not have." );

				sum += w.Weight;
			}

			if ( MathF.Abs( sum - 1f ) > 1e-3f )
				throw new InvalidOperationException( $"Vertex {i} weights sum to {sum}, not 1." );
		}
	}

	// --- humanoid body plan -----------------------------------------------------------------

	static Vec3 Joint( Skeleton s, string name )
	{
		var i = s.IndexOf( name );

		if ( i < 0 )
			throw new ArgumentException( $"This skeleton has no bone '{name}'; the body cannot be placed on it." );

		return s.HeadWorld( i );
	}

	static Vec3 JointOr( Skeleton s, string name, Vec3 fallback )
	{
		var i = s.IndexOf( name );
		return i < 0 ? fallback : s.HeadWorld( i );
	}

	static Ring VRing( Vec3 center, float rx, float ry, DnaRegion region, float v ) =>
		new() { Center = center, U = new Vec3( 1, 0, 0 ), V = new Vec3( 0, 1, 0 ), Ru = rx, Rv = ry, Region = region, VCoord = v };

	static void BuildHumanoid( BuildCtx ctx )
	{
		var o = ctx.Options;
		var t = ctx.Template;
		var s = ctx.Skeleton;
		var mb = ctx.Mb;
		var H = o.Height;
		var headH = H / t.Heads;
		var musc = Eff( o.Muscularity, t.MuscularityBias );
		var effHeadScale = Math.Clamp( o.HeadScale + t.HeadScaleBias, 0.5f, 1.6f );

		var pelvis = Joint( s, "pelvis" );
		var spine2 = Joint( s, "spine_2" );
		var neck = Joint( s, "neck_0" );
		var head = Joint( s, "head" );

		var shoulderW = H * Lerp( 0.20f, 0.30f, Eff( o.ShoulderWidth, t.ShoulderBias ) );
		var chestW = shoulderW * 0.84f;
		var chestD = H * Lerp( 0.10f, 0.16f, Eff( o.ChestDepth, t.ChestBias ) );
		var waistW = H * Lerp( 0.15f, 0.23f, Eff( o.WaistWidth, t.WaistBias ) );
		var waistD = waistW * 0.62f;
		var hipW = H * Lerp( 0.16f, 0.25f, Eff( o.HipWidth, t.HipBias ) );
		var hipD = hipW * 0.66f;
		var neckR = H * 0.030f * effHeadScale;
		var upperArmR = H * 0.024f * (1f + 0.45f * musc);
		var forearmR = H * 0.020f;
		var wristR = H * 0.013f;
		var thighR = H * 0.038f * (1f + 0.25f * musc);
		var calfR = H * 0.030f;
		var ankleR = H * 0.016f;

		var pelvisZ = pelvis.z;
		var waistZ = Lerp( pelvisZ, spine2.z, 0.4f );
		var chestZ = spine2.z;
		var shoulderZ = Joint( s, "arm_upper_L" ).z;
		var neckZ = neck.z;
		var crownZ = H;
		var chinZ = H - headH;

		// Torso: crotch point ring up through the neck. Rings are vertical (axis +Z).
		var hipL = Joint( s, "leg_upper_L" );
		var torsoStarts = mb.AddLoft( new[]
		{
			VRing( new Vec3( pelvis.x, 0, hipL.z - 1.5f ), hipD * 0.25f, hipW * 0.22f, DnaRegion.Pelvis, 0f ),
			VRing( new Vec3( pelvis.x, 0, pelvisZ ), hipD * 0.5f, hipW * 0.5f, DnaRegion.Pelvis, 0.14f ),
			VRing( new Vec3( pelvis.x, 0, waistZ ), waistD * 0.5f, waistW * 0.5f, DnaRegion.Abdomen, 0.32f ),
			VRing( new Vec3( pelvis.x, 0, chestZ ), chestD * 0.5f, chestW * 0.5f, DnaRegion.Chest, 0.55f ),
			VRing( new Vec3( pelvis.x, 0, shoulderZ ), chestD * 0.42f, shoulderW * 0.5f, DnaRegion.Chest, 0.78f ),
			VRing( new Vec3( pelvis.x, 0, neckZ - 0.5f ), neckR, neckR, DnaRegion.Neck, 0.92f ),
			VRing( new Vec3( pelvis.x, 0, neckZ + 2f ), neckR * 0.9f, neckR * 0.9f, DnaRegion.Neck, 1f ),
		}, 16, 0f, 0.38f );

		// Segment the back of the torso: the pelvis ring's posterior half is the
		// buttocks (gluteal mass), the waist ring's posterior half is the lower
		// back. A vertex's stored radial direction decides it: forward is +X, so
		// posterior vertices have radial.x well below zero. The widest (side)
		// vertices stay Pelvis/Abdomen, so the hip-width measurement is unchanged.
		for ( var ri = 1; ri <= 2; ri++ )
		{
			for ( var j = 0; j < 16; j++ )
			{
				var vi = torsoStarts[ri] + j;

				if ( mb.Radial[vi].x < -0.35f )
					mb.Region[vi] = ri == 1 ? DnaRegion.Buttocks : DnaRegion.Back;
			}
		}

		// Head: edge loops at chin / jaw / mouth / nose / eye / brow / cranium / crown heights,
		// so the face has real loops to deform instead of one smooth egg. The first ring is the
		// neck, so the head loft overlaps the torso top instead of leaving a gap.
		var headSides = t.Preset == "realistic_human" ? 24 : 20;
		var craniumR = headH * 0.52f * effHeadScale * (1f + 0.12f * (o.CraniumVault - 0.5f));
		var jawR = craniumR * Lerp( 0.70f, 0.95f, Eff( o.JawWidth, 0f ) );
		var cheekR = craniumR * Lerp( 0.82f, 1.04f, Eff( o.CheekboneProminence, 0f ) );
		var browR = craniumR * Lerp( 0.86f, 0.98f, Eff( o.BrowRidge, 0f ) );
		var eyeR = craniumR * 0.94f;
		var noseR = craniumR * 0.90f;
		var mouthR = jawR * 0.92f;
		var mouthZ = chinZ + headH * 0.30f;
		var noseBaseZ = chinZ + headH * 0.48f;
		var eyeZ = chinZ + headH * 0.60f;
		var browZ = chinZ + headH * 0.70f;
		var craniumZ = chinZ + headH * 0.84f;
		var hx = head.x;

		mb.AddLoft( new[]
		{
			VRing( new Vec3( hx, 0, neckZ + 1f ), neckR * 1.05f, neckR * 1.05f, DnaRegion.Neck, 0f ),
			VRing( new Vec3( hx, 0, chinZ + headH * 0.06f ), jawR * 0.62f, jawR * 0.66f, DnaRegion.Chin, 0.12f ),
			VRing( new Vec3( hx, 0, chinZ + headH * 0.14f ), jawR * 0.88f, jawR * 0.92f, DnaRegion.Jaw, 0.24f ),
			VRing( new Vec3( hx, 0, mouthZ ), mouthR, jawR * 0.98f, DnaRegion.Mouth, 0.38f ),
			VRing( new Vec3( hx, 0, noseBaseZ ), noseR, jawR * 0.96f, DnaRegion.Nose, 0.50f ),
			VRing( new Vec3( hx, 0, eyeZ ), eyeR, cheekR, DnaRegion.Eye, 0.62f ),
			VRing( new Vec3( hx, 0, browZ ), browR, cheekR * 0.98f, DnaRegion.Brow, 0.72f ),
			VRing( new Vec3( hx, 0, craniumZ ), craniumR, craniumR, DnaRegion.Cranium, 0.84f ),
			VRing( new Vec3( hx, 0, crownZ - headH * 0.06f ), craniumR * 0.78f, craniumR * 0.78f, DnaRegion.Cranium, 0.94f ),
			VRing( new Vec3( hx, 0, crownZ ), 0.12f, 0.12f, DnaRegion.Cranium, 1f ),
		}, headSides, 0.38f, 0.20f );

		// Face landmarks, for the sliders and for the eye task that follows.
		var eyeY = craniumR * Lerp( 0.55f, 0.85f, Eff( o.EyeSpacing, 0f ) );
		var noseLen = headH * Lerp( 0.10f, 0.22f, Eff( o.NoseLength, t.NoseLengthBias ) );
		var mouthHalf = craniumR * Lerp( 0.35f, 0.62f, Eff( o.MouthWidth, 0f ) );
		var eyeL = new Vec3( hx + eyeR * 0.72f, eyeY, eyeZ );
		var eyeR3 = new Vec3( hx + eyeR * 0.72f, -eyeY, eyeZ );
		ctx.Landmarks["eye_L"] = eyeL;
		ctx.Landmarks["eye_R"] = eyeR3;
		ctx.Landmarks["head_center"] = new Vec3( hx, 0, chinZ + headH * 0.5f );
		ctx.Landmarks["nose_tip"] = new Vec3( hx + noseR + noseLen, 0, noseBaseZ + headH * 0.05f );
		ctx.Landmarks["nose_base"] = new Vec3( hx + noseR * 0.8f, 0, noseBaseZ );
		ctx.Landmarks["mouth_center"] = new Vec3( hx + mouthR * 0.88f, 0, mouthZ );
		ctx.Landmarks["mouth_L"] = new Vec3( hx + mouthR * 0.8f, mouthHalf, mouthZ );
		ctx.Landmarks["mouth_R"] = new Vec3( hx + mouthR * 0.8f, -mouthHalf, mouthZ );
		ctx.Landmarks["chin"] = new Vec3( hx + jawR * 0.75f, 0, chinZ + headH * 0.06f );
		ctx.Landmarks["brow"] = new Vec3( hx + browR * 0.9f, 0, browZ );
		ctx.Landmarks["crown"] = new Vec3( hx, 0, crownZ );
		ctx.Landmarks["shoulder_L"] = Joint( s, "arm_upper_L" );
		ctx.Landmarks["shoulder_R"] = Joint( s, "arm_upper_R" );
		ctx.Landmarks["neck"] = neck;
		ctx.Measurements["head_height"] = headH;
		ctx.Measurements["cranium_radius"] = craniumR;
		ctx.Measurements["eye_spacing"] = eyeY * 2f;
		ctx.Measurements["nose_length"] = noseLen;
		ctx.Measurements["nose_width"] = craniumR * Lerp( 0.28f, 0.50f, Eff( o.NoseWidth, 0f ) );
		ctx.Measurements["mouth_half"] = mouthHalf;

		// Ears: flattened fins on the sides of the head at eye height.
		foreach ( var side in new[] { 1f, -1f } )
		{
			var earBase = new Vec3( hx + eyeR * 0.25f, side * cheekR * 0.98f, eyeZ - headH * 0.04f );
			var earTip = earBase + new Vec3(
				-0.15f * headH * o.EarPoint,
				side * headH * 0.16f * (0.6f + 0.8f * o.EarSize),
				headH * 0.14f * (0.6f + 0.8f * o.EarSize) + 0.35f * headH * o.EarPoint );
			var earW = headH * 0.05f * (0.6f + 0.8f * o.EarSize);
			mb.AddTube(
				new[] { earBase, (earBase + earTip) * 0.5f, earTip },
				new[] { earW, earW * 0.8f, 0.05f },
				new[] { earW * 0.45f, earW * 0.4f, 0.05f },
				new[] { DnaRegion.Ear, DnaRegion.Ear, DnaRegion.Ear },
				6, side > 0 ? 0.86f : 0.895f, 0.035f );
		}

		// Arms, legs, hands, feet — one side at a time.
		foreach ( var (side, sign) in new[] { ("L", 1f), ("R", -1f) } )
		{
			_ = sign;
			BuildArm( ctx, side, shoulderW, upperArmR, forearmR, wristR, H );
			BuildLeg( ctx, side, thighR, calfR, ankleR, H );
		}

		// Adult sex anatomy: real geometry, only for adult humanoids with a known
		// sex (citizen_male / citizen_female). Neutral archetypes stay smooth, and
		// anyone under 18 gets no sex anatomy at all (see BuildSexAnatomy).
		BuildSexAnatomy( ctx, pelvis, pelvisZ, chestZ, hipD, chestD, chestW );

		ctx.Notes = t.Preset.StartsWith( "citizen", StringComparison.Ordinal )
			? "Citizen hand: 4 digits (index/middle/ring/thumb); citizen has no pinky bone, and the pinky maps to the ring chain per CitizenBoneMap."
			: "Five-finger hands.";
	}

	/// <summary>
	/// Adult sex anatomy as real geometry, not a texture — the RimWorld-style fine
	/// anatomy the sim's R39 wound pipeline needs stable IDs for (groin/genital
	/// injury and severing, breast injury, clothing following damage).
	///
	/// ADULTS ONLY. Builds nothing unless the character is 18+ AND the preset is a
	/// known-sex citizen: citizen_male gets penis + scrotum, citizen_female gets
	/// breasts + vulva/labia. Every other archetype (realistic_human, stylized_hero,
	/// …) stays a smooth neutral base, and an under-18 build gets no sex anatomy
	/// at all — the guard is in this method, not in the caller, so no other path
	/// can produce it by accident.
	///
	/// FORM. Neutral A-pose, neutral proportions, no suggestive posing: the penis
	/// hangs forward-down from the pubic front, the scrotum hangs below/behind it
	/// in two lobes, the breasts project forward from the chest front with a
	/// nipple tip station, the vulva is a short vertical form on the pubic front
	/// flanked by the labia. Every tube's base sits just inside the torso surface
	/// so it joins the body instead of floating in front of it, and every part
	/// gets its own DnaRegion so R39 can address it (Penis/Glans/Scrotum,
	/// Breast/Nipple, Vulva/Labia) and its own landmarks/measurements.
	///
	/// COORDINATES. Inches, Z-up, forward +X, left +Y — the kernel frame. All
	/// positions are joint-relative (pelvis joint, pelvis height, hip/chest
	/// depth/width), so they track the skeleton instead of being absolute numbers.
	/// </summary>
	static void BuildSexAnatomy( BuildCtx ctx, Vec3 pelvis, float pelvisZ, float chestZ, float hipD, float chestD, float chestW )
	{
		var o = ctx.Options;
		var t = ctx.Template;
		var mb = ctx.Mb;

		if ( t.Kind != CharacterKind.Humanoid || o.Age < 18f )
			return;

		var male = t.Preset == "citizen_male";
		var female = t.Preset == "citizen_female";

		if ( !male && !female )
			return;

		var px = pelvis.x;
		var genitalF = 0.75f + 0.5f * o.GenitalSize; // 0.75..1.25 length scale
		var genitalR = 0.8f + 0.4f * o.GenitalSize;  // 0.8..1.2 radius scale

		if ( male )
		{
			// Penis: forward-down tube from the pubic front, glans as the tip
			// station. Base sits just inside the pelvis front so it joins the
			// torso instead of floating in front of it.
			var penBase = new Vec3( px + hipD * 0.42f, 0, pelvisZ - hipD * 0.18f );
			var penMid = penBase + new Vec3( 2.1f * genitalF, 0, -0.9f * genitalF );
			var penTip = penBase + new Vec3( 3.4f * genitalF, 0, -1.7f * genitalF );
			var glansTip = penTip + new Vec3( 0.55f * genitalF, 0, -0.35f * genitalF );
			mb.AddTube(
				new[] { penBase, penMid, penTip, glansTip },
				new[] { 0.62f * genitalR, 0.58f * genitalR, 0.50f * genitalR, 0.40f * genitalR },
				new[] { 0.62f * genitalR, 0.58f * genitalR, 0.50f * genitalR, 0.40f * genitalR },
				new[] { DnaRegion.Penis, DnaRegion.Penis, DnaRegion.Penis, DnaRegion.Glans },
				10, 0.97f, 0.03f );

			// Scrotum: two lobes hanging below and behind the penis base.
			foreach ( var side in new[] { 1f, -1f } )
			{
				var scBase = new Vec3( px + hipD * 0.12f, side * 0.55f * genitalR, pelvisZ - hipD * 0.28f );
				var scMid = new Vec3( px + hipD * 0.08f, side * 0.62f * genitalR, pelvisZ - hipD * 0.42f );
				var scTip = new Vec3( px + hipD * 0.06f, side * 0.55f * genitalR, pelvisZ - hipD * 0.52f );
				mb.AddTube(
					new[] { scBase, scMid, scTip },
					new[] { 0.45f * genitalR, 0.60f * genitalR, 0.30f * genitalR },
					new[] { 0.45f * genitalR, 0.60f * genitalR, 0.30f * genitalR },
					new[] { DnaRegion.Scrotum, DnaRegion.Scrotum, DnaRegion.Scrotum },
					8, 0.97f, 0.03f );
			}

			ctx.Landmarks["penis_base"] = penBase;
			ctx.Landmarks["penis_tip"] = penTip;
			ctx.Landmarks["glans_tip"] = glansTip;
			ctx.Landmarks["scrotum_center"] = new Vec3( px + hipD * 0.08f, 0, pelvisZ - hipD * 0.42f );
		}

		if ( female )
		{
			// Breasts: one forward tube per side off the chest front, nipple as
			// the tip station. Base sits inside the chest so it joins the torso.
			var bustF = 0.6f + 0.8f * o.Bust; // 0.6..1.4 radius/projection scale
			var frontX = px + chestD * 0.5f;
			var breastR = chestW * 0.14f * bustF;

			foreach ( var side in new[] { 1f, -1f } )
			{
				var y0 = side * chestW * 0.30f;
				var brBase = new Vec3( frontX - 0.3f, y0, chestZ + 0.4f );
				var brMid = new Vec3( frontX + breastR * 0.75f, y0, chestZ + 0.15f );
				var nipTip = new Vec3( frontX + breastR * 1.35f, y0, chestZ - 0.1f );
				mb.AddTube(
					new[] { brBase, brMid, nipTip },
					new[] { breastR, breastR * 0.92f, 0.28f },
					new[] { breastR, breastR * 0.92f, 0.28f },
					new[] { DnaRegion.Breast, DnaRegion.Breast, DnaRegion.Nipple },
					12, 0.93f, 0.03f );

				ctx.Landmarks[side > 0f ? "breast_L" : "breast_R"] = nipTip;
			}

			ctx.Landmarks["chest_front"] = new Vec3( frontX, 0, chestZ );

			// Vulva: a short vertical central tube on the pubic front, flanked by
			// the two labia majora. All three sit just inside the pelvis front.
			var vulTop = new Vec3( px + hipD * 0.40f, 0, pelvisZ - hipD * 0.12f );
			var vulMid = new Vec3( px + hipD * 0.44f, 0, pelvisZ - hipD * 0.26f );
			var vulBot = new Vec3( px + hipD * 0.36f, 0, pelvisZ - hipD * 0.38f );
			mb.AddTube(
				new[] { vulTop, vulMid, vulBot },
				new[] { 0.42f, 0.46f, 0.30f },
				new[] { 0.30f, 0.32f, 0.22f },
				new[] { DnaRegion.Vulva, DnaRegion.Vulva, DnaRegion.Vulva },
				8, 0.97f, 0.03f );

			foreach ( var side in new[] { 1f, -1f } )
			{
				var laTop = new Vec3( px + hipD * 0.38f, side * 0.40f, pelvisZ - hipD * 0.12f );
				var laMid = new Vec3( px + hipD * 0.42f, side * 0.44f, pelvisZ - hipD * 0.26f );
				var laBot = new Vec3( px + hipD * 0.34f, side * 0.38f, pelvisZ - hipD * 0.38f );
				mb.AddTube(
					new[] { laTop, laMid, laBot },
					new[] { 0.30f, 0.32f, 0.20f },
					new[] { 0.26f, 0.28f, 0.18f },
					new[] { DnaRegion.Labia, DnaRegion.Labia, DnaRegion.Labia },
					8, 0.97f, 0.03f );
			}

			ctx.Landmarks["vulva_center"] = vulMid;
		}
	}

	static void BuildArm( BuildCtx ctx, string side, float shoulderW, float upperArmR, float forearmR, float wristR, float H )
	{
		var o = ctx.Options;
		var s = ctx.Skeleton;
		var mb = ctx.Mb;
		var shoulder = Joint( s, $"arm_upper_{side}" );
		var elbow = Joint( s, $"arm_lower_{side}" );
		var wrist = Joint( s, $"hand_{side}" );
		var u0 = side == "L" ? 0.58f : 0.65f;

		mb.AddTube(
			new[]
			{
				shoulder,
				(shoulder + elbow) * 0.5f,
				elbow,
				(elbow + wrist) * 0.5f,
				wrist,
			},
			new[] { upperArmR * 1.55f, upperArmR, upperArmR * 0.78f, forearmR, wristR },
			new[] { upperArmR * 1.55f, upperArmR, upperArmR * 0.78f, forearmR, wristR },
			new[] { DnaRegion.Deltoid, DnaRegion.UpperArm, DnaRegion.Elbow, DnaRegion.Forearm, DnaRegion.Forearm },
			10, u0, 0.07f );

		// Hand: flattened palm, then one tapered tube per finger chain the skeleton has.
		var fingertip = JointOr( s, $"finger_middle_2_{side}", wrist + new Vec3( 0, 0, -7f ) );
		var handDir = (fingertip - wrist).LengthSquared > 1e-9f ? (fingertip - wrist).Normal : new Vec3( 0, 0, -1 );
		var handLen = Math.Max( (fingertip - wrist).Length, 1f );
		var knuckle = wrist + handDir * (handLen * 0.5f);
		var palmW = H * 0.024f * o.HandScale;
		var palmT = H * 0.008f * o.HandScale;
		var hu0 = side == "L" ? 0.86f : 0.895f;

		mb.AddTube(
			new[] { wrist, (wrist + knuckle) * 0.5f, knuckle },
			new[] { palmT, palmT, palmT * 0.9f },
			new[] { palmW, palmW, palmW * 0.92f },
			new[] { DnaRegion.Hand, DnaRegion.Hand, DnaRegion.Hand },
			8, hu0, 0.035f, upHint: new Vec3( 1, 0, 0 ) );

		var fingerR = H * 0.0045f * o.HandScale * (ctx.Template.Preset == "heavy_brute" ? 1.35f : 1f);

		foreach ( var finger in new[] { "thumb", "index", "middle", "ring", "pinky" } )
		{
			var meta = s.IndexOf( $"finger_{finger}_meta_{side}" );

			if ( meta < 0 )
				// Citizen's thumb has no meta bone (thumb_0/1/2 only); without this
				// fallback the thumb was silently never built at all.
				meta = s.IndexOf( $"finger_{finger}_0_{side}" );

			if ( meta < 0 )
				continue;

			var p0 = s.HeadWorld( meta );
			var p1 = JointOr( s, $"finger_{finger}_0_{side}", p0 );
			var p2 = JointOr( s, $"finger_{finger}_1_{side}", p1 );
			var p3 = JointOr( s, $"finger_{finger}_2_{side}", p2 );
			var tipDir = (p3 - p2).LengthSquared > 1e-9f ? (p3 - p2).Normal : handDir;
			var tip = p3 + tipDir * (p3 - p2).Length * 0.9f;
			var r = fingerR * (finger == "thumb" ? 1.25f : 1f);
			var claw = ctx.Template.Claws ? 0.04f : 0.15f;

			// One region per finger, so a wound/injury can name the finger it hit
			// (RimWorld-style: thumb / index / middle / ring / pinky, per hand).
			var fingerRegion = finger switch
			{
				"thumb" => DnaRegion.Thumb,
				"index" => DnaRegion.IndexFinger,
				"middle" => DnaRegion.MiddleFinger,
				"ring" => DnaRegion.RingFinger,
				_ => DnaRegion.PinkyFinger,
			};

			mb.AddTube(
				new[] { p0, p1, p2, p3, tip },
				new[] { r, r * 0.95f, r * 0.85f, r * 0.7f, r * claw },
				new[] { r, r * 0.95f, r * 0.85f, r * 0.7f, r * claw },
				new[] { fingerRegion, fingerRegion, fingerRegion, fingerRegion, fingerRegion },
				6, hu0, 0.035f );
		}
	}

	static void BuildLeg( BuildCtx ctx, string side, float thighR, float calfR, float ankleR, float H )
	{
		var o = ctx.Options;
		var s = ctx.Skeleton;
		var mb = ctx.Mb;
		var hip = Joint( s, $"leg_upper_{side}" );
		var knee = Joint( s, $"leg_lower_{side}" );
		var ankle = Joint( s, $"ankle_{side}" );
		var ball = Joint( s, $"ball_{side}" );
		var u0 = side == "L" ? 0.72f : 0.79f;

		mb.AddTube(
			new[]
			{
				hip,
				(hip + knee) * 0.5f,
				knee,
				(knee + ankle) * 0.5f,
				ankle,
			},
			new[] { thighR * 1.15f, thighR, thighR * 0.72f, calfR, ankleR },
			new[] { thighR * 1.15f, thighR, thighR * 0.72f, calfR, ankleR },
			new[] { DnaRegion.Quadriceps, DnaRegion.Quadriceps, DnaRegion.Knee, DnaRegion.Calf, DnaRegion.Ankle },
			10, u0, 0.07f );

		// Foot: flattened tube from the ankle through the ball to the toe on the ground.
		var toe = new Vec3( ball.x + 0.032f * H, ball.y, 0.02f );
		var footH = H * 0.008f * o.FootScale;
		var footW = H * 0.018f * o.FootScale;
		var fu0 = side == "L" ? 0.93f : 0.965f;

		mb.AddTube(
			new[] { ankle, ball, toe },
			new[] { footH, footH * 0.8f, 0.12f },
			new[] { footW, footW * 1.05f, footW * 0.55f },
			new[] { DnaRegion.Foot, DnaRegion.Foot, DnaRegion.Foot },
			8, fu0, 0.035f, upHint: new Vec3( 0, 0, 1 ) );

		// Toes: five small tapered tubes fanning forward from the ball of the foot,
		// one per toe, so the foot is segmented like the hand. The big toe is
		// medial (toward the midline) and the longest / thickest; the little toe
		// is shortest. Citizen has no toe bones, so these are mesh-only segments
		// bound to the ball — same pattern as the fingers on a 4-digit hand.
		// Medial index: L foot's midline side is -Y (k=0), R foot's is +Y (k=4).
		var medialK = side == "L" ? 0 : 4;
		var toeSpacing = footW * 0.36f;

		for ( var k = 0; k < 5; k++ )
		{
			var dMedial = Math.Abs( k - medialK );
			var lenF = 1.2f - 0.09f * dMedial; // 1.20 big toe .. 0.84 little toe
			var radF = 1f - 0.07f * dMedial;
			var yOff = ball.y + (k - 2) * toeSpacing;
			var toeLen = 1.3f * lenF;
			mb.AddTube(
				new[]
				{
					new Vec3( ball.x - 0.5f, yOff, ball.z + 0.2f ),
					new Vec3( ball.x + 0.9f, yOff, 0.35f ),
					new Vec3( ball.x + 0.9f + toeLen, yOff, 0.08f ),
				},
				new[] { 0.28f * radF, 0.24f * radF, 0.10f },
				new[] { 0.28f * radF, 0.24f * radF, 0.10f },
				new[] { DnaRegion.Toe, DnaRegion.Toe, DnaRegion.Toe },
				6, side == "L" ? 0.90f : 0.94f, 0.02f );
		}
	}

	// --- DNA sliders ------------------------------------------------------------------------

	static float Gauss( float x ) => MathF.Exp( -x * x );

	/// <summary>Apply the DNA sliders to the built mesh: muscle inflation along the anatomical
	/// masks, face shaping around the landmarks, age folds/cords, and slouch. Every displacement
	/// is a real vertex move in inches, measured against the landmarks — nothing here scales a
	/// whole part, and height never changes (the final correction re-measures afterwards).</summary>
	static void ApplyDna( BuildCtx ctx )
	{
		var o = ctx.Options;
		var t = ctx.Template;
		var mb = ctx.Mb;
		var musc = Eff( o.Muscularity, t.MuscularityBias );
		var fat = o.BodyFat;
		var H = o.Height;
		var headH = ctx.Measurements.TryGetValue( "head_height", out var hh ) ? hh : H / t.Heads;
		var unit = headH / 9.6f;
		var ageF = Clamp01( (o.Age - 20f) / 40f );
		var shoulderZ = ctx.Landmarks.TryGetValue( "shoulder_L", out var sh ) ? sh.z : H * 0.7f;
		var humanoid = t.Kind == CharacterKind.Humanoid;

		var craniumR = ctx.Measurements.TryGetValue( "cranium_radius", out var cr ) ? cr : headH * 0.5f;
		var noseW = ctx.Measurements.TryGetValue( "nose_width", out var nw ) ? nw : craniumR * 0.4f;
		var noseLen = ctx.Measurements.TryGetValue( "nose_length", out var nl ) ? nl : headH * 0.15f;
		var mouthHalf = ctx.Measurements.TryGetValue( "mouth_half", out var mh ) ? mh : craniumR * 0.45f;

		ctx.Landmarks.TryGetValue( "eye_L", out var eyeL );
		ctx.Landmarks.TryGetValue( "eye_R", out var eyeR );
		ctx.Landmarks.TryGetValue( "nose_tip", out var noseTip );
		ctx.Landmarks.TryGetValue( "nose_base", out var noseBase );
		ctx.Landmarks.TryGetValue( "mouth_center", out var mouthCenter );
		ctx.Landmarks.TryGetValue( "mouth_L", out var mouthL );
		ctx.Landmarks.TryGetValue( "mouth_R", out var mouthR );
		ctx.Landmarks.TryGetValue( "chin", out var chin );
		ctx.Landmarks.TryGetValue( "brow", out var brow );
		ctx.Landmarks.TryGetValue( "head_center", out var headCenter );

		var jawR = craniumR * Lerp( 0.70f, 0.95f, Eff( o.JawWidth, 0f ) );
		var cheekZ = (noseBase.z + eyeL.z) * 0.5f;

		for ( var i = 0; i < mb.P.Count; i++ )
		{
			var p = mb.P[i];
			var radial = mb.Radial[i];
			var axis = mb.Axis[i];
			var region = mb.Region[i];
			var localR = (p - axis).Length;
			var fwd = Math.Clamp( radial.x, -1f, 1f );
			var front = Math.Clamp( radial.x, 0f, 1f );

			// Cranium vault: radial scale of the cranium rings about their own centres.
			if ( region == DnaRegion.Cranium )
			{
				var f = 1f + (o.CraniumVault - 0.5f) * 0.15f;
				p = axis + (p - axis) * f;
				localR = (p - axis).Length;
			}

			// Muscle / fat: radial inflation of the masked region, split front/back where the
			// anatomy splits (biceps front, triceps back; quadriceps front, hamstring back;
			// gastrocnemius at the back of the calf; pectoral at the front of the chest).
			// The deltoid coefficient is large on purpose: the measured deltoid radius is
			// dominated by the fixed inter-shoulder distance, so the cap itself must grow
			// substantially for the measurement to move 10%+ with muscularity.
			float inflate = region switch
			{
				DnaRegion.Deltoid => musc * 0.85f,
				DnaRegion.Chest => (front > 0.2f ? musc * 0.18f * front : 0f) + fat * 0.10f,
				DnaRegion.Abdomen => musc * 0.08f + fat * 0.30f,
				DnaRegion.Back => fat * 0.15f,
				DnaRegion.Pelvis => fat * 0.20f,
				DnaRegion.Buttocks => musc * 0.10f + fat * 0.25f,
				DnaRegion.UpperArm => fwd > 0f ? musc * 0.22f * fwd : musc * 0.18f * -fwd,
				DnaRegion.Forearm => musc * 0.12f,
				DnaRegion.Quadriceps => fwd > 0f ? musc * 0.20f * fwd : musc * 0.12f * -fwd,
				DnaRegion.Calf => fwd < 0f ? musc * 0.22f * -fwd : musc * 0.05f,
				_ => 0f,
			};

			if ( inflate != 0f )
				p += radial * (inflate * localR);

			if ( !humanoid )
			{
				mb.P[i] = p;
				continue;
			}

			// Nose: forward bump at the tip, bridge hump, tip turn.
			if ( front > 0.25f && region is DnaRegion.Nose or DnaRegion.Eye or DnaRegion.Mouth
				or DnaRegion.Cheek or DnaRegion.Brow or DnaRegion.Chin )
			{
				var dzN = (p.z - noseTip.z) / (headH * 0.22f);
				var dyN = p.y / Math.Max( noseW, 0.1f );
				var gN = Gauss( dzN ) * Gauss( dyN );
				p = new Vec3( p.x + noseLen * gN * front, p.y, p.z );

				var bridgeZ = (eyeL.z + noseTip.z) * 0.5f;
				var dzB = (p.z - bridgeZ) / (headH * 0.10f);
				var dyB = p.y / Math.Max( noseW * 0.6f, 0.1f );
				p = new Vec3( p.x + o.NoseBridgeHump * 0.12f * unit * Gauss( dzB ) * Gauss( dyB ) * front, p.y, p.z );

				p = new Vec3( p.x, p.y, p.z + (o.NoseTipTurn - 0.5f) * 0.10f * headH * gN * front );
			}

			// Brow ridge.
			if ( front > 0.25f && region is DnaRegion.Brow or DnaRegion.Eye )
			{
				var dzBr = (p.z - brow.z) / (headH * 0.08f);
				var dyBr = p.y / Math.Max( craniumR * 0.8f, 0.1f );
				p = new Vec3( p.x + o.BrowRidge * 0.18f * unit * Gauss( dzBr ) * Gauss( dyBr ) * front, p.y, p.z );
			}

			// Eye sockets: inward dents around each eye centre, tilted by the canthal tilt
			// (mirrored per side).
			if ( region is DnaRegion.Eye or DnaRegion.Brow or DnaRegion.Cheek )
			{
				foreach ( var eye in new[] { eyeL, eyeR } )
				{
					var dy = p.y - eye.y;
					var dz = p.z - eye.z;
					var tilt = (o.EyeTilt - 0.5f) * 0.35f * Math.Sign( eye.y );
					var c = MathF.Cos( tilt );
					var s2 = MathF.Sin( tilt );
					var ry = dy * c - dz * s2;
					var rz = dy * s2 + dz * c;
					var socketR = craniumR * Lerp( 0.28f, 0.50f, Eff( o.EyeSize, t.EyeSizeBias ) );
					var g = Gauss( ry / socketR ) * Gauss( rz / socketR );
					p -= radial * (0.10f * unit * (0.5f + o.EyeSize) * g * front);
				}
			}

			// Mouth line indent, lip fullness.
			if ( region is DnaRegion.Mouth or DnaRegion.LipUpper or DnaRegion.LipLower or DnaRegion.Chin or DnaRegion.Jaw )
			{
				var dyM = p.y / Math.Max( mouthHalf, 0.1f );
				var dzM = (p.z - mouthCenter.z) / (headH * 0.06f);
				var gM = Gauss( dyM ) * Gauss( dzM );
				p = new Vec3( p.x - 0.06f * unit * gM * front, p.y, p.z );

				var dzU = (p.z - (mouthCenter.z + headH * 0.035f)) / (headH * 0.04f);
				p = new Vec3( p.x + o.UpperLipFullness * 0.08f * unit * Gauss( dyM ) * Gauss( dzU ) * front, p.y, p.z );

				var dzLo = (p.z - (mouthCenter.z - headH * 0.035f)) / (headH * 0.04f);
				p = new Vec3( p.x + o.LowerLipFullness * 0.08f * unit * Gauss( dyM ) * Gauss( dzLo ) * front, p.y, p.z );
			}

			// Chin projection + cleft, jaw angle.
			if ( region is DnaRegion.Chin or DnaRegion.Jaw )
			{
				var dzC = (p.z - chin.z) / (headH * 0.10f);
				var dyC = p.y / Math.Max( jawR, 0.1f );
				var gC = Gauss( dzC ) * Gauss( dyC );
				p = new Vec3( p.x + o.ChinProjection * 0.22f * unit * gC * front, p.y, p.z );
				p = new Vec3( p.x - o.ChinCleft * 0.10f * unit * Gauss( p.y / 0.35f ) * Gauss( dzC ) * front, p.y, p.z );

				if ( region == DnaRegion.Jaw )
					p = new Vec3( p.x, p.y * 1f + (o.JawAngle - 0.5f) * 0.10f, p.z );
			}

			// Cheekbones out, gauntness in below them.
			if ( region is DnaRegion.Cheek or DnaRegion.Eye )
			{
				var dzCh = (p.z - cheekZ) / (headH * 0.10f);
				var sideF = Math.Abs( radial.y );
				p += radial * (o.CheekboneProminence * 0.10f * unit * Gauss( dzCh ) * sideF);
				p -= radial * (o.CheekGauntness * 0.07f * unit
					* Gauss( (p.z - (cheekZ - headH * 0.12f)) / (headH * 0.08f) ) * sideF);
			}

			// Age: nasolabial folds, marionette lines, crow's feet, neck cords.
			if ( ageF > 0f )
			{
				foreach ( var side in new[] { 1f, -1f } )
				{
					var wing = new Vec3( noseBase.x, side * noseW * 0.9f, noseBase.z );
					var corner = side > 0f ? mouthL : mouthR;
					var d = SkinBinder.DistanceToSegment( p, wing, corner );

					if ( d < 0.35f )
						p -= radial * (ageF * 0.10f * unit * (1f - d / 0.35f) * front);

					var d2 = SkinBinder.DistanceToSegment( p, corner, chin );

					if ( d2 < 0.35f )
						p -= radial * (ageF * 0.08f * unit * (1f - d2 / 0.35f) * front);

					var eye = side > 0f ? eyeL : eyeR;
					var outer = eye + new Vec3( 0, side * craniumR * 0.35f, 0 );
					var d3 = SkinBinder.DistanceToSegment( p, outer, outer + new Vec3( 0, side * 0.5f, 0.3f ) );

					if ( d3 < 0.3f )
						p -= radial * (ageF * 0.05f * unit * (1f - d3 / 0.3f) * front);
				}

				if ( region == DnaRegion.Neck && front > 0.5f )
				{
					var neckR = H * 0.030f * Math.Clamp( o.HeadScale + t.HeadScaleBias, 0.5f, 1.6f );

					foreach ( var side in new[] { 1f, -1f } )
					{
						var dyN = MathF.Abs( p.y - side * neckR * 0.45f );

						if ( dyN < 0.3f )
							p = new Vec3( p.x + ageF * 0.06f * unit * front * (1f - dyN / 0.3f), p.y, p.z );
					}
				}
			}

			// Gluteal cleft: a groove down the midline of the buttocks, deepest at
			// the back centre, fading toward the sides. This is what separates the
			// two buttocks on the finished mesh; without it the reclassified
			// Buttocks region would still read as one smooth pelvis ring.
			if ( region == DnaRegion.Buttocks && radial.x < -0.2f )
			{
				var gCleft = Gauss( p.y / 0.7f ) * Math.Clamp( -radial.x, 0f, 1f );
				p -= radial * (0.30f * unit * gCleft);
			}

			// Slouch: everything above the shoulders leans forward, the head most.
			if ( o.PostureSlouch > 0f && p.z > shoulderZ )
			{
				var k = (p.z - shoulderZ) / Math.Max( H - shoulderZ, 1f );
				var headBoost = region is DnaRegion.Cranium or DnaRegion.Jaw or DnaRegion.Chin
					or DnaRegion.Eye or DnaRegion.Nose or DnaRegion.Mouth or DnaRegion.Brow
					or DnaRegion.Cheek or DnaRegion.Ear ? 1.5f : 1f;
				p = new Vec3( p.x + o.PostureSlouch * 0.05f * H * k * headBoost, p.y, p.z );
			}

			mb.P[i] = p;
		}
	}

	// --- quadruped body plan ------------------------------------------------------------------

	static void BuildQuadruped( BuildCtx ctx )
	{
		var o = ctx.Options;
		var t = ctx.Template;
		var s = ctx.Skeleton;
		var mb = ctx.Mb;
		var sc = o.Height / 24f;
		var slim = t.BodySlim;

		var pelvis = Joint( s, "pelvis" );
		var spine2 = Joint( s, "spine_2" );
		var shoulder = Joint( s, "arm_upper_L" );

		// Body: horizontal loft along +X (rump to shoulders). Ring frame: U = +Y (left),
		// V = +Z (up), so U x V = +X.
		var bodyZ = spine2.z;
		mb.AddLoft( new[]
		{
			new Ring { Center = new Vec3( pelvis.x - 7f * sc, 0, pelvis.z ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 3.0f * sc * slim, Rv = 3.6f * sc, Region = DnaRegion.Pelvis, VCoord = 0f },
			new Ring { Center = new Vec3( pelvis.x - 2f * sc, 0, pelvis.z ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 3.4f * sc * slim, Rv = 4.0f * sc, Region = DnaRegion.Pelvis, VCoord = 0.2f },
			new Ring { Center = new Vec3( 0, 0, bodyZ ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 3.6f * sc * slim, Rv = 4.2f * sc, Region = DnaRegion.Belly, VCoord = 0.45f },
			new Ring { Center = new Vec3( shoulder.x, 0, bodyZ ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 3.8f * sc * slim, Rv = 4.6f * sc, Region = DnaRegion.Chest, VCoord = 0.7f },
			new Ring { Center = new Vec3( shoulder.x + 2f * sc, 0, bodyZ ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 3.4f * sc * slim, Rv = 4.2f * sc, Region = DnaRegion.Chest, VCoord = 0.9f },
			new Ring { Center = new Vec3( shoulder.x + 3f * sc, 0, bodyZ ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 0.15f, Rv = 0.15f, Region = DnaRegion.Chest, VCoord = 1f },
		}, 14, 0f, 0.45f );

		// Neck up-forward to the head.
		var neck0 = Joint( s, "neck_0" );
		var neck1 = Joint( s, "neck_1" );
		var head = Joint( s, "head" );

		mb.AddTube(
			new[] { spine2, neck0, neck1, head },
			new[] { 3.2f * sc, 2.8f * sc, 2.4f * sc, 2.2f * sc },
			new[] { 3.2f * sc, 2.8f * sc, 2.4f * sc, 2.2f * sc },
			new[] { DnaRegion.Neck, DnaRegion.Neck, DnaRegion.Neck, DnaRegion.Head },
			12, 0.45f, 0.10f );

		// Skull + muzzle. The muzzle is the snout tube; the canine's is long, the feline's short.
		var muzzleLen = 4.5f * t.Muzzle * sc;
		var skullX = head.x;

		mb.AddLoft( new[]
		{
			new Ring { Center = new Vec3( skullX - 2f * sc, 0, head.z ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 2.6f * sc, Rv = 2.6f * sc, Region = DnaRegion.Head, VCoord = 0f },
			new Ring { Center = new Vec3( skullX + 1f * sc, 0, head.z ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 2.8f * sc, Rv = 2.8f * sc, Region = DnaRegion.Head, VCoord = 0.4f },
			new Ring { Center = new Vec3( skullX + 4f * sc, 0, head.z - 0.5f * sc ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 2.0f * sc, Rv = 1.8f * sc, Region = DnaRegion.Snout, VCoord = 0.7f },
			new Ring { Center = new Vec3( skullX + 4f * sc + muzzleLen, 0, head.z - 0.8f * sc ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 1.1f * sc, Rv = 1.0f * sc, Region = DnaRegion.Snout, VCoord = 0.95f },
			new Ring { Center = new Vec3( skullX + 4.5f * sc + muzzleLen, 0, head.z - 0.8f * sc ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, 1 ), Ru = 0.12f, Rv = 0.12f, Region = DnaRegion.Snout, VCoord = 1f },
		}, 12, 0.45f, 0.15f );

		// Ears: upright fins on the skull.
		foreach ( var side in new[] { 1f, -1f } )
		{
			var earBase = new Vec3( skullX + 0.5f * sc, side * 1.4f * sc, head.z + 2.2f * sc );
			var earTip = earBase + new Vec3( -0.3f * sc, side * 0.6f * sc, 2.5f * sc * t.EarHeight );

			mb.AddTube(
				new[] { earBase, (earBase + earTip) * 0.5f, earTip },
				new[] { 0.9f * sc, 0.7f * sc, 0.06f },
				new[] { 0.35f * sc, 0.3f * sc, 0.06f },
				new[] { DnaRegion.Ear, DnaRegion.Ear, DnaRegion.Ear },
				6, 0.90f, 0.05f );
		}

		// Legs: scapula-humerus-radius-carpus in front, pelvis-femur-tibia-metatarsus behind.
		foreach ( var side in new[] { "L", "R" } )
		{
			var uF = side == "L" ? 0.60f : 0.65f;
			var uH = side == "L" ? 0.70f : 0.75f;
			var scapula = Joint( s, $"scapula_{side}" );
			var sh = Joint( s, $"arm_upper_{side}" );
			var el = Joint( s, $"arm_lower_{side}" );
			var wr = Joint( s, $"hand_{side}" );
			var kn = Joint( s, $"finger_0_{side}" );

			mb.AddTube(
				new[] { scapula, sh, el, wr, kn },
				new[] { 1.4f * sc, 1.2f * sc, 0.9f * sc, 0.6f * sc, 0.45f * sc },
				new[] { 1.4f * sc, 1.2f * sc, 0.9f * sc, 0.6f * sc, 0.45f * sc },
				new[] { DnaRegion.Shoulder, DnaRegion.UpperArm, DnaRegion.Forearm, DnaRegion.Hand, DnaRegion.Foot },
				8, uF, 0.05f );

			var hip = Joint( s, $"leg_upper_{side}" );
			var knee = Joint( s, $"leg_lower_{side}" );
			var hock = Joint( s, $"ankle_{side}" );
			var ball = Joint( s, $"ball_{side}" );

			mb.AddTube(
				new[] { hip, knee, hock, ball },
				new[] { 1.5f * sc, 1.1f * sc, 0.7f * sc, 0.5f * sc },
				new[] { 1.5f * sc, 1.1f * sc, 0.7f * sc, 0.5f * sc },
				new[] { DnaRegion.Hip, DnaRegion.Quadriceps, DnaRegion.Calf, DnaRegion.Foot },
				8, uH, 0.05f );
		}

		// Tail: tapered tube along the tail chain.
		var tailPts = new List<Vec3>();

		// RigBuild.Chain names its bones tail_00, tail_01, ... (two digits).
		for ( var i = 0; i < 5; i++ )
		{
			var idx = s.IndexOf( $"tail_{i:00}" );

			if ( idx >= 0 )
				tailPts.Add( s.HeadWorld( idx ) );
		}

		if ( tailPts.Count >= 2 )
		{
			var last = tailPts[^1];
			var dir = (last - tailPts[^2]).Normal;
			tailPts.Add( last + dir * 2f * sc * t.TailLength );

			var radii = tailPts.Select( (_, i) => Math.Max( 1.0f - i * 0.18f, 0.15f ) * sc ).ToList();
			var regions = tailPts.Select( _ => DnaRegion.Tail ).ToList();

			mb.AddTube( tailPts, radii, radii, regions, 8, 0.80f, 0.06f );
		}

		ctx.Landmarks["eye_L"] = new Vec3( skullX + 2f * sc, 1.8f * sc, head.z + 1.2f * sc );
		ctx.Landmarks["eye_R"] = new Vec3( skullX + 2f * sc, -1.8f * sc, head.z + 1.2f * sc );
		ctx.Landmarks["nose_tip"] = new Vec3( skullX + 4f * sc + muzzleLen, 0, head.z - 0.8f * sc );
		ctx.Landmarks["shoulder_L"] = shoulder;
		// Withers = top of the torso at the shoulder station: the torso tube is centred on
		// bodyZ (spine_2, 23.5*sc), NOT on the shoulder joint (19*sc, near the chest floor).
		ctx.Measurements["withers"] = bodyZ + 4.6f * sc;
		ctx.Measurements["body_length"] = (shoulder.x + 3f * sc) - (pelvis.x - 7f * sc);
		ctx.Notes = "Forelimb: scapula_L > arm_upper_L (humerus) > arm_lower_L (radius) > hand_L (carpus). "
			+ "Hindlimb: leg_upper_L (femur) > leg_lower_L (tibia) > ankle_L (metatarsus) > ball_L. Tail: tail_0..4.";
	}

	// --- fish body plan -----------------------------------------------------------------------

	static void BuildFish( BuildCtx ctx )
	{
		var o = ctx.Options;
		var s = ctx.Skeleton;
		var mb = ctx.Mb;
		var sc = o.Height / 24f;

		// Fusiform body, nose +X to tail -X. Ring frame U = +Y, V = -Z, so U x V = -X.
		mb.AddLoft( new[]
		{
			new Ring { Center = new Vec3( 12f * sc, 0, 0 ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, -1 ), Ru = 0.15f, Rv = 0.15f, Region = DnaRegion.Snout, VCoord = 0f },
			new Ring { Center = new Vec3( 9f * sc, 0, 0 ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, -1 ), Ru = 2.2f * sc, Rv = 1.8f * sc, Region = DnaRegion.Head, VCoord = 0.15f },
			new Ring { Center = new Vec3( 4f * sc, 0, 0 ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, -1 ), Ru = 3.0f * sc, Rv = 2.5f * sc, Region = DnaRegion.Torso, VCoord = 0.4f },
			new Ring { Center = new Vec3( 0, 0, 0 ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, -1 ), Ru = 2.8f * sc, Rv = 2.3f * sc, Region = DnaRegion.Belly, VCoord = 0.6f },
			new Ring { Center = new Vec3( -6f * sc, 0, 0 ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, -1 ), Ru = 2.0f * sc, Rv = 1.6f * sc, Region = DnaRegion.Torso, VCoord = 0.8f },
			new Ring { Center = new Vec3( -11f * sc, 0, 0 ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, -1 ), Ru = 0.8f * sc, Rv = 0.7f * sc, Region = DnaRegion.Tail, VCoord = 0.95f },
			new Ring { Center = new Vec3( -12.5f * sc, 0, 0 ), U = new Vec3( 0, 1, 0 ), V = new Vec3( 0, 0, -1 ), Ru = 0.12f, Rv = 0.12f, Region = DnaRegion.Tail, VCoord = 1f },
		}, 12, 0f, 0.50f );

		// Caudal fin: vertical blade at the tail, thin in Y.
		mb.AddTube(
			new[] { new Vec3( -12f * sc, 0, 0 ), new Vec3( -14f * sc, 0, 1.2f * sc ), new Vec3( -15.5f * sc, 0, 2.6f * sc ) },
			new[] { 0.9f * sc, 1.6f * sc, 0.15f },
			new[] { 0.15f, 0.15f, 0.15f },
			new[] { DnaRegion.Fin, DnaRegion.Fin, DnaRegion.Fin },
			6, 0.80f, 0.06f, upHint: new Vec3( 0, 0, 1 ) );
		mb.AddTube(
			new[] { new Vec3( -12f * sc, 0, 0 ), new Vec3( -14f * sc, 0, -1.2f * sc ), new Vec3( -15.5f * sc, 0, -2.6f * sc ) },
			new[] { 0.9f * sc, 1.6f * sc, 0.15f },
			new[] { 0.15f, 0.15f, 0.15f },
			new[] { DnaRegion.Fin, DnaRegion.Fin, DnaRegion.Fin },
			6, 0.86f, 0.06f, upHint: new Vec3( 0, 0, 1 ) );

		// Dorsal fins along the top, pectoral / pelvic fins below the sides.
		for ( var i = 0; i < 3; i++ )
		{
			var x0 = (-1f - i * 2f) * sc;
			mb.AddTube(
				new[] { new Vec3( x0, 0, 2.3f * sc ), new Vec3( x0 - 1f * sc, 0, 3.4f * sc ), new Vec3( x0 - 2f * sc, 0, 4.2f * sc ) },
				new[] { 1.6f * sc, 1.2f * sc, 0.12f },
				new[] { 0.15f, 0.15f, 0.12f },
				new[] { DnaRegion.Fin, DnaRegion.Fin, DnaRegion.Fin },
				6, 0.92f, 0.02f, upHint: new Vec3( 1, 0, 0 ) );
		}

		foreach ( var side in new[] { 1f, -1f } )
		{
			mb.AddTube(
				new[] { new Vec3( 6f * sc, side * 2.5f * sc, -0.5f * sc ), new Vec3( 5f * sc, side * 3.8f * sc, -1.4f * sc ), new Vec3( 4f * sc, side * 4.6f * sc, -2.2f * sc ) },
				new[] { 1.8f * sc, 1.4f * sc, 0.12f },
				new[] { 0.15f, 0.15f, 0.12f },
				new[] { DnaRegion.Fin, DnaRegion.Fin, DnaRegion.Fin },
				6, side > 0 ? 0.94f : 0.96f, 0.02f, upHint: new Vec3( 1, 0, 0 ) );
		}

		var eyeL = JointOr( s, "eye_L", new Vec3( 11f * sc, 1.2f * sc, 0.8f * sc ) );
		var eyeR = JointOr( s, "eye_R", new Vec3( 11f * sc, -1.2f * sc, 0.8f * sc ) );
		ctx.Landmarks["eye_L"] = eyeL;
		ctx.Landmarks["eye_R"] = eyeR;
		ctx.Landmarks["nose_tip"] = new Vec3( 12f * sc, 0, 0 );
		ctx.Notes = "Fish: root + spine chain, head/jaw/eyes/opercles, caudal + dorsal + pectoral fins.";
	}

	// --- avian body plan ----------------------------------------------------------------------

	static void BuildAvian( BuildCtx ctx )
	{
		var o = ctx.Options;
		var s = ctx.Skeleton;
		var mb = ctx.Mb;
		var sc = o.Height / 36f;

		var pelvis = Joint( s, "pelvis" );
		var spine0 = Joint( s, "spine_0" );
		var neck = Joint( s, "neck" );
		var head = Joint( s, "head" );

		// Torso: upright ovoid.
		mb.AddLoft( new[]
		{
			VRing( new Vec3( 0, 0, pelvis.z - 1f * sc ), 3.2f * sc, 3.2f * sc, DnaRegion.Pelvis, 0f ),
			VRing( new Vec3( 0, 0, pelvis.z + 2f * sc ), 4.2f * sc, 4.2f * sc, DnaRegion.Belly, 0.3f ),
			VRing( new Vec3( 0, 0, spine0.z ), 4.0f * sc, 4.0f * sc, DnaRegion.Chest, 0.6f ),
			VRing( new Vec3( 0, 0, spine0.z + 4f * sc ), 2.8f * sc, 2.8f * sc, DnaRegion.Neck, 0.85f ),
			VRing( new Vec3( 0, 0, spine0.z + 5f * sc ), 1.6f * sc, 1.6f * sc, DnaRegion.Neck, 1f ),
		}, 14, 0f, 0.40f );

		// Neck up-forward, head, beak cone.
		mb.AddTube(
			new[] { spine0, neck, head },
			new[] { 1.8f * sc, 1.4f * sc, 1.2f * sc },
			new[] { 1.8f * sc, 1.4f * sc, 1.2f * sc },
			new[] { DnaRegion.Neck, DnaRegion.Neck, DnaRegion.Head },
			10, 0.40f, 0.10f );

		var beakBase = head + new Vec3( 2f * sc, 0, 0.5f * sc );
		var beakTip = head + new Vec3( 6f * sc, 0, 0 );

		mb.AddTube(
			new[] { beakBase, (beakBase + beakTip) * 0.5f, beakTip },
			new[] { 1.0f * sc, 0.6f * sc, 0.10f },
			new[] { 1.0f * sc, 0.6f * sc, 0.10f },
			new[] { DnaRegion.Beak, DnaRegion.Beak, DnaRegion.Beak },
			8, 0.50f, 0.05f );

		// Wings: flattened tubes along the three wing segments, chord along +X, thin in Z.
		foreach ( var side in new[] { "L", "R" } )
		{
			var u0 = side == "L" ? 0.55f : 0.70f;
			var wsh = Joint( s, $"wing_shoulder_{side}" );
			var wel = Joint( s, $"wing_elbow_{side}" );
			var wwr = Joint( s, $"wing_wrist_{side}" );
			var pri = JointOr( s, $"feather_pri_4_{side}", wwr + new Vec3( -4f * sc, side == "L" ? 4f * sc : -4f * sc, 0 ) );

			mb.AddTube(
				new[] { wsh, wel, wwr, pri },
				new[] { 4.5f * sc, 3.5f * sc, 2.5f * sc, 0.8f * sc },
				new[] { 0.25f * sc, 0.25f * sc, 0.22f * sc, 0.12f },
				new[] { DnaRegion.Wing, DnaRegion.Wing, DnaRegion.Wing, DnaRegion.Wing },
				8, u0, 0.15f, upHint: new Vec3( 1, 0, 0 ) );

			// Legs: thigh, shin, talon, toe.
			var hip = Joint( s, $"leg_upper_{side}" );
			var shin = Joint( s, $"leg_lower_{side}" );
			var talon = Joint( s, $"talon_{side}" );
			var toe = JointOr( s, $"toe_{side}", talon + new Vec3( 2f * sc, 0, -talon.z ) );

			mb.AddTube(
				new[] { hip, shin, talon, toe },
				new[] { 0.9f * sc, 0.6f * sc, 0.45f * sc, 0.25f * sc },
				new[] { 0.9f * sc, 0.6f * sc, 0.45f * sc, 0.25f * sc },
				new[] { DnaRegion.Quadriceps, DnaRegion.Calf, DnaRegion.Foot, DnaRegion.Foot },
				8, side == "L" ? 0.85f : 0.90f, 0.05f );
		}

		// Tail fan: flattened tube back from the pelvis.
		var tail0 = Joint( s, "tail_0" );
		var tail1 = Joint( s, "tail_1" );
		var fanTip = tail1 + new Vec3( -5f * sc, 0, 0 );

		mb.AddTube(
			new[] { tail0, tail1, fanTip },
			new[] { 0.3f * sc, 0.3f * sc, 0.25f * sc },
			new[] { 1.2f * sc, 2.6f * sc, 3.2f * sc },
			new[] { DnaRegion.Tail, DnaRegion.Tail, DnaRegion.Tail },
			8, 0.95f, 0.05f, upHint: new Vec3( 0, 0, 1 ) );

		ctx.Landmarks["eye_L"] = head + new Vec3( 2f * sc, 1.2f * sc, 1f * sc );
		ctx.Landmarks["eye_R"] = head + new Vec3( 2f * sc, -1.2f * sc, 1f * sc );
		ctx.Landmarks["nose_tip"] = beakTip;
		ctx.Landmarks["shoulder_L"] = Joint( s, "wing_shoulder_L" );
		ctx.Notes = "Avian: pelvis/spine/neck/head/beak, three-segment wings with feather chains, talons, tail fan.";
	}

	// --- final measurements -------------------------------------------------------------------

	static float BandWidthMesh( PolyMesh mesh, IReadOnlyList<DnaRegion> regions, float z0, float z1 )
	{
		var lo = float.PositiveInfinity;
		var hi = float.NegativeInfinity;

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];

			if ( p.z < z0 || p.z > z1 )
				continue;

			lo = MathF.Min( lo, p.y );
			hi = MathF.Max( hi, p.y );
		}

		return float.IsInfinity( lo ) ? 0f : hi - lo;
	}

	/// <summary>Measurements off the finished mesh: band widths at the shoulder/chest/waist/hip
	/// joints, deltoid radius off the shoulder-elbow segment, eye spacing.</summary>
	static void FinalMeasurements( BuildCtx ctx, PolyMesh mesh )
	{
		if ( ctx.Template.Kind != CharacterKind.Humanoid )
			return;

		var regions = ctx.Mb.Region;

		if ( ctx.Landmarks.TryGetValue( "shoulder_L", out var shoulder ) )
		{
			ctx.Measurements["shoulder_width"] = BandWidthMesh( mesh, regions, shoulder.z - 1.5f, shoulder.z + 1.5f );

			var elbow = JointOr( ctx.Skeleton, "arm_lower_L", shoulder );
			var deltoidR = 0f;

			for ( var i = 0; i < mesh.Positions.Count; i++ )
			{
				if ( regions[i] != DnaRegion.Deltoid )
					continue;

				deltoidR = MathF.Max( deltoidR, SkinBinder.DistanceToSegment( mesh.Positions[i], shoulder, elbow ) );
			}

			ctx.Measurements["deltoid_radius"] = deltoidR;
		}

		if ( ctx.Landmarks.TryGetValue( "neck", out var neck ) )
			ctx.Measurements["chest_width"] = BandWidthMesh( mesh, regions, neck.z - 6f, neck.z - 2f );

		// Sex anatomy + fine segmentation, off the finished mesh / landmarks.
		// These are counts (as floats, like vertices/faces) and finished lengths,
		// so they describe the exported character, not the pre-correction plan.
		int Count( DnaRegion r ) => regions.Count( x => x == r );
		ctx.Measurements["breast_vertices"] = Count( DnaRegion.Breast ) + Count( DnaRegion.Nipple );
		ctx.Measurements["penis_vertices"] = Count( DnaRegion.Penis ) + Count( DnaRegion.Glans );
		ctx.Measurements["scrotum_vertices"] = Count( DnaRegion.Scrotum );
		ctx.Measurements["vulva_vertices"] = Count( DnaRegion.Vulva ) + Count( DnaRegion.Labia );
		ctx.Measurements["buttocks_vertices"] = Count( DnaRegion.Buttocks );
		ctx.Measurements["toe_vertices"] = Count( DnaRegion.Toe );
		ctx.Measurements["finger_vertices"] = Count( DnaRegion.Thumb ) + Count( DnaRegion.IndexFinger )
			+ Count( DnaRegion.MiddleFinger ) + Count( DnaRegion.RingFinger ) + Count( DnaRegion.PinkyFinger );
		ctx.Measurements["elbow_vertices"] = Count( DnaRegion.Elbow );
		ctx.Measurements["knee_vertices"] = Count( DnaRegion.Knee );
		ctx.Measurements["ankle_vertices"] = Count( DnaRegion.Ankle );

		if ( ctx.Landmarks.TryGetValue( "penis_base", out var penBase )
			&& ctx.Landmarks.TryGetValue( "penis_tip", out var penTip ) )
			ctx.Measurements["penis_length"] = (penTip - penBase).Length;

		if ( ctx.Landmarks.TryGetValue( "breast_L", out var brL )
			&& ctx.Landmarks.TryGetValue( "chest_front", out var chestFront ) )
			ctx.Measurements["breast_projection"] = brL.x - chestFront.x;

		ctx.Measurements["eye_spacing_measured"] = ctx.Landmarks.TryGetValue( "eye_L", out var el )
			&& ctx.Landmarks.TryGetValue( "eye_R", out var er ) ? (el - er).Length : 0f;
	}
}
