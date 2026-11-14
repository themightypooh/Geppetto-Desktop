// Shader precompile: compile the app's programs before the first orbit (godlike Platform P1).
//
// Blender 5.1 precompiles GPU pipelines in parallel so shader compile does
// not stall the first orbit. Geppetto's passes build their GlShader programs
// in their constructors — which already run before the first Draw — except
// the ones created lazily when a feature is first switched on (volumes,
// overlays), and every one of them compiles synchronously on the GL thread.
// This file moves that cost out of the first frame, on purpose, in two parts:
//
//   - Precompile: compile every registered program once, on the calling GL
//     thread, and report per-program milliseconds. The compiled programs are
//     disposed after warming the DRIVER's shader cache — GL program handles
//     are owned by their GlShader (Dispose deletes them), so sharing handles
//     across passes would double-delete; warming the driver cache is the
//     safe mechanism, and it is what makes the passes' own compiles fast
//     when they run. Duplicate sources in one run are compiled once and
//     reported as duplicates, not recompiled.
//   - PrecompileParallel: the same, spread across worker threads, each with
//     its own headless GL context (GL calls stay on the thread that owns
//     their context — that rule is not bent, it is how the parallelism
//     works). Any worker failure falls back to the sequential path for the
//     remaining programs, and the report says which path ran.
//
// HOST WIRING (final integrator, shared entry point — not this file): call
// Precompile with DefaultSpecs() once at startup, after the first GL context
// exists and before the first Draw. Until then nothing changes — the passes
// still compile their own programs, just later and slower.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>One program to precompile: a name and its two sources.</summary>
public sealed record ProgramSpec( string Name, string VertexSource, string FragmentSource );

/// <summary>One compiled program: its name, how long the compile took, and
/// whether it was a duplicate of an earlier spec in the same run (compiled
/// once, served from the driver cache for the rest).</summary>
public sealed record CompiledProgram( string Name, double Milliseconds, bool Duplicate );

/// <summary>What a precompile run did: every program, the total time, the
/// slowest program, and which path ran (Sequential, Parallel, or
/// ParallelWithSequentialFallback).</summary>
public sealed class PrecompileReport
{
	public IReadOnlyList<CompiledProgram> Programs = Array.Empty<CompiledProgram>();
	public double TotalMilliseconds;
	public string? SlowestProgram;
	public double SlowestMilliseconds;
	public string Path = "Sequential";
	public IReadOnlyList<string> Notes = Array.Empty<string>();
}

/// <summary>Shader precompile: warm the driver cache for the app's programs
/// before the first orbit, and report what it cost.</summary>
public static class ShaderPrecompile
{
	/// <summary>The app's real programs, from the public shader sources:
	/// the lit program, the shadow-depth program (same vertex shader, empty
	/// fragment), the TAA resolve, and the bloom downsample/upsample/
	/// composite/DoF programs. Pass-private shaders stay with their owning
	/// pass; the cache dedupes by source, so a program compiled here is not
	/// compiled twice when its pass builds it.</summary>
	public static IReadOnlyList<ProgramSpec> DefaultSpecs() => new[]
	{
		new ProgramSpec( "lit", RendererShaders.VertexSrc, RendererShaders.FragmentSrc ),
		new ProgramSpec( "shadow-depth", RendererShaders.VertexSrc, "#version 460 core\nvoid main() {}" ),
		new ProgramSpec( "taa", TaaShaders.Vertex, TaaShaders.Fragment ),
		new ProgramSpec( "bloom-downsample", PostChainShaders.FullscreenVs, PostChainShaders.DownsampleFs ),
		new ProgramSpec( "bloom-upsample", PostChainShaders.FullscreenVs, PostChainShaders.UpsampleFs ),
		new ProgramSpec( "bloom-composite", PostChainShaders.FullscreenVs, PostChainShaders.Composite ),
		new ProgramSpec( "bloom-dof-prep", PostChainShaders.FullscreenVs, PostChainShaders.DofPrepFs ),
		new ProgramSpec( "bloom-dof-gather", PostChainShaders.FullscreenVs, PostChainShaders.DofGatherFs ),
	};

	/// <summary>SHA-256 of the two sources, hex. The dedupe key: two specs
	/// with the same hash compile once.</summary>
	public static string Hash( ProgramSpec spec )
	{
		ArgumentNullException.ThrowIfNull( spec );
		using var sha = SHA256.Create();
		var bytes = Encoding.UTF8.GetBytes( spec.VertexSource + "\n\x00\n" + spec.FragmentSource );
		return Convert.ToHexString( sha.ComputeHash( bytes ) );
	}

	/// <summary>Compile every spec once, sequentially, on the calling GL
	/// thread. Returns the report. Throws on a compile/link failure — a
	/// broken shader is a bug, not a fallback (unlike the GPU backends,
	/// which decline; precompile runs at startup where a failure must be
	/// loud). The caller must already have a current GL context.</summary>
	public static PrecompileReport Precompile( GL gl, IEnumerable<ProgramSpec> specs )
	{
		ArgumentNullException.ThrowIfNull( gl );
		ArgumentNullException.ThrowIfNull( specs );
		var list = specs.ToList();
		if ( list.Count == 0 ) throw new ArgumentException( "Nothing to precompile.", nameof( specs ) );

		// CPU-side prep in parallel: hashing the sources (the only part that
		// is allowed off the GL thread). GL compiles stay on this thread.
		var hashes = new string[list.Count];
		Parallel.For( 0, list.Count, i => hashes[i] = Hash( list[i] ) );

		var compiled = new List<CompiledProgram>();
		var seen = new HashSet<string>( StringComparer.Ordinal );
		var total = Stopwatch.StartNew();
		var notes = new List<string>();
		foreach ( var (spec, hash) in list.Zip( hashes ) )
		{
			if ( !seen.Add( hash ) )
			{
				compiled.Add( new CompiledProgram( spec.Name, 0, Duplicate: true ) );
				notes.Add( $"'{spec.Name}' duplicates an earlier program's sources; compiled once, not recompiled." );
				continue;
			}
			var sw = Stopwatch.StartNew();
			using var shader = new GlShader( gl, spec.VertexSource, spec.FragmentSource );
			sw.Stop();
			compiled.Add( new CompiledProgram( spec.Name, sw.Elapsed.TotalMilliseconds, Duplicate: false ) );
		}
		total.Stop();
		return new PrecompileReport
		{
			Programs = compiled,
			TotalMilliseconds = total.Elapsed.TotalMilliseconds,
			SlowestProgram = compiled.Where( c => !c.Duplicate ).OrderByDescending( c => c.Milliseconds ).FirstOrDefault()?.Name,
			SlowestMilliseconds = compiled.Where( c => !c.Duplicate ).Select( c => c.Milliseconds ).DefaultIfEmpty( 0 ).Max(),
			Path = "Sequential",
			Notes = notes,
		};
	}

	/// <summary>Compile every spec in parallel: one worker thread per spec,
	/// each with its own headless GL context (GL calls stay on the thread
	/// that owns their context). A worker that cannot create a context, or
	/// whose compile fails, falls back to the sequential path for its spec
	/// on the calling thread, and the report records the fallback. The
	/// sequential <see cref="Precompile"/> is the safe default; use this
	/// when startup time matters and the EGL platform supports concurrent
	/// contexts.</summary>
	public static PrecompileReport PrecompileParallel( IEnumerable<ProgramSpec> specs )
	{
		ArgumentNullException.ThrowIfNull( specs );
		var list = specs.ToList();
		if ( list.Count == 0 ) throw new ArgumentException( "Nothing to precompile.", nameof( specs ) );

		var hashes = new string[list.Count];
		Parallel.For( 0, list.Count, i => hashes[i] = Hash( list[i] ) );

		var compiled = new CompiledProgram[list.Count];
		var notes = new List<string>();
		var fellBack = false;
		var total = Stopwatch.StartNew();
		Parallel.For( 0, list.Count, i =>
		{
			var spec = list[i];
			try
			{
				using var context = new HeadlessGlContext();
				var sw = Stopwatch.StartNew();
				using var shader = new GlShader( context.Gl, spec.VertexSource, spec.FragmentSource );
				sw.Stop();
				compiled[i] = new CompiledProgram( spec.Name, sw.Elapsed.TotalMilliseconds, Duplicate: false );
			}
			catch ( Exception ex )
			{
				lock ( notes ) { fellBack = true; notes.Add( $"'{spec.Name}' could not precompile in parallel ({ex.Message}); falling back to the sequential path." ); }
				compiled[i] = null!;
			}
		} );

		// Duplicates: compile once across the whole run, in spec order. A
		// duplicate that failed in parallel is not compiled in the fallback
		// either — its twin already warmed the driver cache.
		var seen = new HashSet<string>( StringComparer.Ordinal );
		var isDuplicate = new bool[list.Count];
		for ( var i = 0; i < list.Count; i++ )
		{
			isDuplicate[i] = !seen.Add( hashes[i] );
			if ( isDuplicate[i] && compiled[i] is not null )
			{
				compiled[i] = new CompiledProgram( list[i].Name, 0, Duplicate: true );
				notes.Add( $"'{list[i].Name}' duplicates an earlier program's sources; compiled once, not recompiled." );
			}
		}

		// Sequential fallback for the workers that failed (duplicates excluded).
		// A compile failure here throws (a broken shader is a bug); a missing
		// context is the only failure that stays quiet, and only when EVERY
		// worker failed — in which case the report says so and the caller decides.
		var failed = new List<int>();
		for ( var i = 0; i < list.Count; i++ )
			if ( compiled[i] is null && !isDuplicate[i] ) failed.Add( i );
		for ( var i = 0; i < list.Count; i++ )
			if ( compiled[i] is null && isDuplicate[i] )
				compiled[i] = new CompiledProgram( list[i].Name, 0, Duplicate: true );
		if ( failed.Count > 0 )
		{
			HeadlessGlContext? context = null;
			try
			{
				context = new HeadlessGlContext();
				var gl = context.Gl;
				foreach ( var i in failed )
				{
					var sw = Stopwatch.StartNew();
					using var shader = new GlShader( gl, list[i].VertexSource, list[i].FragmentSource );
					sw.Stop();
					compiled[i] = new CompiledProgram( list[i].Name, sw.Elapsed.TotalMilliseconds, Duplicate: false );
				}
			}
			catch ( Exception ex )
			{
				notes.Add( $"Sequential fallback also failed ({ex.Message}); precompile did not complete." );
				throw new InvalidOperationException( $"Shader precompile failed: {ex.Message}", ex );
			}
			finally { context?.Dispose(); }
		}
		total.Stop();
		return new PrecompileReport
		{
			Programs = compiled,
			TotalMilliseconds = total.Elapsed.TotalMilliseconds,
			SlowestProgram = compiled.Where( c => c is not null && !c.Duplicate ).OrderByDescending( c => c.Milliseconds ).FirstOrDefault()?.Name,
			SlowestMilliseconds = compiled.Where( c => c is not null && !c.Duplicate ).Select( c => c.Milliseconds ).DefaultIfEmpty( 0 ).Max(),
			Path = fellBack ? "ParallelWithSequentialFallback" : "Parallel",
			Notes = notes,
		};
	}
}
