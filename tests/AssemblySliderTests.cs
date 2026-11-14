using System;
using System.Linq;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS09: slider joints with constrained travel.
// Mirrors Mobile app/AssemblySliderChecks.cs (22 checks) in Desktop form,
// plus service-layer checks (grounded-B refusal, CreateSlider/SetLimits).
// Checks compare solved origins, axes and stored positions, never only a
// success flag. The undo check re-fetches joint and occurrence from the
// document after each step: AssemblyHistory restores snapshots by replacing
// the document's collections, so a pre-undo object reference goes stale.
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblySliderTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-slider", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float a, float b, float eps, string what )
		{
			if ( MathF.Abs( a - b ) > eps ) throw new Exception( $"{what}: expected {b}, got {a}" );
		}

		(AssemblyOccurrence A, AssemblyOccurrence B, AssemblyJoint Joint) Make(
			AssemblyDocument doc = null,
			float? min = null, float? max = null,
			Xform? transformA = null, Xform? transformB = null,
			Xform? frameA = null, Xform? frameB = null, string kind = "slider" )
		{
			var definition = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Plate",
				SourceKind = "studio-body", SourceReference = "parts/plate.effigy#body-1",
			};
			var a = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = definition.Id, Name = "Plate A",
				Transform = AssemblyTransform.FromXform( transformA ?? Xform.Identity ),
			};
			var b = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = definition.Id, Name = "Plate B",
				Transform = AssemblyTransform.FromXform( transformB ?? Xform.Identity ),
			};
			var joint = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = kind, OccurrenceA = a.Id, OccurrenceB = b.Id,
				FrameA = AssemblyTransform.FromXform( frameA ?? Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( frameB ?? Xform.Identity ),
				Minimum = min, Maximum = max,
			};
			if ( doc != null )
			{
				doc.Definitions.Add( definition );
				doc.Occurrences.Add( a );
				doc.Occurrences.Add( b );
				doc.Joints.Add( joint );
			}
			return (a, b, joint);
		}

		AssemblyOccurrence Occ( AssemblyDocument doc, string id ) =>
			doc.Occurrences.Single( o => o.Id == id );
		AssemblyJoint Joint( AssemblyDocument doc, string id ) =>
			doc.Joints.Single( j => j.Id == id );

		// 1. Travel zero: the connector frames coincide.
		Check( "travel zero makes the connector frames coincide", () =>
		{
			var (a, b, j) = Make();
			var ok = AssemblySlider.TrySolve( j, a, b, 0f, out var solved, out var err, out _ );
			var worldB = solved * j.FrameB.ToXform();
			Require( ok, "solve at zero must succeed: " + err );
			Require( worldB.Origin.AlmostEquals( Vec3.Zero ), "world B origin must be (0,0,0), got " + worldB.Origin );
			Require( worldB.X.AlmostEquals( new Vec3( 1, 0, 0 ) ), "world B X must be (1,0,0)" );
			Require( worldB.Z.AlmostEquals( new Vec3( 0, 0, 1 ) ), "world B Z must be (0,0,1)" );
		} );

		// 2. Positive travel moves B along +Z only; orientation is preserved.
		Check( "positive travel moves B along the shared axis only", () =>
		{
			var (a, b, j) = Make();
			var ok = AssemblySlider.TrySolve( j, a, b, 2f, out var solved, out var err, out var clamped );
			Require( ok && !clamped, "solve at +2 must succeed unclamped: " + err );
			Require( solved.Origin.AlmostEquals( new Vec3( 0, 0, 2 ) ), "B origin must be (0,0,2), got " + solved.Origin );
			Require( solved.X.AlmostEquals( new Vec3( 1, 0, 0 ) ), "B X must stay (1,0,0)" );
			Require( solved.Y.AlmostEquals( new Vec3( 0, 1, 0 ) ), "B Y must stay (0,1,0)" );
			Require( solved.Z.AlmostEquals( new Vec3( 0, 0, 1 ) ), "B Z must stay (0,0,1)" );
		} );

		// 3. Negative travel moves B the opposite way.
		Check( "negative travel moves B the opposite way", () =>
		{
			var (a, b, j) = Make();
			var ok = AssemblySlider.TrySolve( j, a, b, -1.5f, out var solved, out var err, out _ );
			Require( ok, "solve at -1.5 must succeed: " + err );
			Require( solved.Origin.AlmostEquals( new Vec3( 0, 0, -1.5f ) ), "B origin must be (0,0,-1.5), got " + solved.Origin );
		} );

		// 4. Flipped axis: positive travel follows connector +Z, even when that points at world -Z.
		Check( "flipped axis direction: positive travel follows connector +Z", () =>
		{
			var flipped = Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI );
			var (a, b, j) = Make( frameA: flipped );
			var ok = AssemblySlider.TrySolve( j, a, b, 2f, out var solved, out var err, out _ );
			var worldB = solved * j.FrameB.ToXform();
			Require( ok, "solve on a flipped axis must succeed: " + err );
			Require( worldB.Origin.AlmostEquals( new Vec3( 0, 0, -2 ) ), "world B origin must be (0,0,-2), got " + worldB.Origin );
			Require( worldB.Z.AlmostEquals( new Vec3( 0, 0, -1 ) ), "world B Z must be (0,0,-1)" );
		} );

		// 5. Nonzero connector offsets are preserved; travel still lands on the axis.
		Check( "nonzero connector offsets are preserved", () =>
		{
			var (a, b, j) = Make(
				frameA: Xform.Translate( new Vec3( 1, 2, 3 ) ),
				frameB: Xform.Translate( new Vec3( -1, 0, 0.5f ) ) );
			var ok = AssemblySlider.TrySolve( j, a, b, 2f, out var solved, out var err, out _ );
			var worldB = solved * j.FrameB.ToXform();
			Require( ok, "solve with offsets must succeed: " + err );
			Require( worldB.Origin.AlmostEquals( new Vec3( 1, 2, 5 ) ), "world B origin must be (1,2,5), got " + worldB.Origin );
			Require( worldB.X.AlmostEquals( new Vec3( 1, 0, 0 ) ), "world B X must stay (1,0,0)" );
		} );

		// 6. Units: travel is document inches.
		Check( "travel is measured in document inches", () =>
		{
			var (a, b, j) = Make();
			var ok = AssemblySlider.TrySolve( j, a, b, 2.5f, out var solved, out var err, out _ );
			var unitsDoc = new AssemblyDocument();
			Require( ok, "solve at 2.5 must succeed: " + err );
			Require( solved.Origin.AlmostEquals( new Vec3( 0, 0, 2.5f ) ), "B origin must be (0,0,2.5), got " + solved.Origin );
			Require( unitsDoc.LengthUnit == "in", "document length unit must be inches" );
			Require( AssemblySlider.DescribeLimits( j ) == "No travel limits", "unlimited joint must describe no limits" );
		} );

		// 7. Travel clamps at the saved maximum.
		Check( "travel clamps at the saved maximum", () =>
		{
			var (a, b, j) = Make( min: -2, max: 4 );
			var ok = AssemblySlider.TrySolve( j, a, b, 10f, out var solved, out var err, out var clamped );
			Require( ok && clamped, "solve past the max must succeed clamped: " + err );
			Require( solved.Origin.AlmostEquals( new Vec3( 0, 0, 4 ) ), "B origin must clamp to (0,0,4), got " + solved.Origin );
		} );

		// 8. Travel clamps at the saved minimum.
		Check( "travel clamps at the saved minimum", () =>
		{
			var (a, b, j) = Make( min: -2, max: 4 );
			var ok = AssemblySlider.TrySolve( j, a, b, -9f, out var solved, out var err, out var clamped );
			Require( ok && clamped, "solve past the min must succeed clamped: " + err );
			Require( solved.Origin.AlmostEquals( new Vec3( 0, 0, -2 ) ), "B origin must clamp to (0,0,-2), got " + solved.Origin );
		} );

		// 9. Travel inside the limits is not clamped.
		Check( "travel inside the limits is not clamped", () =>
		{
			var (a, b, j) = Make( min: -2, max: 4 );
			var ok = AssemblySlider.TrySolve( j, a, b, 3f, out var solved, out var err, out var clamped );
			Require( ok && !clamped, "solve inside the limits must succeed unclamped: " + err );
			Require( solved.Origin.AlmostEquals( new Vec3( 0, 0, 3 ) ), "B origin must be (0,0,3), got " + solved.Origin );
		} );

		// 10. Inverted limits are rejected with a clear message.
		Check( "inverted limits are rejected with a clear message", () =>
		{
			var invDoc = new AssemblyDocument();
			var (_, _, jInv) = Make( invDoc, min: 4, max: -2 );
			var okInv = AssemblySlider.TrySolve( jInv, invDoc.Occurrences[0], invDoc.Occurrences[1], 0f,
				out _, out var errInv, out _ );
			var okInvSet = AssemblySlider.TrySetPosition( jInv, 0f, out var errInvSet );
			Require( !okInv && errInv != null && errInv.Contains( "minimum cannot exceed" ),
				"solve must fail naming the inverted limits: " + errInv );
			Require( !okInvSet && errInvSet != null && errInvSet.Contains( "minimum cannot exceed" ),
				"set must fail naming the inverted limits: " + errInvSet );
			Require( invDoc.Validate().Any( i => i.Code == "invalid-joint-limit" ),
				"document validation must report invalid-joint-limit" );
		} );

		// 11. Non-finite limits are rejected.
		Check( "non-finite limits are rejected", () =>
		{
			var (a, b, j) = Make( min: float.NaN, max: 4 );
			var ok = AssemblySlider.TrySolve( j, a, b, 0f, out _, out var err, out _ );
			Require( !ok && err != null && err.Contains( "finite" ), "NaN limit must fail naming finiteness: " + err );
		} );

		// 12. Limits and current position survive save and reopen (AssemblyDocument JSON round-trip;
		// the StudioDocument "effigy 3 / assemblyjson" text layer is AS01's, already covered there).
		Check( "slider limits and position survive save and reopen", () =>
		{
			var doc = new AssemblyDocument();
			var (_, _, jSave) = Make( doc, min: -2, max: 4 );
			Require( AssemblySlider.TrySetPosition( jSave, 1.5f, out var errSave ), "position must store: " + errSave );
			var restored = AssemblyDocument.FromJson( doc.ToJson() );
			var back = restored.Joints.Single( j => j.Id == jSave.Id );
			Require( back.Minimum == -2f && back.Maximum == 4f, "limits must survive the round-trip" );
			Require( AssemblySlider.GetPosition( back ) == 1.5f, "stored position must survive the round-trip" );
		} );

		// 13. Setting a position outside the limits keeps the last valid position.
		Check( "setting a position outside the limits keeps the last valid position", () =>
		{
			var (_, _, jKeep) = Make( min: -2, max: 4 );
			AssemblySlider.TrySetPosition( jKeep, 1f, out _ );
			var okKeep = AssemblySlider.TrySetPosition( jKeep, 5f, out var errKeep );
			Require( !okKeep && errKeep != null && errKeep.Contains( "outside the saved limits" ),
				"set past the max must fail naming the limits: " + errKeep );
			Require( AssemblySlider.GetPosition( jKeep ) == 1f, "last valid position 1 must survive" );
		} );

		// 14. A joint without a stored position starts at travel zero.
		Check( "a joint without a stored position starts at travel zero", () =>
		{
			var (a, b, j) = Make();
			var ok = AssemblySlider.TrySolveAtStoredPosition( j, a, b, out var solved, out var err, out _ );
			Require( !AssemblySlider.TryGetPosition( j, out _ ), "no position must be stored" );
			Require( AssemblySlider.GetPosition( j ) == 0f, "unstored position must read as 0" );
			Require( ok, "solve at the stored position must succeed: " + err );
			Require( solved.Origin.AlmostEquals( Vec3.Zero ), "stored-zero solve must put B at the origin" );
		} );

		// 15. Non-slider joints are rejected.
		Check( "non-slider joints are rejected", () =>
		{
			var (a, b, j) = Make( kind: "hinge" );
			var ok = AssemblySlider.TrySolve( j, a, b, 0f, out _, out var err, out _ );
			var okSet = AssemblySlider.TrySetPosition( j, 0f, out var errSet );
			Require( !ok && err != null && err.Contains( "not a slider" ), "hinge solve must fail: " + err );
			Require( !okSet && errSet != null && errSet.Contains( "not a slider" ), "hinge set must fail: " + errSet );
		} );

		// 16. Solving never moves the anchor occurrence, even on a tilted axis.
		Check( "solving never moves the anchor occurrence", () =>
		{
			var tiltA = Xform.Translate( new Vec3( 5, -3, 2 ) ) * Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI / 6 );
			var (a, b, j) = Make( transformA: tiltA );
			var beforeA = a.Transform.ToXform();
			var ok = AssemblySlider.TrySolve( j, a, b, 2f, out var solved, out var err, out _ );
			var worldB = solved * j.FrameB.ToXform();
			var expectAxis = new Vec3( 0, -MathF.Sin( MathF.PI / 6 ), MathF.Cos( MathF.PI / 6 ) );
			var expectOrigin = new Vec3( 5, -3, 2 ) + expectAxis * 2f;
			var afterA = a.Transform.ToXform();
			Require( ok, "solve on a tilted axis must succeed: " + err );
			Require( worldB.Origin.AlmostEquals( expectOrigin ), "world B origin must follow the tilted axis, got " + worldB.Origin );
			Require( worldB.Z.AlmostEquals( expectAxis ), "world B Z must be the tilted axis" );
			Require( afterA.Origin.AlmostEquals( beforeA.Origin ) && afterA.X.AlmostEquals( beforeA.X ),
				"anchor A must not move" );
		} );

		// 17. Chained sliders compose, and solving the second never moves the first.
		Check( "chained sliders compose without moving the middle occurrence", () =>
		{
			var (a, b, j) = Make();
			var okA = AssemblySlider.TrySolve( j, a, b, 2f, out var solved13, out var errA, out _ );
			var (a2, c, j2) = Make( frameA: Xform.Rotate( new Vec3( 0, 1, 0 ), MathF.PI / 2 ) );
			a2.Transform = AssemblyTransform.FromXform( solved13 );
			var okB = AssemblySlider.TrySolve( j2, a2, c, 3f, out var solved14, out var errB, out _ );
			var worldC = solved14 * j2.FrameB.ToXform();
			Require( okA && okB, "both chained solves must succeed: " + errA + errB );
			Require( worldC.Origin.AlmostEquals( new Vec3( 3, 0, 2 ) ), "world C must be (3,0,2), got " + worldC.Origin );
			Require( a2.Transform.ToXform().Origin.AlmostEquals( new Vec3( 0, 0, 2 ) ),
				"middle occurrence must stay at (0,0,2)" );
		} );

		// 18. A failed solve keeps the last valid pose and names the joint; a missing
		// occurrence reference fails with the joint named.
		Check( "a failed solve keeps the last valid pose and names the problem", () =>
		{
			var failDoc = new AssemblyDocument();
			var (_, fb, fj) = Make( failDoc, min: 4, max: -2 );
			fb.Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 0, 1 ) ) );
			var okFail = AssemblySlider.TrySolveInDocument( failDoc, fj.Id, 0f, out _, out var errFail, out _ );
			Require( !okFail && errFail != null && errFail.Contains( "minimum cannot exceed" ),
				"inverted-limit solve must fail naming the limits: " + errFail );
			Require( fb.Transform.ToXform().Origin.AlmostEquals( new Vec3( 0, 0, 1 ) ),
				"B must keep its last valid pose (0,0,1)" );
			failDoc.Occurrences.RemoveAt( 1 );
			var okMissing = AssemblySlider.TrySolveInDocument( failDoc, fj.Id, 0f, out _, out var errMissing, out _ );
			Require( !okMissing && errMissing != null && errMissing.Contains( fj.Id ) && errMissing.Contains( "missing occurrence" ),
				"missing occurrence must fail naming the joint: " + errMissing );
		} );

		// 19. Measurement reads travel back and flags off-axis drift.
		Check( "measurement reads the solved travel back", () =>
		{
			var (a, b, j) = Make();
			AssemblySlider.TrySolve( j, a, b, 2f, out var solved, out _, out _ );
			b.Transform = AssemblyTransform.FromXform( solved );
			var ok = AssemblySlider.TryMeasure( j, a, b, out var measured, out var offAxis, out var err );
			Require( ok, "measure must succeed: " + err );
			Near( measured, 2f, 1e-4f, "measured travel" );
			Require( offAxis < 1e-4f, "off-axis must be ~0 for a solved pose, got " + offAxis );
		} );

		Check( "measurement flags off-axis drift", () =>
		{
			var (a, b, j) = Make();
			b.Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0.5f, 0, 2 ) ) );
			var ok = AssemblySlider.TryMeasure( j, a, b, out var measured, out var offAxis, out _ );
			Require( ok, "measure of a drifted pose must succeed" );
			Near( measured, 2f, 1e-4f, "measured travel with drift" );
			Near( offAxis, 0.5f, 1e-4f, "off-axis drift" );
		} );

		// 20. Slider edits are undoable: position and pose both come back.
		Check( "slider edit undoes", () =>
		{
			var doc = new AssemblyDocument();
			var (_, b, joint) = Make( doc, min: -2, max: 4 );
			var history = new EditHistory();
			Require( AssemblySliderService.SetTravel( doc, history, joint.Id, 2f, out var err ),
				"set travel must succeed: " + err );
			Require( history.UndoCount == 1, "one edit must be one history entry" );
			Require( Occ( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( new Vec3( 0, 0, 2 ) ),
				"travel must apply to B" );
			Require( AssemblySlider.GetPosition( Joint( doc, joint.Id ) ) == 2f, "position 2 must store" );
			history.Undo();
			Require( AssemblySlider.GetPosition( Joint( doc, joint.Id ) ) == 0f, "undo must restore position 0" );
			Require( Occ( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( Vec3.Zero ),
				"undo must restore B to the origin" );
		} );

		Check( "slider edit redoes", () =>
		{
			var doc = new AssemblyDocument();
			var (_, b, joint) = Make( doc, min: -2, max: 4 );
			var history = new EditHistory();
			Require( AssemblySliderService.SetTravel( doc, history, joint.Id, 2f, out var err ),
				"set travel must succeed: " + err );
			history.Undo();
			history.Redo();
			Require( AssemblySlider.GetPosition( Joint( doc, joint.Id ) ) == 2f, "redo must restore position 2" );
			Require( Occ( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( new Vec3( 0, 0, 2 ) ),
				"redo must restore B to (0,0,2)" );
		} );

		// 21. A scaled anchor still travels in world inches.
		Check( "a scaled anchor still travels in world inches", () =>
		{
			var (a, b, j) = Make( transformA: Xform.Scale( new Vec3( 2, 2, 2 ) ) );
			var ok = AssemblySlider.TrySolve( j, a, b, 2f, out var solved, out var err, out _ );
			var worldB = solved * j.FrameB.ToXform();
			Require( ok, "solve on a scaled anchor must succeed: " + err );
			Require( worldB.Origin.AlmostEquals( new Vec3( 0, 0, 2 ) ),
				"world B origin must be (0,0,2) world inches, got " + worldB.Origin );
		} );

		// 22. Limit labels read plainly for the UI.
		Check( "limit labels read plainly", () =>
		{
			var (_, _, jL1) = Make( min: -2, max: 4 );
			var (_, _, jL2) = Make( min: -2 );
			var (_, _, jL3) = Make( max: 4 );
			var (_, _, jL4) = Make();
			Require( AssemblySlider.DescribeLimits( jL1 ) == "Travel -2 in to 4 in", "both limits must read '-2 in to 4 in'" );
			Require( AssemblySlider.DescribeLimits( jL2 ) == "Travel from -2 in", "min-only must read 'from -2 in'" );
			Require( AssemblySlider.DescribeLimits( jL3 ) == "Travel up to 4 in", "max-only must read 'up to 4 in'" );
			Require( AssemblySlider.DescribeLimits( jL4 ) == "No travel limits", "unlimited must read 'No travel limits'" );
		} );

		// Service: SetTravel with B grounded is refused and changes nothing.
		Check( "service refuses to slide a grounded occurrence", () =>
		{
			var doc = new AssemblyDocument();
			var (_, b, joint) = Make( doc, min: -2, max: 4 );
			var history = new EditHistory();
			Require( AssemblyGrounding.SetGrounded( doc, b.Id, true ), "grounding B must succeed" );
			var ok = AssemblySliderService.SetTravel( doc, history, joint.Id, 2f, out var err );
			Require( !ok && err != null && err.Contains( "grounded" ), "grounded B must be refused: " + err );
			Require( history.UndoCount == 0, "a refused edit must consume no undo step" );
			Require( AssemblySlider.GetPosition( Joint( doc, joint.Id ) ) == 0f, "position must stay 0" );
			Require( Occ( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( Vec3.Zero ), "B must not move" );
		} );

		// Service: a grounded anchor does not block sliding B.
		Check( "service slides B when only the anchor is grounded", () =>
		{
			var doc = new AssemblyDocument();
			var (a, b, joint) = Make( doc, min: -2, max: 4 );
			var history = new EditHistory();
			Require( AssemblyGrounding.SetGrounded( doc, a.Id, true ), "grounding A must succeed" );
			Require( AssemblySliderService.SetTravel( doc, history, joint.Id, 2f, out var err ),
				"grounded anchor must not block: " + err );
			Require( Occ( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( new Vec3( 0, 0, 2 ) ),
				"B must reach (0,0,2)" );
		} );

		// Service: CreateSlider validates and creates one undo step; SetLimits validates and applies.
		Check( "service creates a slider joint and sets its limits", () =>
		{
			var doc = new AssemblyDocument();
			var (a, b, _) = Make( doc );
			var history = new EditHistory();
			Require( AssemblySliderService.CreateSlider( doc, history, a.Id, b.Id,
				AssemblyTransform.FromXform( Xform.Identity ), AssemblyTransform.FromXform( Xform.Identity ),
				-2f, 4f, out var created, out var errCreate ), "create must succeed: " + errCreate );
			Require( created != null && created.Kind == "slider", "created joint must be a slider" );
			Require( history.UndoCount == 1, "create must be one history entry" );
			Require( AssemblySliderService.SetLimits( doc, history, created.Id, -1f, 3f, out var errLimits ),
				"set limits must succeed: " + errLimits );
			var after = Joint( doc, created.Id );
			Require( after.Minimum == -1f && after.Maximum == 3f, "limits must update to [-1, 3]" );
			Require( AssemblySlider.DescribeLimits( after ) == "Travel -1 in to 3 in", "limits must describe as '-1 in to 3 in'" );
			Require( !AssemblySliderService.SetLimits( doc, history, created.Id, 5f, -5f, out var errBad )
				&& errBad != null && errBad.Contains( "minimum cannot exceed" ),
				"inverted limits must be refused: " + errBad );
			Require( !AssemblySliderService.CreateSlider( doc, history, a.Id, a.Id,
				AssemblyTransform.FromXform( Xform.Identity ), AssemblyTransform.FromXform( Xform.Identity ),
				null, null, out _, out var errSame )
				&& errSame != null && errSame.Contains( "different occurrences" ),
				"self-joint create must be refused: " + errSame );
			Require( history.UndoCount == 2, "refused ops must add no history entries" );
		} );
	}
}
