using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS14 (performance half): timings for the assembly solver and the
// occurrence snapshot service on small/medium repeated-part fixtures.
//
// There is NO fabricated FPS target and no pass/fail on an invented
// threshold. Timing checks assert scaling behaviour with generous margins
// (a slow CI machine must not flake them) and carry the absolute timings
// plus the fixture sizes in their failure messages, so the numbers are
// published whenever a check fails; the test runner also records each
// check's own duration. Fixture sizes are stated in every message.
// The motion budget's markers (25 parts / 24 joints / 150k faces) are used
// as fixture-size markers, not as pass criteria.
//
// One honest boundary: the fixed solver has no incremental entry point — it
// is a pure full rebuild, and inventing one would violate the "a new
// abstraction must have an actual caller" rule. The incremental-vs-cold
// comparison therefore lives where an incremental path actually exists:
// the snapshot service (Build = cold, GetOrBuild = incremental via its
// per-occurrence cache). The solver is measured cold at two sizes plus
// repeated-solve stability. There is no Mobile performance oracle for this
// ticket (AssemblyPerformanceChecks.cs was not read); these fixtures are
// designed from the ticket text.
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyPerformanceTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-performance", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		// ---- Fixture builders ----

		(AssemblyDocument Doc, List<string> OccIds) ChainDoc( int count )
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Link",
				SourceKind = "effigy-file", SourceReference = "parts/link.effigy#body-1",
			};
			doc.Definitions.Add( def );
			var ids = new List<string>();
			for ( var i = 0; i < count; i++ )
			{
				var occ = new AssemblyOccurrence
				{
					Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Link" + i,
					Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( i * 2f, 0, 0 ) ) ),
				};
				doc.Occurrences.Add( occ );
				ids.Add( occ.Id );
			}
			for ( var i = 0; i + 1 < count; i++ )
			{
				doc.Joints.Add( new AssemblyJoint
				{
					Id = AssemblyDocument.NewId(), Kind = "fixed",
					OccurrenceA = ids[i], OccurrenceB = ids[i + 1],
					FrameA = new AssemblyTransform(), FrameB = new AssemblyTransform(),
				} );
			}
			return (doc, ids);
		}
		(AssemblyDocument Doc, List<string> OccIds) OccurrencesDoc( int count, float step )
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Plate",
				SourceKind = "effigy-file", SourceReference = "parts/plate.effigy#body-1",
			};
			doc.Definitions.Add( def );
			var ids = new List<string>();
			for ( var i = 0; i < count; i++ )
			{
				var occ = new AssemblyOccurrence
				{
					Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate" + i,
					Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( i * step, 0, 0 ) ) ),
				};
				doc.Occurrences.Add( occ );
				ids.Add( occ.Id );
			}
			return (doc, ids);
		}
		PolyMesh GridMesh( int cells )
		{
			var mesh = new PolyMesh();
			var n = cells + 1;
			for ( var j = 0; j < n; j++ )
				for ( var i = 0; i < n; i++ )
					mesh.Positions.Add( new Vec3( (float)i / cells, (float)j / cells, 0f ) );
			for ( var j = 0; j < cells; j++ )
				for ( var i = 0; i < cells; i++ )
				{
					var a = j * n + i;
					var b = a + 1;
					var c = a + n;
					var d = c + 1;
					mesh.Faces.Add( new Face( new[] { a, b, d } ) );
					mesh.Faces.Add( new Face( new[] { a, d, c } ) );
				}
			return mesh;
		}
		double MeanMs( Action action, int timedRuns, int warmupRuns )
		{
			for ( var i = 0; i < warmupRuns; i++ ) action();
			var total = 0.0;
			for ( var i = 0; i < timedRuns; i++ )
			{
				var clock = Stopwatch.StartNew();
				action();
				clock.Stop();
				total += clock.Elapsed.TotalMilliseconds;
			}
			return total / timedRuns;
		}

		// ---- Solver timings ----

		Check( "solve-cold-chain-8", () =>
		{
			var (doc, _) = ChainDoc( 8 );
			var mean = MeanMs( () =>
			{
				var result = AssemblyFixedJointSolver.Solve( doc );
				Require( result.Success, "8-chain solve failed" );
				Require( result.SolvedTransforms.Count == 8, "8-chain must solve 8 occurrences" );
			}, timedRuns: 20, warmupRuns: 3 );
			Require( double.IsFinite( mean ) && mean >= 0, $"8-chain cold solve mean must be finite, got {mean}" );
		} );

		Check( "solve-cold-chain-64-scaling", () =>
		{
			var (doc8, _) = ChainDoc( 8 );
			var mean8 = MeanMs( () =>
			{
				var result = AssemblyFixedJointSolver.Solve( doc8 );
				Require( result.Success, "8-chain solve failed" );
			}, timedRuns: 20, warmupRuns: 3 );
			var (doc64, _) = ChainDoc( 64 );
			var mean64 = MeanMs( () =>
			{
				var result = AssemblyFixedJointSolver.Solve( doc64 );
				Require( result.Success, "64-chain solve failed" );
				Require( result.SolvedTransforms.Count == 64, "64-chain must solve 64 occurrences" );
			}, timedRuns: 20, warmupRuns: 3 );
			// Generous scaling bound, not a performance target: an 8x larger
			// chain must not cost 100x the time.
			Require( mean64 < 100.0 * mean8,
				$"solve scaling: 64-chain mean {mean64:F3}ms vs 8-chain mean {mean8:F3}ms (ratio {mean64 / Math.Max( mean8, 1e-9 ):F1}x, bound 100x)" );
		} );

		Check( "solve-repeated-stability-64", () =>
		{
			var (doc, _) = ChainDoc( 64 );
			for ( var i = 0; i < 5; i++ )
				Require( AssemblyFixedJointSolver.Solve( doc ).Success, "warmup solve failed" );
			var max = 0.0;
			var total = 0.0;
			const int runs = 50;
			for ( var i = 0; i < runs; i++ )
			{
				var clock = Stopwatch.StartNew();
				var result = AssemblyFixedJointSolver.Solve( doc );
				clock.Stop();
				Require( result.Success, $"run {i} failed" );
				max = Math.Max( max, clock.Elapsed.TotalMilliseconds );
				total += clock.Elapsed.TotalMilliseconds;
			}
			var mean = total / runs;
			Require( max < 10.0 * mean,
				$"solve stability: 64-chain max {max:F3}ms vs mean {mean:F3}ms over {runs} runs (ratio {max / Math.Max( mean, 1e-9 ):F1}x, bound 10x)" );
		} );

		Check( "solve-degraded-threshold-fixture-26", () =>
		{
			// 26 occurrences / 25 fixed joints: one past the motion budget's
			// 25-part degraded marker. The solve must still succeed; the
			// timing is reported, not thresholded.
			var (doc, _) = ChainDoc( 26 );
			var mean = MeanMs( () =>
			{
				var result = AssemblyFixedJointSolver.Solve( doc );
				Require( result.Success, "26-chain solve failed" );
				Require( result.SolvedTransforms.Count == 26, "26-chain must solve 26 occurrences" );
			}, timedRuns: 10, warmupRuns: 3 );
			Require( double.IsFinite( mean ) && mean >= 0,
				$"26-chain cold solve mean must be finite, got {mean:F3}ms (26 occurrences, 25 fixed joints)" );
		} );

		// ---- Snapshot timings: cold Build vs incremental GetOrBuild ----
		//
		// Grid 36x36 cells = 2,592 faces per occurrence; 64 occurrences =
		// 165,888 faces, near the motion budget's 150k-face marker. All
		// occurrences share one definition and one source mesh instance,
		// which also proves Build clones instead of mutating its source.

		Check( "snapshot-cold-build-64", () =>
		{
			var (doc, _) = OccurrencesDoc( 64, 2f );
			var mesh = GridMesh( 36 );
			Require( mesh.Faces.Count == 2592, $"grid must have 2592 faces, got {mesh.Faces.Count}" );
			AssemblySourceResolver resolver = _ => (mesh, 1, "Steel");
			var mean = MeanMs( () =>
			{
				var service = new AssemblySnapshotService();
				var snap = service.Build( doc, resolver );
				Require( snap.VisibleCount == 64, $"cold build visible must be 64, got {snap.VisibleCount}" );
				var totalFaces = snap.Occurrences.Sum( o => o.Mesh?.Faces.Count ?? 0 );
				Require( totalFaces == 165888, $"cold build total faces must be 165888, got {totalFaces}" );
			}, timedRuns: 5, warmupRuns: 2 );
			Require( double.IsFinite( mean ) && mean >= 0,
				$"snapshot cold build mean must be finite, got {mean:F3}ms (64 occurrences, 165888 faces)" );
		} );

		Check( "snapshot-incremental-after-one-move", () =>
		{
			var (doc, ids) = OccurrencesDoc( 64, 2f );
			var mesh = GridMesh( 36 );
			AssemblySourceResolver resolver = _ => (mesh, 1, "Steel");
			var service = new AssemblySnapshotService();
			var warm = service.Build( doc, resolver );
			Require( warm.VisibleCount == 64, "warm build visible must be 64" );
			var coldMean = MeanMs( () =>
			{
				var cold = new AssemblySnapshotService();
				var snap = cold.Build( doc, resolver );
				Require( snap.VisibleCount == 64, "cold build visible must be 64" );
			}, timedRuns: 5, warmupRuns: 2 );
			var move = 0;
			var incrementalMean = MeanMs( () =>
			{
				// Exactly one occurrence changes per run, so exactly one
				// cached mesh is rebuilt; the other 63 are reused.
				var occ = doc.Occurrences[move % doc.Occurrences.Count];
				move++;
				occ.Transform = AssemblyTransform.FromXform(
					Xform.Translate( new Vec3( ( move % 64 ) * 2f + 0.01f * move, 0, 0 ) ) );
				var snap = service.GetOrBuild( doc, resolver );
				Require( snap.VisibleCount == 64, "incremental visible must be 64" );
			}, timedRuns: 10, warmupRuns: 2 );
			Require( incrementalMean < 0.5 * coldMean,
				$"snapshot incremental mean {incrementalMean:F3}ms not < 0.5x cold mean {coldMean:F3}ms " +
				$"(64 occurrences, 165888 faces, one moved per run)" );
			_ = ids;
		} );

		Check( "snapshot-incremental-reuses-unchanged-meshes", () =>
		{
			var (doc, _) = OccurrencesDoc( 64, 2f );
			var mesh = GridMesh( 36 );
			AssemblySourceResolver resolver = _ => (mesh, 1, "Steel");
			var service = new AssemblySnapshotService();
			var before = service.Build( doc, resolver );
			var beforeMeshes = before.Occurrences.ToDictionary( o => o.OccurrenceId, o => o.Mesh );
			var moved = doc.Occurrences[3];
			moved.Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			var after = service.GetOrBuild( doc, resolver );
			Require( service.CacheMisses == 1, $"one changed key must be one cache miss, got {service.CacheMisses}" );
			var same = 0;
			var different = 0;
			foreach ( var occ in after.Occurrences )
			{
				if ( ReferenceEquals( beforeMeshes[occ.OccurrenceId], occ.Mesh ) ) same++;
				else different++;
			}
			Require( same == 63 && different == 1,
				$"incremental rebuild must reuse 63 meshes and rebuild 1, got {same} reused + {different} rebuilt" );
		} );

		Check( "snapshot-no-change-cache-hit", () =>
		{
			var (doc, _) = OccurrencesDoc( 8, 2f );
			var mesh = GridMesh( 8 );
			AssemblySourceResolver resolver = _ => (mesh, 1, "Steel");
			var service = new AssemblySnapshotService();
			var first = service.Build( doc, resolver );
			Require( service.CacheHits == 0 && service.CacheMisses == 0,
				$"Build must not touch counters, got {service.CacheHits} hits / {service.CacheMisses} misses" );
			var second = service.GetOrBuild( doc, resolver );
			Require( service.CacheHits == 1, $"unchanged GetOrBuild must be one hit, got {service.CacheHits}" );
			Require( service.CacheMisses == 0, $"unchanged GetOrBuild must be zero misses, got {service.CacheMisses}" );
			Require( ReferenceEquals( first, second ), "unchanged GetOrBuild must return the cached snapshot" );
		} );

		Check( "snapshot-cold-build-source-mesh-untouched", () =>
		{
			var (doc, _) = OccurrencesDoc( 64, 2f );
			var mesh = GridMesh( 36 );
			var firstPosition = mesh.Positions[0];
			var lastPosition = mesh.Positions[mesh.Positions.Count - 1];
			AssemblySourceResolver resolver = _ => (mesh, 1, "Steel");
			(Vec3 Min, Vec3 Max)? bounds = null;
			for ( var i = 0; i < 3; i++ )
			{
				var service = new AssemblySnapshotService();
				var snap = service.Build( doc, resolver );
				Require( snap.VisibleCount == 64, "visible must be 64" );
				if ( bounds == null ) bounds = snap.Bounds;
				else
				{
					Require( MathF.Abs( snap.Bounds.Min.x - bounds.Value.Min.x ) < 1e-6f
						&& MathF.Abs( snap.Bounds.Min.y - bounds.Value.Min.y ) < 1e-6f
						&& MathF.Abs( snap.Bounds.Min.z - bounds.Value.Min.z ) < 1e-6f
						&& MathF.Abs( snap.Bounds.Max.x - bounds.Value.Max.x ) < 1e-6f
						&& MathF.Abs( snap.Bounds.Max.y - bounds.Value.Max.y ) < 1e-6f
						&& MathF.Abs( snap.Bounds.Max.z - bounds.Value.Max.z ) < 1e-6f,
						$"repeated cold builds must agree on bounds (run {i})" );
				}
			}
			Require( mesh.Positions[0].x == firstPosition.x && mesh.Positions[0].y == firstPosition.y
				&& mesh.Positions[0].z == firstPosition.z, "source mesh first position mutated" );
			Require( mesh.Positions[mesh.Positions.Count - 1].x == lastPosition.x
				&& mesh.Positions[mesh.Positions.Count - 1].y == lastPosition.y
				&& mesh.Positions[mesh.Positions.Count - 1].z == lastPosition.z, "source mesh last position mutated" );
			Require( mesh.Positions.Count == 37 * 37, $"source mesh vertex count must stay 1369, got {mesh.Positions.Count}" );
			Require( bounds.HasValue, "no bounds recorded" );
			Require( MathF.Abs( bounds.Value.Min.x - 0f ) < 1e-3f && MathF.Abs( bounds.Value.Min.y - 0f ) < 1e-3f
				&& MathF.Abs( bounds.Value.Max.x - 127f ) < 1e-3f && MathF.Abs( bounds.Value.Max.y - 1f ) < 1e-3f,
				$"bounds must be (0,0,0)-(127,1,0), got ({bounds.Value.Min.x},{bounds.Value.Min.y},{bounds.Value.Min.z})-({bounds.Value.Max.x},{bounds.Value.Max.y},{bounds.Value.Max.z})" );
		} );
	}
}
