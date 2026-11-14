using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS10: reversible motion previews and saved poses.
// Mirrors Mobile app/AssemblyMotionChecks.cs (non-UI assertions) in Desktop
// form, plus the Desktop additions: EditHistory EndSession contracts (the
// Desktop mapping of Mobile's one-CadDoc-checkpoint session), saved poses
// (no Mobile counterpart), degraded-mode budget, and Sweep no-drift.
// Checks compare solved origins, angles and stored positions, never only a
// success flag. Undo/redo re-fetch records from the document after each step:
// AssemblyHistory restores snapshots by replacing the document's collections,
// so a pre-undo object reference goes stale.
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyMotionTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-motion", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float a, float b, float eps, string what )
		{
			if ( MathF.Abs( a - b ) > eps ) throw new Exception( $"{what}: expected {b}, got {a}" );
		}
		void NearOrigin( AssemblyDocument doc, string id, Vec3 expected, string what, float eps = 1e-4f )
		{
			var origin = doc.Occurrences.Single( o => o.Id == id ).Transform.ToXform().Origin;
			if ( !origin.AlmostEquals( expected, eps ) )
				throw new Exception( $"{what}: origin of {id}: expected {expected}, got {origin}" );
		}
		AssemblyJoint JointOf( AssemblyDocument doc, string id ) =>
			doc.Joints.Single( j => j.Id == id );

		// Fixture: A (anchor, identity) -hinge- B (5,0,0) -fixed- C (5,0,2),
		// plus D (-3,0,0) fixed to A. The hinge pin is the world Z axis through
		// the origin: FrameA is identity, FrameB sits at (-5,0,0) in B-local so
		// both frame origins lie on the axis. Hinge limits -90..90 degrees.
		(AssemblyDocument Doc, AssemblyJoint Hinge, string A, string B, string C, string D) HingeFixture()
		{
			var doc = new AssemblyDocument();
			doc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Bracket",
				SourceKind = "effigy-file", SourceReference = "parts/bracket.effigy#body-1",
			} );
			var def = doc.Definitions[0].Id;
			AssemblyOccurrence Occ( string name, Xform t ) => new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = def, Name = name,
				Transform = AssemblyTransform.FromXform( t ),
			};
			var a = Occ( "Base", Xform.Identity );
			var b = Occ( "Arm", Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			var c = Occ( "Tool", Xform.Translate( new Vec3( 5, 0, 2 ) ) );
			var d = Occ( "Plate", Xform.Translate( new Vec3( -3, 0, 0 ) ) );
			doc.Occurrences.AddRange( new[] { a, b, c, d } );
			var hinge = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "hinge", OccurrenceA = a.Id, OccurrenceB = b.Id,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Translate( new Vec3( -5, 0, 0 ) ) ),
				Minimum = -MathF.PI / 2, Maximum = MathF.PI / 2,
			};
			doc.Joints.Add( hinge );
			doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = b.Id, OccurrenceB = c.Id,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Identity ),
			} );
			doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = a.Id, OccurrenceB = d.Id,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Identity ),
			} );
			return (doc, hinge, a.Id, b.Id, c.Id, d.Id);
		}

		// Fixture: Rail (anchor, identity) -slider- Carriage at (0,0,1), limits [-2, 4] in.
		(AssemblyDocument Doc, AssemblyJoint Slider, string A, string B) SliderFixture()
		{
			var doc = new AssemblyDocument();
			doc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Rail",
				SourceKind = "effigy-file", SourceReference = "parts/rail.effigy#body-1",
			} );
			var def = doc.Definitions[0].Id;
			var a = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def, Name = "Rail", Transform = AssemblyTransform.FromXform( Xform.Identity ) };
			var b = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def, Name = "Carriage", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 0, 1 ) ) ) };
			doc.Occurrences.AddRange( new[] { a, b } );
			var slider = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "slider", OccurrenceA = a.Id, OccurrenceB = b.Id,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Identity ),
				Minimum = -2f, Maximum = 4f,
			};
			doc.Joints.Add( slider );
			return (doc, slider, a.Id, b.Id);
		}

		Check( "hinge is the one movable joint; fixed joints are not draggable", () =>
		{
			var (doc, hinge, _, _, _, _) = HingeFixture();
			var movable = AssemblyMotionSession.MovableJoints( doc );
			Require( movable.Count == 1 && movable[0] == hinge, "expected exactly the hinge to be movable" );
			var fixedJoint = doc.Joints.Single( j => j.Kind == "fixed" );
			Require( ( AssemblyMotionSession.LockedReason( doc, fixedJoint ) ?? "" ).Contains( "cannot be dragged" ),
				"fixed joint must explain it cannot be dragged" );
		} );

		Check( "hinge session begins at rest angle 0; moving set is B and C", () =>
		{
			var (doc, hinge, aId, bId, cId, dId) = HingeFixture();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge ) && session.Problem == null, "hinge session begins" );
			Near( session.Value, 0f, 1e-6f, "hinge value starts at its rest angle" );
			Near( session.RestValue, 0f, 1e-6f, "hinge rest value is 0 (frames coincident)" );
			Require( session.MovingOccurrenceIds.Count == 2
				&& session.MovingOccurrenceIds.Contains( bId ) && session.MovingOccurrenceIds.Contains( cId )
				&& !session.MovingOccurrenceIds.Contains( aId ) && !session.MovingOccurrenceIds.Contains( dId ),
				"moving set is B and its fixed chain C; anchor A and fixed-to-anchor D stay" );
			Require( !session.IsDirty, "value starts at rest, not dirty" );
			Require( session.IsActive && session.IsHinge, "session is active and knows it is a hinge" );
		} );

		Check( "preview value alone does not move the document; apply rotates B 45 and carries C; RestoreRest puts the rest pose back", () =>
		{
			var (doc, hinge, aId, bId, cId, dId) = HingeFixture();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge ), "hinge session begins" );
			session.SetValue( MathF.PI / 4 );
			NearOrigin( doc, bId, new Vec3( 5, 0, 0 ), "preview value alone does not move the document" );
			Require( session.ApplyPreview(), "hinge apply succeeds" );
			NearOrigin( doc, bId, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 0 ),
				"45 degree hinge drag rotates B about the anchor axis" );
			NearOrigin( doc, cId, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 2 ),
				"chain part C is carried rigidly with B" );
			NearOrigin( doc, aId, Vec3.Zero, "anchor A does not move" );
			NearOrigin( doc, dId, new Vec3( -3, 0, 0 ), "grounded-to-anchor D does not move" );
			session.RestoreRest();
			NearOrigin( doc, bId, new Vec3( 5, 0, 0 ), "RestoreRest puts B back" );
			NearOrigin( doc, cId, new Vec3( 5, 0, 2 ), "RestoreRest puts C back" );
		} );

		Check( "hinge saved at 30 degrees reopens at 30; drag to 45 lands at 45; chain follows the 15 degree step", () =>
		{
			var (doc, hinge, _, bId, cId, _) = HingeFixture();
			var occB = doc.Occurrences.Single( o => o.Id == bId );
			occB.Transform = AssemblyTransform.FromXform(
				Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 6 ) * Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge ) && MathF.Abs( session.ToDisplay( session.Value ) - 30f ) < 0.01f,
				"hinge saved at 30 degrees reopens at 30 (absolute angle, not rest-is-zero)" );
			session.SetValue( MathF.PI / 4 );
			Require( session.ApplyPreview(), "drag from 30 to 45 applies" );
			NearOrigin( doc, bId, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 0 ),
				"drag from 30 to 45 degrees lands at 45" );
			NearOrigin( doc, cId, new Vec3( 5 * MathF.Cos( MathF.PI / 12 ), 5 * MathF.Sin( MathF.PI / 12 ), 2 ),
				"chain part follows the 15 degree step from its saved pose" );
		} );

		Check( "hinge outside its limits still opens and can be dragged back to 0", () =>
		{
			var (doc, hinge, _, bId, _, _) = HingeFixture();
			hinge.Minimum = -MathF.PI / 18;
			hinge.Maximum = MathF.PI / 18;
			var occB = doc.Occurrences.Single( o => o.Id == bId );
			occB.Transform = AssemblyTransform.FromXform(
				Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 6 ) * Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge ) && MathF.Abs( session.ToDisplay( session.Value ) - 30f ) < 0.01f,
				"hinge outside its limits still opens for dragging back inside" );
			session.SetValue( 0 );
			Require( session.ApplyPreview(), "dragging an out-of-limit hinge back to 0 applies" );
		} );

		Check( "hinge drag clamps at the maximum limit; holding past the limit stays exactly there", () =>
		{
			var (doc, hinge, _, bId, _, _) = HingeFixture();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge ), "hinge session begins" );
			session.SetValue( MathF.PI );
			Require( session.Value == MathF.PI / 2 && session.AtMaxLimit, "hinge drag clamps at the maximum limit" );
			session.ApplyPreview();
			NearOrigin( doc, bId, new Vec3( 0, 5, 0 ), "clamped preview is exactly the limit pose" );
			session.SetValue( MathF.PI );
			session.ApplyPreview();
			Require( session.Value == MathF.PI / 2, "holding past the limit stays at the limit (no jitter)" );
			NearOrigin( doc, bId, new Vec3( 0, 5, 0 ), "holding past the limit keeps the limit pose" );
		} );

		Check( "hinge drag clamps at the minimum limit; drag by pixels maps the limit range across 400 px", () =>
		{
			var (doc, hinge, _, _, _, _) = HingeFixture();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge ), "hinge session begins" );
			Require( session.SetValue( -MathF.PI ) == -MathF.PI / 2 && session.AtMinLimit,
				"hinge drag clamps at the minimum limit" );
			session.SetValue( -MathF.PI / 2 );
			Require( session.DragByPixels( 80 ) == session.Value
				&& MathF.Abs( session.ToDisplay( session.Value ) - ( -54f ) ) < 0.01f,
				"drag by pixels maps the limit range across 400 px (80 px is 36 degrees from -90)" );
			session.SetValue( 0 );
			session.RestoreRest();
		} );

		Check( "slider session begins at its measured position (1 in from geometry; no stored position)", () =>
		{
			var (doc, slider, _, _) = SliderFixture();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( slider ) && MathF.Abs( session.Value - 1f ) < 1e-6f,
				"slider session begins at its measured position (1 in from geometry; no stored position)" );
			Require( !session.IsHinge, "slider session knows it is not a hinge" );
			Require( session.ValueText() == "1.00 in", $"slider value text is inches, got '{session.ValueText()}'" );
			Require( session.LimitsText().Contains( "Travel" ), "slider limits read from AssemblySlider" );
		} );

		Check( "slider set to 2.5 in moves B along the joint axis; drag clamps at max travel; drag by pixels maps the travel range", () =>
		{
			var (doc, slider, _, bId) = SliderFixture();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( slider ), "slider session begins" );
			session.SetValue( 2.5f );
			Require( session.ApplyPreview(), "slider apply succeeds" );
			NearOrigin( doc, bId, new Vec3( 0, 0, 2.5f ), "slider set to 2.5 in moves B along the joint axis" );
			Require( session.SetValue( 9f ) == 4f && session.AtMaxLimit, "slider drag clamps at max travel" );
			session.SetValue( 1f );
			Require( session.DragByPixels( 80 ) == session.Value && MathF.Abs( session.Value - 2.2f ) < 0.01f,
				"slider drag by pixels maps the travel range across 400 px" );
			session.SetValue( 1f );
			session.RestoreRest();
			NearOrigin( doc, bId, new Vec3( 0, 0, 1 ), "RestoreRest puts the carriage back" );
		} );

		Check( "stale stored position does not move the drag start (geometry wins); agreeing stored position starts the drag there", () =>
		{
			var (doc, slider, _, bId) = SliderFixture();
			AssemblySlider.TrySetPosition( slider, 0.5f, out _ );
			var staleSession = new AssemblyMotionSession( doc );
			Require( staleSession.Begin( slider ) && MathF.Abs( staleSession.Value - 1f ) < 1e-6f,
				"stale stored position (0.5 in) does not move the drag start (geometry 1 in wins)" );
			var occB = doc.Occurrences.Single( o => o.Id == bId );
			occB.Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 0, 2 ) ) );
			AssemblySlider.TrySetPosition( slider, 2f, out _ );
			var agreeSession = new AssemblyMotionSession( doc );
			Require( agreeSession.Begin( slider ) && MathF.Abs( agreeSession.Value - 2f ) < 1e-6f,
				"stored position agreeing with geometry (2 in) starts the drag there" );
		} );

		Check( "applying a slider drag does not store the position per drag sample; EndSession commit stores it", () =>
		{
			var (doc, slider, _, bId) = SliderFixture();
			var history = new EditHistory();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( slider, history ), "slider session begins" );
			session.SetValue( 2.5f );
			Require( session.ApplyPreview(), "slider apply succeeds" );
			NearOrigin( doc, bId, new Vec3( 0, 0, 2.5f ), "slider drag moves B" );
			Require( !AssemblySlider.TryGetPosition( JointOf( doc, slider.Id ), out _ ),
				"stored position is still unset before commit (never synced per drag sample)" );
			Require( session.EndSession( commit: true ), "commit ends the session" );
			Require( AssemblySlider.TryGetPosition( JointOf( doc, slider.Id ), out var stored )
				&& MathF.Abs( stored - 2.5f ) < 1e-6f,
				"committing a slider drag stores the new position on the joint" );
		} );

		Check( "slider with misaligned axes is refused with an explanation", () =>
		{
			var (doc, slider, _, _) = SliderFixture();
			slider.FrameB = AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI / 2 ) );
			var session = new AssemblyMotionSession( doc );
			Require( !session.Begin( slider ) && ( session.Problem ?? "" ).Contains( "misaligned" ),
				"slider with misaligned axes is refused with an explanation" );
		} );

		Check( "slider with parts off its axis is refused with an explanation", () =>
		{
			var (doc, slider, _, bId) = SliderFixture();
			doc.Occurrences.Single( o => o.Id == bId ).Transform =
				AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0.5f, 0, 1 ) ) );
			var session = new AssemblyMotionSession( doc );
			Require( !session.Begin( slider ) && ( session.Problem ?? "" ).Contains( "off its axis" ),
				"slider with parts off its axis is refused with an explanation" );
		} );

		Check( "slider with B rotated relative to A is refused (solve would snap its orientation)", () =>
		{
			var (doc, slider, _, bId) = SliderFixture();
			doc.Occurrences.Single( o => o.Id == bId ).Transform =
				AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2 ) * Xform.Translate( new Vec3( 0, 0, 1 ) ) );
			var session = new AssemblyMotionSession( doc );
			Require( !session.Begin( slider ) && ( session.Problem ?? "" ).Contains( "rotated relative" ),
				"slider with B rotated relative to A is refused (solve would snap its orientation)" );
		} );

		Check( "locked joint (min == max) is not draggable and explains itself", () =>
		{
			var (doc, hinge, _, _, _, _) = HingeFixture();
			hinge.Minimum = 0f;
			hinge.Maximum = 0f;
			Require( AssemblyMotionSession.MovableJoints( doc ).Count == 0, "locked joint is not listed as movable" );
			Require( !new AssemblyMotionSession( doc ).Begin( hinge ), "locked joint cannot begin" );
			Require( ( AssemblyMotionSession.LockedReason( doc, hinge ) ?? "" ).Contains( "locked" ),
				"locked joint explains itself" );
		} );

		Check( "closed loop is refused with an explanation, not simulated", () =>
		{
			var (doc, hinge, aId, _, cId, _) = HingeFixture();
			doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = cId, OccurrenceB = aId,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Identity ),
			} );
			var session = new AssemblyMotionSession( doc );
			Require( !session.Begin( hinge ) && ( session.Problem ?? "" ).Contains( "loop" ),
				"closed loop is refused with an explanation, not simulated" );
		} );

		Check( "grounded chain part blocks the drag; grounded part outside the chain does not block and stays put; grounded moving part itself blocks the drag", () =>
		{
			var (doc, hinge, _, _, cId, _) = HingeFixture();
			AssemblyGrounding.SetGrounded( doc, cId, true );
			var groundSession = new AssemblyMotionSession( doc );
			Require( !groundSession.Begin( hinge ) && ( groundSession.Problem ?? "" ).Contains( "grounded" ),
				"grounded chain part blocks the drag" );

			var (doc2, hinge2, _, bId2, _, dId2) = HingeFixture();
			AssemblyGrounding.SetGrounded( doc2, dId2, true );
			var session2 = new AssemblyMotionSession( doc2 );
			Require( session2.Begin( hinge2 ), "grounded part outside the chain does not block the drag" );
			session2.SetValue( MathF.PI / 4 );
			Require( session2.ApplyPreview(), "drag applies with a grounded part outside the chain" );
			NearOrigin( doc2, dId2, new Vec3( -3, 0, 0 ), "grounded part outside the chain stays put" );
			NearOrigin( doc2, bId2, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 0 ),
				"the drag still moves B" );

			var (doc3, hinge3, _, bId3, _, _) = HingeFixture();
			AssemblyGrounding.SetGrounded( doc3, bId3, true );
			Require( !new AssemblyMotionSession( doc3 ).Begin( hinge3 ), "grounded moving part itself blocks the drag" );
		} );

		Check( "one Undo takes an applied drag back to rest; Redo restores the applied pose", () =>
		{
			var (doc, hinge, _, bId, _, _) = HingeFixture();
			var history = new EditHistory();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge, history ), "session begins with history" );
			session.SetValue( MathF.PI / 4 );
			session.ApplyPreview();
			Require( session.EndSession( commit: true ), "commit ends the session" );
			Require( history.UndoCount == 1, $"applied drag is exactly one undo step, got {history.UndoCount}" );
			history.Undo();
			NearOrigin( doc, bId, new Vec3( 5, 0, 0 ), "one Undo takes an applied drag back to rest" );
			history.Redo();
			NearOrigin( doc, bId, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 0 ),
				"Redo restores the applied pose" );
		} );

		Check( "cancel restores rest and leaves no undo step", () =>
		{
			var (doc, hinge, _, bId, _, _) = HingeFixture();
			var history = new EditHistory();
			var canUndoBefore = history.UndoCount;
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge, history ), "session begins with history" );
			session.SetValue( MathF.PI / 4 );
			session.ApplyPreview();
			Require( session.EndSession( commit: false ), "cancel ends the session" );
			NearOrigin( doc, bId, new Vec3( 5, 0, 0 ), "cancel restores rest" );
			Require( history.UndoCount == canUndoBefore, "cancel leaves no undo step" );
		} );

		Check( "unchanged commit registers no undo step", () =>
		{
			var (doc, hinge, _, bId, _, _) = HingeFixture();
			var history = new EditHistory();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge, history ), "session begins with history" );
			Require( session.EndSession( commit: true ), "unchanged commit ends the session" );
			Require( history.UndoCount == 0, "closing unchanged drops the session with no undo step" );
			NearOrigin( doc, bId, new Vec3( 5, 0, 0 ), "document is untouched" );
		} );

		Check( "applied pose survives save and reopen, and still validates", () =>
		{
			var (doc, hinge, _, bId, _, _) = HingeFixture();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge ), "session begins" );
			session.SetValue( MathF.PI / 4 );
			session.ApplyPreview();
			session.EndSession( commit: true );
			var reopened = AssemblyDocument.FromJson( doc.ToJson() );
			var origin = reopened.Occurrences.Single( o => o.Id == bId ).Transform.ToXform().Origin;
			Require( origin.AlmostEquals( new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 0 ), 1e-4f )
				&& reopened.Validate().Count == 0,
				"applied pose survives save and reopen, and still validates" );
		} );

		Check( "saved poses: save captures current values; apply restores them after a manual move", () =>
		{
			var (doc, hinge, _, bId, cId, _) = HingeFixture();
			var history = new EditHistory();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge, history ), "session begins" );
			session.SetValue( MathF.PI / 4 );
			session.ApplyPreview();
			Require( session.EndSession( commit: true ), "commit ends the session" );
			Require( AssemblySavedPoses.SavePose( doc, history, "Open", out var saveWhy ),
				$"save captures current values: {saveWhy}" );
			Require( AssemblySavedPoses.ListPoses( doc ).Count == 1
				&& AssemblySavedPoses.ListPoses( doc )[0] == "Open", "pose is listed" );
			// Manual move back to rest (both B and its chain C, to stay coherent).
			doc.Occurrences.Single( o => o.Id == bId ).Transform =
				AssemblyTransform.FromXform( Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			doc.Occurrences.Single( o => o.Id == cId ).Transform =
				AssemblyTransform.FromXform( Xform.Translate( new Vec3( 5, 0, 2 ) ) );
			Require( AssemblySavedPoses.ApplyPose( doc, history, "Open", out var applyWhy ),
				$"apply restores the saved pose: {applyWhy}" );
			NearOrigin( doc, bId, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 0 ),
				"apply puts B back at the saved 45 degrees" );
			NearOrigin( doc, cId, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 2 ),
				"apply carries the chain C back too" );
		} );

		Check( "saved poses: apply is atomic — a pose naming a missing joint fails and nothing moves", () =>
		{
			var (doc, hinge, _, bId, _, _) = HingeFixture();
			var history = new EditHistory();
			Require( AssemblySavedPoses.SavePose( doc, history, "Open", out _ ), "save the rest pose" );
			// SavePose registered a history entry, which restores snapshots by
			// replacing the document's collections: re-resolve before removing.
			doc.Joints.Remove( JointOf( doc, hinge.Id ) );
			var beforeJson = doc.ToJson();
			Require( !AssemblySavedPoses.ApplyPose( doc, history, "Open", out var why ) && why.Contains( "no longer" ),
				$"apply fails naming the missing joint: {why}" );
			Require( doc.ToJson() == beforeJson, "failed apply moves nothing" );
			NearOrigin( doc, bId, new Vec3( 5, 0, 0 ), "B is untouched by the failed apply" );
		} );

		Check( "saved poses: delete removes it; blank name refused; duplicate name replaces; poses survive round-trip", () =>
		{
			var (doc, hinge, _, bId, _, _) = HingeFixture();
			var history = new EditHistory();
			Require( !AssemblySavedPoses.SavePose( doc, history, "  ", out _ ), "blank pose name is refused" );
			Require( AssemblySavedPoses.SavePose( doc, history, "Open", out _ ), "save the rest pose" );
			// SavePose registered a history entry, which restores snapshots by
			// replacing the document's collections: re-resolve the joint.
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( JointOf( doc, hinge.Id ), history ), "session begins" );
			session.SetValue( MathF.PI / 4 );
			session.ApplyPreview();
			Require( session.EndSession( commit: true ), "commit the 45 degree pose" );
			Require( AssemblySavedPoses.SavePose( doc, history, "Open", out _ ), "re-save under the same name" );
			Require( AssemblySavedPoses.ListPoses( doc ).Count == 1, "duplicate name replaces, it does not duplicate" );
			Require( AssemblySavedPoses.ApplyPose( doc, history, "Open", out _ ), "apply the replaced pose" );
			NearOrigin( doc, bId, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 0 ),
				"the replaced pose holds the new value" );
			var reopened = AssemblyDocument.FromJson( doc.ToJson() );
			Require( AssemblySavedPoses.ListPoses( reopened ).Count == 1
				&& AssemblySavedPoses.ListPoses( reopened )[0] == "Open", "poses survive save and reopen" );
			Require( AssemblySavedPoses.DeletePose( doc, history, "Open" ), "delete removes the pose" );
			Require( AssemblySavedPoses.ListPoses( doc ).Count == 0, "pose list is empty after delete" );
			Require( !AssemblySavedPoses.DeletePose( doc, history, "Open" ), "deleting a missing pose is false" );
			Require( !AssemblySavedPoses.ApplyPose( doc, history, "Open", out _ ), "applying a deleted pose fails" );
		} );

		Check( "saved poses: slider pose restores travel and stored position", () =>
		{
			var (doc, slider, _, bId) = SliderFixture();
			var history = new EditHistory();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( slider, history ), "slider session begins" );
			session.SetValue( 2.5f );
			session.ApplyPreview();
			Require( session.EndSession( commit: true ), "commit stores the slider position" );
			Require( AssemblySavedPoses.SavePose( doc, history, "Out", out _ ), "save the slider pose" );
			doc.Occurrences.Single( o => o.Id == bId ).Transform =
				AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 0, 1 ) ) );
			Require( AssemblySavedPoses.ApplyPose( doc, history, "Out", out var why ),
				$"apply restores the slider pose: {why}" );
			NearOrigin( doc, bId, new Vec3( 0, 0, 2.5f ), "apply puts the carriage back at 2.5 in" );
			Require( AssemblySlider.TryGetPosition( JointOf( doc, slider.Id ), out var stored )
				&& MathF.Abs( stored - 2.5f ) < 1e-6f, "apply also restores the stored slider position" );
		} );

		Check( "degraded budget: 26 parts degraded; small assembly full; faces and memory degrade; message truncated", () =>
		{
			var big = AssemblyMotionBudget.Evaluate( 26, 24, 0, 400, lowMemoryWarning: false );
			Require( big.Level == AssemblyMotionBudgetLevel.Degraded && big.Message.Contains( "Large assembly" ),
				"the budget calls 26 parts degraded" );
			var small = AssemblyMotionBudget.Evaluate( 5, 4, 1000, 400, lowMemoryWarning: false );
			Require( small.Level == AssemblyMotionBudgetLevel.Full && small.Message == "",
				"the budget calls a small assembly full" );
			var faces = AssemblyMotionBudget.Evaluate( 5, 4, 200000, 400, lowMemoryWarning: false );
			Require( faces.Level == AssemblyMotionBudgetLevel.Degraded && faces.Message.Contains( "150k" ),
				"over 150k faces degrades" );
			var memory = AssemblyMotionBudget.Evaluate( 5, 4, 0, 1500, lowMemoryWarning: false );
			Require( memory.Level == AssemblyMotionBudgetLevel.Degraded && memory.Message.Contains( "Memory is low" ),
				"over 1200 MB RSS degrades" );
			var warning = AssemblyMotionBudget.Evaluate( 5, 4, 0, 400, lowMemoryWarning: true );
			Require( warning.Level == AssemblyMotionBudgetLevel.Degraded, "a low-memory warning degrades" );
			var huge = AssemblyMotionBudget.Evaluate( 99999, 99999, 999999999, 99999, lowMemoryWarning: true );
			Require( huge.Message.Length <= 240, $"budget message is truncated to 240 chars, got {huge.Message.Length}" );
		} );

		Check( "degraded session: drag moves the value but not the document; ApplyDeferred solves the deferred pose", () =>
		{
			// 26-part chain: link i at (i,0,0); link 0 -hinge- link 1, fixed after that.
			var doc = new AssemblyDocument();
			doc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Chain",
				SourceKind = "effigy-file", SourceReference = "parts/chain.effigy#body-1",
			} );
			var chainDef = doc.Definitions[0].Id;
			string prevId = null;
			string chainHingeB = null;
			AssemblyJoint chainHinge = null;
			for ( var i = 0; i < 26; i++ )
			{
				var occ = new AssemblyOccurrence
				{
					Id = AssemblyDocument.NewId(), DefinitionId = chainDef, Name = $"Link {i}",
					Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( i, 0, 0 ) ) ),
				};
				doc.Occurrences.Add( occ );
				if ( i == 1 ) chainHingeB = occ.Id;
				if ( prevId != null )
				{
					var isHinge = i == 1;
					var joint = new AssemblyJoint
					{
						Id = AssemblyDocument.NewId(), Kind = isHinge ? "hinge" : "fixed",
						OccurrenceA = prevId, OccurrenceB = occ.Id,
						FrameA = AssemblyTransform.FromXform( Xform.Identity ),
						FrameB = isHinge
							? AssemblyTransform.FromXform( Xform.Translate( new Vec3( -1, 0, 0 ) ) )
							: AssemblyTransform.FromXform( Xform.Identity ),
						Minimum = isHinge ? -MathF.PI / 2 : null,
						Maximum = isHinge ? MathF.PI / 2 : null,
					};
					doc.Joints.Add( joint );
					if ( isHinge ) chainHinge = joint;
				}
				prevId = occ.Id;
			}
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( chainHinge ), "Test motion opens on the 26-part assembly" );
			Require( session.IsDegraded, "26-part assembly runs degraded (solve on finger-up)" );
			Require( session.DegradedMessage.Contains( "Large assembly" ), "degraded session says why" );
			Require( session.DragByPixels( 80 ) == session.Value
				&& MathF.Abs( session.ToDisplay( session.Value ) - 36f ) < 0.5f,
				"degraded drag moves the value (80 px is 36 degrees)" );
			NearOrigin( doc, chainHingeB, new Vec3( 1, 0, 0 ),
				"degraded drag moves the value but not the document yet" );
			Require( session.ApplyDeferred(), "lifting the finger applies the deferred pose" );
			NearOrigin( doc, chainHingeB, new Vec3( MathF.Cos( MathF.PI / 5 ), MathF.Sin( MathF.PI / 5 ), 0 ),
				"lifting the finger solves the deferred pose (36 degrees)" );
		} );

		Check( "sweep: 100 sweeps return to rest with no drift and no history entries per sweep; a dirty commit adds exactly one entry", () =>
		{
			var (doc, hinge, _, bId, _, _) = HingeFixture();
			var history = new EditHistory();
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge, history ), "session begins" );
			var up = session.Sweep( 0, MathF.PI / 2, 50 );
			var down = session.Sweep( MathF.PI / 2, 0, 50 );
			Require( up.Count == 51 && down.Count == 51, $"sweeps apply every step, got {up.Count} and {down.Count}" );
			Near( up[^1], MathF.PI / 2, 1e-6f, "up sweep ends at 90 degrees" );
			Near( down[^1], 0f, 1e-6f, "down sweep ends back at 0" );
			NearOrigin( doc, bId, new Vec3( 5, 0, 0 ), "100 sweeps return to rest within 1e-4" );
			Require( history.UndoCount == 0, "sweeps add no history entries" );
			Require( session.EndSession( commit: true ), "commit at rest ends the session" );
			Require( history.UndoCount == 0, "committing at rest registers no entry" );
			// A dirty commit after the sweeps adds exactly one entry.
			var session2 = new AssemblyMotionSession( doc );
			Require( session2.Begin( hinge, history ), "second session begins" );
			session2.SetValue( MathF.PI / 4 );
			session2.ApplyPreview();
			Require( session2.EndSession( commit: true ), "dirty commit ends the session" );
			Require( history.UndoCount == 1, "one commit is exactly one undo step" );
			history.Undo();
			NearOrigin( doc, bId, new Vec3( 5, 0, 0 ), "undo takes the committed sweep pose back to rest" );
			history.Redo();
			NearOrigin( doc, bId, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 0 ),
				"redo restores the committed pose" );
		} );

		Check( "session guards: null joint, non-draggable kind, inactive session, display conversions", () =>
		{
			var (doc, hinge, _, _, _, _) = HingeFixture();
			var session = new AssemblyMotionSession( doc );
			Require( !session.Begin( null ) && session.Problem == "No joint selected.",
				"Begin with no joint fails with a plain message" );
			Require( ( AssemblyMotionSession.LockedReason( doc, null ) ?? "" ) == "No joint selected.",
				"LockedReason with no joint says so" );
			Require( AssemblyMotionSession.MovableJoints( null ).Count == 0, "MovableJoints on a null doc is empty" );
			Require( session.SetValue( 1f ) == 0f && !session.ApplyPreview()
				&& session.DragByPixels( 10f ) == 0f && !session.Reset() && !session.EndSession( commit: true ),
				"an inactive session does nothing" );
			Require( session.Begin( hinge ), "session begins" );
			Near( session.ToDisplay( MathF.PI / 2 ), 90f, 1e-4f, "hinge display converts radians to degrees" );
			Near( session.FromDisplay( 90f ), MathF.PI / 2, 1e-6f, "hinge display converts degrees back to radians" );
			session.SetValue( MathF.PI / 4 );
			Require( session.ValueText() == "45°", $"hinge value text is degrees, got '{session.ValueText()}'" );
			Require( session.LimitsText() == "-90° to 90°", $"hinge limits text, got '{session.LimitsText()}'" );
		} );

		Check( "switching joints restores the previous preview first", () =>
		{
			var (doc, hinge, aId, bId, _, dId) = HingeFixture();
			// A second hinge on D, pin also the world Z axis through the origin.
			var hinge2 = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "hinge", OccurrenceA = aId, OccurrenceB = dId,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 3, 0, 0 ) ) ),
				Minimum = -MathF.PI / 2, Maximum = MathF.PI / 2,
			};
			doc.Joints.Add( hinge2 );
			var session = new AssemblyMotionSession( doc );
			Require( session.Begin( hinge ), "first session begins" );
			session.SetValue( MathF.PI / 4 );
			Require( session.ApplyPreview(), "first drag applies" );
			NearOrigin( doc, bId, new Vec3( 5 * MathF.Cos( MathF.PI / 4 ), 5 * MathF.Sin( MathF.PI / 4 ), 0 ),
				"first drag is applied" );
			Require( session.Begin( hinge2 ), "switching to the second joint begins" );
			Require( session.Joint == hinge2, "the session now holds the second joint" );
			NearOrigin( doc, bId, new Vec3( 5, 0, 0 ), "switching joints restores the previous preview first" );
			Near( session.Value, 0f, 1e-6f, "the new session starts at the second joint's rest value" );
		} );
	}
}
