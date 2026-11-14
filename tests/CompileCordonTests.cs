#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Effigy;

/// <summary>
/// hammer-compile-cordon-region: cordon / region compile + compile-visible
/// layers only (kernel/Effigy/Map/CompileCordon.cs) — Cordon struct,
/// ApplyCordon sealed sub-documents, region presets (selection / layer),
/// compile-visible-only filter, leak bisect.
///
/// Fixtures in tests/fixtures/hammer/hammer-compile-cordon-region/:
/// two-room-cordon.json (two rooms + layer tags) and leaking-two-room.json
/// (same map with a 1-unit gap in room B's east wall).
///
/// NOT registered in TestRegistry.cs yet — the final integrator adds the one
/// line (same convention as CompileRunnerTests). Run this group by name
/// ("compilecordon") once registered. PO WORKFLOW OVERRIDE 2026-11-13:
/// implementation done here is NOT runtime verified — no full project build
/// or app launch was run for this ticket; the final integration gate runs
/// the group and the coordinated build.
/// </summary>
internal static class CompileCordonTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "compilecordon", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: wrong exception {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}
		bool Near( Vec3 a, Vec3 b, float eps = 1e-3f ) => a.AlmostEquals( b, eps );
		bool NearF( float a, float b, float eps = 1e-3f ) => Math.Abs( a - b ) <= eps;

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-compile-cordon-region" );
					if ( File.Exists( Path.Combine( candidate, "two-room-cordon.json" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "compilecordon fixture dir not found (two-room-cordon.json missing above cwd / BaseDirectory)" );
		}

		(CompileInput Input, Dictionary<int, string> Layers) LoadFixture( string name )
		{
			var path = Path.Combine( FixtureDir(), name + ".json" );
			if ( !File.Exists( path ) ) throw new Exception( $"fixture {name}.json not found under tests/fixtures/hammer/hammer-compile-cordon-region/" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var input = new CompileInput( root.GetProperty( "name" ).GetString() ?? name );
			var layers = new Dictionary<int, string>();
			if ( root.TryGetProperty( "playerStart", out var start ) && start.ValueKind == JsonValueKind.Array )
				input.PlayerStart = new Vec3( start[0].GetSingle(), start[1].GetSingle(), start[2].GetSingle() );
			foreach ( var light in root.GetProperty( "lights" ).EnumerateArray() )
			{
				var p = light.GetProperty( "position" );
				var c = light.GetProperty( "color" );
				input.Lights.Add( new CompileLight(
					new Vec3( p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle() ),
					light.GetProperty( "intensity" ).GetSingle(),
					new Vec3( c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle() ),
					light.TryGetProperty( "range", out var r ) ? r.GetSingle() : 0f ) );
			}
			foreach ( var brush in root.GetProperty( "brushes" ).EnumerateArray() )
			{
				var material = brush.TryGetProperty( "material", out var m ) ? m.GetString() ?? "default" : "default";
				var isDetail = brush.TryGetProperty( "detail", out var d ) && d.GetBoolean();
				var id = brush.GetProperty( "id" ).GetInt32();
				var brushName = brush.GetProperty( "name" ).GetString() ?? $"brush-{id}";
				layers[id] = brush.TryGetProperty( "layer", out var layer ) ? layer.GetString() ?? "" : "";
				if ( brush.TryGetProperty( "box", out var box ) )
				{
					var min = box.GetProperty( "min" );
					var max = box.GetProperty( "max" );
					input.Brushes.Add( CompileBrush.Box( brushName, id,
						new Vec3( min[0].GetSingle(), min[1].GetSingle(), min[2].GetSingle() ),
						new Vec3( max[0].GetSingle(), max[1].GetSingle(), max[2].GetSingle() ),
						material, isDetail ) );
				}
				else if ( brush.TryGetProperty( "faces", out var faces ) )
				{
					var built = new CompileBrush( brushName, id, Array.Empty<CompileFace>(), isDetail );
					foreach ( var face in faces.EnumerateArray() )
					{
						var n = face.GetProperty( "normal" );
						built.Faces.Add( new CompileFace(
							new Vec3( n[0].GetSingle(), n[1].GetSingle(), n[2].GetSingle() ),
							face.GetProperty( "distance" ).GetSingle(),
							face.TryGetProperty( "material", out var fm ) ? fm.GetString() ?? material : material ) );
					}
					input.Brushes.Add( built );
				}
				else throw new Exception( $"fixture brush '{brushName}' has neither 'box' nor 'faces'" );
			}
			return (input, layers);
		}

		string WorkDir( string name )
		{
			var dir = Path.Combine( settings.WorkDirectory, "compilecordon", name );
			if ( Directory.Exists( dir ) ) Directory.Delete( dir, recursive: true );
			Directory.CreateDirectory( dir );
			return dir;
		}

		CompileResult Run( CompileInput input, CompilePreset preset, string workName )
		{
			var options = new CompileOptions { OutputFolder = WorkDir( workName ) };
			return CompileRunner.Run( input, preset, options );
		}

		Func<CompileBrush, string> LayerOf( Dictionary<int, string> layers ) =>
			b => layers.TryGetValue( b.Id, out var layer ) ? layer : "";

		// Room A of the two-room map: the west room plus the shared wall.
		static Cordon RoomACordon() => new( new Vec3( -272, -144, -16 ), new Vec3( 8, 144, 144 ) );
		// Room B of the two-room map: the east room plus the shared wall.
		static Cordon RoomBCordon() => new( new Vec3( -8, -144, -16 ), new Vec3( 272, 144, 144 ) );

		CompileBrush FindBrush( CompileInput input, string name ) =>
			input.Brushes.FirstOrDefault( b => b is not null && b.Name == name )
				?? throw new Exception( $"brush '{name}' not in input" );

		CompileFace FaceWithNormal( CompileBrush brush, Vec3 normal ) =>
			brush.Faces.FirstOrDefault( f => f.Normal.AlmostEquals( normal, 1e-4f ) )
				?? throw new Exception( $"brush '{brush.Name}' has no face with normal {normal}" );

		// -- Cordon value type ------------------------------------------------

		Check( "cordon rejects swapped bounds and zero-volume boxes", () =>
		{
			Throws<ArgumentException>( () => new Cordon( new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 1 ) ), "swapped x accepted" );
			Throws<ArgumentException>( () => new Cordon( new Vec3( 0, 1, 0 ), new Vec3( 1, 0, 1 ) ), "swapped y accepted" );
			Throws<ArgumentException>( () => new Cordon( new Vec3( 0, 0, 1 ), new Vec3( 1, 1, 0 ) ), "swapped z accepted" );
			Throws<ArgumentException>( () => new Cordon( new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 1 ) ), "zero x extent accepted" );
			Throws<ArgumentException>( () => new Cordon( new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 1 ) ), "zero y extent accepted" );
			Throws<ArgumentException>( () => new Cordon( new Vec3( 0, 0, 0 ), new Vec3( 1, 1, 0 ) ), "zero z extent accepted" );
			var c = new Cordon( new Vec3( -272, -144, -16 ), new Vec3( 8, 144, 144 ) );
			Require( Near( c.Center, new Vec3( -132, 0, 64 ) ), $"centre {c.Center}, expected (-132, 0, 64)" );
			Require( Near( c.Size, new Vec3( 280, 288, 160 ) ), $"size {c.Size}, expected (280, 288, 160)" );
			Require( c.LongestAxis == CordonAxis.Y, $"longest axis {c.LongestAxis}, expected Y" );
			Require( c == new Cordon( new Vec3( -272, -144, -16 ), new Vec3( 8, 144, 144 ) ), "equal cordons compare unequal" );
		} );

		Check( "cordon contains and intersects are boundary-inclusive", () =>
		{
			var c = RoomACordon();
			Require( c.Contains( new Vec3( -132, 0, 64 ) ), "centre not contained" );
			Require( c.Contains( new Vec3( 8, 144, 144 ) ), "max corner not contained (boundary must be inclusive)" );
			Require( c.Contains( new Vec3( -272, -144, -16 ) ), "min corner not contained (boundary must be inclusive)" );
			Require( !c.Contains( new Vec3( 8.01f, 0, 64 ) ), "point past max.x contained" );
			Require( c.Intersects( new Vec3( 8, -144, -16 ), new Vec3( 272, 144, 144 ) ), "box touching at x 8 does not intersect" );
			Require( !c.Intersects( new Vec3( 8.01f, -144, -16 ), new Vec3( 272, 144, 144 ) ), "disjoint box intersects" );
		} );

		Check( "cordon from bounds and from brushes unions with margin", () =>
		{
			var fromPoints = Cordon.FromBounds( new[] { new Vec3( 0, 0, 0 ), new Vec3( 10, 20, 30 ) } );
			Require( Near( fromPoints.Min, new Vec3( 0, 0, 0 ) ) && Near( fromPoints.Max, new Vec3( 10, 20, 30 ) ), $"from-bounds {fromPoints}" );
			var grown = Cordon.FromBounds( new[] { new Vec3( 0, 0, 0 ), new Vec3( 10, 20, 30 ) }, margin: 16f );
			Require( Near( grown.Min, new Vec3( -16, -16, -16 ) ) && Near( grown.Max, new Vec3( 26, 36, 46 ) ), $"margin-16 {grown}" );
			Throws<ArgumentException>( () => Cordon.FromBounds( Array.Empty<Vec3>() ), "empty points accepted" );
			Throws<ArgumentException>( () => Cordon.FromBounds( new[] { new Vec3( 0, 0, 0 ) }, margin: -1f ), "negative margin accepted" );
			var (input, _) = LoadFixture( "two-room-cordon" );
			var whole = Cordon.FromBrushes( input.Brushes );
			Require( Near( whole.Min, new Vec3( -272, -144, -16 ) ) && Near( whole.Max, new Vec3( 272, 144, 144 ) ),
				$"whole-map cordon {whole}, expected (-272,-144,-16)..(272,144,144)" );
			Throws<ArgumentException>( () => Cordon.FromBrushes( Array.Empty<CompileBrush>() ), "empty brushes accepted" );
		} );

		Check( "cordon from selection uses the selected brushes", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			// Shared-wall segments only (ids 6, 7, 8): x -8..8, y -144..144, z 0..128, +16 margin.
			var c = Cordon.FromSelection( input, new[] { 6, 7, 8 } );
			Require( Near( c.Min, new Vec3( -24, -160, -16 ) ) && Near( c.Max, new Vec3( 24, 160, 144 ) ),
				$"selection cordon {c}, expected (-24,-160,-16)..(24,160,144)" );
			Throws<ArgumentException>( () => Cordon.FromSelection( input, new[] { 6, 999 } ), "unknown brush id accepted" );
			Throws<ArgumentException>( () => Cordon.FromSelection( input, Array.Empty<int>() ), "empty selection accepted" );
		} );

		Check( "cordon from layer uses the named layer", () =>
		{
			var (input, layers) = LoadFixture( "two-room-cordon" );
			// wall_west only: x -272..-256, y -144..144, z 0..128, +16 margin.
			var c = Cordon.FromLayer( input, "room-a", LayerOf( layers ) );
			Require( Near( c.Min, new Vec3( -288, -160, -16 ) ) && Near( c.Max, new Vec3( -240, 160, 144 ) ),
				$"room-a cordon {c}, expected (-288,-160,-16)..(-240,160,144)" );
			Throws<ArgumentException>( () => Cordon.FromLayer( input, "no-such-layer", LayerOf( layers ) ), "unknown layer accepted" );
			Throws<ArgumentException>( () => Cordon.FromLayer( input, "", LayerOf( layers ) ), "empty layer accepted" );
		} );

		Check( "cordon bisect splits at the midpoint", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			var whole = Cordon.FromBrushes( input.Brushes );
			var (first, second) = whole.Bisect( CordonAxis.X );
			Require( NearF( first.Max.x, 0f ) && NearF( second.Min.x, 0f ), $"split faces {first.Max.x} / {second.Min.x}, expected 0" );
			Require( Near( first.Min, whole.Min ) && Near( second.Max, whole.Max ), "bisect halves do not cover the whole cordon" );
			Require( Near( first.Max, new Vec3( 0, 144, 144 ) ) && Near( second.Min, new Vec3( 0, -144, -16 ) ), "bisect halves have wrong bounds" );
			var (top, bottom) = whole.Bisect( CordonAxis.Z );
			Require( NearF( top.Max.z, 64f ) && NearF( bottom.Min.z, 64f ), $"z split faces {top.Max.z} / {bottom.Min.z}, expected 64" );
		} );

		// -- ApplyCordon (pure) ------------------------------------------------

		Check( "apply cordon keeps one room, clips straddlers, seals the doorway", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			var beforeFloor = FindBrush( input, "floor" );
			Require( NearF( FaceWithNormal( beforeFloor, new Vec3( 1, 0, 0 ) ).Distance, 272f ), "fixture floor does not span to x 272" );
			var sub = CompileCordon.ApplyCordon( input, RoomACordon(), out var report );
			Require( report.KeptBrushes == 8, $"kept {report.KeptBrushes}, expected 8" );
			Require( report.DroppedBrushes == 1 && report.DroppedBrushNames.SequenceEqual( new[] { "wall_east" } ),
				$"dropped {string.Join( ",", report.DroppedBrushNames )}, expected wall_east" );
			Require( report.ClippedBrushes == 4
				&& report.ClippedBrushNames.OrderBy( n => n ).SequenceEqual( new[] { "ceiling", "floor", "wall_north", "wall_south" } ),
				$"clipped {string.Join( ",", report.ClippedBrushNames )}, expected floor/ceiling/wall_south/wall_north" );
			Require( report.SealBrushes == 1, $"seal brushes {report.SealBrushes}, expected 1 (east face only: the other five faces coincide with the hull)" );
			Require( report.LightsKept == 1 && report.LightsDropped == 0, $"lights kept {report.LightsKept} dropped {report.LightsDropped}" );
			Require( report.PlayerStartKept, "player start inside room A was dropped" );
			Require( sub.Name == "two-room-cordon (cordon)", $"output name '{sub.Name}', expected two-room-cordon (cordon)" );
			Require( sub.Brushes.Count == 9, $"output brushes {sub.Brushes.Count}, expected 8 kept + 1 seal" );
			Require( sub.Brushes.All( b => b is null || b.Name != "wall_east" ), "wall_east survived the cordon" );
			// The floor was clipped, not just kept: 6 faces (5 original + the
			// cut face), and its +x face is now the cordon boundary at x 8.
			var floor = FindBrush( sub, "floor" );
			Require( floor.Faces.Count == 6, $"clipped floor has {floor.Faces.Count} faces, expected 6 (cut face replaces the +x face)" );
			Require( NearF( FaceWithNormal( floor, new Vec3( 1, 0, 0 ) ).Distance, 8f ),
				$"clipped floor +x face at {FaceWithNormal( floor, new Vec3( 1, 0, 0 ) ).Distance}, expected 8" );
			Require( FaceWithNormal( floor, new Vec3( 1, 0, 0 ) ).Material == Cordon.SealMaterial,
				"cut face does not carry the seal material" );
			var seals = sub.Brushes.Where( b => b is not null && b.Name.StartsWith( "cordon_seal_", StringComparison.Ordinal ) ).ToList();
			Require( seals.Count == 1, $"seal brushes named {seals.Count}, expected 1" );
			Require( seals.All( b => b.Faces.All( f => f.Material == Cordon.SealMaterial ) ), "seal brush has a non-seal face" );
			Require( seals.All( b => b.Id < 0 ), "seal brush does not have a synthetic negative id" );
			Require( seals.Select( b => b.Name ).SequenceEqual( new[] { "cordon_seal_east" } ),
				$"seal names {string.Join( ",", seals.Select( b => b.Name ) )}, expected cordon_seal_east only" );
			// The source input is never mutated.
			Require( NearF( FaceWithNormal( beforeFloor, new Vec3( 1, 0, 0 ) ).Distance, 272f ), "ApplyCordon mutated the source input" );
			Require( input.Brushes.Count == 9, "ApplyCordon mutated the source brush list" );
		} );

		Check( "cordon around the whole map adds no seal", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			var whole = Cordon.FromBrushes( input.Brushes );
			var sub = CompileCordon.ApplyCordon( input, whole, out var report );
			Require( report.KeptBrushes == 9 && report.DroppedBrushes == 0 && report.ClippedBrushes == 0 && report.SealBrushes == 0,
				$"whole-map cordon kept {report.KeptBrushes} dropped {report.DroppedBrushes} clipped {report.ClippedBrushes} seals {report.SealBrushes}" );
			Require( sub.Brushes.Select( b => b?.Name ).SequenceEqual( input.Brushes.Select( b => b?.Name ) ),
				"whole-map cordon changed the brush list" );
		} );

		Check( "cordon drops a player start and lights outside it", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			input.PlayerStart = new Vec3( 160, 0, 32 );
			input.Lights.Add( new CompileLight( new Vec3( 160, 0, 96 ), 1f ) );
			var sub = CompileCordon.ApplyCordon( input, RoomACordon(), out var report );
			Require( sub.PlayerStart is null && !report.PlayerStartKept, "player start outside the cordon was kept" );
			Require( report.LightsKept == 1 && report.LightsDropped == 1, $"lights kept {report.LightsKept} dropped {report.LightsDropped}, expected 1/1" );
		} );

		Check( "apply cordon is idempotent", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			var once = CompileCordon.ApplyCordon( input, RoomACordon(), out var first );
			var twice = CompileCordon.ApplyCordon( once, RoomACordon(), out var second );
			Require( twice.Brushes.Select( b => b?.Name ).OrderBy( n => n ).SequenceEqual(
				once.Brushes.Select( b => b?.Name ).OrderBy( n => n ) ), "second application changed the brush set" );
			Require( second.DroppedBrushes == 1 && second.SealBrushes == 1 && second.ClippedBrushes == 0,
				$"second pass dropped {second.DroppedBrushes} seals {second.SealBrushes} clipped {second.ClippedBrushes}, expected 1/1/0 (old east seal dropped, re-added, nothing re-clipped)" );
			Require( first.SealBrushes == 1, "first pass seal count moved" );
		} );

		Check( "a brush merely touching the cordon from outside is dropped", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			// Flush against the room-A east face, exactly like a seal box from
			// a previous pass: zero interior volume inside the cordon.
			input.Brushes.Add( CompileBrush.Box( "outsider", 99,
				new Vec3( 8, -144, -16 ), new Vec3( 24, 144, 144 ), "wall" ) );
			var sub = CompileCordon.ApplyCordon( input, RoomACordon(), out var report );
			Require( report.DroppedBrushNames.Contains( "outsider" ), "touching-from-outside brush was kept" );
			Require( report.KeptBrushes == 8 && report.SealBrushes == 1,
				$"kept {report.KeptBrushes} seals {report.SealBrushes}, expected 8/1" );
			Require( sub.Brushes.All( b => b is null || b.Name != "outsider" ), "outsider survived the cordon" );
		} );

		Check( "multiple cordons keep the union", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			var sub = CompileCordon.ApplyCordon( input, new[] { RoomACordon(), RoomBCordon() }, out var report );
			Require( report.KeptBrushes == 9 && report.DroppedBrushes == 0 && report.ClippedBrushes == 0,
				$"union kept {report.KeptBrushes} dropped {report.DroppedBrushes} clipped {report.ClippedBrushes}, expected 9/0/0" );
			Require( report.SealBrushes == 0, $"union seals {report.SealBrushes}, expected 0 (each cordon's cut faces land inside the other cordon, and the union seals itself)" );
			var seals = sub.Brushes.Where( b => b is not null && b.Name.StartsWith( "cordon_seal_", StringComparison.Ordinal ) ).ToList();
			Require( seals.Count == 0, "union compile added a seal inside the compiled region" );
		} );

		Check( "apply cordon rejects nulls and treats an empty list as a no-op", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			Throws<ArgumentNullException>( () => CompileCordon.ApplyCordon( null!, RoomACordon() ), "null input accepted" );
			Throws<ArgumentNullException>( () => CompileCordon.ApplyCordon( input, (IEnumerable<Cordon>)null! ), "null cordons accepted" );
			var same = CompileCordon.ApplyCordon( input, Array.Empty<Cordon>() );
			Require( ReferenceEquals( same, input ), "empty cordon list did not return the input unchanged" );
		} );

		// -- Compile-visible-only (pure) ----------------------------------------

		Check( "visible-only keeps unlayered and visible layers", () =>
		{
			var (input, layers) = LoadFixture( "two-room-cordon" );
			var filtered = CompileCordon.ApplyVisibleOnly( input, new[] { "room-a" }, LayerOf( layers ), out var dropped );
			Require( dropped == 1, $"dropped {dropped}, expected 1 (wall_east)" );
			Require( filtered.Brushes.All( b => b is null || b.Name != "wall_east" ), "hidden wall_east survived" );
			Require( filtered.Brushes.Count == 8, $"filtered brushes {filtered.Brushes.Count}, expected 8" );
			Require( filtered.Name == input.Name, "visible-only filter renamed the map" );
			Require( filtered.PlayerStart is not null && input.PlayerStart is not null
				&& filtered.PlayerStart.Value.AlmostEquals( input.PlayerStart.Value )
				&& filtered.Lights.Count == input.Lights.Count,
				"visible-only filter dropped lights or the player start (it filters brushes only)" );
			var noneVisible = CompileCordon.ApplyVisibleOnly( input, Array.Empty<string>(), LayerOf( layers ), out dropped );
			Require( dropped == 2, $"empty visible set dropped {dropped}, expected 2 (both room walls)" );
			Require( noneVisible.Brushes.Count == 7, "empty visible set kept a layered brush" );
			Throws<ArgumentNullException>( () => CompileCordon.ApplyVisibleOnly( null!, new[] { "room-a" }, LayerOf( layers ) ), "null input accepted" );
			Throws<ArgumentNullException>( () => CompileCordon.ApplyVisibleOnly( input, null!, LayerOf( layers ) ), "null visible set accepted" );
			Throws<ArgumentNullException>( () => CompileCordon.ApplyVisibleOnly( input, new[] { "room-a" }, null! ), "null layer lookup accepted" );
		} );

		// -- Runner: cordon compiles --------------------------------------------

		Check( "cordon around one room compiles only that room", () =>
		{
			var (input, _) = LoadFixture( "two-room-cordon" );
			var sub = CompileCordon.ApplyCordon( input, RoomACordon() );
			var result = Run( sub, CompilePreset.Final, "room-a" );
			Require( result.Succeeded, "cordoned compile failed: " + result.Error );
			Require( result.Leak is not null && !result.Leak.IsLeaked, "cordoned room reported a leak (the seal failed)" );
			var leafA = result.Bsp!.LeafContaining( new Vec3( -160, 100, 32 ) );
			var leafB = result.Bsp.LeafContaining( new Vec3( 160, 100, 32 ) );
			Require( leafA is not null && leafB is not null, "a room centre is in no leaf" );
			Require( !leafA.IsSolid && !leafA.IsVoid, "room A centre is not an interior leaf" );
			Require( leafB.IsVoid, $"room B centre is not void (isSolid {leafB.IsSolid}, isVoid {leafB.IsVoid}): room B was compiled" );
			Require( leafA.Id != leafB.Id, "both room centres landed in one leaf" );
			Require( !result.Pvs!.VisibleFrom( leafA.Id ).Contains( leafB.Id ), "room A sees room B: the cordon did not restrict visibility" );
		} );

		Check( "cordon around the sealed half of a leaking map compiles clean", () =>
		{
			var (input, _) = LoadFixture( "leaking-two-room" );
			var control = Run( input, CompilePreset.QuickPlay, "leaking-control" );
			Require( !control.Succeeded, "leaking two-room map compiled successfully (control is broken)" );
			Require( control.Issues.Any( i => i.Code == "leak" ), "control run has no 'leak' issue: " + string.Join( "; ", control.Issues.Select( i => i.Code ) ) );
			var sub = CompileCordon.ApplyCordon( input, RoomACordon() );
			var result = Run( sub, CompilePreset.QuickPlay, "leaking-room-a" );
			Require( result.Succeeded, "cordoned sealed half failed: " + result.Error );
			Require( result.Leak is not null && !result.Leak.IsLeaked, "cordoned sealed half reported a leak" );
			Require( !result.Issues.Any( i => i.Code == "leak" ), "cordoned sealed half carries a leak issue" );
		} );

		Check( "cordon around the leaking half fails with the leak", () =>
		{
			var (input, _) = LoadFixture( "leaking-two-room" );
			input.PlayerStart = new Vec3( 160, 0, 32 );
			// Control first, same start: the full map leaks through the same gap.
			var control = Run( input, CompilePreset.QuickPlay, "leaking-control-b" );
			Require( !control.Succeeded, "leaking two-room map compiled successfully (control is broken)" );
			var controlLeak = control.Issues.FirstOrDefault( i => i.Code == "leak" );
			Require( controlLeak is not null, "control run has no 'leak' issue: " + string.Join( "; ", control.Issues.Select( i => i.Code ) ) );
			var sub = CompileCordon.ApplyCordon( input, RoomBCordon() );
			var result = Run( sub, CompilePreset.QuickPlay, "leaking-room-b" );
			Require( !result.Succeeded, "cordoned leaking half compiled successfully" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			var leak = result.Issues.FirstOrDefault( i => i.Code == "leak" );
			Require( leak is not null, "no 'leak' issue: " + string.Join( "; ", result.Issues.Select( i => i.Code ) ) );
			Require( result.Leak is not null && result.Leak.IsLeaked, "no leak result recorded" );
			// The cordon must not move the leak: the cordoned run reports the
			// same gap portal as the full map. (The runner reports the gap
			// portal's centroid, (132, 0, 64) for this fixture — the room's
			// midpoint cross-section, not the slot itself — which is the BSP
			// builder's reporting, owned by the native pipeline, not by
			// cordons. What this ticket owns is that the cordon preserves it.)
			Require( leak.Location.Equals( controlLeak.Location ),
				$"cordoned leak location {leak.Location} differs from the full map's {controlLeak.Location}" );
			// ... and the leak is in room B, not room A.
			Require( leak.Location.x >= 8f && leak.Location.x <= 272f,
				$"leak location {leak.Location} is not in room B" );
		} );

		Check( "cordon bisect finds the leaking half", () =>
		{
			var (input, _) = LoadFixture( "leaking-two-room" );
			var whole = Cordon.FromBrushes( input.Brushes );
			var bisect = CompileCordon.BisectLeak( input, whole, CordonAxis.X );
			Require( NearF( bisect.FirstCordon.Max.x, 0f ) && NearF( bisect.SecondCordon.Min.x, 0f ),
				$"bisect split faces {bisect.FirstCordon.Max.x} / {bisect.SecondCordon.Min.x}, expected 0" );
			Require( bisect.LeakingHalf == CordonHalf.Second,
				$"leaking half {bisect.LeakingHalf}, expected Second (the gap is in room B)" );
			Require( bisect.First.Succeeded, "first half failed: " + bisect.First.Error );
			Require( bisect.First.Leak is not null && !bisect.First.Leak.IsLeaked, "first half reported a leak" );
			Require( !bisect.Second.Succeeded, "second half compiled successfully" );
			Require( bisect.Second.Leak is not null && bisect.Second.Leak.IsLeaked, "second half has no leak result" );
			Require( bisect.Second.Issues.Any( i => i.Code == "leak" ), "second half has no 'leak' issue" );
			// Explicit flood origins work too: the input's start is in room A,
			// so pass one in room B for the second half.
			var explicitBisect = CompileCordon.BisectLeak( input, whole, CordonAxis.X,
				firstStart: new Vec3( -160, 0, 32 ), secondStart: new Vec3( 160, 0, 32 ) );
			Require( explicitBisect.LeakingHalf == CordonHalf.Second, $"explicit-start bisect leaking half {explicitBisect.LeakingHalf}" );
			Throws<ArgumentException>(
				() => CompileCordon.BisectLeak( input, whole, CordonAxis.X, secondStart: new Vec3( -160, 0, 32 ) ),
				"bisect accepted a second-half origin outside the second half" );
		} );

		// -- Runner: visible-only compiles --------------------------------------

		Check( "hiding a structural wall leaks", () =>
		{
			var (input, layers) = LoadFixture( "two-room-cordon" );
			var filtered = CompileCordon.ApplyVisibleOnly( input, new[] { "room-a" }, LayerOf( layers ) );
			var result = Run( filtered, CompilePreset.QuickPlay, "hidden-wall" );
			Require( !result.Succeeded, "map with a hidden structural wall compiled successfully" );
			Require( result.Issues.Any( i => i.Code == "leak" ),
				"hiding wall_east did not leak: " + string.Join( "; ", result.Issues.Select( i => i.Code ) ) );
		} );

		Check( "visible-only keeps the bake when only a detail brush is hidden", () =>
		{
			CompileInput SealedRoomWithTrim()
			{
				var room = new CompileInput( "sealed-room-trim" );
				room.Brushes.Add( CompileBrush.Box( "floor", 0, new Vec3( -144, -144, -16 ), new Vec3( 144, 144, 0 ) ) );
				room.Brushes.Add( CompileBrush.Box( "ceiling", 1, new Vec3( -144, -144, 128 ), new Vec3( 144, 144, 144 ) ) );
				room.Brushes.Add( CompileBrush.Box( "wall_west", 2, new Vec3( -144, -144, 0 ), new Vec3( -128, 144, 128 ) ) );
				room.Brushes.Add( CompileBrush.Box( "wall_east", 3, new Vec3( 128, -144, 0 ), new Vec3( 144, 144, 128 ) ) );
				room.Brushes.Add( CompileBrush.Box( "wall_south", 4, new Vec3( -128, -144, 0 ), new Vec3( 128, -128, 128 ) ) );
				room.Brushes.Add( CompileBrush.Box( "wall_north", 5, new Vec3( -128, 128, 0 ), new Vec3( 128, 144, 128 ) ) );
				room.Brushes.Add( CompileBrush.Box( "trim", 6, new Vec3( -16, -16, 0 ), new Vec3( 16, 16, 16 ), "concrete", isDetail: true ) );
				room.PlayerStart = new Vec3( 0, 0, 32 );
				room.Lights.Add( new CompileLight( new Vec3( 0, 0, 96 ), 1f ) );
				return room;
			}
			var layers = new Dictionary<int, string> { [6] = "hidden-props" };
			var full = Run( SealedRoomWithTrim(), CompilePreset.Final, "trim-full" );
			Require( full.Succeeded, "unfiltered sealed room failed: " + full.Error );
			Require( full.Lightmaps is not null && full.Lightmaps.Faces.Count == 42,
				$"unfiltered bake has {full.Lightmaps?.Faces.Count} faces, expected 42 (7 brushes x 6)" );
			var filtered = CompileCordon.ApplyVisibleOnly( SealedRoomWithTrim(), Array.Empty<string>(), LayerOf( layers ) );
			var result = Run( filtered, CompilePreset.Final, "trim-hidden" );
			Require( result.Succeeded, "visible-only sealed room failed: " + result.Error );
			Require( result.Leak is not null && !result.Leak.IsLeaked, "visible-only sealed room leaked (detail brushes never seal)" );
			Require( result.Lightmaps is not null && result.Lightmaps.Faces.Count == 36,
				$"filtered bake has {result.Lightmaps?.Faces.Count} faces, expected 36 (hidden detail trim dropped)" );
		} );
	}
}
