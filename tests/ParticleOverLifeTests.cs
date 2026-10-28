using System;
using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleOverLifeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float actual, float expected, float tol, string what )
		{
			if ( MathF.Abs( actual - expected ) > tol ) throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		void Near4( Vector4 actual, Vector4 expected, float tol, string what )
		{
			Near( actual.X, expected.X, tol, what + ".R" );
			Near( actual.Y, expected.Y, tol, what + ".G" );
			Near( actual.Z, expected.Z, tol, what + ".B" );
			Near( actual.W, expected.W, tol, what + ".A" );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: threw {ex.GetType().Name}, expected {typeof( T ).Name}" ); }
			throw new Exception( $"{what}: did not throw" );
		}

		ParticleOverLifeCurves Curves(
			List<OverLifeFloatKey>? size = null, List<OverLifeColourKey>? colour = null,
			List<OverLifeFloatKey>? rotSpeed = null, List<OverLifeFloatKey>? rotOffset = null,
			List<OverLifeFloatKey>? speed = null )
			=> new() { SizeKeys = size, ColourKeys = colour, RotationSpeedKeys = rotSpeed, RotationOffsetKeys = rotOffset, SpeedKeys = speed };

		registry.Add( "particle-over-life", "size linear midpoint and endpoints", () =>
		{
			var c = Curves( size: new() { new( 0f, 1f ), new( 1f, 0.5f ) } );
			Near( ParticleOverLife.SizeAt( c, 0f ), 1f, 1e-6f, "size t=0" );
			Near( ParticleOverLife.SizeAt( c, 0.5f ), 0.75f, 1e-6f, "size t=0.5" );
			Near( ParticleOverLife.SizeAt( c, 1f ), 0.5f, 1e-6f, "size t=1" );
		} );

		registry.Add( "particle-over-life", "size multi-key midpoints and exact key", () =>
		{
			var c = Curves( size: new() { new( 0f, 0f ), new( 0.5f, 2f ), new( 1f, 0f ) } );
			Near( ParticleOverLife.SizeAt( c, 0.25f ), 1f, 1e-6f, "size t=0.25" );
			Near( ParticleOverLife.SizeAt( c, 0.5f ), 2f, 1e-6f, "size t=0.5 key" );
			Near( ParticleOverLife.SizeAt( c, 0.75f ), 1f, 1e-6f, "size t=0.75" );
		} );

		registry.Add( "particle-over-life", "size clamps past ends and single key is constant", () =>
		{
			var c = Curves( size: new() { new( 0f, 2f ), new( 1f, 4f ) } );
			var s = ParticleOverLife.Evaluate( -1f, 2f, c );
			Near( s.SizeMultiplier, 2f, 1e-6f, "negative age holds first key" );
			Near( s.NormalizedAge, 0f, 1e-6f, "negative age normalizes to 0" );
			s = ParticleOverLife.Evaluate( 99f, 2f, c );
			Near( s.SizeMultiplier, 4f, 1e-6f, "age past lifetime holds last key, never extrapolates" );
			Near( s.NormalizedAge, 1f, 1e-6f, "age past lifetime normalizes to 1" );
			var single = Curves( size: new() { new( 0.3f, 7f ) } );
			Near( ParticleOverLife.SizeAt( single, 0f ), 7f, 1e-6f, "single key t=0" );
			Near( ParticleOverLife.SizeAt( single, 1f ), 7f, 1e-6f, "single key t=1" );
		} );

		registry.Add( "particle-over-life", "colour midpoint and endpoints per channel", () =>
		{
			var c = Curves( colour: new() { new( 0f, new Vector4( 1, 0, 0, 1 ) ), new( 1f, new Vector4( 0, 0, 1, 0 ) ) } );
			Near4( ParticleOverLife.ColourAt( c, 0f ), new Vector4( 1, 0, 0, 1 ), 1e-6f, "colour t=0" );
			Near4( ParticleOverLife.ColourAt( c, 0.5f ), new Vector4( 0.5f, 0, 0.5f, 0.5f ), 1e-6f, "colour t=0.5" );
			Near4( ParticleOverLife.ColourAt( c, 1f ), new Vector4( 0, 0, 1, 0 ), 1e-6f, "colour t=1" );
		} );

		registry.Add( "particle-over-life", "colour three-stop midpoint", () =>
		{
			var c = Curves( colour: new()
			{
				new( 0f, new Vector4( 1, 1, 0, 1 ) ),
				new( 0.5f, new Vector4( 1, 0.5f, 0, 0.8f ) ),
				new( 1f, new Vector4( 0.2f, 0.2f, 0.2f, 0 ) ),
			} );
			Near4( ParticleOverLife.ColourAt( c, 0.75f ), new Vector4( 0.6f, 0.35f, 0.1f, 0.4f ), 1e-6f, "colour t=0.75" );
		} );

		registry.Add( "particle-over-life", "rotation speed multiplier midpoint and endpoints", () =>
		{
			var c = Curves( rotSpeed: new() { new( 0f, 1f ), new( 1f, 0f ) } );
			Near( ParticleOverLife.RotationSpeedAt( c, 0f ), 1f, 1e-6f, "rot speed t=0" );
			Near( ParticleOverLife.RotationSpeedAt( c, 0.5f ), 0.5f, 1e-6f, "rot speed t=0.5" );
			Near( ParticleOverLife.RotationSpeedAt( c, 1f ), 0f, 1e-6f, "rot speed t=1" );
		} );

		registry.Add( "particle-over-life", "rotation offset degrees midpoint and endpoints", () =>
		{
			var c = Curves( rotOffset: new() { new( 0f, 0f ), new( 1f, 360f ) } );
			Near( ParticleOverLife.RotationOffsetAt( c, 0f ), 0f, 1e-6f, "rot offset t=0" );
			Near( ParticleOverLife.RotationOffsetAt( c, 0.25f ), 90f, 1e-6f, "rot offset t=0.25" );
			Near( ParticleOverLife.RotationOffsetAt( c, 1f ), 360f, 1e-6f, "rot offset t=1" );
		} );

		registry.Add( "particle-over-life", "speed multiplier eases in and out without sign change", () =>
		{
			var c = Curves( speed: new() { new( 0f, 0.2f ), new( 0.5f, 1f ), new( 1f, 0.2f ) } );
			Near( ParticleOverLife.SpeedAt( c, 0f ), 0.2f, 1e-6f, "speed t=0" );
			Near( ParticleOverLife.SpeedAt( c, 0.25f ), 0.6f, 1e-6f, "speed t=0.25" );
			Near( ParticleOverLife.SpeedAt( c, 0.5f ), 1f, 1e-6f, "speed t=0.5" );
			Near( ParticleOverLife.SpeedAt( c, 1f ), 0.2f, 1e-6f, "speed t=1" );
		} );

		registry.Add( "particle-over-life", "Evaluate combined sample at t=0.25", () =>
		{
			var c = Curves(
				size: new() { new( 0f, 1f ), new( 1f, 3f ) },
				colour: new() { new( 0f, new Vector4( 1, 0, 0, 1 ) ), new( 1f, new Vector4( 0, 0, 1, 0 ) ) },
				rotSpeed: new() { new( 0f, 1f ), new( 1f, 0f ) },
				rotOffset: new() { new( 0f, 0f ), new( 1f, 360f ) },
				speed: new() { new( 0f, 0.2f ), new( 0.5f, 1f ), new( 1f, 0.2f ) } );
			var s = ParticleOverLife.Evaluate( 0.5f, 2f, c );
			Near( s.NormalizedAge, 0.25f, 1e-6f, "sample t" );
			Near( s.SizeMultiplier, 1.5f, 1e-6f, "sample size" );
			Near4( s.Colour, new Vector4( 0.75f, 0, 0.25f, 0.75f ), 1e-6f, "sample colour" );
			Near( s.RotationOffsetDegrees, 90f, 1e-6f, "sample rot offset" );
			Near( s.RotationSpeedMultiplier, 0.75f, 1e-6f, "sample rot speed" );
			Near( s.SpeedMultiplier, 0.6f, 1e-6f, "sample speed" );
		} );

		registry.Add( "particle-over-life", "Evaluate from ParticleState age", () =>
		{
			var c = Curves( size: new() { new( 0f, 0f ), new( 1f, 8f ) } );
			var state = new ParticleState { Age = 1.5f, Size = 2f };
			var s = ParticleOverLife.Evaluate( state, 2f, c );
			Near( s.NormalizedAge, 0.75f, 1e-6f, "state sample t" );
			Near( s.SizeMultiplier, 6f, 1e-6f, "state sample size" );
		} );

		registry.Add( "particle-over-life", "default curves are identity", () =>
		{
			var s = ParticleOverLife.Evaluate( 0.3f, 1f, ParticleOverLifeCurves.Default );
			Near( s.SizeMultiplier, 1f, 1e-6f, "default size" );
			Near4( s.Colour, Vector4.One, 1e-6f, "default colour" );
			Near( s.RotationOffsetDegrees, 0f, 1e-6f, "default rot offset" );
			Near( s.RotationSpeedMultiplier, 1f, 1e-6f, "default rot speed" );
			Near( s.SpeedMultiplier, 1f, 1e-6f, "default speed" );
			ParticleOverLifeCurves.Default.Validate();
		} );

		registry.Add( "particle-over-life", "Validate rejects unsorted keys", () =>
		{
			var c = Curves( size: new() { new( 0.5f, 1f ), new( 0.25f, 2f ) } );
			Throws<ArgumentException>( () => c.Validate(), "unsorted size keys" );
		} );

		registry.Add( "particle-over-life", "Validate rejects duplicate times", () =>
		{
			var c = Curves( colour: new() { new( 0.5f, Vector4.One ), new( 0.5f, Vector4.Zero ) } );
			Throws<ArgumentException>( () => c.Validate(), "duplicate colour times" );
		} );

		registry.Add( "particle-over-life", "Validate rejects out-of-range times", () =>
		{
			Throws<ArgumentException>( () => Curves( size: new() { new( -0.1f, 1f ) } ).Validate(), "time below 0" );
			Throws<ArgumentException>( () => Curves( speed: new() { new( 1.1f, 1f ) } ).Validate(), "time above 1" );
		} );

		registry.Add( "particle-over-life", "Validate rejects empty key lists", () =>
		{
			Throws<ArgumentException>( () => Curves( size: new() ).Validate(), "empty size list" );
			Throws<ArgumentException>( () => Curves( colour: new() ).Validate(), "empty colour list" );
			Throws<ArgumentException>( () => ParticleOverLife.EvalFloat( new List<OverLifeFloatKey>(), 0.5f, 1f ), "empty list at eval" );
		} );

		registry.Add( "particle-over-life", "Validate rejects bad values", () =>
		{
			Throws<ArgumentException>( () => Curves( size: new() { new( 0f, -1f ) } ).Validate(), "negative size multiplier" );
			Throws<ArgumentException>( () => Curves( speed: new() { new( 0f, -0.5f ) } ).Validate(), "negative speed multiplier" );
			Throws<ArgumentException>( () => Curves( colour: new() { new( 0f, new Vector4( 1.2f, 0, 0, 1 ) ) } ).Validate(), "colour channel above 1" );
			Throws<ArgumentException>( () => Curves( colour: new() { new( 0f, new Vector4( 0, -0.1f, 0, 1 ) ) } ).Validate(), "colour channel below 0" );
			Throws<ArgumentException>( () => Curves( size: new() { new( 0f, float.NaN ) } ).Validate(), "NaN value" );
			Throws<ArgumentException>( () => Curves( size: new() { new( float.NaN, 1f ) } ).Validate(), "NaN time" );
			// Negative rotation offset is legal: spinning the other way is a real effect.
			Curves( rotOffset: new() { new( 0f, 0f ), new( 1f, -180f ) } ).Validate();
			Near( ParticleOverLife.RotationOffsetAt( Curves( rotOffset: new() { new( 0f, 0f ), new( 1f, -180f ) } ), 0.5f ), -90f, 1e-6f, "negative rot offset allowed" );
		} );

		registry.Add( "particle-over-life", "Evaluate rejects bad lifetime and age", () =>
		{
			var c = ParticleOverLifeCurves.Default;
			Throws<ArgumentException>( () => ParticleOverLife.Evaluate( 0f, 0f, c ), "zero lifetime" );
			Throws<ArgumentException>( () => ParticleOverLife.Evaluate( 0f, -2f, c ), "negative lifetime" );
			Throws<ArgumentException>( () => ParticleOverLife.Evaluate( float.NaN, 1f, c ), "NaN age" );
		} );

		registry.Add( "particle-over-life", "evaluation is deterministic", () =>
		{
			var c = Curves(
				size: new() { new( 0f, 1f ), new( 0.33f, 2.7f ), new( 1f, 0.4f ) },
				colour: new() { new( 0f, new Vector4( 0.1f, 0.2f, 0.3f, 1 ) ), new( 1f, new Vector4( 0.9f, 0.8f, 0.7f, 0.2f ) ) } );
			var a = ParticleOverLife.Evaluate( 0.77f, 2.3f, c );
			var b = ParticleOverLife.Evaluate( 0.77f, 2.3f, c );
			Check( a.Equals( b ), "same inputs must give bit-identical samples" );
			Check( a.SizeMultiplier == b.SizeMultiplier && a.Colour == b.Colour, "sample fields identical" );
		} );

		registry.Add( "particle-over-life", "offset quaternion is 90 degrees about Z with zero-axis fallback", () =>
		{
			var q = ParticleOverLife.OffsetQuaternion( 90f, Vector3.UnitZ );
			float s = MathF.Sin( MathF.PI / 4 );
			Near( q.X, 0f, 1e-6f, "quat x" );
			Near( q.Y, 0f, 1e-6f, "quat y" );
			Near( q.Z, s, 1e-6f, "quat z" );
			Near( q.W, s, 1e-6f, "quat w" );
			var fallback = ParticleOverLife.OffsetQuaternion( 90f, Vector3.Zero );
			Near( fallback.Z, s, 1e-6f, "zero axis falls back to Z" );
			var identity = ParticleOverLife.OffsetQuaternion( 0f, Vector3.UnitX );
			Near( identity.W, 1f, 1e-6f, "zero degrees is identity" );
		} );

		registry.Add( "particle-over-life", "first key holds below its time, last key holds above", () =>
		{
			var c = Curves( size: new() { new( 0.25f, 2f ), new( 0.75f, 4f ) } );
			Near( ParticleOverLife.SizeAt( c, 0f ), 2f, 1e-6f, "below first key holds, never extrapolates" );
			Near( ParticleOverLife.SizeAt( c, 0.5f ), 3f, 1e-6f, "between keys interpolates" );
			Near( ParticleOverLife.SizeAt( c, 1f ), 4f, 1e-6f, "above last key holds" );
		} );
	}
}
