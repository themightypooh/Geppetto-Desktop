using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Marionette.Desktop;

internal static class ParticleRenderLayerTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float actual, float expected, float tol, string what )
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

		// LiveBuilder.Data is internal; the live-scene tests read it the same way.
		List<Vector3> Verts( LiveBuilder b )
		{
			var data = (float[])typeof( LiveBuilder ).GetProperty( "Data", BindingFlags.Instance | BindingFlags.NonPublic )!.GetValue( b )!;
			var list = new List<Vector3>();
			for ( int v = 0; v < b.VertexCount; v++ )
				list.Add( new Vector3( data[v * ViewportMesh.FloatsPerVertex], data[v * ViewportMesh.FloatsPerVertex + 1], data[v * ViewportMesh.FloatsPerVertex + 2] ) );
			return list;
		}

		LiveView View() => new( new Vector3( 0, -6, 0 ), Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY );

		ParticleMesh Triangle() => new(
			new[] { new Vector3( 0, 0, 0 ), new Vector3( 2, 0, 0 ), new Vector3( 0, 2, 0 ) },
			new[] { 0, 1, 2 } );

		ParticleSystem BurstSystem( Vector3 position, float size, float lifetime = 10, float speed = 0, Vector3? direction = null, int strandPoints = 0 )
		{
			var s = new ParticleSystem( new ParticleSystemSettings { Gravity = Vector3.Zero, Capacity = 64, StrandPoints = strandPoints } );
			s.AddEmitter( new ParticleEmitter
			{
				Position = position, LifetimeMin = lifetime, LifetimeMax = lifetime,
				SizeMin = size, SizeMax = size, Speed = speed,
				Direction = direction ?? Vector3.UnitZ, Bursts = { new ParticleBurst( 0, 1 ) },
			} );
			s.Step();
			return s;
		}

		registry.Add( "particle-render", "instance matrix matches hand-computed scale/rotation/translation", () =>
		{
			// Direct check of the matrix the layer uploads/expands.
			var state = new ParticleState
			{
				Position = new Vector3( 10, 20, 30 ),
				Rotation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, MathF.PI / 2 ),
				Size = 2,
			};
			var m = ParticleSystem.InstanceMatrix( state );
			var moved = Vector3.Transform( new Vector3( 1, 0, 0 ), m );
			Near( moved.X, 10, 1e-4f, "rotated x" ); Near( moved.Y, 22, 1e-4f, "rotated y" ); Near( moved.Z, 30, 1e-4f, "rotated z" );

			// End to end through the layer: identity rotation, size 2, centre (5,0,0).
			var system = BurstSystem( new Vector3( 5, 0, 0 ), 2 );
			var layer = new ParticleMeshInstanceLayer( system, Triangle() );
			var builder = new LiveBuilder();
			layer.Emit( builder, View() );
			Check( layer.LastDrawn == 1, $"drew {layer.LastDrawn} particles, expected 1" );
			Check( builder.VertexCount == 3, $"emitted {builder.VertexCount} vertices, expected 3 (one triangle)" );
			Near( layer.LastMatrices[0].Translation.X, 5, 1e-4f, "matrix centre x" );
			Near( layer.LastMatrices[0].M11, 2, 1e-4f, "matrix scale" );
			var verts = Verts( builder );
			Near( verts[0].X, 5, 1e-4f, "vertex 0 x" ); Near( verts[0].Y, 0, 1e-4f, "vertex 0 y" );
			Near( verts[1].X, 9, 1e-4f, "vertex 1 x" ); Near( verts[1].Y, 0, 1e-4f, "vertex 1 y" );
			Near( verts[2].X, 5, 1e-4f, "vertex 2 x" ); Near( verts[2].Y, 4, 1e-4f, "vertex 2 y" );
		} );

		registry.Add( "particle-render", "instance matrices are back-to-front ordered", () =>
		{
			var system = new ParticleSystem( new ParticleSystemSettings { Gravity = Vector3.Zero, Capacity = 64 } );
			foreach ( float y in new[] { 0f, 10f } )
				system.AddEmitter( new ParticleEmitter
				{
					Position = new Vector3( 0, y, 0 ), LifetimeMin = 10, LifetimeMax = 10,
					SizeMin = 1, SizeMax = 1, Bursts = { new ParticleBurst( 0, 1 ) },
				} );
			system.Step();
			var layer = new ParticleMeshInstanceLayer( system, Triangle() );
			layer.Emit( new LiveBuilder(), View() );
			Check( layer.LastDrawn == 2, $"drew {layer.LastDrawn} particles, expected 2" );
			Near( layer.LastMatrices[0].Translation.Y, 10, 1e-4f, "first matrix should be the farther particle (y=10)" );
			Near( layer.LastMatrices[1].Translation.Y, 0, 1e-4f, "second matrix should be the nearer particle (y=0)" );
		} );

		registry.Add( "particle-render", "instance fade shrinks a particle near death", () =>
		{
			var system = BurstSystem( Vector3.Zero, 1, lifetime: 1 );
			for ( int i = 0; i < 57; i++ ) system.Step();
			var layer = new ParticleMeshInstanceLayer( system, Triangle() );
			var builder = new LiveBuilder();
			layer.Emit( builder, View() );
			var verts = Verts( builder );
			Check( verts.Count == 3, $"expected 3 vertices, got {verts.Count}" );
			// Template vertex (2,0,0), size 1, centre at origin: distance is 2 × fade.
			float ageFrac = Math.Clamp( system.States[0].Age / system.LifetimeOf( 0 ), 0f, 1f );
			float expected = 2 * Math.Clamp( ( 1f - ageFrac ) / .3f, 0f, 1f );
			Near( verts[1].Length(), expected, 1e-3f, "faded vertex distance" );
			Check( expected < 0.5f, $"fade should have shrunk the particle (expected distance {expected})" );
		} );

		registry.Add( "particle-render", "instance fade disabled keeps full size near death", () =>
		{
			var system = BurstSystem( Vector3.Zero, 1, lifetime: 1 );
			for ( int i = 0; i < 57; i++ ) system.Step();
			var layer = new ParticleMeshInstanceLayer( system, Triangle() ) { FadeOut = 0 };
			var builder = new LiveBuilder();
			layer.Emit( builder, View() );
			var verts = Verts( builder );
			Near( verts[1].Length(), 2, 1e-3f, "unfaded vertex distance" );
		} );

		registry.Add( "particle-render", "spinning instance keeps basis length equal to size", () =>
		{
			var system = new ParticleSystem( new ParticleSystemSettings { Gravity = Vector3.Zero, Capacity = 64 } );
			var em = new ParticleEmitter
			{
				Position = Vector3.Zero, LifetimeMin = 10, LifetimeMax = 10,
				SizeMin = 2, SizeMax = 2, AngularSpeedMax = 6, Bursts = { new ParticleBurst( 0, 1 ) },
			};
			system.AddEmitter( em );
			for ( int i = 0; i < 10; i++ ) system.Step();
			var layer = new ParticleMeshInstanceLayer( system, Triangle() );
			layer.Emit( new LiveBuilder(), View() );
			var m = layer.LastMatrices[0];
			// Row-vector convention: each row is a scaled basis vector.
			Near( new Vector3( m.M11, m.M12, m.M13 ).Length(), 2, 1e-3f, "row 0 length" );
			Near( new Vector3( m.M21, m.M22, m.M23 ).Length(), 2, 1e-3f, "row 1 length" );
			Near( new Vector3( m.M31, m.M32, m.M33 ).Length(), 2, 1e-3f, "row 2 length" );
		} );

		registry.Add( "particle-render", "instance layer draws nothing for an empty system", () =>
		{
			var system = new ParticleSystem( new ParticleSystemSettings { Gravity = Vector3.Zero, Capacity = 64 } );
			var layer = new ParticleMeshInstanceLayer( system, Triangle() );
			var builder = new LiveBuilder();
			layer.Emit( builder, View() );
			Check( layer.LastDrawn == 0 && builder.VertexCount == 0, $"empty system drew {layer.LastDrawn} particles, {builder.VertexCount} vertices" );
		} );

		registry.Add( "particle-render", "instance advance caps at 8 steps per frame like billboards", () =>
		{
			var system = BurstSystem( Vector3.Zero, 1 );
			var layer = new ParticleMeshInstanceLayer( system, Triangle() );
			long before = system.StepIndex; // BurstSystem already stepped once to spawn.
			layer.Advance( 0.5f );
			Check( system.StepIndex == before + 8, $"0.5 s advance stepped {system.StepIndex - before} times, expected cap 8" );
			layer.Advance( 1f / 60 );
			Check( system.StepIndex == before + 9, $"one more frame stepped to {system.StepIndex - before} past start, expected 9" );
		} );

		registry.Add( "particle-render", "strand draws a ribbon along a moving trail", () =>
		{
			// 60 in/s along +X at 60 Hz = 1 inch per step. BurstSystem's spawn
			// step already moved the particle to x=1, so 3 more steps give the
			// 4-point trail x = 4, 3, 2, 1 (the spawn point has aged out).
			var system = BurstSystem( Vector3.Zero, 1, speed: 60, direction: Vector3.UnitX, strandPoints: 4 );
			for ( int i = 0; i < 3; i++ ) system.Step();
			var layer = new ParticleStrandLayer( system );
			var builder = new LiveBuilder();
			layer.Emit( builder, View() );
			Check( layer.LastDrawn == 1, $"drew {layer.LastDrawn} ribbons, expected 1" );
			Check( builder.VertexCount == 18, $"emitted {builder.VertexCount} vertices, expected 3 quads × 6" );
			var verts = Verts( builder );
			float maxZ = 0, tailZ = 0;
			foreach ( var v in verts )
			{
				Near( v.Y, 0, 1e-3f, "ribbon vertex should stay on the trail line (y=0)" );
				Check( v.X is >= 1 - 1e-3f and <= 4 + 1e-3f, $"ribbon vertex x={v.X} outside trail 1..4" );
				maxZ = MathF.Max( maxZ, MathF.Abs( v.Z ) );
				if ( MathF.Abs( v.X - 1 ) < 1e-3f ) tailZ = MathF.Max( tailZ, MathF.Abs( v.Z ) );
			}
			Near( maxZ, 0.5f, 1e-3f, "head half-width (size 1 × scale 1 / 2)" );
			Near( tailZ, 0.15f, 1e-3f, "tail half-width (head × taper 0.3)" );
		} );

		registry.Add( "particle-render", "strand ribbon width scales with particle size", () =>
		{
			var system = BurstSystem( Vector3.Zero, 2, speed: 60, direction: Vector3.UnitX, strandPoints: 4 );
			for ( int i = 0; i < 3; i++ ) system.Step();
			var layer = new ParticleStrandLayer( system );
			var builder = new LiveBuilder();
			layer.Emit( builder, View() );
			float maxZ = 0;
			foreach ( var v in Verts( builder ) ) maxZ = MathF.Max( maxZ, MathF.Abs( v.Z ) );
			Near( maxZ, 1.0f, 1e-3f, "head half-width for size 2" );
		} );

		registry.Add( "particle-render", "strand draws nothing for a motionless particle", () =>
		{
			// Zero velocity: every trail point is the spawn position, so every
			// segment is degenerate and must be skipped, not drawn as a quad.
			var system = BurstSystem( Vector3.Zero, 1, speed: 0, strandPoints: 4 );
			var layer = new ParticleStrandLayer( system );
			var builder = new LiveBuilder();
			layer.Emit( builder, View() );
			Check( layer.LastDrawn == 0 && builder.VertexCount == 0, $"motionless particle drew {layer.LastDrawn} ribbons, {builder.VertexCount} vertices" );
		} );

		registry.Add( "particle-render", "strand layer fails loudly without a strand history", () =>
		{
			var noHistory = new ParticleSystem( new ParticleSystemSettings { Gravity = Vector3.Zero, Capacity = 64, StrandPoints = 0 } );
			Throws<ArgumentException>( () => new ParticleStrandLayer( noHistory ), "strand layer without StrandPoints >= 2" );
			Throws<ArgumentNullException>( () => new ParticleStrandLayer( null! ), "strand layer with null system" );
			Throws<ArgumentNullException>( () => new ParticleMeshInstanceLayer( null!, Triangle() ), "instance layer with null system" );
			Throws<ArgumentNullException>( () => new ParticleMeshInstanceLayer( noHistory, null! ), "instance layer with null template" );
		} );
	}
}
