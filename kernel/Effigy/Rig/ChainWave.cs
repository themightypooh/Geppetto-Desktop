using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A wave travelling down a chain, laid over a clip: each bone swings by a sine whose phase lags
/// its depth below the first, so a bend rolls from root to tip - a tentacle writhing, a tail
/// sweeping, a cable's idle sway. Like <see cref="ChainSpring"/> it never asks which axis a bone
/// points along: the swing axis is worked out in model space from where each bone's child sits, and
/// applied as a turn about the bone's head on top of whatever the clip already does.
/// </summary>
public static class ChainWave
{
	public enum Plane
	{
		/// <summary>Swing side to side: about the model's up, as seen along the bone.</summary>
		Side,

		/// <summary>Swing up and down: about the horizontal across the bone.</summary>
		Up
	}

	public sealed class Settings
	{
		/// <summary>Peak swing per bone, degrees.</summary>
		public float Amplitude = 12f;

		/// <summary>Frames for one full swing.</summary>
		public float Period = 40f;

		/// <summary>Bones per wavelength: how far down the chain one swing has travelled when the root
		/// starts the next. Large is a stiff whole-chain sway, small a tight wriggle.</summary>
		public float Wavelength = 8f;

		/// <summary>0 swings every bone the same; 1 keeps the root still and gives the tip it all.</summary>
		public float Taper = 0.6f;

		public Plane Swing = Plane.Side;

		/// <summary>Frames to shift the wave by - give each of several chains its own so they never
		/// move as twins.</summary>
		public float Offset;
	}

	/// <summary>
	/// The clip's frames (<paramref name="local"/>, parent space) with the wave added to every bone in
	/// <paramref name="bones"/>, which must run root to tip (skeleton order). Other bones keep their
	/// locals and follow a waved parent.
	/// </summary>
	public static List<Xform[]> Apply( Skeleton skeleton, IReadOnlyList<Xform[]> local, IReadOnlyList<int> bones, Settings settings )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( local is null ) throw new ArgumentNullException( nameof( local ) );

		settings ??= new Settings();

		var depth = new Dictionary<int, int>();

		for ( var k = 0; k < bones.Count; k++ )
			depth[bones[k]] = k;

		var firstChild = new int[skeleton.Count];
		Array.Fill( firstChild, -1 );

		for ( var i = skeleton.Count - 1; i >= 0; i-- )
		{
			if ( skeleton.Bones[i].Parent >= 0 )
				firstChild[skeleton.Bones[i].Parent] = i;
		}

		var result = new List<Xform[]>( local.Count );
		var last = Math.Max( bones.Count - 1, 1 );
		var up = new Vec3( 0, 0, 1 );

		for ( var f = 0; f < local.Count; f++ )
		{
			var pose = local[f];
			var world = new Xform[skeleton.Count];

			for ( var i = 0; i < skeleton.Count; i++ )
			{
				var parent = skeleton.Bones[i].Parent;
				var rest = parent >= 0 ? world[parent] * pose[i] : pose[i];
				world[i] = rest;

				if ( !depth.TryGetValue( i, out var d ) )
					continue;

				var offset = firstChild[i] >= 0 ? pose[firstChild[i]].Origin : pose[i].Inverse.TransformDirection( pose[i].Origin );

				if ( offset.LengthSquared < 1e-10f )
					continue;

				var along = (rest.TransformPoint( offset ) - rest.Origin).Normal;
				var axis = settings.Swing == Plane.Side
					? (up - along * Vec3.Dot( up, along )).Normal
					: Vec3.Cross( along, up ).Normal;

				if ( axis.LengthSquared < 1e-8f )
					axis = Vec3.Cross( along, new Vec3( 1, 0, 0 ) ).Normal;

				var gain = (1f - settings.Taper) + settings.Taper * d / last;
				var phase = 2f * MathF.PI * ((f + settings.Offset) / MathF.Max( settings.Period, 1f ) - d / MathF.Max( settings.Wavelength, 0.01f ));
				var angle = settings.Amplitude * gain * MathF.Sin( phase ) * MathF.PI / 180f;

				world[i] = TwoBoneIk.About( rest.Origin, Xform.Rotate( axis, angle ) ) * rest;
			}

			var waved = (Xform[])pose.Clone();

			foreach ( var i in bones )
			{
				var parent = skeleton.Bones[i].Parent;
				waved[i] = parent >= 0 ? world[parent].Inverse * world[i] : world[i];
			}

			result.Add( waved );
		}

		return result;
	}

	/// <summary>Named waves, shared by Marionette's menu, its MCP tool and the headless sim.</summary>
	public static readonly (string Key, string Name, Settings Settings)[] Presets =
	{
		("sway", "Gentle sway", new Settings { Amplitude = 8f, Period = 60f, Wavelength = 12f, Taper = 0.7f }),
		("writhe", "Writhe", new Settings { Amplitude = 15f, Period = 36f, Wavelength = 6f, Taper = 0.5f }),
		("wriggle", "Tight wriggle", new Settings { Amplitude = 10f, Period = 18f, Wavelength = 3f, Taper = 0.3f }),
		("undulate", "Undulate (up and down)", new Settings { Amplitude = 12f, Period = 40f, Wavelength = 8f, Taper = 0.6f, Swing = Plane.Up }),
	};

	/// <summary>A copy of a preset by key or index, or null.</summary>
	public static Settings FindPreset( string keyOrIndex )
	{
		var i = int.TryParse( keyOrIndex, out var n ) ? n : Array.FindIndex( Presets, p => string.Equals( p.Key, keyOrIndex, StringComparison.OrdinalIgnoreCase ) );

		if ( i < 0 || i >= Presets.Length )
			return null;

		var s = Presets[i].Settings;
		return new Settings { Amplitude = s.Amplitude, Period = s.Period, Wavelength = s.Wavelength, Taper = s.Taper, Swing = s.Swing, Offset = s.Offset };
	}

	/// <summary>A phase offset that differs from chain to chain but is the same every time for one
	/// chain - so five cables waved one after another never move as twins, and re-baking one gives
	/// the same result.</summary>
	public static float OffsetFor( string chainRoot, float period )
	{
		// Numbered chains (cable0, cable1, tentacle_3...) are spread by the golden ratio on their
		// number, which keeps any run of neighbours far apart in phase; anything else by a hash.
		var digits = new string( (chainRoot ?? "").SkipWhile( c => !char.IsDigit( c ) ).TakeWhile( char.IsDigit ).ToArray() );
		double fraction;

		if ( digits.Length > 0 && int.TryParse( digits, out var n ) )
			fraction = (n + 1) * 0.6180339887 % 1.0;
		else
		{
			var hash = 17;

			foreach ( var c in chainRoot ?? "" )
				hash = unchecked(hash * 31 + c);

			fraction = (hash & 0x7fffffff) % 1000 / 1000.0;
		}

		return (float)fraction * MathF.Max( period, 1f );
	}

	/// <summary>The period nearest <paramref name="period"/> that fits a whole number of swings into
	/// <paramref name="frames"/> - so a looping clip's wave meets itself at the seam.</summary>
	public static float FitPeriod( float period, int frames )
	{
		if ( frames < 1 || period <= 0f )
			return period;

		var cycles = MathF.Max( MathF.Round( frames / period ), 1f );
		return frames / cycles;
	}

	// --- Lane G: stackable procedural layers --------------------------------------------------

	/// <summary>Apply per-frame world-space rotations to a frame list, the same way
	/// <see cref="Apply"/> does: each bone's world pose is built from its (already rotated)
	/// parent, rotated about its own head, then converted back to parent space. Bones not in a
	/// frame's map keep their locals but still follow a rotated parent. This is the shared engine
	/// for the Lane G generators, so a layer never has to know which axis a bone points along.</summary>
	public static List<Xform[]> ApplyWorldRotations( Skeleton skeleton, IReadOnlyList<Xform[]> local,
		IReadOnlyList<IReadOnlyDictionary<int, (Vec3 Axis, float Radians)>> rotations )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( local is null ) throw new ArgumentNullException( nameof( local ) );
		if ( rotations is null ) throw new ArgumentNullException( nameof( rotations ) );
		if ( rotations.Count != local.Count ) throw new ArgumentException( $"rotations has {rotations.Count} frame(s) for {local.Count} frame(s)." );

		var result = new List<Xform[]>( local.Count );

		for ( var f = 0; f < local.Count; f++ )
		{
			var pose = local[f];
			var map = rotations[f];
			var world = new Xform[skeleton.Count];

			for ( var i = 0; i < skeleton.Count; i++ )
			{
				var parent = skeleton.Bones[i].Parent;
				var rest = parent >= 0 ? world[parent] * pose[i] : pose[i];
				world[i] = rest;

				if ( map is not null && map.TryGetValue( i, out var rot ) && MathF.Abs( rot.Radians ) > 1e-9f )
					world[i] = TwoBoneIk.About( rest.Origin, Xform.Rotate( rot.Axis, rot.Radians ) ) * rest;
			}

			var waved = (Xform[])pose.Clone();

			if ( map is not null )
			{
				foreach ( var (i, _) in map )
				{
					var parent = skeleton.Bones[i].Parent;
					waved[i] = parent >= 0 ? world[parent].Inverse * world[i] : world[i];
				}
			}

			result.Add( waved );
		}

		return result;
	}

	static AnimClip CopyClip( AnimClip clip, List<Xform[]> frames ) => AnimClip.WithFrames( clip, frames );

	/// <summary>Travelling S-wave propulsion over a chain (fish swimming, snakes, eels, capes):
	/// each bone swings by a sine whose phase lags its depth, so the bend rolls root to tip.
	/// <paramref name="envelopeCurve"/>, when given with one gain per chain bone, scales each
	/// bone's swing directly; otherwise the swing tapers from 0.4 at the root to full at the tip.
	/// <paramref name="recoilHeadDeg"/> counter-rotates <paramref name="headBone"/> against the
	/// root's phase, so a swimming head stays steadier than its tail. Returns a new clip; the
	/// input clip is untouched, so layers stack by feeding one generator's output into the next.</summary>
	public static AnimClip ApplyLocomotion( AnimClip clip, Skeleton skeleton, IReadOnlyList<int> chainBones,
		float freqHz, float amplitudeDeg, float wavelength, float[] envelopeCurve = null,
		float recoilHeadDeg = 0f, int headBone = -1 )
	{
		if ( clip is null ) throw new ArgumentNullException( nameof( clip ) );
		if ( chainBones is null || chainBones.Count == 0 ) throw new ArgumentException( "locomotion needs at least one chain bone." );
		if ( freqHz <= 0f ) throw new ArgumentOutOfRangeException( nameof( freqHz ), "frequency must be positive." );
		if ( wavelength <= 0f ) throw new ArgumentOutOfRangeException( nameof( wavelength ), "wavelength must be positive." );

		var period = clip.FrameRate / freqHz;

		if ( clip.Looping )
			period = FitPeriod( period, Math.Max( clip.FrameCount, 1 ) );

		var last = Math.Max( chainBones.Count - 1, 1 );
		var rotations = new List<IReadOnlyDictionary<int, (Vec3, float)>>();

		for ( var f = 0; f < clip.FrameCount; f++ )
		{
			var map = new Dictionary<int, (Vec3, float)>();

			for ( var d = 0; d < chainBones.Count; d++ )
			{
				var gain = envelopeCurve is not null && envelopeCurve.Length == chainBones.Count
					? envelopeCurve[d]
					: 0.4f + 0.6f * d / last;
				var phase = 2f * MathF.PI * (f / period - d / wavelength);
				map[chainBones[d]] = (new Vec3( 0, 0, 1 ), amplitudeDeg * gain * MathF.Sin( phase ) * MathF.PI / 180f);
			}

			if ( headBone >= 0 && MathF.Abs( recoilHeadDeg ) > 1e-9f )
			{
				var recoil = -recoilHeadDeg * MathF.Sin( 2f * MathF.PI * f / period ) * MathF.PI / 180f;

				if ( map.TryGetValue( headBone, out var existing ) )
					map[headBone] = (existing.Item1, existing.Item2 + recoil);
				else
					map[headBone] = (new Vec3( 0, 0, 1 ), recoil);
			}

			rotations.Add( map );
		}

		return CopyClip( clip, ApplyWorldRotations( skeleton, clip.Frames, rotations ) );
	}

	/// <summary>Out-of-water / stunt motion over a chain: <c>flop</c> (C-bend with jaw + opercle
	/// flare + fin flutter), <c>trick</c> (accelerating whip), <c>spike</c> (nose-down tremor),
	/// <c>dazed</c> (decaying list + slow sway). Unknown styles throw instead of silently
	/// producing a flop. Returns a new clip; the input is untouched.</summary>
	public static AnimClip ApplyThrash( AnimClip clip, Skeleton skeleton, IReadOnlyList<int> chainBones,
		int jawBone, IReadOnlyList<int> opercleBones, IReadOnlyList<int> finBones, string style )
	{
		if ( clip is null ) throw new ArgumentNullException( nameof( clip ) );
		if ( chainBones is null || chainBones.Count == 0 ) throw new ArgumentException( "thrash needs at least one chain bone." );

		var key = (style ?? "").Trim().ToLowerInvariant();
		var frames = Math.Max( clip.FrameCount, 1 );
		var last = Math.Max( chainBones.Count - 1, 1 );
		var rotations = new List<IReadOnlyDictionary<int, (Vec3, float)>>();

		for ( var f = 0; f < clip.FrameCount; f++ )
		{
			var map = new Dictionary<int, (Vec3, float)>();
			var t = frames <= 1 ? 0f : f / (float)(frames - 1);

			void Add( int bone, Vec3 axis, float radians )
			{
				if ( bone < 0 )
					return;

				if ( map.TryGetValue( bone, out var existing ) )
					map[bone] = (existing.Item1, existing.Item2 + radians);
				else
					map[bone] = (axis, radians);
			}

			switch ( key )
			{
				case "flop":
				{
					var period = MathF.Max( 8f, frames / 2f );
					var wave = MathF.Sin( 2f * MathF.PI * f / period );

					for ( var d = 0; d < chainBones.Count; d++ )
						Add( chainBones[d], new Vec3( 0, 0, 1 ), 55f * MathF.Sin( MathF.PI * d / last ) * wave * MathF.PI / 180f );

					Add( jawBone, new Vec3( 0, 1, 0 ), 25f * MathF.Max( 0f, wave ) * MathF.PI / 180f );

					if ( opercleBones is not null )
					{
						foreach ( var o in opercleBones )
						{
							var sign = skeleton.Bones[o].Name.EndsWith( "_R", StringComparison.Ordinal ) ? -1f : 1f;
							Add( o, new Vec3( 1, 0, 0 ), sign * 10f * wave * MathF.PI / 180f );
						}
					}

					if ( finBones is not null )
					{
						foreach ( var fin in finBones )
							Add( fin, new Vec3( 0, 0, 1 ), 20f * MathF.Sin( 4f * MathF.PI * f / period ) * MathF.PI / 180f );
					}

					break;
				}
				case "trick":
				{
					var period = MathF.Max( 8f, frames / 2f );

					for ( var d = 0; d < chainBones.Count; d++ )
					{
						var phase = 2f * MathF.PI * (f / period) * (1f + t) - 0.5f * d;
						Add( chainBones[d], new Vec3( 0, 0, 1 ), 70f * (0.4f + 0.6f * d / last) * MathF.Sin( phase ) * MathF.PI / 180f );
					}

					Add( jawBone, new Vec3( 0, 1, 0 ), 30f * MathF.Max( 0f, MathF.Sin( 2f * MathF.PI * f / period ) ) * MathF.PI / 180f );
					break;
				}
				case "spike":
				{
					Add( chainBones[0], new Vec3( 0, 1, 0 ), -15f * MathF.PI / 180f );

					for ( var d = 0; d < chainBones.Count; d++ )
						Add( chainBones[d], new Vec3( 0, 0, 1 ), 8f * MathF.Sin( 2f * MathF.PI * f / 3f ) * MathF.PI / 180f );

					Add( jawBone, new Vec3( 0, 1, 0 ), 5f * MathF.Sin( 2f * MathF.PI * f / 3f ) * MathF.PI / 180f );
					break;
				}
				case "dazed":
				{
					var list = 25f * (1f - t) * (0.5f + 0.5f * MathF.Sin( 2f * MathF.PI * t )) * MathF.PI / 180f;
					var sway = 10f * MathF.Sin( 2f * MathF.PI * t ) * MathF.PI / 180f;

					foreach ( var bone in chainBones )
					{
						// One axis-angle per bone: roll and sway combined, so neither is dropped.
						var mag = MathF.Sqrt( list * list + sway * sway );

						if ( mag > 1e-9f )
							Add( bone, new Vec3( list / mag, 0, sway / mag ), mag );
					}

					Add( jawBone, new Vec3( 0, 1, 0 ), 5f * MathF.Sin( 2f * MathF.PI * t ) * MathF.PI / 180f );
					break;
				}
				default:
					throw new ArgumentException( $"No thrash style '{style}'. Styles: flop, trick, spike, dazed." );
			}

			rotations.Add( map );
		}

		return CopyClip( clip, ApplyWorldRotations( skeleton, clip.Frames, rotations ) );
	}
}
