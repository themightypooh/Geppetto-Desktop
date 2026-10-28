using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU particle sort tests (particles lane 8, group "gpu-sort").
/// Order is compared exactly (indices are integers, ties resolve identically
/// on both sides); depths are compared only via order, never bit-for-bit,
/// per the lane agreement rule. Headless EGL only.
/// Large-count tests (1000, 4096) use the near-tie rule instead: the GPU order
/// must be sorted by its own keys and match the CPU order up to adjacent swaps
/// within 16 key units, because GPU/CPU dot rounding differs by a few ulps
/// (lane 8-FIX measurement, 2026-10-24).
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

		// Near-tie agreement rule (lane 8-FIX, measured 2026-10-24): the GPU sort is
		// exact w.r.t. its own keys, but the GPU dot product rounds differently from
		// the CPU dot (FMA/reassociation), so two particles whose CPU depths agree
		// to within a few ulps can swap. Measured: at 1000 points one adjacent swap
		// (CPU depths 2.8576918 vs 2.8576927, 4 ulps apart); at 4096 points nine
		// adjacent swaps, max CPU-key inversion gap 4. The GPU order was sorted by
		// its own keys in both cases (0 key violations), so the sort is correct and
		// the reference comparison must tolerate near-tie swaps. Bound 16 key units
		// = 4x the measured maximum. Any inversion larger than that, or any order
		// that is not a permutation, still fails loudly.
		static (int[] order, uint[] sortedKeys) GpuSortWithKeys( GL gl, ParticleState[] states, Vector3 cam, Vector3 fwd )
		{
			uint prog = GpuParticleSort.CreateProgram( gl );
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			int padded = GpuParticleSort.PaddedCount( states.Length );
			var (keys, indices) = GpuParticleSort.CreateBuffers( gl, padded );
			try
			{
				GpuParticleSort.ComputeKeys( gl, prog, stateBuf, keys, indices, states.Length, cam, fwd );
				GpuParticleSort.SortBuffers( gl, prog, keys, indices, padded );
				var sortedKeys = GpuParticleSort.DownloadKeys( gl, keys, padded );
				var order = GpuParticleSort.DownloadIndices( gl, indices, padded );
				if ( gl.GetError() != GLEnum.NoError ) throw new Exception( "gpu-sort: GL error after SortIndices" );
				return (order, sortedKeys);
			}
			finally { GpuParticleSort.Dispose( gl, keys, indices, prog ); GpuParticleSystem.Dispose( gl, stateBuf, 0 ); }
		}

		static void CheckOrderNearTies( int[] actual, ParticleState[] states, Vector3 cam, Vector3 fwd, string msg )
		{
			// Permutation: same slots, each exactly once.
			var sorted = (int[])actual.Clone();
			Array.Sort( sorted );
			for ( int i = 0; i < sorted.Length; i++ )
				if ( sorted[i] != i ) throw new Exception( $"gpu-sort: {msg}: not a permutation at {i}: got {sorted[i]}" );
			// Adjacent inversions vs CPU keys must be near-ties only (<= 16 key units).
			for ( int i = 1; i < actual.Length; i++ )
			{
				uint prev = GpuParticleSort.DepthKey( states[actual[i - 1]].Position, cam, fwd );
				uint cur = GpuParticleSort.DepthKey( states[actual[i]].Position, cam, fwd );
				if ( cur < prev && prev - cur > 16 )
					throw new Exception( $"gpu-sort: {msg}: inversion at {i} key gap {prev - cur} > 16 (slots {actual[i - 1]} -> {actual[i]})" );
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
				if ( count == 1000 )
				{
					// Near-tie rule (see helper comment): exact order fails here on
					// float rounding alone, not on sort correctness.
					var (order, sortedKeys) = GpuSortWithKeys( gl, states, cam, fwd );
					for ( int i = 1; i < order.Length; i++ )
						Check( sortedKeys[i] >= sortedKeys[i - 1], $"padding count {count}: gpu keys not ascending at {i}" );
					CheckOrderNearTies( order, states, cam, fwd, $"padding count {count}" );
				}
				else CheckOrder( GpuSort( gl, states, cam, fwd ), ReferenceOrder( states, cam, fwd ), $"padding count {count}" );
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
			// Near-tie rule (see helper comment): exact order fails here on float
			// rounding alone, not on sort correctness.
			var (order4096, sortedKeys4096) = GpuSortWithKeys( gl, states, cam, fwd );
			for ( int i = 1; i < order4096.Length; i++ )
				Check( sortedKeys4096[i] >= sortedKeys4096[i - 1], $"4096: gpu keys not ascending at {i}" );
			CheckOrderNearTies( order4096, states, cam, fwd, "4096" );
		} );
	}
}
