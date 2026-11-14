#nullable enable
using Effigy;
using Marionette.Desktop;
using System.Threading;

/// <summary>
/// Baker 2.0 (godlike-baker-2-paintable-cage): the job around <see cref="NormalBake"/> — size
/// policy, match-by-name groups, per-group progress/cancel, per-map re-bake fingerprints,
/// ray-miss reporting, atomic output — plus the paintable cage (<see cref="BakeCagePaint"/>).
/// Covers <see cref="BakeJob"/>, <see cref="Baker.WriteAtomic"/> and <see cref="BakeCagePaint"/>.
/// The map pixels themselves are <see cref="NormalBake"/>'s contract; these checks prove the
/// orchestration: every map type comes back, a painted cage changes what the rays reach, misses
/// are reported instead of silently zeroed, and a re-bake touches only the maps that changed.
/// NOT registered in TestRegistry.cs yet — the final integrator adds the one line.
/// </summary>
internal static class BakerTests
{
	static PolyMesh LowQuad( float u0 = 0f, float u1 = 1f )
	{
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( -1, -1, 0 ) );
		mesh.AddVertex( new Vec3( 1, -1, 0 ) );
		mesh.AddVertex( new Vec3( 1, 1, 0 ) );
		mesh.AddVertex( new Vec3( -1, 1, 0 ) );
		mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( u0, 0 ), new Vec2( u1, 0 ), new Vec2( u1, 1 ) } );
		mesh.AddFace( new[] { 0, 2, 3 }, new[] { new Vec2( u0, 0 ), new Vec2( u1, 1 ), new Vec2( u0, 1 ) } );
		return mesh;
	}

	/// <summary>High surface as a ramp: low edge 0.1 off the low mesh, high edge 0.4, so the height map has a real range and the baked normal tilts.</summary>
	static PolyMesh HighRamp()
	{
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( -1, -1, 0.1f ) );
		mesh.AddVertex( new Vec3( 1, -1, 0.1f ) );
		mesh.AddVertex( new Vec3( 1, 1, 0.4f ) );
		mesh.AddVertex( new Vec3( -1, 1, 0.4f ) );
		mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ) } );
		mesh.AddFace( new[] { 0, 2, 3 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) } );
		return mesh;
	}

	static PolyMesh HighFlat( float z )
	{
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( -1, -1, z ) );
		mesh.AddVertex( new Vec3( 1, -1, z ) );
		mesh.AddVertex( new Vec3( 1, 1, z ) );
		mesh.AddVertex( new Vec3( -1, 1, z ) );
		mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ) } );
		mesh.AddFace( new[] { 0, 2, 3 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) } );
		return mesh;
	}

	static BakeOptions Opts() => new()
	{
		MaxDistance = 1f,
		AoRays = 8,
		AoDistance = 1f,
		CurvatureRadius = 0.5f,
		CavityRadius = 0.5f,
		ThicknessRays = 8,
		ThicknessDistance = 1f,
		Supersample = 1,
		Padding = 2,
		ReportMiss = true,
	};

	static BakeJob Job( BakeMaps maps, PolyMesh low, PolyMesh high, int size = 32, BakeOptions? options = null, PolyMesh? cage = null ) =>
		new( new[] { new BakeGroup { Name = "low", Low = low, High = high, Cage = cage } }, maps, size, size, options ?? Opts() );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "baker", name, test );

		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}

		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		Check( "sizes are power-of-two 512-8K", () =>
		{
			foreach ( var size in new[] { 512, 1024, 2048, 4096, 8192 } )
				Require( BakeJob.TrySize( size, out _ ), $"size {size} should be accepted" );

			foreach ( var size in new[] { 0, 100, 300, 1000, 8193 } )
				Require( !BakeJob.TrySize( size, out var why ) && why is not null, $"size {size} should be refused with a reason" );

			Throws<ArgumentException>( () => _ = new BakeJob( new[] { new BakeGroup { Name = "low", Low = LowQuad(), High = HighRamp() } }, BakeMaps.Normal, 100 ), "non-standard size" );

			var odd = new BakeJob( new[] { new BakeGroup { Name = "low", Low = LowQuad(), High = HighRamp() } }, BakeMaps.Normal, 32, 64, Opts() );
			Require( odd.Width == 32 && odd.Height == 64, "rectangular preview sizes are allowed" );
		} );

		Check( "match-by-name groups", () =>
		{
			var bodies = new (string, PolyMesh)[]
			{
				("Arm_low", LowQuad()), ("Arm_high", HighRamp()),
				("Leg_low", LowQuad()), ("Leg_high", HighRamp()),
			};
			var job = BakeJob.FromPairs( bodies, BakeMaps.Normal, 512, Opts() );
			Require( job.Groups.Count == 2, $"expected 2 groups, got {job.Groups.Count}" );
			Require( job.Groups[0].Name == "Arm" && job.Groups[1].Name == "Leg", "groups are named by their matched base name" );

			Throws<ArgumentException>( () => _ = BakeJob.FromPairs( new (string, PolyMesh)[] { ("Arm_low", LowQuad()) }, BakeMaps.Normal, 512, Opts() ), "a low with no high must fail loudly" );
		} );

		Check( "every map type bakes from a fixture pair", () =>
		{
			var result = Job( BakeMaps.Everything, LowQuad(), HighRamp() ).Run();
			Require( result.Succeeded, "bake failed: " + result.Error );
			Require( result.Set is not null, "no bake set" );

			foreach ( var (name, map) in result.Set.Maps )
			{
				Require( map.Width == 32 && map.Height == 32, $"map {name} is {map.Width}x{map.Height}, expected 32x32" );

				// Only the hit-driven maps are guaranteed filled on an open fixture: AO has no
				// occluders above a lone ramp, and thickness/cavity can legitimately read zero.
				if ( name is "normal" or "height" )
					Require( map.FilledCount > 0, $"map {name} has no filled texels" );
			}

			Require( result.Set.Normal is not null && result.Set.Height is not null && result.Set.Position is not null, "normal/height/position must be present" );
			Require( result.Set.HeightMin < result.Set.HeightMax, "ramp high should give the height map a real range" );
			Require( result.Samples > 0 && result.Hits > 0 && result.MissRate < 1, "ramp bake should hit" );
			Require( result.Report.Contains( "rays hit" ), "report should state the hit count, got: " + result.Report );
			Require( result.Report.Contains( "height:" ), "report should state the height range, got: " + result.Report );
		} );

		Check( "painted cage offset brings a distant high into range", () =>
		{
			var options = Opts();
			options.MaxDistance = 0.1f;

			var bare = Job( BakeMaps.Normal, LowQuad(), HighFlat( 0.2f ), 32, options ).Run();
			Require( bare.Succeeded, "bare bake failed: " + bare.Error );
			Require( bare.Hits == 0, $"high 0.2 away with MaxDistance 0.1 should miss every ray, got {bare.Hits} hits" );

			var paint = new BakeCagePaint( LowQuad() );
			var touched = paint.PaintOffset( new Vec3( 0, 0, 0 ), 5f, 0.45f );
			Require( touched == 4, $"brush should touch all 4 cage vertices, touched {touched}" );

			var caged = Job( BakeMaps.Normal, LowQuad(), HighFlat( 0.2f ), 32, options, paint.BuildCage() ).Run();
			Require( caged.Succeeded, "caged bake failed: " + caged.Error );
			Require( caged.Hits > 0, "painted cage (offset ~0.36) should bring the high surface into range" );
		} );

		Check( "painted skew moves the cage tangentially", () =>
		{
			var low = LowQuad();
			var paint = new BakeCagePaint( low );
			paint.PaintSkew( new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), 5f, 0.3f );

			var cage = paint.BuildCage();
			Require( cage.Positions[1].x > 1.1f, $"skewed cage vertex should move +x, x is {cage.Positions[1].x}" );
			Require( Math.Abs( cage.Positions[1].z ) < 1e-6f, "skew must not move the cage along its normal" );
			Require( low.Positions[1].x == 1f, "painting must never mutate the low mesh" );
		} );

		Check( "paint undo restores the cage", () =>
		{
			var paint = new BakeCagePaint( LowQuad() );
			paint.PaintOffset( new Vec3( 0, 0, 0 ), 5f, 0.5f );
			Require( paint.StrokeCount == 1, "one stroke should be recorded" );
			Require( paint.Offsets[0] > 0f, "offset should be painted" );

			Require( paint.Undo(), "undo should succeed with a stroke to undo" );
			Require( paint.StrokeCount == 0 && paint.Offsets[0] == 0f, "undo should restore the offsets" );
			Require( !paint.Undo(), "a second undo with no strokes should report false" );
		} );

		Check( "ray misses are reported, not silently zeroed", () =>
		{
			var options = Opts();
			options.MaxDistance = 0.1f;

			var result = Job( BakeMaps.Normal, LowQuad(), HighFlat( 5f ), 32, options ).Run();
			Require( result.Succeeded, "an all-miss bake should report, not crash: " + result.Error );
			Require( result.Hits == 0 && result.Samples > 0, "every ray should miss" );
			Require( result.MissRate == 1, $"miss rate should be 1, got {result.MissRate}" );
			Require( result.GroupMiss.Count == 1 && result.GroupMiss[0].Hits == 0, "per-group miss must be recorded" );
			Require( result.Report.Contains( "missed" ), "report must say rays missed, got: " + result.Report );
			Require( result.Set.MissMap is not null && result.Set.MissMap.FilledCount == 0, "miss map should exist and be empty" );
		} );

		Check( "re-bake plan flags only the maps that changed", () =>
		{
			var job = Job( BakeMaps.Everything, LowQuad(), HighRamp() );
			var previous = job.Run();
			Require( previous.Succeeded, "first bake failed: " + previous.Error );

			Require( job.PlanRebake( previous ) == BakeMaps.None, "an unchanged job should need no re-bake" );

			var greenOptions = Opts();
			greenOptions.FlipGreen = true;
			var greenJob = Job( BakeMaps.Everything, LowQuad(), HighRamp(), 32, greenOptions );
			var greenNeed = greenJob.PlanRebake( previous );
			Require( ( greenNeed & BakeMaps.Normal ) != 0 && ( greenNeed & BakeMaps.BentNormal ) != 0 && ( greenNeed & BakeMaps.ObjectNormal ) != 0, "a green flip must re-bake the normal family" );
			Require( ( greenNeed & BakeMaps.Height ) == 0 && ( greenNeed & BakeMaps.AmbientOcclusion ) == 0 && ( greenNeed & BakeMaps.Position ) == 0, "a green flip must not re-bake height, AO or position" );

			var movedJob = Job( BakeMaps.Everything, LowQuad(), HighFlat( 0.3f ) );
			Require( movedJob.PlanRebake( previous ) == BakeMaps.Everything, "changed geometry must re-bake everything" );
			Require( job.PlanRebake( null! ) == BakeMaps.Everything, "no previous bake means bake everything" );
		} );

		Check( "re-bake reuses unchanged maps", () =>
		{
			var job = Job( BakeMaps.Everything, LowQuad(), HighRamp() );
			var previous = job.Run();
			Require( previous.Succeeded, "first bake failed: " + previous.Error );

			var greenOptions = Opts();
			greenOptions.FlipGreen = true;
			var greenJob = Job( BakeMaps.Everything, LowQuad(), HighRamp(), 32, greenOptions );
			var again = greenJob.Run( previous );
			Require( again.Succeeded, "re-bake failed: " + again.Error );
			Require( !again.ReusedPrevious, "a changed job must not claim a full reuse" );
			Require( ReferenceEquals( again.Set.Height, previous.Set.Height ), "the height map must be the previous instance, untouched" );
			Require( !ReferenceEquals( again.Set.Normal, previous.Set.Normal ), "the normal map must be freshly baked" );
		} );

		Check( "progress reports and cancel stops the bake", () =>
		{
			var cancelled = Job( BakeMaps.Normal, LowQuad(), HighRamp() ).Run( null, new CancellationToken( true ) );
			Require( cancelled.Cancelled && cancelled.Set is null, "a pre-cancelled run must report cancelled with no set" );

			var progress = new CollectingProgress();
			var seen = progress.Seen;
			var result = Job( BakeMaps.Everything, LowQuad(), HighRamp() ).Run( progress );
			Require( result.Succeeded, "bake failed: " + result.Error );
			Require( seen.Exists( p => p.Phase == BakePhase.Preparing ), "progress should report Preparing" );
			Require( seen.Exists( p => p.Phase == BakePhase.Baking ), "progress should report Baking" );
			Require( seen.Exists( p => p.Phase == BakePhase.Done && p.Percent == 100f ), "progress should end at Done 100%" );

			for ( var i = 1; i < seen.Count; i++ )
				Require( seen[i].Percent >= seen[i - 1].Percent, "progress percent must not go backwards" );
		} );

		Check( "atomic write leaves previous files untouched on failure", () =>
		{
			var folder = Path.Combine( settings.WorkDirectory, "baker-atomic" );
			var result = Job( BakeMaps.Normal | BakeMaps.Height, LowQuad(), HighRamp() ).Run();
			Require( result.Succeeded, "bake failed: " + result.Error );

			var (ok, written, error) = Baker.WriteAtomic( result.Set, folder, "test" );
			Require( ok, "atomic write failed: " + error );
			Require( written.Count == 2, $"expected 2 files, wrote {written.Count}" );

			foreach ( var path in written )
				Require( File.Exists( path ), "written file missing: " + path );

			Require( !Directory.EnumerateDirectories( folder ).Any( d => Path.GetFileName( d ).StartsWith( ".bake-" ) ), "no temp or backup directory may remain after a successful write" );
			Require( !File.Exists( Path.Combine( folder, "test.vmat" ) ), "material None must not write a .vmat" );

			var before = written.ToDictionary( p => p, p => File.ReadAllBytes( p ) );
			var (ok2, _, error2) = Baker.WriteAtomic( null!, folder, "test" );
			Require( !ok2 && error2 is not null, "a null bake set must fail with a readable error" );

			foreach ( var (path, bytes) in before )
				Require( File.ReadAllBytes( path ).SequenceEqual( bytes ), "previous file changed by the failed write: " + path );
		} );

		Check( "sixteen-bit data maps, eight-bit id map", () =>
		{
			var options = Opts();
			options.SixteenBit = true;
			var result = Job( BakeMaps.Everything, LowQuad(), HighRamp(), 32, options ).Run();
			Require( result.Succeeded, "bake failed: " + result.Error );
			Require( result.Set.Height.Rgb16 is not null, "height is a continuous map and needs a 16-bit copy" );
			Require( result.Set.Normal.Rgb16 is not null, "normal is a continuous map and needs a 16-bit copy" );
			Require( result.Set.MaterialId.Rgb16 is null, "the id map is flat colours, not continuous data" );
		} );

		Check( "preview is a 256px bake of the same job", () =>
		{
			var job = Job( BakeMaps.Normal | BakeMaps.Height, LowQuad(), HighRamp() );
			var preview = job.Preview();
			Require( preview.Succeeded, "preview failed: " + preview.Error );
			Require( preview.Set.Normal.Width == 256 && preview.Set.Normal.Height == 256, "preview must be 256x256" );
		} );

		Check( "two groups merge into one map set", () =>
		{
			var bodies = new (string, PolyMesh)[]
			{
				("Arm_low", LowQuad( 0f, 0.5f )), ("Arm_high", HighRamp()),
				("Leg_low", LowQuad( 0.5f, 1f )), ("Leg_high", HighRamp()),
			};
			var pairs = BakePairs.Match( bodies, out var problems );
			Require( problems.Count == 0, "match problems: " + string.Join( "; ", problems ) );
			var job = new BakeJob( BakePairs.ToGroups( pairs ), BakeMaps.Normal | BakeMaps.Height, 32, 32, Opts() );
			var result = job.Run();
			Require( result.Succeeded, "two-group bake failed: " + result.Error );
			Require( result.GroupMiss.Count == 2, $"expected 2 group reports, got {result.GroupMiss.Count}" );
			Require( result.GroupMiss.All( g => g.Hits > 0 ), "both groups should hit" );
			Require( result.Set.Normal.FilledCount > 0, "merged normal map should have filled texels from both groups" );
		} );
	}

	/// <summary>Synchronous progress sink: <see cref="Progress{T}"/> posts async, which would race the assertions below.</summary>
	sealed class CollectingProgress : IProgress<BakeProgress>
	{
		public List<BakeProgress> Seen = new();
		public void Report( BakeProgress progress ) => Seen.Add( progress );
	}
}
