using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleBakeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		string work = settings.WorkDirectory;
		void Check( string name, Action test ) => registry.Add( "particle-bake", name, test );
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Action action, string label )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( $"expected an error for {label}, got silent success" );
		}
		void Close( float a, float b, string label )
		{
			if ( Math.Abs( a - b ) > 1e-4f ) throw new Exception( $"{label}: {a} != {b}" );
		}
		void CloseVec( float[] a, Vector3 b, string label )
		{
			Assert( a is not null && a.Length == 3, label + ": not a 3-vector" );
			Close( a[0], b.X, label + ".x" ); Close( a[1], b.Y, label + ".y" ); Close( a[2], b.Z, label + ".z" );
		}

		ParticleSystemDef SampleDef()
		{
			var def = new ParticleSystemDef
			{
				Name = "sample",
				Settings = new ParticleSettingsDef
				{
					Capacity = 500,
					Seed = 42,
					Gravity = new[] { 0f, 0f, 0f },
				},
			};
			def.Emitters.Add( new ParticleEmitterDef
			{
				Shape = "Point",
				Position = new[] { 0f, 0f, 10f },
				Rate = 0f,
				LifetimeMin = 10f,
				LifetimeMax = 10f,
				SizeMin = 1f,
				SizeMax = 1f,
				Direction = new[] { 0f, 0f, 1f },
				Speed = 0f,
				SpreadDegrees = 0f,
			} );
			def.Emitters[0].Bursts = new List<ParticleBurstDef> { new() { Step = 0, Count = 3 } };
			return def;
		}

		ParticleSystemDef DefWithSeed( uint seed )
		{
			var def = SampleDef();
			def.Settings.Seed = seed;
			return def;
		}

		void AssertHashFlips( string label, Action<ParticleSystemDef> mutate )
		{
			string baseHash = ParticleBake.DefinitionHash( SampleDef() );
			var flipped = SampleDef();
			mutate( flipped );
			string flippedHash = ParticleBake.DefinitionHash( flipped );
			Assert( flippedHash != baseHash, $"hash did not change for {label}" );
			Assert( flippedHash.Length == 64, $"hash length {flippedHash.Length} for {label}" );
		}

		// Frames are P40 records: compare every carried field with == (bit-identical, no tolerance).
		void AssertSameFrames( IReadOnlyList<ParticleBakeFrame> a, IReadOnlyList<ParticleBakeFrame> b, string label )
		{
			Assert( a.Count == b.Count, $"{label}: frame count {a.Count} != {b.Count}" );
			for ( int f = 0; f < a.Count; f++ )
			{
				Assert( a[f].Index == b[f].Index, $"{label}: frame {f} index differs" );
				Assert( a[f].Particles.Count == b[f].Particles.Count, $"{label}: frame {f} particle count {a[f].Particles.Count} != {b[f].Particles.Count}" );
				for ( int i = 0; i < a[f].Particles.Count; i++ )
				{
					var x = a[f].Particles[i]; var y = b[f].Particles[i];
					Assert( x.Position[0] == y.Position[0] && x.Position[1] == y.Position[1] && x.Position[2] == y.Position[2], $"{label}: frame {f} particle {i} position differs" );
					Assert( x.Velocity[0] == y.Velocity[0] && x.Velocity[1] == y.Velocity[1] && x.Velocity[2] == y.Velocity[2], $"{label}: frame {f} particle {i} velocity differs" );
					Assert( x.Size == y.Size, $"{label}: frame {f} particle {i} size differs" );
					Assert( x.Age == y.Age, $"{label}: frame {f} particle {i} age differs" );
					Assert( x.Alive == y.Alive, $"{label}: frame {f} particle {i} alive differs" );
				}
			}
		}

		Check( "hash-stable-and-golden", () =>
		{
			string a = ParticleBake.DefinitionHash( SampleDef() );
			string b = ParticleBake.DefinitionHash( SampleDef() );
			Assert( a == b, "same definition hashed differently across calls" );
			Assert( a.Length == 64, $"hash length {a.Length}, expected 64 hex chars" );
			foreach ( char c in a ) Assert( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ), $"hash char '{c}' is not lowercase hex" );
			Assert( a == GoldenHash, $"golden hash mismatch, actual={a}" );
		} );

		Check( "hash-flips-settings", () =>
		{
			AssertHashFlips( "seed", d => d.Settings.Seed = 43 );
			AssertHashFlips( "capacity", d => d.Settings.Capacity = 501 );
			AssertHashFlips( "fixedDeltaTime", d => d.Settings.FixedDeltaTime = 1f / 30f );
			AssertHashFlips( "substeps", d => d.Settings.Substeps = 2 );
			AssertHashFlips( "gravity", d => d.Settings.Gravity[0] = 1f );
			AssertHashFlips( "wind", d => d.Settings.Wind[2] = 5f );
			AssertHashFlips( "drag", d => d.Settings.Drag = 0.5f );
			AssertHashFlips( "turbulenceStrength", d => d.Settings.TurbulenceStrength = 2f );
			AssertHashFlips( "turbulenceFrequency", d => d.Settings.TurbulenceFrequency = 0.2f );
			AssertHashFlips( "turbulenceSpeed", d => d.Settings.TurbulenceSpeed = 1f );
			AssertHashFlips( "vortexStrength", d => d.Settings.VortexStrength = 3f );
			AssertHashFlips( "vortexCenter", d => d.Settings.VortexCenter[1] = 4f );
			AssertHashFlips( "vortexAxis", d => d.Settings.VortexAxis[0] = 1f );
			AssertHashFlips( "vortexRadius", d => d.Settings.VortexRadius = 2f );
			AssertHashFlips( "groundEnabled", d => d.Settings.GroundEnabled = true );
			AssertHashFlips( "groundNormal", d => d.Settings.GroundNormal[2] = 0.5f );
			AssertHashFlips( "groundOffset", d => d.Settings.GroundOffset = -1f );
			AssertHashFlips( "collisionRadius", d => d.Settings.CollisionRadius = 0.05f );
			AssertHashFlips( "restitution", d => d.Settings.Restitution = 0.9f );
			AssertHashFlips( "friction", d => d.Settings.Friction = 0.5f );
			AssertHashFlips( "renderType", d => d.Settings.RenderType = "MeshInstance" );
			AssertHashFlips( "strandPoints", d => d.Settings.StrandPoints = 4 );
		} );

		Check( "hash-flips-emitters", () =>
		{
			AssertHashFlips( "shape", d => d.Emitters[0].Shape = "Box" );
			AssertHashFlips( "position", d => d.Emitters[0].Position![0] = 1f );
			AssertHashFlips( "areaU", d => d.Emitters[0].AreaU = new[] { 2f, 0f, 0f } );
			AssertHashFlips( "areaV", d => d.Emitters[0].AreaV = new[] { 0f, 2f, 0f } );
			AssertHashFlips( "boxHalfExtents", d => d.Emitters[0].BoxHalfExtents = new[] { 2f, 2f, 2f } );
			AssertHashFlips( "sphereRadius", d => d.Emitters[0].SphereRadius = 7f );
			AssertHashFlips( "rate", d => d.Emitters[0].Rate = 9f );
			AssertHashFlips( "burst-step", d => d.Emitters[0].Bursts![0].Step = 1 );
			AssertHashFlips( "burst-count", d => d.Emitters[0].Bursts![0].Count = 4 );
			AssertHashFlips( "lifetimeMin", d => d.Emitters[0].LifetimeMin = 9f );
			AssertHashFlips( "lifetimeMax", d => d.Emitters[0].LifetimeMax = 11f );
			AssertHashFlips( "sizeMin", d => d.Emitters[0].SizeMin = 0.5f );
			AssertHashFlips( "sizeMax", d => d.Emitters[0].SizeMax = 2f );
			AssertHashFlips( "direction", d => d.Emitters[0].Direction![0] = 1f );
			AssertHashFlips( "useSurfaceNormal", d => d.Emitters[0].UseSurfaceNormal = false );
			AssertHashFlips( "speed", d => d.Emitters[0].Speed = 5f );
			AssertHashFlips( "speedJitter", d => d.Emitters[0].SpeedJitter = 0.5f );
			AssertHashFlips( "spreadDegrees", d => d.Emitters[0].SpreadDegrees = 45f );
			AssertHashFlips( "baseVelocity", d => d.Emitters[0].BaseVelocity = new[] { 0f, 1f, 0f } );
			AssertHashFlips( "angularSpeedMax", d => d.Emitters[0].AngularSpeedMax = 3f );
			AssertHashFlips( "enabled", d => d.Emitters[0].Enabled = false );
		} );

		Check( "hash-flips-identity-and-preset", () =>
		{
			AssertHashFlips( "def-name", d => d.Name = "other" );
			AssertHashFlips( "version", d => d.Version = 2 );
			AssertHashFlips( "emitter-name", d => d.Emitters[0].Name = "main" );
			AssertHashFlips( "preset", d => d.Emitters[0].Preset = "sparks" );
			string h1 = ParticleBake.DefinitionHash( SampleDef() );
			string h2 = ParticleBake.DefinitionHash( SampleDef() );
			Assert( h1 == h2, "identical defs must hash identically" );
		} );

		Check( "hash-mesh-and-order", () =>
		{
			var withMesh = SampleDef();
			withMesh.Emitters[0].Mesh = new ParticleMeshDef
			{
				Vertices = new List<float[]> { new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f } },
				Indices = new List<int> { 0, 1, 2 },
			};
			string baseHash = ParticleBake.DefinitionHash( withMesh );
			var flipVert = SampleDef();
			flipVert.Emitters[0].Mesh = new ParticleMeshDef
			{
				Vertices = new List<float[]> { new[] { 0f, 0f, 0f }, new[] { 2f, 0f, 0f }, new[] { 0f, 1f, 0f } },
				Indices = new List<int> { 0, 2, 1 },
			};
			Assert( ParticleBake.DefinitionHash( flipVert ) != baseHash, "mesh vertex flip did not change hash" );
			var flipIdx = SampleDef();
			flipIdx.Emitters[0].Mesh = new ParticleMeshDef
			{
				Vertices = new List<float[]> { new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f } },
				Indices = new List<int> { 0, 2, 1 },
			};
			Assert( ParticleBake.DefinitionHash( flipIdx ) != baseHash, "mesh index flip did not change hash" );
			var two = SampleDef();
			two.Emitters.Add( new ParticleEmitterDef { Shape = "Point", Position = new[] { 5f, 5f, 5f }, Rate = 0f, LifetimeMin = 1f, LifetimeMax = 1f, SizeMin = 1f, SizeMax = 1f } );
			var swapped = SampleDef();
			swapped.Emitters.Add( new ParticleEmitterDef { Shape = "Point", Position = new[] { 5f, 5f, 5f }, Rate = 0f, LifetimeMin = 1f, LifetimeMax = 1f, SizeMin = 1f, SizeMax = 1f } );
			var tmp = swapped.Emitters[0];
			swapped.Emitters[0] = swapped.Emitters[1];
			swapped.Emitters[1] = tmp;
			Assert( ParticleBake.DefinitionHash( two ) != ParticleBake.DefinitionHash( swapped ), "emitter order swap did not change hash" );
		} );

		Check( "hash-null-vs-empty-bursts", () =>
		{
			var withNull = SampleDef();
			withNull.Emitters[0].Bursts = null;
			var withEmpty = SampleDef();
			withEmpty.Emitters[0].Bursts = new List<ParticleBurstDef>();
			Assert( ParticleBake.DefinitionHash( withNull ) != ParticleBake.DefinitionHash( withEmpty ), "null bursts and empty bursts hashed identically" );
		} );

		Check( "hash-unknown-json-fields-ignored", () =>
		{
			string json = ParticleAuthoring.ToJson( SampleDef() );
			Assert( json.Contains( "\"version\": 1", StringComparison.Ordinal ), "lane-5 JSON shape changed; cannot inject unknown field" );
			string json2 = json.Replace( "\"version\": 1", "\"version\": 1,\n  \"bogusField\": 123", StringComparison.Ordinal );
			Assert( json2 != json, "unknown-field injection did not change the document" );
			var a = ParticleAuthoring.FromJson( json );
			var b = ParticleAuthoring.FromJson( json2 );
			Assert( ParticleBake.DefinitionHash( a ) == ParticleBake.DefinitionHash( b ), "unknown JSON fields must not change the hash (hash is over the deserialized def)" );
		} );

		Check( "hash-live-system", () =>
		{
			var system = SampleDef().Build();
			string h1 = ParticleBake.DefinitionHash( system );
			Assert( h1.Length == 64, "live hash length wrong" );
			system.Settings.Gravity = new Vector3( 0, 0, -10 );
			string h2 = ParticleBake.DefinitionHash( system );
			Assert( h2 != h1, "live hash did not change after a settings change" );
			system.Settings.KillVolumes.Add( new ParticleKillVolume { Kind = ParticleKillVolumeKind.Plane, Normal = new Vector3( 0, 0, 1 ), Offset = 5f } );
			string h3 = ParticleBake.DefinitionHash( system );
			Assert( h3 != h2, "live hash did not change after adding a kill volume" );
			Assert( ParticleBake.DefinitionHash( system ) == h3, "live hash not stable" );
		} );

		Check( "bake-known-frame-contents", () =>
		{
			var frames = ParticleBake.Bake( SampleDef(), 2, 60f );
			Assert( frames.Count == 2, $"frame count {frames.Count}, expected 2" );
			float dt = 1f / 60f;
			Assert( frames[0].Index == 0 && frames[1].Index == 1, "frame indices must be sequential from 0" );
			Assert( frames[0].Particles.Count == 3, $"frame0 particles {frames[0].Particles.Count}, expected 3 (step-0 burst)" );
			foreach ( var p in frames[0].Particles )
			{
				CloseVec( p.Position, new Vector3( 0, 0, 10 ), "frame0 position (zero velocity, zero gravity must not move)" );
				Close( p.Size, 1f, "frame0 size" );
				Close( p.Age, dt, "frame0 age" );
				CloseVec( p.Velocity, Vector3.Zero, "frame0 velocity" );
				Assert( p.Alive, "frame0 particle must be alive" );
			}
			Assert( frames[1].Particles.Count == 3, "frame1 lost particles" );
			foreach ( var p in frames[1].Particles ) Close( p.Age, 2f * dt, "frame1 age" );
			// Zero velocity + zero gravity: positions identical across frames.
			for ( int i = 0; i < 3; i++ )
				CloseVec( frames[1].Particles[i].Position, new Vector3( 0, 0, 10 ), "frame1 position" );
		} );

		Check( "bake-matches-live-system", () =>
		{
			var def = SampleDef();
			def.Emitters[0].Speed = 10f;
			def.Emitters[0].AngularSpeedMax = 0f;
			var frames = ParticleBake.Bake( def, 2, 60f );
			var live = def.Build();
			live.Step( 2 );
			var states = live.States;
			int alive = 0;
			for ( int s = 0; s < states.Length; s++ ) if ( states[s].Alive != 0 ) alive++;
			Assert( alive == frames[1].Particles.Count, $"live alive {alive} != baked {frames[1].Particles.Count}" );
			int j = 0;
			for ( int s = 0; s < states.Length; s++ )
			{
				if ( states[s].Alive == 0 ) continue;
				var p = frames[1].Particles[j++];
				Assert( p.Position[0] == states[s].Position.X && p.Position[1] == states[s].Position.Y && p.Position[2] == states[s].Position.Z, "position mismatch vs live" );
				Assert( p.Velocity[0] == states[s].Velocity.X && p.Velocity[1] == states[s].Velocity.Y && p.Velocity[2] == states[s].Velocity.Z, "velocity mismatch vs live" );
				Assert( p.Age == states[s].Age && p.Size == states[s].Size, "age/size mismatch vs live" );
				Assert( p.Alive, "baked particle must be alive" );
			}
		} );

		Check( "bake-deterministic-bit-identical", () =>
		{
			var a = ParticleBake.Bake( SampleDef(), 3, 60f );
			var b = ParticleBake.Bake( SampleDef(), 3, 60f );
			AssertSameFrames( a, b, "deterministic bake" );
		} );

		Check( "bake-stride-rule", () =>
		{
			var frames = ParticleBake.Bake( SampleDef(), 6, 60f, 3 );
			Assert( frames.Count == 2, $"stride-3 bake of 6 steps must give 2 frames, got {frames.Count}" );
			Assert( frames[0].Index == 0 && frames[1].Index == 1, "stride frame indices must be sequential from 0" );
			float dt = 1f / 60f;
			Close( frames[0].Particles[0].Age, 3f * dt, "stride frame0 age (after step 3)" );
			Close( frames[1].Particles[0].Age, 6f * dt, "stride frame1 age (after step 6)" );
			// Stride frames must equal the corresponding every-step frames, exactly.
			var every = ParticleBake.Bake( SampleDef(), 6, 60f );
			AssertSameFrames( new[] { new ParticleBakeFrame { Index = 0, Particles = every[2].Particles } }, new[] { new ParticleBakeFrame { Index = 0, Particles = frames[0].Particles } }, "stride frame0 vs every-step frame 2" );
			AssertSameFrames( new[] { new ParticleBakeFrame { Index = 0, Particles = every[5].Particles } }, new[] { new ParticleBakeFrame { Index = 0, Particles = frames[1].Particles } }, "stride frame1 vs every-step frame 5" );
			// Partial stride: 10 steps at stride 3 captures steps 3, 6, 9, plus a final frame at step 10.
			var partial = ParticleBake.Bake( SampleDef(), 10, 60f, 3 );
			Assert( partial.Count == 4, $"stride-3 bake of 10 steps must give 4 frames, got {partial.Count}" );
			Close( partial[3].Particles[0].Age, 10f * dt, "partial stride final frame age (after step 10)" );
		} );

		Check( "bake-velocity", () =>
		{
			var def = SampleDef();
			def.Emitters[0].Speed = 10f;
			def.Emitters[0].AngularSpeedMax = 0f;
			var frames = ParticleBake.Bake( def, 1, 60f );
			Assert( frames[0].Particles.Count == 3, "velocity bake lost particles" );
			foreach ( var p in frames[0].Particles )
			{
				CloseVec( p.Velocity, new Vector3( 0, 0, 10 ), "launch velocity" );
				Close( p.Position[2], 10f + 10f / 60f, "position after one step at speed 10" );
			}
		} );

		Check( "bake-particle-death", () =>
		{
			var def = SampleDef();
			def.Emitters[0].LifetimeMin = 0.05f;
			def.Emitters[0].LifetimeMax = 0.05f;
			var frames = ParticleBake.Bake( def, 4, 60f );
			Assert( frames[0].Particles.Count == 3, "death bake frame0 empty" );
			Assert( frames[1].Particles.Count == 3, "death bake frame1 empty" );
			Assert( frames[2].Particles.Count == 3, "death bake frame2 empty" );
			Assert( frames[3].Particles.Count == 0, $"lifetime 0.05s must be dead by frame3, got {frames[3].Particles.Count}" );
		} );

		Check( "bake-empty-system-and-zero-steps", () =>
		{
			var empty = new ParticleSystemDef { Name = "empty", Settings = new ParticleSettingsDef { Capacity = 100, Seed = 1, Gravity = new[] { 0f, 0f, 0f } } };
			var frames = ParticleBake.Bake( empty, 2, 60f );
			Assert( frames.Count == 2 && frames[0].Particles.Count == 0 && frames[1].Particles.Count == 0, "emitterless bake must give empty frames" );
			Throws( () => ParticleBake.Bake( SampleDef(), 0, 60f ), "steps 0" );
			Throws( () => ParticleBake.Bake( SampleDef(), -1, 60f ), "steps negative" );
		} );

		Check( "bake-framerate-does-not-resample", () =>
		{
			// Same steps, different frameRate stamp: identical sim frames (frameRate is playback metadata, not a resampler).
			var a = ParticleBake.Bake( SampleDef(), 3, 60f );
			var b = ParticleBake.Bake( SampleDef(), 3, 30f );
			AssertSameFrames( a, b, "frameRate must not change the sim frames" );
			var dump = ParticleBake.BakeDump( SampleDef(), 3, 30f );
			Assert( dump.FrameRate == 30f, "dump must carry the requested frameRate" );
			Assert( dump.FrameCount == 3, "dump frame count must equal steps at stride 1" );
		} );

		Check( "cache-hit-no-resimulation", () =>
		{
			var cache = new ParticleBakeCache();
			var a = cache.BakeCached( SampleDef(), 2, 60f );
			Assert( cache.CacheMisses == 1 && cache.CacheHits == 0, $"after first bake misses={cache.CacheMisses} hits={cache.CacheHits}" );
			Assert( cache.StepsSimulated == 2, $"steps after first bake {cache.StepsSimulated}, expected 2" );
			Assert( cache.Count == 1, $"cache count {cache.Count}" );
			var b = cache.BakeCached( SampleDef(), 2, 60f );
			Assert( cache.CacheHits == 1 && cache.CacheMisses == 1, $"after second bake hits={cache.CacheHits} misses={cache.CacheMisses}" );
			Assert( cache.StepsSimulated == 2, $"cache hit re-simulated: steps {cache.StepsSimulated}, expected still 2" );
			Assert( ReferenceEquals( a, b ), "cache hit did not return the stored instance" );
			AssertSameFrames( a, b, "cache hit frames" );
			Assert( cache.Contains( SampleDef(), 2, 60f ), "Contains missed a cached entry" );
		} );

		Check( "cache-distinct-keys", () =>
		{
			var cache = new ParticleBakeCache();
			cache.BakeCached( SampleDef(), 2, 60f );
			cache.BakeCached( SampleDef(), 1, 60f );
			Assert( cache.Count == 2 && cache.CacheMisses == 2, "same def at different steps must be two entries" );
			Assert( cache.StepsSimulated == 3, $"steps {cache.StepsSimulated}, expected 2+1" );
			cache.BakeCached( SampleDef(), 2, 30f );
			Assert( cache.Count == 3 && cache.StepsSimulated == 5, "same def at different frameRate must be a third entry" );
			cache.BakeCached( SampleDef(), 2, 60f, 2 );
			Assert( cache.Count == 4 && cache.StepsSimulated == 7, "same def at different frameStride must be a fourth entry" );
			cache.BakeCached( DefWithSeed( 7 ), 2, 60f );
			Assert( cache.Count == 5, "different seed must be a separate entry" );
			cache.BakeCached( SampleDef(), 2, 60f );
			Assert( cache.CacheHits == 1, "repeat of first key must hit" );
		} );

		Check( "cache-eviction-fifo", () =>
		{
			var cache = new ParticleBakeCache( 2 );
			cache.BakeCached( DefWithSeed( 1 ), 1, 60f );
			cache.BakeCached( DefWithSeed( 2 ), 1, 60f );
			cache.BakeCached( DefWithSeed( 3 ), 1, 60f );
			Assert( cache.Count == 2, $"cache over bound: count {cache.Count}" );
			Assert( !cache.Contains( DefWithSeed( 1 ), 1, 60f ), "oldest entry (seed 1) was not evicted first" );
			Assert( cache.Contains( DefWithSeed( 2 ), 1, 60f ) && cache.Contains( DefWithSeed( 3 ), 1, 60f ), "newer entries missing after eviction" );
			long before = cache.StepsSimulated;
			cache.BakeCached( DefWithSeed( 1 ), 1, 60f );
			Assert( cache.StepsSimulated == before + 1, "re-baking an evicted entry must be a miss (re-simulate)" );
			Assert( cache.CacheMisses == 4, $"misses {cache.CacheMisses}, expected 4" );
			cache.BakeCached( DefWithSeed( 3 ), 1, 60f );
			Assert( cache.CacheHits == 1, "surviving entry must still hit after eviction round" );
		} );

		Check( "cache-clear", () =>
		{
			var cache = new ParticleBakeCache();
			cache.BakeCached( SampleDef(), 2, 60f );
			Assert( cache.Count == 1, "pre-clear count wrong" );
			long missesBefore = cache.CacheMisses;
			cache.CacheClear();
			Assert( cache.Count == 0 && !cache.Contains( SampleDef(), 2, 60f ), "CacheClear did not empty the cache" );
			cache.BakeCached( SampleDef(), 2, 60f );
			Assert( cache.CacheMisses == missesBefore + 1, "re-bake after clear must be a miss" );
		} );

		Check( "cache-clear-order-bug", () =>
		{
			// First draft cleared entries but not insertion order: re-adding in a
			// different order, then evicting, could evict a live re-added entry.
			// Correct FIFO after clear must evict the oldest POST-clear entry.
			var cache = new ParticleBakeCache( 2 );
			cache.BakeCached( DefWithSeed( 1 ), 1, 60f );
			cache.BakeCached( DefWithSeed( 2 ), 1, 60f );
			cache.CacheClear();
			cache.BakeCached( DefWithSeed( 2 ), 1, 60f );
			cache.BakeCached( DefWithSeed( 1 ), 1, 60f );
			cache.BakeCached( DefWithSeed( 3 ), 1, 60f );
			Assert( cache.Count == 2, $"post-clear cache count {cache.Count}, expected 2" );
			Assert( !cache.Contains( DefWithSeed( 2 ), 1, 60f ), "oldest post-clear entry (seed 2) was not evicted first" );
			Assert( cache.Contains( DefWithSeed( 1 ), 1, 60f ) && cache.Contains( DefWithSeed( 3 ), 1, 60f ), "live re-added entries missing after post-clear eviction" );
		} );

		Check( "cache-bound-validation", () =>
		{
			Throws( () => new ParticleBakeCache( 0 ), "maxEntries 0" );
			Throws( () => new ParticleBakeCache( -3 ), "maxEntries negative" );
			var one = new ParticleBakeCache( 1 );
			one.BakeCached( DefWithSeed( 1 ), 1, 60f );
			one.BakeCached( DefWithSeed( 2 ), 1, 60f );
			Assert( one.Count == 1 && !one.Contains( DefWithSeed( 1 ), 1, 60f ), "bound-1 cache must hold only the newest entry" );
		} );

		Check( "validate-counts-and-rate", () =>
		{
			Throws( () => ParticleBake.Validate( SampleDef(), 0, 60f ), "zero steps" );
			Throws( () => ParticleBake.Validate( SampleDef(), -1, 60f ), "negative steps" );
			Throws( () => ParticleBake.Validate( SampleDef(), 1, 0f ), "zero frameRate" );
			Throws( () => ParticleBake.Validate( SampleDef(), 1, -30f ), "negative frameRate" );
			Throws( () => ParticleBake.Validate( SampleDef(), 1, float.NaN ), "NaN frameRate" );
			Throws( () => ParticleBake.Validate( SampleDef(), 1, 60f, 0 ), "zero frameStride" );
			Throws( () => ParticleBake.Validate( SampleDef(), 1, 60f, -2 ), "negative frameStride" );
			Throws( () => ParticleBake.Bake( SampleDef(), 0, 60f ), "Bake zero steps" );
			Throws( () => ParticleBake.Bake( SampleDef(), 1, 0f ), "Bake zero frameRate" );
			ParticleBake.Validate( SampleDef(), 1, 60f );
		} );

		Check( "validate-bad-settings", () =>
		{
			var bad = SampleDef(); bad.Settings.Capacity = 0;
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "Capacity 0" );
			bad = SampleDef(); bad.Settings.Restitution = 1.5f;
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "Restitution > 1" );
			bad = SampleDef(); bad.Settings.FixedDeltaTime = 0f;
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "FixedDeltaTime 0" );
			bad = SampleDef(); bad.Settings.Drag = -1f;
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "Drag < 0" );
			bad = SampleDef(); bad.Settings.Substeps = 0;
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "Substeps 0" );
		} );

		Check( "validate-bad-emitters", () =>
		{
			var bad = SampleDef(); bad.Emitters[0].Rate = -1f;
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "Rate < 0" );
			bad = SampleDef(); bad.Emitters[0].LifetimeMin = 0f;
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "LifetimeMin 0" );
			bad = SampleDef(); bad.Emitters[0].SpreadDegrees = 200f;
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "SpreadDegrees > 180" );
			bad = SampleDef(); bad.Emitters[0].SpeedJitter = 2f;
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "SpeedJitter > 1" );
			bad = SampleDef(); bad.Emitters[0].Preset = "no_such_preset";
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "unknown preset" );
			bad = SampleDef(); bad.Emitters[0].Shape = "MeshSurface";
			Throws( () => ParticleBake.Validate( bad, 1, 60f ), "MeshSurface without mesh" );
		} );

		Check( "validate-null-and-version", () =>
		{
			Throws( () => ParticleBake.Validate( null!, 1, 60f ), "null def" );
			Throws( () => ParticleBake.DefinitionHash( (ParticleSystemDef)null! ), "null def hash" );
			var v2 = SampleDef(); v2.Version = 2;
			Throws( () => ParticleBake.Validate( v2, 1, 60f ), "version 2" );
			Throws( () => ParticleBake.Bake( v2, 1, 60f ), "Bake version 2" );
			string h = ParticleBake.DefinitionHash( v2 );
			Assert( h.Length == 64, "hash of an invalid def must still be a pure data hash" );
		} );

		Check( "validate-null-settings-and-emitters", () =>
		{
			var noSettings = SampleDef(); noSettings.Settings = null!;
			Throws( () => ParticleBake.Validate( noSettings, 1, 60f ), "null settings" );
			var noEmitters = SampleDef(); noEmitters.Emitters = null!;
			Throws( () => ParticleBake.Validate( noEmitters, 1, 60f ), "null emitters" );
			var nullEmitter = SampleDef(); nullEmitter.Emitters[0] = null!;
			Throws( () => ParticleBake.Validate( nullEmitter, 1, 60f ), "null emitter entry" );
		} );

		Check( "bake-rejects-invalid", () =>
		{
			var bad = SampleDef(); bad.Settings.Capacity = 0;
			Throws( () => ParticleBake.Bake( bad, 1, 60f ), "Bake bad settings" );
			var cache = new ParticleBakeCache();
			Throws( () => cache.BakeCached( bad, 1, 60f ), "cached Bake bad settings" );
			Assert( cache.Count == 0 && cache.CacheMisses == 0, "failed bake must not enter the cache or count as a miss" );
		} );

		Check( "bake-from-json-missing-seed", () =>
		{
			// Same def as SampleDef, but the JSON omits settings.seed: an Error here,
			// even though lane 5 would silently default it to 1.
			string noSeed = "{\"version\":1,\"name\":\"sample\",\"settings\":{\"capacity\":500,\"fixedDeltaTime\":0.0166667,\"gravity\":[0,0,0]},\"emitters\":[{\"shape\":\"Point\",\"position\":[0,0,10],\"rate\":0,\"bursts\":[{\"step\":0,\"count\":3}],\"lifetimeMin\":10,\"lifetimeMax\":10,\"sizeMin\":1,\"sizeMax\":1,\"direction\":[0,0,1],\"speed\":0}]}";
			Throws( () => ParticleBake.BakeFromJson( noSeed, 2, 60f ), "missing seed in JSON" );
			Throws( () => ParticleBake.BakeDumpFromJson( noSeed, 2, 60f ), "missing seed in JSON (dump)" );
			string withSeed = noSeed.Replace( "\"capacity\":500", "\"capacity\":500,\"seed\":42", StringComparison.Ordinal );
			var frames = ParticleBake.BakeFromJson( withSeed, 2, 60f );
			Assert( frames.Count == 2 && frames[0].Particles.Count == 3, "seeded JSON bake must give 2 frames with 3 particles in frame 0" );
			var dump = ParticleBake.BakeDumpFromJson( withSeed, 2, 60f );
			Assert( dump.DefinitionHash == ParticleBake.DefinitionHash( ParticleAuthoring.FromJson( withSeed ) ), "JSON dump hash must equal the def hash" );
			Throws( () => ParticleBake.BakeFromJson( "", 2, 60f ), "empty JSON" );
			Throws( () => ParticleBake.BakeFromJson( "{\"version\":2,\"settings\":{\"seed\":1},\"emitters\":[]}", 2, 60f ), "JSON version 2" );
		} );

		Check( "p40-dump-composition", () =>
		{
			var dump = ParticleBake.BakeDump( SampleDef(), 3, 60f );
			Assert( dump.Version == ParticleBakeExport.CurrentVersion, "dump version must be P40 current" );
			Assert( dump.DefinitionHash == ParticleBake.DefinitionHash( SampleDef() ), "dump hash must equal the def hash" );
			Assert( dump.FrameRate == 60f && dump.FrameCount == 3 && dump.Frames.Count == 3, "dump rate/count wrong" );
			Assert( dump.Frames[0].Particles.Count == 3, "dump frame0 particle count wrong" );
			// P40 round-trips: string and file, carried verbatim.
			string json = ParticleBakeExport.Write( dump );
			var back = ParticleBakeExport.Read( json );
			Assert( back.DefinitionHash == dump.DefinitionHash && back.FrameCount == 3, "P40 string round-trip lost hash/count" );
			Assert( back.Frames[2].Particles.Count == 3, "P40 string round-trip lost frame particles" );
			string path = Path.Combine( work, "p39-dump.json" );
			ParticleBakeExport.SaveFile( dump, path );
			var loaded = ParticleBakeExport.LoadFile( path );
			Assert( loaded.DefinitionHash == dump.DefinitionHash && loaded.FrameCount == 3, "P40 file round-trip lost hash/count" );
			// Cached dump must not re-simulate on a hit.
			var cache = new ParticleBakeCache();
			cache.BakeCachedDump( SampleDef(), 3, 60f );
			long stepsAfterFirst = cache.StepsSimulated;
			var dump2 = cache.BakeCachedDump( SampleDef(), 3, 60f );
			Assert( cache.StepsSimulated == stepsAfterFirst && cache.CacheHits == 1, "cached dump hit re-simulated" );
			Assert( dump2.DefinitionHash == dump.DefinitionHash, "cached dump hash wrong" );
		} );
	}

	// SHA-256 of SampleDef() above, pinned so a hash-algorithm change (or a
	// field-order slip) fails loudly instead of silently re-keying every
	// existing bake. Filled from the first scratch run's measured value.
	const string GoldenHash = "e9caf9df97b9aa971f2a28a7d58436adef2fec13e3b1402b2893d5a2bd42b9e3";
}
