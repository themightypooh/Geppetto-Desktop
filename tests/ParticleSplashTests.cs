// SWEEP ITEM (coordinator only, do not add one-by-one):
//   ParticleSplashTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line in
// tests/TestRegistry.cs. This file is pure splash computation: it never
// touches ParticleSystem state, so it runs without a system instance.
using System;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleSplashTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		static void Near( float actual, float expected, float tol, string what )
		{
			if ( MathF.Abs( actual - expected ) > tol ) throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		static void NearVec( Vector3 actual, Vector3 expected, float tol, string what )
		{
			if ( Vector3.Distance( actual, expected ) > tol ) throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		static void Throws<T>( Action f, string what ) where T : Exception
		{
			try { f(); } catch ( T ) { return; }
			throw new Exception( "expected " + typeof( T ).Name + " for " + what );
		}
		static SplashSpec Spec() => new();
		static Vector3 Hit() => new( 1, 2, 0.01f );
		static Vector3 Falling( float speed ) => new( 0, 0, -speed );

		registry.Add( "particle-splash", "known hit and seed gives measured droplets in range", () =>
		{
			var rng = new ParticleRng( 12345 );
			var got = ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), Spec(), ref rng );
			Check( got.Length == 8, $"expected 8 droplets, got {got.Length}" );
			// Golden values (seed 12345, hit (1,2,0.01), velocity (0,0,-60)):
			// droplet 0 ≈ vel (-1.93,-5.38,24.65) speed 25.31 size 0.148 life 0.519
			// droplet 1 ≈ vel (-0.32, 3.23,12.08) speed 12.51 size 0.186 life 0.275
			// droplet 2 ≈ vel ( 9.36, 1.93,26.41) speed 28.09 size 0.169 life 0.406
			// all at position (1,2,0.03) = hit + 0.02 upward offset.
			NearVec( got[0].Position, new Vector3( 1, 2, 0.03f ), 1e-4f, "droplet 0 position" );
			NearVec( got[0].Velocity, new Vector3( -1.93f, -5.38f, 24.65f ), 0.05f, "droplet 0 velocity" );
			Near( got[0].Velocity.Length(), 25.31f, 0.05f, "droplet 0 speed" );
			Near( got[0].Size, 0.148f, 0.01f, "droplet 0 size" );
			Near( got[0].Lifetime, 0.519f, 0.01f, "droplet 0 lifetime" );
			NearVec( got[1].Velocity, new Vector3( -0.32f, 3.23f, 12.08f ), 0.05f, "droplet 1 velocity" );
			Near( got[1].Size, 0.186f, 0.01f, "droplet 1 size" );
			Near( got[1].Lifetime, 0.275f, 0.01f, "droplet 1 lifetime" );
			NearVec( got[2].Velocity, new Vector3( 9.36f, 1.93f, 26.41f ), 0.05f, "droplet 2 velocity" );
			Near( got[2].Size, 0.169f, 0.01f, "droplet 2 size" );
			Near( got[2].Lifetime, 0.406f, 0.01f, "droplet 2 lifetime" );
			foreach ( var d in got )
			{
				Check( d.Position == new Vector3( 1, 2, 0.03f ), $"droplet not at hit + offset: {d.Position}" );
				Check( d.Size >= 0.06f && d.Size <= 0.20f, $"size out of range: {d.Size}" );
				Check( d.Lifetime >= 0.25f && d.Lifetime <= 0.60f, $"lifetime out of range: {d.Lifetime}" );
				float speed = d.Velocity.Length();
				Check( speed >= 12f && speed <= 36f, $"speed out of range: {speed}" );
			}
		} );

		registry.Add( "particle-splash", "below threshold is zero droplets, at threshold is full count", () =>
		{
			var rng = new ParticleRng( 7 );
			var gentle = ParticleSplash.SpawnSplash( Hit(), Falling( 10 ), Spec(), ref rng );
			Check( gentle.Length == 0, $"gentle landing (10 < 24) should make 0 droplets, got {gentle.Length}" );
			var at = ParticleSplash.SpawnSplash( Hit(), Falling( 24 ), Spec(), ref rng );
			Check( at.Length == 8, $"impact exactly at threshold (24) should make full 8, got {at.Length}" );
			var hard = ParticleSplash.SpawnSplash( Hit(), Falling( 100 ), Spec(), ref rng );
			Check( hard.Length == 8, $"hard impact should make full 8, got {hard.Length}" );
			// Threshold is on speed magnitude, not just the Z component.
			var sideways = ParticleSplash.SpawnSplash( Hit(), new Vector3( 30, 0, 0 ), Spec(), ref rng );
			Check( sideways.Length == 8, $"sideways speed 30 >= 24 should splash, got {sideways.Length}" );
		} );

		registry.Add( "particle-splash", "per-hit cap truncates and drops the excess", () =>
		{
			var spec = Spec(); spec.DropletCount = 500; spec.MaxDropletsPerStep = 64;
			var rng = new ParticleRng( 3 );
			var got = ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), spec, ref rng );
			Check( got.Length == 64, $"500 requested with cap 64 should give 64, got {got.Length}" );
			// Dropped droplets are not queued: a second hit is capped the same way, not topped up.
			var again = ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), spec, ref rng );
			Check( again.Length == 64, $"second hit should also be capped at 64, got {again.Length}" );
		} );

		registry.Add( "particle-splash", "rate limiter budgets a step across hits and counts drops", () =>
		{
			var spec = Spec(); // 8 per hit
			var limiter = new SplashRateLimiter( 10 );
			var rng = new ParticleRng( 11 );
			var first = ParticleSplash.SpawnSplashLimited( Hit(), Falling( 60 ), spec, ref rng, limiter );
			var second = ParticleSplash.SpawnSplashLimited( Hit(), Falling( 60 ), spec, ref rng, limiter );
			var third = ParticleSplash.SpawnSplashLimited( Hit(), Falling( 60 ), spec, ref rng, limiter );
			Check( first.Length == 8, $"first hit should get 8, got {first.Length}" );
			Check( second.Length == 2, $"second hit should get only the remaining 2, got {second.Length}" );
			Check( third.Length == 0, $"third hit should get 0 once the budget is spent, got {third.Length}" );
			Check( limiter.DroppedCount == 14, $"dropped should be 6 + 8 = 14, got {limiter.DroppedCount}" );
			Check( limiter.AllowedCount == 10, $"allowed should be 10, got {limiter.AllowedCount}" );
			limiter.ResetStep();
			var after = ParticleSplash.SpawnSplashLimited( Hit(), Falling( 60 ), spec, ref rng, limiter );
			Check( after.Length == 8, $"after a step reset the budget is full again, got {after.Length}" );
		} );

		registry.Add( "particle-splash", "all droplet velocities stay inside the cone and point upward", () =>
		{
			var spec = Spec(); spec.DropletCount = 200;
			var rng = new ParticleRng( 99 );
			var got = ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), spec, ref rng );
			float cosMax = MathF.Cos( 35f * MathF.PI / 180f );
			foreach ( var d in got )
			{
				var dir = Vector3.Normalize( d.Velocity );
				Check( dir.Z > 0, $"droplet velocity must point upward, got {d.Velocity}" );
				Check( dir.Z >= cosMax - 1e-4f, $"droplet angle exceeds 35° cone: dir {dir}, cos {dir.Z} < {cosMax}" );
			}
		} );

		registry.Add( "particle-splash", "tilted normal opens the cone around that normal", () =>
		{
			var rng = new ParticleRng( 5 );
			var normal = Vector3.Normalize( new Vector3( 1, 0, 1 ) );
			var got = ParticleSplash.SpawnSplashForNormal( Hit(), Falling( 60 ), Spec(), normal, ref rng );
			Check( got.Length == 8, $"expected 8, got {got.Length}" );
			NearVec( got[0].Position, Hit() + normal * 0.02f, 1e-4f, "tilted spawn position" );
			foreach ( var d in got )
				Check( Vector3.Dot( Vector3.Normalize( d.Velocity ), normal ) >= MathF.Cos( 35f * MathF.PI / 180f ) - 1e-4f,
					$"droplet outside cone around tilted normal: {d.Velocity}" );
		} );

		registry.Add( "particle-splash", "droplets are smaller than the parent at the documented ratio", () =>
		{
			// Default sizes 0.06–0.20 in against a 0.5 in parent = ratio 0.12–0.40.
			var rng = new ParticleRng( 21 );
			var got = ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), Spec(), ref rng );
			foreach ( var d in got )
				Check( d.Size < 0.5f, $"droplet size {d.Size} is not smaller than the 0.5 in parent" );
			// Parent-size scaling preserves the ratio: a 1.0 in parent doubles sizes to 0.12–0.40.
			var rng2 = new ParticleRng( 21 );
			var big = ParticleSplash.SpawnSplashForParentSize( Hit(), Falling( 60 ), 1.0f, Spec(), ref rng2 );
			Check( big.Length == got.Length, "parent-size splash should keep the count" );
			for ( int i = 0; i < got.Length; i++ )
				Near( big[i].Size, got[i].Size * 2, 1e-4f, $"scaled size {i}" );
			Throws<ArgumentOutOfRangeException>( () =>
			{
				var r = new ParticleRng( 1 );
				ParticleSplash.SpawnSplashForParentSize( Hit(), Falling( 60 ), 0, Spec(), ref r );
			}, "zero parent size" );
		} );

		registry.Add( "particle-splash", "fixed seed reproduces the splash exactly, other seed differs", () =>
		{
			var a = new ParticleRng( 4242 );
			var b = new ParticleRng( 4242 );
			var c = new ParticleRng( 4243 );
			var first = ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), Spec(), ref a );
			var second = ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), Spec(), ref b );
			var other = ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), Spec(), ref c );
			Check( first.Length == second.Length, "same seed must give same count" );
			for ( int i = 0; i < first.Length; i++ )
				Check( first[i].Equals( second[i] ), $"same seed droplet {i} differs: {first[i]} vs {second[i]}" );
			bool anyDiff = false;
			for ( int i = 0; i < first.Length; i++ ) if ( !first[i].Equals( other[i] ) ) anyDiff = true;
			Check( anyDiff, "a different seed should change at least one droplet" );
		} );

		registry.Add( "particle-splash", "Validate loudly rejects every bad field", () =>
		{
			void Bad( Action<SplashSpec> mutate, string what )
			{
				var s = Spec(); mutate( s );
				Throws<Exception>( () => s.Validate(), what );
				Throws<Exception>( () =>
				{
					var r = new ParticleRng( 1 );
					ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), s, ref r );
				}, "spawn with " + what );
			}
			Bad( s => s.DropletCount = -1, "negative count" );
			Bad( s => s.MaxDropletsPerStep = -1, "negative cap" );
			Bad( s => s.DropletSpeedMin = -1, "negative speed min" );
			Bad( s => s.DropletSpeedMax = 5, "speed max below min (5 < 12)" );
			Bad( s => s.DropletSpeedMin = float.NaN, "NaN speed" );
			Bad( s => s.DropletSizeMin = -0.1f, "negative size min" );
			Bad( s => s.DropletSizeMax = 0.01f, "size max below min" );
			Bad( s => s.DropletLifetimeMin = -1, "negative lifetime min" );
			Bad( s => s.DropletLifetimeMax = 0.1f, "lifetime max below min" );
			Bad( s => s.ImpactSpeedThreshold = -1, "negative threshold" );
			Bad( s => s.ConeHalfAngleDegrees = 0, "cone angle 0" );
			Bad( s => s.ConeHalfAngleDegrees = 90, "cone angle 90" );
			Bad( s => s.ConeHalfAngleDegrees = -10, "cone angle negative" );
			Bad( s => s.ConeHalfAngleDegrees = 120, "cone angle over 90" );
			Bad( s => s.SpawnHeightOffset = -0.01f, "negative spawn offset" );
			// Edges that must NOT throw: zero count, zero threshold, zero cap-adjacent values.
			var ok = Spec(); ok.DropletCount = 0; ok.Validate();
			var ok2 = Spec(); ok2.ImpactSpeedThreshold = 0; ok2.Validate();
			var ok3 = Spec(); ok3.ConeHalfAngleDegrees = 89.9f; ok3.Validate();
			var r2 = new ParticleRng( 1 );
			Check( ParticleSplash.SpawnSplash( Hit(), Falling( 60 ), ok, ref r2 ).Length == 0,
				"zero-count spec should emit nothing, not throw" );
		} );

		registry.Add( "particle-splash", "splash is on ground hit, not on death, by construction", () =>
		{
			// There is no death input anywhere in this file: SpawnSplash
			// takes only a hit position/velocity. This test pins the API
			// shape — a caller cannot feed it a death event, only a hit —
			// and pins that a zero-velocity (already landed, not impacting)
			// contact makes no droplets.
			var rng = new ParticleRng( 8 );
			var still = ParticleSplash.SpawnSplash( Hit(), Vector3.Zero, Spec(), ref rng );
			Check( still.Length == 0, $"zero impact velocity should make 0 droplets, got {still.Length}" );
			Throws<ArgumentException>( () =>
			{
				var r = new ParticleRng( 1 );
				ParticleSplash.SpawnSplashForNormal( Hit(), Falling( 60 ), Spec(), Vector3.Zero, ref r );
			}, "zero ground normal" );
		} );
	}
}
