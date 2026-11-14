using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS08: hinge joints with measured angle limits.
// Ports Mobile app/AssemblyHingeChecks.cs (non-UI assertions) in Desktop form:
// fixtures are two-occurrence documents and a three-part chain. Checks compare
// measured angles, positions and diagnostics, never only a success flag.
// The UI-sheet half of the Mobile checks (sheet opens, buttons, Apply/Cancel)
// has no Desktop counterpart and is not mirrored here; the degree-field parsing
// it exercised lives in AssemblyHingeService.TryParseDegrees and is covered.
// Undo/redo go through EditHistory + AssemblyHistory (one undo step per call);
// save/reopen goes through AssemblyDocument ToJson/FromJson.
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyHingeTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-hinge", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		static bool Near( float a, float b, float eps = 1e-5f ) =>
			MathF.Abs( AssemblyHinge.WrapAngle( a - b ) ) <= eps;
		static float Deg( float degrees ) => degrees * MathF.PI / 180f;

		AssemblyOccurrence Occ( string name, Xform transform, string definitionId ) => new()
		{
			Id = AssemblyDocument.NewId(), DefinitionId = definitionId, Name = name,
			Transform = AssemblyTransform.FromXform( transform ),
		};
		AssemblyJoint Hinge( AssemblyOccurrence a, AssemblyOccurrence b,
			AssemblyTransform frameA = null, AssemblyTransform frameB = null,
			float? minimum = null, float? maximum = null ) => new()
		{
			Id = AssemblyDocument.NewId(), Kind = "hinge", OccurrenceA = a.Id, OccurrenceB = b.Id,
			FrameA = frameA ?? new AssemblyTransform(), FrameB = frameB ?? new AssemblyTransform(),
			Minimum = minimum, Maximum = maximum,
		};
		(AssemblyDocument Doc, AssemblyJoint Joint, AssemblyOccurrence A, AssemblyOccurrence B) Setup(
			Xform transformA, Xform transformB,
			AssemblyTransform frameA = null, AssemblyTransform frameB = null,
			float? minimum = null, float? maximum = null )
		{
			var doc = new AssemblyDocument();
			doc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Bracket",
				SourceKind = "effigy-file", SourceReference = "parts/bracket.effigy#body-1",
			} );
			var a = Occ( "A", transformA, doc.Definitions[0].Id );
			var b = Occ( "B", transformB, doc.Definitions[0].Id );
			doc.Occurrences.Add( a );
			doc.Occurrences.Add( b );
			var joint = Hinge( a, b, frameA, frameB, minimum, maximum );
			doc.Joints.Add( joint );
			return (doc, joint, a, b);
		}
		float Angle( AssemblyDocument doc, AssemblyJoint joint )
		{
			if ( !AssemblyHinge.TryGetHingeAngle( doc, joint, out var angle, out var why ) )
				throw new InvalidOperationException( "angle unreadable: " + why );
			return angle;
		}

		// 0, positive and negative angles.
		Check( "angle zero is frames aligned", () =>
		{
			var zero = Setup( Xform.Identity, Xform.Identity );
			Require( Near( Angle( zero.Doc, zero.Joint ), 0f ), "identity hinge should read 0" );
		} );
		Check( "positive angle reads +45", () =>
		{
			var pos = Setup( Xform.Identity, Xform.Rotate( new Vec3( 0, 0, 1 ), Deg( 45 ) ) );
			Require( Near( Angle( pos.Doc, pos.Joint ), Deg( 45 ) ), "rotated B should read +45" );
		} );
		Check( "negative angle reads -30", () =>
		{
			var neg = Setup( Xform.Identity, Xform.Rotate( new Vec3( 0, 0, 1 ), Deg( -30 ) ) );
			Require( Near( Angle( neg.Doc, neg.Joint ), Deg( -30 ) ), "rotated B should read -30" );
		} );

		// Transformed axes: tilt the whole hinge 30 degrees about X and shift it; the
		// shared axis moves with the parts, the angle still reads 45.
		Check( "transformed axes keep the angle", () =>
		{
			var tilted = Setup(
				Xform.Translate( new Vec3( 5, -2, 3 ) ) * Xform.Rotate( new Vec3( 1, 0, 0 ), Deg( 30 ) ),
				Xform.Translate( new Vec3( 5, -2, 3 ) ) * Xform.Rotate( new Vec3( 1, 0, 0 ), Deg( 30 ) ) * Xform.Rotate( new Vec3( 0, 0, 1 ), Deg( 45 ) ) );
			Require( Near( Angle( tilted.Doc, tilted.Joint ), Deg( 45 ) ), "tilted hinge should still read 45" );
		} );

		// Limit boundaries clamp instead of overshooting.
		Check( "angle stops exactly at the maximum", () =>
		{
			var limited = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			Require( AssemblyHinge.TrySetHingeAngle( limited.Doc, limited.Joint, Deg( 90 ), null, out _ )
				&& Near( Angle( limited.Doc, limited.Joint ), Deg( 90 ) ), "set to 90 should land at 90" );
		} );
		Check( "angle past the maximum clamps to it", () =>
		{
			var limited = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			Require( AssemblyHinge.TrySetHingeAngle( limited.Doc, limited.Joint, Deg( 120 ), null, out _ )
				&& Near( Angle( limited.Doc, limited.Joint ), Deg( 90 ) ), "set to 120 should clamp to 90" );
		} );
		Check( "angle past the minimum clamps to it", () =>
		{
			var limited = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			Require( AssemblyHinge.TrySetHingeAngle( limited.Doc, limited.Joint, Deg( -120 ), null, out _ )
				&& Near( Angle( limited.Doc, limited.Joint ), Deg( -90 ) ), "set to -120 should clamp to -90" );
		} );

		// Angles wrap: 270 degrees is -90, never 270.
		Check( "270 degrees reads as -90", () =>
		{
			var free = Setup( Xform.Identity, Xform.Identity );
			Require( AssemblyHinge.TrySetHingeAngle( free.Doc, free.Joint, Deg( 270 ), null, out _ )
				&& Near( Angle( free.Doc, free.Joint ), Deg( -90 ) ), "270 should wrap to -90" );
		} );
		Check( "-270 degrees reads as 90", () =>
		{
			var free = Setup( Xform.Identity, Xform.Identity );
			Require( AssemblyHinge.TrySetHingeAngle( free.Doc, free.Joint, Deg( -270 ), null, out _ )
				&& Near( Angle( free.Doc, free.Joint ), Deg( 90 ) ), "-270 should wrap to 90" );
		} );

		// Inverted limits are rejected and the previous valid limits are kept.
		Check( "inverted limits are rejected and the previous limits are kept", () =>
		{
			var limits = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			Require( !AssemblyHinge.TrySetLimits( limits.Joint, Deg( 90 ), Deg( -90 ), out var limitWhy )
				&& limitWhy != null && limitWhy.Contains( "cannot exceed", StringComparison.Ordinal )
				&& limits.Joint.Minimum == Deg( -90 ) && limits.Joint.Maximum == Deg( 90 ),
				"inverted limits should be refused with the old limits intact" );
		} );
		Check( "a hinge cannot be created with inverted limits", () =>
		{
			var limits = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			var badCreate = AssemblyHinge.CreateHinge( limits.Doc, limits.A.Id, limits.B.Id,
				new AssemblyTransform(), new AssemblyTransform(), Deg( 10 ), Deg( -10 ), out var createWhy );
			Require( badCreate == null && createWhy != null, "create with inverted limits should return null" );
		} );
		Check( "a hinge is created with a stable id, kind and limits", () =>
		{
			var limits = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			var made = AssemblyHinge.CreateHinge( limits.Doc, limits.A.Id, limits.B.Id,
				new AssemblyTransform(), new AssemblyTransform(), Deg( -45 ), Deg( 45 ), out _ );
			Require( made != null && made.Kind == "hinge" && made.Id.Length == 32
				&& made.Minimum == Deg( -45 ) && made.Maximum == Deg( 45 ),
				"created hinge should carry kind, 32-char id and limits" );
		} );
		Check( "CreateHinge refuses a missing occurrence, the same occurrence and a bad frame", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity );
			Require( AssemblyHinge.CreateHinge( setup.Doc, setup.A.Id, AssemblyDocument.NewId(),
					new AssemblyTransform(), new AssemblyTransform(), null, null, out var missingWhy ) == null
				&& missingWhy != null && missingWhy.Contains( "must exist", StringComparison.Ordinal ),
				"missing occurrence should be refused" );
			Require( AssemblyHinge.CreateHinge( setup.Doc, setup.A.Id, setup.A.Id,
					new AssemblyTransform(), new AssemblyTransform(), null, null, out var sameWhy ) == null
				&& sameWhy != null && sameWhy.Contains( "different occurrences", StringComparison.Ordinal ),
				"same occurrence on both sides should be refused" );
			Require( AssemblyHinge.CreateHinge( setup.Doc, setup.A.Id, setup.B.Id,
					null, new AssemblyTransform(), null, null, out var frameWhy ) == null
				&& frameWhy != null && frameWhy.Contains( "connector frame", StringComparison.Ordinal ),
				"missing frame should be refused" );
			var scaled = AssemblyTransform.FromXform( Xform.Scale( new Vec3( 2, 1, 1 ) ) );
			Require( AssemblyHinge.CreateHinge( setup.Doc, setup.A.Id, setup.B.Id,
					scaled, new AssemblyTransform(), null, null, out var rigidWhy ) == null
				&& rigidWhy != null && rigidWhy.Contains( "rigid transform", StringComparison.Ordinal ),
				"scaled frame should be refused" );
		} );

		// A flipped axis is a conflict, never a silent flip; nothing moves.
		Check( "flipped axis is a conflict and nothing moves", () =>
		{
			var flipped = Setup( Xform.Identity, Xform.Identity,
				frameB: AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI ) ) );
			var flippedBefore = flipped.Doc.ToJson();
			Require( !AssemblyHinge.TryGetHingeAngle( flipped.Doc, flipped.Joint, out _, out var flippedWhy )
				&& flippedWhy != null && flippedWhy.Contains( "flipped", StringComparison.Ordinal )
				&& !AssemblyHinge.TrySetHingeAngle( flipped.Doc, flipped.Joint, Deg( 30 ), null, out _ )
				&& flipped.Doc.ToJson() == flippedBefore,
				"flipped axis should fail reads and sets and leave the document untouched" );
		} );

		// A skewed axis is a conflict; nothing moves.
		Check( "skewed axis is a conflict and nothing moves", () =>
		{
			var skewed = Setup( Xform.Identity, Xform.Identity,
				frameB: AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 1, 0, 0 ), Deg( 5 ) ) ) );
			var skewedBefore = skewed.Doc.ToJson();
			Require( !AssemblyHinge.TryGetHingeAngle( skewed.Doc, skewed.Joint, out _, out var skewedWhy )
				&& skewedWhy != null && skewedWhy.Contains( "misaligned", StringComparison.Ordinal )
				&& !AssemblyHinge.TrySetHingeAngle( skewed.Doc, skewed.Joint, Deg( 30 ), null, out _ )
				&& skewed.Doc.ToJson() == skewedBefore,
				"skewed axis should fail reads and sets and leave the document untouched" );
		} );

		// Frames off the shared line are a conflict; nothing moves.
		Check( "frames off the shared line are a conflict and nothing moves", () =>
		{
			var offLine = Setup( Xform.Identity, Xform.Identity,
				frameB: AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0.5f, 0, 0 ) ) ) );
			var offLineBefore = offLine.Doc.ToJson();
			Require( !AssemblyHinge.TryGetHingeAngle( offLine.Doc, offLine.Joint, out _, out var offLineWhy )
				&& offLineWhy != null && offLineWhy.Contains( "off the shared hinge axis", StringComparison.Ordinal )
				&& !AssemblyHinge.TrySetHingeAngle( offLine.Doc, offLine.Joint, Deg( 30 ), null, out _ )
				&& offLine.Doc.ToJson() == offLineBefore,
				"off-line frames should fail reads and sets and leave the document untouched" );
		} );

		// An offset along the axis is the same line: allowed, and kept while turning.
		Check( "along-axis offset turns to the requested angle", () =>
		{
			var along = Setup( Xform.Identity, Xform.Identity,
				frameB: AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 0, 2 ) ) ) );
			Require( Near( Angle( along.Doc, along.Joint ), 0f )
				&& AssemblyHinge.TrySetHingeAngle( along.Doc, along.Joint, Deg( 60 ), null, out _ )
				&& Near( Angle( along.Doc, along.Joint ), Deg( 60 ) ),
				"along-axis offset should start at 0 and turn to 60" );
		} );
		Check( "along-axis offset is preserved after turning", () =>
		{
			var along = Setup( Xform.Identity, Xform.Identity,
				frameB: AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 0, 2 ) ) ) );
			Require( AssemblyHinge.TrySetHingeAngle( along.Doc, along.Joint, Deg( 60 ), null, out _ ),
				"turn should succeed" );
			var alongWorldA = along.A.Transform.ToXform() * along.Joint.FrameA.ToXform();
			var alongWorldB = along.B.Transform.ToXform() * along.Joint.FrameB.ToXform();
			Require( ( alongWorldB.Origin - alongWorldA.Origin ).AlmostEquals( new Vec3( 0, 0, 2 ) ),
				"world frame origins should still differ by (0,0,2) after turning" );
		} );

		// Chain motion: turning the first hinge carries the second hinge's part with it,
		// and the second hinge keeps its own angle.
		Check( "chain motion carries the next part and keeps the second hinge angle", () =>
		{
			var chainDoc = new AssemblyDocument();
			chainDoc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Bracket",
				SourceKind = "effigy-file", SourceReference = "parts/bracket.effigy#body-1",
			} );
			var chainA = Occ( "Chain A", Xform.Identity, chainDoc.Definitions[0].Id );
			var chainB = Occ( "Chain B", Xform.Identity, chainDoc.Definitions[0].Id );
			var chainC = Occ( "Chain C", Xform.Translate( new Vec3( 2, 0, 0 ) ), chainDoc.Definitions[0].Id );
			chainDoc.Occurrences.Add( chainA );
			chainDoc.Occurrences.Add( chainB );
			chainDoc.Occurrences.Add( chainC );
			var chainFirst = Hinge( chainA, chainB );
			// The second hinge's frames meet at one point: FrameA sits 2 in along B's X (where
			// C's origin is), FrameB is C's origin. Identity frames would put the two axes on
			// different parallel lines, which is not one hinge.
			var chainSecond = Hinge( chainB, chainC,
				frameA: AssemblyTransform.FromXform( Xform.Translate( new Vec3( 2, 0, 0 ) ) ) );
			chainDoc.Joints.Add( chainFirst );
			chainDoc.Joints.Add( chainSecond );
			Require( AssemblyHinge.TrySetHingeAngle( chainDoc, chainFirst, Deg( 30 ), null, out _ )
				&& Near( Angle( chainDoc, chainFirst ), Deg( 30 ) )
				&& Near( Angle( chainDoc, chainSecond ), 0f )
				&& chainC.Transform.ToXform().Origin.AlmostEquals( new Vec3( 2 * MathF.Cos( Deg( 30 ) ), 2 * MathF.Sin( Deg( 30 ) ), 0 ) ),
				"turning the first hinge should carry C to (2cos30, 2sin30, 0) and keep the second angle at 0" );
		} );

		// A grounded part downstream blocks the move; nothing moves.
		Check( "grounded downstream part blocks the move and nothing moves", () =>
		{
			var chainDoc = new AssemblyDocument();
			chainDoc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Bracket",
				SourceKind = "effigy-file", SourceReference = "parts/bracket.effigy#body-1",
			} );
			var chainA = Occ( "Chain A", Xform.Identity, chainDoc.Definitions[0].Id );
			var chainB = Occ( "Chain B", Xform.Identity, chainDoc.Definitions[0].Id );
			var chainC = Occ( "Chain C", Xform.Translate( new Vec3( 2, 0, 0 ) ), chainDoc.Definitions[0].Id );
			chainDoc.Occurrences.Add( chainA );
			chainDoc.Occurrences.Add( chainB );
			chainDoc.Occurrences.Add( chainC );
			var chainFirst = Hinge( chainA, chainB );
			var chainSecond = Hinge( chainB, chainC,
				frameA: AssemblyTransform.FromXform( Xform.Translate( new Vec3( 2, 0, 0 ) ) ) );
			chainDoc.Joints.Add( chainFirst );
			chainDoc.Joints.Add( chainSecond );
			Require( AssemblyHinge.TrySetHingeAngle( chainDoc, chainFirst, Deg( 30 ), null, out _ ),
				"setup turn should succeed" );
			Require( AssemblyGrounding.SetGrounded( chainDoc, chainC.Id, true ), "grounding C should stick" );
			var chainBefore = chainDoc.ToJson();
			Require( !AssemblyHinge.TrySetHingeAngle( chainDoc, chainFirst, Deg( 60 ),
					id => AssemblyGrounding.IsGrounded( chainDoc, id ), out var groundedWhy )
				&& groundedWhy != null && groundedWhy.Contains( "grounded", StringComparison.Ordinal )
				&& chainDoc.ToJson() == chainBefore,
				"grounded C should block the move and leave the document untouched" );
		} );

		// A grounded B blocks the move outright.
		Check( "grounded B blocks the move and nothing moves", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity );
			Require( AssemblyGrounding.SetGrounded( setup.Doc, setup.B.Id, true ), "grounding B should stick" );
			var before = setup.Doc.ToJson();
			Require( !AssemblyHinge.TrySetHingeAngle( setup.Doc, setup.Joint, Deg( 60 ),
					id => AssemblyGrounding.IsGrounded( setup.Doc, id ), out var why )
				&& why != null && why.Contains( "grounded", StringComparison.Ordinal )
				&& setup.Doc.ToJson() == before,
				"grounded B should block the move and leave the document untouched" );
		} );

		// A closed loop back to the root is refused, not solved approximately. The loop
		// joint is a real hinge: its A frame sits -2 in along C's X, back at A's origin.
		Check( "closed loop back to the root is refused and nothing moves", () =>
		{
			var chainDoc = new AssemblyDocument();
			chainDoc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Bracket",
				SourceKind = "effigy-file", SourceReference = "parts/bracket.effigy#body-1",
			} );
			var chainA = Occ( "Chain A", Xform.Identity, chainDoc.Definitions[0].Id );
			var chainB = Occ( "Chain B", Xform.Identity, chainDoc.Definitions[0].Id );
			var chainC = Occ( "Chain C", Xform.Translate( new Vec3( 2, 0, 0 ) ), chainDoc.Definitions[0].Id );
			chainDoc.Occurrences.Add( chainA );
			chainDoc.Occurrences.Add( chainB );
			chainDoc.Occurrences.Add( chainC );
			var chainFirst = Hinge( chainA, chainB );
			var chainSecond = Hinge( chainB, chainC,
				frameA: AssemblyTransform.FromXform( Xform.Translate( new Vec3( 2, 0, 0 ) ) ) );
			chainDoc.Joints.Add( chainFirst );
			chainDoc.Joints.Add( chainSecond );
			Require( AssemblyHinge.TrySetHingeAngle( chainDoc, chainFirst, Deg( 30 ), null, out _ ),
				"setup turn should succeed" );
			chainDoc.Joints.Add( Hinge( chainC, chainA,
				frameA: AssemblyTransform.FromXform( Xform.Translate( new Vec3( -2, 0, 0 ) ) ) ) );
			var loopBefore = chainDoc.ToJson();
			Require( !AssemblyHinge.TrySetHingeAngle( chainDoc, chainFirst, Deg( 60 ), null, out var loopWhy )
				&& loopWhy != null && loopWhy.Contains( "closed loop", StringComparison.Ordinal )
				&& chainDoc.ToJson() == loopBefore,
				"closed loop should be refused and leave the document untouched" );
		} );

		// A fixed joint is not a hinge.
		Check( "a fixed joint is refused as a hinge", () =>
		{
			var fixedDoc = new AssemblyDocument();
			fixedDoc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Bracket",
				SourceKind = "effigy-file", SourceReference = "parts/bracket.effigy#body-1",
			} );
			var fixedA = Occ( "Fixed A", Xform.Identity, fixedDoc.Definitions[0].Id );
			var fixedB = Occ( "Fixed B", Xform.Identity, fixedDoc.Definitions[0].Id );
			fixedDoc.Occurrences.Add( fixedA );
			fixedDoc.Occurrences.Add( fixedB );
			var fixedJoint = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = fixedA.Id, OccurrenceB = fixedB.Id,
				FrameA = new AssemblyTransform(), FrameB = new AssemblyTransform(),
			};
			fixedDoc.Joints.Add( fixedJoint );
			Require( !AssemblyHinge.TryGetHingeAngle( fixedDoc, fixedJoint, out _, out var fixedWhy )
				&& fixedWhy != null && fixedWhy.Contains( "not a hinge", StringComparison.Ordinal ),
				"a fixed joint should be refused with 'not a hinge'" );
		} );

		// A non-finite angle is rejected and moves nothing.
		Check( "a non-finite angle is rejected and moves nothing", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity );
			var before = setup.Doc.ToJson();
			Require( !AssemblyHinge.TrySetHingeAngle( setup.Doc, setup.Joint, float.NaN, null, out var why )
				&& why != null && why.Contains( "finite", StringComparison.Ordinal )
				&& setup.Doc.ToJson() == before,
				"NaN angle should be refused and leave the document untouched" );
		} );

		// Undo and redo restore the angle; each set is one discrete undo step.
		Check( "hinge angle applies as one undo step", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			var history = new EditHistory();
			Require( AssemblyHingeService.SetAngle( setup.Doc, history, setup.Joint.Id, Deg( 45 ), out _ )
				&& Near( Angle( setup.Doc, setup.Joint ), Deg( 45 ) )
				&& history.UndoCount == 1,
				"one set should be one undo entry and land at 45" );
		} );
		Check( "undo restores the hinge angle", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			var history = new EditHistory();
			Require( AssemblyHingeService.SetAngle( setup.Doc, history, setup.Joint.Id, Deg( 45 ), out _ ), "set should succeed" );
			history.Undo();
			var undone = setup.Doc.Joints.Single( j => j.Id == setup.Joint.Id );
			Require( Near( Angle( setup.Doc, undone ), 0f ), "undo should restore angle 0" );
		} );
		Check( "redo reapplies the hinge angle", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			var history = new EditHistory();
			Require( AssemblyHingeService.SetAngle( setup.Doc, history, setup.Joint.Id, Deg( 45 ), out _ ), "set should succeed" );
			history.Undo();
			history.Redo();
			var redone = setup.Doc.Joints.Single( j => j.Id == setup.Joint.Id );
			Require( Near( Angle( setup.Doc, redone ), Deg( 45 ) ), "redo should reapply angle 45" );
		} );
		Check( "a refused set adds no undo step and changes nothing", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity );
			var history = new EditHistory();
			Require( AssemblyGrounding.SetGrounded( setup.Doc, setup.B.Id, true ), "grounding B should stick" );
			var before = setup.Doc.ToJson();
			Require( !AssemblyHingeService.SetAngle( setup.Doc, history, setup.Joint.Id, Deg( 60 ), out var why )
				&& why != null && why.Contains( "grounded", StringComparison.Ordinal )
				&& history.UndoCount == 0
				&& setup.Doc.ToJson() == before,
				"refused set should add no history and leave the document untouched" );
		} );
		Check( "a no-op set adds no undo step", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity );
			var history = new EditHistory();
			Require( AssemblyHingeService.SetAngle( setup.Doc, history, setup.Joint.Id, 0f, out _ )
				&& history.UndoCount == 0,
				"setting the angle it already has should add no history" );
		} );

		// Service CreateHinge adds the joint as one undo step; undo removes it.
		Check( "service CreateHinge adds one joint as one undo step", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity );
			var history = new EditHistory();
			Require( AssemblyHingeService.CreateHinge( setup.Doc, history, setup.A.Id, setup.B.Id,
					new AssemblyTransform(), new AssemblyTransform(), Deg( -45 ), Deg( 45 ),
					out var joint, out _ )
				&& joint != null && joint.Kind == "hinge" && joint.Id.Length == 32
				&& setup.Doc.Joints.Count == 2 && history.UndoCount == 1,
				"service create should add one hinge joint and one undo entry" );
			history.Undo();
			Require( setup.Doc.Joints.Count == 1, "undo should remove the created joint" );
			history.Redo();
			Require( setup.Doc.Joints.Count == 2, "redo should restore the created joint" );
		} );

		// Service SetLimits rejects an inverted pair, keeps the old limits, adds no entry.
		Check( "service SetLimits rejects inverted limits and keeps the old ones", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			var history = new EditHistory();
			Require( !AssemblyHingeService.SetLimits( setup.Doc, history, setup.Joint.Id, Deg( 90 ), Deg( -90 ), out var why )
				&& why != null && why.Contains( "cannot exceed", StringComparison.Ordinal )
				&& setup.Joint.Minimum == Deg( -90 ) && setup.Joint.Maximum == Deg( 90 )
				&& history.UndoCount == 0,
				"inverted service limits should be refused with old limits intact and no history" );
			Require( AssemblyHingeService.SetLimits( setup.Doc, history, setup.Joint.Id, Deg( -30 ), Deg( 30 ), out _ )
				&& setup.Joint.Minimum == Deg( -30 ) && setup.Joint.Maximum == Deg( 30 )
				&& history.UndoCount == 1,
				"valid service limits should apply as one undo step" );
		} );

		// Save and reopen keep the limits and the angle.
		Check( "save and reopen keep hinge limits, angle and validation", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			Require( AssemblyHinge.TrySetHingeAngle( setup.Doc, setup.Joint, Deg( 45 ), null, out _ ),
				"hinge angle applies before save" );
			var text = setup.Doc.ToJson();
			var restored = AssemblyDocument.FromJson( text );
			var restoredJoint = restored.Joints.Single();
			Require( restoredJoint.Minimum == Deg( -90 ) && restoredJoint.Maximum == Deg( 90 )
				&& Near( Angle( restored, restoredJoint ), Deg( 45 ) )
				&& restored.Validate().Count == 0
				&& AssemblyHinge.ValidateHinge( restored, restoredJoint ).Count == 0
				&& restored.ToJson() == text,
				"save/reopen should keep limits, angle, clean validation and byte-identical text" );
		} );

		// Limit text reads in degrees; storage stays in radians.
		Check( "limit text reads in degrees", () =>
		{
			Require( AssemblyHinge.FormatAngle( 0f ) == "0°"
				&& AssemblyHinge.FormatAngle( MathF.PI / 2 ) == "90°"
				&& AssemblyHinge.FormatAngle( -MathF.PI / 4 ) == "-45°"
				&& AssemblyHinge.FormatAngle( Deg( 0.5f ) ) == "0.5°"
				&& AssemblyHinge.LimitText( null, null ) == "No limits"
				&& AssemblyHinge.LimitText( Deg( -90 ), Deg( 90 ) ) == "-90° to 90°"
				&& AssemblyHinge.LimitText( Deg( -90 ), null ) == "Min -90°"
				&& AssemblyHinge.LimitText( null, Deg( 90 ) ) == "Max 90°",
				"FormatAngle/LimitText should read in degrees" );
		} );

		// Hinge validation flags an angle outside its limits and a flipped axis.
		Check( "validation flags a hinge angle outside its limits", () =>
		{
			var outside = Setup( Xform.Identity, Xform.Rotate( new Vec3( 0, 0, 1 ), Deg( 45 ) ) );
			Require( AssemblyHinge.TrySetLimits( outside.Joint, Deg( -30 ), Deg( 30 ), out _ )
				&& AssemblyHinge.ValidateHinge( outside.Doc, outside.Joint ).Any( i => i.Code == "hinge-angle-outside-limits" ),
				"45 degrees with ±30 limits should be flagged" );
		} );
		Check( "validation reports a flipped hinge axis by code", () =>
		{
			var flipped = Setup( Xform.Identity, Xform.Identity,
				frameB: AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI ) ) );
			Require( AssemblyHinge.ValidateHinge( flipped.Doc, flipped.Joint ).Any( i => i.Code == "hinge-axis-flipped" ),
				"flipped axis should be reported as hinge-axis-flipped" );
		} );
		Check( "a healthy hinge validates clean", () =>
		{
			var zero = Setup( Xform.Identity, Xform.Identity );
			Require( AssemblyHinge.ValidateHinge( zero.Doc, zero.Joint ).Count == 0,
				"identity hinge should have no hinge issues" );
		} );
		Check( "ValidateHinge reports a missing joint and a wrong kind", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity );
			Require( AssemblyHinge.ValidateHinge( setup.Doc, null ).Any( i => i.Code == "invalid-joint" ),
				"null joint should be invalid-joint" );
			var fixedJoint = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = setup.A.Id, OccurrenceB = setup.B.Id,
				FrameA = new AssemblyTransform(), FrameB = new AssemblyTransform(),
			};
			Require( AssemblyHinge.ValidateHinge( setup.Doc, fixedJoint ).Any( i => i.Code == "not-a-hinge" ),
				"fixed joint should be not-a-hinge" );
		} );

		// Degree fields: empty means no limit, garbage is rejected.
		Check( "TryParseDegrees accepts empty and numbers and rejects the rest", () =>
		{
			Require( AssemblyHingeService.TryParseDegrees( "", out var noDeg ) && noDeg == null,
				"empty degrees should mean no limit" );
			Require( AssemblyHingeService.TryParseDegrees( "90", out var ninety ) && ninety == 90f,
				"degrees should parse as a number" );
			Require( AssemblyHingeService.TryParseDegrees( "-45.5", out var negDeg ) && negDeg == -45.5f,
				"negative degrees should parse" );
			Require( !AssemblyHingeService.TryParseDegrees( "abc", out _ ), "non-numeric degrees should be rejected" );
			Require( !AssemblyHingeService.TryParseDegrees( "NaN", out _ ), "NaN degrees should be rejected" );
			Require( !AssemblyHingeService.TryParseDegrees( "Infinity", out _ ), "infinite degrees should be rejected" );
		} );

		// SetAngleDegrees end to end: 90 degrees lands at PI/2 stored radians.
		Check( "SetAngleDegrees sets 90 degrees as PI over 2 radians", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity );
			var history = new EditHistory();
			Require( AssemblyHingeService.SetAngleDegrees( setup.Doc, history, setup.Joint.Id, 90f, out var message )
				&& Near( Angle( setup.Doc, setup.Joint ), MathF.PI / 2 )
				&& message != null && message.Contains( "90°", StringComparison.Ordinal )
				&& history.UndoCount == 1,
				"90 degrees should store PI/2, report 90 degrees and add one undo entry" );
		} );
		Check( "SetAngleDegrees reports the clamped angle when the limit bites", () =>
		{
			var setup = Setup( Xform.Identity, Xform.Identity, minimum: Deg( -90 ), maximum: Deg( 90 ) );
			var history = new EditHistory();
			Require( AssemblyHingeService.SetAngleDegrees( setup.Doc, history, setup.Joint.Id, 120f, out var message )
				&& Near( Angle( setup.Doc, setup.Joint ), Deg( 90 ) )
				&& message != null && message.Contains( "90°", StringComparison.Ordinal ),
				"120 degrees with a 90 limit should land at 90 and say so" );
		} );

		// Sheet constants are data only: the hinge section lives beside Fixed.
		Check( "hinge sheet constants name the hinge section", () =>
		{
			Require( AssemblyHingeSheet.SectionTitle == "Hinge"
				&& AssemblyHingeSheet.KindSegmentHint == "Fixed | Hinge"
				&& AssemblyHingeSheet.PartALabel == "Part A (holds still)"
				&& AssemblyHingeSheet.PartBLabel == "Part B (rotates)"
				&& AssemblyHingeSheet.AtLimitText == "At limit",
				"sheet constants should carry the hinge labels" );
		} );
	}
}
