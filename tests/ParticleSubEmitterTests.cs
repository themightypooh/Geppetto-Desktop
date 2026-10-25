using System.Numerics;
using Marionette.Desktop;

internal static class ParticleSubEmitterTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		static void Throws<T>( Action f, string what ) where T : Exception
		{
			try { f(); } catch ( T ) { return; }
			throw new Exception( "expected " + typeof( T ).Name + " for " + what );
		}
		static ParticleEmitter Burst( int count, float lifetime )
		{
			return new ParticleEmitter
			{
				Bursts = new List<ParticleBurst> { new( 0, count ) },
				LifetimeMin = lifetime, LifetimeMax = lifetime, Speed = 0,
			};
		}
		static ParticleEmitter Template( float lifetime )
		{
			return new ParticleEmitter
			{
				Shape = ParticleEmitterShape.Point, Speed = 0,
				LifetimeMin = lifetime, LifetimeMax = lifetime, SizeMin = 1, SizeMax = 1,
			};
		}
		static ParticleSystem BurstSystem( ParticleEmitter em, ParticleSystemSettings? s = null )
		{
			var sys = new ParticleSystem( s ?? new ParticleSystemSettings() );
			sys.AddEmitter( em );
			return sys;
		}

		registry.Add( "particle-subemitter", "death sub-emitter spawns children at the death position, then dies out", () =>
		{
			var child = Template( 10f );
			var em = Burst( 3, 1f / 60f );
			em.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = child, Count = 4, VelocityInherit = 0 };
			var sys = BurstSystem( em );
			var deaths = new List<ParticleDeathInfo>();
			sys.OnDeath += d => deaths.Add( d );
			sys.Step();
			Check( sys.AliveCount == 3, $"step 0 should have 3 alive, got {sys.AliveCount}" );
			Check( deaths.Count == 0, $"no deaths expected at step 0, got {deaths.Count}" );
			sys.Step();
			Check( deaths.Count == 3, $"expected 3 expiry deaths at step 1, got {deaths.Count}" );
			Check( deaths.TrueForAll( d => d.Reason == ParticleDeathReason.Expired ), "deaths were not all Expired" );
			Check( sys.AliveCount == 12, $"expected 12 children after step 1, got {sys.AliveCount}" );
			Check( sys.PendingChildCount == 0, $"pending children should flush, got {sys.PendingChildCount}" );
			foreach ( var st in sys.States )
			{
				if ( st.Alive == 0 ) continue;
				bool match = false;
				foreach ( var d in deaths ) if ( st.Position == d.Position ) match = true;
				Check( match, $"child at {st.Position} does not match any death position" );
			}
			for ( int i = 0; i < 600; i++ ) sys.Step();
			Check( sys.AliveCount == 0, $"children should all expire by step 601, got {sys.AliveCount}" );
			Check( sys.SubEmitterSuppressedCount == 0, "children carry no sub-emitter, nothing should suppress" );
		} );

		registry.Add( "particle-subemitter", "collision callback fires on ground contact with ground normal", () =>
		{
			var s = new ParticleSystemSettings { GroundEnabled = true, GroundNormal = new Vector3( 0, 0, 1 ), GroundOffset = 0 };
			var em = Burst( 1, 10f );
			em.Position = new Vector3( 0, 0, 5 );
			var sys = BurstSystem( em, s );
			var hits = new List<ParticleCollisionInfo>();
			sys.OnCollision += h => hits.Add( h );
			for ( int i = 0; i < 40; i++ ) sys.Step();
			Check( hits.Count >= 1, $"expected at least one ground contact, got {hits.Count}" );
			var first = hits[0];
			Check( first.Kind == ParticleCollisionKind.Ground, $"first hit kind was {first.Kind}, not Ground" );
			Check( first.Normal == new Vector3( 0, 0, 1 ), $"ground normal was {first.Normal}" );
			Check( Math.Abs( first.Position.Z - 0.01f ) < 0.05f, $"contact Z was {first.Position.Z}, not ~0.01" );
			Check( first.Velocity.Z < 0, $"impact velocity Z was {first.Velocity.Z}, not falling" );
		} );

		registry.Add( "particle-subemitter", "die on collision kills on first contact with Collision reason", () =>
		{
			var s = new ParticleSystemSettings { GroundEnabled = true, GroundNormal = new Vector3( 0, 0, 1 ), GroundOffset = 0 };
			var em = Burst( 1, 10f );
			em.Position = new Vector3( 0, 0, 5 );
			em.DieOnCollision = true;
			var sys = BurstSystem( em, s );
			var deaths = new List<ParticleDeathInfo>();
			sys.OnDeath += d => deaths.Add( d );
			for ( int i = 0; i < 40; i++ ) sys.Step();
			Check( sys.AliveCount == 0, $"particle should be dead, got {sys.AliveCount} alive" );
			Check( deaths.Count == 1, $"expected exactly 1 death, got {deaths.Count}" );
			Check( deaths[0].Reason == ParticleDeathReason.Collision, $"death reason was {deaths[0].Reason}, not Collision" );
		} );

		registry.Add( "particle-subemitter", "collision sub-emitter replaces parent with a burst on impact", () =>
		{
			var s = new ParticleSystemSettings { GroundEnabled = true, GroundNormal = new Vector3( 0, 0, 1 ), GroundOffset = 0 };
			var em = Burst( 1, 10f );
			em.Position = new Vector3( 0, 0, 5 );
			em.DieOnCollision = true;
			em.CollisionSubEmitter = new ParticleSubEmitter { ChildTemplate = Template( 10f ), Count = 3, VelocityInherit = 0 };
			var sys = BurstSystem( em, s );
			int steps = 0;
			for ( ; steps < 40 && sys.AliveCount != 3; steps++ ) sys.Step();
			Check( sys.AliveCount == 3, $"expected parent replaced by 3 children, got {sys.AliveCount} after {steps} steps" );
			foreach ( var st in sys.States )
			{
				if ( st.Alive == 0 ) continue;
				Check( Math.Abs( st.Position.Z - 0.01f ) < 0.05f, $"child Z was {st.Position.Z}, not the contact plane ~0.01" );
				Check( Math.Abs( st.Position.X ) < 0.01f && Math.Abs( st.Position.Y ) < 0.01f, $"child not at contact point: {st.Position}" );
			}
		} );

		registry.Add( "particle-subemitter", "kill plane removes particles below it with Killed reason", () =>
		{
			var s = new ParticleSystemSettings();
			s.KillVolumes.Add( new ParticleKillVolume { Kind = ParticleKillVolumeKind.Plane, Normal = new Vector3( 0, 0, 1 ), Offset = 10 } );
			var em = Burst( 1, 10f );
			em.Position = new Vector3( 0, 0, 20 );
			var sys = BurstSystem( em, s );
			var deaths = new List<ParticleDeathInfo>();
			sys.OnDeath += d => deaths.Add( d );
			for ( int i = 0; i < 60; i++ ) sys.Step();
			Check( sys.AliveCount == 0, "particle should be killed by the plane" );
			Check( deaths.Count == 1 && deaths[0].Reason == ParticleDeathReason.Killed, $"expected 1 Killed death, got {deaths.Count} / {(deaths.Count > 0 ? deaths[0].Reason.ToString() : "none")}" );
			Check( deaths[0].Position.Z < 10 && deaths[0].Position.Z > 5, $"kill position Z was {deaths[0].Position.Z}, not just below 10" );
		} );

		registry.Add( "particle-subemitter", "kill sphere inside kills, outside spares", () =>
		{
			var inside = new ParticleSystemSettings();
			inside.KillVolumes.Add( new ParticleKillVolume { Kind = ParticleKillVolumeKind.Sphere, Center = Vector3.Zero, Radius = 5, KillInside = true } );
			var sysIn = BurstSystem( Burst( 1, 10f ), inside );
			sysIn.Step();
			Check( sysIn.AliveCount == 0, "particle spawned inside a kill-inside sphere should die on step 0" );

			var outside = new ParticleSystemSettings();
			outside.KillVolumes.Add( new ParticleKillVolume { Kind = ParticleKillVolumeKind.Sphere, Center = Vector3.Zero, Radius = 5, KillInside = false } );
			var sysOut = BurstSystem( Burst( 1, 10f ), outside );
			for ( int i = 0; i < 5; i++ ) sysOut.Step();
			Check( sysOut.AliveCount == 1, "particle inside a kill-outside sphere should survive" );
		} );

		registry.Add( "particle-subemitter", "depth cap stops grandchildren and counts suppressions", () =>
		{
			var b = Template( 10f );
			var a = Template( 1f / 60f );
			a.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = b, Count = 2, MaxDepth = 1 };
			var em = Burst( 1, 1f / 60f );
			em.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = a, Count = 2, MaxDepth = 1 };
			var sys = BurstSystem( em );
			sys.Step();
			Check( sys.AliveCount == 1, "parent should be alive at step 0" );
			sys.Step();
			Check( sys.AliveCount == 2, $"expected 2 depth-1 children at step 1, got {sys.AliveCount}" );
			sys.Step();
			Check( sys.AliveCount == 0, $"grandchildren must not spawn, got {sys.AliveCount} alive" );
			Check( sys.SubEmitterSuppressedCount == 2, $"expected 2 suppressions, got {sys.SubEmitterSuppressedCount}" );
			Check( sys.PendingChildCount == 0, "no pending children should remain" );
		} );

		registry.Add( "particle-subemitter", "depth exceeded throws when ThrowOnDepthExceeded", () =>
		{
			var b = Template( 10f );
			var a = Template( 1f / 60f );
			a.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = b, Count = 2, MaxDepth = 1, ThrowOnDepthExceeded = true };
			var em = Burst( 1, 1f / 60f );
			em.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = a, Count = 2, MaxDepth = 1 };
			var sys = BurstSystem( em );
			sys.Step();
			sys.Step();
			Throws<InvalidOperationException>( () => sys.Step(), "grandchild spawn past MaxDepth with ThrowOnDepthExceeded" );
			Check( sys.SubEmitterSuppressedCount == 1, $"suppression should count before throwing, got {sys.SubEmitterSuppressedCount}" );
		} );

		registry.Add( "particle-subemitter", "bad sub-emitter and kill-volume configs fail loudly", () =>
		{
			Throws<ArgumentException>( () =>
			{
				var em = Burst( 1, 1f );
				em.DeathSubEmitter = new ParticleSubEmitter { ChildPreset = "no_such_preset", Count = 1 };
				BurstSystem( em );
			}, "unknown child preset" );
			Throws<ArgumentException>( () =>
			{
				var em = Burst( 1, 1f );
				em.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = Template( 1f ), Count = 0 };
				BurstSystem( em );
			}, "count 0" );
			Throws<ArgumentException>( () =>
			{
				var em = Burst( 1, 1f );
				em.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = Template( 1f ), Count = 1, VelocityInherit = 1.5f };
				BurstSystem( em );
			}, "velocity inherit > 1" );
			Throws<ArgumentException>( () =>
			{
				var em = Burst( 1, 1f );
				em.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = Template( 1f ), Count = 1, MaxDepth = -1 };
				BurstSystem( em );
			}, "max depth -1" );
			Throws<ArgumentException>( () =>
			{
				var em = Burst( 1, 1f );
				em.DeathSubEmitter = new ParticleSubEmitter { ChildPreset = "sparks", ChildTemplate = Template( 1f ), Count = 1 };
				BurstSystem( em );
			}, "both preset and template" );
			Throws<ArgumentException>( () =>
			{
				var em = Burst( 1, 1f );
				em.DeathSubEmitter = new ParticleSubEmitter { Count = 1 };
				BurstSystem( em );
			}, "neither preset nor template" );
			Throws<ArgumentException>( () =>
			{
				var em = Burst( 1, 1f );
				em.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = em, Count = 1 };
				BurstSystem( em );
			}, "self-referential template" );
			Throws<ArgumentException>( () =>
			{
				var a = Template( 1f );
				var b = Template( 1f );
				a.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = b, Count = 1 };
				b.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = a, Count = 1 };
				var em = Burst( 1, 1f );
				em.DeathSubEmitter = new ParticleSubEmitter { ChildTemplate = a, Count = 1 };
				BurstSystem( em );
			}, "template cycle A->B->A" );
			Throws<ArgumentException>( () =>
			{
				var s = new ParticleSystemSettings();
				s.KillVolumes.Add( new ParticleKillVolume { Kind = ParticleKillVolumeKind.Sphere, Center = Vector3.Zero, Radius = -1 } );
				BurstSystem( Burst( 1, 1f ), s );
			}, "negative sphere radius" );
			Throws<ArgumentException>( () =>
			{
				var s = new ParticleSystemSettings();
				s.KillVolumes.Add( new ParticleKillVolume { Kind = ParticleKillVolumeKind.Plane, Normal = Vector3.Zero, Offset = 0 } );
				BurstSystem( Burst( 1, 1f ), s );
			}, "zero plane normal" );
		} );

		registry.Add( "particle-subemitter", "idle new features do not perturb the sim; same seed is bit-identical", () =>
		{
			static List<string> Dump( ParticleSystem sys )
			{
				var dump = new List<string>();
				foreach ( var st in sys.States )
					dump.Add( $"{st.Alive}|{st.Position.X},{st.Position.Y},{st.Position.Z}|{st.Velocity.X},{st.Velocity.Y},{st.Velocity.Z}|{st.Age}" );
				return dump;
			}
			ParticleEmitter PlainEmitter()
			{
				var em = Burst( 20, 10f );
				em.Speed = 5;
				return em;
			}
			var settings = new ParticleSystemSettings { TurbulenceStrength = 5, TurbulenceFrequency = 0.1f };
			var a = BurstSystem( PlainEmitter(), settings );
			var a2 = BurstSystem( PlainEmitter(), new ParticleSystemSettings { TurbulenceStrength = 5, TurbulenceFrequency = 0.1f } );
			var bSettings = new ParticleSystemSettings { TurbulenceStrength = 5, TurbulenceFrequency = 0.1f };
			bSettings.KillVolumes.Add( new ParticleKillVolume { Kind = ParticleKillVolumeKind.Plane, Normal = new Vector3( 0, 0, 1 ), Offset = -1000 } );
			var bEmitter = PlainEmitter();
			bEmitter.DeathSubEmitter = new ParticleSubEmitter { ChildPreset = "sparks", Count = 1 };
			var b = BurstSystem( bEmitter, bSettings );
			for ( int i = 0; i < 30; i++ ) { a.Step(); a2.Step(); b.Step(); }
			var da = Dump( a );
			var da2 = Dump( a2 );
			var db = Dump( b );
			Check( da.Count == da2.Count, "determinism dump lengths differ" );
			for ( int i = 0; i < da.Count; i++ ) Check( da[i] == da2[i], $"same-seed determinism broke at slot {i}" );
			Check( da.Count == db.Count, "idle-features dump lengths differ" );
			for ( int i = 0; i < da.Count; i++ ) Check( da[i] == db[i], $"idle kill volume / death sub-emitter perturbed slot {i}" );
			Check( a.AliveCount == b.AliveCount && a.TotalEmitted == b.TotalEmitted, "alive/emitted counts differ with idle features" );
		} );

		registry.Add( "particle-subemitter", "preset child spawns from a death", () =>
		{
			var em = Burst( 1, 1f / 60f );
			em.DeathSubEmitter = new ParticleSubEmitter { ChildPreset = "sparks", Count = 2, VelocityInherit = 0.5f };
			var sys = BurstSystem( em );
			var deaths = new List<ParticleDeathInfo>();
			sys.OnDeath += d => deaths.Add( d );
			sys.Step();
			sys.Step();
			Check( deaths.Count == 1, $"expected 1 death, got {deaths.Count}" );
			Check( sys.AliveCount == 2, $"expected 2 preset children, got {sys.AliveCount}" );
			foreach ( var st in sys.States )
			{
				if ( st.Alive == 0 ) continue;
				Check( st.Position == deaths[0].Position, $"preset child at {st.Position} not at death position {deaths[0].Position}" );
			}
		} );

		registry.Add( "particle-subemitter", "mesh collision callback fires with mesh normal", () =>
		{
			var mesh = new ParticleMesh(
				new[] { new Vector3( 0, 0, 0 ), new Vector3( 10, 0, 0 ), new Vector3( 0, 10, 0 ) },
				new[] { 0, 1, 2 } );
			var s = new ParticleSystemSettings { CollisionMesh = mesh };
			var em = Burst( 1, 10f );
			em.Position = new Vector3( 1, 1, 5 );
			var sys = BurstSystem( em, s );
			var hits = new List<ParticleCollisionInfo>();
			sys.OnCollision += h => hits.Add( h );
			for ( int i = 0; i < 40; i++ ) sys.Step();
			Check( hits.Count >= 1, $"expected at least one mesh contact, got {hits.Count}" );
			var first = hits[0];
			Check( first.Kind == ParticleCollisionKind.Mesh, $"first hit kind was {first.Kind}, not Mesh" );
			Check( first.Normal.Z > 0.9f, $"mesh normal was {first.Normal}, not +Z" );
			Check( Math.Abs( first.Position.Z - 0.01f ) < 0.05f, $"mesh contact Z was {first.Position.Z}, not ~0.01" );
		} );
	}
}
