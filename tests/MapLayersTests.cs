#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

/// <summary>
/// Selection / grouping / layers / visgroups data model (see
/// kernel/Effigy/Map/MapLayers.cs, board
/// hammer-selection-grouping-layers-visgroups-model).
///
/// Fixtures are built synthetically with MapBrushData.Box + MapEntityData:
/// two world boxes, one func_detail brush-entity, one light, one player
/// start, one prop_static, one displacement brush, one nodraw brush and one
/// clip brush — so a missing fixture file can never hide a failure.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// MapLayersTests.Register(registry, settings); line, and the named group
/// runs with --filter maplayers.
/// </summary>
internal static class MapLayersTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "maplayers", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void Throws( Action action )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( "expected an error, got silent success" );
		}

		static MapBrushData Box( string id, string name, Vec3 min, Vec3 max, string material ) =>
			MapBrushData.Box( id, name, min, max, material );

		static MapEntityData Entity( string id, string className, Vec3 origin, params string[] brushIds )
		{
			var entity = new MapEntityData { Id = id, ClassName = className, Origin = origin };
			entity.BrushIds.AddRange( brushIds );
			return entity;
		}

		// World: b1, b2. func_detail: e-detail + b-detail. Light, player
		// start, prop_static. Displacement: b-disp. Nodraw: b-nodraw. Clip: b-clip.
		static MapDocument Doc()
		{
			var doc = MapDocument.Create( "layers-test" );
			doc.AddBrush( Box( "b1", "wall-a", new Vec3( 0, 0, 0 ), new Vec3( 64, 16, 128 ), "brick/brickwall001" ) );
			doc.AddBrush( Box( "b2", "wall-b", new Vec3( 128, 0, 0 ), new Vec3( 192, 16, 128 ), "brick/brickwall001" ) );
			doc.AddBrush( Box( "b-detail", "trim", new Vec3( 0, 64, 0 ), new Vec3( 64, 80, 16 ), "wood/woodfloor001" ) );
			doc.AddBrush( Box( "b-disp", "ground", new Vec3( 0, 0, -16 ), new Vec3( 256, 256, 0 ), "nature/dirt001" ) );
			doc.AddBrush( Box( "b-nodraw", "hidden-face", new Vec3( 0, 128, 0 ), new Vec3( 64, 144, 128 ), "tools/toolsnodraw" ) );
			doc.AddBrush( Box( "b-clip", "clip", new Vec3( 0, 192, 0 ), new Vec3( 64, 208, 128 ), "tools/toolsclip" ) );
			doc.AddEntity( Entity( "e-detail", "func_detail", new Vec3( 0, 0, 0 ), "b-detail" ) );
			doc.AddEntity( Entity( "e-light", "light", new Vec3( 32, 32, 128 ) ) );
			doc.AddEntity( Entity( "e-start", "info_player_start", new Vec3( 32, 32, 64 ) ) );
			var prop = Entity( "e-prop", "prop_static", new Vec3( 200, 200, 0 ) );
			prop.KeyValues["model"] = "models/props/crate.mdl";
			doc.AddEntity( prop );
			doc.AddDisplacement( MapDisplacementData.Flat( "b-disp", 0, 2 ) );
			return doc;
		}

		static void CompileVisibleOnly( MapDocument doc, bool on )
		{
			var settings = doc.CompileSettings.Clone();
			settings.CompileVisibleOnly = on;
			doc.SetCompileSettings( settings );
		}

		void RequireCentroid( MapDocument doc, string brushId, Vec3 expected, string message )
		{
			var brush = doc.FindBrush( brushId ) ?? throw new Exception( $"no brush '{brushId}'" );
			Require( brush.Centroid().AlmostEquals( expected ),
				$"{message}: centroid {brush.Centroid()} vs expected {expected}" );
		}

		Check( "selection add remove toggle clear", () =>
		{
			var layers = new MapLayers( Doc() );
			var b1 = MapObjectRef.Brush( "b1" );
			var b2 = MapObjectRef.Brush( "b2" );
			layers.AddToSelection( b1 );
			layers.AddToSelection( b2 );
			layers.AddToSelection( b1 );
			Require( layers.Selection.Count == 2, "duplicate add grew the selection" );
			Require( layers.IsSelected( b1 ), "b1 not selected" );
			layers.Selection.Toggle( b1 );
			Require( !layers.IsSelected( b1 ) && layers.Selection.Count == 1, "toggle did not remove b1" );
			layers.Selection.Toggle( b1 );
			Require( layers.Selection.Count == 2, "toggle did not re-add b1" );
			Require( layers.RemoveFromSelection( b2 ), "remove b2 returned false" );
			Require( layers.Selection.Count == 1, "remove did not shrink the selection" );
			layers.ClearSelection();
			Require( layers.Selection.IsEmpty, "clear did not empty the selection" );
		} );

		Check( "selection rejects unknown id", () =>
		{
			var layers = new MapLayers( Doc() );
			Throws( () => layers.AddToSelection( MapObjectRef.Brush( "no-such-brush" ) ) );
			Throws( () => layers.AddToSelection( MapObjectRef.Entity( "no-such-entity" ) ) );
			Require( layers.Selection.IsEmpty, "failed selection left a residue" );
		} );

		Check( "group moves as one", () =>
		{
			var doc = Doc();
			var layers = new MapLayers( doc );
			var group = layers.CreateGroup( "walls", new[]
			{
				MapObjectRef.Brush( "b1" ), MapObjectRef.Brush( "b2" ),
			} );
			var moved = layers.MoveGroup( group.Id, new Vec3( 16, 0, 0 ) );
			Require( moved.Count == 2, $"group move moved {moved.Count}, expected 2" );
			RequireCentroid( doc, "b1", new Vec3( 48, 8, 64 ), "b1 did not move with its group" );
			RequireCentroid( doc, "b2", new Vec3( 176, 8, 64 ), "b2 did not move with its group" );
			RequireCentroid( doc, "b-detail", new Vec3( 32, 72, 8 ), "a brush outside the group moved" );
		} );

		Check( "group move carries a member entity and its brushes", () =>
		{
			var doc = Doc();
			var layers = new MapLayers( doc );
			var group = layers.CreateGroup( "detail", new[] { MapObjectRef.Entity( "e-detail" ) } );
			var moved = layers.MoveGroup( group.Id, new Vec3( 0, 32, 0 ) );
			Require( moved.Count == 2, $"entity group move moved {moved.Count}, expected entity + its brush" );
			var entity = doc.FindEntity( "e-detail" ) ?? throw new Exception( "e-detail gone" );
			Require( entity.Origin.AlmostEquals( new Vec3( 0, 32, 0 ) ), $"entity origin {entity.Origin} did not move" );
			RequireCentroid( doc, "b-detail", new Vec3( 32, 104, 8 ), "entity brush did not move with its entity" );
			RequireCentroid( doc, "b1", new Vec3( 32, 8, 64 ), "a brush outside the group moved" );
		} );

		Check( "ignore-groups edits one member", () =>
		{
			var doc = Doc();
			var layers = new MapLayers( doc );
			var group = layers.CreateGroup( "walls", new[]
			{
				MapObjectRef.Brush( "b1" ), MapObjectRef.Brush( "b2" ),
			} );
			var moved = layers.MoveMember( group.Id, MapObjectRef.Brush( "b1" ), new Vec3( 0, 16, 0 ) );
			Require( moved.Count == 1, $"ignore-groups move moved {moved.Count}, expected 1" );
			RequireCentroid( doc, "b1", new Vec3( 32, 24, 64 ), "the edited member did not move" );
			RequireCentroid( doc, "b2", new Vec3( 160, 8, 64 ), "the group-mate moved during an ignore-groups edit" );
			Throws( () => layers.MoveMember( group.Id, MapObjectRef.Brush( "b-detail" ), new Vec3( 1, 0, 0 ) ) );
		} );

		Check( "expand for edit follows ignore-groups", () =>
		{
			var layers = new MapLayers( Doc() );
			var group = layers.CreateGroup( "walls", new[]
			{
				MapObjectRef.Brush( "b1" ), MapObjectRef.Brush( "b2" ),
			} );
			var expanded = layers.ExpandForEdit( new[] { MapObjectRef.Brush( "b1" ) } );
			Require( expanded.Count == 2, $"groups-on expansion gave {expanded.Count}, expected the whole group" );
			layers.IgnoreGroups = true;
			var single = layers.ExpandForEdit( new[] { MapObjectRef.Brush( "b1" ) } );
			Require( single.Count == 1 && single[0].Id == "b1", "ignore-groups expansion did not stay single" );
		} );

		Check( "move selection moves the whole group", () =>
		{
			var doc = Doc();
			var layers = new MapLayers( doc );
			layers.CreateGroup( "walls", new[]
			{
				MapObjectRef.Brush( "b1" ), MapObjectRef.Brush( "b2" ),
			} );
			layers.AddToSelection( MapObjectRef.Brush( "b1" ) );
			layers.MoveSelection( new Vec3( 16, 0, 0 ) );
			RequireCentroid( doc, "b1", new Vec3( 48, 8, 64 ), "selected member did not move" );
			RequireCentroid( doc, "b2", new Vec3( 176, 8, 64 ), "group-mate did not move with the selection" );
			layers.IgnoreGroups = true;
			layers.MoveSelection( new Vec3( 16, 0, 0 ) );
			RequireCentroid( doc, "b1", new Vec3( 64, 8, 64 ), "ignore-groups selection move did not move b1" );
			RequireCentroid( doc, "b2", new Vec3( 176, 8, 64 ), "ignore-groups selection move moved the group-mate" );
		} );

		Check( "hiding a visgroup excludes it from compile-visible export", () =>
		{
			var doc = Doc();
			CompileVisibleOnly( doc, true );
			var hidden = new MapVisGroup { Id = "vg-hide", Name = "Hidden", Colour = "#FF0000", Visible = true, CompileVisible = false };
			doc.AddVisGroup( hidden );
			var keep = doc.FindBrush( "b1" ) ?? throw new Exception( "b1 gone" );
			var hide = doc.FindBrush( "b2" ) ?? throw new Exception( "b2 gone" );
			hide.VisGroupIds.Add( "vg-hide" );
			var layers = new MapLayers( doc );
			Require( !layers.IsCompileVisible( hide ), "compile-hidden brush reports compile-visible" );
			Require( layers.IsCompileVisible( keep ), "ordinary brush reports not compile-visible" );
			var input = layers.ToCompileInput();
			Require( input.Brushes.Any( b => b.Name == "wall-a" ), "compile input lost the kept brush" );
			Require( !input.Brushes.Any( b => b.Name == "wall-b" ), "compile input kept the hidden-visgroup brush" );
		} );

		Check( "eye-hide hides from editor but not from compile", () =>
		{
			var doc = Doc();
			CompileVisibleOnly( doc, true );
			doc.AddVisGroup( new MapVisGroup { Id = "vg-eye", Name = "EyeOff", Colour = "#00FF00", Visible = false, CompileVisible = true } );
			var brush = doc.FindBrush( "b1" ) ?? throw new Exception( "b1 gone" );
			brush.VisGroupIds.Add( "vg-eye" );
			var layers = new MapLayers( doc );
			Require( !layers.IsEditorVisible( brush ), "eye-hidden brush reports editor-visible" );
			Require( !layers.VisibleBrushes().Any( b => b.Id == "b1" ), "eye-hidden brush is in VisibleBrushes" );
			Require( layers.IsCompileVisible( brush ), "eye-hide alone removed the brush from compile (compile flags govern compile)" );
			Require( layers.ToCompileInput().Brushes.Any( b => b.Name == "wall-a" ), "eye-hide alone removed the brush from the compile input" );
		} );

		Check( "quick-hide hides from editor and compile", () =>
		{
			var doc = Doc();
			CompileVisibleOnly( doc, true );
			var layers = new MapLayers( doc );
			layers.AddToSelection( MapObjectRef.Brush( "b1" ) );
			layers.HideSelected();
			Require( layers.IsHidden( MapObjectRef.Brush( "b1" ) ), "HideSelected did not hide b1" );
			Require( !layers.VisibleBrushes().Any( b => b.Id == "b1" ), "quick-hidden brush is in VisibleBrushes" );
			Require( !layers.ToCompileInput().Brushes.Any( b => b.Name == "wall-a" ), "quick-hidden brush is in the compile input" );
			layers.UnhideAll();
			Require( layers.VisibleBrushes().Any( b => b.Id == "b1" ), "UnhideAll did not restore b1" );
			Require( layers.ToCompileInput().Brushes.Any( b => b.Name == "wall-a" ), "UnhideAll did not restore b1 to compile" );
		} );

		Check( "hide unselected hides the rest", () =>
		{
			var doc = Doc();
			var layers = new MapLayers( doc );
			layers.AddToSelection( MapObjectRef.Brush( "b1" ) );
			layers.HideUnselected();
			Require( !layers.IsHidden( MapObjectRef.Brush( "b1" ) ), "HideUnselected hid the selected brush" );
			Require( layers.IsHidden( MapObjectRef.Brush( "b2" ) ), "HideUnselected did not hide b2" );
			Require( layers.IsHidden( MapObjectRef.Entity( "e-light" ) ), "HideUnselected did not hide the light entity" );
			Require( layers.VisibleBrushes().Count == 1, $"HideUnselected left {layers.VisibleBrushes().Count} visible brushes, expected 1" );
		} );

		Check( "filter categories classify and hide", () =>
		{
			var doc = Doc();
			var layers = new MapLayers( doc );
			var b1 = doc.FindBrush( "b1" ) ?? throw new Exception( "b1 gone" );
			var bDetail = doc.FindBrush( "b-detail" ) ?? throw new Exception( "b-detail gone" );
			var bDisp = doc.FindBrush( "b-disp" ) ?? throw new Exception( "b-disp gone" );
			var bNodraw = doc.FindBrush( "b-nodraw" ) ?? throw new Exception( "b-nodraw gone" );
			var bClip = doc.FindBrush( "b-clip" ) ?? throw new Exception( "b-clip gone" );
			var prop = doc.FindEntity( "e-prop" ) ?? throw new Exception( "e-prop gone" );
			var light = doc.FindEntity( "e-light" ) ?? throw new Exception( "e-light gone" );
			Require( layers.ClassifyBrush( b1 ) == MapFilterCategory.World, "world brush misclassified" );
			Require( layers.ClassifyBrush( bDetail ) == MapFilterCategory.Entities, "entity brush misclassified" );
			Require( layers.ClassifyBrush( bDisp ) == MapFilterCategory.Displacements, "displacement brush misclassified" );
			Require( layers.ClassifyBrush( bNodraw ) == MapFilterCategory.NoDraw, "nodraw brush misclassified" );
			Require( layers.ClassifyBrush( bClip ) == MapFilterCategory.ToolTextures, "clip brush misclassified" );
			Require( layers.ClassifyEntity( prop ) == MapFilterCategory.Models, "prop_static misclassified" );
			Require( layers.ClassifyEntity( light ) == MapFilterCategory.Entities, "light misclassified" );
			layers.SetCategoryEnabled( MapFilterCategory.World, false );
			Require( !layers.FilteredBrushes().Any( b => b.Id == "b1" ), "disabled World category still shows a world brush" );
			Require( layers.FilteredBrushes().Any( b => b.Id == "b-detail" ), "disabling World hid an entity brush" );
			layers.SetCategoryEnabled( MapFilterCategory.Entities, false );
			Require( !layers.FilteredBrushes().Any( b => b.Id == "b-detail" ), "disabled Entities category still shows an entity brush" );
		} );

		Check( "auto-visgroups are created once and assigned", () =>
		{
			var doc = Doc();
			var first = MapLayers.EnsureAutoVisGroups( doc );
			var second = MapLayers.EnsureAutoVisGroups( doc );
			Require( first.Count == 4 && second.Count == 4, "auto-visgroups did not return all four groups" );
			Require( doc.VisGroups.Count == 4, $"second auto-visgroup pass created duplicates ({doc.VisGroups.Count} visgroups)" );
			var detail = doc.FindBrush( "b-detail" ) ?? throw new Exception( "b-detail gone" );
			Require( detail.VisGroupIds.Contains( MapLayers.AutoFuncDetailId ), "func_detail brush is not in Auto: func_detail" );
			var disp = doc.FindBrush( "b-disp" ) ?? throw new Exception( "b-disp gone" );
			Require( disp.VisGroupIds.Contains( MapLayers.AutoDisplacementsId ), "displacement brush is not in Auto: Displacements" );
			var clip = doc.FindBrush( "b-clip" ) ?? throw new Exception( "b-clip gone" );
			Require( clip.VisGroupIds.Contains( MapLayers.AutoToolTexturesId ), "clip brush is not in Auto: Tool Textures" );
			var light = doc.FindEntity( "e-light" ) ?? throw new Exception( "e-light gone" );
			Require( light.VisGroupIds.Contains( MapLayers.AutoEntitiesId ), "light entity is not in Auto: Entities" );
			Require( detail.VisGroupIds.Count == 1, "auto-visgroup pass duplicated a membership" );
		} );

		Check( "compile input carries detail flag lights and player start", () =>
		{
			var doc = Doc();
			CompileVisibleOnly( doc, true );
			var layers = new MapLayers( doc );
			var input = layers.ToCompileInput();
			Require( input.Name == "layers-test", $"compile input name '{input.Name}' is not the document name" );
			Require( input.PlayerStart is not null && input.PlayerStart.Value.AlmostEquals( new Vec3( 32, 32, 64 ) ),
				"compile input player start is not the info_player_start origin" );
			Require( input.Lights.Count == 1 && input.Lights[0].Position.AlmostEquals( new Vec3( 32, 32, 128 ) ),
				$"compile input has {input.Lights.Count} lights, expected the one light entity" );
			var detail = input.Brushes.FirstOrDefault( b => b.Name == "trim" ) ?? throw new Exception( "detail brush missing from compile input" );
			Require( detail.IsDetail, "func_detail brush is not flagged detail in the compile input" );
			var world = input.Brushes.FirstOrDefault( b => b.Name == "wall-a" ) ?? throw new Exception( "world brush missing from compile input" );
			Require( !world.IsDetail, "world brush is flagged detail in the compile input" );
			Require( world.Faces.Count == 6, $"world brush has {world.Faces.Count} compile faces, expected 6" );
			Require( world.Faces[0].Material == "brick/brickwall001", $"compile face material '{world.Faces[0].Material}' lost the brush material" );
		} );

		Check( "compile-visible-only off includes everything", () =>
		{
			var doc = Doc();
			CompileVisibleOnly( doc, false );
			doc.AddVisGroup( new MapVisGroup { Id = "vg-off", Name = "Off", Colour = "#0000FF", Visible = true, CompileVisible = false } );
			var brush = doc.FindBrush( "b2" ) ?? throw new Exception( "b2 gone" );
			brush.VisGroupIds.Add( "vg-off" );
			var layers = new MapLayers( doc );
			Require( layers.ToCompileInput().Brushes.Any( b => b.Name == "wall-b" ),
				"compile-visible-only off still excluded the compile-hidden brush" );
		} );

		Check( "group rejects unknown member", () =>
		{
			var layers = new MapLayers( Doc() );
			Throws( () => layers.CreateGroup( "bad", new[] { MapObjectRef.Brush( "no-such-brush" ) } ) );
			Require( layers.Groups.Count == 0, "failed group creation left a group behind" );
		} );

		Check( "validate reports dangling selection", () =>
		{
			var doc = Doc();
			var layers = new MapLayers( doc );
			layers.AddToSelection( MapObjectRef.Brush( "b1" ) );
			doc.RemoveBrush( "b1" );
			var defects = layers.Validate();
			Require( defects.Count == 1 && defects[0].Contains( "b1" ),
				$"Validate did not report the dangling selection (got [{string.Join( "; ", defects )}])" );
		} );
	}
}
