using System;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleSizeModesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( Vector3 actual, Vector3 expected, float tol, string what )
		{
			if ( MathF.Abs( actual.X - expected.X ) > tol || MathF.Abs( actual.Y - expected.Y ) > tol || MathF.Abs( actual.Z - expected.Z ) > tol )
				throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		void Near1( float actual, float expected, float tol, string what )
		{
			if ( MathF.Abs( actual - expected ) > tol ) throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: threw {ex.GetType().Name}, expected {typeof( T ).Name}" ); }
			throw new Exception( $"{what}: did not throw" );
		}

		// --- Uniform: reproduces the existing scalar-size behaviour exactly ---

		registry.Add( "particle-size-modes", "uniform equals base on all axes", () =>
		{
			Near( ParticleSizeModes.Uniform( 2f ), new Vector3( 2, 2, 2 ), 1e-6f, "uniform(2)" );
			Near( ParticleSizeModes.Uniform( 0.5f ), new Vector3( 0.5f, 0.5f, 0.5f ), 1e-6f, "uniform(0.5)" );
		} );

		registry.Add( "particle-size-modes", "uniform matches old scalar scale (CreateScale equivalence)", () =>
		{
			// The old paths scale uniformly by ParticleState.Size; a uniform
			// Vector3 must carry that same scalar on every axis, nothing else.
			var v = ParticleSizeModes.Uniform( 3f );
			Check( v.X == 3f && v.Y == 3f && v.Z == 3f, $"uniform(3) was {v}, not (3,3,3)" );
			var m = Matrix4x4.CreateScale( v );
			Near1( m.M11, 3f, 1e-6f, "scale matrix x" ); Near1( m.M22, 3f, 1e-6f, "scale matrix y" ); Near1( m.M33, 3f, 1e-6f, "scale matrix z" );
		} );

		// --- Per-axis ---

		registry.Add( "particle-size-modes", "per-axis flat disc", () =>
		{
			Near( ParticleSizeModes.PerAxis( 2f, new Vector3( 1, 1, 0.25f ) ), new Vector3( 2, 2, 0.5f ), 1e-6f, "flat disc" );
		} );

		registry.Add( "particle-size-modes", "per-axis long streak", () =>
		{
			Near( ParticleSizeModes.PerAxis( 2f, new Vector3( 3, 0.5f, 0.5f ) ), new Vector3( 6, 1, 1 ), 1e-6f, "long streak" );
		} );

		registry.Add( "particle-size-modes", "per-axis tall column", () =>
		{
			Near( ParticleSizeModes.PerAxis( 2f, new Vector3( 0.5f, 0.5f, 3 ) ), new Vector3( 1, 1, 6 ), 1e-6f, "tall column" );
		} );

		registry.Add( "particle-size-modes", "per-axis zero component flattens that axis", () =>
		{
			Near( ParticleSizeModes.PerAxis( 2f, new Vector3( 1, 0, 1 ) ), new Vector3( 2, 0, 2 ), 1e-6f, "zero y" );
		} );

		// --- Stretch by speed ---

		registry.Add( "particle-size-modes", "stretch at zero speed equals uniform", () =>
		{
			Near( ParticleSizeModes.StretchBySpeed( 2f, Vector3.Zero, 0.1f ), new Vector3( 2, 2, 2 ), 1e-6f, "zero speed" );
			Near( ParticleSizeModes.StretchBySpeed( 2f, Vector3.Zero, 0.1f ), ParticleSizeModes.Uniform( 2f ), 0f, "zero speed vs uniform" );
		} );

		registry.Add( "particle-size-modes", "stretch at speed 10 with factor 0.1 doubles the velocity axis", () =>
		{
			// stretch = 1 + 0.1 * 10 = 2; velocity-frame X = 2*2 = 4, others 2.
			Near( ParticleSizeModes.StretchBySpeed( 2f, new Vector3( 10, 0, 0 ), 0.1f ), new Vector3( 4, 2, 2 ), 1e-6f, "speed 10" );
		} );

		registry.Add( "particle-size-modes", "stretch at speed 20 with factor 0.1 triples the velocity axis", () =>
		{
			Near( ParticleSizeModes.StretchBySpeed( 2f, new Vector3( 0, 20, 0 ), 0.1f ), new Vector3( 6, 2, 2 ), 1e-6f, "speed 20" );
		} );

		registry.Add( "particle-size-modes", "stretch at speed 5 with factor 0.2 doubles the velocity axis", () =>
		{
			Near( ParticleSizeModes.StretchBySpeed( 2f, new Vector3( 0, 0, 5 ), 0.2f ), new Vector3( 4, 2, 2 ), 1e-6f, "speed 5 factor 0.2" );
		} );

		registry.Add( "particle-size-modes", "stretch depends on speed magnitude not direction", () =>
		{
			var a = ParticleSizeModes.StretchBySpeed( 2f, new Vector3( 10, 0, 0 ), 0.1f );
			var b = ParticleSizeModes.StretchBySpeed( 2f, new Vector3( -10, 0, 0 ), 0.1f );
			var c = ParticleSizeModes.StretchBySpeed( 2f, new Vector3( 6, 8, 0 ), 0.1f ); // |(6,8,0)| = 10
			Near( a, b, 0f, "negative velocity" );
			Near( a, c, 1e-6f, "diagonal velocity of speed 10" );
		} );

		registry.Add( "particle-size-modes", "stretch length helper matches the stretched axis", () =>
		{
			Near1( ParticleSizeModes.StretchLength( 2f, new Vector3( 10, 0, 0 ), 0.1f ), 4f, 1e-6f, "stretch length" );
			Near1( ParticleSizeModes.StretchLength( 2f, Vector3.Zero, 0.1f ), 2f, 1e-6f, "stretch length at rest" );
		} );

		registry.Add( "particle-size-modes", "stretch ignores per-axis multipliers (documented)", () =>
		{
			var settings = new ParticleSizeModeSettings { Mode = ParticleSizeMode.StretchBySpeed, StretchFactor = 0.1f, AxisMultipliers = new Vector3( 9, 9, 9 ) };
			Near( ParticleSizeModes.Evaluate( settings, 2f, new Vector3( 10, 0, 0 ) ), new Vector3( 4, 2, 2 ), 1e-6f, "stretch with multipliers set" );
		} );

		// --- Over-life multiplier: multiplicative in every mode, curve not owned ---

		registry.Add( "particle-size-modes", "over-life multiplier scales uniform", () =>
		{
			Near( ParticleSizeModes.Uniform( 2f, 0.5f ), new Vector3( 1, 1, 1 ), 1e-6f, "uniform x 0.5" );
			Near( ParticleSizeModes.Uniform( 2f, 0f ), Vector3.Zero, 0f, "uniform x 0" );
			Near( ParticleSizeModes.Uniform( 2f, 2f ), new Vector3( 4, 4, 4 ), 1e-6f, "uniform x 2" );
		} );

		registry.Add( "particle-size-modes", "over-life multiplier scales per-axis and stretch alike", () =>
		{
			Near( ParticleSizeModes.PerAxis( 2f, new Vector3( 1, 1, 0.25f ), 0.5f ), new Vector3( 1, 1, 0.25f ), 1e-6f, "per-axis x 0.5" );
			Near( ParticleSizeModes.StretchBySpeed( 2f, new Vector3( 10, 0, 0 ), 0.1f, 0.5f ), new Vector3( 2, 1, 1 ), 1e-6f, "stretch x 0.5" );
		} );

		registry.Add( "particle-size-modes", "evaluate dispatch matches direct methods in all modes", () =>
		{
			var u = new ParticleSizeModeSettings { Mode = ParticleSizeMode.Uniform };
			Near( ParticleSizeModes.Evaluate( u, 2f, new Vector3( 10, 0, 0 ), 0.5f ), new Vector3( 1, 1, 1 ), 1e-6f, "dispatch uniform" );
			var p = new ParticleSizeModeSettings { Mode = ParticleSizeMode.PerAxis, AxisMultipliers = new Vector3( 3, 0.5f, 0.5f ) };
			Near( ParticleSizeModes.Evaluate( p, 2f, Vector3.Zero, 1f ), new Vector3( 6, 1, 1 ), 1e-6f, "dispatch per-axis" );
			var s = new ParticleSizeModeSettings { Mode = ParticleSizeMode.StretchBySpeed, StretchFactor = 0.1f };
			Near( ParticleSizeModes.Evaluate( s, 2f, new Vector3( 10, 0, 0 ), 1f ), new Vector3( 4, 2, 2 ), 1e-6f, "dispatch stretch" );
		} );

		// --- Validate(): loud failures ---

		registry.Add( "particle-size-modes", "validate rejects negative base size", () =>
		{
			Throws<ArgumentException>( () => ParticleSizeModes.Uniform( -1f ), "uniform negative base" );
			Throws<ArgumentException>( () => ParticleSizeModes.PerAxis( -1f, Vector3.One ), "per-axis negative base" );
			Throws<ArgumentException>( () => ParticleSizeModes.StretchBySpeed( -1f, Vector3.Zero, 0.1f ), "stretch negative base" );
		} );

		registry.Add( "particle-size-modes", "validate rejects negative multipliers", () =>
		{
			Throws<ArgumentException>( () => ParticleSizeModes.PerAxis( 2f, new Vector3( -1, 1, 1 ) ), "negative x multiplier" );
			Throws<ArgumentException>( () => ParticleSizeModes.PerAxis( 2f, new Vector3( 1, 1, -0.5f ) ), "negative z multiplier" );
			Throws<ArgumentException>( () => ParticleSizeModes.Uniform( 2f, -0.5f ), "negative over-life multiplier" );
			Throws<ArgumentException>( () => ParticleSizeModes.StretchBySpeed( 2f, Vector3.Zero, -0.1f ), "negative stretch factor" );
		} );

		registry.Add( "particle-size-modes", "validate rejects NaN inputs", () =>
		{
			Throws<ArgumentException>( () => ParticleSizeModes.Uniform( float.NaN ), "NaN base" );
			Throws<ArgumentException>( () => ParticleSizeModes.PerAxis( 2f, new Vector3( float.NaN, 1, 1 ) ), "NaN multiplier" );
			Throws<ArgumentException>( () => ParticleSizeModes.StretchBySpeed( 2f, new Vector3( float.NaN, 0, 0 ), 0.1f ), "NaN velocity" );
			Throws<ArgumentException>( () => ParticleSizeModes.Uniform( 2f, float.NaN ), "NaN over-life multiplier" );
			Throws<ArgumentException>( () => ParticleSizeModes.StretchBySpeed( 2f, Vector3.Zero, float.NaN ), "NaN stretch factor" );
		} );

		registry.Add( "particle-size-modes", "validate rejects infinite inputs", () =>
		{
			Throws<ArgumentException>( () => ParticleSizeModes.Uniform( float.PositiveInfinity ), "infinite base" );
			Throws<ArgumentException>( () => ParticleSizeModes.PerAxis( 2f, new Vector3( 1, float.PositiveInfinity, 1 ) ), "infinite multiplier" );
		} );

		registry.Add( "particle-size-modes", "validate rejects unknown mode and bad settings", () =>
		{
			var bad = new ParticleSizeModeSettings { Mode = (ParticleSizeMode)99 };
			Throws<ArgumentException>( () => ParticleSizeModes.Evaluate( bad, 2f, Vector3.Zero ), "unknown mode" );
			Throws<ArgumentException>( () => new ParticleSizeModeSettings { AxisMultipliers = new Vector3( -1, 1, 1 ) }.Validate(), "negative settings multiplier" );
			Throws<ArgumentException>( () => new ParticleSizeModeSettings { StretchFactor = -1f }.Validate(), "negative settings stretch" );
			Throws<ArgumentException>( () => ParticleSizeModes.Evaluate( null!, 2f, Vector3.Zero ), "null settings" );
		} );

		registry.Add( "particle-size-modes", "validate allows zero base and zero multipliers (not errors)", () =>
		{
			Near( ParticleSizeModes.Uniform( 0f ), Vector3.Zero, 0f, "zero base" );
			Near( ParticleSizeModes.PerAxis( 0f, Vector3.One ), Vector3.Zero, 0f, "zero base per-axis" );
			new ParticleSizeModeSettings { Mode = ParticleSizeMode.PerAxis, AxisMultipliers = Vector3.Zero }.Validate();
			new ParticleSizeModeSettings().Validate();
		} );

		registry.Add( "particle-size-modes", "negative velocity components are legal (only finiteness checked)", () =>
		{
			// A velocity pointing -X is ordinary physics, not a validation error.
			Near( ParticleSizeModes.StretchBySpeed( 2f, new Vector3( -6, -8, 0 ), 0.1f ), new Vector3( 4, 2, 2 ), 1e-6f, "negative velocity" );
			var settings = new ParticleSizeModeSettings { Mode = ParticleSizeMode.StretchBySpeed, StretchFactor = 0.1f };
			Near( ParticleSizeModes.Evaluate( settings, 2f, new Vector3( -10, 0, 0 ) ), new Vector3( 4, 2, 2 ), 1e-6f, "dispatch negative velocity" );
		} );
	}
}
