using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS07: face and axis mating operations.
// Mirrors Mobile app/AssemblyFaceMateChecks.cs + app/AssemblyAxisMateChecks.cs
// (non-UI assertions) in Desktop form: fixtures are in-memory box meshes served
// through the AS04 AssemblySourceResolver delegate; checks compare transforms,
// separations and diagnostics, never only a success flag. The Desktop-closed gap
// (grounded B refused by an axis mate) has its own check. Not runtime verified:
// the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyFaceAxisMateTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-mates", name, test );
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
		void NearTransform( Xform a, Xform b, float eps, string what )
		{
			NearVec( a.Origin, b.Origin, eps, what + ".origin" );
			Near( Vec3.Dot( a.X, b.X ), 1f, eps, what + ".X" );
			Near( Vec3.Dot( a.Y, b.Y ), 1f, eps, what + ".Y" );
			Near( Vec3.Dot( a.Z, b.Z ), 1f, eps, what + ".Z" );
		}
		bool ExactVec( Vec3 a, Vec3 b ) => a.x == b.x && a.y == b.y && a.z == b.z;
		bool ExactTransform( Xform a, Xform b ) =>
			ExactVec( a.Origin, b.Origin ) && ExactVec( a.X, b.X ) && ExactVec( a.Y, b.Y ) && ExactVec( a.Z, b.Z );

		// ---- Fixtures ----

		// Box w x h x d, corners 0..7, faces in order +Z, -Z, +X, -X, +Y, -Y,
		// wound outward. Face 0 is the +Z top.
		PolyMesh BoxMesh( float w, float h, float d )
		{
			var mesh = new PolyMesh();
			mesh.AddVertex( new Vec3( 0, 0, 0 ) );
			mesh.AddVertex( new Vec3( w, 0, 0 ) );
			mesh.AddVertex( new Vec3( w, h, 0 ) );
			mesh.AddVertex( new Vec3( 0, h, 0 ) );
			mesh.AddVertex( new Vec3( 0, 0, d ) );
			mesh.AddVertex( new Vec3( w, 0, d ) );
			mesh.AddVertex( new Vec3( w, h, d ) );
			mesh.AddVertex( new Vec3( 0, h, d ) );
			mesh.AddFace( new[] { 4, 5, 6, 7 } );
			mesh.AddFace( new[] { 3, 2, 1, 0 } );
			mesh.AddFace( new[] { 1, 2, 6, 5 } );
			mesh.AddFace( new[] { 0, 4, 7, 3 } );
			mesh.AddFace( new[] { 3, 7, 6, 2 } );
			mesh.AddFace( new[] { 0, 1, 5, 4 } );
			return mesh;
		}

		PolyMesh FlatTriangleMesh()
		{
			var mesh = new PolyMesh();
			mesh.AddVertex( new Vec3( 0, 0, 0 ) );
			mesh.AddVertex( new Vec3( 1, 0, 0 ) );
			mesh.AddVertex( new Vec3( 0, 0, 1 ) );
			mesh.AddFace( new[] { 0, 1, 2 } );
			return mesh;
		}

		PolyMesh BentQuadMesh()
		{
			var mesh = new PolyMesh();
			mesh.AddVertex( new Vec3( 0, 0, 0 ) );
			mesh.AddVertex( new Vec3( 1, 0, 0 ) );
			mesh.AddVertex( new Vec3( 1, 1, 0 ) );
			mesh.AddVertex( new Vec3( 0, 1, 0.5f ) );
			mesh.AddFace( new[] { 0, 1, 2, 3 } );
			return mesh;
		}

		// Two "Plate" definitions, occA at the identity, occB at Translate(5,0,0).
		(AssemblyDocument Doc, AssemblyOccurrence A, AssemblyOccurrence B, Dictionary<string, PolyMesh> Meshes, AssemblySourceResolver Source) Fresh()
		{
			var doc = new AssemblyDocument();
			var defA = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Plate", SourceKind = "effigy-file", SourceReference = "parts/a.effigy#body-a" };
			var defB = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Plate", SourceKind = "effigy-file", SourceReference = "parts/b.effigy#body-b" };
			doc.Definitions.Add( defA );
			doc.Definitions.Add( defB );
			var occA = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = defA.Id, Name = "Plate A", Transform = AssemblyTransform.FromXform( Xform.Identity ) };
			var occB = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = defB.Id, Name = "Plate B", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 5, 0, 0 ) ) ) };
			doc.Occurrences.Add( occA );
			doc.Occurrences.Add( occB );
			var meshes = new Dictionary<string, PolyMesh> { [defA.Id] = BoxMesh( 2, 2, 2 ), [defB.Id] = BoxMesh( 2, 2, 2 ) };
			AssemblySourceResolver source = def =>
			{
				if ( def == null || !meshes.TryGetValue( def.Id, out var mesh ) ) return null;
				return (mesh, 1, "steel");
			};
			return (doc, occA, occB, meshes, source);
		}

		AssemblyMateFace TopFace( AssemblyDocument doc, AssemblySourceResolver source, AssemblyOccurrence occ )
		{
			Require( AssemblyFaceMate.TryFromFace( doc, source, occ.Id, 0, out var face, out var why ), why ?? "top face" );
			return face;
		}

		// Separation along A's face normal plus B's world outward normal, measured
		// from the live face frames — the same measure Mobile's checks use.
		(float Separation, Vec3 NormalB) Measure( AssemblyDocument doc, AssemblySourceResolver source, AssemblyMateFace faceA, AssemblyMateFace faceB, Xform transformB )
		{
			Require( AssemblyFaceMate.TryResolveFaceFrame( doc, source, faceA, out var frameA, out _, out var whyA ), whyA ?? "resolve A" );
			Require( AssemblyFaceMate.TryResolveFaceFrame( doc, source, faceB, out var frameB, out _, out var whyB ), whyB ?? "resolve B" );
			var occA = doc.Occurrences.First( o => o.Id == faceA.OccurrenceId );
			var worldA = occA.Transform.ToXform() * frameA;
			var originA = worldA.Origin;
			var normalA = AssemblyFaceMate.TransformNormal( occA.Transform.ToXform(), frameA.Z );
			var originB = transformB.TransformPoint( frameB.Origin );
			return (Vec3.Dot( originB - originA, normalA ), AssemblyFaceMate.TransformNormal( transformB, frameB.Z ));
		}

		string FirstCode( FaceMateResult result ) =>
			result.Diagnostics.Length > 0 ? result.Diagnostics[0].Code : "(no diagnostic)";

		// ---- Face mates ----

		Check( "box faces point outward", () =>
		{
			var box = BoxMesh( 2, 2, 2 );
			var expected = new[]
			{
				new Vec3( 0, 0, 1 ), new Vec3( 0, 0, -1 ), new Vec3( 1, 0, 0 ),
				new Vec3( -1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, -1, 0 ),
			};
			Require( box.Faces.Count == 6, "box has 6 faces" );
			for ( var i = 0; i < 6; i++ )
				NearVec( box.FaceNormal( box.Faces[i] ), expected[i], 1e-6f, $"face {i} normal" );
		} );

		Check( "opposed at zero offset is coincident with normals against each other", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var beforeA = occA.Transform.ToXform();
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( result.Ok, FirstCode( result ) );
			Require( result.NewTransformB.HasValue, "solved transform" );
			var (separation, normalB) = Measure( doc, source, faceA, faceB, result.NewTransformB.Value );
			Near( separation, 0f, 1e-4f, "separation" );
			NearVec( normalB, new Vec3( 0, 0, -1 ), 1e-4f, "B normal" );
			Require( ExactTransform( occA.Transform.ToXform(), beforeA ), "anchor A never moves" );
			Require( MathF.Abs( result.NewTransformB.Value.Determinant - 1f ) < 1e-4f, "solved transform is rigid" );
		} );

		Check( "offset 0.5 separates the faces by 0.5", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in" );
			Require( result.Ok, FirstCode( result ) );
			var (separation, normalB) = Measure( doc, source, faceA, faceB, result.NewTransformB.Value );
			Near( separation, 0.5f, 1e-4f, "separation" );
			NearVec( normalB, new Vec3( 0, 0, -1 ), 1e-4f, "B normal" );
			Near( result.SeparationInches, 0.5f, 1e-6f, "reported separation" );
		} );

		Check( "same-facing at 0.25 keeps normals parallel", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "same", 0.25f, "in" );
			Require( result.Ok, FirstCode( result ) );
			var (separation, normalB) = Measure( doc, source, faceA, faceB, result.NewTransformB.Value );
			Near( separation, 0.25f, 1e-4f, "separation" );
			NearVec( normalB, new Vec3( 0, 0, 1 ), 1e-4f, "B normal" );
		} );

		Check( "25.4 mm equals 2.54 cm equals 1 in", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var inches = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 1f, "in" );
			var mm = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 25.4f, "mm" );
			var cm = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 2.54f, "cm" );
			Require( inches.Ok && mm.Ok && cm.Ok, "all three units solve" );
			NearTransform( inches.NewTransformB.Value, mm.NewTransformB.Value, 1e-5f, "mm vs in" );
			NearTransform( inches.NewTransformB.Value, cm.NewTransformB.Value, 1e-5f, "cm vs in" );
		} );

		Check( "rotated B aligns rigidly", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			occB.Transform = AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI / 2f ) * Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( result.Ok, FirstCode( result ) );
			var (separation, normalB) = Measure( doc, source, faceA, faceB, result.NewTransformB.Value );
			Near( separation, 0f, 1e-4f, "separation" );
			NearVec( normalB, new Vec3( 0, 0, -1 ), 1e-4f, "B normal" );
			Near( result.NewTransformB.Value.Determinant, 1f, 1e-4f, "determinant" );
		} );

		Check( "flipped winding follows the actual outward normal", () =>
		{
			var (doc, occA, occB, meshes, source) = Fresh();
			var flipped = meshes[occB.DefinitionId];
			foreach ( var face in flipped.Faces ) face.Indices = face.Indices.Reverse().ToArray();
			NearVec( flipped.FaceNormal( flipped.Faces[0] ), new Vec3( 0, 0, -1 ), 1e-6f, "flipped top normal" );
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( result.Ok, FirstCode( result ) );
			var (separation, normalB) = Measure( doc, source, faceA, faceB, result.NewTransformB.Value );
			Near( separation, 0f, 1e-4f, "separation" );
			NearVec( normalB, new Vec3( 0, 0, -1 ), 1e-4f, "B normal follows the flipped outward normal" );
		} );

		Check( "grounded B is refused and applying the refusal changes nothing", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			AssemblyGrounding.SetGrounded( doc, occB.Id, true );
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( !result.Ok, "grounded B must fail" );
			Require( FirstCode( result ) == "grounded-part", $"code was {FirstCode( result )}" );
			Require( result.NewTransformB == null, "no target for a refused mate" );
			var history = new EditHistory();
			var beforeB = occB.Transform.ToXform();
			Require( !AssemblyFaceMate.ApplyToDocument( doc, history, result ), "applying a refused mate is false" );
			Require( doc.Joints.Count == 0, "no joint from a refused mate" );
			Require( ExactTransform( occB.Transform.ToXform(), beforeB ), "B unmoved" );
		} );

		Check( "session swap sides anchors the grounded part instead", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var session = new AssemblyFaceMateSession( doc, source, faceA, faceB, occ => occ.Id == occB.Id );
			Require( !session.CanApply, "grounded B must not apply" );
			session.SwapSides();
			Require( session.CanApply, "after swapping sides the ungrounded part moves" );
			Require( session.PreviewOccurrenceId == occA.Id, "preview now moves A" );
		} );

		Check( "both grounded and already aligned is a no-op success", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var applied = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( applied.Ok, FirstCode( applied ) );
			Require( AssemblyFaceMate.ApplyToDocument( doc, null, applied ), "apply the mate" );
			AssemblyGrounding.SetGrounded( doc, occA.Id, true );
			AssemblyGrounding.SetGrounded( doc, occB.Id, true );
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( result.Ok, FirstCode( result ) );
			NearTransform( result.NewTransformB.Value, occB.Transform.ToXform(), 1e-5f, "no-op target is where B already sits" );
		} );

		Check( "both grounded and not aligned is refused", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			AssemblyGrounding.SetGrounded( doc, occA.Id, true );
			AssemblyGrounding.SetGrounded( doc, occB.Id, true );
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 1f, "in" );
			Require( !result.Ok, "both grounded with an offset must fail" );
			Require( FirstCode( result ) == "both-grounded", $"code was {FirstCode( result )}" );
		} );

		Check( "invalid inputs are refused with stable codes", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			Require( FirstCode( AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", float.NaN, "in" ) ) == "invalid-offset", "NaN offset" );
			Require( FirstCode( AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 1f, "furlongs" ) ) == "invalid-unit", "bad unit" );
			Require( FirstCode( AssemblyFaceMate.Solve( doc, source, faceA, faceB, "sideways", 1f, "in" ) ) == "invalid-alignment", "bad alignment" );
		} );

		Check( "self-mate is refused", () =>
		{
			var (doc, occA, _, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceA, "opposed", 0f, "in" );
			Require( !result.Ok, "same face twice must fail" );
			Require( FirstCode( result ) == "self-mate", $"code was {FirstCode( result )}" );
		} );

		Check( "missing source body is refused", () =>
		{
			var (doc, occA, occB, meshes, source) = Fresh();
			meshes.Remove( occB.DefinitionId );
			var faceA = TopFace( doc, source, occA );
			// B's face reference is built by hand: the mesh is gone, so TryFromFace cannot run.
			var faceB = new AssemblyMateFace { OccurrenceId = occB.Id, Ref = new FaceRef( "body-b", new Vec3( 1, 1, 2 ), new Vec3( 0, 0, 1 ) ) };
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( !result.Ok, "missing body must fail" );
			Require( FirstCode( result ) == "missing-source-body", $"code was {FirstCode( result )}" );
		} );

		Check( "non-planar face is refused", () =>
		{
			var (doc, occA, occB, meshes, source) = Fresh();
			meshes[occB.DefinitionId] = BentQuadMesh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( !result.Ok, "bent quad must fail" );
			Require( FirstCode( result ) == "face-not-planar", $"code was {FirstCode( result )}" );
		} );

		Check( "re-solving follows a moved face", () =>
		{
			var (doc, occA, occB, meshes, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var stale = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( stale.Ok, FirstCode( stale ) );
			var staleOrigin = stale.NewTransformB.Value.Origin;
			meshes[occB.DefinitionId] = BoxMesh( 2, 2, 4 );
			var moved = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0f, "in" );
			Require( moved.Ok, FirstCode( moved ) );
			// B is 4 tall now: its top face sits at local z=4, so B's origin moves up
			// (the mate is a 180-degree turn about X, so the origin lands at z=6) to
			// keep the faces coincident at A's top (z=2).
			Near( moved.NewTransformB.Value.Origin.z, 6f, 1e-4f, "re-solved B origin z" );
			Require( (moved.NewTransformB.Value.Origin - staleOrigin).Length > 0.5f, "stale transform is far off" );
			var (separation, _) = Measure( doc, source, faceA, faceB, moved.NewTransformB.Value );
			Near( separation, 0f, 1e-4f, "re-solved faces still coincident" );
			Require( AssemblyFaceMate.TryResolveFaceFrame( doc, source, faceB, out var frame, out _, out _ ), "tall face resolves" );
			NearVec( frame.Origin, new Vec3( 1, 1, 4 ), 1e-4f, "face frame follows the taller face" );
		} );

		Check( "identical second mate agrees, different offset and swapped sides conflict", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var first = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in" );
			Require( first.Ok, FirstCode( first ) );
			Require( AssemblyFaceMate.ApplyToDocument( doc, null, first ), "apply the first mate" );
			var agree = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in", null, AssemblyDocument.NewId() );
			Require( agree.Ok, $"identical mate must agree, got {FirstCode( agree )}" );
			var clash = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 1f, "in", null, AssemblyDocument.NewId() );
			Require( !clash.Ok && FirstCode( clash ) == "conflicting-mates", $"different offset must conflict, got {FirstCode( clash )}" );
			var flipped = AssemblyFaceMate.Solve( doc, source, faceB, faceA, "opposed", 0.5f, "in", null, AssemblyDocument.NewId() );
			Require( !flipped.Ok && FirstCode( flipped ) == "conflicting-mates", $"swapped sides must conflict, got {FirstCode( flipped )}" );
		} );

		Check( "edit in place keeps one joint and stores the new offset", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var first = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in" );
			Require( AssemblyFaceMate.ApplyToDocument( doc, null, first ), "apply the first mate" );
			Require( doc.Joints.Count == 1, "one joint" );
			Require( AssemblyFaceMate.TryReadStored( doc.Joints[0], out var storedA, out var storedB, out _, out _, out _, out _ ), "read the stored mate" );
			var session = new AssemblyFaceMateSession( doc, source, storedA, storedB, null, null, doc.Joints[0].Id );
			session.Update( "opposed", 1f, "in" );
			Require( session.CanApply, "edited mate applies" );
			Require( session.Apply(), "apply the edit" );
			Require( doc.Joints.Count == 1, "still one joint after the edit" );
			Require( AssemblyFaceMate.TryReadStored( doc.Joints[0], out _, out _, out var align, out var offset, out var unit, out _ ), "read the edited mate" );
			Require( align == "opposed" && offset == 1f && unit == "in", $"stored {align} {offset} {unit}" );
			var (separation, _) = Measure( doc, source, faceA, faceB, occB.Transform.ToXform() );
			Near( separation, 1f, 1e-4f, "B sits at the new offset" );
		} );

		Check( "solving twice is bit-identical", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var first = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in" );
			var second = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in" );
			Require( first.Ok && second.Ok, "both solve" );
			Require( ExactTransform( first.NewTransformB.Value, second.NewTransformB.Value ), "identical transforms" );
		} );

		Check( "preview and cancel never mutate the document", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var beforeB = occB.Transform.ToXform();
			var session = new AssemblyFaceMateSession( doc, source, faceA, faceB );
			Require( session.PreviewTransform.HasValue, "preview exists" );
			Require( session.CanApply, "preview can apply" );
			Require( doc.Joints.Count == 0, "0 joints while previewing" );
			Require( ExactTransform( occB.Transform.ToXform(), beforeB ), "B unmoved while previewing" );
			session.Cancel();
			Require( !session.CanApply, "cancelled session cannot apply" );
			Require( !session.Apply(), "apply after cancel is false" );
			Require( doc.Joints.Count == 0, "0 joints after cancel" );
			Require( ExactTransform( occB.Transform.ToXform(), beforeB ), "B unmoved after cancel" );
		} );

		Check( "undo removes the joint and restores B, redo restores both", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var history = new EditHistory();
			var beforeB = occB.Transform.ToXform();
			var result = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in" );
			Require( AssemblyFaceMate.ApplyToDocument( doc, history, result ), "apply the mate" );
			Require( doc.Joints.Count == 1, "one joint after apply" );
			history.Undo();
			Require( doc.Joints.Count == 0, "0 joints after undo" );
			var undoneB = doc.Occurrences.First( o => o.Id == occB.Id );
			Require( ExactTransform( undoneB.Transform.ToXform(), beforeB ), "B restored exactly after undo" );
			history.Redo();
			Require( doc.Joints.Count == 1, "one joint after redo" );
			var redoneB = doc.Occurrences.First( o => o.Id == occB.Id );
			NearTransform( redoneB.Transform.ToXform(), result.NewTransformB.Value, 1e-6f, "B restored after redo" );
		} );

		Check( "face-mate solve matches the AS05 fixed-joint equation", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var mate = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in" );
			Require( mate.Ok, FirstCode( mate ) );
			Require( AssemblyFaceMate.TryResolveFaceFrame( doc, source, faceA, out var localFrameA, out _, out _ ), "frame A" );
			Require( AssemblyFaceMate.TryResolveFaceFrame( doc, source, faceB, out var localFrameB, out _, out _ ), "frame B" );
			var flippedB = new Xform( localFrameB.X * -1f, localFrameB.Y, localFrameB.Z * -1f, localFrameB.Origin );
			var draft = new AssemblyJointDraft
			{
				ConnectorA = new AssemblyConnectorRef( occA.Id, AssemblyTransform.FromXform( localFrameA ), "face", "Top face A", "face:0" ),
				ConnectorB = new AssemblyConnectorRef( occB.Id, AssemblyTransform.FromXform( flippedB ), "face", "Top face B", "face:0" ),
				Offset = new Vec3( 0, 0, 0.5f ),
			};
			var preview = AssemblyJointService.Preview( doc, draft );
			Require( preview.Ok, preview.Message ?? "AS05 preview" );
			Require( preview.HasProposed, "AS05 proposes a transform" );
			NearTransform( preview.ProposedTransform, mate.NewTransformB.Value, 1e-5f, "face mate vs AS05 preview" );
		} );

		Check( "save and reopen keeps the mate and the legacy centroid mate", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var solved = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in" );
			Require( solved.Ok, FirstCode( solved ) );
			Require( AssemblyFaceMate.ApplyToDocument( doc, null, solved ), "apply the mate" );
			var appliedTransform = occB.Transform.ToXform();
			var studio = new PartStudio();
			studio.Assembly = doc;
			var legacy = new AssemblyMateFeature();
			legacy.Kind.Index = 2;
			legacy.Distance.Value = 5f;
			legacy.Bodies.BodyIds.Add( "body-a" );
			legacy.Bodies.BodyIds.Add( "body-b" );
			studio.Features.Add( legacy );
			var text = StudioDocument.Write( studio );
			var restored = StudioDocument.Read( text );
			var restoredDoc = restored.Assembly;
			Require( restoredDoc.Joints.Count == 1, "one joint after reopen" );
			Require( AssemblyFaceMate.TryReadStored( restoredDoc.Joints[0], out var storedA, out var storedB, out var align, out var offset, out var unit, out _ ),
				"read the stored mate after reopen" );
			Require( align == "opposed" && offset == 0.5f && unit == "in", $"stored {align} {offset} {unit}" );
			var reSolved = AssemblyFaceMate.Solve( restoredDoc, source, storedA, storedB, align, offset, unit );
			Require( reSolved.Ok, FirstCode( reSolved ) );
			NearTransform( reSolved.NewTransformB.Value, appliedTransform, 1e-5f, "re-solved transform after reopen" );
			var restoredLegacy = restored.Features.OfType<AssemblyMateFeature>().FirstOrDefault();
			Require( restoredLegacy != null, "legacy mate survives" );
			Require( restoredLegacy.Kind.Index == 2, "legacy kind survives" );
			Near( restoredLegacy.Distance.Value, 5f, 1e-6f, "legacy distance survives" );
			Require( restoredDoc.Validate().Count == 0, "document with face mate and legacy mate validates clean" );
		} );

		Check( "ValidateStored reports face-invalid after the face is deleted", () =>
		{
			var (doc, occA, occB, meshes, source) = Fresh();
			var faceA = TopFace( doc, source, occA );
			var faceB = TopFace( doc, source, occB );
			var solved = AssemblyFaceMate.Solve( doc, source, faceA, faceB, "opposed", 0.5f, "in" );
			Require( AssemblyFaceMate.ApplyToDocument( doc, null, solved ), "apply the mate" );
			Require( AssemblyFaceMate.ValidateStored( doc, source, doc.Joints[0] ).Length == 0, "clean while both faces resolve" );
			meshes[occB.DefinitionId] = FlatTriangleMesh();
			var issues = AssemblyFaceMate.ValidateStored( doc, source, doc.Joints[0] );
			Require( issues.Any( i => i.Code == "face-invalid" ), $"expected face-invalid, got {string.Join( ",", issues.Select( i => i.Code ) )}" );
			meshes[occB.DefinitionId] = BoxMesh( 2, 2, 2 );
			Require( AssemblyFaceMate.ValidateStored( doc, source, doc.Joints[0] ).Length == 0, "clean again once the face is back" );
		} );

		// ---- Axis mates ----

		// One "Shaft plate" definition, occA at the identity, occB at Translate(5,3,2).
		(AssemblyDocument Doc, AssemblyOccurrence A, AssemblyOccurrence B, AssemblySourceResolver Source) AxisFresh()
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Shaft plate", SourceKind = "effigy-file", SourceReference = "parts/shaft.effigy#plate" };
			doc.Definitions.Add( def );
			var occA = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate A", Transform = AssemblyTransform.FromXform( Xform.Identity ) };
			var occB = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate B", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 5, 3, 2 ) ) ) };
			doc.Occurrences.Add( occA );
			doc.Occurrences.Add( occB );
			var mesh = BoxMesh( 2, 2, 2 );
			AssemblySourceResolver source = d => d != null && d.Id == def.Id ? (mesh, 1, "steel") : null;
			return (doc, occA, occB, source);
		}

		AxisMate StraightMate( AssemblyOccurrence a, AssemblyOccurrence b, float offset = 0f, bool flipped = false ) => new()
		{
			Name = "Shaft mate",
			OccurrenceA = a.Id,
			OccurrenceB = b.Id,
			SourceA = AssemblyMateAxisSources.FromCylindricalFace( a.Id, "face:3", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 2.5f ),
			SourceB = AssemblyMateAxisSources.FromCircularEdge( b.Id, "edge:9", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 1.0f ),
			AxialOffset = offset,
			Flipped = flipped,
		};

		// B's frame origin after a solve, and the angle between B's axis and A's.
		(float OffLine, float Dot, float Along) AxisMeasure( AssemblyDocument doc, AxisMate mate, Xform solvedB )
		{
			Require( AxisMateSolver.TryWorldFrames( doc, mate, out var worldA, out _, out _, out var frameB, out _ ), "world frames" );
			var axisA = worldA.Z.Normal;
			var originB = solvedB.TransformPoint( frameB.Origin );
			var offLine = Vec3.Cross( originB - worldA.Origin, axisA ).Length;
			var dot = Vec3.Dot( solvedB.TransformDirection( frameB.Z ).Normal, axisA );
			var along = Vec3.Dot( originB - worldA.Origin, axisA );
			return (offLine, dot, along);
		}

		Check( "axis frames come from every source kind", () =>
		{
			var edge = AssemblyMateAxisSources.FromStraightEdge( "occ", "edge:1", new Vec3( 1, 2, 3 ), new Vec3( 0, 1, 0 ) );
			var edgeFrame = AssemblyMateAxisSources.TryToFrame( edge );
			Require( edgeFrame.HasValue, "straight edge frame" );
			NearVec( edgeFrame.Value.Z, new Vec3( 0, 1, 0 ), 1e-6f, "edge +Z along the edge" );
			NearVec( edgeFrame.Value.Origin, new Vec3( 1, 2, 3 ), 1e-6f, "edge origin" );
			Near( edgeFrame.Value.Determinant, 1f, 1e-6f, "edge frame rigid" );
			var circle = AssemblyMateAxisSources.FromCircularEdge( "occ", "edge:2", new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), 4f );
			Require( AssemblyMateAxisSources.TryToFrame( circle ).HasValue, "circular edge frame" );
			Near( circle.Radius.Value, 4f, 1e-6f, "circular radius" );
			var cylinder = AssemblyMateAxisSources.FromCylindricalFace( "occ", "face:3", new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), 2.5f );
			Require( AssemblyMateAxisSources.TryToFrame( cylinder ).HasValue, "cylindrical face frame" );
			Near( cylinder.Radius.Value, 2.5f, 1e-6f, "cylindrical radius" );
			var explicitAxis = AssemblyMateAxisSources.FromExplicitAxis( "occ", "explicit:shaft", new Vec3( 1, 1, 1 ), new Vec3( 0, 0, -1 ) );
			var explicitFrame = AssemblyMateAxisSources.TryToFrame( explicitAxis );
			Require( explicitFrame.HasValue, "explicit axis frame" );
			NearVec( explicitFrame.Value.Z, new Vec3( 0, 0, -1 ), 1e-6f, "explicit +Z along the axis" );
		} );

		Check( "mesh-only pick is refused as an axis", () =>
		{
			var mesh = AssemblyMateAxisSources.UnsupportedMesh( "occ", "mesh:0" );
			Require( !mesh.TryValidate( out var meshError ) && meshError != null && meshError.StartsWith( "unsupported-geometry", StringComparison.Ordinal ),
				$"mesh pick must be unsupported-geometry, got {meshError}" );
			Require( AssemblyMateAxisSources.TryToFrame( mesh ) == null, "mesh pick has no frame" );
		} );

		Check( "rotated axes solve coaxially", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var mate = new AxisMate
			{
				Name = "Rotated mate",
				OccurrenceA = occA.Id,
				OccurrenceB = occB.Id,
				SourceA = AssemblyMateAxisSources.FromCylindricalFace( occA.Id, "face:3", new Vec3( 1, 2, 3 ), new Vec3( 1, 1, 1 ), 2.5f ),
				SourceB = AssemblyMateAxisSources.FromCircularEdge( occB.Id, "edge:9", new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ), 1.0f ),
				AxialOffset = 1.5f,
			};
			Require( AxisMateSolver.TrySolve( doc, mate, out var solvedB, out var diag, out var error ), error ?? "rotated solve" );
			var (offLine, dot, along) = AxisMeasure( doc, mate, solvedB );
			Near( offLine, 0f, 1e-3f, "B axis on A's line" );
			Near( dot, 1f, 1e-4f, "axes parallel" );
			Near( along, 1.5f, 1e-4f, "axial offset" );
			Require( diag.RadiusA == 2.5f && diag.RadiusB == 1.0f, "radii reported" );
			Require( diag.SpinPreserved, "spin preserved" );
			Require( diag.AnchorOccurrenceId == occA.Id, "anchor is A" );
			Require( ExactTransform( occA.Transform.ToXform(), Xform.Identity ), "anchor A unmoved by a pure solve" );
		} );

		Check( "opposite-direction connector lands on the same axis line", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var mate = StraightMate( occA, occB );
			mate.SourceB = AssemblyMateAxisSources.FromCircularEdge( occB.Id, "edge:9", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, -1 ), 1.0f );
			Require( AxisMateSolver.TrySolve( doc, mate, out var solvedB, out _, out var error ), error ?? "opposite solve" );
			var (offLine, dot, _) = AxisMeasure( doc, mate, solvedB );
			Near( offLine, 0f, 1e-3f, "on the axis line" );
			Near( dot, 1f, 1e-4f, "without flip B ends up +Z" );
			mate.Flipped = true;
			Require( AxisMateSolver.TrySolve( doc, mate, out solvedB, out _, out error ), error ?? "flipped solve" );
			(offLine, dot, _) = AxisMeasure( doc, mate, solvedB );
			Near( offLine, 0f, 1e-3f, "flipped still on the axis line" );
			Near( dot, -1f, 1e-4f, "with flip B is antiparallel" );
		} );

		Check( "offset signs land B at A origin plus offset along the axis", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			foreach ( var offset in new[] { 3f, -2f } )
			{
				var mate = StraightMate( occA, occB, offset );
				Require( AxisMateSolver.TrySolve( doc, mate, out var solvedB, out _, out var error ), error ?? "offset solve" );
				var (_, _, along) = AxisMeasure( doc, mate, solvedB );
				Near( along, offset, 1e-4f, $"offset {offset}" );
			}
		} );

		Check( "default offset is the current axial separation", () =>
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Shaft plate", SourceKind = "effigy-file", SourceReference = "parts/shaft.effigy#plate" };
			doc.Definitions.Add( def );
			var occA = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate A", Transform = AssemblyTransform.FromXform( Xform.Identity ) };
			var occB = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate B", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 4, -2, 7 ) ) ) };
			doc.Occurrences.Add( occA );
			doc.Occurrences.Add( occB );
			var mate = StraightMate( occA, occB );
			Near( mate.CurrentAxialOffset( doc ), 7f, 1e-5f, "current separation" );
		} );

		Check( "repeated instances mate independently", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var occC = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = occB.DefinitionId, Name = "Plate C", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( -3, 1, -1 ) ) ) };
			doc.Occurrences.Add( occC );
			var mateB = StraightMate( occA, occB, 2f );
			var mateC = StraightMate( occA, occC, -1f );
			Require( mateB.ApplyTo( doc, null, out _, out _, out var errorB ), errorB ?? "apply B" );
			Require( mateC.ApplyTo( doc, null, out _, out _, out var errorC ), errorC ?? "apply C" );
			Require( doc.Joints.Count == 2, "two joints" );
			Near( occB.Transform.ToXform().Origin.z, 2f, 1e-4f, "B at its offset" );
			Near( occC.Transform.ToXform().Origin.z, -1f, 1e-4f, "C at its offset" );
		} );

		Check( "axis solve is deterministic and re-solving a solved mate corrects zero degrees", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var mate = StraightMate( occA, occB, 1.5f );
			Require( AxisMateSolver.TrySolve( doc, mate, out var first, out _, out var error ), error ?? "first solve" );
			Require( AxisMateSolver.TrySolve( doc, mate, out var second, out _, out error ), error ?? "second solve" );
			Require( ExactTransform( first, second ), "solve twice is identical" );
			Require( mate.ApplyTo( doc, null, out _, out _, out error ), error ?? "apply" );
			Require( AxisMateSolver.TrySolve( doc, mate, out _, out var diag, out error ), error ?? "re-solve" );
			Near( diag.AngleCorrectedDegrees, 0f, 1e-4f, "no correction left" );
		} );

		Check( "spin about the axis is preserved", () =>
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Shaft plate", SourceKind = "effigy-file", SourceReference = "parts/shaft.effigy#plate" };
			doc.Definitions.Add( def );
			var occA = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate A", Transform = AssemblyTransform.FromXform( Xform.Identity ) };
			var spin = Xform.Translate( new Vec3( 0, 0, 5 ) ) * Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 6f );
			var occB = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate B", Transform = AssemblyTransform.FromXform( spin ) };
			doc.Occurrences.Add( occA );
			doc.Occurrences.Add( occB );
			var mate = StraightMate( occA, occB, 5f );
			Require( AxisMateSolver.TrySolve( doc, mate, out var solvedB, out var diag, out var error ), error ?? "spin solve" );
			Near( diag.AngleCorrectedDegrees, 0f, 1e-4f, "no angle correction" );
			NearVec( solvedB.Origin, new Vec3( 0, 0, 5 ), 1e-4f, "origin stays" );
			NearVec( solvedB.X, new Vec3( MathF.Cos( MathF.PI / 6f ), MathF.Sin( MathF.PI / 6f ), 0 ), 1e-4f, "X keeps the 30-degree spin" );
		} );

		Check( "stale reference is refused", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var mate = StraightMate( occA, occB, 1f );
			var liveA = AssemblyMateAxisSources.Copy(mate.SourceA);
			Require( !AssemblyMateAxisSources.IsStaleAgainst(mate.SourceA, liveA), "fresh source is not stale" );
			mate.SourceA.Radius = 3.5f;
			Require( AssemblyMateAxisSources.IsStaleAgainst(mate.SourceA, liveA), "edited radius is stale" );
			Require( !AxisMateSolver.TrySolve( doc, mate, out _, out _, out var error ), "stale mate must not solve" );
			Require( error != null && error.StartsWith( "stale-reference", StringComparison.Ordinal ), $"error was {error}" );
		} );

		Check( "conflicting axis mate is refused and B keeps its last valid position", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var occC = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = occB.DefinitionId, Name = "Plate C", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 5, 0 ) ) ) };
			doc.Occurrences.Add( occC );
			var first = StraightMate( occA, occB, 1f );
			Require( first.ApplyTo( doc, null, out _, out _, out var error ), error ?? "apply first" );
			var beforeB = occB.Transform.ToXform();
			var conflict = new AxisMate
			{
				Name = "Other axis mate",
				OccurrenceA = occC.Id,
				OccurrenceB = occB.Id,
				SourceA = AssemblyMateAxisSources.FromCylindricalFace( occC.Id, "face:3", new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ), 2.5f ),
				SourceB = AssemblyMateAxisSources.FromCircularEdge( occB.Id, "edge:9", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 1.0f ),
				AxialOffset = 0f,
			};
			Require( !conflict.ApplyTo( doc, null, out _, out _, out error ), "conflicting mate must not apply" );
			Require( error != null && error.StartsWith( "conflict:", StringComparison.Ordinal ), $"error was {error}" );
			Require( doc.Joints.Count == 1, "still one joint" );
			Require( ExactTransform( occB.Transform.ToXform(), beforeB ), "B keeps its last valid position" );
		} );

		Check( "axis mate save and reopen keeps offset, flip, radii and fingerprints", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var mate = StraightMate( occA, occB, 2.5f, flipped: true );
			Require( mate.ApplyTo( doc, null, out var solvedB, out _, out var error ), error ?? "apply" );
			var text = doc.ToJson();
			var restored = AssemblyDocument.FromJson( text );
			Require( restored.ToJson() == text, "round-trip is byte-identical" );
			Require( restored.Joints.Count == 1, "one joint after reopen" );
			var back = AxisMate.FromJoint( restored.Joints[0], out var why );
			Require( back != null, why ?? "restore the mate" );
			Require( back.Id == mate.Id, "id survives" );
			Near( back.AxialOffset, 2.5f, 1e-6f, "offset survives" );
			Require( back.Flipped, "flip survives" );
			Require( back.SourceA.Radius == 2.5f && back.SourceB.Radius == 1.0f, "radii survive" );
			Require( back.SourceA.Fingerprint == mate.SourceA.Fingerprint, "fingerprint A survives" );
			Require( back.SourceB.Fingerprint == mate.SourceB.Fingerprint, "fingerprint B survives" );
			var issues = restored.Validate();
			Require( issues.All( i => i.Warning ), "no error issues after reopen" );
			Require( issues.Any( i => i.Code == "unsupported-joint-kind" ), "hinge joint carries its expected warning" );
			Require( AxisMateSolver.TrySolve( restored, back, out var reSolved, out _, out error ), error ?? "re-solve" );
			NearTransform( reSolved, solvedB, 1e-5f, "re-solve reproduces the applied transform" );
		} );

		Check( "axis mate undo and redo", () =>
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Shaft plate", SourceKind = "effigy-file", SourceReference = "parts/shaft.effigy#plate" };
			doc.Definitions.Add( def );
			var occA = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate A", Transform = AssemblyTransform.FromXform( Xform.Identity ) };
			var occB = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate B", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 4, 0, 0 ) ) ) };
			doc.Occurrences.Add( occA );
			doc.Occurrences.Add( occB );
			var history = new EditHistory();
			var mate = StraightMate( occA, occB, 1f );
			Require( mate.ApplyTo( doc, history, out _, out _, out var error ), error ?? "apply" );
			NearVec( occB.Transform.ToXform().Origin, new Vec3( 0, 0, 1 ), 1e-4f, "B at (0,0,1) after apply" );
			Require( doc.Joints.Count == 1, "one joint" );
			history.Undo();
			Require( doc.Joints.Count == 0, "0 joints after undo" );
			NearVec( doc.Occurrences.First( o => o.Id == occB.Id ).Transform.ToXform().Origin, new Vec3( 4, 0, 0 ), 1e-4f, "B back at (4,0,0) after undo" );
			history.Redo();
			Require( doc.Joints.Count == 1, "one joint after redo" );
			NearVec( doc.Occurrences.First( o => o.Id == occB.Id ).Transform.ToXform().Origin, new Vec3( 0, 0, 1 ), 1e-4f, "B at (0,0,1) after redo" );
		} );

		Check( "axis solve is a pure preview", () =>
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Shaft plate", SourceKind = "effigy-file", SourceReference = "parts/shaft.effigy#plate" };
			doc.Definitions.Add( def );
			var occA = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate A", Transform = AssemblyTransform.FromXform( Xform.Identity ) };
			var occB = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate B", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 4, 0, 0 ) ) ) };
			doc.Occurrences.Add( occA );
			doc.Occurrences.Add( occB );
			var mate = StraightMate( occA, occB, 1f );
			Require( AxisMateSolver.TrySolve( doc, mate, out var solvedB, out _, out var error ), error ?? "preview solve" );
			NearVec( solvedB.Origin, new Vec3( 0, 0, 1 ), 1e-4f, "preview reports B at (0,0,1)" );
			Require( doc.Joints.Count == 0, "0 joints after a pure solve" );
			NearVec( occB.Transform.ToXform().Origin, new Vec3( 4, 0, 0 ), 1e-4f, "B unmoved by a pure solve" );
		} );

		Check( "axis mate re-apply edits the joint in place", () =>
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Shaft plate", SourceKind = "effigy-file", SourceReference = "parts/shaft.effigy#plate" };
			doc.Definitions.Add( def );
			var occA = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate A", Transform = AssemblyTransform.FromXform( Xform.Identity ) };
			var occB = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Plate B", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 4, 0, 0 ) ) ) };
			doc.Occurrences.Add( occA );
			doc.Occurrences.Add( occB );
			var mate = StraightMate( occA, occB, 1f );
			Require( mate.ApplyTo( doc, null, out _, out _, out var error ), error ?? "apply offset 1" );
			Require( doc.Joints.Count == 1, "one joint" );
			mate.AxialOffset = 2.5f;
			Require( mate.ApplyTo( doc, null, out _, out _, out error ), error ?? "re-apply offset 2.5" );
			Require( doc.Joints.Count == 1, "still one joint after re-apply" );
			NearVec( occB.Transform.ToXform().Origin, new Vec3( 0, 0, 2.5f ), 1e-4f, "B at the new offset" );
			var back = AxisMate.FromJoint( doc.Joints[0], out var why );
			Require( back != null, why ?? "restore the edited mate" );
			Near( back.AxialOffset, 2.5f, 1e-6f, "stored offset updated" );
		} );

		Check( "axis mate encodes as a hinge joint and restores fully", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var mate = StraightMate( occA, occB, -1.25f, flipped: true );
			var joint = mate.ToJoint();
			Require( joint.Kind == "hinge", $"joint kind was {joint.Kind}" );
			var back = AxisMate.FromJoint( joint, out var why );
			Require( back != null, why ?? "restore from the joint" );
			Require( back.Id == mate.Id, "id restores" );
			Near( back.AxialOffset, -1.25f, 1e-6f, "offset restores" );
			Require( back.Flipped, "flip restores" );
			Require( back.SourceA.Radius == 2.5f && back.SourceB.Radius == 1.0f, "radii restore" );
			Require( back.SourceA.Fingerprint == mate.SourceA.Fingerprint, "fingerprint A restores" );
			Require( back.SourceB.Fingerprint == mate.SourceB.Fingerprint, "fingerprint B restores" );
			Require( back.SourceA.GeometryRef == "face:3" && back.SourceB.GeometryRef == "edge:9", "geometry refs restore" );
		} );

		Check( "grounded B is refused by an axis mate (Desktop-closed Mobile gap)", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			AssemblyGrounding.SetGrounded( doc, occB.Id, true );
			var mate = StraightMate( occA, occB, 1f );
			var beforeB = occB.Transform.ToXform();
			Require( !mate.ApplyTo( doc, null, out _, out _, out var error ), "grounded B must not apply" );
			Require( error != null && error.StartsWith( "grounded-part", StringComparison.Ordinal ), $"error was {error}" );
			Require( doc.Joints.Count == 0, "nothing committed" );
			Require( ExactTransform( occB.Transform.ToXform(), beforeB ), "nothing moves" );
		} );

		Check( "fixed solver solves cleanly around an applied axis mate", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var mate = StraightMate( occA, occB, 2.5f );
			Require( mate.ApplyTo( doc, null, out _, out _, out var error ), error ?? "apply the axis mate" );
			var solved = AssemblyFixedJointSolver.Solve( doc );
			Require( solved.Success, "solve succeeds" );
			Require( solved.Diagnostics.All( d => d.Kind != "error" ),
				$"no error diagnostic, got {string.Join( ",", solved.Diagnostics.Select( d => d.Code ) )}" );
			Require( solved.TryGetSolvedTransform( occB.Id, out var solvedB ), "B has a solved transform" );
			NearVec( solvedB.Origin, new Vec3( 0, 0, 2.5f ), 1e-4f, "B keeps its stored solved transform" );
		} );

		Check( "axis self-mate and missing occurrence are refused without mutating", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var self = StraightMate( occA, occA, 1f );
			Require( self.Validate( doc ).Any( e => e.StartsWith( "self-joint", StringComparison.Ordinal ) ), "self-joint validation" );
			Require( !self.ApplyTo( doc, null, out _, out _, out _ ), "self-mate must not apply" );
			var missing = StraightMate( occA, occB, 1f );
			missing.OccurrenceB = "no-such-occurrence";
			missing.SourceB.OccurrenceId = "no-such-occurrence";
			Require( missing.Validate( doc ).Any( e => e.StartsWith( "missing-occurrence", StringComparison.Ordinal ) ), "missing-occurrence validation" );
			Require( !missing.ApplyTo( doc, null, out _, out _, out _ ), "missing occurrence must not apply" );
			Require( doc.Joints.Count == 0, "joint count unchanged" );
		} );

		Check( "edge and ring descriptors become axis connectors", () =>
		{
			var (doc, occA, occB, _, source) = Fresh();
			Require( AssemblyMateAxisSources.TryFromEdge( doc, source, occB.Id, 4, 5, out var edge, out var why ), why ?? "edge descriptor" );
			Require( edge.Kind == AssemblyMateAxisKind.StraightEdge, "straight edge kind" );
			NearVec( edge.Direction, new Vec3( 2, 0, 0 ), 1e-6f, "edge direction" );
			NearVec( edge.Anchor, new Vec3( 0, 0, 2 ), 1e-6f, "edge anchor" );
			Require( AssemblyMateAxisSources.TryFromRing( doc, source, occB.Id, new[] { 4, 5, 6, 7 }, out var ring, out why ), why ?? "ring descriptor" );
			Require( ring.Kind == AssemblyMateAxisKind.CircularEdge, "circular edge kind" );
			NearVec( ring.Anchor, new Vec3( 1, 1, 2 ), 1e-4f, "ring centre" );
			NearVec( ring.Direction, new Vec3( 0, 0, 1 ), 1e-4f, "ring axis" );
			Near( ring.Radius.Value, MathF.Sqrt( 2f ), 1e-4f, "ring radius" );
			Require( !AssemblyMateAxisSources.TryFromEdge( doc, source, occB.Id, 4, 4, out _, out _ ), "zero-length edge refused" );
			Require( !AssemblyMateAxisSources.TryFromRing( doc, source, occB.Id, new[] { 4, 5 }, out _, out _ ), "two-vertex ring refused" );
			_ = occA;
		} );

		Check( "legacy centroid mate survives alongside an axis mate", () =>
		{
			var (doc, occA, occB, _) = AxisFresh();
			var mate = StraightMate( occA, occB, 1f );
			Require( mate.ApplyTo( doc, null, out _, out _, out var error ), error ?? "apply the axis mate" );
			var studio = new PartStudio();
			studio.Assembly = doc;
			var legacy = new AssemblyMateFeature();
			legacy.Kind.Index = 2;
			legacy.Distance.Value = 5f;
			legacy.Bodies.BodyIds.Add( "body-a" );
			legacy.Bodies.BodyIds.Add( "body-b" );
			studio.Features.Add( legacy );
			var restored = StudioDocument.Read( StudioDocument.Write( studio ) );
			Require( restored.Assembly.Joints.Count == 1, "axis joint survives" );
			Require( AxisMate.FromJoint( restored.Assembly.Joints[0], out _ ) != null, "axis mate restores" );
			var restoredLegacy = restored.Features.OfType<AssemblyMateFeature>().FirstOrDefault();
			Require( restoredLegacy != null && restoredLegacy.Kind.Index == 2, "legacy mate survives unconverted" );
			Require( restored.Assembly.Validate().All( i => i.Warning ), "no error issues with axis mate and legacy mate" );
		} );
	}
}
