#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Effigy;

/// <summary>
/// A persistent pose library: named poses, each a bone name → local <see cref="RigPose"/>,
/// saved as JSON. This is the persistent replacement for the script-session-only
/// <c>pose_preset</c> verb in <see cref="BuildScriptAnim"/>: presets die when the script
/// ends, a library survives in a sidecar file next to the model, the way <see cref="FaceRig"/>
/// keeps its presets in <c>NAME.facerig.json</c> (this library uses <c>NAME.poselib.json</c>
/// and never touches Face Poser presets).
///
/// SCOPE (2D suite, lane 8): the library is rig-agnostic — a pose is just bone names and
/// local transforms, so a 2D cutout puppet (flat mesh + bones + Bone tracks, the same
/// tracks the 2D clip bridge previews) uses it exactly as a 3D rig does. It does NOT own
/// retargeting, IK, corrective morphs, or face settings; those belong to other sessions.
/// A pose has no skeleton attached, so applying it to a rig that lacks a bone silently
/// skips that bone's key (the log counts what was applied) rather than failing the
/// whole apply — a library is meant to be shared across rigs that share bone names.
///
/// FORMAT: {"version":1,"poses":{"wave":{"bones":{"arm_L":{"pos":[x,y,z],
/// "rot":[x,y,z,w],"scale":[x,y,z]}}}}}. Rotation is a unit quaternion, not Euler
/// angles, so a round-trip never re-derives angles. On load, a zero-length rotation
/// becomes Identity and any other rotation is normalised; a bad array length or a
/// non-finite number fails loudly instead of loading a corrupt pose.
/// </summary>
public sealed class PoseLibrary
{
	public int Version { get; set; } = 1;

	public Dictionary<string, Pose> Poses { get; set; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>One stored pose: bone name → local transform, in the file's plain form.</summary>
	public sealed class Pose
	{
		public Dictionary<string, BonePose> Bones { get; set; } = new( StringComparer.OrdinalIgnoreCase );
	}

	/// <summary>One bone's local transform as plain float arrays, so JSON round-trips exactly.
/// <see cref="Vec3"/> and <see cref="Quat"/> are readonly-field structs and would not
/// survive System.Text.Json on their own.</summary>
	public sealed class BonePose
	{
		public float[] Pos { get; set; } = { 0f, 0f, 0f };
		public float[] Rot { get; set; } = { 0f, 0f, 0f, 1f };
		public float[] Scale { get; set; } = { 1f, 1f, 1f };

		public RigPose ToRigPose()
		{
			var r = new Quat( Rot[0], Rot[1], Rot[2], Rot[3] );
			var len = MathF.Sqrt( r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w );
			if ( len <= 0f || !float.IsFinite( len ) ) r = Quat.Identity;
			else if ( MathF.Abs( len - 1f ) > 1e-6f ) r = new Quat( r.x / len, r.y / len, r.z / len, r.w / len );
			return new RigPose( new Vec3( Pos[0], Pos[1], Pos[2] ), r, new Vec3( Scale[0], Scale[1], Scale[2] ) );
		}

		public static BonePose FromRigPose( RigPose pose )
		{
			var r = pose.Rotation;
			var len = MathF.Sqrt( r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w );
			if ( len <= 0f || !float.IsFinite( len ) ) r = Quat.Identity;
			else if ( MathF.Abs( len - 1f ) > 1e-6f ) r = new Quat( r.x / len, r.y / len, r.z / len, r.w / len );
			return new BonePose
			{
				Pos = new[] { pose.Position.x, pose.Position.y, pose.Position.z },
				Rot = new[] { r.x, r.y, r.z, r.w },
				Scale = new[] { pose.Scale.x, pose.Scale.y, pose.Scale.z },
			};
		}
	}

	// CamelCase on write so the file matches the documented format, case-insensitive
	// on read so a hand-edited file with PascalCase keys is not silently empty.
	static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

	/// <summary>Sidecar path for a model, mirroring <see cref="FaceRig.SidecarPath"/>.</summary>
	public static string SidecarPath( string model ) =>
		Path.Combine( Path.GetDirectoryName( Path.GetFullPath( model ) ) ?? ".", Path.GetFileNameWithoutExtension( model ) + ".poselib.json" );

	public static PoseLibrary Load( string path )
	{
		if ( !File.Exists( path ) )
			throw new FileNotFoundException( $"no such pose library '{path}'" );
		PoseLibrary lib;
		try
		{
			lib = JsonSerializer.Deserialize<PoseLibrary>( File.ReadAllText( path ), Json )
				?? throw new InvalidDataException( $"'{path}' is not a pose library." );
		}
		catch ( JsonException ex )
		{
			throw new InvalidDataException( $"'{path}' is not a valid pose library: {ex.Message}", ex );
		}
		lib.Normalize();
		return lib;
	}

	public void Save( string path ) => File.WriteAllText( path, JsonSerializer.Serialize( this, Json ) + "\n" );

	/// <summary>Rebuild dictionaries case-insensitively (JSON deserialisation drops the
	/// comparer) and validate every stored transform; fails loudly on corrupt data.</summary>
	void Normalize()
	{
		var poses = new Dictionary<string, Pose>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (name, pose) in Poses )
		{
			if ( string.IsNullOrWhiteSpace( name ) ) throw new InvalidDataException( "pose library has a pose with an empty name." );
			if ( pose is null || pose.Bones is null ) throw new InvalidDataException( $"pose '{name}' is empty." );
			var bones = new Dictionary<string, BonePose>( StringComparer.OrdinalIgnoreCase );
			foreach ( var (bone, bp) in pose.Bones )
			{
				if ( string.IsNullOrWhiteSpace( bone ) ) throw new InvalidDataException( $"pose '{name}' has a bone with an empty name." );
				if ( bp is null || bp.Pos is not { Length: 3 } || bp.Rot is not { Length: 4 } || bp.Scale is not { Length: 3 } )
					throw new InvalidDataException( $"pose '{name}' bone '{bone}' does not have pos[3]/rot[4]/scale[3]." );
				if ( bp.Pos.Concat( bp.Rot ).Concat( bp.Scale ).Any( v => !float.IsFinite( v ) ) )
					throw new InvalidDataException( $"pose '{name}' bone '{bone}' has a non-finite number." );
				bones[bone] = bp;
			}
			poses[name] = new Pose { Bones = bones };
		}
		Poses = poses;
	}

	/// <summary>Add or replace a pose. Empty names and empty poses fail loudly.</summary>
	public void SetPose( string name, IReadOnlyDictionary<string, RigPose> pose )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "pose name must not be empty.", nameof( name ) );
		ArgumentNullException.ThrowIfNull( pose );
		if ( pose.Count == 0 ) throw new ArgumentException( $"pose '{name}' has no bones.", nameof( pose ) );
		var stored = new Pose();
		foreach ( var (bone, p) in pose )
		{
			if ( string.IsNullOrWhiteSpace( bone ) ) throw new ArgumentException( "pose bone name must not be empty.", nameof( pose ) );
			stored.Bones[bone] = BonePose.FromRigPose( p );
		}
		Poses[name] = stored;
	}

	/// <summary>The named pose as bone → local transform, or null if the library has no such pose.
/// The returned dictionary is a copy; editing it does not change the library.</summary>
	public Dictionary<string, RigPose>? GetPose( string name )
	{
		if ( name is null || !Poses.TryGetValue( name, out var pose ) ) return null;
		return pose.Bones.ToDictionary( kv => kv.Key, kv => kv.Value.ToRigPose(), StringComparer.OrdinalIgnoreCase );
	}

	/// <summary>Store a copy of a pose under a name, replacing any pose already stored there.</summary>
	public void Store( string name, IReadOnlyDictionary<string, RigPose> pose ) => SetPose( name, pose );

	/// <summary>Store the timeline's evaluated pose at a frame under a name.</summary>
	public void StoreFromTimeline( string name, NlaTimeline timeline, float frame )
	{
		ArgumentNullException.ThrowIfNull( timeline );
		SetPose( name, timeline.Evaluate( frame ) );
	}

	/// <summary>The stored pose, or null when no pose has that name. The returned map is a copy.</summary>
	public Dictionary<string, RigPose>? Recall( string name ) => GetPose( name );

	/// <summary>
	/// Blend a recalled pose over an evaluated pose set by weight: bones the pose names move
	/// toward the pose, bones it does not name stay exactly where they were.
	/// </summary>
	public static Dictionary<string, RigPose> Apply( IReadOnlyDictionary<string, RigPose> under, IReadOnlyDictionary<string, RigPose> pose, float weight ) =>
		Blend( under ?? new Dictionary<string, RigPose>(), pose ?? new Dictionary<string, RigPose>(), weight );

	public bool HasPose( string name ) => name is not null && Poses.ContainsKey( name );

	public bool RemovePose( string name ) => name is not null && Poses.Remove( name );

	public IReadOnlyList<string> Names() => Poses.Keys.OrderBy( n => n, StringComparer.OrdinalIgnoreCase ).ToList();

	/// <summary>Parse a mask option (comma-separated bone names, <c>*</c> as a glob) into
	/// patterns, or null for "every bone". Empty entries are ignored.</summary>
	public static List<string>? ParseMask( string? mask ) =>
		mask is null ? null
		: mask.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).ToList() is { Count: > 0 } list ? list : null;

	/// <summary>True when <paramref name="bone"/> matches any mask pattern, or when the
	/// mask is null (no mask = every bone). Matching is case-insensitive; <c>*</c>
	/// matches any run of characters, including none.</summary>
	public static bool MatchesMask( string bone, List<string>? mask )
	{
		if ( mask is null ) return true;
		return mask.Any( pattern => Glob( bone, pattern ) );
	}

	static bool Glob( string text, string pattern )
	{
		var ti = 0; var pi = 0; var star = -1; var mark = 0;
		while ( ti < text.Length )
		{
			if ( pi < pattern.Length && ( pattern[pi] == '*' || char.ToLowerInvariant( pattern[pi] ) == char.ToLowerInvariant( text[ti] ) ) )
			{
				if ( pattern[pi] == '*' ) { star = pi++; mark = ti; }
				else { pi++; ti++; }
			}
			else if ( star >= 0 ) { pi = star + 1; ti = ++mark; }
			else return false;
		}
		while ( pi < pattern.Length && pattern[pi] == '*' ) pi++;
		return pi == pattern.Length;
	}

	/// <summary>Capture a pose from a clip: every Bone track evaluated at
	/// <paramref name="frame"/>. Tracks of other kinds are ignored.</summary>
	public static Dictionary<string, RigPose> Capture( RigAnimFile clip, float frame )
	{
		ArgumentNullException.ThrowIfNull( clip );
		var pose = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase );
		foreach ( var track in clip.Tracks )
			if ( track.Kind == RigTrackKind.Bone && track.Keys.Count > 0 )
				pose[track.Name] = track.Evaluate( frame );
		return pose;
	}

	/// <summary>Blend two poses bone by bone: position and scale lerped, rotation
	/// slerped (<see cref="RigPose.Lerp"/>). A bone present in only one pose blends
	/// against Identity on the missing side. The result contains every bone of
	/// <paramref name="a"/> (blended where masked, untouched otherwise) plus every
	/// bone of <paramref name="b"/> that is masked in (blended against Identity);
	/// a <paramref name="b"/>-only bone outside the mask is not carried over.</summary>
	public static Dictionary<string, RigPose> Blend(
		IReadOnlyDictionary<string, RigPose> a, IReadOnlyDictionary<string, RigPose> b,
		float weight, List<string>? mask = null )
	{
		ArgumentNullException.ThrowIfNull( a );
		ArgumentNullException.ThrowIfNull( b );
		var w = Math.Clamp( weight, 0f, 1f );
		var result = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (bone, pa) in a )
			result[bone] = MatchesMask( bone, mask ) && b.TryGetValue( bone, out var pb )
				? RigPose.Lerp( pa, pb, w )
				: pa;
		foreach ( var (bone, pb) in b )
			if ( !result.ContainsKey( bone ) && MatchesMask( bone, mask ) )
				result[bone] = RigPose.Lerp( RigPose.Identity, pb, w );
		return result;
	}

	/// <summary>Write a pose into a clip as one key per bone at <paramref name="frame"/>.
	/// With <paramref name="weight"/> below 1 the key blends from the bone's current
	/// evaluated pose at that frame (Identity for a bone with no track yet) toward
	/// the library pose. Bones outside <paramref name="mask"/> are not keyed.
	/// Returns the number of bones keyed. Setting a key never bakes the clip: other
	/// keys and their easing are untouched.</summary>
	public static int Apply( RigAnimFile clip, IReadOnlyDictionary<string, RigPose> pose,
		int frame, float weight = 1f, List<string>? mask = null, RigEase ease = RigEase.Smooth )
	{
		ArgumentNullException.ThrowIfNull( clip );
		ArgumentNullException.ThrowIfNull( pose );
		var w = Math.Clamp( weight, 0f, 1f );
		var applied = 0;
		foreach ( var (bone, target) in pose )
		{
			if ( !MatchesMask( bone, mask ) ) continue;
			var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == bone );
			var current = track is { Keys.Count: > 0 } ? track.Evaluate( frame ) : RigPose.Identity;
			var value = w >= 1f ? target : RigPose.Lerp( current, target, w );
			if ( track is null )
			{
				track = new RigTrack { Kind = RigTrackKind.Bone, Name = bone };
				clip.Tracks.Add( track );
			}
			var idx = track.Keys.FindIndex( k => k.Frame == frame );
			if ( idx >= 0 ) track.Keys[idx] = new RigKey { Frame = frame, Local = value, Ease = ease };
			else track.Keys.Add( new RigKey { Frame = frame, Local = value, Ease = ease } );
			track.Keys.Sort( ( x, y ) => x.Frame.CompareTo( y.Frame ) );
			applied++;
		}
		if ( applied > 0 && frame >= clip.FrameCount ) clip.FrameCount = frame + 1;
		return applied;
	}

	/// <summary>
	/// Mirror a pose across the sagittal plane (armature x = 0), Blender pose-mirror
	/// style: each bone's transform moves to its mirror partner (.L/.R via
	/// <see cref="Skeleton.FlipSideSuffix"/>), reflected in place — local position x
	/// negated, rotation (x, -y, -z, w), scale unchanged. Centre bones (no side
	/// suffix) mirror onto themselves. A bone whose partner has no key mirrors onto
	/// the partner side only, which IS the mirror image (the unposed side stays at
	/// bind). Assumes a symmetric rig; on an asymmetric rig the result is the
	/// mirror of the pose as named, not of the geometry.
	/// </summary>
	public static Dictionary<string, RigPose> Mirror( IReadOnlyDictionary<string, RigPose> pose )
	{
		ArgumentNullException.ThrowIfNull( pose );
		var mirrored = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (bone, p) in pose )
		{
			if ( string.IsNullOrWhiteSpace( bone ) ) throw new ArgumentException( "pose has a bone with an empty name.", nameof( pose ) );
			var target = Skeleton.FlipSideSuffix( bone ) ?? bone;
			mirrored[target] = new RigPose(
				new Vec3( -p.Position.x, p.Position.y, p.Position.z ),
				new Quat( p.Rotation.x, -p.Rotation.y, -p.Rotation.z, p.Rotation.w ),
				p.Scale );
		}
		return mirrored;
	}

	/// <summary>
	/// Seed poses for a humanoid auto-rig: A-pose (the bind stance — the auto-rig
	/// stands the arms slightly out, which IS an A-pose), T-pose (arms rotated to
	/// horizontal about the shoulders) and Attention (arms straight down). T-pose
	/// and Attention rotate only the upper arms, from the bind, so the chains
	/// follow rigidly; the rotation is computed in world space (shoulder-to-wrist
	/// direction onto the lateral/down axis) and converted back to a local
	/// RigPose, so it works on either naming scheme (citizen arm_upper_L or
	/// template upperarm_L) and on either lateral axis — the lateral axis is
	/// measured from the two shoulder heads, not assumed.
	/// </summary>
	public static PoseLibrary SeedHumanoid( Skeleton skeleton )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		var bind = BindPose( skeleton );
		var lib = new PoseLibrary();
		lib.SetPose( "A-pose", bind );
		lib.SetPose( "T-pose", ArmPose( skeleton, bind, ArmTarget.T ) );
		lib.SetPose( "Attention", ArmPose( skeleton, bind, ArmTarget.Attention ) );
		return lib;
	}

	static Dictionary<string, RigPose> BindPose( Skeleton skeleton )
	{
		var pose = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase );
		foreach ( var bone in skeleton.Bones )
			pose[bone.Name] = RigPose.FromXform( bone.Local );
		return pose;
	}

	enum ArmTarget { T, Attention }

	static Dictionary<string, RigPose> ArmPose( Skeleton skeleton, Dictionary<string, RigPose> bind, ArmTarget target )
	{
		var result = new Dictionary<string, RigPose>( bind, StringComparer.OrdinalIgnoreCase );
		var pelvis = skeleton.IndexOf( "pelvis" );
		var head = skeleton.IndexOf( "head" );
		if ( pelvis < 0 ) throw new ArgumentException( "SeedHumanoid needs a humanoid auto-rig (no 'pelvis' bone).", nameof( skeleton ) );
		var up = new Vec3( 0, 0, 1 );
		if ( head >= 0 )
		{
			var u = skeleton.HeadWorld( head ) - skeleton.HeadWorld( pelvis );
			if ( u.Length > 1e-6f ) up = u.Normal;
		}
		Vec3 lateral = new( 1, 0, 0 );
		var armL = FindArm( skeleton, "L" );
		var armR = FindArm( skeleton, "R" );
		if ( armL >= 0 && armR >= 0 )
		{
			var l = skeleton.HeadWorld( armL ) - skeleton.HeadWorld( armR );
			if ( l.Length > 1e-6f ) lateral = l.Normal;
		}
		foreach ( var (arm, side) in new[] { (armL, 1f), (armR, -1f) } )
		{
			if ( arm < 0 ) continue;
			var d = skeleton.BoneDirection( arm );
			if ( d.Length < 1e-6f ) continue;
			d = d.Normal;
			var want = target == ArmTarget.T ? lateral * side : -up;
			var q = RotationFromTo( d, want );
			if ( q is null ) continue; // already pointing that way
			var parent = skeleton.Bones[arm].Parent;
			var parentWorld = parent < 0 ? Xform.Identity : skeleton.WorldBind( parent );
			var oldWorld = skeleton.WorldBind( arm );
			var newWorld = Xform.Translate( oldWorld.Origin ) * q.Value.ToXform( Vec3.Zero ) * Xform.Translate( -oldWorld.Origin ) * oldWorld;
			var local = parentWorld.Inverse * newWorld;
			result[skeleton.Bones[arm].Name] = RigPose.FromXform( local );
		}
		return result;
	}

	static int FindArm( Skeleton skeleton, string side )
	{
		foreach ( var name in new[] { $"arm_upper_{side}", $"upperarm_{side}" } )
		{
			var i = skeleton.IndexOf( name );
			if ( i >= 0 ) return i;
		}
		return -1;
	}

	/// <summary>Quaternion rotating unit vector <paramref name="from"/> onto <paramref name="to"/>; null when they already agree.</summary>
	static Quat? RotationFromTo( Vec3 from, Vec3 to )
	{
		var dot = Math.Clamp( Vec3.Dot( from, to ), -1f, 1f );
		if ( dot > 0.999999f ) return null;
		var axis = Vec3.Cross( from, to );
		if ( axis.Length < 1e-6f )
		{
			// Opposite directions: any perpendicular axis does, at 180 degrees.
			axis = Vec3.Cross( from, MathF.Abs( from.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 ) );
			return Quat.FromAxisAngle( axis.Normal, MathF.PI );
		}
		return Quat.FromAxisAngle( axis.Normal, MathF.Acos( dot ) );
	}
}
