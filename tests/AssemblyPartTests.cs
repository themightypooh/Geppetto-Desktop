using System;
using System.Linq;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// Assemblies 02: reusable part definitions and occurrence commands.
// Mirrors Mobile app/AssemblyPartsChecks.cs, adapted to Desktop: PartStudio
// instead of CadDoc, DocumentSession/EditHistory instead of CadDoc.Checkpoint,
// StudioDocument.Write/Read for the save/reopen round-trips. No build, app
// launch or screenshot is claimed here; the full build and visual acceptance
// belong to the final integrator.
internal static class AssemblyPartTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-parts", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		static PartStudio TwoBoxStudio()
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() );
			studio.Add( new PrimitiveFeature() );
			studio.Rebuild();
			return studio;
		}

		Check( "add from a studio body creates one shared definition and one identity-placed occurrence", () =>
		{
			var studio = TwoBoxStudio();
			Require( studio.Bodies.Count == 2, $"expected 2 bodies, got {studio.Bodies.Count}" );
			var body = studio.Bodies[0];

			var added = AssemblyPartService.AddOccurrence( studio, body );
			Require( added != null, "add returned null for a body in the studio" );
			Require( studio.Assembly.Occurrences.Count == 1, "expected 1 occurrence" );
			Require( studio.Assembly.Definitions.Count == 1, "expected 1 definition" );
			Require( studio.Assembly.Definitions[0].SourceKind == AssemblyPartService.StudioBodyKind,
				"definition source kind is not studio-body" );
			Require( studio.Assembly.Definitions[0].SourceReference == body.Id,
				"definition does not reference the body id" );
			Require( added!.DefinitionId == studio.Assembly.Definitions[0].Id,
				"occurrence does not point at the definition" );
			Require( added.Visible, "new occurrence must be visible" );
			Require( added.Transform.ToXform().Origin.AlmostEquals( Vec3.Zero ),
				"new occurrence must sit where its source body is" );
		} );

		Check( "adding the same body again shares the definition with a new id and name", () =>
		{
			var studio = TwoBoxStudio();
			var body = studio.Bodies[0];
			var added = AssemblyPartService.AddOccurrence( studio, body );
			var addedAgain = AssemblyPartService.AddOccurrence( studio, body );
			Require( addedAgain != null, "second add returned null" );
			Require( studio.Assembly.Occurrences.Count == 2, "expected 2 occurrences" );
			Require( studio.Assembly.Definitions.Count == 1, "expected the definition to be shared" );
			Require( addedAgain!.DefinitionId == added!.DefinitionId, "second occurrence must share the definition" );
			Require( addedAgain.Id != added.Id, "occurrences must have distinct ids" );
			Require( addedAgain.Name != added.Name, "occurrences must have distinct names" );
		} );

		Check( "adding a different body creates a second definition", () =>
		{
			var studio = TwoBoxStudio();
			var added = AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			var other = AssemblyPartService.AddOccurrence( studio, studio.Bodies[1] );
			Require( other != null, "add of the second body returned null" );
			Require( studio.Assembly.Definitions.Count == 2, "expected 2 definitions" );
			Require( other!.DefinitionId != added!.DefinitionId, "different bodies must not share a definition" );
		} );

		Check( "duplicate shares the definition, offsets along +X by the sibling count, and never moves the source", () =>
		{
			var studio = TwoBoxStudio();
			var body = studio.Bodies[0];
			var added = AssemblyPartService.AddOccurrence( studio, body );
			AssemblyPartService.AddOccurrence( studio, body );
			AssemblyPartService.AddOccurrence( studio, studio.Bodies[1] );
			var beforePositions = body.Mesh.Positions.ToArray();

			var copy = AssemblyPartService.DuplicateOccurrence( studio, added!.Id );
			Require( copy != null, "duplicate returned null" );
			Require( copy!.Id != added.Id, "copy must have a new occurrence id" );
			Require( copy.DefinitionId == added.DefinitionId, "copy must share the source definition" );
			Require( copy.Name.Contains( "copy", StringComparison.Ordinal ), "copy name must say copy" );
			Require( studio.Assembly.Occurrences.Count == 4, "expected 4 occurrences after the duplicate" );
			var offset = copy.Transform.ToXform().Origin - added.Transform.ToXform().Origin;
			Require( MathF.Abs( offset.x - 2f ) < 1e-5f && MathF.Abs( offset.y ) < 1e-5f && MathF.Abs( offset.z ) < 1e-5f,
				$"duplicate must be offset +2 inches along X, got {offset}" );

			copy.Transform.Origin = new[] { 9f, 8f, 7f };
			Require( added.Transform.ToXform().Origin.AlmostEquals( Vec3.Zero ),
				"moving the duplicate must not move the original" );
			Require( body.Mesh.Positions.SequenceEqual( beforePositions ),
				"moving an occurrence must not move the shared source vertices" );
		} );

		Check( "renaming one occurrence renames neither its sibling nor its definition", () =>
		{
			var studio = TwoBoxStudio();
			var body = studio.Bodies[0];
			var added = AssemblyPartService.AddOccurrence( studio, body );
			var addedAgain = AssemblyPartService.AddOccurrence( studio, body );
			Require( AssemblyPartService.RenameOccurrence( studio, added!.Id, "Bracket left" ),
				"rename must succeed" );
			Require( added.Name == "Bracket left", "occurrence was not renamed" );
			Require( addedAgain!.Name != "Bracket left", "rename must not touch the sibling" );
			Require( studio.Assembly.Definitions[0].Name != "Bracket left",
				"rename must not touch the definition" );
			Require( !AssemblyPartService.RenameOccurrence( studio, added.Id, "   " )
				&& added.Name == "Bracket left", "blank rename must be refused and the old name must stand" );
			Require( AssemblyPartService.RenameOccurrence( studio, added.Id, "Bracket left" ),
				"renaming to the current name must be a no-op success" );
			Require( !AssemblyPartService.RenameOccurrence( studio, AssemblyDocument.NewId(), "Nope" ),
				"renaming an unknown occurrence must fail" );
		} );

		Check( "hide and show survive save and reopen", () =>
		{
			var studio = TwoBoxStudio();
			var added = AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			var addedAgain = AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			Require( AssemblyPartService.SetOccurrenceVisible( studio, addedAgain!.Id, false ),
				"hide must succeed" );

			var roundTripped = StudioDocument.Read( StudioDocument.Write( studio ) );
			roundTripped.Rebuild();
			var hiddenBack = roundTripped.Assembly.Occurrences.Single( o => o.Id == addedAgain.Id );
			Require( !hiddenBack.Visible && hiddenBack.Name == addedAgain.Name,
				"hidden occurrence must come back hidden with its name" );
			var shownBack = roundTripped.Assembly.Occurrences.Single( o => o.Id == added!.Id );
			Require( shownBack.Visible, "visible occurrence must come back visible" );
			Require( AssemblyPartService.Issues( roundTripped ).Count == 0,
				"round-tripped assembly must have no validation issues" );
		} );

		Check( "removal is refused while a joint references the occurrence, changing nothing", () =>
		{
			var studio = TwoBoxStudio();
			var added = AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			var copy = AssemblyPartService.DuplicateOccurrence( studio, added!.Id );
			var joint = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed",
				OccurrenceA = added.Id, OccurrenceB = copy!.Id,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Identity ),
			};
			studio.Assembly.Joints.Add( joint );

			var (blocked, blocking) = AssemblyPartService.RemoveOccurrence( studio, added.Id );
			Require( blocked == AssemblyPartService.AssemblyOccurrenceRemoveResult.HasJoints,
				"joint-referenced removal must be refused" );
			Require( blocking.Count == 1 && blocking[0].Id == joint.Id,
				"refusal must name the blocking joint" );
			Require( studio.Assembly.Occurrences.Any( o => o.Id == added.Id ),
				"refused removal must keep the occurrence" );
			Require( studio.Assembly.Joints.Any( j => j.Id == joint.Id ),
				"refused removal must keep the joint" );

			studio.Assembly.Joints.Remove( joint );
			var (removed, _) = AssemblyPartService.RemoveOccurrence( studio, added.Id );
			Require( removed == AssemblyPartService.AssemblyOccurrenceRemoveResult.Removed,
				"unreferenced occurrence must remove cleanly" );
			Require( !studio.Assembly.Occurrences.Any( o => o.Id == added.Id ),
				"removed occurrence must be gone" );
			Require( studio.Assembly.Definitions.Any( d => d.Id == added.DefinitionId ),
				"removing an occurrence must keep its definition for the other occurrences" );
		} );

		Check( "removing an unknown occurrence reports not found", () =>
		{
			var studio = TwoBoxStudio();
			var (result, _) = AssemblyPartService.RemoveOccurrence( studio, AssemblyDocument.NewId() );
			Require( result == AssemblyPartService.AssemblyOccurrenceRemoveResult.NotFound,
				"unknown occurrence must report not found" );
		} );

		Check( "occurrences definitions names and visibility round-trip with no validation issues", () =>
		{
			var studio = TwoBoxStudio();
			var added = AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			var addedAgain = AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			AssemblyPartService.RenameOccurrence( studio, added!.Id, "Bracket left" );
			AssemblyPartService.SetOccurrenceVisible( studio, addedAgain!.Id, false );

			var persisted = StudioDocument.Read( StudioDocument.Write( studio ) );
			persisted.Rebuild();
			Require( persisted.Assembly.Occurrences.Count == studio.Assembly.Occurrences.Count,
				"occurrence count must survive save and reopen" );
			Require( persisted.Assembly.Definitions.Count == studio.Assembly.Definitions.Count,
				"definition count must survive save and reopen" );
			Require( persisted.Assembly.Occurrences.All( o =>
				studio.Assembly.Occurrences.Any( d => d.Id == o.Id && d.Name == o.Name
					&& d.Visible == o.Visible && d.DefinitionId == o.DefinitionId ) ),
				"occurrence ids, names, visibility and definitions must round-trip exactly" );
			Require( AssemblyPartService.Issues( persisted ).Count == 0,
				"round-tripped assembly must validate with no issues" );
		} );

		Check( "missing-source occurrences are diagnosed and survive save and reopen", () =>
		{
			var studio = TwoBoxStudio();
			AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			var ghostDef = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Gone part",
				SourceKind = AssemblyPartService.StudioBodyKind, SourceReference = "no-such-body",
			};
			studio.Assembly.Definitions.Add( ghostDef );
			var ghost = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = ghostDef.Id, Name = "Ghost copy",
			};
			studio.Assembly.Occurrences.Add( ghost );

			Require( AssemblyPartService.Issues( studio ).Any( i => i.Code == "missing-source-reference" ),
				"an occurrence whose source body is gone must be diagnosed" );

			var ghostBack = StudioDocument.Read( StudioDocument.Write( studio ) );
			ghostBack.Rebuild();
			Require( ghostBack.Assembly.Occurrences.Any( o => o.Id == ghost.Id ),
				"missing-source occurrences must survive save and reopen for repair" );
			var ghostIssues = AssemblyPartService.Issues( ghostBack )
				.Where( i => i.Code == "missing-source-reference" ).ToArray();
			Require( ghostIssues.Length == 1 && ghostIssues[0].RecordId == ghostDef.Id,
				"exactly one missing-source issue must survive, naming the ghost definition" );
			Require( AssemblyPartService.MissingLabel( "Ghost copy", "missing-source-reference" )
					== "Missing: Ghost copy (missing-source-reference)",
				"missing rows must read 'Missing: <name>' with the validation code" );
		} );

		Check( "external file references are preserved but report unsupported", () =>
		{
			var studio = TwoBoxStudio();
			var externalDef = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Imported bracket",
				SourceKind = "effigy-file", SourceReference = "bracket.effigy",
			};
			studio.Assembly.Definitions.Add( externalDef );
			var external = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = externalDef.Id, Name = "Imported bracket",
			};
			studio.Assembly.Occurrences.Add( external );

			Require( !AssemblyPartService.SourceExists( studio, externalDef ),
				"an effigy-file source has no live link yet and must report missing" );
			Require( AssemblyPartService.Issues( studio ).Any( i => i.Code == "missing-source-reference" ),
				"an external reference must be diagnosed, not silently resolved" );

			var back = StudioDocument.Read( StudioDocument.Write( studio ) );
			Require( back.Assembly.Definitions.Any( d => d.Id == externalDef.Id )
				&& back.Assembly.Occurrences.Any( o => o.Id == external.Id ),
				"external references must survive save and reopen, never be deleted" );
		} );

		Check( "an occurrence whose definition is gone is diagnosed", () =>
		{
			var studio = TwoBoxStudio();
			var orphan = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = AssemblyDocument.NewId(), Name = "Orphan",
			};
			studio.Assembly.Occurrences.Add( orphan );
			Require( studio.Assembly.Validate().Any( i => i.Code == "missing-definition" ),
				"an occurrence whose definition is gone must be diagnosed" );
			var back = StudioDocument.Read( StudioDocument.Write( studio ) );
			Require( back.Assembly.Occurrences.Any( o => o.Id == orphan.Id ),
				"missing-definition occurrences must survive save and reopen" );
		} );

		Check( "selection is valid only while its occurrence exists", () =>
		{
			var studio = TwoBoxStudio();
			var added = AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			Require( AssemblyPartService.ValidSelection( studio.Assembly, added!.Id ) == added.Id,
				"selection of a live occurrence must stay valid" );
			Require( AssemblyPartService.ValidSelection( studio.Assembly, AssemblyDocument.NewId() ) == "",
				"selection of a gone occurrence must clear" );
			Require( AssemblyPartService.ValidSelection( studio.Assembly, "" ) == "",
				"blank selection must stay blank" );
		} );

		Check( "adding an occurrence is one undo step and redoes with the same id", () =>
		{
			var studio = TwoBoxStudio();
			var session = DocumentSession.CreateEffigy( "assembly-parts.effigy", studio );
			var added = AssemblyPartService.AddOccurrence( session, studio.Bodies[0] );
			Require( added != null, "session add returned null" );
			Require( session.History.UndoCount == 1, $"expected 1 history entry, got {session.History.UndoCount}" );
			Require( session.History.CanUndo && !session.History.CanRedo, "add must be undoable, not redoable" );

			session.History.Undo();
			Require( studio.Assembly.Occurrences.Count == 0, "undo must remove the added occurrence" );
			Require( AssemblyPartService.ValidSelection( studio.Assembly, added!.Id ) == "",
				"selection must clear when undo removes its occurrence" );

			session.History.Redo();
			Require( studio.Assembly.Occurrences.Count == 1, "redo must bring the occurrence back" );
			Require( studio.Assembly.Occurrences[0].Id == added.Id,
				"redo must restore the same occurrence id, not mint a new one" );
			Require( AssemblyPartService.ValidSelection( studio.Assembly, added.Id ) == added.Id,
				"selection must stay valid when its occurrence survives undo/redo" );
		} );

		Check( "renaming an occurrence is one undo step", () =>
		{
			var studio = TwoBoxStudio();
			var session = DocumentSession.CreateEffigy( "assembly-parts.effigy", studio );
			var added = AssemblyPartService.AddOccurrence( session, studio.Bodies[0] );
			Require( AssemblyPartService.RenameOccurrence( session, added!.Id, "Renamed part" ),
				"session rename must succeed" );
			Require( studio.Assembly.Occurrences[0].Name == "Renamed part", "rename did not apply" );
			Require( session.History.UndoCount == 2, $"expected 2 history entries, got {session.History.UndoCount}" );
			session.History.Undo();
			Require( studio.Assembly.Occurrences[0].Name != "Renamed part", "undo must revert the rename" );
			session.History.Redo();
			Require( studio.Assembly.Occurrences[0].Name == "Renamed part", "redo must reapply the rename" );
		} );

		Check( "hiding an occurrence is one undo step", () =>
		{
			var studio = TwoBoxStudio();
			var session = DocumentSession.CreateEffigy( "assembly-parts.effigy", studio );
			var added = AssemblyPartService.AddOccurrence( session, studio.Bodies[0] );
			Require( AssemblyPartService.SetOccurrenceVisible( session, added!.Id, false ),
				"session hide must succeed" );
			Require( !studio.Assembly.Occurrences[0].Visible, "hide did not apply" );
			session.History.Undo();
			Require( studio.Assembly.Occurrences[0].Visible, "undo must restore visibility" );
			session.History.Redo();
			Require( !studio.Assembly.Occurrences[0].Visible, "redo must reapply the hide" );
		} );

		Check( "a source edit updates both occurrences without touching their ids", () =>
		{
			var studio = TwoBoxStudio();
			var added = AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			var addedAgain = AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			var beforePositions = studio.Bodies[0].Mesh.Positions.ToArray();

			((PrimitiveFeature)studio.Features[0]).SizeX.Value = 2f;
			// A bare param write does not dirty the studio (PartStudio only
			// dirties on Add/Insert/Move); force the re-evaluation.
			studio.MarkAllDirty();
			studio.Rebuild();

			Require( !studio.Bodies[0].Mesh.Positions.SequenceEqual( beforePositions ),
				"the source edit must change the source body geometry" );
			Require( studio.Assembly.Definitions.Count == 1, "the source edit must not duplicate the definition" );
			Require( added!.DefinitionId == addedAgain!.DefinitionId
				&& studio.Assembly.Occurrences.All( o => o.DefinitionId == added.DefinitionId ),
				"both occurrences must still share the one definition after the source edit" );
			Require( studio.Assembly.Occurrences.Any( o => o.Id == added.Id )
				&& studio.Assembly.Occurrences.Any( o => o.Id == addedAgain.Id ),
				"occurrence ids must survive a rebuild" );
			Require( AssemblyPartService.ValidSelection( studio.Assembly, added.Id ) == added.Id,
				"selection must survive a rebuild" );
			Require( AssemblyPartService.Issues( studio ).Count == 0,
				"the rebuilt assembly must validate with no issues" );
		} );

		Check( "a suppressed source is diagnosed and its occurrences are kept", () =>
		{
			var studio = TwoBoxStudio();
			var firstBodyId = studio.Bodies[0].Id;
			AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );
			AssemblyPartService.AddOccurrence( studio, studio.Bodies[0] );

			studio.Features[0].Suppressed = true;
			studio.MarkAllDirty();
			studio.Rebuild();
			Require( studio.Bodies.Count == 1, $"expected 1 body after suppression, got {studio.Bodies.Count}" );
			Require( studio.Assembly.Occurrences.Count == 2,
				"suppressing a source must keep its occurrences for repair" );
			var issues = AssemblyPartService.Issues( studio )
				.Where( i => i.Code == "missing-source-reference" ).ToArray();
			Require( issues.Length == 1, "the suppressed source must be diagnosed once" );
			Require( studio.Assembly.Definitions.Any( d => d.SourceReference == firstBodyId ),
				"the definition must be kept even though its source is suppressed" );
		} );

		Check( "adding a foreign body or duplicating a missing occurrence returns null", () =>
		{
			var studio = TwoBoxStudio();
			var otherStudio = TwoBoxStudio();
			Require( AssemblyPartService.AddOccurrence( studio, otherStudio.Bodies[0] ) == null,
				"adding a body from another studio must return null" );
			Require( studio.Assembly.Definitions.Count == 0,
				"a refused add must not create a definition" );
			Require( AssemblyPartService.DuplicateOccurrence( studio, AssemblyDocument.NewId() ) == null,
				"duplicating a missing occurrence must return null" );
			Require( AssemblyPartService.AddOccurrence( studio, null ) == null,
				"adding a null body must return null" );
		} );
	}
}
