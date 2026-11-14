using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;

// Hammer gizmo state persistence + always-available contract (no rendering, no UI).
// Board task: hammer-gizmo-state-persistence (worker muse-3-desktop-hammer-47).
// Group: gizmostatepersistence. Register line (coordinator-owned TestRegistry.cs):
//     GizmoStatePersistenceTests.Register( registry, settings );
// Fixture: tests/fixtures/hammer/hammer-gizmo-state-persistence/saved-states.json.
// Depends on hammer-gizmo-pivot-tool-model (worker 32): GizmoModel, GizmoState,
// GizmoMode/Space/AxisLock, DragToTransform. This file adds no new behaviour to
// those APIs; it only exercises the persistence API added in GizmoModel.cs:
// SaveState/SaveStateString/SaveToDocument, LoadState/LoadStateString/
// LoadFromDocument, GizmoHealthCheck, GizmoLoadResult/GizmoHealthResult.
// MapDocument note: no MapDocument class exists on disk yet (hammer-12's file
// has not landed), so the "map document" here is the document's JsonObject —
// the same pattern FaceMaterial uses — with the gizmo state under the "gizmo"
// key (GizmoModel.DocumentSectionKey). A future MapDocument embeds it verbatim.

internal static class GizmoStatePersistenceTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "gizmostatepersistence", name, test );
		void CheckMissing( string name, Action test, Func<string?> missing ) => registry.Add( "gizmostatepersistence", name, test, missing );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( double actual, double expected, double tolerance, string message )
		{
			if ( Math.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}
		void NearVec( Vector3 actual, Vector3 expected, float tolerance, string message )
		{
			if ( Vector3.Distance( actual, expected ) > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}
		void NearQuat( Quaternion actual, Quaternion expected, float tolerance, string message )
		{
			var d = MathF.Abs( actual.X - expected.X ) + MathF.Abs( actual.Y - expected.Y )
				+ MathF.Abs( actual.Z - expected.Z ) + MathF.Abs( actual.W - expected.W );
			if ( d > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}
		void RequireState( GizmoModel model, GizmoMode mode, GizmoSpace space, GizmoAxisLock locked, Vector3 pivot, Quaternion orientation, bool snap, double grid, double angle, string message )
		{
			Require( model.Mode == mode, $"{message}: mode expected {mode}, got {model.Mode}" );
			Require( model.Space == space, $"{message}: space expected {space}, got {model.Space}" );
			Require( model.LockedAxis == locked, $"{message}: lock expected {locked}, got {model.LockedAxis}" );
			NearVec( model.Pivot, pivot, 1e-6f, $"{message}: pivot" );
			NearQuat( model.SelectionOrientation, orientation, 1e-6f, $"{message}: orientation" );
			Require( model.SnapEnabled == snap, $"{message}: snap expected {snap}, got {model.SnapEnabled}" );
			Near( model.GridSize, grid, 1e-9, $"{message}: grid" );
			Near( model.AngleSnapDegrees, angle, 1e-9, $"{message}: angle snap" );
		}
		void RequireDefaults( GizmoModel model, string message ) => RequireState( model,
			GizmoMode.Translate, GizmoSpace.Global, GizmoAxisLock.None, Vector3.Zero, Quaternion.Identity,
			false, GizmoModel.DefaultGridSize, GizmoModel.DefaultAngleSnapDegrees, message );
		void RequireWarningFor( IReadOnlyList<string> warnings, string field, string message )
		{
			foreach ( var w in warnings )
				if ( w.Contains( field, StringComparison.OrdinalIgnoreCase ) ) return;
			throw new Exception( $"{message}: no warning names '{field}' (warnings: {string.Join( "; ", warnings )})" );
		}
		GizmoModel NonDefaultModel()
		{
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Rotate );
			model.SetSpace( GizmoSpace.Local );
			model.LockAxis( GizmoAxisLock.XY );
			model.SetPivot( new Vector3( 16f, 32f, -8f ) );
			model.SetSelectionOrientation( Quaternion.CreateFromAxisAngle( Vector3.UnitY, MathF.PI / 2f ) );
			model.SetSnap( true, 8.0, 30.0 );
			return model;
		}
		Quaternion QuarterTurnY() => Quaternion.CreateFromAxisAngle( Vector3.UnitY, MathF.PI / 2f );

		// 1. Round-trip: a non-default state survives save/load through a string.
		Check( "round-trip saves and loads a non-default state", () =>
		{
			var saved = NonDefaultModel().SaveStateString();
			var loaded = new GizmoModel();
			var result = loaded.LoadStateString( saved );
			Require( result.Warnings.Count == 0, $"clean round-trip warns: {string.Join( "; ", result.Warnings )}" );
			Require( !result.Migrated, "clean round-trip must not report a migration" );
			Require( loaded.LastLoadWarnings.Count == 0, "LastLoadWarnings empty after a clean load" );
			RequireState( loaded, GizmoMode.Rotate, GizmoSpace.Local, GizmoAxisLock.XY,
				new Vector3( 16f, 32f, -8f ), QuarterTurnY(), true, 8.0, 30.0, "round-trip" );
		} );

		// 2. SaveState is a versioned section with named fields.
		Check( "save produces a versioned section with named fields", () =>
		{
			var section = NonDefaultModel().SaveState();
			Require( section["schemaVersion"]!.GetValue<int>() == GizmoModel.SchemaVersion, "schemaVersion is 1" );
			Require( section["mode"]!.GetValue<string>() == "Rotate", "mode is the name Rotate" );
			Require( section["space"]!.GetValue<string>() == "Local", "space is the name Local" );
			Require( section["lockedAxis"]!.GetValue<string>() == "XY", "lock is the name XY" );
			var pivot = section["pivot"]!.AsArray();
			Require( pivot.Count == 3, "pivot has 3 components" );
			Near( pivot[0]!.GetValue<double>(), 16.0, 1e-9, "pivot x" );
			Near( pivot[1]!.GetValue<double>(), 32.0, 1e-9, "pivot y" );
			Near( pivot[2]!.GetValue<double>(), -8.0, 1e-9, "pivot z" );
			Require( section["snapEnabled"]!.GetValue<bool>(), "snapEnabled is true" );
			Near( section["gridSize"]!.GetValue<double>(), 8.0, 1e-9, "gridSize" );
			Near( section["angleSnapDegrees"]!.GetValue<double>(), 30.0, 1e-9, "angleSnapDegrees" );
		} );

		// 3. Document round-trip: only the "gizmo" key is written; siblings survive.
		Check( "document round-trip preserves sibling fields", () =>
		{
			var document = JsonNode.Parse( "{\"name\":\"test-map\",\"version\":2}" )!.AsObject();
			NonDefaultModel().SaveToDocument( document );
			Require( document["name"]!.GetValue<string>() == "test-map", "sibling 'name' untouched by save" );
			Require( document["version"]!.GetValue<int>() == 2, "sibling 'version' untouched by save" );
			Require( document["gizmo"] is JsonObject, "gizmo section written" );
			var loaded = new GizmoModel();
			var result = loaded.LoadFromDocument( document );
			Require( result.Warnings.Count == 0, "document round-trip warns" );
			RequireState( loaded, GizmoMode.Rotate, GizmoSpace.Local, GizmoAxisLock.XY,
				new Vector3( 16f, 32f, -8f ), QuarterTurnY(), true, 8.0, 30.0, "document round-trip" );
			Require( document["name"]!.GetValue<string>() == "test-map", "sibling 'name' untouched by load" );
		} );

		// 4. Session reload: mode/space/lock survive (spec's explicit case).
		Check( "mode space and lock survive a session reload", () =>
		{
			var before = new GizmoModel();
			before.SetMode( GizmoMode.Scale );
			before.SetSpace( GizmoSpace.Local );
			before.LockAxis( GizmoAxisLock.Z );
			var saved = before.SaveStateString();
			var after = new GizmoModel(); // a new session: fresh model, same saved text
			after.LoadStateString( saved );
			Require( after.Mode == GizmoMode.Scale, "mode survives reload" );
			Require( after.Space == GizmoSpace.Local, "space survives reload" );
			Require( after.LockedAxis == GizmoAxisLock.Z, "lock survives reload" );
		} );

		// 5. Health check passes on a fresh model.
		Check( "health check passes on a fresh model", () =>
		{
			var health = new GizmoModel().GizmoHealthCheck();
			Require( health.Passed, $"fresh model health failed: {health.Reason}" );
			Require( health.Reason.Contains( "available", StringComparison.OrdinalIgnoreCase ), $"fresh reason names availability: {health.Reason}" );
			var staticHealth = GizmoModel.GizmoHealthCheck( new GizmoModel().State );
			Require( staticHealth.Passed, $"static health check on a fresh state failed: {staticHealth.Reason}" );
		} );

		// 6. Health check passes on a fresh document (no gizmo section).
		Check( "health check passes on a fresh document", () =>
		{
			var model = new GizmoModel();
			var result = model.LoadFromDocument( new JsonObject() );
			Require( result.Warnings.Count == 0, "fresh document must load silently (no section = fresh, not corrupt)" );
			Require( !result.Migrated, "fresh document must not report a migration" );
			RequireDefaults( model, "fresh document" );
			var health = model.GizmoHealthCheck();
			Require( health.Passed, $"fresh document health failed: {health.Reason}" );
		} );

		// 7. Corrupted gizmo section: per-field fallback to defaults + named warnings, health still passes.
		Check( "corrupted gizmo section falls back to defaults with named warnings", () =>
		{
			var section = JsonNode.Parse( "{\"mode\":\"Banana\",\"space\":99,\"gridSize\":-4,\"pivot\":\"nope\",\"angleSnapDegrees\":0}" )!.AsObject();
			var model = new GizmoModel();
			var result = model.LoadState( section );
			Require( result.Warnings.Count == 5, $"expected 5 warnings, got {result.Warnings.Count}: {string.Join( "; ", result.Warnings )}" );
			Require( result.Migrated, "corrupted load must report a migration" );
			RequireWarningFor( result.Warnings, "mode", "corrupted" );
			RequireWarningFor( result.Warnings, "space", "corrupted" );
			RequireWarningFor( result.Warnings, "gridSize", "corrupted" );
			RequireWarningFor( result.Warnings, "pivot", "corrupted" );
			RequireWarningFor( result.Warnings, "angleSnapDegrees", "corrupted" );
			RequireDefaults( model, "corrupted section" );
			Require( model.LastLoadWarnings.Count == 5, "LastLoadWarnings mirrors the result" );
			var health = model.GizmoHealthCheck();
			Require( health.Passed, $"health must pass after a corrupted load (fallback to defaults): {health.Reason}" );
		} );

		// 8. Unknown fields warn by name and survive a re-save (round trip keeps them).
		Check( "unknown fields warn by name and survive a re-save", () =>
		{
			var section = JsonNode.Parse( "{\"mode\":\"Rotate\",\"hyperdrive\":true,\"futurePivot\":[1,2,3]}" )!.AsObject();
			var model = new GizmoModel();
			var result = model.LoadState( section );
			Require( result.Warnings.Count == 2, $"expected 2 unknown-field warnings, got {result.Warnings.Count}" );
			RequireWarningFor( result.Warnings, "hyperdrive", "unknown fields" );
			RequireWarningFor( result.Warnings, "futurePivot", "unknown fields" );
			Require( model.Mode == GizmoMode.Rotate, "known field still loads alongside unknown fields" );
			Require( model.PreservedUnknownFields.Count == 2, "both unknown fields preserved" );
			var resaved = model.SaveState();
			Require( resaved["hyperdrive"]!.GetValue<bool>(), "unknown boolean re-emitted on save" );
			Require( resaved["futurePivot"]!.AsArray()[1]!.GetValue<int>() == 2, "unknown array re-emitted on save" );
			var reloaded = new GizmoModel();
			var second = reloaded.LoadState( resaved );
			Require( second.Warnings.Count == 2, "re-loaded unknowns warn again" );
			Require( reloaded.Mode == GizmoMode.Rotate, "known field survives the unknown-field round trip" );
		} );

		// 9. Newer schema version warns but still loads the known fields.
		Check( "newer schema version warns but still loads known fields", () =>
		{
			var section = JsonNode.Parse( "{\"schemaVersion\":99,\"mode\":\"Scale\",\"quantumLock\":\"on\"}" )!.AsObject();
			var model = new GizmoModel();
			var result = model.LoadState( section );
			Require( result.Warnings.Count == 2, $"expected 2 warnings (version + unknown field), got {result.Warnings.Count}" );
			RequireWarningFor( result.Warnings, "99", "newer version" );
			RequireWarningFor( result.Warnings, "newer", "newer version" );
			RequireWarningFor( result.Warnings, "quantumLock", "newer version" );
			Require( model.Mode == GizmoMode.Scale, "known field loads from a newer-version section" );
			Require( model.GizmoHealthCheck().Passed, "health passes after a newer-version load" );
		} );

		// 10. Invalid JSON text falls back to defaults with a warning, never throws.
		Check( "invalid JSON text falls back to defaults with a warning", () =>
		{
			var model = new GizmoModel();
			var result = model.LoadStateString( "{not json" );
			Require( result.Warnings.Count == 1, $"expected 1 warning, got {result.Warnings.Count}" );
			RequireWarningFor( result.Warnings, "not valid JSON", "invalid JSON" );
			RequireDefaults( model, "invalid JSON" );
			Require( model.GizmoHealthCheck().Passed, "health passes after invalid JSON" );
			var empty = new GizmoModel();
			var emptyResult = empty.LoadStateString( "" );
			Require( emptyResult.Warnings.Count == 0, "empty text is a fresh document, not corruption" );
			RequireDefaults( empty, "empty text" );
			var array = new GizmoModel();
			var arrayResult = array.LoadStateString( "[]" );
			Require( arrayResult.Warnings.Count == 1, "non-object JSON warns once" );
			RequireWarningFor( arrayResult.Warnings, "not a JSON object", "non-object JSON" );
		} );

		// 11. A gizmo section that is present but not an object falls back with a warning.
		Check( "non-object gizmo section falls back to defaults with a warning", () =>
		{
			var document = JsonNode.Parse( "{\"name\":\"x\",\"gizmo\":\"just a string\"}" )!.AsObject();
			var model = new GizmoModel();
			var result = model.LoadFromDocument( document );
			Require( result.Warnings.Count == 1, $"expected 1 warning, got {result.Warnings.Count}" );
			RequireWarningFor( result.Warnings, "not a JSON object", "non-object section" );
			RequireDefaults( model, "non-object section" );
			Require( model.GizmoHealthCheck().Passed, "health passes after a non-object section" );
			Require( document["name"]!.GetValue<string>() == "x", "sibling field untouched by a failed load" );
		} );

		// 12. Missing fields fall back silently to defaults (older sections stay quiet).
		Check( "missing fields fall back silently to defaults", () =>
		{
			var section = JsonNode.Parse( "{\"mode\":\"Scale\"}" )!.AsObject();
			var model = new GizmoModel();
			var result = model.LoadState( section );
			Require( result.Warnings.Count == 0, $"a partial section must not warn: {string.Join( "; ", result.Warnings )}" );
			Require( model.Mode == GizmoMode.Scale, "present field loads" );
			Require( model.Space == GizmoSpace.Global, "missing space defaults silently" );
			Require( model.LockedAxis == GizmoAxisLock.None, "missing lock defaults silently" );
			NearVec( model.Pivot, Vector3.Zero, 1e-6f, "missing pivot defaults silently" );
			Require( !model.SnapEnabled && model.GridSize == GizmoModel.DefaultGridSize, "missing snap defaults silently" );
			var nullResult = new GizmoModel().LoadState( null! );
			Require( nullResult.Warnings.Count == 0, "null section is a fresh document, not corruption" );
		} );

		// 13. Invalid pivot/orientation fall back per-field; valid fields are kept.
		Check( "invalid pivot and orientation fall back per-field, valid fields kept", () =>
		{
			var section = JsonNode.Parse( "{\"mode\":\"Rotate\",\"pivot\":[1,2],\"selectionOrientation\":[0,0,0,0]}" )!.AsObject();
			var model = new GizmoModel();
			var result = model.LoadState( section );
			Require( result.Warnings.Count == 2, $"expected 2 warnings, got {result.Warnings.Count}" );
			RequireWarningFor( result.Warnings, "pivot", "bad pivot" );
			RequireWarningFor( result.Warnings, "selectionOrientation", "bad orientation" );
			Require( model.Mode == GizmoMode.Rotate, "valid mode kept despite bad neighbours" );
			NearVec( model.Pivot, Vector3.Zero, 1e-6f, "bad pivot falls back to origin" );
			Require( model.SelectionOrientation == Quaternion.Identity, "zero orientation falls back to identity" );
			var longOrientation = JsonNode.Parse( "{\"selectionOrientation\":[0,0,0,2]}" )!.AsObject();
			var normalized = new GizmoModel();
			normalized.LoadState( longOrientation );
			Require( normalized.SelectionOrientation == Quaternion.Identity, "non-unit orientation normalizes instead of failing" );
			Require( normalized.LastLoadWarnings.Count == 0, "a normalizable orientation is not a warning" );
		} );

		// 14. Health check fails on a corrupt hand-built state, with a named reason.
		Check( "health check fails on a corrupt hand-built state with a named reason", () =>
		{
			var badMode = new GizmoState( (GizmoMode)99, GizmoSpace.Global, GizmoAxisLock.None, Vector3.Zero, Quaternion.Identity, false, 16.0, 15.0 );
			var modeHealth = GizmoModel.GizmoHealthCheck( badMode );
			Require( !modeHealth.Passed, "undefined mode must fail the health check" );
			Require( modeHealth.Reason.Contains( "mode", StringComparison.OrdinalIgnoreCase ), $"mode reason names the field: {modeHealth.Reason}" );
			var badPivot = new GizmoState( GizmoMode.Translate, GizmoSpace.Global, GizmoAxisLock.None, new Vector3( float.NaN, 0f, 0f ), Quaternion.Identity, false, 16.0, 15.0 );
			var pivotHealth = GizmoModel.GizmoHealthCheck( badPivot );
			Require( !pivotHealth.Passed, "NaN pivot must fail the health check" );
			Require( pivotHealth.Reason.Contains( "pivot", StringComparison.OrdinalIgnoreCase ), $"pivot reason names the field: {pivotHealth.Reason}" );
			var badOrientation = new GizmoState( GizmoMode.Translate, GizmoSpace.Global, GizmoAxisLock.None, Vector3.Zero, new Quaternion( 0f, 0f, 0f, 0f ), false, 16.0, 15.0 );
			var orientationHealth = GizmoModel.GizmoHealthCheck( badOrientation );
			Require( !orientationHealth.Passed, "zero orientation must fail the health check" );
			Require( orientationHealth.Reason.Contains( "orientation", StringComparison.OrdinalIgnoreCase ), $"orientation reason names the field: {orientationHealth.Reason}" );
			var nonUnit = new GizmoState( GizmoMode.Translate, GizmoSpace.Global, GizmoAxisLock.None, Vector3.Zero, new Quaternion( 0f, 0f, 0f, 2f ), false, 16.0, 15.0 );
			Require( !GizmoModel.GizmoHealthCheck( nonUnit ).Passed, "non-unit orientation must fail the health check" );
			var badGrid = new GizmoState( GizmoMode.Translate, GizmoSpace.Global, GizmoAxisLock.None, Vector3.Zero, Quaternion.Identity, false, -4.0, 15.0 );
			Require( !GizmoModel.GizmoHealthCheck( badGrid ).Passed, "non-positive grid size must fail the health check" );
		} );

		// 15. Worker-32 behaviour is untouched: a loaded state still drives drag math.
		Check( "loaded state still drives drag math", () =>
		{
			var model = new GizmoModel();
			model.LoadStateString( "{\"mode\":\"Translate\",\"lockedAxis\":\"X\"}" );
			var matrix = model.DragToTransform( new GizmoDrag( new Vector3( 1f, 2f, 3f ) ), GizmoView.Front );
			NearVec( matrix.Translation, new Vector3( 1f, 0f, 0f ), 1e-6f, "axis-locked translate after load" );
			model.LoadStateString( "{\"mode\":\"Rotate\",\"lockedAxis\":\"Z\",\"pivot\":[10,0,0]}" );
			var rotated = model.DragToTransform( new GizmoDrag( new Vector3( 1f, 1f, 0f ) ), GizmoView.Front );
			NearVec( Vector3.Transform( new Vector3( 11f, 0f, 0f ), rotated ),
				new Vector3( 10f + MathF.Cos( MathF.PI / 4f ), MathF.Sin( MathF.PI / 4f ), 0f ), 1e-5f,
				"rotate about loaded pivot" );
		} );

		// 16. Fixture: every saved-state case in saved-states.json loads as expected.
		CheckMissing( "fixture saved-states.json", () =>
		{
			var path = FindFixture()!;
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var cases = doc.RootElement.GetProperty( "cases" );
			Require( cases.GetArrayLength() == 4, "fixture has 4 saved-state cases" );
			foreach ( var c in cases.EnumerateArray() )
			{
				var name = c.GetProperty( "name" ).GetString()!;
				var document = JsonNode.Parse( c.GetProperty( "document" ).GetRawText() )!.AsObject();
				var expect = c.GetProperty( "expected" );
				var model = new GizmoModel();
				var result = model.LoadFromDocument( document );
				Require( model.Mode.ToString() == expect.GetProperty( "mode" ).GetString(), $"case {name}: mode" );
				Require( model.Space.ToString() == expect.GetProperty( "space" ).GetString(), $"case {name}: space" );
				Require( model.LockedAxis.ToString() == expect.GetProperty( "lock" ).GetString(), $"case {name}: lock" );
				NearVec( model.Pivot, ReadVec3( expect.GetProperty( "pivot" ) ), 1e-6f, $"case {name}: pivot" );
				NearQuat( model.SelectionOrientation, ReadQuat( expect.GetProperty( "orientation" ) ), 1e-6f, $"case {name}: orientation" );
				Require( model.SnapEnabled == expect.GetProperty( "snapEnabled" ).GetBoolean(), $"case {name}: snap" );
				Near( model.GridSize, expect.GetProperty( "gridSize" ).GetDouble(), 1e-9, $"case {name}: grid" );
				Near( model.AngleSnapDegrees, expect.GetProperty( "angleSnapDegrees" ).GetDouble(), 1e-9, $"case {name}: angle snap" );
				Require( result.Warnings.Count >= expect.GetProperty( "minWarnings" ).GetInt32(), $"case {name}: expected at least {expect.GetProperty( "minWarnings" ).GetInt32()} warnings, got {result.Warnings.Count}" );
				foreach ( var needle in expect.GetProperty( "warningContains" ).EnumerateArray() )
					RequireWarningFor( result.Warnings, needle.GetString()!, $"case {name}" );
				Require( model.GizmoHealthCheck().Passed, $"case {name}: health must pass after load" );
			}
		}, () => FindFixture() is null ? "fixture saved-states.json not found under the repo root" : null );

		static string? FindFixture()
		{
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			for ( int i = 0; i < 8 && dir != null; i++ )
			{
				var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-gizmo-state-persistence", "saved-states.json" );
				if ( File.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			return null;
		}

		Vector3 ReadVec3( JsonElement element )
		{
			var values = new List<float>();
			foreach ( var item in element.EnumerateArray() ) values.Add( item.GetSingle() );
			Require( values.Count == 3, "expected a 3-component vector in the fixture" );
			return new Vector3( values[0], values[1], values[2] );
		}

		Quaternion ReadQuat( JsonElement element )
		{
			var values = new List<float>();
			foreach ( var item in element.EnumerateArray() ) values.Add( item.GetSingle() );
			Require( values.Count == 4, "expected a 4-component quaternion in the fixture" );
			return new Quaternion( values[0], values[1], values[2], values[3] );
		}
	}
}
