using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>R10: uniform locations looked up once per (program, name). Silk marshals the name
/// string on every <c>GetUniformLocation</c>, which allocates on the per-frame path.</summary>
public sealed class UniformCache
{
	readonly GL _gl;
	readonly Dictionary<(uint, string), int> _locations = new();
	public UniformCache( GL gl ) => _gl = gl;

	public int this[uint program, string name]
	{
		get
		{
			if ( !_locations.TryGetValue( (program, name), out int loc ) )
				_locations[(program, name)] = loc = _gl.GetUniformLocation( program, name );
			return loc;
		}
	}

	/// <summary>Call when a program is deleted so a recycled id cannot hit stale entries.</summary>
	public void Clear() => _locations.Clear();
}
