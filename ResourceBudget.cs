using System;
using System.Threading;

namespace Marionette.Desktop;

/// <summary>
/// M4 resource budget for one loaded viewport mesh: decoded CPU bytes, GPU texture storage,
/// stage timings, batch count and process-wide retained resource counts across repeated reloads.
///
/// The budget is written only by material resolution (<see cref="ViewportMesh.ApplyMaterials"/>)
/// and GL upload/release (<see cref="ViewportMesh.Upload"/>, <see cref="ViewportMesh.ReleaseGpu"/>).
/// The draw path reads nothing here and writes nothing here: a steady-state frame performs no
/// texture decode and no material lookup, which the resource-budget tests assert by capturing
/// the counters before and after rendering frames.
/// </summary>
public sealed class MeshResourceBudget
{
	static long _liveTextures;
	static long _liveTextureBytes;
	static long _reloadCycles;

	/// <summary>GPU textures currently allocated by any live mesh; reload tests assert it returns to baseline.</summary>
	public static long LiveGpuTextures => Interlocked.Read( ref _liveTextures );
	/// <summary>GPU bytes currently allocated by any live mesh.</summary>
	public static long LiveGpuTextureBytes => Interlocked.Read( ref _liveTextureBytes );
	/// <summary>Completed upload/release cycles observed process-wide.</summary>
	public static long ReloadCycles => Interlocked.Read( ref _reloadCycles );

	/// <summary>Material batches in this mesh; each batch is one draw call.</summary>
	public int BatchCount { get; private set; }
	/// <summary>Distinct decoded RGBA images held on the CPU after material resolution.</summary>
	public int DecodedImageCount { get; private set; }
	/// <summary>Sum of decoded RGBA byte[] lengths, counting an image shared by batches once.</summary>
	public long DecodedBytes { get; private set; }
	/// <summary>RGBA8/sRGB8 storage the driver holds for this mesh's textures.</summary>
	public long GpuTextureBytes { get; private set; }
	/// <summary>Texture objects this mesh owns (fallbacks plus per-batch maps).</summary>
	public int GpuTextureCount { get; private set; }
	/// <summary>Document load time handed to the mesh factory.</summary>
	public double LoadSeconds { get; private set; }
	/// <summary>Wall time spent resolving and decoding material textures.</summary>
	public double DecodeSeconds { get; private set; }
	/// <summary>Wall time spent uploading VBO/EBO/textures to the driver.</summary>
	public double UploadSeconds { get; private set; }

	/// <summary>Texture decodes performed so far. Frozen after upload; draws must not raise it.</summary>
	public int DecodeCount { get; private set; }

	internal void RecordLoad( double loadSeconds ) => LoadSeconds = loadSeconds;

	internal void RecordDecode( double seconds, int batchCount, long decodedBytes, int imageCount, int decodeCount )
	{
		DecodeSeconds = seconds;
		BatchCount = batchCount;
		DecodedBytes = decodedBytes;
		DecodedImageCount = imageCount;
		DecodeCount += decodeCount;
	}

	internal void RecordUpload( double seconds, long bytes, int textures )
	{
		UploadSeconds = seconds;
		GpuTextureBytes = bytes;
		GpuTextureCount = textures;
		Interlocked.Add( ref _liveTextures, textures );
		Interlocked.Add( ref _liveTextureBytes, bytes );
	}

	internal void RecordRelease()
	{
		Interlocked.Add( ref _liveTextures, -GpuTextureCount );
		Interlocked.Add( ref _liveTextureBytes, -GpuTextureBytes );
		Interlocked.Increment( ref _reloadCycles );
		GpuTextureCount = 0;
		GpuTextureBytes = 0;
	}
}
