using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class ParticleCueBridgeExtTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		static TimelineEventDispatcher Dispatcher()
		{
			var d = new TimelineEventDispatcher { FrameCount = 61, Loop = false, IsPlaying = true, CurrentFrame = 0f };
			return d;
		}

		registry.Add( "particle-cue-ext", "sound fires preset cue at its bone origin", () =>
		{
			var dispatcher = Dispatcher();
			var calls = new List<(string, Vector3)>();
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls.Add( (cue, origin) ); return new ParticleEmitter(); }, key => key == "hand_L" ? new Vector3( 7, 0, 7 ) : null );
			dispatcher.AddSoundEvent( new RigSoundEvent( "fx:water_splash", 10, "splash.wav", 1f, "hand_L" ) );
			dispatcher.Step( 11f / 30f );
			Check( calls.Count == 1 && calls[0].Item1 == "water_splash" && calls[0].Item2 == new Vector3( 7, 0, 7 ), $"sound fired [{string.Join( ",", calls )}], expected water_splash at (7,0,7)" );
			Check( bridge.FiredCount == 1, $"expected FiredCount 1, got {bridge.FiredCount}" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue-ext", "sound with no bone origin fires at its name key", () =>
		{
			var dispatcher = Dispatcher();
			var calls = new List<(string, Vector3)>();
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls.Add( (cue, origin) ); return new ParticleEmitter(); }, key => key == "fx:sparks" ? new Vector3( 1, 2, 3 ) : null );
			dispatcher.AddSoundEvent( new RigSoundEvent( "fx:sparks", 10, "spark.wav", 1f, "" ) );
			dispatcher.Step( 11f / 30f );
			Check( calls.Count == 1 && calls[0].Item1 == "sparks" && calls[0].Item2 == new Vector3( 1, 2, 3 ), $"bone-less sound fired [{string.Join( ",", calls )}], expected sparks at (1,2,3)" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue-ext", "sound resolves preset names the same way lane 4 does", () =>
		{
			var dispatcher = Dispatcher();
			var calls = new List<string>();
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls.Add( cue ); return new ParticleEmitter(); }, _ => new Vector3( 0, 0, 0 ) );
			dispatcher.AddSoundEvent( new RigSoundEvent( "footstep_water_splash", 10, "step.wav", 1f, "foot_L" ) );
			dispatcher.Step( 11f / 30f );
			Check( calls.Count == 1 && calls[0] == "water_splash", $"underscore-word sound fired [{string.Join( ",", calls )}], expected water_splash" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue-ext", "sound with an unknown name is ignored, not fired", () =>
		{
			var dispatcher = Dispatcher();
			int calls = 0;
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls++; return new ParticleEmitter(); }, _ => new Vector3( 0, 0, 0 ) );
			dispatcher.AddSoundEvent( new RigSoundEvent( "door_creak", 10, "door.wav", 1f, "hand_L" ) );
			dispatcher.Step( 11f / 30f );
			Check( calls == 0 && bridge.FiredCount == 0 && bridge.SkippedUnknownCount == 1 && bridge.SkippedDeliberateCount == 0, $"unknown sound: calls {calls}, fired {bridge.FiredCount}, unknown {bridge.SkippedUnknownCount}, deliberate {bridge.SkippedDeliberateCount}" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue-ext", "origin-less sound is skipped, not guessed", () =>
		{
			var dispatcher = Dispatcher();
			int calls = 0;
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls++; return new ParticleEmitter(); }, key => key == "hand_L" ? new Vector3( 5, 0, 5 ) : null );
			dispatcher.AddSoundEvent( new RigSoundEvent( "fx:sparks", 10, "spark.wav", 1f, "foot_L" ) );
			dispatcher.Step( 11f / 30f );
			Check( calls == 0 && bridge.FiredCount == 0 && bridge.SkippedNoOriginCount == 1, $"origin-less sound: calls {calls}, skipped-no-origin {bridge.SkippedNoOriginCount}" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue-ext", "morph start is deliberately skipped even when preset-named", () =>
		{
			var dispatcher = Dispatcher();
			int calls = 0;
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls++; return new ParticleEmitter(); }, _ => new Vector3( 0, 0, 0 ) );
			dispatcher.AddMorphEvent( new RigMorphEvent { Morph = "fx:sparks", Start = 5, End = 15 } );
			dispatcher.Step( 6f / 30f );
			Check( calls == 0 && bridge.FiredCount == 0 && bridge.SkippedDeliberateCount == 1 && bridge.SkippedUnknownCount == 0, $"preset-named morph start: calls {calls}, deliberate {bridge.SkippedDeliberateCount}, unknown {bridge.SkippedUnknownCount}" );
			dispatcher.Step( 10f / 30f );
			Check( calls == 0 && bridge.FiredCount == 0 && bridge.SkippedDeliberateCount == 2, $"after morph end: calls {calls}, deliberate {bridge.SkippedDeliberateCount}" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue-ext", "morph end is deliberately skipped and does not stop event emitters", () =>
		{
			var dispatcher = Dispatcher();
			var fired = new List<ParticleEmitter>();
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { var em = ParticleEmitterPresets.Create( cue, origin ); fired.Add( em ); return em; }, _ => new Vector3( 0, 0, 0 ) );
			dispatcher.AddEvent( new RigEvent { Name = "bubbles", StartFrame = 5, EndFrame = 15, AttachBone = "hand_L" } );
			dispatcher.AddMorphEvent( new RigMorphEvent { Morph = "smile", Start = 8, End = 12 } );
			dispatcher.Step( 6f / 30f );
			Check( fired.Count == 1 && bridge.LoopingCount == 1, $"after event start: fired {fired.Count}, looping {bridge.LoopingCount}" );
			var rate = fired[0];
			Check( rate.Enabled, "rate emitter was disabled before its event ended" );
			dispatcher.Step( 7f / 30f );
			Check( bridge.SkippedDeliberateCount == 2 && bridge.LoopingCount == 1 && rate.Enabled, $"after morph start+end: deliberate {bridge.SkippedDeliberateCount}, looping {bridge.LoopingCount}, rate enabled {rate.Enabled}" );
			dispatcher.Step( 3f / 30f );
			Check( bridge.LoopingCount == 0 && !rate.Enabled, $"after event end: looping {bridge.LoopingCount}, rate enabled {rate.Enabled}" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue-ext", "sound-fired rate emitter is not tracked, like a mark-fired one", () =>
		{
			var dispatcher = Dispatcher();
			var fired = new List<ParticleEmitter>();
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { var em = ParticleEmitterPresets.Create( cue, origin ); fired.Add( em ); return em; }, _ => new Vector3( 0, 0, 0 ) );
			dispatcher.AddSoundEvent( new RigSoundEvent( "bubbles", 10, "bubbles.wav", 1f, "hand_L" ) );
			dispatcher.Step( 11f / 30f );
			Check( fired.Count == 1 && bridge.FiredCount == 1 && bridge.LoopingCount == 0 && fired[0].Enabled, $"sound rate emitter: fired {fired.Count}, looping {bridge.LoopingCount}, enabled {fired[0].Enabled}" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue-ext", "detach stops all new mappings", () =>
		{
			var dispatcher = Dispatcher();
			int calls = 0;
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls++; return new ParticleEmitter(); }, _ => new Vector3( 0, 0, 0 ) );
			dispatcher.AddSoundEvent( new RigSoundEvent( "fx:sparks", 10, "spark.wav", 1f, "hand_L" ) );
			dispatcher.Step( 11f / 30f );
			Check( calls == 1, $"expected 1 call before detach, got {calls}" );
			bridge.Detach();
			dispatcher.AddSoundEvent( new RigSoundEvent( "fx:fire", 30, "fire.wav", 1f, "hand_L" ) );
			dispatcher.AddMorphEvent( new RigMorphEvent { Morph = "smile", Start = 30, End = 35 } );
			dispatcher.Step( 20f / 30f );
			Check( calls == 1 && bridge.FiredCount == 1 && bridge.SkippedDeliberateCount == 0, $"after detach: calls {calls}, fired {bridge.FiredCount}, deliberate {bridge.SkippedDeliberateCount}" );
		} );
	}
}
