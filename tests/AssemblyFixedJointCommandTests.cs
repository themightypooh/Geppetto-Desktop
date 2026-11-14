using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS05: transactional fixed joints and grounded roots.
// Ports Mobile app/AssemblyFixedJointChecks.cs checks 1-10 in Desktop form.
// Mobile check 11 is the Godot joint sheet (Apply/Cancel/Back buttons); there
// is no UI in this lane, so it is skipped. Desktop differences, all deliberate:
// operations take (AssemblyDocument, EditHistory) instead of a CadDoc, undo/redo
// go through EditHistory (void Undo/Redo, guarded by CanUndo/CanRedo), and
// save/reopen goes through AssemblyDocument.ToJson/FromJson instead of
// StudioDocument. Restores replace the document's collections in place, so
// every check re-resolves occurrences and joints by stable Id after an undo or
// redo. Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyFixedJointCommandTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-fixed-joints", name, test );
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

		bool Redo( EditHistory history )
		{
			if ( !history.CanRedo ) return false;
			history.Redo();
			return true;
		}

		AssemblyOccurrence LiveOcc( AssemblyDocument doc, string id ) =>
			doc.Occurrences.First( o => o.Id == id );

		AssemblyJoint LiveJoint( AssemblyDocument doc, string id ) =>
			doc.Joints.First( j => j.Id == id );

		(AssemblyDocument Doc, AssemblyOccurrence A, AssemblyOccurrence B, AssemblyOccurrence C) TwoPartDoc()
		{
			var doc = new AssemblyDocument();
			var definition = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Bracket",
				SourceKind = "effigy-file", SourceReference = "parts/bracket.effigy#body-1",
			};
			doc.Definitions.Add( definition );
			var a = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = definition.Id, Name = "Bracket A",
				Transform = AssemblyTransform.FromXform( Xform.Identity ),
			};
			var b = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = definition.Id, Name = "Bracket B",
				Transform = AssemblyTransform.FromXform(
					Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2 ) * Xform.Translate( new Vec3( 5, 0, 0 ) ) ),
			};
			var c = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = definition.Id, Name = "Bracket C",
				Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 9, 0 ) ) ),
			};
			doc.Occurrences.Add( a );
			doc.Occurrences.Add( b );
			doc.Occurrences.Add( c );
			return ( doc, a, b, c );
		}

		AssemblyConnectorRef Connector( AssemblyOccurrence part, string label, Xform frame ) =>
			new( part.Id, AssemblyTransform.FromXform( frame ), "face", label, "face#1" );

		AssemblyJointDraft Draft( AssemblyOccurrence a, AssemblyOccurrence b, string name = "" ) =>
			new()
			{
				ConnectorA = Connector( a, "Top face", Xform.Identity ),
				ConnectorB = Connector( b, "Top face", Xform.Identity ),
				Name = name,
			};

		// 1. End-to-end create: one joint, B solved onto A, exactly once.
		Check( "create moves B so its connector frame coincides with A's, adding exactly one joint", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			var result = AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b, "Base joint" ) );
			var joint = doc.Joints.SingleOrDefault();
			var solvedB = LiveOcc( doc, b.Id ).Transform.ToXform();
			Require( result.Ok, result.Message );
			Require( joint != null && doc.Joints.Count == 1 && joint.Kind == "fixed"
				&& AssemblyJointService.GetJointName( joint ) == "Base joint"
				&& result.RootOccurrenceId == a.Id && result.MovingOccurrenceId == b.Id && result.HasProposed
				&& solvedB.Origin.AlmostEquals( Vec3.Zero )
				&& solvedB.X.AlmostEquals( new Vec3( 1, 0, 0 ) ) && solvedB.Y.AlmostEquals( new Vec3( 0, 1, 0 ) ),
				"create did not solve B onto A: " + result.Message );
			// World frames coincide after Apply.
			var worldA = LiveOcc( doc, a.Id ).Transform.ToXform() * Xform.Identity;
			var worldB = LiveOcc( doc, b.Id ).Transform.ToXform() * Xform.Identity;
			Require( worldA.Origin.AlmostEquals( worldB.Origin ) && worldA.Z.AlmostEquals( worldB.Z ),
				"applied joint leaves both connector world frames equal" );
		} );

		// 2. Offset and flip are honoured by the same solve.
		Check( "offset shifts B along the A frame axes and flip turns B 180 about its own X", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			var draft = Draft( a, b );
			draft.Offset = new Vec3( 1, 2, 0 );
			draft.Flipped = true;
			var result = AssemblyJointService.CreateFixedJoint( doc, history, draft );
			var solvedB = LiveOcc( doc, b.Id ).Transform.ToXform();
			var joint = doc.Joints.Single();
			Require( result.Ok, result.Message );
			Require( solvedB.Origin.AlmostEquals( new Vec3( 1, 2, 0 ) )
				&& solvedB.Y.AlmostEquals( new Vec3( 0, -1, 0 ) ) && solvedB.Z.AlmostEquals( new Vec3( 0, 0, -1 ) ),
				"offset/flip solve wrong: origin " + AssemblyJointService.Fmt( solvedB.Origin ) );
			Require( AssemblyJointService.GetJointOffset( joint ).AlmostEquals( new Vec3( 1, 2, 0 ) )
				&& AssemblyJointService.IsJointFlipped( joint ), "stored offset/flip did not round-trip" );
		} );

		// 3. Preview is disposable: it changes nothing.
		Check( "preview computes the solved position without touching the document or the parts", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var draft = Draft( a, b );
			draft.CapturePrior( doc );
			var before = LiveOcc( doc, b.Id ).Transform.ToXform().Origin;
			var preview = AssemblyJointService.Preview( doc, draft );
			Require( preview.Ok && preview.HasProposed && doc.Joints.Count == 0
				&& LiveOcc( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( before )
				&& draft.PriorTransformsStillHold( doc ),
				"preview mutated the document: " + preview.Message );
			Require( preview.RootOccurrenceId == a.Id && preview.MovingOccurrenceId == b.Id,
				"preview picked the wrong root/moving side" );
			Require( preview.RelationText.Contains( "in apart" )
				&& preview.AnchorText.Contains( "Bracket A" ) && preview.AnchorText.Contains( "Bracket B" ),
				"preview texts do not describe the relation: " + preview.RelationText );
		} );

		// 4. Edit: offset change, rename, duplicate name rejected.
		Check( "edit updates the stored joint and re-solves B from the new offset", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			var created = AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b, "First" ) );
			Require( created.Ok, created.Message );
			var joint = doc.Joints.Single();
			var draft = AssemblyJointService.DraftFromJoint( doc, joint );
			Require( draft != null, "DraftFromJoint returned null for a stored joint" );
			draft.Offset = new Vec3( 0, 0, 3 );
			var edited = AssemblyJointService.UpdateFixedJoint( doc, history, draft );
			Require( edited.Ok, edited.Message );
			Require( doc.Joints.Count == 1
				&& LiveOcc( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( new Vec3( 0, 0, 3 ) ),
				"edit did not re-solve B from the new offset: " + edited.Message );
			Require( AssemblyJointService.GetJointOffset( LiveJoint( doc, joint.Id ) ).AlmostEquals( new Vec3( 0, 0, 3 ) ),
				"stored offset was not updated" );
			var renamed = AssemblyJointService.RenameFixedJoint( doc, history, joint.Id, "Renamed" );
			Require( renamed.Ok && AssemblyJointService.GetJointName( LiveJoint( doc, joint.Id ) ) == "Renamed",
				"rename did not stick: " + renamed.Message );
			var second = AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b, "Second" ) );
			Require( !second.Ok && second.Message.Contains( "already have a fixed joint" ),
				"a second fixed joint between the same two parts was not refused: " + second.Message );
			var dupName = AssemblyJointService.RenameFixedJoint( doc, history, joint.Id, "Renamed" );
			Require( dupName.Ok, "renaming a joint to its own current name must be allowed: " + dupName.Message );
			var blank = AssemblyJointService.RenameFixedJoint( doc, history, joint.Id, "   " );
			Require( !blank.Ok && blank.ErrorCode == "invalid-name",
				"an empty joint name was not refused: " + blank.Message );
		} );

		// 5. Suppress / unsuppress, with undo / redo across the whole change.
		Check( "suppressing stores the flag and leaves the parts where they are", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			var created = AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b, "Supp" ) );
			Require( created.Ok, created.Message );
			var jointId = doc.Joints.Single().Id;
			var solvedOrigin = LiveOcc( doc, b.Id ).Transform.ToXform().Origin;
			var suppressed = AssemblyJointService.SetJointSuppressed( doc, history, jointId, true );
			Require( suppressed.Ok && AssemblyJointService.IsJointSuppressed( LiveJoint( doc, jointId ) )
				&& LiveOcc( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( solvedOrigin ),
				"suppressing moved the parts or lost the flag: " + suppressed.Message );
			var already = AssemblyJointService.SetJointSuppressed( doc, history, jointId, true );
			Require( already.Ok && already.Message == "That joint is already suppressed.",
				"suppressing an already-suppressed joint must be a no-op success: " + already.Message );
			// Drift B while suppressed, then unsuppress: it must line up again.
			LiveOcc( doc, b.Id ).Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 7, 7, 7 ) ) );
			var unsuppressed = AssemblyJointService.SetJointSuppressed( doc, history, jointId, false );
			Require( unsuppressed.Ok && !AssemblyJointService.IsJointSuppressed( LiveJoint( doc, jointId ) )
				&& LiveOcc( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( solvedOrigin ),
				"unsuppressing did not re-solve B onto A: " + unsuppressed.Message );
			var active = AssemblyJointService.SetJointSuppressed( doc, history, jointId, false );
			Require( active.Ok && active.Message == "That joint is already active.",
				"unsuppressing an already-active joint must be a no-op success: " + active.Message );
			Require( Undo( history ) && AssemblyJointService.IsJointSuppressed( LiveJoint( doc, jointId ) )
				&& Redo( history ) && !AssemblyJointService.IsJointSuppressed( LiveJoint( doc, jointId ) ),
				"undo and redo did not restore suppression and the joint" );
		} );

		// 6. Undo / redo of a creation restores the joint and B's position.
		Check( "undo removes the created joint and restores B; redo brings both back", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			var before = LiveOcc( doc, b.Id ).Transform.ToXform().Origin;
			var created = AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b, "Undo me" ) );
			// Restores replace the document's collections in place, so the
			// occurrence objects captured above go stale: read the live document
			// back by stable Id after every undo or redo.
			var undone = Undo( history );
			var undoneJoints = doc.Joints.Count;
			var undoneB = LiveOcc( doc, b.Id ).Transform.ToXform();
			var redone = undone && Redo( history );
			var redoneJoints = doc.Joints.Count;
			var redoneB = LiveOcc( doc, b.Id ).Transform.ToXform();
			var redoneJoint = doc.Joints.SingleOrDefault();
			Require( created.Ok && undone && undoneJoints == 0 && undoneB.Origin.AlmostEquals( before )
				&& redone && redoneJoints == 1 && redoneJoint != null && redoneJoint.Id == created.JointId
				&& redoneB.Origin.AlmostEquals( Vec3.Zero )
				&& redoneB.X.AlmostEquals( new Vec3( 1, 0, 0 ) ) && redoneB.Y.AlmostEquals( new Vec3( 0, 1, 0 ) ),
				$"undone={undone} undoneJoints={undoneJoints} undoneB={AssemblyJointService.Fmt( undoneB.Origin )} redone={redone} redoneJoints={redoneJoints} redoneJoint={( redoneJoint == null ? "null" : redoneJoint.Id )} redoneB={AssemblyJointService.Fmt( redoneB.Origin )}" );
		} );

		// 7. Grounding: default root, chosen root, both grounded refuses.
		Check( "with nothing grounded, A stays put by default and B moves", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			var options = AssemblyJointService.GroundedRootOptions( doc, Draft( a, b ) );
			Require( options.Count == 2
				&& options[0].OccurrenceId == a.Id && options[1].OccurrenceId == b.Id,
				"ungrounded root options must offer both sides" );
			var result = AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b ) );
			Require( result.Ok && result.RootOccurrenceId == a.Id
				&& LiveOcc( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( Vec3.Zero ),
				"with nothing grounded A must stay and B must move: " + result.Message );
		} );

		Check( "grounding B keeps B put and moves A onto it instead", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			var grounded = AssemblyJointService.ChooseGroundedRoot( doc, history, b.Id );
			Require( grounded.Ok, grounded.Message );
			Require( AssemblyJointService.GroundedRootOptions( doc, Draft( a, b ) ).Count == 0,
				"a grounded side must decide the root, leaving no options" );
			var result = AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b ) );
			Require( result.Ok && result.RootOccurrenceId == b.Id
				&& LiveOcc( doc, a.Id ).Transform.ToXform().Origin.AlmostEquals( new Vec3( 0, 5, 0 ) )
				&& LiveOcc( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( new Vec3( 0, 5, 0 ) ),
				"grounding B must keep B put and move A onto it: " + result.Message );
			Require( Undo( history ) && Undo( history ) && doc.Joints.Count == 0
				&& !AssemblyJointService.IsOccurrenceGrounded( LiveOcc( doc, b.Id ) ),
				"undoing the joint and the grounding must restore both" );
		} );

		Check( "two grounded parts refuse the joint instead of guessing", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			AssemblyJointService.SetOccurrenceGrounded( LiveOcc( doc, a.Id ), true );
			AssemblyJointService.SetOccurrenceGrounded( LiveOcc( doc, b.Id ), true );
			var result = AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b ) );
			Require( !result.Ok && result.Message.Contains( "grounded" ),
				"two grounded parts must refuse the joint: " + result.Message );
			Require( history.UndoCount == 0, "the refused joint must not add a history entry" );
		} );

		// 8. Errors explain themselves instead of committing a broken relation.
		Check( "refused joints leave nothing behind and add no history entry", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			var samePart = new AssemblyJointDraft
			{
				ConnectorA = Connector( a, "Top", Xform.Identity ),
				ConnectorB = Connector( a, "Side", Xform.Identity ),
			};
			Require( !AssemblyJointService.CreateFixedJoint( doc, history, samePart ).Ok,
				"both connectors on one part must be refused" );
			var missing = new AssemblyJointDraft
			{
				ConnectorA = Connector( a, "Top", Xform.Identity ),
				ConnectorB = new AssemblyConnectorRef( AssemblyDocument.NewId(),
					AssemblyTransform.FromXform( Xform.Identity ), "face", "Gone", "" ),
			};
			Require( !AssemblyJointService.CreateFixedJoint( doc, history, missing ).Ok,
				"a connector whose part is gone must be refused" );
			var badFrame = new AssemblyJointDraft
			{
				ConnectorA = Connector( a, "Top", Xform.Identity ),
				ConnectorB = new AssemblyConnectorRef( b.Id,
					AssemblyTransform.FromXform( new Xform( new Vec3( 2, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), Vec3.Zero ) ),
					"face", "Stretched", "" ),
			};
			Require( !AssemblyJointService.CreateFixedJoint( doc, history, badFrame ).Ok,
				"a non-rigid connector frame must be refused" );
			Require( doc.Joints.Count == 0, "a refused joint left something behind" );
			Require( LiveOcc( doc, b.Id ).Transform.ToXform().Origin.AlmostEquals( new Vec3( 0, 5, 0 ) ),
				"a refused joint moved B" );
			Require( history.UndoCount == 0, "refused joints must not add history entries" );
		} );

		// 9. Chain conflict: parts already fixed together cannot take another joint.
		Check( "a joint closing a fixed chain is refused as over-constraining", () =>
		{
			var (doc, a, b, c) = TwoPartDoc();
			var history = new EditHistory();
			Require( AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b, "AB" ) ).Ok, "AB create failed" );
			Require( AssemblyJointService.CreateFixedJoint( doc, history, Draft( b, c, "BC" ) ).Ok, "BC create failed" );
			var loop = AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, c, "AC" ) );
			Require( !loop.Ok && loop.Message.Contains( "over-constrain" ),
				"a joint closing a fixed chain must be refused: " + loop.Message );
			Require( doc.Joints.Count == 2, "the refused joint was stored" );
		} );

		// 10. Save / reopen keeps name, offset, flip, suppression, grounding and labels.
		Check( "save/reopen round-trips the joint, its options and grounding deterministically", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			AssemblyJointService.ChooseGroundedRoot( doc, history, a.Id );
			var draft = Draft( a, b, "Keep me" );
			draft.Offset = new Vec3( 1, 0, 0 );
			draft.Flipped = true;
			var created = AssemblyJointService.CreateFixedJoint( doc, history, draft );
			Require( created.Ok, created.Message );
			var joint = doc.Joints.Single();
			AssemblyJointService.SetJointSuppressed( doc, history, joint.Id, true );
			var text = doc.ToJson();
			var restored = AssemblyDocument.FromJson( text );
			var jointBack = restored.Joints.Single();
			var aBack = restored.Occurrences.First( o => o.Id == a.Id );
			Require( AssemblyJointService.GetJointName( jointBack ) == "Keep me"
				&& AssemblyJointService.GetJointOffset( jointBack ).AlmostEquals( new Vec3( 1, 0, 0 ) )
				&& AssemblyJointService.IsJointFlipped( jointBack )
				&& AssemblyJointService.IsJointSuppressed( jointBack )
				&& AssemblyJointService.IsOccurrenceGrounded( aBack )
				&& restored.Validate().Count == 0
				&& AssemblyJointService.ValidateFixedJoints( restored ).Count == 0
				&& restored.ToJson() == text,
				"save/reopen did not round-trip the joint, its options and grounding deterministically" );
			var deleted = AssemblyJointService.DeleteFixedJoint( doc, history, joint.Id );
			Require( deleted.Ok && doc.Joints.Count == 0 && doc.Occurrences.Count == 3,
				"delete must remove the joint and leave the parts: " + deleted.Message );
			Require( Undo( history ) && doc.Joints.Count == 1, "undo must bring a deleted joint back" );
		} );

		// 11. ValidateFixedJoints diagnoses hand-built damage (beyond the create path).
		Check( "ValidateFixedJoints flags duplicate names and non-rigid frames", () =>
		{
			var (doc, a, b, c) = TwoPartDoc();
			var history = new EditHistory();
			Require( AssemblyJointService.CreateFixedJoint( doc, history, Draft( a, b, "Dup" ) ).Ok, "AB create failed" );
			Require( AssemblyJointService.CreateFixedJoint( doc, history, Draft( b, c, "Other" ) ).Ok, "BC create failed" );
			Require( AssemblyJointService.ValidateFixedJoints( doc ).Count == 0,
				"two well-formed joints must validate clean" );
			var second = doc.Joints[1];
			second.ExtensionData["name"] = JsonSerializer.SerializeToElement( "Dup" );
			Require( AssemblyJointService.ValidateFixedJoints( doc ).Any( i => i.Code == "duplicate-joint-name" ),
				"a duplicate joint name was not diagnosed" );
			second.ExtensionData["name"] = JsonSerializer.SerializeToElement( "Other" );
			second.FrameB = AssemblyTransform.FromXform(
				new Xform( new Vec3( 2, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), Vec3.Zero ) );
			Require( AssemblyJointService.ValidateFixedJoints( doc ).Any( i => i.Code == "invalid-connector-frame" ),
				"a non-rigid stored frame was not diagnosed" );
			second.FrameB = AssemblyTransform.FromXform( Xform.Identity );
			second.FrameA = null;
			Require( AssemblyJointService.DraftFromJoint( doc, second ) == null,
				"DraftFromJoint must return null when a stored frame is missing" );
		} );

		// 12. Missing and wrong-kind operations fail cleanly; joint helpers default.
		Check( "missing and wrong-kind operations fail cleanly", () =>
		{
			var (doc, a, b, _) = TwoPartDoc();
			var history = new EditHistory();
			var gone = AssemblyDocument.NewId();
			Require( !AssemblyJointService.DeleteFixedJoint( doc, history, gone ).Ok
				&& AssemblyJointService.DeleteFixedJoint( doc, history, gone ).ErrorCode == "missing-joint",
				"deleting a missing joint must fail missing-joint" );
			Require( AssemblyJointService.RenameFixedJoint( doc, history, gone, "X" ).ErrorCode == "missing-joint",
				"renaming a missing joint must fail missing-joint" );
			Require( AssemblyJointService.ChooseGroundedRoot( doc, history, gone ).ErrorCode == "missing-occurrence",
				"grounding a missing part must fail missing-occurrence" );
			var bogus = Draft( a, b );
			bogus.JointId = gone;
			Require( AssemblyJointService.UpdateFixedJoint( doc, history, bogus ).ErrorCode == "missing-joint",
				"updating a missing joint must fail missing-joint" );
			Require( AssemblyJointService.CreateFixedJoint( null, history, Draft( a, b ) ).ErrorCode == "no-document",
				"a null document must fail no-document" );
			var hinge = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "hinge",
				OccurrenceA = a.Id, OccurrenceB = b.Id,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Identity ),
			};
			doc.Joints.Add( hinge );
			Require( AssemblyJointService.RenameFixedJoint( doc, history, hinge.Id, "X" ).ErrorCode == "wrong-kind",
				"renaming a hinge joint must fail wrong-kind" );
			Require( AssemblyJointService.DeleteFixedJoint( doc, history, hinge.Id ).ErrorCode == "wrong-kind",
				"deleting a hinge joint must fail wrong-kind" );
			Require( AssemblyJointService.SetJointSuppressed( doc, history, hinge.Id, true ).ErrorCode == "wrong-kind",
				"suppressing a hinge joint must fail wrong-kind" );
			var hingeDraft = Draft( a, b );
			hingeDraft.JointId = hinge.Id;
			Require( AssemblyJointService.UpdateFixedJoint( doc, history, hingeDraft ).ErrorCode == "wrong-kind",
				"updating a hinge joint must fail wrong-kind" );
			// Helpers on a joint with no ExtensionData.
			Require( AssemblyJointService.GetJointName( hinge ) == ""
				&& AssemblyJointService.JointDisplayName( doc, hinge ) == "Fixed joint 1"
				&& !AssemblyJointService.IsJointSuppressed( hinge )
				&& AssemblyJointService.GetJointOffset( hinge ).AlmostEquals( Vec3.Zero )
				&& !AssemblyJointService.IsJointFlipped( hinge ),
				"joint helpers must default on a joint with no stored options" );
			Require( history.UndoCount == 0, "failed operations must not add history entries" );
		} );
	}
}
