using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS15: end-to-end Desktop assembly acceptance. Three fixtures drive the
// FULL workflow through the documented Desktop service APIs only — create,
// duplicate, place, connect, solve, inspect DOFs, preview/cancel/commit,
// undo/redo, save, close/reopen, render via the AS04 snapshot, export via
// AS12 — and the cross-cutting checks re-import the exports independently
// and convert the assembly to a rig (AS13).
//
//   A: table/chair — one definition repeated as four legs, grounded root,
//      fixed joints, solve to a clean 0-diagnostic state, 0 free DOF.
//   B: hinged door + fixed handle — hinge with measured limits, motion
//      preview drag + cancel leaves the document untouched, commit moves
//      the door (handle follows), undo restores, save/reopen keeps the pose,
//      snapshot + render meshes show the door at the committed angle.
//   C: rifle bolt slider + knob — slider with constrained travel, set-travel
//      rejects out-of-limit (stored value survives), solve clamps, motion
//      drag within limits moves carriage + knob, export at rest vs current
//      pose differ as expected.
//
// Mobile source verified against (git rev-parse HEAD, 2026-11-13):
//   /home/poo/geppetto-mobile @ 0a45d54329bef0e4a27f00306e87adf1d2969f2a
// Port sources: app/AssemblyParts.cs, AssemblyConnectors.cs,
// AssemblyJointCommands.cs, AssemblyPicking.cs, AssemblyFaceMateChecks.cs,
// AssemblyAxisMateChecks.cs, AssemblyExport.cs (+ AssemblyExportChecks.cs),
// AssemblyToRig.cs, app/AssemblyDiagnosticsChecks.cs (via
// UiAssemblyDiagnostics.cs), AssemblyHinge.cs, AssemblySlider.cs,
// UiAssemblyMotion.cs (AssemblyMotionSession), vendor kernel
// AssemblySolver.cs. Mobile has no single end-to-end acceptance file;
// this suite mirrors Mobile's per-feature checks at the Desktop service
// level instead of porting an acceptance file that does not exist.
// Pending / not mirrored here: GLB re-import is AS12's GlbInfo check
// (written, unrun until the final build) and is NOT repeated here; no GPU
// or viewport rendering is exercised (headless CPU snapshot + CpuMesh
// render list only — the GPU smoke script is the integrator's gate);
// no live bot-command path is exercised (services only); face/axis mate
// commit is covered by AS07's own 45 checks, not repeated here.
// Exact commands/exits are deferred to the final integrator: this file
// documents the workflow and cannot run under the edits-only rule (PO
// override: no build). Not runtime verified: the final integrator runs
// tests/Desktop.Tests.csproj.
//
// Undo/redo re-fetch records from the document after each step: history
// restores replace the document's collections, so a pre-undo object
// reference goes stale.
internal static class AssemblyAcceptanceTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-acceptance", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		static bool Close( float a, float b, float tol ) => MathF.Abs( a - b ) <= tol;
		void Near( float a, float b, float tol, string what )
		{
			if ( !Close( a, b, tol ) ) throw new Exception( $"{what}: expected {b}, got {a}" );
		}
		static bool VecClose( Vec3 a, Vec3 b, float tol ) =>
			Close( a.x, b.x, tol ) && Close( a.y, b.y, tol ) && Close( a.z, b.z, tol );
		void RequireVec( Vec3 got, Vec3 want, float tol, string what )
		{
			if ( !VecClose( got, want, tol ) )
				throw new Exception( $"{what}: got ({got.x:0.###}, {got.y:0.###}, {got.z:0.###}) want ({want.x:0.###}, {want.y:0.###}, {want.z:0.###})" );
		}
		static bool BoundsClose( (Vec3 Min, Vec3 Max) got, (Vec3 Min, Vec3 Max) want, float tol ) =>
			VecClose( got.Min, want.Min, tol ) && VecClose( got.Max, want.Max, tol );
		void RequireBounds( (Vec3 Min, Vec3 Max) got, (Vec3 Min, Vec3 Max) want, float tol, string what )
		{
			if ( !BoundsClose( got, want, tol ) )
				throw new Exception( $"{what}: got X [{got.Min.x:0.###}..{got.Max.x:0.###}] Y [{got.Min.y:0.###}..{got.Max.y:0.###}] Z [{got.Min.z:0.###}..{got.Max.z:0.###}] want X [{want.Min.x:0.###}..{want.Max.x:0.###}] Y [{want.Min.y:0.###}..{want.Max.y:0.###}] Z [{want.Min.z:0.###}..{want.Max.z:0.###}]" );
		}
		static (Vec3 Min, Vec3 Max) MeshBounds( PolyMesh m )
		{
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in m.Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
			return (min, max);
		}
		static (Vec3 Min, Vec3 Max) TransformedBounds( PolyMesh m, Xform t )
		{
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in m.Positions )
			{
				var q = t.TransformPoint( p );
				min = new Vec3( MathF.Min( min.x, q.x ), MathF.Min( min.y, q.y ), MathF.Min( min.z, q.z ) );
				max = new Vec3( MathF.Max( max.x, q.x ), MathF.Max( max.y, q.y ), MathF.Max( max.z, q.z ) );
			}
			return (min, max);
		}
		static string TestDir()
		{
			var dir = Path.Combine( Path.GetTempPath(), "asm-as15-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( dir );
			return dir;
		}
		static void DeleteDir( string dir )
		{
			try { if ( Directory.Exists( dir ) ) Directory.Delete( dir, true ); } catch { /* best effort cleanup */ }
		}

		static bool SourceExists( PartStudio studio, AssemblyPartDefinition def ) =>
			AssemblyPartService.SourceExists( studio, def );


		// ================= Fixture A: table =================

		Check( "table: grounded root + 4 fixed joints solve to the stored poses", () =>
		{
			var f = new TableFixture();
			var result = AssemblyFixedJointSolver.Solve( f.Doc, new AssemblySolverOptions() );
			Require( result.Success, "solve failed: " + string.Join( "; ", result.Diagnostics.Select( d => d.Code + ": " + d.Message ) ) );
			Require( result.TryGetSolvedTransform( f.TopId, out var topSolved ), "top has no solved transform" );
			RequireVec( topSolved.Origin, Vec3.Zero, 1e-4f, "top must stay at the origin" );
			for ( int i = 0; i < 4; i++ )
			{
				Require( result.TryGetSolvedTransform( f.LegIds[i], out var legSolved ), $"leg {i} has no solved transform" );
				RequireVec( legSolved.Origin, f.LegOrigins[i], 1e-4f, $"leg {i} solved origin" );
			}
		} );

		Check( "table: zero diagnostics and zero free degrees of freedom", () =>
		{
			var f = new TableFixture();
			Require( f.Doc.Validate( def => SourceExists( f.Studio, def ) ).Count == 0, "document validation must be clean" );
			var diagnostics = AssemblyDiagnostics.Diagnose( f.Doc, def => SourceExists( f.Studio, def ) );
			Require( diagnostics.Count == 0, "expected zero diagnostics, got " + diagnostics.Count + ": " + string.Join( "; ", diagnostics.Select( d => d.Kind + "/" + d.Message ) ) );
			Require( AssemblyDiagnostics.ErrorCount( f.Doc, def => SourceExists( f.Studio, def ) ) == 0, "expected zero errors" );
			Require( !AssemblyDiagnostics.HasErrors( f.Doc, def => SourceExists( f.Studio, def ) ), "expected HasErrors false" );
			for ( int i = 0; i < 4; i++ )
				Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( f.Doc, f.LegIds[i] ) == 0, $"leg {i} must have 0 free DOF" );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( f.Doc, f.TopId ) == 0, "grounded top must have 0 free DOF" );
		} );

		Check( "table: DuplicateOccurrence copies the definition, offset +N in in X", () =>
		{
			var studio = new PartStudio();
			var body = new Body( "body-leg", "Leg", BoxMesh( 0.75f, 0.75f, 6 ) );
			studio.Bodies.Add( body );
			var doc = new AssemblyDocument();
			doc.Definitions.Add( Def( "Leg", body.Id ) );
			var leg = Occ( doc.Definitions[0].Id, "Leg", Xform.Translate( new Vec3( 10, 6, -6.5f ) ) );
			doc.Occurrences.Add( leg );
			studio.Assembly = doc;
			var copy = AssemblyPartService.DuplicateOccurrence( studio, leg.Id );
			if ( copy == null ) throw new Exception( "duplicate must return the copy" );
			Require( copy.DefinitionId == leg.DefinitionId, "copy must share the definition" );
			Require( copy.Name.Contains( "copy", StringComparison.OrdinalIgnoreCase ), "copy name must say copy, got " + copy.Name );
			// One sibling of the definition existed, so the copy sits +1 in
			// in X from the source.
			RequireVec( copy.Transform.ToXform().Origin, new Vec3( 11, 6, -6.5f ), 1e-4f, "copy origin" );
			Require( doc.Occurrences.Count == 2, "document must hold source + copy" );
			Require( AssemblyPartService.DuplicateOccurrence( studio, AssemblyDocument.NewId() ) == null,
				"duplicating a missing occurrence must return null" );
		} );

		Check( "table: snapshot render bounds cover the top and all four legs", () =>
		{
			var f = new TableFixture();
			var snapshot = f.Snapshot();
			Require( snapshot.VisibleCount == 5, $"expected 5 visible occurrences, got {snapshot.VisibleCount}" );
			Require( snapshot.MissingOccurrenceIds.Count == 0, "no occurrence may be missing" );
			Require( snapshot.Occurrences.Count == 5, $"expected 5 snapshots, got {snapshot.Occurrences.Count}" );
			foreach ( var snap in snapshot.Occurrences )
				Require( !snap.MissingSource && snap.Mesh != null, $"occurrence {snap.Name} must resolve" );
			RequireBounds( snapshot.Bounds,
				(new Vec3( -12, -8, -12.5f ), new Vec3( 12, 8, 0.5f )), 1e-4f, "table union bounds" );
			var topSnap = snapshot.Occurrences.First( s => s.OccurrenceId == f.TopId );
			RequireBounds( (topSnap.BoundsMin, topSnap.BoundsMax),
				(new Vec3( -12, -8, -0.5f ), new Vec3( 12, 8, 0.5f )), 1e-4f, "top bounds" );
		} );

		// ================= Fixture B: hinged door =================

		Check( "door: hinge reads 0 degrees at rest, one free DOF, no errors", () =>
		{
			var f = new DoorFixture();
			Near( f.HingeAngle(), 0f, 1e-4f, "hinge angle at rest" );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( f.Doc, f.DoorId ) == 1,
				"door must have exactly 1 free DOF (the hinge)" );
			Require( AssemblyDiagnostics.ErrorCount( f.Doc, def => SourceExists( f.Studio, def ) ) == 0,
				"door assembly must have no errors" );
		} );

		Check( "door: preview drag then cancel leaves the document untouched", () =>
		{
			var f = new DoorFixture();
			var jsonBefore = f.Doc.ToJson();
			var undoBefore = f.History.UndoCount;
			var session = new AssemblyMotionSession( f.Doc );
			Require( session.Begin( LiveJoint( f.Doc, f.HingeId ), f.History ), "begin failed: " + session.Problem );
			Near( session.SetValue( MathF.PI / 4 ), MathF.PI / 4, 1e-4f, "preview value" );
			Require( session.ApplyPreview(), "preview apply failed" );
			var previewOrigin = LiveOcc( f.Doc, f.DoorId ).Transform.ToXform().Origin;
			RequireVec( previewOrigin, new Vec3( MathF.Cos( MathF.PI / 4 ), MathF.Sin( MathF.PI / 4 ), 0 ), 1e-4f,
				"preview must swing the door to 45 degrees" );
			Require( session.EndSession( false ), "cancel failed" );
			Require( f.Doc.ToJson() == jsonBefore, "cancel must restore the document byte-identically" );
			Require( f.History.UndoCount == undoBefore, "cancel must leave no undo step" );
			Near( f.HingeAngle(), 0f, 1e-4f, "angle after cancel" );
		} );

		Check( "door: commit 45 degrees, handle follows, undo restores, redo re-applies", () =>
		{
			var f = new DoorFixture();
			var session = new AssemblyMotionSession( f.Doc );
			Require( session.Begin( LiveJoint( f.Doc, f.HingeId ), f.History ), "begin failed: " + session.Problem );
			Near( session.SetValue( MathF.PI / 4 ), MathF.PI / 4, 1e-4f, "drag value" );
			Require( session.ApplyPreview(), "preview apply failed" );
			Require( session.EndSession( true ), "commit failed" );
			Require( f.History.UndoCount == 1, $"commit must register exactly one undo step, got {f.History.UndoCount}" );
			Near( f.HingeAngle(), MathF.PI / 4, 1e-4f, "committed angle" );
			// The handle is fixed to the door, so it rides the same delta:
			// its origin is the door placement applied to the rest offset.
			var doorPlacement = LiveOcc( f.Doc, f.DoorId ).Transform.ToXform();
			var wantHandle = doorPlacement.TransformPoint( new Vec3( 0.75f, 0.5f, 0 ) );
			RequireVec( LiveOcc( f.Doc, f.HandleId ).Transform.ToXform().Origin, wantHandle, 1e-4f, "handle origin after commit" );

			f.History.Undo();
			Near( f.HingeAngle(), 0f, 1e-4f, "angle after undo" );
			RequireVec( LiveOcc( f.Doc, f.DoorId ).Transform.ToXform().Origin, new Vec3( 1, 0, 0 ), 1e-4f, "door origin after undo" );
			RequireVec( LiveOcc( f.Doc, f.HandleId ).Transform.ToXform().Origin, new Vec3( 1.75f, 0.5f, 0 ), 1e-4f, "handle origin after undo" );

			f.History.Redo();
			Near( f.HingeAngle(), MathF.PI / 4, 1e-4f, "angle after redo" );
		} );

		Check( "door: save/reopen keeps the committed 45 degree pose", () =>
		{
			var f = new DoorFixture();
			var session = new AssemblyMotionSession( f.Doc );
			Require( session.Begin( LiveJoint( f.Doc, f.HingeId ), f.History ), "begin failed: " + session.Problem );
			session.SetValue( MathF.PI / 4 );
			Require( session.ApplyPreview(), "preview apply failed" );
			Require( session.EndSession( true ), "commit failed" );
			var jsonBefore = f.Doc.ToJson();
			f.Studio.Assembly = f.Doc;
			var reopened = StudioDocument.Read( StudioDocument.Write( f.Studio ) );
			Require( reopened.Assembly.ToJson() == jsonBefore, "assembly must round-trip through save/reopen" );
			Require( reopened.Assembly.Occurrences.Count == 3, "reopened assembly must keep 3 occurrences" );
			var joint = LiveJoint( reopened.Assembly, f.HingeId );
			Require( AssemblyHinge.TryGetHingeAngle( reopened.Assembly, joint, out var angle, out var why )
				&& Close( angle, MathF.PI / 4, 1e-4f ), "reopened hinge must read 45 degrees: " + why );
		} );

		Check( "door: snapshot and render meshes show the panel at 45 degrees", () =>
		{
			var f = new DoorFixture();
			var session = new AssemblyMotionSession( f.Doc );
			Require( session.Begin( LiveJoint( f.Doc, f.HingeId ), f.History ), "begin failed: " + session.Problem );
			session.SetValue( MathF.PI / 4 );
			Require( session.ApplyPreview(), "preview apply failed" );
			Require( session.EndSession( true ), "commit failed" );
			var snapshot = f.Snapshot();
			var doorSnap = snapshot.Occurrences.First( s => s.OccurrenceId == f.DoorId );
			var want = TransformedBounds( f.DoorMesh, LiveOcc( f.Doc, f.DoorId ).Transform.ToXform() );
			RequireBounds( (doorSnap.BoundsMin, doorSnap.BoundsMax), want, 1e-4f, "door snapshot bounds at 45 degrees" );
			// The 45-degree panel is no longer axis-aligned with the rest
			// pose: its X span must differ from the rest span [0,2].
			Require( !Close( doorSnap.BoundsMin.x, 0f, 1e-3f ) || !Close( doorSnap.BoundsMax.x, 2f, 1e-3f ),
				"door bounds must have moved off the rest span" );
			var render = new AssemblySnapshotService().BuildRenderMeshes( snapshot );
			Require( render.Count == 3, $"render list must hold 3 meshes, got {render.Count}" );
			var rMin = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var rMax = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var mesh in render )
			{
				rMin = new Vec3( MathF.Min( rMin.x, mesh.BoundsMin.X ), MathF.Min( rMin.y, mesh.BoundsMin.Y ), MathF.Min( rMin.z, mesh.BoundsMin.Z ) );
				rMax = new Vec3( MathF.Max( rMax.x, mesh.BoundsMax.X ), MathF.Max( rMax.y, mesh.BoundsMax.Y ), MathF.Max( rMax.z, mesh.BoundsMax.Z ) );
			}
			RequireBounds( (rMin, rMax), snapshot.Bounds, 1e-4f, "render-mesh union bounds" );
		} );

		// ================= Fixture C: rifle bolt =================

		Check( "bolt: measured travel starts at 1 in, on axis", () =>
		{
			var f = new BoltFixture();
			var joint = LiveJoint( f.Doc, f.SliderId );
			Require( AssemblySlider.TryMeasure( joint, LiveOcc( f.Doc, f.RailId ), LiveOcc( f.Doc, f.CarriageId ),
				out var travel, out var offAxis, out var error ), "measure failed: " + error );
			Near( travel, 1f, 1e-4f, "measured travel" );
			Near( offAxis, 0f, 1e-4f, "off-axis" );
		} );

		Check( "bolt: set-travel rejects 6 in and the stored position survives; solve clamps to 4 in", () =>
		{
			var f = new BoltFixture();
			var joint = LiveJoint( f.Doc, f.SliderId );
			Require( AssemblySliderService.SetTravel( f.Doc, f.History, f.SliderId, 1.5f, out var error ),
				"valid travel must apply: " + error );
			Require( AssemblySlider.TryGetPosition( LiveJoint( f.Doc, f.SliderId ), out var stored ) && Close( stored, 1.5f, 1e-4f ),
				"stored position must be 1.5 in" );
			Near( f.CarriageZ(), 1.5f, 1e-4f, "carriage after valid set-travel" );
			var undoBefore = f.History.UndoCount;
			Require( !AssemblySliderService.SetTravel( f.Doc, f.History, f.SliderId, 6f, out error ),
				"6 in must be rejected past the 4 in limit" );
			Require( AssemblySlider.TryGetPosition( LiveJoint( f.Doc, f.SliderId ), out stored ) && Close( stored, 1.5f, 1e-4f ),
				"stored position must survive the rejected set" );
			Near( f.CarriageZ(), 1.5f, 1e-4f, "carriage must not move on a rejected set" );
			Require( f.History.UndoCount == undoBefore, "a refused edit must cost no undo step" );
			// NOTE: SetTravel moves only B (no chain walk); the session is
			// the path that carries parts fixed to the carriage (see below).
			Require( AssemblySlider.TrySolve( joint, LiveOcc( f.Doc, f.RailId ), LiveOcc( f.Doc, f.CarriageId ),
				6f, out var solvedB, out error, out var clamped ), "solve failed: " + error );
			Require( clamped, "solving 6 in must report clamped" );
			Near( solvedB.Origin.z, 4f, 1e-4f, "clamped solve must stop at the 4 in limit" );
		} );

		Check( "bolt: motion drag to 2.5 in moves carriage + knob, undo restores, redo re-applies", () =>
		{
			var f = new BoltFixture();
			var session = new AssemblyMotionSession( f.Doc );
			Require( session.Begin( LiveJoint( f.Doc, f.SliderId ), f.History ), "begin failed: " + session.Problem );
			Near( session.RestValue, 1f, 1e-4f, "rest travel" );
			Near( session.SetValue( 2.5f ), 2.5f, 1e-4f, "drag value" );
			Require( session.ApplyPreview(), "preview apply failed" );
			Require( session.EndSession( true ), "commit failed" );
			Near( f.CarriageZ(), 2.5f, 1e-4f, "carriage after commit" );
			Near( f.KnobZ(), 7f, 1e-4f, "knob must ride the carriage (+4.5 in)" );
			Require( AssemblySlider.TryGetPosition( LiveJoint( f.Doc, f.SliderId ), out var stored ) && Close( stored, 2.5f, 1e-4f ),
				"stored position must sync on commit" );

			f.History.Undo();
			Near( f.CarriageZ(), 1f, 1e-4f, "carriage after undo" );
			Near( f.KnobZ(), 5.5f, 1e-4f, "knob after undo" );

			f.History.Redo();
			Near( f.CarriageZ(), 2.5f, 1e-4f, "carriage after redo" );
			Near( f.KnobZ(), 7f, 1e-4f, "knob after redo" );
		} );

		Check( "bolt: export at rest vs current pose differ by the 2.5 in drag", () =>
		{
			var f = new BoltFixture();
			var session = new AssemblyMotionSession( f.Doc );
			Require( session.Begin( LiveJoint( f.Doc, f.SliderId ), f.History ), "begin failed: " + session.Problem );
			session.SetValue( 2.5f );
			Require( session.ApplyPreview(), "preview apply failed" );
			Require( session.EndSession( true ), "commit failed" );
			var dir = TestDir();
			try
			{
				var current = AssemblyExportService.ExportAssembledObj( f.Doc, f.Studio, f.Resolver(),
					AssemblyExportPose.Current, dir, "bolt" );
				Require( current.Success, "current export failed: " + current.ErrorMessage );
				var currentObj = ParseObj( File.ReadAllText( current.Files.First( p => p.EndsWith( ".obj", StringComparison.Ordinal ) ) ) );
				RequireBounds( ObjBounds( currentObj, "Carriage" ),
					(new Vec3( -4, -4, -1.5f ), new Vec3( 4, 4, 6.5f )), 1e-4f, "carriage bounds, current pose" );
				RequireBounds( ObjBounds( currentObj, "Knob" ),
					(new Vec3( -1, -1, 6 ), new Vec3( 1, 1, 8 )), 1e-4f, "knob bounds, current pose" );

				var rest = AssemblyExportService.ExportAssembledObj( f.Doc, f.Studio, f.Resolver(),
					AssemblyExportPose.Rest, dir, "bolt-rest" );
				Require( rest.Success, "rest export failed: " + rest.ErrorMessage );
				var restObj = ParseObj( File.ReadAllText( rest.Files.First( p => p.EndsWith( ".obj", StringComparison.Ordinal ) ) ) );
				RequireBounds( ObjBounds( restObj, "Carriage" ),
					(new Vec3( -4, -4, -4 ), new Vec3( 4, 4, 4 )), 1e-4f, "carriage bounds, rest pose" );
				RequireBounds( ObjBounds( restObj, "Knob" ),
					(new Vec3( -1, -1, 3.5f ), new Vec3( 1, 1, 5.5f )), 1e-4f, "knob bounds, rest pose" );
				// Control: the grounded rail never moves between poses.
				RequireBounds( ObjBounds( currentObj, "Rail" ), ObjBounds( restObj, "Rail" ), 1e-4f, "rail must be identical in both poses" );
				// The live document keeps the committed pose: rest export
				// works on a throwaway copy.
				Near( f.CarriageZ(), 2.5f, 1e-4f, "live carriage after rest export" );
			}
			finally { DeleteDir( dir ); }
		} );

		// ================= Cross-cutting =================

		Check( "exports re-import: assembled OBJ matches the snapshot (geometry, pose, materials)", () =>
		{
			var f = new TableFixture();
			var dir = TestDir();
			try
			{
				var result = AssemblyExportService.ExportAssembledObj( f.Doc, f.Studio, f.Resolver(),
					AssemblyExportPose.Current, dir, "table" );
				Require( result.Success, "export failed: " + result.ErrorMessage );
				Require( result.Files.Count == 2, $"expected OBJ + MTL, got {result.Files.Count} files" );
				var objPath = result.Files.First( p => p.EndsWith( ".obj", StringComparison.Ordinal ) );
				var mtlPath = result.Files.First( p => p.EndsWith( ".mtl", StringComparison.Ordinal ) );
				var model = ParseObj( File.ReadAllText( objPath ) );
				Require( model.ObjectNames.SequenceEqual( new[] { "Top", "Leg", "Leg 2", "Leg 3", "Leg 4" } ),
					"objects must be in document order with ' 2' dedup: " + string.Join( ", ", model.ObjectNames ) );
				var snapshot = f.Snapshot();
				Require( snapshot.Occurrences.Count == 5, "snapshot must hold 5 occurrences" );
				var definedMaterials = MtlNames( File.ReadAllText( mtlPath ) );
				for ( int i = 0; i < 5; i++ )
				{
					var name = model.ObjectNames[i];
					var snap = snapshot.Occurrences[i];
					Require( model.Faces[name] == 12, $"object {name} must have 12 triangles, got {model.Faces[name]}" );
					RequireBounds( ObjBounds( model, name ), (snap.BoundsMin, snap.BoundsMax), 1e-4f,
						$"re-imported bounds for {name}" );
					Require( model.Material.TryGetValue( name, out var mat ) && !string.IsNullOrWhiteSpace( mat ),
						$"object {name} must carry a material" );
					Require( definedMaterials.Contains( mat ), $"material {mat} must be defined in the .mtl" );
				}
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "broken: dangling joint is diagnosed and blocks the export", () =>
		{
			var f = new TableFixture();
			f.Doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed",
				OccurrenceA = f.TopId, OccurrenceB = AssemblyDocument.NewId(),
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Identity ),
			} );
			Require( AssemblyDiagnostics.ErrorCount( f.Doc, def => SourceExists( f.Studio, def ) ) > 0,
				"dangling joint must produce an error" );
			var diagnostics = AssemblyDiagnostics.Diagnose( f.Doc, def => SourceExists( f.Studio, def ) );
			Require( diagnostics.Any( d => d.Kind == AssemblyDiagnosticKind.MissingReference ),
				"dangling joint must be a missing-reference diagnostic" );
			var dir = TestDir();
			try
			{
				var result = AssemblyExportService.ExportAssembledObj( f.Doc, f.Studio, f.Resolver(),
					AssemblyExportPose.Current, dir, "broken" );
				Require( !result.Success, "export with a dangling joint must fail" );
				Require( result.ErrorMessage != null && result.ErrorMessage.Contains( "does not exist", StringComparison.Ordinal ),
					"export error must name the missing occurrence, got: " + result.ErrorMessage );
				Require( !Directory.GetFiles( dir, "*.obj", SearchOption.AllDirectories ).Any(),
					"a blocked export must write no OBJ file" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "to-rig: table converts to an editable rig; save/reopen keeps rig + pins", () =>
		{
			var f = new TableFixture();
			var jsonBefore = f.Doc.ToJson();
			var (destination, result) = AssemblyToRig.ConvertToNewStudio( f.Doc, f.Studio, f.TopId );
			Require( result.Plan.Success, "plan failed: " + result.Plan.Error );
			Require( destination.Rig.Count >= 1, $"destination rig must have at least one bone, got {destination.Rig.Count}" );
			Require( result.BoundBodies.Count == 2
				&& result.BoundBodies.ContainsKey( f.TopBody.Id )
				&& result.BoundBodies.ContainsKey( f.LegBody.Id ),
				"both bodies must be pinned, got: " + string.Join( ", ", result.BoundBodies.Keys ) );
			Require( result.UnboundBodies.Count == 0, "no body may be left unbound" );
			Require( f.Studio.Rig.Count == 0, "source studio must keep no rig" );
			Require( f.Doc.ToJson() == jsonBefore, "conversion must not mutate the assembly" );
			Require( destination.Assembly.ToJson() == jsonBefore, "destination must carry the assembly snapshot" );
			var reopened = StudioDocument.Read( StudioDocument.Write( destination ) );
			Require( reopened.Rig.Count == destination.Rig.Count,
				$"rig must round-trip, got {reopened.Rig.Count} vs {destination.Rig.Count}" );
			Require( reopened.BodyBoneMap.ContainsKey( f.TopBody.Id ) && reopened.BodyBoneMap.ContainsKey( f.LegBody.Id ),
				"body pins must round-trip through save/reopen" );
			Require( reopened.Assembly.ToJson() == jsonBefore, "assembly must round-trip with the rig" );
		} );

		Check( "saved poses: the open-door pose round-trips through save/apply", () =>
		{
			var f = new DoorFixture();
			var session = new AssemblyMotionSession( f.Doc );
			Require( session.Begin( LiveJoint( f.Doc, f.HingeId ), f.History ), "begin failed: " + session.Problem );
			session.SetValue( MathF.PI / 4 );
			Require( session.ApplyPreview(), "preview apply failed" );
			Require( session.EndSession( true ), "commit failed" );
			Require( AssemblySavedPoses.SavePose( f.Doc, f.History, "Open", out var why ), "save pose failed: " + why );
			Require( AssemblySavedPoses.ListPoses( f.Doc ).Contains( "Open" ), "pose list must contain Open" );
			Require( AssemblyHingeService.SetAngle( f.Doc, f.History, f.HingeId, 0f, out why ), "close failed: " + why );
			Near( f.HingeAngle(), 0f, 1e-4f, "angle after closing" );
			Require( AssemblySavedPoses.ApplyPose( f.Doc, f.History, "Open", out why ), "apply pose failed: " + why );
			Near( f.HingeAngle(), MathF.PI / 4, 1e-4f, "angle after applying the saved pose" );
		} );
	}

		// Centred box with the AS04 outward winding; signed volume is
		// +(2hx)(2hy)(2hz).
		static PolyMesh BoxMesh( float hx, float hy, float hz )
		{
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( -hx, -hy, -hz ), new Vec3( hx, -hy, -hz ), new Vec3( hx, hy, -hz ), new Vec3( -hx, hy, -hz ),
				new Vec3( -hx, -hy, hz ), new Vec3( hx, -hy, hz ), new Vec3( hx, hy, hz ), new Vec3( -hx, hy, hz ),
			} );
			mesh.AddFace( new[] { 0, 2, 1 } ); mesh.AddFace( new[] { 0, 3, 2 } ); // bottom -Z
			mesh.AddFace( new[] { 4, 5, 6 } ); mesh.AddFace( new[] { 4, 6, 7 } ); // top +Z
			mesh.AddFace( new[] { 0, 1, 5 } ); mesh.AddFace( new[] { 0, 5, 4 } ); // front -Y
			mesh.AddFace( new[] { 3, 7, 6 } ); mesh.AddFace( new[] { 3, 6, 2 } ); // back +Y
			mesh.AddFace( new[] { 0, 4, 7 } ); mesh.AddFace( new[] { 0, 7, 3 } ); // left -X
			mesh.AddFace( new[] { 1, 2, 6 } ); mesh.AddFace( new[] { 1, 6, 5 } ); // right +X
			return mesh;
		}

		static AssemblyPartDefinition Def( string name, string bodyId ) => new()
		{
			Id = AssemblyDocument.NewId(), Name = name,
			SourceKind = "studio-body", SourceReference = bodyId,
		};

		static AssemblyOccurrence Occ( string defId, string name, Xform t ) => new()
		{
			Id = AssemblyDocument.NewId(), DefinitionId = defId, Name = name,
			Transform = AssemblyTransform.FromXform( t ),
		};

		static AssemblyOccurrence LiveOcc( AssemblyDocument doc, string id ) =>
			doc.Occurrences.First( o => o != null && o.Id == id );

		static AssemblyJoint LiveJoint( AssemblyDocument doc, string id ) =>
			doc.Joints.First( j => j != null && j.Id == id );

		// Fixed joint through the AS05 service. Frames are given in each
		// occurrence's LOCAL coordinates; the caller picks them so the
		// solved pose equals the current placement (no surprise move).
		static AssemblyJointResult FixedJoint( AssemblyDocument doc, EditHistory history,
			string aId, string bId, Xform frameA, Xform frameB, string name = "" )
		{
			var draft = new AssemblyJointDraft
			{
				ConnectorA = new AssemblyConnectorRef( aId, AssemblyTransform.FromXform( frameA ), "face", "Top face", "face#1" ),
				ConnectorB = new AssemblyConnectorRef( bId, AssemblyTransform.FromXform( frameB ), "face", "Top face", "face#1" ),
				Name = name,
			};
			return AssemblyJointService.CreateFixedJoint( doc, history, draft );
		}

		// Independent OBJ re-import: parses only `o`, `v`, `f` and `usemtl`
		// lines with invariant culture. It shares no code with ObjWriter or
		// ObjReader, so a writer bug cannot hide behind a matching reader.
		sealed class ObjModel
		{
			public List<string> ObjectNames = new();
			public Dictionary<string, List<Vec3>> Vertices = new( StringComparer.Ordinal );
			public Dictionary<string, int> Faces = new( StringComparer.Ordinal );
			public Dictionary<string, string> Material = new( StringComparer.Ordinal );
		}

		static ObjModel ParseObj( string text )
		{
			var model = new ObjModel();
			string? current = null;
			foreach ( var raw in text.Split( '\n' ) )
			{
				var line = raw.Trim();
				if ( line.StartsWith( "o ", StringComparison.Ordinal ) )
				{
					current = line.Substring( 2 ).Trim();
					model.ObjectNames.Add( current );
					model.Vertices[current] = new List<Vec3>();
					model.Faces[current] = 0;
				}
				else if ( line.StartsWith( "v ", StringComparison.Ordinal ) && current != null )
				{
					var parts = line.Substring( 2 ).Split( ' ', StringSplitOptions.RemoveEmptyEntries );
					model.Vertices[current].Add( new Vec3(
						float.Parse( parts[0], CultureInfo.InvariantCulture ),
						float.Parse( parts[1], CultureInfo.InvariantCulture ),
						float.Parse( parts[2], CultureInfo.InvariantCulture ) ) );
				}
				else if ( line.StartsWith( "f ", StringComparison.Ordinal ) && current != null )
					model.Faces[current]++;
				else if ( line.StartsWith( "usemtl ", StringComparison.Ordinal ) && current != null )
					model.Material[current] = line.Substring( 7 ).Trim();
			}
			return model;
		}

		static (Vec3 Min, Vec3 Max) ObjBounds( ObjModel model, string name )
		{
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in model.Vertices[name] )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
			return (min, max);
		}

		static HashSet<string> MtlNames( string mtlText )
		{
			var names = new HashSet<string>( StringComparer.Ordinal );
			foreach ( var raw in mtlText.Split( '\n' ) )
			{
				var line = raw.Trim();
				if ( line.StartsWith( "newmtl ", StringComparison.Ordinal ) )
					names.Add( line.Substring( 7 ).Trim() );
			}
			return names;
		}

		// ---- Fixture A: table — 24x16x1 top, four 1.5x1.5x12 legs ----
		sealed class TableFixture
		{
			public PartStudio Studio = new PartStudio();
			public AssemblyDocument Doc = new AssemblyDocument();
			public EditHistory History = new EditHistory();
			public Body TopBody;
			public Body LegBody;
			public string TopId;
			public string[] LegIds = new string[4];
			public Vec3[] LegOrigins =
			{
				new Vec3( 10, 6, -6.5f ), new Vec3( -10, 6, -6.5f ),
				new Vec3( 10, -6, -6.5f ), new Vec3( -10, -6, -6.5f ),
			};

			public TableFixture()
			{
				TopBody = new Body( "body-top", "Top", BoxMesh( 12, 8, 0.5f ) );
				LegBody = new Body( "body-leg", "Leg", BoxMesh( 0.75f, 0.75f, 6 ) );
				Studio.Bodies.Add( TopBody );
				Studio.Bodies.Add( LegBody );
				var defTop = Def( "Top", TopBody.Id );
				var defLeg = Def( "Leg", LegBody.Id );
				Doc.Definitions.AddRange( new[] { defTop, defLeg } );
				var top = Occ( defTop.Id, "Top", Xform.Identity );
				TopId = top.Id;
				Doc.Occurrences.Add( top );
				for ( int i = 0; i < 4; i++ )
				{
					var leg = Occ( defLeg.Id, "Leg", Xform.Translate( LegOrigins[i] ) );
					LegIds[i] = leg.Id;
					Doc.Occurrences.Add( leg );
				}
				AssemblyGrounding.SetGrounded( Doc, TopId, true );
				for ( int i = 0; i < 4; i++ )
				{
					// FrameB is the inverse of the leg placement, so the
					// joint's solved pose is the leg's current placement:
					// the legs stay where they were put.
					var frameB = Xform.Translate( new Vec3( -LegOrigins[i].x, -LegOrigins[i].y, -LegOrigins[i].z ) );
					var result = FixedJoint( Doc, History, TopId, LegIds[i], Xform.Identity, frameB );
					if ( !result.Ok ) throw new Exception( "table joint failed: " + result.Message );
				}
			}

			public AssemblySourceResolver Resolver() => AssemblyExportService.StudioResolver( Studio );
			public AssemblySnapshot Snapshot() => new AssemblySnapshotService().Build( Doc, Resolver() );
		}

		// ---- Fixture B: hinged door + fixed handle ----
		// Jamb post at the origin (grounded); door panel 2 wide in X with
		// its hinge line on the world Z axis through the origin, so at
		// angle 0 the panel spans X [0,2]; handle on the panel's far edge.
		sealed class DoorFixture
		{
			public PartStudio Studio = new PartStudio();
			public AssemblyDocument Doc = new AssemblyDocument();
			public EditHistory History = new EditHistory();
			public Body JambBody;
			public Body DoorBody;
			public Body HandleBody;
			public string JambId;
			public string DoorId;
			public string HandleId;
			public string HingeId;
			public string FixedId;
			public PolyMesh DoorMesh;

			public DoorFixture()
			{
				DoorMesh = BoxMesh( 1, 0.25f, 4 );
				JambBody = new Body( "body-jamb", "Jamb", BoxMesh( 0.5f, 2, 5 ) );
				DoorBody = new Body( "body-door", "Door", DoorMesh );
				HandleBody = new Body( "body-handle", "Handle", BoxMesh( 0.25f, 0.25f, 1 ) );
				Studio.Bodies.Add( JambBody );
				Studio.Bodies.Add( DoorBody );
				Studio.Bodies.Add( HandleBody );
				var defJamb = Def( "Jamb", JambBody.Id );
				var defDoor = Def( "Door", DoorBody.Id );
				var defHandle = Def( "Handle", HandleBody.Id );
				Doc.Definitions.AddRange( new[] { defJamb, defDoor, defHandle } );
				var jamb = Occ( defJamb.Id, "Jamb", Xform.Identity );
				var door = Occ( defDoor.Id, "Door", Xform.Translate( new Vec3( 1, 0, 0 ) ) );
				var handle = Occ( defHandle.Id, "Handle", Xform.Translate( new Vec3( 1.75f, 0.5f, 0 ) ) );
				JambId = jamb.Id; DoorId = door.Id; HandleId = handle.Id;
				Doc.Occurrences.AddRange( new[] { jamb, door, handle } );
				AssemblyGrounding.SetGrounded( Doc, JambId, true );
				if ( !AssemblyHingeService.CreateHinge( Doc, History, JambId, DoorId,
						AssemblyTransform.FromXform( Xform.Identity ),
						AssemblyTransform.FromXform( Xform.Translate( new Vec3( -1, 0, 0 ) ) ),
						-MathF.PI / 2, MathF.PI / 2, out var hinge, out var why ) )
					throw new Exception( "door hinge failed: " + why );
				HingeId = hinge.Id;
				// Handle fixed to the door: frames encode the current
				// relative placement (door +1 X, handle +1.75 X +0.5 Y).
				var fixedResult = FixedJoint( Doc, History, DoorId, HandleId,
					Xform.Identity, Xform.Translate( new Vec3( -0.75f, -0.5f, 0 ) ) );
				if ( !fixedResult.Ok ) throw new Exception( "handle joint failed: " + fixedResult.Message );
				FixedId = Doc.Joints.First( j => j != null && j.Kind == "fixed" ).Id;
			}

			public AssemblySourceResolver Resolver() => AssemblyExportService.StudioResolver( Studio );
			public AssemblySnapshot Snapshot() => new AssemblySnapshotService().Build( Doc, Resolver() );
			public float HingeAngle()
			{
				var joint = LiveJoint( Doc, HingeId );
				if ( !AssemblyHinge.TryGetHingeAngle( Doc, joint, out var angle, out var why ) )
					throw new Exception( "cannot read hinge angle: " + why );
				return angle;
			}
		}

		// ---- Fixture C: rifle bolt — rail + sliding carriage + knob ----
		// Rail along Z at the origin (grounded); carriage starts at travel
		// 1 in; knob fixed on top of the carriage (relative +4.5 Z).
		sealed class BoltFixture
		{
			public PartStudio Studio = new PartStudio();
			public AssemblyDocument Doc = new AssemblyDocument();
			public EditHistory History = new EditHistory();
			public Body RailBody;
			public Body CarriageBody;
			public Body KnobBody;
			public string RailId;
			public string CarriageId;
			public string KnobId;
			public string SliderId;

			public BoltFixture()
			{
				RailBody = new Body( "body-rail", "Rail", BoxMesh( 1, 1, 8 ) );
				CarriageBody = new Body( "body-carriage", "Carriage", BoxMesh( 4, 4, 4 ) );
				KnobBody = new Body( "body-knob", "Knob", BoxMesh( 1, 1, 1 ) );
				Studio.Bodies.Add( RailBody );
				Studio.Bodies.Add( CarriageBody );
				Studio.Bodies.Add( KnobBody );
				var defRail = Def( "Rail", RailBody.Id );
				var defCarriage = Def( "Carriage", CarriageBody.Id );
				var defKnob = Def( "Knob", KnobBody.Id );
				Doc.Definitions.AddRange( new[] { defRail, defCarriage, defKnob } );
				var rail = Occ( defRail.Id, "Rail", Xform.Identity );
				var carriage = Occ( defCarriage.Id, "Carriage", Xform.Translate( new Vec3( 0, 0, 1 ) ) );
				var knob = Occ( defKnob.Id, "Knob", Xform.Translate( new Vec3( 0, 0, 5.5f ) ) );
				RailId = rail.Id; CarriageId = carriage.Id; KnobId = knob.Id;
				Doc.Occurrences.AddRange( new[] { rail, carriage, knob } );
				AssemblyGrounding.SetGrounded( Doc, RailId, true );
				if ( !AssemblySliderService.CreateSlider( Doc, History, RailId, CarriageId,
						AssemblyTransform.FromXform( Xform.Identity ),
						AssemblyTransform.FromXform( Xform.Identity ),
						-2f, 4f, out var slider, out var error ) )
					throw new Exception( "bolt slider failed: " + error );
				SliderId = slider.Id;
				var fixedResult = FixedJoint( Doc, History, CarriageId, KnobId,
					Xform.Identity, Xform.Translate( new Vec3( 0, 0, -4.5f ) ) );
				if ( !fixedResult.Ok ) throw new Exception( "knob joint failed: " + fixedResult.Message );
			}

			public AssemblySourceResolver Resolver() => AssemblyExportService.StudioResolver( Studio );
			public AssemblySnapshot Snapshot() => new AssemblySnapshotService().Build( Doc, Resolver() );
			public float CarriageZ() => LiveOcc( Doc, CarriageId ).Transform.ToXform().Origin.z;
			public float KnobZ() => LiveOcc( Doc, KnobId ).Transform.ToXform().Origin.z;
		}

}
