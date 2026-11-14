#nullable enable
using System;
using System.IO;
using System.Linq;
using Effigy;

// Hammer map document (hammer-map-document-model, worker muse-3-desktop-hammer-12).
// Backend only; no UI. The sealed room below (6 brushes + player start + light)
// is the acceptance room from the ticket: it saves, reloads and compares identical.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and
// the final integrator adds the one MapDocumentTests.Register(registry, settings);
// line. To run this group in isolation, claim TestRegistry.cs, add the line, run
// dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter mapdocument,
// then remove the line and verify the file is byte-identical.
internal static class MapDocumentTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "mapdocument", "create empty document", () =>
		{
			var doc = MapDocument.Create( "room" );
			Assert( doc.Name == "room", $"name should be room, got {doc.Name}" );
			Assert( doc.Brushes.Count == 0 && doc.Entities.Count == 0 && doc.Layers.Count == 0, "fresh document must be empty" );
			Assert( doc.LoadedFormatVersion == MapDocument.CurrentFormatVersion, "fresh document stamps the current format" );
			Assert( doc.DocumentId.Length == 8, $"document id should be 8 hex, got {doc.DocumentId}" );
			Assert( doc.Revision == 0, $"fresh revision should be 0, got {doc.Revision}" );
			Assert( doc.IsDirty, "a document that was never saved is dirty" );
			Assert( doc.LastRebuildReport.EntriesFolded == 0, "fresh document folds zero entries" );
		} );

		registry.Add( "mapdocument", "sealed room round-trips", () =>
		{
			var dir = WorkDir( settings, "roundtrip" );
			var room = SealedRoom();
			var path = Path.Combine( dir, "room.gmap" );
			MapDocumentIo.Save( room, path );
			Assert( !room.IsDirty, "a saved document is clean" );
			Assert( room.SourcePath == path, "save records the source path" );
			var loaded = MapDocumentIo.Open( path );
			Assert( loaded.Brushes.Count == 6, $"expected 6 brushes, got {loaded.Brushes.Count}" );
			Assert( loaded.Entities.Count == 2, $"expected 2 entities, got {loaded.Entities.Count}" );
			Assert( loaded.Layers.Count == 1 && loaded.VisGroups.Count == 1, "expected 1 layer and 1 visgroup" );
			Assert( loaded.History.Count == 11, $"expected 11 history entries, got {loaded.History.Count}" );
			Assert( loaded.RollbackIndex == int.MaxValue, "reload keeps the bar at the end" );
			Assert( !loaded.IsDirty, "a freshly opened document is clean" );
			foreach ( var brush in room.Brushes )
			{
				var other = loaded.FindBrush( brush.Id );
				Assert( other is not null && other.SameAs( brush ), $"brush {brush.Id} did not round-trip" );
			}
			foreach ( var entity in room.Entities )
			{
				var other = loaded.FindEntity( entity.Id );
				Assert( other is not null && other.SameAs( entity ), $"entity {entity.Id} did not round-trip" );
			}
			Assert( loaded.CompileSettings.SameAs( room.CompileSettings ), "compile settings did not round-trip" );
			Assert( loaded.FindLayer( "layer-walls" )?.SameAs( room.FindLayer( "layer-walls" ) ) == true, "layer did not round-trip" );
			Assert( loaded.FindVisGroup( "vis-all" )?.SameAs( room.FindVisGroup( "vis-all" ) ) == true, "visgroup did not round-trip" );
			Assert( loaded.LoadNotes.Count == 0, $"v1 reload should need no notes, got: {string.Join( "; ", loaded.LoadNotes )}" );
		} );

		registry.Add( "mapdocument", "save bytes are stable", () =>
		{
			var dir = WorkDir( settings, "stable" );
			var room = SealedRoom();
			var a = Path.Combine( dir, "a.gmap" );
			var b = Path.Combine( dir, "b.gmap" );
			MapDocumentIo.Save( room, a );
			MapDocumentIo.Save( room, b );
			var bytesA = File.ReadAllBytes( a );
			var bytesB = File.ReadAllBytes( b );
			Assert( bytesA.SequenceEqual( bytesB ), "two saves of the same document must be byte-identical" );
		} );

		registry.Add( "mapdocument", "rollback restores earlier state", () =>
		{
			var dir = WorkDir( settings, "rollback" );
			var room = SealedRoom();
			room.RollbackTo( 8 );
			Assert( room.Brushes.Count == 6 && room.Entities.Count == 0, $"rolled back to 8: expected 6 brushes 0 entities, got {room.Brushes.Count}/{room.Entities.Count}" );
			Assert( room.EffectiveCount == 8, $"effective count should be 8, got {room.EffectiveCount}" );
			var path = Path.Combine( dir, "rolled.gmap" );
			MapDocumentIo.Save( room, path );
			var loaded = MapDocumentIo.Open( path );
			Assert( loaded.Entities.Count == 0 && loaded.RollbackIndex == 8, "rollback persists across save/load" );
			loaded.RollbackTo( int.MaxValue );
			Assert( loaded.Entities.Count == 2 && loaded.Brushes.Count == 6, "rolling back to the end restores the room" );
		} );

		registry.Add( "mapdocument", "new edit past the bar drops redo", () =>
		{
			var room = SealedRoom();
			room.RollbackTo( 8 );
			room.AddBrush( MapBrushData.Box( "brush-extra", "Extra", new Vec3( 600f, 600f, 0f ), new Vec3( 640f, 640f, 64f ), "concrete/wall" ) );
			Assert( room.History.Count == 9, $"expected 9 entries after the dropped redo, got {room.History.Count}" );
			Assert( room.Brushes.Count == 7, $"expected 7 brushes, got {room.Brushes.Count}" );
			Assert( room.Entities.Count == 0, "the entity ops past the bar must be gone" );
		} );

		registry.Add( "mapdocument", "old version migrates with a named note", () =>
		{
			var text = File.ReadAllText( Fixture( settings, "sealed-room-v0.gmap" ) );
			var doc = MapDocumentIo.Read( text );
			Assert( doc.LoadedFormatVersion == 0, $"expected loaded format 0, got {doc.LoadedFormatVersion}" );
			Assert( doc.LoadNotes.Any( n => n.Contains( "Migrated map format 0 to 1", StringComparison.Ordinal ) ),
				$"migration must be named in LoadNotes, got: {string.Join( "; ", doc.LoadNotes )}" );
			Assert( doc.Brushes.Count == 6, $"v0 room should have 6 brushes, got {doc.Brushes.Count}" );
			Assert( doc.Entities.Count == 2, $"v0 room should have 2 entities, got {doc.Entities.Count}" );
			Assert( doc.Layers.Count == 1, "v0 room should have 1 layer" );
			Assert( doc.FindLayer( "layer-walls" )?.CompileVisible == true, "v0 layer compile-visible defaults to visible" );
			Assert( doc.CompileSettings.SkyName == "sky_day01_01", $"v0 sky should be sky_day01_01, got {doc.CompileSettings.SkyName}" );
			Assert( doc.FindEntity( "ent-light" )?.TargetName == "main light", "v0 light targetname must survive" );
			var floor = doc.FindBrush( "brush-floor" );
			float volume = 0f;
			Assert( floor is not null && floor.TryComputeVolume( out volume ) && MathF.Abs( volume - 512f * 512f * 16f ) < 1f,
				$"v0 floor volume should be 4194304, got {volume}" );
		} );

		registry.Add( "mapdocument", "v1 golden fixture loads", () =>
		{
			var text = File.ReadAllText( Fixture( settings, "sealed-room-v1.gmap" ) );
			var doc = MapDocumentIo.Read( text );
			Assert( doc.LoadNotes.Count == 0, $"v1 fixture should need no notes, got: {string.Join( "; ", doc.LoadNotes )}" );
			var room = SealedRoom();
			Assert( doc.Brushes.Count == 6 && doc.Entities.Count == 2, "v1 fixture should hold the sealed room" );
			foreach ( var brush in room.Brushes )
			{
				var other = doc.FindBrush( brush.Id );
				Assert( other is not null && other.SameAs( brush ), $"v1 fixture brush {brush.Id} differs from the builder room" );
			}
			foreach ( var entity in room.Entities )
			{
				var other = doc.FindEntity( entity.Id );
				Assert( other is not null && other.SameAs( entity ), $"v1 fixture entity {entity.Id} differs from the builder room" );
			}
		} );

		registry.Add( "mapdocument", "newer version is refused by name", () =>
		{
			var text = MapDocumentIo.Write( SealedRoom() ).Replace( "gmap 1", "gmap 2", StringComparison.Ordinal );
			var ex = Throws<InvalidDataException>( () => MapDocumentIo.Read( text ), "2" );
			Assert( ex.Message.Contains( "1", StringComparison.Ordinal ), $"refusal must name the readable version: {ex.Message}" );
		} );

		registry.Add( "mapdocument", "save refuses a zero-volume brush", () =>
		{
			var dir = WorkDir( settings, "zerovolume" );
			var doc = MapDocument.Create( "flat" );
			doc.AddBrush( MapBrushData.Box( "brush-flat", "Flat", new Vec3( 256f, 256f, 32f ), new Vec3( 256f, 256f, 32f ), "concrete/wall" ) );
			var defects = doc.Validate();
			Assert( defects.Any( d => d.Contains( "volume", StringComparison.OrdinalIgnoreCase ) ),
				$"zero-volume brush must be a named defect, got: {string.Join( "; ", defects )}" );
			var path = Path.Combine( dir, "flat.gmap" );
			Throws<InvalidDataException>( () => MapDocumentIo.Save( doc, path ), "volume" );
			Assert( !File.Exists( path ), "a refused save must not create the file" );
			Assert( doc.IsDirty, "a refused save must not clear the dirty flag" );
		} );

		registry.Add( "mapdocument", "save refuses too few faces", () =>
		{
			var dir = WorkDir( settings, "fewfaces" );
			var doc = MapDocument.Create( "few" );
			var brush = new MapBrushData { Id = "brush-triangle", Name = "Triangle" };
			brush.Faces.Add( new MapBrushFaceData( new Vec3( -1f, 0f, 0f ), 0f, "concrete/wall" ) );
			brush.Faces.Add( new MapBrushFaceData( new Vec3( 1f, 0f, 0f ), 10f, "concrete/wall" ) );
			brush.Faces.Add( new MapBrushFaceData( new Vec3( 0f, 0f, 1f ), 10f, "concrete/wall" ) );
			doc.AddBrush( brush );
			var defects = doc.Validate();
			Assert( defects.Any( d => d.Contains( "at least 4", StringComparison.Ordinal ) ),
				$"a 3-face brush must be a named defect, got: {string.Join( "; ", defects )}" );
			Throws<InvalidDataException>( () => MapDocumentIo.Save( doc, Path.Combine( dir, "few.gmap" ) ), "at least 4" );
		} );

		registry.Add( "mapdocument", "validation hook runs on save", () =>
		{
			var dir = WorkDir( settings, "hook" );
			var doc = SealedRoom();
			doc.ValidationHook = _ => new[] { "custom defect from the hook" };
			Throws<InvalidDataException>( () => MapDocumentIo.Save( doc, Path.Combine( dir, "hooked.gmap" ) ), "custom defect" );
			doc.ValidationHook = _ => Array.Empty<string>();
			MapDocumentIo.Save( doc, Path.Combine( dir, "hooked.gmap" ) );
		} );

		registry.Add( "mapdocument", "registry validation rejects unknown class and key", () =>
		{
			var registry = LoadCoreRegistry( settings );
			Assert( registry.Contains( "light" ) && registry.Contains( "info_player_start" ),
				"the core registry must carry light and info_player_start" );
			var room = SealedRoom();
			Assert( room.Validate( registry ).Count == 0,
				$"the sealed room must validate against the core registry, got: {string.Join( "; ", room.Validate( registry ) )}" );
			var badClass = MapDocument.Create( "badclass" );
			badClass.AddEntity( new MapEntityData { Id = "ent-bad", ClassName = "not_a_real_class", Origin = new Vec3( 0f, 0f, 0f ) } );
			Assert( badClass.Validate( registry ).Any( d => d.Contains( "UnknownEntityClass", StringComparison.Ordinal ) ),
				$"unknown class must be a named defect, got: {string.Join( "; ", badClass.Validate( registry ) )}" );
			var badKey = MapDocument.Create( "badkey" );
			var light = new MapEntityData { Id = "ent-light", ClassName = "light", Origin = new Vec3( 0f, 0f, 0f ) };
			light.KeyValues["not_a_key"] = "1";
			badKey.AddEntity( light );
			Assert( badKey.Validate( registry ).Any( d => d.Contains( "UnknownKeyValue", StringComparison.Ordinal ) ),
				$"unknown key must be a named defect, got: {string.Join( "; ", badKey.Validate( registry ) )}" );
		} );

		registry.Add( "mapdocument", "layer and visgroup round-trip with compile filter", () =>
		{
			var dir = WorkDir( settings, "layers" );
			var doc = MapDocument.Create( "layers" );
			doc.AddLayer( new MapLayer { Id = "layer-hidden", Name = "Hidden", Colour = "#FF0000", Visible = true, CompileVisible = false } );
			doc.AddVisGroup( new MapVisGroup { Id = "vis-off", Name = "Off", Colour = "#00FF00", Visible = false, CompileVisible = true } );
			var brush = MapBrushData.Box( "brush-a", "A", new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), "concrete/wall" );
			brush.LayerId = "layer-hidden";
			brush.VisGroupIds.Add( "vis-off" );
			doc.AddBrush( brush );
			doc.SetCompileSettings( new MapCompileSettings { SkyName = "sky_day01_01", CompileVisibleOnly = true, DefaultLightmapScale = 16f } );
			Assert( doc.CompileVisibleBrushes().Count == 0, "a brush on a compile-hidden layer must not compile when CompileVisibleOnly is on" );
			Assert( doc.VisibleBrushes().Count == 0, "a brush in a hidden visgroup must not show" );
			doc.SetCompileSettings( new MapCompileSettings { SkyName = "sky_day01_01", CompileVisibleOnly = false, DefaultLightmapScale = 16f } );
			Assert( doc.CompileVisibleBrushes().Count == 1, "CompileVisibleOnly off compiles everything" );
			var path = Path.Combine( dir, "layers.gmap" );
			MapDocumentIo.Save( doc, path );
			var loaded = MapDocumentIo.Open( path );
			Assert( loaded.FindLayer( "layer-hidden" )?.CompileVisible == false, "layer compile-visible must round-trip" );
			Assert( loaded.FindVisGroup( "vis-off" )?.Visible == false, "visgroup eye toggle must round-trip" );
			loaded.SetLayerVisible( "layer-hidden", false );
			Assert( loaded.VisibleBrushes().Count == 0, "hiding the layer hides the brush" );
		} );

		registry.Add( "mapdocument", "unknown op fails closed", () =>
		{
			var text = MapDocumentIo.Write( SealedRoom() ) + "op teleport-brush brush-floor\n";
			Throws<InvalidDataException>( () => MapDocumentIo.Read( text ), "teleport-brush" );
			var garbage = MapDocumentIo.Write( SealedRoom() ) + "nonsense 1\n";
			Throws<InvalidDataException>( () => MapDocumentIo.Read( garbage ), "expected" );
		} );

		registry.Add( "mapdocument", "displacement round-trips", () =>
		{
			var dir = WorkDir( settings, "displacement" );
			var doc = SealedRoom();
			var disp = MapDisplacementData.Flat( "brush-floor", 5, 2 );
			disp.Offsets[12] = 2.5f;
			disp.Alphas[0] = 0.5f;
			doc.AddDisplacement( disp );
			Assert( doc.Displacements.Count == 1, "displacement must be in the document" );
			var path = Path.Combine( dir, "disp.gmap" );
			MapDocumentIo.Save( doc, path );
			var loaded = MapDocumentIo.Open( path );
			var other = loaded.FindDisplacement( "brush-floor", 5 );
			Assert( other is not null && other.SameAs( disp ), "displacement did not round-trip" );
			Assert( other!.ToDisplacementState().Heights[12] == 2.5f, "displacement heights must survive as offsets" );
			var bad = new MapDisplacementData
			{
				Id = "brush-floor#5",
				BrushId = "brush-floor",
				FaceIndex = 5,
				Power = 5,
				Offsets = Array.Empty<float>(),
				Alphas = Array.Empty<float>(),
			};
			Assert( bad.Validate().Any( d => d.Contains( "power", StringComparison.OrdinalIgnoreCase ) ),
				$"power 5 must be a named defect, got: {string.Join( "; ", bad.Validate() )}" );
			Throws<InvalidOperationException>( () => doc.AddDisplacement( bad ), "power" );
		} );

		registry.Add( "mapdocument", "brush volume and centroid are exact", () =>
		{
			var floor = MapBrushData.Box( "brush-floor", "Floor", new Vec3( 0f, 0f, 0f ), new Vec3( 512f, 512f, 16f ), "concrete/floor" );
			Assert( floor.TryComputeVolume( out var volume ) && MathF.Abs( volume - 4194304f ) < 1f, $"floor volume should be 4194304, got {volume}" );
			var centroid = floor.Centroid();
			Assert( MathF.Abs( centroid.x - 256f ) < 1e-3f && MathF.Abs( centroid.y - 256f ) < 1e-3f && MathF.Abs( centroid.z - 8f ) < 1e-3f, $"floor centroid should be (256,256,8), got ({centroid.x},{centroid.y},{centroid.z})" );
			var vertexBrush = floor.ToVertexBrush();
			Assert( vertexBrush.VertexCount == 8 && vertexBrush.FaceCount == 6, $"floor vertex form should be 8 verts / 6 faces, got {vertexBrush.VertexCount}/{vertexBrush.FaceCount}" );
			Assert( vertexBrush.Materials.All( m => m == "concrete/floor" ), "floor vertex form must carry the face material" );
			var wall = MapBrushData.Box( "brush-wall", "Wall", new Vec3( 0f, 0f, 0f ), new Vec3( 16f, 512f, 256f ), "concrete/wall" );
			Assert( wall.TryComputeVolume( out var wallVolume ) && MathF.Abs( wallVolume - 2097152f ) < 1f, $"wall volume should be 2097152, got {wallVolume}" );
		} );

		registry.Add( "mapdocument", "undo target round-trips a move", () =>
		{
			var room = SealedRoom();
			IMapUndoTarget target = room;
			var rev0 = target.Revision;
			target.AddBrush( new MapBrushState { Id = "brush-move", Min = new Vec3( 600f, 600f, 0f ), Max = new Vec3( 640f, 640f, 64f ), Material = "concrete/wall" } );
			Assert( target.Revision == rev0 + 1, $"AddBrush must bump Revision exactly once ({rev0} -> {target.Revision})" );
			target.MoveBrush( "brush-move", new Vec3( 600f, 600f, 64f ), new Vec3( 640f, 640f, 128f ) );
			Assert( target.Revision == rev0 + 2, "MoveBrush must bump Revision exactly once" );
			var moved = target.FindBrush( "brush-move" );
			Assert( moved is not null && moved.Min.Equals( new Vec3( 600f, 600f, 64f ) ) && moved.Max.Equals( new Vec3( 640f, 640f, 128f ) ),
				"MoveBrush must move the AABB" );
			target.SetBrushMaterial( "brush-move", "brick/brickwall001" );
			Assert( room.FindBrush( "brush-move" )?.Faces.All( f => f.Material.MaterialName == "brick/brickwall001" ) == true,
				"SetBrushMaterial must retexture every face" );
			target.AddEntity( new MapEntityState { Id = "ent-undo", ClassName = "light", Origin = new Vec3( 0f, 0f, 0f ) } );
			target.SetEntityProperty( "ent-undo", "_light", "255 255 255 200" );
			Assert( room.FindEntity( "ent-undo" )?.KeyValues["_light"] == "255 255 255 200", "SetEntityProperty must land" );
			target.SetEntityProperty( "ent-undo", "angles", "0 45 0" );
			Assert( room.FindEntity( "ent-undo" )?.Angles.Equals( new Vec3( 0f, 45f, 0f ) ) == true
				&& room.FindEntity( "ent-undo" )!.KeyValues.ContainsKey( "angles" ) == false,
				"SetEntityProperty 'angles' must move the Angles field, not store a keyvalue" );
			var snapshot = target.CaptureSnapshot();
			target.MoveBrush( "brush-move", new Vec3( 0f, 0f, 0f ), new Vec3( 10f, 10f, 10f ) );
			target.ApplySnapshot( snapshot );
			var restored = target.FindBrush( "brush-move" );
			Assert( restored is not null && restored.Min.Equals( new Vec3( 600f, 600f, 64f ) ),
				"ApplySnapshot must restore the captured bounds" );
			Assert( room.FindEntity( "ent-spawn" )?.Angles.Equals( new Vec3( 0f, 90f, 0f ) ) == true,
				"ApplySnapshot must keep the spawn's 90-degree yaw" );
			var json = target.SerializeSnapshot();
			var other = MapDocument.Create( "other" );
			( (IMapUndoTarget)other ).RestoreSnapshot( json );
			Assert( other.Brushes.Count == room.Brushes.Count && other.Entities.Count == room.Entities.Count,
				$"snapshot restore must be lossless: {other.Brushes.Count}/{other.Entities.Count} vs {room.Brushes.Count}/{room.Entities.Count}" );
			Assert( other.FindEntity( "ent-undo" )?.KeyValues["_light"] == "255 255 255 200", "snapshot restore must keep keyvalues" );
			Assert( other.FindEntity( "ent-spawn" )?.Angles.Equals( new Vec3( 0f, 90f, 0f ) ) == true,
				"snapshot restore must keep the spawn's 90-degree yaw" );
			target.MarkSaved();
			Assert( !target.IsDirty, "MarkSaved must clear the dirty flag" );
		} );

		registry.Add( "mapdocument", "entity io embeds and round-trips", () =>
		{
			var dir = WorkDir( settings, "io" );
			var doc = SealedRoom();
			doc.AddIo( "io-1", new EntityIoOutput( "spawn1", "OnPressed", "main light", "TurnOn", 0.5, 1, "", true ) );
			Assert( doc.Validate().Count == 0, $"io referencing live targetnames must validate, got: {string.Join( "; ", doc.Validate() )}" );
			var path = Path.Combine( dir, "io.gmap" );
			MapDocumentIo.Save( doc, path );
			var loaded = MapDocumentIo.Open( path );
			Assert( loaded.Io.Count == 1, $"expected 1 io row, got {loaded.Io.Count}" );
			var row = loaded.Io.Outputs[0];
			Assert( row.SourceEntity == "spawn1" && row.Event == "OnPressed" && row.TargetName == "main light" && row.Input == "TurnOn",
				"io row did not round-trip" );
			Assert( row.DelaySeconds == 0.5 && row.MaxFires == 1, "io row delay/fires did not round-trip" );
			var parsed = EntityIoList.FromJson( loaded.Io.ToJson() );
			Assert( parsed.Count == 1 && parsed.Outputs[0].Equals( row ), "entity_io JSON must parse back to the same row" );
			IMapUndoTarget target = loaded;
			target.RemoveIo( "io-1" );
			Assert( loaded.Io.Count == 0, "RemoveIo must drop the row" );
		} );

		registry.Add( "mapdocument", "duplicate brush id is refused", () =>
		{
			var room = SealedRoom();
			var dup = MapBrushData.Box( "brush-floor", "Copy", new Vec3( 0f, 0f, 0f ), new Vec3( 8f, 8f, 8f ), "concrete/wall" );
			Throws<InvalidOperationException>( () => room.AddBrush( dup ), "brush-floor" );
			Assert( room.Brushes.Count == 6, "a refused add must not change the document" );
		} );

		registry.Add( "mapdocument", "issue engine projection carries faces", () =>
		{
			var room = SealedRoom();
			var issue = room.ToIssueDocument();
			Assert( issue.Brushes.Count == 6 && issue.Entities.Count == 2, $"projection should carry 6 brushes 2 entities, got {issue.Brushes.Count}/{issue.Entities.Count}" );
			var floor = issue.Brushes[0];
			Assert( floor.Faces.Count == 6, $"projected floor should have 6 faces, got {floor.Faces.Count}" );
			Assert( Math.Abs( floor.Volume() - 4194304.0 ) < 1.0, $"projected floor volume should be 4194304, got {floor.Volume()}" );
			Assert( issue.Entities[0].TargetName == "spawn1", "projected entity must keep its targetname" );
		} );

		registry.Add( "mapdocument", "mapbrush conversion round-trips and agrees with MapBrush.Validate", () =>
		{
			var room = SealedRoom();
			foreach ( var brush in room.Brushes )
			{
				var core = brush.ToMapBrush();
				Assert( core.IsValid, $"room brush '{brush.Id}' must be valid to the plane core, got: {string.Join( "; ", core.Validate().Select( d => d.ToString() ) )}" );
				Assert( brush.TryComputeVolume( out var volume ) && MathF.Abs( core.Volume - volume ) < volume * 1e-5f,
					$"core volume must agree with the document volume for '{brush.Id}': {core.Volume} vs {volume}" );
				var centroid = brush.Centroid();
				var coreCentroid = core.Centroid;
				Assert( MathF.Abs( coreCentroid.x - centroid.x ) < 1e-3f && MathF.Abs( coreCentroid.y - centroid.y ) < 1e-3f && MathF.Abs( coreCentroid.z - centroid.z ) < 1e-3f,
					$"core centroid must agree for '{brush.Id}': ({coreCentroid.x},{coreCentroid.y},{coreCentroid.z}) vs ({centroid.x},{centroid.y},{centroid.z})" );
				var back = MapBrushData.FromMapBrush( core, brush.Id, brush.Name );
				// FromMapBrush preserves planes/materials/UV only: layer + visgroup
				// membership is document-level and comes back empty by design, so
				// compare geometry here, not full SameAs (which includes LayerId).
				Assert( back.Id == brush.Id && back.Name == brush.Name && back.Faces.Count == brush.Faces.Count
					&& back.Faces.Zip( brush.Faces, ( a, b ) => a.SameAs( b ) ).All( same => same ),
					$"FromMapBrush must round-trip brush '{brush.Id}' planes/materials" );
			}
			var flat = MapBrushData.Box( "brush-flat", "Flat", new Vec3( 256f, 256f, 32f ), new Vec3( 256f, 256f, 32f ), "concrete/wall" );
			var flatCore = flat.ToMapBrush();
			Assert( !flatCore.IsValid && flatCore.Validate().Any( d => d.Code == MapBrushDefect.ZeroVolume ),
				$"the plane core must refuse the flat brush with ZeroVolume, got: {string.Join( "; ", flatCore.Validate().Select( d => d.ToString() ) )}" );
			var floor = room.FindBrush( "brush-floor" );
			System.Collections.Generic.List<Vec3> corners = new();
			Assert( floor is not null && floor.TryComputeFaceVertices( 0, out corners ) && corners.Count == 4,
				"floor face 0 must solve to a 4-corner quad for displacement binding" );
			var dispFace = floor!.Faces[0].ToDisplacementFace( corners.ToArray(), 7 );
			Assert( dispFace.MaterialId == 7 && dispFace.Corners.Length == 4,
				"ToDisplacementFace must carry the caller's slot and 4 corners" );
		} );
	}

	// --- the acceptance room: 6 brushes seal a 512x512x256 room, 16 thick -------

	static MapDocument SealedRoom()
	{
		var doc = MapDocument.Create( "sealed-room" );
		doc.AddLayer( new MapLayer { Id = "layer-walls", Name = "Walls", Colour = "#808080", Visible = true, CompileVisible = true } );
		doc.AddVisGroup( new MapVisGroup { Id = "vis-all", Name = "All", Colour = "#4040FF", Visible = true, CompileVisible = true } );
		AddWall( doc, "brush-floor", "Floor", new Vec3( 0f, 0f, 0f ), new Vec3( 512f, 512f, 16f ), "concrete/floor", false );
		AddWall( doc, "brush-ceiling", "Ceiling", new Vec3( 0f, 0f, 240f ), new Vec3( 512f, 512f, 256f ), "concrete/ceiling", false );
		AddWall( doc, "brush-wall-x0", "Wall -X", new Vec3( 0f, 0f, 0f ), new Vec3( 16f, 512f, 256f ), "concrete/wall", true );
		AddWall( doc, "brush-wall-x1", "Wall +X", new Vec3( 496f, 0f, 0f ), new Vec3( 512f, 512f, 256f ), "concrete/wall", true );
		AddWall( doc, "brush-wall-y0", "Wall -Y", new Vec3( 0f, 0f, 0f ), new Vec3( 512f, 16f, 256f ), "concrete/wall", true );
		AddWall( doc, "brush-wall-y1", "Wall +Y", new Vec3( 0f, 496f, 0f ), new Vec3( 512f, 512f, 256f ), "concrete/wall", true );
		var spawn = new MapEntityData { Id = "ent-spawn", ClassName = "info_player_start", Origin = new Vec3( 256f, 256f, 32f ), Angles = new Vec3( 0f, 90f, 0f ) };
		spawn.KeyValues["targetname"] = "spawn1";
		doc.AddEntity( spawn );
		var light = new MapEntityData { Id = "ent-light", ClassName = "light", Origin = new Vec3( 256f, 256f, 200f ) };
		light.KeyValues["_light"] = "255 240 200";
		light.KeyValues["targetname"] = "main light";
		doc.AddEntity( light );
		doc.SetCompileSettings( new MapCompileSettings { SkyName = "sky_day01_01", CompileVisibleOnly = false, DefaultLightmapScale = 16f } );
		return doc;
	}

	static void AddWall( MapDocument doc, string id, string name, Vec3 min, Vec3 max, string material, bool onWallsLayer )
	{
		var brush = MapBrushData.Box( id, name, min, max, material );
		if ( onWallsLayer )
		{
			brush.LayerId = "layer-walls";
			brush.VisGroupIds.Add( "vis-all" );
		}
		doc.AddBrush( brush );
	}

	// --- helpers ------------------------------------------------------------------

	static string WorkDir( TestSettings settings, string name )
	{
		var dir = Path.Combine( settings.WorkDirectory, "mapdocument", name );
		Directory.CreateDirectory( dir );
		return dir;
	}

	static string Fixture( TestSettings settings, string name )
	{
		var relative = Path.Combine( "tests", "fixtures", "hammer", "hammer-map-document-model", name );
		if ( File.Exists( relative ) )
			return Path.GetFullPath( relative );
		var dir = new DirectoryInfo( AppDomain.CurrentDomain.BaseDirectory );
		while ( dir is not null )
		{
			var candidate = Path.Combine( dir.FullName, relative );
			if ( File.Exists( candidate ) )
				return candidate;
			dir = dir.Parent;
		}
		throw new Exception( $"Fixture {name} not found under {Path.GetFullPath( relative )} or any parent of {AppDomain.CurrentDomain.BaseDirectory}." );
	}

	static EntityRegistry LoadCoreRegistry( TestSettings settings )
	{
		var relative = Path.Combine( "kernel", "Effigy", "Map", "registry", "core-source1.json" );
		if ( File.Exists( relative ) )
			return EntityRegistry.Load( Path.GetFullPath( relative ) );
		var dir = new DirectoryInfo( AppDomain.CurrentDomain.BaseDirectory );
		while ( dir is not null )
		{
			var candidate = Path.Combine( dir.FullName, relative );
			if ( File.Exists( candidate ) )
				return EntityRegistry.Load( candidate );
			dir = dir.Parent;
		}
		throw new Exception( $"Core registry not found under {Path.GetFullPath( relative )} or any parent of {AppDomain.CurrentDomain.BaseDirectory}." );
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition )
			throw new Exception( message );
	}

	static T Throws<T>( Action body, string mustContain ) where T : Exception
	{
		try
		{
			body();
		}
		catch ( T ex )
		{
			Assert( ex.Message.Contains( mustContain, StringComparison.Ordinal ),
				$"expected '{mustContain}' in '{ex.Message}'" );
			return ex;
		}
		catch ( Exception ex )
		{
			throw new Exception( $"expected {typeof( T ).Name} containing '{mustContain}', got {ex.GetType().Name}: {ex.Message}" );
		}
		throw new Exception( $"expected {typeof( T ).Name} containing '{mustContain}', no exception thrown" );
	}
}
