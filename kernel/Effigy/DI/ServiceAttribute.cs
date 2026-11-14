using System;

namespace Effigy;

[AttributeUsage( AttributeTargets.Class, AllowMultiple = false, Inherited = false )]
public sealed class ServiceAttribute : Attribute
{
	public Type? Interface { get; set; }
	public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Singleton;
}

public enum ServiceLifetime { Singleton, Transient }