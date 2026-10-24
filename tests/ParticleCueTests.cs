using System.Numerics;
using Marionette.Desktop;

internal static class ParticleCueTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		static TimelineEventDispatcher Dispatcher()
		{
			var d = new TimelineEventDispatcher { FrameCount = 61, Loop = false, IsPlaying = true, CurrentFrame = 0f };
			return d;
		}

		registry.Add( "particle-cue", "all 14 presets resolve, create, and leave the base settings unmutated", () =>
		{
			Check( ParticleEmitterPresets.Names.Length == 14, $"expected 14 presets, got {ParticleEmitterPresets.Names.Length}: {string.Join( ",", ParticleEmitterPresets.Names )}" );
			var origin = new Vector3( 1, 2, 3 );
			var baseSettings = new ParticleSystemSettings();
			var baseGravity = baseSettings.Gravity;
			foreach ( var name in ParticleEmitterPresets.Names )
			{
				Check( ParticleEmitterPresets.ResolveName( name ) == name, $"preset {name} did not resolve to itself" );
				Check( ParticleEmitterPresets.ResolveName( "fx:" + name ) == name, $"fx: prefix did not resolve {name}" );
				var em = ParticleEmitterPresets.Create( name, origin );
				Check( em.Position == origin, $"preset {name} emitter not at origin" );
				var medium = ParticleEmitterPresets.SettingsFor( name, baseSettings );
				Check( !ReferenceEquals( medium, baseSettings ), $"SettingsFor {name} mutated the base settings object" );
			}
			Check( baseSettings.Gravity == baseGravity, "SettingsFor mutated the caller's base settings" );
			Check( ParticleEmitterPresets.ResolveName( "camera_cut" ) is null, "camera_cut resolved to a preset" );
			Check( ParticleEmitterPresets.ResolveName( "" ) is null, "empty cue resolved to a preset" );
			bool threw = false;
			try { ParticleEmitterPresets.Create( "camera_cut", origin ); } catch ( ArgumentException ) { threw = true; }
			Check( threw, "Create accepted an unknown preset instead of failing loudly" );
		} );

		registry.Add( "particle-cue", "new presets read: rain falls, fire rises, snow drifts gently", () =>
		{
			var origin = Vector3.Zero;
			var baseSettings = new ParticleSystemSettings();
			var rain = ParticleEmitterPresets.Create( "rain", origin );
			Check( rain.Direction.Z < 0 && rain.Shape == ParticleEmitterShape.Box && rain.Bursts[0].Count == 200, $"rain is not a falling box burst: dir {rain.Direction}, shape {rain.Shape}, burst {(rain.Bursts.Count > 0 ? rain.Bursts[0].Count : 0)}" );
			Check( ParticleEmitterPresets.SettingsFor( "rain", baseSettings ).Drag == 0.05f, "rain drag is not 0.05" );
			Check( ParticleEmitterPresets.SettingsFor( "fire", baseSettings ).Gravity.Z == 80f, "fire medium does not rise" );
			Check( ParticleEmitterPresets.Create( "fire", origin ).Bursts[0].Count == 120, "fire burst is not 120" );
			var snow = ParticleEmitterPresets.SettingsFor( "snow", baseSettings );
			Check( snow.Gravity.Z == -30f && snow.Drag == 0.8f, $"snow does not fall gently: gravity {snow.Gravity}, drag {snow.Drag}" );
			Check( ParticleEmitterPresets.SettingsFor( "smoke", baseSettings ).Gravity.Z == 30f, "smoke medium does not rise slowly" );
			Check( ParticleEmitterPresets.SettingsFor( "steam", baseSettings ).Gravity.Z == 40f, "steam medium does not rise" );
			Check( ParticleEmitterPresets.SettingsFor( "leaves", baseSettings ).TurbulenceStrength == 35f, "leaves do not flutter" );
			var confetti = ParticleEmitterPresets.Create( "confetti", origin );
			Check( confetti.AngularSpeedMax == 25f && confetti.Bursts[0].Count == 150, "confetti is not a tumbling 150 burst" );
			var sparkle = ParticleEmitterPresets.Create( "magic_sparkle", origin );
			Check( sparkle.Shape == ParticleEmitterShape.Sphere && sparkle.SpreadDegrees == 180f, "magic_sparkle is not an omnidirectional sphere burst" );
		} );

		registry.Add( "particle-cue", "renderer keeps one system per preset with its own medium, and bubbles rise", () =>
		{
			using var context = new HeadlessGlContext();
			using var renderer = new SceneRenderer( context.Gl ) { SsaoStrength = 0, SsrStrength = 0 };
			var origin = new Vector3( 0, 0, 10 );
			Check( renderer.TriggerParticleCue( "camera_cut", origin ) is null, "camera_cut fired particles" );
			Check( renderer.ParticleSystemFor( "camera_cut" ) is null && renderer.ParticleSystems.Count == 0, "unknown cue created a system" );
			var bubbles = renderer.TriggerParticleCue( "fx:bubbles", origin );
			Check( bubbles is not null, "bubbles cue returned null" );
			var bubblesSystem = renderer.ParticleSystemFor( "bubbles" );
			Check( bubblesSystem is not null && bubblesSystem.Settings.Gravity.Z == 60f, $"bubbles system gravity is {bubblesSystem?.Settings.Gravity}, not rising +60" );
			var sparks = renderer.TriggerParticleCue( "sparks", origin );
			Check( sparks is not null, "sparks cue returned null" );
			var sparksSystem = renderer.ParticleSystemFor( "sparks" );
			Check( sparksSystem is not null && MathF.Abs( sparksSystem.Settings.Gravity.Z - -386.0886f ) < 1e-3f && sparksSystem.Settings.Drag == 0.05f, $"sparks system medium wrong: gravity {sparksSystem?.Settings.Gravity}, drag {sparksSystem?.Settings.Drag}" );
			Check( !ReferenceEquals( bubblesSystem, sparksSystem ), "bubbles and sparks share one system (the old bug)" );
			Check( ReferenceEquals( renderer.TriggerParticleCue( "bubbles", origin ), null ) == false && ReferenceEquals( renderer.ParticleSystemFor( "bubbles" ), bubblesSystem ), "second bubbles cue did not reuse its system" );
			Check( renderer.ParticleSystems.Count == 2 && renderer.Live.Layers.Count == 2, $"expected 2 systems/layers, got {renderer.ParticleSystems.Count}/{renderer.Live.Layers.Count}" );
			bubblesSystem!.Step( 60 );
			Check( bubblesSystem!.AliveCount > 0, "bubbles system emitted nothing in 60 steps" );
			float maxZ = float.NegativeInfinity;
			foreach ( var state in bubblesSystem!.States )
				if ( state.Alive != 0 ) maxZ = MathF.Max( maxZ, state.Position.Z );
			Check( maxZ > origin.Z + 5f, $"bubbles did not rise: max Z {maxZ} from origin Z {origin.Z}" );
			renderer.DetachCueBridge();
		} );

		registry.Add( "particle-cue", "bridge fires preset marks and ignores camera cuts", () =>
		{
			var dispatcher = Dispatcher();
			var calls = new List<string>();
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls.Add( cue ); return new ParticleEmitter(); }, _ => new Vector3( 1, 2, 3 ) );
			dispatcher.AddMark( "fx:water_splash", 10 );
			dispatcher.AddMark( "camera_cut", 20 );
			dispatcher.Step( 21f / 30f );
			Check( calls.Count == 1 && calls[0] == "water_splash", $"bridge fired [{string.Join( ",", calls )}], expected only water_splash" );
			Check( bridge.FiredCount == 1 && bridge.SkippedUnknownCount == 1, $"bridge counts wrong: fired {bridge.FiredCount}, unknown {bridge.SkippedUnknownCount}" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue", "bridge skips cues with no resolvable origin instead of firing them somewhere wrong", () =>
		{
			var dispatcher = Dispatcher();
			var calls = new List<string>();
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls.Add( cue ); return ParticleEmitterPresets.Create( cue, origin ); }, key => key == "hand_L" ? new Vector3( 5, 0, 5 ) : null );
			dispatcher.AddEvent( new RigEvent { Name = "fx:bubbles", StartFrame = 5, EndFrame = 15, AttachBone = "foot_L" } );
			dispatcher.AddEvent( new RigEvent { Name = "fx:sparks", StartFrame = 5, EndFrame = 15, AttachBone = "hand_L" } );
			dispatcher.Step( 6f / 30f );
			Check( calls.Count == 1 && calls[0] == "sparks", $"bridge fired [{string.Join( ",", calls )}], expected only sparks" );
			Check( bridge.SkippedNoOriginCount == 1, $"expected 1 skipped-no-origin, got {bridge.SkippedNoOriginCount}" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue", "bridge without a resolver fires nothing", () =>
		{
			var dispatcher = Dispatcher();
			int calls = 0;
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls++; return new ParticleEmitter(); } );
			dispatcher.AddMark( "fx:sparks", 10 );
			dispatcher.Step( 11f / 30f );
			Check( calls == 0 && bridge.SkippedNoOriginCount == 1 && bridge.FiredCount == 0, $"resolver-less bridge fired {calls} cues" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue", "bridge stops looping rate emitters at event end, leaves bursts alone", () =>
		{
			var dispatcher = Dispatcher();
			var fired = new List<ParticleEmitter>();
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { var em = ParticleEmitterPresets.Create( cue, origin ); fired.Add( em ); return em; }, _ => new Vector3( 0, 0, 0 ) );
			dispatcher.AddEvent( new RigEvent { Name = "bubbles", StartFrame = 5, EndFrame = 15, AttachBone = "hand_L" } );
			dispatcher.AddEvent( new RigEvent { Name = "sparks", StartFrame = 5, EndFrame = 15, AttachBone = "hand_L" } );
			dispatcher.Step( 6f / 30f );
			Check( fired.Count == 2 && bridge.LoopingCount == 1, $"after event start: fired {fired.Count}, looping {bridge.LoopingCount} (expected 2 fired, 1 looping rate emitter)" );
			var rate = fired[0].Rate > 0 ? fired[0] : fired[1];
			Check( rate.Enabled, "rate emitter was disabled before its event ended" );
			dispatcher.Step( 10f / 30f );
			Check( bridge.LoopingCount == 0 && !rate.Enabled, $"after event end: looping {bridge.LoopingCount}, rate emitter still enabled {rate.Enabled}" );
			bridge.Detach();
		} );

		registry.Add( "particle-cue", "bridge detach stops all firing", () =>
		{
			var dispatcher = Dispatcher();
			int calls = 0;
			var bridge = new ParticleCueBridge( dispatcher, ( cue, origin, dir ) => { calls++; return new ParticleEmitter(); }, _ => new Vector3( 0, 0, 0 ) );
			dispatcher.AddMark( "fx:sparks", 10 );
			dispatcher.Step( 11f / 30f );
			Check( calls == 1, $"expected 1 call before detach, got {calls}" );
			bridge.Detach();
			dispatcher.AddMark( "fx:fire", 30 );
			dispatcher.Step( 20f / 30f );
			Check( calls == 1, $"detached bridge still fired (calls {calls})" );
		} );

		registry.Add( "particle-cue", "renderer AttachCueBridge fires a cue from the dispatcher", () =>
		{
			using var context = new HeadlessGlContext();
			using var renderer = new SceneRenderer( context.Gl ) { SsaoStrength = 0, SsrStrength = 0 };
			var dispatcher = Dispatcher();
			var origin = new Vector3( 0, 0, 20 );
			renderer.AttachCueBridge( dispatcher, _ => origin );
			dispatcher.AddMark( "fx:sparks", 10 );
			dispatcher.Step( 11f / 30f );
			var system = renderer.ParticleSystemFor( "sparks" );
			Check( system is not null, "attached bridge did not fire sparks through the renderer" );
			renderer.Live.Advance( 0.05f );
			Check( system!.AliveCount > 0, $"sparks burst produced no live particles (alive {system.AliveCount})" );
			renderer.DetachCueBridge();
		} );
	}
}
