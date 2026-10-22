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

	/// <summary>Every key the full rig expects, for a sculpting checklist.</summary>
	public IEnumerable<string> ExpectedKeys() => Controllers.SelectMany( c => c.Keys() ).Distinct();

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
