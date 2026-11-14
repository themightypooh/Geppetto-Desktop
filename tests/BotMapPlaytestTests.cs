#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

internal static class BotMapPlaytestTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "botmapplaytest", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string? contains = null ) where T : Exception
		{
			try { action(); }
			catch ( T ex )
			{
				if ( contains is not null ) Require( ex.Message.Contains( contains, StringComparison.Ordinal ), $"error '{ex.Message}' must mention '{contains}'" );
				return;
			}
			catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}
		string Work( string name )
		{
			string dir = Path.Combine( settings.WorkDirectory, "botmapplaytest", name );
			Directory.CreateDirectory( dir );
			return dir;
		}
		string Fixture( string name )
		{
			var candidates = new[]
			{
				Path.Combine( AppContext.BaseDirectory, "fixtures", "hammer", "hammer-ai-bot-commands-map-playtest-loop", name ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", "hammer-ai-bot-commands-map-playtest-loop", name ) ),
				Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", "hammer-ai-bot-commands-map-playtest-loop", name ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return candidate;
			throw new Exception( "botmapplaytest fixture missing: " + name + " (probed " + string.Join( ", ", candidates ) + ")" );
		}
		void RequireStepsInOrder( BotMapPlaytestResult result )
		{
			Require( result.Steps.Count == BotMapPlaytest.StepNames.Length,
				$"loop must return {BotMapPlaytest.StepNames.Length} steps, got {result.Steps.Count}" );
			for ( int i = 0; i < BotMapPlaytest.StepNames.Length; i++ )
				Require( result.Steps[i].Name == BotMapPlaytest.StepNames[i],
					$"step {i} must be {BotMapPlaytest.StepNames[i]}, got {result.Steps[i].Name}" );
		}
		BotMapStepResult Step( BotMapPlaytestResult result, string name )
		{
			var step = result.Step( name );
			Require( step != null, "loop must have a " + name + " step" );
			return step!;
		}

		Check( "sealed room runs clean with no fix", () =>
		{
			var result = BotMapPlaytest.Run( Fixture( "sealed-room.vmf" ) );
			RequireStepsInOrder( result );
			Require( result.Passed, "sealed room loop must pass" );
			Require( !result.NeedsFix, "sealed room must not need a fix" );
			Require( !result.FixApplied, "sealed room must not apply a fix" );
			foreach ( var step in result.Steps )
				Require( step.Ran && step.Ok, $"sealed step {step.Name} must run and pass, got ran={step.Ran} ok={step.Ok} code={step.Code}" );
			Require( Step( result, "fix" ).Detail.Contains( "no fix needed", StringComparison.Ordinal ),
				"sealed fix step must say no fix needed" );
			Require( Step( result, "re-validate" ).Detail.Contains( "clean", StringComparison.Ordinal ),
				"sealed re-validate must report clean" );
		} );

		Check( "leaking room reports the leak and the scripted fix re-validates clean", () =>
		{
			var result = BotMapPlaytest.Run( Fixture( "leaking-room.vmf" ) );
			RequireStepsInOrder( result );
			Require( result.NeedsFix, "leaking room must need a fix" );
			Require( result.FixApplied, "leaking room must apply a fix" );
			Require( result.FixName == "seal-leak-portal",
				$"leaking fix must use the bake portal, got {result.FixName}" );
			Require( result.SealBox != null, "leaking fix must record its seal box" );
			var build = Step( result, "build" );
			Require( build.Ran && !build.Ok && build.Code == "leak",
				$"leaking build must report unsealed with code leak, got ok={build.Ok} code={build.Code}" );
			var validate = Step( result, "validate" );
			Require( validate.Ran && !validate.Ok, "leaking validate must fail" );
			Require( validate.IssueCodes.Contains( "LEAK" ),
				"leaking validate must name the LEAK issue, got: " + string.Join( ",", validate.IssueCodes ) );
			Require( validate.Code == "LEAK", $"leaking validate code must be LEAK, got {validate.Code}" );
			var play = Step( result, "play" );
			Require( play.Ran && play.Ok,
				$"leaking play must pass (spawn ok, walk 64in clear of the east wall), got ok={play.Ok} code={play.Code} detail={play.Detail}" );
			var report = Step( result, "report" );
			Require( report.Ran && report.Ok && report.IssueCodes.Contains( "LEAK" ),
				"leaking report must list the LEAK code" );
			var fix = Step( result, "fix" );
			Require( fix.Ran && fix.Ok, $"leaking fix must apply, got ok={fix.Ok} code={fix.Code} detail={fix.Detail}" );
			var revalidate = Step( result, "re-validate" );
			Require( revalidate.Ran && revalidate.Ok,
				$"leaking re-validate must be clean after the seal, got ok={revalidate.Ok} code={revalidate.Code} detail={revalidate.Detail}" );
			Require( !revalidate.IssueCodes.Contains( "LEAK" ), "re-validate must not list LEAK" );
			Require( result.Passed, "leaking room loop must pass after the scripted fix" );
		} );

		Check( "seal box covers the leak portal", () =>
		{
			var portal = new QuickPlayPortal
			{
				Min = new Vec3( 144f, -0.5f, 0f ),
				Max = new Vec3( 144f, 0.5f, 128f ),
			};
			var box = BotMapPlaytest.SealBoxForPortal( portal, 8f, 32f );
			Require( Math.Abs( box.Min.x - 112f ) < 1e-3f && Math.Abs( box.Max.x - 176f ) < 1e-3f,
				$"portal seal must straddle the wall x 112..176, got {box}" );
			Require( Math.Abs( box.Min.y - ( -8.5f ) ) < 1e-3f && Math.Abs( box.Max.y - 8.5f ) < 1e-3f,
				$"portal seal must cover the slit y -8.5..8.5, got {box}" );
			Require( Math.Abs( box.Min.z - ( -8f ) ) < 1e-3f && Math.Abs( box.Max.z - 136f ) < 1e-3f,
				$"portal seal must cover the slit height z -8..136, got {box}" );
		} );

		Check( "seal box falls back to the probe escape when the bake has no portal", () =>
		{
			var doc = OpsCli.LoadMap( Fixture( "leaking-room.vmf" ) );
			var issueDoc = OpsCli.ToIssueDocument( doc );
			var bake = new QuickPlayBakeResult { Sealed = false, LeakPortal = null };
			var box = BotMapPlaytest.SealBoxForBake( bake, issueDoc, null );
			Require( box != null, "escape fallback must locate the leak from the probe" );
			// Side plate: straddles the east outer face x=144 (+/-32) and spans
			// the full tangential bounds y -144..144, z -16..144, plus margin 8.
			Require( Math.Abs( box!.Min.x - 112f ) < 1e-3f && Math.Abs( box.Max.x - 176f ) < 1e-3f,
				$"escape seal must straddle the east wall x 112..176, got {box}" );
			Require( Math.Abs( box.Min.y - ( -152f ) ) < 1e-3f && Math.Abs( box.Max.y - 152f ) < 1e-3f,
				$"escape seal must span the full room width y -152..152, got {box}" );
			Require( Math.Abs( box.Min.z - ( -24f ) ) < 1e-3f && Math.Abs( box.Max.z - 152f ) < 1e-3f,
				$"escape seal must span the full room height z -24..152, got {box}" );
			// And the side plate genuinely seals: add it and re-bake.
			var (brushId, faceId) = BotMapPlaytest.NextIds( doc );
			doc.WorldBrushes.Add( BotMapPlaytest.BuildBoxBrush( brushId, faceId, box.Min, box.Max, "BRICK/BRICKWALL001A" ) );
			var rebake = QuickPlay.Bake( OpsCli.ToQuickPlayMap( doc ) );
			Require( rebake.Sealed, "fallback side plate must seal the leaking room" );
		} );

		Check( "seal box from an escape plates the leaking side", () =>
		{
			var escape = new LeakEscape
			{
				Direction = new Vec3( 1f, 0f, 0f ),
				// RayExitPoint expands the bounds by 64: the exit sits in the
				// void at x=208, NOT at the wall. The plate must still straddle
				// the wall face x=144.
				Exit = new Vec3( 208f, 0f, 32f ),
				Gapped = true,
			};
			var box = BotMapPlaytest.SealBoxForEscape( escape,
				new Vec3( -144f, -144f, -16f ), new Vec3( 144f, 144f, 144f ), 32f, 8f );
			Require( Math.Abs( box.Min.x - 112f ) < 1e-3f && Math.Abs( box.Max.x - 176f ) < 1e-3f, $"escape plate x must be 112..176, got {box}" );
			Require( Math.Abs( box.Min.y - ( -152f ) ) < 1e-3f && Math.Abs( box.Max.y - 152f ) < 1e-3f, $"escape plate y must be -152..152, got {box}" );
			Require( Math.Abs( box.Min.z - ( -24f ) ) < 1e-3f && Math.Abs( box.Max.z - 152f ) < 1e-3f, $"escape plate z must be -24..152, got {box}" );
		} );

		Check( "box brush projects back to its own bounds", () =>
		{
			var brush = BotMapPlaytest.BuildBoxBrush( 9, 900,
				new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), "BRICK/BRICKWALL001A" );
			Require( brush.Faces.Count == 6, $"box brush must have 6 faces, got {brush.Faces.Count}" );
			var doc = new VmfDocument();
			doc.WorldBrushes.Add( brush );
			var quick = OpsCli.ToQuickPlayMap( doc );
			Require( quick.Brushes.Count == 1, $"projected map must have 1 brush, got {quick.Brushes.Count}" );
			var projected = quick.Brushes[0];
			Require( Math.Abs( projected.Min.x ) < 1e-3f && Math.Abs( projected.Min.y ) < 1e-3f && Math.Abs( projected.Min.z ) < 1e-3f,
				$"projected min must be 0,0,0, got {projected.Min}" );
			Require( Math.Abs( projected.Max.x - 64f ) < 1e-3f && Math.Abs( projected.Max.y - 64f ) < 1e-3f && Math.Abs( projected.Max.z - 64f ) < 1e-3f,
				$"projected max must be 64,64,64, got {projected.Max}" );
			var issues = new IssueEngine().Run( OpsCli.ToIssueDocument( doc ) );
			Require( !issues.Any( i => i != null && i.Code == "INVALID_BRUSH" ),
				"box brush must not be an invalid brush" );
			var (nextBrush, nextFace) = BotMapPlaytest.NextIds( OpsCli.LoadMap( Fixture( "sealed-room.vmf" ) ) );
			Require( nextBrush == 7, $"sealed room next brush id must be 7, got {nextBrush}" );
			Require( nextFace > 600, $"sealed room next face id must be past the fixture faces, got {nextFace}" );
		} );

		Check( "missing player start fails the build step with a named code", () =>
		{
			var result = BotMapPlaytest.Run( Fixture( "no-spawn-room.vmf" ) );
			var build = Step( result, "build" );
			Require( build.Ran && !build.Ok && build.Code == "no-player-start",
				$"no-spawn build must fail with no-player-start, got ok={build.Ok} code={build.Code}" );
			foreach ( var name in new[] { "validate", "play", "report", "fix", "re-validate" } )
				Require( !Step( result, name ).Ran, $"no-spawn {name} step must not run after a failed build" );
			Require( !result.Passed, "no-spawn loop must not pass" );
		} );

		Check( "fixed map round-trips through VmfIo", () =>
		{
			string dir = Work( "roundtrip" );
			string fixedPath = Path.Combine( dir, "leaking-fixed.vmf" );
			var before = OpsCli.LoadMap( Fixture( "leaking-room.vmf" ) );
			var result = BotMapPlaytest.Run( Fixture( "leaking-room.vmf" ),
				new BotMapPlaytestOptions { FixedMapPath = fixedPath } );
			Require( result.Passed, "leaking loop must pass before the round-trip check" );
			Require( result.FixedMapPath == fixedPath, "loop must record the fixed map path" );
			Require( File.Exists( fixedPath ), "fixed map must be written to " + fixedPath );
			var reloaded = OpsCli.LoadMap( fixedPath );
			Require( reloaded.WorldBrushes.Count == before.WorldBrushes.Count + 1,
				$"reloaded map must have one more world brush than the leaking input, got {reloaded.WorldBrushes.Count} vs {before.WorldBrushes.Count}" );
			var bake = QuickPlay.Bake( OpsCli.ToQuickPlayMap( reloaded ) );
			Require( bake.Sealed, "reloaded fixed map must bake sealed" );
			Require( bake.Spawn.Ok && !bake.Walk.Blocked, "reloaded fixed map must spawn and walk clean" );
		} );

		Check( "missing file and non-map extension throw before any step", () =>
		{
			string dir = Work( "missing" );
			Throws<FileNotFoundException>( () => BotMapPlaytest.Run( Path.Combine( dir, "missing.vmf" ) ) );
			// LoadMap checks existence before the extension, so the bad
			// extension only surfaces once the file exists.
			string bad = Path.Combine( dir, "x.effigy" );
			File.WriteAllText( bad, "" );
			Throws<ArgumentException>( () => BotMapPlaytest.Run( bad ), "Map must be" );
		} );
	}
}
