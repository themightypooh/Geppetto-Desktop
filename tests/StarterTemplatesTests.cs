using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

namespace Effigy.Map;

/// <summary>Hammer Desktop P2b: first-map starter templates + onboarding checklist
/// (hammer-first-map-starter-templates). Templates are built in code; the
/// tests/fixtures/hammer/hammer-first-map-starter-templates/ files are the
/// golden oracles (checked-in minimal-room.vmf + expected.json). Run with:
/// dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter startertemplates
/// (from the repo root; the Register line in tests/TestRegistry.cs is added by
/// the final integrator — see the ticket handoff).</summary>
internal static class StarterTemplatesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "startertemplates", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: wrong exception {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}
		bool Near( double a, double b, double tolerance ) => Math.Abs( a - b ) <= tolerance;

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-first-map-starter-templates" );
					if ( File.Exists( Path.Combine( candidate, "minimal-room.vmf" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "startertemplates fixture dir not found (minimal-room.vmf missing above cwd / BaseDirectory)" );
		}

		StarterMap WithoutLight()
		{
			var map = StarterTemplates.MinimalRoom();
			map.Document.Entities.RemoveAll( e => e.ClassName == "light" );
			return map;
		}

		Check( "catalogue lists three templates with distinct ids", () =>
		{
			var all = StarterTemplates.All;
			Require( all.Count == 3, $"catalogue has {all.Count} templates, expected 3" );
			Require( all.Select( t => t.Id ).Distinct( StringComparer.Ordinal ).Count() == 3, "template ids are not distinct" );
			Require( all.Any( t => t.Id == "minimal-room" ), "minimal-room missing" );
			Require( all.Any( t => t.Id == "surf-ramp" ), "surf-ramp missing" );
			Require( all.Any( t => t.Id == "outdoor-displacement" ), "outdoor-displacement missing" );
			foreach ( var template in all )
			{
				Require( !string.IsNullOrWhiteSpace( template.Name ), $"template {template.Id} has no name" );
				Require( !string.IsNullOrWhiteSpace( template.Description ), $"template {template.Id} has no description" );
			}
		} );

		Check( "unknown template id throws naming the id", () =>
		{
			Throws<ArgumentException>( () => StarterTemplates.Build( "no-such-template" ), "unknown id" );
			try { StarterTemplates.Build( "no-such-template" ); }
			catch ( ArgumentException ex ) { Require( ex.Message.Contains( "no-such-template" ), "error does not name the id: " + ex.Message ); }
			Throws<ArgumentException>( () => StarterTemplates.Build( "" ), "empty id" );
		} );

		Check( "minimal room validates clean and passes its own checklist", () =>
		{
			var map = StarterTemplates.MinimalRoom();
			Require( map.Document.Brushes.Count == 6, $"minimal room has {map.Document.Brushes.Count} brushes, expected 6" );
			Require( map.Document.Entities.Count == 2, $"minimal room has {map.Document.Entities.Count} entities, expected 2" );
			Require( map.HasEntity( "info_player_start" ), "no player start" );
			Require( map.HasEntity( "light" ), "no light" );
			Require( map.Worldspawn.SkyboxName == "sky_day01_01", "skybox is " + map.Worldspawn.SkyboxName );
			Require( !map.Skybox.Is3D, "minimal room should be a 2D skybox" );
			var issues = StarterTemplates.Validate( map );
			Require( issues.Count == 0, "minimal room has issues: " + string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			var result = Checklist.Evaluate( map );
			Require( result.AllPassed, "checklist did not pass: " + string.Join( "; ", result.Steps.Where( s => !s.Passed ).Select( s => s.StepId + ": " + s.Details ) ) );
			Require( result.FirstIncompleteStepId is null, "first incomplete step is " + result.FirstIncompleteStepId );
			Require( result.Steps.Count == 6, $"checklist has {result.Steps.Count} steps, expected 6" );
			Require( result.Steps.Select( s => s.StepId ).SequenceEqual( new[] { "seal", "spawn", "light", "skybox", "compile", "playtest" } ),
				"checklist step order moved" );
		} );

		Check( "minimal room brushes are valid with exact volumes", () =>
		{
			var map = StarterTemplates.MinimalRoom();
			foreach ( var brush in map.Document.Brushes )
			{
				var defects = InvalidBrushRule.Validate( brush );
				Require( defects.Count == 0, $"brush {brush.Id} invalid: {string.Join( "; ", defects )}" );
				Require( brush.Faces.Count == 6, $"brush {brush.Id} has {brush.Faces.Count} faces, expected 6" );
				Require( brush.Faces.All( f => f.Material == StarterTemplates.DevMaterial ), $"brush {brush.Id} has a non-dev face material" );
			}
			var floor = map.Document.Brushes[0];
			Require( Near( floor.Volume(), 512 * 512 * 16, 1.0 ), $"floor volume {floor.Volume()}, expected {512 * 512 * 16}" );
			var wall = map.Document.Brushes[2];
			Require( Near( wall.Volume(), 16 * 544 * 256, 1.0 ), $"wall volume {wall.Volume()}, expected {16 * 544 * 256}" );
		} );

		Check( "surf ramp starter has a wedge ramp and passes its checklist", () =>
		{
			var map = StarterTemplates.SurfRamp();
			Require( map.Document.Brushes.Count == 7, $"surf starter has {map.Document.Brushes.Count} brushes, expected 7" );
			var ramp = map.Document.Brushes[6];
			Require( ramp.Faces.Count == 5, $"ramp has {ramp.Faces.Count} faces, expected 5 (wedge)" );
			Require( InvalidBrushRule.Validate( ramp ).Count == 0, "ramp brush invalid: " + string.Join( "; ", InvalidBrushRule.Validate( ramp ) ) );
			// Tolerance 2.0, not 1.0: the slope plane normal is irrational, so the
			// float triple-plane vertices carry ~1e-6 rounding; the volume is
			// right to ~1 part in 4M.
			Require( Near( ramp.Volume(), 256 * 256 * 128 / 2.0, 2.0 ), $"ramp volume {ramp.Volume()}, expected {256 * 256 * 128 / 2.0}" );
			var result = Checklist.Evaluate( map );
			Require( result.AllPassed, "surf checklist did not pass: " + string.Join( "; ", result.Steps.Where( s => !s.Passed ).Select( s => s.StepId + ": " + s.Details ) ) );
			var spawn = map.FindEntities( "info_player_start" )[0];
			Require( spawn.GetKeyValue( "angles" ) == "0 180 0", "spawn does not face the ramp: " + spawn.GetKeyValue( "angles" ) );
		} );

		Check( "outdoor starter has displacement, sun, sky_camera and a 3D skybox section", () =>
		{
			var map = StarterTemplates.OutdoorDisplacement();
			Require( map.Document.Brushes.Count == 6, $"outdoor starter has {map.Document.Brushes.Count} brushes, expected 6" );
			Require( map.HasEntity( "light_environment" ), "no light_environment sun" );
			Require( map.HasSkyCamera, "no sky_camera" );
			Require( map.Skybox.Is3D, "outdoor starter should be a 3D skybox" );
			Require( map.Skybox.Section3D!.Scale == 16f, "3D section scale is " + map.Skybox.Section3D.Scale + ", expected 16" );
			Require( map.Skybox.Section3D.Brushes.Count == 3, $"3D section has {map.Skybox.Section3D.Brushes.Count} brushes, expected 3" );
			foreach ( var sectionBrush in map.Skybox.Section3D.Brushes )
				Require( InvalidBrushRule.Validate( sectionBrush ).Count == 0, $"3D section brush {sectionBrush.Id} invalid" );
			Require( map.Displacements.Count == 1, $"expected 1 displacement record, got {map.Displacements.Count}" );
			var disp = map.Displacements[0];
			Require( disp.BrushId == 1 && disp.FaceIndex == 5 && disp.Power == 3, $"displacement record is brush {disp.BrushId} face {disp.FaceIndex} power {disp.Power}" );
			var lid = map.Document.Brushes[1];
			Require( lid.Faces.All( f => f.Material == StarterTemplates.SkyboxMaterial ), "ceiling lid is not the skybox material" );
			var result = Checklist.Evaluate( map );
			Require( result.AllPassed, "outdoor checklist did not pass: " + string.Join( "; ", result.Steps.Where( s => !s.Passed ).Select( s => s.StepId + ": " + s.Details ) ) );
		} );

		Check( "checklist reports the missing step when the light is removed", () =>
		{
			var map = WithoutLight();
			// Removing a light creates no IssueEngine issue: this is a
			// checklist concern, not an engine rule.
			Require( StarterTemplates.Validate( map ).Count == 0, "light removal should not create engine issues" );
			var result = Checklist.Evaluate( map );
			Require( !result.AllPassed, "checklist passed with no light" );
			Require( result.FirstIncompleteStepId == "light", "first incomplete step is " + result.FirstIncompleteStepId + ", expected light" );
			var byId = result.Steps.ToDictionary( s => s.StepId );
			Require( !byId["light"].Passed, "light step passed with no light" );
			Require( byId["seal"].Passed && byId["spawn"].Passed && byId["skybox"].Passed, "unrelated steps failed" );
			Require( byId["compile"].Passed, "compile step failed with no light (no error issues exist)" );
			Require( !byId["playtest"].Passed, "playtest step passed while blocked" );
			Require( byId["playtest"].Details.Contains( "light" ), "playtest step does not name the blocker: " + byId["playtest"].Details );
		} );

		Check( "checklist reports the missing step when the spawn is removed", () =>
		{
			var map = StarterTemplates.MinimalRoom();
			map.Document.Entities.RemoveAll( e => e.ClassName == "info_player_start" );
			var result = Checklist.Evaluate( map );
			Require( !result.AllPassed, "checklist passed with no spawn" );
			var byId = result.Steps.ToDictionary( s => s.StepId );
			Require( !byId["spawn"].Passed, "spawn step passed with no spawn" );
			Require( !byId["playtest"].Passed, "playtest step passed while blocked" );
		} );

		Check( "checklist reports the missing step when the skybox name is cleared", () =>
		{
			var map = StarterTemplates.MinimalRoom();
			map.Worldspawn.SkyboxName = "";
			var result = Checklist.Evaluate( map );
			Require( !result.AllPassed, "checklist passed with no skybox" );
			Require( result.FirstIncompleteStepId == "skybox", "first incomplete step is " + result.FirstIncompleteStepId + ", expected skybox" );
			var byId = result.Steps.ToDictionary( s => s.StepId );
			Require( !byId["skybox"].Passed, "skybox step passed with no skybox name" );
			Require( byId["seal"].Passed && byId["spawn"].Passed && byId["light"].Passed, "unrelated steps failed" );
		} );

		Check( "checklist reports the missing sky_camera for a 3D section without one", () =>
		{
			var map = StarterTemplates.OutdoorDisplacement();
			map.Document.Entities.RemoveAll( e => e.ClassName == "sky_camera" );
			var result = Checklist.Evaluate( map );
			Require( !result.AllPassed, "checklist passed with a 3D section and no sky_camera" );
			var byId = result.Steps.ToDictionary( s => s.StepId );
			Require( !byId["skybox"].Passed, "skybox step passed with no sky_camera" );
			Require( byId["skybox"].Details.Contains( "sky_camera" ), "skybox step does not name sky_camera: " + byId["skybox"].Details );
		} );

		Check( "checklist seal and compile steps fail on an unsealed room", () =>
		{
			var map = StarterTemplates.MinimalRoom();
			map.Document.Brushes.RemoveAll( b => b.Id == 4 );
			var issues = StarterTemplates.Validate( map );
			Require( issues.Any( i => i.Code == "UNSEALED_MAP" || i.Code == "LEAK" ),
				"unsealed room produced no leak/unsealed issue: " + string.Join( "; ", issues.Select( i => i.Code ) ) );
			var result = Checklist.Evaluate( map, issues );
			var byId = result.Steps.ToDictionary( s => s.StepId );
			Require( !byId["seal"].Passed, "seal step passed on an unsealed room" );
			Require( !byId["compile"].Passed, "compile step passed with error issues" );
			Require( !byId["playtest"].Passed, "playtest step passed on an unsealed room" );
			Require( byId["seal"].BlockingIssueCodes.Count > 0, "seal step names no blocking issue code" );
		} );

		Check( "minimal room round-trips through VMF export", () =>
		{
			var map = StarterTemplates.MinimalRoom();
			var text = StarterTemplates.ToVmfText( map );
			Require( text.Contains( "\"skyname\" \"sky_day01_01\"" ), "VMF text has no skyname" );
			var again = VmfIo.Parse( text );
			Require( again.Validate().Count == 0, "round-tripped VMF invalid: " + string.Join( "; ", again.Validate() ) );
			Require( again.WorldspawnGet( "skyname" ) == "sky_day01_01", "skyname lost in round-trip" );
			Require( again.WorldBrushes.Count == 6, $"round-trip has {again.WorldBrushes.Count} world brushes, expected 6" );
			Require( again.AllBrushes().SelectMany( b => b.Faces ).Count() == 36, "round-trip face count moved" );
			Require( again.Entities.Count == 2, $"round-trip has {again.Entities.Count} entities, expected 2" );
			var spawn = again.Entities.FirstOrDefault( e => e.ClassName == "info_player_start" );
			Require( spawn is not null, "player start lost in round-trip" );
			Require( spawn!.Get( "origin" ) == "0 0 64", "spawn origin moved: " + spawn.Get( "origin" ) );
			var light = again.Entities.FirstOrDefault( e => e.ClassName == "light" );
			Require( light is not null && light.Get( "_light" ) == "255 240 200 400", "light keyvalues lost in round-trip" );
			foreach ( var brush in again.WorldBrushes )
				foreach ( var face in brush.Faces )
					Require( !string.IsNullOrEmpty( face.Material ), "a round-tripped face lost its material" );
		} );

		Check( "outdoor starter round-trips displacement and sky_camera through VMF", () =>
		{
			var map = StarterTemplates.OutdoorDisplacement();
			var again = VmfIo.Parse( StarterTemplates.ToVmfText( map ) );
			Require( again.Validate().Count == 0, "round-tripped outdoor VMF invalid: " + string.Join( "; ", again.Validate() ) );
			var dispFaces = again.AllBrushes().SelectMany( b => b.Faces ).Where( f => f.Displacement != null ).ToList();
			Require( dispFaces.Count == 1, $"expected 1 dispinfo face, got {dispFaces.Count}" );
			Require( dispFaces[0].Displacement!.Power == 3, "dispinfo power is " + dispFaces[0].Displacement.Power + ", expected 3" );
			Require( again.Entities.Any( e => e.ClassName == "sky_camera" ), "sky_camera lost in round-trip" );
			Require( again.Entities.Any( e => e.ClassName == "light_environment" ), "light_environment lost in round-trip" );
		} );

		Check( "checked-in minimal-room.vmf matches the builder", () =>
		{
			var dir = FixtureDir();
			var text = File.ReadAllText( Path.Combine( dir, "minimal-room.vmf" ) );
			var doc = VmfIo.Parse( text );
			Require( doc.Validate().Count == 0, "fixture VMF invalid: " + string.Join( "; ", doc.Validate() ) );
			Require( doc.WorldspawnGet( "skyname" ) == "sky_day01_01", "fixture skyname moved" );
			Require( doc.WorldBrushes.Count == 6, $"fixture has {doc.WorldBrushes.Count} world brushes, expected 6" );
			Require( doc.Entities.Count == 2, $"fixture has {doc.Entities.Count} entities, expected 2" );
			var expected = JsonDocument.Parse( File.ReadAllText( Path.Combine( dir, "minimal-room.expected.json" ) ) ).RootElement;
			Require( expected.GetProperty( "templateId" ).GetString() == "minimal-room", "expected.json templateId moved" );
			Require( expected.GetProperty( "brushes" ).GetInt32() == doc.WorldBrushes.Count, "expected.json brush count disagrees with the fixture" );
			Require( expected.GetProperty( "faces" ).GetInt32() == doc.WorldBrushes.SelectMany( b => b.Faces ).Count(), "expected.json face count disagrees with the fixture" );
			Require( expected.GetProperty( "entities" ).GetInt32() == doc.Entities.Count, "expected.json entity count disagrees with the fixture" );
			Require( expected.GetProperty( "skyboxName" ).GetString() == doc.WorldspawnGet( "skyname" ), "expected.json skybox disagrees with the fixture" );
			Require( expected.GetProperty( "checklistAllPassed" ).GetBoolean(), "expected.json says the checklist does not pass" );
			var map = StarterTemplates.MinimalRoom();
			Require( StarterTemplates.ToVmfText( map ) == text, "builder VMF text differs from the checked-in fixture (regenerate the fixture from the builder, do not edit it by hand)" );
		} );

		Check( "checked-in light-removed expectation matches the checklist", () =>
		{
			var dir = FixtureDir();
			var expected = JsonDocument.Parse( File.ReadAllText( Path.Combine( dir, "checklist-light-removed.expected.json" ) ) ).RootElement;
			var result = Checklist.Evaluate( WithoutLight() );
			Require( result.FirstIncompleteStepId == expected.GetProperty( "firstIncompleteStepId" ).GetString(),
				"first incomplete step is " + result.FirstIncompleteStepId );
			var steps = expected.GetProperty( "steps" );
			foreach ( var step in result.Steps )
			{
				Require( steps.TryGetProperty( step.StepId, out var want ),
					"expected.json has no entry for step " + step.StepId );
				Require( step.Passed == want.GetBoolean(),
					$"step {step.StepId} passed={step.Passed}, expected {want.GetBoolean()}" );
			}
		} );

		Check( "null arguments throw instead of silently returning nothing", () =>
		{
			Throws<ArgumentNullException>( () => StarterTemplates.Validate( null! ), "Validate(null)" );
			Throws<ArgumentNullException>( () => Checklist.Evaluate( null! ), "Evaluate(null)" );
			Throws<ArgumentNullException>( () => Checklist.Evaluate( StarterTemplates.MinimalRoom(), null! ), "Evaluate(map, null)" );
			Throws<ArgumentNullException>( () => StarterTemplates.ToVmf( null! ), "ToVmf(null)" );
		} );
	}
}
