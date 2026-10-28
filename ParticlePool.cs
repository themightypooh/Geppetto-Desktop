#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Marionette.Desktop;

/// <summary>
/// A pooled particle-state store with free-list reuse. Kernel inches, Z-up, seconds —
///
/// the same units as <see cref="ParticleSystem"/> — but this class owns NO simulation:
/// it only hands out slots, holds states, and takes slots back. The sim step stays in
/// <see cref="ParticleSystem"/>; a future sweep may point the sim's allocation at this
/// pool, but this file never wires itself in.
///
/// LAYOUT — how this maps to <see cref="ParticleState"/>:
/// The store is a flat <see cref="ParticleState"/> array, one entry per slot, so slot
/// <c>i</c> here IS slot <c>i</c> in <see cref="ParticleSystem"/> terms and the array
/// uploads unchanged wherever a <c>ParticleState</c> array uploads. <c>ParticleState</c>
/// is 64 bytes, <c>Pack = 4</c>, sequential: Position (Vector3, 12) + Age (float, 4) +
/// Velocity (Vector3, 12) + Alive (float, 4: 1 = live, 0 = free) + Rotation
/// (Quaternion, 16) + AngularVelocity (Vector3, 12) + Size (float, 4) = 64.
/// The pool's bookkeeping — the live flags, the generation counters, the free stack,
/// the sorted live list — lives in SEPARATE sidecar arrays and is never part of the
/// 64-byte GPU layout. The pool keeps the state's own Alive float in sync with its
/// live flag (1 on Allocate, 0 on Free) so a reader of the raw array alone can still
/// tell live slots from free ones.
///
/// FREE-LIST DISCIPLINE:
/// Allocation pops from a stack, mirroring <see cref="ParticleSystem"/>'s free stack,
/// which pops the lowest slot first: a fresh pool allocates 0, 1, 2, … in order.
/// Freeing pushes the slot back, so freed slots are reused (LIFO) before any slot
/// that has never been used. The pool NEVER grows past its capacity: Allocate at
/// full capacity returns -1 (and AllocateHandle returns a handle with Slot -1), never
/// an out-of-range index and never a silent overwrite of a live particle. Callers
/// that need a count of dropped spawns must count -1 returns themselves — the pool
/// keeps no drop counter, so it cannot disagree with a caller's.
///
/// GENERATION GUARD / HANDLE SHAPE:
/// A handle is <see cref="ParticleHandle"/>: a (Slot, Generation) pair. Each slot
/// carries a generation counter, starting at 0, incremented on every REUSE of the
/// slot (every Allocate after its first). A handle is valid only while its slot is
/// live AND its generation still matches the slot's current generation, so a handle
/// kept past a Free is dead twice over: the slot is not live, and once the slot is
/// reused the generation has moved on. Freeing by handle checks the generation and
/// throws on a stale handle instead of freeing whatever particle now owns the slot.
///
/// ITERATION:
/// <see cref="IterateLive"/> yields live slot indices in ascending slot order —
/// deterministic, independent of allocation/free history. It walks a dense,
/// slot-sorted live list, so the sim step never scans dead slots and never sees a
/// free slot's state. <see cref="LiveSlotAt"/> gives the same order by position for
/// callers that want zero allocation.
///
/// CLEAR — <see cref="Clear"/> frees every slot, zeroes every state (Rotation
/// Identity), and rebuilds the free stack to fresh-pool order, so the next
/// Allocate sequence is 0, 1, 2, … again. Generations and ever-allocated flags
/// are deliberately NOT reset, so a handle issued before Clear stays stale.
///
/// ALLOCATION — every backing array (_state, _live, _generation, _everAllocated,
/// _freeStack, and the live list, pre-sized to capacity) is allocated once in
/// the ctor. Allocate/Free allocate no arrays. The two documented exceptions
/// are <see cref="IterateLive"/>, which returns a snapshot copy per call, and
/// <see cref="Validate"/>, which allocates one bool array per call; neither is
/// on the spawn path.
///
/// DIVERGENCE FROM THE SIM — a fresh pool pops the lowest slot first, exactly
/// like ParticleSystem's ctor. After frees the two differ on purpose: the sim
/// re-sorts its free stack each step (SortFreeStackDescending) so its next pop
/// is again the lowest free slot, while this pool pops freed slots LIFO
/// (most-recently-freed first) and only then returns to virgin slots. A sweep
/// that wants bit-identical sim order must add the sort, not assume it.
///
/// 3D ONLY — this pool is typed to ParticleState, not generic: it does NOT
/// serve Particle2D (Particle2DState is a separate type). A 2D pool, if wanted,
/// is a separate generic-or-typed lane, not a claim made here.
///
/// ALIASES — Acquire/Release/Count are thin aliases for Allocate/Free/LiveCount,
/// provided so callers using the pooling-brief names need no adapter.
/// </summary>
public sealed class ParticlePool
{
	/// <summary>Maximum number of particles. Mirrors ParticleSystem's 1..4,000,000 range.</summary>
	public const int MaxCapacity = 4_000_000;

	/// <summary>Failure value returned by <see cref="Allocate"/> when the pool is full.</summary>
	public const int Full = -1;

	readonly ParticleState[] _state;
	readonly bool[] _live;
	readonly int[] _generation;
	readonly bool[] _everAllocated;
	readonly int[] _freeStack;
	int _freeCount;
	/// <summary>Live slot indices, kept sorted ascending. Dense: no dead slots in it.</summary>
	readonly List<int> _liveSlots;

	/// <summary>Capacity this pool was built with. Never changes.</summary>
	public int Capacity => _state.Length;

	/// <summary>Number of live slots right now.</summary>
	public int LiveCount() => _liveSlots.Count;

	/// <summary>Number of free slots right now. Always Capacity - LiveCount().</summary>
	public int FreeCount => _freeCount;

	/// <param name="capacity">Slot count, 1..4,000,000. Non-positive (or over-max) capacities throw loudly.</param>
	public ParticlePool( int capacity )
	{
		if ( capacity <= 0 || capacity > MaxCapacity )
			throw new ArgumentOutOfRangeException( nameof( capacity ), $"Capacity must be 1..{MaxCapacity}." );
		_state = new ParticleState[capacity];
		_live = new bool[capacity];
		_generation = new int[capacity];
		_everAllocated = new bool[capacity];
		_freeStack = new int[capacity];
		_liveSlots = new List<int>( capacity );
		// Free stack pops lowest slot first, exactly like ParticleSystem's ctor.
		for ( int i = 0; i < capacity; i++ ) _freeStack[i] = capacity - 1 - i;
		_freeCount = capacity;
		for ( int i = 0; i < capacity; i++ ) _state[i].Rotation = Quaternion.Identity;
	}

	/// <summary>
	/// Take a slot. Returns the slot index, or <see cref="Full"/> (-1) when every slot
	/// is live. A -1 return changes nothing: no live particle is overwritten, no slot
	/// is handed out twice.
	/// </summary>
	public int Allocate()
	{
		if ( _freeCount == 0 ) return Full;
		int slot = _freeStack[--_freeCount];
		if ( _everAllocated[slot] ) _generation[slot]++;
		_everAllocated[slot] = true;
		_live[slot] = true;
		_state[slot].Alive = 1f;
		int at = _liveSlots.BinarySearch( slot );
		_liveSlots.Insert( at < 0 ? ~at : at, slot );
		return slot;
	}

	/// <summary>
	/// Allocate and return a generation-guarded handle for the slot. At full capacity
	/// returns a handle with Slot == <see cref="Full"/> (-1); it is never valid
	/// (see <see cref="IsValid"/>).
	/// </summary>
	public ParticleHandle AllocateHandle()
	{
		int slot = Allocate();
		return slot == Full ? new ParticleHandle( Full, -1 ) : new ParticleHandle( slot, _generation[slot] );
	}

	/// <summary>Alias for <see cref="Allocate"/> (pooling-brief name).</summary>
	public int Acquire() => Allocate();

	/// <summary>Alias for <see cref="Free(int)"/> (pooling-brief name).</summary>
	public void Release( int slot ) => Free( slot );

	/// <summary>Alias for <see cref="Free(ParticleHandle)"/> (pooling-brief name).</summary>
	public void Release( ParticleHandle handle ) => Free( handle );

	/// <summary>Number of live slots right now. Alias for <see cref="LiveCount"/>.</summary>
	public int Count => _liveSlots.Count;

	/// <summary>
	/// Return a slot to the free-list. The slot's state is zeroed (Rotation reset to
	/// Identity, matching ctor init) so no stale data leaks into its next reuse.
	/// Throws <see cref="ArgumentOutOfRangeException"/> for an out-of-range index and
	/// <see cref="InvalidOperationException"/> on a double-free (an already-free slot).
	/// A throwing Free changes nothing.
	/// </summary>
	public void Free( int slot )
	{
		if ( slot < 0 || slot >= _state.Length )
			throw new ArgumentOutOfRangeException( nameof( slot ), $"Slot {slot} is outside 0..{_state.Length - 1}." );
		if ( !_live[slot] )
			throw new InvalidOperationException( $"Slot {slot} is already free (double-free)." );
		_live[slot] = false;
		_liveSlots.Remove( slot );
		_state[slot] = new ParticleState { Rotation = Quaternion.Identity };
		_freeStack[_freeCount++] = slot;
	}

	/// <summary>
	/// Free by handle. Throws <see cref="InvalidOperationException"/> when the handle
	/// is stale (slot freed, or reused under a newer generation) instead of freeing
	/// whatever particle now owns the slot. A throwing Free changes nothing.
	/// </summary>
	public void Free( ParticleHandle handle )
	{
		if ( !IsValid( handle ) )
			throw new InvalidOperationException( $"Stale handle: slot {handle.Slot}, generation {handle.Generation} is not live." );
		Free( handle.Slot );
	}

	/// <summary>
	/// Reset the pool: every slot free, no stale live flags, every state zeroed
	/// (Rotation Identity), free stack rebuilt to fresh-pool order so the next
	/// Allocate sequence is 0, 1, 2, … again. Generations and ever-allocated
	/// flags are deliberately NOT reset, so a handle issued before Clear stays
	/// stale even if its slot is re-allocated afterwards. A cleared slot never
	/// reads as live: IsLive is false, GetState throws, its state's Alive is 0.
	/// </summary>
	public void Clear()
	{
		Array.Clear( _live, 0, _live.Length );
		_liveSlots.Clear();
		for ( int i = 0; i < _state.Length; i++ )
		{
			_state[i] = new ParticleState { Rotation = Quaternion.Identity };
			_freeStack[i] = _state.Length - 1 - i;
		}
		_freeCount = _state.Length;
	}

	/// <summary>True while the slot holds a live particle. Out-of-range slots are not live (false, no throw).</summary>
	public bool IsLive( int slot ) => slot >= 0 && slot < _state.Length && _live[slot];

	/// <summary>Current generation of a slot (0 before its first reuse). Out-of-range throws.</summary>
	public int Generation( int slot )
	{
		if ( slot < 0 || slot >= _state.Length )
			throw new ArgumentOutOfRangeException( nameof( slot ), $"Slot {slot} is outside 0..{_state.Length - 1}." );
		return _generation[slot];
	}

	/// <summary>
	/// True only when the handle's slot is live AND its generation matches the slot's
	/// current generation. A handle for a freed slot, a reused slot, or Slot -1 (the
	/// full-capacity failure handle) is invalid.
	/// </summary>
	public bool IsValid( ParticleHandle handle ) =>
		handle.Slot >= 0 && handle.Slot < _state.Length && _live[handle.Slot] && _generation[handle.Slot] == handle.Generation;

	/// <summary>Live slot index at position <paramref name="index"/> in ascending slot order. Out-of-range throws.</summary>
	public int LiveSlotAt( int index )
	{
		if ( index < 0 || index >= _liveSlots.Count )
			throw new ArgumentOutOfRangeException( nameof( index ), $"Live index {index} is outside 0..{_liveSlots.Count - 1}." );
		return _liveSlots[index];
	}

	/// <summary>
	/// Live slot indices in ascending slot order, deterministic for any allocation/free
	/// history. Walks the dense live list only — dead slots are never visited, and the
	/// yielded sequence is a snapshot copy, safe to hold across further Allocate/Free calls.
	/// </summary>
	public IReadOnlyList<int> IterateLive() => _liveSlots.ToArray();

	/// <summary>State of a live slot. Throws on a free slot: free slots hold no particle to read.</summary>
	public ParticleState GetState( int slot )
	{
		if ( slot < 0 || slot >= _state.Length )
			throw new ArgumentOutOfRangeException( nameof( slot ), $"Slot {slot} is outside 0..{_state.Length - 1}." );
		if ( !_live[slot] )
			throw new InvalidOperationException( $"Slot {slot} is free; it holds no particle state." );
		return _state[slot];
	}

	/// <summary>Replace the state of a live slot. Throws on a free slot or out-of-range index.</summary>
	public void SetState( int slot, ParticleState state )
	{
		if ( slot < 0 || slot >= _state.Length )
			throw new ArgumentOutOfRangeException( nameof( slot ), $"Slot {slot} is outside 0..{_state.Length - 1}." );
		if ( !_live[slot] )
			throw new InvalidOperationException( $"Slot {slot} is free; it holds no particle state." );
		_state[slot] = state;
		_state[slot].Alive = 1f;
	}

	/// <summary>
	/// Audits every pool invariant and throws <see cref="InvalidOperationException"/>
	/// on the first break found: capacity positive, free count + live count == capacity,
	/// the free stack holding exactly the free slots with no duplicates, every live slot
	/// in the sorted live list exactly once, and every state's Alive float matching its
	/// live flag. A healthy pool passes silently after any sequence of operations.
	/// </summary>
	public void Validate()
	{
		if ( _state.Length <= 0 )
			throw new InvalidOperationException( "Pool capacity is non-positive." );
		if ( _freeCount < 0 || _freeCount > _state.Length )
			throw new InvalidOperationException( $"Free count {_freeCount} is outside 0..{_state.Length}." );
		if ( _freeCount + _liveSlots.Count != _state.Length )
			throw new InvalidOperationException( $"Free ({_freeCount}) + live ({_liveSlots.Count}) != capacity ({_state.Length})." );
		var seenFree = new bool[_state.Length];
		for ( int i = 0; i < _freeCount; i++ )
		{
			int slot = _freeStack[i];
			if ( slot < 0 || slot >= _state.Length )
				throw new InvalidOperationException( $"Free stack entry {slot} is out of range." );
			if ( seenFree[slot] )
				throw new InvalidOperationException( $"Free stack holds slot {slot} twice (double-free corruption)." );
			seenFree[slot] = true;
			if ( _live[slot] )
				throw new InvalidOperationException( $"Slot {slot} is both free and live." );
		}
		int prev = -1;
		foreach ( int slot in _liveSlots )
		{
			if ( slot <= prev )
				throw new InvalidOperationException( "Live list is not in ascending slot order." );
			prev = slot;
			if ( !_live[slot] || seenFree[slot] )
				throw new InvalidOperationException( $"Live list slot {slot} is not marked live, or is also free." );
			if ( _state[slot].Alive != 1f )
				throw new InvalidOperationException( $"Live slot {slot} has Alive != 1 in its state." );
		}
		for ( int i = 0; i < _state.Length; i++ )
			if ( !_live[i] && _state[i].Alive != 0f )
				throw new InvalidOperationException( $"Free slot {i} has Alive != 0 in its state." );
	}
}

/// <summary>
/// Generation-guarded reference to a pooled particle: the slot index plus the
/// generation the slot had when the handle was issued. Pass it to
/// <see cref="ParticlePool.IsValid"/> before trusting it, and to
/// <see cref="ParticlePool.Free(ParticleHandle)"/> to free it; a handle whose slot
/// was freed or reused is stale and both calls refuse it. Slot -1 (with generation
/// -1) is the full-capacity failure handle from <see cref="ParticlePool.AllocateHandle"/>.
/// </summary>
public readonly record struct ParticleHandle( int Slot, int Generation );
