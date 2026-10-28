using System;
using System.Collections.Generic;
using Marionette.Desktop;

internal static class ParticleDefinitionDiffTests
{
    public static void Register( TestRegistry registry, TestSettings settings )
    {
        void Check( string name, Action test ) => registry.Add( "particle-definition-diff", name, test );
        void Assert( bool condition, string message = "assertion failed" )
        {
            if ( !condition ) throw new Exception( message );
        }
        void AssertEntry( ParticleDefinitionChange entry, string path, string? oldValue, string? newValue, ParticleDefinitionChangeKind kind )
        {
            Assert( entry.Path == path, $"path: expected '{path}', got '{entry.Path}'" );
            Assert( entry.OldValue == oldValue, $"path {path}: old: expected '{oldValue}', got '{entry.OldValue}'" );
            Assert( entry.NewValue == newValue, $"path {path}: new: expected '{newValue}', got '{entry.NewValue}'" );
            Assert( entry.Kind == kind, $"path {path}: kind: expected {kind}, got {entry.Kind}" );
        }
        void Throws( Func<object?> action, string label )
        {
            try { action(); }
            catch ( Exception ) { return; }
            throw new Exception( $"expected an error for {label}, got silent success" );
        }
        ParticleDefinitionChange Only( IReadOnlyList<ParticleDefinitionChange> diff, string label )
        {
            Assert( diff.Count == 1, $"{label}: expected exactly 1 entry, got {diff.Count}" );
            return diff[0];
        }

        const string Base = """
            {
              "version": 1,
              "name": "ember",
              "settings": { "capacity": 4000, "seed": 7, "gravity": [0, 0, -386.0886], "drag": 0.4, "renderType": "Billboard" },
              "emitters": [ { "name": "main", "shape": "Point", "rate": 12, "position": [0, 0, 10] } ]
            }
            """;

        Check( "identical inputs give an empty diff", () =>
        {
            var diff = ParticleDefinitionDiff.Diff( Base, Base );
            Assert( diff.Count == 0, $"identical inputs must give 0 entries, got {diff.Count}" );
            Assert( ParticleDefinitionDiff.Summarize( diff ) == "no changes", "empty diff must summarize as 'no changes'" );
            var counts = ParticleDefinitionDiff.CountByKind( diff );
            Assert( counts.Added == 0 && counts.Removed == 0 && counts.Changed == 0 && counts.Total == 0,
                $"empty counts must all be 0, got {counts}" );
        } );

        Check( "key reordering is not a change", () =>
        {
            const string reordered = """
                {
                  "emitters": [ { "position": [0, 0, 10], "rate": 12, "shape": "Point", "name": "main" } ],
                  "settings": { "renderType": "Billboard", "drag": 0.4, "gravity": [0, 0, -386.0886], "seed": 7, "capacity": 4000 },
                  "name": "ember",
                  "version": 1
                }
                """;
            var diff = ParticleDefinitionDiff.Diff( Base, reordered );
            Assert( diff.Count == 0, $"reordered keys must give 0 entries, got {diff.Count}: {ParticleDefinitionDiff.Summarize( diff )}" );
        } );

        Check( "single gravity change gives exactly one entry", () =>
        {
            const string changed = """
                {
                  "version": 1,
                  "name": "ember",
                  "settings": { "capacity": 4000, "seed": 7, "gravity": [0, 0, -60], "drag": 0.4, "renderType": "Billboard" },
                  "emitters": [ { "name": "main", "shape": "Point", "rate": 12, "position": [0, 0, 10] } ]
                }
                """;
            var entry = Only( ParticleDefinitionDiff.Diff( Base, changed ), "gravity change" );
            AssertEntry( entry, "settings.gravity[2]", "-386.0886", "-60", ParticleDefinitionChangeKind.Changed );
        } );

        Check( "added field reports Added with null old value", () =>
        {
            const string added = """
                {
                  "version": 1,
                  "name": "ember",
                  "settings": { "capacity": 4000, "seed": 7, "gravity": [0, 0, -386.0886], "drag": 0.4, "renderType": "Billboard", "turbulenceStrength": 2 },
                  "emitters": [ { "name": "main", "shape": "Point", "rate": 12, "position": [0, 0, 10] } ]
                }
                """;
            var entry = Only( ParticleDefinitionDiff.Diff( Base, added ), "added field" );
            AssertEntry( entry, "settings.turbulenceStrength", null, "2", ParticleDefinitionChangeKind.Added );
        } );

        Check( "removed field reports Removed with null new value", () =>
        {
            const string removed = """
                {
                  "version": 1,
                  "name": "ember",
                  "settings": { "capacity": 4000, "seed": 7, "gravity": [0, 0, -386.0886], "renderType": "Billboard" },
                  "emitters": [ { "name": "main", "shape": "Point", "rate": 12, "position": [0, 0, 10] } ]
                }
                """;
            var entry = Only( ParticleDefinitionDiff.Diff( Base, removed ), "removed field" );
            AssertEntry( entry, "settings.drag", "0.4", null, ParticleDefinitionChangeKind.Removed );
        } );

        Check( "unknown and extra fields are diffed like any other", () =>
        {
            const string oldJson = """
                { "version": 1, "name": "ember", "futureField": { "a": 1, "b": 2 },
                  "settings": { "capacity": 4000 }, "emitters": [] }
                """;
            const string newJson = """
                { "version": 1, "name": "ember", "futureField": { "a": 1, "b": 3, "c": 9 },
                  "settings": { "capacity": 4000 }, "emitters": [] }
                """;
            var diff = ParticleDefinitionDiff.Diff( oldJson, newJson );
            Assert( diff.Count == 2, $"expected 2 unknown-field entries, got {diff.Count}: {ParticleDefinitionDiff.Summarize( diff )}" );
            AssertEntry( diff[0], "futureField.b", "2", "3", ParticleDefinitionChangeKind.Changed );
            AssertEntry( diff[1], "futureField.c", null, "9", ParticleDefinitionChangeKind.Added );
        } );

        Check( "nested mesh geometry diffs by index", () =>
        {
            const string oldJson = """
                { "version": 1, "settings": {},
                  "emitters": [ { "shape": "MeshSurface",
                    "mesh": { "vertices": [[0, 0, 0], [10, 0, 0]], "indices": [0, 1] } } ] }
                """;
            const string newJson = """
                { "version": 1, "settings": {},
                  "emitters": [ { "shape": "MeshSurface",
                    "mesh": { "vertices": [[0, 0, 0], [10, 5, 0]], "indices": [0, 1] } } ] }
                """;
            var entry = Only( ParticleDefinitionDiff.Diff( oldJson, newJson ), "mesh vertex change" );
            AssertEntry( entry, "emitters[0].mesh.vertices[1][1]", "0", "5", ParticleDefinitionChangeKind.Changed );
        } );

        Check( "emitter rate change reports emitters[0].rate", () =>
        {
            const string changed = """
                {
                  "version": 1,
                  "name": "ember",
                  "settings": { "capacity": 4000, "seed": 7, "gravity": [0, 0, -386.0886], "drag": 0.4, "renderType": "Billboard" },
                  "emitters": [ { "name": "main", "shape": "Point", "rate": 30, "position": [0, 0, 10] } ]
                }
                """;
            var entry = Only( ParticleDefinitionDiff.Diff( Base, changed ), "rate change" );
            AssertEntry( entry, "emitters[0].rate", "12", "30", ParticleDefinitionChangeKind.Changed );
        } );

        Check( "appended emitter reports emitters[1] Added", () =>
        {
            const string appended = """
                {
                  "version": 1,
                  "name": "ember",
                  "settings": { "capacity": 4000, "seed": 7, "gravity": [0, 0, -386.0886], "drag": 0.4, "renderType": "Billboard" },
                  "emitters": [ { "name": "main", "shape": "Point", "rate": 12, "position": [0, 0, 10] },
                                { "name": "second", "shape": "Box", "rate": 5 } ]
                }
                """;
            var entry = Only( ParticleDefinitionDiff.Diff( Base, appended ), "appended emitter" );
            AssertEntry( entry, "emitters[1]", null, """{"name": "second", "shape": "Box", "rate": 5}""",
                ParticleDefinitionChangeKind.Added );
        } );

        Check( "removed emitter reports emitters[1] Removed", () =>
        {
            const string two = """
                {
                  "version": 1,
                  "name": "ember",
                  "settings": { "capacity": 4000, "seed": 7, "gravity": [0, 0, -386.0886], "drag": 0.4, "renderType": "Billboard" },
                  "emitters": [ { "name": "main", "shape": "Point", "rate": 12, "position": [0, 0, 10] },
                                { "name": "second", "shape": "Box", "rate": 5 } ]
                }
                """;
            var entry = Only( ParticleDefinitionDiff.Diff( two, Base ), "removed emitter" );
            AssertEntry( entry, "emitters[1]", """{"name": "second", "shape": "Box", "rate": 5}""", null,
                ParticleDefinitionChangeKind.Removed );
        } );

        Check( "insertion shifts indices instead of matching by name", () =>
        {
            const string inserted = """
                {
                  "version": 1,
                  "name": "ember",
                  "settings": { "capacity": 4000, "seed": 7, "gravity": [0, 0, -386.0886], "drag": 0.4, "renderType": "Billboard" },
                  "emitters": [ { "name": "front", "shape": "Point", "rate": 1, "position": [0, 0, 0] },
                                { "name": "main", "shape": "Point", "rate": 12, "position": [0, 0, 10] } ]
                }
                """;
            var diff = ParticleDefinitionDiff.Diff( Base, inserted );
            // Index-based, honestly: index 0 compares old "main" against
            // new "front" (Changed), and the shifted old emitter lands
            // as an Added tail entry — no identity matching happens.
            bool nameChanged = false, tailAdded = false;
            foreach ( var entry in diff )
            {
                if ( entry.Path == "emitters[0].name" && entry.Kind == ParticleDefinitionChangeKind.Changed
                    && entry.OldValue == "\"main\"" && entry.NewValue == "\"front\"" )
                    nameChanged = true;
                if ( entry.Path == "emitters[1]" && entry.Kind == ParticleDefinitionChangeKind.Added )
                    tailAdded = true;
            }
            Assert( nameChanged, $"insertion must Changed emitters[0].name, got: {ParticleDefinitionDiff.Summarize( diff )}" );
            Assert( tailAdded, $"insertion must Add emitters[1], got: {ParticleDefinitionDiff.Summarize( diff )}" );
        } );

        Check( "numeric 1 vs 1.0 and 1e2 vs 100 are equal", () =>
        {
            const string oldJson = """
                { "version": 1, "name": "ember",
                  "settings": { "capacity": 4000, "seed": 7, "gravity": [0, 0, -60], "drag": 0.4 },
                  "emitters": [] }
                """;
            const string newJson = """
                { "version": 1, "name": "ember",
                  "settings": { "capacity": 4000.0, "seed": 7.0, "gravity": [0.0, 0, -60.00], "drag": 0.40 },
                  "emitters": [] }
                """;
            var diff = ParticleDefinitionDiff.Diff( oldJson, newJson );
            Assert( diff.Count == 0, $"numeric spellings must be equal, got {diff.Count}: {ParticleDefinitionDiff.Summarize( diff )}" );
            const string expJson = """
                { "version": 1, "settings": { "capacity": 1e2 }, "emitters": [] }
                """;
            const string plainJson = """
                { "version": 1, "settings": { "capacity": 100 }, "emitters": [] }
                """;
            Assert( ParticleDefinitionDiff.Diff( expJson, plainJson ).Count == 0, "1e2 vs 100 must be equal" );
        } );

        Check( "numeric inequality is Changed", () =>
        {
            const string changed = """
                { "version": 1, "settings": { "capacity": 4001 }, "emitters": [] }
                """;
            const string oldJson = """
                { "version": 1, "settings": { "capacity": 4000 }, "emitters": [] }
                """;
            var entry = Only( ParticleDefinitionDiff.Diff( oldJson, changed ), "capacity change" );
            AssertEntry( entry, "settings.capacity", "4000", "4001", ParticleDefinitionChangeKind.Changed );
        } );

        Check( "type change is Changed with whole values", () =>
        {
            const string oldJson = """
                { "version": 1, "settings": { "drag": 0.4 }, "emitters": [ { "rate": 12 } ] }
                """;
            const string newJson = """
                { "version": 1, "settings": { "drag": "fast" }, "emitters": [ { "rate": "fast" } ] }
                """;
            var diff = ParticleDefinitionDiff.Diff( oldJson, newJson );
            Assert( diff.Count == 2, $"expected 2 type-change entries, got {diff.Count}" );
            AssertEntry( diff[0], "settings.drag", "0.4", "\"fast\"", ParticleDefinitionChangeKind.Changed );
            AssertEntry( diff[1], "emitters[0].rate", "12", "\"fast\"", ParticleDefinitionChangeKind.Changed );
        } );

        Check( "explicit null differs from missing, null vs null is equal", () =>
        {
            const string valued = """{ "version": 1, "name": "ember", "settings": {}, "emitters": [] }""";
            const string nulled = """{ "version": 1, "name": null, "settings": {}, "emitters": [] }""";
            const string missing = """{ "version": 1, "settings": {}, "emitters": [] }""";
            var toNull = Only( ParticleDefinitionDiff.Diff( valued, nulled ), "value to null" );
            AssertEntry( toNull, "name", "\"ember\"", "null", ParticleDefinitionChangeKind.Changed );
            var nullRemoved = Only( ParticleDefinitionDiff.Diff( nulled, missing ), "null field removed" );
            AssertEntry( nullRemoved, "name", "null", null, ParticleDefinitionChangeKind.Removed );
            Assert( ParticleDefinitionDiff.Diff( nulled, nulled ).Count == 0, "null vs null must be equal" );
        } );

        Check( "malformed JSON throws on either side", () =>
        {
            Throws( () => ParticleDefinitionDiff.Diff( "{ not json", Base ), "malformed old JSON" );
            Throws( () => ParticleDefinitionDiff.Diff( Base, "{ not json" ), "malformed new JSON" );
            Throws( () => ParticleDefinitionDiff.Diff( "", Base ), "empty old JSON" );
            Throws( () => ParticleDefinitionDiff.Diff( Base, "  " ), "blank new JSON" );
            Throws( () => ParticleDefinitionDiff.Diff( "[1, 2]", Base ), "non-object old root" );
            Throws( () => ParticleDefinitionDiff.Diff( Base, "42" ), "non-object new root" );
        } );

        Check( "bad or missing version throws on either side", () =>
        {
            const string noVersion = """{ "name": "ember", "settings": {}, "emitters": [] }""";
            const string v2 = """{ "version": 2, "settings": {}, "emitters": [] }""";
            const string vString = """{ "version": "1", "settings": {}, "emitters": [] }""";
            const string vFloat = """{ "version": 1.5, "settings": {}, "emitters": [] }""";
            Throws( () => ParticleDefinitionDiff.Diff( noVersion, Base ), "missing old version" );
            Throws( () => ParticleDefinitionDiff.Diff( Base, noVersion ), "missing new version" );
            Throws( () => ParticleDefinitionDiff.Diff( v2, Base ), "old version 2" );
            Throws( () => ParticleDefinitionDiff.Diff( Base, v2 ), "new version 2" );
            Throws( () => ParticleDefinitionDiff.Diff( vString, Base ), "string version" );
            Throws( () => ParticleDefinitionDiff.Diff( Base, vFloat ), "fractional version" );
        } );

        Check( "null inputs throw", () =>
        {
            Throws( () => ParticleDefinitionDiff.Diff( null, Base ), "null old JSON" );
            Throws( () => ParticleDefinitionDiff.Diff( Base, null ), "null new JSON" );
            Throws( () => ParticleDefinitionDiff.Summarize( null ), "null summary input" );
            Throws( () => ParticleDefinitionDiff.CountByKind( null ), "null count input" );
        } );

        Check( "summary lines match the entries exactly", () =>
        {
            const string oldJson = """
                { "version": 1, "name": "ember",
                  "settings": { "capacity": 4000, "drag": 0.4 }, "emitters": [] }
                """;
            const string newJson = """
                { "version": 1, "name": "frost",
                  "settings": { "capacity": 4000, "turbulenceStrength": 2 }, "emitters": [] }
                """;
            var diff = ParticleDefinitionDiff.Diff( oldJson, newJson );
            Assert( diff.Count == 3, $"expected 3 entries, got {diff.Count}" );
            string expected =
                "name: \"ember\" -> \"frost\"\n" +
                "settings.drag: 0.4 (removed)\n" +
                "settings.turbulenceStrength: (added) 2";
            Assert( ParticleDefinitionDiff.Summarize( diff ) == expected,
                $"summary mismatch.\nexpected:\n{expected}\ngot:\n{ParticleDefinitionDiff.Summarize( diff )}" );
            var counts = ParticleDefinitionDiff.CountByKind( diff );
            Assert( counts.Added == 1 && counts.Removed == 1 && counts.Changed == 1 && counts.Total == 3,
                $"counts must be 1/1/1/3, got {counts}" );
        } );

        Check( "summary line for a single entry equals the entry text", () =>
        {
            const string changed = """
                { "version": 1, "settings": { "capacity": 4001 }, "emitters": [] }
                """;
            const string oldJson = """
                { "version": 1, "settings": { "capacity": 4000 }, "emitters": [] }
                """;
            var diff = ParticleDefinitionDiff.Diff( oldJson, changed );
            Assert( ParticleDefinitionDiff.Summarize( diff ) == "settings.capacity: 4000 -> 4001",
                $"single-entry summary mismatch: {ParticleDefinitionDiff.Summarize( diff )}" );
            Assert( ParticleDefinitionDiff.Summarize( diff ) == diff[0].ToString(), "summary must equal entry ToString" );
        } );
    }
}
