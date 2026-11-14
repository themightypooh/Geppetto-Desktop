using System;

namespace Effigy;

[AttributeUsage( AttributeTargets.Class, AllowMultiple = false, Inherited = false )]
public sealed class PluginAttribute : Attribute
{
	public string Name { get; }
	public string Version { get; set; } = "1.0";
	public PluginAttribute( string name ) { Name = name; }
}