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
	MorphEventEnd
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
		RigMorphEvent? MorphEvent = null
	);

	readonly List<RigMark> _marks = new();
	readonly List<RigEvent> _events = new();
	readonly List<RigMorphEvent> _morphEvents = new();
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
}
