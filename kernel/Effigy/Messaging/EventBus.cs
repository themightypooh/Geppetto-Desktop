using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

public sealed class EventBus
{
	private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();

	public void Subscribe<T>( Action<T> handler )
	{
		var list = _handlers.GetOrAdd( typeof( T ), _ => new List<Delegate>() );
		lock ( list ) list.Add( handler );
	}

	public void Unsubscribe<T>( Action<T> handler )
	{
		if ( _handlers.TryGetValue( typeof( T ), out var list ) )
			lock ( list ) list.Remove( handler );
	}

	public void Publish<T>( T evt )
	{
		if ( !_handlers.TryGetValue( typeof( T ), out var list ) ) return;
		Delegate[] snapshot;
		lock ( list ) snapshot = list.ToArray();
		foreach ( var d in snapshot )
			((Action<T>)d)( evt );
	}
}