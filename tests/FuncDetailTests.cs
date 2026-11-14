using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

/// <summary>
/// func_detail optimisation (hammer-optimisation-func-detail).
///
/// The acceptance tests, against the real MapBrush + BspBuilder + MapEntities
/// APIs (both dependencies DONE): a decorated room (stairs/trim as func_detail)
/// yields the same leaf count as the bare room; a sealing wall flagged
/// func_detail produces a validation issue (SealingBrushFuncDetail); a
/// func_detail_blocker still cuts leaves like a structural brush; a
/// func_detail_illusionary is non-solid and never cuts leaves.
///
/// Fixtures are the source of truth for the maps: plain-text brush lists under
/// tests/fixtures/hammer/hammer-optimisation-func-detail/ — one
/// `brush &lt;name&gt; [detail|blocker|illusionary]` line, then one
/// `plane nx ny nz d` line per face (outward unit normal, plane Dot(n, p) == d,
/// solid side Dot(n, p) &lt;= d — the same plane convention as MapBrushFace and
/// BspPlane). The parser below is the only reader; if a fixture fails to parse,
/// the tests fail, so a stale fixture cannot silently pass. Code-built rooms
/// use the same numbers as the BSP fixture sealed-room.txt (interior void
/// -128..128 x -128..128 x 0..128).
/// </summary>
internal static class FuncDetailTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "funcdetail", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void ThrowsNamed( Action action, string mustContain )
		{
			try { action(); }
			catch ( Exception ex )
			{
				Require( ex.Message.Contains( mustContain, StringComparison.Ordinal ),
					$"error '{ex.Message}' did not name '{mustContain}'" );
				return;
			}
			throw new Exception( $"expected a rejection naming '{mustContain}', got silent success" );
		}

		// ---- Map construction (code-built; same numbers as sealed-room.txt) ----

		/// <summary>The six shell brushes of a sealed room (floor, ceiling, 4 walls),
		/// in a fixed order: 0 floor, 1 ceiling, 2 wall-x-neg, 3 wall-x-pos,
		/// 4 wall-y-neg, 5 wall-y-pos. Interior void -128..128 x -128..128 x 0..128.</summary>
		static List<MapBrush> SealedRoom() => new()
		{
			MapBrush.Box( new Vec3( -144, -144, -16 ), new Vec3( 144, 144, 0 ) ),
			MapBrush.Box( new Vec3( -144, -144, 128 ), new Vec3( 144, 144, 144 ) ),
			MapBrush.Box( new Vec3( -144, -144, 0 ), new Vec3( -128, 144, 128 ) ),
			MapBrush.Box( new Vec3( 128, -144, 0 ), new Vec3( 144, 144, 128 ) ),
			MapBrush.Box( new Vec3( -128, -144, 0 ), new Vec3( 128, -128, 128 ) ),
			MapBrush.Box( new Vec3( -128, 128, 0 ), new Vec3( 128, 144, 128 ) ),
		};

		/// <summary>Decoration inside the room: three stair treads against the
		/// x-neg wall and a trim beam under the ceiling. All inside the void, so
		/// as structural brushes they would split leaves; as func_detail they
		/// must not.</summary>
		static List<MapBrush> Decoration() => new()
		{
			MapBrush.Box( new Vec3( -128, -64, 0 ), new Vec3( -96, 0, 16 ) ),
			MapBrush.Box( new Vec3( -128, -64, 16 ), new Vec3( -96, 0, 32 ) ),
			MapBrush.Box( new Vec3( -128, -64, 32 ), new Vec3( -96, 0, 48 ) ),
			MapBrush.Box( new Vec3( -128, -128, 112 ), new Vec3( 128, 128, 120 ) ),
		};

		/// <summary>A dividing wall across the middle of a doubled room (x -256..256
		/// interior), with a doorway 64 wide (y -32..32) and 96 high (z 0..96):
		/// three boxes — left of the door, right of the door, lintel above it.
		/// The shell is SealedRoom() stretched in x; the wall is returned
		/// separately so the caller can flag it (or not) per test.</summary>
		static (List<MapBrush> Shell, List<MapBrush> Wall) TwoRoomDoorway()
		{
			var shell = new List<MapBrush>
			{
				MapBrush.Box( new Vec3( -272, -144, -16 ), new Vec3( 272, 144, 0 ) ),
				MapBrush.Box( new Vec3( -272, -144, 128 ), new Vec3( 272, 144, 144 ) ),
				MapBrush.Box( new Vec3( -272, -144, 0 ), new Vec3( -256, 144, 128 ) ),
				MapBrush.Box( new Vec3( 256, -144, 0 ), new Vec3( 272, 144, 128 ) ),
				MapBrush.Box( new Vec3( -256, -144, 0 ), new Vec3( 256, -128, 128 ) ),
				MapBrush.Box( new Vec3( -256, 128, 0 ), new Vec3( 256, 144, 128 ) ),
			};
			var wall = new List<MapBrush>
			{
				MapBrush.Box( new Vec3( -16, -128, 0 ), new Vec3( 16, -32, 128 ) ),
				MapBrush.Box( new Vec3( -16, 32, 0 ), new Vec3( 16, 128, 128 ) ),
				MapBrush.Box( new Vec3( -16, -32, 96 ), new Vec3( 16, 32, 128 ) ),
			};
			return (shell, wall);
		}

		// ---- Fixture loading ----

		static string FixtureDir()
		{
			var rel = Path.Combine( "tests", "fixtures", "hammer", "hammer-optimisation-func-detail" );
			if ( Directory.Exists( rel ) ) return rel;
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			while ( dir != null )
			{
				var candidate = Path.Combine( dir.FullName, rel );
				if ( Directory.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			throw new Exception( "func_detail fixture dir not found from " + Directory.GetCurrentDirectory() );
		}

		/// <summary>Parse one fixture file into (brushes, flags, names): the flag
		/// token on a `brush` line (detail/blocker/illusionary) marks every brush
		/// parsed from that block with that kind.</summary>
		static (List<MapBrush> Brushes, FuncDetailSet Flags, List<string> Names) LoadFixture( string name )
		{
			var path = Path.Combine( FixtureDir(), name );
			var brushes = new List<MapBrush>();
			var names = new List<string>();
			var flags = new FuncDetailSet();
			// Fully qualified: Displacement.cs defines a second, competing
			// Effigy.Map.MapBrushFace (a quad face for displacements), so the
			// bare name is ambiguous in a file with `using Effigy.Map;`. The
			// landed plane-based face this fixture format describes is
			// Effigy.MapBrushFace (MapBrush.cs).
			var faces = new List<Effigy.MapBrushFace>();
			var brushName = "";
			FuncDetailKind? kind = null;
			void Flush()
			{
				if ( faces.Count == 0 ) return;
				var brush = MapBrush.FromPlanes( faces );
				brushes.Add( brush );
				names.Add( brushName );
				if ( kind.HasValue ) flags.Mark( brush, kind.Value );
				faces = new List<Effigy.MapBrushFace>();
				kind = null;
			}
			foreach ( var raw in File.ReadLines( path ) )
			{
				var line = raw.Trim();
				if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
				var parts = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
				if ( parts[0] == "brush" )
				{
					Flush();
					if ( parts.Length < 2 ) throw new Exception( $"{name}: brush line needs a name: '{line}'" );
					brushName = parts[1];
					if ( parts.Length > 2 )
						kind = parts[2] switch
						{
							"detail" => FuncDetailKind.Detail,
							"blocker" => FuncDetailKind.Blocker,
							"illusionary" => FuncDetailKind.Illusionary,
							_ => throw new Exception( $"{name}: unknown flag '{parts[2]}' on brush '{brushName}'" ),
						};
				}
				else if ( parts[0] == "plane" )
				{
					if ( parts.Length != 5 ) throw new Exception( $"{name}: plane line needs nx ny nz d: '{line}'" );
					float Num( int i ) => float.Parse( parts[i], CultureInfo.InvariantCulture );
					faces.Add( new Effigy.MapBrushFace( new Vec3( Num( 1 ), Num( 2 ), Num( 3 ) ), Num( 4 ) ) );
				}
				else throw new Exception( $"{name}: unknown line: '{line}'" );
			}
			Flush();
			if ( brushes.Count == 0 ) throw new Exception( $"{name}: no brushes parsed" );
			return (brushes, flags, names);
		}

		string RepoRoot()
		{
			string? dir = AppContext.BaseDirectory;
			while ( dir is not null )
			{
				if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
				dir = Directory.GetParent( dir )?.FullName;
			}
			dir = Directory.GetCurrentDirectory();
			while ( dir is not null )
			{
				if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
				dir = Directory.GetParent( dir )?.FullName;
			}
			throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
		}

		/// <summary>A registry that defines the three func_detail classes (the
		/// shipped core registry does not — see FuncDetail.cs header). Proves the
		/// entity path works with no code change once a registry gains them.</summary>
		EntityRegistry FuncDetailRegistry() => EntityRegistry.LoadFromJson(
			"{\"schemaVersion\":1,\"name\":\"test-func-detail\",\"classes\":[" +
			"{\"name\":\"func_detail\",\"kind\":\"brush\",\"keyvalues\":[]}," +
			"{\"name\":\"func_detail_blocker\",\"kind\":\"brush\",\"keyvalues\":[]}," +
			"{\"name\":\"func_detail_illusionary\",\"kind\":\"brush\",\"keyvalues\":[]}," +
			"{\"name\":\"func_door\",\"kind\":\"brush\",\"keyvalues\":[]}]}", "test-func-detail" );

		// ---- Acceptance: decoration does not cut leaves ----

		Check( "decorated room (stairs/trim as func_detail) has the same leaf count as the bare room", () =>
		{
			var room = SealedRoom();
			var bare = new FuncDetailSet().Build( room );
			Require( bare.VisLeafCount == 1, $"bare room visleaves {bare.VisLeafCount}\n{bare.Describe()}" );
			Require( bare.IsSealed, "bare room must be sealed" );

			var all = new List<MapBrush>( room );
			all.AddRange( Decoration() );
			var flags = new FuncDetailSet();
			foreach ( var brush in all.Skip( room.Count ) ) flags.Mark( brush, FuncDetailKind.Detail );
			var decorated = flags.Build( all );
			Require( decorated.VisLeafCount == bare.VisLeafCount,
				$"decorated room visleaves {decorated.VisLeafCount}, bare room {bare.VisLeafCount}\n{decorated.Describe()}" );
			Require( decorated.IsSealed, "decorated room must stay sealed" );
			Require( decorated.LeafCount == bare.LeafCount,
				$"decorated room leaves {decorated.LeafCount}, bare room {bare.LeafCount}" );

			// The same decoration as structural brushes DOES cut leaves, so the
			// equality above is the flag doing work, not the geometry being inert.
			var structural = new FuncDetailSet().Build( all );
			Require( structural.VisLeafCount > bare.VisLeafCount,
				$"structural decoration must add visleaves: got {structural.VisLeafCount}, bare {bare.VisLeafCount}" );
		} );

		Check( "fixture decorated room parses and matches the bare room", () =>
		{
			var (brushes, flags, names) = LoadFixture( "decorated-room.txt" );
			Require( brushes.Count == 10, $"decorated-room.txt must have 10 brushes, got {brushes.Count}" );
			Require( flags.Count == 4, $"decorated-room.txt must flag 4 detail brushes, got {flags.Count}" );
			Require( names.Count == 10 && names[6] == "step-1" && names[9] == "trim-beam",
				$"fixture brush names wrong: {string.Join( ",", names )}" );
			foreach ( var brush in brushes )
				Require( brush.IsValid, $"fixture brush invalid: {string.Join( "; ", brush.Validate() )}" );
			var tree = flags.Build( brushes );
			var bare = new FuncDetailSet().Build( SealedRoom() );
			Require( tree.VisLeafCount == bare.VisLeafCount,
				$"fixture decorated room visleaves {tree.VisLeafCount}, bare room {bare.VisLeafCount}" );
			Require( tree.IsSealed, "fixture decorated room must stay sealed" );
			Require( flags.Validate( brushes ).Count == 0, "correctly decorated room must validate clean" );
		} );

		// ---- Acceptance: sealing misuse is flagged, not silent ----

		Check( "sealing wall flagged func_detail produces a validation issue", () =>
		{
			var room = SealedRoom();
			var flags = new FuncDetailSet();
			flags.Mark( room[3], FuncDetailKind.Detail ); // wall-x-pos seals the room
			var issues = flags.Validate( room );
			Require( issues.Count == 1, $"want exactly 1 issue, got {issues.Count}: {string.Join( "; ", issues )}" );
			var issue = issues[0];
			Require( issue.Severity == FuncDetailIssueSeverity.Warning, $"issue must be a Warning, got {issue.Severity}" );
			Require( issue.Code == FuncDetailIssue.CodeSealingBrushFuncDetail, $"code {issue.Code}" );
			Require( issue.BrushIndex == 3, $"issue must name brush #3 (wall-x-pos), got #{issue.BrushIndex}" );

			// The consequence is visible, not silent: the BSP excluded the wall,
			// so the room no longer seals.
			var tree = flags.Build( room );
			Require( !tree.IsSealed, "room with its sealing wall flagged detail must report unsealed" );
			Require( tree.VisLeafCount == 0, $"unsealed room must have no visleaves, got {tree.VisLeafCount}" );
		} );

		Check( "fixture sealing wall is flagged by validation", () =>
		{
			var (brushes, flags, names) = LoadFixture( "sealing-wall.txt" );
			Require( brushes.Count == 6 && flags.Count == 1, $"sealing-wall.txt must have 6 brushes, 1 flagged; got {brushes.Count}/{flags.Count}" );
			Require( names[3] == "wall-x-pos", $"flagged brush must be wall-x-pos, got '{names[3]}'" );
			var issues = flags.Validate( brushes );
			Require( issues.Count == 1, $"want exactly 1 issue, got {issues.Count}: {string.Join( "; ", issues )}" );
			Require( issues[0].Code == FuncDetailIssue.CodeSealingBrushFuncDetail, $"code {issues[0].Code}" );
			Require( issues[0].BrushIndex == 3, $"issue must name brush #3, got #{issues[0].BrushIndex}" );
		} );

		Check( "a correctly decorated room validates clean and an unflagged sealing wall is not flagged", () =>
		{
			var all = new List<MapBrush>( SealedRoom() );
			all.AddRange( Decoration() );
			var flags = new FuncDetailSet();
			foreach ( var brush in all.Skip( 6 ) ) flags.Mark( brush, FuncDetailKind.Detail );
			Require( flags.Validate( all ).Count == 0,
				$"decorated room must validate clean: {string.Join( "; ", flags.Validate( all ) )}" );

			// The same room with nothing flagged validates clean too.
			Require( new FuncDetailSet().Validate( SealedRoom() ).Count == 0, "bare room must validate clean" );
		} );

		// ---- Variants: blocker cuts, illusionary does not, and is non-solid ----

		Check( "func_detail_blocker still cuts leaves like a structural brush", () =>
		{
			var (shell, wall) = TwoRoomDoorway();
			var bare = new FuncDetailSet().Build( shell );
			Require( bare.VisLeafCount == 1, $"room without the dividing wall must be 1 visleaf, got {bare.VisLeafCount}" );

			var structural = new List<MapBrush>( shell );
			structural.AddRange( wall );
			var structuralTree = new FuncDetailSet().Build( structural );
			Require( structuralTree.VisLeafCount >= 2,
				$"structural dividing wall must split the room, got {structuralTree.VisLeafCount} visleaves" );
			Require( structuralTree.IsSealed, "two-room map must stay sealed" );

			var blockerFlags = new FuncDetailSet();
			blockerFlags.MarkAll( wall, FuncDetailKind.Blocker );
			var blockerTree = blockerFlags.Build( structural );
			Require( blockerTree.VisLeafCount == structuralTree.VisLeafCount,
				$"blocker wall visleaves {blockerTree.VisLeafCount}, structural wall {structuralTree.VisLeafCount}" );
			Require( blockerTree.Describe() == structuralTree.Describe(),
				"blocker wall must build the identical tree to the structural wall" );

			// The same wall as plain Detail merges the rooms back into one leaf.
			var detailFlags = new FuncDetailSet();
			detailFlags.MarkAll( wall, FuncDetailKind.Detail );
			var detailTree = detailFlags.Build( structural );
			Require( detailTree.VisLeafCount == 1, $"detail dividing wall must not split the room, got {detailTree.VisLeafCount}" );

			// A blocker is structural for visibility, so it is never a sealing misuse.
			Require( blockerFlags.Validate( structural ).Count == 0, "blocker wall must validate clean" );
		} );

		Check( "func_detail_illusionary is non-solid, never cuts leaves, and a sealing illusionary wall is flagged", () =>
		{
			Require( !FuncDetail.IsSolid( FuncDetailKind.Illusionary ), "illusionary must be non-solid" );
			Require( FuncDetail.IsSolid( FuncDetailKind.Detail ), "detail must be solid" );
			Require( FuncDetail.IsSolid( FuncDetailKind.Blocker ), "blocker must be solid" );
			Require( !FuncDetail.CutsLeaves( FuncDetailKind.Detail ), "detail must not cut leaves" );
			Require( FuncDetail.CutsLeaves( FuncDetailKind.Blocker ), "blocker must cut leaves" );
			Require( !FuncDetail.CutsLeaves( FuncDetailKind.Illusionary ), "illusionary must not cut leaves" );
			Require( FuncDetail.Renders( FuncDetailKind.Detail )
				&& FuncDetail.Renders( FuncDetailKind.Blocker )
				&& FuncDetail.Renders( FuncDetailKind.Illusionary ), "all three kinds render" );

			var all = new List<MapBrush>( SealedRoom() );
			all.AddRange( Decoration() );
			var flags = new FuncDetailSet();
			foreach ( var brush in all.Skip( 6 ) ) flags.Mark( brush, FuncDetailKind.Illusionary );
			var tree = flags.Build( all );
			var bare = new FuncDetailSet().Build( SealedRoom() );
			Require( tree.VisLeafCount == bare.VisLeafCount, "illusionary decoration must not change the leaf count" );
			Require( !flags.IsSolid( all[6] ), "an illusionary brush must report non-solid" );
			Require( flags.Validate( all ).Count == 0, "illusionary decoration must validate clean" );

			// ... but an illusionary SEALING wall is the same misuse as a detail one.
			var room = SealedRoom();
			var sealing = new FuncDetailSet();
			sealing.Mark( room[3], FuncDetailKind.Illusionary );
			var issues = sealing.Validate( room );
			Require( issues.Count == 1 && issues[0].Code == FuncDetailIssue.CodeSealingBrushFuncDetail,
				$"sealing illusionary wall must warn, got: {string.Join( "; ", issues )}" );
		} );

		// ---- Flag store: per-brush, re-markable, unmarkable ----

		Check( "marking is per-brush: re-marking changes the kind and unmarking restores structural", () =>
		{
			var room = SealedRoom();
			var flags = new FuncDetailSet();
			Require( flags.KindOf( room[0] ) is null, "unmarked brush must have no kind" );
			Require( !flags.IsFlagged( room[0] ), "unmarked brush must not be flagged" );
			Require( flags.IsSolid( room[0] ) && flags.CutsLeaves( room[0] ), "unmarked brush is structural" );

			flags.Mark( room[0], FuncDetailKind.Detail );
			Require( flags.KindOf( room[0] ) == FuncDetailKind.Detail, "mark must store Detail" );
			Require( flags.Count == 1, "one brush flagged" );
			Require( flags.ExcludesFromLeaves( room[0] ), "detail must exclude from leaves" );
			Require( flags.KindOf( room[1] ) is null, "marking one brush must not mark its neighbour" );

			flags.Mark( room[0], FuncDetailKind.Blocker );
			Require( flags.KindOf( room[0] ) == FuncDetailKind.Blocker, "re-mark must replace the kind" );
			Require( !flags.ExcludesFromLeaves( room[0] ), "blocker must not exclude from leaves" );
			Require( flags.Count == 1, "re-marking must not duplicate the flag" );

			Require( flags.Unmark( room[0] ), "unmark must report the flag removed" );
			Require( flags.KindOf( room[0] ) is null, "unmarked brush must be structural again" );
			Require( !flags.Unmark( room[0] ), "unmarking an unflagged brush must report nothing removed" );
			Require( flags.Count == 0, "set must be empty after unmarking" );

			flags.Mark( room[0], FuncDetailKind.Detail );
			flags.Clear();
			Require( flags.Count == 0 && flags.KindOf( room[0] ) is null, "Clear must drop every flag" );

			ThrowsNamed( () => flags.Mark( null, FuncDetailKind.Detail ), "brush" );
			ThrowsNamed( () => flags.KindOf( null ), "brush" );
		} );

		Check( "merging combines entity-derived and hand-marked flags, other set wins on overlap", () =>
		{
			var room = SealedRoom();
			var a = new FuncDetailSet();
			a.Mark( room[0], FuncDetailKind.Detail );
			var b = new FuncDetailSet();
			b.Mark( room[0], FuncDetailKind.Blocker );
			b.Mark( room[1], FuncDetailKind.Illusionary );
			a.Merge( b );
			Require( a.KindOf( room[0] ) == FuncDetailKind.Blocker, "merge: other set must win on overlap" );
			Require( a.KindOf( room[1] ) == FuncDetailKind.Illusionary, "merge: other set's new flag must land" );
			Require( a.Count == 2, "merged set must hold both flags" );
			ThrowsNamed( () => a.Merge( null ), "other" );
		} );

		// ---- Adapter: MapBrush -> BspBrush preserves planes ----

		Check( "to-bsp-brushes adapter preserves planes, names brushes, and sets IsDetail per kind", () =>
		{
			var room = SealedRoom();
			var flags = new FuncDetailSet();
			flags.Mark( room[0], FuncDetailKind.Detail );
			flags.Mark( room[1], FuncDetailKind.Blocker );
			flags.Mark( room[2], FuncDetailKind.Illusionary );
			var converted = flags.ToBspBrushes( room );
			Require( converted.Count == room.Count, "one BspBrush per MapBrush" );
			for ( int i = 0; i < room.Count; i++ )
			{
				Require( converted[i].Planes.Count == room[i].Faces.Count, $"brush {i} plane count" );
				for ( int f = 0; f < room[i].Faces.Count; f++ )
				{
					Require( converted[i].Planes[f].Normal.Equals( room[i].Faces[f].Normal ),
						$"brush {i} face {f} normal must survive the adapter" );
					Require( converted[i].Planes[f].Distance == room[i].Faces[f].Distance,
						$"brush {i} face {f} distance must survive the adapter" );
				}
				Require( converted[i].Name == $"brush {i}", $"converted brush must be named 'brush {i}', got '{converted[i].Name}'" );
				Require( MathF.Abs( converted[i].Volume() - room[i].Volume ) < 1f,
					$"brush {i} volume {converted[i].Volume()}, want {room[i].Volume}" );
			}
			Require( converted[0].IsDetail, "Detail must convert to IsDetail" );
			Require( !converted[1].IsDetail, "Blocker must convert to structural (IsDetail clear)" );
			Require( converted[2].IsDetail, "Illusionary must convert to IsDetail" );
			Require( !converted[3].IsDetail, "unflagged brush must convert to structural" );

			ThrowsNamed( () => flags.ToBspBrushes( null ), "brushes" );
			ThrowsNamed( () => flags.ToBspBrushes( new MapBrush[] { room[0], null } ), "index 1" );
		} );

		// ---- Errors: all-detail, invalid brush, empty map ----

		Check( "an all-detail map is an error, not a silent empty build", () =>
		{
			var room = SealedRoom();
			var flags = new FuncDetailSet();
			flags.MarkAll( room, FuncDetailKind.Detail );
			var issues = flags.Validate( room );
			Require( issues.Count == 1, $"want exactly 1 issue, got {issues.Count}: {string.Join( "; ", issues )}" );
			Require( issues[0].Severity == FuncDetailIssueSeverity.Error, "all-detail must be an Error" );
			Require( issues[0].Code == FuncDetailIssue.CodeNoStructuralBrushes, $"code {issues[0].Code}" );
			ThrowsNamed( () => flags.Build( room ), "non-detail" );

			var empty = new FuncDetailSet().Validate( Array.Empty<MapBrush>() );
			Require( empty.Count == 1 && empty[0].Code == FuncDetailIssue.CodeNoStructuralBrushes,
				"an empty brush list must report NoStructuralBrushes" );
			ThrowsNamed( () => new FuncDetailSet().Build( Array.Empty<MapBrush>() ), "at least one brush" );
		} );

		Check( "an invalid brush is reported with its defect code, not silently dropped", () =>
		{
			var flat = MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 0, 128, 64 ) );
			Require( !flat.IsValid, "flat box must be invalid" );
			var brushes = new List<MapBrush>( SealedRoom() ) { flat };
			var issues = new FuncDetailSet().Validate( brushes );
			Require( issues.Count == 1, $"want exactly 1 issue, got {issues.Count}: {string.Join( "; ", issues )}" );
			Require( issues[0].Code == FuncDetailIssue.CodeInvalidBrush, $"code {issues[0].Code}" );
			Require( issues[0].BrushIndex == 6, $"invalid brush must be #6, got #{issues[0].BrushIndex}" );
			Require( issues[0].Message.Contains( "ZeroVolume", StringComparison.Ordinal ),
				$"message must name the defect: {issues[0].Message}" );
			ThrowsNamed( () => new FuncDetailSet().Build( brushes ), "zero volume" );
		} );

		// ---- Detail still renders and collides (downstream of the BSP) ----

		Check( "a detail brush still renders and collides: its mesh and solidity survive the flag", () =>
		{
			// A real material, not the default nodraw: ToMesh drops nodraw faces
			// (FaceMaterial.ContributesToBakedMesh), so a default-material box
			// bakes to 0 faces — that is the material doing work, not the flag.
			var step = MapBrush.Box( new Vec3( -128, -64, 0 ), new Vec3( -96, 0, 16 ), "brick/brickwall001" );
			var flags = new FuncDetailSet();
			flags.Mark( step, FuncDetailKind.Detail );
			Require( flags.IsSolid( step ), "detail brush must stay solid (collides)" );
			Require( FuncDetail.Renders( FuncDetailKind.Detail ), "detail brush must still render" );
			var mesh = step.ToMesh();
			Require( mesh.FaceCount == 6, $"detail brush mesh must keep all 6 faces, got {mesh.FaceCount}" );
			Require( mesh.Positions.Count == 8, $"detail brush mesh must keep all 8 verts, got {mesh.Positions.Count}" );
		} );

		// ---- Class names + entity bridge ----

		Check( "func_detail class names are recognised case-insensitively, other classes are not", () =>
		{
			Require( FuncDetail.IsFuncDetailClass( "func_detail" ), "func_detail must be recognised" );
			Require( FuncDetail.IsFuncDetailClass( "FUNC_DETAIL_BLOCKER" ), "blocker must be recognised case-insensitively" );
			Require( FuncDetail.IsFuncDetailClass( " func_detail_illusionary " ), "illusionary must be recognised with whitespace" );
			Require( !FuncDetail.IsFuncDetailClass( "func_door" ), "func_door must not be func_detail" );
			Require( !FuncDetail.IsFuncDetailClass( "func_detail_extra" ), "a longer name must not match" );
			Require( !FuncDetail.IsFuncDetailClass( null ), "null must not be func_detail" );
			Require( !FuncDetail.IsFuncDetailClass( "" ), "empty must not be func_detail" );
			Require( FuncDetail.TryKindOfClass( "func_detail_blocker", out var kind ) && kind == FuncDetailKind.Blocker,
				"blocker class must map to Blocker" );
			Require( FuncDetail.TryKindOfClass( "func_detail_illusionary", out kind ) && kind == FuncDetailKind.Illusionary,
				"illusionary class must map to Illusionary" );
			Require( !FuncDetail.TryKindOfClass( "trigger_multiple", out _ ), "trigger must not map" );
		} );

		Check( "from-entities collects func_detail-tied brushes and ignores other classes", () =>
		{
			var registry = FuncDetailRegistry();
			var entities = new MapEntities( registry );
			var room = SealedRoom();
			var detail = entities.Tie( "func_detail", new[] { room[0], room[1] } );
			var blocker = entities.Tie( "func_detail_blocker", new[] { room[2] } );
			var door = entities.Tie( "func_door", new[] { room[3] } );
			Require( detail.ClassName == "func_detail" && blocker.ClassName == "func_detail_blocker",
				"entities must keep their func_detail classes" );
			Require( door.ClassName == "func_door", "door must keep its class" );

			var flags = FuncDetail.FromEntities( entities );
			Require( flags.Count == 3, $"FromEntities must collect 3 flagged brushes, got {flags.Count}" );
			Require( flags.KindOf( room[0] ) == FuncDetailKind.Detail, "func_detail tie must map to Detail" );
			Require( flags.KindOf( room[1] ) == FuncDetailKind.Detail, "second func_detail tie must map to Detail" );
			Require( flags.KindOf( room[2] ) == FuncDetailKind.Blocker, "blocker tie must map to Blocker" );
			Require( flags.KindOf( room[3] ) is null, "func_door tie must stay structural" );

			// The collected flags drive the same BSP exclusion as hand-marking.
			var all = new List<MapBrush>( room );
			var tree = flags.Build( all );
			Require( !tree.IsSealed, "flagging two sealing walls via entities must unseal the room" );

			ThrowsNamed( () => FuncDetail.FromEntities( null ), "entities" );
		} );

		Check( "with the shipped core registry (no func_detail classes) from-entities is empty, not an error", () =>
		{
			var core = EntityRegistry.Load( Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" ) );
			Require( core.Find( "func_detail" ) is null, "core registry must not define func_detail (that is the documented gap)" );
			var entities = new MapEntities( core );
			var room = SealedRoom();
			entities.Tie( "func_door", new[] { room[0] } );
			var flags = FuncDetail.FromEntities( entities );
			Require( flags.Count == 0, "no func_detail classes means no flags collected" );
		} );
	}
}
