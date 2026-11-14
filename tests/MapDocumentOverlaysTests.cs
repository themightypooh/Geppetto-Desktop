using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

/// <summary>
/// MapDocument adapter for overlays, decals + path_track chains
/// (hammer-overlays-decals-paths, group "mapdocumentoverlays").
///
/// Built against the landed APIs: MapDocument / MapBrushData / MapEntityData /
/// MapDocumentIo (hammer-map-document-model), MapBrush / MapClip (hammer-brush-core,
/// hammer-brush-clip-two-point), MapVertexBrush / MapVertexEdit
/// (hammer-brush-vertex-edit-invalid-face) and OverlayDecalSet
/// (hammer-overlays-decals-paths). The room brush below is the 64-inch box with
/// the same face order as the overlay tests: 0:-X brick, 1:+X metal, 2:-Y wood,
/// 3:+Y glass, 4:-Z concrete, 5:+Z plaster.
///
/// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and
/// the final integrator adds the one MapDocumentOverlaysTests.Register(registry, settings);
/// line.
/// </summary>
internal static class MapDocumentOverlaysTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mapdocumentoverlays", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string mustContain = null ) where T : Exception
		{
			try { action(); }
			catch ( T ex )
			{
				if ( mustContain is not null && !ex.Message.Contains( mustContain, StringComparison.Ordinal ) )
					throw new Exception( $"expected error naming '{mustContain}', got: {ex.Message}" );
				return;
			}
			catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}
		void Approx( double actual, double expected, double tol, string what )
		{
			Require( Math.Abs( actual - expected ) <= tol, $"{what}: got {actual}, expected {expected} (tol {tol})" );
		}
		bool HasCode( IReadOnlyList<OverlayDecalIssue> issues, string code ) => issues.Any( i => i.Code == code );

		static string WorkDir( TestSettings settings, string name )
		{
			var dir = Path.Combine( settings.WorkDirectory, "mapdocumentoverlays", name );
			Directory.CreateDirectory( dir );
			return dir;
		}

		static MapDocument RoomDoc()
		{
			var doc = MapDocument.Create( "room" );
			doc.AddBrush( MapBrushData.Box( "room", "room", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ),
				new[] { "brick/brick", "metal/metal", "wood/wood", "glass/glass", "concrete/concrete", "plaster/plaster" } ) );
			return doc;
		}

		static MapOverlay ProjectionOverlay( MapBrush brush, params int[] faces )
			=> new MapOverlay( "marking", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec2( 1f / 64f, 1f / 64f ), FaceUv.Default,
				faces.Select( f => OverlayFaceRef.For( brush, "room", f ) ) );

		static OverlayDecalSet RoomSet( MapBrush brush )
		{
			var set = new OverlayDecalSet();
			set.AddOverlay( ProjectionOverlay( brush, 5, 1 ) );
			set.AddDecal( MapDecal.For( "poster", "decals/poster01", brush, "room", 5, new Vec3( 32, 32, 64 ), 8 ) );
			set.AddPath( new PathTrackChain( "train", new[]
			{
				new PathTrackNode( "track_a", new Vec3( 0, 0, 32 ), 64, "track_b" ),
				new PathTrackNode( "track_b", new Vec3( 64, 0, 32 ), 64, "track_c" ),
				new PathTrackNode( "track_c", new Vec3( 64, 64, 32 ), 0 ),
			} ) );
			return set;
		}

		Check( "adapter-validates-clean-against-doc-room", () =>
		{
			var doc = RoomDoc();
			var adapter = new MapDocumentOverlays( doc, RoomSet( MapDocumentOverlays.BrushesOf( doc )["room"] ) );
			var issues = adapter.Validate();
			Require( issues.Count == 0, "the room set must validate clean against the document: " +
				string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			Require( adapter.Brushes().Count == 1 && adapter.Brushes().ContainsKey( "room" ),
				"the adapter must resolve the document's one brush by id" );
			Throws<ArgumentNullException>( () => new MapDocumentOverlays( null ) );
			Throws<ArgumentNullException>( () => MapDocumentOverlays.BrushesOf( null ) );
		} );

		Check( "adapter-path-entities-export-and-persist", () =>
		{
			var doc = RoomDoc();
			var adapter = new MapDocumentOverlays( doc, RoomSet( MapDocumentOverlays.BrushesOf( doc )["room"] ) );
			var entities = adapter.PathEntities();
			Require( entities.Count == 3, $"expected 3 path_track entities, got {entities.Count}" );
			Require( entities.All( e => e.ClassName == "path_track" ), "every path entity must be class path_track" );
			Require( adapter.Document.Entities.Count == 0,
				"building the entities must not add them to the document — only AddPathsToDocument commits" );
			var byName = entities.ToDictionary( e => e.TargetName, e => e );
			Require( byName["track_a"].KeyValues["target"] == "track_b", "track_a must target track_b" );
			Require( byName["track_b"].KeyValues["target"] == "track_c", "track_b must target track_c" );
			Require( !byName["track_c"].KeyValues.ContainsKey( "target" ), "the end of the chain carries no target key" );
			Require( byName["track_a"].KeyValues["speed"] == "64", $"track_a speed must be '64', got '{byName["track_a"].KeyValues["speed"]}'" );
			Require( byName["track_c"].KeyValues["speed"] == "0", $"track_c speed must be '0', got '{byName["track_c"].KeyValues["speed"]}'" );
			Require( byName["track_b"].Origin.Equals( new Vec3( 64, 0, 32 ) ), "track_b origin must be the node position" );
			var added = adapter.AddPathsToDocument();
			Require( added.Count == 3, $"expected 3 added entity ids, got {added.Count}" );
			Require( doc.Entities.Count == 3, $"expected 3 entities in the document, got {doc.Entities.Count}" );
			Require( doc.FindEntityByTargetName( "track_b" ) is not null, "track_b must be findable by targetname" );
			var dir = WorkDir( settings, "persist" );
			var path = Path.Combine( dir, "room.gmap" );
			MapDocumentIo.Save( doc, path );
			var loaded = MapDocumentIo.Open( path );
			Require( loaded.Entities.Count == 3, $"expected 3 entities after reload, got {loaded.Entities.Count}" );
			foreach ( var entity in doc.Entities )
			{
				var other = loaded.FindEntity( entity.Id );
				Require( other is not null && other.SameAs( entity ), $"path_track '{entity.TargetName}' did not round-trip through the .gmap" );
			}
		} );

		Check( "adapter-json-round-trip-and-wrong-document-throws", () =>
		{
			var doc = RoomDoc();
			var adapter = new MapDocumentOverlays( doc, RoomSet( MapDocumentOverlays.BrushesOf( doc )["room"] ) );
			var text = adapter.ToJson().ToJsonString();
			var parsed = MapDocumentOverlays.FromJsonString( doc, text );
			Require( ReferenceEquals( parsed.Document, doc ), "the parsed adapter must bind to the same document" );
			Require( parsed.Set.Overlays.Count == 1 && parsed.Set.Decals.Count == 1 && parsed.Set.Paths.Count == 1,
				$"the parsed set must hold 1/1/1, got {parsed.Set.Overlays.Count}/{parsed.Set.Decals.Count}/{parsed.Set.Paths.Count}" );
			Require( parsed.Set.Overlays[0].Id == "marking" && parsed.Set.Decals[0].Id == "poster" && parsed.Set.Paths[0].Name == "train",
				"the parsed set must keep the overlay / decal / chain ids" );
			Require( parsed.Set.Overlays[0].Targets.Count == 2, "the parsed overlay must keep both target faces" );
			var other = MapDocument.Create( "other" );
			Throws<ArgumentException>( () => MapDocumentOverlays.FromJsonString( other, text ), "belongs to document" );
			Throws<ArgumentException>( () => MapDocumentOverlays.FromJsonString( doc, "" ), "blank" );
			Throws<ArgumentException>( () => MapDocumentOverlays.FromJson( doc, new JsonObject() ), "missing 'documentId'" );
		} );

		Check( "adapter-apply-clip-through-doc", () =>
		{
			var doc = RoomDoc();
			var brush = MapDocumentOverlays.BrushesOf( doc )["room"];
			var set = new OverlayDecalSet();
			set.AddOverlay( ProjectionOverlay( brush, 2, 1 ) );
			var adapter = new MapDocumentOverlays( doc, set );
			var result = MapClip.Clip( brush, new Vec3( 32, 0, 0 ), new Vec3( 32, 0, 64 ), ClipViewAxis.Y );
			Require( result.Outcome == ClipOutcome.Cut, $"outcome was {result.Outcome}, expected Cut" );
			var report = adapter.ApplyClip( "room", result );
			Require( report.Survived.Count == 1, $"expected 1 surviving target, got {report.Survived.Count}" );
			Require( report.Survived[0].Target.FaceIndex == 2, "the surviving target must be the wood face 2" );
			Approx( report.Survived[0].Uvs.Max( uv => uv.x ), 0.5, 1e-6,
				"the surviving wood face must be re-projected to the clipped half (max U 0.5)" );
			Require( report.Lost.Count == 1 && report.Lost[0].Code == OverlayDecalIssue.OverlayFaceLost,
				"the cut-away metal face must be one OverlayFaceLost" );
			Require( adapter.Validate().Count == 0,
				"the clip report does not rewrite the document's brushes — the set still fits the doc as stored; applying the clip to the doc itself is the clip ticket's job" );
		} );

		Check( "adapter-vertex-edit-reprojects-through-doc", () =>
		{
			var doc = RoomDoc();
			var brushData = doc.FindBrush( "room" );
			var planeBrush = brushData.ToMapBrush();
			var vertexBrush = brushData.ToVertexBrush();
			var topFace = Array.IndexOf( vertexBrush.Materials, "plaster/plaster" );
			var leftFace = Array.IndexOf( vertexBrush.Materials, "brick/brick" );
			Require( topFace >= 0 && leftFace >= 0, "the vertex brush must carry the plaster top and brick left faces" );
			// Vertex-form face indices, plane-form planes: the ref's index is the
			// vertex identity, its plane/material the clip identity (see OverlaysDecals.cs).
			var set = new OverlayDecalSet();
			set.AddOverlay( new MapOverlay( "marking", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec2( 1f / 64f, 1f / 64f ), FaceUv.Default,
				new[]
				{
					new OverlayFaceRef( "room", topFace, planeBrush.Faces[5].Normal, planeBrush.Faces[5].Distance, "plaster/plaster" ),
					new OverlayFaceRef( "room", leftFace, planeBrush.Faces[0].Normal, planeBrush.Faces[0].Distance, "brick/brick" ),
				} ) );
			var adapter = new MapDocumentOverlays( doc, set );
			// Move the top corner (64,0,64) to (48,0,64): the top face stretches, the left face is untouched.
			var corner = -1;
			var best = float.MaxValue;
			for ( var i = 0; i < vertexBrush.Vertices.Length; i++ )
			{
				var d = ( vertexBrush.Vertices[i] - new Vec3( 64, 0, 64 ) ).Length;
				if ( d < best ) { best = d; corner = i; }
			}
			Require( corner >= 0 && best <= 1e-3f, $"no vertex sits at the top corner (64,0,64); nearest was {best} away" );
			var edit = MapVertexEdit.MoveVertex( vertexBrush, corner, new Vec3( 48, 0, 64 ) );
			Require( edit.AffectedFaces.Contains( topFace ), "the move must affect the top face" );
			Require( !edit.AffectedFaces.Contains( leftFace ), "the move must not affect the left face" );
			var report = adapter.ApplyVertexEdit( "room", vertexBrush, edit.Brush, edit.AffectedFaces );
			Require( report.Lost.Count == 0 && report.NeedsRetarget.Count == 0,
				"no target may be lost or split by a plain vertex move" );
			Require( report.Reprojected.Count == 1, $"expected 1 re-projected target, got {report.Reprojected.Count}" );
			Require( report.Reprojected[0].FaceIndex == topFace, "the re-projected target must be the top face" );
			Require( report.Reprojected[0].Uvs.Any( uv => Math.Abs( uv.x - 0.75 ) < 1e-6 && Math.Abs( uv.y - 1 ) < 1e-6 ),
				"the moved corner must re-project to U 0.75 (x=48/64), V 1 (z=64)" );
			Approx( report.Reprojected[0].Uvs.Max( uv => uv.x ), 1.0, 1e-6,
				"the top face max U stays 1: the other x=64 corner did not move" );
			Require( report.Unchanged.Count == 1, $"expected 1 unchanged target, got {report.Unchanged.Count}" );
			Require( report.Unchanged[0].FaceIndex == leftFace, "the unchanged target must be the left face" );
			foreach ( var uv in report.Unchanged[0].Uvs )
				Approx( uv.x, 0.0, 1e-6, "left face U is unchanged (x=0 everywhere)" );
		} );

		Check( "adapter-validation-hook-names-broken-chain", () =>
		{
			var doc = RoomDoc();
			var brush = MapDocumentOverlays.BrushesOf( doc )["room"];
			var broken = new OverlayDecalSet();
			broken.AddPath( new PathTrackChain( "train", new[]
			{
				new PathTrackNode( "track_a", new Vec3( 0, 0, 32 ), 64, "track_b" ),
				new PathTrackNode( "track_b", new Vec3( 64, 0, 32 ), 64, "missing-node" ),
			} ) );
			doc.ValidationHook = MapDocumentOverlays.ValidationHook( broken );
			var hookIssues = doc.ValidationHook( doc ).ToList();
			Require( hookIssues.Any( s => s.Contains( OverlayDecalIssue.PathMissingNode, StringComparison.Ordinal ) ),
				"the hook must report the broken chain as PathMissingNode: " + string.Join( "; ", hookIssues ) );
			Require( hookIssues.Any( s => s.Contains( "missing-node", StringComparison.Ordinal ) ),
				"the hook issue must name the missing node" );
			var clean = RoomSet( brush );
			var cleanIssues = MapDocumentOverlays.ValidationHook( clean )( doc ).ToList();
			Require( cleanIssues.Count == 0, "the hook must be silent for a clean set: " + string.Join( "; ", cleanIssues ) );
			Throws<ArgumentNullException>( () => MapDocumentOverlays.ValidationHook( null ) );
		} );

		Check( "adapter-path-targetname-clash-throws", () =>
		{
			var doc = RoomDoc();
			var brush = MapDocumentOverlays.BrushesOf( doc )["room"];
			var squatter = new MapEntityData
			{
				Id = MapDocument.NewId(),
				ClassName = "info_player_start",
				Origin = new Vec3( 32, 32, 32 ),
			};
			squatter.KeyValues["targetname"] = "track_a";
			doc.AddEntity( squatter );
			var adapter = new MapDocumentOverlays( doc, RoomSet( brush ) );
			Throws<InvalidOperationException>( () => adapter.AddPathsToDocument(), "track_a" );
			Require( doc.Entities.Count == 1, "a refused add must leave the document untouched" );
			Require( doc.FindEntityByTargetName( "track_b" ) is null, "no path entity may have been added before the throw" );
		} );
	}
}
