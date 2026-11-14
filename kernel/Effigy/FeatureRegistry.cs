using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Effigy;

public sealed class FeatureDescriptor
{
	public Type Type { get; init; } = null!;
	public string Name { get; init; } = "";
	public string Category { get; init; } = "General";
	public string Description { get; init; } = "";
}

public static class FeatureRegistry
{
	private static readonly List<FeatureDescriptor> _features = new();
	private static bool _scanned;

	public static IReadOnlyList<FeatureDescriptor> Features => _features;

	/// <summary>Register one feature type from its <see cref="FeatureAttribute"/>:
	/// the explicit half of Scan, so a feature can be wired (and tested) without a
	/// whole-assembly scan. Returns false when the type carries no FeatureAttribute or
	/// is already registered; never throws for an unmarked type.</summary>
	public static bool Register( Type type )
	{
		if ( type is null ) return false;
		var attr = type.GetCustomAttribute<FeatureAttribute>();
		if ( attr is null ) return false;
		if ( _features.Any( f => f.Type == type ) ) return false;
		_features.Add( new FeatureDescriptor
		{
			Type = type,
			Name = attr.Name,
			Category = attr.Category,
			Description = attr.Description,
		} );
		return true;
	}

	public static void Scan( System.Reflection.Assembly? assembly = null )
	{
		if ( _scanned ) return;
		_scanned = true;
		var asms = assembly is null ? AppDomain.CurrentDomain.GetAssemblies() : new[] { assembly };
		foreach ( var asm in asms )
		{
			Type[] types;
			try { types = asm.GetTypes(); } catch { continue; }
			foreach ( var t in types )
				Register( t );
		}
	}

	public static IEnumerable<FeatureDescriptor> ByCategory( string category ) =>
		_features.Where( f => f.Category == category );

	public static FeatureDescriptor? Find( string name ) =>
		_features.FirstOrDefault( f => f.Name == name );
}