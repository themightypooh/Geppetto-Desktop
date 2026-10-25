using System.Collections.Generic;
using Marionette.Desktop;

internal static class Particle2DOverLifeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearColor( Particle2DColor got, Particle2DColor want, float tol, string what )
		{
			if ( MathF.Abs( got.R - want.R ) > tol || MathF.Abs( got.G - want.G ) > tol || MathF.Abs( got.B - want.B ) > tol || MathF.Abs( got.A - want.A ) > tol )
				throw new Exception( $"{what}: got {got}, want {want} ± {tol}" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			throw new Exception( $"{what}: expected an ArgumentException, none thrown" );
		}
		Particle2DOverLifeCurves Curves(
			List<OverLifeKey>? size = null, List<OverLifeKey>? alpha = null,
			List<OverLifeKey>? rot = null, List<OverLifeColorKey>? color = null )
			=> new() { SizeKeys = size, AlphaKeys = alpha, RotationKeys = rot, ColorKeys = color };

		registry.Add( "particle-2d-over-life", "size interpolates linearly, midpoint exact", () =>
		{
			var c = Curves( size: new List<OverLifeKey> { new( 0, 0.5f ), new( 1, 2 ) } );
			Near( c.EvaluateSize( 0.5f ), 1.25f, 1e-6f, "size at t=0.5 from 0.5->2" );
		} );

		registry.Add( "particle-2d-over-life", "size endpoints are exact key values", () =>
		{
			var c = Curves( size: new List<OverLifeKey> { new( 0, 0.5f ), new( 1, 2 ) } );
			Near( c.EvaluateSize( 0 ), 0.5f, 1e-6f, "size at t=0" );
			Near( c.EvaluateSize( 1 ), 2, 1e-6f, "size at t=1" );
		} );

		registry.Add( "particle-2d-over-life", "size multi-key segments interpolate per segment", () =>
		{
			var c = Curves( size: new List<OverLifeKey> { new( 0, 1 ), new( 0.5f, 2 ), new( 1, 0 ) } );
			Near( c.EvaluateSize( 0.25f ), 1.5f, 1e-6f, "size at t=0.25 (1->2 segment)" );
			Near( c.EvaluateSize( 0.75f ), 1.0f, 1e-6f, "size at t=0.75 (2->0 segment)" );
			Near( c.EvaluateSize( 0.5f ), 2, 1e-6f, "size exactly on middle key" );
		} );

		registry.Add( "particle-2d-over-life", "size clamps to end keys outside the key range", () =>
		{
			var c = Curves( size: new List<OverLifeKey> { new( 0.25f, 2 ), new( 0.75f, 4 ) } );
			Near( c.EvaluateSize( 0 ), 2, 1e-6f, "before first key" );
			Near( c.EvaluateSize( -0.5f ), 2, 1e-6f, "negative normalized age" );
			Near( c.EvaluateSize( 1 ), 4, 1e-6f, "after last key" );
			Near( c.EvaluateSize( 1.5f ), 4, 1e-6f, "normalized age past 1" );
			Near( c.Evaluate( 3, 2 ).SizeMultiplier, 4, 1e-6f, "age past lifetime clamps to last key" );
			Near( c.Evaluate( -1, 2 ).SizeMultiplier, 2, 1e-6f, "negative age clamps to first key" );
		} );

		registry.Add( "particle-2d-over-life", "alpha fade-in and fade-out segments", () =>
		{
			var c = Curves( alpha: new List<OverLifeKey> { new( 0, 0 ), new( 0.25f, 1 ), new( 0.75f, 1 ), new( 1, 0 ) } );
			Near( c.EvaluateAlpha( 0.125f ), 0.5f, 1e-6f, "alpha mid fade-in" );
			Near( c.EvaluateAlpha( 0.5f ), 1, 1e-6f, "alpha full plateau" );
			Near( c.EvaluateAlpha( 0.875f ), 0.5f, 1e-6f, "alpha mid fade-out" );
			Near( c.EvaluateAlpha( 0 ), 0, 1e-6f, "alpha at birth" );
			Near( c.EvaluateAlpha( 1 ), 0, 1e-6f, "alpha at death" );
		} );

		registry.Add( "particle-2d-over-life", "rotation interpolates in degrees", () =>
		{
			var c = Curves( rot: new List<OverLifeKey> { new( 0, 0 ), new( 1, 90 ) } );
			Near( c.EvaluateRotationDegrees( 0.5f ), 45, 1e-6f, "rotation at t=0.5" );
			Near( c.Evaluate( 0.5f, 1 ).RotationRadians, MathF.PI / 4, 1e-6f, "45 degrees converts to PI/4 radians for the draw record" );
		} );

		registry.Add( "particle-2d-over-life", "rotation sign: negative is counter-clockwise, zero crossing exact", () =>
		{
			var c = Curves( rot: new List<OverLifeKey> { new( 0, -30 ), new( 1, 30 ) } );
			Near( c.EvaluateRotationDegrees( 0 ), -30, 1e-6f, "rotation at birth" );
			Near( c.EvaluateRotationDegrees( 0.5f ), 0, 1e-6f, "rotation at midpoint crossing" );
			Near( c.EvaluateRotationDegrees( 1 ), 30, 1e-6f, "rotation at death" );
		} );

		registry.Add( "particle-2d-over-life", "rotation clamps past the last key", () =>
		{
			var c = Curves( rot: new List<OverLifeKey> { new( 0, 0 ), new( 0.5f, 180 ) } );
			Near( c.EvaluateRotationDegrees( 1 ), 180, 1e-6f, "rotation past last key stays at 180, never extrapolates to 360" );
		} );

		registry.Add( "particle-2d-over-life", "colour interpolates per channel, midpoint exact", () =>
		{
			var c = Curves( color: new List<OverLifeColorKey> { new( 0, new Particle2DColor( 1, 0, 0, 1 ) ), new( 1, new Particle2DColor( 0, 0, 1, 1 ) ) } );
			NearColor( c.EvaluateColor( 0.5f ), new Particle2DColor( 0.5f, 0, 0.5f, 1 ), 1e-6f, "red->blue at t=0.5" );
			NearColor( c.EvaluateColor( 0 ), new Particle2DColor( 1, 0, 0, 1 ), 1e-6f, "colour at birth" );
			NearColor( c.EvaluateColor( 1 ), new Particle2DColor( 0, 0, 1, 1 ), 1e-6f, "colour at death" );
		} );

		registry.Add( "particle-2d-over-life", "colour multi-stop interpolates per segment including alpha channel", () =>
		{
			var c = Curves( color: new List<OverLifeColorKey>
			{
				new( 0, new Particle2DColor( 1, 0, 0, 0.2f ) ),
				new( 0.5f, new Particle2DColor( 0, 1, 0, 0.5f ) ),
				new( 1, new Particle2DColor( 0, 0, 1, 0.8f ) ),
			} );
			NearColor( c.EvaluateColor( 0.25f ), new Particle2DColor( 0.5f, 0.5f, 0, 0.35f ), 1e-6f, "colour at t=0.25" );
			NearColor( c.EvaluateColor( 0.75f ), new Particle2DColor( 0, 0.5f, 0.5f, 0.65f ), 1e-6f, "colour at t=0.75" );
		} );

		registry.Add( "particle-2d-over-life", "Evaluate combines size, alpha, rotation and colour at one age", () =>
		{
			var c = Curves(
				size: new List<OverLifeKey> { new( 0, 0.5f ), new( 1, 1.5f ) },
				alpha: new List<OverLifeKey> { new( 0, 0 ), new( 0.5f, 1 ), new( 1, 0 ) },
				rot: new List<OverLifeKey> { new( 0, 0 ), new( 1, 90 ) },
				color: new List<OverLifeColorKey> { new( 0, new Particle2DColor( 1, 0, 0, 1 ) ), new( 1, new Particle2DColor( 0, 0, 1, 1 ) ) } );
			var r = c.Evaluate( 1, 2 );
			Near( r.SizeMultiplier, 1.0f, 1e-6f, "combined size at age 1 of lifetime 2" );
			Near( r.Alpha, 1, 1e-6f, "combined alpha at t=0.5" );
			Near( r.RotationDegrees, 45, 1e-6f, "combined rotation at t=0.5" );
			NearColor( r.Color, new Particle2DColor( 0.5f, 0, 0.5f, 1 ), 1e-6f, "combined colour at t=0.5" );
			NearColor( r.FinalColor, new Particle2DColor( 0.5f, 0, 0.5f, 1 ), 1e-6f, "final colour at full alpha" );
		} );

		registry.Add( "particle-2d-over-life", "Evaluate final colour multiplies colour alpha by alpha curve", () =>
		{
			var c = Curves(
				alpha: new List<OverLifeKey> { new( 0, 0 ), new( 1, 1 ) },
				color: new List<OverLifeColorKey> { new( 0, new Particle2DColor( 1, 0, 0, 0.5f ) ), new( 1, new Particle2DColor( 1, 0, 0, 0.5f ) ) } );
			var r = c.Evaluate( 0.5f, 1 );
			Near( r.Alpha, 0.5f, 1e-6f, "alpha curve at t=0.5" );
			Near( r.FinalColor.A, 0.25f, 1e-6f, "final alpha = colour alpha 0.5 x curve alpha 0.5" );
		} );

		registry.Add( "particle-2d-over-life", "Evaluate endpoints and lifetime rules", () =>
		{
			var c = Curves( size: new List<OverLifeKey> { new( 0, 1 ), new( 1, 3 ) } );
			Near( c.Evaluate( 0, 2 ).SizeMultiplier, 1, 1e-6f, "age 0 is birth key" );
			Near( c.Evaluate( 2, 2 ).SizeMultiplier, 3, 1e-6f, "age == lifetime is death key" );
			Throws( () => c.Evaluate( 1, 0 ), "lifetime 0 must fail loudly" );
			Throws( () => c.Evaluate( 1, -2 ), "negative lifetime must fail loudly" );
			Throws( () => c.Evaluate( float.NaN, 2 ), "NaN age must fail loudly" );
			Throws( () => c.EvaluateNormalized( float.NaN ), "NaN normalized age must fail loudly" );
		} );

		registry.Add( "particle-2d-over-life", "Validate rejects unsorted keys", () =>
		{
			Throws( () => Curves( size: new List<OverLifeKey> { new( 0.5f, 1 ), new( 0.25f, 2 ) } ).Validate(), "unsorted size keys" );
		} );

		registry.Add( "particle-2d-over-life", "Validate rejects duplicate times", () =>
		{
			Throws( () => Curves( alpha: new List<OverLifeKey> { new( 0, 0 ), new( 0.5f, 1 ), new( 0.5f, 0 ) } ).Validate(), "duplicate alpha times" );
			Throws( () => Curves( color: new List<OverLifeColorKey> { new( 0, new Particle2DColor( 1, 0, 0, 1 ) ), new( 0, new Particle2DColor( 0, 0, 1, 1 ) ) } ).Validate(), "duplicate colour times" );
		} );

		registry.Add( "particle-2d-over-life", "Validate rejects empty key lists", () =>
		{
			Throws( () => Curves( size: new List<OverLifeKey>() ).Validate(), "empty size list" );
			Throws( () => Curves( color: new List<OverLifeColorKey>() ).Validate(), "empty colour list" );
		} );

		registry.Add( "particle-2d-over-life", "Validate rejects out-of-range times and values", () =>
		{
			Throws( () => Curves( size: new List<OverLifeKey> { new( -0.1f, 1 ) } ).Validate(), "time below 0" );
			Throws( () => Curves( size: new List<OverLifeKey> { new( 1.1f, 1 ) } ).Validate(), "time above 1" );
			Throws( () => Curves( size: new List<OverLifeKey> { new( 0, -1 ) } ).Validate(), "negative size multiplier" );
			Throws( () => Curves( alpha: new List<OverLifeKey> { new( 0, 1.5f ) } ).Validate(), "alpha above 1" );
			Throws( () => Curves( alpha: new List<OverLifeKey> { new( 0, -0.5f ) } ).Validate(), "alpha below 0" );
			Throws( () => Curves( color: new List<OverLifeColorKey> { new( 0, new Particle2DColor( 1.5f, 0, 0, 1 ) ) } ).Validate(), "colour channel above 1" );
			Throws( () => Curves( size: new List<OverLifeKey> { new( float.NaN, 1 ) } ).Validate(), "NaN time" );
			Throws( () => Curves( size: new List<OverLifeKey> { new( 0, float.PositiveInfinity ) } ).Validate(), "infinite size value" );
		} );

		registry.Add( "particle-2d-over-life", "single-key curves are constant, absent curves are defaults", () =>
		{
			var onlySize = Curves( size: new List<OverLifeKey> { new( 0.3f, 2.5f ) } );
			Near( onlySize.EvaluateSize( 0 ), 2.5f, 1e-6f, "single size key before its time" );
			Near( onlySize.EvaluateSize( 1 ), 2.5f, 1e-6f, "single size key after its time" );
			var r = onlySize.Evaluate( 0.5f, 1 );
			Near( r.Alpha, 1, 1e-6f, "absent alpha defaults to 1" );
			Near( r.RotationDegrees, 0, 1e-6f, "absent rotation defaults to 0" );
			NearColor( r.Color, new Particle2DColor( 1, 1, 1, 1 ), 1e-6f, "absent colour defaults to opaque white" );
			var none = new Particle2DOverLifeCurves();
			none.Validate();
			var d = none.Evaluate( 0.25f, 1 );
			Near( d.SizeMultiplier, 1, 1e-6f, "all-absent size defaults to 1" );
			Near( d.Alpha, 1, 1e-6f, "all-absent alpha defaults to 1" );
		} );

		registry.Add( "particle-2d-over-life", "evaluation is deterministic across repeated calls", () =>
		{
			var c = Curves(
				size: new List<OverLifeKey> { new( 0, 1 ), new( 0.5f, 2 ), new( 1, 0 ) },
				color: new List<OverLifeColorKey> { new( 0, new Particle2DColor( 1, 0, 0, 1 ) ), new( 1, new Particle2DColor( 0, 0, 1, 0 ) ) } );
			var first = c.Evaluate( 0.37f, 1 );
			for ( int i = 0; i < 1000; i++ )
			{
				var again = c.Evaluate( 0.37f, 1 );
				Check( again.SizeMultiplier == first.SizeMultiplier && again.Alpha == first.Alpha && again.RotationDegrees == first.RotationDegrees
					&& again.Color.R == first.Color.R && again.Color.G == first.Color.G && again.Color.B == first.Color.B && again.Color.A == first.Color.A,
					$"repeat {i} differed from first evaluation" );
			}
			Near( first.SizeMultiplier, 1.74f, 1e-6f, "determinism check value at t=0.37 (1->2 segment)" );
		} );

		registry.Add( "particle-2d-over-life", "Constant factory is identity at every age", () =>
		{
			var c = Particle2DOverLifeCurves.Constant();
			c.Validate();
			var r = c.Evaluate( 0.7f, 1 );
			Near( r.SizeMultiplier, 1, 1e-6f, "constant size" );
			Near( r.Alpha, 1, 1e-6f, "constant alpha" );
			Near( r.RotationDegrees, 0, 1e-6f, "constant rotation" );
			NearColor( r.Color, new Particle2DColor( 1, 1, 1, 1 ), 1e-6f, "constant colour" );
		} );

		registry.Add( "particle-2d-over-life", "FadeInOut factory fades in, holds, fades out", () =>
		{
			var c = Particle2DOverLifeCurves.FadeInOut();
			c.Validate();
			Near( c.EvaluateAlpha( 0 ), 0, 1e-6f, "fade-in start" );
			Near( c.EvaluateAlpha( 0.125f ), 0.5f, 1e-6f, "fade-in midpoint" );
			Near( c.EvaluateAlpha( 0.5f ), 1, 1e-6f, "fade plateau" );
			Near( c.EvaluateAlpha( 0.875f ), 0.5f, 1e-6f, "fade-out midpoint" );
			Near( c.EvaluateAlpha( 1 ), 0, 1e-6f, "fade-out end" );
			Throws( () => Particle2DOverLifeCurves.FadeInOut( 0.8f, 0.2f ), "fade-out before fade-in must fail loudly" );
		} );

		registry.Add( "particle-2d-over-life", "rotation keys may exceed 360 degrees, size keys may grow past 1", () =>
		{
			var c = Curves(
				rot: new List<OverLifeKey> { new( 0, 0 ), new( 1, 720 ) },
				size: new List<OverLifeKey> { new( 0, 1 ), new( 1, 3 ) } );
			c.Validate();
			Near( c.EvaluateRotationDegrees( 0.5f ), 360, 1e-6f, "720-degree spin at midpoint" );
			Near( c.EvaluateSize( 1 ), 3, 1e-6f, "growth past 1 allowed" );
		} );
	}
}
