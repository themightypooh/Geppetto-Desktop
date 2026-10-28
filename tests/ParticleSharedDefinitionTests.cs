using System;
using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleSharedDefinitionTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		const string group = "particle-shared-definition";
		const float tol = 1e-4f;
		void Check( string name, Action test ) => registry.Add( group, name, test );
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, string label )
		{
			if ( Math.Abs( actual - expected ) > tol ) throw new Exception( $"{label}: {actual} != {expected} (tol {tol})" );
		}
		void NearVec( Vector2 actual, Vector2 expected, string label )
		{
			Near( actual.X, expected.X, label + ".X" );
			Near( actual.Y, expected.Y, label + ".Y" );
		}
		void Throws( Action action, string label )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( $"expected an error for {label}, got silent success" );
		}
		bool DroppedHas( SharedParticleFields shared, string needle )
		{
			foreach ( var d in shared.DroppedFields ) if ( d.Contains( needle, StringComparison.Ordinal ) ) return true;
			return false;
		}

		ParticleSystemSettings Sample3DSettings()
		{
			return new ParticleSystemSettings
			{
				Seed = 42, Capacity = 5000, FixedDeltaTime = 1f / 60f, Substeps = 2,
				Gravity = new Vector3( 0, 0, -386.0886f ), Wind = new Vector3( 10, 0, -30 ),
				Drag = 0.8f, TurbulenceStrength = 5f, TurbulenceFrequency = 0.08f, TurbulenceSpeed = 3.5f,
				VortexStrength = 2f, VortexCenter = new Vector3( 4, 0, -6 ), VortexRadius = 5f,
				GroundEnabled = true, GroundOffset = 5f, Restitution = 0.35f, Friction = 0.25f,
			};
		}
		ParticleEmitter Sample3DEmitter()
		{
			return new ParticleEmitter
			{
				Rate = 30f, Bursts = new List<ParticleBurst> { new( 0, 25 ), new( 3, 7 ) },
				LifetimeMin = 0.5f, LifetimeMax = 2f, SizeMin = 0.2f, SizeMax = 0.6f,
				Direction = new Vector3( 0, 0, -1 ), Speed = 12f, SpeedJitter = 0.2f,
				SpreadDegrees = 45f, BaseVelocity = new Vector3( 5, 0, -7 ), AngularSpeedMax = 3f,
				Position = new Vector3( 10, 0, -20 ),
			};
		}
		Particle2DSettings Sample2DSettings()
		{
			return new Particle2DSettings
			{
				Seed = 42, Capacity = 5000, FixedDeltaTime = 1f / 60f, Substeps = 2,
				Gravity = new Vector2( 0, 980f ), Wind = new Vector2( 10, 30 ),
				Drag = 0.8f, TurbulenceStrength = 10f, TurbulenceFrequency = 0.04f, TurbulenceSpeed = 3.5f,
				VortexStrength = 8f, VortexCenter = new Vector2( 8, 12 ), VortexRadius = 10f,
				FloorEnabled = true, FloorY = -10f, Restitution = 0.35f, Friction = 0.25f,
			};
		}
		Particle2DEmitter Sample2DEmitter()
		{
			return new Particle2DEmitter
			{
				Rate = 30f, Bursts = new List<Particle2DBurst> { new( 0, 25 ), new( 3, 7 ) },
				LifetimeMin = 0.5f, LifetimeMax = 2f, SizeMin = 0.4f, SizeMax = 1.2f,
				Direction = new Vector2( 0, -1 ), Speed = 24f, SpeedJitter = 0.2f,
				SpreadDegrees = 45f, BaseVelocity = new Vector2( 10, 14 ), AngularSpeedMax = 3f,
				Position = new Vector2( 20, 40 ),
			};
		}
		void AssertSharedEqual( SharedParticleFields a, SharedParticleFields b, string label )
		{
			Assert( a.Seed == b.Seed, $"{label}: seed" );
			Assert( a.Capacity == b.Capacity, $"{label}: capacity" );
			Near( a.FixedDeltaTime, b.FixedDeltaTime, label + " fixed step" );
			Assert( a.Substeps == b.Substeps, $"{label}: substeps" );
			NearVec( a.Gravity, b.Gravity, label + " gravity" );
			NearVec( a.Wind, b.Wind, label + " wind" );
			Near( a.Drag, b.Drag, label + " drag" );
			Near( a.TurbulenceStrength, b.TurbulenceStrength, label + " turbulence strength" );
			Near( a.TurbulenceFrequency, b.TurbulenceFrequency, label + " turbulence frequency" );
			Near( a.TurbulenceSpeed, b.TurbulenceSpeed, label + " turbulence speed" );
			Near( a.VortexStrength, b.VortexStrength, label + " vortex strength" );
			NearVec( a.VortexCenter, b.VortexCenter, label + " vortex center" );
			Near( a.VortexRadius, b.VortexRadius, label + " vortex radius" );
			Assert( a.GroundEnabled == b.GroundEnabled, $"{label}: ground enabled" );
			Near( a.GroundOffset, b.GroundOffset, label + " ground offset" );
			Near( a.Restitution, b.Restitution, label + " restitution" );
			Near( a.Friction, b.Friction, label + " friction" );
			Near( a.Rate, b.Rate, label + " rate" );
			Assert( a.Bursts.Count == b.Bursts.Count, $"{label}: burst count" );
			for ( int i = 0; i < a.Bursts.Count; i++ )
			{
				Assert( a.Bursts[i].Step == b.Bursts[i].Step, $"{label}: burst {i} step" );
				Assert( a.Bursts[i].Count == b.Bursts[i].Count, $"{label}: burst {i} count" );
			}
			Near( a.LifetimeMin, b.LifetimeMin, label + " lifetime min" );
			Near( a.LifetimeMax, b.LifetimeMax, label + " lifetime max" );
			Near( a.SizeMin, b.SizeMin, label + " size min" );
			Near( a.SizeMax, b.SizeMax, label + " size max" );
			Near( a.Speed, b.Speed, label + " speed" );
			Near( a.SpeedJitter, b.SpeedJitter, label + " speed jitter" );
			Near( a.SpreadDegrees, b.SpreadDegrees, label + " spread" );
			NearVec( a.Direction, b.Direction, label + " direction" );
			NearVec( a.BaseVelocity, b.BaseVelocity, label + " base velocity" );
			Near( a.AngularSpeedMax, b.AngularSpeedMax, label + " angular speed" );
			Assert( a.Enabled == b.Enabled, $"{label}: enabled" );
			NearVec( a.EmitterPosition, b.EmitterPosition, label + " emitter position" );
		}

		Check( "gravity 3D to 2D exact at factor 1, both directions", () =>
		{
			var s3 = new ParticleSystemSettings { Gravity = new Vector3( 0, 0, -386.0886f ) };
			var shared = ParticleSharedDefinition.ToSharedFrom3D( s3 );
			Assert( shared.Gravity == new Vector2( 0, 386.0886f ), $"shared gravity {shared.Gravity} != (0, 386.0886)" );
			var s2 = ParticleSharedDefinition.FromSharedTo2D( shared, 1f );
			Assert( s2.Gravity == new Vector2( 0, 386.0886f ), $"2D gravity {s2.Gravity} != (0, 386.0886)" );
			var back = ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings { Gravity = new Vector2( 0, 386.0886f ) }, 1f );
			Assert( back.Gravity == new Vector2( 0, 386.0886f ), "2D -> shared gravity" );
			var s3b = ParticleSharedDefinition.FromSharedTo3D( back );
			Assert( s3b.Gravity == new Vector3( 0, 0, -386.0886f ), $"3D gravity {s3b.Gravity} != (0, 0, -386.0886)" );
		} );

		Check( "gravity sign: rising bubbles are negative in 2D, positive Z in 3D", () =>
		{
			var shared = ParticleSharedDefinition.ToSharedFrom3D( new ParticleSystemSettings { Gravity = new Vector3( 0, 0, 60f ) } );
			Assert( shared.Gravity == new Vector2( 0, -60f ), $"rising shared gravity {shared.Gravity} != (0, -60)" );
			var s2 = ParticleSharedDefinition.FromSharedTo2D( shared, 1f );
			Assert( s2.Gravity == new Vector2( 0, -60f ), $"rising 2D gravity {s2.Gravity} != (0, -60)" );
			var back = ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings { Gravity = new Vector2( 0, -60f ) }, 1f );
			Assert( ParticleSharedDefinition.FromSharedTo3D( back ).Gravity == new Vector3( 0, 0, 60f ), "rising round-trip" );
		} );

		Check( "wind and base velocity map exactly at factor 1, both directions", () =>
		{
			var shared = ParticleSharedDefinition.ToSharedFrom3D( Sample3DSettings(), Sample3DEmitter() );
			Assert( shared.Wind == new Vector2( 10, 30 ), $"shared wind {shared.Wind} != (10, 30)" );
			Assert( shared.BaseVelocity == new Vector2( 5, 7 ), $"shared base velocity {shared.BaseVelocity} != (5, 7)" );
			var s2 = ParticleSharedDefinition.FromSharedTo2D( shared, 1f );
			var e2 = ParticleSharedDefinition.FromSharedTo2DEmitter( shared, 1f );
			Assert( s2.Wind == new Vector2( 10, 30 ), $"2D wind {s2.Wind}" );
			Assert( e2.BaseVelocity == new Vector2( 5, 7 ), $"2D base velocity {e2.BaseVelocity}" );
			var back = ParticleSharedDefinition.ToSharedFrom2D( s2, e2, 1f );
			var s3b = ParticleSharedDefinition.FromSharedTo3D( back );
			var e3b = ParticleSharedDefinition.FromSharedTo3DEmitter( back );
			Assert( s3b.Wind == new Vector3( 10, 0, -30 ), $"3D wind {s3b.Wind}" );
			Assert( e3b.BaseVelocity == new Vector3( 5, 0, -7 ), $"3D base velocity {e3b.BaseVelocity}" );
		} );

		Check( "depth component is dropped and flagged, never silently lost", () =>
		{
			var shared = ParticleSharedDefinition.ToSharedFrom3D( new ParticleSystemSettings { Wind = new Vector3( 10, 20, -30 ) } );
			Assert( shared.Wind == new Vector2( 10, 30 ), $"shared wind {shared.Wind}" );
			Assert( DroppedHas( shared, "Wind.Y" ), "Wind.Y depth not in DroppedFields" );
			var s3b = ParticleSharedDefinition.FromSharedTo3D( shared );
			Assert( s3b.Wind == new Vector3( 10, 0, -30 ), $"reconstructed wind {s3b.Wind} (depth rebuilt as 0)" );
		} );

		Check( "factor 1 is identity on magnitudes", () =>
		{
			var shared = ParticleSharedDefinition.ToSharedFrom2D( Sample2DSettings(), Sample2DEmitter(), 1f );
			Assert( shared.Gravity == new Vector2( 0, 980f ), $"F=1 gravity {shared.Gravity}" );
			Assert( shared.SizeMin == 0.4f && shared.SizeMax == 1.2f, "F=1 sizes" );
			Assert( shared.Speed == 24f, "F=1 speed" );
			Assert( shared.TurbulenceStrength == 10f, "F=1 turbulence strength" );
			Assert( shared.TurbulenceFrequency == 0.04f, "F=1 turbulence frequency" );
		} );

		Check( "shared to 2D to shared is identity at factor 2.5", () =>
		{
			var original = ParticleSharedDefinition.ToSharedFrom3D( Sample3DSettings(), Sample3DEmitter() );
			var s2 = ParticleSharedDefinition.FromSharedTo2D( original, 2.5f );
			var e2 = ParticleSharedDefinition.FromSharedTo2DEmitter( original, 2.5f );
			var round = ParticleSharedDefinition.ToSharedFrom2D( s2, e2, 2.5f );
			AssertSharedEqual( original, round, "shared->2D->shared F=2.5" );
		} );

		Check( "shared to 3D to shared is identity", () =>
		{
			var original = ParticleSharedDefinition.ToSharedFrom2D( Sample2DSettings(), Sample2DEmitter(), 2f );
			var s3 = ParticleSharedDefinition.FromSharedTo3D( original );
			var e3 = ParticleSharedDefinition.FromSharedTo3DEmitter( original );
			var round = ParticleSharedDefinition.ToSharedFrom3D( s3, e3 );
			AssertSharedEqual( original, round, "shared->3D->shared" );
		} );

		Check( "2D to shared to 2D preserves every shared field at factor 2", () =>
		{
			var s2 = Sample2DSettings();
			var e2 = Sample2DEmitter();
			var shared = ParticleSharedDefinition.ToSharedFrom2D( s2, e2, 2f );
			var s2b = ParticleSharedDefinition.FromSharedTo2D( shared, 2f );
			var e2b = ParticleSharedDefinition.FromSharedTo2DEmitter( shared, 2f );
			var round = ParticleSharedDefinition.ToSharedFrom2D( s2b, e2b, 2f );
			AssertSharedEqual( shared, round, "2D->shared->2D F=2" );
			// And the concrete 2D values survive too (sample was built so F=2 maps exactly).
			Assert( s2b.Gravity == s2.Gravity, "2D gravity concrete" );
			Assert( s2b.Wind == s2.Wind, "2D wind concrete" );
			Assert( e2b.SizeMin == e2.SizeMin && e2b.SizeMax == e2.SizeMax, "2D sizes concrete" );
			Assert( e2b.Speed == e2.Speed, "2D speed concrete" );
			Assert( e2b.Bursts.Count == 2 && e2b.Bursts[0].Count == 25 && e2b.Bursts[1].Count == 7, "2D bursts concrete" );
		} );

		Check( "3D to shared to 3D preserves every shared field", () =>
		{
			var shared = ParticleSharedDefinition.ToSharedFrom3D( Sample3DSettings(), Sample3DEmitter() );
			var s3b = ParticleSharedDefinition.FromSharedTo3D( shared );
			var e3b = ParticleSharedDefinition.FromSharedTo3DEmitter( shared );
			var round = ParticleSharedDefinition.ToSharedFrom3D( s3b, e3b );
			AssertSharedEqual( shared, round, "3D->shared->3D" );
			Assert( s3b.Gravity == new Vector3( 0, 0, -386.0886f ), "3D gravity concrete" );
			Assert( e3b.Bursts.Count == 2 && e3b.Bursts[0].Count == 25 && e3b.Bursts[1].Count == 7, "3D bursts concrete" );
		} );

		Check( "turbulence frequency scales inversely, turbulence speed never scales", () =>
		{
			var shared = ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings { TurbulenceFrequency = 0.04f, TurbulenceSpeed = 3.5f }, 2f );
			Near( shared.TurbulenceFrequency, 0.08f, "2D 0.04/px at F=2 -> 0.08/in" );
			Near( shared.TurbulenceSpeed, 3.5f, "turbulence speed unscaled" );
			var s2b = ParticleSharedDefinition.FromSharedTo2D( shared, 2f );
			Near( s2b.TurbulenceFrequency, 0.04f, "shared 0.08/in at F=2 -> 0.04/px" );
			Near( s2b.TurbulenceSpeed, 3.5f, "turbulence speed back unscaled" );
		} );

		Check( "vortex strength scales by factor squared, radius by factor", () =>
		{
			var shared = ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings { VortexStrength = 8f, VortexRadius = 10f }, 2f );
			Near( shared.VortexStrength, 2f, "2D 8 px^2/s^2 at F=2 -> 2 in^2/s^2" );
			Near( shared.VortexRadius, 5f, "2D 10 px at F=2 -> 5 in" );
			var s2b = ParticleSharedDefinition.FromSharedTo2D( shared, 2f );
			Near( s2b.VortexStrength, 8f, "vortex strength back" );
			Near( s2b.VortexRadius, 10f, "vortex radius back" );
		} );

		Check( "ground offset maps to floor with a sign flip", () =>
		{
			var shared = ParticleSharedDefinition.ToSharedFrom3D( new ParticleSystemSettings { GroundEnabled = true, GroundOffset = 5f } );
			Near( shared.GroundOffset, 5f, "shared ground offset" );
			var s2 = ParticleSharedDefinition.FromSharedTo2D( shared, 2f );
			Assert( s2.FloorEnabled, "floor enabled" );
			Near( s2.FloorY, -10f, "FloorY = -offset * F" );
			var back = ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings { FloorEnabled = true, FloorY = -10f }, 2f );
			Near( back.GroundOffset, 5f, "floor back to ground offset" );
			Assert( ParticleSharedDefinition.FromSharedTo3D( back ).GroundOffset == 5f, "ground offset concrete" );
		} );

		Check( "gravity angle converts effective gravity and drops the angle", () =>
		{
			var s2 = new Particle2DSettings { Gravity = new Vector2( 0, 980f ), GravityAngleDegrees = 0f };
			var shared = ParticleSharedDefinition.ToSharedFrom2D( s2, 1f );
			NearVec( shared.Gravity, new Vector2( 980f, 0f ), "angle 0 = +X at full magnitude" );
			Assert( DroppedHas( shared, "GravityAngleDegrees" ), "angle not in DroppedFields" );
			var s2b = new Particle2DSettings { Gravity = new Vector2( 0, 980f ), GravityAngleDegrees = 90f };
			var sharedB = ParticleSharedDefinition.ToSharedFrom2D( s2b, 1f );
			NearVec( sharedB.Gravity, new Vector2( 0, 980f ), "angle 90 = +Y down at full magnitude" );
		} );

		Check( "times counts seeds and dimensionless factors never scale at factor 96", () =>
		{
			var shared = ParticleSharedDefinition.ToSharedFrom2D( Sample2DSettings(), Sample2DEmitter(), 96f );
			Assert( shared.Seed == 42, "seed unscaled" );
			Assert( shared.Substeps == 2, "substeps unscaled" );
			Near( shared.FixedDeltaTime, 1f / 60f, "fixed step unscaled" );
			Near( shared.Drag, 0.8f, "drag unscaled" );
			Near( shared.Rate, 30f, "rate unscaled" );
			Near( shared.LifetimeMin, 0.5f, "lifetime unscaled" );
			Near( shared.SpeedJitter, 0.2f, "speed jitter unscaled" );
			Near( shared.Restitution, 0.35f, "restitution unscaled" );
			// And a length DOES scale at the same factor, proving the factor applied.
			Near( shared.SizeMin, 0.4f / 96f, "size scaled at F=96" );
			Near( shared.Gravity.Y, 980f / 96f, "gravity scaled at F=96" );
		} );

		Check( "dropped fields: a 2D-only field appears after conversion", () =>
		{
			var e2 = Sample2DEmitter();
			e2.Palette = new List<Particle2DColor> { new( 1, 0, 0, 1 ) };
			var s2 = Sample2DSettings();
			s2.WrapEnabled = true;
			s2.WrapMin = new Vector2( 0, 0 );
			s2.WrapMax = new Vector2( 100, 100 );
			var shared = ParticleSharedDefinition.ToSharedFrom2D( s2, e2, 2f );
			Assert( DroppedHas( shared, "Palette" ), "Palette not in DroppedFields" );
			Assert( DroppedHas( shared, "WrapEnabled" ), "WrapEnabled not in DroppedFields" );
			Assert( DroppedHas( shared, "ColorStart" ), "ColorStart not in DroppedFields" );
		} );

		Check( "dropped fields: a 3D-only field appears after conversion", () =>
		{
			var s3 = Sample3DSettings();
			s3.RenderType = ParticleRenderType.Strand;
			s3.StrandPoints = 4;
			var e3 = Sample3DEmitter();
			e3.DieOnCollision = true;
			var shared = ParticleSharedDefinition.ToSharedFrom3D( s3, e3 );
			Assert( DroppedHas( shared, "StrandPoints" ), "StrandPoints not in DroppedFields" );
			Assert( DroppedHas( shared, "RenderType" ), "RenderType not in DroppedFields" );
			Assert( DroppedHas( shared, "DieOnCollision" ), "DieOnCollision not in DroppedFields" );
		} );

		Check( "validate rejects non-positive and non-finite pixels per inch", () =>
		{
			Throws( () => ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings(), 0f ), "ppi 0" );
			Throws( () => ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings(), -3f ), "ppi negative" );
			Throws( () => ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings(), float.NaN ), "ppi NaN" );
			Throws( () => ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings(), float.PositiveInfinity ), "ppi infinite" );
			Throws( () => ParticleSharedDefinition.FromSharedTo2D( new SharedParticleFields(), 0f ), "FromShared ppi 0" );
		} );

		Check( "validate rejects null settings emitter and shared", () =>
		{
			Throws( () => ParticleSharedDefinition.ToSharedFrom2D( null!, 1f ), "null 2D settings" );
			Throws( () => ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings(), null!, 1f ), "null 2D emitter" );
			Throws( () => ParticleSharedDefinition.ToSharedFrom3D( null! ), "null 3D settings" );
			Throws( () => ParticleSharedDefinition.ToSharedFrom3D( new ParticleSystemSettings(), null! ), "null 3D emitter" );
			Throws( () => ParticleSharedDefinition.FromSharedTo2D( null!, 1f ), "null shared to 2D" );
			Throws( () => ParticleSharedDefinition.FromSharedTo3D( null! ), "null shared to 3D" );
		} );

		Check( "validate rejects non-finite inputs and negative lifetimes sizes rates", () =>
		{
			Throws( () => ParticleSharedDefinition.ToSharedFrom2D( new Particle2DSettings { Gravity = new Vector2( float.NaN, 0 ) }, 1f ), "NaN 2D gravity" );
			Throws( () => new SharedParticleFields { Gravity = new Vector2( float.NaN, 0 ) }.Validate(), "NaN shared gravity" );
			Throws( () => ParticleSharedDefinition.FromSharedTo2D( new SharedParticleFields { LifetimeMin = -1f }, 1f ), "negative lifetime" );
			Throws( () => ParticleSharedDefinition.FromSharedTo3D( new SharedParticleFields { SizeMin = -0.5f } ), "negative size" );
			Throws( () => ParticleSharedDefinition.FromSharedTo2D( new SharedParticleFields { Rate = -2f }, 1f ), "negative rate" );
			Throws( () => ParticleSharedDefinition.FromSharedTo3D( new SharedParticleFields { LifetimeMax = 0.5f, LifetimeMin = 2f } ), "lifetime max below min" );
		} );

		Check( "capacity above the 2D cap converts but fails 2D validate, passes 3D validate", () =>
		{
			var shared = new SharedParticleFields { Capacity = 2_000_000 };
			shared.Validate();
			var s2 = ParticleSharedDefinition.FromSharedTo2D( shared, 1f );
			Assert( s2.Capacity == 2_000_000, "capacity carried, not clamped" );
			Throws( () => s2.Validate(), "2D validate on 2M capacity" );
			var s3 = ParticleSharedDefinition.FromSharedTo3D( shared );
			s3.Validate();
		} );

		Check( "settings-only round-trip preserves system fields", () =>
		{
			var s2 = Sample2DSettings();
			var shared = ParticleSharedDefinition.ToSharedFrom2D( s2, 2f );
			var s2b = ParticleSharedDefinition.FromSharedTo2D( shared, 2f );
			var round = ParticleSharedDefinition.ToSharedFrom2D( s2b, 2f );
			NearVec( round.Gravity, shared.Gravity, "settings-only 2D gravity" );
			Near( round.TurbulenceFrequency, shared.TurbulenceFrequency, "settings-only 2D frequency" );
			Assert( round.Seed == shared.Seed && round.Capacity == shared.Capacity, "settings-only 2D seed/capacity" );
			var s3 = Sample3DSettings();
			var shared3 = ParticleSharedDefinition.ToSharedFrom3D( s3 );
			var s3b = ParticleSharedDefinition.FromSharedTo3D( shared3 );
			var round3 = ParticleSharedDefinition.ToSharedFrom3D( s3b );
			NearVec( round3.Gravity, shared3.Gravity, "settings-only 3D gravity" );
			NearVec( round3.Wind, shared3.Wind, "settings-only 3D wind" );
		} );
	}
}
