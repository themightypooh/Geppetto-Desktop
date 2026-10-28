using System;
using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleScalingTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Eq( float actual, float expected, string what )
		{
			if ( actual != expected ) throw new Exception( $"{what}: got {actual}, expected {expected} exactly" );
		}
		void EqV( Vector3 actual, Vector3 expected, string what )
		{
			if ( actual != expected ) throw new Exception( $"{what}: got {actual}, expected {expected} exactly" );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: threw {ex.GetType().Name}, expected {typeof( T ).Name}" ); }
			throw new Exception( $"{what}: did not throw" );
		}

		ParticleSystemSettings Settings()
		{
			return new ParticleSystemSettings
			{
				Capacity = 1000,
				Seed = 7,
				FixedDeltaTime = 1f / 60f,
				Substeps = 2,
				Gravity = new Vector3( 0, 0, -386f ),
				Wind = new Vector3( 4f, 2f, 0f ),
				Drag = 0.8f,
				TurbulenceStrength = 10f,
				TurbulenceFrequency = 0.2f,
				TurbulenceSpeed = 3f,
				VortexStrength = 4f,
				VortexCenter = new Vector3( 1f, 2f, 3f ),
				VortexAxis = new Vector3( 0, 0, 1 ),
				VortexRadius = 2f,
				GroundEnabled = true,
				GroundNormal = new Vector3( 0, 0, 1 ),
				GroundOffset = 5f,
				CollisionRadius = 0.01f,
				Restitution = 0.3f,
				Friction = 0.2f,
				RenderType = ParticleRenderType.Billboard,
				StrandPoints = 0,
				KillVolumes = new List<ParticleKillVolume>
				{
					new() { Kind = ParticleKillVolumeKind.Plane, Normal = new Vector3( 0, 0, 1 ), Offset = 10f },
					new() { Kind = ParticleKillVolumeKind.Sphere, Center = new Vector3( 1f, 1f, 1f ), Radius = 3f, KillInside = false },
				},
			};
		}

		ParticleEmitter Emitter()
		{
			return new ParticleEmitter
			{
				Position = new Vector3( 3f, 0f, 0f ),
				AreaU = new Vector3( 2f, 0f, 0f ),
				AreaV = new Vector3( 0f, 2f, 0f ),
				BoxHalfExtents = new Vector3( 1f, 2f, 3f ),
				SphereRadius = 4f,
				Rate = 60f,
				Bursts = new List<ParticleBurst> { new( 5, 7 ) },
				LifetimeMin = 1.5f,
				LifetimeMax = 2.5f,
				SizeMin = 2f,
				SizeMax = 2f,
				Direction = new Vector3( 0, 0, 1 ),
				Speed = 10f,
				SpeedJitter = 0.25f,
				SpreadDegrees = 30f,
				BaseVelocity = new Vector3( 5f, 0f, 0f ),
				AngularSpeedMax = 1.5f,
				DeathSubEmitter = new ParticleSubEmitter
				{
					ChildTemplate = new ParticleEmitter { SizeMin = 1f, SizeMax = 1f, Speed = 6f },
					Count = 3,
					VelocityInherit = 0.5f,
				},
				CollisionSubEmitter = new ParticleSubEmitter { ChildPreset = "sparks", Count = 1 },
			};
		}

		registry.Add( "particle-scaling", "worldscale S=2 settings numbers", () =>
		{
			var scaled = ParticleScaling.ScaleSettings( Settings(), 2f, ParticleScalingMode.WorldScale );
			EqV( scaled.Gravity, new Vector3( 0, 0, -772f ), "gravity" );
			EqV( scaled.Wind, new Vector3( 8f, 4f, 0f ), "wind" );
			Eq( scaled.Drag, 0.8f, "linear drag unchanged" );
			Eq( scaled.TurbulenceStrength, 20f, "turbulence strength" );
			Eq( scaled.TurbulenceFrequency, 0.1f, "turbulence frequency 1/S" );
			Eq( scaled.TurbulenceSpeed, 3f, "turbulence speed unchanged" );
			Eq( scaled.VortexStrength, 16f, "vortex strength S^2" );
			EqV( scaled.VortexCenter, new Vector3( 2f, 4f, 6f ), "vortex centre" );
			Eq( scaled.VortexRadius, 4f, "vortex radius" );
			Eq( scaled.GroundOffset, 10f, "ground offset" );
			Eq( scaled.CollisionRadius, 0.02f, "collision radius" );
			Eq( scaled.Restitution, 0.3f, "restitution unchanged" );
			Eq( scaled.Friction, 0.2f, "friction unchanged" );
			Check( scaled.Capacity == 1000 && scaled.Seed == 7 && scaled.Substeps == 2, "capacity/seed/substeps unchanged" );
			Eq( scaled.FixedDeltaTime, 1f / 60f, "fixed step unchanged" );
		} );

		registry.Add( "particle-scaling", "worldscale S=2 emitter numbers", () =>
		{
			var scaled = ParticleScaling.ScaleEmitter( Emitter(), 2f, ParticleScalingMode.WorldScale );
			Eq( scaled.SizeMin, 4f, "size 2 -> 4" );
			Eq( scaled.SizeMax, 4f, "size max 2 -> 4" );
			Eq( scaled.Speed, 20f, "speed 10 -> 20" );
			EqV( scaled.Position, new Vector3( 6f, 0f, 0f ), "position" );
			EqV( scaled.AreaU, new Vector3( 4f, 0f, 0f ), "area U" );
			EqV( scaled.AreaV, new Vector3( 0f, 4f, 0f ), "area V" );
			EqV( scaled.BoxHalfExtents, new Vector3( 2f, 4f, 6f ), "box extents" );
			Eq( scaled.SphereRadius, 8f, "sphere radius" );
			EqV( scaled.BaseVelocity, new Vector3( 10f, 0f, 0f ), "base velocity" );
			Eq( scaled.LifetimeMin, 1.5f, "lifetime unchanged" );
			Eq( scaled.LifetimeMax, 2.5f, "lifetime max unchanged" );
			Eq( scaled.Rate, 60f, "rate unchanged" );
			Check( scaled.Bursts.Count == 1 && scaled.Bursts[0].Step == 5 && scaled.Bursts[0].Count == 7, "bursts unchanged" );
			Eq( scaled.SpeedJitter, 0.25f, "jitter unchanged" );
			Eq( scaled.SpreadDegrees, 30f, "spread unchanged" );
			Eq( scaled.AngularSpeedMax, 1.5f, "angular speed unchanged" );
			EqV( scaled.Direction, new Vector3( 0, 0, 1 ), "direction unchanged" );
		} );

		registry.Add( "particle-scaling", "preserveworld world settings unchanged", () =>
		{
			var scaled = ParticleScaling.ScaleSettings( Settings(), 2f, ParticleScalingMode.PreserveWorld );
			EqV( scaled.Gravity, new Vector3( 0, 0, -386f ), "gravity unchanged" );
			EqV( scaled.Wind, new Vector3( 4f, 2f, 0f ), "wind unchanged" );
			Eq( scaled.TurbulenceStrength, 10f, "turbulence strength unchanged" );
			Eq( scaled.TurbulenceFrequency, 0.2f, "turbulence frequency unchanged" );
			Eq( scaled.VortexStrength, 4f, "vortex strength unchanged" );
			EqV( scaled.VortexCenter, new Vector3( 1f, 2f, 3f ), "vortex centre unchanged" );
			Eq( scaled.VortexRadius, 2f, "vortex radius unchanged" );
			Eq( scaled.GroundOffset, 5f, "ground offset unchanged" );
			var em = ParticleScaling.ScaleEmitter( Emitter(), 2f, ParticleScalingMode.PreserveWorld );
			Eq( em.SizeMin, 2f, "size unchanged" );
			Eq( em.Speed, 10f, "speed unchanged" );
			EqV( em.Position, new Vector3( 3f, 0f, 0f ), "position unchanged" );
		} );

		registry.Add( "particle-scaling", "preserveworld compensated-then-scaled equals original exactly", () =>
		{
			var comp = ParticleScaling.CompensateForParent( Settings(), 2f, ParticleScalingMode.PreserveWorld );
			EqV( comp.Gravity, new Vector3( 0, 0, -193f ), "compensated gravity -386/2" );
			EqV( comp.Wind, new Vector3( 2f, 1f, 0f ), "compensated wind" );
			Eq( comp.TurbulenceStrength, 5f, "compensated turbulence strength" );
			Eq( comp.TurbulenceFrequency, 0.4f, "compensated turbulence frequency xS" );
			Eq( comp.VortexStrength, 1f, "compensated vortex strength /S^2" );
			EqV( comp.VortexCenter, new Vector3( 0.5f, 1f, 1.5f ), "compensated vortex centre" );
			Eq( comp.VortexRadius, 1f, "compensated vortex radius" );
			Eq( comp.GroundOffset, 2.5f, "compensated ground offset" );
			Eq( comp.CollisionRadius, 0.005f, "compensated collision radius" );
			var em = ParticleScaling.CompensateEmitterForParent( Emitter(), 2f, ParticleScalingMode.PreserveWorld );
			Eq( em.SizeMin, 1f, "compensated size 2/2" );
			Eq( em.Speed, 5f, "compensated speed 10/2" );
			// Parent scale xS restores the authored world result exactly.
			var restored = ParticleScaling.ScaleSettings( comp, 2f, ParticleScalingMode.WorldScale );
			var orig = Settings();
			EqV( restored.Gravity, orig.Gravity, "restored gravity" );
			EqV( restored.Wind, orig.Wind, "restored wind" );
			Eq( restored.TurbulenceStrength, orig.TurbulenceStrength, "restored turbulence strength" );
			Eq( restored.TurbulenceFrequency, orig.TurbulenceFrequency, "restored turbulence frequency" );
			Eq( restored.VortexStrength, orig.VortexStrength, "restored vortex strength" );
			EqV( restored.VortexCenter, orig.VortexCenter, "restored vortex centre" );
			Eq( restored.VortexRadius, orig.VortexRadius, "restored vortex radius" );
			Eq( restored.GroundOffset, orig.GroundOffset, "restored ground offset" );
			Eq( restored.CollisionRadius, orig.CollisionRadius, "restored collision radius" );
			var emRestored = ParticleScaling.ScaleEmitter( em, 2f, ParticleScalingMode.WorldScale );
			Eq( emRestored.SizeMin, 2f, "restored size" );
			Eq( emRestored.Speed, 10f, "restored speed" );
		} );

		registry.Add( "particle-scaling", "scalesizeonly sizes scale speeds and gravity do not", () =>
		{
			var scaled = ParticleScaling.ScaleSettings( Settings(), 2f, ParticleScalingMode.ScaleSizeOnly );
			EqV( scaled.Gravity, new Vector3( 0, 0, -386f ), "gravity held" );
			EqV( scaled.Wind, new Vector3( 4f, 2f, 0f ), "wind held" );
			Eq( scaled.TurbulenceStrength, 10f, "turbulence strength held" );
			Eq( scaled.TurbulenceFrequency, 0.1f, "turbulence frequency 1/S (position-tied)" );
			Eq( scaled.VortexStrength, 8f, "vortex strength xS (accel held, length scaled)" );
			EqV( scaled.VortexCenter, new Vector3( 2f, 4f, 6f ), "vortex centre scaled" );
			Eq( scaled.VortexRadius, 4f, "vortex radius scaled" );
			Eq( scaled.GroundOffset, 10f, "ground offset scaled" );
			var em = ParticleScaling.ScaleEmitter( Emitter(), 2f, ParticleScalingMode.ScaleSizeOnly );
			Eq( em.SizeMin, 4f, "size 2 -> 4" );
			Eq( em.Speed, 10f, "speed held" );
			EqV( em.Position, new Vector3( 6f, 0f, 0f ), "position scaled" );
			EqV( em.BaseVelocity, new Vector3( 5f, 0f, 0f ), "base velocity held" );
			Eq( em.LifetimeMin, 1.5f, "lifetime held" );
			Eq( em.Rate, 60f, "rate held" );
			var comp = ParticleScaling.CompensateEmitterForParent( Emitter(), 2f, ParticleScalingMode.ScaleSizeOnly );
			Eq( comp.SizeMin, 2f, "compensated size held (parent grows it)" );
			Eq( comp.Speed, 5f, "compensated speed halved (parent restores it)" );
		} );

		registry.Add( "particle-scaling", "drag coefficients linear held quadratic 1 over S", () =>
		{
			var drag = new ParticleDragCoefficients( 0.8f, 0.5f );
			Eq( ParticleScaling.ScaleDragCoefficients( drag, 2f, ParticleScalingMode.WorldScale ).Linear, 0.8f, "world linear held" );
			Eq( ParticleScaling.ScaleDragCoefficients( drag, 2f, ParticleScalingMode.WorldScale ).Quadratic, 0.25f, "world quadratic 0.5/2" );
			Eq( ParticleScaling.ScaleDragCoefficients( drag, 2f, ParticleScalingMode.PreserveWorld ).Quadratic, 0.5f, "preserve quadratic held" );
			Eq( ParticleScaling.ScaleDragCoefficients( drag, 2f, ParticleScalingMode.ScaleSizeOnly ).Quadratic, 0.5f, "sizesonly quadratic held" );
			Eq( ParticleScaling.CompensateDragCoefficients( drag, 2f, ParticleScalingMode.PreserveWorld ).Quadratic, 1f, "compensated preserve quadratic xS" );
			Eq( ParticleScaling.CompensateDragCoefficients( drag, 2f, ParticleScalingMode.WorldScale ).Quadratic, 0.5f, "compensated world quadratic held" );
			Eq( ParticleScaling.CompensateDragCoefficients( drag, 2f, ParticleScalingMode.ScaleSizeOnly ).Quadratic, 1f, "compensated sizesonly quadratic xS (parent v^2 growth restores held world drag)" );
			foreach ( var mode in new[] { ParticleScalingMode.WorldScale, ParticleScalingMode.PreserveWorld, ParticleScalingMode.ScaleSizeOnly } )
			{
				var roundTrip = ParticleScaling.ScaleDragCoefficients( ParticleScaling.CompensateDragCoefficients( drag, 2f, mode ), 2f, ParticleScalingMode.WorldScale );
				var direct = ParticleScaling.ScaleDragCoefficients( drag, 2f, mode );
				Eq( roundTrip.Linear, direct.Linear, $"drag composition linear {mode}" );
				Eq( roundTrip.Quadratic, direct.Quadratic, $"drag composition quadratic {mode}" );
				Eq( ParticleScaling.CompensateDragCoefficients( drag, 2f, mode ).Linear, 0.8f, $"compensated linear held {mode}" );
			}
		} );

		registry.Add( "particle-scaling", "S=1 identity for all modes exactly", () =>
		{
			foreach ( var mode in new[] { ParticleScalingMode.WorldScale, ParticleScalingMode.PreserveWorld, ParticleScalingMode.ScaleSizeOnly } )
			{
				var s = ParticleScaling.ScaleSettings( Settings(), 1f, mode );
				EqV( s.Gravity, new Vector3( 0, 0, -386f ), $"S=1 gravity {mode}" );
				EqV( s.Wind, new Vector3( 4f, 2f, 0f ), $"S=1 wind {mode}" );
				Eq( s.TurbulenceFrequency, 0.2f, $"S=1 frequency {mode}" );
				Eq( s.VortexStrength, 4f, $"S=1 vortex strength {mode}" );
				Eq( s.GroundOffset, 5f, $"S=1 ground offset {mode}" );
				var e = ParticleScaling.ScaleEmitter( Emitter(), 1f, mode );
				Eq( e.SizeMin, 2f, $"S=1 size {mode}" );
				Eq( e.Speed, 10f, $"S=1 speed {mode}" );
				var d = ParticleScaling.ScaleDragCoefficients( new ParticleDragCoefficients( 0.8f, 0.5f ), 1f, mode );
				Eq( d.Linear, 0.8f, $"S=1 linear drag {mode}" );
				Eq( d.Quadratic, 0.5f, $"S=1 quadratic drag {mode}" );
				var c = ParticleScaling.CompensateForParent( Settings(), 1f, mode );
				EqV( c.Gravity, new Vector3( 0, 0, -386f ), $"S=1 compensated gravity {mode}" );
			}
		} );

		registry.Add( "particle-scaling", "inputs never mutated outputs are fresh copies", () =>
		{
			var settings = Settings();
			var emitter = Emitter();
			var scaled = ParticleScaling.ScaleSettings( settings, 2f, ParticleScalingMode.WorldScale );
			ParticleScaling.ScaleEmitter( emitter, 2f, ParticleScalingMode.WorldScale );
			EqV( settings.Gravity, new Vector3( 0, 0, -386f ), "input gravity untouched" );
			Eq( settings.KillVolumes[0].Offset, 10f, "input kill offset untouched" );
			Eq( emitter.SizeMin, 2f, "input size untouched" );
			Eq( emitter.Speed, 10f, "input speed untouched" );
			Check( !ReferenceEquals( scaled.KillVolumes, settings.KillVolumes ), "kill list is a fresh list" );
			scaled.KillVolumes[0].Offset = 999f;
			Eq( settings.KillVolumes[0].Offset, 10f, "mutating output list leaves input alone" );
		} );

		registry.Add( "particle-scaling", "validate rejects bad scale mode and inputs", () =>
		{
			Throws<ArgumentException>( () => ParticleScaling.Validate( 0f, ParticleScalingMode.WorldScale ), "S=0" );
			Throws<ArgumentException>( () => ParticleScaling.Validate( -2f, ParticleScalingMode.WorldScale ), "S negative" );
			Throws<ArgumentException>( () => ParticleScaling.Validate( float.NaN, ParticleScalingMode.WorldScale ), "S NaN" );
			Throws<ArgumentException>( () => ParticleScaling.Validate( float.PositiveInfinity, ParticleScalingMode.WorldScale ), "S infinite" );
			Throws<ArgumentException>( () => ParticleScaling.Validate( 2f, (ParticleScalingMode)99 ), "unknown mode" );
			Throws<ArgumentException>( () => ParticleScaling.ScaleSettings( Settings(), 0f, ParticleScalingMode.WorldScale ), "ScaleSettings S=0" );
			Throws<ArgumentException>( () => ParticleScaling.ScaleSettings( Settings(), 2f, (ParticleScalingMode)99 ), "ScaleSettings unknown mode" );
			var badGravity = Settings();
			badGravity.Gravity = new Vector3( 0, 0, float.NaN );
			Throws<ArgumentException>( () => ParticleScaling.ScaleSettings( badGravity, 2f, ParticleScalingMode.WorldScale ), "NaN gravity" );
			var badEmitter = Emitter();
			badEmitter.Speed = float.PositiveInfinity;
			Throws<ArgumentException>( () => ParticleScaling.ScaleEmitter( badEmitter, 2f, ParticleScalingMode.WorldScale ), "infinite speed" );
			var badPosition = Emitter();
			badPosition.Position = new Vector3( float.NaN, 0, 0 );
			Throws<ArgumentException>( () => ParticleScaling.ScaleEmitter( badPosition, 2f, ParticleScalingMode.WorldScale ), "NaN position" );
			Throws<ArgumentException>( () => ParticleScaling.ScaleDragCoefficients( new ParticleDragCoefficients( 0.8f, float.NaN ), 2f, ParticleScalingMode.WorldScale ), "NaN quadratic drag" );
			var huge = Settings();
			huge.Gravity = new Vector3( 0, 0, 1e38f );
			Throws<ArgumentException>( () => ParticleScaling.ScaleSettings( huge, 1e10f, ParticleScalingMode.WorldScale ), "overflow to infinity rejected" );
		} );

		registry.Add( "particle-scaling", "composition identity for all modes", () =>
		{
			foreach ( var mode in new[] { ParticleScalingMode.WorldScale, ParticleScalingMode.PreserveWorld, ParticleScalingMode.ScaleSizeOnly } )
			{
				var direct = ParticleScaling.ScaleSettings( Settings(), 2f, mode );
				var via = ParticleScaling.ScaleSettings( ParticleScaling.CompensateForParent( Settings(), 2f, mode ), 2f, ParticleScalingMode.WorldScale );
				EqV( via.Gravity, direct.Gravity, $"composition gravity {mode}" );
				EqV( via.Wind, direct.Wind, $"composition wind {mode}" );
				Eq( via.TurbulenceStrength, direct.TurbulenceStrength, $"composition turbulence strength {mode}" );
				Eq( via.TurbulenceFrequency, direct.TurbulenceFrequency, $"composition turbulence frequency {mode}" );
				Eq( via.VortexStrength, direct.VortexStrength, $"composition vortex strength {mode}" );
				EqV( via.VortexCenter, direct.VortexCenter, $"composition vortex centre {mode}" );
				Eq( via.VortexRadius, direct.VortexRadius, $"composition vortex radius {mode}" );
				Eq( via.GroundOffset, direct.GroundOffset, $"composition ground offset {mode}" );
				Eq( via.CollisionRadius, direct.CollisionRadius, $"composition collision radius {mode}" );
				var emDirect = ParticleScaling.ScaleEmitter( Emitter(), 2f, mode );
				var emVia = ParticleScaling.ScaleEmitter( ParticleScaling.CompensateEmitterForParent( Emitter(), 2f, mode ), 2f, ParticleScalingMode.WorldScale );
				Eq( emVia.SizeMin, emDirect.SizeMin, $"composition size {mode}" );
				Eq( emVia.Speed, emDirect.Speed, $"composition speed {mode}" );
				EqV( emVia.Position, emDirect.Position, $"composition position {mode}" );
			}
		} );

		registry.Add( "particle-scaling", "world state placement and pivot helpers", () =>
		{
			var state = new ParticleState
			{
				Position = new Vector3( 1f, 2f, 3f ),
				Age = 0.5f,
				Velocity = new Vector3( 10f, 0f, 0f ),
				Alive = 1f,
				Rotation = Quaternion.Identity,
				AngularVelocity = new Vector3( 0f, 0f, 1.5f ),
				Size = 2f,
			};
			var world = ParticleScaling.ToWorldState( state, 2f );
			EqV( world.Position, new Vector3( 2f, 4f, 6f ), "world position" );
			EqV( world.Velocity, new Vector3( 20f, 0f, 0f ), "world velocity" );
			Eq( world.Size, 4f, "world size" );
			Eq( world.Age, 0.5f, "age unchanged" );
			Eq( world.Alive, 1f, "alive unchanged" );
			EqV( world.AngularVelocity, new Vector3( 0f, 0f, 1.5f ), "angular velocity unchanged" );
			Check( world.Rotation == Quaternion.Identity, "rotation unchanged" );
			EqV( ParticleScaling.ScalePositionAboutPivot( new Vector3( 3f, 0f, 0f ), new Vector3( 1f, 0f, 0f ), 2f ), new Vector3( 5f, 0f, 0f ), "pivot position" );
			Eq( ParticleScaling.ScalePlaneOffset( 10f, new Vector3( 0, 0, 1 ), new Vector3( 0, 0, 2 ), 2f ), 18f, "pivot plane offset" );
			Eq( ParticleScaling.ScalePlaneOffset( 10f, new Vector3( 0, 0, 1 ), new Vector3( 0, 0, 0 ), 2f ), 20f, "origin plane offset is plain xS" );
		} );

		registry.Add( "particle-scaling", "kill volumes subemitters and mesh reference", () =>
		{
			var scaled = ParticleScaling.ScaleSettings( Settings(), 2f, ParticleScalingMode.WorldScale );
			Eq( scaled.KillVolumes[0].Offset, 20f, "kill plane offset" );
			EqV( scaled.KillVolumes[1].Center, new Vector3( 2f, 2f, 2f ), "kill sphere centre" );
			Eq( scaled.KillVolumes[1].Radius, 6f, "kill sphere radius" );
			Check( scaled.KillVolumes[1].KillInside == false, "kill inside flag unchanged" );
			var mesh = new ParticleMesh( new[] { new Vector3( 0, 0, 0 ), new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ) }, new[] { 0, 1, 2 } );
			var emitter = Emitter();
			emitter.Mesh = mesh;
			var em = ParticleScaling.ScaleEmitter( emitter, 2f, ParticleScalingMode.WorldScale );
			Check( ReferenceEquals( em.Mesh, mesh ), "mesh shared by reference, not rescaled (sweep must pre-scale it)" );
			Check( em.DeathSubEmitter is not null && em.DeathSubEmitter.ChildTemplate is not null, "child template present" );
			Eq( em.DeathSubEmitter!.ChildTemplate!.SizeMin, 2f, "child template size scaled" );
			Eq( em.DeathSubEmitter.ChildTemplate.Speed, 12f, "child template speed scaled" );
			Eq( em.DeathSubEmitter.Count, 3, "child count unchanged" );
			Eq( em.DeathSubEmitter.VelocityInherit, 0.5f, "velocity inherit unchanged" );
			Check( em.CollisionSubEmitter is not null && em.CollisionSubEmitter.ChildPreset == "sparks" && em.CollisionSubEmitter.ChildTemplate is null, "preset sub-emitter passes through" );
		} );

		registry.Add( "particle-scaling", "factor table derived from triples", () =>
		{
			Eq( ParticleScaling.Factor( ParticleScalingMode.WorldScale, ParticleScaleQuantity.Length, 2f ), 2f, "world length" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.WorldScale, ParticleScaleQuantity.Rate, 2f ), 2f, "world rate" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.WorldScale, ParticleScaleQuantity.Acceleration, 2f ), 2f, "world accel" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.WorldScale, ParticleScaleQuantity.InverseLength, 2f ), 0.5f, "world inverse length" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.WorldScale, ParticleScaleQuantity.VortexStrength, 2f ), 4f, "world vortex strength" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.WorldScale, ParticleScaleQuantity.QuadraticDrag, 2f ), 0.5f, "world quadratic drag" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.WorldScale, ParticleScaleQuantity.LinearDrag, 2f ), 1f, "world linear drag" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.WorldScale, ParticleScaleQuantity.Time, 2f ), 1f, "world time" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.WorldScale, ParticleScaleQuantity.RatePerSecond, 2f ), 1f, "world rate per second" );
			foreach ( var q in new[] { ParticleScaleQuantity.Length, ParticleScaleQuantity.Rate, ParticleScaleQuantity.Acceleration, ParticleScaleQuantity.InverseLength, ParticleScaleQuantity.VortexStrength, ParticleScaleQuantity.QuadraticDrag, ParticleScaleQuantity.LinearDrag, ParticleScaleQuantity.Time })
				Eq( ParticleScaling.Factor( ParticleScalingMode.PreserveWorld, q, 2f ), 1f, $"preserve factor {q}" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.ScaleSizeOnly, ParticleScaleQuantity.Length, 2f ), 2f, "sizesonly length" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.ScaleSizeOnly, ParticleScaleQuantity.Rate, 2f ), 1f, "sizesonly rate" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.ScaleSizeOnly, ParticleScaleQuantity.Acceleration, 2f ), 1f, "sizesonly accel" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.ScaleSizeOnly, ParticleScaleQuantity.InverseLength, 2f ), 0.5f, "sizesonly inverse length" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.ScaleSizeOnly, ParticleScaleQuantity.VortexStrength, 2f ), 2f, "sizesonly vortex strength" );
			Eq( ParticleScaling.Factor( ParticleScalingMode.ScaleSizeOnly, ParticleScaleQuantity.QuadraticDrag, 2f ), 1f, "sizesonly quadratic drag" );
			Throws<ArgumentException>( () => ParticleScaling.Factor( (ParticleScalingMode)99, ParticleScaleQuantity.Length, 2f ), "factor unknown mode" );
			Throws<ArgumentException>( () => ParticleScaling.Factor( ParticleScalingMode.WorldScale, (ParticleScaleQuantity)99, 2f ), "factor unknown quantity" );
		} );
	}
}
