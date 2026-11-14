using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

internal static class PreviewEffectsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "previeweffects", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: wrong exception {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}
		bool Near( float a, float b, float eps = 1e-3f ) => MathF.Abs( a - b ) <= eps;
		bool NearVec( Vec3 a, Vec3 b, float eps = 1e-3f ) => a.AlmostEquals( b, eps );

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-preview-wysiwyg-effects" );
					if ( File.Exists( Path.Combine( candidate, "preview-scene.json" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "previeweffects fixture dir not found (preview-scene.json missing above cwd / BaseDirectory)" );
		}

		PreviewEffects.PreviewParticleSystem Smoke() => new PreviewEffects.PreviewParticleSystem
		{
			Name = "smoke",
			Origin = new Vec3( 0, 0, 64 ),
			Direction = new Vec3( 0, 0, 1 ),
			EmissionRate = 10,
			Lifetime = 2,
			Speed = 50,
			Spread = 0,
			Gravity = Vec3.Zero,
			StartSize = 4,
			EndSize = 8,
		};

		// ---- Particles ----

		Check( "particle system at t=0 and t=1 gives different positions", () =>
		{
			var at0 = PreviewEffects.EvaluateParticles( Smoke(), 0 );
			var at1 = PreviewEffects.EvaluateParticles( Smoke(), 1 );
			Require( at0.AliveCount == 1, $"t=0 alive {at0.AliveCount}, expected 1" );
			Require( at1.AliveCount == 11, $"t=1 alive {at1.AliveCount}, expected 11" );
			Require( NearVec( at0.Particles[0].Position, new Vec3( 0, 0, 64 ) ), $"t=0 particle at {at0.Particles[0].Position}" );
			Require( NearVec( at1.Particles[0].Position, new Vec3( 0, 0, 114 ) ), $"t=1 particle 0 at {at1.Particles[0].Position}, expected (0, 0, 114)" );
			Require( !NearVec( at0.Particles[0].Position, at1.Particles[0].Position ), "particle did not move between t=0 and t=1" );
			Require( !at0.Capped && !at1.Capped, "small system reported capped" );
		} );

		Check( "particle position is exact with zero spread and zero gravity", () =>
		{
			var system = new PreviewEffects.PreviewParticleSystem
			{
				Name = "straight",
				Origin = Vec3.Zero,
				Direction = new Vec3( 1, 0, 0 ),
				EmissionRate = 1,
				Lifetime = 10,
				Speed = 100,
				Spread = 0,
				Gravity = Vec3.Zero,
			};
			var result = PreviewEffects.EvaluateParticles( system, 1 );
			Require( result.AliveCount == 2, $"alive {result.AliveCount}, expected 2 (spawned at t=0 and t=1)" );
			Require( result.Particles[0].Index == 0 && result.Particles[1].Index == 1, "particle indices are not spawn order" );
			Require( NearVec( result.Particles[0].Position, new Vec3( 100, 0, 0 ) ), $"particle 0 at {result.Particles[0].Position}, expected (100, 0, 0)" );
			Require( NearVec( result.Particles[1].Position, Vec3.Zero ), $"particle 1 at {result.Particles[1].Position}, expected origin" );
			Require( Near( result.Particles[0].Age, 1f ), $"particle 0 age {result.Particles[0].Age}" );
			Require( Near( result.Particles[0].LifeFraction, 0.1f ), $"particle 0 life fraction {result.Particles[0].LifeFraction}" );
		} );

		Check( "dead particles drop out after their lifetime", () =>
		{
			var system = new PreviewEffects.PreviewParticleSystem
			{
				Name = "short",
				Origin = Vec3.Zero,
				Direction = new Vec3( 0, 0, 1 ),
				EmissionRate = 1,
				Lifetime = 1,
				Speed = 10,
				Spread = 0,
			};
			var result = PreviewEffects.EvaluateParticles( system, 3 );
			Require( result.AliveCount == 1, $"alive {result.AliveCount}, expected 1" );
			Require( result.Particles[0].Index == 3, $"survivor index {result.Particles[0].Index}, expected 3" );
			Require( Near( result.Particles[0].Age, 0f ), $"survivor age {result.Particles[0].Age}" );
		} );

		Check( "gravity pulls particles down over time", () =>
		{
			var system = new PreviewEffects.PreviewParticleSystem
			{
				Name = "falling",
				Origin = new Vec3( 0, 0, 1000 ),
				Direction = new Vec3( 0, 0, 1 ),
				EmissionRate = 1,
				Lifetime = 10,
				Speed = 0,
				Spread = 0,
				Gravity = new Vec3( 0, 0, -100 ),
			};
			var result = PreviewEffects.EvaluateParticles( system, 1 );
			Require( NearVec( result.Particles[0].Position, new Vec3( 0, 0, 950 ) ),
				$"particle at {result.Particles[0].Position}, expected (0, 0, 950) after 0.5*g*t^2" );
		} );

		Check( "zero emission rate emits nothing and negative time clamps to zero", () =>
		{
			var system = Smoke();
			system.EmissionRate = 0;
			Require( PreviewEffects.EvaluateParticles( system, 5 ).AliveCount == 0, "zero-rate system emitted particles" );
			system.EmissionRate = 10;
			var neg = PreviewEffects.EvaluateParticles( system, -5 );
			var zero = PreviewEffects.EvaluateParticles( system, 0 );
			Require( neg.AliveCount == zero.AliveCount, "negative t did not clamp to t=0" );
			Require( NearVec( neg.Particles[0].Position, zero.Particles[0].Position ), "negative-t position differs from t=0" );
		} );

		// ---- Fog ----

		Check( "fog density rises with distance", () =>
		{
			var controller = new PreviewEffects.PreviewFogController
			{
				Origin = Vec3.Zero,
				FogStart = 128,
				FogEnd = 1024,
				FogMaxDensity = 0.9f,
				FogColor = new Vec3( 0.5f, 0.6f, 0.7f ),
				Enabled = true,
			};
			var near = PreviewEffects.EvaluateFog( controller, new Vec3( 100, 0, 0 ) );
			var mid = PreviewEffects.EvaluateFog( controller, new Vec3( 512, 0, 0 ) );
			var far = PreviewEffects.EvaluateFog( controller, new Vec3( 2000, 0, 0 ) );
			Require( Near( near.Density, 0f ), $"inside fogstart density {near.Density}, expected 0" );
			Require( Near( mid.Density, 0.3857143f ), $"mid density {mid.Density}, expected 0.3857143" );
			Require( Near( mid.Factor, 0.4285714f ), $"mid factor {mid.Factor}, expected 0.4285714" );
			Require( Near( far.Density, 0.9f ), $"beyond fogend density {far.Density}, expected max 0.9" );
			Require( near.Density < mid.Density && mid.Density < far.Density, "fog density does not rise with distance" );
			Require( NearVec( mid.Color, new Vec3( 0.5f, 0.6f, 0.7f ) ), $"fog colour {mid.Color} did not pass through" );
			Require( Near( mid.Distance, 512f ), $"sample distance {mid.Distance}, expected 512" );
		} );

		Check( "disabled fog is zero, degenerate range guards, camera-to-sample overload", () =>
		{
			var controller = new PreviewEffects.PreviewFogController
			{
				Origin = Vec3.Zero,
				FogStart = 128,
				FogEnd = 1024,
				FogMaxDensity = 0.9f,
				Enabled = false,
			};
			Require( Near( PreviewEffects.EvaluateFog( controller, new Vec3( 512, 0, 0 ) ).Density, 0f ), "disabled fog has density" );
			controller.Enabled = true;
			var at = PreviewEffects.EvaluateFogAt( controller, Vec3.Zero, new Vec3( 512, 0, 0 ) );
			Require( Near( at.Density, 0.3857143f ), $"camera-to-sample density {at.Density}, expected 0.3857143" );
			var degenerate = new PreviewEffects.PreviewFogController { FogStart = 500, FogEnd = 500, FogMaxDensity = 1f };
			Require( Near( PreviewEffects.EvaluateFog( degenerate, new Vec3( 400, 0, 0 ) ).Density, 0f ), "degenerate range fogged before start" );
			Require( Near( PreviewEffects.EvaluateFog( degenerate, new Vec3( 600, 0, 0 ) ).Density, 1f ), "degenerate range did not reach max past start" );
		} );

		// ---- Ropes ----

		Check( "a rope sags below its endpoints with the documented depth", () =>
		{
			var span = new PreviewEffects.PreviewRopeSpan( new Vec3( -64, 0, 128 ), new Vec3( 64, 0, 128 ) );
			var result = PreviewEffects.EvaluateRope( span, 8, 16 );
			Require( result.Points.Count == 17, $"points {result.Points.Count}, expected 17" );
			Require( NearVec( result.Points[0], span.Start ), $"first point {result.Points[0]} is not the start" );
			Require( NearVec( result.Points[16], span.End ), $"last point {result.Points[16]} is not the end" );
			Require( Near( result.SagDepth, 19.5959f ), $"sag {result.SagDepth}, expected sqrt(3*128*8/8) = 19.5959" );
			Require( NearVec( result.Points[8], new Vec3( 0, 0, 108.4041f ) ), $"midpoint {result.Points[8]}, expected (0, 0, 108.4041)" );
			for ( int i = 1; i < 16; i++ )
				Require( result.Points[i].z < 128f, $"point {i} at z={result.Points[i].z} is not below the endpoints" );
			Require( result.Length > result.StraightLength, $"rope length {result.Length} not longer than straight {result.StraightLength}" );
			Require( Near( result.StraightLength, 128f ), $"straight length {result.StraightLength}" );
		} );

		Check( "zero slack is a straight rope and coincident endpoints collapse", () =>
		{
			var span = new PreviewEffects.PreviewRopeSpan( new Vec3( -64, 0, 128 ), new Vec3( 64, 0, 128 ) );
			var taut = PreviewEffects.EvaluateRope( span, 0, 8 );
			Require( Near( taut.SagDepth, 0f ), $"taut sag {taut.SagDepth}" );
			Require( Near( taut.Length, 128f ), $"taut length {taut.Length}, expected 128" );
			foreach ( var p in taut.Points )
				Require( Near( p.z, 128f ), $"taut point {p} left the straight line" );
			var point = new PreviewEffects.PreviewRopeSpan( new Vec3( 1, 2, 3 ), new Vec3( 1, 2, 3 ) );
			var collapsed = PreviewEffects.EvaluateRope( point, 5, 4 );
			Require( Near( collapsed.SagDepth, 0f ), "coincident rope sagged" );
			foreach ( var p in collapsed.Points )
				Require( NearVec( p, new Vec3( 1, 2, 3 ) ), $"collapsed point {p} moved" );
		} );

		// ---- Proxies ----

		Check( "an animated texture's UV offset advances with time and wraps", () =>
		{
			var material = new PreviewEffects.PreviewMaterialProxy
			{
				MaterialName = "brick/scroll",
				ScrollRateU = 0.3f,
				ScrollRateV = 0.1f,
				ScaleU = 2f,
				ScaleV = 1f,
			};
			var at0 = PreviewEffects.EvaluateProxy( material, 0 );
			var at1 = PreviewEffects.EvaluateProxy( material, 1 );
			var at25 = PreviewEffects.EvaluateProxy( material, 2.5f );
			var at4 = PreviewEffects.EvaluateProxy( material, 4 );
			Require( Near( at0.OffsetU, 0f ) && Near( at0.OffsetV, 0f ), "offset at t=0 is not zero" );
			Require( Near( at1.OffsetU, 0.3f ) && Near( at1.OffsetV, 0.1f ), $"offset at t=1 is ({at1.OffsetU}, {at1.OffsetV})" );
			Require( Near( at25.OffsetU, 0.75f ) && Near( at25.OffsetV, 0.25f ), $"offset at t=2.5 is ({at25.OffsetU}, {at25.OffsetV})" );
			Require( Near( at4.OffsetU, 0.2f ) && Near( at4.OffsetV, 0.4f ), $"offset at t=4 is ({at4.OffsetU}, {at4.OffsetV}), expected wrapped (0.2, 0.4)" );
			Require( Near( at1.ScaleU, 2f ) && Near( at1.ScaleV, 1f ), "scale did not pass through" );
		} );

		Check( "proxy rotation and animated frames advance with time", () =>
		{
			var material = new PreviewEffects.PreviewMaterialProxy
			{
				MaterialName = "spin/frames",
				RotateRateDegrees = 90f,
				Frames = 4,
				FrameRate = 2f,
			};
			Require( Near( PreviewEffects.EvaluateProxy( material, 0 ).RotationDegrees, 0f ), "rotation at t=0 is not zero" );
			Require( Near( PreviewEffects.EvaluateProxy( material, 1 ).RotationDegrees, 90f ), "rotation at t=1 is not 90" );
			Require( Near( PreviewEffects.EvaluateProxy( material, 5 ).RotationDegrees, 90f ), "rotation at t=5 did not wrap 450 -> 90" );
			Require( PreviewEffects.EvaluateProxy( material, 0 ).FrameIndex == 0, "frame at t=0 is not 0" );
			Require( PreviewEffects.EvaluateProxy( material, 0.75f ).FrameIndex == 1, "frame at t=0.75 is not 1" );
			Require( PreviewEffects.EvaluateProxy( material, 2 ).FrameIndex == 0, "frame at t=2 did not wrap 4 -> 0" );
			var unframed = new PreviewEffects.PreviewMaterialProxy { MaterialName = "plain" };
			Require( PreviewEffects.EvaluateProxy( unframed, 3 ).FrameIndex == -1, "unframed material reported a frame" );
		} );

		// ---- Render effects ----

		Check( "renderfx colour, alpha, bodygroup and scale evaluate per mode", () =>
		{
			var normal = new PreviewEffects.PreviewRenderFxEntity
			{
				ClassName = "prop_static",
				Mode = PreviewEffects.PreviewRenderMode.Normal,
				RenderColor = new Vec3( 255, 255, 255 ),
				RenderAmt = 255,
				BodyGroup = 1,
				Scale = 2f,
			};
			var n = PreviewEffects.EvaluateRenderFx( normal );
			Require( Near( n.Alpha, 1f ), $"normal alpha {n.Alpha}, expected 1" );
			Require( NearVec( n.Color, new Vec3( 1, 1, 1 ) ), "normal colour is not white" );
			Require( n.BodyGroup == 1 && Near( n.Scale, 2f ), "bodygroup/scale did not pass through" );
			Require( n.IsVisible, "opaque entity reported invisible" );

			var trans = new PreviewEffects.PreviewRenderFxEntity
			{
				ClassName = "prop_static",
				Mode = PreviewEffects.PreviewRenderMode.TransAlpha,
				RenderColor = new Vec3( 255, 128, 0 ),
				RenderAmt = 128,
				BodyGroup = 2,
				Scale = 1.5f,
			};
			var t = PreviewEffects.EvaluateRenderFx( trans );
			Require( Near( t.Alpha, 128f / 255f ), $"transalpha alpha {t.Alpha}, expected 128/255" );
			Require( NearVec( t.Color, new Vec3( 1, 128f / 255f, 0 ) ), $"trans colour {t.Color}" );
			Require( t.BodyGroup == 2 && Near( t.Scale, 1.5f ), "trans bodygroup/scale did not pass through" );

			var invisible = new PreviewEffects.PreviewRenderFxEntity
			{
				ClassName = "prop_static",
				Mode = PreviewEffects.PreviewRenderMode.TransAdd,
				RenderAmt = 0,
			};
			var inv = PreviewEffects.EvaluateRenderFx( invisible );
			Require( Near( inv.Alpha, 0f ) && !inv.IsVisible, "zero-amt entity reported visible" );

			var clamped = new PreviewEffects.PreviewRenderFxEntity
			{
				ClassName = "prop_static",
				Mode = PreviewEffects.PreviewRenderMode.TransAlpha,
				RenderAmt = 300,
				Scale = -1f,
			};
			var c = PreviewEffects.EvaluateRenderFx( clamped );
			Require( Near( c.Alpha, 1f ), $"amt 300 alpha {c.Alpha}, expected clamped 1" );
			Require( Near( c.Scale, 0f ), $"negative scale {c.Scale}, expected clamped 0" );
		} );

		Check( "time-varying renderfx modulate alpha with time, deterministically", () =>
		{
			PreviewEffects.PreviewRenderFxEntity Pulse( float time ) => new PreviewEffects.PreviewRenderFxEntity
			{
				ClassName = "prop_dynamic",
				Mode = PreviewEffects.PreviewRenderMode.TransAlpha,
				Fx = PreviewEffects.PreviewRenderFx.PulseSlow,
				RenderAmt = 255,
				TimeSeconds = time,
			};
			Require( Near( PreviewEffects.EvaluateRenderFx( Pulse( 0 ) ).Alpha, 0.5f ), "pulse at t=0 is not 0.5" );
			Require( Near( PreviewEffects.EvaluateRenderFx( Pulse( 0.5f ) ).Alpha, 1f ), "pulse at t=0.5 is not 1" );
			Require( Near( PreviewEffects.EvaluateRenderFx( Pulse( 1 ) ).Alpha, 0.5f ), "pulse at t=1 is not 0.5" );

			PreviewEffects.PreviewRenderFxEntity Strobe( float time ) => new PreviewEffects.PreviewRenderFxEntity
			{
				ClassName = "prop_dynamic",
				Mode = PreviewEffects.PreviewRenderMode.TransAlpha,
				Fx = PreviewEffects.PreviewRenderFx.StrobeSlow,
				RenderAmt = 255,
				TimeSeconds = time,
			};
			Require( Near( PreviewEffects.EvaluateRenderFx( Strobe( 0 ) ).Alpha, 1f ), "strobe at t=0 is not on" );
			Require( Near( PreviewEffects.EvaluateRenderFx( Strobe( 0.75f ) ).Alpha, 0f ), "strobe at t=0.75 is not off" );
			Require( !PreviewEffects.EvaluateRenderFx( Strobe( 0.75f ) ).IsVisible, "strobe-off entity reported visible" );

			var fade0 = new PreviewEffects.PreviewRenderFxEntity
			{
				ClassName = "prop_dynamic",
				Mode = PreviewEffects.PreviewRenderMode.TransAlpha,
				Fx = PreviewEffects.PreviewRenderFx.FadeSlow,
				RenderAmt = 255,
				TimeSeconds = 0,
			};
			Require( Near( PreviewEffects.EvaluateRenderFx( fade0 ).Alpha, 0f ), "fade at t=0 is not 0" );

			PreviewEffects.PreviewRenderFxEntity Flicker( float time ) => new PreviewEffects.PreviewRenderFxEntity
			{
				ClassName = "prop_dynamic",
				Mode = PreviewEffects.PreviewRenderMode.TransAlpha,
				Fx = PreviewEffects.PreviewRenderFx.FlickerFast,
				RenderAmt = 255,
				TimeSeconds = time,
			};
			float a1 = PreviewEffects.EvaluateRenderFx( Flicker( 0.37f ) ).Alpha;
			float a2 = PreviewEffects.EvaluateRenderFx( Flicker( 0.37f ) ).Alpha;
			Require( Near( a1, a2 ), "flicker is not deterministic at the same time" );
			Require( a1 >= 0.3f && a1 <= 1f, $"flicker alpha {a1} outside [0.3, 1]" );
		} );

		// ---- Invalid inputs ----

		Check( "null inputs throw and negative rope slack throws", () =>
		{
			Throws<ArgumentNullException>( () => PreviewEffects.EvaluateParticles( null, 0 ), "null particle system" );
			Throws<ArgumentNullException>( () => PreviewEffects.EvaluateFog( null, Vec3.Zero ), "null fog controller" );
			Throws<ArgumentNullException>( () => PreviewEffects.EvaluateFogAt( null, Vec3.Zero, Vec3.Zero ), "null fog controller (overload)" );
			Throws<ArgumentNullException>( () => PreviewEffects.EvaluateRope( null, 0 ), "null rope span" );
			Throws<ArgumentNullException>( () => PreviewEffects.EvaluateProxy( null, 0 ), "null material proxy" );
			Throws<ArgumentNullException>( () => PreviewEffects.EvaluateRenderFx( null ), "null renderfx entity" );
			Throws<ArgumentNullException>( () => PreviewEffects.EvaluateBatch( null, null ), "null batch input" );
			Throws<ArgumentOutOfRangeException>( () => PreviewEffects.EvaluateRope( new PreviewEffects.PreviewRopeSpan( Vec3.Zero, new Vec3( 10, 0, 0 ) ), -1 ), "negative slack" );
			Throws<ArgumentOutOfRangeException>( () => PreviewEffects.EvaluateRope( new PreviewEffects.PreviewRopeSpan( Vec3.Zero, new Vec3( 10, 0, 0 ) ), 0, 0 ), "zero segments" );
			Throws<ArgumentOutOfRangeException>( () => PreviewEffects.EvaluateRope( new PreviewEffects.PreviewRopeSpan( Vec3.Zero, new Vec3( 10, 0, 0 ) ), 0, 300 ), "too many segments" );
		} );

		// ---- Toggles + batch ----

		Check( "per-effect toggles gate the batch evaluator", () =>
		{
			var toggles = new PreviewEffects.PreviewToggles();
			Require( toggles.IsEnabled( PreviewEffects.PreviewEffectKind.Particles ), "default toggles disable particles" );
			Require( toggles.IsEnabled( PreviewEffects.PreviewEffectKind.Fog ), "default toggles disable fog" );
			Require( toggles.IsEnabled( PreviewEffects.PreviewEffectKind.Ropes ), "default toggles disable ropes" );
			Require( toggles.IsEnabled( PreviewEffects.PreviewEffectKind.Proxies ), "default toggles disable proxies" );
			Require( toggles.IsEnabled( PreviewEffects.PreviewEffectKind.RenderFx ), "default toggles disable renderfx" );
			var off = PreviewEffects.PreviewToggles.AllOff();
			foreach ( PreviewEffects.PreviewEffectKind kind in Enum.GetValues<PreviewEffects.PreviewEffectKind>() )
				Require( !off.IsEnabled( kind ), $"AllOff still enables {kind}" );
			var noFog = toggles.With( PreviewEffects.PreviewEffectKind.Fog, false );
			Require( !noFog.IsEnabled( PreviewEffects.PreviewEffectKind.Fog ), "With(false) did not disable fog" );
			Require( noFog.IsEnabled( PreviewEffects.PreviewEffectKind.Particles ), "With(false) on fog also disabled particles" );
			Require( toggles.IsEnabled( PreviewEffects.PreviewEffectKind.Fog ), "With mutated the original toggles" );

			var input = new PreviewEffects.PreviewBatchInput
			{
				Particles = Smoke(),
				Fog = new PreviewEffects.PreviewFogController { FogStart = 128, FogEnd = 1024, FogMaxDensity = 0.9f },
				FogCameraPos = new Vec3( 512, 0, 0 ),
				Rope = new PreviewEffects.PreviewRopeSpan( new Vec3( -64, 0, 128 ), new Vec3( 64, 0, 128 ) ),
				RopeSlack = 8,
				Proxy = new PreviewEffects.PreviewMaterialProxy { MaterialName = "brick/scroll", ScrollRateU = 0.25f },
				RenderFx = new PreviewEffects.PreviewRenderFxEntity { ClassName = "prop_static", Mode = PreviewEffects.PreviewRenderMode.TransAlpha, RenderAmt = 128 },
				Time = 1,
			};
			var all = PreviewEffects.EvaluateBatch( input, null );
			Require( all.Particles is not null && all.Fog is not null && all.Rope is not null && all.Proxy is not null && all.RenderFx is not null,
				"batch with all inputs and null toggles missed a result" );
			var solo = PreviewEffects.EvaluateParticles( input.Particles, 1 );
			Require( all.Particles.AliveCount == solo.AliveCount, "batch particle count differs from the single-effect call" );
			Require( NearVec( all.Particles.Particles[0].Position, solo.Particles[0].Position ), "batch particle position differs from the single-effect call" );

			var gated = PreviewEffects.EvaluateBatch( input, toggles.With( PreviewEffects.PreviewEffectKind.Particles, false ).With( PreviewEffects.PreviewEffectKind.Fog, false ) );
			Require( gated.Particles is null, "disabled particles still evaluated in batch" );
			Require( gated.Fog is null, "disabled fog still evaluated in batch" );
			Require( gated.Rope is not null && gated.Proxy is not null && gated.RenderFx is not null, "enabled kinds went missing in gated batch" );

			var empty = PreviewEffects.EvaluateBatch( new PreviewEffects.PreviewBatchInput(), new PreviewEffects.PreviewToggles() );
			Require( empty.Particles is null && empty.Fog is null && empty.Rope is null && empty.Proxy is null && empty.RenderFx is null,
				"batch with no inputs returned results" );
		} );

		// ---- Fixture ----

		Check( "fixture preview-scene.json evaluates to its hand-computed values", () =>
		{
			var path = Path.Combine( FixtureDir(), "preview-scene.json" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;

			Vec3 ReadVec3( JsonElement el ) => new Vec3(
				(float)el[0].GetDouble(), (float)el[1].GetDouble(), (float)el[2].GetDouble() );
			float F( JsonElement el, string name ) => (float)el.GetProperty( name ).GetDouble();

			var ps = root.GetProperty( "particleSystem" );
			var system = new PreviewEffects.PreviewParticleSystem
			{
				Name = ps.GetProperty( "name" ).GetString(),
				Origin = ReadVec3( ps.GetProperty( "origin" ) ),
				Direction = ReadVec3( ps.GetProperty( "direction" ) ),
				EmissionRate = F( ps, "emissionRate" ),
				Lifetime = F( ps, "lifetime" ),
				Speed = F( ps, "speed" ),
				Spread = F( ps, "spread" ),
				Gravity = ReadVec3( ps.GetProperty( "gravity" ) ),
				StartSize = F( ps, "startSize" ),
				EndSize = F( ps, "endSize" ),
			};
			var particles = PreviewEffects.EvaluateParticles( system, 1 );
			var expP = root.GetProperty( "particleAtT1" );
			Require( particles.AliveCount == expP.GetProperty( "aliveCount" ).GetInt32(), $"fixture alive {particles.AliveCount}" );
			Require( NearVec( particles.Particles[0].Position, ReadVec3( expP.GetProperty( "particle0Position" ) ) ), $"fixture particle 0 at {particles.Particles[0].Position}" );
			Require( Near( particles.Particles[0].LifeFraction, F( expP, "particle0LifeFraction" ) ), "fixture life fraction moved" );
			Require( Near( particles.Particles[0].Size, F( expP, "particle0Size" ) ), "fixture size moved" );
			Require( Near( particles.Particles[0].Alpha, F( expP, "particle0Alpha" ) ), "fixture alpha moved" );

			var fg = root.GetProperty( "fog" );
			var fog = new PreviewEffects.PreviewFogController
			{
				Origin = ReadVec3( fg.GetProperty( "origin" ) ),
				FogStart = F( fg, "start" ),
				FogEnd = F( fg, "end" ),
				FogMaxDensity = F( fg, "maxDensity" ),
				FogColor = ReadVec3( fg.GetProperty( "color" ) ),
				Enabled = fg.GetProperty( "enabled" ).GetBoolean(),
			};
			var fogAt = PreviewEffects.EvaluateFog( fog, new Vec3( 512, 0, 0 ) );
			var expF = root.GetProperty( "fogAtDistance512" );
			Require( Near( fogAt.Factor, F( expF, "factor" ) ), $"fixture fog factor {fogAt.Factor}" );
			Require( Near( fogAt.Density, F( expF, "density" ) ), $"fixture fog density {fogAt.Density}" );

			var rp = root.GetProperty( "rope" );
			var rope = PreviewEffects.EvaluateRope(
				new PreviewEffects.PreviewRopeSpan( ReadVec3( rp.GetProperty( "start" ) ), ReadVec3( rp.GetProperty( "end" ) ) ),
				F( rp, "slack" ), rp.GetProperty( "segments" ).GetInt32() );
			var expR = root.GetProperty( "ropeExpected" );
			Require( Near( rope.SagDepth, F( expR, "sagDepth" ) ), $"fixture sag {rope.SagDepth}" );
			Require( NearVec( rope.Points[rope.Points.Count / 2], ReadVec3( expR.GetProperty( "midpoint" ) ) ), $"fixture midpoint {rope.Points[rope.Points.Count / 2]}" );

			var px = root.GetProperty( "proxy" );
			var proxy = PreviewEffects.EvaluateProxy( new PreviewEffects.PreviewMaterialProxy
			{
				MaterialName = px.GetProperty( "material" ).GetString(),
				ScrollRateU = F( px, "scrollU" ),
				ScrollRateV = F( px, "scrollV" ),
				ScaleU = F( px, "scaleU" ),
				ScaleV = F( px, "scaleV" ),
				RotateRateDegrees = F( px, "rotateRate" ),
				Frames = px.GetProperty( "frames" ).GetInt32(),
				FrameRate = F( px, "frameRate" ),
			}, 2 );
			var expX = root.GetProperty( "proxyAtT2" );
			Require( Near( proxy.OffsetU, F( expX, "offsetU" ) ), $"fixture offsetU {proxy.OffsetU}" );
			Require( Near( proxy.OffsetV, F( expX, "offsetV" ) ), $"fixture offsetV {proxy.OffsetV}" );
			Require( Near( proxy.RotationDegrees, F( expX, "rotationDegrees" ) ), "fixture rotation moved" );
			Require( proxy.FrameIndex == expX.GetProperty( "frameIndex" ).GetInt32(), "fixture frame moved" );

			var rf = root.GetProperty( "renderFx" );
			var renderFx = PreviewEffects.EvaluateRenderFx( new PreviewEffects.PreviewRenderFxEntity
			{
				ClassName = rf.GetProperty( "className" ).GetString(),
				Mode = (PreviewEffects.PreviewRenderMode)rf.GetProperty( "mode" ).GetInt32(),
				Fx = (PreviewEffects.PreviewRenderFx)rf.GetProperty( "fx" ).GetInt32(),
				RenderColor = ReadVec3( rf.GetProperty( "color" ) ),
				RenderAmt = F( rf, "amt" ),
				BodyGroup = rf.GetProperty( "bodyGroup" ).GetInt32(),
				Scale = F( rf, "scale" ),
				TimeSeconds = F( rf, "time" ),
			} );
			var expRf = root.GetProperty( "renderFxExpected" );
			Require( Near( renderFx.Alpha, F( expRf, "alpha" ) ), $"fixture alpha {renderFx.Alpha}" );
			Require( NearVec( renderFx.Color, ReadVec3( expRf.GetProperty( "color" ) ) ), $"fixture colour {renderFx.Color}" );
			Require( renderFx.IsVisible == expRf.GetProperty( "visible" ).GetBoolean(), "fixture visibility moved" );
			Require( renderFx.BodyGroup == 2 && Near( renderFx.Scale, 1.5f ), "fixture bodygroup/scale moved" );
		} );
	}
}
