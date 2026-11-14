using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Effigy;

/// <summary>Which stage a bake job is in, for progress reporting.</summary>
public enum BakePhase
{
	Preparing,
	Baking,
	Writing,
	Done,
	Cancelled,
	Failed,
}

/// <summary>One progress report from a running <see cref="BakeJob"/>.</summary>
public sealed class BakeProgress
{
	public BakePhase Phase;
	public string Group;
	public string Message;
	public float Percent;
}

/// <summary>
/// What a <see cref="BakeJob"/> run produced. A failed or cancelled run has <see cref="Set"/>
/// null and a readable <see cref="Error"/> (or <see cref="Cancelled"/>); it never throws, and it
/// never writes files — writing is <see cref="Baker.WriteAtomic"/>, which is what keeps a failed
/// bake from touching the previous good files.
/// </summary>
public sealed class BakeJobResult
{
	public BakeSet Set;
	public string Report = "";
	public int Samples, Hits;
	public double MissRate;
	public List<(string Name, int Samples, int Hits)> GroupMiss = new();
	public TimeSpan Duration;
	public bool Cancelled;
	public string Error;
	public bool Succeeded => Error is null && !Cancelled && Set is not null;

	/// <summary>True when nothing had changed since <see cref="BakeJob.PlanRebake"/> ran: the previous set was reused untouched.</summary>
	public bool ReusedPrevious;

	/// <summary>Whole-job fingerprint and one per map, for <see cref="BakeJob.PlanRebake"/>.</summary>
	public string Fingerprint = "";
	public Dictionary<string, string> MapFingerprints = new();
}

/// <summary>
/// Baker 2.0 orchestration: the job around <see cref="NormalBake"/>, which already bakes every
/// map type but has no notion of a job.
///
/// WHAT THIS ADDS, and nothing it duplicates:
/// <list type="bullet">
/// <item>Size policy: 512–8K power-of-two final sizes (1K props, 2K standard, 4K hero, 8K film),
/// with a 256 px live preview that does not pretend to be a final bake.</item>
/// <item>Groups by name: <see cref="BakePairs.Match"/> + <see cref="BakePairs.ToGroups"/> turn
/// low/high bodies into <see cref="BakeGroup"/>s; each group bakes apart (its rays only ever
/// see its own high mesh) and the filled texels merge into one map set, as
/// <see cref="NormalBake.BakeGroups"/> does.</item>
/// <item>Progress + cancel: per-group bakes, so a cancel lands between groups and progress is
/// real, not a spinner over one monolithic call. Cancelling never claims a partial result.</item>
/// <item>Re-bake on change: a content fingerprint per map, so changing only the green flip
/// re-bakes the normal family and leaves height, AO and the rest untouched.</item>
/// <item>Ray-miss reporting: the merged hit/miss numbers are surfaced as a miss rate and a
/// per-group report, not silently zeroed into the maps.</item>
/// <item>Atomic output: <see cref="Baker.WriteAtomic"/> stages into a temp directory and only
/// then swaps files into place, with the previous files backed up and restored on failure.</item>
/// </list>
/// Bit depth: 8-bit is the default the existing materials expect. <see cref="BakeOptions.SixteenBit"/>
/// flows straight through to <see cref="NormalBake"/>, which keeps a 16-bit copy of the maps made
/// from a continuous number (height, the three normal maps, position); the ID map stays 8-bit.
/// MikkTSpace tangents are <see cref="NormalBake"/>'s job (see the mikktspace ticket), not this file's.
/// </summary>
public sealed class BakeJob
{
	/// <summary>Final bake sizes: 1K props, 2K standard, 4K hero, 8K film.</summary>
	public static readonly int[] StandardSizes = { 512, 1024, 2048, 4096, 8192 };

	public IReadOnlyList<BakeGroup> Groups { get; }
	public BakeMaps Maps { get; }
	public int Width { get; }
	public int Height { get; }
	public BakeOptions Options { get; }

	/// <summary>Square final bake. <paramref name="size"/> must be a <see cref="StandardSizes"/> power of two.</summary>
	public BakeJob( IReadOnlyList<BakeGroup> groups, BakeMaps maps, int size, BakeOptions options = null )
		: this( groups, maps, size, size, options )
	{
		if ( !TrySize( size, out var why ) )
			throw new ArgumentException( why, nameof( size ) );
	}

	/// <summary>Rectangular bake (previews and odd atlases). Each side must be a power of two, 16–8192.</summary>
	public BakeJob( IReadOnlyList<BakeGroup> groups, BakeMaps maps, int width, int height, BakeOptions options = null )
	{
		if ( groups is null || groups.Count == 0 ) throw new ArgumentException( "A bake job needs at least one group.", nameof( groups ) );
		if ( maps == BakeMaps.None ) throw new ArgumentException( "A bake job needs at least one map.", nameof( maps ) );
		if ( !IsPowerOfTwo( width ) || width < 16 || width > 8192 ) throw new ArgumentException( $"Bake width {width} is not a power of two between 16 and 8192.", nameof( width ) );
		if ( !IsPowerOfTwo( height ) || height < 16 || height > 8192 ) throw new ArgumentException( $"Bake height {height} is not a power of two between 16 and 8192.", nameof( height ) );

		foreach ( var g in groups )
		{
			if ( g is null ) throw new ArgumentException( "Bake groups must not be null.", nameof( groups ) );
			if ( g.Low is null ) throw new ArgumentException( $"Group '{g.Name}' has no low mesh.", nameof( groups ) );
			if ( g.High is null ) throw new ArgumentException( $"Group '{g.Name}' has no high mesh.", nameof( groups ) );
		}

		Groups = groups;
		Maps = maps;
		Width = width;
		Height = height;
		Options = options ?? new BakeOptions();
	}

	/// <summary>True when <paramref name="size"/> is a final bake size: a power of two from 512 to 8192.</summary>
	public static bool TrySize( int size, out string why )
	{
		if ( !IsPowerOfTwo( size ) || size < 512 || size > 8192 )
		{
			why = $"Bake size {size} is not a power of two between 512 and 8192 (512, 1024, 2048, 4096, 8192).";
			return false;
		}

		why = null;
		return true;
	}

	static bool IsPowerOfTwo( int v ) => v > 0 && ( v & ( v - 1 ) ) == 0;

	/// <summary>
	/// Match low/high bodies by name (<c>Arm_low</c> + <c>Arm_high</c>) into a job, as Substance and
	/// Marmoset do. Throws with the match problems listed when bodies cannot be paired.
	/// </summary>
	public static BakeJob FromPairs( IEnumerable<(string Name, PolyMesh Mesh)> bodies, BakeMaps maps, int size, BakeOptions options = null, bool idByBody = false )
	{
		if ( bodies is null ) throw new ArgumentNullException( nameof( bodies ) );

		var pairs = BakePairs.Match( bodies, out var problems );

		if ( problems.Count > 0 )
			throw new ArgumentException( "Could not match bake pairs: " + string.Join( "; ", problems ), nameof( bodies ) );

		if ( pairs.Count == 0 )
			throw new ArgumentException( "No low/high bake pairs found in the given bodies.", nameof( bodies ) );

		return new BakeJob( BakePairs.ToGroups( pairs, idByBody ), maps, size, options );
	}

	/// <summary>
	/// A 256 px preview of this job: same groups, maps and options, one supersample, AO rays capped
	/// at 8. For the live preview while the cage or a source is being edited; not a final bake.
	/// </summary>
	public BakeJobResult Preview( IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		var options = CloneOptions( Options );
		options.Supersample = 1;
		options.AoRays = Math.Min( options.AoRays == 0 ? 8 : options.AoRays, 8 );
		var preview = new BakeJob( Groups, Maps, 256, 256, options );
		return preview.Run( progress, cancel );
	}

	/// <summary>Whole-job fingerprint: size, maps, options and every group's geometry.</summary>
	public string Fingerprint() => HashString( JobHash() );

	/// <summary>One fingerprint per map short name ("normal", "ao", ...): geometry plus only the options that map reads.</summary>
	public Dictionary<string, string> MapFingerprints()
	{
		var geometry = GeometryHash();
		var result = new Dictionary<string, string>();

		foreach ( var (flag, name) in MapNames() )
		{
			if ( ( Maps & flag ) == 0 ) continue;
			result[name] = HashString( Mix( geometry, MapOptionsHash( flag ) ) );
		}

		return result;
	}

	/// <summary>
	/// Which maps need baking given a previous result: every map whose fingerprint changed, or
	/// that the previous set is missing. <see cref="BakeMaps.None"/> means nothing changed and the
	/// previous set can be reused as-is.
	/// </summary>
	public BakeMaps PlanRebake( BakeJobResult previous )
	{
		if ( previous is null || previous.Set is null || previous.MapFingerprints is null )
			return Maps;

		var current = MapFingerprints();
		var need = BakeMaps.None;

		foreach ( var (flag, name) in MapNames() )
		{
			if ( ( Maps & flag ) == 0 ) continue;
			if ( !previous.MapFingerprints.TryGetValue( name, out var old ) || old != current[name] ) need |= flag;
			else if ( GetMap( previous.Set, name ) is null ) need |= flag;
		}

		return need;
	}

	/// <summary>
	/// Bake the job, one group at a time so progress is real and a cancel lands between groups.
	/// Never throws for a bake failure or a cancel: those come back as <see cref="BakeJobResult.Error"/>
	/// / <see cref="BakeJobResult.Cancelled"/> with <see cref="BakeJobResult.Set"/> null. When
	/// <paramref name="previous"/> is given, only the maps <see cref="PlanRebake"/> flags are baked
	/// and the rest are reused from it.
	/// </summary>
	public BakeJobResult Run( IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		return Run( null, progress, cancel );
	}

	/// <inheritdoc cref="Run(IProgress{BakeProgress}, CancellationToken)"/>
	public BakeJobResult Run( BakeJobResult previous, IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		var clock = Stopwatch.StartNew();

		void Report( BakePhase phase, float percent, string group = null, string message = null ) =>
			progress?.Report( new BakeProgress { Phase = phase, Percent = percent, Group = group, Message = message } );

		BakeJobResult Fail( string error, BakePhase phase )
		{
			Report( phase, 100f, null, error );
			return new BakeJobResult { Error = error, Duration = clock.Elapsed, Fingerprint = Fingerprint(), MapFingerprints = MapFingerprints() };
		}

		try
		{
			Report( BakePhase.Preparing, 0f, null, "Preparing bake groups." );
			cancel.ThrowIfCancellationRequested();

			var bakeMaps = PlanRebake( previous );

			if ( bakeMaps == BakeMaps.None && previous is not null )
			{
				Report( BakePhase.Done, 100f, null, "Nothing changed since the last bake; reusing it." );
				return new BakeJobResult
				{
					Set = previous.Set,
					Report = previous.Report,
					Samples = previous.Samples,
					Hits = previous.Hits,
					MissRate = previous.MissRate,
					GroupMiss = new List<(string, int, int)>( previous.GroupMiss ),
					Duration = clock.Elapsed,
					ReusedPrevious = true,
					Fingerprint = Fingerprint(),
					MapFingerprints = MapFingerprints(),
				};
			}

			var sets = new List<BakeSet>();

			for ( var g = 0; g < Groups.Count; g++ )
			{
				cancel.ThrowIfCancellationRequested();

				var group = Groups[g];
				Report( BakePhase.Baking, Groups.Count <= 1 ? 50f : 100f * g / Groups.Count, group.Name, $"Baking group {group.Name} ({g + 1}/{Groups.Count})." );

				var options = CloneOptions( Options );
				options.CageMesh = group.Cage;
				sets.Add( NormalBake.BakeAll( group.Low, group.High, Width, Height, bakeMaps, options ) );
			}

			cancel.ThrowIfCancellationRequested();
			Report( BakePhase.Baking, 95f, null, "Merging group maps." );

			var merged = MergeSets( sets );

			if ( previous is not null )
			{
				foreach ( var (flag, name) in MapNames() )
				{
					if ( ( Maps & flag ) == 0 || ( bakeMaps & flag ) != 0 ) continue;
					var reused = GetMap( previous.Set, name );
					if ( reused is not null ) SetMap( merged, name, reused );
				}
			}

			var result = new BakeJobResult
			{
				Set = merged,
				Report = merged.Report,
				Samples = merged.Samples,
				Hits = merged.Hits,
				MissRate = merged.Samples == 0 ? 0 : 1.0 - (double)merged.Hits / merged.Samples,
				GroupMiss = new List<(string, int, int)>( merged.GroupMiss ),
				Duration = clock.Elapsed,
				Fingerprint = Fingerprint(),
				MapFingerprints = MapFingerprints(),
			};

			Report( BakePhase.Done, 100f, null, result.Report.Split( '\n' ).FirstOrDefault() ?? "Bake done." );
			return result;
		}
		catch ( OperationCanceledException )
		{
			return new BakeJobResult { Cancelled = true, Duration = clock.Elapsed, Fingerprint = Fingerprint(), MapFingerprints = MapFingerprints() };
		}
		catch ( Exception ex )
		{
			return Fail( $"Bake failed ({ex.GetType().Name}): {ex.Message}", BakePhase.Failed );
		}
	}

	/// <summary>
	/// The (flag, short name) of every map <see cref="NormalBake.BakeAll"/> can write, in
	/// <see cref="BakeSet.Maps"/> order. Short names are the filenames <see cref="BakeMaterial.WriteSet"/> uses.
	/// </summary>
	static (BakeMaps Flag, string Name)[] MapNames() => new[]
	{
		(BakeMaps.Normal, "normal"),
		(BakeMaps.AmbientOcclusion, "ao"),
		(BakeMaps.Curvature, "curvature"),
		(BakeMaps.Cavity, "cavity"),
		(BakeMaps.Height, "height"),
		(BakeMaps.Thickness, "thickness"),
		(BakeMaps.BentNormal, "bentnormal"),
		(BakeMaps.ObjectNormal, "objectnormal"),
		(BakeMaps.Position, "position"),
		(BakeMaps.MaterialId, "id"),
	};

	static BakedMap GetMap( BakeSet set, string name ) => name switch
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

	/// <summary>Merge per-group sets into one: filled texels win, in group order (the same last-one-wins rule <see cref="NormalBake"/> uses for overlapping UVs).</summary>
	BakeSet MergeSets( IReadOnlyList<BakeSet> sets )
	{
		var merged = new BakeSet();

		foreach ( var (flag, name) in MapNames() )
		{
			var sources = sets.Select( s => GetMap( s, name ) ).Where( m => m is not null ).ToList();
			if ( sources.Count == 0 ) continue;
			SetMap( merged, name, MergeMaps( sources ) );
		}

		if ( sets.Any( s => s.CurvatureWorld is not null ) )
			merged.CurvatureWorld = MergeMaps( sets.Select( s => s.CurvatureWorld ).Where( m => m is not null ).ToList() );

		if ( sets.Any( s => s.MissMap is not null ) )
			merged.MissMap = MergeMaps( sets.Select( s => s.MissMap ).Where( m => m is not null ).ToList() );

		merged.Samples = sets.Sum( s => s.Samples );
		merged.Hits = sets.Sum( s => s.Hits );

		foreach ( var s in sets )
			merged.GroupMiss.AddRange( s.GroupMiss );

		var withHeight = sets.Where( s => s.Height is not null ).ToList();
		if ( withHeight.Count > 0 )
		{
			merged.HeightMin = withHeight.Min( s => s.HeightMin );
			merged.HeightMax = withHeight.Max( s => s.HeightMax );
			merged.HeightRange = withHeight.Max( s => s.HeightRange );
		}

		if ( sets.Any( s => s.Curvature is not null ) ) merged.CurvatureRange = sets.Where( s => s.Curvature is not null ).Max( s => s.CurvatureRange );
		if ( sets.Any( s => s.Cavity is not null ) ) merged.CavityRange = sets.Where( s => s.Cavity is not null ).Max( s => s.CavityRange );
		if ( sets.Any( s => s.AmbientOcclusion is not null ) ) merged.AoDistance = sets.Where( s => s.AmbientOcclusion is not null ).Max( s => s.AoDistance );
		if ( sets.Any( s => s.Thickness is not null ) ) merged.ThicknessDistance = sets.Where( s => s.Thickness is not null ).Max( s => s.ThicknessDistance );
		if ( sets.Any( s => s.CurvatureWorld is not null ) ) merged.CurvatureWorldRange = sets.Where( s => s.CurvatureWorld is not null ).Max( s => s.CurvatureWorldRange );

		if ( sets.Any( s => s.Position is not null ) )
		{
			merged.PositionMin = new Vec3(
				sets.Where( s => s.Position is not null ).Min( s => s.PositionMin.x ),
				sets.Where( s => s.Position is not null ).Min( s => s.PositionMin.y ),
				sets.Where( s => s.Position is not null ).Min( s => s.PositionMin.z ) );
			merged.PositionMax = new Vec3(
				sets.Where( s => s.Position is not null ).Max( s => s.PositionMax.x ),
				sets.Where( s => s.Position is not null ).Max( s => s.PositionMax.y ),
				sets.Where( s => s.Position is not null ).Max( s => s.PositionMax.z ) );
		}

		foreach ( var s in sets )
		{
			if ( s.IdColors is null ) continue;
			merged.IdColors ??= new Dictionary<int, (byte R, byte G, byte B)>();
			foreach ( var pair in s.IdColors )
				merged.IdColors[pair.Key] = pair.Value;
		}

		if ( sets.Any( s => s.ThicknessUnits is not null ) )
		{
			var units = new float[Width * Height];
			Array.Fill( units, -1f );

			foreach ( var s in sets )
			{
				if ( s.ThicknessUnits is null || s.Thickness is null ) continue;
				for ( var i = 0; i < units.Length && i < s.ThicknessUnits.Length; i++ )
				{
					if ( s.Thickness.Filled[i] )
						units[i] = s.ThicknessUnits[i];
				}
			}

			merged.ThicknessUnits = units;
		}

		if ( sets.All( s => s.Texels is not null ) && sets.Count > 0 )
		{
			var texels = new BakeTexels { Width = Width, Height = Height, Hit = new bool[Width * Height], Point = new Vec3[Width * Height], Normal = new Vec3[Width * Height], Face = new int[Width * Height], Occlusion = new float[Width * Height], Curvature = new float[Width * Height], Cavity = new float[Width * Height] };
			Array.Fill( texels.Face, -1 );

			foreach ( var s in sets )
			{
				for ( var i = 0; i < texels.Hit.Length; i++ )
				{
					if ( !s.Texels.Hit[i] ) continue;
					texels.Hit[i] = true;
					texels.Point[i] = s.Texels.Point[i];
					texels.Normal[i] = s.Texels.Normal[i];
					texels.Face[i] = s.Texels.Face[i];
					texels.Occlusion[i] = s.Texels.Occlusion[i];
					texels.Curvature[i] = s.Texels.Curvature[i];
					texels.Cavity[i] = s.Texels.Cavity[i];
				}
			}

			merged.Texels = texels;
		}

		return merged;
	}

	static BakedMap MergeMaps( IReadOnlyList<BakedMap> sources )
	{
		var first = sources[0];
		var merged = new BakedMap( first.Width, first.Height );
		var any16 = sources.Any( m => m.Rgb16 is not null );

		foreach ( var src in sources )
		{
			for ( var i = 0; i < src.Filled.Length; i++ )
			{
				if ( !src.Filled[i] ) continue;
				merged.Filled[i] = true;
				Array.Copy( src.Rgb, i * 3, merged.Rgb, i * 3, 3 );
			}
		}

		if ( any16 )
		{
			merged.EnableSixteenBit();

			foreach ( var src in sources )
			{
				if ( src.Rgb16 is null ) continue;
				for ( var i = 0; i < src.Filled.Length; i++ )
				{
					if ( !src.Filled[i] ) continue;
					Array.Copy( src.Rgb16, i * 3, merged.Rgb16, i * 3, 3 );
				}
			}
		}

		return merged;
	}

	/// <summary>Per-group options: the job's options, with that group's cage mesh. <see cref="BakeOptions"/> has no clone, so every field is copied here — add new fields here too.</summary>
	static BakeOptions CloneOptions( BakeOptions options )
	{
		return new BakeOptions
		{
			MaxDistance = options.MaxDistance,
			FlipGreen = options.FlipGreen,
			Padding = options.Padding,
			Cage = options.Cage,
			Supersample = options.Supersample,
			AoRays = options.AoRays,
			AoDistance = options.AoDistance,
			CurvatureRadius = options.CurvatureRadius,
			CavityRadius = options.CavityRadius,
			ThicknessRays = options.ThicknessRays,
			ThicknessDistance = options.ThicknessDistance,
			KeepHits = options.KeepHits,
			CageMesh = options.CageMesh,
			SixteenBit = options.SixteenBit,
			BevelRadius = options.BevelRadius,
			Skew = options.Skew,
			SkewEdgeAngle = options.SkewEdgeAngle,
			CurvatureWorld = options.CurvatureWorld,
			ReportMiss = options.ReportMiss,
			FaceIds = options.FaceIds,
		};
	}

	ulong JobHash()
	{
		var h = 1469598103934665603ul;
		h = MixInt( h, Width );
		h = MixInt( h, Height );
		h = MixInt( h, (int)Maps );
		h = Mix( h, GeometryHash() );
		h = Mix( h, AllOptionsHash() );
		return h;
	}

	ulong GeometryHash()
	{
		var h = 1469598103934665603ul;

		foreach ( var g in Groups )
		{
			h = MixMesh( h, g.Low );
			h = MixMesh( h, g.High );
			h = g.Cage is null ? Mix( h, 0 ) : MixMesh( h, g.Cage );
			h = MixInts( h, g.HighIds );
		}

		return h;
	}

	static ulong MixMesh( ulong h, PolyMesh mesh )
	{
		h = MixInt( h, mesh.VertexCount );
		h = MixInt( h, mesh.FaceCount );

		foreach ( var p in mesh.Positions )
			h = MixVec3( h, p );

		var materialSum = 0;
		var uvCorners = 0;

		foreach ( var f in mesh.Faces )
		{
			materialSum += f.Material;
			uvCorners += f.UVs is null ? 0 : f.UVs.Length;
		}

		h = MixInt( h, materialSum );
		h = MixInt( h, uvCorners );
		return h;
	}

	ulong AllOptionsHash()
	{
		var h = 1469598103934665603ul;
		h = MixFloat( h, Options.MaxDistance );
		h = MixBool( h, Options.FlipGreen );
		h = MixInt( h, Options.Padding );
		h = MixFloat( h, Options.Cage );
		h = MixInt( h, Options.Supersample );
		h = MixInt( h, Options.AoRays );
		h = MixFloat( h, Options.AoDistance );
		h = MixFloat( h, Options.CurvatureRadius );
		h = MixFloat( h, Options.CavityRadius );
		h = MixInt( h, Options.ThicknessRays );
		h = MixFloat( h, Options.ThicknessDistance );
		h = MixBool( h, Options.KeepHits );
		h = MixBool( h, Options.SixteenBit );
		h = MixFloat( h, Options.BevelRadius );
		h = MixFloat( h, Options.Skew );
		h = MixFloat( h, Options.SkewEdgeAngle );
		h = MixBool( h, Options.CurvatureWorld );
		h = MixBool( h, Options.ReportMiss );
		h = MixInts( h, Options.FaceIds );
		return h;
	}

	/// <summary>Only the options a map reads: changing the green flip re-bakes the normal family, not the height map.</summary>
	ulong MapOptionsHash( BakeMaps map )
	{
		var h = 1469598103934665603ul;
		h = MixFloat( h, Options.MaxDistance );
		h = MixFloat( h, Options.Cage );
		h = MixInt( h, Options.Supersample );
		h = MixInt( h, Options.Padding );

		switch ( map )
		{
			case BakeMaps.Normal:
			case BakeMaps.BentNormal:
			case BakeMaps.ObjectNormal:
				h = MixBool( h, Options.FlipGreen );
				h = MixFloat( h, Options.Skew );
				h = MixFloat( h, Options.SkewEdgeAngle );
				h = MixFloat( h, Options.BevelRadius );
				h = MixBool( h, Options.SixteenBit );
				break;
			case BakeMaps.Height:
			case BakeMaps.Position:
				h = MixBool( h, Options.SixteenBit );
				break;
			case BakeMaps.AmbientOcclusion:
				h = MixInt( h, Options.AoRays );
				h = MixFloat( h, Options.AoDistance );
				break;
			case BakeMaps.Curvature:
				h = MixFloat( h, Options.CurvatureRadius );
				h = MixBool( h, Options.CurvatureWorld );
				break;
			case BakeMaps.Cavity:
				h = MixFloat( h, Options.CavityRadius );
				break;
			case BakeMaps.Thickness:
				h = MixInt( h, Options.ThicknessRays );
				h = MixFloat( h, Options.ThicknessDistance );
				break;
			case BakeMaps.MaterialId:
				h = MixInts( h, Options.FaceIds );
				break;
		}

		return h;
	}

	static ulong Mix( ulong h, ulong v ) => ( h ^ v ) * 1099511628211ul;
	static ulong MixInt( ulong h, int v ) => Mix( h, (ulong)(uint)v );
	static ulong MixBool( ulong h, bool v ) => Mix( h, v ? 1ul : 0ul );
	static ulong MixFloat( ulong h, float v ) => Mix( h, (ulong)(uint)BitConverter.SingleToInt32Bits( v ) );
	static ulong MixVec3( ulong h, Vec3 v ) => MixFloat( MixFloat( MixFloat( h, v.x ), v.y ), v.z );

	static ulong MixInts( ulong h, int[] values )
	{
		if ( values is null ) return Mix( h, 0 );
		h = MixInt( h, values.Length );
		foreach ( var v in values )
			h = MixInt( h, v );
		return h;
	}

	static string HashString( ulong hash ) => hash.ToString( "X16", CultureInfo.InvariantCulture );
}

/// <summary>
/// File output for a <see cref="BakeJob"/> result. Kept apart from the job so a bake can be
/// inspected, previewed or discarded without touching disk.
/// </summary>
public static class Baker
{
	/// <summary>
	/// Write a bake set to <paramref name="folder"/> as <c><paramref name="name"/>_&lt;map&gt;.png</c>
	/// (plus a material file when <paramref name="material"/> names one), atomically: the maps are
	/// staged in a temp directory inside the folder first, and only then swapped into place.
	/// A failure at any point leaves the previous files byte-identical — staged files are deleted
	/// and any file already swapped in is restored from its backup. Returns success, the files
	/// written, and a readable error on failure (never throws).
	/// </summary>
	public static (bool Ok, List<string> Written, string Error) WriteAtomic( BakeSet set, string folder, string name, bool flipV = false, string material = "None" )
	{
		if ( set is null ) return (false, new List<string>(), "No bake to write: the bake set is null.");
		if ( string.IsNullOrWhiteSpace( folder ) ) return (false, new List<string>(), "No output folder given for the bake.");
		if ( string.IsNullOrWhiteSpace( name ) ) return (false, new List<string>(), "No base name given for the bake files.");

		var temp = Path.Combine( folder, ".bake-tmp-" + Guid.NewGuid().ToString( "N" ) );
		var backup = Path.Combine( folder, ".bake-bak-" + Guid.NewGuid().ToString( "N" ) );
		var swapped = new List<string>();

		try
		{
			Directory.CreateDirectory( temp );
			var staged = BakeMaterial.WriteSet( set, temp, name, flipV, null, material );
			Directory.CreateDirectory( folder );
			Directory.CreateDirectory( backup );

			foreach ( var path in staged )
			{
				var dest = Path.Combine( folder, Path.GetFileName( path ) );

				if ( File.Exists( dest ) )
					File.Copy( dest, Path.Combine( backup, Path.GetFileName( path ) ), true );

				File.Move( path, dest, true );
				swapped.Add( dest );
			}

			Directory.Delete( temp, true );
			Directory.Delete( backup, true );
			return (true, swapped, null);
		}
		catch ( Exception ex )
		{
			try
			{
				foreach ( var dest in swapped )
				{
					var saved = Path.Combine( backup, Path.GetFileName( dest ) );

					if ( File.Exists( saved ) )
						File.Copy( saved, dest, true );
					else if ( File.Exists( dest ) )
						File.Delete( dest );
				}
			}
			catch
			{
				// Restoration is best-effort; the error below still names the failed write.
			}

			try { if ( Directory.Exists( temp ) ) Directory.Delete( temp, true ); } catch { }
			try { if ( Directory.Exists( backup ) ) Directory.Delete( backup, true ); } catch { }

			return (false, new List<string>(), $"Bake write failed ({ex.GetType().Name}): {ex.Message} Previous files were left untouched where possible.");
		}
	}
}
