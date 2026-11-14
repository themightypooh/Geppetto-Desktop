using System;

namespace Effigy;

[AttributeUsage( AttributeTargets.Class, AllowMultiple = false, Inherited = false )]
public sealed class FeatureAttribute : Attribute
{
	public string Name { get; }
	public string Category { get; set; } = "General";
	public string Description { get; set; } = "";
	public FeatureAttribute( string name ) { Name = name; }
}