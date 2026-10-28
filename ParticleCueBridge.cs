#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// Lane 4 (cues + presets): the missing caller for
/// <see cref="SceneRenderer.TriggerParticleCue"/>. Subscribes to a
/// <see cref="TimelineEventDispatcher"/>'s <c>NotificationDispatched</c> event
/// (the dispatcher itself is never edited) and turns timeline marks and event
/// starts whose names resolve to a particle preset into cues.
///
/// Origin rule — the one thing this bridge refuses to guess at: a timeline
/// notification carries NO world position, only a name and, for events, an
/// attach bone. The caller supplies <c>originResolver</c>, which maps an
/// event's attach bone — or a mark's cue name — to a world position. When
/// there is no resolver, or it returns null for that key, the cue is SKIPPED
/// and counted, never fired at a default origin where it does not belong.
/// A viewport owner that can resolve bone world positions supplies one;
/// until then, cues stay silent rather than wrong.
///
/// Stop rule: a rate emitter (bubbles, dust_motes) fired by a RigEvent start
/// is tracked under that event object and disabled when the event's end
/// notification arrives. Burst presets are never tracked — they die on
/// their own. A mark-fired rate emitter has no end notification, so it
/// keeps running until its caller disables it, exactly like a direct
/// <see cref="ParticleSystem.TriggerCue"/> call; prefer burst presets for
/// marks. Non-preset names (camera cuts, prop attaches) are ignored.
/// P34 (cue-bridge event extensions) — lane 4's file, extended, lane 4's
/// behaviour unchanged for the kinds lane 4 already handled (Mark, Event /
/// EventStart, EventEnd). Uncovered kinds, enumerated from
/// TimelineEventKind in code (EventStart is the same value as Event, and
/// MorphEventStart is the same value as MorphEvent, so there are six
/// distinct values, not eight):
/// - Sound: FIRES, exactly like a mark. A sound cue is instantaneous, like
///   a mark, and its name resolves through the same ResolveName path, so a
///   sound named for a preset (e.g. a footstep sound named fx:water_splash)
///   fires that preset. Its origin key is its BoneOrigin when one is set
///   (a hand, mouth, or foot bone), otherwise its name, exactly as lane 4
///   keys marks on their name. A sound has no end notification, so a rate
///   emitter fired by a sound is NOT tracked — it keeps running until its
///   caller disables it, like a mark-fired rate emitter. Origin-less sounds
///   are skipped silently, never positioned by guesswork, same as lane 4.
/// - MorphEvent / MorphEventStart: DELIBERATE SKIP, counted separately in
///   SkippedDeliberateCount. A morph event is continuous face/body shape
///   state (a morph target held at a weight over a frame range), not a cue:
///   its name identifies a morph target, not a particle preset, and it
///   carries no bone and no position at all. Firing on a name coincidence
///   would put FX on a face change, and there is no origin to resolve
///   except the morph name itself — which is the guesswork lane 4 forbids.
///   Even a preset-named morph is therefore skipped, not fired.
/// - MorphEventEnd: DELIBERATE SKIP, counted the same way. Nothing is
///   tracked under a morph event (see above), so there is nothing to stop;
///   the end notification must not disable emitters tracked under a RigEvent
///   or any other object, and must not crash.
/// </summary>
public sealed class ParticleCueBridge
{
	readonly TimelineEventDispatcher _dispatcher;
	readonly Func<string, Vector3, Vector3?, ParticleEmitter?> _trigger;
	readonly Func<string, Vector3?>? _originResolver;
	readonly Action<TimelineEventNotification> _handler;
	readonly Dictionary<object, List<ParticleEmitter>> _looping = new();

	/// <summary>Cues fired through the trigger delegate.</summary>
	public int FiredCount { get; private set; }

	/// <summary>Cues skipped because no origin resolved for their key.</summary>
	public int SkippedNoOriginCount { get; private set; }

	/// <summary>Notifications ignored because their name is no particle preset.</summary>
	public int SkippedUnknownCount { get; private set; }

	/// <summary>Notifications deliberately skipped by kind (morph start/end, P34), never fired.</summary>
	public int SkippedDeliberateCount { get; private set; }

	/// <summary>Rate emitters currently tracked for a RigEvent end.</summary>
	public int LoopingCount
	{
		get
		{
			int n = 0;
			foreach ( var list in _looping.Values ) n += list.Count;
			return n;
		}
	}

	public ParticleCueBridge( TimelineEventDispatcher dispatcher, Func<string, Vector3, Vector3?, ParticleEmitter?> trigger, Func<string, Vector3?>? originResolver = null )
	{
		_dispatcher = dispatcher ?? throw new ArgumentNullException( nameof( dispatcher ) );
		_trigger = trigger ?? throw new ArgumentNullException( nameof( trigger ) );
		_originResolver = originResolver;
		_handler = OnNotification;
		_dispatcher.NotificationDispatched += _handler;
	}

	/// <summary>Unsubscribe from the dispatcher. Idempotent.</summary>
	public void Detach()
	{
		_dispatcher.NotificationDispatched -= _handler;
	}

	void OnNotification( TimelineEventNotification notification )
	{
		if ( notification is null ) return;
		switch ( notification.Kind )
		{
			case TimelineEventKind.EventEnd:
				if ( notification.Event is { } ended && _looping.TryGetValue( ended, out var emitters ) )
				{
					foreach ( var e in emitters ) e.Enabled = false;
					_looping.Remove( ended );
				}
				return;
			case TimelineEventKind.MorphEvent:
				// MorphEventStart is the same enum value as MorphEvent.
				// Deliberate skip (P34): a morph is shape state, not a cue,
				// and carries no bone or position. Counted separately so a
				// preset-named morph is visibly skipped, never fired.
				SkippedDeliberateCount++;
				return;
			case TimelineEventKind.MorphEventEnd:
				// Deliberate skip (P34): nothing is tracked under a morph
				// event, so there is nothing to stop, and this must not
				// touch emitters tracked under a RigEvent.
				SkippedDeliberateCount++;
				return;
			case TimelineEventKind.Mark:
			case TimelineEventKind.Event:
			case TimelineEventKind.Sound:
				// EventStart is the same enum value as Event, so it is
				// covered by the Event case exactly as lane 4 handled it.
				break;
			default:
				return;
		}
		var preset = ParticleEmitterPresets.ResolveName( notification.Name );
		if ( preset is null ) { SkippedUnknownCount++; return; }
		// Events key on their attach bone; sounds key on their bone origin
		// when one is set; marks (and bone-less sounds) key on the cue name
		// itself. Either way the resolver, not this bridge, decides what
		// that key means in world space.
		string key = notification.Event?.AttachBone is { Length: > 0 } bone ? bone
			: notification.SoundEvent?.BoneOrigin is { Length: > 0 } boneOrigin ? boneOrigin
			: notification.Name;
		var origin = _originResolver?.Invoke( key );
		if ( origin is null ) { SkippedNoOriginCount++; return; }
		var emitter = _trigger( preset, origin.Value, null );
		if ( emitter is null ) return;
		FiredCount++;
		// Only a RigEvent start gives us an end to stop at, and only a rate
		// emitter needs stopping. Sound and mark firings have no end, so
		// their rate emitters are left running for the caller to disable,
		// exactly like lane 4's mark rule.
		if ( notification.Kind == TimelineEventKind.Event && notification.Event is { } evt && emitter.Rate > 0 )
		{
			if ( !_looping.TryGetValue( evt, out var list ) ) _looping[evt] = list = new List<ParticleEmitter>();
			list.Add( emitter );
		}
	}
}
