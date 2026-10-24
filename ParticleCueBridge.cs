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
/// marks. Non-preset names (camera cuts, prop attaches) are ignored, and
/// morph/sound notifications are ignored entirely.
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
			case TimelineEventKind.Mark:
			case TimelineEventKind.Event:
				break;
			default:
				return;
		}
		var preset = ParticleEmitterPresets.ResolveName( notification.Name );
		if ( preset is null ) { SkippedUnknownCount++; return; }
		// Events key on their attach bone; marks have no bone, so they key on
		// the cue name itself. Either way the resolver, not this bridge,
		// decides what that key means in world space.
		string key = notification.Event?.AttachBone is { Length: > 0 } bone ? bone : notification.Name;
		var origin = _originResolver?.Invoke( key );
		if ( origin is null ) { SkippedNoOriginCount++; return; }
		var emitter = _trigger( preset, origin.Value, null );
		if ( emitter is null ) return;
		FiredCount++;
		// Only a RigEvent start gives us an end to stop at, and only a rate
		// emitter needs stopping.
		if ( notification.Kind == TimelineEventKind.Event && notification.Event is { } evt && emitter.Rate > 0 )
		{
			if ( !_looping.TryGetValue( evt, out var list ) ) _looping[evt] = list = new List<ParticleEmitter>();
			list.Add( emitter );
		}
	}
}
