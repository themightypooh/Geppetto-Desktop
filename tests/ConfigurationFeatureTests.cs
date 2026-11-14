using System;
using System.Linq;
using Effigy;

internal static class ConfigurationFeatureTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "cad-configurations", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception e ) { throw new Exception( $"expected {typeof( T ).Name}, got {e.GetType().Name}: {e.Message}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}

		(PartStudio Studio, PrimitiveFeature Cylinder, FilletFeature Fillet, HoleFeature Hole, ConfigurationFeature Configs) MakeStudio()
		{
			var studio = new PartStudio();
			var cylinder = studio.Add( new PrimitiveFeature() );
			cylinder.Shape.Index = 1; // Cylinder
			cylinder.Radius.Value = 0.5f;
			cylinder.SizeZ.Value = 1f;
			var fillet = studio.Add( new FilletFeature() );
			var hole = studio.Add( new HoleFeature() );
			var configs = studio.Add( new ConfigurationFeature() );
			configs.AddConfiguration( new DesignConfiguration
			{
				Name = "small",
				Overrides =
				{
					new ConfigurationOverride { FeatureId = cylinder.Id, Kind = ConfigurationOverrideKind.Param, Label = "Radius", Value = "0.5" },
					new ConfigurationOverride { FeatureId = fillet.Id, Kind = ConfigurationOverrideKind.Suppressed, Value = "0" },
					new ConfigurationOverride { FeatureId = cylinder.Id, Kind = ConfigurationOverrideKind.Material, Value = "0" },
				},
			} );
			configs.AddConfiguration( new DesignConfiguration
			{
				Name = "Large 2x",
				Overrides =
				{
					new ConfigurationOverride { FeatureId = cylinder.Id, Kind = ConfigurationOverrideKind.Param, Label = "Radius", Value = "1" },
					new ConfigurationOverride { FeatureId = fillet.Id, Kind = ConfigurationOverrideKind.Suppressed, Value = "1" },
					new ConfigurationOverride { FeatureId = cylinder.Id, Kind = ConfigurationOverrideKind.Material, Value = "3" },
				},
			} );
			configs.AddConfiguration( new DesignConfiguration
			{
				Name = "hole-big",
				Overrides =
				{
					new ConfigurationOverride { FeatureId = hole.Id, Kind = ConfigurationOverrideKind.Param, Label = "Diameter", Value = "0.5" },
				},
			} );
			return (studio, cylinder, fillet, hole, configs);
		}

		float MaxRadius( PartStudio studio )
		{
			var body = studio.Bodies.FirstOrDefault()
				?? throw new Exception( "no bodies after rebuild" );
			var max = 0f;
			foreach ( var p in body.Mesh.Positions )
				max = MathF.Max( max, MathF.Sqrt( p.x * p.x + p.y * p.y ) );
			return max;
		}

		Check( "two configs rebuild to the right geometry under each config", () =>
		{
			var (studio, cylinder, fillet, _, configs) = MakeStudio();
			var small = configs.SetActive( studio, "small" );
			Require( small.FeaturesSuppressed == 0, $"small suppressed {small.FeaturesSuppressed}" );
			Require( cylinder.Radius.Value == 0.5f && cylinder.Material.Value == 0, "small values not applied" );
			Require( !fillet.Suppressed, "fillet should run under small" );
			Require( MathF.Abs( MaxRadius( studio ) - 0.5f ) < 0.01f, $"small body radius {MaxRadius( studio )}" );

			var large = configs.SetActive( studio, "Large 2x" );
			Require( large.FeaturesSuppressed == 1, $"large suppressed {large.FeaturesSuppressed}" );
			Require( cylinder.Radius.Value == 1f && cylinder.Material.Value == 3, "large values not applied" );
			Require( fillet.Suppressed, "fillet should be suppressed under large" );
			Require( MathF.Abs( MaxRadius( studio ) - 1f ) < 0.01f, $"large body radius {MaxRadius( studio )}" );
			Require( configs.Active.Value == "Large 2x", "active name not recorded" );
		} );

		Check( "switching back restores the first config exactly", () =>
		{
			var (studio, cylinder, fillet, _, configs) = MakeStudio();
			configs.SetActive( studio, "Large 2x" );
			configs.SetActive( studio, "small" );
			Require( cylinder.Radius.Value == 0.5f, $"radius {cylinder.Radius.Value}" );
			Require( cylinder.Material.Value == 0, $"material slot {cylinder.Material.Value}" );
			Require( !fillet.Suppressed, "fillet suppression not restored" );
			Require( MathF.Abs( MaxRadius( studio ) - 0.5f ) < 0.01f, $"body radius {MaxRadius( studio )}" );
		} );

		Check( "clearing the active config restores the base values", () =>
		{
			var (studio, cylinder, fillet, _, configs) = MakeStudio();
			configs.SetActive( studio, "Large 2x" );
			configs.ClearActive( studio );
			Require( cylinder.Radius.Value == 0.5f && cylinder.Material.Value == 0, "base values not restored" );
			Require( !fillet.Suppressed, "base suppression not restored" );
			Require( configs.Active.Value == "", "active name not cleared" );
		} );

		Check( "a config naming a deleted feature is refused and applies nothing", () =>
		{
			var (studio, cylinder, fillet, _, configs) = MakeStudio();
			configs.SetActive( studio, "small" );
			studio.Remove( fillet );
			Throws<InvalidOperationException>( () => configs.SetActive( studio, "Large 2x" ) );
			Require( cylinder.Radius.Value == 0.5f, $"partial application moved radius to {cylinder.Radius.Value}" );
			Require( cylinder.Material.Value == 0, "partial application moved the material slot" );
			Require( configs.Active.Value == "small", "failed switch moved the active name" );
			Require( configs.Validate( studio ).Count > 0, "Validate did not report the deleted feature" );
		} );

		Check( "a hole diameter configures at the parameter level", () =>
		{
			var (studio, _, _, hole, configs) = MakeStudio();
			var report = configs.SetActive( studio, "hole-big" );
			Require( hole.Diameter.Value == 0.5f, $"hole diameter {hole.Diameter.Value}" );
			// No faces are picked, so the hole fails honestly instead of drilling — the
			// configuration still applied, and the rebuild reports the failure rather than
			// crashing. (Drilling itself needs the boolean provider; see HoleFeature.)
			Require( report.Errors.Any( e => e.FeatureId == hole.Id ), "hole failure not reported" );
		} );

		Check( "configurations survive save and reopen, and the document stamps format 4", () =>
		{
			var (studio, _, _, _, configs) = MakeStudio();
			configs.SetActive( studio, "small" );
			var text = StudioDocument.Write( studio );
			Require( text.StartsWith( "effigy 4\n", StringComparison.Ordinal ), "config document did not stamp format 4" );

			var reopened = StudioDocument.Read( text );
			var reopenedConfigs = reopened.Features.OfType<ConfigurationFeature>().Single();
			Require( reopenedConfigs.FindConfiguration( "small" ) is not null, "small config lost" );
			Require( reopenedConfigs.FindConfiguration( "Large 2x" ) is not null, "config name with a space lost" );
			Require( reopenedConfigs.Active.Value == "small", "active name lost" );

			var cylinder = reopened.Features.OfType<PrimitiveFeature>().Single();
			reopenedConfigs.SetActive( reopened, "Large 2x" );
			Require( cylinder.Radius.Value == 1f, $"reopened large radius {cylinder.Radius.Value}" );

			var plain = new PartStudio();
			plain.Add( new PrimitiveFeature() );
			Require( StudioDocument.Write( plain ).StartsWith( "effigy 1\n", StringComparison.Ordinal ),
				"a document without configurations must stay format 1" );
		} );

		Check( "invalid configurations are refused", () =>
		{
			var (_, _, _, _, configs) = MakeStudio();
			Throws<ArgumentException>( () => configs.AddConfiguration( new DesignConfiguration { Name = "small" } ) );
			Throws<ArgumentException>( () => configs.AddConfiguration( new DesignConfiguration
			{
				Name = "bad",
				Overrides = { new ConfigurationOverride { FeatureId = "x", Kind = ConfigurationOverrideKind.Suppressed, Value = "maybe" } },
			} ) );
			var (studio, _, _, _, configs2) = MakeStudio();
			Throws<ArgumentException>( () => configs2.SetActive( studio, "nope" ) );
			// Validate reports problems without throwing: against an empty studio every
			// override names a missing feature.
			Require( configs.Validate( new PartStudio() ).Count > 0, "Validate did not report missing features" );
		} );
	}
}
