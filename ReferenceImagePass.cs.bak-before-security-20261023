#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// Draws a document's reference images (<see cref="PartStudio.ReferenceImages"/>) in the viewport:
/// one textured quad per picture, placed the way the s&amp;box editor places them
/// (EffigyViewport.ReferenceImages.cs) so a document looks the same in both.
///
/// ALWAYS BEHIND. The quads are drawn after the scene, into the caller's target, with depth test on
/// against the scene depth SceneRenderer copies back and every fragment pushed to the far plane: a
/// pixel the model covers keeps the model, an empty pixel shows the picture. So the picture sits
/// behind the model from every angle whatever its Depth, and it is composited in display colour,
/// after the tone map, so a photo looks like the photo.
///
/// Textures are cached by path and file time. PNG is decoded by the kernel's reader; anything else
/// (a JPEG photo) is converted through ImageMagick's `magick` when it is installed. A picture that
/// will not load is reported in <see cref="Problems"/> rather than silently missing.
/// </summary>
public sealed class ReferenceImagePass : IDisposable
{
	public const float MinHeight = 0.01f, MinOpacity = 0.05f;

	const string VertexSrc = @"#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUv;
uniform mat4 uViewProj;
out vec2 vUv;
void main() {
	vUv = aUv;
	vec4 p = uViewProj * vec4(aPos, 1.0);
	gl_Position = vec4(p.xy, p.w, p.w);   // far plane: only where the scene left depth clear
}
";

	const string FragmentSrc = @"#version 460 core
in vec2 vUv;
uniform sampler2D uImage;
uniform float uOpacity;
out vec4 FragColor;
void main() {
	vec4 c = texture(uImage, vUv);
	FragColor = vec4(c.rgb, c.a * uOpacity);
}
";

	sealed class Entry { public uint Texture; public int Width, Height; public DateTime Stamp; public string? Error; }

	readonly GL _gl;
	readonly GlShader _shader;
	readonly int _uViewProj, _uOpacity, _uImage;
	readonly uint _vao, _vbo;
	readonly Dictionary<string, Entry> _cache = new( StringComparer.Ordinal );
	readonly float[] _quad = new float[6 * 5];

	/// <summary>The pictures to draw, in the document's order (later ones draw over earlier ones).</summary>
	public List<ReferenceImage> Images { get; } = new();
	/// <summary>Relative paths resolve against this folder (the open document's), else the working directory.</summary>
	public string? BaseDirectory { get; set; }
	/// <summary>One line per picture that could not be drawn, refreshed by each <see cref="Draw"/>.</summary>
	public List<string> Problems { get; } = new();

	public unsafe ReferenceImagePass( GL gl )
	{
		_gl = gl;
		_shader = new GlShader( gl, VertexSrc, FragmentSrc );
		_uViewProj = _shader.Uniform( "uViewProj" );
		_uOpacity = _shader.Uniform( "uOpacity" );
		_uImage = _shader.Uniform( "uImage" );
		_vao = _gl.GenVertexArray();
		_vbo = _gl.GenBuffer();
		_gl.BindVertexArray( _vao );
		_gl.BindBuffer( BufferTargetARB.ArrayBuffer, _vbo );
		_gl.BufferData( BufferTargetARB.ArrayBuffer, (nuint)(_quad.Length * sizeof( float )), null, BufferUsageARB.DynamicDraw );
		_gl.EnableVertexAttribArray( 0 );
		_gl.VertexAttribPointer( 0, 3, VertexAttribPointerType.Float, false, 5 * sizeof( float ), (void*)0 );
		_gl.EnableVertexAttribArray( 1 );
		_gl.VertexAttribPointer( 1, 2, VertexAttribPointerType.Float, false, 5 * sizeof( float ), (void*)(3 * sizeof( float )) );
		_gl.BindVertexArray( 0 );
	}

	/// <summary>The plane's own axes: right and up on the picture, and the way it faces (toward its viewer).</summary>
	public static (Vector3 Right, Vector3 Up, Vector3 Facing) Axes( string view ) => view switch
	{
		"side" => (Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY),
		"top" => (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
		_ => (-Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
	};

	/// <summary>The four corners (bottom-left, bottom-right, top-right, top-left) of a picture of this aspect.</summary>
	public static Vector3[] Corners( ReferenceImage image, float aspect )
	{
		var (right, up, facing) = Axes( image.View );
		float h = MathF.Max( image.Height, MinHeight ), w = h * aspect;
		var c = facing * -image.Depth + right * image.Offset.x + up * image.Offset.y;
		return new[] { c - right * w / 2 - up * h / 2, c + right * w / 2 - up * h / 2, c + right * w / 2 + up * h / 2, c - right * w / 2 + up * h / 2 };
	}

	/// <summary>The box the shown pictures cover; null when none is shown.</summary>
	public (Vector3 Min, Vector3 Max)? Bounds()
	{
		Vector3 min = new( float.MaxValue ), max = new( float.MinValue );
		bool any = false;
		foreach ( var image in Images )
		{
			if ( image is null || !image.Visible || string.IsNullOrWhiteSpace( image.Path ) ) continue;
			foreach ( var c in Corners( image, Aspect( image ) ) ) { min = Vector3.Min( min, c ); max = Vector3.Max( max, c ); }
			any = true;
		}
		return any ? (min, max) : null;
	}

	public string Resolve( string path ) =>
		Path.IsPathRooted( path ) ? path : Path.GetFullPath( Path.Combine( BaseDirectory ?? Directory.GetCurrentDirectory(), path ) );

	/// <summary>Width over height of the picture, loading it if needed; 1 when it will not load.</summary>
	public float Aspect( ReferenceImage image ) => Load( image.Path ) is { Error: null, Height: > 0 } e ? e.Width / (float)e.Height : 1f;

	/// <summary>Draws into the bound framebuffer, which must hold the scene's depth.</summary>
	public unsafe void Draw( Matrix4x4 viewProj )
	{
		Problems.Clear();
		if ( Images.Count == 0 ) return;
		_shader.Use();
		_shader.SetMatrix( _uViewProj, viewProj );
		_gl.Uniform1( _uImage, 0 );
		_gl.Enable( EnableCap.DepthTest );
		_gl.DepthFunc( DepthFunction.Lequal );
		_gl.DepthMask( false );
		_gl.Disable( EnableCap.CullFace );
		_gl.Enable( EnableCap.Blend );
		_gl.BlendFuncSeparate( BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.Zero, BlendingFactor.One );
		_gl.BindVertexArray( _vao );
		_gl.BindBuffer( BufferTargetARB.ArrayBuffer, _vbo );
		_gl.ActiveTexture( TextureUnit.Texture0 );
		foreach ( var image in Images )
		{
			if ( image is null || !image.Visible || string.IsNullOrWhiteSpace( image.Path ) ) continue;
			var entry = Load( image.Path );
			if ( entry.Error is not null ) { Problems.Add( entry.Error ); continue; }
			var k = Corners( image, entry.Width / (float)Math.Max( 1, entry.Height ) );
			// v runs down the picture: its top row at the plane's top
			Vertex( 0, k[0], 0, 1 ); Vertex( 1, k[1], 1, 1 ); Vertex( 2, k[2], 1, 0 );
			Vertex( 3, k[0], 0, 1 ); Vertex( 4, k[2], 1, 0 ); Vertex( 5, k[3], 0, 0 );
			fixed ( float* p = _quad )
				_gl.BufferSubData( BufferTargetARB.ArrayBuffer, 0, (nuint)(_quad.Length * sizeof( float )), p );
			_gl.BindTexture( TextureTarget.Texture2D, entry.Texture );
			_gl.Uniform1( _uOpacity, Math.Clamp( image.Opacity, MinOpacity, 1f ) );
			_gl.DrawArrays( PrimitiveType.Triangles, 0, 6 );
		}
		_gl.BindVertexArray( 0 );
		_gl.Disable( EnableCap.Blend );
		_gl.DepthMask( true );
		_gl.DepthFunc( DepthFunction.Less );
	}

	void Vertex( int i, Vector3 p, float u, float v )
	{
		int o = i * 5;
		_quad[o] = p.X; _quad[o + 1] = p.Y; _quad[o + 2] = p.Z; _quad[o + 3] = u; _quad[o + 4] = v;
	}

	Entry Load( string path )
	{
		string full = Resolve( path );
		DateTime stamp = File.Exists( full ) ? File.GetLastWriteTimeUtc( full ) : DateTime.MinValue;
		if ( _cache.TryGetValue( full, out var cached ) && cached.Stamp == stamp ) return cached;
		if ( cached is not null && cached.Texture != 0 ) _gl.DeleteTexture( cached.Texture );
		var entry = new Entry { Stamp = stamp };
		_cache[full] = entry;
		try
		{
			if ( stamp == DateTime.MinValue ) throw new FileNotFoundException( "no file there" );
			var (rgba, w, h) = Decode( full );
			entry.Width = w; entry.Height = h;
			entry.Texture = Upload( rgba, w, h );
		}
		catch ( Exception ex ) when ( ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception )
		{
			entry.Error = $"Can't show reference image '{Path.GetFileName( full )}': {ex.Message}";
		}
		return entry;
	}

	/// <summary>RGBA, row 0 at the top.</summary>
	public static (byte[] Rgba, int Width, int Height) Decode( string path )
	{
		var bytes = File.ReadAllBytes( path );
		if ( bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G' )
		{
			try { return PngReader.Read( bytes ); }
			catch ( InvalidDataException ) { /* paletted or interlaced: let magick flatten it */ }
		}
		// A JPEG/WebP/paletted PNG photo: magick writes a plain 8-bit RGBA PNG the kernel reader takes.
		var start = new ProcessStartInfo( "magick", new[] { path, "-auto-orient", "-depth", "8", "PNG32:-" } )
			{ RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		Process? process;
		try { process = Process.Start( start ); }
		catch ( System.ComponentModel.Win32Exception ) { throw new InvalidDataException( "only PNG reads without ImageMagick (magick) installed; save it as PNG" ); }
		using ( process )
		{
			using var output = new MemoryStream();
			var err = process!.StandardError.ReadToEndAsync();
			process.StandardOutput.BaseStream.CopyTo( output );
			process.WaitForExit();
			if ( process.ExitCode != 0 || output.Length == 0 )
				throw new InvalidDataException( "not a picture magick can read (" + err.Result.Split( '\n' )[0].Trim() + ")" );
			return PngReader.Read( output.ToArray() );
		}
	}

	unsafe uint Upload( byte[] rgba, int w, int h )
	{
		uint tex = _gl.GenTexture();
		_gl.ActiveTexture( TextureUnit.Texture0 );
		_gl.BindTexture( TextureTarget.Texture2D, tex );
		_gl.PixelStore( PixelStoreParameter.UnpackAlignment, 1 );
		fixed ( byte* p = rgba )
			_gl.TexImage2D( TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p );
		_gl.GenerateMipmap( TextureTarget.Texture2D );
		_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear );
		_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear );
		_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge );
		_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge );
		return tex;
	}

	public void Dispose()
	{
		foreach ( var e in _cache.Values ) if ( e.Texture != 0 ) _gl.DeleteTexture( e.Texture );
		_cache.Clear();
		_gl.DeleteBuffer( _vbo ); _gl.DeleteVertexArray( _vao );
		_shader.Dispose();
	}
}
