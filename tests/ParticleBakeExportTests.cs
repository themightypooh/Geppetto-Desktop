using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleBakeExportTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		string work = settings.WorkDirectory;
		void Check( string name, Action test ) => registry.Add( "particle-bake-export", name, test );
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Func<object?> action, string label )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( $"expected an error for {label}, got silent success" );
		}
		ParticleBakeParticle P( float px, float py, float pz, float vx, float vy, float vz, float size, float age, bool alive = true )
			=> new() { Position = new[] { px, py, pz }, Velocity = new[] { vx, vy, vz }, Size = size, Age = age, Alive = alive };

		ParticleBakeDump SampleDump()
		{
			var frames = new List<ParticleBakeFrame>
			{
				new() { Index = 0, Particles = new List<ParticleBakeParticle>
					{ P( 1.5f, -2.25f, 3f, 10f, 0f, -5f, 0.5f, 0.25f ), P( 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f ) } },
				new() { Index = 1, Particles = new List<ParticleBakeParticle>() },
				new() { Index = 2, Particles = new List<ParticleBakeParticle>
					{ P( 100.125f, 200.5f, -300.75f, -1f, 2f, 3f, 2.5f, 9.75f ),
					  P( 7f, 8f, 9f, 0f, 0f, 0f, 1f, 1f, alive: false ),
					  P( -0.001f, 123456f, 0.5f, 3.5f, -3.5f, 0f, 0.125f, 0.5f ) } },
			};
			return ParticleBakeExport.Create( frames, 60f, "p39-test-hash-abc" );
		}

		void AssertSameDump( ParticleBakeDump a, ParticleBakeDump b, string label )
		{
			Assert( a.Version == b.Version, $"{label}: version differs" );
			Assert( a.DefinitionHash == b.DefinitionHash, $"{label}: definitionHash differs" );
			Assert( a.FrameRate == b.FrameRate, $"{label}: frameRate differs" );
			Assert( a.FrameCount == b.FrameCount, $"{label}: frameCount differs" );
			Assert( a.Frames.Count == b.Frames.Count, $"{label}: frame count differs" );
			for ( int i = 0; i < a.Frames.Count; i++ )
			{
				Assert( a.Frames[i].Index == b.Frames[i].Index, $"{label}: frame {i} index differs" );
				Assert( a.Frames[i].Particles.Count == b.Frames[i].Particles.Count, $"{label}: frame {i} particle count differs" );
				for ( int j = 0; j < a.Frames[i].Particles.Count; j++ )
				{
					var x = a.Frames[i].Particles[j]; var y = b.Frames[i].Particles[j];
					Assert( x.Position[0] == y.Position[0] && x.Position[1] == y.Position[1] && x.Position[2] == y.Position[2], $"{label}: frame {i} particle {j} position differs" );
					Assert( x.Velocity[0] == y.Velocity[0] && x.Velocity[1] == y.Velocity[1] && x.Velocity[2] == y.Velocity[2], $"{label}: frame {i} particle {j} velocity differs" );
					Assert( x.Size == y.Size, $"{label}: frame {i} particle {j} size differs" );
					Assert( x.Age == y.Age, $"{label}: frame {i} particle {j} age differs" );
					Assert( x.Alive == y.Alive, $"{label}: frame {i} particle {j} alive differs" );
				}
			}
		}

		Check( "write-read round-trip is identical on a multi-frame sample", () =>
		{
			var dump = SampleDump();
			var back = ParticleBakeExport.Read( ParticleBakeExport.Write( dump ) );
			AssertSameDump( dump, back, "round-trip" );
			Assert( ParticleBakeExport.Write( back ) == ParticleBakeExport.Write( dump ), "re-serialized dump differs" );
		} );

		Check( "FromStates keeps live particles exactly and drops dead slots", () =>
		{
			var states = new[]
			{
				new ParticleState { Position = new Vector3( 1, 2, 3 ), Velocity = new Vector3( 4, 5, 6 ), Age = 0.5f, Alive = 1, Size = 0.75f },
				new ParticleState { Position = new Vector3( 9, 9, 9 ), Velocity = new Vector3( 9, 9, 9 ), Age = 9f, Alive = 0, Size = 9f },
				new ParticleState { Position = new Vector3( -1, -2, -3 ), Velocity = new Vector3( 0, 0, 1 ), Age = 0f, Alive = 1, Size = 0.25f },
			};
			var frame = ParticleBakeExport.FromStates( states, 0 );
			Assert( frame.Index == 0, "frame index lost" );
			Assert( ParticleBakeExport.FromStates( states, 4 ).Index == 4, "FromStates must keep a non-zero frame index" );
			Assert( frame.Particles.Count == 2, $"expected 2 live particles, got {frame.Particles.Count}" );
			Assert( frame.Particles[0].Position[0] == 1 && frame.Particles[0].Velocity[2] == 6 && frame.Particles[0].Size == 0.75f && frame.Particles[0].Age == 0.5f && frame.Particles[0].Alive, "first live particle fields differ" );
			Assert( frame.Particles[1].Position[0] == -1 && frame.Particles[1].Age == 0f, "second live particle fields differ" );
			var dump = ParticleBakeExport.Create( new[] { frame }, 60f );
			AssertSameDump( dump, ParticleBakeExport.Read( ParticleBakeExport.Write( dump ) ), "FromStates round-trip" );
		} );

		Check( "bad version fails loudly", () =>
		{
			string json = ParticleBakeExport.Write( SampleDump() );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"version\":1", "\"version\":2" ) ), "version 2" );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"version\":1", "\"version\":0" ) ), "version 0" );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"version\":1,", "" ) ), "missing version" );
			Throws( () => ParticleBakeExport.Write( new ParticleBakeDump { Version = 2, FrameRate = 60f, FrameCount = 0 } ), "writing version 2" );
		} );

		Check( "malformed JSON fails loudly", () =>
		{
			Throws( () => ParticleBakeExport.Read( "{not json" ), "garbage" );
			Throws( () => ParticleBakeExport.Read( ParticleBakeExport.Write( SampleDump() )[..20] ), "truncated dump" );
			Throws( () => ParticleBakeExport.Read( "" ), "empty string" );
			Throws( () => ParticleBakeExport.Read( "[1,2,3]" ), "top-level array" );
		} );

		Check( "negative size fails loudly", () =>
		{
			var dump = SampleDump();
			dump.Frames[0].Particles[0].Size = -0.5f;
			Throws( () => ParticleBakeExport.Write( dump ), "negative size on write" );
			string json = ParticleBakeExport.Write( SampleDump() );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"size\":0.5", "\"size\":-0.5" ) ), "negative size on read" );
		} );

		Check( "invalid values fail loudly: NaN, infinity, negative age", () =>
		{
			var nan = SampleDump();
			nan.Frames[0].Particles[0].Position = new[] { float.NaN, 0f, 0f };
			Throws( () => ParticleBakeExport.Write( nan ), "NaN position on write" );
			string json = ParticleBakeExport.Write( SampleDump() );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"position\":[1.5,", "\"position\":[1e999," ) ), "infinite position on read" );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"age\":0.25", "\"age\":-0.25" ) ), "negative age on read" );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"velocity\":[10,", "\"velocity\":[10,0," ) ), "4-component velocity on read" );
		} );

		Check( "frameCount, frameRate, and frame index mismatches fail loudly", () =>
		{
			string json = ParticleBakeExport.Write( SampleDump() );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"frameCount\":3", "\"frameCount\":2" ) ), "frameCount mismatch" );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"frameRate\":60", "\"frameRate\":0" ) ), "frameRate 0" );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"frameRate\":60", "\"frameRate\":-30" ) ), "negative frameRate" );
			Throws( () => ParticleBakeExport.Read( json.Replace( "\"index\":2", "\"index\":5" ) ), "frame index out of sequence" );
		} );

		Check( "unknown fields are ignored at dump, frame, and particle level", () =>
		{
			string json = ParticleBakeExport.Write( SampleDump() );
			json = json.Replace( "\"definitionHash\"", "\"futureDumpField\":{\"x\":1},\"definitionHash\"" );
			int frameAt = json.IndexOf( "{\"index\":0,", StringComparison.Ordinal );
			json = json.Insert( frameAt + "{\"index\":0,".Length, "\"futureFrameField\":true," );
			int particleAt = json.IndexOf( "\"position\"", StringComparison.Ordinal );
			json = json.Insert( particleAt, "\"futureParticleField\":\"v2\"," );
			var back = ParticleBakeExport.Read( json );
			AssertSameDump( SampleDump(), back, "unknown-field tolerance" );
		} );

		Check( "definitionHash round-trips, and defaults to empty for unknown", () =>
		{
			Assert( ParticleBakeExport.Read( ParticleBakeExport.Write( SampleDump() ) ).DefinitionHash == "p39-test-hash-abc", "hash lost" );
			var plain = ParticleBakeExport.Create( new List<ParticleBakeFrame>(), 30f );
			Assert( plain.DefinitionHash == "", "default hash should be empty" );
			Assert( ParticleBakeExport.Read( ParticleBakeExport.Write( plain ) ).DefinitionHash == "", "empty hash lost" );
			string noHash = ParticleBakeExport.Write( plain ).Replace( "\"definitionHash\":\"\",", "" );
			Assert( ParticleBakeExport.Read( noHash ).DefinitionHash == "", "missing hash should default to empty" );
		} );

		Check( "bytes-per-particle is measured on a sample dump", () =>
		{
			var frames = new List<ParticleBakeFrame>();
			for ( int f = 0; f < 10; f++ )
			{
				var frame = new ParticleBakeFrame { Index = f };
				for ( int i = 0; i < 100; i++ )
					frame.Particles.Add( P( i * 1.5f, i * -0.5f, f * 2f, 1f, 2f, 3f, 0.5f, f * 0.1f + i * 0.01f ) );
				frames.Add( frame );
			}
			var dump = ParticleBakeExport.Create( frames, 60f, "measure" );
			string json = ParticleBakeExport.Write( dump );
			int total = ParticleBakeExport.TotalParticles( dump );
			Assert( total == 1000, $"expected 1000 particles, got {total}" );
			double bpp = ParticleBakeExport.BytesPerParticle( json, total );
			Console.WriteLine( $"particle-bake-export sample: {json.Length} bytes / {total} particles = {bpp:F2} bytes per particle" );
			Assert( bpp > 40 && bpp < 250, $"bytes per particle {bpp:F2} outside expected 40-250 range" );
			Throws( () => ParticleBakeExport.BytesPerParticle( json, 0 ), "zero particles for bytes-per-particle" );
		} );

		Check( "SaveFile and LoadFile round-trip", () =>
		{
			Directory.CreateDirectory( work );
			string path = Path.Combine( work, "bake-dump.json" );
			var dump = SampleDump();
			ParticleBakeExport.SaveFile( dump, path );
			Assert( File.Exists( path ), "dump file was not written" );
			AssertSameDump( dump, ParticleBakeExport.LoadFile( path ), "file round-trip" );
			Throws( () => ParticleBakeExport.LoadFile( Path.Combine( work, "missing.json" ) ), "missing file" );
		} );
	}
}
