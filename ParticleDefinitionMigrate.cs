#nullable enable
using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Marionette.Desktop;

// Particle definition migration (particles wave-6 lane P50, 2026-10-24).
//
// WHAT THIS IS: JSON-in / JSON-out migration for particle definitions in
// lane 5's format family (ParticleAuthoring.cs: versioned JSON, settings +
// emitters + inline mesh geometry, seed always serialized). It is pure
// text transformation: no file I/O inside Migrate itself, no sim, no
// rendering, no validation beyond the version rules below. Strict
// validation of ranges and cross-field rules belongs to P71, and the
// JSON Schema itself belongs to P49 — this file invents neither.
//
// v2 = v1 + EXACTLY THESE TWO ADDITIONS, NOTHING ELSE. No field is
// renamed, removed, retyped, or moved; every v1 field keeps its value
// exactly (numbers keep their raw text, unknown fields are preserved
// wherever they sit — lane 5's forward-compat rule, never strip them).
//   1. Top-level "migratedFrom" (integer, REQUIRED in a v2 document):
//      provenance — the version the document came from. 1 = upgraded
//      from v1 by this migrator, 2 = authored natively at v2. Any other
//      value (0, 3, 99, a string, a float) fails loudly.
//   2. Per-emitter "label" (string, OPTIONAL in a v2 document): a
//      human-readable emitter name for authoring UI. On upgrade it is
//      filled from the emitter's existing "name" if present, else from
//      its existing "preset" if present, else OMITTED — never invented
//      (no empty-string or generated labels). A v2 document missing a
//      label is valid; a v2 document missing "migratedFrom" is not.
//
// BEHAVIOUR:
//   - v1 input -> v2 output: "version" becomes 2, "migratedFrom" is set
//     to 1 (overwriting a stray one a v1 doc may carry), emitter labels
//     are filled as above, everything else is untouched.
//   - v2 input -> returned unchanged (idempotent): migrating twice is
//     migrating once. See SERIALIZER NOTE for the exact sense of
//     "unchanged".
//   - Anything else fails LOUDLY with ArgumentException, never a silent
//     partial output: unknown version (0), newer-than-known version
//     (3, 99, ...), malformed JSON, a missing "version" field, a
//     non-object top level, a non-integer "version", or a v2 document
//     missing/invalid "migratedFrom". (A null input throws
//     ArgumentNullException, an ArgumentException subclass.)
//   - What migration does NOT do, on purpose: it does not check that
//     settings/emitters are well-formed, that presets exist, or that
//     numbers are in range — a v1 doc that lane 5 would reject still
//     migrates to a v2 doc its successor would reject the same way.
//     Version upgrade only; lane 5's Build() decides buildability.
//     In particular this migrator does NOT downgrade v2 back to v1:
//     there is no v2 -> v1 path, because dropping "migratedFrom" and
//     labels would destroy provenance the author may rely on.
//
// SERIALIZER NOTE: output is indented JSON (2 spaces, matching lane 5's
// WriteIndented output). Key order is preserved as parsed, except the
// two v2 additions, which are appended last (top-level "migratedFrom"
// after the document's existing keys, "label" after the emitter's
// existing keys). Composite values are re-emitted indented, so their
// whitespace normalizes; scalar spellings (e.g. 2.50, 0.0166667) are
// preserved verbatim. Strings are written with relaxed escaping (unicode
// kept literal, only JSON-required escapes applied). Consequence for
// idempotence: a v2 document already in this indented form comes back
// byte-for-byte identical; a compact or oddly-spaced v2 document comes
// back semantically identical (same keys, same values, same order),
// differing only in whitespace. Tests prove both.
//
// COMPATIBILITY VERDICT (proven, 2026-10-24): lane 5's Build() does NOT
// accept v2 documents — ParticleSystemDef.Build() and
// ParticleAuthoring.FromJson() both reject any version != 1. A migrated
// v2 document therefore cannot be built until the sweep decides one of:
// (a) Build()/FromJson() accept v2 (and ignore or adopt "label" /
// "migratedFrom"), or (b) the migrator targets a future builder. The
// tests assert the current rejection rather than claiming a build that
// does not run.
//
// P49 SEAM: P49's JSON Schema + validator did not exist in the
// canonical tree at this lane's check time (no ParticleSchema.cs, no
// schema JSON), so no schema content is invented here. The seam is:
// every document this migrator outputs must be accepted by P49's schema
// once it lands — i.e. P49's v2 schema must allow "migratedFrom" and
// per-emitter "label" exactly as defined above, and must still accept
// every v1 field and unknown field this migrator preserves.
public static class ParticleDefinitionMigrate
{
	/// <summary>The version this migrator produces.</summary>
	public const int CurrentVersion = 2;

	/// <summary>The earliest version this migrator upgrades from.</summary>
	public const int FirstVersion = 1;

	static readonly JsonSerializerOptions OutputOptions = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>Migrates one definition document from JSON text to JSON
	/// text. v1 becomes v2; v2 comes back unchanged (see header).
	/// Throws ArgumentException on every malformed or unsupported input
	/// class listed in the header; never returns a partial document.</summary>
	public static string Migrate( string json )
	{
		ArgumentNullException.ThrowIfNull( json );
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "particle definition migration: empty JSON." );
		JsonObject root;
		try
		{
			var node = JsonNode.Parse( json )
				?? throw new ArgumentException( "particle definition migration: JSON parsed to null." );
			root = node as JsonObject
				?? throw new ArgumentException( "particle definition migration: top level must be a JSON object." );
		}
		catch ( JsonException e )
		{
			throw new ArgumentException( $"particle definition migration: malformed JSON: {e.Message}", e );
		}
		if ( !root.TryGetPropertyValue( "version", out var versionNode ) )
			throw new ArgumentException( "particle definition migration: missing \"version\"." );
		if ( versionNode is not JsonValue versionValue
			|| !versionValue.TryGetValue<int>( out int version ) )
			throw new ArgumentException( "particle definition migration: \"version\" must be a JSON integer." );
		if ( version == CurrentVersion )
		{
			ValidateV2( root );
			return root.ToJsonString( OutputOptions );
		}
		if ( version != FirstVersion )
			throw new ArgumentException(
				version > CurrentVersion
					? $"particle definition migration: version {version} is newer than this build knows (latest: {CurrentVersion})."
					: $"particle definition migration: unsupported version {version} (known: {FirstVersion}..{CurrentVersion})." );
		UpgradeV1ToV2( root );
		return root.ToJsonString( OutputOptions );
	}

	/// <summary>Thin file wrapper only: read, <see cref="Migrate"/>,
	/// write. It adds no behaviour beyond that pipeline.</summary>
	public static void MigrateFile( string inputPath, string outputPath )
	{
		if ( string.IsNullOrWhiteSpace( inputPath ) )
			throw new ArgumentException( "particle definition migration: input path must not be empty." );
		if ( string.IsNullOrWhiteSpace( outputPath ) )
			throw new ArgumentException( "particle definition migration: output path must not be empty." );
		string json;
		try { json = File.ReadAllText( inputPath ); }
		catch ( IOException e )
		{ throw new ArgumentException( $"particle definition migration: cannot read '{inputPath}': {e.Message}", e ); }
		string migrated = Migrate( json );
		try { File.WriteAllText( outputPath, migrated ); }
		catch ( IOException e )
		{ throw new ArgumentException( $"particle definition migration: cannot write '{outputPath}': {e.Message}", e ); }
	}

	static void ValidateV2( JsonObject root )
	{
		if ( !root.TryGetPropertyValue( "migratedFrom", out var fromNode ) )
			throw new ArgumentException( "particle definition migration: v2 document is missing required \"migratedFrom\"." );
		if ( fromNode is not JsonValue fromValue
			|| !fromValue.TryGetValue<int>( out int from ) )
			throw new ArgumentException( "particle definition migration: v2 \"migratedFrom\" must be a JSON integer." );
		if ( from != FirstVersion && from != CurrentVersion )
			throw new ArgumentException( $"particle definition migration: v2 \"migratedFrom\" {from} is not a known source version ({FirstVersion} or {CurrentVersion})." );
	}

	static void UpgradeV1ToV2( JsonObject root )
	{
		root["version"] = CurrentVersion;
		// Provenance is the actual source version, so a stray
		// "migratedFrom" carried by a v1 doc is corrected, not kept.
		root["migratedFrom"] = FirstVersion;
		if ( root.TryGetPropertyValue( "emitters", out var emittersNode )
			&& emittersNode is JsonArray emitters )
		{
			foreach ( var emitterNode in emitters )
			{
				if ( emitterNode is not JsonObject emitter ) continue;
				if ( emitter.ContainsKey( "label" ) ) continue;
				JsonNode? label = StringField( emitter, "name" ) ?? StringField( emitter, "preset" );
				if ( label is not null ) emitter["label"] = label;
			}
		}
	}

	static JsonNode? StringField( JsonObject emitter, string field )
	{
		if ( !emitter.TryGetPropertyValue( field, out var node ) ) return null;
		if ( node is JsonValue value && value.TryGetValue<string>( out string? text ) && text is not null )
			return JsonValue.Create( text );
		return null;
	}
}
