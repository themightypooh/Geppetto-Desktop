using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Ready-made skeletons to start a rig from: the s&amp;box human, citizen, a biped built from
/// head-height proportions, and a quadruped with a choice of leg.
///
/// HUMAN IS THE MEASURED ONE. Its joint positions are the s&amp;box human's
/// (<c>citizen_human_male_REF.fbx</c>, read bone by bone 2026-10-16 and recorded in the
/// geppetto craft notes, 1 unit = 1 inch, ground at z 0, A-pose with the arms 49° below
/// horizontal and the legs splayed 6°) and its bone names are the REF's. The bones are placed the
/// way Effigy places every bone - +Y down the bone - so the ORIENTATIONS are not the engine's bind
/// pose: this is a rig to model, block out, weight and animate in Effigy on, not a drop-in for the
/// human's own animations. For those, <see cref="CitizenSkeleton"/> is the engine-exact table.
/// The fingers are laid out by the hand's measured length (wrist to middle tip, 7.2 in), not
/// measured joint by joint.
///
/// BIPED is the proportion preset (geppetto gap P2-13): a height and a number of heads give the
/// chin, the crotch comes from the style, and the torso's joints sit at the fractions of chin to
/// crotch the measured human has. Styles and where their numbers came from:
///   realistic  7.5 heads, crotch at 0.485 of the height (ANSUR II, scaled), shoulders 2 heads wide
///   heroic     8 heads, crotch at half the height, shoulders 2.33 heads wide (craft notes 1.3)
///   fashion    8.75 heads, crotch at 0.53 (legs exaggerated; derived from "superhero/fashion")
///   stylized   6 heads, crotch at 0.47 (derived)
///   chibi      2.5 heads, crotch at 0.33, arms three quarters long (derived from "stubby limbs")
///   child      5.5 heads, crotch at 0.44 (derived)
///   human      the measured layout above, scaled to the height
///
/// QUADRUPED takes the leg type from the creature-leg notes (Draw Sessions, "Plantigrade vs
/// Digitigrade vs Unguligrade"): plantigrade puts the whole sole down; digitigrade lifts the heel
/// (the hock) high and stands on the toes; unguligrade has a short thigh and shin and a long
/// cannon to the hoof. Every foot passes the notes' balance check - a vertical from the knee falls
/// on the foot. Scale 1 is a 24-inch-at-the-shoulder dog.
/// </summary>
public static class RigTemplates
{
	public static readonly string[] Names = { "human", "citizen", "biped", "quadruped", "fish", "avian", "arachnid", "serpentine", "vehicle_wheeled", "weapon_firearm" };

	public static readonly string[] Styles = { "realistic", "heroic", "fashion", "stylized", "chibi", "child", "human" };

	public static readonly string[] LegTypes = { "plantigrade", "digitigrade", "unguligrade" };

	/// <summary>The limb bones a template's twist option gives two twist bones each - the ones the
	/// human REF carries <c>_twist0/1</c> on.</summary>
	public static readonly string[] TwistLimbs =
	{
		"arm_upper_L", "arm_lower_L", "leg_upper_L", "leg_lower_L",
		"arm_upper_R", "arm_lower_R", "leg_upper_R", "leg_lower_R",
	};

	/// <summary>The s&amp;box human's height, top of the head, in inches.</summary>
	public const float HumanHeight = 68.9f;

	/// <summary>Named heights the template was built to, for the script to report and a test to
	/// check: top, chin, shoulder, crotch, knee, ankle, and heads.</summary>
	public sealed class Report
	{
		public readonly Dictionary<string, float> Landmarks = new();
		public string Summary;
	}

	/// <summary>Joint positions for one side (the left, +y) and the middle.</summary>
	sealed class Layout
	{
		public Vec3 Pelvis, Spine0, Spine1, Spine2, Neck, Head, Top;
		public Vec3 Clavicle, Shoulder, Elbow, Wrist, Fingertip;
		public Vec3 Hip, Knee, Ankle, Ball, Toe;
	}

	// --- human ------------------------------------------------------------------------------------

	static Layout HumanLayout( float s )
	{
		// Arm joints in the A-pose: z from the measured table, y from the measured segment lengths
		// (upper arm 10.5, forearm 9.8, wrist to middle tip 7.2) so each bone is its real length.
		var shoulder = new Vec3( 0, 7.30f, 53.8f );
		var elbow = new Vec3( 0, shoulder.y + MathF.Sqrt( 10.5f * 10.5f - 8.4f * 8.4f ), 45.4f );
		var wrist = new Vec3( 0, elbow.y + MathF.Sqrt( 9.8f * 9.8f - 6.6f * 6.6f ), 38.8f );
		var tip = new Vec3( 0, wrist.y + MathF.Sqrt( 7.2f * 7.2f - 6.9f * 6.9f ), 31.9f );

		// Legs splayed 6°: thigh 16.1, shin 15.5; ankle to ball 5.3 forward and down to 0.36.
		var hip = new Vec3( 0, 3.94f, 34.9f );
		var splay = MathF.Sin( 6f * MathF.PI / 180f );
		var knee = new Vec3( 0, hip.y + 16.1f * splay, 18.9f );
		var ankle = new Vec3( 0, knee.y + 15.5f * splay, 3.6f );
		var ball = new Vec3( MathF.Sqrt( 5.3f * 5.3f - 3.24f * 3.24f ), ankle.y, 0.36f );

		return Scale( new Layout
		{
			Pelvis = new Vec3( 0, 0, 36.6f ),
			Spine0 = new Vec3( 0, 0, 39.8f ),
			Spine1 = new Vec3( 0, 0, 44.2f ),
			Spine2 = new Vec3( 0, 0, 48.6f ),
			Neck = new Vec3( 0, 0, 57.2f ),
			Head = new Vec3( 0, 0, 60.4f ),
			Top = new Vec3( 0, 0, 67.5f ),
			Clavicle = new Vec3( 0, 0.70f, 54.3f ),
			Shoulder = shoulder,
			Elbow = elbow,
			Wrist = wrist,
			Fingertip = tip,
			Hip = hip,
			Knee = knee,
			Ankle = ankle,
			Ball = ball,
			Toe = ball + new Vec3( 2.2f, 0, -0.36f ),
		}, s );
	}

	static Layout Scale( Layout l, float s )
	{
		Vec3 S( Vec3 v ) => v * s;
		return new Layout
		{
			Pelvis = S( l.Pelvis ), Spine0 = S( l.Spine0 ), Spine1 = S( l.Spine1 ), Spine2 = S( l.Spine2 ),
			Neck = S( l.Neck ), Head = S( l.Head ), Top = S( l.Top ),
			Clavicle = S( l.Clavicle ), Shoulder = S( l.Shoulder ), Elbow = S( l.Elbow ), Wrist = S( l.Wrist ),
			Fingertip = S( l.Fingertip ), Hip = S( l.Hip ), Knee = S( l.Knee ), Ankle = S( l.Ankle ),
			Ball = S( l.Ball ), Toe = S( l.Toe ),
		};
	}

	/// <summary>The s&amp;box human's joints and bone names, <paramref name="height"/> tall (68.9 is
	/// life size). Fingers on by default; <paramref name="twist"/> adds two twist bones to each
	/// upper and lower arm and leg, as the REF has.</summary>
	public static Skeleton Human( float height = HumanHeight, bool fingers = true, bool twist = false ) =>
		Human( height, fingers, twist, out _ );

	public static Skeleton Human( float height, bool fingers, bool twist, out Report report )
	{
		RequirePositive( height, "height" );
		var layout = HumanLayout( height / HumanHeight );
		report = Describe( layout, height, HumanHeight / 9.84f, "human" );
		return BuildBiped( layout, fingers, twist );
	}

	/// <summary>Citizen's skeleton, engine-exact (see <see cref="CitizenSkeleton"/>).</summary>
	public static Skeleton Citizen() => CitizenSkeleton.Build();

	// --- biped ------------------------------------------------------------------------------------

	/// <summary>Default heads for a style, and its crotch height as a fraction of the total.</summary>
	public static (float Heads, float Crotch, float ShoulderHeads, float Arms) Style( string style ) =>
		(style ?? "realistic").Trim().ToLowerInvariant() switch
		{
			"realistic" or "real" or "average" => (7.5f, 0.485f, 2.0f, 1f),
			"heroic" or "hero" or "ideal" => (8f, 0.5f, 2.33f, 1f),
			"fashion" or "superhero" => (8.75f, 0.53f, 2.0f, 1f),
			"stylized" or "stylised" or "anime" or "teen" => (6f, 0.47f, 1.8f, 1f),
			"chibi" or "sd" => (2.5f, 0.33f, 1.5f, 0.75f),
			"child" or "kid" => (5.5f, 0.44f, 1.7f, 1f),
			"human" or "sbox" => (7f, 0.443f, 2.0f, 1f),
			_ => throw new ArgumentException( $"No biped style '{style}'. Styles: {string.Join( ", ", Styles )}." ),
		};

	/// <summary>
	/// A biped from proportions: <paramref name="heads"/> head-heights in <paramref name="height"/>
	/// inches (0 = the style's own count). Human bone names, so it binds, mirrors and exports like
	/// the human template.
	/// </summary>
	public static Skeleton Biped( float heads = 0f, string style = "realistic", float height = HumanHeight,
		bool fingers = true, bool twist = false ) => Biped( heads, style, height, fingers, twist, out _ );

	public static Skeleton Biped( float heads, string style, float height, bool fingers, bool twist, out Report report )
	{
		RequirePositive( height, "height" );
		var preset = Style( style );

		if ( heads <= 0f )
			heads = preset.Heads;

		if ( heads < 1.5f || heads > 12f )
			throw new ArgumentOutOfRangeException( nameof( heads ), $"heads={heads}: a figure is 1.5 to 12 heads tall." );

		var key = (style ?? "realistic").Trim().ToLowerInvariant();

		if ( key is "human" or "sbox" )
		{
			// The measured layout, its head resized: everything below the chin keeps the human's
			// shape and is scaled into what the head leaves.
			var human = HumanLayout( 1f );
			var chin = 59.1f;
			var newHead = height / heads;
			var below = (height - newHead) / chin;
			Vec3 Squash( Vec3 v ) => v.z <= chin ? new Vec3( v.x * below, v.y * below, v.z * below ) : v;
			var l = new Layout
			{
				Pelvis = Squash( human.Pelvis ), Spine0 = Squash( human.Spine0 ), Spine1 = Squash( human.Spine1 ),
				Spine2 = Squash( human.Spine2 ), Neck = Squash( human.Neck ),
				Head = new Vec3( 0, 0, height - newHead + 0.13f * newHead ), Top = new Vec3( 0, 0, height - 0.14f * newHead ),
				Clavicle = Squash( human.Clavicle ), Shoulder = Squash( human.Shoulder ), Elbow = Squash( human.Elbow ),
				Wrist = Squash( human.Wrist ), Fingertip = Squash( human.Fingertip ), Hip = Squash( human.Hip ),
				Knee = Squash( human.Knee ), Ankle = Squash( human.Ankle ), Ball = Squash( human.Ball ), Toe = Squash( human.Toe ),
			};
			report = Describe( l, height, heads, "human" );
			return BuildBiped( l, fingers, twist );
		}

		var layout = ProportionLayout( height, heads, preset );
		report = Describe( layout, height, heads, key );
		return BuildBiped( layout, fingers, twist );
	}

	static Layout ProportionLayout( float t, float heads, (float Heads, float Crotch, float ShoulderHeads, float Arms) p )
	{
		var h = t / heads;
		var chin = t - h;
		var crotch = p.Crotch * t;

		if ( crotch > chin - 0.15f * t )
			crotch = chin - 0.15f * t;

		// The torso's joints sit where the measured human has them along chin-to-crotch.
		var torso = chin - crotch;
		var neck = chin - 0.066f * torso;
		var shoulderZ = chin - 0.185f * torso;
		var pelvisZ = crotch + 0.213f * torso;
		var hipZ = crotch + 0.154f * torso;
		float SpineAt( float f ) => pelvisZ + (neck - pelvisZ) * f;

		// Widths from the measured human (shoulder joints 0.212 of the height apart at 2 heads of
		// shoulder, hips 0.114), scaled by the style's shoulder width.
		var shoulderY = 0.106f * t * p.ShoulderHeads / 2f;
		var hipY = 0.057f * t;

		// Arms in the human's A-pose, 49° below horizontal; the hand hangs steeper.
		var upper = 0.152f * t * p.Arms;
		var fore = 0.142f * t * p.Arms;
		var hand = 0.105f * t * p.Arms;
		var down = 49f * MathF.PI / 180f;
		var armDir = new Vec3( 0, MathF.Cos( down ), -MathF.Sin( down ) );
		var handDown = 73f * MathF.PI / 180f;
		var handDir = new Vec3( 0, MathF.Cos( handDown ), -MathF.Sin( handDown ) );
		var shoulder = new Vec3( 0, shoulderY, shoulderZ );
		var elbow = shoulder + armDir * upper;
		var wrist = elbow + armDir * fore;

		// Legs: knee and ankle at the human's fractions of the crotch height, splayed 6°.
		var hip = new Vec3( 0, hipY, hipZ );
		var kneeZ = 0.62f * crotch;
		var ankleZ = 0.118f * crotch;
		var splay = MathF.Tan( 6f * MathF.PI / 180f );
		var knee = new Vec3( 0, hip.y + (hipZ - kneeZ) * splay, kneeZ );
		var ankle = new Vec3( 0, knee.y + (kneeZ - ankleZ) * splay, ankleZ );
		var foot = 0.077f * t;
		var ball = new Vec3( MathF.Sqrt( MathF.Max( foot * foot - ankleZ * ankleZ, foot * foot * 0.25f ) ), ankle.y, 0.005f * t );

		return new Layout
		{
			Pelvis = new Vec3( 0, 0, pelvisZ ),
			Spine0 = new Vec3( 0, 0, SpineAt( 0.155f ) ),
			Spine1 = new Vec3( 0, 0, SpineAt( 0.369f ) ),
			Spine2 = new Vec3( 0, 0, SpineAt( 0.583f ) ),
			Neck = new Vec3( 0, 0, neck ),
			Head = new Vec3( 0, 0, chin + 0.13f * h ),
			Top = new Vec3( 0, 0, t - 0.14f * h ),
			Clavicle = new Vec3( 0, 0.096f * shoulderY, shoulderZ + 0.017f * torso ),
			Shoulder = shoulder,
			Elbow = elbow,
			Wrist = wrist,
			Fingertip = wrist + handDir * hand,
			Hip = hip,
			Knee = knee,
			Ankle = ankle,
			Ball = ball,
			Toe = ball + new Vec3( 0.032f * t, 0, -ball.z ),
		};
	}

	static Report Describe( Layout l, float height, float heads, string style )
	{
		var report = new Report();
		var crotch = l.Hip.z - (l.Pelvis.z - l.Hip.z) / (0.213f - 0.154f) * 0.154f;
		var chin = height - height / heads;
		report.Landmarks["top"] = height;
		report.Landmarks["chin"] = chin;
		report.Landmarks["shoulder"] = l.Shoulder.z;
		report.Landmarks["crotch"] = crotch;
		report.Landmarks["knee"] = l.Knee.z;
		report.Landmarks["ankle"] = l.Ankle.z;
		report.Landmarks["heads"] = heads;
		report.Landmarks["shoulder width"] = l.Shoulder.y * 2f;
		report.Summary = $"{style}, {heads:0.##} heads, {height:0.#} tall: chin {chin:0.#}, shoulders {l.Shoulder.z:0.#} "
			+ $"({l.Shoulder.y * 2f:0.#} apart), crotch {crotch:0.#}, knees {l.Knee.z:0.#}, ankles {l.Ankle.z:0.#}";
		return report;
	}

	static Skeleton BuildBiped( Layout l, bool fingers, bool twist )
	{
		var s = new Skeleton();
		var forward = new Vec3( 1, 0, 0 );

		int Add( string name, int parent, Vec3 head, Vec3 tail ) =>
			s.AddBoneFromPoints( name, parent, head, tail, RigBuild.DefaultUp( tail - head ) );

		var pelvis = Add( "pelvis", -1, l.Pelvis, l.Spine0 );
		var spine0 = Add( "spine_0", pelvis, l.Spine0, l.Spine1 );
		var spine1 = Add( "spine_1", spine0, l.Spine1, l.Spine2 );
		var spine2 = Add( "spine_2", spine1, l.Spine2, l.Neck );
		var neck = Add( "neck_0", spine2, l.Neck, l.Head );
		var headBone = Add( "head", neck, l.Head, l.Top );
		// Face bones (wave 3): brow inner/outer and lid upper/lower parented to head.
		// Fractions of head height, forward +X, up +Z, lateral Y, left +Y.
		// MirrorSide below mirrors every _L bone to _R.
		{ var hc = (l.Head + l.Top) / 2f; var hh = MathF.Max( (l.Top - l.Head).z, 1f );
		Add( "jaw", headBone, hc + new Vec3( 0.2f * hh, 0, -0.35f * hh ), hc + new Vec3( 0.35f * hh, 0, -0.45f * hh ) );
		Add( "eye_L", headBone, hc + new Vec3( 0.25f * hh, 0.18f * hh, 0.15f * hh ), hc + new Vec3( 0.45f * hh, 0.18f * hh, 0.15f * hh ) );
		Add( "brow_inner_L", headBone, hc + new Vec3( 0.30f * hh, 0.08f * hh, 0.32f * hh ), hc + new Vec3( 0.38f * hh, 0.08f * hh, 0.32f * hh ) );
		Add( "brow_outer_L", headBone, hc + new Vec3( 0.28f * hh, 0.28f * hh, 0.30f * hh ), hc + new Vec3( 0.36f * hh, 0.28f * hh, 0.30f * hh ) );
		Add( "lid_upper_L", headBone, hc + new Vec3( 0.25f * hh, 0.18f * hh, 0.20f * hh ), hc + new Vec3( 0.30f * hh, 0.28f * hh, 0.20f * hh ) );
		Add( "lid_lower_L", headBone, hc + new Vec3( 0.25f * hh, 0.18f * hh, 0.10f * hh ), hc + new Vec3( 0.30f * hh, 0.28f * hh, 0.10f * hh ) ); }


		var clavicle = Add( "clavicle_L", spine2, l.Clavicle, l.Shoulder );
		var upperArm = Add( "arm_upper_L", clavicle, l.Shoulder, l.Elbow );
		var lowerArm = Add( "arm_lower_L", upperArm, l.Elbow, l.Wrist );
		var handLength = (l.Fingertip - l.Wrist).Length;
		var handDir = (l.Fingertip - l.Wrist).Normal;
		var hand = Add( "hand_L", lowerArm, l.Wrist, l.Wrist + handDir * (0.5f * handLength) );

		if ( fingers )
			AddFingers( s, hand, l.Wrist, handDir, forward, handLength / 7.2f );

		var upperLeg = Add( "leg_upper_L", pelvis, l.Hip, l.Knee );
		var lowerLeg = Add( "leg_lower_L", upperLeg, l.Knee, l.Ankle );
		var ankle = Add( "ankle_L", lowerLeg, l.Ankle, l.Ball );
		Add( "ball_L", ankle, l.Ball, l.Toe );

		RigBuild.MirrorSide( s, 'L', out _ );

		// After the mirror: a twist bone's name ends in its station number, not its side, so the
		// mirror would not recognise it as a left-side bone.
		if ( twist )
		{
			foreach ( var limb in TwistLimbs )
				RigBuild.AddTwist( s, s.IndexOf( limb ), 2 );
		}

		return SkeletonOrderByChains( s );
	}

	/// <summary>
	/// Fingers in a hand frame: <paramref name="along"/> wrist to middle tip, thumb side
	/// <paramref name="thumbSide"/>, sized by <paramref name="s"/> (1 = the human's 7.2-inch hand).
	/// Names are the human REF's: finger_thumb_0..2, finger_{index,middle,ring,pinky}_meta and _0..2.
	/// </summary>
	static void AddFingers( Skeleton skeleton, int hand, Vec3 wrist, Vec3 along, Vec3 thumbSide, float s )
	{
		var side = (thumbSide - along * Vec3.Dot( thumbSide, along )).Normal;
		var palm = Vec3.Cross( along, side ).Normal;
		var suffix = skeleton.Bones[hand].Name.EndsWith( "_R" ) ? "_R" : "_L";

		int Add( string name, int parent, Vec3 head, Vec3 tail ) =>
			skeleton.AddBoneFromPoints( name + suffix, parent, head, tail, palm );

		// Lane across the palm, knuckle distance from the wrist, three phalanx lengths.
		var rows = new (string Name, float Lane, float Knuckle, float A, float B, float C)[]
		{
			("index", 0.9f, 3.5f, 1.45f, 0.9f, 0.75f),
			("middle", 0.3f, 3.6f, 1.6f, 1.0f, 0.85f),
			("ring", -0.3f, 3.45f, 1.5f, 0.95f, 0.8f),
			("pinky", -0.85f, 3.2f, 1.15f, 0.75f, 0.7f),
		};

		foreach ( var (name, lane, knuckle, a, b, c) in rows )
		{
			var metaHead = wrist + (along * 0.35f + side * (lane * 0.6f)) * s;
			var k0 = wrist + (along * knuckle + side * lane) * s;
			var meta = Add( $"finger_{name}_meta", hand, metaHead, k0 );
			var k1 = k0 + along * (a * s);
			var k2 = k1 + along * (b * s);
			var k3 = k2 + along * (c * s);
			var p0 = Add( $"finger_{name}_0", meta, k0, k1 );
			var p1 = Add( $"finger_{name}_1", p0, k1, k2 );
			Add( $"finger_{name}_2", p1, k2, k3 );
		}

		var thumbDir = (along * 0.55f + side * 0.8f - palm * 0.25f).Normal;
		var t0 = wrist + (along * 0.9f + side * 0.9f - palm * 0.5f) * s;
		var t1 = t0 + thumbDir * (1.6f * s);
		var t2 = t1 + thumbDir * (1.3f * s);
		var t3 = t2 + thumbDir * (1.1f * s);
		var th0 = Add( "finger_thumb_0", hand, t0, t1 );
		var th1 = Add( "finger_thumb_1", th0, t1, t2 );
		Add( "finger_thumb_2", th1, t2, t3 );
	}

	/// <summary>The mirrored right side lands at the end of the list; put each side's bones next to
	/// the centre bone they hang from so the rig tree and the exported node block read limb by limb.
	/// Depth-first from each root, children in the order they were added.</summary>
	static Skeleton SkeletonOrderByChains( Skeleton s )
	{
		var records = RigBuild.Records( s );
		var ordered = new List<RigBuild.Record>( records.Count );

		void Visit( int i )
		{
			ordered.Add( records[i] );
			foreach ( var c in s.Children( i ) )
				Visit( c );
		}

		for ( var i = 0; i < s.Count; i++ )
		{
			if ( s.Bones[i].Parent < 0 )
				Visit( i );
		}

		RigBuild.Rebuild( s, ordered );
		return s;
	}

	// --- quadruped --------------------------------------------------------------------------------

	/// <summary>
	/// A four-legged rig, <paramref name="scale"/> × a 24-inch-at-the-shoulder dog, standing on z 0
	/// facing +x: pelvis, three spine bones, two neck bones, head and jaw, a five-bone tail, and
	/// per side a scapula, upper arm, forearm, hand and toe in front and a thigh, shin, ankle and
	/// ball behind. <paramref name="legs"/> picks the leg (see the class notes).
	/// </summary>
	public static Skeleton Quadruped( float scale = 1f, string legs = "digitigrade" ) => Quadruped( scale, legs, out _ );

	public static Skeleton Quadruped( float scale, string legs, out Report report )
	{
		RequirePositive( scale, "scale" );
		var kind = (legs ?? "digitigrade").Trim().ToLowerInvariant();

		if ( !LegTypes.Contains( kind ) )
			throw new ArgumentException( $"No leg type '{legs}'. Leg types: {string.Join( ", ", LegTypes )}." );

		var s = new Skeleton();
		Vec3 P( float x, float y, float z ) => new Vec3( x, y, z ) * scale;

		int Add( string name, int parent, Vec3 head, Vec3 tail ) =>
			s.AddBoneFromPoints( name, parent, head, tail, RigBuild.DefaultUp( tail - head ) );

		const float hipX = -12f, hipZ = 21f, shoulderX = 10f, shoulderZ = 19f;

		var pelvis = Add( "pelvis", -1, P( hipX, 0, 22f ), P( -6f, 0, 22.5f ) );
		var spine0 = Add( "spine_0", pelvis, P( -6f, 0, 22.5f ), P( 0, 0, 23f ) );
		var spine1 = Add( "spine_1", spine0, P( 0, 0, 23f ), P( 6f, 0, 23.5f ) );
		var spine2 = Add( "spine_2", spine1, P( 6f, 0, 23.5f ), P( 11f, 0, 24f ) );
		var neck0 = Add( "neck_0", spine2, P( 11f, 0, 24f ), P( 15f, 0, 28f ) );
		var neck1 = Add( "neck_1", neck0, P( 15f, 0, 28f ), P( 18f, 0, 31f ) );
		var head = Add( "head", neck1, P( 18f, 0, 31f ), P( 25f, 0, 29f ) );
		Add( "jaw", head, P( 19f, 0, 29.5f ), P( 24.5f, 0, 27.5f ) );
		RigBuild.Chain( s, "tail", pelvis, new[] { P( hipX - 1f, 0, 22.5f ), P( -20f, 0, 21f ), P( -27f, 0, 17f ) }, 5 );

		// Joint heights as fractions of the hip (rear) or shoulder (front) height, x offsets in
		// inches from the hip or shoulder. Each foot sits under its knee: the balance check.
		var (knee, hock, ball, toe) = kind switch
		{
			"plantigrade" => ((1.5f, 0.52f), (-1.0f, 0.06f), (4.5f, 0.015f), (6.5f, 0f)),
			"unguligrade" => ((2.5f, 0.72f), (-3.0f, 0.42f), (-1.0f, 0.08f), (0.5f, 0f)),
			_ => ((3.0f, 0.62f), (-3.0f, 0.30f), (1.0f, 0.04f), (3.5f, 0f)),
		};

		var (elbow, wrist, knuckle, tip) = kind switch
		{
			"plantigrade" => ((-1.0f, 0.55f), (0.5f, 0.08f), (4.0f, 0.02f), (6.0f, 0f)),
			"unguligrade" => ((-1.0f, 0.62f), (0.5f, 0.32f), (0.5f, 0.08f), (1.5f, 0f)),
			_ => ((-1.0f, 0.58f), (1.0f, 0.20f), (2.0f, 0.04f), (3.5f, 0f)),
		};

		const float y = 3.5f;
		var scapula = Add( "scapula_L", spine2, P( shoulderX - 2f, y - 1f, 25f ), P( shoulderX, y, shoulderZ ) );
		var armUpper = Add( "arm_upper_L", scapula, P( shoulderX, y, shoulderZ ), P( shoulderX + elbow.Item1, y, shoulderZ * elbow.Item2 ) );
		var armLower = Add( "arm_lower_L", armUpper, P( shoulderX + elbow.Item1, y, shoulderZ * elbow.Item2 ), P( shoulderX + wrist.Item1, y, shoulderZ * wrist.Item2 ) );
		var hand = Add( "hand_L", armLower, P( shoulderX + wrist.Item1, y, shoulderZ * wrist.Item2 ), P( shoulderX + knuckle.Item1, y, shoulderZ * knuckle.Item2 ) );
		Add( "finger_0_L", hand, P( shoulderX + knuckle.Item1, y, shoulderZ * knuckle.Item2 ), P( shoulderX + tip.Item1, y, MathF.Max( shoulderZ * tip.Item2, 0f ) ) );

		var legUpper = Add( "leg_upper_L", pelvis, P( hipX, y - 0.5f, hipZ ), P( hipX + knee.Item1, y - 0.5f, hipZ * knee.Item2 ) );
		var legLower = Add( "leg_lower_L", legUpper, P( hipX + knee.Item1, y - 0.5f, hipZ * knee.Item2 ), P( hipX + hock.Item1, y - 0.5f, hipZ * hock.Item2 ) );
		var ankle = Add( "ankle_L", legLower, P( hipX + hock.Item1, y - 0.5f, hipZ * hock.Item2 ), P( hipX + ball.Item1, y - 0.5f, hipZ * ball.Item2 ) );
		Add( "ball_L", ankle, P( hipX + ball.Item1, y - 0.5f, hipZ * ball.Item2 ), P( hipX + toe.Item1, y - 0.5f, hipZ * toe.Item2 ) );

		RigBuild.MirrorSide( s, 'L', out _ );
		SkeletonOrderByChains( s );

		report = new Report();
		report.Landmarks["hip"] = hipZ * scale;
		report.Landmarks["shoulder"] = shoulderZ * scale;
		report.Landmarks["knee"] = hipZ * knee.Item2 * scale;
		report.Landmarks["hock"] = hipZ * hock.Item2 * scale;
		report.Landmarks["length"] = (25f - hipX) * scale;
		report.Summary = $"quadruped, {kind} legs, scale {scale:0.##}: hips {hipZ * scale:0.#}, shoulders {shoulderZ * scale:0.#}, "
			+ $"knees {hipZ * knee.Item2 * scale:0.#}, hocks {hipZ * hock.Item2 * scale:0.#}, nose to rump {(25f - hipX) * scale:0.#}";
		return s;
	}

	static void RequirePositive( float value, string what )
	{
		if ( !(value > 0f) || float.IsInfinity( value ) )
			throw new ArgumentOutOfRangeException( what, $"{what}={value}: it has to be a positive number." );
	}


	static Vec3 MinV( Vec3 a, Vec3 b ) => new( MathF.Min( a.x, b.x ), MathF.Min( a.y, b.y ), MathF.Min( a.z, b.z ) );
	static Vec3 MaxV( Vec3 a, Vec3 b ) => new( MathF.Max( a.x, b.x ), MathF.Max( a.y, b.y ), MathF.Max( a.z, b.z ) );

	// --- Lane G: new production templates -----------------------------------------------------

	/// <summary>The six Lane G templates, plus the existing four: <see cref="Names"/> now lists
	/// all ten. Aliases (piscine, winged, multipod, tentacle) are accepted by
	/// <see cref="BuildTemplate"/> but the canonical names below are what a script should use.</summary>
	public static readonly string[] NewTemplateNames = { "fish", "avian", "arachnid", "serpentine", "vehicle_wheeled", "weapon_firearm" };

	/// <summary>Parameters for <see cref="BuildTemplate"/>: scale is a uniform multiplier on the
	/// template's base size (1 = the sizes in the builders below, inches).</summary>
	public sealed class TemplateOptions
	{
		public float Scale = 1f;
		public int SpineCount = 6;
		public int DorsalCount = 3;
		public int SerpentineCount = 12;
		public int LegPairs = 4;
	}

	/// <summary>Build one of the ten templates by name. Unknown names throw listing every name,
	/// so a script typo fails loudly instead of silently producing a human.</summary>
	public static Skeleton BuildTemplate( string name, TemplateOptions options = null )
	{
		options ??= new TemplateOptions();
		RequirePositive( options.Scale, "scale" );

		return (name ?? "").Trim().ToLowerInvariant() switch
		{
			"human" => Human(),
			"citizen" => Citizen(),
			"biped" => Biped(),
			"quadruped" => Quadruped(),
			"fish" or "piscine" => Fish( options.Scale, options.SpineCount, options.DorsalCount ),
			"avian" or "winged" => Avian( options.Scale ),
			"arachnid" or "multipod" => Arachnid( options.Scale, options.LegPairs ),
			"serpentine" or "tentacle" => Serpentine( options.Scale, options.SerpentineCount ),
			"vehicle_wheeled" or "vehicle" => VehicleWheeled( options.Scale ),
			"weapon_firearm" or "weapon" => WeaponFirearm( options.Scale ),
			_ => throw new ArgumentException( $"No rig template '{name}'. Templates: {string.Join( ", ", Names )}, {string.Join( ", ", NewTemplateNames )}." ),
		};
	}

	/// <summary>Scale + centre a template onto a set of bodies, so a template rig fits the body it
	/// was built for instead of the template's base size. Uniform scale only (a rig stretched
	/// non-uniformly would shear its bones); the scale is set by the longest body dimension, and
	/// the template's centre is moved onto the bodies' centre. Returns a new skeleton; the
	/// template passed in is untouched.</summary>
	public static Skeleton FitToBody( Skeleton template, IReadOnlyList<Body> bodies )
	{
		if ( template is null ) throw new ArgumentNullException( nameof( template ) );

		var min = new Vec3( float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity );
		var max = new Vec3( float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity );
		var found = false;

		foreach ( var body in bodies ?? Array.Empty<Body>() )
		{
			if ( body?.Mesh is null )
				continue;

			foreach ( var p in body.Mesh.Positions )
			{
				min = MinV( min, p );
				max = MaxV( max, p );
				found = true;
			}
		}

		if ( !found )
			throw new ArgumentException( "FitToBody needs at least one body with mesh positions." );

		return FitToBounds( template, min, max );
	}

	/// <summary>The bounds form of <see cref="FitToBody"/>: uniform-scale the skeleton so its
	/// longest bind dimension matches the bounds' longest dimension, then centre it on them.</summary>
	public static Skeleton FitToBounds( Skeleton template, Vec3 min, Vec3 max )
	{
		if ( template is null ) throw new ArgumentNullException( nameof( template ) );

		var clone = template.Clone();
		var lo = new Vec3( float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity );
		var hi = new Vec3( float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity );

		for ( var i = 0; i < clone.Count; i++ )
		{
			lo = MinV( lo, clone.HeadWorld( i ) );
			lo = MinV( lo, clone.TailWorld( i ) );
			hi = MaxV( hi, clone.HeadWorld( i ) );
			hi = MaxV( hi, clone.TailWorld( i ) );
		}

		var span = hi - lo;
		var longest = MathF.Max( span.x, MathF.Max( span.y, span.z ) );

		if ( longest < 1e-6f )
			throw new ArgumentException( "FitToBounds needs a skeleton with a non-zero extent." );

		var target = max - min;
		var targetLongest = MathF.Max( target.x, MathF.Max( target.y, target.z ) );
		var factor = targetLongest / longest;
		var centre = (lo + hi) * 0.5f;
		var delta = (min + max) * 0.5f - centre * factor;

		foreach ( var bone in clone.Bones )
		{
			bone.Local = new Xform( bone.Local.X, bone.Local.Y, bone.Local.Z, bone.Local.Origin * factor );
			bone.Length *= factor;

			if ( bone.Parent < 0 )
				bone.Local = new Xform( bone.Local.X, bone.Local.Y, bone.Local.Z, bone.Local.Origin + delta );
		}

		return clone;
	}

	static Skeleton Fish( float s, int spineCount, int dorsalCount )
	{
		if ( spineCount < 1 ) throw new ArgumentOutOfRangeException( nameof( spineCount ), "a fish needs at least 1 spine bone." );
		if ( dorsalCount < 0 ) throw new ArgumentOutOfRangeException( nameof( dorsalCount ), "dorsal count cannot be negative." );

		// Forward +x (head at +x, tail at -x), dorsal +z, lateral +-y. Body 24 in long at scale 1.
		var length = 24f * s;
		var headX = length * 0.5f;
		var tailX = -length * 0.5f;
		var halfWidth = 3f * s;
		var halfDepth = 2.5f * s;
		var k = new Skeleton();

		int Add( string name, int parent, Vec3 head, Vec3 tail ) =>
			k.AddBoneFromPoints( name, parent, head, tail, RigBuild.DefaultUp( tail - head ) );

		var root = Add( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( headX, 0, 0 ) );
		var spine = new int[spineCount + 1];

		for ( var i = 0; i <= spineCount; i++ )
		{
			var x0 = headX - length * i / (spineCount + 1);
			var x1 = headX - length * (i + 1) / (spineCount + 1);
			spine[i] = Add( $"spine_{i}", i == 0 ? root : spine[i - 1], new Vec3( x0, 0, 0 ), new Vec3( x1, 0, 0 ) );
		}

		var head = Add( "head", spine[0], new Vec3( headX, 0, 0 ), new Vec3( headX + 3f * s, 0, 0 ) );
		Add( "jaw", head, new Vec3( headX - 1f * s, 0, -1f * s ), new Vec3( headX + 2f * s, 0, -1.5f * s ) );
		Add( "eye_L", head, new Vec3( headX - 1f * s, halfWidth * 0.4f, 0.8f * s ), new Vec3( headX - 1f * s, halfWidth * 0.7f, 0.8f * s ) );
		Add( "opercle_L", head, new Vec3( headX - 2f * s, halfWidth * 0.5f, 0 ), new Vec3( headX - 3f * s, halfWidth * 0.7f, -0.5f * s ) );
		RigBuild.MirrorSide( k, 'L', out _ );

		var lastSpine = spine[spineCount];
		Add( "caudal_upper", lastSpine, new Vec3( tailX, 0, 0 ), new Vec3( tailX - 3f * s, 0, 1.5f * s ) );
		Add( "caudal_lower", lastSpine, new Vec3( tailX, 0, 0 ), new Vec3( tailX - 3f * s, 0, -1.5f * s ) );
		Add( "pectoral_L", spine[Math.Min( 1, spineCount )], new Vec3( headX * 0.5f, halfWidth, -0.5f * s ), new Vec3( headX * 0.5f - 1f * s, halfWidth + 2f * s, -1f * s ) );
		Add( "pelvic_L", spine[Math.Min( 2, spineCount )], new Vec3( 0, halfWidth, -halfDepth ), new Vec3( -1f * s, halfWidth + 1.5f * s, -halfDepth - 1f * s ) );
		RigBuild.MirrorSide( k, 'L', out _ );

		var mid = spine[spineCount / 2];
		var prev = mid;

		for ( var i = 0; i < dorsalCount; i++ )
		{
			var x0 = -i * 2f * s;
			var x1 = -(i + 1) * 2f * s;
			var z0 = halfDepth + (i == 0 ? 0f : 1.5f * s);
			var z1 = halfDepth + 1.5f * s;
			prev = Add( $"dorsal_{i}", prev, new Vec3( x0, 0, z0 ), new Vec3( x1, 0, z1 ) );
		}

		Add( "anal", spine[Math.Max( 0, spineCount - 1 )], new Vec3( -length * 0.35f, 0, -halfDepth ), new Vec3( -length * 0.4f, 0, -halfDepth - 1.5f * s ) );
		return SkeletonOrderByChains( k );
	}

	static Skeleton Avian( float s )
	{
		// Standing bird, forward +x, up +z, ground at z 0. Wings fold in three segments per side.
		var k = new Skeleton();

		int Add( string name, int parent, Vec3 head, Vec3 tail ) =>
			k.AddBoneFromPoints( name, parent, head, tail, RigBuild.DefaultUp( tail - head ) );

		var pelvis = Add( "pelvis", -1, new Vec3( 0, 0, 18f * s ), new Vec3( 0, 0, 24f * s ) );
		var spine0 = Add( "spine_0", pelvis, new Vec3( 0, 0, 24f * s ), new Vec3( 0, 0, 28f * s ) );
		var neck = Add( "neck", spine0, new Vec3( 0, 0, 28f * s ), new Vec3( 2f * s, 0, 32f * s ) );
		var head = Add( "head", neck, new Vec3( 2f * s, 0, 32f * s ), new Vec3( 5f * s, 0, 33f * s ) );
		Add( "beak", head, new Vec3( 5f * s, 0, 33f * s ), new Vec3( 8f * s, 0, 32.5f * s ) );
		Add( "sternum", spine0, new Vec3( 0, 0, 20f * s ), new Vec3( 2f * s, 0, 16f * s ) );

		var hip = Add( "leg_upper_L", pelvis, new Vec3( 0, 2.5f * s, 18f * s ), new Vec3( 0, 3f * s, 10f * s ) );
		var shin = Add( "leg_lower_L", hip, new Vec3( 0, 3f * s, 10f * s ), new Vec3( 0, 3f * s, 2f * s ) );
		var foot = Add( "talon_L", shin, new Vec3( 0, 3f * s, 2f * s ), new Vec3( 2f * s, 3f * s, 0 ) );
		Add( "toe_L", foot, new Vec3( 2f * s, 3f * s, 0 ), new Vec3( 4f * s, 3f * s, 0 ) );

		var shoulder = Add( "wing_shoulder_L", spine0, new Vec3( 0, 3f * s, 26f * s ), new Vec3( 0, 8f * s, 26f * s ) );
		var elbow = Add( "wing_elbow_L", shoulder, new Vec3( 0, 8f * s, 26f * s ), new Vec3( 0, 14f * s, 24f * s ) );
		var wrist = Add( "wing_wrist_L", elbow, new Vec3( 0, 14f * s, 24f * s ), new Vec3( 0, 20f * s, 22f * s ) );
		var pri = wrist;

		for ( var i = 1; i <= 4; i++ )
			pri = Add( $"feather_pri_{i}", pri, new Vec3( -2f * (i - 1) * s, (20f + 4f * (i - 1)) * s, 22f * s ), new Vec3( -2f * i * s, (20f + 4f * i) * s, 22f * s ) );

		var sec = elbow;

		for ( var i = 1; i <= 3; i++ )
			sec = Add( $"feather_sec_{i}", sec, new Vec3( -2f * (i - 1) * s, (14f + 4f * (i - 1)) * s, (24f - i) * s ), new Vec3( -2f * i * s, (14f + 4f * i) * s, (24f - i) * s ) );

		var tail0 = Add( "tail_0", pelvis, new Vec3( -2f * s, 0, 18f * s ), new Vec3( -6f * s, 0, 18f * s ) );
		var tail1 = Add( "tail_1", tail0, new Vec3( -6f * s, 0, 18f * s ), new Vec3( -10f * s, 0, 18f * s ) );
		Add( "tail_fan_L", tail1, new Vec3( -10f * s, 0, 18f * s ), new Vec3( -13f * s, 2f * s, 18f * s ) );
		RigBuild.MirrorSide( k, 'L', out _ );
		return SkeletonOrderByChains( k );
	}

	static Skeleton Arachnid( float s, int pairs )
	{
		if ( pairs < 3 || pairs > 5 ) throw new ArgumentOutOfRangeException( nameof( pairs ), $"arachnid leg pairs must be 3, 4 or 5, not {pairs}." );

		var k = new Skeleton();

		int Add( string name, int parent, Vec3 head, Vec3 tail ) =>
			k.AddBoneFromPoints( name, parent, head, tail, RigBuild.DefaultUp( tail - head ) );

		var body = Add( "cephalothorax", -1, new Vec3( 0, 0, 6f * s ), new Vec3( 3f * s, 0, 6f * s ) );
		Add( "abdomen", body, new Vec3( -1f * s, 0, 6f * s ), new Vec3( -5f * s, 0, 6f * s ) );
		Add( "chelicera_L", body, new Vec3( 3f * s, 1f * s, 5f * s ), new Vec3( 5f * s, 1.5f * s, 4f * s ) );
		Add( "pedipalp_L", body, new Vec3( 2f * s, 2f * s, 5f * s ), new Vec3( 4f * s, 3f * s, 3f * s ) );

		for ( var p = 1; p <= pairs; p++ )
		{
			var fx = (2f - (p - 1) * 1.5f) * s;
			var back = p > pairs / 2 ? -0.5f : 0.5f;
			var coxa = Add( $"leg{p}_coxa_L", body, new Vec3( fx, 2f * s, 5f * s ), new Vec3( fx, 3.5f * s, 5f * s ) );
			var femur = Add( $"leg{p}_femur_L", coxa, new Vec3( fx, 3.5f * s, 5f * s ), new Vec3( fx + back * s, 6f * s, 3f * s ) );
			var patella = Add( $"leg{p}_patella_L", femur, new Vec3( fx + back * s, 6f * s, 3f * s ), new Vec3( fx, 7f * s, 2f * s ) );
			var tibia = Add( $"leg{p}_tibia_L", patella, new Vec3( fx, 7f * s, 2f * s ), new Vec3( fx, 8f * s, 0.5f * s ) );
			var tarsus = Add( $"leg{p}_tarsus_L", tibia, new Vec3( fx, 8f * s, 0.5f * s ), new Vec3( fx + 0.5f * s, 8.5f * s, 0 ) );
			// IK targets: bones at the feet, parented to the body so an IK solve can aim at them
			// without them inheriting the leg chain's own motion.
			Add( $"leg{p}_ik_L", body, new Vec3( fx + 0.5f * s, 8.5f * s, 0 ), new Vec3( fx + 0.5f * s, 8.5f * s, 0.5f * s ) );
			_ = tarsus;
		}

		RigBuild.MirrorSide( k, 'L', out _ );
		return SkeletonOrderByChains( k );
	}

	static Skeleton Serpentine( float s, int count )
	{
		if ( count < 8 || count > 24 ) throw new ArgumentOutOfRangeException( nameof( count ), $"serpentine count must be 8..24, not {count}." );

		var k = new Skeleton();

		int Add( string name, int parent, Vec3 head, Vec3 tail ) =>
			k.AddBoneFromPoints( name, parent, head, tail, RigBuild.DefaultUp( tail - head ) );

		var prev = Add( "root", -1, new Vec3( 0, 0, 2f * s ), new Vec3( 2f * s, 0, 2f * s ) );
		var segLen = 2f * s;

		for ( var i = 0; i < count; i++ )
		{
			var x0 = 2f * s + i * segLen;
			var y0 = MathF.Sin( i * 0.7f ) * 1.5f * s;
			var x1 = x0 + segLen;
			var y1 = MathF.Sin( (i + 1) * 0.7f ) * 1.5f * s;
			prev = Add( $"seg_{i}", prev, new Vec3( x0, y0, 2f * s ), new Vec3( x1, y1, 2f * s ) );
			// SoftBone tag: the chain is meant to be spring-simulated, not keyed stiff.
			k.Bones[prev].Soft = new SoftBone { Stiffness = 60f, Damping = 0.5f, Weight = 0f, MaxAngle = 80f };
		}

		return k;
	}

	static Skeleton VehicleWheeled( float s )
	{
		var k = new Skeleton();

		int Add( string name, int parent, Vec3 head, Vec3 tail ) =>
			k.AddBoneFromPoints( name, parent, head, tail, RigBuild.DefaultUp( tail - head ) );

		var chassis = Add( "chassis", -1, new Vec3( 0, 0, 3f * s ), new Vec3( 4f * s, 0, 3f * s ) );
		var roll = Add( "body_roll", chassis, new Vec3( 0, 0, 3f * s ), new Vec3( 0, 0, 5f * s ) );
		Add( "steer_L", chassis, new Vec3( 3.5f * s, 2.5f * s, 1.5f * s ), new Vec3( 3.5f * s, 3.5f * s, 1.5f * s ) );
		Add( "steer_R", chassis, new Vec3( 3.5f * s, -2.5f * s, 1.5f * s ), new Vec3( 3.5f * s, -3.5f * s, 1.5f * s ) );
		Add( "wheel_FL", chassis, new Vec3( 3.5f * s, 2f * s, 1.5f * s ), new Vec3( 3.5f * s, 3.2f * s, 1.5f * s ) );
		Add( "wheel_FR", chassis, new Vec3( 3.5f * s, -2f * s, 1.5f * s ), new Vec3( 3.5f * s, -3.2f * s, 1.5f * s ) );
		Add( "wheel_RL", chassis, new Vec3( -3.5f * s, 2f * s, 1.5f * s ), new Vec3( -3.5f * s, 3.2f * s, 1.5f * s ) );
		Add( "wheel_RR", chassis, new Vec3( -3.5f * s, -2f * s, 1.5f * s ), new Vec3( -3.5f * s, -3.2f * s, 1.5f * s ) );
		Add( "door_L", roll, new Vec3( 0, 3f * s, 3f * s ), new Vec3( 0, 3.2f * s, 4f * s ) );
		Add( "door_R", roll, new Vec3( 0, -3f * s, 3f * s ), new Vec3( 0, -3.2f * s, 4f * s ) );
		Add( "hood", chassis, new Vec3( 4f * s, 0, 4f * s ), new Vec3( 6f * s, 0, 4f * s ) );
		Add( "trunk", chassis, new Vec3( -4f * s, 0, 4f * s ), new Vec3( -6f * s, 0, 4f * s ) );
		return k;
	}

	static Skeleton WeaponFirearm( float s )
	{
		var k = new Skeleton();

		int Add( string name, int parent, Vec3 head, Vec3 tail ) =>
			k.AddBoneFromPoints( name, parent, head, tail, RigBuild.DefaultUp( tail - head ) );

		var root = Add( "weapon_root", -1, new Vec3( 0, 0, 0 ), new Vec3( 4f * s, 0, 0 ) );
		Add( "muzzle", root, new Vec3( 6f * s, 0, 0 ), new Vec3( 8f * s, 0, 0 ) );
		Add( "slide_bolt", root, new Vec3( 2f * s, 0, 1f * s ), new Vec3( 5f * s, 0, 1f * s ) );
		Add( "trigger", root, new Vec3( 1f * s, 0, -1f * s ), new Vec3( 1f * s, 0, -2f * s ) );
		Add( "magazine", root, new Vec3( 1.5f * s, 0, -1f * s ), new Vec3( 1f * s, 0, -3f * s ) );
		Add( "ChargingHandle", root, new Vec3( -1f * s, 0, 1f * s ), new Vec3( -2f * s, 0, 1f * s ) );
		Add( "eject_port", root, new Vec3( 3f * s, 1f * s, 0.5f * s ), new Vec3( 4f * s, 1f * s, 0.5f * s ) );
		Add( "hand_ik_L", root, new Vec3( 3f * s, 0, -2f * s ), new Vec3( 3.5f * s, 0, -2f * s ) );
		return k;
	}
}
