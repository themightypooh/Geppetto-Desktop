using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A baked animation: one local-to-parent pose per bone per frame.
///
/// BAKED, NOT KEYED, and that is the whole design rather than a simplification. Every tool that
/// authors animation against this kernel keeps its own curve model — Marionette's `.riganim` has
/// keyframes, five easing modes and IK solves layered over each other — and none of that survives
/// a model compiler anyway: ModelDoc resamples to a fixed rate on import regardless of what the
/// source file thought it was doing. Sampling to a flat frame list at the boundary means the
/// kernel never has to know what a curve is, and the authoring side keeps exactly one job — say
/// where every bone is on frame N.
///
/// POSES ARE LOCAL TO THE PARENT, matching <see cref="Bone.Local"/> and every skeletal format
/// worth naming. A world-space pose list would need the skeleton to mean anything at all, and
/// would come apart the moment a parent moved — which is the normal case, not the edge one.
///
/// A BONE THAT IS NEVER POSED STILL GETS A FRAME. There is no "no value here" entry, because a
/// hole would have to be filled by whoever reads it and every reader would fill it differently.
/// The sampler writes the bind pose for an untouched bone, which is what the animator saw in the
/// viewport and therefore what they meant.
/// </summary>
public sealed class AnimClip
{
	/// <summary>What the clip is called inside the compiled model — the name AnimGraph and
	/// <c>SetAnimParameter</c> end up seeing, so it is not decorative.</summary>
	public string Name = "anim";

	/// <summary>Frames per second the frames below are sampled at.</summary>
	public float FrameRate = 30f;

	public bool Looping;

	/// <summary>Frames[frame][bone], the bone index being its position in
	/// <see cref="Skeleton.Bones"/>. Every frame is the same length as the skeleton.</summary>
	public List<Xform[]> Frames = new();

	public int FrameCount => Frames.Count;

	/// <summary>
	/// Scales[frame][bone]: each bone's uniform scale, or null - the usual case - when nothing in
	/// the clip scales. Kept apart from <see cref="Frames"/> because a pose's rotation goes into the
	/// file as a quaternion read off the basis, and a scale folded into that basis would come out as
	/// a skew. Uniform because a Source 2 bone scales in one number.
	/// </summary>
	public List<float[]> Scales;

	/// <summary>The bones whose scale is anything but 1 on some frame - the ones a scale channel is
	/// worth writing for.</summary>
	public bool Scaled( int bone )
	{
		if ( Scales is null )
			return false;

		foreach ( var frame in Scales )
		{
			if ( frame is not null && MathF.Abs( frame[bone] - 1f ) > 1e-5f )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Length in seconds.
	///
	/// N frames span N-1 intervals, not N — a two-frame clip at 30fps lasts a thirtieth of a
	/// second, not a fifteenth. Getting this wrong stretches every clip by one frame, which reads
	/// as animation that drifts slowly out of sync with anything it was timed against rather than
	/// as an obvious bug.
	/// </summary>
	public float Duration => FrameCount <= 1 ? 0f : (FrameCount - 1) / MathF.Max( FrameRate, 0.0001f );

	/// <summary>The time in seconds at which a given frame lands.</summary>
	public float TimeOf( int frame ) => frame / MathF.Max( FrameRate, 0.0001f );

	public void AddFrame( Xform[] pose )
	{
		if ( pose is null )
			throw new ArgumentNullException( nameof( pose ) );

		Frames.Add( pose );
		Scales?.Add( null );
	}

	/// <summary>A frame with its bones' uniform scales. The first scaled frame starts the
	/// <see cref="Scales"/> list, back-filling earlier frames at 1.</summary>
	public void AddFrame( Xform[] pose, float[] scales )
	{
		if ( pose is null )
			throw new ArgumentNullException( nameof( pose ) );

		if ( scales is null )
		{
			AddFrame( pose );
			return;
		}

		if ( Scales is null )
		{
			Scales = new List<float[]>();

			for ( var f = 0; f < Frames.Count; f++ )
				Scales.Add( null );
		}

		Frames.Add( pose );
		Scales.Add( scales );
	}

	/// <summary>A bone's scale on a frame - 1 where the clip does not scale.</summary>
	public float ScaleAt( int frame, int bone ) => Scales?[frame] is { } s ? s[bone] : 1f;

	/// <summary>
	/// Morph (flex controller) values per frame, by morph name - one value per frame in
	/// <see cref="Frames"/>, 0 where the clip does not drive it. Empty for a clip that drives none,
	/// which then exports exactly as before. Written by DmxAnimWriter as flex channels.
	/// </summary>
	public SortedDictionary<string, List<float>> Morphs = new( StringComparer.Ordinal );

	/// <summary>The morph values of the frame just added with AddFrame. A morph first named here is
	/// back-filled with 0 for the frames before it; one not named is 0 on this frame.</summary>
	public void SetMorphs( IReadOnlyDictionary<string, float> values )
	{
		var frame = FrameCount - 1;

		if ( frame < 0 )
			return;

		foreach ( var (name, _) in values ?? new Dictionary<string, float>() )
		{
			if ( !string.IsNullOrWhiteSpace( name ) && !Morphs.ContainsKey( name ) )
				Morphs[name] = new List<float>();
		}

		foreach ( var (name, list) in Morphs )
		{
			while ( list.Count < frame )
				list.Add( 0f );

			var value = values is not null && values.TryGetValue( name, out var v ) ? v : 0f;

			if ( list.Count == frame )
				list.Add( value );
			else
				list[frame] = value;
		}
	}

	/// <summary>A morph's value at a frame; 0 where it is not driven.</summary>
	public float MorphAt( string name, int frame ) =>
		Morphs.TryGetValue( name, out var list ) && frame >= 0 && frame < list.Count ? list[frame] : 0f;

	/// <summary>
	/// Check the clip against the skeleton it claims to animate, and say what is wrong rather than
	/// leaving it to be discovered as a model that will not load.
	///
	/// Returns null when the clip is writable. A ragged frame list is the mistake worth catching
	/// here: it comes from a sampler that grew a bone mid-loop, and downstream it is an index walk
	/// off the end of an array in the middle of writing a file.
	/// </summary>
	public string Validate( Skeleton skeleton )
	{
		if ( skeleton is null || skeleton.Count == 0 )
			return "a clip needs a skeleton with at least one bone";

		if ( FrameCount == 0 )
			return "a clip needs at least one frame";

		if ( FrameRate <= 0f )
			return $"frame rate must be positive, not {FrameRate}";

		for ( var f = 0; f < Frames.Count; f++ )
		{
			if ( Frames[f] is null )
				return $"frame {f} is null";

			if ( Frames[f].Length != skeleton.Count )
				return $"frame {f} has {Frames[f].Length} pose(s) for a skeleton of {skeleton.Count} bone(s)";

			if ( Scales?[f] is { } scales && scales.Length != skeleton.Count )
				return $"frame {f} has {scales.Length} scale(s) for a skeleton of {skeleton.Count} bone(s)";
		}

		return null;
	}

	// --- Lane G: stackable procedural layers --------------------------------------------------
	// Every generator below returns a NEW clip and leaves its input untouched, so layers compose:
	// ApplyLocomotion(base) -> ApplyThrash(...) -> BakeSecondary(...) -> ApplyBreathing(...).
	// The one signature deviation from the roadmap is deliberate: the roadmap's generator
	// signatures name no skeleton, but a baked frame list is meaningless without one (bone
	// indices are positions in Skeleton.Bones), so every generator takes the skeleton explicitly.

	/// <summary>Copy a clip's metadata onto a new frame list. Scales and morphs are deep-copied
	/// so a layer never mutates the clip it was stacked on.</summary>
	internal static AnimClip WithFrames( AnimClip clip, List<Xform[]> frames )
	{
		var copy = new AnimClip { Name = clip.Name, FrameRate = clip.FrameRate, Looping = clip.Looping, Frames = frames };

		if ( clip.Scales is not null )
			copy.Scales = clip.Scales.Select( s => s is null ? null : (float[])s.Clone() ).ToList();

		foreach ( var (name, list) in clip.Morphs )
			copy.Morphs[name] = new List<float>( list );

		return copy;
	}

	/// <summary>Travelling S-wave propulsion; see <see cref="ChainWave.ApplyLocomotion"/>.</summary>
	public static AnimClip ApplyLocomotion( AnimClip clip, Skeleton skeleton, IReadOnlyList<int> chainBones,
		float freqHz, float amplitudeDeg, float wavelength, float[] envelopeCurve = null,
		float recoilHeadDeg = 0f, int headBone = -1 ) =>
		ChainWave.ApplyLocomotion( clip, skeleton, chainBones, freqHz, amplitudeDeg, wavelength, envelopeCurve, recoilHeadDeg, headBone );

	/// <summary>Flop / trick / spike / dazed motion; see <see cref="ChainWave.ApplyThrash"/>.</summary>
	public static AnimClip ApplyThrash( AnimClip clip, Skeleton skeleton, IReadOnlyList<int> chainBones,
		int jawBone, IReadOnlyList<int> opercleBones, IReadOnlyList<int> finBones, string style ) =>
		ChainWave.ApplyThrash( clip, skeleton, chainBones, jawBone, opercleBones, finBones, style );

	/// <summary>Spring follow-through; see <see cref="ChainSpring.BakeSecondary"/>.</summary>
	public static AnimClip BakeSecondary( AnimClip clip, Skeleton skeleton, IReadOnlyList<int> springBones,
		float stiffness, float damping, float gravityFactor = 1f ) =>
		ChainSpring.BakeSecondary( clip, skeleton, springBones, stiffness, damping, gravityFactor );

	/// <summary>Subtle alive idle: ribs expand by <paramref name="depth"/> (a fraction, e.g. 0.03
	/// for 3%) at <paramref name="rateHz"/>, the jaw opens by up to <paramref name="depth"/>
	/// radians in phase, and gill bones flutter at half that. A full period returns every bone
	/// to its base pose, so breathing loops cleanly. Returns a new clip.</summary>
	public static AnimClip ApplyBreathing( AnimClip clip, Skeleton skeleton, IReadOnlyList<int> ribBones,
		int jawBone, IReadOnlyList<int> gillBones, float rateHz, float depth )
	{
		if ( clip is null ) throw new ArgumentNullException( nameof( clip ) );
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( rateHz <= 0f ) throw new ArgumentOutOfRangeException( nameof( rateHz ), "breathing rate must be positive." );

		var rotations = new List<IReadOnlyDictionary<int, (Vec3, float)>>();

		for ( var f = 0; f < clip.FrameCount; f++ )
		{
			var phase = 2f * MathF.PI * rateHz * f / MathF.Max( clip.FrameRate, 0.0001f );
			var map = new Dictionary<int, (Vec3, float)>();

			if ( jawBone >= 0 )
				map[jawBone] = (new Vec3( 0, 1, 0 ), depth * MathF.Sin( phase ));

			if ( gillBones is not null )
			{
				foreach ( var gill in gillBones )
					map[gill] = (new Vec3( 0, 0, 1 ), depth * 0.5f * MathF.Sin( phase ));
			}

			rotations.Add( map );
		}

		var breathed = ChainWave.ApplyWorldRotations( skeleton, clip.Frames, rotations );
		var result = WithFrames( clip, breathed );

		if ( ribBones is not null && ribBones.Count > 0 )
		{
			if ( result.Scales is null )
			{
				result.Scales = new List<float[]>();

				for ( var f = 0; f < clip.FrameCount; f++ )
					result.Scales.Add( null );
			}

			for ( var f = 0; f < clip.FrameCount; f++ )
			{
				var phase = 2f * MathF.PI * rateHz * f / MathF.Max( clip.FrameRate, 0.0001f );
				var scales = result.Scales[f];

				if ( scales is null )
				{
					scales = new float[skeleton.Count];

					for ( var b = 0; b < skeleton.Count; b++ )
						scales[b] = 1f;

					result.Scales[f] = scales;
				}

				foreach ( var rib in ribBones )
					scales[rib] *= 1f + depth * MathF.Sin( phase );
			}
		}

		return result;
	}
}
