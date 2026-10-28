using System;
using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleVerbPackTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "particle-verb-pack", name, test );
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Func<object?> action, string label )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( $"expected an error for {label}, got silent success" );
		}
		void Bad( string name, string line ) => Check( "bad: " + name, () => Throws( () => ParticleVerbPack.Parse( line ), name ) );
		ParticleLoopRegistry Fresh() => new();

		Check( "burst parses: kind, canonical preset, at, count", () =>
		{
			var cmd = ParticleVerbPack.Parse( "particle burst preset=fx:sparks at=0,0,10 count=5" );
			Assert( cmd.Kind == ParticleVerbPackKind.Burst, $"kind {cmd.Kind}" );
			Assert( cmd.Preset == "sparks", $"preset {cmd.Preset}" );
			Assert( cmd.At == new Vector3( 0, 0, 10 ), $"at {cmd.At}" );
			Assert( cmd.Count == 5, $"count {cmd.Count}" );
		} );
		Check( "burst count overrides the preset burst count", () =>
		{
			var r = ParticleVerbPack.Run( "particle burst preset=sparks at=0,0,10 count=5", Fresh() );
			Assert( r.System!.AliveCount == 5, $"sparks carries a 70 burst; count=5 fired {r.System.AliveCount}" );
		} );
		Check( "burst fires at once: alive right after Run, one step taken", () =>
		{
			var r = ParticleVerbPack.Run( "particle burst preset=dust_motes at=1,2,3 count=40", Fresh() );
			Assert( r.System!.StepIndex == 1, $"StepIndex {r.System.StepIndex}" );
			Assert( r.System.AliveCount == 40, $"AliveCount {r.System.AliveCount}" );
			Assert( r.Emitter!.Position == new Vector3( 1, 2, 3 ), $"emitter at {r.Emitter.Position}" );
			Assert( r.Emitter.Rate == 0, $"burst emitter rate {r.Emitter.Rate} (must be 0, no trickle)" );
			r.System.Step( 60 );
			Assert( r.System.AliveCount == 40, $"burst must not refire; after 60 more steps {r.System.AliveCount} (dust lives 3s+, none dead yet)" );
		} );
		Check( "burst applies the preset medium like lane 5", () =>
		{
			var r = ParticleVerbPack.Run( "particle burst preset=bubbles at=0,0,0 count=25", Fresh() );
			Assert( r.System!.AliveCount == 25, $"AliveCount {r.System.AliveCount}" );
			Assert( r.System.Settings.Gravity.Z == 60f, $"bubbles medium gravity Z {r.System.Settings.Gravity.Z} (must rise, not fall)" );
		} );
		Check( "burst accepts position= as the at alias", () =>
		{
			var cmd = ParticleVerbPack.Parse( "particle burst preset=sparks position=4,5,6 count=3" );
			Assert( cmd.At == new Vector3( 4, 5, 6 ), $"at {cmd.At}" );
		} );
		Check( "burst registers no handle", () =>
		{
			var reg = Fresh();
			var r = ParticleVerbPack.Run( "particle burst preset=sparks at=0,0,10 count=5", reg );
			Assert( r.Handle is null, $"burst handle {r.Handle}" );
			Assert( reg.Count == 0, $"registry count {reg.Count}" );
		} );
		Check( "loop parses and registers loop-1 with rate, enabled, no bursts", () =>
		{
			var reg = Fresh();
			var r = ParticleVerbPack.Run( "particle loop preset=dust_motes at=0,0,10 rate=60", reg );
			Assert( r.Handle == "loop-1", $"handle {r.Handle}" );
			Assert( r.Emitter!.Rate == 60f, $"rate {r.Emitter.Rate}" );
			Assert( r.Emitter.Enabled, "loop emitter must start enabled" );
			Assert( r.Emitter.Bursts.Count == 0, $"preset bursts not cleared: {r.Emitter.Bursts.Count}" );
			Assert( reg.Count == 1, $"registry count {reg.Count}" );
		} );
		Check( "loop handles increment and never repeat", () =>
		{
			var reg = Fresh();
			var a = ParticleVerbPack.Run( "particle loop preset=dust_motes at=0,0,0 rate=10", reg );
			var b = ParticleVerbPack.Run( "particle loop preset=dust_motes at=0,0,0 rate=10", reg );
			Assert( a.Handle == "loop-1" && b.Handle == "loop-2", $"handles {a.Handle} / {b.Handle}" );
		} );
		Check( "loop emits over time: 0 before stepping, 60 after 60 steps at 60/s", () =>
		{
			var reg = Fresh();
			var r = ParticleVerbPack.Run( "particle loop preset=dust_motes at=0,0,10 rate=60", reg );
			Assert( r.System!.AliveCount == 0, $"before stepping {r.System.AliveCount}" );
			r.System.Step( 60 );
			Assert( r.System.AliveCount == 60, $"after 60 steps {r.System.AliveCount} (dust lives 3-6s, none dead yet)" );
		} );
		Check( "loop clears the preset burst: sparks loop fires 1, not 71, on step 1", () =>
		{
			var r = ParticleVerbPack.Run( "particle loop preset=sparks at=0,0,10 rate=60", Fresh() );
			r.System!.Step();
			Assert( r.System.AliveCount == 1, $"AliveCount {r.System.AliveCount} (70-burst leaked into the loop if 71)" );
		} );
		Check( "stop halts emission: alive frozen, emitter disabled, handle consumed", () =>
		{
			var reg = Fresh();
			var r = ParticleVerbPack.Run( "particle loop preset=dust_motes at=0,0,10 rate=60", reg );
			r.System!.Step( 60 );
			Assert( r.System.AliveCount == 60, $"before stop {r.System.AliveCount}" );
			var stopped = ParticleVerbPack.Run( "particle stop id=loop-1", reg );
			Assert( stopped.Emitter!.Enabled == false, "stop must disable the emitter" );
			Assert( reg.Count == 0, $"registry count after stop {reg.Count}" );
			r.System.Step( 60 );
			Assert( r.System.AliveCount == 60, $"after stop + 60 steps {r.System.AliveCount} (no new, none dead: dust lives 3s+)" );
		} );
		Check( "set-rate changes emission live: 60, then 0, then 120/s", () =>
		{
			var reg = Fresh();
			var r = ParticleVerbPack.Run( "particle loop preset=dust_motes at=0,0,10 rate=60", reg );
			r.System!.Step( 60 );
			Assert( r.System.AliveCount == 60, $"phase 1: {r.System.AliveCount}" );
			ParticleVerbPack.Run( "particle set-rate id=loop-1 rate=0", reg );
			r.System.Step( 60 );
			Assert( r.System.AliveCount == 60, $"phase 2 at rate 0: {r.System.AliveCount}" );
			ParticleVerbPack.Run( "particle set-rate id=loop-1 rate=120", reg );
			r.System.Step( 30 );
			Assert( r.System.AliveCount == 120, $"phase 3 at 120/s for 30 steps: {r.System.AliveCount}" );
		} );
		Check( "set-rate before any step changes the emitter rate", () =>
		{
			var reg = Fresh();
			var r = ParticleVerbPack.Run( "particle loop preset=dust_motes at=0,0,10 rate=10", reg );
			ParticleVerbPack.Run( "particle set-rate id=loop-1 rate=90", reg );
			Assert( r.Emitter!.Rate == 90f, $"rate {r.Emitter.Rate}" );
		} );
		Check( "IsPackLine routes pack forms here and lane-5 forms away", () =>
		{
			Assert( ParticleVerbPack.IsPackLine( "particle burst preset=sparks at=0,0,10 count=5" ), "burst must route here" );
			Assert( ParticleVerbPack.IsPackLine( "particle loop preset=dust_motes at=0,0,10 rate=60" ), "loop must route here" );
			Assert( ParticleVerbPack.IsPackLine( "particle stop id=loop-1" ), "stop must route here" );
			Assert( ParticleVerbPack.IsPackLine( "particle set-rate id=loop-1 rate=30" ), "set-rate must route here" );
			Assert( !ParticleVerbPack.IsPackLine( "particle preset=sparks at=0,0,10" ), "lane-5 preset form must NOT route here" );
			Assert( !ParticleVerbPack.IsPackLine( "particle file=defs/fountain.json" ), "lane-5 file form must NOT route here" );
			Assert( !ParticleVerbPack.IsPackLine( "particle preset=sparks save=defs/dust.json" ), "lane-5 save form must NOT route here" );
			Assert( !ParticleVerbPack.IsPackLine( "" ), "empty must not route here" );
			Assert( !ParticleVerbPack.IsPackLine( "particle frobnicate preset=sparks" ), "unknown word must not route here" );
		} );
		Check( "pack lines parse case-insensitively like lane 5", () =>
		{
			var cmd = ParticleVerbPack.Parse( "PARTICLE BURST PRESET=sparks AT=0,0,10 COUNT=7" );
			Assert( cmd.Kind == ParticleVerbPackKind.Burst && cmd.Count == 7, $"got {cmd.Kind}/{cmd.Count}" );
		} );

		Bad( "empty line", "" );
		Bad( "line not starting with particle", "burst preset=sparks at=0,0,10 count=5" );
		Bad( "token without equals", "particle burst preset sparks at=0,0,10 count=5" );
		Bad( "duplicate key", "particle burst preset=sparks preset=sparks at=0,0,10 count=5" );
		Bad( "unknown form word", "particle frobnicate preset=sparks at=0,0,10" );
		Bad( "burst missing preset", "particle burst at=0,0,10 count=5" );
		Bad( "burst missing at", "particle burst preset=sparks count=5" );
		Bad( "burst missing count", "particle burst preset=sparks at=0,0,10" );
		Bad( "burst count zero", "particle burst preset=sparks at=0,0,10 count=0" );
		Bad( "burst count negative", "particle burst preset=sparks at=0,0,10 count=-5" );
		Bad( "burst count non-integer", "particle burst preset=sparks at=0,0,10 count=10.5" );
		Bad( "burst unknown preset", "particle burst preset=nope at=0,0,10 count=5" );
		Bad( "burst malformed at, too few components", "particle burst preset=sparks at=0,0 count=5" );
		Bad( "burst malformed at, non-numeric component", "particle burst preset=sparks at=0,0,x count=5" );
		Bad( "burst wrong-form key rate", "particle burst preset=sparks at=0,0,10 count=5 rate=10" );
		Bad( "loop wrong-form key count", "particle loop preset=dust_motes at=0,0,10 rate=60 count=5" );
		Bad( "loop missing rate", "particle loop preset=dust_motes at=0,0,10" );
		Bad( "loop negative rate", "particle loop preset=dust_motes at=0,0,10 rate=-1" );
		Bad( "loop non-numeric rate", "particle loop preset=dust_motes at=0,0,10 rate=fast" );
		Bad( "loop missing at", "particle loop preset=dust_motes rate=60" );
		Bad( "loop unknown preset", "particle loop preset=nope at=0,0,10 rate=60" );
		Bad( "stop missing id", "particle stop" );
		Bad( "stop wrong-form key rate", "particle stop id=loop-1 rate=5" );
		Bad( "set-rate missing rate", "particle set-rate id=loop-1" );
		Bad( "set-rate missing id", "particle set-rate rate=30" );
		Bad( "set-rate negative rate", "particle set-rate id=loop-1 rate=-30" );
		Bad( "unterminated quote", "particle burst preset=\"sparks at=0,0,10 count=5" );
		Bad( "empty value", "particle burst preset= at=0,0,10 count=5" );
		Bad( "at and position together", "particle burst preset=sparks at=0,0,10 position=1,1,1 count=5" );

		Check( "bad: stop unknown handle fails loudly", () =>
			Throws( () => ParticleVerbPack.Run( "particle stop id=loop-9", Fresh() ), "stop loop-9" ) );
		Check( "bad: set-rate unknown handle fails loudly", () =>
			Throws( () => ParticleVerbPack.Run( "particle set-rate id=loop-9 rate=30", Fresh() ), "set-rate loop-9" ) );
		Check( "bad: stop twice fails loudly on the second stop", () =>
		{
			var reg = Fresh();
			ParticleVerbPack.Run( "particle loop preset=dust_motes at=0,0,10 rate=60", reg );
			ParticleVerbPack.Run( "particle stop id=loop-1", reg );
			Throws( () => ParticleVerbPack.Run( "particle stop id=loop-1", reg ), "second stop" );
		} );
		Check( "bad: set-rate after stop fails loudly", () =>
		{
			var reg = Fresh();
			ParticleVerbPack.Run( "particle loop preset=dust_motes at=0,0,10 rate=60", reg );
			ParticleVerbPack.Run( "particle stop id=loop-1", reg );
			Throws( () => ParticleVerbPack.Run( "particle set-rate id=loop-1 rate=30", reg ), "set-rate after stop" );
		} );
		Check( "bad: burst count over system capacity fails loudly", () =>
			Throws( () => ParticleVerbPack.Run( "particle burst preset=sparks at=0,0,10 count=10001", Fresh() ), "count 10001 over default capacity 10000" ) );
	}
}
