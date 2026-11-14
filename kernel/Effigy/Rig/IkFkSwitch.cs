using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// One limb's IK/FK switch state, Blender Rigify style: a slider, a pole target and a
/// stretch limit, on top of the existing solvers — no new solver here.
///
/// THE SLIDER IS 0 = IK, 1 = FK, as the ticket says. That is the opposite of Blender's
/// 0 = FK / 1 = IK, so read it off this field, not off habit. The solver blend passed
/// to <see cref="IkFk.BlendWorld"/> is 1 − <see cref="Blend"/>.
///
/// THE POLE IS A DIRECTION FROM THE CHAIN ROOT, not a point. That is
/// <see cref="RigIk.PoleFor"/>'s convention (a pole bone's head, minus the root, or the
/// stored <see cref="RigIk.Pole"/> direction), and it is what
/// <see cref="TwoBoneIk.TrySolve"/> normalises into the bend plane. A pole that arrives
/// as a point — <see cref="IkFk.DefaultPole"/> returns one — is converted here with the
/// root subtracted, so a rig whose root is nowhere near the origin still bends the right
/// way. <see cref="ResolvePole"/> returns the direction; <see cref="SnapToIk"/> reports it
/// the same way.
/// </summary>
public sealed class LimbIkFk
{
	/// <summary>Name of the limb, e.g. "arm_L". Also the name of the <see cref="RigIk"/> the
	/// solve builds, so a clip's IK goal track is keyed under it.</summary>
	public string Name = "";

	/// <summary>Top bone of the chain — the bone that turns, never the end.</summary>
	public string RootBone = "";

	/// <summary>Middle bone, for a two-bone chain. Must be the bone between
	/// <see cref="RootBone"/> and <see cref="EndBone"/> when set; empty means "walk the
	/// parents and take whatever is there".</summary>
	public string MidBone = "";

	/// <summary>The bone that reaches — the chain's end, and the name its goal track is
	/// keyed under.</summary>
	public string EndBone = "";

	/// <summary>Optional pole-target bone: the pole direction is that bone's head minus the
	/// chain root, so the pole follows the rig instead of a fixed world axis. Empty means
	/// "use <see cref="Pole"/> when <see cref="UseFixedPole"/> is set, else the FK pose's
	/// own bend". Beats <see cref="UseFixedPole"/> when both are set, the same order
	/// <see cref="RigIk.PoleFor"/> applies.</summary>
	public string PoleBone = "";

	/// <summary>Use the fixed <see cref="Pole"/> direction instead of the FK pose's bend.
	/// Off by default: a fresh limb bends the way it was authored.</summary>
	public bool UseFixedPole;

	/// <summary>Fixed pole direction from the chain root, when <see cref="UseFixedPole"/> is
	/// set. (0, 0, 1) is the default, matching <see cref="RigIk.Pole"/>.</summary>
	public Vec3 Pole = new( 0, 0, 1 );

	/// <summary>0 = IK, 1 = FK. Clamped on use; out of range is a caller bug, not a crash.</summary>
	public float Blend;

	/// <summary>How far past its length the chain may stretch to reach a goal — 1 never
	/// stretches, 1.5 lets it reach half as far again. Passed through to
	/// <see cref="RigIk.MaxStretch"/> and <see cref="ChainIk.Stretch"/>.</summary>
	public float MaxStretch = 1f;

	/// <summary>Whether the end bone takes the goal's rotation as well as its position.</summary>
	public bool GoalRotation = true;

	/// <summary>How many bones above the end the solve may turn. 2 is an arm or a leg.</summary>
	public int ChainLength = 2;

	/// <summary>The chain's bone indices, root first, end last, resolved against a skeleton
	/// and validated: the walk from <see cref="EndBone"/> up <see cref="ChainLength"/> parents
	/// must land on <see cref="RootBone"/>, and <see cref="MidBone"/>, when set, must be the
	/// middle of a two-bone chain.</summary>
	public int[] ChainIndices( Skeleton skeleton )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( ChainLength < 1 ) throw new ArgumentOutOfRangeException( nameof( ChainLength ), "A limb needs at least one bone above the end." );

		var end = skeleton.IndexOf( EndBone );
		if ( end < 0 ) throw new ArgumentException( $"No bone named '{EndBone}' in the rig.", nameof( EndBone ) );

		var chain = new int[ChainLength + 1];
		chain[^1] = end;
		for ( var k = chain.Length - 2; k >= 0; k-- )
		{
			chain[k] = skeleton.Bones[chain[k + 1]].Parent;
			if ( chain[k] < 0 )
				throw new ArgumentException( $"Chain '{EndBone}' is only {chain.Length - 1 - k} bones deep, not {ChainLength}." );
		}

		if ( skeleton.IndexOf( RootBone ) is var root && root != chain[0] )
			throw new ArgumentException( $"Chain '{EndBone}' walked up {ChainLength} parents lands on '{skeleton.Bones[chain[0]].Name}', not '{RootBone}'." );

		if ( ChainLength == 2 && !string.IsNullOrEmpty( MidBone ) && skeleton.IndexOf( MidBone ) != chain[1] )
			throw new ArgumentException( $"Mid bone '{MidBone}' is not between '{RootBone}' and '{EndBone}'." );

		return chain;
	}
}

/// <summary>
/// Per-limb IK/FK switching and snapping, composed from the existing solvers:
/// <see cref="ChainIk"/> (pole bone, stretch, two-bone / CCD / FABRIK / spline) for the
/// solve, <see cref="IkFk"/> for the blend and the snap. The <c>rig ikfk</c> script verb
/// already exposed the raw math; this is the production state on top of it — a limb
/// remembers its slider, its pole and its stretch, snaps without popping, and writes
/// its keys when it switches.
///
/// FK is the pose the animator authored (local rotations, sampled from a clip or the
/// bind pose); IK is the goal-driven solve. Switching is a snap, not a blend over time:
/// snap computes the other mode's exact pose at the switch frame, and the slider blends
/// the two poses only while the animator drags it.
/// </summary>
public static class IkFkSwitch
{
	/// <summary>The pole direction from the chain root for this limb in this FK pose:
	/// the pole bone's head, minus the root, when <see cref="LimbIkFk.PoleBone"/> names a
	/// bone that is there; else the fixed <see cref="LimbIkFk.Pole"/> direction when
	/// <see cref="LimbIkFk.UseFixedPole"/> is set; else the FK pose's own bend
	/// (<see cref="IkFk.DefaultPole"/>, minus the root), so knees point forward and elbows
	/// back relative to the pose as authored, not to an arbitrary world axis.</summary>
	public static Vec3 ResolvePole( Skeleton skeleton, LimbIkFk limb, IReadOnlyList<Xform> fkWorld )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( limb is null ) throw new ArgumentNullException( nameof( limb ) );
		if ( fkWorld is null ) throw new ArgumentNullException( nameof( fkWorld ) );

		var chain = limb.ChainIndices( skeleton );
		var root = fkWorld[chain[0]].Origin;
		var end = fkWorld[chain[^1]].Origin;
		var axis = end - root;

		if ( !string.IsNullOrEmpty( limb.PoleBone ) )
		{
			var bone = skeleton.IndexOf( limb.PoleBone );
			if ( bone >= 0 && bone < fkWorld.Count )
			{
				var offset = fkWorld[bone].Origin - root;
				if ( offset.Length > 1e-5f ) return BendDirection( offset, axis );
			}
		}

		if ( limb.UseFixedPole ) return BendDirection( limb.Pole, axis );

		var mid = fkWorld[chain[1]].Origin;
		return BendDirection( IkFk.DefaultPole( root, mid, end ) - root, axis );
	}

	/// <summary>The bend direction: a pole offset with the component along the chain
	/// axis removed. The solvers only ever use the perpendicular part — it is what
	/// places the elbow in its plane — so the direction ResolvePole returns is the
	/// pure bend direction, not root-to-point: a pole point on the chain line would
	/// otherwise smuggle the mid joint's axial position into the direction (a straight
	/// arm along +X with a +Z pole would come back (1, 0, 1), not (0, 0, 1)).</summary>
	static Vec3 BendDirection( Vec3 offset, Vec3 axis )
	{
		var len = axis.Length;
		Vec3 a;
		if ( len > 1e-6f )
		{
			a = new Vec3( axis.x / len, axis.y / len, axis.z / len );
			offset -= a * Vec3.Dot( offset, a );
		}
		else
		{
			a = new Vec3( 1, 0, 0 );
		}

		if ( offset.LengthSquared > 1e-10f ) return offset;

		// Degenerate: the pole sits on the chain line (or the chain has no length).
		// Pick any direction perpendicular to the axis so the solver still has a plane.
		var fallback = Math.Abs( a.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		return fallback - a * Vec3.Dot( fallback, a );
	}

	/// <summary>Rotate a pole direction around the root-to-end axis by an angle — the pole
	/// ANGLE control: 0 leaves the bend plane alone, 180° flips the elbow to the other
	/// side of the limb. A degenerate chain (root on the end) returns the pole unchanged.</summary>
	public static Vec3 PoleAtAngle( Vec3 root, Vec3 end, Vec3 poleDirection, float angleRadians )
	{
		var axis = end - root;
		if ( axis.LengthSquared < 1e-12f ) return poleDirection;
		return Xform.Rotate( axis.Normal, angleRadians ).TransformDirection( poleDirection );
	}

	/// <summary>Solve the limb toward <paramref name="goal"/> and blend the result with the
	/// FK pose by the slider: <see cref="LimbIkFk.Blend"/> 0 returns the pure IK pose, 1
	/// returns the FK pose untouched. Null when the chain is degenerate — a chain that is
	/// shorter than asked, or a zero-length bone — in which case the pose stands as it was
	/// and the caller, not the solver, decides what to tell the animator.</summary>
	public static Xform[] Solve( Skeleton skeleton, LimbIkFk limb, IReadOnlyList<Xform> fkWorld, RigPose goal )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( limb is null ) throw new ArgumentNullException( nameof( limb ) );
		if ( fkWorld is null ) throw new ArgumentNullException( nameof( fkWorld ) );

		var chain = limb.ChainIndices( skeleton );
		var ik = new RigIk
		{
			Name = limb.Name,
			EndBone = limb.EndBone,
			ChainLength = limb.ChainLength,
			Pole = ResolvePole( skeleton, limb, fkWorld ),
			PoleBone = limb.PoleBone,
			MaxStretch = limb.MaxStretch,
			GoalRotation = limb.GoalRotation,
			Weight = 1f,
		};

		var ikWorld = ChainIk.Apply( skeleton, fkWorld, ik, goal );
		if ( ikWorld is null ) return null;

		return IkFk.BlendWorld( fkWorld, ikWorld, 1f - Math.Clamp( limb.Blend, 0f, 1f ), chain );
	}

	/// <summary>FK locals that reproduce the IK pose: the snap that makes switching from IK
	/// to FK not pop. The chain bones come from <see cref="IkFk.SnapLocals"/>; every other
	/// bone keeps the FK pose's locals, so the rest of the rig does not snap back to bind
	/// on the switch frame. <paramref name="maxError"/> is the worst joint-position error
	/// between the rebuilt pose and the IK pose, over the chain — 0 means no pop at all.
	/// Null, with <paramref name="maxError"/> NaN, when the IK pose cannot be represented
	/// as FK locals (a singular transform).</summary>
	public static Xform[] SnapToFk( Skeleton skeleton, LimbIkFk limb, IReadOnlyList<Xform> fkWorld, IReadOnlyList<Xform> ikWorld, out float maxError )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( limb is null ) throw new ArgumentNullException( nameof( limb ) );
		if ( fkWorld is null ) throw new ArgumentNullException( nameof( fkWorld ) );
		if ( ikWorld is null ) throw new ArgumentNullException( nameof( ikWorld ) );

		maxError = float.NaN;
		var chain = limb.ChainIndices( skeleton );
		var snap = IkFk.SnapLocals( skeleton, fkWorld, ikWorld, chain );
		if ( snap is null ) return null;

		var locals = RigAnimFile.LocalSpace( skeleton, fkWorld );
		foreach ( var b in chain ) locals[b] = snap[b];

		var rebuilt = IkFk.WorldFromLocals( skeleton, locals );
		var worst = 0f;
		foreach ( var b in chain )
			worst = Math.Max( worst, (rebuilt[b].Origin - ikWorld[b].Origin).Length );
		maxError = worst;
		return locals;
	}

	/// <summary>The IK goal and pole that reproduce the FK pose: the snap that makes
	/// switching from FK to IK not pop. The goal is the FK end bone's pose; the pole is
	/// <see cref="ResolvePole"/> for the same pose, so the elbow stays where the FK pose
	/// put it.</summary>
	public static void SnapToIk( Skeleton skeleton, LimbIkFk limb, IReadOnlyList<Xform> fkWorld, out RigPose goal, out Vec3 pole )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( limb is null ) throw new ArgumentNullException( nameof( limb ) );
		if ( fkWorld is null ) throw new ArgumentNullException( nameof( fkWorld ) );

		var chain = limb.ChainIndices( skeleton );
		goal = RigPose.FromXform( fkWorld[chain[^1]] );
		pole = ResolvePole( skeleton, limb, fkWorld );
	}

	/// <summary>Auto-key the snap: write the chain bones' locals into the clip at
	/// <paramref name="frame"/>, creating bone tracks that do not exist yet. A key already
	/// on that frame is replaced, not doubled, and keys stay sorted by frame. Returns how
	/// many keys were written — one per chain bone. This is the key the switch frame gets;
	/// the animator's clip still owns undo, and the switch itself is one keyframe, not a
	/// baked range.</summary>
	public static int AutoKey( RigAnimFile clip, LimbIkFk limb, Skeleton skeleton, int frame, IReadOnlyList<Xform> locals )
	{
		if ( clip is null ) throw new ArgumentNullException( nameof( clip ) );
		if ( limb is null ) throw new ArgumentNullException( nameof( limb ) );
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( locals is null ) throw new ArgumentNullException( nameof( locals ) );

		var chain = limb.ChainIndices( skeleton );
		var written = 0;

		foreach ( var b in chain )
		{
			var name = skeleton.Bones[b].Name;
			var track = clip.Find( name );
			if ( track is null )
			{
				track = new RigTrack { Name = name, Kind = RigTrackKind.Bone };
				clip.Tracks.Add( track );
			}

			var pose = RigPose.FromXform( locals[b] );
			var existing = track.Keys.FirstOrDefault( k => k.Frame == frame );
			if ( existing is not null )
				existing.Local = pose;
			else
			{
				track.Keys.Add( new RigKey { Frame = frame, Local = pose } );
				track.Keys.Sort( ( x, y ) => x.Frame.CompareTo( y.Frame ) );
			}
			written++;
		}

		return written;
	}

	/// <summary>Parent switching on a POSED limb: the new Local for <paramref name="boneIndex"/>
	/// under <paramref name="newParent"/> (-1 for a root) that keeps the bone's posed world
	/// transform exactly. Only the moved bone's Local changes — its descendants keep theirs
	/// and follow it — so this is one assignment, not a re-pose. The bind-pose equivalent
	/// is <see cref="Skeleton.SetParent"/>, which rewrites Local against the bind world;
	/// this one rewrites it against the pose in <paramref name="world"/>. Throws when the
	/// new parent's posed transform is singular, like <see cref="IkFk.SnapLocals"/> does.</summary>
	public static Xform SwitchParentPosed( IReadOnlyList<Xform> world, int boneIndex, int newParent )
	{
		if ( world is null ) throw new ArgumentNullException( nameof( world ) );
		if ( boneIndex < 0 || boneIndex >= world.Count ) throw new ArgumentOutOfRangeException( nameof( boneIndex ) );
		if ( newParent < -1 || newParent >= world.Count ) throw new ArgumentOutOfRangeException( nameof( newParent ) );

		var parentWorld = newParent < 0 ? Xform.Identity : world[newParent];
		return parentWorld.Inverse * world[boneIndex];
	}
}

/// <summary>
/// Foot-roll: one control rotating the foot about a ground pivot, Rigify style. The
/// pivot is the phase — the heel for heel lift, the ball for toe-off, the toe for the
/// end of the step — and the axis is the foot's lateral axis, so one angle drives the
/// whole roll and the toes follow the foot instead of being keyed separately. This is
/// the math primitive; the control rig owns which pivot and angle the animator sees.
/// </summary>
public static class FootRoll
{
	/// <summary>The foot's world transform rolled <paramref name="radians"/> about
	/// <paramref name="pivot"/> on <paramref name="axis"/>. Zero radians returns the foot
	/// unchanged; a pivot on the foot's origin keeps the origin in place and turns the
	/// foot in place. Throws when the axis is (near) zero.</summary>
	public static Xform Roll( Xform footWorld, Vec3 pivot, Vec3 axis, float radians )
	{
		if ( axis.LengthSquared < 1e-12f )
			throw new ArgumentException( "A foot-roll needs a lateral axis.", nameof( axis ) );
		return Xform.RotateAbout( pivot, axis.Normal, radians ) * footWorld;
	}
}
