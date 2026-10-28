#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Desktop;

/// <summary>Which preset collection an index entry came from. The source is
/// the class that owns the preset, not a marketing label: BuiltIn is
/// <see cref="ParticleEmitterPresets"/>, PackB is
/// <see cref="ParticlePresetPackB"/>, PackC is
/// <see cref="ParticlePresetPackC"/>, BuiltIn2D is
/// <see cref="Particle2DPresets"/>.</summary>
public enum PresetSource { BuiltIn, PackB, PackC, BuiltIn2D }

/// <summary>One entry in the preset index: the canonical preset name, the
/// source that owns it, a small fixed-vocabulary tag set, and a one-line
/// description copied or paraphrased from the preset's own documented
/// look. Descriptions never invent behaviour the preset does not have;
/// PackC descriptions are <see cref="ParticlePresetPackC.DescriptionFor"/>
/// verbatim, the rest are paraphrases of the preset's Create/SettingsFor
/// comments and numbers.</summary>
public sealed record PresetIndexEntry( string Name, PresetSource Source, string[] Tags, string Description );

/// <summary>Wave-6 lane P69: a pure-data index over every particle preset
/// that is actually present in the tree, plus search functions. It defines
/// no presets, edits no preset class, and never resolves a preset into
/// sim settings (<see cref="ParticleEmitterPresets.SettingsFor"/> already
/// does that); it only answers "what presets exist, what are they tagged,
/// and how do I find one by name".
///
/// Presence rule. Build() enumerates each source class's own Names list
/// at runtime — it never carries a hardcoded list of pack names. The
/// tag/description tables below are consulted only for names a source
/// actually lists, so a pack whose file is absent contributes no entries
/// (its block in Build() goes away with the file; the tables alone can
/// never resurrect it). SourcesFound() reports which sources were found
/// at the check time; at this lane's check time all four were present.
///
/// 2D namespacing. The 2D presets (lane 1) share five names with the 3D
/// presets (sparks, rain, snow, bubbles, confetti), so a bare name cannot
/// be the index key for both. 3D entries keep the plain preset name;
/// 2D entries are namespaced as "2d:&lt;name&gt;" (for example
/// "2d:sparks") with Source BuiltIn2D. That keeps every Name unique, so
/// Validate() can reject real duplicates instead of hiding a 2D/3D
/// collision behind one. 2D entries are 2D-canvas presets (Y-down
/// pixels, seconds); 3D entries are kernel inches, Z-up, seconds.
///
/// Ordering. Every list this class returns — Build(), Search(), and
/// BuildFrom() — is sorted alphabetically by Name with ordinal
/// comparison. Because '2' sorts before 'a', the 2d: entries come first
/// and the last entry is "waterfall" (water_splash sorts before it,
/// since '_' sorts before 'f'). The order is stable and deterministic;
/// there is no System.Random anywhere in this file. One record caveat:
/// PresetIndexEntry's Tags array compares by reference under record
/// equality, so "Build() twice gives identical results" means identical
/// field by field (name, source, description, tag sequence), which is
/// what the tests assert — not record == on the array member.
///
/// Search. Search(query) is a case-insensitive substring match over
/// name + tags + description. An empty, whitespace-only, or null query
/// returns every entry; a query that matches nothing returns an empty
/// list, never an exception.
///
/// Find. Find(name) resolves with the same prefix/word rules as the
/// preset resolvers: trim, lowercase, strip one fx:/fx_/vfx:/vfx_/
/// particle:/particle_ prefix, fold '-', ':', space, '/' to '_', then
/// match a preset name exactly or as a whole underscore-separated word
/// (so "footstep_water_splash" finds water_splash, but bare "splash"
/// does not — it is not a whole word of water_splash). Sources are
/// tried in order BuiltIn, PackB, PackC, then BuiltIn2D, so a plain
/// name shared by 3D and 2D resolves to the 3D preset; prefixing with
/// "2d:" or "2d_" forces the 2D preset ("2d:sparks" finds 2d:sparks,
/// "2d:fx:sparks" also works). An unknown, null, or whitespace-only
/// name returns null, never an exception. TagsFor(name) mirrors Find:
/// it returns a copy of the found entry's tags, or null when Find
/// returns null.
///
/// Validate. Validate(entries) loudly rejects: a null entry, a
/// null/empty name, a null tag list, a null/empty description, and any
/// duplicate Name. A pack name colliding with a built-in name is
/// therefore an error the index reports (Build() throws), not a
/// collision it silently hides. BuildFrom(entries) is the same
/// sort + validate pipeline over a caller-supplied list, so tests can
/// exercise Validate() with synthetic duplicates without touching a
/// real preset class.
///
/// Tag vocabulary (fixed — do not invent per-preset free text; every
/// tag below is used by at least one entry): air, ash, blood, burst,
/// cold, colour, drifting, dust, earth, falling, fire, glow, heavy,
/// ink, light, loop, mist, oil, rising, slime, soft, spreading,
/// swirling, water. Meanings are plain: burst = fires once, loop =
/// continuous rate, rising/falling = dominant travel direction,
/// drifting = slow wandering motion, swirling = vortex-driven,
/// spreading = radial/outward, glow = bright/glowing look, mist =
/// fine hanging vapour, soft = slow and diffuse, heavy = sinks or
/// sticks rather than drifting.
///
/// Out of scope, stated plainly: no new presets, no settings
/// resolution, no cue/UI wiring, no save or serialization beyond this
/// in-memory index, no game loop, play mode, live physics, input, or
/// networking. This index stands alone; it needs no hookup line beyond
/// its own test registration (see the test file header).</summary>
public static class ParticlePresetIndex
{
	/// <summary>The fixed tag vocabulary. Every tag used by any entry
	/// appears here, and no entry uses a tag outside this list.</summary>
	public static readonly string[] TagVocabulary =
		{ "air", "ash", "blood", "burst", "cold", "colour", "drifting", "dust", "earth",
		  "falling", "fire", "glow", "heavy", "ink", "light", "loop", "mist", "oil",
		  "rising", "slime", "soft", "spreading", "swirling", "water" };

	/// <summary>Build the index from every preset source actually present
	/// in the tree, sorted alphabetically by Name and validated. Throws
	/// (via Validate) if two sources ever claim the same canonical name.</summary>
	public static IReadOnlyList<PresetIndexEntry> Build()
	{
		var entries = new List<PresetIndexEntry>();
		AddSource( entries, PresetSource.BuiltIn, ParticleEmitterPresets.Names, name => name );
		AddSource( entries, PresetSource.PackB, ParticlePresetPackB.Names, name => name );
		AddSource( entries, PresetSource.PackC, ParticlePresetPackC.Names, name => name );
		AddSource( entries, PresetSource.BuiltIn2D, Particle2DPresets.Names, name => "2d:" + name );
		return BuildFrom( entries );
	}

	/// <summary>Which sources were found present at build time, in
	/// BuiltIn, PackB, PackC, BuiltIn2D order. A source counts as found
	/// when its Names list is non-empty.</summary>
	public static IReadOnlyList<PresetSource> SourcesFound()
	{
		var found = new List<PresetSource>();
		if ( ParticleEmitterPresets.Names.Length > 0 ) found.Add( PresetSource.BuiltIn );
		if ( ParticlePresetPackB.Names.Length > 0 ) found.Add( PresetSource.PackB );
		if ( ParticlePresetPackC.Names.Length > 0 ) found.Add( PresetSource.PackC );
		if ( Particle2DPresets.Names.Length > 0 ) found.Add( PresetSource.BuiltIn2D );
		return found;
	}

	/// <summary>Sort + validate a caller-supplied entry list and return
	/// it as a read-only list. The test seam for Validate(): synthetic
	/// duplicates and bad entries are rejected here exactly as Build()
	/// would reject real ones.</summary>
	public static IReadOnlyList<PresetIndexEntry> BuildFrom( IEnumerable<PresetIndexEntry> entries )
	{
		ArgumentNullException.ThrowIfNull( entries );
		var list = entries.ToList();
		Validate( list );
		list.Sort( ( a, b ) => string.CompareOrdinal( a.Name, b.Name ) );
		return list;
	}

	/// <summary>Loudly reject a bad index: null entries, null/empty
	/// names, null tag lists, null/empty descriptions, duplicate names.
	/// Throws ArgumentException on the first problem found.</summary>
	public static void Validate( IReadOnlyList<PresetIndexEntry> entries )
	{
		ArgumentNullException.ThrowIfNull( entries );
		var seen = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var entry in entries )
		{
			if ( entry is null ) throw new ArgumentException( "Preset index contains a null entry." );
			if ( string.IsNullOrEmpty( entry.Name ) ) throw new ArgumentException( "Preset index contains an entry with a null or empty name." );
			if ( entry.Tags is null ) throw new ArgumentException( $"Preset index entry '{entry.Name}' has a null tag list." );
			if ( string.IsNullOrEmpty( entry.Description ) ) throw new ArgumentException( $"Preset index entry '{entry.Name}' has a null or empty description." );
			if ( !seen.Add( entry.Name ) ) throw new ArgumentException( $"Duplicate preset name in index: '{entry.Name}'." );
		}
	}

	/// <summary>Case-insensitive substring search over name + tags +
	/// description, over the built index, in documented (alphabetical)
	/// order. Empty/whitespace/null query returns everything; a
	/// no-match query returns an empty list, never an exception.</summary>
	public static IReadOnlyList<PresetIndexEntry> Search( string? query ) => Search( Build(), query );

	/// <summary>Same search over a caller-supplied (already built) list,
	/// preserving its order.</summary>
	public static IReadOnlyList<PresetIndexEntry> Search( IReadOnlyList<PresetIndexEntry> entries, string? query )
	{
		ArgumentNullException.ThrowIfNull( entries );
		if ( string.IsNullOrWhiteSpace( query ) ) return entries.ToList();
		var q = query.Trim().ToLowerInvariant();
		return entries.Where( e =>
			e.Name.ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ||
			e.Tags.Any( t => t.ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ) ||
			e.Description.ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ).ToList();
	}

	/// <summary>Resolve one preset by name with the preset resolvers'
	/// prefix/word rules (see the class note for source precedence and
	/// the 2d: prefix). Returns null for unknown, null, or
	/// whitespace-only names, never an exception.</summary>
	public static PresetIndexEntry? Find( string? name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		var entries = Build();
		var n = name.Trim().ToLowerInvariant();
		var twoDOnly = false;
		if ( n.StartsWith( "2d:", StringComparison.Ordinal ) || n.StartsWith( "2d_", StringComparison.Ordinal ) )
		{
			twoDOnly = true;
			n = n[3..];
		}
		if ( !twoDOnly )
		{
			var key = ParticleEmitterPresets.ResolveName( n )
				?? ParticlePresetPackB.ResolveName( n )
				?? ParticlePresetPackC.ResolveName( n );
			if ( key is not null )
			{
				var source = ParticleEmitterPresets.ResolveName( n ) is not null ? PresetSource.BuiltIn
					: ParticlePresetPackB.ResolveName( n ) is not null ? PresetSource.PackB : PresetSource.PackC;
				return entries.First( e => e.Source == source && e.Name == key );
			}
		}
		var dkey = Particle2DPresets.ResolveName( twoDOnly ? n : name );
		if ( dkey is null ) return null;
		return entries.First( e => e.Source == PresetSource.BuiltIn2D && e.Name == "2d:" + dkey );
	}

	/// <summary>Tags of the preset Find(name) resolves, as a fresh copy;
	/// null when Find returns null.</summary>
	public static string[]? TagsFor( string? name )
	{
		var entry = Find( name );
		return entry?.Tags.ToArray();
	}

	static void AddSource( List<PresetIndexEntry> entries, PresetSource source, string[] names, Func<string, string> indexName )
	{
		foreach ( var name in names )
			entries.Add( new PresetIndexEntry( indexName( name ), source, TagsForSource( source, name ), DescriptionFor( source, name ) ) );
	}

	static string[] TagsForSource( PresetSource source, string name ) => ( source, name ) switch
	{
		( PresetSource.BuiltIn, "water_splash" ) => new[] { "water", "burst" },
		( PresetSource.BuiltIn, "bubbles" ) => new[] { "water", "loop", "rising" },
		( PresetSource.BuiltIn, "blood_mist" ) => new[] { "blood", "burst", "mist" },
		( PresetSource.BuiltIn, "sparks" ) => new[] { "fire", "burst", "glow" },
		( PresetSource.BuiltIn, "dust_motes" ) => new[] { "dust", "loop", "drifting" },
		( PresetSource.BuiltIn, "breath_frost" ) => new[] { "cold", "burst", "mist" },
		( PresetSource.BuiltIn, "fire" ) => new[] { "fire", "burst", "rising" },
		( PresetSource.BuiltIn, "smoke" ) => new[] { "air", "burst", "rising", "drifting", "soft" },
		( PresetSource.BuiltIn, "rain" ) => new[] { "water", "burst", "falling" },
		( PresetSource.BuiltIn, "snow" ) => new[] { "cold", "burst", "falling", "drifting" },
		( PresetSource.BuiltIn, "leaves" ) => new[] { "earth", "burst", "falling", "drifting" },
		( PresetSource.BuiltIn, "confetti" ) => new[] { "colour", "burst", "falling" },
		( PresetSource.BuiltIn, "steam" ) => new[] { "air", "burst", "rising", "soft" },
		( PresetSource.BuiltIn, "magic_sparkle" ) => new[] { "light", "burst", "glow", "drifting" },
		( PresetSource.PackB, "explosion" ) => new[] { "fire", "burst", "spreading" },
		( PresetSource.PackB, "fountain" ) => new[] { "water", "loop", "rising" },
		( PresetSource.PackB, "waterfall" ) => new[] { "water", "loop", "falling" },
		( PresetSource.PackB, "tornado" ) => new[] { "air", "loop", "swirling", "rising" },
		( PresetSource.PackB, "galaxy" ) => new[] { "light", "loop", "swirling", "drifting", "glow" },
		( PresetSource.PackB, "plasma" ) => new[] { "light", "burst", "glow", "spreading" },
		( PresetSource.PackB, "slime" ) => new[] { "slime", "loop", "falling", "heavy" },
		( PresetSource.PackB, "ash" ) => new[] { "ash", "loop", "falling", "drifting" },
		( PresetSource.PackC, "ink" ) => new[] { "ink", "burst", "spreading" },
		( PresetSource.PackC, "plankton" ) => new[] { "water", "loop", "drifting" },
		( PresetSource.PackC, "sand" ) => new[] { "earth", "burst", "falling", "heavy" },
		( PresetSource.PackC, "silt" ) => new[] { "water", "loop", "falling", "drifting" },
		( PresetSource.PackC, "caustic_motes" ) => new[] { "light", "loop", "drifting", "glow" },
		( PresetSource.PackC, "oil" ) => new[] { "oil", "loop", "rising", "heavy" },
		( PresetSource.PackC, "lava" ) => new[] { "fire", "loop", "rising", "glow" },
		( PresetSource.PackC, "frost_breath" ) => new[] { "cold", "burst", "mist" },
		( PresetSource.BuiltIn2D, "sparks" ) => new[] { "fire", "burst", "glow" },
		( PresetSource.BuiltIn2D, "rain" ) => new[] { "water", "loop", "falling" },
		( PresetSource.BuiltIn2D, "snow" ) => new[] { "cold", "loop", "falling", "drifting" },
		( PresetSource.BuiltIn2D, "bubbles" ) => new[] { "water", "loop", "rising" },
		( PresetSource.BuiltIn2D, "confetti" ) => new[] { "colour", "burst", "falling" },
		( PresetSource.BuiltIn2D, "magic" ) => new[] { "light", "loop", "glow", "drifting" },
		( PresetSource.BuiltIn2D, "dust" ) => new[] { "dust", "loop", "drifting" },
		( PresetSource.BuiltIn2D, "splash" ) => new[] { "water", "burst" },
		_ => throw new InvalidOperationException( $"No index tags for preset '{name}' from source {source} — the source listed a name the index does not know." ),
	};

	static string DescriptionFor( PresetSource source, string name )
	{
		if ( source == PresetSource.PackC ) return ParticlePresetPackC.DescriptionFor( name );
		return ( source, name ) switch
		{
			( PresetSource.BuiltIn, "water_splash" ) => "Upward burst of water droplets that falls back under air gravity.",
			( PresetSource.BuiltIn, "bubbles" ) => "Rising bubbles in a sphere volume, looping at a steady rate.",
			( PresetSource.BuiltIn, "blood_mist" ) => "Burst of fine blood mist that hangs and drifts.",
			( PresetSource.BuiltIn, "sparks" ) => "Fast bright spark burst, short-lived.",
			( PresetSource.BuiltIn, "dust_motes" ) => "Slow dust motes drifting in a box volume, looping.",
			( PresetSource.BuiltIn, "breath_frost" ) => "Short forward puff of frosty breath vapour.",
			( PresetSource.BuiltIn, "fire" ) => "Burst of flames rising under buoyant gravity.",
			( PresetSource.BuiltIn, "smoke" ) => "Slow smoke burst rising and drifting.",
			( PresetSource.BuiltIn, "rain" ) => "Fast falling rain burst from a wide box.",
			( PresetSource.BuiltIn, "snow" ) => "Slow drifting snow falling from a wide box.",
			( PresetSource.BuiltIn, "leaves" ) => "Leaves tumbling down, wind-blown.",
			( PresetSource.BuiltIn, "confetti" ) => "Colourful confetti burst tumbling down.",
			( PresetSource.BuiltIn, "steam" ) => "Rising steam burst, soft and slow.",
			( PresetSource.BuiltIn, "magic_sparkle" ) => "Glowing magic sparkles drifting in a sphere volume.",
			( PresetSource.PackB, "explosion" ) => "Radial burst in every direction, fast and short-lived.",
			( PresetSource.PackB, "fountain" ) => "Continuous upward water jet, looping, arcing back under air gravity.",
			( PresetSource.PackB, "waterfall" ) => "Continuous downward sheet of water from a flat area.",
			( PresetSource.PackB, "tornado" ) => "Tall swirling vortex column rising under weak gravity.",
			( PresetSource.PackB, "galaxy" ) => "Slow spiral disc of glowing motes under a gentle vortex.",
			( PresetSource.PackB, "plasma" ) => "Fast jittery burst with strong turbulence, dead in half a second.",
			( PresetSource.PackB, "slime" ) => "Slow heavy drips falling and sticking to the ground.",
			( PresetSource.PackB, "ash" ) => "Slow falling grey motes drifting down in a wide box.",
			( PresetSource.BuiltIn2D, "sparks" ) => "2D fast bright sparks bursting upward on the canvas (Y-down pixels).",
			( PresetSource.BuiltIn2D, "rain" ) => "2D rain falling in slanted streaks across a wide box, looping.",
			( PresetSource.BuiltIn2D, "snow" ) => "2D slow snow drifting down across a wide box, looping.",
			( PresetSource.BuiltIn2D, "bubbles" ) => "2D bubbles rising from a circle volume, looping.",
			( PresetSource.BuiltIn2D, "confetti" ) => "2D colourful confetti burst tumbling down.",
			( PresetSource.BuiltIn2D, "magic" ) => "2D glowing magic motes cycling sprite frames, looping.",
			( PresetSource.BuiltIn2D, "dust" ) => "2D slow dust motes drifting in a box volume, looping.",
			( PresetSource.BuiltIn2D, "splash" ) => "2D splash burst of blue droplets.",
			_ => throw new InvalidOperationException( $"No index description for preset '{name}' from source {source} — the source listed a name the index does not know." ),
		};
	}
}
