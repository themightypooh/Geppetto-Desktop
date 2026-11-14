using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Effigy;

public sealed class Container
{
	private readonly Dictionary<Type, ServiceDescriptor> _descriptors = new();
	private readonly ConcurrentDictionary<Type, object> _singletons = new();

	public void Register<TService, TImpl>() where TImpl : TService => Register( typeof( TService ), typeof( TImpl ), ServiceLifetime.Singleton );
	public void Register<TService, TImpl>( ServiceLifetime lifetime ) where TImpl : TService => Register( typeof( TService ), typeof( TImpl ), lifetime );
	public void Register( Type service, Type impl, ServiceLifetime lifetime = ServiceLifetime.Singleton )
	{
		_descriptors[service] = new ServiceDescriptor( service, impl, lifetime );
	}

	public void RegisterInstance<T>( T instance ) where T : class
	{
		_singletons[typeof( T )] = instance!;
		_descriptors[typeof( T )] = new ServiceDescriptor( typeof( T ), instance!.GetType(), ServiceLifetime.Singleton );
	}

	public T Resolve<T>() => (T)Resolve( typeof( T ) );

	public object Resolve( Type service )
	{
		if ( _singletons.TryGetValue( service, out var cached ) ) return cached;
		if ( !_descriptors.TryGetValue( service, out var desc ) )
			throw new InvalidOperationException( $"Service not registered: {service}" );
		var instance = Create( desc.Implementation );
		if ( desc.Lifetime == ServiceLifetime.Singleton )
			_singletons[service] = instance;
		return instance;
	}

	private object Create( Type type )
	{
		var ctor = type.GetConstructors().OrderByDescending( c => c.GetParameters().Length ).FirstOrDefault()
			?? throw new InvalidOperationException( $"No constructor for {type}" );
		var args = ctor.GetParameters().Select( p => Resolve( p.ParameterType ) ).ToArray();
		return ctor.Invoke( args );
	}

	private sealed class ServiceDescriptor
	{
		public Type Service { get; }
		public Type Implementation { get; }
		public ServiceLifetime Lifetime { get; }
		public ServiceDescriptor( Type s, Type i, ServiceLifetime l ) { Service = s; Implementation = i; Lifetime = l; }
	}
}