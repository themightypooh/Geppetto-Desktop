using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS06: deterministic fixed-component solver and degrees of freedom.
// Ports Mobile app/AssemblySolverChecks.cs (fixtures ChainDoc, oracle
// ConnectorFramesCoincide) plus the ticket's extra scale/mirror, NaN/singular,
// reversed-order, multi-grounded-root and redundant-cycle cases. The solver is a
// pure function of the stored document: these tests compare connector equations
// and exact transforms, never only a success flag. Not runtime verified: the
// final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblySolverTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-solver", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float a, float b, float eps, string what )
		{
			if ( MathF.Abs( a - b ) > eps ) throw new Exception( $"{what}: expected {b}, got {a}" );
		}
		void NearVec( Vec3 a, Vec3 b, float eps, string what )
		{
			Near( a.x, b.x, eps, what + ".x" );
			Near( a.y, b.y, eps, what + ".y" );
			Near( a.z, b.z, eps, what + ".z" );
		}
		void NearXform( Xform a, Xform b, float eps, string what )
		{
			NearVec( a.X, b.X, eps, what + ".X" );
			NearVec( a.Y, b.Y, eps, what + ".Y" );
			NearVec( a.Z, b.Z, eps, what + ".Z" );
			NearVec( a.Origin, b.Origin, eps, what + ".Origin" );
		}
		bool HasError( AssemblySolveResult result, string code ) =>
			result.Diagnostics.Any( d => d.Kind == "error" && d.Code == code );
		bool HasDiagnostic( AssemblySolveResult result, string code ) =>
			result.Diagnostics.Any( d => d.Code == code );

		// Shared fixture: one "Link" definition, A grounded-able, B and C elsewhere.
		// Joint A-B: FrameA = Rotate((1,2,3), 37deg) * Translate(1,0,0),
		// FrameB = Rotate(Y, 90deg/2) * Translate(0,2,0).
		// Joint B-C: FrameA = Rotate(Z, 120deg) * Translate(0,0,4),
		// FrameB = Translate(-2,0,1).
		(AssemblyDocument Doc, string A, string B, string C) ChainDoc()
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Link",
				SourceKind = "effigy-file", SourceReference = "parts/link.effigy#body-1",
			};
			doc.Definitions.Add( def );
			string Occ( string name, Xform transform )
			{
				var occ = new AssemblyOccurrence
				{
					Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = name,
					Transform = AssemblyTransform.FromXform( transform ),
				};
				doc.Occurrences.Add( occ );
				return occ.Id;
			}
			var a = Occ( "A", Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f ) * Xform.Translate( new Vec3( 3, 1, 0 ) ) );
			var b = Occ( "B", Xform.Translate( new Vec3( 9, 9, 9 ) ) );
			var c = Occ( "C", Xform.Translate( new Vec3( -5, -5, -5 ) ) );
			doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = a, OccurrenceB = b,
				FrameA = AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 1, 2, 3 ), 37f * MathF.PI / 180f ) * Xform.Translate( new Vec3( 1, 0, 0 ) ) ),
				FrameB = AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 0, 1, 0 ), MathF.PI / 4f ) * Xform.Translate( new Vec3( 0, 2, 0 ) ) ),
			} );
			doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = b, OccurrenceB = c,
				FrameA = AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 0, 0, 1 ), 120f * MathF.PI / 180f ) * Xform.Translate( new Vec3( 0, 0, 4 ) ) ),
				FrameB = AssemblyTransform.FromXform( Xform.Translate( new Vec3( -2, 0, 1 ) ) ),
			} );
			return (doc, a, b, c);
		}

		void Ground( AssemblyDocument doc, string occurrenceId )
		{
			var occ = doc.Occurrences.First( o => o.Id == occurrenceId );
			occ.ExtensionData ??= new Dictionary<string, JsonElement>();
			occ.ExtensionData["grounded"] = JsonSerializer.SerializeToElement( true );
		}

		AssemblyOccurrence Find( AssemblyDocument doc, string id ) =>
			doc.Occurrences.First( o => o.Id == id );

		// Oracle: a joint's connector frames coincide in the solved state.
		void FramesCoincide( AssemblySolveResult result, AssemblyJoint joint, float eps = 1e-4f )
		{
			Require( result.TryGetSolvedTransform( joint.OccurrenceA, out var ta ), "missing solved A" );
			Require( result.TryGetSolvedTransform( joint.OccurrenceB, out var tb ), "missing solved B" );
			var worldA = ta * joint.FrameA.ToXform();
			var worldB = tb * joint.FrameB.ToXform();
			NearXform( worldA, worldB, eps, "connector frames must coincide" );
		}

		Check( "grounded chain solves and frames coincide", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			Ground( doc, a );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve must succeed: " + string.Join( "; ", result.Diagnostics.Select( d => d.Code + " " + d.Message ) ) );
			Require( result.Components.Count == 1, $"expected 1 component, got {result.Components.Count}" );
			Require( result.Components[0].RootIsGrounded, "root must be grounded" );
			Require( result.Components[0].RootOccurrenceId == a, "root must be A" );
			Require( result.Components[0].OccurrenceIds.Count == 3, "component must hold A, B, C" );
			// Grounded root keeps its stored transform exactly.
			NearXform( result.SolvedTransforms[a], Find( doc, a ).Transform.ToXform(), 0f, "grounded root must keep stored transform" );
			foreach ( var joint in doc.Joints ) FramesCoincide( result, joint );
			// Solved B equals TA * FA * FB^-1 exactly (same computation, bitwise).
			var jointAB = doc.Joints[0];
			var expectedB = Find( doc, a ).Transform.ToXform() * jointAB.FrameA.ToXform() * jointAB.FrameB.ToXform().Inverse;
			Require( result.SolvedTransforms[b].Equals( expectedB ), "solved B must equal TA*FA*FB^-1" );
			Require( !result.Diagnostics.Any( d => d.Kind == "error" ), "no error diagnostics" );
		} );

		Check( "ungrounded chain picks first id as root and reports it", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve must succeed" );
			var root = new[] { a, b, c }.OrderBy( id => id, StringComparer.Ordinal ).First();
			Require( result.Components[0].RootOccurrenceId == root, $"root must be smallest id {root}" );
			Require( !result.Components[0].RootIsGrounded, "root must not be grounded" );
			Require( HasDiagnostic( result, "ungrounded-component-root" ), "must report ungrounded-component-root info" );
			NearXform( result.SolvedTransforms[root], Find( doc, root ).Transform.ToXform(), 0f, "root keeps stored transform" );
		} );

		Check( "shared definition occurrences solve independently", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			Ground( doc, a );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve must succeed" );
			Require( result.SolvedTransforms.Count == 3, "all three occurrences solved" );
			Require( !result.SolvedTransforms[b].Equals( result.SolvedTransforms[c] ), "B and C must differ" );
		} );

		Check( "moving grounded root carries the chain by the same transform", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			Ground( doc, a );
			var before = AssemblyFixedJointSolver.Solve( doc );
			Require( before.Success, "baseline solve must succeed" );
			var move = Xform.Rotate( new Vec3( 0, 1, 0 ), MathF.PI / 6f ) * Xform.Translate( new Vec3( 2, -1, 5 ) );
			Find( doc, a ).Transform = AssemblyTransform.FromXform( move * Find( doc, a ).Transform.ToXform() );
			var after = AssemblyFixedJointSolver.Solve( doc );
			Require( after.Success, "solve after move must succeed" );
			NearXform( after.SolvedTransforms[b], move * before.SolvedTransforms[b], 1e-4f, "B must move with root" );
			NearXform( after.SolvedTransforms[c], move * before.SolvedTransforms[c], 1e-4f, "C must move with root" );
			foreach ( var joint in doc.Joints ) FramesCoincide( after, joint );
		} );

		Check( "repeated solves are bit-identical and do not mutate the document", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			Ground( doc, a );
			var json = doc.ToJson();
			var first = AssemblyFixedJointSolver.Solve( doc );
			var second = AssemblyFixedJointSolver.Solve( doc );
			var third = AssemblyFixedJointSolver.Solve( doc );
			Require( first.Success && second.Success && third.Success, "all solves must succeed" );
			foreach ( var id in new[] { a, b, c } )
				Require( first.SolvedTransforms[id].Equals( second.SolvedTransforms[id] ) && second.SolvedTransforms[id].Equals( third.SolvedTransforms[id] ), $"repeated solve must not drift for {id}" );
			Require( doc.ToJson() == json, "solver must not mutate the document" );
		} );

		Check( "hinge alone does not rigidly attach", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			foreach ( var joint in doc.Joints ) joint.Kind = "hinge";
			doc.Joints[0].Minimum = -MathF.PI;
			doc.Joints[0].Maximum = MathF.PI;
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve must succeed" );
			Require( result.Components.Count == 3, $"hinge-only doc must have 3 components, got {result.Components.Count}" );
			NearXform( result.SolvedTransforms[b], Find( doc, b ).Transform.ToXform(), 0f, "B keeps stored transform" );
			NearXform( result.SolvedTransforms[c], Find( doc, c ).Transform.ToXform(), 0f, "C keeps stored transform" );
		} );

		Check( "invalid reference fails and preserves last valid", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			Ground( doc, a );
			var good = AssemblyFixedJointSolver.Solve( doc );
			Require( good.Success, "baseline solve must succeed" );
			doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = b, OccurrenceB = AssemblyDocument.NewId(),
				FrameA = new AssemblyTransform(), FrameB = new AssemblyTransform(),
			} );
			var broken = AssemblyFixedJointSolver.Solve( doc, new AssemblySolverOptions { PreviousResult = good } );
			Require( !broken.Success, "broken solve must fail" );
			Require( HasError( broken, "missing-occurrence" ), "must report missing-occurrence" );
			Require( broken.PreservedLastValid, "must preserve last valid" );
			Require( broken.SolvedTransforms[b].Equals( good.SolvedTransforms[b] ), "preserved B must equal previous B" );
			var noPrevious = AssemblyFixedJointSolver.Solve( doc );
			Require( !noPrevious.Success && !noPrevious.PreservedLastValid && noPrevious.SolvedTransforms.Count == 0, "no previous result means empty failure" );
		} );

		Check( "contradictory loop fails with contradictory-fixed-joint", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			Ground( doc, a );
			doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = c, OccurrenceB = a,
				FrameA = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 100, 0, 0 ) ) ),
				FrameB = new AssemblyTransform(),
			} );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( !result.Success, "contradictory loop must fail" );
			Require( HasError( result, "contradictory-fixed-joint" ), "must report contradictory-fixed-joint" );
		} );

		Check( "consistent redundant cycle still solves", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			Ground( doc, a );
			var solved = AssemblyFixedJointSolver.Solve( doc );
			Require( solved.Success, "baseline solve must succeed" );
			// Third joint consistent with the solved poses: C identity frame, A frame
			// = OccA^-1 * OccC, so solvedC * I == solvedA * FB'.
			var frameB = solved.SolvedTransforms[a].Inverse * solved.SolvedTransforms[c];
			doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = c, OccurrenceB = a,
				FrameA = new AssemblyTransform(),
				FrameB = AssemblyTransform.FromXform( frameB ),
			} );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "consistent redundant cycle must solve: " + string.Join( "; ", result.Diagnostics.Select( d => d.Code + " " + d.Message ) ) );
			foreach ( var joint in doc.Joints ) FramesCoincide( result, joint );
		} );

		Check( "two grounded roots that disagree fail with grounded-conflict", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			Ground( doc, a );
			Ground( doc, b );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( !result.Success, "incompatible grounded roots must fail" );
			Require( HasError( result, "grounded-conflict" ), "must report grounded-conflict" );
		} );

		Check( "joint order does not change the solved result", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			Ground( doc, a );
			var forward = AssemblyFixedJointSolver.Solve( doc );
			Require( forward.Success, "forward solve must succeed" );
			doc.Joints.Reverse();
			// Swap the endpoints of each joint too: same constraints, reversed storage.
			foreach ( var joint in doc.Joints )
			{
				( joint.OccurrenceA, joint.OccurrenceB ) = ( joint.OccurrenceB, joint.OccurrenceA );
				( joint.FrameA, joint.FrameB ) = ( joint.FrameB, joint.FrameA );
			}
			var reversed = AssemblyFixedJointSolver.Solve( doc );
			Require( reversed.Success, "reversed solve must succeed" );
			foreach ( var id in new[] { a, b, c } )
				NearXform( reversed.SolvedTransforms[id], forward.SolvedTransforms[id], 1e-4f, $"reversed solve must match for {id}" );
		} );

		Check( "disconnected components solve independently", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			// Drop the B-C joint: {A,B} and {C} are separate components.
			doc.Joints.RemoveAt( 1 );
			Ground( doc, a );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve must succeed" );
			Require( result.Components.Count == 2, $"expected 2 components, got {result.Components.Count}" );
			NearXform( result.SolvedTransforms[c], Find( doc, c ).Transform.ToXform(), 0f, "lone C keeps stored transform" );
			FramesCoincide( result, doc.Joints[0] );
		} );

		Check( "scaled occurrence solves and child inherits the scale", () =>
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Link", SourceKind = "effigy-file", SourceReference = "parts/link.effigy#body-1" };
			doc.Definitions.Add( def );
			var a = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "A", Transform = AssemblyTransform.FromXform( Xform.Scale( new Vec3( 2, 2, 2 ) ) * Xform.Translate( new Vec3( 1, 0, 0 ) ) ) };
			var b = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "B", Transform = new AssemblyTransform() };
			doc.Occurrences.Add( a );
			doc.Occurrences.Add( b );
			doc.Joints.Add( new AssemblyJoint { Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = a.Id, OccurrenceB = b.Id, FrameA = new AssemblyTransform(), FrameB = new AssemblyTransform() } );
			Ground( doc, a.Id );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "scaled solve must succeed" );
			Near( result.SolvedTransforms[b.Id].Determinant, 8f, 1e-4f, "child must inherit scale (det 8)" );
			FramesCoincide( result, doc.Joints[0] );
		} );

		Check( "mirrored occurrence keeps det -1 and is never silently fixed", () =>
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Link", SourceKind = "effigy-file", SourceReference = "parts/link.effigy#body-1" };
			doc.Definitions.Add( def );
			var a = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "A", Transform = AssemblyTransform.FromXform( Xform.Mirror( Vec3.Zero, new Vec3( 1, 0, 0 ) ) ) };
			var b = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "B", Transform = new AssemblyTransform() };
			doc.Occurrences.Add( a );
			doc.Occurrences.Add( b );
			doc.Joints.Add( new AssemblyJoint { Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = a.Id, OccurrenceB = b.Id, FrameA = new AssemblyTransform(), FrameB = new AssemblyTransform() } );
			Ground( doc, a.Id );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "mirrored solve must succeed" );
			Near( result.SolvedTransforms[b.Id].Determinant, -1f, 1e-4f, "child must keep the mirror (det -1)" );
			FramesCoincide( result, doc.Joints[0] );
		} );

		Check( "NaN and singular transforms fail as invalid-transform", () =>
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Link", SourceKind = "effigy-file", SourceReference = "parts/link.effigy#body-1" };
			doc.Definitions.Add( def );
			var bad = new AssemblyTransform { X = new[] { 1f, 0f, 0f }, Y = new[] { 0f, 1f, 0f }, Z = new[] { 0f, 0f, 1f }, Origin = new[] { float.NaN, 0f, 0f } };
			doc.Occurrences.Add( new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Bad", Transform = bad } );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( !result.Success, "NaN transform must fail" );
			Require( HasError( result, "invalid-transform" ), "must report invalid-transform" );

			doc.Occurrences[0].Transform = new AssemblyTransform { X = new[] { 0f, 0f, 0f }, Y = new[] { 0f, 1f, 0f }, Z = new[] { 0f, 0f, 1f }, Origin = new[] { 0f, 0f, 0f } };
			result = AssemblyFixedJointSolver.Solve( doc );
			Require( !result.Success, "singular transform must fail" );
			Require( HasError( result, "invalid-transform" ), "must report invalid-transform for singular basis" );
		} );

		Check( "non-rigid connector frame fails as invalid-connector-frame", () =>
		{
			var (doc, a, b, c) = ChainDoc();
			doc.Joints[0].FrameA = new AssemblyTransform { X = new[] { 2f, 0f, 0f }, Y = new[] { 0f, 1f, 0f }, Z = new[] { 0f, 0f, 1f }, Origin = new[] { 0f, 0f, 0f } };
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( !result.Success, "stretched frame must fail" );
			Require( HasError( result, "invalid-connector-frame" ), "must report invalid-connector-frame" );
		} );

		Check( "free degrees of freedom minimise over all paths", () =>
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Link", SourceKind = "effigy-file", SourceReference = "parts/link.effigy#body-1" };
			doc.Definitions.Add( def );
			string Occ( string name )
			{
				var occ = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = name };
				doc.Occurrences.Add( occ );
				return occ.Id;
			}
			var ground = Occ( "Ground" );
			var mid = Occ( "Mid" );
			var leaf = Occ( "Leaf" );
			var lone = Occ( "Lone" );
			void Joint( string kind, string x, string y ) =>
				doc.Joints.Add( new AssemblyJoint { Id = AssemblyDocument.NewId(), Kind = kind, OccurrenceA = x, OccurrenceB = y } );
			Ground( doc, ground );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, ground ) == 0, "grounded is 0" );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, lone ) == 6, "no path to ground is 6" );
			Joint( "fixed", ground, mid );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, mid ) == 0, "fixed to ground is 0" );
			Joint( "hinge", mid, leaf );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, leaf ) == 1, "one hinge is 1" );
			// A parallel fixed path constrains the leaf even though the hinge hop is shorter.
			Joint( "fixed", ground, leaf );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, leaf ) == 0, "parallel fixed path leaves 0 free" );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, "" ) == 6, "empty id is 6" );
		} );

		Check( "legacy mate math matches AssemblyMateFeature", () =>
		{
			Require( AssemblyLegacyMate.KindName( 1 ) == "Coincident centers", "kind 1 name" );
			Require( AssemblyLegacyMate.KindName( 2 ) == "Center distance", "kind 2 name" );
			Require( AssemblyLegacyMate.KindName( 0 ) == "Fixed centers", "kind 0 name" );
			var offset = new Vec3( 1, 2, 3 );
			NearVec( AssemblyLegacyMate.MateOffset( 1, offset, 0, 7.5f ), Vec3.Zero, 0f, "coincident offset is zero" );
			NearVec( AssemblyLegacyMate.MateOffset( 2, offset, 2, 7.5f ), new Vec3( 0, 0, 7.5f ), 0f, "center distance axis 2" );
			NearVec( AssemblyLegacyMate.MateOffset( 0, offset, 0, 7.5f ), offset, 0f, "fixed centers keeps offset" );
			NearVec( AssemblyLegacyMate.MateTargetCentroid( new Vec3( 1, 1, 1 ), 2, offset, 2, 7.5f ), new Vec3( 1, 1, 8.5f ), 0f, "mate target centroid" );
			NearVec( AssemblyLegacyMate.MateTranslation( new Vec3( 1, 1, 1 ), new Vec3( 0, 0, 0 ), 0, offset, 0, 0f ), new Vec3( 2, 3, 4 ), 0f, "mate translation" );
		} );
	}
}
