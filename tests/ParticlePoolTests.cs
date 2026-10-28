using System.Numerics;
using Marionette.Desktop;

internal static class ParticlePoolTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Eq<T>( T got, T want, string what ) where T : IEquatable<T>
		{
			if ( !got.Equals( want ) ) throw new Exception( $"{what}: got {got}, want {want}" );
		}
		void Throws<T>( Action body, string what ) where T : Exception
		{
			try { body(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, none thrown" );
		}
		void LiveIs( ParticlePool pool, params int[] want )
		{
			var got = pool.IterateLive();
			Check( got.Count == want.Length, $"IterateLive count {got.Count}, want {want.Length} (got [{string.Join( ",", got )}])" );
			for ( int i = 0; i < want.Length; i++ )
				Check( got[i] == want[i], $"IterateLive[{i}] = {got[i]}, want {want[i]} (got [{string.Join( ",", got )}])" );
			for ( int i = 0; i < want.Length; i++ )
				Check( pool.LiveSlotAt( i ) == want[i], $"LiveSlotAt({i}) = {pool.LiveSlotAt( i )}, want {want[i]}" );
			Eq( pool.LiveCount(), want.Length, "LiveCount" );
		}

		registry.Add( "particle-pool", "fresh pool allocates slots in order", () =>
		{
			var pool = new ParticlePool( 5 );
			Eq( pool.Allocate(), 0, "first Allocate" );
			Eq( pool.Allocate(), 1, "second Allocate" );
			Eq( pool.Allocate(), 2, "third Allocate" );
			LiveIs( pool, 0, 1, 2 );
			Eq( pool.FreeCount, 2, "FreeCount after 3 of 5" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "freed slots reused before virgin slots, LIFO", () =>
		{
			var pool = new ParticlePool( 5 );
			for ( int i = 0; i < 5; i++ ) Eq( pool.Allocate(), i, $"fill slot {i}" );
			pool.Free( 2 );
			Eq( pool.Allocate(), 2, "freed slot 2 reused before any virgin slot (none left, but reuse is exact)" );
			pool.Free( 4 );
			pool.Free( 1 );
			Eq( pool.Allocate(), 1, "most recently freed slot reused first (LIFO)" );
			Eq( pool.Allocate(), 4, "next freed slot reused second" );
			LiveIs( pool, 0, 1, 2, 3, 4 );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "full capacity returns -1 and overwrites nothing", () =>
		{
			var pool = new ParticlePool( 3 );
			int a = pool.Allocate(), b = pool.Allocate(), c = pool.Allocate();
			pool.SetState( a, new ParticleState { Position = new Vector3( 1, 2, 3 ), Size = 7 } );
			pool.SetState( b, new ParticleState { Position = new Vector3( 4, 5, 6 ), Size = 8 } );
			pool.SetState( c, new ParticleState { Position = new Vector3( 7, 8, 9 ), Size = 9 } );
			Eq( pool.Allocate(), -1, "Allocate at full capacity" );
			Eq( pool.Allocate(), ParticlePool.Full, "Allocate at full capacity is the documented Full value" );
			var failed = pool.AllocateHandle();
			Check( failed.Slot == -1, $"AllocateHandle at full capacity must have Slot -1, got {failed.Slot}" );
			Check( !pool.IsValid( failed ), "full-capacity failure handle must never be valid" );
			Eq( pool.LiveCount(), 3, "LiveCount unchanged by failed allocates" );
			Eq( pool.GetState( a ).Position, new Vector3( 1, 2, 3 ), "live slot A untouched by failed allocate" );
			Eq( pool.GetState( b ).Size, 8f, "live slot B untouched by failed allocate" );
			Eq( pool.GetState( c ).Position, new Vector3( 7, 8, 9 ), "live slot C untouched by failed allocate" );
			LiveIs( pool, 0, 1, 2 );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "free at full capacity makes room again", () =>
		{
			var pool = new ParticlePool( 2 );
			pool.Allocate(); pool.Allocate();
			Eq( pool.Allocate(), -1, "full before free" );
			pool.Free( 0 );
			Eq( pool.Allocate(), 0, "freed slot 0 reused after full" );
			Eq( pool.Allocate(), -1, "full again" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "IterateLive contents after known operations", () =>
		{
			var pool = new ParticlePool( 5 );
			for ( int i = 0; i < 5; i++ ) pool.Allocate();
			pool.Free( 1 );
			pool.Free( 3 );
			LiveIs( pool, 0, 2, 4 );
			pool.Free( 0 ); pool.Free( 2 ); pool.Free( 4 );
			LiveIs( pool );
			Eq( pool.Allocate(), 4, "LIFO reuse: last freed slot first" );
			LiveIs( pool, 4 );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "IterateLive snapshot is safe across later frees", () =>
		{
			var pool = new ParticlePool( 4 );
			pool.Allocate(); pool.Allocate(); pool.Allocate();
			var snap = pool.IterateLive();
			pool.Free( 1 );
			pool.Allocate();
			Check( snap.Count == 3 && snap[0] == 0 && snap[1] == 1 && snap[2] == 2,
				$"snapshot must stay [0,1,2], got [{string.Join( ",", snap )}]" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "generation guard detects stale handles", () =>
		{
			var pool = new ParticlePool( 1 );
			var h0 = pool.AllocateHandle();
			Eq( h0.Slot, 0, "handle slot" );
			Eq( h0.Generation, 0, "first-use generation is 0" );
			Check( pool.IsValid( h0 ), "fresh handle is valid" );
			pool.Free( h0 );
			Check( !pool.IsValid( h0 ), "handle is invalid once its slot is freed" );
			var h1 = pool.AllocateHandle();
			Eq( h1.Slot, 0, "slot reused" );
			Eq( h1.Generation, 1, "generation incremented on reuse" );
			Check( pool.IsValid( h1 ), "new handle is valid" );
			Check( !pool.IsValid( h0 ), "old handle stays invalid after reuse" );
			Throws<InvalidOperationException>( () => pool.Free( h0 ), "freeing a stale handle" );
			Eq( pool.LiveCount(), 1, "failed stale free changed nothing" );
			pool.Free( h1 );
			Eq( pool.LiveCount(), 0, "freeing the current handle works" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "stale handle in a multi-slot pool", () =>
		{
			var pool = new ParticlePool( 4 );
			pool.Allocate(); // 0
			var old = pool.AllocateHandle(); // 1, gen 0
			pool.Allocate(); // 2
			pool.Free( 1 );
			var fresh = pool.AllocateHandle(); // 1 again, gen 1
			Eq( fresh.Slot, 1, "freed slot reused" );
			Eq( fresh.Generation, 1, "generation bumped on reuse" );
			Check( !pool.IsValid( old ), "old handle for slot 1 is stale" );
			Check( pool.IsValid( fresh ), "fresh handle for slot 1 is valid" );
			LiveIs( pool, 0, 1, 2 );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "double-free fails loudly and changes nothing", () =>
		{
			var pool = new ParticlePool( 3 );
			pool.Allocate(); pool.Allocate();
			pool.Free( 0 );
			Throws<InvalidOperationException>( () => pool.Free( 0 ), "double-free" );
			Eq( pool.LiveCount(), 1, "LiveCount unchanged by double-free attempt" );
			LiveIs( pool, 1 );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "free out of range fails loudly", () =>
		{
			var pool = new ParticlePool( 3 );
			pool.Allocate();
			Throws<ArgumentOutOfRangeException>( () => pool.Free( -1 ), "Free(-1)" );
			Throws<ArgumentOutOfRangeException>( () => pool.Free( 3 ), "Free(capacity)" );
			Throws<ArgumentOutOfRangeException>( () => pool.Free( 99 ), "Free(99)" );
			Eq( pool.LiveCount(), 1, "LiveCount unchanged by out-of-range frees" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "Validate rejects non-positive and over-max capacities", () =>
		{
			Throws<ArgumentOutOfRangeException>( () => new ParticlePool( 0 ), "capacity 0" );
			Throws<ArgumentOutOfRangeException>( () => new ParticlePool( -5 ), "capacity -5" );
			Throws<ArgumentOutOfRangeException>( () => new ParticlePool( ParticlePool.MaxCapacity + 1 ), "capacity over max" );
			var ok = new ParticlePool( 1 );
			ok.Validate();
			var big = new ParticlePool( ParticlePool.MaxCapacity );
			Eq( big.Capacity, ParticlePool.MaxCapacity, "max capacity accepted" );
			Eq( big.LiveCount(), 0, "max pool starts empty" );
		} );

		registry.Add( "particle-pool", "Validate passes after a mixed allocation history", () =>
		{
			var pool = new ParticlePool( 8 );
			for ( int i = 0; i < 8; i++ ) pool.Allocate();
			pool.Free( 7 ); pool.Free( 0 ); pool.Free( 3 );
			pool.Allocate(); pool.Allocate();
			pool.Free( 5 );
			pool.Validate();
			LiveIs( pool, 0, 1, 2, 3, 4, 6 );
		} );

		registry.Add( "particle-pool", "state round-trips and free zeroes the slot", () =>
		{
			var pool = new ParticlePool( 2 );
			int slot = pool.Allocate();
			var state = new ParticleState
			{
				Position = new Vector3( 10, 20, 30 ), Age = 1.5f, Velocity = new Vector3( 0, 0, -9 ),
				Rotation = Quaternion.CreateFromYawPitchRoll( 0.3f, 0.2f, 0.1f ),
				AngularVelocity = new Vector3( 1, 2, 3 ), Size = 4.5f,
			};
			pool.SetState( slot, state );
			var back = pool.GetState( slot );
			Eq( back.Position, state.Position, "state position round-trip" );
			Eq( back.Age, 1.5f, "state age round-trip" );
			Eq( back.Size, 4.5f, "state size round-trip" );
			Eq( back.Alive, 1f, "live state Alive is 1" );
			pool.Free( slot );
			Throws<InvalidOperationException>( () => pool.GetState( slot ), "GetState on a free slot" );
			Throws<InvalidOperationException>( () => pool.SetState( slot, state ), "SetState on a free slot" );
			int reused = pool.Allocate();
			Eq( reused, slot, "same slot reused" );
			var clean = pool.GetState( reused );
			Eq( clean.Position, Vector3.Zero, "reused slot position zeroed" );
			Eq( clean.Size, 0f, "reused slot size zeroed" );
			Eq( clean.Rotation, Quaternion.Identity, "reused slot rotation reset to Identity" );
			Eq( clean.Alive, 1f, "reused slot Alive is 1" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "state layout matches ParticleState 64-byte contract", () =>
		{
			Eq( ParticleState.SizeInBytes, 64, "ParticleState.SizeInBytes" );
			var pool = new ParticlePool( 2 );
			int slot = pool.Allocate();
			Check( pool.GetState( slot ).Rotation == Quaternion.Identity, "fresh slot rotation is Identity (ParticleSystem ctor convention)" );
			Eq( pool.GetState( slot ).Alive, 1f, "fresh live slot Alive is 1" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "clear resets pool to a fresh state", () =>
		{
			var pool = new ParticlePool( 4 );
			int a = pool.Allocate();
			pool.SetState( a, new ParticleState { Position = new Vector3( 9, 9, 9 ), Size = 3 } );
			pool.Allocate(); pool.Allocate();
			var stale = pool.AllocateHandle();
			pool.Clear();
			Eq( pool.LiveCount(), 0, "LiveCount after Clear" );
			Eq( pool.Count, 0, "Count after Clear" );
			Eq( pool.FreeCount, 4, "FreeCount after Clear is full capacity" );
			for ( int i = 0; i < 4; i++ ) Check( !pool.IsLive( i ), $"slot {i} must not read live after Clear" );
			Throws<InvalidOperationException>( () => pool.GetState( a ), "GetState on a cleared slot" );
			Check( !pool.IsValid( stale ), "pre-Clear handle stays invalid after Clear" );
			pool.Validate();
			Eq( pool.Allocate(), 0, "first Allocate after Clear" );
			Eq( pool.Allocate(), 1, "second Allocate after Clear" );
			Eq( pool.Allocate(), 2, "third Allocate after Clear" );
			Eq( pool.Allocate(), 3, "fourth Allocate after Clear" );
			Eq( pool.Allocate(), -1, "full again after Clear + refill" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "acquire-all release-all acquire-all again loses no slots", () =>
		{
			var pool = new ParticlePool( 4 );
			for ( int i = 0; i < 4; i++ ) Eq( pool.Allocate(), i, $"first fill slot {i}" );
			pool.Free( 3 ); pool.Free( 2 ); pool.Free( 1 ); pool.Free( 0 );
			Eq( pool.LiveCount(), 0, "LiveCount after release-all" );
			Eq( pool.FreeCount, 4, "FreeCount after release-all" );
			for ( int i = 0; i < 4; i++ ) Eq( pool.Allocate(), i, $"refill slot {i} identical after reverse-order release" );
			pool.Free( 1 ); pool.Free( 3 ); pool.Free( 0 ); pool.Free( 2 );
			Eq( pool.Allocate(), 2, "arbitrary-order release: LIFO re-acquire first is last freed" );
			Eq( pool.Allocate(), 0, "arbitrary-order release: LIFO re-acquire second" );
			Eq( pool.Allocate(), 3, "arbitrary-order release: LIFO re-acquire third" );
			Eq( pool.Allocate(), 1, "arbitrary-order release: LIFO re-acquire fourth (same set, no leak)" );
			Eq( pool.Allocate(), -1, "full again after arbitrary-order refill" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "backing capacity never grows across full cycles", () =>
		{
			var pool = new ParticlePool( 6 );
			Eq( pool.Capacity, 6, "Capacity at construction (backing state array length)" );
			for ( int cycle = 0; cycle < 3; cycle++ )
			{
				for ( int i = 0; i < 6; i++ ) pool.Allocate();
				for ( int i = 0; i < 6; i++ ) pool.Free( i );
			}
			Eq( pool.Capacity, 6, "Capacity unchanged after 3 full acquire/release cycles" );
			Eq( pool.LiveCount(), 0, "LiveCount after cycles" );
			Eq( pool.FreeCount, 6, "FreeCount after cycles" );
			pool.Validate();
		} );

		registry.Add( "particle-pool", "same script same indices on two pools", () =>
		{
			int[] Script( ParticlePool pool )
			{
				var seq = new System.Collections.Generic.List<int>();
				seq.Add( pool.Allocate() ); seq.Add( pool.Allocate() ); seq.Add( pool.Allocate() );
				pool.Free( 1 );
				seq.Add( pool.Allocate() );
				pool.Free( 0 ); pool.Free( 2 );
				seq.Add( pool.Allocate() ); seq.Add( pool.Allocate() );
				return seq.ToArray();
			}
			var x = Script( new ParticlePool( 5 ) );
			var y = Script( new ParticlePool( 5 ) );
			Eq( x.Length, y.Length, "script length" );
			for ( int i = 0; i < x.Length; i++ ) Eq( x[i], y[i], $"script step {i}" );
			Eq( x[3], 1, "script step 3 is the LIFO reuse of freed slot 1" );
			Eq( x[4], 2, "script step 4 is the LIFO reuse of last-freed slot 2" );
		} );

		registry.Add( "particle-pool", "acquire release count aliases match allocate free livecount", () =>
		{
			var pool = new ParticlePool( 3 );
			Eq( pool.Acquire(), 0, "Acquire first" );
			Eq( pool.Acquire(), 1, "Acquire second" );
			Eq( pool.Count, 2, "Count after two Acquires" );
			pool.Release( 0 );
			Eq( pool.Count, 1, "Count after Release" );
			Eq( pool.Acquire(), 0, "Acquire reuses released slot" );
			var h = pool.AllocateHandle();
			Eq( h.Slot, 2, "handle slot" );
			pool.Release( h );
			Eq( pool.Count, 2, "Count after handle Release" );
			Throws<InvalidOperationException>( () => pool.Release( h ), "double Release by stale handle" );
			pool.Validate();
		} );
	}
}
