using System;
using System.Diagnostics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>R10: per-pass CPU time, GPU time (timestamp queries read back a few frames later so
/// nothing stalls) and managed allocation for <see cref="SceneRenderer"/>. Allocation-free.</summary>
public sealed class RenderStats : IDisposable
{
	const int Latency = 4;
	static readonly int Kinds = Enum.GetValues<RenderPassKind>().Length;
	readonly GL _gl;
	readonly uint[] _queries; // [frame % Latency][kind][begin,end]
	readonly bool[] _issued;
	readonly long[] _cpuStart, _allocStart;
	int _frame;

	/// <summary>Last frame's CPU milliseconds per <see cref="RenderPassKind"/>.</summary>
	public double[] CpuMs { get; }
	/// <summary>GPU milliseconds per pass from <see cref="Latency"/> frames ago (0 until available).</summary>
	public double[] GpuMs { get; }
	/// <summary>Managed bytes allocated on the render thread per pass, last frame.</summary>
	public long[] AllocatedBytes { get; }
	/// <summary>Total managed bytes allocated by <see cref="SceneRenderer.Draw"/> last frame.</summary>
	public long FrameAllocatedBytes { get; private set; }
	public bool GpuTimingEnabled { get; set; } = true;

	public RenderStats( GL gl )
	{
		_gl = gl;
		_queries = new uint[Latency * Kinds * 2];
		gl.GenQueries( (uint)_queries.Length, _queries );
		_issued = new bool[Latency * Kinds];
		_cpuStart = new long[Kinds]; _allocStart = new long[Kinds];
		CpuMs = new double[Kinds]; GpuMs = new double[Kinds]; AllocatedBytes = new long[Kinds];
	}

	int Slot( RenderPassKind kind ) => ( _frame % Latency ) * Kinds + (int)kind;

	public void BeginFrame()
	{
		// Read the slot we are about to overwrite: issued Latency frames ago.
		if ( !GpuTimingEnabled ) return;
		for ( int k = 0; k < Kinds; k++ )
		{
			int s = ( _frame % Latency ) * Kinds + k;
			if ( !_issued[s] ) continue;
			// Must read before reissuing this slot; only blocks if the GPU is Latency frames behind.
			_gl.GetQueryObject( _queries[s * 2], QueryObjectParameterName.Result, out ulong t0 );
			_gl.GetQueryObject( _queries[s * 2 + 1], QueryObjectParameterName.Result, out ulong t1 );
			GpuMs[k] = ( t1 - t0 ) / 1e6;
			_issued[s] = false;
		}
	}

	public void Begin( RenderPassKind kind )
	{
		int k = (int)kind;
		_allocStart[k] = GC.GetAllocatedBytesForCurrentThread();
		_cpuStart[k] = Stopwatch.GetTimestamp();
		if ( GpuTimingEnabled ) _gl.QueryCounter( _queries[Slot( kind ) * 2], QueryCounterTarget.Timestamp );
	}

	public void End( RenderPassKind kind )
	{
		int k = (int)kind;
		if ( GpuTimingEnabled ) { _gl.QueryCounter( _queries[Slot( kind ) * 2 + 1], QueryCounterTarget.Timestamp ); _issued[Slot( kind )] = true; }
		CpuMs[k] = ( Stopwatch.GetTimestamp() - _cpuStart[k] ) * 1000.0 / Stopwatch.Frequency;
		AllocatedBytes[k] = GC.GetAllocatedBytesForCurrentThread() - _allocStart[k];
	}

	public void EndFrame( long frameAllocated )
	{
		FrameAllocatedBytes = frameAllocated;
		_frame++;
	}

	public void Dispose() => _gl.DeleteQueries( (uint)_queries.Length, _queries );
}
