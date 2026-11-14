using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

/// <summary>Hammer Desktop P1: live Issue engine (hammer-validation-issue-engine).
/// Fixture maps are built in code below (a sealed 256x256x128 room, one
/// wall with a 1-unit gap, a room missing a whole wall, ...); the
/// tests/fixtures/hammer/hammer-validation-issue-engine/*.expected.json
/// files are the oracles the tests compare Run() against. Run with:
/// dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter issueengine
/// (from the repo root; the Register line in tests/TestRegistry.cs is
/// added by the final integrator — see the ticket handoff).</summary>
internal static class IssueEngineTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "issueengine", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}

		// ---- fixture builders: axis-aligned boxes, outward-wound ----
		// Face order: -X, +X, -Y, +Y, -Z, +Z. Each face lists its four
		// corners so the fan triangulation winds outward (the
		// cross-product check is in the comment-free math below; the
		// engine's own Lint reuse proves it at runtime).
		IssueBrush Box( int id, float x0, float y0, float z0, float x1, float y1, float z1, string material = "concrete/wall", bool isDetail = false )
		{
			IssueFace Face( Vec3 normal, params Vec3[] verts ) =>
				new IssueFace( normal, Vec3.Dot( normal, verts[0] ), material, verts );
			var faces = new[]
			{
				Face( new Vec3( -1, 0, 0 ), new Vec3( x0, y0, z0 ), new Vec3( x0, y0, z1 ), new Vec3( x0, y1, z1 ), new Vec3( x0, y1, z0 ) ),
				Face( new Vec3( 1, 0, 0 ), new Vec3( x1, y1, z0 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y0, z0 ) ),
				Face( new Vec3( 0, -1, 0 ), new Vec3( x1, y0, z0 ), new Vec3( x1, y0, z1 ), new Vec3( x0, y0, z1 ), new Vec3( x0, y0, z0 ) ),
				Face( new Vec3( 0, 1, 0 ), new Vec3( x0, y1, z0 ), new Vec3( x0, y1, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y1, z0 ) ),
				Face( new Vec3( 0, 0, -1 ), new Vec3( x0, y1, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x1, y0, z0 ), new Vec3( x0, y0, z0 ) ),
				Face( new Vec3( 0, 0, 1 ), new Vec3( x0, y0, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x0, y1, z1 ) ),
			};
			return new IssueBrush( id, faces, isDetail );
		}

		IssueEntity PlayerStart() => new IssueEntity( 100, "info_player_start", new Vec3( 0, 0, 32 ) );
		IssueEntity Light() => new IssueEntity( 101, "light", new Vec3( 0, 0, 100 ) );

		// Sealed room, interior 256x256, floor z=0, ceiling z=128,
		// 16-unit walls. Brush ids: 1 floor, 2 ceiling, 3 -X, 4 +X,
		// 5 -Y, 6 +Y.
		IssueDocument SealedRoom()
		{
			var doc = new IssueDocument { Name = "clean-room" };
			doc.Brushes.Add( Box( 1, -128, -128, -16, 128, 128, 0 ) );
			doc.Brushes.Add( Box( 2, -128, -128, 128, 128, 128, 144 ) );
			doc.Brushes.Add( Box( 3, -144, -144, 0, -128, 144, 128 ) );
			doc.Brushes.Add( Box( 4, 128, -144, 0, 144, 144, 128 ) );
			doc.Brushes.Add( Box( 5, -128, -144, 0, 128, -128, 128 ) );
			doc.Brushes.Add( Box( 6, -128, 128, 0, 128, 144, 128 ) );
			doc.Entities.Add( PlayerStart() );
			doc.Entities.Add( Light() );
			return doc;
		}

		// Same room, but the +X wall is two brushes leaving a 1-unit
		// gap at y in [-0.5, 0.5] — the leak ticket's 1-unit gap.
		IssueDocument LeakRoom()
		{
			var doc = SealedRoom();
			doc.Name = "leak-room";
			doc.Brushes.RemoveAll( b => b.Id == 4 );
			doc.Brushes.Add( Box( 4, 128, 0.5f, 0, 144, 144, 128 ) );
			doc.Brushes.Add( Box( 7, 128, -144, 0, 144, -0.5f, 128 ) );
			return doc;
		}

		// Same room with the whole +X wall missing.
		IssueDocument UnsealedRoom()
		{
			var doc = SealedRoom();
			doc.Name = "unsealed-room";
			doc.Brushes.RemoveAll( b => b.Id == 4 );
			return doc;
		}

		IssueDocument BadTargetRoom()
		{
			var doc = SealedRoom();
			doc.Name = "bad-target-room";
			var trigger = new IssueEntity( 102, "trigger_multiple", new Vec3( 0, 0, 64 ) );
			trigger.SetKeyValue( "targetname", "trig1" );
			trigger.SetKeyValue( "target", "missing_door" );
			doc.Entities.Add( trigger );
			return doc;
		}

		IssueDocument DuplicateTargetRoom()
		{
			var doc = SealedRoom();
			var a = new IssueEntity( 102, "func_door", new Vec3( 120, 0, 64 ) );
			a.SetKeyValue( "targetname", "door1" );
			var b = new IssueEntity( 103, "func_door", new Vec3( -120, 0, 64 ) );
			b.SetKeyValue( "targetname", "door1" );
			doc.Entities.Add( a );
			doc.Entities.Add( b );
			return doc;
		}

		IssueDocument MissingMaterialRoom()
		{
			var doc = SealedRoom();
			doc.Brushes[0].Faces[5].Material = "";
			return doc;
		}

		IssueDocument NoClassRoom()
		{
			var doc = SealedRoom();
			doc.Entities.Add( new IssueEntity( 102, "", new Vec3( 0, 0, 64 ) ) );
			return doc;
		}

		IssueDocument OverLimitRoom()
		{
			var doc = SealedRoom();
			doc.Name = "over-limit-room";
			doc.Limits = new IssueLimits { MaxBrushes = 4 };
			return doc;
		}

		IReadOnlyList<Issue> Run( IssueDocument doc ) => new IssueEngine().Run( doc );
		int Count( IReadOnlyList<Issue> issues, IssueKind kind ) => issues.Count( i => i.Kind == kind );

		// ---- JSON oracles under tests/fixtures/hammer/... ----
		string FixtureDir()
		{
			const string relative = "tests/fixtures/hammer/hammer-validation-issue-engine";
			var candidates = new List<string> { Path.GetFullPath( relative ) };
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			while ( dir != null )
			{
				candidates.Add( Path.Combine( dir.FullName, relative ) );
				candidates.Add( Path.Combine( dir.FullName, "hammer-validation-issue-engine" ) );
				dir = dir.Parent;
			}
			dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
			while ( dir != null )
			{
				candidates.Add( Path.Combine( dir.FullName, relative ) );
				dir = dir.Parent;
			}
			foreach ( var candidate in candidates )
				if ( Directory.Exists( candidate ) )
					return candidate;
			throw new Exception( $"fixture directory not found (tried {candidates[0]} and walk-ups from the test binary)" );
		}

		List<(string kind, string severity, int brushId, int entityId)> Expected( string mapName )
		{
			var path = Path.Combine( FixtureDir(), mapName + ".expected.json" );
			if ( !File.Exists( path ) )
				throw new Exception( $"missing oracle fixture: {path}" );
			var expected = new List<(string, string, int, int)>();
			using var json = JsonDocument.Parse( File.ReadAllText( path ) );
			foreach ( var item in json.RootElement.GetProperty( "expected" ).EnumerateArray() )
				expected.Add( (
					item.GetProperty( "kind" ).GetString() ?? "",
					item.GetProperty( "severity" ).GetString() ?? "",
					item.GetProperty( "brushId" ).GetInt32(),
					item.GetProperty( "entityId" ).GetInt32() ) );
			return expected;
		}

		void ExpectOracle( string mapName, IReadOnlyList<Issue> actual )
		{
			var want = Expected( mapName )
				.OrderBy( e => e.kind ).ThenBy( e => e.brushId ).ThenBy( e => e.entityId ).ToList();
			var got = actual
				.Select( i => (i.Kind.ToString(), i.Severity.ToString(), i.BrushId, i.EntityId) )
				.OrderBy( e => e.Item1 ).ThenBy( e => e.Item3 ).ThenBy( e => e.Item4 ).ToList();
			Require( want.Count == got.Count,
				$"{mapName}: expected {want.Count} issues, got {got.Count}: {string.Join( " | ", got.Select( g => $"{g.Item1} b{g.Item3} e{g.Item4}" ) )}" );
			for ( var i = 0; i < want.Count; i++ )
				Require( want[i] == got[i],
					$"{mapName}: issue {i} expected {want[i]}, got {got[i]}" );
		}

		Check( "a clean sealed room produces zero issues", () =>
		{
			var issues = Run( SealedRoom() );
			Require( issues.Count == 0, $"expected 0 issues, got {issues.Count}: {string.Join( " | ", issues.Select( i => i.ToString() ) )}" );
			ExpectOracle( "clean-room", issues );
		} );

		Check( "a room with a 1-unit gap in one wall reports exactly one leak", () =>
		{
			var issues = Run( LeakRoom() );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}: {string.Join( " | ", issues.Select( i => i.ToString() ) )}" );
			Require( Count( issues, IssueKind.Leak ) == 1, "the one issue must be a leak" );
			var leak = issues[0];
			Require( leak.Severity == IssueSeverity.Error, "a leak is an error" );
			Require( leak.HasLocation, "a leak must carry its gap location" );
			Require( leak.Location.x > 128, $"gap location should be at/through the +X wall, got {leak.Location}" );
			Require( !string.IsNullOrWhiteSpace( leak.FixHint ), "a leak must carry a fix hint" );
			ExpectOracle( "leak-room", issues );
		} );

		Check( "a room missing a whole wall reports one unsealed map and no leak", () =>
		{
			var issues = Run( UnsealedRoom() );
			Require( Count( issues, IssueKind.UnsealedMap ) == 1, $"expected 1 unsealed issue, got {issues.Count}: {string.Join( " | ", issues.Select( i => i.ToString() ) )}" );
			Require( Count( issues, IssueKind.Leak ) == 0, "a missing wall is unsealed, not a leak" );
			Require( issues.Count == 1, $"expected exactly 1 issue, got {issues.Count}" );
			Require( issues[0].HasLocation, "the unsealed issue must carry the open gap location" );
		} );

		Check( "an entity targeting a missing targetname reports one missing-target issue", () =>
		{
			var issues = Run( BadTargetRoom() );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}: {string.Join( " | ", issues.Select( i => i.ToString() ) )}" );
			Require( Count( issues, IssueKind.MissingTarget ) == 1, "the one issue must be missing-target" );
			Require( issues[0].EntityId == 102, $"issue must name entity 102, got {issues[0].EntityId}" );
			Require( issues[0].Message.Contains( "missing_door", StringComparison.Ordinal ), "the message must name the missing target" );
			Require( issues[0].FixHint.Contains( "missing_door", StringComparison.Ordinal ), "the fix hint must name the stub to create" );
			ExpectOracle( "bad-target-room", issues );
		} );

		Check( "two entities sharing a targetname report one duplicate-target warning", () =>
		{
			var issues = Run( DuplicateTargetRoom() );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}" );
			Require( Count( issues, IssueKind.DuplicateTargetName ) == 1, "the one issue must be duplicate-targetname" );
			Require( issues[0].Severity == IssueSeverity.Warning, "a duplicate targetname is a warning, not an error" );
			Require( issues[0].Message.Contains( "door1", StringComparison.Ordinal ), "the message must name the duplicated targetname" );
			Require( issues[0].Message.Contains( "102", StringComparison.Ordinal ) && issues[0].Message.Contains( "103", StringComparison.Ordinal ),
				"the message must name both holders" );
		} );

		Check( "a face with no material reports one missing-material issue on its brush", () =>
		{
			var issues = Run( MissingMaterialRoom() );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}: {string.Join( " | ", issues.Select( i => i.ToString() ) )}" );
			Require( Count( issues, IssueKind.MissingMaterial ) == 1, "the one issue must be missing-material" );
			Require( issues[0].BrushId == 1, $"issue must name brush 1 (floor), got {issues[0].BrushId}" );
			Require( issues[0].FixHint.Contains( "nodraw", StringComparison.Ordinal ), "the fix hint must name nodraw" );
		} );

		Check( "an entity with no classname reports one entity-no-class issue", () =>
		{
			var issues = Run( NoClassRoom() );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}" );
			Require( Count( issues, IssueKind.EntityNoClass ) == 1, "the one issue must be entity-no-class" );
			Require( issues[0].EntityId == 102, $"issue must name entity 102, got {issues[0].EntityId}" );
		} );

		Check( "an over-limit map reports one limit issue per exceeded limit", () =>
		{
			var issues = Run( OverLimitRoom() );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}" );
			Require( Count( issues, IssueKind.LimitExceeded ) == 1, "the one issue must be limit-exceeded" );
			Require( issues[0].Message.Contains( "6", StringComparison.Ordinal ) && issues[0].Message.Contains( "4", StringComparison.Ordinal ),
				$"the message must carry count and limit, got: {issues[0].Message}" );
			ExpectOracle( "over-limit-room", issues );

			var two = OverLimitRoom();
			two.Limits = new IssueLimits { MaxBrushes = 4, MaxEntities = 1 };
			var twoIssues = Run( two );
			Require( Count( twoIssues, IssueKind.LimitExceeded ) == 2, $"expected 2 limit issues, got {twoIssues.Count}" );
		} );

		Check( "a brush with one vertex twisted out of plane reports one invalid brush naming planarity", () =>
		{
			var doc = SealedRoom();
			// Ceiling brush (id 2), top face, one corner raised 8 units.
			// The stored plane is untouched, so the face is non-planar
			// while the ray seal is unchanged — no leak must appear.
			doc.Brushes[1].Faces[5].Vertices[2] += new Vec3( 0, 0, 8 );
			var issues = Run( doc );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}: {string.Join( " | ", issues.Select( i => i.ToString() ) )}" );
			Require( Count( issues, IssueKind.InvalidBrush ) == 1, "the one issue must be invalid-brush" );
			Require( issues[0].BrushId == 2, $"issue must name brush 2, got {issues[0].BrushId}" );
			Require( issues[0].Message.Contains( "not planar", StringComparison.Ordinal ), $"message must name planarity, got: {issues[0].Message}" );
		} );

		Check( "a flat zero-volume brush reports one invalid brush naming volume", () =>
		{
			var doc = SealedRoom();
			doc.Brushes.Add( Box( 50, 200, 0, 0, 264, 64, 0 ) );
			var issues = Run( doc );
			Require( Count( issues, IssueKind.InvalidBrush ) == 1, $"expected 1 invalid brush, got {issues.Count}: {string.Join( " | ", issues.Select( i => i.ToString() ) )}" );
			Require( issues.Count == 1, $"expected exactly 1 issue, got {issues.Count}" );
			Require( issues[0].Message.Contains( "volume", StringComparison.Ordinal ), $"message must name volume, got: {issues[0].Message}" );
		} );

		Check( "an open brush with a missing face reports one invalid brush naming the watertight check", () =>
		{
			var doc = SealedRoom();
			var open = Box( 51, 200, 0, 0, 264, 64, 64 );
			open.Faces.RemoveAt( 5 );
			doc.Brushes.Add( open );
			var issues = Run( doc );
			Require( Count( issues, IssueKind.InvalidBrush ) == 1, $"expected 1 invalid brush, got {issues.Count}: {string.Join( " | ", issues.Select( i => i.ToString() ) )}" );
			Require( issues.Count == 1, $"expected exactly 1 issue, got {issues.Count}" );
			Require( issues[0].Message.Contains( "boundary", StringComparison.Ordinal ), $"message must name the boundary-edge (watertight) failure, got: {issues[0].Message}" );
		} );

		Check( "incremental re-check refreshes only the changed brush and entity and drops fixed issues", () =>
		{
			var engine = new IssueEngine();
			var doc = SealedRoom();
			var first = engine.Run( doc );
			Require( first.Count == 0, "the sealed room must start clean" );

			var trigger = new IssueEntity( 102, "trigger_multiple", new Vec3( 0, 0, 64 ) );
			trigger.SetKeyValue( "targetname", "trig1" );
			trigger.SetKeyValue( "target", "missing_door" );
			doc.Entities.Add( trigger );
			var second = engine.ReCheck( doc, Array.Empty<int>(), new[] { 102 }, first );
			Require( second.Count == 1 && second[0].Kind == IssueKind.MissingTarget,
				$"re-check must find the new missing target, got {second.Count}" );
			Require( engine.LastStats.WasIncremental, "ReCheck must report itself incremental" );
			Require( engine.LastStats.BrushesChecked == 0, $"no brush changed, so no brush may be re-checked, got {engine.LastStats.BrushesChecked}" );

			var door = new IssueEntity( 103, "func_door", new Vec3( 120, 0, 64 ) );
			door.SetKeyValue( "targetname", "missing_door" );
			doc.Entities.Add( door );
			var third = engine.ReCheck( doc, Array.Empty<int>(), new[] { 103 }, second );
			Require( third.Count == 0, $"adding the target must clear the issue, got {third.Count}" );

			doc.Brushes[1].Faces[5].Vertices[2] += new Vec3( 0, 0, 8 );
			var fourth = engine.ReCheck( doc, new[] { 2 }, Array.Empty<int>(), third );
			Require( fourth.Count == 1 && fourth[0].Kind == IssueKind.InvalidBrush && fourth[0].BrushId == 2,
				$"re-check must find the twisted brush, got {fourth.Count}" );
			Require( engine.LastStats.BrushesChecked == 1, $"only the one changed brush may be re-checked, got {engine.LastStats.BrushesChecked}" );

			doc.Brushes[1].Faces[5].Vertices[2] -= new Vec3( 0, 0, 8 );
			var fifth = engine.ReCheck( doc, new[] { 2 }, Array.Empty<int>(), fourth );
			Require( fifth.Count == 0, $"untwisting must drop the stale invalid-brush issue, got {fifth.Count}" );
		} );

		Check( "the registry accepts a custom rule, rejects a duplicate id, and unregisters", () =>
		{
			var engine = new IssueEngine();
			// Ten defaults: the nine already registered (incl. GiantSkybox, added
			// 2026-11-13 without updating this count) plus ZeroScalePropRule
			// (hammer-prop-lighting-negative-scale).
			Require( engine.Rules.Count == 10, $"expected 10 default rules, got {engine.Rules.Count}" );
			var ping = new PingRule();
			engine.Register( ping );
			Require( engine.Rules.Count == 11, "registering must add the rule" );
			Throws<ArgumentException>( () => engine.Register( new PingRule() ) );
			Require( engine.Run( SealedRoom() ).Any( i => i.Message == "custom-rule fired" ), "the custom rule must fire" );
			Require( engine.Unregister( "ping" ), "unregister must report success" );
			Require( !engine.Unregister( "ping" ), "unregistering twice must report failure" );
			Require( engine.Run( SealedRoom() ).All( i => i.Message != "custom-rule fired" ), "the custom rule must stop firing" );
			Require( engine.Rules.Count == 10, "the defaults must be intact after unregister" );
		} );

		Check( "Run rejects a null document and an empty document reports zero issues", () =>
		{
			var engine = new IssueEngine();
			Throws<ArgumentNullException>( () => engine.Run( null! ) );
			Require( engine.Run( new IssueDocument() ).Count == 0,
				"an empty document has no brushes, no entities and no player start, so no rule can fire" );
		} );

		Check( "a kitchen-sink map composes every rule and every issue carries code, message and fix hint", () =>
		{
			var doc = LeakRoom();
			doc.Limits = new IssueLimits { MaxBrushes = 4 };
			var trigger = new IssueEntity( 102, "trigger_multiple", new Vec3( 0, 0, 64 ) );
			trigger.SetKeyValue( "targetname", "trig1" );
			trigger.SetKeyValue( "target", "missing_door" );
			doc.Entities.Add( trigger );
			var doorA = new IssueEntity( 103, "func_door", new Vec3( 120, 0, 64 ) );
			doorA.SetKeyValue( "targetname", "door1" );
			var doorB = new IssueEntity( 104, "func_door", new Vec3( -120, 0, 64 ) );
			doorB.SetKeyValue( "targetname", "door1" );
			doc.Entities.Add( doorA );
			doc.Entities.Add( doorB );
			doc.Entities.Add( new IssueEntity( 105, "", new Vec3( 0, 0, 64 ) ) );
			doc.Brushes[0].Faces[5].Material = "";
			var issues = Run( doc );
			Require( issues.Count == 6, $"expected 6 issues, got {issues.Count}: {string.Join( " | ", issues.Select( i => i.ToString() ) )}" );
			Require( Count( issues, IssueKind.Leak ) == 1, "kitchen sink must have 1 leak" );
			Require( Count( issues, IssueKind.UnsealedMap ) == 0, "a gapped leak is not unsealed" );
			Require( Count( issues, IssueKind.MissingTarget ) == 1, "kitchen sink must have 1 missing target" );
			Require( Count( issues, IssueKind.MissingMaterial ) == 1, "kitchen sink must have 1 missing material" );
			Require( Count( issues, IssueKind.DuplicateTargetName ) == 1, "kitchen sink must have 1 duplicate targetname" );
			Require( Count( issues, IssueKind.EntityNoClass ) == 1, "kitchen sink must have 1 entity with no class" );
			Require( Count( issues, IssueKind.LimitExceeded ) == 1, "kitchen sink must have 1 limit issue" );
			foreach ( var issue in issues )
			{
				Require( !string.IsNullOrWhiteSpace( issue.Code ), $"issue {issue.Kind} has no code" );
				Require( !string.IsNullOrWhiteSpace( issue.Message ), $"issue {issue.Kind} has no message" );
				Require( !string.IsNullOrWhiteSpace( issue.FixHint ), $"issue {issue.Kind} has no fix hint" );
			}
		} );
	}

	sealed class PingRule : IIssueRule
	{
		public string Id => "ping";
		public string Name => "Ping";
		public IssueScope Scope => IssueScope.Global;
		public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.LimitExceeded };
		public IReadOnlyList<Issue> Check( IssueDocument document ) => new[]
		{
			new Issue
			{
				Kind = IssueKind.LimitExceeded,
				Severity = IssueSeverity.Info,
				Code = "PING",
				Message = "custom-rule fired",
				FixHint = "none",
			},
		};
	}
}
