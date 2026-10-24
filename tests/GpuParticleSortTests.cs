using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU particle sort tests (particles lane 8, group "gpu-sort").
/// Order is compared exactly (indices are integers, ties resolve identically
/// on both sides); depths are compared only via order, never bit-for-bit,
/// per the lane agreement rule. Headless EGL only.
/// </summary>
internal static class GpuParticleSortTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool cond, string msg ) { if ( !cond ) throw new Exception( "gpu-sort: " + msg ); }

		static void CheckOrder( int[] actual, int[] expected, string msg )
		{
			if ( actual.Length != expected.Length ) throw new Exception( $"gpu-sort: {msg}: length {actual.Length} != {expected.Length}" );
			for ( int i = 0; i < expected.Length; i++ )
				if ( actual[i] != expected[i] ) throw new Exception( $"gpu-sort: {msg}: at {i} got {actual[i]} want {expected[i]}" );
		}

		// CPU reference order with the same rules as the GPU: (depth key, slot index).
		static int[] ReferenceOrder( ParticleState[] states, Vector3 cam, Vector3 fwd )
		{
			var items = new (uint key, int idx)[states.Length];
			for ( int i = 0; i < states.Length; i++ )
				items[i] = ( states[i].Alive == 0 ? GpuParticleSort.DeadKey : GpuParticleSort.DepthKey( states[i].Position, cam, fwd ), i );
			Array.Sort( items, ( a, b ) => a.key != b.key ? a.key.CompareTo( b.key ) : a.idx.CompareTo( b.idx ) );
			var order = new int[items.Length];
			for ( int i = 0; i < items.Length; i++ ) order[i] = items[i].idx;
			return order;
		}

		static ParticleState Make( Vector3 pos, float alive = 1f, float size = 1f ) => new()
		{
			Position = pos, Age = 0, Velocity = Vector3.Zero, Alive = alive,
			Rotation = Quaternion.Identity, AngularVelocity = Vector3.Zero, Size = size
		};

		static int[] GpuSort( GL gl, ParticleState[] states, Vector3 cam, Vector3 fwd )
		{
			uint prog = GpuParticleSort.CreateProgram( gl );
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			try
			{
				var order = GpuParticleSort.SortIndices( gl, prog, stateBuf, states.Length, cam, fwd );
				if ( gl.GetError() != GLEnum.NoError ) throw new Exception( "gpu-sort: GL error after SortIndices" );
				return order;
			}
			finally
			{
				GpuParticleSystem.Dispose( gl, stateBuf, prog );
			}
		}

		registry.Add( "gpu-sort", "keys follow cpu depth order", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			// Axis-aligned camera so GPU/CPU depth bits agree exactly.
			var cam = Vector3.Zero; var fwd = new Vector3( 0, 0, 1 );
			var states = new[] { Make( new Vector3( 0, 0, 1 ) ), Make( new Vector3( 0, 0, 5 ) ), Make( new Vector3( 0, 0, 10 ) ) };
			uint prog = GpuParticleSort.CreateProgram( gl );
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			var (keys, indices) = GpuParticleSort.CreateBuffers( gl, 4 );
			try
			{
				GpuParticleSort.ComputeKeys( gl, prog, stateBuf, keys, indices, states.Length, cam, fwd );
				var gotKeys = GpuParticleSort.DownloadKeys( gl, keys, 4 );
				for ( int i = 0; i < 3; i++ )
					Check( gotKeys[i] == GpuParticleSort.DepthKey( states[i].Position, cam, fwd ), $"key[{i}] {gotKeys[i]} != cpu {GpuParticleSort.DepthKey( states[i].Position, cam, fwd )}" );
				Check( gotKeys[3] == GpuParticleSort.DeadKey && GpuParticleSort.DownloadIndices( gl, indices, 4 ).Length == 3, "padding slot must be MaxKey/-1 and stripped" );
				GpuParticleSort.SortBuffers( gl, prog, keys, indices, 4 );
				CheckOrder( GpuParticleSort.DownloadIndices( gl, indices, 4 ), new[] { 2, 1, 0 }, "axis order" );
				var sortedKeys = GpuParticleSort.DownloadKeys( gl, keys, 4 );
				Check( sortedKeys[0] <= sortedKeys[1] && sortedKeys[1] <= sortedKeys[2], "sorted keys must be ascending" );
				if ( gl.GetError() != GLEnum.NoError ) throw new Exception( "gpu-sort: GL error in keys test" );
			}
			finally { GpuParticleSort.Dispose( gl, keys, indices, prog ); GpuParticleSystem.Dispose( gl, stateBuf, 0 ); }
		} );

		registry.Add( "gpu-sort", "shuffled input sorts back-to-front", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			// Deterministic LCG positions (no System.Random in test data).
			uint s = 0x12345678u;
			var states = new ParticleState[37];
			for ( int i = 0; i < states.Length; i++ )
			{
				float Next() { s = s * 1664525u + 1013904223u; return ( s >> 8 ) % 20000 / 100f - 100f; }
				states[i] = Make( new Vector3( Next(), Next(), Next() ) );
			}
			var cam = new Vector3( 10, -20, 30 ); var fwd = Vector3.Normalize( new Vector3( 1, 2, 3 ) );
			CheckOrder( GpuSort( gl, states, cam, fwd ), ReferenceOrder( states, cam, fwd ), "shuffled 37" );
		} );

		registry.Add( "gpu-sort", "ties break by lower index", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			var cam = Vector3.Zero; var fwd = new Vector3( 0, 0, 1 );
			var states = new[]
			{
				Make( new Vector3( 5, 5, 5 ) ), Make( new Vector3( -1, 0, 0 ) ), Make( new Vector3( 5, 5, 5 ) ),
				Make( new Vector3( 9, 9, 9 ) ), Make( new Vector3( -1, 0, 0 ) ), Make( new Vector3( 5, 5, 5 ) ),
			};
			// Depths 5,0,5,9,0,5 -> back-to-front: 9 first, then the 5s in slot order, then the 0s in slot order.
			CheckOrder( GpuSort( gl, states, cam, fwd ), new[] { 3, 0, 2, 5, 1, 4 }, "ties" );
			CheckOrder( ReferenceOrder( states, cam, fwd ), new[] { 3, 0, 2, 5, 1, 4 }, "reference ties" );
		} );

		registry.Add( "gpu-sort", "dead slots sort last", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			var cam = Vector3.Zero; var fwd = new Vector3( 0, 0, 1 );
			var states = new[]
			{
				Make( new Vector3( 0, 0, 2 ) ), Make( new Vector3( 0, 0, 7 ), alive: 0 ), Make( new Vector3( 0, 0, 9 ) ),
				Make( new Vector3( 0, 0, 1 ), alive: 0 ), Make( new Vector3( 0, 0, 4 ) ), Make( new Vector3( 0, 0, 3 ), alive: 0 ),
			};
			var order = GpuSort( gl, states, cam, fwd );
			CheckOrder( order, new[] { 2, 4, 0, 1, 3, 5 }, "dead last in slot order" );
			Check( order.Length == states.Length, "no sentinels in output" );
		} );

		registry.Add( "gpu-sort", "non-power-of-two counts sort correctly", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			var cam = new Vector3( 3, -1, 2 ); var fwd = Vector3.Normalize( new Vector3( 0, 1, 2 ) );
			foreach ( int count in new[] { 1, 2, 3, 5, 17, 100, 1000 } )
			{
				uint s = 0xABCDEF01u + (uint)count;
				var states = new ParticleState[count];
				for ( int i = 0; i < count; i++ )
				{
					float Next() { s = s * 1664525u + 1013904223u; return ( s >> 8 ) % 20000 / 100f - 100f; }
					states[i] = Make( new Vector3( Next(), Next(), Next() ) );
				}
				CheckOrder( GpuSort( gl, states, cam, fwd ), ReferenceOrder( states, cam, fwd ), $"padding count {count}" );
			}
		} );

		registry.Add( "gpu-sort", "pure sort on hand-built keys", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSort.CreateProgram( gl );
			uint keys = GpuParticleSort.UploadKeys( gl, new uint[] { 5, 1, 4, 1, 3, 0, 0, 0 } );
			uint indices = GpuParticleSort.UploadIndices( gl, new int[] { 0, 1, 2, 3, 4, 5, 6, 7 } );
			try
			{
				GpuParticleSort.SortBuffers( gl, prog, keys, indices, 8 );
				CheckOrder( GpuParticleSort.DownloadIndices( gl, indices, 8 ), new[] { 5, 6, 7, 1, 3, 4, 2, 0 }, "hand-built keys" );
				if ( gl.GetError() != GLEnum.NoError ) throw new Exception( "gpu-sort: GL error in pure sort" );
			}
			finally { GpuParticleSort.Dispose( gl, keys, indices, prog ); }
		} );

		registry.Add( "gpu-sort", "empty sort returns empty without gl error", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSort.CreateProgram( gl );
			uint stateBuf = GpuParticleSystem.UploadStates( gl, Array.Empty<ParticleState>() );
			try
			{
				var order = GpuParticleSort.SortIndices( gl, prog, stateBuf, 0, Vector3.Zero, Vector3.UnitZ );
				Check( order.Length == 0, "empty sort must return empty" );
				if ( gl.GetError() != GLEnum.NoError ) throw new Exception( "gpu-sort: GL error on empty sort" );
			}
			finally { GpuParticleSort.Dispose( gl, 0, 0, prog ); GpuParticleSystem.Dispose( gl, stateBuf, 0 ); }
		} );

		registry.Add( "gpu-sort", "invalid camera throws before gl use", () =>
		{
			// gl is null on purpose: if validation ran after any GL call this would throw NullReferenceException instead.
			try { GpuParticleSort.SortIndices( null!, 0, 0, 4, Vector3.Zero, Vector3.Zero ); throw new Exception( "zero forward did not throw" ); }
			catch ( ArgumentException ) { }
			try { GpuParticleSort.SortIndices( null!, 0, 0, 4, new Vector3( float.NaN, 0, 0 ), Vector3.UnitZ ); throw new Exception( "NaN camera did not throw" ); }
			catch ( ArgumentException ) { }
		} );

		registry.Add( "gpu-sort", "repeated sort is deterministic", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			var cam = new Vector3( 1, 2, 3 ); var fwd = Vector3.Normalize( new Vector3( -2, 1, 0 ) );
			uint s = 0x77777777u;
			var states = new ParticleState[64];
			for ( int i = 0; i < states.Length; i++ )
			{
				float Next() { s = s * 1664525u + 1013904223u; return ( s >> 8 ) % 20000 / 100f - 100f; }
				states[i] = Make( new Vector3( Next(), Next(), Next() ), alive: i % 5 == 0 ? 0 : 1 );
			}
			var first = GpuSort( gl, states, cam, fwd );
			var second = GpuSort( gl, states, cam, fwd );
			CheckOrder( first, second, "repeated sort" );
		} );

		registry.Add( "gpu-sort", "large count sorts", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			var cam = new Vector3( -5, 8, 0 ); var fwd = Vector3.Normalize( new Vector3( 2, -1, 4 ) );
			uint s = 0x0BADF00Du;
			var states = new ParticleState[4096];
			for ( int i = 0; i < states.Length; i++ )
			{
				float Next() { s = s * 1664525u + 1013904223u; return ( s >> 8 ) % 20000 / 100f - 100f; }
				states[i] = Make( new Vector3( Next(), Next(), Next() ) );
			}
			CheckOrder( GpuSort( gl, states, cam, fwd ), ReferenceOrder( states, cam, fwd ), "4096" );
		} );
	}
}
