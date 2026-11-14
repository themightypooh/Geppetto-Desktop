using System;
using System.Collections.Generic;

namespace Effigy;

public class VmdlMorph
{
	private readonly Dictionary<string, object> _cache = new Dictionary<string, object>();
	private const int CacheLimit = 64;

	public object GetMorph( string name )
	{
		if ( _cache.TryGetValue( name, out var v ) ) return v;
		var result = new object();
		if ( _cache.Count >= CacheLimit ) _cache.Clear();
		_cache[name] = result;
		return result;
	}

	public void ClearCache() => _cache.Clear();
}