// ParticleTestHarnessTests.cs — tests for the deterministic scenario runner
// (ParticleTestHarness.cs). Group "particle-harness". No live cabinet, no
// files, no network: every scenario here is pure sim + assertions.

using System.Numerics;
using Marionette.Desktop;

internal static class ParticleTestHarnessTests
{
    public static void Register( TestRegistry registry, TestSettings settings )
    {
        void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
        void Throws( Action body, string what )
        {
            try { body(); }
            catch ( ArgumentException ) { return; }
            catch ( InvalidOperationException ) { return; }
            throw new Exception( $"{what}: expected a loud exception, none thrown" );
        }
        bool Finite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );
        bool Finite2( Vector2 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y );

        ParticleScenario Fountain( int steps ) => new()
        {
            Name = "fountain",
            Seed = 1234,
            Steps = steps,
            Setup = sys =>
            {
                sys.Settings.Gravity = new Vector3( 0, 0, -100 );
                sys.AddEmitter( new ParticleEmitter
                {
                    Position = Vector3.Zero,
                    Direction = new Vector3( 0, 0, 1 ),
                    Rate = 60,
                    Speed = 50,
                    SpeedJitter = 0.2f,
                    SpreadDegrees = 30,
                    LifetimeMin = 1f,
                    LifetimeMax = 2f,
                    SizeMin = 1f,
                    SizeMax = 2f,
                } );
            },
        };

        Particle2DScenario Fountain2D( int steps ) => new()
        {
            Name = "fountain-2d",
            Seed = 4321,
            Steps = steps,
            Setup = sys =>
            {
                sys.AddEmitter( new Particle2DEmitter
                {
                    Position = new Vector2( 100, 100 ),
                    Direction = new Vector2( 0, -1 ),
                    Rate = 60,
                    Speed = 100,
                    SpeedJitter = 0.2f,
                    SpreadDegrees = 30,
                    LifetimeMin = 1f,
                    LifetimeMax = 2f,
                    SizeMin = 2f,
                    SizeMax = 4f,
                } );
            },
        };

        registry.Add( "particle-harness", "empty system yields zero alive and no NaN at every step", () =>
        {
            var scenario = new ParticleScenario { Name = "empty", Seed = 7, Steps = 5, Setup = _ => { } };
            var result = ParticleTestHarness.Run( scenario );
            Check( result.Snapshots.Count == 6, $"Steps=5 must yield 6 snapshots, got {result.Snapshots.Count}" );
            foreach ( var snap in result.Snapshots )
            {
                Check( snap.AliveCount == 0, $"step {snap.StepIndex}: expected AliveCount 0, got {snap.AliveCount}" );
                Check( snap.MeanPosition == Vector3.Zero, $"step {snap.StepIndex}: expected MeanPosition Zero, got {snap.MeanPosition}" );
                Check( snap.MeanSpeed == 0 && snap.MeanSize == 0, $"step {snap.StepIndex}: expected zero speed/size" );
                Check( snap.BoundsMin == Vector3.Zero && snap.BoundsMax == Vector3.Zero, $"step {snap.StepIndex}: expected Zero bounds" );
                Check( Finite( snap.MeanPosition ) && float.IsFinite( snap.MeanSpeed ) && float.IsFinite( snap.MeanSize )
                    && Finite( snap.BoundsMin ) && Finite( snap.BoundsMax ), $"step {snap.StepIndex}: NaN in empty snapshot" );
                Check( snap.Positions.Length == 0, $"step {snap.StepIndex}: expected no positions" );
            }
            Console.WriteLine( $"empty: {result.Snapshots.Count} snapshots, all AliveCount 0, MeanPosition Vector3.Zero, no NaN" );
        } );

        registry.Add( "particle-harness", "seeded fountain run twice is bit-identical", () =>
        {
            var check = ParticleTestHarness.RunTwice( Fountain( 60 ) );
            Check( check.Identical, $"double run differed: {check.FirstDifference}" );
            var last = check.First[^1];
            Check( last.AliveCount > 0, $"fountain final step should be alive, got {last.AliveCount}" );
            Check( Finite( last.MeanPosition ), $"fountain final MeanPosition not finite: {last.MeanPosition}" );
            for ( int i = 0; i < check.First.Count; i++ )
                for ( int j = 0; j < check.First[i].Positions.Length; j++ )
                    Check( check.First[i].Positions[j] == check.Second[i].Positions[j],
                        $"position {j} at step {check.First[i].StepIndex} differs between runs" );
            Console.WriteLine( $"fountain twice: {check.First.Count} snapshots, final AliveCount {last.AliveCount}, final MeanPosition {last.MeanPosition}, bit-identical" );
        } );

        registry.Add( "particle-harness", "perturbed golden reports exactly the perturbed step and position field", () =>
        {
            var snapshots = ParticleTestHarness.Run( Fountain( 30 ) ).Snapshots;
            var golden = snapshots.ToList();
            var target = golden[10];
            Check( target.Positions.Length > 0, "fountain step 10 should have live particles to perturb" );
            var perturbedPositions = target.Positions.ToArray();
            perturbedPositions[0] += new Vector3( 5, 0, 0 );
            golden[10] = target with { Positions = perturbedPositions };
            var comparison = ParticleTestHarness.Compare( snapshots, golden, 0.001f );
            Check( !comparison.Matches, "perturbed golden must not match" );
            Check( comparison.FirstDifference is not null && comparison.FirstDifference.StepIndex == 10
                && comparison.FirstDifference.Field == "Position[0]",
                $"expected first difference step 10 field Position[0], got step {comparison.FirstDifference?.StepIndex} field {comparison.FirstDifference?.Field}" );
            var clean = ParticleTestHarness.Compare( snapshots, snapshots, 0.001f );
            Check( clean.Matches, $"unperturbed comparison must match: {clean.Message}" );
            Console.WriteLine( $"perturbed golden: {comparison.Message}" );
        } );

        registry.Add( "particle-harness", "perturbed golden mean reports the mean field", () =>
        {
            var snapshots = ParticleTestHarness.Run( Fountain( 30 ) ).Snapshots;
            var golden = snapshots.ToList();
            golden[20] = golden[20] with { MeanPosition = golden[20].MeanPosition + new Vector3( 0, 0, 9 ) };
            var comparison = ParticleTestHarness.Compare( snapshots, golden, 0.001f );
            Check( !comparison.Matches && comparison.FirstDifference is not null
                && comparison.FirstDifference.StepIndex == 20 && comparison.FirstDifference.Field == "MeanPosition",
                $"expected step 20 MeanPosition, got step {comparison.FirstDifference?.StepIndex} field {comparison.FirstDifference?.Field}" );
            Console.WriteLine( $"perturbed mean: {comparison.Message}" );
        } );

        registry.Add( "particle-harness", "too-tight tolerance fails with expected actual tolerance in the message", () =>
        {
            var scenario = Fountain( 60 );
            scenario.Expectations.Add( ParticleExpectation.ForMeanSpeed( 60, 0, 0.000001f ) );
            var result = ParticleTestHarness.Run( scenario );
            Check( result.Verdicts.Count == 1, $"expected 1 verdict, got {result.Verdicts.Count}" );
            var verdict = result.Verdicts[0];
            Check( !verdict.Passed, $"too-tight MeanSpeed assertion should fail, message: {verdict.Message}" );
            Check( verdict.Message.Contains( "step 60" ) && verdict.Message.Contains( "MeanSpeed" )
                && verdict.Message.Contains( verdict.Actual ) && verdict.Message.Contains( verdict.Expected )
                && verdict.Message.Contains( "1E-06" ),
                $"failing message must carry step/field/expected/actual/tolerance, got: {verdict.Message}" );
            Console.WriteLine( $"too-tight verdict: {verdict.Message}" );
        } );

        registry.Add( "particle-harness", "passing assertion verdict carries full detail", () =>
        {
            var scenario = new ParticleScenario { Name = "empty-expect", Seed = 7, Steps = 5, Setup = _ => { } };
            scenario.Expectations.Add( ParticleExpectation.ForAliveCount( 3, 0 ) );
            var result = ParticleTestHarness.Run( scenario );
            var verdict = result.Verdicts[0];
            Check( verdict.Passed, $"empty AliveCount assertion should pass: {verdict.Message}" );
            Check( verdict.Message.Contains( "step 3" ) && verdict.Message.Contains( "AliveCount" )
                && verdict.Message.Contains( verdict.Actual ) && verdict.Message.Contains( verdict.Expected ),
                $"passing message must carry step/field/expected/actual, got: {verdict.Message}" );
        } );

        registry.Add( "particle-harness", "steps zero yields only the initial snapshot", () =>
        {
            var result = ParticleTestHarness.Run( Fountain( 0 ) );
            Check( result.Snapshots.Count == 1, $"Steps=0 must yield 1 snapshot, got {result.Snapshots.Count}" );
            Check( result.Snapshots[0].StepIndex == 0, $"initial snapshot StepIndex must be 0, got {result.Snapshots[0].StepIndex}" );
            Check( result.Snapshots[0].AliveCount == 0, "no step taken, so no particle has been emitted yet" );
        } );

        registry.Add( "particle-harness", "negative steps null scenario null setup throw loudly", () =>
        {
            Throws( () => ParticleTestHarness.Run( new ParticleScenario { Name = "neg", Seed = 1, Steps = -1, Setup = _ => { } } ), "negative Steps" );
            Throws( () => ParticleTestHarness.Run( null! ), "null scenario" );
            Throws( () => ParticleTestHarness.Run( new ParticleScenario { Name = "nosetup", Seed = 1, Steps = 1 } ), "null setup" );
            Throws( () => ParticleTestHarness.RunTwice( new ParticleScenario { Name = "neg", Seed = 1, Steps = -2, Setup = _ => { } } ), "RunTwice negative Steps" );
        } );

        registry.Add( "particle-harness", "2d empty system yields zero alive and no NaN", () =>
        {
            var scenario = new Particle2DScenario { Name = "empty-2d", Seed = 7, Steps = 5, Setup = _ => { } };
            var result = Particle2DTestHarness.Run( scenario );
            Check( result.Snapshots.Count == 6, $"Steps=5 must yield 6 snapshots, got {result.Snapshots.Count}" );
            foreach ( var snap in result.Snapshots )
            {
                Check( snap.AliveCount == 0 && snap.MeanPosition == Vector2.Zero && snap.MeanSpeed == 0
                    && snap.BoundsMin == Vector2.Zero && snap.BoundsMax == Vector2.Zero && snap.Positions.Length == 0,
                    $"2d step {snap.StepIndex}: empty snapshot wrong" );
                Check( Finite2( snap.MeanPosition ) && float.IsFinite( snap.MeanSpeed ), $"2d step {snap.StepIndex}: NaN in empty snapshot" );
            }
            Console.WriteLine( "empty-2d: 6 snapshots, all AliveCount 0, MeanPosition Vector2.Zero, no NaN" );
        } );

        registry.Add( "particle-harness", "2d seeded fountain run twice is bit-identical", () =>
        {
            var check = Particle2DTestHarness.RunTwice( Fountain2D( 60 ) );
            Check( check.Identical, $"2d double run differed: {check.FirstDifference}" );
            var last = check.First[^1];
            Check( last.AliveCount > 0, $"2d fountain final step should be alive, got {last.AliveCount}" );
            Console.WriteLine( $"fountain-2d twice: {check.First.Count} snapshots, final AliveCount {last.AliveCount}, final MeanPosition {last.MeanPosition}, bit-identical" );
        } );

        registry.Add( "particle-harness", "2d perturbed golden reports exactly the perturbed step and position field", () =>
        {
            var snapshots = Particle2DTestHarness.Run( Fountain2D( 30 ) ).Snapshots;
            var golden = snapshots.ToList();
            var target = golden[10];
            Check( target.Positions.Length > 0, "2d fountain step 10 should have live particles to perturb" );
            var perturbedPositions = target.Positions.ToArray();
            perturbedPositions[0] += new Vector2( 5, 0 );
            golden[10] = target with { Positions = perturbedPositions };
            var comparison = Particle2DTestHarness.Compare( snapshots, golden, 0.001f );
            Check( !comparison.Matches && comparison.FirstDifference is not null
                && comparison.FirstDifference.StepIndex == 10 && comparison.FirstDifference.Field == "Position[0]",
                $"expected 2d step 10 Position[0], got step {comparison.FirstDifference?.StepIndex} field {comparison.FirstDifference?.Field}" );
            Console.WriteLine( $"perturbed golden 2d: {comparison.Message}" );
        } );
    }
}
