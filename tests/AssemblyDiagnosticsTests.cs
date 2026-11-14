using System;
using System.Linq;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS11: actionable constraint and reference diagnostics.
// Ports Mobile app/AssemblyDiagnosticsChecks.cs (18 scenarios) in Desktop form.
// Desktop differences, all deliberate: operations take (AssemblyDocument,
// EditHistory) instead of a CadDoc, undo/redo go through EditHistory (void
// Undo/Redo, guarded by CanUndo/CanRedo), save/reopen goes through
// AssemblyDocument.ToJson/FromJson instead of StudioDocument, and
// SolveForDiagnostics returns a (Result, Diagnostics) tuple, so the
// baseline solve is read through .Result. Restores replace the document's
// collections in place, so every check re-resolves occurrences and joints by
// stable Id after an undo or redo. DisplayName is the Desktop-wide form
// ("Fixed joint N" for fixed joints), so the pair-contradiction check asserts
// the part names in the diagnostic Title and Detail (both name the parts).
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyDiagnosticsTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-diagnostics", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		bool Undo( EditHistory history )
		{
			if ( !history.CanUndo ) return false;
			history.Undo();
			return true;
		}

		AssemblyDocument NewDoc( out AssemblyOccurrence left, out AssemblyOccurrence right )
		{
			var doc = new AssemblyDocument();
			doc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Bracket",
				SourceKind = "effigy-file", SourceReference = "parts/bracket.effigy#body-1",
			} );
			left = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = doc.Definitions[0].Id, Name = "Bracket left",
				Transform = AssemblyTransform.FromXform( Xform.Identity ),
			};
			right = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = doc.Definitions[0].Id, Name = "Bracket right",
				Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 4, 0, 0 ) ) ),
			};
			doc.Occurrences.Add( left );
			doc.Occurrences.Add( right );
			return doc;
		}

		AssemblyJoint Fixed( AssemblyOccurrence a, AssemblyOccurrence b, Vec3 originA, Vec3 originB ) => new()
		{
			Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = a.Id, OccurrenceB = b.Id,
			FrameA = AssemblyTransform.FromXform( Xform.Translate( originA ) ),
			FrameB = AssemblyTransform.FromXform( Xform.Translate( originB ) ),
		};

		AssemblyJoint Hinge( AssemblyOccurrence a, AssemblyOccurrence b, Xform frameA, Xform frameB ) => new()
		{
			Id = AssemblyDocument.NewId(), Kind = "hinge", OccurrenceA = a.Id, OccurrenceB = b.Id,
			FrameA = AssemblyTransform.FromXform( frameA ), FrameB = AssemblyTransform.FromXform( frameB ),
		};

		AssemblyJoint Slider( AssemblyOccurrence a, AssemblyOccurrence b ) => new()
		{
			Id = AssemblyDocument.NewId(), Kind = "slider", OccurrenceA = a.Id, OccurrenceB = b.Id,
			FrameA = AssemblyTransform.FromXform( Xform.Identity ), FrameB = AssemblyTransform.FromXform( Xform.Identity ),
		};

		// 1. A clean assembly reports nothing.
		Check( "valid grounded assembly reports no problems", () =>
		{
			var clean = NewDoc( out var cleanA, out var cleanB );
			AssemblyDiagnostics.SetGrounded( cleanA, true );
			clean.Joints.Add( Fixed( cleanA, cleanB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			Require( AssemblyDiagnostics.Diagnose( clean ).Count == 0, "expected zero diagnostics, got " + AssemblyDiagnostics.Diagnose( clean ).Count );
			Require( AssemblyDiagnostics.ErrorCount( clean ) == 0, "expected zero errors" );
			Require( !AssemblyDiagnostics.HasErrors( clean ), "expected HasErrors false" );
		} );

		// 2. A joint pointing at a gone occurrence names the cause and offers delete.
		Check( "joint to a missing occurrence is a classified missing-reference error with a delete action", () =>
		{
			var missing = NewDoc( out var missA, out var missB );
			var orphan = Fixed( missA, missB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			orphan.OccurrenceB = AssemblyDocument.NewId();
			missing.Joints.Add( orphan );
			var missingDiags = AssemblyDiagnostics.Diagnose( missing );
			Require( missingDiags.Any( d => d.Kind == AssemblyDiagnosticKind.MissingReference && d.IsError
					&& d.Action == AssemblyDiagnosticAction.DeleteJoint && d.AffectedNames.Count > 0 ),
				"expected a MissingReference error with a DeleteJoint action" );
			Require( AssemblyDiagnostics.ErrorCount( missing ) == 1, "expected exactly one error" );
			Require( AssemblyDiagnostics.HasErrors( missing ), "expected HasErrors true" );
		} );
		Check( "deleting the orphan joint clears the error", () =>
		{
			var missing = NewDoc( out var missA, out var missB );
			var orphan = Fixed( missA, missB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			orphan.OccurrenceB = AssemblyDocument.NewId();
			missing.Joints.Add( orphan );
			var history = new EditHistory();
			Require( AssemblyDiagnostics.TryDeleteJoint( missing, history, orphan.Id ).Success
					&& AssemblyDiagnostics.ErrorCount( missing ) == 0,
				"expected the delete to succeed and the error to clear" );
		} );

		// 3. A missing source file is its own kind, and broken references survive save/reopen.
		Check( "unresolvable source file is a missing-source error naming the file", () =>
		{
			var gone = NewDoc( out _, out _ );
			gone.Definitions[0].SourceReference = "parts/gone.effigy#body-1";
			var goneDiags = AssemblyDiagnostics.Diagnose( gone, _ => false );
			Require( goneDiags.Any( d => d.Kind == AssemblyDiagnosticKind.MissingSource && d.IsError
					&& d.Detail.Contains( "parts/gone.effigy", StringComparison.Ordinal ) ),
				"expected a MissingSource error naming the file" );
		} );
		Check( "missing source is still diagnosed after save and reopen", () =>
		{
			var gone = NewDoc( out _, out _ );
			gone.Definitions[0].SourceReference = "parts/gone.effigy#body-1";
			var goneReopened = AssemblyDocument.FromJson( gone.ToJson() );
			Require( AssemblyDiagnostics.Diagnose( goneReopened, _ => false )
					.Any( d => d.Kind == AssemblyDiagnosticKind.MissingSource ),
				"expected the missing source to survive save and reopen" );
		} );

		// 4. Two fixed joints, same pair: different frames contradict, identical frames are redundant.
		Check( "two fixed joints demanding different poses are a contradictory error naming the parts", () =>
		{
			var clash = NewDoc( out var clashA, out var clashB );
			clash.Joints.Add( Fixed( clashA, clashB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			clash.Joints.Add( Fixed( clashA, clashB, new Vec3( 2, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			var clashDiags = AssemblyDiagnostics.Diagnose( clash );
			Require( clashDiags.Any( d => d.Kind == AssemblyDiagnosticKind.Contradictory && d.IsError
					&& d.Message.Contains( "Bracket left", StringComparison.Ordinal )
					&& d.Detail.Contains( "Bracket left", StringComparison.Ordinal ) ),
				"expected a Contradictory error naming Bracket left in title and detail" );
			Require( AssemblyDiagnostics.HasErrors( clash ), "expected HasErrors true" );
		} );
		Check( "two identical fixed joints are a redundant warning, not an error", () =>
		{
			var dupe = NewDoc( out var dupeA, out var dupeB );
			dupe.Joints.Add( Fixed( dupeA, dupeB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			dupe.Joints.Add( Fixed( dupeA, dupeB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			var dupeDiags = AssemblyDiagnostics.Diagnose( dupe );
			Require( dupeDiags.Any( d => d.Kind == AssemblyDiagnosticKind.RedundantJoint && !d.IsError )
					&& !dupeDiags.Any( d => d.IsError ),
				"expected a RedundantJoint warning and no error" );
			Require( !AssemblyDiagnostics.HasErrors( dupe ), "expected HasErrors false" );
		} );

		// 5. Fixed + hinge on the same pair contradicts; hinge + slider does not.
		Check( "fixed joint plus hinge on the same pair is contradictory", () =>
		{
			var locked = NewDoc( out var lockA, out var lockB );
			locked.Joints.Add( Fixed( lockA, lockB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			locked.Joints.Add( Hinge( lockA, lockB, Xform.Identity, Xform.Identity ) );
			Require( AssemblyDiagnostics.Diagnose( locked )
					.Any( d => d.Kind == AssemblyDiagnosticKind.Contradictory && d.IsError ),
				"expected a Contradictory error for fixed plus hinge" );
		} );
		Check( "hinge plus slider on the same pair is a cylindrical pairing, not a contradiction", () =>
		{
			var cyl = NewDoc( out var cylA, out var cylB );
			cyl.Joints.Add( Hinge( cylA, cylB, Xform.Identity, Xform.Identity ) );
			cyl.Joints.Add( Slider( cylA, cylB ) );
			Require( !AssemblyDiagnostics.Diagnose( cyl ).Any( d => d.Kind == AssemblyDiagnosticKind.Contradictory ),
				"expected no Contradictory diagnostic for hinge plus slider" );
		} );

		// 6. Two hinges about different axes contradict; about the same axis they are redundant.
		Check( "two hinges about different axes are contradictory", () =>
		{
			var axes = NewDoc( out var axA, out var axB );
			axes.Joints.Add( Hinge( axA, axB, Xform.Identity, Xform.Identity ) );
			axes.Joints.Add( Hinge( axA, axB, Xform.Rotate( new Vec3( 1, 0, 0 ), -MathF.PI / 2 ), Xform.Identity ) );
			Require( AssemblyDiagnostics.Diagnose( axes )
					.Any( d => d.Kind == AssemblyDiagnosticKind.Contradictory && d.IsError ),
				"expected a Contradictory error for hinges about different axes" );
		} );
		Check( "two hinges about the same axis are redundant", () =>
		{
			var sameAxis = NewDoc( out var saA, out var saB );
			sameAxis.Joints.Add( Hinge( saA, saB, Xform.Identity, Xform.Identity ) );
			sameAxis.Joints.Add( Hinge( saA, saB, Xform.Identity, Xform.Identity ) );
			Require( AssemblyDiagnostics.Diagnose( sameAxis )
					.Any( d => d.Kind == AssemblyDiagnosticKind.RedundantJoint && !d.IsError ),
				"expected a RedundantJoint warning for hinges about the same axis" );
		} );

		// 7. A connector frame that stopped being rigid (face changed upstream) is unsupported geometry.
		Check( "non-rigid connector frame is unsupported geometry with a reselect action", () =>
		{
			var stale = NewDoc( out var staleA, out var staleB );
			var staleJoint = Fixed( staleA, staleB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			staleJoint.FrameA.X = new[] { 0f, 0f, 0f };
			stale.Joints.Add( staleJoint );
			var staleDiags = AssemblyDiagnostics.Diagnose( stale );
			Require( staleDiags.Any( d => d.Kind == AssemblyDiagnosticKind.UnsupportedGeometry && d.IsError
					&& d.Action == AssemblyDiagnosticAction.Reselect ),
				"expected an UnsupportedGeometry error with a Reselect action" );
		} );

		// 8. A disconnected part is a warning, never an error; a hinge-only part is info.
		Check( "unconnected part is a floating warning, not an error", () =>
		{
			var lone = NewDoc( out _, out _ );
			var loneDiags = AssemblyDiagnostics.Diagnose( lone );
			Require( loneDiags.Any( d => d.Kind == AssemblyDiagnosticKind.FloatingOccurrence && !d.IsError )
					&& !loneDiags.Any( d => d.IsError ),
				"expected a FloatingOccurrence warning and no error" );
		} );
		Check( "hinge-only part is under-constrained info, not an error", () =>
		{
			var free = NewDoc( out var freeA, out var freeB );
			free.Joints.Add( Hinge( freeA, freeB, Xform.Identity, Xform.Identity ) );
			var freeDiags = AssemblyDiagnostics.Diagnose( free );
			Require( freeDiags.Any( d => d.Kind == AssemblyDiagnosticKind.UnderConstrained && d.Severity == AssemblyDiagnosticSeverity.Info )
					&& !freeDiags.Any( d => d.IsError ),
				"expected UnderConstrained info and no error" );
		} );
		Check( "under-constrained detail names the free degrees of freedom", () =>
		{
			var free = NewDoc( out var freeA, out var freeB );
			free.Joints.Add( Hinge( freeA, freeB, Xform.Identity, Xform.Identity ) );
			var dof = AssemblyFixedJointSolver.FreeDegreesOfFreedom( free, freeB.Id );
			var detail = AssemblyDiagnostics.Diagnose( free )
				.First( d => d.Kind == AssemblyDiagnosticKind.UnderConstrained && d.OccurrenceIds.Contains( freeB.Id ) ).Detail;
			Require( detail.Contains( dof + " free degree", StringComparison.Ordinal ),
				"expected the detail to name " + dof + " free degrees, got: " + detail );
			var grounded = NewDoc( out var gA, out var gB );
			AssemblyDiagnostics.SetGrounded( gA, true );
			grounded.Joints.Add( Hinge( gA, gB, Xform.Identity, Xform.Identity ) );
			var groundedDof = AssemblyFixedJointSolver.FreeDegreesOfFreedom( grounded, gB.Id );
			Require( groundedDof == 1, "expected 1 free degree against a grounded root, got " + groundedDof );
			var groundedDetail = AssemblyDiagnostics.Diagnose( grounded )
				.First( d => d.Kind == AssemblyDiagnosticKind.UnderConstrained && d.OccurrenceIds.Contains( gB.Id ) ).Detail;
			Require( groundedDetail.Contains( "1 free degree", StringComparison.Ordinal ),
				"expected the detail to name 1 free degree, got: " + groundedDetail );
		} );

		// 9. A triangle of fixed joints is a loop; suppressing one joint breaks it.
		Check( "triangle of fixed joints is an unsupported loop warning", () =>
		{
			var loop = NewDoc( out var loopA, out var loopB );
			var loopC = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = loop.Definitions[0].Id, Name = "Bracket top",
				Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 4, 0 ) ) ),
			};
			loop.Occurrences.Add( loopC );
			loop.Joints.Add( Fixed( loopA, loopB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			loop.Joints.Add( Fixed( loopB, loopC, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			loop.Joints.Add( Fixed( loopC, loopA, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			Require( AssemblyDiagnostics.Diagnose( loop )
					.Any( d => d.Kind == AssemblyDiagnosticKind.UnsupportedLoop && d.Severity == AssemblyDiagnosticSeverity.Warning ),
				"expected an UnsupportedLoop warning" );
		} );
		Check( "suppressing one joint in the loop clears the loop warning", () =>
		{
			var loop = NewDoc( out var loopA, out var loopB );
			var loopC = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = loop.Definitions[0].Id, Name = "Bracket top",
				Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 4, 0 ) ) ),
			};
			loop.Occurrences.Add( loopC );
			loop.Joints.Add( Fixed( loopA, loopB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			loop.Joints.Add( Fixed( loopB, loopC, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			var loopJoint = Fixed( loopC, loopA, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			loop.Joints.Add( loopJoint );
			var history = new EditHistory();
			Require( AssemblyDiagnostics.TrySuppress( loop, history, loopJoint.Id, true ).Success
					&& !AssemblyDiagnostics.Diagnose( loop ).Any( d => d.Kind == AssemblyDiagnosticKind.UnsupportedLoop ),
				"expected the suppress to succeed and the loop warning to clear" );
			Require( AssemblyDiagnostics.Diagnose( loop ).Any( d => d.Kind == AssemblyDiagnosticKind.SuppressedJoint && d.Severity == AssemblyDiagnosticSeverity.Info ),
				"expected a SuppressedJoint info diagnostic for the suppressed joint" );
		} );

		// 10. Inverted limits are an error; setting them is refused before any undo step is taken.
		Check( "minimum above maximum is an invalid-limit error with a swap action", () =>
		{
			var limits = NewDoc( out var limA, out var limB );
			var hinge = Hinge( limA, limB, Xform.Identity, Xform.Identity );
			hinge.Minimum = 2f; hinge.Maximum = -2f;
			limits.Joints.Add( hinge );
			Require( AssemblyDiagnostics.Diagnose( limits )
					.Any( d => d.Kind == AssemblyDiagnosticKind.InvalidLimit && d.IsError
						&& d.Action == AssemblyDiagnosticAction.RepairLimits ),
				"expected an InvalidLimit error with a RepairLimits action" );
			Require( AssemblyDiagnostics.CanSwapLimits( hinge ), "expected CanSwapLimits true for inverted limits" );
		} );
		Check( "inverted limits are refused without consuming an undo step", () =>
		{
			var fresh = NewDoc( out var freshA, out var freshB );
			var freshJoint = Fixed( freshA, freshB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			fresh.Joints.Add( freshJoint );
			var history = new EditHistory();
			Require( !history.CanUndo
					&& !AssemblyDiagnostics.TrySetJointLimits( fresh, history, freshJoint.Id, 5f, 1f ).Success
					&& !history.CanUndo,
				"expected the inverted limits to be refused with no undo step taken" );
			var stored = fresh.Joints.First( j => j.Id == freshJoint.Id );
			Require( !stored.Minimum.HasValue && !stored.Maximum.HasValue, "expected the limits to stay unchanged" );
		} );
		Check( "swapping inverted limits repairs them", () =>
		{
			var limits = NewDoc( out var limA, out var limB );
			var hinge = Hinge( limA, limB, Xform.Identity, Xform.Identity );
			hinge.Minimum = 2f; hinge.Maximum = -2f;
			limits.Joints.Add( hinge );
			var history = new EditHistory();
			Require( AssemblyDiagnostics.TrySwapJointLimits( limits, history, hinge.Id ).Success,
				"expected the swap to succeed" );
			var stored = limits.Joints.First( j => j.Id == hinge.Id );
			Require( stored.Minimum == -2f && stored.Maximum == 2f,
				"expected the limits swapped to -2..2, got " + stored.Minimum + ".." + stored.Maximum );
			Require( !AssemblyDiagnostics.Diagnose( limits ).Any( d => d.Kind == AssemblyDiagnosticKind.InvalidLimit ),
				"expected the InvalidLimit error to clear after the swap" );
		} );

		// 11. An edit that would introduce an error is rolled back; the last valid pose is kept.
		Check( "edit introducing an error is rolled back and the document is byte-identical to before", () =>
		{
			var guarded = NewDoc( out var grdA, out var grdB );
			var guardedJoint = Fixed( grdA, grdB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			guarded.Joints.Add( guardedJoint );
			var beforeJson = guarded.ToJson();
			var history = new EditHistory();
			var bad = AssemblyDiagnostics.TryEdit( guarded, history, assembly =>
			{
				assembly.Joints[0].Minimum = 5f;
				assembly.Joints[0].Maximum = 1f;
			} );
			Require( !bad.Success && bad.RolledBack && bad.Diagnostics.Any( d => d.Kind == AssemblyDiagnosticKind.InvalidLimit )
					&& guarded.ToJson() == beforeJson,
				"expected the edit to roll back with the document byte-identical" );
			Require( !history.CanUndo, "expected a rolled-back edit to take no undo step" );
		} );

		// 12. Undo restores a successful suppress; a failed edit leaves redo history alone.
		Check( "undo after a suppress brings the joint back", () =>
		{
			var undone = NewDoc( out var unA, out var unB );
			var undoneJoint = Fixed( unA, unB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			undone.Joints.Add( undoneJoint );
			var history = new EditHistory();
			Require( AssemblyDiagnostics.TrySuppress( undone, history, undoneJoint.Id, true ).Success
					&& AssemblyDiagnostics.IsSuppressed( undone.Joints[0] )
					&& Undo( history )
					&& !AssemblyDiagnostics.IsSuppressed( undone.Joints[0] ),
				"expected the suppress to succeed, then undo to restore the active joint" );
		} );

		// 13. An occurrence cannot be deleted while a joint still uses it.
		Check( "deleting a jointed occurrence is refused while the joint exists", () =>
		{
			var guarded2 = NewDoc( out var g2A, out var g2B );
			guarded2.Joints.Add( Fixed( g2A, g2B, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			var history = new EditHistory();
			Require( !AssemblyDiagnostics.TryDeleteOccurrence( guarded2, history, g2B.Id ).Success
					&& guarded2.Occurrences.Count == 2,
				"expected the delete to be refused while the joint exists" );
		} );
		Check( "deleting the joint first lets the occurrence go, and the shared source stays", () =>
		{
			var guarded2 = NewDoc( out var g2A, out var g2B );
			guarded2.Joints.Add( Fixed( g2A, g2B, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			var history = new EditHistory();
			var jointId = guarded2.Joints[0].Id;
			Require( AssemblyDiagnostics.TryDeleteJoint( guarded2, history, jointId ).Success
					&& AssemblyDiagnostics.TryDeleteOccurrence( guarded2, history, g2B.Id ).Success
					&& guarded2.Occurrences.Count == 1,
				"expected the joint delete then the occurrence delete to succeed" );
			Require( guarded2.Definitions.Count == 1, "expected the shared definition to stay" );
		} );

		// 14. Reorder changes solve order; undo restores it.
		Check( "joint reorder moves solve order and undoes cleanly", () =>
		{
			var order = NewDoc( out var ordA, out var ordB );
			var ordC = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = order.Definitions[0].Id, Name = "Bracket top",
				Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 4, 0 ) ) ),
			};
			order.Occurrences.Add( ordC );
			var firstJoint = Fixed( ordA, ordB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			var secondJoint = Fixed( ordB, ordC, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			order.Joints.Add( firstJoint );
			order.Joints.Add( secondJoint );
			var history = new EditHistory();
			Require( AssemblyDiagnostics.TryMoveJoint( order, history, secondJoint.Id, 0 ).Success
					&& order.Joints[0].Id == secondJoint.Id
					&& Undo( history ) && order.Joints[0].Id == firstJoint.Id,
				"expected the reorder to move the joint and undo to restore it" );
		} );

		// 15. No grounded root is a warning; grounding clears it; ungrounding brings it back.
		Check( "joints with no grounded part warn about the missing anchor", () =>
		{
			var root = NewDoc( out var rootA, out var rootB );
			root.Joints.Add( Fixed( rootA, rootB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			Require( AssemblyDiagnostics.Diagnose( root )
					.Any( d => d.Kind == AssemblyDiagnosticKind.NoGroundedRoot && d.Severity == AssemblyDiagnosticSeverity.Warning ),
				"expected a NoGroundedRoot warning" );
		} );
		Check( "grounding a part clears the missing-anchor warning", () =>
		{
			var root = NewDoc( out var rootA, out var rootB );
			root.Joints.Add( Fixed( rootA, rootB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			var history = new EditHistory();
			Require( AssemblyDiagnostics.TrySetGrounded( root, history, rootA.Id, true ).Success
					&& !AssemblyDiagnostics.Diagnose( root ).Any( d => d.Kind == AssemblyDiagnosticKind.NoGroundedRoot ),
				"expected grounding to clear the warning" );
			Require( AssemblyDiagnostics.IsGrounded( root.Occurrences.First( o => o.Id == rootA.Id ) ),
				"expected the occurrence to read back as grounded" );
		} );
		Check( "ungrounding brings the missing-anchor warning back", () =>
		{
			var root = NewDoc( out var rootA, out var rootB );
			root.Joints.Add( Fixed( rootA, rootB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			var history = new EditHistory();
			AssemblyDiagnostics.TrySetGrounded( root, history, rootA.Id, true );
			Require( AssemblyDiagnostics.TrySetGrounded( root, history, rootA.Id, false ).Success
					&& AssemblyDiagnostics.Diagnose( root ).Any( d => d.Kind == AssemblyDiagnosticKind.NoGroundedRoot ),
				"expected ungrounding to restore the warning" );
		} );

		// 16. Suppression and grounding survive save/reopen; delete impact names what disconnects.
		Check( "suppressed and grounded states survive save and reopen", () =>
		{
			var persist = NewDoc( out var perA, out var perB );
			var perJoint = Fixed( perA, perB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			persist.Joints.Add( perJoint );
			var history = new EditHistory();
			AssemblyDiagnostics.TrySuppress( persist, history, perJoint.Id, true );
			AssemblyDiagnostics.TrySetGrounded( persist, history, perA.Id, true );
			var persistReopened = AssemblyDocument.FromJson( persist.ToJson() );
			var reopenedJoint = persistReopened.Joints[0];
			Require( AssemblyDiagnostics.IsSuppressed( reopenedJoint )
					&& AssemblyDiagnostics.IsGrounded( persistReopened.Occurrences[0] )
					&& AssemblyDiagnostics.Diagnose( persistReopened )
						.Any( d => d.Kind == AssemblyDiagnosticKind.SuppressedJoint && d.Severity == AssemblyDiagnosticSeverity.Info ),
				"expected the suppressed and grounded states to survive save and reopen" );
		} );
		Check( "delete impact names both parts that would be left unconnected", () =>
		{
			var impactDoc = NewDoc( out var impA, out var impB );
			var impJoint = Fixed( impA, impB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			impactDoc.Joints.Add( impJoint );
			var impact = AssemblyDiagnostics.ImpactOfDeleteJoint( impactDoc, impJoint.Id );
			Require( impact.IsOnlyJoint
					&& impact.WouldDisconnectNames.Contains( "Bracket left" )
					&& impact.WouldDisconnectNames.Contains( "Bracket right" ),
				"expected the impact to name both parts and flag the only joint" );
		} );

		// 17. Rename: empty is refused, a real name sticks and is what diagnostics show.
		Check( "empty joint name is refused and a real rename sticks", () =>
		{
			var rename = NewDoc( out var renA, out var renB );
			var renJoint = Fixed( renA, renB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) );
			rename.Joints.Add( renJoint );
			var history = new EditHistory();
			Require( !AssemblyDiagnostics.TryRenameJoint( rename, history, renJoint.Id, "   " ).Success
					&& AssemblyDiagnostics.TryRenameJoint( rename, history, renJoint.Id, "Door hinge" ).Success
					&& AssemblyDiagnostics.DisplayName( rename.Joints[0], rename ) == "Door hinge",
				"expected the blank name to be refused and the real rename to stick" );
		} );
		Check( "renaming a hinge joint sticks and is what diagnostics show", () =>
		{
			var rename = NewDoc( out var renA, out var renB );
			var renJoint = Hinge( renA, renB, Xform.Identity, Xform.Identity );
			rename.Joints.Add( renJoint );
			var history = new EditHistory();
			Require( AssemblyDiagnostics.TryRenameJoint( rename, history, renJoint.Id, "Door hinge" ).Success
					&& AssemblyDiagnostics.DisplayName( rename.Joints[0], rename ) == "Door hinge",
				"expected the hinge rename to stick" );
			Require( AssemblyDiagnostics.TrySuppress( rename, history, renJoint.Id, true ).Success,
				"expected the suppress to succeed" );
			Require( AssemblyDiagnostics.Diagnose( rename )
					.Any( d => d.Kind == AssemblyDiagnosticKind.SuppressedJoint
						&& d.AffectedNames.Contains( "Door hinge" ) ),
				"expected diagnostics to show the new joint name" );
		} );

		// 18. Solver verdicts: two grounded parts that disagree are a
		// contradictory error, and a failed solve after a success labels
		// the last valid pose.
		Check( "two grounded parts that disagree are a contradictory error naming the part", () =>
		{
			var conflict = NewDoc( out var confA, out var confB );
			AssemblyDiagnostics.SetGrounded( confA, true );
			AssemblyDiagnostics.SetGrounded( confB, true );
			conflict.Joints.Add( Fixed( confA, confB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			Require( AssemblyDiagnostics.Diagnose( conflict )
					.Any( d => d.Kind == AssemblyDiagnosticKind.Contradictory && d.IsError
						&& d.AffectedNames.Count > 0 ),
				"expected a Contradictory error naming the part" );
		} );
		Check( "baseline solve succeeds before the breaking edit", () =>
		{
			var pose = NewDoc( out var poseA, out var poseB );
			AssemblyDiagnostics.SetGrounded( poseA, true );
			pose.Joints.Add( Fixed( poseA, poseB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			var previous = AssemblyDiagnostics.SolveForDiagnostics( pose );
			Require( previous.Result != null && previous.Result.Success, "expected the baseline solve to succeed" );
			Require( previous.Diagnostics.Count == 0, "expected the baseline solve to report no diagnostics" );
		} );
		Check( "a failed solve after a success labels the last valid pose", () =>
		{
			var pose = NewDoc( out var poseA, out var poseB );
			AssemblyDiagnostics.SetGrounded( poseA, true );
			pose.Joints.Add( Fixed( poseA, poseB, new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			var previous = AssemblyDiagnostics.SolveForDiagnostics( pose );
			Require( previous.Result != null && previous.Result.Success, "expected the baseline solve to succeed" );
			pose.Joints.Add( Fixed( poseA, poseB, new Vec3( 2, 0, 0 ), new Vec3( -1, 0, 0 ) ) );
			Require( AssemblyDiagnostics.Diagnose( pose, previousResult: previous.Result )
					.Any( d => d.Kind == AssemblyDiagnosticKind.PreservedLastValid && d.Severity == AssemblyDiagnosticSeverity.Info ),
				"expected a PreservedLastValid info diagnostic" );
			var failed = AssemblyDiagnostics.SolveForDiagnostics( pose, new AssemblySolverOptions { PreviousResult = previous.Result } );
			Require( failed.Result != null && !failed.Result.Success && failed.Result.PreservedLastValid,
				"expected the failing solve to preserve the last valid result" );
			Require( failed.Diagnostics.Any( d => d.Kind == AssemblyDiagnosticKind.PreservedLastValid ),
				"expected the failing solve's diagnostics to label the last valid pose" );
		} );
		Check( "an unsupported joint kind is reported, not a false all-clear", () =>
		{
			var weird = NewDoc( out var wA, out var wB );
			weird.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "ball", OccurrenceA = wA.Id, OccurrenceB = wB.Id,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ), FrameB = AssemblyTransform.FromXform( Xform.Identity ),
			} );
			var diags = AssemblyDiagnostics.Diagnose( weird );
			Require( diags.Count > 0 && diags.Any( d => d.Kind == AssemblyDiagnosticKind.UnderConstrained ),
				"expected the unknown joint kind to be reported as under-constrained, not zero diagnostics" );
			Require( !diags.Any( d => d.IsError ), "expected no error for the unknown kind alone" );
		} );
	}
}
