#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Marionette.Desktop;

// Particle definition diff (particles wave-6 lane P70, 2026-10-24).
//
// WHAT THIS IS: a report-only differ for particle system definitions in
// the lane-5 schema (ParticleAuthoring.cs: version 1 JSON, settings +
// emitters + inline mesh geometry). Given two definitions it lists every
// field that changed. It never migrates, validates, or patches anything:
// migration is P50's file, validation beyond the checks below is P71's,
// and applying a diff is out of scope — this only reports.
//
// INPUT FORM (chosen deliberately, not by accident): Diff takes the two
// definitions as JSON STRINGS, not as deserialized ParticleSystemDef
// objects. Lane 5's deserializer ignores unknown JSON fields (forward
// compat), so a def-object diff would silently drop exactly the fields
// this lane must still report as changes. Diffing the JSON documents
// keeps unknown/extra fields visible. Callers that already hold def
// objects can round-trip through ParticleAuthoring.ToJson first.
//
// PATH FORM: dotted field names with [index] for array elements, e.g.
//   settings.gravity[2], emitters[0].rate, emitters[1], name
// The root object itself has no path; its fields are top-level names.
//
// ENTRY ORDER (deterministic): depth-first. For each object, fields are
// visited in the OLD document's order (Changed/Removed entries first),
// then fields present only in the new document in the NEW document's
// order (Added entries). Arrays are visited in index order.
//
// CHANGE KINDS:
//   Changed — field present on both sides with a different value.
//   Added   — field present only in the new definition (OldValue null).
//   Removed — field present only in the old definition (NewValue null).
// A field explicitly set to JSON null is NOT missing: null vs a value
// is Changed (with the null side rendered "null"), and null vs null is
// equal. Only absence counts as Added/Removed.
//
// OBJECTS: compared by field name, order-insensitive — reordering keys
// (at any depth) produces no entries. ARRAYS: compared BY INDEX, not by
// matching IDs or names. Inserting an element at the front therefore
// shifts every later element: the shifted elements report as Changed
// and the tail reports as Added. That is honest index behaviour, not a
// move detector, and the tests pin an insertion-shift case so nobody
// mistakes it for one.
//
// NUMBERS: compared NUMERICALLY, not as text, so 1 vs 1.0 is equal,
// as are 1e2 vs 100 and 0.4 vs 0.40. Comparison is exact decimal
// comparison when both numbers fit in a decimal; numbers outside the
// decimal range fall back to exact double comparison. -0 equals 0.
// Strings compare ordinally (case-sensitive); true vs false and any
// other kind change (number vs string, object vs array, ...) is Changed
// with the whole old/new values rendered.
//
// VALUE RENDERING (OldValue/NewValue): compact JSON — strings quoted,
// numbers decimal-normalized (1.0 renders "1"), objects/arrays compact
// with keys in document order. So a gravity change surfaces as
// settings.gravity[2]: -386.0886 -> -60, not as a whole-vector entry.
//
// LOUD FAILURES (same strictness spirit as lane 5, no silent repair):
//   - null input (either side) throws ArgumentNullException.
//   - empty/whitespace input throws ArgumentException.
//   - malformed JSON on either side throws ArgumentException wrapping
//     the JsonException (message prefixed "particle def diff:").
//   - a non-object root throws ArgumentException.
//   - a missing "version", a non-integer "version", or a "version"
//     other than ParticleAuthoring.CurrentVersion (1) on EITHER side
//     throws ArgumentException. This is a check, not a migration: a
//     version-2 definition is rejected, never upgraded (P50 owns that).
//   - Summarize/CountByKind on a null list throw ArgumentNullException.
// No other validation happens here: unknown presets, bad shapes, bad
// render types, and invalid values are P71's validation pack, not this
// differ's business — they still diff like any other field.
//
// SUMMARY: Summarize returns one line per change, in entry order:
//   Changed: "path: old -> new"
//   Added:   "path: (added) new"
//   Removed: "path: old (removed)"
// An empty diff summarizes as the literal string "no changes", never an
// exception. CountByKind counts entries per kind (Total = sum).
//
// LIMITS (stated, not hidden): no patch/apply function; no wiring into
// authoring or UI; arrays are index-based (see above); duplicate JSON
// keys in one object are not deduplicated and may report twice —
// duplicate keys are invalid authoring practice, and the differ does
// not silently repair them.

/// <summary>How a field differs between two definitions.</summary>
public enum ParticleDefinitionChangeKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>One changed field: its dotted path, the old and new rendered
/// values (null on the absent side for Added/Removed), and its kind.</summary>
public sealed class ParticleDefinitionChange
{
    public ParticleDefinitionChange( string path, string? oldValue, string? newValue, ParticleDefinitionChangeKind kind )
    {
        Path = path;
        OldValue = oldValue;
        NewValue = newValue;
        Kind = kind;
    }

    public string Path { get; }
    public string? OldValue { get; }
    public string? NewValue { get; }
    public ParticleDefinitionChangeKind Kind { get; }

    /// <summary>The Summarize line for this entry.</summary>
    public override string ToString() => Kind switch
    {
        ParticleDefinitionChangeKind.Added => $"{Path}: (added) {NewValue}",
        ParticleDefinitionChangeKind.Removed => $"{Path}: {OldValue} (removed)",
        _ => $"{Path}: {OldValue} -> {NewValue}",
    };
}

/// <summary>Counts of diff entries by kind.</summary>
public readonly record struct ParticleDefinitionCounts( int Added, int Removed, int Changed )
{
    public int Total => Added + Removed + Changed;
}

/// <summary>Report-only differ for lane-5 particle definition JSON.
/// See the file header for the input form, path form, ordering, numeric,
/// array, and failure rules.</summary>
public static class ParticleDefinitionDiff
{
    /// <summary>Diff two particle definitions given as JSON strings.
    /// Returns the changed fields, empty when the definitions are
    /// identical (modulo key order and numeric representation).</summary>
    public static IReadOnlyList<ParticleDefinitionChange> Diff( string? oldJson, string? newJson )
    {
        ArgumentNullException.ThrowIfNull( oldJson );
        ArgumentNullException.ThrowIfNull( newJson );
        if ( string.IsNullOrWhiteSpace( oldJson ) )
            throw new ArgumentException( "particle def diff: old JSON is empty." );
        if ( string.IsNullOrWhiteSpace( newJson ) )
            throw new ArgumentException( "particle def diff: new JSON is empty." );
        JsonElement oldRoot, newRoot;
        try
        {
            using var oldDoc = JsonDocument.Parse( oldJson );
            using var newDoc = JsonDocument.Parse( newJson );
            oldRoot = oldDoc.RootElement.Clone();
            newRoot = newDoc.RootElement.Clone();
        }
        catch ( JsonException e )
        {
            throw new ArgumentException( $"particle def diff: malformed JSON: {e.Message}", e );
        }
        CheckVersion( oldRoot, "old" );
        CheckVersion( newRoot, "new" );
        var changes = new List<ParticleDefinitionChange>();
        DiffElement( oldRoot, newRoot, "", changes );
        return changes;
    }

    static void CheckVersion( JsonElement root, string side )
    {
        if ( root.ValueKind != JsonValueKind.Object )
            throw new ArgumentException( $"particle def diff: {side} definition top level must be a JSON object." );
        if ( !root.TryGetProperty( "version", out var version ) )
            throw new ArgumentException( $"particle def diff: {side} definition is missing \"version\" (this build reads version {ParticleAuthoring.CurrentVersion})." );
        if ( version.ValueKind != JsonValueKind.Number || !version.TryGetInt32( out int v ) || v != ParticleAuthoring.CurrentVersion )
            throw new ArgumentException( $"particle def diff: {side} definition has unsupported version '{version}' (this build reads version {ParticleAuthoring.CurrentVersion})." );
    }

    static void DiffElement( JsonElement oldEl, JsonElement newEl, string path, List<ParticleDefinitionChange> changes )
    {
        if ( oldEl.ValueKind == JsonValueKind.Number && newEl.ValueKind == JsonValueKind.Number )
        {
            if ( !NumbersEqual( oldEl, newEl ) )
                changes.Add( new ParticleDefinitionChange( path, Render( oldEl ), Render( newEl ), ParticleDefinitionChangeKind.Changed ) );
            return;
        }
        if ( oldEl.ValueKind != newEl.ValueKind )
        {
            changes.Add( new ParticleDefinitionChange( path, Render( oldEl ), Render( newEl ), ParticleDefinitionChangeKind.Changed ) );
            return;
        }
        switch ( oldEl.ValueKind )
        {
            case JsonValueKind.Object:
            {
                foreach ( var prop in oldEl.EnumerateObject() )
                {
                    string child = ChildPath( path, prop.Name );
                    if ( newEl.TryGetProperty( prop.Name, out var newProp ) )
                        DiffElement( prop.Value, newProp, child, changes );
                    else
                        changes.Add( new ParticleDefinitionChange( child, Render( prop.Value ), null, ParticleDefinitionChangeKind.Removed ) );
                }
                foreach ( var prop in newEl.EnumerateObject() )
                {
                    if ( !oldEl.TryGetProperty( prop.Name, out _ ) )
                        changes.Add( new ParticleDefinitionChange( ChildPath( path, prop.Name ), null, Render( prop.Value ), ParticleDefinitionChangeKind.Added ) );
                }
                break;
            }
            case JsonValueKind.Array:
            {
                int shared = Math.Min( oldEl.GetArrayLength(), newEl.GetArrayLength() );
                for ( int i = 0; i < shared; i++ )
                    DiffElement( oldEl[i], newEl[i], $"{path}[{i}]", changes );
                for ( int i = shared; i < oldEl.GetArrayLength(); i++ )
                    changes.Add( new ParticleDefinitionChange( $"{path}[{i}]", Render( oldEl[i] ), null, ParticleDefinitionChangeKind.Removed ) );
                for ( int i = shared; i < newEl.GetArrayLength(); i++ )
                    changes.Add( new ParticleDefinitionChange( $"{path}[{i}]", null, Render( newEl[i] ), ParticleDefinitionChangeKind.Added ) );
                break;
            }
            case JsonValueKind.String:
                if ( !string.Equals( oldEl.GetString(), newEl.GetString(), StringComparison.Ordinal ) )
                    changes.Add( new ParticleDefinitionChange( path, Render( oldEl ), Render( newEl ), ParticleDefinitionChangeKind.Changed ) );
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                // Same kind here means same value; nothing to report.
                break;
            default:
                // Undefined cannot appear from a parsed document; a kind
                // mismatch above already covers anything unexpected.
                break;
        }
    }

    static string ChildPath( string path, string name ) => path.Length == 0 ? name : path + "." + name;

    static bool NumbersEqual( JsonElement a, JsonElement b )
    {
        if ( a.TryGetDecimal( out decimal da ) && b.TryGetDecimal( out decimal db ) )
            return da == db;
        if ( a.TryGetDouble( out double fa ) && b.TryGetDouble( out double fb ) )
            return fa == fb;
        return string.Equals( a.GetRawText(), b.GetRawText(), StringComparison.Ordinal );
    }

    /// <summary>Compact JSON rendering with numbers decimal-normalized
    /// (1.0 renders "1"). Strings render quoted, as JSON.</summary>
    static string Render( JsonElement el )
    {
        switch ( el.ValueKind )
        {
            case JsonValueKind.Number:
                if ( el.TryGetDecimal( out decimal d ) )
                    return d.ToString( CultureInfo.InvariantCulture );
                if ( el.TryGetDouble( out double f ) )
                    return f.ToString( "R", CultureInfo.InvariantCulture );
                return el.GetRawText();
            case JsonValueKind.String:
                return JsonSerializer.Serialize( el.GetString() );
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.Null: return "null";
            case JsonValueKind.Array:
            {
                var parts = new List<string>();
                foreach ( var item in el.EnumerateArray() ) parts.Add( Render( item ) );
                return "[" + string.Join( ", ", parts ) + "]";
            }
            case JsonValueKind.Object:
            {
                var parts = new List<string>();
                foreach ( var prop in el.EnumerateObject() )
                    parts.Add( JsonSerializer.Serialize( prop.Name ) + ": " + Render( prop.Value ) );
                return "{" + string.Join( ", ", parts ) + "}";
            }
            default:
                return el.GetRawText();
        }
    }

    /// <summary>One summary line per change, in entry order; "no changes"
    /// for an empty diff, never an exception for an empty list.</summary>
    public static string Summarize( IReadOnlyList<ParticleDefinitionChange>? diff )
    {
        ArgumentNullException.ThrowIfNull( diff );
        if ( diff.Count == 0 ) return "no changes";
        var lines = new StringBuilder();
        for ( int i = 0; i < diff.Count; i++ )
        {
            if ( i > 0 ) lines.Append( '\n' );
            lines.Append( diff[i].ToString() );
        }
        return lines.ToString();
    }

    /// <summary>Counts of the diff's entries by kind.</summary>
    public static ParticleDefinitionCounts CountByKind( IReadOnlyList<ParticleDefinitionChange>? diff )
    {
        ArgumentNullException.ThrowIfNull( diff );
        int added = 0, removed = 0, changed = 0;
        foreach ( var entry in diff )
        {
            switch ( entry.Kind )
            {
                case ParticleDefinitionChangeKind.Added: added++; break;
                case ParticleDefinitionChangeKind.Removed: removed++; break;
                case ParticleDefinitionChangeKind.Changed: changed++; break;
            }
        }
        return new ParticleDefinitionCounts( added, removed, changed );
    }
}
