using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

public sealed class GlShader : IDisposable
{
	readonly GL _gl;
	public uint Handle { get; }

	public GlShader( GL gl, string vertexSource, string fragmentSource )
	{
		_gl = gl;
		var vs = Compile( ShaderType.VertexShader, vertexSource );
		var fs = Compile( ShaderType.FragmentShader, fragmentSource );
		Handle = _gl.CreateProgram();
		_gl.AttachShader( Handle, vs );
		_gl.AttachShader( Handle, fs );
		_gl.LinkProgram( Handle );
		_gl.GetProgram( Handle, ProgramPropertyARB.LinkStatus, out int linked );
		if ( linked == 0 )
		{
			var log = _gl.GetProgramInfoLog( Handle );
			_gl.DeleteShader( vs );
			_gl.DeleteShader( fs );
			_gl.DeleteProgram( Handle );
			throw new InvalidOperationException( "shader link failed:\n" + log );
		}
		_gl.DetachShader( Handle, vs );
		_gl.DetachShader( Handle, fs );
		_gl.DeleteShader( vs );
		_gl.DeleteShader( fs );
	}

	uint Compile( ShaderType type, string source )
	{
		var shader = _gl.CreateShader( type );
		_gl.ShaderSource( shader, source );
		_gl.CompileShader( shader );
		_gl.GetShader( shader, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 )
		{
			var log = _gl.GetShaderInfoLog( shader );
			_gl.DeleteShader( shader );
			throw new InvalidOperationException( $"{type} compile failed:\n" + log );
		}
		return shader;
	}

	public void Use() => _gl.UseProgram( Handle );

	// Uniform locations are cached per program: Silk marshals the name string on every
	// GetUniformLocation, which allocates on the per-frame path (SetLook alone is 13
	// lookups per call, 3 calls per frame; the steady-frame check allows 0 B). The cache
	// lives on the instance and the instance dies with its program, so a recycled
	// program id can never hit a stale entry.
	readonly Dictionary<string, int> _uniforms = new();

	public int Uniform( string name )
	{
		if ( !_uniforms.TryGetValue( name, out int loc ) )
			_uniforms[name] = loc = _gl.GetUniformLocation( Handle, name );
		return loc;
	}

	public unsafe void SetMatrix( int location, System.Numerics.Matrix4x4 m )
	{
		if ( location < 0 ) return;
		_gl.UniformMatrix4( location, 1, false, (float*)&m );
	}

	public unsafe void SetMatrixArray( int location, ReadOnlySpan<System.Numerics.Matrix4x4> matrices )
	{
		if ( location < 0 || matrices.IsEmpty ) return;
		fixed ( System.Numerics.Matrix4x4* ptr = matrices )
		{
			_gl.UniformMatrix4( location, (uint)matrices.Length, false, (float*)ptr );
		}
	}

	public void SetVec3( int location, float x, float y, float z )
	{
		if ( location < 0 ) return;
		_gl.Uniform3( location, x, y, z );
	}

	public void SetVec2( int location, float x, float y )
	{
		if ( location < 0 ) return;
		_gl.Uniform2( location, x, y );
	}

	public unsafe void SetVec2Array( int location, ReadOnlySpan<System.Numerics.Vector2> values )
	{
		if ( location < 0 || values.IsEmpty ) return;
		fixed ( System.Numerics.Vector2* ptr = values )
		{
			_gl.Uniform2( location, (uint)values.Length, (float*)ptr );
		}
	}

	public unsafe void SetFloatArray( int location, ReadOnlySpan<float> values )
	{
		if ( location < 0 || values.IsEmpty ) return;
		fixed ( float* ptr = values )
		{
			_gl.Uniform1( location, (uint)values.Length, ptr );
		}
	}

	public void SetFloat( int location, float v )
	{
		if ( location < 0 ) return;
		_gl.Uniform1( location, v );
	}

	public void SetInt( int location, int v )
	{
		if ( location < 0 ) return;
		_gl.Uniform1( location, v );
	}

	// Lane C: the desktop GLSL half of the look spectrum, mirroring Effigy.Render.LookSpectrum.
	// This is the GLSL source + a uniform helper, kept in the wrapper file Lane C owns.
	// It is NOT yet wired into RendererShaders.FragmentSrc / SceneRenderer (those files are
	// outside Lane C, and SceneRenderer is Lane B's): a future wiring pastes LookSpectrumGlsl
	// into the material fragment shader, calls lookSpectrum() in place of the plain diffuse
	// term, and calls SetLook() once per material. The helper takes plain floats on purpose:
	// the desktop builds against its own kernel mirror, which Lane F has not synced yet, so
	// it must not depend on the new SlotLook fields to compile.
	public const string LookSpectrumGlsl = @"
float lookQuantize(float nl, int bands, float edge) {
	if (bands <= 1) return clamp(nl, 0.0, 1.0);
	float scaled = clamp(nl, 0.0, 1.0) * float(bands);
	float level = floor(scaled);
	float frac = scaled - level;
	float lo = level / float(bands);
	if (frac < edge) return mix(max(level - 1.0, 0.0) / float(bands), lo, smoothstep(0.0, edge, frac));
	if (frac > 1.0 - edge) return mix(lo, min(level + 1.0, float(bands)) / float(bands), smoothstep(1.0 - edge, 1.0, frac));
	return lo;
}
vec3 lookOutlineOffset(vec3 pos, vec3 normal, float width) {
	return width > 0.0 ? pos + normalize(normal) * width : pos;
}
float lookDitheredAlpha(float opacity, vec2 fragCoord) {
	if (opacity >= 1.0) return 1.0;
	if (opacity <= 0.0) return 0.0;
	float t = fract(sin(dot(fragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
	return opacity > t ? 1.0 : 0.0;
}
vec3 lookSpectrum(vec3 albedo, float nl, float nv, float nh, float vl, float roughness,
	float cavity, float ao,
	float uToon, int uToonBands, float uToonRim, float uStylized,
	float uClearcoat, float uClearcoatRough, float uSubsurface, vec3 uSubsurfaceColor, float uOpacity,
	out float outAlpha, out float outRough) {
	float diffuse = max(nl, 0.0);
	float rough = clamp(roughness, 0.0, 1.0);
	vec3 colour = albedo;
	float spec = 0.0;
	if (uToon > 0.0) {
		int bands = clamp(uToonBands, 2, 8);
		diffuse = mix(diffuse, lookQuantize(diffuse, bands, 0.08), uToon);
		if (rough < 0.4) {
			float h = pow(max(nh, 0.0), max(1.0, 8.0 * (1.0 - rough)));
			spec = mix(h, lookQuantize(h, bands, 0.08), uToon);
		}
		float fres = pow(1.0 - clamp(nv, 0.0, 1.0), 4.0);
		colour += vec3(1.0) * (smoothstep(0.62, 0.68, fres) * uToonRim * uToon * 0.5);
	} else {
		spec = pow(max(nh, 0.0), 1.0 / (rough * rough + 0.02)) * 0.25;
	}
	if (uStylized > 0.0) {
		float ridge = clamp(-cavity, 0.0, 1.0);
		float crease = clamp(cavity, 0.0, 1.0);
		colour *= 1.0 + 0.18 * uStylized * ridge;
		float lum = (colour.r + colour.g + colour.b) / 3.0;
		colour = mix(vec3(lum), colour, 1.0 + 0.2 * uStylized * ridge);
		colour = mix(colour, vec3(colour.r, colour.g * 0.85, colour.b * 0.65), uStylized * crease * 0.5);
		colour *= 1.0 - 0.25 * uStylized * clamp(ao, 0.0, 1.0) * crease;
		rough = clamp(rough + 0.25 * uStylized * crease, 0.0, 1.0);
	}
	colour = colour * (0.25 + 0.75 * diffuse) + vec3(1.0) * spec * diffuse;
	if (uClearcoat > 0.0) {
		float cr = clamp(uClearcoatRough, 0.01, 1.0);
		float fresnel = 0.04 + 0.96 * pow(1.0 - clamp(nv, 0.0, 1.0), 5.0);
		colour += vec3(1.0) * (uClearcoat * pow(max(nh, 0.0), 1.0 / (cr * cr + 0.02)) * fresnel * 0.5);
	}
	if (uSubsurface > 0.0) {
		float wrap = 0.5 * uSubsurface;
		float wrapDiff = max(0.0, (nl + wrap) / (1.0 + wrap));
		float back = pow(max(0.0, -vl), 3.0) * (1.0 - 0.5 * clamp(ao, 0.0, 1.0));
		colour += uSubsurfaceColor * (uSubsurface * (wrapDiff * 0.5 + back * 0.75));
	}
	outAlpha = clamp(uOpacity, 0.0, 1.0);
	outRough = rough;
	return colour;
}
";

	/// <summary>Lane C: push one look's spectrum values into the uLook* uniforms of this program.
	/// Missing uniforms (a shader that has not pasted <see cref="LookSpectrumGlsl"/> in yet) are
	/// ignored by <see cref="SetFloat"/>/<see cref="SetInt"/>/<see cref="SetVec3"/>, which skip
	/// locations below 0, so calling this before the wiring lands is a safe no-op.</summary>
	public void SetLook( float toon, int toonBands, float toonRim, float stylized,
		float outlineWidth, float outlineR, float outlineG, float outlineB,
		float clearcoat, float clearcoatRough, float subsurface, float subsurfaceR, float subsurfaceG, float subsurfaceB,
		float opacity, float shadowTintR = 1f, float shadowTintG = 1f, float shadowTintB = 1f, float iridescence = 0f )
	{
		SetFloat( Uniform( "uLookToon" ), toon );
		SetInt( Uniform( "uLookToonBands" ), toonBands );
		SetFloat( Uniform( "uLookToonRim" ), toonRim );
		SetFloat( Uniform( "uLookStylized" ), stylized );
		SetFloat( Uniform( "uLookOutlineWidth" ), outlineWidth );
		SetVec3( Uniform( "uLookOutlineColor" ), outlineR, outlineG, outlineB );
		SetFloat( Uniform( "uLookClearcoat" ), clearcoat );
		SetFloat( Uniform( "uLookClearcoatRough" ), clearcoatRough );
		SetFloat( Uniform( "uLookSubsurface" ), subsurface );
		SetVec3( Uniform( "uLookSubsurfaceColor" ), subsurfaceR, subsurfaceG, subsurfaceB );
		SetFloat( Uniform( "uLookOpacity" ), opacity );
		SetVec3( Uniform( "uLookShadowTint" ), shadowTintR, shadowTintG, shadowTintB );
		SetFloat( Uniform( "uLookIridescence" ), iridescence );
	}

	public void Dispose() => _gl.DeleteProgram( Handle );
}
