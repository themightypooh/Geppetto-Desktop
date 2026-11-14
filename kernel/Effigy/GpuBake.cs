using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Effigy;

/// <summary>
/// GPU bake job: the GPU path for the bake suite, on top of <see cref="Baker"/>.
///
/// A GPU backend is a kernel-side interface (<see cref="IGpuBakeBackend"/>),
/// exactly like <see cref="IMeshBoolean"/>: the kernel stays engine-free and a
/// host installs the real GL implementation (<c>GpuBakeComputeBackend</c> in the
/// Desktop app). The job itself is the honest part:
///
///   - 1K test-bake first: the GPU path is only trusted for a job after its
///     1K test-bake matches the CPU reference (<see cref="BakeJob.Run"/>) within
///     tolerance, per map. A GPU result that diverges is discarded, not shipped.
///   - Per-area rebake: <see cref="BakeJob.PlanRebake"/> fingerprints decide
///     which maps are stale; unchanged maps are reused from the previous set.
///   - Per-map fallback: a backend bakes the maps it supports and leaves the
///     rest null; the job fills those from the CPU bake and says so in the route.
///   - 16-bit flows through <see cref="BakeOptions.SixteenBit"/> untouched.
///
/// The 4K final never runs the CPU reference (that would erase the point of
/// the GPU path): it runs on the test-bake's verdict. The Marmoset-class
/// acceptance (4K normal+AO+curvature from multi-million-poly source in &lt;~60 s)
/// is a measurement for the final build's perf harness, not a claim made here.
/// </summary>
public static class GpuBake
{
	/// <summary>
	/// A GPU bake backend. Returns false with a reason when it declines a job
	/// (no GL context, over its triangle/texel ceiling, unsupported maps) — a
	/// decline is an ordinary outcome, not an error, and the job falls back to
	/// the CPU bake. A backend bakes the maps it supports and leaves the rest
	/// of the set null; the job merges in CPU maps for those.
	/// </summary>
	public interface IGpuBakeBackend
	{
		bool TryBake( BakeJob job, BakeMaps maps, out BakeSet set, out string why );
	}

	/// <summary>The installed GPU backend, or null when none is installed. A host that never installs one gets the CPU bake, silently.</summary>
	public static IGpuBakeBackend Provider { get; set; }

	public static bool Available => Provider is not null;

	/// <summary>What a GPU bake run produced, and which path each map took.</summary>
	public sealed class GpuBakeResult
	{
		public BakeSet Set;
		public string Route = "";
		public Dictionary<string, (double Mean, double Max)> Deviations = new();
		public bool TestPassed;
		public bool ReusedPrevious;
		public string Report = "";
		public TimeSpan Duration;
		public bool Succeeded => Set is not null;
	}

	/// <summary>
	/// One GPU bake job: a <see cref="BakeJob"/> plus the test-then-final policy.
	/// <paramref name="testSize"/> is the 1K test-bake (1024); <paramref name="finalSize"/>
	/// is the 4K final (4096). Tolerances are mean / max absolute deviation in
	/// 0..255 grey steps over filled texels, per map.
	/// </summary>
	public sealed class GpuBakeJob
	{
		public BakeJob Job { get; }
		public int TestSize { get; }
		public int FinalSize { get; }
		public double MeanTolerance { get; }
		public double MaxTolerance { get; }

		bool? _testPassed;
		Dictionary<string, (double Mean, double Max)> _testDeviations = new();

		public GpuBakeJob( BakeJob job, int testSize = 1024, int finalSize = 4096, double meanTolerance = 2.0, double maxTolerance = 12.0 )
		{
			Job = job ?? throw new ArgumentNullException( nameof( job ) );
			if ( !IsBakeSize( testSize ) ) throw new ArgumentException( $"Test-bake size {testSize} is not a power of two between 16 and 8192.", nameof( testSize ) );
			if ( !IsBakeSize( finalSize ) ) throw new ArgumentException( $"Final-bake size {finalSize} is not a power of two between 16 and 8192.", nameof( finalSize ) );
			TestSize = testSize;
			FinalSize = finalSize;
			MeanTolerance = meanTolerance;
			MaxTolerance = maxTolerance;
		}

		static bool IsBakeSize( int size ) => size >= 16 && size <= 8192 && (size & (size - 1)) == 0;

		/// <summary>
		/// The 1K test-bake: CPU reference and GPU side by side, compared per map.
		/// The GPU path is accepted only when every map is inside tolerance.
		/// </summary>
		public GpuBakeResult TestBake( IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			var testJob = new BakeJob( Job.Groups, Job.Maps, TestSize, TestSize, Job.Options );
			var cpu = testJob.Run( progress, cancel );
			if ( cpu is null || !cpu.Succeeded )
				return new GpuBakeResult { Report = $"CPU test-bake failed: {cpu?.Error ?? "no result"}", Duration = clock.Elapsed };

			if ( Provider is null )
			{
				_testPassed = false;
				return new GpuBakeResult { Set = cpu.Set, Route = "cpu (no gpu backend installed)", TestPassed = false, Report = cpu.Report, Duration = clock.Elapsed };
			}

			if ( !Provider.TryBake( testJob, Job.Maps, out var gpu, out var why ) || gpu is null )
			{
				_testPassed = false;
				return new GpuBakeResult { Set = cpu.Set, Route = $"cpu (gpu declined: {why ?? "no reason given"})", TestPassed = false, Report = cpu.Report, Duration = clock.Elapsed };
			}

			var deviations = CompareMaps( gpu, cpu.Set, Job.Maps );
			_testDeviations = deviations;
			var passed = deviations.Count > 0 && deviations.Values.All( d => d.Mean <= MeanTolerance && d.Max <= MaxTolerance );
			_testPassed = passed;
			if ( !passed )
			{
				var worst = deviations.OrderByDescending( d => d.Value.Max ).First();
				return new GpuBakeResult
				{
					Set = cpu.Set,
					Route = $"cpu (gpu diverged on {worst.Key}: mean {worst.Value.Mean:F2} max {worst.Value.Max:F2})",
					Deviations = deviations,
					TestPassed = false,
					Report = cpu.Report,
					Duration = clock.Elapsed,
				};
			}
			return new GpuBakeResult
			{
				Set = gpu,
				Route = "gpu (test-bake matches cpu within tolerance)",
				Deviations = deviations,
				TestPassed = true,
				Report = gpu.Report,
				Duration = clock.Elapsed,
			};
		}

		/// <summary>
		/// The final bake at <see cref="FinalSize"/>. Runs the test-bake first when it
		/// has not run yet. When the GPU path passed, the final runs on the GPU and
		/// any map the backend leaves null is filled from a CPU bake of just that
		/// map. Otherwise the whole final is the CPU bake.
		/// </summary>
		public GpuBakeResult FinalBake( BakeJobResult previous = null, IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			if ( _testPassed is null )
			{
				var test = TestBake( progress, cancel );
				if ( !test.TestPassed )
					return FinalCpu( previous, test.Route, clock );
			}
			if ( _testPassed == false )
				return FinalCpu( previous, "cpu (gpu test-bake did not pass)", clock );

			var finalJob = new BakeJob( Job.Groups, Job.Maps, FinalSize, FinalSize, Job.Options );
			string why = null!;
			if ( Provider is null || !Provider.TryBake( finalJob, Job.Maps, out var gpu, out why ) || gpu is null )
				return FinalCpu( previous, $"cpu (gpu declined the final bake: {why ?? "no reason given"})", clock );

			var missing = MissingMaps( gpu, Job.Maps );
			if ( missing == BakeMaps.None )
				return new GpuBakeResult { Set = gpu, Route = "gpu", Deviations = _testDeviations, TestPassed = true, Report = gpu.Report, Duration = clock.Elapsed };

			var cpuFill = new BakeJob( Job.Groups, missing, FinalSize, FinalSize, Job.Options ).Run( progress, cancel );
			if ( cpuFill is null || !cpuFill.Succeeded )
				return new GpuBakeResult { Set = gpu, Route = $"gpu + cpu fill FAILED ({cpuFill?.Error ?? "no result"})", Deviations = _testDeviations, TestPassed = true, Report = gpu.Report, Duration = clock.Elapsed };
			var merged = MergeSets( gpu, cpuFill.Set, Job.Maps );
			return new GpuBakeResult
			{
				Set = merged,
				Route = $"gpu + cpu ({MapNames( missing )} filled from cpu)",
				Deviations = _testDeviations,
				TestPassed = true,
				Report = gpu.Report,
				Duration = clock.Elapsed,
			};
		}

		/// <summary>
		/// Per-area rebake: only the maps <see cref="BakeJob.PlanRebake"/> flags as
		/// stale are baked; the rest are reused from <paramref name="previous"/>.
		/// A null or failed previous bakes everything.
		/// </summary>
		public GpuBakeResult Run( BakeJobResult previous, IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			if ( previous is null || previous.Set is null )
				return FinalBake( null, progress, cancel );
			var needed = Job.PlanRebake( previous );
			if ( needed == BakeMaps.None )
				return new GpuBakeResult
				{
					Set = previous.Set,
					Route = "reused previous (nothing changed)",
					Deviations = _testDeviations,
					TestPassed = _testPassed ?? false,
					ReusedPrevious = true,
					Report = previous.Report,
					Duration = clock.Elapsed,
				};
			var partial = new GpuBakeJob( new BakeJob( Job.Groups, needed, Job.Width, Job.Height, Job.Options ), TestSize, FinalSize, MeanTolerance, MaxTolerance );
			var result = partial.FinalBake( null, progress, cancel );
			if ( result is null || !result.Succeeded )
				return result ?? new GpuBakeResult { Report = "Per-area rebake produced nothing.", Duration = clock.Elapsed };
			var merged = MergeSets( result.Set, previous.Set, Job.Maps );
			result.Set = merged;
			result.Route += " + reused unchanged maps";
			result.Duration = clock.Elapsed;
			return result;
		}

		GpuBakeResult FinalCpu( BakeJobResult previous, string route, System.Diagnostics.Stopwatch clock )
		{
			var cpu = Job.Run( previous, null, default );
			if ( cpu is null )
				return new GpuBakeResult { Report = "CPU bake produced nothing.", Duration = clock.Elapsed };
			return new GpuBakeResult
			{
				Set = cpu.Set,
				Route = route,
				Deviations = _testDeviations,
				TestPassed = false,
				ReusedPrevious = cpu.ReusedPrevious,
				Report = cpu.Report,
				Duration = clock.Elapsed,
			};
		}

		/// <summary>Mean / max absolute deviation per map, 0..255 grey steps, over texels filled in both maps.</summary>
		public static Dictionary<string, (double Mean, double Max)> CompareMaps( BakeSet gpu, BakeSet cpu, BakeMaps maps )
		{
			var result = new Dictionary<string, (double Mean, double Max)>();
			if ( gpu is null || cpu is null ) return result;
			foreach ( var (name, gpuMap) in gpu.Maps )
			{
				var flag = MapFlag( name );
				if ( flag == BakeMaps.None || (maps & flag) == 0 ) continue;
				var cpuMap = MapByName( cpu, name );
				if ( cpuMap is null || gpuMap.Width != cpuMap.Width || gpuMap.Height != cpuMap.Height ) continue;
				var sum = 0.0;
				var max = 0.0;
				var n = 0;
				for ( var i = 0; i < gpuMap.Width * gpuMap.Height; i++ )
				{
					if ( !gpuMap.Filled[i] || !cpuMap.Filled[i] ) continue;
					for ( var c = 0; c < 3; c++ )
					{
						var d = Math.Abs( gpuMap.Rgb[i * 3 + c] - cpuMap.Rgb[i * 3 + c] );
						sum += d;
						if ( d > max ) max = d;
					}
					n++;
				}
				if ( n > 0 )
					result[name] = (sum / (n * 3.0), max);
			}
			return result;
		}

		/// <summary>Which of <paramref name="wanted"/> the set does not have.</summary>
		public static BakeMaps MissingMaps( BakeSet set, BakeMaps wanted )
		{
			var missing = BakeMaps.None;
			if ( set is null ) return wanted;
			foreach ( var (flag, name) in MapFlags() )
			{
				if ( (wanted & flag) == 0 ) continue;
				if ( MapByName( set, name ) is null ) missing |= flag;
			}
			return missing;
		}

		/// <summary>Merge two sets: maps from <paramref name="first"/> win, gaps are filled from <paramref name="second"/>; ranges come from the second (CPU) set.</summary>
		public static BakeSet MergeSets( BakeSet first, BakeSet second, BakeMaps maps )
		{
			if ( first is null ) return second;
			if ( second is null ) return first;
			var merged = new BakeSet
			{
				PositionMin = second.PositionMin,
				PositionMax = second.PositionMax,
				IdColors = second.IdColors,
				HeightMin = second.HeightMin,
				HeightMax = second.HeightMax,
				HeightRange = second.HeightRange,
				CurvatureRange = second.CurvatureRange,
				CavityRange = second.CavityRange,
				AoDistance = second.AoDistance,
				ThicknessDistance = second.ThicknessDistance,
				ThicknessUnits = second.ThicknessUnits,
				Samples = first.Samples + second.Samples,
				Hits = first.Hits + second.Hits,
				Texels = first.Texels ?? second.Texels,
				MissMap = first.MissMap ?? second.MissMap,
				CurvatureWorld = first.CurvatureWorld ?? second.CurvatureWorld,
				CurvatureWorldRange = second.CurvatureWorldRange,
				GroupMiss = first.GroupMiss is { Count: > 0 } ? first.GroupMiss : second.GroupMiss,
			};
			foreach ( var (flag, name) in MapFlags() )
			{
				if ( (maps & flag) == 0 ) continue;
				SetMap( merged, name, MapByName( first, name ) ?? MapByName( second, name ) );
			}
			return merged;
		}

		static IEnumerable<(BakeMaps Flag, string Name)> MapFlags()
		{
			yield return (BakeMaps.Normal, "normal");
			yield return (BakeMaps.AmbientOcclusion, "ao");
			yield return (BakeMaps.Curvature, "curvature");
			yield return (BakeMaps.Cavity, "cavity");
			yield return (BakeMaps.Height, "height");
			yield return (BakeMaps.Thickness, "thickness");
			yield return (BakeMaps.BentNormal, "bentnormal");
			yield return (BakeMaps.ObjectNormal, "objectnormal");
			yield return (BakeMaps.Position, "position");
			yield return (BakeMaps.MaterialId, "id");
		}

		static BakeMaps MapFlag( string name )
		{
			foreach ( var (flag, n) in MapFlags() )
				if ( n == name ) return flag;
			return BakeMaps.None;
		}

		static string MapNames( BakeMaps maps )
		{
			var names = new List<string>();
			foreach ( var (flag, name) in MapFlags() )
				if ( (maps & flag) != 0 ) names.Add( name );
			return string.Join( "+", names );
		}

		static BakedMap MapByName( BakeSet set, string name ) => name switch
		{
			"normal" => set.Normal,
			"ao" => set.AmbientOcclusion,
			"curvature" => set.Curvature,
			"cavity" => set.Cavity,
			"height" => set.Height,
			"thickness" => set.Thickness,
			"bentnormal" => set.BentNormal,
			"objectnormal" => set.ObjectNormal,
			"position" => set.Position,
			"id" => set.MaterialId,
			_ => null,
		};

		static void SetMap( BakeSet set, string name, BakedMap map )
		{
			switch ( name )
			{
				case "normal": set.Normal = map; break;
				case "ao": set.AmbientOcclusion = map; break;
				case "curvature": set.Curvature = map; break;
				case "cavity": set.Cavity = map; break;
				case "height": set.Height = map; break;
				case "thickness": set.Thickness = map; break;
				case "bentnormal": set.BentNormal = map; break;
				case "objectnormal": set.ObjectNormal = map; break;
				case "position": set.Position = map; break;
				case "id": set.MaterialId = map; break;
			}
		}
	}
}
