using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Effigy;

/// <summary>
/// A face rig the way Source does it (HL2 heads, Faceposer, the GMod Face Poser tool): shape keys are
/// <b>flexes</b>, and a smaller set of <b>flex controllers</b> (sliders) drives them. A stereo controller
/// is one slider plus a balance slider over a left and a right key (Split Shape Key makes those from
/// one sculpt, as QC <c>flexpair</c> did); a bipolar controller runs −1..1 over two opposite keys (jaw
/// sideways). An expression (preset) is just controller values.
///
/// The rig is data: the built-in HL2-style set (<see cref="Default"/>), trimmed to the keys a mesh
/// actually has (<see cref="For"/>), with presets saved next to the model in <c>NAME.facerig.json</c>.
/// Presets bake to s&amp;box as a ModelDoc AnimationList of MorphFrames (<see cref="WritePrefab"/>);
/// the ones named <c>viseme_*</c> are what s&amp;box lip-sync reads.
/// </summary>
public sealed class FaceRig
{
	public sealed class Controller
	{
		public string Name { get; set; } = "";
		public string Group { get; set; } = "";
		/// <summary>Plain: one key. Stereo: base name, keys BASE_L and BASE_R. Bipolar: keys Negative and Positive.</summary>
		public string Key { get; set; } = "";
		public bool Stereo { get; set; }
		/// <summary>Bipolar only: the keys the slider drives below and above 0.</summary>
		public string Negative { get; set; }
		public string Positive { get; set; }
		public string Tip { get; set; } = "";

		[JsonIgnore] public bool Bipolar => Negative is not null && Positive is not null;
		[JsonIgnore] public float Min => Bipolar ? -1 : 0;
		[JsonIgnore] public float Max => 1;

		public IEnumerable<string> Keys() =>
			Bipolar ? new[] { Negative, Positive } : Stereo ? new[] { Key + LeftSuffix, Key + RightSuffix } : new[] { Key };
	}

	/// <summary>One controller's setting: value, and for stereo ones the balance as Valve's DMX has it (0 = right only, 0.5 = both, 1 = left only).</summary>
	public struct Setting
	{
		public float Value { get; set; }
		public float Balance { get; set; }
		public Setting( float value, float balance = 0.5f ) { Value = value; Balance = balance; }
	}

	public const string LeftSuffix = "_L", RightSuffix = "_R";

	public List<Controller> Controllers { get; set; } = new();

	/// <summary>Expressions: preset name → controller name → setting. Missing controllers are 0.</summary>
	public Dictionary<string, Dictionary<string, Setting>> Presets { get; set; } = new();

	/// <summary>The groups, in panel order (the Face Poser tabs).</summary>
	public static readonly string[] Groups = { "Eyes", "Brows", "Cheeks & Nose", "Mouth", "Jaw" };

	/// <summary>
	/// Stereo balance (Valve DMX: 0 = 100% right, 1 = 100% left): at 0.5 both sides get the full
	/// value; moving towards one end fades the other side out, never boosting the near one.
	/// </summary>
	public static (float Left, float Right) Balance( float value, float balance )
	{
		balance = Math.Clamp( balance, 0, 1 );
		return (value * Math.Min( 1, 2 * balance ), value * Math.Min( 1, 2 * (1 - balance) ));
	}

	/// <summary>Morph weights for these settings, times <paramref name="scale"/> (Face Poser's flex scale). Every key of every controller is in the result, so it also zeros what it does not set.</summary>
	public Dictionary<string, float> Evaluate( IReadOnlyDictionary<string, Setting> settings, float scale = 1 )
	{
		var w = new Dictionary<string, float>();
		foreach ( var c in Controllers )
		{
			foreach ( var k in c.Keys() ) w.TryAdd( k, 0 );
			if ( !settings.TryGetValue( c.Name, out var s ) ) continue;
			var v = Math.Clamp( s.Value, c.Min, c.Max ) * scale;
			if ( c.Bipolar ) { w[c.Negative] += Math.Max( 0, -v ); w[c.Positive] += Math.Max( 0, v ); }
			else if ( c.Stereo ) { var (l, r) = Balance( v, s.Balance ); w[c.Key + LeftSuffix] += l; w[c.Key + RightSuffix] += r; }
			else w[c.Key] += v;
		}
		return w;
	}

	public Controller Find( string name ) =>
		Controllers.FirstOrDefault( c => string.Equals( c.Name, name, StringComparison.OrdinalIgnoreCase ) );

	/// <summary>Only the controllers whose keys all exist in <paramref name="morphNames"/>, and the presets trimmed to them.</summary>
	public FaceRig Trim( IEnumerable<string> morphNames )
	{
		var have = new HashSet<string>( morphNames );
		var rig = new FaceRig { Controllers = Controllers.Where( c => c.Keys().All( have.Contains ) ).ToList() };
		var names = new HashSet<string>( rig.Controllers.Select( c => c.Name ) );
		foreach ( var (p, s) in Presets )
			rig.Presets[p] = s.Where( kv => names.Contains( kv.Key ) ).ToDictionary( kv => kv.Key, kv => kv.Value );
		return rig;
	}

	/// <summary>Landmarks <see cref="GenerateStandardFlexes"/> fitted this rig to, if any.</summary>
	[JsonIgnore] public FaceBounds Bounds { get; set; }

	/// <summary>
	/// Procedurally generate the standard face rig for a head (roadmap J3): the full HL2-style
	/// FACS controller set from <see cref="Default"/> — 32 controllers covering the 24 standard
	/// stereo/bipolar flexes and more — plus the 15 s&amp;box <c>viseme_*</c> lip-sync presets,
	/// trimmed to what <paramref name="landmarks"/> says the head actually has. Eyes and brows
	/// need eyes, cheeks and nose need a nose, mouth needs a mouth, jaw needs a mouth to move.
	/// Presets are pruned to the surviving controllers, the way <see cref="Trim"/> prunes them
	/// to existing morphs, so a viseme never drives a controller the head does not have.
	/// The rig itself is morph-driven, so <paramref name="rig"/> is validated but its bone
	/// names do not gate any controller; the jaw bone, when present, is what a jaw flex drives.
	/// </summary>
	public static FaceRig GenerateStandardFlexes( PolyMesh headMesh, Skeleton rig, FaceBounds landmarks )
	{
		if ( headMesh is null )
			throw new ArgumentNullException( nameof( headMesh ) );

		if ( rig is null )
			throw new ArgumentNullException( nameof( rig ) );

		var result = Default();

		result.Bounds = landmarks;

		bool Keep( Controller c ) => c.Group switch
		{
			"Eyes" or "Brows" => landmarks.HasEyes,
			"Cheeks & Nose" => landmarks.HasNose,
			"Mouth" => landmarks.HasMouth,
			"Jaw" => landmarks.HasJaw,
			_ => true,
		};

		result.Controllers = result.Controllers.Where( Keep ).ToList();

		var names = new HashSet<string>( result.Controllers.Select( c => c.Name ) );

		foreach ( var (preset, settings) in result.Presets.ToList() )
			result.Presets[preset] = settings.Where( kv => names.Contains( kv.Key ) ).ToDictionary( kv => kv.Key, kv => kv.Value );

		return result;
	}

	/// <summary>GenerateStandardFlexes with landmarks estimated from the head mesh's bounds (Y up, +Z forward).</summary>
	public static FaceRig GenerateStandardFlexes( PolyMesh headMesh, Skeleton rig )
	{
		if ( headMesh is null )
			throw new ArgumentNullException( nameof( headMesh ) );

		var (min, max) = ModelMeasure.Bounds( headMesh );

		return GenerateStandardFlexes( headMesh, rig, FaceBounds.FromHeadBounds( min, max ) );
	}

	/// <summary>Every key the full rig expects, for a sculpting checklist.</summary>
	public IEnumerable<string> ExpectedKeys() => Controllers.SelectMany( c => c.Keys() ).Distinct();


	// ---------------------------------------------------------------- automatic FACS morph synthesis (spec 4.5)

	/// <summary>Options for <see cref="SynthesizeFacsMorphs"/>: which morph families to generate.</summary>
	public sealed class SynthesizeOptions
	{
		public bool Arkit52 = true;
		public bool Visemes = true;
		public bool FleshyLids = true;
		public bool LipZip = true;
	}

	/// <summary>Lid follow as a fraction of eye pitch (spec 4.5: 35%).</summary>
	public const float LidFollowPitchFraction = 0.35f;

	/// <summary>How far the cornea bulges past the eyeball sphere (spec 4.5: 0.04 in). Lids ride this proud.</summary>
	public const float CornealBulge = 0.04f;

	/// <summary>The 52 ARKit blendshape names, in ARKit order.</summary>
	public static readonly string[] ArkitMorphNames =
	{
		"browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight",
		"cheekPuff", "cheekSquintLeft", "cheekSquintRight",
		"eyeBlinkLeft", "eyeBlinkRight", "eyeLookDownLeft", "eyeLookDownRight",
		"eyeLookInLeft", "eyeLookInRight", "eyeLookOutLeft", "eyeLookOutRight",
		"eyeLookUpLeft", "eyeLookUpRight", "eyeSquintLeft", "eyeSquintRight", "eyeWideLeft", "eyeWideRight",
		"jawForward", "jawLeft", "jawOpen", "jawRight",
		"mouthClose", "mouthDimpleLeft", "mouthDimpleRight", "mouthFrownLeft", "mouthFrownRight",
		"mouthFunnel", "mouthLeft", "mouthLowerDownLeft", "mouthLowerDownRight",
		"mouthPressLeft", "mouthPressRight", "mouthPucker", "mouthRight",
		"mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper",
		"mouthSmileLeft", "mouthSmileRight", "mouthStretchLeft", "mouthStretchRight",
		"mouthUpperUpLeft", "mouthUpperUpRight", "noseSneerLeft", "noseSneerRight", "tongueOut",
	};

	/// <summary>
	/// Generate ready-to-use <see cref="MeshMorph"/> deltas for a head: the 32 flex controllers'
	/// keys (40 morphs, stereo split), the 52 ARKit morphs and the 15 viseme presets, plus the
	/// spec-named aliases (blink_L, eye_wide_L, squint_L, brow_inner_up_L, brow_outer_up_L,
	/// brow_down_L, smile_L, frown_L, upper_lip_raiser_L, nose_sneer_L, cheek_puff_L and their _R
	/// twins — same real deltas as lid_raiser_L, lid_tightener_L, inner_raiser_L, outer_raiser_L,
	/// lowerer_L, corner_puller_L, corner_depressor_L, upper_raiser_L, wrinkler, cheek_puffer_L).
	/// Morphs are installed on <paramref name="headMesh"/>.Morphs (same name replaced) and returned.
	/// Lids slide along the spherical eyeball surface, never in a straight line, so they cannot clip
	/// the cornea; brow morphs are strain-compatible with <see cref="WrinkleMaps.VertexValues"/>.
	/// </summary>
	public static IReadOnlyList<MeshMorph> SynthesizeFacsMorphs( PolyMesh headMesh, FaceBounds landmarks ) =>
		SynthesizeFacsMorphs( headMesh, landmarks, new SynthesizeOptions() );

	public static IReadOnlyList<MeshMorph> SynthesizeFacsMorphs( PolyMesh headMesh, FaceBounds landmarks, SynthesizeOptions options )
	{
		if ( headMesh is null ) throw new ArgumentNullException( nameof( headMesh ) );
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );
		if ( headMesh.VertexCount == 0 ) return Array.Empty<MeshMorph>();
		var (min, max) = ModelMeasure.Bounds( headMesh );
		var headH = MathF.Max( max.y - min.y, 1e-6f );
		var headW = MathF.Max( max.x - min.x, 1e-6f );
		var flex = new Dictionary<string, Vec3[]>();
		foreach ( var key in Default().Controllers.SelectMany( c => c.Keys() ).Distinct() )
		{
			if ( key == "lip_zip" ) continue;
			if ( !RegionPresent( key, landmarks ) ) continue;
			flex[key] = FlexDelta( key, headMesh, landmarks, headH, headW );
		}
		var made = new List<MeshMorph>();
		foreach ( var (name, deltas) in flex ) made.Add( new MeshMorph { Name = name, Deltas = deltas } );
		foreach ( var (alias, source) in AliasMap() )
		{
			if ( !flex.TryGetValue( source, out var d ) ) continue;
			if ( flex.ContainsKey( alias ) ) continue;
			made.Add( new MeshMorph { Name = alias, Deltas = d } );
			flex[alias] = d;
		}
		if ( options.Arkit52 )
		{
			foreach ( var name in ArkitMorphNames )
			{
				if ( flex.ContainsKey( name ) ) continue;
				if ( ArkitDelta( name, headMesh, landmarks, headH, headW, flex ) is { } d )
					made.Add( new MeshMorph { Name = name, Deltas = d } );
			}
		}
		if ( options.Visemes && landmarks.HasMouth )
		{
			var rig = Default();
			foreach ( var (preset, settings) in rig.Presets.Where( p => p.Key.StartsWith( "viseme_" ) ) )
			{
				var weights = rig.Evaluate( settings );
				var d = new Vec3[headMesh.VertexCount];
				foreach ( var (key, w) in weights )
				{
					if ( MathF.Abs( w ) < 1e-4f || !flex.TryGetValue( key, out var fd ) ) continue;
					for ( var i = 0; i < d.Length; i++ ) d[i] += fd[i] * w;
				}
				made.Add( new MeshMorph { Name = preset, Deltas = d } );
			}
		}
		if ( options.LipZip && landmarks.HasMouth && flex.TryGetValue( "jaw_drop", out var jaw ) )
		{
			var part = flex.TryGetValue( "part", out var pd ) ? pd : new Vec3[headMesh.VertexCount];
			var d = new Vec3[headMesh.VertexCount];
			var halfMouth = MathF.Max( (landmarks.MouthRight.x - landmarks.MouthLeft.x) * 0.5f, 1e-6f );
			for ( var i = 0; i < d.Length; i++ )
			{
				var p = headMesh.Positions[i];
				if ( DistToSegment( p, landmarks.MouthLeft, landmarks.MouthRight ) > 0.045f * headH ) continue;
				var seal = Math.Clamp( MathF.Abs( p.x - landmarks.MouthCenter.x ) / halfMouth, 0f, 1f );
				d[i] = (jaw[i] + part[i]) * -seal;
			}
			made.Add( new MeshMorph { Name = "lip_zip", Deltas = d } );
		}
		if ( options.FleshyLids && landmarks.HasEyes )
		{
			foreach ( var m in FleshyEyeTrackDeltas( headMesh, landmarks, 0f, 0f ) ) made.Add( m );
		}
		FacsMorphInstall.Install( headMesh, made );
		return made;
	}

	/// <summary>
	/// The fleshy eye-tracking driver (spec 4.5): for an eye rotation of <paramref name="pitch"/>
	/// (radians, positive = gaze up) and <paramref name="yaw"/> (positive = gaze to the head's right),
	/// the lids follow 35% of the pitch along the eyeball sphere and ride 0.04 in proud of the corneal
	/// bulge. Returns lid_follow_L / lid_follow_R morphs for that pose; pitch = yaw = 0 gives the
	/// rest-pose (zero-delta) drivers SynthesizeFacsMorphs installs. FacePoserService calls this per
	/// frame and blends the two morphs by the live eye rotation — it is the kernel side of
	/// FacePoserService.ApplyFleshyEyeTrack, which lives in the desktop app, not the kernel.
	/// </summary>
	public static IReadOnlyList<MeshMorph> FleshyEyeTrackDeltas( PolyMesh headMesh, FaceBounds landmarks, float pitch, float yaw )
	{
		if ( headMesh is null ) throw new ArgumentNullException( nameof( headMesh ) );
		var (min, max) = ModelMeasure.Bounds( headMesh );
		var headH = MathF.Max( max.y - min.y, 1e-6f );
		var headW = MathF.Max( max.x - min.x, 1e-6f );
		var follow = LidFollowPitchFraction * pitch;
		var bulge = CornealBulge * Math.Clamp( MathF.Abs( pitch ) / 0.6f, 0f, 1f );
		var o = new List<MeshMorph>();
		foreach ( var left in new[] { true, false } )
		{
			var d = new Vec3[headMesh.VertexCount];
			EyeSphere( landmarks, headH, left, out var centre, out var radius );
			var (inner, outer) = EyeCorners( landmarks, headW, left );
			var lidLine = left ? landmarks.LeftEye : landmarks.RightEye;
			for ( var i = 0; i < d.Length; i++ )
			{
				var p = headMesh.Positions[i];
				var w = Capsule( p, inner, outer, 0.045f * headH );
				if ( w <= 0f ) continue;
				var upper = p.y >= lidLine.y ? 1f : 0.35f;
				var rotated = RotatePoint( p, centre, new Vec3( 1, 0, 0 ), -follow * upper );
				var radial = rotated - centre;
				var len = radial.Length;
				if ( len < radius + bulge && len > 1e-6f ) rotated = centre + radial / len * (radius + bulge);
				var shift = new Vec3( yaw * radius * 0.2f * w, 0, 0 );
				d[i] = (rotated - p) * w + shift;
			}
			o.Add( new MeshMorph { Name = left ? "lid_follow_L" : "lid_follow_R", Deltas = d } );
		}
		return o;
	}

	/// <summary>
	/// Brow wrinkle coupling (spec 4.5): the forehead/glabella compression a brow morph produces,
	/// as per-vertex strain values from <see cref="WrinkleMaps.VertexValues"/> — the same values the
	/// wrinkle baker turns into squish maps. Call after generating a brow morph.
	/// </summary>
	public static float[] WrinkleStrain( PolyMesh mesh, MeshMorph morph ) => WrinkleMaps.VertexValues( mesh, morph );

	static bool RegionPresent( string key, FaceBounds landmarks )
	{
		if ( key.StartsWith( "blink" ) || key.StartsWith( "lid_" ) || key.StartsWith( "half_closed" )
			|| key.StartsWith( "inner_raiser" ) || key.StartsWith( "outer_raiser" ) || key.StartsWith( "lowerer" ) )
			return landmarks.HasEyes;
		if ( key.StartsWith( "cheek_raiser" ) || key.StartsWith( "wrinkler" ) || key.StartsWith( "dilator" ) || key.StartsWith( "cheek_puffer" ) )
			return landmarks.HasNose || landmarks.HasEyes;
		if ( key.StartsWith( "jaw_" ) || key.StartsWith( "mouth_" ) )
			return landmarks.HasJaw || landmarks.HasMouth;
		return landmarks.HasMouth;
	}

	static IEnumerable<(string Alias, string Source)> AliasMap()
	{
		yield return ("blink_L", "blink");
		yield return ("blink_R", "blink");
		yield return ("eye_wide_L", "lid_raiser_L");
		yield return ("eye_wide_R", "lid_raiser_R");
		yield return ("squint_L", "lid_tightener_L");
		yield return ("squint_R", "lid_tightener_R");
		yield return ("brow_inner_up_L", "inner_raiser_L");
		yield return ("brow_inner_up_R", "inner_raiser_R");
		yield return ("brow_outer_up_L", "outer_raiser_L");
		yield return ("brow_outer_up_R", "outer_raiser_R");
		yield return ("brow_down_L", "lowerer_L");
		yield return ("brow_down_R", "lowerer_R");
		yield return ("smile_L", "corner_puller_L");
		yield return ("smile_R", "corner_puller_R");
		yield return ("frown_L", "corner_depressor_L");
		yield return ("frown_R", "corner_depressor_R");
		yield return ("upper_lip_raiser_L", "upper_raiser_L");
		yield return ("upper_lip_raiser_R", "upper_raiser_R");
		yield return ("nose_sneer_L", "wrinkler");
		yield return ("nose_sneer_R", "wrinkler");
		yield return ("cheek_puff_L", "cheek_puffer_L");
		yield return ("cheek_puff_R", "cheek_puffer_R");
	}


	// ---------------------------------------------------------------- synthesis helpers (spec 4.5)

	static float Smooth01( float a, float b, float x )
	{
		if ( a < b ) { var t = Math.Clamp( (x - a) / MathF.Max( b - a, 1e-6f ), 0f, 1f ); return t * t * (3f - 2f * t); }
		var t2 = Math.Clamp( (x - b) / MathF.Max( a - b, 1e-6f ), 0f, 1f ); return 1f - t2 * t2 * (3f - 2f * t2);
	}

	static float DistToSegment( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var len2 = ab.LengthSquared;
		var t = len2 < 1e-12f ? 0f : Math.Clamp( Vec3.Dot( p - a, ab ) / len2, 0f, 1f );
		return (p - (a + ab * t)).Length;
	}

	/// <summary>Smooth anatomical falloff capsule: 1 on the segment, 0 at radius, smooth between.</summary>
	static float Capsule( Vec3 p, Vec3 a, Vec3 b, float radius ) => 1f - Smooth01( 0f, MathF.Max( radius, 1e-6f ), DistToSegment( p, a, b ) );

	static Vec3 RotatePoint( Vec3 p, Vec3 pivot, Vec3 axis, float angle )
	{
		var k = axis.Normal;
		var v = p - pivot;
		var cos = MathF.Cos( angle );
		var sin = MathF.Sin( angle );
		return pivot + v * cos + Vec3.Cross( k, v ) * sin + k * Vec3.Dot( k, v ) * (1f - cos);
	}

	static void TranslateCapsule( Vec3[] d, PolyMesh mesh, Vec3 a, Vec3 b, float radius, Vec3 dir, float amp )
	{
		for ( var i = 0; i < d.Length; i++ )
		{
			var w = Capsule( mesh.Positions[i], a, b, radius );
			if ( w > 0f ) d[i] += dir * (amp * w);
		}
	}

	static void TranslatePoint( Vec3[] d, PolyMesh mesh, Vec3 centre, float radius, Vec3 dir, float amp )
	{
		for ( var i = 0; i < d.Length; i++ )
		{
			var w = 1f - Smooth01( 0f, MathF.Max( radius, 1e-6f ), (mesh.Positions[i] - centre).Length );
			if ( w > 0f ) d[i] += dir * (amp * w);
		}
	}

	/// <summary>Rotate the lower face around the jaw pivot (chin drops down-and-back for angle > 0).</summary>
	static void JawRotate( Vec3[] d, PolyMesh mesh, FaceBounds landmarks, float headH, float angle )
	{
		var pivot = landmarks.Jaw;
		var mouthY = landmarks.MouthCenter.y;
		for ( var i = 0; i < d.Length; i++ )
		{
			var p = mesh.Positions[i];
			if ( p.y >= mouthY ) continue;
			var w = Smooth01( mouthY, mouthY - 0.06f * headH, p.y );
			if ( w <= 0f ) continue;
			d[i] += (RotatePoint( p, pivot, new Vec3( 1, 0, 0 ), angle ) - p) * w;
		}
	}

	static void EyeSphere( FaceBounds landmarks, float headH, bool left, out Vec3 centre, out float radius )
	{
		radius = Math.Clamp( 0.05f * headH, 0.3f, 0.6f );
		var eye = left ? landmarks.LeftEye : landmarks.RightEye;
		centre = eye - new Vec3( 0, 0, radius * 0.55f );
	}

	static (Vec3 Inner, Vec3 Outer) EyeCorners( FaceBounds landmarks, float headW, bool left )
	{
		var eye = left ? landmarks.LeftEye : landmarks.RightEye;
		var towardCentre = left ? 1f : -1f;
		return (eye + new Vec3( towardCentre * 0.055f * headW, 0, 0 ), eye - new Vec3( towardCentre * 0.055f * headW, 0, 0 ));
	}

	static (Vec3 Inner, Vec3 Outer) BrowPoints( FaceBounds landmarks, float headH, float headW, bool left )
	{
		var eye = left ? landmarks.LeftEye : landmarks.RightEye;
		var towardCentre = left ? 1f : -1f;
		return (
			eye + new Vec3( towardCentre * 0.08f * headW, 0.085f * headH, 0 ),
			eye + new Vec3( -towardCentre * 0.16f * headW, 0.07f * headH, 0 ) );
	}

	/// <summary>
	/// Slide the lids along the eyeball sphere: upper lid rotates by <paramref name="upperAngle"/>
	/// (positive closes it, sliding down the front of the sphere), lower lid by
	/// <paramref name="lowerAngle"/> (negative raises it). Vertices stay on the sphere, pushed out to
	/// radius + <see cref="CornealBulge"/> when they would ride inside it — so a blink can never clip
	/// the cornea. Angles in radians; a full blink is about +1.1 / -0.65.
	/// </summary>
	static Vec3[] LidDelta( PolyMesh mesh, FaceBounds landmarks, float headH, float headW, bool left, float upperAngle, float lowerAngle )
	{
		var d = new Vec3[mesh.VertexCount];
		EyeSphere( landmarks, headH, left, out var centre, out var radius );
		var (inner, outer) = EyeCorners( landmarks, headW, left );
		var eye = left ? landmarks.LeftEye : landmarks.RightEye;
		for ( var i = 0; i < d.Length; i++ )
		{
			var p = mesh.Positions[i];
			var w = Capsule( p, inner, outer, 0.045f * headH );
			if ( w <= 0f ) continue;
			var angle = p.y >= eye.y ? upperAngle : lowerAngle;
			var rotated = RotatePoint( p, centre, new Vec3( 1, 0, 0 ), angle );
			var radial = rotated - centre;
			var len = radial.Length;
			if ( len < radius + CornealBulge && len > 1e-6f ) rotated = centre + radial / len * (radius + CornealBulge);
			d[i] = (rotated - p) * w;
		}
		return d;
	}

	static Vec3[] Add( Vec3[] a, Vec3[] b )
	{
		var o = new Vec3[a.Length];
		for ( var i = 0; i < o.Length; i++ ) o[i] = a[i] + b[i];
		return o;
	}

	static Vec3[] Scale( Vec3[] a, float k )
	{
		var o = new Vec3[a.Length];
		for ( var i = 0; i < o.Length; i++ ) o[i] = a[i] * k;
		return o;
	}

	static Vec3[] SplitSide( Vec3[] a, PolyMesh mesh, float centreX, float headW, bool left )
	{
		var o = new Vec3[a.Length];
		for ( var i = 0; i < o.Length; i++ )
		{
			var p = mesh.Positions[i];
			var w = left
				? Math.Clamp( (centreX + 0.04f * headW - p.x) / (0.08f * headW), 0f, 1f )
				: Math.Clamp( (p.x - centreX + 0.04f * headW) / (0.08f * headW), 0f, 1f );
			o[i] = a[i] * w;
		}
		return o;
	}

	static bool IsLeft( string key ) => key.EndsWith( "_L", StringComparison.Ordinal );

	static Vec3[] FlexDelta( string key, PolyMesh mesh, FaceBounds landmarks, float headH, float headW )
	{
		var left = IsLeft( key );
		var eye = left ? landmarks.LeftEye : landmarks.RightEye;
		var towardCentre = left ? 1f : -1f;
		var (browInner, browOuter) = BrowPoints( landmarks, headH, headW, left );
		var mouthL = landmarks.MouthLeft;
		var mouthR = landmarks.MouthRight;
		var mouthC = landmarks.MouthCenter;
		var upperLip = new Vec3( mouthC.x, mouthC.y + 0.015f * headH, mouthC.z );
		var lowerLip = new Vec3( mouthC.x, mouthC.y - 0.015f * headH, mouthC.z );
		var up = new Vec3( 0, 1, -0.15f ).Normal;
		var down = new Vec3( 0, -1, 0.1f ).Normal;
		Vec3[] d;
		switch ( key )
		{
			case "blink": return Add( LidDelta( mesh, landmarks, headH, headW, true, 1.1f, -0.65f ), LidDelta( mesh, landmarks, headH, headW, false, 1.1f, -0.65f ) );
			case "lid_closer_L": case "lid_closer_R": return LidDelta( mesh, landmarks, headH, headW, left, 0.95f, 0f );
			case "lid_raiser_L": case "lid_raiser_R": return LidDelta( mesh, landmarks, headH, headW, left, -0.4f, 0.12f );
			case "lid_tightener_L": case "lid_tightener_R":
				d = LidDelta( mesh, landmarks, headH, headW, left, 0.3f, -0.45f );
				TranslatePoint( d, mesh, new Vec3( eye.x, eye.y - 0.05f * headH, eye.z ), 0.09f * headH, up, 0.010f * headH );
				return d;
			case "lid_droop_L": case "lid_droop_R": return LidDelta( mesh, landmarks, headH, headW, left, 0.3f, 0f );
			case "half_closed": return Add( LidDelta( mesh, landmarks, headH, headW, true, 0.55f, -0.3f ), LidDelta( mesh, landmarks, headH, headW, false, 0.55f, -0.3f ) );
			case "inner_raiser_L": case "inner_raiser_R":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, browInner, 0.05f * headH, up, 0.020f * headH );
				return d;
			case "outer_raiser_L": case "outer_raiser_R":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, browOuter, 0.05f * headH, up, 0.016f * headH );
				return d;
			case "lowerer_L": case "lowerer_R":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, browInner, browOuter, 0.05f * headH, (down + new Vec3( towardCentre * 0.5f, 0, 0 )).Normal, 0.018f * headH );
				return d;
			case "cheek_raiser_L": case "cheek_raiser_R":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, new Vec3( eye.x, eye.y - 0.06f * headH, eye.z ), new Vec3( eye.x, eye.y - 0.12f * headH, eye.z ), 0.08f * headH, up, 0.014f * headH );
				return d;
			case "wrinkler":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, landmarks.NoseTip, (landmarks.LeftEye + landmarks.RightEye) * 0.5f, 0.06f * headH, up, 0.010f * headH );
				return d;
			case "dilator":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, landmarks.NoseTip + new Vec3( -0.03f * headW, 0, 0 ), 0.04f * headH, new Vec3( -1, 0, 0 ), 0.008f * headH );
				TranslatePoint( d, mesh, landmarks.NoseTip + new Vec3( 0.03f * headW, 0, 0 ), 0.04f * headH, new Vec3( 1, 0, 0 ), 0.008f * headH );
				return d;
			case "cheek_puffer_L": case "cheek_puffer_R":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, new Vec3( eye.x, eye.y - 0.14f * headH, eye.z ), 0.10f * headH, new Vec3( towardCentre * -0.7f, 0, 0.7f ).Normal, 0.016f * headH );
				return d;
			case "upper_raiser_L": case "upper_raiser_R":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.04f * headH, up, 0.014f * headH );
				return SplitSide( d, mesh, mouthC.x, headW, left );
			case "corner_puller_L": case "corner_puller_R":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, left ? mouthL : mouthR, 0.05f * headH, new Vec3( towardCentre * -0.3f, 0.8f, -0.5f ).Normal, 0.030f * headH );
				return d;
			case "smile":
				d = FlexDelta( "corner_puller_L", mesh, landmarks, headH, headW );
				d = Add( d, FlexDelta( "corner_puller_R", mesh, landmarks, headH, headW ) );
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.04f * headH, up, 0.008f * headH );
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.04f * headH, down, 0.008f * headH );
				return d;
			case "corner_depressor_L": case "corner_depressor_R":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, left ? mouthL : mouthR, 0.05f * headH, down, 0.024f * headH );
				return d;
			case "dimpler_L": case "dimpler_R":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, left ? mouthL : mouthR, 0.04f * headH, new Vec3( towardCentre * 0.9f, 0.3f, 0 ).Normal, 0.014f * headH );
				return d;
			case "stretcher_L": case "stretcher_R":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, left ? mouthL : mouthR, 0.05f * headH, new Vec3( towardCentre * -1f, 0, 0 ), 0.020f * headH );
				return d;
			case "part":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.03f * headH, up, 0.012f * headH );
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.03f * headH, down, 0.012f * headH );
				return d;
			case "puckerer":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.04f * headH, new Vec3( 0, -0.3f, 0.9f ).Normal, 0.014f * headH );
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.04f * headH, new Vec3( 0, 0.3f, 0.9f ).Normal, 0.014f * headH );
				return d;
			case "funneler":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.04f * headH, new Vec3( 0, 0, 1 ), 0.020f * headH );
				return d;
			case "bite":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, lowerLip, 0.04f * headH, new Vec3( 0, 0.5f, -0.8f ).Normal, 0.012f * headH );
				return d;
			case "presser":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.03f * headH, down, 0.010f * headH );
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.03f * headH, up, 0.010f * headH );
				return d;
			case "tightener":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.03f * headH, down, 0.006f * headH );
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.03f * headH, up, 0.006f * headH );
				return d;
			case "lower_lip":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, lowerLip, 0.04f * headH, down, 0.014f * headH );
				return d;
			case "chin_raiser":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, landmarks.Jaw, 0.06f * headH, up, 0.016f * headH );
				return d;
			case "mouth_left":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.05f * headH, new Vec3( -1, 0, 0 ), 0.020f * headH );
				return d;
			case "mouth_right":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.05f * headH, new Vec3( 1, 0, 0 ), 0.020f * headH );
				return d;
			case "jaw_drop":
				d = new Vec3[mesh.VertexCount];
				JawRotate( d, mesh, landmarks, headH, 0.35f );
				return d;
			case "mouth_drop":
				d = new Vec3[mesh.VertexCount];
				JawRotate( d, mesh, landmarks, headH, 0.5f );
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.03f * headH, up, 0.008f * headH );
				TranslateCapsule( d, mesh, mouthL, mouthR, 0.03f * headH, down, 0.008f * headH );
				return d;
			case "jaw_clencher":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, landmarks.Jaw, mouthC, 0.08f * headH, up, 0.010f * headH );
				return d;
			case "jaw_left":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, landmarks.Jaw, mouthC, 0.12f * headH, new Vec3( -1, 0, 0 ), 0.020f * headH );
				return d;
			case "jaw_right":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, landmarks.Jaw, mouthC, 0.12f * headH, new Vec3( 1, 0, 0 ), 0.020f * headH );
				return d;
			default:
				return new Vec3[mesh.VertexCount];
		}
	}

	/// <summary>One ARKit morph from the flex deltas, or a direct capsule rule where no flex maps.</summary>
	static Vec3[] ArkitDelta( string name, PolyMesh mesh, FaceBounds landmarks, float headH, float headW, Dictionary<string, Vec3[]> flex )
	{
		Vec3[] Get( string k ) => flex.TryGetValue( k, out var v ) ? v : new Vec3[mesh.VertexCount];
		Vec3[] Avg( string a, string b ) => Scale( Add( Get( a ), Get( b ) ), 0.5f );
		var mouthC = landmarks.MouthCenter;
		var eyeL = landmarks.LeftEye;
		var eyeR = landmarks.RightEye;
		Vec3[] d;
		switch ( name )
		{
			case "browDownLeft": return Get( "lowerer_L" );
			case "browDownRight": return Get( "lowerer_R" );
			case "browInnerUp": return Avg( "inner_raiser_L", "inner_raiser_R" );
			case "browOuterUpLeft": return Get( "outer_raiser_L" );
			case "browOuterUpRight": return Get( "outer_raiser_R" );
			case "cheekPuff": return Avg( "cheek_puffer_L", "cheek_puffer_R" );
			case "cheekSquintLeft": return Get( "cheek_raiser_L" );
			case "cheekSquintRight": return Get( "cheek_raiser_R" );
			case "eyeBlinkLeft": return Get( "blink_L" );
			case "eyeBlinkRight": return Get( "blink_R" );
			case "eyeLookDownLeft": case "eyeLookDownRight":
			case "eyeLookUpLeft": case "eyeLookUpRight":
			case "eyeLookInLeft": case "eyeLookInRight":
			case "eyeLookOutLeft": case "eyeLookOutRight":
			{
				var left = name.EndsWith( "Left", StringComparison.Ordinal );
				var eye = left ? eyeL : eyeR;
				var towardCentre = left ? 1f : -1f;
				var dir = name.Contains( "Down" ) ? new Vec3( 0, -1, 0 )
					: name.Contains( "Up" ) ? new Vec3( 0, 1, 0 )
					: name.Contains( "In" ) ? new Vec3( towardCentre, 0, 0 )
					: new Vec3( -towardCentre, 0, 0 );
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, eye, 0.06f * headH, dir, 0.006f * headH );
				return d;
			}
			case "eyeSquintLeft": return Get( "lid_tightener_L" );
			case "eyeSquintRight": return Get( "lid_tightener_R" );
			case "eyeWideLeft": return Get( "lid_raiser_L" );
			case "eyeWideRight": return Get( "lid_raiser_R" );
			case "jawForward":
				d = new Vec3[mesh.VertexCount];
				TranslateCapsule( d, mesh, landmarks.Jaw, mouthC, 0.12f * headH, new Vec3( 0, 0, 1 ), 0.020f * headH );
				return d;
			case "jawLeft": return Get( "jaw_left" );
			case "jawOpen": return Get( "jaw_drop" );
			case "jawRight": return Get( "jaw_right" );
			case "mouthClose": return Get( "presser" );
			case "mouthDimpleLeft": return Get( "dimpler_L" );
			case "mouthDimpleRight": return Get( "dimpler_R" );
			case "mouthFrownLeft": return Get( "corner_depressor_L" );
			case "mouthFrownRight": return Get( "corner_depressor_R" );
			case "mouthFunnel": return Get( "funneler" );
			case "mouthLeft": return Get( "mouth_left" );
			case "mouthRight": return Get( "mouth_right" );
			case "mouthLowerDownLeft": return SplitSide( Get( "lower_lip" ), mesh, mouthC.x, headW, true );
			case "mouthLowerDownRight": return SplitSide( Get( "lower_lip" ), mesh, mouthC.x, headW, false );
			case "mouthPressLeft": return SplitSide( Get( "presser" ), mesh, mouthC.x, headW, true );
			case "mouthPressRight": return SplitSide( Get( "presser" ), mesh, mouthC.x, headW, false );
			case "mouthPucker": return Get( "puckerer" );
			case "mouthRollLower":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, new Vec3( mouthC.x, mouthC.y - 0.015f * headH, mouthC.z ), 0.04f * headH, new Vec3( 0, -0.2f, -0.9f ).Normal, 0.008f * headH );
				return d;
			case "mouthRollUpper":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, new Vec3( mouthC.x, mouthC.y + 0.015f * headH, mouthC.z ), 0.04f * headH, new Vec3( 0, 0.2f, -0.9f ).Normal, 0.008f * headH );
				return d;
			case "mouthShrugLower":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, new Vec3( mouthC.x, mouthC.y - 0.015f * headH, mouthC.z ), 0.04f * headH, new Vec3( 0, 1, 0 ), 0.008f * headH );
				return d;
			case "mouthShrugUpper":
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, new Vec3( mouthC.x, mouthC.y + 0.015f * headH, mouthC.z ), 0.04f * headH, new Vec3( 0, -1, 0 ), 0.008f * headH );
				return d;
			case "mouthSmileLeft": return Get( "corner_puller_L" );
			case "mouthSmileRight": return Get( "corner_puller_R" );
			case "mouthStretchLeft": return Get( "stretcher_L" );
			case "mouthStretchRight": return Get( "stretcher_R" );
			case "mouthUpperUpLeft": return Get( "upper_raiser_L" );
			case "mouthUpperUpRight": return Get( "upper_raiser_R" );
			case "noseSneerLeft": return SplitSide( Get( "wrinkler" ), mesh, mouthC.x, headW, true );
			case "noseSneerRight": return SplitSide( Get( "wrinkler" ), mesh, mouthC.x, headW, false );
			case "tongueOut":
				// No tongue mesh on a bare head: the visible proxy is the lower lip + chin pushed
				// forward and down. The dental MouthMaker tongue carries the real tongue-out.
				d = new Vec3[mesh.VertexCount];
				TranslatePoint( d, mesh, new Vec3( mouthC.x, mouthC.y - 0.015f * headH, mouthC.z ), 0.06f * headH, new Vec3( 0, -0.3f, 0.9f ).Normal, 0.018f * headH );
				return d;
			default: return null;
		}
	}

	// ---------------------------------------------------------------- sidecar

	public static string SidecarPath( string model ) =>
		Path.Combine( Path.GetDirectoryName( Path.GetFullPath( model ) ) ?? ".", Path.GetFileNameWithoutExtension( model ) + ".facerig.json" );

	static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

	/// <summary>The rig for a model: its sidecar if it has one, else the default; trimmed to the keys the model has.</summary>
	public static FaceRig For( string model, IEnumerable<string> morphNames )
	{
		var path = model is null ? null : SidecarPath( model );
		var rig = path is not null && File.Exists( path ) ? Load( path ) : Default();
		return rig.Trim( morphNames );
	}

	public static FaceRig Load( string path ) =>
		JsonSerializer.Deserialize<FaceRig>( File.ReadAllText( path ), Json ) ?? throw new InvalidDataException( $"{path} is not a face rig." );

	public void Save( string path ) => File.WriteAllText( path, JsonSerializer.Serialize( this, Json ) + "\n" );

	/// <summary>Add or replace one preset in a model's sidecar (creating it from the default rig), keeping its other presets.</summary>
	public static void SavePreset( string model, string preset, IReadOnlyDictionary<string, Setting> settings )
	{
		var path = SidecarPath( model );
		var rig = File.Exists( path ) ? Load( path ) : Default();
		rig.Presets[preset] = settings.Where( kv => Math.Abs( kv.Value.Value ) > 1e-4f ).ToDictionary( kv => kv.Key, kv => kv.Value );
		rig.Save( path );
	}

	// ---------------------------------------------------------------- s&box

	/// <summary>
	/// Write the presets as a ModelDoc AnimationList prefab: one MorphFrame per preset with its morph
	/// weights. Reference it from the .vmdl's AnimationList (a Prefab node) so the frames compile in;
	/// viseme_* frames feed s&amp;box lip-sync, the rest are poses for an animgraph or code.
	/// </summary>
	public string WritePrefab( string path, float fade = 0.2f )
	{
		var sb = new StringBuilder();
		sb.Append( "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->\n" );
		sb.Append( "{\n\trootNode =\n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren =\n\t\t[\n\t\t\t{\n\t\t\t\t_class = \"AnimationList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n" );
		var frames = Presets.OrderBy( p => p.Key.StartsWith( "viseme_" ) ? 1 : 0 ).ThenBy( p => p.Key ).ToList();
		for ( var i = 0; i < frames.Count; i++ )
		{
			var (name, settings) = frames[i];
			var weights = Evaluate( settings ).Where( kv => kv.Value > 1e-4f ).OrderBy( kv => kv.Key ).ToList();
			string t = "\t\t\t\t\t";
			sb.Append( $"{t}{{\n{t}\t_class = \"MorphFrame\"\n{t}\tname = \"{name}\"\n{t}\tactivity_name = \"\"\n{t}\tactivity_weight = 1\n{t}\tweight_list_name = \"\"\n" );
			sb.Append( string.Create( CultureInfo.InvariantCulture, $"{t}\tfade_in_time = {fade}\n{t}\tfade_out_time = {fade}\n" ) );
			sb.Append( $"{t}\tlooping = false\n{t}\tdelta = false\n{t}\tworldSpace = false\n{t}\thidden = false\n{t}\tanim_markup_ordered = false\n{t}\tdisable_compression = false\n{t}\tdisable_interpolation = false\n{t}\tenable_scale = false\n" );
			sb.Append( $"{t}\tmorph_weights =\n{t}\t[\n" );
			for ( var j = 0; j < weights.Count; j++ )
				sb.Append( string.Create( CultureInfo.InvariantCulture, $"{t}\t\t{{\n{t}\t\t\tmorph = \"{weights[j].Key}\"\n{t}\t\t\tweight = {Math.Round( weights[j].Value, 3 )}\n{t}\t\t}}{(j < weights.Count - 1 ? "," : "")}\n" ) );
			sb.Append( $"{t}\t]\n{t}}}{(i < frames.Count - 1 ? "," : "")}\n" );
		}
		sb.Append( "\t\t\t\t]\n\t\t\t\tdefault_root_bone_name = \"\"\n\t\t\t},\n\t\t]\n\t}\n}\n" );
		File.WriteAllText( path, sb.ToString() );
		return path;
	}

	// ---------------------------------------------------------------- the built-in rig

	static Controller C( string group, string name, string tip, bool stereo = true ) =>
		new() { Group = group, Name = name, Key = name, Stereo = stereo, Tip = tip };

	static Controller B( string group, string name, string negative, string positive, string tip ) =>
		new() { Group = group, Name = name, Negative = negative, Positive = positive, Tip = tip };

	static Dictionary<string, Setting> P( params (string Name, float Value)[] v ) =>
		v.ToDictionary( x => x.Name, x => new Setting( x.Value ) );

	/// <summary>
	/// The HL2 flex controller set (Valve's FACS-based heads), stereo pairs folded under one slider as
	/// Faceposer shows them, plus neutral/tired/smile/frown and the s&amp;box viseme set.
	/// </summary>
	public static FaceRig Default()
	{
		var rig = new FaceRig
		{
			Controllers =
			{
				C( "Eyes", "blink", "Closes both lids fully; the quick one for blinks." , stereo: false ),
				C( "Eyes", "lid_closer", "Closes the upper lid (AU 43): sleepy to shut." ),
				C( "Eyes", "lid_raiser", "Opens the upper lid wide (AU 5): surprise, fear." ),
				C( "Eyes", "lid_tightener", "Squints the lower lid up (AU 7): suspicion, effort." ),
				C( "Eyes", "lid_droop", "Upper lid sags heavy (AU 41): tired, bored." ),
				C( "Eyes", "half_closed", "Narrows both eyes to a slit (AU 42).", stereo: false ),
				C( "Brows", "inner_raiser", "Lifts the inner brow (AU 1): worry, sadness." ),
				C( "Brows", "outer_raiser", "Lifts the outer brow (AU 2): surprise, a questioning look." ),
				C( "Brows", "lowerer", "Pulls the brow down and in (AU 4): anger, concentration." ),
				C( "Cheeks & Nose", "cheek_raiser", "Lifts the cheek and crinkles the lower lid (AU 6): a real smile." ),
				C( "Cheeks & Nose", "wrinkler", "Wrinkles the nose (AU 9): disgust.", stereo: false ),
				C( "Cheeks & Nose", "dilator", "Flares the nostrils (AU 38): effort, anger.", stereo: false ),
				C( "Cheeks & Nose", "cheek_puffer", "Puffs the cheek out with air." ),
				C( "Mouth", "upper_raiser", "Lifts the upper lip (AU 10): sneer, disgust." ),
				C( "Mouth", "corner_puller", "Pulls the lip corner up and back (AU 12): smile." ),
				C( "Mouth", "smile", "A full open smile, lips parted (AU 12+25).", stereo: false ),
				C( "Mouth", "corner_depressor", "Pulls the lip corner down (AU 15): frown, sadness." ),
				C( "Mouth", "dimpler", "Tucks the lip corner in (AU 14): smirk, contempt." ),
				C( "Mouth", "stretcher", "Stretches the lips sideways (AU 20): fear, a grimace." ),
				C( "Mouth", "part", "Parts the lips without dropping the jaw (AU 25)." ),
				C( "Mouth", "puckerer", "Pushes the lips forward into a kiss (AU 18): oo, w." ),
				C( "Mouth", "funneler", "Rounds the lips into a tube (AU 22): o, sh." ),
				C( "Mouth", "bite", "Bites the lower lip under the top teeth (AU 32): f, v.", stereo: false ),
				C( "Mouth", "presser", "Presses the lips together (AU 24): m, b, p, holding back.", stereo: false ),
				C( "Mouth", "tightener", "Thins and tightens the lips (AU 23): anger.", stereo: false ),
				C( "Mouth", "lower_lip", "Rolls the lower lip down (AU 16): f, v, disgust.", stereo: false ),
				C( "Mouth", "chin_raiser", "Pushes the chin and lower lip up (AU 17): pout, doubt.", stereo: false ),
				C( "Mouth", "lip_zip", "Keeps the lips sealed from the corners inward as the jaw opens; peels open centre-to-corners (sticky lips).", stereo: false ),
				B( "Mouth", "mouth_sideways", "mouth_left", "mouth_right", "Shifts the whole mouth to the character's left (−) or right (+)." ),
				C( "Jaw", "jaw_drop", "Opens the jaw (AU 26/27): talking, shock.", stereo: false ),
				C( "Jaw", "mouth_drop", "Stretches the mouth open past the jaw drop (AU 27): a yell, aa." ),
				C( "Jaw", "jaw_clencher", "Clenches the jaw muscles.", stereo: false ),
				B( "Jaw", "jaw_sideways", "jaw_left", "jaw_right", "Slides the jaw to the character's left (−) or right (+)." ),
			},
		};

		rig.Presets["neutral"] = new();
		rig.Presets["tired"] = P( ("lid_droop", 0.55f), ("inner_raiser", 0.2f), ("corner_depressor", 0.15f), ("jaw_drop", 0.05f), ("part", 0.15f) );
		rig.Presets["smile"] = P( ("corner_puller", 0.8f), ("cheek_raiser", 0.45f), ("lid_tightener", 0.2f), ("part", 0.15f) );
		rig.Presets["frown"] = P( ("corner_depressor", 0.7f), ("lowerer", 0.55f), ("chin_raiser", 0.35f), ("tightener", 0.2f) );

		// s&box's 15 lip-sync visemes (OVRLipSync order), named as its lip-sync looks them up.
		rig.Presets["viseme_sil"] = new();
		rig.Presets["viseme_PP"] = P( ("presser", 1f), ("jaw_drop", 0.2f) );
		rig.Presets["viseme_FF"] = P( ("bite", 0.85f), ("upper_raiser", 0.15f) );
		rig.Presets["viseme_TH"] = P( ("jaw_drop", 0.15f), ("part", 0.4f) );
		rig.Presets["viseme_DD"] = P( ("jaw_drop", 0.2f), ("part", 0.3f), ("stretcher", 0.15f) );
		rig.Presets["viseme_KK"] = P( ("jaw_drop", 0.25f), ("part", 0.25f), ("stretcher", 0.2f) );
		rig.Presets["viseme_CH"] = P( ("funneler", 0.6f), ("jaw_drop", 0.1f), ("part", 0.3f) );
		rig.Presets["viseme_SS"] = P( ("stretcher", 0.35f), ("part", 0.3f), ("jaw_drop", 0.05f) );
		rig.Presets["viseme_NN"] = P( ("jaw_drop", 0.15f), ("part", 0.2f) );
		rig.Presets["viseme_RR"] = P( ("funneler", 0.35f), ("puckerer", 0.2f), ("jaw_drop", 0.1f) );
		rig.Presets["viseme_AA"] = P( ("jaw_drop", 0.8f), ("mouth_drop", 0.6f), ("part", 0.3f) );
		rig.Presets["viseme_E"] = P( ("jaw_drop", 0.35f), ("stretcher", 0.35f), ("corner_puller", 0.15f) );
		rig.Presets["viseme_I"] = P( ("part", 1f), ("funneler", 0.4f), ("stretcher", 0.78f), ("jaw_clencher", 0.55f), ("jaw_drop", 0.3f) );
		rig.Presets["viseme_O"] = P( ("jaw_drop", 0.4f), ("funneler", 0.65f) );
		rig.Presets["viseme_U"] = P( ("jaw_drop", 0.4f), ("puckerer", 0.9f) );
		return rig;
	}
}

/// <summary>
/// Landmarks a generated face rig is fitted to: the points a photo fit or a modeller picks
/// for the eyes, nose, mouth and jaw. The Has* flags say which regions exist; a zero point
/// with its flag false means "not present", not "at the origin". FromHeadBounds estimates
/// them from a head mesh's bounds (Y up, +Z forward) when no landmarks were picked: eyes at
/// 72% of the height and 18% of the width off centre, nose at 55%, mouth at 30%, jaw at 15%.
/// An estimate, not a measurement — it keeps every region, which is the safe default for a
/// head mesh that has all of them.
/// </summary>
public struct FaceBounds
{
	public Vec3 LeftEye, RightEye, NoseTip, MouthLeft, MouthRight, MouthCenter, Jaw;
	public bool HasEyes, HasNose, HasMouth, HasJaw;

	public static FaceBounds FromHeadBounds( Vec3 min, Vec3 max )
	{
		var w = max.x - min.x;
		var h = max.y - min.y;
		var cx = (min.x + max.x) / 2f;
		var front = max.z;

		return new FaceBounds
		{
			LeftEye = new Vec3( cx - 0.18f * w, min.y + 0.72f * h, front ),
			RightEye = new Vec3( cx + 0.18f * w, min.y + 0.72f * h, front ),
			NoseTip = new Vec3( cx, min.y + 0.55f * h, front ),
			MouthLeft = new Vec3( cx - 0.12f * w, min.y + 0.30f * h, front ),
			MouthRight = new Vec3( cx + 0.12f * w, min.y + 0.30f * h, front ),
			MouthCenter = new Vec3( cx, min.y + 0.30f * h, front ),
			Jaw = new Vec3( cx, min.y + 0.15f * h, front ),
			HasEyes = true,
			HasNose = true,
			HasMouth = true,
			HasJaw = true,
		};
	}
}
