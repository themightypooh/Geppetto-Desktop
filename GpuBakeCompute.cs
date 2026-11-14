using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// The GPU bake backend: normal + height maps by compute-shader raycast.
///
/// HOW IT WORKS. For each texel, the CPU builds a ray from the cage (the
/// low mesh's UVs locate the texel on the cage, barycentric interpolation
/// gives the cage point, normal and tangent frame) and the GPU finds the
/// closest high-mesh triangle along it (Möller–Trumbore, no backface cull).
/// The shader returns the hit normal in object space and the hit distance;
/// the CPU encodes tangent-space normal + height with the same conventions
/// as <see cref="NormalBake"/> (OpenGL green, −y by default, honoring
/// <see cref="BakeOptions.FlipGreen"/>; height is mid-grey on the cage).
///
/// WHAT IT IS NOT. A first GPU step, not the Marmoset-class path:
///   - Brute force: every texel tests every high triangle. The ceiling is
///     32,768 high triangles and 1M texels (a 1K test-bake); above that the
///     backend declines and the job falls back to the CPU bake. A BVH in the
///     shader is what makes multi-million-poly sources fast, and it is not here.
///   - Normal + height only. AO / curvature / thickness / ID stay on the CPU
///     bake, which the job merges in (see GpuBake.GpuBakeJob).
///   - Cage: an explicit group cage is used when set; without one the rays
///     start on the low surface itself, while the CPU bake inflates a cage.
///     On convex fixtures the two agree; on concave ones the test-bake
///     cross-check is what catches the difference, and the job falls back.
///   - Padding is a small CPU dilation (nearest filled neighbour), not the
///     CPU bake's exact bleed.
/// Every GL failure — no EGL context, shader compile/link failure, buffer
/// failure — declines with a reason instead of throwing, so a broken GPU
/// path costs a CPU bake, never a crash or a wrong map. GL thread rules are
/// the caller's: one lock serialises all backend calls.
///
/// HOST WIRING (final integrator, shared entry point — not this file): call
/// <see cref="Install"/> once at Desktop startup, next to
/// ManifoldBoolean.Install(). Until then the CPU bake runs and nothing changes.
/// </summary>
public sealed class GpuBakeComputeBackend : GpuBake.IGpuBakeBackend, IDisposable
{
	public const int MaxHighTriangles = 32768;
	public const int MaxTexels = 1024 * 1024;

	public static void Install() => GpuBake.Provider ??= new GpuBakeComputeBackend();

	readonly object _lock = new();
	HeadlessGlContext _context;
	uint _program;
	bool _disposed;

	public void Dispose()
	{
		lock ( _lock )
		{
			_disposed = true;
			_context?.Dispose();
			_context = null;
		}
	}

	public bool TryBake( BakeJob job, BakeMaps maps, out BakeSet set, out string why )
	{
		set = null;
		why = null;
		if ( job is null ) { why = "No bake job."; return false; }
		var wanted = maps & (BakeMaps.Normal | BakeMaps.Height);
		if ( wanted == BakeMaps.None ) { why = "GpuBakeCompute bakes normal + height only."; return false; }
		if ( job.Width * job.Height > MaxTexels ) { why = $"GpuBakeCompute ceiling is {MaxTexels:N0} texels (this job is {job.Width * job.Height:N0})."; return false; }
		var highTris = 0;
		foreach ( var g in job.Groups )
			highTris += TriangleCount( g.High );
		if ( highTris > MaxHighTriangles ) { why = $"GpuBakeCompute ceiling is {MaxHighTriangles:N0} high triangles (this job has {highTris:N0})."; return false; }

		lock ( _lock )
		{
			if ( _disposed ) { why = "GpuBakeCompute backend is disposed."; return false; }
			try
			{
				var gl = GetGl( out why );
				if ( gl is null ) return false;
				set = BakeOnGpu( gl, job, wanted );
				return true;
			}
			catch ( Exception ex )
			{
				set = null;
				why = $"GPU bake failed: {ex.Message}";
				return false;
			}
		}
	}

	GL GetGl( out string why )
	{
		why = null;
		if ( _context is null )
		{
			try { _context = new HeadlessGlContext(); }
			catch ( Exception ex ) { why = $"No headless GL context: {ex.Message}"; return null; }
		}
		return _context.Gl;
	}

	BakeSet BakeOnGpu( GL gl, BakeJob job, BakeMaps wanted )
	{
		EnsureProgram( gl );
		var set = new BakeSet();
		var width = job.Width;
		var height = job.Height;
		var texels = width * height;
		var options = job.Options ?? new BakeOptions();

		foreach ( var group in job.Groups )
		{
			var cage = group.Cage ?? group.Low;
			var rays = BuildRays( cage, width, height );
			var (positions, normals, indices) = UploadHigh( group.High );
			var hits = Raycast( gl, (positions, normals, indices), rays, options );
			if ( (wanted & BakeMaps.Normal) != 0 )
				WriteNormal( set, hits, rays.Frames, width, height, options );
			if ( (wanted & BakeMaps.Height) != 0 )
				WriteHeight( set, hits, width, height, options );
			set.Samples += texels;
			set.Hits += hits.Count( h => h.HasHit );
		}
		return set;
	}

	// ------------------------------------------------------------------ rays

	/// <summary>Per-texel ray origin + direction, and the tangent frame at that texel.</summary>
	sealed class TexelFrame
	{
		public Vec3 Tangent;
		public Vec3 Bitangent;
		public Vec3 Normal;
	}

	/// <summary>
	/// Locate every texel on the cage through its UVs: a uniform UV grid indexes
	/// the cage faces, so the lookup is near-constant time instead of a scan.
	/// The frame comes from the MikkTSpace hook when installed (the same frame
	/// the CPU bake uses), else a Gram-Schmidt frame from the normal.
	/// </summary>
	(float[] Origins, float[] Directions, TexelFrame[] Frames) BuildRays( PolyMesh cage, int width, int height )
	{
		var texels = width * height;
		var origins = new float[texels * 4];
		var directions = new float[texels * 4];
		var frames = new TexelFrame[texels];
		var normals = VertexNormals( cage );
		var tangents = CornerTangents( cage, normals );

		const int cells = 32;
		var grid = new List<int>[cells * cells];
		for ( var i = 0; i < grid.Length; i++ ) grid[i] = new List<int>();
		for ( var fi = 0; fi < cage.Faces.Count; fi++ )
		{
			var f = cage.Faces[fi];
			var minU = float.MaxValue; var maxU = float.MinValue;
			var minV = float.MaxValue; var maxV = float.MinValue;
			foreach ( var uv in f.UVs )
			{
				minU = Math.Min( minU, uv.x ); maxU = Math.Max( maxU, uv.x );
				minV = Math.Min( minV, uv.y ); maxV = Math.Max( maxV, uv.y );
			}
			var x0 = Math.Clamp( (int)(minU * cells), 0, cells - 1 );
			var x1 = Math.Clamp( (int)(maxU * cells), 0, cells - 1 );
			var y0 = Math.Clamp( (int)(minV * cells), 0, cells - 1 );
			var y1 = Math.Clamp( (int)(maxV * cells), 0, cells - 1 );
			for ( var gy = y0; gy <= y1; gy++ )
			for ( var gx = x0; gx <= x1; gx++ )
				grid[gy * cells + gx].Add( fi );
		}

		for ( var y = 0; y < height; y++ )
		for ( var x = 0; x < width; x++ )
		{
			var u = (x + 0.5f) / width;
			var v = (y + 0.5f) / height;
			var i = y * width + x;
			var gx = Math.Clamp( (int)(u * cells), 0, cells - 1 );
			var gy = Math.Clamp( (int)(v * cells), 0, cells - 1 );
			var found = false;
			foreach ( var fi in grid[gy * cells + gx] )
			{
				if ( !TryLocate( cage, fi, u, v, normals, tangents, out var point, out var frame ) ) continue;
				origins[i * 4] = point.x; origins[i * 4 + 1] = point.y; origins[i * 4 + 2] = point.z;
				directions[i * 4] = frame.Normal.x; directions[i * 4 + 1] = frame.Normal.y; directions[i * 4 + 2] = frame.Normal.z;
				frames[i] = frame;
				found = true;
				break;
			}
			if ( !found )
			{
				frames[i] = new TexelFrame { Tangent = new Vec3( 1, 0, 0 ), Bitangent = new Vec3( 0, 1, 0 ), Normal = new Vec3( 0, 0, 1 ) };
			}
		}
		return (origins, directions, frames);
	}

	static bool TryLocate( PolyMesh cage, int faceIndex, float u, float v, Vec3[] normals, Vec4[] tangents, out Vec3 point, out TexelFrame frame )
	{
		point = Vec3.Zero;
		frame = null;
		var f = cage.Faces[faceIndex];
		if ( f.Count < 3 ) return false;
		// Fan-triangulate the face in UV space; the first triangle containing (u, v) wins.
		for ( var k = 1; k + 1 < f.Count; k++ )
		{
			var uv0 = f.UVs[0]; var uv1 = f.UVs[k]; var uv2 = f.UVs[k + 1];
			var (a, b, c) = Barycentric( u, v, uv0, uv1, uv2 );
			if ( a < -1e-6f || b < -1e-6f || c < -1e-6f ) continue;
			var i0 = f.Indices[0]; var i1 = f.Indices[k]; var i2 = f.Indices[k + 1];
			point = cage.Positions[i0] * a + cage.Positions[i1] * b + cage.Positions[i2] * c;
			var n = (normals[i0] * a + normals[i1] * b + normals[i2] * c).Normal;
			var t = new Vec3( tangents[i0].x, tangents[i0].y, tangents[i0].z ) * a
				+ new Vec3( tangents[i1].x, tangents[i1].y, tangents[i1].z ) * b
				+ new Vec3( tangents[i2].x, tangents[i2].y, tangents[i2].z ) * c;
			// Orthogonalise the interpolated tangent against the normal.
			t -= n * Vec3.Dot( t, n );
			if ( t.LengthSquared < 1e-12f ) t = Math.Abs( n.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
			t = t.Normal;
			var w = (tangents[i0].w * a + tangents[i1].w * b + tangents[i2].w * c) >= 0f ? 1f : -1f;
			var bitangent = Vec3.Cross( n, t ) * w;
			frame = new TexelFrame { Tangent = t, Bitangent = bitangent, Normal = n };
			return true;
		}
		return false;
	}

	static (float A, float B, float C) Barycentric( float u, float v, Vec2 uv0, Vec2 uv1, Vec2 uv2 )
	{
		var d = (uv1.y - uv2.y) * (uv0.x - uv2.x) + (uv2.x - uv1.x) * (uv0.y - uv2.y);
		if ( Math.Abs( d ) < 1e-12f ) return (-1f, -1f, -1f);
		var a = ((uv1.y - uv2.y) * (u - uv2.x) + (uv2.x - uv1.x) * (v - uv2.y)) / d;
		var b = ((uv2.y - uv0.y) * (u - uv2.x) + (uv0.x - uv2.x) * (v - uv2.y)) / d;
		return (a, b, 1f - a - b);
	}

	/// <summary>Per-vertex tangents from the MikkTSpace hook when installed, else a Gram-Schmidt frame.</summary>
	static Vec4[] CornerTangents( PolyMesh mesh, Vec3[] normals )
	{
		var positions = mesh.Positions.ToArray();
		var uvs = new Vec2[mesh.Positions.Count];
		// One UV per vertex: the first corner that uses it (seams collapse, which the hook tolerates).
		var seen = new bool[mesh.Positions.Count];
		foreach ( var f in mesh.Faces )
			for ( var c = 0; c < f.Count; c++ )
				if ( !seen[f.Indices[c]] )
				{
					seen[f.Indices[c]] = true;
					uvs[f.Indices[c]] = f.UVs[c];
				}
		if ( NormalBake.MikkTangents is not null )
		{
			try { return NormalBake.MikkTangents( positions, normals, uvs ); } catch { }
		}
		var tangents = new Vec4[mesh.Positions.Count];
		for ( var i = 0; i < tangents.Length; i++ )
		{
			var n = normals[i];
			var t = Math.Abs( n.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
			t = (t - n * Vec3.Dot( t, n )).Normal;
			tangents[i] = new Vec4( t.x, t.y, t.z, 1f );
		}
		return tangents;
	}

	static Vec3[] VertexNormals( PolyMesh mesh )
	{
		var normals = new Vec3[mesh.Positions.Count];
		foreach ( var f in mesh.Faces )
		{
			if ( f.Count < 3 ) continue;
			var a = mesh.Positions[f.Indices[0]];
			var b = mesh.Positions[f.Indices[1]];
			var c = mesh.Positions[f.Indices[2]];
			var n = Vec3.Cross( b - a, c - a );
			foreach ( var i in f.Indices ) normals[i] += n;
		}
		for ( var i = 0; i < normals.Length; i++ )
			normals[i] = normals[i].LengthSquared > 1e-12f ? normals[i].Normal : new Vec3( 0, 0, 1 );
		return normals;
	}

	// ------------------------------------------------------------------ GPU raycast

	sealed class Hit
	{
		public bool HasHit;
		public Vec3 Normal;
		public float Distance;
	}

	static int TriangleCount( PolyMesh mesh )
	{
		var n = 0;
		foreach ( var f in mesh.Faces ) n += Math.Max( 0, f.Count - 2 );
		return n;
	}

	(float[] Positions, float[] Normals, uint[] Indices) UploadHigh( PolyMesh high )
	{
		var positions = new float[high.Positions.Count * 4];
		for ( var i = 0; i < high.Positions.Count; i++ )
		{
			positions[i * 4] = high.Positions[i].x;
			positions[i * 4 + 1] = high.Positions[i].y;
			positions[i * 4 + 2] = high.Positions[i].z;
		}
		var normals = VertexNormals( high );
		var nrm = new float[high.Positions.Count * 4];
		for ( var i = 0; i < normals.Length; i++ )
		{
			nrm[i * 4] = normals[i].x;
			nrm[i * 4 + 1] = normals[i].y;
			nrm[i * 4 + 2] = normals[i].z;
		}
		var indices = new List<uint>();
		foreach ( var f in high.Faces )
			for ( var k = 1; k + 1 < f.Count; k++ )
			{
				indices.Add( (uint)f.Indices[0] );
				indices.Add( (uint)f.Indices[k] );
				indices.Add( (uint)f.Indices[k + 1] );
			}
		return (positions, nrm, indices.ToArray());
	}

	Hit[] Raycast( GL gl, (float[] Positions, float[] Normals, uint[] Indices) high,
		(float[] Origins, float[] Directions, TexelFrame[] Frames) rays, BakeOptions options )
	{
		var texels = rays.Frames.Length;
		var outData = new float[texels * 4];
		var maxDistance = options.MaxDistance > 0f ? options.MaxDistance : 1e6f;
		var buffers = new List<uint>();
		try
		{
			uint Upload( float[] data ) => UploadBuffer( gl, buffers, data );
			uint UploadUints( uint[] data ) => UploadBuffer( gl, buffers, data );
			var posBuf = Upload( high.Positions );
			var nrmBuf = Upload( high.Normals );
			var idxBuf = UploadUints( high.Indices );
			var orgBuf = Upload( rays.Origins );
			var dirBuf = Upload( rays.Directions );
			var outBuf = NewBuffer( gl, buffers, texels * 4 * 4 );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, posBuf );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, nrmBuf );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, idxBuf );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 3, orgBuf );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 4, dirBuf );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 5, outBuf );
			gl.UseProgram( _program );
			SetUniform( gl, _program, "uTriCount", high.Indices.Length / 3 );
			SetUniform( gl, _program, "uTexelCount", texels );
			SetUniform( gl, _program, "uMaxDistance", maxDistance );
			gl.DispatchCompute( (uint)((texels + 63) / 64), 1, 1 );
			gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
			gl.Finish();
			ReadBuffer( gl, outBuf, outData );
		}
		finally
		{
			foreach ( var b in buffers ) gl.DeleteBuffer( b );
		}
		var hits = new Hit[texels];
		for ( var i = 0; i < texels; i++ )
		{
			var w = outData[i * 4 + 3];
			hits[i] = w >= 0f
				? new Hit { HasHit = true, Normal = new Vec3( outData[i * 4], outData[i * 4 + 1], outData[i * 4 + 2] ), Distance = w }
				: new Hit { HasHit = false };
		}
		return hits;
	}

	static uint UploadBuffer<T>( GL gl, List<uint> buffers, T[] data ) where T : unmanaged
	{
		var id = gl.GenBuffer();
		buffers.Add( id );
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, id );
		var bytes = (nuint)(data.Length * Marshal.SizeOf<T>());
		unsafe { gl.BufferData( BufferTargetARB.ShaderStorageBuffer, bytes, null, BufferUsageARB.StaticDraw ); }
		unsafe
		{
			fixed ( T* p = data ) gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, bytes, p );
		}
		return id;
	}

	static uint NewBuffer( GL gl, List<uint> buffers, int bytes )
	{
		var id = gl.GenBuffer();
		buffers.Add( id );
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, id );
		unsafe { gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)bytes, null, BufferUsageARB.StaticDraw ); }
		return id;
	}

	static void ReadBuffer<T>( GL gl, uint id, T[] data ) where T : unmanaged
	{
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, id );
		var bytes = (nuint)(data.Length * Marshal.SizeOf<T>());
		unsafe
		{
			fixed ( T* p = data ) gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, bytes, p );
		}
	}

	static void SetUniform( GL gl, uint program, string name, int value )
	{
		var loc = gl.GetUniformLocation( program, name );
		if ( loc >= 0 ) gl.Uniform1( loc, value );
	}

	static void SetUniform( GL gl, uint program, string name, float value )
	{
		var loc = gl.GetUniformLocation( program, name );
		if ( loc >= 0 ) gl.Uniform1( loc, value );
	}

	void EnsureProgram( GL gl )
	{
		if ( _program != 0 ) return;
		var shader = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( shader, RaycastSrc );
		gl.CompileShader( shader );
		gl.GetShader( shader, ShaderParameterName.CompileStatus, out int compiled );
		if ( compiled == 0 )
		{
			var log = gl.GetShaderInfoLog( shader );
			gl.DeleteShader( shader );
			throw new InvalidOperationException( $"GPU bake raycast shader failed to compile: {log}" );
		}
		_program = gl.CreateProgram();
		gl.AttachShader( _program, shader );
		gl.LinkProgram( _program );
		gl.GetProgram( _program, ProgramPropertyARB.LinkStatus, out int linked );
		if ( linked == 0 )
		{
			var log = gl.GetProgramInfoLog( _program );
			gl.DeleteProgram( _program );
			_program = 0;
			throw new InvalidOperationException( $"GPU bake raycast program failed to link: {log}" );
		}
		gl.DeleteShader( shader );
	}

	const string RaycastSrc = @"#version 430 core
layout(local_size_x = 64) in;
layout(std430, binding = 0) readonly buffer HighPos { vec4 highPos[]; };
layout(std430, binding = 1) readonly buffer HighNrm { vec4 highNrm[]; };
layout(std430, binding = 2) readonly buffer HighIdx { uint highIdx[]; };
layout(std430, binding = 3) readonly buffer RayOrg { vec4 rayOrg[]; };
layout(std430, binding = 4) readonly buffer RayDir { vec4 rayDir[]; };
layout(std430, binding = 5) buffer OutData { vec4 outData[]; };
uniform int uTriCount;
uniform int uTexelCount;
uniform float uMaxDistance;
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uTexelCount)) return;
    vec3 o = rayOrg[i].xyz;
    vec3 d = rayDir[i].xyz;
    float bestT = uMaxDistance;
    vec3 bestN = vec3(0.0);
    bool hit = false;
    for (int t = 0; t < uTriCount; t++) {
        uint i0 = highIdx[t * 3u];
        uint i1 = highIdx[t * 3u + 1u];
        uint i2 = highIdx[t * 3u + 2u];
        vec3 a = highPos[i0].xyz;
        vec3 b = highPos[i1].xyz;
        vec3 c = highPos[i2].xyz;
        vec3 e1 = b - a;
        vec3 e2 = c - a;
        vec3 p = cross(d, e2);
        float det = dot(e1, p);
        if (abs(det) < 1e-8) continue;
        float inv = 1.0 / det;
        vec3 s = o - a;
        float u = dot(s, p) * inv;
        if (u < 0.0 || u > 1.0) continue;
        vec3 q = cross(s, e1);
        float v = dot(d, q) * inv;
        if (v < 0.0 || u + v > 1.0) continue;
        float tt = dot(e2, q) * inv;
        if (tt < 1e-4 || tt >= bestT) continue;
        bestT = tt;
        vec3 n0 = highNrm[i0].xyz;
        vec3 n1 = highNrm[i1].xyz;
        vec3 n2 = highNrm[i2].xyz;
        bestN = normalize(n0 * (1.0 - u - v) + n1 * u + n2 * v);
        hit = true;
    }
    if (hit) outData[i] = vec4(bestN, bestT);
    else outData[i] = vec4(0.0, 0.0, 0.0, -1.0);
}
";

	// ------------------------------------------------------------------ encode

	void WriteNormal( BakeSet set, Hit[] hits, TexelFrame[] frames, int width, int height, BakeOptions options )
	{
		var map = new BakedMap( width, height );
		var flip = options.FlipGreen;
		for ( var i = 0; i < hits.Length; i++ )
		{
			if ( !hits[i].HasHit ) continue;
			map.Filled[i] = true;
			var n = hits[i].Normal;
			var f = frames[i];
			var tx = Vec3.Dot( n, f.Tangent );
			var ty = Vec3.Dot( n, f.Bitangent );
			var tz = Vec3.Dot( n, f.Normal );
			var enc = new Vec3( tx * 0.5f + 0.5f, (flip ? ty : -ty) * 0.5f + 0.5f, tz * 0.5f + 0.5f );
			var ai = i * 3;
			map.Rgb[ai] = ToByte( enc.x );
			map.Rgb[ai + 1] = ToByte( enc.y );
			map.Rgb[ai + 2] = ToByte( enc.z );
			if ( options.SixteenBit )
			{
				map.EnableSixteenBit();
				map.Rgb16![ai] = ToUShort( enc.x );
				map.Rgb16[ai + 1] = ToUShort( enc.y );
				map.Rgb16[ai + 2] = ToUShort( enc.z );
			}
		}
		Dilate( map, options.Padding );
		set.Normal = MergeInto( set.Normal, map, width, height );
	}

	void WriteHeight( BakeSet set, Hit[] hits, int width, int height, BakeOptions options )
	{
		var map = new BakedMap( width, height );
		var min = float.MaxValue;
		var max = float.MinValue;
		foreach ( var h in hits )
		{
			if ( !h.HasHit ) continue;
			min = Math.Min( min, h.Distance );
			max = Math.Max( max, h.Distance );
		}
		if ( min == float.MaxValue ) { set.Height = MergeInto( set.Height, map, width, height ); return; }
		var range = Math.Max( Math.Abs( min ), Math.Abs( max ) );
		if ( range < 1e-6f ) range = 1e-6f;
		set.HeightMin = min;
		set.HeightMax = max;
		set.HeightRange = range;
		for ( var i = 0; i < hits.Length; i++ )
		{
			if ( !hits[i].HasHit ) continue;
			map.Filled[i] = true;
			var g = Math.Clamp( 0.5f + 0.5f * hits[i].Distance / range, 0f, 1f );
			var ai = i * 3;
			map.Rgb[ai] = map.Rgb[ai + 1] = map.Rgb[ai + 2] = ToByte( g );
			if ( options.SixteenBit )
			{
				map.EnableSixteenBit();
				map.Rgb16![ai] = map.Rgb16[ai + 1] = map.Rgb16[ai + 2] = ToUShort( g );
			}
		}
		Dilate( map, options.Padding );
		set.Height = MergeInto( set.Height, map, width, height );
	}

	/// <summary>Groups share one atlas: a later group's filled texels win, exactly as the CPU bake merges groups.</summary>
	static BakedMap MergeInto( BakedMap existing, BakedMap next, int width, int height )
	{
		if ( existing is null ) return next;
		for ( var i = 0; i < width * height; i++ )
		{
			if ( !next.Filled[i] ) continue;
			existing.Filled[i] = true;
			Array.Copy( next.Rgb, i * 3, existing.Rgb, i * 3, 3 );
			if ( next.Rgb16 is not null )
			{
				existing.EnableSixteenBit();
				Array.Copy( next.Rgb16, i * 3, existing.Rgb16!, i * 3, 3 );
			}
		}
		return existing;
	}

	/// <summary>Bleed filled texels outward so shader filtering across an island edge does not pick up black. Nearest filled neighbour, repeated.</summary>
	static void Dilate( BakedMap map, int iterations )
	{
		iterations = Math.Clamp( iterations, 0, 16 );
		for ( var pass = 0; pass < iterations; pass++ )
		{
			var changed = false;
			var copy = (byte[])map.Rgb.Clone();
			var filledCopy = (bool[])map.Filled.Clone();
			for ( var y = 0; y < map.Height; y++ )
			for ( var x = 0; x < map.Width; x++ )
			{
				var i = y * map.Width + x;
				if ( filledCopy[i] ) continue;
				foreach ( var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) } )
				{
					var nx = x + dx; var ny = y + dy;
					if ( nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height ) continue;
					var ni = ny * map.Width + nx;
					if ( !filledCopy[ni] ) continue;
					Array.Copy( copy, ni * 3, map.Rgb, i * 3, 3 );
					map.Filled[i] = true;
					changed = true;
					break;
				}
			}
			if ( !changed ) break;
		}
	}

	static byte ToByte( float v ) => (byte)Math.Clamp( (int)MathF.Round( Math.Clamp( v, 0f, 1f ) * 255f ), 0, 255 );
	static ushort ToUShort( float v ) => (ushort)Math.Clamp( (int)MathF.Round( Math.Clamp( v, 0f, 1f ) * 65535f ), 0, 65535 );
}
