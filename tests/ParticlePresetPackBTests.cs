using System.Numerics;
using Marionette.Desktop;

// Wave-6 lane P25 tests. Group: particle-preset-pack-b.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   ParticlePresetPackBTests.Register( registry, settings );
// after the GpuParticleSystemTests.Register line.
//
// Every simulation check steps a real ParticleSystem built from the pack's
// own Create + SettingsFor (seed 7 unless stated), so the numbers below
// are measured motion, not restated table values. Units are kernel inches,
// Z-up, seconds; one fixed step is 1/60 s.

internal static class ParticlePresetPackBTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }

		(ParticleSystem sys, ParticleEmitter em) Build( string name, Vector3 origin, uint seed = 7, Action<ParticleSystemSettings>? tweak = null )
		{
			var baseSettings = new ParticleSystemSettings { Seed = seed };
			var s = ParticlePresetPackB.SettingsFor( name, baseSettings );
			tweak?.Invoke( s );
			var sys = new ParticleSystem( s );
			var em = ParticlePresetPackB.Create( name, origin );
			sys.AddEmitter( em );
			return (sys, em);
		}

		Vector3 MeanPos( ParticleSystem sys )
		{
			var sum = Vector3.Zero; int n = 0;
			foreach ( var p in sys.States ) if ( p.Alive != 0 ) { sum += p.Position; n++; }
			Check( n > 0, "MeanPos: no live particles" );
			return sum / n;
		}
		Vector3 MeanVel( ParticleSystem sys )
		{
			var sum = Vector3.Zero; int n = 0;
			foreach ( var p in sys.States ) if ( p.Alive != 0 ) { sum += p.Velocity; n++; }
			Check( n > 0, "MeanVel: no live particles" );
			return sum / n;
		}
		float MeanRadius( ParticleSystem sys, Vector3 origin )
		{
			double sum = 0; int n = 0;
			foreach ( var p in sys.States ) if ( p.Alive != 0 ) { sum += (p.Position - origin).Length(); n++; }
			Check( n > 0, "MeanRadius: no live particles" );
			return (float)(sum / n);
		}
		// Mean |tangential speed| around the Z axis through center: the
		// vortex signature (cross(Z, r-hat) is the swirl direction).
		float MeanTangential( ParticleSystem sys, Vector3 center )
		{
			double sum = 0; int n = 0;
			foreach ( var p in sys.States )
			{
				if ( p.Alive == 0 ) continue;
				var rp = new Vector3( p.Position.X - center.X, p.Position.Y - center.Y, 0 );
				float len = rp.Length();
				if ( len < 1e-3f ) continue;
				var tangent = new Vector3( -rp.Y / len, rp.X / len, 0 );
				sum += MathF.Abs( Vector3.Dot( p.Velocity, tangent ) ); n++;
			}
			Check( n > 0, "MeanTangential: no live off-axis particles" );
			return (float)(sum / n);
		}
		float MeanAbsZ( ParticleSystem sys, float originZ )
		{
			double sum = 0; int n = 0;
			foreach ( var p in sys.States ) if ( p.Alive != 0 ) { sum += MathF.Abs( p.Position.Z - originZ ); n++; }
			Check( n > 0, "MeanAbsZ: no live particles" );
			return (float)(sum / n);
		}

		registry.Add( "particle-preset-pack-b", "eight names, none duplicates an existing preset", () =>
		{
			Check( ParticlePresetPackB.Names.Length == 8, $"expected 8 pack-B names, got {ParticlePresetPackB.Names.Length}" );
			Check( new HashSet<string>( ParticlePresetPackB.Names ).Count == 8, "pack-B names contain a duplicate" );
			foreach ( var name in ParticlePresetPackB.Names )
			{
				Check( ParticlePresetPackB.ResolveName( name ) == name, $"pack-B preset {name} did not resolve to itself" );
				Check( Array.IndexOf( ParticleEmitterPresets.Names, name ) < 0, $"pack-B name {name} duplicates an existing preset" );
			}
			Check( ParticleEmitterPresets.Names.Length == 14, $"existing preset count moved from 14 to {ParticleEmitterPresets.Names.Length} — this lane must not edit ParticleSystem.cs" );
		} );

		registry.Add( "particle-preset-pack-b", "name resolution mirrors the existing prefix and word rules", () =>
		{
			Check( ParticlePresetPackB.ResolveName( "fx:explosion" ) == "explosion", "fx: prefix did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "fx_explosion" ) == "explosion", "fx_ prefix did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "vfx:waterfall" ) == "waterfall", "vfx: prefix did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "vfx_tornado" ) == "tornado", "vfx_ prefix did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "particle:galaxy" ) == "galaxy", "particle: prefix did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "particle_plasma" ) == "plasma", "particle_ prefix did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "EXPLOSION" ) == "explosion", "uppercase name did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "Fx:Fountain" ) == "fountain", "mixed-case prefixed name did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "big_explosion_burst" ) == "explosion", "underscore-word name did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "footstep_ash_cloud" ) == "ash", "underscore-word ash did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "the_galaxy_disc" ) == "galaxy", "underscore-word galaxy did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "big-explosion" ) == "explosion", "dash-folded name did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "tornado storm" ) == "tornado", "space-folded name did not resolve" );
			Check( ParticlePresetPackB.ResolveName( "explosions" ) is null, "bare substring 'explosions' resolved (word boundary broken)" );
			Check( ParticlePresetPackB.ResolveName( "camera_cut" ) is null, "camera_cut resolved to a preset" );
			Check( ParticlePresetPackB.ResolveName( "" ) is null, "empty cue resolved to a preset" );
			Check( ParticlePresetPackB.ResolveName( "   " ) is null, "blank cue resolved to a preset" );
			bool threw = false;
			try { ParticlePresetPackB.Create( "camera_cut", Vector3.Zero ); } catch ( ArgumentException ) { threw = true; }
			Check( threw, "Create accepted an unknown preset instead of failing loudly" );
			threw = false;
			try { ParticlePresetPackB.SettingsFor( "camera_cut", new ParticleSystemSettings() ); } catch ( ArgumentException ) { threw = true; }
			Check( threw, "SettingsFor accepted an unknown preset instead of failing loudly" );
		} );

		registry.Add( "particle-preset-pack-b", "emitter numbers match the documented table", () =>
		{
			var o = Vector3.Zero;
			var ex = ParticlePresetPackB.Create( "explosion", o );
			Check( ex.Shape == ParticleEmitterShape.Point && ex.Direction == new Vector3( 0, 0, 1 ) && ex.Speed == 500f && ex.SpeedJitter == 0.5f && ex.SpreadDegrees == 180f && ex.Bursts.Count == 1 && ex.Bursts[0].Count == 200 && ex.LifetimeMin == 0.3f && ex.LifetimeMax == 0.8f && ex.SizeMin == 0.5f && ex.SizeMax == 1.5f && ex.AngularSpeedMax == 15f, "explosion emitter off-table" );
			var fo = ParticlePresetPackB.Create( "fountain", o );
			Check( fo.Shape == ParticleEmitterShape.Point && fo.Direction == new Vector3( 0, 0, 1 ) && fo.Speed == 320f && fo.SpeedJitter == 0.25f && fo.SpreadDegrees == 18f && fo.Rate == 120f && fo.LifetimeMin == 1.5f && fo.LifetimeMax == 2.5f && fo.SizeMin == 0.8f && fo.SizeMax == 2f, "fountain emitter off-table" );
			var wa = ParticlePresetPackB.Create( "waterfall", o );
			Check( wa.Shape == ParticleEmitterShape.Area && wa.AreaU == new Vector3( 40f, 0, 0 ) && wa.AreaV == new Vector3( 0, 8f, 0 ) && !wa.UseSurfaceNormal && wa.Direction == new Vector3( 0, 0, -1 ) && wa.Speed == 20f && wa.SpeedJitter == 0.3f && wa.SpreadDegrees == 10f && wa.Rate == 300f && wa.LifetimeMin == 1.2f && wa.LifetimeMax == 2f && wa.SizeMin == 1f && wa.SizeMax == 3f, "waterfall emitter off-table" );
			var to = ParticlePresetPackB.Create( "tornado", o );
			Check( to.Shape == ParticleEmitterShape.Box && to.BoxHalfExtents == new Vector3( 12f, 12f, 60f ) && to.Direction == new Vector3( 0, 0, 1 ) && to.Speed == 60f && to.SpeedJitter == 0.5f && to.SpreadDegrees == 45f && to.Rate == 200f && to.LifetimeMin == 3f && to.LifetimeMax == 5f && to.SizeMin == 2f && to.SizeMax == 4f, "tornado emitter off-table" );
			var ga = ParticlePresetPackB.Create( "galaxy", o );
			Check( ga.Shape == ParticleEmitterShape.Box && ga.BoxHalfExtents == new Vector3( 50f, 50f, 4f ) && ga.Direction == new Vector3( 0, 0, 1 ) && ga.Speed == 5f && ga.SpeedJitter == 0.8f && ga.SpreadDegrees == 180f && ga.Rate == 60f && ga.LifetimeMin == 8f && ga.LifetimeMax == 12f && ga.SizeMin == 0.5f && ga.SizeMax == 1.2f, "galaxy emitter off-table" );
			var pl = ParticlePresetPackB.Create( "plasma", o );
			Check( pl.Shape == ParticleEmitterShape.Point && pl.Direction == new Vector3( 0, 0, 1 ) && pl.Speed == 380f && pl.SpeedJitter == 0.7f && pl.SpreadDegrees == 120f && pl.Bursts.Count == 1 && pl.Bursts[0].Count == 150 && pl.LifetimeMin == 0.2f && pl.LifetimeMax == 0.5f && pl.SizeMin == 0.2f && pl.SizeMax == 0.6f && pl.AngularSpeedMax == 20f, "plasma emitter off-table" );
			var sl = ParticlePresetPackB.Create( "slime", o );
			Check( sl.Shape == ParticleEmitterShape.Point && sl.Direction == new Vector3( 0, 0, -1 ) && sl.Speed == 15f && sl.SpeedJitter == 0.2f && sl.SpreadDegrees == 15f && sl.Rate == 30f && sl.LifetimeMin == 2f && sl.LifetimeMax == 4f && sl.SizeMin == 2.5f && sl.SizeMax == 5f, "slime emitter off-table" );
			var ash = ParticlePresetPackB.Create( "ash", o );
			Check( ash.Shape == ParticleEmitterShape.Box && ash.BoxHalfExtents == new Vector3( 60f, 60f, 10f ) && ash.Direction == new Vector3( 0, 0, -1 ) && ash.Speed == 8f && ash.SpeedJitter == 0.6f && ash.SpreadDegrees == 25f && ash.Rate == 40f && ash.LifetimeMin == 6f && ash.LifetimeMax == 10f && ash.SizeMin == 0.3f && ash.SizeMax == 0.8f, "ash emitter off-table" );
		} );

		registry.Add( "particle-preset-pack-b", "medium numbers match the table and override, never add", () =>
		{
			var baseSettings = new ParticleSystemSettings { Gravity = new Vector3( 0, 0, -999f ), Drag = 9f };
			baseSettings.KillVolumes.Add( new ParticleKillVolume() );
			ParticleSystemSettings Med( string n ) => ParticlePresetPackB.SettingsFor( n, baseSettings );
			Check( Med( "explosion" ).Gravity.Z == -386.0886f && Med( "explosion" ).Drag == 0.1f, "explosion medium off-table" );
			var fo = Med( "fountain" );
			Check( fo.Gravity.Z == -386.0886f && fo.Drag == 0.15f && fo.GroundEnabled && fo.Restitution == 0.45f && fo.Friction == 0.25f, "fountain medium off-table" );
			var wa = Med( "waterfall" );
			Check( wa.Gravity.Z == -386.0886f && wa.Drag == 1.2f && wa.TurbulenceStrength == 12f && wa.GroundEnabled && wa.Restitution == 0.2f && wa.Friction == 0.4f, "waterfall medium off-table" );
			var to = Med( "tornado" );
			Check( to.Gravity.Z == -40f && to.Drag == 0.3f && to.VortexStrength == 60000f && to.VortexRadius == 15f && to.VortexAxis == new Vector3( 0, 0, 1 ), "tornado medium off-table" );
			var ga = Med( "galaxy" );
			Check( ga.Gravity.Z == -5f && ga.Drag == 0.05f && ga.VortexStrength == 1200f && ga.VortexRadius == 40f, "galaxy medium off-table" );
			var pl = Med( "plasma" );
			Check( pl.Gravity.Z == -386.0886f && pl.Drag == 0.05f && pl.TurbulenceStrength == 120f && pl.TurbulenceFrequency == 0.5f, "plasma medium off-table" );
			var sl = Med( "slime" );
			Check( sl.Gravity.Z == -600f && sl.Drag == 2.5f && sl.GroundEnabled && sl.Restitution == 0.05f && sl.Friction == 0.9f, "slime medium off-table" );
			var ash = Med( "ash" );
			Check( ash.Gravity.Z == -25f && ash.Drag == 1.5f && ash.TurbulenceStrength == 6f && ash.Wind == new Vector3( 3f, 1f, 0f ), "ash medium off-table" );
			// Override, never add: -999 base gravity must NOT appear in any
			// result (no -999 + preset sum), and the base object is untouched.
			foreach ( var n in ParticlePresetPackB.Names )
				Check( Med( n ).Gravity.Z != -999f, $"medium for {n} leaked the base gravity" );
			Check( baseSettings.Gravity.Z == -999f && baseSettings.Drag == 9f, "SettingsFor mutated the caller's base settings" );
			Check( !ReferenceEquals( Med( "ash" ), baseSettings ), "SettingsFor returned the base object instead of a copy" );
			Check( Med( "ash" ).KillVolumes.Count == 1, "SettingsFor dropped the base kill volumes" );
		} );

		registry.Add( "particle-preset-pack-b", "explosion expands radially and dies out", () =>
		{
			var (sys, _) = Build( "explosion", Vector3.Zero );
			sys.Step( 1 );
			float r1 = MeanRadius( sys, Vector3.Zero );
			Check( sys.AliveCount == 200, $"explosion burst is not 200 alive at step 1 (got {sys.AliveCount})" );
			sys.Step( 9 );
			Check( sys.AliveCount > 0, "explosion died before step 10 (min lifetime 0.3 s = 18 steps)" );
			sys.Step( 20 );
			float r30 = MeanRadius( sys, Vector3.Zero );
			Check( r30 > 100f, $"explosion mean radius at 30 steps is only {r30} in (burst did not expand)" );
			Check( r30 > r1 * 5f, $"explosion mean radius grew only {r1} -> {r30} in (not radial expansion)" );
			sys.Step( 60 );
			Check( sys.AliveCount == 0, $"explosion still alive at 90 steps (got {sys.AliveCount}; max lifetime 0.8 s = 48 steps)" );
		} );

		registry.Add( "particle-preset-pack-b", "fountain arcs up and bounces off the ground", () =>
		{
			var origin = new Vector3( 0, 0, 20f );
			var (sys, _) = Build( "fountain", origin );
			int collisions = 0;
			sys.OnCollision += _ => collisions++;
			sys.Step( 30 );
			float z30 = MeanPos( sys ).Z;
			Check( z30 > origin.Z, $"fountain mean Z at 30 steps is {z30} (jet never rose above its {origin.Z} origin)" );
			sys.Step( 30 );
			float maxZ = float.NegativeInfinity;
			foreach ( var p in sys.States ) if ( p.Alive != 0 ) maxZ = MathF.Max( maxZ, p.Position.Z );
			Check( maxZ > origin.Z + 40f, $"fountain apex only {maxZ} (origin {origin.Z} + 40 required; jet too weak)" );
			sys.Step( 90 );
			Check( collisions > 0, "fountain recorded zero ground contacts in 150 steps (arc never reached the ground plane)" );
		} );

		registry.Add( "particle-preset-pack-b", "waterfall falls as a wide sheet, slowed by misty drag", () =>
		{
			var origin = new Vector3( 0, 0, 100f );
			var (sys, _) = Build( "waterfall", origin );
			// 60 steps, not 30: a rate emitter's mean particle age at 30
			// steps is only ~0.25 s, so the 30-step mean fall (~19 in)
			// understates the fall; at 60 steps the mean age is ~0.5 s.
			sys.Step( 60 );
			float drop = origin.Z - MeanPos( sys ).Z;
			Check( drop > 30f, $"waterfall mean fall at 60 steps is only {drop} in (sheet not falling)" );
			float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
			foreach ( var p in sys.States ) if ( p.Alive != 0 ) { minX = MathF.Min( minX, p.Position.X ); maxX = MathF.Max( maxX, p.Position.X ); }
			Check( maxX - minX > 50f, $"waterfall sheet span is only {maxX - minX} in wide (area emitter 80 in wide, not a sheet)" );
			// Misty-drag proof: same seed, drag removed, falls faster.
			// Ground is OFF in both runs here: with it on, the no-drag
			// control hits the ground early and bounces to a standstill,
			// which lowers its mean |Vz| below the preset's and hides
			// the drag effect entirely (the first cut's confound).
			var (presetNoGround, _) = Build( "waterfall", origin, 7, s => s.GroundEnabled = false );
			var (ctrl, _) = Build( "waterfall", origin, 7, s => { s.Drag = 0f; s.TurbulenceStrength = 0f; s.GroundEnabled = false; } );
			presetNoGround.Step( 60 ); ctrl.Step( 60 );
			float presetFallSpeed = MathF.Abs( MeanVel( presetNoGround ).Z ), ctrlFallSpeed = MathF.Abs( MeanVel( ctrl ).Z );
			Check( presetFallSpeed < ctrlFallSpeed - 20f, $"misty drag did not slow the fall: preset |Vz| {presetFallSpeed} vs no-drag {ctrlFallSpeed}" );
		} );

		registry.Add( "particle-preset-pack-b", "tornado spins up and rises", () =>
		{
			var (sys, _) = Build( "tornado", Vector3.Zero );
			sys.Step( 1 );
			float tang1 = MeanTangential( sys, Vector3.Zero );
			sys.Step( 59 );
			float tang60 = MeanTangential( sys, Vector3.Zero );
			float z60 = MeanPos( sys ).Z;
			Check( tang60 > 50f, $"tornado tangential speed at 60 steps is only {tang60} in/s (vortex not driving the swirl)" );
			Check( tang60 > tang1, $"tornado tangential speed did not spin up: {tang1} -> {tang60} in/s" );
			// Absolute rise, not step-1-relative: at step 1 only ~3
			// particles exist, so the step-1 mean Z (19.3 in one run) is
			// sampling noise, not a baseline. Spawn mean Z is 0.
			Check( z60 > 10f, $"tornado mean Z at 60 steps is {z60} (column not spiralling upward from its 0 centre)" );
		} );

		registry.Add( "particle-preset-pack-b", "galaxy swirls slowly and stays a thin long-lived disc", () =>
		{
			var (sys, _) = Build( "galaxy", Vector3.Zero );
			// Baseline at 30 steps, not step 1: at 60/s only 1 particle
			// exists at step 1, so a step-1 radius is one sample, not a
			// baseline. At 30 steps the disc mean radius is established.
			sys.Step( 30 );
			float r30 = MeanRadius( sys, Vector3.Zero );
			sys.Step( 90 );
			float tang = MeanTangential( sys, Vector3.Zero );
			float r120 = MeanRadius( sys, Vector3.Zero );
			float thick = MeanAbsZ( sys, 0f );
			Check( tang > 5f, $"galaxy tangential speed at 120 steps is only {tang} in/s (no gentle swirl)" );
			Check( MathF.Abs( r120 - r30 ) < 25f, $"galaxy disc radius moved {r30} -> {r120} in (disc flew apart or collapsed, not a slow spiral)" );
			Check( thick < 12f, $"galaxy disc thickness (mean |Z|) is {thick} in (disc puffed out of its 8-in slab)" );
			sys.Step( 180 );
			Check( sys.AliveCount >= 250, $"galaxy lost particles by 300 steps (got {sys.AliveCount}; min lifetime 8 s = 480 steps)" );
		} );

		registry.Add( "particle-preset-pack-b", "plasma jitters off its ballistic path and dies fast", () =>
		{
			var (sys, _) = Build( "plasma", Vector3.Zero );
			var (ctrl, _) = Build( "plasma", Vector3.Zero, 7, s => s.TurbulenceStrength = 0f );
			sys.Step( 20 ); ctrl.Step( 20 );
			Check( sys.HighWater == ctrl.HighWater, "plasma/control slot counts diverged (same-seed comparison invalid)" );
			double sum = 0; int n = 0; float max = 0;
			for ( int i = 0; i < sys.HighWater; i++ )
			{
				if ( sys.States[i].Alive == 0 ) continue;
				float d = (sys.States[i].Position - ctrl.States[i].Position).Length();
				sum += d; max = MathF.Max( max, d ); n++;
			}
			Check( n > 0, "plasma had no live particles at 20 steps" );
			Check( sum / n > 2f, $"plasma turbulence moved particles only {sum / n} in on average vs the no-turbulence control (not jittery)" );
			Check( max > 5f, $"plasma max jitter displacement is only {max} in (turbulence too weak to read)" );
			sys.Step( 40 );
			Check( sys.AliveCount == 0, $"plasma still alive at 60 steps (got {sys.AliveCount}; max lifetime 0.5 s = 30 steps)" );
		} );

		registry.Add( "particle-preset-pack-b", "slime falls heavy and sticks to the ground", () =>
		{
			var origin = new Vector3( 0, 0, 50f );
			var (sys, em) = Build( "slime", origin );
			int collisions = 0;
			sys.OnCollision += _ => collisions++;
			sys.Step( 30 );
			em.Enabled = false;
			sys.Step( 150 );
			Check( collisions > 0, "slime recorded zero ground contacts (drips never landed)" );
			Check( sys.AliveCount > 0, "slime has no survivors at 180 steps to check for sticking" );
			int stuck = 0, alive = 0;
			foreach ( var p in sys.States )
			{
				if ( p.Alive == 0 ) continue;
				alive++;
				if ( p.Position.Z < 2f && p.Velocity.Length() < 10f ) stuck++;
			}
			Check( alive > 0 && stuck * 5 >= alive * 4, $"slime stuck fraction is only {stuck}/{alive} (restitution 0.05 + friction 0.9 should pin >= 80% to the ground)" );
		} );

		registry.Add( "particle-preset-pack-b", "ash falls slowly, drifts with the wind, and lives long", () =>
		{
			var origin = new Vector3( 0, 0, 100f );
			var (sys, _) = Build( "ash", origin );
			sys.Step( 60 );
			float drop = origin.Z - MeanPos( sys ).Z;
			Check( drop > 5f && drop < 60f, $"ash mean fall at 60 steps is {drop} in (slow fall means 5–60 in; default gravity would drop ~190 in)" );
			sys.Step( 60 );
			float vx = MeanVel( sys ).X;
			Check( vx > 1f && vx < 5f, $"ash mean Vx at 120 steps is {vx} in/s (wind (3,1,0) through drag 1.5 should drift it at ~3 in/s)" );
			sys.Step( 180 );
			Check( sys.AliveCount >= 150, $"ash lost particles by 300 steps (got {sys.AliveCount}; min lifetime 6 s = 360 steps)" );
		} );

		registry.Add( "particle-preset-pack-b", "same seed steps identically; caller direction re-aims a preset", () =>
		{
			var (a, _) = Build( "explosion", Vector3.Zero, 42 );
			var (b, _) = Build( "explosion", Vector3.Zero, 42 );
			a.Step( 30 ); b.Step( 30 );
			Check( a.HighWater == b.HighWater, "determinism check: slot counts diverged" );
			for ( int i = 0; i < a.HighWater; i++ )
			{
				Check( a.States[i].Alive == b.States[i].Alive, $"determinism check: slot {i} alive flag diverged" );
				if ( a.States[i].Alive != 0 )
					Check( a.States[i].Position == b.States[i].Position && a.States[i].Velocity == b.States[i].Velocity, $"determinism check: slot {i} state diverged" );
			}
			var origin = new Vector3( 1, 2, 3 );
			Check( ParticlePresetPackB.Create( "fountain", origin ).Direction == new Vector3( 0, 0, 1 ), "fountain default direction is not +Z" );
			Check( ParticlePresetPackB.Create( "fountain", origin, new Vector3( 1, 0, 0 ) ).Direction == new Vector3( 1, 0, 0 ), "non-zero caller direction did not re-aim the preset" );
			Check( ParticlePresetPackB.Create( "fountain", origin, Vector3.Zero ).Direction == new Vector3( 0, 0, 1 ), "zero caller direction overrode the preset direction" );
			Check( ParticlePresetPackB.Create( "fountain", origin ).Position == origin, "fountain emitter not at origin" );
		} );
	}
}
