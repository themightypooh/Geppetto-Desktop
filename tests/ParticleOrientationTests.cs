#nullable enable
using System;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleOrientationTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float actual, float expected, float tol, string what )
		{
			if ( MathF.Abs( actual - expected ) > tol ) throw new Exception( $"{what}: got {actual}, expected {expected} ± {tol}" );
		}
		void Vec( Vector3 actual, Vector3 expected, float tol, string what )
		{
			Near( actual.X, expected.X, tol, what + ".x" );
			Near( actual.Y, expected.Y, tol, what + ".y" );
			Near( actual.Z, expected.Z, tol, what + ".z" );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: threw {ex.GetType().Name}, expected {typeof( T ).Name}" ); }
			throw new Exception( $"{what}: did not throw" );
		}
		void CheckBasis( ParticleOrientationBasis b, string what )
		{
			Check( b.IsOrthonormal( 1e-4f ), $"{what}: basis not orthonormal (R={b.Right} U={b.Up} N={b.Normal})" );
			Vec( Vector3.Cross( b.Right, b.Up ), b.Normal, 1e-4f, $"{what}: Right x Up != Normal" );
		}

		// Canonical camera: eye at (0,-100,0) looking +Y, so the existing
		// ParticleLayer view basis is Right=X, Up=Z, normal=-Y.
		Vector3 Eye = new( 0, -100, 0 );
		Vector3 Forward = Vector3.UnitY;
		Vector3 Origin = Vector3.Zero;
		ParticleOrientationOptions Opt( ParticleOrientationMode mode, float stretch = 1f ) =>
			new() { Mode = mode, AxialAxis = Vector3.UnitZ, WorldUp = Vector3.UnitZ, StretchFactor = stretch, SpeedEpsilon = 1e-4f };

		registry.Add( "particle-orientation", "camera billboard matches the existing layer convention", () =>
		{
			var r = ParticleOrientation.Evaluate( Origin, Vector3.Zero, Eye, Forward, Opt( ParticleOrientationMode.CameraBillboard ) );
			// Look-at normal (0,-1,0); Right = Z x (0,-1,0) = X; Up = N x R = Z.
			Vec( r.Basis.Right, Vector3.UnitX, 1e-4f, "billboard right" );
			Vec( r.Basis.Up, Vector3.UnitZ, 1e-4f, "billboard up" );
			Vec( r.Basis.Normal, -Vector3.UnitY, 1e-4f, "billboard normal" );
			Check( !r.FellBackToBillboard, "billboard must not report a fallback" );
			Near( r.LengthScale, 1f, 1e-6f, "billboard length" );
			Near( r.WidthScale, 1f, 1e-6f, "billboard width" );
			CheckBasis( r.Basis, "billboard" );
		} );

		registry.Add( "particle-orientation", "camera billboard from a LiveView basis", () =>
		{
			// The exact View() the render-layer tests use: Eye (0,-6,0),
			// Right X, Up Z, Forward Y.
			var b = ParticleOrientation.CameraBillboardFromView( Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY );
			Vec( b.Right, Vector3.UnitX, 1e-4f, "view right" );
			Vec( b.Up, Vector3.UnitZ, 1e-4f, "view up" );
			Vec( b.Normal, -Vector3.UnitY, 1e-4f, "view normal" );
			CheckBasis( b, "view billboard" );
			Throws<ArgumentException>( () => ParticleOrientation.CameraBillboardFromView( Vector3.UnitX, Vector3.UnitX, Vector3.UnitY ), "non-perpendicular view Right/Up" );
			Throws<ArgumentException>( () => ParticleOrientation.CameraBillboardFromView( Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY ), "view normal inconsistent with -forward" );
		} );

		registry.Add( "particle-orientation", "velocity-aligned up axis follows velocity", () =>
		{
			var r = ParticleOrientation.Evaluate( Origin, new Vector3( 10, 0, 0 ), Eye, Forward, Opt( ParticleOrientationMode.VelocityAligned ) );
			Vec( r.Basis.Up, Vector3.UnitX, 1e-4f, "velocity up" );
			Vec( r.Basis.Right, Vector3.UnitY, 1e-4f, "velocity right (Z x X)" );
			Vec( r.Basis.Normal, -Vector3.UnitZ, 1e-4f, "velocity normal" );
			Check( !r.FellBackToBillboard, "moving particle must not fall back" );
			CheckBasis( r.Basis, "velocity-aligned" );
			// Diagonal velocity: up is exactly the normalized velocity.
			var d = ParticleOrientation.Evaluate( Origin, new Vector3( 0, 3, 4 ), Eye, Forward, Opt( ParticleOrientationMode.VelocityAligned ) );
			Vec( d.Basis.Up, new Vector3( 0, 0.6f, 0.8f ), 1e-4f, "diagonal velocity up" );
			CheckBasis( d.Basis, "diagonal velocity-aligned" );
		} );

		registry.Add( "particle-orientation", "velocity-aligned parallel to world up uses a fallback reference", () =>
		{
			var r = ParticleOrientation.Evaluate( Origin, new Vector3( 0, 0, 10 ), Eye, Forward, Opt( ParticleOrientationMode.VelocityAligned ) );
			Vec( r.Basis.Up, Vector3.UnitZ, 1e-4f, "vertical velocity up" );
			Vec( r.Basis.Right, -Vector3.UnitY, 1e-4f, "vertical velocity right (X x Z)" );
			Vec( r.Basis.Normal, -Vector3.UnitX, 1e-4f, "vertical velocity normal" );
			CheckBasis( r.Basis, "vertical velocity-aligned" );
		} );

		registry.Add( "particle-orientation", "velocity-aligned zero speed falls back to billboard, never NaN", () =>
		{
			var r = ParticleOrientation.Evaluate( Origin, Vector3.Zero, Eye, Forward, Opt( ParticleOrientationMode.VelocityAligned ) );
			Check( r.FellBackToBillboard, "zero speed must fall back" );
			Vec( r.Basis.Right, Vector3.UnitX, 1e-4f, "fallback right" );
			Vec( r.Basis.Up, Vector3.UnitZ, 1e-4f, "fallback up" );
			Vec( r.Basis.Normal, -Vector3.UnitY, 1e-4f, "fallback normal" );
			CheckBasis( r.Basis, "zero-speed fallback" );
			var tiny = ParticleOrientation.Evaluate( Origin, new Vector3( 1e-7f, 0, 0 ), Eye, Forward, Opt( ParticleOrientationMode.VelocityAligned ) );
			Check( tiny.FellBackToBillboard, "sub-epsilon speed must fall back" );
			CheckBasis( tiny.Basis, "tiny-speed fallback" );
		} );

		registry.Add( "particle-orientation", "axial billboard keeps the default Z axis upright", () =>
		{
			// Camera slightly above the particle plane so the billboard
			// would tilt, but the axial mode must keep Up == Z exactly.
			var eye = new Vector3( 0, -100, 10 );
			var r = ParticleOrientation.Evaluate( Origin, new Vector3( 5, 0, 0 ), eye, Forward, Opt( ParticleOrientationMode.AxialBillboard ) );
			Vec( r.Basis.Up, Vector3.UnitZ, 1e-4f, "axial up" );
			Vec( r.Basis.Right, Vector3.UnitX, 1e-4f, "axial right" );
			Vec( r.Basis.Normal, -Vector3.UnitY, 1e-4f, "axial normal" );
			Check( !r.FellBackToBillboard, "off-axis camera must not fall back" );
			CheckBasis( r.Basis, "axial Z" );
		} );

		registry.Add( "particle-orientation", "axial billboard with a custom X axis", () =>
		{
			var o = Opt( ParticleOrientationMode.AxialBillboard );
			o.AxialAxis = Vector3.UnitX;
			var r = ParticleOrientation.Evaluate( Origin, Vector3.Zero, Eye, Forward, o );
			Vec( r.Basis.Up, Vector3.UnitX, 1e-4f, "axial X up" );
			Vec( r.Basis.Right, -Vector3.UnitZ, 1e-4f, "axial X right (X x -Y)" );
			Vec( r.Basis.Normal, -Vector3.UnitY, 1e-4f, "axial X normal" );
			CheckBasis( r.Basis, "axial X" );
		} );

		registry.Add( "particle-orientation", "axial billboard camera above the axis never produces NaN", () =>
		{
			// Camera directly above: toEye is parallel to the Z axis, so the
			// width direction is undefined — deterministic fallback, not NaN.
			var r = ParticleOrientation.Evaluate( Origin, Vector3.Zero, new Vector3( 0, 0, 100 ), Vector3.UnitY, Opt( ParticleOrientationMode.AxialBillboard ) );
			Check( r.FellBackToBillboard, "camera down the axis must flag the width fallback" );
			Vec( r.Basis.Up, Vector3.UnitZ, 1e-4f, "above-axis up stays Z" );
			CheckBasis( r.Basis, "above-axis axial" );
		} );

		registry.Add( "particle-orientation", "stretched billboard stretches along projected velocity at known speeds", () =>
		{
			// Velocity (3,4,0): speed 5. Projected onto the billboard plane
			// (normal -Y) it is (3,0,0), so the length axis is +X.
			var r = ParticleOrientation.Evaluate( Origin, new Vector3( 3, 4, 0 ), Eye, Forward, Opt( ParticleOrientationMode.StretchedBillboard, stretch: 2f ) );
			Vec( r.Basis.Up, Vector3.UnitX, 1e-4f, "stretched length axis" );
			Vec( r.Basis.Right, -Vector3.UnitZ, 1e-4f, "stretched width axis" );
			Vec( r.Basis.Normal, -Vector3.UnitY, 1e-4f, "stretched normal" );
			Near( r.LengthScale, 11f, 1e-4f, "stretched length: 1 + 2*5" );
			Near( r.WidthScale, 1f, 1e-6f, "stretched width stays 1" );
			Check( !r.FellBackToBillboard, "stretched with a real projection must not fall back" );
			CheckBasis( r.Basis, "stretched" );
			// Quad corners: size 2 -> half-width 1, half-length 11.
			var (a, b, c, d) = r.QuadCorners( Origin, 2f );
			Vec( a, new Vector3( -11, 0, 1 ), 1e-4f, "stretched corner a (-r-u)" );
			Vec( b, new Vector3( -11, 0, -1 ), 1e-4f, "stretched corner b (+r-u)" );
			Vec( c, new Vector3( 11, 0, -1 ), 1e-4f, "stretched corner c (+r+u)" );
			Vec( d, new Vector3( 11, 0, 1 ), 1e-4f, "stretched corner d (-r+u)" );
		} );

		registry.Add( "particle-orientation", "stretch factor effect at known speeds", () =>
		{
			// Length = 1 + factor * speed, width always 1.
			var cases = new (float speed, float factor, float expected)[]
			{
				( 0f, 2f, 1f ), ( 2f, 0.5f, 2f ), ( 10f, 0.25f, 3.5f ), ( 5f, 0f, 1f ),
			};
			foreach ( var (speed, factor, expected) in cases )
			{
				var r = ParticleOrientation.Evaluate( Origin, new Vector3( speed, 0, 0 ), Eye, Forward, Opt( ParticleOrientationMode.StretchedBillboard, stretch: factor ) );
				Near( r.LengthScale, expected, 1e-4f, $"stretch speed {speed} factor {factor}" );
				Near( r.WidthScale, 1f, 1e-6f, $"stretch width speed {speed}" );
			}
		} );

		registry.Add( "particle-orientation", "stretched toward the camera falls back unstretched", () =>
		{
			// Velocity straight at the camera: projection onto the billboard
			// plane is zero, so there is no stretch direction.
			var r = ParticleOrientation.Evaluate( Origin, new Vector3( 0, -5, 0 ), Eye, Forward, Opt( ParticleOrientationMode.StretchedBillboard, stretch: 2f ) );
			Check( r.FellBackToBillboard, "toward-camera motion must fall back" );
			Near( r.LengthScale, 1f, 1e-6f, "fallback length unstretched" );
			Vec( r.Basis.Normal, -Vector3.UnitY, 1e-4f, "fallback normal" );
			CheckBasis( r.Basis, "toward-camera fallback" );
			var still = ParticleOrientation.Evaluate( Origin, Vector3.Zero, Eye, Forward, Opt( ParticleOrientationMode.StretchedBillboard, stretch: 2f ) );
			Check( still.FellBackToBillboard, "zero-speed stretch must fall back" );
			Near( still.LengthScale, 1f, 1e-6f, "zero-speed stretch length" );
		} );

		registry.Add( "particle-orientation", "validate rejects zero axes", () =>
		{
			var o = Opt( ParticleOrientationMode.AxialBillboard );
			o.AxialAxis = Vector3.Zero;
			Throws<ArgumentException>( () => ParticleOrientation.Evaluate( Origin, Vector3.Zero, Eye, Forward, o ), "zero axial axis" );
			var w = Opt( ParticleOrientationMode.CameraBillboard );
			w.WorldUp = Vector3.Zero;
			Throws<ArgumentException>( () => ParticleOrientation.Evaluate( Origin, Vector3.Zero, Eye, Forward, w ), "zero world up" );
			Throws<ArgumentException>( () => ParticleOrientation.CameraBillboardFromView( Vector3.Zero, Vector3.UnitZ, Vector3.UnitY ), "zero view right" );
		} );

		registry.Add( "particle-orientation", "validate rejects negative stretch and non-normalized camera directions", () =>
		{
			var o = Opt( ParticleOrientationMode.StretchedBillboard, stretch: -1f );
			Throws<ArgumentException>( () => ParticleOrientation.Evaluate( Origin, new Vector3( 1, 0, 0 ), Eye, Forward, o ), "negative stretch" );
			var ok = Opt( ParticleOrientationMode.CameraBillboard );
			Throws<ArgumentException>( () => ParticleOrientation.Evaluate( Origin, Vector3.Zero, Eye, new Vector3( 0, 2, 0 ), ok ), "camera forward length 2" );
			Throws<ArgumentException>( () => ParticleOrientation.Evaluate( Origin, Vector3.Zero, Eye, Vector3.Zero, ok ), "zero camera forward" );
			Throws<ArgumentException>( () => ParticleOrientation.Evaluate( Origin, Vector3.Zero, Eye, new Vector3( float.NaN, 1, 0 ), ok ), "NaN camera forward" );
		} );

		registry.Add( "particle-orientation", "validate rejects non-finite particle inputs instead of returning NaN", () =>
		{
			var ok = Opt( ParticleOrientationMode.CameraBillboard );
			var nan = new Vector3( float.NaN, 0, 0 );
			Throws<ArgumentException>( () => ParticleOrientation.Evaluate( nan, Vector3.Zero, Eye, Forward, ok ), "NaN position" );
			Throws<ArgumentException>( () => ParticleOrientation.Evaluate( Origin, nan, Eye, Forward, ok ), "NaN velocity" );
			Throws<ArgumentException>( () => ParticleOrientation.Evaluate( Origin, Vector3.Zero, nan, Forward, ok ), "NaN camera position" );
		} );

		registry.Add( "particle-orientation", "every returned basis is orthonormal across modes and inputs", () =>
		{
			var modes = new[] { ParticleOrientationMode.CameraBillboard, ParticleOrientationMode.VelocityAligned, ParticleOrientationMode.AxialBillboard, ParticleOrientationMode.StretchedBillboard };
			var velocities = new[] { Vector3.Zero, new Vector3( 1, 0, 0 ), new Vector3( 0, 0, 7 ), new Vector3( -2, 5, 3 ), new Vector3( 0, -9, 0 ), new Vector3( 0.001f, 0.002f, -0.003f ) };
			var eyes = new[] { new Vector3( 0, -100, 0 ), new Vector3( 30, 40, 50 ), new Vector3( 0, 0, 100 ), new Vector3( -80, 10, -20 ) };
			foreach ( var mode in modes )
				foreach ( var v in velocities )
					foreach ( var eye in eyes )
					{
						var r = ParticleOrientation.Evaluate( new Vector3( 1, 2, 3 ), v, eye, Vector3.UnitY, Opt( mode, stretch: 0.7f ) );
						CheckBasis( r.Basis, $"{mode} v={v} eye={eye}" );
						Check( float.IsFinite( r.LengthScale ) && r.LengthScale >= 1f, $"{mode}: length scale {r.LengthScale} must be finite and >= 1" );
						Near( r.WidthScale, 1f, 1e-6f, $"{mode}: width scale" );
					}
		} );

		registry.Add( "particle-orientation", "rotation basis and quaternion round-trip", () =>
		{
			var q = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, MathF.PI / 2 );
			var b = ParticleOrientation.BasisFromRotation( q );
			Vec( b.Right, Vector3.UnitY, 1e-4f, "rotated right (Z 90deg)" );
			Vec( b.Up, -Vector3.UnitX, 1e-4f, "rotated up" );
			Vec( b.Normal, Vector3.UnitZ, 1e-4f, "rotated normal" );
			CheckBasis( b, "rotation basis" );
			// Round-trip: the basis quaternion must rotate local axes back
			// onto the basis vectors.
			var m = Matrix4x4.CreateFromQuaternion( b.ToQuaternion() );
			Vec( Vector3.Transform( Vector3.UnitX, m ), b.Right, 1e-4f, "round-trip X" );
			Vec( Vector3.Transform( Vector3.UnitY, m ), b.Up, 1e-4f, "round-trip Y" );
			Vec( Vector3.Transform( Vector3.UnitZ, m ), b.Normal, 1e-4f, "round-trip Z" );
			Vec( ParticleOrientation.BasisFromRotation( Quaternion.Identity ).Right, Vector3.UnitX, 1e-4f, "identity right" );
			Throws<ArgumentException>( () => ParticleOrientation.BasisFromRotation( new Quaternion( 0, 0, 0, 0 ) ), "zero quaternion" );
		} );

		registry.Add( "particle-orientation", "state overload and billboard quad corners use the per-particle look-at basis", () =>
		{
			var state = new ParticleState { Position = new Vector3( 5, 0, 0 ), Velocity = new Vector3( 1, 0, 0 ), Rotation = Quaternion.Identity, Size = 2 };
			var r = ParticleOrientation.Evaluate( state, Eye, Forward, Opt( ParticleOrientationMode.CameraBillboard ) );
			// CLOSE-OUT FIX (first agent's test bug, production untouched):
			// the old expectation assumed the layer's fixed view basis
			// (Right = X) for a particle at (5,0,0). Evaluate is spherical
			// per particle: normal = (Eye - Position) normalized =
			// (-5,-100,0)/sqrt(10025) = (-0.049938,-0.998752,0), so the
			// basis tilts ~2.86 deg from the view basis: Right = Z x
			// normal = (0.998752,-0.049938,0), Up = Z. The origin
			// billboard test above still pins the exact layer basis
			// (Right = X, Up = Z, normal = -Y) for a centred particle.
			Vec( r.Basis.Right, new Vector3( 0.998752f, -0.049938f, 0 ), 1e-4f, "state billboard right" );
			Vec( r.Basis.Up, Vector3.UnitZ, 1e-4f, "state billboard up" );
			Vec( r.Basis.Normal, new Vector3( -0.049938f, -0.998752f, 0 ), 1e-4f, "state billboard normal" );
			CheckBasis( r.Basis, "state billboard" );
			// Quad corners in -r-u / +r-u / +r+u / -r+u order, half-size 1.
			var (a, b, c, d) = r.QuadCorners( state.Position, state.Size );
			Vec( a, new Vector3( 4.001248f, 0.049938f, -1 ), 1e-4f, "layer corner a" );
			Vec( b, new Vector3( 5.998752f, -0.049938f, -1 ), 1e-4f, "layer corner b" );
			Vec( c, new Vector3( 5.998752f, -0.049938f, 1 ), 1e-4f, "layer corner c" );
			Vec( d, new Vector3( 4.001248f, 0.049938f, 1 ), 1e-4f, "layer corner d" );
			// ToMatrix: size-2 billboard at (5,0,0) maps local (1,1,0) to
			// centre + Right*2 + Up*2 = (6.997505,-0.099875,2).
			Vec( Vector3.Transform( new Vector3( 1, 1, 0 ), r.ToMatrix( state.Position, state.Size ) ), new Vector3( 6.997505f, -0.099875f, 2 ), 1e-4f, "billboard matrix corner" );
		} );
	}
}
