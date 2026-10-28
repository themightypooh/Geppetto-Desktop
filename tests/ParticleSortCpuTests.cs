using System;
using System.Numerics;
using Marionette.Desktop;

// CPU sort reference tests (wave-6 lane P60). Every order below is a
// known, hand-computed order — no test "discovers" its expectation from
// the code under test. Distances are Euclidean, farthest first, ties to
// the lower index. Units: kernel inches, Z-up, as in ParticleSystem.
internal static class ParticleSortCpuTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Eq( int[] got, int[] want, string what )
		{
			Check( got.Length == want.Length, $"{what}: length {got.Length}, want {want.Length}." );
			for ( int i = 0; i < want.Length; i++ )
				Check( got[i] == want[i], $"{what}: position {i} is {got[i]}, want {want[i]} (got [{string.Join( ",", got )}], want [{string.Join( ",", want )}])." );
		}
		void Throws<T>( Action body, string what ) where T : Exception
		{
			try { body(); }
			catch ( T ) { return; }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, no throw." );
		}

		registry.Add( "particle-sort-cpu", "farthest-first from camera at origin", () =>
		{
			var pos = new[] { new Vector3( 0, 0, 10 ), new Vector3( 0, 0, 30 ), new Vector3( 0, 0, 20 ) };
			Eq( ParticleSortCpu.SortByDistance( pos, Vector3.Zero ), new[] { 1, 2, 0 }, "origin camera" );
		} );

		registry.Add( "particle-sort-cpu", "farthest-first from an offset camera", () =>
		{
			// Camera at Z=100: distances 90, 70, 80 -> order 0, 2, 1.
			var pos = new[] { new Vector3( 0, 0, 10 ), new Vector3( 0, 0, 30 ), new Vector3( 0, 0, 20 ) };
			Eq( ParticleSortCpu.SortByDistance( pos, new Vector3( 0, 0, 100 ) ), new[] { 0, 2, 1 }, "offset camera" );
		} );

		registry.Add( "particle-sort-cpu", "ties break to the lower index", () =>
		{
			// All three are exactly distance 10 from the origin.
			var pos = new[] { new Vector3( 10, 0, 0 ), new Vector3( 0, 10, 0 ), new Vector3( 0, 0, 10 ) };
			Eq( ParticleSortCpu.SortByDistance( pos, Vector3.Zero ), new[] { 0, 1, 2 }, "all tied" );
			// Mixed: distances 30, 10, 10 -> farthest first, then tie 1 before 2.
			var mixed = new[] { new Vector3( 0, 0, 30 ), new Vector3( 0, 0, 10 ), new Vector3( 0, 10, 0 ) };
			Eq( ParticleSortCpu.SortByDistance( mixed, Vector3.Zero ), new[] { 0, 1, 2 }, "mixed tie" );
		} );

		registry.Add( "particle-sort-cpu", "3-4-5 triangle ties across axes (Z-up Euclidean)", () =>
		{
			// (3,4,0) and (0,0,5) are both distance 5; (0,0,6) is farther.
			var pos = new[] { new Vector3( 3, 4, 0 ), new Vector3( 0, 0, 5 ), new Vector3( 0, 0, 6 ) };
			Eq( ParticleSortCpu.SortByDistance( pos, Vector3.Zero ), new[] { 2, 0, 1 }, "3-4-5 vs Z" );
		} );

		registry.Add( "particle-sort-cpu", "single particle and particle at the camera", () =>
		{
			Eq( ParticleSortCpu.SortByDistance( new[] { new Vector3( 1, 2, 3 ) }, Vector3.Zero ), new[] { 0 }, "single" );
			// Distance 0 sorts last: behind nothing, drawn first in a
			// back-to-front pass it is the nearest, so it comes last.
			var pos = new[] { new Vector3( 0, 0, 0 ), new Vector3( 0, 0, 50 ) };
			Eq( ParticleSortCpu.SortByDistance( pos, Vector3.Zero ), new[] { 1, 0 }, "at camera" );
		} );

		registry.Add( "particle-sort-cpu", "negative coordinates and non-origin camera", () =>
		{
			// Camera (-10,0,0): distances 10, 30, 20 -> order 1, 2, 0.
			var pos = new[] { new Vector3( 0, 0, 0 ), new Vector3( -40, 0, 0 ), new Vector3( -30, 0, 0 ) };
			Eq( ParticleSortCpu.SortByDistance( pos, new Vector3( -10, 0, 0 ) ), new[] { 1, 2, 0 }, "negative axis" );
		} );

		registry.Add( "particle-sort-cpu", "repeated sorts are identical and input is not mutated", () =>
		{
			var pos = new[]
			{
				new Vector3( 0, 0, 5 ), new Vector3( 0, 0, 50 ), new Vector3( 0, 0, 5 ),
				new Vector3( 10, 0, 0 ), new Vector3( 0, 0, 25 ),
			};
			var snapshot = (Vector3[])pos.Clone();
			var first = ParticleSortCpu.SortByDistance( pos, Vector3.Zero );
			Eq( first, new[] { 1, 4, 3, 0, 2 }, "first sort" );
			for ( int r = 0; r < 3; r++ )
				Eq( ParticleSortCpu.SortByDistance( pos, Vector3.Zero ), first, $"repeat {r}" );
			for ( int i = 0; i < pos.Length; i++ )
				Check( pos[i] == snapshot[i], $"input mutated at {i}." );
		} );

		registry.Add( "particle-sort-cpu", "1000-particle deterministic set is monotone and a permutation", () =>
		{
			// Fixed LCG, no System.Random (house rule from ParticleSystem).
			uint s = 0x12345678u;
			uint Next() { s = s * 1664525u + 1013904223u; return s >> 8; }
			var pos = new Vector3[1000];
			for ( int i = 0; i < pos.Length; i++ )
				pos[i] = new Vector3( ( Next() % 2000 ) - 1000, ( Next() % 2000 ) - 1000, ( Next() % 2000 ) - 1000 );
			var cam = new Vector3( 100, -200, 50 );
			var order = ParticleSortCpu.SortByDistance( pos, cam );
			Check( order.Length == 1000, "length 1000." );
			var seen = new bool[1000];
			float prev = float.PositiveInfinity;
			for ( int i = 0; i < order.Length; i++ )
			{
				Check( order[i] >= 0 && order[i] < 1000, $"index {order[i]} out of range." );
				Check( !seen[order[i]], $"slot {order[i]} twice." );
				seen[order[i]] = true;
				float d = Vector3.Distance( pos[order[i]], cam );
				Check( d <= prev, $"not monotone at position {i}: {d} after {prev}." );
				prev = d;
			}
			Eq( ParticleSortCpu.SortByDistance( pos, cam ), order, "repeat of 1000" );
		} );

		registry.Add( "particle-sort-cpu", "CompareToGpu exact match", () =>
		{
			var c = ParticleSortCpu.CompareToGpu( new[] { 2, 1, 0 }, new[] { 2, 1, 0 } );
			Check( c.ExactMatch && c.FirstMismatchIndex == -1 && c.MismatchCount == 0 && c.Problem is null,
				$"exact reported as {c}." );
			var empty = ParticleSortCpu.CompareToGpu( Array.Empty<int>(), Array.Empty<int>() );
			Check( empty.ExactMatch && empty.MismatchCount == 0, $"empty-vs-empty reported as {empty}." );
		} );

		registry.Add( "particle-sort-cpu", "CompareToGpu mismatch diagnostics", () =>
		{
			// Swap at positions 1,2: first differing position 1, count 2.
			var c = ParticleSortCpu.CompareToGpu( new[] { 2, 1, 0 }, new[] { 2, 0, 1 } );
			Check( !c.ExactMatch && c.FirstMismatchIndex == 1 && c.MismatchCount == 2 && c.Problem is null,
				$"swap reported as {c}." );
			// Swap at position 0 only (a 2-cycle at the front).
			var d = ParticleSortCpu.CompareToGpu( new[] { 0, 1, 2, 3 }, new[] { 1, 0, 2, 3 } );
			Check( !d.ExactMatch && d.FirstMismatchIndex == 0 && d.MismatchCount == 2,
				$"front swap reported as {d}." );
			// Fully reversed 4-cycle: every position differs.
			var e = ParticleSortCpu.CompareToGpu( new[] { 0, 1, 2, 3 }, new[] { 3, 2, 1, 0 } );
			Check( !e.ExactMatch && e.FirstMismatchIndex == 0 && e.MismatchCount == 4,
				$"reversal reported as {e}." );
		} );

		registry.Add( "particle-sort-cpu", "CompareToGpu broken GPU orders are diagnosed, not thrown", () =>
		{
			var dup = ParticleSortCpu.CompareToGpu( new[] { 0, 1, 2 }, new[] { 1, 1, 2 } );
			Check( !dup.ExactMatch && dup.Problem is not null && dup.Problem.Contains( "more than once" ),
				$"duplicate reported as {dup}." );
			var range = ParticleSortCpu.CompareToGpu( new[] { 0, 1, 2 }, new[] { 0, 1, 7 } );
			Check( !range.ExactMatch && range.Problem is not null && range.Problem.Contains( "out of range" ),
				$"out-of-range reported as {range}." );
			var len = ParticleSortCpu.CompareToGpu( new[] { 0, 1, 2 }, new[] { 0, 1 } );
			Check( !len.ExactMatch && len.FirstMismatchIndex == 2 && len.MismatchCount == 1 && len.Problem is not null,
				$"short GPU order reported as {len}." );
			Throws<ArgumentNullException>( () => ParticleSortCpu.CompareToGpu( null!, new[] { 0 } ), "null CPU order" );
			Throws<ArgumentNullException>( () => ParticleSortCpu.CompareToGpu( new[] { 0 }, null! ), "null GPU order" );
		} );

		registry.Add( "particle-sort-cpu", "Validate loudly rejects empty, NaN, and infinite input", () =>
		{
			Throws<ArgumentException>( () => ParticleSortCpu.Validate( Array.Empty<Vector3>(), Vector3.Zero ), "empty list" );
			Throws<ArgumentException>( () => ParticleSortCpu.Validate( new[] { new Vector3( float.NaN, 0, 0 ) }, Vector3.Zero ), "NaN position" );
			Throws<ArgumentException>( () => ParticleSortCpu.Validate( new[] { new Vector3( 0, 0, float.PositiveInfinity ) }, Vector3.Zero ), "infinite position" );
			Throws<ArgumentException>( () => ParticleSortCpu.Validate( new[] { Vector3.Zero }, new Vector3( float.NaN, 0, 0 ) ), "NaN camera" );
			Throws<ArgumentNullException>( () => ParticleSortCpu.Validate( null!, Vector3.Zero ), "null list" );
			// SortByDistance itself is loud too, not just Validate().
			Throws<ArgumentException>( () => ParticleSortCpu.SortByDistance( Array.Empty<Vector3>(), Vector3.Zero ), "sort of empty" );
			Throws<ArgumentException>( () => ParticleSortCpu.SortByDistance( new[] { new Vector3( float.NaN, 0, 0 ) }, Vector3.Zero ), "sort of NaN" );
			// And a finite list validates cleanly.
			ParticleSortCpu.Validate( new[] { new Vector3( 1, 2, 3 ), new Vector3( -4, 0, 9 ) }, Vector3.Zero );
		} );
	}
}
