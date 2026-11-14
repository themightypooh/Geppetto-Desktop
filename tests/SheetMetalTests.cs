#nullable enable
using System;
using System.Linq;
using Effigy;

internal static class SheetMetalTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		// Fixture for port-sheet-metal-feature-and-assess-exact-cad-exchange-gaps.
		// Mirrors the Mobile acceptance checks in geppetto-mobile/app/CadOpsChecks.cs
		// ("Sheet metal: base flange with bent edge flanges, real bend radius, flat
		// pattern by K-factor"), same defaults, same numbers, so a Desktop pass means
		// the same part Mobile builds.
		//
		// Honesty note: the body this builds is a closed MESH solid (bend arcs are
		// faceted in pi/16 steps by SheetMetalFeature.Bend), not a B-rep solid.
		// STEP/IGES export must treat it as a faceted shell until a SheetMetal
		// OCCT replay exists; that is a separate ticket, not this one.
		//
		// Not yet registered in tests/TestRegistry.cs: that file is
		// coordinator-owned and the final integrator adds the one
		// SheetMetalTests.Register(registry, settings); line.
		registry.Add( "sheetmetal", "default U builds one solid", () =>
		{
			var studio = new PartStudio();
			studio.Add( new SheetMetalFeature() );
			studio.Rebuild();
			Assert( studio.Bodies.Count == 1, $"expected 1 body, got {studio.Bodies.Count}" );
			var bent = studio.Bodies[0].Mesh;
			Assert( bent.SignedVolume() > 0, "bent sheet must have positive volume" );
		} );

		registry.Add( "sheetmetal", "flanges stand up flange plus bend tall", () =>
		{
			var studio = new PartStudio();
			var sheet = studio.Add( new SheetMetalFeature() );
			studio.Rebuild();
			var bent = studio.Bodies[0].Mesh;
			float t = sheet.Thickness.Value, r = sheet.BendRadius.Value;
			float minZ = bent.Positions.Min( q => q.z ), maxZ = bent.Positions.Max( q => q.z );
			Assert( MathF.Abs( minZ ) < 1e-4f, $"base underside must sit on z=0, minZ={minZ}" );
			Assert( MathF.Abs( maxZ - (1.5f + r + t) ) < 1e-3f, $"flange height must be 1.5+r+t, maxZ={maxZ}" );
		} );

		registry.Add( "sheetmetal", "U width is base plus two outside bends", () =>
		{
			var studio = new PartStudio();
			var sheet = studio.Add( new SheetMetalFeature() );
			studio.Rebuild();
			var bent = studio.Bodies[0].Mesh;
			float t = sheet.Thickness.Value, r = sheet.BendRadius.Value;
			float minX = bent.Positions.Min( q => q.x ), maxX = bent.Positions.Max( q => q.x );
			Assert( MathF.Abs( maxX - minX - (4f + 2 * (r + t)) ) < 1e-3f,
				$"U width must be base plus two outside bends, span={maxX - minX}" );
		} );

		registry.Add( "sheetmetal", "bent volume matches sheet area times thickness", () =>
		{
			var studio = new PartStudio();
			var sheet = studio.Add( new SheetMetalFeature() );
			studio.Rebuild();
			var bent = studio.Bodies[0].Mesh;
			float t = sheet.Thickness.Value, r = sheet.BendRadius.Value;
			// Volume of sheet = thickness x mid-surface length x width (exact for the
			// arcs too, to faceting).
			float midLen = 4f + 3f + 2 * (MathF.PI / 2 * (r + t / 2));
			float expected = t * midLen * 4f;
			Assert( MathF.Abs( bent.SignedVolume() - expected ) < 0.01f * expected,
				$"bent volume {bent.SignedVolume()} must match sheet area x thickness {expected}" );
		} );

		registry.Add( "sheetmetal", "flat length uses bend allowance", () =>
		{
			var studio = new PartStudio();
			var sheet = studio.Add( new SheetMetalFeature() );
			float t = sheet.Thickness.Value, r = sheet.BendRadius.Value;
			float ba = SheetMetalFeature.BendAllowance( 90, r, sheet.KFactor.Value, t );
			Assert( MathF.Abs( sheet.FlatLength - (4f + 3f + 2 * ba) ) < 1e-4f,
				$"flat length {sheet.FlatLength} must be base + flanges + 2 bend allowances" );
		} );

		registry.Add( "sheetmetal", "flat pattern lays the blank out flat", () =>
		{
			var studio = new PartStudio();
			var sheet = studio.Add( new SheetMetalFeature() );
			sheet.FlatPattern.Value = true;
			studio.MarkAllDirty();
			studio.Rebuild();
			var flat = studio.Bodies[0].Mesh;
			float t = sheet.Thickness.Value;
			Assert( MathF.Abs( flat.Positions.Max( q => q.z ) - t ) < 1e-4f,
				"flat blank must be exactly one thickness tall" );
			Assert( MathF.Abs( flat.Positions.Max( q => q.x ) - flat.Positions.Min( q => q.x ) - sheet.FlatLength ) < 1e-3f,
				"flat blank x-extent must equal FlatLength" );
			Assert( flat.SignedVolume() > 0, "flat blank must still be a solid" );
		} );

		registry.Add( "sheetmetal", "single 45 degree flange builds", () =>
		{
			var studio = new PartStudio();
			var sheet = studio.Add( new SheetMetalFeature() );
			sheet.FlangeB.Value = 0;
			sheet.AngleA.Value = 45;
			studio.Rebuild();
			Assert( studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.SignedVolume() > 0,
				"single 45 degree flange must build one solid" );
		} );

		registry.Add( "sheetmetal", "sheet metal saves and opens", () =>
		{
			var studio = new PartStudio();
			var sheet = studio.Add( new SheetMetalFeature() );
			sheet.FlangeB.Value = 0;
			sheet.AngleA.Value = 45;
			studio.Rebuild();
			var round = StudioDocument.Read( StudioDocument.Write( studio ) );
			round.Rebuild();
			var back = round.Features.OfType<SheetMetalFeature>().FirstOrDefault();
			Assert( back is not null, "reopened document must still have the sheet metal feature" );
			Assert( back!.AngleA.Value == 45 && back.FlangeB.Value == 0,
				$"reopened params must survive (AngleA={back.AngleA.Value}, FlangeB={back.FlangeB.Value})" );
			Assert( round.Bodies.Count == 1, $"reopened document must rebuild one body, got {round.Bodies.Count}" );
		} );

		registry.Add( "sheetmetal", "no flanges builds a flat plate", () =>
		{
			var studio = new PartStudio();
			var sheet = studio.Add( new SheetMetalFeature() );
			sheet.FlangeA.Value = 0;
			sheet.FlangeB.Value = 0;
			studio.Rebuild();
			Assert( studio.Bodies.Count == 1, "flangeless sheet must still build one body" );
			var plate = studio.Bodies[0].Mesh;
			Assert( MathF.Abs( sheet.FlatLength - sheet.BaseLength.Value ) < 1e-4f,
				"flat length with no bends must be the base length" );
			Assert( MathF.Abs( plate.Positions.Max( q => q.z ) - sheet.Thickness.Value ) < 1e-4f,
				"flangeless sheet must be one thickness tall" );
			Assert( plate.SignedVolume() > 0, "flat plate must have positive volume" );
		} );

		registry.Add( "sheetmetal", "bend allowance is zero at zero angle", () =>
		{
			float ba = SheetMetalFeature.BendAllowance( 0, 0.06f, 0.44f, 0.06f );
			Assert( ba == 0f, $"zero-angle bend must use no sheet, got {ba}" );
			float quarter = SheetMetalFeature.BendAllowance( 90, 0.06f, 0.44f, 0.06f );
			Assert( quarter > 0f && MathF.Abs( quarter - MathF.PI / 2 * (0.06f + 0.44f * 0.06f) ) < 1e-6f,
				"90 degree bend allowance must be angle * (R + K*T)" );
		} );
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}
}
