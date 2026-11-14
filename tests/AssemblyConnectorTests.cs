using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS03: connector frames, grounding and durable references.
// Ports Mobile app/AssemblyConnectorChecks.cs (23 checks) in Desktop form:
// fixtures are a unit quad, an 8-point ring and a two-occurrence document.
// Checks compare frames, origins and diagnostics, never only a success flag.
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyConnectorTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-connectors", name, test );
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

		PolyMesh QuadMesh()
		{
			var mesh = new PolyMesh();
			mesh.AddVertex( new Vec3( 0, 0, 0 ) );
			mesh.AddVertex( new Vec3( 1, 0, 0 ) );
			mesh.AddVertex( new Vec3( 1, 1, 0 ) );
			mesh.AddVertex( new Vec3( 0, 1, 0 ) );
			mesh.AddFace( new[] { 0, 1, 2 } );
			mesh.AddFace( new[] { 0, 2, 3 } );
			return mesh;
		}

		PolyMesh RingMesh( int count, float radius )
		{
			var mesh = new PolyMesh();
			var indices = new List<int>();
			for ( var i = 0; i < count; i++ )
			{
				var angle = i * MathF.PI * 2f / count;
				indices.Add( mesh.AddVertex( new Vec3( MathF.Cos( angle ) * radius, MathF.Sin( angle ) * radius, 0 ) ) );
			}
			return mesh;
		}

		(AssemblyDocument Doc, AssemblyOccurrence Left, AssemblyOccurrence Right) TwoOccurrenceDoc()
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Bracket", SourceKind = "effigy-file", SourceReference = "parts/bracket.effigy#body-1" };
			doc.Definitions.Add( def );
			var left = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Bracket left", Transform = new AssemblyTransform() };
			var right = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def.Id, Name = "Bracket right", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( -4, 2, 1 ) ) ) };
			doc.Occurrences.Add( left );
			doc.Occurrences.Add( right );
			return (doc, left, right);
		}

		Check( "compose order: occurrence then local frame", () =>
		{
			var occ = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 10, 0, 0 ) ) );
			var local = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 1, 0, 0 ) ) );
			var space = AssemblyConnectorFrames.ToAssemblySpace( occ, local );
			Require( space != null, "frame must compose" );
			NearVec( space.Frame.Origin, new Vec3( 11, 0, 0 ), 1e-5f, "occ(10,0,0) + local(1,0,0) must be (11,0,0)" );
			Require( space.Rigid, "composed frame must be rigid" );
			Require( !space.Mirrored, "composed frame must not be mirrored" );
		} );

		Check( "rotate-then-translate differs from translate-then-rotate", () =>
		{
			var a = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f ) * Xform.Translate( new Vec3( 2, 0, 0 ) );
			NearVec( a.Origin, new Vec3( 0, 2, 0 ), 1e-5f, "rotate-then-translate origin" );
			var b = Xform.Translate( new Vec3( 2, 0, 0 ) ) * Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f );
			NearVec( b.Origin, new Vec3( 2, 0, 0 ), 1e-5f, "translate-then-rotate origin" );
		} );

		Check( "90 degree Z rotation carries origin and keeps Z", () =>
		{
			var occ = AssemblyTransform.FromXform( Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f ) );
			var local = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 1, 0, 0 ) ) );
			var space = AssemblyConnectorFrames.ToAssemblySpace( occ, local );
			NearVec( space.Frame.Origin, new Vec3( 0, 1, 0 ), 1e-5f, "rotated origin" );
			NearVec( space.Frame.Z, new Vec3( 0, 0, 1 ), 1e-5f, "Z stays Z" );
		} );

		Check( "mirror is reported left-handed, never silently fixed", () =>
		{
			var occ = AssemblyTransform.FromXform( Xform.Mirror( Vec3.Zero, new Vec3( 1, 0, 0 ) ) );
			var space = AssemblyConnectorFrames.ToAssemblySpace( occ, new AssemblyTransform() );
			Require( space.Mirrored, "mirrored frame must report mirrored" );
			Require( !space.Rigid, "mirrored frame must not report rigid" );
			Near( space.Determinant, -1f, 1e-5f, "mirror determinant" );
			NearVec( space.Frame.X, new Vec3( -1, 0, 0 ), 1e-5f, "mirrored X" );
		} );

		Check( "uniform scale composes to non-rigid with det 8", () =>
		{
			var occ = AssemblyTransform.FromXform( Xform.Scale( new Vec3( 2, 2, 2 ) ) );
			var space = AssemblyConnectorFrames.ToAssemblySpace( occ, new AssemblyTransform() );
			Require( !space.Rigid, "scaled frame must not report rigid" );
			Near( space.Determinant, 8f, 1e-4f, "scale determinant" );
		} );

		Check( "grounding survives save reopen and clearing removes the key", () =>
		{
			var (doc, left, right) = TwoOccurrenceDoc();
			Require( AssemblyGrounding.ChooseRoot( doc ) == left, "ungrounded root is the first occurrence" );
			Require( AssemblyGrounding.IsMovable( doc, left.Id ), "ungrounded part is movable" );
			Require( AssemblyGrounding.SetGrounded( doc, right.Id, true ), "grounding must succeed" );
			Require( !AssemblyGrounding.IsMovable( doc, right.Id ), "grounded part is not movable" );
			Require( AssemblyGrounding.ChooseRoot( doc ) == right, "grounded part is the deterministic root" );
			Require( !AssemblyGrounding.SetGrounded( doc, AssemblyDocument.NewId(), true ), "grounding an unknown id fails" );
			var json = doc.ToJson();
			Require( json.Contains( "grounded" ), "saved text must carry the grounded flag" );
			var reopened = AssemblyDocument.FromJson( json );
			Require( AssemblyGrounding.IsGrounded( reopened, right.Id ), "grounding must survive reopen" );
			Require( AssemblyGrounding.ChooseRoot( reopened ).Id == right.Id, "root must survive reopen" );
			AssemblyGrounding.SetGrounded( reopened, right.Id, false );
			Require( !reopened.ToJson().Contains( "grounded" ), "clearing grounding must remove the key" );
			Require( reopened.Occurrences[1].ExtensionData == null, "clearing the last key nulls ExtensionData" );
		} );

		Check( "explicit connector round-trips and resolves without geometry", () =>
		{
			var (doc, left, _) = TwoOccurrenceDoc();
			Require( AssemblyConnectorGeometry.TryExplicit( left.Id, "Origin", new Vec3( 1, 2, 3 ), new Vec3( 0, 0, 1 ), null, out var connector, out var why ), "explicit must build: " + why );
			Require( AssemblyConnectorStore.AddConnector( doc, connector ), "explicit must store" );
			var json = doc.ToJson();
			var reopened = AssemblyDocument.FromJson( json );
			var found = AssemblyConnectorStore.FindConnector( reopened, connector.Id );
			Require( found != null, "connector must survive reopen" );
			NearVec( AssemblyConnectorFrames.FrameOrigin( found.LocalFrame ), new Vec3( 1, 2, 3 ), 1e-5f, "explicit origin" );
			NearVec( AssemblyConnectorFrames.FramePrimaryAxis( found.LocalFrame ), new Vec3( 0, 0, 1 ), 1e-5f, "explicit axis" );
			var resolution = AssemblyConnectorResolver.Resolve( found, null );
			Require( resolution.Status == AssemblyConnectorStatus.Resolved, "explicit resolves with a null mesh" );
		} );

		Check( "planar connector from quad has centroid origin and +Z", () =>
		{
			var mesh = QuadMesh();
			Require( AssemblyConnectorGeometry.TryPlanarFaces( mesh, new[] { 0, 1 }, "occ", "body-1", "Top", out var connector, out var why ), "planar must build: " + why );
			NearVec( AssemblyConnectorFrames.FrameOrigin( connector.LocalFrame ), new Vec3( 0.5f, 0.5f, 0 ), 1e-5f, "quad centroid" );
			NearVec( AssemblyConnectorFrames.FramePrimaryAxis( connector.LocalFrame ), new Vec3( 0, 0, 1 ), 1e-5f, "quad normal" );
			Require( connector.LocalFrame.IsRigidFrame( out _ ), "planar frame must be rigid" );
			var resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Resolved, "planar must resolve: " + resolution.Detail );
			Require( mesh.Positions.Count == 4, "derivation must not modify the mesh" );
			// A small edit moves the plane: changed, never a silent snap.
			mesh.Positions[2] = mesh.Positions[2] + new Vec3( 0, 0, 0.05f );
			resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Changed, $"edited plane must be changed, got {resolution.Status}" );
			resolution = AssemblyConnectorResolver.Resolve( connector, null );
			Require( resolution.Status == AssemblyConnectorStatus.Missing, "null mesh must be missing" );
			var empty = new PolyMesh();
			resolution = AssemblyConnectorResolver.Resolve( connector, empty );
			Require( resolution.Status == AssemblyConnectorStatus.Missing, "empty mesh must be missing" );
		} );

		Check( "flipped winding gives a flipped normal and still resolves", () =>
		{
			var mesh = new PolyMesh();
			mesh.AddVertex( new Vec3( 0, 0, 0 ) );
			mesh.AddVertex( new Vec3( 1, 0, 0 ) );
			mesh.AddVertex( new Vec3( 1, 1, 0 ) );
			mesh.AddVertex( new Vec3( 0, 1, 0 ) );
			mesh.AddFace( new[] { 0, 2, 1 } );
			mesh.AddFace( new[] { 0, 3, 2 } );
			Require( AssemblyConnectorGeometry.TryPlanarFaces( mesh, new[] { 0, 1 }, "occ", "body-1", "Bottom", out var connector, out var why ), "flipped planar must build: " + why );
			NearVec( AssemblyConnectorFrames.FramePrimaryAxis( connector.LocalFrame ), new Vec3( 0, 0, -1 ), 1e-5f, "flipped normal must be -Z" );
			var resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Resolved, "flipped planar must resolve" );
		} );

		Check( "straight edge connector has midpoint origin and edge axis", () =>
		{
			var mesh = QuadMesh();
			Require( AssemblyConnectorGeometry.TryStraightEdge( mesh, 0, 1, "occ", "body-1", "Edge", out var connector, out var why ), "edge must build: " + why );
			NearVec( AssemblyConnectorFrames.FrameOrigin( connector.LocalFrame ), new Vec3( 0.5f, 0, 0 ), 1e-5f, "edge midpoint" );
			NearVec( AssemblyConnectorFrames.FramePrimaryAxis( connector.LocalFrame ), new Vec3( 1, 0, 0 ), 1e-5f, "edge direction" );
			var resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Resolved, "edge must resolve" );
			mesh.Positions[1] = mesh.Positions[1] + new Vec3( 0.05f, 0, 0 );
			resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Changed, "moved edge must be changed" );
		} );

		Check( "circular connector extracts centre radius and axis", () =>
		{
			var mesh = RingMesh( 8, 2f );
			var indices = Enumerable.Range( 0, 8 ).ToList();
			Require( AssemblyConnectorGeometry.TryCircularLoop( mesh, indices, "occ", "body-1", "Ring", out var connector, out var why ), "ring must build: " + why );
			NearVec( AssemblyConnectorFrames.FrameOrigin( connector.LocalFrame ), new Vec3( 0, 0, 0 ), 1e-5f, "ring centre" );
			Near( connector.Source.Fingerprint.Radius, 2f, 1e-4f, "ring radius" );
			NearVec( AssemblyConnectorFrames.FramePrimaryAxis( connector.LocalFrame ), new Vec3( 0, 0, 1 ), 1e-5f, "ring axis" );
			var resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Resolved, "ring must resolve: " + resolution.Detail );
			mesh.Positions[0] = mesh.Positions[0] + new Vec3( 0.1f, 0, 0 );
			resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Changed, "moved ring point must be changed" );
		} );

		Check( "doubled faces are ambiguous, never a silent pick", () =>
		{
			var mesh = QuadMesh();
			mesh.AddFace( new[] { 0, 1, 2 } );
			mesh.AddFace( new[] { 0, 2, 3 } );
			Require( AssemblyConnectorGeometry.TryPlanarFaces( mesh, new[] { 0, 1 }, "occ", "body-1", "Top", out var connector, out _ ), "planar must build" );
			var resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Ambiguous, $"doubled faces must be ambiguous, got {resolution.Status}" );
		} );

		Check( "non-coplanar faces are refused", () =>
		{
			var mesh = QuadMesh();
			mesh.AddVertex( new Vec3( 0.5f, 0.5f, 1 ) );
			mesh.AddFace( new[] { 0, 1, 4 } );
			var ok = AssemblyConnectorGeometry.TryPlanarFaces( mesh, new[] { 0, 2 }, "occ", "body-1", "Top", out _, out var why );
			Require( !ok, "non-coplanar faces must be refused" );
			Require( why != null && why.Contains( "coplanar" ), "refusal must name coplanarity: " + why );
		} );

		Check( "zero-length and degenerate geometry are refused", () =>
		{
			var ok = AssemblyConnectorFrames.TryFrameFromOriginAndAxis( Vec3.Zero, Vec3.Zero, null, out _, out var why );
			Require( !ok && why.Contains( "zero-length" ), "zero axis must be refused" );
			var mesh = new PolyMesh();
			mesh.AddVertex( new Vec3( 0, 0, 0 ) );
			mesh.AddVertex( new Vec3( 1, 0, 0 ) );
			mesh.AddVertex( new Vec3( 2, 0, 0 ) );
			mesh.AddFace( new[] { 0, 1, 2 } );
			ok = AssemblyConnectorGeometry.TryPlanarFaces( mesh, new[] { 0 }, "occ", "body-1", "Flat", out _, out why );
			Require( !ok, "collinear face must be refused" );
			ok = AssemblyConnectorGeometry.TryStraightEdge( mesh, 0, 0, "occ", "body-1", "Edge", out _, out why );
			Require( !ok, "same-vertex edge must be refused" );
			ok = AssemblyConnectorGeometry.TryCircularLoop( mesh, new[] { 0, 1, 2 }, "occ", "body-1", "Ring", out _, out why );
			Require( !ok, "3-point ring must be refused" );
			mesh.AddVertex( new Vec3( 0.0005f, 0, 0 ) );
			mesh.AddFace( new[] { 0, 3, 1 } );
			ok = AssemblyConnectorGeometry.TryStraightEdge( mesh, 0, 3, "occ", "body-1", "Tiny", out _, out why );
			Require( !ok && why.Contains( "too short" ), "tiny edge must be refused: " + why );
		} );

		Check( "joint attach by id round-trips; drift and removal are diagnosed", () =>
		{
			var (doc, left, right) = TwoOccurrenceDoc();
			Require( AssemblyConnectorGeometry.TryExplicit( left.Id, "A", Vec3.Zero, new Vec3( 0, 0, 1 ), null, out var a, out _ ), "explicit A" );
			Require( AssemblyConnectorGeometry.TryExplicit( right.Id, "B", Vec3.Zero, new Vec3( 0, 0, 1 ), null, out var b, out _ ), "explicit B" );
			Require( AssemblyConnectorStore.AddConnector( doc, a ) && AssemblyConnectorStore.AddConnector( doc, b ), "store both" );
			var joint = new AssemblyJoint { Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = left.Id, OccurrenceB = right.Id, FrameA = a.LocalFrame, FrameB = b.LocalFrame };
			doc.Joints.Add( joint );
			Require( AssemblyConnectorStore.AttachToJoint( doc, joint, "A", a.Id ), "attach A" );
			Require( AssemblyConnectorStore.AttachToJoint( doc, joint, "B", b.Id ), "attach B" );
			Require( !AssemblyConnectorStore.AttachToJoint( doc, joint, "C", a.Id ), "bad side refused" );
			Require( !AssemblyConnectorStore.AttachToJoint( doc, joint, "A", b.Id ), "cross-occurrence attach refused" );
			var json = doc.ToJson();
			var reopened = AssemblyDocument.FromJson( json );
			var reopenedJoint = reopened.Joints[0];
			Require( AssemblyConnectorStore.ConnectorForJoint( reopened, reopenedJoint, "A" )?.Id == a.Id, "attach must survive reopen" );
			Require( AssemblyConnectorValidation.Validate( reopened ).Count == 0, "attached doc must validate clean" );
			reopenedJoint.FrameA = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 6, 0, 0 ) ) );
			var issues = AssemblyConnectorValidation.Validate( reopened );
			Require( issues.Any( i => i.Code == "connector-frame-mismatch" ), "drifted joint frame must warn connector-frame-mismatch" );
			AssemblyConnectorStore.RemoveConnector( reopened, a.Id );
			issues = AssemblyConnectorValidation.Validate( reopened );
			Require( issues.Any( i => i.Code == "missing-connector" ), "removed connector must report missing-connector" );
		} );

		Check( "transformed repeated occurrences compose independently", () =>
		{
			var (doc, left, right) = TwoOccurrenceDoc();
			Require( AssemblyConnectorGeometry.TryExplicit( left.Id, "L", new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), null, out var l, out _ ), "left connector" );
			Require( AssemblyConnectorGeometry.TryExplicit( right.Id, "R", new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), null, out var r, out _ ), "right connector" );
			AssemblyConnectorStore.AddConnector( doc, l );
			AssemblyConnectorStore.AddConnector( doc, r );
			Require( AssemblyConnectorFrames.TryFrameInAssembly( doc, l, out var leftSpace, out _ ), "left space frame" );
			Require( AssemblyConnectorFrames.TryFrameInAssembly( doc, r, out var rightSpace, out _ ), "right space frame" );
			NearVec( leftSpace.Frame.Origin, new Vec3( 1, 0, 0 ), 1e-5f, "left assembly origin" );
			NearVec( rightSpace.Frame.Origin, new Vec3( -3, 2, 1 ), 1e-5f, "right assembly origin" );
			// Inverse: assembly frame back to local space.
			Require( AssemblyConnectorFrames.TryToLocalSpace( right.Transform, AssemblyTransform.FromXform( rightSpace.Frame ), out var back, out _ ), "to local" );
			NearVec( AssemblyConnectorFrames.FrameOrigin( back ), new Vec3( 1, 0, 0 ), 1e-5f, "round-trip local origin" );
			// Singular occurrence cannot invert.
			var singular = new AssemblyTransform { X = new[] { 0f, 0f, 0f }, Y = new[] { 0f, 1f, 0f }, Z = new[] { 0f, 0f, 1f }, Origin = new[] { 0f, 0f, 0f } };
			Require( !AssemblyConnectorFrames.TryToLocalSpace( singular, new AssemblyTransform(), out _, out var why ) && why.Contains( "singular" ), "singular occurrence must fail to invert" );
		} );

		Check( "mirrored occurrence warns mirrored-connector", () =>
		{
			var (doc, left, _) = TwoOccurrenceDoc();
			left.Transform = AssemblyTransform.FromXform( Xform.Mirror( Vec3.Zero, new Vec3( 1, 0, 0 ) ) );
			Require( AssemblyConnectorGeometry.TryExplicit( left.Id, "M", Vec3.Zero, new Vec3( 0, 0, 1 ), null, out var connector, out _ ), "explicit on mirror" );
			AssemblyConnectorStore.AddConnector( doc, connector );
			var issues = AssemblyConnectorValidation.Validate( doc, _ => QuadMesh() );
			Require( issues.Any( i => i.Code == "mirrored-connector" && i.Warning ), "mirrored connector must warn" );
		} );

		Check( "units survive and one inch stays one inch", () =>
		{
			var doc = new AssemblyDocument();
			Require( doc.LengthUnit == "in" && doc.AngleUnit == "rad", "document units" );
			var json = doc.ToJson();
			var reopened = AssemblyDocument.FromJson( json );
			Require( reopened.LengthUnit == "in" && reopened.AngleUnit == "rad", "units survive reopen" );
			var mesh = QuadMesh();
			Require( AssemblyConnectorGeometry.TryPlanarFaces( mesh, new[] { 0, 1 }, "occ", "body-1", "Top", out var connector, out _ ), "planar" );
			Near( connector.Source.Fingerprint.Area, 1f, 1e-5f, "unit quad area is 1 square inch" );
		} );

		Check( "future connector kind is preserved and unresolvable", () =>
		{
			var (doc, left, _) = TwoOccurrenceDoc();
			Require( AssemblyConnectorGeometry.TryExplicit( left.Id, "F", Vec3.Zero, new Vec3( 0, 0, 1 ), null, out var connector, out _ ), "explicit" );
			connector.Kind = "future-kind";
			AssemblyConnectorStore.AddConnector( doc, connector );
			var reopened = AssemblyDocument.FromJson( doc.ToJson() );
			var found = AssemblyConnectorStore.FindConnector( reopened, connector.Id );
			Require( found != null && found.Kind == "future-kind", "future kind must survive round-trip" );
			var resolution = AssemblyConnectorResolver.Resolve( found, QuadMesh() );
			Require( resolution.Status == AssemblyConnectorStatus.Unresolvable, "future kind must be unresolvable" );
			var issues = AssemblyConnectorValidation.Validate( reopened );
			Require( issues.Any( i => i.Code == "unresolvable-connector" && i.Warning ), "future kind must warn unresolvable-connector" );
		} );

		Check( "scaled local frame is invalid", () =>
		{
			var (doc, left, _) = TwoOccurrenceDoc();
			Require( AssemblyConnectorGeometry.TryExplicit( left.Id, "S", Vec3.Zero, new Vec3( 0, 0, 1 ), null, out var connector, out _ ), "explicit" );
			connector.LocalFrame = new AssemblyTransform { X = new[] { 2f, 0f, 0f }, Y = new[] { 0f, 1f, 0f }, Z = new[] { 0f, 0f, 1f }, Origin = new[] { 0f, 0f, 0f } };
			AssemblyConnectorStore.AddConnector( doc, connector );
			var resolution = AssemblyConnectorResolver.Resolve( connector, QuadMesh() );
			Require( resolution.Status == AssemblyConnectorStatus.Invalid, "scaled frame must be invalid" );
			var issues = AssemblyConnectorValidation.Validate( doc );
			Require( issues.Any( i => i.Code == "invalid-connector-frame" ), "scaled frame must report invalid-connector-frame" );
		} );

		Check( "connector naming a missing occurrence mismatches", () =>
		{
			var (doc, left, _) = TwoOccurrenceDoc();
			Require( AssemblyConnectorGeometry.TryExplicit( left.Id, "M", Vec3.Zero, new Vec3( 0, 0, 1 ), null, out var connector, out _ ), "explicit" );
			connector.OccurrenceId = AssemblyDocument.NewId();
			Require( !AssemblyConnectorStore.AddConnector( doc, connector ), "add must fail for a missing occurrence" );
			// Stored by hand on the wrong occurrence: the mismatch must be diagnosed, not dropped.
			AssemblyConnectorStore.SetConnectors( left, new[] { connector } );
			var issues = AssemblyConnectorValidation.Validate( doc );
			Require( issues.Any( i => i.Code == "connector-occurrence-mismatch" ), "stored mismatch must report connector-occurrence-mismatch" );
			Require( AssemblyConnectorStore.FindConnector( doc, connector.Id ) != null, "mismatched record must be preserved for repair" );
		} );

		Check( "document text snapshot restores grounding and connectors identically", () =>
		{
			var (doc, left, right) = TwoOccurrenceDoc();
			AssemblyGrounding.SetGrounded( doc, left.Id, true );
			Require( AssemblyConnectorGeometry.TryExplicit( left.Id, "A", new Vec3( 1, 2, 3 ), new Vec3( 0, 0, 1 ), null, out var a, out _ ), "explicit A" );
			AssemblyConnectorStore.AddConnector( doc, a );
			var json = doc.ToJson();
			var restored = AssemblyDocument.FromJson( json );
			Require( restored.ToJson() == json, "snapshot must restore byte-identically" );
			Require( AssemblyGrounding.IsGrounded( restored, left.Id ), "grounding restored" );
			Require( AssemblyConnectorStore.FindConnector( restored, a.Id ) != null, "connector restored" );
		} );

		Check( "corrupt connectors value is skipped and diagnosed", () =>
		{
			var (doc, left, _) = TwoOccurrenceDoc();
			left.ExtensionData ??= new Dictionary<string, JsonElement>();
			left.ExtensionData["connectors"] = JsonSerializer.SerializeToElement( "nope" );
			var ok = AssemblyConnectorStore.TryGetConnectors( left, out var connectors, out var skipped );
			Require( !ok && skipped == 1 && connectors.Count == 0, "corrupt connectors must skip 1" );
			var issues = AssemblyConnectorValidation.Validate( doc );
			Require( issues.Any( i => i.Code == "invalid-connector" ), "corrupt connectors must report invalid-connector" );
		} );

		Check( "store caps at 64 connectors per occurrence", () =>
		{
			var (doc, left, _) = TwoOccurrenceDoc();
			for ( var i = 0; i < AssemblyConnectorStore.MaxConnectorsPerOccurrence; i++ )
			{
				Require( AssemblyConnectorGeometry.TryExplicit( left.Id, $"C{i}", Vec3.Zero, new Vec3( 0, 0, 1 ), null, out var c, out _ ), "explicit" );
				Require( AssemblyConnectorStore.AddConnector( doc, c ), $"add {i} must succeed" );
			}
			Require( AssemblyConnectorGeometry.TryExplicit( left.Id, "Overflow", Vec3.Zero, new Vec3( 0, 0, 1 ), null, out var extra, out _ ), "explicit extra" );
			Require( !AssemblyConnectorStore.AddConnector( doc, extra ), "65th connector must be refused" );
			// Replacing an existing Id still works when full.
			var existing = AssemblyConnectorStore.GetConnectors( left )[0];
			existing.Name = "Renamed";
			Require( AssemblyConnectorStore.AddConnector( doc, existing ), "replace by Id must work when full" );
		} );

		Check( "ring with near-coincident vertices resolves; collapsing one changes it", () =>
		{
			var mesh = new PolyMesh();
			for ( var i = 0; i < 8; i++ )
			{
				var angle = i * MathF.PI * 2f / 8f;
				mesh.AddVertex( new Vec3( MathF.Cos( angle ) * 2f, MathF.Sin( angle ) * 2f, 0 ) );
			}
			// Nudge vertex 1 almost onto vertex 0 (0.00025 rad apart): still resolves.
			mesh.Positions[1] = new Vec3( MathF.Cos( 0.00025f ) * 2f, MathF.Sin( 0.00025f ) * 2f, 0 );
			var indices = Enumerable.Range( 0, 8 ).ToList();
			Require( AssemblyConnectorGeometry.TryCircularLoop( mesh, indices, "occ", "body-1", "Ring", out var connector, out var why ), "near-coincident ring must build: " + why );
			var resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Resolved, "near-coincident ring must resolve: " + resolution.Detail );
			// Collapse vertex 1 onto vertex 0: the ring changed.
			mesh.Positions[1] = mesh.Positions[0];
			resolution = AssemblyConnectorResolver.Resolve( connector, mesh );
			Require( resolution.Status == AssemblyConnectorStatus.Changed, "collapsed ring must be changed" );
		} );

		Check( "history helper makes one undo step and restores the snapshot", () =>
		{
			var (doc, left, _) = TwoOccurrenceDoc();
			var history = new EditHistory();
			AssemblyHistory.ApplyWithHistory( doc, history, "ground right", () => AssemblyGrounding.SetGrounded( doc, left.Id, true ) );
			Require( history.UndoCount == 1, "one edit must be one history entry" );
			Require( AssemblyGrounding.IsGrounded( doc, left.Id ), "edit must apply" );
			history.Undo();
			Require( !AssemblyGrounding.IsGrounded( doc, left.Id ), "undo must restore the snapshot" );
			history.Redo();
			Require( AssemblyGrounding.IsGrounded( doc, left.Id ), "redo must re-apply" );
			// A no-op edit adds no entry.
			AssemblyHistory.ApplyWithHistory( doc, history, "no-op", () => { } );
			Require( history.UndoCount == 1, "no-op edit must add no entry" );
		} );
	}
}
