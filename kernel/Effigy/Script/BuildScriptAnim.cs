using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// Lane K: a declarative animation script - one verb per line, in the same spirit as
/// <see cref="BuildScript"/>, but for clips instead of models. Run it with
/// <see cref="Run(string, Skeleton)"/>; the result carries every clip the script made.
/// 
///   # a comment
///   clip name=walk fps=30 frames=60
///   key bone=root frame=0 pos=0,0,0
///   key bone=root frame=60 pos=40,0,0
///   gait type=walk feet=foot_L,foot_R stride=40 height=4 frames=60 root=root
///   wave bones=tail1,tail2,tail3 amplitude=12 period=40
///   breathe bones=rib_L,rib_R amplitude=0.05 period=60
///   lint autofix=1
///   save_clip path=out/walk.riganim
/// 
/// Verbs that BAKE (wave, thrash, spring, breathe, gait's root, blend_layer, lint
/// autofix, save_clip) replace the affected tracks with per-frame keys: a baked
/// frame list has no segments to ease, so key easing is not preserved through a
/// bake, and that is stated on each verb rather than hidden. Verbs that do not
/// bake (key, channel, pose_preset) keep the clip's key structure.
/// 
/// AXES. Gait moves forward on +X and lifts feet on +Z (the kernel's model space,
/// where the floor sits on Z - see SoftBone's ground note). A rig built facing
/// another axis needs its feet/root keys rotated, not a different gait.
/// 
/// BOUNDARY, stated plainly: `euphoria` is a desktop verb. The kernel cannot call
/// the desktop's EuphoriaService (the dependency runs the other way), so a script
/// naming it fails loudly with that message instead of pretending to apply it.
/// The same holds for any verb that would need the cabinet viewport.
/// </summary>
public static class BuildScriptAnim
{
	public sealed class Result
	{
		public bool Ok = true;
		public List<string> Log = new();
		public List<string> Written = new();
		public string Error;
		public int ErrorLine;
		public Dictionary<string, RigAnimFile> Clips = new( StringComparer.OrdinalIgnoreCase );
		public string Current = "";

		public RigAnimFile CurrentClip => Current != "" && Clips.TryGetValue( Current, out var c ) ? c : null;

		public override string ToString()
		{
			var sb = new StringBuilder();

			foreach ( var line in Log )
				sb.AppendLine( line );

			if ( Error is not null )
				sb.AppendLine( $"ERROR line {ErrorLine}: {Error}" );

			return sb.ToString();
		}
	}

	/// <summary>Run a script against a skeleton. The skeleton is required: gait, wave,
	/// spring, thrash, breathe, look_at, lint and blend all sample poses through it.</summary>
	public static Result Run( string text, Skeleton skeleton )
	{
		ArgumentNullException.ThrowIfNull( skeleton );

		var result = new Result();
		var presets = new Dictionary<string, Dictionary<string, RigPose>>( StringComparer.OrdinalIgnoreCase );
		var lines = (text ?? "").Split( '\n' );

		for ( var li = 0; li < lines.Length; li++ )
		{
			var raw = lines[li];
			var line = StripComment( raw ).Trim();

			if ( line.Length == 0 )
				continue;

			var tokens = Tokenize( line );

			if ( tokens.Count == 0 )
				continue;

			try
			{
				DoVerb( tokens, skeleton, result, presets );
			}
			catch ( AnimException ex )
			{
				result.Ok = false;
				result.Error = ex.Message;
				result.ErrorLine = li + 1;
				return result;
			}
			catch ( Exception ex ) when ( ex is ArgumentException or InvalidOperationException or IOException )
			{
				result.Ok = false;
				result.Error = ex.Message;
				result.ErrorLine = li + 1;
				return result;
			}
		}

		return result;
	}

	sealed class AnimException : Exception
	{
		public AnimException( string message ) : base( message ) { }
	}

	// --- parsing ---------------------------------------------------------------------------

	static string StripComment( string line )
	{
		var inQuote = false;

		for ( var i = 0; i < line.Length; i++ )
		{
			if ( line[i] == '"' )
				inQuote = !inQuote;
			else if ( line[i] == '#' && !inQuote )
				return line[..i];
		}

		return line;
	}

	static List<string> Tokenize( string line )
	{
		var tokens = new List<string>();
		var sb = new StringBuilder();
		var inQuote = false;

		foreach ( var ch in line )
		{
			if ( ch == '"' )
			{
				inQuote = !inQuote;
				continue;
			}

			if ( !inQuote && char.IsWhiteSpace( ch ) )
			{
				if ( sb.Length > 0 )
				{
					tokens.Add( sb.ToString() );
					sb.Clear();
				}
			}
			else
			{
				sb.Append( ch );
			}
		}

		if ( sb.Length > 0 )
			tokens.Add( sb.ToString() );

		return tokens;
	}

	/// <summary>key=value options after the verb; a bare word is an error, not a guess.</summary>
	static Dictionary<string, string> Options( List<string> tokens )
	{
		var opts = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		for ( var i = 1; i < tokens.Count; i++ )
		{
			var eq = tokens[i].IndexOf( '=' );

			if ( eq <= 0 )
				throw new AnimException( $"'{tokens[i]}' is not key=value" );

			opts[tokens[i][..eq]] = tokens[i][(eq + 1)..];
		}

		return opts;
	}

	static string Req( Dictionary<string, string> opts, string key ) =>
		opts.TryGetValue( key, out var v ) && !string.IsNullOrWhiteSpace( v )
			? v
			: throw new AnimException( $"missing {key}=" );

	static string Opt( Dictionary<string, string> opts, string key, string dflt = null ) =>
		opts.TryGetValue( key, out var v ) ? v : dflt;

	static float Num( Dictionary<string, string> opts, string key, float dflt )
	{
		if ( !opts.TryGetValue( key, out var v ) )
			return dflt;

		if ( !float.TryParse( v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n ) )
			throw new AnimException( $"'{v}' is not a number for {key}=" );

		return n;
	}

	static int Int( Dictionary<string, string> opts, string key, int dflt )
	{
		if ( !opts.TryGetValue( key, out var v ) )
			return dflt;

		if ( !int.TryParse( v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n ) )
			throw new AnimException( $"'{v}' is not an integer for {key}=" );

		return n;
	}

	static Vec3 Vec( string text, string what )
	{
		var parts = (text ?? "").Split( ',' );

		if ( parts.Length != 3
			|| !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
			|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
			|| !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
			throw new AnimException( $"'{text}' is not x,y,z for {what}" );

		return new Vec3( x, y, z );
	}

	static List<string> Names( string text ) =>
		(text ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).ToList();

	static RigAnimFile Clip( Result result )
	{
		var clip = result.CurrentClip;

		if ( clip is null )
			throw new AnimException( "no clip selected - start with 'clip name=X' or 'load name=X path=...'" );

		return clip;
	}

	static List<int> BoneIndices( Skeleton skeleton, List<string> names, string what )
	{
		var indices = new List<int>();

		foreach ( var name in names )
		{
			var idx = skeleton.IndexOf( name );

			if ( idx < 0 )
				throw new AnimException( $"skeleton has no bone '{name}' for {what}" );

			indices.Add( idx );
		}

		indices.Sort();
		return indices;
	}

	static void SetKey( RigAnimFile clip, string bone, int frame, RigPose pose, RigEase ease )
	{
		var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == bone );

		if ( track is null )
		{
			track = new RigTrack { Kind = RigTrackKind.Bone, Name = bone };
			clip.Tracks.Add( track );
		}

		var idx = track.Keys.FindIndex( k => k.Frame == frame );

		if ( idx >= 0 )
			track.Keys[idx] = new RigKey { Frame = frame, Local = pose, Ease = ease };
		else
			track.Keys.Add( new RigKey { Frame = frame, Local = pose, Ease = ease } );

		track.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );

		if ( frame >= clip.FrameCount )
			clip.FrameCount = frame + 1;
	}

	static RigEase ParseEase( string text ) =>
		Enum.TryParse<RigEase>( (text ?? "").Replace( " ", "" ), true, out var ease ) ? ease : throw new AnimException( $"'{text}' is not an ease (Smooth, Linear, Stepped, EaseIn, EaseOut, Cubic, Monotone)" );

	static Quat Conj( Quat q )
	{
		var len = MathF.Sqrt( q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w );
		return len <= 0f || !float.IsFinite( len ) ? Quat.Identity : new Quat( -q.x / len, -q.y / len, -q.z / len, q.w / len );
	}

	// --- verbs -----------------------------------------------------------------------------

	static void DoVerb( List<string> tokens, Skeleton skeleton, Result result, Dictionary<string, Dictionary<string, RigPose>> presets )
	{
		var verb = tokens[0].ToLowerInvariant();
		var opts = Options( tokens );

		switch ( verb )
		{
			case "clip":
			{
				var name = Req( opts, "name" );

				if ( !result.Clips.TryGetValue( name, out var clip ) )
				{
					clip = new RigAnimFile { FrameRate = Int( opts, "fps", 30 ), FrameCount = Int( opts, "frames", 0 ) };
					result.Clips[name] = clip;
				}
				else
				{
					if ( opts.ContainsKey( "fps" ) ) clip.FrameRate = Int( opts, "fps", clip.FrameRate );
					if ( opts.ContainsKey( "frames" ) ) clip.FrameCount = Int( opts, "frames", clip.FrameCount );
				}

				result.Current = name;
				result.Log.Add( $"clip {name} ({clip.FrameRate} fps, {clip.FrameCount} frames)" );
				break;
			}

			case "load":
			{
				var name = Req( opts, "name" );
				var path = Req( opts, "path" );

				if ( !File.Exists( path ) )
					throw new AnimException( $"no such clip file '{path}'" );

				result.Clips[name] = RigAnimFile.ReadFile( path );
				result.Current = name;
				result.Log.Add( $"loaded {name} from {path}" );
				break;
			}

			case "pose_preset":
			{
				var name = Req( opts, "name" );
				var pose = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase );
				string bone = null;
				var pos = Vec3.Zero;
				var rot = Quat.Identity;
				var scale = Vec3.One;
				var have = false;

				void Flush()
				{
					if ( have && bone is not null )
						pose[bone] = new RigPose( pos, rot, scale );

					have = false;
					pos = Vec3.Zero;
					rot = Quat.Identity;
					scale = Vec3.One;
				}

				// Options() lost order, so re-read the tokens in order here.
				foreach ( var token in tokens.Skip( 1 ) )
				{
					var eq = token.IndexOf( '=' );
					var key = token[..eq].ToLowerInvariant();
					var value = token[(eq + 1)..];

					switch ( key )
					{
						case "name": break;
						case "bone": Flush(); bone = value; have = true; break;
						case "pos": pos = Vec( value, "pos=" ); break;
						case "rot":
							var a = Vec( value, "rot=" );
							rot = Quat.FromAngles( a.x, a.y, a.z );
							break;
						case "scale": scale = Vec( value, "scale=" ); break;
						default: throw new AnimException( $"pose_preset takes bone=/pos=/rot=/scale=, not '{key}='" );
					}
				}

				Flush();

				if ( pose.Count == 0 )
					throw new AnimException( "pose_preset needs at least one bone=" );

				presets[name] = pose;
				result.Log.Add( $"pose_preset {name} ({pose.Count} bone(s))" );
				break;
			}

			case "key":
			{
				var clip = Clip( result );
				var frame = Int( opts, "frame", 0 );
				var ease = opts.TryGetValue( "ease", out var easeText ) ? ParseEase( easeText ) : RigEase.Smooth;

				if ( opts.TryGetValue( "preset", out var presetName ) )
				{
					if ( !presets.TryGetValue( presetName, out var preset ) )
						throw new AnimException( $"no pose_preset '{presetName}'" );

					var onlyBone = Opt( opts, "bone" );
					var applied = 0;

					foreach ( var (bone, pose) in preset )
					{
						if ( onlyBone is not null && !string.Equals( bone, onlyBone, StringComparison.OrdinalIgnoreCase ) )
							continue;

						SetKey( clip, bone, frame, pose, ease );
						applied++;
					}

					if ( applied == 0 )
						throw new AnimException( $"preset '{presetName}' has no bone '{onlyBone}'" );

					result.Log.Add( $"key preset {presetName} at frame {frame} ({applied} bone(s))" );
					break;
				}

				var boneName = Req( opts, "bone" );
				var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == boneName );
				var basePose = track is { Keys.Count: > 0 } ? track.Evaluate( frame ) : RigPose.Identity;
				var pos2 = opts.TryGetValue( "pos", out var posText ) ? Vec( posText, "pos=" ) : basePose.Position;
				var scale2 = opts.TryGetValue( "scale", out var scaleText ) ? Vec( scaleText, "scale=" ) : basePose.Scale;
				var rot2 = basePose.Rotation;

				if ( opts.TryGetValue( "rot", out var rotText ) )
				{
					var angles = Vec( rotText, "rot=" );
					rot2 = Quat.FromAngles( angles.x, angles.y, angles.z );
				}

				SetKey( clip, boneName, frame, new RigPose( pos2, rot2, scale2 ), ease );
				result.Log.Add( $"key {boneName} at frame {frame}" );
				break;
			}

			case "channel":
			{
				var clip = Clip( result );
				var bone = Req( opts, "bone" );
				var channelText = Req( opts, "channel" );

				if ( !Enum.TryParse<RigChannel>( channelText, true, out var channel ) )
					throw new AnimException( $"'{channelText}' is not a channel (PosX/Y/Z, EulerX/Y/Z, ScaleX/Y/Z)" );

				var keys = ClipKeys.ParseChannelKeys( Req( opts, "keys" ), out var error );

				if ( keys is null )
					throw new AnimException( error );

				foreach ( var k in keys )
					ClipKeys.SetChannelKey( clip, bone, channel, k.Frame, k.Value );

				if ( opts.TryGetValue( "modifier", out var modText ) )
				{
					if ( !Enum.TryParse<FCurveModifierKind>( modText, true, out var kind ) )
						throw new AnimException( $"'{modText}' is not a modifier (Cycle, SinWave, PerlinNoise, SpringOvershoot, SteppedQuantize)" );

					var track = clip.Tracks.First( t => t.Kind == RigTrackKind.Bone && t.Name == bone );
					track.Modifiers.Add( new FCurveModifier( kind,
						Num( opts, "amplitude", 0f ), Num( opts, "period", 30f ),
						Num( opts, "phase", 0f ), Int( opts, "steps", 2 ) ) );
				}

				result.Log.Add( $"channel {bone}.{channel} ({keys.Count} key(s))" );
				break;
			}

			case "bake_channels":
			{
				var clip = Clip( result );
				var count = ClipKeys.BakeChannels( clip, Req( opts, "bone" ) );
				result.Log.Add( $"baked channels for {Req( opts, "bone" )}: {count} key(s)" );
				break;
			}

			case "wave":
			{
				var clip = Clip( result );
				var bones = BoneIndices( skeleton, Names( Req( opts, "bones" ) ), "wave" );
				var settings = new ChainWave.Settings
				{
					Amplitude = Num( opts, "amplitude", 12f ),
					Period = Num( opts, "period", 40f ),
					Taper = Num( opts, "taper", 0.6f ),
					Wavelength = Num( opts, "wavelength", 8f ),
					Offset = Num( opts, "offset", 0f ),
				};

				if ( opts.TryGetValue( "plane", out var planeText ) )
				{
					if ( !Enum.TryParse<ChainWave.Plane>( planeText, true, out var plane ) )
						throw new AnimException( $"'{planeText}' is not a wave plane (Side, Up)" );

					settings.Swing = plane;
				}

				var frames = BakeFrames( clip, skeleton );
				var waved = ChainWave.Apply( skeleton, frames, bones, settings );
				WriteBackBones( clip, skeleton, waved, bones );
				result.Log.Add( $"wave on {bones.Count} bone(s), {frames.Count} frame(s) baked" );
				break;
			}

			case "thrash":
			{
				var clip = Clip( result );
				var bones = BoneIndices( skeleton, Names( Req( opts, "bones" ) ), "thrash" );
				var style = Opt( opts, "style", "flop" );
				var jaw = Opt( opts, "jaw" ) is { } jawName ? skeleton.IndexOf( jawName ) : -1;
				var opercles = Opt( opts, "opercle" ) is { } opText ? BoneIndices( skeleton, Names( opText ), "thrash opercle" ) : new List<int>();
				var fins = Opt( opts, "fins" ) is { } finText ? BoneIndices( skeleton, Names( finText ), "thrash fins" ) : new List<int>();

				if ( clip.Tracks.Any( t => t.ChannelCurves.Count > 0 ) )
					result.Log.Add( "note: thrash bakes from ToClip, which samples keyed poses only - run bake_channels first to keep channel curves" );

				var baked = ChainWave.ApplyThrash( clip.ToClip( skeleton ), skeleton, bones, jaw, opercles, fins, style );
				WriteBackAll( clip, skeleton, baked );
				result.Log.Add( $"thrash {style} on {bones.Count} bone(s), {baked.FrameCount} frame(s) baked" );
				break;
			}

			case "spring":
			{
				var clip = Clip( result );
				var bones = BoneIndices( skeleton, Names( Req( opts, "bones" ) ), "spring" );
				var stiffness = Num( opts, "stiffness", 60f );
				var damping = Num( opts, "damping", 0.04f );
				var gravity = Num( opts, "gravity", 1f );

				if ( clip.Tracks.Any( t => t.ChannelCurves.Count > 0 ) )
					result.Log.Add( "note: spring bakes from ToClip, which samples keyed poses only - run bake_channels first to keep channel curves" );

				var baked = ChainSpring.BakeSecondary( clip.ToClip( skeleton ), skeleton, bones, stiffness, damping, gravity );
				WriteBackBones( clip, skeleton, baked.Frames, bones );
				result.Log.Add( $"spring on {bones.Count} bone(s), {baked.FrameCount} frame(s) baked" );
				break;
			}

			case "breathe":
			{
				var clip = Clip( result );
				var bones = BoneIndices( skeleton, Names( Req( opts, "bones" ) ), "breathe" );
				var amp = Num( opts, "amplitude", 0.05f );
				var period = Math.Max( Num( opts, "period", 60f ), 1e-6f );
				var frames = Int( opts, "frames", Math.Max( clip.LastKeyFrame, 0 ) );

				for ( var f = 0; f <= frames; f++ )
				{
					var factor = 1f + amp * MathF.Sin( 2f * MathF.PI * f / period );
					var pose = clip.SampleLocal( skeleton, f );

					foreach ( var b in bones )
					{
						var local = RigPose.FromXform( pose[b] );
						SetKey( clip, skeleton.Bones[b].Name, f,
							new RigPose( local.Position, local.Rotation, local.Scale * factor ), RigEase.Smooth );
					}
				}

				result.Log.Add( $"breathe on {bones.Count} bone(s), {frames + 1} frame(s) baked" );
				break;
			}

			case "gait":
			{
				var clip = Clip( result );
				var type = Req( opts, "type" ).ToLowerInvariant();
				var feet = Names( Req( opts, "feet" ) );
				var stride = Num( opts, "stride", 20f );
				var height = Num( opts, "height", 4f );
				var frames = Int( opts, "frames", 60 );
				var rootName = Opt( opts, "root" );

				if ( frames <= 0 )
					throw new AnimException( "gait needs frames= above 0" );

				if ( feet.Count == 0 )
					throw new AnimException( "gait needs at least one foot in feet=" );

				// Phase offsets and planted fractions per gait. Quad trot pairs
				// diagonal feet; arachnid alternates sides; the rest are biped.
				var (phases, planted) = type switch
				{
					"walk" => (new[] { 0f, 0.5f }, 0.6f),
					"run" => (new[] { 0f, 0.5f }, 0.4f),
					"sneak" => (new[] { 0f, 0.5f }, 0.7f),
					"quad_trot" => (new[] { 0f, 0.5f, 0.5f, 0f }, 0.5f),
					"arachnid" => (new[] { 0f, 0.5f }, 0.6f),
					_ => throw new AnimException( $"'{type}' is not a gait (walk, run, sneak, quad_trot, arachnid)" ),
				};

				foreach ( var foot in feet )
					if ( skeleton.IndexOf( foot ) < 0 )
						throw new AnimException( $"skeleton has no bone '{foot}' for gait" );

				// Root travels one stride per cycle, +X, from its bind position.
				if ( rootName is not null )
				{
					var rootIdx = skeleton.IndexOf( rootName );

					if ( rootIdx < 0 )
						throw new AnimException( $"skeleton has no bone '{rootName}' for gait root" );

					var bind = RigPose.FromXform( skeleton.Bones[rootIdx].Local );

					for ( var f = 0; f <= frames; f++ )
					{
						var x = bind.Position.x + stride * f / frames;
						SetKey( clip, rootName, f, new RigPose( new Vec3( x, bind.Position.y, bind.Position.z ), bind.Rotation, bind.Scale ), RigEase.Linear );
					}
				}

				for ( var fi = 0; fi < feet.Count; fi++ )
				{
					var foot = feet[fi];
					var phase = phases[fi % phases.Length];
					var footIdx = skeleton.IndexOf( foot );
					var bind = RigPose.FromXform( skeleton.Bones[footIdx].Local );
					var goalTrack = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.IkGoal && t.Name == foot );

					if ( goalTrack is null )
					{
						goalTrack = new RigTrack { Kind = RigTrackKind.IkGoal, Name = foot };
						clip.Tracks.Add( goalTrack );
					}

					for ( var f = 0; f <= frames; f++ )
					{
						var cycle = (f / (float)frames + phase) % 1f;
						Vec3 pos;

						if ( cycle < planted )
						{
							// Planted: the foot travels backwards relative to the body
							// at exactly the root's speed, so it stands still in the world.
							var u = cycle / planted;
							pos = new Vec3( bind.Position.x + stride * (0.5f - u), bind.Position.y, bind.Position.z );
						}
						else
						{
							// Swing: forward again, lifted on +Z with a sine arc.
							var u = (cycle - planted) / (1f - planted);
							pos = new Vec3(
								bind.Position.x + stride * (u - 0.5f),
								bind.Position.y,
								bind.Position.z + height * MathF.Sin( MathF.PI * u ) );
						}

						var idx = goalTrack.Keys.FindIndex( k => k.Frame == f );

						if ( idx >= 0 )
							goalTrack.Keys[idx] = new RigKey { Frame = f, Local = new RigPose( pos, Quat.Identity, Vec3.One ), Ease = RigEase.Linear };
						else
							goalTrack.Keys.Add( new RigKey { Frame = f, Local = new RigPose( pos, Quat.Identity, Vec3.One ), Ease = RigEase.Linear } );
					}

					goalTrack.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
				}

				if ( frames >= clip.FrameCount )
					clip.FrameCount = frames + 1;

				result.Log.Add( $"gait {type} on {feet.Count} foot/feet, {frames + 1} frame(s)" );
				break;
			}

			case "look_at":
			{
				var clip = Clip( result );
				var bone = Req( opts, "bone" );
				var target = Vec( Req( opts, "target" ), "target=" );
				var frame = Int( opts, "frame", 0 );
				var idx = skeleton.IndexOf( bone );

				if ( idx < 0 )
					throw new AnimException( $"skeleton has no bone '{bone}' for look_at" );

				var sampled = clip.SampleLocal( skeleton, frame );
				var world = RigAnimFile.ModelSpace( skeleton, sampled );
				var dir = target - world[idx].Origin;

				if ( dir.LengthSquared < 1e-12f )
					throw new AnimException( "look_at target is on the bone itself" );

				dir = dir.Normal;
				var axis = world[idx].Z.Normal;
				var dot = Math.Clamp( Vec3.Dot( axis, dir ), -1f, 1f );
				Quat aim;

				if ( dot > 0.9999f )
					aim = Quat.Identity;
				else if ( dot < -0.9999f )
				{
					var perp = Math.Abs( axis.x ) < 0.9f ? Vec3.Cross( axis, new Vec3( 1, 0, 0 ) ) : Vec3.Cross( axis, new Vec3( 0, 1, 0 ) );
					aim = Quat.FromAxisAngle( perp.Normal, MathF.PI );
				}
				else
				{
					aim = Quat.FromAxisAngle( Vec3.Cross( axis, dir ).Normal, MathF.Acos( dot ) );
				}

				var parent = skeleton.Bones[idx].Parent;
				var parentRot = parent >= 0 ? Quat.FromXform( world[parent] ) : Quat.Identity;
				var localRot = Conj( parentRot ) * (aim * Quat.FromXform( world[idx] ));
				var sampledPose = RigPose.FromXform( sampled[idx] );
				SetKey( clip, bone, frame, new RigPose( sampledPose.Position, localRot, sampledPose.Scale ), RigEase.Smooth );
				result.Log.Add( $"look_at {bone} at frame {frame}" );
				break;
			}

			case "blend_layer":
			{
				var baseName = Req( opts, "base" );
				var layerName = Req( opts, "layer" );
				var outName = Opt( opts, "out", "blend" );
				var weight = Math.Clamp( Num( opts, "weight", 0.5f ), 0f, 1f );
				var mode = Opt( opts, "mode", "override" ).ToLowerInvariant();

				if ( mode is not ("override" or "additive") )
					throw new AnimException( $"'{mode}' is not a blend mode (override, additive)" );

				if ( !result.Clips.TryGetValue( baseName, out var baseClip ) )
					throw new AnimException( $"no clip '{baseName}' for blend_layer" );

				if ( !result.Clips.TryGetValue( layerName, out var layerClip ) )
					throw new AnimException( $"no clip '{layerName}' for blend_layer" );

				var mask = Opt( opts, "bones" ) is { } maskText ? new HashSet<string>( Names( maskText ), StringComparer.OrdinalIgnoreCase ) : null;
				var last = Math.Max( baseClip.LastKeyFrame, layerClip.LastKeyFrame );
				var blended = new RigAnimFile { FrameRate = baseClip.FrameRate, FrameCount = last + 1, SourceModel = baseClip.SourceModel };

				for ( var f = 0; f <= last; f++ )
				{
					var basePose = baseClip.SampleLocal( skeleton, f );
					var layerPose = layerClip.SampleLocalChannels( skeleton, f );

					for ( var b = 0; b < skeleton.Count; b++ )
					{
						var name = skeleton.Bones[b].Name;
						var bp = RigPose.FromXform( basePose[b] );

						if ( mask is not null && !mask.Contains( name ) )
						{
							SetKey( blended, name, f, bp, RigEase.Smooth );
							continue;
						}

						var lp = RigPose.FromXform( layerPose[b] );
						RigPose outPose;

						if ( mode == "override" )
						{
							outPose = new RigPose(
								Vec3.Lerp( bp.Position, lp.Position, weight ),
								Quat.Slerp( bp.Rotation, lp.Rotation, weight ),
								Vec3.Lerp( bp.Scale, lp.Scale, weight ) );
						}
						else
						{
							// Additive: the layer's delta from its own first key, scaled.
							var layerTrack = layerClip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == name );
							var refPose = layerTrack is { Keys.Count: > 0 }
								? layerTrack.EvaluateWithChannels( layerTrack.Keys[0].Frame )
								: RigPose.FromXform( skeleton.Bones[b].Local );
							var deltaRot = Conj( refPose.Rotation ) * lp.Rotation;
							outPose = new RigPose(
								bp.Position + (lp.Position - refPose.Position) * weight,
								bp.Rotation * Quat.Slerp( Quat.Identity, deltaRot, weight ),
								new Vec3(
									bp.Scale.x * (1f + (lp.Scale.x / Math.Max( refPose.Scale.x, 1e-6f ) - 1f) * weight),
									bp.Scale.y * (1f + (lp.Scale.y / Math.Max( refPose.Scale.y, 1e-6f ) - 1f) * weight),
									bp.Scale.z * (1f + (lp.Scale.z / Math.Max( refPose.Scale.z, 1e-6f ) - 1f) * weight) ) );
						}

						SetKey( blended, name, f, outPose, RigEase.Smooth );
					}
				}

				result.Clips[outName] = blended;
				result.Current = outName;
				result.Log.Add( $"blend_layer {baseName}+{layerName} -> {outName} ({mode}, weight {weight}, {last + 1} frame(s) baked)" );
				break;
			}

			case "lint":
			{
				var clip = Clip( result );
				var options = new MotionLint.Options { Loop = Opt( opts, "loop", "0" ) == "1" };
				var lint = new MotionLint( skeleton, clip );
				var lintResult = lint.Run( options );

				foreach ( var line in lintResult.ToString().Split( '\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries ) )
					result.Log.Add( $"lint: {line}" );

				if ( Opt( opts, "autofix", "0" ) == "1" )
				{
					// AutoFix works on baked frames (Lane G's design): bake, fix, and
					// write the repaired frames back as keys. The original key
					// structure is gone afterwards - that is the price of a repair,
					// and it is logged, not hidden.
					var baked = clip.ToClip( skeleton );
					var fixes = MotionLint.AutoFix( baked, skeleton, options );
					WriteBackAll( clip, skeleton, baked );
					result.Log.Add( $"lint autofix: {fixes.Fixes.Count} fix(es) on {fixes.ChangedFrames} frame(s)" );

					foreach ( var fix in fixes.Fixes.Take( 12 ) )
						result.Log.Add( $"lint fix: {fix.Kind} {fix.Bone} @ {fix.Frame}: {fix.Detail}" );
				}

				break;
			}

			case "euphoria":
				throw new AnimException( "euphoria needs the desktop EuphoriaService and cannot run headlessly - use the cabinet" );

			case "save_clip":
			{
				var clip = Clip( result );
				var path = Req( opts, "path" );
				var name = Opt( opts, "name" );

				// Bake channels + constraints into keys so the file plays exactly
				// what the script plays: a reader without Lane K must not lose them.
				var baked = new RigAnimFile
				{
					SourceModel = clip.SourceModel,
					RigAsset = clip.RigAsset,
					FrameRate = clip.FrameRate,
					FrameCount = clip.FrameCount,
				};

				var last = clip.LastKeyFrame;

				foreach ( var track in clip.Tracks )
					foreach ( var keys in track.ChannelCurves.Values )
						foreach ( var k in keys )
							last = Math.Max( last, k.Frame );

				last = Math.Max( last, 0 );

				for ( var f = 0; f <= last; f++ )
				{
					var pose = clip.SampleConstrained( skeleton, f );

					for ( var b = 0; b < skeleton.Count; b++ )
						SetKey( baked, skeleton.Bones[b].Name, f, RigPose.FromXform( pose[b] ), RigEase.Smooth );
				}

				foreach ( var track in clip.Tracks.Where( t => t.Kind is RigTrackKind.IkGoal or RigTrackKind.Morph ) )
				{
					var copy = new RigTrack { Kind = track.Kind, Name = track.Name };

					foreach ( var k in track.Keys )
						copy.Keys.Add( new RigKey { Frame = k.Frame, Local = k.Local, Ease = k.Ease } );

					baked.Tracks.Add( copy );
				}

				var dir = Path.GetDirectoryName( Path.GetFullPath( path ) );

				if ( !string.IsNullOrEmpty( dir ) )
					Directory.CreateDirectory( dir );

				File.WriteAllText( path, RigAnimWriter.Write( baked ) );
				result.Written.Add( path );

				if ( name is not null )
				{
					result.Clips[name] = baked;
					result.Current = name;
				}

				result.Log.Add( $"saved clip to {path} ({last + 1} frame(s) baked)" );
				break;
			}

			default:
				throw new AnimException( $"unknown anim verb '{tokens[0]}'" );
		}
	}

	// --- baking helpers ----------------------------------------------------------------------

	/// <summary>Local poses per frame, 0..LastFrame, through the clip as it plays.</summary>
	static List<Xform[]> BakeFrames( RigAnimFile clip, Skeleton skeleton )
	{
		var frames = new List<Xform[]>();
		var last = Math.Max( clip.LastKeyFrame, 0 );

		for ( var f = 0; f <= last; f++ )
			frames.Add( clip.SampleLocal( skeleton, f ) );

		return frames;
	}

	/// <summary>Write baked frames back as per-frame keys, for the named bones only.</summary>
	static void WriteBackBones( RigAnimFile clip, Skeleton skeleton, IReadOnlyList<Xform[]> frames, IReadOnlyList<int> bones )
	{
		foreach ( var b in bones )
		{
			var name = skeleton.Bones[b].Name;
			var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == name );

			if ( track is null )
			{
				track = new RigTrack { Kind = RigTrackKind.Bone, Name = name };
				clip.Tracks.Add( track );
			}

			track.Keys.Clear();

			for ( var f = 0; f < frames.Count; f++ )
				track.Keys.Add( new RigKey { Frame = f, Local = RigPose.FromXform( frames[f][b] ), Ease = RigEase.Smooth } );
		}

		if ( frames.Count > clip.FrameCount )
			clip.FrameCount = frames.Count;
	}

	/// <summary>Write a whole baked AnimClip back as per-frame keys on every bone.</summary>
	static void WriteBackAll( RigAnimFile clip, Skeleton skeleton, AnimClip baked )
	{
		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var name = skeleton.Bones[b].Name;
			var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == name );

			if ( track is null )
			{
				track = new RigTrack { Kind = RigTrackKind.Bone, Name = name };
				clip.Tracks.Add( track );
			}

			track.Keys.Clear();

			for ( var f = 0; f < baked.FrameCount; f++ )
				track.Keys.Add( new RigKey { Frame = f, Local = RigPose.FromXform( baked.Frames[f][b] ), Ease = RigEase.Smooth } );
		}

		if ( baked.FrameCount > clip.FrameCount )
			clip.FrameCount = baked.FrameCount;
	}
}
