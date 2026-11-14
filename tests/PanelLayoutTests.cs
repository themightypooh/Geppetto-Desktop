using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

/// <summary>
/// Docked panel layout data model (hammer-panels-docked-layout-model, group "panellayout").
///
/// Data model only — no UI. Fixtures live in
/// tests/fixtures/hammer/hammer-panels-docked-layout-model/.
/// </summary>
internal static class PanelLayoutTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "panellayout", name, test );
		void CheckMissing( string name, Action test, Func<string> missing ) => suite.Add( "panellayout", name, test, missing );
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
		void Approx( double actual, double expected, string what )
		{
			Require( Math.Abs( actual - expected ) <= 1e-9, $"{what}: got {actual}, expected {expected}" );
		}

		static string? FindFixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-panels-docked-layout-model" );
					if ( File.Exists( Path.Combine( candidate, "layout-roundtrip.json" ) ) )
						return candidate;
				}
			}
			return null;
		}

		static string TempDir()
		{
			var dir = Path.Combine( Path.GetTempPath(), "panellayout-tests-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( dir );
			return dir;
		}

		void SameLayout( LayoutDocument got, LayoutDocument want, string what )
		{
			Require( got.SchemaVersion == want.SchemaVersion, $"{what}: schemaVersion {got.SchemaVersion} != {want.SchemaVersion}" );
			Require( got.MapId == want.MapId, $"{what}: MapId '{got.MapId}' != '{want.MapId}'" );
			Require( got.WorkspaceId == want.WorkspaceId, $"{what}: WorkspaceId '{got.WorkspaceId}' != '{want.WorkspaceId}'" );
			Require( got.ActivePanelId == want.ActivePanelId, $"{what}: ActivePanelId '{got.ActivePanelId}' != '{want.ActivePanelId}'" );
			Require( got.Panels.Count == want.Panels.Count, $"{what}: panel count {got.Panels.Count} != {want.Panels.Count}" );
			foreach ( var wantPanel in want.Panels )
			{
				var gotPanel = got.GetPanel( wantPanel.PanelId );
				Require( gotPanel.Zone == wantPanel.Zone, $"{what}: panel '{wantPanel.PanelId}' zone {gotPanel.Zone} != {wantPanel.Zone}" );
				Require( Math.Abs( gotPanel.Size - wantPanel.Size ) <= 1e-9, $"{what}: panel '{wantPanel.PanelId}' size {gotPanel.Size} != {wantPanel.Size}" );
				Require( gotPanel.Visible == wantPanel.Visible, $"{what}: panel '{wantPanel.PanelId}' visible {gotPanel.Visible} != {wantPanel.Visible}" );
				Require( gotPanel.Order == wantPanel.Order, $"{what}: panel '{wantPanel.PanelId}' order {gotPanel.Order} != {wantPanel.Order}" );
				Require( Math.Abs( gotPanel.FloatX - wantPanel.FloatX ) <= 1e-9, $"{what}: panel '{wantPanel.PanelId}' floatX {gotPanel.FloatX} != {wantPanel.FloatX}" );
				Require( Math.Abs( gotPanel.FloatY - wantPanel.FloatY ) <= 1e-9, $"{what}: panel '{wantPanel.PanelId}' floatY {gotPanel.FloatY} != {wantPanel.FloatY}" );
				Require( Math.Abs( gotPanel.FloatWidth - wantPanel.FloatWidth ) <= 1e-9, $"{what}: panel '{wantPanel.PanelId}' floatWidth {gotPanel.FloatWidth} != {wantPanel.FloatWidth}" );
				Require( Math.Abs( gotPanel.FloatHeight - wantPanel.FloatHeight ) <= 1e-9, $"{what}: panel '{wantPanel.PanelId}' floatHeight {gotPanel.FloatHeight} != {wantPanel.FloatHeight}" );
			}
		}

		Check( "roundtrip-preserves-positions", () =>
		{
			var dir = TempDir();
			try
			{
				var layout = LayoutDocument.CreateDefault( "test-room", "default" );
				layout.MovePanel( "textures", DockZone.Bottom, 2 );
				layout.ResizePanel( "properties", 320 );
				layout.SetFloatingRect( "face-edit", 120, 90, 480, 360 );
				layout.SetVisible( "build", false );
				layout.ActivePanelId = "properties";
				var path = Path.Combine( dir, "layout.json" );
				layout.Save( path );
				var reloaded = LayoutDocument.Load( path );
				SameLayout( reloaded, layout, "roundtrip" );
				Require( reloaded.Validate( PanelRegistry.Default() ).Count == 0, "roundtripped layout validates clean: " + string.Join( "; ", reloaded.Validate( PanelRegistry.Default() ) ) );
			}
			finally { Directory.Delete( dir, true ); }
		} );

		CheckMissing( "roundtrip-from-fixture", () =>
		{
			var layout = LayoutDocument.Load( Path.Combine( FindFixtureDir(), "layout-roundtrip.json" ) );
			Require( layout.SchemaVersion == 1, $"fixture schemaVersion {layout.SchemaVersion} != 1" );
			Require( layout.MapId == "test-room", $"fixture MapId '{layout.MapId}' != 'test-room'" );
			Require( layout.Panels.Count == 8, $"fixture panel count {layout.Panels.Count} != 8" );
			Require( layout.GetPanel( "entities" ).Zone == DockZone.Left, "entities must dock Left" );
			Approx( layout.GetPanel( "entities" ).Size, 280, "entities size" );
			Require( layout.GetPanel( "properties" ).Zone == DockZone.Right, "properties must dock Right" );
			Approx( layout.GetPanel( "properties" ).Size, 320, "properties size" );
			Require( layout.GetPanel( "console" ).Zone == DockZone.Bottom, "console must dock Bottom" );
			Require( layout.GetPanel( "tool-options" ).Zone == DockZone.Top, "tool-options must dock Top" );
			var faceEdit = layout.GetPanel( "face-edit" );
			Require( faceEdit.Zone == DockZone.FloatingDocked, "face-edit must be FloatingDocked" );
			Approx( faceEdit.FloatX, 120, "face-edit floatX" );
			Approx( faceEdit.FloatY, 90, "face-edit floatY" );
			Approx( faceEdit.FloatWidth, 480, "face-edit floatWidth" );
			Approx( faceEdit.FloatHeight, 360, "face-edit floatHeight" );
			// The hidden Build panel keeps its dock position in the fixture.
			var build = layout.GetPanel( "build" );
			Require( !build.Visible, "build must be hidden in the fixture" );
			Require( build.Zone == DockZone.Bottom, "hidden build must keep Bottom zone" );
			Approx( build.Size, 200, "hidden build size" );
			Require( build.Order == 1, "hidden build order must stay 1" );
			Require( layout.Validate( PanelRegistry.Default() ).Count == 0, "fixture layout validates clean: " + string.Join( "; ", layout.Validate( PanelRegistry.Default() ) ) );
			// And it round-trips through a save/load without losing the hidden panel.
			var dir = TempDir();
			try
			{
				var path = Path.Combine( dir, "out.json" );
				layout.Save( path );
				SameLayout( LayoutDocument.Load( path ), layout, "fixture roundtrip" );
			}
			finally { Directory.Delete( dir, true ); }
		}, () => FindFixtureDir() is null ? "fixture layout-roundtrip.json not found under tests/fixtures/hammer/hammer-panels-docked-layout-model" : null );

		Check( "hide-keeps-dock-position", () =>
		{
			var layout = LayoutDocument.CreateDefault( "test-room", "default" );
			layout.SetVisible( "textures", false );
			var textures = layout.GetPanel( "textures" );
			Require( !textures.Visible, "textures must be hidden" );
			Require( textures.Zone == DockZone.Right, "hiding must keep Right zone" );
			Approx( textures.Size, 280, "hiding must keep size" );
			Require( textures.Order == 1, "hiding must keep order" );
			layout.SetVisible( "textures", true );
			Require( layout.GetPanel( "textures" ).Visible, "reopen must show the panel again" );
			Require( layout.GetPanel( "textures" ).Zone == DockZone.Right, "reopen must restore the Right zone" );
		} );

		Check( "modal-operation-fails-contract", () =>
		{
			var registry = PanelRegistry.Default();
			var operation = new MapOperationDeclaration( "face-edit-dialog", "face-edit", requiresModalDialog: true, modalReason: "Face Edit opens a modal dialog" );
			var result = NoModalContract.Check( operation, registry );
			Require( !result.Passed, "a modal-only operation must fail the contract check" );
			Require( result.Code == PanelLayoutErrorCode.ModalDialogNotAllowed, $"code must be ModalDialogNotAllowed, got {result.Code}" );
			Require( result.Reason.Contains( "face-edit-dialog", StringComparison.Ordinal ), $"reason must name the operation, got: {result.Reason}" );
			Require( result.Reason.Contains( "modal", StringComparison.OrdinalIgnoreCase ), $"reason must say modal, got: {result.Reason}" );
			Throws<PanelLayoutError>( () => NoModalContract.Require( operation, registry ), "modal-dialog" );
			try { NoModalContract.Require( operation, registry ); }
			catch ( PanelLayoutError ex ) { Require( ex.Code == PanelLayoutErrorCode.ModalDialogNotAllowed, $"thrown code must be ModalDialogNotAllowed, got {ex.Code}" ); }
		} );

		Check( "missing-panel-target-fails", () =>
		{
			var registry = PanelRegistry.Default();
			var operation = new MapOperationDeclaration( "place-entity", "" );
			var result = NoModalContract.Check( operation, registry );
			Require( !result.Passed, "an operation with no panel target must fail" );
			Require( result.Code == PanelLayoutErrorCode.MissingPanelTarget, $"code must be MissingPanelTarget, got {result.Code}" );
			Require( result.Reason.Contains( "place-entity", StringComparison.Ordinal ), $"reason must name the operation, got: {result.Reason}" );
			Throws<PanelLayoutError>( () => NoModalContract.Require( operation, registry ), "missing-panel-target" );
		} );

		Check( "unknown-panel-target-fails", () =>
		{
			var registry = PanelRegistry.Default();
			var operation = new MapOperationDeclaration( "edit-thing", "no-such-panel" );
			var result = NoModalContract.Check( operation, registry );
			Require( !result.Passed, "an operation targeting an unregistered panel must fail" );
			Require( result.Code == PanelLayoutErrorCode.UnknownPanelTarget, $"code must be UnknownPanelTarget, got {result.Code}" );
			Require( result.Reason.Contains( "no-such-panel", StringComparison.Ordinal ), $"reason must name the panel, got: {result.Reason}" );
		} );

		Check( "declared-panel-target-passes", () =>
		{
			var registry = PanelRegistry.Default();
			NoModalContract.Require( new MapOperationDeclaration( "place-entity", "entities" ), registry );
			NoModalContract.Require( new MapOperationDeclaration( "edit-face", "face-edit" ), registry );
			NoModalContract.Require( new MapOperationDeclaration( "run-build", "build" ), registry );
			var results = NoModalContract.CheckAll( new[]
			{
				new MapOperationDeclaration( "place-entity", "entities" ),
				new MapOperationDeclaration( "browse-textures", "textures" ),
			}, registry );
			Require( results.Count == 2 && results.All( r => r.Passed ), "all declared operations must pass" );
		} );

		Check( "duplicate-panel-registration-fails", () =>
		{
			var registry = new PanelRegistry();
			registry.Register( new PanelDefinition( "entities", "Entities", DockZone.Left, 280 ) );
			Throws<PanelLayoutError>( () => registry.Register( new PanelDefinition( "entities", "Entities again", DockZone.Right, 200 ) ), "duplicate-panel-id" );
			Require( registry.Count == 1, "the duplicate must not be registered" );
		} );

		Check( "unknown-panel-move-fails", () =>
		{
			var layout = LayoutDocument.CreateDefault();
			Throws<PanelLayoutError>( () => layout.MovePanel( "no-such-panel", DockZone.Left, 0 ), "unknown-panel-id" );
			Throws<PanelLayoutError>( () => layout.GetPanel( "no-such-panel" ), "unknown-panel-id" );
			Throws<PanelLayoutError>( () => layout.ResizePanel( "no-such-panel", 100 ), "unknown-panel-id" );
			Throws<PanelLayoutError>( () => layout.SetVisible( "no-such-panel", false ), "unknown-panel-id" );
		} );

		Check( "invalid-size-rejected", () =>
		{
			var layout = LayoutDocument.CreateDefault();
			Throws<PanelLayoutError>( () => layout.ResizePanel( "entities", 0 ), "invalid-panel-size" );
			Throws<PanelLayoutError>( () => layout.ResizePanel( "entities", -50 ), "invalid-panel-size" );
			Throws<PanelLayoutError>( () => new PanelDefinition( "x", "X", DockZone.Left, 0 ), "invalid-panel-size" );
			Throws<PanelLayoutError>( () => layout.MovePanel( "entities", DockZone.Left, -1 ), "invalid-panel-order" );
			Throws<PanelLayoutError>( () => layout.SetFloatingRect( "face-edit", 0, 0, 0, 100 ), "invalid-floating-rect" );
			Require( layout.GetPanel( "entities" ).Size == 280, "a rejected resize must not change the size" );
		} );

		CheckMissing( "old-version-load-fails-named", () =>
		{
			var json = File.ReadAllText( Path.Combine( FindFixtureDir(), "layout-old-version.json" ) );
			try { LayoutDocument.FromJson( json ); }
			catch ( PanelLayoutError ex )
			{
				Require( ex.Code == PanelLayoutErrorCode.UnknownSchemaVersion, $"code must be UnknownSchemaVersion, got {ex.Code}" );
				Require( ex.Message.Contains( "99", StringComparison.Ordinal ), $"error must name the found version 99, got: {ex.Message}" );
				return;
			}
			throw new Exception( "old-version layout loaded silently instead of failing with unknown-schema-version" );
		}, () => FindFixtureDir() is null ? "fixture layout-old-version.json not found under tests/fixtures/hammer/hammer-panels-docked-layout-model" : null );

		Check( "default-registry-covers-all-dock-zones", () =>
		{
			var registry = PanelRegistry.Default();
			foreach ( var zone in Enum.GetValues<DockZone>() )
				Require( registry.Panels.Any( p => p.DefaultZone == zone ), $"default registry must cover dock zone {zone}" );
			Require( !Enum.GetNames<DockZone>().Any( name => name.Contains( "Modal", StringComparison.OrdinalIgnoreCase ) ),
				"there must be no modal dock zone" );
			Require( registry.IsRegistered( "entities" ) && registry.IsRegistered( "properties" ) && registry.IsRegistered( "textures" )
				&& registry.IsRegistered( "console" ) && registry.IsRegistered( "build" ) && registry.IsRegistered( "face-edit" ),
				"default registry must include the standard Hammer-style panels" );
			Throws<PanelLayoutError>( () => registry.Get( "no-such-panel" ), "unknown-panel-id" );
		} );

		Check( "move-panel-changes-zone-and-order", () =>
		{
			var layout = LayoutDocument.CreateDefault();
			layout.MovePanel( "textures", DockZone.Bottom, 2 );
			var textures = layout.GetPanel( "textures" );
			Require( textures.Zone == DockZone.Bottom, "textures must move to Bottom" );
			Require( textures.Order == 2, "textures order must become 2" );
			Require( textures.Visible, "moving must not hide the panel" );
			Approx( textures.Size, 280, "moving must keep the size" );
		} );

		CheckMissing( "default-fixture-matches-create-default", () =>
		{
			var fromFixture = LayoutDocument.Load( Path.Combine( FindFixtureDir(), "layout-default.json" ) );
			SameLayout( fromFixture, LayoutDocument.CreateDefault(), "default fixture" );
		}, () => FindFixtureDir() is null ? "fixture layout-default.json not found under tests/fixtures/hammer/hammer-panels-docked-layout-model" : null );

		Check( "save-writes-versioned-json", () =>
		{
			var dir = TempDir();
			try
			{
				var layout = LayoutDocument.CreateDefault( "test-room", "default" );
				var path = Path.Combine( dir, "layout.json" );
				layout.Save( path );
				using var parsed = JsonDocument.Parse( File.ReadAllText( path ) );
				var root = parsed.RootElement;
				Require( root.GetProperty( "schemaVersion" ).GetInt32() == LayoutDocument.CurrentSchemaVersion,
					"saved JSON must carry the current schemaVersion" );
				Require( root.GetProperty( "mapId" ).GetString() == "test-room", "saved JSON must carry the map id" );
				Require( root.GetProperty( "panels" ).GetArrayLength() == PanelRegistry.Default().Count,
					"saved JSON must carry every default panel" );
				Require( File.Exists( path ), "saved layout file must exist" );
			}
			finally { Directory.Delete( dir, true ); }
		} );
	}
}
