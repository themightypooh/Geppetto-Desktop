#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// Issue auto-fix actions (see kernel/Effigy/Map/IssueAutoFix.cs, board
/// hammer-issue-autofix-actions), driven by the LANDED Issue engine
/// (kernel/Effigy/Map/IssueEngine.cs: Issue / IssueDocument / IssueEngine).
///
/// The acceptance numbers live in the fixture
/// tests/fixtures/hammer/hammer-issue-autofix-actions/cases.json and one check
/// below recomputes them from that file; the rest build the same cases
/// synthetically so a missing fixture can never hide a geometry failure.
/// Volumes are asserted on SignedVolume (positive = outward winding) and every
/// produced piece must be a closed convex solid. Two checks run the real
/// engine (Run) before and after a fix to prove the issue clears on re-check.
///
/// ID MAPPING. The engine numbers brushes/entities with ints; the undo target
/// uses string ids. These checks use the default mapping (brush #12 <-> target
/// id "12").
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// IssueAutoFixTests.Register(registry, settings); line.
/// </summary>
internal static class IssueAutoFixTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "issueautofix", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void RequireVolume( float got, float expected, string message )
		{
			Require( got > 0, $"{message}: volume {got:0.0000} is not positive, so the winding is inward" );
			Require( Math.Abs( got - expected ) < 1e-3f * Math.Max( 1f, expected ),
				$"{message}: volume {got:0.0000} vs expected {expected:0.0000}" );
		}

		static (InMemoryMapUndoTarget Target, MapUndo Undo) NewStack()
		{
			var target = new InMemoryMapUndoTarget( "autofix-test", "" );
			return ( target, new MapUndo( target ) );
		}

		static void AddBoxBrush( IMapUndoTarget target, string id, Vec3 min, Vec3 max, string material ) =>
			target.AddBrush( new MapBrushState { Id = id, Min = min, Max = max, Material = material } );

		static string StateKey( IMapUndoTarget target )
		{
			var parts = new List<string>();
			foreach ( var b in target.Brushes.OrderBy( b => b.Id ) )
				parts.Add( "B:" + b.Id + ":" + b.Min + ":" + b.Max + ":" + b.Material );
			foreach ( var e in target.Entities.OrderBy( e => e.Id ) )
			{
				var props = string.Join( ",", e.Properties.OrderBy( kv => kv.Key ).Select( kv => kv.Key + "=" + kv.Value ) );
				parts.Add( "E:" + e.Id + ":" + e.ClassName + ":" + e.Origin + ":" + props );
			}
			foreach ( var io in target.IoConnections.OrderBy( i => i.Id ) )
				parts.Add( "I:" + io.Id + ":" + io.SourceId + ":" + io.TargetId + ":" + io.Output + ":" + io.Input );
			foreach ( var d in target.Displacements.OrderBy( d => d.BrushId + "#" + d.FaceIndex ) )
				parts.Add( "D:" + d.BrushId + "#" + d.FaceIndex + ":p" + d.Power + ":h" + string.Join( ",", d.Heights.Select( h => h.ToString( "0.###" ) ) ) );
			return string.Join( "|", parts );
		}

		static PolyMesh MakeBox( Vec3 min, Vec3 max )
		{
			var m = new PolyMesh();
			m.AddVertex( new Vec3( min.x, min.y, min.z ) ); // 0
			m.AddVertex( new Vec3( max.x, min.y, min.z ) ); // 1
			m.AddVertex( new Vec3( max.x, max.y, min.z ) ); // 2
			m.AddVertex( new Vec3( min.x, max.y, min.z ) ); // 3
			m.AddVertex( new Vec3( min.x, min.y, max.z ) ); // 4
			m.AddVertex( new Vec3( max.x, min.y, max.z ) ); // 5
			m.AddVertex( new Vec3( max.x, max.y, max.z ) ); // 6
			m.AddVertex( new Vec3( min.x, max.y, max.z ) ); // 7
			m.AddFace( new[] { 0, 3, 2, 1 } ); // -Z
			m.AddFace( new[] { 4, 5, 6, 7 } ); // +Z
			m.AddFace( new[] { 0, 1, 5, 4 } ); // -Y
			m.AddFace( new[] { 1, 2, 6, 5 } ); // +X
			m.AddFace( new[] { 2, 3, 7, 6 } ); // +Y
			m.AddFace( new[] { 3, 0, 4, 7 } ); // -X
			return m;
		}

		static PolyMesh MakeLPrism()
		{
			// Footprint CCW from +Z: (0,0) (3,0) (3,2) (1,2) (1,3) (0,3); z 0..1. Area 7, volume 7.
			var foot = new[] { new Vec2( 0, 0 ), new Vec2( 3, 0 ), new Vec2( 3, 2 ), new Vec2( 1, 2 ), new Vec2( 1, 3 ), new Vec2( 0, 3 ) };
			var m = new PolyMesh();
			for ( int i = 0; i < 6; i++ ) m.AddVertex( new Vec3( foot[i].x, foot[i].y, 0f ) );
			for ( int i = 0; i < 6; i++ ) m.AddVertex( new Vec3( foot[i].x, foot[i].y, 1f ) );
			m.AddFace( new[] { 0, 5, 4, 3, 2, 1 } ); // bottom -Z
			m.AddFace( new[] { 6, 7, 8, 9, 10, 11 } ); // top +Z
			for ( int i = 0; i < 6; i++ )
			{
				int a = i, b = ( i + 1 ) % 6;
				m.AddFace( new[] { a, b, b + 6, a + 6 } ); // sides, outward
			}
			return m;
		}

		static IssueBrush IssueBrushFromMesh( int id, PolyMesh mesh, string[] materials )
		{
			var faces = new List<IssueFace>();
			for ( int i = 0; i < mesh.Faces.Count; i++ )
			{
				var face = mesh.Faces[i];
				var normal = mesh.FaceNormal( face );
				var vertices = face.Indices.Select( vi => mesh.Positions[vi] ).ToList();
				faces.Add( new IssueFace( normal, Vec3.Dot( normal, vertices[0] ), materials[i], vertices ) );
			}
			return new IssueBrush( id, faces );
		}

		static IssueBrush IssueBrushFromBox( int id, Vec3 min, Vec3 max, string material )
		{
			var mesh = MakeBox( min, max );
			return IssueBrushFromMesh( id, mesh, Enumerable.Repeat( material, mesh.Faces.Count ).ToArray() );
		}

		static IssueEntity DocEntity( int id, string className, Vec3 origin, params (string Key, string Value)[] keyValues )
		{
			var entity = new IssueEntity( id, className, origin );
			foreach ( var kv in keyValues ) entity.KeyValues[kv.Key] = kv.Value;
			return entity;
		}

		// Fix-generated ids are strings like "0-fix-1" / "autofix-door_1", not ints:
		// map them to stable synthetic ints so the engine can re-check a fixed document.
		static int ProjectId( string id, Dictionary<string, int> map, ref int next )
		{
			if ( int.TryParse( id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed ) ) return parsed;
			if ( !map.TryGetValue( id, out int synthetic ) )
			{
				synthetic = next;
				next++;
				map[id] = synthetic;
			}
			return synthetic;
		}

		static IssueDocument ProjectDocument( IMapUndoTarget target )
		{
			var doc = new IssueDocument { Name = "autofix-test" };
			var idMap = new Dictionary<string, int>();
			int next = 1000000;
			foreach ( var brush in target.Brushes )
				doc.Brushes.Add( IssueBrushFromBox( ProjectId( brush.Id, idMap, ref next ), brush.Min, brush.Max, brush.Material ) );
			foreach ( var entity in target.Entities )
			{
				var projected = new IssueEntity( ProjectId( entity.Id, idMap, ref next ), entity.ClassName, entity.Origin );
				foreach ( var kv in entity.Properties ) projected.KeyValues[kv.Key] = kv.Value;
				doc.Entities.Add( projected );
			}
			return doc;
		}

		static string? FindFixture( string name )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-issue-autofix-actions", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		Check( "l-prism splits into two convex boxes with the same volume", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 3, 3, 1 ), "brick/brickwall" );
			var mesh = MakeLPrism();
			RequireVolume( MathF.Abs( mesh.SignedVolume() ), 7f, "L-prism fixture mesh" );
			Require( MeshValidator.Validate( mesh ).IsClosed, "L-prism fixture mesh must be closed" );
			Require( !MapCsg.IsConvex( mesh ), "L-prism fixture mesh must be concave, or the test proves nothing" );
			var doc = new IssueDocument { Name = "l-prism" };
			doc.Brushes.Add( IssueBrushFromMesh( 0, mesh, Enumerable.Repeat( "brick/brickwall", mesh.Faces.Count ).ToArray() ) );
			var engine = new IssueEngine();
			var found = engine.Run( doc );
			Require( found.Any( i => i.Kind == IssueKind.InvalidBrush && i.BrushId == 0 ),
				"the engine must flag the L-prism invalid, or the fix proves nothing" );

			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( doc, new Issue
			{
				Kind = IssueKind.InvalidBrush,
				Code = "INVALID_BRUSH",
				Severity = IssueSeverity.Error,
				BrushId = 0
			} );

			Require( result.Applied, "fix must apply: " + result.Reason );
			Require( result.Pieces.Count == 2, $"expected 2 convex pieces, got {result.Pieces.Count}" );
			RequireVolume( result.BeforeVolume, 7f, "before volume" );
			RequireVolume( result.AfterVolume, 7f, "after volume" );
			foreach ( var piece in result.Pieces )
			{
				Require( MeshValidator.Validate( piece ).IsClosed, "every piece must be a closed solid" );
				Require( MapCsg.IsConvex( piece ), "every piece must be convex" );
			}
			var volumes = result.Pieces.Select( p => MathF.Abs( p.SignedVolume() ) ).OrderBy( v => v ).ToArray();
			RequireVolume( volumes[0], 1f, "small piece" );
			RequireVolume( volumes[1], 6f, "large piece" );
			Require( target.FindBrush( "0" ) == null, "original brush must be removed" );
			Require( target.Brushes.Count == 2, $"expected 2 piece brushes, got {target.Brushes.Count}" );
			var ids = target.Brushes.Select( b => b.Id ).OrderBy( id => id ).ToArray();
			Require( ids[0] == "0-fix-1" && ids[1] == "0-fix-2", $"piece ids are {string.Join( ",", ids )}" );
			foreach ( var b in target.Brushes )
			{
				Require( b.Material == "brick/brickwall", $"piece {b.Id} must keep the original material, got '{b.Material}'" );
				float v = MathF.Abs( ( b.Max.x - b.Min.x ) * ( b.Max.y - b.Min.y ) * ( b.Max.z - b.Min.z ) );
				if ( Math.Abs( v - 6f ) < 1e-3f )
				{
					Require( b.Min.Equals( new Vec3( 0, 0, 0 ) ) && b.Max.Equals( new Vec3( 3, 2, 1 ) ),
						$"6-volume piece bounds are {b.Min} / {b.Max}, expected (0, 0, 0) / (3, 2, 1)" );
				}
				else if ( Math.Abs( v - 1f ) < 1e-3f )
				{
					Require( b.Min.Equals( new Vec3( 0, 2, 0 ) ) && b.Max.Equals( new Vec3( 1, 3, 1 ) ),
						$"1-volume piece bounds are {b.Min} / {b.Max}, expected (0, 2, 0) / (1, 3, 1)" );
				}
				else throw new Exception( $"piece {b.Id} has unexpected volume {v}" );
			}
			Require( result.Summary.Contains( "2 convex pieces" ), "summary must state the piece count: " + result.Summary );
			Require( result.UndoDescription.Length > 0, "an applied fix must record its undo description" );
			// The issue clears on re-check: the projected pieces are valid boxes.
			var after = engine.Run( ProjectDocument( target ) );
			Require( !after.Any( i => i.Kind == IssueKind.InvalidBrush ),
				"re-check must find no invalid brush after the fix, got: " + string.Join( "; ", after.Select( i => i.ToString() ) ) );
		} );

		Check( "l-prism fix is one undo step and undoes and redoes cleanly", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 3, 3, 1 ), "brick/brickwall" );
			var doc = new IssueDocument { Name = "l-prism" };
			doc.Brushes.Add( IssueBrushFromMesh( 0, MakeLPrism(), Enumerable.Repeat( "brick/brickwall", 8 ).ToArray() ) );
			string before = StateKey( target );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( doc, new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			Require( result.Applied, "fix must apply: " + result.Reason );
			Require( undo.UndoCount == 1, $"fix must be exactly one undo step, got {undo.UndoCount}" );
			undo.Undo();
			Require( StateKey( target ) == before, "undo must restore the original brush exactly" );
			Require( target.Brushes.Count == 1 && target.FindBrush( "0" ) != null, "undo must bring back brush 0" );
			Require( undo.RedoCount == 1, "undo must leave one redo step" );
			undo.Redo();
			Require( target.Brushes.Count == 2, "redo must re-apply the split" );
			Require( Math.Abs( target.Brushes.Sum( b => Math.Abs( ( b.Max.x - b.Min.x ) * ( b.Max.y - b.Min.y ) * ( b.Max.z - b.Min.z ) ) ) - 7f ) < 1e-3f,
				"redo must restore the split volumes" );
		} );

		Check( "l-prism fix preserves displacements across undo", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 3, 3, 1 ), "brick/brickwall" );
			var heights = Enumerable.Repeat( 0.5f, 25 ).ToArray();
			target.AddDisplacement( new MapDisplacementState { BrushId = "0", FaceIndex = 0, Power = 2, Heights = heights } );
			var doc = new IssueDocument { Name = "l-prism" };
			doc.Brushes.Add( IssueBrushFromMesh( 0, MakeLPrism(), Enumerable.Repeat( "brick/brickwall", 8 ).ToArray() ) );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( doc, new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			Require( result.Applied, "fix must apply: " + result.Reason );
			Require( target.Displacements.Count == 0, "the split brush's displacements must go with it" );
			undo.Undo();
			Require( target.Displacements.Count == 1, "undo must restore the displacement" );
			var restored = target.FindDisplacement( "0", 0 );
			Require( restored != null && restored.Power == 2 && restored.Heights.Length == 25 && restored.Heights.All( h => h == 0.5f ),
				"undo must restore the displacement heights exactly" );
		} );

		Check( "inverted bounds normalise with volume preserved", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 64 ), "brick/brickwall" );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( new IssueDocument(), new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			Require( result.Applied, "fix must apply: " + result.Reason );
			var brush = target.FindBrush( "0" );
			Require( brush != null && brush.Min.Equals( new Vec3( 0, 0, 0 ) ) && brush.Max.Equals( new Vec3( 64, 64, 64 ) ),
				$"bounds must normalise to (0, 0, 0) / (64, 64, 64), got {brush?.Min} / {brush?.Max}" );
			RequireVolume( result.BeforeVolume, 262144f, "before volume" );
			RequireVolume( result.AfterVolume, 262144f, "after volume" );
			Require( undo.UndoCount == 1, "bounds fix must be one undo step" );
			undo.Undo();
			var undone = target.FindBrush( "0" );
			Require( undone != null && undone.Min.Equals( new Vec3( 64, 0, 0 ) ) && undone.Max.Equals( new Vec3( 0, 64, 64 ) ),
				"undo must restore the inverted bounds" );
		} );

		Check( "zero-volume brush refuses and changes nothing", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 0, 64, 64 ), "brick/brickwall" );
			string before = StateKey( target );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( new IssueDocument(), new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			Require( !result.Applied, "a zero-volume brush must not be fixed" );
			Require( result.Reason.Contains( "zero volume" ), "reason must say zero volume: " + result.Reason );
			Require( StateKey( target ) == before, "a refused fix must change nothing" );
			Require( undo.UndoCount == 0, "a refused fix must push no undo step" );
			Require( target.FindBrush( "0" ) != null, "a refused fix must never delete the brush" );
		} );

		Check( "convex brush mesh is already valid", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), "brick/brickwall" );
			var doc = new IssueDocument { Name = "box" };
			doc.Brushes.Add( IssueBrushFromBox( 0, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), "brick/brickwall" ) );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( doc, new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			Require( !result.Applied, "a convex brush must not be rewritten" );
			Require( result.Reason.Contains( "already a valid convex" ), "reason must say already valid: " + result.Reason );
			Require( undo.UndoCount == 0, "a no-op fix must push no undo step" );
			Require( target.Brushes.Count == 1, "the brush must be untouched" );
		} );

		Check( "open mesh refuses and changes nothing", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), "brick/brickwall" );
			var open = MakeBox( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ) );
			open.Faces.RemoveAt( 0 );
			Require( !MeshValidator.Validate( open ).IsClosed, "the open fixture must actually be open" );
			var doc = new IssueDocument { Name = "open" };
			doc.Brushes.Add( IssueBrushFromMesh( 0, open, Enumerable.Repeat( "brick/brickwall", open.Faces.Count ).ToArray() ) );
			string before = StateKey( target );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( doc, new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			Require( !result.Applied, "an open mesh must not be triangulated" );
			Require( result.Reason.Contains( "not a closed solid" ), "reason must say not closed: " + result.Reason );
			Require( StateKey( target ) == before, "a refused fix must change nothing" );
			Require( undo.UndoCount == 0, "a refused fix must push no undo step" );
		} );

		Check( "non-planar face refuses and names the face", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), "brick/brickwall" );
			var bent = MakeBox( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ) );
			bent.Positions[6] = new Vec3( 64, 64, 70 ); // top face corner lifted off its plane
			var doc = new IssueDocument { Name = "bent" };
			doc.Brushes.Add( IssueBrushFromMesh( 0, bent, Enumerable.Repeat( "brick/brickwall", bent.Faces.Count ).ToArray() ) );
			string before = StateKey( target );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( doc, new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			Require( !result.Applied, "a non-planar face must not be papered over by a bounds change" );
			Require( result.Reason.Contains( "non-planar" ), "reason must name the non-planar face: " + result.Reason );
			Require( StateKey( target ) == before, "a refused fix must change nothing" );
			Require( undo.UndoCount == 0, "a refused fix must push no undo step" );
		} );

		Check( "missing target creates flagged stub and clears on re-check", () =>
		{
			var (target, undo) = NewStack();
			target.AddEntity( new MapEntityState
			{
				Id = "0",
				ClassName = "func_button",
				Origin = new Vec3( 128, 0, 32 ),
				Properties = new Dictionary<string, string> { { "target", "door_1" } }
			} );
			var doc = new IssueDocument { Name = "missing-target" };
			doc.Entities.Add( DocEntity( 0, "func_button", new Vec3( 128, 0, 32 ), ( "target", "door_1" ) ) );
			var engine = new IssueEngine();
			var found = engine.Run( doc );
			Require( found.Any( i => i.Kind == IssueKind.MissingTarget && i.EntityId == 0 ),
				"the engine must flag the missing target, or the fix proves nothing" );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( doc, new Issue
			{
				Kind = IssueKind.MissingTarget,
				Code = "MISSING_TARGET",
				Severity = IssueSeverity.Error,
				EntityId = 0
			} );
			Require( result.Applied, "stub fix must apply: " + result.Reason );
			Require( result.ChangedObjects.Count == 1, "exactly one object must change" );
			string stubId = result.ChangedObjects[0];
			var stub = target.FindEntity( stubId );
			Require( stub != null, "the stub entity must exist" );
			Require( stub!.ClassName == "info_target", $"stub class must be info_target, got '{stub.ClassName}'" );
			Require( stub.Origin.Equals( new Vec3( 128, 0, 32 ) ), $"stub must sit at the referring entity's origin, got {stub.Origin}" );
			Require( stub.Properties.TryGetValue( "targetname", out string? tn ) && tn == "door_1", "stub must carry targetname door_1" );
			Require( stub.Properties.TryGetValue( "mapcheck_stub", out string? flag ) && flag == "1", "stub must carry the mapcheck_stub=1 flag" );
			Require( result.Flags.Contains( "mapcheck_stub:" + stubId ), "result must flag the stub: " + string.Join( ",", result.Flags ) );
			Require( undo.UndoCount == 1, "stub fix must be one undo step" );
			// Re-check: the projected document resolves door_1, so the issue clears.
			var after = engine.Run( ProjectDocument( target ) );
			Require( !after.Any( i => i.Kind == IssueKind.MissingTarget ),
				"re-check must find no missing target after the fix, got: " + string.Join( "; ", after.Select( i => i.ToString() ) ) );
			undo.Undo();
			Require( target.FindEntity( stubId ) == null, "undo must remove the stub" );
			var undone = engine.Run( ProjectDocument( target ) );
			Require( undone.Any( i => i.Kind == IssueKind.MissingTarget && i.EntityId == 0 ),
				"undo must make the target missing again on re-check" );
		} );

		Check( "missing target retargets to an existing match", () =>
		{
			var (target, undo) = NewStack();
			target.AddEntity( new MapEntityState
			{
				Id = "0",
				ClassName = "func_button",
				Origin = new Vec3( 128, 0, 32 ),
				Properties = new Dictionary<string, string> { { "target", "door_1" } }
			} );
			target.AddEntity( new MapEntityState
			{
				Id = "1",
				ClassName = "func_door",
				Origin = new Vec3( 0, 0, 0 ),
				Properties = new Dictionary<string, string> { { "targetname", "door1" } }
			} );
			var doc = new IssueDocument { Name = "retarget" };
			doc.Entities.Add( DocEntity( 0, "func_button", new Vec3( 128, 0, 32 ), ( "target", "door_1" ) ) );
			doc.Entities.Add( DocEntity( 1, "func_door", new Vec3( 0, 0, 0 ), ( "targetname", "door1" ) ) );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( doc, new Issue { Kind = IssueKind.MissingTarget, Code = "MISSING_TARGET", EntityId = 0 },
				new AutoFixOptions { TargetFix = MissingTargetFix.Retarget, CandidateTargetNames = new[] { "door1" } } );
			Require( result.Applied, "retarget fix must apply: " + result.Reason );
			Require( target.Entities.Count == 2, "retarget must create no stub entity" );
			var referrer = target.FindEntity( "0" );
			Require( referrer!.Properties.TryGetValue( "target", out string? t ) && t == "door1",
				$"referrer must now target door1, got '{t}'" );
			undo.Undo();
			Require( target.FindEntity( "0" )!.Properties["target"] == "door_1", "undo must restore door_1" );
		} );

		Check( "missing target already present is a no-op; special target refuses", () =>
		{
			var (target, undo) = NewStack();
			target.AddEntity( new MapEntityState
			{
				Id = "1",
				ClassName = "func_door",
				Origin = new Vec3( 0, 0, 0 ),
				Properties = new Dictionary<string, string> { { "targetname", "door_1" } }
			} );
			var doc = new IssueDocument { Name = "present" };
			doc.Entities.Add( DocEntity( 0, "func_button", new Vec3( 128, 0, 32 ), ( "target", "door_1" ) ) );
			doc.Entities.Add( DocEntity( 1, "func_door", new Vec3( 0, 0, 0 ), ( "targetname", "door_1" ) ) );
			var fixer = new IssueAutoFix( target, undo );
			var present = fixer.Fix( doc, new Issue { Kind = IssueKind.MissingTarget, Code = "MISSING_TARGET", EntityId = 0 } );
			Require( !present.Applied, "an already-resolving target must not create a stub" );
			Require( present.Reason.Contains( "stale" ), "reason must say the issue is stale: " + present.Reason );
			var specialDoc = new IssueDocument { Name = "special" };
			specialDoc.Entities.Add( DocEntity( 0, "func_button", new Vec3( 128, 0, 32 ), ( "target", "!player" ) ) );
			var special = fixer.Fix( specialDoc, new Issue { Kind = IssueKind.MissingTarget, Code = "MISSING_TARGET", EntityId = 0 } );
			Require( !special.Applied, "an engine special target must never be stubbed" );
			Require( special.Reason.Contains( "special target" ), "reason must say special target: " + special.Reason );
			Require( target.Entities.Count == 1, "neither no-op must add an entity" );
			Require( undo.UndoCount == 0, "neither no-op must push an undo step" );
		} );

		Check( "missing material assigns nodraw and flags", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), "" );
			var materials = new[] { "", "brick/brickwall", "brick/brickwall", "brick/brickwall", "brick/brickwall", "brick/brickwall" };
			var doc = new IssueDocument { Name = "nomat" };
			doc.Brushes.Add( IssueBrushFromMesh( 0, MakeBox( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ) ), materials ) );
			var engine = new IssueEngine();
			Require( engine.Run( doc ).Any( i => i.Kind == IssueKind.MissingMaterial && i.BrushId == 0 ),
				"the engine must flag the missing material, or the fix proves nothing" );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( doc, new Issue { Kind = IssueKind.MissingMaterial, Code = "MISSING_MATERIAL", BrushId = 0 } );
			Require( result.Applied, "nodraw fix must apply: " + result.Reason );
			Require( result.Summary.Contains( "face(s) 0" ), "summary must name the bad face: " + result.Summary );
			Require( target.FindBrush( "0" )!.Material == "tools/toolsnodraw",
				$"material must become tools/toolsnodraw, got '{target.FindBrush( "0" )!.Material}'" );
			Require( result.Flags.Contains( "material-nodraw:0" ), "result must flag the nodraw: " + string.Join( ",", result.Flags ) );
			Require( undo.UndoCount == 1, "nodraw fix must be one undo step" );
			undo.Undo();
			Require( target.FindBrush( "0" )!.Material == "", "undo must restore the empty material" );
		} );

		Check( "unsealed reports the gap and changes nothing", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 256, 256, 128 ), "brick/brickwall" );
			string before = StateKey( target );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( new IssueDocument(), new Issue
			{
				Kind = IssueKind.UnsealedMap,
				Code = "UNSEALED_MAP",
				Severity = IssueSeverity.Error,
				Location = new Vec3( 64, 0, 32 ),
				HasLocation = true,
				Message = "ray escaped the map"
			} );
			Require( !result.Applied, "an unsealed map must never be auto-sealed" );
			Require( result.Summary.Contains( "(64, 0, 32)" ), "summary must report the gap location: " + result.Summary );
			Require( result.Reason.Contains( "auto-seal" ), "reason must say auto-seal is refused: " + result.Reason );
			Require( StateKey( target ) == before, "a report-only fix must change nothing" );
			Require( undo.UndoCount == 0, "a report-only fix must push no undo step" );
		} );

		Check( "over-limit lists biggest contributors and changes nothing", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), "brick/brickwall" );
			string before = StateKey( target );
			var fixer = new IssueAutoFix( target, undo );
			var result = fixer.Fix( new IssueDocument(), new Issue
			{
				Kind = IssueKind.LimitExceeded,
				Code = "LIMIT_EXCEEDED",
				Severity = IssueSeverity.Error,
				Message = "face count 70000 exceeds the limit 65536 (biggest: brush #3 (1204), brush #5 (980), brush #7 (911))"
			} );
			Require( !result.Applied, "an over-limit map has no automatic fix" );
			Require( result.Summary.Contains( "brush #3 (1204)" ), "summary must list the biggest contributor: " + result.Summary );
			Require( result.Summary.Contains( "brush #5 (980)" ) && result.Summary.Contains( "brush #7 (911)" ), "summary must list all contributors" );
			Require( result.Summary.Contains( "70000" ) && result.Summary.Contains( "65536" ), "summary must state count and limit" );
			Require( StateKey( target ) == before, "a report-only fix must change nothing" );
			Require( undo.UndoCount == 0, "a report-only fix must push no undo step" );
		} );

		Check( "fix then undo-all restores the original document", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 64 ), "brick/brickwall" );
			AddBoxBrush( target, "1", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), "" );
			target.AddEntity( new MapEntityState
			{
				Id = "0",
				ClassName = "func_button",
				Origin = new Vec3( 128, 0, 32 ),
				Properties = new Dictionary<string, string> { { "target", "door_1" } }
			} );
			var doc = new IssueDocument { Name = "combined" };
			doc.Brushes.Add( IssueBrushFromMesh( 1, MakeBox( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ) ),
				new[] { "", "brick/brickwall", "brick/brickwall", "brick/brickwall", "brick/brickwall", "brick/brickwall" } ) );
			doc.Entities.Add( DocEntity( 0, "func_button", new Vec3( 128, 0, 32 ), ( "target", "door_1" ) ) );
			string before = StateKey( target );
			var fixer = new IssueAutoFix( target, undo );
			var r1 = fixer.Fix( doc, new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			var r2 = fixer.Fix( doc, new Issue { Kind = IssueKind.MissingMaterial, Code = "MISSING_MATERIAL", BrushId = 1 } );
			var r3 = fixer.Fix( doc, new Issue { Kind = IssueKind.MissingTarget, Code = "MISSING_TARGET", EntityId = 0 } );
			Require( r1.Applied && r2.Applied && r3.Applied, $"all three fixes must apply: {r1.Reason} / {r2.Reason} / {r3.Reason}" );
			Require( undo.UndoCount == 3, $"three fixes must be three undo steps, got {undo.UndoCount}" );
			undo.Undo();
			undo.Undo();
			undo.Undo();
			Require( StateKey( target ) == before, "undoing all three fixes must restore the original document" );
			Require( undo.RedoCount == 3, "all three fixes must be redoable" );
		} );

		Check( "fixture cases file matches the geometry", () =>
		{
			string? path = FindFixture( "cases.json" );
			Require( path != null, "cases.json fixture must ship with the tests" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path! ) );
			var root = doc.RootElement;
			Require( root.GetProperty( "schema" ).GetString() == "hammer-issue-autofix-actions/cases/v1", "fixture schema must match" );
			var lPrism = root.GetProperty( "lPrism" );
			Require( lPrism.GetProperty( "volume" ).GetInt32() == 7, "fixture L-prism volume must be 7" );
			var pieces = lPrism.GetProperty( "pieceVolumes" ).EnumerateArray().Select( e => e.GetInt32() ).OrderBy( v => v ).ToArray();
			Require( pieces.Length == 2 && pieces[0] == 1 && pieces[1] == 6, "fixture piece volumes must be 1 and 6" );
			Require( root.GetProperty( "invertedBrush" ).GetProperty( "volume" ).GetInt32() == 262144, "fixture inverted-brush volume must be 262144" );
			Require( root.GetProperty( "missingMaterial" ).GetProperty( "replacement" ).GetString() == "tools/toolsnodraw",
				"fixture nodraw replacement must be tools/toolsnodraw" );
			Require( root.GetProperty( "missingTarget" ).GetProperty( "stubClass" ).GetString() == "info_target",
				"fixture stub class must be info_target" );
			// The synthetic L-prism built above must agree with the fixture numbers.
			RequireVolume( MathF.Abs( MakeLPrism().SignedVolume() ), 7f, "synthetic L-prism vs fixture" );
		} );

		Check( "null issue and document throw; unknown kind and unknown brush refuse", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), "brick/brickwall" );
			var fixer = new IssueAutoFix( target, undo );
			bool threw = false;
			try { fixer.Fix( new IssueDocument(), null! ); } catch ( ArgumentNullException ) { threw = true; }
			Require( threw, "Fix(doc, null) must throw ArgumentNullException" );
			threw = false;
			try { fixer.Fix( null!, new Issue { Kind = IssueKind.Leak } ); } catch ( ArgumentNullException ) { threw = true; }
			Require( threw, "Fix(null, issue) must throw ArgumentNullException" );
			var unknown = fixer.Fix( new IssueDocument(), new Issue { Kind = (IssueKind)999, Code = "UNKNOWN" } );
			Require( !unknown.Applied && unknown.Reason.Contains( "No auto-fix" ), "unknown kind must refuse with a reason: " + unknown.Reason );
			var missing = fixer.Fix( new IssueDocument(), new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 99 } );
			Require( !missing.Applied && missing.Reason.Contains( "not in the undo target" ), "unknown brush must refuse: " + missing.Reason );
			Require( !IssueAutoFix.CanFix( IssueKind.Leak ), "leaks must not be CanFix" );
			Require( IssueAutoFix.CanFix( IssueKind.InvalidBrush ) && IssueAutoFix.CanFix( IssueKind.MissingTarget ) && IssueAutoFix.CanFix( IssueKind.MissingMaterial ),
				"the three fixable kinds must be CanFix" );
			Require( undo.UndoCount == 0, "refusals must push no undo step" );
		} );

		Check( "fix refuses while a stroke is active", () =>
		{
			var (target, undo) = NewStack();
			AddBoxBrush( target, "0", new Vec3( 0, 0, 0 ), new Vec3( 3, 3, 1 ), "brick/brickwall" );
			var doc = new IssueDocument { Name = "l-prism" };
			doc.Brushes.Add( IssueBrushFromMesh( 0, MakeLPrism(), Enumerable.Repeat( "brick/brickwall", 8 ).ToArray() ) );
			string before = StateKey( target );
			undo.BeginStroke( "sculpt stroke" );
			var fixer = new IssueAutoFix( target, undo );
			var refused = fixer.Fix( doc, new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			Require( !refused.Applied, "a fix must not land inside a stroke" );
			Require( refused.Reason.Contains( "stroke" ), "reason must name the stroke: " + refused.Reason );
			Require( StateKey( target ) == before, "the refused fix must change nothing" );
			undo.CancelStroke();
			var applied = fixer.Fix( doc, new Issue { Kind = IssueKind.InvalidBrush, Code = "INVALID_BRUSH", BrushId = 0 } );
			Require( applied.Applied, "the same fix must apply once the stroke is over: " + applied.Reason );
		} );
	}
}
