// MapLayerHierarchyTests — Hammer Desktop P2: hierarchical layers +
// auto-visgroups + Mark/select-all-in-group
// (board hammer-visgroups-hierarchical-layers).
//
// The acceptance numbers live in the fixture
// tests/fixtures/hammer/hammer-visgroups-hierarchical-layers/visgroups-hierarchy.json
// (world/detail/trim tree, one func_detail brush, one all-tool brush, one
// light, one func_instance); the rest build the same shapes synthetically so
// a missing fixture can never hide a classification failure.
//
// NOT yet registered in tests/TestRegistry.cs — that file is
// coordinator-owned; the final integrator adds the one
// MapLayerHierarchyTests.Register(registry, settings); line
// (same precedent as LinkedGroupsTests).
//
// No UI. No full-project build per the PO workflow override; this file is
// exercised by the named test group only, at the integration gate.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

internal static class MapLayerHierarchyTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "visgroups", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void RequireSame<T>( IReadOnlyList<T> actual, IReadOnlyList<T> expected, string message )
		{
			Require( actual.Count == expected.Count,
				$"{message}: got {actual.Count} items [{string.Join( ", ", actual )}], expected {expected.Count} [{string.Join( ", ", expected )}]" );
			for ( var i = 0; i < expected.Count; i++ )
				Require( Equals( actual[i], expected[i] ),
					$"{message}: item {i} is '{actual[i]}', expected '{expected[i]}'" );
		}

		static MapDocument DocWithLayers( params (string Id, string Name, bool Visible, bool CompileVisible)[] layers )
		{
			var doc = MapDocument.Create( "visgroups-test" );
			foreach ( var (id, name, visible, compileVisible) in layers )
				doc.AddLayer( new MapLayer { Id = id, Name = name, Visible = visible, CompileVisible = compileVisible } );
			return doc;
		}

		static MapBrushData Box( string id, string material, string layerId = "" )
		{
			var brush = MapBrushData.Box( id, id, new Vec3( 0, 0, 0 ), new Vec3( 16, 16, 16 ), material );
			brush.LayerId = layerId;
			return brush;
		}

		static MapBrushData BoxFaces( string id, string[] materials, string layerId = "" )
		{
			var brush = MapBrushData.Box( id, id, new Vec3( 0, 0, 0 ), new Vec3( 16, 16, 16 ), materials );
			brush.LayerId = layerId;
			return brush;
		}

		static MapEntityData Entity( string id, string className, params string[] brushIds ) => new()
		{
			Id = id,
			ClassName = className,
			BrushIds = brushIds.ToList(),
		};

		Check( "parent-hidden-hides-children", () =>
		{
			var doc = DocWithLayers( ("world", "World", true, true), ("detail", "Detail", true, true), ("trim", "Trim", true, true) );
			var tree = new MapLayerHierarchy( doc );
			tree.SetParent( "detail", "world" );
			tree.SetParent( "trim", "detail" );
			Require( tree.EffectiveVisible( "trim" ), "trim starts visible" );
			RequireSame( tree.Descendants( "world" ), new[] { "detail", "trim" }, "descendants of world" );
			RequireSame( tree.ChildrenOf( "world" ), new[] { "detail" }, "children of world" );
			RequireSame( tree.RootLayers(), new[] { "world" }, "root layers" );

			doc.SetLayerVisible( "world", false );
			Require( !tree.EffectiveVisible( "world" ), "world hidden" );
			Require( !tree.EffectiveVisible( "detail" ), "hiding a parent hides the child" );
			Require( !tree.EffectiveVisible( "trim" ), "hiding a parent hides the grandchild" );
			doc.SetLayerVisible( "world", true );
			Require( tree.EffectiveVisible( "trim" ), "re-showing the parent re-shows the grandchild" );
		} );

		Check( "child-override-cannot-reshow-hidden-parent", () =>
		{
			var doc = DocWithLayers( ("world", "World", true, true), ("detail", "Detail", true, true) );
			var tree = new MapLayerHierarchy( doc );
			tree.SetParent( "detail", "world" );
			tree.SetVisibleOverride( "detail", true );
			doc.SetLayerVisible( "world", false );
			Require( !tree.EffectiveVisible( "detail" ),
				"a child override of visible cannot re-show a child of a hidden parent" );
			doc.SetLayerVisible( "world", true );
			tree.SetVisibleOverride( "detail", false );
			Require( !tree.EffectiveVisible( "detail" ), "a child override of hidden hides the child under a visible parent" );
			tree.SetVisibleOverride( "detail", null );
			Require( tree.EffectiveVisible( "detail" ), "clearing the override restores the layer flag" );
		} );

		Check( "compile-visible-inherits-down", () =>
		{
			var doc = DocWithLayers( ("world", "World", true, false), ("detail", "Detail", true, true) );
			doc.AddBrush( Box( "b1", "concrete/concretefloor001", "detail" ) );
			var tree = new MapLayerHierarchy( doc );
			tree.SetParent( "detail", "world" );
			Require( !tree.EffectiveCompileVisible( "detail" ), "compile-hidden parent hides the child at compile time" );
			Require( !tree.IsCompileVisible( doc.FindBrush( "b1" ) ), "brush in a compile-hidden subtree is not compile-visible" );
			tree.SetCompileVisibleOverride( "detail", true );
			Require( !tree.EffectiveCompileVisible( "detail" ),
				"a child compile override cannot re-show a child of a compile-hidden parent" );
			Require( tree.IsVisible( doc.FindBrush( "b1" ) ), "compile hiding does not hide the brush in the editor" );
		} );

		Check( "set-parent-rejects-cycle-self-and-unknown", () =>
		{
			var doc = DocWithLayers( ("a", "A", true, true), ("b", "B", true, true), ("c", "C", true, true) );
			var tree = new MapLayerHierarchy( doc );
			tree.SetParent( "b", "a" );
			tree.SetParent( "c", "b" );
			RequireThrows<InvalidOperationException>( () => tree.SetParent( "a", "c" ), "a under c would cycle" );
			RequireThrows<InvalidOperationException>( () => tree.SetParent( "a", "a" ), "self-parent" );
			RequireThrows<InvalidOperationException>( () => tree.SetParent( "a", "nope" ), "unknown parent" );
			RequireThrows<InvalidOperationException>( () => tree.SetParent( "nope", "a" ), "unknown layer" );
			Require( tree.ParentOf( "a" ) == "", "rejected parents left the tree unchanged" );
		} );

		Check( "func-detail-brush-lands-in-world-detail", () =>
		{
			var doc = MapDocument.Create( "visgroups-test" );
			doc.AddBrush( Box( "b-detail", "concrete/concretefloor001" ) );
			doc.AddBrush( Box( "b-wall", "concrete/concretefloor001" ) );
			doc.AddEntity( Entity( "e-detail", "func_detail", "b-detail" ) );
			var tree = new MapLayerHierarchy( doc );
			Require( tree.ClassifyBrush( doc.FindBrush( "b-detail" ) ) == AutoVisGroupKind.WorldDetail,
				"a func_detail brush lands in the World Detail auto-visgroup" );
			Require( tree.ClassifyBrush( doc.FindBrush( "b-wall" ) ) == AutoVisGroupKind.WorldGeometry,
				"an unbound brush stays World Geometry" );
			Require( tree.AssignAutoVisGroup( doc.FindBrush( "b-detail" ) ) == AutoVisGroupKind.WorldDetail,
				"assign returns the kind" );
			Require( doc.FindBrush( "b-detail" ).VisGroupIds.Contains( MapLayerHierarchy.AutoWorldDetailId ),
				"assign records the auto-visgroup on the brush" );
			Require( tree.ClassifyEntity( doc.FindEntity( "e-detail" ) ) == AutoVisGroupKind.WorldDetail,
				"the func_detail entity travels with its brushes" );
		} );

		Check( "tool-brush-and-entity-classification", () =>
		{
			var doc = MapDocument.Create( "visgroups-test" );
			var tools = Enumerable.Repeat( "tools/toolsclip", 6 ).ToArray();
			doc.AddBrush( BoxFaces( "b-clip", tools ) );
			doc.AddBrush( BoxFaces( "b-mixed", new[] { "tools/toolsclip", "concrete/concretefloor001", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip" } ) );
			doc.AddBrush( Box( "b-wall", "concrete/concretefloor001" ) );
			var faceless = new MapBrushData { Id = "b-empty", Name = "b-empty" };
			doc.AddBrush( faceless );
			doc.AddEntity( Entity( "e-light", "light" ) );
			doc.AddEntity( Entity( "e-inst", "func_instance" ) );
			doc.AddEntity( Entity( "e-blocker", "func_detail_blocker", "b-wall" ) );
			var tree = new MapLayerHierarchy( doc );
			Require( tree.ClassifyBrush( doc.FindBrush( "b-clip" ) ) == AutoVisGroupKind.ToolBrushes,
				"an all-tool-texture brush lands in Tool Brushes" );
			Require( tree.ClassifyBrush( doc.FindBrush( "b-mixed" ) ) == AutoVisGroupKind.WorldGeometry,
				"one non-tool face keeps a brush World Geometry" );
			Require( tree.ClassifyBrush( doc.FindBrush( "b-empty" ) ) == AutoVisGroupKind.WorldGeometry,
				"a faceless brush is World Geometry" );
			Require( tree.ClassifyEntity( doc.FindEntity( "e-light" ) ) == AutoVisGroupKind.Entities,
				"a point entity lands in Entities" );
			Require( tree.ClassifyEntity( doc.FindEntity( "e-inst" ) ) == AutoVisGroupKind.Instances,
				"a func_instance lands in Instance" );
			Require( tree.ClassifyEntity( doc.FindEntity( "e-blocker" ) ) == AutoVisGroupKind.WorldDetail,
				"func_detail_blocker is a func_detail class too" );
			Require( tree.ClassifyBrush( doc.FindBrush( "b-wall" ) ) == AutoVisGroupKind.WorldDetail,
				"a brush bound to a func_detail_blocker is World Detail, not World Geometry" );
		} );

		Check( "assign-all-is-idempotent", () =>
		{
			var doc = MapDocument.Create( "visgroups-test" );
			doc.AddBrush( Box( "b1", "concrete/concretefloor001" ) );
			doc.AddEntity( Entity( "e1", "light" ) );
			var tree = new MapLayerHierarchy( doc );
			var counts = tree.AssignAll();
			Require( counts[AutoVisGroupKind.WorldGeometry] == 1 && counts[AutoVisGroupKind.Entities] == 1,
				"assign-all counts one world brush and one entity" );
			tree.AssignAll();
			Require( doc.FindBrush( "b1" ).VisGroupIds.Count == 1, "a second assign-all does not duplicate the membership" );
			Require( doc.VisGroups.Count == 5, "assign-all created exactly the five auto-visgroups" );
		} );

		Check( "ensure-auto-visgroups-is-idempotent", () =>
		{
			var doc = MapDocument.Create( "visgroups-test" );
			var tree = new MapLayerHierarchy( doc );
			var created = tree.EnsureAutoVisGroups();
			Require( created.Count == 5, "first ensure creates five" );
			Require( tree.EnsureAutoVisGroups().Count == 0, "second ensure creates none" );
			Require( doc.FindVisGroup( MapLayerHierarchy.AutoWorldDetailId )?.Name == "World Detail",
				"the World Detail group is named" );
		} );

		Check( "select-all-in-group-returns-exactly-members", () =>
		{
			var doc = MapDocument.Create( "visgroups-test" );
			doc.AddBrush( Box( "b-detail", "concrete/concretefloor001" ) );
			doc.AddBrush( Box( "b-wall", "concrete/concretefloor001" ) );
			doc.AddEntity( Entity( "e-detail", "func_detail", "b-detail" ) );
			doc.AddEntity( Entity( "e-light", "light" ) );
			var tree = new MapLayerHierarchy( doc );
			tree.AssignAll();
			RequireSame( tree.SelectAllInGroup( MapLayerHierarchy.AutoWorldDetailId ),
				new[]
				{
					new MapGroupMember( MapGroupMemberKind.Brush, "b-detail" ),
					new MapGroupMember( MapGroupMemberKind.Entity, "e-detail" ),
				},
				"SelectAllInGroup returns exactly the group's members" );
			RequireSame( tree.SelectAllInGroup( MapLayerHierarchy.AutoEntitiesId ),
				new[] { new MapGroupMember( MapGroupMemberKind.Entity, "e-light" ) },
				"the Entities group holds only the light" );
			RequireThrows<InvalidOperationException>( () => tree.SelectAllInGroup( "nope" ), "unknown group" );
		} );

		Check( "mark-group-and-unmark", () =>
		{
			var doc = MapDocument.Create( "visgroups-test" );
			doc.AddBrush( Box( "b-detail", "concrete/concretefloor001" ) );
			doc.AddEntity( Entity( "e-detail", "func_detail", "b-detail" ) );
			var tree = new MapLayerHierarchy( doc );
			tree.AssignAll();
			Require( tree.MarkedMembers().Count == 0, "nothing marked at first" );
			tree.MarkGroup( MapLayerHierarchy.AutoWorldDetailId );
			Require( tree.MarkedVisGroupId == MapLayerHierarchy.AutoWorldDetailId, "mark records the group" );
			RequireSame( tree.MarkedMembers(), tree.SelectAllInGroup( MapLayerHierarchy.AutoWorldDetailId ),
				"marked members are the marked group's members" );
			RequireThrows<InvalidOperationException>( () => tree.MarkGroup( "nope" ), "marking an unknown group" );
			tree.Unmark();
			Require( tree.MarkedVisGroupId == "" && tree.MarkedMembers().Count == 0, "unmark clears the mark" );
		} );

		Check( "visgroup-eye-and-compile-flags-hide-members", () =>
		{
			var doc = MapDocument.Create( "visgroups-test" );
			doc.AddBrush( Box( "b1", "concrete/concretefloor001" ) );
			var tree = new MapLayerHierarchy( doc );
			tree.AssignAll();
			var brush = doc.FindBrush( "b1" );
			Require( tree.IsVisible( brush ) && tree.IsCompileVisible( brush ), "member starts visible" );
			doc.SetVisGroupVisible( MapLayerHierarchy.AutoWorldGeometryId, false );
			Require( !tree.IsVisible( brush ), "hiding the visgroup hides its member in the editor" );
			Require( tree.IsCompileVisible( brush ), "the eye toggle alone does not hide it at compile time" );
			doc.SetVisGroupVisible( MapLayerHierarchy.AutoWorldGeometryId, true );
			doc.FindVisGroup( MapLayerHierarchy.AutoWorldGeometryId ).CompileVisible = false;
			Require( tree.IsVisible( brush ), "the compile flag alone does not hide it in the editor" );
			Require( !tree.IsCompileVisible( brush ), "a compile-hidden visgroup excludes its member from compile" );
		} );

		Check( "validate-reports-dangling-references", () =>
		{
			var doc = DocWithLayers( ("world", "World", true, true), ("detail", "Detail", true, true) );
			doc.AddBrush( Box( "b1", "concrete/concretefloor001", "world" ) );
			var tree = new MapLayerHierarchy( doc );
			tree.SetParent( "detail", "world" );
			Require( tree.Validate().Count == 0, "clean tree validates clean" );
			doc.RemoveLayer( "world" );
			var defects = tree.Validate();
			Require( defects.Any( d => d.Contains( "detail" ) && d.Contains( "world" ) ),
				"removing a parent leaves a dangling-parent defect, not a silent clear" );
			Require( defects.Any( d => d.Contains( "b1" ) && d.Contains( "world" ) ),
				"the brush still pointing at the removed layer is a defect" );
		} );

		Check( "fixture-visgroups-hierarchy", () =>
		{
			var path = Fixture( settings, "visgroups-hierarchy.json" );
			using var json = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = json.RootElement;
			var doc = MapDocument.Create( "visgroups-fixture" );
			foreach ( var layer in root.GetProperty( "layers" ).EnumerateArray() )
				doc.AddLayer( new MapLayer
				{
					Id = layer.GetProperty( "id" ).GetString()!,
					Name = layer.GetProperty( "name" ).GetString()!,
				} );
			var tree = new MapLayerHierarchy( doc );
			foreach ( var layer in root.GetProperty( "layers" ).EnumerateArray() )
				if ( layer.TryGetProperty( "parent", out var parent ) )
					tree.SetParent( layer.GetProperty( "id" ).GetString()!, parent.GetString()! );
			var i = 0;
			foreach ( var brush in root.GetProperty( "brushes" ).EnumerateArray() )
			{
				var materials = brush.GetProperty( "materials" ).EnumerateArray()
					.Select( m => m.GetString()! ).ToArray();
				// One material in the fixture means all six faces; six means one per face.
				if ( materials.Length == 1 )
					materials = Enumerable.Repeat( materials[0], 6 ).ToArray();
				if ( materials.Length != 6 )
					throw new Exception( $"Fixture brush '{brush.GetProperty( "id" ).GetString()}' needs 1 or 6 materials, got {materials.Length}." );
				var b = MapBrushData.Box(
					brush.GetProperty( "id" ).GetString()!,
					brush.GetProperty( "id" ).GetString()!,
					new Vec3( i * 32, 0, 0 ), new Vec3( i * 32 + 16, 16, 16 ), materials );
				b.LayerId = brush.GetProperty( "layer" ).GetString()!;
				doc.AddBrush( b );
				i++;
			}
			foreach ( var entity in root.GetProperty( "entities" ).EnumerateArray() )
				doc.AddEntity( Entity(
					entity.GetProperty( "id" ).GetString()!,
					entity.GetProperty( "classname" ).GetString()!,
					entity.GetProperty( "brushes" ).EnumerateArray().Select( b => b.GetString()! ).ToArray() ) );
			tree.AssignAll();
			var expect = root.GetProperty( "expect" );
			foreach ( var (objectId, groupId) in expect.GetProperty( "autoVisGroups" ).EnumerateObject()
				.Select( p => (p.Name, p.Value.GetString()! ) ) )
			{
				var brush = doc.FindBrush( objectId );
				if ( brush is not null )
					Require( brush.VisGroupIds.Contains( groupId ),
						$"fixture: brush '{objectId}' should be in '{groupId}'" );
				else
					Require( doc.FindEntity( objectId )!.VisGroupIds.Contains( groupId ),
						$"fixture: entity '{objectId}' should be in '{groupId}'" );
			}
			foreach ( var group in expect.GetProperty( "selectAllInGroup" ).EnumerateObject() )
			{
				var expected = group.Value.EnumerateArray().Select( m =>
				{
					var parts = m.GetString()!.Split( ':' );
					return new MapGroupMember( parts[0] == "brush" ? MapGroupMemberKind.Brush : MapGroupMemberKind.Entity, parts[1] );
				} ).ToList();
				RequireSame( tree.SelectAllInGroup( group.Name ), expected, $"fixture: SelectAllInGroup({group.Name})" );
			}
			RequireSame( tree.ChildrenOf( "world" ), new[] { "detail" }, "fixture: children of world" );
			RequireSame( tree.Descendants( "world" ), new[] { "detail", "trim" }, "fixture: descendants of world" );
			doc.SetLayerVisible( "world", false );
			Require( !tree.EffectiveVisible( "trim" ), "fixture: hiding world hides trim through detail" );
		} );

		static string Fixture( TestSettings settings, string name )
		{
			var relative = Path.Combine( "tests", "fixtures", "hammer", "hammer-visgroups-hierarchical-layers", name );
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

		static void RequireThrows<T>( Action action, string message ) where T : Exception
		{
			try
			{
				action();
			}
			catch ( T )
			{
				return;
			}
			catch ( Exception ex )
			{
				throw new Exception( $"{message}: threw {ex.GetType().Name} instead of {typeof( T ).Name}" );
			}
			throw new Exception( $"{message}: expected {typeof( T ).Name}, nothing was thrown" );
		}
	}
}
