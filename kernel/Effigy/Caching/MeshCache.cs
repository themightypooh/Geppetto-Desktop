using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Generic TTL cache for expensive mesh operations (subdivision, booleans, etc.).
/// Thread-safe via ConcurrentDictionary. Entries expire after a configurable TTL.
/// </summary>
public sealed class MeshCache<TKey, TValue> where TKey : notnull
{
	private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _store = new();
	private readonly TimeSpan _ttl;

	public MeshCache( TimeSpan? ttl = null )
	{
		_ttl = ttl ?? TimeSpan.FromMinutes( 5 );
	}

	public bool TryGet( TKey key, out TValue value )
	{
		if ( _store.TryGetValue( key, out var entry ) )
		{
			if ( DateTime.UtcNow - entry.Created <= _ttl )
			{
				value = entry.Value;
				return true;
			}
			_store.TryRemove( key, out _ );
		}
		value = default!;
		return false;
	}

	public void Set( TKey key, TValue value )
	{
		_store[key] = new CacheEntry<TValue>( value, DateTime.UtcNow );
	}

	public bool Remove( TKey key ) => _store.TryRemove( key, out _ );

	public void Clear() => _store.Clear();

	public int Count => _store.Count;

	public int ExpiredCount
	{
		get
		{
			var now = DateTime.UtcNow;
			var n = 0;
			foreach ( var kv in _store )
				if ( now - kv.Value.Created > _ttl ) n++;
			return n;
		}
	}

	public void PurgeExpired()
	{
		var now = DateTime.UtcNow;
		foreach ( var kv in _store )
			if ( now - kv.Value.Created > _ttl )
				_store.TryRemove( kv.Key, out _ );
	}

	private readonly struct CacheEntry<T>
	{
		public readonly T Value;
		public readonly DateTime Created;
		public CacheEntry( T value, DateTime created ) { Value = value; Created = created; }
	}
}

/// <summary>
/// Static shared caches for common mesh operations.
/// </summary>
public static class MeshCaches
{
	public static readonly MeshCache<int, object> Subdivision = new( TimeSpan.FromMinutes( 10 ) );
	public static readonly MeshCache<int, object> Boolean = new( TimeSpan.FromMinutes( 10 ) );
}