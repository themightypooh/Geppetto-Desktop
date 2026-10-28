using System.Numerics;
using Marionette.Desktop;

// Lane P20 (particles session, 2026-10-24): 2D draw-record LOD.
// Same harness shape as Particle2DTests: registry.Add("particle-2d-lod", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   Particle2DLodTests.Register( registry, settings );
// placed immediately after the Particle2DTests.Register line in
// tests/TestRegistry.cs (which is itself still pending the sweep — all
// 2D particle test files sit unregistered pending the sweep, same as
// the sibling lanes; if the sweep lands Particle2DTests.Register first,
// this line goes right after it).
//
// SWEEP INTEGRATION LINES (NOT applied by this lane — WriteDrawRecords
// is not this lane's file; the coordinator owns the wiring):
//   In Particle2DSystem.WriteDrawRecords, before returning, optionally:
//     var lod = <caller-supplied Particle2DLodSettings or null>;
//     if ( lod is not null ) { /* apply lod.Apply(records, referencePoint)
//        over the output list and return the collapsed list */ }
//   OPEN DECISIONS the sweep must make (see the lane report):
//   1. Reference-point source: the viewport owner must supply it
//      (expected: viewport centre in canvas coordinates).
//   2. Band-table location: a caller-owned Particle2DLodSettings
//      (per viewport/layer), NOT a field on Particle2DSettings —
//      the LOD pass is a view concern, not a sim concern.

internal static class Particle2DLodTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}

		Particle2DDrawRecord Rec( float x, float y, float size = 4f ) => new()
		{
			Position = new Vector2( x, y ),
			Size = size, SizeX = size, SizeY = size,
			Rotation = 0f, Color = new Particle2DColor( 1f, 1f, 1f, 1f ), Frame = 0,
		};

		// Shared table for most tests: Full < 100, Reduced stride 2 < 300, Collapsed to +inf.
		Particle2DLodSettings Table() => new()
		{
			Bands = new[]
			{
				new Particle2DLodBand( 100f, Particle2DLodCollapse.Full, 1 ),
				new Particle2DLodBand( 300f, Particle2DLodCollapse.Reduced, 2 ),
				new Particle2DLodBand( float.PositiveInfinity, Particle2DLodCollapse.Collapsed, 1 ),
			},
			MaxRecords = 4096,
			CellSize = 100f,
		};

		Vector2 Ref0 = new( 0f, 0f );

		registry.Add( "particle-2d-lod", "band assignment exact at 0 below at above each threshold", () =>
		{
			var s = Table();
			Check( s.BandIndexFor( 0f ) == 0, "distance 0 must be band 0 (Full)" );
			Check( s.BandIndexFor( 99f ) == 0, "distance 99 must be band 0 (Full)" );
			Check( s.BandIndexFor( 100f ) == 1, "distance exactly 100 must fall to band 1 (Reduced), not band 0" );
			Check( s.BandIndexFor( 299f ) == 1, "distance 299 must be band 1 (Reduced)" );
			Check( s.BandIndexFor( 300f ) == 2, "distance exactly 300 must fall to band 2 (Collapsed), not band 1" );
			Check( s.BandIndexFor( 10000f ) == 2, "distance 10000 must be band 2 (Collapsed)" );
			Check( s.CollapseFor( 50f ) == Particle2DLodCollapse.Full, "CollapseFor(50) must be Full" );
			Check( s.CollapseFor( 150f ) == Particle2DLodCollapse.Reduced, "CollapseFor(150) must be Reduced" );
			Check( s.CollapseFor( 500f ) == Particle2DLodCollapse.Collapsed, "CollapseFor(500) must be Collapsed" );
		} );

		registry.Add( "particle-2d-lod", "at-threshold takes the farther band", () =>
		{
			// The strictly-below rule, mirroring P35: a measure exactly
			// on a band's upper threshold belongs to the NEXT band.
			var s = Table();
			var at100 = s.Apply( new[] { Rec( 100f, 0f ) }, Ref0 );
			var at300 = s.Apply( new[] { Rec( 300f, 0f ) }, Ref0 );
			Check( s.BandIndexFor( 100f ) == 1 && s.BandIndexFor( 300f ) == 2,
				"at-threshold must resolve to the farther band (100->1, 300->2)" );
			Check( at100.Count == 1 && at300.Count == 1, "single records survive either way; the band index is the assertion" );
		} );

		registry.Add( "particle-2d-lod", "full band passes records unchanged", () =>
		{
			var s = Table();
			var input = new[] { Rec( 10f, 20f, 3f ), Rec( 30f, 40f, 7f ), Rec( 0f, 0f, 5f ) };
			var output = s.Apply( input, Ref0 );
			Check( output.Count == 3, $"Full band must keep all 3 records, got {output.Count}" );
			for ( int i = 0; i < 3; i++ )
				Check( output[i].Position == input[i].Position && output[i].Size == input[i].Size,
					$"Full record {i} must be verbatim" );
		} );

		registry.Add( "particle-2d-lod", "reduced stride 2 keeps exactly 5 of 10 with named indices", () =>
		{
			var s = Table();
			var input = new List<Particle2DDrawRecord>();
			for ( int i = 0; i < 10; i++ ) input.Add( Rec( 150f, i * 10f ) ); // distances 150..~174, all Reduced
			var output = s.Apply( input, Ref0 );
			Check( output.Count == 5, $"stride-2 Reduced on 10 must emit exactly 5, got {output.Count}" );
			int[] kept = { 0, 2, 4, 6, 8 };
			for ( int k = 0; k < 5; k++ )
				Check( output[k].Position == input[kept[k]].Position,
					$"kept record {k} must be input {kept[k]} (y={input[kept[k]].Position.Y}), got y={output[k].Position.Y}" );
		} );

		registry.Add( "particle-2d-lod", "reduced stride 3 keeps exactly 4 of 10", () =>
		{
			var s = Table();
			s.Bands = new[]
			{
				new Particle2DLodBand( 100f, Particle2DLodCollapse.Full, 1 ),
				new Particle2DLodBand( 300f, Particle2DLodCollapse.Reduced, 3 ),
				new Particle2DLodBand( float.PositiveInfinity, Particle2DLodCollapse.Collapsed, 1 ),
			};
			var input = new List<Particle2DDrawRecord>();
			for ( int i = 0; i < 10; i++ ) input.Add( Rec( 150f, i * 10f ) );
			var output = s.Apply( input, Ref0 );
			Check( output.Count == 4, $"stride-3 Reduced on 10 must emit exactly 4, got {output.Count}" );
			int[] kept = { 0, 3, 6, 9 };
			for ( int k = 0; k < 4; k++ )
				Check( output[k].Position == input[kept[k]].Position, $"kept record {k} must be input {kept[k]}" );
		} );

		registry.Add( "particle-2d-lod", "reduced counting is per band not global", () =>
		{
			var s = Table();
			// Interleaved: near (Full), mid (Reduced) x3. The Reduced
			// counter sees only mids, so mids 0 and 2 survive: global
			// inputs 1 and 5. A global counter would keep globals 0,2,4
			// instead — this test distinguishes the two.
			var input = new[] { Rec( 10f, 0f ), Rec( 150f, 0f ), Rec( 20f, 0f ), Rec( 160f, 0f ), Rec( 30f, 0f ), Rec( 170f, 0f ) };
			var output = s.Apply( input, Ref0 );
			Check( output.Count == 5, $"3 Full + 2 of 3 Reduced must be 5, got {output.Count}" );
			Check( output[0].Position.X == 10f && output[1].Position.X == 150f && output[2].Position.X == 20f
				&& output[3].Position.X == 30f && output[4].Position.X == 170f,
				$"survivors must be inputs 0,1,2,4,5 in order, got x = {string.Join( ",", output.Select( r => r.Position.X ) )}" );
		} );

		registry.Add( "particle-2d-lod", "collapsed keeps one representative per cell", () =>
		{
			var s = Table();
			var input = new[] { Rec( 1000f, 10f ), Rec( 1010f, 20f ), Rec( 1090f, 90f ), Rec( 1200f, 10f ), Rec( -10f, 1010f ) };
			// Cells (CellSize 100): (10,0) x3, (12,0) x1, (-1,10) x1 -> 3 records.
			var output = s.Apply( input, Ref0 );
			Check( output.Count == 3, $"3 occupied cells must emit exactly 3, got {output.Count}" );
			Check( output[0].Position == input[0].Position, "cell (10,0) representative must be the FIRST input in that cell" );
			Check( output[1].Position == input[3].Position, "cell (12,0) representative must be input 3" );
			Check( output[2].Position == input[4].Position, "negative-coordinate cell (-1,10) must be its own cell, input 4" );
		} );

		registry.Add( "particle-2d-lod", "collapsed representative is emitted unchanged", () =>
		{
			var s = Table();
			var input = new[] { Rec( 1000f, 10f, 9f ), Rec( 1050f, 50f, 2f ) };
			input[0].Frame = 7; input[1].Frame = 3;
			var output = s.Apply( input, Ref0 );
			Check( output.Count == 1, $"same cell must collapse to 1, got {output.Count}" );
			Check( output[0].Position == input[0].Position && output[0].Size == 9f && output[0].Frame == 7,
				"collapsed representative must be verbatim input 0 (position, size, frame), not an average" );
		} );

		registry.Add( "particle-2d-lod", "budget keeps exactly N nearest with named survivors in input order", () =>
		{
			var s = new Particle2DLodSettings
			{
				Bands = new[] { new Particle2DLodBand( float.PositiveInfinity, Particle2DLodCollapse.Full, 1 ) },
				MaxRecords = 3, CellSize = 32f,
			};
			var input = new[] { Rec( 50f, 0f ), Rec( 10f, 0f ), Rec( 40f, 0f ), Rec( 20f, 0f ), Rec( 30f, 0f ) };
			var output = s.Apply( input, Ref0 );
			Check( output.Count == 3, $"budget 3 on 5 must keep exactly 3, got {output.Count}" );
			float[] xs = { 10f, 20f, 30f };
			for ( int i = 0; i < 3; i++ )
				Check( output[i].Position.X == xs[i], $"survivor {i} must be x={xs[i]} (input order among survivors), got x={output[i].Position.X}" );
		} );

		registry.Add( "particle-2d-lod", "budget never drops a near record for a far one", () =>
		{
			var s = new Particle2DLodSettings
			{
				Bands = new[] { new Particle2DLodBand( float.PositiveInfinity, Particle2DLodCollapse.Full, 1 ) },
				MaxRecords = 2, CellSize = 32f,
			};
			var input = new[] { Rec( 900f, 0f ), Rec( 800f, 0f ), Rec( 5f, 0f ), Rec( 7f, 0f ) };
			var output = s.Apply( input, Ref0 );
			Check( output.Count == 2, $"budget 2 must keep exactly 2, got {output.Count}" );
			Check( output[0].Position.X == 5f && output[1].Position.X == 7f,
				$"far-first input must still keep the two nearest (5, 7), got ({output[0].Position.X}, {output[1].Position.X})" );
		} );

		registry.Add( "particle-2d-lod", "budget zero emits nothing and budget above count keeps all", () =>
		{
			var s = new Particle2DLodSettings
			{
				Bands = new[] { new Particle2DLodBand( float.PositiveInfinity, Particle2DLodCollapse.Full, 1 ) },
				MaxRecords = 0, CellSize = 32f,
			};
			var input = new[] { Rec( 10f, 0f ), Rec( 20f, 0f ) };
			Check( s.Apply( input, Ref0 ).Count == 0, "budget 0 must emit nothing" );
			s.MaxRecords = 99;
			Check( s.Apply( input, Ref0 ).Count == 2, "budget above count must keep all" );
		} );

		registry.Add( "particle-2d-lod", "budget applies after collapse", () =>
		{
			var s = Table();
			s.MaxRecords = 3;
			var input = new List<Particle2DDrawRecord>();
			for ( int i = 0; i < 10; i++ ) input.Add( Rec( 150f, i * 10f ) ); // Reduced stride 2 -> 5, then budget 3
			var output = s.Apply( input, Ref0 );
			Check( output.Count == 3, $"10 -> 5 collapsed -> budget 3 must be exactly 3, got {output.Count}" );
			// The 5 collapsed records sit at y = 0,20,40,60,80 (distances
			// ~150..170); the 3 nearest are y = 0,20,40.
			Check( output[0].Position.Y == 0f && output[1].Position.Y == 20f && output[2].Position.Y == 40f,
				$"budget-after-collapse survivors must be y = 0,20,40, got {string.Join( ",", output.Select( r => r.Position.Y ) )}" );
		} );

		registry.Add( "particle-2d-lod", "loud failures for bad tables budgets and reference points", () =>
		{
			var good = Table();
			Throws( () => new Particle2DLodSettings { Bands = Array.Empty<Particle2DLodBand>() }.Validate(), "empty band table" );
			Throws( () => new Particle2DLodSettings { Bands = null! }.Validate(), "null band table" );
			Throws( () => new Particle2DLodSettings
			{
				Bands = new[] { new Particle2DLodBand( 100f, Particle2DLodCollapse.Full, 1 ), new Particle2DLodBand( 100f, Particle2DLodCollapse.Full, 1 ) },
			}.Validate(), "equal (non-strictly-increasing) thresholds" );
			Throws( () => new Particle2DLodSettings
			{
				Bands = new[] { new Particle2DLodBand( 300f, Particle2DLodCollapse.Full, 1 ), new Particle2DLodBand( 100f, Particle2DLodCollapse.Full, 1 ) },
			}.Validate(), "unsorted thresholds" );
			Throws( () => new Particle2DLodSettings
			{
				Bands = new[] { new Particle2DLodBand( float.PositiveInfinity, Particle2DLodCollapse.Full, 1 ), new Particle2DLodBand( float.PositiveInfinity, Particle2DLodCollapse.Full, 1 ) },
			}.Validate(), "infinite non-last threshold" );
			Throws( () => new Particle2DLodSettings
			{
				Bands = new[] { new Particle2DLodBand( float.NaN, Particle2DLodCollapse.Full, 1 ) },
			}.Validate(), "NaN threshold" );
			Throws( () => { var s = Table(); s.MaxRecords = -1; s.Validate(); }, "negative budget" );
			Throws( () => { var s = Table(); s.CellSize = 0f; s.Validate(); }, "zero cell size" );
			Throws( () => { var s = Table(); s.CellSize = -5f; s.Validate(); }, "negative cell size" );
			Throws( () => new Particle2DLodSettings
			{
				Bands = new[] { new Particle2DLodBand( float.PositiveInfinity, Particle2DLodCollapse.Reduced, 0 ) },
			}.Validate(), "stride below 1" );
			Throws( () => good.Apply( new[] { Rec( 10f, 0f ) }, new Vector2( float.NaN, 0f ) ), "NaN reference point" );
			Throws( () => good.BandIndexFor( -1f ), "negative distance" );
			Throws( () => good.BandIndexFor( float.NaN ), "NaN distance" );
		} );

		registry.Add( "particle-2d-lod", "zero particles means zero records never an error", () =>
		{
			var output = Table().Apply( Array.Empty<Particle2DDrawRecord>(), Ref0 );
			Check( output.Count == 0, $"empty input must give empty output, got {output.Count}" );
		} );

		registry.Add( "particle-2d-lod", "lod never modifies its input list", () =>
		{
			var s = Table();
			s.MaxRecords = 2;
			var input = new List<Particle2DDrawRecord>
			{
				Rec( 10f, 0f ), Rec( 150f, 0f ), Rec( 160f, 0f ), Rec( 1000f, 10f ), Rec( 1050f, 20f ),
			};
			var snapshot = input.ToArray();
			var output = s.Apply( input, Ref0 );
			Check( output.Count < input.Count, $"collapse+budget must shrink output ({output.Count} vs {input.Count})" );
			Check( input.Count == snapshot.Length, "input count must be unchanged" );
			for ( int i = 0; i < snapshot.Length; i++ )
				Check( input[i].Position == snapshot[i].Position && input[i].Size == snapshot[i].Size,
					$"input record {i} must be untouched" );
			Check( !ReferenceEquals( output, input ), "output must be a new list, not the input list" );
		} );

		registry.Add( "particle-2d-lod", "lod is deterministic across runs", () =>
		{
			var s = Table();
			s.MaxRecords = 4;
			var input = new List<Particle2DDrawRecord>();
			for ( int i = 0; i < 12; i++ ) input.Add( Rec( 150f + i * 80f, i * 30f ) );
			var a = s.Apply( input, Ref0 );
			var b = s.Apply( input, Ref0 );
			Check( a.Count == b.Count, $"determinism: counts differ ({a.Count} vs {b.Count})" );
			for ( int i = 0; i < a.Count; i++ )
				Check( a[i].Position == b[i].Position && a[i].Size == b[i].Size, $"determinism: record {i} differs between runs" );
		} );

		registry.Add( "particle-2d-lod", "reference point is a caller input not a hidden constant", () =>
		{
			var s = Table();
			var input = new[] { Rec( 50f, 0f ), Rec( 60f, 0f ), Rec( 70f, 0f ) };
			var nearRef = s.Apply( input, new Vector2( 0f, 0f ) ); // all Full -> 3
			var farRef = s.Apply( input, new Vector2( 1000f, 0f ) ); // all Collapsed, one cell -> 1
			Check( nearRef.Count == 3, $"reference (0,0) must give 3 Full records, got {nearRef.Count}" );
			Check( farRef.Count == 1, $"reference (1000,0) must collapse the same records to 1, got {farRef.Count}" );
		} );

		registry.Add( "particle-2d-lod", "live sim records shrink but alive count is untouched", () =>
		{
			var sys = new Particle2DSystem( new Particle2DSettings { Seed = 42, Capacity = 1000 } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 100f, 100f ), Rate = 60, Speed = 200, SpreadDegrees = 90,
				LifetimeMin = 0.5f, LifetimeMax = 1.5f, SizeMin = 2, SizeMax = 6,
			} );
			sys.Step( 60 );
			Check( sys.AliveCount > 0, "sim must have live particles for this test" );
			var arr = new Particle2DDrawRecord[sys.AliveCount];
			int n = sys.WriteDrawRecords( arr );
			Check( n == sys.AliveCount, $"WriteDrawRecords returned {n} but AliveCount is {sys.AliveCount}" );
			var before = arr.ToArray();
			// Budget on live records (exact: AliveCount is certainly > 2
			// after 60 steps at rate 60/s with lifetime >= 0.5 s). Cell
			// collapse exactness is proven on synthetic records above;
			// this test proves the pass touches only the records, not
			// the sim, against a real system.
			var lod = new Particle2DLodSettings
			{
				Bands = new[] { new Particle2DLodBand( float.PositiveInfinity, Particle2DLodCollapse.Full, 1 ) },
				MaxRecords = 2, CellSize = 32f,
			};
			var output = lod.Apply( arr, new Vector2( 100f, 100f ) );
			Check( output.Count == 2, $"budget 2 on live records must give exactly 2, got {output.Count}" );
			foreach ( var r in output )
				Check( before.Any( b => b.Position == r.Position ), "every output record must be a verbatim live input record" );
			Check( sys.AliveCount == before.Length, $"sim AliveCount must be untouched ({sys.AliveCount} vs {before.Length})" );
			for ( int i = 0; i < before.Length; i++ )
				Check( arr[i].Position == before[i].Position, $"input record {i} must be untouched by the LOD pass" );
		} );
	}
}
