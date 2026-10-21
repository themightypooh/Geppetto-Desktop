using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>GPU-only directional key-light depth pass, shared by EGL and the viewport.</summary>
public sealed class KeyShadowMap : IDisposable
{
	public const int Resolution = 2048;
	public static readonly Vector3 Direction = Vector3.Normalize( new Vector3( 0.6f, -0.7f, 0.9f ) );
	readonly GL _gl;
	readonly uint _framebuffer;
	readonly GlShader _shader;
	readonly int _mvp, _model, _skinned, _bones;
	public uint Texture { get; }
	public Matrix4x4 ViewProjection { get; private set; }
	public Vector3 LightDirection { get; set; } = Direction;
	public float SlopeBias { get; set; } = 2f;
	public float ConstantBias { get; set; } = 1.5f;
	public float BoundsPadding { get; set; } = 0.05f;
	public float WorldTexelSize { get; private set; }
	/// <summary>Depth range of the light's orthographic projection, in world units. The
	/// receiver bias is expressed in world units and converted with it, so a tighter
	/// fit does not silently shrink the bias and let surfaces self-shadow.</summary>
	public float DepthRange { get; private set; } = 1f;
	ViewportMesh _boundsMesh;
	float[] _boundsVertices;
	readonly Vector3[] _boneMin = new Vector3[MarionettePlayer.MaxGpuBones + 1];
	readonly Vector3[] _boneMax = new Vector3[MarionettePlayer.MaxGpuBones + 1];
	int _boundsBoneCount = -1;

	public KeyShadowMap( GL gl )
	{
		_gl = gl;
		_shader = new GlShader( gl, RendererShaders.VertexSrc, "#version 460 core\nvoid main() {}" );
		_mvp = _shader.Uniform( "uMvp" ); _model = _shader.Uniform( "uModel" ); _skinned = _shader.Uniform( "uSkinned" ); _bones = _shader.Uniform( "uBones[0]" );
		Texture = gl.GenTexture();
		gl.BindTexture( TextureTarget.Texture2D, Texture );
		unsafe { gl.TexImage2D( TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, Resolution, Resolution, 0, PixelFormat.DepthComponent, PixelType.Float, null ); }
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge );
		_framebuffer = gl.GenFramebuffer();
		gl.BindFramebuffer( FramebufferTarget.Framebuffer, _framebuffer );
		gl.FramebufferTexture2D( FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, Texture, 0 );
		gl.DrawBuffer( DrawBufferMode.None ); gl.ReadBuffer( ReadBufferMode.None );
		if ( gl.CheckFramebufferStatus( FramebufferTarget.Framebuffer ) != GLEnum.FramebufferComplete )
			throw new InvalidOperationException( "Key-light shadow framebuffer is incomplete." );
		gl.BindFramebuffer( FramebufferTarget.Framebuffer, 0 );
	}

	public void Draw( ViewportMesh mesh, MarionettePlayer player, LiveGeometry live = null )
	{
		bool skinned = mesh.HasSkin && player is not null;
		GetWorldBounds(mesh, player, skinned, out var minimum, out var maximum);
		if ( live is { HasCasters: true } ) { minimum = Vector3.Min( minimum, live.CasterMin ); maximum = Vector3.Max( maximum, live.CasterMax ); } // R9
		var centre = (minimum + maximum) * 0.5f;
		float radius = Math.Max((maximum - minimum).Length() * 0.5f, 1);
		var dir = LightDirection.LengthSquared() > 1e-4f ? Vector3.Normalize( LightDirection ) : Direction;
		var up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitZ)) > 0.98f ? Vector3.UnitY : Vector3.UnitZ;
		// Fixed light orientation and texel-snapped projected centre prevent subpixel
		// shadow swimming as the animated bounds translate through the world.
		var view = Matrix4x4.CreateLookAt( centre + dir * radius * 3, centre, up );
		var lightMin = new Vector3(float.PositiveInfinity);
		var lightMax = new Vector3(float.NegativeInfinity);
		TransformBounds(minimum, maximum, view, ref lightMin, ref lightMax);
		float padding = Math.Max(radius * Math.Clamp(BoundsPadding, 0.001f, 1f), 0.01f);
		float extent = Math.Max(lightMax.X - lightMin.X, lightMax.Y - lightMin.Y) + 2 * padding;
		// Quantised extent avoids continuously changing the world size of a texel.
		extent = MathF.Ceiling(extent * 16) / 16;
		WorldTexelSize = extent / Resolution;
		float cx = (lightMin.X + lightMax.X) * 0.5f;
		float cy = (lightMin.Y + lightMax.Y) * 0.5f;
		float worldCx = cx - view.M41, worldCy = cy - view.M42;
		cx += MathF.Round(worldCx / WorldTexelSize) * WorldTexelSize - worldCx;
		cy += MathF.Round(worldCy / WorldTexelSize) * WorldTexelSize - worldCy;
		float near = Math.Max(0.01f, -lightMax.Z - padding);
		float far = Math.Max(near + 0.1f, -lightMin.Z + padding);
		DepthRange = far - near;
		var projection = Matrix4x4.CreateOrthographicOffCenter(cx - extent * 0.5f, cx + extent * 0.5f,
			cy - extent * 0.5f, cy + extent * 0.5f, near, far);
		projection.M33 = -2f / (far - near);
		projection.M43 = -(far + near) / (far - near);
		ViewProjection = view * projection;
		_gl.BindTexture( TextureTarget.Texture2D, 0 );
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, _framebuffer );
		_gl.Viewport( 0, 0, Resolution, Resolution );
		_gl.Enable( EnableCap.DepthTest ); _gl.DepthMask( true );
		_gl.Enable( EnableCap.PolygonOffsetFill ); _gl.PolygonOffset( SlopeBias, ConstantBias );
		_gl.Clear( ClearBufferMask.DepthBufferBit );
		_shader.Use(); _shader.SetMatrix( _mvp, ViewProjection ); _shader.SetMatrix( _model, Matrix4x4.Identity );
		_shader.SetInt( _skinned, skinned ? 1 : 0 );
		if ( skinned ) _shader.SetMatrixArray( _bones, player.GpuBones );
		mesh.Draw( _gl );
		if ( live is { HasCasters: true } ) { _shader.SetInt( _skinned, 0 ); live.DrawDepth( _gl ); } // R9: live layers cast
		_gl.Disable( EnableCap.PolygonOffsetFill );
	}

	int _boundsMorphRevision = -1; // R10: rescan only when morphs rewrote the vertices
	void GetWorldBounds(ViewportMesh mesh, MarionettePlayer player, bool skinned, out Vector3 minimum, out Vector3 maximum)
	{
		int boneCount = skinned ? player.GpuBones.Length : 0;
		// Upload may release Vertices. BaseVertices is the retained bind snapshot;
		// synthetic upload-only meshes can retain neither, so keep a conservative fallback.
		var vertices = mesh.Vertices ?? mesh.BaseVertices;
		// A weighted vertex is a convex combination of bone-transformed positions.
		// Unioning each bone's influenced bind-space AABB is conservative even for
		// nonuniform scale and mixed influences, without CPU skinning every frame.
		// Morph snapshots mutate their vertex array, so refresh their bounds explicitly.
		if (!ReferenceEquals(mesh, _boundsMesh) || !ReferenceEquals(vertices, _boundsVertices)
			|| boneCount != _boundsBoneCount || (mesh.Morphs is not null && mesh.MorphRevision != _boundsMorphRevision))
		{
			Array.Fill(_boneMin, new Vector3(float.PositiveInfinity));
			Array.Fill(_boneMax, new Vector3(float.NegativeInfinity));
			for (int offset = 0; vertices is not null && offset < vertices.Length; offset += ViewportMesh.FloatsPerVertex)
			{
				var position = new Vector3(vertices[offset], vertices[offset + 1], vertices[offset + 2]);
				bool influenced = false;
				if (skinned)
					for (int influence = 0; influence < 4; influence++)
					{
						int bone = (int)vertices[offset + 6 + influence];
						if (vertices[offset + 10 + influence] <= 0 || bone < 0 || bone >= boneCount) continue;
						_boneMin[bone] = Vector3.Min(_boneMin[bone], position);
						_boneMax[bone] = Vector3.Max(_boneMax[bone], position);
						influenced = true;
					}
				if (!influenced)
				{
					_boneMin[MarionettePlayer.MaxGpuBones] = Vector3.Min(_boneMin[MarionettePlayer.MaxGpuBones], position);
					_boneMax[MarionettePlayer.MaxGpuBones] = Vector3.Max(_boneMax[MarionettePlayer.MaxGpuBones], position);
				}
			}
			if (vertices is null)
			{
				var lo = mesh.Centre - new Vector3(Math.Max(mesh.Radius, 1));
				var hi = mesh.Centre + new Vector3(Math.Max(mesh.Radius, 1));
				for (int bone = 0; bone < boneCount; bone++) { _boneMin[bone] = lo; _boneMax[bone] = hi; }
				if (boneCount == 0) { _boneMin[MarionettePlayer.MaxGpuBones] = lo; _boneMax[MarionettePlayer.MaxGpuBones] = hi; }
			}
			_boundsMesh = mesh; _boundsVertices = vertices; _boundsBoneCount = boneCount; _boundsMorphRevision = mesh.MorphRevision;
		}
		minimum = _boneMin[MarionettePlayer.MaxGpuBones];
		maximum = _boneMax[MarionettePlayer.MaxGpuBones];
		for (int bone = 0; bone < boneCount; bone++)
			if (_boneMin[bone].X <= _boneMax[bone].X)
				TransformBounds(_boneMin[bone], _boneMax[bone], player.GpuBones[bone], ref minimum, ref maximum);
		if (!float.IsFinite(minimum.Length()) || !float.IsFinite(maximum.Length()))
		{
			minimum = mesh.Centre - new Vector3(Math.Max(mesh.Radius, 1));
			maximum = mesh.Centre + new Vector3(Math.Max(mesh.Radius, 1));
		}
	}

	static void TransformBounds(Vector3 minimum, Vector3 maximum, Matrix4x4 matrix, ref Vector3 resultMin, ref Vector3 resultMax)
	{
		for (int corner = 0; corner < 8; corner++)
		{
			var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? minimum.X : maximum.X,
				(corner & 2) == 0 ? minimum.Y : maximum.Y, (corner & 4) == 0 ? minimum.Z : maximum.Z), matrix);
			resultMin = Vector3.Min(resultMin, point); resultMax = Vector3.Max(resultMax, point);
		}
	}

	public void Dispose()
	{
		_gl.DeleteFramebuffer( _framebuffer ); _gl.DeleteTexture( Texture ); _shader.Dispose();
	}
}
