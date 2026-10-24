#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Mode of blending between animation layers.
/// Override replaces the base pose by weight.
/// Additive applies motion delta from reference/bind pose on top of base pose.
/// </summary>
public enum BlendMode
{
	Override = 0,
	Overlay = 0,
	Additive = 1
}

/// <summary>
/// Whitelist and blacklist filtering for bone names during layer blending.
/// Supports exact bone names as well as glob patterns (e.g., "arm_*", "*_finger*").
/// </summary>
public sealed class BoneMask
{
	public HashSet<string> Whitelist { get; } = new( StringComparer.OrdinalIgnoreCase );
	public HashSet<string> Blacklist { get; } = new( StringComparer.OrdinalIgnoreCase );

	public BoneMask() { }

	public BoneMask( IEnumerable<string>? whitelist, IEnumerable<string>? blacklist = null )
	{
		if ( whitelist != null )
			foreach ( var w in whitelist ) Whitelist.Add( w );
		if ( blacklist != null )
			foreach ( var b in blacklist ) Blacklist.Add( b );
	}

	public static BoneMask All => new();

	public static BoneMask CreateWhitelist( params string[] bones ) => new( bones );
	public static BoneMask CreateWhitelist( IEnumerable<string> bones ) => new( bones );
	public static BoneMask CreateBlacklist( params string[] bones ) => new( null, bones );
	public static BoneMask CreateBlacklist( IEnumerable<string> bones ) => new( null, bones );

	/// <summary>
	/// Returns true if the given bone is allowed to blend.
	/// If blacklisted, returns false.
	/// If whitelist is populated, must match whitelist.
	/// </summary>
	public bool IsAllowed( string boneName )
	{
		if ( string.IsNullOrEmpty( boneName ) ) return false;

		if ( Blacklist.Count > 0 && (Blacklist.Contains( boneName ) || MotionLint.Matches( boneName, Blacklist )) )
			return false;

		if ( Whitelist.Count > 0 )
			return Whitelist.Contains( boneName ) || MotionLint.Matches( boneName, Whitelist );

		return true;
	}
}

/// <summary>
/// Descriptor for a blend layer evaluated on top of a base clip.
/// </summary>
public sealed class BlendLayer
{
	public RigAnimFile Clip { get; set; }
	public float Time { get; set; }
	public float Weight { get; set; } = 1.0f;
	public BlendMode Mode { get; set; } = BlendMode.Override;
	public BoneMask? Mask { get; set; }
	public bool Loop { get; set; } = true;
	public RigAnimFile? ReferenceClip { get; set; }
	public float ReferenceTime { get; set; } = 0f;

	public BlendLayer( RigAnimFile clip, float time = 0f, float weight = 1.0f, BlendMode mode = BlendMode.Override, BoneMask? mask = null, bool loop = true )
	{
		Clip = clip ?? throw new ArgumentNullException( nameof( clip ) );
		Time = time;
		Weight = weight;
		Mode = mode;
		Mask = mask;
		Loop = loop;
	}
}

/// <summary>
/// CPU blend and evaluation service for animation clips, overlay/additive layers, and crossfading.
/// 
/// Evaluation Order:
/// 1. Sample base keys (or reference/bind pose fallback if unkeyed).
/// 2. Sample layer keys (or reference/bind pose fallback if unkeyed).
/// 3. Blend layer pose onto base pose (Override or Additive compose, respecting bone mask and weights).
/// 4. Apply rig limits (RigLimit.Clamp).
/// 5. Solve IK constraints (RigAnimFile.SolveIk).
/// 6. In-place root motion adjustment (if enabled).
/// 
/// Invariants:
/// - Weight 0 returns exact base pose without floating-point drift.
/// - Weight 1 in Override mode returns exact target pose.
/// - Renormalizes quaternions and handles antipodal quaternions (q vs -q) via dot &lt; 0 sign flip to guarantee shortest arc.
/// - Additive mode uses ClipLayer.Compose semantics: delta from reference pose, NOT component-wise addition.
/// - Changing layers or evaluating blend does not mutate underlying clips or tracks.
/// </summary>
public static class ClipBlendService
{
	public static float Dot( Quat a, Quat b ) =>
		a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

	public static Quat Negate( Quat q ) =>
		new( -q.x, -q.y, -q.z, -q.w );

	public static Quat Normalize( Quat q )
	{
		float lenSq = Dot( q, q );
		if ( lenSq < 1e-12f ) return Quat.Identity;
		float invLen = 1f / MathF.Sqrt( lenSq );
		return new Quat( q.x * invLen, q.y * invLen, q.z * invLen, q.w * invLen );
	}

	/// <summary>
	/// Slerps between two quaternions along the shortest arc on the 4D sphere.
	/// Flips the target quaternion sign when dot &lt; 0 to correctly handle antipodal representations.
	/// </summary>
	public static Quat SlerpShortestArc( Quat a, Quat b, float t )
	{
		if ( t <= 0f ) return a;
		if ( t >= 1f ) return b;

		float dot = Dot( a, b );
		Quat bTarget = dot < 0f ? Negate( b ) : b;
		float absDot = MathF.Abs( dot );

		// Fallback to normalized linear interpolation for nearly collinear quaternions to avoid numerical instability
		if ( absDot >= 0.9999f )
		{
			float nx = a.x + (bTarget.x - a.x) * t;
			float ny = a.y + (bTarget.y - a.y) * t;
			float nz = a.z + (bTarget.z - a.z) * t;
			float nw = a.w + (bTarget.w - a.w) * t;
			return Normalize( new Quat( nx, ny, nz, nw ) );
		}

		return Quat.Slerp( a, bTarget, t ).Normalized;
	}

	/// <summary>
	/// Blends between basePose and targetPose given a referencePose, weight (0..1), and blend mode.
	/// </summary>
	public static RigPose BlendPose( RigPose basePose, RigPose targetPose, RigPose referencePose, float weight, BlendMode mode )
	{
		// Exact boundary invariant: weight 0 is exactly base pose
		if ( weight <= 0f )
			return basePose;

		weight = Math.Clamp( weight, 0f, 1f );

		if ( mode == BlendMode.Override )
		{
			// Exact boundary invariant: weight 1 in override mode is exactly target pose
			if ( weight >= 1f )
				return targetPose;

			Vec3 pos = Vec3.Lerp( basePose.Position, targetPose.Position, weight );
			Vec3 scale = Vec3.Lerp( basePose.Scale, targetPose.Scale, weight );
			Quat rot = SlerpShortestArc( basePose.Rotation, targetPose.Rotation, weight );
			return new RigPose( pos, rot, scale );
		}
		else // Additive
		{
			// Translation: additive delta from reference pose
			Vec3 deltaPos = (targetPose.Position - referencePose.Position) * weight;
			Vec3 finalPos = basePose.Position + deltaPos;

			// Rotation: delta rotation in reference frame D = R_ref^-1 * R_target
			Quat deltaRot = (referencePose.Rotation.Inverse * targetPose.Rotation).Normalized;

			// Handle antipodal sign flip relative to Identity
			float dot = Dot( Quat.Identity, deltaRot );
			if ( dot < 0f )
				deltaRot = Negate( deltaRot );

			Quat delta;
			if ( weight >= 1f )
			{
				delta = deltaRot;
			}
			else if ( MathF.Abs( dot ) >= 0.9999f )
			{
				float nx = deltaRot.x * weight;
				float ny = deltaRot.y * weight;
				float nz = deltaRot.z * weight;
				float nw = 1f + (deltaRot.w - 1f) * weight;
				delta = Normalize( new Quat( nx, ny, nz, nw ) );
			}
			else
			{
				delta = Quat.Slerp( Quat.Identity, deltaRot, weight ).Normalized;
			}

			Quat finalRot = (basePose.Rotation * delta).Normalized;

			// Scale: ratio = target / reference; Lerp(1, ratio, weight)
			float ratioX = Ratio( targetPose.Scale.x, referencePose.Scale.x );
			float ratioY = Ratio( targetPose.Scale.y, referencePose.Scale.y );
			float ratioZ = Ratio( targetPose.Scale.z, referencePose.Scale.z );

			Vec3 finalScale = new Vec3(
				basePose.Scale.x * Lerp1( ratioX, weight ),
				basePose.Scale.y * Lerp1( ratioY, weight ),
				basePose.Scale.z * Lerp1( ratioZ, weight ) );

			return new RigPose( finalPos, finalRot, finalScale );
		}
	}

	public static RigPose BlendPose( RigPose basePose, RigPose targetPose, float weight, BlendMode mode = BlendMode.Override ) =>
		BlendPose( basePose, targetPose, RigPose.Identity, weight, mode );

	public static RigPose Compose( RigPose below, RigPose reference, RigPose layer, float weight, BlendMode mode ) =>
		BlendPose( below, layer, reference, weight, mode );

	public static RigPose Compose( RigPose below, RigPose reference, RigPose layer, float weight, RigLayerMode mode ) =>
		BlendPose( below, layer, reference, weight, mode == RigLayerMode.Overlay ? BlendMode.Override : BlendMode.Additive );

	static float Ratio( float value, float reference ) => MathF.Abs( reference ) > 1e-6f ? value / reference : 1f;
	static float Lerp1( float ratio, float weight ) => 1f + (ratio - 1f) * weight;

	/// <summary>
	/// Computes the playback frame for a pre-cached lastFrame and frameRate, allocating zero bytes.
	/// </summary>
	public static float CalculateFrame( int lastFrame, int frameRate, float timeSeconds, bool loop )
	{
		if ( lastFrame <= 0 ) return 0f;
		int fps = Math.Max( 1, frameRate );
		float rawFrame = timeSeconds * fps;
		return loop
			? ((rawFrame % lastFrame) + lastFrame) % lastFrame
			: Math.Clamp( rawFrame, 0f, lastFrame );
	}

	/// <summary>
	/// Computes the playback frame for a clip at given time in seconds, respecting looping or one-shot clamping.
	/// </summary>
	public static float CalculateFrame( RigAnimFile clip, float timeSeconds, bool loop )
	{
		ArgumentNullException.ThrowIfNull( clip );
		return CalculateFrame( clip.LastFrame, clip.FrameRate, timeSeconds, loop );
	}

	/// <summary>
	/// Evaluates crossfade weight (0.0 to 1.0) over a transition duration.
	/// If duration &lt;= 0, snaps to 1 immediately for non-negative elapsedTime.
	/// </summary>
	public static float EvaluateCrossfadeWeight( float elapsedTime, float duration, bool smooth = true )
	{
		if ( duration <= 0f )
			return elapsedTime < 0f ? 0f : 1f;

		float t = Math.Clamp( elapsedTime / duration, 0f, 1f );
		return smooth ? t * t * (3f - 2f * t) : t;
	}

	/// <summary>
	/// Samples a bone from a clip at a given frame.
	/// Falls back to Skeleton bind pose if unkeyed, or defaultPose / RigPose.Identity.
	/// </summary>
	public static RigPose SampleClipBone( RigAnimFile clip, string boneName, float frame, Skeleton? skeleton = null, RigPose? defaultPose = null )
	{
		var track = clip.Find( boneName );
		if ( track is { Keys.Count: > 0 } )
			return track.Evaluate( frame );

		if ( skeleton != null )
		{
			int idx = skeleton.IndexOf( boneName );
			if ( idx >= 0 )
				return RigPose.FromXform( skeleton.Bones[idx].Local );
		}

		return defaultPose ?? RigPose.Identity;
	}

	/// <summary>
	/// Evaluates blending between baseClip and layerClip across all bones.
	/// Returns a dictionary of bone names to blended RigPose.
	/// Does not mutate either clip.
	/// </summary>
	public static Dictionary<string, RigPose> EvaluateBlend(
		RigAnimFile baseClip,
		float baseTime,
		RigAnimFile layerClip,
		float layerTime,
		float weight,
		BlendMode mode = BlendMode.Override,
		BoneMask? mask = null,
		Skeleton? skeleton = null,
		bool baseLoop = true,
		bool layerLoop = true,
		IReadOnlyDictionary<string, RigPose>? referencePoses = null )
	{
		ArgumentNullException.ThrowIfNull( baseClip );
		ArgumentNullException.ThrowIfNull( layerClip );

		float baseFrame = CalculateFrame( baseClip, baseTime, baseLoop );
		float layerFrame = CalculateFrame( layerClip, layerTime, layerLoop );

		// Collect target bone names
		IEnumerable<string> boneNames;
		if ( skeleton != null )
		{
			boneNames = skeleton.Bones.Select( b => b.Name );
		}
		else
		{
			var names = new HashSet<string>( StringComparer.Ordinal );
			foreach ( var t in baseClip.BoneTracks ) names.Add( t.Name );
			foreach ( var t in layerClip.BoneTracks ) names.Add( t.Name );
			if ( referencePoses != null )
				foreach ( var k in referencePoses.Keys ) names.Add( k );
			boneNames = names;
		}

		var result = new Dictionary<string, RigPose>( StringComparer.Ordinal );

		foreach ( var bone in boneNames )
		{
			// Determine reference pose for additive deltas and unkeyed fallbacks
			RigPose refPose;
			if ( referencePoses != null && referencePoses.TryGetValue( bone, out var r ) )
			{
				refPose = r;
			}
			else if ( layerClip.Find( bone ) is { Keys.Count: > 0 } layerTrack )
			{
				refPose = layerTrack.Keys[0].Local;
			}
			else if ( skeleton != null && skeleton.IndexOf( bone ) is >= 0 and var bIdx )
			{
				refPose = RigPose.FromXform( skeleton.Bones[bIdx].Local );
			}
			else
			{
				refPose = RigPose.Identity;
			}

			RigPose basePose = SampleClipBone( baseClip, bone, baseFrame, skeleton, refPose );

			if ( mask != null && !mask.IsAllowed( bone ) )
			{
				result[bone] = basePose;
				continue;
			}

			RigPose layerPose = SampleClipBone( layerClip, bone, layerFrame, skeleton, refPose );
			RigPose blended = BlendPose( basePose, layerPose, refPose, weight, mode );

			// Limits clamping if baseClip defines limits
			if ( baseClip.Limits is { Count: > 0 } )
				blended = RigLimit.Clamp( baseClip.Limits, bone, blended );

			result[bone] = blended;
		}

		return result;
	}

	/// <summary>
	/// Evaluates blending between baseClip and layerClip directly into Skeleton local Xforms.
	/// Respects full evaluation order: base keys -> layer keys -> blend -> limits -> IK solving.
	/// </summary>
	public static Xform[] EvaluateBlendLocal(
		Skeleton skeleton,
		RigAnimFile baseClip,
		float baseTime,
		RigAnimFile layerClip,
		float layerTime,
		float weight,
		BlendMode mode = BlendMode.Override,
		BoneMask? mask = null,
		bool baseLoop = true,
		bool layerLoop = true )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		var poses = new Xform[skeleton.Count];
		EvaluateBlendLocal( skeleton, baseClip, baseTime, layerClip, layerTime, weight, poses, mode, mask, baseLoop, layerLoop );
		return poses;
	}

	/// <summary>
	/// In-place allocation-free (or minimal allocation) local pose evaluation for a Skeleton.
	/// </summary>
	public static void EvaluateBlendLocal(
		Skeleton skeleton,
		RigAnimFile baseClip,
		float baseTime,
		RigAnimFile layerClip,
		float layerTime,
		float weight,
		Span<Xform> outLocalPoses,
		BlendMode mode = BlendMode.Override,
		BoneMask? mask = null,
		bool baseLoop = true,
		bool layerLoop = true )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		if ( outLocalPoses.Length < skeleton.Count )
			throw new ArgumentException( "Output buffer too small for skeleton.", nameof( outLocalPoses ) );

		float baseFrame = CalculateFrame( baseClip, baseTime, baseLoop );
		float layerFrame = CalculateFrame( layerClip, layerTime, layerLoop );

		for ( int b = 0; b < skeleton.Count; b++ )
		{
			string boneName = skeleton.Bones[b].Name;
			var bindLocal = skeleton.Bones[b].Local;
			var bindPose = RigPose.FromXform( bindLocal );

			var baseTrack = baseClip.Find( boneName );
			RigPose basePose = baseTrack is { Keys.Count: > 0 } ? baseTrack.Evaluate( baseFrame ) : bindPose;

			if ( mask != null && !mask.IsAllowed( boneName ) )
			{
				if ( baseClip.Limits is { Count: > 0 } )
					basePose = RigLimit.Clamp( baseClip.Limits, boneName, basePose );
				outLocalPoses[b] = basePose.ToXform();
				continue;
			}

			var layerTrack = layerClip.Find( boneName );
			RigPose refPose = layerTrack is { Keys.Count: > 0 } ? layerTrack.Keys[0].Local : bindPose;
			RigPose layerPose = layerTrack is { Keys.Count: > 0 } ? layerTrack.Evaluate( layerFrame ) : bindPose;

			RigPose blended = BlendPose( basePose, layerPose, refPose, weight, mode );

			if ( baseClip.Limits is { Count: > 0 } )
				blended = RigLimit.Clamp( baseClip.Limits, boneName, blended );

			outLocalPoses[b] = blended.ToXform();
		}

		// Solve IK goals if present
		if ( baseClip.IkGoalTracks.Any() )
		{
			var localArray = outLocalPoses.Slice( 0, skeleton.Count ).ToArray();
			var solved = baseClip.SolveIk( skeleton, localArray, baseFrame );
			solved.CopyTo( outLocalPoses );
		}
	}

	/// <summary>
	/// Evaluates a smooth crossfade between two clips over time.
	/// </summary>
	public static Dictionary<string, RigPose> EvaluateCrossfade(
		RigAnimFile fromClip,
		float fromStartTime,
		bool fromLoop,
		RigAnimFile toClip,
		float toStartTime,
		bool toLoop,
		float elapsedTime,
		float duration,
		Skeleton? skeleton = null,
		BoneMask? mask = null,
		BlendMode mode = BlendMode.Override,
		bool smooth = true )
	{
		float weight = EvaluateCrossfadeWeight( elapsedTime, duration, smooth );
		float fromTime = fromStartTime + elapsedTime;
		float toTime = toStartTime + elapsedTime;

		return EvaluateBlend(
			baseClip: fromClip,
			baseTime: fromTime,
			layerClip: toClip,
			layerTime: toTime,
			weight: weight,
			mode: mode,
			mask: mask,
			skeleton: skeleton,
			baseLoop: fromLoop,
			layerLoop: toLoop );
	}

	/// <summary>
	/// Evaluates crossfade local poses for a Skeleton.
	/// </summary>
	public static Xform[] EvaluateCrossfadeLocal(
		Skeleton skeleton,
		RigAnimFile fromClip,
		float fromStartTime,
		bool fromLoop,
		RigAnimFile toClip,
		float toStartTime,
		bool toLoop,
		float elapsedTime,
		float duration,
		BoneMask? mask = null,
		BlendMode mode = BlendMode.Override,
		bool smooth = true )
	{
		float weight = EvaluateCrossfadeWeight( elapsedTime, duration, smooth );
		float fromTime = fromStartTime + elapsedTime;
		float toTime = toStartTime + elapsedTime;

		return EvaluateBlendLocal(
			skeleton: skeleton,
			baseClip: fromClip,
			baseTime: fromTime,
			layerClip: toClip,
			layerTime: toTime,
			weight: weight,
			mode: mode,
			mask: mask,
			baseLoop: fromLoop,
			layerLoop: toLoop );
	}
}

/// <summary>
/// Pre-allocated zero-allocation blend evaluator for a fixed Skeleton.
/// Pre-indexes track lookups and caches frame parameters to avoid per-frame allocations or LINQ queries.
/// </summary>
public sealed class ClipBlendEvaluator
{
	public Skeleton Skeleton { get; }
	readonly int _boneCount;
	readonly RigPose[] _bindPoses;
	readonly Xform[] _bindLocal;
	readonly RigTrack?[] _baseTracks;
	readonly RigTrack?[] _layerTracks;
	readonly RigPose[] _referencePoses;
	readonly RigPose[] _blendedPoses;
	readonly Xform[] _localPoses;

	int _lastFrameBase;
	int _lastFrameLayer;
	int _fpsBase;
	int _fpsLayer;

	public RigAnimFile? BaseClip { get; private set; }
	public RigAnimFile? LayerClip { get; private set; }
	public ReadOnlySpan<Xform> LocalPoses => _localPoses.AsSpan( 0, _boneCount );

	public ClipBlendEvaluator( Skeleton skeleton )
	{
		Skeleton = skeleton ?? throw new ArgumentNullException( nameof( skeleton ) );
		_boneCount = skeleton.Count;
		_bindPoses = new RigPose[_boneCount];
		_bindLocal = new Xform[_boneCount];
		_baseTracks = new RigTrack?[_boneCount];
		_layerTracks = new RigTrack?[_boneCount];
		_referencePoses = new RigPose[_boneCount];
		_blendedPoses = new RigPose[_boneCount];
		_localPoses = new Xform[_boneCount];

		for ( int b = 0; b < _boneCount; b++ )
		{
			_bindLocal[b] = skeleton.Bones[b].Local;
			_bindPoses[b] = RigPose.FromXform( _bindLocal[b] );
			_referencePoses[b] = _bindPoses[b];
			_localPoses[b] = _bindLocal[b];
		}
	}

	public void SetClips( RigAnimFile baseClip, RigAnimFile layerClip )
	{
		BaseClip = baseClip ?? throw new ArgumentNullException( nameof( baseClip ) );
		LayerClip = layerClip ?? throw new ArgumentNullException( nameof( layerClip ) );

		_lastFrameBase = baseClip.LastFrame;
		_lastFrameLayer = layerClip.LastFrame;
		_fpsBase = Math.Max( 1, baseClip.FrameRate );
		_fpsLayer = Math.Max( 1, layerClip.FrameRate );

		Array.Clear( _baseTracks );
		Array.Clear( _layerTracks );

		for ( int b = 0; b < _boneCount; b++ )
		{
			string name = Skeleton.Bones[b].Name;
			_baseTracks[b] = baseClip.Find( name );
			var lt = layerClip.Find( name );
			_layerTracks[b] = lt;
			_referencePoses[b] = lt is { Keys.Count: > 0 } ? lt.Keys[0].Local : _bindPoses[b];
		}
	}

	/// <summary>
	/// Zero-allocation frame evaluation for the pre-indexed skeleton and clips.
	/// </summary>
	public void Evaluate( float baseTime, float layerTime, float weight, BlendMode mode = BlendMode.Override, BoneMask? mask = null, bool baseLoop = true, bool layerLoop = true )
	{
		if ( BaseClip == null || LayerClip == null )
			throw new InvalidOperationException( "Clips must be set before evaluation." );

		float baseFrame = ClipBlendService.CalculateFrame( _lastFrameBase, _fpsBase, baseTime, baseLoop );
		float layerFrame = ClipBlendService.CalculateFrame( _lastFrameLayer, _fpsLayer, layerTime, layerLoop );

		for ( int b = 0; b < _boneCount; b++ )
		{
			string name = Skeleton.Bones[b].Name;
			var bt = _baseTracks[b];
			RigPose basePose = bt is { Keys.Count: > 0 } ? bt.Evaluate( baseFrame ) : _bindPoses[b];

			if ( mask != null && !mask.IsAllowed( name ) )
			{
				if ( BaseClip.Limits is { Count: > 0 } )
					basePose = RigLimit.Clamp( BaseClip.Limits, name, basePose );
				_localPoses[b] = basePose.ToXform();
				continue;
			}

			var lt = _layerTracks[b];
			RigPose refPose = _referencePoses[b];
			RigPose layerPose = lt is { Keys.Count: > 0 } ? lt.Evaluate( layerFrame ) : _bindPoses[b];

			RigPose blended = ClipBlendService.BlendPose( basePose, layerPose, refPose, weight, mode );

			if ( BaseClip.Limits is { Count: > 0 } )
				blended = RigLimit.Clamp( BaseClip.Limits, name, blended );

			_localPoses[b] = blended.ToXform();
		}
	}
}
