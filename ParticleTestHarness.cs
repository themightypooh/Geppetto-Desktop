// ParticleTestHarness.cs — deterministic scenario runner FOR FUTURE LANES' TESTS.
//
// This is a library that future particle tests CALL. It is NOT a test runner:
// it does not touch TestRegistry, Program.cs, or any dispatch, and it never
// touches the live cabinet, files, or the network. A scenario is pure sim
// plus assertions, run in memory.
//
// A scenario is a small plain record:
//   Name  — human label, required, non-blank.
//   Seed  — the sim seed. The harness builds the system with this seed
//           BEFORE Setup runs, and Setup must not change Settings.Seed
//           afterwards (emitter RNG streams are keyed at AddEmitter time,
//           so a late seed change would silently not reseed anything).
//           Changing the seed inside Setup throws loudly.
//   Steps — fixed-step count at the sim's 60 Hz fixed step. Steps = 0 is
//           legal and yields exactly one snapshot: the initial state.
//   Setup — an action that configures a fresh system (add emitters, tweak
//           non-seed settings such as gravity or drag) BEFORE stepping.
//           Setup runs before any step and must itself be deterministic:
//           the same seed must produce the same setup, or RunTwice will
//           (correctly) report a difference. Setup must not read files,
//           the network, the clock, or any unseeded RNG.
//   Capacity — system capacity (default 10000). Like Seed, it is fixed at
//           construction; changing Settings.Capacity inside Setup throws.
//
// Run(scenario) captures one snapshot per step PLUS the initial snapshot,
// so a run always yields Steps + 1 snapshots, with StepIndex 0..Steps.
// Snapshot fields and their empty-system behaviour (no emitters, or no
// live particles): AliveCount is 0, MeanPosition is Vector3.Zero,
// MeanSpeed and MeanSize are 0, BoundsMin and BoundsMax are Vector3.Zero,
// Positions is empty. A snapshot NEVER reports NaN for an empty system.
//
// Extra snapshot fields, beyond the required StepIndex / AliveCount /
// MeanPosition: MeanSpeed (mean |velocity| of live particles, 0 when
// empty), MeanSize (mean Size of live particles, 0 when empty),
// BoundsMin / BoundsMax (component-wise min/max of live positions,
// Vector3.Zero when empty), Positions (live positions in slot order,
// copied per snapshot so later steps cannot rewrite earlier snapshots).
//
// Assertions: a scenario carries Expectations, each naming a step, a
// field, an expected value, and a tolerance. Run returns one verdict per
// expectation; every verdict message carries the step, the field, the
// expected value, the actual value, and the tolerance, whether it passed
// or failed. A bare boolean fail is a defect, not a verdict. Integer
// fields (StepIndex, AliveCount) ignore the tolerance (any difference is
// a difference) but still print it in the message. An expectation naming
// a step beyond the run fails with actual "missing snapshot", never a
// silent skip.
//
// Golden comparison: Compare(snapshots, golden, tolerance) walks both
// sequences in step order and field order (StepIndex, AliveCount,
// MeanPosition, MeanSpeed, MeanSize, BoundsMin, BoundsMax, then each
// Position[i] in slot order) and reports the FIRST differing step/field
// only — not a bare "mismatch". A length difference is reported at the
// first missing step with field "SnapshotCount".
//
// Determinism proof: RunTwice(scenario) runs the scenario twice and
// compares the two snapshot sequences EXACTLY (positions compared with
// ==, not within tolerance — the sim is deterministic, so the harness
// proves it rather than approximating it). It returns a report, it does
// not throw on a difference: the caller decides what a difference means.
// Future tests should assert Identical.
//
// 2D path: SHIPPED, same shape, separate types (Particle2D*). The 2D
// system fits cleanly: Particle2DSystem has Settings.Seed, AddEmitter,
// Step(int), AliveCount, and a States span with Position / Velocity /
// Alive, exactly mirroring the 3D system. One documented difference:
// the 2D snapshot has NO MeanSize field, because 2D particle size is
// age-derived inside WriteDrawRecords, not stored per-particle state;
// a future extension can add it from draw records. Everything else
// (empty-system sentinels, exact RunTwice, golden first-difference,
// verdict messages) behaves identically, with Vector2 in place of
// Vector3 and Vector2.Zero as the empty sentinel.
//
// Out of scope, deliberately: no test-runner replacement, no GPU
// harness, no scenario save/serialization (in-memory records only),
// no canvas/UI, no game loop, play mode, live physics, input, or
// networking.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>Which snapshot field an expectation or golden difference names.</summary>
public enum ParticleSnapshotField
{
    StepIndex,
    AliveCount,
    MeanPosition,
    MeanSpeed,
    MeanSize,
    BoundsMin,
    BoundsMax,
    /// <summary>A single live position, by slot order: field name is "Position[i]".</summary>
    Position,
}

/// <summary>One captured step of a 3D scenario run. Immutable; Positions is a per-snapshot copy.</summary>
public sealed record ParticleSnapshot(
    long StepIndex,
    int AliveCount,
    Vector3 MeanPosition,
    float MeanSpeed,
    float MeanSize,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    Vector3[] Positions );

/// <summary>One expectation a scenario carries: a step, a field, an expected value, a tolerance.</summary>
public sealed class ParticleExpectation
{
    public long Step;
    public ParticleSnapshotField Field;
    public float ExpectedScalar;
    public Vector3 ExpectedVector;
    public float Tolerance;

    ParticleExpectation( long step, ParticleSnapshotField field, float scalar, Vector3 vector, float tolerance )
    {
        Step = step; Field = field; ExpectedScalar = scalar; ExpectedVector = vector; Tolerance = tolerance;
    }

    public static ParticleExpectation ForAliveCount( long step, int expected )
        => new( step, ParticleSnapshotField.AliveCount, expected, Vector3.Zero, 0 );
    public static ParticleExpectation ForMeanPosition( long step, Vector3 expected, float tolerance )
        => new( step, ParticleSnapshotField.MeanPosition, 0, expected, tolerance );
    public static ParticleExpectation ForMeanSpeed( long step, float expected, float tolerance )
        => new( step, ParticleSnapshotField.MeanSpeed, expected, Vector3.Zero, tolerance );
    public static ParticleExpectation ForMeanSize( long step, float expected, float tolerance )
        => new( step, ParticleSnapshotField.MeanSize, expected, Vector3.Zero, tolerance );
    public static ParticleExpectation ForBoundsMin( long step, Vector3 expected, float tolerance )
        => new( step, ParticleSnapshotField.BoundsMin, 0, expected, tolerance );
    public static ParticleExpectation ForBoundsMax( long step, Vector3 expected, float tolerance )
        => new( step, ParticleSnapshotField.BoundsMax, 0, expected, tolerance );
}

/// <summary>Per-assertion verdict. Message always carries step, field, expected, actual, tolerance.</summary>
public sealed record ParticleAssertionVerdict(
    long Step,
    ParticleSnapshotField Field,
    string Expected,
    string Actual,
    float Tolerance,
    bool Passed,
    string Message );

/// <summary>A 3D scenario: name, seed, fixed-step count, and a deterministic Setup action.</summary>
public sealed class ParticleScenario
{
    public string Name = "";
    public uint Seed = 1;
    public int Steps;
    public int Capacity = 10_000;
    public Action<ParticleSystem>? Setup;
    public List<ParticleExpectation> Expectations = new();
}

/// <summary>Everything Run returns: the snapshots plus one verdict per expectation.</summary>
public sealed record ParticleRunResult(
    IReadOnlyList<ParticleSnapshot> Snapshots,
    IReadOnlyList<ParticleAssertionVerdict> Verdicts );

/// <summary>First-difference report from a golden comparison. Null means the sequences match.</summary>
public sealed record ParticleGoldenDifference(
    long StepIndex,
    string Field,
    string Expected,
    string Actual,
    float Tolerance );

/// <summary>Result of Compare: whether the sequences match, and the first difference if not.</summary>
public sealed record ParticleGoldenComparison(
    bool Matches,
    ParticleGoldenDifference? FirstDifference,
    string Message );

/// <summary>Result of RunTwice: whether two same-seed runs were bit-identical, and where they first differed.</summary>
public sealed record ParticleDeterminismResult(
    bool Identical,
    string? FirstDifference,
    IReadOnlyList<ParticleSnapshot> First,
    IReadOnlyList<ParticleSnapshot> Second );

/// <summary>Deterministic 3D scenario runner. Pure sim + assertions; no I/O, no cabinet, no network.</summary>
public static class ParticleTestHarness
{
    public static ParticleRunResult Run( ParticleScenario scenario )
    {
        ArgumentNullException.ThrowIfNull( scenario );
        if ( string.IsNullOrWhiteSpace( scenario.Name ) ) throw new ArgumentException( "Scenario needs a non-blank Name.", nameof( scenario ) );
        if ( scenario.Steps < 0 ) throw new ArgumentOutOfRangeException( nameof( scenario ), "Steps must be >= 0; Steps = 0 yields only the initial snapshot." );
        ArgumentNullException.ThrowIfNull( scenario.Setup, nameof( scenario ) );

        var system = new ParticleSystem( new ParticleSystemSettings { Seed = scenario.Seed, Capacity = scenario.Capacity } );
        scenario.Setup( system );
        if ( system.Settings.Seed != scenario.Seed )
            throw new InvalidOperationException( $"Scenario '{scenario.Name}': Setup changed Settings.Seed; the harness owns the seed and emitter streams are keyed at AddEmitter time." );
        if ( system.Settings.Capacity != scenario.Capacity )
            throw new InvalidOperationException( $"Scenario '{scenario.Name}': Setup changed Settings.Capacity after construction; capacity is fixed when the system is built." );

        var snapshots = new List<ParticleSnapshot>();
        snapshots.Add( Capture( system ) );
        for ( int i = 0; i < scenario.Steps; i++ )
        {
            system.Step();
            snapshots.Add( Capture( system ) );
        }
        var verdicts = new List<ParticleAssertionVerdict>();
        foreach ( var expectation in scenario.Expectations ) verdicts.Add( Evaluate( snapshots, expectation ) );
        return new ParticleRunResult( snapshots, verdicts );
    }

    public static ParticleGoldenComparison Compare(
        IReadOnlyList<ParticleSnapshot> snapshots,
        IReadOnlyList<ParticleSnapshot> golden,
        float tolerance )
    {
        ArgumentNullException.ThrowIfNull( snapshots );
        ArgumentNullException.ThrowIfNull( golden );
        if ( tolerance < 0 ) throw new ArgumentOutOfRangeException( nameof( tolerance ), "Tolerance must be >= 0." );
        int shared = Math.Min( snapshots.Count, golden.Count );
        for ( int i = 0; i < shared; i++ )
        {
            var diff = FirstFieldDifference( snapshots[i], golden[i], tolerance );
            if ( diff is not null )
                return new ParticleGoldenComparison( false, diff,
                    $"first difference at step {diff.StepIndex} field {diff.Field}: expected {diff.Expected}, actual {diff.Actual}, tolerance {diff.Tolerance}" );
        }
        if ( snapshots.Count != golden.Count )
        {
            long step = shared < snapshots.Count ? snapshots[shared].StepIndex : golden[shared].StepIndex;
            var diff = new ParticleGoldenDifference( step, "SnapshotCount", golden.Count.ToString(), snapshots.Count.ToString(), tolerance );
            return new ParticleGoldenComparison( false, diff,
                $"first difference at step {diff.StepIndex} field {diff.Field}: expected {diff.Expected}, actual {diff.Actual}, tolerance {diff.Tolerance}" );
        }
        return new ParticleGoldenComparison( true, null, "snapshots match golden within tolerance" );
    }

    public static ParticleDeterminismResult RunTwice( ParticleScenario scenario )
    {
        ArgumentNullException.ThrowIfNull( scenario );
        var first = Run( scenario ).Snapshots;
        var second = Run( scenario ).Snapshots;
        string? difference = FirstExactDifference( first, second );
        return new ParticleDeterminismResult( difference is null, difference, first, second );
    }

    static ParticleSnapshot Capture( ParticleSystem system )
    {
        var positions = new List<Vector3>();
        double sx = 0, sy = 0, sz = 0, speedSum = 0, sizeSum = 0;
        var min = new Vector3( float.MaxValue, float.MaxValue, float.MaxValue );
        var max = new Vector3( float.MinValue, float.MinValue, float.MinValue );
        foreach ( var state in system.States )
        {
            if ( state.Alive <= 0.5f ) continue;
            positions.Add( state.Position );
            sx += state.Position.X; sy += state.Position.Y; sz += state.Position.Z;
            speedSum += state.Velocity.Length();
            sizeSum += state.Size;
            min = Vector3.Min( min, state.Position );
            max = Vector3.Max( max, state.Position );
        }
        int n = positions.Count;
        return new ParticleSnapshot(
            system.StepIndex,
            system.AliveCount,
            n == 0 ? Vector3.Zero : new Vector3( (float)( sx / n ), (float)( sy / n ), (float)( sz / n ) ),
            n == 0 ? 0 : (float)( speedSum / n ),
            n == 0 ? 0 : (float)( sizeSum / n ),
            n == 0 ? Vector3.Zero : min,
            n == 0 ? Vector3.Zero : max,
            positions.ToArray() );
    }

    static ParticleAssertionVerdict Evaluate( IReadOnlyList<ParticleSnapshot> snapshots, ParticleExpectation expectation )
    {
        ParticleSnapshot? snapshot = null;
        foreach ( var s in snapshots ) if ( s.StepIndex == expectation.Step ) { snapshot = s; break; }
        if ( snapshot is null )
        {
            string expected = Describe( expectation );
            string message = $"step {expectation.Step} field {expectation.Field}: expected {expected}, actual missing snapshot, tolerance {expectation.Tolerance} — FAIL";
            return new ParticleAssertionVerdict( expectation.Step, expectation.Field, expected, "missing snapshot", expectation.Tolerance, false, message );
        }
        string actual = Actual( snapshot, expectation.Field );
        bool passed = expectation.Field switch
        {
            ParticleSnapshotField.AliveCount => snapshot.AliveCount == (int)expectation.ExpectedScalar,
            ParticleSnapshotField.StepIndex => snapshot.StepIndex == (long)expectation.ExpectedScalar,
            ParticleSnapshotField.MeanPosition => Within( snapshot.MeanPosition, expectation.ExpectedVector, expectation.Tolerance ),
            ParticleSnapshotField.MeanSpeed => MathF.Abs( snapshot.MeanSpeed - expectation.ExpectedScalar ) <= expectation.Tolerance,
            ParticleSnapshotField.MeanSize => MathF.Abs( snapshot.MeanSize - expectation.ExpectedScalar ) <= expectation.Tolerance,
            ParticleSnapshotField.BoundsMin => Within( snapshot.BoundsMin, expectation.ExpectedVector, expectation.Tolerance ),
            ParticleSnapshotField.BoundsMax => Within( snapshot.BoundsMax, expectation.ExpectedVector, expectation.Tolerance ),
            _ => false,
        };
        string expectedText = Describe( expectation );
        string verdictMessage = $"step {expectation.Step} field {expectation.Field}: expected {expectedText}, actual {actual}, tolerance {expectation.Tolerance} — {( passed ? "PASS" : "FAIL" )}";
        return new ParticleAssertionVerdict( expectation.Step, expectation.Field, expectedText, actual, expectation.Tolerance, passed, verdictMessage );
    }

    static string Describe( ParticleExpectation expectation ) => expectation.Field switch
    {
        ParticleSnapshotField.AliveCount => ( (int)expectation.ExpectedScalar ).ToString(),
        ParticleSnapshotField.StepIndex => ( (long)expectation.ExpectedScalar ).ToString(),
        ParticleSnapshotField.MeanPosition or ParticleSnapshotField.BoundsMin or ParticleSnapshotField.BoundsMax => Format( expectation.ExpectedVector ),
        _ => expectation.ExpectedScalar.ToString(),
    };

    static string Actual( ParticleSnapshot snapshot, ParticleSnapshotField field ) => field switch
    {
        ParticleSnapshotField.AliveCount => snapshot.AliveCount.ToString(),
        ParticleSnapshotField.StepIndex => snapshot.StepIndex.ToString(),
        ParticleSnapshotField.MeanPosition => Format( snapshot.MeanPosition ),
        ParticleSnapshotField.MeanSpeed => snapshot.MeanSpeed.ToString(),
        ParticleSnapshotField.MeanSize => snapshot.MeanSize.ToString(),
        ParticleSnapshotField.BoundsMin => Format( snapshot.BoundsMin ),
        ParticleSnapshotField.BoundsMax => Format( snapshot.BoundsMax ),
        _ => "?",
    };

    static bool Within( Vector3 got, Vector3 want, float tolerance )
        => MathF.Abs( got.X - want.X ) <= tolerance
            && MathF.Abs( got.Y - want.Y ) <= tolerance
            && MathF.Abs( got.Z - want.Z ) <= tolerance;

    static string Format( Vector3 v ) => $"({v.X}, {v.Y}, {v.Z})";

    static ParticleGoldenDifference? FirstFieldDifference( ParticleSnapshot got, ParticleSnapshot want, float tolerance )
    {
        long step = got.StepIndex;
        if ( got.StepIndex != want.StepIndex )
            return new ParticleGoldenDifference( step, nameof( ParticleSnapshotField.StepIndex ), want.StepIndex.ToString(), got.StepIndex.ToString(), tolerance );
        if ( got.AliveCount != want.AliveCount )
            return new ParticleGoldenDifference( step, nameof( ParticleSnapshotField.AliveCount ), want.AliveCount.ToString(), got.AliveCount.ToString(), tolerance );
        if ( !Within( got.MeanPosition, want.MeanPosition, tolerance ) )
            return new ParticleGoldenDifference( step, nameof( ParticleSnapshotField.MeanPosition ), Format( want.MeanPosition ), Format( got.MeanPosition ), tolerance );
        if ( MathF.Abs( got.MeanSpeed - want.MeanSpeed ) > tolerance )
            return new ParticleGoldenDifference( step, nameof( ParticleSnapshotField.MeanSpeed ), want.MeanSpeed.ToString(), got.MeanSpeed.ToString(), tolerance );
        if ( MathF.Abs( got.MeanSize - want.MeanSize ) > tolerance )
            return new ParticleGoldenDifference( step, nameof( ParticleSnapshotField.MeanSize ), want.MeanSize.ToString(), got.MeanSize.ToString(), tolerance );
        if ( !Within( got.BoundsMin, want.BoundsMin, tolerance ) )
            return new ParticleGoldenDifference( step, nameof( ParticleSnapshotField.BoundsMin ), Format( want.BoundsMin ), Format( got.BoundsMin ), tolerance );
        if ( !Within( got.BoundsMax, want.BoundsMax, tolerance ) )
            return new ParticleGoldenDifference( step, nameof( ParticleSnapshotField.BoundsMax ), Format( want.BoundsMax ), Format( got.BoundsMax ), tolerance );
        if ( got.Positions.Length != want.Positions.Length )
            return new ParticleGoldenDifference( step, "PositionCount", want.Positions.Length.ToString(), got.Positions.Length.ToString(), tolerance );
        for ( int i = 0; i < got.Positions.Length; i++ )
            if ( !Within( got.Positions[i], want.Positions[i], tolerance ) )
                return new ParticleGoldenDifference( step, $"Position[{i}]", Format( want.Positions[i] ), Format( got.Positions[i] ), tolerance );
        return null;
    }

    static string? FirstExactDifference( IReadOnlyList<ParticleSnapshot> first, IReadOnlyList<ParticleSnapshot> second )
    {
        if ( first.Count != second.Count ) return $"snapshot count differs: {first.Count} vs {second.Count}";
        for ( int i = 0; i < first.Count; i++ )
        {
            var a = first[i]; var b = second[i];
            if ( a.StepIndex != b.StepIndex ) return $"step index at snapshot {i} differs: {a.StepIndex} vs {b.StepIndex}";
            if ( a.AliveCount != b.AliveCount ) return $"step {a.StepIndex} AliveCount differs: {a.AliveCount} vs {b.AliveCount}";
            if ( a.MeanPosition != b.MeanPosition ) return $"step {a.StepIndex} MeanPosition differs exactly: {Format( a.MeanPosition )} vs {Format( b.MeanPosition )}";
            if ( a.MeanSpeed != b.MeanSpeed ) return $"step {a.StepIndex} MeanSpeed differs exactly";
            if ( a.MeanSize != b.MeanSize ) return $"step {a.StepIndex} MeanSize differs exactly";
            if ( a.BoundsMin != b.BoundsMin || a.BoundsMax != b.BoundsMax ) return $"step {a.StepIndex} bounds differ exactly";
            if ( a.Positions.Length != b.Positions.Length ) return $"step {a.StepIndex} position count differs: {a.Positions.Length} vs {b.Positions.Length}";
            for ( int j = 0; j < a.Positions.Length; j++ )
                if ( a.Positions[j] != b.Positions[j] ) return $"step {a.StepIndex} Position[{j}] differs exactly: {Format( a.Positions[j] )} vs {Format( b.Positions[j] )}";
        }
        return null;
    }
}

// ---------------------------------------------------------------------------
// 2D path: same snapshot/assertion shape on Particle2DSystem (Vector2).
// Difference, documented: no MeanSize — 2D size is age-derived inside
// WriteDrawRecords, not stored per-particle state. Empty-system sentinels
// are Vector2.Zero / 0, never NaN. RunTwice compares positions exactly.
// ---------------------------------------------------------------------------

/// <summary>One captured step of a 2D scenario run. Immutable; Positions is a per-snapshot copy.</summary>
public sealed record Particle2DSnapshot(
    long StepIndex,
    int AliveCount,
    Vector2 MeanPosition,
    float MeanSpeed,
    Vector2 BoundsMin,
    Vector2 BoundsMax,
    Vector2[] Positions );

/// <summary>Which 2D snapshot field an expectation names. MeanSize is intentionally absent (see header).</summary>
public enum Particle2DSnapshotField
{
    StepIndex,
    AliveCount,
    MeanPosition,
    MeanSpeed,
    BoundsMin,
    BoundsMax,
}

/// <summary>One 2D expectation: a step, a field, an expected value, a tolerance.</summary>
public sealed class Particle2DExpectation
{
    public long Step;
    public Particle2DSnapshotField Field;
    public float ExpectedScalar;
    public Vector2 ExpectedVector;
    public float Tolerance;

    Particle2DExpectation( long step, Particle2DSnapshotField field, float scalar, Vector2 vector, float tolerance )
    {
        Step = step; Field = field; ExpectedScalar = scalar; ExpectedVector = vector; Tolerance = tolerance;
    }

    public static Particle2DExpectation ForAliveCount( long step, int expected )
        => new( step, Particle2DSnapshotField.AliveCount, expected, Vector2.Zero, 0 );
    public static Particle2DExpectation ForMeanPosition( long step, Vector2 expected, float tolerance )
        => new( step, Particle2DSnapshotField.MeanPosition, 0, expected, tolerance );
    public static Particle2DExpectation ForMeanSpeed( long step, float expected, float tolerance )
        => new( step, Particle2DSnapshotField.MeanSpeed, expected, Vector2.Zero, tolerance );
    public static Particle2DExpectation ForBoundsMin( long step, Vector2 expected, float tolerance )
        => new( step, Particle2DSnapshotField.BoundsMin, 0, expected, tolerance );
    public static Particle2DExpectation ForBoundsMax( long step, Vector2 expected, float tolerance )
        => new( step, Particle2DSnapshotField.BoundsMax, 0, expected, tolerance );
}

/// <summary>Per-assertion 2D verdict; message always carries step, field, expected, actual, tolerance.</summary>
public sealed record Particle2DAssertionVerdict(
    long Step,
    Particle2DSnapshotField Field,
    string Expected,
    string Actual,
    float Tolerance,
    bool Passed,
    string Message );

/// <summary>A 2D scenario: name, seed, fixed-step count, deterministic Setup action.</summary>
public sealed class Particle2DScenario
{
    public string Name = "";
    public uint Seed = 1;
    public int Steps;
    public int Capacity = 10_000;
    public Action<Particle2DSystem>? Setup;
    public List<Particle2DExpectation> Expectations = new();
}

/// <summary>Everything a 2D Run returns: snapshots plus one verdict per expectation.</summary>
public sealed record Particle2DRunResult(
    IReadOnlyList<Particle2DSnapshot> Snapshots,
    IReadOnlyList<Particle2DAssertionVerdict> Verdicts );

/// <summary>First-difference report from a 2D golden comparison. Null means match.</summary>
public sealed record Particle2DGoldenDifference(
    long StepIndex,
    string Field,
    string Expected,
    string Actual,
    float Tolerance );

/// <summary>Result of a 2D Compare, and of a 2D RunTwice (Identical + first exact difference).</summary>
public sealed record Particle2DGoldenComparison(
    bool Matches,
    Particle2DGoldenDifference? FirstDifference,
    string Message );

public sealed record Particle2DDeterminismResult(
    bool Identical,
    string? FirstDifference,
    IReadOnlyList<Particle2DSnapshot> First,
    IReadOnlyList<Particle2DSnapshot> Second );

/// <summary>Deterministic 2D scenario runner. Pure sim + assertions; no I/O, no cabinet, no network.</summary>
public static class Particle2DTestHarness
{
    public static Particle2DRunResult Run( Particle2DScenario scenario )
    {
        ArgumentNullException.ThrowIfNull( scenario );
        if ( string.IsNullOrWhiteSpace( scenario.Name ) ) throw new ArgumentException( "Scenario needs a non-blank Name.", nameof( scenario ) );
        if ( scenario.Steps < 0 ) throw new ArgumentOutOfRangeException( nameof( scenario ), "Steps must be >= 0; Steps = 0 yields only the initial snapshot." );
        ArgumentNullException.ThrowIfNull( scenario.Setup, nameof( scenario ) );

        var system = new Particle2DSystem( new Particle2DSettings { Seed = scenario.Seed, Capacity = scenario.Capacity } );
        scenario.Setup( system );
        if ( system.Settings.Seed != scenario.Seed )
            throw new InvalidOperationException( $"Scenario '{scenario.Name}': Setup changed Settings.Seed; the harness owns the seed and emitter streams are keyed at AddEmitter time." );
        if ( system.Settings.Capacity != scenario.Capacity )
            throw new InvalidOperationException( $"Scenario '{scenario.Name}': Setup changed Settings.Capacity after construction; capacity is fixed when the system is built." );

        var snapshots = new List<Particle2DSnapshot>();
        snapshots.Add( Capture( system ) );
        for ( int i = 0; i < scenario.Steps; i++ )
        {
            system.Step();
            snapshots.Add( Capture( system ) );
        }
        var verdicts = new List<Particle2DAssertionVerdict>();
        foreach ( var expectation in scenario.Expectations ) verdicts.Add( Evaluate( snapshots, expectation ) );
        return new Particle2DRunResult( snapshots, verdicts );
    }

    public static Particle2DGoldenComparison Compare(
        IReadOnlyList<Particle2DSnapshot> snapshots,
        IReadOnlyList<Particle2DSnapshot> golden,
        float tolerance )
    {
        ArgumentNullException.ThrowIfNull( snapshots );
        ArgumentNullException.ThrowIfNull( golden );
        if ( tolerance < 0 ) throw new ArgumentOutOfRangeException( nameof( tolerance ), "Tolerance must be >= 0." );
        int shared = Math.Min( snapshots.Count, golden.Count );
        for ( int i = 0; i < shared; i++ )
        {
            var diff = FirstFieldDifference( snapshots[i], golden[i], tolerance );
            if ( diff is not null )
                return new Particle2DGoldenComparison( false, diff,
                    $"first difference at step {diff.StepIndex} field {diff.Field}: expected {diff.Expected}, actual {diff.Actual}, tolerance {diff.Tolerance}" );
        }
        if ( snapshots.Count != golden.Count )
        {
            long step = shared < snapshots.Count ? snapshots[shared].StepIndex : golden[shared].StepIndex;
            var diff = new Particle2DGoldenDifference( step, "SnapshotCount", golden.Count.ToString(), snapshots.Count.ToString(), tolerance );
            return new Particle2DGoldenComparison( false, diff,
                $"first difference at step {diff.StepIndex} field {diff.Field}: expected {diff.Expected}, actual {diff.Actual}, tolerance {diff.Tolerance}" );
        }
        return new Particle2DGoldenComparison( true, null, "snapshots match golden within tolerance" );
    }

    public static Particle2DDeterminismResult RunTwice( Particle2DScenario scenario )
    {
        ArgumentNullException.ThrowIfNull( scenario );
        var first = Run( scenario ).Snapshots;
        var second = Run( scenario ).Snapshots;
        string? difference = FirstExactDifference( first, second );
        return new Particle2DDeterminismResult( difference is null, difference, first, second );
    }

    static Particle2DSnapshot Capture( Particle2DSystem system )
    {
        var positions = new List<Vector2>();
        double sx = 0, sy = 0, speedSum = 0;
        var min = new Vector2( float.MaxValue, float.MaxValue );
        var max = new Vector2( float.MinValue, float.MinValue );
        foreach ( var state in system.States )
        {
            if ( !state.Alive ) continue;
            positions.Add( state.Position );
            sx += state.Position.X; sy += state.Position.Y;
            speedSum += state.Velocity.Length();
            min = Vector2.Min( min, state.Position );
            max = Vector2.Max( max, state.Position );
        }
        int n = positions.Count;
        return new Particle2DSnapshot(
            system.StepIndex,
            system.AliveCount,
            n == 0 ? Vector2.Zero : new Vector2( (float)( sx / n ), (float)( sy / n ) ),
            n == 0 ? 0 : (float)( speedSum / n ),
            n == 0 ? Vector2.Zero : min,
            n == 0 ? Vector2.Zero : max,
            positions.ToArray() );
    }

    static Particle2DAssertionVerdict Evaluate( IReadOnlyList<Particle2DSnapshot> snapshots, Particle2DExpectation expectation )
    {
        Particle2DSnapshot? snapshot = null;
        foreach ( var s in snapshots ) if ( s.StepIndex == expectation.Step ) { snapshot = s; break; }
        if ( snapshot is null )
        {
            string expected = Describe( expectation );
            string message = $"step {expectation.Step} field {expectation.Field}: expected {expected}, actual missing snapshot, tolerance {expectation.Tolerance} — FAIL";
            return new Particle2DAssertionVerdict( expectation.Step, expectation.Field, expected, "missing snapshot", expectation.Tolerance, false, message );
        }
        string actual = Actual( snapshot, expectation.Field );
        bool passed = expectation.Field switch
        {
            Particle2DSnapshotField.AliveCount => snapshot.AliveCount == (int)expectation.ExpectedScalar,
            Particle2DSnapshotField.StepIndex => snapshot.StepIndex == (long)expectation.ExpectedScalar,
            Particle2DSnapshotField.MeanPosition => Within( snapshot.MeanPosition, expectation.ExpectedVector, expectation.Tolerance ),
            Particle2DSnapshotField.MeanSpeed => MathF.Abs( snapshot.MeanSpeed - expectation.ExpectedScalar ) <= expectation.Tolerance,
            Particle2DSnapshotField.BoundsMin => Within( snapshot.BoundsMin, expectation.ExpectedVector, expectation.Tolerance ),
            Particle2DSnapshotField.BoundsMax => Within( snapshot.BoundsMax, expectation.ExpectedVector, expectation.Tolerance ),
            _ => false,
        };
        string expectedText = Describe( expectation );
        string verdictMessage = $"step {expectation.Step} field {expectation.Field}: expected {expectedText}, actual {actual}, tolerance {expectation.Tolerance} — {( passed ? "PASS" : "FAIL" )}";
        return new Particle2DAssertionVerdict( expectation.Step, expectation.Field, expectedText, actual, expectation.Tolerance, passed, verdictMessage );
    }

    static string Describe( Particle2DExpectation expectation ) => expectation.Field switch
    {
        Particle2DSnapshotField.AliveCount => ( (int)expectation.ExpectedScalar ).ToString(),
        Particle2DSnapshotField.StepIndex => ( (long)expectation.ExpectedScalar ).ToString(),
        Particle2DSnapshotField.MeanPosition or Particle2DSnapshotField.BoundsMin or Particle2DSnapshotField.BoundsMax => Format( expectation.ExpectedVector ),
        _ => expectation.ExpectedScalar.ToString(),
    };

    static string Actual( Particle2DSnapshot snapshot, Particle2DSnapshotField field ) => field switch
    {
        Particle2DSnapshotField.AliveCount => snapshot.AliveCount.ToString(),
        Particle2DSnapshotField.StepIndex => snapshot.StepIndex.ToString(),
        Particle2DSnapshotField.MeanPosition => Format( snapshot.MeanPosition ),
        Particle2DSnapshotField.MeanSpeed => snapshot.MeanSpeed.ToString(),
        Particle2DSnapshotField.BoundsMin => Format( snapshot.BoundsMin ),
        Particle2DSnapshotField.BoundsMax => Format( snapshot.BoundsMax ),
        _ => "?",
    };

    static bool Within( Vector2 got, Vector2 want, float tolerance )
        => MathF.Abs( got.X - want.X ) <= tolerance
            && MathF.Abs( got.Y - want.Y ) <= tolerance;

    static string Format( Vector2 v ) => $"({v.X}, {v.Y})";

    static Particle2DGoldenDifference? FirstFieldDifference( Particle2DSnapshot got, Particle2DSnapshot want, float tolerance )
    {
        long step = got.StepIndex;
        if ( got.StepIndex != want.StepIndex )
            return new Particle2DGoldenDifference( step, nameof( Particle2DSnapshotField.StepIndex ), want.StepIndex.ToString(), got.StepIndex.ToString(), tolerance );
        if ( got.AliveCount != want.AliveCount )
            return new Particle2DGoldenDifference( step, nameof( Particle2DSnapshotField.AliveCount ), want.AliveCount.ToString(), got.AliveCount.ToString(), tolerance );
        if ( !Within( got.MeanPosition, want.MeanPosition, tolerance ) )
            return new Particle2DGoldenDifference( step, nameof( Particle2DSnapshotField.MeanPosition ), Format( want.MeanPosition ), Format( got.MeanPosition ), tolerance );
        if ( MathF.Abs( got.MeanSpeed - want.MeanSpeed ) > tolerance )
            return new Particle2DGoldenDifference( step, nameof( Particle2DSnapshotField.MeanSpeed ), want.MeanSpeed.ToString(), got.MeanSpeed.ToString(), tolerance );
        if ( !Within( got.BoundsMin, want.BoundsMin, tolerance ) )
            return new Particle2DGoldenDifference( step, nameof( Particle2DSnapshotField.BoundsMin ), Format( want.BoundsMin ), Format( got.BoundsMin ), tolerance );
        if ( !Within( got.BoundsMax, want.BoundsMax, tolerance ) )
            return new Particle2DGoldenDifference( step, nameof( Particle2DSnapshotField.BoundsMax ), Format( want.BoundsMax ), Format( got.BoundsMax ), tolerance );
        if ( got.Positions.Length != want.Positions.Length )
            return new Particle2DGoldenDifference( step, "PositionCount", want.Positions.Length.ToString(), got.Positions.Length.ToString(), tolerance );
        for ( int i = 0; i < got.Positions.Length; i++ )
            if ( !Within( got.Positions[i], want.Positions[i], tolerance ) )
                return new Particle2DGoldenDifference( step, $"Position[{i}]", Format( want.Positions[i] ), Format( got.Positions[i] ), tolerance );
        return null;
    }

    static string? FirstExactDifference( IReadOnlyList<Particle2DSnapshot> first, IReadOnlyList<Particle2DSnapshot> second )
    {
        if ( first.Count != second.Count ) return $"snapshot count differs: {first.Count} vs {second.Count}";
        for ( int i = 0; i < first.Count; i++ )
        {
            var a = first[i]; var b = second[i];
            if ( a.StepIndex != b.StepIndex ) return $"step index at snapshot {i} differs: {a.StepIndex} vs {b.StepIndex}";
            if ( a.AliveCount != b.AliveCount ) return $"step {a.StepIndex} AliveCount differs: {a.AliveCount} vs {b.AliveCount}";
            if ( a.MeanPosition != b.MeanPosition ) return $"step {a.StepIndex} MeanPosition differs exactly: {Format( a.MeanPosition )} vs {Format( b.MeanPosition )}";
            if ( a.MeanSpeed != b.MeanSpeed ) return $"step {a.StepIndex} MeanSpeed differs exactly";
            if ( a.BoundsMin != b.BoundsMin || a.BoundsMax != b.BoundsMax ) return $"step {a.StepIndex} bounds differ exactly";
            if ( a.Positions.Length != b.Positions.Length ) return $"step {a.StepIndex} position count differs: {a.Positions.Length} vs {b.Positions.Length}";
            for ( int j = 0; j < a.Positions.Length; j++ )
                if ( a.Positions[j] != b.Positions[j] ) return $"step {a.StepIndex} Position[{j}] differs exactly: {Format( a.Positions[j] )} vs {Format( b.Positions[j] )}";
        }
        return null;
    }
}
