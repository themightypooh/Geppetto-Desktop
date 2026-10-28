using System.Diagnostics;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// G11: the engine-free control registry and search index (core/ControlRegistry.cs).
/// CPU only. Run just these with:
///   dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter control-registry
/// </summary>
internal static class ControlRegistryTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "control-registry", name, test );
		void Assert( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		string Top( string query, int n = 5 ) =>
			string.Join( ", ", ControlRegistry.Shared.Search( query, n ).Select( h => $"{h.Id}={h.Score:0.0}" ) );

		Check( "feature count and names match ApiCatalog", () =>
		{
			var reg = ControlRegistry.Shared;
			var catalog = ApiCatalog.FeatureNames();
			Assert( reg.Features.Count == catalog.Count, $"registry has {reg.Features.Count} features, catalog has {catalog.Count}" );
			Assert( reg.Features.Select( f => f.Name ).SequenceEqual( catalog ), "registry feature names/order differ from ApiCatalog.FeatureNames()" );
			Assert( catalog.Count >= 100, $"catalog unexpectedly small: {catalog.Count}" );
		} );

		Check( "every catalog parameter is indexed", () =>
		{
			var reg = ControlRegistry.Shared;
			foreach ( var name in ApiCatalog.FeatureNames() )
			{
				var feature = BuildScript.CreateFeature( name );
				Assert( feature is not null, $"catalog feature {name} cannot be created" );
				var expected = feature.Parameters.Concat( feature.AdvancedParameters ).Distinct().Count();
				var entry = reg.Feature( name );
				Assert( entry is not null, $"registry missing {name}" );
				var got = entry.Params.Count( p => p.Source == ControlSource.FeatureParameter );
				Assert( got == expected, $"{name}: {got} indexed parameters, kernel has {expected}" );
				Assert( entry.Params.Select( p => p.Id ).Distinct().Count() == entry.Params.Count, $"{name}: duplicate param ids" );
			}
			Assert( reg.FeatureParamCount > 1000, $"only {reg.FeatureParamCount} feature params indexed" );
		} );

		Check( "ranges, defaults and options come from the kernel", () =>
		{
			var reg = ControlRegistry.Shared;
			var waist = reg.Param( "DraftPanels.waist" );
			Assert( waist is not null && waist.Kind == ControlKind.Number && waist.Min == 0 && waist.Max == 200 && waist.Default == "0", $"DraftPanels.waist wrong: {waist}" );
			var preset = reg.Param( "Eye.preset" );
			Assert( preset is not null && preset.Kind == ControlKind.Choice && preset.Options.Contains( "Cat" ) && preset.Default == "Human", $"Eye.preset wrong: {preset}" );
			var slot = reg.Param( "Eye.irisslot" );
			Assert( slot is not null && slot.Kind == ControlKind.Integer && slot.Group == "Material slots", $"Eye.irisslot wrong: {slot} group {slot?.Group}" );
			var rough = reg.Param( "Material.RoughnessFactor" );
			Assert( rough is not null && rough.Default == "0.5", $"Material.RoughnessFactor wrong: {rough}" );
		} );

		Check( "every label and tooltip is marked draft", () =>
		{
			var reg = ControlRegistry.Shared;
			foreach ( var f in reg.Features.Append( reg.Material ) )
			{
				Assert( f.IsDraft && f.DraftSource.StartsWith( "DRAFT" ) && !string.IsNullOrWhiteSpace( f.DraftLabel ) && !string.IsNullOrWhiteSpace( f.Category ), $"{f.Name} not a complete draft" );
				foreach ( var p in f.Params )
					Assert( p.IsDraft && p.DraftSource.StartsWith( "DRAFT" ) && !string.IsNullOrWhiteSpace( p.DraftLabel ) && !string.IsNullOrWhiteSpace( p.DraftTooltip ) && !string.IsNullOrWhiteSpace( p.Group ), $"{p.Id} not a complete draft" );
			}
		} );

		Check( "search waist finds torso waist params", () =>
		{
			var hits = ControlRegistry.Shared.Search( "waist", 5 );
			Assert( hits.Count > 0 && hits[0].Id == "DraftPanels.waist", "waist top hits: " + Top( "waist" ) );
			Assert( hits.Take( 3 ).Any( h => h.Id == "DraftPanels.waistease" ), "waist ease not in top 3: " + Top( "waist" ) );
		} );

		Check( "search roughness finds material params", () =>
		{
			var hits = ControlRegistry.Shared.Search( "roughness", 8 );
			Assert( hits.Count > 0 && hits[0].Param is not null && hits[0].Param.Key.Contains( "roughness" ), "roughness top hits: " + Top( "roughness", 8 ) );
			Assert( hits.Any( h => h.Id == "Paint.roughness" ), "Paint.roughness missing: " + Top( "roughness", 8 ) );
			Assert( hits.Any( h => h.Id == "Material.RoughnessFactor" ), "Material.RoughnessFactor missing: " + Top( "roughness", 8 ) );
		} );

		Check( "search eye finds the Eye feature first", () =>
		{
			var hits = ControlRegistry.Shared.Search( "eye", 8 );
			Assert( hits.Count > 0 && hits[0].Kind == ControlHitKind.Feature && hits[0].Id == "Eye", "eye top hits: " + Top( "eye", 8 ) );
			Assert( hits.Take( 8 ).Count( h => h.Feature?.Name == "Eye" ) >= 4, "eye: too few Eye entries near the top: " + Top( "eye", 8 ) );
		} );

		Check( "search hair finds Hair then fur", () =>
		{
			var hits = ControlRegistry.Shared.Search( "hair", 10 );
			Assert( hits.Count > 0 && hits[0].Id == "Hair", "hair top hits: " + Top( "hair", 10 ) );
			Assert( hits.Any( h => h.Id == "Fur" ), "hair synonym fur missing: " + Top( "hair", 10 ) );
		} );

		Check( "multi-word and synonym queries rank sensibly", () =>
		{
			var iris = ControlRegistry.Shared.Search( "iris colour", 3 );
			Assert( iris.Count > 0 && iris[0].Id == "Eye.iriscolor", "iris colour top hits: " + Top( "iris colour", 3 ) );
			var gloss = ControlRegistry.Shared.Search( "gloss", 10 );
			Assert( gloss.Any( h => h.Param?.Key.Contains( "roughness" ) == true ), "gloss should reach roughness via synonyms: " + Top( "gloss", 10 ) );
			Assert( ControlRegistry.Shared.Search( "   " ).Count == 0, "blank query should return nothing" );
			Assert( ControlRegistry.Shared.Search( "zzqqxx" ).Count == 0, "nonsense query should return nothing" );
		} );

		Check( "search is fast", () =>
		{
			var reg = ControlRegistry.Shared;
			reg.Search( "warm up" );
			var clock = Stopwatch.StartNew();
			string[] queries = { "waist", "roughness", "eye", "hair", "iris colour", "material slot", "gloss", "seed" };
			for ( int i = 0; i < 50; i++ )
				foreach ( var q in queries ) reg.Search( q );
			double perQuery = clock.Elapsed.TotalMilliseconds / ( 50 * queries.Length );
			Assert( perQuery < 5, $"search took {perQuery:0.000} ms per query" );
			Console.WriteLine( $"  control-registry: {reg.Features.Count} features, {reg.FeatureParamCount} feature params, {reg.Material.Params.Count} material params, {perQuery:0.000} ms/query" );
			foreach ( var q in new[] { "waist", "roughness", "eye", "hair" } )
				Console.WriteLine( $"  search '{q}': {Top( q )}" );
		} );
	}
}
