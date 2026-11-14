using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Effigy;

namespace Effigy;

/// <summary>
/// Lane 9 (2D animation suite, Phase 2): Face Poser settings into clips.
///
/// Until now a Face Poser face could not be keyed into a clip at all: the
/// panel wrote morph weights onto the live viewport mesh and its presets
/// lived in sidecar files, while clips carried only Bone/Part/IkGoal/Camera/
/// IkWeight/Morph tracks and nothing ever wrote a Face Poser setting into a
/// Morph track. This file closes that gap, and nothing else.
///
/// Inputs are a <see cref="FaceRig"/> and plain controller settings — the same
/// shape Face Poser edits — NOT the FacePoserService instance. That service is
/// hot and read-only for this lane, and a caller that needs no pane is testable
/// headless. ServeMode wiring (`face_anim` or similar) is deferred to the
/// coordinator sweep; this file is the builder the wire will call.
///
/// A Morph track's value is the key's Scale.x
/// (<see cref="RigAnimFile.MorphWeightsAt(float)"/>), so a face key becomes one
/// Morph key per morph, per face keyframe. Every keyframe of one clip keys the
/// SAME morph set (the union of morphs that move anywhere in the clip), so a
/// later face that does not touch a morph keys it back to 0 instead of letting
/// the previous face bleed through — the classic lip-sync bleed bug.
///
/// Lip-sync is a SWITCH layer, not a blend: viseme keys use
/// <see cref="RigEase.Stepped"/>, one shape held until the next onset. One
/// Marionette quirk to know: RigTrack.Evaluate at the exact integer frame of a
/// Stepped key still returns the previous key (segment order), so a viseme
/// keyed at frame F is visible at F+0.5, not at exactly F. Tests sample
/// fractional frames for switch assertions for this reason.
///
/// No AI lip-sync engine lives here. Audio analysis is an IMPORT: a Rhubarb
/// tracking file (time + shape code) becomes Stepped viseme keys. If the
/// built-in code table does not match a rig's mouth shapes, pass a custom map;
/// never silently re-time or re-shape the cues.
/// </summary>
public static class FaceAnimCallers
{
	/// <summary>One face at one frame: whole controller settings, as Face Poser holds them.</summary>
	public sealed class FaceKeyframe
	{
		public int Frame;
		public Dictionary<string, FaceRig.Setting> Settings = new( StringComparer.OrdinalIgnoreCase );
		public RigEase Ease = RigEase.Smooth;
		public float Scale = 1;
	}

	/// <summary>One named FaceRig preset at one frame.</summary>
	public sealed class PresetStep
	{
		public int Frame;
		public string Preset = "";
		public RigEase Ease = RigEase.Smooth;
		public float Scale = 1;
	}

	/// <summary>One viseme onset at one frame (switch layer: Stepped).</summary>
	public sealed class VisemeKey
	{
		public int Frame;
		public string Viseme = "";
	}

	/// <summary>One parsed Rhubarb cue: seconds, the frame it lands on, and its shape code.</summary>
	public sealed class RhubarbCue
	{
		public double TimeSeconds;
		public int Frame;
		public string Code = "";
	}

	/// <summary>
	/// Simplified 10-viseme set (the lane brief's set): vowel shapes by name, and
	/// consonant groups by their members. Values are FaceRig preset names, so a
	/// rig whose sidecar renames a viseme still resolves through the preset
	/// lookup first (see <see cref="ResolveViseme"/>).
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> SimpleVisemes =
		new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			["A"] = "viseme_AA",
			["E"] = "viseme_E",
			["I"] = "viseme_I",
			["O"] = "viseme_O",
			["U"] = "viseme_U",
			["BMP"] = "viseme_PP",
			["FV"] = "viseme_FF",
			["LR"] = "viseme_RR",
			["DGHKN"] = "viseme_DD",
			["ST"] = "viseme_SS",
		};

	/// <summary>
	/// Single-letter aliases into the 10-viseme groups, so hand keys can say B,
	/// M, P, F, V, L, R, D, G, H, K, N, S or T instead of the group name.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> SimpleVisemeAliases =
		new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			["B"] = "BMP", ["M"] = "BMP", ["P"] = "BMP",
			["F"] = "FV", ["V"] = "FV",
			["L"] = "LR", ["R"] = "LR",
			["D"] = "DGHKN", ["G"] = "DGHKN", ["H"] = "DGHKN", ["K"] = "DGHKN", ["N"] = "DGHKN",
			["S"] = "ST", ["T"] = "ST",
		};

	/// <summary>
	/// Preston Blair / Papagayo mouth shapes. Where the default rig has no exact
	/// match the nearest shape is used and marked: L has no dedicated preset, so
	/// it uses viseme_RR (rounded, tongue-adjacent); WQ is a rounded shape, so it
	/// uses viseme_U (puckerer); "etc" (the Papagayo catch-all for ch/sh/th-ish
	/// shapes) uses viseme_CH. A rig with its own shapes should override these
	/// with a custom map rather than trusting the approximations.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> BlairVisemes =
		new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			["rest"] = "viseme_sil",
			["AI"] = "viseme_AA",
			["E"] = "viseme_E",
			["etc"] = "viseme_CH",
			["FV"] = "viseme_FF",
			["L"] = "viseme_RR",
			["MBP"] = "viseme_PP",
			["O"] = "viseme_O",
			["U"] = "viseme_U",
			["WQ"] = "viseme_U",
		};

	/// <summary>
	/// Rhubarb shape codes (A–H, X) to FaceRig presets. TRAP: Rhubarb letters
	/// are shape codes, NOT vowels — Rhubarb A is lips-pressed (PP), not vowel
	/// A (AA). This table follows the shape-to-blend mapping used by avatar
	/// pipelines over Rhubarb output (A=PP, B=KK, C=I, D=AA, E=O, F=U, G=FF,
	/// H=TH, X=sil); it is overridable per call because mouth-shape naming
	/// differs between rigs, and a wrong table here would silently mouth the
	/// wrong words.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> RhubarbCodes =
		new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			["A"] = "viseme_PP",
			["B"] = "viseme_KK",
			["C"] = "viseme_I",
			["D"] = "viseme_AA",
			["E"] = "viseme_O",
			["F"] = "viseme_U",
			["G"] = "viseme_FF",
			["H"] = "viseme_TH",
			["X"] = "viseme_sil",
		};

	/// <summary>
	/// Face keyframes into a Morph-track clip. One track per morph that moves
	/// anywhere in the clip; every keyframe keys every such morph (0 where the
	/// face does not set it), so faces never bleed into each other.
	/// </summary>
	public static RigAnimFile BuildFaceClip( FaceRig rig, IReadOnlyList<FaceKeyframe> keys, int frameRate = 30, string sourceModel = "" )
	{
		if ( rig is null ) throw new ArgumentNullException( nameof( rig ) );
		if ( keys is null || keys.Count == 0 ) throw new ArgumentException( "face clip needs at least one keyframe." );
		if ( frameRate <= 0 ) throw new ArgumentException( "face clip needs a frame rate above 0." );

		var ordered = keys.OrderBy( k => k.Frame ).ToList();
		for ( var i = 1; i < ordered.Count; i++ )
			if ( ordered[i].Frame == ordered[i - 1].Frame )
				throw new ArgumentException( $"two face keyframes on frame {ordered[i].Frame}; one face per frame." );
		foreach ( var k in ordered )
			if ( k.Frame < 0 ) throw new ArgumentException( "face keyframes need frames at or above 0." );

		var snapshots = ordered.Select( k => rig.Evaluate( k.Settings ?? new Dictionary<string, FaceRig.Setting>(), k.Scale ) ).ToList();
		var moving = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var snap in snapshots )
			foreach ( var (morph, value) in snap )
				if ( Math.Abs( value ) > 1e-4f ) moving.Add( morph );

		var clip = new RigAnimFile { SourceModel = sourceModel ?? "", FrameRate = frameRate, FrameCount = ordered[^1].Frame + 1 };
		foreach ( var morph in moving.OrderBy( m => m, StringComparer.Ordinal ) )
		{
			var track = new RigTrack { Kind = RigTrackKind.Morph, Name = morph };
			for ( var i = 0; i < ordered.Count; i++ )
				track.Keys.Add( new RigKey
				{
					Frame = ordered[i].Frame,
					Ease = ordered[i].Ease,
					Local = new RigPose( Vec3.Zero, Quat.Identity, new Vec3( snapshots[i].TryGetValue( morph, out var v ) ? v : 0, 1, 1 ) ),
				} );
			clip.Tracks.Add( track );
		}
		return clip;
	}

	/// <summary>
	/// Named FaceRig presets over time into a Morph-track clip. An unknown
	/// preset fails loudly with the presets that do exist; "neutral" (or any
	/// preset with no settings) keys every moving morph to 0, which is what
	/// returns a face to rest instead of holding the previous expression.
	/// </summary>
	public static RigAnimFile BuildPresetClip( FaceRig rig, IReadOnlyList<PresetStep> steps, int frameRate = 30, string sourceModel = "" )
	{
		if ( rig is null ) throw new ArgumentNullException( nameof( rig ) );
		if ( steps is null || steps.Count == 0 ) throw new ArgumentException( "preset clip needs at least one step." );

		var keys = steps.Select( s =>
		{
			if ( string.IsNullOrWhiteSpace( s.Preset ) ) throw new ArgumentException( "preset step needs a preset name." );
			var name = rig.Presets.Keys.FirstOrDefault( k => string.Equals( k, s.Preset, StringComparison.OrdinalIgnoreCase ) )
				?? throw new ArgumentException( $"No preset '{s.Preset}'. Presets: {string.Join( ", ", rig.Presets.Keys )}." );
			return new FaceKeyframe
			{
				Frame = s.Frame,
				Settings = new Dictionary<string, FaceRig.Setting>( rig.Presets[name], StringComparer.OrdinalIgnoreCase ),
				Ease = s.Ease,
				Scale = s.Scale,
			};
		} ).ToList();
		return BuildFaceClip( rig, keys, frameRate, sourceModel );
	}

	/// <summary>
	/// Resolve a hand-keyed viseme name to a FaceRig preset name, in order:
	/// custom map, exact preset name, 10-viseme set (plus single-letter
	/// aliases), Preston Blair set. Anything else fails loudly.
	/// </summary>
	public static string ResolveViseme( FaceRig rig, string viseme, IReadOnlyDictionary<string, string> customMap = null )
	{
		if ( rig is null ) throw new ArgumentNullException( nameof( rig ) );
		if ( string.IsNullOrWhiteSpace( viseme ) ) throw new ArgumentException( "viseme needs a name." );

		if ( customMap is not null && customMap.TryGetValue( viseme, out var custom ) && !string.IsNullOrWhiteSpace( custom ) )
			return RequirePreset( rig, custom, viseme );

		var exact = rig.Presets.Keys.FirstOrDefault( k => string.Equals( k, viseme, StringComparison.OrdinalIgnoreCase ) );
		if ( exact is not null ) return exact;

		if ( SimpleVisemes.TryGetValue( viseme, out var simple ) ) return RequirePreset( rig, simple, viseme );
		if ( SimpleVisemeAliases.TryGetValue( viseme, out var group ) && SimpleVisemes.TryGetValue( group, out simple ) )
			return RequirePreset( rig, simple, viseme );
		if ( BlairVisemes.TryGetValue( viseme, out var blair ) ) return RequirePreset( rig, blair, viseme );

		throw new ArgumentException( $"No viseme '{viseme}'. Presets: {string.Join( ", ", rig.Presets.Keys )}. " +
			$"10-viseme: {string.Join( ", ", SimpleVisemes.Keys )}. Blair: {string.Join( ", ", BlairVisemes.Keys )}." );
	}

	static string RequirePreset( FaceRig rig, string preset, string asked ) =>
		rig.Presets.Keys.FirstOrDefault( k => string.Equals( k, preset, StringComparison.OrdinalIgnoreCase ) )
		?? throw new ArgumentException( $"Viseme '{asked}' maps to preset '{preset}', which this rig has no preset for. Presets: {string.Join( ", ", rig.Presets.Keys )}." );

	/// <summary>
	/// Viseme onsets into a Stepped switch-layer clip. <paramref name="leadFrames"/>
	/// pulls every onset earlier by that many frames (coarticulation lead: the
	/// mouth starts shaping before the sound), clamped at frame 0; two onsets
	/// that collide after the shift keep the later viseme, since it is the one
	/// the ear hears at that point.
	/// </summary>
	public static RigAnimFile BuildVisemeClip( FaceRig rig, IReadOnlyList<VisemeKey> keys, int frameRate = 30, int leadFrames = 0, IReadOnlyDictionary<string, string> customMap = null, string sourceModel = "" )
	{
		if ( rig is null ) throw new ArgumentNullException( nameof( rig ) );
		if ( keys is null || keys.Count == 0 ) throw new ArgumentException( "viseme clip needs at least one key." );
		if ( leadFrames < 0 ) throw new ArgumentException( "lead frames cannot be negative." );

		var shifted = keys
			.Select( k => (Frame: Math.Max( 0, k.Frame - leadFrames ), k.Viseme) )
			.GroupBy( k => k.Frame )
			.Select( g => g.Last() )
			.OrderBy( k => k.Frame )
			.ToList();

		var steps = shifted.Select( k => new PresetStep
		{
			Frame = k.Frame,
			Preset = ResolveViseme( rig, k.Viseme, customMap ),
			Ease = RigEase.Stepped,
		} ).ToList();
		return BuildPresetClip( rig, steps, frameRate, sourceModel );
	}

	/// <summary>
	/// Rhubarb TSV: one cue per line, "SECONDS TAB CODE" (spaces also accepted),
	/// the format Rhubarb writes with -f tsv. First cue is always 0.00; the last
	/// line is the recording end and still becomes a key, so a trailing rest
	/// closes the mouth instead of holding the last shape.
	/// </summary>
	public static List<RhubarbCue> ParseRhubarbTsv( string text, int frameRate = 30 )
	{
		if ( text is null ) throw new ArgumentNullException( nameof( text ) );
		if ( frameRate <= 0 ) throw new ArgumentException( "Rhubarb import needs a frame rate above 0." );

		var cues = new List<RhubarbCue>();
		foreach ( var raw in text.Split( '\n' ) )
		{
			var line = raw.Trim( '\r', ' ', '\t' );
			if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
			var parts = line.Split( new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length != 2 || !double.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds ) )
				throw new ArgumentException( $"Rhubarb TSV line is not 'seconds code': '{line}'." );
			if ( seconds < 0 ) throw new ArgumentException( $"Rhubarb TSV time cannot be negative: '{line}'." );
			cues.Add( new RhubarbCue { TimeSeconds = seconds, Frame = (int)Math.Round( seconds * frameRate ), Code = parts[1] } );
		}
		if ( cues.Count == 0 ) throw new ArgumentException( "Rhubarb TSV has no cues." );
		return cues;
	}

	/// <summary>
	/// Frame-based tracking sidecar (.dat): one cue per line, "FRAME CODE"
	/// (e.g. "12 D") — the form a TSV converts to when sampled to frames, and
	/// the form a hand-written sidecar takes. This is NOT the full Papagayo
	/// phrase/word hierarchy; flatten a Papagayo .dat to frame cues first.
	/// </summary>
	public static List<RhubarbCue> ParseRhubarbDat( string text, int frameRate = 30 )
	{
		if ( text is null ) throw new ArgumentNullException( nameof( text ) );
		if ( frameRate <= 0 ) throw new ArgumentException( "Rhubarb import needs a frame rate above 0." );

		var cues = new List<RhubarbCue>();
		foreach ( var raw in text.Split( '\n' ) )
		{
			var line = raw.Trim( '\r', ' ', '\t' );
			if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
			var parts = line.Split( new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length != 2 || !int.TryParse( parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame ) )
				throw new ArgumentException( $"Rhubarb .dat line is not 'frame code': '{line}'." );
			if ( frame < 0 ) throw new ArgumentException( $"Rhubarb .dat frame cannot be negative: '{line}'." );
			cues.Add( new RhubarbCue { TimeSeconds = frame / (double)frameRate, Frame = frame, Code = parts[1] } );
		}
		if ( cues.Count == 0 ) throw new ArgumentException( "Rhubarb .dat has no cues." );
		return cues;
	}

	/// <summary>
	/// A Rhubarb tracking file into a Stepped viseme clip. Rhubarb codes resolve
	/// through <see cref="RhubarbCodes"/> (or the custom map, which wins); a
	/// code with no mapping fails loudly instead of mouthing silence as a
	/// shape. TSV cues are already sorted by Rhubarb; .dat cues are sorted here.
	/// </summary>
	public static RigAnimFile BuildRhubarbClip( FaceRig rig, string text, int frameRate = 30, bool fromDat = false, int leadFrames = 0, IReadOnlyDictionary<string, string> customMap = null, string sourceModel = "" )
	{
		if ( rig is null ) throw new ArgumentNullException( nameof( rig ) );
		var cues = (fromDat ? ParseRhubarbDat( text, frameRate ) : ParseRhubarbTsv( text, frameRate ))
			.OrderBy( c => c.Frame ).ToList();

		var keys = cues.Select( c => new VisemeKey { Frame = c.Frame, Viseme = ResolveRhubarbCode( rig, c.Code, customMap ) } ).ToList();
		// Viseme keys are already preset names, so resolve them directly: build
		// the Stepped preset clip without re-running the viseme tables.
		var shifted = keys
			.Select( k => (Frame: Math.Max( 0, k.Frame - leadFrames ), k.Viseme) )
			.GroupBy( k => k.Frame )
			.Select( g => g.Last() )
			.OrderBy( k => k.Frame )
			.ToList();
		var steps = shifted.Select( k => new PresetStep { Frame = k.Frame, Preset = k.Viseme, Ease = RigEase.Stepped } ).ToList();
		return BuildPresetClip( rig, steps, frameRate, sourceModel );
	}

	static string ResolveRhubarbCode( FaceRig rig, string code, IReadOnlyDictionary<string, string> customMap )
	{
		if ( customMap is not null && customMap.TryGetValue( code, out var custom ) && !string.IsNullOrWhiteSpace( custom ) )
			return RequirePreset( rig, custom, code );
		if ( RhubarbCodes.TryGetValue( code, out var preset ) )
			return RequirePreset( rig, preset, code );
		var exact = rig.Presets.Keys.FirstOrDefault( k => string.Equals( k, code, StringComparison.OrdinalIgnoreCase ) );
		if ( exact is not null ) return exact;
		throw new ArgumentException( $"No Rhubarb code '{code}'. Codes: {string.Join( ", ", RhubarbCodes.Keys )}. Presets: {string.Join( ", ", rig.Presets.Keys )}." );
	}
}
