using System.Numerics;
using Marionette.Desktop;

// Wave-6 lane P21 tests. Group: particle-shapes.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   ParticleEmitterShapesTests.Register( registry, settings );
// after the GpuParticleSystemTests.Register line.

internal static class ParticleEmitterShapesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}

		registry.Add( "particle-shapes", "torus surface points lie on the tube with outward normals", () =>
		{
			var shape = new ParticleTorusShape { MajorRadius = 2, MinorRadius = 0.5f };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 2000; i++ )
			{
				var s = shape.Sample( ref rng );
				var ringR = MathF.Sqrt( s.Position.X * s.Position.X + s.Position.Y * s.Position.Y );
				var tubeDist = MathF.Sqrt( (ringR - 2) * (ringR - 2) + s.Position.Z * s.Position.Z );
				Near( tubeDist, 0.5f, 1e-3f, $"torus surface tube distance sample {i}" );
				Near( s.Normal.Length(), 1, 1e-4f, $"torus surface normal length sample {i}" );
				var radial = new Vector3( s.Position.X / ringR, s.Position.Y / ringR, 0 );
				var tubeDir = (s.Position - 2 * radial) / 0.5f;
				Near( (s.Normal - tubeDir).Length(), 0, 1e-4f, $"torus surface normal direction sample {i}" );
			}
		} );

		registry.Add( "particle-shapes", "torus volume points lie inside the tube", () =>
		{
			var shape = new ParticleTorusShape { MajorRadius = 2, MinorRadius = 0.5f, Mode = ParticleTorusMode.Volume };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 2000; i++ )
			{
				var s = shape.Sample( ref rng );
				var ringR = MathF.Sqrt( s.Position.X * s.Position.X + s.Position.Y * s.Position.Y );
				var tubeDist = MathF.Sqrt( (ringR - 2) * (ringR - 2) + s.Position.Z * s.Position.Z );
				Check( tubeDist <= 0.5f + 1e-3f, $"torus volume sample {i} outside tube: {tubeDist}" );
			}
		} );

		registry.Add( "particle-shapes", "torus surface area weighting favours the outer equator", () =>
		{
			// Density ∝ (R + r·cos φ): fraction with cos φ > 0 is 0.5 + r/(πR) = 0.659 for R=2, r=1.
			// Uniform φ would give 0.5, so this proves the rejection sampling, not just the shape.
			var shape = new ParticleTorusShape { MajorRadius = 2, MinorRadius = 1 };
			var rng = new ParticleRng( 42 );
			var outer = 0;
			const int n = 4000;
			for ( int i = 0; i < n; i++ )
			{
				var s = shape.Sample( ref rng );
				var ringR = MathF.Sqrt( s.Position.X * s.Position.X + s.Position.Y * s.Position.Y );
				var radial = new Vector3( s.Position.X / ringR, s.Position.Y / ringR, 0 );
				var tubeDir = (s.Position - 2 * radial) / 1f;
				if ( Vector3.Dot( tubeDir, radial ) > 0 ) outer++;
			}
			var fraction = (float)outer / n;
			Near( fraction, 0.659f, 0.04f, "torus outer-equator fraction" );
		} );

		registry.Add( "particle-shapes", "torus same seed gives identical samples", () =>
		{
			var shape = new ParticleTorusShape { MajorRadius = 2, MinorRadius = 0.5f };
			var a = new ParticleRng( 7 );
			var b = new ParticleRng( 7 );
			for ( int i = 0; i < 100; i++ )
			{
				var sa = shape.Sample( ref a );
				var sb = shape.Sample( ref b );
				Check( sa.Position == sb.Position && sa.Normal == sb.Normal, $"torus determinism sample {i} differs" );
			}
		} );

		registry.Add( "particle-shapes", "cylinder side points lie on the wall with radial normals", () =>
		{
			var shape = new ParticleCylinderShape { Radius = 1, Height = 2, Mode = ParticleCylinderMode.Side };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 1000; i++ )
			{
				var s = shape.Sample( ref rng );
				var radius = MathF.Sqrt( s.Position.X * s.Position.X + s.Position.Y * s.Position.Y );
				Near( radius, 1, 1e-3f, $"cylinder side radius sample {i}" );
				Check( MathF.Abs( s.Position.Z ) <= 1, $"cylinder side z out of range sample {i}: {s.Position.Z}" );
				Near( s.Normal.Length(), 1, 1e-4f, $"cylinder side normal length sample {i}" );
				var radial = new Vector3( s.Position.X / radius, s.Position.Y / radius, 0 );
				Near( (s.Normal - radial).Length(), 0, 1e-4f, $"cylinder side normal direction sample {i}" );
			}
		} );

		registry.Add( "particle-shapes", "cylinder caps land on the end discs with axial normals", () =>
		{
			var shape = new ParticleCylinderShape { Radius = 1, Height = 2, Mode = ParticleCylinderMode.Caps };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 1000; i++ )
			{
				var s = shape.Sample( ref rng );
				Near( MathF.Abs( s.Position.Z ), 1, 1e-4f, $"cylinder cap z sample {i}" );
				var radius = MathF.Sqrt( s.Position.X * s.Position.X + s.Position.Y * s.Position.Y );
				Check( radius <= 1 + 1e-4f, $"cylinder cap radius sample {i}: {radius}" );
				Check( s.Normal == new Vector3( 0, 0, MathF.Sign( s.Position.Z ) ), $"cylinder cap normal sample {i}: {s.Normal}" );
			}
		} );

		registry.Add( "particle-shapes", "cylinder side-and-caps split follows area", () =>
		{
			// Side 2πRH = 4π vs caps 2πR² = 2π → side fraction 2/3.
			var shape = new ParticleCylinderShape { Radius = 1, Height = 2, Mode = ParticleCylinderMode.SideAndCaps };
			var rng = new ParticleRng( 42 );
			var side = 0;
			const int n = 3000;
			for ( int i = 0; i < n; i++ )
			{
				var s = shape.Sample( ref rng );
				if ( MathF.Abs( s.Position.Z ) < 1 - 1e-4f ) side++;
			}
			Near( (float)side / n, 2f / 3f, 0.04f, "cylinder side fraction" );
		} );

		registry.Add( "particle-shapes", "cylinder volume points lie inside", () =>
		{
			var shape = new ParticleCylinderShape { Radius = 1, Height = 2, Mode = ParticleCylinderMode.Volume };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 1000; i++ )
			{
				var s = shape.Sample( ref rng );
				var radius = MathF.Sqrt( s.Position.X * s.Position.X + s.Position.Y * s.Position.Y );
				Check( radius <= 1 + 1e-3f, $"cylinder volume radius sample {i}: {radius}" );
				Check( MathF.Abs( s.Position.Z ) <= 1 + 1e-4f, $"cylinder volume z sample {i}: {s.Position.Z}" );
			}
		} );

		registry.Add( "particle-shapes", "cone lateral points lie on the slant with perpendicular normals", () =>
		{
			var shape = new ParticleConeShape { Radius = 2, Height = 3, Mode = ParticleConeMode.Lateral };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 1000; i++ )
			{
				var s = shape.Sample( ref rng );
				Check( s.Position.Z >= 0 && s.Position.Z <= 3, $"cone lateral z sample {i}: {s.Position.Z}" );
				var radial = MathF.Sqrt( s.Position.X * s.Position.X + s.Position.Y * s.Position.Y );
				Near( radial, 2 * s.Position.Z / 3, 1e-3f, $"cone lateral radius sample {i}" );
				if ( radial > 1e-3f )
				{
					var radialDir = new Vector3( s.Position.X / radial, s.Position.Y / radial, 0 );
					var slantDir = Vector3.Normalize( 2 * radialDir + new Vector3( 0, 0, 3 ) );
					Near( Vector3.Dot( s.Normal, slantDir ), 0, 1e-3f, $"cone lateral normal perpendicular sample {i}" );
					Check( Vector3.Dot( s.Normal, radialDir ) > 0, $"cone lateral normal points inward sample {i}" );
				}
			}
		} );

		registry.Add( "particle-shapes", "cone volume points lie inside", () =>
		{
			var shape = new ParticleConeShape { Radius = 2, Height = 3, Mode = ParticleConeMode.Volume };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 1000; i++ )
			{
				var s = shape.Sample( ref rng );
				Check( s.Position.Z >= 0 && s.Position.Z <= 3, $"cone volume z sample {i}: {s.Position.Z}" );
				var radial = MathF.Sqrt( s.Position.X * s.Position.X + s.Position.Y * s.Position.Y );
				Check( radial <= 2 * s.Position.Z / 3 + 1e-3f, $"cone volume radius sample {i}: {radial} at z {s.Position.Z}" );
			}
		} );

		registry.Add( "particle-shapes", "cone lateral-and-base split follows area", () =>
		{
			// Lateral πRL vs base πR², L = √13 ≈ 3.606 → lateral fraction L/(L+R) ≈ 0.643.
			var shape = new ParticleConeShape { Radius = 2, Height = 3, Mode = ParticleConeMode.LateralAndBase };
			var rng = new ParticleRng( 42 );
			var lateral = 0;
			const int n = 3000;
			for ( int i = 0; i < n; i++ )
			{
				var s = shape.Sample( ref rng );
				if ( s.Position.Z < 3 - 1e-4f ) lateral++;
			}
			Near( (float)lateral / n, 0.643f, 0.04f, "cone lateral fraction" );
		} );

		registry.Add( "particle-shapes", "disc points lie in the disc with axial normal", () =>
		{
			var shape = new ParticleDiscShape { Radius = 2 };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 1000; i++ )
			{
				var s = shape.Sample( ref rng );
				Near( s.Position.Z, 0, 1e-6f, $"disc z sample {i}" );
				var radius = MathF.Sqrt( s.Position.X * s.Position.X + s.Position.Y * s.Position.Y );
				Check( radius <= 2 + 1e-4f, $"disc radius sample {i}: {radius}" );
				Check( s.Normal == new Vector3( 0, 0, 1 ), $"disc normal sample {i}: {s.Normal}" );
			}
		} );

		registry.Add( "particle-shapes", "capsule surface points lie at radius from the segment", () =>
		{
			var shape = new ParticleCapsuleShape { Radius = 0.5f, CylinderHeight = 2, Mode = ParticleCapsuleMode.Surface };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 1000; i++ )
			{
				var s = shape.Sample( ref rng );
				var cz = MathF.Max( -1, MathF.Min( 1, s.Position.Z ) );
				var closest = new Vector3( 0, 0, cz );
				Near( (s.Position - closest).Length(), 0.5f, 1e-3f, $"capsule surface distance sample {i}" );
				Near( (s.Normal - (s.Position - closest) / 0.5f).Length(), 0, 1e-4f, $"capsule surface normal sample {i}" );
			}
		} );

		registry.Add( "particle-shapes", "capsule volume points lie inside", () =>
		{
			var shape = new ParticleCapsuleShape { Radius = 0.5f, CylinderHeight = 2, Mode = ParticleCapsuleMode.Volume };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 1000; i++ )
			{
				var s = shape.Sample( ref rng );
				var cz = MathF.Max( -1, MathF.Min( 1, s.Position.Z ) );
				var dist = (s.Position - new Vector3( 0, 0, cz )).Length();
				Check( dist <= 0.5f + 1e-3f, $"capsule volume distance sample {i}: {dist}" );
			}
		} );

		registry.Add( "particle-shapes", "capsule with zero cylinder height is a sphere", () =>
		{
			var rng = new ParticleRng( 42 );
			var surface = new ParticleCapsuleShape { Radius = 0.5f, CylinderHeight = 0, Mode = ParticleCapsuleMode.Surface };
			for ( int i = 0; i < 500; i++ )
				Near( surface.Sample( ref rng ).Position.Length(), 0.5f, 1e-3f, $"capsule sphere surface sample {i}" );
			var volume = new ParticleCapsuleShape { Radius = 0.5f, CylinderHeight = 0, Mode = ParticleCapsuleMode.Volume };
			for ( int i = 0; i < 500; i++ )
			{
				var d = volume.Sample( ref rng ).Position.Length();
				Check( d <= 0.5f + 1e-3f, $"capsule sphere volume sample {i}: {d}" );
			}
		} );

		registry.Add( "particle-shapes", "grid cells land on the lattice", () =>
		{
			var shape = new ParticleGridShape { CountX = 3, CountY = 2, CountZ = 1, Spacing = new Vector3( 2, 3, 5 ), Jitter = 0 };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 6; i++ )
			{
				var s = shape.CellAt( i, ref rng );
				var ix = i % 3;
				var iy = (i / 3) % 2;
				Near( s.Position.X, (ix - 1) * 2, 1e-6f, $"grid cell {i} x" );
				Near( s.Position.Y, (iy - 0.5f) * 3, 1e-6f, $"grid cell {i} y" );
				Near( s.Position.Z, 0, 1e-6f, $"grid cell {i} z" );
				Check( s.Normal == new Vector3( 0, 0, 1 ), $"grid cell {i} normal" );
			}
			// Jitter applies on every axis, including Z when CountZ = 1: the jittered
			// point must sit within Jitter*Spacing of a lattice point on each axis.
			var jittered = new ParticleGridShape { CountX = 3, CountY = 2, CountZ = 1, Spacing = new Vector3( 2, 3, 5 ), Jitter = 0.25f };
			for ( int i = 0; i < 500; i++ )
			{
				var s = jittered.Sample( ref rng );
				var fx = s.Position.X / 2 + 1;
				var fy = s.Position.Y / 3 + 0.5f;
				var fz = s.Position.Z / 5;
				Check( MathF.Abs( fx - MathF.Round( fx ) ) * 2 <= 0.5f + 1e-4f, $"grid jitter x sample {i}: {s.Position.X}" );
				Check( MathF.Abs( fy - MathF.Round( fy ) ) * 3 <= 0.75f + 1e-4f, $"grid jitter y sample {i}: {s.Position.Y}" );
				Check( MathF.Abs( fz - MathF.Round( fz ) ) * 5 <= 1.25f + 1e-4f, $"grid jitter z sample {i}: {s.Position.Z}" );
			}
		} );

		registry.Add( "particle-shapes", "axis frame rotates shapes", () =>
		{
			var cyl = new ParticleCylinderShape { Radius = 1, Height = 2, Axis = new Vector3( 1, 0, 0 ), Mode = ParticleCylinderMode.Side };
			var rng = new ParticleRng( 42 );
			for ( int i = 0; i < 500; i++ )
			{
				var s = cyl.Sample( ref rng );
				var radial = MathF.Sqrt( s.Position.Y * s.Position.Y + s.Position.Z * s.Position.Z );
				Near( radial, 1, 1e-3f, $"x-axis cylinder radius sample {i}" );
				Check( MathF.Abs( s.Position.X ) <= 1, $"x-axis cylinder x sample {i}: {s.Position.X}" );
				Near( Vector3.Dot( s.Normal, new Vector3( 1, 0, 0 ) ), 0, 1e-4f, $"x-axis cylinder normal sample {i}" );
			}
			var disc = new ParticleDiscShape { Radius = 2, Axis = new Vector3( 0, 1, 0 ) };
			for ( int i = 0; i < 200; i++ )
			{
				var s = disc.Sample( ref rng );
				Near( s.Position.Y, 0, 1e-6f, $"y-axis disc y sample {i}" );
				Check( s.Normal == new Vector3( 0, 1, 0 ), $"y-axis disc normal sample {i}" );
			}
			var unnormalized = new ParticleDiscShape { Radius = 1, Axis = new Vector3( 0, 0, 5 ) };
			Check( unnormalized.Sample( ref rng ).Normal == new Vector3( 0, 0, 1 ), "non-unit axis must still give a unit normal" );
		} );

		registry.Add( "particle-shapes", "degenerate shapes fail loudly", () =>
		{
			var rng = new ParticleRng( 42 );
			Throws( () => new ParticleTorusShape { MajorRadius = 0 }.Sample( ref rng ), "torus zero major radius" );
			Throws( () => new ParticleTorusShape { MinorRadius = -1 }.Sample( ref rng ), "torus negative minor radius" );
			Throws( () => new ParticleTorusShape { Axis = new Vector3( 0, 0, 0 ) }.Sample( ref rng ), "torus zero axis" );
			Throws( () => new ParticleCylinderShape { Radius = 0 }.Sample( ref rng ), "cylinder zero radius" );
			Throws( () => new ParticleCylinderShape { Height = -1 }.Sample( ref rng ), "cylinder negative height" );
			Throws( () => new ParticleConeShape { Height = 0 }.Sample( ref rng ), "cone zero height" );
			Throws( () => new ParticleDiscShape { Radius = float.NaN }.Sample( ref rng ), "disc NaN radius" );
			Throws( () => new ParticleCapsuleShape { Radius = 0 }.Sample( ref rng ), "capsule zero radius" );
			Throws( () => new ParticleCapsuleShape { CylinderHeight = -1 }.Sample( ref rng ), "capsule negative height" );
			Throws( () => new ParticleGridShape { CountX = 0 }.Sample( ref rng ), "grid zero count" );
			Throws( () => new ParticleGridShape { Spacing = new Vector3( -1, 1, 1 ) }.Sample( ref rng ), "grid negative spacing" );
			Throws( () => new ParticleGridShape { Jitter = 0.6f }.Sample( ref rng ), "grid jitter above 0.5" );
			var grid = new ParticleGridShape { CountX = 2, CountY = 2, CountZ = 1 };
			Throws( () => grid.CellAt( -1, ref rng ), "grid negative cell index" );
			Throws( () => grid.CellAt( 4, ref rng ), "grid cell index past the end" );
		} );

		registry.Add( "particle-shapes", "different seeds give different samples", () =>
		{
			var shape = new ParticleTorusShape { MajorRadius = 2, MinorRadius = 0.5f };
			var a = new ParticleRng( 1 );
			var b = new ParticleRng( 2 );
			var differ = false;
			for ( int i = 0; i < 10; i++ )
				if ( shape.Sample( ref a ).Position != shape.Sample( ref b ).Position ) differ = true;
			Check( differ, "seeds 1 and 2 gave identical first 10 samples" );
		} );

		registry.Add( "particle-shapes", "normals are unit length everywhere", () =>
		{
			var rng = new ParticleRng( 42 );
			void Sweep( Func<ParticleShapeSample> sample, string what )
			{
				for ( int i = 0; i < 200; i++ )
					Near( sample().Normal.Length(), 1, 1e-4f, $"{what} normal length sample {i}" );
			}
			Sweep( () => new ParticleTorusShape { MajorRadius = 2, MinorRadius = 0.5f }.Sample( ref rng ), "torus surface" );
			Sweep( () => new ParticleTorusShape { MajorRadius = 2, MinorRadius = 0.5f, Mode = ParticleTorusMode.Volume }.Sample( ref rng ), "torus volume" );
			Sweep( () => new ParticleCylinderShape { Radius = 1, Height = 2, Mode = ParticleCylinderMode.SideAndCaps }.Sample( ref rng ), "cylinder" );
			Sweep( () => new ParticleCylinderShape { Radius = 1, Height = 2, Mode = ParticleCylinderMode.Volume }.Sample( ref rng ), "cylinder volume" );
			Sweep( () => new ParticleConeShape { Radius = 2, Height = 3, Mode = ParticleConeMode.LateralAndBase }.Sample( ref rng ), "cone" );
			Sweep( () => new ParticleConeShape { Radius = 2, Height = 3, Mode = ParticleConeMode.Volume }.Sample( ref rng ), "cone volume" );
			Sweep( () => new ParticleDiscShape { Radius = 2 }.Sample( ref rng ), "disc" );
			Sweep( () => new ParticleCapsuleShape { Radius = 0.5f, CylinderHeight = 2, Mode = ParticleCapsuleMode.Surface }.Sample( ref rng ), "capsule" );
			Sweep( () => new ParticleCapsuleShape { Radius = 0.5f, CylinderHeight = 2, Mode = ParticleCapsuleMode.Volume }.Sample( ref rng ), "capsule volume" );
			Sweep( () => new ParticleGridShape { CountX = 3, CountY = 2, CountZ = 2, Axis = new Vector3( 1, 2, 3 ) }.Sample( ref rng ), "grid tilted axis" );
		} );
	}
}
