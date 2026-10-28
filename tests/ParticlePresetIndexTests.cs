using System;
using System.Collections.Generic;
using System.Linq;
using Marionette.Desktop;

// Wave-6 lane P69 tests. Group: particle-preset-index.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   ParticlePresetIndexTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line.
//
// Counts pinned here are measured from the index itself at this lane's
// check time, when all four sources were present: 14 built-ins + 8 pack B
// + 8 pack C + 8 2D = 38 entries. Search counts: "water" 13 (name, tag,
// or description — PackC's ink/caustic/oil descriptions say "in water"
// even though their tags do not), "swirling" 2, "spark" 4 (sparks,
// magic_sparkle, caustic_motes via its "sparkles" description, 2d:sparks),
// "glow" 8 (sparks, magic_sparkle, galaxy, plasma, caustic_motes, lava,
// 2d:sparks, 2d:magic). If a pack lands or leaves, these counts move with Build()
// and the failing test names the new count — that is the point.

internal static class ParticlePresetIndexTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Throws( Action action, string reason )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( reason );
		}
		string[] Names( IReadOnlyList<PresetIndexEntry> entries ) => entries.Select( e => e.Name ).ToArray();

		registry.Add( "particle-preset-index", "all 14 built-ins present with BuiltIn source", () =>
		{
			var built = ParticlePresetIndex.Build().Where( e => e.Source == PresetSource.BuiltIn ).ToList();
			Check( built.Count == 14, $"expected 14 built-ins, got {built.Count}" );
			foreach ( var name in ParticleEmitterPresets.Names )
				Check( built.Any( e => e.Name == name ), $"built-in '{name}' missing from index" );
			Check( built.Count == ParticleEmitterPresets.Names.Length, "built-in count differs from ParticleEmitterPresets.Names" );
		} );

		registry.Add( "particle-preset-index", "all four sources found, 8 + 8 + 8 beyond the built-ins, 38 total", () =>
		{
			var found = ParticlePresetIndex.SourcesFound();
			Check( found.SequenceEqual( new[] { PresetSource.BuiltIn, PresetSource.PackB, PresetSource.PackC, PresetSource.BuiltIn2D } ),
				$"expected sources BuiltIn, PackB, PackC, BuiltIn2D, got {string.Join( ", ", found )}" );
			var all = ParticlePresetIndex.Build();
			Check( all.Count == 38, $"expected 38 entries, got {all.Count}" );
			Check( all.Count( e => e.Source == PresetSource.PackB ) == 8, "pack B count is not 8" );
			Check( all.Count( e => e.Source == PresetSource.PackC ) == 8, "pack C count is not 8" );
			Check( all.Count( e => e.Source == PresetSource.BuiltIn2D ) == 8, "2D count is not 8" );
			foreach ( var name in ParticlePresetPackB.Names )
				Check( all.Any( e => e.Source == PresetSource.PackB && e.Name == name ), $"pack-B '{name}' missing" );
			foreach ( var name in ParticlePresetPackC.Names )
				Check( all.Any( e => e.Source == PresetSource.PackC && e.Name == name ), $"pack-C '{name}' missing" );
		} );

		registry.Add( "particle-preset-index", "2D entries are namespaced 2d: and never duplicate a 3D name", () =>
		{
			var all = ParticlePresetIndex.Build();
			var twoD = all.Where( e => e.Source == PresetSource.BuiltIn2D ).ToList();
			Check( twoD.Count == 8, $"expected 8 2D entries, got {twoD.Count}" );
			foreach ( var name in Particle2DPresets.Names )
				Check( twoD.Any( e => e.Name == "2d:" + name ), $"2D entry '2d:{name}' missing" );
			Check( twoD.All( e => e.Name.StartsWith( "2d:", StringComparison.Ordinal ) ), "a 2D entry is not 2d:-namespaced" );
			Check( Names( all ).Distinct( StringComparer.Ordinal ).Count() == all.Count, "index names are not unique" );
		} );

		registry.Add( "particle-preset-index", "ordering is alphabetical by name, 2d: first, waterfall last", () =>
		{
			var names = Names( ParticlePresetIndex.Build() );
			for ( int i = 1; i < names.Length; i++ )
				Check( string.CompareOrdinal( names[i - 1], names[i] ) < 0, $"order breaks at '{names[i - 1]}' before '{names[i]}'" );
			Check( names[0] == "2d:bubbles", $"first entry should be 2d:bubbles, got {names[0]}" );
			Check( names[names.Length - 1] == "waterfall", $"last entry should be waterfall, got {names[names.Length - 1]}" );
		} );

		registry.Add( "particle-preset-index", "search finds by tag: swirling gives tornado, galaxy", () =>
		{
			var found = ParticlePresetIndex.Search( "swirling" );
			Check( found.Count == 2, $"expected 2 swirling results, got {found.Count}" );
			Check( Names( found ).SequenceEqual( new[] { "galaxy", "tornado" } ), $"swirling results wrong: {string.Join( ", ", Names( found ) )}" );
		} );

		registry.Add( "particle-preset-index", "search finds by name fragment: spark gives 4", () =>
		{
			var found = ParticlePresetIndex.Search( "spark" );
			Check( found.Count == 4, $"expected 4 spark results, got {found.Count}" );
			Check( Names( found ).SequenceEqual( new[] { "2d:sparks", "caustic_motes", "magic_sparkle", "sparks" } ),
				$"spark results wrong: {string.Join( ", ", Names( found ) )}" );
		} );

		registry.Add( "particle-preset-index", "search finds by name, tag, and description: water gives 13", () =>
		{
			var found = ParticlePresetIndex.Search( "water" );
			Check( found.Count == 13, $"expected 13 water results, got {found.Count}" );
			Check( found.Any( e => e.Name == "ink" ), "water search missed ink (its description says underwater)" );
			Check( found.Any( e => e.Name == "water_splash" ), "water search missed water_splash" );
			Check( found.Any( e => e.Name == "2d:splash" ), "water search missed 2d:splash" );
		} );

		registry.Add( "particle-preset-index", "search is case-insensitive", () =>
		{
			Check( ParticlePresetIndex.Search( "WATER" ).Count == 13, "WATER search did not give 13" );
			Check( ParticlePresetIndex.Search( "SwIrLiNg" ).Count == 2, "SwIrLiNg search did not give 2" );
			Check( Names( ParticlePresetIndex.Search( "GLOW" ) ).SequenceEqual( Names( ParticlePresetIndex.Search( "glow" ) ) ),
				"GLOW and glow searches differ" );
			Check( ParticlePresetIndex.Search( "glow" ).Count == 8, $"expected 8 glow results, got {ParticlePresetIndex.Search( "glow" ).Count}" );
		} );

		registry.Add( "particle-preset-index", "empty query returns everything in documented order", () =>
		{
			var all = ParticlePresetIndex.Build();
			foreach ( var q in new string?[] { "", "   ", null } )
			{
				var found = ParticlePresetIndex.Search( q );
				Check( found.Count == 38, $"empty query gave {found.Count}, not 38" );
				Check( Names( found ).SequenceEqual( Names( all ) ), "empty-query order differs from Build order" );
			}
		} );

		registry.Add( "particle-preset-index", "unknown query returns an empty list, never an exception", () =>
		{
			var found = ParticlePresetIndex.Search( "zzzz_no_such_preset" );
			Check( found.Count == 0, $"unknown query gave {found.Count} results, not 0" );
		} );

		registry.Add( "particle-preset-index", "search preserves documented order on a subset", () =>
		{
			var found = ParticlePresetIndex.Search( "falling" );
			var names = Names( found );
			for ( int i = 1; i < names.Length; i++ )
				Check( string.CompareOrdinal( names[i - 1], names[i] ) < 0, "search subset is not in alphabetical order" );
			Check( found.Count > 0, "falling search unexpectedly empty" );
		} );

		registry.Add( "particle-preset-index", "find uses prefix and whole-word rules", () =>
		{
			Check( ParticlePresetIndex.Find( "fx:fire" )?.Name == "fire", "fx:fire did not find fire" );
			Check( ParticlePresetIndex.Find( "footstep_water_splash" )?.Name == "water_splash", "whole-word cue did not find water_splash" );
			Check( ParticlePresetIndex.Find( "FX:EXPLOSION" )?.Name == "explosion", "FX:EXPLOSION did not find explosion" );
			Check( ParticlePresetIndex.Find( "FX:EXPLOSION" )?.Source == PresetSource.PackB, "explosion source is not PackB" );
		} );

		registry.Add( "particle-preset-index", "find 2d: prefix forces 2D, plain shared name gives 3D", () =>
		{
			var twoD = ParticlePresetIndex.Find( "2d:sparks" );
			Check( twoD?.Name == "2d:sparks" && twoD?.Source == PresetSource.BuiltIn2D, "2d:sparks did not find the 2D entry" );
			Check( ParticlePresetIndex.Find( "2d:fx:sparks" )?.Name == "2d:sparks", "2d:fx:sparks did not find the 2D entry" );
			var plain = ParticlePresetIndex.Find( "sparks" );
			Check( plain?.Name == "sparks" && plain?.Source == PresetSource.BuiltIn, "plain sparks did not resolve to the 3D preset" );
			Check( ParticlePresetIndex.Find( "splash" )?.Name == "2d:splash", "bare splash should resolve to 2d:splash (it is not a whole word of water_splash)" );
		} );

		registry.Add( "particle-preset-index", "find keeps frost_breath and breath_frost distinct", () =>
		{
			Check( ParticlePresetIndex.Find( "frost_breath" )?.Source == PresetSource.PackC, "frost_breath did not resolve to PackC" );
			Check( ParticlePresetIndex.Find( "breath_frost" )?.Source == PresetSource.BuiltIn, "breath_frost did not resolve to BuiltIn" );
		} );

		registry.Add( "particle-preset-index", "find unknown, null, and whitespace names return null", () =>
		{
			Check( ParticlePresetIndex.Find( "zzzz_no_such_preset" ) is null, "unknown name did not return null" );
			Check( ParticlePresetIndex.Find( null ) is null, "null name did not return null" );
			Check( ParticlePresetIndex.Find( "   " ) is null, "whitespace name did not return null" );
		} );

		registry.Add( "particle-preset-index", "TagsFor mirrors Find and returns null for unknown names", () =>
		{
			var tags = ParticlePresetIndex.TagsFor( "fire" );
			Check( tags is not null && tags.Contains( "fire" ) && tags.Contains( "rising" ), "TagsFor(fire) wrong" );
			Check( ParticlePresetIndex.TagsFor( "2d:magic" ) is { } magic && magic.Contains( "light" ) && magic.Contains( "glow" ), "TagsFor(2d:magic) wrong" );
			Check( ParticlePresetIndex.TagsFor( "zzzz_no_such_preset" ) is null, "TagsFor(unknown) did not return null" );
			Check( ParticlePresetIndex.TagsFor( null ) is null, "TagsFor(null) did not return null" );
		} );

		registry.Add( "particle-preset-index", "validate rejects a synthetic duplicate name", () =>
		{
			var entries = new[]
			{
				new PresetIndexEntry( "fire", PresetSource.BuiltIn, new[] { "fire" }, "A fire preset." ),
				new PresetIndexEntry( "fire", PresetSource.PackB, new[] { "fire" }, "A colliding fire preset." ),
			};
			Throws( () => ParticlePresetIndex.BuildFrom( entries ), "duplicate name was not rejected" );
			Throws( () => ParticlePresetIndex.Validate( entries ), "Validate did not reject a duplicate name" );
		} );

		registry.Add( "particle-preset-index", "validate rejects null/empty names and null tag lists", () =>
		{
			Throws( () => ParticlePresetIndex.BuildFrom( new[] { new PresetIndexEntry( "", PresetSource.BuiltIn, new[] { "fire" }, "No name." ) } ),
				"empty name was not rejected" );
			Throws( () => ParticlePresetIndex.BuildFrom( new[] { new PresetIndexEntry( "fire", PresetSource.BuiltIn, null!, "No tags." ) } ),
				"null tag list was not rejected" );
			Throws( () => ParticlePresetIndex.BuildFrom( new[] { new PresetIndexEntry( "fire", PresetSource.BuiltIn, new[] { "fire" }, "" ) } ),
				"empty description was not rejected" );
		} );

		registry.Add( "particle-preset-index", "Build twice gives identical results, and tags stay in the fixed vocabulary", () =>
		{
			var first = ParticlePresetIndex.Build();
			var second = ParticlePresetIndex.Build();
			Check( first.Count == second.Count, "two builds differ in count" );
			// Field-wise, not record ==: a record's Tags array compares by
			// reference, so two identical builds are only equal field by field.
			for ( int i = 0; i < first.Count; i++ )
				Check( first[i].Name == second[i].Name && first[i].Source == second[i].Source
					&& first[i].Description == second[i].Description && first[i].Tags.SequenceEqual( second[i].Tags ),
					$"two builds differ at index {i}" );
			var vocab = new HashSet<string>( ParticlePresetIndex.TagVocabulary, StringComparer.Ordinal );
			foreach ( var entry in first )
				foreach ( var tag in entry.Tags )
					Check( vocab.Contains( tag ), $"entry '{entry.Name}' uses tag '{tag}' outside the fixed vocabulary" );
			Check( ParticlePresetIndex.TagVocabulary.Length == 24, $"expected 24 vocabulary tags, got {ParticlePresetIndex.TagVocabulary.Length}" );
		} );
	}
}
