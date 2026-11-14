using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS04: transformed-occurrence snapshots, picking and render meshes.
// Fixture: one "Chair" definition with a unit-box mesh (8 verts, 12 tris,
// outward winding, signed volume +1), occurrences A at identity and B at
// Translate(5,0,0), both visible. Box corners are (0,0,0)-(1,1,1), so B's
// bounds are (5,0,0)-(6,1,1) and the union is (0,0,0)-(6,1,1). No files,
// no app. Not runtime verified: the final integrator runs
// tests/Desktop.Tests.csproj.
internal static class AssemblyOccurrenceRenderTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-snapshots", name, test );
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
		void NearVec3( Vector3 a, Vector3 b, float eps, string what )
		{
			Near( a.X, b.X, eps, what + ".X" );
			Near( a.Y, b.Y, eps, what + ".Y" );
			Near( a.Z, b.Z, eps, what + ".Z" );
		}

		Check( "two copies of one chair are independently pickable", () =>
		{
			var f = new Fixture();
			var snap = f.Service.Build( f.Doc, f.Resolver );
			Require( snap.Occurrences.Count == 2, $"expected 2 occurrences, got {snap.Occurrences.Count}" );

			var hitA = f.Service.Pick( snap, new Vec3( -1f, 0.5f, 0.5f ), new Vec3( 1f, 0f, 0f ) );
			Require( hitA.HasValue, "ray at A should hit" );
			Require( hitA.Value.OccurrenceId == f.AId, $"ray at A returned {hitA.Value.OccurrenceId}, expected occurrence {f.AId}" );
			Require( hitA.Value.OccurrenceId != f.Chair.Id, "pick returned the definition id, not the occurrence id" );
			Near( hitA.Value.Distance, 1f, 1e-4f, "distance to A" );

			var hitB = f.Service.Pick( snap, new Vec3( 4f, 0.5f, 0.5f ), new Vec3( 1f, 0f, 0f ) );
			Require( hitB.HasValue, "ray at B should hit" );
			Require( hitB.Value.OccurrenceId == f.BId, $"ray at B returned {hitB.Value.OccurrenceId}, expected occurrence {f.BId}" );
			Require( hitB.Value.OccurrenceId != f.Chair.Id, "pick returned the definition id, not the occurrence id" );
			Near( hitB.Value.Distance, 1f, 1e-4f, "distance to B" );

			// Nearest-first: a ray from the left passes A before B.
			var hitNear = f.Service.Pick( snap, new Vec3( -2f, 0.5f, 0.5f ), new Vec3( 1f, 0f, 0f ) );
			Require( hitNear.HasValue && hitNear.Value.OccurrenceId == f.AId, "nearest hit from the left should be A" );
			Near( hitNear.Value.Distance, 2f, 1e-4f, "distance to nearest (A)" );

			// A ray that hits nothing returns null.
			var miss = f.Service.Pick( snap, new Vec3( 0.5f, 0.5f, -1f ), new Vec3( 0f, 0f, -1f ) );
			Require( !miss.HasValue, "ray away from both boxes should hit nothing" );
		} );

		Check( "hidden copy is absent from snapshot, picking and render list", () =>
		{
			var f = new Fixture();
			f.Occurrence( f.BId ).Visible = false;
			var snap = f.Service.Build( f.Doc, f.Resolver );
			Require( snap.Occurrences.Count == 1, $"expected 1 occurrence, got {snap.Occurrences.Count}" );
			Require( snap.Occurrences[0].OccurrenceId == f.AId, "the remaining occurrence should be A" );
			Require( snap.VisibleCount == 1, $"visible count should be 1, got {snap.VisibleCount}" );

			var hitB = f.Service.Pick( snap, new Vec3( 4f, 0.5f, 0.5f ), new Vec3( 1f, 0f, 0f ) );
			Require( !hitB.HasValue, "ray at hidden B's position should hit nothing" );

			var render = f.Service.BuildRenderMeshes( snap );
			Require( render.Count == 1, $"render list should have 1 mesh, got {render.Count}" );
		} );

		Check( "source mesh is unchanged by build, pick and render", () =>
		{
			var f = new Fixture();
			var beforePos = f.ChairMesh.Positions.Select( p => (p.x, p.y, p.z) ).ToArray();
			var beforeFaces = f.ChairMesh.Faces.Select( face => (int[])face.Indices.Clone() ).ToArray();

			var snap = f.Service.Build( f.Doc, f.Resolver );
			_ = f.Service.Pick( snap, new Vec3( -1f, 0.5f, 0.5f ), new Vec3( 1f, 0f, 0f ) );
			_ = f.Service.BuildRenderMeshes( snap );

			Require( f.ChairMesh.Positions.Count == beforePos.Length, "source vertex count changed" );
			for ( var i = 0; i < beforePos.Length; i++ )
			{
				var p = f.ChairMesh.Positions[i];
				Require( p.x == beforePos[i].x && p.y == beforePos[i].y && p.z == beforePos[i].z,
					$"source position {i} changed" );
			}
			Require( f.ChairMesh.Faces.Count == beforeFaces.Length, "source face count changed" );
			for ( var i = 0; i < beforeFaces.Length; i++ )
				Require( f.ChairMesh.Faces[i].Indices.SequenceEqual( beforeFaces[i] ), $"source face {i} winding changed" );

			Require( !ReferenceEquals( snap.Occurrences[0].Mesh, f.ChairMesh ), "snapshot mesh must be a clone, not the source" );
			Require( !ReferenceEquals( snap.Occurrences[1].Mesh, f.ChairMesh ), "snapshot mesh must be a clone, not the source" );
		} );

		Check( "transform is applied exactly once", () =>
		{
			var f = new Fixture();
			var snap1 = f.Service.Build( f.Doc, f.Resolver );
			var snap2 = f.Service.Build( f.Doc, f.Resolver );

			// Source bounds (0,0,0)-(1,1,1) translated by (5,0,0), on both builds.
			foreach ( var snap in new[] { snap1, snap2 } )
			{
				var b = snap.Occurrences[1];
				NearVec( b.BoundsMin, new Vec3( 5f, 0f, 0f ), 1e-5f, "B min" );
				NearVec( b.BoundsMax, new Vec3( 6f, 1f, 1f ), 1e-5f, "B max" );
				// Source vertex 1 is (1,0,0): once-transformed it is (6,0,0),
				// twice-transformed it would be (11,0,0).
				NearVec( b.Mesh.Positions[1], new Vec3( 6f, 0f, 0f ), 1e-5f, "B vertex 1" );
			}
			var a = snap1.Occurrences[0];
			NearVec( a.BoundsMin, new Vec3( 0f, 0f, 0f ), 1e-5f, "A min" );
			NearVec( a.BoundsMax, new Vec3( 1f, 1f, 1f ), 1e-5f, "A max" );
		} );

		Check( "cache reuses unchanged occurrences and rebuilds only what changed", () =>
		{
			var f = new Fixture();
			var s = f.Service;

			var snap1 = s.GetOrBuild( f.Doc, f.Resolver );
			Require( s.CacheMisses == 1 && s.CacheHits == 0, $"first GetOrBuild should be a miss (hits={s.CacheHits}, misses={s.CacheMisses})" );

			var snap2 = s.GetOrBuild( f.Doc, f.Resolver );
			Require( ReferenceEquals( snap1, snap2 ), "unchanged inputs should return the same snapshot object" );
			Require( s.CacheHits == 1, $"second GetOrBuild should be a hit (hits={s.CacheHits})" );

			// Move B only: A keeps its mesh object, B is rebuilt.
			f.Occurrence( f.BId ).Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 6f, 0f, 0f ) ) );
			var snap3 = s.GetOrBuild( f.Doc, f.Resolver );
			Require( !ReferenceEquals( snap2, snap3 ), "changed transform should rebuild the snapshot" );
			Require( ReferenceEquals( snap3.Occurrences[0].Mesh, snap2.Occurrences[0].Mesh ), "A's mesh should be reused when only B moves" );
			Require( !ReferenceEquals( snap3.Occurrences[1].Mesh, snap2.Occurrences[1].Mesh ), "B's mesh should be rebuilt after its transform changes" );
			NearVec( snap3.Occurrences[1].BoundsMin, new Vec3( 6f, 0f, 0f ), 1e-5f, "B min after move" );
			Require( s.CacheMisses == 2, $"moving B should be one more miss (misses={s.CacheMisses})" );

			// Bump the source revision: both Chair occurrences rebuild.
			var oldAMesh = snap3.Occurrences[0].Mesh;
			var oldBMesh = snap3.Occurrences[1].Mesh;
			f.Revision = 2;
			var snap4 = s.GetOrBuild( f.Doc, f.Resolver );
			Require( !ReferenceEquals( snap4.Occurrences[0].Mesh, oldAMesh ), "revision bump should rebuild A" );
			Require( !ReferenceEquals( snap4.Occurrences[1].Mesh, oldBMesh ), "revision bump should rebuild B" );
			Require( s.CacheMisses == 3, $"revision bump should be one more miss (misses={s.CacheMisses})" );

			// Invalidate forces a full rebuild.
			s.Invalidate();
			var snap5 = s.GetOrBuild( f.Doc, f.Resolver );
			Require( !ReferenceEquals( snap4, snap5 ), "Invalidate should force a new snapshot object" );
			Require( !ReferenceEquals( snap5.Occurrences[0].Mesh, snap4.Occurrences[0].Mesh ), "Invalidate should rebuild A's mesh" );
			Require( s.CacheMisses == 4, $"expected 4 misses, got {s.CacheMisses}" );
		} );

		Check( "mirrored occurrence reverses winding and reports Mirrored", () =>
		{
			var f = new Fixture();
			var mirrorId = AssemblyDocument.NewId();
			f.Doc.Occurrences.Add( new AssemblyOccurrence
			{
				Id = mirrorId,
				DefinitionId = f.Chair.Id,
				Name = "Chair mirrored",
				Transform = AssemblyTransform.FromXform( Xform.Mirror( Vec3.Zero, new Vec3( 1f, 0f, 0f ) ) ),
			} );
			var snap = f.Service.Build( f.Doc, f.Resolver );
			var plain = snap.Occurrences[0];
			var mirrored = snap.Occurrences[2];

			Require( !plain.Mirrored, "plain occurrence should not report Mirrored" );
			Require( mirrored.Mirrored, "mirrored occurrence should report Mirrored" );
			Require( plain.Mesh.SignedVolume() > 0.9f, $"unmirrored box volume should be +1, got {plain.Mesh.SignedVolume()}" );
			Require( mirrored.Mesh.SignedVolume() < -0.9f, $"mirrored box volume should be -1, got {mirrored.Mesh.SignedVolume()}" );
			NearVec( mirrored.BoundsMin, new Vec3( -1f, 0f, 0f ), 1e-5f, "mirrored min" );
			NearVec( mirrored.BoundsMax, new Vec3( 0f, 1f, 1f ), 1e-5f, "mirrored max" );
		} );

		Check( "missing source is recorded, not fatal", () =>
		{
			var f = new Fixture();
			var leg = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(),
				Name = "Leg",
				SourceKind = "studio-body",
				SourceReference = "studio#leg",
			};
			f.Doc.Definitions.Add( leg );
			var legId = AssemblyDocument.NewId();
			f.Doc.Occurrences.Add( new AssemblyOccurrence
			{
				Id = legId,
				DefinitionId = leg.Id,
				Name = "Leg C",
				Transform = AssemblyTransform.FromXform( Xform.Identity ),
			} );

			var snap = f.Service.Build( f.Doc, f.Resolver );
			Require( snap.Occurrences.Count == 3, $"missing-source occurrence stays in the snapshot (count={snap.Occurrences.Count})" );
			var missing = snap.Occurrences[2];
			Require( missing.MissingSource, "Leg occurrence should report MissingSource" );
			Require( missing.Mesh is null, "missing-source occurrence has no mesh" );
			Require( snap.MissingOccurrenceIds.Count == 1 && snap.MissingOccurrenceIds[0] == legId,
				"Leg id should be the only MissingOccurrenceId" );
			Require( snap.VisibleCount == 3, $"visible count includes the missing-source occurrence (got {snap.VisibleCount})" );

			var hitA = f.Service.Pick( snap, new Vec3( -1f, 0.5f, 0.5f ), new Vec3( 1f, 0f, 0f ) );
			Require( hitA.HasValue && hitA.Value.OccurrenceId == f.AId, "A should still be pickable with a missing source present" );

			var render = f.Service.BuildRenderMeshes( snap );
			Require( render.Count == 2, $"render list should skip the missing-source occurrence (got {render.Count})" );
		} );

		Check( "snapshot bounds are the union of visible occurrences", () =>
		{
			var f = new Fixture();
			var snap = f.Service.Build( f.Doc, f.Resolver );
			NearVec( snap.Bounds.Min, new Vec3( 0f, 0f, 0f ), 1e-5f, "union min" );
			NearVec( snap.Bounds.Max, new Vec3( 6f, 1f, 1f ), 1e-5f, "union max" );
		} );

		Check( "material name flows into the render meshes", () =>
		{
			var f = new Fixture();
			var snap = f.Service.Build( f.Doc, f.Resolver );
			var render = f.Service.BuildRenderMeshes( snap );
			Require( render.Count == 2, $"one CpuMesh per visible occurrence (got {render.Count})" );
			Require( render[0].Batches.Length >= 1, "A render mesh should have a batch" );
			Require( render[0].Batches[0].MaterialName == "oak", $"A batch material should be oak, got '{render[0].Batches[0].MaterialName}'" );
			Require( render[1].Batches.Length >= 1, "B render mesh should have a batch" );
			Require( render[1].Batches[0].MaterialName == "oak", $"B batch material should be oak, got '{render[1].Batches[0].MaterialName}'" );
			NearVec3( render[0].BoundsMin, new Vector3( 0f, 0f, 0f ), 1e-5f, "A render min" );
			NearVec3( render[0].BoundsMax, new Vector3( 1f, 1f, 1f ), 1e-5f, "A render max" );
			NearVec3( render[1].BoundsMin, new Vector3( 5f, 0f, 0f ), 1e-5f, "B render min" );
			NearVec3( render[1].BoundsMax, new Vector3( 6f, 1f, 1f ), 1e-5f, "B render max" );
		} );

		Check( "empty document builds an empty snapshot with null pick", () =>
		{
			var service = new AssemblySnapshotService();
			var doc = new AssemblyDocument();
			AssemblySourceResolver never = _ => null;
			var snap = service.Build( doc, never );
			Require( snap.Occurrences.Count == 0, "empty doc should have no occurrences" );
			Require( snap.MissingOccurrenceIds.Count == 0, "empty doc should have no missing ids" );
			Require( snap.VisibleCount == 0, "empty doc visible count should be 0" );
			NearVec( snap.Bounds.Min, Vec3.Zero, 0f, "empty min" );
			NearVec( snap.Bounds.Max, Vec3.Zero, 0f, "empty max" );
			Require( !service.Pick( snap, new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ) ).HasValue, "empty snapshot should pick nothing" );
			Require( service.BuildRenderMeshes( snap ).Count == 0, "empty snapshot should render nothing" );
		} );
	}

	sealed class Fixture
	{
		public AssemblyDocument Doc = new();
		public AssemblyPartDefinition Chair;
		public string AId;
		public string BId;
		public PolyMesh ChairMesh;
		public int Revision = 1;
		public AssemblySnapshotService Service = new();

		public Fixture()
		{
			Chair = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(),
				Name = "Chair",
				SourceKind = "studio-body",
				SourceReference = "studio#chair",
			};
			Doc.Definitions.Add( Chair );
			ChairMesh = BoxMesh();
			AId = AssemblyDocument.NewId();
			BId = AssemblyDocument.NewId();
			Doc.Occurrences.Add( new AssemblyOccurrence
			{
				Id = AId,
				DefinitionId = Chair.Id,
				Name = "Chair A",
				Transform = AssemblyTransform.FromXform( Xform.Identity ),
			} );
			Doc.Occurrences.Add( new AssemblyOccurrence
			{
				Id = BId,
				DefinitionId = Chair.Id,
				Name = "Chair B",
				Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 5f, 0f, 0f ) ) ),
			} );
		}

		public AssemblyOccurrence Occurrence( string id ) => Doc.Occurrences.First( o => o.Id == id );

		public (PolyMesh Mesh, int Revision, string MaterialName)? Resolve( AssemblyPartDefinition def )
		{
			if ( def is null || def.Id != Chair.Id )
				return null;
			return (ChairMesh, Revision, "oak");
		}

		public AssemblySourceResolver Resolver => Resolve;
	}

	static PolyMesh BoxMesh()
	{
		var mesh = new PolyMesh();
		mesh.Positions.AddRange( new[]
		{
			new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 1f, 1f, 0f ), new Vec3( 0f, 1f, 0f ),
			new Vec3( 0f, 0f, 1f ), new Vec3( 1f, 0f, 1f ), new Vec3( 1f, 1f, 1f ), new Vec3( 0f, 1f, 1f ),
		} );
		// Outward winding; signed volume +1.
		mesh.AddFace( new[] { 0, 2, 1 } ); mesh.AddFace( new[] { 0, 3, 2 } ); // bottom -Z
		mesh.AddFace( new[] { 4, 5, 6 } ); mesh.AddFace( new[] { 4, 6, 7 } ); // top +Z
		mesh.AddFace( new[] { 0, 1, 5 } ); mesh.AddFace( new[] { 0, 5, 4 } ); // front -Y
		mesh.AddFace( new[] { 3, 7, 6 } ); mesh.AddFace( new[] { 3, 6, 2 } ); // back +Y
		mesh.AddFace( new[] { 0, 4, 7 } ); mesh.AddFace( new[] { 0, 7, 3 } ); // left -X
		mesh.AddFace( new[] { 1, 2, 6 } ); mesh.AddFace( new[] { 1, 6, 5 } ); // right +X
		return mesh;
	}
}
