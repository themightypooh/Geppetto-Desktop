using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Clean-room native .riganim Base Motion Library generator for Camhead.
/// Uses zero Citizen/s&amp;box animation assets. Instead, poses are authored from model-space
/// biomechanics (pelvis trajectories, spine counter-torsion, TwoBoneIk foot/hand targets,
/// hydraulic piston telescoping, camera shutter/iris/focus channels, and ChainSpring secondary lag)
/// and serialized to standard Marionette .riganim files via <see cref="RigAnimWriter"/>.
/// </summary>
public static class BaseMotionLibrary
{
	public sealed record Entry(
		string Id,
		string Title,
		string FilePath,
		RigAnimFile Clip,
		bool Loop,
		string LintSummary );

	internal readonly record struct PistonLink( int Cyl, int Rod, int Sleeve, int TargetBone, Vec3 TargetLocalAnchor );


	static Xform DeltaBetweenDirs( Vec3 from, Vec3 to )
	{
		if ( from.Length < 0.001f || to.Length < 0.001f )
			return Xform.Identity;
		from = from.Normal;
		to = to.Normal;
		var dot = Math.Clamp( Vec3.Dot( from, to ), -1f, 1f );
		if ( dot > 0.99999f )
			return Xform.Identity;
		if ( dot < -0.99999f )
		{
			var perpendicular = Vec3.Cross( from, MathF.Abs( from.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ) );
			return Xform.Rotate( perpendicular.Normal, MathF.PI );
		}
		return Xform.Rotate( Vec3.Cross( from, to ).Normal, MathF.Acos( dot ) );
	}

	public static List<Entry> BuildAll( Skeleton rig, string clipsDir )
	{
		Directory.CreateDirectory( clipsDir );

		var builders = new (string Id, string Title, Func<Skeleton, RigAnimFile> Build)[]
		{
			("camhead_idle_breathe",  "1: Idle Breathe & Scan",   BuildIdleBreathe),
			("camhead_look_scan",     "2: Optical Surveillance",  BuildLookScan),
			("camhead_walk_cycle",    "3: Stride Walk Cycle",     BuildWalkCycle),
			("camhead_run_cycle",     "4: Tactical Sprint Cycle", BuildRunCycle),
			("camhead_reach_inspect", "5: Reach & Lens Inspect",  BuildReachInspect),
			("camhead_jump_land",     "6: Jump & Shock Absorb",   BuildJumpLand),
		};

		var list = new List<Entry>( builders.Length );
		foreach ( var (id, title, build) in builders )
		{
			var clip = build( rig );
			var filePath = Path.Combine( clipsDir, id + ".riganim" );
			File.WriteAllText( filePath, RigAnimWriter.Write( clip ) );

			var lint = new MotionLint( rig, clip ).Run( new MotionLint.Options { Loop = true } );
			int pops = lint.Of( "pop" ).Count();
			int loopFails = lint.Of( "loop" ).Count( f => f.Fail );
			string summary = pops == 0 && loopFails == 0
				? "clean (0 pops, seamless loop)"
				: $"pops={pops}, loopSeam={loopFails}";

			list.Add( new Entry( id, title, filePath, clip, true, summary ) );
		}

		return list;
	}

	// -------------------------------------------------------------------------
	// 1. IDLE BREATHE & MICRO-SCAN (90 frames @ 30fps, 3.0s loop)
	// -------------------------------------------------------------------------
	static RigAnimFile BuildIdleBreathe( Skeleton rig )
	{
		const int frames = 90;
		const int fps = 30;
		var rigPose = new PoseRig( rig );
		var sampled = new List<Xform[]>( frames + 1 );

		for ( int f = 0; f <= frames; f++ )
		{
			float u = (float)f / frames;
			float phase = u * MathF.PI * 2f;
			float phase2 = phase * 2f;

			rigPose.ResetToBind();

			// Subtle weight shift and breathing bob on pelvis while feet stay planted
			float pelvisZ = -0.55f + 0.35f * MathF.Sin( phase );
			float pelvisY = 0.45f * MathF.Sin( phase );
			float pelvisRoll = 1.2f * MathF.Sin( phase );
			float pelvisYaw = 2.0f * MathF.Cos( phase );
			rigPose.MoveAndTurnPelvis( new Vec3( 0f, pelvisY, pelvisZ ), pelvisRoll, 1.5f, pelvisYaw );

			// Spine counter-balance and respiration expansion
			float breathePitch = -1.8f * MathF.Sin( phase );
			rigPose.RotateModel( "spine_0", -pelvisRoll * 0.4f, 1.0f + breathePitch * 0.4f, -pelvisYaw * 0.3f );
			rigPose.RotateModel( "spine_1", -pelvisRoll * 0.4f, 1.2f + breathePitch * 0.5f, -pelvisYaw * 0.3f );
			rigPose.RotateModel( "spine_2", -pelvisRoll * 0.2f, 1.0f + breathePitch * 0.6f, -pelvisYaw * 0.4f );

			// Camera head micro-scan
			float headYaw = 6.0f * MathF.Sin( phase ) + 2.5f * MathF.Sin( phase2 );
			float headPitch = -2.0f * MathF.Cos( phase );
			rigPose.RotateModel( "neck_0", 0f, headPitch * 0.35f, headYaw * 0.35f );
			rigPose.RotateModel( "head", 0f, headPitch * 0.65f, headYaw * 0.65f );

			// Relaxed ready arms (brought down from bind A-pose via TwoBoneIk)
			rigPose.RotateModel( "clavicle_L", -4f, 0f, 0f );
			rigPose.RotateModel( "clavicle_R",  4f, 0f, 0f );
			float armSwing = 0.6f * MathF.Sin( phase );
			rigPose.SolveArmIk( left: true,  new Vec3( 3.2f + armSwing, -9.2f, -11.5f - 0.3f * MathF.Sin( phase ) ) );
			rigPose.SolveArmIk( left: false, new Vec3( 3.2f - armSwing,  9.2f, -11.5f - 0.3f * MathF.Sin( phase ) ) );
			rigPose.CurlFingers( left: true,  18f );
			rigPose.CurlFingers( left: false, 18f );

			// Planted feet in model space (zero foot skate)
			rigPose.SolveLegIk( left: true,  Vec3.Zero, 0f, 0f );
			rigPose.SolveLegIk( left: false, Vec3.Zero, 0f, 0f );

			// Camera lens focus ring + quick shutter blink around frame 45
			float blink = MathF.Exp( -MathF.Pow( (f - 45f) / 2.2f, 2f ) );
			rigPose.PoseCameraOptics(
				focusDeg: 25f * MathF.Sin( phase ),
				shutterClose: blink * 0.85f,
				apertureClose: 0.2f + 0.15f * MathF.Sin( phase2 ) );

			rigPose.SolvePistons();
			sampled.Add( rigPose.CloneLocal() );
		}

		return BakeToClip( rig, sampled, fps, frames, loop: true );
	}

	// -------------------------------------------------------------------------
	// 2. OPTICAL SURVEILLANCE SCAN (90 frames @ 30fps, 3.0s loop)
	// -------------------------------------------------------------------------
	static RigAnimFile BuildLookScan( Skeleton rig )
	{
		const int frames = 90;
		const int fps = 30;
		var rigPose = new PoseRig( rig );
		var sampled = new List<Xform[]>( frames + 1 );

		for ( int f = 0; f <= frames; f++ )
		{
			float u = (float)f / frames;
			float phase = u * MathF.PI * 2f;

			// Smooth plateau scan wave: holds slightly at left and right extremes
			float scan = MathF.Sin( phase ) + 0.22f * MathF.Sin( phase * 3f );
			scan = Math.Clamp( scan / 1.15f, -1f, 1f );

			rigPose.ResetToBind();

			// Shift hips toward the scan direction
			float pelvisY = 1.4f * scan;
			float pelvisZ = -1.1f - 0.4f * MathF.Abs( scan );
			float pelvisYaw = 8f * scan;
			rigPose.MoveAndTurnPelvis( new Vec3( 0.3f * MathF.Abs( scan ), pelvisY, pelvisZ ), 2.2f * scan, 3.0f, pelvisYaw );

			// Torso turns into the scan
			rigPose.RotateModel( "spine_0", 0f, 2.0f, 5f * scan );
			rigPose.RotateModel( "spine_1", 0f, 2.0f, 7f * scan );
			rigPose.RotateModel( "spine_2", 0f, 1.5f, 9f * scan );

			// Camera head leads the turn + pitches up/down across the sweep
			float lookPitch = -6f * MathF.Sin( phase * 2f );
			rigPose.RotateModel( "neck_0", -2f * scan, lookPitch * 0.35f, 10f * scan );
			rigPose.RotateModel( "head",   -3f * scan, lookPitch * 0.65f, 18f * scan );

			// Ready arms counterbalance the torso turn
			rigPose.SolveArmIk( left: true,  new Vec3( 4.0f - 1.8f * scan, -8.8f, -10.5f ) );
			rigPose.SolveArmIk( left: false, new Vec3( 4.0f + 1.8f * scan,  8.8f, -10.5f ) );
			rigPose.CurlFingers( left: true,  22f );
			rigPose.CurlFingers( left: false, 22f );

			// Feet stay planted on the floor
			rigPose.SolveLegIk( left: true,  Vec3.Zero, 0f, 0f );
			rigPose.SolveLegIk( left: false, Vec3.Zero, 0f, 0f );

			// Lens focuses and squints when holding at the left/right extremes
			float lockOn = MathF.Pow( MathF.Abs( scan ), 2f );
			rigPose.PoseCameraOptics(
				focusDeg: 65f * scan,
				shutterClose: 0.45f * lockOn,
				apertureClose: 0.65f * lockOn );

			rigPose.SolvePistons();
			sampled.Add( rigPose.CloneLocal() );
		}

		return BakeToClip( rig, sampled, fps, frames, loop: true );
	}

	// -------------------------------------------------------------------------
	// 3. STRIDE WALK CYCLE (36 frames @ 30fps, 1.2s loop)
	// -------------------------------------------------------------------------
	static RigAnimFile BuildWalkCycle( Skeleton rig )
	{
		const int frames = 36;
		const int fps = 30;
		var rigPose = new PoseRig( rig );
		var sampled = new List<Xform[]>( frames + 1 );

		const float strideHalf = 7.2f;
		const float stepLift = 3.2f;

		for ( int f = 0; f <= frames; f++ )
		{
			float u = (float)f / frames;
			float phaseL = u * MathF.PI * 2f;
			float phaseR = phaseL + MathF.PI;

			rigPose.ResetToBind();

			// Double-frequency vertical bob + lateral weight shift over stance leg
			float bobZ = -1.25f + 0.85f * MathF.Cos( phaseL * 2f );
			float swayY = -0.95f * MathF.Sin( phaseL );
			float hipRoll = -2.8f * MathF.Sin( phaseL );
			float hipYaw = -5.5f * MathF.Cos( phaseL );
			rigPose.MoveAndTurnPelvis( new Vec3( 0f, swayY, bobZ ), hipRoll, 3.5f, hipYaw );

			// Spine counter-rotation and forward walk lean
			float spineYaw = 4.2f * MathF.Cos( phaseL );
			rigPose.RotateModel( "spine_0", -hipRoll * 0.4f, 1.5f, spineYaw * 0.6f );
			rigPose.RotateModel( "spine_1", -hipRoll * 0.4f, 1.5f, spineYaw * 0.8f );
			rigPose.RotateModel( "spine_2", -hipRoll * 0.3f, 1.2f, spineYaw * 1.0f );

			// Head stays stabilized on the horizon
			rigPose.RotateModel( "neck_0", 0f, -1.5f, -spineYaw * 0.45f );
			rigPose.RotateModel( "head",   0f, -2.0f, -spineYaw * 0.55f );

			// Foot trajectories: flat stance from +strideHalf to -strideHalf, lifted arc during swing
			var (footOffL, footPitchL, toePitchL) = FootStepTrajectory( phaseL, strideHalf, stepLift );
			var (footOffR, footPitchR, toePitchR) = FootStepTrajectory( phaseR, strideHalf, stepLift );
			rigPose.SolveLegIk( left: true,  footOffL, footPitchL, toePitchL );
			rigPose.SolveLegIk( left: false, footOffR, footPitchR, toePitchR );

			// Opposite-phase arm swing
			float swingL = -5.5f * MathF.Cos( phaseL );
			float swingR = -5.5f * MathF.Cos( phaseR );
			rigPose.SolveArmIk( left: true,  new Vec3( 2.8f + swingL, -9.2f, -10.8f + 0.9f * MathF.Abs( MathF.Cos( phaseL ) ) ) );
			rigPose.SolveArmIk( left: false, new Vec3( 2.8f + swingR,  9.2f, -10.8f + 0.9f * MathF.Abs( MathF.Cos( phaseR ) ) ) );
			rigPose.CurlFingers( left: true,  25f );
			rigPose.CurlFingers( left: false, 25f );

			rigPose.PoseCameraOptics(
				focusDeg: 15f * MathF.Sin( phaseL ),
				shutterClose: 0.1f,
				apertureClose: 0.25f );

			rigPose.SolvePistons();
			sampled.Add( rigPose.CloneLocal() );
		}

		return BakeToClip( rig, sampled, fps, frames, loop: true );
	}

	// -------------------------------------------------------------------------
	// 4. TACTICAL SPRINT CYCLE (24 frames @ 30fps, 0.8s loop)
	// -------------------------------------------------------------------------
	static RigAnimFile BuildRunCycle( Skeleton rig )
	{
		const int frames = 24;
		const int fps = 30;
		var rigPose = new PoseRig( rig );
		var sampled = new List<Xform[]>( frames + 1 );

		const float strideHalf = 10.5f;
		const float stepLift = 6.2f;

		for ( int f = 0; f <= frames; f++ )
		{
			float u = (float)f / frames;
			float phaseL = u * MathF.PI * 2f;
			float phaseR = phaseL + MathF.PI;

			rigPose.ResetToBind();

			// Deeper crouch + flight phase bounce
			float bobZ = -2.4f + 1.65f * MathF.Cos( phaseL * 2f );
			float swayY = -0.75f * MathF.Sin( phaseL );
			float hipRoll = -3.8f * MathF.Sin( phaseL );
			float hipYaw = -8.5f * MathF.Cos( phaseL );
			rigPose.MoveAndTurnPelvis( new Vec3( 0.8f, swayY, bobZ ), hipRoll, 9.0f, hipYaw );

			// Forward sprint lean + strong chest counter-twist
			float spineYaw = 6.5f * MathF.Cos( phaseL );
			rigPose.RotateModel( "spine_0", -hipRoll * 0.4f, 3.0f, spineYaw * 0.6f );
			rigPose.RotateModel( "spine_1", -hipRoll * 0.4f, 3.0f, spineYaw * 0.9f );
			rigPose.RotateModel( "spine_2", -hipRoll * 0.3f, 2.5f, spineYaw * 1.1f );

			// Stabilized camera head pitching back to stay level with horizon
			rigPose.RotateModel( "neck_0", 0f, -6.5f, -spineYaw * 0.5f );
			rigPose.RotateModel( "head",   0f, -8.5f, -spineYaw * 0.6f );

			// High-knee sprint foot trajectories
			var (footOffL, footPitchL, toePitchL) = FootStepTrajectory( phaseL, strideHalf, stepLift );
			var (footOffR, footPitchR, toePitchR) = FootStepTrajectory( phaseR, strideHalf, stepLift );
			rigPose.SolveLegIk( left: true,  footOffL, footPitchL, toePitchL );
			rigPose.SolveLegIk( left: false, footOffR, footPitchR, toePitchR );

			// Pumped bent-elbow arm drive
			float pumpL = -8.2f * MathF.Cos( phaseL );
			float pumpR = -8.2f * MathF.Cos( phaseR );
			rigPose.SolveArmIk( left: true,  new Vec3( 6.2f + pumpL, -8.2f, -5.5f + 2.2f * (-MathF.Cos( phaseL )) ) );
			rigPose.SolveArmIk( left: false, new Vec3( 6.2f + pumpR,  8.2f, -5.5f + 2.2f * (-MathF.Cos( phaseR )) ) );
			rigPose.CurlFingers( left: true,  55f );
			rigPose.CurlFingers( left: false, 55f );

			rigPose.PoseCameraOptics(
				focusDeg: 35f * MathF.Sin( phaseL ),
				shutterClose: 0.35f,
				apertureClose: 0.5f );

			rigPose.SolvePistons();
			sampled.Add( rigPose.CloneLocal() );
		}

		return BakeToClip( rig, sampled, fps, frames, loop: true );
	}

	// -------------------------------------------------------------------------
	// 5. REACH & LENS INSPECT (60 frames @ 30fps, 2.0s loop)
	// -------------------------------------------------------------------------
	static RigAnimFile BuildReachInspect( Skeleton rig )
	{
		const int frames = 60;
		const int fps = 30;
		var rigPose = new PoseRig( rig );
		var sampled = new List<Xform[]>( frames + 1 );

		for ( int f = 0; f <= frames; f++ )
		{
			float u = (float)f / frames;
			// Smooth bell envelope with anticipation dip at u=0.12 and inspect hold at u=0.35..0.65
			float reach = SmoothStep( 0.14f, 0.34f, u ) * (1f - SmoothStep( 0.66f, 0.90f, u ));
			float antic = MathF.Exp( -MathF.Pow( (u - 0.10f) / 0.05f, 2f ) ) * (1f - reach);
			float inspectOsc = reach * MathF.Sin( (u - 0.35f) * MathF.PI * 6f );

			rigPose.ResetToBind();

			// Weight shifts forward onto right foot during reach, winds back slightly on anticipation
			float pelvisX = 1.6f * reach - 0.6f * antic;
			float pelvisY = -0.9f * reach + 0.3f * antic;
			float pelvisZ = -1.1f * reach - 0.5f * antic;
			rigPose.MoveAndTurnPelvis( new Vec3( pelvisX, pelvisY, pelvisZ ), -1.5f * reach, 3.5f * reach - 1.5f * antic, 6f * reach );

			// Upper torso leans in to inspect
			rigPose.RotateModel( "spine_0", 0f, 3.0f * reach - 1.5f * antic, 3.5f * reach );
			rigPose.RotateModel( "spine_1", 0f, 4.0f * reach - 1.5f * antic, 4.5f * reach );
			rigPose.RotateModel( "spine_2", 0f, 4.5f * reach - 1.0f * antic, 5.5f * reach );

			// Camera head tilts down and in toward the right hand
			rigPose.RotateModel( "neck_0",  2.5f * reach, 5.0f * reach, -5.0f * reach );
			rigPose.RotateModel( "head",    4.5f * reach + 2.0f * inspectOsc, 7.5f * reach, -8.5f * reach );

			// Left arm stays in counterbalance ready pose
			rigPose.SolveArmIk( left: true, new Vec3( 2.8f - 1.5f * reach, -9.2f, -11.2f ) );
			rigPose.CurlFingers( left: true, 20f );

			// Right arm anticipates back/down, then reaches forward/up/inward to the inspection point
			var restHandR = new Vec3( 3.2f, 9.2f, -11.2f );
			var anticHandR = new Vec3( 0.8f, 10.2f, -12.8f );
			var reachHandR = new Vec3( 14.5f, 4.2f, 1.5f + 0.6f * inspectOsc );
			var targetR = Vec3.Lerp( Vec3.Lerp( restHandR, anticHandR, antic ), reachHandR, reach );
			rigPose.SolveArmIk( left: false, targetR );
			rigPose.CurlFingers( left: false, 18f + 38f * reach + 10f * inspectOsc );

			// Feet stay planted
			rigPose.SolveLegIk( left: true,  Vec3.Zero, 0f, 0f );
			rigPose.SolveLegIk( left: false, Vec3.Zero, 0f, 0f );

			// Lens zooms/focuses rapidly while inspecting
			rigPose.PoseCameraOptics(
				focusDeg: 95f * reach + 25f * inspectOsc,
				shutterClose: 0.55f * reach,
				apertureClose: 0.75f * reach );

			rigPose.SolvePistons();
			sampled.Add( rigPose.CloneLocal() );
		}

		return BakeToClip( rig, sampled, fps, frames, loop: true );
	}

	// -------------------------------------------------------------------------
	// 6. JUMP & SHOCK ABSORB (48 frames @ 30fps, 1.6s loop)
	// -------------------------------------------------------------------------
	static RigAnimFile BuildJumpLand( Skeleton rig )
	{
		const int frames = 48;
		const int fps = 30;
		var rigPose = new PoseRig( rig );
		var sampled = new List<Xform[]>( frames + 1 );

		for ( int f = 0; f <= frames; f++ )
		{
			float u = (float)f / frames;

			// 4 beats:
			//   0.00..0.25: anticipation crouch (pelvis -6.2 in, feet planted)
			//   0.25..0.62: airborne arc (pelvis up to +9.5 in, feet lifted +5.5 in)
			//   0.62..0.82: landing shock compression (pelvis -7.2 in, feet planted)
			//   0.82..1.00: hydraulic recovery to rest
			float crouch = MathF.Sin( Math.Clamp( u / 0.25f, 0f, 1f ) * MathF.PI );
			if ( u > 0.25f ) crouch = 0f;

			float airU = Math.Clamp( (u - 0.24f) / 0.38f, 0f, 1f );
			float air = u >= 0.24f && u <= 0.62f ? MathF.Sin( airU * MathF.PI ) : 0f;

			float landU = Math.Clamp( (u - 0.60f) / 0.28f, 0f, 1f );
			float land = u >= 0.60f && u <= 0.88f ? MathF.Sin( landU * MathF.PI ) : 0f;

			rigPose.ResetToBind();

			float pelvisZ = -0.5f - 6.0f * crouch + 10.0f * air - 7.0f * land;
			float pelvisX = -1.0f * crouch + 0.8f * air - 1.2f * land;
			float torsoPitch = 8.0f * crouch - 4.0f * air + 10.0f * land;

			rigPose.MoveAndTurnPelvis( new Vec3( pelvisX, 0f, pelvisZ ), 0f, torsoPitch * 0.4f, 0f );
			rigPose.RotateModel( "spine_0", 0f, torsoPitch * 0.25f, 0f );
			rigPose.RotateModel( "spine_1", 0f, torsoPitch * 0.25f, 0f );
			rigPose.RotateModel( "spine_2", 0f, torsoPitch * 0.25f, 0f );
			rigPose.RotateModel( "neck_0",  0f, -torsoPitch * 0.35f, 0f );
			rigPose.RotateModel( "head",    0f, -torsoPitch * 0.45f, 0f );

			// Arms swing back in crouch, thrust up in air, absorb out/down on landing
			var armRest = new Vec3( 3.2f, 9.2f, -11.2f );
			var armCrouch = new Vec3( -3.5f, 9.8f, -13.0f );
			var armAir = new Vec3( 7.5f, 10.5f, -2.5f );
			var armLand = new Vec3( 5.5f, 11.2f, -12.5f );

			var armR = armRest
				+ (armCrouch - armRest) * crouch
				+ (armAir - armRest) * air
				+ (armLand - armRest) * land;
			var armL = new Vec3( armR.x, -armR.y, armR.z );

			rigPose.SolveArmIk( left: true,  armL );
			rigPose.SolveArmIk( left: false, armR );
			rigPose.CurlFingers( left: true,  25f + 35f * air );
			rigPose.CurlFingers( left: false, 25f + 35f * air );

			// Feet stay planted on the ground during crouch and landing, tuck up in the air
			var footL = new Vec3( -1.5f * air, 0f, 6.2f * air );
			var footR = new Vec3( -0.5f * air, 0f, 5.4f * air );
			float footPitch = -22f * air;
			rigPose.SolveLegIk( left: true,  footL, footPitch, -footPitch * 0.6f );
			rigPose.SolveLegIk( left: false, footR, footPitch, -footPitch * 0.6f );

			rigPose.PoseCameraOptics(
				focusDeg: 40f * (crouch - air + land),
				shutterClose: 0.55f * (crouch + land),
				apertureClose: 0.65f * land );

			rigPose.SolvePistons();
			sampled.Add( rigPose.CloneLocal() );
		}

		return BakeToClip( rig, sampled, fps, frames, loop: true );
	}

	// -------------------------------------------------------------------------
	// Secondary Spring Bake + Track Serialization
	// -------------------------------------------------------------------------
	static RigAnimFile BakeToClip( Skeleton rig, List<Xform[]> localFrames, int fps, int frameCount, bool loop )
	{
		// Run Effigy.Kernel's ChainSpring over Camhead's springy mechanical parts (antenna + shoulder pads)
		var soft = new Dictionary<int, SoftBone>();
		var springy = ChainSpring.FindPreset( "springy" )?.ToSoftBone() ?? new SoftBone { Stiffness = 300f, Damping = 0.003f, Weight = 0.1f, MaxAngle = 30f };
		var stiff = ChainSpring.FindPreset( "stiff" )?.ToSoftBone() ?? new SoftBone { Stiffness = 500f, Damping = 0.0001f, Weight = 0.05f, MaxAngle = 15f };

		int antenna = rig.IndexOf( "antenna" );
		if ( antenna >= 0 ) soft[antenna] = springy.Clone();

		int padL = rig.IndexOf( "shoulder_pad_L" );
		if ( padL >= 0 ) soft[padL] = stiff.Clone();

		int padR = rig.IndexOf( "shoulder_pad_R" );
		if ( padR >= 0 ) soft[padR] = stiff.Clone();

		var baked = soft.Count > 0
			? ChainSpring.Bake( rig, localFrames, soft, fps, loop: loop )
			: localFrames;

		// Ensure the loop seam is exact on frameCount == frame 0
		if ( loop && baked.Count > frameCount )
			baked[frameCount] = (Xform[])baked[0].Clone();

		var clip = new RigAnimFile
		{
			SourceModel = "models/players/camhead.vmdl",
			FrameRate = fps,
			FrameCount = frameCount + 1,
		};

		for ( int b = 0; b < rig.Count; b++ )
		{
			var bind = rig.Bones[b].Local;
			bool moves = false;
			for ( int f = 0; f <= frameCount; f++ )
			{
				var cur = baked[f][b];
				if ( (cur.Origin - bind.Origin).LengthSquared > 1e-6f ||
					 (cur.X - bind.X).LengthSquared > 1e-6f ||
					 (cur.Y - bind.Y).LengthSquared > 1e-6f )
				{
					moves = true;
					break;
				}
			}

			if ( !moves ) continue;

			var track = new RigTrack
			{
				Name = rig.Bones[b].Name,
				Kind = RigTrackKind.Bone,
			};

			for ( int f = 0; f <= frameCount; f++ )
			{
				track.Keys.Add( new RigKey
				{
					Frame = f,
					Local = RigPose.FromXform( baked[f][b] ),
					Ease = RigEase.Monotone,
				} );
			}

			clip.Tracks.Add( track );
		}

		return clip;
	}

	static (Vec3 Offset, float FootPitchDeg, float ToePitchDeg) FootStepTrajectory( float phase, float strideHalf, float stepLift )
	{
		// Normalise phase to [0, 2*PI)
		phase %= MathF.PI * 2f;
		if ( phase < 0f ) phase += MathF.PI * 2f;

		// [0, PI]: Stance phase (foot planted on z=0, moving from +strideHalf to -strideHalf)
		if ( phase <= MathF.PI )
		{
			float u = phase / MathF.PI; // 0 (front heel strike) -> 1 (back toe off)
			float x = strideHalf * MathF.Cos( u * MathF.PI );
			// Subtle heel-strike at u=0 and toe push-off near u=1
			float heelStrike = (1f - SmoothStep( 0f, 0.18f, u )) * -10f;
			float toeOff = SmoothStep( 0.72f, 1.0f, u ) * 16f;
			float footPitch = heelStrike + toeOff;
			return (new Vec3( x, 0f, 0f ), footPitch, -toeOff * 0.85f);
		}
		else
		{
			// (PI, 2*PI): Swing phase (foot lifts in an arc from -strideHalf back to +strideHalf)
			float u = (phase - MathF.PI) / MathF.PI; // 0 -> 1
			float x = -strideHalf * MathF.Cos( u * MathF.PI );
			float z = stepLift * MathF.Sin( u * MathF.PI );
			float footPitch = 16f * (1f - SmoothStep( 0f, 0.35f, u )) - 10f * SmoothStep( 0.65f, 1.0f, u );
			return (new Vec3( x, 0f, z ), footPitch, 0f);
		}
	}

	static float SmoothStep( float a, float b, float x )
	{
		float t = Math.Clamp( (x - a) / Math.Max( 1e-5f, b - a ), 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	// -------------------------------------------------------------------------
	// Model-Space Pose Builder & Hydraulic Piston Solver
	// -------------------------------------------------------------------------
	public sealed class PoseRig
	{
		readonly Skeleton _rig;
		readonly Xform[] _bindLocal;
		readonly Xform[] _bindWorld;
		readonly Xform[] _local;
		readonly List<PistonLink> _pistons = new();

		readonly int _pelvis;
		readonly int _handL, _handR;
		readonly int _ankleL, _ankleR;
		readonly int _ballL, _ballR;
		readonly int _lensFocus, _lidUp, _lidLo;
		readonly int[] _iris = new int[7];

		public PoseRig( Skeleton rig )
		{
			_rig = rig;
			_bindLocal = new Xform[rig.Count];
			for ( int i = 0; i < rig.Count; i++ )
				_bindLocal[i] = rig.Bones[i].Local;

			_bindWorld = RigAnimFile.ModelSpace( rig, _bindLocal );
			_local = (Xform[])_bindLocal.Clone();

			_pelvis = rig.IndexOf( "pelvis" );
			_handL = rig.IndexOf( "hand_L" );
			_handR = rig.IndexOf( "hand_R" );
			_ankleL = rig.IndexOf( "ankle_L" );
			_ankleR = rig.IndexOf( "ankle_R" );
			_ballL = rig.IndexOf( "ball_L" );
			_ballR = rig.IndexOf( "ball_R" );
			_lensFocus = rig.IndexOf( "lens_focus" );
			_lidUp = rig.IndexOf( "lid_up" );
			_lidLo = rig.IndexOf( "lid_lo" );
			for ( int i = 0; i < 7; i++ )
				_iris[i] = rig.IndexOf( $"iris_{i}" );

			AddPiston( "bicep_L_cyl",     "bicep_L_rod",     "bicep_L_sleeve",     "arm_lower_L" );
			AddPiston( "bicep_R_cyl",     "bicep_R_rod",     "bicep_R_sleeve",     "arm_lower_R" );
			AddPiston( "hamstring_L_cyl", "hamstring_L_rod", "hamstring_L_sleeve", "leg_lower_L" );
			AddPiston( "hamstring_R_cyl", "hamstring_R_rod", "hamstring_R_sleeve", "leg_lower_R" );
			AddPiston( "achilles_L_cyl",  "achilles_L_rod",  null,                 "ankle_L" );
			AddPiston( "achilles_R_cyl",  "achilles_R_rod",  null,                 "ankle_R" );
			AddPiston( "ab_piston_L_cyl", "ab_piston_L_rod", null,                 "pelvis" );
			AddPiston( "ab_piston_R_cyl", "ab_piston_R_rod", null,                 "pelvis" );
		}

		void AddPiston( string cylName, string rodName, string sleeveName, string targetBoneName )
		{
			int cyl = _rig.IndexOf( cylName );
			int rod = _rig.IndexOf( rodName );
			int sleeve = sleeveName is not null ? _rig.IndexOf( sleeveName ) : -1;
			int target = _rig.IndexOf( targetBoneName );
			if ( cyl < 0 || rod < 0 || target < 0 ) return;

			var anchorLocal = _bindWorld[target].Inverse.TransformPoint( _bindWorld[rod].Origin );
			_pistons.Add( new PistonLink( cyl, rod, sleeve, target, anchorLocal ) );
		}

		public void ResetToBind()
		{
			Array.Copy( _bindLocal, _local, _local.Length );
		}

		public Xform[] CloneLocal() => (Xform[])_local.Clone();

		public void MoveAndTurnPelvis( Vec3 deltaModelPos, float rollDeg, float pitchDeg, float yawDeg )
		{
			if ( _pelvis < 0 ) return;
			var rot = ModelRot( rollDeg, pitchDeg, yawDeg );
			var origin = _bindWorld[_pelvis].Origin;
			var worldPelvis = Xform.Translate( origin + deltaModelPos ) * rot * Xform.Translate( -origin ) * _bindWorld[_pelvis];
			int parent = _rig.Bones[_pelvis].Parent;
			if ( parent >= 0 )
			{
				var world = RigAnimFile.ModelSpace( _rig, _local );
				_local[_pelvis] = world[parent].Inverse * worldPelvis;
			}
			else
			{
				_local[_pelvis] = worldPelvis;
			}
		}

		public void RotateModel( string boneName, float rollDeg, float pitchDeg, float yawDeg )
		{
			int b = _rig.IndexOf( boneName );
			if ( b < 0 ) return;
			var world = RigAnimFile.ModelSpace( _rig, _local );
			var rot = ModelRot( rollDeg, pitchDeg, yawDeg );
			var origin = world[b].Origin;
			var newWorld = Xform.Translate( origin ) * rot * Xform.Translate( -origin ) * world[b];
			int parent = _rig.Bones[b].Parent;
			_local[b] = parent >= 0 ? world[parent].Inverse * newWorld : newWorld;
		}

		public void SolveArmIk( bool left, Vec3 handDeltaFromBind )
		{
			int hand = left ? _handL : _handR;
			if ( hand < 0 ) return;
			float side = left ? 1f : -1f;
			var world = RigAnimFile.ModelSpace( _rig, _local );
			var target = _bindWorld[hand].Origin + handDeltaFromBind;
			// Elbow bends backward (-X) and outward (side * +Y)
			var pole = new Vec3( -1.0f, side * 0.55f, -0.25f );
			if ( TwoBoneIk.Apply( _rig, world, hand, target, pole, softness: 1.0f ) is { } solved )
			{
				var after = RigAnimFile.LocalSpace( _rig, solved );
				int lower = _rig.Bones[hand].Parent;
				int upper = lower >= 0 ? _rig.Bones[lower].Parent : -1;
				if ( upper >= 0 ) _local[upper] = after[upper];
				if ( lower >= 0 ) _local[lower] = after[lower];
				_local[hand] = after[hand];
			}
		}

		public void SolveLegIk( bool left, Vec3 ankleDeltaFromBind, float footPitchDeg, float toePitchDeg )
		{
			int ankle = left ? _ankleL : _ankleR;
			if ( ankle < 0 ) return;
			float side = left ? 1f : -1f;
			var world = RigAnimFile.ModelSpace( _rig, _local );
			var target = _bindWorld[ankle].Origin + ankleDeltaFromBind;
			// Knee bends forward (+X) and slightly outward
			var pole = new Vec3( 1.0f, side * 0.08f, 0.15f );
			if ( TwoBoneIk.Apply( _rig, world, ankle, target, pole, softness: 0.75f ) is { } solved )
			{
				// Keep the foot oriented relative to model ground + requested footPitchDeg
				var ankleOrigin = solved[ankle].Origin;
				var bindAnkleBasis = new Xform( _bindWorld[ankle].X, _bindWorld[ankle].Y, _bindWorld[ankle].Z, ankleOrigin );
				var pitchRot = ModelRot( 0f, footPitchDeg, 0f );
				solved[ankle] = Xform.Translate( ankleOrigin ) * pitchRot * Xform.Translate( -ankleOrigin ) * bindAnkleBasis;

				var after = RigAnimFile.LocalSpace( _rig, solved );
				int lower = _rig.Bones[ankle].Parent;
				int upper = lower >= 0 ? _rig.Bones[lower].Parent : -1;
				if ( upper >= 0 ) _local[upper] = after[upper];
				if ( lower >= 0 ) _local[lower] = after[lower];
				_local[ankle] = after[ankle];

				int ball = left ? _ballL : _ballR;
				if ( ball >= 0 && MathF.Abs( toePitchDeg ) > 1e-3f )
					RotateModel( _rig.Bones[ball].Name, 0f, toePitchDeg, 0f );
			}
		}

		public void CurlFingers( bool left, float degrees )
		{
			string suf = left ? "_L" : "_R";
			float sign = left ? 1f : -1f;
			float rad = degrees * MathF.PI / 180f;
			foreach ( string finger in new[] { "index", "middle", "ring" } )
			{
				for ( int k = 0; k <= 2; k++ )
				{
					int b = _rig.IndexOf( $"finger_{finger}_{k}{suf}" );
					if ( b >= 0 )
						_local[b] = _bindLocal[b] * Xform.Rotate( new Vec3( 0, 0, sign ), rad * 0.45f );
				}
			}
		}

		public void PoseCameraOptics( float focusDeg, float shutterClose, float apertureClose )
		{
			// Focus ring rotates around camera forward (+X in model space)
			if ( _lensFocus >= 0 )
				RotateModel( "lens_focus", focusDeg, 0f, 0f );

			// Upper and lower shutter lids translate/pitch slightly to narrow the viewport
			shutterClose = Math.Clamp( shutterClose, 0f, 1f );
			if ( _lidUp >= 0 )
				RotateModel( "lid_up", 0f, shutterClose * 18f, 0f );
			if ( _lidLo >= 0 )
				RotateModel( "lid_lo", 0f, -shutterClose * 18f, 0f );

			// 7 iris blades rotate in their blade plane to contract/dilate the aperture
			apertureClose = Math.Clamp( apertureClose, 0f, 1f );
			float irisRad = apertureClose * 22f * MathF.PI / 180f;
			for ( int i = 0; i < _iris.Length; i++ )
			{
				int b = _iris[i];
				if ( b >= 0 )
					_local[b] = _bindLocal[b] * Xform.Rotate( new Vec3( 1, 0, 0 ), irisRad );
			}
		}

		public void SolvePistons()
		{
			SolvePistonsOnPose( _rig, _bindLocal, _bindWorld, _pistons, _local );
		}

		internal static void SolvePistonsOnPose(
			Skeleton rig,
			Xform[] bindLocal,
			Xform[] bindWorld,
			List<PistonLink> pistons,
			Xform[] local )
		{
			if ( pistons.Count == 0 ) return;
			var world = RigAnimFile.ModelSpace( rig, local );

			foreach ( var p in pistons )
			{
				var cylHead = world[p.Cyl].Origin;
				var rodTarget = world[p.TargetBone].TransformPoint( p.TargetLocalAnchor );
				var oldRodHead = world[p.Rod].Origin;

				var fromDir = oldRodHead - cylHead;
				var toDir = rodTarget - cylHead;
				float dist = toDir.Length;
				if ( dist < 1e-3f || fromDir.Length < 1e-3f ) continue;

				var deltaRot = DeltaBetweenDirs( fromDir, toDir );
				var newCylWorld = Xform.Translate( cylHead ) * deltaRot * Xform.Translate( -cylHead ) * world[p.Cyl];
				int cylParent = rig.Bones[p.Cyl].Parent;
				local[p.Cyl] = cylParent >= 0 ? world[cylParent].Inverse * newCylWorld : newCylWorld;
				world[p.Cyl] = newCylWorld;

				var bindRod = bindLocal[p.Rod];
				local[p.Rod] = new Xform( bindRod.X, bindRod.Y, bindRod.Z, new Vec3( dist, bindRod.Origin.y, bindRod.Origin.z ) );

				if ( p.Sleeve >= 0 )
				{
					var bindSleeve = bindLocal[p.Sleeve];
					local[p.Sleeve] = new Xform( bindSleeve.X, bindSleeve.Y, bindSleeve.Z, new Vec3( dist * 0.5f, bindSleeve.Origin.y, bindSleeve.Origin.z ) );
				}
			}
		}

		static Xform ModelRot( float rollDeg, float pitchDeg, float yawDeg )
		{
			const float d2r = MathF.PI / 180f;
			var rx = MathF.Abs( rollDeg ) > 1e-5f ? Xform.Rotate( new Vec3( 1, 0, 0 ), rollDeg * d2r ) : Xform.Identity;
			var ry = MathF.Abs( pitchDeg ) > 1e-5f ? Xform.Rotate( new Vec3( 0, 1, 0 ), pitchDeg * d2r ) : Xform.Identity;
			var rz = MathF.Abs( yawDeg ) > 1e-5f ? Xform.Rotate( new Vec3( 0, 0, 1 ), yawDeg * d2r ) : Xform.Identity;
			return rz * ry * rx;
		}
	}
}
