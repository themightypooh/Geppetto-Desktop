using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Motion-capture cleanup: smooth a clip, pin planted feet, bake the result to
/// editable keys. Everything here works on a <see cref="RigAnimFile"/> directly -
/// no dependency on the in-progress graph/dope/NLA triad or game-clip pipeline -
/// and every method returns a NEW clip, so the input is never mutated.
///
/// WHAT THIS IS NOT:
/// - Rest-pose match and scale compensation live in
///   <see cref="SkeletonRetarget.RetargetClip"/> already (bind-relative rotation
///   deltas; height-based root-motion scale). <see cref="RetargetAndClean"/> calls
///   it and does not re-implement it.
/// - The foot fix corrects ROOT-MOTION skate: a planted foot is held by shifting
///   the root bone, which is exactly where mocap drift lives (the root travels
///   while the foot should stand still). A foot that slides because the LEG swings
///   while planted needs leg-IK pinning, which is a follow-up, not this ticket.
/// - Smoothing on rotations runs through the quaternion log map (axis * angle),
///   filtered like any other channel and mapped back - so a Butterworth here is a
///   real second-order low-pass on the rotation's tangent-space path, not a hack
///   on Euler angles. Quaternions are hemisphere-flipped to their neighbour first
///   (q and -q are the same rotation; an unguarded log map would spike).
/// - Baked clips stand alone: IK goals, limits and root-motion settings are NOT
///   carried onto the output, because the bake samples the clip AS IT PLAYS
///   (through <see cref="RigAnimFile.SampleLocal"/>), so their effect is already
///   in the keys - carrying them would apply them twice.
/// </summary>
public static class MocapCleanup
{
	public enum SmoothKind { Gaussian, Butterworth }

	public sealed class Options
	{
		public bool Smooth = true;
		public SmoothKind Kind = SmoothKind.Gaussian;

		/// <summary>Gaussian sigma in frames.</summary>
		public float SigmaFrames = 1.5f;

		/// <summary>Butterworth -3 dB cutoff in Hz, against the clip's frame rate.</summary>
		public float CutoffHz = 6f;

		public bool FixFeet = true;

		/// <summary>Max horizontal foot travel per frame that still counts as planted
		/// (MotionLint's PlantSpeed, same units).</summary>
		public float PlantSpeed = 0.05f;

		/// <summary>How far above the lowest foot height a foot may sit and still
		/// count as planted, in skeleton units.</summary>
		public float HeightTolerance = 0.5f;

		/// <summary>Shortest planted run that gets fixed (shorter runs are noise).</summary>
		public int MinSpanFrames = 3;

		/// <summary>Frames over which the pin ramps in/out at a span's edges, so the
		/// root does not pop. 0 means the full pin from the span's first frame.</summary>
		public int RampFrames = 2;

		/// <summary>Bake one key every N frames (1 = every frame).</summary>
		public int BakeEveryN = 1;

		/// <summary>Prune baked keys with <see cref="ClipBake.Reduce"/>.</summary>
		public bool ReduceKeys = true;

		/// <summary>Ground height; null means the lowest foot height in the clip.</summary>
		public float? GroundY;

		public string[] FootBones = { "ankle_L", "ankle_R" };

		/// <summary>The root bone, shifted to hold planted feet. Must be a parentless
		/// bone; otherwise the first parentless bone is used.</summary>
		public string RootBone = "pelvis";
	}

	public sealed class Report
	{
		public RigAnimFile Clip;
		public float RootMotionScale = 1f;
		public int Frames;
		public List<string> Unmapped = new();
		public int SpansFixed;
		public float MaxSkateBefore, MaxSkateAfter;
		public int KeysBefore, KeysAfter;
		public List<string> Notes = new();
	}

	/// <summary>
	/// The mocap-to-game workflow in one call: retarget (rest-pose + scale, via
	/// <see cref="SkeletonRetarget.RetargetClip"/>), smooth, pin planted feet, bake
	/// to editable keys.
	/// </summary>
	public static Report RetargetAndClean( RigAnimFile sourceClip, Skeleton sourceRig, Skeleton targetRig,
		IReadOnlyDictionary<string, string> boneMap, Options options = null )
	{
		options ??= new Options();
		ArgumentNullException.ThrowIfNull( sourceClip );
		ArgumentNullException.ThrowIfNull( sourceRig );
		ArgumentNullException.ThrowIfNull( targetRig );
		ArgumentNullException.ThrowIfNull( boneMap );

		var notes = new List<string>();
		var ret = SkeletonRetarget.RetargetClip( sourceClip, sourceRig, targetRig, boneMap );
		var clip = ret.Clip;
		var keysBefore = CountKeys( clip );
		float skateBefore = 0f, skateAfter = 0f;
		var spans = 0;

		if ( options.Smooth )
		{
			clip = Smooth( clip, targetRig, options );
			notes.Add( $"smoothed ({options.Kind})" );
		}

		if ( options.FixFeet )
		{
			( clip, skateBefore, skateAfter, spans ) = FixFootSkate( clip, targetRig, options );
			notes.Add( $"foot-skate: {spans} contact span(s), max skate {skateBefore:F3} -> {skateAfter:F3}" );
		}

		clip = Bake( clip, targetRig, options.BakeEveryN, options.ReduceKeys );
		var keysAfter = CountKeys( clip );

		return new Report
		{
			Clip = clip,
			RootMotionScale = ret.RootMotionScale,
			Frames = ret.Frames,
			Unmapped = ret.Unmapped,
			SpansFixed = spans,
			MaxSkateBefore = skateBefore,
			MaxSkateAfter = skateAfter,
			KeysBefore = keysBefore,
			KeysAfter = keysAfter,
			Notes = notes,
		};
	}

	/// <summary>
	/// Smooth every bone track of a clip, sampled per frame. Positions and scales
	/// filter per component; rotations filter through the log map (see the class
	/// doc). A key lands on every frame of the returned clip.
	/// </summary>
	public static RigAnimFile Smooth( RigAnimFile clip, Skeleton skeleton, Options options = null )
	{
		options ??= new Options();
		ArgumentNullException.ThrowIfNull( clip );
		ArgumentNullException.ThrowIfNull( skeleton );

		var last = clip.LastFrame;
		if ( last <= 0 || skeleton.Count == 0 )
			return Clone( clip );

		var frames = last + 1;
		var sampled = SampleAll( clip, skeleton, frames );

		var smoothed = new RigAnimFile
		{
			SourceModel = clip.SourceModel,
			RigAsset = clip.RigAsset,
			FrameRate = clip.FrameRate,
			FrameCount = frames,
		};

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var name = skeleton.Bones[b].Name;
			if ( clip.Find( name ) is not { Keys.Count: > 0 } )
				continue;

			var poses = new RigPose[frames];
			for ( var f = 0; f < frames; f++ )
				poses[f] = RigPose.FromXform( sampled[f][b] );

			var px = Filter( poses.Select( p => p.Position.x ).ToArray(), clip.FrameRate, options );
			var py = Filter( poses.Select( p => p.Position.y ).ToArray(), clip.FrameRate, options );
			var pz = Filter( poses.Select( p => p.Position.z ).ToArray(), clip.FrameRate, options );
			var sx = Filter( poses.Select( p => p.Scale.x ).ToArray(), clip.FrameRate, options );
			var sy = Filter( poses.Select( p => p.Scale.y ).ToArray(), clip.FrameRate, options );
			var sz = Filter( poses.Select( p => p.Scale.z ).ToArray(), clip.FrameRate, options );

			// Rotations: hemisphere-align neighbours, then filter the log map.
			var log = new Vec3[frames];
			var prev = poses[0].Rotation;
			for ( var f = 0; f < frames; f++ )
			{
				var q = poses[f].Rotation;
				if ( Dot( q, prev ) < 0f )
					q = new Quat( -q.x, -q.y, -q.z, -q.w );
				prev = q;
				log[f] = Log( q );
			}
			var lx = Filter( log.Select( v => v.x ).ToArray(), clip.FrameRate, options );
			var ly = Filter( log.Select( v => v.y ).ToArray(), clip.FrameRate, options );
			var lz = Filter( log.Select( v => v.z ).ToArray(), clip.FrameRate, options );

			var track = new RigTrack { Name = name, Kind = RigTrackKind.Bone };
			for ( var f = 0; f < frames; f++ )
			{
				// Monotone keys: these are sampled values, and the default Smooth
				// ease would bow the curve between the per-frame keys.
				track.Keys.Add( new RigKey
				{
					Frame = f,
					Local = new RigPose(
						new Vec3( px[f], py[f], pz[f] ),
						Exp( new Vec3( lx[f], ly[f], lz[f] ) ),
						new Vec3( sx[f], sy[f], sz[f] ) ),
					Ease = RigEase.Monotone,
				} );
			}
			smoothed.Tracks.Add( track );
		}

		foreach ( var t in clip.Tracks.Where( t => t.Kind != RigTrackKind.Bone ) )
			smoothed.Tracks.Add( CloneTrack( t ) );
		return smoothed;
	}

	/// <summary>
	/// Find planted-foot spans and hold each planted foot at its span-start
	/// position by shifting the root bone. Returns the fixed clip, the worst
	/// skate inside any span before and after, and the span count.
	/// Skate is the farthest a planted foot travels horizontally from its anchor.
	/// </summary>
	public static (RigAnimFile Clip, float MaxSkateBefore, float MaxSkateAfter, int SpansFixed) FixFootSkate(
		RigAnimFile clip, Skeleton skeleton, Options options = null )
	{
		options ??= new Options();
		ArgumentNullException.ThrowIfNull( clip );
		ArgumentNullException.ThrowIfNull( skeleton );

		var last = clip.LastFrame;
		var feet = options.FootBones
			.Select( n => skeleton.IndexOf( n ) )
			.Where( i => i >= 0 )
			.ToArray();
		if ( feet.Length == 0 || last <= 0 || skeleton.Count == 0 )
			return ( Clone( clip ), 0f, 0f, 0 );

		var frames = last + 1;
		var sampled = SampleAll( clip, skeleton, frames );
		var footWorld = SampleFootWorld( sampled, skeleton, feet );
		var groundY = options.GroundY ?? footWorld.SelectMany( w => w ).Min( p => p.y );

		var planted = new bool[feet.Length][];
		for ( var i = 0; i < feet.Length; i++ )
		{
			planted[i] = new bool[frames];
			for ( var f = 0; f < frames; f++ )
			{
				var speed = f + 1 < frames
					? Horizontal( footWorld[i][f + 1] - footWorld[i][f] )
					: f > 0 ? Horizontal( footWorld[i][f] - footWorld[i][f - 1] ) : 0f;
				planted[i][f] = footWorld[i][f].y - groundY <= options.HeightTolerance
					&& speed <= options.PlantSpeed;
			}
		}

		var spans = new List<(int Foot, int Start, int End, Vec3 Anchor)>();
		for ( var i = 0; i < feet.Length; i++ )
		{
			var f = 0;
			while ( f < frames )
			{
				if ( !planted[i][f] ) { f++; continue; }
				var s = f;
				while ( f < frames && planted[i][f] ) f++;
				var e = f - 1;
				if ( e - s + 1 >= options.MinSpanFrames )
					spans.Add( (i, s, e, footWorld[i][s]) );
			}
		}

		if ( spans.Count == 0 )
			return ( Clone( clip ), 0f, 0f, 0 );

		var before = MaxSkateInSpans( footWorld, spans );

		var rootIdx = skeleton.IndexOf( options.RootBone );
		if ( rootIdx < 0 || skeleton.Bones[rootIdx].Parent >= 0 )
			rootIdx = skeleton.Bones.FindIndex( b => b.Parent < 0 );
		if ( rootIdx < 0 )
			return ( Clone( clip ), before, before, 0 );

		// One root offset per frame: the (ramp-weighted) mean of the planted feet's
		// horizontal errors, negated. Horizontal only - the pin holds the foot in
		// place, it does not re-pose the leg, so it must not fight crouch height.
		var offset = new Vec3[frames];
		for ( var f = 0; f < frames; f++ )
		{
			var acc = Vec3.Zero;
			var wsum = 0f;
			foreach ( var (foot, s, e, anchor) in spans )
			{
				if ( f < s || f > e )
					continue;
				var ramp = Ramp( f - s, e - f, options.RampFrames );
				if ( ramp <= 0f )
					continue;
				var err = footWorld[foot][f] - anchor;
				acc += new Vec3( err.x, 0f, err.z ) * ramp;
				wsum += ramp;
			}
			offset[f] = wsum > 0f ? acc * ( -1f / wsum ) : Vec3.Zero;
		}

		var fixedClip = new RigAnimFile
		{
			SourceModel = clip.SourceModel,
			RigAsset = clip.RigAsset,
			FrameRate = clip.FrameRate,
			FrameCount = frames,
		};
		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var name = skeleton.Bones[b].Name;
			if ( clip.Find( name ) is not { Keys.Count: > 0 } )
				continue;
			var track = new RigTrack { Name = name, Kind = RigTrackKind.Bone };
			for ( var f = 0; f < frames; f++ )
			{
				var pose = RigPose.FromXform( sampled[f][b] );
				if ( b == rootIdx )
					pose = new RigPose( pose.Position + offset[f], pose.Rotation, pose.Scale );
				track.Keys.Add( new RigKey { Frame = f, Local = pose } );
			}
			fixedClip.Tracks.Add( track );
		}
		foreach ( var t in clip.Tracks.Where( t => t.Kind != RigTrackKind.Bone ) )
			fixedClip.Tracks.Add( CloneTrack( t ) );

		var afterWorld = SampleFootWorld( SampleAll( fixedClip, skeleton, frames ), skeleton, feet );
		return ( fixedClip, before, MaxSkateInSpans( afterWorld, spans ), spans.Count );
	}

	/// <summary>
	/// Resample a clip into plain editable keys: one key per bone every
	/// <paramref name="everyN"/> frames (always including the last frame), then
	/// optionally prune with <see cref="ClipBake.Reduce"/>.
	/// </summary>
	public static RigAnimFile Bake( RigAnimFile clip, Skeleton skeleton, int everyN = 1, bool reduce = true )
	{
		ArgumentNullException.ThrowIfNull( clip );
		ArgumentNullException.ThrowIfNull( skeleton );
		everyN = Math.Max( 1, everyN );

		var last = clip.LastFrame;
		var baked = new RigAnimFile
		{
			SourceModel = clip.SourceModel,
			RigAsset = clip.RigAsset,
			FrameRate = clip.FrameRate,
			FrameCount = last + 1,
		};
		if ( last < 0 || skeleton.Count == 0 )
			return baked;

		var frames = new List<int>();
		for ( var f = 0; f <= last; f += everyN )
			frames.Add( f );
		if ( frames[^1] != last )
			frames.Add( last );

		var sampled = SampleAll( clip, skeleton, last + 1 );
		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var name = skeleton.Bones[b].Name;
			if ( clip.Find( name ) is not { Keys.Count: > 0 } )
				continue;
			var track = new RigTrack { Name = name, Kind = RigTrackKind.Bone };
			// Monotone keys: Reduce evaluates the curve between the keys, and the
			// default Smooth ease remaps t with a quadratic ease-in-out, so a
			// 3-frame gap on linear motion reads 0.033 off and only every other key
			// prunes (6 keys, not 2). Monotone Hermite through collinear samples is
			// exactly the line, so Reduce prunes to the endpoints.
			foreach ( var f in frames )
				track.Keys.Add( new RigKey { Frame = f, Local = RigPose.FromXform( sampled[f][b] ), Ease = RigEase.Monotone } );
			if ( reduce && track.Keys.Count > 2 )
				ClipBake.Reduce( track, track.Keys[0].Frame, track.Keys[^1].Frame );
			baked.Tracks.Add( track );
		}
		foreach ( var t in clip.Tracks.Where( t => t.Kind != RigTrackKind.Bone ) )
			baked.Tracks.Add( CloneTrack( t ) );
		return baked;
	}

	// --- helpers ---

	/// <summary>Local pose per frame per bone, sampled once (not per bone per frame).</summary>
	static Xform[][] SampleAll( RigAnimFile clip, Skeleton skeleton, int frames )
	{
		var sampled = new Xform[frames][];
		for ( var f = 0; f < frames; f++ )
			sampled[f] = clip.SampleLocal( skeleton, f );
		return sampled;
	}

	static Vec3[][] SampleFootWorld( Xform[][] sampled, Skeleton skeleton, int[] feet )
	{
		var world = new Vec3[feet.Length][];
		for ( var i = 0; i < feet.Length; i++ )
			world[i] = new Vec3[sampled.Length];
		for ( var f = 0; f < sampled.Length; f++ )
		{
			var model = RigAnimFile.ModelSpace( skeleton, sampled[f] );
			for ( var i = 0; i < feet.Length; i++ )
				world[i][f] = model[feet[i]].Origin;
		}
		return world;
	}

	static float MaxSkateInSpans( Vec3[][] footWorld, List<(int Foot, int Start, int End, Vec3 Anchor)> spans )
	{
		var max = 0f;
		foreach ( var (foot, s, e, anchor) in spans )
			for ( var f = s; f <= e; f++ )
				max = Math.Max( max, Horizontal( footWorld[foot][f] - anchor ) );
		return max;
	}

	static float Horizontal( Vec3 v ) => MathF.Sqrt( v.x * v.x + v.z * v.z );

	static float Ramp( int fromStart, int toEnd, int rampFrames )
	{
		if ( rampFrames <= 0 )
			return 1f;
		return MathF.Min( 1f, MathF.Min( fromStart, toEnd ) / (float)rampFrames );
	}

	static float[] Filter( float[] series, int frameRate, Options options ) =>
		options.Kind == SmoothKind.Butterworth
			? ButterworthLowpass( series, options.CutoffHz, frameRate )
			: GaussianSmooth( series, options.SigmaFrames );

	static float[] GaussianSmooth( float[] x, float sigma )
	{
		var n = x.Length;
		if ( n < 3 || sigma <= 0f )
			return (float[])x.Clone();
		var radius = Math.Max( 1, (int)MathF.Ceiling( 3f * sigma ) );
		var w = new float[2 * radius + 1];
		for ( var i = -radius; i <= radius; i++ )
			w[i + radius] = MathF.Exp( -( i * i ) / ( 2f * sigma * sigma ) );
		// Odd extension (2*x[0] - x[k]): the padded signal keeps the endpoint
		// value AND slope, so the kernel stays symmetric at the edges. Truncating
		// the kernel and renormalising instead folds the signal back on itself at
		// the first/last frames - on the mocap fixture that left 0.056 of 0.071
		// noise behind (the suite's gaussian check).
		var ext = new float[n + 2 * radius];
		for ( var i = 0; i < radius; i++ ) ext[i] = 2f * x[0] - x[radius - i];
		Array.Copy( x, 0, ext, radius, n );
		for ( var i = 0; i < radius; i++ ) ext[radius + n + i] = 2f * x[n - 1] - x[n - 2 - i];
		var wsum = 0f;
		for ( var i = 0; i < w.Length; i++ ) wsum += w[i];
		var y = new float[n];
		for ( var f = 0; f < n; f++ )
		{
			var acc = 0f;
			for ( var i = -radius; i <= radius; i++ )
				acc += w[i + radius] * ext[f + radius + i];
			y[f] = acc / wsum;
		}
		return y;
	}

	/// <summary>
	/// Second-order Butterworth low-pass, zero-phase (forward then backward, so the
	/// motion is not delayed). Edges are odd-extended (2*x[0] - x[k], the filtfilt
	/// default): the extension keeps the endpoint value and slope, so the first and
	/// last frames do not fold back on themselves. A plain mirror folds the signal
	/// at the ends - on the mocap fixture that left the 10 Hz noise untouched.
	/// </summary>
	static float[] ButterworthLowpass( float[] x, float cutoffHz, int frameRate )
	{
		var n = x.Length;
		if ( n < 3 || frameRate <= 0 || cutoffHz <= 0f )
			return (float[])x.Clone();

		var fs = (float)frameRate;
		var fc = Math.Min( cutoffHz, fs * 0.45f );
		var w0 = 2f * MathF.PI * fc / fs;
		var cos = MathF.Cos( w0 );
		var alpha = MathF.Sin( w0 ) / ( 2f * 0.70710678f );
		var b0 = ( 1f - cos ) / 2f;
		var b1 = 1f - cos;
		var b2 = ( 1f - cos ) / 2f;
		var a0 = 1f + alpha;
		var a1 = -2f * cos;
		var a2 = 1f - alpha;
		b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;

		var pad = Math.Min( n - 1, 32 );
		var ext = new float[n + 2 * pad];
		for ( var i = 0; i < pad; i++ ) ext[i] = 2f * x[0] - x[pad - i];
		Array.Copy( x, 0, ext, pad, n );
		for ( var i = 0; i < pad; i++ ) ext[pad + n + i] = 2f * x[n - 1] - x[n - 2 - i];

		float[] Pass( float[] input, bool backward )
		{
			var y = new float[input.Length];
			float x1 = 0f, x2 = 0f, y1 = 0f, y2 = 0f;
			if ( !backward )
			{
				for ( var i = 0; i < input.Length; i++ )
				{
					var v = b0 * input[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
					x2 = x1; x1 = input[i]; y2 = y1; y1 = v; y[i] = v;
				}
			}
			else
			{
				for ( var i = input.Length - 1; i >= 0; i-- )
				{
					var v = b0 * input[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
					x2 = x1; x1 = input[i]; y2 = y1; y1 = v; y[i] = v;
				}
			}
			return y;
		}

		var forward = Pass( ext, backward: false );
		var both = Pass( forward, backward: true );
		var y2 = new float[n];
		Array.Copy( both, pad, y2, 0, n );
		return y2;
	}

	/// <summary>Quaternion log map: rotation as an axis * angle vector.</summary>
	static Vec3 Log( Quat q )
	{
		var w = MathF.Max( -1f, MathF.Min( 1f, q.w ) );
		var angle = 2f * MathF.Acos( w );
		var s = MathF.Sqrt( Math.Max( 0f, 1f - w * w ) );
		if ( s < 1e-6f )
			return Vec3.Zero;
		return new Vec3( q.x / s, q.y / s, q.z / s ) * angle;
	}

	/// <summary>Quaternion exp map: axis * angle vector back to a rotation.</summary>
	static Quat Exp( Vec3 v )
	{
		var angle = v.Length;
		if ( angle < 1e-6f )
			return Quat.Identity;
		return Quat.FromAxisAngle( v * ( 1f / angle ), angle );
	}

	static float Dot( Quat a, Quat b ) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

	static int CountKeys( RigAnimFile clip ) => clip.Tracks.Sum( t => t.Keys.Count );

	static RigAnimFile Clone( RigAnimFile clip )
	{
		var c = new RigAnimFile
		{
			SourceModel = clip.SourceModel,
			RigAsset = clip.RigAsset,
			FrameRate = clip.FrameRate,
			FrameCount = clip.FrameCount,
		};
		foreach ( var t in clip.Tracks )
			c.Tracks.Add( CloneTrack( t ) );
		return c;
	}

	static RigTrack CloneTrack( RigTrack t )
	{
		var c = new RigTrack { Name = t.Name, Kind = t.Kind };
		foreach ( var k in t.Keys )
		{
			c.Keys.Add( new RigKey
			{
				Frame = k.Frame,
				Local = k.Local,
				Ease = k.Ease,
				TangentIn = k.TangentIn?.ToArray(),
				TangentOut = k.TangentOut?.ToArray(),
			} );
		}
		return c;
	}
}
