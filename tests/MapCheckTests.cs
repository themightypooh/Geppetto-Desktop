// Check-for-Problems API tests (group "mapcheck").
//
// Hammer Desktop P2: hammer-check-problems-fix-actions (worker muse-3-desktop-hammer-26).
// Fixtures: tests/fixtures/hammer/hammer-check-problems-fix-actions/ — see
// its README.md for the expected issue set of each fixture map.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

internal static class MapCheckTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mapcheck", name, test );
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
		bool Near( Vec3 a, Vec3 b ) => a.AlmostEquals( b, 1e-3f );

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-check-problems-fix-actions" );
					if ( File.Exists( Path.Combine( candidate, "map-missing-targets.json" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "mapcheck fixture dir not found (map-missing-targets.json missing above cwd / BaseDirectory)" );
		}

		InMemoryMapCheckDocument Load( string file ) =>
			MapCheckDocumentJson.LoadFromFile( Path.Combine( FixtureDir(), file ) );

		Check( "missing-target fixture has exactly 3 missing-target issues", () =>
		{
			var doc = Load( "map-missing-targets.json" );
			var report = MapCheck.RunFullCheck( doc );
			Require( report.DocumentName == "map-missing-targets", $"document name '{report.DocumentName}'" );
			Require( report.IssueCount == 3, $"expected 3 issues, got {report.IssueCount}" );
			Require( report.CountOf( MapIssueType.MissingTarget ) == 3, "not all issues are MissingTarget" );
			Require( report.HasErrors, "missing targets should be errors" );
			var targets = report.Issues.Select( i => i.Detail ).OrderBy( t => t ).ToArray();
			Require( targets.SequenceEqual( new[] { "counter_missing", "door_01", "relay_missing" } ),
				"missing targets were: " + string.Join( ",", targets ) );
			// Stable ids in detection order.
			Require( report.Issues[0].Id == "CHK-0001" && report.Issues[2].Id == "CHK-0003",
				$"issue ids {report.Issues[0].Id}..{report.Issues[2].Id}" );
			// Every issue names its fix and its source entity.
			foreach ( var issue in report.Issues )
			{
				Require( issue.FixHint == "Create target stub (or retarget)", $"fix hint '{issue.FixHint}'" );
				Require( issue.Key == "target", $"referencing key '{issue.Key}'" );
				Require( !string.IsNullOrEmpty( issue.EntityId ), "issue has no source entity" );
			}
			// No leak graph: the Leak rule is skipped, never faked.
			Require( report.SkippedRules.Count == 1 && report.SkippedRules[0] == "Leak",
				"skipped rules: " + string.Join( ",", report.SkippedRules ) );
		} );

		Check( "FixAllOfType fixes all 3 missing targets and reports 3 results", () =>
		{
			var doc = Load( "map-missing-targets.json" );
			var log = new List<string>();
			var results = MapCheck.FixAllOfType( doc, MapIssueType.MissingTarget, log );
			Require( results.Count == 3, $"expected 3 results, got {results.Count}" );
			Require( results.All( r => r.Applied ), "not every result is Fixed: " +
				string.Join( "; ", results.Select( r => r.Status + ":" + r.Detail ) ) );
			Require( results.All( r => r.Type == MapIssueType.MissingTarget ), "wrong result types" );
			Require( log.Count == 3, $"expected 3 log lines, got {log.Count}" );
			Require( log.All( l => l.StartsWith( "[mapcheck] CHK-", StringComparison.Ordinal ) ),
				"log line shape: " + string.Join( " | ", log ) );
			Require( log.All( l => l.Contains( "Fixed", StringComparison.Ordinal ) ), "log lacks Fixed" );
			// Stubs exist, flagged, at the source entities' origins.
			foreach ( var name in new[] { "door_01", "relay_missing", "counter_missing" } )
			{
				var stub = doc.FindEntityByTargetName( name );
				Require( stub is not null, $"no stub for '{name}'" );
				Require( stub.ClassName == MapCheck.StubClassName, $"stub class '{stub.ClassName}'" );
				Require( stub.Properties.TryGetValue( "mapcheck_stub", out var flag ) && flag == "1",
					"stub is not flagged mapcheck_stub=1" );
			}
			// Re-check: the missing-target issues are gone.
			var after = MapCheck.RunFullCheck( doc );
			Require( after.CountOf( MapIssueType.MissingTarget ) == 0,
				$"re-check still reports {after.CountOf( MapIssueType.MissingTarget )} missing targets" );
			Require( after.IssueCount == 0, $"re-check reports {after.IssueCount} issues" );
		} );

		Check( "go-to returns the right entity for a missing-target issue", () =>
		{
			var doc = Load( "map-missing-targets.json" );
			var report = MapCheck.RunFullCheck( doc );
			var issue = report.Issues.First( i => i.Detail == "door_01" );
			var target = MapCheck.GoTo( issue );
			Require( target.Kind == GoToKind.Entity, $"go-to kind {target.Kind}" );
			Require( target.EntityId == "e_btn", $"go-to entity '{target.EntityId}', expected e_btn" );
			Require( Near( target.Position, new Vec3( -32, 0, 32 ) ), $"go-to position {target.Position}" );
			Require( target.Label.Contains( "e_btn", StringComparison.Ordinal ), $"label '{target.Label}'" );
			// Same answer from the issue itself.
			var direct = issue.GoTo();
			Require( direct.Kind == target.Kind && direct.EntityId == target.EntityId, "issue.GoTo disagrees" );
		} );

		Check( "go-to returns the brush for a missing-material issue", () =>
		{
			var doc = Load( "map-mixed-issues.json" );
			var report = MapCheck.RunFullCheck( doc );
			var issue = report.Issues.First( i => i.Type == MapIssueType.MissingMaterial );
			Require( issue.BrushId == "b_badmat", $"brush '{issue.BrushId}'" );
			Require( issue.Detail == "missing/mat_xyz", $"detail '{issue.Detail}'" );
			var target = MapCheck.GoTo( issue );
			Require( target.Kind == GoToKind.Brush && target.BrushId == "b_badmat",
				$"go-to {target.Kind} '{target.BrushId}'" );
			Require( Near( target.Position, new Vec3( -60, 0, 132 ) ), $"go-to position {target.Position}" );
		} );

		Check( "compile-log coordinate resolves to the player-start entity", () =>
		{
			var doc = Load( "map-missing-targets.json" );
			var lines = File.ReadAllLines( Path.Combine( FixtureDir(), "compile-log.txt" ) );
			var leaked = lines.First( l => l.Contains( "leaked!", StringComparison.Ordinal ) );
			var resolved = MapCheck.GoToCoordinates( doc, leaked );
			Require( resolved is not null, "leaked line did not resolve" );
			Require( resolved.Kind == GoToKind.Entity, $"kind {resolved.Kind}" );
			Require( resolved.EntityId == "e_player", $"entity '{resolved.EntityId}', expected e_player" );
			Require( resolved.Distance == 0, $"distance {resolved.Distance}" );
			Require( Near( resolved.Position, new Vec3( 0, 0, 32 ) ), $"position {resolved.Position}" );
			// The button line resolves to the button at its exact origin.
			var buttonLine = lines.First( l => l.Contains( "(-32 0 32)", StringComparison.Ordinal ) );
			var button = MapCheck.GoToCoordinates( doc, buttonLine );
			Require( button is not null && button.Kind == GoToKind.Entity && button.EntityId == "e_btn",
				$"button line resolved to {button?.Kind} '{button?.EntityId}'" );
		} );

		Check( "coordinate inside a wall brush resolves to that brush", () =>
		{
			var doc = Load( "map-missing-targets.json" );
			var resolved = MapCheck.GoToCoordinates( doc, new Vec3( 64, 0, 32 ) );
			Require( resolved.Kind == GoToKind.Brush, $"kind {resolved.Kind}" );
			Require( resolved.BrushId == "b_wall", $"brush '{resolved.BrushId}', expected b_wall" );
			Require( resolved.Distance == 0, $"distance {resolved.Distance} (point is inside the brush)" );
		} );

		Check( "coordinate far outside resolves to the nearest brush", () =>
		{
			var doc = Load( "map-missing-targets.json" );
			var resolved = MapCheck.GoToCoordinates( doc, new Vec3( 200, 0, 32 ) );
			Require( resolved.Kind == GoToKind.Brush, $"kind {resolved.Kind}" );
			Require( resolved.BrushId == "b_wall", $"brush '{resolved.BrushId}', expected b_wall" );
			// b_wall centre (60, 0, 64): sqrt(140^2 + 32^2) = 143.6...
			Require( MathF.Abs( resolved.Distance - 143.6108f ) < 0.01f, $"distance {resolved.Distance}" );
		} );

		Check( "log line without coordinates resolves to null", () =>
		{
			var doc = Load( "map-missing-targets.json" );
			Require( MapCheck.GoToCoordinates( doc, "material \"missing/mat_xyz\" not found" ) is null,
				"material line resolved to something" );
			Require( MapCheck.GoToCoordinates( doc, "" ) is null, "empty line resolved to something" );
			Require( MapCheck.GoToCoordinates( doc, "**** leaked ****" ) is null,
				"header line resolved to something" );
		} );

		Check( "mixed fixture full check finds all five issue types", () =>
		{
			var doc = Load( "map-mixed-issues.json" );
			var report = MapCheck.RunFullCheck( doc );
			Require( report.IssueCount == 6, $"expected 6 issues, got {report.IssueCount}: " +
				string.Join( "; ", report.Issues.Select( i => i.Type + ":" + i.Detail ) ) );
			Require( report.CountOf( MapIssueType.InvalidBrush ) == 2, "invalid brush count" );
			Require( report.CountOf( MapIssueType.MissingTarget ) == 1, "missing target count" );
			Require( report.CountOf( MapIssueType.MissingMaterial ) == 1, "missing material count" );
			Require( report.CountOf( MapIssueType.BadKeyValue ) == 1, "bad keyvalue count" );
			Require( report.CountOf( MapIssueType.Leak ) == 1, "leak count" );
			Require( report.HasErrors, "mixed map should have errors" );
			Require( report.SkippedRules.Count == 0, "no rule should be skipped on the mixed map" );
			var leak = report.Issues.First( i => i.Type == MapIssueType.Leak );
			Require( Near( leak.Position, new Vec3( 64, 0, 32 ) ), $"gap location {leak.Position}" );
			Require( leak.GapLeafId == 0 && leak.GapPortalId == 0, $"gap ids {leak.GapLeafId}/{leak.GapPortalId}" );
			Require( leak.GapPointFile == "0 0 32\n64 0 32\n80 0 32\n",
				"leak point file was: " + leak.GapPointFile.Replace( "\n", "|" ) );
			var badKey = report.Issues.First( i => i.Type == MapIssueType.BadKeyValue );
			Require( badKey.EntityId == "e_badkv" && badKey.Key == "speed" && badKey.Detail == "fast",
				$"bad keyvalue {badKey.EntityId}/{badKey.Key}='{badKey.Detail}'" );
			Require( badKey.Severity == MapIssueSeverity.Warning, "bad keyvalue should be a warning" );
		} );

		Check( "invalid-brush fix repairs inverted bounds and preserves volume", () =>
		{
			var doc = Load( "map-mixed-issues.json" );
			var report = MapCheck.RunFullCheck( doc );
			var issue = report.Issues.First( i => i.Type == MapIssueType.InvalidBrush && i.BrushId == "b_inverted" );
			var before = doc.FindBrush( "b_inverted" );
			float beforeVolume = MathF.Abs( before.Volume );
			var log = new List<string>();
			var result = MapCheck.Fix( doc, issue, log );
			Require( result.Status == MapFixStatus.Fixed, $"status {result.Status}: {result.Detail}" );
			Require( result.IssueId == issue.Id, "result does not name its issue" );
			Require( log.Count == 1 && log[0].Contains( "Fixed", StringComparison.Ordinal ), "fix was not logged" );
			var after = doc.FindBrush( "b_inverted" );
			Require( after.IsValid, "brush is still invalid after repair" );
			Require( MathF.Abs( after.Volume - beforeVolume ) < 1e-3f,
				$"volume moved: |{beforeVolume}| -> {after.Volume}" );
			Require( Near( after.Min, new Vec3( 56, 56, 0 ) ) && Near( after.Max, new Vec3( 64, 64, 128 ) ),
				$"bounds after repair ({after.Min})-({after.Max})" );
			// Re-check: only the unrepairable flat brush remains invalid.
			var again = MapCheck.RunFullCheck( doc );
			Require( again.CountOf( MapIssueType.InvalidBrush ) == 1, "inverted-brush issue did not clear" );
			Require( again.Issues.First( i => i.Type == MapIssueType.InvalidBrush ).BrushId == "b_flat",
				"wrong brush remains invalid" );
		} );

		Check( "zero-volume brush fix cannot fix and leaves the brush untouched", () =>
		{
			var doc = Load( "map-mixed-issues.json" );
			var report = MapCheck.RunFullCheck( doc );
			var issue = report.Issues.First( i => i.Type == MapIssueType.InvalidBrush && i.BrushId == "b_flat" );
			var beforeMin = doc.FindBrush( "b_flat" ).Min;
			var beforeMax = doc.FindBrush( "b_flat" ).Max;
			var result = MapCheck.Fix( doc, issue );
			Require( result.Status == MapFixStatus.CannotFix, $"status {result.Status}: {result.Detail}" );
			Require( !result.Applied, "unrepairable fix claims Applied" );
			Require( result.Detail.Contains( "IssueAutoFix", StringComparison.Ordinal ),
				"result does not name the triangulation owner: " + result.Detail );
			var brush = doc.FindBrush( "b_flat" );
			Require( brush is not null, "brush was deleted by the fix" );
			Require( brush.Min.Equals( beforeMin ) && brush.Max.Equals( beforeMax ), "brush bounds moved" );
			Require( MapCheck.RunFullCheck( doc ).CountOf( MapIssueType.InvalidBrush ) == 2,
				"issue cleared without being fixed" );
		} );

		Check( "missing-material fix assigns nodraw and records the old material", () =>
		{
			var doc = Load( "map-mixed-issues.json" );
			var report = MapCheck.RunFullCheck( doc );
			var issue = report.Issues.First( i => i.Type == MapIssueType.MissingMaterial );
			var result = MapCheck.Fix( doc, issue );
			Require( result.Status == MapFixStatus.Fixed, $"status {result.Status}: {result.Detail}" );
			Require( result.Detail.Contains( "missing/mat_xyz", StringComparison.Ordinal ),
				"result does not record the old material: " + result.Detail );
			Require( result.Detail.Contains( MapCheck.NoDrawMaterial, StringComparison.Ordinal ),
				"result does not record nodraw: " + result.Detail );
			Require( doc.FindBrush( "b_badmat" ).Material == MapCheck.NoDrawMaterial,
				$"material is now '{doc.FindBrush( "b_badmat" ).Material}'" );
			Require( MapCheck.RunFullCheck( doc ).CountOf( MapIssueType.MissingMaterial ) == 0,
				"missing-material issue did not clear" );
		} );

		Check( "bad-keyvalue fix removes the key and logs the old value", () =>
		{
			var doc = Load( "map-mixed-issues.json" );
			var report = MapCheck.RunFullCheck( doc );
			var issue = report.Issues.First( i => i.Type == MapIssueType.BadKeyValue );
			var log = new List<string>();
			var result = MapCheck.Fix( doc, issue, log );
			Require( result.Status == MapFixStatus.Fixed, $"status {result.Status}: {result.Detail}" );
			Require( result.Detail.Contains( "'speed'", StringComparison.Ordinal ), "result lacks the key" );
			Require( result.Detail.Contains( "'fast'", StringComparison.Ordinal ), "result lacks the old value" );
			Require( log.Count == 1 && log[0].Contains( "fast", StringComparison.Ordinal ),
				"log lacks the old value: " + string.Join( " | ", log ) );
			var entity = doc.FindEntity( "e_badkv" );
			Require( !entity.Properties.ContainsKey( "speed" ), "key was not removed" );
			Require( entity.Properties.ContainsKey( "targetname" ), "fix removed the wrong key" );
			Require( MapCheck.RunFullCheck( doc ).CountOf( MapIssueType.BadKeyValue ) == 0,
				"bad-keyvalue issue did not clear" );
		} );

		Check( "leak fix does not auto-seal", () =>
		{
			var doc = Load( "map-mixed-issues.json" );
			var report = MapCheck.RunFullCheck( doc );
			var issue = report.Issues.First( i => i.Type == MapIssueType.Leak );
			var result = MapCheck.Fix( doc, issue );
			Require( result.Status == MapFixStatus.CannotFix, $"status {result.Status}: {result.Detail}" );
			Require( !result.Applied, "leak fix claims Applied" );
			Require( result.Detail.Contains( "64", StringComparison.Ordinal ),
				"result does not name the gap location: " + result.Detail );
			Require( result.Detail.Contains( "64 0 32", StringComparison.Ordinal ),
				"result does not carry the point file: " + result.Detail );
			// The map is still leaked afterwards: nothing was sealed.
			Require( MapCheck.RunFullCheck( doc ).CountOf( MapIssueType.Leak ) == 1,
				"leak cleared without being sealed" );
			// But the caller can go to the gap.
			var target = MapCheck.GoTo( issue );
			Require( target.Kind == GoToKind.Coordinate, $"leak go-to kind {target.Kind}" );
			Require( Near( target.Position, new Vec3( 64, 0, 32 ) ), $"leak go-to position {target.Position}" );
		} );

		Check( "clean map reports zero issues and skips no rules", () =>
		{
			var doc = Load( "map-clean.json" );
			var report = MapCheck.RunFullCheck( doc );
			Require( report.IssueCount == 0, $"clean map reported {report.IssueCount} issues" );
			Require( !report.HasErrors, "clean map reports errors" );
			Require( report.SkippedRules.Count == 0, "rules skipped on a clean map with a sealed graph" );
			Require( MapCheck.FixAllOfType( doc, MapIssueType.MissingTarget ).Count == 0,
				"FixAllOfType on a clean map returned results" );
		} );

		Check( "single Fix logs one entry; FixAllOfType with no matches returns empty", () =>
		{
			var doc = Load( "map-missing-targets.json" );
			var report = MapCheck.RunFullCheck( doc );
			var log = new List<string>();
			var result = MapCheck.Fix( doc, report.Issues[0], log );
			Require( result.Applied, $"single fix failed: {result.Detail}" );
			Require( log.Count == 1, $"expected 1 log line, got {log.Count}" );
			Require( log[0].StartsWith( "[mapcheck] " + report.Issues[0].Id, StringComparison.Ordinal ),
				"log line: " + log[0] );
			var empty = MapCheck.FixAllOfType( doc, MapIssueType.Leak, log );
			Require( empty.Count == 0, $"FixAllOfType(Leak) returned {empty.Count} results" );
			Require( log.Count == 1, "empty FixAllOfType logged lines" );
		} );

		Check( "TryParseCoordinates parses negatives, decimals and commas; rejects the rest", () =>
		{
			Require( MapCheck.TryParseCoordinates( "Entity x (-64.5 0 32) here", out var a )
				&& Near( a, new Vec3( -64.5f, 0, 32 ) ), "negative/decimal parse failed" );
			Require( MapCheck.TryParseCoordinates( "at (1,2,3) done", out var b )
				&& Near( b, new Vec3( 1, 2, 3 ) ), "comma-separated parse failed" );
			Require( MapCheck.TryParseCoordinates( "first (1 2) then (4 5 6) end", out var c )
				&& Near( c, new Vec3( 4, 5, 6 ) ), "did not skip a bad triple for a good one" );
			Require( !MapCheck.TryParseCoordinates( "no coordinates here", out _ ), "plain text parsed" );
			Require( !MapCheck.TryParseCoordinates( "(1 2)", out _ ), "two-number triple parsed" );
			Require( !MapCheck.TryParseCoordinates( "(a b c)", out _ ), "non-numeric triple parsed" );
			Require( !MapCheck.TryParseCoordinates( "", out _ ), "empty string parsed" );
			Require( !MapCheck.TryParseCoordinates( null, out _ ), "null parsed" );
		} );

		Check( "null inputs throw; unknown rule throws", () =>
		{
			var doc = Load( "map-clean.json" );
			Throws<ArgumentNullException>( () => MapCheck.RunFullCheck( null ), "null document" );
			Throws<ArgumentNullException>( () => MapCheck.RunFullCheck( doc, null ), "null source" );
			Throws<ArgumentNullException>( () => MapCheck.Fix( doc, null ), "null issue" );
			Throws<ArgumentNullException>( () => MapCheck.Fix( null, new MapIssue() ), "null document on Fix" );
			Throws<ArgumentNullException>( () => MapCheck.GoTo( null ), "null issue on GoTo" );
			Throws<ArgumentNullException>( () => MapCheck.GoToCoordinates( null, new Vec3( 0, 0, 0 ) ), "null document on GoToCoordinates" );
			Throws<ArgumentException>( () => new DefaultIssueSource().RunRule( doc, "NoSuchRule" ), "unknown rule" );
			Throws<JsonException>( () => MapCheckDocumentJson.Load( "{ not json" ), "malformed fixture JSON" );
			Throws<JsonException>( () => MapCheckDocumentJson.Load( "{\"brushes\":[{\"id\":\"b1\"}]}" ), "brush without bounds" );
		} );

		Check( "fix retargets a stale issue when a case-variant target appeared later", () =>
		{
			// Detection is case-insensitive (Source targetnames are), so
			// "door_01" -> "Door_01" raises no issue in a fresh check...
			var doc = new InMemoryMapCheckDocument { Name = "case-variant" };
			doc.AddKnownMaterial( "concrete/concretefloor001" );
			doc.AddBrush( new CheckBrush { Id = "b1", Min = new Vec3( -64, -64, 0 ), Max = new Vec3( 64, 64, 8 ), Material = "concrete/concretefloor001" } );
			var door = new CheckEntity { Id = "e_door", ClassName = "func_door", Origin = new Vec3( 0, 0, 32 ) };
			door.Properties["targetname"] = "Door_01";
			doc.AddEntity( door );
			var button = new CheckEntity { Id = "e_btn", ClassName = "func_button", Origin = new Vec3( -32, 0, 32 ) };
			button.Properties["target"] = "door_01";
			doc.AddEntity( button );
			Require( MapCheck.RunFullCheck( doc ).CountOf( MapIssueType.MissingTarget ) == 0,
				"case-insensitive target match was flagged" );
			// ...but a stale issue raised before the door existed must still
			// fix cleanly: retarget to the canonical casing, no stub.
			var stale = new MapIssue
			{
				Id = "CHK-0001", Type = MapIssueType.MissingTarget, Severity = MapIssueSeverity.Error,
				EntityId = "e_btn", Key = "target", Detail = "door_01"
			};
			var result = MapCheck.Fix( doc, stale );
			Require( result.Status == MapFixStatus.Fixed, $"status {result.Status}: {result.Detail}" );
			Require( doc.FindEntity( "e_btn" ).Properties["target"] == "Door_01",
				$"target is now '{doc.FindEntity( "e_btn" ).Properties["target"]}'" );
			Require( doc.Entities.Count == 2, "retarget created a stub it did not need" );
			Require( MapCheck.RunFullCheck( doc ).IssueCount == 0, "retarget did not clear the issue" );
		} );

		Check( "empty document resolves coordinates to Coordinate with distance -1", () =>
		{
			var doc = new InMemoryMapCheckDocument { Name = "empty" };
			var resolved = MapCheck.GoToCoordinates( doc, new Vec3( 1, 2, 3 ) );
			Require( resolved.Kind == GoToKind.Coordinate, $"kind {resolved.Kind}" );
			Require( resolved.Distance == -1, $"distance {resolved.Distance}" );
			var report = MapCheck.RunFullCheck( doc );
			Require( report.IssueCount == 0, "empty document reported issues" );
			Require( report.SkippedRules.Contains( "Leak" ), "Leak rule not skipped on an empty document" );
		} );
	}
}
