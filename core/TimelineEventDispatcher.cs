#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Type of event notification emitted when the timeline playhead crosses a mark or event boundary.
/// </summary>
public enum TimelineEventKind
{
	/// <summary>Instantaneous timeline mark / cue point.</summary>
	Mark,

	/// <summary>Prop attach or timeline event triggered at start frame.</summary>
	Event,

	/// <summary>Explicit alias for EventStart.</summary>
	EventStart = Event,

	/// <summary>Prop detach or timeline event ended at end frame.</summary>
	EventEnd,

	/// <summary>Morph target trigger event at start frame.</summary>
	MorphEvent,

	/// <summary>Explicit alias for MorphEventStart.</summary>
	MorphEventStart = MorphEvent,

	/// <summary>Morph target event ending at end frame.</summary>
	MorphEventEnd,

	/// <summary>
	/// Instantaneous sound cue at its start frame. Appended, never inserted, so every
	/// earlier kind keeps its number.
	/// </summary>
	Sound
}

/// <summary>
/// Which authority realm owns spawning this event's prop.
/// </summary>
public enum RigEventSide
{
	Client,
	Server
}

/// <summary>
/// A named frame on the clip's timeline (note/cue point with no procedural prop attachment).
/// </summary>
public sealed class RigMark
{
	public string Name { get; set; } = "mark";
	public int Frame { get; set; }
	public string Note { get; set; } = "";

	public RigMark() { }

	public RigMark( string name, int frame, string note = "" )
	{
		Name = name;
		Frame = frame;
		Note = note;
	}

	public override string ToString() => $"RigMark('{Name}', Frame={Frame})";
}

/// <summary>
/// Prop attachment or timeline action active over a frame window [StartFrame, EndFrame].
/// </summary>
public sealed class RigEvent
{
	public string Name { get; set; } = "event";
	public int StartFrame { get; set; }
	public int EndFrame { get; set; } = 1;
	public RigEventSide Side { get; set; } = RigEventSide.Client;
	public string PropModel { get; set; } = "";
	public string AttachBone { get; set; } = "hand_L";
	public bool FollowBone { get; set; } = true;
	public Vec3 PositionOffset { get; set; }
	public Quat RotationOffset { get; set; } = Quat.Identity;
	public float ScaleOffset { get; set; } = 1f;

	public bool IsActive( float frame ) => frame >= StartFrame && frame <= EndFrame;

	public override string ToString() => $"RigEvent('{Name}', [{StartFrame}..{EndFrame}], Bone='{AttachBone}')";
}

/// <summary>
/// An instantaneous sound cue on the timeline: a sound path or event name fired once when
/// the playhead crosses <see cref="StartFrame"/>, at <see cref="Volume"/>, from
/// <see cref="BoneOrigin"/> (empty means the character root, the usual case for a voice or
/// body sound; a hand or mouth bone for a held prop or a footstep).
///
/// A cue, not a sample player: the dispatcher says WHEN, the game's audio system decides HOW.
/// That split is why this type carries a path and not a waveform — the desktop preview has
/// no audio output to play through, and the runtime player fires the same cue through its own
/// sound system instead of guessing at one here.
/// </summary>
public sealed class RigSoundEvent
{
	public string Name { get; set; } = "sound";
	public int StartFrame { get; set; }
	public int EndFrame { get; set; }
	public string SoundPath { get; set; } = "";
	public float Volume { get; set; } = 1f;
	public string BoneOrigin { get; set; } = "";

	public RigSoundEvent() { }

	public RigSoundEvent( string name, int startFrame, string soundPath, float volume = 1f, string boneOrigin = "" )
	{
		Name = name;
		StartFrame = startFrame;
		EndFrame = startFrame;
		SoundPath = soundPath;
		Volume = volume;
		BoneOrigin = boneOrigin;
	}

	public bool IsActive( float frame ) => frame >= StartFrame && frame <= (EndFrame > 0 ? EndFrame : StartFrame);

	public override string ToString() => $"RigSoundEvent('{Name}', Frame={StartFrame}, Path='{SoundPath}')";
}

/// <summary>
/// Lip-sync from phoneme cues: each cue names a phoneme over a frame range, and
/// <see cref="Sample"/> turns the cues live at a frame into <c>viseme_*</c> morph weights —
/// the morph names the face rig already uses, so no new morph target is invented here.
///
/// ENVELOPE. A cue ramps in over its first frame and out over its last (the same smoothstep
/// a morph event uses), holds at its weight between, and when two cues map to the same
/// viseme the stronger wins rather than adding — two mouth shapes do not stack, the mouth
/// is one shape at a time. An optional per-frame amplitude envelope (from
/// <see cref="WavEnvelope"/>) scales every weight, so a whispered line moves the mouth less
/// than a shouted one without re-authoring the cues. Frames past the envelope's end read
/// its last value; a null envelope means full amplitude.
/// </summary>
public sealed class AudioVisemeTrack
{
	/// <summary>One phoneme held over [StartFrame, EndFrame] at Weight.</summary>
	public sealed record Cue( string Phoneme, int StartFrame, int EndFrame, float Weight = 1f );

	/// <summary>ARPAbet-style phonemes to the viseme morph they drive. Unknown phonemes map to
	/// nothing and are skipped by Sample, rather than driving a mouth shape nobody asked for.</summary>
	public static readonly IReadOnlyDictionary<string, string> PhonemeToViseme =
		new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			["AA"] = "viseme_AA", ["AH"] = "viseme_AA", ["AO"] = "viseme_AA",
			["EH"] = "viseme_E", ["AE"] = "viseme_E",
			["IH"] = "viseme_I", ["IY"] = "viseme_I",
			["OW"] = "viseme_O", ["OH"] = "viseme_O", ["OO"] = "viseme_O", ["UW"] = "viseme_O",
			["UH"] = "viseme_U",
			["P"] = "viseme_PP", ["B"] = "viseme_PP", ["M"] = "viseme_PP",
			["F"] = "viseme_FF", ["V"] = "viseme_FF",
			["TH"] = "viseme_TH", ["DH"] = "viseme_TH",
			["T"] = "viseme_DD", ["D"] = "viseme_DD", ["N"] = "viseme_DD", ["L"] = "viseme_DD",
			["K"] = "viseme_kk", ["G"] = "viseme_kk",
			["CH"] = "viseme_CH", ["JH"] = "viseme_CH", ["SH"] = "viseme_CH", ["ZH"] = "viseme_CH",
			["S"] = "viseme_SS", ["Z"] = "viseme_SS",
			["NG"] = "viseme_nn",
			["R"] = "viseme_RR",
			["SIL"] = "viseme_sil", ["SP"] = "viseme_sil", ["PAU"] = "viseme_sil",
		};

	/// <summary>Map one phoneme to its viseme morph, or "" when it has none.</summary>
	public static string MapPhoneme( string phoneme ) =>
		string.IsNullOrWhiteSpace( phoneme ) ? "" :
		PhonemeToViseme.TryGetValue( phoneme.Trim(), out var viseme ) ? viseme : "";

	public List<Cue> Cues { get; } = new();

	/// <summary>Per-frame amplitude, one value per frame, 0..1; null means full amplitude.</summary>
	public float[]? AmplitudeEnvelope { get; set; }

	public void AddCue( string phoneme, int startFrame, int endFrame, float weight = 1f ) =>
		Cues.Add( new Cue( phoneme, startFrame, endFrame, weight ) );

	/// <summary>Viseme morph weights at a frame: viseme name to 0..1 weight. Empty when silent.</summary>
	public Dictionary<string, float> Sample( float frame )
	{
		var weights = new Dictionary<string, float>( StringComparer.Ordinal );
		var amplitude = AmplitudeAt( frame );

		foreach ( var cue in Cues )
		{
			var viseme = MapPhoneme( cue.Phoneme );
			if ( viseme.Length == 0 || cue.Weight <= 0f ) continue;

			var end = Math.Max( cue.EndFrame, cue.StartFrame );
			if ( frame < cue.StartFrame || frame > end ) continue;

			// One-frame smoothstep in and out; a one-frame cue is at full weight on its frame.
			var up = end == cue.StartFrame ? 1f : Smooth( (frame - cue.StartFrame) / 1f );
			var down = end == cue.StartFrame ? 1f : Smooth( (end - frame) / 1f );
			var value = cue.Weight * MathF.Min( up, down ) * amplitude;

			if ( !weights.TryGetValue( viseme, out var existing ) || value > existing )
				weights[viseme] = value;
		}

		return weights;
	}

	float AmplitudeAt( float frame )
	{
		if ( AmplitudeEnvelope is null || AmplitudeEnvelope.Length == 0 ) return 1f;
		var index = (int)MathF.Floor( frame );
		if ( index < 0 ) return AmplitudeEnvelope[0];
		return AmplitudeEnvelope[Math.Min( index, AmplitudeEnvelope.Length - 1 )];
	}

	static float Smooth( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
	}
}

/// <summary>
/// A WAV file's loudness over time, as one RMS value per timeline frame, normalised so the
/// loudest frame is 1. What <see cref="AudioVisemeTrack.AmplitudeEnvelope"/> eats: the line's
/// dynamics without re-authoring its phonemes.
///
/// Only plain RIFF/WAVE is read: PCM integer 8/16/24/32-bit and IEEE float 32/64-bit, any
/// channel count (mixed down by averaging). Anything else — ADPCM, an MP3 in a .wav coat, a
/// truncated data chunk — throws <see cref="FormatException"/> instead of guessing, because
/// a silently wrong envelope moves the mouth to the wrong line.
/// </summary>
public static class WavEnvelope
{
	public static float[] Read( byte[] wav, float frameRate )
	{
		if ( wav is null ) throw new ArgumentNullException( nameof( wav ) );
		if ( frameRate <= 0f ) throw new ArgumentOutOfRangeException( nameof( frameRate ) );
		if ( wav.Length < 44 || Text( wav, 0, 4 ) != "RIFF" || Text( wav, 8, 4 ) != "WAVE" )
			throw new FormatException( "not a RIFF/WAVE file" );

		int format = 0, channels = 0, sampleRate = 0, bits = 0;
		int dataAt = -1, dataLength = 0;

		for ( int at = 12; at + 8 <= wav.Length; )
		{
			var id = Text( wav, at, 4 );
			var size = BitConverter.ToInt32( wav, at + 4 );
			if ( size < 0 || at + 8 + size > wav.Length ) break;
			var body = at + 8;

			if ( id == "fmt " && size >= 16 )
			{
				format = BitConverter.ToUInt16( wav, body );
				channels = BitConverter.ToUInt16( wav, body + 2 );
				sampleRate = BitConverter.ToInt32( wav, body + 4 );
				bits = BitConverter.ToUInt16( wav, body + 14 );
			}
			else if ( id == "data" )
			{
				dataAt = body;
				dataLength = size;
			}

			at = body + size + (size % 2);
		}

		if ( dataAt < 0 ) throw new FormatException( "WAVE file has no data chunk" );
		if ( format != 1 && format != 3 ) throw new FormatException( $"WAVE format {format} is not PCM or IEEE float" );
		if ( channels <= 0 || sampleRate <= 0 ) throw new FormatException( "WAVE file has no channels or no sample rate" );
		if ( format == 1 && bits != 8 && bits != 16 && bits != 24 && bits != 32 ) throw new FormatException( $"PCM bit depth {bits} is not supported" );
		if ( format == 3 && bits != 32 && bits != 64 ) throw new FormatException( $"float bit depth {bits} is not supported" );

		var bytesPerSample = bits / 8;
		var totalFrames = (int)Math.Ceiling( (dataLength / (double)(bytesPerSample * channels)) / (sampleRate / (double)frameRate) );
		var envelope = new float[Math.Max( 1, totalFrames )];
		var samplesPerFrame = sampleRate / frameRate;

		for ( int f = 0; f < envelope.Length; f++ )
		{
			var start = (int)Math.Floor( f * samplesPerFrame );
			var end = Math.Min( (int)Math.Floor( (f + 1) * samplesPerFrame ), dataLength / (bytesPerSample * channels) );
			if ( end <= start ) { envelope[f] = 0f; continue; }

			double sum = 0;
			for ( int s = start; s < end; s++ )
			{
				double mixed = 0;
				for ( int c = 0; c < channels; c++ )
					mixed += Sample( wav, dataAt + (s * channels + c) * bytesPerSample, format, bits );
				mixed /= channels;
				sum += mixed * mixed;
			}
			envelope[f] = (float)Math.Sqrt( sum / (end - start ) );
		}

		var peak = envelope.Length > 0 ? envelope.Max() : 0f;
		if ( peak > 0f )
			for ( int i = 0; i < envelope.Length; i++ ) envelope[i] /= peak;

		return envelope;
	}

	static double Sample( byte[] wav, int at, int format, int bits )
	{
		if ( format == 3 )
			return bits == 32 ? BitConverter.ToSingle( wav, at ) : BitConverter.ToDouble( wav, at );

		return bits switch
		{
			8 => (wav[at] - 128) / 128.0,
			16 => BitConverter.ToInt16( wav, at ) / 32768.0,
			24 => (wav[at] | (wav[at + 1] << 8) | (wav[at + 2] << 16)) switch
			{
				var v => (v >= 0x800000 ? v - 0x1000000 : v) / 8388608.0,
			},
			_ => BitConverter.ToInt32( wav, at ) / 2147483648.0,
		};
	}

	static string Text( byte[] wav, int at, int length ) =>
		System.Text.Encoding.ASCII.GetString( wav, at, length );
}

/// <summary>
/// Strongly typed notification produced when a timeline mark or event boundary is crossed.
/// </summary>
public sealed record TimelineEventNotification
{
	public TimelineEventKind Kind { get; init; }
	public string Name { get; init; } = "";
	public float Frame { get; init; }
	public int IntegerFrame => (int)MathF.Round( Frame );

	public RigMark? Mark { get; init; }
	public RigEvent? Event { get; init; }
	public RigMorphEvent? MorphEvent { get; init; }
	public RigSoundEvent? SoundEvent { get; init; }

	public bool IsReplay { get; init; }
	public float PlayheadTime { get; init; }
	public int LoopIteration { get; init; }
	public long SequenceNumber { get; init; }

	public override string ToString() =>
		$"TimelineEventNotification({Kind}, '{Name}', Frame={Frame:F2}, Loop={LoopIteration}, Replay={IsReplay})";
}

/// <summary>
/// Encapsulates an exception caught from a consumer callback during event dispatch.
/// </summary>
public sealed class TimelineEventException : Exception
{
	public TimelineEventNotification Notification { get; }
	public Exception ConsumerException => InnerException!;

	public TimelineEventException( Exception inner, TimelineEventNotification notification )
		: base( $"Consumer exception during event notification '{notification.Name}' at frame {notification.Frame:F2}: {inner.Message}", inner )
	{
		Notification = notification;
	}
}

/// <summary>
/// Extension methods allowing marks and events to be attached to <see cref="RigAnimFile"/> instances.
/// </summary>
public static class RigAnimFileTimelineExtensions
{
	static readonly ConditionalWeakTable<RigAnimFile, List<RigMark>> _marksTable = new();
	static readonly ConditionalWeakTable<RigAnimFile, List<RigEvent>> _eventsTable = new();

	public static List<RigMark> GetMarks( this RigAnimFile clip ) =>
		_marksTable.GetOrCreateValue( clip );

	public static void SetMarks( this RigAnimFile clip, IEnumerable<RigMark> marks )
	{
		var list = _marksTable.GetOrCreateValue( clip );
		list.Clear();
		if ( marks is not null ) list.AddRange( marks );
	}

	public static void AddMark( this RigAnimFile clip, RigMark mark )
	{
		ArgumentNullException.ThrowIfNull( mark );
		_marksTable.GetOrCreateValue( clip ).Add( mark );
	}

	public static List<RigEvent> GetEvents( this RigAnimFile clip ) =>
		_eventsTable.GetOrCreateValue( clip );

	public static void SetEvents( this RigAnimFile clip, IEnumerable<RigEvent> events )
	{
		var list = _eventsTable.GetOrCreateValue( clip );
		list.Clear();
		if ( events is not null ) list.AddRange( events );
	}

	public static void AddEvent( this RigAnimFile clip, RigEvent evt )
	{
		ArgumentNullException.ThrowIfNull( evt );
		_eventsTable.GetOrCreateValue( clip ).Add( evt );
	}
}

/// <summary>
/// Deterministic timeline event dispatcher for animation playback and scrubbing.
/// Dispatches crossings over half-open interval (previousFrame, currentFrame],
/// handles loop wrapping cleanly without dropping or duplicating events, enforces
/// scrub/seek suppression policies, preserves stable same-frame ordering, and
/// catches consumer exceptions without corrupting playback transport.
/// </summary>
public sealed class TimelineEventDispatcher
{
	sealed record DispatchItem(
		TimelineEventKind Kind,
		string Name,
		float Frame,
		long Sequence,
		RigMark? Mark = null,
		RigEvent? Event = null,
		RigMorphEvent? MorphEvent = null,
		RigSoundEvent? SoundEvent = null
	);

	readonly List<RigMark> _marks = new();
	readonly List<RigEvent> _events = new();
	readonly List<RigMorphEvent> _morphEvents = new();
	readonly List<RigSoundEvent> _soundEvents = new();
	readonly List<DispatchItem> _items = new();
	readonly List<TimelineEventException> _errors = new();

	long _sequenceCounter;
	bool _itemsDirty;
	bool _fromStart;
	float? _previousFrame;
	int? _explicitEndFrame;

	public RigAnimFile? Clip { get; private set; }
	public float FrameRate { get; set; } = 30f;
	public int FrameCount { get; set; } = 1;
	public bool Loop { get; set; } = true;
	public bool IsPlaying { get; set; }
	public float CurrentFrame { get; set; }

	public float CurrentTime
	{
		get => FrameRate > 0 ? CurrentFrame / FrameRate : 0f;
		set => CurrentFrame = value * FrameRate;
	}

	public int ClipEndFrame
	{
		get
		{
			if ( _explicitEndFrame.HasValue ) return _explicitEndFrame.Value;
			if ( Clip is not null )
			{
				int clipFrames = Math.Max( Clip.FrameCount - 1, Clip.LastFrame );
				return Math.Max( 0, clipFrames );
			}
			return Math.Max( 0, FrameCount - 1 );
		}
		set => _explicitEndFrame = Math.Max( 0, value );
	}

	/// <summary>Maximum number of full loop iterations allowed in a single huge step to prevent thread starvation.</summary>
	public int MaxLoopIterationsPerStep { get; set; } = 1000;

	public IReadOnlyList<RigMark> Marks => _marks;
	public IReadOnlyList<RigEvent> Events => _events;
	public IReadOnlyList<RigMorphEvent> MorphEvents => _morphEvents;
	public IReadOnlyList<RigSoundEvent> SoundEvents => _soundEvents;
	public IReadOnlyList<TimelineEventException> Errors => _errors;

	/// <summary>Fired when an event notification is dispatched.</summary>
	public event Action<TimelineEventNotification>? NotificationDispatched;

	/// <summary>Fired when a consumer callback throws an exception during dispatch.</summary>
	public event Action<TimelineEventException>? DispatchError;

	public Action<TimelineEventNotification>? OnNotification { get; set; }
	public Action<TimelineEventException>? OnError { get; set; }

	public TimelineEventDispatcher() { }

	public TimelineEventDispatcher( RigAnimFile clip, string? sourceJson = null )
	{
		LoadClip( clip, sourceJson );
	}

	public void Clear()
	{
		_marks.Clear();
		_events.Clear();
		_morphEvents.Clear();
		_soundEvents.Clear();
		_items.Clear();
		_errors.Clear();
		_sequenceCounter = 0;
		_itemsDirty = false;
		Clip = null;
		CurrentFrame = 0f;
		_previousFrame = null;
		_fromStart = false;
		_explicitEndFrame = null;
	}

	public void AddMark( RigMark mark )
	{
		ArgumentNullException.ThrowIfNull( mark );
		_marks.Add( mark );
		_itemsDirty = true;
	}

	public void AddMark( string name, int frame, string note = "" ) =>
		AddMark( new RigMark( name, frame, note ) );

	public void AddEvent( RigEvent evt )
	{
		ArgumentNullException.ThrowIfNull( evt );
		_events.Add( evt );
		_itemsDirty = true;
	}

	public void AddMorphEvent( RigMorphEvent morphEvent )
	{
		ArgumentNullException.ThrowIfNull( morphEvent );
		_morphEvents.Add( morphEvent );
		_itemsDirty = true;
	}

	public void AddSoundEvent( RigSoundEvent soundEvent )
	{
		ArgumentNullException.ThrowIfNull( soundEvent );
		_soundEvents.Add( soundEvent );
		_itemsDirty = true;
	}

	/// <summary>
	/// Load an animation clip, inspecting its morph events, attached marks/events,
	/// and optional source JSON containing Marks and Events arrays.
	/// Does NOT fire playback events during loading. Resets playhead to 0.
	/// </summary>
	public void LoadClip( RigAnimFile clip, string? sourceJson = null )
	{
		ArgumentNullException.ThrowIfNull( clip );
		Clear();
		Clip = clip;
		FrameRate = Math.Max( 1f, clip.FrameRate );
		FrameCount = Math.Max( 1, clip.FrameCount );

		if ( !string.IsNullOrWhiteSpace( sourceJson ) )
		{
			ParseJsonPayload( sourceJson );
		}

		// Also check extension marks & events attached directly to RigAnimFile
		var extMarks = clip.GetMarks();
		foreach ( var m in extMarks )
			if ( !_marks.Any( existing => existing.Name == m.Name && existing.Frame == m.Frame ) ) _marks.Add( m );

		var extEvents = clip.GetEvents();
		foreach ( var e in extEvents )
			if ( !_events.Any( existing => existing.Name == e.Name && existing.StartFrame == e.StartFrame && existing.EndFrame == e.EndFrame ) ) _events.Add( e );

		// RigAnimFile native morph events
		if ( clip.MorphEvents is not null )
		{
			foreach ( var me in clip.MorphEvents )
				if ( !_morphEvents.Any( existing => existing.Morph == me.Morph && existing.Start == me.Start && existing.End == me.End ) ) _morphEvents.Add( me );
		}

		_itemsDirty = true;
		_fromStart = true;
		_previousFrame = null;
		CurrentFrame = 0f;
	}

	/// <summary>
	/// Load marks, events, and morph events directly from animation JSON text.
	/// </summary>
	public void LoadJson( string json )
	{
		ArgumentNullException.ThrowIfNull( json );
		Clear();
		ParseJsonPayload( json );
		_itemsDirty = true;
		_fromStart = true;
		_previousFrame = null;
		CurrentFrame = 0f;
	}

	void ParseJsonPayload( string json )
	{
		using var doc = JsonDocument.Parse( json.TrimStart( '\uFEFF' ) );
		var root = doc.RootElement;

		if ( root.TryGetProperty( "AnimationSpeed", out var speed ) && speed.ValueKind == JsonValueKind.Number )
			FrameRate = Math.Max( 1f, speed.GetSingle() );

		if ( root.TryGetProperty( "FrameCount", out var count ) && count.ValueKind == JsonValueKind.Number )
			FrameCount = Math.Max( 1, count.GetInt32() );

		if ( root.TryGetProperty( "Marks", out var marksProp ) && marksProp.ValueKind == JsonValueKind.Array )
		{
			foreach ( var m in marksProp.EnumerateArray() )
			{
				string name = m.TryGetProperty( "Name", out var n ) ? n.GetString() ?? "mark" : "mark";
				int frame = m.TryGetProperty( "Frame", out var f ) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
				string note = m.TryGetProperty( "Note", out var nt ) ? nt.GetString() ?? "" : "";
				_marks.Add( new RigMark( name, frame, note ) );
			}
		}

		if ( root.TryGetProperty( "Events", out var eventsProp ) && eventsProp.ValueKind == JsonValueKind.Array )
		{
			foreach ( var e in eventsProp.EnumerateArray() )
			{
				string name = e.TryGetProperty( "Name", out var n ) ? n.GetString() ?? "event" : "event";
				int start = e.TryGetProperty( "StartFrame", out var sf ) && sf.ValueKind == JsonValueKind.Number ? sf.GetInt32() : 0;
				int end = e.TryGetProperty( "EndFrame", out var ef ) && ef.ValueKind == JsonValueKind.Number ? ef.GetInt32() : 1;
				var side = RigEventSide.Client;
				if ( e.TryGetProperty( "Side", out var sp ) )
				{
					if ( sp.ValueKind == JsonValueKind.String && Enum.TryParse<RigEventSide>( sp.GetString(), true, out var parsedSide ) )
						side = parsedSide;
					else if ( sp.ValueKind == JsonValueKind.Number && sp.TryGetInt32( out var sideNum ) )
						side = (RigEventSide)sideNum;
				}
				string propModel = e.TryGetProperty( "PropModel", out var pm ) ? pm.GetString() ?? "" : "";
				string attachBone = e.TryGetProperty( "AttachBone", out var ab ) ? ab.GetString() ?? "hand_L" : "hand_L";
				bool followBone = !e.TryGetProperty( "FollowBone", out var fb ) || fb.ValueKind != JsonValueKind.False;
				float scale = e.TryGetProperty( "ScaleOffset", out var sc ) && sc.ValueKind == JsonValueKind.Number ? sc.GetSingle() : 1f;

				Vec3 pos = Vec3.Zero;
				if ( e.TryGetProperty( "PositionOffset", out var posElem ) && posElem.ValueKind == JsonValueKind.String )
				{
					var parts = posElem.GetString()?.Split( ',' );
					if ( parts is { Length: 3 } &&
						 float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x ) &&
						 float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y ) &&
						 float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z ) )
					{
						pos = new Vec3( x, y, z );
					}
				}

				Quat rot = Quat.Identity;
				if ( e.TryGetProperty( "RotationOffset", out var rotElem ) && rotElem.ValueKind == JsonValueKind.String )
				{
					var parts = rotElem.GetString()?.Split( ',' );
					if ( parts is { Length: 3 } &&
						 float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float p ) &&
						 float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw ) &&
						 float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float r ) )
					{
						rot = Quat.FromAngles( p, yaw, r );
					}
				}

				_events.Add( new RigEvent
				{
					Name = name,
					StartFrame = start,
					EndFrame = end,
					Side = side,
					PropModel = propModel,
					AttachBone = attachBone,
					FollowBone = followBone,
					PositionOffset = pos,
					RotationOffset = rot,
					ScaleOffset = scale
				} );
			}
		}

		if ( root.TryGetProperty( "MorphEvents", out var morphsProp ) && morphsProp.ValueKind == JsonValueKind.Array )
		{
			foreach ( var me in morphsProp.EnumerateArray() )
			{
				string morph = me.TryGetProperty( "MorphName", out var mn ) ? mn.GetString() ?? "" : "";
				if ( string.IsNullOrWhiteSpace( morph ) && me.TryGetProperty( "Name", out var n ) )
					morph = n.GetString() ?? "morph";

				int start = me.TryGetProperty( "StartFrame", out var sf ) && sf.ValueKind == JsonValueKind.Number ? sf.GetInt32() : 0;
				int end = me.TryGetProperty( "EndFrame", out var ef ) && ef.ValueKind == JsonValueKind.Number ? ef.GetInt32() : 1;
				int fadeIn = me.TryGetProperty( "FadeIn", out var fi ) && fi.ValueKind == JsonValueKind.Number ? fi.GetInt32() : 0;
				int fadeOut = me.TryGetProperty( "FadeOut", out var fo ) && fo.ValueKind == JsonValueKind.Number ? fo.GetInt32() : 0;
				float val = me.TryGetProperty( "Value", out var v ) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 1f;

				_morphEvents.Add( new RigMorphEvent
				{
					Morph = morph,
					Start = start,
					End = end,
					FadeIn = fadeIn,
					FadeOut = fadeOut,
					Value = val
				} );
			}
		}

		if ( root.TryGetProperty( "SoundEvents", out var soundsProp ) && soundsProp.ValueKind == JsonValueKind.Array )
		{
			foreach ( var s in soundsProp.EnumerateArray() )
			{
				string name = s.TryGetProperty( "Name", out var n ) ? n.GetString() ?? "sound" : "sound";
				int start = s.TryGetProperty( "StartFrame", out var sf ) && sf.ValueKind == JsonValueKind.Number ? sf.GetInt32()
					: s.TryGetProperty( "Frame", out var fr ) && fr.ValueKind == JsonValueKind.Number ? fr.GetInt32() : 0;
				string path = s.TryGetProperty( "SoundPath", out var sp ) ? sp.GetString() ?? ""
					: s.TryGetProperty( "Path", out var pp ) ? pp.GetString() ?? "" : "";
				float volume = s.TryGetProperty( "Volume", out var v ) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 1f;
				string bone = s.TryGetProperty( "BoneOrigin", out var bo ) ? bo.GetString() ?? ""
					: s.TryGetProperty( "Bone", out var bo2 ) ? bo2.GetString() ?? "" : "";

				_soundEvents.Add( new RigSoundEvent( name, start, path, volume, bone ) );
			}
		}
	}

	void RebuildDispatchItemsIfNeeded()
	{
		if ( !_itemsDirty ) return;
		_items.Clear();
		_sequenceCounter = 0;

		foreach ( var m in _marks )
		{
			_items.Add( new DispatchItem( TimelineEventKind.Mark, m.Name, m.Frame, _sequenceCounter++, Mark: m ) );
		}

		foreach ( var e in _events )
		{
			long startSeq = _sequenceCounter++;
			_items.Add( new DispatchItem( TimelineEventKind.EventStart, e.Name, e.StartFrame, startSeq, Event: e ) );
			long endSeq = _sequenceCounter++;
			_items.Add( new DispatchItem( TimelineEventKind.EventEnd, e.Name, e.EndFrame, endSeq, Event: e ) );
		}

		foreach ( var me in _morphEvents )
		{
			long startSeq = _sequenceCounter++;
			_items.Add( new DispatchItem( TimelineEventKind.MorphEventStart, me.Morph, me.Start, startSeq, MorphEvent: me ) );
			long endSeq = _sequenceCounter++;
			_items.Add( new DispatchItem( TimelineEventKind.MorphEventEnd, me.Morph, me.End, endSeq, MorphEvent: me ) );
		}

		foreach ( var s in _soundEvents )
		{
			_items.Add( new DispatchItem( TimelineEventKind.Sound, s.Name, s.StartFrame, _sequenceCounter++, SoundEvent: s ) );
		}

		_itemsDirty = false;
	}

	public void Play()
	{
		int end = ClipEndFrame;
		if ( !Loop && CurrentFrame >= end )
		{
			CurrentFrame = 0f;
			_fromStart = true;
		}
		else if ( CurrentFrame <= 0f )
		{
			_fromStart = true;
		}
		IsPlaying = true;
	}

	public void Pause()
	{
		IsPlaying = false;
	}

	public void Stop()
	{
		IsPlaying = false;
		CurrentFrame = 0f;
		_previousFrame = null;
		_fromStart = true;
	}

	/// <summary>
	/// Seek or scrub playhead to the specified frame.
	/// By default, seeking and scrubbing suppress forward play events.
	/// If replay is true and seeking forward, intermediate events are dispatched.
	/// Backward seek never emits forward play events.
	/// </summary>
	public IReadOnlyList<TimelineEventNotification> Seek( float targetFrame, bool replay = false )
	{
		if ( !float.IsFinite( targetFrame ) )
			throw new ArgumentOutOfRangeException( nameof( targetFrame ), "Seek frame must be finite." );

		int end = ClipEndFrame;
		float clampedTarget = Math.Clamp( targetFrame, 0f, Math.Max( end, 0f ) );
		float prev = _previousFrame ?? CurrentFrame;

		CurrentFrame = clampedTarget;
		_previousFrame = clampedTarget;
		_fromStart = false;

		if ( !replay || clampedTarget <= prev )
		{
			// Suppressed by default, or backward seek (which emits no forward play events)
			return Array.Empty<TimelineEventNotification>();
		}

		// Explicit forward replay requested
		return DispatchInterval( prev, clampedTarget, isReplay: true, loopIteration: 0 );
	}

	public IReadOnlyList<TimelineEventNotification> Scrub( float targetFrame, bool replay = false ) =>
		Seek( targetFrame, replay );

	/// <summary>
	/// Advance the playhead by <paramref name="deltaTime"/> seconds.
	/// Dispatches crossings over half-open interval (previousFrame, currentFrame].
	/// If paused or deltaTime is 0, no events are dispatched.
	/// If advancing past clip end while looping, dispatches previousFrame to end, plus 0 to currentFrame.
	/// </summary>
	public IReadOnlyList<TimelineEventNotification> Step( float deltaTime )
	{
		if ( !float.IsFinite( deltaTime ) || deltaTime < 0f )
			throw new ArgumentOutOfRangeException( nameof( deltaTime ), "Step delta time must be non-negative and finite." );

		if ( !IsPlaying || deltaTime == 0f )
			return Array.Empty<TimelineEventNotification>();

		int end = ClipEndFrame;
		if ( end <= 0 )
		{
			CurrentFrame = 0f;
			_previousFrame = 0f;
			_fromStart = false;
			return Array.Empty<TimelineEventNotification>();
		}

		float deltaFrames = deltaTime * FrameRate;
		float prev = _fromStart ? -1f : (_previousFrame ?? CurrentFrame);
		_fromStart = false;

		float rawTarget = CurrentFrame + deltaFrames;
		var results = new List<TimelineEventNotification>();

		if ( !Loop )
		{
			// One-shot transport
			if ( rawTarget >= end )
			{
				CurrentFrame = end;
				IsPlaying = false;
				results.AddRange( DispatchInterval( prev, end, isReplay: false, loopIteration: 0 ) );
				_previousFrame = end;
			}
			else
			{
				CurrentFrame = rawTarget;
				results.AddRange( DispatchInterval( prev, rawTarget, isReplay: false, loopIteration: 0 ) );
				_previousFrame = rawTarget;
			}
			return results;
		}

		// Looping transport
		if ( rawTarget <= end )
		{
			CurrentFrame = rawTarget;
			results.AddRange( DispatchInterval( prev, rawTarget, isReplay: false, loopIteration: 0 ) );
			_previousFrame = rawTarget;
			return results;
		}

		// Loop wrapping: rawTarget > end
		// 1. Dispatch from previousFrame to end of current cycle
		results.AddRange( DispatchInterval( prev, end, isReplay: false, loopIteration: 0 ) );

		float remainingDelta = rawTarget - end;
		int fullLoops = (int)(remainingDelta / end);

		// Guard against thread starvation from huge deltas (bounded huge-delta policy)
		if ( fullLoops > MaxLoopIterationsPerStep )
			fullLoops = MaxLoopIterationsPerStep;

		// 2. Dispatch full intermediate loop cycles [0..end]
		for ( int i = 1; i <= fullLoops; i++ )
		{
			results.AddRange( DispatchInterval( -1f, end, isReplay: false, loopIteration: i ) );
		}

		// 3. Dispatch final remainder wrap from 0 to current wrapped frame
		float wrappedFrame = remainingDelta % end;
		CurrentFrame = wrappedFrame;
		results.AddRange( DispatchInterval( -1f, wrappedFrame, isReplay: false, loopIteration: fullLoops + 1 ) );
		_previousFrame = wrappedFrame;

		return results;
	}

	List<TimelineEventNotification> DispatchInterval( float fromFrame, float toFrame, bool isReplay, int loopIteration )
	{
		var batch = new List<TimelineEventNotification>();
		if ( toFrame <= fromFrame )
			return batch;

		RebuildDispatchItemsIfNeeded();

		// Candidate items in (fromFrame, toFrame]
		var candidates = _items
			.Where( item => item.Frame > fromFrame && item.Frame <= toFrame )
			.OrderBy( item => item.Frame )
			.ThenBy( item => item.Sequence )
			.ToList();

		foreach ( var item in candidates )
		{
			var notification = new TimelineEventNotification
			{
				Kind = item.Kind,
				Name = item.Name,
				Frame = item.Frame,
				Mark = item.Mark,
				Event = item.Event,
				MorphEvent = item.MorphEvent,
				SoundEvent = item.SoundEvent,
				IsReplay = isReplay,
				PlayheadTime = FrameRate > 0 ? item.Frame / FrameRate : 0f,
				LoopIteration = loopIteration,
				SequenceNumber = item.Sequence
			};

			batch.Add( notification );
			InvokeNotificationSafely( notification );
		}

		return batch;
	}

	void InvokeNotificationSafely( TimelineEventNotification notification )
	{
		if ( NotificationDispatched is not null )
		{
			foreach ( var handler in NotificationDispatched.GetInvocationList() )
			{
				try
				{
					((Action<TimelineEventNotification>)handler)( notification );
				}
				catch ( Exception ex )
				{
					ReportError( new TimelineEventException( ex, notification ) );
				}
			}
		}

		if ( OnNotification is not null )
		{
			try
			{
				OnNotification( notification );
			}
			catch ( Exception ex )
			{
				ReportError( new TimelineEventException( ex, notification ) );
			}
		}
	}

	void ReportError( TimelineEventException error )
	{
		_errors.Add( error );
		try
		{
			DispatchError?.Invoke( error );
		}
		catch { /* Suppress secondary failure in error reporter */ }

		try
		{
			OnError?.Invoke( error );
		}
		catch { /* Suppress secondary failure in error reporter */ }
	}

	public IReadOnlyList<RigEvent> GetActiveEvents( float frame ) =>
		_events.Where( e => e.IsActive( frame ) ).ToList();

	public IReadOnlyList<RigMorphEvent> GetActiveMorphEvents( float frame ) =>
		_morphEvents.Where( me => frame >= me.Start && frame <= me.End ).ToList();

	public IReadOnlyList<RigSoundEvent> GetActiveSoundEvents( float frame ) =>
		_soundEvents.Where( s => s.IsActive( frame ) ).ToList();
}
