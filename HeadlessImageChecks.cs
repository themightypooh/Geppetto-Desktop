using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>Owned, windowless fixtures rendered through the production SceneRenderer. Coordinates are GL bottom-left.</summary>
public static class HeadlessImageChecks
{
	public const int Width = 384, Height = 384;
	public const float Exposure = 1f;
	public readonly record struct Pixel(byte R, byte G, byte B, byte A)
	{
		public double Luma => .2126 * R + .7152 * G + .0722 * B;
	}
	public sealed class Image
	{
		readonly byte[] _rgba;
		internal Image(byte[] rgba) => _rgba = rgba;
		public Pixel At(int x, int y)
		{
			if ((uint)x >= Width || (uint)y >= Height) throw new ArgumentOutOfRangeException();
			int i = (y * Width + x) * 4;
			return new(_rgba[i], _rgba[i + 1], _rgba[i + 2], _rgba[i + 3]);
		}
		public double MeanLuma(int x, int y, int halfWidth = 3)
		{
			double sum = 0;
			for (int j = -halfWidth; j <= halfWidth; j++)
			for (int i = -halfWidth; i <= halfWidth; i++) sum += At(x + i, y + j).Luma;
			return sum / ((2 * halfWidth + 1) * (2 * halfWidth + 1));
		}
		public int ClippedChannels()
		{
			int count = 0;
			for (int i = 0; i < _rgba.Length; i += 4)
				for (int c = 0; c < 3; c++) if (_rgba[i + c] == 255) count++;
			return count;
		}
		public void SavePpm(string path)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
			using var stream = File.Create(path);
			using var writer = new BinaryWriter(stream);
			writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{Width} {Height}\n255\n"));
			for (int y = Height - 1; y >= 0; y--)
			for (int x = 0; x < Width; x++)
			{
				var p = At(x, y);
				writer.Write(p.R); writer.Write(p.G); writer.Write(p.B);
			}
		}
	}

	public sealed class Fixture
	{
		readonly List<float> _vertices = new();
		readonly List<uint> _indices = new();
		readonly List<ViewportMesh.DrawBatch> _batches = new();
		public Fixture Quad(string name, Vector3 center, Vector3 right, Vector3 up, Vector3 normal, MaterialPalette.Entry material)
		{
			int offset = _indices.Count;
			uint first = (uint)(_vertices.Count / ViewportMesh.FloatsPerVertex);
			var positions = new[] { center - right - up, center + right - up, center + right + up, center - right + up };
			var uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
			var tangent = Vector3.Normalize(right);
			for (int corner = 0; corner < 4; corner++)
			{
				var p = positions[corner]; var uv = uvs[corner];
				_vertices.AddRange(new[] { p.X, p.Y, p.Z, normal.X, normal.Y, normal.Z,
					0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f,
					uv.X, uv.Y, tangent.X, tangent.Y, tangent.Z, 1f });
			}
			_indices.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
			_batches.Add(new ViewportMesh.DrawBatch(name, material, offset * sizeof(uint), 6));
			return this;
		}
		public ViewportMesh Mesh() => new()
		{
			Vertices = _vertices.ToArray(), Indices = _indices.ToArray(), Batches = _batches.ToArray(),
			Centre = Vector3.Zero, Radius = 3f, TriangleCount = _indices.Count / 3
		};
	}

	public static OrbitCamera Camera() => new()
	{
		Target = Vector3.Zero, Distance = 8, YawDegrees = 35, PitchDegrees = 70,
		FovDegrees = 45, Near = .1f, Far = 40
	};

	public static (int X, int Y) Project(OrbitCamera camera, Vector3 point)
	{
		var clip = Vector4.Transform(new Vector4(point, 1), camera.ViewMatrix() * camera.ProjectionMatrix(1));
		if (clip.W <= 0) throw new ArgumentException("Probe is behind the camera.");
		return ((int)Math.Round((clip.X / clip.W * .5f + .5f) * Width),
			(int)Math.Round((clip.Y / clip.W * .5f + .5f) * Height));
	}

	public static unsafe Image Render(HeadlessGlContext context, SceneRenderer renderer, ViewportMesh mesh,
		OrbitCamera camera, float exposure = Exposure)
	{
		if (!float.IsFinite(exposure) || exposure <= 0) throw new ArgumentOutOfRangeException(nameof(exposure));
		var gl = context.Gl;
		using var target = new CaptureTarget(gl, Width, Height);
		mesh.Upload(gl);
		try
		{
			renderer.Draw(mesh, null!, camera, Width, Height, exposure, target.Handle);
			gl.Finish(); // verification only; not a render-loop readback
			if (gl.GetError() is var drawError && drawError != GLEnum.NoError)
				throw new InvalidOperationException($"EGL draw GL error: {drawError}");
			var rgba = new byte[Width * Height * 4];
			gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.Handle);
			gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
			gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
			fixed (byte* ptr = rgba)
				gl.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
			if (gl.GetError() is var readError && readError != GLEnum.NoError)
				throw new InvalidOperationException($"EGL readback GL error: {readError}");
			return new Image(rgba);
		}
		finally { mesh.ReleaseGpu(gl); }
	}

	public static void SaveMetadata(string path, object probes)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		File.WriteAllText(path, JsonSerializer.Serialize(new
		{
			fixture = "V3 flat-material quads and receiver/caster; Z up, inches", resolution = new[] { Width, Height },
			camera = new { target = new[] { 0, 0, 0 }, distance = 8, yawDegrees = 35, pitchDegrees = 70,
				fovDegrees = 45, near = .1, far = 40 }, exposure = Exposure,
			keyDirection = KeyShadowMap.Direction, shadowResolution = KeyShadowMap.Resolution,
			limitations = "This fixture probes flat channels only; UV/texture/normal maps and skinned casters are not asserted here.", probes
		}, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
	}
}
